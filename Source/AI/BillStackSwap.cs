using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// <b>把"我们自己的 bill"临时换进工作台，借原版选料器用一次，然后立刻还原。</b>
    ///
    /// <para><b>为什么需要</b>：<c>WorkGiver_DoBill.JobOnThing</c> 只认台子 <c>BillStack</c> 里**已有**的 bill；
    /// 而用户拍板"全程在面板操作、不需要操作工作台" ⇒ 台子上不会再有 bill。
    /// 换进我们那条 bill 之后，原版那一整条链（含营养换算、混料规则、stuff 取整、
    /// 核心容器候选、地面候选）**一个字都不用重写**。</para>
    ///
    /// <para><b>为什么安全</b>：<c>Building_WorkTable.billStack</c> 是 <b>public 字段</b>
    /// （<c>RimWorld\Building_WorkTable.cs:8</c>），交换是两次字段写入，且在 <c>try/finally</c> 里还原 ——
    /// 期间不会被别的东西观察到（同步单线程，交换与调用之间没有 yield）。
    /// 非 <c>Building_WorkTable</c> 的自定义 <c>IBillGiver</c>（某些 mod）走反射兜底找同名字段。</para>
    /// </summary>
    internal static class BillStackSwap
    {
        private static readonly Dictionary<Type, FieldInfo> fieldCache = new Dictionary<Type, FieldInfo>();

        /// <summary>
        /// 换入。<paramref name="original"/> 返回被换下来的原栈（调用方必须原样还原）；
        /// 返回 false 表示这种台子换不了（那就别用它选料）。
        /// </summary>
        public static bool TrySwapIn(Thing bench, BillStack replacement, out BillStack original)
        {
            original = null;
            if (bench == null || replacement == null) return false;

            Building_WorkTable table = bench as Building_WorkTable;
            if (table != null)
            {
                original = table.billStack;
                table.billStack = replacement;
                return true;
            }

            FieldInfo f = FieldFor(bench.GetType());
            if (f == null) return false;
            original = (BillStack)f.GetValue(bench);
            f.SetValue(bench, replacement);
            return true;
        }

        public static void Restore(Thing bench, BillStack original)
        {
            if (bench == null || original == null) return;

            Building_WorkTable table = bench as Building_WorkTable;
            if (table != null)
            {
                table.billStack = original;
                return;
            }

            FieldInfo f = FieldFor(bench.GetType());
            if (f != null) f.SetValue(bench, original);
        }

        private static FieldInfo FieldFor(Type t)
        {
            FieldInfo f;
            if (fieldCache.TryGetValue(t, out f)) return f;

            f = AccessTools.Field(t, "billStack");
            if (f != null && !typeof(BillStack).IsAssignableFrom(f.FieldType)) f = null;
            fieldCache[t] = f;
            return f;
        }
    }
}
