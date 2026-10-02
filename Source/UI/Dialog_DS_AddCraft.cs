using System;
using System.Collections.Generic;
using System.Text;
using DigitalStorage.AI;
using DigitalStorage.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// <b>"添加配方"窗口</b>：列出 13×13 范围内工作台能做、且研究已解锁的配方，点一下加进面板。
    ///
    /// <para>这就是用户说的"打开菜单，列出能做的 bill" —— 列表来自
    /// <see cref="CraftUnlocks"/>（台子类型 → 配方表），**不需要工作台上真的有 bill**。</para>
    ///
    /// <para>资质不够的配方照样显示（标红），让玩家知道"是这台机器等级不够"而不是"配方不存在"。</para>
    /// </summary>
    public class Dialog_DS_AddCraft : Window
    {
        private readonly CompBillAutomation comp;
        private readonly List<RecipeDef> shown = new List<RecipeDef>();
        private string search = "";
        private Vector2 scroll;

        public Dialog_DS_AddCraft(CompBillAutomation comp)
        {
            this.comp = comp;
            this.doCloseX = true;
            this.closeOnClickedOutside = true;
            this.absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(640f, 640f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 30f), "DS_BA_AddTitle".Translate());
            Text.Font = GameFont.Small;

            search = Widgets.TextField(new Rect(0f, 32f, inRect.width, 26f), search);

            Rect listRect = new Rect(0f, 64f, inRect.width, inRect.height - 64f - 40f);
            BuildList();

            if (shown.Count == 0)
            {
                Widgets.Label(listRect, "DS_BA_NoRecipeAvailable".Translate());
            }
            else
            {
                const float rowH = 42f;
                Rect inner = new Rect(0f, 0f, listRect.width - 16f, Math.Max(listRect.height, shown.Count * rowH));
                Widgets.BeginScrollView(listRect, ref scroll, inner);

                float y = 0f;
                for (int i = 0; i < shown.Count; i++)
                {
                    RecipeDef r = shown[i];
                    Rect row = new Rect(0f, y, inner.width, rowH - 2f);
                    if (i % 2 == 0) Widgets.DrawAltRect(row);

                    bool added = comp.HasPlan(r);
                    bool skillOk = SkillOk(r);

                    Widgets.Label(new Rect(row.x + 4f, row.y + 2f, 240f, 18f), r.LabelCap);
                    Widgets.Label(new Rect(row.x + 4f, row.y + 20f, row.width - 90f, 18f), IngredientsLine(r));

                    string right = added
                        ? "DS_BA_AlreadyAdded".Translate()
                        : (skillOk ? "DS_BA_ClickToAdd".Translate() : "DS_BA_SkillShort".Translate());
                    Text.Anchor = TextAnchor.MiddleRight;
                    Widgets.Label(new Rect(row.xMax - 200f, row.y + 11f, 196f, 18f), right);
                    Text.Anchor = TextAnchor.UpperLeft;

                    if (!added && Mouse.IsOver(row) && !skillOk)
                    {
                        TooltipHandler.TipRegion(row, "DS_BA_NeedSkill".Translate(SkillLine(r)));
                    }

                    if (!added && Widgets.ButtonInvisible(row))
                    {
                        comp.AddPlan(r);
                    }
                    y += rowH;
                }

                Widgets.EndScrollView();
            }

            if (Widgets.ButtonText(new Rect(inRect.center.x - 60f, inRect.yMax - 32f, 120f, 28f), "DS_BA_Close".Translate()))
            {
                Close();
            }
        }

        private void BuildList()
        {
            shown.Clear();
            IList<RecipeDef> all = comp.UnlockedRecipes;
            for (int i = 0; i < all.Count; i++)
            {
                RecipeDef r = all[i];
                if (r == null) continue;
                if (!string.IsNullOrEmpty(search))
                {
                    string label = r.LabelCap;
                    bool hit = (label != null && label.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        || r.defName.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit) continue;
                }
                shown.Add(r);
            }
        }

        private static string IngredientsLine(RecipeDef r)
        {
            if (r.ingredients == null || r.ingredients.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < r.ingredients.Count; i++)
            {
                if (i > 0) sb.Append(" + ");
                sb.Append(r.ingredients[i].Summary);
            }
            return sb.ToString();
        }

        private static string SkillLine(RecipeDef r)
        {
            if (r.skillRequirements == null || r.skillRequirements.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < r.skillRequirements.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(r.skillRequirements[i].skill.LabelCap).Append(' ')
                    .Append(r.skillRequirements[i].minLevel);
            }
            return sb.ToString();
        }

        /// <summary>代理的固定资质够不够这个配方的 skillRequirements。</summary>
        private bool SkillOk(RecipeDef r)
        {
            if (r.skillRequirements == null) return true;
            int level = comp.Props.skillLevel;
            for (int i = 0; i < r.skillRequirements.Count; i++)
            {
                if (r.skillRequirements[i].minLevel > level) return false;
            }
            return true;
        }
    }
}
