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
            var mapComp = pawn.Map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return true;
            var cores = mapComp.GetAllCores();
            bool anyCore = false;
            for (int i = 0; i < cores.Count; i++) { if (IsCoreUsable(cores[i])) { anyCore = true; break; } }
            if (!anyCore) return true;

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

            var mapComp = pawn.Map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return null;
            var cores = mapComp.GetAllCores();

            billGiver.BillStack.RemoveIncompletableBills();
            bool chip = Hediff_TerminalImplant.HasTerminalImplant(pawn);

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

                foreach (var core in cores)
                {
                    if (!IsCoreUsable(core)) continue;
                    var plan = LedgerBillPlanner.TryPlan(bill, core);
                    if (plan == null) continue;

                    if (chip) return MakeJob(thing, bill, core, IntVec3.Invalid);

                    IntVec3 proxy = PickProxyCell(pawn, core);
                    if (!proxy.IsValid) continue;
                    return MakeJob(thing, bill, core, proxy);
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
