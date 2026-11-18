using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>
/// 收银页功能栏「历史」入口（原「取单」格）的分流：今天有挂单进挂单页，没有进本地订单历史。
/// 统计口径由 <see cref="HeldOrderTodayCountServiceTests"/> 覆盖，这里的统计服务一律用假对象。
/// </summary>
[Collection(GlobalLoggingTestCollection.Name)]
public sealed class PosTerminalHistoryEntryTests
{
    private static readonly PosSessionState Session =
        new("HB POS", "S01", "Main Branch", "POS-01", "C001", "Alice", true, 0);

    [Fact]
    public async Task Open_history_goes_to_held_orders_when_there_are_held_orders_today()
    {
        var counter = new FakeHeldOrderTodayCountService { Count = 2 };
        var navigation = new List<string>();
        using var viewModel = CreateViewModel(counter, navigation, new FakeTimeProvider());

        await viewModel.OpenHistoryCommand.ExecuteAsync(null);

        Assert.Equal(["held"], navigation);
        // 点击时重新统计过一次，角标同步为最新值。
        Assert.Equal(2, viewModel.HeldOrderTodayCount);
    }

    [Fact]
    public async Task Open_history_goes_to_local_history_when_there_are_no_held_orders()
    {
        var counter = new FakeHeldOrderTodayCountService { Count = 0 };
        var navigation = new List<string>();
        using var viewModel = CreateViewModel(counter, navigation, new FakeTimeProvider());

        await viewModel.OpenHistoryCommand.ExecuteAsync(null);

        Assert.Equal(["history"], navigation);
    }

    [Fact]
    public async Task Open_history_recounts_instead_of_trusting_a_stale_badge()
    {
        // 角标还停在「有 3 单」，但这些单已被别的收银机取走：点击时重新统计，应进本地历史而不是空的挂单页。
        var counter = new FakeHeldOrderTodayCountService { Count = 3 };
        var navigation = new List<string>();
        using var viewModel = CreateViewModel(counter, navigation, new FakeTimeProvider());
        await viewModel.RefreshHeldOrderTodayCountAsync();
        Assert.Equal(3, viewModel.HeldOrderTodayCount);

        counter.Count = 0;
        await viewModel.OpenHistoryCommand.ExecuteAsync(null);

        Assert.Equal(["history"], navigation);
        Assert.Equal(0, viewModel.HeldOrderTodayCount);
    }

    [Fact]
    public async Task Open_history_falls_back_to_the_badge_value_when_recount_is_slow()
    {
        var counter = new FakeHeldOrderTodayCountService { Count = 3 };
        var navigation = new List<string>();
        var clock = new FakeTimeProvider();
        using var viewModel = CreateViewModel(counter, navigation, clock);
        await viewModel.RefreshHeldOrderTodayCountAsync();

        // 之后的统计一直不返回（慢网络）：等够 2 秒就沿用角标当前值，不让收银员干等。
        counter.Handler = _ => new TaskCompletionSource<int>().Task;
        var opening = viewModel.OpenHistoryCommand.ExecuteAsync(null);
        clock.Advance(TimeSpan.FromSeconds(2));
        await opening;

        Assert.Equal(["held"], navigation);
    }

    [Fact]
    public async Task Open_history_without_count_service_goes_to_local_history()
    {
        var navigation = new List<string>();
        using var viewModel = CreateViewModel(counter: null, navigation, new FakeTimeProvider());

        await viewModel.OpenHistoryCommand.ExecuteAsync(null);

        Assert.Equal(["history"], navigation);
    }

    private static PosTerminalViewModel CreateViewModel(
        IHeldOrderTodayCountService? counter,
        List<string> navigation,
        FakeTimeProvider clock)
    {
        return new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            onRecallOrderAsync: () =>
            {
                navigation.Add("held");
                return Task.CompletedTask;
            },
            onOpenHistoryAsync: () =>
            {
                navigation.Add("history");
                return Task.CompletedTask;
            },
            timeProvider: clock,
            heldOrderTodayCountService: counter);
    }
}
