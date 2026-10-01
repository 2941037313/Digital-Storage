using System.Collections.Generic;
using System.Text;
using DigitalStorage.AI;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Backpack
{
    public class HediffCompProperties_Backpack : HediffCompProperties
    {
        public int capacityStacks = 4;

        public HediffCompProperties_Backpack()
        {
            compClass = typeof(HediffComp_Backpack);
        }
    }

    /// <summary>
    /// 「背包」：挂在 pawn 身上的 ThingOwner（实验版）。
    ///
    /// <para><b>核心机制</b>：链条 <c>物品 → 本 comp → Pawn</c>，而
    /// <c>Thing.SpawnedParentOrMe</c>（<c>Thing.cs:223</c>）委托
    /// <c>ThingOwnerUtility.SpawnedParentOrMe(ParentHolder)</c> **沿持有者链上爬、不要求链上是 Thing**
    /// ⇒ 背包内容物的 <c>SpawnedParentOrMe</c> = **小人本人**
    /// ⇒ 原版 <c>JobDriver_DoBill:121</c> 的 <c>canGotoSpawnedParent</c> 寻路目标就是小人自己
    /// ⇒ **0 距离取料**。</para>
    ///
    /// <para><b>接口真相（编译器确认）</b>：<c>IHaulSource</c> 本身继承
    /// <c>IThingHolder + IStoreSettingsParent</c>，并额外要求 <c>Map</c>。
    /// 所以「背包」天然就是一个储存节点 —— 这让 <c>Accepts</c> 可以**只认自己的内容物**：
    /// 内容物算"在储存中"（不被原版搬走），而任何外部物品都不被接受（不会变成卸货点）。</para>
    ///
    /// <para><b>本实验要测的三件事</b>（开发者菜单 → DigitalStorage）：① 原版搜索能否看见背包里的料；
    /// ② <c>ListerHaulables.ShouldBeHaulable</c>（反射原版私有）会不会判它可搬；③ 会不会被当卸货点。</para>
    /// </summary>
    public class HediffComp_Backpack : HediffComp, IThingHolder, IHaulSource, IHaulDestination
    {
        private ThingOwner backpack;
        private StorageSettings settings;
        private bool sourceEnabled = true;
        private bool registered;

        public HediffCompProperties_Backpack Props => (HediffCompProperties_Backpack)props;

        // ---- IThingHolder ----
        public IThingHolder ParentHolder => Pawn;   // ← 实验核心：链条终点是小人自己

        public ThingOwner GetDirectlyHeldThings()
        {
            if (backpack == null) backpack = new ThingOwner<Thing>(this);
            return backpack;
        }

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        // ---- IHaulSource ----
        public bool HaulSourceEnabled => sourceEnabled;

        public Map Map => Pawn?.Map;

        // ---- IStoreSettingsParent（IHaulSource 的基接口）----
        public StorageSettings GetStoreSettings()
        {
            if (settings == null) settings = new StorageSettings(this);
            return settings;
        }

        public StorageSettings GetParentStoreSettings() => null;

        public void Notify_SettingsChanged() { }

        public bool StorageTabVisible => false;

        // ---- IHaulDestination：只认自己的内容物 ----
        // 「已在自己肚子里」⇒ 算在储存中（原版不会把它搬走）；
        // 其余一律 false ⇒ 原版永远不会把外面的东西卸进背包。
        public bool HaulDestinationEnabled => false;

        /// <summary>IHaulDestination 要的位置 —— 背包的位置就是小人自己（这也是"0 距离"的来源）。</summary>
        public IntVec3 Position => Pawn != null ? Pawn.PositionHeld : IntVec3.Invalid;

        public bool Accepts(Thing t)
        {
            return t != null && ReferenceEquals(t.ParentHolder, this);
        }

        // ---- 注册 ----
        public void EnsureRegistered()
        {
            Map map = Pawn?.Map;
            if (map == null || registered) return;
            GetStoreSettings().Priority = StoragePriority.Critical; // 高于任何格子型储存 ⇒ IsInValidBestStorage=true
            map.haulDestinationManager.AddHaulSource(this);
            registered = true;
        }

        public void SetSourceEnabled(bool value)
        {
            sourceEnabled = value;
            Map map = Pawn?.Map;
            if (map == null) return;
            if (value && !registered) { map.haulDestinationManager.AddHaulSource(this); registered = true; }
            else if (!value && registered) { map.haulDestinationManager.RemoveHaulSource(this); registered = false; }
        }

        public override void CompExposeData()
        {
            base.CompExposeData();
            Scribe_Deep.Look(ref backpack, "dsBackpack", this);
            Scribe_Values.Look(ref sourceEnabled, "dsBackpackSourceEnabled", true);
        }

        public override void CompPostMake()
        {
            base.CompPostMake();
            if (backpack == null) backpack = new ThingOwner<Thing>(this);
        }

        public override void CompPostPostAdd(DamageInfo? dinfo)
        {
            base.CompPostPostAdd(dinfo);
            if (backpack == null) backpack = new ThingOwner<Thing>(this);
            EnsureRegistered();
        }

        public override void CompPostPostRemoved()
        {
            base.CompPostPostRemoved();
            // 防止丢物：hediff 移除（含死亡）时把背包清空落地。
            DropEverything();
            Map map = Pawn?.Map;
            if (map != null && registered) { map.haulDestinationManager.RemoveHaulSource(this); registered = false; }
        }

        public void DropEverything()
        {
            ThingOwner held = GetDirectlyHeldThings();
            if (held.Count == 0) return;
            Map map = Pawn?.MapHeld;
            IntVec3 cell = Pawn != null ? Pawn.PositionHeld : IntVec3.Invalid;
            if (map == null || !cell.IsValid)
            {
                held.ClearAndDestroyContents();
                return;
            }
            held.TryDropAll(cell, map, ThingPlaceMode.Near);
        }

        // ---- 实验动作（由 BackpackDebugActions 的开发者菜单调用）----
        public void TakeOneFromCore()
        {
            Map map = Pawn?.Map;
            if (map == null) return;
            foreach (Building_StorageCore core in CoreFinder.AllUsableCores(map))
            {
                ThingOwner held = core.GetDirectlyHeldThings();
                for (int i = 0; i < held.Count; i++)
                {
                    Thing src = held[i];
                    if (src == null || src.Destroyed) continue;
                    Thing one = src.SplitOff(1);
                    if (one == null) continue;
                    if (GetDirectlyHeldThings().TryAdd(one, true))
                    {
                        Messages.Message("已放入背包：" + one.LabelShort, Pawn, MessageTypeDefOf.NeutralEvent);
                        return;
                    }
                    GenPlace.TryPlaceThing(one, Pawn.PositionHeld, map, ThingPlaceMode.Near);
                }
            }
            Messages.Message("核心里没有可取的东西。", Pawn, MessageTypeDefOf.RejectInput);
        }

        private static readonly System.Reflection.MethodInfo ShouldBeHaulableMethod =
            AccessTools.Method(typeof(ListerHaulables), "ShouldBeHaulable");

        public void Dump()
        {
            Map map = Pawn?.Map;
            var sb = new StringBuilder();
            sb.AppendLine("[DS-BAG] ==== 背包实验 :: " + (Pawn == null ? "null" : Pawn.LabelShortCap) + " ====");
            sb.AppendLine("  sourceEnabled=" + sourceEnabled + " registered=" + registered
                + " 背包件数=" + GetDirectlyHeldThings().Count
                + " 优先级=" + (settings == null ? "?" : settings.Priority.ToString()));
            if (map != null)
            {
                sb.AppendLine("  ∈AllHaulSourcesListForReading=" + map.haulDestinationManager.AllHaulSourcesListForReading.Contains(this));
                sb.AppendLine("  ∈AllHaulDestinationsListInPriorityOrder=" + map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder.Contains(this));
            }

            ThingOwner held = GetDirectlyHeldThings();
            Thing item = held.Count > 0 ? held[0] : null;
            if (item == null)
            {
                sb.AppendLine("  ⚠ 背包是空的 —— 先用开发者菜单「背包：取 1 件料到背包」。");
                Log.Warning(sb.ToString());
                return;
            }

            Thing parentOrMe = item.SpawnedParentOrMe;
            sb.AppendLine("  物品=" + item.LabelShort + " spawned=" + item.Spawned
                + " ParentHolder=" + (item.ParentHolder == null ? "null" : item.ParentHolder.GetType().Name)
                + " **SpawnedParentOrMe=" + (parentOrMe == null ? "null" : parentOrMe.LabelShortCap) + "**（应为小人自己）");
            try
            {
                sb.AppendLine("  CanReach(物品,ClosestTouch)=" + Pawn.CanReach(item, PathEndMode.ClosestTouch, Danger.Deadly)
                    + "  CanReserve(物品)=" + Pawn.CanReserve(item));
            }
            catch (System.Exception e) { sb.AppendLine("  CanReach/CanReserve 抛异常: " + e.GetType().Name); }

            sb.AppendLine("  ∈listerHaulables=" + (map != null && map.listerHaulables.ThingsPotentiallyNeedingHauling().Contains(item)));
            if (ShouldBeHaulableMethod != null && map != null)
            {
                try
                {
                    object r = ShouldBeHaulableMethod.Invoke(map.listerHaulables, new object[] { item });
                    sb.AppendLine("  原版 ShouldBeHaulable=" + r + "（true ⇒ 搬运工会来掏背包）");
                }
                catch (System.Exception e) { sb.AppendLine("  ShouldBeHaulable 反射失败: " + e.GetType().Name); }
            }
            sb.AppendLine("  分支值[在任意储存=" + StoreUtility.IsInAnyStorage(item)
                + " 在有效最优储存=" + StoreUtility.IsInValidBestStorage(item)
                + " 我的Accepts=" + Accepts(item) + "]");

            Log.Warning(sb.ToString());
        }
    }
}
