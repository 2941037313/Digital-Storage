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
    /// 跨图支持：通过 CoreFinder 发现远程核心（同 NetworkName + 跨图接口）。
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
            return !CoreFinder.AnyUsableAccess(pawn);
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return FindAcceptingAccess(pawn, t, forced) != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            var best = FindAcceptingAccess(pawn, t, forced);
            if (best == null) return null;

            var job = JobMaker.MakeJob(DigitalStorage_JobDefOf.DigitalStorage_IngestToCore, t);
            job.SetTarget(TargetIndex.C, best.Value.ledgerCore);
            job.count = t.stackCount;

            if (!Hediff_TerminalImplant.HasTerminalImplant(pawn))
            {
                IntVec3 proxy = CoreFinder.PickProxyCell(pawn, best.Value.proxyCore);
                if (!proxy.IsValid) return null;
                job.SetTarget(TargetIndex.B, proxy);
            }
            return job;
        }

        // ---------- helpers ----------

        private CoreAccess? FindAcceptingAccess(Pawn pawn, Thing t, bool forced)
        {
            if (t == null || t.Destroyed) return null;
            if (!LedgerPolicy.CanIngest(t)) return null;
            if (t.IsForbidden(Faction.OfPlayer)) return null;
            if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, forced)) return null;
            if (t.Map.reservationManager.IsReserved(t)) return null;

            // 跳过已在任何存储区（Stockpile/架/缓冲仓库）中的物品
            if (t.IsInAnyStorage()) return null;

            // 如果物品已有合适的储存区目标，让原版搬运处理
            if (StoreUtility.TryFindBestBetterStoreCellFor(t, pawn, t.Map,
                StoreUtility.CurrentStoragePriorityOf(t), pawn.Faction, out _, true))
                return null;

            bool chip = Hediff_TerminalImplant.HasTerminalImplant(pawn);
            CoreAccess? best = null;
            int bestDist = int.MaxValue;

            foreach (var access in CoreFinder.AllUsableAccesses(pawn))
            {
                if (!access.ledgerCore.AllowsItem(t)) continue;
                if (!access.ledgerCore.Ledger.CanAccept(t, access.ledgerCore.GetCapacity())) continue;

                IntVec3 anchor = chip
                    ? pawn.Position
                    : CoreFinder.PickProxyCell(pawn, access.proxyCore);
                if (!anchor.IsValid) continue;

                int d = (anchor - pawn.Position).LengthManhattan;
                if (d < bestDist) { bestDist = d; best = access; }
            }
            return best;
        }
    }

    [DefOf]
    public static class DigitalStorage_JobDefOf
    {
        public static JobDef DigitalStorage_IngestToCore;
        public static JobDef DigitalStorage_WithdrawToBill;
        public static JobDef DigitalStorage_WithdrawToConstruction;
        public static JobDef DigitalStorage_WithdrawToSpot;
        public static JobDef DigitalStorage_ConsumeFromLedger;

        static DigitalStorage_JobDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(DigitalStorage_JobDefOf)); }
    }
}
