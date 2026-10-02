using System;
using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// <b>制作代理的统一面板</b>（用户拍板："和建筑同级的一个统一面板"）。
    ///
    /// <para>这**不是**只读看板：它是这台建筑自己的 bill 列表 —— 加配方、设次数/无限、挂起、删除、
    /// 调顺序，全在这里；工作台只需要"存在"（解锁配方），**不需要在上面建任何 bill**。</para>
    ///
    /// <para>每条配方一行：模式 / +1 / +10 / 「在产 x/y 台」/ 进度或阻塞原因。
    /// 剩余条数、挂起状态由 <see cref="CraftPlan"/> 自己存盘。</para>
    /// </summary>
    public class ITab_BillAutomation : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(500f, 540f);
        private Vector2 scroll;

        private CompBillAutomation Comp
        {
            get { return (SelThing == null) ? null : SelThing.TryGetComp<CompBillAutomation>(); }
        }

        public ITab_BillAutomation()
        {
            this.size = WinSize;
            this.labelKey = "DS_TabBillAutomation";
        }

        public override bool IsVisible
        {
            get { return Comp != null; }
        }

        protected override void FillTab()
        {
            CompBillAutomation comp = Comp;
            if (comp == null) return;

            Rect rect = new Rect(0f, 0f, WinSize.x, WinSize.y).ContractedBy(10f);
            Text.Font = GameFont.Small;
            float y = 0f;

            // ① 开关 / 超频 / 添加配方
            string state = comp.Enabled ? "DS_BA_On".Translate() : "DS_BA_Off".Translate();
            if (Widgets.ButtonText(new Rect(rect.x, rect.y + y, 132f, 26f), "DS_BA_ToggleState".Translate(state)))
            {
                comp.Enabled = !comp.Enabled;
            }
            if (Widgets.ButtonText(new Rect(rect.x + 136f, rect.y + y, 150f, 26f), "DS_BA_Overclock".Translate(comp.OverclockLabel())))
            {
                comp.OverclockTier = (comp.OverclockTier + 1) % 4;
            }
            if (Widgets.ButtonText(new Rect(rect.x + 290f, rect.y + y, rect.width - 290f, 26f), "DS_BA_Add".Translate()))
            {
                Find.WindowStack.Add(new Dialog_DS_AddCraft(comp));
            }
            y += 30f;

            // ② 耗电明细（自身 + Σ台子，再乘超频倍率）+ 统计
            Widgets.Label(new Rect(rect.x, rect.y + y, rect.width, 22f),
                "DS_BA_Watts".Translate(
                    comp.CurrentWatts.ToString("#####0"),
                    comp.Props.basePowerWatts.ToString("#####0"),
                    comp.BenchWatts().ToString("#####0"),
                    comp.OverclockPowerMult.ToString("0")));
            y += 22f;
            Widgets.Label(new Rect(rect.x, rect.y + y, rect.width, 22f),
                "DS_BA_Stats".Translate(comp.CompletedCount, comp.DroppedCount, comp.PlansForReading.Count));
            y += 26f;

            IList<CraftPlan> plans = comp.PlansForReading;
            if (plans.Count == 0)
            {
                Widgets.Label(new Rect(rect.x, rect.y + y, rect.width, 44f), "DS_BA_Empty".Translate());
                return;
            }

            // ③ 配方列表
            const float rowH = 50f;
            Rect outer = new Rect(rect.x, rect.y + y, rect.width, rect.height - y);
            Rect inner = new Rect(0f, 0f, outer.width - 16f, Math.Max(outer.height, plans.Count * rowH));
            Widgets.BeginScrollView(outer, ref scroll, inner);

            float ly = 0f;
            for (int i = 0; i < plans.Count; i++)
            {
                CraftPlan plan = plans[i];
                Rect row = new Rect(0f, ly, inner.width, rowH - 4f);
                if (i % 2 == 0) Widgets.DrawAltRect(row);
                DrawPlanRow(comp, plan, row);
                ly += rowH;
            }

            Widgets.EndScrollView();
        }

        private void DrawPlanRow(CompBillAutomation comp, CraftPlan plan, Rect row)
        {
            float x = row.x + 2f;

            // ---- 上排：排序 / 配方名 / 挂起 / 删除 ----
            if (Widgets.ButtonText(new Rect(x, row.y + 2f, 20f, 20f), "↑"))
            {
                comp.MovePlan(plan, -1);
                return;
            }
            x += 22f;
            if (Widgets.ButtonText(new Rect(x, row.y + 2f, 20f, 20f), "↓"))
            {
                comp.MovePlan(plan, 1);
                return;
            }
            x += 24f;

            string label = (plan.recipe == null) ? "?" : plan.recipe.LabelCap.ToString();
            Widgets.Label(new Rect(x, row.y + 3f, row.xMax - x - 92f, 20f), label);

            float rx = row.xMax - 86f;
            if (Widgets.ButtonText(new Rect(rx, row.y + 2f, 60f, 20f),
                    plan.suspended ? "DS_BA_Resume".Translate() : "DS_BA_Suspend".Translate()))
            {
                plan.suspended = !plan.suspended;
            }
            if (Widgets.ButtonText(new Rect(rx + 62f, row.y + 2f, 20f, 20f), "✕"))
            {
                comp.RemovePlan(plan);
                return;
            }

            // ---- 下排：模式 / +1 / +10 / 在产台数 / 进度或原因 ----
            x = row.x + 2f;
            string modeLabel = (plan.mode == CraftPlan.ModeCount)
                ? "DS_BA_Remain".Translate(plan.remaining).ToString()
                : "DS_BA_Forever".Translate().ToString();
            if (Widgets.ButtonText(new Rect(x, row.y + 24f, 78f, 20f), modeLabel))
            {
                if (plan.mode == CraftPlan.ModeCount) plan.SetForever();
                else plan.AddCount(1);
            }
            x += 80f;
            if (Widgets.ButtonText(new Rect(x, row.y + 24f, 28f, 20f), "+1")) plan.AddCount(1);
            x += 30f;
            if (Widgets.ButtonText(new Rect(x, row.y + 24f, 34f, 20f), "+10")) plan.AddCount(10);
            x += 38f;

            Widgets.Label(new Rect(x, row.y + 25f, 100f, 20f),
                "DS_BA_Lines".Translate(plan.lines.Count, comp.UsableBenchCountFor(plan.recipe)));
            x += 102f;

            Rect barRect = new Rect(x, row.y + 26f, Math.Max(40f, row.xMax - x - 4f), 16f);
            CraftLine first = (plan.lines.Count > 0) ? plan.lines[0] : null;
            bool working = first != null && first.HasWork;
            // FillableBar 自己不 clamp（内部就一句 rect.width *= fillPercent），自己夹。
            Widgets.FillableBar(barRect, working ? Mathf.Clamp01(first.Progress01) : 0f);

            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(barRect, StatusText(comp, plan, first, working));
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private static string StatusText(CompBillAutomation comp, CraftPlan plan, CraftLine first, bool working)
        {
            if (plan.Done) return "DS_BA_PlanFinished".Translate();
            if (plan.suspended) return "DS_BA_Suspended".Translate();
            if (plan.lines.Count == 0) return "DS_BA_NoBench".Translate();
            if (working) return first.Progress01.ToStringPercent();
            if (first != null && !first.BlockKey.NullOrEmpty()) return first.BlockKey.Translate();
            return "DS_BA_NoBill".Translate();
        }
    }
}
