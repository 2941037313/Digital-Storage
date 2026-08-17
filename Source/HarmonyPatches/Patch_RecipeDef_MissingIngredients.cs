using System;
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

            List<ThingDef> resultList;
            try
            {
                // 原方法遍历 map.listerThings(ThingsInGroup HaulableEver)。防御:
                // 若其他 mod 把未 Spawn 物品挂进该索引(与本 mod GhostThing 同类的
                // hack),原迭代器可能抛异常——异常会让健康卡手术列表整体消失
                // (社区反馈:医药/植入体"不识别")。异常时按"不缺"处理,保底不崩。
                resultList = __result.ToList();
            }
            catch (Exception e)
            {
                if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                    Log.Warning($"[DS] PotentiallyMissingIngredients 迭代异常,按不缺料处理: {e.Message}");
                __result = new List<ThingDef>();
                return;
            }
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
                        if (kv.Value <= 0 || kv.Key.def == null) continue;
                        // L8: 用可用量（扣预订）而非总库存——与 LedgerBillPlanner.Available()
                        // 口径统一，避免「报不缺药但实际取不到」的手术卡死（I10.03 根因）
                        if (core.Ledger.Available(kv.Key) <= 0) continue;
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
