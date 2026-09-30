using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Services;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// I2a 地基：跨地图核心发现。
    /// 本地核心优先 → 本地没货时扩展到同 NetworkName 的远程核心。
    /// 远程核心可直连（跨图接口 → GetProxyCells 返回 pawn 所在地图的接口位置）。
    /// </summary>
    public struct CoreAccess
    {
        public Building_StorageCore ledgerCore; // 账本操作目标（可远程）
        public Building_StorageCore proxyCore;  // 代理点来源（GetProxyCells 的调用对象）
    }

    public static class CoreFinder
    {
        // X3: 小环形缓存（多槽）——think tree 对全 pawn 求值时单槽缓存反复 miss，
        // 每槽存 (pawn, tick, chipRequired, result)，最近使用的 4 个 pawn 命中缓存
        private static readonly List<(Pawn pawn, int tick, bool chipRequired, List<CoreAccess> result)> accessCache
            = new List<(Pawn, int, bool, List<CoreAccess>)>();
        private const int AccessCacheSize = 4;

        /// <summary>
        /// 返回所有可用核心访问入口。本地优先，远程同网络兜底，跨图接口直连。
        /// 同一 tick 同一 pawn 缓存结果（4 槽环形）。设置项变化会自然 miss 重建。
        /// </summary>
        public static List<CoreAccess> AllUsableAccesses(Pawn pawn)
        {
            int tick = Find.TickManager.TicksGame;
            bool chipRequired = DigitalStorage.Settings.DigitalStorageSettings.requireChipForCoreAccess;
            for (int i = 0; i < accessCache.Count; i++)
            {
                if (accessCache[i].pawn == pawn && accessCache[i].tick == tick
                    && accessCache[i].chipRequired == chipRequired)
                    return accessCache[i].result;
            }

            var r = BuildAccessList(pawn);
            accessCache.Insert(0, (pawn, tick, chipRequired, r));
            if (accessCache.Count > AccessCacheSize)
                accessCache.RemoveAt(accessCache.Count - 1);
            return r;
        }

        /// <summary>
        /// 快速检查：有没有任何可用访问入口（走同一缓存，结果一致）。
        /// </summary>
        public static bool AnyUsableAccess(Pawn pawn)
        {
            return AllUsableAccesses(pawn).Count > 0;
        }

        private static List<CoreAccess> BuildAccessList(Pawn pawn)
        {
            var result = new List<CoreAccess>();
            if (pawn?.Map == null) return result;

            // 核心发现委托给 LedgerItemCollector（消除重复的逻辑）
            var allCores = Core.LedgerItemCollector.GetAllUsableCores(pawn.Map);
            if (allCores.Count == 0) return result;

            bool chip = Hediff_TerminalImplant.HasTerminalImplant(pawn);

            // 设置「需要终端芯片」：无芯片 = 无任何访问入口（含接口代理点）。
            // 这是社区反馈「科技没研发、部件没装却能用全部功能」的可配置解法，
            // 默认关闭，保持 v3「无芯片走接口」的设计。
            if (!chip && DigitalStorage.Settings.DigitalStorageSettings.requireChipForCoreAccess)
                return result;

            // 本地核心按 NetworkName 建索引（用于远程找代理）
            var localByNetwork = new Dictionary<string, Building_StorageCore>();
            var addedRemotes = new HashSet<Building_StorageCore>();

            for (int i = 0; i < allCores.Count; i++)
            {
                var core = allCores[i];

                if (core.Map == pawn.Map)
                {
                    // 本地核心：芯片直连，否则必须有可达的代理点（接口）
                    if (chip || HasReachableProxy(pawn, core))
                        result.Add(new CoreAccess { ledgerCore = core, proxyCore = core });
                    if (!string.IsNullOrEmpty(core.NetworkName) && !localByNetwork.ContainsKey(core.NetworkName))
                        localByNetwork[core.NetworkName] = core;
                }
                else
                {
                    // 远程核心：优先本地同网络代理，兜底跨图接口直连
                    if (addedRemotes.Contains(core)) continue;

                    if (localByNetwork.TryGetValue(core.NetworkName, out var localProxy))
                    {
                        if (chip || HasReachableProxy(pawn, localProxy))
                        {
                            result.Add(new CoreAccess { ledgerCore = core, proxyCore = localProxy });
                            addedRemotes.Add(core);
                            continue;
                        }
                    }

                    if (chip || HasReachableProxy(pawn, core))
                    {
                        result.Add(new CoreAccess { ledgerCore = core, proxyCore = core });
                        addedRemotes.Add(core);
                    }
                }
            }
            return result;
        }

        public static bool IsUsable(Building_StorageCore core) =>
            core != null && core.Spawned && !core.Destroyed && core.Powered;

        public static bool HasReachableProxy(Pawn pawn, Building_StorageCore core)
        {
            foreach (var c in core.GetProxyCells())
            {
                if (c.InBounds(pawn.Map) && pawn.CanReach(c, PathEndMode.Touch, Danger.Deadly))
                    return true;
            }
            // 接口都不可达时，检查核心自身交互格（兜底）
            if (core.InteractionCell.IsValid && core.InteractionCell.InBounds(pawn.Map)
                && pawn.CanReach(core.InteractionCell, PathEndMode.Touch, Danger.Deadly))
                return true;
            return false;
        }

        public static IntVec3 PickProxyCell(Pawn pawn, Building_StorageCore core)
        {
            IntVec3 best = IntVec3.Invalid;
            int bestDist = int.MaxValue;
            foreach (var c in core.GetProxyCells())
            {
                if (!c.InBounds(pawn.Map)) continue;
                if (!pawn.CanReach(c, PathEndMode.Touch, Danger.Deadly)) continue;
                int d = (c - pawn.Position).LengthManhattan;
                if (d < bestDist) { bestDist = d; best = c; }
            }
            // 接口都不可达时，检查核心自身交互格
            if (!best.IsValid && core.InteractionCell.IsValid && core.InteractionCell.InBounds(pawn.Map)
                && pawn.CanReach(core.InteractionCell, PathEndMode.Touch, Danger.Deadly))
                best = core.InteractionCell;
            return best;
        }
    }
}
