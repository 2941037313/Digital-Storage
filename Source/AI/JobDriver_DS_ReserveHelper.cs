using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// #3/#5 共享：ReleaseByJob + ReservePlan + PendingPlan 通信 channel。
    /// </summary>
    public static class JobDriver_DS_ReserveHelper
    {
        // ---------- #5: PendingPlan 统一 channel ----------

        private static readonly Dictionary<Job, (ItemKey key, int count)> pendingPlans
            = new Dictionary<Job, (ItemKey, int)>();

        public static void SetPendingPlan(Job job, ItemKey key, int count)
        {
            if (job != null && count > 0)
                pendingPlans[job] = (key, count);
        }

        public static bool TryConsumePendingPlan(Job job, out ItemKey key, out int count)
        {
            if (pendingPlans.TryGetValue(job, out var pp))
            {
                key = pp.key;
                count = pp.count;
                pendingPlans.Remove(job);
                return true;
            }
            key = default;
            count = 0;
            return false;
        }

        // ---------- #3: Release + Reserve ----------

        /// <summary>
        /// 替代 AddFinishAction(_ => TargetCore?.Ledger.ReleaseByJob(job))。
        /// </summary>
        public static void RegisterRelease(JobDriver driver, Building_StorageCore core)
        {
            driver.AddFinishAction(_ => core?.Ledger.ReleaseByJob(driver.job));
        }

        /// <summary>
        /// 构建"预订单个 ItemKey"的 Instant Toil。planCount<=0 或预订不足→Incompletable。
        /// </summary>
        public static Toil MakeReserveToil(JobDriver driver, Building_StorageCore core,
            Core.ItemKey planKey, int planCount)
        {
            var toil = ToilMaker.MakeToil("DS_ReserveLedger");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            var count = planCount; // 捕获值拷贝
            toil.initAction = () =>
            {
                if (core == null || count <= 0)
                {
                    driver.EndJobWith(JobCondition.Incompletable);
                    return;
                }
                int got = core.Ledger.Reserve(driver.job, planKey, count);
                if (got < count)
                    driver.EndJobWith(JobCondition.Incompletable);
            };
            return toil;
        }
    }
}
