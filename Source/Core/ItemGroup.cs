using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DigitalStorage.Core
{
    /// <summary>
    /// 面板分组枚举（6 组）+ 归组函数。
    /// 每种 ThingDef 归组一次，结果缓存。
    /// </summary>
    public enum ItemGroup
    {
        Raw = 0,          // 原料：ResourcesRaw
        Food,             // 食物：Foods
        Manufactured,     // 制成品：Manufactured 非药品弹药 + Textiles + Leathers + Wools
        MedicalAmmo,      // 药品耗材：Medicine / Drugs / MortarShells
        BodyParts,        // 义体植入：BodyParts
        Other             // 其他：Items / Chunks / 未归组
    }

    public static class ItemGrouping
    {
        private static readonly Dictionary<ThingDef, ItemGroup> cache = new Dictionary<ThingDef, ItemGroup>();

        public static ItemGroup GroupOf(ThingDef def)
        {
            if (def == null) return ItemGroup.Other;
            if (cache.TryGetValue(def, out var g)) return g;

            g = ComputeGroup(def);
            cache[def] = g;
            return g;
        }

        private static ItemGroup ComputeGroup(ThingDef def)
        {
            if (def.thingCategories == null) return ItemGroup.Other;

            // 按优先级查：药品弹药 > 食物 > 原料 > 制成品 > 义体 > 其他
            // 同一物品理论只命中一类，但药品类有时既在 Manufactured 又在 Medicine 下，需要先命中更具体的
            for (int i = 0; i < def.thingCategories.Count; i++)
            {
                var cat = def.thingCategories[i];
                for (var c = cat; c != null; c = c.parent)
                {
                    string d = c.defName;
                    if (d == "Medicine" || d == "Drugs" || d == "MortarShells") return ItemGroup.MedicalAmmo;
                    if (d == "BodyParts") return ItemGroup.BodyParts;
                }
            }

            for (int i = 0; i < def.thingCategories.Count; i++)
            {
                var cat = def.thingCategories[i];
                for (var c = cat; c != null; c = c.parent)
                {
                    string d = c.defName;
                    if (d == "Foods") return ItemGroup.Food;
                    if (d == "ResourcesRaw") return ItemGroup.Raw;
                    if (d == "Textiles" || d == "Leathers" || d == "Wools" || d == "Manufactured") return ItemGroup.Manufactured;
                }
            }

            return ItemGroup.Other;
        }

        public static string LabelKeyOf(ItemGroup g)
        {
            switch (g)
            {
                case ItemGroup.Raw: return "DS_Group_Raw";
                case ItemGroup.Food: return "DS_Group_Food";
                case ItemGroup.Manufactured: return "DS_Group_Manufactured";
                case ItemGroup.MedicalAmmo: return "DS_Group_MedicalAmmo";
                case ItemGroup.BodyParts: return "DS_Group_BodyParts";
                default: return "DS_Group_Other";
            }
        }
    }
}
