using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Performance
{
    /// <summary>
    /// <b>把"每帧绘制"拆开量出来</b>（只在开发者模式生效，默认零开销）。
    ///
    /// <para>动机：优化到"每帧绘制"这一层之后，剩下的判断不能靠感觉 —— 到底是
    /// ① <c>MapDrawer.DrawMapMesh</c>（地形/静态物网格，原版最大的绘制项）、
    /// ② <c>DynamicDrawManager</c>（动态物）、③ 标记、④ <c>OverlayDrawer</c>、
    /// ⑤ <c>TemporaryThingDrawer</c>（Mote：我们的工作手就在这）、
    /// ⑥ <c>FleckManager</c>（fleck：标记喷的元气泡在这里）、⑦ 选中覆盖层，
    /// 必须各自有毫秒数才知道下一个该动谁。</para>
    ///
    /// <para>这七个方法正好是 <c>Map.MapUpdate</c> 里 <c>if (drawingMap &amp;&amp; Find.CurrentMap == this)</c>
    /// 块内的绘制序列（<c>Map.cs:1176-1183</c>）＋ <c>SelectionDrawer.DrawSelectionOverlays</c>，
    /// 所以它们的和 ≈ 每帧**托管侧**绘制提交耗时。</para>
    ///
    /// <para><b>怎么看结论</b>：把每 180 帧打印的那一行和 avgFrame 比 ——
    /// 若各项之和接近 avgFrame ⇒ 卡在我们能改的托管代码里；
    /// 若各项之和远小于 avgFrame（例如 2ms vs 20ms）⇒ 压力在**渲染线程/GPU**（fill rate、
    /// draw call、shader），mod 侧只能靠"少画点东西"（减少标记/贴图/实例数）而不能靠改代码。
    /// 这两种情况的处方完全不同，所以先量。</para>
    ///
    /// <para>开关：<c>Prefs.DevMode</c> **且** mod 设置「详细日志」（默认关）。关闭时不读时钟、不累计。</para>
    /// </summary>
    internal static class DevDrawProfiler
    {
        private const int WindowFrames = 180;

        private static string buildStamp;

        /// <summary>
        /// DLL 的最后写入时间 —— 让**每一行探针日志自带版本**。
        /// 踩过：拿到一份缺 <c>DS-valid</c> 的日志，花了半轮才判断出"对方跑的是上一版 DLL"。
        /// </summary>
        private static string BuildStamp()
        {
            if (buildStamp != null) return buildStamp;
            try
            {
                buildStamp = System.IO.File.GetLastWriteTime(typeof(DevDrawProfiler).Assembly.Location)
                    .ToString("MMdd-HHmm", CultureInfo.InvariantCulture);
            }
            catch
            {
                buildStamp = "?";
            }
            return buildStamp;
        }

        /// <summary>固定顺序，保证日志一行稳定好对比。
        /// ⚠️ 嵌套关系（2026 修正）：<c>Ticks</c> 与 <c>MapUpd</c> 是**并列**的兄弟 ——
        /// 1.6 里 <c>Map.MapUpdate</c> 由 <c>Game.Update</c> 每帧调一次（<c>Game.cs:675</c>），
        /// 不在 <c>TickManagerUpdate</c> 里（所以暂停时 <c>刻度=0</c> 而 <c>MapUpd</c> 照跑）。
        /// 六个地图绘制项与 <c>DSWork</c> 分别在 <c>MapUpd</c> / <c>Ticks</c> 内部；
        /// <c>SelDraw</c> 在 <c>MapInterfaceUpdate</c> 里。</summary>
        private static readonly string[] Keys =
        {
            "Ticks", "MapUpd", "DSWork", "HaulSweep",
            "DS-valid", "DS-scan", "DS-work", "DS-finish", "DS-visual", "DS-allocMB", "ScanSet",
            "Drops", "DropsDirect",
            "MapMesh", "DynThings", "Designations", "Overlays", "Motes", "Flecks", "SelDraw"
        };

        private static readonly Dictionary<string, double> frameMs = new Dictionary<string, double>();
        private static readonly Dictionary<string, double> windowMs = new Dictionary<string, double>();
        private static readonly Dictionary<string, double> windowMax = new Dictionary<string, double>();
        private static readonly Dictionary<string, int> counters = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> frameCounts = new Dictionary<string, int>();
        private static readonly Dictionary<string, double> windowCounts = new Dictionary<string, double>();

        private static int frameStamp = -1;
        private static int frames;
        private static double frameTotal;
        private static double windowTotal;
        private static double windowPeak;
        private static double windowDelta;
        private static double windowDeltaPeak;

        // GC / 分配（判断"帧时间去哪了"的另一半：托管堆抖动）
        private static long allocated;
        private static long lastHeap = -1L;
        private static long heapStart;
        private static int gc0Start;
        private static int gc1Start;
        private static int gc2Start;

        /// <summary>
        /// 开关：<c>Prefs.DevMode</c>（选项 → 开发者模式）**并且** mod 设置里的「详细日志」
        /// （<c>DigitalStorageSettings.enableDebugLog</c>，**默认关**）。
        ///
        /// <para>为什么要加后半个开关：探针是开发者模式就开，实测时很容易忘了关 ——
        /// 而它每 ~120 帧就往日志写一行。收进"详细日志"之后，默认状态下日志是干净的，
        /// 要看性能数据时在 mod 设置里勾一下即可（勾选/取消立刻生效，不用重启）。</para>
        /// </summary>
        internal static bool Enabled
        {
            get { return Prefs.DevMode && Settings.DigitalStorageSettings.enableDebugLog; }
        }

        internal static long Now
        {
            get { return Stopwatch.GetTimestamp(); }
        }

        internal static double Ms(long since)
        {
            return (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;
        }

        internal static void Add(string key, double ms)
        {
            if (!Enabled) return;
            CheckFrame();
            double cur;
            frameMs.TryGetValue(key, out cur);
            frameMs[key] = cur + ms;
            frameTotal += ms;
        }

        /// <summary>本帧的量级（标记总数 / 选中数），日志里取最后一帧的值。</summary>
        internal static void Counter(string key, int value)
        {
            if (!Enabled) return;
            CheckFrame();
            counters[key] = value;
        }

        /// <summary>本帧内的最大值（多实例/多 tick 的场景，例如"任务数"）。</summary>
        internal static void CounterMax(string key, int value)
        {
            if (!Enabled) return;
            CheckFrame();
            int cur;
            if (counters.TryGetValue(key, out cur) && cur >= value) return;
            counters[key] = value;
        }

        /// <summary>取一个时间戳（关掉时返回 0，配 <see cref="Mark"/> 用，调用点为"零开销"）。</summary>
        internal static long Stamp()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        /// <summary>结算一段 <see cref="Stamp"/> 起的时间。</summary>
        internal static void Mark(string key, long since)
        {
            if (since == 0L || !Enabled) return;
            Add(key, Ms(since));
        }

        /// <summary>取一个堆大小戳（配 <see cref="MarkAlloc"/> 用）。</summary>
        internal static long HeapStamp()
        {
            return Enabled ? GC.GetTotalMemory(false) : 0L;
        }

        /// <summary>结算一段的"托管堆净增"（≈分配量），累加到同名 key 上（值为 MB）。</summary>
        internal static void MarkAlloc(string key, long heapSince)
        {
            if (heapSince == 0L || !Enabled) return;
            long now = GC.GetTotalMemory(false);
            if (now > heapSince) Add(key, (now - heapSince) / 1048576.0);
        }

        /// <summary>本帧计数（次数型量级：扫描了几次、完成了几件）。日志里给"每帧平均"。</summary>
        internal static void Bump(string key, int delta)
        {
            if (!Enabled) return;
            CheckFrame();
            int cur;
            frameCounts.TryGetValue(key, out cur);
            frameCounts[key] = cur + delta;
        }

        private static void CheckFrame()
        {
            if (frameStamp == Time.frameCount) return;
            if (frameStamp < 0) ResetWindow();
            else FlushFrame();
            frameStamp = Time.frameCount;
            frameMs.Clear();
            frameCounts.Clear();
            frameTotal = 0.0;
        }

        private static void FlushFrame()
        {
            frames++;
            windowTotal += frameTotal;
            if (frameTotal > windowPeak) windowPeak = frameTotal;

            double delta = Time.unscaledDeltaTime * 1000.0;
            windowDelta += delta;
            if (delta > windowDeltaPeak) windowDeltaPeak = delta;

            for (int i = 0; i < Keys.Length; i++)
            {
                string k = Keys[i];
                double v;
                if (!frameMs.TryGetValue(k, out v)) continue;
                double sum;
                windowMs.TryGetValue(k, out sum);
                windowMs[k] = sum + v;
                double max;
                windowMax.TryGetValue(k, out max);
                if (v > max) windowMax[k] = v;
            }

            // 堆净增之和 ≈ 本窗口的托管分配量（不强制 GC，代价可忽略；只在 DevMode 下跑）
            long heap = GC.GetTotalMemory(false);
            if (lastHeap >= 0L && heap > lastHeap) allocated += heap - lastHeap;
            lastHeap = heap;

            foreach (KeyValuePair<string, int> kv in frameCounts)
            {
                double sum;
                windowCounts.TryGetValue(kv.Key, out sum);
                windowCounts[kv.Key] = sum + kv.Value;
            }

            if (frames >= WindowFrames)
            {
                LogWindow();
                ResetWindow();
            }
        }

        private static void ResetWindow()
        {
            frames = 0;
            windowTotal = 0.0;
            windowPeak = 0.0;
            windowDelta = 0.0;
            windowDeltaPeak = 0.0;
            windowMs.Clear();
            windowMax.Clear();
            windowCounts.Clear();
            allocated = 0L;
            lastHeap = GC.GetTotalMemory(false);
            heapStart = lastHeap;
            gc0Start = GC.CollectionCount(0);
            gc1Start = GC.CollectionCount(1);
            gc2Start = GC.CollectionCount(2);
        }

        private static double KeyAvg(string k)
        {
            double s;
            return windowMs.TryGetValue(k, out s) && frames > 0 ? s / frames : 0.0;
        }

        private static double KeyMax(string k)
        {
            double m;
            return windowMax.TryGetValue(k, out m) ? m : 0.0;
        }

        private static void LogWindow()
        {
            double frameAvg = WindowDeltaAvg();
            double ticks = KeyAvg("Ticks");
            double selDraw = KeyAvg("SelDraw");
            double drawInner = KeyAvg("MapMesh") + KeyAvg("DynThings") + KeyAvg("Designations")
                             + KeyAvg("Overlays") + KeyAvg("Motes") + KeyAvg("Flecks");
            double other = frameAvg - ticks - selDraw;
            if (other < 0.0) other = 0.0;
            double tickNonDraw = ticks - drawInner;
            if (tickNonDraw < 0.0) tickNonDraw = 0.0;

            var sb = new System.Text.StringBuilder();
            sb.Append("[DS-Draw ").Append(BuildStamp()).Append("] 帧=").Append(frames);
            sb.Append(" 帧间隔 ").Append(F(frameAvg)).Append("(峰 ").Append(F(windowDeltaPeak)).Append(')');
            sb.Append(" | 刻度 ").Append(F(ticks)).Append("(峰 ").Append(F(KeyMax("Ticks"))).Append(')');
            int ticksPerFrame;
            if (counters.TryGetValue("tick/帧", out ticksPerFrame)) sb.Append("[tick/帧=").Append(ticksPerFrame).Append(']');
            sb.Append(" | 刻度内绘制 ").Append(F(drawInner));
            sb.Append(" 刻度内非绘制 ").Append(F(tickNonDraw));
            sb.Append(" 帧内其他 ").Append(F(other));
            sb.Append(" ||");
            for (int i = 0; i < Keys.Length; i++)
            {
                string k = Keys[i];
                double sum;
                if (!windowMs.TryGetValue(k, out sum) || sum <= 0.0) continue;
                sb.Append(' ').Append(k).Append(' ').Append(F(sum / frames)).Append('/').Append(F(KeyMax(k)));
            }
            sb.Append(" || GC0+").Append(GC.CollectionCount(0) - gc0Start);
            sb.Append(" GC1+").Append(GC.CollectionCount(1) - gc1Start);
            sb.Append(" GC2+").Append(GC.CollectionCount(2) - gc2Start);
            sb.Append(" 分配").Append(F(allocated / 1048576.0)).Append("MB");
            sb.Append(" 堆Δ").Append(F((GC.GetTotalMemory(false) - heapStart) / 1048576.0)).Append("MB");
            int selected;
            if (counters.TryGetValue("选中", out selected)) sb.Append(" 选中=").Append(selected);
            int des;
            if (counters.TryGetValue("标记", out des)) sb.Append(" 标记=").Append(des);
            int tasks;
            if (counters.TryGetValue("任务", out tasks)) sb.Append(" 任务=").Append(tasks);
            // 性能开关状态：一眼看出"原版性能修复"那个勾是否还在（批绘/括号裁剪/气泡节流都挂在它上面）
            sb.Append(" 性能开关=")
              .Append(Settings.DigitalStorageSettings.perfOptimizationsEnabled ? 1 : 0)
              .Append(" 直塞预算=")
              .Append(Settings.DigitalStorageSettings.workerCompletionsPerTick);
            foreach (KeyValuePair<string, double> kv in windowCounts)
            {
                if (frames > 0) sb.Append(' ').Append(kv.Key).Append("/帧=").Append(F(kv.Value / frames));
            }
            Log.Warning(sb.ToString());
        }

        private static double WindowDeltaAvg()
        {
            return frames > 0 ? windowDelta / frames : 0.0;
        }

        private static string F(double v)
        {
            return v.ToString("F2", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>① 地形/静态物网格（原版通常最大的一项）。</summary>
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.DrawMapMesh))]
    internal static class PerfProbe_MapMesh
    {
        private static long t0;

        private static void Prefix()
        {
            t0 = DevDrawProfiler.Enabled ? DevDrawProfiler.Now : 0L;
        }

        private static void Postfix()
        {
            if (t0 == 0L) return;
            DevDrawProfiler.Add("MapMesh", DevDrawProfiler.Ms(t0));
            t0 = 0L;
        }
    }

    /// <summary>② 动态物（会动的 Thing，含我们的代理建筑贴图）。</summary>
    [HarmonyPatch(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings))]
    internal static class PerfProbe_DynThings
    {
        private static long t0;

        private static void Prefix()
        {
            t0 = DevDrawProfiler.Enabled ? DevDrawProfiler.Now : 0L;
        }

        private static void Postfix()
        {
            if (t0 == 0L) return;
            DevDrawProfiler.Add("DynThings", DevDrawProfiler.Ms(t0));
            t0 = 0L;
        }
    }

    /// <summary>③ 标记（我们只改了它的缓存策略，这里看它的真实占比与图上的标记总数）。</summary>
    [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.DrawDesignations))]
    internal static class PerfProbe_Designations
    {
        private static long t0;

        // ⚠️ 必须 Priority.First：本 mod 自己的 BatchDraw 补丁也是这个方法的 prefix，
        // 而**任一 prefix 返回 false 后，排在后面的 prefix 不会再跑**（postfix 仍会跑）。
        // 不加优先级就可能出现"优化生效的帧反而测不到时间"——正好是我们最想量的那种帧。
        [HarmonyPriority(Priority.First)]
        private static void Prefix(DesignationManager __instance)
        {
            if (!DevDrawProfiler.Enabled) return;
            t0 = DevDrawProfiler.Now;
            DevDrawProfiler.Counter("标记", CountAll(__instance));
        }

        private static void Postfix()
        {
            if (t0 == 0L) return;
            DevDrawProfiler.Add("Designations", DevDrawProfiler.Ms(t0));
            t0 = 0L;
        }

        private static int CountAll(DesignationManager mgr)
        {
            if (mgr == null) return 0;
            int n = 0;
            List<DesignationDef> defs = DefDatabase<DesignationDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                List<Designation> list = mgr.designationsByDef[defs[i]];
                if (list != null) n += list.Count;
            }
            return n;
        }
    }

    /// <summary>④ 覆盖层（已选/已规划/各种 overlay）。</summary>
    [HarmonyPatch(typeof(OverlayDrawer), nameof(OverlayDrawer.DrawAllOverlays))]
    internal static class PerfProbe_Overlays
    {
        private static long t0;

        private static void Prefix()
        {
            t0 = DevDrawProfiler.Enabled ? DevDrawProfiler.Now : 0L;
        }

        private static void Postfix()
        {
            if (t0 == 0L) return;
            DevDrawProfiler.Add("Overlays", DevDrawProfiler.Ms(t0));
            t0 = 0L;
        }
    }

    /// <summary>⑤ Mote（我们代理的工作手/进度条走这里）。</summary>
    [HarmonyPatch(typeof(TemporaryThingDrawer), nameof(TemporaryThingDrawer.Draw))]
    internal static class PerfProbe_Motes
    {
        private static long t0;

        private static void Prefix()
        {
            t0 = DevDrawProfiler.Enabled ? DevDrawProfiler.Now : 0L;
        }

        private static void Postfix()
        {
            if (t0 == 0L) return;
            DevDrawProfiler.Add("Motes", DevDrawProfiler.Ms(t0));
            t0 = 0L;
        }
    }

    /// <summary>⑥ Fleck（标记喷的元气泡、命中特效都在这）。</summary>
    [HarmonyPatch(typeof(FleckManager), nameof(FleckManager.FleckManagerDraw))]
    internal static class PerfProbe_Flecks
    {
        private static long t0;

        private static void Prefix()
        {
            t0 = DevDrawProfiler.Enabled ? DevDrawProfiler.Now : 0L;
        }

        private static void Postfix()
        {
            if (t0 == 0L) return;
            DevDrawProfiler.Add("Flecks", DevDrawProfiler.Ms(t0));
            t0 = 0L;
        }
    }

    /// <summary>⑦ 选中括号覆盖层（<c>MapInterfaceUpdate</c> 里每帧调；这里同时记录选中数量）。</summary>
    [HarmonyPatch(typeof(SelectionDrawer), nameof(SelectionDrawer.DrawSelectionOverlays))]
    internal static class PerfProbe_Selection
    {
        private static long t0;

        private static void Prefix()
        {
            if (!DevDrawProfiler.Enabled) return;
            t0 = DevDrawProfiler.Now;
            DevDrawProfiler.Counter("选中", Find.Selector != null ? Find.Selector.NumSelected : 0);
        }

        private static void Postfix()
        {
            if (t0 == 0L) return;
            DevDrawProfiler.Add("SelDraw", DevDrawProfiler.Ms(t0));
            t0 = 0L;
        }
    }

    /// <summary>
    /// ⑧ 整帧刻度：<c>Game.Update</c> 每帧调一次 <c>TickManagerUpdate</c>，内部是
    /// <c>DoSingleTick</c> 循环（含每个 <c>Map.MapUpdate</c> ⇒ 含上面六个绘制项）。
    /// 于是 <b>帧间隔 − 刻度 − SelDraw = 帧内其他</b>（UI/输入/渲染线程/GC 等）。
    /// </summary>
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
    internal static class PerfProbe_Ticks
    {
        private static long t0;

        private static void Prefix()
        {
            t0 = DevDrawProfiler.Enabled ? DevDrawProfiler.Now : 0L;
        }

        private static void Postfix()
        {
            if (t0 == 0L) return;
            DevDrawProfiler.Add("Ticks", DevDrawProfiler.Ms(t0));
            TickManager tm = Find.TickManager;
            if (tm != null) DevDrawProfiler.Counter("tick/帧", tm.TicksThisFrame);
            t0 = 0L;
        }
    }

    /// <summary>⑨ 单张地图的一次更新（每个 tick 每图一次；含绘制块与全部地图逻辑）。</summary>
    [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
    internal static class PerfProbe_MapUpdate
    {
        private static long t0;

        private static void Prefix()
        {
            t0 = DevDrawProfiler.Enabled ? DevDrawProfiler.Now : 0L;
        }

        private static void Postfix()
        {
            if (t0 == 0L) return;
            DevDrawProfiler.Add("MapUpd", DevDrawProfiler.Ms(t0));
            t0 = 0L;
        }
    }

    /// <summary>
    /// ⑩ 我们自己的代理建筑 tick（一个建筑一个 comp，多实例同帧累加）。
    /// 这是"刻度内非绘制"里最该被怀疑的一块：并行 300 时它每 tick 都在跑。
    /// </summary>
    [HarmonyPatch(typeof(CompDigitalWorker), nameof(CompDigitalWorker.CompTick))]
    internal static class PerfProbe_DSWorker
    {
        private static long t0;

        private static void Prefix(CompDigitalWorker __instance)
        {
            if (!DevDrawProfiler.Enabled) return;
            t0 = DevDrawProfiler.Now;
            DevDrawProfiler.CounterMax("任务", __instance.ActiveCount);
        }

        private static void Postfix()
        {
            if (t0 == 0L) return;
            DevDrawProfiler.Add("DSWork", DevDrawProfiler.Ms(t0));
            t0 = 0L;
        }
    }
}
