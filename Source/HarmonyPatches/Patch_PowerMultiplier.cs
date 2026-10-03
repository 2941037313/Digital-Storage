using System;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using UnityEngine;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>电力倍率</b>（设置项「电力倍率」，1/100 ~ 100×）。挂在
    /// <c>CompPowerTrader.SetUpPowerVars</c> 上，把**耗电**那一次赋值乘上倍率。
    ///
    /// <para><b>为什么挂在这里，而不是改 <c>CompProperties_Power.basePowerConsumption</c></b>：</para>
    /// <list type="bullet">
    /// <item>那个字段是 <c>private</c>（只能反射改），而且改它就得自己维护基线快照；</item>
    /// <item><c>SetUpPowerVars</c> 是**原版唯一的"把耗电写成 <c>PowerOutput</c>"的地方**
    ///   （<c>CompPowerTrader.cs:245-258</c>），<c>PowerOutput</c> 又是电网每 tick 现算的输入
    ///   （<c>PowerNet.PowerNetTick → CurrentEnergyGainRate</c>）⇒ 在这里乘一次，
    ///   电网、检视栏（<c>CompInspectStringExtra</c> 用的是 <c>PowerOutput</c>）全都对，
    ///   而且**不需要任何基线**（每次 <c>SetUpPowerVars</c> 都是从 <c>Props</c> 重算，天然幂等）。</item>
    /// <item><c>SetUpPowerVars</c> 自带"重算"语义：原版研究完成且 <c>recalculatePower</c> 时就是这么
    ///   刷全图用电建筑的（<c>ResearchManager.cs:448</c>）⇒ 倍率改动时
    ///   <see cref="DefMultipliers.RefreshPower"/> 在同一入口再调一遍即可立即生效。</item>
    /// </list>
    ///
    /// <para><b>只缩"耗电"，不动"发电"</b>：原版把两者都塞在 <c>PowerOutput</c> 里 ——
    /// 负数 = 耗电（<c>-PowerConsumption</c> / <c>-idlePowerDraw</c>），正数 = 出力
    /// （太阳能 / 风电 / 燃油发电机的 <c>basePowerConsumption</c> 是负的 ⇒ <c>PowerOutput</c> 为正）。
    /// 倍率若把发电一起缩，设 1/100 时发电机只剩 1% 出力，等于把电网搞死 —— 那不是"省电"。</para>
    ///
    /// <para>⚠️ 读的是 <b>public 字段 <c>powerOutputInt</c></b>，不是 <c>PowerOutput</c> 属性：
    /// 那个属性的 getter 在 <c>StunnedByEMP</c> 时返回 0（<c>CompPowerTrader.cs:36-45</c>），
    /// 拿它当乘法输入会把"被 EMP 瘫痪那一下"的 0 写回字段，**永久抹掉这台机器的耗电**。</para>
    /// </summary>
    [HarmonyPatch(typeof(CompPowerTrader), "SetUpPowerVars")]
    internal static class Patch_PowerMultiplier
    {
        private static void Postfix(CompPowerTrader __instance)
        {
            if (__instance == null) return;

            float mult = DigitalStorageSettings.powerMultiplier;
            if (Mathf.Approximately(mult, 1f)) return;

            // 负数 = 耗电；正数 = 发电 ⇒ 不动。
            if (__instance.powerOutputInt < 0f)
                __instance.powerOutputInt *= mult;
        }
    }
}
