using System;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// G1：治疗时账本取药。Postfix HealthAIUtility.FindBestMedicine。
    /// 原版返回 null（地图无药）→ 扫账本 → Withdraw → 芯片直塞背包 / 无芯片 spawn 脚下。
    /// 药品优先级按 MedicalPotency 排序（闪耀药 > 普通药 > 草药），同时受患者 medCare 策略约束。
    /// </summary>
    [HarmonyPatch(typeof(HealthAIUtility), "FindBestMedicine")]
    static class Patch_FindBestMedicine
    {
        static void Postfix(Pawn healer, Pawn patient, bool onlyUseInventory, ref Thing __result)
        {
            if (__result is Ghost.GhostThing) __result = null;
            if (__result != null || onlyUseInventory) return;
            if (healer?.Map == null || patient == null) return;

            var accesses = CoreFinder.AllUsableAccesses(healer);
            if (accesses.Count == 0) return;

            MedicalCareCategory medCare = patient.playerSettings?.medCare ?? MedicalCareCategory.NoMeds;
            if (medCare <= MedicalCareCategory.NoMeds) return;

            int needed = Medicine.GetMedicineCountToFullyHeal(patient);
            if (needed <= 0) return;

            // 扫所有核心账本，找品质最高的药品
            ItemKey? bestKey = null;
            float bestPotency = float.MinValue;
            Building_StorageCore bestCore = null;
            long bestAvail = 0;

            foreach (var access in accesses)
            {
                foreach (var kv in access.ledgerCore.Ledger.Stock)
                {
                    if (kv.Value <= 0) continue;
                    var def = kv.Key.def;
                    if (def == null || !def.IsMedicine) continue;
                    if (!medCare.AllowsMedicine(def)) continue;

                    long avail = access.ledgerCore.Ledger.Available(kv.Key);
                    if (avail <= 0) continue;

                    float potency = def.GetStatValueAbstract(StatDefOf.MedicalPotency);
                    if (potency > bestPotency)
                    {
                        bestPotency = potency;
                        bestKey = kv.Key;
                        bestCore = access.ledgerCore;
                        bestAvail = avail;
                    }
                }
            }

            if (bestKey == null || bestCore == null) return;

            int take = Math.Min((int)bestAvail, needed);
            if (take <= 0) return;

            var thing = bestCore.Ledger.Withdraw(bestKey.Value, take);
            if (thing == null) return;

            if (Hediff_TerminalImplant.HasTerminalImplant(healer))
            {
                // 芯片 pawn：药直塞背包，跳过取药行走
                if (!healer.inventory.innerContainer.TryAdd(thing, true))
                    return;
            }
            else
            {
                // 无芯片：药生成在脚下，就近捡起
                if (!GenPlace.TryPlaceThing(thing, healer.Position, healer.Map, ThingPlaceMode.Near, null, null, default))
                    return;
            }

            __result = thing;
        }
    }
}
