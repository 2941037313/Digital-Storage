using RimWorld;
using Verse;

namespace DigitalStorage.Core
{
    /// <summary>
    /// 白名单策略：决定什么物品能进账本。
    /// v3 规则：衣物、武器、尸体、任何带品质的物品一律拒；其余放行。
    /// </summary>
    public static class LedgerPolicy
    {
        /// <summary>
        /// 检查一个 ThingDef 是否允许进入账本。
        /// </summary>
        public static bool CanIngest(ThingDef def)
        {
            if (def == null) return false;

            // 带品质的一律拒（武器、盔甲、手工家具等）
            if (def.HasComp(typeof(CompQuality))) return false;

            // 走 thingCategories 链判根分类
            if (def.thingCategories != null)
            {
                for (int i = 0; i < def.thingCategories.Count; i++)
                {
                    if (IsBlockedCategory(def.thingCategories[i])) return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 检查一个 Thing 实例是否允许进入账本。
        /// </summary>
        public static bool CanIngest(Thing t)
        {
            if (t == null || t.Destroyed) return false;
            if (t is Ghost.GhostThing) return false;
            if (t is UnfinishedThing) return false;
            if (t is MinifiedThing) return false;
            return CanIngest(t.def);
        }

        private static bool IsBlockedCategory(ThingCategoryDef cat)
        {
            for (var c = cat; c != null; c = c.parent)
            {
                string d = c.defName;
                if (d == "Apparel" || d == "Weapons" || d == "Corpses") return true;
            }
            return false;
        }
    }
}
