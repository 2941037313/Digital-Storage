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
    /// 阶段 4.3+：通用"出核心"方向 JobDriver（非 Bill 场景）。
    /// 每趟 Job 取一种 ItemKey，搬到 targetA 放下。
    ///
    /// targetA = 目的地（蓝图 / Frame / 玩家指定格 / 贸易台）
    /// targetB = 代理点 Cell（Invalid = 芯片跳过）
    /// targetC = 核心 Building
    /// </summary>
    public class JobDriver_DS_Withdraw : JobDriver
    {
        private ItemKey planKey;
        private int planCount;

        public Building_StorageCore TargetCore => job.GetTarget(TargetIndex.C).Thing as Building_StorageCore;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            var t = job.GetTarget(TargetIndex.A).Thing;
            return t == null || pawn.Reserve(t, job, 1, -1, null, errorOnFailed);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            ItemKey.Scribe_KeyAndCount(ref planKey, ref planCount, "planKey", "planCount");
        }

        public override void Notify_Starting()
        {
            base.Notify_Starting();
            JobDriver_DS_ReserveHelper.RegisterRelease(this, TargetCore);

            // 玩家右键触发 → 从 pending 消费
            if (planCount <= 0)
                JobDriver_DS_ReserveHelper.TryConsumePendingPlan(job, out planKey, out planCount);

            // 构造场景 → 自动规划
            if (planCount <= 0 && TargetCore != null)
            {
                var constructible = job.GetTarget(TargetIndex.A).Thing as IConstructible;
                if (constructible != null)
                {
                    var plan = ConstructLedgerPlanner.TryPlan(constructible, TargetCore);
                    if (plan != null)
                    {
                        planKey = plan.Value.key;
                        planCount = plan.Value.count;
                    }
                }
            }
            if (planCount <= 0)
            {
                EndJobWith(JobCondition.Incompletable);
            }
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => TargetCore == null || !TargetCore.Spawned || !TargetCore.Powered);

            // 1) 预订账本
            yield return JobDriver_DS_ReserveHelper.MakeReserveToil(this, TargetCore, planKey, planCount);

            // 2) 走到代理点（芯片 pawn 跳过）
            if (!Hediff_TerminalImplant.HasTerminalImplant(pawn) && job.GetTarget(TargetIndex.B).IsValid)
            {
                yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.Touch);
            }

            // 3) 账本取料 → 手上
            yield return WithdrawToHand();

            // 4) 走到目的地
            if (job.GetTarget(TargetIndex.A).IsValid)
            {
                yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch);
            }

            // 5) 蓝图 → Frame（原版 HaulToContainer 同款，解决 #5）
            yield return Toils_Construct.MakeSolidThingFromBlueprintIfNecessary(TargetIndex.A, TargetIndex.None);

            // 6) 塞容器优先（解决 #1 搬运死循环），失败则落地 + 移除 haul 标记
            yield return PlaceHauled();
        }

        // ---------- Toil 积木 ----------

        private Toil WithdrawToHand()
        {
            var toil = ToilMaker.MakeToil("DS_WithdrawToHand");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = () =>
            {
                var actor = toil.actor;
                var core = TargetCore;
                if (core == null) { EndJobWith(JobCondition.Incompletable); return; }

                // 裁剪到 pawn 负重上限（Bug #2）
                int maxCarry = actor.carryTracker.AvailableStackSpace(planKey.def);
                if (maxCarry <= 0) { EndJobWith(JobCondition.Incompletable); return; }
                int take = System.Math.Min(planCount, maxCarry);

                var spawned = core.Ledger.Withdraw(planKey, take, job);
                if (spawned == null) { EndJobWith(JobCondition.Incompletable); return; }
                int taken = actor.carryTracker.TryStartCarry(spawned, spawned.stackCount, false);
                // 失败/部分成功：剩余退回账本，避免扣账后悬空丢失
                if (taken <= 0)
                {
                    core.Ledger.AddRaw(planKey, spawned.stackCount);
                    if (!spawned.Destroyed) spawned.Destroy(DestroyMode.Vanish);
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                if (taken < spawned.stackCount)
                {
                    core.Ledger.AddRaw(planKey, spawned.stackCount);
                    if (!spawned.Destroyed) spawned.Destroy(DestroyMode.Vanish);
                }
            };
            return toil;
        }

        private Toil PlaceHauled()
        {
            var toil = ToilMaker.MakeToil("DS_PlaceHauled");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = () =>
            {
                var actor = toil.actor;
                var carried = actor.carryTracker.CarriedThing;
                if (carried == null) return;

                var targetThing = job.GetTarget(TargetIndex.A).Thing;

                // 构造场景：塞容器优先（不落地 → listerHaulables 看不到，#1）
                if (targetThing is IConstructible)
                {
                    var container = targetThing.TryGetInnerInteractableThingOwner();
                    if (container != null && container.CanAcceptAnyOf(carried, true))
                    {
                        var taken = actor.carryTracker.innerContainer.Take(carried, carried.stackCount);
                        if (taken != null && container.TryAdd(taken, true))
                        {
                            // B2: 不设 forbidden。蓝图 reservation 已保护材料；建造失败时原版退回材料不会带 forbidden。
                            return;
                        }
                        if (taken != null)
                            actor.carryTracker.innerContainer.TryAdd(taken, true);
                    }
                }

                // 非构造场景（右键取料 / 贸易 / 容器放不进）：落地
                IntVec3 dropCell = actor.Position;
                var targetA = job.GetTarget(TargetIndex.A);
                if (targetA.HasThing)
                {
                    var t = targetA.Thing;
                    dropCell = t.Position;
                    if (t is IBillGiver giver)
                    {
                        foreach (var c in giver.IngredientStackCells)
                        {
                            if (c.InBounds(actor.Map)
                                && GenPlace.HaulPlaceBlockerIn(carried, c, actor.Map, false) == null)
                            { dropCell = c; break; }
                        }
                    }
                }
                else if (targetA.IsValid)
                {
                    dropCell = targetA.Cell;
                }
                if (actor.carryTracker.TryDropCarriedThing(dropCell, ThingPlaceMode.Near, out var dropped, null))
                {
                    if (dropped != null && !(targetThing is IConstructible))
                        dropped.SetForbidden(true, false);
                }
            };
            return toil;
        }
    }

    /// <summary>
    /// 构造账本规划器——比 BillPlanner 简单，只按 ThingDef 匹配。
    /// </summary>
    public static class ConstructLedgerPlanner
    {
        public static (ItemKey key, int count)? TryPlan(IConstructible c, Building_StorageCore core)
        {
            // 安装蓝图（搬移已建成建筑/家具）没有材料账单，原版 TotalMaterialCost()
            // 会主动 Log.Error。必须跳过，交给原版安装流程处理。
            if (c is Blueprint_Install) return null;

            var materials = c.TotalMaterialCost();
            if (materials == null || materials.Count == 0) return null;
            var ledger = core.Ledger;

            for (int mi = 0; mi < materials.Count; mi++)
            {
                var need = materials[mi];
                if (need.count <= 0) continue;
                int remaining = c.ThingCountNeeded(need.thingDef);
                if (c is IHaulEnroute enroute)
                    remaining = enroute.GetSpaceRemainingWithEnroute(need.thingDef, null);
                if (remaining <= 0) continue;

                foreach (var kv in ledger.Stock)
                {
                    if (kv.Key.def != need.thingDef) continue;
                    long avail = ledger.Available(kv.Key);
                    if (avail <= 0) continue;
                    int take = (int)Math.Min(avail, (long)remaining);
                    return (kv.Key, take);
                }
            }
            return null;
        }
    }
}
