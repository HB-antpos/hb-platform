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
/// 设置页「无交易开钱箱需要再次扫码」（默认关闭）。
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
    public void Settings_page_has_the_toggle_bound_to_the_view_model_and_both_languages_have_the_strings()
    {
        var wpfRoot = WpfRoot();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var view = XDocument.Load(Path.Combine(wpfRoot, "Views", "Screens", "SettingsView.xaml"));
        var toggle = Assert.Single(view.Descendants(presentation + "ToggleButton").Where(element =>
            (string?)element.Attribute(x + "Name") == "NoSaleCashDrawerRescanToggle"));
        Assert.Equal("{Binding ReceiptNoSaleCashDrawerRequiresRescan, Mode=TwoWay}", (string?)toggle.Attribute("IsChecked"));
        Assert.Equal(
            "{loc:Loc settings.receiptPrinter.noSaleDrawerRescan}",
            (string?)toggle.Attribute("AutomationProperties.Name"));
        // 与银行收据开关同在「打印与钱箱选项」卡片里，随小票打印机设置一起点保存生效。
        var bankReceiptToggle = Assert.Single(view.Descendants(presentation + "ToggleButton").Where(element =>
            (string?)element.Attribute(x + "Name") == "PrintBankReceiptTextToggle"));
        var card = bankReceiptToggle.Ancestors(presentation + "Border").First(border =>
            (string?)border.Attribute("Style") == "{StaticResource SettingsActionCardStyle}");
        Assert.Contains(card, toggle.Ancestors(presentation + "Border"));

        foreach (var (file, keys) in new[]
                 {
                     ("SettingsStrings.resx", new[] { "settings.receiptPrinter.noSaleDrawerRescan", "settings.receiptPrinter.noSaleDrawerRescan.help" }),
                     ("SettingsStrings.zh-CN.resx", new[] { "settings.receiptPrinter.noSaleDrawerRescan", "settings.receiptPrinter.noSaleDrawerRescan.help" }),
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

    /// <summary>
    /// 记录每次授权请求及其是否要求「再次扫码」的授权替身：approve = true 恒批准；false 模拟没扫码确认（返回 null）。
    /// 旧方法与带 requireFreshScan 的新重载都实现，才能区分调用方走的是哪一个。
    /// </summary>
    private sealed class RescanAwareAuthorizationService(bool approve) : IOperationAuthorizationService
    {
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public event EventHandler? StatusChanged
        {
            add { }
            remove { }
        }

        public string ScannerPageId => "NoSaleCashDrawerRescanTest";

        public bool IsPromptOpen => false;

        public bool IsBusy => false;

        public string PromptMessage => string.Empty;

        public string StatusMessage => string.Empty;

        public string PermissionCode => string.Empty;

        public string Screen => string.Empty;

        public string Action => string.Empty;

        public IRelayCommand CancelCommand { get; } = new RelayCommand(() => { });

        public List<(string Permission, bool RequireFreshScan)> Requests { get; } = [];

        public Task<OperationAuthorizationScope?> AuthorizeAsync(
            string permissionCode,
            string screen,
            string action,
            PosSessionState session,
            CancellationToken cancellationToken = default) =>
            Record(permissionCode, screen, action, session, requireFreshScan: false);

        public Task<OperationAuthorizationScope?> AuthorizeAsync(
            string permissionCode,
            string screen,
            string action,
            PosSessionState session,
            bool requireFreshScan,
            CancellationToken cancellationToken = default) =>
            Record(permissionCode, screen, action, session, requireFreshScan);

        private Task<OperationAuthorizationScope?> Record(
            string permissionCode,
            string screen,
            string action,
            PosSessionState session,
            bool requireFreshScan)
        {
            Requests.Add((permissionCode, requireFreshScan));
            if (!approve)
            {
                return Task.FromResult<OperationAuthorizationScope?>(null);
            }

            var requestingSession = new CashierSessionDto(
                session.CashierId,
                session.CashierId,
                session.CashierName,
                session.StoreCode,
                session.DeviceCode,
                [],
                [],
                [session.StoreCode],
                false,
                false,
                false);
            return Task.FromResult<OperationAuthorizationScope?>(
                new OperationAuthorizationScope(requestingSession, permissionCode, screen, action));
        }

        public bool ProcessScannerBarcode(string barcode) => false;

        public void Cancel()
        {
        }

        public void RevokeAll()
        {
        }
    }
}
