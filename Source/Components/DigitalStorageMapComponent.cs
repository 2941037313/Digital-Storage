using System.Collections.Generic;
using DigitalStorage.Services;
using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 地图级组件 —— 核心注册表 + 存储优先级调度。
    /// </summary>
    public class DigitalStorageMapComponent : MapComponent
    {
        private readonly List<Building_StorageCore> cores = new List<Building_StorageCore>();
        private readonly List<Building_BufferWarehouse> buffers = new List<Building_BufferWarehouse>();
        private List<Building_StorageCore> cachedCleanList;
        private int lastCleanTick = -1;
        private int lastPriorityScanTick = -1;

        public DigitalStorageMapComponent(Map map) : base(map) { }

        public static DigitalStorageMapComponent For(Map map)
            => map?.GetComponent<DigitalStorageMapComponent>();

        public override void MapComponentTick()
        {
            base.MapComponentTick();

            int tick = Find.TickManager.TicksGame;
            if (tick - lastPriorityScanTick < 60) return;
            lastPriorityScanTick = tick;

            var liveCores = GetAllCores();
            if (liveCores.Count == 0) return;

            foreach (var core in liveCores)
            {
                if (core == null || !core.Powered) continue;

                // 搬出: 核心→高级储存区（spawn物品→原版haul接管）
                // 搬入已迁移到 WorkGiver_DS_StorageToCore
                if (ItemRouter.RouteCoreToStorage(core, map))
                    return;
            }
        }

        public void RegisterCore(Building_StorageCore core)
        {
            if (core != null && !cores.Contains(core))
            {
                cores.Add(core);
                cachedCleanList = null;
            }
        }

        public void DeregisterCore(Building_StorageCore core)
        {
            if (core != null)
            {
                cores.Remove(core);
                cachedCleanList = null;
            }
        }

        public IReadOnlyList<Building_StorageCore> GetAllCores()
        {
            int tick = Find.TickManager.TicksGame;
            if (cachedCleanList != null && tick - lastCleanTick < 60)
                return cachedCleanList;

            // 清理死引用（低频，60 tick 一次）
            cores.RemoveAll(c => c == null || c.Destroyed);
            lastCleanTick = tick;

            // 去重
            if (cores.Count > 1)
            {
                var seen = new HashSet<Building_StorageCore>();
                for (int i = cores.Count - 1; i >= 0; i--)
                    if (!seen.Add(cores[i]))
                        cores.RemoveAt(i);
            }

            cachedCleanList = new List<Building_StorageCore>(cores);
            return cachedCleanList;
        }

        public void RegisterBufferWarehouse(Building_BufferWarehouse bw)
        {
            if (bw != null && !buffers.Contains(bw))
                buffers.Add(bw);
        }

        public void DeregisterBufferWarehouse(Building_BufferWarehouse bw)
        {
            if (bw != null)
                buffers.Remove(bw);
        }

        public IReadOnlyList<Building_BufferWarehouse> GetAllBufferWarehouses()
        {
            buffers.RemoveAll(b => b == null || b.Destroyed);
            return buffers;
        }
    }
}
