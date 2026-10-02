using System.Collections.Generic;
using RimWorld;
using Verse;
namespace DigitalStorage.Components
{
    /// <summary>
    /// 代理建筑（数字工人）的 comp 属性。
    ///
    /// <para>一个建筑 = 一类工作 × 一个等级。等级体现为三件事：
    /// <b>技能资质</b>（只进品质/产量，**不进速度**）、<b>工作速度倍率</b>、以及 XML 里的耗电与造价。</para>
    /// </summary>
    public class CompProperties_DigitalWorker : CompProperties
    {
        /// <summary>
        /// 这个代理建筑管哪些工作类型。
        ///
        /// <para><b>为什么是列表</b>："种植"在游戏里是两个 WorkTypeDef（<c>Growing</c> 播种/收割、
        /// <c>PlantCutting</c> 伐木），而用户把伐木归进种植 ⇒ 一个种植代理要同时管这两个。
        /// 扫描时按列表顺序依次试，第一个命中的活就认领。</para>
        /// </summary>
        public List<WorkTypeDef> workTypes;

        /// <summary>工人的技能资质（8 / 15 / 20）。只用于品质与产量判定，**不参与速度计算**。</summary>
        public int skillLevel = 8;

        /// <summary>
        /// 工作速度倍率（0.8 / 1.2 / 2.0）。
        ///
        /// <para><b>这是速度的唯一来源</b>——刻意不乘 <c>pawn.GetStatValue(...)</c>：
        /// ① 原版清洁根本不吃技能（<c>CleaningSpeed</c> 无 skillNeed），若让技能进速度，
        /// 清洁代理升到三级毫无变化；② 技能与倍率双算会让三级比值失控
        /// （挖掘 lv20 的 stat 已是 lv8 的 2.44 倍，再乘 2/0.8 = 6.1 倍）。
        /// 基准 = 原版"技能 8 的健康殖民者"（三类工作速度在 lv8 恰好 = 1.000）。</para>
        /// </summary>
        public float workSpeedMult = 0.8f;

        /// <summary>两次找活之间的间隔（tick）。默认 60 = 1 秒。</summary>
        public int scanIntervalTicks = 60;

        /// <summary>
        /// <b>每类工作最多同时处理几件活</b>（并行度）。
        ///
        /// <para>普通代理建筑 = 1（一次一件）；<b>超凡代理 = 50</b>，它管 4 个 workTypes
        /// ⇒ 总计最多 50 × 4 = 200 件同时进行。</para>
        /// </summary>
        public int maxParallelPerWorkType = 1;

        /// <summary>
        /// <b>总并行上限</b>（0 = 不限）。
        ///
        /// <para>为什么要这个：用户说的是"每类 50、四类合计 200"，但游戏里"种植"是
        /// <b>两个</b> WorkTypeDef（<c>Growing</c> + <c>PlantCutting</c>），
        /// 只靠"每类 50"会算成 5 × 50 = 250。加上总量上限才能精确表达"合计 200"。</para>
        /// </summary>
        public int maxParallelTotal = 0;

        /// <summary>
        /// 最多同时给几件活画"手 + 黄色读条"。
        ///
        /// <para>纯表现上限：并行 200 时如果每件都挂一只 Mote + 一根读条，
        /// 画面会变成"手海"而且帧数会掉。默认只画最近的 6 件（机制不受影响）。</para>
        /// </summary>
        public int maxVisualTasks = 6;

        public CompProperties_DigitalWorker()
        {
            compClass = typeof(CompDigitalWorker);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string e in base.ConfigErrors(parentDef))
            {
                yield return e;
            }
            if (workTypes == null || workTypes.Count == 0)
            {
                yield return parentDef.defName + "：CompProperties_DigitalWorker 必须指定至少一个 <workTypes><li>…</li></workTypes>。";
            }
            if (skillLevel < 0 || skillLevel > 20)
            {
                yield return parentDef.defName + "：skillLevel 应在 0~20（收到 " + skillLevel + "）。";
            }
            if (workSpeedMult <= 0f)
            {
                yield return parentDef.defName + "：workSpeedMult 必须 > 0（收到 " + workSpeedMult + "）。";
            }
            if (maxParallelPerWorkType < 1)
            {
                yield return parentDef.defName + "：maxParallelPerWorkType 至少为 1（收到 " + maxParallelPerWorkType + "）。";
            }
            if (maxParallelTotal < 0)
            {
                yield return parentDef.defName + "：maxParallelTotal 不能为负（0 = 不限，收到 " + maxParallelTotal + "）。";
            }
            if (maxVisualTasks < 0)
            {
                yield return parentDef.defName + "：maxVisualTasks 不能为负（收到 " + maxVisualTasks + "）。";
            }
        }
    }
}
