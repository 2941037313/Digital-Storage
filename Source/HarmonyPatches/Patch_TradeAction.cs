using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Components;
using DigitalStorage.Data;
using DigitalStorage.Services;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 交易行为兼容：交易完成后从虚拟存储扣除物品
    /// </summary>

    /// <summary>
    /// 虚拟存储交易扣除的通用逻辑
    /// </summary>
    public static class TradeDeductionHelper
    {
        /// <summary>
        /// 从虚拟存储扣除交易物品。返回 true 表示已处理（应跳过原版逻辑），false 表示非虚拟物品。
        /// </summary>
        public static bool TryDeductVirtualItem(Thing toGive, int countToGive, string tradeType)
        {
            if (toGive == null)
            {
                return false;
            }

            var tradeInfo = TradeItemTracker.GetTradeItemInfo(toGive);
            if (tradeInfo == null)
            {
                return false;
            }

            if (DigitalStorageSettings.enableTradeLog)
            {
                Log.Message($"[数字存储] 交易扣除开始 ({tradeType}): {tradeInfo.def?.label ?? "null"} x{countToGive}, " +
                    $"stuff={tradeInfo.stuffDef?.label ?? "null"}, " +
                    $"核心={tradeInfo.sourceCore?.NetworkName ?? "null"}, " +
                    $"核心状态: Spawned={tradeInfo.sourceCore?.Spawned}, Powered={tradeInfo.sourceCore?.Powered}");
            }

            int deducted = 0;

            if (tradeInfo.sourceCore != null && tradeInfo.sourceCore.Spawned && tradeInfo.sourceCore.Powered)
            {
                if (tradeInfo.stuffDef != null)
                {
                    Thing extracted = tradeInfo.sourceCore.ExtractItem(tradeInfo.def, countToGive, tradeInfo.stuffDef);
                    if (extracted != null)
                    {
                        deducted = extracted.stackCount;
                        extracted.Destroy(DestroyMode.Vanish);
                    }
                }
                else
                {
                    deducted = tradeInfo.sourceCore.DeductVirtualItems(tradeInfo.def, countToGive);
                }
            }
            else if (DigitalStorageSettings.enableTradeLog)
            {
                Log.Warning($"[数字存储] 交易扣除失败 ({tradeType}): 核心不可用, {tradeInfo.def?.label ?? "null"} x{countToGive}");
            }

            if (DigitalStorageSettings.enableTradeLog)
            {
                Log.Message($"[数字存储] 交易扣除完成 ({tradeType}): {tradeInfo.def?.label ?? "null"}, " +
                    $"请求={countToGive}, 实际扣除={deducted}, 差额={countToGive - deducted}");
            }

            if (deducted < countToGive)
            {
                Log.Warning($"[数字存储] 交易扣除不足 ({tradeType}): {tradeInfo.def?.label ?? "null"}, " +
                    $"请求={countToGive}, 实际={deducted}");
            }

            TradeItemTracker.UnregisterTradeItem(toGive);
            return true;
        }
    }

    /// <summary>
    /// 来访商人交易：卖出虚拟存储物品时从核心扣除
    /// </summary>
    [HarmonyPatch(typeof(Pawn_TraderTracker), "GiveSoldThingToTrader")]
    public static class Patch_TradeAction_GiveSoldThingToTrader
    {
        public static bool Prefix(Thing toGive, int countToGive, Pawn playerNegotiator)
        {
            if (TradeDeductionHelper.TryDeductVirtualItem(toGive, countToGive, "来访商人"))
            {
                return false; // 已处理，跳过原版逻辑
            }
            return true; // 非虚拟物品，走原版逻辑
        }
    }

    /// <summary>
    /// 远行队在据点交易：卖出虚拟存储物品时从核心扣除
    /// </summary>
    [HarmonyPatch(typeof(Settlement_TraderTracker), "GiveSoldThingToTrader")]
    public static class Patch_Settlement_TraderTracker_GiveSoldThingToTrader
    {
        public static bool Prefix(Thing toGive, int countToGive, Pawn playerNegotiator)
        {
            if (TradeDeductionHelper.TryDeductVirtualItem(toGive, countToGive, "据点交易"))
            {
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 轨道交易（商船）：卖出虚拟存储物品时从核心扣除
    ///
    /// 这是之前遗漏的关键补丁！
    /// 原版调用链：TradeDeal.TryExecute → Tradeable.ResolveTrade → TransferableUtility.TransferNoSplit
    ///   → TradeSession.trader.GiveSoldThingToTrader(thing, count, negotiator)
    /// 对于轨道交易，TradeSession.trader 是 TradeShip 实例，
    /// 所以实际调用的是 TradeShip.GiveSoldThingToTrader，而非 Pawn_TraderTracker 的版本。
    /// </summary>
    [HarmonyPatch(typeof(TradeShip), "GiveSoldThingToTrader")]
    public static class Patch_TradeShip_GiveSoldThingToTrader
    {
        public static bool Prefix(Thing toGive, int countToGive, Pawn playerNegotiator)
        {
            if (TradeDeductionHelper.TryDeductVirtualItem(toGive, countToGive, "轨道交易"))
            {
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 轨道交易扣除物品
    /// 
    /// 原版 LaunchThingsOfType 按 ThingDef+数量 从信标范围内收集物品并销毁。
    /// 
    /// 策略：完全接管此方法。
    /// 1. 先复刻原版逻辑，从地图信标范围内扣除物理物品
    /// 2. 如果物理物品不够，再从虚拟存储补扣剩余
    /// </summary>
    [HarmonyPatch(typeof(TradeUtility), "LaunchThingsOfType")]
    public static class Patch_TradeUtility_LaunchThingsOfType
    {
        public static bool Prefix(ThingDef resDef, int debt, Map map, TradeShip trader)
        {
            if (debt <= 0 || map == null || resDef == null)
            {
                return true;
            }

            // 检查是否有虚拟存储
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null || gameComp.GetAllCores().Count == 0)
            {
                return true; // 没有核心，走原版逻辑
            }

            int remaining = debt;

            // ===== 第一步：从地图信标范围内扣除物理物品（复刻原版逻辑） =====
            List<Building_OrbitalTradeBeacon> beacons = Building_OrbitalTradeBeacon.AllPowered(map).ToList();
            
            // 收集信标范围内的所有匹配物品
            List<Thing> launchableThings = new List<Thing>();
            foreach (Building_OrbitalTradeBeacon beacon in beacons)
            {
                foreach (IntVec3 cell in beacon.TradeableCells)
                {
                    List<Thing> thingsAtCell = map.thingGrid.ThingsListAt(cell);
                    for (int i = 0; i < thingsAtCell.Count; i++)
                    {
                        Thing thing = thingsAtCell[i];
                        if (thing.def == resDef)
                        {
                            launchableThings.Add(thing);
                        }
                    }
                }
            }

            // 从物理物品中扣除
            for (int i = 0; i < launchableThings.Count && remaining > 0; i++)
            {
                Thing thing = launchableThings[i];
                if (thing == null || thing.Destroyed)
                {
                    continue;
                }

                // 跳过核心 SlotGroup 中的预留物品（这些由虚拟存储管理）
                bool isInCore = false;
                foreach (Building_StorageCore core in gameComp.GetAllCores())
                {
                    if (core != null && core.Spawned && core.Map == map)
                    {
                        SlotGroup sg = core.GetSlotGroup();
                        if (sg != null && sg.CellsList.Contains(thing.Position))
                        {
                            isInCore = true;
                            break;
                        }
                    }
                }
                if (isInCore)
                {
                    continue; // 预留物品不在这里扣，后面从虚拟存储统一扣
                }

                int toTake = System.Math.Min(thing.stackCount, remaining);
                if (toTake >= thing.stackCount)
                {
                    remaining -= thing.stackCount;
                    // 给商人（原版行为）
                    trader?.GiveSoldThingToTrader(thing, thing.stackCount, TradeSession.playerNegotiator);
                    // 如果 GiveSoldThingToTrader 没有销毁它，手动销毁
                    if (!thing.Destroyed)
                    {
                        thing.Destroy(DestroyMode.Vanish);
                    }
                }
                else
                {
                    remaining -= toTake;
                    Thing splitOff = thing.SplitOff(toTake);
                    trader?.GiveSoldThingToTrader(splitOff, splitOff.stackCount, TradeSession.playerNegotiator);
                    if (!splitOff.Destroyed)
                    {
                        splitOff.Destroy(DestroyMode.Vanish);
                    }
                }
            }

            if (DigitalStorageSettings.enableTradeLog)
            {
                Log.Message($"[数字存储] LaunchThingsOfType: {resDef.label}, debt={debt}, 物理扣除后剩余={remaining}");
            }

            // ===== 第二步：从虚拟存储补扣剩余 =====
            if (remaining > 0)
            {
                foreach (Building_StorageCore core in gameComp.GetAllCores())
                {
                    if (core == null || !core.Spawned || !core.Powered || core.Map != map)
                    {
                        continue;
                    }

                    if (remaining <= 0)
                    {
                        break;
                    }

                    int deducted = core.DeductVirtualItems(resDef, remaining);
                    remaining -= deducted;

                    if (DigitalStorageSettings.enableTradeLog && deducted > 0)
                    {
                        Log.Message($"[数字存储] 轨道交易从虚拟存储扣除: {resDef.label} x{deducted}");
                    }
                }
            }

            if (remaining > 0)
            {
                Log.Warning($"[数字存储] LaunchThingsOfType: 无法完全满足 {resDef.label} 的需求, 缺口={remaining}");
            }

            // 完全接管，不执行原版逻辑
            return false;
        }
    }

    /// <summary>
    /// 交易对话框关闭时清理追踪
    /// </summary>
    [HarmonyPatch(typeof(Dialog_Trade), "Close")]
    public static class Patch_Dialog_Trade_Close
    {
        public static void Postfix()
        {
            // 清理所有交易物品追踪
            TradeItemTracker.Clear();
        }
    }
}
