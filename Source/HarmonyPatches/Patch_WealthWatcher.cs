using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
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

        /// <summary>
        /// P4: 每单位市值缓存。账本按 (def, stuff) 归组，同一 key 的单位价值恒定
        /// （账本不收带品质/耐久差异的物品），所以没必要每次财富重算都跑一遍 stat 系统——
        /// `StatWorker_MarketValue.CalculatedBaseMarketValue` 对无 CostList 的 def 会遍历
        /// `DefDatabase&lt;RecipeDef&gt;.AllDefsListForReading`，是真·贵操作。
        /// 值只依赖静态 def，跨存档/跨图都成立，故用静态缓存且无需清理。
        /// </summary>
        private static readonly Dictionary<ItemKey, float> unitValueCache = new Dictionary<ItemKey, float>();

        private static float UnitMarketValue(ItemKey key)
        {
            if (unitValueCache.TryGetValue(key, out float cached)) return cached;

            float value = key.def.BaseMarketValue;
            if (key.stuff != null)
            {
                var statReq = StatRequest.For(key.def, key.stuff);
                value = StatDefOf.MarketValue.Worker.GetValueUnfinalized(statReq, true);
            }
            unitValueCache[key] = value;
            return value;
        }

        static void Postfix(WealthWatcher __instance, ref float __result)
        {
            var map = (Map)mapField.GetValue(__instance);
            if (map == null) return;

            var mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return;

            var cores = mapComp.GetAllCores();
            for (int ci = 0; ci < cores.Count; ci++)
            {
                var core = cores[ci];
                if (!CoreFinder.IsUsable(core)) continue;
                foreach (var kv in core.Ledger.Stock)
                {
                    if (kv.Value <= 0 || kv.Key.def == null) continue;
                    __result += UnitMarketValue(kv.Key) * kv.Value;
                }
            }
        }
    }
}
