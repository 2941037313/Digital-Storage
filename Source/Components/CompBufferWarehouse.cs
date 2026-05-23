using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Core;
using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 5b: 单阈值 + 补货 FreezeItemTick 补全。
    /// 每 15 tick 扫描 SlotGroup →
    ///   - 低于阈值：从核心 Withdraw → Spawn 补货 + FreezeItemTick
    ///   - 超出阈值：Ingest 入核心
    /// 阈值 = 0：不补货，仅接收溢出。
    /// </summary>
    public class CompBufferWarehouse : ThingComp
    {
        private Building_BufferWarehouse warehouse;
        private int threshold = DefaultThreshold;

        private static Thing[] restockBuffer = new Thing[50];

        private const int ScanInterval = 15;
        private const int MaxRestockPerScan = 20;
        private const int DefaultThreshold = 100;

        public int Threshold
        {
            get => threshold;
            set => threshold = value > 0 ? value : 0;
        }

        // 旧 API 兼容（5c ITab 重写后删除）
        public int GetThreshold(ItemKey key) => threshold;
        public void SetThreshold(ItemKey key, int count) => Threshold = count;

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

            var lockedDef = warehouse.LockedItemDef;

            // 1. 自动锁定：lockedDef 为 null → 从核心挑第一个可用物品
            if (lockedDef == null)
            {
                foreach (var kv in core.Ledger.Stock)
                {
                    if (core.Ledger.Available(kv.Key) <= 0) continue;
                    if (!warehouse.GetStoreSettings().AllowedToAccept(kv.Key.def)) continue;
                    warehouse.SetLockedItemDef(kv.Key.def);
                    lockedDef = kv.Key.def;
                    break;
                }
                if (lockedDef == null) return;
            }

            // 2. 统计 SlotGroup 上 lockedDef 的物理存量
            long onHand = 0;
            foreach (var t in slotGroup.HeldThings)
            {
                if (t.Destroyed) continue;
                if (t.def == lockedDef) onHand += t.stackCount;
            }

            // 3. 补货：低于阈值 → 从核心取
            if (threshold > 0 && onHand < threshold)
            {
                long needed = threshold - onHand;

                // 找到 lockedDef 对应库存最多的 ItemKey
                ItemKey bestKey = default;
                long bestAvail = 0;
                foreach (var kv in core.Ledger.Stock)
                {
                    if (kv.Key.def != lockedDef) continue;
                    long avail = core.Ledger.Available(kv.Key);
                    if (avail > bestAvail) { bestKey = kv.Key; bestAvail = avail; }
                }

                if (bestAvail > 0)
                {
                    int take = needed > int.MaxValue ? int.MaxValue : (int)needed;
                    if (take > bestAvail) take = (int)bestAvail;

                    var thing = core.Ledger.Withdraw(bestKey, take);
                    if (thing != null)
                    {
                        GenSpawn.Spawn(thing, parent.Position, parent.Map);

                        if (thing.Spawned && (thing.Position != parent.Position || !thing.IsInAnyStorage()))
                        {
                            thing.DeSpawn();
                            core.Ledger.AddRaw(bestKey, take);
                            thing.Destroy(DestroyMode.Vanish);
                        }
                        else if (!thing.Spawned)
                        {
                            core.Ledger.AddRaw(bestKey, take);
                            thing.Destroy(DestroyMode.Vanish);
                        }
                        else
                        {
                            warehouse.FreezeItemTick(thing);
                            CompAutoIngest.MarkWithdrawn(thing);
                            onHand += take;
                        }
                    }
                }
            }

            // 4. 溢出：超出阈值 → 吸入核心
            if (onHand > threshold && threshold >= 0)
            {
                int excess = onHand > int.MaxValue ? int.MaxValue : (int)(onHand - threshold);
                if (excess > 0)
                {
                    // 从 SlotGroup 上找 lockedDef 物品吸入
                    int bufSize = 50;
                    if (restockBuffer.Length < bufSize)
                        restockBuffer = new Thing[bufSize];
                    int bufCount = 0;

                    foreach (var t in slotGroup.HeldThings)
                    {
                        if (bufCount >= bufSize) break;
                        if (t.Destroyed) continue;
                        if (t.def != lockedDef) continue;
                        if (!LedgerPolicy.CanIngest(t)) continue;
                        if (!core.AllowsItem(t)) continue;
                        restockBuffer[bufCount++] = t;
                    }

                    int capacity = core.GetCapacity();
                    int taken = 0;
                    for (int i = 0; i < bufCount && taken < excess; i++)
                    {
                        var t = restockBuffer[i];
                        if (t.Destroyed) continue;

                        int take = System.Math.Min(t.stackCount, excess - taken);
                        if (take <= 0) continue;

                        if (!core.Ledger.CanAccept(t, capacity)) continue;

                        Thing toIngest = take >= t.stackCount ? t : t.SplitOff(take);
                        if (toIngest == null) continue;

                        if (core.Ledger.Ingest(toIngest, capacity))
                            taken += take;
                    }
                    for (int i = 0; i < bufCount; i++) restockBuffer[i] = null;
                }
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();

            // Load old multi-threshold format → migrate to single threshold
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                threshold = DefaultThreshold;

                List<string> oldKeys = null;
                List<int> oldVals = null;
                Scribe_Collections.Look(ref oldKeys, "bwThreshKeys", LookMode.Value);
                Scribe_Collections.Look(ref oldVals, "bwThreshVals", LookMode.Value);

                var oldZero = new HashSet<string>();
                Scribe_Collections.Look(ref oldZero, "bwExplicitZero", LookMode.Value);

                // Take first valid threshold from old format
                if (oldKeys != null && oldVals != null)
                {
                    int n = System.Math.Min(oldKeys.Count, oldVals.Count);
                    for (int i = 0; i < n; i++)
                    {
                        if (oldVals[i] > 0 && ItemKey.TryParse(oldKeys[i], out _))
                        {
                            threshold = oldVals[i];
                            break;
                        }
                    }
                }
            }

            // Single threshold serialization (both save and load)
            Scribe_Values.Look(ref threshold, "bwThreshold", DefaultThreshold);
        }
    }
}
