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
        private struct WithdrawRecord
        {
            public Thing thing;
            public CoreLedger sourceLedger;
        }

        private class TradeableEntry
        {
            public Tradeable tradeable;
            public List<WithdrawRecord> records = new List<WithdrawRecord>();
        }

        private class DialogState
        {
            public List<TradeableEntry> entries = new List<TradeableEntry>();
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
                int available = (int)System.Math.Min(kv.Value.Avail, (long)int.MaxValue);
                if (available <= 0) continue;

                var entry = new TradeableEntry();

                var thing = LedgerItemCollector.WithdrawFromLedgers(
                    key, available, kv.Value.Ledgers,
                    (t, l) => { entry.records.Add(new WithdrawRecord { thing = t, sourceLedger = l }); },
                    logSkipped: false);
                if (thing == null) continue;

                var tr = new Tradeable();
                tr.AddThing(thing, Transactor.Colony);
                TradeSession.deal.AllTradeables.Add(tr);
                cachedList.Add(tr);

                if (tr.IsCurrency)
                {
                    var currencyField = AccessTools.Field(typeof(Dialog_Trade), "cachedCurrencyTradeable");
                    var existing = currencyField.GetValue(dialog) as Tradeable;
                    if (existing != null && existing != tr)
                        existing.AddThing(thing, Transactor.Colony);
                    else
                        currencyField.SetValue(dialog, tr);
                }
                entry.tradeable = tr;
                state.entries.Add(entry);
            }

            state.injected = true;
        }

        private static void RemoveCoreTradeables(DialogState state, List<Tradeable> cachedList)
        {
            // 捕获旧的 deal tradeables（归还前）
            var toRemove = new HashSet<Tradeable>();
            foreach (var e in state.entries) toRemove.Add(e.tradeable);

            // 归还旧注入的物品
            RollbackInternal(state);

            for (int i = cachedList.Count - 1; i >= state.coreStartIndex && i >= 0; i--)
                cachedList.RemoveAt(i);
            var dealTradeables = TradeSession.deal?.AllTradeables;
            if (dealTradeables != null)
            {
                for (int i = dealTradeables.Count - 1; i >= 0; i--)
                    if (toRemove.Contains(dealTradeables[i]))
                        dealTradeables.RemoveAt(i);
            }
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
            foreach (var entry in state.entries)
            {
                foreach (var rec in entry.records)
                {
                    if (rec.thing == null || rec.thing.Destroyed) continue;
                    rec.sourceLedger?.AddRaw(ItemKey.Of(rec.thing), rec.thing.stackCount);
                    if (rec.thing.Spawned) rec.thing.DeSpawn(DestroyMode.Vanish);
                    rec.thing.Destroy(DestroyMode.Vanish);
                }
            }
            state.entries.Clear();
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
        /// 对话框关闭时清理 state（防止内存泄漏 + 跨 mod 冲突）
        /// </summary>
        public static void CleanupState(Dialog_Trade dialog)
        {
            states.Remove(dialog);
        }

        /// <summary>
        /// 标记最近注入的交易为"已执行"——不依赖 Dialog_Trade 在窗口栈上。
        /// （兼容 Dynamic Trade Interface 等替换原版交易窗口的 mod）
        /// </summary>
        public static void MarkDealExecutedForAnyActiveDialog()
        {
            foreach (var kv in states)
            {
                if (kv.Value.injected && !kv.Value.dealExecuted && kv.Value.entries.Count > 0)
                {
                    kv.Value.dealExecuted = true;
                    return;
                }
            }
        }

        /// <summary>
        /// 成交后清理——同不依赖 Dialog_Trade 在窗口栈上。
        /// </summary>
        public static void CleanupAfterDealForAnyActiveDialog()
        {
            foreach (var kv in states)
            {
                if (kv.Value.dealExecuted && kv.Value.entries.Count > 0)
                {
                    CleanupAfterDealEntries(kv.Value);
                    kv.Value.injected = false;
                    return;
                }
            }
        }

        /// <summary>
        /// 对 states 中所有未执行的注入进行回滚。
        /// （兼容替换原版窗口的 mod——窗口关闭时不一定是 Dialog_Trade）
        /// </summary>
        public static void RollbackAndCleanupIfNotExecuted()
        {
            var toRollback = new List<DialogState>();
            var toRemove = new List<Dialog_Trade>();
            foreach (var kv in states)
            {
                if (kv.Value.injected && !kv.Value.dealExecuted && kv.Value.entries.Count > 0)
                {
                    toRollback.Add(kv.Value);
                    toRemove.Add(kv.Key);
                }
            }
            foreach (var state in toRollback)
                RollbackInternal(state);
            foreach (var key in toRemove)
                states.Remove(key);
        }

        private static void CleanupAfterDealEntries(DialogState state)
        {
            foreach (var entry in state.entries)
            {
                foreach (var rec in entry.records)
                {
                    if (rec.thing == null) continue;
                    if (rec.thing.stackCount > 0 && rec.sourceLedger != null)
                        rec.sourceLedger.AddRaw(ItemKey.Of(rec.thing), rec.thing.stackCount);
                    if (!rec.thing.Destroyed)
                        rec.thing.Destroy(DestroyMode.Vanish);
                }
            }
            state.entries.Clear();
        }

        /// <summary>
        /// 成交后清理。Tradeable.CountToTransferToDestination > 0 → 已售不归还；= 0 → 归还账本。
        /// </summary>
        public static void CleanupAfterDeal(Dialog_Trade dialog)
        {
            var state = GetState(dialog);
            if (state.entries.Count == 0) return;
            CleanupAfterDealEntries(state);
            state.injected = false;
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
