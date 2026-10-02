using UnityEngine;
using Verse;

namespace DigitalStorage.Settings
{
    /// <summary>
    /// Mod 设置：造价倍率、日志开关、以及社区反馈要求的访问/收纳开关。
    /// </summary>
    public class DigitalStorageSettings : ModSettings
    {
        public static float costMultiplier = 1.0f;
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

        public override void ExposeData()
        {
            Scribe_Values.Look(ref costMultiplier, "costMultiplier", 1.0f);
            Scribe_Values.Look(ref enableDebugLog, "enableDebugLog", false);
            Scribe_Values.Look(ref autoIngestEnabled, "autoIngestEnabled", true);
            Scribe_Values.Look(ref perfOptimizationsEnabled, "perfOptimizationsEnabled", true);
            Scribe_Values.Look(ref workerCompletionsPerTick, "workerCompletionsPerTick", 16);
            base.ExposeData();
        }

        public static void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            Text.Font = GameFont.Medium;
            listing.Label("DS_CostSettings".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.Label("DS_CostMultiplier".Translate(costMultiplier));
            costMultiplier = listing.Slider(costMultiplier, 0.1f, 20f);
            listing.Gap(6f);
            listing.Label("DS_CostMultiplierDesc".Translate());
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
            listing.Gap(24f);

            Text.Font = GameFont.Medium;
            listing.Label("DS_DebugSettings".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.CheckboxLabeled("DS_EnableDebugLog".Translate(), ref enableDebugLog,
                "DS_EnableDebugLogDesc".Translate());

            listing.End();
        }
    }
}
