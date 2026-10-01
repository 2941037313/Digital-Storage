using System;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// G1：治疗时从容器取药。Postfix <c>HealthAIUtility.FindBestMedicine</c>。
    ///
    /// 原版返回 null（地图上没药）→ 扫容器 → 取出放到治疗者脚下 → 原版 tend job 照跑。
    ///
    /// 药品优先级按 <c>MedicalPotency</c> 排序（闪耀药 &gt; 普通药 &gt; 草药），
    /// 同时受患者 <c>medCare</c> 策略约束。
    ///
    /// <b>为什么必须"取出来给它"</b>：原版这个方法既没有 <c>lookInHaulSources</c> 参数，
    /// 下游的 <c>JobDriver_TendPatient</c> 也没有 <c>canGotoSpawnedParent</c> ——
    /// 原版根本走不到容器内容物。取出后原版 job 语义一字不改。
    /// </summary>
    [HarmonyPatch(typeof(HealthAIUtility), "FindBestMedicine")]
    static class Patch_FindBestMedicine
    {
        static void Postfix(Pawn healer, Pawn patient, bool onlyUseInventory, ref Thing __result)
        {
            if (__result != null || onlyUseInventory) return;
            if (healer?.Map == null || patient == null) return;

            MedicalCareCategory medCare = patient.playerSettings?.medCare ?? MedicalCareCategory.NoMeds;
            if (medCare <= MedicalCareCategory.NoMeds) return;

            int needed = Medicine.GetMedicineCountToFullyHeal(patient);
            if (needed <= 0) return;

            Thing best = HaulSourceContents.FindBest(
                healer.Map,
                t => t.def.GetStatValueAbstract(StatDefOf.MedicalPotency),
                t => t.def.IsMedicine && medCare.AllowsMedicine(t.def));

            if (best == null) return;

            Thing taken = HaulSourceContents.ExtractToFeet(best, needed, healer);
            if (taken != null) __result = taken;
        }
    }
}
