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
        private Dictionary<ItemKey, long> stock = new Dictionary<ItemKey, long>();
        private readonly Dictionary<Job, List<ReservationEntry>> reservedByJob = new Dictionary<Job, List<ReservationEntry>>();

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

        public long ReservedTotal(ItemKey key)
        {
            long total = 0;
            foreach (var list in reservedByJob.Values)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].key.Equals(key)) total += list[i].count;
                }
            }
            return total;
        }

        public IReadOnlyDictionary<ItemKey, long> Stock => stock;
        public int KindCount => stock.Count;

        // ========== 容量 ==========

        /// <summary>
        /// 已用容量 = 库存非零的 key 数量。
        /// </summary>
        public int UsedCapacity()
        {
            int c = 0;
            foreach (var v in stock.Values)
            {
                if (v > 0) c++;
            }
            return c;
        }

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
            stock[key] = cur + n;

            if (t.Spawned) t.DeSpawn(DestroyMode.Vanish);
            t.Destroy(DestroyMode.Vanish);

            groupTotalsDirty = true;
            return true;
        }

        /// <summary>
        /// 直接加数（不销毁 Thing，用于核心摧毁转移等内部操作）。
        /// </summary>
        public void AddRaw(ItemKey key, long count)
        {
            if (count <= 0) return;
            if (!stock.TryGetValue(key, out long cur)) cur = 0;
            stock[key] = cur + count;
            groupTotalsDirty = true;
        }

        // ========== 取出 ==========

        /// <summary>
        /// 从账本扣除并生成 Thing（满耐久、满 HP）。
        /// 传 job 时排除该 job 自己的预订再算可用量——允许自己取自己预订的东西。
        /// 取多少就从该 job 的预订里扣多少（自动抹平），返回生成的 Thing，无货返回 null。
        /// </summary>
        public Thing Withdraw(ItemKey key, int requestCount, Job forJob = null)
        {
            if (requestCount <= 0) return null;
            long available = AvailableExceptJob(key, forJob);
            if (available <= 0) return null;

            int take = requestCount > available ? (int)available : requestCount;
            if (take <= 0) return null;

            long cur = stock[key];
            long after = cur - take;
            if (after <= 0) stock.Remove(key);
            else stock[key] = after;

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
                        break;
                    }
                }
            }

            groupTotalsDirty = true;

            var thing = ThingMaker.MakeThing(key.def, key.stuff);
            thing.stackCount = take;
            return thing;
        }

        /// <summary>
        /// 可用量 = 库存 - 除自己的预订。传 null 等于 Available()。
        /// </summary>
        private long AvailableExceptJob(ItemKey key, Job excludeJob)
        {
            if (!stock.TryGetValue(key, out long s)) s = 0;
            long r = 0;
            foreach (var kv in reservedByJob)
            {
                if (kv.Key == excludeJob) continue;
                foreach (var e in kv.Value)
                {
                    if (e.key.Equals(key)) r += e.count;
                }
            }
            long a = s - r;
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
                    return take;
                }
            }
            list.Add(new ReservationEntry { key = key, count = take });
            return take;
        }

        /// <summary>
        /// 释放某工单的全部预订。JobDriver.Cleanup 统一入口调用。
        /// </summary>
        public void ReleaseByJob(Job job)
        {
            if (job == null) return;
            reservedByJob.Remove(job);
        }

        // ========== 存读档 ==========

        public void ExposeData()
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
                groupTotalsDirty = true;
            }
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
