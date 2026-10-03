using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.AI;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>亚核心扫描仪（差分 <c>SubcoreSoftscanner</c> / 裂解 <c>SubcoreRipscanner</c>）的隔空投料。</b>
    ///
    /// <para><b>为什么这两台机器必须单独补</b>：它们的原料不走 Bill，走的是原版自己的
    /// <c>WorkGiver_HaulToSubcoreScanner</c> + <c>BuildingProperties.subcoreScannerFixedIngredients</c>
    /// （固定 <c>Steel 50</c> + <c>ComponentIndustrial 4</c>，写在 Def 的抽象父级
    /// <c>SubcoreScannerBase</c> 上，两台机器共用）。那条 workgiver 的原版实现是：</para>
    /// <code>
    /// GenClosest.ClosestThingReachable(…, ThingRequestGroup.HaulableEver, …)   // lookInHaulSources 用默认值 false
    /// </code>
    /// <para>⇒ <b>只看本图已 Spawned 的散落物</b>。而本 mod 的内容物是容器里未 Spawned 的真实 Thing
    /// （硬约束：内容物绝不进 <c>listerThings</c>），于是扫描仪对数字存储**完全看不见** ——
    /// 核心里躺着上万钢材也照样卡在"等待材料"。</para>
    ///
    /// <para><b>第二个断链（更隐蔽）</b>：就算玩家把料丢在地上，自动收纳（<c>CompAutoIngest</c>）
    /// 会在 15 tick 内把它吸回核心 —— <c>RejectReason</c> 只豁免"工作台材料区 / 关押区 / 已预订"，
    /// 而扫描仪既不是 <c>IBillGiver</c>（Def 里没有 <c>AllRecipes</c>，也不占材料区），
    /// 那 50 钢材在扫描仪旁边的地上活不过一轮收纳。原版搬运工白跑，扫描仪永远差那点料。</para>
    ///
    /// <para><b>所以这里做的事</b>：把原版"派个 pawn 把料搬进去"那一趟，换成**容器→容器直塞**
    /// （<c>ThingOwner.TryTransferToContainer</c>，与 <c>ContainerAutoLoader</c> 装载运输舱/传送门
    /// 同款原语）。语义与其它远程取料一致：<b>料是真的从核心里扣掉的，不白给</b>。</para>
    ///
    /// <list type="number">
    /// <item><b>只在</b> <c>State == WaitingForIngredients</c> <b>时动手</b> —— 这个状态由原版自己算，
    ///   一次性管住三件事：玩家已按下 Start（<c>initScanner</c>）、通电、且确实还缺料。
    ///   玩家点"取消装载"（<c>EjectContents</c> 会把 <c>initScanner</c> 复位）后立刻停手。</item>
    /// <item><b>有搬运工在路上就不抢</b>：原版 <c>JobDriver_HaulToContainer.TryMakePreToilReservations</c>
    ///   会预约容器，所以"扫描仪被玩家阵营预约"= 有 pawn 正把地面上的料送过来 ⇒ 让原版那条腿先走完，
    ///   送不齐的部分下一轮再从核心补。**不预先扣在途量**，因为 <c>JobDriver_HaulToContainer</c> 没有
    ///   <c>initialCount</c>（那是运输舱/传送门的子类才有）；最坏结果是同一批料多搬一份进机器，
    ///   取消时原样掉回地面，不丢东西。</item>
    /// <item><b>每 15 tick 才真扫一次</b>：错相写法（<c>(tick + thingIDNumber) % 15</c>，与
    ///   <c>CompAutoIngest</c> 同款），零状态、无字典，且保证"核心一直缺料"时不会每 tick 遍历整个容器。</item>
    /// </list>
    ///
    /// <para><b>不碰的两件事</b>：pawn 自己走进去的那条链（<c>Building_Enterable.SelectPawn</c> →
    /// <c>JobDefOf.EnterBuilding</c> / <c>JobDefOf.CarryToBuilding</c>）本 mod 一行都没改，原版照常；
    /// 扫描仪吐出来的亚核心照旧由 <c>GenPlace.TryPlaceThing</c> 落地，随后被自动收纳吃掉
    /// （或直塞核心，看那一刻是否有代理在干活）。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_Building_SubcoreScanner_SupplyFromCore
    {
        /// <summary>
        /// 目标方法只能显式定位：<c>Building_SubcoreScanner.Tick</c> 是 <c>protected override</c>，
        /// 而基类 <c>Building</c> 也有同名虚方法 —— 用 <c>DeclaredMethod</c> 钉死在本类自己声明的
        /// 那一个，免得解析到基类去（挂错目标 = 补丁静默无效，还不会报错）。
        /// </summary>
        private static MethodBase TargetMethod()
        {
            return AccessTools.DeclaredMethod(typeof(Building_SubcoreScanner), "Tick", Type.EmptyTypes)
                ?? AccessTools.Method(typeof(Building_SubcoreScanner), "Tick", Type.EmptyTypes);
        }

        private static void Postfix(Building_SubcoreScanner __instance)
        {
            if (__instance == null || !__instance.Spawned) return;

            // 非"缺料待装"状态一律零开销返回（绝大多数 tick 走这一行）。
            if (__instance.State != SubcoreScannerState.WaitingForIngredients) return;

            // 相位节流：每 15 tick 真扫一次核心（0.25 秒内必定补上，与人眼无差别）。
            if ((Find.TickManager.TicksGame + __instance.thingIDNumber) % 15 != 0) return;

            Supply(__instance);
        }

        private static void Supply(Building_SubcoreScanner scanner)
        {
            List<IngredientCount> required = scanner.def?.building?.subcoreScannerFixedIngredients;
            if (required == null || required.Count == 0) return;

            Map map = scanner.Map;
            if (map == null) return;

            // 有搬运工在路上（原版预约了容器）⇒ 这一轮不动手，让原版那条腿先走。
            ReservationManager resMgr = map.reservationManager;
            if (resMgr != null && resMgr.IsReservedByAnyoneOf(scanner, Faction.OfPlayer)) return;

            for (int i = 0; i < required.Count; i++)
            {
                ThingDef def = required[i].FixedIngredient;
                if (def == null) continue;

                // 原版自己的"还差多少"（= 固定清单数量 − 容器里已有的数量）。
                int need = scanner.GetRequiredCountOf(def);
                if (need <= 0) continue;

                TakeFromCores(scanner, map, def, need);
            }
        }

        /// <summary>
        /// 从本图可用核心（已通电）里凑 <paramref name="need"/> 个 <paramref name="def"/>，
        /// 直塞进扫描仪容器。
        ///
        /// <para><b>没有任何选择规则是这里发明的</b>：取什么由原版固定清单决定，缺多少由原版
        /// <c>GetRequiredCountOf</c> 决定；"取谁的"按<b>离扫描仪最近的核心优先</b>（确定性，便于复现）。</para>
        /// </summary>
        private static void TakeFromCores(Building_SubcoreScanner scanner, Map map, ThingDef def, int need)
        {
            ThingOwner dest = scanner.innerContainer;
            if (dest == null) return;

            List<Building_StorageCore> cores = CoreFinder.AllUsableCores(map);
            if (cores.Count == 0) return;

            IntVec3 pos = scanner.Position;
            cores.Sort((a, b) => (a.Position - pos).LengthHorizontalSquared.CompareTo((b.Position - pos).LengthHorizontalSquared));

            ReservationManager resMgr = map.reservationManager;

            for (int c = 0; c < cores.Count && need > 0; c++)
            {
                Building_StorageCore core = cores[c];
                if (core == null || !core.IsUsableNow) continue;

                ThingOwner owner = core.GetDirectlyHeldThings();
                if (owner == null) continue;

                // 倒序遍历：整堆被搬走时 TryTransferToContainer 会把它从 owner 里摘掉，
                // 正序遍历会漏项。
                for (int k = owner.Count - 1; k >= 0 && need > 0; k--)
                {
                    Thing t = owner[k];
                    if (t == null || t.Destroyed || t.def != def) continue;

                    // 已被别人预订的料不抢（原版搬运工 / 本 mod 自己派出去的取料 job）。
                    if (resMgr != null && resMgr.IsReservedByAnyoneOf(t, Faction.OfPlayer)) continue;

                    int before = t.stackCount;
                    if (before <= 0) continue;

                    owner.TryTransferToContainer(t, dest, Math.Min(need, before), true);

                    // 【不能信返回值】canMerge=true 时拆出来的那件会被并进 dest 里已有的堆并销毁 ⇒
                    // 返回 0，但东西确实搬到了。从源侧算实际搬走的量（同 ContainerAutoLoader）。
                    int moved = owner.Contains(t) ? before - t.stackCount : before;
                    if (moved <= 0) continue;

                    need -= moved;
                }
            }
        }
    }
}
