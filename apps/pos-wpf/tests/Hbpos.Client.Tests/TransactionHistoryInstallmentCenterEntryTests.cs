using System.ComponentModel;
using System.Xml.Linq;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Cashiers;

namespace Hbpos.Client.Tests;

/// <summary>
/// 历史页「在线」来源里的「分期中心」入口：命令只在在线来源可用、按「查看分期」权限放行、
/// 放行后只调用导航器注入的回调（历史页本身不依赖分期中心视图模型），以及视图里按钮的绑定契约。
/// </summary>
public sealed class TransactionHistoryInstallmentCenterEntryTests
{
    [Fact]
    public void Entry_command_is_available_only_while_the_online_source_is_selected()
    {
        using var viewModel = CreateViewModel(() => { });
        var canExecuteChanged = 0;
        viewModel.OpenInstallmentCenterCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        // 默认是「本机」来源：不可用。
        Assert.True(viewModel.IsLocalSourceSelected);
        Assert.False(viewModel.OpenInstallmentCenterCommand.CanExecute(null));

        viewModel.IsOnlineSourceSelected = true;

        Assert.True(viewModel.OpenInstallmentCenterCommand.CanExecute(null));
        Assert.True(canExecuteChanged > 0, "切到在线来源后应通知命令可用性变化，按钮才会及时启用。");

        // 分期来源有自己的订单列表与操作，不是分期中心入口所在的来源。
        viewModel.IsInstallmentSourceSelected = true;
        Assert.False(viewModel.OpenInstallmentCenterCommand.CanExecute(null));

        viewModel.IsOnlineSourceSelected = true;
        Assert.True(viewModel.OpenInstallmentCenterCommand.CanExecute(null));

        viewModel.IsLocalSourceSelected = true;
        Assert.False(viewModel.OpenInstallmentCenterCommand.CanExecute(null));
    }

    [Fact]
    public async Task Entry_command_invokes_the_navigation_callback_once()
    {
        var opened = 0;
        // 没有授权服务、没有强制权限（单机未登录收银员）：按原有门禁直接放行。
        using var viewModel = CreateViewModel(() => opened++);
        viewModel.IsOnlineSourceSelected = true;

        await viewModel.OpenInstallmentCenterCommand.ExecuteAsync(null);

        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task Entry_command_does_nothing_when_executed_outside_the_online_source()
    {
        var opened = 0;
        using var viewModel = CreateViewModel(() => opened++);

        // ExecuteAsync 不检查 CanExecute（例如绑定残留时被直接调用）：方法体自己也必须挡住非在线来源。
        Assert.False(viewModel.OpenInstallmentCenterCommand.CanExecute(null));
        await viewModel.OpenInstallmentCenterCommand.ExecuteAsync(null);

        Assert.Equal(0, opened);
    }

    [Fact]
    public void Entry_command_is_disabled_without_a_navigation_callback_or_after_disposal()
    {
        using var withoutCallback = CreateViewModel(openInstallmentCenter: null);
        withoutCallback.IsOnlineSourceSelected = true;
        Assert.False(withoutCallback.OpenInstallmentCenterCommand.CanExecute(null));

        var disposed = CreateViewModel(() => { });
        disposed.IsOnlineSourceSelected = true;
        Assert.True(disposed.OpenInstallmentCenterCommand.CanExecute(null));

        disposed.Dispose();

        Assert.False(disposed.OpenInstallmentCenterCommand.CanExecute(null));
    }

    [Fact]
    public async Task Entry_requires_the_view_installments_permission()
    {
        var opened = 0;
        // 收银员只有「历史取单」权限，没有「查看分期」权限；强制权限校验开启。
        using var viewModel = CreateViewModel(
            () => opened++,
            session: Session(Cashier(Permissions.PosTerminal.History.Recall)),
            enforcePermissions: true);
        viewModel.IsOnlineSourceSelected = true;

        await viewModel.OpenInstallmentCenterCommand.ExecuteAsync(null);

        Assert.Equal(0, opened);
        // 被拒绝时与历史页其它操作一样，在状态栏写出原因。
        Assert.False(string.IsNullOrWhiteSpace(viewModel.StatusMessage));
    }

    [Fact]
    public async Task Entry_is_denied_when_permissions_are_enforced_and_nobody_is_signed_in()
    {
        var opened = 0;
        using var viewModel = CreateViewModel(() => opened++, enforcePermissions: true);
        viewModel.IsOnlineSourceSelected = true;

        await viewModel.OpenInstallmentCenterCommand.ExecuteAsync(null);

        Assert.Equal(0, opened);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.StatusMessage));
    }

    [Fact]
    public async Task Entry_is_allowed_for_a_cashier_with_the_view_installments_permission()
    {
        var opened = 0;
        using var viewModel = CreateViewModel(
            () => opened++,
            session: Session(Cashier(Permissions.PosTerminal.Installments.View)),
            enforcePermissions: true);
        viewModel.IsOnlineSourceSelected = true;

        await viewModel.OpenInstallmentCenterCommand.ExecuteAsync(null);

        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task Entry_goes_through_the_operation_authorization_service_for_the_view_permission()
    {
        var opened = 0;
        var authorization = new RecordingAuthorizationService();
        using var viewModel = CreateViewModel(
            () => opened++,
            operationAuthorizationService: authorization);
        viewModel.IsOnlineSourceSelected = true;

        await viewModel.OpenInstallmentCenterCommand.ExecuteAsync(null);

        // 与历史页其它操作、以及分期中心原有的付款页入口一致：权限码是「查看分期」，
        // 配置了授权服务时可由有权限的员工扫码放行。
        var request = Assert.Single(authorization.Requests);
        Assert.Equal(Permissions.PosTerminal.Installments.View, request.Permission);
        Assert.Equal("transaction-history", request.Screen);
        Assert.Equal("open-installment-center", request.Action);
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task Entry_does_not_navigate_when_the_authorization_is_rejected()
    {
        var opened = 0;
        var authorization = new RecordingAuthorizationService { Deny = true };
        using var viewModel = CreateViewModel(
            () => opened++,
            operationAuthorizationService: authorization);
        viewModel.IsOnlineSourceSelected = true;

        await viewModel.OpenInstallmentCenterCommand.ExecuteAsync(null);

        Assert.Single(authorization.Requests);
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task Entry_does_not_navigate_when_the_view_model_is_disposed_while_waiting_for_authorization()
    {
        var opened = 0;
        TransactionHistoryViewModel? viewModel = null;
        var authorization = new RecordingAuthorizationService
        {
            // 模拟授权等待期间历史页被释放（例如清屏）：放行后不能再跳转。
            BeforeReturn = () => viewModel!.Dispose()
        };
        viewModel = CreateViewModel(() => opened++, operationAuthorizationService: authorization);
        viewModel.IsOnlineSourceSelected = true;

        await viewModel.OpenInstallmentCenterCommand.ExecuteAsync(null);

        Assert.Equal(0, opened);
    }

    [Fact]
    public void History_view_shows_the_entry_button_only_for_the_online_source_and_binds_existing_members()
    {
        var view = XDocument.Load(Path.Combine(
            FindWpfRoot(),
            "Views",
            "Screens",
            "TransactionHistoryView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var header = Assert.Single(
            view.Descendants(presentation + "Grid"),
            element => (string?)element.Attribute(x + "Name") == "HistoryHeaderRegion");
        var button = Assert.Single(
            header.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Command") == "{Binding OpenInstallmentCenterCommand}");

        // 可见性跟「在线」来源走；视觉沿用页面现有的次级按钮样式。
        Assert.Equal("{Binding IsOnlineSourceSelected, Converter={StaticResource BoolToVis}}", (string?)button.Attribute("Visibility"));
        Assert.Equal("{StaticResource PosSecondaryButtonStyle}", (string?)button.Attribute("Style"));
        Assert.Equal("{loc:Loc installment.center.title}", (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal("TransactionHistoryOpenInstallmentCenter", (string?)button.Attribute("AutomationProperties.AutomationId"));
        // 入口在来源切换条之外，不能混进「本机 / 在线 / 分期 / 挂单」四个来源页签里。
        Assert.DoesNotContain(
            button.Ancestors(presentation + "Border"),
            element => (string?)element.Attribute(x + "Name") == "HistorySourceNavigation");

        // XAML 绑定只在运行时校验：这里把用到的绑定路径与视图模型里的真实成员逐个对上。
        var viewModelType = typeof(TransactionHistoryViewModel);
        Assert.NotNull(viewModelType.GetProperty(nameof(TransactionHistoryViewModel.OpenInstallmentCenterCommand)));
        Assert.NotNull(viewModelType.GetProperty(nameof(TransactionHistoryViewModel.IsOnlineSourceSelected)));
    }

    private static TransactionHistoryViewModel CreateViewModel(
        Action? openInstallmentCenter,
        PosSessionState? session = null,
        bool enforcePermissions = false,
        IOperationAuthorizationService? operationAuthorizationService = null) =>
        new(
            new EmptyReceiptQueryService(),
            null,
            null,
            session ?? Session(),
            enforcePermissionsWhenNoCashier: enforcePermissions,
            operationAuthorizationService: operationAuthorizationService,
            openInstallmentCenter: openInstallmentCenter);

    private static PosSessionState Session(CashierSessionDto? cashier = null) =>
        new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0, cashier);

    private static CashierSessionDto Cashier(params string[] permissions) => new(
        "C001", "user-1", "Alice", "S001", "POS-01", [], permissions, ["S001"], false, false, false);

    private static string FindWpfRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, "apps", "pos-wpf", "src", "Hbpos.Client.Wpf");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Unable to find the Hbpos.Client.Wpf project directory.");
    }

    private sealed class EmptyReceiptQueryService : IReceiptQueryService
    {
        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);

        public Task<ReceiptDetails?> GetReceiptAsync(Guid orderGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReceiptDetails?>(null);

        public Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ReceiptDetails?>(null);
    }

    private sealed class RecordingAuthorizationService : IOperationAuthorizationService
    {
        public List<(string Permission, string Screen, string Action)> Requests { get; } = [];

        public bool Deny { get; init; }

        public Action? BeforeReturn { get; init; }

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

        public string ScannerPageId => "history-installment-center-entry-test";

        public bool IsPromptOpen => false;

        public bool IsBusy => false;

        public string PromptMessage => string.Empty;

        public string StatusMessage => string.Empty;

        public string PermissionCode => string.Empty;

        public string Screen => string.Empty;

        public string Action => string.Empty;

        public IRelayCommand CancelCommand { get; } = new RelayCommand(() => { });

        public Task<OperationAuthorizationScope?> AuthorizeAsync(
            string permissionCode,
            string screen,
            string action,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((permissionCode, screen, action));
            BeforeReturn?.Invoke();
            if (Deny)
            {
                return Task.FromResult<OperationAuthorizationScope?>(null);
            }

            var supervisor = new CashierSessionDto(
                "S100",
                "user-s",
                "Supervisor",
                "S001",
                "POS-01",
                [],
                [permissionCode],
                ["S001"],
                false,
                false,
                false);
            return Task.FromResult<OperationAuthorizationScope?>(
                new OperationAuthorizationScope(supervisor, permissionCode, screen, action));
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
