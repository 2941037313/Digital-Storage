using System.Collections.Generic;
using System.Text;
using DigitalStorage.Components;
using RimWorld;
using Verse;

namespace DigitalStorage.Diagnostics
{
    /// <summary>
    /// 自动收纳的「为什么没收这件东西」诊断（开发者模式 gizmo 触发，无常驻开销）。
    ///
    /// <para><b>关键纪律：它必须调用与运行时完全相同的那份判定</b>
    /// （<c>CompAutoIngest.RejectReason</c> / <c>CompAutoIngest.WouldVanillaHaulIntoCore</c> /
    /// 同一个候选集 <c>listerHaulables</c>），绝不另写一套近似逻辑。本 mod 已经因为
    /// "诊断测的是上游闸门"误判过一轮（交易那次的 <c>PlayerSellableNow</c> vs
    /// <c>InSellablePosition</c>），平行实现的诊断只会制造新的假绿。</para>
    ///
    /// <para>输出分三层：①核心/设置/研究/供电；②原版待搬表里每一项被哪一步拦下；
    /// ③"过了过滤但原版不肯搬进核心"的样本 —— 打印核心与物品当前储存的**优先级对比**，
    /// 那就是原版目的地搜索的决胜依据。</para>
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

            // ② 与运行时同一个候选集 + 同一份过滤
            var haulables = map.listerHaulables.ThingsPotentiallyNeedingHauling();
            int[] tally = new int[RejectNames.Length];
            int intoThisCore = 0;
            int intoOtherCore = 0;
            int noDestination = 0;
            var samples = new List<Thing>();
            foreach (Thing t in haulables)
            {
                CompAutoIngest.Reject r = CompAutoIngest.RejectReason(t, map);
                tally[(int)r]++;
                if (r != CompAutoIngest.Reject.None) continue;

                Building_StorageCore dest = CompAutoIngest.WouldVanillaHaulIntoCore(map, t);
                if (dest == core) intoThisCore++;
                else if (dest != null) intoOtherCore++;
                else
                {
                    noDestination++;
                    if (samples.Count < 8) samples.Add(t);
                }
            }

            sb.AppendLine("  原版待搬表(listerHaulables)总数=" + haulables.Count);
            for (int i = 0; i < tally.Length; i++)
            {
                if (tally[i] == 0) continue;
                sb.AppendLine("    " + RejectNames[i] + " = " + tally[i]);
            }
            sb.AppendLine("    原版判定会搬进**本核心** = " + intoThisCore
                + "；搬进别的核心 = " + intoOtherCore
                + "；原版没有更好去处 = " + noDestination);

            // ③ 最内层：原版为什么不选核心 —— 打印优先级对比
            for (int i = 0; i < samples.Count; i++)
            {
                Thing t = samples[i];
                StoragePriority current = StoreUtility.CurrentStoragePriorityOf(t);
                StoreUtility.TryFindBestBetterStorageFor(t, null, map, current, Faction.OfPlayer,
                    out IntVec3 cell, out IHaulDestination dest, needAccurateResult: false);
                sb.AppendLine("    ✗ " + t.LabelShort + " x" + t.stackCount + " @ " + t.PositionHeld
                    + " 当前储存优先级=" + current
                    + " 原版选中的格子=" + cell
                    + " 原版选中的目的地=" + (dest == null ? "null(认为无处可去/已放好)" : dest.ToString())
                    + " 核心Accept=" + core.Accepts(t)
                    + " 收得下=" + core.GetDirectlyHeldThings().GetCountCanAccept(t));
            }

            Log.Warning(sb.ToString());
        }
    }
}
