using RimWorld;
using Verse;

namespace DigitalStorage.Services
{
    /// <summary>
    /// 4.0 更新信封：每个存档（含新开档）弹一次，沿用 3.0 的形态 ——
    /// <c>GameComponent.FinalizeInit</c> + 一个被 Scribe 的布尔标记。
    ///
    /// <para><b>为什么沿用 <c>DigitalStorage.Services.DigitalStorageGameComponent</c> 这个类名</b>：
    /// 3.0 的存档里有这个组件的条目（旧字段 <c>globalCores</c> / <c>shown30Letter</c>）。
    /// 同名重建 ⇒ 读档时组件能解析、旧字段被安静忽略（RimWorld 不报"未知组件"），
    /// 而且 <c>shown30Letter</c> 与这里的 <c>shown40Letter</c> 是**不同的 Scribe 键**
    /// ⇒ 从 3.0 升上来的玩家**照样会看到这封信**（这正是我们要的）。</para>
    ///
    /// <para>信本身用 <c>LetterDefOf.NeutralEvent</c>，不需要自定义 LetterDef。</para>
    /// </summary>
    public class DigitalStorageGameComponent : GameComponent
    {
        private bool shown40Letter;

        public DigitalStorageGameComponent(Game game) { }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            if (shown40Letter) return;

            shown40Letter = true;
            Find.LetterStack.ReceiveLetter(
                "DigitalStorage_Letter40_Label".Translate(),
                "DigitalStorage_Letter40_Text".Translate(),
                LetterDefOf.NeutralEvent);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            // 键名与字段名保持一致：Scribe 的键一经写入就固定，改名就会"读不到 ⇒ 每档重弹"
            // （3.0 那个 F1 注释记的就是这个坑）。
            Scribe_Values.Look(ref shown40Letter, "shown40Letter", false);
        }
    }
}
