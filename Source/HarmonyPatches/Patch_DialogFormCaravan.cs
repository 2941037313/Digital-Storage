using DigitalStorage.UI;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// I2d: 把核心库存以 AddSection 方式注入 itemsTransfer widget，复用原版 UI。
    /// 提款在 PostOpen（unspawned Thing），Close/Cancel 回滚，Send 时合并到 transferables + 按 CountToTransfer 筛选。
    /// </summary>
    [HarmonyPatch(typeof(Dialog_FormCaravan))]
    public static class Patch_DialogFormCaravan
    {
        // ---------- PostOpen ----------

        [HarmonyPostfix]
        [HarmonyPatch("PostOpen")]
        static void PostOpen_Postfix(Dialog_FormCaravan __instance)
        {
            CaravanDS_Helper.InjectCoreItems(__instance);
        }

        // ---------- PostClose: WorldRoutePlanner 临时关闭不触发回滚 ----------

        [HarmonyPostfix]
        [HarmonyPatch("PostClose")]
        static void PostClose_Postfix(Dialog_FormCaravan __instance)
        {
            if (CaravanDS_Helper.WasCaravanSent(__instance)) return;
            // WorldRoutePlanner.Start → WindowStack.TryRemove 会临时关闭对话框重新打开，
            // 这种假关闭不应回滚数据。
            if (System.Environment.StackTrace.Contains("WorldRoutePlanner.Start")) return;
            CaravanDS_Helper.Rollback(__instance);
        }

        // ---------- CalculateAndRecacheTransferables: Reset / 首次创建时注入 ----------

        [HarmonyPostfix]
        [HarmonyPatch("CalculateAndRecacheTransferables")]
        static void CalculateAndRecache_Postfix(Dialog_FormCaravan __instance)
        {
            CaravanDS_Helper.InjectCoreItems(__instance);
        }

        // ---------- TryFormAndSendCaravan: Prefix 合并 + Postfix 筛选/生成 ----------

        [HarmonyPrefix]
        [HarmonyPatch("TryFormAndSendCaravan")]
        static void TryFormAndSend_Prefix(Dialog_FormCaravan __instance)
        {
            CaravanDS_Helper.MergeToTransferables(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch("TryFormAndSendCaravan")]
        static void TryFormAndSend_Postfix(Dialog_FormCaravan __instance, bool __result)
        {
            if (!__result) return;
            CaravanDS_Helper.MarkCaravanSent(__instance);
            var map = AccessTools.Field(typeof(Dialog_FormCaravan), "map").GetValue(__instance) as Map;
            if (map != null)
                CaravanDS_Helper.FinalizeCaravanItems(__instance, map, spawnOnMap: true);
        }

        // ---------- TryReformCaravan: Prefix 合并 + Postfix 筛选 ----------

        [HarmonyPrefix]
        [HarmonyPatch("TryReformCaravan")]
        static void TryReform_Prefix(Dialog_FormCaravan __instance)
        {
            CaravanDS_Helper.MergeToTransferables(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch("TryReformCaravan")]
        static void TryReform_Postfix(Dialog_FormCaravan __instance, bool __result)
        {
            if (!__result) return;
            CaravanDS_Helper.MarkCaravanSent(__instance);
            var map = AccessTools.Field(typeof(Dialog_FormCaravan), "map").GetValue(__instance) as Map;
            if (map != null)
                CaravanDS_Helper.FinalizeCaravanItems(__instance, map, spawnOnMap: false);
        }
    }
}
