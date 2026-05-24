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
            if (!CoreFinder.AnyUsableAccess(pawn))
            {
                if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                    Log.Message($"[DS-Job] ShouldSkip={true} ({def.workType.defName}) pawn={pawn.LabelShort}: AnyUsableAccess=false");
                return true;
            }
            if (pawn.workSettings.GetPriority(def.workType) == 0)
            {
                if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                    Log.Message($"[DS-Job] ShouldSkip={true} ({def.workType.defName}) pawn={pawn.LabelShort}: priority=0");
                return true;
            }
            if (pawn.WorkTagIsDisabled(def.workTags))
            {
                if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                    Log.Message($"[DS-Job] ShouldSkip={true} ({def.workType.defName}) pawn={pawn.LabelShort}: WorkTagDisabled");
                return true;
            }

            if (forced) return false;

            var list = pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.PotentialBillGiver);
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is IBillGiver bg) || bg == pawn) continue;
                var bs = bg.BillStack;
                for (int j = 0; j < bs.Count; j++)
                {
                    var bill = bs[j];
                    if (!bill.ShouldDoNow()) continue;
                    // 只看与本 workType 匹配的 bill
                    if (bill.recipe.requiredGiverWorkType != null
                        && bill.recipe.requiredGiverWorkType != def.workType)
                        continue;
                    if (bill.recipe.requiredGiverWorkType == null && bill.recipe.workSkill != null
                        && !WorkTypeMatchesSkill(def.workType, bill.recipe.workSkill))
                        continue;

                    if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                        Log.Message($"[DS-Job] ShouldSkip={false} ({def.workType.defName}) pawn={pawn.LabelShort}: bill={bill.Label} on {bg}");
                    return false;
                }
            }
            if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                Log.Message($"[DS-Job] ShouldSkip={true} ({def.workType.defName}) pawn={pawn.LabelShort}: no active bills");
            return true;
        }

        public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
        {
            bool dbg = DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog;
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
                // 食谱未指定 workType 时，检查技能匹配（防医生做雕塑）
                if (bill.recipe.requiredGiverWorkType == null && bill.recipe.workSkill != null
                    && !WorkTypeMatchesSkill(def.workType, bill.recipe.workSkill))
                    continue;
                if (!bill.ShouldDoNow()) continue;
                if (!bill.PawnAllowedToStartAnew(pawn)) continue;
                if (bill.recipe.FirstSkillRequirementPawnDoesntSatisfy(pawn) != null) continue;

                // 有未完成物品 → 让原版 DoBill 处理续工，不从核心取新材料
                if (bill is Bill_ProductionWithUft uftBill)
                {
                    if (uftBill.BoundUft != null)
                    {
                        if (uftBill.BoundWorker == pawn && pawn.CanReserveAndReach(uftBill.BoundUft, PathEndMode.Touch, Danger.Deadly, 1, -1, null, false) && !uftBill.BoundUft.IsForbidden(pawn))
                        {
                            if (dbg) Log.Message($"[DS-Job] {pawn.LabelShort} bill={bill.Label}: BoundUft continue (BoundWorker=me)");
                            continue;
                        }
                        if (dbg) Log.Message($"[DS-Job] {pawn.LabelShort} bill={bill.Label}: BoundUft exists but BoundWorker!=me → FALLING THROUGH (BUG)");
                    }
                    if (FindUnfinishedForBill(pawn, uftBill) != null)
                    {
                        if (dbg) Log.Message($"[DS-Job] {pawn.LabelShort} bill={bill.Label}: found unbound UFT → continue");
                        continue;
                    }
                }

                foreach (var access in accesses)
                {
                    var plan = LedgerBillPlanner.TryPlan(bill, access.ledgerCore);
                    if (plan == null) continue;

                    if (chip)
                    {
                        if (dbg) Log.Message($"[DS-Job] {pawn.LabelShort}(chip) → bill={bill.Label} on {thing.LabelShort} SUCCESS");
                        return MakeJob(thing, bill, access.ledgerCore, IntVec3.Invalid);
                    }

                    IntVec3 proxy = CoreFinder.PickProxyCell(pawn, access.proxyCore);
                    if (!proxy.IsValid)
                    {
                        if (dbg) Log.Message($"[DS-Job] {pawn.LabelShort} bill={bill.Label}: TryPlan OK but PickProxyCell invalid for core={access.proxyCore}");
                        continue;
                    }
                    if (dbg) Log.Message($"[DS-Job] {pawn.LabelShort}(non-chip) → bill={bill.Label} x{plan.Count}items proxy={proxy} on {thing.LabelShort} SUCCESS");
                    return MakeJob(thing, bill, access.ledgerCore, proxy);
                }

                // 核心无材料 → 芯片 pawn 从缓冲仓库传送
                if (chip && BufferWarehouseJobHelper.TryTakeForBill(pawn, bill, thing, out var bwJob))
                {
                    if (dbg) Log.Message($"[DS-Job] {pawn.LabelShort}(chip) bill={bill.Label}: BufferWarehouse fallback SUCCESS");
                    return bwJob;
                }
            }

            if (dbg) Log.Message($"[DS-Job] {pawn.LabelShort} {def.workType.defName} JobOnThing on {thing.LabelShort}: no job returned");
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

        private static bool WorkTypeMatchesSkill(WorkTypeDef w, SkillDef s)
        {
            string wn = w?.defName ?? "";
            string sn = s?.defName ?? "";
            // Crafting/Smithing/Tailoring 共用 Crafting 技能
            if (sn == "Crafting" && (wn == "Crafting" || wn == "Smithing" || wn == "Tailoring"))
                return true;
            if (sn == "Artistic" && wn == "Art") return true;
            if (sn == "Cooking" && wn == "Cooking") return true;
            if (sn == "Medicine" && wn == "Doctor") return true;
            if (sn == "Intellectual" && wn == "Research") return true;
            return false;
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
