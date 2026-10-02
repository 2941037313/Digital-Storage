using System;
using RimWorld;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// <b>假 pawn（"资质载体"）工厂</b> —— 从 <see cref="DigitalStorage.Components.CompDigitalWorker.EnsureWorker"/>
    /// 原样抽出，供两个 comp 共用：<c>CompDigitalWorker</c>（挖掘/建造/清洁/种植）与
    /// <c>CompBillAutomation</c>（制作代理）。
    ///
    /// <para>抽出而不是复制：这段里有四条**踩过坑才写对**的东西（组件补齐 / 不进注册表 / 清特质 / 固定资质），
    /// 两处各写一份必然漂移 —— 而漂移的表现是"某个代理类型偶尔 NRE"，最难查。</para>
    ///
    /// <para><b>为什么不 spawn 真 pawn</b>：见 obsidian <c>代码Wiki/rimworld/代理工人-脱离job直调产出.md</c>。
    /// 假 pawn 只是"原版 API 要求的参数形状"，从不进 TickManager、不进存档。</para>
    /// </summary>
    internal static class DigitalWorkerFactory
    {
        /// <summary>
        /// 造一个资质固定为 <paramref name="skillLevel"/> 的假殖民者。
        /// 失败返回 null（调用方按"没有工人"处理，绝不抛进 tick）。
        /// </summary>
        public static Pawn Create(int skillLevel, string nameShort, string nameNick)
        {
            try
            {
                // 先例：god hand MapComponent_GodAssistant.cs:18-33
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                p.Name = new NameTriple("", nameShort, nameNick);

                // ★ 补上"生成时不需要、被 spawn 时才建"的那批组件（pather / rotationTracker / natives /
                //   filth / roping…）。不补的话，**任何读 pawn.DrawPos 的原版代码都会 NRE** ——
                //   PawnTweener.TweenedPosRoot 直接解引用 pawn.pather（Verse\PawnTweener.cs:104），
                //   而 pather 是 Pawn.SpawnSetup → PawnComponentsUtility.AddComponentsForSpawn 才建的。
                //   实测踩过：原版挖掘特效的 sprayer 取 TargetInfo.CenterVector3（→ Pawn.DrawPos）时炸掉。
                //   AddComponentsForSpawn 内部对"还没真的 spawn"的 pawn 是安全的
                //   （它给 AddAndRemoveDynamicComponents 传 actAsIfSpawned: true，PawnComponentsUtility.cs:206）。
                PawnComponentsUtility.AddComponentsForSpawn(p);

                // ① 不进地图注册表：即便作用域期间 Spawned 为 true，RegisterPawn 也会早退
                //    （MapPawns.cs:847 `if (!p.mindState.Active) return;`）
                p.mindState.Active = false;

                // ② 清掉随机特质 —— WorkTypeIsDisabled 会吃背景/特质，机器不该因抽到
                //    "不能做熟练劳动"而罢工（Notify_DisabledWorkTypesChanged 会清 Pawn 侧缓存）
                if (p.story != null && p.story.traits != null && p.story.traits.allTraits != null)
                {
                    p.story.traits.allTraits.Clear();
                }
                if (p.relations != null)
                {
                    p.relations.ClearAllRelations();
                }

                // ③ 固定资质：技能只进品质/门槛，且**永不成长**（任何地方都不调 skills.Learn）
                if (p.skills != null)
                {
                    for (int i = 0; i < p.skills.skills.Count; i++)
                    {
                        p.skills.skills[i].Level = skillLevel;
                    }
                }
                p.Notify_DisabledWorkTypesChanged();

                if (p.workSettings != null)
                {
                    p.workSettings.EnableAndInitialize();
                }
                return p;
            }
            catch (Exception e)
            {
                Log.Error("[DigitalStorage] 生成数字工人失败：" + e);
                return null;
            }
        }
    }
}
