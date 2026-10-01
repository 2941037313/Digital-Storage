using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 原版选材菜单的账本扩展：
    /// 只把 ProcessInput 里 "地图上物理存在该 stuff" 的条件扩展为
    /// "地图上有 或 账本里有"，其余全部走原版。
    ///
    /// 这样可保留 writeStuff / sourcePrecept / onCloseCallback / CheckCanInteract，
    /// 并与其他对 ProcessInput 做 Transpiler 的 mod（VMF / Storage Network / MultiFloors）链式共存。
    /// </summary>
    [HarmonyPatch(typeof(Designator_Build), "ProcessInput")]
    [HarmonyPriority(Priority.High)]
    internal static class Patch_DesignatorBuild_ProcessInput_Transpiler
    {
        public static bool Applied;

        private static readonly MethodInfo GetCount = AccessTools.PropertyGetter(typeof(List<Thing>), "Count");
        private static readonly MethodInfo GetMap = AccessTools.PropertyGetter(typeof(Designator), "Map");
        private static readonly MethodInfo ExtendList = AccessTools.Method(typeof(DSMaterialMenuUtility), "ExtendList");

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            if (GetCount == null || GetMap == null || ExtendList == null)
            {
                Log.Error("[DigitalStorage] ProcessInput transpiler: reflection lookup failed; fallback prefix will be used.");
                return list;
            }

            try
            {
                int countIndex = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    var mi = list[i].operand as MethodInfo;
                    if (list[i].opcode == OpCodes.Callvirt && mi == GetCount)
                    {
                        countIndex = i;
                        break;
                    }
                }
                if (countIndex < 0)
                {
                    Log.Error("[DigitalStorage] ProcessInput transpiler: List<Thing>.get_Count not found; fallback prefix will be used.");
                    return list;
                }

                // 找到承载 thingDef2 的局部变量加载指令。
                // 常规情况：get_Count 前面依次是 ThingsOfDef(...) 和 ldloc thingDef2；
                // 其他 transpiler 改写后（Storage Network 替换 ThingsOfDef / VMF 插入 AddThingList），
                // 向前找最近的 ldloc 仍然是传入的 stuff def。
                CodeInstruction defLoad = null;
                for (int i = countIndex - 1; i >= 0; i--)
                {
                    var op = list[i].opcode;
                    if (op == OpCodes.Ldloc || op == OpCodes.Ldloc_S || op == OpCodes.Ldloc_0
                        || op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2 || op == OpCodes.Ldloc_3)
                    {
                        // 如果拿得到局部变量类型信息，确认它就是 ThingDef；拿不到就按最近的 ldloc 处理。
                        var local = list[i].operand as LocalBuilder;
                        if (local != null && local.LocalType != typeof(ThingDef)) continue;
                        var localInfo = list[i].operand as LocalVariableInfo;
                        if (localInfo != null && localInfo.LocalType != typeof(ThingDef)) continue;
                        defLoad = list[i];
                        break;
                    }
                }
                if (defLoad == null)
                {
                    Log.Error("[DigitalStorage] ProcessInput transpiler: stuffDef local load not found; fallback prefix will be used.");
                    return list;
                }

                list.InsertRange(countIndex, new[]
                {
                    CodeInstruction.LoadArgument(0),
                    new CodeInstruction(OpCodes.Callvirt, GetMap),
                    defLoad.Clone(),
                    new CodeInstruction(OpCodes.Call, ExtendList)
                });
                Applied = true;
            }
            catch (Exception ex)
            {
                Log.Error("[DigitalStorage] ProcessInput transpiler failed: " + ex);
            }
            return list;
        }
    }

    /// <summary>
    /// 降级兜底：Transpiler 匹配失败时（未来版本 IL 变化 / 其他 mod 先改写 IL），
    /// 才用原 Prefix 替换菜单。Priority=Low 保证其他 Prefix（MapLevelFramework / Infinite Storage 等）
    /// 先执行；它 return false 时本 Prefix 会被 Harmony 跳过。
    /// </summary>
    [HarmonyPatch(typeof(Designator_Build), "ProcessInput")]
    [HarmonyPriority(Priority.Low)]
    internal static class Patch_DesignatorBuild_ProcessInput_Fallback
    {
        private static readonly List<Thing> tmpStuffThings = new List<Thing>();

        private static readonly AccessTools.FieldRef<Designator_Build, bool> WriteStuffRef =
            AccessTools.FieldRefAccess<Designator_Build, bool>("writeStuff");

        private static readonly Func<Designator, bool> CanInteract =
            AccessTools.MethodDelegate<Func<Designator, bool>>(AccessTools.Method(typeof(Designator), "CheckCanInteract"));

        private static bool Prefix(Designator_Build __instance)
        {
            if (Patch_DesignatorBuild_ProcessInput_Transpiler.Applied) return true;

            var thingDef = __instance.PlacingDef as ThingDef;
            if (thingDef == null || !thingDef.MadeFromStuff) return true;

            var map = __instance.Map;
            if (map == null) return true;
            if (!CanInteract(__instance)) return false;

            var allCores = HaulSourceContents.EnabledSources(map);
            if (allCores.Count == 0) return true;

            var mapStuffDefs = new HashSet<ThingDef>();
            var containerStuffDefs = new HashSet<ThingDef>();

            CollectStuffDefs(map, thingDef, containerStuffDefs);

            foreach (var d in map.resourceCounter.AllCountedAmounts.Keys)
            {
                if (d.IsStuff && d.stuffProps.CanMake(thingDef)
                    && map.listerThings.ThingsOfDef(d).Count > 0)
                    mapStuffDefs.Add(d);
            }

            var missingFromMap = new HashSet<ThingDef>(containerStuffDefs);
            missingFromMap.ExceptWith(mapStuffDefs);
            if (missingFromMap.Count == 0) return true;

            var allDefs = new HashSet<ThingDef>(mapStuffDefs);
            allDefs.UnionWith(containerStuffDefs);

            var list = new List<FloatMenuOption>();
            foreach (var stuffDef in allDefs.OrderByDescending(d => d.stuffProps?.commonality ?? 0f)
                         .ThenBy(d => d.BaseMarketValue))
            {
                var localDef = stuffDef;
                string text = GenLabel.ThingLabel(__instance.PlacingDef, localDef, 1).CapitalizeFirst();
                list.Add(new FloatMenuOption(text, () =>
                {
                    __instance.SetStuffDef(localDef);
                    WriteStuffRef(__instance) = true;
                    Find.DesignatorManager.Select(__instance);
                }, localDef));
            }

            var floatMenu = new FloatMenu(list);
            floatMenu.onCloseCallback = () => WriteStuffRef(__instance) = true;
            Find.WindowStack.Add(floatMenu);
            Find.DesignatorManager.Select(__instance);
            return false;
        }

        private static void CollectStuffDefs(Map map, ThingDef thingDef, HashSet<ThingDef> result)
        {
            HaulSourceContents.GatherAll(map, tmpStuffThings);
            for (int i = 0; i < tmpStuffThings.Count; i++)
            {
                ThingDef d = tmpStuffThings[i]?.def;
                if (d != null && d.IsStuff && d.stuffProps.CanMake(thingDef))
                    result.Add(d);
            }
            tmpStuffThings.Clear();
        }
    }

    /// <summary>
    /// ProcessInput 的账本扩展工具：
    /// 原版 ProcessInput 遍历 resourceCounter.AllCountedAmounts.Keys 时，条件里会检查
    /// `listerThings.ThingsOfDef(stuffDef).Count > 0`。Transpiler 在 get_Count 前插入本工具调用，
    /// 把"地图上有"扩展为"地图上有 或 账本里有"，其余逻辑全部保留原版。
    /// </summary>
    internal static class DSMaterialMenuUtility
    {
        private static Map cachedMap;
        private static int cachedTick = -1;
        private static readonly HashSet<ThingDef> cachedContainerStuffDefs = new HashSet<ThingDef>();
        private static readonly List<Thing> tmpStuffThings = new List<Thing>();

        /// <summary>
        /// 只扩展列表内容，不修改原版缓存的 List&lt;Thing&gt;。
        /// - 原列表非空 → 原样返回（地图/其他 mod 已提供）
        /// - 账本没有该 stuff → 原样返回
        /// - 账本有该 stuff → 返回一个临时非空列表（元素只用于 Count > 0，不会被遍历）
        /// </summary>
        public static List<Thing> ExtendList(List<Thing> list, Map map, ThingDef stuffDef)
        {
            if (list == null || list.Count > 0 || map == null || stuffDef == null) return list;
            if (!GetContainerStuffDefs(map).Contains(stuffDef)) return list;
            return new List<Thing>(1) { null };
        }

        private static HashSet<ThingDef> GetContainerStuffDefs(Map map)
        {
            int tick = Find.TickManager.TicksGame;
            if (map == cachedMap && tick == cachedTick) return cachedContainerStuffDefs;

            cachedMap = map;
            cachedTick = tick;
            cachedContainerStuffDefs.Clear();

            // 4.0：容器内容物取代账本。ExtendList 只关心"这个 stuff 在不在"，
            // 所以只收 def（不需要数量）。缓存按 tick，因为内容物随时会变。
            HaulSourceContents.GatherAll(map, tmpStuffThings);
            for (int i = 0; i < tmpStuffThings.Count; i++)
            {
                Thing t = tmpStuffThings[i];
                if (t?.def != null) cachedContainerStuffDefs.Add(t.def);
            }
            tmpStuffThings.Clear();

            return cachedContainerStuffDefs;
        }
    }
}
