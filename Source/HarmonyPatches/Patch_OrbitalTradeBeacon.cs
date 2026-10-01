using System.Collections.Generic;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 让轨道交易看见存储核心的内容物。
    ///
    /// <para><b>为什么必须 patch</b>：<c>TradeUtility.AllLaunchableThingsForTrade</c> 遍历信标
    /// 格子里的物品，而它对"容器里的东西"的支持是**硬编码类型白名单**
    /// （<c>GeneBank</c> → genepacks、<c>Building_Bookcase</c> → <c>HeldBooks</c>、
    /// <c>Building_OutfitStand</c> → <c>HeldItems</c>），**没有通用的 <c>IThingHolder</c> 递归**。</para>
    ///
    /// <para><b>4.0 的做法就是"排进那个白名单"</b>：追加容器内容物（真实 Thing），
    /// 并用与原版分支**同一个** <see cref="TradeUtility.PlayerSellableNow"/> 过滤。
    /// 容器内容物未 Spawned，而原版白名单里的书架/衣架内容物同样未 Spawned ⇒ 形状完全一致，
    /// 下游 <c>TradeDeal</c> / <c>Tradeable</c> 路径不需要我们做任何额外的事。</para>
    ///
    /// <para><b>因此 TradeDS_Helper 与 Patch_DialogTrade 整块删除</b>：3.0 之所以要
    /// "提款造 unspawned Thing → 注入 Tradeable → 关窗回滚 → 成交后退账"，
    /// 是因为账本里的东西不是真 Thing、原版列表里根本没有它们。现在它们本来就在列表里，
    /// 交易执行时才由原版从容器里拿走 —— **没有预扣，也就不需要回滚**。</para>
    ///
    /// <para>（对比：3.0 的 beacon patch 只能 yield 一个 <c>GhostThing</c>，它被
    /// <c>PlayerSellableNow</c> 判为不可交易，仅用来满足 <c>ColonyHasEnoughSilver</c>
    /// / <c>AmountSendableSilver</c> 之类的前置检查。）</para>
    /// </summary>
    [HarmonyPatch(typeof(TradeUtility), "AllLaunchableThingsForTrade")]
    static class Patch_AllLaunchableThingsForTrade
    {
        static IEnumerable<Thing> Postfix(IEnumerable<Thing> __result, Map map, ITrader trader)
        {
            foreach (Thing thing in __result)
                yield return thing;

            if (map == null) yield break;

            var contents = new List<Thing>();
            HaulSourceContents.GatherAll(map, contents);
            for (int i = 0; i < contents.Count; i++)
            {
                Thing t = contents[i];
                if (t == null || t.def == null || t.Destroyed) continue;
                // 与原版自身容器分支同一把尺子
                if (!TradeUtility.PlayerSellableNow(t, trader)) continue;
                yield return t;
            }
        }
    }
}
