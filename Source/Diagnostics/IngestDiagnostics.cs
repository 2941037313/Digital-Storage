using System.Collections.Generic;
using System.Reflection;
using System.Text;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Diagnostics
{
    /// <summary>
    /// 自动收纳的「为什么没收这件东西」诊断（开发者模式 gizmo 触发，无常驻开销）。
    ///
    /// <para><b>纪律一：调用与运行时完全相同的那份判定</b>
    /// （<c>CompAutoIngest.RejectReason</c> / <c>WouldVanillaHaulIntoCore</c> /
    /// 同一个候选集 <c>listerHaulables</c>），绝不另写近似逻辑。</para>
    ///
    /// <para><b>纪律二：每个"闸门"都要给样本，且要给出能一眼看穿"全灭型故障"的分布数字。</b>
    /// 教训：曾出现"待搬表 98 件全部被同一道闸门拦掉"，而诊断当时只打了一张计数表 ——
    /// 计数的形状（98 全在同一格）根本没有暴露出来，白花一轮。所以现在每个拒因都带样本，
    /// 并且直接打 <c>Spawned / 非Spawned</c>、"HaulableEver 里有多少不在待搬表里" 这两组分布。</para>
    ///
    /// <para><b>纪律三：判断"某件东西为什么进不了候选集"要问原版自己</b>
    /// （反射调用私有的 <c>ListerHaulables.ShouldBeHaulable</c>），而不是把它的分支再抄一遍
    /// —— 抄一遍就会漂移，而漂移出来的假绿正是本 mod 反复吃过亏的地方。</para>
    /// </summary>
    public static class IngestDiagnostics
    {
        private static readonly string[] RejectNames =
        {
            "通过",
            "已销毁/空引用",
            "不在图上(背包/容器内)",
            "被预订",
            "刚被取出(保护窗口)"
        };

        private static readonly MethodInfo ShouldBeHaulableMethod =
            AccessTools.Method(typeof(ListerHaulables), "ShouldBeHaulable");

        public static void DumpFor(Building_StorageCore core)
        {
            if (core == null || core.Map == null)
            {
                Log.Warning("[DS-DIAG] 自动收纳诊断：核心不在图上或已销毁。");
                return;
            }

            Map map = core.Map;
            var sb = new StringBuilder();
            sb.AppendLine("[DS-DIAG] ==== 自动收纳诊断 :: 核心 @ " + core.PositionHeld + " ====");

            // ① 外层开关
            sb.AppendLine("  设置 autoIngestEnabled=" + Settings.DigitalStorageSettings.autoIngestEnabled);
            if (!core.Powered)
            {
                sb.AppendLine("  ⚠ 核心**没电** —— CompTick 第一行就会 return，什么都收不了。");
            }
            StorageSettings coreSettings = core.GetStoreSettings();
            sb.AppendLine("  核心 powered=" + core.Powered
                + " HaulDestinationEnabled=" + core.HaulDestinationEnabled
                + " **储存优先级=" + (coreSettings != null ? coreSettings.Priority.ToString() : "?") + "**"
                + " **被玩家方预约=" + map.reservationManager.IsReservedByAnyoneOf(core, Faction.OfPlayer) + "**"
                + " 栈=" + core.GetDirectlyHeldThings().Count + "/" + core.maxStacks);

            CompAutoIngest comp = core.GetComp<CompAutoIngest>();
            if (comp == null)
            {
                sb.AppendLine("  ⚠ 核心上**没有 CompAutoIngest**，自动收纳根本不会跑。");
            }
            else
            {
                sb.AppendLine("  CompAutoIngest: enabled=" + comp.Enabled
                    + " 已研究(AutoIngest1)=" + comp.IsResearched);
                if (!comp.IsResearched)
                {
                    sb.AppendLine("  ⚠ 自动收纳研究尚未完成 —— CompTick 会在 EnsureResearchCache 后 return。");
                }
            }

            // ② 与运行时同一个候选集 + 同一份过滤（每个拒因都给样本）
            var haulables = map.listerHaulables.ThingsPotentiallyNeedingHauling();
            int[] tally = new int[RejectNames.Length];
            var samples = new List<Thing>[RejectNames.Length];
            int spawnedInList = 0;
            int intoThisCore = 0;
            int intoOtherCore = 0;
            int noDestination = 0;
            var noDestSamples = new List<Thing>();

            foreach (Thing t in haulables)
            {
                if (t.Spawned) spawnedInList++;

                CompAutoIngest.Reject r = CompAutoIngest.RejectReason(t, map);
                tally[(int)r]++;
                if (r != CompAutoIngest.Reject.None)
                {
                    if (samples[(int)r] == null) samples[(int)r] = new List<Thing>();
                    if (samples[(int)r].Count < 4) samples[(int)r].Add(t);
                    continue;
                }

                Building_StorageCore dest = CompAutoIngest.WouldVanillaHaulIntoCore(map, t);
                if (dest == core) intoThisCore++;
                else if (dest != null) intoOtherCore++;
                else
                {
                    noDestination++;
                    if (noDestSamples.Count < 6) noDestSamples.Add(t);
                }
            }

            sb.AppendLine("  原版待搬表(listerHaulables)总数=" + haulables.Count
                + " —— **Spawned=" + spawnedInList + " / 非Spawned=" + (haulables.Count - spawnedInList) + "**"
                + "（非 Spawned 全会被过滤，这是正常的：容器内容物/背包物品）");
            for (int i = 0; i < tally.Length; i++)
            {
                if (tally[i] == 0) continue;
                sb.AppendLine("    " + RejectNames[i] + " = " + tally[i]);
                if (samples[i] == null) continue;
                for (int k = 0; k < samples[i].Count; k++)
                {
                    Thing s = samples[i][k];
                    sb.AppendLine("        · " + Describe(s));
                }
            }
            sb.AppendLine("    原版判定会搬进**本核心** = " + intoThisCore
                + "；搬进别的核心 = " + intoOtherCore
                + "；原版没有更好去处 = " + noDestination);

            // ③ 最内层：原版为什么不选核心 —— 打印优先级对比
            for (int i = 0; i < noDestSamples.Count; i++)
            {
                Thing t = noDestSamples[i];
                StoragePriority current = StoreUtility.CurrentStoragePriorityOf(t);
                StoreUtility.TryFindBestBetterStorageFor(t, null, map, current, Faction.OfPlayer,
                    out IntVec3 cell, out IHaulDestination dest, needAccurateResult: false);
                sb.AppendLine("    ✗ " + Describe(t)
                    + " 当前储存优先级=" + current
                    + " 原版选中的格子=" + cell
                    + " 原版选中的目的地=" + (dest == null ? "null(认为无处可去/已放好)" : dest.ToString())
                    + " 内层[不需预约]=" + InnerVerdict(t, map, current, requiresReservation: false)
                    + " 内层[需预约]=" + InnerVerdict(t, map, current, requiresReservation: true)
                    + " 核心Accept=" + core.Accepts(t)
                    + " 收得下=" + core.GetDirectlyHeldThings().GetCountCanAccept(t));
            }

            // ④ 反向对比：地图上的可搬物里，有多少**根本进不了**待搬表，以及原版为什么拒绝
            List<Thing> ever = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
            int everSpawned = 0;
            int neverListed = 0;
            var neverListedSamples = new List<Thing>();
            for (int i = 0; i < ever.Count; i++)
            {
                Thing t = ever[i];
                if (!t.Spawned) continue;
                everSpawned++;
                if (haulables.Contains(t)) continue;
                neverListed++;
                if (neverListedSamples.Count < 6) neverListedSamples.Add(t);
            }
            sb.AppendLine("  对比 HaulableEver(Spawned)=" + everSpawned
                + " 其中**不在待搬表里**=" + neverListed);
            for (int i = 0; i < neverListedSamples.Count; i++)
            {
                sb.AppendLine("    ✗ " + Describe(neverListedSamples[i])
                    + " 原版ShouldBeHaulable=" + Verdict(neverListedSamples[i], map)
                    + " 分支值[禁止=" + neverListedSamples[i].IsForbidden(Faction.OfPlayer)
                    + " alwaysHaulable=" + neverListedSamples[i].def.alwaysHaulable
                    + " EverHaulable=" + neverListedSamples[i].def.EverHaulable
                    + " 在任意储存=" + StoreUtility.IsInAnyStorage(neverListedSamples[i])
                    + " 在有效最优储存=" + StoreUtility.IsInValidBestStorage(neverListedSamples[i]) + "]");
            }

            Log.Warning(sb.ToString());
        }

        /// <summary>物品的一句话描述（带 holder 链，专门为了让"不在图上"这类结论可核对）。</summary>
        private static string Describe(Thing t)
        {
            if (t == null) return "<null>";
            return t.LabelShort + " x" + t.stackCount
                + " @ " + t.PositionHeld
                + " spawned=" + t.Spawned
                + " parent=" + (t.ParentHolder == null ? "null" : t.ParentHolder.GetType().Name)
                + " def=" + (t.def == null ? "null" : t.def.defName);
        }

        /// <summary>
        /// 内层函数（非格子型储存 = 容器那条腿）的判决。
        /// <c>requiresDestReservation</c> 取 true/false 的差别是本 mod 踩过的大坑：
        /// 为 true 时"目的地被任何玩家小人预约着"就会被跳过，而核心正是热门卸货点。
        /// </summary>
        private static string InnerVerdict(Thing t, Map map, StoragePriority current, bool requiresReservation)
        {
            try
            {
                bool ok = StoreUtility.TryFindBestBetterNonSlotGroupStorageFor(t, null, map, current,
                    Faction.OfPlayer, out IHaulDestination dest, acceptSamePriority: false,
                    requiresDestReservation: requiresReservation);
                if (!ok) return "false";
                return dest == null ? "true/null" : dest.GetType().Name;
            }
            catch (System.Exception e)
            {
                return "throw:" + e.GetType().Name;
            }
        }

        /// <summary>直接问原版（私有方法 ShouldBeHaulable），不抄它的分支。</summary>
        private static string Verdict(Thing t, Map map)
        {
            if (ShouldBeHaulableMethod == null) return "<?>";
            try
            {
                object ok = ShouldBeHaulableMethod.Invoke(map.listerHaulables, new object[] { t });
                return ok is bool b ? b.ToString() : "<?>";
            }
            catch (System.Exception e)
            {
                return "<反射失败:" + e.GetType().Name + ">";
            }
        }
    }
}
