using System.Collections.Generic;
using DigitalStorage.Components;
using Verse;

namespace DigitalStorage.Services
{
    /// <summary>
    /// 游戏级组件 —— v3 过渡态
    /// 只保留跨地图的核心注册表。
    /// 跨地图查找、异步掉落、物品转换等逻辑将由账本层接管。
    /// </summary>
    public class DigitalStorageGameComponent : GameComponent
    {
        private List<Building_StorageCore> globalCores = new List<Building_StorageCore>();

        public DigitalStorageGameComponent(Game game) { }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref globalCores, "globalCores", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.LoadingVars && globalCores == null)
            {
                globalCores = new List<Building_StorageCore>();
            }
        }

        public void RegisterCore(Building_StorageCore core)
        {
            if (core != null && !globalCores.Contains(core))
            {
                globalCores.Add(core);
            }
        }

        public void DeregisterCore(Building_StorageCore core)
        {
            if (core != null)
            {
                globalCores.Remove(core);
            }
        }

        public List<Building_StorageCore> GetAllCores()
        {
            globalCores.RemoveAll(c => c == null || c.Destroyed);
            return globalCores;
        }
    }
}
