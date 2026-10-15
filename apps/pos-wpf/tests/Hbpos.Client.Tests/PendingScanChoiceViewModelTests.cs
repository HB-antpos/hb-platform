using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>
/// 收银页「待选条」的界面状态与流程：弹窗期间继续扫码不丢商品、选定后正确入车、
/// 付款 / 挂单被拦、放弃二次确认、会话与清空购物车时的清理。
/// </summary>
public sealed class PendingScanChoiceViewModelTests
{
    private const string Conflict = "930114";

    [Fact]
    public void Scanning_continues_while_a_conflicting_barcode_is_pending_and_the_later_choice_is_added_correctly()
    {
        var fixture = CreateFixture();

        fixture.Scanner.Emit(Conflict);
        fixture.Scanner.Emit("930110");
        fixture.Scanner.Emit("930111");

        // 弹窗未处理期间继续扫的商品照常入车，待选原样保留（旧行为下待选会被第二次扫码顶掉）。
        Assert.Equal(["Scanner Apple", "Scanner Pear"], fixture.ViewModel.CartLines.Select(line => line.DisplayName));
        Assert.Equal(1, fixture.ViewModel.PendingChoices.Count);
        Assert.True(fixture.ViewModel.PendingChoices.IsExpandedVisible);
        Assert.False(fixture.ViewModel.IsMatchesPopupOpen);
        Assert.Empty(fixture.ViewModel.Matches);

        fixture.ViewModel.PendingChoices.SelectMatchCommand.Execute(fixture.Large);

        Assert.Equal(
            ["Scanner Apple", "Scanner Pear", "Conflict Large"],
            fixture.ViewModel.CartLines.Select(line => line.DisplayName));
        Assert.Equal(0, fixture.ViewModel.PendingChoices.Count);
        Assert.False(fixture.ViewModel.PendingChoices.HasPending);
        Assert.Same(fixture.ViewModel.CartLines[2], fixture.ViewModel.SelectedCartLine);
        Assert.Contains("Conflict Large", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_pending_choices_are_handled_first_in_first_out_and_the_next_one_comes_up_automatically()
    {
        var fixture = CreateFixture();

        fixture.Scanner.Emit(Conflict);
        fixture.Scanner.Emit("930110");
        fixture.Scanner.Emit(Conflict);

        var panel = fixture.ViewModel.PendingChoices;
        Assert.Equal(2, panel.Count);
        Assert.Equal("Pending 1 / 2", panel.BadgeText);
        Assert.False(panel.Current!.SameBarcodeAsPrevious);
        Assert.Equal([true, false], panel.QueueDots.Select(dot => dot.IsCurrent).Take(2));

        panel.SelectMatchCommand.Execute(fixture.Small);

        Assert.Equal(1, panel.Count);
        Assert.Equal("Pending 1 / 1", panel.BadgeText);
        // 第二个是同一条码连扫的：入队时已打上「与上一项同一条码」的标记（可能是重复扫码），轮到它时标记仍在，提示收银员留意。
        Assert.True(panel.Current!.SameBarcodeAsPrevious);
        Assert.True(panel.ShowSameBarcodeHint);
        Assert.Equal(2, panel.CurrentCandidates.Count);
        panel.SelectMatchCommand.Execute(fixture.Small);

        Assert.Equal(0, panel.Count);
        var smallLine = Assert.Single(fixture.ViewModel.CartLines, line => line.DisplayName == "Conflict Small");
        Assert.Equal(2m, smallLine.Quantity);
    }

    [Fact]
    public void A_scanned_conflict_clears_the_search_box_and_plays_the_multiple_match_cue()
    {
        var fixture = CreateFixture();
        fixture.ViewModel.ScanText = "leftover";

        fixture.Scanner.Emit(Conflict);

        Assert.Empty(fixture.ViewModel.ScanText);
        Assert.Contains(UserFeedbackCue.ScanMultipleMatches, fixture.Feedback.Cues);
    }

    [Fact]
    public void Paying_with_pending_choices_asks_first_and_only_pays_after_discarding()
    {
        var fixture = CreateFixture();
        fixture.Scanner.Emit("930110");
        fixture.Scanner.Emit(Conflict);
        fixture.Scanner.Emit(Conflict);

        fixture.ViewModel.OpenPaymentCommand.Execute(null);

        // 被拦：不进付款页，弹出确认（不会白白弹一次授权）。
        Assert.Equal(0, fixture.PaymentOpened);
        var panel = fixture.ViewModel.PendingChoices;
        Assert.True(panel.IsPromptOpen);
        Assert.Equal(PendingChoiceReasons.Pay, panel.PromptReason);
        Assert.Equal("2 pending item(s) not handled", panel.PromptTitleText);
        Assert.Equal("Discard 2 and pay", panel.PromptConfirmText);

        // 「返回处理」：关掉确认、待选原样保留、条保持展开。
        panel.CancelPromptCommand.Execute(null);
        Assert.False(panel.IsPromptOpen);
        Assert.Equal(2, panel.Count);
        Assert.Equal(0, fixture.PaymentOpened);

        // 再点付款并确认放弃：待选清零后才真正进入付款。
        fixture.ViewModel.OpenPaymentCommand.Execute(null);
        panel.ConfirmPromptCommand.Execute(null);

        Assert.Equal(0, panel.Count);
        Assert.Equal(1, fixture.PaymentOpened);
        Assert.False(panel.IsPromptOpen);
    }

    [Fact]
    public void Paying_without_pending_choices_is_not_blocked()
    {
        var fixture = CreateFixture();
        fixture.Scanner.Emit("930110");

        fixture.ViewModel.OpenPaymentCommand.Execute(null);

        Assert.Equal(1, fixture.PaymentOpened);
        Assert.False(fixture.ViewModel.PendingChoices.IsPromptOpen);
    }

    [Fact]
    public void Holding_the_order_with_pending_choices_asks_first()
    {
        var fixture = CreateFixture();
        fixture.Scanner.Emit("930110");
        fixture.Scanner.Emit(Conflict);

        fixture.ViewModel.HoldOrderCommand.Execute(null);

        Assert.Equal(0, fixture.HoldCalls);
        Assert.Equal(PendingChoiceReasons.Hold, fixture.ViewModel.PendingChoices.PromptReason);

        fixture.ViewModel.PendingChoices.ConfirmPromptCommand.Execute(null);

        Assert.Equal(1, fixture.HoldCalls);
        Assert.Equal(0, fixture.ViewModel.PendingChoices.Count);
    }

    [Fact]
    public void Abandon_needs_a_second_tap_and_the_first_tap_expires_after_a_few_seconds()
    {
        var fixture = CreateFixture();
        fixture.Scanner.Emit(Conflict);
        var panel = fixture.ViewModel.PendingChoices;

        panel.AbandonCommand.Execute(null);
        Assert.True(panel.IsAbandonArmed);
        Assert.Equal("Tap again to discard", panel.AbandonText);
        Assert.Equal(1, panel.Count);

        // 超时自动还原：防止触屏误点后过一会儿再碰到就直接放弃。
        fixture.Time.Advance(PendingScanChoicePanelViewModel.AbandonConfirmWindow + TimeSpan.FromMilliseconds(1));
        Assert.False(panel.IsAbandonArmed);
        Assert.Equal("Discard this", panel.AbandonText);
        Assert.Equal(1, panel.Count);

        panel.AbandonCommand.Execute(null);
        panel.AbandonCommand.Execute(null);

        Assert.Equal(0, panel.Count);
        Assert.Empty(fixture.ViewModel.CartLines);
        Assert.Equal("Discarded a pending item (0 left).", fixture.ViewModel.StatusMessage);
    }

    [Fact]
    public void Discard_all_goes_through_the_prompt_without_continuing_to_another_action()
    {
        var fixture = CreateFixture();
        fixture.Scanner.Emit(Conflict);
        fixture.Scanner.Emit(Conflict);
        var panel = fixture.ViewModel.PendingChoices;
        Assert.True(panel.CanAbandonAll);

        panel.AbandonAllCommand.Execute(null);
        Assert.True(panel.IsPromptOpen);
        Assert.Equal(PendingChoiceReasons.AbandonAll, panel.PromptReason);
        panel.ConfirmPromptCommand.Execute(null);

        Assert.Equal(0, panel.Count);
        Assert.Equal(0, fixture.PaymentOpened);
        Assert.Equal(0, fixture.HoldCalls);
        Assert.Equal("Discarded 2 pending item(s).", fixture.ViewModel.StatusMessage);
    }

    [Fact]
    public void Clearing_the_cart_discards_pending_choices_too()
    {
        var fixture = CreateFixture();
        fixture.Scanner.Emit("930110");
        fixture.Scanner.Emit(Conflict);

        fixture.ViewModel.ClearCartCommand.Execute(null);

        Assert.Empty(fixture.ViewModel.CartLines);
        Assert.Equal(0, fixture.ViewModel.PendingChoices.Count);
    }

    [Fact]
    public void Switching_cashier_keeps_pending_choices_but_switching_store_discards_them()
    {
        var fixture = CreateFixture();
        fixture.Scanner.Emit(Conflict);

        // 锁屏后换人登录：购物车保留，待选随购物车保留。
        fixture.ViewModel.Session = Session with { CashierId = "C002", CashierName = "Bob" };
        Assert.Equal(1, fixture.ViewModel.PendingChoices.Count);

        // 换门店：候选属于旧门店的目录，不能带到新会话。
        fixture.ViewModel.Session = Session with { StoreCode = "S002", StoreName = "Second Store" };
        Assert.Equal(0, fixture.ViewModel.PendingChoices.Count);
    }

    [Fact]
    public void Disposing_the_page_discards_pending_choices_and_stops_listening_to_the_queue()
    {
        var fixture = CreateFixture();
        fixture.Scanner.Emit(Conflict);
        var queue = fixture.Workflow.PendingChoices!;

        fixture.ViewModel.Dispose();

        Assert.Equal(0, queue.Count);
        queue.TryEnqueue("930999", "raw", null, "S001", [fixture.Small]);
        Assert.Equal(0, fixture.ViewModel.PendingChoices.Count);
    }

    [Fact]
    public void Collapsing_keeps_the_pending_choice_visible_as_a_bar_and_resets_when_it_is_done()
    {
        var fixture = CreateFixture();
        fixture.Scanner.Emit(Conflict);
        var panel = fixture.ViewModel.PendingChoices;

        panel.ToggleCollapsedCommand.Execute(null);

        Assert.True(panel.HasPending);
        Assert.True(panel.IsCollapsedBarVisible);
        Assert.False(panel.IsExpandedVisible);
        Assert.StartsWith("1 pending - earliest ", panel.CollapsedBarText, StringComparison.Ordinal);

        // 收起期间又来一个待选：仍保持收起，只是数量变了。
        fixture.Scanner.Emit(Conflict);
        Assert.True(panel.IsCollapsedBarVisible);
        Assert.Equal(2, panel.Count);

        panel.SelectMatchCommand.Execute(fixture.Small);
        panel.SelectMatchCommand.Execute(fixture.Large);

        // 全部处理完恢复展开默认值，下一次待选不会一出来就是收起的。
        Assert.False(panel.HasPending);
        Assert.False(panel.IsCollapsed);
        Assert.Equal(string.Empty, panel.StatusHintText);
    }

    [Fact]
    public void Status_badge_text_and_prompt_texts_follow_the_pending_count()
    {
        var fixture = CreateFixture();
        var panel = fixture.ViewModel.PendingChoices;
        Assert.Equal(string.Empty, panel.StatusHintText);

        fixture.Scanner.Emit(Conflict);
        fixture.Scanner.Emit(Conflict);

        Assert.Equal("2 pending", panel.StatusHintText);
        Assert.Equal("Discard all (2)...", panel.AbandonAllText);
    }

    private static Fixture CreateFixture()
    {
        var index = new LocalSellableItemIndex();
        var small = CreateItem("SKU-CS", "Conflict Small", Conflict, 1.8m);
        var large = CreateItem("SKU-CL", "Conflict Large", Conflict, 2.8m);
        index.ReplaceAll(
        [
            small,
            large,
            CreateItem("SKU-110", "Scanner Apple", "930110", 1.5m),
            CreateItem("SKU-111", "Scanner Pear", "930111", 2.5m)
        ]);
        var cart = new PosCartService();
        var workflow = new PosTerminalWorkflowService(index, cart);
        var scanner = new FakeRawScannerService();
        var feedback = new RecordingUserFeedbackService();
        var time = new FakeTimeProvider();
        var paymentOpened = 0;
        var holdCalls = 0;
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: () => paymentOpened++,
            localization: new LocalizationService(),
            userFeedbackService: feedback,
            rawScannerService: scanner,
            workflowService: workflow,
            onHoldOrderAsync: () =>
            {
                holdCalls++;
                return Task.CompletedTask;
            },
            timeProvider: time);
        scanner.SetActivePage(PosTerminalViewModel.PageId);
        return new Fixture(viewModel, workflow, scanner, feedback, time, small, large, () => paymentOpened, () => holdCalls);
    }

    private static SellableItemDto CreateItem(string productCode, string name, string lookupCode, decimal price)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: name,
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: "store",
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static PosSessionState Session => new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    private sealed class Fixture(
        PosTerminalViewModel viewModel,
        PosTerminalWorkflowService workflow,
        FakeRawScannerService scanner,
        RecordingUserFeedbackService feedback,
        FakeTimeProvider time,
        SellableItemDto small,
        SellableItemDto large,
        Func<int> paymentOpened,
        Func<int> holdCalls)
    {
        public PosTerminalViewModel ViewModel { get; } = viewModel;

        public PosTerminalWorkflowService Workflow { get; } = workflow;

        public FakeRawScannerService Scanner { get; } = scanner;

        public RecordingUserFeedbackService Feedback { get; } = feedback;

        public FakeTimeProvider Time { get; } = time;

        public SellableItemDto Small { get; } = small;

        public SellableItemDto Large { get; } = large;

        public int PaymentOpened => paymentOpened();

        public int HoldCalls => holdCalls();
    }

    private sealed class RecordingUserFeedbackService : IUserFeedbackService
    {
        public List<UserFeedbackCue> Cues { get; } = [];

        public void Play(UserFeedbackCue cue)
        {
            Cues.Add(cue);
        }
    }

    private sealed class FakeRawScannerService : IRawScannerService
    {
        private readonly Dictionary<string, Action<RawBarcodeScannedEventArgs>> _handlers = [];
        private string? _activePageId;

        public bool IsActive { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void Subscribe(string pageId, Action<RawBarcodeScannedEventArgs> handler)
        {
            _handlers[pageId] = handler;
        }

        public void Unsubscribe(string pageId)
        {
            _handlers.Remove(pageId);
        }

        public void SetActivePage(string? pageId)
        {
            _activePageId = pageId;
        }

        public void Start(IntPtr hwnd)
        {
            IsActive = true;
        }

        public void Stop()
        {
            IsActive = false;
        }

        public void ClearPendingInput()
        {
        }

        public Task ResetBindingAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public IntPtr ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            return IntPtr.Zero;
        }

        public void Emit(string barcode)
        {
            if (_activePageId is not null && _handlers.TryGetValue(_activePageId, out var handler))
            {
                handler(new RawBarcodeScannedEventArgs(barcode, "scanner-device", DateTimeOffset.Now));
            }
        }

        public void Dispose()
        {
        }
    }
}
