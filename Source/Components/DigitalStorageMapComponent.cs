using System.Collections.Generic;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 地图级组件 —— v3 过渡态
    /// 只保留核心注册表，不再承担跨地图查找逻辑（迁移到 GameComponent 或账本层）。
    /// </summary>
    public class DigitalStorageMapComponent : MapComponent
    {
        private readonly List<Building_StorageCore> cores = new List<Building_StorageCore>();

        public DigitalStorageMapComponent(Map map) : base(map) { }

        public void RegisterCore(Building_StorageCore core)
        {
            if (core != null && !cores.Contains(core))
            {
                cores.Add(core);
            }
        }

        public void DeregisterCore(Building_StorageCore core)
        {
            if (core != null)
            {
                cores.Remove(core);
            }
        }

        public IReadOnlyList<Building_StorageCore> GetAllCores()
        {
            cores.RemoveAll(c => c == null || c.Destroyed);
            // Scribe 可能留重复引用，去重
            var seen = new HashSet<Building_StorageCore>();
            cores.RemoveAll(c => !seen.Add(c));
            return cores;
        }
    }
}
