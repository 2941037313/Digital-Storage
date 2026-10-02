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

        // ===================================================================
        // 跨图（4.0 第三阶段）
        // ===================================================================
        //
        // 【为什么不需要任何注册表 / 房间 / 网络名】"b 图怎么识别 a 图的容器" 这个问题
        // 本身是伪问题 —— 两条索引原版都给了：
        //   ① 全局图列表 Find.Maps（Game.cs:300）
        //   ② 每张图自己的 haul source 列表 map.haulDestinationManager.AllHaulSourcesListForReading
        // 拼起来就是"全图的容器"。而"这件东西属于谁"更不需要查：
        //   Thing.ParentHolder → Building_StorageCore → .Map（靠 ParentHolder => Map 的数据修复，
        //   跨图的 MapHeld / SpawnedParentOrMe 本来就解析得对）。
        // 真正缺的只有两件事，分别在 Bill 发现层（见 HarmonyPatches/Patch_WorkGiver_DoBill_RemoteIngredients）
        // 与预订闸门（见 JobDriver_DS_Consume / JobDriver_DS_Withdraw 的跨图分支）处理。

        /// <summary>
        /// 全游戏所有可用核心（含当前图）。返回**新建列表**，可以安全持有。
        ///
        /// <para>只在"本图找不到"的兜底路径上调用（见 <c>HaulSourceContents.FindBestIncludingRemote</c>），
        /// 所以不做 tick 缓存 —— 缓存反而会让刚放下的核心要等一 tick 才被看见。</para>
        /// </summary>
        public static List<Building_StorageCore> AllUsableCoresGlobal()
        {
            var result = new List<Building_StorageCore>();
            List<Map> maps = Find.Maps;
            if (maps == null) return result;

            for (int i = 0; i < maps.Count; i++)
            {
                Map m = maps[i];
                if (m == null) continue;
                result.AddRange(AllUsableCores(m));
            }
            return result;
        }

        /// <summary>
        /// <b>别的图</b>上有没有可用核心。<c>ShouldSkip</c> 这类每次扫描都要问的入口用，
        /// 按 tick 缓存（一 tick 内图/核心集合不会变）。
        /// </summary>
        public static bool AnyRemoteUsableCore(Map exclude)
        {
            int tick = Find.TickManager.TicksGame;
            if (globalTick == tick && cachedExclude == exclude) return globalAny;

            globalTick = tick;
            cachedExclude = exclude;
            globalAny = false;

            List<Map> maps = Find.Maps;
            if (maps != null)
            {
                for (int i = 0; i < maps.Count && !globalAny; i++)
                {
                    Map m = maps[i];
                    if (m == null || m == exclude) continue;
                    if (AllUsableCores(m).Count > 0) globalAny = true;
                }
            }

            // 同一个 tick 内换一张 exclude 图就重算（缓存只服务"同一张图连续问"的调用模式）
            return globalAny;
        }

        /// <summary>全游戏（含本图）有没有可用核心。</summary>
        public static bool AnyUsableCoreGlobal(Pawn pawn)
        {
            if (AnyUsableCore(pawn)) return true;
            return AnyRemoteUsableCore(pawn?.Map);
        }

        private static int globalTick = -1;
        private static Map cachedExclude;
        private static bool globalAny;

        /// <summary>同图 / 跨图共用的唯一判定（实现在 <see cref="Building_StorageCore.IsUsableNow"/>）。</summary>
        public static bool IsUsable(Building_StorageCore core) => core != null && core.IsUsableNow;
    }
}
