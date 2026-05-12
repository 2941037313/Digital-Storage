using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 阶段 4.1：进核心方向的 JobDriver。
    /// targetA = 地上物品；targetB = 最近代理点（接口或核心交互格），芯片 pawn 为 Invalid 跳过走位。
    /// targetThingC = 目标核心，Cleanup 时释放预订（本步骤 Ingest 不开预订，Hook 预留给后续共用）。
    /// </summary>
    public class JobDriver_DS_Ingest : JobDriver
    {
        private const TargetIndex HaulableInd = TargetIndex.A;
        private const TargetIndex ProxyInd = TargetIndex.B;
        private const TargetIndex CoreInd = TargetIndex.C;

        public Thing ToHaul => job.GetTarget(HaulableInd).Thing;
        public Building_StorageCore TargetCore => job.GetTarget(CoreInd).Thing as Building_StorageCore;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(HaulableInd), job, 1, -1, null, errorOnFailed);
        }

        public override string GetReport()
        {
            var t = ToHaul ?? pawn.carryTracker?.CarriedThing;
            var core = TargetCore;
            if (t == null) return "ReportHaulingUnknown".Translate();
            if (core != null)
            {
                return "DS_ReportHaulingToCore".Translate(t.Label, core.NetworkName.Named("NETWORK"), t.Named("THING"));
            }
            return "ReportHauling".Translate(t.Label, t);
        }

        public override void Notify_Starting()
        {
            base.Notify_Starting();
            // 工单结束（无论什么原因）统一释放账本预订
            AddFinishAction(_ => TargetCore?.Ledger.ReleaseByJob(job));
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(HaulableInd);
            this.FailOn(() => TargetCore == null || !TargetCore.Spawned || !TargetCore.Powered);

            // 1) 走到物品
            yield return Toils_Goto.GotoThing(HaulableInd, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(HaulableInd)
                .FailOnSomeonePhysicallyInteracting(HaulableInd);

            // 2) 抬起
            yield return Toils_Haul.StartCarryThing(HaulableInd, false, false, false, true);

            // 3) 走到代理点（芯片 pawn 跳过）
            if (!Hediff_TerminalImplant.HasTerminalImplant(pawn))
            {
                yield return Toils_Goto.GotoCell(ProxyInd, PathEndMode.Touch);
            }

            // 4) 把手上物品塞进账本
            yield return MakeIngestToil();
        }

        private Toil MakeIngestToil()
        {
            Toil toil = ToilMaker.MakeToil("DS_IngestToLedger");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = () =>
            {
                var actor = toil.actor;
                var carried = actor.carryTracker?.CarriedThing;
                var core = TargetCore;
                if (carried == null || core == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                int capacity = core.GetCapacity();
                if (!core.Ledger.CanAccept(carried, capacity))
                {
                    // 容量路上满了 → 落地让原版 haul 走
                    actor.carryTracker.TryDropCarriedThing(actor.Position, ThingPlaceMode.Near, out _);
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                // 从 carryTracker 里拿出 Thing（不落地），直接交账本吃掉
                var taken = actor.carryTracker.innerContainer.Take(carried, carried.stackCount);
                if (taken == null || !core.Ledger.Ingest(taken, capacity))
                {
                    // 吃不下就丢地上保底
                    if (taken != null && !taken.Destroyed)
                    {
                        GenPlace.TryPlaceThing(taken, actor.Position, actor.Map, ThingPlaceMode.Near);
                    }
                    EndJobWith(JobCondition.Incompletable);
                }
            };
            return toil;
        }
    }
}
