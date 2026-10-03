using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Settings
{
    /// <summary>
    /// <b>三个"数值倍率"设置的实际施加者</b>：造价（<c>costMultiplier</c>）、
    /// 研究点数（<c>researchMultiplier</c>），以及电力（<c>powerMultiplier</c>，本体在
    /// <see cref="HarmonyPatches.Patch_PowerMultiplier"/> —— 这里只负责"让已建成的建筑重算一遍"）。
    ///
    /// <para><b>为什么必须有基线快照</b>：这些倍率改的是 <c>Def</c> 自己的字段
    /// （<c>costList[].count</c> / <c>costStuffCount</c> / <c>baseCost</c>）。
    /// 直接"当前值 × 倍率"会在每次改设置时**复利**（1× → 2× → 再改回 1× 只剩 1.5×）。
    /// 所以第一次施加前把 XML 原值抄一份进快照，之后每次施加都是
    /// <c>原值 × 倍率</c> ⇒ 幂等，反复拖滑条也不会漂移。</para>
    ///
    /// <para><b>施加时机</b>：① <c>[StaticConstructorOnStartup]</c>（def 与 Mod 设置都已就绪）；
    /// ② 设置窗口里滑条真的变化时（<see cref="DigitalStorageSettings.DoSettingsWindowContents"/>），
    /// 让玩家拖完就能在建造菜单里看到新造价。</para>
    ///
    /// <para><b>范围</b>：造价 = 所有 <c>ThingDef</c>（建筑 / 物品）与 <c>TerrainDef</c>（地板）的
    /// 建造材料；研究 = 所有 <c>ResearchProjectDef</c>。都是**全局**（含原版与其它 mod），
    /// 与设置里写明的口径一致。</para>
    ///
    /// <para><b>四舍五入</b>：<c>Mathf.Max(1, Round(原值 × 倍率))</c> —— 最小 1 件，
    /// 既不会出现 0 消耗的蓝图（原版对 0 耗材的工地行为没保障），也让 1/100 档对
    /// "需求 4 个零件"这种小数目退化成"1 个"而不是"0 个"。</para>
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class DefMultipliers
    {
        /// <summary>原值快照（只在第一次施加前抄一次）。</summary>
        private sealed class CostSnapshot
        {
            public int[] counts;      // BuildableDef.costList
            public int stuff;         // BuildableDef.costStuffCount
            public int[] diffCounts;  // costListForDifficulty.costList
            public int diffStuff;     // costListForDifficulty.costStuffCount
        }

        private sealed class ResearchSnapshot
        {
            public float baseCost;
            public float knowledgeCost;
        }

        private static readonly Dictionary<BuildableDef, CostSnapshot> costSnapshots
            = new Dictionary<BuildableDef, CostSnapshot>();

        private static readonly Dictionary<ResearchProjectDef, ResearchSnapshot> researchSnapshots
            = new Dictionary<ResearchProjectDef, ResearchSnapshot>();

        private static bool captured;

        static DefMultipliers()
        {
            ApplyAll();
        }

        /// <summary>抄基线 → 施加造价与研究 → 让已建成的建筑重算电力。幂等，随时可调。</summary>
        public static void ApplyAll()
        {
            try
            {
                Capture();
                ApplyCost(DigitalStorageSettings.costMultiplier);
                ApplyResearch(DigitalStorageSettings.researchMultiplier);
                RefreshPower();
            }
            catch (Exception e)
            {
                Log.Error("[DigitalStorage] 倍率设置施加失败（本次改动未生效）：" + e);
            }
        }

        // ===================================================================
        // 基线
        // ===================================================================

        /// <summary>
        /// 抄一份原值。**Def 还没加载完时直接返回、什么都不锁**：
        /// <c>Mod</c> 构造（读设置文件）可能早于 XML 解析，那时 <c>DefDatabase</c> 还是空的，
        /// 若在这里把 <c>captured = true</c> 钉死，后面就永远抄不到基线了。
        /// </summary>
        private static void Capture()
        {
            if (captured) return;

            List<ThingDef> things = DefDatabase<ThingDef>.AllDefsListForReading;
            if (things == null || things.Count == 0) return;

            for (int i = 0; i < things.Count; i++) CaptureCost(things[i]);

            List<TerrainDef> terrains = DefDatabase<TerrainDef>.AllDefsListForReading;
            if (terrains != null)
            {
                for (int i = 0; i < terrains.Count; i++) CaptureCost(terrains[i]);
            }

            List<ResearchProjectDef> research = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
            if (research != null)
            {
                for (int i = 0; i < research.Count; i++)
                {
                    ResearchProjectDef def = research[i];
                    if (def == null) continue;
                    if (def.baseCost <= 0f && def.knowledgeCost <= 0f) continue;
                    researchSnapshots[def] = new ResearchSnapshot
                    {
                        baseCost = def.baseCost,
                        knowledgeCost = def.knowledgeCost
                    };
                }
            }

            captured = true;
        }

        private static void CaptureCost(BuildableDef def)
        {
            if (def == null) return;

            CostSnapshot s = new CostSnapshot();
            bool any = false;

            if (def.costList != null && def.costList.Count > 0)
            {
                s.counts = new int[def.costList.Count];
                for (int i = 0; i < def.costList.Count; i++)
                    s.counts[i] = def.costList[i] != null ? def.costList[i].count : 0;
                any = true;
            }
            if (def.costStuffCount > 0)
            {
                s.stuff = def.costStuffCount;
                any = true;
            }
            if (def.costListForDifficulty != null)
            {
                List<ThingDefCountClass> diff = def.costListForDifficulty.costList;
                if (diff != null && diff.Count > 0)
                {
                    s.diffCounts = new int[diff.Count];
                    for (int i = 0; i < diff.Count; i++)
                        s.diffCounts[i] = diff[i] != null ? diff[i].count : 0;
                    any = true;
                }
                if (def.costListForDifficulty.costStuffCount > 0)
                {
                    s.diffStuff = def.costListForDifficulty.costStuffCount;
                    any = true;
                }
            }

            if (any) costSnapshots[def] = s;
        }

        // ===================================================================
        // 施加
        // ===================================================================

        private static void ApplyCost(float mult)
        {
            foreach (KeyValuePair<BuildableDef, CostSnapshot> kv in costSnapshots)
            {
                BuildableDef def = kv.Key;
                CostSnapshot s = kv.Value;

                SetCounts(def.costList, s.counts, mult);
                if (s.stuff > 0) def.costStuffCount = Scaled(s.stuff, mult);

                if (def.costListForDifficulty != null)
                {
                    SetCounts(def.costListForDifficulty.costList, s.diffCounts, mult);
                    if (s.diffStuff > 0)
                        def.costListForDifficulty.costStuffCount = Scaled(s.diffStuff, mult);
                }
            }
        }

        private static void SetCounts(List<ThingDefCountClass> list, int[] baseline, float mult)
        {
            if (list == null || baseline == null) return;
            int n = Mathf.Min(list.Count, baseline.Length);
            for (int i = 0; i < n; i++)
            {
                if (list[i] != null) list[i].count = Scaled(baseline[i], mult);
            }
        }

        private static void ApplyResearch(float mult)
        {
            foreach (KeyValuePair<ResearchProjectDef, ResearchSnapshot> kv in researchSnapshots)
            {
                ResearchProjectDef def = kv.Key;
                if (def == null) continue;
                if (kv.Value.baseCost > 0f) def.baseCost = ScaledFloat(kv.Value.baseCost, mult);
                if (kv.Value.knowledgeCost > 0f) def.knowledgeCost = ScaledFloat(kv.Value.knowledgeCost, mult);
            }
        }

        /// <summary>最少 1 件：0 耗材的蓝图/研究进度在哪个分支都不好看。</summary>
        private static int Scaled(int original, float mult)
        {
            if (original <= 0) return original;
            return Mathf.Max(1, Mathf.RoundToInt(original * mult));
        }

        private static float ScaledFloat(float original, float mult)
        {
            if (original <= 0f) return original;
            return Mathf.Max(1f, original * mult);
        }

        /// <summary>
        /// 让**已建成**的用电建筑按新倍率重算 <c>PowerOutput</c>。
        /// 抄的原版自己的做法（<c>ResearchManager.cs:448</c>：研究完成且 <c>recalculatePower</c> 时
        /// 就是 <c>map.listerThings.ThingsInGroup(ThingRequestGroup.PowerTrader)</c> + <c>SetUpPowerVars()</c>）。
        /// 电网每 tick 从各 comp 的 <c>PowerOutput</c> 现算（<c>PowerNet.PowerNetTick</c>），
        /// 所以重算完下一 tick 就生效，不需要通知任何网络。
        /// </summary>
        private static void RefreshPower()
        {
            if (Current.ProgramState != ProgramState.Playing) return;

            List<Map> maps = Find.Maps;
            if (maps == null) return;

            for (int i = 0; i < maps.Count; i++)
            {
                Map map = maps[i];
                if (map == null || map.listerThings == null) continue;

                List<Thing> comps = map.listerThings.ThingsInGroup(ThingRequestGroup.PowerTrader);
                for (int j = 0; j < comps.Count; j++)
                {
                    CompPowerTrader comp = comps[j]?.TryGetComp<CompPowerTrader>();
                    if (comp != null) comp.SetUpPowerVars();
                }
            }
        }
    }
}
