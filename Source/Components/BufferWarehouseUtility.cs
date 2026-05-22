using DigitalStorage.Core;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 7g: 从地图上所有缓冲仓库尝试取物理物品的共享工具。
    /// </summary>
    public static class BufferWarehouseUtility
    {
        /// <summary>
        /// 尝试从任何缓冲仓库 SlotGroup 中取出指定 ItemKey 的物品。
        /// 返回取出的 Thing（unspawned），失败返回 null。
        /// </summary>
        public static Thing TryTakeFromAny(Map map, ItemKey key, int count)
        {
            if (map == null || count <= 0) return null;
            var mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return null;

            var buffers = mapComp.GetAllBufferWarehouses();
            for (int i = 0; i < buffers.Count; i++)
            {
                var bw = buffers[i];
                if (bw == null || bw.Destroyed || !bw.Spawned) continue;

                var slot = bw.GetSlotGroup();
                if (slot == null) continue;

                foreach (var t in slot.HeldThings)
                {
                    if (t.Destroyed) continue;
                    if (!ItemKey.Of(t).Equals(key)) continue;

                    int take = count > t.stackCount ? t.stackCount : count;
                    Thing result = t.SplitOff(take);
                    return result; // SplitOff creates unspawned Thing
                }
            }
            return null;
        }
    }
}
