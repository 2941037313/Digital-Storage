using LudeonTK;
using RimWorld;
using Verse;

namespace DigitalStorage.Backpack
{
    /// <summary>
    /// 背包实验的入口（开发者菜单 → DigitalStorage）。
    /// HediffComp 没有 gizmo 钩子（<c>HediffComp</c> 不提供 CompGetGizmos*），所以走开发者菜单：
    /// <c>ToolMapForPawns</c> 让"点地图上某个小人"成为参数，正好用于逐个 pawn 验证。
    /// </summary>
    internal static class BackpackDebugActions
    {
        private static HediffComp_Backpack Bag(Pawn p)
        {
            HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail("DS_CoreBackpack");
            if (def == null || p?.health == null) return null;
            return p.health.hediffSet.GetFirstHediffOfDef(def)?.TryGetComp<HediffComp_Backpack>();
        }

        [DebugAction("DigitalStorage", "背包：取 1 件料到背包", actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void TakeOne(Pawn p)
        {
            HediffComp_Backpack bag = Bag(p);
            if (bag == null) { Messages.Message("这个 pawn 没有背包 hediff。", MessageTypeDefOf.RejectInput); return; }
            bag.TakeOneFromCore();
        }

        [DebugAction("DigitalStorage", "背包：打印诊断", actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Dump(Pawn p)
        {
            Bag(p)?.Dump();
        }

        [DebugAction("DigitalStorage", "背包：切换 HaulSourceEnabled", actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Toggle(Pawn p)
        {
            HediffComp_Backpack bag = Bag(p);
            if (bag == null) return;
            bag.SetSourceEnabled(!bag.HaulSourceEnabled);
            Messages.Message("HaulSourceEnabled → " + bag.HaulSourceEnabled, MessageTypeDefOf.NeutralEvent);
        }

        [DebugAction("DigitalStorage", "背包：清空并落地", actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Clear(Pawn p)
        {
            Bag(p)?.DropEverything();
        }
    }
}
