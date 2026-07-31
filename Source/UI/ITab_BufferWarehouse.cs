using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// 8.1: DSU 式单元 ITab —— 放什么物品(选择) / Min-Max / 清空该单元。
    /// 方框布局（用户 8.1 拍板，抄 OutputPortDsuBuilding 的 OutputSettings 面板）。
    /// </summary>
    public class ITab_BufferWarehouse : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(380f, 190f);
        private string minBuf;
        private string maxBuf;

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

            // 绑定核心
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 22f),
                "DS_BufferBoundTo".Translate(core.LabelCap));
            y += 26f;

            // 放什么物品：名称 + 选择按钮
            var lockedDef = wh.LockedItemDef;
            Rect itemLbl = new Rect(rect.x, rect.y + y, 90f, 24f);
            Widgets.Label(itemLbl, "放什么物品:");
            Rect itemName = new Rect(rect.x + 90f, rect.y + y, rect.width - 172f, 24f);
            string itemNameStr;
            if (lockedDef != null) itemNameStr = lockedDef.LabelCap;
            else itemNameStr = "（无）";
            Widgets.Label(itemName, itemNameStr);
            Rect selectBtn = new Rect(rect.x + rect.width - 74f, rect.y + y, 74f, 24f);
            if (Widgets.ButtonText(selectBtn, "选择"))
                OpenItemPicker(wh, core);
            y += 28f;

            if (lockedDef != null)
            {
                // 图标 + 格上物理数量
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
                Rect countRow = new Rect(rect.x + 34f, rect.y + y, rect.width - 34f, 28f);
                Widgets.Label(countRow, $"x{onHand}");
                y += 32f;
            }
            else
            {
                y += 4f;
            }

            // Min - Max
            Rect minLbl = new Rect(rect.x, rect.y + y, 40f, 24f);
            Widgets.Label(minLbl, "Min");
            Rect minR = new Rect(rect.x + 40f, rect.y + y, 70f, 24f);
            int minVal = comp.Min;
            if (minBuf == null) minBuf = minVal.ToString();
            Widgets.TextFieldNumeric<int>(minR, ref minVal, ref minBuf, 0f, 99999f);
            if (minVal != comp.Min) comp.Min = minVal;

            Rect dash = new Rect(rect.x + 116f, rect.y + y, 20f, 24f);
            Widgets.Label(dash, "-");

            Rect maxLbl = new Rect(rect.x + 136f, rect.y + y, 40f, 24f);
            Widgets.Label(maxLbl, "Max");
            Rect maxR = new Rect(rect.x + 176f, rect.y + y, 70f, 24f);
            int maxVal = comp.Max;
            if (maxBuf == null) maxBuf = maxVal.ToString();
            Widgets.TextFieldNumeric<int>(maxR, ref maxVal, ref maxBuf, 0f, 99999f);
            if (maxVal != comp.Max) comp.Max = maxVal;
            y += 30f;

            // 清空该单元：放什么物品=null，min=max=0
            Rect clearBtn = new Rect(rect.x, rect.y + y, rect.width, 26f);
            if (Widgets.ButtonText(clearBtn, "清空该单元（放什么物品为null，min=max=0）"))
            {
                wh.ClearUnit();
                comp.Min = 0;
                comp.Max = 0;
                minBuf = null;
                maxBuf = null;
            }
        }

        /// <summary>物品选择：核心账本现有 defs（锁定后立即可补货）。</summary>
        private void OpenItemPicker(Building_BufferWarehouse wh, Building_StorageCore core)
        {
            var options = new List<FloatMenuOption>();
            var seen = new HashSet<ThingDef>();
            foreach (var kv in core.Ledger.Stock)
            {
                if (kv.Value <= 0) continue;
                var def = kv.Key.def;
                if (!seen.Add(def)) continue;
                var d = def;
                options.Add(new FloatMenuOption(d.LabelCap, delegate
                {
                    wh.SetLockedItemDef(d);
                }));
            }
            if (options.Count == 0)
                options.Add(new FloatMenuOption("（核心无库存）", null));
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
