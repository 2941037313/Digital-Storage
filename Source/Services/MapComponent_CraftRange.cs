using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Services
{
    /// <summary>
    /// <b>制作代理的扫描范围显示</b>：把开了"显示范围"开关的每一台制作代理的 13×13 方形画个边线。
    ///
    /// <para>为什么用 <see cref="MapComponent"/> 而不是给建筑写 <c>DrawExtraSelectionOverlays</c>：
    /// 后者只在**选中**时画，而用户要的是"gizmo 开着就一直显示"（用来摆工作台）。
    /// <c>Map</c> 会自动实例化所有非抽象 <c>MapComponent</c> 子类（<c>Verse\Map.cs:713</c>），
    /// 所以本类不需要任何 Def；<see cref="MapComponentDraw"/> 是绘制时机（不是 Update）。</para>
    ///
    /// <para>成本：每台开着的代理 169 个格子画边线，格子表在 comp 里缓存（建筑不动就不重建）。</para>
    /// </summary>
    public class MapComponent_CraftRange : MapComponent
    {
        /// <summary>与界面稿一致的强调金，带一点透明。</summary>
        private static readonly Color RangeColor = new Color(0.788f, 0.663f, 0.380f, 0.85f);

        public MapComponent_CraftRange(Map map)
            : base(map)
        {
        }

        public override void MapComponentDraw()
        {
            base.MapComponentDraw();
            if (map == null) return;

            List<Building> all = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < all.Count; i++)
            {
                Building b = all[i];
                if (b == null || b.Destroyed || !b.Spawned) continue;

                CompBillAutomation comp = b.TryGetComp<CompBillAutomation>();
                if (comp == null || !comp.ShowRange) continue;

                GenDraw.DrawFieldEdges(comp.RangeCellsForDrawing(), RangeColor);
            }
        }
    }
}
