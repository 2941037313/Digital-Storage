using System.Collections.Generic;
using DigitalStorage.Compatibility;
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
    /// （<c>RedPacketTab.cs:157</c>）的**默认分支**（它们的 <c>AllItemsTradable</c> 是
    /// <c>Scribe_Values.Look(..., defaultValue: false)</c>）是
    /// <c>maps.SelectMany(m =&gt; m.haulDestinationManager.AllGroups).SelectMany(g =&gt; g.HeldThings)</c>
    /// —— 它们不认 <c>IHaulSource</c>，只认 <c>SlotGroup</c>。而原版 <c>SlotGroup.HeldThings</c>
    /// 按 <c>parent.AllSlotCellsList()</c> **逐格**枚举（<c>SlotGroup.cs:18-36</c>），
    /// ThingOwner 里的内容物永远看不见。</para>
    ///
    /// <para><b>接的是谁</b>：核心的**惰性替身** <see cref="CoreSlotGroupAdapter"/>
    /// （零格子、<c>HaulDestinationEnabled=false</c>、<c>Accepts=false</c>，
    /// 由 <c>Building_StorageCore</c> 在 SpawnSetup/DeSpawn 注册注销）。
    /// **不是核心自己** —— 核心自己实现 <c>ISlotGroupParent</c> 会被原版容器腿
    /// （<c>StoreUtility.cs:252</c>）当格子型储存跳过，那正是 2026-10-02 那次事故。</para>
    ///
    /// <para><b>过滤与原版逐字对齐</b>：<c>def.EverStorable(willMinifyIfPossible: false)</c>
    /// （<c>SlotGroup.cs:29</c>），否则两边的"件数"会对不上。</para>
    ///
    /// <para><b>仍未打通</b>：<c>listerThings.AllThings</c> 那条分支（Phinix 打开
    /// "允许交易不在储存区中的物品"时）—— 未 Spawn 的物品绝不能再进 <c>listerThings</c>
    /// （旧 GhostThing 的 NRE 路），硬约束不变。</para>
    /// </summary>
    [HarmonyPatch(typeof(SlotGroup), nameof(SlotGroup.HeldThings), MethodType.Getter)]
    internal static class Patch_SlotGroup_HeldThings
    {
        [HarmonyPostfix]
        private static void Postfix(SlotGroup __instance, ref IEnumerable<Thing> __result)
        {
            CoreSlotGroupAdapter adapter = AdapterOf(__instance);
            if (adapter == null) return;
            __result = WithContents(__result, adapter);
        }

        private static IEnumerable<Thing> WithContents(IEnumerable<Thing> original, CoreSlotGroupAdapter adapter)
        {
            // 原版那半边（零格子时为空）先照原样走完，语义不变
            if (original != null)
            {
                foreach (Thing t in original) yield return t;
            }

            Building_StorageCore core = adapter.Core;
            if (core == null) yield break;
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

        internal static CoreSlotGroupAdapter AdapterOf(SlotGroup group)
        {
            return (group == null) ? null : (group.parent as CoreSlotGroupAdapter);
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
            CoreSlotGroupAdapter adapter = Patch_SlotGroup_HeldThings.AdapterOf(__instance);
            Building_StorageCore core = (adapter == null) ? null : adapter.Core;
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
