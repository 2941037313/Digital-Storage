using System;
using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// G4: 喂食病人/囚犯时，食物从账本取。
    /// FoodUtility.TryFindBestFoodSourceFor 只在 map+背包搜索食物，
    /// 此 Postfix 在找不到时查账本 → Withdraw → spawn 脚下。
    /// 按 FoodPreferability 降序选择最佳食物。
    /// </summary>
    [HarmonyPatch(typeof(FoodUtility), "TryFindBestFoodSourceFor")]
    static class Patch_FeedFromLedger
    {
        static void Postfix(Pawn getter, Pawn eater, ref Thing foodSource, ref ThingDef foodDef, ref bool __result)
        {
            if (foodSource is Ghost.GhostThing) { foodSource = null; __result = false; }
            if (__result || foodSource != null) return;
            if (getter?.Map == null || eater == null) return;

            // 芯片持有者 → 隔空取食。看护喂食 → 不需芯片。
            // 8.1 bugfix(喂食覆盖不全):对齐原版 FeedPatientUtility.ShouldBeFed 的
            // 范围——狱警送饭(囚犯,含站立)或 喂食无法行动者(殖民者/奴隶/访客/
            // 囚犯/躺玩家床的动物)。旧实现只认 IsColonist/IsPrisonerOfColony,
            // 漏了奴隶(Faction=玩家但 IsColonist=false)、殖民地动物、访客
            // (HostFaction=玩家)——这些倒地后吃不到核心食物。
            bool isCaregiving = getter != eater
                && (eater.IsPrisonerOfColony || FeedPatientUtility.ShouldBeFed(eater));
            if (!Hediff_TerminalImplant.HasTerminalImplant(getter) && !isCaregiving) return;

            var accesses = CoreFinder.AllUsableAccesses(getter);
            if (accesses.Count == 0) return;

            ThingFilter foodFilter = null;
            if (eater.foodRestriction != null)
            {
                var policy = eater.foodRestriction.GetCurrentRespectedRestriction(getter);
                if (policy != null) foodFilter = policy.filter;
            }

            // 收集候选食物
            var candidates = new List<(CoreAccess access, ItemKey key, long avail)>();
            foreach (var access in accesses)
            {
                foreach (var kv in access.ledgerCore.Ledger.Stock)
                {
                    if (kv.Value <= 0 || kv.Key.def == null) continue;
                    var def = kv.Key.def;
                    if (!def.IsNutritionGivingIngestible || !def.IsIngestible) continue;
                    if (foodFilter != null && !foodFilter.Allows(def)) continue;
                    if (!eater.RaceProps.Eats(def.ingestible.foodType)) continue;

                    long avail = access.ledgerCore.Ledger.Available(kv.Key);
                    if (avail <= 0) continue;
                    candidates.Add((access, kv.Key, avail));
                }
            }

            // 按 FoodOptimality 降序
            candidates.Sort((a, b) => FoodScoring.Score(eater, b.key.def).CompareTo(FoodScoring.Score(eater, a.key.def)));

            foreach (var (access, key, avail) in candidates)
            {
                int take = CalculateFeedAmount(eater, key.def, (int)Math.Min(avail, 75));
                if (take <= 0) continue;

                var thing = access.ledgerCore.Ledger.Withdraw(key, take);
                if (thing == null) continue;

                if (GenPlace.TryPlaceThing(thing, getter.Position, getter.Map, ThingPlaceMode.Near, null, null, default))
                {
                    CompAutoIngest.MarkWithdrawn(thing);
                    foodSource = thing;
                    foodDef = key.def;
                    __result = true;
                    return;
                }
                access.ledgerCore.Ledger.AddRaw(key, take);
                thing.Destroy(DestroyMode.Vanish);
            }
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
