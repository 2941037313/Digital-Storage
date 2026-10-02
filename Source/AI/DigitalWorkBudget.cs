using DigitalStorage.Settings;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// <b>「每 tick 最多让几件活真正落地」的预算闸门</b> —— 治卡顿的那道闸。
    ///
    /// <para><b>为什么需要它</b>：完成一件活要付一次**原版的一次性代价**：
    /// 挖掘 <c>Mineable.DestroyMined</c> 要生成掉落物（<c>GenPlace.TryPlaceThing</c> 得**搜附近空位**）
    /// 再销毁；建造完 <c>Frame.CompleteConstruction</c> 要生成建筑；拆除 <c>Destroy(Deconstruct)</c>
    /// 要还材料；收获要生成作物。每一项都伴随 region 变脏 + <c>listerThings</c> 注册/注销。
    /// 实测量级约 <b>0.4~0.5ms/件</b>（60Hz 的 log：刻度 40.7ms 里 CompTick 占 38.9ms，
    /// 而 75 并行 × 7.5 倍速 ⇒ 每 tick 完成 20~40 件 ⇒ 10~20ms/tick）。</para>
    ///
    /// <para><b>为什么"摊平"不够</b>：卡顿是**吞吐撞上单件成本**，不是分布不匀 ——
    /// 把同样多的事件挪到别的 tick，每秒的总 CPU 不变。所以只能压"每 tick 落地多少件"，
    /// 让帧时间变成常量，代价是扫图变慢（这是玩家自己的取舍，见设置项）。</para>
    ///
    /// <para><b>0 = 无限制</b>（回到旧行为：能多快就多快，帧率随吞吐恶化）。</para>
    ///
    /// <para>调用点一律是"本来这一 tick 就要收尾"的那一瞬；拿不到票就 <c>return</c>，
    /// **不改动任何进度/血量状态**，下一 tick 原样重试 ⇒ 不会丢活、不会重复产物。</para>
    /// </summary>
    internal static class DigitalWorkBudget
    {
        private static int tick = -1;

        private static int left;

        /// <summary>这一 tick 还能不能收尾一件活（能则扣掉一张票）。</summary>
        public static bool AllowCompletion()
        {
            int cap = DigitalStorageSettings.workerCompletionsPerTick;
            if (cap <= 0) return true;      // 无限制：连 TickManager 都不碰

            int now = GenTicks.TicksGame;
            if (now != tick)
            {
                tick = now;
                left = cap;
            }
            if (left <= 0) return false;
            left--;
            return true;
        }
    }
}
