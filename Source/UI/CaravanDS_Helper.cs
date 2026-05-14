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

        private class DialogState
        {
            public List<Thing> things = new List<Thing>();
            public List<CoreLedger> sourceLedgers = new List<CoreLedger>();
            public List<TransferableOneWay> transferables = new List<TransferableOneWay>();
            public bool caravanSent;
            public bool injected;
            public bool sectionAdded;
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

        private static void InjectCoreItemsInternal(Dialog_FormCaravan dialog)
        {
            var state = GetState(dialog);
            Log.Warning($"[DS] InjectCoreItems: injected={state.injected}, sectionAdded={state.sectionAdded}, things.Count={state.things.Count}");

            if (state.injected) return;

            var map = AccessTools.Field(typeof(Dialog_FormCaravan), "map").GetValue(dialog) as Map;
            if (map == null) return;

            var merged = new Dictionary<ItemKey, MergedStock>();
            CollectCoreItems(map, merged);
            Log.Warning($"[DS] CollectCoreItems: merged.Count={merged.Count}");
            if (merged.Count == 0) return;

            var coreTransferables = new List<TransferableOneWay>();

            foreach (var kv in merged)
            {
                var key = kv.Key;
                if (key.def == null) continue;
                int available = (int)kv.Value.Avail;
                if (available <= 0) continue;

                var thing = WithdrawFromLedgers(key, available, kv.Value.Ledgers, state);
                if (thing == null) continue;

                var tw = new TransferableOneWay();
                tw.things.Add(thing);
                // 不加到 dialog.transferables！CreateCaravanTransferableWidgets 内 lazy query
                // 会在渲染时遍历 transferables，触发 GetTransferableCategory NRE。
                // 发车时在 Prefix 里加入（CheckForErrors + StartFormingCaravan 拿到完整列表）。
                coreTransferables.Add(tw);
                state.transferables.Add(tw);
            }

            Log.Warning($"[DS] Created {coreTransferables.Count} transferables, state.things.Count={state.things.Count}");
            if (coreTransferables.Count == 0) return;

            var widget = GetItemsTransfer(dialog);
            if (widget != null && !state.sectionAdded)
            {
                widget.AddSection("DS_CaravanTab".Translate(), coreTransferables);
                state.sectionAdded = true;
                Log.Warning($"[DS] AddSection called with {coreTransferables.Count} items");
            }
            else
            {
                Log.Warning($"[DS] AddSection skipped: widget={widget != null}, sectionAdded={state.sectionAdded}");
            }

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
            state.transferables.Clear();
            state.injected = false;
            state.sectionAdded = false;
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
            if (state.things.Count == 0) return;

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

            for (int i = state.things.Count - 1; i >= 0; i--)
            {
                var thing = state.things[i];
                if (thing == null || thing.Destroyed) { RemoveAt(state, i); continue; }

                var tw = i < state.transferables.Count ? state.transferables[i] : null;
                int selected = tw != null ? tw.CountToTransfer : 0;

                if (selected <= 0)
                {
                    // 未选择 → 归还账本
                    var key = ItemKey.Of(thing);
                    if (i < state.sourceLedgers.Count && state.sourceLedgers[i] != null)
                        state.sourceLedgers[i].AddRaw(key, thing.stackCount);
                    thing.Destroy(DestroyMode.Vanish);
                    RemoveAt(state, i);
                }
                else if (spawnOnMap)
                {
                    if (selected < thing.stackCount)
                        thing.stackCount = selected;
                    if (!thing.Spawned)
                        GenPlace.TryPlaceThing(thing, avgPos, map, ThingPlaceMode.Near, null, null, default);
                }
                // Reform: 不 spawn；原版 AddItemsFromTransferablesToRandomInventories 会把选中物品分发给pawn背包
            }
        }

        /// <summary>
        /// 发车 Prefix：把核心 TransferableOneWay 合并到 dialog.transferables。
        /// </summary>
        public static void MergeToTransferables(Dialog_FormCaravan dialog)
        {
            var state = GetState(dialog);
            if (!state.injected || state.transferables.Count == 0) return;
            foreach (var tw in state.transferables)
            {
                if (!dialog.transferables.Contains(tw))
                    dialog.transferables.Add(tw);
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

        private static void RemoveAt(DialogState state, int i)
        {
            state.things.RemoveAt(i);
            if (i < state.sourceLedgers.Count) state.sourceLedgers.RemoveAt(i);
            if (i < state.transferables.Count) state.transferables.RemoveAt(i);
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
            {
                foreach (var c in mapComp.GetAllCores())
                {
                    if (!CoreFinder.IsUsable(c)) continue;
                    if (seenCores.Add(c)) allCores.Add(c);
                }
            }

            var gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp != null)
            {
                foreach (var c in gameComp.GetAllCores())
                {
                    if (c.Map == map) continue;
                    if (!CoreFinder.IsUsable(c)) continue;
                    if (string.IsNullOrEmpty(c.NetworkName)) continue;
                    if (!seenCores.Add(c)) continue;

                    bool hasLocalPeer = allCores.Any(lc =>
                        CoreFinder.IsUsable(lc) && lc.NetworkName == c.NetworkName);
                    bool hasCrossIface = false;
                    if (!hasLocalPeer)
                    {
                        foreach (var cell in c.GetProxyCells())
                            if (cell.InBounds(map)) { hasCrossIface = true; break; }
                    }
                    if (hasLocalPeer || hasCrossIface) allCores.Add(c);
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

                // ThingMaker.MakeThing 对某些 def（如 MinifiedThing）可能产生
                // 渲染阶段无法取 LabelNoCount 的 Thing，提前验证并跳过。
                try { var _ = thing.LabelNoCount; }
                catch (System.Exception ex)
                {
                    Log.Warning($"[DS] Skipping item {key}: {ex.Message}");
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

        private static TransferableOneWayWidget GetItemsTransfer(Dialog_FormCaravan dialog)
        {
            var field = AccessTools.Field(typeof(Dialog_FormCaravan), "itemsTransfer");
            return field.GetValue(dialog) as TransferableOneWayWidget;
        }
    }
}
