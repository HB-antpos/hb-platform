using System.Xml.Linq;
using Hbpos.Client.Wpf;

namespace Hbpos.Client.Tests;

/// <summary>
/// 收银员登录弹窗「刚输入的内容短暂明文显示」：
/// 扫码登录是核心路径，明文层必须是不抢焦点的只读叠层，且在弹窗开关/登录后一律复位，明文不得写日志。
/// MainWindow 无法在测试里直接构造，这里用源码/XAML 契约守住关键约束。
/// </summary>
public sealed class CashierLoginInputRevealTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Reveal_duration_is_short_but_long_enough_to_read()
    {
        Assert.InRange(MainWindow.CashierLoginRevealDuration, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Reveal_text_is_a_non_focusable_non_hit_testable_overlay_in_the_input_cell()
    {
        var document = XDocument.Load(Path.Combine(RepoPath("MainWindow.xaml")));
        var passwordBox = Assert.Single(document.Descendants(Presentation + "PasswordBox"), element =>
            (string?)element.Attribute(Xaml + "Name") == "CashierLoginOverlayPasswordBox");
        var reveal = Assert.Single(document.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute(Xaml + "Name") == "CashierLoginOverlayRevealText");

        // 与输入框同一格叠放，不改变弹窗布局。
        Assert.Equal((string?)passwordBox.Attribute("Grid.Row"), (string?)reveal.Attribute("Grid.Row"));
        // 默认收起；不可聚焦、不参与命中测试，点击/触摸全部落到下面的 PasswordBox，焦点永远只在 PasswordBox 上。
        Assert.Equal("Collapsed", (string?)reveal.Attribute("Visibility"));
        Assert.Equal("False", (string?)reveal.Attribute("Focusable"));
        Assert.Equal("False", (string?)reveal.Attribute("IsHitTestVisible"));
        // 不绑定任何数据源：明文只来自 PasswordBox 本身，不会经 ViewModel 流向客显屏等其他界面。
        Assert.DoesNotContain(reveal.Attributes(), attribute =>
            attribute.Value.Contains("{Binding", StringComparison.Ordinal));
    }

    [Fact]
    public void Password_changed_refreshes_the_reveal_for_every_input_source()
    {
        var method = ExtractMethod(
            "private void CashierLoginOverlayPasswordBoxPasswordChanged(",
            "// 收银员条码输入后明文闪显的时长");

        // 物理键盘、扫码枪、屏幕键盘整串赋值与 Clear() 都触发 PasswordChanged，只在这一处刷新。
        Assert.Contains("UpdateCashierLoginInputReveal(passwordBox.Password)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Reveal_is_hidden_when_overlay_visibility_changes_and_after_login()
    {
        var visibleChanged = ExtractMethod(
            "private void CashierLoginOverlayIsVisibleChanged(",
            "private void MainWindowActivated(");
        var clearAfterLogin = ExtractMethod(
            "private void ClearCashierLoginOverlayInputAfterLogin(",
            "private void CashierLoginOverlayIsVisibleChanged(");

        // 输入框本就为空时 Clear() 不触发 PasswordChanged，所以弹窗开关必须显式收回，不能留给下一位收银员。
        Assert.Contains("HideCashierLoginInputReveal();", visibleChanged, StringComparison.Ordinal);
        // 登录后立即收回，不等 Background 优先级的 Clear。
        Assert.Contains("HideCashierLoginInputReveal();", clearAfterLogin, StringComparison.Ordinal);
    }

    [Fact]
    public void Hide_restores_theme_values_and_reveal_never_logs_or_steals_focus()
    {
        var reveal = ExtractMethod(
            "private void UpdateCashierLoginInputReveal(",
            "private void CashierLoginOverlayKeyboardButtonClick(");

        // 恢复用 ClearValue，回到主题/样式原值而不是写死颜色。
        Assert.Contains("ClearValue(Control.ForegroundProperty)", reveal, StringComparison.Ordinal);
        Assert.Contains("ClearValue(PasswordBox.CaretBrushProperty)", reveal, StringComparison.Ordinal);
        // 明文闪显的代码不得写日志，也不得动焦点（扫码焦点是核心路径）。
        Assert.DoesNotContain("ConsoleLog", reveal, StringComparison.Ordinal);
        Assert.DoesNotContain(".Focus()", reveal, StringComparison.Ordinal);
        Assert.DoesNotContain("Keyboard.Focus", reveal, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string startMarker, string endMarker)
    {
        var source = File.ReadAllText(RepoPath("MainWindow.xaml.cs"));
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"找不到 {endMarker}");
        return source[start..end];
    }

    private static string RepoPath(string fileName)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                var path = Path.Combine(current.FullName, "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", fileName);
                if (File.Exists(path))
                {
                    return path;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException($"Unable to locate {fileName}.");
    }
}
