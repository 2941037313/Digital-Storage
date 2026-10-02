using DigitalStorage.UI;
using RimWorld;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// <b>底部菜单栏那颗按钮</b>（用户要求："不仅可以从制作代理的 ITab 打开，底部菜单栏也可以打开"）。
    ///
    /// <para>底部栏由 <c>MainButtonsRoot</c> 按 <c>MainButtonDef.order</c> 绘制，按钮行为全在
    /// <see cref="MainButtonWorker"/> 里：<c>Activate()</c> 是点击动作，<c>Disabled</c>/<c>Visible</c>
    /// 由基类按"有没有地图"等条件算（我们不用管）。
    /// Def 见 <c>Defs/MainButtons_Craft.xml</c>（order 95：夹在原版 Factions 90 与 Menu 500 之间）。</para>
    ///
    /// <para>不做成原版那种 <c>MainTabWindow</c>：那是"底部栏 + 全屏标签页"的一套配套机制，
    /// 而我们要的是一个**三栏大窗口**（可拖动、可缩放、能同时看材料与订单），
    /// 所以直接开 <see cref="Window_CraftAutomation"/>。</para>
    /// </summary>
    public class MainButtonWorker_CraftAutomation : MainButtonWorker
    {
        public override void Activate()
        {
            Window_CraftAutomation.OpenOrFocus(null);
        }
    }
}
