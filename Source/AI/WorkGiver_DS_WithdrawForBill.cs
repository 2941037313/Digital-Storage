using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
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

                // 培育器/自主工作台（机械培育器）：Gathering 之外由原版 WorkOnFormedBill
                // 接管（Preparing 继续周期 / Formed 取成品）。这里派取料 job 会重复从
                // 账本扣料——原料已进 innerContainer 或机械体已成型。
                if (bill is Bill_Autonomous billAuto && billAuto.State != FormingState.Gathering)
                    continue;
                // 机械培育器毒废料包满：对齐原版 WorkGiver_DoBill.StartOrResumeBillJob
                // 的 WasteContainerFull 拦截，避免废料溢出时仍启动培育。
                if (bill is Bill_Mech billMech && billMech.Gestator.WasteProducer.Waste != null
                    && billMech.Gestator.GestatingMech == null)
                    continue;

                // 有未完成物品 → 让原版 DoBill 处理续工，不从核心取新材料
                if (bill is Bill_ProductionWithUft uftBill && uftBill.BoundUft != null)
                    continue;

                foreach (var access in accesses)
                {
                    // 防御:未知配方类型/账本结构异常 → 跳过该核心交回原版 DoBill,
                    // 不让右键菜单(GetWorkGiverOption)因本 WorkGiver 抛异常消失
                    List<(ItemKey key, int count)> plan;
                    try
                    {
                        plan = LedgerBillPlanner.TryPlan(bill, access.ledgerCore);
                    }
                    catch (Exception e)
                    {
                        if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                            Log.Warning($"[DS-Job] TryPlan 异常 bill={bill.Label} core={access.ledgerCore}: {e.Message}");
                        continue;
                    }
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

        // helpers moved to CoreFinder
    }
}
