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

        internal void SetLockedItemDef(ThingDef def) { lockedItemDef = def; }

        /// <summary>
        /// 手动解锁：物品吸入核心 → lockedItemDef=null → 下次补货重新锁定。
        /// Threshold 保留。
        /// </summary>
        public void Unlock()
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
                    else
                        t.Destroy(DestroyMode.Vanish);
                }
            }
            lockedItemDef = null;
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

        // Accepts 已被 Harmony patch (Patch_BufferWarehouse_Accepts) 拦截，始终返回 false。
        // 物品只能通过 CompBufferWarehouse.CompTick 补货进入（GenSpawn.Spawn 绕过 Accepts）。

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

        /// <summary>
        /// 物品离开缓冲仓库时恢复 tick。
        /// </summary>
        private void UnfreezeItemTick(Thing t)
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
