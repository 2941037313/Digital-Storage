using System;
using DigitalStorage.Settings;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DigitalStorage.Performance
{
    /// <summary>
    /// <b>每帧气泡（fleck）创建上限</b> —— 治"标记完闪一下、卡几秒"。
    ///
    /// <para><b>实测症状</b>（数字存储 4.0，用户 250×250 全图标记）：标记刚落、代理还没开工的
    /// 那几秒，<c>Flecks</c> 稳定吃 2.9~3.1ms/帧（峰值 29ms），代理一开工就掉到 0.08ms。
    /// 原因：<c>DesignationManager.AddDesignation</c> 每加**一个**标记都喷一次元气泡
    /// （每次 4~6 个 fleck），全图一次标记 = 几万个 fleck 一次性创建，
    /// 它们各自存活 1~2 秒并**逐帧绘制** ⇒ 视觉上"闪一下"，帧率上"卡几秒"。</para>
    ///
    /// <para><b>为什么另加这一道</b>：已有的 <c>Patch_FleckMaker_MetaPuffThrottle</c>
    /// 只限"每 tick 16 次调用"，实测没能拦住这个洪泛（说明还有别的洪泛源，或调用计数与实际
    /// 创建量不成比例）。这里改成**在 fleck 创建这一层**限总量：每帧最多创建
    /// <see cref="DigitalStorageSettings.fleckBudgetPerFrame"/> 个，
    /// 超出的**直接不创建**。teck 是纯视觉对象，不做任何游戏逻辑，丢掉只影响观感。</para>
    ///
    /// <para>默认 500/帧（≈3 万/秒）：正常游玩（殖民地零星特效）远低于这个量，完全无感；
    /// 只有病态洪泛（几万个一次性创建）会被削平。</para>
    ///
    /// <para><b>0 = 无限制</b>（回到原版行为）。</para>
    /// </summary>
    [HarmonyPatch(typeof(FleckManager), nameof(FleckManager.CreateFleck))]
    internal static class Patch_FleckManager_CreationBudget
    {
        private static int frame = -1;

        private static int createdThisFrame;

        private static bool Prefix()
        {
            int cap = DigitalStorageSettings.fleckBudgetPerFrame;
            if (cap <= 0) return true;
            if (!DigitalStorageSettings.perfOptimizationsEnabled) return true;

            int now = Time.frameCount;
            if (now != frame)
            {
                frame = now;
                createdThisFrame = 0;
            }
            if (createdThisFrame >= cap)
            {
                DevDrawProfiler.Bump("造泡拦", 1);
                return false;
            }
            createdThisFrame++;
            DevDrawProfiler.Bump("造泡", 1);
            return true;
        }
    }
}
