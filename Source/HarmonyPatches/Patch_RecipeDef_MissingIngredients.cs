using System;
using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Core;
using HarmonyLib;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 让手术/建造/任务生成的"缺料"判定看得见容器内容物。
    ///
    /// <para>原版 <c>RecipeDef.PotentiallyMissingIngredients</c> 只遍历
    /// <c>map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)</c>
    /// —— 容器内容物未 Spawned，不在里面。</para>
    ///
    /// <para>调用方：<c>HealthCardUtility:278</c>（手术列表的「缺少 X」提示）、
    /// <c>QuestNode_GetThingPlayerCanProduce</c>、<c>PlayerItemAccessibilityUtility</c>。
    /// 症状：容器里明明有草药，手术却显示「麻醉（缺少草药）」。</para>
    /// </summary>
    [HarmonyPatch(typeof(RecipeDef), "PotentiallyMissingIngredients")]
    [HarmonyPatch(new[] { typeof(Pawn), typeof(Map) })]
    static class Patch_RecipeDef_MissingIngredients
    {
        private static readonly List<Thing> tmpThings = new List<Thing>();

        static void Postfix(ref IEnumerable<ThingDef> __result, Map map, RecipeDef __instance)
        {
            if (map == null || __instance?.ingredients == null || __instance.ingredients.Count == 0) return;

            List<ThingDef> resultList;
            try
            {
                // 原方法遍历 map.listerThings。防御：若其他 mod 把未 Spawn 物品挂进该索引
                // （与本 mod GhostThing 同类的 hack），原迭代器可能抛异常 ——
                // 异常会让健康卡手术列表整体消失（社区反馈：医药/植入体"不识别"）。
                // 异常时按"不缺"处理，保底不崩。
                resultList = __result.ToList();
            }
            catch (Exception e)
            {
                if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                    Log.Warning("[DS] PotentiallyMissingIngredients 迭代异常,按不缺料处理: " + e.Message);
                __result = new List<ThingDef>();
                return;
            }
            if (resultList.Count == 0) return;

            // 4.0：可用料 = 容器内容物，而不是账本库存。
            var availableDefs = new HashSet<ThingDef>();
            HaulSourceContents.GatherAll(map, tmpThings);
            for (int i = 0; i < tmpThings.Count; i++)
            {
                Thing t = tmpThings[i];
                if (t?.def == null) continue;
                // 保留 L8：用「可用量」（扣掉已被预订的）而非总量 ——
                // 避免「报不缺药但实际取不到」导致的手术卡死（I10.03 根因）。
                if (map.reservationManager.IsReserved(t)) continue;
                availableDefs.Add(t.def);
            }
            tmpThings.Clear();

            if (availableDefs.Count == 0) return;

            ThingFilter fixedFilter = __instance.fixedIngredientFilter;
            var filtered = new List<ThingDef>();

            foreach (ThingDef missingDef in resultList)
            {
                bool stillMissing = true;
                foreach (IngredientCount ing in __instance.ingredients)
                {
                    if (!ing.filter.Allows(missingDef)) continue;
                    foreach (ThingDef availDef in availableDefs)
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
