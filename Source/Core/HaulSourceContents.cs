using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DigitalStorage.Core
{
    /// <summary>
    /// <b>4.0 的账本替身</b>：直接遍历地图上的 haul source 容器内容物。
    ///
    /// <para>3.0 里这一层是 <c>CoreLedger</c>（<c>(def, stuff)</c> → 数量的纯数据）。
    /// 4.0 里容器内容物是**真实的 Thing**，住在 <c>ThingOwner</c> 里，
    /// 所以"查库存"就是"遍历容器"——不需要索引、不需要 ghost、不会失同步。</para>
    ///
    /// <para><b>收集方式与原版对齐</b>：统一用
    /// <c>ThingOwnerUtility.GetAllThingsRecursively(source, list)</c>（递归进 holder 树），
    /// 和 <c>WorkGiver_DoBill.cs:487</c> 的做法一致 —— 这样嵌套容器（容器里的容器）
    /// 也能被看见，且不会漏掉 <c>IThingHolder</c> 链上的东西。</para>
    ///
    /// <para><b>为什么不用 <c>listerThings</c></b>：容器内容物未 Spawned，本来就不在里面。
    /// 而硬把它塞进去是旧 <c>GhostThing</c> 的路 —— 未 Spawn 的东西被原版遍历到就 NRE 遍地。
    /// 详见 obsidian：<c>代码Wiki/csharp-api/容器内容物参与原版作业-ParentHolder返回Map.md</c></para>
    /// </summary>
    public static class HaulSourceContents
    {
        /// <summary>收集用的静态缓冲（这些方法都在"原版找不到 → 兜底"路径上，非热路径）。</summary>
        private static readonly List<Thing> tmpThings = new List<Thing>();
        private static readonly List<IHaulSource> tmpSources = new List<IHaulSource>();

        /// <summary>本图所有启用中的 haul source。返回的列表是复用的静态缓冲，别存起来。</summary>
        public static List<IHaulSource> EnabledSources(Map map)
        {
            tmpSources.Clear();
            if (map == null) return tmpSources;

            List<IHaulSource> all = map.haulDestinationManager?.AllHaulSourcesListForReading;
            if (all == null) return tmpSources;

            for (int i = 0; i < all.Count; i++)
            {
                IHaulSource s = all[i];
                if (s != null && s.HaulSourceEnabled) tmpSources.Add(s);
            }
            return tmpSources;
        }

        /// <summary>本图容器里有没有东西。</summary>
        public static bool AnyContent(Map map)
        {
            List<IHaulSource> sources = EnabledSources(map);
            for (int i = 0; i < sources.Count; i++)
            {
                ThingOwner held = sources[i].GetDirectlyHeldThings();
                if (held != null && held.Count > 0) return true;
            }
            return false;
        }

        /// <summary>把本图所有容器内容物收集进 <paramref name="outThings"/>（先清空）。</summary>
        public static void GatherAll(Map map, List<Thing> outThings)
        {
            outThings.Clear();
            List<IHaulSource> sources = EnabledSources(map);
            for (int i = 0; i < sources.Count; i++)
            {
                tmpThings.Clear();
                ThingOwnerUtility.GetAllThingsRecursively(sources[i], tmpThings);
                for (int j = 0; j < tmpThings.Count; j++)
                {
                    Thing t = tmpThings[j];
                    if (t != null && !t.Destroyed) outThings.Add(t);
                }
            }
            tmpThings.Clear();
        }

        /// <summary>
        /// 在容器里找 <paramref name="score"/> 最大且通过 <paramref name="validator"/> 的东西。
        /// 找不到返回 null。<paramref name="score"/> 可以是 null（等价于都取 0，返回第一个通过的）。
        /// </summary>
        public static Thing FindBest(Map map, Func<Thing, float> score, Predicate<Thing> validator)
        {
            if (map == null || validator == null) return null;

            Thing best = null;
            float bestScore = float.MinValue;
            List<IHaulSource> sources = EnabledSources(map);

            for (int i = 0; i < sources.Count; i++)
            {
                tmpThings.Clear();
                ThingOwnerUtility.GetAllThingsRecursively(sources[i], tmpThings);
                for (int j = 0; j < tmpThings.Count; j++)
                {
                    Thing t = tmpThings[j];
                    if (t == null || t.Destroyed || !validator(t)) continue;

                    float s = (score != null) ? score(t) : 0f;
                    if (best == null || s > bestScore)
                    {
                        best = t;
                        bestScore = s;
                    }
                }
            }
            tmpThings.Clear();
            return best;
        }

        /// <summary>容器里某 def 的总数量。</summary>
        public static int CountOf(Map map, ThingDef def)
        {
            if (map == null || def == null) return 0;

            int total = 0;
            List<IHaulSource> sources = EnabledSources(map);
            for (int i = 0; i < sources.Count; i++)
            {
                tmpThings.Clear();
                ThingOwnerUtility.GetAllThingsRecursively(sources[i], tmpThings);
                for (int j = 0; j < tmpThings.Count; j++)
                {
                    Thing t = tmpThings[j];
                    if (t != null && !t.Destroyed && t.def == def) total += t.stackCount;
                }
            }
            tmpThings.Clear();
            return total;
        }

        /// <summary>
        /// 把 <paramref name="t"/>（住在容器里）取出 <paramref name="count"/> 个，
        /// 落到 <paramref name="pawn"/> 脚下并返回落地的那个 Thing。失败返回 null（物品留在原处）。
        ///
        /// <para><b>为什么是"取出放脚下"而不是"让原版自己走到容器里拿"</b>：
        /// 消耗这条链的原版 job（<c>JobDriver_Refuel</c> / <c>JobDriver_FixBrokenDownBuilding</c> /
        /// <c>JobDriver_TendPatient</c> / <c>JobDriver_Ingest</c>）**没有 <c>canGotoSpawnedParent</c>**
        /// —— 原版只给 <c>JobDriver_DoBill:121</c> / <c>HaulToCell:114</c> / <c>HaulToContainer:137</c>
        /// 等少数几条装了它。所以对这几条链，原版根本走不到容器内容物，
        /// 只能由我们把人参换成"脚边一个真东西"，原版 job 语义一字不改。
        /// （详见 obsidian 4.0-乙-原版可见性判定表 第八节）</para>
        ///
        /// <para><c>MarkWithdrawn</c>：给自动收纳一个 300 tick 的保护窗口，
        /// 免得刚取出来就被瞬移吸回去，形成"取—吸"死循环。</para>
        /// </summary>
        public static Thing ExtractToFeet(Thing t, int count, Pawn pawn)
        {
            if (t == null || t.Destroyed || pawn == null) return null;
            Map map = pawn.Map;
            if (map == null) return null;
            if (count <= 0) return null;

            IThingHolder holder = t.ParentHolder as IThingHolder;
            if (holder == null) return null;
            ThingOwner owner = holder.GetDirectlyHeldThings();
            if (owner == null || !owner.Contains(t)) return null;

            // 只在容器里还有同 def 的其它堆时才有意义；先算实际能取多少。
            int take = Math.Min(count, t.stackCount);
            if (take <= 0) return null;

            Thing taken;
            if (take >= t.stackCount)
            {
                // 整堆取走
                owner.Remove(t);
                taken = t;
            }
            else
            {
                // 拆一部分出来。SplitOff 不触碰 owner，原堆留在容器里且数量已减。
                taken = t.SplitOff(take);
            }
            if (taken == null) return null;

            if (!GenPlace.TryPlaceThing(taken, pawn.Position, map, ThingPlaceMode.Near, null, null, default))
            {
                // 脚下放不下 → 退回容器，避免物品消失
                if (!owner.TryAdd(taken, true))
                    GenPlace.TryPlaceThing(taken, pawn.Position, map, ThingPlaceMode.Near);
                return null;
            }

            Components.CompAutoIngest.MarkWithdrawn(taken);
            return taken;
        }
    }
}
