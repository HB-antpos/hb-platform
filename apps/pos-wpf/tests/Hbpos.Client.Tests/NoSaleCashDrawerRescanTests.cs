using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Cashiers;

namespace Hbpos.Client.Tests;

/// <summary>
/// 设置页「无交易开钱箱需要再次扫码」（默认关闭）。同一张「再次扫码确认」卡片里的退货开关见 ReturnsRescanTests。
///
/// 规则：
/// 1. 只管侧栏「开钱箱」按钮（没有交易时手动开）；收现金后的自动开钱箱走 MainViewModel 的另一条路径，不受影响。
/// 2. 关闭（默认）：和原来一样，当前收银员有权限就直接开。
/// 3. 开启：即使当前收银员自己有开钱箱权限，也要先扫一次授权员工条码确认；没通过就不开。
/// 4. 每次点按钮都现读设置，改设置后立即生效；读取设置失败时按「需要」处理（安全类开关，宁可多扫一次）。
/// </summary>
public sealed class NoSaleCashDrawerRescanTests
{
    private static PosSessionState Session => new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    // ===================================================================================
    // 一、收银页：开钱箱按钮
    // ===================================================================================

    [Fact]
    public async Task Setting_off_requests_the_normal_authorization_and_opens_the_drawer()
    {
        using var harness = new Harness(readSetting: _ => Task.FromResult(false));

        await harness.OpenAsync();

        var request = Assert.Single(harness.Authorization.Requests);
        Assert.Equal(Permissions.PosTerminal.CashDrawer.Open, request.Permission);
        Assert.False(request.RequireFreshScan);
        Assert.Equal(1, harness.OpenCount);
        Assert.Equal(OperationAuditTypes.CashDrawerOpen, Assert.Single(harness.Audit.Events).OperationType);
    }

    [Fact]
    public async Task Setting_on_requests_a_fresh_scan_and_opens_after_it_is_approved()
    {
        using var harness = new Harness(readSetting: _ => Task.FromResult(true));

        await harness.OpenAsync();

        var request = Assert.Single(harness.Authorization.Requests);
        Assert.Equal(Permissions.PosTerminal.CashDrawer.Open, request.Permission);
        Assert.True(request.RequireFreshScan);
        Assert.Equal(1, harness.OpenCount);
        Assert.Equal(OperationAuditTypes.CashDrawerOpen, Assert.Single(harness.Audit.Events).OperationType);
    }

    [Fact]
    public async Task Setting_on_and_scan_not_confirmed_does_not_open_the_drawer()
    {
        using var harness = new Harness(readSetting: _ => Task.FromResult(true), approve: false);

        await harness.OpenAsync();

        Assert.True(Assert.Single(harness.Authorization.Requests).RequireFreshScan);
        Assert.Equal(0, harness.OpenCount);
        Assert.Empty(harness.Audit.Events);
    }

    [Fact]
    public async Task Without_a_setting_source_it_behaves_as_before()
    {
        using var harness = new Harness(readSetting: null);

        await harness.OpenAsync();

        Assert.False(Assert.Single(harness.Authorization.Requests).RequireFreshScan);
        Assert.Equal(1, harness.OpenCount);
    }

    [Fact]
    public async Task Failing_to_read_the_setting_requires_a_fresh_scan()
    {
        using var harness = new Harness(
            readSetting: _ => throw new InvalidOperationException("settings store unavailable"));

        await harness.OpenAsync();

        // 安全类开关读不到：不知道配置，按「需要」处理；这次点击没有因为异常而中断。
        Assert.True(Assert.Single(harness.Authorization.Requests).RequireFreshScan);
        Assert.Equal(1, harness.OpenCount);
    }

    [Fact]
    public async Task Changing_the_setting_takes_effect_on_the_next_press_without_restart()
    {
        var enabled = false;
        using var harness = new Harness(readSetting: _ => Task.FromResult(enabled));

        await harness.OpenAsync();
        enabled = true;
        await harness.OpenAsync();
        enabled = false;
        await harness.OpenAsync();

        Assert.Equal(
            [false, true, false],
            harness.Authorization.Requests.Select(request => request.RequireFreshScan));
        Assert.Equal(3, harness.OpenCount);
    }

    // ===================================================================================
    // 二、设置页：开关的界面与文案
    // ===================================================================================

    [Fact]
    public void Settings_page_has_both_rescan_toggles_in_their_own_card_and_both_languages_have_the_strings()
    {
        var wpfRoot = WpfRoot();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var view = XDocument.Load(Path.Combine(wpfRoot, "Views", "Screens", "SettingsView.xaml"));
        XElement Toggle(string name) => Assert.Single(view.Descendants(presentation + "ToggleButton").Where(element =>
            (string?)element.Attribute(x + "Name") == name));

        var cashDrawerToggle = Toggle("NoSaleCashDrawerRescanToggle");
        Assert.Equal("{Binding ReceiptNoSaleCashDrawerRequiresRescan, Mode=TwoWay}", (string?)cashDrawerToggle.Attribute("IsChecked"));
        Assert.Equal(
            "{loc:Loc settings.receiptPrinter.noSaleDrawerRescan}",
            (string?)cashDrawerToggle.Attribute("AutomationProperties.Name"));
        var returnsToggle = Toggle("ReturnsRescanToggle");
        Assert.Equal("{Binding ReceiptReturnsRequireRescan, Mode=TwoWay}", (string?)returnsToggle.Attribute("IsChecked"));
        Assert.Equal(
            "{loc:Loc settings.receiptPrinter.returnsRescan}",
            (string?)returnsToggle.Attribute("AutomationProperties.Name"));

        // 两个「再次扫码」开关同在「再次扫码确认」卡片里；银行收据开关在另一张「打印选项」卡片里。
        // 两张卡片放在小票打印机页第 1 行的同一个 StackPanel 里上下排列，后面的卡片不用重新编号。
        XElement Card(XElement toggle) => toggle.Ancestors(presentation + "Border").First(border =>
            (string?)border.Attribute("Style") == "{StaticResource SettingsActionCardStyle}");
        var printCard = Card(Toggle("PrintBankReceiptTextToggle"));
        var rescanCard = Card(cashDrawerToggle);
        Assert.NotSame(printCard, rescanCard);
        Assert.Same(rescanCard, Card(returnsToggle));
        Assert.Same(printCard.Parent, rescanCard.Parent);
        Assert.Equal("1", (string?)printCard.Parent!.Attribute("Grid.Row"));

        var rescanKeys = new[]
        {
            "settings.receiptPrinter.rescan.title",
            "settings.receiptPrinter.rescan.help",
            "settings.receiptPrinter.noSaleDrawerRescan",
            "settings.receiptPrinter.noSaleDrawerRescan.help",
            "settings.receiptPrinter.returnsRescan",
            "settings.receiptPrinter.returnsRescan.help"
        };
        foreach (var (file, keys) in new[]
                 {
                     ("SettingsStrings.resx", rescanKeys),
                     ("SettingsStrings.zh-CN.resx", rescanKeys),
                     ("Strings.resx", new[] { "operationAuthorization.rescanPrompt" }),
                     ("Strings.zh-CN.resx", new[] { "operationAuthorization.rescanPrompt" })
                 })
        {
            var resources = XDocument.Load(Path.Combine(wpfRoot, "Resources", file));
            foreach (var key in keys)
            {
                var data = Assert.Single(resources.Descendants("data").Where(element => (string?)element.Attribute("name") == key));
                Assert.False(string.IsNullOrWhiteSpace((string?)data.Element("value")), $"{file} 的 {key} 为空。");
            }
        }
    }

    // ===================================================================================
    // 三、操作授权弹窗：再次扫码时不能说「没有权限」
    // ===================================================================================

    [Fact]
    public void Authorization_dialog_swaps_no_permission_texts_for_rescan_texts_in_rescan_mode()
    {
        var wpfRoot = WpfRoot();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var window = XDocument.Load(Path.Combine(wpfRoot, "MainWindow.xaml"));
        XElement Text(string name) => Assert.Single(window.Descendants(presentation + "TextBlock").Where(element =>
            (string?)element.Attribute(x + "Name") == name));

        // 「没有权限」那一组：再次扫码模式下隐藏（触发器置 Collapsed）。
        foreach (var name in new[]
                 {
                     "OperationAuthorizationMessage",
                     "OperationAuthorizationMissingPermission",
                     "OperationAuthorizationScanHint"
                 })
        {
            var block = Text(name);
            Assert.Null(block.Attribute("Visibility"));
            AssertTriggerSets(block, "Collapsed");
        }

        // 「再次扫码」那一组：默认隐藏必须写在 Style 里（元素上的本地值会压过触发器），再次扫码模式下显示。
        foreach (var name in new[]
                 {
                     "OperationAuthorizationRescanMessage",
                     "OperationAuthorizationRequiredPermission",
                     "OperationAuthorizationRescanScanHint"
                 })
        {
            var block = Text(name);
            Assert.Null(block.Attribute("Visibility"));
            var style = Assert.Single(block.Descendants(presentation + "Style"));
            Assert.Single(style.Elements(presentation + "Setter").Where(setter =>
                (string?)setter.Attribute("Property") == "Visibility" && (string?)setter.Attribute("Value") == "Collapsed"));
            AssertTriggerSets(block, "Visible");
        }

        // 缺少权限的权限码行在两种模式下都要显示权限码。
        Assert.Contains(
            Text("OperationAuthorizationRequiredPermission").Descendants(presentation + "Run"),
            run => (string?)run.Attribute("Text") == "{Binding OperationAuthorization.PermissionCode, Mode=OneWay}");

        var keys = new[]
        {
            "shell.operationAuthorization.rescanMessage",
            "shell.operationAuthorization.rescanPermissionLabel",
            "shell.operationAuthorization.rescanScanHint"
        };
        foreach (var file in new[] { "Strings.resx", "Strings.zh-CN.resx" })
        {
            var resources = XDocument.Load(Path.Combine(wpfRoot, "Resources", file));
            foreach (var key in keys)
            {
                var data = Assert.Single(resources.Descendants("data").Where(element => (string?)element.Attribute("name") == key));
                Assert.False(string.IsNullOrWhiteSpace((string?)data.Element("value")), $"{file} 的 {key} 为空。");
            }
        }

        void AssertTriggerSets(XElement block, string visibility)
        {
            var trigger = Assert.Single(block.Descendants(presentation + "DataTrigger"));
            Assert.Equal("{Binding OperationAuthorization.IsRescanConfirmation}", (string?)trigger.Attribute("Binding"));
            Assert.Equal("True", (string?)trigger.Attribute("Value"));
            Assert.Single(trigger.Elements(presentation + "Setter").Where(setter =>
                (string?)setter.Attribute("Property") == "Visibility" && (string?)setter.Attribute("Value") == visibility));
        }
    }

    // ===================================================================================
    // 辅助
    // ===================================================================================

    private static string WpfRoot([CallerFilePath] string testFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..", "src", "Hbpos.Client.Wpf"));

    private sealed class Harness : IDisposable
    {
        public Harness(Func<CancellationToken, Task<bool>>? readSetting, bool approve = true)
        {
            Authorization = new RescanAwareAuthorizationService(approve);
            Audit = new RecordingOperationAuditLogger();
            ViewModel = new PosTerminalViewModel(
                new LocalSellableItemIndex(),
                new PosCartService(),
                Session,
                onOpenPayment: null,
                onOpenCashDrawerAsync: () =>
                {
                    OpenCount++;
                    return Task.FromResult(new ReceiptPrintResult(true, string.Empty));
                },
                operationAuditLogger: Audit,
                operationAuthorizationService: Authorization,
                isNoSaleCashDrawerRescanRequiredAsync: readSetting);
        }

        public RescanAwareAuthorizationService Authorization { get; }

        public RecordingOperationAuditLogger Audit { get; }

        public PosTerminalViewModel ViewModel { get; }

        public int OpenCount { get; private set; }

        public Task OpenAsync() => ((IAsyncRelayCommand)ViewModel.OpenCashDrawerCommand).ExecuteAsync(null);

        public void Dispose()
        {
            ViewModel.Dispose();
        }
    }

    private sealed class RecordingOperationAuditLogger : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent)
        {
            Events.Add(auditEvent);
        }
    }
}
