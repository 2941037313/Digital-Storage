using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 代理建筑干的"一件活"。
    ///
    /// <para>做法：**逐类照抄原版 <c>JobDriver</c> 的 <c>tickAction</c> / <c>tickIntervalAction</c>**，
    /// 把"走到目标"那一半扔掉。为什么不能直接跑原版 driver：
    /// <c>JobDriver.CurToil</c> / <c>HaveCurToil</c> 硬校验 <c>pawn.CurJob == job</c>
    /// （<c>Verse\JobDriver.cs</c>），脱离 job 的 driver 第一步就返回 null。</para>
    ///
    /// <para>速度**只认 <c>speedMult</c>**（= 建筑的等级倍率 0.8/1.2/2.0），刻意不乘
    /// <c>pawn.GetStatValue(...)</c> —— 理由见 <see cref="CompProperties_DigitalWorker.workSpeedMult"/>。
    /// 技能资质只用于品质与产量。</para>
    /// </summary>
    public abstract class DigitalTask
    {
        public Thing target;
        public CompDigitalWorker comp;

        /// <summary>这件活是哪一类工作找来的（并行配额按它计数）。由 comp 在认领时填。</summary>
        public WorkTypeDef workType;

        /// <summary>
        /// 任务自己要求"放弃这一件"（例如建造发现核心凑不齐料，要让路给原版搬运工）。
        /// ⚠️ 不要用 <c>comp.Release()</c> —— 那是"全放手"，并行时会连带杀掉别的活。
        /// </summary>
        public bool Abort;

        /// <summary>
        /// "干完一步"的计数（挖一镐 / 砍一刀 / 建一点）。comp 靠它决定要不要让手上那只手挥一下。
        /// 没有离散步骤的工作（例如将来的连续推进类）让它保持 0 即可，手就只待机呼吸。
        /// </summary>
        public int StrikeCount;

        /// <summary>
        /// 这类活趴在被加工目标上的是哪只手（Mote defName）。
        /// 拓展到种植/建造/清洁时：加一个换贴图的 Mote def，在对应适配器里重写本属性指过去即可。
        /// </summary>
        public virtual string HandMoteDefName
        {
            get { return "DS_WorkHand"; }
        }

        /// <summary>面板/调试用的活名（挖掘 / 建造 / …）。</summary>
        public abstract string Label { get; }

        /// <summary>还能不能继续干（每 tick 问一次）。</summary>
        public abstract bool StillValid(Pawn pawn, Map map);

        /// <summary>干 1 tick 的活。<paramref name="speedMult"/> 已经乘进各类的算式里。</summary>
        public abstract void Work(Pawn pawn, Map map, float speedMult);

        public virtual bool Finished
        {
            get { return Abort || target == null || target.Destroyed || !target.Spawned; }
        }

        /// <summary>0~1 的进度（给建筑底下那根黄色读条用）；&lt;0 表示这类活没有进度概念。</summary>
        public virtual float Progress01
        {
            get { return -1f; }
        }

        /// <summary>放手时收掉自己挂的 effecter 等资源（断电/拆除/目标失效/干完都会调）。</summary>
        public virtual void Cleanup()
        {
        }

        public string TargetLabel
        {
            get { return (target == null) ? "?" : target.LabelShort; }
        }
    }

    /// <summary>一种工作类型的适配器：绑定"哪个原版 <see cref="WorkGiver"/> 负责找这种活"。</summary>
    public abstract class DigitalTaskAdapter
    {
        /// <summary>对应的原版 WorkGiver 类（用 <c>IsAssignableFrom</c> ⇒ 子类/mod 扩展也算）。</summary>
        public abstract Type WorkGiverClass { get; }

        public abstract DigitalTask MakeTask(Thing t, CompDigitalWorker comp);

        /// <summary>找活前的廉价预筛（可选）。</summary>
        public virtual bool CanTarget(Pawn pawn, Thing t)
        {
            return true;
        }

        /// <summary>
        /// 是否信任原版 <c>WorkGiver.HasJobOnThing</c> 作为闸门（默认信任）。
        ///
        /// <para>不信任的场合只有一种：<b>原版闸门里含"可达性"</b>，而代理建筑是**隔空**干活的。
        /// 目前只有清洁需要自备候选集（原版 <c>WorkGiver_CleanFilth</c> 的候选集来自
        /// <c>listerFilthInHomeArea</c>，而我们拍板"放宽 Home 区"）⇒ 它同时不信任候选集与闸门，
        /// 改为在 <c>CanTarget</c>/<c>StillValid</c> 里自己做同样的检查。</para>
        /// </summary>
        public virtual bool TrustWorkGiver
        {
            get { return true; }
        }

        /// <summary>
        /// 候选集。默认走原版（<c>scanner.PotentialWorkThingsGlobal</c>，为空则退到
        /// <c>listerThings.ThingsMatching(PotentialWorkThingRequest)</c>）；需要放宽原版范围时重写。
        /// </summary>
        public virtual IEnumerable<Thing> CandidateSet(Map map, Pawn pawn, WorkGiver_Scanner scanner)
        {
            IEnumerable<Thing> set = null;
            try
            {
                set = scanner.PotentialWorkThingsGlobal(pawn);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 取代理候选集失败：" + scanner.def.defName + " → " + e,
                    scanner.def.shortHash * 31 + 7717);
            }
            if (set == null)
            {
                set = map.listerThings.ThingsMatching(scanner.PotentialWorkThingRequest);
            }
            return set;
        }
    }

    /// <summary>
    /// 适配器注册表。**白名单**：没登记的工作类型，代理建筑直接视而不见（也省掉它的候选扫描）。
    /// 第一批 = 挖掘（本波）；建造/清洁/种植随后按同一模板插入。
    /// </summary>
    public static class DigitalTaskRegistry
    {
        private static readonly List<DigitalTaskAdapter> All = new List<DigitalTaskAdapter>
        {
            new DigitalTaskAdapter_Mine(),
            new DigitalTaskAdapter_Construct(),
            new DigitalTaskAdapter_Clean(),
            new DigitalTaskAdapter_PlantCut(),
        };

        private static readonly Dictionary<WorkTypeDef, WorkGiver> giverCache = new Dictionary<WorkTypeDef, WorkGiver>();

        /// <summary>这个工作类型下、**有适配器**的那个 WorkGiver。没有则返回 null（那个建筑就不干活）。</summary>
        public static WorkGiver FindGiver(WorkTypeDef workType)
        {
            if (workType == null) return null;

            WorkGiver cached;
            if (giverCache.TryGetValue(workType, out cached)) return cached;

            WorkGiver found = null;
            List<WorkGiverDef> all = DefDatabase<WorkGiverDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                WorkGiverDef d = all[i];
                if (d == null || d.workType != workType) continue;
                WorkGiver w = d.Worker;
                if (w == null || AdapterFor(w) == null) continue;
                found = w;
                break;
            }
            giverCache[workType] = found;
            return found;
        }

        public static DigitalTaskAdapter AdapterFor(WorkGiver giver)
        {
            if (giver == null || giver.def == null || giver.def.giverClass == null) return null;
            for (int i = 0; i < All.Count; i++)
            {
                if (All[i].WorkGiverClass.IsAssignableFrom(giver.def.giverClass)) return All[i];
            }
            return null;
        }

        /// <summary>
        /// 原版 <c>JobGiver_Work.PawnCanUseWorkGiver</c> 的节选（它是 private，只能照抄）。
        ///
        /// <para><b>刻意跳过两道</b>：<c>WorkTypeIsDisabled</c> 与 <c>WorkTagIsDisabled</c>。
        /// 原因是我们的工人是**临时生成的 colonist**，会带随机背景与特质，
        /// 而 <c>GetDisabledWorkTypes</c> 会把背景的禁用工作类型算进去（<c>Pawn.cs:4251-4260</c>）——
        /// 机器不该因为抽到"不能做熟练劳动"就罢工。**代理建筑本身就是许可**。
        /// 其余三道照旧：<c>nonColonistsCanDo</c> / <c>ShouldSkip</c> / <c>MissingRequiredCapacity</c>。</para>
        /// </summary>
        public static bool PawnCanUse(WorkGiver giver, Pawn pawn)
        {
            if (giver == null || giver.def == null || pawn == null) return false;
            if (!giver.def.nonColonistsCanDo && !pawn.IsColonist
                && !(pawn.RaceProps.IsMechanoid && pawn.Faction == Faction.OfPlayer))
            {
                return false;
            }
            if (giver.ShouldSkip(pawn)) return false;
            if (giver.MissingRequiredCapacity(pawn) != null) return false;
            if (pawn.RaceProps.IsMechanoid && !giver.def.canBeDoneByMechs) return false;
            return true;
        }
    }

    // ==========================================================================================
    // 挖掘 —— 照抄 JobDriver_Mine.DoDamage / ResetTicksToPickHit
    // ==========================================================================================
    public class DigitalTaskAdapter_Mine : DigitalTaskAdapter
    {
        public override Type WorkGiverClass
        {
            get { return typeof(WorkGiver_Miner); }
        }

        public override DigitalTask MakeTask(Thing t, CompDigitalWorker comp)
        {
            return new DigitalTask_Mine { target = t, comp = comp };
        }
    }

    public class DigitalTask_Mine : DigitalTask
    {
        /// <summary>原版 <c>JobDriver_Mine.ResetTicksToPickHit</c>：<c>round(100 / MiningSpeed)</c>。
        /// 这里把 <c>MiningSpeed</c> 换成建筑的速度倍率（拍板：速度只认倍率）。</summary>
        private const int BaseTicksBetweenPickHits = 100;

        private const int BaseDamagePerPickHit_NaturalRock = 80;
        private const int BaseDamagePerPickHit_NotNaturalRock = 40;

        private float ticksToPickHit = -1f;

        /// <summary>镐击特效（原版 <c>JobDriver_Mine</c> 用的是 <c>EffecterDefOf.Mine</c>，每镐触发一次）。</summary>
        private Effecter effecter;

        public override string Label
        {
            get { return "挖掘"; }
        }

        /// <summary>与原版 <c>JobDriver_Mine</c> 的读条口径一致：<c>1 - HitPoints/MaxHitPoints</c>。</summary>
        public override float Progress01
        {
            get
            {
                if (target == null || target.MaxHitPoints <= 0) return -1f;
                return 1f - (float)target.HitPoints / (float)target.MaxHitPoints;
            }
        }

        public override void Cleanup()
        {
            if (effecter != null)
            {
                effecter.Cleanup();
                effecter = null;
            }
        }

        public override bool StillValid(Pawn pawn, Map map)
        {
            if (target == null || target.Destroyed || !target.Spawned || !target.def.mineable) return false;
            DesignationManager dm = map.designationManager;
            return dm.DesignationAt(target.Position, DesignationDefOf.Mine) != null
                || dm.DesignationAt(target.Position, DesignationDefOf.MineVein) != null;
        }

        public override void Work(Pawn pawn, Map map, float speedMult)
        {
            // 铁律：产出函数没有 Destroyed 守卫，重复调 = 重复掉产物
            if (target == null || target.Destroyed || !target.Spawned) return;

            if (ticksToPickHit < 0f)
            {
                ticksToPickHit = Mathf.Round(BaseTicksBetweenPickHits / speedMult);
            }

            ticksToPickHit -= 1f;
            if (ticksToPickHit > 0f) return;

            // 原版 JobDriver_Mine.cs:62-66：每镐先触发特效再结算伤害。
            // ⚠️ 但**不要照抄 `Trigger(actor, mineTarget)`** —— 那个 actor 是我们的假 pawn，
            // sprayer 会去读 `TargetInfo.CenterVector3` → `Pawn.DrawPos` → `PawnTweener`，
            // 而假 pawn 从未 spawn，这条链过去会 NRE（pather 为 null）。
            // 现在虽然补了 AddComponentsForSpawn 兜底，仍然用"目标打目标"更稳、也更符合语义：
            // 手是隔空干活的，碎石就该从矿上崩起来，没有"从人手上飞出"这一半。
            if (effecter == null)
            {
                effecter = EffecterDefOf.Mine.Spawn();
            }
            effecter.Trigger(target, target);

            StrikeCount++;   // 让"干活的那只手"挥一下

            DoDamage(pawn, map);

            if (target != null && !target.Destroyed)
            {
                ticksToPickHit = Mathf.Round(BaseTicksBetweenPickHits / speedMult);
            }
        }

        /// <summary>
        /// 原样搬 <c>JobDriver_Mine.DoDamage</c>：最后一镐走"手动清零 + <c>DestroyMined</c>"，
        /// 而不是 <c>TakeDamage</c> 打死它 —— 后者走 <c>Mineable.Destroy(KillFinalize)</c> 分支时
        /// pawn 为 null，产物会被自动 Forbid（原版 <c>JobDriver_Mine.cs:112-115</c> 同款处理）。
        /// </summary>
        private void DoDamage(Pawn pawn, Map map)
        {
            Thing t = target;
            int num = (t.def.building != null && t.def.building.isNaturalRock)
                ? BaseDamagePerPickHit_NaturalRock
                : BaseDamagePerPickHit_NotNaturalRock;

            Mineable mineable = t as Mineable;
            if (mineable == null || t.HitPoints > num)
            {
                t.TakeDamage(new DamageInfo(DamageDefOf.Mining, num, 0f, -1f, pawn));
                return;
            }

            bool isMineVein = map.designationManager.DesignationAt(mineable.Position, DesignationDefOf.MineVein) != null;
            IntVec3 pos = mineable.Position;

            mineable.Notify_TookMiningDamage(t.HitPoints, pawn);
            mineable.HitPoints = 0;
            mineable.DestroyMined(pawn);

            if (pawn.records != null)
            {
                pawn.records.Increment(RecordDefOf.CellsMined);
            }
            if (map.mineStrikeManager != null)
            {
                map.mineStrikeManager.CheckStruckOre(pos, t.def, pawn);
            }
            if (isMineVein)
            {
                IntVec3[] adjacent = GenAdj.AdjacentCells;
                for (int i = 0; i < adjacent.Length; i++)
                {
                    Designator_MineVein.FloodFillDesignations(pos + adjacent[i], map, t.def);
                }
            }
        }
    }

    // ==========================================================================================
    // 建造 —— 照抄 JobDriver_ConstructFinishFrame 的 tickAtomicAction + 数字存储独有的"补料"
    //
    // 原版耦合点：蓝图 → Frame 不是建造工干的，只能靠搬运链的收尾 toil
    // （JobDriver_HaulToContainer.cs:178 → Toils_Construct.MakeSolidThingFromBlueprintIfNecessary
    //  → Blueprint.TryReplaceWithSolidThing，public virtual）。
    // More Organs 当年只能做"材料已在框里"的 Frame（R4：CompleteConstruction 会无条件
    // ClearAndDestroyContents）。数字存储的核心能瞬间供料，所以这里**连送料一起做** ⇒ 真·全自动建造。
    // ==========================================================================================
    public class DigitalTaskAdapter_Construct : DigitalTaskAdapter
    {
        public override Type WorkGiverClass
        {
            get { return typeof(WorkGiver_ConstructFinishFrames); }
        }

        /// <summary>
        /// 不信任原版闸门：<c>WorkGiver_ConstructFinishFrames.JobOnThing</c> 会走
        /// <c>GenConstruct.CanConstruct</c>，而它含**可达性**判定（CanTouchTargetFromValidCell /
        /// CanReserveAndReach）—— 代理建筑是隔空干活的，照抄那道门就永远不干活。
        /// 技能/意识/阻塞那几项由 <see cref="DigitalTask_Construct.StillValid"/> 自己查。
        /// </summary>
        public override bool TrustWorkGiver
        {
            get { return false; }
        }

        public override DigitalTask MakeTask(Thing t, CompDigitalWorker comp)
        {
            return new DigitalTask_Construct { target = t, comp = comp };
        }

        public override IEnumerable<Thing> CandidateSet(Map map, Pawn pawn, WorkGiver_Scanner scanner)
        {
            List<Thing> blueprints = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint);
            List<Thing> frames = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame);
            List<Thing> all = new List<Thing>(blueprints.Count + frames.Count);
            all.AddRange(blueprints);
            all.AddRange(frames);
            return all;
        }

        public override bool CanTarget(Pawn pawn, Thing t)
        {
            // 只认建筑蓝图（Blueprint_Build）：安装蓝图（Blueprint_Install）要的是"小化的建筑"，
            // 不是钢/木料，供料逻辑不适用。
            return t is Blueprint_Build || t is Frame;
        }
    }

    public class DigitalTask_Construct : DigitalTask
    {
        private bool failed;

        public override string Label
        {
            get { return "建造"; }
        }

        private Frame FrameTarget
        {
            get { return target as Frame; }
        }

        public override bool StillValid(Pawn pawn, Map map)
        {
            if (target == null || target.Destroyed || !target.Spawned) return false;

            Blueprint bp = target as Blueprint;
            if (bp != null) return true;   // 蓝图：等我们供料；核心凑不齐会自己放手（见 Work）

            Frame f = FrameTarget;
            if (f == null) return false;
            if (!f.IsCompleted() || f.WorkLeft <= 0f) return false;
            if (f.Faction != null && pawn.Faction != null && f.Faction != pawn.Faction) return false;
            if (f.IsBurning()) return false;
            if (f.def.constructionSkillPrerequisite > 0 && pawn.skills != null
                && pawn.skills.GetSkill(SkillDefOf.Construction).Level < f.def.constructionSkillPrerequisite)
            {
                return false;
            }
            return GenConstruct.FirstBlockingThing(f, pawn) == null;
        }

        public override float Progress01
        {
            get
            {
                Frame f = FrameTarget;
                if (f == null || f.WorkToBuild <= 0f) return -1f;
                return Mathf.Clamp01(f.workDone / f.WorkToBuild);
            }
        }

        public override void Work(Pawn pawn, Map map, float speedMult)
        {
            if (target == null || target.Destroyed || !target.Spawned) return;

            // ---- 阶段 1：蓝图 → 框 + 从核心补料 ----
            Blueprint bp = target as Blueprint;
            if (bp != null)
            {
                if (!TrySupplyFromCores(pawn, map, bp))
                {
                    // 核心凑不齐 ⇒ 让路给原版搬运工（只放弃这一件，别动别的活）
                    Abort = true;
                }
                return;
            }

            // ---- 阶段 2：框 → 建完（照抄 JobDriver_ConstructFinishFrame.cs:44-80 的算式）----
            Frame f = FrameTarget;
            if (f == null || f.Destroyed) return;

            float num = speedMult * 1.7f;                 // 原版是 ConstructionSpeed * 1.7
            if (f.Stuff != null)
            {
                num *= f.Stuff.GetStatValueAbstract(StatDefOf.ConstructionSpeedFactor);
            }

            float workToBuild = f.WorkToBuild;
            if (workToBuild <= 0f)
            {
                f.CompleteConstruction(pawn);
                return;
            }

            if (pawn.Faction == Faction.OfPlayer && !TutorSystem.TutorialMode)
            {
                float successChance = pawn.GetStatValue(StatDefOf.ConstructSuccessChance);
                if (Rand.Value < 1f - Mathf.Pow(successChance, num / workToBuild))
                {
                    // 原版这里结束 toil 去重新拿材料；我们也交还这只"手"，下次重找
                    f.FailConstruction(pawn);
                    failed = true;
                    return;
                }
            }

            f.workDone += num;
            StrikeCount++;
            if (f.workDone >= workToBuild)
            {
                f.CompleteConstruction(pawn);
            }
        }

        public override bool Finished
        {
            get
            {
                if (Abort || failed) return true;
                Frame f = FrameTarget;
                if (f != null && !f.Destroyed && f.IsCompleted() && f.WorkLeft <= 0f) return true;
                return base.Finished;
            }
        }

        /// <summary>
        /// 把蓝图变成框，并把核心里的料塞进 <c>frame.resourceContainer</c>。
        /// 返回 false = 核心凑不齐（调用方应放手让原版搬运工做）。
        /// </summary>
        private bool TrySupplyFromCores(Pawn pawn, Map map, Blueprint bp)
        {
            List<ThingDefCountClass> cost = bp.TotalMaterialCost();
            if (cost == null || cost.Count == 0)
            {
                // 0 成本蓝图（原版走 JobDefOf.PlaceNoCostFrame）：直接转框，不用供料
                return TryReplace(pawn, bp);
            }

            // 先数一遍，不够就整单不做（不做"半送"—— 半送的料会留在框里等下一次，语义更乱）
            for (int i = 0; i < cost.Count; i++)
            {
                ThingDefCountClass need = cost[i];
                if (need == null || need.thingDef == null || need.count <= 0) continue;
                if (CountInCores(map, need.thingDef) < need.count) return false;
            }

            if (!TryReplace(pawn, bp)) return false;

            // 转框后 target 已是 Frame；料塞进它的 resourceContainer
            Frame f = FrameTarget;
            if (f == null || f.Destroyed) return true;

            ThingOwner dest = f.TryGetInnerInteractableThingOwner();
            if (dest == null) return true;

            for (int i = 0; i < cost.Count; i++)
            {
                ThingDefCountClass need = cost[i];
                if (need == null || need.thingDef == null || need.count <= 0) continue;
                TransferFromCores(map, need.thingDef, need.count, dest);
            }
            return true;
        }

        private bool TryReplace(Pawn pawn, Blueprint bp)
        {
            Thing created;
            bool jobEnded;
            if (!bp.TryReplaceWithSolidThing(pawn, out created, out jobEnded))
            {
                return false;
            }
            if (created != null)
            {
                target = created;   // 之后就是 Frame
            }
            return true;
        }

        private static int CountInCores(Map map, ThingDef def)
        {
            int total = 0;
            List<IHaulSource> cores = HaulSourceContents.CoreSources(map);
            for (int i = 0; i < cores.Count; i++)
            {
                ThingOwner owner = cores[i].GetDirectlyHeldThings();
                if (owner == null) continue;
                for (int j = 0; j < owner.Count; j++)
                {
                    Thing t = owner[j];
                    if (t != null && !t.Destroyed && t.def == def) total += t.stackCount;
                }
            }
            return total;
        }

        private static void TransferFromCores(Map map, ThingDef def, int count, ThingOwner dest)
        {
            int left = count;
            List<IHaulSource> cores = HaulSourceContents.CoreSources(map);
            for (int i = 0; i < cores.Count && left > 0; i++)
            {
                ThingOwner owner = cores[i].GetDirectlyHeldThings();
                if (owner == null) continue;
                for (int j = owner.Count - 1; j >= 0 && left > 0; j--)
                {
                    Thing t = owner[j];
                    if (t == null || t.Destroyed || t.def != def) continue;
                    int moved = owner.TryTransferToContainer(t, dest, Mathf.Min(left, t.stackCount), true);
                    if (moved > 0) left -= moved;
                }
            }
        }
    }

    // ==========================================================================================
    // 清洁 —— 照抄 JobDriver_CleanFilth 的算式；**放宽原版"只扫 Home 区"**（用户拍板）
    // ==========================================================================================
    public class DigitalTaskAdapter_Clean : DigitalTaskAdapter
    {
        public override Type WorkGiverClass
        {
            get { return typeof(WorkGiver_CleanFilth); }
        }

        /// <summary>
        /// 不信任原版：<c>WorkGiver_CleanFilth</c> 的候选集来自 <c>listerFilthInHomeArea</c>、
        /// 闸门里也卡 Home 区。用户拍板"放宽 Home 区（全图污物都能清）"⇒ 候选集换成全图 Filth，
        /// 迷雾/厚度那几道自己查。
        /// </summary>
        public override bool TrustWorkGiver
        {
            get { return false; }
        }

        public override IEnumerable<Thing> CandidateSet(Map map, Pawn pawn, WorkGiver_Scanner scanner)
        {
            return map.listerThings.ThingsInGroup(ThingRequestGroup.Filth);
        }

        public override DigitalTask MakeTask(Thing t, CompDigitalWorker comp)
        {
            return new DigitalTask_Clean { target = t, comp = comp };
        }

        public override bool CanTarget(Pawn pawn, Thing t)
        {
            return t is Filth;
        }
    }

    public class DigitalTask_Clean : DigitalTask
    {
        private float cleaningWorkDone;
        private float totalWorkRequired;

        public override string Label
        {
            get { return "清洁"; }
        }

        private Filth FilthTarget
        {
            get { return target as Filth; }
        }

        public override bool StillValid(Pawn pawn, Map map)
        {
            Filth filth = FilthTarget;
            if (filth == null || filth.Destroyed || !filth.Spawned) return false;
            if (map.fogGrid != null && map.fogGrid.IsFogged(filth.Position)) return false;
            // 原版 WorkGiver_CleanFilth 的节流：污物刚出现 600 tick 内不清理
            return filth.TicksSinceThickened >= 600;
        }

        public override float Progress01
        {
            get
            {
                Filth filth = FilthTarget;
                if (filth == null || filth.Destroyed || totalWorkRequired <= 0f) return -1f;
                float remaining = filth.thickness * filth.def.filth.cleaningWorkToReduceThickness - cleaningWorkDone;
                return Mathf.Clamp01(1f - remaining / totalWorkRequired);
            }
        }

        public override void Work(Pawn pawn, Map map, float speedMult)
        {
            Filth filth = FilthTarget;
            if (filth == null || filth.Destroyed) return;

            if (totalWorkRequired <= 0f)
            {
                totalWorkRequired = filth.def.filth.cleaningWorkToReduceThickness * filth.thickness;
            }

            float terrainFactor = filth.Position.GetTerrain(filth.Map)
                .GetStatValueAbstract(StatDefOf.CleaningTimeFactor);
            float num = speedMult;      // 速度只认倍率（原版 CleaningSpeed 本来就不吃技能/能力）
            if (terrainFactor != 0f)
            {
                num /= terrainFactor;
            }

            cleaningWorkDone += num;
            if (cleaningWorkDone > filth.def.filth.cleaningWorkToReduceThickness)
            {
                filth.ThinFilth();
                StrikeCount++;
                cleaningWorkDone = 0f;
                if (filth.Destroyed && pawn.records != null)
                {
                    pawn.records.Increment(RecordDefOf.MessesCleaned);
                }
            }
        }
    }

    // ==========================================================================================
    // 伐木 —— 照抄 JobDriver_PlantWork 的产出段（More Organs LaborTask_PlantBase 的移植）
    //
    // 归属：用户把"伐木"归进"种植"（每类一个专用建筑），所以种植代理的 workTypes = Growing + PlantCutting。
    // ⚠️ 收割（Growing/Harvest）与播种（Growing/Sow）**尚未接**：原版这两个 WorkGiver 是
    // **scanCells 型**（候选是格子不是 Thing），需要一条"格子型目标"的管线，留待下一波。
    // ==========================================================================================
    public class DigitalTaskAdapter_PlantCut : DigitalTaskAdapter
    {
        public override Type WorkGiverClass
        {
            get { return typeof(WorkGiver_PlantsCut); }
        }

        public override DigitalTask MakeTask(Thing t, CompDigitalWorker comp)
        {
            return new DigitalTask_PlantCut { target = t, comp = comp };
        }

        public override bool CanTarget(Pawn pawn, Thing t)
        {
            return t is Plant;
        }
    }

    public class DigitalTask_PlantCut : DigitalTask
    {
        private float workDone;

        public override string Label
        {
            get { return "伐木"; }
        }

        private Plant PlantTarget
        {
            get { return target as Plant; }
        }

        public override bool StillValid(Pawn pawn, Map map)
        {
            Plant p = PlantTarget;
            if (p == null || p.Destroyed || !p.Spawned) return false;
            return map.designationManager.DesignationOn(p, DesignationDefOf.CutPlant) != null;
        }

        public override float Progress01
        {
            get
            {
                Plant p = PlantTarget;
                if (p == null || p.def.plant.harvestWork <= 0f) return -1f;
                return Mathf.Clamp01(workDone / p.def.plant.harvestWork);
            }
        }

        public override void Work(Pawn pawn, Map map, float speedMult)
        {
            Plant p = PlantTarget;
            if (p == null || p.Destroyed) return;

            // 原版 JobDriver_PlantWork.WorkDonePerTick = PlantWorkSpeed * Lerp(3.3, 1, Growth)
            // 这里把 PlantWorkSpeed 换成建筑倍率（速度只认倍率），Growth 那一项照抄。
            workDone += speedMult * Mathf.Lerp(3.3f, 1f, p.Growth);
            StrikeCount++;

            if (workDone < p.def.plant.harvestWork) return;

            Harvest(pawn, p);
            workDone = 0f;
        }

        /// <summary>原样搬 JobDriver_PlantWork 的产出段，落点从 <c>actor.Position</c> 换成植株格。</summary>
        private void Harvest(Pawn pawn, Plant plant)
        {
            if (plant.def.plant.harvestedThingDef != null)
            {
                StatDef yieldStat = (plant.def.plant.harvestedThingDef.IsDrug || plant.def.plant.drugForHarvestPurposes)
                    ? StatDefOf.DrugHarvestYield
                    : StatDefOf.PlantHarvestYield;
                float statValue = pawn.GetStatValue(yieldStat);

                if (pawn.RaceProps.Humanlike && plant.def.plant.harvestFailable && !plant.Blighted && Rand.Value > statValue)
                {
                    // 不用 pawn.DrawPos：假 pawn 的 DrawPos 虽然已安全，但没必要碰它
                    MoteMaker.ThrowText(plant.DrawPos, pawn.Map,
                        "TextMote_HarvestFailed".Translate(), 3.65f);
                }
                else
                {
                    int num = plant.YieldNow();
                    if (statValue > 1f)
                    {
                        num = GenMath.RoundRandom(num * statValue);
                    }
                    if (num > 0)
                    {
                        Thing thing = ThingMaker.MakeThing(plant.def.plant.harvestedThingDef);
                        thing.stackCount = num;
                        Find.QuestManager.Notify_PlantHarvested(pawn, thing);
                        GenPlace.TryPlaceThing(thing, plant.Position, pawn.Map, ThingPlaceMode.Near);
                        if (pawn.records != null)
                        {
                            pawn.records.Increment(RecordDefOf.PlantsHarvested);
                        }
                    }
                    if (plant.HarvestableNow)
                    {
                        List<ThingComp> comps = plant.AllComps;
                        for (int i = 0; i < comps.Count; i++)
                        {
                            foreach (ThingDefCountClass extra in comps[i].GetAdditionalHarvestYield())
                            {
                                Thing extraThing = ThingMaker.MakeThing(extra.thingDef);
                                extraThing.stackCount = extra.count;
                                GenPlace.TryPlaceThing(extraThing, plant.Position, pawn.Map, ThingPlaceMode.Near);
                            }
                        }
                    }
                }
            }

            if (plant.def.plant.soundHarvestFinish != null)
            {
                // 声音源用植株本身（隔空干活没有"人"在场，也避开假 pawn 的 TargetInfo）
                plant.def.plant.soundHarvestFinish.PlayOneShot(plant);
            }

            plant.PlantCollected(pawn, PlantDestructionMode.Cut);

            // 原版收尾 toil = Toils_Interact.DestroyThing（它自己带 !Destroyed 守卫）
            if (!plant.Destroyed && plant.Spawned)
            {
                plant.Destroy();
            }
        }
    }
}
