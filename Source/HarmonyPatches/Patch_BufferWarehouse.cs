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
    /// </summary>
    [HarmonyPatch(typeof(Thing), "Print")]
    public static class Patch_BufferWarehouse_HideItems
    {
        private static readonly Dictionary<Map, HashSet<IntVec3>> bwPositions
            = new Dictionary<Map, HashSet<IntVec3>>();

        public static void Register(Map map, IntVec3 pos)
        {
            if (!bwPositions.TryGetValue(map, out var set))
                bwPositions[map] = set = new HashSet<IntVec3>();
            set.Add(pos);
        }

        public static void Deregister(Map map, IntVec3 pos)
        {
            if (bwPositions.TryGetValue(map, out var set))
                set.Remove(pos);
        }

        [HarmonyPrefix]
        static bool Prefix(Thing __instance)
        {
            var map = __instance.Map;
            if (map == null) return true;
            if (__instance is Building_BufferWarehouse) return true;
            if (bwPositions.TryGetValue(map, out var set)
                && set.Contains(__instance.Position))
                return false;
            return true;
        }
    }
}
