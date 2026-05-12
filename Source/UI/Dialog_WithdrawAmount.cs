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
        private string amountStr;
        private int amount;

        public override Vector2 InitialSize => new Vector2(360f, 200f);

        public Dialog_WithdrawAmount(Building_StorageCore core, ItemKey key)
        {
            this.core = core;
            this.key = key;
            this.available = (int)System.Math.Min(core.Ledger.Available(key), int.MaxValue);
            this.amount = System.Math.Min(available, key.def.stackLimit);
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

            Widgets.TextFieldNumeric(new Rect(0, 64f, inRect.width, 32f), ref amount, ref amountStr, 1, available);

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
            if (amount <= 0 || core == null || !core.Spawned) return;
            var thing = core.Ledger.Withdraw(key, amount);
            if (thing == null) return;

            var map = core.Map;
            var center = core.Position;
            if (!GenPlace.TryPlaceThing(thing, center, map, ThingPlaceMode.Near))
            {
                Messages.Message("DS_NoSpaceNearCore".Translate(), core, MessageTypeDefOf.RejectInput);
                // 放不下，还回账本
                core.Ledger.AddRaw(key, thing.stackCount);
                thing.Destroy(DestroyMode.Vanish);
            }
        }
    }
}
