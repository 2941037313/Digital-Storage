using System.Linq;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// G2: 补充燃料从账本取。
    /// RefuelWorkGiverUtility.FindBestFuel 只在 map 上搜索燃料，
    /// 此 Postfix 在 map 无燃料时查账本 → Withdraw → spawn 脚下。
    /// </summary>
    [HarmonyPatch(typeof(RefuelWorkGiverUtility), "FindBestFuel")]
    [HarmonyPatch(new[] { typeof(Pawn), typeof(Thing) })]
    static class Patch_RefuelFromLedger
    {
        static void Postfix(Pawn pawn, Thing refuelable, ref Thing __result)
        {
            if (__result != null || pawn?.Map == null || refuelable == null) return;

            var comp = refuelable.TryGetComp<CompRefuelable>();
            if (comp == null) return;

            var filter = comp.Props.fuelFilter;
            if (filter == null) return;

            var accesses = CoreFinder.AllUsableAccesses(pawn);
            if (accesses.Count == 0) return;

            // 找燃料：任何通过 fuelFilter 的 ThingDef
            foreach (var access in accesses)
            {
                foreach (var kv in access.ledgerCore.Ledger.Stock)
                {
                    if (kv.Value <= 0 || kv.Key.def == null) continue;
                    if (!filter.Allows(kv.Key.def)) continue;

                    long avail = access.ledgerCore.Ledger.Available(kv.Key);
                    if (avail <= 0) continue;

                    int needed = comp.GetFuelCountToFullyRefuel();
                    if (needed <= 0) continue;
                    int take = System.Math.Min(needed, (int)System.Math.Min(avail, (long)int.MaxValue));
                    var thing = access.ledgerCore.Ledger.Withdraw(kv.Key, take);
                    if (thing == null) continue;

                    // 统一 spawn 脚下：FindBestFuel 只扫地图不扫背包，芯片也无法走背包捷径
                    if (GenPlace.TryPlaceThing(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, null, null, default))
                    {
                        __result = thing;
                        return;
                    }
                    // spawn 失败 → 退回账本
                    access.ledgerCore.Ledger.AddRaw(kv.Key, take);
                    thing.Destroy(DestroyMode.Vanish);
                }
            }
        }
    }
}
