using System.Collections.Generic;
using DigitalStorage.Components;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 可用存储核心的发现器。
    ///
    /// <para><b>4.0 大瘦身</b>（相对 3.0）：
    /// <list type="bullet">
    /// <item>删掉「跨地图 / 同 NetworkName 远程核心」——用户拍板暂不做跨图，只收本图。</item>
    /// <item>删掉芯片分支与代理点。3.0 是三层：有芯片 → 隔空取；无芯片 → 走代理点/接口
    ///   且要求 <c>pawn.CanReach</c>；<c>requireChipForCoreAccess</c> 打开 → 无芯片完全没入口。
    ///   现在统一为「纯轮椅」：任何位置都能存取，不看芯片也不看可达性。
    ///   （这也正是旧版「为什么有时候能隔空取物有时候不行」的来源。）</item>
    /// <item>删掉 <c>CoreAccess</c> 结构体与 <c>PickProxyCell</c> / <c>HasReachableProxy</c>
    ///   —— 4.0 的 job 直接带真实 Thing，不需要代理格。</item>
    /// </list></para>
    ///
    /// <para>保留的唯一门是**核心必须通电**（<see cref="IsUsable"/>）—— 那是核心自身状态，
    /// 与访问方式无关。</para>
    /// </summary>
    public static class CoreFinder
    {
        // 环形缓存：think tree 对全 pawn 求值时单槽缓存反复 miss，
        // 每槽存 (pawn, tick, result)，最近使用的 4 个 pawn 命中缓存
        private static readonly List<(Pawn pawn, int tick, List<Building_StorageCore> result)> cache
            = new List<(Pawn, int, List<Building_StorageCore>)>();
        private const int CacheSize = 4;

        /// <summary>返回本图所有可用（已通电）的核心。同一 tick 同一 pawn 缓存结果（4 槽环形）。</summary>
        public static List<Building_StorageCore> AllUsableCores(Pawn pawn)
        {
            int tick = Find.TickManager.TicksGame;
            for (int i = 0; i < cache.Count; i++)
            {
                if (cache[i].pawn == pawn && cache[i].tick == tick)
                    return cache[i].result;
            }

            List<Building_StorageCore> r = BuildCoreList(pawn);
            cache.Insert(0, (pawn, tick, r));
            if (cache.Count > CacheSize)
                cache.RemoveAt(cache.Count - 1);
            return r;
        }

        /// <summary>快速检查：有没有任何可用核心（走同一缓存，结果一致）。</summary>
        public static bool AnyUsableCore(Pawn pawn)
        {
            return AllUsableCores(pawn).Count > 0;
        }

        private static List<Building_StorageCore> BuildCoreList(Pawn pawn)
        {
            var result = new List<Building_StorageCore>();
            if (pawn?.Map == null) return result;

            List<Building_StorageCore> all = Core.LedgerItemCollector.GetAllUsableCores(pawn.Map);
            for (int i = 0; i < all.Count; i++)
            {
                Building_StorageCore core = all[i];
                if (core == null || core.Map != pawn.Map) continue; // 暂不做跨图
                result.Add(core);
            }
            return result;
        }

        public static bool IsUsable(Building_StorageCore core) =>
            core != null && core.Spawned && !core.Destroyed && core.Powered;
    }
}
