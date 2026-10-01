using System.Collections.Generic;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 兼容层：把核心容器里的内容物接进 <c>SlotGroup.HeldThings</c> / <c>HeldThingsCount</c>。
    ///
    /// <para><b>为什么需要</b>：Phinix（<c>TradeWindow.cs:106</c>）与 Phinix 红包
    /// （<c>RedPacketTab.cs:157</c>）的**默认分支**（它们的 <c>AllItemsTradable</c> 是无初值 bool，
    /// 默认 false）是
    /// <c>maps.SelectMany(m =&gt; m.haulDestinationManager.AllGroups).SelectMany(g =&gt; g.HeldThings)</c>
    /// —— 它们不认 <c>IHaulSource</c>，只认 <c>SlotGroup</c>。而原版 <c>SlotGroup.HeldThings</c>
    /// 按 <c>parent.AllSlotCellsList()</c> **逐格**枚举（<c>SlotGroup.cs:18-36</c>），
    /// ThingOwner 里的内容物永远看不见。</para>
    ///
    /// <para><b>做法</b>：核心已经是零格子的 <c>ISlotGroupParent</c>（见 <c>Building_StorageCore</c>），
    /// 于是它的 <c>SlotGroup</c> 自动进 <c>allGroupsInOrder</c>；这里只把容器内容物**追加**进那两个枚举。
    /// 一处补丁覆盖所有遍历 <c>AllGroups</c> 的消费者 —— 包括原版 <c>ResourceCounter</c>
    /// （<c>:131-134</c>），而 <c>Patch_ResourceCounter_HaulSources:52</c> 早就写了
    /// <c>if (source is ISlotGroupParent) continue;</c> 防双算 ⇒ 资源读数由原版算、数值不变。</para>
    ///
    /// <para><b>过滤与原版逐字对齐</b>：<c>def.EverStorable(willMinifyIfPossible: false)</c>
    /// （<c>SlotGroup.cs:29</c>），否则两边的"件数"会对不上。</para>
    ///
    /// <para><b>仍未打通</b>：<c>listerThings.AllThings</c> 那条分支（Phinix 打开
    /// <c>AllItemsTradable</c> 时）—— 未 Spawn 的物品绝不能再进 <c>listerThings</c>
    /// （旧 GhostThing 的 NRE 路），硬约束不变。</para>
    /// </summary>
    [HarmonyPatch(typeof(SlotGroup), nameof(SlotGroup.HeldThings), MethodType.Getter)]
    internal static class Patch_SlotGroup_HeldThings
    {
        [HarmonyPostfix]
        private static void Postfix(SlotGroup __instance, ref IEnumerable<Thing> __result)
        {
            Building_StorageCore core = CoreOf(__instance);
            if (core == null) return;
            __result = WithContents(__result, core);
        }

        private static IEnumerable<Thing> WithContents(IEnumerable<Thing> original, Building_StorageCore core)
        {
            // 原版那半边（零格子时为空）先照原样走完，语义不变
            if (original != null)
            {
                foreach (Thing t in original) yield return t;
            }

            ThingOwner held = core.GetDirectlyHeldThings();
            if (held == null) yield break;

            for (int i = 0; i < held.Count; i++)
            {
                Thing t = held[i];
                if (t == null || t.Destroyed || t.def == null) continue;
                if (!t.def.EverStorable(willMinifyIfPossible: false)) continue; // 与原版同款过滤
                yield return t;
            }
        }

        internal static Building_StorageCore CoreOf(SlotGroup group)
        {
            return (group == null) ? null : (group.parent as Building_StorageCore);
        }
    }

    /// <summary>
    /// <c>HeldThingsCount</c> 是独立的格子循环（<c>SlotGroup.cs:38-58</c>），不会走 <c>HeldThings</c>
    /// ⇒ 必须单独补，否则"件数"与"列表"两个读数会漂移。
    /// </summary>
    [HarmonyPatch(typeof(SlotGroup), nameof(SlotGroup.HeldThingsCount), MethodType.Getter)]
    internal static class Patch_SlotGroup_HeldThingsCount
    {
        [HarmonyPostfix]
        private static void Postfix(SlotGroup __instance, ref int __result)
        {
            Building_StorageCore core = Patch_SlotGroup_HeldThings.CoreOf(__instance);
            if (core == null) return;

            ThingOwner held = core.GetDirectlyHeldThings();
            if (held == null) return;

            for (int i = 0; i < held.Count; i++)
            {
                Thing t = held[i];
                if (t == null || t.Destroyed || t.def == null) continue;
                if (!t.def.EverStorable(willMinifyIfPossible: false)) continue;
                __result++;
            }
        }
    }
}
