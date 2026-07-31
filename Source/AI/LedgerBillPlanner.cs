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

                // M1: 跨 key 累加凑数——不同 stuff 的同 def 材料（不同 ItemKey）能凑齐
                // （与原版 bill 取料一致：stuff 不参与 ingredient 匹配）
                int remainingNeed = need;
                foreach (var kv in ledger.Stock)
                {
                    if (remainingNeed <= 0) break;
                    if (!MatchIngredient(kv.Key.def, ing, bill)) continue;
                    long avail = ledger.Available(kv.Key);
                    if (tempUsed.TryGetValue(kv.Key, out long used)) avail -= used;
                    if (avail <= 0) continue;
                    int t = (int)System.Math.Min(avail, (long)remainingNeed);
                    tempUsed[kv.Key] = tempUsed.TryGetValue(kv.Key, out long u2) ? u2 + t : t;
                    result.Add((kv.Key, t));
                    remainingNeed -= t;
                }
                if (remainingNeed > 0)
                {
                    if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                        Log.Message($"[DS-Job] TryPlan FAIL bill={bill.Label}: ingredient[{i}] need={need} shortfall={remainingNeed} in core");
                    return null;
                }
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
