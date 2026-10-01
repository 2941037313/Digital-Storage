using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Backpack
{
    /// <summary>
    /// 每个殖民者 / 殖民地机械族自动植入「背包」（用户拍板的决策 1）。
    ///
    /// <para>用 <c>Pawn.SpawnSetup</c> 补挂：地图载入时 pawn 会重新 Spawn ⇒ 读档也覆盖；
    /// 新加入的殖民者同样走这里。同时负责把背包注册进 haul source 表
    /// （<c>Thing.SpawnSetup</c> 只给 Thing 自动注册，HediffComp 得自己来）。</para>
    /// </summary>
    [HarmonyPatch(typeof(Pawn), "SpawnSetup")]
    internal static class Patch_AutoImplantBackpack
    {
        [HarmonyPostfix]
        private static void Postfix(Pawn __instance)
        {
            Pawn pawn = __instance;
            if (pawn?.health == null) return;
            if (pawn.Faction != Faction.OfPlayer) return;
            // 殖民者（含奴隶）+ 玩家的机械族
            if (!pawn.RaceProps.Humanlike && !pawn.RaceProps.IsMechanoid) return;

            HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail("DS_CoreBackpack");
            if (def == null) return;

            HediffWithComps hediff = pawn.health.hediffSet.GetFirstHediffOfDef(def) as HediffWithComps;
            if (hediff == null)
            {
                hediff = pawn.health.AddHediff(def) as HediffWithComps;
            }
            (hediff?.TryGetComp<HediffComp_Backpack>())?.EnsureRegistered();
        }
    }
}
