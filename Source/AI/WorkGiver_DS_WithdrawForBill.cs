using System;
using DigitalStorage.Components;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 阶段 4.2：扫 IBillGiver → 查账本凑材料 → 凑齐就派 DigitalStorage_WithdrawToBill。
    /// priorityInType 设为 999，抢在所有 DoBillsXxx 之前。
    /// 账本凑不齐 → 返 null，原版 DoBill 接力跑地图找材料。
    /// </summary>
    public class WorkGiver_DS_WithdrawForBill : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.InteractionCell;
        public override Danger MaxPathDanger(Pawn pawn) => Danger.Some;

        public override ThingRequest PotentialWorkThingRequest =>
            ThingRequest.ForGroup(ThingRequestGroup.PotentialBillGiver);

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            if (!CoreFinder.AnyUsableAccess(pawn)) return true;
            // forced（右键）不检查 bills，让 JobOnThing 逐个评估
            if (forced) return false;

            var list = pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.PotentialBillGiver);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is IBillGiver bg && bg != pawn && bg.BillStack.AnyShouldDoNow) return false;
            }
            return true;
        }

        public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
        {
            if (!(thing is IBillGiver billGiver)) return null;
            if (!billGiver.CurrentlyUsableForBills()) return null;
            if (!billGiver.BillStack.AnyShouldDoNow) return null;
            if (!pawn.CanReserve(thing, 1, -1, null, forced)) return null;
            if (thing.IsBurning()) return null;
            if (thing.def.hasInteractionCell
                && !pawn.CanReserveSittableOrSpot(thing.InteractionCell, thing, forced))
                return null;

            billGiver.BillStack.RemoveIncompletableBills();
            bool chip = Hediff_TerminalImplant.HasTerminalImplant(pawn);
            var accesses = CoreFinder.AllUsableAccesses(pawn);

            for (int i = 0; i < billGiver.BillStack.Count; i++)
            {
                var bill = billGiver.BillStack[i];
                if (bill.recipe.requiredGiverWorkType != null && bill.recipe.requiredGiverWorkType != def.workType)
                    continue;
                if (Find.TickManager.TicksGame <= bill.nextTickToSearchForIngredients
                    && FloatMenuMakerMap.makingFor != pawn) continue;
                if (!bill.ShouldDoNow()) continue;
                if (!bill.PawnAllowedToStartAnew(pawn)) continue;
                if (bill.recipe.FirstSkillRequirementPawnDoesntSatisfy(pawn) != null) continue;

                // 有未完成物品 → 让原版 DoBill 处理续工，不从核心取新材料
                if (bill is Bill_ProductionWithUft uftBill)
                {
                    if (uftBill.BoundUft != null)
                    {
                        if (uftBill.BoundWorker == pawn && pawn.CanReserveAndReach(uftBill.BoundUft, PathEndMode.Touch, Danger.Deadly, 1, -1, null, false) && !uftBill.BoundUft.IsForbidden(pawn))
                            continue;
                    }
                    if (FindUnfinishedForBill(pawn, uftBill) != null) continue;
                }

                foreach (var access in accesses)
                {
                    var plan = LedgerBillPlanner.TryPlan(bill, access.ledgerCore);
                    if (plan == null) continue;

                    if (chip) return MakeJob(thing, bill, access.ledgerCore, IntVec3.Invalid);

                    IntVec3 proxy = CoreFinder.PickProxyCell(pawn, access.proxyCore);
                    if (!proxy.IsValid) continue;
                    return MakeJob(thing, bill, access.ledgerCore, proxy);
                }
            }
            return null;
        }

        private static Job MakeJob(Thing workTable, Bill bill, Building_StorageCore core, IntVec3 proxyCell)
        {
            var job = JobMaker.MakeJob(DigitalStorage_JobDefOf.DigitalStorage_WithdrawToBill, workTable);
            job.bill = bill;
            job.SetTarget(TargetIndex.C, core);
            if (proxyCell.IsValid) job.SetTarget(TargetIndex.B, proxyCell);
            job.haulMode = HaulMode.ToCellNonStorage;
            return job;
        }

        private static UnfinishedThing FindUnfinishedForBill(Pawn pawn, Bill_ProductionWithUft bill)
        {
            if (bill.recipe?.unfinishedThingDef == null) return null;

            Predicate<Thing> validator = t =>
            {
                if (t.IsForbidden(pawn)) return false;
                var uft = t as UnfinishedThing;
                if (uft == null || uft.Recipe != bill.recipe || uft.Creator != pawn) return false;
                var ingredients = uft.ingredients;
                for (int j = 0; j < ingredients.Count; j++)
                    if (!bill.IsFixedOrAllowedIngredient(ingredients[j].def)) return false;
                return pawn.CanReserve(t, 1, -1, null, false);
            };

            return (UnfinishedThing)GenClosest.ClosestThingReachable(
                pawn.Position, pawn.Map,
                ThingRequest.ForDef(bill.recipe.unfinishedThingDef),
                PathEndMode.InteractionCell,
                TraverseParms.For(pawn, pawn.NormalMaxDanger(), TraverseMode.ByPawn, false, false, false, true),
                9999f, validator, null, 0, -1, false, RegionType.Set_Passable, false, false);
        }

        // helpers moved to CoreFinder
    }
}
