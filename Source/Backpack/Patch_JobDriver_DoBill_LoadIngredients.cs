using System.Collections.Generic;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Backpack
{
    /// <summary>
    /// 把原版 bill 的「取料」从<b>走到核心门口</b>改成<b>0 距离</b>。
    ///
    /// <para><b>现状为什么不满足</b>：原版<b>选料</b>其实已经找得到核心里的原料
    /// （<c>WorkGiver_DoBill.cs:487</c> 对 haul source 调
    /// <c>ThingOwnerUtility.GetAllThingsRecursively</c>，我们核心是 Thing 所以在表里）
    /// —— 这正是"bill 能开工、但小人先走到核心"的现状。真正要走路的是
    /// <c>JobDriver_DoBill.cs:121</c> 的 <c>GotoThing(..., canGotoSpawnedParent: true)</c>，
    /// 目标由 <c>SpawnedParentOrMe</c> 决定，而它是**运行期解析**的
    /// （<c>Toils_Goto.cs:20</c>，在 toil 的 initAction 里）。</para>
    ///
    /// <para>⇒ 只要在这一跳<b>之前</b>把原料挪进背包，寻路目标就变成小人自己。
    /// 后面的 <c>StartCarryThing</c>（<c>canTakeFromInventory: true</c>）从
    /// <c>thing.holdingOwner</c> 取物（<c>Pawn_CarryTracker.TryStartCarry:94</c> 的 <c>SplitOff</c>），
    /// 不关心容器是谁 —— 所以背包这种"非原版背包"的容器也照样成立。</para>
    ///
    /// <para><b>注入点为什么是 <c>MakeNewToils</c></b>：<c>CollectIngredientsToils</c>
    /// 被 <c>JobDriver_RecolorApparel</c> 复用（target index 含义不同，patch 它会误伤），
    /// 只有 <c>MakeNewToils</c> 是 DoBill 专属。前置一个 Instant toil 不破坏原版那些
    /// <c>Toils_Jump</c> —— 它们持有 toil 对象引用，与顺序无关。
    /// <c>SetupToils</c> 会一次性枚举完（含原版前几行的 <c>AddEndCondition</c> / <c>FailOn</c>），
    /// 所以把我们的 toil 排在最前也没有初始化顺序问题。</para>
    ///
    /// <para><b>按需取用</b>：搬多少完全由 <c>job.countQueue</c> 决定 —— 它由
    /// <c>WorkGiver_DoBill.TryStartNewDoBillJob:338</c> 按配方算好（<c>chosenIngThings[i].Count</c>），
    /// 不是整堆。作业做完这些料会被原版放到工作台上，背包随之空掉。</para>
    ///
    /// <para><b>只搬"住在数字存储核心里"的</b>（<c>t.ParentHolder is Building_StorageCore</c>）：
    /// 地上的原料原版自己走两步就到，不动它；别人家容器（书架 / 自动工作台内胆）也不动 ——
    /// 本期范围就是核心。要放开只需改这一处判定。</para>
    /// </summary>
    [HarmonyPatch(typeof(JobDriver_DoBill), "MakeNewToils")]
    internal static class Patch_JobDriver_DoBill_LoadIngredients
    {
        [HarmonyPostfix]
        private static void Postfix(ref IEnumerable<Toil> __result)
        {
            if (__result == null) return;
            __result = Prepend(__result);
        }

        private static IEnumerable<Toil> Prepend(IEnumerable<Toil> original)
        {
            yield return MakeLoadToil();
            foreach (Toil toil in original)
            {
                yield return toil;
            }
        }

        private static Toil MakeLoadToil()
        {
            Toil toil = ToilMaker.MakeToil("DS_LoadBillIngredients");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = delegate
            {
                Pawn actor = toil.actor;
                if (actor == null) return;

                // 没背包（非玩家阵营 / 未植入 hediff）⇒ 原版流程一字不改
                HediffComp_Backpack bag = HediffComp_Backpack.For(actor);
                if (bag == null) return;

                Job job = (actor.jobs == null) ? null : actor.jobs.curJob;
                if (job == null) return;

                List<LocalTargetInfo> queue = job.GetTargetQueue(TargetIndex.B);
                if (queue.NullOrEmpty()) return;
                List<int> counts = job.countQueue;

                // 作业开始时先清掉上一次的残留（「默认清空」+ 保证有空间）。
                // **必须有这一步**：背包满了 CanFit 会让每一件料都被静默拒掉（实测症状：
                // 同一作业里两个不同 def 都"取到 0"，小人照样走到核心）。而背包内容物
                // 对原版搜索是不可见的（不是 Thing），所以满背包里的东西不会自己回核心。
                if (bag.Count > 0) bag.ReturnContentsToCore();

                // 已经在工作台内胆里的料，原版有 JumpIfTargetInsideBillGiver 直接跳过取料
                // （JobDriver_DoBill.cs:145）—— 别把它拽出来，反而打乱原版路径。
                Thing billGiver = job.GetTarget(TargetIndex.A).Thing;
                ThingOwner giverInner = (billGiver == null) ? null : billGiver.TryGetInnerInteractableThingOwner();

                for (int i = 0; i < queue.Count; i++)
                {
                    Thing t = queue[i].Thing;
                    if (t == null || t.Destroyed) continue;
                    if (t.Spawned) continue;                              // 地上的：原版自己会走过去
                    if (!(t.ParentHolder is Building_StorageCore)) continue; // 只管核心内容物
                    if (giverInner != null && giverInner.Contains(t)) continue;

                    // 数量未知就**不动**（宁可不取，也不能超量）：countQueue 由原版
                    // TryStartNewDoBillJob:338 与 targetQueueB 平行填充，正常永远对得上。
                    // 若某个第三方建的 DoBill 作业缺 countQueue，回退成"整堆"会搬出比
                    // curJob.count 更多的东西，之后 StartCarryThing 的
                    // failIfStackCountLessThanJobCount 判定就失去意义。
                    if (counts == null || i >= counts.Count) continue;

                    Thing absorbed = bag.TryAbsorb(t, counts[i]);
                    if (absorbed == null) continue;

                    // ★★ 决定性的一步：把作业队列那一项**改指到背包里那件**。
                    //
                    // 拆堆时（要 15、原摞 75）进背包的是 SplitOff **新建的 Thing**，
                    // 而 targetQueueB 还指着核心里原来那一摞 ⇒ 不改指的话，
                    // 原版 GotoThing(..., canGotoSpawnedParent: true) 解析出来**还是核心**
                    // ⇒ 小人照样走向核心。2026-10-02 实测就是这么走的：
                    //   "#0 钢铁 需要 15 → 实际取到 15；SpawnedParentOrMe=数字存储核心"
                    // （早期实验版没测出这个，是因为它检查的是**背包里那件**，
                    //   而不是**作业队列指着的那件** —— 测错了对象。）
                    if (!ReferenceEquals(absorbed, t))
                    {
                        queue[i] = new LocalTargetInfo(absorbed); // GetTargetQueue 返回的就是 job 的列表本体
                        actor.Reserve(new LocalTargetInfo(absorbed), job, 1, -1, null, false);
                        if (actor.Map != null) actor.Map.reservationManager.Release(t, actor, job);
                    }
                }
            };
            return toil;
        }
    }
}
