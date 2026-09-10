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

            var allCores = LedgerItemCollector.GetAllUsableCores(map);
            if (allCores.Count == 0) return true;

            var mapStuffDefs = new HashSet<ThingDef>();
            var ledgerStuffDefs = new HashSet<ThingDef>();

            for (int i = 0; i < allCores.Count; i++)
                CollectStuffDefs(allCores[i], thingDef, ledgerStuffDefs);

            foreach (var d in map.resourceCounter.AllCountedAmounts.Keys)
            {
                if (d.IsStuff && d.stuffProps.CanMake(thingDef)
                    && map.listerThings.ThingsOfDef(d).Count > 0)
                    mapStuffDefs.Add(d);
            }

            var missingFromMap = new HashSet<ThingDef>(ledgerStuffDefs);
            missingFromMap.ExceptWith(mapStuffDefs);
            if (missingFromMap.Count == 0) return true;

            var allDefs = new HashSet<ThingDef>(mapStuffDefs);
            allDefs.UnionWith(ledgerStuffDefs);

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

        private static void CollectStuffDefs(Building_StorageCore core, ThingDef thingDef, HashSet<ThingDef> result)
        {
            if (core == null || core.Ledger == null) return;
            foreach (var kv in core.Ledger.Stock)
            {
                if (kv.Value > 0 && kv.Key.def != null && kv.Key.def.IsStuff
                    && kv.Key.def.stuffProps.CanMake(thingDef))
                    result.Add(kv.Key.def);
            }
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
        private static readonly HashSet<ThingDef> cachedLedgerStuffDefs = new HashSet<ThingDef>();

        /// <summary>
        /// 只扩展列表内容，不修改原版缓存的 List&lt;Thing&gt;。
        /// - 原列表非空 → 原样返回（地图/其他 mod 已提供）
        /// - 账本没有该 stuff → 原样返回
        /// - 账本有该 stuff → 返回一个临时非空列表（元素只用于 Count > 0，不会被遍历）
        /// </summary>
        public static List<Thing> ExtendList(List<Thing> list, Map map, ThingDef stuffDef)
        {
            if (list == null || list.Count > 0 || map == null || stuffDef == null) return list;
            if (!GetLedgerStuffDefs(map).Contains(stuffDef)) return list;
            return new List<Thing>(1) { null };
        }

        private static HashSet<ThingDef> GetLedgerStuffDefs(Map map)
        {
            int tick = Find.TickManager.TicksGame;
            if (map == cachedMap && tick == cachedTick) return cachedLedgerStuffDefs;

            cachedMap = map;
            cachedTick = tick;
            cachedLedgerStuffDefs.Clear();

            var cores = LedgerItemCollector.GetAllUsableCores(map);
            for (int i = 0; i < cores.Count; i++)
            {
                var core = cores[i];
                if (core == null || core.Ledger == null) continue;
                foreach (var kv in core.Ledger.Stock)
                {
                    if (kv.Value > 0 && kv.Key.def != null)
                        cachedLedgerStuffDefs.Add(kv.Key.def);
                }
            }
            return cachedLedgerStuffDefs;
        }
    }
}
