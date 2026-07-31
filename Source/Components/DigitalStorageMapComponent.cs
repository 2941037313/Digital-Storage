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
        private int rrIndex; // M2: 轮转指针——多核心公平调度，避免第一个核心垄断搬出

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

            // M2: 轮转调度——从 rrIndex 开始绕一圈，每 60 tick 只处理一颗核心（保持限速），
            // 但机会均分，不再让注册序靠前的核心永久垄断搬出
            for (int i = 0; i < liveCores.Count; i++)
            {
                var core = liveCores[(rrIndex + i) % liveCores.Count];
                if (core == null || !core.Powered) continue;

                // 搬出: 核心→高级储存区（直接放进目标 zone 格）
                // 搬入已迁移到 WorkGiver_DS_StorageToCore
                if (ItemRouter.RouteCoreToStorage(core, map))
                {
                    rrIndex = (rrIndex + i + 1) % liveCores.Count;
                    return;
                }
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
