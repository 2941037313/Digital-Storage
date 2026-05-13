using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.UI
{
    /// <summary>
    /// 阶段 4.4：右键地图空格 → "从数字存储取出…" → 选物品+数量 → 派 JobDriver_DS_Withdraw。
    /// 原版 FloatMenuOptionProvider 扩展点，零 Harmony。
    /// </summary>
    public class FloatMenuOptionProvider_DS_Withdraw : FloatMenuOptionProvider
    {
        protected override bool Drafted => false;
        protected override bool Undrafted => true;
        protected override bool Multiselect => false;

        protected override FloatMenuOption GetSingleOption(FloatMenuContext context)
        {
            var pawn = context.FirstSelectedPawn;
            if (pawn == null) return null;

            var cores = GetUsableCores(pawn.Map);
            if (cores.Count == 0) return null;

            // 每个核心的可用物品列表
            var allItems = new List<(Building_StorageCore core, ItemKey key, long available)>();
            foreach (var core in cores)
            {
                foreach (var kv in core.Ledger.Stock)
                {
                    long avail = core.Ledger.Available(kv.Key);
                    if (avail > 0)
                        allItems.Add((core, kv.Key, avail));
                }
            }
            if (allItems.Count == 0) return null;

            return new FloatMenuOption("DS_WithdrawToSpot".Translate(), () =>
            {
                var subOptions = new List<FloatMenuOption>();
                foreach (var (core, key, avail) in allItems)
                {
                    string label = $"DS_WithdrawItemLabel".Translate(key.ToString(), avail, core.NetworkName);
                    subOptions.Add(new FloatMenuOption(label, () =>
                    {
                        // 芯片 pawn 不走代理点
                        IntVec3 proxy = IntVec3.Invalid;
                        if (!Hediff_TerminalImplant.HasTerminalImplant(pawn))
                        {
                            proxy = PickProxyCell(pawn, core);
                            if (!proxy.IsValid)
                            {
                                Messages.Message("DS_NoPathToCore".Translate(), MessageTypeDefOf.RejectInput, false);
                                return;
                            }
                        }

                        int maxCarry = pawn.carryTracker?.AvailableStackSpace(key.def) ?? 0;
                        Find.WindowStack.Add(new Dialog_WithdrawAmount(core, key, maxCarry, amount =>
                        {
                            var job = JobMaker.MakeJob(DigitalStorage_JobDefOf.DigitalStorage_WithdrawToSpot,
                                context.ClickedCell);
                            job.SetTarget(TargetIndex.C, core);
                            if (proxy.IsValid) job.SetTarget(TargetIndex.B, proxy);
                            JobDriver_DS_Withdraw.SetPendingPlan(job, key, amount);
                            pawn.jobs.TryTakeOrderedJob(job, JobTag.MiscWork);
                        }));
                    }));
                }
                if (subOptions.Count > 0)
                    Find.WindowStack.Add(new FloatMenu(subOptions));
            });
        }

        private static List<Building_StorageCore> GetUsableCores(Map map)
        {
            var result = new List<Building_StorageCore>();
            var mapComp = map?.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return result;
            foreach (var c in mapComp.GetAllCores())
            {
                if (c != null && c.Spawned && !c.Destroyed && c.Powered)
                    result.Add(c);
            }
            return result;
        }

        private static IntVec3 PickProxyCell(Pawn pawn, Building_StorageCore core)
        {
            IntVec3 best = IntVec3.Invalid;
            int bestDist = int.MaxValue;
            foreach (var c in core.GetProxyCells())
            {
                if (!c.InBounds(pawn.Map)) continue;
                if (!pawn.CanReach(c, PathEndMode.Touch, Danger.Deadly)) continue;
                int d = (c - pawn.Position).LengthManhattan;
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }
    }
}
