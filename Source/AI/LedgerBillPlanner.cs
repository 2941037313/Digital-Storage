using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
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
                // 8.1 bugfix（社区反馈：制作食物时 1 单位食物被当成 1 营养值）：
                // 配方需求是「值」（营养/体积），不是单位数——必须按每个候选 def 的
                // 每单位值换算（对齐原版 TryFindBestBillIngredientsInSet_AllowMix 的
                // ceil(剩余值/每单位值) + 按值扣减；生肉 0.05 营养/单位 → 0.5 营养的
                // 简单餐需要 10 单位肉）。旧实现 Math.Ceiling(GetBaseCount()) 把
                // 1 单位当 1 营养，少取 10 倍，原版制作又不校验摆料数量 → 凭空多料。
                float remainingValue = recipe.Worker.GetIngredientCount(ing, bill);
                if (remainingValue <= 0f) continue;

                var valueGetter = recipe.IngredientValueGetter;
                // M1: 跨 key 累加凑数——不同 stuff 的同 def 材料（不同 ItemKey）能凑齐
                // （与原版 bill 取料一致：stuff 不参与 ingredient 匹配）
                foreach (var kv in ledger.Stock)
                {
                    if (remainingValue <= 0.0001f) break;
                    if (!MatchIngredient(kv.Key.def, ing, bill)) continue;
                    long avail = ledger.Available(kv.Key);
                    if (tempUsed.TryGetValue(kv.Key, out long used)) avail -= used;
                    if (avail <= 0) continue;

                    float unitVal = valueGetter.ValuePerUnitOf(kv.Key.def);
                    if (unitVal <= 0f) continue; // 营养 getter 对不可食 def 返回 0 → 跳过，防除零

                    int needForDef = Mathf.CeilToInt(remainingValue / unitVal);
                    int t = (int)System.Math.Min(avail, (long)needForDef);
                    if (t <= 0) continue;
                    tempUsed[kv.Key] = tempUsed.TryGetValue(kv.Key, out long u2) ? u2 + t : t;
                    result.Add((kv.Key, t));
                    remainingValue -= t * unitVal; // 按「值」扣减：混合 def（肉+蛋）各自营养不同
                }
                if (remainingValue > 0.0001f)
                {
                    if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                        Log.Message($"[DS-Job] TryPlan FAIL bill={bill.Label}: ingredient[{i}] value-shortfall={remainingValue:F2} in core");
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
