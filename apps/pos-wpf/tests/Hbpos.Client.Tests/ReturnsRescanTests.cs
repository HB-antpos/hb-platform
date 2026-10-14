using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Tests;

/// <summary>
/// 设置页「退货操作需要再次扫码」（默认关闭）。
///
/// 规则：
/// 1. 只管侧栏「退款」进入退货页那一下（OpenReturnsCommand，权限 Returns.View）；进入退货页后的加小票行、
///    加无小票商品、确认退货（ReceiptReturnsViewModel 里各自的授权）不再重复扫码，也没有其它入口绕过这一下。
/// 2. 关闭（默认）：和原来一样，当前收银员有权限就直接进入退货页。
/// 3. 开启：即使当前收银员自己有退货权限，也要先扫一次授权员工条码确认；没通过就不进入。
/// 4. 每次点「退款」都现读设置，改设置后立即生效；读取设置失败时按「需要」处理（安全类开关，宁可多扫一次）。
/// 5. 与「无交易开钱箱」两个开关互相独立：开一个不影响另一个。
/// </summary>
public sealed class ReturnsRescanTests
{
    private static PosSessionState Session => new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    [Fact]
    public async Task Setting_off_requests_the_normal_authorization_and_opens_returns()
    {
        using var harness = new Harness(readReturnsSetting: _ => Task.FromResult(false));

        await harness.OpenReturnsAsync();

        var request = Assert.Single(harness.Authorization.Requests);
        Assert.Equal(Permissions.PosTerminal.Returns.View, request.Permission);
        Assert.False(request.RequireFreshScan);
        Assert.Equal(1, harness.OpenReturnsCount);
    }

    [Fact]
    public async Task Setting_on_requests_a_fresh_scan_and_opens_returns_after_it_is_approved()
    {
        using var harness = new Harness(readReturnsSetting: _ => Task.FromResult(true));

        await harness.OpenReturnsAsync();

        var request = Assert.Single(harness.Authorization.Requests);
        Assert.Equal(Permissions.PosTerminal.Returns.View, request.Permission);
        Assert.True(request.RequireFreshScan);
        Assert.Equal(1, harness.OpenReturnsCount);
    }

    [Fact]
    public async Task Setting_on_and_scan_not_confirmed_does_not_open_returns()
    {
        using var harness = new Harness(readReturnsSetting: _ => Task.FromResult(true), approve: false);

        await harness.OpenReturnsAsync();

        Assert.True(Assert.Single(harness.Authorization.Requests).RequireFreshScan);
        Assert.Equal(0, harness.OpenReturnsCount);
    }

    [Fact]
    public async Task Without_a_setting_source_it_behaves_as_before()
    {
        using var harness = new Harness(readReturnsSetting: null);

        await harness.OpenReturnsAsync();

        Assert.False(Assert.Single(harness.Authorization.Requests).RequireFreshScan);
        Assert.Equal(1, harness.OpenReturnsCount);
    }

    [Fact]
    public async Task Failing_to_read_the_setting_requires_a_fresh_scan()
    {
        using var harness = new Harness(
            readReturnsSetting: _ => throw new InvalidOperationException("settings store unavailable"));

        await harness.OpenReturnsAsync();

        // 安全类开关读不到：不知道配置，按「需要」处理；这次点击没有因为异常而中断。
        Assert.True(Assert.Single(harness.Authorization.Requests).RequireFreshScan);
        Assert.Equal(1, harness.OpenReturnsCount);
    }

    [Fact]
    public async Task Changing_the_setting_takes_effect_on_the_next_press_without_restart()
    {
        var enabled = false;
        using var harness = new Harness(readReturnsSetting: _ => Task.FromResult(enabled));

        await harness.OpenReturnsAsync();
        enabled = true;
        await harness.OpenReturnsAsync();
        enabled = false;
        await harness.OpenReturnsAsync();

        Assert.Equal(
            [false, true, false],
            harness.Authorization.Requests.Select(request => request.RequireFreshScan));
        Assert.Equal(3, harness.OpenReturnsCount);
    }

    [Fact]
    public async Task The_returns_switch_and_the_cash_drawer_switch_are_independent()
    {
        // 退货开关开、钱箱开关关：只有退货要求再扫。
        using var returnsOnly = new Harness(
            readReturnsSetting: _ => Task.FromResult(true),
            readCashDrawerSetting: _ => Task.FromResult(false));
        await returnsOnly.OpenReturnsAsync();
        await returnsOnly.OpenCashDrawerAsync();
        Assert.Equal(
            [
                (Permissions.PosTerminal.Returns.View, true),
                (Permissions.PosTerminal.CashDrawer.Open, false)
            ],
            returnsOnly.Authorization.Requests);

        // 反过来：钱箱开关开、退货开关关：只有钱箱要求再扫。
        using var drawerOnly = new Harness(
            readReturnsSetting: _ => Task.FromResult(false),
            readCashDrawerSetting: _ => Task.FromResult(true));
        await drawerOnly.OpenReturnsAsync();
        await drawerOnly.OpenCashDrawerAsync();
        Assert.Equal(
            [
                (Permissions.PosTerminal.Returns.View, false),
                (Permissions.PosTerminal.CashDrawer.Open, true)
            ],
            drawerOnly.Authorization.Requests);
    }

    private sealed class Harness : IDisposable
    {
        public Harness(
            Func<CancellationToken, Task<bool>>? readReturnsSetting,
            Func<CancellationToken, Task<bool>>? readCashDrawerSetting = null,
            bool approve = true)
        {
            Authorization = new RescanAwareAuthorizationService(approve);
            ViewModel = new PosTerminalViewModel(
                new LocalSellableItemIndex(),
                new PosCartService(),
                Session,
                onOpenPayment: null,
                onOpenReturns: () => OpenReturnsCount++,
                onOpenCashDrawerAsync: () => Task.FromResult(new ReceiptPrintResult(true, string.Empty)),
                operationAuthorizationService: Authorization,
                isNoSaleCashDrawerRescanRequiredAsync: readCashDrawerSetting,
                isReturnsRescanRequiredAsync: readReturnsSetting);
        }

        public RescanAwareAuthorizationService Authorization { get; }

        public PosTerminalViewModel ViewModel { get; }

        public int OpenReturnsCount { get; private set; }

        public Task OpenReturnsAsync() => ((IAsyncRelayCommand)ViewModel.OpenReturnsCommand).ExecuteAsync(null);

        public Task OpenCashDrawerAsync() => ((IAsyncRelayCommand)ViewModel.OpenCashDrawerCommand).ExecuteAsync(null);

        public void Dispose()
        {
            ViewModel.Dispose();
        }
    }
}
