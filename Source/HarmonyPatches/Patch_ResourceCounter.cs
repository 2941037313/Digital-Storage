using System;
using System.Collections.Generic;
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
    /// I5a：让 resourceCounter.GetCount 把全网账本库存也算进去。
    /// 本地核心 + 同 NetworkName 远程核心的账本合计。
    /// 一次修复 DrawPlaceMouseAttachments + DrawPanelReadout 的红字。
    /// 不影响财富/贸易——那些走独立代码路径。
    /// </summary>
    [HarmonyPatch(typeof(ResourceCounter), "GetCount", new Type[] { typeof(ThingDef) })]
    static class Patch_ResourceCounter_GetCount
    {
        private static readonly FieldInfo mapField = AccessTools.Field(typeof(ResourceCounter), "map");

        static void Postfix(ResourceCounter __instance, ThingDef rDef, ref int __result)
        {
            if (rDef.resourceReadoutPriority == ResourceCountPriority.Uncounted) return;
            // I4c: 交易对话框已通过 TradeDS_Helper 注入核心 Tradeable，此处跳过避免殖民地栏重复计数
            if (Find.WindowStack.WindowOfType<Dialog_Trade>() != null) return;

            var map = (Map)mapField.GetValue(__instance);
            if (map == null) return;

            var mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return;

            var cores = mapComp.GetAllCores();
            var networkNames = new HashSet<string>();

            for (int i = 0; i < cores.Count; i++)
            {
                var core = cores[i];
                if (!CoreFinder.IsUsable(core)) continue;
                if (!string.IsNullOrEmpty(core.NetworkName)) networkNames.Add(core.NetworkName);
                AddFromLedger(core.Ledger, rDef, ref __result);
            }

            // 远程同网络核心
            var gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp != null && networkNames.Count > 0)
            {
                foreach (var core in gameComp.GetAllCores())
                {
                    if (core.Map == map) continue;
                    if (!CoreFinder.IsUsable(core)) continue;
                    if (string.IsNullOrEmpty(core.NetworkName)) continue;
                    if (!networkNames.Contains(core.NetworkName)) continue;
                    AddFromLedger(core.Ledger, rDef, ref __result);
                }
            }
        }

        private static void AddFromLedger(Core.CoreLedger ledger, ThingDef def, ref int result)
        {
            foreach (var kv in ledger.Stock)
            {
                if (kv.Key.def == def && kv.Value > 0)
                {
                    long add = kv.Value > int.MaxValue - result ? int.MaxValue - result : kv.Value;
                    result += (int)add;
                }
            }
        }
    }
}
