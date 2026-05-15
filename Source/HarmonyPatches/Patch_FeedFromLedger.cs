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
    /// G4: 喂食病人/囚犯时，食物从账本取。
    /// FoodUtility.TryFindBestFoodSourceFor 只在 map+背包搜索食物，
    /// 此 Postfix 在找不到时查账本 → Withdraw → spawn 脚下。
    /// 覆盖 FeedPatient + Warden_Feed + 任何其他调用方。
    /// </summary>
    [HarmonyPatch(typeof(FoodUtility), "TryFindBestFoodSourceFor")]
    static class Patch_FeedFromLedger
    {
        static void Postfix(Pawn getter, Pawn eater, ref Thing foodSource, ref ThingDef foodDef, ref bool __result)
        {
            if (__result || foodSource != null) return;
            if (getter?.Map == null || eater == null) return;

            var accesses = CoreFinder.AllUsableAccesses(getter);
            if (accesses.Count == 0) return;

            // 检查食物限制
            ThingFilter foodFilter = null;
            if (eater.foodRestriction != null)
            {
                var policy = eater.foodRestriction.GetCurrentRespectedRestriction(getter);
                if (policy != null) foodFilter = policy.filter;
            }

            foreach (var access in accesses)
            {
                foreach (var kv in access.ledgerCore.Ledger.Stock)
                {
                    if (kv.Value <= 0 || kv.Key.def == null) continue;
                    var def = kv.Key.def;
                    if (!def.IsNutritionGivingIngestible || !def.IsIngestible) continue;
                    if (foodFilter != null && !foodFilter.Allows(def)) continue;

                    long avail = access.ledgerCore.Ledger.Available(kv.Key);
                    if (avail <= 0) continue;

                    int take = (int)Math.Min(avail, (long)int.MaxValue);
                    var thing = access.ledgerCore.Ledger.Withdraw(kv.Key, take);
                    if (thing == null) continue;

                    if (GenPlace.TryPlaceThing(thing, getter.Position, getter.Map, ThingPlaceMode.Near, null, null, default))
                    {
                        foodSource = thing;
                        foodDef = def;
                        __result = true;
                        return;
                    }
                    access.ledgerCore.Ledger.AddRaw(kv.Key, take);
                    thing.Destroy(DestroyMode.Vanish);
                }
            }
        }
    }
}
