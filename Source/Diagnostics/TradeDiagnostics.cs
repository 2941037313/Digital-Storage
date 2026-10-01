using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Diagnostics
{
    /// <summary>
    /// 【临时诊断脚手架 —— 只读，不改任何逻辑】
    ///
    /// 目的：把"交易面板我方完全空白"这个故障拆成**可判定的几个闸门**，
    /// 一次测试就能定位到底卡在哪一层，而不是继续猜。
    ///
    /// <para>要回答的问题（按闸门顺序）：</para>
    /// <list type="number">
    /// <item><b>跑的到底是哪个 DLL？</b> —— 打印运行程序集的路径 / 写入时间 / 大小 / 版本。
    ///   用户上一次实测时，安装目录里的 DLL 比源码旧（82944 @ 22:47 vs 源码 83456 @ 23:10），
    ///   所以"没看到 ErrorOnce 标记"**不能**证明补丁没抛异常 —— 旧 DLL 里根本没有那段。</item>
    /// <item><b>补丁挂上了吗？</b> —— 用 <c>Harmony.GetPatchInfo</c> 直接查两条目标方法的 prefix/postfix 数。</item>
    /// <item><b>核心在两个注册表里吗？</b> —— 分别用
    ///   <c>listerThings.AllThings</c> / <c>listerBuildings.AllBuildingsColonistOfClass</c> /
    ///   <c>haulDestinationManager.AllHaulSourcesListForReading</c> 三条互相独立的途径找同一个核心，
    ///   看它出现在哪几条里。**这是本次故障的核心待证命题。**</item>
    /// <item><b>内容物被原版判定为可卖吗？</b> —— 分别以"真实 trader"和 <c>null</c> 调
    ///   <c>TradeUtility.PlayerSellableNow</c> 计数（原版自己在 <c>FactionDialogMaker</c> /
    ///   <c>ColonyHasEnoughSilver</c> 里就传 null）。</item>
    /// <item><b>商队那条路可达吗？</b> —— 原版
    ///   <c>Pawn_TraderTracker.ColonyThingsWillingToBuy:124-128</c> 对每个
    ///   <c>IHaulSource</c> 要求 <c>ReachableForTrade</c>（<c>CanReach</c> 到建筑格）。
    ///   封闭房间里的核心会被静默跳过。</item>
    /// </list>
    ///
    /// <para>全部输出用 <see cref="Log.Warning"/> —— <c>Log.Message</c> 在游戏内日志窗口**不显示**，
    /// 只有 Player.log 有。放这里就是为了不用翻文件。</para>
    /// </summary>
    public static class TradeDiagnostics
    {
        private static readonly HashSet<string> firedKeys = new HashSet<string>();
        private static bool versionLogged;

        /// <summary>本次运行内同一个 key 只返回一次 true（避免每 tick / 每个商人刷屏）。</summary>
        public static bool FirstTime(string key)
        {
            if (key == null) return false;
            if (firedKeys.Contains(key)) return false;
            firedKeys.Add(key);
            return true;
        }

        // ===================================================================
        // 闸门 1+2：运行的程序集 & 补丁状态
        // ===================================================================

        public static void LogVersionOnce()
        {
            if (versionLogged) return;
            versionLogged = true;

            try
            {
                Assembly asm = typeof(TradeDiagnostics).Assembly;
                string loc = asm.Location;
                string stamp = "?";
                long size = -1;
                try
                {
                    if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                    {
                        stamp = File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm:ss");
                        size = new FileInfo(loc).Length;
                    }
                }
                catch
                {
                    // 只读诊断，取不到时间不影响结论
                }

                Log.Warning("[DS-DIAG] 运行程序集=" + loc
                    + " 写入时间=" + stamp
                    + " 大小=" + size
                    + " 版本=" + (asm.GetName().Version != null ? asm.GetName().Version.ToString() : "?")
                    + " DevMode=" + Prefs.DevMode);

                Log.Warning("[DS-DIAG] 补丁状态: TradeUtility.AllLaunchableThingsForTrade["
                    + PatchState(typeof(TradeUtility), "AllLaunchableThingsForTrade")
                    + "] Pawn_TraderTracker.ColonyThingsWillingToBuy["
                    + PatchState(typeof(Pawn_TraderTracker), "ColonyThingsWillingToBuy")
                    + "] Building_StorageCore.GetDirectlyHeldThings["
                    + PatchState(typeof(Building_StorageCore), "GetDirectlyHeldThings") + "]");
            }
            catch (Exception e)
            {
                Log.Warning("[DS-DIAG] 版本诊断自身失败: " + e);
            }
        }

        private static string PatchState(Type type, string methodName)
        {
            try
            {
                MethodInfo mi = AccessTools.Method(type, methodName);
                if (mi == null) return "找不到方法";
                Patches info = Harmony.GetPatchInfo(mi);
                if (info == null) return "未补丁";
                return "pre=" + info.Prefixes.Count + " post=" + info.Postfixes.Count;
            }
            catch (Exception e)
            {
                return "查询失败(" + e.GetType().Name + ")";
            }
        }

        // ===================================================================
        // 闸门 3+4+5：注册表 / 内容物 / 可达性
        // ===================================================================

        /// <summary>把所有玩家地图上的存储核心状态打成**一条** Warning。只读。</summary>
        public static void DumpCoreState(string tag, ITrader trader)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("[DS-DIAG] ==== 存储核心诊断 :: ").Append(tag).AppendLine(" ====");
                sb.Append("商人=").Append(DescribeTrader(trader)).AppendLine();

                List<Map> maps = Find.Maps;
                for (int mi = 0; mi < maps.Count; mi++)
                {
                    Map map = maps[mi];
                    if (map == null) continue;

                    DumpMap(sb, mi, map, trader);
                }

                Log.Warning(sb.ToString());
            }
            catch (Exception e)
            {
                Log.Warning("[DS-DIAG] 诊断自身失败: " + e);
            }
        }

        private static void DumpMap(StringBuilder sb, int index, Map map, ITrader trader)
        {
            List<IHaulSource> sources = null;
            try { sources = map.haulDestinationManager != null ? map.haulDestinationManager.AllHaulSourcesListForReading : null; }
            catch (Exception e) { sb.Append("  AllHaulSourcesListForReading 读取失败: ").Append(e.GetType().Name).AppendLine(); }

            int colonistSourceCount = 0;
            var colonistSources = new List<IHaulSource>();
            foreach (IHaulSource s in map.listerBuildings.AllColonistBuildingsOfType<IHaulSource>())
            {
                colonistSourceCount++;
                colonistSources.Add(s);
            }

            // 途径 A：listerThings（未 Spawned 的容器内容物不会在这里，但容器本身应该在）
            var viaListerThings = new List<Building_StorageCore>();
            List<Thing> allThings = map.listerThings.AllThings;
            for (int i = 0; i < allThings.Count; i++)
            {
                var c = allThings[i] as Building_StorageCore;
                if (c != null) viaListerThings.Add(c);
            }

            // 途径 B：allBuildingsColonist（商队路径用的就是这条）
            var viaColonistBuildings = new List<Building_StorageCore>();
            foreach (Building b in map.listerBuildings.AllBuildingsColonistOfClass<Building>())
            {
                var c = b as Building_StorageCore;
                if (c != null) viaColonistBuildings.Add(c);
            }

            // 途径 C：haulDestinationManager 的 haul source 列表（轨道补丁用的就是这条）
            var viaHaulSourceList = new List<Building_StorageCore>();
            if (sources != null)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    var c = sources[i] as Building_StorageCore;
                    if (c != null) viaHaulSourceList.Add(c);
                }
            }

            if (viaListerThings.Count == 0 && viaColonistBuildings.Count == 0 && viaHaulSourceList.Count == 0)
                return; // 这张图上没有存储核心，不用刷屏

            sb.Append("地图[").Append(index).Append("] ").Append(map).AppendLine();
            sb.Append("  haulSources总数=").Append(sources != null ? sources.Count : -1)
              .Append(" colonistIHaulSources=").Append(colonistSourceCount)
              .Append(" allThings=").Append(allThings.Count).AppendLine();
            sb.Append("  按途径找到的核心数: listerThings=").Append(viaListerThings.Count)
              .Append(" allBuildingsColonist=").Append(viaColonistBuildings.Count)
              .Append(" haulSourceList=").Append(viaHaulSourceList.Count).AppendLine();

            var seen = new List<Building_StorageCore>();
            AddUnique(seen, viaListerThings);
            AddUnique(seen, viaColonistBuildings);
            AddUnique(seen, viaHaulSourceList);

            for (int i = 0; i < seen.Count; i++)
            {
                Building_StorageCore c = seen[i];
                ThingOwner held = null;
                try { held = c.GetDirectlyHeldThings(); }
                catch (Exception e) { sb.Append("     GetDirectlyHeldThings 抛异常: ").Append(e).AppendLine(); }

                int sellableTrader = 0, silverTrader = 0, sellableNull = 0, silverNull = 0;
                CountSellable(held, trader, out sellableTrader, out silverTrader);
                CountSellable(held, null, out sellableNull, out silverNull);

                sb.Append("  #").Append(i)
                  .Append(" pos=").Append(c.Position)
                  .Append(" spawned=").Append(c.Spawned)
                  .Append(" mapHeld=").Append(c.MapHeld == null ? "null" : "ok")
                  .Append(" faction=").Append(c.Faction == null ? "null" : c.Faction.Name)
                  .Append(" 供电=").Append(c.Powered)
                  .AppendLine();
                sb.Append("     HaulSourceEnabled=").Append(c.HaulSourceEnabled)
                  .Append(" HaulDestinationEnabled=").Append(c.HaulDestinationEnabled)
                  .Append(" 栈=").Append(held != null ? held.Count : -1)
                  .Append(" 单位=").Append(held != null ? held.TotalStackCount : -1).AppendLine();
                sb.Append("     可卖出(真实trader)=").Append(sellableTrader).Append(" 其中白银=").Append(silverTrader)
                  .Append(" | 可卖出(trader=null)=").Append(sellableNull).Append(" 其中白银=").Append(silverNull).AppendLine();
                sb.Append("     ∈listerThings=").Append(Contains(viaListerThings, c))
                  .Append(" ∈allBuildingsColonist=").Append(Contains(viaColonistBuildings, c))
                  .Append(" ∈haulSourceList=").Append(Contains(viaHaulSourceList, c))
                  .Append(" ∈colonistIHaulSources=").Append(ContainsSource(colonistSources, c)).AppendLine();
                AppendSample(sb, held);
            }
        }

        /// <summary>原版 <c>Pawn_TraderTracker.ReachableForTrade</c> 的近似复刻（private，只能自己算）。</summary>
        public static bool ApproxReachableForTrade(Pawn pawn, Thing thing)
        {
            if (pawn == null || thing == null || pawn.Map == null) return false;
            try
            {
                if (pawn.Map != thing.MapHeld) return false;
                return pawn.Map.reachability.CanReach(pawn.Position, thing, PathEndMode.Touch,
                    TraverseParms.For(TraverseMode.PassDoors));
            }
            catch
            {
                return false;
            }
        }

        private static void CountSellable(ThingOwner held, ITrader trader, out int sellable, out int silver)
        {
            sellable = 0;
            silver = 0;
            if (held == null) return;

            for (int i = 0; i < held.Count; i++)
            {
                Thing t = held[i];
                if (t == null || t.def == null) continue;
                bool ok;
                try { ok = TradeUtility.PlayerSellableNow(t, trader); }
                catch { ok = false; }
                if (!ok) continue;

                sellable++;
                if (t.def == ThingDefOf.Silver) silver++;
            }
        }

        private static void AppendSample(StringBuilder sb, ThingOwner held)
        {
            if (held == null || held.Count == 0)
            {
                sb.Append("     （容器为空）").AppendLine();
                return;
            }

            sb.Append("     前几件: ");
            int shown = 0;
            for (int i = 0; i < held.Count && shown < 6; i++)
            {
                Thing t = held[i];
                if (t == null) continue;
                if (shown > 0) sb.Append(", ");
                sb.Append(t.def != null ? t.def.defName : "?")
                  .Append("×").Append(t.stackCount)
                  .Append("(spawned=").Append(t.Spawned).Append(")");
                shown++;
            }
            sb.AppendLine();
        }

        private static string DescribeTrader(ITrader trader)
        {
            if (trader == null) return "null";
            try
            {
                return trader.GetType().Name
                    + (trader.TraderKind != null ? "/" + trader.TraderKind.defName : "");
            }
            catch
            {
                return trader.GetType().Name;
            }
        }

        private static void AddUnique(List<Building_StorageCore> list, List<Building_StorageCore> add)
        {
            for (int i = 0; i < add.Count; i++)
                if (!list.Contains(add[i])) list.Add(add[i]);
        }

        private static bool Contains(List<Building_StorageCore> list, Building_StorageCore c)
        {
            return list.Contains(c);
        }

        private static bool ContainsSource(List<IHaulSource> list, Building_StorageCore c)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], c)) return true;
            return false;
        }
    }
}
