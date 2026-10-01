using System.Collections.Generic;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 4.0 拱心石：把 IHaulSource 持有的内容物算进地图资源计数器。
    ///
    /// <para><b>为什么需要</b>：原版 <c>ResourceCounter.UpdateResourceCounts</c> 只遍历
    /// <c>map.haulDestinationManager.AllGroupsListForReading</c>（<c>SlotGroup</c>，由
    /// <c>ISlotGroupParent</c> 喂养）。以 <c>ThingOwner</c> 持有内容物的建筑没有 SlotGroup
    /// ⇒ 内容物一律不计。**原版自己的 <c>Building_Bookcase</c> / <c>Building_OutfitStand</c>
    /// 也不是 <c>ISlotGroupParent</c>，所以它们里面装的东西本来就不进资源读数** —— 我们不是特例。</para>
    ///
    /// <para><b>收益面</b>：一条加性 Postfix 换来 12 个原版系统同时正确（全部经
    /// <c>map.resourceCounter</c> 取数）：
    /// 左上角资源读数（<c>ResourceReadout</c> / <c>Listing_ResourceReadout</c>）、
    /// 食物与婴儿食物警报（<c>Alert_LowFood</c> / <c>Alert_LowBabyFood</c>）、
    /// 药品警报（<c>Alert_LowMedicine</c>）、建造选材与材料负担（<c>Designator_Build</c> ×5）、
    /// 染料（<c>Designator_Paint</c> / <c>Dialog_StylingStation</c>）、出门带粮（<c>JobGiver_PackFood</c>）、
    /// <b>bill 的 ×N 计数</b>（<c>RecipeWorkerCounter</c> / <c>_ButcherAnimals</c> / <c>_MakeStoneBlocks</c>）、
    /// 过滤器 UI 数量（<c>Listing_TreeThingFilter</c>）、暴食（<c>MentalStateWorker_BingingFood</c>）、
    /// 任务生成可及性（<c>PlayerItemAccessibilityUtility</c>）。</para>
    ///
    /// <para><b>兼容性</b>：Vehicle Map Framework 也 Postfix 同一方法
    /// （<c>Patch_ResourceCounter_UpdateResourceCounts</c>），它把子地图的 SlotGroup 内容加进来；
    /// 两者都是往同一个 <c>countedAmounts</c> 上做加法 ⇒ 可交换 ⇒ 兼容。</para>
    ///
    /// <para><b>防重复计数</b>：跳过同时是 <c>ISlotGroupParent</c> 的 haul source ——
    /// 那种情况原版的 SlotGroup 循环已经算过一遍。已知同类实现：VMF <c>Building_Hatch</c>、
    /// ASF <c>ThingClass</c>。</para>
    /// </summary>
    [HarmonyPatch(typeof(ResourceCounter), "UpdateResourceCounts")]
    public static class Patch_ResourceCounter_HaulSources
    {
        [HarmonyPostfix]
        public static void Postfix(Map ___map, Dictionary<ThingDef, int> ___countedAmounts)
        {
            if (___map == null || ___countedAmounts == null) return;

            List<IHaulSource> sources = ___map.haulDestinationManager?.AllHaulSourcesListForReading;
            if (sources == null) return;

            for (int i = 0; i < sources.Count; i++)
            {
                IHaulSource source = sources[i];
                if (source == null || !source.HaulSourceEnabled) continue;

                // 已经由原版 SlotGroup 循环计过的，不要重复加。
                // ① 本身是 ISlotGroupParent 的（VMF Building_Hatch、ASF ThingClass 等）
                // ② 我们的核心：内容物由「惰性替身」的零格子 SlotGroup 被原版算进去
                //    （见 Compatibility/CoreSlotGroupAdapter —— 核心自己**不**实现 ISlotGroupParent，
                //     所以下面那道 `source is ISlotGroupParent` 判不到它，必须显式补这一条，
                //     否则资源读数会翻倍）
                if (source is ISlotGroupParent) continue;
                if (source is Building_StorageCore core && core.CompatSlotGroupRegistered) continue;

                ThingOwner held = source.GetDirectlyHeldThings();
                if (held == null) continue;

                for (int j = 0; j < held.Count; j++)
                {
                    Thing t = held[j];
                    if (t == null || t.def == null) continue;

                    // 与原版 UpdateResourceCounts 完全同构：先取 minified 内层。
                    Thing inner = t.GetInnerIfMinified();
                    if (inner == null || inner.def == null) continue;
                    if (!inner.def.CountAsResource) continue;
                    if (!ShouldCount(inner)) continue;

                    int cur;
                    ___countedAmounts.TryGetValue(inner.def, out cur);
                    ___countedAmounts[inner.def] = cur + inner.stackCount;
                }
            }
        }

        /// <summary>
        /// 原版 <c>ResourceCounter.ShouldCount</c> 的副本（它是 private）。
        /// 刻意不反射调用：避免 vanilla 改名/移除后运行期抛异常，语义只有两行，重复成本可忽略。
        /// </summary>
        private static bool ShouldCount(Thing t)
        {
            if (t.IsNotFresh()) return false;
            if (t.SpawnedOrAnyParentSpawned && t.PositionHeld.Fogged(t.MapHeld)) return false;
            return true;
        }
    }
}
