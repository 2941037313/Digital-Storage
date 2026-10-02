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
            StripLegacy30Chip(__instance);
            BackpackImplant.Sync(__instance);
        }

        /// <summary>
        /// 3.0 → 4.0 迁移：摘掉 3.0 的「终端植入体」并退回一枚终端芯片。
        ///
        /// <para>4.0 是纯轮椅，不需要芯片；而墓碑 HediffDef 让旧存档能正常解析，
        /// 不摘掉的话它会作为一枚"无功能植入体"永远留在健康页里。
        /// <c>HediffDef.spawnThingOnRemoved</c> 只有**手术配方**会用（不是 <c>RemoveHediff</c>），
        /// 所以这里手动把芯片放回脚下 —— 那玩意值 1000 银，别让玩家白丢。</para>
        /// </summary>
        private static void StripLegacy30Chip(Pawn pawn)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null) return;

            HediffDef legacyDef = DefDatabase<HediffDef>.GetNamedSilentFail("DigitalStorage_TerminalImplant");
            if (legacyDef == null) return;

            Hediff hediff = pawn.health.hediffSet.GetFirstHediffOfDef(legacyDef);
            if (hediff == null) return;

            pawn.health.RemoveHediff(hediff);

            ThingDef chipDef = DefDatabase<ThingDef>.GetNamedSilentFail("DigitalStorage_TerminalChip");
            Map map = pawn.MapHeld;
            if (chipDef == null || map == null) return;

            GenPlace.TryPlaceThing(ThingMaker.MakeThing(chipDef), pawn.PositionHeld, map, ThingPlaceMode.Near);
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
