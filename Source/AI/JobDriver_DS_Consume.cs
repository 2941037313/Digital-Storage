using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using DigitalStorage.HarmonyPatches;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// I1.5：从核心取消耗品 → carryTracker → 原版 ingest toils 处理进嘴。
    /// 芯片 pawn：原地取料 → 走到 chair/spot → 吃。
    /// 无芯片 pawn：走代理点 → 取料 → 走到 chair/spot → 吃。
    /// </summary>
    public class JobDriver_DS_Consume : JobDriver
    {
        private ItemKey planKey;
        private int planCount;
        private bool eatingFromInventory;

        private static readonly Dictionary<Job, (ItemKey key, int count)> pendingPlans =
            new Dictionary<Job, (ItemKey, int)>();

        public static void SetPendingPlan(Job job, ItemKey key, int count)
        {
            if (job != null && count > 0)
                pendingPlans[job] = (key, count);
        }

        public Building_StorageCore TargetCore => job.GetTarget(TargetIndex.C).Thing as Building_StorageCore;

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                string s = planKey.ToSaveString();
                Scribe_Values.Look(ref s, "planKey");
                Scribe_Values.Look(ref planCount, "planCount");
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                string s = null;
                Scribe_Values.Look(ref s, "planKey");
                Scribe_Values.Look(ref planCount, "planCount");
                if (s != null) ItemKey.TryParse(s, out planKey);
            }
        }

        public override void Notify_Starting()
        {
            base.Notify_Starting();
            // Consume 无预订——Withdraw 在同一帧完成，无需防抢料

            if (planCount <= 0 && pendingPlans.TryGetValue(job, out var pp))
            {
                planKey = pp.key;
                planCount = pp.count;
                pendingPlans.Remove(job);
            }
            if (planCount <= 0) { EndJobWith(JobCondition.Incompletable); return; }

            eatingFromInventory = Hediff_TerminalImplant.HasTerminalImplant(pawn);
        }

        public override string GetReport()
        {
            return "DS_ReportConsuming".Translate(planKey.ToString());
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => TargetCore == null || !TargetCore.Spawned || !TargetCore.Powered);

            // 1) 走到代理点（芯片跳过）
            if (!Hediff_TerminalImplant.HasTerminalImplant(pawn) && job.GetTarget(TargetIndex.B).IsValid)
                yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.Touch);

            // 2) 放下手上异物 → 取料 → 走到 chair/spot → 找桌子 → 嚼 → 吞
            yield return DropIncompatibleCarryIfNeeded();
            yield return WithdrawToCarry();

            yield return CarryToChewSpot();
            yield return Toils_Ingest.FindAdjacentEatSurface(TargetIndex.B, TargetIndex.A);

            var chewing = Toils_Ingest.ChewIngestible(pawn, ChewDurationMultiplier, TargetIndex.A, TargetIndex.B)
                .FailOn((Toil x) => !pawn.carryTracker.CarriedThing.IngestibleNow)
                .FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch);
            yield return chewing;
            yield return Toils_Ingest.FinalizeIngest(pawn, TargetIndex.A);
        }

        // ---------- toil builders ----------

        private Toil DropIncompatibleCarryIfNeeded()
        {
            var toil = ToilMaker.MakeToil("DS_DropIncompatible");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = () =>
            {
                var carried = pawn.carryTracker.CarriedThing;
                if (carried != null && carried.def != planKey.def)
                    pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
            };
            return toil;
        }

        private Toil WithdrawToCarry()
        {
            var toil = ToilMaker.MakeToil("DS_WithdrawToCarry");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = () =>
            {
                var core = TargetCore;
                if (core == null) { Fail(); return; }

                int maxCarry = pawn.carryTracker.AvailableStackSpace(planKey.def);
                if (maxCarry <= 0) { Fail(); return; }
                int take = Math.Min(planCount, Math.Min(maxCarry, planKey.def.stackLimit));

                var spawned = core.Ledger.Withdraw(planKey, take, job);
                if (spawned == null) { Fail(); return; }

                int taken = pawn.carryTracker.TryStartCarry(spawned, spawned.stackCount, false);
                if (taken <= 0)
                {
                    core.Ledger.AddRaw(planKey, spawned.stackCount);
                    spawned.Destroy(DestroyMode.Vanish);
                    Fail();
                    return;
                }
                if (taken < spawned.stackCount)
                {
                    spawned.stackCount -= taken;
                    core.Ledger.AddRaw(planKey, spawned.stackCount);
                    spawned.Destroy(DestroyMode.Vanish);
                }
                job.SetTarget(TargetIndex.A, pawn.carryTracker.CarriedThing);
            };
            return toil;
        }

        private Toil CarryToChewSpot()
        {
            var toil = ToilMaker.MakeToil("DS_CarryToChewSpot");
            toil.initAction = () =>
            {
                var carried = pawn.carryTracker.CarriedThing;
                if (carried == null) { Fail(); return; }

                IntVec3 spot;
                if (Toils_Ingest.TryFindChairOrSpot(pawn, carried, out spot))
                {
                    pawn.ReserveSittableOrSpot(spot, job, true);
                    pawn.Map.pawnDestinationReservationManager.Reserve(pawn, job, spot);
                    pawn.pather.StartPath(spot, PathEndMode.OnCell);
                    return;
                }
                // 无 chair/spot → 原地吃（pawn 留在当前位置）
                pawn.pather.StartPath(pawn.Position, PathEndMode.OnCell);
            };
            toil.defaultCompleteMode = ToilCompleteMode.PatherArrival;
            return toil;
        }

        private float ChewDurationMultiplier
        {
            get
            {
                var def = planKey.def;
                if (def?.ingestible != null && !def.ingestible.useEatingSpeedStat) return 1f;
                return 1f / pawn.GetStatValue(StatDefOf.EatingSpeed, true, -1);
            }
        }

        private void Fail()
        {
            HarmonyPatches.ConsumePatchUtil.NotifyFail(pawn);
            EndJobWith(JobCondition.Incompletable);
        }
    }
}
