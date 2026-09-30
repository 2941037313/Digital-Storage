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
        /// 路由一个地面物品到最优去向。优先级高于核心的储存区→spawn过去，
        /// 没有更高优先级的储存区但核心优先级>Unstored→吸入账本，否则留地上。
        /// 语义（用户 7.31 拍板）：全链路严格比较——只有 zone 优先级「严格高于」核心
        /// 才优先 zone；平级时核心收（新物品会进核心，而不是全被平级 zone 截走）。
        /// </summary>
        /// <returns>true=物品已被处理(de/spawn/ingest)，false=留在地上</returns>
        public static bool RouteGroundItem(Thing item, Map map,
            Building_StorageCore core, int coreCapacity)
        {
            StoragePriority corePrio = core.storagePriority;
            // L3: DeSpawn 后 Position 失效，先缓存
            IntVec3 itemPos = item.Position;

            // 找优先级严格高于核心的储存区（平级 zone 不抢——新物品进核心）
            var bestStorage = FindBestStorageFor(item, map, corePrio);
            if (bestStorage != null)
            {
                // 用原版 TryPlaceDirect 自动合并堆叠 + 检查容量
                item.DeSpawn();
                if (!GenPlace.TryPlaceThing(item, bestStorage.Value, map,
                    ThingPlaceMode.Direct))
                {
                    // L3: 放不进去 → 放回原位，下个周期重试；绝不 GenSpawn 强塞
                    //（强塞会造成超出 MaxItemsInCell 的地图污染，且 IsInAnyStorage 挡住后续整理）
                    if (!GenPlace.TryPlaceThing(item, itemPos, map, ThingPlaceMode.Near))
                        return false; // 连原位都放不回 → 极端情况，交给调用方
                    return false;
                }
                return true;
            }

            // 没有储存区要 → 核心接管（优先级 > Unstored + 过滤器放行(M4) + 有容量）
            if (corePrio > StoragePriority.Unstored &&
                core.AllowsItem(item) &&
                core.Ledger.CanAccept(item, coreCapacity))
            {
                core.Ledger.Ingest(item, coreCapacity);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 统一判据：这个物品是否该由核心接收（而不是留给原版搬运/更高优先级储存区）。
        /// 语义：物品去「优先级 > 核心」的储存区，否则核心吃掉（平级时核心收）。
        /// 收敛点（H1/M4/M5/M3）：RouteGroundItem、HaulToCore、StorageToCore 共用，
        /// 消除「物品该去哪」判据的四处不同实现。
        /// </summary>
        public static bool ShouldCoreTakeItem(Thing t, Map map,
            Building_StorageCore core, int coreCapacity)
        {
            if (t == null || t.Destroyed) return false;
            if (core == null || !core.Spawned || !core.Powered) return false;
            if (!LedgerPolicy.CanIngest(t)) return false;
            if (!core.AllowsItem(t)) return false;
            if (!core.Ledger.CanAccept(t, coreCapacity)) return false;
            // 存在优先级严格高于核心的储存区想要它 → 让原版 haul 处理
            if (FindBestStorageFor(t, map, core.storagePriority) != null) return false;
            return true;
        }

        /// <summary>
        /// 为地面物品找最佳储存格（只找优先级严格高于 corePrio 的）。
        /// 检查格子容量+堆叠可能，不依赖 IsGoodStoreCell（太严格，会拒绝满堆但可开新堆的情况）。
        /// </summary>
        public static IntVec3? FindBestStorageFor(Thing item, Map map,
            StoragePriority corePrio)
        {
            var allGroups = map.haulDestinationManager.AllGroupsListInPriorityOrder;
            if (allGroups.Count == 0) return null;

            // P1: 同一 tick 内重复查询直接命中缓存。
            // 调用方在同一 tick 里会对同一个物品问两遍（ShouldCoreTakeItem 一次、
            // RouteGroundItem 再一次），旧的「物品 × 组 × 格」三重线性扫描翻倍。
            // 命中后用 CanCellAcceptItem 复验单格（O(1)），保证不会返回已被占用的格。
            int tick = Find.TickManager.TicksGame;
            if (tick != cacheTick)
            {
                cacheTick = tick;
                cellCache.Clear();
            }
            var cacheKey = (map.uniqueID, item.thingIDNumber, (int)corePrio);
            if (cellCache.TryGetValue(cacheKey, out var cached) && cached.IsValid)
            {
                if (CanCellAcceptItem(cached, map, item)) return cached;
                cellCache.Remove(cacheKey);
            }

            foreach (var group in allGroups)
            {
                StoragePriority sp = group.Settings.Priority;
                // 严格高于核心才接受——平级 zone 不抢地面物品（新物品进核心，用户 7.31 拍板）
                if (sp <= corePrio) continue;
                if (!group.Settings.AllowedToAccept(item)) continue;

                foreach (var cell in group.CellsList)
                {
                    if (CanCellAcceptItem(cell, map, item))
                    {
                        cellCache[cacheKey] = cell;
                        return cell;
                    }
                }
            }
            return null;
        }

        // P1: tick 级缓存（静态复用，避免每 tick 分配）
        private static int cacheTick = -1;
        private static readonly Dictionary<(int mapId, int thingId, int prio), IntVec3> cellCache
            = new Dictionary<(int, int, int), IntVec3>();


        /// <summary>
        /// 检查格子是否能接收该物品——可合并到已有堆 or 有空位开新堆。
        /// L2: 目标格已被其他 pawn 的 haul 工单预留 → 不接受（避免放置冲突）。
        /// </summary>
        private static bool CanCellAcceptItem(IntVec3 cell, Map map, Thing item)
        {
            // L2: 预留检查（原版 haul 工在途目标格不塞入）
            if (map.reservationManager.IsReservedByAnyoneOf(cell, Faction.OfPlayer))
                return false;

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

        /// <summary>
        /// 在 group 里找一个能「完整放下」该堆叠的格子（可完整合并或开新堆，查预留）。
        /// 拒绝余量不足的格：TryPlaceThing Direct 会拆分合并 → 残余堆落核心旁地面 →
        /// 被自动收纳吸回核心 → 反复 Withdraw/拆分（ogre 大堆叠下的 75/9925 乒乓，7.31 晚）。
        /// </summary>
        private static IntVec3 FindCellInGroup(SlotGroup group, Map map, Thing t)
        {
            foreach (var cell in group.CellsList)
                if (CanPlaceFullStack(cell, map, t))
                    return cell;
            return IntVec3.Invalid;
        }

        /// <summary>
        /// 格子能否完整吞下整个堆叠：与已有同类堆合并（余量 ≥ 堆叠）或开新堆。
        /// 有同类堆但余量不足 → false（拆分合并会留残余，绝不选）。
        /// </summary>
        private static bool CanPlaceFullStack(IntVec3 cell, Map map, Thing item)
        {
            bool hasMergeable = false;
            int itemCount = 0;
            var things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++)
            {
                var t = things[i];
                if (t.def.category == ThingCategory.Item) itemCount++;
                if (t.CanStackWith(item))
                {
                    hasMergeable = true;
                    if (t.def.stackLimit - t.stackCount >= item.stackCount)
                        return true; // 余量足够，可完整合并
                }
            }
            if (hasMergeable) return false; // 有同类堆但余量不足 → 会拆分 → 不选
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
            // H3/L5: 删除 Important 一刀切——按比较逻辑，核心想要的物品只向
            // 「严格高于核心」的 zone 升仓；Critical 核心因不存在更高的 zone 天然囤积。

            // 快照 key 列表：Withdraw 会修改 stock 字典，迭代中 Remove 会抛异常（L6/I10.01.19）
            routeKeyBuffer.Clear();
            foreach (var k in core.Ledger.AllKeys()) routeKeyBuffer.Add(k);

            for (int i = 0; i < routeKeyBuffer.Count; i++)
            {
                ItemKey key = routeKeyBuffer[i];
                long have = core.Ledger.StockOf(key);
                if (have <= 0) continue;

                ThingDef def = key.def;
                // 驱逐模式：核心过滤器关闭该 def（核心不想要）→ 搬去第一个接受的 zone，
                // 任意优先级（含平级/更低）。驱逐后 StorageToCore/自动收纳因
                // AllowsItem=false 不会吸回，无乒乓。
                bool coreWants = core.AllowsDef(def);

                foreach (var group in allGroups)
                {
                    // 7.31 晚：缓冲仓库库存由阈值补货逻辑管理（CompBufferWarehouse），
                    // 升仓/驱逐不塞它——塞货会突破阈值且无法回收 → 乒乓。普通 zone 不变。
                    if (group.parent is Building_BufferWarehouse) continue;

                    StoragePriority sp = group.Settings.Priority;
                    // 核心想要的物品：只升仓，平级/更低不搬（用户 7.31 拍板）
                    if (coreWants && sp <= corePrio) break;
                    if (!group.Settings.AllowedToAccept(def)) continue;

                    // 数量上限交给 Withdraw 内部按 stackLimit 截断（X5），不再硬编码 75
                    Thing spawned = core.Ledger.Withdraw(key, int.MaxValue);
                    if (spawned == null) continue;

                    // H4: 用 Thing 重载判定（hitPoints/special filters）——失配退回账本，
                    // 避免「搬出→原版 haul 拒收→滞留」的活锁
                    if (!group.Settings.AllowedToAccept(spawned))
                    {
                        core.Ledger.AddRaw(key, spawned.stackCount);
                        if (!spawned.Destroyed) spawned.Destroy(DestroyMode.Vanish);
                        continue;
                    }

                    // H2: 直接放进目标 zone 的空闲格——一步到位进储存区，
                    // StorageToCore/HaulToCore/自动收纳都因 IsInAnyStorage 或优先级跳过他，
                    // 乒乓从结构上消失（不再依赖 recentlyWithdrawn 时间窗）
                    IntVec3 target = FindCellInGroup(group, map, spawned);
                    if (!target.IsValid)
                    {
                        core.Ledger.AddRaw(key, spawned.stackCount);
                        if (!spawned.Destroyed) spawned.Destroy(DestroyMode.Vanish);
                        continue;
                    }

                    if (!GenPlace.TryPlaceThing(spawned, target, map, ThingPlaceMode.Direct))
                    {
                        // 极端情况：放置仍失败 → 退回账本，绝不让物品凭空消失
                        core.Ledger.AddRaw(key, spawned.stackCount);
                        if (!spawned.Destroyed) spawned.Destroy(DestroyMode.Vanish);
                        continue;
                    }

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
    }
}
