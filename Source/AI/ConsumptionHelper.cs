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
            // 8.1 bugfix:机械体不消费任何 ingestible(无食物/药物/娱乐需求),
            // 统一排除——所有消费 patch 都走这个入口,防止机械体从核心吃食物
            if (pawn.RaceProps.IsMechanoid) return null;
            bool chip = Hediff_TerminalImplant.HasTerminalImplant(pawn);

            // 设置「需要终端芯片」：没有芯片就没有任何访问入口（对齐 3.0 反馈
            // 「科技没点、也没装部件，却能远程取物吃」）。默认关闭保持 v3 原设计。
            if (!chip && DigitalStorage.Settings.DigitalStorageSettings.requireChipForCoreAccess)
                return null;

            // 社区反馈「食物方案禁止吃虫胶也没用」：
            // 原版 JobGiver_GetFood → FoodUtility.TryFindBestFoodSourceFor 会对每个候选调用
            // FoodUtility.WillEat（FoodIsSuitable + FoodPolicy.Allows + 泰特托 + 圣兽肉 + 头衔），
            // 本 mod 直连账本绕过了这一层。这里补上同一套 gate。
            bool allowDrug = !pawn.IsTeetotaler();

            // 收集所有候选，按 FoodOptimality 评分降序
            var candidates = new List<(CoreAccess access, ItemKey key, long avail)>();

            foreach (var access in CoreFinder.AllUsableAccesses(pawn))
            {
                var ledger = access.ledgerCore.Ledger;
                foreach (var kv in ledger.Stock)
                {
                    if (kv.Value <= 0) continue;
                    if (kv.Key.def == null) continue;
                    if (!filter(kv.Key)) continue;

                    var def = kv.Key.def;
                    // 食物：营养可食 + 食物方案/可食性/圣兽肉/头衔 四重校验（与原版同口径）
                    if (def.IsNutritionGivingIngestible)
                    {
                        if (!pawn.WillEat(def, pawn, careIfNotAcceptableForTitle: true)) continue;
                    }
                    else if (def.IsDrug)
                    {
                        // 成瘾品 / 娱乐性药物：不贪食者与变体限制（对齐原版 JobGiver_GetFood 的 allowDrug）
                        if (!allowDrug) continue;
                        if (!pawn.DrugIsSuitable(def)) continue;
                    }

                    long avail = ledger.Available(kv.Key);
                    if (avail <= 0) continue;
                    candidates.Add((access, kv.Key, avail));
                }
            }

            // 按 FoodOptimality 降序排列（药物走 FoodScoring 会因 preferability 得低分，排到最后）
            candidates.Sort((a, b) =>
                FoodScoring.Score(pawn, b.key.def)
                    .CompareTo(FoodScoring.Score(pawn, a.key.def)));

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
