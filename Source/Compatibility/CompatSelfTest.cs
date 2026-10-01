using System;
using System.Reflection;
using System.Text;
using DigitalStorage.AI;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Compatibility
{
    /// <summary>
    /// 【临时诊断】兼容层自检：核心 → AllGroups → SlotGroup.HeldThings → 第三方能看见什么。
    ///
    /// <para>用户实测「核心物品没出现在 Phinix / 红包的交易列表」。已核对 Phinix 那条流水线
    /// （<c>TradeWindow.cs:105-108</c>）：枚举之后**只筛** <c>category == Item &amp;&amp; !IsCorpse</c>，
    /// <c>StackedThings.GroupThings</c> 也只按 defName 分组、不碰 <c>Spawned</c>
    /// ⇒ 只要东西真进了 <c>HeldThings</c> 就必然显示。所以断点在我们这一侧，
    /// 而这条链上有两个可能断的地方：**组没进 AllGroups**、或**补丁没挂上**。两边都直接量。</para>
    ///
    /// <para>入口：选中核心 → 开发者模式下的 gizmo「[DEV] 兼容层自检」。定位完整文件删除。</para>
    /// </summary>
    public static class CompatSelfTest
    {
        private const string HarmonyId = "DigitalStorage.HarmonyPatches";

        public static void Run()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("[DS-COMPAT] ==== 兼容层自检 ====");

                Building_StorageCore core = Find.Selector.SingleSelectedThing as Building_StorageCore;
                if (core == null) core = CoreFinder.AllUsableCores(Find.CurrentMap).FirstOrFallback(null);
                if (core == null)
                {
                    sb.AppendLine("  找不到核心（选一个核心再点）。");
                    Log.Warning(sb.ToString());
                    return;
                }

                Map map = core.Map;
                sb.AppendLine("  核心=" + core.LabelShort
                    + "  图=" + (map == null ? "null" : map.ToString())
                    + "  IsPlayerHome=" + (map != null && map.IsPlayerHome)
                    + "  Spawned=" + core.Spawned);

                HaulDestinationManager mgr = map == null ? null : map.haulDestinationManager;
                SlotGroup group = core.GetSlotGroup();

                sb.AppendLine("  ① GetSlotGroup()=" + (group == null ? "null" : "非 null")
                    + "   group.parent 就是本核心=" + (group != null && ReferenceEquals(group.parent, core))
                    + "   CellsList.Count=" + (group == null ? -1 : group.CellsList.Count) + "（应为 0）");

                if (mgr == null)
                {
                    sb.AppendLine("  ② 无 haulDestinationManager（不在图上）");
                }
                else
                {
                    sb.AppendLine("  ② AllGroups 总数=" + mgr.AllGroupsListForReading.Count
                        + "   本核心的组在表里=" + mgr.AllGroupsListForReading.Contains(group)
                        + "   核心在 AllHaulDestinations 里=" + mgr.AllHaulDestinationsListForReading.Contains(core));
                }

                int total = 0, unspawned = 0;
                if (group != null)
                {
                    try
                    {
                        foreach (Thing t in group.HeldThings)
                        {
                            total++;
                            if (!t.Spawned) unspawned++;
                        }
                    }
                    catch (Exception e)
                    {
                        sb.AppendLine("  ★ HeldThings 枚举抛异常：" + e.GetType().Name + " " + e.Message);
                    }
                }
                sb.AppendLine("  ③ HeldThings 枚举出 " + total + " 件（其中未 Spawn = 容器内容物 " + unspawned
                    + " 件）   HeldThingsCount=" + (group == null ? -1 : group.HeldThingsCount));

                MethodInfo getter = AccessTools.PropertyGetter(typeof(SlotGroup), "HeldThings");
                int all = 0;
                bool ours = false;
                Patches info = getter == null ? null : Harmony.GetPatchInfo(getter);
                if (info != null && info.Postfixes != null)
                {
                    foreach (Patch p in info.Postfixes)
                    {
                        all++;
                        if (p != null && p.owner == HarmonyId) ours = true;
                    }
                }
                sb.AppendLine("  ④ SlotGroup.get_HeldThings 补丁：方法=" + (getter == null ? "没找到" : "找到")
                    + "  全量 postfix=" + all + "   本 mod 挂上了=" + ours);

                sb.AppendLine("  ⑤ " + PhinixState());
                Log.Warning(sb.ToString());
            }
            catch (Exception e)
            {
                Log.Warning("[DS-COMPAT] 自检自身异常：" + e);
            }
        }

        /// <summary>反射读 Phinix 的分支开关：true ⇒ 它走 listerThings，我们打不通。</summary>
        private static string PhinixState()
        {
            try
            {
                Type clientType = AccessTools.TypeByName("PhinixClient.Client");
                if (clientType == null) return "Phinix：未安装（找不到 PhinixClient.Client）";

                object instance = null;
                PropertyInfo instProp = clientType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (instProp != null) instance = instProp.GetValue(null);
                if (instance == null)
                {
                    FieldInfo instField = clientType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (instField != null) instance = instField.GetValue(null);
                }
                if (instance == null) return "Phinix：已安装但 Client.Instance 为 null（未初始化）";

                PropertyInfo settingsProp = clientType.GetProperty("Settings");
                object settings = settingsProp == null ? null : settingsProp.GetValue(instance);
                if (settings == null) return "Phinix：已安装但 Settings 读不到";

                PropertyInfo flag = settings.GetType().GetProperty("AllItemsTradable");
                if (flag == null) return "Phinix：Settings 里没有 AllItemsTradable 属性（版本不同）";

                object value = flag.GetValue(settings);
                return "Phinix：AllItemsTradable=" + value
                    + (Equals(value, true) ? "  ⇒ 它走 listerThings.AllThings 分支，**我们打不通**（需把它关掉）"
                                           : "  ⇒ 走 AllGroups → HeldThings 分支（我们能接）");
            }
            catch (Exception e)
            {
                return "Phinix：读取失败 " + e.GetType().Name + " " + e.Message;
            }
        }
    }
}
