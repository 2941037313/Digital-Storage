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
    /// <para><b>4.0 的做法就是"排进那个白名单"</b>：追加容器内容物（真实 Thing）。
    /// 容器内容物未 Spawned，而原版白名单里的书架/衣架内容物同样未 Spawned ⇒ 形状完全一致。</para>
    ///
    /// <para><b>⚠️ 与书架分支的**有意**差异</b>：原版那个分支写的是
    /// <c>PlayerSellableNow(t, trader)</c>，而 <c>t</c> 是**容器（书架本体）**，
    /// 不是被 yield 的 <c>heldBook</c> —— 看起来是原版的复制粘贴笔误。
    /// 我们传**内容物本身**（更正确：可售性判定的应该是那件东西）。
    /// <b>将来不要为了"对齐原版"把它改回容器</b>，那会把过滤器加在错误的对象上。</para>
    ///
    /// <para><b>为什么不需要"先取出再交交易"</b>（交易执行链已逐行核对）：
    /// <c>Tradeable.ResolveTrade</c> → <c>ITrader.GiveSoldThingToTrader</c>
    /// （<c>TradeShip.cs:181</c> / <c>Pawn_TraderTracker.cs:162</c>）第一句就是
    /// <c>toGive.SplitOff(countToGive)</c>，随后 <c>things.TryAdd(...)</c> 或
    /// <c>TryAbsorbStack</c>。而 <c>Thing.SplitOff</c>（<c>Thing.cs:1604</c>）：
    /// <code>
    /// if (count &gt;= stackCount) { DeSpawnOrDeselect(); holdingOwner?.Remove(this); return this; }
    /// // count &lt; stackCount → 新建一个无主 Thing，原堆 stackCount 就地扣减
    /// </code>
    /// **整堆卖出会 <c>holdingOwner?.Remove(this)</c>，把东西从我们的 <c>ThingOwner</c> 里正确摘除**；
    /// 部分卖出则只扣数量。整条链**没有任何 <c>thing.Spawned</c> / <c>thing.Position</c> 依赖**
    /// ⇒ 未 Spawned 的容器内容物天然可用，不需要预扣、也就不需要回滚。</para>
    ///
    /// <para><b>因此 3.0 的整层注入机制被删除</b>：<c>TradeDS_Helper</c>（提款造
    /// unspawned Thing → 注入 Tradeable → 关窗回滚 → 成交后退账）+
    /// <c>Patch_DialogTrade</c>（PostOpen/CacheTradeables/PostClose/TryExecute 四个补丁）。
    /// 它们当初存在**只是因为账本里的东西不是真 Thing**、原版列表里根本没有它们。
    /// <b>附带收益：DTI（Dynamic Trade Interface）兼容层也不需要了</b> ——
    /// DTI 消费的就是 <c>TradeSession.deal.AllTradeables</c>，而它由 <c>TradeDeal</c> 从
    /// <c>trader.ColonyThingsWillingToBuy</c> 构建；我们让原版列表直接包含内容物，
    /// DTI 自动就看见了，<c>DTICompat</c> / <c>Window_DynamicTrade</c> / <c>beingReplaced</c>
    /// 那套换窗 HACK 全部作废。</para>
    ///
    /// <para><b>顺带发现</b>：<c>Pawn_TraderTracker.ColonyThingsWillingToBuy:124</c>
    /// **原本就在枚举** <c>map.listerBuildings.AllColonistBuildingsOfType&lt;IHaulSource&gt;()</c>
    /// 并 yield <c>GetDirectlyHeldThings()</c> ⇒ 访客/商队交易**零补丁**就能看见我们的容器。
    /// 只有轨道信标这条（走格子）需要补。</para>
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
                // 传内容物本身（原版书架分支误传了容器 —— 见类注释，不要"对齐"回去）
                if (!TradeUtility.PlayerSellableNow(t, trader)) continue;
                yield return t;
            }
        }
    }
}
