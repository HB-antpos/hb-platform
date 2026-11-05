using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.Zeller;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>
/// Zeller 扣款客户端：先落库、按结果分类、按 reference 核实、结果未知时自动重查、未付款后迟到复核。
/// 网关用脚本代替，尝试表用真实 SQLite。每条用例对应真机验证里的一种结局。
/// </summary>
public sealed class ZellerCardTerminalClientTests
{
    private const string Uuid = "65c4b343-03e6-49ef-a54d-012e3f2c222c";
    private static readonly ZellerPurchaseContext Context = new(100, "{\"draft\":1}", "S001", "POS-01", "C001");

    // ── 直接有结论 ──

    [Fact]
    public async Task Approved_purchase_is_saved_with_the_transaction_and_needs_no_query()
    {
        await using var harness = new Harness();
        var progress = new SafeList<ZellerPaymentProgress>();
        harness.Gateway.OnPurchase = (request, events, _) =>
        {
            events?.Report(new ZellerTerminalEvent(ZellerTerminalEventKind.AwaitingPaymentMethod, "AwaitingPaymentMethod"));
            events?.Report(new ZellerTerminalEvent(ZellerTerminalEventKind.Processing, "Processing"));
            return Task.FromResult(ZellerCallResult.FromTransaction(Approved(request.Reference)));
        };

        var outcome = await harness.Client.PurchaseAsync(Context, new SyncProgress<ZellerPaymentProgress>(progress.Add));
        var attempt = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.Approved, outcome.State);
        Assert.StartsWith("HBZ-", outcome.Reference, StringComparison.Ordinal);
        Assert.Equal($"ZELLER:{outcome.Reference}:{Uuid}", outcome.PaymentReference);
        Assert.Equal(ZellerAttemptStatus.Approved, attempt.Status);
        Assert.Equal(Uuid, attempt.TransactionUuid);
        Assert.Equal("456321", attempt.ApprovalCode);
        Assert.Equal("RECEIPT", attempt.ReceiptText);
        Assert.True(attempt.ProcessingStarted);
        Assert.NotNull(attempt.CompletedAt);
        Assert.Equal(2, attempt.Version);
        Assert.Equal(0, harness.Gateway.QueryCount);
        Assert.Equal(
            [ZellerPaymentProgressKind.Submitted, ZellerPaymentProgressKind.AwaitingCard, ZellerPaymentProgressKind.Processing],
            progress.ToArray().Select(item => item.Kind));
    }

    [Fact]
    public async Task Attempt_is_saved_as_submitted_before_the_terminal_is_called()
    {
        await using var harness = new Harness();
        IReadOnlyList<LocalZellerPaymentAttempt>? seenByTerminal = null;
        string? sentReference = null;
        harness.Gateway.OnPurchase = async (request, _, _) =>
        {
            sentReference = request.Reference;
            seenByTerminal = await harness.Attempts.ListOpenAsync("S001", "POS-01");
            return ZellerCallResult.FromTransaction(Approved(request.Reference));
        };

        await harness.Client.PurchaseAsync(Context);

        var saved = Assert.Single(seenByTerminal!);
        Assert.Equal(ZellerAttemptStatus.Submitted, saved.Status);
        Assert.Equal(sentReference, saved.Reference);
        Assert.Equal(100, saved.AmountCents);
        Assert.Equal("{\"draft\":1}", saved.OrderDraftJson);
    }

    [Fact]
    public async Task Every_purchase_uses_a_new_reference()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(Approved(request.Reference)));

        var first = await harness.Client.PurchaseAsync(Context);
        var second = await harness.Client.PurchaseAsync(Context);

        Assert.NotEqual(first.Reference, second.Reference);
        Assert.NotEqual(first.AttemptGuid, second.AttemptGuid);
    }

    [Fact]
    public async Task Declined_purchase_is_declined_without_a_query()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(
            Approved(request.Reference) with { Status = ZellerTransactionStatus.Declined, ResponseCode = "51", ResponseText = "Insufficient funds" }));

        var outcome = await harness.Client.PurchaseAsync(Context);
        var attempt = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.Declined, outcome.State);
        Assert.Equal(ZellerFailureKind.BankDeclined, outcome.FailureKind);
        Assert.Equal(ZellerAttemptStatus.Declined, attempt.Status);
        Assert.Equal("51", attempt.ResponseCode);
        Assert.Null(outcome.PaymentReference);
        Assert.Equal(0, harness.Gateway.QueryCount);
    }

    [Fact]
    public async Task Request_that_never_left_the_pos_is_not_paid_without_a_query_or_recheck()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.OperationRequestInvalid));

        var outcome = await harness.Client.PurchaseAsync(Context);
        var attempt = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.NotPaid, outcome.State);
        Assert.Equal(ZellerFailureKind.InvalidRequest, outcome.FailureKind);
        Assert.Equal(ZellerAttemptStatus.Canceled, attempt.Status);
        Assert.Null(attempt.LateRecheckDueAt);
        Assert.Equal(0, harness.Gateway.QueryCount);
    }

    [Fact]
    public async Task Cancellation_before_submission_closes_the_attempt_without_calling_the_terminal()
    {
        await using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var outcome = await harness.Client.PurchaseAsync(Context, cancellationToken: cancellation.Token);
        var attempt = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.NotPaid, outcome.State);
        Assert.Equal(ZellerFailureKind.Cancelled, outcome.FailureKind);
        Assert.Equal(ZellerAttemptStatus.Canceled, attempt.Status);
        Assert.Empty(harness.Gateway.Calls.ToArray());
    }

    // ── 查一次再定 ──

    [Fact]
    public async Task Cancelled_purchase_with_no_transaction_is_not_paid_and_rechecked_a_minute_later()
    {
        // 真机 S5、S6：取消后按 reference 查到 0 笔。
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.Cancelled, reachedTerminal: true));
        harness.Gateway.OnQuery = (_, _) => ZellerQueryResult.Found();

        var outcome = await harness.Client.PurchaseAsync(Context);
        var attempt = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.NotPaid, outcome.State);
        Assert.Equal(ZellerFailureKind.Cancelled, outcome.FailureKind);
        Assert.Equal(ZellerAttemptStatus.Canceled, attempt.Status);
        Assert.Equal(harness.Time.GetUtcNow().AddSeconds(60), attempt.LateRecheckDueAt);
        Assert.Equal(1, harness.Gateway.QueryCount);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(5));
        Assert.Equal(1, harness.Gateway.QueryCount);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        await WaitUntilAsync(async () => (await harness.AttemptAsync(outcome)).LateRecheckedAt is not null);
        var rechecked = await harness.AttemptAsync(outcome);

        Assert.Equal(2, harness.Gateway.QueryCount);
        Assert.Equal(ZellerAttemptStatus.Canceled, rechecked.Status);
    }

    [Fact]
    public async Task Late_recheck_that_finds_an_approval_reopens_the_attempt_as_unknown()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.InactivityTimeout, reachedTerminal: true));
        harness.Gateway.OnQuery = (reference, index) => index == 0 ? ZellerQueryResult.Found() : ZellerQueryResult.Found(Queried(reference));

        var outcome = await harness.Client.PurchaseAsync(Context);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));
        await WaitUntilAsync(async () => (await harness.AttemptAsync(outcome)).Status == ZellerAttemptStatus.Unknown);
        var reopened = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.NotPaid, outcome.State);
        Assert.Equal(ZellerUnknownReason.LateApproval, reopened.UnknownReason);
        Assert.Equal(Uuid, reopened.TransactionUuid);
        Assert.NotNull(reopened.LateRecheckedAt);
    }

    [Fact]
    public async Task Timeout_with_an_approval_found_by_the_query_is_approved()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.InactivityTimeout, reachedTerminal: true));
        harness.Gateway.OnQuery = (reference, _) => ZellerQueryResult.Found(Queried(reference));

        var outcome = await harness.Client.PurchaseAsync(Context);
        var attempt = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.Approved, outcome.State);
        Assert.Equal(ZellerAttemptStatus.Approved, attempt.Status);
        Assert.Null(attempt.ApprovalCode);
        Assert.Null(attempt.ReceiptText);
        Assert.Equal(1, attempt.VerificationRounds);
        Assert.Null(attempt.LateRecheckDueAt);
    }

    [Fact]
    public async Task Card_error_confirmed_by_the_query_is_declined()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.CardReadFailure, reachedTerminal: true));
        harness.Gateway.OnQuery = (_, _) => ZellerQueryResult.Found();

        var outcome = await harness.Client.PurchaseAsync(Context);

        Assert.Equal(ZellerPaymentState.Declined, outcome.State);
        Assert.Equal(ZellerFailureKind.CardProblem, outcome.FailureKind);
    }

    [Fact]
    public async Task Cancelled_purchase_whose_query_fails_keeps_polling_instead_of_assuming_not_paid()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.Cancelled, reachedTerminal: true));
        harness.Gateway.OnQuery = (reference, index) => index == 0
            ? ZellerQueryResult.Failed(ZellerErrorType.NetworkFailure)
            : ZellerQueryResult.Found();

        var purchase = harness.Client.PurchaseAsync(Context);
        await WaitUntilAsync(() => Task.FromResult(harness.Gateway.QueryCount >= 2));
        Assert.False(purchase.IsCompleted);
        await harness.AdvanceAsync(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(5));
        var outcome = await purchase.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ZellerPaymentState.Unknown, outcome.State);
        Assert.Equal(ZellerUnknownReason.VerificationFailed, outcome.UnknownReason);
    }

    // ── 结果未知：自动重查 ──

    [Fact]
    public async Task Network_failure_polls_until_the_approval_appears()
    {
        // 真机 S8：NetworkFailure 时钱已经扣了，网络恢复后按 reference 查到。
        await using var harness = new Harness();
        var progress = new SafeList<ZellerPaymentProgress>();
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(
            ZellerCallResult.FromError(ZellerErrorType.NetworkFailure, reachedTerminal: true, processingStarted: true));
        harness.Gateway.OnQuery = (reference, index) => index < 2 ? ZellerQueryResult.Found() : ZellerQueryResult.Found(Queried(reference));

        var purchase = harness.Client.PurchaseAsync(Context, new SyncProgress<ZellerPaymentProgress>(progress.Add));
        await harness.AdvanceUntilAsync(() => purchase.IsCompleted, TimeSpan.FromSeconds(1));
        var outcome = await purchase;
        var attempt = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.Approved, outcome.State);
        Assert.Equal(3, harness.Gateway.QueryCount);
        Assert.Equal(3, attempt.VerificationRounds);
        Assert.True(attempt.ProcessingStarted);
        Assert.Equal(3, attempt.Version); // 创建 → 已发出 → 结果未知 → 批准
        Assert.Equal(
            [1, 2, 3],
            progress.ToArray().Where(item => item.Kind == ZellerPaymentProgressKind.Verifying).Select(item => item.VerificationRound));
    }

    [Fact]
    public async Task Unknown_result_is_left_for_recovery_when_the_window_runs_out()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.NetworkFailure, reachedTerminal: true));
        harness.Gateway.OnQuery = (_, _) => ZellerQueryResult.Found();

        var purchase = harness.Client.PurchaseAsync(Context);
        await harness.AdvanceAsync(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(5));
        var outcome = await purchase.WaitAsync(TimeSpan.FromSeconds(10));
        var attempt = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.Unknown, outcome.State);
        Assert.Equal(ZellerUnknownReason.SdkError, outcome.UnknownReason);
        Assert.Equal(ZellerAttemptStatus.Unknown, attempt.Status);
        Assert.InRange(harness.Gateway.QueryCount, 30, 37);
        Assert.Equal(harness.Gateway.QueryCount, attempt.VerificationRounds);
        Assert.Contains(attempt, await harness.Attempts.ListOpenAsync("S001", "POS-01"));
    }

    [Fact]
    public async Task Abandoned_call_resets_the_terminal_before_the_first_query()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.Abandoned(reachedTerminal: true));
        harness.Gateway.OnQuery = (reference, _) => ZellerQueryResult.Found(Queried(reference));

        var outcome = await harness.Client.PurchaseAsync(Context);

        Assert.Equal(ZellerPaymentState.Approved, outcome.State);
        Assert.Equal(["purchase", "reset", "query"], harness.Gateway.Calls.ToArray().Select(call => call.Split(':')[0]));
    }

    [Fact]
    public async Task Transaction_returned_for_another_reference_resets_then_polls_our_reference()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(Approved("HBZ-00000000000000000000000000000009")));
        harness.Gateway.OnQuery = (reference, _) => ZellerQueryResult.Found(Queried(reference));

        var outcome = await harness.Client.PurchaseAsync(Context);

        Assert.Equal(ZellerPaymentState.Approved, outcome.State);
        Assert.Equal(["purchase", "reset", "query"], harness.Gateway.Calls.ToArray().Select(call => call.Split(':')[0]));
        Assert.Equal($"query:{outcome.Reference}", harness.Gateway.Calls.ToArray()[2]);
    }

    [Fact]
    public async Task Gateway_exception_is_treated_as_unknown_and_resolved_by_querying()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (_, _, _) => throw new InvalidOperationException("unexpected");
        harness.Gateway.OnQuery = (reference, _) => ZellerQueryResult.Found(Queried(reference));

        var outcome = await harness.Client.PurchaseAsync(Context);

        Assert.Equal(ZellerPaymentState.Approved, outcome.State);
        Assert.Equal(1, harness.Gateway.ResetCount);
    }

    [Fact]
    public async Task Two_approvals_found_while_polling_go_straight_to_a_supervisor()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.NetworkFailure, reachedTerminal: true));
        harness.Gateway.OnQuery = (reference, _) => ZellerQueryResult.Found(
            Queried(reference),
            Queried(reference) with { TransactionUuid = "98b690b7-5eaa-41ec-8ff8-168c60364aac" });

        var outcome = await harness.Client.PurchaseAsync(Context);
        var attempt = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.Unknown, outcome.State);
        Assert.Equal(ZellerUnknownReason.MultipleApproved, outcome.UnknownReason);
        Assert.Equal(ZellerUnknownReason.MultipleApproved, attempt.UnknownReason);
        Assert.Equal(1, harness.Gateway.QueryCount);
    }

    [Fact]
    public async Task Approval_for_a_different_amount_needs_a_supervisor_without_polling()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (request, _, _) => Task.FromResult(
            ZellerCallResult.FromTransaction(Approved(request.Reference) with { AmountCents = 1000 }));

        var outcome = await harness.Client.PurchaseAsync(Context);
        var attempt = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.Unknown, outcome.State);
        Assert.Equal(ZellerUnknownReason.AmountMismatch, outcome.UnknownReason);
        Assert.Equal(Uuid, attempt.TransactionUuid);
        Assert.Equal(0, harness.Gateway.QueryCount);
    }

    [Fact]
    public async Task Cashier_stopping_the_wait_leaves_the_attempt_unknown()
    {
        await using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.NetworkFailure, reachedTerminal: true));
        harness.Gateway.OnQuery = (_, _) => ZellerQueryResult.Found();

        var purchase = harness.Client.PurchaseAsync(Context, cancellationToken: cancellation.Token);
        await WaitUntilAsync(() => Task.FromResult(harness.Gateway.QueryCount >= 1));
        cancellation.Cancel();
        var outcome = await purchase.WaitAsync(TimeSpan.FromSeconds(10));
        var attempt = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.Unknown, outcome.State);
        Assert.Equal(ZellerAttemptStatus.Unknown, attempt.Status);
        Assert.Equal(1, attempt.VerificationRounds);
    }

    // ── 建单与复核补做 ──

    [Fact]
    public async Task Only_an_approved_attempt_can_be_marked_as_order_completed()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(Approved(request.Reference)));
        var approved = await harness.Client.PurchaseAsync(Context);
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.OperationRequestInvalid));
        var notPaid = await harness.Client.PurchaseAsync(Context);

        Assert.True(await harness.Client.MarkOrderCompletedAsync(approved.AttemptGuid));
        Assert.False(await harness.Client.MarkOrderCompletedAsync(approved.AttemptGuid));
        Assert.False(await harness.Client.MarkOrderCompletedAsync(notPaid.AttemptGuid));
        Assert.False(await harness.Client.MarkOrderCompletedAsync(Guid.NewGuid()));
        Assert.Equal(ZellerAttemptStatus.OrderCompleted, (await harness.AttemptAsync(approved)).Status);
    }

    [Fact]
    public async Task Late_rechecks_that_came_due_while_the_pos_was_closed_are_done_on_demand()
    {
        await using var harness = new Harness();
        var guid = Guid.NewGuid();
        var created = harness.Time.GetUtcNow();
        var pending = new LocalZellerPaymentAttempt(
            guid, ZellerAttemptOperation.Sale, $"HBZ-{guid:N}", 100, ZellerAttemptStatus.Pending,
            "{}", "S001", "POS-01", "C001", created, created);
        await harness.Attempts.CreateAsync(pending);
        var submitted = await harness.Attempts.TryUpdateAsync(pending, pending with { Status = ZellerAttemptStatus.Submitted });
        await harness.Attempts.TryUpdateAsync(submitted!, submitted! with
        {
            Status = ZellerAttemptStatus.Canceled,
            FailureKind = ZellerFailureKind.Cancelled,
            LateRecheckDueAt = created.AddSeconds(-1)
        });
        harness.Gateway.OnQuery = (_, _) => ZellerQueryResult.Found();

        var count = await harness.Client.RunDueLateRechecksAsync();
        var again = await harness.Client.RunDueLateRechecksAsync();

        Assert.Equal(1, count);
        Assert.Equal(0, again);
        Assert.NotNull((await harness.Attempts.GetAsync(guid))!.LateRecheckedAt);
    }

    [Fact]
    public async Task Late_recheck_that_cannot_query_stays_due_for_a_later_try()
    {
        await using var harness = new Harness();
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.Cancelled, reachedTerminal: true));
        harness.Gateway.OnQuery = (_, index) => index == 0 ? ZellerQueryResult.Found() : ZellerQueryResult.Failed(ZellerErrorType.NetworkFailure);
        var outcome = await harness.Client.PurchaseAsync(Context);

        var recheck = await harness.Client.RecheckAsync(outcome.AttemptGuid);
        var attempt = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.NotPaid, recheck!.State);
        Assert.Null(attempt.LateRecheckedAt);
        Assert.Equal(ZellerAttemptStatus.Canceled, attempt.Status);
    }

    // ── 退款 ──

    private const string OriginalPayment = $"ZELLER:HBZ-799287a33dc24df1bca3d33eea19b35a:{Uuid}";
    private const string RefundUuid = "28b1e54e-a420-41d6-bb8e-2a7497bf783d";
    private static readonly ZellerRefundContext Refund = new(100, OriginalPayment, "{\"return\":1}", "S001", "POS-01", "C001");

    private static ZellerTransaction RefundApproved(string reference) => new(
        ZellerTransactionStatus.Approved,
        ZellerTransactionType.Refund,
        100,
        RefundUuid,
        reference,
        ResponseCode: "00",
        ResponseText: "APPROVED",
        Rrn: "629266629008",
        Scheme: "OTHER",
        ReceiptText: "REFUND RECEIPT");

    [Fact]
    public async Task Approved_refund_targets_the_original_transaction_uuid_and_returns_a_refund_reference()
    {
        // 真机 S13：按原交易的 transactionUuid 退款成功，不需要刷卡。
        await using var harness = new Harness();
        harness.Gateway.OnRefund = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(RefundApproved(request.Reference)));

        var outcome = await harness.Client.RefundAsync(Refund);
        var attempt = await harness.AttemptAsync(outcome);
        var request = Assert.Single(harness.Gateway.RefundRequests.ToArray());

        Assert.Equal(ZellerPaymentState.Approved, outcome.State);
        Assert.Equal(ZellerAttemptOperation.Refund, outcome.Operation);
        Assert.StartsWith("HBZR-", outcome.Reference, StringComparison.Ordinal);
        Assert.Equal($"ZELLERRF:{outcome.Reference}:{RefundUuid}", outcome.PaymentReference);
        Assert.Equal(Uuid, request.OriginalTransactionUuid);
        Assert.Equal(outcome.Reference, request.Reference);
        Assert.Equal(100, request.AmountCents);
        Assert.Equal(ZellerAttemptOperation.Refund, attempt.OperationKind);
        Assert.Equal(Uuid, attempt.OriginalTransactionUuid);
        Assert.Equal(RefundUuid, attempt.TransactionUuid);
        Assert.Equal(ZellerAttemptStatus.Approved, attempt.Status);
    }

    [Fact]
    public async Task Refund_accepts_the_original_reference_inside_a_card_refund_wrapper()
    {
        await using var harness = new Harness();
        harness.Gateway.OnRefund = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(RefundApproved(request.Reference)));

        var outcome = await harness.Client.RefundAsync(Refund with
        {
            OriginalPaymentReference = Hbpos.Contracts.Orders.CardRefundReference.Format(null, OriginalPayment)
        });

        Assert.Equal(ZellerPaymentState.Approved, outcome.State);
        Assert.Equal(Uuid, Assert.Single(harness.Gateway.RefundRequests.ToArray()).OriginalTransactionUuid);
    }

    [Theory]
    [InlineData("SQ:payment-1")]
    [InlineData("ANZ:TXN-1")]
    [InlineData("ZELLER:broken")]
    [InlineData("")]
    public async Task Refund_of_a_payment_that_is_not_a_zeller_payment_is_rejected_before_anything_is_saved(string original)
    {
        await using var harness = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Client.RefundAsync(Refund with { OriginalPaymentReference = original }));
        Assert.Empty(harness.Gateway.Calls.ToArray());
    }

    [Fact]
    public async Task Refund_above_what_is_left_is_declined()
    {
        // 真机 S13：超额退款返回成交类响应，Status=Declined，code 13。
        await using var harness = new Harness();
        harness.Gateway.OnRefund = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(
            RefundApproved(request.Reference) with { Status = ZellerTransactionStatus.Declined, ResponseCode = "13", ResponseText = "Invalid amount" }));

        var outcome = await harness.Client.RefundAsync(Refund);

        Assert.Equal(ZellerPaymentState.Declined, outcome.State);
        Assert.Equal("13", outcome.Transaction!.ResponseCode);
        Assert.Null(outcome.PaymentReference);
        Assert.Equal(0, harness.Gateway.QueryCount);
    }

    [Fact]
    public async Task Refund_of_a_missing_original_is_not_refunded_and_not_retried()
    {
        await using var harness = new Harness();
        harness.Gateway.OnRefund = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.TransactionNotFound));

        var outcome = await harness.Client.RefundAsync(Refund);
        var attempt = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.NotPaid, outcome.State);
        Assert.Equal(ZellerFailureKind.OriginalTransactionNotFound, outcome.FailureKind);
        Assert.Equal(ZellerAttemptStatus.Canceled, attempt.Status);
        Assert.Null(attempt.LateRecheckDueAt);
        Assert.Equal(0, harness.Gateway.QueryCount);
        Assert.Single(harness.Gateway.RefundRequests.ToArray());
    }

    [Fact]
    public async Task Unknown_refund_is_verified_by_the_refund_reference_and_never_sent_again()
    {
        await using var harness = new Harness();
        harness.Gateway.OnRefund = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.NetworkFailure, reachedTerminal: true));
        harness.Gateway.OnQuery = (reference, index) => index < 1 ? ZellerQueryResult.Found() : ZellerQueryResult.Found(RefundApproved(reference));

        var refund = harness.Client.RefundAsync(Refund);
        await harness.AdvanceUntilAsync(() => refund.IsCompleted, TimeSpan.FromSeconds(1));
        var outcome = await refund;

        Assert.Equal(ZellerPaymentState.Approved, outcome.State);
        Assert.Single(harness.Gateway.RefundRequests.ToArray());
        Assert.All(
            harness.Gateway.Calls.ToArray().Where(call => call.StartsWith("query:", StringComparison.Ordinal)),
            call => Assert.Equal($"query:{outcome.Reference}", call));
    }

    [Fact]
    public async Task Refund_verification_ignores_the_original_purchase_under_the_same_query()
    {
        await using var harness = new Harness();
        harness.Gateway.OnRefund = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.Cancelled, reachedTerminal: true));
        // 查询结果里混进一笔扣款（类型不同），不能被当成退款已完成。
        harness.Gateway.OnQuery = (reference, _) => ZellerQueryResult.Found(Approved(reference));

        var outcome = await harness.Client.RefundAsync(Refund);

        Assert.Equal(ZellerPaymentState.NotPaid, outcome.State);
    }

    [Fact]
    public async Task Second_refund_for_the_same_return_while_the_first_is_unknown_is_not_sent()
    {
        await using var harness = new Harness();
        harness.Gateway.OnRefund = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.NetworkFailure, reachedTerminal: true));
        harness.Gateway.OnQuery = (_, _) => ZellerQueryResult.Found();
        using var stopWaiting = new CancellationTokenSource();
        var keyed = Refund with { RefundBusinessKey = "RETURN-1:LINE-1" };

        var first = harness.Client.RefundAsync(keyed, cancellationToken: stopWaiting.Token);
        await WaitUntilAsync(() => Task.FromResult(harness.Gateway.QueryCount >= 1));
        stopWaiting.Cancel();
        var firstOutcome = await first.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await harness.Client.RefundAsync(keyed);

        Assert.Equal(ZellerPaymentState.Unknown, firstOutcome.State);
        Assert.Equal(ZellerPaymentState.Unknown, second.State);
        Assert.Equal(ZellerUnknownReason.RefundAlreadyInProgress, second.UnknownReason);
        Assert.Equal(firstOutcome.AttemptGuid, second.AttemptGuid);
        Assert.Single(harness.Gateway.RefundRequests.ToArray());
    }

    [Fact]
    public async Task Refund_for_the_same_return_is_allowed_again_once_the_first_was_declined()
    {
        await using var harness = new Harness();
        harness.Gateway.OnRefund = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(
            RefundApproved(request.Reference) with { Status = ZellerTransactionStatus.Declined, ResponseCode = "13" }));
        var keyed = Refund with { RefundBusinessKey = "RETURN-2:LINE-1" };

        var first = await harness.Client.RefundAsync(keyed);
        harness.Gateway.OnRefund = (request, _, _) => Task.FromResult(ZellerCallResult.FromTransaction(RefundApproved(request.Reference)));
        var second = await harness.Client.RefundAsync(keyed);

        Assert.Equal(ZellerPaymentState.Declined, first.State);
        Assert.Equal(ZellerPaymentState.Approved, second.State);
        Assert.NotEqual(first.AttemptGuid, second.AttemptGuid);
        Assert.Equal(2, harness.Gateway.RefundRequests.Count);
    }

    [Fact]
    public async Task Cancelled_refund_is_rechecked_as_a_refund_later()
    {
        await using var harness = new Harness();
        harness.Gateway.OnRefund = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.Cancelled, reachedTerminal: true));
        harness.Gateway.OnQuery = (reference, index) => index == 0 ? ZellerQueryResult.Found() : ZellerQueryResult.Found(RefundApproved(reference));

        var outcome = await harness.Client.RefundAsync(Refund);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));
        await WaitUntilAsync(async () => (await harness.AttemptAsync(outcome)).Status == ZellerAttemptStatus.Unknown);
        var reopened = await harness.AttemptAsync(outcome);

        Assert.Equal(ZellerPaymentState.NotPaid, outcome.State);
        Assert.Equal(ZellerUnknownReason.LateApproval, reopened.UnknownReason);
        Assert.Equal(RefundUuid, reopened.TransactionUuid);
    }

    [Fact]
    public async Task Non_positive_refund_amount_is_rejected_before_anything_is_saved()
    {
        await using var harness = new Harness();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Client.RefundAsync(Refund with { AmountCents = -100 }));
        Assert.Empty(harness.Gateway.Calls.ToArray());
    }

    // ── 参数 ──

    [Fact]
    public async Task Non_positive_amount_is_rejected_before_anything_is_saved()
    {
        await using var harness = new Harness();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Client.PurchaseAsync(Context with { AmountCents = 0 }));
        Assert.Empty(harness.Gateway.Calls.ToArray());
        Assert.Empty(await harness.Attempts.ListOpenAsync("S001", "POS-01"));
    }

    [Fact]
    public void Non_positive_durations_are_rejected()
    {
        var gateway = new ScriptedZellerGateway();
        var attempts = new LocalZellerPaymentAttemptRepository(new LocalSqliteStore(Path.Combine(Path.GetTempPath(), $"unused-{Guid.NewGuid():N}.db")));

        Assert.Throws<ArgumentOutOfRangeException>(() => new ZellerCardTerminalClient(gateway, attempts, new ZellerClientOptions { VerificationInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZellerCardTerminalClient(gateway, attempts, new ZellerClientOptions { VerificationWindow = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZellerCardTerminalClient(gateway, attempts, new ZellerClientOptions { LateRecheckDelay = TimeSpan.Zero }));
    }

    // ── 辅助 ──

    private static ZellerTransaction Approved(string reference) => new(
        ZellerTransactionStatus.Approved,
        ZellerTransactionType.Purchase,
        100,
        Uuid,
        reference,
        ResponseCode: "00",
        ResponseText: "APPROVED",
        ApprovalCode: "456321",
        Rrn: "264753970917",
        PanMasked: "…2902",
        Scheme: "VISA",
        Bin: "483028",
        MerchantId: "000000000143281",
        TerminalId: "00134553",
        ReceiptText: "RECEIPT",
        ReceiptLink: "https://services.myzeller.com/member/receipts/au/65c4b343");

    /// <summary>按 reference 查回来的交易：没有小票原文，批准码为空，文案也不同。</summary>
    private static ZellerTransaction Queried(string reference) => Approved(reference) with
    {
        ResponseText = "Approved or completed successfully",
        ApprovalCode = "",
        ReceiptText = null
    };

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail("Condition was not met within 5 seconds.");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-zeller-client-{Guid.NewGuid():N}.db");

        public Harness()
        {
            Attempts = new LocalZellerPaymentAttemptRepository(new LocalSqliteStore(_databasePath));
            Client = new ZellerCardTerminalClient(Gateway, Attempts, timeProvider: Time, log: Log.Add);
        }

        public ScriptedZellerGateway Gateway { get; } = new();

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 5, 1, 0, 0, TimeSpan.Zero));

        public LocalZellerPaymentAttemptRepository Attempts { get; }

        public ZellerCardTerminalClient Client { get; }

        public SafeList<string> Log { get; } = new();

        public async Task<LocalZellerPaymentAttempt> AttemptAsync(ZellerPaymentOutcome outcome) =>
            (await Attempts.GetAsync(outcome.AttemptGuid))!;

        public async Task AdvanceAsync(TimeSpan total, TimeSpan step)
        {
            var remaining = total;
            while (remaining > TimeSpan.Zero)
            {
                var next = remaining < step ? remaining : step;
                Time.Advance(next);
                remaining -= next;
                await Task.Delay(15);
            }
        }

        public async Task AdvanceUntilAsync(Func<bool> condition, TimeSpan step, int maxSteps = 400)
        {
            for (var index = 0; index < maxSteps && !condition(); index++)
            {
                await Task.Delay(15);
                if (!condition())
                {
                    Time.Advance(step);
                }
            }
        }

        public async ValueTask DisposeAsync() => await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(_databasePath);
    }
}

/// <summary>按脚本回答的网关，记录调用顺序。</summary>
internal sealed class ScriptedZellerGateway : IZellerTerminalGateway
{
    private int _queryCount;
    private int _resetCount;

    public Func<ZellerPurchaseRequest, IProgress<ZellerTerminalEvent>?, CancellationToken, Task<ZellerCallResult>> OnPurchase { get; set; } =
        (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.Cancelled));

    /// <summary>参数：reference、这是第几次查询（从 0 开始）。</summary>
    public Func<string, int, ZellerQueryResult> OnQuery { get; set; } = (_, _) => ZellerQueryResult.Found();

    public SafeList<string> Calls { get; } = new();

    public int QueryCount => Volatile.Read(ref _queryCount);

    public int ResetCount => Volatile.Read(ref _resetCount);

    public Task<ZellerPairingResult> InitialiseAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ZellerPairingResult(true));

    public Task<ZellerPairingResult> SetupAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ZellerPairingResult(true));

    public Task<ZellerCallResult> PurchaseAsync(
        ZellerPurchaseRequest request,
        IProgress<ZellerTerminalEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"purchase:{request.Reference}");
        return OnPurchase(request, progress, cancellationToken);
    }

    public Func<ZellerRefundRequest, IProgress<ZellerTerminalEvent>?, CancellationToken, Task<ZellerCallResult>> OnRefund { get; set; } =
        (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.Cancelled));

    public SafeList<ZellerRefundRequest> RefundRequests { get; } = new();

    public Task<ZellerCallResult> RefundAsync(
        ZellerRefundRequest request,
        IProgress<ZellerTerminalEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"refund:{request.Reference}");
        RefundRequests.Add(request);
        return OnRefund(request, progress, cancellationToken);
    }

    public Task<ZellerQueryResult> GetTransactionsAsync(string reference, CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _queryCount) - 1;
        Calls.Add($"query:{reference}");
        return Task.FromResult(cancellationToken.IsCancellationRequested
            ? ZellerQueryResult.Failed(ZellerErrorType.Cancelled)
            : OnQuery(reference, index));
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _resetCount);
        Calls.Add("reset");
        return Task.CompletedTask;
    }
}
