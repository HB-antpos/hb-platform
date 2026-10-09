using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

// M14：已批准（有扣款证据）却无法自动建单的记录，主管“确认已退款/另行处理”的受控出口在真实 SQLite 上的 CAS 行为。
public sealed class LocalCardPaymentApprovedEvidenceExitTests
{
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-07-28T01:00:00+00:00");

    [Theory]
    [InlineData(LocalCardPaymentAttemptStatus.Approved)]
    [InlineData(LocalCardPaymentAttemptStatus.RequiresReview)]
    public async Task Exit_closes_approved_evidence_record_only_when_explicitly_allowed(
        LocalCardPaymentAttemptStatus status)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalCardPaymentAttemptRepository(store);
            var attempt = CreateAttempt(
                Guid.Parse("22000000-0000-0000-0000-000000000001"),
                "SESSION-EXIT-001",
                "Sale",
                status);
            await repository.CreateAsync(attempt);
            var resolution = CreateResolution(attempt, ActiveSessionSupervisorDecision.ConfirmNotPaid);

            // 默认仍保持“已有批准证据不能结案”：证据排除条件没有被放宽。
            Assert.False(await repository.ResolvePaymentWithJournalAsync(
                resolution,
                CreateJournal(attempt, BaseTime.AddMinutes(1))));
            var untouched = Assert.IsType<LocalCardPaymentAttempt>(
                await repository.GetAttemptAsync(attempt.AttemptGuid));
            Assert.Equal(status, untouched.Status);
            Assert.Equal("00", untouched.ResponseCode);

            Assert.True(await repository.ResolvePaymentWithJournalAsync(
                new ActiveSessionResolution(
                    resolution.AttemptGuid,
                    resolution.SessionId,
                    resolution.Decision,
                    resolution.ExpectedStatus,
                    resolution.ExpectedUpdatedAt,
                    resolution.Reason,
                    resolution.Evidence,
                    resolution.PaymentReference,
                    resolution.ResolvedAt)
                {
                    AllowApprovedEvidence = true
                },
                CreateJournal(attempt, BaseTime.AddMinutes(1))));

            var closed = Assert.IsType<LocalCardPaymentAttempt>(
                await repository.GetAttemptAsync(attempt.AttemptGuid));
            Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, closed.Status);
            Assert.Equal(ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid, closed.ResponseCode);
            Assert.Equal(CardRecoveryPhases.FinalizePending, closed.RecoveryPhase);
            Assert.Equal(LocalCardPaymentAttemptStatus.Abandoned.ToString(), closed.RecoveryTargetStatus);
            // 银行参考号保留作为审计证据，不会被结案清掉。
            Assert.Equal("ANZ:TXN-EXIT", closed.PaymentReference);
            await using var connection = await store.OpenConnectionAsync();
            Assert.Equal(1L, await ReadLongAsync(
                connection,
                $"SELECT COUNT(*) FROM LocalFinancialSupervisorResolutions WHERE AttemptGuid='{attempt.AttemptGuid:D}';"));
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Exit_never_confirms_paid_and_cannot_be_applied_twice()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalCardPaymentAttemptRepository(store);
            var attempt = CreateAttempt(
                Guid.Parse("22100000-0000-0000-0000-000000000001"),
                "SESSION-EXIT-002",
                "Sale",
                LocalCardPaymentAttemptStatus.Approved);
            await repository.CreateAsync(attempt);
            var paid = new ActiveSessionResolution(
                attempt.AttemptGuid,
                "SESSION-EXIT-002",
                ActiveSessionSupervisorDecision.ConfirmPaid,
                attempt.Status,
                attempt.UpdatedAt,
                "paid",
                "bank evidence",
                "REF-PAID",
                BaseTime.AddMinutes(1))
            {
                AllowApprovedEvidence = true
            };

            // 放宽证据排除的出口只服务于“确认已退款/另行处理”，不能用来确认已付款。
            await Assert.ThrowsAsync<ArgumentException>(() => repository.ResolvePaymentWithJournalAsync(
                paid,
                CreateJournal(attempt, BaseTime.AddMinutes(1))));

            var notPaid = new ActiveSessionResolution(
                attempt.AttemptGuid,
                "SESSION-EXIT-002",
                ActiveSessionSupervisorDecision.ConfirmNotPaid,
                attempt.Status,
                attempt.UpdatedAt,
                string.Empty,
                "refund ref RF-2",
                PaymentReference: null,
                BaseTime.AddMinutes(1))
            {
                AllowApprovedEvidence = true
            };
            Assert.True(await repository.ResolvePaymentWithJournalAsync(
                notPaid,
                CreateJournal(attempt, BaseTime.AddMinutes(1))));
            // 第二个主管再点一次：记录已被结案，CAS 失败而不是重复结案。
            Assert.False(await repository.ResolvePaymentWithJournalAsync(
                notPaid,
                CreateJournal(attempt, BaseTime.AddMinutes(2))));
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Not_paid_finalization_closes_an_already_acknowledged_active_session_once()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalCardPaymentAttemptRepository(store);
            var acknowledgedAt = BaseTime.AddMinutes(-5);
            var attempt = CreateAttempt(
                Guid.Parse("22200000-0000-0000-0000-000000000001"),
                "SESSION-EXIT-003",
                "ActiveSession",
                LocalCardPaymentAttemptStatus.Approved) with
            {
                AcknowledgedAt = acknowledgedAt
            };
            await repository.CreateOrGetActiveSessionAsync(attempt);
            // 接管时已 ack 的孤儿批准仍然是未结记录（Approved），直到主管关闭它。
            Assert.Single(await repository.GetOpenAttemptsAsync("S001", "POS-01", "Production"));
            var resolution = new ActiveSessionResolution(
                attempt.AttemptGuid,
                "SESSION-EXIT-003",
                ActiveSessionSupervisorDecision.ConfirmNotPaid,
                attempt.Status,
                attempt.UpdatedAt,
                string.Empty,
                "refund ref RF-3",
                PaymentReference: null,
                BaseTime.AddMinutes(1))
            {
                AllowApprovedEvidence = true
            };
            Assert.True(await repository.ResolvePaymentWithJournalAsync(
                resolution,
                CreateJournal(attempt, BaseTime.AddMinutes(1))));
            var resolved = Assert.IsType<LocalCardPaymentAttempt>(
                await repository.GetAttemptAsync(attempt.AttemptGuid));

            var finalizedAt = BaseTime.AddMinutes(2);
            Assert.True(await repository.TryFinalizeSupervisorNotPaidAndAcknowledgeAsync(
                resolved.AttemptGuid,
                resolved.Status,
                resolved.UpdatedAt,
                finalizedAt));

            var closed = Assert.IsType<LocalCardPaymentAttempt>(
                await repository.GetAttemptAsync(attempt.AttemptGuid));
            Assert.Equal(LocalCardPaymentAttemptStatus.Abandoned, closed.Status);
            Assert.Equal(CardRecoveryPhases.None, closed.RecoveryPhase);
            // 原 ack 时间保留，不因出口结案被改写。
            Assert.Equal(acknowledgedAt, closed.AcknowledgedAt);
            Assert.Empty(await repository.GetOpenAttemptsAsync("S001", "POS-01", "Production"));
            Assert.False(await repository.TryFinalizeSupervisorNotPaidAndAcknowledgeAsync(
                closed.AttemptGuid,
                resolved.Status,
                resolved.UpdatedAt,
                finalizedAt.AddMinutes(1)));
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    private static ActiveSessionResolution CreateResolution(
        LocalCardPaymentAttempt attempt,
        ActiveSessionSupervisorDecision decision) =>
        new(
            attempt.AttemptGuid,
            attempt.SessionId!,
            decision,
            attempt.Status,
            attempt.UpdatedAt,
            "refunded",
            "refund ref RF-1",
            PaymentReference: null,
            BaseTime.AddMinutes(1));

    private static LocalCardPaymentAttempt CreateAttempt(
        Guid attemptGuid,
        string sessionId,
        string operationKind,
        LocalCardPaymentAttemptStatus status) =>
        new(
            attemptGuid,
            sessionId,
            TxnRef: "TXN-EXIT",
            Processor: "Linkly",
            Environment: "Production",
            ConnectionMode: "CloudBackendAsync",
            TxnType: "P",
            Amount: 10m,
            Status: status,
            OrderDraftJson: "{}",
            StoreCode: "S001",
            DeviceCode: "POS-01",
            CashierId: "C001",
            ResponseCode: "00",
            ResponseText: "APPROVED",
            PaymentReference: "ANZ:TXN-EXIT",
            CreatedAt: BaseTime.AddMinutes(-1),
            UpdatedAt: BaseTime,
            CompletedAt: null,
            AcknowledgedAt: null,
            OperationKind: operationKind);

    private static LocalFinancialSupervisorResolution CreateJournal(
        LocalCardPaymentAttempt attempt,
        DateTimeOffset resolvedAt)
    {
        var auditEventId = Guid.NewGuid();
        return new LocalFinancialSupervisorResolution(
            Guid.NewGuid(),
            LocalFinancialSupervisorResolutionTarget.ActiveSession,
            "Linkly",
            attempt.Environment,
            attempt.StoreCode,
            attempt.DeviceCode,
            attempt.AttemptGuid,
            RefundStepGuid: null,
            OperationGuid: null,
            attempt.SessionId,
            ActiveSessionSupervisorDecision.ConfirmNotPaid.ToString(),
            "manager-exit",
            OperatorUserGuid: null,
            OperatorName: "manager-exit",
            Reason: string.Empty,
            Evidence: "refund ref RF-1",
            FinancialReference: null,
            RetryReference: null,
            resolvedAt,
            auditEventId,
            $$"""{"eventId":"{{auditEventId:D}}"}""");
    }

    private static async Task<long> ReadLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string CreateTempDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"hbpos-approved-exit-{Guid.NewGuid():N}.db");

    private static void DeleteTempDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var candidate in new[] { path, $"{path}-wal", $"{path}-shm" })
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }
}
