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

            // 4.0：这段「核心 → 高级储存区」的主动搬运已删除 —— 它现在由原版自己完成（甲-1，已实测）。
            //
            // 容器是 IHaulDestination，ListerHaulables.ShouldBeHaulable 通过
            //   IsInAnyStorage() => CurrentHaulDestinationOf(t)?.Accepts(t)
            // 判断「它还在有效存储里吗」。玩家把某类物品从过滤器去掉 ⇒ Accepts 变 false
            // ⇒ 原版派 HaulToCell 作业把它搬到更合适的储存区。
            //
            // 也就是说「优先级调度」不再需要 mod 主动做：它是容器过滤器的自然结果。
            // 搬入方向同样原生（StoreUtility.TryFindBestBetterNonSlotGroupStorageFor）。
            //
            // 原来用于轮转调度的 rrIndex / lastPriorityScanTick 保留字段以便将来恢复限速逻辑。
            _ = map;
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

    }
}
