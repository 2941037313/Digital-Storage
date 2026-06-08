using System;
using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using DigitalStorage.Services;
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
        internal struct WithdrawRecord
        {
            public Thing thing;
            public CoreLedger sourceLedger;
        }

        internal class TradeableEntry
        {
            public Tradeable tradeable;
            public List<WithdrawRecord> records = new List<WithdrawRecord>();
            public bool isNew; // 新建的 Tradeable 还是合并进已有
        }

        internal class DialogState
        {
            public List<TradeableEntry> entries = new List<TradeableEntry>();
            public bool injected;
            public bool dealExecuted;
            public bool beingReplaced; // DTI TryRemove → 跳过 PostClose 回滚+清理
        }

        private static readonly Dictionary<Dialog_Trade, DialogState> states
            = new Dictionary<Dialog_Trade, DialogState>();

        private static DialogState GetState(Dialog_Trade d)
        {
            if (!states.TryGetValue(d, out var s)) { s = new DialogState(); states[d] = s; }
            return s;
        }

        internal static DialogState GetStateFor(Dialog_Trade d)
        {
            states.TryGetValue(d, out var s);
            return s;
        }

        // ---------- injection ----------

        /// <summary>
        /// CacheTradeables Prefix 调用：提款 + 创建 Tradeable + 注入 deal.AllTradeables。
        /// 原版 CacheTradeables 会从 deal.AllTradeables 重建 cachedTradeables（排序+过滤）。
        /// </summary>
        public static void InjectCoreTradeables(Dialog_Trade dialog, List<Tradeable> dealList)
        {
            if (dealList == null) return;

var state = GetState(dialog);

            if (!state.injected)
                Log.Warning($"[DS-DTI] InjectCoreTradeables FIRST call: dealList={dealList.Count}");

            if (state.injected)
            {
                Log.Warning($"[DS-DTI] Re-inject: dealList={dealList.Count}");
                RemoveCoreTradeables(state, dealList);
            }

            var map = FindCurrentMap(dialog);
            if (map == null) { Log.Warning("[DS-DTI] InjectCoreTradeables: FindCurrentMap returned null"); return; }

            var merged = new Dictionary<ItemKey, MergedStock>();
            LedgerItemCollector.CollectCoreItems(map, merged, includeCrossMapInterfaces: false);
            if (merged.Count == 0) { Log.Warning("[DS-DTI] InjectCoreTradeables: merged.Count=0, nothing to inject"); return; }

            Log.Warning($"[DS-DTI] InjectCoreTradeables: {merged.Count} unique items, dealList before={dealList.Count}");

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
                dealList.Add(tr);

                // 白银等货币：合并到已有 CurrencyTradeable
                if (tr.IsCurrency)
                {
                    var existingCurrency = TradeSession.deal?.CurrencyTradeable;
                    if (existingCurrency != null && existingCurrency != tr)
                    {
                        existingCurrency.AddThing(thing, Transactor.Colony);
                        dealList.Remove(tr);
                        tr = existingCurrency;
                    }
                }

                entry.tradeable = tr;
                entry.isNew = true;
                state.entries.Add(entry);
            }

            Log.Warning($"[DS-DTI] Inject done: injected={state.entries.Count} items, dealList before={dealList.Count - state.entries.Count}→after={dealList.Count}");
            state.injected = true;
        }

        private static void RemoveCoreTradeables(DialogState state, List<Tradeable> dealList)
        {
            // 归还旧注入的物品
            RollbackInternal(state);

            // 从 deal.AllTradeables 中删除旧的注入条目
            for (int i = dealList.Count - 1; i >= 0; i--)
                foreach (var e in state.entries)
                    if (e.isNew && e.tradeable == dealList[i])
                        dealList.RemoveAt(i);
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
        }

        public static void MarkDealExecuted(Dialog_Trade dialog)
        {
            GetState(dialog).dealExecuted = true;
        }

        public static bool WasDealExecuted(Dialog_Trade dialog)
        {
            return states.TryGetValue(dialog, out var s) && s.dealExecuted;
        }

        /// <summary>DTI TryRemove 时标记——PostClose 不回滚不清理，留给 DTI 窗口关闭处理。</summary>
        public static void MarkBeingReplaced(Dialog_Trade dialog)
        {
            GetState(dialog).beingReplaced = true;
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
            // 只回滚已经不在窗口栈上的交易——防止 FloatMenu/通知等窗口 PostClose 误杀
            var toRollback = new List<DialogState>();
            var toRemove = new List<Dialog_Trade>();
            var openWindows = Find.WindowStack?.Windows;

            foreach (var kv in states)
            {
                // beingReplaced → 原 Dialog_Trade 被 DTI 换掉了，状态应回滚（除非 deal 已执行）
                bool shouldRollback = kv.Value.injected && kv.Value.entries.Count > 0
                    && (kv.Value.beingReplaced || !kv.Value.dealExecuted);
                if (shouldRollback)
                {
                    // 对话框仍在栈上（未替换）→ 等待用户操作，不回滚
                    bool isNativeDialog = !kv.Value.beingReplaced && openWindows != null && openWindows.Contains(kv.Key);
                    if (isNativeDialog)
                        continue;
                    // beingReplaced → 检查是否有 DTI 窗口还在栈上
                    if (kv.Value.beingReplaced && openWindows != null)
                    {
                        bool dtiOpen = false;
                        foreach (var w in openWindows)
                            if (w.GetType().Name == "Window_DynamicTrade")
                                { dtiOpen = true; break; }
                        if (dtiOpen) continue;
                    }
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
