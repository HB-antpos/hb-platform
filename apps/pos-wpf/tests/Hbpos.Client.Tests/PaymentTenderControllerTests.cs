using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

/// <summary>
/// 直接覆盖付款页加 tender 的入口计划：阻断条件的优先级、代金券券码校验、
/// 刷卡退款在未选中刷卡时改用默认可退额，以及引用（券码 / 原卡引用）的取值。
/// </summary>
public sealed class PaymentTenderControllerTests
{
    private readonly PaymentTenderController _controller = new();

    [Fact]
    public void Interaction_block_is_ignored_silently_before_any_other_check()
    {
        var probe = new RequestProbe();
        var request = probe.Create(PaymentMethodKind.Voucher) with
        {
            IsInteractionBlocked = true,
            HasPendingVoucherUpload = true,
            IsOfflineVoucherRefundUnavailable = true,
            HasDuplicateTender = true,
            BlockingCartIssueStatusKey = "cart.issue"
        };

        var plan = _controller.CreateAddTenderPlan(request);

        Assert.Equal(PaymentTenderAddPlan.Ignore(), plan);
        Assert.False(plan.ShouldProceed);
        Assert.Null(plan.StatusKey);
        Assert.False(plan.NotifyCommandStates);
        Assert.Equal(0, probe.TotalCalls);
    }

    [Fact]
    public void Blocking_conditions_are_reported_in_priority_order()
    {
        var probe = new RequestProbe();
        var request = probe.Create(PaymentMethodKind.Card) with
        {
            HasPendingVoucherUpload = true,
            IsOfflineVoucherRefundUnavailable = true,
            HasDuplicateTender = true,
            BlockingCartIssueStatusKey = "cart.issue"
        };

        Assert.Equal("payment.status.retryVoucherUpload", _controller.CreateAddTenderPlan(request).StatusKey);

        request = request with { HasPendingVoucherUpload = false };
        Assert.Equal("payment.refund.status.voucherOfflineUnavailable", _controller.CreateAddTenderPlan(request).StatusKey);

        request = request with { IsOfflineVoucherRefundUnavailable = false };
        Assert.Equal("payment.status.duplicatePaymentMethod", _controller.CreateAddTenderPlan(request).StatusKey);

        request = request with { HasDuplicateTender = false };
        Assert.Equal("cart.issue", _controller.CreateAddTenderPlan(request).StatusKey);

        Assert.Equal(0, probe.TotalCalls);
    }

    [Fact]
    public void Pending_voucher_upload_blocks_without_changing_selected_method()
    {
        var plan = _controller.CreateAddTenderPlan(new RequestProbe().Create(PaymentMethodKind.Cash) with
        {
            HasPendingVoucherUpload = true
        });

        Assert.False(plan.ShouldProceed);
        Assert.Null(plan.SelectedPaymentMethod);
        Assert.True(plan.NotifyCommandStates);
        Assert.Null(plan.AmountText);
        Assert.Null(plan.ReferenceText);
    }

    [Fact]
    public void Offline_voucher_refund_and_cart_issue_block_without_selecting_method()
    {
        var offline = _controller.CreateAddTenderPlan(new RequestProbe().Create(PaymentMethodKind.Voucher, isRefundMode: true) with
        {
            IsOfflineVoucherRefundUnavailable = true
        });
        var cartIssue = _controller.CreateAddTenderPlan(new RequestProbe().Create(PaymentMethodKind.Cash) with
        {
            BlockingCartIssueStatusKey = "payment.status.cartHasInvalidLines"
        });

        Assert.Equal("payment.refund.status.voucherOfflineUnavailable", offline.StatusKey);
        Assert.Null(offline.SelectedPaymentMethod);
        Assert.True(offline.NotifyCommandStates);
        Assert.Equal("payment.status.cartHasInvalidLines", cartIssue.StatusKey);
        Assert.Null(cartIssue.SelectedPaymentMethod);
        Assert.True(cartIssue.NotifyCommandStates);
    }

    [Fact]
    public void Duplicate_tender_selects_requested_method_so_user_sees_existing_line()
    {
        var plan = _controller.CreateAddTenderPlan(new RequestProbe().Create(PaymentMethodKind.Voucher) with
        {
            HasDuplicateTender = true
        });

        Assert.False(plan.ShouldProceed);
        Assert.Equal("payment.status.duplicatePaymentMethod", plan.StatusKey);
        Assert.Equal(PaymentMethodKind.Voucher, plan.SelectedPaymentMethod);
        Assert.True(plan.NotifyCommandStates);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Voucher_sale_without_code_is_blocked_and_selects_voucher(string voucherCode)
    {
        var probe = new RequestProbe();
        var plan = _controller.CreateAddTenderPlan(probe.Create(PaymentMethodKind.Voucher, voucherCode: voucherCode));

        Assert.False(plan.ShouldProceed);
        Assert.Equal("payment.status.voucherCodeRequired", plan.StatusKey);
        Assert.Equal(PaymentMethodKind.Voucher, plan.SelectedPaymentMethod);
        Assert.True(plan.NotifyCommandStates);
        Assert.Equal(0, probe.TotalCalls);
    }

    [Fact]
    public void Voucher_sale_with_code_uses_typed_amount_and_voucher_code_as_reference()
    {
        var probe = new RequestProbe(typedAmount: "12.50", defaultAmount: "99.00", refundReference: "ANZ:SHOULD-NOT-BE-USED");

        var plan = _controller.CreateAddTenderPlan(probe.Create(PaymentMethodKind.Voucher, voucherCode: " VC-001 "));

        Assert.Equal(PaymentTenderAddPlan.Ready(PaymentMethodKind.Voucher, "12.50", " VC-001 "), plan);
        Assert.Equal(new[] { "typed:Voucher" }, probe.Calls);
    }

    [Fact]
    public void Voucher_refund_does_not_require_code_and_passes_through_voucher_text()
    {
        var probe = new RequestProbe(typedAmount: "8.00");

        var plan = _controller.CreateAddTenderPlan(probe.Create(PaymentMethodKind.Voucher, isRefundMode: true, voucherCode: string.Empty));

        Assert.True(plan.ShouldProceed);
        Assert.Equal("8.00", plan.AmountText);
        Assert.Equal(string.Empty, plan.ReferenceText);
        Assert.Equal(new[] { "typed:Voucher" }, probe.Calls);
    }

    [Fact]
    public void Cash_sale_uses_typed_amount_and_no_reference()
    {
        var probe = new RequestProbe(typedAmount: "20.00", refundReference: "ANZ:IGNORED");

        var plan = _controller.CreateAddTenderPlan(probe.Create(PaymentMethodKind.Cash, voucherCode: "VC-IGNORED"));

        Assert.Equal(PaymentTenderAddPlan.Ready(PaymentMethodKind.Cash, "20.00", null), plan);
        Assert.Equal(new[] { "typed:Cash" }, probe.Calls);
    }

    [Fact]
    public void Card_sale_does_not_look_up_refund_reference()
    {
        var probe = new RequestProbe(typedAmount: "15.00", refundReference: "ANZ:IGNORED");

        var plan = _controller.CreateAddTenderPlan(probe.Create(PaymentMethodKind.Card, selected: PaymentMethodKind.Cash));

        Assert.Equal(PaymentTenderAddPlan.Ready(PaymentMethodKind.Card, "15.00", null), plan);
        // 销售模式下即使未选中刷卡，也用输入金额而不是默认金额。
        Assert.Equal(new[] { "typed:Card" }, probe.Calls);
    }

    [Fact]
    public void Card_refund_switching_from_other_method_uses_default_refund_amount_and_original_reference()
    {
        // 从现金切到刷卡退款时输入框里是现金金额，必须改用刷卡可退默认额，避免超出原卡容量。
        var probe = new RequestProbe(typedAmount: "50.00", defaultAmount: "12.30", refundReference: "ANZ:TXN-ORIGINAL");

        var plan = _controller.CreateAddTenderPlan(probe.Create(
            PaymentMethodKind.Card,
            selected: PaymentMethodKind.Cash,
            isRefundMode: true));

        Assert.Equal(PaymentTenderAddPlan.Ready(PaymentMethodKind.Card, "12.30", "ANZ:TXN-ORIGINAL"), plan);
        Assert.Equal(new[] { "default:Card", "reference:Card" }, probe.Calls);
    }

    [Fact]
    public void Card_refund_already_selected_uses_typed_amount_and_original_reference()
    {
        var probe = new RequestProbe(typedAmount: "5.00", defaultAmount: "12.30", refundReference: "ANZ:TXN-ORIGINAL");

        var plan = _controller.CreateAddTenderPlan(probe.Create(
            PaymentMethodKind.Card,
            selected: PaymentMethodKind.Card,
            isRefundMode: true));

        Assert.Equal(PaymentTenderAddPlan.Ready(PaymentMethodKind.Card, "5.00", "ANZ:TXN-ORIGINAL"), plan);
        Assert.Equal(new[] { "typed:Card", "reference:Card" }, probe.Calls);
    }

    [Fact]
    public void Card_refund_without_original_reference_still_produces_plan_with_null_reference()
    {
        // 控制器只整理参数；缺原卡引用的拦截在 CardStrategy / 工作流里完成。
        var probe = new RequestProbe(typedAmount: "5.00", refundReference: null);

        var plan = _controller.CreateAddTenderPlan(probe.Create(PaymentMethodKind.Card, selected: PaymentMethodKind.Card, isRefundMode: true));

        Assert.True(plan.ShouldProceed);
        Assert.Null(plan.ReferenceText);
    }

    [Theory]
    [InlineData(PaymentMethodKind.Card, PaymentMethodKind.Cash, true, true)]
    [InlineData(PaymentMethodKind.Card, PaymentMethodKind.Voucher, true, true)]
    [InlineData(PaymentMethodKind.Card, PaymentMethodKind.Card, true, false)]
    [InlineData(PaymentMethodKind.Card, PaymentMethodKind.Cash, false, false)]
    [InlineData(PaymentMethodKind.Cash, PaymentMethodKind.Card, true, false)]
    [InlineData(PaymentMethodKind.Voucher, PaymentMethodKind.Cash, true, false)]
    public void Method_default_amount_is_used_only_for_card_refund_when_card_is_not_selected(
        PaymentMethodKind method,
        PaymentMethodKind selected,
        bool isRefundMode,
        bool expected)
    {
        var request = new RequestProbe().Create(method, selected, isRefundMode);

        Assert.Equal(expected, request.ShouldUseMethodDefaultAmount);
    }

    [Fact]
    public async Task Quick_cash_sets_two_decimal_amount_then_adds_cash_tender()
    {
        var events = new List<string>();

        await _controller.ApplyQuickCashAsync(
            new QuickCashOption(50m, "$50", "note", "fg"),
            text => events.Add("text:" + text),
            method =>
            {
                events.Add("add:" + method);
                return Task.CompletedTask;
            });

        // 金额文本按当前文化格式化，与付款页解析 TenderAmountText 的文化一致。
        Assert.Equal(new[] { "text:" + 50m.ToString("0.00"), "add:Cash" }, events);
    }

    [Fact]
    public async Task Quick_cash_rounds_amount_text_to_two_decimals()
    {
        string? amountText = null;

        await _controller.ApplyQuickCashAsync(
            new QuickCashOption(7.456m, "odd", "note", "fg"),
            text => amountText = text,
            _ => Task.CompletedTask);

        Assert.NotNull(amountText);
        Assert.Equal(7.46m.ToString("0.00"), amountText);
    }

    [Fact]
    public async Task Quick_cash_without_option_does_nothing()
    {
        var calls = 0;

        await _controller.ApplyQuickCashAsync(
            null,
            _ => calls++,
            _ =>
            {
                calls++;
                return Task.CompletedTask;
            });

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Quick_cash_awaits_add_tender_completion()
    {
        var addStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var quickCash = _controller.ApplyQuickCashAsync(
            new QuickCashOption(20m, "$20", "note", "fg"),
            _ => { },
            async _ =>
            {
                addStarted.SetResult();
                await releaseAdd.Task;
            });

        await addStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.False(quickCash.IsCompleted);

        releaseAdd.SetResult();
        await quickCash.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
    }

    private sealed class RequestProbe(
        string typedAmount = "1.00",
        string defaultAmount = "2.00",
        string? refundReference = "ANZ:TXN-DEFAULT")
    {
        public List<string> Calls { get; } = [];

        public int TotalCalls => Calls.Count;

        public PaymentTenderAddRequest Create(
            PaymentMethodKind method,
            PaymentMethodKind? selected = null,
            bool isRefundMode = false,
            string voucherCode = "VC-DEFAULT") =>
            new(
                method,
                selected ?? method,
                IsInteractionBlocked: false,
                HasPendingVoucherUpload: false,
                isRefundMode,
                IsOfflineVoucherRefundUnavailable: false,
                HasDuplicateTender: false,
                BlockingCartIssueStatusKey: null,
                voucherCode,
                ResolveTenderAmountText: requested =>
                {
                    Calls.Add("typed:" + requested);
                    return typedAmount;
                },
                ResolveDefaultTenderAmountText: requested =>
                {
                    Calls.Add("default:" + requested);
                    return defaultAmount;
                },
                GetRefundReference: requested =>
                {
                    Calls.Add("reference:" + requested);
                    return refundReference;
                });
    }
}
