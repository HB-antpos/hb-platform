using System.Globalization;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 日结金额的统一格式：小票和日结页面共用，保证同一个差额在两处的符号写法完全一致。
/// 页面以前用 XAML 的 <c>{0:C2}</c>，由 WPF 绑定语言（DailyCloseView 没有设置，默认 en-US）和系统区域设置决定负数怎么写
/// （可能是 <c>-$54.83</c>，也可能是 <c>($54.83)</c>），与小票的 <c>-$54.83</c> 不一致，短款容易被读成正数。
/// 这里固定用 InvariantCulture 和显式的 + / - 号，不受界面语言和系统区域影响。
/// </summary>
public static class DailyCloseMoneyFormat
{
    /// <summary>不带符号的金额，如 <c>$54.83</c>（调用方传入非负数；负数会带 - 号）。</summary>
    public static string Money(decimal amount) =>
        string.Create(CultureInfo.InvariantCulture, $"${amount:0.00}");

    /// <summary>带显式正负号的金额：大于等于 0 写 <c>+$x</c>，小于 0 写 <c>-$x</c>；0 写 <c>+$0.00</c>。</summary>
    public static string SignedMoney(decimal amount) =>
        (amount >= 0m ? "+" : "-") + Money(Math.Abs(amount));
}
