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
            LedgerItemCollector.CollectCoreItems(map, merged, includeCrossMapInterfaces: false);
            if (merged.Count == 0) return;

            state.coreStartIndex = cachedList.Count;

            foreach (var kv in merged)
            {
                var key = kv.Key;
                if (key.def == null) continue;
                int available = (int)kv.Value.Avail;
                if (available <= 0) continue;

                var thing = LedgerItemCollector.WithdrawFromLedgers(
                    key, available, kv.Value.Ledgers,
                    (t, l) => { state.things.Add(t); state.sourceLedgers.Add(l); },
                    logSkipped: false);
                if (thing == null) continue;

                var tr = new Tradeable();
                tr.AddThing(thing, Transactor.Colony);
                // 所有核心 Tradeable 必须同时加入 TradeDeal.tradeables——UpdateCurrencyCount
                // 遍历后者算价格。cachedTradeables 只是 UI 渲染列表。
                TradeSession.deal.AllTradeables.Add(tr);
                cachedList.Add(tr);

                if (tr.IsCurrency)
                {
                    // 和现有的货币 Tradeable 合并（殖民地可能已有白银在地图上）
                    var currencyField = AccessTools.Field(typeof(Dialog_Trade), "cachedCurrencyTradeable");
                    var existing = currencyField.GetValue(dialog) as Tradeable;
                    if (existing != null && existing != tr)
                    {
                        existing.AddThing(thing, Transactor.Colony);
                        Log.Warning($"[DS I4] Silver: merged into existing currency, +{available}");
                    }
                    else
                    {
                        currencyField.SetValue(dialog, tr);
                        Log.Warning($"[DS I4] Silver: set new currency, available={available}");
                    }
                }
                state.tradeables.Add(tr);
            }

            state.injected = true;
        }

        private static void RemoveCoreTradeables(DialogState state, List<Tradeable> cachedList)
        {
            // 从 cachedTradeables 清除
            for (int i = cachedList.Count - 1; i >= state.coreStartIndex && i >= 0; i--)
                cachedList.RemoveAt(i);
            // 从 TradeDeal.tradeables 清除——否则 sort 重 Cache 累计重复
            var dealTradeables = TradeSession.deal?.AllTradeables;
            if (dealTradeables != null)
            {
                for (int i = dealTradeables.Count - 1; i >= 0; i--)
                    if (state.tradeables.Contains(dealTradeables[i]))
                        dealTradeables.RemoveAt(i);
            }
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
            Log.Warning($"[DS I4] Rollback: returning {state.things.Count} things to ledger");
            LedgerItemCollector.Rollback(state.things, state.sourceLedgers);
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
            return Find.CurrentMap;
        }
    }
}
