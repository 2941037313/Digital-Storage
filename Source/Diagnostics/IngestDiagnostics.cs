using System.Collections.Generic;
using System.Text;
using DigitalStorage.AI;
using DigitalStorage.Components;
using RimWorld;
using Verse;

namespace DigitalStorage.Diagnostics
{
    /// <summary>
    /// 自动收纳的「为什么没收这件东西」诊断（开发者模式 gizmo 触发，无常驻开销）。
    ///
    /// <para><b>关键纪律：它必须调用与过滤完全相同的那份判定</b>
    /// （<c>CompAutoIngest.RejectReason</c> / <c>FindBestIngestCore</c>），
    /// 绝不另写一套近似逻辑。本 mod 已经因为"诊断测的是上游闸门"而误判过一轮
    /// （交易那次的 <c>PlayerSellableNow</c> vs <c>InSellablePosition</c>），
    /// 平行实现的诊断只会制造新的假绿。</para>
    ///
    /// <para>输出分三层，从外到内：①核心/设置/研究是否满足；②全图 HaulableEver 里
    /// 每一项被哪一道闸门拦下（含"任何核心都不收"）；③对"通过了过滤但没人收"的样本，
    /// 打印目标核心的过滤器/栈位/容量判定。</para>
    /// </summary>
    public static class IngestDiagnostics
    {
        private static readonly string[] RejectNames =
        {
            "通过",
            "已销毁/空引用",
            "不是物品",
            "未开采矿脉(Mineable)",
            "被禁止",
            "被预订",
            "刚被取出(保护窗口)",
            "在更高优先级的储存里"
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
            sb.AppendLine("  核心 powered=" + core.Powered
                + " HaulDestinationEnabled=" + core.HaulDestinationEnabled
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

            // ② 逐项过滤统计（与运行时同一份判定）
            StoragePriority maxPrio = StoragePriority.Unstored;
            foreach (Building_StorageCore c in CoreFinder.AllUsableCores(map))
            {
                if (c.storagePriority > maxPrio) maxPrio = c.storagePriority;
            }
            sb.AppendLine("  全图最高核心优先级=" + maxPrio
                + " 核心数=" + CoreFinder.AllUsableCores(map).Count);

            List<Thing> all = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
            int[] tally = new int[RejectNames.Length];
            int passedButNoCore = 0;
            var noCoreSamples = new List<Thing>();
            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                CompAutoIngest.Reject r = CompAutoIngest.RejectReason(t, map, maxPrio);
                tally[(int)r]++;
                if (r != CompAutoIngest.Reject.None) continue;
                if (CompAutoIngest.FindBestIngestCore(map, t) == null)
                {
                    passedButNoCore++;
                    if (noCoreSamples.Count < 8) noCoreSamples.Add(t);
                }
            }

            sb.AppendLine("  HaulableEver 总数=" + all.Count + "（" + map + "）");
            for (int i = 0; i < tally.Length; i++)
            {
                if (tally[i] == 0) continue;
                sb.AppendLine("    " + RejectNames[i] + " = " + tally[i]);
            }
            sb.AppendLine("    通过了过滤但**任何核心都不收** = " + passedButNoCore
                + "（收得下的 = " + (tally[0] - passedButNoCore) + "）");
            sb.AppendLine("  全图钢铁=" + map.listerThings.ThingsOfDef(ThingDefOf.Steel).Count + " 件");

            // ③ 最内层：为什么没有核心收
            for (int i = 0; i < noCoreSamples.Count; i++)
            {
                Thing t = noCoreSamples[i];
                StorageSettings st = core.GetStoreSettings();
                bool filterAllows = st == null || st.filter == null || st.filter.Allows(t);
                ThingOwner held = core.GetDirectlyHeldThings();
                sb.AppendLine("    ✗ " + t.LabelShort + " x" + t.stackCount + " @ " + t.PositionHeld
                    + " 禁止=" + t.IsForbidden(Faction.OfPlayer)
                    + " 过滤器允许=" + filterAllows
                    + " 栈数=" + held.Count + "/" + core.maxStacks
                    + " 收得下=" + held.GetCountCanAccept(t)
                    + " Accepts=" + core.Accepts(t));
            }

            Log.Warning(sb.ToString());
        }
    }
}
