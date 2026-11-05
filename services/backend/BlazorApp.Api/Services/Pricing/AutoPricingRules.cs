namespace BlazorApp.Api.Services.Pricing
{
    /// <summary>
    /// 自动定价的前置约束。各入口（进货单检测/编辑/执行、商品维护等）在调用定价计算前按行判断，
    /// 不满足的行给出行级提示；不能把判断留给底层抛异常，否则一行脏价格会让整批操作失败且界面只看到「失败」。
    /// </summary>
    public static class AutoPricingRules
    {
        /// <summary>自动定价支持的最低进货价：低于它无法同时满足合法尾数与 1.5～5 成率。</summary>
        public const decimal MinPurchasePrice = 0.1m;

        /// <summary>面向用户的行级提示，所有入口统一用这一句。</summary>
        public const string BelowMinimumHint = "进货价低于 0.10，无法自动定价，请核对进货价或关闭自动定价后手填零售价";

        public static bool SupportsAutoPricing(decimal? purchasePrice)
            => purchasePrice.HasValue && purchasePrice.Value >= MinPurchasePrice;
    }
}
