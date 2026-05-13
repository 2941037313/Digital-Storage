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

            var mapComp = pawn.Map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return result;

            var localCores = mapComp.GetAllCores();
            bool chip = Hediff_TerminalImplant.HasTerminalImplant(pawn);

            // 1) 本地核心（账本=代理=同一个）
            var localByNetwork = new Dictionary<string, Building_StorageCore>();
            var addedRemotes = new HashSet<Building_StorageCore>();
            for (int i = 0; i < localCores.Count; i++)
            {
                var core = localCores[i];
                if (!IsUsable(core)) continue;
                result.Add(new CoreAccess { ledgerCore = core, proxyCore = core });
                if (!string.IsNullOrEmpty(core.NetworkName) && !localByNetwork.ContainsKey(core.NetworkName))
                    localByNetwork[core.NetworkName] = core;
            }

            // 2) 远程核心
            var gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp != null)
            {
                var allCores = gameComp.GetAllCores();
                for (int i = 0; i < allCores.Count; i++)
                {
                    var core = allCores[i];
                    if (core.Map == pawn.Map) continue;
                    if (!IsUsable(core)) continue;
                    if (string.IsNullOrEmpty(core.NetworkName)) continue;
                    if (addedRemotes.Contains(core)) continue;

                    // 优先：本地同网络核心做代理
                    if (localByNetwork.TryGetValue(core.NetworkName, out var localProxy))
                    {
                        if (chip || HasReachableProxy(pawn, localProxy))
                        {
                            result.Add(new CoreAccess { ledgerCore = core, proxyCore = localProxy });
                            addedRemotes.Add(core);
                            continue;
                        }
                    }

                    // 兜底：远程核心的跨图接口直接做代理
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
