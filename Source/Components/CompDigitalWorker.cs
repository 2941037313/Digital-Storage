using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.AI;
using DigitalStorage.Effects;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// <b>代理建筑</b>（数字工人）：由建筑自己完成 挖掘/建造/清洁/种植(伐木) 之一，殖民者不跑腿。
    ///
    /// <para><b>路线</b>：不 spawn 任何 Pawn（"假 pawn 当资质载体"），而是照抄原版 driver 的
    /// "对目标干活"那一段，脱离 job 直接调用产出函数。三条路线为何这样选、以及为什么
    /// "完全不要 pawn"不成立（<c>Plant.PlantCollected(by,…)</c> 会解引用 <c>by</c>），
    /// 见 obsidian：<c>代码Wiki/rimworld/代理工人-脱离job直调产出.md</c>。</para>
    ///
    /// <para><b>并行</b>：普通代理建筑 <c>maxParallelPerWorkType = 1</c>（一次一件活）；
    /// 超凡代理 = 50 且管 4 个 workTypes ⇒ 总计最多 200 件同时进行。
    /// 因此整个 tick <b>只借还一次地图</b>（<see cref="DigitalWorkerScope"/>），而不是逐任务借还。</para>
    ///
    /// <para><b>四件必须记住的事</b>（都是踩过的）：
    /// ① 产出函数**没有** <c>Destroyed</c> 守卫 ⇒ 每次动手前后都要复查目标；
    /// ② 找活与干活都必须包在 <see cref="DigitalWorkerScope"/> 里 ——
    ///    <c>ReservationManager.CanReserve</c> 要求 <c>claimant.Spawned &amp;&amp; claimant.Map == map</c>
    ///    （<c>ReservationManager.cs:164-167</c>），而假 pawn 从不 spawn；
    /// ③ 借还 <c>mapIndexOrState</c> 必须 <c>try/finally</c>，漏一次 pawn 就永久挂在错误地图上；
    /// ④ 假 pawn 必须补 <c>PawnComponentsUtility.AddComponentsForSpawn</c>，否则任何读
    ///    <c>pawn.DrawPos</c> 的原版代码都会在 <c>PawnTweener.TweenedPosRoot</c> 里 NRE
    ///    （它直接解引用 <c>pawn.pather</c>）。</para>
    /// </summary>
    public class CompDigitalWorker : ThingComp
    {
        /// <summary>一件正在干的活 + 它自己的表现件（手 / 黄色读条）。</summary>
        private class ActiveWork
        {
            public DigitalTask task;
            public Map map;
            public Mote_DS_WorkHand hand;
            public Effecter bar;
            public Effecter hitFx;
            public int lastStrikes;
        }

        /// <summary>资质载体。**不进存档**（可重建的派生对象），读档后按需重建。</summary>
        private Pawn worker;

        private readonly List<ActiveWork> works = new List<ActiveWork>();
        private int nextScanTick;
        private bool enabled = true;

        /// <summary>候选集遍历上限（防某个 lister 把一帧吃光）。**每次 giver 调用**的上限。</summary>
        private const int MaxIterate = 4000;

        /// <summary>
        /// <b>一次扫描（跨所有 add、所有 giver）的总枚举额度</b>。
        ///
        /// <para>⚠️ 2026 实测踩坑：原先只有 <see cref="MaxIterate"/>（每次 add 4000），
        /// 而一次扫描最多 <see cref="AddsPerScan"/> 个 add ⇒ 最坏 <b>60 × 4000 = 24 万次枚举</b>，
        /// 实测 33ms/帧（240000 × ~137ns），是当时卡顿的第一大头（`DS-scan` 占 CompTick 的 65%）。
        /// 用户实测场景：4 个超凡代理 + 全图 2.5 万个标记，每个候选还要过一遍
        /// <c>HasJobOnThing</c>，乘积爆炸。</para>
        ///
        /// <para>额度取 6000 ≈ 0.8ms/次扫描：稳态下第一个可认领的候选通常在枚举前几百个内
        /// （已认领的会被快速跳过、完成的会从候选集里消失），所以 60 个 add 照样能填满。</para>
        /// </summary>
        private const int MaxEnumerationsPerScan = 6000;

        private int scanBudget;

        /// <summary>扫描序号：用来判断候选集缓存是不是本次扫描已物化过。</summary>
        private int scanSerial;

        /// <summary>
        /// <b>候选集缓存（一次扫描内每个 giver 只物化一次）</b>。
        ///
        /// <para>⚠️ 2026 实测踩坑：<c>CandidateSet</c> 原本是**每次 add 都调**的，
        /// 而清洁适配器的候选集是"扫全图 <c>listerThings.AllThings</c> 过滤出污物"
        /// （<c>ThingRequestGroup.Filth</c> 在 <c>ThingListGroupHelper</c> 里没有分支，拿不到列表，
        /// 只能这么写）。一次扫描最多 <see cref="AddsPerScan"/>=60 个 add ⇒
        /// **60 次全图扫描**（用户场景：4 个超凡代理 + 全图 2 万岩屑 + 几十万 item）
        /// ⇒ 每次扫描 15ms+，还每帧分配几十 MB（实测 分配 111MB/窗口）。
        /// 物化一次之后，枚举开销降到 1/60。</para>
        ///
        /// <para>用 <c>Clear()</c> 复用 List 容量而不是丢弃重建，避免 GC 抖动。
        /// 键是 <c>WorkGiver_Scanner</c>（<c>def.Worker</c> 单例，身份稳定）。</para>
        /// </summary>
        private readonly Dictionary<WorkGiver, List<Thing>> candidateCache =
            new Dictionary<WorkGiver, List<Thing>>();

        private readonly Dictionary<WorkGiver, int> candidateSerial = new Dictionary<WorkGiver, int>();

        /// <summary>一次扫描最多新增几件活（实测不卡，给大一点让并行舰队快速填满）。</summary>
        private const int AddsPerScan = 60;

        /// <summary>没填满时用更短的扫描间隔，避免"并行数爬升很慢"。</summary>
        private const int FillScanIntervalTicks = 15;

        public CompProperties_DigitalWorker Props
        {
            get { return (CompProperties_DigitalWorker)props; }
        }

        /// <summary>同时进行几件活（UI/调试用）。</summary>
        public int ActiveCount
        {
            get { return works.Count; }
        }

        public DigitalTask CurrentTask
        {
            get { return works.Count > 0 ? works[0].task : null; }
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                enabled = value;
                if (!enabled) ReleaseAll();
            }
        }

        /// <summary>有电才能干活 —— 沿用核心"取出的唯一门就是电"的既有口径。</summary>
        public bool Powered
        {
            get
            {
                CompPowerTrader p = parent.GetComp<CompPowerTrader>();
                return p == null || p.PowerOn;
            }
        }

        public bool CanWork
        {
            get { return parent.Spawned && !parent.Destroyed && Powered && enabled; }
        }

        public Pawn Worker
        {
            get
            {
                EnsureWorker();
                return worker;
            }
        }

        /// <summary>只在已经建好时返回，**不会触发创建** —— 给热路径（如 <c>CanReserve</c> 补丁）用。</summary>
        public Pawn WorkerIfCreated
        {
            get { return worker; }
        }

        // ===================================================================
        // 主循环
        // ===================================================================

        public override void CompTick()
        {
            // 断电 / 被拆 / 关掉 ⇒ 立刻全放手（不占着目标）
            if (!CanWork)
            {
                ReleaseAll();
                return;
            }

            Map map = parent.Map;
            if (map == null) return;

            Pawn w = Worker;
            if (w == null) return;

            // 分帧推进：把"要 O(全图) 才能算出来的候选集"每 tick 推一小片
            // （适配器的默认实现是空操作，目前只有清洁会真的动）。
            // 位置刻意放在这里 —— 与扫描频率无关，尖峰才能真正变成常量。
            SliceCandidates(map, w);

            long heap0 = Performance.DevDrawProfiler.HeapStamp();
            DigitalWorkerScope.Enter(w, map, parent.PositionHeld);
            // 本次 tick 期间的掉落直塞目标（没有可用核心时 Active=false，整条链路零开销）
            Core.DigitalDropRedirect.Begin(map, parent.PositionHeld);
            try
            {
                // 1) 丢掉失效的（目标没了 / 设计取消 / 被别人订走）
                long p = Performance.DevDrawProfiler.Stamp();
                for (int i = works.Count - 1; i >= 0; i--)
                {
                    if (!works[i].task.StillValid(w, map)) ReleaseWorkAt(i);
                }
                Performance.DevDrawProfiler.Mark("DS-valid", p);

                // 2) 补到并行上限（按 workTypes 各自的配额；限流见 AddsPerScan）
                p = Performance.DevDrawProfiler.Stamp();
                int now = Find.TickManager.TicksGame;
                if (now >= nextScanTick)
                {
                    Performance.DevDrawProfiler.Bump("扫描", 1);
                    int interval = Math.Max(1, Props.scanIntervalTicks);
                    int totalCap = TotalParallelCap();
                    if (totalCap > 0 && works.Count < totalCap)
                    {
                        interval = Math.Min(interval, FillScanIntervalTicks);   // 没填满 ⇒ 快扫
                    }
                    nextScanTick = now + interval;
                    scanBudget = MaxEnumerationsPerScan;   // 本次扫描的总枚举额度（跨 add / 跨 giver）
                    scanSerial++;                          // 候选集缓存按扫描序号失效
                    for (int added = 0; added < AddsPerScan; added++)
                    {
                        if (scanBudget <= 0) break;
                        if (!TryAddOneWork(map, w)) break;
                    }
                    Performance.DevDrawProfiler.Bump("枚举", MaxEnumerationsPerScan - scanBudget);
                }
                Performance.DevDrawProfiler.Mark("DS-scan", p);

                // 3) 干活（每件活各自累积）
                p = Performance.DevDrawProfiler.Stamp();
                for (int i = 0; i < works.Count; i++)
                {
                    try
                    {
                        works[i].task.Work(w, map, Props.workSpeedMult);
                    }
                    catch (Exception e)
                    {
                        Log.Error("[DigitalStorage] 代理建筑干活出错（丢这一件）：" + parent + " → " + e);
                        ReleaseWorkAt(i);
                        i--;
                    }
                }
                Performance.DevDrawProfiler.Mark("DS-work", p);

                // 4) 收掉干完的
                p = Performance.DevDrawProfiler.Stamp();
                for (int i = works.Count - 1; i >= 0; i--)
                {
                    if (works[i].task.Finished)
                    {
                        Performance.DevDrawProfiler.Bump("完成", 1);
                        ReleaseWorkAt(i);
                    }
                }
                Performance.DevDrawProfiler.Mark("DS-finish", p);
            }
            finally
            {
                Core.DigitalDropRedirect.End();
                DigitalWorkerScope.Exit(w);
            }

            long pv = Performance.DevDrawProfiler.Stamp();
            UpdateVisuals(map);
            Performance.DevDrawProfiler.Mark("DS-visual", pv);
            Performance.DevDrawProfiler.MarkAlloc("DS-allocMB", heap0);
        }

        /// <summary>再找一件活（受"每类并行配额"限制）。找到并认领返回 true。</summary>
        private bool TryAddOneWork(Map map, Pawn w)
        {
            List<WorkTypeDef> types = Props.workTypes;
            if (types == null) return false;

            if (Props.maxParallelTotal > 0 && works.Count >= Props.maxParallelTotal) return false;

            int cap = Math.Max(1, Props.maxParallelPerWorkType);
            for (int i = 0; i < types.Count; i++)
            {
                WorkTypeDef wt = types[i];
                if (wt == null) continue;
                if (scanBudget <= 0) return false;          // 枚举额度用完 ⇒ 本次扫描到此为止
                if (CountWorksOf(wt) >= cap) continue;
                if (TryScanWorkType(map, w, wt)) return true;
            }
            return false;
        }

        private int CountWorksOf(WorkTypeDef wt)
        {
            int n = 0;
            for (int i = 0; i < works.Count; i++)
            {
                if (works[i].task.workType == wt) n++;
            }
            return n;
        }

        /// <summary>把本建筑关心的每个 giver 的候选集刷新推进一步（分帧；多数适配器是空操作）。</summary>
        private void SliceCandidates(Map map, Pawn w)
        {
            List<WorkTypeDef> types = Props.workTypes;
            if (types == null) return;
            for (int i = 0; i < types.Count; i++)
            {
                WorkTypeDef wt = types[i];
                if (wt == null) continue;
                List<WorkGiver> givers = DigitalTaskRegistry.FindGivers(wt);
                if (givers == null) continue;
                for (int g = 0; g < givers.Count; g++)
                {
                    WorkGiver_Scanner scanner = givers[g] as WorkGiver_Scanner;
                    if (scanner == null) continue;
                    DigitalTaskAdapter adapter = DigitalTaskRegistry.AdapterFor(givers[g]);
                    if (adapter == null) continue;
                    adapter.SliceTick(map, w, scanner);
                }
            }
        }

        /// <summary>总并行上限（含 maxParallelTotal 与"每类配额 × 类别数"取小）。</summary>
        private int TotalParallelCap()
        {
            List<WorkTypeDef> types = Props.workTypes;
            int cap = Math.Max(1, Props.maxParallelPerWorkType) * (types != null && types.Count > 0 ? types.Count : 1);
            if (Props.maxParallelTotal > 0 && Props.maxParallelTotal < cap) cap = Props.maxParallelTotal;
            return cap;
        }

        private bool TryScanWorkType(Map map, Pawn w, WorkTypeDef workType)
        {
            // ⚠️ 一个工作类型可能有**多个**有适配器的 WorkGiver（Construction = 建造 + 拆除 + 维修…），
            // 必须全试 —— 早先只取第一个，表现就是"建造代理永远不拆建筑"。
            List<WorkGiver> givers = DigitalTaskRegistry.FindGivers(workType);
            if (givers == null) return false;

            for (int g = 0; g < givers.Count; g++)
            {
                if (scanBudget <= 0) return false;          // 枚举额度用完 ⇒ 换下一个工作类型也没意义
                if (TryScanGiver(map, w, workType, givers[g])) return true;
            }
            return false;
        }

        private bool TryScanGiver(Map map, Pawn w, WorkTypeDef workType, WorkGiver giver)
        {
            WorkGiver_Scanner scanner = giver as WorkGiver_Scanner;
            if (scanner == null) return false;

            DigitalTaskAdapter adapter = DigitalTaskRegistry.AdapterFor(giver);
            if (adapter == null) return false;

            if (!DigitalTaskRegistry.PawnCanUse(giver, w, adapter)) return false;

            List<Thing> set = GetCandidates(map, w, scanner, adapter);
            if (set == null || set.Count == 0) return false;

            int seen = 0;
            foreach (Thing t in set)
            {
                if (--scanBudget < 0) break;      // 本次扫描的枚举额度（跨 add / 跨 giver）
                if (++seen > MaxIterate) break;
                if (t == null || t.Destroyed || !t.Spawned) continue;

                // 不抢别人（含原版殖民者）已认领的目标；
                // ⚠️ 也必须跳过**本建筑自己**已认领的 —— 否则同一次扫描会把同一个目标
                // 反复认领成多件活（并行 200 时 = 200 件活全砸在同一块矿上，
                // 表现成"只有一个目标"，而且瞬间挖穿）
                if (t.IsForbidden(w)) continue;
                if (DigitalWorkerClaims.OwnerOf(map, t) != null) continue;
                if (!adapter.CanTarget(w, t)) continue;

                if (adapter.TrustWorkGiver)
                {
                    bool hasJob;
                    try
                    {
                        Performance.DevDrawProfiler.Bump("试岗", 1);   // HasJobOnThing 调用次数
                        hasJob = scanner.HasJobOnThing(w, t, false);
                    }
                    catch (Exception e)
                    {
                        Log.ErrorOnce("[DigitalStorage] 问 WorkGiver 时出错：" + giver.def.defName + " → " + e,
                            giver.def.shortHash * 31 + 9923);
                        hasJob = false;
                    }
                    if (!hasJob) continue;
                }

                DigitalTask candidate = adapter.MakeTask(t, this);
                candidate.workType = workType;
                if (!candidate.StillValid(w, map)) continue;

                DigitalWorkerClaims.TryClaim(map, t, this);
                works.Add(new ActiveWork { task = candidate, map = map });
                return true;
            }
            return false;
        }

        /// <summary>
        /// 取候选集（**一次扫描内每个 giver 只物化一次**，见 <see cref="candidateCache"/>）。
        /// 物化拷贝到这里而不是直接枚举 <c>CandidateSet</c>，是因为它会**每次调用都全图扫一遍**
        /// （清洁适配器）；而一次扫描要调它最多 60 次。
        /// </summary>
        private List<Thing> GetCandidates(Map map, Pawn w, WorkGiver_Scanner scanner, DigitalTaskAdapter adapter)
        {
            List<Thing> list;
            if (!candidateCache.TryGetValue(scanner, out list))
            {
                list = new List<Thing>();
                candidateCache[scanner] = list;
                candidateSerial[scanner] = int.MinValue;
            }
            if (candidateSerial[scanner] == scanSerial) return list;

            list.Clear();
            long t0 = Performance.DevDrawProfiler.Stamp();
            IEnumerable<Thing> set = adapter.CandidateSet(map, w, scanner);
            if (set != null)
            {
                foreach (Thing t in set)
                {
                    if (t != null) list.Add(t);
                }
            }
            Performance.DevDrawProfiler.Mark("ScanSet", t0);
            Performance.DevDrawProfiler.Bump("物化", 1);
            candidateSerial[scanner] = scanSerial;
            return list;
        }

        /// <summary>放手一件活（清它那份认领 + 收掉它的表现件）。</summary>
        private void ReleaseWorkAt(int i)
        {
            ActiveWork aw = works[i];
            works.RemoveAt(i);
            DigitalWorkerClaims.Release(aw.map, aw.task.target, this);
            aw.task.Cleanup();
            CleanupVisual(aw);
        }

        /// <summary>全部放手（断电/拆除/关闭/出异常）。</summary>
        public void ReleaseAll()
        {
            for (int i = works.Count - 1; i >= 0; i--) ReleaseWorkAt(i);
            DigitalWorkerClaims.ReleaseAll(this);   // 兜底：万一有漏网的条目
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            ReleaseAll();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref enabled, "enabled", true);
            // 刻意不 Scribe worker / works：工人与进行中的活都是可重建的派生状态，
            // 读档后重新找活即可（比存一份可能对不上的认领表更安全）。
        }

        /// <summary>
        /// 检查面板状态行。
        /// TODO(4.0 收尾)：改成 Keyed 翻译（现在为了实测方便先写死中文）。
        /// </summary>
        public override string CompInspectStringExtra()
        {
            if (!parent.Spawned) return null;
            if (!Powered) return "代理建筑：断电";
            if (!enabled) return "代理建筑：已关闭";

            int cap = TotalParallelCap();

            if (works.Count == 0)
            {
                if (Find.TickManager.TicksGame < nextScanTick) return "代理建筑：待命";
                return "代理建筑：待命（没找到目标）";
            }

            DigitalTask first = works[0].task;
            return "代理建筑：" + first.Label + " · " + first.TargetLabel
                + "（速度 " + Props.workSpeedMult.ToString("0.0") + "×，资质 " + Props.skillLevel
                + "，并行 " + works.Count + "/" + cap + "）";
        }

        // ===================================================================
        // 表现：目标上的那只手 + 目标底下的黄色读条（都只画前 N 件，见 maxVisualTasks）
        // ===================================================================

        private void UpdateVisuals(Map map)
        {
            int cap = Math.Max(0, Props.maxVisualTasks);
            for (int i = 0; i < works.Count; i++)
            {
                ActiveWork aw = works[i];
                Thing t = aw.task.target;

                if (i >= cap || t == null || t.Destroyed || !t.Spawned)
                {
                    CleanupVisual(aw);
                    continue;
                }

                // ---- 手 ----
                if (aw.hand == null || aw.hand.Destroyed || !aw.hand.Spawned)
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(aw.task.HandMoteDefName);
                    if (def != null)
                    {
                        Mote_DS_WorkHand m = ThingMaker.MakeThing(def) as Mote_DS_WorkHand;
                        if (m != null)
                        {
                            GenSpawn.Spawn(m, t.Position, map);
                            aw.hand = m;
                        }
                    }
                }
                if (aw.hand != null && !aw.hand.Destroyed)
                {
                    Vector3 pos = t.DrawPos;
                    pos.y = 0f;        // y 由 Mote.DrawMote 按 altitudeLayer 每帧重设
                    pos.z += 0.15f;    // 略微朝镜头，压在目标正面
                    aw.hand.exactPosition = pos;
                    aw.hand.Maintain();   // 不再 Maintain 时它 1 秒后自愈消失
                    if (aw.task.StrikeCount != aw.lastStrikes)
                    {
                        aw.lastStrikes = aw.task.StrikeCount;
                        aw.hand.Strike();

                        // 命中特效也走同一套"只画前 N 件"的上限 —— 否则并行 200 时
                        // 会出现"矿上有特效、但那只手不在"的错位。
                        // Trigger 的两个目标都用目标物本身：sprayer 不会去读假 pawn 的 DrawPos。
                        if (aw.task.HitEffecterDef != null)
                        {
                            if (aw.hitFx == null) aw.hitFx = aw.task.HitEffecterDef.Spawn();
                            aw.hitFx.Trigger(t, t);
                        }
                    }
                }

                // ---- 黄色读条（原版 EffecterDefOf.ProgressBar + MoteProgressBar）----
                float p = aw.task.Progress01;
                if (p < 0f)
                {
                    if (aw.bar != null)
                    {
                        aw.bar.Cleanup();
                        aw.bar = null;
                    }
                    continue;
                }
                if (aw.bar == null) aw.bar = EffecterDefOf.ProgressBar.Spawn();
                aw.bar.EffectTick(new TargetInfo(t), TargetInfo.Invalid);

                MoteProgressBar mote = (aw.bar.children.Count > 0)
                    ? (aw.bar.children[0] as SubEffecter_ProgressBar)?.mote
                    : null;
                if (mote != null)
                {
                    mote.progress = Mathf.Clamp01(p);
                    mote.offsetZ = -0.5f;     // 原版 WithProgressBar 的默认位置（贴在目标"底下"）
                    mote.alwaysShow = true;   // 代理可能在远离镜头处干活，别只在最近缩放才画
                }
            }
        }

        private void CleanupVisual(ActiveWork aw)
        {
            if (aw.hand != null && !aw.hand.Destroyed) aw.hand.Destroy();
            aw.hand = null;
            if (aw.bar != null)
            {
                aw.bar.Cleanup();
                aw.bar = null;
            }
            if (aw.hitFx != null)
            {
                aw.hitFx.Cleanup();
                aw.hitFx = null;
            }
            aw.lastStrikes = 0;
        }

        // ===================================================================
        // 资质载体（假 pawn）
        // ===================================================================

        /// <summary>
        /// 造资质载体。<b>生成逻辑已抽到 <see cref="DigitalWorkerFactory"/></b>（制作代理的
        /// <c>CompBillAutomation</c> 要用同一套 —— 组件补齐/不进注册表/清特质/固定资质
        /// 四条坑，两处各写一份必然漂移）。
        /// </summary>
        private void EnsureWorker()
        {
            if (worker != null && !worker.Destroyed) return;
            string nick = (Props.workTypes != null && Props.workTypes.Count > 0 ? Props.workTypes[0].defName : "?")
                + Props.skillLevel;
            worker = DigitalWorkerFactory.Create(Props.skillLevel, "数字工人", nick);
        }
    }

    /// <summary>
    /// <b>假 pawn 的"借地图"作用域</b>：临时把 <c>mapIndexOrState</c> 与 <c>Position</c> 设成真的，
    /// 让读 <c>pawn.Map</c> / 要求 <c>claimant.Spawned</c> 的原版代码放行。
    ///
    /// <para>先例：<c>god hand GodAssistantController.cs:288-318</c>（它用在制作产物上）。
    /// 我们比它多一个理由：<b>找活阶段也要借</b> —— <c>WorkGiver.HasJobOnThing</c> 里就有
    /// <c>CanReserve</c>，而它要求 <c>claimant.Spawned &amp;&amp; claimant.Map == map</c>。
    /// 但作用域要**尽量窄**，且必须 <c>try/finally</c>；并行 200 时**整个 tick 只借还一次**
    /// （逐任务借还就是每 tick 几百次反射写）。</para>
    ///
    /// <para>顺序很关键：进入时<b>先设 Position 再翻 mapIndexOrState</b>（<c>Position</c> 的 setter
    /// 在 <c>Spawned</c> 时会去动 region/lister）；退出时<b>先翻回 -1 再清 Position</b>。</para>
    /// </summary>
    internal static class DigitalWorkerScope
    {
        private static readonly FieldInfo MapIndexField = AccessTools.Field(typeof(Thing), "mapIndexOrState");

        public static void Enter(Pawn pawn, Map map, IntVec3 cell)
        {
            if (pawn == null || map == null || MapIndexField == null) return;
            pawn.Position = cell.IsValid ? cell : map.Center;
            MapIndexField.SetValue(pawn, (sbyte)map.Index);
        }

        public static void Exit(Pawn pawn)
        {
            if (pawn == null || MapIndexField == null) return;
            MapIndexField.SetValue(pawn, (sbyte)(-1));
            pawn.Position = IntVec3.Invalid;
        }
    }

    /// <summary>
    /// <b>图级认领表</b>：替代做不到的真预约。
    ///
    /// <para><c>ReservationManager.Reserve(pawn, job: null, …)</c> 会直接
    /// <c>Log.Warning + return false</c> ⇒ 绕开 job 就无法正规预约；用假 Job 预约则会把假 Job
    /// 写进存档、读档找不回来 = 永久占着某块矿。所以只能"自有认领表 + 只读 <c>CanReserve</c> 过滤"。</para>
    ///
    /// <para>认领表**不进存档**：读档后所有代理重新找活即可（比存一份可能对不上的表更安全）。
    /// 一个建筑可以同时认领多件活（超凡代理并行 200）⇒ 放行 <c>owner == me</c>。</para>
    /// </summary>
    internal static class DigitalWorkerClaims
    {
        private static readonly Dictionary<Map, Dictionary<Thing, CompDigitalWorker>> claims =
            new Dictionary<Map, Dictionary<Thing, CompDigitalWorker>>();

        private static Dictionary<Thing, CompDigitalWorker> For(Map map)
        {
            Dictionary<Thing, CompDigitalWorker> d;
            if (!claims.TryGetValue(map, out d))
            {
                d = new Dictionary<Thing, CompDigitalWorker>();
                claims[map] = d;
            }
            return d;
        }

        /// <summary>认领表里有没有任何条目 —— <c>CanReserve</c> 补丁的廉价早退用（热路径）。</summary>
        public static bool AnyClaims
        {
            get { return claims.Count > 0; }
        }

        /// <summary>这个目标被哪个代理建筑认领了（没有/已失效则返回 null，并顺手清理失效项）。</summary>
        public static CompDigitalWorker OwnerOf(Map map, Thing t)
        {
            if (map == null || t == null) return null;
            Dictionary<Thing, CompDigitalWorker> d;
            if (!claims.TryGetValue(map, out d)) return null;
            CompDigitalWorker owner;
            if (!d.TryGetValue(t, out owner)) return null;
            if (owner == null || owner.parent == null || !owner.parent.Spawned)
            {
                d.Remove(t);
                return null;
            }
            return owner;
        }

        public static bool IsClaimedByOther(Map map, Thing t, CompDigitalWorker me)
        {
            CompDigitalWorker owner = OwnerOf(map, t);
            return owner != null && owner != me;
        }

        /// <summary>被**任何**代理认领了（包括自己）—— 找活时必须跳过这种目标。</summary>
        public static bool IsClaimedByAnyone(Map map, Thing t)
        {
            return OwnerOf(map, t) != null;
        }

        public static void TryClaim(Map map, Thing t, CompDigitalWorker me)
        {
            if (map == null || t == null || me == null) return;
            For(map)[t] = me;
        }

        /// <summary>只放掉**这一件**（并行时不能把别的活一起放了）。</summary>
        public static void Release(Map map, Thing t, CompDigitalWorker me)
        {
            if (map == null || t == null) return;
            Dictionary<Thing, CompDigitalWorker> d;
            if (!claims.TryGetValue(map, out d)) return;
            CompDigitalWorker owner;
            if (d.TryGetValue(t, out owner) && owner == me)
            {
                d.Remove(t);
            }
            if (d.Count == 0) claims.Remove(map);
        }

        /// <summary>把这个建筑的所有认领全放掉（断电/拆除/兜底）。</summary>
        public static void ReleaseAll(CompDigitalWorker me)
        {
            if (me == null) return;
            List<Map> empty = null;
            foreach (KeyValuePair<Map, Dictionary<Thing, CompDigitalWorker>> kv in claims)
            {
                List<Thing> mine = null;
                foreach (KeyValuePair<Thing, CompDigitalWorker> p in kv.Value)
                {
                    if (p.Value == me)
                    {
                        if (mine == null) mine = new List<Thing>();
                        mine.Add(p.Key);
                    }
                }
                if (mine != null)
                {
                    for (int i = 0; i < mine.Count; i++) kv.Value.Remove(mine[i]);
                }
                if (kv.Value.Count == 0)
                {
                    if (empty == null) empty = new List<Map>();
                    empty.Add(kv.Key);
                }
            }
            if (empty != null)
            {
                for (int i = 0; i < empty.Count; i++) claims.Remove(empty[i]);
            }
        }
    }
}
