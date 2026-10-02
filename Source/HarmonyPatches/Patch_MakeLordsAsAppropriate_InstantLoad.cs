using System.Collections.Generic;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 【装载直塞接缝】把"住在数字存储核心里的货"直接塞进装载容器，不派 pawn 走过去搬。
    ///
    /// <para><b>为什么挂 <c>MakeLordsAsAppropriate</c> 而不是别的</b>：
    /// 装载清单（<c>leftToLoad</c>）定稿之后<b>不能立刻搬</b> ——
    /// <c>Dialog_LoadTransporters.AssignTransferablesToRandomTransporters:600-615</c> 的收尾循环
    /// 会把 <c>innerContainer</c> 里"leftToLoad 已经不要了"的东西<b>丢回地上</b>。
    /// 所以任何挂在 <c>AddToTheToLoadList</c>（清单边建边搬）上的实现，塞进去的货都会被丢出来。</para>
    ///
    /// <para>而原版把"装载清单已定稿、收尾已跑完"这件事全部收敛到
    /// <c>MakeLordsAsAppropriate</c>：全 vanilla 只有 5 个调用点，<b>没有例外</b>。</para>
    /// <code>
    /// Dialog_LoadTransporters.cs:439 / :453   玩家点确认（两条分支）→ 穿梭机 / 空投仓 / 载具货舱
    /// CompShuttle.cs:689 / :702               任务穿梭机 autoload（CheckAutoload，120 tick 轮询）
    /// Dialog_EnterPortal.cs:117               传送门装货
    /// </code>
    ///
    /// <para>于是两个 postfix 就覆盖了全部装载入口，<b>无反射、无 tick 扫描</b>，
    /// 而且任何 mod 只要走原版装载流程也自动受益（含 <c>CompTransporter</c> 的各路 mod 子类）。</para>
    ///
    /// <para><b>踩过/查过的两个"看起来更通用"的死路</b>：</para>
    /// <list type="bullet">
    /// <item><c>CompTransporter.CompTick</c> —— <c>CompShuttle</c> 覆写了 <c>CompTick:473</c>
    /// 且<b>不调 base</b>，穿梭机（正是最常用的那个）根本收不到 postfix。</item>
    /// <item><c>LoadTransportersJobUtility.FindThingToLoad</c>（拉模型：pawn 想搬时才搬）——
    /// 会被 <c>CompTransporter.CompTick</c> 的"没人能装"判定撞上，误报"无法装载更多"；
    /// 而且所有殖民者都不思考装载时永远不触发。</item>
    /// </list>
    ///
    /// <para>具体搬运逻辑见 <see cref="ContainerAutoLoader"/>。</para>
    /// </summary>
    [HarmonyPatch(typeof(TransporterUtility), nameof(TransporterUtility.MakeLordsAsAppropriate))]
    internal static class Patch_TransporterUtility_MakeLordsAsAppropriate
    {
        private static void Postfix(List<CompTransporter> transporters, Map map)
        {
            ContainerAutoLoader.FillTransporters(transporters, map);
        }
    }

    /// <summary>
    /// 传送门版。结构与运输舱完全平行（<c>MapPortal</c> 自己实现了一套
    /// <c>leftToLoad</c> / <c>AddToTheToLoadList</c> / <c>SubtractFromToLoadList</c>），
    /// 只是接缝在 <c>EnterPortalUtility.MakeLordsAsAppropriate</c>。
    ///
    /// <para>注意传送门的"容器"是漏斗：<c>PortalContainerProxy.TryAdd</c> 直接
    /// <c>GenDrop.TryDropSpawn(..., portal.GetOtherMap(), ...)</c> —— 直塞 = 货直接到对面地图，
    /// 正是"进传送门"的原义。</para>
    /// </summary>
    [HarmonyPatch(typeof(EnterPortalUtility), nameof(EnterPortalUtility.MakeLordsAsAppropriate))]
    internal static class Patch_EnterPortalUtility_MakeLordsAsAppropriate
    {
        private static void Postfix(MapPortal portal)
        {
            ContainerAutoLoader.FillPortal(portal);
        }
    }
}
