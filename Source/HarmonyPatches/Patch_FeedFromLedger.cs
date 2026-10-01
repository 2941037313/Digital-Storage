using System;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// G4: 喂食病人/囚犯时，食物从容器取。
    ///
    /// 原版 <c>FoodUtility.TryFindBestFoodSourceFor</c> 只在 map + 背包里搜食物 →
    /// 此 Postfix 在找不到时扫容器 → 取出放到喂食者脚下。
    /// 按 <c>FoodScoring.Score</c> 降序选最优食物。
    /// </summary>
    [HarmonyPatch(typeof(FoodUtility), "TryFindBestFoodSourceFor")]
    static class Patch_FeedFromLedger
    {
        static void Postfix(Pawn getter, Pawn eater, ref Thing foodSource, ref ThingDef foodDef, ref bool __result)
        {
            if (__result || foodSource != null) return;
            if (getter?.Map == null || eater == null) return;

            // 8.1 bugfix(喂食覆盖不全): 对齐原版 FeedPatientUtility.ShouldBeFed 的范围 ——
            // 狱警送饭(囚犯,含站立) 或 喂食无法行动者(殖民者/奴隶/访客/囚犯/躺玩家床的动物)。
            // 旧实现只认 IsColonist/IsPrisonerOfColony，漏了奴隶(Faction=玩家但 IsColonist=false)、
            // 殖民地动物、访客(HostFaction=玩家) —— 这些倒地后吃不到核心食物。
            bool isCaregiving = getter != eater
                && (eater.IsPrisonerOfColony || FeedPatientUtility.ShouldBeFed(eater));
            if (!Hediff_TerminalImplant.HasTerminalImplant(getter) && !isCaregiving) return;

            ThingFilter foodFilter = null;
            if (eater.foodRestriction != null)
            {
                var policy = eater.foodRestriction.GetCurrentRespectedRestriction(getter);
                if (policy != null) foodFilter = policy.filter;
            }
            ThingFilter capturedFilter = foodFilter;

            Thing best = HaulSourceContents.FindBest(
                getter.Map,
                t => FoodScoring.Score(eater, t.def),
                t => t.def.IsNutritionGivingIngestible && t.def.IsIngestible
                     && (capturedFilter == null || capturedFilter.Allows(t.def))
                     && eater.RaceProps.Eats(t.def.ingestible.foodType));

            if (best == null) return;

            int take = CalculateFeedAmount(eater, best.def, best.stackCount);
            if (take <= 0) return;

            Thing taken = HaulSourceContents.ExtractToFeet(best, take, getter);
            if (taken == null) return;

            foodSource = taken;
            foodDef = taken.def;
            __result = true;
        }

        private static int CalculateFeedAmount(Pawn eater, ThingDef def, int maxAvailable)
        {
            float nutritionWanted = eater.needs?.food != null
                ? eater.needs.food.NutritionWanted
                : 1f;
            float nutritionPerItem = def.GetStatValueAbstract(StatDefOf.Nutrition);
            if (nutritionPerItem <= 0f) return 1;
            int needed = (int)Math.Ceiling(nutritionWanted / nutritionPerItem);
            if (needed <= 0) needed = 1;
            if (def.ingestible.maxNumToIngestAtOnce > 0)
                needed = Math.Min(needed, def.ingestible.maxNumToIngestAtOnce);
            return Math.Min(needed, maxAvailable);
        }
    }
}
