using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// G3: 替换损坏零部件从容器取。
    ///
    /// <c>WorkGiver_FixBrokenDownBuilding.FindClosestComponent</c> 是 <b>private</b>，
    /// 内部裸 <c>GenClosest.ClosestThingReachable(...)</c> <b>没传 <c>lookInHaulSources</c></b>
    /// → 只在 map 上找零件 → 此 Postfix 在找不到时扫容器 → 取出放脚下。
    ///
    /// 下游 <c>JobDriver_FixBrokenDownBuilding</c> 没有 <c>canGotoSpawnedParent</c>，必须取出。
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_FixBrokenDownBuilding), "FindClosestComponent")]
    [HarmonyPatch(new[] { typeof(Pawn) })]
    static class Patch_FixBrokenFromStorage
    {
        static void Postfix(Pawn pawn, ref Thing __result)
        {
            if (__result != null || pawn?.Map == null) return;

            // 修一次只需要 1 个工业零件（与原版 FindClosestComponent 同一个 def）。
            Thing best = HaulSourceContents.FindBestIncludingRemote(
                pawn.Map, null, t => t.def == ThingDefOf.ComponentIndustrial);
            if (best == null) return;

            Thing taken = HaulSourceContents.ExtractToFeet(best, 1, pawn);
            if (taken != null) __result = taken;
        }
    }
}
