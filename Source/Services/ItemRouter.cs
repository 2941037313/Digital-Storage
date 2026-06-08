using System;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Services
{
    /// <summary>
    /// 物品路由器：地面物品直接路由到储存区/核心（函数2），
    /// 核心↔储存区搬入搬出（函数1），共享优先级比较逻辑。
    /// </summary>
    public static class ItemRouter
    {
        // ===== 函数2: 地面物品路由 (CompAutoIngest 15tick 调用) =====

        /// <summary>
        /// 路由一个地面物品到最优去向。优先级高于核心的储存区→spawn过去，
        /// 没有储存区要但核心优先级>Unstored→吸入账本，核心优先级太低→留地上。
        /// </summary>
        /// <returns>true=物品已被处理(de/spawn/ingest)，false=留在地上</returns>
        public static bool RouteGroundItem(Thing item, Map map,
            Building_StorageCore core, int coreCapacity)
        {
            StoragePriority corePrio = core.storagePriority;

            // 找优先级高于核心的储存区
            var bestStorage = FindBestStorageFor(item, map, corePrio);
            if (bestStorage != null)
            {
                // 用原版 TryPlaceDirect 自动合并堆叠 + 检查容量
                item.DeSpawn();
                if (!GenPlace.TryPlaceThing(item, bestStorage.Value, map,
                    ThingPlaceMode.Direct))
                {
                    // 兜底：格子满/无法合并 → 强行放入（被挤出也无妨，会被再次路由）
                    GenSpawn.Spawn(item, bestStorage.Value, map);
                }
                return true;
            }

            // 没有储存区要 → 核心接管（如果核心优先级不是 Unstored 且有容量）
            if (corePrio > StoragePriority.Unstored &&
                core.Ledger.CanAccept(item, coreCapacity))
            {
                core.Ledger.Ingest(item, coreCapacity);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 为地面物品找最佳储存格（只找优先级高于 corePrio 的）。
        /// 按 HaulDestinationManager 已排序的列表从高到低遍历。
        /// </summary>
        public static IntVec3? FindBestStorageFor(Thing item, Map map,
            StoragePriority corePrio)
        {
            var allGroups = map.haulDestinationManager.AllGroupsListInPriorityOrder;
            if (allGroups.Count == 0) return null;

            IntVec3 itemPos = item.Position;

            foreach (var group in allGroups)
            {
                StoragePriority sp = group.Settings.Priority;

                // 只找比核心优先级更高的
                if (sp <= corePrio) continue;

                // 滤网检查
                if (!group.Settings.AllowedToAccept(item)) continue;

                // 找这个组里最近的可用格子
                IntVec3? bestCell = null;
                float bestDist = float.MaxValue;

                foreach (var cell in group.CellsList)
                {
                    if (!StoreUtility.IsGoodStoreCell(cell, map, item, null,
                        Faction.OfPlayer))
                        continue;

                    float dist = (cell - itemPos).LengthHorizontalSquared;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestCell = cell;
                    }
                }

                if (bestCell != null)
                    return bestCell;
            }

            return null;
        }

        // ===== 函数1: 核心↔储存区优先级调度 (60tick 调用) =====

        /// <summary>
        /// 从核心搬出物品到更高优先级的储存区。
        /// 从账本取出→spawn在核心旁→标记刚取出→原版Haul系统接管搬入储存区。
        /// </summary>
        /// <returns>true=已处理一批</returns>
        public static bool RouteCoreToStorage(Building_StorageCore core, Map map)
        {
            var allGroups = map.haulDestinationManager.AllGroupsListInPriorityOrder;
            if (allGroups.Count == 0) return false;

            StoragePriority corePrio = core.storagePriority;
            if (corePrio >= StoragePriority.Important) return false; // 核心已经足够高

            foreach (var group in allGroups)
            {
                StoragePriority sp = group.Settings.Priority;
                if (sp <= corePrio) break; // 排序降序，后面的都≤核心

                foreach (var kv in core.Ledger.Stock)
                {
                    if (kv.Value <= 0) continue;

                    ThingDef def = kv.Key.def;
                    if (!group.Settings.AllowedToAccept(def)) continue;
                    if (!HasFreeCell(group, map)) continue;

                    int amount = (int)Math.Min(kv.Value, 75L);
                    Thing spawned = core.Ledger.Withdraw(kv.Key, amount);
                    if (spawned == null) continue;

                    // Spawn 在核心交互格旁
                    IntVec3 spawnPos = core.InteractionCell;
                    if (!spawnPos.IsValid || !spawnPos.InBounds(map) ||
                        !spawnPos.Walkable(map))
                        spawnPos = core.Position;

                    // 用原版放置——自动堆叠合并 + 找就近空格
                    GenPlace.TryPlaceThing(spawned, spawnPos, map,
                        ThingPlaceMode.Near);
                    CompAutoIngest.MarkWithdrawn(spawned);

                    return true; // 一次 tick 一种
                }
            }

            return false;
        }

        /// <summary>
        /// 从低级储存区搬入物品到核心。创建 DS_IngestToCore Job 并尝试分配给空闲pawn。
        /// </summary>
        /// <returns>true=Job已分配</returns>
        public static bool TryCreateAndDispatchHaulToCore(Map map,
            Building_StorageCore core)
        {
            if (core.storagePriority <= StoragePriority.Low) return false;

            var allGroups = map.haulDestinationManager.AllGroupsListInPriorityOrder;

            foreach (var group in allGroups)
            {
                StoragePriority sp = group.Settings.Priority;
                if (sp >= core.storagePriority) continue;

                foreach (var cell in group.CellsList)
                {
                    var things = map.thingGrid.ThingsListAt(cell);
                    for (int i = 0; i < things.Count; i++)
                    {
                        var t = things[i];
                        if (t.def.category != ThingCategory.Item) continue;
                        if (!LedgerPolicy.CanIngest(t)) continue;
                        if (t.IsForbidden(Faction.OfPlayer)) continue;
                        if (map.reservationManager.IsReserved(t)) continue;
                        if (!core.AllowsItem(t)) continue;
                        if (!core.Ledger.CanAccept(t, core.GetCapacity())) continue;

                        return TryDispatchJobToPawn(map, t, core);
                    }
                }
            }

            return false;
        }

        private static bool TryDispatchJobToPawn(Map map, Thing t,
            Building_StorageCore core)
        {
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.Downed || pawn.IsPrisoner) continue;
                if (!pawn.workSettings.EverWork) continue;
                if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                    continue;
                if (pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) continue;
                if (!pawn.CanReach(t, PathEndMode.ClosestTouch, Danger.Deadly))
                    continue;

                var job = JobMaker.MakeJob(
                    DigitalStorage_JobDefOf.DigitalStorage_IngestToCore, t);
                job.SetTarget(TargetIndex.C, core);
                job.count = t.stackCount;

                // 不打断当前job，排到队列最前面
                pawn.jobs.jobQueue.EnqueueFirst(job, JobTag.MiscWork);
                return true;
            }
            return false;
        }

        // ===== helpers =====

        private static bool HasFreeCell(SlotGroup group, Map map)
        {
            foreach (var cell in group.CellsList)
                if (cell.GetItemCount(map) < cell.GetMaxItemsAllowedInCell(map))
                    return true;
            return false;
        }
    }
}
