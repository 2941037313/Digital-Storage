using System;
using System.Collections.Generic;
using System.Text;
using DigitalStorage.AI;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// <b>制作代理</b>：由建筑自己把范围内工作台上的 bill 做掉，殖民者不参与。
    ///
    /// <para><b>路线（与 <see cref="CompDigitalWorker"/> 同族，但活来自 bill 而不是 WorkGiver）</b>：
    /// 每 tick 扫范围内的 <c>IBillGiver</c>，每张台子一个槽位；取活时借原版
    /// <c>WorkGiver_DoBill.JobOnThing</c>（**公开入口**，job 里已装好原版挑的料，job 本身从不上岗），
    /// 然后自己扣工作量，够了就走 <c>GenRecipe.MakeRecipeProducts</c> 那套唯一漏斗结算。
    /// 细节与出处见 <c>docs/实现方案-bill自动化.md</c>。</para>
    ///
    /// <para><b>为什么不做真 job/真 toil</b>：假 pawn 过不了 <c>GotoThing</c> 的寻路层，
    /// 而且 job 会被写进存档。本 mod 整套代理体系就是"脱离 job 直调产出"，
    /// 见 obsidian <c>代码Wiki/rimworld/代理工人-脱离job直调产出.md</c>。</para>
    ///
    /// <para><b>速度</b>：<c>原版公式(recipe.workSpeedStat × 台子 workTableSpeedStat)</c>
    /// × <c>Props.workSpeedMult</c> × 超频倍率。不发经验（假工人资质恒定）。</para>
    ///
    /// <para><b>耗电</b>（用户拍板）：<c>(300 + Σ台子耗电) × 超频倍率</c>；
    /// 台子没有电力组件时按 100W 折算；台子自己的电**照交**（不豁免）。
    /// 台子断电/缺燃料 ⇒ 这台不干（代理不代劳加燃料）。</para>
    /// </summary>
    public class CompBillAutomation : ThingComp
    {
        /// <summary>槽位（一台工作台 = 一个）。不进存档：进度丢了不丢料（扣料只在完成那一刻）。</summary>
        private readonly List<BillSlot> slots = new List<BillSlot>();

        /// <summary>槽位是在哪张图上建的（认领表按图分桶，放手时要同一个 map 引用）。</summary>
        private Map slotsMap;

        private Pawn worker;
        private int nextScanTick;
        private bool enabled = true;

        /// <summary>超频档位：0 = 关，1 = 3GHz，2 = 6GHz，3 = 9GHz。</summary>
        private int overclockTier;

        /// <summary>缓存的实际耗电（W）。每 tick 只与 <c>CompPowerTrader.PowerOutput</c> 比一次。</summary>
        private float cachedWatts = -1f;

        // ---- 左上角提示的聚合缓冲（超频 ×9 时产物按秒刷，逐件弹会把消息栏刷爆）----
        private readonly Dictionary<string, int> pendingStored = new Dictionary<string, int>();
        private readonly Dictionary<string, int> pendingDropped = new Dictionary<string, int>();
        private int lastMessageTick = -99999;

        private int completedCount;
        private int droppedCount;

        public CompProperties_BillAutomation Props
        {
            get { return (CompProperties_BillAutomation)props; }
        }

        /// <summary>有电才能干活 —— 沿用核心"取出的唯一门就是电"的既有口径。</summary>
        public bool Powered
        {
            get
            {
                CompPowerTrader p = parent.GetComp<CompPowerTrader>();
                return p == null || p.PowerOn;
            }
        }

        public bool CanWork
        {
            get { return parent.Spawned && !parent.Destroyed && Powered && enabled; }
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                if (enabled == value) return;
                enabled = value;
                ReleaseAll();
                RecalcWatts();
            }
        }

        public int OverclockTier
        {
            get { return overclockTier; }
            set
            {
                int v = Mathf.Clamp(value, 0, 3);
                if (v == overclockTier) return;
                overclockTier = v;
                RecalcWatts();
            }
        }

        /// <summary>超频速度倍率（用户拍板：1 / 3 / 6 / 9）。</summary>
        public float OverclockSpeedMult
        {
            get { return overclockTier <= 0 ? 1f : (overclockTier == 1 ? 3f : (overclockTier == 2 ? 6f : 9f)); }
        }

        /// <summary>超频耗电倍率（用户拍板：1 / 9 / 27 / 81）。</summary>
        public float OverclockPowerMult
        {
            get { return overclockTier <= 0 ? 1f : (overclockTier == 1 ? 9f : (overclockTier == 2 ? 27f : 81f)); }
        }

        /// <summary>当前实际耗电（W），面板/检视栏用。</summary>
        public float CurrentWatts
        {
            get
            {
                float w = Props.basePowerWatts;
                if (enabled) w += BenchWatts();
                return w * OverclockPowerMult;
            }
        }

        /// <summary>范围内台子的耗电合计（没有电力组件的台子按 <c>benchWithoutPowerWatts</c> 折算）。</summary>
        public float BenchWatts()
        {
            float sum = 0f;
            for (int i = 0; i < slots.Count; i++)
            {
                Thing bench = slots[i].Bench;
                if (bench == null || bench.Destroyed) continue;
                CompPowerTrader p = bench.TryGetComp<CompPowerTrader>();
                sum += (p == null) ? Props.benchWithoutPowerWatts : p.Props.PowerConsumption;
            }
            return sum;
        }

        /// <summary>给面板看的槽位快照（只读用途）。<c>BillSlot</c> 是 internal ⇒ 本属性也是 internal。</summary>
        internal IList<BillSlot> SlotsForReading
        {
            get { return slots; }
        }

        public int CompletedCount
        {
            get { return completedCount; }
        }

        public int DroppedCount
        {
            get { return droppedCount; }
        }

        /// <summary>资质载体（懒建）。造失败要退避 —— 否则会每 tick 跑一次 <c>PawnGenerator</c>。</summary>
        public Pawn Worker
        {
            get
            {
                if (worker != null && !worker.Destroyed) return worker;

                int now = Find.TickManager.TicksGame;
                if (now < nextWorkerAttemptTick) return null;
                nextWorkerAttemptTick = now + 250;
                worker = DigitalWorkerFactory.Create(Props.skillLevel, "数字制作工", "Craft" + Props.skillLevel);
                return worker;
            }
        }

        private int nextWorkerAttemptTick = -99999;

        // ===================================================================
        // 主循环
        // ===================================================================

        public override void CompTick()
        {
            // 断电 / 被拆 / 关掉 ⇒ 立刻全放手（不占着台子）
            if (!CanWork)
            {
                ReleaseAll();
                return;
            }

            Map map = parent.Map;
            if (map == null) return;

            int now = Find.TickManager.TicksGame;

            // ① 扫描工作台（低频；台子集合不变时结果也不变）
            if (now >= nextScanTick)
            {
                nextScanTick = now + Math.Max(1, Props.scanIntervalTicks);
                RescanBenches(map);
                RecalcWatts();
            }

            // ② 电力复读：CompPowerTrader.SetUpPowerVars 会在电网变化/研究完成时被调
            //    （PowerNetManager.cs:125/160、ResearchManager.cs:450），把 PowerOutput 打回
            //    -props.PowerConsumption ⇒ 每 tick 只做一次 float 比较，被改了才写回。
            if (slots.Count == 0 && cachedWatts < 0f) { RecalcWatts(); }
            ReassertPower();

            // 左上角提示要在这里刷：槽位可能刚好在上一段被清空（bill 做完了），
            // 若把 FlushMessages 放在 `slots.Count == 0 return` 之后，最后那条提示就永远发不出去。
            FlushMessages(now);

            if (slots.Count == 0) return;

            Pawn w = Worker;
            if (w == null) return;

            // ③ 推进已有的活（先推进：完成的槽位本 tick 就能接上新活）
            AdvanceAll(w, map, now);

            // ④ 给空槽位取活（配额 + 每槽退避）
            int probes = Math.Max(1, Props.maxProbesPerTick);
            for (int i = 0; i < slots.Count && probes > 0; i++)
            {
                BillSlot s = slots[i];
                if (s.HasWork || now < s.NextAcquireTick) continue;
                probes--;
                BillProbe.TryAcquire(this, s, map, w, now);
            }

            // ⑤ 表现（提示已在前面刷过）
            UpdateVisuals(map);
        }

        /// <summary>
        /// 推进所有槽位。完成的交给 <see cref="BillCraftFunnel"/>。
        /// 「拿不到完成预算」与「这次作废」要分开：前者**不能丢进度**（活都干完了，等下一 tick 的票就行）。
        /// </summary>
        private void AdvanceAll(Pawn w, Map map, int now)
        {
            float speedMult = Math.Max(0.01f, Props.workSpeedMult) * OverclockSpeedMult;

            for (int i = slots.Count - 1; i >= 0; i--)
            {
                BillSlot s = slots[i];
                if (!s.HasWork) continue;

                if (!StillValid(s, map))
                {
                    s.ClearWork();
                    s.NextAcquireTick = 0;
                    s.BlockKey = null;
                    continue;
                }

                s.WorkLeft -= Math.Max(0f, s.BaseRate) * speedMult;

                try
                {
                    s.Bill.Notify_PawnDidWork(w);   // 原版 DoRecipeWork:104（Bill_Production 是空实现）
                }
                catch (Exception)
                {
                    // 子类可能重写；它抛异常不该弄死我们的 tick
                }

                if (s.WorkLeft > 0f) continue;

                CraftResult result = BillCraftFunnel.TryComplete(this, s, map, w);
                if (result == CraftResult.NoBudget)
                {
                    s.WorkLeft = 0f;      // 保持"差一点"，下一 tick 再收尾（不重做）
                    continue;
                }
                s.ClearWork();
                s.NextAcquireTick = 0;
            }
        }

        /// <summary>这一轮还作不作数（台子/电量/bill 状态/原料都还在）。</summary>
        private static bool StillValid(BillSlot s, Map map)
        {
            Thing bench = s.Bench;
            if (bench == null || bench.Destroyed || !bench.Spawned) return false;

            IBillGiver giver = bench as IBillGiver;
            if (giver == null) return false;
            if (!giver.CurrentlyUsableForBills()) return false;

            Bill_Production bill = s.Bill;
            if (bill == null || bill.recipe == null || bill.DeletedOrDereferenced || bill.suspended) return false;

            // 真人接手了这台 ⇒ 让
            if (map.reservationManager != null
                && map.reservationManager.IsReservedByAnyoneOf(bench, Faction.OfPlayer)) return false;

            // 原料还在容器里、数量还够（可能被真人或别的代理拿走了）
            for (int i = 0; i < s.Ingredients.Length; i++)
            {
                Thing t = s.Ingredients[i];
                if (t == null || t.Destroyed) return false;
                Building_StorageCore core = t.ParentHolder as Building_StorageCore;
                if (core == null || core.Map != map) return false;
                if (s.Counts[i] > t.stackCount) return false;
            }
            return true;
        }

        // ===================================================================
        // 扫描与认领
        // ===================================================================

        /// <summary>
        /// 扫"以建筑为中心的 13×13 方形"（<see cref="CompProperties_BillAutomation.scanRadius"/>）里的工作台。
        ///
        /// <para>候选来源用 <c>listerThings.ThingsInGroup(PotentialBillGiver)</c> —— 原版
        /// <c>WorkGiver_DoBill.ShouldSkip:128</c> 用的就是这条，它是有 lister 分支的组，不是全图遍历。</para>
        ///
        /// <para>只有"还有活要做"的台子才占槽位（<c>BillStack.AnyShouldDoNow</c>）：
        /// 空台子/达标台子不占并发数，也不需要代理去认领。</para>
        /// </summary>
        private void RescanBenches(Map map)
        {
            if (slotsMap != null && slotsMap != map)
            {
                ReleaseAll();      // 换图（穿梭机/传送门搬走了）：旧图的认领要放手
            }
            slotsMap = map;

            for (int i = 0; i < slots.Count; i++) slots[i].Seen = false;

            int radius = Math.Max(0, Props.scanRadius);
            CellRect rect = CellRect.CenteredOn(parent.PositionHeld, radius);
            List<Thing> all = map.listerThings.ThingsInGroup(ThingRequestGroup.PotentialBillGiver);

            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                if (t == null || t.Destroyed || !t.Spawned) continue;
                if (t == parent) continue;
                if (!rect.Contains(t.PositionHeld)) continue;

                IBillGiver giver = t as IBillGiver;
                if (giver == null) continue;
                if (!giver.BillStack.AnyShouldDoNow) continue;

                BillSlot slot = FindSlot(t);
                if (slot == null)
                {
                    if (slots.Count >= Math.Max(1, Props.maxSlots)) continue;
                    if (!BillBenchClaims.TryClaim(map, t, this)) continue;
                    slot = new BillSlot();
                    slot.Bench = t;
                    slots.Add(slot);
                }
                else if (BillBenchClaims.OwnerOf(map, t) != this)
                {
                    continue;   // 不是我的（被别人抢了）⇒ 本轮当没见到
                }
                slot.Seen = true;
            }

            // 本轮没再见到的：拆了 / 超范围 / 没活了 / 断电 ⇒ 释放（顺手放手认领）
            for (int i = slots.Count - 1; i >= 0; i--)
            {
                if (!slots[i].Seen) ReleaseSlotAt(i);
            }
        }

        private BillSlot FindSlot(Thing bench)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Bench == bench) return slots[i];
            }
            return null;
        }

        private void ReleaseSlotAt(int i)
        {
            BillSlot s = slots[i];
            if (slotsMap != null) BillBenchClaims.Release(slotsMap, s.Bench, this);
            s.ClearAll();
            slots.RemoveAt(i);
        }

        /// <summary>放手全部（断电 / 关掉 / 拆除 / 换图）。</summary>
        public void ReleaseAll()
        {
            for (int i = slots.Count - 1; i >= 0; i--)
            {
                slots[i].ClearAll();
            }
            slots.Clear();
            if (slotsMap != null) BillBenchClaims.ReleaseAll(slotsMap, this);
            slotsMap = null;
            pendingStored.Clear();
            pendingDropped.Clear();
        }

        // ===================================================================
        // 电力
        // ===================================================================

        /// <summary>按"当前台子集合 + 超频档位 + 开关"重算并写回耗电。</summary>
        public void RecalcWatts()
        {
            float w = Props.basePowerWatts;
            if (enabled) w += BenchWatts();
            cachedWatts = w * OverclockPowerMult;
            ReassertPower();
        }

        /// <summary>
        /// 把缓存值复读回 <c>CompPowerTrader</c>。原版会在若干时机调 <c>SetUpPowerVars</c>
        /// 把输出打回 <c>-Props.PowerConsumption</c>（电网变化、研究完成），所以不能只写一次。
        /// </summary>
        private void ReassertPower()
        {
            if (cachedWatts < 0f) return;
            CompPowerTrader p = parent.GetComp<CompPowerTrader>();
            if (p == null) return;
            float want = -cachedWatts;
            if (!Mathf.Approximately(p.PowerOutput, want)) p.PowerOutput = want;
        }

        // ===================================================================
        // 表现 + 左上角提示
        // ===================================================================

        private void UpdateVisuals(Map map)
        {
            int cap = Math.Max(0, Props.maxVisualSlots);
            for (int i = 0; i < slots.Count; i++)
            {
                slots[i].TickVisual(map, i, cap);
            }
        }

        /// <summary>
        /// 记一笔产物（给左上角提示聚合）。<paramref name="stored"/> = 已进核心。
        /// </summary>
        internal void NoteProduct(Thing p, bool stored)
        {
            if (p == null) return;
            string key = BillCraftFunnel.DescribeProduct(p);
            int amount = Math.Max(1, p.stackCount);

            Dictionary<string, int> map = stored ? pendingStored : pendingDropped;
            int cur;
            map.TryGetValue(key, out cur);
            map[key] = cur + amount;

            if (stored) completedCount++;
            else droppedCount++;
        }

        /// <summary>
        /// 按 <see cref="CompProperties_BillAutomation.messageIntervalTicks"/> 的节奏，把这段时间
        /// 做完的产物汇成**一条**左上角消息（用户拍板：产物直塞核心 + 左上角弹提示）。
        ///
        /// <para>用 <c>Messages.Message</c> 而不是信件：信件是屏幕中央的弹窗且会打断操作，
        /// 工厂按秒出货时那是灾难。品质信（大师/传奇）已被
        /// <see cref="BillAutomationScope"/> 抑制，品质信息在这里用"传奇 XX"补回来。</para>
        /// </summary>
        private void FlushMessages(int now)
        {
            if (pendingStored.Count == 0 && pendingDropped.Count == 0) return;
            if (now - lastMessageTick < Math.Max(0, Props.messageIntervalTicks)) return;
            lastMessageTick = now;

            string stored = BuildList(pendingStored);
            string dropped = BuildList(pendingDropped);

            TargetInfo look = new TargetInfo(parent.PositionHeld, parent.MapHeld);
            if (stored != null)
            {
                Messages.Message("DS_BA_Done".Translate(stored), look, MessageTypeDefOf.TaskCompletion, false);
            }
            if (dropped != null)
            {
                Messages.Message("DS_BA_DoneDropped".Translate(dropped), look, MessageTypeDefOf.CautionInput, false);
            }

            pendingStored.Clear();
            pendingDropped.Clear();
        }

        private static string BuildList(Dictionary<string, int> map)
        {
            if (map.Count == 0) return null;
            StringBuilder sb = new StringBuilder();
            int shown = 0;
            foreach (KeyValuePair<string, int> kv in map)
            {
                if (shown >= 4)
                {
                    sb.Append("…");
                    break;
                }
                if (shown > 0) sb.Append("、");
                sb.Append(kv.Key);
                sb.Append(" ×");
                sb.Append(kv.Value);
                shown++;
            }
            return sb.ToString();
        }

        // ===================================================================
        // Gizmo / 检查面板 / 存档
        // ===================================================================

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (!parent.Spawned) yield break;

            yield return new Command_Toggle
            {
                defaultLabel = "DS_BA_Toggle".Translate(),
                defaultDesc = "DS_BA_ToggleDesc".Translate(),
                isActive = () => enabled,
                toggleAction = () => { Enabled = !enabled; }
            };

            yield return new Command_Action
            {
                defaultLabel = "DS_BA_Overclock".Translate(OverclockLabel()),
                defaultDesc = "DS_BA_OverclockDesc".Translate(OverclockLabel(), CurrentWatts.ToString("#####0")),
                action = () =>
                {
                    int next = (overclockTier + 1) % 4;
                    OverclockTier = next;
                    Messages.Message("DS_BA_OverclockMsg".Translate(parent.LabelShort, OverclockLabel(), CurrentWatts.ToString("#####0")),
                        new TargetInfo(parent.PositionHeld, parent.MapHeld), MessageTypeDefOf.SilentInput, false);
                }
            };
        }

        /// <summary>当前超频档位名（面板/描述用）。</summary>
        public string OverclockLabel()
        {
            switch (overclockTier)
            {
                case 1: return "DS_BA_OC_3".Translate();
                case 2: return "DS_BA_OC_6".Translate();
                case 3: return "DS_BA_OC_9".Translate();
                default: return "DS_BA_OC_Off".Translate();
            }
        }

        /// <summary>
        /// 检查面板状态行。
        /// TODO：改成 Keyed 翻译（与 <c>CompDigitalWorker.CompInspectStringExtra</c> 同一笔债）。
        /// </summary>
        public override string CompInspectStringExtra()
        {
            if (!parent.Spawned) return null;
            if (!Powered) return "制作代理：断电";
            if (!enabled) return "制作代理：已关闭";

            int working = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].HasWork) working++;
            }

            string s = "制作代理：在产 " + working + " / 共 " + slots.Count + " 台 · 耗电 "
                + CurrentWatts.ToString("#####0") + "W（速度 " + Props.workSpeedMult.ToString("0.0")
                + "× × 超频 " + OverclockSpeedMult.ToString("0") + "×，资质 " + Props.skillLevel + "）";
            if (slots.Count == 0) s += " · 范围内没有在做 bill 的工作台";
            return s;
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            RecalcWatts();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            if (slotsMap == null) slotsMap = map;
            ReleaseAll();
            slotsMap = null;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref enabled, "billAutoEnabled", true);
            Scribe_Values.Look(ref overclockTier, "billAutoOverclock", 0);
            // 刻意不 Scribe worker / slots：工人与进行中的活都是可重建的派生状态。
            // 扣料只在完成那一刻发生 ⇒ 读档丢进度不丢料。
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                slots.Clear();
                slotsMap = null;
                cachedWatts = -1f;
                RecalcWatts();
            }
        }
    }
}
