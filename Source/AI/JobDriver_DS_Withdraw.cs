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
    /// 每趟 Job 取**一件真实的 Thing**，搬到 targetA 放下。
    ///
    /// <para><b>4.0 改造</b>：要取的那件东西直接是 <c>targetB</c>（真实 Thing），
    /// 不再有 <c>ItemKey</c>、不再有挂在 job 上的计划字典
    /// （<c>JobDriver_DS_ReserveHelper</c> 已删）。理由同消耗链：
    /// 「全放开」后同一 def 可能有多把传奇剑，<c>(def,stuff)</c> 指不出是哪一把；
    /// 而 job 目标会被 Scribe，存档读档不丢。</para>
    ///
    /// <para><b>纯轮椅</b>：不再有代理点走位 —— 3.0 的 <c>targetB</c> 是"代理格"，
    /// 无芯片的 pawn 要先走过去；现在 <c>targetB</c> 改指「要取的那件东西」，
    /// 材料直接到手（<c>CoreFinder.PickProxyCell</c> / 代理点机制已整体删除）。</para>
    ///
    /// targetA = 目的地（蓝图 / Frame / 玩家指定格）
    /// targetB = 要取的那件东西（住在容器里）
    /// targetC = 它所在的容器建筑（用于通电/存在性检查）
    /// job.count = 取多少
    /// </summary>
    public class JobDriver_DS_Withdraw : JobDriver
    {
        /// <summary>要取的那件东西。</summary>
        private Thing SourceThing => job.GetTarget(TargetIndex.B).Thing;

        /// <summary>它所在的容器（取出前有效）。</summary>
        private Building_StorageCore TargetCore => job.GetTarget(TargetIndex.C).Thing as Building_StorageCore;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // 目的地
            Thing dest = job.GetTarget(TargetIndex.A).Thing;
            if (dest != null && !pawn.Reserve(dest, job, 1, -1, null, errorOnFailed)) return false;

            // 那件东西本身（未 Spawned 也能预订 —— MapHeld 由 ParentHolder => Map 保证非 null）。
            // 两个 pawn 不会抢同一堆。errorOnFailed=false：抢不到也不硬失败，由取料 toil 兜底。
            Thing src = SourceThing;
            if (src != null && !src.Destroyed && !pawn.Reserve(src, job, 1, -1, null, false))
                return false;
            return true;
        }

        public override void Notify_Starting()
        {
            base.Notify_Starting();

            // 右键路径：调用方已经把 Thing + count 放进 job 了
            if (job.count > 0 && SourceThing != null) return;

            // 构造场景：现场规划材料（蓝图 / Frame → 容器里凑得出的那种）
            Thing target = job.GetTarget(TargetIndex.A).Thing;
            IConstructible constructible = target as IConstructible;
            if (constructible == null)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }

            var plan = ConstructMaterialPlanner.TryPlan(constructible, pawn, job.playerForced, pawn.Map);
            if (plan == null)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }

            Thing src = plan.Value.thing;
            job.SetTarget(TargetIndex.B, src);
            job.SetTarget(TargetIndex.C, src.ParentHolder as Thing);
            job.count = plan.Value.count;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // 那件东西没了 / 被销毁 → 放弃
            this.FailOn(() =>
            {
                Thing src = SourceThing;
                return src == null || src.Destroyed;
            });

            // 容器被拆 / 断电 → 放弃（核心必须通电，这是保留下来的唯一门）
            this.FailOn(() =>
            {
                Building_StorageCore core = TargetCore;
                return core != null && (!core.Spawned || !core.Powered);
            });

            // 1) 从容器取料到手上
            yield return WithdrawToHand();

            // 2) 走到目的地
            if (job.GetTarget(TargetIndex.A).IsValid)
            {
                yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch);
            }

            // 3) 蓝图 → Frame（原版 HaulToContainer 同款，解决 #5）
            yield return Toils_Construct.MakeSolidThingFromBlueprintIfNecessary(TargetIndex.A, TargetIndex.None);

            // 4) 塞容器优先（解决 #1 搬运死循环），失败则落地 + 移除 haul 标记
            yield return PlaceHauled();
        }

        // ---------- Toil 积木 ----------

        /// <summary>
        /// 把那件东西从容器取到手上。失败/背不下时**退回容器**，绝不让物品消失。
        /// </summary>
        private Toil WithdrawToHand()
        {
            var toil = ToilMaker.MakeToil("DS_WithdrawToHand");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = () =>
            {
                Pawn actor = toil.actor;
                Thing source = SourceThing;
                if (source == null || source.Destroyed) { EndJobWith(JobCondition.Incompletable); return; }

                IThingHolder holder = source.ParentHolder as IThingHolder;
                ThingOwner owner = holder?.GetDirectlyHeldThings();
                if (owner == null || !owner.Contains(source)) { EndJobWith(JobCondition.Incompletable); return; }

                // 裁剪到 pawn 负重上限（Bug #2）
                int maxCarry = actor.carryTracker.AvailableStackSpace(source.def);
                if (maxCarry <= 0) { EndJobWith(JobCondition.Incompletable); return; }
                int take = Math.Min(Math.Min(job.count, maxCarry), source.stackCount);
                if (take <= 0) { EndJobWith(JobCondition.Incompletable); return; }

                Thing taken;
                if (take >= source.stackCount)
                {
                    owner.Remove(source);
                    taken = source;
                }
                else
                {
                    taken = source.SplitOff(take);
                }
                if (taken == null) { EndJobWith(JobCondition.Incompletable); return; }

                int carried = actor.carryTracker.TryStartCarry(taken, taken.stackCount, false);
                if (carried < taken.stackCount)
                {
                    // 背不下（或部分背下）→ 剩余退回容器，避免取出后悬空丢失
                    if (!owner.TryAdd(taken, true))
                        GenPlace.TryPlaceThing(taken, actor.Position, actor.Map, ThingPlaceMode.Near);
                    EndJobWith(JobCondition.Incompletable);
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
                Pawn actor = toil.actor;
                Thing carried = actor.carryTracker.CarriedThing;
                if (carried == null) return;

                Thing targetThing = job.GetTarget(TargetIndex.A).Thing;

                // 构造场景：塞容器优先（不落地 → listerHaulables 看不到，#1）
                if (targetThing is IConstructible)
                {
                    ThingOwner container = targetThing.TryGetInnerInteractableThingOwner();
                    if (container != null && container.CanAcceptAnyOf(carried, true))
                    {
                        Thing taken = actor.carryTracker.innerContainer.Take(carried, carried.stackCount);
                        if (taken != null && container.TryAdd(taken, true))
                        {
                            // B2: 不设 forbidden。蓝图 reservation 已保护材料；建造失败时原版退回材料不会带 forbidden。
                            return;
                        }
                        if (taken != null)
                            actor.carryTracker.innerContainer.TryAdd(taken, true);
                    }
                }

                // 非构造场景（右键取料 / 容器放不进）：落地
                IntVec3 dropCell = actor.Position;
                LocalTargetInfo targetA = job.GetTarget(TargetIndex.A);
                if (targetA.HasThing)
                {
                    Thing t = targetA.Thing;
                    dropCell = t.Position;
                    if (t is IBillGiver giver)
                    {
                        foreach (IntVec3 c in giver.IngredientStackCells)
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
                if (actor.carryTracker.TryDropCarriedThing(dropCell, ThingPlaceMode.Near, out Thing dropped, null))
                {
                    if (dropped != null && !(targetThing is IConstructible))
                        dropped.SetForbidden(true, false);
                }
            };
            return toil;
        }
    }

    /// <summary>
    /// 构造材料规划器 —— 从**容器内容物**里挑一种还没凑齐的材料。
    ///
    /// <para>4.0 与 3.0 的差别只在数据源：账本 <c>(def,stuff)+数量</c> → 真实的 Thing。
    /// 数量语义与原版 <c>WorkGiver_ConstructDeliverResources.ResourceDeliverJobFor</c> 对齐
    /// （forced → <c>ThingCountNeeded</c>；否则 <c>IHaulEnroute</c> 扣掉已送达 + enroute）。</para>
    /// </summary>
    public static class ConstructMaterialPlanner
    {
        /// <summary>返回「要取的那件东西 + 取多少」。凑不出材料返回 null。</summary>
        public static (Thing thing, int count)? TryPlan(IConstructible c, Pawn pawn, bool forced, Map map)
        {
            // 安装蓝图（搬移已建成建筑/家具）没有材料账单，原版 TotalMaterialCost()
            // 会主动 Log.Error。必须跳过，交给原版安装流程处理。
            if (c is Blueprint_Install) return null;
            if (map == null) return null;

            List<ThingDefCountClass> materials;
            try
            {
                materials = c.TotalMaterialCost();
            }
            catch (Exception ex)
            {
                // 第三方 IConstructible 的 TotalMaterialCost 可能抛异常；
                // 原版路径由原版兜底，这里只跳过，避免整个 WorkGiver 扫描崩掉。
                Log.WarningOnce("[DigitalStorage] TotalMaterialCost failed for " + c.GetType().Name
                    + ": " + ex.Message, c.GetType().Name.GetHashCode());
                return null;
            }
            if (materials == null || materials.Count == 0) return null;

            for (int mi = 0; mi < materials.Count; mi++)
            {
                ThingDefCountClass need = materials[mi];
                if (need == null || need.thingDef == null || need.count <= 0) continue;

                int remaining;
                if (forced)
                {
                    remaining = c.ThingCountNeeded(need.thingDef);
                }
                else
                {
                    IHaulEnroute enroute = c as IHaulEnroute;
                    remaining = enroute != null
                        ? enroute.GetSpaceRemainingWithEnroute(need.thingDef, pawn)
                        : c.ThingCountNeeded(need.thingDef);
                }
                if (remaining <= 0) continue;

                ThingDef def = need.thingDef;
                // 取同 def 里最大的那一堆：一次能送多少送多少，剩下的由后续 job 接力。
                Thing best = HaulSourceContents.FindBest(map, t => t.stackCount, t => t.def == def);
                if (best == null) continue;

                return (best, Math.Min(remaining, best.stackCount));
            }
            return null;
        }
    }
}
