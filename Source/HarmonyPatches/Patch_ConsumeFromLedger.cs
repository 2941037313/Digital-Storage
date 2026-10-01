using DigitalStorage.AI;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// I1.5：自动消耗接入。
    /// 当原版 JobGiver 找不到地图上的实物时，查账本 → 派 JobDriver_DS_Consume。
    /// </summary>

    // ---- 4.6.2：食物 ----
    [HarmonyPatch(typeof(JobGiver_GetFood), "TryGiveJob")]
    static class Patch_GetFood
    {
        static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result != null) return;
            if (!ConsumePatchUtil.ShouldTry(pawn)) return;
            if (!CanUseCoreFood(pawn)) return;
            __result = ConsumptionHelper.TryCreateJob(pawn,
                t => t.def.IsNutritionGivingIngestible);
        }

        private static bool CanUseCoreFood(Pawn pawn)
        {
            // 8.1 bugfix(社区反馈:机械体吃饭):机械体没有食物需求(needs.food==null),
            // 原版 JobGiver_GetFood 因无需求返回 null,这里不能接管派饭——
            // 否则玩家派系机械体会不停从核心吃食物(食物凭空消耗,机械体不消化)。
            // v3-bug1 修了派系/囚犯过滤,漏了机械体(v3-bug1 后仍会吃)。
            if (pawn.needs?.food == null) return false;
            if (pawn.Faction == Faction.OfPlayer) return true;
            if (pawn.IsPrisonerOfColony) return true;
            if (Hediff_TerminalImplant.HasTerminalImplant(pawn)) return true;
            return false;
        }
    }

    // ---- 4.6.3：成瘾品 ----
    [HarmonyPatch(typeof(JobGiver_TakeDrugsForDrugPolicy), "TryGiveJob")]
    static class Patch_TakeDrugs
    {
        static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result != null) return;
            if (!ConsumePatchUtil.ShouldTry(pawn)) return;
            var policy = pawn.drugs?.CurrentPolicy;
            if (policy == null) return;
            for (int i = 0; i < policy.Count; i++)
            {
                var drugDef = policy[i].drug;
                if (!pawn.drugs.ShouldTryToTakeScheduledNow(drugDef)) continue;
                var job = ConsumptionHelper.TryCreateJob(pawn, t => t.def == drugDef);
                if (job != null) { __result = job; return; }
            }
        }
    }

    // ---- 4.6.4：娱乐消耗品 ----
    [HarmonyPatch(typeof(JoyGiver_Ingest), "TryGiveJob")]
    static class Patch_JoyIngest
    {
        static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result != null) return;
            if (!ConsumePatchUtil.ShouldTry(pawn)) return;
            foreach (var def in DefDatabase<JoyGiverDef>.AllDefs)
            {
                if (!(def.Worker is JoyGiver_Ingest)) continue;
                if (def.thingDefs == null) continue;
                foreach (var td in def.thingDefs)
                {
                    var job = ConsumptionHelper.TryCreateJob(pawn, t => t.def == td);
                    if (job != null) { __result = job; return; }
                }
            }
        }
    }

    /// <summary>节流：防同 tick 内反复创同一 job 导致 10 jobs/tick 循环。</summary>
    static class ConsumePatchUtil
    {
        private static int lastFailTick = -1;
        private static int lastFailPawnID = -1;

        public static bool ShouldTry(Pawn pawn)
        {
            // 同 tick 同 pawn 刚失败过 → 不重试
            if (Find.TickManager.TicksGame == lastFailTick && pawn.thingIDNumber == lastFailPawnID)
                return false;

            // pawn 已有 ConsumeFromLedger job 在队列里 → 不重复创
            if (pawn.CurJob?.def == DigitalStorage_JobDefOf.DigitalStorage_ConsumeFromLedger)
                return false;
            for (int i = 0; i < pawn.jobs.jobQueue.Count; i++)
            {
                if (pawn.jobs.jobQueue[i].job.def == DigitalStorage_JobDefOf.DigitalStorage_ConsumeFromLedger)
                    return false;
            }
            return true;
        }

        public static void NotifyFail(Pawn pawn)
        {
            lastFailTick = Find.TickManager.TicksGame;
            lastFailPawnID = pawn.thingIDNumber;
        }
    }
}
