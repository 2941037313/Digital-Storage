using System.Collections.Generic;
using DigitalStorage.Diagnostics;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 【临时诊断脚手架 —— 只计数，不改内容】
    ///
    /// <para>访客商队这条交易路径**原版本来就支持容器**（
    /// <c>Pawn_TraderTracker.ColonyThingsWillingToBuy:124-135</c> 枚举
    /// <c>listerBuildings.AllColonistBuildingsOfType&lt;IHaulSource&gt;()</c>
    /// 并 yield <c>GetDirectlyHeldThings()</c>），所以它**不是**我们的补丁能影响的。
    /// 它同样是空的 ⇒ 故障点在这些原版闸门上：</para>
    /// <list type="bullet">
    /// <item>核心在不在 <c>allBuildingsColonist</c>（= 登记时的阵营）</item>
    /// <item><c>ReachableForTrade</c>（<c>CanReach</c> 到核心格，封闭房间会失败）</item>
    /// </list>
    ///
    /// <para>这里只在枚举**结束后**打一条"原版产出 N 件" + 核心状态，用来区分
    /// "原版序列本身是空的" 与 "原版序列有东西但被 Dialog_Trade 过滤掉了"。
    /// 转发逻辑与原版逐字一致。</para>
    /// </summary>
    internal static class TradeDiagWrap
    {
        internal static IEnumerable<Thing> Wrap(IEnumerable<Thing> source, Pawn negotiator, string key, string label)
        {
            int count = 0;
            if (source != null)
            {
                foreach (Thing t in source)
                {
                    count++;
                    yield return t;
                }
            }

            if (!TradeDiagnostics.FirstTime(key)) yield break;

            Log.Warning("[DS-DIAG] " + label + " 枚举完成：原版产出 " + count + " 件"
                + "（Negotiator=" + (negotiator == null ? "null" : negotiator.LabelShort) + "）");
            TradeDiagnostics.DumpCoreState(label, null);
        }
    }

    /// <summary>访客商人（<c>Pawn_TraderTracker</c>，注意它**只**实现 IExposable，不是 ITrader）。</summary>
    [HarmonyPatch(typeof(Pawn_TraderTracker), "ColonyThingsWillingToBuy")]
    internal static class Patch_Diag_PawnTraderTracker
    {
        private static IEnumerable<Thing> Postfix(IEnumerable<Thing> __result, Pawn playerNegotiator)
        {
            return TradeDiagWrap.Wrap(__result, playerNegotiator,
                "diag.caravan.pawn", "访客商队 ColonyThingsWillingToBuy");
        }
    }

    /// <summary>商队/据点商人基类（virtual 声明处）。</summary>
    [HarmonyPatch(typeof(Settlement_TraderTracker), "ColonyThingsWillingToBuy")]
    internal static class Patch_Diag_SettlementTraderTracker
    {
        private static IEnumerable<Thing> Postfix(IEnumerable<Thing> __result, Pawn playerNegotiator)
        {
            return TradeDiagWrap.Wrap(__result, playerNegotiator,
                "diag.caravan.settlement", "据点商人 ColonyThingsWillingToBuy");
        }
    }
}
