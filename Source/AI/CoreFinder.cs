using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 可用存储核心的发现器。
    ///
    /// <para><b>⚠️ 2026-10-01 修 bug</b>：这里原先走 <c>LedgerItemCollector.GetAllUsableCores</c>
    /// → <c>DigitalStorageMapComponent.GetAllCores()</c> 这个**注册表**。但批 1 重写
    /// <c>Building_StorageCore</c> 时把 <c>SpawnSetup</c> 里的
    /// <c>map.GetComponent&lt;DigitalStorageMapComponent&gt;()?.RegisterCore(this)</c> 当成
    /// "账本时代的残留"删掉了 —— 于是没有任何东西往注册表里写，
    /// <c>GetAllCores()</c> **恒为空**。后果是两条功能静默失效：
    /// <list type="bullet">
    /// <item><c>CoreFinder.AnyUsableCore</c> 恒 false ⇒ 建造投料
    ///   （<c>WorkGiver_DS_WithdrawForConstruct</c>）永远不派工、右键"从数字存储取出"永不出现</item>
    /// <item><c>CompAutoIngest.FindBestIngestCore</c> 恒 null ⇒ 自动收纳完全不工作</item>
    /// </list>
    /// 现在改为**直接枚举 haul source**（与硬约束"数据源一律用 HaulSourceContents"一致），
    /// 不再依赖任何注册表 —— 注册表也因此成为死代码，批 3 删除。</para>
    ///
    /// <para><b>4.0 大瘦身</b>（相对 3.0）：删掉跨图 / 同 NetworkName 远程核心；
    /// 删掉芯片分支与代理点（纯轮椅：任何位置都能存取，不看芯片也不看可达性）；
    /// 删掉 <c>CoreAccess</c> 结构体与 <c>PickProxyCell</c> / <c>HasReachableProxy</c>
    /// （job 直接带真实 Thing，不需要代理格）。</para>
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

        /// <summary>本图所有可用（已通电）的核心。同一 tick 同一 pawn 缓存结果（4 槽环形）。</summary>
        public static List<Building_StorageCore> AllUsableCores(Pawn pawn)
        {
            int tick = Find.TickManager.TicksGame;
            for (int i = 0; i < cache.Count; i++)
            {
                if (cache[i].pawn == pawn && cache[i].tick == tick)
                    return cache[i].result;
            }

            List<Building_StorageCore> r = AllUsableCores(pawn?.Map);
            cache.Insert(0, (pawn, tick, r));
            if (cache.Count > CacheSize)
                cache.RemoveAt(cache.Count - 1);
            return r;
        }

        /// <summary>
        /// 本图所有可用（已通电、未销毁）的核心。直接枚举 haul source ——
        /// 不依赖任何注册表（见类注释里的 bug 记录）。
        /// <b>调用方注意</b>：返回的是**新建列表**，可以安全持有；
        /// 但 <c>HaulSourceContents.EnabledSources</c> 内部用的是复用缓冲，别把它存起来。
        /// </summary>
        public static List<Building_StorageCore> AllUsableCores(Map map)
        {
            var result = new List<Building_StorageCore>();
            if (map == null) return result;

            List<IHaulSource> sources = HaulSourceContents.EnabledSources(map);
            for (int i = 0; i < sources.Count; i++)
            {
                Building_StorageCore core = sources[i] as Building_StorageCore;
                if (core == null) continue;
                if (!IsUsable(core)) continue;
                result.Add(core);
            }
            return result;
        }

        /// <summary>快速检查：有没有任何可用核心（走同一缓存，结果一致）。</summary>
        public static bool AnyUsableCore(Pawn pawn)
        {
            return AllUsableCores(pawn).Count > 0;
        }

        public static bool IsUsable(Building_StorageCore core) =>
            core != null && core.Spawned && !core.Destroyed && core.Powered;
    }
}
