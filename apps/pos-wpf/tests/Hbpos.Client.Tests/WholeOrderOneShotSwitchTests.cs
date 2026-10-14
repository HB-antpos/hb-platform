using System.ComponentModel;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

/// <summary>
/// 收银页「整单操作」是一次性开关：点一次只管下一次折扣。
///
/// 规则：
/// 1. 按下任一折扣键（折扣金额 / 折扣% / 10–50% 快速折扣）的那一刻就用掉这一次，不管后面成功还是被拒绝、
///    主管授权被取消或未通过，开关都回到默认；输入缓冲照旧保留，方便改数后重试。
/// 2. 一单结束或换人时也复位，不能把「整单」带给下一位顾客：清空购物车 / 挂单 / 取单 / 付款完成（购物车变空）、
///    锁屏、换收银员或设备。
/// 3. 用掉之后的审计类型和授权权限码仍按「整单折扣」走（用的是发起时的快照），下一次折扣则是单品折扣。
/// </summary>
public sealed class WholeOrderOneShotSwitchTests
{
    private const string StoreCode = "S001";

    private static PosSessionState Session => new("HB POS", StoreCode, "Main Store", "POS-01", "C001", "Alice", true, 0);

    // ===================================================================================
    // 一、按下折扣键就用掉这一次：成功、被拒绝、授权被取消都一样
    // ===================================================================================

    [Fact]
    public async Task Successful_whole_order_discount_uses_order_permission_and_audit_then_next_discount_asks_the_line_permission()
    {
        using var harness = new Harness();
        harness.ViewModel.SelectedCartLine = harness.Line;

        harness.ViewModel.IsWholeOrderOperation = true;
        await harness.PercentAsync("10");

        Assert.False(harness.ViewModel.IsWholeOrderOperation);
        Assert.Equal([Permissions.PosTerminal.Sales.OrderManualDiscount], harness.Authorization.RequestedPermissions);
        Assert.Equal("CART_ORDER_DISCOUNT_CHANGE", Assert.Single(harness.Audit.Events).OperationType);
        Assert.Equal(3m, harness.Line.DiscountAmount);

        // 不用再关一次：下一次折扣直接是单品折扣，请求的是行级权限（行级审计类型见 Per_line_discount_with_switch_off_leaves_the_switch_off）。
        harness.ViewModel.SelectedCartLine = harness.Line;
        await harness.PercentAsync("20");

        Assert.False(harness.ViewModel.IsWholeOrderOperation);
        Assert.Equal(
            [Permissions.PosTerminal.Sales.OrderManualDiscount, Permissions.PosTerminal.Sales.LineManualDiscount],
            harness.Authorization.RequestedPermissions);
    }

    [Fact]
    public async Task Rejected_amount_too_high_still_uses_up_the_switch_and_keeps_the_keypad_input()
    {
        using var harness = new Harness();
        harness.ViewModel.SelectedCartLine = harness.Line;

        harness.ViewModel.IsWholeOrderOperation = true;
        await harness.AmountAsync("999");

        Assert.False(harness.ViewModel.IsWholeOrderOperation);
        Assert.Equal("pos.status.discountAmountTooHigh", harness.ViewModel.StatusMessage);
        Assert.Equal("999", harness.ViewModel.KeypadBuffer);
        Assert.Equal(0m, harness.Line.DiscountAmount);
        Assert.Empty(harness.Audit.Events);
    }

    [Fact]
    public async Task Rejected_percent_out_of_range_still_uses_up_the_switch()
    {
        using var harness = new Harness();
        harness.ViewModel.SelectedCartLine = harness.Line;

        harness.ViewModel.IsWholeOrderOperation = true;
        await harness.PercentAsync("101");

        Assert.False(harness.ViewModel.IsWholeOrderOperation);
        Assert.Equal("pos.status.discountPercentOutOfRange", harness.ViewModel.StatusMessage);
        Assert.Equal(0m, harness.Line.DiscountAmount);
    }

    [Fact]
    public async Task Unsupported_quick_discount_value_still_uses_up_the_switch()
    {
        using var harness = new Harness();

        harness.ViewModel.IsWholeOrderOperation = true;
        await ((IAsyncRelayCommand<string>)harness.ViewModel.ApplyQuickDiscountPercentCommand).ExecuteAsync("15");

        Assert.False(harness.ViewModel.IsWholeOrderOperation);
        // 档位不在 10–50% 里：不授权、不改购物车，只给错误提示。
        Assert.Empty(harness.Authorization.RequestedPermissions);
        Assert.Equal(0m, harness.Line.DiscountAmount);
    }

    [Fact]
    public async Task Cancelled_or_denied_authorization_still_uses_up_the_switch_and_changes_nothing()
    {
        using var harness = new Harness(approveAuthorization: false);
        harness.ViewModel.SelectedCartLine = harness.Line;

        harness.ViewModel.IsWholeOrderOperation = true;
        await harness.PercentAsync("10");

        // 弹了授权（请求的是整单折扣权限）但没通过：这一次整单开关也已用掉，购物车没有任何变化。
        Assert.Equal([Permissions.PosTerminal.Sales.OrderManualDiscount], harness.Authorization.RequestedPermissions);
        Assert.False(harness.ViewModel.IsWholeOrderOperation);
        Assert.Equal(0m, harness.Line.DiscountAmount);
        Assert.Empty(harness.Audit.Events);
    }

    [Fact]
    public async Task Per_line_discount_with_switch_off_leaves_the_switch_off()
    {
        using var harness = new Harness();
        harness.ViewModel.SelectedCartLine = harness.Line;

        await harness.PercentAsync("10");

        Assert.False(harness.ViewModel.IsWholeOrderOperation);
        Assert.Equal([Permissions.PosTerminal.Sales.LineManualDiscount], harness.Authorization.RequestedPermissions);
        Assert.Equal("CART_LINE_DISCOUNT_CHANGE", Assert.Single(harness.Audit.Events).OperationType);
    }

    [Fact]
    public void Switching_on_does_not_change_anything_until_a_discount_key_is_pressed()
    {
        using var harness = new Harness();

        harness.ViewModel.IsWholeOrderOperation = true;
        harness.ViewModel.KeypadInputCommand.Execute("5");

        Assert.True(harness.ViewModel.IsWholeOrderOperation);
        Assert.Empty(harness.Authorization.RequestedPermissions);
        Assert.Equal(0m, harness.Line.DiscountAmount);
    }

    // ===================================================================================
    // 二、一单结束 / 换人：不能把「整单」带给下一位顾客
    // ===================================================================================

    [Fact]
    public async Task Clear_cart_turns_the_switch_off()
    {
        using var harness = new Harness();
        harness.ViewModel.IsWholeOrderOperation = true;

        await ((IAsyncRelayCommand)harness.ViewModel.ClearCartCommand).ExecuteAsync(null);

        Assert.True(harness.Cart.IsEmpty);
        Assert.False(harness.ViewModel.IsWholeOrderOperation);
    }

    [Fact]
    public void Cart_emptied_elsewhere_such_as_payment_completion_turns_the_switch_off()
    {
        using var harness = new Harness();
        harness.ViewModel.IsWholeOrderOperation = true;

        // 付款完成 / 其它流程清空的是共享购物车，收银页只会收到「购物车变空」事件。
        harness.Cart.Clear();

        Assert.False(harness.ViewModel.IsWholeOrderOperation);
    }

    [Fact]
    public async Task Hold_order_turns_the_switch_off()
    {
        var held = false;
        using var harness = new Harness(onHoldOrderAsync: () =>
        {
            held = true;
            return Task.CompletedTask;
        });
        harness.ViewModel.IsWholeOrderOperation = true;

        await ((IAsyncRelayCommand)harness.ViewModel.HoldOrderCommand).ExecuteAsync(null);

        Assert.True(held);
        Assert.False(harness.ViewModel.IsWholeOrderOperation);
    }

    [Fact]
    public async Task Recall_order_turns_the_switch_off()
    {
        var recalled = false;
        using var harness = new Harness(onRecallOrderAsync: () =>
        {
            recalled = true;
            return Task.CompletedTask;
        });
        harness.ViewModel.IsWholeOrderOperation = true;

        await ((IAsyncRelayCommand)harness.ViewModel.RecallOrderCommand).ExecuteAsync(null);

        Assert.True(recalled);
        Assert.False(harness.ViewModel.IsWholeOrderOperation);
    }

    [Fact]
    public async Task Lock_cashier_turns_the_switch_off_even_if_the_same_cashier_unlocks()
    {
        var locked = false;
        using var harness = new Harness(onLockCashierAsync: () =>
        {
            locked = true;
            return Task.CompletedTask;
        });
        harness.ViewModel.IsWholeOrderOperation = true;

        await ((IAsyncRelayCommand)harness.ViewModel.LockCashierCommand).ExecuteAsync(null);

        Assert.True(locked);
        Assert.False(harness.ViewModel.IsWholeOrderOperation);
    }

    [Fact]
    public void Changing_cashier_turns_the_switch_off_but_a_plain_online_status_change_does_not()
    {
        using var harness = new Harness();
        harness.ViewModel.IsWholeOrderOperation = true;

        // 只是联机状态变化：同一位收银员、同一台设备，开关保持。
        harness.ViewModel.Session = Session with { IsOnline = false };
        Assert.True(harness.ViewModel.IsWholeOrderOperation);

        // 换收银员：新会话不继承上一位的「整单」。
        harness.ViewModel.Session = Session with { CashierId = "C002", CashierName = "Bob" };
        Assert.False(harness.ViewModel.IsWholeOrderOperation);
    }

    // ===================================================================================
    // 辅助
    // ===================================================================================

    private static SellableItemDto CreateItem(decimal price)
    {
        return new SellableItemDto(
            StoreCode: StoreCode,
            ProductCode: "SKU-ONE-SHOT",
            ReferenceCode: null,
            DisplayName: "One Shot Tea",
            LookupCode: "930900",
            ItemNumber: "SKU-ONE-SHOT",
            Barcode: "930900",
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>真实的购物车 / 工作流 / ViewModel，授权服务与审计换成只做记录的替身；购物车里有一行 30 元的商品。</summary>
    private sealed class Harness : IDisposable
    {
        public Harness(
            bool approveAuthorization = true,
            Func<Task>? onHoldOrderAsync = null,
            Func<Task>? onRecallOrderAsync = null,
            Func<Task>? onLockCashierAsync = null)
        {
            Cart = new PosCartService();
            Authorization = new ScriptedAuthorizationService(approveAuthorization);
            Audit = new RecordingOperationAuditLogger();
            ViewModel = new PosTerminalViewModel(
                new LocalSellableItemIndex(),
                Cart,
                Session,
                onOpenPayment: null,
                onHoldOrderAsync: onHoldOrderAsync,
                onRecallOrderAsync: onRecallOrderAsync,
                operationAuditLogger: Audit,
                onLockCashierAsync: onLockCashierAsync,
                operationAuthorizationService: Authorization);

            // ViewModel 先建好再加行（购物车变更事件驱动列表刷新），与既有收银页测试一致。
            Line = Cart.AddItem(CreateItem(30m));
        }

        public PosCartService Cart { get; }

        public ScriptedAuthorizationService Authorization { get; }

        public RecordingOperationAuditLogger Audit { get; }

        public PosTerminalViewModel ViewModel { get; }

        public CartLine Line { get; }

        public Task AmountAsync(string keypad)
        {
            ViewModel.KeypadBuffer = keypad;
            return ((IAsyncRelayCommand)ViewModel.ApplySelectedLineDiscountAmountCommand).ExecuteAsync(null);
        }

        public Task PercentAsync(string keypad)
        {
            ViewModel.KeypadBuffer = keypad;
            return ((IAsyncRelayCommand)ViewModel.ApplySelectedLineDiscountPercentCommand).ExecuteAsync(null);
        }

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

    /// <summary>记录请求过哪些权限的授权替身：approve = true 恒批准；false 则模拟「取消 / 未通过」（返回 null）。</summary>
    private sealed class ScriptedAuthorizationService(bool approve) : IOperationAuthorizationService
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

        public string ScannerPageId => "WholeOrderOneShotSwitchTest";

        public bool IsPromptOpen => false;

        public bool IsBusy => false;

        public string PromptMessage => string.Empty;

        public string StatusMessage => string.Empty;

        public string PermissionCode => string.Empty;

        public string Screen => string.Empty;

        public string Action => string.Empty;

        public IRelayCommand CancelCommand { get; } = new RelayCommand(() => { });

        public List<string> RequestedPermissions { get; } = [];

        public Task<OperationAuthorizationScope?> AuthorizeAsync(
            string permissionCode,
            string screen,
            string action,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            RequestedPermissions.Add(permissionCode);
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
