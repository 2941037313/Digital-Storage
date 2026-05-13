using System.Collections.Generic;
using System.Text;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 存储核心 —— v3 账本建筑。
    ///
    /// 不再继承 Building_Storage：原版派工、StoreUtility、物品堆放统统看不到这个建筑。
    /// 所有库存由 CoreLedger 管理，纯数据。
    /// </summary>
    [StaticConstructorOnStartup]
    public class Building_StorageCore : Building, IRenameable
    {
        private static readonly Texture2D RenameTex = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", true);
        private static readonly Material LightMat = MaterialPool.MatFrom("2.0/一束光", ShaderDatabase.MoteGlow);
        private static readonly Material OrbMat = MaterialPool.MatFrom("2.0/一个球", ShaderDatabase.Cutout);

        private string networkName;
        private CompPowerTrader powerComp;
        private CompStorageCoreUpgrade upgradeComp;
        private CoreLedger ledger = new CoreLedger();
        private readonly List<Building_InputInterface> interfaces = new List<Building_InputInterface>();

        public CoreLedger Ledger => ledger;
        public IReadOnlyList<Building_InputInterface> Interfaces => interfaces;

        public void RegisterInterface(Building_InputInterface iface)
        {
            if (iface != null && !interfaces.Contains(iface)) interfaces.Add(iface);
        }

        public void DeregisterInterface(Building_InputInterface iface)
        {
            if (iface != null) interfaces.Remove(iface);
        }

        /// <summary>
        /// 代理点 = 所有已连接的接口的位置 + 自身交互格。
        /// 派工层用这个列表选 pawn 最近的落脚点。
        /// 没接口时核心自己顶上（第八章规则 2）。
        /// </summary>
        public IEnumerable<IntVec3> GetProxyCells()
        {
            for (int i = 0; i < interfaces.Count; i++)
            {
                var iface = interfaces[i];
                if (iface != null && iface.Spawned)
                {
                    yield return iface.Position;
                }
            }
            if (Spawned) yield return InteractionCell;
        }

        public string NetworkName
        {
            get => networkName ?? "DS_UnnamedNetwork".Translate().ToString();
            set => networkName = value;
        }

        public string RenamableLabel
        {
            get => networkName ?? LabelCapNoCount;
            set => networkName = value;
        }

        public string BaseLabel => LabelCapNoCount;
        public string InspectLabel => LabelCap;

        public bool Powered => powerComp != null && powerComp.PowerOn;

        public int GetCapacity()
        {
            return upgradeComp != null ? upgradeComp.GetCapacity() : 100;
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();
            upgradeComp = GetComp<CompStorageCoreUpgrade>();

            map.GetComponent<DigitalStorageMapComponent>()?.RegisterCore(this);
            Current.Game?.GetComponent<Services.DigitalStorageGameComponent>()?.RegisterCore(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map?.GetComponent<DigitalStorageMapComponent>()?.DeregisterCore(this);
            Current.Game?.GetComponent<Services.DigitalStorageGameComponent>()?.DeregisterCore(this);
            base.DeSpawn(mode);
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc, flip);

            Vector3 lightPos = drawLoc;
            lightPos.y = AltitudeLayer.BuildingOnTop.AltitudeFor();
            var lightMat = Matrix4x4.TRS(lightPos, Quaternion.identity, new Vector3(3f, 10f, 3f));
            Graphics.DrawMesh(MeshPool.plane10, lightMat, LightMat, 0);

            float floatOffset = Mathf.Sin(Time.realtimeSinceStartup * 2f) * 0.15f;
            Vector3 orbPos = drawLoc;
            orbPos.y = AltitudeLayer.BuildingOnTop.AltitudeFor() + 0.01f;
            orbPos.z += floatOffset;
            var orbMat = Matrix4x4.TRS(orbPos, Quaternion.identity, new Vector3(3f, 10f, 3f));
            Graphics.DrawMesh(MeshPool.plane10, orbMat, OrbMat, 0);
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;

            yield return new Command_Action
            {
                defaultLabel = "DS_RenameNetwork".Translate(),
                defaultDesc = "DS_RenameNetworkDesc".Translate(),
                icon = RenameTex,
                action = () => Find.WindowStack.Add(new Dialog_RenameNetwork(this))
            };

            // 临时 gizmo（阶段 4 派工完成后会删掉）
            yield return new Command_Action
            {
                defaultLabel = "DS_DebugIngestAdjacent".Translate(),
                defaultDesc = "DS_DebugIngestAdjacentDesc".Translate(),
                icon = TexCommand.ForbidOff,
                action = DebugIngestAdjacent
            };
        }

        /// <summary>临时调试：把核心相邻 8 格 + 自身 9 格里所有可吃物品吞进账本。</summary>
        private void DebugIngestAdjacent()
        {
            if (Map == null) return;
            int cap = GetCapacity();
            int total = 0;
            int rejected = 0;

            var cells = new List<IntVec3>();
            cells.AddRange(GenAdj.CellsOccupiedBy(this));
            foreach (var c in GenAdj.CellsAdjacent8Way(this)) cells.Add(c);

            foreach (var cell in cells)
            {
                if (!cell.InBounds(Map)) continue;
                var things = Map.thingGrid.ThingsListAtFast(cell);
                for (int i = things.Count - 1; i >= 0; i--)
                {
                    var t = things[i];
                    if (t.def.category != ThingCategory.Item) continue;
                    if (!LedgerPolicy.CanIngest(t)) { rejected++; continue; }
                    if (ledger.Ingest(t, cap)) total++;
                    else rejected++;
                }
            }
            Messages.Message("DS_DebugIngestResult".Translate(total, rejected), this, MessageTypeDefOf.NeutralEvent);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref networkName, "networkName");
            Scribe_Deep.Look(ref ledger, "ledger");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && ledger == null)
            {
                ledger = new CoreLedger();
            }
        }

        public override string GetInspectString()
        {
            var sb = new StringBuilder();
            string baseInspect = base.GetInspectString();
            if (!string.IsNullOrEmpty(baseInspect)) sb.AppendLine(baseInspect);
            sb.AppendLine("DS_InspectNetwork".Translate(NetworkName));
            sb.AppendLine("DS_InspectCapacity".Translate(ledger.UsedCapacity(), GetCapacity()));
            if (!Powered) sb.AppendLine("DS_NoPower".Translate());
            return sb.ToString().TrimEnd();
        }
    }
}
