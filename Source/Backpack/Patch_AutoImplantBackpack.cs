using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Backpack
{
    /// <summary>
    /// 「背包」植入判据 + 状态同步的**唯一入口**（三个 Harmony 钩子共用，避免判据漂移）。
    ///
    /// <para><b>谁该有</b>：玩家派系的殖民者（含奴隶）+ 殖民地机械族。
    /// <b>囚犯不给</b>（用户拍板）：囚犯不干活、也不该碰核心，给他们挂背包只会在健康页多一行噪音。</para>
    ///
    /// <para>不需要注册成 haul source：原版 bill 的容器扫描要求 <c>item is Thing</c>
    /// （<c>WorkGiver_DoBill.cs:483</c>），HediffComp 永远进不去。
    /// 详见 <see cref="HediffComp_Backpack"/> 的类注释。</para>
    /// </summary>
    internal static class BackpackImplant
    {
        private const string HediffDefName = "DS_CoreBackpack";
        private static HediffDef def;
        private static bool defResolved;

        private static HediffDef Def
        {
            get
            {
                if (!defResolved)
                {
                    def = DefDatabase<HediffDef>.GetNamedSilentFail(HediffDefName);
                    defResolved = true;
                }
                return def;
            }
        }

        internal static bool ShouldHave(Pawn pawn)
        {
            if (pawn == null || pawn.def == null || pawn.RaceProps == null) return false;
            if (pawn.health == null || pawn.health.hediffSet == null) return false;
            if (pawn.Dead) return false; // 尸体不需要背包（死人身上的背包由 Kill 补丁清）
            if (pawn.Faction != Faction.OfPlayer) return false;
            if (pawn.IsPrisoner) return false; // 用户拍板：囚犯不给
            if (pawn.RaceProps.Humanlike) return true;
            // 机械族：默认也给（它们也会做 bill），但留一个开关给玩家对照排查 ——
            // 背包是挂在**别人的 pawn** 上的 hediff，而机械族 mod 遍地都是。
            return pawn.RaceProps.IsMechanoid && DigitalStorage.Settings.DigitalStorageSettings.backpackForMechanoids;
        }

        /// <summary>
        /// 幂等：该有的补上，不该有的摘掉。摘除会走 <c>CompPostPostRemoved</c>（内容物落地，防丢物）。
        ///
        /// <para><b>整体 try/catch 是硬要求</b>：本方法挂在原版 <c>Pawn.SpawnSetup</c> 的 postfix 上，
        /// 异常一旦从这里抛出去，那个 pawn 就是"生成到一半"，之后它自己的 Tick / 绘制会以
        /// 「某个本该存在的组件/引用是 null」的形式炸 —— 症状看起来完全像是**别的 mod** 坏了
        /// （2026-10-03 用户反馈的「米莉拉无人机/机器人 Tick/绘制 NRE」就是这个形状，值得先排除我们）。
        /// 我们的背包只是"顺手带的"，绝不该有能力弄坏别的 mod 的 pawn。</para>
        /// </summary>
        internal static void Sync(Pawn pawn)
        {
            try
            {
                HediffDef hediffDef = Def;
                if (hediffDef == null || pawn == null || pawn.health == null || pawn.health.hediffSet == null) return;

                Hediff existing = pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef);
                if (ShouldHave(pawn))
                {
                    if (existing == null)
                    {
                        pawn.health.AddHediff(hediffDef);
                        LogChange(pawn, added: true);
                    }
                }
                else if (existing != null)
                {
                    pawn.health.RemoveHediff(existing);
                    LogChange(pawn, added: false);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 挂/摘数字存储背包失败（这台 pawn 跳过，其余照常）：" + e, 0x44534250);
            }
        }

        /// <summary>
        /// 只在**真的挂上 / 摘掉**时打一行（且只在「启用详细日志」时）。
        /// 存在的意义：招募 / 囚禁 / 奴役这些身份切换发生在几十个 tick 的流程里，
        /// 玩家（和我们）需要在日志里看到"到底哪一步切了、当时判成什么"。
        /// </summary>
        private static void LogChange(Pawn pawn, bool added)
        {
            if (!DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog) return;
            if (pawn == null) return;

            string who = pawn.LabelShortCap;
            string faction = (pawn.Faction == null) ? "无派系" : pawn.Faction.Name;
            Log.Warning("[DS] 数字存储背包 " + (added ? "挂上" : "摘掉") + "：" + who
                + "（阵营=" + faction + (pawn.IsPrisoner ? "，囚犯" : (pawn.IsSlave ? "，奴隶" : "")) + "）");
        }
    }

    /// <summary>
    /// 植入 / 摘除。用 <c>Pawn.SpawnSetup</c> 补挂：地图载入时 pawn 会重新 Spawn ⇒ 读档也覆盖；
    /// 新加入的殖民者同样走这里。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), "SpawnSetup")]
    internal static class Patch_AutoImplantBackpack
    {
        [HarmonyPostfix]
        private static void Postfix(Pawn __instance)
        {
            if (__instance == null) return;
            BackpackImplant.Sync(__instance);
            // 【这里原本有 StripLegacy30Chip —— 已随墓碑 Def 一起删除（用户 2026-10-02 拍板）】
            //
            // 它做的是「摘掉 3.0 的终端植入体并退回一枚终端芯片」（那玩意值 1000 银）。
            // 决定不再携带 Legacy30_Compat.xml 之后，这条迁移**自然作废**，因为：
            //   HediffDef 找不到 ⇒ ScribeExtractor.SaveableFromNode 捕获异常返回 null
            //   ⇒ HediffSet.ExposeData:251 的 hediffs.RemoveAll(x => x == null) 把它丢掉，
            //     只留一行 "had some null hediffs."
            // 也就是说旧存档里的植入体**根本解析不出来**，没有东西可摘、也没有东西可退。
            // 顺带：那一版的墓碑 Def 还会每次启动刷 3 条 config error
            // （ResearchProjectDef 要求 researchViewY >= 0，我却为了藏到页签外写了 -3；
            //   墓碑建筑基类给了 minifiedDef 却没给 thingCategories）。
            // 旧存档现在会看到几条一次性「找不到 Def」+ 几条落物 NRE 栈 —— 已接受。
        }
    }

    /// <summary>
    /// 囚犯 ↔ 殖民者的**身份切换点**（guest 状态那一半）。
    ///
    /// <para>⚠️ <b>这个钩子单独用不够</b>（2026-10-03 用户反馈「招募囚犯后不会加背包」就是它）：
    /// 招募时它跑得<b>早了一步</b> —— 见 <see cref="Patch_BackpackOnFactionChanged"/> 里的解释。
    /// 但它对"囚犯 ↔ 奴隶"这种**阵营不变、只有 guest 状态变**的切换仍然是对的那个点，
    /// 所以两个钩子都留着（<c>Sync</c> 幂等，重复调用无副作用）。</para>
    ///
    /// <para>只挂 <c>SpawnSetup</c> 会漏掉"游戏中途被俘 / 被招募 / 被买奴"（都不重新 Spawn）。</para>
    /// </summary>
    [HarmonyPatch(typeof(Pawn_GuestTracker), "SetGuestStatus")]
    internal static class Patch_BackpackOnGuestStatusChanged
    {
        // pawn 是 private 字段 ⇒ 用 Harmony 的下划线字段注入
        [HarmonyPostfix]
        private static void Postfix(Pawn ___pawn)
        {
            if (___pawn == null) return;
            BackpackImplant.Sync(___pawn);
        }
    }

    /// <summary>
    /// <b>阵营变更点</b>：<c>Pawn.SetFaction</c> 的 postfix —— <b>招募的正确钩子</b>。
    ///
    /// <para><b>为什么必须补这一条</b>：<c>RecruitUtility.Recruit</c>
    /// （<c>RecruitUtility.cs:24-27</c>）的顺序是</para>
    /// <code>
    /// if (pawn.guest != null) pawn.guest.SetGuestStatus(null);        // ① 先清 guest 状态
    /// if (pawn.Faction != faction) pawn.SetFaction(faction, recruiter); // ② 再换阵营
    /// </code>
    /// <para>只挂 <c>SetGuestStatus</c> 的话，我们的 postfix 在 <b>①</b> 跑 —— 那一刻
    /// <c>pawn.Faction</c> 还是**原来那个敌对阵营**，于是
    /// <c>BackpackImplant.Sync</c> 判定"不该有" ⇒ 什么都不做；等 <b>②</b> 把阵营换成玩家时，
    /// 已经没有任何钩子会再调一次 <c>Sync</c> ⇒ <b>招募来的小人一直没有背包</b>
    /// （要等下次读档 / 重新 Spawn 才补上）。用户 2026-10-03 反馈的正是这个。</para>
    ///
    /// <para>挂 <c>SetFaction</c> 之后，招募 / 被俘 / 买奴 / 释放 / 加入派系 全部会自动重算
    /// （<c>Sync</c> 幂等，且判据只看"当前 Faction + 是不是囚犯"）。</para>
    /// </summary>
    [HarmonyPatch(typeof(Pawn), "SetFaction")]
    internal static class Patch_BackpackOnFactionChanged
    {
        [HarmonyPostfix]
        private static void Postfix(Pawn __instance)
        {
            if (__instance == null) return;
            BackpackImplant.Sync(__instance);
        }
    }

    /// <summary>
    /// 死亡时清空背包。
    ///
    /// <para><b>为什么不能只靠 <c>CompPostTickInterval</c></b>：
    /// <c>Pawn_HealthTracker.HealthTickInterval</c> 第一行就是 <c>if (Dead) return;</c>
    /// （<c>Pawn_HealthTracker.cs:1071</c>）⇒ 尸体身上的 hediff 永远等不到周期检查，
    /// 而尸体/hediff 之后也不会被移除（<c>CompPostPostRemoved</c> 同样不会来）。
    /// 背包内容物会跟着 hediff 对象一起烂在存档里 —— 那是丢物。</para>
    ///
    /// <para>先试退回核心（干净、不用搬运工），退不掉（没核心 / 断电）才落地。</para>
    /// </summary>
    [HarmonyPatch(typeof(Pawn), "Kill")]
    internal static class Patch_BackpackEjectOnDeath
    {
        [HarmonyPostfix]
        private static void Postfix(Pawn __instance)
        {
            // 死亡路径同样不能抛：挂在原版 Pawn.Kill 上，抛出去会让"死亡处理"停在半路。
            try
            {
                HediffComp_Backpack bag = HediffComp_Backpack.For(__instance);
                if (bag == null || bag.Count == 0) return;

                bag.ReturnContentsToCore();
                if (bag.Count > 0) bag.EjectAll();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 死亡时清空背包失败（物品仍留在背包里，不会丢）：" + e, 0x44534251);
            }
        }
    }
}
