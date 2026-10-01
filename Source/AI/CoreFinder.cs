using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Services;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// I2a 地基：跨地图核心发现。
    /// 本地核心优先 → 本地没货时扩展到同 NetworkName 的远程核心。
    /// 远程核心可直连（跨图接口 → GetProxyCells 返回 pawn 所在地图的接口位置）。
    /// </summary>
    public struct CoreAccess
    {
        public Building_StorageCore ledgerCore; // 账本操作目标（可远程）
        public Building_StorageCore proxyCore;  // 代理点来源（GetProxyCells 的调用对象）
    }

    public static class CoreFinder
    {
        // X3: 小环形缓存（多槽）——think tree 对全 pawn 求值时单槽缓存反复 miss，
        // 每槽存 (pawn, tick, chipRequired, result)，最近使用的 4 个 pawn 命中缓存
        private static readonly List<(Pawn pawn, int tick, bool chipRequired, List<CoreAccess> result)> accessCache
            = new List<(Pawn, int, bool, List<CoreAccess>)>();
        private const int AccessCacheSize = 4;

        /// <summary>
        /// 返回所有可用核心访问入口。本地优先，远程同网络兜底，跨图接口直连。
        /// 同一 tick 同一 pawn 缓存结果（4 槽环形）。设置项变化会自然 miss 重建。
        /// </summary>
        public static List<CoreAccess> AllUsableAccesses(Pawn pawn)
        {
            int tick = Find.TickManager.TicksGame;
            // 缓存键里的 chipRequired 已无意义（4.0 不再分芯片），保留字段恒为 false 以稳定缓存。
            bool chipRequired = false;
            for (int i = 0; i < accessCache.Count; i++)
            {
                if (accessCache[i].pawn == pawn && accessCache[i].tick == tick
                    && accessCache[i].chipRequired == chipRequired)
                    return accessCache[i].result;
            }

            var r = BuildAccessList(pawn);
            accessCache.Insert(0, (pawn, tick, chipRequired, r));
            if (accessCache.Count > AccessCacheSize)
                accessCache.RemoveAt(accessCache.Count - 1);
            return r;
        }

        /// <summary>
        /// 快速检查：有没有任何可用访问入口（走同一缓存，结果一致）。
        /// </summary>
        public static bool AnyUsableAccess(Pawn pawn)
        {
            return AllUsableAccesses(pawn).Count > 0;
        }

        private static List<CoreAccess> BuildAccessList(Pawn pawn)
        {
            var result = new List<CoreAccess>();
            if (pawn?.Map == null) return result;

            var allCores = Core.LedgerItemCollector.GetAllUsableCores(pawn.Map);
            if (allCores.Count == 0) return result;

            // 【4.0「纯轮椅」】不再分有芯片 / 无芯片。3.0 是三层：
            //   ① 有芯片 → 隔空取（不看可达性）
            //   ② 无芯片 → 走代理点/接口到核心再取（要求 pawn.CanReach）
            //   ③ requireChipForCoreAccess 打开 → 无芯片 = 完全没入口
            //
            // 用户拍板「核心一放就是完全体，全部无损直接隔空获取，不需要任何额外
            // hediff / 建筑」，所以 ①②③ 全拆：所有人的行为与"有芯片"一致。
            //
            // 这也正是「为什么有时候能隔空取物有时候不行」的答案：旧代码里换个 pawn
            // （有没有芯片）或换个位置（第 ② 层的 CanReach 判定）就会翻面。
            //
            // 唯一保留的门是核心必须通电 —— 那是核心自身状态，与访问方式无关（见 IsUsable）。
            //
            // 【4.0 暂不做跨图】只收本图核心；同 NetworkName 的远程核心逻辑随之作废。
            for (int i = 0; i < allCores.Count; i++)
            {
                Building_StorageCore core = allCores[i];
                if (core == null || core.Map != pawn.Map) continue;
                result.Add(new CoreAccess { ledgerCore = core, proxyCore = core });
            }
            return result;
        }

        public static bool IsUsable(Building_StorageCore core) =>
            core != null && core.Spawned && !core.Destroyed && core.Powered;

        public static bool HasReachableProxy(Pawn pawn, Building_StorageCore core)
        {
            foreach (var c in core.GetProxyCells())
            {
                if (c.InBounds(pawn.Map) && pawn.CanReach(c, PathEndMode.Touch, Danger.Deadly))
                    return true;
            }
            // 接口都不可达时，检查核心自身交互格（兜底）
            if (core.InteractionCell.IsValid && core.InteractionCell.InBounds(pawn.Map)
                && pawn.CanReach(core.InteractionCell, PathEndMode.Touch, Danger.Deadly))
                return true;
            return false;
        }

        /// <summary>
        /// 【4.0「纯轮椅」】恒返回 <see cref="IntVec3.Invalid"/> —— 表示"不需要走位"。
        ///
        /// 调用方（<c>JobDriver_DS_Withdraw</c> / <c>WorkGiver_DS_WithdrawForConstruct</c>）
        /// 用 <c>proxy.IsValid</c> 决定要不要插一个 goto toil；Invalid 时材料直接到手，
        /// 与消耗/取药那几条链的隔空取物保持一致。
        ///
        /// 3.0 这里是"按芯片决定走不走位"：有芯片 → Invalid，无芯片 → 最近接口/核心交互格。
        /// 用户拍板核心一放就是完全体，走出去这一步就不该再有。
        /// （接口建筑本身也属批 3 删除项。）
        /// </summary>
        public static IntVec3 PickProxyCell(Pawn pawn, Building_StorageCore core)
        {
            return IntVec3.Invalid;
        }
    }
}
