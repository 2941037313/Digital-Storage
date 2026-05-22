using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// 7e: 缓冲仓库物理库存 ITab。
    /// 合并核心账本 + SlotGroup 物理库存，显示所有可配置的物品。
    /// 每行数字输入框设置保留阈值，有取出按钮。
    /// </summary>
    public class ITab_BufferWarehouse : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(480f, 540f);
        private readonly bool[] expanded = new bool[6];
        private Vector2 scroll;
        private string search = "";
        private Dictionary<ItemKey, string> thresholdBuffers = new Dictionary<ItemKey, string>();

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

            var slot = wh.GetSlotGroup();
            var comp = wh.GetComp<CompBufferWarehouse>();
            var core = wh.BoundCore;
            if (comp == null || core == null) return;

            Rect rect = new Rect(0f, 0f, WinSize.x, WinSize.y).ContractedBy(10f);
            Text.Font = GameFont.Small;
            float y = 0f;

            // 绑定核心
            Rect infoRect = new Rect(rect.x, rect.y + y, rect.width, 22f);
            Widgets.Label(infoRect, "DS_BufferBoundTo".Translate(core.LabelCap));
            y += 24f;

            // 搜索框
            Rect searchRect = new Rect(rect.x, rect.y + y, rect.width, 24f);
            search = Widgets.TextField(searchRect, search);
            y += 30f;

            // 合并数据源：物理库存 + 核心账本
            var groups = BuildMergedGroups(slot, core, comp);
            Rect listOuter = new Rect(rect.x, rect.y + y, rect.width, rect.height - y);
            float listHeight = CalcListHeight(groups);
            Rect listInner = new Rect(0, 0, listOuter.width - 16f, listHeight);
            Widgets.BeginScrollView(listOuter, ref scroll, listInner);

            float ly = 0f;
            for (int gi = 0; gi < 6; gi++)
            {
                var group = (ItemGroup)gi;
                if (!groups.TryGetValue(group, out var items) || items.Count == 0)
                {
                    if (string.IsNullOrEmpty(search)) continue;
                }

                long gTotal = 0;
                int gKinds = 0;
                if (items != null)
                {
                    foreach (var e in items)
                    {
                        if (!MatchSearch(e.key)) continue;
                        gTotal += e.total;
                        gKinds++;
                    }
                }
                if (gKinds == 0 && string.IsNullOrEmpty(search)) continue;

                Rect header = new Rect(0, ly, listInner.width, 26f);
                if (Mouse.IsOver(header)) Widgets.DrawHighlight(header);
                string arrow = expanded[gi] ? "▼" : "▶";
                string label = $"{arrow} {ItemGrouping.LabelKeyOf(group).Translate()}   x{gTotal}   ({gKinds} {"DS_Kinds".Translate()})";
                Widgets.Label(header, label);
                if (Widgets.ButtonInvisible(header)) expanded[gi] = !expanded[gi];
                ly += 28f;

                if (!expanded[gi] || items == null) continue;

                foreach (var entry in items)
                {
                    if (!MatchSearch(entry.key)) continue;

                    Rect row = new Rect(12f, ly, listInner.width - 12f, 24f);
                    if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

                    // 图标
                    Widgets.ThingIcon(new Rect(row.x, row.y, 22f, 22f), entry.key.def, entry.key.stuff);

                    // 标签 + 物理数量/账本数量
                    Rect labelR = new Rect(row.x + 26f, row.y, row.width - 270f, 24f);
                    string lbl = entry.total > 0
                        ? $"{entry.key}   [物理 x{entry.total}]   (账本 x{entry.ledgerTotal})"
                        : $"{entry.key}   (账本 x{entry.ledgerTotal})";
                    Widgets.Label(labelR, lbl);

                    // 归零按钮
                    Rect zeroR = new Rect(row.xMax - 24f, row.y, 24f, 22f);
                    if (Widgets.ButtonText(zeroR, "0"))
                    {
                        comp.SetThreshold(entry.key, 0);
                        thresholdBuffers[entry.key] = "0";
                    }

                    // 阈值输入框
                    int threshold = comp.GetThreshold(entry.key);
                    if (!thresholdBuffers.TryGetValue(entry.key, out var buf))
                        buf = thresholdBuffers[entry.key] = threshold.ToString();
                    Rect threshR = new Rect(row.xMax - 88f, row.y, 56f, 22f);
                    Widgets.TextFieldNumeric<int>(threshR, ref threshold, ref buf, 0f, 99999f);
                    if (threshold != comp.GetThreshold(entry.key))
                    {
                        comp.SetThreshold(entry.key, threshold);
                        thresholdBuffers[entry.key] = threshold.ToString();
                    }

                    // 取出按钮
                    Rect btnR = new Rect(row.xMax - 172f, row.y, 80f, 22f);
                    if (entry.total > 0)
                    {
                        if (Widgets.ButtonText(btnR, "DS_WithdrawBtn".Translate()))
                        {
                            Thing toTake = entry.things?.FirstOrDefault(t => !t.Destroyed);
                            if (toTake != null)
                            {
                                int take = System.Math.Min(75, toTake.stackCount);
                                Thing taken = toTake.SplitOff(take);
                                if (taken != null)
                                    GenPlace.TryPlaceThing(taken, wh.Position, wh.Map, ThingPlaceMode.Near);
                            }
                        }
                    }

                    ly += 26f;
                }
            }

            Widgets.EndScrollView();
        }

        private struct BufferEntry
        {
            public ItemKey key;
            public long total;        // 物理库存总计
            public long ledgerTotal;  // 核心账本总计
            public List<Thing> things;
        }

        private Dictionary<ItemGroup, List<BufferEntry>> BuildMergedGroups(SlotGroup slot, Building_StorageCore core, CompBufferWarehouse comp)
        {
            var merged = new Dictionary<ItemKey, BufferEntry>();

            // 账本物品（全部显示）
            foreach (var kv in core.Ledger.Stock)
            {
                if (kv.Value <= 0) continue;
                merged[kv.Key] = new BufferEntry
                {
                    key = kv.Key,
                    total = 0,
                    ledgerTotal = kv.Value,
                    things = new List<Thing>()
                };
            }

            // 物理库存（合并/叠加）
            if (slot != null)
            {
                foreach (var t in slot.HeldThings)
                {
                    if (t.Destroyed) continue;
                    var key = ItemKey.Of(t);
                    if (merged.TryGetValue(key, out var entry))
                    {
                        entry.total += t.stackCount;
                        entry.things.Add(t);
                        merged[key] = entry;
                    }
                    else
                    {
                        merged[key] = new BufferEntry
                        {
                            key = key,
                            total = t.stackCount,
                            ledgerTotal = 0,
                            things = new List<Thing> { t }
                        };
                    }
                }
            }

            var result = new Dictionary<ItemGroup, List<BufferEntry>>();
            foreach (var kv in merged)
            {
                var g = ItemGrouping.GroupOf(kv.Key.def);
                if (!result.TryGetValue(g, out var list))
                {
                    list = new List<BufferEntry>();
                    result[g] = list;
                }
                list.Add(kv.Value);
            }
            return result;
        }

        private bool MatchSearch(ItemKey key)
        {
            if (string.IsNullOrEmpty(search)) return true;
            return key.ToString().IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private float CalcListHeight(Dictionary<ItemGroup, List<BufferEntry>> groups)
        {
            float h = 0f;
            for (int gi = 0; gi < 6; gi++)
            {
                var group = (ItemGroup)gi;
                h += 28f;
                if (!expanded[gi]) continue;
                if (groups.TryGetValue(group, out var items))
                {
                    foreach (var e in items)
                    {
                        if (!MatchSearch(e.key)) continue;
                        h += 26f;
                    }
                }
            }
            return h;
        }
    }
}
