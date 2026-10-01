using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Compatibility
{
    /// <summary>
    /// 定向兼容补丁：Phinix / Phinix 红包 的「允许交易不在储存区中的物品」分支。
    ///
    /// <para><b>为什么这条只能定向打</b>：那个开关<b>打开</b>时，它们枚举的是
    /// <c>map.listerThings.AllThings</c>（<c>TradeWindow.cs:106</c> / <c>RedPacketTab.cs:157</c>）
    /// —— 只有**已 Spawn** 的物品。而我们的内容物住在 <c>ThingOwner</c> 里（未 Spawn）：
    /// 把未 Spawn 的东西塞进 <c>listerThings</c> 是旧 GhostThing 的 NRE 路，硬约束不允许。
    /// 所以这一面没有数据层接缝，只能在它们的列表上做加法。
    /// 开关<b>关闭</b>时走 <c>AllGroups → HeldThings</c>，那面已由
    /// <c>Patch_SlotGroup_HeldThings</c> + 惰性替身覆盖（这才是通用解）。</para>
    ///
    /// <para><b>mod 版本稳定性</b>：Phinix 最后更新 2025-08-22，之后未动 ⇒ 反射目标稳定。
    /// 反射面（均已在你装的 DLL 里核对过字符串）：</para>
    /// <code>
    /// PhinixClient.TradeWindow.PreOpen()
    ///   private List&lt;StackedThings&gt; availableItems          // UI 真正的数据源（:169/:203）
    ///   private List&lt;StackedThings&gt; filteredAvailableItems   // PreOpen 里 = availableItems（同一实例）
    /// PhinixRedPacket.RedPacketTab.RefreshAvailableItems()
    ///   private List&lt;StackedThings&gt; availableItems
    ///   private List&lt;StackedThings&gt; filteredItems
    ///   private Dictionary&lt;StackedThings,int&gt; itemCounts      // 金额/显示用，必须同步
    ///   private void UpdateFilteredItems()                     // 追加后要让它重算 filteredItems
    /// </code>
    ///
    /// <para><b>不自己造它们的对象</b>：用对方 <c>public static StackedThings.GroupThings(IEnumerable&lt;Thing&gt;)</c>
    /// 分组，语义与它们自己那条完全一致。</para>
    ///
    /// <para><b>安全纪律</b>：<c>Install</c> 在 <c>Harmony.PatchAll()</c> <b>之后</b>单独调用，
    /// 且全程 try/catch —— 第三方补丁的失败绝不能中断本 mod 自己的 PatchAll
    /// （2026-10-02 那次 <c>Toils_Goto.GotoThing</c> 歧义中断 PatchAll 的教训）。
    /// 每个 postfix 自身也整体 try/catch：绝不把异常抛进别人的调用栈。</para>
    /// </summary>
    internal static class PhinixCompatPatch
    {
        private sealed class Target
        {
            public MethodBase method;           // 已挂上的原始方法
            public string label;
            public string availableField = "availableItems";
            public string filteredField;        // 与 availableField 同实例时自动跳过
            public string updateFilteredMethod; // 追加后调用（对方重算 filtered）
            public string countsField;          // Dictionary<StackedThings,int>
            public bool excludeMinified;        // 对齐对方各自的筛选条件
        }

        private static readonly List<Target> installed = new List<Target>();
        private static Type stackedThingsType;
        private static MethodInfo groupThingsMethod;
        private static FieldInfo thingsField;

        /// <summary>已成功挂上的第三方挂点数量（自检用）。</summary>
        public static int InstalledCount => installed.Count;

        /// <summary>在 <c>HarmonyInit</c> 的 <c>PatchAll()</c> 之后调用。加载失败/字段改名 → 静默跳过。</summary>
        public static void Install(Harmony harmony)
        {
            try
            {
                TryInstall(harmony, "PhinixClient.TradeWindow", "PreOpen",
                    new Target
                    {
                        label = "Phinix 交易窗口",
                        filteredField = "filteredAvailableItems",
                        excludeMinified = false // 它们的筛选就是 category==Item && !IsCorpse
                    });

                TryInstall(harmony, "PhinixRedPacket.RedPacketTab", "RefreshAvailableItems",
                    new Target
                    {
                        label = "Phinix 红包",
                        filteredField = "filteredItems",
                        updateFilteredMethod = "UpdateFilteredItems",
                        countsField = "itemCounts",
                        excludeMinified = true // 它们的筛选多一条 !(thing is MinifiedThing)
                    });
            }
            catch (Exception e)
            {
                Log.Warning("[DigitalStorage] Phinix 兼容补丁安装失败（已忽略）：" + e);
            }
        }

        private static void TryInstall(Harmony harmony, string typeName, string methodName, Target target)
        {
            try
            {
                Type type = AccessTools.TypeByName(typeName);
                if (type == null) return; // mod 未安装

                MethodInfo method = AccessTools.Method(type, methodName);
                if (method == null) return; // 版本不同

                if (stackedThingsType == null)
                {
                    stackedThingsType = AccessTools.TypeByName("PhinixClient.StackedThings");
                    if (stackedThingsType == null) return;
                    groupThingsMethod = AccessTools.Method(stackedThingsType, "GroupThings");
                    thingsField = AccessTools.Field(stackedThingsType, "Things");
                    if (groupThingsMethod == null || thingsField == null) return;
                }

                MethodInfo postfix = AccessTools.Method(typeof(PhinixCompatPatch), nameof(Postfix));
                harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                target.method = method;
                installed.Add(target);

                if (Prefs.DevMode)
                    Log.Warning("[DigitalStorage] Phinix 兼容补丁已挂：" + target.label + " ← " + typeName + "." + methodName);
            }
            catch (Exception e)
            {
                Log.Warning("[DigitalStorage] Phinix 兼容补丁挂点失败（已忽略）：" + typeName + "." + methodName + " : " + e.Message);
            }
        }

        private static void Postfix(object __instance, MethodBase __originalMethod)
        {
            try
            {
                Target target;
                if (__instance == null || __originalMethod == null) return;
                target = FindTarget(__originalMethod);
                if (target == null) return;

                // 开关关着时它们走 AllGroups → HeldThings，我们的东西已经在列表里了 —— 别重复加
                bool? tradable = TryGetAllItemsTradable();
                if (tradable == false) return;

                List<Thing> ours = CollectContents(target.excludeMinified);
                if (ours.Count == 0) return;

                // 再加一道与开关无关的去重保险：列表里已经出现我们的对象就什么都不做
                if (AlreadyListed(__instance, target.availableField, ours)) return;

                object grouped = groupThingsMethod.Invoke(null, new object[] { ours });
                if (grouped == null) return;

                AppendToFieldList(__instance, target.availableField, grouped);

                if (!string.IsNullOrEmpty(target.updateFilteredMethod))
                    InvokeIfExists(__instance, target.updateFilteredMethod);
                else if (!string.IsNullOrEmpty(target.filteredField)
                         && target.filteredField != target.availableField)
                    AppendToFieldList(__instance, target.filteredField, grouped);

                if (!string.IsNullOrEmpty(target.countsField))
                    AddToCounts(__instance, target.countsField, grouped);
            }
            catch (Exception e)
            {
                if (Prefs.DevMode) Log.Warning("[DigitalStorage] Phinix 兼容补丁 postfix 异常（已吞）：" + e.Message);
            }
        }

        // ============ 我们的物品 ============

        /// <summary>按名字+声明类型找目标（不依赖 MethodBase 的引用相等，避免挂点对不上）。</summary>
        private static Target FindTarget(MethodBase original)
        {
            for (int i = 0; i < installed.Count; i++)
            {
                Target t = installed[i];
                if (t.method == null) continue;
                if (ReferenceEquals(t.method, original)) return t;
                if (t.method.Name == original.Name && t.method.DeclaringType == original.DeclaringType) return t;
            }
            return null;
        }

        private static List<Thing> CollectContents(bool excludeMinified)
        {
            var result = new List<Thing>();
            List<Map> maps = Find.Maps;
            if (maps == null) return result;

            for (int i = 0; i < maps.Count; i++)
            {
                Map map = maps[i];
                if (map == null || !map.IsPlayerHome) continue; // 与它们一致：只看玩家主图

                // 复用缓冲，必须立刻用完（内部别调别的 HaulSourceContents 方法）
                List<IHaulSource> sources = HaulSourceContents.EnabledSources(map);
                for (int j = 0; j < sources.Count; j++)
                {
                    Building_StorageCore core = sources[j] as Building_StorageCore;
                    if (core == null) continue;

                    ThingOwner held = core.GetDirectlyHeldThings();
                    if (held == null) continue;

                    for (int k = 0; k < held.Count; k++)
                    {
                        Thing t = held[k];
                        if (t == null || t.Destroyed || t.def == null) continue;
                        if (t.def.category != ThingCategory.Item || t.def.IsCorpse) continue;
                        if (excludeMinified && t is MinifiedThing) continue;
                        result.Add(t);
                    }
                }
            }
            return result;
        }

        // ============ 反射小工具 ============

        /// <summary>读 Phinix 的「允许交易不在储存区中的物品」；读不到返回 null。</summary>
        public static bool? TryGetAllItemsTradable()
        {
            try
            {
                Type clientType = AccessTools.TypeByName("PhinixClient.Client");
                if (clientType == null) return null;

                object instance = AccessTools.Property(clientType, "Instance")?.GetValue(null)
                               ?? AccessTools.Field(clientType, "Instance")?.GetValue(null);
                if (instance == null) return null;

                object settings = AccessTools.Property(clientType, "Settings")?.GetValue(instance);
                if (settings == null) return null;

                PropertyInfo flag = AccessTools.Property(settings.GetType(), "AllItemsTradable");
                object value = flag == null ? null : flag.GetValue(settings);
                return (value is bool b) ? (bool?)b : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AppendToFieldList(object instance, string fieldName, object grouped)
        {
            IList list = AccessTools.Field(instance.GetType(), fieldName)?.GetValue(instance) as IList;
            IEnumerable src = grouped as IEnumerable;
            if (list == null || src == null) return;

            foreach (object o in src) list.Add(o);
        }

        private static void InvokeIfExists(object instance, string methodName)
        {
            AccessTools.Method(instance.GetType(), methodName)?.Invoke(instance, null);
        }

        private static void AddToCounts(object instance, string fieldName, object grouped)
        {
            IDictionary dict = AccessTools.Field(instance.GetType(), fieldName)?.GetValue(instance) as IDictionary;
            IEnumerable src = grouped as IEnumerable;
            if (dict == null || src == null) return;

            foreach (object entry in src)
            {
                // 它们自己就是 `itemCounts[stack] = stack.Count`，这里照抄
                object count = entry.GetType().GetProperty("Count")?.GetValue(entry);
                if (count != null) dict[entry] = count;
            }
        }

        /// <summary>列表里是否已经出现我们的对象（用于和"开关关着时的 HeldThings 路径"去重）。</summary>
        private static bool AlreadyListed(object instance, string fieldName, List<Thing> ours)
        {
            IList list = AccessTools.Field(instance.GetType(), fieldName)?.GetValue(instance) as IList;
            if (list == null || list.Count == 0 || thingsField == null) return false;

            var mine = new HashSet<Thing>(ours);
            for (int i = 0; i < list.Count; i++)
            {
                IList things = thingsField.GetValue(list[i]) as IList;
                if (things == null) continue;

                for (int j = 0; j < things.Count; j++)
                {
                    Thing t = things[j] as Thing;
                    if (t != null && mine.Contains(t)) return true;
                }
            }
            return false;
        }
    }
}
