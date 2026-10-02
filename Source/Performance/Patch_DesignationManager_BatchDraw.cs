using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace DigitalStorage.Performance
{
    /// <summary>
    /// <b>标记（designation）批绘的增量维护</b> —— 把原版"每帧全量重建矩阵"变成 O(改动量)。
    ///
    /// <para><b>原版代价（这才是"标记太多就卡"的主因）</b>：格子类标记（挖掘/平整/铺地板…）走的是
    /// 实例化合批绘制，合批本身没问题，问题在矩阵缓存的重建时机：</para>
    /// <list type="number">
    /// <item><c>IndexDesignation</c>（增）和 <c>RemoveDesignation</c>（删）都会调
    /// <c>DirtyCellDesignationsCache(def)</c> ⇒ <c>designationMatriciesDirty[def] = true</c>；</item>
    /// <item><c>DrawDesignations</c> 每帧跑一次，发现 dirty 就调
    /// <c>CalculateCellDesignationDrawMatricies(def)</c> —— 而这个方法是
    /// <b>把该 def 在图上的**全部**标记重算一遍 <c>Matrix4x4.TRS(DrawLoc())</c></b>，
    /// 不是因为只有几个格子变了就少算。</item>
    /// </list>
    /// <para>于是：图上挂着 2 万个挖掘标记时，只要有**一个**被抹掉/新增，
    /// 本帧就要重算 2 万个矩阵（实测量级每次 3–5 ms），而代理建筑在扫图时**每 tick 都在抹标记**
    /// ⇒ 每帧都 dirty ⇒ <b>持续每帧 3–5 ms 的无用功</b>，一直卡到图被扫干净。
    /// 标记越多越卡，正好对上"标记太多东西会出现"。</para>
    ///
    /// <para><b>本补丁</b>：接管 <c>DrawDesignations</c>，自己维护一份与
    /// <c>designationsByDef[def]</c> 同步的矩阵表：</para>
    /// <list type="bullet">
    /// <item>增：若 <c>Count == list.Count - 1</c> 且 <c>list[末尾] == 新标记</c>，只算这一个矩阵并追加；</item>
    /// <item>删：靠 <c>Designation → 槽位</c> 字典 O(1) 定位，末位换填（顺序无所谓，都是同一个四边形贴图）；</item>
    /// <item>任何一处对不上（数量不等 / 槽位里不是它 / hook 抛异常）⇒ <c>NeedResync</c>，
    /// 下一帧从 <c>designationsByDef</c> 全量重建一次 —— 也就是**最坏退化成原版行为，不会画错**。</item>
    /// </list>
    ///
    /// <para><b>绘制部分逐字对齐原版</b>（<c>DesignationManager.DrawDesignations</c>）：
    /// 同样的 <c>DefDatabase&lt;DesignationDef&gt;.AllDefsListForReading</c> 顺序、
    /// 同样的 <c>CellRect = CurrentViewRect.ExpandedBy(3)</c>、
    /// 同样的 <c>drawMeshInstanced(plane10, 0, iconMat, batch, 1023, block, ShadowCastingMode.Off, receiveShadows: true, 0)</c>、
    /// 以及**未开批绘的 def（Thing 目标、<c>PaintFloor</c> 这种带 per-标记颜色的）依旧逐格走
    /// <c>DesignationDraw()</c>** —— 所以颜色/顺序/层都跟原版一致。</para>
    ///
    /// <para>不支持 GPU 实例化的机器上原版本来就退回逐格路径，这里直接交回原版。</para>
    /// </summary>
    [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.DrawDesignations))]
    internal static class Patch_DesignationManager_BatchDraw
    {
        internal const int MatrixPerBatch = 1023;

        private static readonly ConditionalWeakTable<DesignationManager, MapCache> caches =
            new ConditionalWeakTable<DesignationManager, MapCache>();

        private static MaterialPropertyBlock block;

        /// <summary>hook 出过任何异常就永久停用本优化（交回原版，宁慢不错）。</summary>
        internal static bool Failed;

        internal static bool Active
        {
            get
            {
                return !Failed
                    && Settings.DigitalStorageSettings.perfOptimizationsEnabled
                    && SystemInfo.supportsInstancing;
            }
        }

        internal static MapCache CacheFor(DesignationManager mgr)
        {
            MapCache cache;
            if (!caches.TryGetValue(mgr, out cache))
            {
                cache = new MapCache();
                caches.Add(mgr, cache);
            }
            return cache;
        }

        internal static bool IsBatched(DesignationDef def)
        {
            return def != null && def.targetType == TargetType.Cell && def.shouldBatchDraw;
        }

        private static bool Prefix(DesignationManager __instance)
        {
            if (__instance == null || !Active) return true;
            try
            {
                Draw(__instance);
                return false;
            }
            catch (Exception ex)
            {
                Failed = true;
                Log.ErrorOnce("[DigitalStorage] 标记批绘优化异常，已永久交回原版路径：" + ex, 7711233);
                return true;
            }
        }

        internal static Matrix4x4 MatrixOf(Designation des)
        {
            return Matrix4x4.TRS(des.DrawLoc(), Quaternion.identity, Vector3.one);
        }

        private static void Draw(DesignationManager mgr)
        {
            MapCache cache = CacheFor(mgr);
            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(3);
            if (block == null) block = new MaterialPropertyBlock();

            List<DesignationDef> defs = DefDatabase<DesignationDef>.AllDefsListForReading;
            for (int d = 0; d < defs.Count; d++)
            {
                DesignationDef def = defs[d];
                List<Designation> list = mgr.designationsByDef[def];
                if (list == null || list.Count == 0) continue;

                if (!IsBatched(def))
                {
                    // 原版逐格路径：Thing 目标 / PaintFloor 这类每条自带颜色的标记
                    for (int i = 0; i < list.Count; i++)
                    {
                        Designation des = list[i];
                        if ((!des.target.HasThing || des.target.Thing.Map == mgr.map) && view.Contains(des.target.Cell))
                        {
                            des.DesignationDraw();
                        }
                    }
                    continue;
                }

                BatchCache bc = cache.For(def);
                if (bc.NeedResync || bc.Count != list.Count) bc.Resync(list);
                if (bc.Count == 0) continue;

                Material mat = def.iconMat;
                if (mat == null) continue;
                mat.enableInstancing = true;

                int n = bc.Count;
                int full = n / MatrixPerBatch;
                for (int i = 0; i < full; i++)
                {
                    Graphics.DrawMeshInstanced(MeshPool.plane10, 0, mat, bc.matrices[i], MatrixPerBatch,
                        block, ShadowCastingMode.Off, true, 0);
                }
                int rest = n % MatrixPerBatch;
                if (rest > 0)
                {
                    Graphics.DrawMeshInstanced(MeshPool.plane10, 0, mat, bc.matrices[full], rest,
                        block, ShadowCastingMode.Off, true, 0);
                }
            }
        }
    }

    /// <summary>每个 <see cref="DesignationManager"/>（= 每张图）一份缓存，随 manager 一起被 GC。</summary>
    internal sealed class MapCache
    {
        private readonly BatchCache[] byDefIndex = new BatchCache[DefDatabase<DesignationDef>.DefCount];

        public BatchCache For(DesignationDef def)
        {
            BatchCache bc = byDefIndex[def.index];
            if (bc == null)
            {
                bc = new BatchCache();
                byDefIndex[def.index] = bc;
            }
            return bc;
        }

        public BatchCache Get(DesignationDef def)
        {
            return byDefIndex[def.index];
        }
    }

    /// <summary>一个 DesignationDef 的矩阵表：与 <c>designationsByDef[def]</c> 同步，O(1) 增删。</summary>
    internal sealed class BatchCache
    {
        public int Count;

        public bool NeedResync;

        public readonly List<Matrix4x4[]> matrices = new List<Matrix4x4[]>();

        private readonly List<Designation> order = new List<Designation>();

        private readonly Dictionary<Designation, int> slot = new Dictionary<Designation, int>();

        public void OnAdded(DesignationManager mgr, Designation des)
        {
            if (NeedResync) return;
            try
            {
                List<Designation> list = mgr.designationsByDef[des.def];
                // 原版 IndexDesignation 就是 Append，所以末位必然是新标记；不是就说明我们漏了改动
                if (list.Count == 0 || list[list.Count - 1] != des || Count != list.Count - 1)
                {
                    NeedResync = true;
                    return;
                }
                Ensure(Count + 1);
                matrices[Count / Patch_DesignationManager_BatchDraw.MatrixPerBatch]
                        [Count % Patch_DesignationManager_BatchDraw.MatrixPerBatch] =
                    Patch_DesignationManager_BatchDraw.MatrixOf(des);
                order.Add(des);
                slot[des] = Count;
                Count++;
            }
            catch (Exception ex)
            {
                Patch_DesignationManager_BatchDraw.Failed = true;
                Log.ErrorOnce("[DigitalStorage] 标记批绘增量（增）异常，已停用优化：" + ex, 7711234);
            }
        }

        public void OnRemoved(DesignationManager mgr, Designation des)
        {
            if (NeedResync) return;
            try
            {
                int i;
                if (!slot.TryGetValue(des, out i) || i < 0 || i >= Count || order[i] != des)
                {
                    NeedResync = true;
                    return;
                }
                int last = Count - 1;
                if (i != last)
                {
                    Designation moved = order[last];
                    order[i] = moved;
                    slot[moved] = i;
                    matrices[i / Patch_DesignationManager_BatchDraw.MatrixPerBatch]
                            [i % Patch_DesignationManager_BatchDraw.MatrixPerBatch] =
                        matrices[last / Patch_DesignationManager_BatchDraw.MatrixPerBatch]
                                [last % Patch_DesignationManager_BatchDraw.MatrixPerBatch];
                }
                order.RemoveAt(last);
                slot.Remove(des);
                Count = last;
            }
            catch (Exception ex)
            {
                Patch_DesignationManager_BatchDraw.Failed = true;
                Log.ErrorOnce("[DigitalStorage] 标记批绘增量（删）异常，已停用优化：" + ex, 7711235);
            }
        }

        /// <summary>从原版列表全量重建 —— 就是原版那一遍重算，只在失同步时发生。</summary>
        public void Resync(List<Designation> list)
        {
            // 诊断：正常运行时这个计数应当**接近 0**（只有读档/失同步才重建）。
            // 若日志里"重同步/帧"很高，说明增量维护没接上，优化等于没做。
            DevDrawProfiler.Bump("重同步", 1);
            order.Clear();
            slot.Clear();
            Ensure(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                Designation des = list[i];
                order.Add(des);
                slot[des] = i;
                matrices[i / Patch_DesignationManager_BatchDraw.MatrixPerBatch]
                        [i % Patch_DesignationManager_BatchDraw.MatrixPerBatch] =
                    Patch_DesignationManager_BatchDraw.MatrixOf(des);
            }
            Count = list.Count;
            NeedResync = false;
        }

        private void Ensure(int needed)
        {
            while (matrices.Count * Patch_DesignationManager_BatchDraw.MatrixPerBatch < needed)
            {
                matrices.Add(new Matrix4x4[Patch_DesignationManager_BatchDraw.MatrixPerBatch]);
            }
        }
    }

    /// <summary>
    /// 增量维护的"增"侧：<c>IndexDesignation</c> 是**唯一**的索引入口
    /// （<c>AddDesignation</c> 和读档时的 <c>ExposeData</c> 都走它），挂这里就覆盖了全部新增路径。
    /// </summary>
    [HarmonyPatch(typeof(DesignationManager), "IndexDesignation")]
    internal static class Patch_DesignationManager_BatchDraw_Add
    {
        private static void Postfix(DesignationManager __instance, Designation designation)
        {
            if (!Patch_DesignationManager_BatchDraw.Active) return;
            if (designation == null || !Patch_DesignationManager_BatchDraw.IsBatched(designation.def)) return;
            Patch_DesignationManager_BatchDraw.CacheFor(__instance).For(designation.def).OnAdded(__instance, designation);
        }
    }

    /// <summary>增量维护的"删"侧：<c>RemoveDesignation</c> 是所有删除路径的唯一出口。</summary>
    [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.RemoveDesignation))]
    internal static class Patch_DesignationManager_BatchDraw_Remove
    {
        private static void Prefix(DesignationManager __instance, Designation des)
        {
            if (!Patch_DesignationManager_BatchDraw.Active) return;
            if (des == null || !Patch_DesignationManager_BatchDraw.IsBatched(des.def)) return;
            BatchCache bc = Patch_DesignationManager_BatchDraw.CacheFor(__instance).Get(des.def);
            if (bc == null) return;
            bc.OnRemoved(__instance, des);
        }
    }
}
