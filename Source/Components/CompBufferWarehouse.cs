using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Core;
using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 7c: 缓冲仓库保留阈值管理 + 双向物流。
    /// 每 15 tick 扫描 SlotGroup 物理库存 →
    ///   - 低于阈值：从核心 Withdraw → Spawn 补货到阈值
    ///   - 超出阈值：Ingest 入核心（需通过 LedgerPolicy，品质物品留物理层）
    /// 阈值 = 0：不补货，仅接收溢出。
    /// </summary>
    public class CompBufferWarehouse : ThingComp
    {
        private Building_BufferWarehouse warehouse;
        private Dictionary<ItemKey, int> thresholds = new Dictionary<ItemKey, int>();
        private HashSet<string> explicitZero = new HashSet<string>();

        private static Thing[] restockBuffer = new Thing[50];

        private const int ScanInterval = 15;
        private const int MaxRestockPerScan = 20;
        private const int DefaultThreshold = 100;

        public int GetThreshold(ItemKey key) =>
            thresholds.TryGetValue(key, out int v) ? v : 0;

        public void SetThreshold(ItemKey key, int count)
        {
            string id = key.ToSaveString();
            explicitZero.Remove(id);
            if (count <= 0)
            {
                thresholds.Remove(key);
                explicitZero.Add(id);
            }
            else
            {
                thresholds[key] = count;
            }
        }

        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            warehouse = parent as Building_BufferWarehouse;
        }

        public override void CompTick()
        {
            base.CompTick();
            if (warehouse == null || !warehouse.Spawned) return;

            int tick = Find.TickManager.TicksGame;
            if ((tick + parent.thingIDNumber) % ScanInterval != 0) return;

            var core = warehouse.BoundCore;
            if (core == null || !core.Spawned || !core.Powered) return;

            var slotGroup = warehouse.GetSlotGroup();
            if (slotGroup == null) return;

            // 1. 统计 SlotGroup 现有物品 (ItemKey → totalStack)
            var onHand = new Dictionary<ItemKey, long>();
            foreach (var t in slotGroup.HeldThings)
            {
                if (t.Destroyed) continue;
                var key = ItemKey.Of(t);
                onHand.TryGetValue(key, out long cur);
                onHand[key] = cur + t.stackCount;
            }

            // 1b. 新物品自动设置默认阈值
            AutoSetDefaults(onHand);

            // 2. 补货：低于阈值 → 从核心取
            int restocked = 0;
            foreach (var kv in thresholds)
            {
                if (restocked >= MaxRestockPerScan) break;
                if (kv.Value <= 0) continue;

                onHand.TryGetValue(kv.Key, out long current);
                long needed = kv.Value - current;
                if (needed <= 0) continue;

                int take = needed > int.MaxValue ? int.MaxValue : (int)needed;
                long avail = core.Ledger.Available(kv.Key);
                if (avail <= 0) continue;
                if (take > avail) take = (int)avail;

                var thing = core.Ledger.Withdraw(kv.Key, take);
                if (thing == null) continue;

                // 直接 Spawn 到仓库格子上（PassThroughOnly 可堆叠）
                GenSpawn.Spawn(thing, parent.Position, parent.Map);
                // 如果 spawn 后不在 SlotGroup（格子满了弹到外面），收回
                if (thing.Spawned && (thing.Position != parent.Position || !thing.IsInAnyStorage()))
                {
                    thing.DeSpawn();
                    core.Ledger.AddRaw(kv.Key, take);
                    thing.Destroy(DestroyMode.Vanish);
                }
                else if (!thing.Spawned)
                {
                    core.Ledger.AddRaw(kv.Key, take);
                    thing.Destroy(DestroyMode.Vanish);
                }
                else
                {
                    CompAutoIngest.MarkWithdrawn(thing);
                    restocked++;
                }
            }

            // 3. 溢出：超出阈值 → 吸入核心
            int capacity = core.GetCapacity();
            int overflowed = 0;

            // 先用缓冲收集候选项，避免迭代中 Ingest 修改 HeldThings
            int bufSize = 50;
            if (restockBuffer.Length < bufSize)
                restockBuffer = new Thing[bufSize];
            int bufCount = 0;

            foreach (var t in slotGroup.HeldThings)
            {
                if (bufCount >= bufSize) break;
                if (t.Destroyed) continue;
                if (!LedgerPolicy.CanIngest(t)) continue;
                if (!core.AllowsItem(t)) continue;

                var key = ItemKey.Of(t);
                int threshold = GetThreshold(key);
                onHand.TryGetValue(key, out long total);

                if (threshold < 0) continue; // 负值：不溢出
                long excess = total - threshold; // 阈值0 = 全溢出
                if (excess <= 0) continue;
                restockBuffer[bufCount++] = t;
            }

            for (int i = 0; i < bufCount && restocked + overflowed < MaxRestockPerScan; i++)
            {
                var t = restockBuffer[i];
                if (t.Destroyed) continue;

                var key = ItemKey.Of(t);
                int threshold = GetThreshold(key);
                onHand.TryGetValue(key, out long total);
                int take = threshold > 0
                    ? (int)System.Math.Min(total - threshold, (long)int.MaxValue)
                    : t.stackCount;
                if (take <= 0 || take > t.stackCount) take = t.stackCount;
                if (take <= 0) continue;

                if (!core.Ledger.CanAccept(t, capacity)) continue;

                Thing toIngest = take >= t.stackCount ? t : t.SplitOff(take);
                if (toIngest == null) continue;

                if (core.Ledger.Ingest(toIngest, capacity))
                {
                    overflowed++;
                    onHand[key] -= take;
                    if (onHand[key] <= 0) onHand.Remove(key);
                }
            }
            for (int i = 0; i < bufCount; i++) restockBuffer[i] = null;
        }

        private void AutoSetDefaults(Dictionary<ItemKey, long> onHand)
        {
            foreach (var kv in onHand)
            {
                if (thresholds.ContainsKey(kv.Key)) continue;
                string id = kv.Key.ToSaveString();
                if (explicitZero.Contains(id)) continue;
                if (kv.Value <= 0) continue;
                thresholds[kv.Key] = DefaultThreshold;
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref explicitZero, "bwExplicitZero", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                var keys = new List<string>();
                var vals = new List<int>();
                foreach (var kv in thresholds)
                {
                    if (kv.Value <= 0) continue;
                    keys.Add(kv.Key.ToSaveString());
                    vals.Add(kv.Value);
                }
                Scribe_Collections.Look(ref keys, "bwThreshKeys", LookMode.Value);
                Scribe_Collections.Look(ref vals, "bwThreshVals", LookMode.Value);
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (explicitZero == null) explicitZero = new HashSet<string>();
                thresholds = new Dictionary<ItemKey, int>();
                List<string> keys = null;
                List<int> vals = null;
                Scribe_Collections.Look(ref keys, "bwThreshKeys", LookMode.Value);
                Scribe_Collections.Look(ref vals, "bwThreshVals", LookMode.Value);
                if (keys != null && vals != null)
                {
                    int n = System.Math.Min(keys.Count, vals.Count);
                    for (int i = 0; i < n; i++)
                    {
                        if (ItemKey.TryParse(keys[i], out var k) && vals[i] > 0)
                            thresholds[k] = vals[i];
                    }
                }
            }
        }
    }
}
