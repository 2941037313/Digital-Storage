using HarmonyLib;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 防御 patch（原版 null 缺陷）：PawnCanAutomaticallyHaul 对未 Spawned 物品
    /// （t.Map == null）会在 t.Position.Fogged(t.Map) 处直接 NRE——
    /// GridsUtility.Fogged(IntVec3, Map) 无 null 检查（HaulAIUtility.cs:35）。
    /// 触发者：本 mod 的 GhostThing 不 Spawn 却挂进 listerThings.listsByDef
    /// （供其他 mod 扫描核心库存），原版 WorkGiver_CookFillHopper.HopperFillFoodJob
    /// 走 ThingsOfDef 遍历时拿到 ghost → PawnCanAutomaticallyHaul(ghost) → NRE。
    /// 语义：不在地图上的物品本来就不能 haul，提前返回 false 与原版意图一致，
    /// 顺带保护所有会把 despawned thing 放进索引的 mod。
    /// </summary>
    [HarmonyPatch(typeof(HaulAIUtility), "PawnCanAutomaticallyHaul")]
    static class Patch_HaulAIUtility_PawnCanAutomaticallyHaul
    {
        static bool Prefix(Pawn p, Thing t, bool forced, ref bool __result)
        {
            if (p == null || t == null || !t.Spawned)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 同款防御：PawnCanAutomaticallyHaulFast 的 t.Fogged() 内部
    /// 直接访问 t.MapHeld.fogGrid，对未 Spawned 物品同样 NRE。
    /// </summary>
    [HarmonyPatch(typeof(HaulAIUtility), "PawnCanAutomaticallyHaulFast")]
    static class Patch_HaulAIUtility_PawnCanAutomaticallyHaulFast
    {
        static bool Prefix(Pawn p, Thing t, bool forced, ref bool __result)
        {
            if (p == null || t == null || !t.Spawned)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
