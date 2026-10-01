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
    /// <para>2026-10-02 事故：曾按名字 patch <c>Toils_Goto.GotoThing</c>，而它有**两个重载**
    /// ⇒ <c>AmbiguousMatchException</c> 抛在 <c>HarmonyInit</c> 静态构造里 ⇒
    /// <b><c>PatchAll</c> 中断，它之后的所有补丁全部静默不挂</b>（整个 mod 半死不活）。
    /// 所以现在这里有一条<b>挂点审计</b>：逐条 try/catch，把"没挂上"和"歧义"都打出来。</para>
    ///
    /// <para>开关：开发者模式 或 Mod 设置里的「启用调试日志」。全部走 <c>Log.Warning</c>。</para>
    /// </summary>
    internal static class BackpackDiag
    {
        /// <summary>与 <c>HarmonyInit</c> 里 new Harmony(...) 的 id 必须一致。</summary>
        public const string HarmonyId = "DigitalStorage.HarmonyPatches";

        public static bool On
        {
            get { return Prefs.DevMode || DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog; }
        }

        public static void Say(string msg)
        {
            if (On) Log.Warning("[DS-BAG] " + msg);
        }

        /// <summary>
        /// 启动时审计本 mod 的每个挂点。**本方法在 HarmonyInit 静态构造里被调用，
        /// 因此自身绝不能抛异常** —— 全程 try/catch。
        /// </summary>
        public static void ReportPatchState()
        {
            if (!On) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("[DS-BAG] ==== 本 mod Harmony 挂点审计（ours = 本 mod 挂上的条数） ====");
                int bad = 0;

                bad += Check(sb, typeof(JobDriver_DoBill), "MakeNewToils");
                bad += Check(sb, typeof(JobDriver_HaulToContainer), "MakeNewToils");
                bad += Check(sb, typeof(WorkGiverUtility), "HaulStuffOffBillGiverJob");
                bad += Check(sb, typeof(Pawn), "SpawnSetup");
                bad += Check(sb, typeof(Pawn), "Kill");
                bad += Check(sb, typeof(Pawn_GuestTracker), "SetGuestStatus");
                bad += Check(sb, typeof(JobGiver_GetFood), "TryGiveJob");
                bad += Check(sb, typeof(JobGiver_TakeDrugsForDrugPolicy), "TryGiveJob");
                bad += Check(sb, typeof(JoyGiver_Ingest), "TryGiveJob");
                bad += Check(sb, typeof(JobGiver_SatisfyChemicalNeed), "TryGiveJob");
                bad += Check(sb, typeof(JobGiver_SatifyChemicalDependency), "TryGiveJob");
                bad += Check(sb, typeof(JoyGiver_TakeDrug), "BestIngestItem");
                bad += Check(sb, typeof(FoodUtility), "TryFindBestFoodSourceFor");
                bad += Check(sb, typeof(HealthAIUtility), "FindBestMedicine");
                bad += Check(sb, typeof(HaulAIUtility), "PawnCanAutomaticallyHaul");
                bad += Check(sb, typeof(HaulAIUtility), "PawnCanAutomaticallyHaulFast");
                bad += Check(sb, typeof(JobDriver_Equip), "Notify_Starting");
                bad += Check(sb, typeof(JobDriver_Equip), "MakeNewToils");
                bad += Check(sb, typeof(TradeUtility), "AllLaunchableThingsForTrade");
                bad += Check(sb, typeof(TradeDeal), "InSellablePosition");
                bad += Check(sb, typeof(ResourceCounter), "UpdateResourceCounts");
                bad += Check(sb, typeof(Designator_Build), "ProcessInput");

                sb.AppendLine("  —— ours=0 / 异常 的条目数 = " + bad + "（>0 就是有挂点没挂上）");
                Log.Warning(sb.ToString());
            }
            catch (Exception e)
            {
                Log.Warning("[DS-BAG] 挂点审计自身异常（已忽略）：" + e);
            }
        }

        private static int Check(StringBuilder sb, Type type, string method)
        {
            try
            {
                MethodInfo mi = AccessTools.Method(type, method);
                if (mi == null)
                {
                    sb.AppendLine("  " + type.Name + "." + method + " —— 方法没找到");
                    return 1;
                }

                Patches info = Harmony.GetPatchInfo(mi);
                int ours = CountOurs(info == null ? null : info.Prefixes)
                         + CountOurs(info == null ? null : info.Postfixes)
                         + CountOurs(info == null ? null : info.Transpilers);
                int all = Count(info == null ? null : info.Prefixes)
                        + Count(info == null ? null : info.Postfixes)
                        + Count(info == null ? null : info.Transpilers);

                sb.AppendLine("  " + type.Name + "." + method + "  ours=" + ours + " / 全量=" + all);
                return ours == 0 ? 1 : 0;
            }
            catch (Exception e)
            {
                // 歧义 / 解析失败 —— 这类异常会让 PatchAll 中断，必须显眼
                sb.AppendLine("  " + type.Name + "." + method + " —— ★★ 挂点异常 "
                    + e.GetType().Name + "（这种异常会中断 PatchAll，后面全部补丁不挂）");
                return 1;
            }
        }

        private static int Count(System.Collections.Generic.IEnumerable<Patch> patches)
        {
            if (patches == null) return 0;
            int n = 0;
            foreach (Patch p in patches) n++;
            return n;
        }

        private static int CountOurs(System.Collections.Generic.IEnumerable<Patch> patches)
        {
            if (patches == null) return 0;
            int n = 0;
            foreach (Patch p in patches)
            {
                if (p != null && p.owner == HarmonyId) n++;
            }
            return n;
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
