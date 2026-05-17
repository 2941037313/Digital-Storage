using System.Collections.Generic;
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
    /// 让轨道交易信标能"看到"核心库存。
    /// TradeUtility.AllLaunchableThingsForTrade 遍历信标格子内的物品，
    /// 核心物品不在格子上所以看不到。此 postfix 追加核心库存的 GhostThing。
    /// 注意：实际交易逻辑由 TradeDS_Helper 处理（注入 Tradeable），
    /// 此 patch 主要解决 HasLaunchableThings 等前置检查。
    /// </summary>
    [HarmonyPatch(typeof(TradeUtility), "AllLaunchableThingsForTrade")]
    static class Patch_AllLaunchableThingsForTrade
    {
        static IEnumerable<Thing> Postfix(IEnumerable<Thing> __result, Map map)
        {
            foreach (var thing in __result)
                yield return thing;

            var mapComp = map?.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) yield break;

            foreach (var core in mapComp.GetAllCores())
            {
                if (!CoreFinder.IsUsable(core)) continue;
                foreach (var kv in core.Ledger.Stock)
                {
                    if (kv.Value <= 0 || kv.Key.def == null) continue;
                    long avail = core.Ledger.Available(kv.Key);
                    if (avail <= 0) continue;

                    var ghost = Ghost.GhostLedgerIndex.FindGhostFor(map, kv.Key);
                    if (ghost != null)
                        yield return ghost;
                }
            }
        }
    }
}
