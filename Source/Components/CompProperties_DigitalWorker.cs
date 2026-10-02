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
        /// <summary>这个代理建筑干哪一类活（Mining / Construction / Cleaning / Growing / PlantCutting）。</summary>
        public WorkTypeDef workType;

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
            if (workType == null)
            {
                yield return parentDef.defName + "：CompProperties_DigitalWorker 必须指定 <workType>。";
            }
            if (skillLevel < 0 || skillLevel > 20)
            {
                yield return parentDef.defName + "：skillLevel 应在 0~20（收到 " + skillLevel + "）。";
            }
            if (workSpeedMult <= 0f)
            {
                yield return parentDef.defName + "：workSpeedMult 必须 > 0（收到 " + workSpeedMult + "）。";
            }
        }
    }
}
