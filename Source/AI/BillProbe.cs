using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// <b>取活</b>：问原版"这张工作台下一个该做哪条 bill、要哪些料"，然后把答案装进槽位。
    ///
    /// <para><b>核心：<c>WorkGiver_DoBill.JobOnThing(pawn, bench)</c> 是公开入口</b>
    /// （<c>RimWorld\WorkGiver_DoBill.cs:139</c>），它返回的 job 里已经装好了原版亲手挑的原料
    /// （<c>TryStartNewDoBillJob:329-352</c>：<c>targetQueueB</c> = 真实 Thing、<c>countQueue</c> = 各取多少、
    /// <c>bill</c> = 那一条）。**job 本身从不上岗** —— 我们只要里面的数据。</para>
    ///
    /// <para>为什么不自己按 <c>recipe.ingredients</c> 挑：那要复刻原版的营养换算
    /// （<c>IngredientValueGetter.ValuePerUnitOf</c>）、混料规则（<c>allowMixingIngredients</c> ⇒
    /// <c>TryFindBestBillIngredientsInSet_NoMix/_AllowMix</c>）、stuff 取整… 本 mod 已经立过规矩
    /// ——见 <c>Patch_WorkGiver_DoBill_RemoteIngredients</c>："本 mod 不发明任何挑选规则，
    /// 也就不会和原版漂移"。</para>
    ///
    /// <para><b>成本</b>：原版候选来源第一处就是本图 haul source（我们的核心，
    /// <c>WorkGiver_DoBill.cs:481-496</c>），且**在区域遍历之前**（<c>:525-528</c>）；
    /// 核心里有料 ⇒ 只扫一遍容器内容物就返回，不做寻路。核心里没料时才会做全图区域遍历，
    /// 那种情况我们用自己的退避（<see cref="CraftBackoffTicks"/>）压住。</para>
    /// </summary>
    internal static class BillProbe
    {
        /// <summary>取活失败（没活/没料/条件不满足）后的重试间隔。</summary>
        private const int RetryTicks = 30;

        /// <summary>
        /// "核心里凑不齐、但地面上有"的退避（2 秒）。
        ///
        /// <para>⚠️ 刻意**不写** <c>bill.nextTickToSearchForIngredients</c>：那是原版给**真人**用的
        /// 负缓存，我们占了它会让真人也不能用地面上的料做这条 bill。</para>
        /// </summary>
        private const int CraftBackoffTicks = 120;

        private static List<WorkGiverDef> doBillDefs;

        /// <summary>所有 <c>giverClass</c> 派生自 <see cref="WorkGiver_DoBill"/> 的 WorkGiverDef（按 defName 稳定排序）。</summary>
        private static List<WorkGiverDef> DoBillDefs()
        {
            if (doBillDefs != null) return doBillDefs;
            List<WorkGiverDef> list = new List<WorkGiverDef>();
            List<WorkGiverDef> all = DefDatabase<WorkGiverDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                WorkGiverDef d = all[i];
                if (d == null || d.giverClass == null) continue;
                if (!typeof(WorkGiver_DoBill).IsAssignableFrom(d.giverClass)) continue;
                list.Add(d);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.defName, b.defName));
            doBillDefs = list;
            return doBillDefs;
        }

        /// <summary>
        /// 给槽位取一件活。返回 true 表示槽位已装好（<c>Bill/Ingredients/WorkLeft</c> 就绪）。
        /// </summary>
        public static bool TryAcquire(CompBillAutomation comp, BillSlot slot, Map map, Pawn w, int now)
        {
            slot.ClearWork();
            slot.BlockKey = "DS_BA_NoBill";

            Thing bench = slot.Bench;
            if (bench == null || bench.Destroyed || !bench.Spawned) return false;

            IBillGiver giver = bench as IBillGiver;
            if (giver == null) return false;
            if (!giver.BillStack.AnyShouldDoNow) return false;

            // 断电 / 缺燃料 / 故障 ⇒ 停（用户拍板：代理不代劳加燃料）。
            // 用 CurrentlyUsableForBills 而不是 UsableForBillsAfterFueling：后者不管燃料。
            if (!giver.CurrentlyUsableForBills()) { slot.BlockKey = "DS_BA_BenchOff"; return false; }
            if (bench.IsBurning()) { slot.BlockKey = "DS_BA_BenchOff"; return false; }

            // 真人正在用这台 ⇒ 让。等价于原版 JobOnThing 里那句 pawn.CanReserve(台子)，
            // 但不需要借地图（我们只读，不占预订）。
            if (map.reservationManager != null
                && map.reservationManager.IsReservedByAnyoneOf(bench, Faction.OfPlayer))
            {
                slot.BlockKey = "DS_BA_BenchBusy";
                slot.NextAcquireTick = now + RetryTicks;
                return false;
            }

            List<WorkGiverDef> defs = DoBillDefs();
            if (defs.Count == 0) return false;

            bool noCoreMaterial = false;
            bool obstructed = false;
            DigitalWorkerScope.Enter(w, map, bench.PositionHeld);
            try
            {
                for (int i = 0; i < defs.Count; i++)
                {
                    WorkGiverDef d = defs[i];
                    if (d.fixedBillGiverDefs != null && !d.fixedBillGiverDefs.Contains(bench.def)) continue;

                    WorkGiver_DoBill doBill;
                    try { doBill = d.Worker as WorkGiver_DoBill; }
                    catch (Exception) { continue; }
                    if (doBill == null) continue;

                    Job got;
                    try { got = doBill.JobOnThing(w, bench, false); }
                    catch (Exception e)
                    {
                        Log.ErrorOnce("[DigitalStorage] 制作代理取活失败（跳过这个 WorkGiver）："
                            + d.defName + " :: " + e, d.shortHash * 31 + 991);
                        continue;
                    }
                    if (got == null) continue;
                    if (got.bill == null)
                    {
                        // 原版给的是"加燃料 job"或"先把台上杂物搬走 job" —— 两种都意味着**这台子现在开不了工**。
                        obstructed = true;
                        continue;
                    }

                    // 只认普通生产 bill。医疗/机械/自动（基因舱等）各自有状态机，不在这条链上。
                    Bill_Production bp = got.bill as Bill_Production;
                    if (bp == null) continue;
                    if (got.bill is Bill_Medical || got.bill is Bill_Mech || got.bill is Bill_Autonomous) continue;
                    if (bp.recipe == null) continue;
                    // v1 不做未完成品类（艺术/雕塑/生物塑型）：它们的原料要先变成 UnfinishedThing，是另一条链。
                    if (bp.recipe.UsesUnfinishedThing) continue;
                    if (bp.suspended || bp.DeletedOrDereferenced) continue;

                    Thing[] things;
                    int[] counts;
                    if (!ExtractIngredients(got, map, out things, out counts))
                    {
                        // 原版选了地面上的料（核心里凑不齐）⇒ 用户拍板"物品在核心里才可以被自动化使用"
                        noCoreMaterial = true;
                        continue;
                    }

                    slot.Probe = got;
                    slot.Bill = bp;
                    slot.Ingredients = things;
                    slot.Counts = counts;
                    slot.BaseRate = ComputeBaseRate(bp, bench, w);
                    slot.WorkAmount = bp.GetWorkAmount(LastIngredient(things, got));
                    slot.WorkLeft = slot.WorkAmount;
                    slot.NextAcquireTick = 0;
                    slot.BlockKey = null;

                    // 与 JobDriver_DoBill 的两个 toil 对齐（:100 Notify_DoBillStarted / DoRecipeWork init:76）。
                    // Bill_Production 自己没重写，但子类/mod 扩展可能在用 ⇒ 该调的还是要调。
                    try
                    {
                        bp.Notify_DoBillStarted(w);
                        bp.Notify_BillWorkStarted(w);
                    }
                    catch (Exception e)
                    {
                        Log.ErrorOnce("[DigitalStorage] bill.Notify_* 抛异常（已忽略）：" + e, 771133);
                    }
                    return true;
                }
            }
            finally
            {
                DigitalWorkerScope.Exit(w);
            }

            if (noCoreMaterial)
            {
                slot.BlockKey = "DS_BA_NoCoreMaterial";
                slot.NextAcquireTick = now + CraftBackoffTicks;
            }
            else if (obstructed)
            {
                slot.BlockKey = "DS_BA_Block_Obstructed";
                slot.NextAcquireTick = now + RetryTicks;
            }
            else
            {
                slot.BlockKey = Diagnose(giver, w);
                slot.NextAcquireTick = now + RetryTicks;
            }
            return false;
        }

        /// <summary>
        /// 取活全部失败时，用**便宜的只读判据**给面板一个具体原因。
        ///
        /// <para>为什么值得写：面板只显示"没有可做的 bill"等于没说 —— 到底是缺料、资质不够、
        /// bill 被绑给某个小人，还是未完成品类被跳过？本 mod 在"诊断只打计数"上吃过亏
        /// （见 obsidian 里那条教训：探针必须能自证）。</para>
        ///
        /// <para>⚠️ <c>Bill.PawnAllowedToStartAnew</c> 会写全局静态 <c>JobFailReason</c>（右键菜单用它显示
        /// "技能不符"之类），诊断完必须 <c>Clear()</c>，否则残留在玩家的右键菜单里。</para>
        /// </summary>
        private static string Diagnose(IBillGiver giver, Pawn w)
        {
            BillStack stack = giver.BillStack;
            if (stack == null || stack.Count == 0) return "DS_BA_NoBill";

            bool anyDoable = false;      // 有 bill 想做
            bool materialOnly = false;   // 有 bill 通过了所有便宜判据 ⇒ 失败原因只能是"缺料"
            bool skillBad = false;
            bool restricted = false;
            bool uft = false;

            for (int i = 0; i < stack.Count; i++)
            {
                Bill b = stack[i];
                if (b == null || b.DeletedOrDereferenced || b.suspended) continue;
                if (!b.ShouldDoNow()) continue;
                anyDoable = true;

                bool ok = b.PawnAllowedToStartAnew(w);
                JobFailReason.Clear();
                if (!ok) { restricted = true; continue; }

                if (b.recipe == null) continue;
                if (b.recipe.FirstSkillRequirementPawnDoesntSatisfy(w) != null) { skillBad = true; continue; }
                if (b.recipe.UsesUnfinishedThing) { uft = true; continue; }
                materialOnly = true;
            }

            if (!anyDoable) return "DS_BA_NoBill";
            if (materialOnly) return "DS_BA_Block_Material";
            if (skillBad) return "DS_BA_Block_Skill";
            if (restricted) return "DS_BA_Block_Restricted";
            if (uft) return "DS_BA_Block_Uft";
            return "DS_BA_NoBill";
        }

        /// <summary>
        /// 原版速度公式（照抄 <c>Toils_Recipe.DoRecipeWork:109-113</c>）：
        /// <c>rate = workSpeedStat==null ? 1 : pawn.GetStatValue(workSpeedStat)</c>，
        /// 若配方有 <c>workTableSpeedStat</c> 且台子是 <c>Building_WorkTable</c> 再乘台子的该 stat。
        ///
        /// <para>**刻意不额外乘技能**：<c>workSpeedStat</c> 自己就吃技能（做饭类是 <c>CookSpeed</c>），
        /// 再乘一次就是双算。</para>
        /// </summary>
        private static float ComputeBaseRate(Bill_Production bill, Thing bench, Pawn w)
        {
            RecipeDef r = bill.recipe;
            float rate = (r.workSpeedStat == null) ? 1f : w.GetStatValue(r.workSpeedStat);
            Building_WorkTable table = bench as Building_WorkTable;
            if (r.workTableSpeedStat != null && table != null)
            {
                rate *= table.GetStatValue(r.workTableSpeedStat);
            }
            return rate;
        }

        /// <summary>
        /// 原版 <c>Toils_Recipe.DoRecipeWork</c> 的 <c>workLeft = bill.GetWorkAmount(thing)</c> 里那个
        /// <c>thing</c>，取的是 <c>TargetIndex.B.Thing</c> —— 对非未完成品配方就是**最后一件被取走的原料**
        /// （<c>JobDriver_DoBill.CollectIngredientsToils</c> 逐件设 targetB）。
        /// </summary>
        private static Thing LastIngredient(Thing[] things, Job job)
        {
            if (things != null && things.Length > 0) return things[things.Length - 1];
            return job.GetTarget(TargetIndex.B).Thing;
        }

        /// <summary>
        /// 把 <c>job.targetQueueB / countQueue</c> 抄成平行数组，并施加**反向守卫**：
        /// 每一件原料都必须住在**本图某个存储核心的容器**里（用户规则：材料只能在核心里）。
        ///
        /// <para>守卫是必要的：原版候选来源的第二处是"区域遍历地图上的散落物"
        /// （<c>WorkGiver_DoBill.cs:529-551</c>），核心里凑不齐时它会把**地上的东西**选进来。
        /// 不挡的话，制作代理就变成"第二套自动收纳"，直接从地上吃料。</para>
        /// </summary>
        private static bool ExtractIngredients(Job job, Map map, out Thing[] things, out int[] counts)
        {
            List<LocalTargetInfo> q = job.targetQueueB;
            int n = (q == null) ? 0 : q.Count;
            things = new Thing[n];
            counts = new int[n];

            for (int i = 0; i < n; i++)
            {
                Thing t = q[i].Thing;
                things[i] = t;
                counts[i] = (job.countQueue != null && i < job.countQueue.Count)
                    ? job.countQueue[i]
                    : (t != null ? t.stackCount : 0);

                if (t == null || t.Destroyed) return false;

                Building_StorageCore core = t.ParentHolder as Building_StorageCore;
                if (core == null || core.Map != map) return false;   // 地上的 / 别的图的 / 书架衣架里的
                if (counts[i] > t.stackCount) return false;          // 被人顺走了几件，数量对不上
            }
            return true;
        }
    }
}
