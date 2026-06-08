using System.Collections.Generic;
using DigitalStorage.UI;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// DTI 存在检测——静态初始化一次。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class DTICompat
    {
        public static readonly bool Active;
        private static readonly System.Type WindowDynamicTradeType;

        static DTICompat()
        {
            WindowDynamicTradeType = AccessTools.TypeByName("DynamicTradeInterface.UserInterface.Window_DynamicTrade");
            Active = WindowDynamicTradeType != null;
            if (Active)
                Log.Warning("[DS-DTI] DTI detected — using PostOpen-only injection");
        }

        public static bool IsDTIOnStack()
        {
            if (!Active) return false;
            var stack = Find.WindowStack?.Windows;
            if (stack == null) return false;
            foreach (var w in stack)
                if (WindowDynamicTradeType.IsInstanceOfType(w))
                    return true;
            return false;
        }
    }

    // ===== PostOpen Prefix：在 DTI 换窗前注入 deal.AllTradeables =====
    [HarmonyPatch(typeof(Dialog_Trade), "PostOpen")]
    [HarmonyPriority(-2)]
    [HarmonyBefore("DynamicTradeInterfaceMod")]
    public static class Patch_DialogTrade_PostOpen_Early
    {
        [HarmonyPrefix]
        static void Prefix(Dialog_Trade __instance)
        {
            // DTI 的 TryRemove 在 Add(DTI窗口) 之前触发 PostClose。
            // PostClose 检查 DTI 窗口时窗口还没入栈 → IsDTIOnStack=false → 错误回滚。
            // 提前标 beingReplaced，PostClose 看到标记直接跳过。
            if (DTICompat.Active)
                TradeDS_Helper.MarkBeingReplaced(__instance);

            TradeDS_Helper.InjectCoreTradeables(__instance, TradeSession.deal?.AllTradeables);
        }
    }

    // ===== CacheTradeables Prefix：原版兜底（搜索/排序触发）=====
    // DTI 已通过 PostOpen Prefix 注入——CacheTradeables 再跑 Re-inject 会回滚销毁 DTI 持有的 Thing。
    [HarmonyPatch(typeof(Dialog_Trade), "CacheTradeables")]
    public static class Patch_DialogTrade_CacheTradeables
    {
        [HarmonyPrefix]
        static void Prefix(Dialog_Trade __instance)
        {
            if (!DTICompat.Active)
                TradeDS_Helper.InjectCoreTradeables(__instance, TradeSession.deal?.AllTradeables);
        }
    }

    // ===== PostClose / rollback =====
    [HarmonyPatch(typeof(Window), "PostClose")]
    public static class Patch_Window_PostClose_ForTrade
    {
        [HarmonyPostfix]
        static void Postfix(Window __instance)
        {
            if (__instance is Dialog_Trade dialog)
            {
                var state = TradeDS_Helper.GetStateFor(dialog);
                if (state != null && state.beingReplaced)
                    return; // DTI 换窗——在 PostOpen 已提前标记
                if (!TradeDS_Helper.WasDealExecuted(dialog))
                    TradeDS_Helper.Rollback(dialog);
                TradeDS_Helper.CleanupState(dialog);
            }
            else
            {
                TradeDS_Helper.RollbackAndCleanupIfNotExecuted();
            }
        }
    }

    // ===== TryExecute =====
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
