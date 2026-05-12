using System.Collections.Generic;
using System.Text;
using DigitalStorage.Services;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 输入接口 —— v3 纯交互建筑。
    /// 不再继承 Building_Storage：原版 haul/StoreUtility 看不到它，不会被乱扔物品堵口。
    /// 作用是给 pawn 一个"走到这里伸手"的真实坐标，由核心代理点列表调度。
    /// </summary>
    public class Building_InputInterface : Building
    {
        private Building_StorageCore boundCore;
        private CompPowerTrader powerComp;
        private string savedCoreNetworkName;

        public Building_StorageCore BoundCore => boundCore;
        public bool Powered => powerComp != null && powerComp.PowerOn;
        public bool IsActive => Powered && boundCore != null && boundCore.Powered;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();

            if (boundCore == null && Map != null)
            {
                TryAutoConnect();
                if (boundCore == null && !string.IsNullOrEmpty(savedCoreNetworkName))
                {
                    LongEventHandler.ExecuteWhenFinished(() =>
                    {
                        if (Spawned && boundCore == null)
                        {
                            TryAutoConnect();
                        }
                    });
                }
            }

            boundCore?.RegisterInterface(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            boundCore?.DeregisterInterface(this);
            if (mode == DestroyMode.Vanish || mode == DestroyMode.WillReplace)
            {
                boundCore = null;
            }
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref boundCore, "boundCore");
            Scribe_Values.Look(ref savedCoreNetworkName, "savedCoreNetworkName");
        }

        public override string GetInspectString()
        {
            var sb = new StringBuilder();
            string baseStr = base.GetInspectString();
            if (!string.IsNullOrEmpty(baseStr))
            {
                sb.AppendLine(baseStr);
            }

            if (boundCore != null)
            {
                sb.AppendLine("DS_ConnectedTo".Translate(boundCore.NetworkName));
            }
            else
            {
                sb.AppendLine("DS_NotConnected".Translate());
            }

            if (!Powered)
            {
                sb.AppendLine("DS_NoPower".Translate());
            }
            return sb.ToString().TrimEnd();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            yield return new Command_Action
            {
                defaultLabel = "DS_ConnectToCore".Translate(),
                defaultDesc = "DS_ConnectToCoreDescInterface".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", true),
                action = () =>
                {
                    var options = new List<FloatMenuOption>();
                    var gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
                    if (gameComp != null)
                    {
                        foreach (var core in gameComp.GetAllCores())
                        {
                            if (core != null && core.Spawned && core.Map == Map)
                            {
                                var localCore = core;
                                options.Add(new FloatMenuOption(localCore.NetworkName, () => SetBoundCore(localCore)));
                            }
                        }
                    }
                    if (options.Count == 0)
                    {
                        options.Add(new FloatMenuOption("DS_NoCoresAvailable".Translate(), null));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };
        }

        public void SetBoundCore(Building_StorageCore core)
        {
            if (boundCore == core) return;
            boundCore?.DeregisterInterface(this);
            boundCore = core;
            savedCoreNetworkName = core?.NetworkName;
            if (Spawned) boundCore?.RegisterInterface(this);
        }

        private void TryAutoConnect()
        {
            if (Map == null) return;

            if (!string.IsNullOrEmpty(savedCoreNetworkName))
            {
                var gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
                if (gameComp != null)
                {
                    foreach (var core in gameComp.GetAllCores())
                    {
                        if (core != null && core.Spawned && core.Map == Map && core.NetworkName == savedCoreNetworkName)
                        {
                            SetBoundCore(core);
                            return;
                        }
                    }
                }
            }

            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(this))
            {
                if (!cell.InBounds(Map)) continue;
                if (cell.GetFirstBuilding(Map) is Building_StorageCore core)
                {
                    SetBoundCore(core);
                    return;
                }
            }
        }
    }
}
