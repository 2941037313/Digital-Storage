using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 低级储存区物品 → 高优先级核心。由原版 ThinkTree 调度，不自己 dispatch。
    /// 与 WorkGiver_DS_HaulToCore 互补：那个管地上物品，这个管已在储存区内的。
    /// </summary>
    public class WorkGiver_DS_StorageToCore : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;
        public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            var comp = DigitalStorageMapComponent.For(pawn.Map);
            if (comp == null) yield break;

            var cores = comp.GetAllCores();
            var groups = pawn.Map.haulDestinationManager.AllGroupsListInPriorityOrder;

            foreach (var core in cores)
            {
                if (core == null || !core.Powered) continue;
                if (core.storagePriority <= StoragePriority.Low) continue;

                foreach (var group in groups)
                {
                    if (group.Settings.Priority >= core.storagePriority) break;

                    foreach (var cell in group.CellsList)
                    {
                        var things = pawn.Map.thingGrid.ThingsListAt(cell);
                        for (int i = 0; i < things.Count; i++)
                        {
                            var t = things[i];
                            if (t.def.category != ThingCategory.Item) continue;
                            if (!LedgerPolicy.CanIngest(t)) continue;
                            if (!core.AllowsItem(t)) continue;
                            if (!core.Ledger.CanAccept(t, core.GetCapacity())) continue;
                            yield return t;
                        }
                    }
                }
            }
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            var comp = DigitalStorageMapComponent.For(pawn.Map);
            if (comp == null) return true;

            foreach (var core in comp.GetAllCores())
            {
                if (core == null || !core.Powered) continue;
                if (core.storagePriority <= StoragePriority.Low) continue;

                foreach (var group in pawn.Map.haulDestinationManager.AllGroupsListInPriorityOrder)
                {
                    if (group.Settings.Priority >= core.storagePriority) break;
                    foreach (var cell in group.CellsList)
                    {
                        var things = pawn.Map.thingGrid.ThingsListAt(cell);
                        for (int i = 0; i < things.Count; i++)
                        {
                            if (things[i].def.category == ThingCategory.Item)
                                return false;
                        }
                    }
                }
            }
            return true;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!LedgerPolicy.CanIngest(t)) return false;
            if (t.IsForbidden(pawn.Faction)) return false;
            if (pawn.Map.reservationManager.IsReserved(t)) return false;

            var sg = t.GetSlotGroup();
            if (sg == null) return false;

            return FindCoreForItem(pawn, t, sg) != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            var sg = t.GetSlotGroup();
            var core = FindCoreForItem(pawn, t, sg);
            if (core == null) return null;

            var job = JobMaker.MakeJob(
                DigitalStorage_JobDefOf.DigitalStorage_IngestToCore, t);
            job.SetTarget(TargetIndex.C, core);
            job.count = t.stackCount;
            return job;
        }

        private static Building_StorageCore FindCoreForItem(Pawn pawn, Thing t,
            SlotGroup sg)
        {
            var comp = DigitalStorageMapComponent.For(pawn.Map);
            if (comp == null) return null;

            StoragePriority itemPrio = sg.Settings.Priority;

            foreach (var core in comp.GetAllCores())
            {
                if (core == null || !core.Powered) continue;
                if (core.storagePriority <= itemPrio) continue;
                if (!core.AllowsItem(t)) continue;
                if (!core.Ledger.CanAccept(t, core.GetCapacity())) continue;
                if (!pawn.CanReach(t, PathEndMode.ClosestTouch, Danger.Deadly))
                    continue;
                return core;
            }
            return null;
        }
    }
}
