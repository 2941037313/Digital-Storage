using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Performance
{
    /// <summary>
    /// <b>框选一堆东西之后，每帧的无裁剪绘制</b>（原版热点，实测卡顿来源之一）。
    ///
    /// <para><b>原版代价</b>：<c>MapInterface.MapInterfaceUpdate()</c> 每帧调
    /// <c>SelectionDrawer.DrawSelectionOverlays()</c>，它对 <c>Find.Selector.SelectedObjects</c> 里
    /// **每一个**选中物调 <c>DrawSelectionBracketFor</c>，而后者是
    /// <b>4 次 <c>Graphics.DrawMesh</c>（不是实例化合批）</b>，且<b>没有任何视野裁剪</b>
    /// （源码 <c>SelectionDrawer.cs:40-120</c>）。选中 200 个（原版 <c>Selector.SelectInternal</c>
    /// 有 <c>selected.Count &lt; 200</c> 的硬上限）就是 <b>每帧 800 次 DrawMesh</b>；
    /// 再加上 <c>StorageGroupUtility.DrawSelectionOverlaysFor</c> 会给**整个储存组**的成员逐个画括号
    /// （那条路绕过 200 上限，储存组建越大越糟）⇒ 每帧上千次 DrawMesh，帧率直接掉。</para>
    ///
    /// <para><b>本补丁</b>：视野（外扩若干格）之外的括号**根本不可能被看见**，直接不画。
    /// 这是纯裁剪，不改任何可见结果：<c>DrawSelectionBracketFor</c> 只画以物体自身位置为中心的 4 片括号，
    /// 中心不在视野内 ⇒ 没东西可看。外扩量按本体尺寸给（原版最大建筑约 20×20 格，
    /// 中心离屏 12 格时括号角仍在屏内）。</para>
    ///
    /// <para><b>为什么挂在 <c>DrawSelectionBracketFor</c> 而不是替换 <c>DrawSelectionOverlays</c></b>：
    /// 后者要访问 <c>SelectionDrawer</c> 的私有 <c>drawnStorageGroupBrackets</c> 去清"本帧已画的储存组"，
    /// 漏清会让原版的储存组覆盖层第一帧之后再也不画（静默错）。挂在画括号这一步则
    /// 连储存组那条路（<c>StorageGroupUtility.DrawSelectionOverlaysFor</c> 也调它）一起受益。</para>
    ///
    /// <para>未 spawn 的物（被搬运中/在容器里）一律**不裁剪**交回原版 —— 原版对它们
    /// 有专门分支（<c>CustomRectForSelector</c> / <c>SpawnedParentOrMe is Pawn</c> / <c>DrawPosHeld</c>），
    /// 不插手就不会有行为差异。</para>
    /// </summary>
    [HarmonyPatch(typeof(SelectionDrawer), "DrawSelectionBracketFor", new[] { typeof(object), typeof(Material) })]
    internal static class Patch_SelectionDrawer_BracketCull
    {
        /// <summary>本体最大约 20×20 格，中心离屏 12 格时括号角仍可能在屏内，留足。</summary>
        private const int ThingMargin = 12;

        /// <summary>区域/计划的括号是逐格描边，相邻一格就可能把边画进屏内。</summary>
        private const int CellMargin = 2;

        private static bool Prefix(object obj)
        {
            if (!Settings.DigitalStorageSettings.perfOptimizationsEnabled) return true;
            if (Find.CurrentMap == null) return true;

            CellRect view = Find.CameraDriver.CurrentViewRect;

            Thing thing = obj as Thing;
            if (thing != null)
            {
                // 未 spawn（搬运中 / 容器内 / 进料口）：原版有专门分支，别插手
                if (!thing.Spawned) return true;

                // 被搬运/装进建筑时真正画在父级身上，用父级位置判断才不会误裁
                Thing anchor = thing.SpawnedParentOrMe ?? thing;
                Vector3 pos = anchor.DrawPos;
                IntVec3 cell = new IntVec3(Mathf.RoundToInt(pos.x), 0, Mathf.RoundToInt(pos.z));
                return view.ExpandedBy(ThingMargin).Contains(cell);
            }

            Zone zone = obj as Zone;
            if (zone != null) return AnyCellInView(zone.Cells, view);

            Plan plan = obj as Plan;
            if (plan != null) return AnyCellInView(plan.Cells, view);

            return true;
        }

        /// <summary>区域/计划只要有一格在视野内就得画（早退，通常第一次就命中）。</summary>
        private static bool AnyCellInView(List<IntVec3> cells, CellRect view)
        {
            CellRect grown = view.ExpandedBy(CellMargin);
            for (int i = 0; i < cells.Count; i++)
            {
                if (grown.Contains(cells[i])) return true;
            }
            return false;
        }
    }
}
