using System.Reflection;
using System.Runtime.CompilerServices;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using SqlSugar;

namespace Hbpos.Api.Tests;

public sealed class InstallmentRepaymentClaimCommitRepositoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-04T01:00:00Z");

    [Fact]
    public async Task Commit_rolls_back_payment_order_and_claim_together_then_retries_idempotently()
    {
        await using var fixture = new CommitSqliteFixture();
        var claim = await fixture.SeedProviderPendingClaimAsync(PaymentMethodKind.Cash);
        var installmentRepository = new SqlSugarInstallmentRepository(fixture.DbContext);
        var failingRepository = new SqlSugarInstallmentRepaymentClaimCommitRepository(
            fixture.DbContext,
            installmentRepository,
            new ThrowAfterPaymentInsert());

        await Assert.ThrowsAsync<InjectedCommitFailure>(() =>
            failingRepository.CommitAsync(
                claim,
                new InstallmentRepaymentClaimCommitRequest(),
                RecoveryIdentity(),
                Now,
                CancellationToken.None));

        var afterFailure = await fixture.ReadStateAsync(claim);
        Assert.Equal(0, afterFailure.RepaymentCount);
        Assert.Equal(20m, afterFailure.Order.PaidAmount);
        Assert.Equal(80m, afterFailure.Order.BalanceAmount);
        Assert.Equal((int)InstallmentStatus.Active, afterFailure.Order.Status);
        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending.ToString(), afterFailure.Claim.Status);
        Assert.True(afterFailure.Claim.IsBlocking);
        Assert.Null(afterFailure.Claim.CommitResponseJson);

        var repository = new SqlSugarInstallmentRepaymentClaimCommitRepository(
            fixture.DbContext,
            installmentRepository,
            new NoOpInstallmentRepaymentClaimCommitFaultInjector());
        var committed = await repository.CommitAsync(
            claim,
            new InstallmentRepaymentClaimCommitRequest(),
            RecoveryIdentity(),
            Now,
            CancellationToken.None);
        var replay = await repository.CommitAsync(
            claim,
            new InstallmentRepaymentClaimCommitRequest(),
            RecoveryIdentity(),
            Now.AddSeconds(1),
            CancellationToken.None);

        var final = await fixture.ReadStateAsync(claim);
        Assert.False(committed.AlreadyRecorded);
        Assert.True(replay.AlreadyRecorded);
        Assert.Equal(1, final.RepaymentCount);
        Assert.Equal(30m, final.Order.PaidAmount);
        Assert.Equal(70m, final.Order.BalanceAmount);
        Assert.Equal(10m, final.Repayment.Amount);
        Assert.Equal(InstallmentRepaymentClaimStatus.Committed.ToString(), final.Claim.Status);
        Assert.False(final.Claim.IsBlocking);
        Assert.False(string.IsNullOrWhiteSpace(final.Claim.CommitResponseJson));
        Assert.Equal("USER-RECOVERY", final.Claim.LastRecoveryCashierUserGuid);
        Assert.Equal(Now, new DateTimeOffset(final.Claim.RecoveredAtUtc!.Value, TimeSpan.Zero));
        Assert.Equal("POS-02", final.Repayment.DeviceCode);
        Assert.Equal("C01", final.Repayment.CashierId);
        Assert.Equal("Claim Cashier", final.Repayment.CashierName);
    }

    [Fact]
    public async Task Card_commit_records_the_client_evidence_atomically_and_replays_idempotently()
    {
        await using var fixture = new CommitSqliteFixture();
        var claim = await fixture.SeedProviderPendingClaimAsync(PaymentMethodKind.Card, amount: 10m);
        var repository = new SqlSugarInstallmentRepaymentClaimCommitRepository(
            fixture.DbContext,
            new SqlSugarInstallmentRepository(fixture.DbContext),
            new NoOpInstallmentRepaymentClaimCommitFaultInjector());

        var first = await repository.CommitAsync(
            claim,
            CardCommitRequest(claim.Amount),
            RecoveryIdentity(),
            Now,
            CancellationToken.None);
        var replay = await repository.CommitAsync(
            claim,
            CardCommitRequest(claim.Amount),
            RecoveryIdentity(),
            Now,
            CancellationToken.None);

        Assert.False(first.AlreadyRecorded);
        Assert.True(replay.AlreadyRecorded);
        var state = await fixture.ReadStateAsync(claim);
        Assert.Equal(1, state.RepaymentCount);
        Assert.Equal((int)PaymentMethodKind.Card, state.Repayment.Method);
        Assert.Equal(10m, state.Repayment.Amount);
        Assert.Equal("APPROVED", state.Repayment.Reference);
        Assert.Contains("\"txnRef\":\"TXN-ATOMIC\"", state.Repayment.CardTransactionsJson);
        Assert.Equal(30m, state.Order.PaidAmount);
        Assert.Equal(70m, state.Order.BalanceAmount);
        Assert.Equal(InstallmentRepaymentClaimStatus.Committed.ToString(), state.Claim.Status);
        Assert.False(state.Claim.IsBlocking);
    }

    [Fact]
    public async Task Card_commit_with_mismatched_evidence_amount_fails_closed_and_leaves_all_ledgers_unchanged()
    {
        await using var fixture = new CommitSqliteFixture();
        var claim = await fixture.SeedProviderPendingClaimAsync(PaymentMethodKind.Card, amount: 10m);
        var repository = new SqlSugarInstallmentRepaymentClaimCommitRepository(
            fixture.DbContext,
            new SqlSugarInstallmentRepository(fixture.DbContext),
            new NoOpInstallmentRepaymentClaimCommitFaultInjector());
        var before = await fixture.ReadStateAsync(claim);

        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            repository.CommitAsync(
                claim,
                CardCommitRequest(claim.Amount + 1m),
                RecoveryIdentity(),
                Now,
                CancellationToken.None));

        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Invalid, exception.Code);
        var after = await fixture.ReadStateAsync(claim);
        Assert.Equal(before.RepaymentCount, after.RepaymentCount);
        Assert.Equal(before.Order.PaidAmount, after.Order.PaidAmount);
        Assert.Equal(before.Order.BalanceAmount, after.Order.BalanceAmount);
        Assert.Equal(before.Order.Status, after.Order.Status);
        Assert.Equal(before.Claim.Status, after.Claim.Status);
        Assert.Equal(before.Claim.IsBlocking, after.Claim.IsBlocking);
        Assert.Equal(before.Claim.Revision, after.Claim.Revision);
        Assert.Null(after.Claim.CommitResponseJson);
    }

    [Fact]
    public async Task Cash_commit_and_replay_preserve_the_exact_claim_amount()
    {
        await using var fixture = new CommitSqliteFixture();
        var claim = await fixture.SeedProviderPendingClaimAsync(PaymentMethodKind.Cash, amount: 10m);
        var installmentRepository = new SqlSugarInstallmentRepository(fixture.DbContext);
        var repository = new SqlSugarInstallmentRepaymentClaimCommitRepository(
            fixture.DbContext,
            installmentRepository,
            new NoOpInstallmentRepaymentClaimCommitFaultInjector());

        var committed = await repository.CommitAsync(
            claim,
            new InstallmentRepaymentClaimCommitRequest(),
            RecoveryIdentity(),
            Now,
            CancellationToken.None);
        var replay = await repository.CommitAsync(
            claim,
            new InstallmentRepaymentClaimCommitRequest(),
            RecoveryIdentity(),
            Now.AddSeconds(1),
            CancellationToken.None);

        var state = await fixture.ReadStateAsync(claim);
        Assert.False(committed.AlreadyRecorded);
        Assert.True(replay.AlreadyRecorded);
        Assert.Equal(1, state.RepaymentCount);
        Assert.Equal(10m, state.Repayment.Amount);
        Assert.Equal(70m, state.Order.BalanceAmount);
    }

    [Fact]
    public async Task Committed_replay_returns_the_persisted_response_after_order_state_changes()
    {
        await using var fixture = new CommitSqliteFixture();
        var claim = await fixture.SeedProviderPendingClaimAsync(PaymentMethodKind.Cash, amount: 10m);
        var repository = new SqlSugarInstallmentRepaymentClaimCommitRepository(
            fixture.DbContext,
            new SqlSugarInstallmentRepository(fixture.DbContext),
            new NoOpInstallmentRepaymentClaimCommitFaultInjector());

        var committed = await repository.CommitAsync(
            claim,
            new InstallmentRepaymentClaimCommitRequest(),
            RecoveryIdentity(),
            Now,
            CancellationToken.None);
        await fixture.ChangeOrderAfterCommitAsync(claim.InstallmentGuid);
        var replay = await repository.CommitAsync(
            claim,
            new InstallmentRepaymentClaimCommitRequest(),
            RecoveryIdentity(),
            Now.AddMinutes(10),
            CancellationToken.None);

        Assert.Equal(committed.CommitResponse.PaidAmount, replay.CommitResponse.PaidAmount);
        Assert.Equal(committed.CommitResponse.BalanceAmount, replay.CommitResponse.BalanceAmount);
        Assert.Equal(committed.CommitResponse.Status, replay.CommitResponse.Status);
        Assert.Equal(committed.CommitResponse.Details.Status, replay.CommitResponse.Details.Status);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(committed.CommitResponse),
            System.Text.Json.JsonSerializer.Serialize(replay.CommitResponse));
        var state = await fixture.ReadStateAsync(claim);
        Assert.False(string.IsNullOrWhiteSpace(state.Claim.CommitResponseJson));
    }

    private static InstallmentRepaymentClaimIdentity RecoveryIdentity() => new(
        "S01",
        "POS-02",
        "C01",
        "Recovery Cashier",
        [],
        "USER-RECOVERY");

    private static InstallmentRepaymentClaimCommitRequest CardCommitRequest(decimal amount) => new(
        Reference: "APPROVED",
        CardTransactions:
        [
            new CardTransactionDto(
                "ANZ",
                "TXN-ATOMIC",
                "AUTH",
                "VISA",
                412345,
                "****1234",
                "MERCHANT",
                "00",
                "APPROVED",
                "123456",
                Now,
                amount,
                "approved")
        ]);

    private sealed class ThrowAfterPaymentInsert : IInstallmentRepaymentClaimCommitFaultInjector
    {
        public Task AfterPaymentInsertedAsync(CancellationToken cancellationToken) =>
            throw new InjectedCommitFailure();
    }

    private sealed class InjectedCommitFailure : Exception;

    private sealed class CommitSqliteFixture : IAsyncDisposable
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-installment-claim-commit-{Guid.NewGuid():N}.db");
        private readonly SqlSugarClient client;

        public CommitSqliteFixture()
        {
            client = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databasePath}",
                DbType = DbType.Sqlite,
                InitKeyType = InitKeyType.Attribute,
                IsAutoCloseConnection = true
            });
            client.CodeFirst.InitTables<
                InstallmentOrderEntity,
                InstallmentOrderLineEntity,
                InstallmentPaymentEntity,
                InstallmentRepaymentClaimEntity>();
            DbContext = CreateDbContext(client);
        }

        public HbposSqlSugarContext DbContext { get; }

        public async Task<InstallmentRepaymentClaimRecord> SeedProviderPendingClaimAsync(
            PaymentMethodKind method = PaymentMethodKind.Card,
            decimal amount = 10m)
        {
            var installmentGuid = Guid.NewGuid();
            var operationGuid = Guid.NewGuid();
            var paymentGuid = Guid.NewGuid();
            await client.Insertable(new InstallmentOrderEntity
            {
                InstallmentGuid = installmentGuid.ToString("D"),
                InstallmentNumber = "INS-ATOMIC-001",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                CashierId = "ORIGINAL",
                CashierName = "Original Cashier",
                CustomerName = "Atomic Customer",
                CustomerPhone = "0400000000",
                TotalAmount = 100m,
                MinimumDownPayment = 20m,
                DownPaymentAmount = 20m,
                PaidAmount = 20m,
                BalanceAmount = 80m,
                Status = (int)InstallmentStatus.Active,
                CreatedAt = Now.AddDays(-1).UtcDateTime,
                UpdatedAt = Now.AddDays(-1).UtcDateTime
            }).ExecuteCommandAsync();
            await client.Insertable(new InstallmentPaymentEntity
            {
                PaymentGuid = Guid.NewGuid().ToString("D"),
                InstallmentGuid = installmentGuid.ToString("D"),
                Method = (int)PaymentMethodKind.Cash,
                Amount = 20m,
                Status = (int)InstallmentPaymentStatus.Recorded,
                RecordedAt = Now.AddDays(-1).UtcDateTime,
                CashierId = "ORIGINAL",
                CashierName = "Original Cashier",
                DeviceCode = "POS-01",
                IdempotencyKey = "down-payment"
            }).ExecuteCommandAsync();
            var claim = new InstallmentRepaymentClaimEntity
            {
                OperationGuid = operationGuid,
                InstallmentGuid = installmentGuid,
                PaymentGuid = paymentGuid,
                StoreCode = "S01",
                ClaimantDeviceCode = "POS-02",
                CashierId = "C01",
                CashierName = "Claim Cashier",
                Amount = amount,
                Method = (int)method,
                IdempotencyKey = "claim-payment",
                Fingerprint = new string('a', 64),
                Status = InstallmentRepaymentClaimStatus.ProviderPending.ToString(),
                IsBlocking = true,
                Provider = method switch
                {
                    PaymentMethodKind.Cash => "cash",
                    PaymentMethodKind.Voucher => "voucher",
                    _ => "linkly"
                },
                ProviderAttemptId = "attempt-atomic",
                CreatedAtUtc = Now.AddMinutes(-1).UtcDateTime,
                UpdatedAtUtc = Now.AddMinutes(-1).UtcDateTime,
                Revision = 2
            };
            await client.Insertable(claim).ExecuteCommandAsync();
            return new InstallmentRepaymentClaimRecord(
                installmentGuid,
                operationGuid,
                paymentGuid,
                claim.StoreCode,
                claim.ClaimantDeviceCode,
                claim.CashierId,
                claim.CashierName,
                claim.Amount,
                (PaymentMethodKind)claim.Method,
                claim.IdempotencyKey,
                claim.Fingerprint,
                InstallmentRepaymentClaimStatus.ProviderPending,
                claim.Provider,
                claim.ProviderAttemptId,
                new DateTimeOffset(claim.CreatedAtUtc, TimeSpan.Zero),
                new DateTimeOffset(claim.UpdatedAtUtc, TimeSpan.Zero),
                null,
                null,
                claim.Revision);
        }

        public async Task<CommitState> ReadStateAsync(InstallmentRepaymentClaimRecord claim)
        {
            var installmentGuid = claim.InstallmentGuid.ToString("D");
            var paymentGuid = claim.PaymentGuid.ToString("D");
            var order = await client.Queryable<InstallmentOrderEntity>()
                .FirstAsync(entity => entity.InstallmentGuid == installmentGuid);
            var claimEntity = await client.Queryable<InstallmentRepaymentClaimEntity>()
                .FirstAsync(entity => entity.OperationGuid == claim.OperationGuid);
            var repayments = await client.Queryable<InstallmentPaymentEntity>()
                .Where(entity => entity.PaymentGuid == paymentGuid)
                .ToListAsync();
            return new CommitState(order, claimEntity, repayments.SingleOrDefault() ?? new InstallmentPaymentEntity(), repayments.Count);
        }

        public Task ChangeOrderAfterCommitAsync(Guid installmentGuid)
        {
            var key = installmentGuid.ToString("D");
            return client.Updateable<InstallmentOrderEntity>()
                .SetColumns(entity => entity.Status == (int)InstallmentStatus.PickedUp)
                .SetColumns(entity => entity.UpdatedAt == Now.AddMinutes(5).UtcDateTime)
                .Where(entity => entity.InstallmentGuid == key)
                .ExecuteCommandAsync();
        }

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            if (File.Exists(databasePath))
            {
                try
                {
                    File.Delete(databasePath);
                }
                catch (IOException)
                {
                    // SQLite 可能短暂占用临时数据库，不影响测试断言。
                }
            }

            return ValueTask.CompletedTask;
        }

        private static HbposSqlSugarContext CreateDbContext(ISqlSugarClient posmDb)
        {
            var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HbposSqlSugarContext));
            SetAutoProperty(context, nameof(HbposSqlSugarContext.MainDb), posmDb);
            SetAutoProperty(context, nameof(HbposSqlSugarContext.PosmDb), posmDb);
            return context;
        }

        private static void SetAutoProperty(HbposSqlSugarContext context, string propertyName, ISqlSugarClient value)
        {
            var backingField = typeof(HbposSqlSugarContext).GetField(
                $"<{propertyName}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(backingField);
            backingField!.SetValue(context, value);
        }
    }

    private sealed record CommitState(
        InstallmentOrderEntity Order,
        InstallmentRepaymentClaimEntity Claim,
        InstallmentPaymentEntity Repayment,
        int RepaymentCount);
}
