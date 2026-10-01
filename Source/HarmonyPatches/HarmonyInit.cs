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
            // 第三方定向兼容（可选 mod）：必须在 PatchAll 之后单独装，失败也不能影响本 mod 的补丁
            Compatibility.PhinixCompatPatch.Install(harmony);
            // Log.Message 在游戏内日志窗口不显示（只有 Player.log 有），诊断一律用 Warning。
            Log.Warning("[DigitalStorage 4.0] Harmony ready.");
            // 第三方兼容挂点数量（仅开发者模式；Phinix/红包未安装或改名时为 0）
            if (Prefs.DevMode)
                Log.Warning("[DigitalStorage] 第三方兼容挂点=" + Compatibility.PhinixCompatPatch.InstalledCount);
        }
    }
}
