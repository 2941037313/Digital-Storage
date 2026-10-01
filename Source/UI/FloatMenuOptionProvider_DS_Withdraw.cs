using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.UI
{
    /// <summary>
    /// 阶段 4.4：右键地图空格 → "从数字存储取出…" → 选物品+数量 → 派 JobDriver_DS_Withdraw。
    /// 原版 FloatMenuOptionProvider 扩展点，零 Harmony。
    ///
    /// <para><b>4.0</b>：数据源从账本换成容器内容物；去掉芯片/代理点分支（纯轮椅）；
    /// job 直接带上"要取的那件 Thing"。</para>
    ///
    /// <para><b>取舍</b>：菜单按 <c>ThingDef</c> 归组（同类合并显示总数），
    /// 真正取哪一件在派 job 时选"最大的一堆"。「全放开」之后同一 def 可能有品质/耐久差异，
    /// 按 def 归组就分不出来 —— 要精确挑某一件请用核心的 ITab 面板。</para>
    /// </summary>
    public class FloatMenuOptionProvider_DS_Withdraw : FloatMenuOptionProvider
    {
        protected override bool Drafted => false;
        protected override bool Undrafted => true;
        protected override bool Multiselect => false;

        protected override FloatMenuOption GetSingleOption(FloatMenuContext context)
        {
            Pawn pawn = context.FirstSelectedPawn;
            if (pawn?.Map == null) return null;

            // 没有可用（已通电）核心就不出这个菜单项
            if (!CoreFinder.AnyUsableCore(pawn)) return null;

            Map map = pawn.Map;
            var availableByDef = new Dictionary<ThingDef, int>();
            var all = new List<Thing>();
            HaulSourceContents.GatherAll(map, all);
            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                if (t?.def == null || t.def.category != ThingCategory.Item) continue;
                int cur;
                availableByDef.TryGetValue(t.def, out cur);
                availableByDef[t.def] = cur + t.stackCount;
            }
            if (availableByDef.Count == 0) return null;

            var defs = new List<ThingDef>(availableByDef.Keys);
            defs.Sort((a, b) => string.Compare(a.LabelCap, b.LabelCap, System.StringComparison.Ordinal));

            return new FloatMenuOption("DS_WithdrawToSpot".Translate(), () =>
            {
                var subOptions = new List<FloatMenuOption>();
                for (int i = 0; i < defs.Count; i++)
                {
                    ThingDef def = defs[i];
                    int total = availableByDef[def];
                    string label = "DS_WithdrawItemLabel".Translate(def.LabelCap, total);
                    subOptions.Add(new FloatMenuOption(label, () =>
                    {
                        int maxCarry = pawn.carryTracker?.AvailableStackSpace(def) ?? 0;
                        Find.WindowStack.Add(new Dialog_WithdrawAmount(def, total, maxCarry, amount =>
                        {
                            // 取同 def 里最大的一堆（job 只认一件 Thing；凑不齐由后续 job 接力）
                            Thing src = HaulSourceContents.FindBest(map, t => t.stackCount, t => t.def == def);
                            if (src == null) return;

                            var job = JobMaker.MakeJob(
                                DigitalStorage_JobDefOf.DigitalStorage_WithdrawToSpot, context.ClickedCell);
                            job.SetTarget(TargetIndex.B, src);
                            job.SetTarget(TargetIndex.C, src.ParentHolder as Thing);
                            job.count = amount;
                            pawn.jobs.TryTakeOrderedJob(job, JobTag.MiscWork);
                        }));
                    }));
                }
                if (subOptions.Count > 0)
                    Find.WindowStack.Add(new FloatMenu(subOptions));
            });
        }
    }
}
