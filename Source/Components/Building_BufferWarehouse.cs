using System.Collections.Generic;
using System.Linq;
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
    public class Building_BufferWarehouse : Building_Storage
    {
        private Building_StorageCore boundCore;

        public Building_StorageCore BoundCore => boundCore;

        // ========== 生命周期 ==========

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            // 自动绑定同 NetworkName 核心
            if (boundCore == null || boundCore.Destroyed)
                TryAutoBind();

            // 加载后冻结已有物品
            if (respawningAfterLoad && slotGroup != null)
            {
                foreach (var t in slotGroup.HeldThings)
                    FreezeItemTick(t);
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
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

        /// <summary>
        /// 遮盖原版 Accepts，加入绑定核心状态检查。
        /// </summary>
        public new bool Accepts(Thing t)
        {
            if (boundCore == null || boundCore.Destroyed || !boundCore.Spawned)
                return false;
            // 核心通电检查
            if (!boundCore.Powered) return false;
            // 委托原版储存筛选
            return GetStoreSettings().AllowedToAccept(t);
        }

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
        private void FreezeItemTick(Thing t)
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

        // ========== Gizmo + FloatMenu ==========

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos())
                yield return g;

            if (boundCore != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DS_BufferBoundTo".Translate(boundCore.LabelCap),
                    defaultDesc = "DS_BufferBoundToDesc".Translate(),
                    icon = TexCommand.DesirePower,
                    action = delegate
                    {
                        Find.WindowStack.Add(new FloatMenu(GetBindOptions().ToList()));
                    }
                };
            }
            else
            {
                yield return new Command_Action
                {
                    defaultLabel = "DS_BufferBindCore".Translate(),
                    defaultDesc = "DS_BufferBindCoreDesc".Translate(),
                    icon = TexCommand.DesirePower,
                    action = delegate
                    {
                        Find.WindowStack.Add(new FloatMenu(GetBindOptions().ToList()));
                    }
                };
            }
        }

        private IEnumerable<FloatMenuOption> GetBindOptions()
        {
            var mapComp = Map?.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) yield break;

            // 解绑选项
            if (boundCore != null)
            {
                yield return new FloatMenuOption("DS_BufferUnbind".Translate(), delegate
                {
                    UnbindFromCore();
                });
            }

            // 所有可用核心
            foreach (var core in mapComp.GetAllCores())
            {
                if (core == null || core.Destroyed || !core.Spawned) continue;
                var c = core; // capture
                string label = core == boundCore
                    ? "DS_BufferCurrentCore".Translate(c.LabelCap, c.NetworkName)
                    : "DS_BufferSelectCore".Translate(c.LabelCap, c.NetworkName);
                yield return new FloatMenuOption(label, delegate
                {
                    BindToCore(c);
                });
            }
        }

        // ========== 序列化 ==========

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref boundCore, "boundCore");
        }
    }
}
