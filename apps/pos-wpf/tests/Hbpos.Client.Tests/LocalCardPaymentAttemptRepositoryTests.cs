using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Models;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class LocalCardPaymentAttemptRepositoryTests
{
    [Theory]
    [InlineData("Sale")]
    [InlineData("Refund")]
    public async Task Final_acknowledgement_queue_excludes_ineligible_or_out_of_scope_failures(string operationKind)
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalCardPaymentAttemptRepository(store);
            var final = CreateAttempt(
                status: LocalCardPaymentAttemptStatus.Declined,
                sessionId: "SESSION-FINAL",
                operationKind: operationKind) with
            {
                ConnectionMode = nameof(LinklyConnectionMode.CloudBackendAsync),
                CompletedAt = DateTimeOffset.UtcNow
            };
            LocalCardPaymentAttempt[] excluded =
            [
                final with { AcknowledgedAt = DateTimeOffset.UtcNow },
                final with { CompletedAt = null },
                final with { RecoveryPhase = CardRecoveryPhases.FinalizePending },
                final with { SessionId = null },
                final with { SessionId = " " },
                final with { ConnectionMode = nameof(LinklyConnectionMode.LocalIp) },
                final with { ConnectionMode = nameof(LinklyConnectionMode.CloudDirectSync) },
                final with { Status = LocalCardPaymentAttemptStatus.Abandoned },
                final with { Processor = "Square" },
                final with { StoreCode = "OTHER" },
                final with { DeviceCode = "OTHER" },
                final with { Environment = "Production" }
            ];
            foreach (var attempt in excluded)
            {
                await repository.CreateAsync(attempt with { AttemptGuid = Guid.NewGuid() });
            }

            Assert.Empty(await repository.GetOpenAttemptsAsync("S001", "POS-01", "sandbox"));
            Assert.Null(await repository.GetLatestOpenAttemptAsync("S001", "POS-01", null, "sandbox"));
            Assert.Empty(await repository.GetOpenRefundAttemptsAsync("S001", "POS-01", "sandbox"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Mark_order_completed_refuses_to_overwrite_a_terminal_attempt()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalCardPaymentAttemptRepository(store);
            var attempt = CreateAttempt(status: LocalCardPaymentAttemptStatus.Declined);
            await repository.CreateAsync(attempt);

            // 修复前这里是 WHERE AttemptGuid 的无条件 UPDATE，会把已被终端拒绝的交易
            // 直接改写成“订单已完成”。带守卫后冲突必须显式抛出，交由调用方回滚。
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => repository.MarkOrderCompletedAsync(attempt.AttemptGuid, DateTimeOffset.UtcNow));

            var stored = await repository.GetAttemptAsync(attempt.AttemptGuid);
            Assert.NotNull(stored);
            Assert.Equal(LocalCardPaymentAttemptStatus.Declined, stored!.Status);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Mark_acknowledged_refuses_an_attempt_pending_recovery_finalization()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalCardPaymentAttemptRepository(store);
            // ack 的守卫不看 Status——完成的交易本来就应该被 ack——它挡的是
            // RecoveryPhase=FinalizePending，即恢复服务或主管结案已经接管该行。
            // RecoveryTargetStatus 保持 null，使守卫中针对 Linkly 销售的放行特例不成立。
            var attempt = CreateAttempt(status: LocalCardPaymentAttemptStatus.Approved) with
            {
                RecoveryPhase = CardRecoveryPhases.FinalizePending
            };
            await repository.CreateAsync(attempt);

            // ack 原本是 WHERE AttemptGuid 的无条件 UPDATE：调用方在读取与写入之间还隔着
            // 一次 Linkly 网络请求，期间该行若被推进为 FinalizePending，盖上 ack 章会把这笔
            // 结果尚未确定的交易挤出以 AcknowledgedAt IS NULL 为条件的恢复队列。
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => repository.MarkAcknowledgedAsync(attempt.AttemptGuid, DateTimeOffset.UtcNow));

            var stored = await repository.GetAttemptAsync(attempt.AttemptGuid);
            Assert.NotNull(stored);
            Assert.Null(stored!.AcknowledgedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Mark_acknowledged_allows_a_completed_attempt_without_pending_finalization()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalCardPaymentAttemptRepository(store);
            var attempt = CreateAttempt(status: LocalCardPaymentAttemptStatus.OrderCompleted);
            await repository.CreateAsync(attempt);

            // 正常路径必须保持放行：订单已完成的交易正是应该被 ack 的对象。
            await repository.MarkAcknowledgedAsync(attempt.AttemptGuid, DateTimeOffset.UtcNow);

            var stored = await repository.GetAttemptAsync(attempt.AttemptGuid);
            Assert.NotNull(stored);
            Assert.NotNull(stored!.AcknowledgedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Local_schema_service_creates_local_card_payment_attempts_table_and_indexes()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);

            await schema.InitializeAsync();

            await using var connection = await store.OpenConnectionAsync();
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'LocalCardPaymentAttempts';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_LocalCardPaymentAttempts_RecoverLatest';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'LocalSquarePaymentAttempts';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_LocalSquarePaymentAttempts_RecoverLatest';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_LocalSquarePaymentAttempts_CheckoutId';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('LocalCardPaymentAttempts') WHERE name = 'RecoveryPhase';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('LocalCardPaymentAttempts') WHERE name = 'RecoveryTargetStatus';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('LocalSquarePaymentAttempts') WHERE name = 'RecoveryPhase';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('LocalSquarePaymentAttempts') WHERE name = 'RecoveryTargetStatus';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('LocalSquarePaymentAttempts') WHERE name = 'SupervisorFinancialReference';"));
            Assert.Equal(
                [
                    "Pending",
                    "SessionStarted",
                    "Recovering",
                    "Approved",
                    "RequiresReview",
                    "Declined",
                    "TimedOut",
                    "Cancelled",
                    "Failed",
                    "OrderCompleted",
                    "Abandoned"
                ],
                Enum.GetNames<LocalCardPaymentAttemptStatus>());
            Assert.Equal(
                [
                    "Pending",
                    "CheckoutCreated",
                    "Recovering",
                    "CheckoutCompleted",
                    "PaymentVerified",
                    "Canceled",
                    "TimedOut",
                    "Failed",
                    "Unknown",
                    "OrderCompleted",
                    "Abandoned"
                ],
                Enum.GetNames<LocalSquarePaymentAttemptStatus>());
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_attempt_repository_persists_checkout_payment_and_order_completion()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var attempt = CreateSquareAttempt();
            var checkoutAt = attempt.CreatedAt.AddMinutes(1);
            var paymentAt = attempt.CreatedAt.AddMinutes(2);
            var completedAt = attempt.CreatedAt.AddMinutes(3);

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);
            await repository.MarkCheckoutCreatedAsync(attempt.AttemptGuid, "checkout-1", "PENDING", checkoutAt);
            await repository.MarkPaymentVerifiedAsync(
                attempt.AttemptGuid,
                "payment-1",
                "COMPLETED",
                null,
                "Payment verified.",
                paymentAt);
            await repository.MarkOrderCompletedAsync(attempt.AttemptGuid, completedAt);

            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);

            Assert.NotNull(saved);
            Assert.Equal("checkout-1", saved.CheckoutId);
            Assert.Equal("payment-1", saved.PaymentId);
            Assert.Equal("COMPLETED", saved.PaymentStatus);
            Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, saved.Status);
            Assert.Equal(paymentAt, saved.CompletedAt);
            Assert.Equal(completedAt, saved.OrderCompletedAt);
            Assert.Equal(completedAt, saved.UpdatedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_MarkOrderCompleted_rejects_attempt_that_is_not_payment_verified()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var attempt = CreateSquareAttempt(
                attemptGuid: Guid.Parse("10000000-0000-0000-0000-000000000099"),
                status: LocalSquarePaymentAttemptStatus.Recovering,
                operationKind: "Sale");

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.MarkOrderCompletedAsync(
                    attempt.AttemptGuid,
                    attempt.UpdatedAt.AddMinutes(1)));
            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);

            Assert.Equal(LocalSquarePaymentAttemptStatus.Recovering, saved!.Status);
            Assert.Null(saved.OrderCompletedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_attempt_repository_mark_failed_persists_cancel_reason_with_failure_fields()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var attempt = CreateSquareAttempt();
            var resolvedAt = attempt.CreatedAt.AddMinutes(4);

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);
            await repository.MarkFailedAsync(
                attempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Canceled,
                "CANCELED",
                paymentStatus: null,
                responseCode: "BUYER_CANCELED",
                responseText: "Square checkout was canceled by the buyer.",
                resolvedAt,
                cancelReason: "BUYER_CANCELED");

            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);

            Assert.NotNull(saved);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Canceled, saved.Status);
            Assert.Equal("CANCELED", saved.CheckoutStatus);
            Assert.Equal("BUYER_CANCELED", saved.CancelReason);
            Assert.Equal("BUYER_CANCELED", saved.ResponseCode);
            Assert.Equal("Square checkout was canceled by the buyer.", saved.ResponseText);
            Assert.Equal(resolvedAt, saved.ResolvedAt);
            Assert.Equal(resolvedAt, saved.UpdatedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_attempt_repository_gets_latest_open_attempt_with_scope_filter()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var baseTime = DateTimeOffset.Parse("2026-06-05T09:00:00+10:00");
            var olderOpen = CreateSquareAttempt(
                attemptGuid: Guid.Parse("11111111-aaaa-1111-1111-111111111111"),
                status: LocalSquarePaymentAttemptStatus.Pending,
                updatedAt: baseTime);
            var latestOpen = CreateSquareAttempt(
                attemptGuid: Guid.Parse("22222222-aaaa-2222-2222-222222222222"),
                status: LocalSquarePaymentAttemptStatus.PaymentVerified,
                updatedAt: baseTime.AddMinutes(1));
            var terminal = CreateSquareAttempt(
                attemptGuid: Guid.Parse("33333333-aaaa-3333-3333-333333333333"),
                status: LocalSquarePaymentAttemptStatus.Failed,
                updatedAt: baseTime.AddMinutes(2));
            var otherCashier = CreateSquareAttempt(
                attemptGuid: Guid.Parse("44444444-aaaa-4444-4444-444444444444"),
                cashierId: "C002",
                status: LocalSquarePaymentAttemptStatus.Recovering,
                updatedAt: baseTime.AddMinutes(3));
            var otherDevice = CreateSquareAttempt(
                attemptGuid: Guid.Parse("55555555-aaaa-5555-5555-555555555555"),
                deviceCode: "POS-02",
                cashierId: "C003",
                status: LocalSquarePaymentAttemptStatus.Recovering,
                updatedAt: baseTime.AddMinutes(4));
            var otherEnvironment = CreateSquareAttempt(
                attemptGuid: Guid.Parse("66666666-aaaa-6666-6666-666666666666"),
                cashierId: "C004",
                environment: "Sandbox",
                status: LocalSquarePaymentAttemptStatus.Recovering,
                updatedAt: baseTime.AddMinutes(5));

            await schema.InitializeAsync();
            await repository.CreateAsync(olderOpen);
            await repository.CreateAsync(latestOpen);
            await repository.CreateAsync(terminal);
            await repository.CreateAsync(otherCashier);
            await repository.CreateAsync(otherDevice);
            await repository.CreateAsync(otherEnvironment);

            var saved = await repository.GetLatestOpenAttemptAsync("S001", "POS-01", "C001", "Production");
            var terminalScoped = await repository.GetLatestOpenSaleAttemptForTerminalAsync(
                "S001",
                "POS-01",
                "Production");

            Assert.NotNull(saved);
            Assert.Equal(latestOpen.AttemptGuid, saved.AttemptGuid);
            Assert.Equal(LocalSquarePaymentAttemptStatus.PaymentVerified, saved.Status);
            Assert.NotNull(terminalScoped);
            Assert.Equal(otherCashier.AttemptGuid, terminalScoped.AttemptGuid);
            Assert.Equal("C002", terminalScoped.CashierId);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_attempt_repository_returns_only_open_refunds_for_terminal_scope()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var openRefund = CreateSquareAttempt(
                attemptGuid: Guid.Parse("10101010-aaaa-bbbb-cccc-101010101010"),
                cashierId: "C002",
                status: LocalSquarePaymentAttemptStatus.Unknown,
                operationKind: "Refund");

            await schema.InitializeAsync();
            await repository.CreateAsync(CreateSquareAttempt(
                attemptGuid: Guid.Parse("20202020-aaaa-bbbb-cccc-202020202020"),
                operationKind: "Sale"));
            await repository.CreateAsync(openRefund);
            await repository.CreateAsync(CreateSquareAttempt(
                attemptGuid: Guid.Parse("30303030-aaaa-bbbb-cccc-303030303030"),
                status: LocalSquarePaymentAttemptStatus.Failed,
                operationKind: "Refund"));
            await repository.CreateAsync(CreateSquareAttempt(
                attemptGuid: Guid.Parse("40404040-aaaa-bbbb-cccc-404040404040"),
                deviceCode: "POS-02",
                status: LocalSquarePaymentAttemptStatus.Recovering,
                operationKind: "Refund"));

            var saved = await repository.GetOpenRefundAttemptsAsync("S001", "POS-01", "Production");

            var attempt = Assert.Single(saved);
            Assert.Equal(openRefund.AttemptGuid, attempt.AttemptGuid);
            Assert.Equal("C002", attempt.CashierId);
            Assert.Equal("Refund", attempt.OperationKind);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_refund_resolution_preserves_idempotency_key_and_uses_compare_and_set()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var attempt = CreateSquareAttempt(
                status: LocalSquarePaymentAttemptStatus.Unknown,
                operationKind: "Refund") with
            {
                CheckoutId = "checkout-1",
                PaymentId = null
            };

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);

            var applied = await repository.ResolveRefundAsync(new CardRefundAttemptResolution(
                attempt.AttemptGuid,
                CardRefundSupervisorDecision.ConfirmNotRefunded,
                "Checked bank portal",
                "Bank search returned no refund",
                RefundReference: null,
                RetryTxnRef: null,
                DateTimeOffset.Parse("2026-07-28T10:00:00+10:00")));
            var duplicate = await repository.ResolveRefundAsync(new CardRefundAttemptResolution(
                attempt.AttemptGuid,
                CardRefundSupervisorDecision.ConfirmRefunded,
                "Second decision must lose the race",
                Evidence: null,
                RefundReference: "refund-2",
                RetryTxnRef: null,
                DateTimeOffset.Parse("2026-07-28T10:01:00+10:00")));
            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);

            Assert.True(applied);
            Assert.False(duplicate);
            Assert.NotNull(saved);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Pending, saved.Status);
            Assert.Equal(attempt.IdempotencyKey, saved.IdempotencyKey);
            Assert.Null(saved.CheckoutId);
            Assert.Null(saved.PaymentId);
            Assert.Equal(CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded, saved.ResponseCode);
            Assert.Equal(CardRecoveryPhases.FinalizePending, saved.RecoveryPhase);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, saved.RecoveryTargetStatus);
            Assert.Contains("Bank search returned no refund", saved.ResponseText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_refund_confirmed_refunded_persists_reference_and_terminal_marker()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var attempt = CreateSquareAttempt(
                status: LocalSquarePaymentAttemptStatus.Unknown,
                operationKind: "Refund") with
            {
                CheckoutId = "checkout-refund",
                PaymentId = null
            };

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);

            var applied = await repository.ResolveRefundAsync(new CardRefundAttemptResolution(
                attempt.AttemptGuid,
                CardRefundSupervisorDecision.ConfirmRefunded,
                "Matched Square settlement",
                Evidence: null,
                RefundReference: "refund-square-1",
                RetryTxnRef: null,
                DateTimeOffset.Parse("2026-07-28T10:00:00+10:00")));
            var duplicate = await repository.ResolveRefundAsync(new CardRefundAttemptResolution(
                attempt.AttemptGuid,
                CardRefundSupervisorDecision.ContinueWaiting,
                "Late duplicate decision",
                Evidence: null,
                RefundReference: null,
                RetryTxnRef: null,
                DateTimeOffset.Parse("2026-07-28T10:01:00+10:00")));
            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);

            Assert.True(applied);
            Assert.False(duplicate);
            Assert.NotNull(saved);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Recovering, saved.Status);
            Assert.Equal(attempt.IdempotencyKey, saved.IdempotencyKey);
            Assert.Null(saved.PaymentId);
            Assert.Null(saved.PaymentStatus);
            Assert.Equal("refund-square-1", saved.SupervisorFinancialReference);
            Assert.Equal(CardRefundSupervisorResolutionCodes.ConfirmedRefunded, saved.ResponseCode);
            Assert.Equal(CardRecoveryPhases.FinalizePending, saved.RecoveryPhase);
            Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, saved.RecoveryTargetStatus);
            Assert.NotNull(saved.CompletedAt);
            Assert.NotNull(saved.ResolvedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CreateAsync_saves_and_reads_card_payment_attempt()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var expected = CreateAttempt(orderDraftJson: """{"orderGuid":"ORDER-1","lines":[]}""");

            await schema.InitializeAsync();
            await repository.CreateAsync(expected);

            var saved = await repository.GetAttemptAsync(expected.AttemptGuid);

            Assert.NotNull(saved);
            Assert.Equal(expected.AttemptGuid, saved.AttemptGuid);
            Assert.Equal(expected.SessionId, saved.SessionId);
            Assert.Equal(expected.TxnRef, saved.TxnRef);
            Assert.Equal(expected.Processor, saved.Processor);
            Assert.Equal(expected.Environment, saved.Environment);
            Assert.Equal(expected.ConnectionMode, saved.ConnectionMode);
            Assert.Equal(expected.TxnType, saved.TxnType);
            Assert.Equal(expected.Amount, saved.Amount);
            Assert.Equal(expected.Status, saved.Status);
            Assert.Equal(expected.OrderDraftJson, saved.OrderDraftJson);
            Assert.Equal(expected.StoreCode, saved.StoreCode);
            Assert.Equal(expected.DeviceCode, saved.DeviceCode);
            Assert.Equal(expected.CashierId, saved.CashierId);
            Assert.Equal(expected.ResponseCode, saved.ResponseCode);
            Assert.Equal(expected.ResponseText, saved.ResponseText);
            Assert.Equal(expected.PaymentReference, saved.PaymentReference);
            Assert.Equal(expected.CreatedAt, saved.CreatedAt);
            Assert.Equal(expected.UpdatedAt, saved.UpdatedAt);
            Assert.Equal(expected.CompletedAt, saved.CompletedAt);
            Assert.Equal(expected.AcknowledgedAt, saved.AcknowledgedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Status_update_methods_persist_session_outcome_completion_and_acknowledgement()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var attempt = CreateAttempt();
            var sessionAt = attempt.CreatedAt.AddMinutes(1);
            var approvedAt = attempt.CreatedAt.AddMinutes(2);
            var completedAt = attempt.CreatedAt.AddMinutes(3);
            var acknowledgedAt = attempt.CreatedAt.AddMinutes(4);

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);
            await repository.UpdateSessionAsync(attempt.AttemptGuid, "SESSION-001", "TXN-001", sessionAt);
            await repository.UpdateOutcomeAsync(
                attempt.AttemptGuid,
                LocalCardPaymentAttemptStatus.Approved,
                "00",
                "APPROVED",
                "PAYMENT-001",
                approvedAt);
            await repository.MarkOrderCompletedAsync(attempt.AttemptGuid, completedAt);
            await repository.MarkAcknowledgedAsync(attempt.AttemptGuid, acknowledgedAt);

            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);

            Assert.NotNull(saved);
            Assert.Equal("SESSION-001", saved.SessionId);
            Assert.Equal("TXN-001", saved.TxnRef);
            Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, saved.Status);
            Assert.Equal("00", saved.ResponseCode);
            Assert.Equal("APPROVED", saved.ResponseText);
            Assert.Equal("PAYMENT-001", saved.PaymentReference);
            Assert.Equal(approvedAt, saved.CompletedAt);
            Assert.Equal(acknowledgedAt, saved.AcknowledgedAt);
            Assert.Equal(acknowledgedAt, saved.UpdatedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Theory]
    [InlineData(LocalCardPaymentAttemptStatus.Declined)]
    [InlineData(LocalCardPaymentAttemptStatus.Failed)]
    [InlineData(LocalCardPaymentAttemptStatus.Cancelled)]
    [InlineData(LocalCardPaymentAttemptStatus.TimedOut)]
    [InlineData(LocalCardPaymentAttemptStatus.Abandoned)]
    public async Task MarkRecoveringAsync_rejects_terminal_attempt_and_does_not_change_other_key(
        LocalCardPaymentAttemptStatus terminalStatus)
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var terminalAttempt = CreateAttempt(
                attemptGuid: Guid.Parse("70000000-0000-0000-0000-000000000001"),
                status: terminalStatus);
            var otherOpenAttempt = CreateAttempt(
                attemptGuid: Guid.Parse("70000000-0000-0000-0000-000000000002"),
                status: LocalCardPaymentAttemptStatus.Pending);

            await schema.InitializeAsync();
            await repository.CreateAsync(terminalAttempt);
            await repository.CreateAsync(otherOpenAttempt);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.MarkRecoveringAsync(
                    terminalAttempt.AttemptGuid,
                    terminalAttempt.UpdatedAt.AddMinutes(1)));

            var savedTerminal = await repository.GetAttemptAsync(terminalAttempt.AttemptGuid);
            var savedOther = await repository.GetAttemptAsync(otherOpenAttempt.AttemptGuid);
            Assert.Equal(terminalStatus, savedTerminal!.Status);
            Assert.Equal(terminalAttempt.UpdatedAt, savedTerminal.UpdatedAt);
            Assert.Equal(LocalCardPaymentAttemptStatus.Pending, savedOther!.Status);
            Assert.Equal(otherOpenAttempt.UpdatedAt, savedOther.UpdatedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Theory]
    [InlineData(LocalCardPaymentAttemptStatus.Pending, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData(LocalCardPaymentAttemptStatus.SessionStarted, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData(LocalCardPaymentAttemptStatus.Recovering, LocalCardPaymentAttemptStatus.Recovering)]
    [InlineData(LocalCardPaymentAttemptStatus.Approved, LocalCardPaymentAttemptStatus.Approved)]
    public async Task MarkRecoveringAsync_accepts_open_source_without_downgrading_approved_evidence(
        LocalCardPaymentAttemptStatus sourceStatus,
        LocalCardPaymentAttemptStatus expectedStatus)
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var attempt = CreateAttempt(status: sourceStatus);
            var recoveringAt = attempt.UpdatedAt.AddMinutes(1);

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);
            await repository.MarkRecoveringAsync(attempt.AttemptGuid, recoveringAt);

            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);
            Assert.Equal(expectedStatus, saved!.Status);
            Assert.Equal(recoveringAt, saved.UpdatedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetLatestOpenAttemptAsync_filters_scope_and_ignores_terminal_statuses()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var baseTime = DateTimeOffset.Parse("2026-06-05T09:00:00+10:00");
            var olderOpen = CreateAttempt(
                attemptGuid: Guid.Parse("11111111-1111-1111-1111-111111111111"),
                status: LocalCardPaymentAttemptStatus.Pending,
                updatedAt: baseTime);
            var latestOpen = CreateAttempt(
                attemptGuid: Guid.Parse("22222222-2222-2222-2222-222222222222"),
                status: LocalCardPaymentAttemptStatus.Approved,
                updatedAt: baseTime.AddMinutes(1));
            var terminal = CreateAttempt(
                attemptGuid: Guid.Parse("33333333-3333-3333-3333-333333333333"),
                status: LocalCardPaymentAttemptStatus.Declined,
                updatedAt: baseTime.AddMinutes(2));
            var otherCashier = CreateAttempt(
                attemptGuid: Guid.Parse("44444444-4444-4444-4444-444444444444"),
                cashierId: "C002",
                status: LocalCardPaymentAttemptStatus.Recovering,
                updatedAt: baseTime.AddMinutes(3));
            var otherEnvironment = CreateAttempt(
                attemptGuid: Guid.Parse("55555555-5555-5555-5555-555555555555"),
                environment: "prod",
                status: LocalCardPaymentAttemptStatus.SessionStarted,
                updatedAt: baseTime.AddMinutes(4));

            await schema.InitializeAsync();
            await repository.CreateAsync(olderOpen);
            await repository.CreateAsync(latestOpen);
            await repository.CreateAsync(terminal);
            await repository.CreateAsync(otherCashier);
            await repository.CreateAsync(otherEnvironment);

            var saved = await repository.GetLatestOpenAttemptAsync("S001", "POS-01", "C001", "sandbox");

            Assert.NotNull(saved);
            Assert.Equal(latestOpen.AttemptGuid, saved.AttemptGuid);
            Assert.Equal(LocalCardPaymentAttemptStatus.Approved, saved.Status);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Card_attempt_repository_returns_only_open_refunds_for_terminal_scope()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var openRefund = CreateAttempt(
                attemptGuid: Guid.Parse("50505050-1111-2222-3333-505050505050"),
                cashierId: "C002",
                status: LocalCardPaymentAttemptStatus.RequiresReview,
                operationKind: "Refund");

            await schema.InitializeAsync();
            await repository.CreateAsync(CreateAttempt(
                attemptGuid: Guid.Parse("60606060-1111-2222-3333-606060606060"),
                operationKind: "Sale"));
            await repository.CreateAsync(openRefund);
            await repository.CreateAsync(CreateAttempt(
                attemptGuid: Guid.Parse("70707070-1111-2222-3333-707070707070"),
                status: LocalCardPaymentAttemptStatus.Declined,
                operationKind: "Refund"));
            await repository.CreateAsync(CreateAttempt(
                attemptGuid: Guid.Parse("80808080-1111-2222-3333-808080808080"),
                deviceCode: "POS-02",
                status: LocalCardPaymentAttemptStatus.Recovering,
                operationKind: "Refund"));

            var saved = await repository.GetOpenRefundAttemptsAsync("S001", "POS-01", "sandbox");

            var attempt = Assert.Single(saved);
            Assert.Equal(openRefund.AttemptGuid, attempt.AttemptGuid);
            Assert.Equal("C002", attempt.CashierId);
            Assert.Equal("Refund", attempt.OperationKind);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Linkly_refund_resolution_persists_new_txn_ref_and_uses_compare_and_set()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var attempt = CreateAttempt(
                status: LocalCardPaymentAttemptStatus.Recovering,
                sessionId: "session-1",
                txnRef: "old-txn-ref",
                operationKind: "Refund");

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);

            var applied = await repository.ResolveRefundAsync(new CardRefundAttemptResolution(
                attempt.AttemptGuid,
                CardRefundSupervisorDecision.ConfirmNotRefunded,
                "Checked bank portal",
                "No matching refund was found",
                RefundReference: null,
                RetryTxnRef: "new-refund-txn-ref",
                DateTimeOffset.Parse("2026-07-28T10:00:00+10:00")));
            var duplicate = await repository.ResolveRefundAsync(new CardRefundAttemptResolution(
                attempt.AttemptGuid,
                CardRefundSupervisorDecision.ConfirmRefunded,
                "Second decision must lose the race",
                Evidence: null,
                RefundReference: "refund-2",
                RetryTxnRef: null,
                DateTimeOffset.Parse("2026-07-28T10:01:00+10:00")));
            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);

            Assert.True(applied);
            Assert.False(duplicate);
            Assert.NotNull(saved);
            Assert.Equal(LocalCardPaymentAttemptStatus.Pending, saved.Status);
            Assert.Null(saved.SessionId);
            Assert.Equal("new-refund-txn-ref", saved.TxnRef);
            Assert.Equal(CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded, saved.ResponseCode);
            Assert.Contains("No matching refund was found", saved.ResponseText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Linkly_refund_confirmed_refunded_persists_reference_and_terminal_marker()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var attempt = CreateAttempt(
                status: LocalCardPaymentAttemptStatus.Recovering,
                sessionId: "session-refund",
                txnRef: "txn-refund",
                operationKind: "Refund");

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);

            var applied = await repository.ResolveRefundAsync(new CardRefundAttemptResolution(
                attempt.AttemptGuid,
                CardRefundSupervisorDecision.ConfirmRefunded,
                "Matched Linkly settlement",
                Evidence: null,
                RefundReference: "refund-linkly-1",
                RetryTxnRef: null,
                DateTimeOffset.Parse("2026-07-28T10:00:00+10:00")));
            var duplicate = await repository.ResolveRefundAsync(new CardRefundAttemptResolution(
                attempt.AttemptGuid,
                CardRefundSupervisorDecision.ContinueWaiting,
                "Late duplicate decision",
                Evidence: null,
                RefundReference: null,
                RetryTxnRef: null,
                DateTimeOffset.Parse("2026-07-28T10:01:00+10:00")));
            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);

            Assert.True(applied);
            Assert.False(duplicate);
            Assert.NotNull(saved);
            Assert.Equal(LocalCardPaymentAttemptStatus.Approved, saved.Status);
            Assert.Equal("refund-linkly-1", saved.PaymentReference);
            Assert.Equal(CardRefundSupervisorResolutionCodes.ConfirmedRefunded, saved.ResponseCode);
            Assert.NotNull(saved.CompletedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetLatestOpenAttemptAsync_without_cashier_filter_returns_latest_open_attempt_for_terminal()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var baseTime = DateTimeOffset.Parse("2026-06-29T10:30:00+10:00");
            var currentCashierAttempt = CreateAttempt(
                attemptGuid: Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111"),
                cashierId: "C001",
                status: LocalCardPaymentAttemptStatus.Pending,
                updatedAt: baseTime);
            var emergencyAttempt = CreateAttempt(
                attemptGuid: Guid.Parse("aaaaaaaa-2222-2222-2222-222222222222"),
                cashierId: "EMERGENCY",
                status: LocalCardPaymentAttemptStatus.SessionStarted,
                updatedAt: baseTime.AddMinutes(1));
            var otherTerminalAttempt = CreateAttempt(
                attemptGuid: Guid.Parse("aaaaaaaa-3333-3333-3333-333333333333"),
                deviceCode: "POS-02",
                cashierId: "EMERGENCY",
                status: LocalCardPaymentAttemptStatus.Recovering,
                updatedAt: baseTime.AddMinutes(2));

            await schema.InitializeAsync();
            await repository.CreateAsync(currentCashierAttempt);
            await repository.CreateAsync(emergencyAttempt);
            await repository.CreateAsync(otherTerminalAttempt);

            var saved = await repository.GetLatestOpenAttemptAsync("S001", "POS-01", null, "sandbox");

            Assert.NotNull(saved);
            Assert.Equal(emergencyAttempt.AttemptGuid, saved.AttemptGuid);
            Assert.Equal("EMERGENCY", saved.CashierId);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetLatestOpenAttemptAsync_returns_requires_review_attempt()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var attempt = CreateAttempt(status: LocalCardPaymentAttemptStatus.RequiresReview);

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);

            var saved = await repository.GetLatestOpenAttemptAsync("S001", "POS-01", "C001", "sandbox");

            Assert.NotNull(saved);
            Assert.Equal(attempt.AttemptGuid, saved.AttemptGuid);
            Assert.Equal(LocalCardPaymentAttemptStatus.RequiresReview, saved.Status);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetLatestOpenAttemptAsync_returns_unacknowledged_order_completed_attempt_with_session()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var attempt = CreateAttempt(
                status: LocalCardPaymentAttemptStatus.OrderCompleted,
                sessionId: "SESSION-ACK",
                txnRef: "TXN-ACK");

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);

            var saved = await repository.GetLatestOpenAttemptAsync("S001", "POS-01", "C001", "sandbox");

            Assert.NotNull(saved);
            Assert.Equal(attempt.AttemptGuid, saved.AttemptGuid);
            Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, saved.Status);
            Assert.Equal("SESSION-ACK", saved.SessionId);
            Assert.Null(saved.AcknowledgedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetLatestOpenAttemptAsync_ignores_acknowledged_or_unbound_order_completed_attempts()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var baseTime = DateTimeOffset.Parse("2026-06-05T09:00:00+10:00");
            var acknowledged = CreateAttempt(
                attemptGuid: Guid.Parse("66666666-6666-6666-6666-666666666666"),
                status: LocalCardPaymentAttemptStatus.OrderCompleted,
                sessionId: "SESSION-DONE",
                txnRef: "TXN-DONE",
                updatedAt: baseTime.AddMinutes(1),
                acknowledgedAt: baseTime.AddMinutes(2));
            var unbound = CreateAttempt(
                attemptGuid: Guid.Parse("77777777-7777-7777-7777-777777777777"),
                status: LocalCardPaymentAttemptStatus.OrderCompleted,
                sessionId: null,
                txnRef: "TXN-NO-SESSION",
                updatedAt: baseTime.AddMinutes(3));

            await schema.InitializeAsync();
            await repository.CreateAsync(acknowledged);
            await repository.CreateAsync(unbound);

            var saved = await repository.GetLatestOpenAttemptAsync("S001", "POS-01", "C001", "sandbox");

            Assert.Null(saved);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetUnacknowledgedInstallmentAttemptsAsync_returns_only_unacknowledged_installment_attempts_with_a_txn_ref_in_scope()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalCardPaymentAttemptRepository(store);
            var included = new[]
            {
                CreateAttempt(Guid.NewGuid(), txnRef: "PINSTALLMENT0001", operationKind: "Create"),
                CreateAttempt(Guid.NewGuid(), txnRef: "PINSTALLMENT0002", operationKind: "Repayment", sessionId: "SESSION-1")
            };
            LocalCardPaymentAttempt[] excluded =
            [
                CreateAttempt(Guid.NewGuid(), txnRef: "PINSTALLMENT0003", operationKind: "Repayment", acknowledgedAt: DateTimeOffset.UtcNow),
                CreateAttempt(Guid.NewGuid(), txnRef: null, operationKind: "Repayment"),
                CreateAttempt(Guid.NewGuid(), txnRef: "PSALE000000000001", operationKind: "Sale"),
                CreateAttempt(Guid.NewGuid(), txnRef: "PINSTALLMENT0004", operationKind: "Repayment", storeCode: "OTHER"),
                CreateAttempt(Guid.NewGuid(), txnRef: "PINSTALLMENT0005", operationKind: "Repayment", deviceCode: "OTHER"),
                CreateAttempt(Guid.NewGuid(), txnRef: "PINSTALLMENT0006", operationKind: "Repayment", environment: "Production")
            ];
            foreach (var attempt in included.Concat(excluded))
            {
                await repository.CreateAsync(attempt);
            }

            var result = await repository.GetUnacknowledgedInstallmentAttemptsAsync("S001", "POS-01", "sandbox");

            Assert.Equal(
                included.Select(attempt => attempt.AttemptGuid).Order(),
                result.Select(attempt => attempt.AttemptGuid).Order());
            // 分期 attempt 不进入异常中心队列：队列里只剩范围内那条销售。
            var queue = await repository.GetOpenAttemptsAsync("S001", "POS-01", "sandbox");
            Assert.Equal("Sale", Assert.Single(queue).OperationKind);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Linkly_GetOpenAttemptsAsync_returns_all_open_sale_refund_and_active_session_attempts_across_cashiers()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var baseTime = DateTimeOffset.Parse("2026-06-05T09:00:00+10:00");

            var salePending = CreateAttempt(
                attemptGuid: Guid.Parse("10000000-0000-0000-0000-000000000001"),
                status: LocalCardPaymentAttemptStatus.Pending,
                updatedAt: baseTime);
            var saleApproved = CreateAttempt(
                attemptGuid: Guid.Parse("10000000-0000-0000-0000-000000000002"),
                status: LocalCardPaymentAttemptStatus.Approved,
                updatedAt: baseTime.AddMinutes(1));
            var saleDeclined = CreateAttempt(
                attemptGuid: Guid.Parse("10000000-0000-0000-0000-000000000003"),
                status: LocalCardPaymentAttemptStatus.Declined,
                updatedAt: baseTime.AddMinutes(2));
            var refundOpen = CreateAttempt(
                attemptGuid: Guid.Parse("10000000-0000-0000-0000-000000000004"),
                status: LocalCardPaymentAttemptStatus.RequiresReview,
                operationKind: "Refund",
                updatedAt: baseTime.AddMinutes(3));
            var refundFailed = CreateAttempt(
                attemptGuid: Guid.Parse("10000000-0000-0000-0000-000000000005"),
                status: LocalCardPaymentAttemptStatus.Failed,
                operationKind: "Refund",
                updatedAt: baseTime.AddMinutes(4));
            var activeOpen = CreateAttempt(
                attemptGuid: Guid.Parse("10000000-0000-0000-0000-000000000006"),
                status: LocalCardPaymentAttemptStatus.Pending,
                operationKind: "ActiveSession",
                sessionId: "SESSION-OPEN",
                updatedAt: baseTime.AddMinutes(5));
            var activeAcknowledged = CreateAttempt(
                attemptGuid: Guid.Parse("10000000-0000-0000-0000-000000000007"),
                status: LocalCardPaymentAttemptStatus.Cancelled,
                operationKind: "ActiveSession",
                sessionId: "SESSION-DONE",
                updatedAt: baseTime.AddMinutes(6),
                acknowledgedAt: baseTime.AddMinutes(6));
            var activeAcknowledgedReview = CreateAttempt(
                attemptGuid: Guid.Parse("10000000-0000-0000-0000-000000000011"),
                status: LocalCardPaymentAttemptStatus.RequiresReview,
                operationKind: "ActiveSession",
                sessionId: "SESSION-APPROVED-REVIEW",
                updatedAt: baseTime.AddMinutes(6).AddSeconds(30),
                acknowledgedAt: baseTime.AddMinutes(6).AddSeconds(30));
            var otherCashier = CreateAttempt(
                attemptGuid: Guid.Parse("10000000-0000-0000-0000-000000000008"),
                cashierId: "C999",
                status: LocalCardPaymentAttemptStatus.Recovering,
                updatedAt: baseTime.AddMinutes(7));
            var otherDevice = CreateAttempt(
                attemptGuid: Guid.Parse("10000000-0000-0000-0000-000000000009"),
                deviceCode: "POS-99",
                status: LocalCardPaymentAttemptStatus.Pending,
                updatedAt: baseTime.AddMinutes(8));
            var otherEnvironment = CreateAttempt(
                attemptGuid: Guid.Parse("10000000-0000-0000-0000-000000000010"),
                environment: "Production",
                status: LocalCardPaymentAttemptStatus.Pending,
                updatedAt: baseTime.AddMinutes(9));

            await schema.InitializeAsync();
            foreach (var attempt in new[]
            {
                salePending,
                saleApproved,
                saleDeclined,
                refundOpen,
                refundFailed,
                activeOpen,
                activeAcknowledged,
                activeAcknowledgedReview,
                otherCashier,
                otherDevice,
                otherEnvironment
            })
            {
                await repository.CreateAsync(attempt);
            }

            var open = await repository.GetOpenAttemptsAsync("S001", "POS-01", "sandbox");

            Assert.Equal(
                [
                    otherCashier.AttemptGuid,
                    activeAcknowledgedReview.AttemptGuid,
                    activeOpen.AttemptGuid,
                    refundOpen.AttemptGuid,
                    saleApproved.AttemptGuid,
                    salePending.AttemptGuid
                ],
                open.Select(attempt => attempt.AttemptGuid).ToArray());
            Assert.DoesNotContain(open, attempt => attempt.Status == LocalCardPaymentAttemptStatus.Declined);
            Assert.DoesNotContain(open, attempt => attempt.Status == LocalCardPaymentAttemptStatus.Failed);
            Assert.DoesNotContain(open, attempt => attempt.AttemptGuid == activeAcknowledged.AttemptGuid);
            Assert.Contains(open, attempt => attempt.AttemptGuid == activeAcknowledgedReview.AttemptGuid);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_GetOpenAttemptsAsync_returns_all_open_sale_and_refund_attempts_across_cashiers()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var baseTime = DateTimeOffset.Parse("2026-06-05T09:00:00+10:00");

            var salePending = CreateSquareAttempt(
                attemptGuid: Guid.Parse("20000000-0000-0000-0000-000000000001"),
                status: LocalSquarePaymentAttemptStatus.Pending,
                updatedAt: baseTime);
            var saleFailed = CreateSquareAttempt(
                attemptGuid: Guid.Parse("20000000-0000-0000-0000-000000000002"),
                status: LocalSquarePaymentAttemptStatus.Failed,
                updatedAt: baseTime.AddMinutes(1));
            var refundOpen = CreateSquareAttempt(
                attemptGuid: Guid.Parse("20000000-0000-0000-0000-000000000003"),
                status: LocalSquarePaymentAttemptStatus.Unknown,
                operationKind: "Refund",
                updatedAt: baseTime.AddMinutes(2));
            var refundCanceled = CreateSquareAttempt(
                attemptGuid: Guid.Parse("20000000-0000-0000-0000-000000000004"),
                status: LocalSquarePaymentAttemptStatus.Canceled,
                operationKind: "Refund",
                updatedAt: baseTime.AddMinutes(3));
            var otherCashier = CreateSquareAttempt(
                attemptGuid: Guid.Parse("20000000-0000-0000-0000-000000000005"),
                cashierId: "C999",
                status: LocalSquarePaymentAttemptStatus.Recovering,
                updatedAt: baseTime.AddMinutes(4));
            var otherDevice = CreateSquareAttempt(
                attemptGuid: Guid.Parse("20000000-0000-0000-0000-000000000006"),
                deviceCode: "POS-99",
                status: LocalSquarePaymentAttemptStatus.Pending,
                updatedAt: baseTime.AddMinutes(5));
            var otherEnvironment = CreateSquareAttempt(
                attemptGuid: Guid.Parse("20000000-0000-0000-0000-000000000007"),
                environment: "Sandbox",
                status: LocalSquarePaymentAttemptStatus.Pending,
                updatedAt: baseTime.AddMinutes(6));

            await schema.InitializeAsync();
            foreach (var attempt in new[]
            {
                salePending,
                saleFailed,
                refundOpen,
                refundCanceled,
                otherCashier,
                otherDevice,
                otherEnvironment
            })
            {
                await repository.CreateAsync(attempt);
            }

            var open = await repository.GetOpenAttemptsAsync("S001", "POS-01", "Production");

            Assert.Equal(
                [
                    otherCashier.AttemptGuid,
                    refundOpen.AttemptGuid,
                    salePending.AttemptGuid
                ],
                open.Select(attempt => attempt.AttemptGuid).ToArray());
            Assert.DoesNotContain(open, attempt => attempt.Status == LocalSquarePaymentAttemptStatus.Failed);
            Assert.DoesNotContain(open, attempt => attempt.OperationKind == "Refund" && attempt.Status == LocalSquarePaymentAttemptStatus.Canceled);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Local_schema_service_migrates_open_attempts_to_none_phase_without_changing_status()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await using (var connection = await store.OpenConnectionAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE LocalCardPaymentAttempts (
                        AttemptGuid TEXT PRIMARY KEY, SessionId TEXT NULL, TxnRef TEXT NULL,
                        Processor TEXT NOT NULL, Environment TEXT NOT NULL, ConnectionMode TEXT NOT NULL,
                        TxnType TEXT NOT NULL, Amount TEXT NOT NULL, Status TEXT NOT NULL,
                        OrderDraftJson TEXT NOT NULL, StoreCode TEXT NOT NULL, DeviceCode TEXT NOT NULL,
                        CashierId TEXT NOT NULL, ResponseCode TEXT NULL, ResponseText TEXT NULL,
                        PaymentReference TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
                        CompletedAt TEXT NULL, AcknowledgedAt TEXT NULL,
                        OperationKind TEXT NOT NULL DEFAULT 'Sale', OperationGuid TEXT NULL,
                        SubmissionToken TEXT NULL, RefundBusinessKey TEXT NULL);
                    CREATE TABLE LocalSquarePaymentAttempts (
                        AttemptGuid TEXT PRIMARY KEY, CheckoutId TEXT NULL, IdempotencyKey TEXT NOT NULL,
                        DeviceId TEXT NOT NULL, LocationId TEXT NOT NULL, Environment TEXT NOT NULL,
                        Amount TEXT NOT NULL, AmountCents INTEGER NOT NULL, Currency TEXT NOT NULL,
                        Status TEXT NOT NULL, CheckoutStatus TEXT NULL, CancelReason TEXT NULL,
                        OrderDraftJson TEXT NOT NULL, StoreCode TEXT NOT NULL, DeviceCode TEXT NOT NULL,
                        CashierId TEXT NOT NULL, PaymentId TEXT NULL, PaymentStatus TEXT NULL,
                        ResponseCode TEXT NULL, ResponseText TEXT NULL, CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL, CompletedAt TEXT NULL, OrderCompletedAt TEXT NULL,
                        ResolvedAt TEXT NULL, OperationKind TEXT NOT NULL DEFAULT 'Sale',
                        OperationGuid TEXT NULL, SubmissionToken TEXT NULL, RefundBusinessKey TEXT NULL);
                    INSERT INTO LocalCardPaymentAttempts (
                        AttemptGuid, Processor, Environment, ConnectionMode, TxnType, Amount, Status,
                        OrderDraftJson, StoreCode, DeviceCode, CashierId, CreatedAt, UpdatedAt)
                    VALUES ('11111111-1111-1111-1111-111111111111', 'Linkly', 'Production', 'LocalIp',
                        'P', '10', 'Approved', '{}', 'S001', 'POS-01', 'C001', '2026-01-01', '2026-01-02');
                    INSERT INTO LocalSquarePaymentAttempts (
                        AttemptGuid, IdempotencyKey, DeviceId, LocationId, Environment, Amount,
                        AmountCents, Currency, Status, OrderDraftJson, StoreCode, DeviceCode,
                        CashierId, CreatedAt, UpdatedAt)
                    VALUES ('22222222-2222-2222-2222-222222222222', 'idem', 'dev', 'loc', 'Production',
                        '10', 1000, 'AUD', 'PaymentVerified', '{}', 'S001', 'POS-01', 'C001',
                        '2026-01-01', '2026-01-02');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await new LocalSchemaService(store).InitializeAsync();

            await using var migrated = await store.OpenConnectionAsync();
            Assert.Equal(
                "Approved|None",
                await ReadScalarStringAsync(
                    migrated,
                    "SELECT Status || '|' || RecoveryPhase FROM LocalCardPaymentAttempts LIMIT 1;"));
            Assert.Equal(
                "PaymentVerified|None",
                await ReadScalarStringAsync(
                    migrated,
                    "SELECT Status || '|' || RecoveryPhase FROM LocalSquarePaymentAttempts LIMIT 1;"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_payment_resolution_persists_three_state_cas_and_journal()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var resolvedAt = DateTimeOffset.Parse("2026-07-28T10:00:00+10:00");

            await schema.InitializeAsync();

            var confirmedAttempt = CreateSquareAttempt(
                attemptGuid: Guid.Parse("40000000-0000-0000-0000-000000000001"),
                status: LocalSquarePaymentAttemptStatus.Recovering,
                operationKind: "Sale");
            await repository.CreateAsync(confirmedAttempt);
            var confirmed = await repository.ResolvePaymentWithJournalAsync(
                new SquarePaymentResolution(
                    confirmedAttempt.AttemptGuid,
                    CardRecoverySupervisorDecision.ConfirmProcessed,
                    "Confirmed paid",
                    "Bank matched",
                    "ref-1",
                    confirmedAttempt.Status,
                    confirmedAttempt.UpdatedAt,
                    resolvedAt),
                CreateSquareSaleJournal(confirmedAttempt.AttemptGuid, "ConfirmProcessed", "Confirmed paid"));
            var confirmedSaved = await repository.GetAttemptAsync(confirmedAttempt.AttemptGuid);
            Assert.True(confirmed);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Recovering, confirmedSaved!.Status);
            Assert.Equal("SUPERVISOR_CONFIRMED_PAID", confirmedSaved.ResponseCode);
            Assert.Null(confirmedSaved.PaymentId);
            Assert.Equal("ref-1", confirmedSaved.SupervisorFinancialReference);
            Assert.Equal(CardRecoveryPhases.FinalizePending, confirmedSaved.RecoveryPhase);
            Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, confirmedSaved.RecoveryTargetStatus);

            var duplicate = await repository.ResolvePaymentWithJournalAsync(
                new SquarePaymentResolution(
                    confirmedAttempt.AttemptGuid,
                    CardRecoverySupervisorDecision.ConfirmProcessed,
                    "Duplicate",
                    Evidence: "Bank matched again",
                    PaymentReference: "ref-duplicate",
                    confirmedAttempt.Status,
                    confirmedAttempt.UpdatedAt,
                    resolvedAt),
                CreateSquareSaleJournal(confirmedAttempt.AttemptGuid, "ConfirmProcessed", "Duplicate"));
            Assert.False(duplicate);

            var notPaidAttempt = CreateSquareAttempt(
                attemptGuid: Guid.Parse("40000000-0000-0000-0000-000000000002"),
                status: LocalSquarePaymentAttemptStatus.Recovering,
                operationKind: "Sale") with
            {
                CheckoutId = "CHECKOUT-NOTPAID"
            };
            await repository.CreateAsync(notPaidAttempt);
            var notPaid = await repository.ResolvePaymentWithJournalAsync(
                new SquarePaymentResolution(
                    notPaidAttempt.AttemptGuid,
                    CardRecoverySupervisorDecision.ConfirmNotProcessed,
                    "Not paid",
                    "Bank returned nothing",
                    PaymentReference: null,
                    notPaidAttempt.Status,
                    notPaidAttempt.UpdatedAt,
                    resolvedAt),
                CreateSquareSaleJournal(notPaidAttempt.AttemptGuid, "ConfirmNotProcessed", "Not paid"));
            var notPaidSaved = await repository.GetAttemptAsync(notPaidAttempt.AttemptGuid);
            Assert.True(notPaid);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Pending, notPaidSaved!.Status);
            Assert.Equal("SUPERVISOR_CONFIRMED_NOT_PAID", notPaidSaved.ResponseCode);
            Assert.Null(notPaidSaved.CheckoutId);
            Assert.Equal(CardRecoveryPhases.FinalizePending, notPaidSaved.RecoveryPhase);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, notPaidSaved.RecoveryTargetStatus);

            var waitingAttempt = CreateSquareAttempt(
                attemptGuid: Guid.Parse("40000000-0000-0000-0000-000000000003"),
                status: LocalSquarePaymentAttemptStatus.Recovering,
                operationKind: "Sale");
            await repository.CreateAsync(waitingAttempt);
            var waiting = await repository.ResolvePaymentWithJournalAsync(
                new SquarePaymentResolution(
                    waitingAttempt.AttemptGuid,
                    CardRecoverySupervisorDecision.ContinueWaiting,
                    "Keep waiting",
                    Evidence: null,
                    PaymentReference: null,
                    waitingAttempt.Status,
                    waitingAttempt.UpdatedAt,
                    resolvedAt),
                CreateSquareSaleJournal(waitingAttempt.AttemptGuid, "ContinueWaiting", "Keep waiting"));
            var waitingSaved = await repository.GetAttemptAsync(waitingAttempt.AttemptGuid);
            Assert.True(waiting);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Recovering, waitingSaved!.Status);
            Assert.Equal("SUPERVISOR_CONTINUE_WAITING", waitingSaved.ResponseCode);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_payment_resolution_cannot_downgrade_verified_sale_or_clear_payment_evidence()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var verifiedAt = DateTimeOffset.Parse("2026-07-28T09:00:00+10:00");
            var attempt = CreateSquareAttempt(
                attemptGuid: Guid.Parse("40000000-0000-0000-0000-000000000004"),
                status: LocalSquarePaymentAttemptStatus.PaymentVerified,
                operationKind: "Sale",
                updatedAt: verifiedAt) with
            {
                CheckoutId = "CHECKOUT-VERIFIED",
                PaymentId = "PAYMENT-VERIFIED",
                PaymentStatus = "COMPLETED",
                CompletedAt = verifiedAt
            };

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);

            var applied = await repository.ResolvePaymentWithJournalAsync(
                new SquarePaymentResolution(
                    attempt.AttemptGuid,
                    CardRecoverySupervisorDecision.ConfirmNotProcessed,
                    "Not paid",
                    "Bank returned nothing",
                    PaymentReference: null,
                    attempt.Status,
                    attempt.UpdatedAt,
                    verifiedAt.AddMinutes(1)),
                CreateSquareSaleJournal(attempt.AttemptGuid, "ConfirmNotProcessed", "Not paid"));
            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);

            Assert.False(applied);
            Assert.Equal(LocalSquarePaymentAttemptStatus.PaymentVerified, saved!.Status);
            Assert.Equal("CHECKOUT-VERIFIED", saved.CheckoutId);
            Assert.Equal("PAYMENT-VERIFIED", saved.PaymentId);
            Assert.Equal("COMPLETED", saved.PaymentStatus);
            Assert.Equal(verifiedAt, saved.CompletedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_sale_automatic_updates_cannot_overwrite_supervisor_paid_or_not_paid_closure()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var resolvedAt = DateTimeOffset.Parse("2026-07-28T09:00:00+10:00");
            var paidAttempt = CreateSquareAttempt(
                attemptGuid: Guid.Parse("40000000-0000-0000-0000-000000000005"),
                status: LocalSquarePaymentAttemptStatus.PaymentVerified,
                operationKind: "Sale",
                updatedAt: resolvedAt) with
            {
                PaymentId = "PAYMENT-SUPERVISOR",
                PaymentStatus = "COMPLETED",
                ResponseCode = ActiveSessionSupervisorResolutionCodes.ConfirmedPaid,
                ResponseText = "Bank matched"
            };
            var notPaidAttempt = CreateSquareAttempt(
                attemptGuid: Guid.Parse("40000000-0000-0000-0000-000000000006"),
                status: LocalSquarePaymentAttemptStatus.Pending,
                operationKind: "Sale",
                updatedAt: resolvedAt) with
            {
                ResponseCode = ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid,
                ResponseText = "Bank confirmed no charge"
            };

            await schema.InitializeAsync();
            await repository.CreateAsync(paidAttempt);
            await repository.CreateAsync(notPaidAttempt);

            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.MarkCheckoutCreatedAsync(
                paidAttempt.AttemptGuid,
                "STALE-CHECKOUT",
                "PENDING",
                resolvedAt.AddMinutes(1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateCheckoutStatusAsync(
                paidAttempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Canceled,
                "CANCELED",
                "STALE",
                resolvedAt.AddMinutes(1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.MarkPaymentVerifiedAsync(
                notPaidAttempt.AttemptGuid,
                "STALE-PAYMENT",
                "COMPLETED",
                responseCode: null,
                responseText: "stale automatic result",
                resolvedAt.AddMinutes(1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.MarkFailedAsync(
                notPaidAttempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Unknown,
                "FAILED",
                paymentStatus: null,
                responseCode: null,
                responseText: "stale automatic failure",
                resolvedAt.AddMinutes(1)));

            var paidSaved = await repository.GetAttemptAsync(paidAttempt.AttemptGuid);
            Assert.Equal(LocalSquarePaymentAttemptStatus.PaymentVerified, paidSaved!.Status);
            Assert.Equal("PAYMENT-SUPERVISOR", paidSaved.PaymentId);
            Assert.Equal(ActiveSessionSupervisorResolutionCodes.ConfirmedPaid, paidSaved.ResponseCode);
            var notPaidSaved = await repository.GetAttemptAsync(notPaidAttempt.AttemptGuid);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Pending, notPaidSaved!.Status);
            Assert.Equal(ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid, notPaidSaved.ResponseCode);
            Assert.Equal("Bank confirmed no charge", notPaidSaved.ResponseText);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_automatic_writes_from_stale_worker_cannot_downgrade_verified_or_completed_sale()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var winner = new LocalSquarePaymentAttemptRepository(store);
            var staleWorker = new LocalSquarePaymentAttemptRepository(store);
            var attempt = CreateSquareAttempt(
                attemptGuid: Guid.Parse("50000000-0000-0000-0000-000000000004"),
                status: LocalSquarePaymentAttemptStatus.CheckoutCreated,
                operationKind: "Sale");

            await schema.InitializeAsync();
            await winner.CreateAsync(attempt);
            await winner.MarkPaymentVerifiedAsync(
                attempt.AttemptGuid,
                "PAYMENT-WINNER",
                "COMPLETED",
                responseCode: null,
                responseText: "verified by winner",
                attempt.UpdatedAt.AddMinutes(1));

            await Assert.ThrowsAsync<InvalidOperationException>(() => staleWorker.UpdateCheckoutStatusAsync(
                attempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Recovering,
                "PENDING",
                null,
                attempt.UpdatedAt.AddMinutes(2)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => staleWorker.MarkFailedAsync(
                attempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Unknown,
                "FAILED",
                null,
                null,
                "stale failure",
                attempt.UpdatedAt.AddMinutes(2)));

            var verified = await winner.GetAttemptAsync(attempt.AttemptGuid);
            Assert.Equal(LocalSquarePaymentAttemptStatus.PaymentVerified, verified!.Status);
            Assert.Equal("PAYMENT-WINNER", verified.PaymentId);
            Assert.Equal("COMPLETED", verified.PaymentStatus);

            await winner.MarkOrderCompletedAsync(attempt.AttemptGuid, attempt.UpdatedAt.AddMinutes(3));
            await Assert.ThrowsAsync<InvalidOperationException>(() => staleWorker.MarkCheckoutCreatedAsync(
                attempt.AttemptGuid,
                "CHECKOUT-STALE",
                "PENDING",
                attempt.UpdatedAt.AddMinutes(4)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => staleWorker.MarkPaymentVerifiedAsync(
                attempt.AttemptGuid,
                "PAYMENT-STALE",
                "COMPLETED",
                null,
                "stale verified write",
                attempt.UpdatedAt.AddMinutes(4)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => staleWorker.MarkFailedAsync(
                attempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Unknown,
                "FAILED",
                null,
                null,
                "stale failure",
                attempt.UpdatedAt.AddMinutes(4)));

            var completed = await winner.GetAttemptAsync(attempt.AttemptGuid);
            Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, completed!.Status);
            Assert.Equal("PAYMENT-WINNER", completed.PaymentId);
            Assert.Equal("COMPLETED", completed.PaymentStatus);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Repayment")]
    public async Task Square_installment_operation_kinds_persist_guarded_automatic_transitions(string operationKind)
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var verifiedAttempt = CreateSquareAttempt(
                attemptGuid: Guid.NewGuid(),
                operationKind: operationKind);
            var failedAttempt = CreateSquareAttempt(
                attemptGuid: Guid.NewGuid(),
                operationKind: operationKind);

            await schema.InitializeAsync();
            await repository.CreateAsync(verifiedAttempt);
            await repository.CreateAsync(failedAttempt);

            await repository.MarkCheckoutCreatedAsync(
                verifiedAttempt.AttemptGuid,
                $"CHECKOUT-{operationKind}",
                "PENDING",
                verifiedAttempt.UpdatedAt.AddMinutes(1));
            await repository.MarkPaymentVerifiedAsync(
                verifiedAttempt.AttemptGuid,
                $"PAYMENT-{operationKind}",
                "COMPLETED",
                responseCode: null,
                responseText: "installment payment verified",
                verifiedAttempt.UpdatedAt.AddMinutes(2));
            await repository.MarkFailedAsync(
                failedAttempt.AttemptGuid,
                LocalSquarePaymentAttemptStatus.Failed,
                checkoutStatus: "FAILED",
                paymentStatus: null,
                responseCode: "DECLINED",
                responseText: "installment payment declined",
                failedAttempt.UpdatedAt.AddMinutes(1));

            var verified = await repository.GetAttemptAsync(verifiedAttempt.AttemptGuid);
            Assert.Equal(operationKind, verified!.OperationKind);
            Assert.Equal(LocalSquarePaymentAttemptStatus.PaymentVerified, verified.Status);
            Assert.Equal($"CHECKOUT-{operationKind}", verified.CheckoutId);
            Assert.Equal($"PAYMENT-{operationKind}", verified.PaymentId);
            Assert.Equal("COMPLETED", verified.PaymentStatus);

            var failed = await repository.GetAttemptAsync(failedAttempt.AttemptGuid);
            Assert.Equal(operationKind, failed!.OperationKind);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Failed, failed.Status);
            Assert.Equal("DECLINED", failed.ResponseCode);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Theory]
    [InlineData("Create", LocalSquarePaymentAttemptStatus.PaymentVerified)]
    [InlineData("Create", LocalSquarePaymentAttemptStatus.OrderCompleted)]
    [InlineData("Repayment", LocalSquarePaymentAttemptStatus.PaymentVerified)]
    [InlineData("Repayment", LocalSquarePaymentAttemptStatus.OrderCompleted)]
    public async Task Square_installment_operation_kinds_keep_verified_and_completed_stale_worker_guard(
        string operationKind,
        LocalSquarePaymentAttemptStatus status)
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var attempt = CreateSquareAttempt(
                attemptGuid: Guid.NewGuid(),
                status: status,
                operationKind: operationKind);

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);

            await AssertSquareAutomaticWritesRejectedAsync(repository, attempt);

            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);
            Assert.Equal(status, saved!.Status);
            Assert.Equal(operationKind, saved.OperationKind);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Theory]
    [InlineData("Create", ActiveSessionSupervisorResolutionCodes.ConfirmedPaid)]
    [InlineData("Create", ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid)]
    [InlineData("Repayment", ActiveSessionSupervisorResolutionCodes.ConfirmedPaid)]
    [InlineData("Repayment", ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid)]
    public async Task Square_installment_operation_kinds_keep_supervisor_terminal_guard(
        string operationKind,
        string supervisorResolutionCode)
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var attempt = CreateSquareAttempt(
                attemptGuid: Guid.NewGuid(),
                operationKind: operationKind) with
            {
                ResponseCode = supervisorResolutionCode,
                ResponseText = "supervisor terminal decision"
            };

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);

            await AssertSquareAutomaticWritesRejectedAsync(repository, attempt);

            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Pending, saved!.Status);
            Assert.Equal(supervisorResolutionCode, saved.ResponseCode);
            Assert.Equal("supervisor terminal decision", saved.ResponseText);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_unknown_operation_kind_rejects_all_guarded_automatic_writes()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var attempt = CreateSquareAttempt(
                attemptGuid: Guid.NewGuid(),
                operationKind: "UnexpectedInstallmentKind");

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);

            await AssertSquareAutomaticWritesRejectedAsync(repository, attempt);

            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Pending, saved!.Status);
            Assert.Null(saved.CheckoutId);
            Assert.Null(saved.PaymentId);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_refund_submission_writes_cannot_downgrade_payment_verified_attempt()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            const string submissionToken = "refund-worker-token";
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var attempt = CreateSquareAttempt(
                attemptGuid: Guid.Parse("50000000-0000-0000-0000-000000000005"),
                status: LocalSquarePaymentAttemptStatus.Pending,
                operationKind: "Refund");

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);
            Assert.True(await repository.TryBeginRefundSubmissionAsync(
                attempt.AttemptGuid,
                attempt.UpdatedAt,
                submissionToken,
                attempt.UpdatedAt.AddMinutes(1)));
            Assert.True(await repository.TryMarkRefundPaymentVerifiedAsync(
                attempt.AttemptGuid,
                submissionToken,
                "REFUND-WINNER",
                "COMPLETED",
                null,
                "verified by winner",
                attempt.UpdatedAt.AddMinutes(2)));

            Assert.False(await repository.TryMarkRefundCheckoutCreatedAsync(
                attempt.AttemptGuid,
                submissionToken,
                "REFUND-CHECKOUT-STALE",
                "PENDING",
                attempt.UpdatedAt.AddMinutes(3)));
            Assert.False(await repository.TryMarkRefundFailedAsync(
                attempt.AttemptGuid,
                submissionToken,
                LocalSquarePaymentAttemptStatus.Unknown,
                "FAILED",
                null,
                null,
                "stale refund failure",
                attempt.UpdatedAt.AddMinutes(3)));

            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);
            Assert.Equal(LocalSquarePaymentAttemptStatus.PaymentVerified, saved!.Status);
            Assert.Equal("REFUND-WINNER", saved.PaymentId);
            Assert.Equal("COMPLETED", saved.PaymentStatus);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_TryTerminalizeNotPaid_rejects_already_terminal_attempt()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var attempt = CreateSquareAttempt(
                attemptGuid: Guid.Parse("50000000-0000-0000-0000-000000000001"),
                status: LocalSquarePaymentAttemptStatus.Pending,
                operationKind: "Sale") with
            {
                ResponseCode = ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid
            };

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);

            var resolvedAt = DateTimeOffset.Parse("2026-07-28T10:00:00+10:00");
            var first = await repository.TryTerminalizeNotPaidAsync(
                attempt.AttemptGuid,
                attempt.Status,
                attempt.UpdatedAt,
                resolvedAt);
            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);
            Assert.True(first);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, saved!.Status);

            var second = await repository.TryTerminalizeNotPaidAsync(
                attempt.AttemptGuid,
                saved.Status,
                saved.UpdatedAt,
                resolvedAt.AddMinutes(1));
            Assert.False(second);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static LocalCardPaymentAttempt CreateAttempt(
        Guid? attemptGuid = null,
        string storeCode = "S001",
        string deviceCode = "POS-01",
        string cashierId = "C001",
        string environment = "sandbox",
        LocalCardPaymentAttemptStatus status = LocalCardPaymentAttemptStatus.Pending,
        DateTimeOffset? updatedAt = null,
        string orderDraftJson = "{}",
        string? sessionId = null,
        string? txnRef = null,
        DateTimeOffset? acknowledgedAt = null,
        string operationKind = "Sale")
    {
        var effectiveUpdatedAt = updatedAt ?? DateTimeOffset.Parse("2026-06-05T10:00:00+10:00");

        return new LocalCardPaymentAttempt(
            attemptGuid ?? Guid.Parse("99999999-8888-7777-6666-555555555555"),
            sessionId,
            txnRef,
            "Linkly",
            environment,
            "Cloud",
            "Purchase",
            12.34m,
            status,
            orderDraftJson,
            storeCode,
            deviceCode,
            cashierId,
            null,
            null,
            null,
            effectiveUpdatedAt.AddMinutes(-1),
            effectiveUpdatedAt,
            status == LocalCardPaymentAttemptStatus.OrderCompleted ? effectiveUpdatedAt : null,
            acknowledgedAt,
            operationKind);
    }

    private static LocalSquarePaymentAttempt CreateSquareAttempt(
        Guid? attemptGuid = null,
        string storeCode = "S001",
        string deviceCode = "POS-01",
        string cashierId = "C001",
        string environment = "Production",
        LocalSquarePaymentAttemptStatus status = LocalSquarePaymentAttemptStatus.Pending,
        DateTimeOffset? updatedAt = null,
        string orderDraftJson = "{}",
        string operationKind = "Sale")
    {
        var effectiveUpdatedAt = updatedAt ?? DateTimeOffset.Parse("2026-06-05T10:00:00+10:00");

        return new LocalSquarePaymentAttempt(
            attemptGuid ?? Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            null,
            "idem-1",
            "DEV-1",
            "LOC-1",
            environment,
            12.34m,
            1234,
            "AUD",
            status,
            null,
            null,
            orderDraftJson,
            storeCode,
            deviceCode,
            cashierId,
            null,
            null,
            null,
            null,
            effectiveUpdatedAt.AddMinutes(-1),
            effectiveUpdatedAt,
            null,
            null,
            null,
            operationKind);
    }

    private static async Task AssertSquareAutomaticWritesRejectedAsync(
        LocalSquarePaymentAttemptRepository repository,
        LocalSquarePaymentAttempt attempt)
    {
        var attemptedAt = attempt.UpdatedAt.AddMinutes(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.MarkCheckoutCreatedAsync(
            attempt.AttemptGuid,
            "CHECKOUT-STALE",
            "PENDING",
            attemptedAt));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.MarkPaymentVerifiedAsync(
            attempt.AttemptGuid,
            "PAYMENT-STALE",
            "COMPLETED",
            responseCode: null,
            responseText: "stale verified write",
            attemptedAt));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.MarkFailedAsync(
            attempt.AttemptGuid,
            LocalSquarePaymentAttemptStatus.Failed,
            checkoutStatus: "FAILED",
            paymentStatus: null,
            responseCode: null,
            responseText: "stale failed write",
            attemptedAt));
    }

    private static LocalFinancialSupervisorResolution CreateSquareSaleJournal(
        Guid attemptGuid,
        string decision,
        string reason)
    {
        return new LocalFinancialSupervisorResolution(
            Guid.NewGuid(),
            LocalFinancialSupervisorResolutionTarget.ActiveSession,
            "Square",
            "Production",
            "S001",
            "POS-01",
            attemptGuid,
            null,
            null,
            "CHECKOUT-1",
            decision,
            "C001",
            null,
            "Alice",
            reason,
            null,
            null,
            null,
            DateTimeOffset.Parse("2026-07-28T10:00:00+10:00"),
            Guid.NewGuid(),
            "{}");
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-card-attempt-repo-{Guid.NewGuid():N}.db");
    }

    private static void DeleteTempDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<int> ReadScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }
}
