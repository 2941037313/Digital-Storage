using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Core
{
    /// <summary>
    /// <b>装载直塞</b>：装载目标本身就是 <c>ThingOwner</c> 容器，和数字存储核心同构 ——
    /// 那么"从核心装进它"就**不需要 pawn**，直接容器→容器即可。
    ///
    /// <para>覆盖两类，接缝见
    /// <c>HarmonyPatches/Patch_MakeLordsAsAppropriate_InstantLoad.cs</c>：</para>
    /// <list type="bullet">
    /// <item><b>运输舱族</b> <see cref="CompTransporter"/>：穿梭机（<c>CompShuttle</c> 子类）、
    /// 空投仓、以及任何 mod 的 <c>CompTransporter</c> 子类（载具货舱等）。</item>
    /// <item><b>传送门</b> <see cref="MapPortal"/>（Odyssey）：结构完全平行
    /// （<c>leftToLoad</c> + <c>AddToTheToLoadList</c> + <c>Notify_ThingAdded</c>）。</item>
    /// </list>
    ///
    /// <para><b>为什么原版原语一次就把账算对</b>：
    /// <c>ThingOwner.TryTransferToContainer</c>（<c>ThingOwner.cs:715</c>）内部是
    /// SplitOff → Remove → other.TryAdd，失败自动吸回不丢货；而
    /// <c>ThingOwner.NotifyAdded</c>（<c>ThingOwner.cs:972</c>）在 owner 是
    /// <c>CompTransporter</c> 时会回调 <c>Notify_ThingAdded</c> →
    /// <c>SubtractFromToLoadList</c>。于是 <b>leftToLoad 扣减 / 装载完成消息 /
    /// 任务信号 / 质量重算全部由原版自己完成</b>，这里一行都不用抄。
    /// 原版自己的 dev 按钮 <c>Dialog_LoadTransporters.DebugTryLoadInstantly</c> 走的就是这条链。</para>
    ///
    /// <para><b>传送门是漏斗不是仓库</b>：<c>PortalContainerProxy.TryAdd</c>（<c>:20-27</c>）
    /// 不存东西，直接 <c>Notify_ThingAdded</c> + <c>GenDrop.TryDropSpawn(..., GetOtherMap(), ...)</c>
    /// —— "放进传送门"的语义本来就是"立刻出现在对面地图的目的地"。所以我们的直塞对传送门同样成立。</para>
    ///
    /// <para><b>只搬核心里的</b>：<c>leftToLoad[].things</c> 里 <c>ParentHolder</c> 不是本 mod 核心的
    /// （地面上的、pawn 手上的、原版书架/衣架里的）一律不碰，照旧由原版派 pawn 搬。
    /// 所以"核心不够"时行为与原版逐字相同 —— 这是纯加速，不是替代。</para>
    /// </summary>
    internal static class ContainerAutoLoader
    {
        /// <summary>装载条目的快照。搬运过程中 <c>leftToLoad</c> 会被 <c>Notify_ThingAdded</c> 改写。</summary>
        private static readonly List<TransferableOneWay> tmpEntries = new List<TransferableOneWay>();

        /// <summary>单个条目 <c>things</c> 的快照。见 <see cref="TransferFromCores"/> 里的说明。</summary>
        private static readonly List<Thing> tmpThings = new List<Thing>();

        /// <summary>在途量。<b>故意与 <see cref="HaulSourceContents"/> 的静态缓冲分开</b>（本 mod 踩过缓冲互相覆盖的坑）。</summary>
        private static readonly Dictionary<TransferableOneWay, int> tmpAlreadyLoading = new Dictionary<TransferableOneWay, int>();

        // ===================================================================
        // 入口
        // ===================================================================

        public static void FillTransporters(List<CompTransporter> transporters, Map map)
        {
            if (transporters == null || map == null) return;

            for (int i = 0; i < transporters.Count; i++)
            {
                CompTransporter t = transporters[i];
                if (t == null || t.parent == null || !t.parent.Spawned) continue;
                if (t.innerContainer == null) continue;

                // 没人在等装载 ⇒ 什么都不做（绝大多数 tick 走这条）
                if (t.leftToLoad == null || t.leftToLoad.Count == 0) continue;

                tmpEntries.Clear();
                tmpEntries.AddRange(t.leftToLoad);
                CollectAlreadyLoading(t.parent, JobDefOf.HaulToTransporter, t.leftToLoad, map);
                TransferFromCores(t.innerContainer, map);
            }
        }

        public static void FillPortal(MapPortal portal)
        {
            if (portal == null || !portal.Spawned) return;
            if (portal.leftToLoad == null || portal.leftToLoad.Count == 0) return;

            ThingOwner dest = portal.GetDirectlyHeldThings();
            if (dest == null) return;

            tmpEntries.Clear();
            tmpEntries.AddRange(portal.leftToLoad);
            CollectAlreadyLoading(portal, JobDefOf.HaulToPortal, portal.leftToLoad, portal.Map);
            TransferFromCores(dest, portal.Map);
        }

        // ===================================================================
        // 在途量：照抄原版的 tmpAlreadyLoading 算法
        // ===================================================================

        /// <summary>
        /// 数出"已经在搬、还没送到"的量。
        ///
        /// <para>算法逐字对齐原版 <c>LoadTransportersJobUtility.FindThingToLoad:44-70</c>
        /// （<c>EnterPortalUtility.FindThingToLoad</c> 同构）：凡是当前 job 是 <c>HaulTo*</c>
        /// 且目标容器是这一个的 pawn，把它的 <c>initialCount</c> 记到对应 <c>TransferableOneWay</c> 上。</para>
        ///
        /// <para><b>为什么必须扣</b>：<c>CountToTransfer</c> **不含在途量**（原版也只在派 job 时扣）。
        /// 不扣就会"pawn 手上 20 + 我塞 50 = 70"，超出玩家在对话框里要的数量，
        /// 而那 50 是照着 <c>CheckForErrors</c> 的质量上限验过的 ⇒ 可能超载。</para>
        ///
        /// <para><b>为什么用 <c>initialCount</c> 而不是 <c>CarriedThing.stackCount</c></b>：
        /// pawn 可能还在路上、手上什么都没有，但 job 已经盯上了核心里的某一堆 ——
        /// 那份也算"已有归属"，抢走只会让它白跑一趟。</para>
        /// </summary>
        private static void CollectAlreadyLoading(Thing container, JobDef jobDef, List<TransferableOneWay> leftToLoad, Map map)
        {
            tmpAlreadyLoading.Clear();
            if (container == null || jobDef == null || leftToLoad == null || map == null) return;

            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || p.CurJobDef != jobDef) continue;
                if (p.jobs == null) continue;

                JobDriver_HaulToContainer driver = p.jobs.curDriver as JobDriver_HaulToContainer;
                if (driver == null || driver.Container != container) continue;
                if (driver.ThingToCarry == null) continue;

                int already;
                if (driver is JobDriver_HaulToTransporter transporterDriver) already = transporterDriver.initialCount;
                else if (driver is JobDriver_HaulToPortal portalDriver) already = portalDriver.initialCount;
                else continue;
                if (already <= 0) continue;

                TransferableOneWay entry = TransferableUtility.TransferableMatchingDesperate(
                    driver.ThingToCarry, leftToLoad, TransferAsOneMode.PodsOrCaravanPacking);
                if (entry == null) continue;

                int cur;
                tmpAlreadyLoading.TryGetValue(entry, out cur);
                tmpAlreadyLoading[entry] = cur + already;
            }
        }

        // ===================================================================
        // 搬运本体
        // ===================================================================

        private static void TransferFromCores(ThingOwner dest, Map map)
        {
            if (dest == null || map == null) return;

            for (int e = 0; e < tmpEntries.Count; e++)
            {
                TransferableOneWay entry = tmpEntries[e];
                if (entry == null) continue;

                int want = entry.CountToTransfer;
                if (want <= 0) continue;

                int alreadyLoading;
                if (tmpAlreadyLoading.TryGetValue(entry, out alreadyLoading)) want -= alreadyLoading;
                if (want <= 0) continue;

                // 【必须快照】MapPortal.SubtractFromToLoadList:214 会把搬走的那个 Thing
                // 从 things 里 Remove（CompTransporter 那一版不移）—— 直接遍历原列表会漏项。
                tmpThings.Clear();
                tmpThings.AddRange(entry.things);

                for (int k = 0; k < tmpThings.Count && want > 0; k++)
                {
                    Thing t = tmpThings[k];
                    if (t == null || t.Destroyed) continue;

                    // 只搬住在本 mod 核心里的。地面上的 / pawn 手上的留给原版。
                    Building_StorageCore core = t.ParentHolder as Building_StorageCore;
                    if (core == null || !core.IsUsableNow) continue;

                    ThingOwner owner = core.GetDirectlyHeldThings();
                    if (owner == null || !owner.Contains(t)) continue;

                    // 不抢别人已经预订的料（建造工 / 另一个搬运工）
                    if (map.reservationManager != null &&
                        map.reservationManager.IsReservedByAnyoneOf(t, Faction.OfPlayer)) continue;

                    int take = Math.Min(want, t.stackCount);
                    if (take <= 0) continue;

                    int before = t.stackCount;
                    owner.TryTransferToContainer(t, dest, take, true);

                    // 【不能信返回值】TryTransferToContainer:754 返回的是 `thing.stackCount`，
                    // 而 canMerge=true 时拆出来的那件会被并进 dest 里已有的堆并销毁 ⇒ 返回 0，
                    // 但东西**确实搬到了**。照返回值记数会重复搬（over-fill）。
                    // 所以从**源侧**算实际搬走的量：还留在源容器里就比数量差，整堆被拿走就是 take。
                    int moved = owner.Contains(t) ? (before - t.stackCount) : take;
                    if (moved <= 0) break;

                    want -= moved;
                }
            }

            tmpEntries.Clear();
            tmpThings.Clear();
            tmpAlreadyLoading.Clear();
        }
    }
}
