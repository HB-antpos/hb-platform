using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Microsoft.Extensions.Time.Testing;
using static Hbpos.Client.Tests.PosTerminalRemoteLookupTestSupport;

namespace Hbpos.Client.Tests;

/// <summary>
/// C4：收银页对「本地未命中后远程补查」的即时反馈、重复扫码、迟到结果丢弃与审计口径。
/// 本文件只使用补查改动前就存在的公开 API（构造函数、命令、Session），便于在旧代码上跑出失败对照。
/// </summary>
[Collection(CultureSensitiveTestCollection.Name)]
public sealed class PosTerminalRemoteLookupViewModelTests
{
    [Fact]
    public async Task Local_miss_shows_lookup_status_and_plays_accept_cue_as_soon_as_lookup_is_dispatched()
    {
        using var harness = new TerminalHarness();

        harness.Scan("9305001");
        await harness.Remote.WaitForCallAsync("9305001");

        // 补查还没回来，状态栏和提示音必须已经给出，收银员不会在这 2 秒里以为没扫上。
        var started = harness.Localization.T("pos.status.remoteLookupStarted");
        Assert.NotEqual("pos.status.remoteLookupStarted", started);
        await WaitUntilAsync(() => harness.ViewModel.StatusMessage.Contains(started, StringComparison.Ordinal));
        Assert.Equal([UserFeedbackCue.ButtonClick], harness.Feedback.Cues);
        Assert.Equal(StatusFeedbackKind.Neutral, harness.ViewModel.StatusFeedbackKind);

        harness.Remote.Found("9305001", CreateItem("SKU-C4-V01", "Remote Cola", "9305001", 3m));
        await WaitUntilAsync(() => harness.Cart.Lines.Count == 1);
        await WaitUntilAsync(() => harness.Feedback.Cues.Count == 2);
        Assert.Equal([UserFeedbackCue.ButtonClick, UserFeedbackCue.ScanAdded], harness.Feedback.Cues);
    }

    [Fact]
    public async Task Same_code_scanned_twice_while_pending_hints_in_progress_without_extra_add_or_sound()
    {
        using var harness = new TerminalHarness();

        harness.Scan("9305002");
        await harness.Remote.WaitForCallAsync("9305002");
        harness.Scan("9305002");

        var inProgress = harness.Localization.T("pos.status.remoteLookupInProgress");
        Assert.NotEqual("pos.status.remoteLookupInProgress", inProgress);
        await WaitUntilAsync(() => harness.ViewModel.StatusMessage.Contains(inProgress, StringComparison.Ordinal));
        Assert.Equal(StatusFeedbackKind.Warning, harness.ViewModel.StatusFeedbackKind);
        Assert.Equal([UserFeedbackCue.ButtonClick], harness.Feedback.Cues);
        Assert.Empty(harness.Cart.Lines);

        harness.Remote.Found("9305002", CreateItem("SKU-C4-V02", "Remote Cola", "9305002", 3m));
        await WaitUntilAsync(() => harness.Cart.Lines.Count == 1);
        await WaitUntilAsync(() => harness.Feedback.Cues.Count == 2);

        // 同码只加一次；重复扫码没有再叠加数量，也没有多响一次。
        Assert.Equal(1m, Assert.Single(harness.Cart.Lines).Quantity);
        Assert.Equal([UserFeedbackCue.ButtonClick, UserFeedbackCue.ScanAdded], harness.Feedback.Cues);
        Assert.Equal(1, harness.Remote.Calls("9305002"));
    }

    [Fact]
    public async Task Different_codes_scanned_while_pending_are_both_added()
    {
        using var harness = new TerminalHarness();

        harness.Scan("9305003");
        await harness.Remote.WaitForCallAsync("9305003");
        harness.Scan("9305004");
        await harness.Remote.WaitForCallAsync("9305004");
        harness.Remote.Found("9305003", CreateItem("SKU-C4-V03", "Remote Cola", "9305003", 3m));
        harness.Remote.Found("9305004", CreateItem("SKU-C4-V04", "Remote Juice", "9305004", 5m));

        await WaitUntilAsync(() => harness.Cart.Lines.Count == 2);
        Assert.Equal(8m, harness.Cart.TotalAmount);
    }

    [Fact]
    public async Task Clear_cart_while_lookup_pending_discards_result_with_notice_and_error_cue()
    {
        using var harness = new TerminalHarness();
        harness.Cart.AddItem(CreateItem("SKU-C4-V05", "Existing Tea", "9305005", 2m));

        harness.Scan("9305006");
        await harness.Remote.WaitForCallAsync("9305006");
        // 设计：补查进行中「清空」仍可点击（命令可用性只取决于购物车是否为空），迟到结果事后丢弃。
        Assert.True(harness.ViewModel.ClearCartCommand.CanExecute(null));
        await ((IAsyncRelayCommand)harness.ViewModel.ClearCartCommand).ExecuteAsync(null);
        Assert.Empty(harness.Cart.Lines);
        harness.Remote.Found("9305006", CreateItem("SKU-C4-V06", "Remote Water", "9305006", 1.5m));

        var discarded = harness.Localization.T("pos.status.remoteLookupDiscarded");
        Assert.NotEqual("pos.status.remoteLookupDiscarded", discarded);
        await WaitUntilAsync(() => harness.ViewModel.StatusMessage.Contains(discarded, StringComparison.Ordinal));
        Assert.Empty(harness.Cart.Lines);
        Assert.Equal(StatusFeedbackKind.Warning, harness.ViewModel.StatusFeedbackKind);
        Assert.Equal(UserFeedbackCue.OperationError, harness.Feedback.Cues.Last());
    }

    [Fact]
    public async Task Cart_emptied_by_other_flow_while_pending_discards_result()
    {
        using var harness = new TerminalHarness();
        harness.Cart.AddItem(CreateItem("SKU-C4-V09", "Existing Tea", "9305009", 2m));

        harness.Scan("9305010");
        await harness.Remote.WaitForCallAsync("9305010");
        // 付款完成 / 挂单成功等流程都会直接清空共享购物车：这也是一次换单。
        harness.Cart.Clear();
        harness.Remote.Found("9305010", CreateItem("SKU-C4-V10", "Remote Water", "9305010", 1.5m));

        var discarded = harness.Localization.T("pos.status.remoteLookupDiscarded");
        await WaitUntilAsync(() => harness.ViewModel.StatusMessage.Contains(discarded, StringComparison.Ordinal));
        Assert.Empty(harness.Cart.Lines);
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("recall")]
    [InlineData("returns")]
    public async Task Leaving_for_hold_recall_or_returns_while_pending_discards_result(string navigation)
    {
        using var harness = new TerminalHarness();
        harness.Cart.AddItem(CreateItem("SKU-C4-V11", "Existing Tea", "9305011", 2m));

        harness.Scan("9305012");
        await harness.Remote.WaitForCallAsync("9305012");
        switch (navigation)
        {
            case "hold":
                await ((IAsyncRelayCommand)harness.ViewModel.HoldOrderCommand).ExecuteAsync(null);
                break;
            case "recall":
                await ((IAsyncRelayCommand)harness.ViewModel.RecallOrderCommand).ExecuteAsync(null);
                break;
            default:
                await ((IAsyncRelayCommand)harness.ViewModel.OpenReturnsCommand).ExecuteAsync(null);
                break;
        }

        harness.Remote.Found("9305012", CreateItem("SKU-C4-V12", "Remote Water", "9305012", 1.5m));

        var discarded = harness.Localization.T("pos.status.remoteLookupDiscarded");
        await WaitUntilAsync(() => harness.ViewModel.StatusMessage.Contains(discarded, StringComparison.Ordinal));
        Assert.Equal("Existing Tea", Assert.Single(harness.Cart.Lines).DisplayName);
    }

    [Fact]
    public async Task Cashier_switched_while_pending_discards_result()
    {
        using var harness = new TerminalHarness();

        harness.Scan("9305013");
        await harness.Remote.WaitForCallAsync("9305013");
        harness.ViewModel.Session = Session with { CashierId = "C002", CashierName = "Bob" };
        harness.Remote.Found("9305013", CreateItem("SKU-C4-V13", "Remote Water", "9305013", 1.5m));

        var discarded = harness.Localization.T("pos.status.remoteLookupDiscarded");
        await WaitUntilAsync(() => harness.ViewModel.StatusMessage.Contains(discarded, StringComparison.Ordinal));
        Assert.Empty(harness.Cart.Lines);
    }

    [Fact]
    public async Task Connectivity_and_sync_count_changes_while_pending_do_not_discard_result()
    {
        using var harness = new TerminalHarness();

        harness.Scan("9305014");
        await harness.Remote.WaitForCallAsync("9305014");
        // 联机状态、待同步数是高频变化的非身份字段，不能让补查结果白白作废。
        harness.ViewModel.Session = Session with { IsOnline = false, PendingSyncCount = 3 };
        harness.Remote.Found("9305014", CreateItem("SKU-C4-V14", "Remote Water", "9305014", 1.5m));

        await WaitUntilAsync(() => harness.Cart.Lines.Count == 1);
        Assert.Equal(1.5m, harness.Cart.TotalAmount);
    }

    [Fact]
    public async Task Disposed_view_model_does_not_add_late_result()
    {
        using var harness = new TerminalHarness();
        harness.Scan("9305015");
        await harness.Remote.WaitForCallAsync("9305015");

        // 收银页被销毁（重新登录、锁定重建等）后，旧页面的在途补查不得再改动共享购物车。
        harness.ViewModel.Dispose();
        harness.Remote.Found("9305015", CreateItem("SKU-C4-V15", "Remote Water", "9305015", 1.5m));

        var discarded = harness.Localization.T("pos.status.remoteLookupDiscarded");
        await WaitUntilAsync(() =>
            harness.Cart.Lines.Count > 0 ||
            harness.ViewModel.StatusMessage.Contains(discarded, StringComparison.Ordinal));
        Assert.Empty(harness.Cart.Lines);
    }

    [Fact]
    public async Task Late_remote_add_is_audited_against_the_cart_at_apply_time_not_at_scan_time()
    {
        using var harness = new TerminalHarness();
        var tea = CreateItem("SKU-C4-V16", "Local Tea", "9305016", 2m);
        var juice = CreateItem("SKU-C4-V17", "Local Juice", "9305017", 4m);
        harness.Index.ReplaceAll([tea, juice]);
        harness.Cart.AddItem(tea);

        harness.Scan("9305018");
        await harness.Remote.WaitForCallAsync("9305018");
        // 补查等待期间又扫了一个本地商品，它有自己独立的审计记录。
        harness.Scan("9305017");
        await WaitUntilAsync(() => harness.Audit.Events.Count == 1);
        harness.Remote.Found("9305018", CreateItem("SKU-C4-V18", "Remote Water", "9305018", 1.5m));
        await WaitUntilAsync(() => harness.Audit.Events.Count == 2);

        var events = harness.Audit.Events.ToArray();
        var localEvent = events[0];
        var remoteEvent = events[1];
        Assert.Equal("Local Juice", Assert.Single(localEvent.Items).DisplayName);
        Assert.Equal(4m, localEvent.AmountDelta);

        // 补查那次加购的前后快照只对应「这一件」：不能把等待期间别的扫码也记进来。
        var remoteItem = Assert.Single(remoteEvent.Items);
        Assert.Equal("Remote Water", remoteItem.DisplayName);
        Assert.Equal(0m, remoteItem.BeforeQuantity);
        Assert.Equal(1m, remoteItem.AfterQuantity);
        Assert.Equal(1.5m, remoteEvent.AmountDelta);
        Assert.Equal(6m, remoteEvent.BeforeActual);
        Assert.Equal(7.5m, remoteEvent.AfterActual);
        Assert.Equal("SCAN", remoteEvent.ReasonCode);
    }

    [Fact]
    public async Task Discarded_late_result_records_no_cart_audit()
    {
        using var harness = new TerminalHarness();
        harness.Cart.AddItem(CreateItem("SKU-C4-V19", "Existing Tea", "9305019", 2m));

        harness.Scan("9305020");
        await harness.Remote.WaitForCallAsync("9305020");
        await ((IAsyncRelayCommand)harness.ViewModel.ClearCartCommand).ExecuteAsync(null);
        harness.Remote.Found("9305020", CreateItem("SKU-C4-V20", "Remote Water", "9305020", 1.5m));

        var discarded = harness.Localization.T("pos.status.remoteLookupDiscarded");
        await WaitUntilAsync(() => harness.ViewModel.StatusMessage.Contains(discarded, StringComparison.Ordinal));

        // 只有「清空」一条审计，迟到被丢弃的扫码不应产生加购记录。
        var auditEvent = Assert.Single(harness.Audit.Events);
        Assert.Equal("CART_CLEAR", auditEvent.OperationType);
    }

    [Fact]
    public async Task Lookup_timeout_after_immediate_feedback_falls_back_to_no_match_error()
    {
        var timeProvider = new FakeTimeProvider();
        using var harness = new TerminalHarness(timeProvider);

        harness.Scan("9305021");
        await harness.Remote.WaitForCallAsync("9305021");
        await WaitUntilAsync(() => harness.Feedback.Cues.Count == 1);
        timeProvider.Advance(PosTerminalWorkflowService.RemoteLookupTimeout);

        // 超时沿用现有行为：回到「未命中」错误提示与错误音；之前的受理音保留。
        var noMatch = harness.Localization.T("pos.status.noLocalMatch");
        await WaitUntilAsync(() => harness.ViewModel.StatusMessage.Contains(noMatch, StringComparison.Ordinal));
        Assert.Equal([UserFeedbackCue.ButtonClick, UserFeedbackCue.ScanNoMatch], harness.Feedback.Cues);
        Assert.Empty(harness.Cart.Lines);
    }

    [Theory]
    [InlineData("en-US", "Looking up")]
    [InlineData("zh-CN", "正在查询")]
    public void New_status_texts_exist_in_both_languages(string culture, string expectedStartedFragment)
    {
        var localization = new LocalizationService();
        localization.SetCulture(culture);
        try
        {
            foreach (var key in new[]
            {
                "pos.status.remoteLookupStarted",
                "pos.status.remoteLookupInProgress",
                "pos.status.remoteLookupDiscarded"
            })
            {
                Assert.NotEqual(key, localization.T(key));
                Assert.False(string.IsNullOrWhiteSpace(localization.T(key)));
            }

            Assert.Contains(expectedStartedFragment, localization.T("pos.status.remoteLookupStarted"), StringComparison.Ordinal);
        }
        finally
        {
            localization.SetCulture(LocalizationService.DefaultCultureName);
        }
    }
}
