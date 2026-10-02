using System;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// G2: 补充燃料从容器取。
    ///
    /// <c>RefuelWorkGiverUtility.FindBestFuel</c> 是 <b>private static</b>，
    /// 内部裸 <c>GenClosest.ClosestThingReachable(...)</c> <b>没传 <c>lookInHaulSources</c></b>
    /// → 只在 map 上找燃料 → 此 Postfix 在找不到时扫容器 → 取出放脚下。
    ///
    /// 下游 <c>JobDriver_Refuel</c> / <c>RefuelAtomic</c> 没有 <c>canGotoSpawnedParent</c>，
    /// 所以必须取出，不能让原版自己去容器里拿。
    /// </summary>
    [HarmonyPatch(typeof(RefuelWorkGiverUtility), "FindBestFuel")]
    [HarmonyPatch(new[] { typeof(Pawn), typeof(Thing) })]
    static class Patch_RefuelFromStorage
    {
        static void Postfix(Pawn pawn, Thing refuelable, ref Thing __result)
        {
            if (__result != null || pawn?.Map == null || refuelable == null) return;

            CompRefuelable comp = refuelable.TryGetComp<CompRefuelable>();
            if (comp == null) return;

            ThingFilter filter = comp.Props.fuelFilter;
            if (filter == null) return;

            int needed = comp.GetFuelCountToFullyRefuel();
            if (needed <= 0) return;

            Thing best = HaulSourceContents.FindBestIncludingRemote(pawn.Map, null, t => filter.Allows(t.def));
            if (best == null) return;

            Thing taken = HaulSourceContents.ExtractToFeet(best, needed, pawn);
            if (taken != null) __result = taken;
        }
    }
}
