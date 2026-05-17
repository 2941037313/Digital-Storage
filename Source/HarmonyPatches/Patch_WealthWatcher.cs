using System.Reflection;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Services;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 让财富计算包含核心账本中的物品价值。
    /// </summary>
    [HarmonyPatch(typeof(WealthWatcher), "CalculateWealthItems")]
    static class Patch_WealthWatcher
    {
        private static readonly FieldInfo mapField = AccessTools.Field(typeof(WealthWatcher), "map");

        static void Postfix(WealthWatcher __instance, ref float __result)
        {
            var map = (Map)mapField.GetValue(__instance);
            if (map == null) return;

            var mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return;

            foreach (var core in mapComp.GetAllCores())
            {
                if (!CoreFinder.IsUsable(core)) continue;
                foreach (var kv in core.Ledger.Stock)
                {
                    if (kv.Value <= 0 || kv.Key.def == null) continue;
                    float marketValue = kv.Key.def.BaseMarketValue;
                    if (kv.Key.stuff != null)
                    {
                        var statReq = StatRequest.For(kv.Key.def, kv.Key.stuff);
                        marketValue = StatDefOf.MarketValue.Worker.GetValueUnfinalized(statReq, true);
                    }
                    __result += marketValue * kv.Value;
                }
            }
        }
    }
}
