using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Core;
using DigitalStorage.Services;
using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// I5a+I5b: 自动收纳。Tick 扫 listerHaulables → 过滤 → 吸入自身账本。
    /// I5b: 研究解锁 + Gizmo 开关 + 电力检查。
    /// I5c: 多级速率。
    /// </summary>
    public class CompAutoIngest : ThingComp
    {
        private Building_StorageCore core;
        private bool enabled = true;
        private int ingestRateCache = -1;
        private bool researchedCache;
        private int researchCheckTick = -1;

        /// <summary>
        /// L1: 刚取出标记表——按 thingID 记过期 tick（固定 300 tick 完整窗口），
        /// 不再每 120 tick 全清（旧实现窗口长度不确定，延迟拾取会被吞回）。
        /// </summary>
        private static readonly Dictionary<int, int> withdrawnUntil = new Dictionary<int, int>();
        private static Thing[] candidateBuffer = new Thing[30];
        private static int lastSweepTick = -1;

        public static void MarkWithdrawn(Thing t)
        {
            if (t != null)
                withdrawnUntil[t.thingIDNumber] = Find.TickManager.TicksGame + 300;
        }

        public static bool IsRecentlyWithdrawn(Thing t)
        {
            return t != null && withdrawnUntil.TryGetValue(t.thingIDNumber, out int until)
                && Find.TickManager.TicksGame < until;
        }

        /// <summary>L1: 低频清扫过期项，防字典无界增长。</summary>
        public static void SweepWithdrawn()
        {
            int tick = Find.TickManager.TicksGame;
            if (tick - lastSweepTick < 2000) return;
            lastSweepTick = tick;
            var expired = new List<int>();
            foreach (var kv in withdrawnUntil)
                if (tick >= kv.Value) expired.Add(kv.Key);
            for (int i = 0; i < expired.Count; i++)
                withdrawnUntil.Remove(expired[i]);
        }

        /// <summary>
        /// M3: 找接受该物品的「最高优先级」核心——路由决策基准。
        /// 多核心并存时物品去向不再取决于谁先 tick（旧实现各核心用自己的优先级独立决策）。
        /// </summary>
        private static Building_StorageCore FindBestIngestCore(Map map, Thing t)
        {
            var comp = DigitalStorageMapComponent.For(map);
            if (comp == null) return null;
            Building_StorageCore best = null;
            foreach (var core in comp.GetAllCores())
            {
                if (core == null || !core.Powered) continue;
                if (!core.AllowsItem(t)) continue;
                if (!core.Ledger.CanAccept(t, core.GetCapacity())) continue;
                if (best == null || core.storagePriority > best.storagePriority)
                    best = core;
            }
            return best;
        }

        public bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            core = parent as Building_StorageCore;
        }

        public bool IsResearched
        {
            get
            {
                int tick = Find.TickManager.TicksGame;
                if (tick != researchCheckTick)
                {
                    researchCheckTick = tick;
                    researchedCache = (ResearchProjectDef.Named("DigitalStorage_AutoIngest1")?.IsFinished ?? false);
                    if (ResearchProjectDef.Named("DigitalStorage_AutoIngest3")?.IsFinished ?? false) ingestRateCache = 10;
                    else if (ResearchProjectDef.Named("DigitalStorage_AutoIngest2")?.IsFinished ?? false) ingestRateCache = 5;
                    else ingestRateCache = 1;
                }
                return researchedCache;
            }
        }

        public override void CompTick()
        {
            base.CompTick();
            if (core == null || !enabled || !core.Powered) return;

            int tick = Find.TickManager.TicksGame;

            // 每 15 tick 一次，用核心 ID 错开相位
            if ((tick + core.thingIDNumber) % 15 != 0) return;

            if (!IsResearched) return;

            var map = core.Map;
            if (map == null) return;

            SweepWithdrawn(); // L1: 清扫过期标记

            int rate = ingestRateCache;
            int taken = 0;

            // 收集候选项到静态小缓冲，避免 Ingest/Destroy 修改 haulables 列表导致迭代异常。
            // M4: 候选收集不再按核心过滤器/容量过滤——被过滤的物品仍应路由到合格储存区，
            // 「核心吃不吃」的判断下沉到 RouteGroundItem 吞入分支。
            var haulables = map.listerHaulables.ThingsPotentiallyNeedingHauling();
            int bufSize = rate * 3;
            if (candidateBuffer.Length < bufSize)
                candidateBuffer = new Thing[bufSize];
            int bufCount = 0;

            foreach (var t in haulables)
            {
                if (bufCount >= bufSize) break;
                if (!LedgerPolicy.CanIngest(t)) continue;
                if (t.IsForbidden(Faction.OfPlayer)) continue;
                if (t.IsInAnyStorage()) continue;
                if (map.reservationManager.IsReserved(t)) continue;
                if (IsRecentlyWithdrawn(t)) continue;
                candidateBuffer[bufCount++] = t;
            }

            for (int i = 0; i < bufCount && taken < rate; i++)
            {
                var t = candidateBuffer[i];
                if (t.Destroyed) continue;

                // M3: 路由基准统一为「接受该物品的最高优先级核心」
                var best = FindBestIngestCore(map, t);
                if (best == null) continue;
                if (ItemRouter.RouteGroundItem(t, map, best, best.GetCapacity()))
                    taken++;
            }
            // 清理引用防止 GC 泄漏
            for (int i = 0; i < bufCount; i++)
                candidateBuffer[i] = null;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref enabled, "autoIngestEnabled", true);
        }
    }
}
