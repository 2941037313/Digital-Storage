using System;
using System.Collections.Generic;
using System.Linq;
using DigitalStorage.AI;
using DigitalStorage.Core;
using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// I5a+I5b: 自动收纳。Tick 扫 listerHaulables → 过滤 → 吸入自身**容器**。
    /// （3.0 是吸入账本；4.0 改投真实容器，见 TryIngest。两者都保留"入库瞬移"的产品决策。）
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
        private static readonly ScanWindow scanWindow = new ScanWindow();

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
            Building_StorageCore best = null;
            // 直接枚举 haul source（不再走 DigitalStorageMapComponent 注册表 ——
            // 那个注册表自批 1 起无人写入，导致这里恒返回 null、自动收纳静默失效。
            // 详见 CoreFinder 类注释里的 bug 记录。）
            foreach (Building_StorageCore core in CoreFinder.AllUsableCores(map))
            {
                if (core == null || !core.Powered) continue;
                // 4.0：收不收由容器自己答（过滤器 + 容量 + HaulDestinationEnabled），
                // 不再有账本白名单。Accepts 已在「东西已经在里面」时只按过滤器作答。
                if (!core.Accepts(t)) continue;
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

            // 候选来源：map.listerThings.ThingsInGroup(HaulableEver)——**不用**原版的
            // listerHaulables.ThingsPotentiallyNeedingHauling()。
            //
            // 原版那张表是"待搬"判定：ListerHaulables.ShouldBeHaulable（ListerHaulables.cs:194）
            // 会把**已经位于其最优储存位置**的东西剔掉（:211 `IsInValidBestStorage()`）。
            // 而我们的产品语义恰恰要收"正躺在储存区里"的东西 —— 交易赚来的白银由
            // Pawn_TraderTracker.GiveSoldThingToPlayer:194 掉在**商人脚下的那格**上，
            // 若那格在储存区内，原版认为"已经放好了"，我们从那张表里就再也看不见它，
            // 表现就是"有的物品收得进、有的收不进"。
            //
            // 参考 Manipulator Beam Emitter（workshop 3683998684）的做法，
            // BeamManipulatorUtility.cs:110-111 的注释写得很准：
            //   「直接使用地图维护的可搬物列表，包含已经入库的物品；不等待原版逐格重算待搬状态。」
            //
            // 窗口轮转（同款 BeamScanWindow）：这张表可能上千项。若每轮都从表头扫、
            // 收满缓冲就 break，表尾会被**永久遮蔽**（而新物品恰恰是 append 到表尾的，
            // 最该被看见的正是它们）。所以每轮只扫一个预算窗口，游标持续前进并绕圈。
            List<Thing> all = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
            if (all.Count > 0)
            {
                StoragePriority maxPrio = StoragePriority.Unstored;
                foreach (Building_StorageCore c in CoreFinder.AllUsableCores(map))
                    if (c.storagePriority > maxPrio)
                        maxPrio = c.storagePriority;

                int budget = rate * 16;
                if (budget < 64) budget = 64;
                int start = scanWindow.Take(all.Count, budget);
                for (int step = 0; step < scanWindow.Count; step++)
                {
                    Thing t = all[(start + step) % all.Count];
                    if (!CanCollect(t, map, maxPrio, homeAreaOnly)) continue;
                    // 缓冲满了也把窗口推完（否则前段会永久遮蔽后段），只是不再收。
                    if (bufCount >= bufSize) continue;
                    candidateBuffer[bufCount++] = t;
                }
            }

            for (int i = 0; i < bufCount && taken < rate; i++)
            {
                var t = candidateBuffer[i];
                if (t.Destroyed) continue;

                // M3: 路由基准统一为「接受该物品的最高优先级核心」
                var best = FindBestIngestCore(map, t);
                if (best == null) continue;
                if (TryIngest(best, t))
                    taken++;
            }
            // 清理引用防止 GC 泄漏
            for (int i = 0; i < bufCount; i++)
                candidateBuffer[i] = null;
        }

        /// <summary>
        /// 入库瞬移：把一件地面物品直接搬进容器（产品决策：全部无损隔空获取，保留瞬移）。
        ///
        /// 4.0 与 3.0 的差别只在"搬进哪儿"：3.0 是 <c>ledger.Ingest</c>（纯数据），
        /// 4.0 是 <c>innerContainer.TryAdd</c>（真实 Thing）—— 也因此这批东西
        /// 立刻能被原版看见（bill 取料 / 读数 / 出库）。
        ///
        /// <b>失败必须放回地面</b>，否则物品凭空消失。
        /// </summary>
        private static bool TryIngest(Building_StorageCore core, Thing t)
        {
            if (core == null || t == null || t.Destroyed) return false;
            if (!core.Accepts(t)) return false;

            Map map = core.Map;
            if (map == null) return false;
            IntVec3 originalPos = t.PositionHeld;

            t.DeSpawn();
            if (core.GetDirectlyHeldThings().TryAdd(t, true))
            {
                core.Notify_SettingsChanged();
                return true;
            }

            GenPlace.TryPlaceThing(t, originalPos, map, ThingPlaceMode.Near);
            return false;
        }

        /// <summary>
        /// 自动收纳候选的统一过滤：可收纳、未禁止、未预订、保护窗口外、不在更高优先级的储存里、
        /// 可选活动区。**取代了原来"地面 / zone 两趟收集"的两套过滤**（见 CompTick 里的类注释）。
        /// </summary>
        private static bool CanCollect(Thing t, Map map, StoragePriority maxCorePrio, bool homeAreaOnly)
        {
            if (t == null || t.Destroyed) return false;
            // 4.0「全放开」：LedgerPolicy 白名单已废，只留"是不是物品"。
            // 具体收不收由目标容器的 Accepts（过滤器 + 容量）决定 —— 见 TryIngest。
            if (t.def == null || t.def.category != ThingCategory.Item) return false;

            // 未开采的矿脉/岩石同样是 Item 类别。原版 listerHaulables 是靠
            // "Mineable 的 alwaysHaulable 为 false ⇒ 没有搬运标记就不入表"顺带挡住它们的；
            // 我们改用 HaulableEver 之后它们会全部进来。社区反馈过「老远处的石块被瞬移」，
            // 旧实现用"仅活动区"兜（既不精确，又挡不住营地内挖的矿），这里直接按类型挡。
            if (t is Mineable) return false;

            if (t.IsForbidden(Faction.OfPlayer)) return false;
            if (map.reservationManager.IsReserved(t)) return false;
            if (IsRecentlyWithdrawn(t)) return false;

            // 玩家主动把东西放在**更高优先级**的储存里 → 不动它（用户 7.31 拍板的路由规则，
            // 旧实现写在 zone 全扫那一段，现在统一到这一个过滤器）。
            SlotGroup slotGroup = map.haulDestinationManager.SlotGroupAt(t.PositionHeld);
            if (slotGroup != null && slotGroup.Settings.Priority > maxCorePrio) return false;

            // 活动区限制只作用于"完全没有储存归属"的散落物：
            // 有储存归属（zone/货架/容器）的东西本身就是玩家划定的意图，不该被活动区挡住，
            // 否则交易掉在仓区里的白银、或区外营地的产出就永远收不进来。
            if (homeAreaOnly && slotGroup == null)
            {
                var home = map.areaManager?.Home;
                if (home != null && !home[t.PositionHeld]) return false;
            }
            return true;
        }

        /// <summary>
        /// 跨轮继续的小窗口扫描器：游标不因空闲、暂停或列表长度变化而重置，不写存档。
        /// 抄自 Manipulator Beam Emitter 的 <c>BeamScanWindow.cs</c>
        /// （用法同 <c>BeamManipulatorUtility.cs:110-125</c>）：全表轮转 + 每轮预算，
        /// 避免"每轮都从表头扫、收满就 break"把表尾永久遮蔽。
        /// </summary>
        private sealed class ScanWindow
        {
            private int cursor;
            private int remaining;

            public int Count { get; private set; }

            public int Take(int itemCount, int budget)
            {
                if (itemCount <= 0 || budget <= 0)
                {
                    Count = 0;
                    remaining = 0;
                    return 0;
                }
                cursor %= itemCount;
                if (remaining <= 0 || remaining > itemCount)
                {
                    remaining = itemCount;
                }
                int start = cursor;
                Count = Math.Min(budget, remaining);
                cursor = (cursor + Count) % itemCount;
                remaining -= Count;
                return start;
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref enabled, "autoIngestEnabled", true);
        }
    }
}
