using System;
using System.Reflection;
using System.Text;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Backpack
{
    /// <summary>
    /// 【临时诊断】背包取料链的探针。定位完就整文件删除。
    ///
    /// <para>用户报「让殖民者制作东西，殖民者还是前往核心而不是走背包」，但静态读源码
    /// 每一道门都成立（<c>ParentHolder</c> 确实是核心、<c>SpawnedParentOrMe</c> 确实是小人、
    /// 读档确实会调 <c>Pawn.SpawnSetup</c>）。所以不再推断，让运行时说实话：
    /// <list type="number">
    /// <item>启动时汇报关键挂点的 patch 数 —— 直接回答"补丁到底挂上没有"
    ///   （<c>Harmony.PatchAll</c> 里任一补丁抛异常，它**后面**的补丁就全不挂，
    ///   所以不能假设挂上了）</item>
    /// <item>取料 toil 每次执行打印全部门值 + 每件料的处置</item>
    /// <item><c>JobDriver_HaulToContainer</c> 目的地是我们的核心时打印一行 ——
    ///   覆盖"搬空工作台 / 搬货入库"这条也会走到核心的路</item>
    /// </list></para>
    ///
    /// <para>开关：开发者模式 或 Mod 设置里的「启用调试日志」。全部走 <c>Log.Warning</c>。</para>
    /// </summary>
    internal static class BackpackDiag
    {
        public static bool On
        {
            get { return Prefs.DevMode || DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog; }
        }

        public static void Say(string msg)
        {
            if (On) Log.Warning("[DS-BAG] " + msg);
        }

        /// <summary>启动时汇报挂点状态。补丁没挂上时这是唯一能说话的地方。</summary>
        public static void ReportPatchState()
        {
            if (!On) return;

            var sb = new StringBuilder();
            sb.AppendLine("[DS-BAG] ==== Harmony 挂点状态 ====");
            Append(sb, typeof(JobDriver_DoBill), "MakeNewToils");
            Append(sb, typeof(JobDriver_HaulToContainer), "MakeNewToils");
            Append(sb, typeof(Pawn), "SpawnSetup");
            Append(sb, typeof(Pawn), "Kill");
            Append(sb, typeof(Pawn_GuestTracker), "SetGuestStatus");
            Log.Warning(sb.ToString());
        }

        private static void Append(StringBuilder sb, System.Type type, string method)
        {
            MethodInfo mi = AccessTools.Method(type, method);
            if (mi == null)
            {
                sb.AppendLine("  " + type.Name + "." + method + " —— **方法没找到**");
                return;
            }

            Patches info = Harmony.GetPatchInfo(mi);
            int pre = (info == null || info.Prefixes == null) ? 0 : info.Prefixes.Count;
            int post = (info == null || info.Postfixes == null) ? 0 : info.Postfixes.Count;
            sb.AppendLine("  " + type.Name + "." + method + " prefix=" + pre + " postfix=" + post);
        }
    }

    /// <summary>
    /// 诊断：**用原版自己的那条 toil** 验证"走到 X"会把 dest 解析成谁。
    ///
    /// <para><c>Toils_Goto.GotoThing(..., canGotoSpawnedParent: true)</c> 的 dest 在
    /// toil 的 initAction 里才取 <c>SpawnedParentOrMe</c>（<c>Toils_Goto.cs:20</c>）。
    /// 用户看到的"为取料走向核心"如果成立，这里就会在 `job=DoBill` 而 dest 解析成核心时打出 ★。
    /// 反过来说：**只要 ★ 一次都没出现，bill 的取料就真的没走向核心**，往核心走的是别的作业。</para>
    ///
    /// <para>只在诊断开关打开时才包装 initAction（release 下零影响），且原 initAction 照常调用。</para>
    /// </summary>
    [HarmonyPatch(typeof(Toils_Goto), "GotoThing")]
    internal static class Patch_Diag_GotoSpawnedParent
    {
        [HarmonyPostfix]
        private static void Postfix(TargetIndex ind, bool canGotoSpawnedParent, ref Toil __result)
        {
            if (!BackpackDiag.On || !canGotoSpawnedParent || __result == null) return;

            Toil toil = __result;
            Action original = toil.initAction;
            toil.initAction = delegate
            {
                Pawn actor = toil.actor;
                Job job = (actor == null || actor.jobs == null) ? null : actor.jobs.curJob;
                Thing thing = (job == null) ? null : job.GetTarget(ind).Thing;
                Thing dest = (thing == null) ? null : thing.SpawnedParentOrMe;

                if (dest is Building_StorageCore)
                {
                    BackpackDiag.Say("★ 往核心寻路：pawn=" + (actor == null ? "?" : actor.LabelShortCap)
                        + " job=" + (job == null ? "?" : job.def.defName)
                        + " 目标=" + (thing == null ? "?" : thing.LabelShort)
                        + " dest=" + dest.LabelShort
                        + "（背包里那个应该解析成小人自己才会是 0 距离）");
                }

                if (original != null) original();
            };
        }
    }

    /// <summary>
    /// 诊断：原版"台子上有东西 ⇒ 先搬走再开工"这条链（<c>WorkGiverUtility.HaulStuffOffBillGiverJob</c>）。
    /// 它一响，DoBill 作业根本不会建 —— 用户看到的"殖民者拿着料往核心走"多半就是它。
    /// 只读，不改行为。
    /// </summary>
    [HarmonyPatch(typeof(WorkGiverUtility), "HaulStuffOffBillGiverJob")]
    internal static class Patch_Diag_HaulStuffOffBillGiver
    {
        [HarmonyPostfix]
        private static void Postfix(Pawn pawn, IBillGiver giver, ref Job __result)
        {
            if (!BackpackDiag.On || __result == null) return;

            Thing bench = giver as Thing;
            BackpackDiag.Say("【搬空工作台】pawn=" + (pawn == null ? "?" : pawn.LabelShortCap)
                + " 工作台=" + (bench == null ? "?" : bench.LabelShort)
                + " → 派了作业 " + __result.def.defName
                + "（DoBill 本次不会建）");
        }
    }

    /// <summary>
    /// 诊断：任何"把东西搬进我们核心"的搬运作业都报一行。
    /// 这条覆盖"取料 toil 之外"的走位 —— 用户看到的"前往核心"可能是它。
    /// 只读，不改行为。
    /// </summary>
    [HarmonyPatch(typeof(JobDriver_HaulToContainer), "MakeNewToils")]
    internal static class Patch_Diag_HaulToCore
    {
        [HarmonyPostfix]
        private static void Postfix(JobDriver_HaulToContainer __instance)
        {
            if (!BackpackDiag.On) return;

            Job job = __instance.job;
            if (job == null) return;

            Thing dest = job.GetTarget(TargetIndex.B).Thing;
            if (!(dest is Building_StorageCore)) return;

            Thing carried = job.GetTarget(TargetIndex.A).Thing;
            BackpackDiag.Say("【搬运入库】pawn=" + (__instance.pawn == null ? "?" : __instance.pawn.LabelShortCap)
                + " 搬=" + (carried == null ? "?" : carried.LabelShort)
                + " → 核心 " + dest.LabelShort
                + "  job=" + job.def.defName
                + " 已在手上=" + (__instance.pawn != null && __instance.pawn.IsCarryingThing(carried)));
        }
    }
}
