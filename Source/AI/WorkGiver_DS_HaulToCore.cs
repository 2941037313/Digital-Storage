using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 阶段 4.1：扫地图上可搬运物品，派"送进核心"工单。
    /// 优先级在 Defs 里设为高于 HaulGeneral(15)。
    /// 芯片 pawn 跳过走代理点；普通 pawn 走最近接口/核心交互格。
    /// </summary>
    public class WorkGiver_DS_HaulToCore : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;
        public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            return pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling();
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            if (pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling().Count == 0) return true;
            var mapComp = pawn.Map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return true;
            var cores = mapComp.GetAllCores();
            for (int i = 0; i < cores.Count; i++)
            {
                if (IsCoreUsable(cores[i])) return false;
            }
            return true;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return FindAcceptingCore(pawn, t, forced) != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            var core = FindAcceptingCore(pawn, t, forced);
            if (core == null) return null;

            // targetA = 地上物品；targetC = 目标核心；targetB = 代理点（芯片 pawn 留 Invalid）
            var job = JobMaker.MakeJob(DigitalStorage_JobDefOf.DigitalStorage_IngestToCore, t);
            job.SetTarget(TargetIndex.C, core);
            job.count = t.stackCount;

            if (!Hediff_TerminalImplant.HasTerminalImplant(pawn))
            {
                IntVec3 proxy = PickProxyCell(pawn, core);
                if (!proxy.IsValid) return null;
                job.SetTarget(TargetIndex.B, proxy);
            }
            return job;
        }

        // ---------- helpers ----------

        private Building_StorageCore FindAcceptingCore(Pawn pawn, Thing t, bool forced)
        {
            if (t == null || t.Destroyed) return null;
            if (!LedgerPolicy.CanIngest(t)) return null;
            if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, forced)) return null;

            var mapComp = pawn.Map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return null;

            var cores = mapComp.GetAllCores();
            Building_StorageCore best = null;
            int bestDist = int.MaxValue;

            for (int i = 0; i < cores.Count; i++)
            {
                var core = cores[i];
                if (!IsCoreUsable(core)) continue;
                if (!core.Ledger.CanAccept(t, core.GetCapacity())) continue;
                // 芯片 pawn 不用走代理点，直接按核心距离估
                IntVec3 anchor = Hediff_TerminalImplant.HasTerminalImplant(pawn)
                    ? core.Position
                    : PickProxyCell(pawn, core);
                if (!anchor.IsValid) continue;
                int d = (anchor - pawn.Position).LengthManhattan;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = core;
                }
            }
            return best;
        }

        private static bool IsCoreUsable(Building_StorageCore core)
        {
            return core != null && core.Spawned && !core.Destroyed && core.Powered;
        }

        private static IntVec3 PickProxyCell(Pawn pawn, Building_StorageCore core)
        {
            IntVec3 best = IntVec3.Invalid;
            int bestDist = int.MaxValue;
            foreach (var cell in core.GetProxyCells())
            {
                if (!cell.InBounds(pawn.Map)) continue;
                if (!pawn.CanReach(cell, PathEndMode.Touch, Danger.Deadly)) continue;
                int d = (cell - pawn.Position).LengthManhattan;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = cell;
                }
            }
            return best;
        }
    }

    [DefOf]
    public static class DigitalStorage_JobDefOf
    {
        public static JobDef DigitalStorage_IngestToCore;
        public static JobDef DigitalStorage_WithdrawToBill;

        static DigitalStorage_JobDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(DigitalStorage_JobDefOf)); }
    }
}
