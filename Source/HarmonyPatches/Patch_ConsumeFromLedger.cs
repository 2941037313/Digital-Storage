using System.Collections.Generic;
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
            if (__result != null)
            {
                // 目标住在容器里 → 原版那条消耗链执行不了，改由我们接管（否则静默卡死）
                if (!ConsumePatchUtil.IsUnexecutableContainerTarget(__result)) return;
                __result = null;
            }
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
            // 4.0：删掉了「有终端芯片也放行」那一支 —— 芯片整体砍除（Hediff_TerminalImplant 已删）；
            // faction / prisoner 两条已覆盖正常吃饭场景。
            return false;
        }
    }

    // ---- 4.6.3：成瘾品 ----
    [HarmonyPatch(typeof(JobGiver_TakeDrugsForDrugPolicy), "TryGiveJob")]
    static class Patch_TakeDrugs
    {
        static void Postfix(Pawn pawn, ref Job __result)
        {
            // 原版可能自己返回了作业，但目标住在容器里 —— JobDriver_Ingest 那条链没有
            // canGotoSpawnedParent，执行不了。这种「看着有作业、实际做不成」比返回 null 更坏：
            // 它会静默卡住，而且我们因为 __result != null 而放手。所以精确识别并接管。
            if (__result != null)
            {
                if (!ConsumePatchUtil.IsUnexecutableContainerTarget(__result)) return;
                __result = null;
            }

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
            if (__result != null)
            {
                // 目标住在容器里 → 原版那条消耗链执行不了，改由我们接管（否则静默卡死）
                if (!ConsumePatchUtil.IsUnexecutableContainerTarget(__result)) return;
                __result = null;
            }
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
        /// <summary>
        /// 诊断日志节流：**按 key 分别记**（同 key 最多 60 tick 一条）。
        /// 早先版本是全局共用一个名额，结果 GetFood 先占了、TakeDrugs 就被压掉 —— 那正是
        /// 「只有一条日志」的原因。每个 patch 有独立名额才不会互相掩盖。
        /// 仅供 <c>enableDebugLog</c> 打开时的调试输出使用（默认关闭）。
        /// </summary>
        private static readonly Dictionary<string, int> lastLogTickByKey = new Dictionary<string, int>();

        public static bool ShouldLog(string key)
        {
            int tick = Find.TickManager.TicksGame;
            int last;
            if (lastLogTickByKey.TryGetValue(key, out last) && tick - last < 60) return false;
            lastLogTickByKey[key] = tick;
            return true;
        }

        /// <summary>
        /// 这个作业的目标是不是「未 Spawned 且住在 IHaulSource 容器里」的东西。
        ///
        /// 这种目标原版的消耗链执行不了（<c>JobDriver_Ingest</c> 没有
        /// <c>canGotoSpawnedParent</c>），所以一旦 <c>__result</c> 指向它，必须由我们接管，
        /// 否则就是一个静默卡死的作业。
        ///
        /// 判据与 <c>HaulAIUtility.IsInHaulableInventory</c> 一致 ——
        /// 注意 pawn 的背包是 <c>Pawn_InventoryTracker</c>，**不是** IHaulSource，
        /// 所以原版「从别人背包拿」那条路不会被误伤。
        /// </summary>
        public static bool IsUnexecutableContainerTarget(Job job)
        {
            return IsHaulSourceContent(job?.targetA.Thing)
                || IsHaulSourceContent(job?.targetB.Thing)
                || IsHaulSourceContent(job?.targetC.Thing);
        }

        private static bool IsHaulSourceContent(Thing t)
        {
            return t != null && !t.Spawned && t.ParentHolder is IHaulSource;
        }

        private static int lastFailTick = -1;
        private static int lastFailPawnID = -1;

        public static bool ShouldTry(Pawn pawn)
        {
            // 同 tick 同 pawn 刚失败过 → 不重试
            if (Find.TickManager.TicksGame == lastFailTick && pawn.thingIDNumber == lastFailPawnID)
                return false;

            // pawn 已有 consume job 在队列里 → 不重复创
            if (pawn.CurJob?.def == DigitalStorage_JobDefOf.DigitalStorage_Consume)
                return false;
            for (int i = 0; i < pawn.jobs.jobQueue.Count; i++)
            {
                if (pawn.jobs.jobQueue[i].job.def == DigitalStorage_JobDefOf.DigitalStorage_Consume)
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
