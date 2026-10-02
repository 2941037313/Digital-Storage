using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Performance
{
    /// <summary>
    /// <b>大批量标记时的"元气泡"洪泛</b>（原版标记路径的第二笔开销）。
    ///
    /// <para><b>原版代价</b>：<c>DesignationManager.AddDesignation</c> 每加**一个**标记都无条件调
    /// <c>FleckMaker.ThrowMetaPuffs(target)</c>（源码 <c>DesignationManager.cs:189</c>），
    /// 而它每个格子喷 <b>4–6 个 fleck</b>（<c>FleckMaker.cs:100-108</c>）。拖框一次性标记
    /// 5000 格 ⇒ 一瞬间 2.5 万个 fleck 对象，既是一次分配洪峰，之后每帧还要走更新与绘制。
    /// 唯一已有的保护只是 <c>if (Find.TickManager.Paused) return;</c> —— 暂停时免费，**不暂停就没有任何上限**。</para>
    ///
    /// <para><b>本补丁</b>：给这条路径一个每 tick 上限（默认 16 次调用 ≈ 80 个 fleck）。
    /// 连续放置一栋 3×3 建筑是 9 次调用，正常游玩完全够用；一次性刷 2000 格时，
    /// 前十几个格子照样有反馈，其余不再喷 —— 与"暂停时原版干脆一个都不喷"相比，
    /// 这仍然比原版**更慷慨**，所以不存在"信息丢失"的问题。</para>
    /// </summary>
    [HarmonyPatch(typeof(FleckMaker), nameof(FleckMaker.ThrowMetaPuffs), new[] { typeof(TargetInfo) })]
    internal static class Patch_FleckMaker_MetaPuffThrottle
    {
        private const int MaxCallsPerTick = 16;

        private static int lastTick = -1;

        private static int callsThisTick;

        private static bool Prefix()
        {
            if (!Settings.DigitalStorageSettings.perfOptimizationsEnabled) return true;
            // 原版在这个重载里第一句就是"暂停直接 return"，先放行省一次计数
            if (Find.TickManager == null || Find.TickManager.Paused) return true;

            int tick = GenTicks.TicksGame;
            if (tick != lastTick)
            {
                lastTick = tick;
                callsThisTick = 0;
            }
            if (callsThisTick >= MaxCallsPerTick) return false;
            callsThisTick++;
            return true;
        }
    }
}
