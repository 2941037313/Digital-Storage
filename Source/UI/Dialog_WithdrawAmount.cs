using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// 取出数量对话框：输入数量，确认后从账本扣除并在核心旁生成物品。
    /// </summary>
    public class Dialog_WithdrawAmount : Window
    {
        private readonly Building_StorageCore core;
        private readonly ItemKey key;
        private readonly int available;
        private readonly int maxCarry;
        private string amountStr;
        private int amount;
        private readonly System.Action<int> onConfirm;

        public override Vector2 InitialSize => new Vector2(360f, 220f);

        /// <summary>ITab 取出——直接操作账本生成物品在核心旁。</summary>
        public Dialog_WithdrawAmount(Building_StorageCore core, ItemKey key) : this(core, key, 0, null) { }

        /// <summary>右键菜单取出——确认后回调，由调用方派 Job。</summary>
        public Dialog_WithdrawAmount(Building_StorageCore core, ItemKey key, int maxCarry, System.Action<int> onConfirm)
        {
            this.core = core;
            this.key = key;
            this.maxCarry = maxCarry;
            this.onConfirm = onConfirm;
            this.available = (int)System.Math.Min(core.Ledger.Available(key), int.MaxValue);
            int limit = maxCarry > 0 ? System.Math.Min(key.def.stackLimit, maxCarry) : key.def.stackLimit;
            this.amount = System.Math.Min(available, limit);
            this.amountStr = amount.ToString();
            this.forcePause = true;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.closeOnClickedOutside = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0, 0, inRect.width, 28f), "DS_WithdrawTitle".Translate(key.ToString()));
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

            // 右键菜单模式：回调给调用方
            if (onConfirm != null)
            {
                onConfirm(amount);
                return;
            }

            // ITab 模式：直接在核心旁生成物品
            if (core == null || !core.Spawned) return;
            var thing = core.Ledger.Withdraw(key, amount);
            if (thing == null) return;

            var map = core.Map;
            var center = core.Position;
            if (!GenPlace.TryPlaceThing(thing, center, map, ThingPlaceMode.Near))
            {
                Messages.Message("DS_NoSpaceNearCore".Translate(), core, MessageTypeDefOf.RejectInput);
                core.Ledger.AddRaw(key, thing.stackCount);
                thing.Destroy(DestroyMode.Vanish);
            }
            else
            {
                thing.SetForbidden(true, false);
            }
        }
    }
}
