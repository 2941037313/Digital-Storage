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

    /// <summary>
    /// 单次提款凭证：记录「从哪个账本提了多少」。
    /// 回滚只依赖 (ledger, key, count) 三元组，不依赖 Thing 存活状态——
    /// 修复 I10.01.7(H6)：多核心合并提款时第 2+ 核心的 Thing 在合并中被销毁，
    /// 若回滚依赖 Thing 引用会导致该核心库存永久丢失。
    /// </summary>
    public struct WithdrawSlip
    {
        public CoreLedger ledger;
        public ItemKey key;
        public int count;
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
                    // 4.0 删了接口建筑（Building_InputInterface / GetProxyCells 已移除）：
                    // 跨图核心现在只认「本地有同 NetworkName 的核心」这一条路。
                    // 整个跨图逻辑在批 3 随账本一起删。
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
        /// slipCallback: 每个源账本提款时回调 WithdrawSlip（在 Thing 合并销毁之前）。
        /// logSkipped: 遇到 LabelNoCount 抛异常的 Thing 时是否写 Warning 日志。
        /// </summary>
        public static Thing WithdrawFromLedgers(
            ItemKey key, int total, List<CoreLedger> ledgers,
            System.Action<WithdrawSlip> slipCallback,
            bool logSkipped = true)
        {
            int remaining = total;
            Thing firstThing = null;

            foreach (var ledger in ledgers)
            {
                if (remaining <= 0) break;
                // 合并场景需要一个 Thing 代表全部库存 → 允许超限堆叠
                var thing = ledger.Withdraw(key, remaining, null, allowOverstack: true);
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

                int got = thing.stackCount;
                // 回调必须在 Destroy 之前：slip 记录不依赖 Thing 存活
                slipCallback?.Invoke(new WithdrawSlip { ledger = ledger, key = key, count = got });

                if (firstThing == null) firstThing = thing;
                else
                {
                    firstThing.stackCount += got;
                    thing.Destroy(DestroyMode.Vanish);
                }
                remaining -= got;
            }
            return firstThing;
        }

        /// <summary>
        /// 回滚：按 slips 纯账本归还，不碰 Thing。
        /// </summary>
        public static void Rollback(List<WithdrawSlip> slips)
        {
            foreach (var s in slips)
                s.ledger?.AddRaw(s.key, s.count);
            slips.Clear();
        }

        /// <summary>
        /// 按 slips 摊还归还指定数量（成交/发送后剩余物品归还账本）。
        /// 按 slips 顺序逐个归还，最后一个 slip 兜底剩余量，总量守恒。
        /// </summary>
        public static void Refund(List<WithdrawSlip> slips, int amount)
        {
            if (amount <= 0) return;
            int rest = amount;
            for (int i = 0; i < slips.Count; i++)
            {
                var s = slips[i];
                int back = (i == slips.Count - 1) ? rest
                    : (rest > s.count ? s.count : rest);
                if (back <= 0) continue;
                s.ledger?.AddRaw(s.key, back);
                rest -= back;
                if (rest <= 0) return;
            }
        }
    }
}
