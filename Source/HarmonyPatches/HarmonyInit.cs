using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            var harmony = new Harmony("DigitalStorage.HarmonyPatches");
            PatchAllIsolated(harmony);
            // 第三方定向兼容（可选 mod）：必须在 PatchAll 之后单独装，失败也不能影响本 mod 的补丁
            Compatibility.PhinixCompatPatch.Install(harmony);
            // Log.Message 在游戏内日志窗口不显示（只有 Player.log 有），诊断一律用 Warning。
            Log.Warning("[DigitalStorage 4.0] Harmony ready." + PatchSummary);
            // 第三方兼容挂点数量（仅开发者模式；Phinix/红包未安装或改名时为 0）
            if (Prefs.DevMode)
                Log.Warning("[DigitalStorage] 第三方兼容挂点=" + Compatibility.PhinixCompatPatch.InstalledCount);
        }

        internal static string PatchSummary = "";

        /// <summary>
        /// <b>逐个补丁类隔离挂载</b>，而不是 <c>harmony.PatchAll()</c>。
        ///
        /// <para>为什么必须这样：<c>PatchAll()</c> 是**一个循环**，任何一次挂载抛异常
        /// （参数名写错 / 目标方法有重载导致 <c>AmbiguousMatchException</c> /
        /// 类里那个约定名为 <c>HarmonyInit</c> 的静态初始化器抛异常），
        /// <b>循环后面的补丁类全部静默不挂</b> —— 游戏照常启动，只是功能一个个消失，
        /// 而日志里只有一条被埋掉的报错。本 mod 2026-10-01 已经因此丢过整整一批补丁
        /// （<c>Toils_Goto.GotoThing</c> 有两个重载 ⇒ AmbiguousMatchException ⇒
        /// 之后所有补丁静默失效）。</para>
        ///
        /// <para>现在每个补丁类各自 try/catch：坏的那一个被点名报出来，其余照常生效。</para>
        /// </summary>
        private static void PatchAllIsolated(Harmony harmony)
        {
            Type[] types;
            try
            {
                types = typeof(HarmonyInit).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
                Log.Error("[DigitalStorage] 程序集类型枚举部分失败，跳过为 null 的项：" + e.Message);
            }

            int ok = 0;
            var failed = new List<string>();

            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type == null) continue;
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;

                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ok++;
                }
                catch (Exception ex)
                {
                    failed.Add(type.Name);
                    Log.Error("[DigitalStorage] 补丁类挂载失败（已跳过，其余继续）：" + type.FullName + " :: " + ex);
                }
            }

            PatchSummary = failed.Count == 0
                ? " 补丁类=" + ok
                : " 补丁类=" + ok + " 失败=" + failed.Count + " [" + string.Join(",", failed.ToArray()) + "]";
        }
    }
}
