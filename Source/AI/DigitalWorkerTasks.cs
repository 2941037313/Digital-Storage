using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using UnityEngine;
using Verse;

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

        /// <summary>面板/调试用的活名（挖掘 / 建造 / …）。</summary>
        public abstract string Label { get; }

        /// <summary>还能不能继续干（每 tick 问一次）。</summary>
        public abstract bool StillValid(Pawn pawn, Map map);

        /// <summary>干 1 tick 的活。<paramref name="speedMult"/> 已经乘进各类的算式里。</summary>
        public abstract void Work(Pawn pawn, Map map, float speedMult);

        public virtual bool Finished
        {
            get { return target == null || target.Destroyed || !target.Spawned; }
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

        public override string Label
        {
            get { return "挖掘"; }
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
}
