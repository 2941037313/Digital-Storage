using System.Collections.Generic;
using System.Text;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 存储核心 —— <b>4.0 容器实现</b>。
    ///
    /// 与 3.0（账本建筑）的根本差别：内容物是**真实的 Thing**，住在 <see cref="ThingOwner{T}"/> 里，
    /// 而不是一串 (def, stuff) → 数量的数据。因此：
    ///
    /// <list type="bullet">
    /// <item>原版**双向**原生接受，零 Harmony：
    ///   取出 = <see cref="IHaulSource"/>（<c>Thing.SpawnSetup:888</c> 自动登记）
    ///   放入 = <see cref="IHaulDestination"/>（<c>Thing.SpawnSetup:884</c> 自动登记）</item>
    /// <item>能存品质 / 耐久 / 衣物 / 武器差异 —— 账本键 (def, stuff) 存不下这些</item>
    /// <item>不 tick（<see cref="ShouldTickContents"/> = false + <c>dontTickContents</c>）⇒ 不腐烂、不耗性能</item>
    /// </list>
    ///
    /// 原版同构模板：<c>Building_OutfitStand</c>（奥德赛衣架）/
    /// <c>Building_Bookcase</c>（书架）——「用 ThingOwner 存东西的建筑」是原版就有的形态。
    ///
    /// 机制细节与实测数据见 obsidian：
    /// <c>代码Wiki/csharp-api/容器内容物参与原版作业-ParentHolder返回Map.md</c>
    /// </summary>
    [StaticConstructorOnStartup]
    public class Building_StorageCore : Building, IThingHolder, ISearchableContents,
        IHaulSource, IHaulDestination, IApparelSource, IStoreSettingsParent, IThingHolderTickable
    {
        private static readonly Material LightMat = MaterialPool.MatFrom("2.0/一束光", ShaderDatabase.MoteGlow);
        private static readonly Material OrbMat = MaterialPool.MatFrom("2.0/一个球", ShaderDatabase.Cutout);

        // ===================================================================
        // 4.0 容器本体
        // ===================================================================

        /// <summary>内容物。物品在这里**未 Spawned**，因此不进 listerThings / 不注册 TickManager。</summary>
        public ThingOwner<Thing> innerContainer;

        private StorageSettings storeSettings;
        private bool haulSourceEnabled = true;

        /// <summary>
        /// 过滤器 UI 的「父过滤器」= 可选范围的**全集**（静态、只读）。
        ///
        /// ⚠️ <b>它必须与 <see cref="StorageFilter"/> 是两个不同的对象。</b>
        /// <c>ThingFilterUI.DoThingFilterConfigWindow(rect, state, filter, parentFilter, mask)</c>
        /// 用 parentFilter 建树、用 filter 画勾选。批 1 曾把两者都返回
        /// <c>GetStoreSettings().filter</c>，结果树的可选范围 = 当前勾选范围 ——
        /// 取消勾选一个条目，它就从树里消失，玩家再也勾不回来
        /// （用户实测「被操作过的条目会消失，让我无法操作筛选」）。
        /// </summary>
        private static ThingFilter parentFilter;
        private bool haulDestinationEnabled = true;

        /// <summary>
        /// 栈数上限（<see cref="Accepts"/> 用）。4.0 起不再有"升级扩容"，是固定值 + 未来的 def 字段。
        /// </summary>
        public int maxStacks = 500;

        private StoragePriority storagePriorityField = StoragePriority.Preferred;

        /// <summary>
        /// 武器是否可进容器。<b>4.0 起为 true</b>（"全放开"：真实 Thing 存得住什么就存什么）。
        ///
        /// 之所以曾经必须为 false：<c>Verse.AI/JobDriver_Equip.cs</c> 硬编码了具体类
        /// （:22 <c>ParentHolder is Building_OutfitStand</c>，:28 / :35 / :55 硬转换），
        /// 容器里的武器会让它走 :101 的 else 分支 —— 对**未 Spawn 的物品**调 <c>DeSpawn()</c>（报错），
        /// 然后在物品**仍在容器里**时 <c>pawn.equipment.AddEquipment(...)</c>。
        ///
        /// 现在由 <c>HarmonyPatches/Patch_JobDriver_Equip.cs</c> 接管：
        /// 目标是通用 <see cref="IHaulSource"/> 容器内容物时，自己走"走到容器 → 取出 → 装备"这条链。
        /// 原版衣架路径零影响（卫语句显式排除 <c>Building_OutfitStand</c>）。
        /// </summary>
        private bool allowWeaponsInStorage = true;

        public Building_StorageCore()
        {
            innerContainer = new ThingOwner<Thing>(this);
            // 双保险：即便外面漏了 ShouldTickContents，容器自身也不再 tick 内容。
            // ThingOwner.DoTick() 会线性遍历每个物品调 DoTick()，而容器里的物品是未 Spawned 的
            // —— 实测报错 "Got temperature for null map" ← CompRottable.TickInterval ← ThingOwner.DoTick。
            innerContainer.dontTickContents = true;
        }

        // ===== IThingHolderTickable =====
        //
        // Thing.DoTick() 末尾检查这个（Thing.cs:752）：
        //   if (... || (cachedTickable != null && !cachedTickable.ShouldTickContents) ...) return;
        // false = 不 tick 内容物 ⇒ 不腐烂、不耗性能。这是整个设计的存在理由。
        public bool ShouldTickContents => false;

        // ===== IThingHolder =====

        /// <summary>
        /// 【4.0 的地基】不是 null，而是 <c>Map</c>。
        ///
        /// <c>ThingOwnerUtility.GetRootMap</c> 的循环（<c>ThingOwnerUtility.cs:140</c>）：
        /// <code>
        /// while (holder != null) { if (holder is Map m) return m; holder = holder.ParentHolder; }
        /// </code>
        /// 这里 holder 的静态类型是 IThingHolder ⇒ <c>holder.ParentHolder</c> 是**接口分派**，
        /// 会走到本属性。链变成：物品 → 本建筑（不是 Map）→ .ParentHolder = Map → is Map → 返回 Map ✓
        ///
        /// <b>为什么是数据修复而不是 Harmony 补丁</b>：改的是数据不是代码入口，**不会被 JIT 内联绕过**
        /// （2026-10-01 实测：给 <c>ThingOwnerUtility.GetRootMap</c> 打 postfix 无效，
        /// 因为该小静态方法被内联进了 <c>Thing.get_MapHeld</c>）。
        ///
        /// <b>没有它</b>，下列闸门全部会拒掉内容物：
        /// <code>
        /// ReservationManager.cs:172                     MapHeld != map
        /// ToilFailConditions.cs:69                      MapHeld != actor.Map
        /// WorkGiver_DoBill.cs:490                       pawn.CanReserve(内容物)
        /// </code>
        /// 注意 <c>Thing.SpawnedOrAnyParentSpawned</c> 走的是 <c>Thing.ParentHolder</c>（非虚，不受 <c>new</c> 影响），
        /// 所以不受牵连 —— 它本来就返回 true。
        /// </summary>
        public new IThingHolder ParentHolder => Map;

        public ThingOwner GetDirectlyHeldThings() => innerContainer;

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, innerContainer);
        }

        // ===== ISearchableContents（Z 搜索能看到） =====

        public ThingOwner SearchableContents => innerContainer;

        // ===== IHaulSource（取出方向） =====

        public bool HaulSourceEnabled => haulSourceEnabled;

        // ===== IHaulDestination（放入方向） =====

        public bool HaulDestinationEnabled => haulDestinationEnabled;

        /// <summary>
        /// 原版问"这个目的地收不收这件东西"。被
        /// <c>StoreUtility.TryFindBestBetterNonSlotGroupStorageFor</c>（<c>StoreUtility.cs:262</c>）
        /// 与 <c>JobDriver_HaulToContainer</c> 的失败条件调用，**每次找目的地都会跑**，要保持便宜。
        ///
        /// <b>【甲-1】按"东西在哪边"分流 —— 这个方法就是出库语义的开关：</b>
        ///
        /// <c>ListerHaulables.ShouldBeHaulable</c> 靠
        /// <c>IsInAnyStorage() =&gt; CurrentHaulDestinationOf(t)?.Accepts(t) ?? false</c>
        /// 判断"它还在有效存储里吗"。
        ///
        /// <list type="bullet">
        /// <item><b>已经在容器里</b> → <b>只按过滤器作答</b>。
        ///   改过滤器 → Allows 变 false → IsInAnyStorage 变 false → ShouldBeHaulable 变 true
        ///   → WorkGiver_Haul 建 HaulToCell 作业 → 搬运工来容器里取走（原版容器感知）。
        ///   这里**绝不能**掺容量规则：一旦 maxStacks 满，容器里所有"放不下"的东西都会被判为
        ///   无处可去，原版会把已有库存全搬出去。</item>
        /// <item><b>要进来的</b> → 过滤器 + 容量 + 真收得下。
        ///   否则 <c>HaulAIUtility.cs:191</c> 的 job.count 会是 0 → <c>StartCarryThing</c> 抛异常。</item>
        /// </list>
        ///
        /// 这也和原版一致：<c>Building_Storage.Accepts</c> 只看过滤器，容量由 <c>MaxItemsInCell</c> 在放置时管。
        /// </summary>
        public bool Accepts(Thing t)
        {
            if (t == null || t.def == null) return false;
            if (!Spawned) return false;
            if (!haulDestinationEnabled) return false;

            StorageSettings st = GetStoreSettings();
            bool allowed = (st == null || st.filter == null) || st.filter.Allows(t);

            if (ReferenceEquals(t.ParentHolder, this)) return allowed;

            if (!allowed) return false;
            if (innerContainer.Count >= maxStacks && !ContainerHasDef(t.def)) return false;
            return innerContainer.GetCountCanAccept(t) > 0;
        }

        /// <summary>
        /// 容器里是否已经有同 def 的东西。**只在 maxStacks 满时调用**，
        /// 所以 O(N) 线性扫描是可以接受的（不进热路径）。
        /// </summary>
        private bool ContainerHasDef(ThingDef def)
        {
            for (int i = 0; i < innerContainer.Count; i++)
            {
                Thing t = innerContainer[i];
                if (t != null && t.def == def) return true;
            }
            return false;
        }

        // ===== IApparelSource（穿戴方向） =====
        //
        // JobDriver_Wear.cs 的两处：
        //   apparel.ParentHolder is IApparelSource apparelSource   // 决定走不走 source 路径
        //   ApparelSource.RemoveApparel(apparel);                  // 从容器里摘出来再穿
        // JobGiver_OptimizeApparel.cs:147 也用（target 变成容器后才发 Wear 作业）。
        //
        // 没有它：Wear 走 else 分支的 GotoThing(A) + FailOnDespawnedNullOrForbidden(A)，
        // 内容物 Spawned=false ⇒ 当场 Incompletable ⇒ 一 tick 十次。
        //
        // 注意：**衣物走接口（可扩展），武器走具体类（JobDriver_Equip 写死 Building_OutfitStand）**
        // —— 这是原版自己的不一致，也是 allowWeaponsInStorage 默认 false 的原因。
        public bool ApparelSourceEnabled => haulSourceEnabled;

        public bool RemoveApparel(Apparel apparel) => innerContainer.Remove(apparel);

        // ===== IStoreSettingsParent =====

        /// <summary>原版存储标签页。false = 不显示（我们用自己的 Dialog_StorageFilter）。</summary>
        public bool StorageTabVisible => false;

        public StorageSettings GetStoreSettings()
        {
            if (storeSettings == null)
            {
                storeSettings = new StorageSettings(this);
                // 「全放开」：真实 Thing 存得住什么就存什么（品质/耐久/衣物都行）。
                // 用父过滤器而不是 SetAllowAll(null)，让"自己的可选范围"与"UI 显示的全集"
                // 完全一致 —— 否则过滤器窗口里会出现 UI 看不见/勾不到的条目。
                storeSettings.filter.SetAllowAll(GetParentFilterPublic());
                storeSettings.Priority = storagePriorityField;
                ApplyWeaponFilter();
            }
            return storeSettings;
        }

        public StorageSettings GetParentStoreSettings()
        {
            return (def != null && def.building != null) ? def.building.fixedStorageSettings : null;
        }

        /// <summary>把 <see cref="allowWeaponsInStorage"/> 落到过滤器上。Accepts 直接查过滤器 ⇒ 同时改了进出两侧。</summary>
        private void ApplyWeaponFilter()
        {
            if (storeSettings == null || storeSettings.filter == null) return;
            storeSettings.filter.SetAllow(ThingCategoryDefOf.Weapons, allowWeaponsInStorage);
        }

        public void Notify_SettingsChanged()
        {
            if (!Spawned || MapHeld == null) return;
            MapHeld.listerHaulables?.Notify_HaulSourceChanged(this);
            MapHeld.haulDestinationManager?.Notify_HaulDestinationChangedPriority();
        }

        // ===== 兼容层：惰性替身（让第三方扫描器看见内容物）=====
        //
        // 目的：Phinix（TradeWindow.cs:106）与 Phinix 红包（RedPacketTab.cs:157）的**默认分支**是
        //   maps.SelectMany(m => m.haulDestinationManager.AllGroups).SelectMany(g => g.HeldThings)
        // —— 它们不认 IHaulSource，只认 SlotGroup。核心得先进 AllGroups。
        //
        // 【⚠️ 绝不能由核心自己实现 ISlotGroupParent】2026-10-02 实测事故：
        //   StoreUtility.cs:252  if (haulDestination2 is ISlotGroupParent || ...) continue;   // 容器腿
        // 核心自己实现该接口 ⇒ 被当"格子型储存"跳过 ⇒ 自动收纳停摆 / pawn 不往核心搬货 /
        // 右键报"无可用存储区"（HaulAIUtility:129、Pawn_JobTracker:710,733 还会当格子目的地建作业）。
        // 改成**惰性替身**：替身进 AllGroups，但 HaulDestinationEnabled=false + Accepts=false +
        // 零格子 ⇒ 容器腿两次判断都跳过它，对原版管线完全透明。详见 CoreSlotGroupAdapter。
        private Compatibility.CoreSlotGroupAdapter compatSlotGroup;

        /// <summary>替身是否已注册进 <c>haulDestinationManager</c>（资源计数防双算要用）。</summary>
        public bool CompatSlotGroupRegistered { get; private set; }

        internal Compatibility.CoreSlotGroupAdapter CompatSlotGroup
        {
            get
            {
                if (compatSlotGroup == null) compatSlotGroup = new Compatibility.CoreSlotGroupAdapter(this);
                return compatSlotGroup;
            }
        }

        private void RegisterCompatSlotGroup()
        {
            Map map = Map;
            HaulDestinationManager mgr = (map == null) ? null : map.haulDestinationManager;
            if (mgr == null) return;

            Compatibility.CoreSlotGroupAdapter adapter = CompatSlotGroup;
            if (mgr.AllHaulDestinationsListForReading.Contains(adapter)) { CompatSlotGroupRegistered = true; return; }

            mgr.AddHaulDestination(adapter); // 内部会对 ISlotGroupParent 调 GetSlotGroup() 并加进 allGroupsInOrder
            CompatSlotGroupRegistered = true;
        }

        private void UnregisterCompatSlotGroup()
        {
            Map map = Map;
            HaulDestinationManager mgr = (map == null) ? null : map.haulDestinationManager;
            if (mgr == null || compatSlotGroup == null) { CompatSlotGroupRegistered = false; return; }

            if (mgr.AllHaulDestinationsListForReading.Contains(compatSlotGroup))
                mgr.RemoveHaulDestination(compatSlotGroup); // 会连带把 SlotGroup 从 allGroupsInOrder 摘掉
            CompatSlotGroupRegistered = false;
        }

        // ===== 对外小接口 =====

        public StoragePriority storagePriority
        {
            get => storagePriorityField;
            set
            {
                if (storagePriorityField == value) return;
                storagePriorityField = value;
                if (storeSettings != null) storeSettings.Priority = value;
                Notify_SettingsChanged();
            }
        }

        public ThingFilter StorageFilter => GetStoreSettings().filter;

        public bool Powered => GetComp<CompPowerTrader>()?.PowerOn ?? true;

        /// <summary>
        /// 可选范围全集：所有 <c>ThingCategory.Item</c> 非尸体 def（「全放开」）。
        /// 静态缓存。**不要返回 <see cref="StorageFilter"/>** —— 见 parentFilter 字段的注释。
        /// </summary>
        public ThingFilter GetParentFilterPublic()
        {
            if (parentFilter == null)
            {
                parentFilter = new ThingFilter();
                foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
                {
                    if (def.category == ThingCategory.Item && !def.IsCorpse)
                        parentFilter.SetAllow(def, true);
                }
            }
            return parentFilter;
        }

        // ===== 生命周期 =====

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (innerContainer == null) innerContainer = new ThingOwner<Thing>(this);
            GetStoreSettings();

            // StoreUtility.TryFindBestBetterNonSlotGroupStorageFor:267 有
            //   if (thing != null && thing.Faction != faction) continue;
            // 「开发者 → 生成 → 建筑」是裸 GenSpawn 不设阵营 → 会被静默跳过。补上。
            if (Faction == null && def != null && def.CanHaveFaction)
                SetFaction(Faction.OfPlayer);

            // 兼容层：注册惰性替身（只为了让核心进 AllGroups 供第三方扫描器枚举）
            RegisterCompatSlotGroup();

            // 3.0 → 4.0：把旧账本搬进容器（只有从 3.0 存档读出来才有数据，新核心是 no-op）
            MigrateLegacy30IfNeeded();
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // 拆/毁时把内容物全部落地，避免随建筑一起消失（物品丢失）。
            if (mode == DestroyMode.Deconstruct || mode == DestroyMode.KillFinalize)
                DropAllContents();

            // 注销替身要在 base.DeSpawn 之前（那时 Map 还在），否则 allGroupsInOrder 会留一个僵尸组
            UnregisterCompatSlotGroup();

            base.DeSpawn(mode);
        }

        /// <summary>
        /// 把内容物全部丢在脚下。**不用 CoreDestroyDropQueue**（那是账本时代按 ItemKey 排队的东西）。
        /// 放不下的会留在容器里 —— 这里记一条 error，不再静默吞掉。
        /// </summary>
        private void DropAllContents()
        {
            if (innerContainer == null || innerContainer.Count == 0) return;
            Map map = Map;
            if (map == null) return;

            innerContainer.TryDropAll(Position, map, ThingPlaceMode.Near);
            if (innerContainer.Count > 0)
            {
                Log.Error("[DigitalStorage] " + innerContainer.Count
                    + " 个物品无法落在存储核心脚下（空间不足），将随建筑一起销毁：" + this);
            }
        }

        protected override void ReceiveCompSignal(string signal)
        {
            base.ReceiveCompSignal(signal);
            if (signal == "PowerTurnedOn" || signal == "PowerTurnedOff") Notify_SettingsChanged();
        }

        // ===== 3.0 → 4.0 存档迁移（墓碑字段）=====
        //
        // 3.0 的这个类里是：
        //   Scribe_Values.Look(ref networkName, "networkName");
        //   Scribe_Deep.Look(ref ledger, "ledger");              // CoreLedger：stockKeys + stockCounts
        //   Scribe_Deep.Look(ref storageFilter, "storageFilter");
        // 4.0 的容器里是真实 Thing，那些数据没有别的东西会去读 ⇒ 不读就等于玩家的货消失。
        // 这里用**同键**把它们读回来（类名 4.0 没变，所以不需要复活旧类），
        // 读档后在 SpawnSetup 里一次性落进 innerContainer。详见 Compatibility/Legacy30Migration。
        private Compatibility.Legacy30Migration.LegacyLedger legacyLedger;
        private ThingFilter legacyStorageFilter;
        private bool legacy30Migrated;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref innerContainer, "innerContainer", this);
            Scribe_Deep.Look(ref storeSettings, "storeSettings", this);
            Scribe_Values.Look(ref haulSourceEnabled, "haulSourceEnabled", true);
            Scribe_Values.Look(ref haulDestinationEnabled, "haulDestinationEnabled", true);
            Scribe_Values.Look(ref maxStacks, "maxStacks", 500);
            Scribe_Values.Look(ref storagePriorityField, "storagePriority", StoragePriority.Preferred);
            Scribe_Values.Look(ref allowWeaponsInStorage, "allowWeaponsInStorage", false);

            // 3.0 遗留数据（键名必须与 3.0 逐字一致；4.0 自己的存档里这些是 null）
            Scribe_Deep.Look(ref legacyLedger, "ledger");
            Scribe_Deep.Look(ref legacyStorageFilter, "storageFilter");
            Scribe_Values.Look(ref legacy30Migrated, "dsLegacy30Migrated", false);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (innerContainer == null) innerContainer = new ThingOwner<Thing>(this);
                innerContainer.dontTickContents = true;
                GetStoreSettings();
                ApplyWeaponFilter();
            }
        }

        /// <summary>
        /// 3.0 存档迁移：把旧账本搬进容器、沿用旧筛选。
        /// 跑完就把墓碑清空并置标记 —— 否则下次存档会把旧数据写回去，读档时重复迁移（凭空多出货物）。
        /// </summary>
        private void MigrateLegacy30IfNeeded()
        {
            if (legacy30Migrated) return;
            if (legacyLedger == null && legacyStorageFilter == null) return;

            legacy30Migrated = true;

            if (legacyStorageFilter != null)
            {
                Compatibility.Legacy30Migration.ApplyLegacyFilter(this, legacyStorageFilter);
                legacyStorageFilter = null;
            }
            if (legacyLedger != null)
            {
                Compatibility.Legacy30Migration.Migrate(this, legacyLedger);
                legacyLedger = null;
            }
        }

        // ===== 表现 =====

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

        public override string GetInspectString()
        {
            var sb = new StringBuilder();
            string baseInspect = base.GetInspectString();
            if (!string.IsNullOrEmpty(baseInspect)) sb.AppendLine(baseInspect);

            sb.AppendLine("存储核心：" + innerContainer.Count + " 栈 / " + innerContainer.TotalStackCount
                + " 个单位（上限 " + maxStacks + " 栈）");
            sb.AppendLine("存储优先级：" + storagePriorityField);
            if (!Powered) sb.AppendLine("DS_NoPower".Translate());
            return sb.ToString().TrimEnd();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;

            yield return new Command_Action
            {
                defaultLabel = "DS_StorageFilter".Translate(),
                defaultDesc = "DS_StorageFilterDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/SetTargetFuelLevel", true),
                action = () => Find.WindowStack.Add(new UI.Dialog_StorageFilter(this))
            };

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
        }
    }
}
