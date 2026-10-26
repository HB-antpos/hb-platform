namespace BlazorApp.Shared.Helper
{
    /// <summary>
    /// 分店价格 <c>StoreRetailPrice.DiscountRate</c> 的唯一写入口径：「减免比例」小数，范围 [0, 0.9999]。
    /// 0.2 表示减 20%；不允许 100% 及以上（整行免单），也不允许负数。
    /// 历史上字段里混过百分数（50 表示 50%，来自总部同步与批量脚本）和 1 / 100 这类脏值，
    /// 1002 店因此出现过整行 -100%、$0。所有写库前都应经过 <see cref="Normalize"/>。
    /// </summary>
    public static class StoreDiscountRate
    {
        /// <summary>允许写入的最大折扣率（99.99%），保证折后价永远不会被折成 0。</summary>
        public const decimal MaxStoredRate = 0.9999m;

        /// <summary>
        /// 归一化到标准小数口径；无法判断或不合法的值返回 null（按「没有折扣」处理）。
        /// </summary>
        public static decimal? Normalize(decimal? value)
        {
            if (value is not { } rate)
            {
                return null;
            }

            // 负数没有业务含义（曾见 -250000 这类垃圾值）。
            if (rate < 0m)
            {
                return null;
            }

            if (rate == 0m)
            {
                return 0m;
            }

            // 恰好等于 1 无法区分是 1% 还是 100%，两种读法都不该自动生效，统一视为脏值。
            if (rate == 1m || rate >= 100m)
            {
                return null;
            }

            // (1, 100) 是总部同步带来的百分数写法，换算成小数。
            var ratio = rate > 1m ? rate / 100m : rate;
            return Math.Min(MaxStoredRate, Math.Round(ratio, 4, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// 管理端显式改价用：只接受已经是标准小数的值，其余直接拒绝，避免静默把 50 当成 50%。
        /// </summary>
        public static decimal RequireValid(decimal value)
        {
            if (value < 0m || value > MaxStoredRate)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"DiscountRate 必须在 0 到 {MaxStoredRate} 之间（0.2 表示减 20%）"
                );
            }

            return value;
        }
    }
}
