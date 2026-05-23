using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(Building_Storage), "Accepts")]
    public static class Patch_BufferWarehouse_Accepts
    {
        [HarmonyPrefix]
        static bool Prefix(Thing t, Building_Storage __instance, ref bool __result)
        {
            if (__instance is Building_BufferWarehouse)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
