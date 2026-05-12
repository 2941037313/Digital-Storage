using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 阶段 4.3：扫蓝图 / Frame → 账本有没有对应材料 → 有就派 DigitalStorage_WithdrawToConstruction。
    /// priorityInType=200（高于 DeliverResourcesToFrames=10 和 ToBlueprints=9）。
    /// 账本凑不齐 → 返 null，原版 ConstructDeliverResources 接力。
    ///
    /// WorkGiver 只负责"能派工吗？能就填 targetA/B/C"。
    /// 具体取哪种材料、取多少，由 JobDriver.Notify_Starting 基于启动时账本状态重算。
    /// </summary>
    public class WorkGiver_DS_WithdrawForConstruct : WorkGiver_Scanner
    {
        public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;
        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override ThingRequest PotentialWorkThingRequest =>
            ThingRequest.ForGroup(ThingRequestGroup.BuildingFrame);

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            foreach (var t in pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
                yield return t;
            foreach (var t in pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint))
                yield return t;
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            var mapComp = pawn.Map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return true;
            var cores = mapComp.GetAllCores();
            for (int i = 0; i < cores.Count; i++) { if (IsCoreUsable(cores[i])) return false; }
            return true;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t.Faction != pawn.Faction) return false;
            var constructible = t as IConstructible;
            if (constructible == null) return false;
            if (!(t is Frame) && !(t is Blueprint)) return false;
            if (!pawn.CanReserve(t, 1, -1, null, forced)) return false;
            if (!GenConstruct.CanConstruct(t, pawn, def.workType, forced, DigitalStorage_JobDefOf.DigitalStorage_WithdrawToConstruction))
                return false;

            var core = FindCoreWithMaterial(pawn, constructible);
            return core != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            var constructible = t as IConstructible;
            if (constructible == null) return null;

            var core = FindCoreWithMaterial(pawn, constructible);
            if (core == null) return null;

            var job = JobMaker.MakeJob(DigitalStorage_JobDefOf.DigitalStorage_WithdrawToConstruction, t);
            job.SetTarget(TargetIndex.C, core);

            if (!Hediff_TerminalImplant.HasTerminalImplant(pawn))
            {
                IntVec3 proxy = PickProxyCell(pawn, core);
                if (!proxy.IsValid) return null;
                job.SetTarget(TargetIndex.B, proxy);
            }
            return job;
        }

        // ---------- helpers ----------

        private Building_StorageCore FindCoreWithMaterial(Pawn pawn, IConstructible c)
        {
            var mapComp = pawn.Map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return null;
            bool chip = Hediff_TerminalImplant.HasTerminalImplant(pawn);

            foreach (var core in mapComp.GetAllCores())
            {
                if (!IsCoreUsable(core)) continue;
                var plan = ConstructLedgerPlanner.TryPlan(c, core);
                if (plan == null) continue;
                if (chip) return core;
                if (PickProxyCell(pawn, core).IsValid) return core;
            }
            return null;
        }

        private static bool IsCoreUsable(Building_StorageCore core) =>
            core != null && core.Spawned && !core.Destroyed && core.Powered;

        private static IntVec3 PickProxyCell(Pawn pawn, Building_StorageCore core)
        {
            IntVec3 best = IntVec3.Invalid;
            int bestDist = int.MaxValue;
            foreach (var c in core.GetProxyCells())
            {
                if (!c.InBounds(pawn.Map)) continue;
                if (!pawn.CanReach(c, PathEndMode.Touch, Danger.Deadly)) continue;
                int d = (c - pawn.Position).LengthManhattan;
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }
    }
}
