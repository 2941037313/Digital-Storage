using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Backpack
{
    /// <summary>
    /// 「背包」植入判据 + 状态同步的**唯一入口**（三个 Harmony 钩子共用，避免判据漂移）。
    ///
    /// <para><b>谁该有</b>：玩家派系的殖民者（含奴隶）+ 殖民地机械族。
    /// <b>囚犯不给</b>（用户拍板）：囚犯不干活、也不该碰核心，给他们挂背包只会在健康页多一行噪音。</para>
    ///
    /// <para>不需要注册成 haul source：原版 bill 的容器扫描要求 <c>item is Thing</c>
    /// （<c>WorkGiver_DoBill.cs:483</c>），HediffComp 永远进不去。
    /// 详见 <see cref="HediffComp_Backpack"/> 的类注释。</para>
    /// </summary>
    internal static class BackpackImplant
    {
        private const string HediffDefName = "DS_CoreBackpack";
        private static HediffDef def;
        private static bool defResolved;

        private static HediffDef Def
        {
            get
            {
                if (!defResolved)
                {
                    def = DefDatabase<HediffDef>.GetNamedSilentFail(HediffDefName);
                    defResolved = true;
                }
                return def;
            }
        }

        internal static bool ShouldHave(Pawn pawn)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null) return false;
            if (pawn.Faction != Faction.OfPlayer) return false;
            if (pawn.IsPrisoner) return false; // 用户拍板：囚犯不给
            return pawn.RaceProps.Humanlike || pawn.RaceProps.IsMechanoid;
        }

        /// <summary>幂等：该有的补上，不该有的摘掉。摘除会走 <c>CompPostPostRemoved</c>（内容物落地，防丢物）。</summary>
        internal static void Sync(Pawn pawn)
        {
            HediffDef hediffDef = Def;
            if (hediffDef == null || pawn == null || pawn.health == null || pawn.health.hediffSet == null) return;

            Hediff existing = pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef);
            if (ShouldHave(pawn))
            {
                if (existing == null) pawn.health.AddHediff(hediffDef);
            }
            else if (existing != null)
            {
                pawn.health.RemoveHediff(existing);
            }
        }
    }

    /// <summary>
    /// 植入 / 摘除。用 <c>Pawn.SpawnSetup</c> 补挂：地图载入时 pawn 会重新 Spawn ⇒ 读档也覆盖；
    /// 新加入的殖民者同样走这里。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), "SpawnSetup")]
    internal static class Patch_AutoImplantBackpack
    {
        [HarmonyPostfix]
        private static void Postfix(Pawn __instance)
        {
            BackpackImplant.Sync(__instance);
            // 【这里原本有 StripLegacy30Chip —— 已随墓碑 Def 一起删除（用户 2026-10-02 拍板）】
            //
            // 它做的是「摘掉 3.0 的终端植入体并退回一枚终端芯片」（那玩意值 1000 银）。
            // 决定不再携带 Legacy30_Compat.xml 之后，这条迁移**自然作废**，因为：
            //   HediffDef 找不到 ⇒ ScribeExtractor.SaveableFromNode 捕获异常返回 null
            //   ⇒ HediffSet.ExposeData:251 的 hediffs.RemoveAll(x => x == null) 把它丢掉，
            //     只留一行 "had some null hediffs."
            // 也就是说旧存档里的植入体**根本解析不出来**，没有东西可摘、也没有东西可退。
            // 顺带：那一版的墓碑 Def 还会每次启动刷 3 条 config error
            // （ResearchProjectDef 要求 researchViewY >= 0，我却为了藏到页签外写了 -3；
            //   墓碑建筑基类给了 minifiedDef 却没给 thingCategories）。
            // 旧存档现在会看到几条一次性「找不到 Def」+ 几条落物 NRE 栈 —— 已接受。
        }
    }

    /// <summary>
    /// 囚犯 ↔ 殖民者的**身份切换点**。
    ///
    /// <para>只挂 <c>SpawnSetup</c> 会漏掉"游戏中途被俘 / 被招募"（两者都不重新 Spawn）：
    /// 被俘的殖民者会**留着**背包，被招募的囚犯要等下次读档才拿到。
    /// <c>Pawn_GuestTracker.SetGuestStatus</c> 是权威切换点（俘虏 = 玩家派系 + Prisoner；
    /// 招募 = 清空 guest 状态），postfix 时状态已经落定，两个方向都能判对。</para>
    /// </summary>
    [HarmonyPatch(typeof(Pawn_GuestTracker), "SetGuestStatus")]
    internal static class Patch_BackpackOnGuestStatusChanged
    {
        // pawn 是 private 字段 ⇒ 用 Harmony 的下划线字段注入
        [HarmonyPostfix]
        private static void Postfix(Pawn ___pawn)
        {
            BackpackImplant.Sync(___pawn);
        }
    }

    /// <summary>
    /// 死亡时清空背包。
    ///
    /// <para><b>为什么不能只靠 <c>CompPostTickInterval</c></b>：
    /// <c>Pawn_HealthTracker.HealthTickInterval</c> 第一行就是 <c>if (Dead) return;</c>
    /// （<c>Pawn_HealthTracker.cs:1071</c>）⇒ 尸体身上的 hediff 永远等不到周期检查，
    /// 而尸体/hediff 之后也不会被移除（<c>CompPostPostRemoved</c> 同样不会来）。
    /// 背包内容物会跟着 hediff 对象一起烂在存档里 —— 那是丢物。</para>
    ///
    /// <para>先试退回核心（干净、不用搬运工），退不掉（没核心 / 断电）才落地。</para>
    /// </summary>
    [HarmonyPatch(typeof(Pawn), "Kill")]
    internal static class Patch_BackpackEjectOnDeath
    {
        [HarmonyPostfix]
        private static void Postfix(Pawn __instance)
        {
            HediffComp_Backpack bag = HediffComp_Backpack.For(__instance);
            if (bag == null || bag.Count == 0) return;

            bag.ReturnContentsToCore();
            if (bag.Count > 0) bag.EjectAll();
        }
    }
}
