using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using DigitalStorage.Services;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// I4a 贸易：核心库存注入交易列表。提款创建 unspawned Thing → Tradeable → 加入列表。
    /// Widgets.ListSeparator 由 FillMainRect transpiler 负责插入。
    /// </summary>
    public static class TradeDS_Helper
    {
        private class DialogState
        {
            public List<Thing> things = new List<Thing>();
            public List<CoreLedger> sourceLedgers = new List<CoreLedger>();
            public List<Tradeable> tradeables = new List<Tradeable>();
            public int coreStartIndex = -1;
            public bool injected;
            public bool dealExecuted;
        }

        private static readonly Dictionary<Dialog_Trade, DialogState> states
            = new Dictionary<Dialog_Trade, DialogState>();

        private static DialogState GetState(Dialog_Trade d)
        {
            if (!states.TryGetValue(d, out var s)) { s = new DialogState(); states[d] = s; }
            return s;
        }

        // ---------- injection ----------

        /// <summary>
        /// CacheTradeables Postfix 调用：提款 + 创建 Tradeable + 追加到 cachedTradeables。
        /// </summary>
        public static void InjectCoreTradeables(Dialog_Trade dialog, List<Tradeable> cachedList)
        {
            var state = GetState(dialog);

            // 如果已注入过（CacheTradeables 被排序变更触发多次），先清理旧注入
            if (state.injected)
                RemoveCoreTradeables(state, cachedList);

            var map = FindCurrentMap(dialog);
            if (map == null) return;

            var merged = new Dictionary<ItemKey, MergedStock>();
            CollectCoreItems(map, merged);
            if (merged.Count == 0) return;

            state.coreStartIndex = cachedList.Count;

            foreach (var kv in merged)
            {
                var key = kv.Key;
                if (key.def == null) continue;
                int available = (int)kv.Value.Avail;
                if (available <= 0) continue;

                var thing = WithdrawFromLedgers(key, available, kv.Value.Ledgers, state);
                if (thing == null) continue;

                var tr = new Tradeable();
                tr.AddThing(thing, Transactor.Colony);
                cachedList.Add(tr);
                state.tradeables.Add(tr);
            }

            state.injected = true;
        }

        /// <summary>
        /// Sort/Filter 触发 CacheTradeables 重新执行时清理旧注入。
        /// </summary>
        private static void RemoveCoreTradeables(DialogState state, List<Tradeable> cachedList)
        {
            for (int i = cachedList.Count - 1; i >= state.coreStartIndex && i >= 0; i--)
                cachedList.RemoveAt(i);
            state.tradeables.Clear();
            state.coreStartIndex = -1;
        }

        // ---------- rollback ----------

        public static void Rollback(Dialog_Trade dialog)
        {
            var state = GetState(dialog);
            RollbackInternal(state);
        }

        private static void RollbackInternal(DialogState state)
        {
            for (int i = 0; i < state.things.Count; i++)
            {
                var thing = state.things[i];
                if (thing == null || thing.Destroyed) continue;
                var key = ItemKey.Of(thing);
                if (i < state.sourceLedgers.Count && state.sourceLedgers[i] != null)
                    state.sourceLedgers[i].AddRaw(key, thing.stackCount);
                if (thing.Spawned) thing.DeSpawn(DestroyMode.Vanish);
                thing.Destroy(DestroyMode.Vanish);
            }
            state.things.Clear();
            state.sourceLedgers.Clear();
            state.tradeables.Clear();
            state.injected = false;
            state.coreStartIndex = -1;
        }

        public static void MarkDealExecuted(Dialog_Trade dialog)
        {
            GetState(dialog).dealExecuted = true;
        }

        public static bool WasDealExecuted(Dialog_Trade dialog)
        {
            return states.TryGetValue(dialog, out var s) && s.dealExecuted;
        }

        /// <summary>
        /// I4b: 成交后清理。Tradeable.CountToTransfer > 0 → 已售不归还；= 0 → 归还账本。
        /// ResolveTrade 用 TransferNoSplit 不销毁原 Thing，不能用 thing.Destroyed 判断。
        /// </summary>
        public static void CleanupAfterDeal(Dialog_Trade dialog)
        {
            var state = GetState(dialog);
            if (state.things.Count == 0) return;

            for (int i = state.things.Count - 1; i >= 0; i--)
            {
                var thing = state.things[i];
                if (thing == null) { Remove(state, i); continue; }

                var tr = i < state.tradeables.Count ? state.tradeables[i] : null;
                bool sold = tr != null && tr.CountToTransferToDestination > 0;

                if (!sold)
                {
                    // 未售出 → 归还账本
                    if (thing.stackCount > 0 && i < state.sourceLedgers.Count && state.sourceLedgers[i] != null)
                        state.sourceLedgers[i].AddRaw(ItemKey.Of(thing), thing.stackCount);
                }
                // 已售：TradeDeal.ResolveTrade 已转让，thing 归交易方，不归我们管

                if (!sold)
                    thing.Destroy(DestroyMode.Vanish);
                Remove(state, i);
            }
        }

        private static void Remove(DialogState state, int i)
        {
            state.things.RemoveAt(i);
            if (i < state.sourceLedgers.Count) state.sourceLedgers.RemoveAt(i);
            if (i < state.tradeables.Count) state.tradeables.RemoveAt(i);
        }

        // ---------- helpers ----------

        private static Map FindCurrentMap(Dialog_Trade dialog)
        {
            var negotiator = TradeSession.playerNegotiator;
            if (negotiator != null && negotiator.Map != null) return negotiator.Map;
            // 商队贸易场景：取殖民地所在的地图
            return Find.CurrentMap;
        }

        private struct MergedStock
        {
            public long Avail;
            public List<CoreLedger> Ledgers;
        }

        private static void CollectCoreItems(Map map, Dictionary<ItemKey, MergedStock> merged)
        {
            var seenCores = new HashSet<Building_StorageCore>();
            var allCores = new List<Building_StorageCore>();

            var mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp != null)
                foreach (var c in mapComp.GetAllCores())
                    if (CoreFinder.IsUsable(c) && seenCores.Add(c)) allCores.Add(c);

            var gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp != null)
            {
                foreach (var c in gameComp.GetAllCores())
                {
                    if (c.Map == map) continue;
                    if (!CoreFinder.IsUsable(c)) continue;
                    if (string.IsNullOrEmpty(c.NetworkName)) continue;
                    if (!seenCores.Add(c)) continue;
                    bool hasPeer = allCores.Exists(lc => CoreFinder.IsUsable(lc) && lc.NetworkName == c.NetworkName);
                    if (hasPeer) allCores.Add(c);
                }
            }

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
                        merged[kv.Key] = new MergedStock { Avail = avail, Ledgers = new List<CoreLedger> { ledger } };
                    }
                }
            }
        }

        private static Thing WithdrawFromLedgers(ItemKey key, int total, List<CoreLedger> ledgers, DialogState state)
        {
            int remaining = total;
            Thing firstThing = null;

            foreach (var ledger in ledgers)
            {
                if (remaining <= 0) break;
                var thing = ledger.Withdraw(key, remaining, null);
                if (thing == null) continue;

                try { var _ = thing.LabelNoCount; }
                catch (System.Exception)
                {
                    ledger.AddRaw(key, thing.stackCount);
                    thing.Destroy(DestroyMode.Vanish);
                    continue;
                }

                if (firstThing == null) firstThing = thing;
                else { firstThing.stackCount += thing.stackCount; thing.Destroy(DestroyMode.Vanish); }

                state.things.Add(thing);
                state.sourceLedgers.Add(ledger);
                remaining -= thing.stackCount;
            }
            return firstThing;
        }
    }
}
