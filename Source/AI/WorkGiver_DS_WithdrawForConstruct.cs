using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 阶段 4.3：扫蓝图 / Frame → 容器里有没有对应材料 → 有就派 DigitalStorage_WithdrawToConstruction。
    /// priorityInType=200（高于 DeliverResourcesToFrames=10 和 ToBlueprints=9）。
    /// 容器凑不齐 → 返 null，原版 ConstructDeliverResources 接力。
    ///
    /// 材料判定统一委托给 DSConstructionDelivery（与 ResourceDeliverJobFor 的 Postfix 共享）：
    /// 安装蓝图、forced 语义、第三方 IConstructible 的异常防护都在那里处理。
    /// </summary>
    public class WorkGiver_DS_WithdrawForConstruct : WorkGiver_Scanner
    {
        public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;
        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override ThingRequest PotentialWorkThingRequest =>
            ThingRequest.ForGroup(ThingRequestGroup.BuildingFrame);

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            foreach (Thing t in pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
                yield return t;
            foreach (Thing t in pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint))
                yield return t;
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            // 没有可用（已通电）的核心就别扫了。核心通电是 4.0 保留的唯一门。
            // 跨图：本图没有也算 —— 材料可能全在另一张图的核心里（4.0 第三阶段）。
            return !CoreFinder.AnyUsableCoreGlobal(pawn);
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t.Faction != pawn.Faction) return false;
            IConstructible constructible = t as IConstructible;
            if (constructible == null) return false;
            if (!(t is Frame) && !(t is Blueprint)) return false;
            if (t is Blueprint_Install) return false; // 安装蓝图由原版安装流程处理
            if (!pawn.CanReserve(t, 1, -1, null, forced)) return false;
            if (!GenConstruct.CanConstruct(t, pawn, def.workType, forced, DigitalStorage_JobDefOf.DigitalStorage_WithdrawToConstruction))
                return false;

            return DSConstructionDelivery.CanMakeJob(pawn, constructible, forced);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            IConstructible constructible = t as IConstructible;
            if (constructible == null) return null;
            if (!DSConstructionDelivery.TryMakeJob(pawn, constructible, forced, out Job job)) return null;
            return job;
        }
    }

    /// <summary>
    /// 建造取料 job 的统一构造入口。
    /// - WorkGiver_DS_WithdrawForConstruct（Hauling priorityInType=200，容器优先路径）
    /// - Patch_ResourceDeliverJobFor（原版 ResourceDeliverJobFor 的 fallback 路径：Achtung 强制、
    ///   Construction work、第三方 WorkGiver 子类等）
    /// 共用同一套 install / forced / 异常防护逻辑，避免再出现"复刻原版材料判定漏分支"的 bug。
    ///
    /// <para><b>4.0</b>：不再有芯片 / 代理点分支（纯轮椅）。job 直接带上"要取的那件 Thing"
    /// （targetB）与容器（targetC），取料由 <see cref="JobDriver_DS_Withdraw"/> 完成。</para>
    /// </summary>
    public static class DSConstructionDelivery
    {
        /// <summary>轻量检查：当前 pawn 能否为 c 生成一个容器取料 job（不构建 Job 对象）。</summary>
        public static bool CanMakeJob(Pawn pawn, IConstructible c, bool forced)
        {
            return PlanFor(pawn, c, forced) != null;
        }

        /// <summary>
        /// 构造 DigitalStorage_WithdrawToConstruction：
        /// targetA = 蓝图 / Frame，targetB = 要取的 Thing，targetC = 它所在的容器。
        /// 返回 false 时交回原版流程。
        /// </summary>
        public static bool TryMakeJob(Pawn pawn, IConstructible c, bool forced, out Job job)
        {
            job = null;
            var plan = PlanFor(pawn, c, forced);
            if (plan == null) return false;

            Thing target = c as Thing;
            if (target == null) return false;

            Thing src = plan.Value.thing;
            job = JobMaker.MakeJob(DigitalStorage_JobDefOf.DigitalStorage_WithdrawToConstruction, target);
            job.SetTarget(TargetIndex.B, src);
            job.SetTarget(TargetIndex.C, src.ParentHolder as Thing);
            job.count = plan.Value.count;
            return true;
        }

        private static (Thing thing, int count)? PlanFor(Pawn pawn, IConstructible c, bool forced)
        {
            if (!IsValidTarget(pawn, c)) return null;
            // 跨图：材料可以来自**任意图**上通电的核心（本图优先，见 ConstructMaterialPlanner）
            if (!CoreFinder.AnyUsableCoreGlobal(pawn)) return null;
            return ConstructMaterialPlanner.TryPlan(c, pawn, forced, pawn.Map);
        }

        private static bool IsValidTarget(Pawn pawn, IConstructible c)
        {
            if (pawn == null || pawn.Map == null || !pawn.Spawned) return false;
            if (c == null) return false;
            if (c is Blueprint_Install) return false; // 安装蓝图没有材料账单，原版 TotalMaterialCost() 会 Log.Error

            Thing t = c as Thing;
            if (t == null || t.Destroyed || !t.Spawned) return false;
            if (t.Map != pawn.Map) return false; // 只管当前地图：跨图路径交回原版
            if (t.Faction != pawn.Faction) return false;
            return true;
        }
    }
}
