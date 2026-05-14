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
            if (__instance is Dialog_Trade dialog && !TradeDS_Helper.WasDealExecuted(dialog))
                TradeDS_Helper.Rollback(dialog);
        }
    }

    [HarmonyPatch(typeof(TradeDeal), "TryExecute")]
    public static class Patch_TradeDeal_TryExecute
    {
        [HarmonyPrefix]
        static void Prefix()
        {
            var dialog = Find.WindowStack.WindowOfType<Dialog_Trade>();
            if (dialog != null)
                TradeDS_Helper.MarkDealExecuted(dialog);
        }

        [HarmonyPostfix]
        static void Postfix(bool __result)
        {
            if (!__result) return;
            var dialog = Find.WindowStack.WindowOfType<Dialog_Trade>();
            if (dialog != null)
                TradeDS_Helper.CleanupAfterDeal(dialog);
        }
    }
}
