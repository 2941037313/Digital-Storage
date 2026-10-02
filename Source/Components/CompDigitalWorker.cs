using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.AI;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// <b>代理建筑</b>（数字工人）：由建筑自己完成 挖掘/建造/清洁/种植 之一，殖民者不跑腿。
    ///
    /// <para><b>路线</b>：不 spawn 任何 Pawn（"假 pawn 当资质载体"），而是照抄原版 driver 的
    /// "对目标干活"那一段，脱离 job 直接调用产出函数。三条路线为何这样选、以及为什么
    /// "完全不要 pawn"不成立（<c>Plant.PlantCollected(by,…)</c> 会解引用 <c>by</c>），
    /// 见 obsidian：<c>代码Wiki/rimworld/代理工人-脱离job直调产出.md</c>。</para>
    ///
    /// <para><b>三处必须记住的纪律</b>（都是 More Organs 踩过的）：
    /// ① 产出函数**没有** <c>Destroyed</c> 守卫 ⇒ 每次动手前后都要复查目标；
    /// ② 找活与干活都必须包在 <see cref="DigitalWorkerScope"/> 里 ——
    ///    <c>ReservationManager.CanReserve</c> 要求 <c>claimant.Spawned &amp;&amp; claimant.Map == map</c>
    ///    （<c>ReservationManager.cs:164-167</c>），而假 pawn 从不 spawn；
    /// ③ 借还 <c>mapIndexOrState</c> 必须 <c>try/finally</c>，漏一次 pawn 就永久挂在错误地图上。</para>
    /// </summary>
    public class CompDigitalWorker : ThingComp
    {
        /// <summary>资质载体。**不进存档**（可重建的派生对象），读档后按需重建。</summary>
        private Pawn worker;

        private DigitalTask task;
        private int nextScanTick;
        private bool enabled = true;

        /// <summary>干活时目标"底下"的黄色进度条（原版同款 effecter，见 <see cref="UpdateProgressBar"/>）。</summary>
        private Effecter progressBar;

        /// <summary>候选集遍历上限（防某个 lister 把一帧吃光）。</summary>
        private const int MaxIterate = 4000;

        public CompProperties_DigitalWorker Props
        {
            get { return (CompProperties_DigitalWorker)props; }
        }

        /// <summary>工人在干活吗（UI/调试用）。</summary>
        public DigitalTask CurrentTask
        {
            get { return task; }
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                enabled = value;
                if (!enabled) Release();
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
            // 断电 / 被拆 / 关掉 ⇒ 立刻放手（不占着目标）
            if (!CanWork)
            {
                Release();
                return;
            }

            Map map = parent.Map;
            if (map == null) return;

            Pawn w = Worker;
            if (w == null) return;

            if (task != null && !task.StillValid(w, map))
            {
                Release();
            }

            if (task == null)
            {
                int now = Find.TickManager.TicksGame;
                if (now < nextScanTick) return;
                nextScanTick = now + Math.Max(1, Props.scanIntervalTicks);
                TryScan(map, w);
                if (task == null) return;
            }

            DigitalWorkerScope.Enter(w, map, task.target.PositionHeld);
            try
            {
                task.Work(w, map, Props.workSpeedMult);
            }
            catch (Exception e)
            {
                Log.Error("[DigitalStorage] 代理建筑干活出错（已放手）：" + parent + " → " + e);
                Release();
                return;
            }
            finally
            {
                DigitalWorkerScope.Exit(w);
            }

            UpdateProgressBar();

            if (task != null && task.Finished) Release();
        }

        /// <summary>
        /// 干活时目标"底下"的黄色进度条。
        ///
        /// <para>原版那个读条不是 UI，而是 <c>EffecterDefOf.ProgressBar</c> 生成的一个 effecter，
        /// 里面挂着一个 <c>MoteProgressBar</c>（填充色 (0.9,0.85,0.2) 就是那个黄）。
        /// 我们没有 JobDriver，所以照 <c>ToilEffects.WithProgressBar</c> 的口径自己挂一个 ——
        /// **一点补丁都不用加**。先例：More Organs <c>LaborHand.cs:338-360</c>。</para>
        /// </summary>
        private void UpdateProgressBar()
        {
            float p = (task == null) ? -1f : task.Progress01;
            if (p < 0f || task.target == null || task.target.Destroyed || !task.target.Spawned)
            {
                CleanupProgressBar();
                return;
            }
            if (progressBar == null)
            {
                progressBar = EffecterDefOf.ProgressBar.Spawn();
            }
            progressBar.EffectTick(new TargetInfo(task.target), TargetInfo.Invalid);

            MoteProgressBar mote = (progressBar.children.Count > 0)
                ? (progressBar.children[0] as SubEffecter_ProgressBar)?.mote
                : null;
            if (mote != null)
            {
                mote.progress = Mathf.Clamp01(p);
                mote.offsetZ = -0.5f;     // 原版 WithProgressBar 的默认位置（贴在目标"底下"）
                mote.alwaysShow = true;   // 代理可能在远离镜头处干活，别只在最近缩放才画
            }
        }

        private void CleanupProgressBar()
        {
            if (progressBar != null)
            {
                progressBar.Cleanup();
                progressBar = null;
            }
        }

        /// <summary>找一件活并认领。整段都在作用域里（<c>HasJobOnThing</c> 会走 <c>CanReserve</c>）。</summary>
        private void TryScan(Map map, Pawn w)
        {
            WorkGiver giver = DigitalTaskRegistry.FindGiver(Props.workType);
            if (giver == null) return;

            WorkGiver_Scanner scanner = giver as WorkGiver_Scanner;
            if (scanner == null) return;

            DigitalTaskAdapter adapter = DigitalTaskRegistry.AdapterFor(giver);
            if (adapter == null) return;

            DigitalWorkerScope.Enter(w, map, parent.PositionHeld);
            try
            {
                if (!DigitalTaskRegistry.PawnCanUse(giver, w)) return;

                IEnumerable<Thing> set = null;
                try
                {
                    set = scanner.PotentialWorkThingsGlobal(w);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[DigitalStorage] 取代理候选集失败：" + giver.def.defName + " → " + e,
                        giver.def.shortHash * 31 + 7717);
                }
                if (set == null)
                {
                    set = map.listerThings.ThingsMatching(scanner.PotentialWorkThingRequest);
                }
                if (set == null) return;

                int seen = 0;
                foreach (Thing t in set)
                {
                    if (++seen > MaxIterate) break;
                    if (t == null || t.Destroyed || !t.Spawned) continue;

                    // 不抢别人（含原版殖民者）已预约的活
                    if (t.IsForbidden(w)) continue;
                    if (DigitalWorkerClaims.IsClaimedByOther(map, t, this)) continue;
                    if (!adapter.CanTarget(w, t)) continue;

                    bool hasJob;
                    try
                    {
                        hasJob = scanner.HasJobOnThing(w, t, false);
                    }
                    catch (Exception e)
                    {
                        Log.ErrorOnce("[DigitalStorage] 问 WorkGiver 时出错：" + giver.def.defName + " → " + e,
                            giver.def.shortHash * 31 + 9923);
                        hasJob = false;
                    }
                    if (!hasJob) continue;

                    DigitalTask candidate = adapter.MakeTask(t, this);
                    if (!candidate.StillValid(w, map)) continue;

                    DigitalWorkerClaims.TryClaim(map, t, this);
                    task = candidate;
                    return;
                }
            }
            finally
            {
                DigitalWorkerScope.Exit(w);
            }
        }

        /// <summary>放手：清认领表 + 丢任务 + 收掉 effecter。断电/拆除/目标失效/干完都走这里。</summary>
        public void Release()
        {
            DigitalWorkerClaims.ReleaseAll(this);
            if (task != null)
            {
                task.Cleanup();
                task = null;
            }
            CleanupProgressBar();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            Release();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref enabled, "enabled", true);
            // 刻意不 Scribe worker / task：工人是可重建的派生对象，任务重建后会重新认领
        }

        /// <summary>
        /// 检查面板状态行。
        /// TODO(4.0 收尾)：改成 Keyed 翻译（现在为了第一波实测方便先写死中文）。
        /// </summary>
        public override string CompInspectStringExtra()
        {
            if (!parent.Spawned) return null;
            if (!Powered) return "代理建筑：断电";
            if (!enabled) return "代理建筑：已关闭";
            if (task == null)
            {
                if (Find.TickManager.TicksGame < nextScanTick) return "代理建筑：待命";
                return "代理建筑：待命（没找到目标）";
            }
            return "代理建筑：" + task.Label + " · " + task.TargetLabel
                + "（速度 " + Props.workSpeedMult.ToString("0.0") + "×，资质 " + Props.skillLevel + "）";
        }

        // ===================================================================
        // 资质载体（假 pawn）
        // ===================================================================

        private void EnsureWorker()
        {
            if (worker != null && !worker.Destroyed) return;
            try
            {
                // 先例：god hand MapComponent_GodAssistant.cs:18-33
                worker = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                worker.Name = new NameTriple("", "数字工人", Props.workType.defName + Props.skillLevel);

                // ① 不进地图注册表：即便作用域期间 Spawned 为 true，RegisterPawn 也会早退
                //    （MapPawns.cs:847 `if (!p.mindState.Active) return;`）
                worker.mindState.Active = false;

                // ② 清掉随机特质 —— WorkTypeIsDisabled 会吃背景/特质，机器不该因抽到
                //    "不能做熟练劳动"而罢工（Notify_DisabledWorkTypesChanged 会清 Pawn 侧缓存）
                if (worker.story != null && worker.story.traits != null && worker.story.traits.allTraits != null)
                {
                    worker.story.traits.allTraits.Clear();
                }
                if (worker.relations != null)
                {
                    worker.relations.ClearAllRelations();
                }

                // ③ 固定资质：技能只进品质/产量，且**永不成长**（不调 skills.Learn）
                if (worker.skills != null)
                {
                    for (int i = 0; i < worker.skills.skills.Count; i++)
                    {
                        worker.skills.skills[i].Level = Props.skillLevel;
                    }
                }
                worker.Notify_DisabledWorkTypesChanged();

                if (worker.workSettings != null)
                {
                    worker.workSettings.EnableAndInitialize();
                }
            }
            catch (Exception e)
            {
                Log.Error("[DigitalStorage] 生成数字工人失败：" + e);
                worker = null;
            }
        }
    }

    /// <summary>
    /// <b>假 pawn 的"借地图"作用域</b>：临时把 <c>mapIndexOrState</c> 与 <c>Position</c> 设成真的，
    /// 让读 <c>pawn.Map</c> / 要求 <c>claimant.Spawned</c> 的原版代码放行。
    ///
    /// <para>先例：<c>god hand GodAssistantController.cs:288-318</c>（它用在制作产物上）。
    /// 我们比它多一个理由：<b>找活阶段也要借</b> —— <c>WorkGiver.HasJobOnThing</c> 里就有
    /// <c>CanReserve</c>，而它要求 <c>claimant.Spawned &amp;&amp; claimant.Map == map</c>。
    /// 但作用域要**尽量窄**，且必须 <c>try/finally</c>。</para>
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
    /// <para>认领表**不进存档**：读档后所有代理重新找活即可（比存一份可能对不上的表更安全）。</para>
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
            Dictionary<Thing, CompDigitalWorker> d = For(map);
            CompDigitalWorker owner;
            if (!d.TryGetValue(t, out owner)) return false;
            if (owner == me) return false;
            if (owner == null || owner.parent == null || !owner.parent.Spawned)
            {
                d.Remove(t);
                return false;
            }
            return true;
        }

        public static void TryClaim(Map map, Thing t, CompDigitalWorker me)
        {
            if (map == null || t == null || me == null) return;
            For(map)[t] = me;
        }

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
