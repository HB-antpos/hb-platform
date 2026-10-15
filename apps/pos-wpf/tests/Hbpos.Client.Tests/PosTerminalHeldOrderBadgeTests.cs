using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>
/// 首页「取单」角标的 UI 状态：徽标文字映射、挂单后刷新、失败保留旧值、过期结果丢弃、
/// 显示时才轮询、Dispose 停止。统计口径本身由 <see cref="HeldOrderTodayCountServiceTests"/> 覆盖，
/// 这里的服务一律用假对象。VM 失败日志走 ConsoleLog，与其他日志测试串行。
/// </summary>
[Collection(GlobalLoggingTestCollection.Name)]
public sealed class PosTerminalHeldOrderBadgeTests
{
    private static readonly PosSessionState Session =
        new("HB POS", "S01", "Main Branch", "POS-01", "C001", "Alice", true, 0);

    [Theory]
    [InlineData(0, "")]
    [InlineData(-3, "")]
    [InlineData(1, "1")]
    [InlineData(9, "9")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(1234, "99+")]
    public void Badge_text_is_blank_for_zero_number_up_to_99_and_99_plus_beyond(int count, string expected)
    {
        Assert.Equal(expected, PosTerminalViewModel.FormatHeldOrderBadgeText(count));
    }

    [Fact]
    public async Task Refresh_publishes_count_and_raises_badge_property_changes()
    {
        var counter = new FakeHeldOrderTodayCountService { Count = 100 };
        using var viewModel = CreateViewModel(counter, out _);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        await viewModel.RefreshHeldOrderTodayCountAsync();

        Assert.Equal(100, viewModel.HeldOrderTodayCount);
        Assert.Equal("99+", viewModel.HeldOrderBadgeText);
        Assert.True(viewModel.IsHeldOrderBadgeVisible);
        Assert.Contains(nameof(PosTerminalViewModel.HeldOrderTodayCount), changed);
        Assert.Contains(nameof(PosTerminalViewModel.HeldOrderBadgeText), changed);
        Assert.Contains(nameof(PosTerminalViewModel.IsHeldOrderBadgeVisible), changed);

        counter.Count = 0;
        await viewModel.RefreshHeldOrderTodayCountAsync();

        Assert.Equal(0, viewModel.HeldOrderTodayCount);
        Assert.Equal(string.Empty, viewModel.HeldOrderBadgeText);
        Assert.False(viewModel.IsHeldOrderBadgeVisible);
    }

    [Fact]
    public async Task Without_a_count_service_badge_stays_hidden_and_nothing_is_scheduled()
    {
        using var viewModel = CreateViewModel(counter: null, out var clock);

        viewModel.OnScreenShown();
        await viewModel.RefreshHeldOrderTodayCountAsync(forceRemoteRefresh: true);
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Null(viewModel.LastHeldOrderCountRefreshTask);
        Assert.Equal(0, viewModel.HeldOrderTodayCount);
        Assert.False(viewModel.IsHeldOrderBadgeVisible);
    }

    [Fact]
    public async Task Hold_order_refreshes_badge_after_the_hold_completes_without_forcing_remote()
    {
        var counter = new FakeHeldOrderTodayCountService { Count = 0 };
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-HOLD"));
        using var viewModel = CreateViewModel(
            counter,
            out _,
            cart,
            onHoldOrderAsync: () =>
            {
                // 模拟导航层完成挂单：购物车清空、本机多了一张今天的挂单。
                cart.Clear();
                counter.Count = 4;
                return Task.CompletedTask;
            });
        Assert.Equal(0, viewModel.HeldOrderTodayCount);

        await viewModel.HoldOrderCommand.ExecuteAsync(null);
        await viewModel.LastHeldOrderCountRefreshTask!;

        Assert.Equal(4, viewModel.HeldOrderTodayCount);
        var call = Assert.Single(counter.Calls);
        Assert.False(call.Force);
        Assert.Equal("S01", call.Session.StoreCode);
    }

    [Fact]
    public async Task Failed_refresh_keeps_previous_value_and_never_throws()
    {
        var counter = new FakeHeldOrderTodayCountService { Count = 5 };
        using var viewModel = CreateViewModel(counter, out _);
        await viewModel.RefreshHeldOrderTodayCountAsync();
        Assert.Equal(5, viewModel.HeldOrderTodayCount);

        counter.Failure = new InvalidOperationException("sqlite busy");
        await viewModel.RefreshHeldOrderTodayCountAsync();
        await viewModel.RefreshHeldOrderTodayCountAsync();

        Assert.Equal(5, viewModel.HeldOrderTodayCount);
        Assert.Equal("5", viewModel.HeldOrderBadgeText);

        // 恢复后正常更新。
        counter.Failure = null;
        counter.Count = 6;
        await viewModel.RefreshHeldOrderTodayCountAsync();
        Assert.Equal(6, viewModel.HeldOrderTodayCount);
    }

    [Fact]
    public async Task Stale_refresh_result_is_discarded_when_a_newer_refresh_finished_first()
    {
        var counter = new FakeHeldOrderTodayCountService();
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = 0;
        counter.Handler = _ => ++invocation == 1 ? first.Task : Task.FromResult(2);
        using var viewModel = CreateViewModel(counter, out _);

        var olderRefresh = viewModel.RefreshHeldOrderTodayCountAsync();
        var newerRefresh = viewModel.RefreshHeldOrderTodayCountAsync();
        await newerRefresh;
        Assert.Equal(2, viewModel.HeldOrderTodayCount);

        // 旧请求（挂单前的读数）晚到：即使它没响应取消，也不得覆盖新值。
        first.SetResult(9);
        await olderRefresh;

        Assert.Equal(2, viewModel.HeldOrderTodayCount);
        Assert.True(counter.Calls[0].Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Entering_the_screen_joins_an_in_flight_refresh_instead_of_preempting_it()
    {
        var counter = new FakeHeldOrderTodayCountService();
        var inFlight = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        counter.Handler = _ => inFlight.Task;
        using var viewModel = CreateViewModel(counter, out _);

        // 例如取单成功后的强制刷新还在跑，随后 ShowPos 触发进入首页。
        var recallRefresh = viewModel.RefreshHeldOrderTodayCountAsync(forceRemoteRefresh: true);
        viewModel.OnScreenShown();

        Assert.Single(counter.Calls);
        Assert.True(counter.Calls[0].Force);

        inFlight.SetResult(7);
        await recallRefresh;
        Assert.Equal(7, viewModel.HeldOrderTodayCount);
    }

    [Fact]
    public async Task Screen_shown_refreshes_once_and_polls_every_60_seconds_only_while_visible()
    {
        var counter = new FakeHeldOrderTodayCountService { Count = 2 };
        using var viewModel = CreateViewModel(counter, out var clock);

        viewModel.OnScreenShown();
        await viewModel.LastHeldOrderCountRefreshTask!;
        Assert.Equal(2, viewModel.HeldOrderTodayCount);
        Assert.Equal(1, counter.CallCount);

        // 已可见时重复通知不重复刷新。
        viewModel.OnScreenShown();
        Assert.Equal(1, counter.CallCount);

        counter.Count = 3;
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, counter.CallCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        await viewModel.LastHeldOrderCountRefreshTask!;
        Assert.Equal(2, counter.CallCount);
        Assert.Equal(3, viewModel.HeldOrderTodayCount);
        Assert.False(counter.Calls[^1].Force);

        // 离开首页：停表，不再刷新。
        viewModel.OnScreenHidden();
        counter.Count = 4;
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(2, counter.CallCount);
        Assert.Equal(3, viewModel.HeldOrderTodayCount);

        // 回到首页：立即刷新并恢复轮询。
        viewModel.OnScreenShown();
        await viewModel.LastHeldOrderCountRefreshTask!;
        Assert.Equal(3, counter.CallCount);
        Assert.Equal(4, viewModel.HeldOrderTodayCount);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(4, counter.CallCount);
    }

    [Fact]
    public async Task Dispose_stops_polling_and_discards_in_flight_result()
    {
        var counter = new FakeHeldOrderTodayCountService();
        var inFlight = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        counter.Handler = _ => inFlight.Task;
        var viewModel = CreateViewModel(counter, out var clock);

        viewModel.OnScreenShown();
        var refresh = viewModel.LastHeldOrderCountRefreshTask!;
        viewModel.Dispose();
        inFlight.SetResult(9);
        await refresh;
        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(0, viewModel.HeldOrderTodayCount);
        Assert.Equal(1, counter.CallCount);
        Assert.True(counter.Calls[0].Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Store_change_resets_badge_and_forces_a_refresh_for_the_new_store_while_visible()
    {
        var counter = new FakeHeldOrderTodayCountService { Count = 3 };
        using var viewModel = CreateViewModel(counter, out _);
        viewModel.OnScreenShown();
        await viewModel.LastHeldOrderCountRefreshTask!;
        Assert.Equal(3, viewModel.HeldOrderTodayCount);

        counter.Count = 1;
        viewModel.Session = Session with { StoreCode = "S02" };
        await viewModel.LastHeldOrderCountRefreshTask!;

        var call = counter.Calls[^1];
        Assert.Equal("S02", call.Session.StoreCode);
        Assert.True(call.Force);
        Assert.Equal(1, viewModel.HeldOrderTodayCount);
    }

    [Fact]
    public async Task Session_changes_unrelated_to_scope_do_not_trigger_a_refresh()
    {
        var counter = new FakeHeldOrderTodayCountService { Count = 3 };
        using var viewModel = CreateViewModel(counter, out _);
        viewModel.OnScreenShown();
        await viewModel.LastHeldOrderCountRefreshTask!;

        // 待同步数 / 收银员变化与角标无关。
        viewModel.Session = Session with { PendingSyncCount = 7 };
        viewModel.Session = Session with { CashierId = "C002", CashierName = "Bob" };

        Assert.Equal(1, counter.CallCount);
        Assert.Equal(3, viewModel.HeldOrderTodayCount);

        // 联机状态变化（掉线 / 恢复联机）会重新取：联机与否决定远端共享挂单能不能算进来。
        viewModel.Session = Session with { IsOnline = false };
        await viewModel.LastHeldOrderCountRefreshTask!;
        Assert.Equal(2, counter.CallCount);
        Assert.False(counter.Calls[^1].Session.IsOnline);
    }

    private static PosTerminalViewModel CreateViewModel(
        IHeldOrderTodayCountService? counter,
        out FakeTimeProvider clock,
        PosCartService? cart = null,
        Func<Task>? onHoldOrderAsync = null)
    {
        clock = new FakeTimeProvider();
        return new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            cart ?? new PosCartService(),
            Session,
            onOpenPayment: null,
            onHoldOrderAsync: onHoldOrderAsync,
            timeProvider: clock,
            heldOrderTodayCountService: counter);
    }

    private static SellableItemDto CreateItem(string productCode) =>
        new(
            "S01",
            productCode,
            null,
            "Item " + productCode,
            "LOOKUP-" + productCode,
            "ITEM-" + productCode,
            null,
            5m,
            PriceSourceKind.StoreRetailPrice,
            "Store",
            1m,
            null);
}

/// <summary>
/// 记录每次调用的假统计服务，供 VM / 导航 / 工厂测试共用。
/// 默认同步返回 <see cref="Count"/>；设置 <see cref="Failure"/> 返回失败；设置 <see cref="Handler"/> 可控制完成时机。
/// </summary>
internal sealed class FakeHeldOrderTodayCountService : IHeldOrderTodayCountService
{
    private readonly object _gate = new();
    private readonly List<(PosSessionState Session, bool Force, CancellationToken Token)> _calls = [];

    public int Count { get; set; }

    public Exception? Failure { get; set; }

    public Func<CancellationToken, Task<int>>? Handler { get; set; }

    public IReadOnlyList<(PosSessionState Session, bool Force, CancellationToken Token)> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public int CallCount => Calls.Count;

    public Task<int> GetTodayCountAsync(
        PosSessionState session,
        bool forceRemoteRefresh = false,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _calls.Add((session, forceRemoteRefresh, cancellationToken));
        }

        if (Handler is not null)
        {
            return Handler(cancellationToken);
        }

        return Failure is not null
            ? Task.FromException<int>(Failure)
            : Task.FromResult(Count);
    }
}
