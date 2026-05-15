using System.Linq;
using DigitalStorage.Core;
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

        public bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        public bool IsResearched =>
            ResearchProjectDef.Named("DigitalStorage_AutoIngest1")?.IsFinished ?? false;

        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            core = parent as Building_StorageCore;
        }

        private int IngestRate
        {
            get
            {
                if (ResearchProjectDef.Named("DigitalStorage_AutoIngest3")?.IsFinished ?? false) return 10;
                if (ResearchProjectDef.Named("DigitalStorage_AutoIngest2")?.IsFinished ?? false) return 5;
                return 1;
            }
        }

        public override void CompTick()
        {
            base.CompTick();
            if (core == null || !enabled || !IsResearched || !core.Powered) return;

            var map = core.Map;
            if (map == null) return;

            var ledger = core.Ledger;
            int capacity = core.GetCapacity();
            var haulables = map.listerHaulables.ThingsPotentiallyNeedingHauling().ToList();
            int rate = IngestRate;
            int taken = 0;

            foreach (var t in haulables)
            {
                if (taken >= rate) break;
                if (!LedgerPolicy.CanIngest(t)) continue;
                if (t.IsForbidden(Faction.OfPlayer)) continue;
                if (map.reservationManager.IsReserved(t)) continue;
                if (!ledger.CanAccept(t, capacity)) continue;

                if (ledger.Ingest(t, capacity))
                    taken++;
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref enabled, "autoIngestEnabled", true);
        }
    }
}
