using System.Collections.Generic;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 缓冲仓库的 haul 拦截：物品完全可见（不接受 Forbidden），但 pawn 不能搬入。
    /// 借鉴 DSU 的 ForbidPawnInput 架构——物品自由，只禁搬运方向。
    /// </summary>

    // 阻止 haul 目的地选中 BW：所有 FindBestStorage 路径跳过 BW SlotGroup
    [HarmonyPatch(typeof(StoreUtility), "TryFindBestBetterStorageFor")]
    public static class Patch_BW_BlockHaulDestination
    {
        [HarmonyPrefix]
        static bool Prefix(Thing t, Map map, ref IntVec3 foundCell, ref IHaulDestination haulDestination)
        {
            // 如果当前物品已经在 BW 上，正常走原版逻辑找更好的存储
            return true;
        }

        [HarmonyPostfix]
        static void Postfix(ref IntVec3 foundCell, ref IHaulDestination haulDestination, ref bool __result)
        {
            if (!__result) return;
            if (haulDestination is Building_BufferWarehouse)
            {
                foundCell = IntVec3.Invalid;
                haulDestination = null;
                __result = false;
            }
        }
    }

    /// <summary>
    /// 阻止缓冲仓库格子上的物品被渲染（运行时隐藏）。
    /// F5: 旧实现用静态 <c>Dictionary&lt;Map,HashSet&lt;IntVec3&gt;&gt;</c> 记坐标——map 条目从不清理
    /// （每次开新图泄漏一份），而且「站在该格的任何东西」都被吞掉绘制。
    /// 改为按 SlotGroup 的宿主判定，无静态状态、无坐标误伤。
    /// </summary>
    [HarmonyPatch(typeof(Thing), "Print")]
    public static class Patch_BufferWarehouse_HideItems
    {
        [HarmonyPrefix]
        static bool Prefix(Thing __instance)
        {
            if (__instance is Building_BufferWarehouse) return true;
            var map = __instance.Map;
            if (map == null) return true;
            if (__instance.def.category != ThingCategory.Item) return true;

            var group = map.haulDestinationManager.SlotGroupAt(__instance.Position);
            return !(group?.parent is Building_BufferWarehouse);
        }
    }
}
