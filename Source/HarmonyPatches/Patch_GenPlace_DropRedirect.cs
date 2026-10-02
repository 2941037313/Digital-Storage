using System;
using System.Reflection;
using DigitalStorage.Components;
using DigitalStorage.Core;
using DigitalStorage.Performance;
using HarmonyLib;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>代理干活时的掉落重定向</b>：<c>GenPlace.TryPlaceThing</c> 的落地改成直塞数字存储。
    ///
    /// <para>为什么挂这个重载：<c>GenPlace</c> 有两个 public 重载，
    /// 不带 <c>out</c> 的那个只是**转调**带 <c>out Thing lastResultingThing</c> 的这个
    /// （<c>GenPlace.cs:27-35</c>），所以只挂后者就覆盖了全部调用点 ——
    /// 挖掘产物、收获产物、拆除返还材料，一律经过它。</para>
    ///
    /// <para>⚠️ <b>只能手工挂</b>：<c>out</c> 参数在特性里写不出来
    /// （特性实参必须是常量/typeof/数组创建，<c>typeof(Thing).MakeByRefType()</c> 是方法调用）。
    /// 所以本类没有 <c>[HarmonyPatch]</c>，由 <see cref="HarmonyInit"/> 调 <see cref="Install"/>。</para>
    ///
    /// <para>作用域：只有 <c>CompDigitalWorker.CompTick</c> 开着
    /// <see cref="DigitalDropRedirect"/> 的那一小段（假工人在干活）才接管；
    /// 其余任何时候（原版搬运工、玩家操作、其它 mod）<c>Active</c> 为 false，一行分支就返回原版。</para>
    ///
    /// <para>失败即放行：核心不收 / 收不下 / 没有可用核心 ⇒ 返回 true 走原版落地，绝不吞东西。
    /// 因为 <see cref="DigitalDropRedirect.TryIngestUnspawned"/> 内部**不会**再调
    /// <c>TryPlaceThing</c>（那是 <c>CompAutoIngest.TryIngest</c> 的兜底逻辑，这里已避开），
    /// 所以不存在自我递归。</para>
    /// </summary>
    internal static class Patch_GenPlace_DropRedirect
    {
        private static readonly Type[] TargetSignature =
        {
            typeof(Thing),
            typeof(IntVec3),
            typeof(Map),
            typeof(ThingPlaceMode),
            typeof(Thing).MakeByRefType(),
            typeof(Action<Thing, int>),
            typeof(Predicate<IntVec3>),
            typeof(Nullable<Rot4>),
            typeof(int)
        };

        internal static void Install(Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(GenPlace), "TryPlaceThing", TargetSignature);
            if (target == null)
            {
                Log.Error("[DigitalStorage] 找不到 GenPlace.TryPlaceThing(out Thing, …) 重载 —— 掉落直塞未挂上"
                          + "（原版签名变了？本功能会静默退回原版落地行为）。");
                return;
            }
            harmony.Patch(target,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(Patch_GenPlace_DropRedirect), nameof(Prefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(Patch_GenPlace_DropRedirect), nameof(Postfix))));
        }

        /// <summary>本帧最近一次进来是不是被我们接管的（决定 Postfix 把它记到哪个 key）。</summary>
        private static bool tookOver;

        private static long stamp;

        private static bool Prefix(Thing thing, Map map, out Thing lastResultingThing, ref bool __result)
        {
            lastResultingThing = null;
            // ⚠️ 探针的开关**不能**闸住功能本身：DevMode 关掉时 stamp 保持 0（=不记时），
            //    但直塞照常工作。
            if (DevDrawProfiler.Enabled) stamp = DevDrawProfiler.Stamp();

            if (!DigitalDropRedirect.Active) return true;

            Building_StorageCore core = DigitalDropRedirect.Core;
            if (core == null || map == null || core.Map != map) return true;
            if (!DigitalDropRedirect.TryIngestUnspawned(core, thing)) return true;

            tookOver = true;
            lastResultingThing = thing;
            __result = true;
            return false;
        }

        /// <summary>
        /// 探针：<c>Drops</c> = 走原版落地花了多少（含 <c>TryFindPlaceSpotNear</c> + 注册），
        /// <c>DropsDirect</c> = 直塞花了多少。两者的差值就是 (b) 的真实收益 ——
        /// 别再用推测，这两个数直接回答"直塞值不值"。
        /// </summary>
        private static void Postfix()
        {
            if (!DevDrawProfiler.Enabled || stamp == 0L) return;
            string key = tookOver ? "DropsDirect" : "Drops";
            DevDrawProfiler.Add(key, DevDrawProfiler.Ms(stamp));
            DevDrawProfiler.Bump(tookOver ? "直塞/帧" : "落地/帧", 1);
            stamp = 0L;
            tookOver = false;
        }
    }
}
