using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

/// <summary>
/// 退货单已完成、Square 退款仍在结算（PENDING）的跟踪：只查询同一笔退款，转为 REJECTED/FAILED 时进入待处理，
/// 主管确认已改用其他方式退款后不再提醒。用真实 SQLite 仓储验证 SQL 与 CAS 条件。
/// </summary>
public sealed class SquareRefundSettlementTests
{
    private const string OriginalPaymentId = "PAYMENT-ORIGINAL";
    private const string RefundId = "REFUND-SETTLE-1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly PosSessionState Session = new("HB POS", "S001", "Main Branch", "POS-01", "C001", "Alice", true, 0);

    [Fact]
    public async Task Pending_refund_that_square_completes_is_marked_completed_and_leaves_the_tracking_list()
    {
        await using var fixture = await Fixture.CreateAsync();
        var attempt = await fixture.SeedOrderCompletedRefundAsync("PENDING");
        fixture.Terminal.Refund = new SquareRefundStatusResult(RefundId, "COMPLETED", OriginalPaymentId, 1000, "AUD");

        var result = await fixture.Service.CheckPendingAsync(Session);

        Assert.Equal(new SquareRefundSettlementCheckResult(1, 1, 0, 0, 0), result);
        Assert.Equal(1, fixture.Terminal.GetRefundCallCount);
        Assert.Equal("COMPLETED", (await fixture.Repository.GetAttemptAsync(attempt.AttemptGuid))!.PaymentStatus);
        Assert.Empty(await fixture.Repository.GetRefundSettlementsAsync("S001", "POS-01", "Sandbox"));
    }

    [Fact]
    public async Task Pending_refund_still_pending_at_square_is_left_untouched_for_the_next_check()
    {
        await using var fixture = await Fixture.CreateAsync();
        var attempt = await fixture.SeedOrderCompletedRefundAsync("PENDING");
        fixture.Terminal.Refund = new SquareRefundStatusResult(RefundId, "PENDING", OriginalPaymentId, 1000, "AUD");

        var result = await fixture.Service.CheckPendingAsync(Session);

        Assert.Equal(new SquareRefundSettlementCheckResult(1, 0, 0, 1, 0), result);
        var stored = (await fixture.Repository.GetAttemptAsync(attempt.AttemptGuid))!;
        Assert.Equal("PENDING", stored.PaymentStatus);
        Assert.Equal(attempt.UpdatedAt, stored.UpdatedAt);
        Assert.Single(await fixture.Repository.GetRefundSettlementsAsync("S001", "POS-01", "Sandbox"));
    }

    [Theory]
    [InlineData("REJECTED")]
    [InlineData("FAILED")]
    public async Task Pending_refund_that_square_rejects_becomes_an_unhandled_failure_and_is_never_refunded_again(string squareStatus)
    {
        await using var fixture = await Fixture.CreateAsync();
        var attempt = await fixture.SeedOrderCompletedRefundAsync("PENDING");
        fixture.Terminal.Refund = new SquareRefundStatusResult(RefundId, squareStatus, OriginalPaymentId, 1000, "AUD");

        var result = await fixture.Service.CheckPendingAsync(Session);

        Assert.Equal(new SquareRefundSettlementCheckResult(1, 0, 1, 0, 0), result);
        Assert.Equal(squareStatus, (await fixture.Repository.GetAttemptAsync(attempt.AttemptGuid))!.PaymentStatus);
        Assert.Equal(1, await fixture.Service.CountUnhandledFailuresAsync(Session));
        // 失败状态已固化，后续检查不会再查询 Square，更不会重新发起退款。
        var second = await fixture.Service.CheckPendingAsync(Session);
        Assert.Equal(SquareRefundSettlementCheckResult.Empty, second);
        Assert.Equal(1, fixture.Terminal.GetRefundCallCount);
    }

    [Fact]
    public async Task Refund_response_that_does_not_match_the_local_record_is_ignored()
    {
        await using var fixture = await Fixture.CreateAsync();
        var attempt = await fixture.SeedOrderCompletedRefundAsync("PENDING");
        // 金额、原付款号、退款号任意一项对不上，都不能把结果写进这笔记录。
        foreach (var mismatch in new[]
                 {
                     new SquareRefundStatusResult(RefundId, "COMPLETED", OriginalPaymentId, 999, "AUD"),
                     new SquareRefundStatusResult(RefundId, "COMPLETED", "PAYMENT-OTHER", 1000, "AUD"),
                     new SquareRefundStatusResult("REFUND-OTHER", "COMPLETED", OriginalPaymentId, 1000, "AUD"),
                     new SquareRefundStatusResult(RefundId, "COMPLETED", OriginalPaymentId, 1000, "USD")
                 })
        {
            fixture.Terminal.Refund = mismatch;
            var result = await fixture.Service.CheckPendingAsync(Session);
            Assert.Equal(1, result.Errors);
            Assert.Equal("PENDING", (await fixture.Repository.GetAttemptAsync(attempt.AttemptGuid))!.PaymentStatus);
        }
    }

    [Fact]
    public async Task Lookup_failure_keeps_the_refund_pending_and_does_not_throw()
    {
        await using var fixture = await Fixture.CreateAsync();
        var attempt = await fixture.SeedOrderCompletedRefundAsync("PENDING");
        fixture.Terminal.GetRefundException = new HttpRequestException("offline");

        var result = await fixture.Service.CheckPendingAsync(Session);

        Assert.Equal(new SquareRefundSettlementCheckResult(1, 0, 0, 0, 1), result);
        Assert.Equal("PENDING", (await fixture.Repository.GetAttemptAsync(attempt.AttemptGuid))!.PaymentStatus);
    }

    [Fact]
    public async Task Acknowledging_a_rejected_refund_marks_it_handled_and_removes_it_from_the_count()
    {
        await using var fixture = await Fixture.CreateAsync();
        var attempt = await fixture.SeedOrderCompletedRefundAsync("REJECTED");
        Assert.Equal(1, await fixture.Service.CountUnhandledFailuresAsync(Session));

        var acknowledged = await fixture.Service.AcknowledgeFailureAsync(attempt.AttemptGuid, Session, "Paid $10 cash");

        Assert.True(acknowledged);
        var stored = (await fixture.Repository.GetAttemptAsync(attempt.AttemptGuid))!;
        Assert.Equal(SquareRefundSettlementStatuses.RejectedHandled, stored.PaymentStatus);
        Assert.Contains("Paid $10 cash", stored.ResponseText, StringComparison.Ordinal);
        Assert.Equal(0, await fixture.Service.CountUnhandledFailuresAsync(Session));
        // 已处理的失败不能被再次确认；还在结算中的退款也不允许被“确认已用其他方式退款”。
        Assert.False(await fixture.Service.AcknowledgeFailureAsync(attempt.AttemptGuid, Session, "again"));
        var pending = await fixture.SeedOrderCompletedRefundAsync("PENDING", Guid.Parse("c2000000-0000-0000-0000-000000000002"));
        Assert.False(await fixture.Service.AcknowledgeFailureAsync(pending.AttemptGuid, Session, "too early"));
    }

    [Fact]
    public async Task Recovery_service_lists_rejected_settlements_as_open_review_items_and_pending_ones_as_history()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rejected = await fixture.SeedOrderCompletedRefundAsync("FAILED", Guid.Parse("c2000000-0000-0000-0000-000000000003"));
        var pending = await fixture.SeedOrderCompletedRefundAsync("PENDING", Guid.Parse("c2000000-0000-0000-0000-000000000004"));
        var recovery = fixture.CreateRecoveryService();

        var open = await recovery.ListOpenAsync(Session);
        var history = await recovery.ListHistoryAsync(Session);

        var openItem = Assert.Single(open);
        Assert.Equal(rejected.AttemptGuid, openItem.AttemptGuid);
        Assert.Equal("SettlementRejected", openItem.Status);
        Assert.True(openItem.IsOpen);
        var pendingItem = Assert.Single(history, item => item.AttemptGuid == pending.AttemptGuid);
        Assert.Equal("SettlementPending", pendingItem.Status);
        Assert.False(pendingItem.IsOpen);
    }

    [Fact]
    public async Task Recovering_a_pending_settlement_only_looks_up_the_refund_and_acknowledging_requires_the_refunded_decision()
    {
        await using var fixture = await Fixture.CreateAsync();
        var pending = await fixture.SeedOrderCompletedRefundAsync("PENDING", Guid.Parse("c2000000-0000-0000-0000-000000000005"));
        var rejected = await fixture.SeedOrderCompletedRefundAsync("REJECTED", Guid.Parse("c2000000-0000-0000-0000-000000000006"));
        fixture.Terminal.Refund = new SquareRefundStatusResult(RefundId, "COMPLETED", OriginalPaymentId, 1000, "AUD");
        var recovery = fixture.CreateRecoveryService();

        var recovered = await recovery.RecoverAttemptAsync(pending.AttemptGuid, new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Checking, recovered.Outcome);
        Assert.Contains("completed the refund", recovered.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("COMPLETED", (await fixture.Repository.GetAttemptAsync(pending.AttemptGuid))!.PaymentStatus);

        var wrongDecision = await recovery.ResolveAttemptAsync(
            rejected.AttemptGuid, CardRecoverySupervisorDecision.ConfirmNotProcessed, "n", null, null, new PosCartService(), Session);
        Assert.False(wrongDecision.Succeeded);
        Assert.Equal(1, await fixture.Service.CountUnhandledFailuresAsync(Session));

        var ok = await recovery.ResolveAttemptAsync(
            rejected.AttemptGuid, CardRecoverySupervisorDecision.ConfirmProcessed, "paid cash", "receipt 42", null, new PosCartService(), Session);
        Assert.True(ok.Succeeded);
        Assert.Equal(0, await fixture.Service.CountUnhandledFailuresAsync(Session));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _databasePath;

        private readonly LocalSqliteStore _store;

        private Fixture(string databasePath, LocalSqliteStore store, LocalSquarePaymentAttemptRepository repository)
        {
            _databasePath = databasePath;
            _store = store;
            Repository = repository;
            Terminal = new FakeSquareTerminalPaymentClient();
            Service = new SquareRefundSettlementService(Repository, Terminal, new SandboxSettingsProvider());
        }

        public LocalSquarePaymentAttemptRepository Repository { get; }

        public FakeSquareTerminalPaymentClient Terminal { get; }

        public SquareRefundSettlementService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"hbpos-square-settlement-{Guid.NewGuid():N}.db");
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            return new Fixture(path, store, new LocalSquarePaymentAttemptRepository(store));
        }

        public SquarePaymentRecoveryService CreateRecoveryService() =>
            new(
                Repository,
                new SandboxSettingsProvider(),
                Terminal,
                new CashCheckoutService(),
                new LocalOrderRepository(_store),
                refundSettlementService: Service);

        // 退货单已完成的 Square 退款：Status=OrderCompleted，PaymentStatus 为 Square 退款状态。
        public async Task<LocalSquarePaymentAttempt> SeedOrderCompletedRefundAsync(string paymentStatus, Guid? attemptGuid = null)
        {
            var now = DateTimeOffset.Parse("2026-10-09T18:36:20+10:00");
            var draft = new CardPaymentOrderDraft(
                Guid.NewGuid(),
                Session,
                new PosCartSnapshot([]),
                [],
                -10m,
                10m,
                "R",
                $"SQ:{OriginalPaymentId}",
                now.AddSeconds(-5));
            var attempt = new LocalSquarePaymentAttempt(
                attemptGuid ?? Guid.Parse("c2000000-0000-0000-0000-000000000001"),
                CheckoutId: null,
                IdempotencyKey: $"refund-key-{Guid.NewGuid():N}",
                DeviceId: "SQ-DEVICE",
                LocationId: "SQ-LOCATION",
                Environment: "Sandbox",
                Amount: 10m,
                AmountCents: 1000,
                Currency: "AUD",
                Status: LocalSquarePaymentAttemptStatus.OrderCompleted,
                CheckoutStatus: null,
                CancelReason: null,
                OrderDraftJson: JsonSerializer.Serialize(draft, JsonOptions),
                StoreCode: "S001",
                DeviceCode: "POS-01",
                CashierId: "C001",
                PaymentId: RefundId,
                PaymentStatus: paymentStatus,
                ResponseCode: null,
                ResponseText: "Square accepted the refund; settlement is pending.",
                CreatedAt: now.AddMinutes(-1),
                UpdatedAt: now,
                CompletedAt: now,
                OrderCompletedAt: now,
                ResolvedAt: null,
                OperationKind: "Refund",
                OperationGuid: draft.OrderGuid,
                SubmissionToken: "refund-token",
                RefundBusinessKey: null);
            await Repository.CreateAsync(attempt);
            return attempt;
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSquareTerminalPaymentClient : ISquareTerminalPaymentClient
    {
        public SquareRefundStatusResult Refund { get; set; } = new(RefundId, "PENDING", OriginalPaymentId, 1000, "AUD");

        public Exception? GetRefundException { get; set; }

        public int GetRefundCallCount { get; private set; }

        public Task<SquareCheckoutStatusResult> GetCheckoutAsync(
            CardTerminalSettings settings, string checkoutId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SquarePaymentStatusResult> GetPaymentAsync(
            CardTerminalSettings settings, string paymentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SquareRefundStatusResult> GetRefundAsync(
            CardTerminalSettings settings, string refundId, CancellationToken cancellationToken = default)
        {
            GetRefundCallCount++;
            return GetRefundException is null
                ? Task.FromResult(Refund)
                : Task.FromException<SquareRefundStatusResult>(GetRefundException);
        }
    }

    private sealed class SandboxSettingsProvider : ICardTerminalSettingsProvider
    {
        public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CardTerminalSettings(
                CardProcessorKind.Square,
                CardTerminalEnvironment.Sandbox,
                "127.0.0.1",
                2011,
                "DEVICE-001",
                "LOCATION-001",
                "token",
                "https://connect.squareupsandbox.com",
                TimeSpan.FromSeconds(30),
                LinklyConnectionMode.LocalIp));
    }
}
