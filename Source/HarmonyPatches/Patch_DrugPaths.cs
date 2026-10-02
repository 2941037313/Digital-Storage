using System;
using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 药/化学品的**成瘾、化学依赖、娱乐**三条取用路径 —— 3.0/4.0 此前只补了「预定」那条
    /// （<c>JobGiver_TakeDrugsForDrugPolicy</c>），所以核心里的药取不到。
    ///
    /// <para><b>原版有四条互不相干的路径</b>（依据 <c>DrugPolicyEntry</c> 的三个独立开关
    /// <c>allowedForAddiction</c> / <c>allowedForJoy</c> / <c>allowScheduled</c>）：</para>
    /// <list type="bullet">
    /// <item><b>成瘾</b>：<c>Need_Chemical.CurCategory &lt;= Desire</c> →
    ///   <c>JobGiver_SatisfyChemicalNeed</c>（GetPriority 9.25）← 本文件</item>
    /// <item><b>化学依赖</b>（Biotech 基因）：<c>Hediff_ChemicalDependency</c> →
    ///   <c>JobGiver_SatifyChemicalDependency</c>（9.25）← 本文件</item>
    /// <item><b>预定</b>：<c>allowScheduled</c>（默认 false）→
    ///   <c>JobGiver_TakeDrugsForDrugPolicy</c>（7.5）← <c>Patch_ConsumeFromLedger</c> 已补</item>
    /// <item><b>娱乐</b>：<c>allowedForJoy</c> → <c>JoyGiver_TakeDrug</c> 等 ← 本文件</item>
    /// </list>
    ///
    /// <para><b>为什么必须补</b>：三个 giver 的搜索（<c>GenClosest.ClosestThingReachable</c> /
    /// <c>listerThings.ThingsOfDef</c>）**全都没传 <c>lookInHaulSources</c>**，看不见未 Spawned 的
    /// 容器内容物；而"放储存区能取用、放核心不能"正是因为储存区里的东西是 Spawned 的。</para>
    ///
    /// <para><b>产出 job 前必须先取出</b>：<c>JobDriver_Ingest</c> 那条链没有
    /// <c>canGotoSpawnedParent</c>，喂它一个未 Spawned 的 targetA 会静默卡死。
    /// 所以统一 <c>ExtractToFeet</c> 取出到脚下，再交给原版的
    /// <c>DrugAIUtility.IngestAndTakeToInventoryJob</c>。</para>
    ///
    /// <para><b>校验逻辑全部镜像原版</b>（不自己发明）：原版各自的 <c>DrugValidator</c> 是
    /// <c>if (drug.Spawned) { CanReserve / IsForbidden / IsSociallyProper / IngestibleNow }</c>
    /// —— **未 Spawned 的东西整段跳过、直接放行**，所以我们只需原样搬那几条与"东西在不在容器里"
    /// 无关的判定（chemical 匹配 + 药物政策）。</para>
    /// </summary>
    internal static class ContainerDrugFinder
    {
        /// <summary>
        /// 在容器里找一件通过 <paramref name="validator"/> 的药 → 取出到脚下 → 返回它。
        /// 找不到返回 null，并给出可诊断的原因字符串。
        ///
        /// <para>额外过一道 <c>reservationManager.IsReserved</c>：原版对未 Spawned 物品会跳过
        /// <c>CanReserve</c>（见类注释），不加这道就会有两个 pawn 抢同一堆。</para>
        /// </summary>
        public static Thing FindAndExtract(Pawn pawn, Predicate<Thing> validator, out string reason)
        {
            reason = null;
            if (pawn?.Map == null) { reason = "pawn/map null"; return null; }

            Map map = pawn.Map;
            ReservationManager res = map.reservationManager;
            int seen = 0, rejected = 0, reserved = 0;

            Thing best = HaulSourceContents.FindBestIncludingRemote(map, null, t =>
            {
                seen++;
                if (!validator(t)) { rejected++; return false; }
                if (res != null && res.IsReserved(t)) { reserved++; return false; }
                return true;
            });

            if (best == null)
            {
                reason = "no match (seen=" + seen + " rejected=" + rejected + " reserved=" + reserved + ")";
                return null;
            }

            Thing taken = HaulSourceContents.ExtractToFeet(best, 1, pawn);
            if (taken == null) { reason = "extract failed (容器没货/脚下放不下)"; return null; }

            reason = "ok: " + taken.def.defName;
            return taken;
        }
    }

    // ===================================================================
    // 路径 1：成瘾（JobGiver_SatisfyChemicalNeed）
    // ===================================================================

    [HarmonyPatch(typeof(JobGiver_SatisfyChemicalNeed), "TryGiveJob")]
    internal static class Patch_SatisfyChemicalNeed
    {
        [HarmonyPostfix]
        internal static void Postfix(Pawn pawn, ref Job __result)
        {
            bool debug = DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog
                         && ConsumePatchUtil.ShouldLog("ChemNeed");
            if (debug)
                Log.Warning("[DS] ChemNeed: entered, vanilla __result=" + (__result != null ? "non-null" : "null"));

            // 原版可能返回一个「目标住在容器里」的作业 —— Ingest 链走不通，会静默卡死
            if (__result != null)
            {
                if (!ConsumePatchUtil.IsUnexecutableContainerTarget(__result)) return;
                if (debug) Log.Warning("[DS] ChemNeed: 原版作业目标在容器里，接管。");
                __result = null;
            }

            // 镜像原版：收集 ShouldSatisfy 的 Need_Chemical，按 CurLevel 升序逐个试
            List<Need> all = pawn?.needs?.AllNeeds;
            if (all == null) { if (debug) Log.Warning("[DS] ChemNeed: needs=null"); return; }

            var needs = new List<Need_Chemical>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is Need_Chemical nc && nc.CurCategory <= DrugDesireCategory.Desire)
                    needs.Add(nc);
            }
            if (needs.Count == 0) { if (debug) Log.Warning("[DS] ChemNeed: 没有需要满足的 Need_Chemical"); return; }
            needs.Sort((a, b) => a.CurLevel.CompareTo(b.CurLevel));

            for (int i = 0; i < needs.Count; i++)
            {
                Need_Chemical need = needs[i];
                Hediff_Addiction addiction = need.AddictionHediff;
                if (addiction == null) { if (debug) Log.Warning("[DS] ChemNeed: AddictionHediff=null"); continue; }

                Thing taken = ContainerDrugFinder.FindAndExtract(pawn,
                    t => DrugValidator(pawn, addiction, t), out string reason);
                if (debug) Log.Warning("[DS] ChemNeed: " + need.def.defName + " -> " + reason);
                if (taken == null) continue;

                __result = DrugAIUtility.IngestAndTakeToInventoryJob(taken, pawn, 1);
                return;
            }
        }

        /// <summary>
        /// 镜像原版 <c>JobGiver_SatisfyChemicalNeed.DrugValidator</c>（private static，抄不过来）。
        /// 唯一省略的是原版那段 <c>if (drug.Spawned) { ... }</c> —— 容器内容物永远未 Spawned，
        /// 原版对它本来也是整段跳过。
        /// </summary>
        private static bool DrugValidator(Pawn pawn, Hediff_Addiction addiction, Thing drug)
        {
            if (!drug.def.IsDrug) return false;

            CompDrug compDrug = drug.TryGetComp<CompDrug>();
            if (compDrug?.Props.chemical == null) return false;
            if (compDrug.Props.chemical.addictionHediff != addiction.def) return false;

            DrugPolicy drugPolicy = pawn.drugs?.CurrentPolicy;
            if (drugPolicy != null
                && !drugPolicy[drug.def].allowedForAddiction
                && pawn.story != null
                && pawn.story.traits.DegreeOfTrait(TraitDefOf.DrugDesire) <= 0
                && (!pawn.InMentalState || !pawn.MentalStateDef.ignoreDrugPolicy))
                return false;

            return true;
        }
    }

    // ===================================================================
    // 路径 2：化学依赖（JobGiver_SatifyChemicalDependency，Biotech）
    // ===================================================================

    [HarmonyPatch(typeof(JobGiver_SatifyChemicalDependency), "TryGiveJob")]
    internal static class Patch_SatifyChemicalDependency
    {
        [HarmonyPostfix]
        internal static void Postfix(Pawn pawn, ref Job __result)
        {
            bool debug = DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog
                         && ConsumePatchUtil.ShouldLog("ChemDep");
            if (debug)
                Log.Warning("[DS] ChemDep: entered, vanilla __result=" + (__result != null ? "non-null" : "null"));

            if (__result != null)
            {
                if (!ConsumePatchUtil.IsUnexecutableContainerTarget(__result)) return;
                if (debug) Log.Warning("[DS] ChemDep: 原版作业目标在容器里，接管。");
                __result = null;
            }

            // 镜像原版的门：非 Biotech 直接不做
            if (!ModsConfig.BiotechActive) { if (debug) Log.Warning("[DS] ChemDep: Biotech 未启用"); return; }

            List<Hediff> hediffs = pawn?.health?.hediffSet?.hediffs;
            if (hediffs == null) { if (debug) Log.Warning("[DS] ChemDep: hediffs=null"); return; }

            var deps = new List<Hediff_ChemicalDependency>();
            for (int i = 0; i < hediffs.Count; i++)
            {
                if (hediffs[i] is Hediff_ChemicalDependency d && d.ShouldSatify) deps.Add(d);
            }
            if (deps.Count == 0) { if (debug) Log.Warning("[DS] ChemDep: 没有需要满足的化学依赖"); return; }
            // 原版按 Severity 降序（SortBy(x => 0f - x.Severity)）
            deps.Sort((a, b) => b.Severity.CompareTo(a.Severity));

            for (int i = 0; i < deps.Count; i++)
            {
                Hediff_ChemicalDependency dep = deps[i];
                Thing taken = ContainerDrugFinder.FindAndExtract(pawn,
                    t => DrugValidator(pawn, dep, t), out string reason);
                if (debug) Log.Warning("[DS] ChemDep: " + dep.def.defName + " -> " + reason);
                if (taken == null) continue;

                __result = DrugAIUtility.IngestAndTakeToInventoryJob(taken, pawn, 1);
                return;
            }
        }

        /// <summary>
        /// 镜像原版 <c>JobGiver_SatifyChemicalDependency.DrugValidator</c>（private static）。
        ///
        /// ⚠️ 最后那道精神状态的括号是**原版原文照抄**（<c>(!InMentalState || MentalStateDef.ignoreDrugPolicy)</c>），
        /// 与 <c>JobGiver_SatisfyChemicalNeed</c> 里那一份（<c>(!InMentalState || !ignoreDrugPolicy)</c>）
        /// 看起来是相反的 —— 原版自己就不一致。这里**不"修正"它**：目的是让容器内容物与原版
        /// 地面物品受到完全相同的对待，而不是顺手改原版行为。
        /// </summary>
        private static bool DrugValidator(Pawn pawn, Hediff_ChemicalDependency dependency, Thing drug)
        {
            if (!drug.def.IsDrug) return false;

            CompDrug compDrug = drug.TryGetComp<CompDrug>();
            if (compDrug == null || compDrug.Props.chemical == null) return false;
            if (compDrug.Props.chemical != dependency.chemical) return false;

            if (pawn.drugs != null
                && !pawn.drugs.CurrentPolicy[drug.def].allowedForAddiction
                && (!pawn.InMentalState || pawn.MentalStateDef.ignoreDrugPolicy))
                return false;

            return true;
        }
    }

    // ===================================================================
    // 路径 3：娱乐性用药（JoyGiver_TakeDrug）
    // ===================================================================

    /// <summary>
    /// ⚠️ <b>挂点与任务书不同（任务书写的是 <c>JoyGiver_TakeDrug.TryGiveJob</c>，那个方法不存在）</b>：
    /// <c>JoyGiver_TakeDrug</c> 只重写了 <c>BestIngestItem</c>，<c>TryGiveJob</c> 继承自
    /// <c>JoyGiver_Ingest</c>。所以正确的挂点是 <c>BestIngestItem</c>（protected，可 patch）。
    ///
    /// <para>为什么现有 <c>Patch_JoyIngest</c> 盖不住它：那条 patch 遍历 <c>JoyGiverDef.AllDefs</c>
    /// 并要求 <c>def.thingDefs != null</c>，而 <c>JoyGiver_TakeDrug</c> 的候选来自**药物政策**而非
    /// <c>thingDefs</c> ⇒ 被 <c>continue</c> 跳过。</para>
    /// </summary>
    [HarmonyPatch(typeof(JoyGiver_TakeDrug), "BestIngestItem")]
    internal static class Patch_JoyTakeDrug
    {
        private static readonly List<ThingDef> takeableDrugs = new List<ThingDef>();

        [HarmonyPostfix]
        internal static void Postfix(Pawn pawn, Predicate<Thing> extraValidator, ref Thing __result)
        {
            bool debug = DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog
                         && ConsumePatchUtil.ShouldLog("JoyDrug");
            if (debug)
                Log.Warning("[DS] JoyDrug: entered, vanilla __result=" + (__result != null ? "non-null" : "null"));

            if (__result != null) return; // 原版在背包/地图上找到了
            if (pawn?.drugs == null || pawn.Map == null) return;

            // 镜像原版：DrugDesire>0 或精神状态下无视政策；否则只取 allowedForJoy 的药。
            // （原版还会 Shuffle 候选；这里保持确定顺序，行为等价。）
            bool ignorePolicy = pawn.story != null
                && (pawn.story.traits.DegreeOfTrait(TraitDefOf.DrugDesire) > 0 || pawn.InMentalState);

            takeableDrugs.Clear();
            DrugPolicy policy = pawn.drugs.CurrentPolicy;
            for (int i = 0; i < policy.Count; i++)
            {
                if (ignorePolicy || policy[i].allowedForJoy) takeableDrugs.Add(policy[i].drug);
            }

            for (int i = 0; i < takeableDrugs.Count; i++)
            {
                ThingDef def = takeableDrugs[i];
                Thing taken = ContainerDrugFinder.FindAndExtract(pawn,
                    t => t.def == def
                         && CanIngestForJoyMirror(pawn, t)
                         && (extraValidator == null || extraValidator(t))
                         && t.def.ingestible != null
                         && t.def.ingestible.drugCategory != DrugCategory.None,
                    out string reason);
                if (debug) Log.Warning("[DS] JoyDrug: " + def.defName + " -> " + reason);
                if (taken == null) continue;

                __result = taken;
                return;
            }
        }

        /// <summary>
        /// 镜像 <c>JoyGiver_Ingest.CanIngestForJoy</c>（<c>protected virtual</c>，外部调不到）。
        /// 同样省掉原版 <c>if (t.Spawned) { ... }</c> 整段 —— 容器内容物永远未 Spawned。
        /// </summary>
        private static bool CanIngestForJoyMirror(Pawn pawn, Thing t)
        {
            if (!t.def.IsIngestible || t.def.ingestible.joyKind == null
                || t.def.ingestible.joy <= 0f || !pawn.WillEat(t))
                return false;

            if (t.def.IsDrug && pawn.drugs != null
                && !pawn.drugs.CurrentPolicy[t.def].allowedForJoy
                && pawn.story != null
                && pawn.story.traits.DegreeOfTrait(TraitDefOf.DrugDesire) <= 0
                && !pawn.InMentalState)
                return false;

            return true;
        }
    }
}
