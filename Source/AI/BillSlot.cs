using DigitalStorage.Effects;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 一次结算的结果。
    ///
    /// <para><b>为什么要三态而不是 bool</b>：<see cref="BillCraftFunnel.TryComplete"/> 失败有两种完全不同的原因 ——
    /// "这次作废"（bill 被真人做完 / 台子断电 / 原料被抢）应当**丢掉进度重新取活**；
    /// 而"这一 tick 拿不到完成预算"只是限流，活其实已经干完了，**必须保留进度**等下一 tick 的票。
    /// 用 bool 就会把后者降级成"重做一遍"，在预算吃紧时表现为产量凭空掉一半。</para>
    /// </summary>
    internal enum CraftResult
    {
        Done,
        NoBudget,
        Invalid
    }

    /// <summary>
    /// <b>一个槽位 = 一张工作台 = 一个虚拟工匠</b>。
    ///
    /// <para>槽位里放的是"这一轮要做的那条 bill + 原版亲手挑好的原料"（<see cref="BillProbe"/> 填），
    /// 然后每 tick 扣工作量，够了就交给 <see cref="BillCraftFunnel"/> 结算。</para>
    ///
    /// <para><b>为什么不进存档</b>：进行中的进度是纯派生状态（真原版也存 <c>JobDriver_DoBill.workLeft</c>，
    /// 但我们更省事）—— 关键是**扣料只发生在完成那一刻**，所以读档丢进度**不会丢料**，
    /// 最多是这一件重新开始。认领表同理（谁先扫到谁拿）。</para>
    /// </summary>
    internal sealed class BillSlot
    {
        /// <summary>认领的工作台（一定是 <c>IBillGiver</c>）。</summary>
        public Thing Bench;

        /// <summary>本轮扫描有没有再见到它（没见到 = 拆了/超范围/被别人认领/没活了 ⇒ 释放）。</summary>
        public bool Seen;

        /// <summary>原版 <c>WorkGiver_DoBill.JobOnThing</c> 给的 job。**只当数据用**：原料队列 + <c>bill</c>，job 从不上岗。</summary>
        public Job Probe;

        /// <summary>正在做的那条 bill。</summary>
        public Bill_Production Bill;

        /// <summary>原版选好的原料（指向核心容器里的真实 Thing）与每件取多少（=<c>job.countQueue</c>）。</summary>
        public Thing[] Ingredients;
        public int[] Counts;

        /// <summary>原版速度公式在"取活那一刻"的值：<c>workSpeedStat</c> × 台子 <c>workTableSpeedStat</c>。</summary>
        public float BaseRate = 1f;

        public float WorkAmount;
        public float WorkLeft;

        /// <summary>下次允许取活的 tick（失败退避，见 <c>CompProperties_BillAutomation.noWorkRetryTicks</c>）。</summary>
        public int NextAcquireTick;

        /// <summary>没在干活时的原因（Keyed 键名后缀，如 <c>DS_BA_NoMaterial</c>）；null = 正在干活。</summary>
        public string BlockKey;

        // ---- 表现件（手 / 黄条）----
        public Mote_DS_WorkHand Hand;
        public Effecter Bar;
        private float strikeTimer;

        public bool HasWork
        {
            get { return Bill != null && Ingredients != null; }
        }

        /// <summary>0~1 进度（给建筑底下那根黄色读条用）。</summary>
        public float Progress01
        {
            get
            {
                if (WorkAmount <= 0f) return 1f;
                return Mathf.Clamp01(1f - WorkLeft / WorkAmount);
            }
        }

        /// <summary>丢掉这一轮（保留槽位本身与认领）。</summary>
        public void ClearWork()
        {
            Probe = null;
            Bill = null;
            Ingredients = null;
            Counts = null;
            BaseRate = 1f;
            WorkAmount = 0f;
            WorkLeft = 0f;
        }

        /// <summary>彻底释放槽位（连带表现件）。</summary>
        public void ClearAll()
        {
            ClearWork();
            CleanupVisual();
            Bench = null;
            Seen = false;
            NextAcquireTick = 0;
            BlockKey = null;
        }

        // ===================================================================
        // 表现：目标（工作台）上那只手 + 目标底下的黄色读条
        // ===================================================================

        /// <summary>
        /// 与 <c>CompDigitalWorker.UpdateVisuals</c> 同构（原版 <c>EffecterDefOf.ProgressBar</c> +
        /// <c>Mote_DS_WorkHand</c>），只是目标换成工作台。
        /// <c>index &gt;= cap</c> 时收掉 —— 槽位可能几十个，全画就是"手海"。
        /// </summary>
        public void TickVisual(Map map, int index, int cap)
        {
            Thing t = Bench;
            if (index >= cap || t == null || t.Destroyed || !t.Spawned || !HasWork)
            {
                CleanupVisual();
                return;
            }

            // ---- 手 ----
            if (Hand == null || Hand.Destroyed || !Hand.Spawned)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("DS_WorkHand");
                if (def != null)
                {
                    Mote_DS_WorkHand m = ThingMaker.MakeThing(def) as Mote_DS_WorkHand;
                    if (m != null)
                    {
                        GenSpawn.Spawn(m, t.PositionHeld, map);
                        Hand = m;
                    }
                }
            }
            if (Hand != null && !Hand.Destroyed)
            {
                Vector3 pos = t.DrawPos;
                pos.y = 0f;        // y 由 Mote.DrawMote 按 altitudeLayer 每帧重设
                pos.z += 0.15f;    // 略微朝镜头，压在目标正面
                Hand.exactPosition = pos;
                Hand.Maintain();   // 不再 Maintain 时它 1 秒后自愈消失

                strikeTimer -= 1f;
                if (strikeTimer <= 0f)
                {
                    strikeTimer = 30f;   // 半秒挥一下
                    Hand.Strike();
                }
            }

            // ---- 黄色读条（原版 EffecterDefOf.ProgressBar + MoteProgressBar）----
            if (Bar == null) Bar = EffecterDefOf.ProgressBar.Spawn();
            Bar.EffectTick(new TargetInfo(t), TargetInfo.Invalid);

            MoteProgressBar mote = (Bar.children.Count > 0)
                ? (Bar.children[0] as SubEffecter_ProgressBar)?.mote
                : null;
            if (mote != null)
            {
                mote.progress = Progress01;
                mote.offsetZ = -0.5f;     // 原版 WithProgressBar 的默认位置（贴在目标"底下"）
                mote.alwaysShow = true;   // 代理可能在远离镜头处干活，别只在最近缩放才画
            }
        }

        public void CleanupVisual()
        {
            if (Hand != null && !Hand.Destroyed) Hand.Destroy();
            Hand = null;
            if (Bar != null)
            {
                Bar.Cleanup();
                Bar = null;
            }
            strikeTimer = 0f;
        }
    }
}
