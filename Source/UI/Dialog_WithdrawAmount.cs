using System;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// 取出数量对话框。
    ///
    /// <para><b>4.0 改造</b>：参数从账本键 <c>ItemKey</c> 换成 <c>ThingDef</c>（+ 可用总数）。
    /// 「全放开」之后同一 def 可能有品质/耐久差异，而对话框只需要"取哪种、取多少"；
    /// 具体取哪一件由 <see cref="HaulSourceContents.ExtractDefTo"/> / job 选取最大堆决定。</para>
    ///
    /// <para>两种模式：
    /// <list type="bullet">
    /// <item><b>ITab 模式</b>：<paramref name="core"/> 非空，确认后直接把物品落到核心旁边
    ///   （<c>onConfirm</c> 为 null）。</item>
    /// <item><b>右键模式</b>：确认后回调，由调用方派 job。</item>
    /// </list></para>
    /// </summary>
    public class Dialog_WithdrawAmount : Window
    {
        /// <summary>ITab 模式用；右键模式为 null。</summary>
        private readonly Building_StorageCore core;
        private readonly ThingDef def;
        private readonly int available;
        private readonly int maxCarry;
        private readonly Action<int> onConfirm;
        private string amountStr;
        private int amount;

        public override Vector2 InitialSize => new Vector2(360f, 220f);

        /// <summary>ITab 取出——直接把物品落到核心旁边。</summary>
        public Dialog_WithdrawAmount(Building_StorageCore core, ThingDef def, int available)
        {
            this.core = core;
            this.def = def;
            this.available = available;
            this.maxCarry = 0;
            this.onConfirm = null;
            Init();
        }

        /// <summary>右键菜单取出——确认后回调，由调用方派 Job。</summary>
        public Dialog_WithdrawAmount(ThingDef def, int available, int maxCarry, Action<int> onConfirm)
        {
            this.core = null;
            this.def = def;
            this.available = available;
            this.maxCarry = maxCarry;
            this.onConfirm = onConfirm;
            Init();
        }

        private void Init()
        {
            int limit = maxCarry > 0 ? Math.Min(def.stackLimit, maxCarry) : def.stackLimit;
            this.amount = Math.Min(available, limit);
            if (this.amount <= 0) this.amount = 1;
            this.amountStr = this.amount.ToString();
            this.forcePause = true;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.closeOnClickedOutside = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0, 0, inRect.width, 28f), "DS_WithdrawTitle".Translate(def.LabelCap));
            Widgets.Label(new Rect(0, 32f, inRect.width, 24f), "DS_WithdrawAvailable".Translate(available));
            if (maxCarry > 0)
                Widgets.Label(new Rect(0, 54f, inRect.width, 24f), "DS_WithdrawCarryLimit".Translate(maxCarry));

            float inputY = maxCarry > 0 ? 78f : 64f;
            Widgets.TextFieldNumeric(new Rect(0, inputY, inRect.width, 32f), ref amount, ref amountStr, 1, available);

            float btnY = inRect.height - 38f;
            float btnW = inRect.width / 2f - 8f;

            if (Widgets.ButtonText(new Rect(0, btnY, btnW, 32f), "DS_Confirm".Translate()))
            {
                DoWithdraw();
                Close();
            }
            if (Widgets.ButtonText(new Rect(btnW + 16f, btnY, btnW, 32f), "DS_Cancel".Translate()))
            {
                Close();
            }
        }

        private void DoWithdraw()
        {
            if (amount <= 0) return;

            // 右键模式：回调给调用方派 job
            if (onConfirm != null)
            {
                onConfirm(amount);
                return;
            }

            // ITab 模式：直接从容器取出，落到核心旁边
            if (core == null || !core.Spawned) return;
            Map map = core.Map;
            if (map == null) return;

            if (HaulSourceContents.CountOf(map, def) <= 0)
            {
                Messages.Message("DS_NoCoreItems".Translate(), core, MessageTypeDefOf.RejectInput);
                return;
            }

            int got = HaulSourceContents.ExtractDefTo(def, amount, core.Position, map, forbid: true);
            if (got <= 0)
                Messages.Message("DS_NoSpaceNearCore".Translate(), core, MessageTypeDefOf.RejectInput);
        }
    }
}
