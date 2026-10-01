using HarmonyLib;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            var harmony = new Harmony("DigitalStorage.HarmonyPatches");
            harmony.PatchAll();
            // Log.Message 在游戏内日志窗口不显示（只有 Player.log 有），诊断一律用 Warning。
            Log.Warning("[DigitalStorage 4.0] Harmony ready.");
            // 【临时诊断】补丁是否真的挂上（PatchAll 里任一补丁抛异常，它后面的就全不挂）
            Backpack.BackpackDiag.ReportPatchState();
        }
    }
}
