using System.Collections.Generic;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(Building_Storage), "Accepts")]
    public static class Patch_BufferWarehouse_Accepts
    {
        [HarmonyPrefix]
        static bool Prefix(Thing t, Building_Storage __instance, ref bool __result)
        {
            if (__instance is Building_BufferWarehouse)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 5c: 阻止缓冲仓库格子上的物品被渲染。
    /// 跟踪每个 map 上所有 BW 的 Position，Thing.Print 时检查位置并跳过。
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
