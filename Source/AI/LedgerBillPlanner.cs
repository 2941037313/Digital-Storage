using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// bill 材料 → 账本 ItemKey 的规划器。
    /// WorkGiver 判派工能否派、JobDriver 启动时重算时都走这一套，规则一致。
    /// 凑不齐返 null；成功返 (ItemKey, count) 列表。
    /// </summary>
    public static class LedgerBillPlanner
    {
        public static List<(ItemKey key, int count)> TryPlan(Bill bill, Building_StorageCore core)
        {
            if (bill == null || core == null) return null;
            var recipe = bill.recipe;
            if (recipe?.ingredients == null || recipe.ingredients.Count == 0)
                return new List<(ItemKey, int)>();

            var ledger = core.Ledger;
            var result = new List<(ItemKey, int)>();
            var tempUsed = new Dictionary<ItemKey, long>();

            for (int i = 0; i < recipe.ingredients.Count; i++)
            {
                var ing = recipe.ingredients[i];
                int need = (int)System.Math.Ceiling(ing.GetBaseCount());
                if (need <= 0) continue;

                ItemKey picked = default;
                bool found = false;
                foreach (var kv in ledger.Stock)
                {
                    if (!MatchIngredient(kv.Key.def, ing, bill)) continue;
                    long avail = ledger.Available(kv.Key);
                    if (tempUsed.TryGetValue(kv.Key, out long used)) avail -= used;
                    if (avail < need) continue;
                    picked = kv.Key;
                    found = true;
                    break;
                }
                if (!found)
                {
                    if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                        Log.Message($"[DS-Job] TryPlan FAIL bill={bill.Label}: ingredient[{i}] need={need} not found in core");
                    return null;
                }

                if (!tempUsed.ContainsKey(picked)) tempUsed[picked] = 0;
                tempUsed[picked] += need;
                result.Add((picked, need));
            }
            return result;
        }

        private static bool MatchIngredient(ThingDef def, IngredientCount ing, Bill bill)
        {
            if (!ing.filter.Allows(def)) return false;
            if (!ing.IsFixedIngredient && !bill.ingredientFilter.Allows(def)) return false;
            return true;
        }
    }
}
