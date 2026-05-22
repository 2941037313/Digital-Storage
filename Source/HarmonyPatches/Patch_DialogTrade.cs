using System.Collections.Generic;
using DigitalStorage.UI;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(Dialog_Trade))]
    public static class Patch_DialogTrade
    {
        [HarmonyPostfix]
        [HarmonyPatch("CacheTradeables")]
        static void CacheTradeables_Postfix(Dialog_Trade __instance, List<Tradeable> ___cachedTradeables)
        {
            TradeDS_Helper.InjectCoreTradeables(__instance, ___cachedTradeables);
        }
    }

    [HarmonyPatch(typeof(Window), "PostClose")]
    public static class Patch_Window_PostClose_ForTrade
    {
        [HarmonyPostfix]
        static void Postfix(Window __instance)
        {
            if (__instance is Dialog_Trade dialog)
            {
                if (!TradeDS_Helper.WasDealExecuted(dialog))
                    TradeDS_Helper.Rollback(dialog);
                TradeDS_Helper.CleanupState(dialog);
            }
            else
            {
                // 兼容 Dynamic Trade Interface 等替换原版窗口的 mod
                TradeDS_Helper.RollbackAndCleanupIfNotExecuted();
            }
        }
    }

    [HarmonyPatch(typeof(TradeDeal), "TryExecute")]
    public static class Patch_TradeDeal_TryExecute
    {
        [HarmonyPrefix]
        static void Prefix()
        {
            TradeDS_Helper.MarkDealExecutedForAnyActiveDialog();
        }

        [HarmonyPostfix]
        static void Postfix(bool __result)
        {
            if (!__result) return;
            TradeDS_Helper.CleanupAfterDealForAnyActiveDialog();
        }
    }
}
