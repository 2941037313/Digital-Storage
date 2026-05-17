using System.Collections.Generic;
using System.Linq;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(RecipeDef), "PotentiallyMissingIngredients")]
    [HarmonyPatch(new[] { typeof(Pawn), typeof(Map) })]
    static class Patch_RecipeDef_MissingIngredients
    {
        static void Postfix(ref IEnumerable<ThingDef> __result, Map map, RecipeDef __instance)
        {
            if (map == null || __instance?.ingredients == null || __instance.ingredients.Count == 0) return;

            var resultList = __result.ToList();
            if (resultList.Count == 0) return;

            var availableDefs = new HashSet<ThingDef>();
            var mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp != null)
            {
                foreach (var core in mapComp.GetAllCores())
                {
                    if (!CoreFinder.IsUsable(core)) continue;
                    foreach (var kv in core.Ledger.Stock)
                    {
                        if (kv.Value > 0 && kv.Key.def != null)
                            availableDefs.Add(kv.Key.def);
                    }
                }
            }

            if (availableDefs.Count == 0) return;

            var fixedFilter = __instance.fixedIngredientFilter;
            var filtered = new List<ThingDef>();

            foreach (var missingDef in resultList)
            {
                bool stillMissing = true;
                foreach (var ing in __instance.ingredients)
                {
                    if (!ing.filter.Allows(missingDef)) continue;
                    foreach (var availDef in availableDefs)
                    {
                        if (!ing.filter.Allows(availDef)) continue;
                        if (!ing.IsFixedIngredient && fixedFilter != null && !fixedFilter.Allows(availDef)) continue;
                        stillMissing = false;
                        break;
                    }
                    if (!stillMissing) break;
                }
                if (stillMissing) filtered.Add(missingDef);
            }

            __result = filtered;
        }
    }
}
