using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Core
{
    /// <summary>
    /// 核心账本。
    ///
    /// 两张表：
    ///   stock   —— 每种物品实际库存
    ///   reserved —— 每个工单预订了哪几种物品各多少
    ///
    /// 对外四个动作：
    ///   Query      —— 问可用量（库存 - 预订）
    ///   Ingest     —— pawn 手上的 Thing 进来，Thing 销毁
    ///   Withdraw   —— 按 def+stuff 生成满耐久 Thing 返回，账本扣
    ///   Reserve / ReleaseByJob —— 按工单预订/释放
    ///
    /// 容量单位：按 key 计（一种物品不管多少都只算 1 组）。
    /// 预订表不入档，读档后自动清空。
    /// </summary>
    public class CoreLedger : IExposable
    {
        public event Action<ItemKey, long> StockChanged;
        private bool suppressEvents;

        /// <summary>
        /// 批量内部操作（核心摧毁转移等）期间抑制 StockChanged，
        /// 避免逐 key fire 事件触发 Ghost 的全图聚合风暴（F4）。
        /// 批处理结束后调用方需自己调 GhostLedgerIndex.OnKeyChanged 定向刷账。
        /// </summary>
        public bool SuppressStockEvents
        {
            get => suppressEvents;
            set => suppressEvents = value;
        }

        private void NotifyStockChanged(ItemKey key)
        {
            if (!suppressEvents)
                StockChanged?.Invoke(key, Available(key));
        }

        public IEnumerable<ItemKey> AllKeys() => stock.Keys;

        private Dictionary<ItemKey, long> stock = new Dictionary<ItemKey, long>();
        private readonly Dictionary<Job, List<ReservationEntry>> reservedByJob = new Dictionary<Job, List<ReservationEntry>>();
        private readonly Dictionary<ItemKey, long> reservedTotals = new Dictionary<ItemKey, long>();
        // P6: stock 中「值 > 0」的 key 数量（UsedCapacity 的 O(1) 来源，由 SetStock 维护）
        private int nonzero;

        // 派生值缓存
        private bool groupTotalsDirty = true;
        private readonly long[] groupCounts = new long[6];
        private readonly int[] groupKinds = new int[6];

        public struct ReservationEntry
        {
            public ItemKey key;
            public int count;
        }

        // ========== 可用量 ==========

        /// <summary>
        /// 问可用量（= 库存 - 所有工单在此 key 上的预订总和）。
        /// </summary>
        public long Available(ItemKey key)
        {
            if (!stock.TryGetValue(key, out long s)) s = 0;
            long r = ReservedTotal(key);
            long a = s - r;
            return a > 0 ? a : 0;
        }

        public long StockOf(ItemKey key) => stock.TryGetValue(key, out long s) ? s : 0;

        public long ReservedTotal(ItemKey key) =>
            reservedTotals.TryGetValue(key, out long t) ? t : 0;

        public IReadOnlyDictionary<ItemKey, long> Stock => stock;
        public int KindCount => stock.Count;

        // ========== 容量 ==========

        /// <summary>
        /// 已用容量 = 库存非零的 key 数量。
        /// P6: O(1)（由 SetStock 维护的 running total），不再全表扫 stock——
        /// CanAccept 在自动收纳的 (物品 × 核心) 双层循环里被高频调用。
        /// </summary>
        public int UsedCapacity() => nonzero;

        // ========== 存入 ==========

        /// <summary>
        /// 能否吞下这个 Thing。capacity 为核心当前总容量。
        /// </summary>
        public bool CanAccept(Thing t, int capacity)
        {
            if (!LedgerPolicy.CanIngest(t)) return false;
            var key = ItemKey.Of(t);
            // 同 key 已存在：容量不占新组
            if (stock.TryGetValue(key, out long cur) && cur > 0) return true;
            // 新 key：需要空组
            return UsedCapacity() < capacity;
        }

        /// <summary>
        /// 把 Thing 吞进账本并销毁原 Thing。返回是否成功。
        /// 容量不足或白名单拒会返回 false，Thing 保持原样。
        /// </summary>
        public bool Ingest(Thing t, int capacity)
        {
            if (!CanAccept(t, capacity)) return false;

            var key = ItemKey.Of(t);
            int n = t.stackCount;
            if (n <= 0) return false;

            if (!stock.TryGetValue(key, out long cur)) cur = 0;
            SetStock(key, cur + n);

            if (t.Spawned) t.DeSpawn(DestroyMode.Vanish);
            t.Destroy(DestroyMode.Vanish);

            NotifyStockChanged(key);
            return true;
        }

        /// <summary>
        /// 直接加数（不销毁 Thing，用于核心摧毁转移等内部操作）。
        /// </summary>
        public void AddRaw(ItemKey key, long count)
        {
            AddRawNoNotify(key, count);
            NotifyStockChanged(key);
        }

        /// <summary>
        /// 加数但不 fire StockChanged（批量转移用；调用方负责批量后定向刷账）。
        /// </summary>
        public void AddRawNoNotify(ItemKey key, long count)
        {
            if (count <= 0) return;
            if (!stock.TryGetValue(key, out long cur)) cur = 0;
            SetStock(key, cur + count);
        }

        /// <summary>
        /// 直接扣数（不销毁 Thing，用于核心摧毁转移的源账本扣减，X2）。
        /// </summary>
        public void RemoveRaw(ItemKey key, long count)
        {
            RemoveRawNoNotify(key, count);
            NotifyStockChanged(key);
        }

        /// <summary>
        /// 扣数但不 fire StockChanged（批量转移用）。
        /// </summary>
        public void RemoveRawNoNotify(ItemKey key, long count)
        {
            if (count <= 0) return;
            if (!stock.TryGetValue(key, out long cur) || cur <= 0) return;
            SetStock(key, cur - count);
        }

        /// <summary>
        /// P6: 唯一的 stock 写入点——顺带维护「非零 key 数」running total，
        /// 让 UsedCapacity() 变成 O(1)（旧实现每次 CanAccept 都要全表扫一遍 stock）。
        /// </summary>
        private void SetStock(ItemKey key, long value)
        {
            bool had = stock.TryGetValue(key, out long cur) && cur > 0;
            bool has = value > 0;
            if (has) stock[key] = value;
            else stock.Remove(key);

            if (had != has) nonzero += has ? 1 : -1;
            groupTotalsDirty = true;
            stockVersion++;
        }

        /// <summary>
        /// P8: 库存版本号（每次写入自增）。UI 用它判断是否需要重算派生布局值
        /// （ITab 的列表高度），避免每帧 6 遍全表扫描。
        /// </summary>
        public int StockVersion => stockVersion;

        private int stockVersion;

        // ========== 取出 ==========

        /// <summary>
        /// 从账本扣除并生成 Thing（满耐久、满 HP）。
        /// 传 job 时排除该 job 自己的预订再算可用量——允许自己取自己预订的东西。
        /// 取多少就从该 job 的预订里扣多少（自动抹平），返回生成的 Thing，无货返回 null。
        /// allowOverstack: 允许产出超过 def.stackLimit 的堆叠（仅交易/商队合并场景用，
        /// 用一个 Thing 代表全部库存）；默认 false 保证产出堆叠合法（I10.01.6/X5）。
        /// </summary>
        public Thing Withdraw(ItemKey key, int requestCount, Job forJob = null,
            bool allowOverstack = false)
        {
            if (requestCount <= 0) return null;
            long available = AvailableExceptJob(key, forJob);
            if (available <= 0) return null;

            int take = requestCount > available ? (int)available : requestCount;
            if (take <= 0) return null;
            if (!allowOverstack)
                take = System.Math.Min(take, key.def.stackLimit);
            if (take <= 0) return null;

            long cur = stock[key];
            SetStock(key, cur - take);

            // 从预订里扣掉已取走的量
            if (forJob != null && reservedByJob.TryGetValue(forJob, out var list))
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].key.Equals(key))
                    {
                        var e = list[i];
                        int ded = System.Math.Min(take, e.count);
                        e.count -= ded;
                        if (e.count <= 0) list.RemoveAt(i);
                        else list[i] = e;
                        // 同步 reservedTotals
                        if (reservedTotals.TryGetValue(key, out long rt))
                        {
                            long afterRt = rt - ded;
                            if (afterRt <= 0) reservedTotals.Remove(key);
                            else reservedTotals[key] = afterRt;
                        }
                        break;
                    }
                }
            }

            groupTotalsDirty = true;

            var thing = ThingMaker.MakeThing(key.def, key.stuff);
            thing.stackCount = take;
            NotifyStockChanged(key);
            return thing;
        }

        /// <summary>
        /// 可用量 = 库存 - 除自己的预订。
        /// 传 null 等于 Available()（直接查 running total）。
        /// P5: 旧实现每次 Withdraw 都全表扫 reservedByJob（交易/商队按 key 合并提款时
        /// 放大成 O(key × 工单)）；现在用 reservedTotals，再只扣掉该 job 自己的那一份。
        /// </summary>
        private long AvailableExceptJob(ItemKey key, Job excludeJob)
        {
            if (!stock.TryGetValue(key, out long s)) s = 0;
            long r = ReservedTotal(key);

            // reservedTotals 含该 job 自己的量；把自己那一份减掉（同 ReservedTotal 口径）
            if (excludeJob != null && reservedByJob.TryGetValue(excludeJob, out var own))
            {
                for (int i = 0; i < own.Count; i++)
                {
                    if (own[i].key.Equals(key)) r -= own[i].count;
                }
            }

            long a = s - (r > 0 ? r : 0);
            return a > 0 ? a : 0;
        }

        // ========== 预订 ==========

        /// <summary>
        /// 为某工单预订一定数量。允许同一工单多次预订（累加），但总量不能超过可用量。
        /// 返回实际预订到的数量（可能小于 requested）。
        /// </summary>
        public int Reserve(Job job, ItemKey key, int requested)
        {
            if (job == null || requested <= 0) return 0;
            long available = Available(key);
            if (available <= 0) return 0;

            int take = requested > available ? (int)available : requested;

            if (!reservedByJob.TryGetValue(job, out var list))
            {
                list = new List<ReservationEntry>();
                reservedByJob[job] = list;
            }

            // 同 key 累加
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].key.Equals(key))
                {
                    var e = list[i];
                    e.count += take;
                    list[i] = e;
                    reservedTotals[key] = reservedTotals.TryGetValue(key, out long rt) ? rt + take : take;
                    // F3: 累加分支也必须发通知——Available 变了，GhostThing.stackCount
                    // 是给其他 mod 读的公共量，不发通知会长期高于真实可用量。
                    NotifyStockChanged(key);
                    return take;
                }
            }
            list.Add(new ReservationEntry { key = key, count = take });
            reservedTotals[key] = reservedTotals.TryGetValue(key, out long rt2) ? rt2 + take : take;
            NotifyStockChanged(key);
            return take;
        }

        /// <summary>
        /// 释放某工单的全部预订。JobDriver.Cleanup 统一入口调用。
        /// </summary>
        public void ReleaseByJob(Job job)
        {
            if (job == null) return;
            if (reservedByJob.TryGetValue(job, out var entries))
            {
                reservedByJob.Remove(job);
                foreach (var e in entries)
                {
                    if (reservedTotals.TryGetValue(e.key, out long t))
                    {
                        long after = t - e.count;
                        if (after <= 0) reservedTotals.Remove(e.key);
                        else reservedTotals[e.key] = after;
                    }
                    NotifyStockChanged(e.key);
                }
            }
        }

        // ========== 存读档 ==========

        public void ExposeData()
        {
            suppressEvents = true;
            try
            {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                var entries = new List<string>(stock.Count);
                var counts = new List<long>(stock.Count);
                foreach (var kv in stock)
                {
                    if (kv.Value <= 0) continue;
                    entries.Add(kv.Key.ToSaveString());
                    counts.Add(kv.Value);
                }
                Scribe_Collections.Look(ref entries, "stockKeys", LookMode.Value);
                Scribe_Collections.Look(ref counts, "stockCounts", LookMode.Value);
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                List<string> entries = null;
                List<long> counts = null;
                Scribe_Collections.Look(ref entries, "stockKeys", LookMode.Value);
                Scribe_Collections.Look(ref counts, "stockCounts", LookMode.Value);

                stock = new Dictionary<ItemKey, long>();
                if (entries != null && counts != null)
                {
                    int n = System.Math.Min(entries.Count, counts.Count);
                    for (int i = 0; i < n; i++)
                    {
                        if (ItemKey.TryParse(entries[i], out var k) && counts[i] > 0)
                        {
                            stock[k] = counts[i];
                        }
                    }
                }
                reservedByJob.Clear();
                reservedTotals.Clear();
                groupTotalsDirty = true;
                // P6: 重建 nonzero（否则读档后 UsedCapacity=0，容量判定会误判）
                nonzero = 0;
                foreach (var v in stock.Values)
                    if (v > 0) nonzero++;
            }
            }
            finally { suppressEvents = false; }
        }

        // ========== 分组统计（派生值，脏标记） ==========

        public void EnsureGroupTotals()
        {
            if (!groupTotalsDirty) return;
            for (int i = 0; i < 6; i++) { groupCounts[i] = 0; groupKinds[i] = 0; }
            foreach (var kv in stock)
            {
                if (kv.Value <= 0) continue;
                int idx = (int)ItemGrouping.GroupOf(kv.Key.def);
                groupCounts[idx] += kv.Value;
                groupKinds[idx]++;
            }
            groupTotalsDirty = false;
        }

        public long GroupCount(ItemGroup g) { EnsureGroupTotals(); return groupCounts[(int)g]; }
        public int GroupKinds(ItemGroup g) { EnsureGroupTotals(); return groupKinds[(int)g]; }
    }
}
