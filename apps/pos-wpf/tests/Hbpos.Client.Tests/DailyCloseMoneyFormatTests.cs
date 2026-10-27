using System.Globalization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>
/// 日结差额在小票和页面共用同一个格式：显式 + / - 号、固定 $ 与两位小数，不受界面语言和系统区域设置影响。
/// 背景：页面以前用 XAML 的 {0:C2}，负数写法随区域设置变化（-$54.83 或 ($54.83)），与小票的 -$54.83 不一致，短款被读成正数。
/// </summary>
public sealed class DailyCloseMoneyFormatTests
{
    [Theory]
    [InlineData(-54.83, "-$54.83")]
    [InlineData(54.83, "+$54.83")]
    [InlineData(0, "+$0.00")]
    [InlineData(-0.05, "-$0.05")]
    [InlineData(0.1, "+$0.10")]
    [InlineData(-2.61, "-$2.61")]
    [InlineData(1234.5, "+$1234.50")]
    public void Signed_money_always_carries_an_explicit_sign(double amount, string expected)
    {
        Assert.Equal(expected, DailyCloseMoneyFormat.SignedMoney((decimal)amount));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-AU")]
    [InlineData("zh-CN")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public void Signed_money_does_not_change_with_the_current_culture(string cultureName)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

            Assert.Equal("-$54.83", DailyCloseMoneyFormat.SignedMoney(-54.83m));
            Assert.Equal("+$54.83", DailyCloseMoneyFormat.SignedMoney(54.83m));
            Assert.Equal("$141.83", DailyCloseMoneyFormat.Money(141.83m));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Short_and_over_have_opposite_signs_so_a_short_is_never_read_as_positive()
    {
        // 小票上这一天：应有 141.83、实点 87.00 → 短款 54.83。
        var difference = 87.00m - 141.83m;

        Assert.Equal("-$54.83", DailyCloseMoneyFormat.SignedMoney(difference));
        Assert.StartsWith("-", DailyCloseMoneyFormat.SignedMoney(difference), StringComparison.Ordinal);
        Assert.StartsWith("+", DailyCloseMoneyFormat.SignedMoney(-difference), StringComparison.Ordinal);
    }
}
