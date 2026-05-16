using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DigitalStorage.Components;
using DigitalStorage.Core;
using Verse;

namespace DigitalStorage.Ghost
{
    public class GhostLedgerIndex : MapComponent
    {
        private readonly Dictionary<ItemKey, GhostThing> ghosts = new Dictionary<ItemKey, GhostThing>();
        private readonly HashSet<Thing> ghostThings = new HashSet<Thing>();

        private static readonly FieldInfo mapIndexField =
            typeof(Thing).GetField("mapIndexOrState", BindingFlags.NonPublic | BindingFlags.Instance);

        public GhostLedgerIndex(Map map) : base(map) { }

        // ═══════════════════════════════════════════
        // Public API
        // ═══════════════════════════════════════════

        public static bool IsGhostThing(Thing t)
        {
            return t is GhostThing;
        }

        public long AggregateAvailablePublic(ItemKey key) => AggregateAvailable(key);

        // ═══════════════════════════════════════════
        // 生命周期
        // ═══════════════════════════════════════════

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            RebuildAll();
        }

        // ═══════════════════════════════════════════
        // 事件订阅
        // ═══════════════════════════════════════════

        public void RegisterCore(Building_StorageCore core)
        {
            if (core?.Ledger == null) return;
            core.Ledger.StockChanged += OnStockChanged;
        }

        public void UnregisterCore(Building_StorageCore core)
        {
            if (core?.Ledger == null) return;
            core.Ledger.StockChanged -= OnStockChanged;
        }

        public void OnCoreStateChanged(Building_StorageCore core)
        {
            if (core?.Ledger == null) return;
            foreach (var key in core.Ledger.AllKeys())
                ProcessKey(key);
        }

        // ═══════════════════════════════════════════
        // 核心逻辑
        // ═══════════════════════════════════════════

        private void OnStockChanged(ItemKey key, long available)
        {
            ProcessKey(key);
        }

        private void ProcessKey(ItemKey key)
        {
            if (key.def == null || key.def.category != ThingCategory.Item) return;

            long aggregate = AggregateAvailable(key);

            if (aggregate > 0)
            {
                if (ghosts.TryGetValue(key, out var existing))
                {
                    existing.stackCount = ClampToInt(aggregate);
                }
                else
                {
                    InjectGhost(key, aggregate);
                }
            }
            else
            {
                if (ghosts.TryGetValue(key, out var existing))
                    RemoveGhost(key, existing);
            }
        }

        private long AggregateAvailable(ItemKey key)
        {
            long total = 0;
            var mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return 0;

            foreach (var core in mapComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered) continue;
                total += core.Ledger.Available(key);
            }
            return total;
        }

        // ═══════════════════════════════════════════
        // 注入 / 移除（不 Spawn，直接操作 listerThings）
        // ═══════════════════════════════════════════

        private void InjectGhost(ItemKey key, long aggregate)
        {
            try
            {
                var ghost = (GhostThing)Activator.CreateInstance(typeof(GhostThing));
                ghost.def = key.def;
                if (key.stuff != null)
                    ghost.SetStuffDirect(key.stuff);

                // 设 mapIndexOrState 让 thing.Spawned=true, thing.Map=map
                if (mapIndexField != null)
                    mapIndexField.SetValue(ghost, (sbyte)map.Index);

                ghost.stackCount = ClampToInt(aggregate);
                ghost.Key = key;

                // 直接注入 listerThings 索引，不经过 GenSpawn
                map.listerThings.Add(ghost);

                ghosts[key] = ghost;
                ghostThings.Add(ghost);
            }
            catch (Exception ex)
            {
                Log.Warning($"[DS] Ghost inject failed for {key}: {ex.Message}");
            }
        }

        private void RemoveGhost(ItemKey key, GhostThing ghost)
        {
            map.listerThings.Remove(ghost);
            ghosts.Remove(key);
            ghostThings.Remove(ghost);
        }

        // ═══════════════════════════════════════════
        // Rebuild
        // ═══════════════════════════════════════════

        public void RebuildAll()
        {
            // 清理所有现有 ghost
            foreach (var ghost in ghosts.Values.ToList())
                map.listerThings.Remove(ghost);
            ghosts.Clear();
            ghostThings.Clear();

            // 重建
            var mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return;

            var allKeys = new HashSet<ItemKey>();
            foreach (var core in mapComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered) continue;
                foreach (var key in core.Ledger.AllKeys())
                    allKeys.Add(key);
            }

            foreach (var key in allKeys)
                ProcessKey(key);
        }

        // ═══════════════════════════════════════════
        // Util
        // ═══════════════════════════════════════════

        private static int ClampToInt(long value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }
    }
}
