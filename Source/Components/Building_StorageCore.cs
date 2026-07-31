using System.Collections.Generic;
using System.Linq;
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
        private StoragePriority storagePriorityField = StoragePriority.Normal;

        /// <summary>
        /// X4: 优先级改属性——setter 触发 Ghost 刷新通知。
        /// 旧实现直接赋值字段，在途工单/UI 缓存不知道优先级变了。
        /// </summary>
        public StoragePriority storagePriority
        {
            get => storagePriorityField;
            set
            {
                if (storagePriorityField == value) return;
                storagePriorityField = value;
                Map?.GetComponent<Ghost.GhostLedgerIndex>()?.OnCoreStateChanged(this);
            }
        }
        private readonly List<Building_InputInterface> interfaces = new List<Building_InputInterface>();
        private ThingFilter storageFilter;
        private static ThingFilter parentFilter;

        private static ThingFilter GetParentFilter()
        {
            if (parentFilter == null)
            {
                parentFilter = new ThingFilter();
                // 正向添加：只允许可存储的根类别
                foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
                {
                    if (def.category == ThingCategory.Item && !def.IsCorpse && LedgerPolicy.CanIngest(def))
                        parentFilter.SetAllow(def, true);
                }
            }
            return parentFilter;
        }

        public CoreLedger Ledger => ledger;
        public ThingFilter StorageFilter => storageFilter;
        public ThingFilter GetParentFilterPublic() => GetParentFilter();
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
            bool hasInterface = false;
            for (int i = 0; i < interfaces.Count; i++)
            {
                var iface = interfaces[i];
                if (iface != null && iface.Spawned)
                {
                    hasInterface = true;
                    yield return iface.Position;
                }
            }
            // 只有没有接口时才用核心自身作为代理点
            if (!hasInterface && Spawned) yield return InteractionCell;
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

        public bool AllowsItem(Thing t)
        {
            if (storageFilter == null) return true;
            return storageFilter.Allows(t.def);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();
            upgradeComp = GetComp<CompStorageCoreUpgrade>();

            if (storageFilter == null)
            {
                storageFilter = new ThingFilter();
                storageFilter.SetAllowAll(GetParentFilter());
            }

            map.GetComponent<DigitalStorageMapComponent>()?.RegisterCore(this);
            Current.Game?.GetComponent<Services.DigitalStorageGameComponent>()?.RegisterCore(this);
            map.GetComponent<Ghost.GhostLedgerIndex>()?.RegisterCore(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // I6a: 摧毁前转移库存到存活核心
            if (mode == DestroyMode.Deconstruct || mode == DestroyMode.KillFinalize)
                TransferOrQueueDrop();

            Map?.GetComponent<Ghost.GhostLedgerIndex>()?.UnregisterCore(this);
            Map?.GetComponent<Ghost.GhostLedgerIndex>()?.OnCoreStateChanged(this);
            Map?.GetComponent<DigitalStorageMapComponent>()?.DeregisterCore(this);
            Current.Game?.GetComponent<Services.DigitalStorageGameComponent>()?.DeregisterCore(this);
            base.DeSpawn(mode);
        }

        private void TransferOrQueueDrop()
        {
            if (ledger == null) return;
            var map = Map;
            if (map == null) return;

            var mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) return;

            // 找同图其他 powered 核心，按剩余容量排序
            var targets = mapComp.GetAllCores()
                .Where(c => c != this && c.Spawned && c.Powered)
                .OrderByDescending(c => c.GetCapacity() - c.Ledger.UsedCapacity())
                .ToList();

            var keysToTransfer = ledger.AllKeys().ToList();
            var overflow = new List<Core.ItemKey>();

            foreach (var key in keysToTransfer)
            {
                long amount = ledger.StockOf(key);
                if (amount <= 0) continue;

                foreach (var target in targets)
                {
                    int remaining = target.GetCapacity() - target.Ledger.UsedCapacity();
                    if (remaining <= 0) continue;
                    // 新 key 需要 1 组容量；已存在的 key 不占新组
                    if (target.Ledger.StockOf(key) <= 0 && remaining < 1) continue;

                    // 转移（AddRaw 会 fire StockChanged → Ghost 更新）
                    // X2: 加给目标后立即从源账本扣减——修复拆核心再放回库存翻倍
                    target.Ledger.AddRaw(key, amount);
                    ledger.RemoveRaw(key, amount);
                    amount = 0;
                    break;
                }

                if (amount > 0)
                    overflow.Add(key);
            }

            // I6b: 溢出部分加入分帧掉落队列（X2: Enqueue 时同步扣源账本，防止重复）
            if (overflow.Count > 0)
            {
                var dropQueue = map.GetComponent<CoreDestroyDropQueue>();
                if (dropQueue != null)
                {
                    foreach (var key in overflow)
                    {
                        long amount = ledger.StockOf(key);
                        if (amount > 0)
                        {
                            dropQueue.Enqueue(key, amount, Position);
                            ledger.RemoveRaw(key, amount);
                        }
                    }
                }
            }
        }

        protected override void ReceiveCompSignal(string signal)
        {
            base.ReceiveCompSignal(signal);
            if (signal == "PowerTurnedOn" || signal == "PowerTurnedOff")
                Map?.GetComponent<Ghost.GhostLedgerIndex>()?.OnCoreStateChanged(this);
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
                defaultLabel = "DS_StorageFilter".Translate(),
                defaultDesc = "DS_StorageFilterDesc".Translate(),
                icon = ContentFinder<UnityEngine.Texture2D>.Get("UI/Commands/SetTargetFuelLevel", true),
                action = () => Find.WindowStack.Add(new UI.Dialog_StorageFilter(this))
            };

            yield return new Command_Action
            {
                defaultLabel = "DS_RenameNetwork".Translate(),
                defaultDesc = "DS_RenameNetworkDesc".Translate(),
                icon = RenameTex,
                action = () => Find.WindowStack.Add(new Dialog_RenameNetwork(this))
            };

            // I5b: 自动收纳开关（研究后可见）
            var autoIngest = GetComp<CompAutoIngest>();
            if (autoIngest != null && autoIngest.IsResearched)
            {
                yield return new Command_Toggle
                {
                    defaultLabel = "DS_AutoIngest".Translate(),
                    defaultDesc = "DS_AutoIngestDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("收纳", true),
                    isActive = () => autoIngest.Enabled,
                    toggleAction = () => autoIngest.Enabled = !autoIngest.Enabled
                };
            }

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
            Scribe_Values.Look(ref storagePriorityField, "storagePriority", StoragePriority.Normal);
            Scribe_Deep.Look(ref ledger, "ledger");
            Scribe_Deep.Look(ref storageFilter, "storageFilter");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && ledger == null)
            {
                ledger = new CoreLedger();
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit && storageFilter == null)
            {
                storageFilter = new ThingFilter();
                storageFilter.SetAllowAll(null);
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
