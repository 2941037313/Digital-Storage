using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Core;
using DigitalStorage.Effects;
using UnityEngine;
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


            // 候选来源：**原版自己的"待搬"表** —— `WorkGiver_Haul.PotentialWorkThingsGlobal`
            // 用的就是它（WorkGiver_Haul.cs:16）。于是"禁止 / 不可搬 / 已经在最优储存位置"
            // 全部由原版判定（`ListerHaulables.ShouldBeHaulable` → `StoreUtility.IsInValidBestStorage`），
            // 我们一行都不用重写；未开采的矿脉也天然不在表里（Mineable 的 alwaysHaulable 为
            // false ⇒ 没有搬运标记就不入表），所以连 `t is Mineable` 这种特例都不需要。
            //
            // 【曾走过的弯路，勿重蹈】一度换成 `listerThings.ThingsInGroup(HaulableEver)`，
            // 理由是"原版待搬表漏掉了躺在仓区里的交易白银"。那个判断是**错的**：
            // `IsInValidBestStorage` 用 `faction: Faction.OfPlayer` 去找"有没有更好的去处"，
            // 而核心走的是 `TryFindBestBetterNonSlotGroupStorageFor` 那条腿
            // （StoreUtility.cs:144 / :242），只要核心比当前储存更优，物品本来就在待搬表里。
            // 真正的元凶是当时那条活动区闸门（已删）。
            var haulables = map.listerHaulables.ThingsPotentiallyNeedingHauling();
            foreach (Thing t in haulables)
            {
                if (bufCount >= bufSize) break;
                if (!CanCollect(t, map)) continue;
                candidateBuffer[bufCount++] = t;
            }

            for (int i = 0; i < bufCount && taken < rate; i++)
            {
                var t = candidateBuffer[i];
                if (t.Destroyed) continue;

                // 路由判据：**原版自己会不会把它搬进核心**（见 WouldVanillaHaulIntoCore）。
                // 优先级比较、过滤器、容量全部由原版回答，不再由本 mod 复述一遍。
                Building_StorageCore dest = WouldVanillaHaulIntoCore(map, t);
                if (dest == null) continue;

                // 立刻入库（零延迟的产品决策不变）。光束只是**余像**：物品此刻已经进核心，
                // 所以在它被搬走之前先把原来的格子和绘制位置记下来交给光束。
                IntVec3 beamCell = t.PositionHeld;
                Vector3 beamPos = t.DrawPos;
                if (TryIngest(dest, t))
                {
                    taken++;
                    SpawnIngestBeam(map, beamCell, beamPos);
                }
            }
            // 清理引用防止 GC 泄漏
            for (int i = 0; i < bufCount; i++)
                candidateBuffer[i] = null;
        }

        /// <summary>
        /// 在物品**原来站的那一格**留一道收纳光束（纯余像）。物品这一刻已经进核心了，
        /// 光束<b>不承载任何逻辑</b>：拿不到 Def 或生成失败就直接没有任何效果，不影响功能。
        /// 由 <c>Mote_DS_IngestBeam</c>（Mote 子类）自己按 Def 的 mote 字段播完消失。
        /// </summary>
        private static void SpawnIngestBeam(Map map, IntVec3 cell, Vector3 drawPos)
        {
            ThingDef beamDef = DefDatabase<ThingDef>.GetNamedSilentFail("DS_IngestBeam");
            if (beamDef == null) return;

            Mote_DS_IngestBeam mote = GenSpawn.Spawn(beamDef, cell, map) as Mote_DS_IngestBeam;
            if (mote == null) return;

            mote.exactPosition = drawPos;
            mote.Init(Rand.Range(0f, 360f));
        }

        /// <summary>
        /// 入库瞬移：把一件地面物品直接搬进容器（产品决策：全部无损隔空获取，保留瞬移、零延迟）。
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
        /// 候选被拒的原因（<b>只保留原版不回答的那两件事</b>）。过滤与诊断共用这一份判定
        /// （<see cref="RejectReason"/>），诊断绝不另写一套近似逻辑 —— 那样两边会漂移，
        /// 出现"诊断全绿但就是不收"的假象（这个坑本 mod 已经踩过一次）。
        ///
        /// <para>其余判据**全部交还原版**：禁止 / 不可搬 / 已在最优储存 → 由
        /// <c>listerHaulables</c> 的准入（<c>ShouldBeHaulable</c>）回答；
        /// 未开采矿脉 → 同上（Mineable 不入表）；优先级与容量 → 由
        /// <see cref="WouldVanillaHaulIntoCore"/> 里的原版目的地搜索回答。
        /// 本 mod 不再复述任何一条，也就不会和原版漂移。</para>
        /// </summary>
        internal enum Reject
        {
            None = 0,
            NullOrDestroyed,
            /// <summary>不在图上（在别人背包里 / 在某个容器里）—— 绝不能对它们 DeSpawn。</summary>
            NotOnMap,
            Reserved,
            RecentlyWithdrawn
        }

        /// <summary>
        /// 候选过滤：只做两件原版不替我们回答的事 —— **只读的预订检查**（不碰
        /// <c>ReservationManager.Reserve</c>，与 MoreOrgans 的劳务手同款）与
        /// **"刚被取出"保护窗口**（防止玩家取出后立刻被吸回去），外加一条
        /// <b>必须在地图上</b>的守卫：<c>listerHaulables</c> 里会出现"因过滤器变更而要从
        /// 核心里搬出去"的内容物（甲-1 的机制），对容器里的东西 <c>DeSpawn</c> 是错的。
        ///
        /// <para>活动区闸门已于 4.0 删除：它误伤极大（挖矿掉落、拆建筑材料、交易掉在区外的
        /// 白银全在 Home 区之外），而它想防的"老远处石块"其实是未开采矿脉，现已由原版
        /// 待搬表天然挡住。用户既定方向是「不加任何限制、任何位置隔空取放」。</para>
        /// </summary>
        internal static Reject RejectReason(Thing t, Map map)
        {
            if (t == null || t.Destroyed) return Reject.NullOrDestroyed;
            if (!t.Spawned || t.ParentHolder != null) return Reject.NotOnMap;
            if (map.reservationManager.IsReserved(t)) return Reject.Reserved;
            if (IsRecentlyWithdrawn(t)) return Reject.RecentlyWithdrawn;
            return Reject.None;
        }

        private static bool CanCollect(Thing t, Map map)
        {
            return RejectReason(t, map) == Reject.None;
        }

        /// <summary>
        /// <b>收纳判据：原版自己会不会把这件东西搬进我们的核心。</b>
        ///
        /// <para>调用的是原版 <c>StoreUtility.TryFindBestBetterStorageFor</c>
        /// （<c>StoreUtility.cs:144</c>）：它同时搜"格子型储存"与"非格子型储存"，
        /// 后者（<c>TryFindBestBetterNonSlotGroupStorageFor</c>，<c>:242</c>）正是我们的核心
        /// 所在的那条腿（只跳过 <c>ISlotGroupParent</c> / <c>Building_Grave</c> /
        /// <c>!HaulDestinationEnabled</c>）。返回的目的地就是我们的核心时才插手
        /// —— <b>"原版本来就要把它搬进核心"，我们只是把这段搬运改成瞬时完成。</b></para>
        ///
        /// <para><b>为什么可以传 <c>carrier: null</c></b>：原版自己就这么调
        /// （<c>IsInValidBestStorage</c>，<c>StoreUtility.cs:67</c>，连
        /// <c>needAccurateResult: false</c> 也一样），它是个常年跑的路径，空 carrier 是被支持的。
        /// <b>这也正是我们要的</b>：不借任何殖民者 ⇒ <c>p.health.capacities</c>、
        /// <c>p.CanReserve</c>、可达性都不会渗进结论，某个小人倒地/断手也不会让核心静默停工
        /// （"殖民者状态不影响核心的工作"）。距离只用于同为格子型储存之间的决胜，
        /// 永远不会成为挡住核心的闸门。</para>
        /// </summary>
        internal static Building_StorageCore WouldVanillaHaulIntoCore(Map map, Thing t)
        {
            if (map == null || t == null || t.Destroyed) return null;

            StoragePriority currentPriority = StoreUtility.CurrentStoragePriorityOf(t);
            if (!StoreUtility.TryFindBestBetterStorageFor(t, null, map, currentPriority,
                    Faction.OfPlayer, out IntVec3 _, out IHaulDestination dest, needAccurateResult: false))
            {
                return null; // 原版没有更好的去处（已经放好了 / 没地方放）
            }

            Building_StorageCore core = dest as Building_StorageCore;
            if (core == null || core.Destroyed) return null;
            if (!core.Powered) return null;  // 断电的核心原版也不会搬进去（没有电力它就不可用）
            return core;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref enabled, "autoIngestEnabled", true);
        }
    }
}
