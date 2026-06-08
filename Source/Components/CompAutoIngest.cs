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

        private static readonly HashSet<int> recentlyWithdrawn = new HashSet<int>();
        private static Thing[] candidateBuffer = new Thing[30];
        private static int lastClearTick = -1;

        public static void MarkWithdrawn(Thing t)
        {
            if (t != null) recentlyWithdrawn.Add(t.thingIDNumber);
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

            if (tick - lastClearTick > 120)
            {
                recentlyWithdrawn.Clear();
                lastClearTick = tick;
            }

            var ledger = core.Ledger;
            int capacity = core.GetCapacity();

            int rate = ingestRateCache;
            int taken = 0;

            // 收集候选项到静态小缓冲，避免 Ingest/Destroy 修改 haulables 列表导致迭代异常
            var haulables = map.listerHaulables.ThingsPotentiallyNeedingHauling();
            int bufSize = rate * 3;
            if (candidateBuffer.Length < bufSize)
                candidateBuffer = new Thing[bufSize];
            int bufCount = 0;

            foreach (var t in haulables)
            {
                if (bufCount >= bufSize) break;
                if (!LedgerPolicy.CanIngest(t)) continue;
                if (!core.AllowsItem(t)) continue;
                if (t.IsForbidden(Faction.OfPlayer)) continue;
                if (t.IsInAnyStorage()) continue;
                if (map.reservationManager.IsReserved(t)) continue;
                if (recentlyWithdrawn.Contains(t.thingIDNumber)) continue;
                if (!ledger.CanAccept(t, capacity)) continue;
                candidateBuffer[bufCount++] = t;
            }

            for (int i = 0; i < bufCount && taken < rate; i++)
            {
                var t = candidateBuffer[i];
                if (t.Destroyed) continue;

                if (ItemRouter.RouteGroundItem(t, map, core, capacity))
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
