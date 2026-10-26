using BlazorApp.Shared.Helper;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 折扣率统一口径：标准小数 [0, 0.9999]；百分数换算；1 / ≥100 / 负数视为脏值（置空）。
/// 1002 店 10-24/25 的整行 -100% 就来自写成 1 / 100 的折扣率。
/// </summary>
public sealed class StoreDiscountRateTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("0", "0")]
    [InlineData("0.2", "0.2")]
    [InlineData("0.9999", "0.9999")]
    [InlineData("0.99996", "0.9999")]   // 四舍五入会到 1.0000，必须封顶
    [InlineData("50", "0.5")]           // 总部同步带来的百分数
    [InlineData("66.56", "0.6656")]
    [InlineData("99.99", "0.9999")]
    [InlineData("99.999", "0.9999")]    // 换算后会到 1.0000，必须封顶
    public void Normalize_保留合法值并换算百分数(string? input, string? expected)
    {
        var actual = StoreDiscountRate.Normalize(input is null ? null : decimal.Parse(input));
        Assert.Equal(expected is null ? null : decimal.Parse(expected), actual);
    }

    [Theory]
    [InlineData("1")]        // 无法区分 1% 还是 100%
    [InlineData("100")]
    [InlineData("101")]
    [InlineData("250000")]
    [InlineData("-0.38")]
    [InlineData("-250000")]
    public void Normalize_脏值一律置空(string input)
    {
        Assert.Null(StoreDiscountRate.Normalize(decimal.Parse(input)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.2")]
    [InlineData("0.9999")]
    public void RequireValid_接受标准小数(string input)
    {
        var value = decimal.Parse(input);
        Assert.Equal(value, StoreDiscountRate.RequireValid(value));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("50")]
    [InlineData("100")]
    [InlineData("-0.1")]
    public void RequireValid_拒绝百分数与越界值(string input)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StoreDiscountRate.RequireValid(decimal.Parse(input)));
    }
}
