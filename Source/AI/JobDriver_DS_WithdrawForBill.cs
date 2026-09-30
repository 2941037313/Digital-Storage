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
    /// 阶段 4.2：从账本取料 → 工作台做 bill。
    /// 继承 JobDriver_DoBill 复用原版制作流程（UnfinishedThing / DoRecipeWork / FinishRecipe）。
    ///
    /// 前导 Toil 链：
    ///   1) Notify_Starting 里按 bill.recipe.ingredients 重算 plan（单一真相源：账本）；plan 入档存活跨存读档
    ///   2) 账本按 plan 预订
    ///   3) 走代理点（芯片跳过）
    ///   4) 对 plan 每项：账本 Withdraw → 走摆料格 → 手动落地 + 填 job.placedThings
    ///   5) yield base.MakeNewToils()：targetQueueB 为空 → 原版跳过收集段 → 直接制作。
    ///
    /// targetA = 工作台；targetB = 代理点 Cell（Invalid=芯片跳过）；targetC = 核心。
    /// </summary>
    public class JobDriver_DS_WithdrawForBill : JobDriver_DoBill
    {
        private List<ItemKey> planKeys = new List<ItemKey>();
        private List<int> planCounts = new List<int>();

        public Building_StorageCore TargetCore => job.GetTarget(TargetIndex.C).Thing as Building_StorageCore;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (!pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed))
                return false;
            var ta = job.GetTarget(TargetIndex.A).Thing;
            if (ta != null && ta.def.hasInteractionCell
                && !pawn.ReserveSittableOrSpot(ta.InteractionCell, job, errorOnFailed))
                return false;
            return true;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                var keyStrs = new List<string>(planKeys.Count);
                for (int i = 0; i < planKeys.Count; i++) keyStrs.Add(planKeys[i].ToSaveString());
                Scribe_Collections.Look(ref keyStrs, "planKeys", LookMode.Value);
                Scribe_Collections.Look(ref planCounts, "planCounts", LookMode.Value);
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                List<string> keyStrs = null;
                Scribe_Collections.Look(ref keyStrs, "planKeys", LookMode.Value);
                Scribe_Collections.Look(ref planCounts, "planCounts", LookMode.Value);
                planKeys = new List<ItemKey>();
                if (keyStrs != null)
                {
                    foreach (var s in keyStrs)
                    {
                        if (ItemKey.TryParse(s, out var k)) planKeys.Add(k);
                    }
                }
                if (planCounts == null) planCounts = new List<int>();
            }
        }

        public override void Notify_Starting()
        {
            base.Notify_Starting();
            JobDriver_DS_ReserveHelper.RegisterRelease(this, TargetCore);

            // 启动时基于当前账本+bill重算 plan（WorkGiver 到 StartJob 之间可能隔帧）
            if (planKeys.Count == 0 && TargetCore != null && job.bill != null)
            {
                List<(ItemKey key, int count)> plan;
                try
                {
                    plan = LedgerBillPlanner.TryPlan(job.bill, TargetCore);
                }
                catch (Exception e)
                {
                    // 防御:未知配方类型 → 放弃本 job,原版 DoBill 接力
                    if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                        Log.Warning($"[DS-Withdraw] Notify_Starting TryPlan 异常 bill={job.bill.Label}: {e.Message}");
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                if (plan == null)
                {
                    // 启动瞬间被别人抢了材料 → 放弃，让原版 DoBill 接力
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                foreach (var (k, c) in plan)
                {
                    planKeys.Add(k);
                    planCounts.Add(c);
                }
            }
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => TargetCore == null || !TargetCore.Spawned || !TargetCore.Powered);

            // 1) 账本按 plan 批量预订
            yield return ReservePlanInLedger();

            // 2) 走代理点（芯片跳过）
            if (!Hediff_TerminalImplant.HasTerminalImplant(pawn) && job.GetTarget(TargetIndex.B).IsValid)
            {
                yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.Touch);
            }

            // 3) 对 plan 每项取料 + 摆到工作台摆料格
            yield return WithdrawAndPlaceAllToil();

            // 4) yield 原版制作流程（targetQueueB 空 → 原版跳过收集段）
            foreach (var t in base.MakeNewToils())
            {
                yield return t;
            }
        }

        // ---------- 前导 Toil ----------

        private Toil ReservePlanInLedger()
        {
            var toil = ToilMaker.MakeToil("DS_ReserveLedger");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = () =>
            {
                var core = TargetCore;
                if (core == null || job.bill == null) { EndJobWith(JobCondition.Incompletable); return; }
                var ledger = core.Ledger;

                // 社区反馈「核心里东西不够，小人依旧能直接在机械培育器里做」+「材料凭空出现」：
                // WorkGiver 的 TryPlan 只算 Available 不占预订，从派单到本 toil 执行之间
                // 材料可能已经被别的 job 吃掉。现在改为「此刻重算 plan + 预订」一次完成：
                // plan 就是此刻能锁住的量，后面的 WithdrawAndPlaceAllToil 必然拿得到。
                // 先清掉本 job 之前占的预订（Notify_Starting 可能已预留一批），避免重复占用。
                ledger.ReleaseByJob(job);

                List<(ItemKey key, int count)> fresh;
                try
                {
                    fresh = LedgerBillPlanner.TryPlan(job.bill, core);
                }
                catch (Exception e)
                {
                    if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                        Log.Warning($"[DS-Withdraw] 执行期 TryPlan 异常 bill={job.bill.Label}: {e.Message}");
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                if (fresh == null || fresh.Count == 0)
                {
                    // 材料已被抢走 → 放弃，交回原版 DoBill 接力
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                for (int i = 0; i < fresh.Count; i++)
                {
                    int got = ledger.Reserve(job, fresh[i].key, fresh[i].count);
                    if (got < fresh[i].count)
                    {
                        EndJobWith(JobCondition.Incompletable);
                        return;
                    }
                }

                // 用重算结果覆盖 plan（与账本预订保持一致）
                planKeys.Clear();
                planCounts.Clear();
                for (int i = 0; i < fresh.Count; i++)
                {
                    planKeys.Add(fresh[i].key);
                    planCounts.Add(fresh[i].count);
                }
            };
            return toil;
        }

        private Toil WithdrawAndPlaceAllToil()
        {
            var toil = ToilMaker.MakeToil("DS_WithdrawAndPlace");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = () =>
            {
                var actor = toil.actor;
                var workTable = job.GetTarget(TargetIndex.A).Thing;
                var billGiver = workTable as IBillGiver;
                var core = TargetCore;
                if (core == null || workTable == null || billGiver == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                var ledger = core.Ledger;

                for (int i = 0; i < planKeys.Count; i++)
                {
                    var key = planKeys[i];
                    int need = planCounts[i];
                    while (need > 0)
                    {
                        var spawned = ledger.Withdraw(key, need, job);
                        if (spawned == null)
                        {
                            EndJobWith(JobCondition.Incompletable);
                            return;
                        }
                        int taken = spawned.stackCount;

                        // 培育器/自主工作台（机械培育器等 Building_WorkTableAutonomous）：
                        // 原料必须进 innerContainer——原版 CollectIngredientsToils 的
                        // placeInBillGiver=true 走 DepositHauledThingInContainer，
                        // 培育完成时 ClearAndDestroyContents 才消耗容器里的原料。
                        // 放地上会让原料永不消耗（凭空多料）且培育器 UI 显示 0/50。
                        var autoTable = workTable as Building_WorkTableAutonomous;
                        if (autoTable != null)
                        {
                            if (!autoTable.innerContainer.TryAdd(spawned, true))
                            {
                                // 容器异常（不可加）→ 退回账本，放弃 job
                                ledger.AddRaw(key, taken);
                                EndJobWith(JobCondition.Incompletable);
                                return;
                            }
                            (billGiver as INotifyHauledTo)?.Notify_HauledTo(actor, spawned, taken);
                            HaulAIUtility.UpdateJobWithPlacedThings(job, spawned, taken);
                            if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                                Log.Message($"[DS-Withdraw] {actor.LabelShort} deposited {key} x{taken} into autonomous billgiver {workTable.LabelShort}");
                            need -= taken;
                            continue;
                        }

                        IntVec3 placeCell = PickPlaceCell(billGiver, actor.Map, spawned);
                        bool placed;
                        if (placeCell.IsValid)
                        {
                            placed = GenPlace.TryPlaceThing(spawned, placeCell, actor.Map, ThingPlaceMode.Direct,
                                (t, added) =>
                                {
                                    HaulAIUtility.UpdateJobWithPlacedThings(job, t, added);
                                    actor.Reserve(t, job, 1, t.stackCount, null, true);
                                });
                            if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                                Log.Message($"[DS-Withdraw] {actor.LabelShort} placed {key} x{taken} at worktable {placeCell} placed={placed}");
                            if (!placed)
                            {
                                GenPlace.TryPlaceThing(spawned, actor.Position, actor.Map, ThingPlaceMode.Near,
                                    (t, added) =>
                                    {
                                        HaulAIUtility.UpdateJobWithPlacedThings(job, t, added);
                                        actor.Reserve(t, job, 1, t.stackCount, null, true);
                                    });
                                if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                                    Log.Message($"[DS-Withdraw] {actor.LabelShort} fallback: placed {key} x{taken} at pawn feet (worktable place failed)");
                            }
                        }
                        else
                        {
                            GenPlace.TryPlaceThing(spawned, actor.Position, actor.Map, ThingPlaceMode.Near,
                                (t, added) =>
                                {
                                    HaulAIUtility.UpdateJobWithPlacedThings(job, t, added);
                                    actor.Reserve(t, job, 1, t.stackCount, null, true);
                                });
                            if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                                Log.Message($"[DS-Withdraw] {actor.LabelShort} placed {key} x{taken} at pawn feet (no placeCell)");
                        }

                        need -= taken;
                    }
                }
            };
            return toil;
        }

        private static IntVec3 PickPlaceCell(IBillGiver giver, Map map, Thing t)
        {
            foreach (var c in giver.IngredientStackCells)
            {
                if (!c.InBounds(map)) continue;
                if (GenPlace.HaulPlaceBlockerIn(t, c, map, false) == null) return c;
            }
            var table = (Thing)giver;
            foreach (var c in GenRadial.RadialCellsAround(table.Position, 3f, true))
            {
                if (!c.InBounds(map)) continue;
                if (GenPlace.HaulPlaceBlockerIn(t, c, map, false) == null) return c;
            }
            return IntVec3.Invalid;
        }
    }
}
