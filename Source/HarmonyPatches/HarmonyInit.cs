using HarmonyLib;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// v3 过渡态：Harmony 补丁全部清空。账本层接入后再重新添加必要的兼容补丁。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            var harmony = new Harmony("DigitalStorage.HarmonyPatches");
            harmony.PatchAll();
            Log.Message("[DigitalStorage 3.0] Harmony ready (no patches applied yet).");
        }
    }
}
