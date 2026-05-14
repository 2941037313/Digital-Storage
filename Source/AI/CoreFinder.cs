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
        /// <summary>
        /// 返回所有可用核心访问入口。本地优先，远程同网络兜底，跨图接口直连。
        /// </summary>
        public static List<CoreAccess> AllUsableAccesses(Pawn pawn)
        {
            var result = new List<CoreAccess>();
            if (pawn?.Map == null) return result;

            // 核心发现委托给 LedgerItemCollector（消除重复的逻辑）
            var allCores = Core.LedgerItemCollector.GetAllUsableCores(pawn.Map);
            if (allCores.Count == 0) return result;

            bool chip = Hediff_TerminalImplant.HasTerminalImplant(pawn);

            // 本地核心按 NetworkName 建索引（用于远程找代理）
            var localByNetwork = new Dictionary<string, Building_StorageCore>();
            var addedRemotes = new HashSet<Building_StorageCore>();

            for (int i = 0; i < allCores.Count; i++)
            {
                var core = allCores[i];

                if (core.Map == pawn.Map)
                {
                    // 本地核心：账本=代理=同一个
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
            return best;
        }
    }
}
