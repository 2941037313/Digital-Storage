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
    /// B3: Right-click workbench menu → ingredient availability check.
    /// Vanilla RecipeDef.PotentiallyMissingIngredients only scans map lister.
    /// This postfix removes items that exist in the ledger from the "missing" list.
    /// </summary>
    [HarmonyPatch(typeof(RecipeDef), "PotentiallyMissingIngredients")]
    [HarmonyPatch(new[] { typeof(Pawn), typeof(Map) })]
    static class Patch_RecipeDef_MissingIngredients
    {
        static void Postfix(ref IEnumerable<ThingDef> __result, Map map)
        {
            if (map == null) return;

            var mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return;

            var gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            var allCores = new HashSet<Building_StorageCore>();

            foreach (var c in mapComp.GetAllCores())
                if (CoreFinder.IsUsable(c)) allCores.Add(c);

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

            // Collect all ThingDefs available in any ledger
            var availableDefs = new HashSet<ThingDef>();
            foreach (var core in allCores)
            {
                foreach (var kv in core.Ledger.Stock)
                {
                    if (kv.Value > 0 && kv.Key.def != null)
                        availableDefs.Add(kv.Key.def);
                }
            }

            if (availableDefs.Count > 0)
                __result = __result.Where(def => !availableDefs.Contains(def));
        }
    }
}
