using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using DigitalStorage.Services;
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

            // L1: 刚搬出/取出的物品不立刻送回核心（保护窗口期内）
            if (CompAutoIngest.IsRecentlyWithdrawn(t)) return null;

            bool chip = Hediff_TerminalImplant.HasTerminalImplant(pawn);
            CoreAccess? best = null;
            int bestDist = int.MaxValue;

            foreach (var access in CoreFinder.AllUsableAccesses(pawn))
            {
                // H1: 统一判据（ItemRouter.ShouldCoreTakeItem）——
                // 核心接受该物品 且 不存在「优先级 > 核心」的储存区时才派送核心工单
                // （平级 zone 不抢——新物品进核心，用户 7.31 拍板）。
                // 旧实现用 CurrentStoragePriorityOf(t)=Unstored 做让位基准，
                // 任何储存区都让位 → 核心优先级对地面物品完全失效（I10.01.2）。
                if (!ItemRouter.ShouldCoreTakeItem(t, t.Map, access.ledgerCore,
                    access.ledgerCore.GetCapacity()))
                    continue;

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
