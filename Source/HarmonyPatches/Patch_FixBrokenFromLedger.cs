using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// G3: 替换损坏零部件从账本取。
    /// WorkGiver_FixBrokenDownBuilding.FindClosestComponent 只在 map 上搜索零部件，
    /// 此 Postfix 在 map 无零件时查账本 → Withdraw → spawn 脚下。
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_FixBrokenDownBuilding), "FindClosestComponent")]
    [HarmonyPatch(new[] { typeof(Pawn) })]
    static class Patch_FixBrokenFromLedger
    {
        static void Postfix(Pawn pawn, ref Thing __result)
        {
            if (__result != null || pawn?.Map == null) return;

            var accesses = CoreFinder.AllUsableAccesses(pawn);
            if (accesses.Count == 0) return;

            foreach (var access in accesses)
            {
                foreach (var kv in access.ledgerCore.Ledger.Stock)
                {
                    if (kv.Value <= 0 || kv.Key.def == null) continue;
                    if (kv.Key.def != ThingDefOf.ComponentIndustrial) continue;

                    long avail = access.ledgerCore.Ledger.Available(kv.Key);
                    if (avail <= 0) continue;

                    // 修一次只需要 1 个零件
                    var thing = access.ledgerCore.Ledger.Withdraw(kv.Key, 1);
                    if (thing == null) continue;

                    if (GenPlace.TryPlaceThing(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, null, null, default))
                    {
                        __result = thing;
                        return;
                    }
                    access.ledgerCore.Ledger.AddRaw(kv.Key, 1);
                    thing.Destroy(DestroyMode.Vanish);
                }
            }
        }
    }
}
