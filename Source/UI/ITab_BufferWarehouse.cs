using DigitalStorage.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// 5c: 极简单行 ITab —— 物品图标 + 名称 + 物理数量 + threshold 输入 + 解锁按钮。
    /// </summary>
    public class ITab_BufferWarehouse : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(380f, 180f);
        private string thresholdBuf;

        private Building_BufferWarehouse Warehouse => SelThing as Building_BufferWarehouse;

        public ITab_BufferWarehouse()
        {
            size = WinSize;
            labelKey = "DS_TabBufferWarehouse";
        }

        public override bool IsVisible => Warehouse != null && Warehouse.BoundCore != null;

        protected override void FillTab()
        {
            var wh = Warehouse;
            if (wh == null) return;

            var comp = wh.GetComp<CompBufferWarehouse>();
            var core = wh.BoundCore;
            if (comp == null || core == null) return;

            Rect rect = new Rect(0f, 0f, WinSize.x, WinSize.y).ContractedBy(12f);
            Text.Font = GameFont.Small;
            float y = 0f;

            // 绑定核心信息
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 22f),
                "DS_BufferBoundTo".Translate(core.LabelCap));
            y += 26f;

            var lockedDef = wh.LockedItemDef;
            if (lockedDef != null)
            {
                // 物品图标 + 名称 + 数量（单行）
                Widgets.ThingIcon(new Rect(rect.x, rect.y + y, 28f, 28f), lockedDef);

                long onHand = 0;
                var slot = wh.GetSlotGroup();
                if (slot != null)
                {
                    foreach (var t in slot.HeldThings)
                    {
                        if (t.Destroyed) continue;
                        if (t.def == lockedDef) onHand += t.stackCount;
                    }
                }

                Rect itemRow = new Rect(rect.x + 34f, rect.y + y, rect.width - 34f, 28f);
                Widgets.Label(itemRow, $"<b>{lockedDef.LabelCap}</b>   x{onHand}");
                y += 34f;

                // Threshold 输入框 + 解锁按钮（同一行）
                Rect threshLbl = new Rect(rect.x, rect.y + y, 80f, 24f);
                Widgets.Label(threshLbl, "保留数量" + ":");
                Rect threshR = new Rect(rect.x + 84f, rect.y + y, 60f, 24f);
                int tval = comp.Threshold;
                if (thresholdBuf == null) thresholdBuf = tval.ToString();
                Widgets.TextFieldNumeric<int>(threshR, ref tval, ref thresholdBuf, 0f, 99999f);
                if (tval != comp.Threshold)
                    comp.Threshold = tval;

                Rect unlockR = new Rect(rect.x + 160f, rect.y + y, 80f, 24f);
                if (Widgets.ButtonText(unlockR, "Unlock"))
                {
                    wh.Unlock();
                    thresholdBuf = null;
                }
                y += 28f;
            }
            else
            {
                // 空仓：等待补货
                Rect emptyR = new Rect(rect.x, rect.y + y, rect.width, 24f);
                Widgets.Label(emptyR, "DS_NoCoreItems".Translate());
                y += 28f;

                // 依然显示 threshold 输入（预设置）
                int tval = comp.Threshold;
                if (thresholdBuf == null) thresholdBuf = tval.ToString();
                Rect threshLbl2 = new Rect(rect.x, rect.y + y, 80f, 24f);
                Widgets.Label(threshLbl2, "保留数量" + ":");
                Rect threshR2 = new Rect(rect.x + 84f, rect.y + y, 60f, 24f);
                Widgets.TextFieldNumeric<int>(threshR2, ref tval, ref thresholdBuf, 0f, 99999f);
                if (tval != comp.Threshold)
                    comp.Threshold = tval;
            }
        }
    }
}
