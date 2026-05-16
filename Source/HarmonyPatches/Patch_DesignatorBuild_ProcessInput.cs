using System.Collections.Generic;
using System.Linq;
using DigitalStorage.AI;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// I5b：修复 MadeFromStuff 建筑的换单里不出现纯账本材料 + "缺少材料"误弹。
    /// 策略：prefix 先判断原版会不会空 → 不会就放行；会空且账本有 → 自建 FloatMenu + 跳过原版。
    /// 跨图：远程同 NetworkName 核心的 stuff 也计入。
    /// </summary>
    [HarmonyPatch(typeof(Designator_Build), "ProcessInput")]
    static class Patch_DesignatorBuild_ProcessInput
    {
        static bool Prefix(Designator_Build __instance, Event ev)
        {
            var thingDef = __instance.PlacingDef as ThingDef;
            if (thingDef == null || !thingDef.MadeFromStuff) return true;

            var map = __instance.Map;
            if (map == null) return true;

            // 用 LedgerItemCollector 统一发现本地+跨图核心
            var allCores = Core.LedgerItemCollector.GetAllUsableCores(map);
            if (allCores.Count == 0) return true;

            var mapStuffDefs = new HashSet<ThingDef>();
            var ledgerStuffDefs = new HashSet<ThingDef>();

            foreach (var core in allCores)
                CollectStuffDefs(core, thingDef, ledgerStuffDefs);

            // 地图上已有的 stuff
            foreach (var d in map.resourceCounter.AllCountedAmounts.Keys)
            {
                if (d.IsStuff && d.stuffProps.CanMake(thingDef)
                    && map.listerThings.ThingsOfDef(d).Count > 0)
                    mapStuffDefs.Add(d);
            }

            // 账本独有的 stuff（地图没有）
            var missingFromMap = new HashSet<ThingDef>(ledgerStuffDefs);
            missingFromMap.ExceptWith(mapStuffDefs);

            if (missingFromMap.Count == 0) return true; // 地图已有全部材料 → 原版处理

            // 原版没这些东西会弹 "NoStuffsToBuildWith" → 我们接管
            var allDefs = new HashSet<ThingDef>(mapStuffDefs);
            allDefs.UnionWith(ledgerStuffDefs);

            var list = new List<FloatMenuOption>();
            foreach (var stuffDef in allDefs.OrderByDescending(d => d.stuffProps?.commonality ?? 0f)
                         .ThenBy(d => d.BaseMarketValue))
            {
                var localDef = stuffDef;
                string text = GenLabel.ThingLabel(__instance.PlacingDef, localDef, 1).CapitalizeFirst();
                list.Add(new FloatMenuOption(text, () =>
                {
                    __instance.SetStuffDef(localDef);
                    Find.DesignatorManager.Select(__instance);
                }, localDef));
            }

            var floatMenu = new FloatMenu(list);
            Find.WindowStack.Add(floatMenu);
            Find.DesignatorManager.Select(__instance);
            return false; // 跳过原版
        }

        private static void CollectStuffDefs(Building_StorageCore core, ThingDef thingDef, HashSet<ThingDef> result)
        {
            foreach (var kv in core.Ledger.Stock)
            {
                if (kv.Value > 0 && kv.Key.def.IsStuff
                    && kv.Key.def.stuffProps.CanMake(thingDef))
                    result.Add(kv.Key.def);
            }
        }
    }
}
