using UnityEngine;
using Verse;

namespace DigitalStorage.Settings
{
    /// <summary>
    /// Mod 设置：三个<b>数值倍率</b>（造价 / 电力 / 研究点数）、日志开关、以及社区反馈要求的访问/收纳开关。
    ///
    /// <para>倍率都是**全局**的（含原版与其它 mod），施加逻辑见 <see cref="DefMultipliers"/> ——
    /// 这里只负责改值 + 立刻施加。滑条是<b>离散档位</b>（<see cref="MultiplierLadder"/>）：
    /// 1/100 ~ 100× 跨了四个数量级，线性滑条会让"1×"挤在 1% 的位置上没法用，
    /// 所以档位按等比铺开，两端都点得到。</para>
    /// </summary>
    public class DigitalStorageSettings : ModSettings
    {
        /// <summary>造价倍率：所有建筑 / 地板 / 物品的建造材料。</summary>
        public static float costMultiplier = 1.0f;

        /// <summary>电力倍率：所有建筑的耗电量（发电不受影响）。</summary>
        public static float powerMultiplier = 1.0f;

        /// <summary>研究点数倍率：所有研究的所需点数。</summary>
        public static float researchMultiplier = 1.0f;

        public static bool enableDebugLog = false;

        /// <summary>
        /// 自动收纳：核心每 15 tick 把附近散落物品吸入自身**容器**。
        /// （3.0 是吸入账本；4.0 改投真实容器。）
        /// 关掉后仍可用原版搬运工单，只是没有「隔空收纳」。
        /// （社区反馈：「禁止自动收纳后（瞬移搬运）就没有出现了」→ 给玩家开关）
        /// </summary>
        public static bool autoIngestEnabled = true;

        /// <summary>
        /// 原版热点的顺手优化，三块（详见 <c>Source/Performance/</c>）：
        /// ① 框选一堆东西时括号的视野裁剪；② 标记批绘矩阵的增量维护（原版每帧全量重建）；
        /// ③ 大批量标记时的"元气泡"每 tick 节流。
        /// <para>默认开。万一出现任何视觉异常，关掉即可**逐条退回原版行为**（不涉及存档）。</para>
        /// </summary>
        public static bool perfOptimizationsEnabled = true;

        /// <summary>
        /// <b>每 tick 最多让代理"真正落地"几件活</b>（0 = 无限制）。
        ///
        /// <para>落地 = 走原版那一次性代价（挖完生成掉落物 / 建完生成建筑 / 拆完还材料 / 收获生成作物），
        /// 实测约 0.4~0.5ms/件。代理吞吐是"并行 75 × 7.5 倍速 ⇒ 每 tick 完成 20~40 件"，
        /// 也就是每 tick 要 10~20ms，而 60Hz 下每帧只有 16.7ms（且每帧必跑 1 tick）
        /// ⇒ 帧时间 20~56ms。压住这个数字，帧时间就变成常量；代价是扫图变慢。</para>
        ///
        /// <para>默认 16：约 960 件/秒（8000 格的图约 8~9 秒扫完），CPU 约 6~8ms/tick。</para>
        /// </summary>
        public static int workerCompletionsPerTick = 16;

        /// <summary>
        /// <b>每帧最多创建多少个 fleck（气泡特效）</b>（0 = 无限制）。
        ///
        /// <para>实测症状：250×250 全图标记一次 ⇒ <c>DesignationManager.AddDesignation</c>
        /// 给每个标记喷 4~6 个元气泡 ⇒ 几万个 fleck 同一瞬间创建、各自活 1~2 秒并逐帧绘制
        /// ⇒ "标记完闪一下、卡几秒"（实测 Flecks 吃 2.9~3.1ms/帧、峰值 29ms）。
        /// fleck 是纯视觉对象，丢掉只影响观感。</para>
        ///
        /// <para>默认 500/帧（≈3 万/秒）：正常游玩远低于此，无感；只有病态洪泛会被削平。</para>
        /// </summary>
        public static int fleckBudgetPerFrame = 500;

        /// <summary>
        /// 倍率档位：1/100 到 100× 等比铺开 18 档，两端都点得到，中间全是round数。
        /// 顺序必须递增（滑条按下标取值）。
        /// </summary>
        private static readonly float[] MultiplierLadder =
        {
            0.01f, 0.02f, 0.05f, 0.1f, 0.15f, 0.2f, 0.3f, 0.5f, 0.75f,
            1f, 1.5f, 2f, 3f, 5f, 10f, 20f, 50f, 100f
        };

        public override void ExposeData()
        {
            Scribe_Values.Look(ref costMultiplier, "costMultiplier", 1.0f);
            Scribe_Values.Look(ref powerMultiplier, "powerMultiplier", 1.0f);
            Scribe_Values.Look(ref researchMultiplier, "researchMultiplier", 1.0f);
            Scribe_Values.Look(ref enableDebugLog, "enableDebugLog", false);
            Scribe_Values.Look(ref autoIngestEnabled, "autoIngestEnabled", true);
            Scribe_Values.Look(ref perfOptimizationsEnabled, "perfOptimizationsEnabled", true);
            Scribe_Values.Look(ref workerCompletionsPerTick, "workerCompletionsPerTick", 16);
            Scribe_Values.Look(ref fleckBudgetPerFrame, "fleckBudgetPerFrame", 500);
            base.ExposeData();

            // 不在这里施加：读设置文件可能早于 XML 解析（DefDatabase 还空着）。
            // 施加由 [StaticConstructorOnStartup]（DefMultipliers 静态构造）与下面的滑条负责。
        }

        public static void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            bool changed = false;

            Text.Font = GameFont.Medium;
            listing.Label("DS_CostSettings".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.Label("DS_CostMultiplier".Translate(costMultiplier));
            costMultiplier = MultiplierSlider(listing, costMultiplier, ref changed);
            listing.Gap(6f);
            listing.Label("DS_CostMultiplierDesc".Translate());
            listing.Gap(18f);

            listing.Label("DS_PowerMultiplier".Translate(powerMultiplier));
            powerMultiplier = MultiplierSlider(listing, powerMultiplier, ref changed);
            listing.Gap(6f);
            listing.Label("DS_PowerMultiplierDesc".Translate());
            listing.Gap(18f);

            listing.Label("DS_ResearchMultiplier".Translate(researchMultiplier));
            researchMultiplier = MultiplierSlider(listing, researchMultiplier, ref changed);
            listing.Gap(6f);
            listing.Label("DS_ResearchMultiplierDesc".Translate());
            listing.Gap(24f);

            Text.Font = GameFont.Medium;
            listing.Label("DS_AccessSettings".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.CheckboxLabeled("DS_AutoIngestToggle".Translate(), ref autoIngestEnabled,
                "DS_AutoIngestToggleDesc".Translate());
            listing.Gap(6f);
            listing.CheckboxLabeled("DS_PerfToggle".Translate(), ref perfOptimizationsEnabled,
                "DS_PerfToggleDesc".Translate());
            listing.Gap(6f);
            listing.Label("DS_WorkerBudget".Translate(workerCompletionsPerTick));
            float budget = workerCompletionsPerTick;
            budget = listing.Slider(budget, 0f, 120f);
            workerCompletionsPerTick = Mathf.RoundToInt(budget);
            listing.Label("DS_WorkerBudgetDesc".Translate());
            listing.Gap(6f);
            listing.Label("DS_FleckBudget".Translate(fleckBudgetPerFrame));
            float flecks = fleckBudgetPerFrame;
            flecks = listing.Slider(flecks, 0f, 5000f);
            fleckBudgetPerFrame = Mathf.RoundToInt(flecks);
            listing.Label("DS_FleckBudgetDesc".Translate());
            listing.Gap(24f);

            Text.Font = GameFont.Medium;
            listing.Label("DS_DebugSettings".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.CheckboxLabeled("DS_EnableDebugLog".Translate(), ref enableDebugLog,
                "DS_EnableDebugLogDesc".Translate());

            listing.End();

            // 离散档位 ⇒ 一次拖动最多触发十几次，代价可以忽略（改完立刻生效：造价/研究改 def、
            // 电力把已建成建筑的 PowerOutput 重算一遍）。
            if (changed) DefMultipliers.ApplyAll();
        }

        /// <summary>
        /// 倍率滑条：滑的是**档位下标**，返回值是档位对应的倍率。
        /// 当前值不在档位上时（老存档里的 0.37 之类）取最近档显示，但**不动**玩家的值 ——
        /// 只有真的拖了才落到档位上。
        /// </summary>
        private static float MultiplierSlider(Listing_Standard listing, float current, ref bool changed)
        {
            int index = NearestLadderIndex(current);
            float value = Widgets.HorizontalSlider(listing.GetRect(22f), index, 0f,
                MultiplierLadder.Length - 1, true, null, null, null, 1f);
            int newIndex = Mathf.Clamp(Mathf.RoundToInt(value), 0, MultiplierLadder.Length - 1);

            float result = MultiplierLadder[newIndex];
            if (!Mathf.Approximately(result, current)) changed = true;
            return result;
        }

        private static int NearestLadderIndex(float value)
        {
            int best = 0;
            float bestDelta = float.MaxValue;
            for (int i = 0; i < MultiplierLadder.Length; i++)
            {
                float delta = Mathf.Abs(MultiplierLadder[i] - value);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = i;
                }
            }
            return best;
        }
    }
}
