using System.Collections.Generic;
using DigitalStorage.Core;
using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 8.1: DSU 式 Min/Max + 空槽补货（抄 OutputPortDsuBuilding，用户 8.1 拍板）。
    /// 单元语义：一个缓冲仓库 = 一个缓冲位，锁定一种物品，Min/Max 管住格上存量：
    ///   - 格上无物品（空槽）且 Max > 0 → 从核心补货（数量 = min(核心可用, Max)；
    ///     核心可用 < Min 则不补，防乒乓）
    ///   - 格上堆叠 < Min → 整堆吸回核心
    ///   - 格上堆叠 > Max → split 超出部分吸回核心
    ///   - 非锁定物品 / 多余堆 → 吸回核心（只留第一个锁定堆）
    /// 乒乓防护（DSU 同款）：补货只在「空槽」发生；吸回后核心可用 < Min 不再补。
    /// </summary>
    public class CompBufferWarehouse : ThingComp
    {
        private Building_BufferWarehouse warehouse;
        private int min;
        private int max = DefaultMax;

        private const int ScanInterval = 15;
        private const int DefaultMax = 100;

        public int Min
        {
            get => min;
            set => min = value > 0 ? value : 0;
        }

        public int Max
        {
            get => max;
            set => max = value > 0 ? value : 0;
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

            var lockedDef = warehouse.LockedItemDef;

            // 1. 自动锁定：min=max=0 视为「单元已清空」，不自动锁定。
            // 不再查 GetStoreSettings().AllowedToAccept——8.1 起筛选跟随锁定
            // （未锁定 = 全部不允许），锁定后 SetLockedItemDef 自动设筛选。
            if (lockedDef == null && (Min > 0 || Max > 0))
            {
                foreach (var kv in core.Ledger.Stock)
                {
                    if (core.Ledger.Available(kv.Key) <= 0) continue;
                    warehouse.SetLockedItemDef(kv.Key.def);
                    lockedDef = kv.Key.def;
                    break;
                }
                if (lockedDef == null) return;
            }
            if (lockedDef == null) return;

            int capacity = core.GetCapacity();

            // 2. 违规回收：格上只能有一个锁定堆，且数量在 [Min, Max]。
            // 先收集再处理（Ingest 会修改 HeldThings 列表）。
            var held = new List<Thing>();
            foreach (var t in slotGroup.HeldThings)
                if (!t.Destroyed) held.Add(t);

            bool hasKeptStack = false; // 保留的第一个锁定堆
            for (int i = 0; i < held.Count; i++)
            {
                var t = held[i];
                if (t.def != lockedDef)
                {
                    TrySuckBack(t, core, capacity); // 非锁定物品回核心
                    continue;
                }
                if (!hasKeptStack)
                {
                    hasKeptStack = true;
                    if (Min > 0 && t.stackCount < Min)
                    {
                        TrySuckBack(t, core, capacity); // 低于 Min → 整堆吸回
                        hasKeptStack = false;
                        continue;
                    }
                    if (Max > 0 && t.stackCount > Max)
                    {
                        Thing split = t.SplitOff(t.stackCount - Max); // 超出 Max 部分吸回
                        if (split != null) TrySuckBack(split, core, capacity);
                    }
                    continue;
                }
                TrySuckBack(t, core, capacity); // 多余堆回核心
            }

            // 3. 空槽补货：格子上完全没有物品才补（DSU 语义）。
            if (!hasKeptStack && Max > 0)
            {
                bool anyThingOnSlot = false;
                foreach (var t in slotGroup.HeldThings)
                {
                    if (!t.Destroyed) { anyThingOnSlot = true; break; }
                }
                if (!anyThingOnSlot)
                {
                    // 找 lockedDef 对应库存最多的 ItemKey
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
                        int take = bestAvail > int.MaxValue ? int.MaxValue : (int)bestAvail;
                        if (take > Max) take = Max;
                        // 核心可用 < Min → 不补（防乒乓：吸回的残堆不会反复吐出）
                        if (Min > 0 && take < Min) take = 0;

                        if (take > 0)
                        {
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
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 违规物品吸回核心。核心满/不可吞 → 留在格上，下轮重试（绝不销毁物品）。
        /// </summary>
        private void TrySuckBack(Thing t, Building_StorageCore core, int capacity)
        {
            if (t == null || t.Destroyed) return;
            if (!LedgerPolicy.CanIngest(t)) return;
            if (!core.Ledger.CanAccept(t, capacity)) return;
            core.Ledger.Ingest(t, capacity);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();

            // 8.1: 旧单阈值格式迁移 → bwThreshold 的值成为新 Max，Min = 0。
            // 更老的 bwThreshKeys/bwThreshVals 多阈值格式也一并迁移。
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                List<string> oldKeys = null;
                List<int> oldVals = null;
                Scribe_Collections.Look(ref oldKeys, "bwThreshKeys", LookMode.Value);
                Scribe_Collections.Look(ref oldVals, "bwThreshVals", LookMode.Value);

                var oldZero = new HashSet<string>();
                Scribe_Collections.Look(ref oldZero, "bwExplicitZero", LookMode.Value);

                int oldThreshold = 0;
                Scribe_Values.Look(ref oldThreshold, "bwThreshold", 0);

                if (oldThreshold > 0)
                {
                    max = oldThreshold; // 单阈值 → Max
                }
                else if (oldKeys != null && oldVals != null)
                {
                    int n = System.Math.Min(oldKeys.Count, oldVals.Count);
                    for (int i = 0; i < n; i++)
                    {
                        if (oldVals[i] > 0 && ItemKey.TryParse(oldKeys[i], out _))
                        {
                            max = oldVals[i];
                            break;
                        }
                    }
                }
                min = 0;
            }

            Scribe_Values.Look(ref min, "bwMin", 0);
            Scribe_Values.Look(ref max, "bwMax", DefaultMax);
        }
    }
}
