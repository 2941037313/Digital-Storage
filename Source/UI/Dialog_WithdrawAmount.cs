using System;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// 取出数量对话框 —— 纯"问个数量，回调给你"的窗口。
    ///
    /// <para><b>4.0 改造</b>：原先它同时承担两种模式（ITab 直接落物品 / 右键派 job），
    /// 参数是账本键 <c>ItemKey</c>。现在数据源是真实容器内容物，而"取出来之后干什么"
    /// 因调用方而异，所以改成**只负责问数量 + 回调**：</para>
    /// <list type="bullet">
    /// <item>ITab 面板：回调里 <c>HaulSourceContents.ExtractMatchingTo</c> 把那一批（def+stuff+品质）
    ///   取到核心旁。</item>
    /// <item>右键菜单：回调里派 <c>DigitalStorage_WithdrawToSpot</c> job。</item>
    /// </list>
    /// </summary>
    public class Dialog_WithdrawAmount : Window
    {
        private readonly string title;
        private readonly int available;
        private readonly int maxCarry;
        private readonly int upperBound;
        private readonly Action<int> onConfirm;
        private string amountStr;
        private int amount;

        public override Vector2 InitialSize => new Vector2(360f, 220f);

        /// <param name="title">标题里显示的名字（通常是物品名）。</param>
        /// <param name="available">可用总数。</param>
        /// <param name="maxCarry">pawn 能搬多少；0 = 不显示也不限制（面板模式）。</param>
        /// <param name="onConfirm">确认回调，参数为数量。</param>
        public Dialog_WithdrawAmount(string title, int available, int maxCarry, Action<int> onConfirm)
        {
            this.title = title;
            this.available = Math.Max(available, 0);
            this.maxCarry = maxCarry;
            this.onConfirm = onConfirm;
            this.upperBound = maxCarry > 0 ? Math.Min(this.available, maxCarry) : this.available;
            if (this.upperBound <= 0) this.upperBound = 1;

            this.amount = this.upperBound;
            this.amountStr = this.amount.ToString();
            this.forcePause = true;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.closeOnClickedOutside = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0, 0, inRect.width, 28f), "DS_WithdrawTitle".Translate(title));
            Widgets.Label(new Rect(0, 32f, inRect.width, 24f), "DS_WithdrawAvailable".Translate(available));
            if (maxCarry > 0)
                Widgets.Label(new Rect(0, 54f, inRect.width, 24f), "DS_WithdrawCarryLimit".Translate(maxCarry));

            float inputY = maxCarry > 0 ? 78f : 64f;
            Widgets.TextFieldNumeric(new Rect(0, inputY, inRect.width, 32f), ref amount, ref amountStr, 1, upperBound);

            float btnY = inRect.height - 38f;
            float btnW = inRect.width / 2f - 8f;

            if (Widgets.ButtonText(new Rect(0, btnY, btnW, 32f), "DS_Confirm".Translate()))
            {
                if (amount > 0) onConfirm?.Invoke(amount);
                Close();
            }
            if (Widgets.ButtonText(new Rect(btnW + 16f, btnY, btnW, 32f), "DS_Cancel".Translate()))
            {
                Close();
            }
        }
    }
}
