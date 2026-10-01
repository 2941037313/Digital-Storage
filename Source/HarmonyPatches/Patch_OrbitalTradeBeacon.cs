using System;
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
    /// <para><b>4.0 的做法就是"排进那个白名单"</b>，并且**逐条对齐原版分支的形状**：
    /// <list type="bullet">
    /// <item>只取容器的**直接**内容物（<c>HaulSourceContents.CollectDirect</c>），
    ///   与原版 <c>HeldBooks</c> / <c>HeldItems</c> 一致 —— 递归会把尸体里的 pawn、
    ///   缩小件里的 building 也交出来，那不是可交易物。</item>
    /// <item>用与原版分支**同一个** <see cref="TradeUtility.PlayerSellableNow"/> 过滤。</item>
    /// </list></para>
    ///
    /// <para><b>⚠️ 本 Postfix 有一条硬约束：绝不能让调用方的惰性序列塌掉。</b>
    /// <c>TradeSession.deal.AllTradeables</c> 是由惰性序列（LINQ/迭代器）构建的，
    /// **序列一旦中断抛异常，整张交易列表就是空的** —— 表现是"连白银都不显示"，
    /// 看起来与 mod 毫无关系，极难定位。
    /// 而 C# **不允许在 <c>try/catch</c> 里 <c>yield return</c>**，所以这里必须
    /// **先在 try/catch 里把我们的追加物化成 <c>List</c>，再统一 yield**。
    /// 这样本方法在任何输入下的最坏结果都只是"少显示我们那部分"，不会波及原版内容；
    /// 同时把异常原文打进日志（<c>ErrorOnce</c>），下一次测试就能直接看到真凶。</para>
    ///
    /// <para><b>因此 3.0 的整层注入机制被删除</b>：<c>TradeDS_Helper</c>（提款造
    /// unspawned Thing → 注入 Tradeable → 关窗回滚 → 成交后退账）+
    /// <c>Patch_DialogTrade</c>（PostOpen/CacheTradeables/PostClose/TryExecute 四个补丁）。
    /// 它们当初存在**只是因为账本里的东西不是真 Thing**、原版列表里根本没有它们。
    /// （用户环境未安装 DTI，本次故障与 DTI 时序无关。）</para>
    ///
    /// <para><b>顺带发现</b>：<c>Pawn_TraderTracker.ColonyThingsWillingToBuy:124</c>
    /// **原本就在枚举** <c>map.listerBuildings.AllColonistBuildingsOfType&lt;IHaulSource&gt;()</c>
    /// 并 yield <c>GetDirectlyHeldThings()</c> ⇒ 访客/商队交易**零补丁**就能看见我们的容器
    /// （且原版那里也只取直接内容物，与本节的做法一致）。
    /// 只有轨道信标这条（走格子）需要补。</para>
    /// </summary>
    [HarmonyPatch(typeof(TradeUtility), "AllLaunchableThingsForTrade")]
    static class Patch_AllLaunchableThingsForTrade
    {
        static IEnumerable<Thing> Postfix(IEnumerable<Thing> __result, Map map, ITrader trader)
        {
            // 1) 原版结果原样转发。不把它包进 try/catch —— 原版自己的异常应当照原样暴露。
            foreach (Thing thing in __result)
                yield return thing;

            if (map == null) yield break;

            // 2) 我们的追加部分：先在 try/catch 里物化，再 yield（见类注释的硬约束）。
            List<Thing> additions = null;
            try
            {
                additions = new List<Thing>();
                var contents = new List<Thing>();
                HaulSourceContents.CollectDirect(map, contents);
                for (int i = 0; i < contents.Count; i++)
                {
                    Thing t = contents[i];
                    if (t == null || t.def == null || t.Destroyed) continue;

                    // 直接内容物不会是 pawn；防御 PlayerSellableNow 在 trader == null 时读 trader.Faction
                    // （FactionDialogMaker.AmountSendableSilver / ColonyHasEnoughSilver 都不传 trader）。
                    if (t is Pawn) continue;

                    // 与原版容器分支同一把尺子
                    if (!TradeUtility.PlayerSellableNow(t, trader)) continue;
                    additions.Add(t);
                }
            }
            catch (Exception e)
            {
                // 只报一次，避免刷屏；带上异常原文，下一次测试即可定位真凶。
                Log.ErrorOnce("[DigitalStorage] 追加容器内容物到交易列表时抛异常"
                    + "（已降级为只显示原版内容，交易列表不会因此变空）: " + e, 0x5D51A);
                additions = null;
            }

            if (additions == null) yield break;
            for (int i = 0; i < additions.Count; i++)
                yield return additions[i];
        }
    }
}
