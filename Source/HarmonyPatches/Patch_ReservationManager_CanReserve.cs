using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>让代理建筑"占住"目标</b>：用读侧补丁代替做不到的真预约。
    ///
    /// <para><b>为什么必须加这个</b>：<c>ReservationManager.Reserve(pawn, job: null, …)</c> 会被原版
    /// 直接 <c>Log.Warning + return false</c>；而拿"假 Job"去预约会把那个 Job 写进存档、
    /// 读档找不回来 ⇒ 永久占着某块矿（More Organs 的 R3 结论）。所以我们的"认领表"只有自己人看得见，
    /// 原版殖民者一查 <c>pawn.CanReserve(t, …)</c>（<c>MineAIUtility.JobOnThing</c> 里那道闸门）是绿的
    /// ⇒ **实测 bug：殖民者跑去挖代理正在挖的那块**。</para>
    ///
    /// <para>解法：在 <c>CanReserve</c> 的读侧，把"已被代理认领的目标"判成订不到。
    /// ① 不往存档写任何东西（无假 Job、无幽灵预约）；
    /// ② 这是热路径，所以第一道就是 <see cref="DigitalWorkerClaims.AnyClaims"/> 的 O(1) 早退 ——
    ///    没有代理在干活时零开销；
    /// ③ 尊重 <c>ignoreOtherReservations</c>（玩家强制 / 无视预约的那条路）—— 那种情况不拦，
    ///    否则"右键强制让殖民者去挖"会被我们挡掉，是坏 UX。</para>
    ///
    /// <para>⚠️ 重载必须显式给参数类型：本 mod 被 <c>Toils_Goto.GotoThing</c> 的
    /// <c>AmbiguousMatchException</c> 坑过一次（那次导致 <c>PatchAll</c> 中断、它之后所有补丁静默不挂）。</para>
    /// </summary>
    [HarmonyPatch(typeof(ReservationManager), "CanReserve",
        new[] { typeof(Pawn), typeof(LocalTargetInfo), typeof(int), typeof(int), typeof(ReservationLayerDef), typeof(bool) })]
    internal static class Patch_ReservationManager_CanReserve_ProxyClaims
    {
        private static void Postfix(Pawn claimant, LocalTargetInfo target, bool ignoreOtherReservations, ref bool __result)
        {
            if (!__result || claimant == null || ignoreOtherReservations) return;
            if (!DigitalWorkerClaims.AnyClaims) return;

            Thing t = target.Thing;
            if (t == null) return;

            Map map = t.MapHeld;
            if (map == null) return;

            CompDigitalWorker owner = DigitalWorkerClaims.OwnerOf(map, t);
            if (owner == null) return;
            if (owner.WorkerIfCreated == claimant) return;   // 我们自己的工人要能过

            __result = false;
        }
    }
}
