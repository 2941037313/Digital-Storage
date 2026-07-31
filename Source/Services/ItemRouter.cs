using System;
using System.Collections.Generic;
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
        /// 路由一个地面物品到最优去向。优先级不低于核心的储存区→spawn过去，
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
        /// 为地面物品找最佳储存格（只找优先级不低于 corePrio 的）。
        /// 检查格子容量+堆叠可能，不依赖 IsGoodStoreCell（太严格，会拒绝满堆但可开新堆的情况）。
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
                // 平级（sp == corePrio）也接受——料斗等平级储存区优先于核心，与原版行为一致
                if (sp < corePrio) continue;
                if (!group.Settings.AllowedToAccept(item)) continue;

                foreach (var cell in group.CellsList)
                {
                    if (CanCellAcceptItem(cell, map, item))
                        return cell;
                }
            }
            return null;
        }

        /// <summary>
        /// 检查格子是否能接收该物品——可合并到已有堆 or 有空位开新堆。
        /// </summary>
        private static bool CanCellAcceptItem(IntVec3 cell, Map map, Thing item)
        {
            var things = map.thingGrid.ThingsListAt(cell);
            foreach (var t in things)
            {
                if (t.CanStackWith(item) && t.stackCount < t.def.stackLimit)
                    return true; // 可合并
            }
            int itemCount = 0;
            foreach (var t in things)
                if (t.def.category == ThingCategory.Item) itemCount++;
            return itemCount < cell.GetMaxItemsAllowedInCell(map); // 可开新堆
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

            // 快照 key 列表：Withdraw 会修改 stock 字典，迭代中 Remove 会抛异常（L6/I10.01.19）
            routeKeyBuffer.Clear();
            foreach (var k in core.Ledger.AllKeys()) routeKeyBuffer.Add(k);

            foreach (var group in allGroups)
            {
                StoragePriority sp = group.Settings.Priority;
                if (sp <= corePrio) break; // 排序降序，后面的都≤核心

                for (int i = 0; i < routeKeyBuffer.Count; i++)
                {
                    ItemKey key = routeKeyBuffer[i];
                    long have = core.Ledger.StockOf(key);
                    if (have <= 0) continue;

                    ThingDef def = key.def;
                    if (!group.Settings.AllowedToAccept(def)) continue;
                    if (!HasFreeCell(group, map)) continue;

                    // 数量上限交给 Withdraw 内部按 stackLimit 截断（X5），不再硬编码 75
                    Thing spawned = core.Ledger.Withdraw(key, int.MaxValue);
                    if (spawned == null) continue;

                    // Spawn 在核心交互格旁
                    IntVec3 spawnPos = core.InteractionCell;
                    if (!spawnPos.IsValid || !spawnPos.InBounds(map) ||
                        !spawnPos.Walkable(map))
                        spawnPos = core.Position;

                    // 用原版放置——自动堆叠合并 + 找就近空格；失败退回账本（H5/I10.01.6）
                    if (!PlaceOrRefund(spawned, spawnPos, map, core.Ledger, key))
                        continue;

                    CompAutoIngest.MarkWithdrawn(spawned);
                    return true; // 一次 tick 一种
                }
            }

            return false;
        }

        // ===== helpers =====

        /// <summary>
        /// 安全放置：失败时退回账本并销毁，绝不丢物品（H5/I10.01.6）。
        /// 所有搬出/取料路径统一走这里。
        /// </summary>
        public static bool PlaceOrRefund(Thing t, IntVec3 pos, Map map,
            CoreLedger ledger, ItemKey key)
        {
            if (GenPlace.TryPlaceThing(t, pos, map, ThingPlaceMode.Near)) return true;
            ledger.AddRaw(key, t.stackCount);
            if (!t.Destroyed) t.Destroy(DestroyMode.Vanish);
            return false;
        }

        // 复用缓冲，避免每 60 tick 分配（比照 CompAutoIngest.candidateBuffer）
        private static readonly List<ItemKey> routeKeyBuffer = new List<ItemKey>();

        private static bool HasFreeCell(SlotGroup group, Map map)
        {
            foreach (var cell in group.CellsList)
                if (cell.GetItemCount(map) < cell.GetMaxItemsAllowedInCell(map))
                    return true;
            return false;
        }
    }
}
