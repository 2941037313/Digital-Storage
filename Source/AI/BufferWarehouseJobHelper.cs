using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 芯片 pawn 从缓冲仓库传送物品的共享逻辑。
    /// 当核心账本无材料时，芯片 pawn 可隔空从缓冲仓库 SplitOff 物品。
    /// 被 WorkGiver_DS_WithdrawForBill 和其他取料路径调用。
    /// </summary>
    public static class BufferWarehouseJobHelper
    {
        /// <summary>
        /// 尝试从地图上任一缓冲仓库取出 bill 所需材料。
        /// 成功时物品 spawn 在 pawn 脚下，返回 DoBill job。
        /// 失败返回 false。
        /// </summary>
        public static bool TryTakeForBill(Pawn pawn, Bill bill, Thing workTable, out Job job)
        {
            job = null;
            if (pawn.Map == null) return false;

            var mapComp = pawn.Map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return false;

            var buffers = mapComp.GetAllBufferWarehouses();
            for (int i = 0; i < buffers.Count; i++)
            {
                var bw = buffers[i];
                if (bw == null || bw.Destroyed) continue;
                var slot = bw.GetSlotGroup();
                if (slot == null) continue;

                foreach (var t in slot.HeldThings)
                {
                    if (t.Destroyed) continue;
                    var keyDef = ItemKey.Of(t).def;
                    if (!MatchesBill(bill, keyDef)) continue;

                    int need = 0;
                    foreach (var ing in bill.recipe.ingredients)
                    {
                        if (ing.filter.Allows(keyDef))
                        {
                            need = (int)System.Math.Ceiling(ing.GetBaseCount());
                            break;
                        }
                    }
                    int take = System.Math.Min(need > 0 ? need : 75, t.stackCount);
                    var thing = t.SplitOff(take);
                    if (thing == null) continue;

                    if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                        Log.Message($"[DS-Job] BufferWarehouseHelper: {pawn.LabelShort} bill={bill.Label} take={take} {keyDef.defName} from bw={bw}");

                    GenPlace.TryPlaceThing(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near);
                    job = JobMaker.MakeJob(JobDefOf.DoBill, workTable);
                    job.bill = bill;
                    job.haulMode = HaulMode.ToCellNonStorage;
                    job.targetQueueB = new List<LocalTargetInfo> { thing };
                    job.countQueue = new List<int> { take };
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 检查 ThingDef 是否匹配 bill 的任一原料筛选。
        /// </summary>
        public static bool MatchesBill(Bill bill, ThingDef def)
        {
            if (bill.recipe?.ingredients == null) return false;
            foreach (var ing in bill.recipe.ingredients)
            {
                if (ing.filter.Allows(def))
                {
                    if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                        Log.Message($"[DS-Job] MatchesBill: {def.defName} matches ingredient filter but does NOT verify ALL ingredients satisfied (BUG risk)");
                    return true;
                }
            }
            return false;
        }
    }
}
