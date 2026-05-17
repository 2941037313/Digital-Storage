using System;
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
    /// 让 ResourceCounter 把核心账本库存也算进去。
    /// Patch UpdateResourceCounts 的 postfix，直接往 countedAmounts 字典里加。
    /// 这样 TotalHumanEdibleNutrition、GetCount、GetCountIn 全部自动生效。
    /// </summary>
    [HarmonyPatch(typeof(ResourceCounter), "UpdateResourceCounts")]
    static class Patch_ResourceCounter_UpdateCounts
    {
        private static readonly FieldInfo mapField = AccessTools.Field(typeof(ResourceCounter), "map");
        private static readonly FieldInfo countedAmountsField = AccessTools.Field(typeof(ResourceCounter), "countedAmounts");

        static void Postfix(ResourceCounter __instance)
        {
            if (Find.WindowStack.WindowOfType<Dialog_Trade>() != null) return;

            var map = (Map)mapField.GetValue(__instance);
            if (map == null) return;

            var countedAmounts = (Dictionary<ThingDef, int>)countedAmountsField.GetValue(__instance);
            if (countedAmounts == null) return;

            var mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return;

            var networkNames = new HashSet<string>();
            var cores = mapComp.GetAllCores();

            for (int i = 0; i < cores.Count; i++)
            {
                var core = cores[i];
                if (!CoreFinder.IsUsable(core)) continue;
                if (!string.IsNullOrEmpty(core.NetworkName)) networkNames.Add(core.NetworkName);
                AddLedgerToCounts(core.Ledger, countedAmounts);
            }

            var gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp != null && networkNames.Count > 0)
            {
                foreach (var core in gameComp.GetAllCores())
                {
                    if (core.Map == map) continue;
                    if (!CoreFinder.IsUsable(core)) continue;
                    if (string.IsNullOrEmpty(core.NetworkName)) continue;
                    if (!networkNames.Contains(core.NetworkName)) continue;
                    AddLedgerToCounts(core.Ledger, countedAmounts);
                }
            }
        }

        private static void AddLedgerToCounts(CoreLedger ledger, Dictionary<ThingDef, int> countedAmounts)
        {
            foreach (var kv in ledger.Stock)
            {
                if (kv.Value <= 0 || kv.Key.def == null) continue;
                if (!kv.Key.def.CountAsResource) continue;
                long add = kv.Value > int.MaxValue ? int.MaxValue : kv.Value;
                if (countedAmounts.ContainsKey(kv.Key.def))
                {
                    long cur = countedAmounts[kv.Key.def];
                    long sum = cur + add;
                    countedAmounts[kv.Key.def] = sum > int.MaxValue ? int.MaxValue : (int)sum;
                }
            }
        }
    }

}
