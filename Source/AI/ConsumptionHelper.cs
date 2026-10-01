using System;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 消费类 JobGiver 的共用入口：给定 pawn 的需求，在容器里找一件匹配的**真实 Thing**，创 job。
    /// 返回 null = 容器里没有可用的 → patch 不干扰原版的 null 行为。
    ///
    /// <para><b>4.0 与 3.0 的差别</b>：3.0 扫的是账本 <c>(def, stuff) → 数量</c>，
    /// 取料要"扣账 + spawn"；4.0 直接拿着那件真实的 Thing，取出即可。
    /// 因此不再需要 <c>ItemKey</c>、不再需要在 job 上挂"计划"（见
    /// <c>JobDriver_DS_Consume</c>：Thing 就放在 <c>job.targetA</c>）。</para>
    /// </summary>
    public static class ConsumptionHelper
    {
        /// <summary>找某个特定 ThingDef（drug 等精确匹配用）。</summary>
        public static Job TryCreateJob(Pawn pawn, ThingDef desiredDef)
        {
            return TryCreateJob(pawn, t => t.def == desiredDef);
        }

        /// <summary>用自定义 predicate 找（食物用：任何可食即可）。</summary>
        public static Job TryCreateJob(Pawn pawn, Predicate<Thing> filter)
        {
            if (pawn?.Map == null || filter == null) return null;

            // 8.1 bugfix: 机械体不消费任何 ingestible（无食物/药物/娱乐需求），
            // 统一排除 —— 所有消费 patch 都走这个入口，防止机械体从核心吃食物。
            if (pawn.RaceProps.IsMechanoid) return null;

            bool chip = Hediff_TerminalImplant.HasTerminalImplant(pawn);
            // 设置「需要终端芯片」：没有芯片就没有任何访问入口（对齐 3.0 反馈
            // 「科技没点、也没装部件，却能远程取物吃」）。默认关闭保持 v3 原设计。
            if (!chip && DigitalStorage.Settings.DigitalStorageSettings.requireChipForCoreAccess)
                return null;

            // 社区反馈「食物方案禁止吃虫胶也没用」：原版 JobGiver_GetFood →
            // FoodUtility.TryFindBestFoodSourceFor 会对每个候选调 FoodUtility.WillEat
            // （FoodIsSuitable + FoodPolicy.Allows + 泰特托 + 圣兽肉 + 头衔），
            // 本 mod 直连容器绕过了这一层，这里补上同一套 gate。
            bool allowDrug = !pawn.IsTeetotaler();
            Map map = pawn.Map;
            ReservationManager resMgr = map.reservationManager;

            Thing best = HaulSourceContents.FindBest(
                map,
                t => FoodScoring.Score(pawn, t.def),
                t =>
                {
                    if (!filter(t)) return false;
                    ThingDef def = t.def;
                    if (def == null) return false;

                    if (def.IsNutritionGivingIngestible)
                    {
                        if (!pawn.WillEat(def, pawn, careIfNotAcceptableForTitle: true)) return false;
                    }
                    else if (def.IsDrug)
                    {
                        // 成瘾品 / 娱乐性药物：不贪食者与变体限制（对齐原版 JobGiver_GetFood 的 allowDrug）
                        if (!allowDrug) return false;
                        if (!pawn.DrugIsSuitable(def)) return false;
                    }

                    // 已被别人预订的不要选（原版 GenClosest 也会做这个检查）
                    if (resMgr != null && resMgr.IsReserved(t)) return false;
                    return true;
                });

            if (best == null) return null;

            int take = GetIngestAmount(pawn, best.def, best.stackCount);
            if (take <= 0) return null;

            var job = JobMaker.MakeJob(DigitalStorage_JobDefOf.DigitalStorage_ConsumeFromLedger);
            // targetA = 要吃/要用的那件真实东西（会被 Scribe，存档读档不丢）
            job.SetTarget(TargetIndex.A, best);
            // targetC = 它所在的容器（用于 FailOn 的电力/存在性检查）
            Thing holder = best.ParentHolder as Thing;
            if (holder != null) job.SetTarget(TargetIndex.C, holder);
            job.count = take;
            return job;
        }

        /// <summary>按吃饭/吃药的需求算一次取多少，不贪心拿满。</summary>
        private static int GetIngestAmount(Pawn pawn, ThingDef def, int maxAvailable)
        {
            if (def.ingestible == null) return 0;

            int take;
            if (def.IsNutritionGivingIngestible && pawn.needs?.food != null)
            {
                // 食物：按饥饿度算需要多少
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
