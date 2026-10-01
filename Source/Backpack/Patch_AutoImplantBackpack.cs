using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Backpack
{
    /// <summary>
    /// 每个殖民者 / 殖民地机械族自动植入「背包」（用户拍板的决策 1）。
    ///
    /// <para>用 <c>Pawn.SpawnSetup</c> 补挂：地图载入时 pawn 会重新 Spawn ⇒ 读档也覆盖；
    /// 新加入的殖民者同样走这里。幂等：已有该 hediff 就不再加。</para>
    ///
    /// <para><b>不需要注册成 haul source</b>：原版 bill 的容器扫描要求
    /// <c>item is Thing</c>（<c>WorkGiver_DoBill.cs:483</c>），HediffComp 永远进不去。
    /// 详见 <see cref="HediffComp_Backpack"/> 的类注释。</para>
    /// </summary>
    [HarmonyPatch(typeof(Pawn), "SpawnSetup")]
    internal static class Patch_AutoImplantBackpack
    {
        [HarmonyPostfix]
        private static void Postfix(Pawn __instance)
        {
            Pawn pawn = __instance;
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null) return;
            if (pawn.Faction != Faction.OfPlayer) return;
            // 殖民者（含奴隶）+ 玩家的机械族
            if (!pawn.RaceProps.Humanlike && !pawn.RaceProps.IsMechanoid) return;

            HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail("DS_CoreBackpack");
            if (def == null) return;
            if (pawn.health.hediffSet.GetFirstHediffOfDef(def) != null) return;

            pawn.health.AddHediff(def);
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
