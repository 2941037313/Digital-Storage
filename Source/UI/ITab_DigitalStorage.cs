using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// 核心的虚拟库存 ITab。
    /// 6 组折叠展示，每组列出条目 + 数量，每行有"取出"按钮。
    /// </summary>
    public class ITab_DigitalStorage : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(460f, 540f);
        private readonly bool[] expanded = new bool[6];
        private Vector2 scroll;
        private string search = "";

        private Building_StorageCore Core => SelThing as Building_StorageCore;

        public ITab_DigitalStorage()
        {
            this.size = WinSize;
            this.labelKey = "DS_TabDigitalStorage";
        }

        public override bool IsVisible => Core != null;

        protected override void FillTab()
        {
            var core = Core;
            if (core == null) return;

            Rect rect = new Rect(0f, 0f, WinSize.x, WinSize.y).ContractedBy(10f);
            Text.Font = GameFont.Small;
            float y = 0f;

            // 顶部：容量条
            Rect barRect = new Rect(rect.x, rect.y + y, rect.width, 22f);
            int used = core.Ledger.UsedCapacity();
            int cap = core.GetCapacity();
            Widgets.FillableBar(barRect, cap > 0 ? (float)used / cap : 0f);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(barRect, "DS_CapacityBar".Translate(used, cap));
            Text.Anchor = TextAnchor.UpperLeft;
            y += 26f;

            // 搬运优先级
            Rect prioLabel = new Rect(rect.x, rect.y + y, 60f, 22f);
            Widgets.Label(prioLabel, "DS_Priority".Translate() + ":");
            Rect prioRect = new Rect(rect.x + 62f, rect.y + y, 140f, 22f);
            if (Widgets.ButtonText(prioRect, PriorityLabel(core.storagePriority)))
            {
                var options = new List<FloatMenuOption>();
                foreach (StoragePriority p in System.Enum.GetValues(typeof(StoragePriority)))
                {
                    var priority = p;
                    if (priority == StoragePriority.Unstored) continue;
                    options.Add(new FloatMenuOption(PriorityLabel(priority), delegate
                    {
                        core.storagePriority = priority;
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            y += 26f;

            // 搜索框
            Rect searchRect = new Rect(rect.x, rect.y + y, rect.width, 24f);
            search = Widgets.TextField(searchRect, search);
            y += 30f;

            // 列表区
            Rect listOuter = new Rect(rect.x, rect.y + y, rect.width, rect.height - y);
            float listHeight = CalcListHeight(core);
            Rect listInner = new Rect(0, 0, listOuter.width - 16f, listHeight);
            Widgets.BeginScrollView(listOuter, ref scroll, listInner);

            float ly = 0f;
            for (int gi = 0; gi < 6; gi++)
            {
                var group = (ItemGroup)gi;
                long gcount = core.Ledger.GroupCount(group);
                int gkinds = core.Ledger.GroupKinds(group);
                if (gkinds == 0 && string.IsNullOrEmpty(search)) continue; // 空组不显示（无搜索时）

                // 分组头
                Rect header = new Rect(0, ly, listInner.width, 26f);
                if (Mouse.IsOver(header)) Widgets.DrawHighlight(header);
                string arrow = expanded[gi] ? "▼" : "▶";
                string label = $"{arrow} {ItemGrouping.LabelKeyOf(group).Translate()}   x{gcount}   ({gkinds} {"DS_Kinds".Translate()})";
                Widgets.Label(header, label);
                if (Widgets.ButtonInvisible(header)) expanded[gi] = !expanded[gi];
                ly += 28f;

                if (!expanded[gi]) continue;

                // 组内条目
                foreach (var kv in core.Ledger.Stock)
                {
                    if (kv.Value <= 0) continue;
                    if (ItemGrouping.GroupOf(kv.Key.def) != group) continue;

                    string itemLabel = kv.Key.ToString();
                    if (!string.IsNullOrEmpty(search) &&
                        itemLabel.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    Rect row = new Rect(12f, ly, listInner.width - 12f, 24f);
                    if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

                    // 图标
                    Rect iconR = new Rect(row.x, row.y, 22f, 22f);
                    Widgets.ThingIcon(iconR, kv.Key.def, kv.Key.stuff);

                    // 标签
                    Rect labelR = new Rect(row.x + 26f, row.y, row.width - 130f, 24f);
                    long avail = core.Ledger.Available(kv.Key);
                    long reserved = kv.Value - avail;
                    string text = reserved > 0
                        ? $"{itemLabel}   x{kv.Value}  ({"DS_Reserved".Translate(reserved)})"
                        : $"{itemLabel}   x{kv.Value}";
                    Widgets.Label(labelR, text);

                    // 取出按钮
                    Rect btnR = new Rect(row.xMax - 100f, row.y, 100f, 22f);
                    if (Widgets.ButtonText(btnR, "DS_WithdrawBtn".Translate()))
                    {
                        Find.WindowStack.Add(new Dialog_WithdrawAmount(core, kv.Key));
                    }

                    ly += 26f;
                }
            }

            Widgets.EndScrollView();
        }

        private static string PriorityLabel(StoragePriority p)
        {
            // 键名必须与 vanilla Enums.xml 一致(StoragePriorityXxx,见 StoragePriorityHelper),
            // 错误键名(PriorityXxx)会被 Translate() 当缺失键 → 显示乱码(泰南语)
            switch (p)
            {
                case StoragePriority.Low: return "StoragePriorityLow".Translate();
                case StoragePriority.Normal: return "StoragePriorityNormal".Translate();
                case StoragePriority.Preferred: return "StoragePriorityPreferred".Translate();
                case StoragePriority.Important: return "StoragePriorityImportant".Translate();
                case StoragePriority.Critical: return "StoragePriorityCritical".Translate();
                default: return p.ToString();
            }
        }

        private float CalcListHeight(Building_StorageCore core)
        {
            float h = 0f;
            for (int gi = 0; gi < 6; gi++)
            {
                var group = (ItemGroup)gi;
                int gkinds = core.Ledger.GroupKinds(group);
                if (gkinds == 0 && string.IsNullOrEmpty(search)) continue;
                h += 28f;
                if (!expanded[gi]) continue;
                foreach (var kv in core.Ledger.Stock)
                {
                    if (kv.Value <= 0) continue;
                    if (ItemGrouping.GroupOf(kv.Key.def) != group) continue;
                    if (!string.IsNullOrEmpty(search) &&
                        kv.Key.ToString().IndexOf(search, System.StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    h += 26f;
                }
            }
            return h;
        }
    }
}
