using DigitalStorage.AI;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 原版建造投料回落点：
    /// WorkGiver_ConstructDeliverResources.ResourceDeliverJobFor 在原版找不到物理材料（返回 null）时，
    /// 尝试从数字存储账本补一个 DigitalStorage_WithdrawToConstruction job。
    ///
    /// 用 Postfix 而不是 Prefix：
    /// - 不覆盖原版已有行为：原版找到物理材料 / 安装蓝图 InstallJob 时 __result != null，直接跳过；
    /// - Blueprint_Install 的 TotalMaterialCost 契约由原版与 DSConstructionDelivery 双重保证；
    /// - `!__runOriginal` 时不补 job：尊重其他 mod 在 ResourceDeliverJobFor 上的 Prefix veto；
    /// - Priority=Low：让其他 mod 的 fallback postfix 先跑。
    ///
    /// VEF 技能限制 / 第一人称角色 avatar 禁用等 gate 位于 JobOnThing 层：
    /// gate 返回 false 时整个 JobOnThing 方法体不执行，本 Postfix 不会被调用，天然继承 gate。
    ///
    /// Achtung 强制建造、Construction work 取料、第三方 WorkGiver 子类等原版路径也都会经过
    /// ResourceDeliverJobFor，因此都能走这条账本兜底；正常 Hauling 路径仍由
    /// WorkGiver_DS_WithdrawForConstruct（priorityInType=200）保持"账本优先"的现有体验。
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor")]
    public static class Patch_ResourceDeliverJobFor
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        public static void Postfix(Pawn pawn, IConstructible c, bool forced, bool __runOriginal, ref Job __result)
        {
            if (!__runOriginal) return;       // 其他 Prefix 跳过了原版：尊重其决定
            if (__result != null) return;     // 原版已找到物理材料 job / 安装 job

            if (DSConstructionDelivery.TryMakeJob(pawn, c, forced, out var job))
                __result = job;
        }
    }
}
