using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Services;
using Verse;

namespace DigitalStorage.Core
{
    /// <summary>
    /// 共享模块：收集所有可访问核心的账本库存 + 提款 + 回滚。
    /// CaravanDS_Helper 和 TradeDS_Helper 共用，消除 ~200 行重复代码。
    /// </summary>
    public struct MergedStock
    {
        public long Avail;
        public List<CoreLedger> Ledgers;
    }

    public static class LedgerItemCollector
    {
        /// <summary>
        /// 获取所有可用的核心（本地图 + 同 NetworkName 远程核心）。
        /// CoreFinder 和 CollectCoreItems 共用，消除重复的核心发现循环。
        /// </summary>
        public static List<Building_StorageCore> GetAllUsableCores(Map map,
            bool includeCrossMapInterfaces = true)
        {
            var result = new List<Building_StorageCore>();
            var seenCores = new HashSet<Building_StorageCore>();

            var mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp != null)
                foreach (var c in mapComp.GetAllCores())
                    if (CoreFinder.IsUsable(c) && seenCores.Add(c))
                        result.Add(c);

            var gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp != null)
            {
                foreach (var c in gameComp.GetAllCores())
                {
                    if (c.Map == map) continue;
                    if (!CoreFinder.IsUsable(c)) continue;
                    if (string.IsNullOrEmpty(c.NetworkName)) continue;
                    if (!seenCores.Add(c)) continue;

                    bool hasPeer = result.Exists(lc =>
                        CoreFinder.IsUsable(lc) && lc.NetworkName == c.NetworkName);
                    if (!hasPeer && includeCrossMapInterfaces)
                    {
                        foreach (var cell in c.GetProxyCells())
                            if (cell.InBounds(map)) { hasPeer = true; break; }
                    }
                    if (hasPeer) result.Add(c);
                }
            }
            return result;
        }

        /// <summary>
        /// 收集本地图 + 同 NetworkName 远程核心的所有 ItemKey，按 key 合并总量。
        /// </summary>
        public static void CollectCoreItems(Map map, Dictionary<ItemKey, MergedStock> merged,
            bool includeCrossMapInterfaces = true)
        {
            var allCores = GetAllUsableCores(map, includeCrossMapInterfaces);
            foreach (var core in allCores)
            {
                var ledger = core.Ledger;
                foreach (var kv in ledger.Stock)
                {
                    if (kv.Value <= 0) continue;
                    long avail = ledger.Available(kv.Key);
                    if (avail <= 0) continue;
                    if (merged.TryGetValue(kv.Key, out var existing))
                    {
                        existing.Avail += avail;
                        existing.Ledgers.Add(ledger);
                    }
                    else
                    {
                        merged[kv.Key] = new MergedStock
                        {
                            Avail = avail,
                            Ledgers = new List<CoreLedger> { ledger }
                        };
                    }
                }
            }
        }

        /// <summary>
        /// 从合并的多核心账本提款，合并成一个 Thing。
        /// 返回合并后的 Thing（unspawned），如果所有 ledgers 都无货则返回 null。
        /// stateCallback: 每提款一个 Thing 时回调 (thing, sourceLedger) 用于状态追踪。
        /// logSkipped: 遇到 LabelNoCount 抛异常的 Thing 时是否写 Warning 日志。
        /// </summary>
        public static Thing WithdrawFromLedgers(
            ItemKey key, int total, List<CoreLedger> ledgers,
            System.Action<Thing, CoreLedger> stateCallback,
            bool logSkipped = true)
        {
            int remaining = total;
            Thing firstThing = null;

            foreach (var ledger in ledgers)
            {
                if (remaining <= 0) break;
                var thing = ledger.Withdraw(key, remaining, null);
                if (thing == null) continue;

                try { var _ = thing.LabelNoCount; }
                catch (System.Exception ex)
                {
                    if (logSkipped)
                        Log.Warning($"[DS] Skipping item {key}: {ex.Message}");
                    ledger.AddRaw(key, thing.stackCount);
                    thing.Destroy(DestroyMode.Vanish);
                    continue;
                }

                if (firstThing == null) firstThing = thing;
                else
                {
                    firstThing.stackCount += thing.stackCount;
                    thing.Destroy(DestroyMode.Vanish);
                }

                stateCallback?.Invoke(thing, ledger);
                remaining -= thing.stackCount;
            }
            return firstThing;
        }

        /// <summary>
        /// 回滚：把 state 里追踪的 Thing 全部归还账本并销毁。
        /// </summary>
        public static void Rollback(
            List<Thing> things, List<CoreLedger> sourceLedgers)
        {
            for (int i = 0; i < things.Count; i++)
            {
                var thing = things[i];
                if (thing == null || thing.Destroyed) continue;
                var key = ItemKey.Of(thing);
                if (i < sourceLedgers.Count && sourceLedgers[i] != null)
                    sourceLedgers[i].AddRaw(key, thing.stackCount);
                if (thing.Spawned) thing.DeSpawn(DestroyMode.Vanish);
                thing.Destroy(DestroyMode.Vanish);
            }
        }
    }
}
