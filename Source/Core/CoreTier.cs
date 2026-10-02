using RimWorld;
using Verse;

namespace DigitalStorage.Core
{
    /// <summary>
    /// 存储核心等级（Lv1~Lv4）—— 容量阶梯**挂在研究上**，不再挂在建筑上的升级组件上。
    ///
    /// <para>4.0 一度把升级整个删掉（"核心一放就是完全体"，上限硬编码 500 栈）。
    /// 用户拍板把它以**研究**的形式请回来 —— 三条研究，点数与目标上限同数：</para>
    ///
    /// <list type="table">
    /// <listheader><term>等级</term><description>上限 / 解锁方式</description></listheader>
    /// <item><term>Lv1</term><description>500 栈 —— 建筑自带，无研究</description></item>
    /// <item><term>Lv2</term><description>1000 栈 —— 研究「核心 Lv2」1000 点</description></item>
    /// <item><term>Lv3</term><description>1500 栈 —— 研究「核心 Lv3」1500 点</description></item>
    /// <item><term>Lv4</term><description>3000 栈 —— 研究「核心 Lv4」3000 点</description></item>
    /// </list>
    ///
    /// <para><b>为什么是全局一份缓存，而不是每个核心各存一个等级</b>：研究是殖民地级的、只增不减，
    /// 所以"当前等级"全局唯一。核心只读它 ⇒ 研究一完成，**地图上已有的核心立刻扩容**，
    /// 不需要重建、不需要重读档，也不需要给每个核心写通知。</para>
    ///
    /// <para><b>为什么按 tick 缓存</b>：<c>Building_StorageCore.Accepts</c> 是热路径
    /// （原版每找一个存储目的地都会问一次），而 <c>ResearchProjectDef.IsFinished</c> 要走
    /// <c>ResearchManager.GetProgress</c> 的字典。这里记下"本 tick 已经查过了"
    /// ⇒ 热路径上只剩一次 int 比较；研究完成的感知延迟上限 = 1 tick，肉眼看不出。</para>
    ///
    /// <para><b>为什么满级后直接短路</b>：研究只增不减，到 Lv4 就再没有可查的东西了，
    /// 之后的每次调用都是第一次 if 就返回。</para>
    /// </summary>
    internal static class CoreTier
    {
        /// <summary>最高等级（Lv4）。</summary>
        public const int MaxLevel = 4;

        /// <summary>Lv1 基准上限 —— 建筑自带，无需任何研究。</summary>
        public const int BaseStacks = 500;

        /// <summary>
        /// 等级 → 研究 defName。**下标 0 是 Lv1（无研究）**，所以下标 i 对应等级 i + 1。
        /// 顺序即阶梯顺序，<see cref="Ensure"/> 从最高级往下找第一个已完成的。
        /// </summary>
        private static readonly string[] ResearchByLevel =
        {
            null,
            "DigitalStorage_CoreLevel2",
            "DigitalStorage_CoreLevel3",
            "DigitalStorage_CoreLevel4",
        };

        /// <summary>等级 → 栈数上限。**下标 = 等级 - 1**。</summary>
        private static readonly int[] CapByLevel = { BaseStacks, 1000, 1500, 3000 };

        private static int cachedLevel = 1;

        /// <summary><c>int.MinValue</c> 保证进游戏后的第一次调用一定会去查研究。</summary>
        private static int cachedTick = int.MinValue;

        /// <summary>当前等级（1~4）。可在热路径调用。</summary>
        public static int Level
        {
            get
            {
                Ensure();
                return cachedLevel;
            }
        }

        /// <summary>当前等级对应的栈数上限（500 / 1000 / 1500 / 3000）。</summary>
        public static int Cap
        {
            get
            {
                Ensure();
                return CapByLevel[cachedLevel - 1];
            }
        }

        private static void Ensure()
        {
            // 满级：研究只增不减，此后无新信息可查。
            if (cachedLevel == MaxLevel) return;

            TickManager tickManager = Find.TickManager;
            // 不在游戏里（主菜单 / 加载早期）：保持上次结果，默认 Lv1。
            if (tickManager == null) return;

            int tick = tickManager.TicksGame;
            if (tick == cachedTick) return;
            cachedTick = tick;

            if (Find.ResearchManager == null) return;

            for (int i = ResearchByLevel.Length - 1; i >= 1; i--)
            {
                ResearchProjectDef def = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(ResearchByLevel[i]);
                // GetNamedSilentFail：万一子模组/翻译把它删了也不刷红字，安静退化成 Lv1。
                if (def != null && def.IsFinished)
                {
                    cachedLevel = i + 1;
                    return;
                }
            }
            cachedLevel = 1;
        }
    }
}
