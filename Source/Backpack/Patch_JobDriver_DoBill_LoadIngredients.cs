using System;
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
                LoadIngredients(toil);
            };
            return toil;
        }

        /// <summary>
        /// 取料本体。**整段 try/catch 是硬要求**：这个 toil 被前置进**每一个** DoBill 作业
        /// （<c>JobDriver_DoBill.MakeNewToils</c>），异常一旦抛出去，这个 pawn 的每一条 bill 作业
        /// 都会在第一步就失败 —— 玩家看到的就是"这个小人干不了活"。
        /// 失败时什么都不做：原版后面的 toil 照旧走"走到核心去拿"的老路，最坏只是慢。
        /// </summary>
        private static void LoadIngredients(Toil toil)
        {
            try
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
                    if (absorbed == null)
                    {
                        // ★ 跨图源吸不进背包时**必须收工**（2026-10-02 跨图阶段新增）。
                        //
                        // 本图源失败可以先放着 —— 原版会走到核心门口自己拿，是既有的退化路径。
                        // 但跨图源失败就完全不同了：队列里这一项指向的是**另一张图**上的容器，
                        // GotoThing(canGotoSpawnedParent: true) 会把它的 SpawnedParentOrMe 解析成
                        // a 图上的核心建筑，然后**照 a 图的坐标在 b 图上寻路**（不报错，就是走错地方）。
                        // 宁可直接判定作业不可能完成 —— 原版的 nextTickToSearchForIngredients
                        // 会让它在 500~600 tick 后重新找料，那时多半就拿得到别的了。
                        Building_StorageCore remote = t.ParentHolder as Building_StorageCore;
                        if (remote != null && remote.Spawned && remote.Map != actor.Map)
                        {
                            if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                                Log.Warning("[DS] 跨图取料：源已被截走（" + t.def.defName
                                    + " @ " + remote.Map + "），本作业放弃，等待下轮重找");
                            actor.jobs.curDriver.EndJobWith(JobCondition.Incompletable);
                            return;
                        }
                        continue;
                    }

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

                        // ★ 只有**真的订过**才 Release（2026-10-02 实测补上的守卫）。
                        //
                        // 无条件 Release 会在跨图取料时刷红字（用户实测：一次做饭 + 一次手术共 5 条）：
                        //   "Tried to release Thing_Meat_Dromedary92683 that wasn't reserved by Blizzard."
                        //
                        // 原因：ReservationManager 是**每图一份**，而它的 CanReserve 有一条地图门
                        //   target.Thing.SpawnedOrAnyParentSpawned && target.Thing.MapHeld != map ⇒ false
                        // JobDriver_DoBill.TryMakePreToilReservations 调的 ReserveAsManyAsPossible 是
                        // errorOnFailed:false ⇒ 跨图的队列项**静默地没订上**，
                        // 于是我们这句 Release 变成"释放一个从没订过的目标"，
                        // Release 内部找不到就 Log.Error（ReservationManager.cs:392-401）。
                        //
                        // 顺带把另一个更老的隐患一起盖住：本图原料在"搜索 → 开作业"之间被别的 pawn
                        // 抢先订走时，同样会出现"没订上却 Release"。ReservedBy 正是 Release 自己的判据。
                        Verse.Map actorMap = actor.Map;
                        if (actorMap != null && actorMap.reservationManager.ReservedBy(t, actor, job))
                            actorMap.reservationManager.Release(t, actor, job);
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] bill 取料（背包）失败，本作业退回原版路径：" + e, 0x44534252);
            }
        }
    }
}
