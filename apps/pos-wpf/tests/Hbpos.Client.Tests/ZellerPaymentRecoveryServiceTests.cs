using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.Zeller;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>
/// Zeller 恢复服务：启动或登录时按 reference 查未结记录、关闭从未发出的记录、主管结案，
/// 以及不碰付款页还在处理的记录。对应真机 S9（杀进程后重启能查到）与方案 D3 的保守规则。
/// </summary>
public sealed class ZellerPaymentRecoveryServiceTests
{
    private const string Uuid = "65c4b343-03e6-49ef-a54d-012e3f2c222c";
    private const string SecondUuid = "98b690b7-5eaa-41ec-8ff8-168c60364aac";

    // ── 自动恢复 ──

    [Fact]
    public async Task Payment_cut_off_by_a_crash_is_recovered_as_approved_and_waits_for_its_order()
    {
        // 真机 S9：刷卡机进入处理后程序被杀，重启后按 reference 查到 Approved。
        await using var harness = new Harness();
        var crashed = await harness.SeedAsync(ZellerAttemptStatus.Submitted);
        harness.Gateway.OnQuery = (reference, _) => ZellerQueryResult.Found(Queried(reference));

        var summary = await harness.Recovery.AutoRecoverAsync("S001", "POS-01");
        var result = Assert.Single(summary.Results);
        var attempt = await harness.GetAsync(crashed);

        Assert.Equal(ZellerRecoveryStatus.Resolved, result.Status);
        Assert.Equal(ZellerPaymentState.Approved, result.Outcome!.State);
        Assert.Equal($"ZELLER:{crashed.Reference}:{Uuid}", result.PaymentReference);
        Assert.Equal(ZellerAttemptStatus.Approved, attempt.Status);
        Assert.Equal(Uuid, attempt.TransactionUuid);
        Assert.Equal(crashed.AttemptGuid, Assert.Single(summary.AwaitingOrderCompletion).AttemptGuid);

        Assert.True(await harness.Client.MarkOrderCompletedAsync(crashed.AttemptGuid));
        Assert.Empty(await harness.Recovery.ListAwaitingOrderCompletionAsync("S001", "POS-01"));
    }

    [Fact]
    public async Task Crash_with_no_transaction_found_is_left_unknown_for_a_supervisor()
    {
        // 方案 D3：进程中途退出后查到 0 笔只有一个样本，试点期间不自动收尾。
        await using var harness = new Harness();
        var crashed = await harness.SeedAsync(ZellerAttemptStatus.Submitted);
        harness.Gateway.OnQuery = (_, _) => ZellerQueryResult.Found();

        var result = Assert.Single((await harness.Recovery.AutoRecoverAsync("S001", "POS-01")).Results);
        var attempt = await harness.GetAsync(crashed);

        Assert.Equal(ZellerRecoveryStatus.StillUnknown, result.Status);
        Assert.Equal(ZellerAttemptStatus.Unknown, attempt.Status);
        Assert.Equal(ZellerUnknownReason.ProcessExited, attempt.UnknownReason);
        Assert.Equal(1, attempt.VerificationRounds);
    }

    [Fact]
    public async Task Crash_whose_query_fails_is_still_marked_unknown()
    {
        await using var harness = new Harness();
        var crashed = await harness.SeedAsync(ZellerAttemptStatus.Submitted);
        harness.Gateway.OnQuery = (_, _) => ZellerQueryResult.Failed(ZellerErrorType.NetworkFailure);

        await harness.Recovery.AutoRecoverAsync("S001", "POS-01");
        var attempt = await harness.GetAsync(crashed);

        Assert.Equal(ZellerAttemptStatus.Unknown, attempt.Status);
        Assert.Equal(ZellerUnknownReason.ProcessExited, attempt.UnknownReason);
    }

    [Fact]
    public async Task Unknown_attempt_with_still_no_transaction_is_not_rewritten()
    {
        await using var harness = new Harness();
        var unknown = await harness.SeedAsync(ZellerAttemptStatus.Unknown, attempt => attempt with { UnknownReason = ZellerUnknownReason.SdkError });
        harness.Gateway.OnQuery = (_, _) => ZellerQueryResult.Found();

        var result = await harness.Recovery.RecoverAttemptAsync(unknown.AttemptGuid);
        var attempt = await harness.GetAsync(unknown);

        Assert.Equal(ZellerRecoveryStatus.StillUnknown, result.Status);
        Assert.Equal(unknown.Version, attempt.Version);
        Assert.Equal(ZellerUnknownReason.SdkError, attempt.UnknownReason);
    }

    [Fact]
    public async Task Unknown_attempt_found_declined_is_closed_as_declined()
    {
        await using var harness = new Harness();
        var unknown = await harness.SeedAsync(ZellerAttemptStatus.Unknown);
        harness.Gateway.OnQuery = (reference, _) => ZellerQueryResult.Found(
            Queried(reference) with { Status = ZellerTransactionStatus.Declined, ResponseCode = "51" });

        var result = await harness.Recovery.RecoverAttemptAsync(unknown.AttemptGuid);
        var attempt = await harness.GetAsync(unknown);

        Assert.Equal(ZellerRecoveryStatus.Resolved, result.Status);
        Assert.Equal(ZellerPaymentState.Declined, result.Outcome!.State);
        Assert.Equal(ZellerAttemptStatus.Declined, attempt.Status);
        Assert.Equal("51", attempt.ResponseCode);
    }

    [Fact]
    public async Task Two_approvals_found_during_recovery_are_recorded_for_a_supervisor()
    {
        await using var harness = new Harness();
        var unknown = await harness.SeedAsync(ZellerAttemptStatus.Unknown);
        harness.Gateway.OnQuery = (reference, _) => ZellerQueryResult.Found(
            Queried(reference),
            Queried(reference) with { TransactionUuid = SecondUuid });

        var result = await harness.Recovery.RecoverAttemptAsync(unknown.AttemptGuid);
        var attempt = await harness.GetAsync(unknown);

        Assert.Equal(ZellerRecoveryStatus.StillUnknown, result.Status);
        Assert.Equal(ZellerUnknownReason.MultipleApproved, attempt.UnknownReason);
        Assert.False(string.IsNullOrWhiteSpace(attempt.TransactionUuid));
    }

    [Fact]
    public async Task Attempt_that_never_reached_the_terminal_call_is_closed_without_a_query()
    {
        await using var harness = new Harness();
        var pending = await harness.SeedAsync(ZellerAttemptStatus.Pending);

        var result = Assert.Single((await harness.Recovery.AutoRecoverAsync("S001", "POS-01")).Results);
        var attempt = await harness.GetAsync(pending);

        Assert.Equal(ZellerRecoveryStatus.Closed, result.Status);
        Assert.Equal(ZellerPaymentState.NotPaid, result.Outcome!.State);
        Assert.Equal(ZellerAttemptStatus.Canceled, attempt.Status);
        Assert.Equal(0, harness.Gateway.QueryCount);
    }

    [Fact]
    public async Task Attempts_being_processed_on_this_pos_are_left_alone()
    {
        await using var harness = new Harness();
        var submitted = await harness.SeedAsync(ZellerAttemptStatus.Submitted);
        var pending = await harness.SeedAsync(ZellerAttemptStatus.Pending);
        using var submittedLease = harness.Client.Activity.TryEnter(submitted.AttemptGuid);
        using var pendingLease = harness.Client.Activity.TryEnter(pending.AttemptGuid);

        var summary = await harness.Recovery.AutoRecoverAsync("S001", "POS-01");

        Assert.All(summary.Results, result => Assert.Equal(ZellerRecoveryStatus.InProgress, result.Status));
        Assert.Equal(2, summary.Results.Count);
        Assert.Equal(0, harness.Gateway.QueryCount);
        Assert.Equal(ZellerAttemptStatus.Pending, (await harness.GetAsync(pending)).Status);
    }

    [Fact]
    public async Task Payment_still_verifying_on_the_payment_page_is_reported_in_progress()
    {
        await using var harness = new Harness();
        using var stopWaiting = new CancellationTokenSource();
        harness.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.NetworkFailure, reachedTerminal: true));
        harness.Gateway.OnQuery = (_, _) => ZellerQueryResult.Found();

        var purchase = harness.Client.PurchaseAsync(
            new ZellerPurchaseContext(100, "{}", "S001", "POS-01", "C001"),
            cancellationToken: stopWaiting.Token);
        await WaitUntilAsync(async () => (await harness.Recovery.ListOpenAsync("S001", "POS-01")).Any(attempt => attempt.Status == ZellerAttemptStatus.Unknown));
        var open = Assert.Single(await harness.Recovery.ListOpenAsync("S001", "POS-01"));
        var queriesBefore = harness.Gateway.QueryCount;

        var whileVerifying = await harness.Recovery.RecoverAttemptAsync(open.AttemptGuid);
        var resolveWhileVerifying = await harness.Recovery.ResolveAsync(open.AttemptGuid, Supervisor(ZellerSupervisorDecision.ConfirmNotPaid));
        var queriesWhileVerifying = harness.Gateway.QueryCount;
        stopWaiting.Cancel();
        await purchase.WaitAsync(TimeSpan.FromSeconds(10));
        var afterPaymentPage = await harness.Recovery.RecoverAttemptAsync(open.AttemptGuid);

        Assert.Equal(ZellerRecoveryStatus.InProgress, whileVerifying.Status);
        Assert.False(resolveWhileVerifying.Succeeded);
        Assert.Equal(ZellerAttemptStatus.Unknown, (await harness.GetAsync(open)).Status);
        Assert.Equal(queriesBefore, queriesWhileVerifying);
        Assert.Equal(ZellerRecoveryStatus.StillUnknown, afterPaymentPage.Status);
    }

    [Fact]
    public async Task Missing_or_finished_attempts_are_not_open()
    {
        await using var harness = new Harness();
        var approved = await harness.SeedAsync(ZellerAttemptStatus.Approved, attempt => attempt with { TransactionUuid = Uuid });

        Assert.Equal(ZellerRecoveryStatus.NotOpen, (await harness.Recovery.RecoverAttemptAsync(Guid.NewGuid())).Status);
        Assert.Equal(ZellerRecoveryStatus.NotOpen, (await harness.Recovery.RecoverAttemptAsync(approved.AttemptGuid)).Status);
        Assert.Equal(0, harness.Gateway.QueryCount);
    }

    [Fact]
    public async Task Refund_is_recovered_against_refund_transactions_only()
    {
        await using var harness = new Harness();
        var refund = await harness.SeedAsync(ZellerAttemptStatus.Unknown, operation: ZellerAttemptOperation.Refund);
        harness.Gateway.OnQuery = (reference, _) => ZellerQueryResult.Found(
            Queried(reference),
            Queried(reference) with { Type = ZellerTransactionType.Refund, TransactionUuid = SecondUuid });

        var result = await harness.Recovery.RecoverAttemptAsync(refund.AttemptGuid);

        Assert.Equal(ZellerRecoveryStatus.Resolved, result.Status);
        Assert.Equal(ZellerPaymentState.Approved, result.Outcome!.State);
        Assert.Equal($"ZELLERRF:{refund.Reference}:{SecondUuid}", result.PaymentReference);
    }

    [Fact]
    public async Task Auto_recovery_also_does_due_late_rechecks()
    {
        await using var harness = new Harness();
        await harness.SeedAsync(ZellerAttemptStatus.Canceled, attempt => attempt with
        {
            FailureKind = ZellerFailureKind.Cancelled,
            LateRecheckDueAt = harness.Time.GetUtcNow().AddSeconds(-1)
        });
        harness.Gateway.OnQuery = (_, _) => ZellerQueryResult.Found();

        var summary = await harness.Recovery.AutoRecoverAsync("S001", "POS-01");

        Assert.Equal(1, summary.LateRechecks);
        Assert.Empty(summary.Results);
    }

    [Fact]
    public async Task Only_this_devices_attempts_are_recovered()
    {
        await using var harness = new Harness();
        await harness.SeedAsync(ZellerAttemptStatus.Submitted, attempt => attempt, deviceCode: "POS-02");

        var summary = await harness.Recovery.AutoRecoverAsync("S001", "POS-01");

        Assert.Empty(summary.Results);
        Assert.Equal(0, harness.Gateway.QueryCount);
    }

    // ── 主管结案 ──

    [Fact]
    public async Task Confirm_paid_with_a_known_transaction_uses_it_in_the_order_reference()
    {
        await using var harness = new Harness();
        var unknown = await harness.SeedAsync(ZellerAttemptStatus.Unknown, attempt => attempt with
        {
            UnknownReason = ZellerUnknownReason.MultipleApproved,
            TransactionUuid = Uuid
        });

        var result = await harness.Recovery.ResolveAsync(unknown.AttemptGuid, Supervisor(ZellerSupervisorDecision.ConfirmPaid, "terminal showed APPROVED", "264753970917"));
        var attempt = await harness.GetAsync(unknown);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal($"ZELLER:{unknown.Reference}:{Uuid}", result.OrderPaymentReference);
        Assert.Equal(ZellerAttemptStatus.Approved, attempt.Status);
        Assert.Equal(ZellerSupervisorDecision.ConfirmPaid, attempt.SupervisorDecision);
        Assert.Equal("terminal showed APPROVED", attempt.SupervisorNote);
        Assert.Equal("264753970917", attempt.SupervisorFinancialReference);
        Assert.Equal("M001", attempt.SupervisorOperatorId);
        Assert.Equal("Store Manager", attempt.SupervisorOperatorName);
        Assert.Equal(harness.Time.GetUtcNow(), attempt.SupervisorDecidedAt);
        Assert.Contains(attempt.AttemptGuid, (await harness.Recovery.ListAwaitingOrderCompletionAsync("S001", "POS-01")).Select(item => item.AttemptGuid));
    }

    [Fact]
    public async Task Confirm_paid_without_a_transaction_gives_an_unverified_reference_that_cannot_be_refunded_to_card()
    {
        await using var harness = new Harness();
        var unknown = await harness.SeedAsync(ZellerAttemptStatus.Unknown);

        var result = await harness.Recovery.ResolveAsync(unknown.AttemptGuid, Supervisor(ZellerSupervisorDecision.ConfirmPaid));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal($"ZELLER:{unknown.Reference}", result.OrderPaymentReference);
        Assert.True(ZellerPaymentReference.IsZeller(result.OrderPaymentReference));
        Assert.True(ZellerPaymentReference.IsUnverified(result.OrderPaymentReference));
        Assert.False(ZellerPaymentReference.TryGetOriginalTransactionUuid(result.OrderPaymentReference, out _));
    }

    [Fact]
    public async Task Confirm_refunded_without_a_transaction_gives_an_unverified_refund_reference()
    {
        await using var harness = new Harness();
        var refund = await harness.SeedAsync(ZellerAttemptStatus.Unknown, operation: ZellerAttemptOperation.Refund);

        var result = await harness.Recovery.ResolveAsync(refund.AttemptGuid, Supervisor(ZellerSupervisorDecision.ConfirmPaid));

        Assert.Equal($"ZELLERRF:{refund.Reference}", result.OrderPaymentReference);
    }

    [Fact]
    public async Task Confirm_not_paid_closes_the_attempt()
    {
        await using var harness = new Harness();
        var unknown = await harness.SeedAsync(ZellerAttemptStatus.Unknown);

        var result = await harness.Recovery.ResolveAsync(unknown.AttemptGuid, Supervisor(ZellerSupervisorDecision.ConfirmNotPaid, "  terminal showed declined  "));
        var attempt = await harness.GetAsync(unknown);

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.OrderPaymentReference);
        Assert.Equal(ZellerAttemptStatus.Canceled, attempt.Status);
        Assert.Equal("terminal showed declined", attempt.SupervisorNote);
        Assert.Empty(await harness.Recovery.ListOpenAsync("S001", "POS-01"));
    }

    [Theory]
    [InlineData(ZellerUnknownReason.LateApproval)]
    [InlineData(ZellerUnknownReason.MultipleApproved)]
    public async Task Confirm_not_paid_is_refused_when_zeller_already_reported_an_approval(ZellerUnknownReason reason)
    {
        await using var harness = new Harness();
        var unknown = await harness.SeedAsync(ZellerAttemptStatus.Unknown, attempt => attempt with { UnknownReason = reason, TransactionUuid = Uuid });

        var result = await harness.Recovery.ResolveAsync(unknown.AttemptGuid, Supervisor(ZellerSupervisorDecision.ConfirmNotPaid));
        var attempt = await harness.GetAsync(unknown);

        Assert.False(result.Succeeded);
        Assert.Contains("already reported this payment as approved", result.Error, StringComparison.Ordinal);
        Assert.Equal(ZellerAttemptStatus.Unknown, attempt.Status);
        Assert.Null(attempt.SupervisorDecision);
    }

    [Fact]
    public async Task Continue_waiting_keeps_the_attempt_open_and_records_the_supervisor()
    {
        await using var harness = new Harness();
        var submitted = await harness.SeedAsync(ZellerAttemptStatus.Submitted);

        var result = await harness.Recovery.ResolveAsync(submitted.AttemptGuid, Supervisor(ZellerSupervisorDecision.ContinueWaiting, " "));
        var attempt = await harness.GetAsync(submitted);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(ZellerAttemptStatus.Unknown, attempt.Status);
        Assert.Equal(ZellerUnknownReason.ProcessExited, attempt.UnknownReason);
        Assert.Equal(ZellerSupervisorDecision.ContinueWaiting, attempt.SupervisorDecision);
        Assert.Null(attempt.SupervisorNote);
        Assert.Single(await harness.Recovery.ListOpenAsync("S001", "POS-01"));
    }

    [Fact]
    public async Task Resolution_requires_a_supervisor_and_bounded_text()
    {
        await using var harness = new Harness();
        var unknown = await harness.SeedAsync(ZellerAttemptStatus.Unknown);

        var noOperator = await harness.Recovery.ResolveAsync(unknown.AttemptGuid, Supervisor(ZellerSupervisorDecision.ConfirmPaid) with { OperatorId = " " });
        var longNote = await harness.Recovery.ResolveAsync(unknown.AttemptGuid, Supervisor(ZellerSupervisorDecision.ConfirmPaid, new string('x', 501)));
        var longReference = await harness.Recovery.ResolveAsync(unknown.AttemptGuid, Supervisor(ZellerSupervisorDecision.ConfirmPaid, null, new string('9', 101)));

        Assert.False(noOperator.Succeeded);
        Assert.False(longNote.Succeeded);
        Assert.False(longReference.Succeeded);
        Assert.Equal(ZellerAttemptStatus.Unknown, (await harness.GetAsync(unknown)).Status);
    }

    [Fact]
    public async Task Resolution_is_refused_for_missing_finished_or_busy_attempts()
    {
        await using var harness = new Harness();
        var approved = await harness.SeedAsync(ZellerAttemptStatus.Approved, attempt => attempt with { TransactionUuid = Uuid });
        var busy = await harness.SeedAsync(ZellerAttemptStatus.Unknown);
        using var lease = harness.Client.Activity.TryEnter(busy.AttemptGuid);

        Assert.False((await harness.Recovery.ResolveAsync(Guid.NewGuid(), Supervisor(ZellerSupervisorDecision.ConfirmPaid))).Succeeded);
        Assert.False((await harness.Recovery.ResolveAsync(approved.AttemptGuid, Supervisor(ZellerSupervisorDecision.ConfirmNotPaid))).Succeeded);
        Assert.False((await harness.Recovery.ResolveAsync(busy.AttemptGuid, Supervisor(ZellerSupervisorDecision.ConfirmPaid))).Succeeded);
        Assert.Equal(ZellerAttemptStatus.Unknown, (await harness.GetAsync(busy)).Status);
    }

    // ── 辅助 ──

    private static ZellerSupervisorResolution Supervisor(ZellerSupervisorDecision decision, string? note = null, string? reference = null) =>
        new(decision, "M001", "Store Manager", note, reference);

    private static ZellerTransaction Queried(string reference) => new(
        ZellerTransactionStatus.Approved,
        ZellerTransactionType.Purchase,
        100,
        Uuid,
        reference,
        ResponseCode: "00",
        ResponseText: "Approved or completed successfully",
        ApprovalCode: "",
        Rrn: "264753970917",
        PanMasked: "…2902",
        Scheme: "VISA");

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
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-zeller-recovery-{Guid.NewGuid():N}.db");

        public Harness()
        {
            Attempts = new LocalZellerPaymentAttemptRepository(new LocalSqliteStore(_databasePath));
            Client = new ZellerCardTerminalClient(Gateway, Attempts, timeProvider: Time);
            Recovery = new ZellerPaymentRecoveryService(Gateway, Attempts, Client, Time);
        }

        public ScriptedZellerGateway Gateway { get; } = new();

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 5, 1, 0, 0, TimeSpan.Zero));

        public LocalZellerPaymentAttemptRepository Attempts { get; }

        public ZellerCardTerminalClient Client { get; }

        public ZellerPaymentRecoveryService Recovery { get; }

        public async Task<LocalZellerPaymentAttempt> GetAsync(LocalZellerPaymentAttempt attempt) =>
            (await Attempts.GetAsync(attempt.AttemptGuid))!;

        /// <summary>按客户端的真实顺序把一条记录推进到指定状态：待发出 → 已发出 → 目标状态。</summary>
        public async Task<LocalZellerPaymentAttempt> SeedAsync(
            ZellerAttemptStatus status,
            Func<LocalZellerPaymentAttempt, LocalZellerPaymentAttempt>? customize = null,
            ZellerAttemptOperation operation = ZellerAttemptOperation.Sale,
            string deviceCode = "POS-01")
        {
            var guid = Guid.NewGuid();
            var now = Time.GetUtcNow();
            var pending = new LocalZellerPaymentAttempt(
                guid,
                operation,
                operation == ZellerAttemptOperation.Refund
                    ? ZellerPaymentReference.CreateRefundReference(guid)
                    : ZellerPaymentReference.CreatePurchaseReference(guid),
                100,
                ZellerAttemptStatus.Pending,
                "{\"draft\":1}",
                "S001",
                deviceCode,
                "C001",
                now,
                now)
            {
                OriginalTransactionUuid = operation == ZellerAttemptOperation.Refund ? "28b1e54e-a420-41d6-bb8e-2a7497bf783d" : null
            };
            await Attempts.CreateAsync(pending);
            if (status == ZellerAttemptStatus.Pending)
            {
                return pending;
            }

            var customizeOrSame = customize ?? (attempt => attempt);
            var submitted = (await Attempts.TryUpdateAsync(pending, pending with { Status = ZellerAttemptStatus.Submitted }))!;
            if (status == ZellerAttemptStatus.Submitted)
            {
                return customize is null ? submitted : (await Attempts.TryUpdateAsync(submitted, customizeOrSame(submitted)))!;
            }

            var target = customizeOrSame(submitted with
            {
                Status = status,
                FailureKind = status == ZellerAttemptStatus.Unknown ? ZellerFailureKind.ResultUnknown : ZellerFailureKind.None,
                UnknownReason = status == ZellerAttemptStatus.Unknown ? ZellerUnknownReason.SdkError : ZellerUnknownReason.None
            });
            return (await Attempts.TryUpdateAsync(submitted, target))!;
        }

        public async ValueTask DisposeAsync() => await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(_databasePath);
    }
}
