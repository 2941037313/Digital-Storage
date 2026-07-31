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
    /// I2d 商队组队：把核心库存注入 Items Tab 的"数字存储"分区。
    /// 利用 TransferableOneWayWidget.AddSection() 复用原版 UI。
    /// 提款在 PostOpen（unspawned Thing），Close/Cancel 回滚，Send 时按 CountToTransfer 筛选。
    /// </summary>
    public static class CaravanDS_Helper
    {
        // ---------- per-dialog state ----------

        /// <summary>
        /// 单一条目：合并后的 Thing + 对应 Transferable + 各源账本提款凭证。
        /// 修复 X1：旧实现用 things/sourceLedgers（按源 ledger 计）与 transferables（按合并 key 计）
        /// 三个并行数组索引错位，导致退货退错核心、库存错乱。
        /// </summary>
        private class Entry
        {
            public Thing thing;
            public TransferableOneWay tw;
            public List<WithdrawSlip> slips = new List<WithdrawSlip>();
        }

        private class DialogState
        {
            public List<Entry> entries = new List<Entry>();
            public bool caravanSent;
            public bool injected;
        }

        private static readonly Dictionary<Dialog_FormCaravan, DialogState> states
            = new Dictionary<Dialog_FormCaravan, DialogState>();

        private static DialogState GetState(Dialog_FormCaravan d)
        {
            if (!states.TryGetValue(d, out var s)) { s = new DialogState(); states[d] = s; }
            return s;
        }

        // ---------- Inject ----------

        /// <summary>
        /// 注入核心物品分区。如果已注入过（injected=true）则跳过，避免 PostOpen 内重复注入。
        /// Reset 时 CalculateAndRecache postfix 先 Rollback（清除 injected）再重新注入。
        /// </summary>
        public static void InjectCoreItems(Dialog_FormCaravan dialog)
        {
            try { InjectCoreItemsInternal(dialog); }
            catch (System.Exception ex) { Log.Error($"[DS] InjectCoreItems: {ex}"); }
        }

        private static bool WidgetHasOurSection(TransferableOneWayWidget widget, string title)
        {
            if (widget == null) return false;
            var sectionsField = AccessTools.Field(typeof(TransferableOneWayWidget), "sections");
            if (sectionsField == null) return false;
            var sections = sectionsField.GetValue(widget) as System.Collections.IList;
            if (sections == null || sections.Count == 0) return false;
            var titleField = AccessTools.Field(sections[0].GetType(), "title");
            if (titleField == null) return false;
            foreach (var s in sections)
            {
                var t = titleField.GetValue(s) as string;
                if (t == title) return true;
            }
            return false;
        }

        private static void InjectCoreItemsInternal(Dialog_FormCaravan dialog)
        {
            var state = GetState(dialog);
            string title = "DS_CaravanTab".Translate();

            var widget = GetItemsTransfer(dialog);

            // 1) section 已存在 → 免疫所有重复调用（WorldRoutePlanner 临时关窗重开等）
            if (WidgetHasOurSection(widget, title))
                return;

            // 2) section 不存在但有旧数据 → widget 被重建（Reset 按钮）
            if (state.injected)
                _Rollback(dialog);

            // 3) 完整注入
            var map = AccessTools.Field(typeof(Dialog_FormCaravan), "map").GetValue(dialog) as Map;
            if (map == null) return;

            var merged = new Dictionary<ItemKey, MergedStock>();
            LedgerItemCollector.CollectCoreItems(map, merged);
            if (merged.Count == 0) return;

            var coreTransferables = new List<TransferableOneWay>();

            foreach (var kv in merged)
            {
                var key = kv.Key;
                if (key.def == null) continue;
                int available = (int)kv.Value.Avail;
                if (available <= 0) continue;

                var entry = new Entry();
                var thing = LedgerItemCollector.WithdrawFromLedgers(
                    key, available, kv.Value.Ledgers,
                    slip => { entry.slips.Add(slip); },
                    logSkipped: true);
                if (thing == null) continue;
                entry.thing = thing;

                var tw = new TransferableOneWay();
                tw.things.Add(thing);
                entry.tw = tw;
                coreTransferables.Add(tw);
                state.entries.Add(entry);
            }

            if (coreTransferables.Count == 0) return;

            if (widget != null)
                widget.AddSection(title, coreTransferables);

            state.injected = true;
        }

        // ---------- Rollback ----------

        public static void Rollback(Dialog_FormCaravan dialog)
        {
            try { _Rollback(dialog); }
            catch (System.Exception ex) { Log.Error($"[DS] Rollback: {ex}"); }
        }

        private static void _Rollback(Dialog_FormCaravan dialog)
        {
            var state = GetState(dialog);
            foreach (var entry in state.entries)
            {
                // 纯账本归还（slip 不依赖 Thing 存活）
                LedgerItemCollector.Rollback(entry.slips);
                if (entry.thing != null && !entry.thing.Destroyed)
                {
                    if (entry.thing.Spawned) entry.thing.DeSpawn(DestroyMode.Vanish);
                    entry.thing.Destroy(DestroyMode.Vanish);
                }
            }
            state.entries.Clear();
            state.injected = false;
        }

        // ---------- Finalize: caravan 确认后的清理 ----------

        /// <summary>
        /// Caravan 确认发送后调用。按 CountToTransfer 筛选：
        /// - 选中（>0）且 spawnOnMap：生成到表单平均位置附近
        /// - 选中（>0）且 !spawnOnMap（Reform）：不动，原版 AddItemsFromTransferables 处理
        /// - 未选中（=0）：归还账本 + 销毁 Thing
        /// </summary>
        public static void FinalizeCaravanItems(Dialog_FormCaravan dialog, Map map, bool spawnOnMap)
        {
            try { _Finalize(dialog, map, spawnOnMap); }
            catch (System.Exception ex) { Log.Error($"[DS] FinalizeCaravanItems: {ex}"); }
        }

        private static void _Finalize(Dialog_FormCaravan dialog, Map map, bool spawnOnMap)
        {
            var state = GetState(dialog);
            if (state.entries.Count == 0) return;

            IntVec3 avgPos = map.Center;
            if (spawnOnMap)
            {
                var pawns = TransferableUtility.GetPawnsFromTransferables(dialog.transferables);
                if (pawns.Count > 0)
                {
                    IntVec3 sum = IntVec3.Zero;
                    foreach (var p in pawns) sum += p.Position;
                    avgPos = sum / pawns.Count;
                }
            }

            for (int i = state.entries.Count - 1; i >= 0; i--)
            {
                var entry = state.entries[i];
                var thing = entry.thing;
                if (thing == null || thing.Destroyed) { state.entries.RemoveAt(i); continue; }

                int selected = entry.tw != null ? entry.tw.CountToTransfer : 0;

                if (selected <= 0)
                {
                    // 未选择 → 归还全部账本（按 slips 摊还到各源核心）
                    LedgerItemCollector.Rollback(entry.slips);
                    thing.Destroy(DestroyMode.Vanish);
                    state.entries.RemoveAt(i);
                }
                else
                {
                    if (selected < thing.stackCount)
                    {
                        if (!spawnOnMap)
                        {
                            // 8.1 bugfix(Reform 部分选择丢物品):TryReformCaravan 是原版
                            // 同步装载——Postfix 运行时选中量已被
                            // AddItemsFromTransferablesToRandomInventories 从 thing
                            // SplitOff 走,thing.stackCount 已是纯剩余(未选中),
                            // 直接按剩余退账并销毁(旧实现按「thing 还完整」假设退款错、
                            // 残留幻影物品丢失)。
                            LedgerItemCollector.Refund(entry.slips, thing.stackCount);
                            thing.Destroy(DestroyMode.Vanish);
                        }
                        else
                        {
                            // 部分选择：SplitOff 选中部分，剩余归还账本
                            int remain = thing.stackCount - selected;
                            thing.stackCount = remain;
                            LedgerItemCollector.Refund(entry.slips, remain);
                            thing.stackCount = selected;
                        }
                    }
                    if (spawnOnMap && !thing.Spawned)
                        GenPlace.TryPlaceThing(thing, avgPos, map, ThingPlaceMode.Near, null, null, default);
                    // Reform: 不 spawn；原版 AddItemsFromTransferablesToRandomInventories 分发pawn背包
                }
            }
        }

        /// <summary>
        /// 发车 Prefix：把核心 TransferableOneWay 合并到 dialog.transferables。
        /// </summary>
        public static void MergeToTransferables(Dialog_FormCaravan dialog)
        {
            var state = GetState(dialog);
            if (!state.injected || state.entries.Count == 0) return;
            foreach (var entry in state.entries)
            {
                if (entry.tw != null && !dialog.transferables.Contains(entry.tw))
                    dialog.transferables.Add(entry.tw);
            }
        }

        public static void MarkCaravanSent(Dialog_FormCaravan dialog)
        {
            GetState(dialog).caravanSent = true;
        }

        public static bool WasCaravanSent(Dialog_FormCaravan dialog)
        {
            return states.TryGetValue(dialog, out var s) && s.caravanSent;
        }

        // ---------- helpers ----------

        private static TransferableOneWayWidget GetItemsTransfer(Dialog_FormCaravan dialog)
        {
            var field = AccessTools.Field(typeof(Dialog_FormCaravan), "itemsTransfer");
            return field.GetValue(dialog) as TransferableOneWayWidget;
        }
    }
}
