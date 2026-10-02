using RimWorld;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>制作面板里"添加配方"的分类栏目（用户拍板：必须有分类，具体怎么分由实现决定）。</summary>
    internal enum CraftCategory
    {
        All,
        Weapon,
        Apparel,
        Manufactured,
        Food,
        Medical,
        Building,
        Other
    }

    /// <summary>
    /// <b>配方 → 分类</b>：全部按产物 def 的**真实数据**判定（不写 defName 名单，mod 物品也能自动落位）。
    ///
    /// <para>顺序即判定优先级：先武器，再衣物，再药，再食物，再建筑/家具，剩下的 Item 算"制成品"，
    /// 没有产物的（拆解、熔炼那种 specialProducts）落到"其他"。</para>
    /// </summary>
    internal static class CraftCategories
    {
        /// <summary>页签顺序。</summary>
        public static readonly CraftCategory[] Tabs =
        {
            CraftCategory.All,
            CraftCategory.Weapon,
            CraftCategory.Apparel,
            CraftCategory.Manufactured,
            CraftCategory.Food,
            CraftCategory.Medical,
            CraftCategory.Building,
            CraftCategory.Other
        };

        public static string LabelKey(CraftCategory c)
        {
            switch (c)
            {
                case CraftCategory.Weapon: return "DS_Cat_Weapon";
                case CraftCategory.Apparel: return "DS_Cat_Apparel";
                case CraftCategory.Manufactured: return "DS_Cat_Manufactured";
                case CraftCategory.Food: return "DS_Cat_Food";
                case CraftCategory.Medical: return "DS_Cat_Medical";
                case CraftCategory.Building: return "DS_Cat_Building";
                case CraftCategory.Other: return "DS_Cat_Other";
                default: return "DS_Cat_All";
            }
        }

        /// <summary>配方的"代表产物"（图标/分类都看它）。熔炼、屠宰这类没有 products 的拿 uiIconThing。</summary>
        public static ThingDef MainProduct(RecipeDef r)
        {
            if (r == null) return null;
            if (r.ProducedThingDef != null) return r.ProducedThingDef;
            if (r.products != null)
            {
                for (int i = 0; i < r.products.Count; i++)
                {
                    ThingDefCountClass c = r.products[i];
                    if (c != null && c.thingDef != null) return c.thingDef;
                }
            }
            return r.UIIconThing;
        }

        public static CraftCategory Of(RecipeDef r)
        {
            ThingDef p = MainProduct(r);
            if (p == null) return CraftCategory.Other;

            if (p.IsWeapon || p.IsRangedWeapon) return CraftCategory.Weapon;
            if (p.IsApparel) return CraftCategory.Apparel;
            if (p.IsMedicine || p.IsDrug) return CraftCategory.Medical;

            IngestibleProperties ing = p.ingestible;
            if (ing != null && ing.foodType != FoodTypeFlags.None) return CraftCategory.Food;

            if (p.building != null || p.category == ThingCategory.Building) return CraftCategory.Building;
            if (p.category == ThingCategory.Item) return CraftCategory.Manufactured;
            return CraftCategory.Other;
        }
    }
}
