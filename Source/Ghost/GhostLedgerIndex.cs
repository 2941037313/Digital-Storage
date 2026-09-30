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
        private static int nextGhostId = -100000;

        // 反射访问 listerThings 内部字典（绕过 onThingAdded 回调，防止进入 save 系统）
        private static readonly FieldInfo listsByDefField =
            typeof(ListerThings).GetField("listsByDef", BindingFlags.NonPublic | BindingFlags.Instance);

        public GhostLedgerIndex(Map map) : base(map) { }

        // ═══════════════════════════════════════════
        // Public API
        // ═══════════════════════════════════════════

        public static bool IsGhostThing(Thing t)
        {
            return t is GhostThing;
        }

        public static GhostThing FindGhostFor(Map map, ItemKey key)
        {
            var comp = map?.GetComponent<GhostLedgerIndex>();
            if (comp == null) return null;
            comp.ghosts.TryGetValue(key, out var ghost);
            return ghost;
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

        /// <summary>
        /// F4: 单 key 定向刷账（核心摧毁批量转移后调用），替代逐 key StockChanged 风暴。
        /// </summary>
        public void OnKeyChanged(ItemKey key) => ProcessKey(key);

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

                ghost.thingIDNumber = nextGhostId--;
                ghost.stackCount = ClampToInt(aggregate);
                ghost.Key = key;

                // 直接操作 listerThings 内部字典，绕过 onThingAdded 回调
                AddToListerDirect(ghost);

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
            RemoveFromListerDirect(ghost);
            ghosts.Remove(key);
            ghostThings.Remove(ghost);
        }

        // ═══════════════════════════════════════════
        // 直接操作 listerThings 内部字典（不触发回调）
        // ═══════════════════════════════════════════

        private void AddToListerDirect(Thing t)
        {
            var lister = map.listerThings;
            var listsByDef = (Dictionary<ThingDef, List<Thing>>)listsByDefField.GetValue(lister);

            // 只注入 listsByDef（ThingsOfDef 查询）
            // 不注入 listsByGroup（避免进入 AllThings → Map.ExposeData 序列化）
            if (!listsByDef.TryGetValue(t.def, out var defList))
            {
                defList = new List<Thing>();
                listsByDef[t.def] = defList;
            }
            defList.Add(t);
        }

        private void RemoveFromListerDirect(Thing t)
        {
            var lister = map.listerThings;
            var listsByDef = (Dictionary<ThingDef, List<Thing>>)listsByDefField.GetValue(lister);

            if (listsByDef.TryGetValue(t.def, out var defList))
                defList.Remove(t);
        }

        // ═══════════════════════════════════════════
        // Rebuild
        // ═══════════════════════════════════════════

        public void RebuildAll()
        {
            // 清理所有现有 ghost（用直接操作，不触发回调）
            foreach (var ghost in ghosts.Values.ToList())
                RemoveFromListerDirect(ghost);
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
        // Materializer：从账本提取真货
        // ═══════════════════════════════════════════

        public Thing MaterializeFromLedger(ItemKey key, int count)
        {
            if (count <= 0) return null;

            var mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return null;

            int remaining = count;
            Thing result = null;

            foreach (var core in mapComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered) continue;
                long avail = core.Ledger.Available(key);
                if (avail <= 0) continue;

                int take = remaining > avail ? (int)avail : remaining;
                Thing withdrawn = core.Ledger.Withdraw(key, take);
                if (withdrawn == null) continue;

                if (result == null)
                {
                    result = withdrawn;
                }
                else
                {
                    result.stackCount += withdrawn.stackCount;
                    withdrawn.Destroy(DestroyMode.Vanish);
                }

                remaining -= take;
                if (remaining <= 0) break;
            }

            if (result == null && DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                Log.Message($"[DS-Ghost] MaterializeFromLedger: {key} x{count} yielded nothing");
            return result;
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
