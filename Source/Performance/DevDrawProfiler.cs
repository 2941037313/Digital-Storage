using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    /// <para>开关：<c>Prefs.DevMode</c>（选项 → 开发者模式）。关闭时不读时钟、不累计。</para>
    /// </summary>
    internal static class DevDrawProfiler
    {
        private const int WindowFrames = 180;

        /// <summary>固定顺序，保证日志一行稳定好对比。</summary>
        private static readonly string[] Keys =
        {
            "MapMesh", "DynThings", "Designations", "Overlays", "Motes", "Flecks", "SelDraw"
        };

        private static readonly Dictionary<string, double> frameMs = new Dictionary<string, double>();
        private static readonly Dictionary<string, double> windowMs = new Dictionary<string, double>();
        private static readonly Dictionary<string, double> windowMax = new Dictionary<string, double>();
        private static readonly Dictionary<string, int> counters = new Dictionary<string, int>();

        private static int frameStamp = -1;
        private static int frames;
        private static double frameTotal;
        private static double windowTotal;
        private static double windowPeak;
        private static double windowDelta;
        private static double windowDeltaPeak;

        internal static bool Enabled
        {
            get { return Prefs.DevMode; }
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

        private static void CheckFrame()
        {
            if (frameStamp == Time.frameCount) return;
            if (frameStamp >= 0) FlushFrame();
            frameStamp = Time.frameCount;
            frameMs.Clear();
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

            if (frames >= WindowFrames) LogWindow();
        }

        private static void LogWindow()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[DS-Draw] 帧=").Append(frames);
            sb.Append(" 帧间隔 ").Append(F(WindowDeltaAvg()));
            sb.Append("ms(峰 ").Append(F(windowDeltaPeak)).Append(')');
            sb.Append(" | 托管绘制合计 ").Append(F(windowTotal / frames));
            sb.Append("ms(峰 ").Append(F(windowPeak)).Append(')');
            for (int i = 0; i < Keys.Length; i++)
            {
                string k = Keys[i];
                double sum;
                if (!windowMs.TryGetValue(k, out sum) || sum <= 0.0) continue;
                double max;
                windowMax.TryGetValue(k, out max);
                sb.Append(" | ").Append(k).Append(' ').Append(F(sum / frames)).Append('/').Append(F(max));
            }
            int selected;
            if (counters.TryGetValue("选中", out selected)) sb.Append(" || 选中=").Append(selected);
            int des;
            if (counters.TryGetValue("标记", out des)) sb.Append(" 标记=").Append(des);
            Log.Warning(sb.ToString());

            frames = 0;
            windowTotal = 0.0;
            windowPeak = 0.0;
            windowDelta = 0.0;
            windowDeltaPeak = 0.0;
            windowMs.Clear();
            windowMax.Clear();
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
}
