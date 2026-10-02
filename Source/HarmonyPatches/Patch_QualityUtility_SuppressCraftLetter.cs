using DigitalStorage.AI;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>抑制制作代理产物的"大师 / 传奇"信件</b>。
    ///
    /// <para><c>GenRecipe.PostProcessProduct</c>（<c>Verse\GenRecipe.cs:100</c>）对带品质的产物会调
    /// <c>QualityUtility.SendCraftNotification</c>，那是**信件**（<c>Find.LetterStack.ReceiveLetter</c>，
    /// 屏幕中央弹窗 + 可选跳转）。真人手工出一件传奇是惊喜；制作代理超频 9GHz 时，那是每个 tick
    /// 一封信的灾难 —— 而信件堆叠还会拖慢 UI。</para>
    ///
    /// <para>只在 <see cref="BillAutomationScope.SuppressCraftLetters"/> 为 true 时拦（作用域只包
    /// <c>MakeRecipeProducts</c> 那一段），**真人的手工产出一个字都不改**。品质信息不丢：
    /// 完成提示里会写成"传奇 钢铁长剑"（<c>BillCraftFunnel.DescribeProduct</c>）。</para>
    /// </summary>
    [HarmonyPatch(typeof(QualityUtility), "SendCraftNotification")]
    internal static class Patch_QualityUtility_SuppressCraftLetter
    {
        private static bool Prefix(Thing thing, Pawn worker)
        {
            return !BillAutomationScope.SuppressCraftLetters;
        }
    }
}
