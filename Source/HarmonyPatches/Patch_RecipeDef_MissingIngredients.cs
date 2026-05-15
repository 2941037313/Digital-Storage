using System.Collections.Generic;
using System.Linq;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Services;
using HarmonyLib;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// B3 + G1.5: 料理/手术缺少配料检查。原版只扫地图 listerThings；
    /// 此 Postfix 检查账本是否有任何可替代 def，有则从"缺少"列表中移除。
    ///
    /// 关键：不是精确 def 匹配（草药 vs 医药），而是逐 ingredient filter 检查：
    /// ingredient filter 允许{草药,医药,闪耀药} + fixedIngredientFilter 也允许 → 账本有医药 → 草药不算缺。
    /// </summary>
    [HarmonyPatch(typeof(RecipeDef), "PotentiallyMissingIngredients")]
    [HarmonyPatch(new[] { typeof(Pawn), typeof(Map) })]
    static class Patch_RecipeDef_MissingIngredients
    {
        static void Postfix(ref IEnumerable<ThingDef> __result, Map map, RecipeDef __instance)
        {
            if (map == null || __instance?.ingredients == null || __instance.ingredients.Count == 0) return;

            var allCores = new HashSet<Building_StorageCore>();
            var mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp != null)
            {
                foreach (var c in mapComp.GetAllCores())
                    if (CoreFinder.IsUsable(c)) allCores.Add(c);
            }

            var gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp != null)
            {
                foreach (var c in gameComp.GetAllCores())
                {
                    if (c.Map == map) continue;
                    if (!CoreFinder.IsUsable(c)) continue;
                    if (string.IsNullOrEmpty(c.NetworkName)) continue;
                    bool hasPeer = allCores.Any(lc => CoreFinder.IsUsable(lc) && lc.NetworkName == c.NetworkName);
                    if (hasPeer) allCores.Add(c);
                }
            }

            var availableDefs = new HashSet<ThingDef>();
            foreach (var core in allCores)
            {
                foreach (var kv in core.Ledger.Stock)
                {
                    if (kv.Value > 0 && kv.Key.def != null)
                        availableDefs.Add(kv.Key.def);
                }
            }

            if (availableDefs.Count == 0) return;

            var missingList = __result.ToList();
            if (missingList.Count == 0) return;

            var fixedFilter = __instance.fixedIngredientFilter;

            var filtered = missingList.Where(missingDef =>
            {
                foreach (var ing in __instance.ingredients)
                {
                    if (!ing.filter.Allows(missingDef)) continue;
                    foreach (var availDef in availableDefs)
                    {
                        if (!ing.filter.Allows(availDef)) continue;
                        if (!ing.IsFixedIngredient && fixedFilter != null && !fixedFilter.Allows(availDef)) continue;
                        return false;
                    }
                    return true;
                }
                return true;
            }).ToList();

            __result = filtered;
        }
    }
}
