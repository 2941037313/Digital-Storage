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

            var mapComp = pawn.Map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return null;
            var cores = mapComp.GetAllCores();
            bool chip = Hediff_TerminalImplant.HasTerminalImplant(pawn);

            for (int ci = 0; ci < cores.Count; ci++)
            {
                var core = cores[ci];
                if (core == null || !core.Spawned || core.Destroyed || !core.Powered) continue;

                var ledger = core.Ledger;
                foreach (var kv in ledger.Stock)
                {
                    if (kv.Value <= 0) continue;
                    if (!filter(kv.Key)) continue;
                    long avail = ledger.Available(kv.Key);
                    if (avail <= 0) continue;

                    int limit = avail > int.MaxValue ? int.MaxValue : (int)avail;
                    int take = GetIngestAmount(pawn, kv.Key.def, limit);
                    if (take <= 0) continue;

                    if (chip) return MakeJob(core, IntVec3.Invalid, kv.Key, take);

                    IntVec3 proxy = PickProxyCell(pawn, core);
                    if (proxy.IsValid) return MakeJob(core, proxy, kv.Key, take);
                }
            }
            return null;
        }

        private static Job MakeJob(Building_StorageCore core, IntVec3 proxy, ItemKey key, int count)
        {
            var job = JobMaker.MakeJob(DigitalStorage_JobDefOf.DigitalStorage_ConsumeFromLedger);
            job.SetTarget(TargetIndex.C, core);
            if (proxy.IsValid) job.SetTarget(TargetIndex.B, proxy);
            JobDriver_DS_Consume.SetPendingPlan(job, key, count);
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

        private static IntVec3 PickProxyCell(Pawn pawn, Building_StorageCore core)
        {
            IntVec3 best = IntVec3.Invalid;
            int bestDist = int.MaxValue;
            foreach (var c in core.GetProxyCells())
            {
                if (!c.InBounds(pawn.Map)) continue;
                if (!pawn.CanReach(c, PathEndMode.Touch, Danger.Deadly)) continue;
                int d = (c - pawn.Position).LengthManhattan;
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }
    }
}
