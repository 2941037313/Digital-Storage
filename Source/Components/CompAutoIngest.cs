using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Core;
using DigitalStorage.Services;
using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// I5a+I5b: 自动收纳。Tick 扫 listerHaulables → 过滤 → 吸入自身账本。
    /// I5b: 研究解锁 + Gizmo 开关 + 电力检查。
    /// I5c: 多级速率。
    /// </summary>
    public class CompAutoIngest : ThingComp
    {
        private Building_StorageCore core;
        private bool enabled = true;
        private int ingestRateCache = -1;
        private bool researchedCache;
        private int researchCheckTick = -1;

        /// <summary>
        /// L1: 刚取出标记表——按 thingID 记过期 tick（固定 300 tick 完整窗口），
        /// 不再每 120 tick 全清（旧实现窗口长度不确定，延迟拾取会被吞回）。
        /// </summary>
        private static readonly Dictionary<int, int> withdrawnUntil = new Dictionary<int, int>();
        private static Thing[] candidateBuffer = new Thing[30];
        private static int lastSweepTick = -1;
        private static int lastZoneScanTick = -1; // zone 内物品收集的 60tick 节流

        public static void MarkWithdrawn(Thing t)
        {
            if (t != null)
                withdrawnUntil[t.thingIDNumber] = Find.TickManager.TicksGame + 300;
        }

        public static bool IsRecentlyWithdrawn(Thing t)
        {
            return t != null && withdrawnUntil.TryGetValue(t.thingIDNumber, out int until)
                && Find.TickManager.TicksGame < until;
        }

        /// <summary>L1: 低频清扫过期项，防字典无界增长。</summary>
        public static void SweepWithdrawn()
        {
            int tick = Find.TickManager.TicksGame;
            if (tick - lastSweepTick < 2000) return;
            lastSweepTick = tick;
            var expired = new List<int>();
            foreach (var kv in withdrawnUntil)
                if (tick >= kv.Value) expired.Add(kv.Key);
            for (int i = 0; i < expired.Count; i++)
                withdrawnUntil.Remove(expired[i]);
        }

        /// <summary>
        /// M3: 找接受该物品的「最高优先级」核心——路由决策基准。
        /// 多核心并存时物品去向不再取决于谁先 tick（旧实现各核心用自己的优先级独立决策）。
        /// </summary>
        private static Building_StorageCore FindBestIngestCore(Map map, Thing t)
        {
            var comp = DigitalStorageMapComponent.For(map);
            if (comp == null) return null;
            Building_StorageCore best = null;
            foreach (var core in comp.GetAllCores())
            {
                if (core == null || !core.Powered) continue;
                if (!core.AllowsItem(t)) continue;
                if (!core.Ledger.CanAccept(t, core.GetCapacity())) continue;
                if (best == null || core.storagePriority > best.storagePriority)
                    best = core;
            }
            return best;
        }

        public bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            core = parent as Building_StorageCore;
            // 新建核心的 gizmo 开关跟随 Mod 设置默认值
            enabled = DigitalStorage.Settings.DigitalStorageSettings.autoIngestEnabled;
        }

        /// <summary>
        /// F10: 显式刷新研究缓存（旧实现把 ingestRateCache 的初始化藏在 IsResearched
        /// 的 getter 副作用里，调用顺序一变就会拿到 -1 → bufSize 为负 → 数组越界）。
        /// </summary>
        private void EnsureResearchCache()
        {
            int tick = Find.TickManager.TicksGame;
            if (tick == researchCheckTick && ingestRateCache > 0) return;

            researchCheckTick = tick;
            researchedCache = ResearchProjectDef.Named("DigitalStorage_AutoIngest1")?.IsFinished ?? false;
            if (ResearchProjectDef.Named("DigitalStorage_AutoIngest3")?.IsFinished ?? false) ingestRateCache = 10;
            else if (ResearchProjectDef.Named("DigitalStorage_AutoIngest2")?.IsFinished ?? false) ingestRateCache = 5;
            else ingestRateCache = 1;
        }

        public bool IsResearched
        {
            get
            {
                EnsureResearchCache();
                return researchedCache;
            }
        }

        public override void CompTick()
        {
            base.CompTick();
            if (core == null || !enabled || !core.Powered) return;

            // 设置「自动收纳」总开关（社区反馈：关掉后瞬移搬运就消失）
            if (!DigitalStorage.Settings.DigitalStorageSettings.autoIngestEnabled) return;

            int tick = Find.TickManager.TicksGame;

            // 每 15 tick 一次，用核心 ID 错开相位
            if ((tick + core.thingIDNumber) % 15 != 0) return;

            EnsureResearchCache();
            if (!researchedCache) return;

            var map = core.Map;
            if (map == null) return;

            SweepWithdrawn(); // L1: 清扫过期标记

            // M4: 候选收集不再按核心过滤器/容量过滤——被过滤的物品仍应路由到合格储存区，
            // 「核心吃不吃」的判断下沉到 RouteGroundItem 吞入分支。
            int rate = ingestRateCache > 0 ? ingestRateCache : 1;
            int taken = 0;

            // 收集候选项到静态小缓冲，避免 Ingest/Destroy 修改 haulables 列表导致迭代异常。
            int bufSize = rate * 3;
            if (candidateBuffer.Length < bufSize)
                candidateBuffer = new Thing[bufSize];
            int bufCount = 0;

            bool homeAreaOnly = DigitalStorage.Settings.DigitalStorageSettings.autoIngestHomeAreaOnly;

            // 收集 1: 地面散落物品（listerHaulables）
            // 社区反馈「老远处的石块被瞬移」：这类没有玩家意图的物品默认只处理活动区内，
            // 防止地图边缘/远矿裸地物品被隔空吸走。
            var haulables = map.listerHaulables.ThingsPotentiallyNeedingHauling();
            foreach (var t in haulables)
            {
                if (bufCount >= bufSize) break;
                if (!CanCollect(t, map, homeAreaOnly)) continue;
                candidateBuffer[bufCount++] = t;
            }

            // 收集 2: 优先级 ≤ 全图最高核心 的 zone 内物品（用户 7.31 拍板——
            // 自动收纳也要处理 zone 物品：路由到更高 zone 或吸进核心）。
            // 60 tick 节流：zone 全扫比 listerHaulables 贵，不用 15 tick 节奏。
            // 注意：zone 是玩家主动划定的意图，不受「仅活动区」限制（矿区营地照常工作）。
            if (bufCount < bufSize && tick - lastZoneScanTick >= 60)
            {
                lastZoneScanTick = tick;
                var comp = DigitalStorageMapComponent.For(map);
                StoragePriority maxPrio = StoragePriority.Unstored;
                if (comp != null)
                {
                    foreach (var c in comp.GetAllCores())
                        if (c != null && c.Powered && c.storagePriority > maxPrio)
                            maxPrio = c.storagePriority;
                }
                if (maxPrio > StoragePriority.Unstored)
                {
                    var groups = map.haulDestinationManager.AllGroupsListInPriorityOrder;
                    foreach (var group in groups)
                    {
                        if (bufCount >= bufSize) break;
                        // 7.31 晚：缓冲仓库有独立阈值补货逻辑，跳过——吸回会与补货
                        // 互相拉扯成乒乓。普通 zone 行为不变。
                        if (group.parent is Building_BufferWarehouse) continue;
                        if (group.Settings.Priority > maxPrio) continue; // 严格高于最高核心 → 不动
                        foreach (var cell in group.CellsList)
                        {
                            if (bufCount >= bufSize) break;
                            var things = map.thingGrid.ThingsListAt(cell);
                            for (int i = 0; i < things.Count; i++)
                            {
                                if (bufCount >= bufSize) break;
                                var t = things[i];
                                if (!CanCollect(t, map, false)) continue;
                                candidateBuffer[bufCount++] = t;
                            }
                        }
                    }
                }
            }

            for (int i = 0; i < bufCount && taken < rate; i++)
            {
                var t = candidateBuffer[i];
                if (t.Destroyed) continue;

                // M3: 路由基准统一为「接受该物品的最高优先级核心」
                var best = FindBestIngestCore(map, t);
                if (best == null) continue;
                if (ItemRouter.RouteGroundItem(t, map, best, best.GetCapacity()))
                    taken++;
            }
            // 清理引用防止 GC 泄漏
            for (int i = 0; i < bufCount; i++)
                candidateBuffer[i] = null;
        }

        /// <summary>
        /// 自动收纳候选的统一过滤：可收纳、未禁止、未预订、保护窗口外、可选活动区。
        /// 不含 zone 判断（地面与 zone 内物品都处理，用户 7.31 拍板）。
        /// </summary>
        private static bool CanCollect(Thing t, Map map, bool homeAreaOnly)
        {
            if (t == null || t.Destroyed) return false;
            if (!LedgerPolicy.CanIngest(t)) return false;
            if (t.IsForbidden(Faction.OfPlayer)) return false;
            if (map.reservationManager.IsReserved(t)) return false;
            if (IsRecentlyWithdrawn(t)) return false;
            // 社区反馈「老远处的石块被瞬移」：默认只处理活动区内的物品。
            if (homeAreaOnly)
            {
                var home = map.areaManager?.Home;
                if (home != null && !home[t.Position]) return false;
            }
            return true;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref enabled, "autoIngestEnabled", true);
        }
    }
}
