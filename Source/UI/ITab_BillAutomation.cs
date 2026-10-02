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
    /// <para>一台制作代理一个面板，列出**范围内所有正在做 bill 的工作台**（一个槽位一行）：
    /// 台子名 · 正在做的 bill · 进度条 · 没在干活时的原因。用户在原版 bill 界面删/挂起/排序/限技能，
    /// 这里会**跟着变** —— 面板本身没有第二套配方开关（那会与工作台的 bill 栈语义打架）。</para>
    ///
    /// <para>形态与 <see cref="ITab_DigitalStorage"/> 保持同族：固定窗口、滚动列表、偶数行底色。</para>
    /// </summary>
    public class ITab_BillAutomation : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(480f, 460f);
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

            // ① 总开关 + 超频档位
            Rect toggleRect = new Rect(rect.x, rect.y + y, 150f, 26f);
            string state = comp.Enabled ? "DS_BA_On".Translate() : "DS_BA_Off".Translate();
            if (Widgets.ButtonText(toggleRect, "DS_BA_ToggleState".Translate(state)))
            {
                comp.Enabled = !comp.Enabled;
            }

            Rect ocRect = new Rect(rect.x + 156f, rect.y + y, rect.width - 156f, 26f);
            if (Widgets.ButtonText(ocRect, "DS_BA_Overclock".Translate(comp.OverclockLabel())))
            {
                comp.OverclockTier = (comp.OverclockTier + 1) % 4;
            }
            y += 30f;

            // ② 耗电明细：自身 + Σ台子，再乘超频倍率（用户拍板的口径）
            Widgets.Label(new Rect(rect.x, rect.y + y, rect.width, 22f),
                "DS_BA_Watts".Translate(
                    comp.CurrentWatts.ToString("#####0"),
                    comp.Props.basePowerWatts.ToString("#####0"),
                    comp.BenchWatts().ToString("#####0"),
                    comp.OverclockPowerMult.ToString("0")));
            y += 24f;

            // ③ 统计
            Widgets.Label(new Rect(rect.x, rect.y + y, rect.width, 22f),
                "DS_BA_Stats".Translate(comp.CompletedCount, comp.DroppedCount, comp.SlotsForReading.Count));
            y += 26f;

            IList<BillSlot> slots = comp.SlotsForReading;
            if (slots.Count == 0)
            {
                Widgets.Label(new Rect(rect.x, rect.y + y, rect.width, 40f), "DS_BA_NoBench".Translate());
                return;
            }

            // ④ 槽位列表
            const float rowH = 46f;
            Rect listOuter = new Rect(rect.x, rect.y + y, rect.width, rect.height - y);
            Rect listInner = new Rect(0f, 0f, listOuter.width - 16f, Math.Max(listOuter.height, slots.Count * rowH));
            Widgets.BeginScrollView(listOuter, ref scroll, listInner);

            float ly = 0f;
            for (int i = 0; i < slots.Count; i++)
            {
                BillSlot s = slots[i];
                Rect row = new Rect(0f, ly, listInner.width, rowH - 4f);
                if (i % 2 == 0) Widgets.DrawAltRect(row);

                string bench = (s.Bench == null || s.Bench.Destroyed) ? "?" : s.Bench.LabelShort;
                string title = (s.HasWork && s.Bill != null) ? s.Bill.LabelCap : "DS_BA_Idle".Translate().ToString();

                Rect labelRect = new Rect(row.x + 4f, row.y + 2f, row.width - 8f, 20f);
                Widgets.Label(labelRect, bench + " · " + title);

                Rect barRect = new Rect(row.x + 4f, row.y + 23f, row.width - 8f, 16f);
                // FillableBar 自己不 clamp（内部就一句 rect.width *= fillPercent），自己夹。
                Widgets.FillableBar(barRect, s.HasWork ? Mathf.Clamp01(s.Progress01) : 0f);
                Text.Anchor = TextAnchor.MiddleCenter;
                if (s.HasWork)
                {
                    Widgets.Label(barRect, s.Progress01.ToStringPercent());
                }
                else
                {
                    Widgets.Label(barRect, (s.BlockKey.NullOrEmpty() ? "DS_BA_NoBill" : s.BlockKey).Translate());
                }
                Text.Anchor = TextAnchor.UpperLeft;

                ly += rowH;
            }

            Widgets.EndScrollView();
        }
    }
}
