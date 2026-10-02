namespace DigitalStorage.AI
{
    /// <summary>
    /// 制作自动化的一次"产物生成"作用域。
    ///
    /// <para><b>为什么需要它</b>：<c>GenRecipe.PostProcessProduct</c> 里会对
    /// 大师 / 传奇产物调 <c>QualityUtility.SendCraftNotification</c>（<c>GenRecipe.cs:100</c>），
    /// 那是**信件**（屏幕中央弹一个带跳转的窗口）。真人手工出一件传奇是惊喜，制作代理
    /// 满速量产就是灾难。用户拍板：自动化产物不发信，改成左上角一条消息（含品质名）。
    ///
    /// <para>作用域要窄（只包产物生成那一段），且必须 try/finally；同步单线程无重入
    /// （<c>MakeRecipeProducts</c> 不会自己再进来），所以一个静态 bool 就够 ——
    /// 与 <c>Core/DigitalDropRedirect</c> 同一套形态。</para>
    /// </summary>
    internal static class BillAutomationScope
    {
        /// <summary>true = 当前正在由代理生成产物 ⇒ <c>SendCraftNotification</c> 直接跳过。</summary>
        public static bool SuppressCraftLetters;
    }
}
