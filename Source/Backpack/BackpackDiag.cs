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
