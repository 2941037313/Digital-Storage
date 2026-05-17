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
    /// I1.5 共用：给定 pawn 的需求（thingDef 或 filter），扫核心账本→找到匹配 key→创 Job。
    /// 所有消费类 JobGiver 的 patch 都走这个入口。
    /// 返回 null = 账本无可用的 → patch 不干扰原版 null 行为。
    /// </summary>
    public static class ConsumptionHelper
    {
        /// <summary>
        /// 找某个特定 ThingDef。drug/medicine 等精确匹配用。
        /// </summary>
        public static Job TryCreateJob(Pawn pawn, ThingDef desiredDef)
        {
            return TryCreateJob(pawn, key => key.def == desiredDef);
        }

        /// <summary>
        /// 用自定义 filter 找。食物用（任何可食即可）。
        /// </summary>
        public static Job TryCreateJob(Pawn pawn, Func<ItemKey, bool> filter)
        {
            if (pawn?.Map == null) return null;
            bool chip = Hediff_TerminalImplant.HasTerminalImplant(pawn);

            // 收集所有候选，按 FoodOptimality 评分降序
            var candidates = new List<(CoreAccess access, ItemKey key, long avail)>();

            foreach (var access in CoreFinder.AllUsableAccesses(pawn))
            {
                var ledger = access.ledgerCore.Ledger;
                foreach (var kv in ledger.Stock)
                {
                    if (kv.Value <= 0) continue;
                    if (!filter(kv.Key)) continue;
                    long avail = ledger.Available(kv.Key);
                    if (avail <= 0) continue;
                    candidates.Add((access, kv.Key, avail));
                }
            }

            // 按 FoodOptimality 降序排列
            candidates.Sort((a, b) => FoodScoring.Score(pawn, b.key.def).CompareTo(FoodScoring.Score(pawn, a.key.def)));

            foreach (var (access, key, avail) in candidates)
            {
                int limit = avail > int.MaxValue ? int.MaxValue : (int)avail;
                int take = GetIngestAmount(pawn, key.def, limit);
                if (take <= 0) continue;

                if (chip) return MakeJob(access.ledgerCore, IntVec3.Invalid, key, take);

                IntVec3 proxy = CoreFinder.PickProxyCell(pawn, access.proxyCore);
                if (proxy.IsValid) return MakeJob(access.ledgerCore, proxy, key, take);
            }
            return null;
        }

        private static Job MakeJob(Building_StorageCore core, IntVec3 proxy, ItemKey key, int count)
        {
            var job = JobMaker.MakeJob(DigitalStorage_JobDefOf.DigitalStorage_ConsumeFromLedger);
            job.SetTarget(TargetIndex.C, core);
            if (proxy.IsValid) job.SetTarget(TargetIndex.B, proxy);
            JobDriver_DS_ReserveHelper.SetPendingPlan(job, key, count);
            return job;
        }

        /// <summary>
        /// 按吃饭/吃药的需求算一次取多少，不贪心拿满。
        /// </summary>
        private static int GetIngestAmount(Pawn pawn, ThingDef def, int maxAvailable)
        {
            if (def.ingestible == null) return 0;

            int take;
            // 食物：按饥饿度算需要多少
            if (def.IsNutritionGivingIngestible && pawn.needs?.food != null)
            {
                float nutritionPerItem = def.GetStatValueAbstract(StatDefOf.Nutrition);
                take = FoodUtility.StackCountForNutrition(pawn.needs.food.NutritionWanted, nutritionPerItem);
                if (def.ingestible.maxNumToIngestAtOnce > 0)
                    take = Math.Min(take, def.ingestible.maxNumToIngestAtOnce);
            }
            else
            {
                take = def.ingestible.defaultNumToIngestAtOnce;
                if (take <= 0) take = 1;
            }
            return Math.Min(take, maxAvailable);
        }

    }
}
