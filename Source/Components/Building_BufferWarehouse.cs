using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Core;
using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 缓冲仓库 —— 物理存储桥接层。
    /// 继承 Building_Storage 获得 SlotGroup + 原版搬运兼容。
    /// 物品在 SlotGroup 上真实存在，其他 mod 可通过任何 API 发现。
    /// 绑定一个核心，双向物流（补货/收纳）在 CompBufferWarehouse 中处理。
    /// </summary>
    public partial class Building_BufferWarehouse : Building_Storage
    {
        private Building_StorageCore boundCore;
        private ThingDef lockedItemDef;

        public Building_StorageCore BoundCore => boundCore;
        public ThingDef LockedItemDef => lockedItemDef;

        /// <summary>
        /// 8.1: 设置锁定物品，存储筛选同步跟随——缓冲仓库的设置唯一入口是新 UI
        /// （ITab:放什么物品 + Min/Max + 清空该单元），原版筛选/优先级按钮已屏蔽。
        /// 锁定 → 筛选只允许该物品；清空/替换 → 自动取消（全部不允许）。
        /// </summary>
        internal void SetLockedItemDef(ThingDef def)
        {
            lockedItemDef = def;
            var settings = GetStoreSettings();
            if (settings != null)
            {
                settings.filter.SetDisallowAll();
                if (def != null) settings.filter.SetAllow(def, true);
            }
        }

        /// <summary>
        /// 清空该单元（UI「清空该单元」按钮）：物品吸入核心 → lockedItemDef=null，
        /// Min=Max=0 由 UI 侧设置。核心满吸不回 → 留在格上（绝不销毁物品，8.1 改进）。
        /// </summary>
        public void ClearUnit()
        {
            if (slotGroup != null && boundCore != null && !boundCore.Destroyed)
            {
                int capacity = boundCore.GetCapacity();
                var toIngest = new List<Thing>();
                foreach (var t in slotGroup.HeldThings)
                {
                    if (t.Destroyed) continue;
                    toIngest.Add(t);
                }
                foreach (var t in toIngest)
                {
                    if (t.Destroyed) continue;
                    if (boundCore.Ledger.CanAccept(t, capacity))
                        boundCore.Ledger.Ingest(t, capacity);
                }
            }
            SetLockedItemDef(null); // 清空：筛选同步取消（全部不允许）
        }

        // ========== 生命周期 ==========

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            // 注册到 MapComponent
            map.GetComponent<DigitalStorageMapComponent>()?.RegisterBufferWarehouse(this);

            // 注册 draw 抑制位置
            DigitalStorage.HarmonyPatches.Patch_BufferWarehouse_HideItems.Register(map, Position);

            // 自动绑定同 NetworkName 核心
            if (boundCore == null || boundCore.Destroyed)
                TryAutoBind();

            // 8.1: 优先级锁定最高——不存在高于缓冲仓库的存储区，原版「按优先级搬运」
            // 永远不会把仓库物品搬去其他存储区（玩家不可改，无 UI 入口）。
            var settings = GetStoreSettings();
            if (settings != null)
                settings.Priority = StoragePriority.Critical;
            // 筛选与锁定同步（含读档后：防旧存档遗留玩家改过的筛选）
            SetLockedItemDef(lockedItemDef);

            // 加载后冻结已有物品 + 存量迁移到单物品模式
            if (respawningAfterLoad && slotGroup != null)
            {
                foreach (var t in slotGroup.HeldThings)
                    FreezeItemTick(t);

                MigrateToSingleItem();
            }
        }

        private void MigrateToSingleItem()
        {
            if (slotGroup == null || boundCore == null || boundCore.Destroyed) return;

            // 锁定为首个有效物品的 def
            if (lockedItemDef == null)
            {
                foreach (var t in slotGroup.HeldThings)
                {
                    if (t.Destroyed) continue;
                    lockedItemDef = t.def;
                    break;
                }
            }
            if (lockedItemDef == null) return;

            // 吸入不匹配 lockedItemDef 的物品
            var toIngest = new List<Thing>();
            foreach (var t in slotGroup.HeldThings)
            {
                if (t.Destroyed) continue;
                if (t.def != lockedItemDef && LedgerPolicy.CanIngest(t))
                    toIngest.Add(t);
            }

            int capacity = boundCore.GetCapacity();
            foreach (var t in toIngest)
            {
                if (t.Destroyed) continue;
                if (boundCore.Ledger.CanAccept(t, capacity))
                    boundCore.Ledger.Ingest(t, capacity);
                else
                    t.Destroy(DestroyMode.Vanish);
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            DigitalStorage.HarmonyPatches.Patch_BufferWarehouse_HideItems.Deregister(Map, Position);
            Map?.GetComponent<DigitalStorageMapComponent>()?.DeregisterBufferWarehouse(this);
            if (boundCore != null && !boundCore.Destroyed)
            {
                // TODO 7c: 库存处理（归还核心或掉落）
            }
            boundCore = null;
            base.DeSpawn(mode);
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            if (boundCore != null && !boundCore.Destroyed)
            {
                // I6 模式：摧毁时转移
            }
            base.Destroy(mode);
        }

        // ========== 绑定逻辑 ==========

        public void BindToCore(Building_StorageCore core)
        {
            if (boundCore == core) return;
            UnbindFromCore();
            boundCore = core;
        }

        public void UnbindFromCore()
        {
            boundCore = null;
        }

        private void TryAutoBind()
        {
            var mapComp = Map?.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return;

            var myNet = NetworkName();
            if (string.IsNullOrEmpty(myNet)) return;

            foreach (var core in mapComp.GetAllCores())
            {
                if (core == null || core.Destroyed || !core.Spawned) continue;
                if (core.NetworkName == myNet)
                {
                    boundCore = core;
                    break;
                }
            }
        }

        private string NetworkName()
        {
            // 从自身找，或者用第一个本地核心的网名
            var mapComp = Map?.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return null;

            foreach (var core in mapComp.GetAllCores())
            {
                if (core != null && core.Spawned && !core.Destroyed)
                    return core.NetworkName;
            }
            return null;
        }

        // ========== 物品接收 ==========

        // Accepts 走原版 Building_Storage 基类逻辑——物品正常视为"已存储"。
        // pawn 搬运入仓被 Patch_BW_BlockHaulDestination 拦截（TryFindBestBetterStorageFor 跳 BW）。
        // 物品只能通过 CompBufferWarehouse.CompTick 补货进入（GenSpawn.Spawn 不经过 StoreUtility）。

        public override void Notify_ReceivedThing(Thing newItem)
        {
            base.Notify_ReceivedThing(newItem);
            FreezeItemTick(newItem);
            // TODO 7c: 触发超额收纳检查
        }

        public override void Notify_LostThing(Thing newItem)
        {
            base.Notify_LostThing(newItem);
            UnfreezeItemTick(newItem);
            // TODO 7c: 触发补货检查
        }

        // ========== 7b: Tick 冻结 ==========

        /// <summary>
        /// 物品放入缓冲仓库后冻结：移除 tick 注册 + 停止动态渲染 + 重置腐烂。
        /// 保留 ListerThings / WealthWatcher / Room 统计（mod 兼容用）。
        /// </summary>
        internal void FreezeItemTick(Thing t)
        {
            var map = Map;
            if (map == null) return;
            Find.TickManager.DeRegisterAllTickabilityFor(t);
            map.dynamicDrawManager.DeRegisterDrawable(t);
        }

        internal void UnfreezeItemTick(Thing t)
        {
            var map = Map;
            if (map == null) return;
            Find.TickManager.RegisterAllTickabilityFor(t);
            map.dynamicDrawManager.RegisterDrawable(t);
        }

        // ========== 序列化 ==========

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref boundCore, "boundCore");
            Scribe_Defs.Look(ref lockedItemDef, "lockedItemDef");
        }
    }
}
