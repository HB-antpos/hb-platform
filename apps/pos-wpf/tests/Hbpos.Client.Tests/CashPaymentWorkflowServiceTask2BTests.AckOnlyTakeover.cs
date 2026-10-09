using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

// M4：实时 ack 失败后本地 attempt 已是终态（订单已完成/明确失败）但服务端会话未确认。
// 下一笔 Cloud 刷卡的接管匹配到它时，CAS 拒绝写 session/outcome（这是对的，不能放宽）却被当成失败，
// 此后这台设备每笔刷卡都被阻断。修复：终态一致时只补 ack；ack 失败进后台重试。
// M16：接管时也要认领 Sale 已终态且已不在未结队列里的记录，不再造重复的 generic 批准记录。
public sealed partial class CashPaymentWorkflowServiceTask2BTests
{
    private static readonly PosSessionState AckSession =
        new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    private sealed class SqliteHarness : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-ack-takeover-{Guid.NewGuid():N}.db");

        public LocalSqliteStore Store { get; private set; } = null!;

        public LocalCardPaymentAttemptRepository Attempts { get; private set; } = null!;

        public static async Task<SqliteHarness> CreateAsync()
        {
            var harness = new SqliteHarness();
            harness.Store = new LocalSqliteStore(harness._path);
            await new LocalSchemaService(harness.Store).InitializeAsync();
            harness.Attempts = new LocalCardPaymentAttemptRepository(harness.Store);
            return harness;
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private static LinklyCloudBackendSessionResponse VerifiedApprovedSession(string sessionId, string txnRef) =>
        FinalApprovedSession(sessionId, txnRef, transactionSuccess: null) with
        {
            CardTransaction = VerifiedCardTransaction(txnRef, 1000)
        };

    private static async Task<PaymentTenderAttemptResult> RunNewCardPaymentAsync(
        CashPaymentWorkflowService workflow,
        PosSessionState session)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-ACK-ONLY", "Ack Only Tea", "930ACKONLY", 10m));
        return await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cartSnapshot: cart.CreateSnapshot());
    }

    [Fact]
    public async Task Takeover_of_a_completed_but_unacknowledged_sale_only_acknowledges_and_keeps_the_local_result()
    {
        await using var harness = await SqliteHarness.CreateAsync();
        var existing = CreateExistingAttempt(
            "Sale", "active-session-1", "TXN-OLD", AckSession, Guid.NewGuid());
        await harness.Attempts.CreateAsync(existing);
        await harness.Attempts.UpdateOutcomeAsync(
            existing.AttemptGuid,
            LocalCardPaymentAttemptStatus.Approved,
            "00",
            "APPROVED",
            "ANZ:TXN-OLD",
            DateTimeOffset.UtcNow.AddMinutes(-3));
        await harness.Attempts.MarkOrderCompletedAsync(existing.AttemptGuid, DateTimeOffset.UtcNow.AddMinutes(-2));
        var beforeTakeover = Assert.IsType<LocalCardPaymentAttempt>(
            await harness.Attempts.GetAttemptAsync(existing.AttemptGuid));
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, beforeTakeover.Status);
        Assert.Null(beforeTakeover.AcknowledgedAt);

        var events = new List<string>();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(events, VerifiedApprovedSession("active-session-1", "TXN-OLD"));
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor, settings, ActivePendingSession("active-session-1", "TXN-OLD"), events);
        var workflow = CreateWorkflow(terminal, harness.Attempts, settings, accessor, backend);

        var result = await RunNewCardPaymentAsync(workflow, AckSession);

        Assert.True(result.Succeeded);
        Assert.True(terminal.TakeoverResult?.Succeeded);
        Assert.Equal(["acknowledge", "new-start"], events.Where(item => item is "acknowledge" or "new-start"));
        var afterTakeover = Assert.IsType<LocalCardPaymentAttempt>(
            await harness.Attempts.GetAttemptAsync(existing.AttemptGuid));
        // 本地终态原样保留：状态、响应码、付款参考号都不被接管改写，只多了 ack 时间。
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, afterTakeover.Status);
        Assert.Equal("00", afterTakeover.ResponseCode);
        Assert.Equal("ANZ:TXN-OLD", afterTakeover.PaymentReference);
        Assert.NotNull(afterTakeover.AcknowledgedAt);
        // 不会再造一条 generic 记录，也不会遗留未结记录。
        Assert.DoesNotContain(
            await harness.Attempts.GetRecentAttemptsAsync("S001", "POS-01", "Sandbox"),
            attempt => attempt.OperationKind == "ActiveSession");
        Assert.DoesNotContain(
            await harness.Attempts.GetOpenAttemptsAsync("S001", "POS-01", "Sandbox"),
            attempt => attempt.AttemptGuid == existing.AttemptGuid);
    }

    [Fact]
    public async Task Takeover_of_a_finally_failed_unacknowledged_sale_only_acknowledges()
    {
        await using var harness = await SqliteHarness.CreateAsync();
        var existing = CreateExistingAttempt(
            "Sale", "active-session-1", "TXN-OLD", AckSession, Guid.NewGuid());
        await harness.Attempts.CreateAsync(existing);
        await harness.Attempts.UpdateOutcomeAsync(
            existing.AttemptGuid,
            LocalCardPaymentAttemptStatus.Declined,
            "05",
            "DECLINED",
            null,
            DateTimeOffset.UtcNow.AddMinutes(-2));

        var events = new List<string>();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var failed = ActivePendingSession("active-session-1", "TXN-OLD") with
        {
            Status = "Completed",
            ResponseCode = "05",
            ResponseText = "DECLINED",
            TransactionSuccess = false
        };
        var backend = new RecordingBackendTerminalClient(events, failed);
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor, settings, ActivePendingSession("active-session-1", "TXN-OLD"), events);
        var workflow = CreateWorkflow(terminal, harness.Attempts, settings, accessor, backend);

        var result = await RunNewCardPaymentAsync(workflow, AckSession);

        Assert.True(result.Succeeded);
        var afterTakeover = Assert.IsType<LocalCardPaymentAttempt>(
            await harness.Attempts.GetAttemptAsync(existing.AttemptGuid));
        Assert.Equal(LocalCardPaymentAttemptStatus.Declined, afterTakeover.Status);
        Assert.NotNull(afterTakeover.AcknowledgedAt);
        Assert.Contains("acknowledge", events);
    }

    [Theory]
    [InlineData(LocalCardPaymentAttemptStatus.OrderCompleted, "Failed")]
    [InlineData(LocalCardPaymentAttemptStatus.Declined, "Approved")]
    public async Task Takeover_refuses_to_acknowledge_when_linkly_contradicts_the_saved_local_result(
        LocalCardPaymentAttemptStatus localStatus,
        string remote)
    {
        await using var harness = await SqliteHarness.CreateAsync();
        var existing = CreateExistingAttempt(
            "Sale", "active-session-1", "TXN-OLD", AckSession, Guid.NewGuid());
        await harness.Attempts.CreateAsync(existing);
        if (localStatus == LocalCardPaymentAttemptStatus.OrderCompleted)
        {
            await harness.Attempts.UpdateOutcomeAsync(
                existing.AttemptGuid, LocalCardPaymentAttemptStatus.Approved, "00", "APPROVED", "ANZ:TXN-OLD", DateTimeOffset.UtcNow.AddMinutes(-3));
            await harness.Attempts.MarkOrderCompletedAsync(existing.AttemptGuid, DateTimeOffset.UtcNow.AddMinutes(-2));
        }
        else
        {
            await harness.Attempts.UpdateOutcomeAsync(
                existing.AttemptGuid, localStatus, "05", "DECLINED", null, DateTimeOffset.UtcNow.AddMinutes(-2));
        }

        var events = new List<string>();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var finalStatus = remote == "Approved"
            ? VerifiedApprovedSession("active-session-1", "TXN-OLD")
            : ActivePendingSession("active-session-1", "TXN-OLD") with
            {
                Status = "Failed",
                ResponseCode = "05",
                ResponseText = "DECLINED"
            };
        var backend = new RecordingBackendTerminalClient(events, finalStatus);
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor, settings, ActivePendingSession("active-session-1", "TXN-OLD"), events);
        var workflow = CreateWorkflow(terminal, harness.Attempts, settings, accessor, backend);

        var result = await RunNewCardPaymentAsync(workflow, AckSession);

        Assert.False(result.Succeeded);
        Assert.False(terminal.TakeoverResult?.Succeeded);
        Assert.Contains("differs", terminal.TakeoverResult?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acknowledge", events);
        Assert.DoesNotContain("new-start", events);
        var untouched = Assert.IsType<LocalCardPaymentAttempt>(
            await harness.Attempts.GetAttemptAsync(existing.AttemptGuid));
        Assert.Equal(localStatus, untouched.Status);
        Assert.Null(untouched.AcknowledgedAt);
    }

    [Fact]
    public async Task Takeover_still_refuses_an_attempt_that_is_mid_finalization()
    {
        await using var harness = await SqliteHarness.CreateAsync();
        var existing = CreateExistingAttempt(
            "Sale", "active-session-1", "TXN-OLD", AckSession, Guid.NewGuid()) with
        {
            Status = LocalCardPaymentAttemptStatus.Approved,
            ResponseCode = ActiveSessionSupervisorResolutionCodes.ConfirmedPaid,
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalCardPaymentAttemptStatus.OrderCompleted.ToString()
        };
        await harness.Attempts.CreateAsync(existing);

        var events = new List<string>();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(events, VerifiedApprovedSession("active-session-1", "TXN-OLD"));
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor, settings, ActivePendingSession("active-session-1", "TXN-OLD"), events);
        var workflow = CreateWorkflow(terminal, harness.Attempts, settings, accessor, backend);

        var result = await RunNewCardPaymentAsync(workflow, AckSession);

        // FinalizePending 仍由恢复流程收尾，接管不能插手（CAS 没被放宽）。
        Assert.False(result.Succeeded);
        Assert.DoesNotContain("acknowledge", events);
        Assert.DoesNotContain("new-start", events);
    }

    [Fact]
    public async Task Takeover_links_a_settled_sale_without_session_id_by_txn_ref_instead_of_creating_a_duplicate_record()
    {
        await using var harness = await SqliteHarness.CreateAsync();
        // 主管确认已付款时只有 TxnRef、会话从未被 ack：订单早已建好（OrderCompleted、无 SessionId，不在未结队列里）。
        var existing = CreateExistingAttempt("Sale", null, "TXN-OLD", AckSession, Guid.NewGuid());
        await harness.Attempts.CreateAsync(existing);
        await harness.Attempts.UpdateOutcomeAsync(
            existing.AttemptGuid,
            LocalCardPaymentAttemptStatus.Approved,
            "00",
            "APPROVED",
            "ANZ:TXN-OLD",
            DateTimeOffset.UtcNow.AddMinutes(-3));
        await harness.Attempts.MarkOrderCompletedAsync(existing.AttemptGuid, DateTimeOffset.UtcNow.AddMinutes(-2));
        Assert.Empty(await harness.Attempts.GetOpenAttemptsAsync("S001", "POS-01", "Sandbox"));

        var events = new List<string>();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(events, VerifiedApprovedSession("active-session-1", "TXN-OLD"));
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor, settings, ActivePendingSession("active-session-1", "TXN-OLD"), events);
        var workflow = CreateWorkflow(terminal, harness.Attempts, settings, accessor, backend);

        var result = await RunNewCardPaymentAsync(workflow, AckSession);

        Assert.True(result.Succeeded);
        Assert.Contains("acknowledge", events);
        var recent = await harness.Attempts.GetRecentAttemptsAsync("S001", "POS-01", "Sandbox");
        Assert.DoesNotContain(recent, attempt => attempt.OperationKind == "ActiveSession");
        Assert.DoesNotContain(
            await harness.Attempts.GetOpenAttemptsAsync("S001", "POS-01", "Sandbox"),
            attempt => attempt.OperationKind == "ActiveSession");
        var linked = Assert.Single(recent, attempt => attempt.AttemptGuid == existing.AttemptGuid);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, linked.Status);
    }

    [Fact]
    public async Task Takeover_does_not_link_a_settled_sale_that_belongs_to_a_different_session()
    {
        await using var harness = await SqliteHarness.CreateAsync();
        var other = CreateExistingAttempt("Sale", "another-session", "TXN-OTHER", AckSession, Guid.NewGuid());
        await harness.Attempts.CreateAsync(other);
        await harness.Attempts.UpdateOutcomeAsync(
            other.AttemptGuid,
            LocalCardPaymentAttemptStatus.Approved,
            "00",
            "APPROVED",
            "ANZ:TXN-OTHER",
            DateTimeOffset.UtcNow.AddMinutes(-3));
        await harness.Attempts.MarkOrderCompletedAsync(other.AttemptGuid, DateTimeOffset.UtcNow.AddMinutes(-2));
        await harness.Attempts.MarkAcknowledgedAsync(other.AttemptGuid, DateTimeOffset.UtcNow.AddMinutes(-1));

        var events = new List<string>();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new RecordingBackendTerminalClient(
            events,
            FinalApprovedSession("active-session-1", "TXN-OLD", transactionSuccess: null));
        var settings = CreateBackendLinklySettings();
        var terminal = new TakeoverInvokingCardTerminalClient(
            accessor, settings, ActivePendingSession("active-session-1", "TXN-OLD"), events);
        var workflow = CreateWorkflow(terminal, harness.Attempts, settings, accessor, backend);

        var result = await RunNewCardPaymentAsync(workflow, AckSession);

        // 没有任何本机 attempt 对应这个会话：保持原有行为，生成 generic 待复核记录。
        Assert.True(result.Succeeded);
        var recent = await harness.Attempts.GetRecentAttemptsAsync("S001", "POS-01", "Sandbox");
        Assert.Single(recent, attempt => attempt.OperationKind == "ActiveSession");
    }

    // ───────────── ack 失败后台重试 ─────────────

    private sealed class FlakyAckBackend(int failuresBeforeSuccess) : ILinklyBackendTerminalClient
    {
        private int _failuresRemaining = failuresBeforeSuccess;

        public int AcknowledgeCalls { get; private set; }

        public Task AcknowledgeSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            AcknowledgeCalls++;
            if (_failuresRemaining > 0 || failuresBeforeSuccess < 0)
            {
                _failuresRemaining--;
                return Task.FromException(new HttpRequestException("ack failed"));
            }

            return Task.CompletedTask;
        }

        public Task AcknowledgeSupervisorResolvedSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            AcknowledgeSessionAsync(settings, sessionId, cancellationToken);

        public Task<LinklyConnectionTestResult> TestConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LinklyConnectionTestResult> TestTransactionStatusAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> PurchaseAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LinklyCloudBackendSessionResponse?>(null);

        public Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> ResumeSessionUntilFinalAsync(
            CardTerminalSettings settings,
            LinklyCloudBackendSessionResponse activeStatus,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // 终端返回明确拒绝（带会话号）：拒绝结果落库后立即 ack 释放会话。
    private sealed class DecliningSessionCardTerminalClient(
        ILinklyPaymentAttemptContextAccessor accessor) : ICardTerminalClient
    {
        public async Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            var context = accessor.Current;
            Assert.NotNull(context);
            await context!.BindSessionAsync("SESSION-DECLINED", context.TxnRef, DateTimeOffset.UtcNow, cancellationToken);
            return new PaymentAuthorizationResult(
                false,
                null,
                "Declined",
                SessionId: "SESSION-DECLINED",
                TxnRef: context.TxnRef,
                ResponseCode: "05",
                ResponseText: "DECLINED");
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Failed_realtime_acknowledge_is_retried_in_the_background_until_the_session_is_released()
    {
        await using var harness = await SqliteHarness.CreateAsync();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new FlakyAckBackend(failuresBeforeSuccess: 2);
        var settings = CreateBackendLinklySettings();
        var delays = new List<TimeSpan>();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new StubOrderRepository(),
            new StubSyncQueueRepository(),
            cardTerminalClient: new DecliningSessionCardTerminalClient(accessor),
            cardPaymentAttemptRepository: harness.Attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
            linklyPaymentAttemptContextAccessor: accessor,
            linklyBackendTerminalClient: backend,
            acknowledgeRetryDelayAsync: (delay, _) =>
            {
                lock (delays)
                {
                    delays.Add(delay);
                }

                return Task.CompletedTask;
            });

        var result = await RunNewCardPaymentAsync(workflow, AckSession);
        await workflow.WaitForAcknowledgeRetriesAsync();

        Assert.False(result.Succeeded);
        // 第一次实时 ack 失败 + 两次后台重试（第二次失败、第三次成功）。
        Assert.Equal(3, backend.AcknowledgeCalls);
        var attempt = Assert.Single(
            await harness.Attempts.GetRecentAttemptsAsync("S001", "POS-01", "Sandbox"));
        Assert.Equal(LocalCardPaymentAttemptStatus.Declined, attempt.Status);
        Assert.NotNull(attempt.AcknowledgedAt);
        Assert.Empty(await harness.Attempts.GetOpenAttemptsAsync("S001", "POS-01", "Sandbox"));
        lock (delays)
        {
            Assert.Equal(2, delays.Count);
            // 指数退避：间隔逐次拉长。
            Assert.True(delays[1] > delays[0]);
        }
    }

    [Fact]
    public async Task Background_acknowledge_retries_are_bounded_and_leave_the_attempt_for_recovery()
    {
        await using var harness = await SqliteHarness.CreateAsync();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var backend = new FlakyAckBackend(failuresBeforeSuccess: -1);
        var settings = CreateBackendLinklySettings();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new StubOrderRepository(),
            new StubSyncQueueRepository(),
            cardTerminalClient: new DecliningSessionCardTerminalClient(accessor),
            cardPaymentAttemptRepository: harness.Attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
            linklyPaymentAttemptContextAccessor: accessor,
            linklyBackendTerminalClient: backend,
            acknowledgeRetryDelayAsync: (_, _) => Task.CompletedTask);

        await RunNewCardPaymentAsync(workflow, AckSession);
        await workflow.WaitForAcknowledgeRetriesAsync();

        // 1 次实时 + 5 次退避重试后放弃；记录仍在未结队列，等待启动自动恢复或异常中心。
        Assert.Equal(6, backend.AcknowledgeCalls);
        var stillOpen = Assert.Single(await harness.Attempts.GetOpenAttemptsAsync("S001", "POS-01", "Sandbox"));
        Assert.Equal(LocalCardPaymentAttemptStatus.Declined, stillOpen.Status);
        Assert.Null(stillOpen.AcknowledgedAt);
    }

    [Fact]
    public async Task Acknowledge_retry_queue_runs_one_chain_per_attempt_and_stops_after_success()
    {
        var attempts = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new CardAcknowledgeRetryQueue(
            async (_, _) =>
            {
                attempts++;
                await gate.Task;
                return attempts >= 2;
            },
            delayAsync: (_, _) => Task.CompletedTask,
            delays: [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)]);
        var attemptGuid = Guid.NewGuid();

        queue.Schedule(attemptGuid);
        queue.Schedule(attemptGuid);
        gate.SetResult();
        await queue.WhenIdleAsync();

        Assert.Equal(2, attempts);

        // 链结束后允许再次调度。
        queue.Schedule(attemptGuid);
        await queue.WhenIdleAsync();
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Acknowledge_retry_queue_survives_exceptions_and_gives_up_after_the_last_delay()
    {
        var calls = 0;
        var queue = new CardAcknowledgeRetryQueue(
            (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("boom");
            },
            delayAsync: (_, _) => Task.CompletedTask,
            delays: [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]);

        queue.Schedule(Guid.NewGuid());
        await queue.WhenIdleAsync();

        Assert.Equal(2, calls);
    }
}
