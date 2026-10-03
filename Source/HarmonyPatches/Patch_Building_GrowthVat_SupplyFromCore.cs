using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>生育舱（<c>Building_GrowthVat</c>）的隔空投料。</b>
    ///
    /// <para><b>断在哪</b>：喂生育舱的原版路径是 <c>WorkGiver_HaulToGrowthVat</c>，
    /// 它的候选来源是 <c>GenClosest.ClosestThingReachable(…, FoodSourceNotPlantOrTree, …)</c>
    /// —— 和亚核心扫描仪一样**没传 <c>lookInHaulSources</c>**（默认 false，<c>GenClosest.cs:40</c>），
    /// 只看本图已 Spawned 的散落物。本 mod 的内容物是容器里未 Spawned 的真实 Thing
    /// （硬约束：内容物绝不进 <c>listerThings</c>）⇒ 核心里堆满食物，生育舱照样饿着孩子；
    /// 而玩家丢在地上的食物会在 15 tick 内被自动收纳吸回核心。</para>
    ///
    /// <para><b>本补丁做两件事（都只在"原版那条腿走不通"时才起作用）</b>：</para>
    /// <list type="number">
    /// <item><b>胚胎送进舱</b>：玩家在 gizmo 里点选胚胎后，原版要派 pawn 把它搬进来
    ///   （<c>WorkGiver_HaulToGrowthVat</c> 的 <c>selectedEmbryo</c> 分支）。胚胎是**物品类**，
    ///   会被自动收纳吞进核心（<c>HumanEmbryo</c> 是 <c>ThingCategory.Item</c>），
    ///   于是那趟搬运既找不到目标、玩家也可能选不到它。这里在"玩家已点选、胚胎还在核心"时
    ///   直接送进 <c>innerContainer</c>，随后原版自己的 <c>TryGrowEmbryo</c> 会开始孕育。</item>
    /// <item><b>营养从核心补</b>：把缺的那点营养用核心里的食物补上（直塞 <c>innerContainer</c>）。
    ///   取多少完全照抄原版：<c>count = ceil(缺口 / 单品营养)</c>，且**单品营养不得超过缺口**
    ///   （原版 <c>FindNutrition</c> 的 validator 就是这条）；补到原版自己那道"还缺不超过 2.5"的
    ///   闸门以下就停 —— 与 <c>WorkGiver_HaulToGrowthVat.NutritionBuffer = 2.5f</c> 同一个数，
    ///   免得把舱塞到 10 营养满格。</item>
    /// </list>
    ///
    /// <para><b>挑哪一件不发明规则</b>：能不能进由原版的三道闸回答（不是禁用的 / 单品营养不超过缺口 /
    /// <c>CanAcceptNutrition</c> = 玩家给这台舱设的食物过滤器）；取谁按「就近核心优先」，
    /// 与 <see cref="Patch_Building_SubcoreScanner_SupplyFromCore"/> 同一口径。
    /// 已被别人预订的料不抢（原版搬运工 / 本 mod 派出去的取料 job），
    /// 有搬运工在路上（舱被预约）时这一轮整个不动手。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_Building_GrowthVat_SupplyFromCore
    {
        /// <summary>原版 <c>WorkGiver_HaulToGrowthVat.NutritionBuffer</c> 同一个数：存量 ≥ 7.5 就不派活。</summary>
        private const float NutritionSlack = 2.5f;

        /// <summary>单轮补料最多搬几次（防御性上限：正常 1~2 次就补满）。</summary>
        private const int MaxMovesPerPass = 20;

        /// <summary>
        /// <c>Building_GrowthVat.Tick</c> 是 <c>protected override</c>，且基类 <c>Building</c> 也有同名虚方法
        /// —— 用 <c>DeclaredMethod</c> 钉死本类自己声明的那个（挂错目标会静默无效）。
        /// </summary>
        private static MethodBase TargetMethod()
        {
            return AccessTools.DeclaredMethod(typeof(Building_GrowthVat), "Tick", Type.EmptyTypes)
                ?? AccessTools.Method(typeof(Building_GrowthVat), "Tick", Type.EmptyTypes);
        }

        private static void Postfix(Building_GrowthVat __instance)
        {
            if (__instance == null || !__instance.Spawned) return;

            // 相位节流：每 15 tick 真扫一次核心（所有闸门都在这之后才跑）。
            if ((Find.TickManager.TicksGame + __instance.thingIDNumber) % 15 != 0) return;

            Map map = __instance.Map;
            if (map == null) return;

            ReservationManager resMgr = map.reservationManager;
            if (resMgr != null && resMgr.IsReservedByAnyoneOf(__instance, Faction.OfPlayer)) return;

            // 胚胎与营养无关（没营养也照样该把胚胎送进去），先做。
            DeliverEmbryoFromCore(__instance);

            if (__instance.NutritionNeeded > NutritionSlack)
                SupplyNutrition(__instance, map, resMgr);
        }

        /// <summary>
        /// 玩家已点选、但还在核心里的胚胎：直接送进舱内（原版要派 pawn 搬，而胚胎会被自动收纳吞掉）。
        /// 地上的 / 别的舱里的胚胎留给原版那条腿，我们不抢。
        /// </summary>
        private static void DeliverEmbryoFromCore(Building_GrowthVat vat)
        {
            HumanEmbryo embryo = vat.selectedEmbryo;
            if (embryo == null || embryo.Destroyed) return;
            if (vat.innerContainer.Contains(embryo)) return;
            if (!(embryo.ParentHolder is Building_StorageCore)) return;

            CoreSupply.TakeAndPlace(embryo, 1, taken => vat.innerContainer.TryAddOrTransfer(taken));
        }

        /// <summary>
        /// 从核心里补营养，直到原版那道「还缺不超过 2.5」的闸门之下（或核心凑不出来为止）。
        /// </summary>
        private static void SupplyNutrition(Building_GrowthVat vat, Map map, ReservationManager resMgr)
        {
            List<Thing> candidates = new List<Thing>();

            for (int move = 0; move < MaxMovesPerPass && vat.NutritionNeeded > NutritionSlack; move++)
            {
                float need = vat.NutritionNeeded;
                if (need <= 0f) return;

                // 每轮重新收集：上一轮整堆搬走后，候选表里的引用已经不是核心内容物了。
                candidates.Clear();
                CoreSupply.GatherContents(map, vat.Position, candidates);

                Thing pick = null;
                int pickCount = 0;

                for (int i = 0; i < candidates.Count; i++)
                {
                    Thing t = candidates[i];
                    if (t == null || t.Destroyed) continue;
                    // 只认核心里的（上一轮搬走的会被这条挡掉，顺带防重复计数）
                    if (!(t.ParentHolder is Building_StorageCore)) continue;
                    if (t.def == null) continue;
                    if (t.IsForbidden(Faction.OfPlayer)) continue;
                    // 已被别人预订的料不抢（原版搬运工 / 本 mod 派出去的取料 job）
                    if (resMgr != null && resMgr.IsReservedByAnyoneOf(t, Faction.OfPlayer)) continue;
                    // 原版闸门：玩家给这台舱设的食物过滤器
                    if (!vat.CanAcceptNutrition(t)) continue;

                    float nutrition = t.def.GetStatValueAbstract(StatDefOf.Nutrition);
                    if (nutrition <= 0f) continue;
                    // 原版闸门：单品营养不得超过缺口（否则原版的 FindNutrition 根本不会选它）
                    if (nutrition > need) continue;

                    pick = t;
                    // 原版同一行：ceil(缺口 / 单品营养)，再被这一堆的数量兜住
                    pickCount = Math.Min(t.stackCount, Mathf.CeilToInt(need / nutrition));
                    break;
                }

                if (pick == null || pickCount <= 0) return;

                if (!CoreSupply.TakeAndPlace(pick, pickCount, taken => vat.innerContainer.TryAddOrTransfer(taken)))
                    return; // 放不进去（货已由 TakeAndPlace 还给核心）⇒ 收工，等等看
            }
        }
    }
}
