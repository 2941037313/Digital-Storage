using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Compatibility
{
    /// <summary>
    /// <b>与 Pick Up And Haul 的冲突：明确声明，不做兼容</b>（2026-10-12 用户拍板）。
    ///
    /// <para><b>为什么不做兼容</b>：本 mod 的入库是<b>瞬移</b>（散落物自动进核心、材料直送台子），
    /// 压根没有"搬运作业"可以交接；而 PUAH 的核心机制是"先组批、再把东西一件件塞进容器"
    /// （<c>JobDriver_UnloadYourHauledInventory</c>），它全程拿着 <b>Thing 引用</b>，
    /// 判定"这件东西还在不在小人背包里"靠的是一份自己的 <c>HashSet</c>。
    /// 我们把人正要拿的那件东西瞬移进核心时**并没有销毁它** ⇒ 对方的引用不会失效，
    /// 两边状态就此脱节（实测表现：小人原地发呆 / 反复取物 / 每 tick 一条红字）。</para>
    ///
    /// <para>要真让两者共存，等于把本 mod 的地基从"瞬移"换成"派 job"—— 那就不再是这个 mod 了。
    /// 而社区调研的结论正好相反：「有核心了没必要加 PUAH」（PUAH 省的是搬运趟数，
    /// 而这里根本不搬运）。所以取"声明不兼容 + 给玩家开关"这条路线：
    /// <b>启动时日志里报三行</b>，<b>每次进入存档再给一封显眼的红信</b>，
    /// 并指出那条真正的出路 —— 设置里的「自动收纳」总开关（关掉瞬移，两者即可共存）。</para>
    /// </summary>
    internal static class PickUpAndHaulConflict
    {
        /// <summary>PUAH 本体（工坊 1279012058）。</summary>
        private const string PackageId = "Mehni.PickUpAndHaul";

        /// <summary>中文分支（工坊 3522897816）—— 只查本体 packageId 会漏掉它。</summary>
        private const string PackageIdCn = "day29zh.PickUpAndHaul";

        /// <summary>
        /// PUAH 的一个类型名（它的 <c>CompHauledToInventory</c>，ThingComp，代码里写死的类名）。
        ///
        /// <para><b>为什么不能只查 <c>ModsConfig</c></b>：fork / 汉化分支 / 手装副本 / 改名的
        /// packageId 各不相同，而"装配体里到底有没有 PUAH 的补丁代码"才是我们要问的问题 ——
        /// 类型名比 packageId 稳。两条路都走：先 mod 列表（覆盖已知 id），再类型表（覆盖未知的改名分支）。</para>
        /// </summary>
        private const string ProbeType = "PickUpAndHaul.CompHauledToInventory";

        private static bool probed;
        private static bool active;

        /// <summary>PUAH 在不在（结果缓存：mod 列表与类型表在整个进程里不会变）。</summary>
        public static bool IsActive
        {
            get
            {
                if (probed) return active;
                probed = true;
                try
                {
                    if (ModsConfig.IsActive(PackageId) || ModsConfig.IsActive(PackageIdCn)) active = true;
                    else active = AccessTools.TypeByName(ProbeType) != null;
                }
                catch (Exception e)
                {
                    active = false;
                    Log.ErrorOnce("[DigitalStorage] 检测 Pick Up And Haul 时出错（按未安装处理）：" + e, 0x44534301);
                }
                return active;
            }
        }

        /// <summary>
        /// 启动时把三行报错写进日志（每进程一次）。
        /// 放在 <c>HarmonyInit</c> 的静态构造里而不是读档时：反馈里附的第一个文件永远是 <c>Player.log</c>。
        /// </summary>
        public static void LogOnce()
        {
            if (!IsActive) return;
            Log.ErrorOnce("[DigitalStorage] " + Body(), 0x44534302);
        }

        /// <summary>
        /// 进入存档后弹一封**红信**（<c>LetterDefOf.NegativeEvent</c>，留在信件栏、不挡操作）。
        /// 刻意做成"每次进档都弹"：这是**不兼容声明**，不是一次性通知 ——
        /// 玩家换了档、换了 mod 列表，都可能重新踩进来。
        /// <para>唯一的例外：**玩家已经关掉「自动收纳」**（信里给的那条共存出路）⇒ 冲突已解，
        /// 这时不再弹（否则每次读档都误报一次）。启动日志不受此限。</para>
        /// </summary>
        public static void ReceiveLetter()
        {
            if (!IsActive) return;

            // 玩家已经按信里的出路做了 ⇒ 冲突**已经解开**，再每次读档弹红信就是误报：
            // 关掉「自动收纳」= 瞬移停止、搬运交还给殖民者与 PUAH，这正是共存那条路。
            // （启动时那三行日志仍然照打：它是"装了 PUAH"这个**事实** + 共存办法，
            //   对排查有用，且不在玩家眼前刷屏。）
            if (!DigitalStorage.Settings.DigitalStorageSettings.autoIngestEnabled) return;

            Find.LetterStack.ReceiveLetter(
                "DS_PUAH_LetterLabel".Translate(),
                "DS_PUAH_LetterText".Translate(),
                LetterDefOf.NegativeEvent);
        }

        /// <summary>
        /// 三行正文（keyed，中英各一份）—— 日志与信件**共用同一份**，绝不各写一套
        /// （两套文案一定会漂移，然后玩家截图与日志对不上）。
        /// </summary>
        private static string Body()
        {
            return "DS_PUAH_LetterText".Translate();
        }
    }
}
