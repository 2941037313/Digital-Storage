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

            var accesses = CoreFinder.AllUsableAccesses(pawn);
            if (accesses.Count == 0) return null;

            // 每个核心的可用物品列表（带代理核心）
            var allItems = new List<(Building_StorageCore ledgerCore, Building_StorageCore proxyCore, ItemKey key, long available)>();
            foreach (var access in accesses)
            {
                foreach (var kv in access.ledgerCore.Ledger.Stock)
                {
                    long avail = access.ledgerCore.Ledger.Available(kv.Key);
                    if (avail > 0)
                        allItems.Add((access.ledgerCore, access.proxyCore, kv.Key, avail));
                }
            }
            if (allItems.Count == 0) return null;

            return new FloatMenuOption("DS_WithdrawToSpot".Translate(), () =>
            {
                var subOptions = new List<FloatMenuOption>();
                foreach (var (ledgerCore, proxyCore, key, avail) in allItems)
                {
                    string label = "DS_WithdrawItemLabel".Translate(key.ToString(), avail, ledgerCore.NetworkName);
                    subOptions.Add(new FloatMenuOption(label, () =>
                    {
                        // 芯片 pawn 不走代理点
                        IntVec3 proxy = IntVec3.Invalid;
                        if (!Hediff_TerminalImplant.HasTerminalImplant(pawn))
                        {
                            proxy = CoreFinder.PickProxyCell(pawn, proxyCore);
                            if (!proxy.IsValid)
                            {
                                Messages.Message("DS_NoPathToCore".Translate(), MessageTypeDefOf.RejectInput, false);
                                return;
                            }
                        }

                        int maxCarry = pawn.carryTracker?.AvailableStackSpace(key.def) ?? 0;
                        Find.WindowStack.Add(new Dialog_WithdrawAmount(ledgerCore, key, maxCarry, amount =>
                        {
                            var job = JobMaker.MakeJob(DigitalStorage_JobDefOf.DigitalStorage_WithdrawToSpot,
                                context.ClickedCell);
                            job.SetTarget(TargetIndex.C, ledgerCore);
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

    }
}
