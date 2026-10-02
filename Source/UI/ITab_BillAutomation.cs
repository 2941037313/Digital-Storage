using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// <b>制作代理的检查面板（ITab）</b> —— 现在是**入口 + 摘要**，不再是完整编辑器（用户拍板：
    /// "界面可以大一点，不仅可以从 ITab 打开，底部菜单栏也可以打开"）。
    ///
    /// <para>完整的三栏界面（订单 / 材料 / 进度 / 操作）在 <see cref="Window_CraftAutomation"/> 里，
    /// 这里只留玩家最常用的四件事：打开面板、总开关、超频、耗电与在产摘要。</para>
    /// </summary>
    public class ITab_BillAutomation : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(420f, 320f);

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

            // ① 打开完整面板
            if (Widgets.ButtonText(new Rect(rect.x, rect.y + y, rect.width, 30f), "DS_CA_OpenPanel".Translate()))
            {
                Window_CraftAutomation.OpenOrFocus(comp);
            }
            y += 34f;

            // ② 开关 / 超频
            string state = comp.Enabled ? "DS_BA_On".Translate() : "DS_BA_Off".Translate();
            if (Widgets.ButtonText(new Rect(rect.x, rect.y + y, 150f, 26f), "DS_BA_ToggleState".Translate(state)))
            {
                comp.Enabled = !comp.Enabled;
            }
            if (Widgets.ButtonText(new Rect(rect.x + 156f, rect.y + y, rect.width - 156f, 26f),
                    "DS_BA_Overclock".Translate(comp.OverclockLabel())))
            {
                comp.OverclockTier = (comp.OverclockTier + 1) % 4;
            }
            y += 30f;

            // ③ 耗电 / 统计
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

            // ④ 在产摘要（前几条）
            IList<CraftPlan> plans = comp.PlansForReading;
            if (plans.Count == 0)
            {
                Widgets.Label(new Rect(rect.x, rect.y + y, rect.width, 40f), "DS_BA_Empty".Translate());
                return;
            }

            for (int i = 0; i < plans.Count && i < 6; i++)
            {
                CraftPlan plan = plans[i];
                CraftLine first = (plan.lines.Count > 0) ? plan.lines[0] : null;
                string label = (plan.recipe == null) ? "?" : plan.recipe.LabelCap.ToString();
                string tail;
                if (plan.Done) tail = "DS_BA_PlanFinished".Translate().ToString();
                else if (plan.suspended) tail = "DS_BA_Suspended".Translate().ToString();
                else if (first != null && first.HasWork) tail = (first.Progress01 * 100f).ToString("0") + "%";
                else tail = "DS_BA_NoBench".Translate().ToString();

                Widgets.Label(new Rect(rect.x, rect.y + y, rect.width - 46f, 20f), label);
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(rect.xMax - 42f, rect.y + y, 42f, 20f), tail);
                Text.Anchor = TextAnchor.UpperLeft;
                y += 21f;
            }
        }
    }
}
