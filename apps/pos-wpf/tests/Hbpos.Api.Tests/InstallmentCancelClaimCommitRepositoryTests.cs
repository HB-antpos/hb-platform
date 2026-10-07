using System.Reflection;
using System.Runtime.CompilerServices;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using BlazorApp.Shared.Models.POSM;
using SqlSugar;

namespace Hbpos.Api.Tests;

public sealed class InstallmentCancelClaimCommitRepositoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-04T03:00:00Z");

    [Fact]
    public async Task Commit_rolls_back_refunds_order_and_claim_together_then_replays_the_persisted_snapshot()
    {
        await using var fixture = new CommitFixture();
        var claim = await fixture.SeedRefundPendingClaimAsync();
        var request = new InstallmentCancelClaimCommitRequest(
        [
            new InstallmentRefundPaymentCommandDto(
                Guid.NewGuid(),
                PaymentMethodKind.Cash,
                20m,
                null,
                [],
                RefundIdempotencyKey(claim, fixture.SourcePaymentGuid),
                fixture.SourcePaymentGuid)
        ]);
        var installments = new SqlSugarInstallmentRepository(fixture.DbContext);
        var failing = new SqlSugarInstallmentCancelClaimCommitRepository(
            fixture.DbContext,
            installments,
            new ThrowAfterRefundInsert());

        await Assert.ThrowsAsync<InjectedCommitFailure>(() =>
            failing.CommitAsync(claim, request, RecoveryIdentity(), Now, CancellationToken.None));

        var afterFailure = await fixture.ReadStateAsync(claim);
        Assert.Equal(0, afterFailure.RefundCount);
        Assert.Equal((int)InstallmentStatus.Active, afterFailure.Order.Status);
        Assert.Equal(20m, afterFailure.Order.PaidAmount);
        Assert.Equal(30m, afterFailure.Order.BalanceAmount);
        Assert.Equal(InstallmentCancelClaimStatus.RefundPending.ToString(), afterFailure.Claim.Status);
        Assert.True(afterFailure.Claim.IsBlocking);
        Assert.Null(afterFailure.Claim.CommitResponseJson);

        var repository = new SqlSugarInstallmentCancelClaimCommitRepository(
            fixture.DbContext,
            installments,
            new NoOpInstallmentCancelClaimCommitFaultInjector());
        var committed = await repository.CommitAsync(
            claim,
            request,
            RecoveryIdentity(),
            Now,
            CancellationToken.None);
        await fixture.ChangeOrderAfterCommitAsync(claim.InstallmentGuid);
        var replay = await repository.CommitAsync(
            claim,
            request,
            RecoveryIdentity("C03", "Late Recovery", "U03"),
            Now.AddMinutes(10),
            CancellationToken.None);

        var final = await fixture.ReadStateAsync(claim);
        Assert.Equal(1, final.RefundCount);
        Assert.Equal(-20m, final.Refund.Amount);
        Assert.Equal(InstallmentCancelClaimStatus.Committed.ToString(), final.Claim.Status);
        Assert.False(final.Claim.IsBlocking);
        Assert.Equal("C03", final.Claim.LastRecoveryCashierId);
        Assert.Equal("U03", final.Claim.LastRecoveryCashierUserGuid);
        Assert.Equal(InstallmentStatus.Cancelled, committed.CommitResponse.Details.Status);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(committed.CommitResponse),
            System.Text.Json.JsonSerializer.Serialize(replay.CommitResponse));
    }

    [Fact]
    public async Task Paid_off_installment_not_picked_up_can_commit_cancellation_refund()
    {
        await using var fixture = new CommitFixture();
        var claim = await fixture.SeedRefundPendingClaimAsync(paidOff: true);
        var repository = new SqlSugarInstallmentCancelClaimCommitRepository(
            fixture.DbContext,
            new SqlSugarInstallmentRepository(fixture.DbContext),
            new NoOpInstallmentCancelClaimCommitFaultInjector());

        var result = await repository.CommitAsync(
            claim,
            CashRefundRequest(claim, fixture.SourcePaymentGuid),
            RecoveryIdentity(),
            Now,
            CancellationToken.None);

        var state = await fixture.ReadStateAsync(claim);
        Assert.Equal(InstallmentStatus.Cancelled, result.CommitResponse.Details.Status);
        Assert.Equal((int)InstallmentStatus.Cancelled, state.Order.Status);
        Assert.Equal(0m, state.Order.PaidAmount);
        Assert.Equal(-20m, state.Refund.Amount);
    }

    [Fact]
    public async Task Paid_off_installment_picked_up_during_refund_cannot_commit_cancellation()
    {
        await using var fixture = new CommitFixture();
        var claim = await fixture.SeedRefundPendingClaimAsync(paidOff: true);
        // 退款进行中另一入口完成提货：提交必须拒绝，不能出现"货已拿走又全额退款"。
        await fixture.ChangeOrderAfterCommitAsync(claim.InstallmentGuid);
        var repository = new SqlSugarInstallmentCancelClaimCommitRepository(
            fixture.DbContext,
            new SqlSugarInstallmentRepository(fixture.DbContext),
            new NoOpInstallmentCancelClaimCommitFaultInjector());

        await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            repository.CommitAsync(
                claim,
                CashRefundRequest(claim, fixture.SourcePaymentGuid),
                RecoveryIdentity(),
                Now,
                CancellationToken.None));

        var state = await fixture.ReadStateAsync(claim);
        Assert.Equal(0, state.RefundCount);
        Assert.Equal((int)InstallmentStatus.PickedUp, state.Order.Status);
    }

    [Fact]
    public async Task Voucher_mode_refunds_card_original_payment_with_server_issued_voucher()
    {
        await using var fixture = new CommitFixture();
        var claim = await fixture.SeedRefundPendingClaimAsync(
            PaymentMethodKind.Card,
            reference: "ANZ:ORIGINAL-TXN",
            paidOff: true,
            refundMode: InstallmentCancelRefundMode.Voucher);
        var idempotencyKey = RefundIdempotencyKey(claim, fixture.SourcePaymentGuid);
        var voucherCode = await fixture.SeedRefundVoucherAsync(idempotencyKey, 20m);
        var repository = new SqlSugarInstallmentCancelClaimCommitRepository(
            fixture.DbContext,
            new SqlSugarInstallmentRepository(fixture.DbContext),
            new NoOpInstallmentCancelClaimCommitFaultInjector());

        var result = await repository.CommitAsync(
            claim,
            new InstallmentCancelClaimCommitRequest(
            [
                new InstallmentRefundPaymentCommandDto(
                    Guid.NewGuid(),
                    PaymentMethodKind.Voucher,
                    20m,
                    $"VOUCHER_REFUND:{voucherCode}",
                    [],
                    idempotencyKey,
                    fixture.SourcePaymentGuid)
            ]),
            RecoveryIdentity(),
            Now,
            CancellationToken.None);

        var state = await fixture.ReadStateAsync(claim);
        Assert.Equal(InstallmentStatus.Cancelled, result.CommitResponse.Details.Status);
        Assert.Equal((int)PaymentMethodKind.Voucher, state.Refund.Method);
        Assert.Equal(-20m, state.Refund.Amount);
        Assert.Equal(InstallmentCancelRefundMode.Voucher, result.Claim.RefundMode);
    }

    [Fact]
    public async Task Voucher_mode_rejects_cash_refund_for_any_original_payment()
    {
        await using var fixture = new CommitFixture();
        var claim = await fixture.SeedRefundPendingClaimAsync(
            PaymentMethodKind.Cash,
            refundMode: InstallmentCancelRefundMode.Voucher);
        var repository = new SqlSugarInstallmentCancelClaimCommitRepository(
            fixture.DbContext,
            new SqlSugarInstallmentRepository(fixture.DbContext),
            new NoOpInstallmentCancelClaimCommitFaultInjector());

        // 选了退代金券就不能再退现金，哪怕原付款是现金。
        await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            repository.CommitAsync(
                claim,
                CashRefundRequest(claim, fixture.SourcePaymentGuid),
                RecoveryIdentity(),
                Now,
                CancellationToken.None));

        Assert.Equal(0, (await fixture.ReadStateAsync(claim)).RefundCount);
    }

    private static InstallmentCancelClaimCommitRequest CashRefundRequest(
        InstallmentCancelClaimRecord claim,
        Guid sourcePaymentGuid) =>
        new(
        [
            new InstallmentRefundPaymentCommandDto(
                Guid.NewGuid(),
                PaymentMethodKind.Cash,
                20m,
                null,
                [],
                RefundIdempotencyKey(claim, sourcePaymentGuid),
                sourcePaymentGuid)
        ]);

    [Fact]
    public async Task Card_commit_without_approved_provider_evidence_is_rejected_before_any_ledger_mutation()
    {
        await using var fixture = new CommitFixture();
        var claim = await fixture.SeedRefundPendingClaimAsync(
            PaymentMethodKind.Card,
            reference: "SQ:original-payment",
            cardTransactions:
            [
                CardTransaction("Square", "original-payment", 20m, responseText: "COMPLETED")
            ]);
        var request = new InstallmentCancelClaimCommitRequest(
        [
            new InstallmentRefundPaymentCommandDto(
                Guid.NewGuid(),
                PaymentMethodKind.Card,
                20m,
                "SQRF:forged-refund",
                [],
                RefundIdempotencyKey(claim, fixture.SourcePaymentGuid),
                fixture.SourcePaymentGuid)
        ]);
        var repository = new SqlSugarInstallmentCancelClaimCommitRepository(
            fixture.DbContext,
            new SqlSugarInstallmentRepository(fixture.DbContext),
            new NoOpInstallmentCancelClaimCommitFaultInjector());

        var error = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            repository.CommitAsync(claim, request, RecoveryIdentity(), Now, CancellationToken.None));

        Assert.Equal(InstallmentCancelClaimErrorCodes.RefundMethodUnsupported, error.Code);
        var state = await fixture.ReadStateAsync(claim);
        Assert.Equal(0, state.RefundCount);
        Assert.Equal((int)InstallmentStatus.Active, state.Order.Status);
        Assert.Equal(InstallmentCancelClaimStatus.RefundPending.ToString(), state.Claim.Status);
    }

    [Fact]
    public async Task Card_commit_with_declined_or_forged_provider_evidence_is_rejected()
    {
        await using var fixture = new CommitFixture();
        var claim = await fixture.SeedRefundPendingClaimAsync(
            PaymentMethodKind.Card,
            reference: "SQ:original-payment",
            cardTransactions:
            [
                CardTransaction("Square", "original-payment", 20m, responseText: "COMPLETED")
            ]);
        var request = new InstallmentCancelClaimCommitRequest(
        [
            new InstallmentRefundPaymentCommandDto(
                Guid.NewGuid(),
                PaymentMethodKind.Card,
                20m,
                "SQRF:forged-refund",
                [CardTransaction("Square", "forged-refund", 20m, responseCode: "05", responseText: "REJECTED")],
                RefundIdempotencyKey(claim, fixture.SourcePaymentGuid),
                fixture.SourcePaymentGuid)
        ]);
        var repository = new SqlSugarInstallmentCancelClaimCommitRepository(
            fixture.DbContext,
            new SqlSugarInstallmentRepository(fixture.DbContext),
            new NoOpInstallmentCancelClaimCommitFaultInjector());

        var error = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            repository.CommitAsync(claim, request, RecoveryIdentity(), Now, CancellationToken.None));

        Assert.Equal(InstallmentCancelClaimErrorCodes.RefundMethodUnsupported, error.Code);
    }

    [Fact]
    public async Task Voucher_commit_without_the_server_issued_refund_voucher_is_rejected()
    {
        await using var fixture = new CommitFixture();
        var claim = await fixture.SeedRefundPendingClaimAsync(
            PaymentMethodKind.Voucher,
            reference: "VC-ORIGINAL");
        var request = new InstallmentCancelClaimCommitRequest(
        [
            new InstallmentRefundPaymentCommandDto(
                Guid.NewGuid(),
                PaymentMethodKind.Voucher,
                20m,
                "VOUCHER_REFUND:RF-NOT-ISSUED",
                [],
                RefundIdempotencyKey(claim, fixture.SourcePaymentGuid),
                fixture.SourcePaymentGuid)
        ]);
        var repository = new SqlSugarInstallmentCancelClaimCommitRepository(
            fixture.DbContext,
            new SqlSugarInstallmentRepository(fixture.DbContext),
            new NoOpInstallmentCancelClaimCommitFaultInjector());

        var error = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            repository.CommitAsync(claim, request, RecoveryIdentity(), Now, CancellationToken.None));

        Assert.Equal(InstallmentCancelClaimErrorCodes.Invalid, error.Code);
    }

    [Fact]
    public async Task Card_commit_with_a_completed_client_transaction_is_rejected_without_server_evidence()
    {
        await using var fixture = new CommitFixture();
        var claim = await fixture.SeedRefundPendingClaimAsync(
            PaymentMethodKind.Card,
            reference: "SQ:original-payment",
            cardTransactions:
            [
                CardTransaction("Square", "original-payment", 20m, responseText: "COMPLETED")
            ]);
        var request = new InstallmentCancelClaimCommitRequest(
        [
            new InstallmentRefundPaymentCommandDto(
                Guid.NewGuid(),
                PaymentMethodKind.Card,
                20m,
                "SQRF:approved-refund",
                [CardTransaction("Square", "approved-refund", 20m, responseText: "COMPLETED")],
                RefundIdempotencyKey(claim, fixture.SourcePaymentGuid),
                fixture.SourcePaymentGuid)
        ]);
        var repository = new SqlSugarInstallmentCancelClaimCommitRepository(
            fixture.DbContext,
            new SqlSugarInstallmentRepository(fixture.DbContext),
            new NoOpInstallmentCancelClaimCommitFaultInjector());

        var error = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            repository.CommitAsync(claim, request, RecoveryIdentity(), Now, CancellationToken.None));

        Assert.Equal(InstallmentCancelClaimErrorCodes.RefundMethodUnsupported, error.Code);
        var state = await fixture.ReadStateAsync(claim);
        Assert.Equal(0, state.RefundCount);
        Assert.Equal((int)InstallmentStatus.Active, state.Order.Status);
    }

    [Fact]
    public async Task Server_issued_voucher_refund_bound_to_the_step_can_commit()
    {
        await using var fixture = new CommitFixture();
        var claim = await fixture.SeedRefundPendingClaimAsync(
            PaymentMethodKind.Voucher,
            reference: "VC-ORIGINAL");
        var idempotencyKey = RefundIdempotencyKey(claim, fixture.SourcePaymentGuid);
        var voucherCode = await fixture.SeedRefundVoucherAsync(idempotencyKey, 20m);
        var request = new InstallmentCancelClaimCommitRequest(
        [
            new InstallmentRefundPaymentCommandDto(
                Guid.NewGuid(),
                PaymentMethodKind.Voucher,
                20m,
                $"VOUCHER_REFUND:{voucherCode}",
                [],
                idempotencyKey,
                fixture.SourcePaymentGuid)
        ]);
        var repository = new SqlSugarInstallmentCancelClaimCommitRepository(
            fixture.DbContext,
            new SqlSugarInstallmentRepository(fixture.DbContext),
            new NoOpInstallmentCancelClaimCommitFaultInjector());

        var result = await repository.CommitAsync(
            claim,
            request,
            RecoveryIdentity(),
            Now,
            CancellationToken.None);

        Assert.Equal(InstallmentStatus.Cancelled, result.CommitResponse.Details.Status);
        Assert.Equal(InstallmentCancelClaimStatus.Committed, result.Claim.Status);
    }

    [Fact]
    public async Task Voucher_with_a_foreign_claim_marker_cannot_prove_this_claim()
    {
        await using var fixture = new CommitFixture();
        var claim = await fixture.SeedRefundPendingClaimAsync(
            PaymentMethodKind.Voucher,
            reference: "VC-ORIGINAL");
        var voucherCode = await fixture.SeedRefundVoucherAsync("foreign-operation:refund:foreign-payment", 20m);
        var request = new InstallmentCancelClaimCommitRequest(
        [
            new InstallmentRefundPaymentCommandDto(
                Guid.NewGuid(),
                PaymentMethodKind.Voucher,
                20m,
                $"VOUCHER_REFUND:{voucherCode}",
                [],
                RefundIdempotencyKey(claim, fixture.SourcePaymentGuid),
                fixture.SourcePaymentGuid)
        ]);
        var repository = new SqlSugarInstallmentCancelClaimCommitRepository(
            fixture.DbContext,
            new SqlSugarInstallmentRepository(fixture.DbContext),
            new NoOpInstallmentCancelClaimCommitFaultInjector());

        var error = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            repository.CommitAsync(claim, request, RecoveryIdentity(), Now, CancellationToken.None));

        Assert.Equal(InstallmentCancelClaimErrorCodes.Invalid, error.Code);
        Assert.Equal(0, (await fixture.ReadStateAsync(claim)).RefundCount);
    }

    [Fact]
    public async Task Voucher_with_a_noncanonical_embedded_marker_is_rejected()
    {
        await using var fixture = new CommitFixture();
        var claim = await fixture.SeedRefundPendingClaimAsync(
            PaymentMethodKind.Voucher,
            reference: "VC-ORIGINAL");
        var idempotencyKey = RefundIdempotencyKey(claim, fixture.SourcePaymentGuid);
        var voucherCode = await fixture.SeedRefundVoucherAsync(idempotencyKey, 20m, canonical: false);
        var request = new InstallmentCancelClaimCommitRequest(
        [
            new InstallmentRefundPaymentCommandDto(
                Guid.NewGuid(),
                PaymentMethodKind.Voucher,
                20m,
                $"VOUCHER_REFUND:{voucherCode}",
                [],
                idempotencyKey,
                fixture.SourcePaymentGuid)
        ]);
        var repository = new SqlSugarInstallmentCancelClaimCommitRepository(
            fixture.DbContext,
            new SqlSugarInstallmentRepository(fixture.DbContext),
            new NoOpInstallmentCancelClaimCommitFaultInjector());

        var error = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            repository.CommitAsync(claim, request, RecoveryIdentity(), Now, CancellationToken.None));

        Assert.Equal(InstallmentCancelClaimErrorCodes.Invalid, error.Code);
    }

    private static CardTransactionDto CardTransaction(
        string processor,
        string txnRef,
        decimal amount,
        string? responseCode = null,
        string? responseText = null) => new(
        processor,
        txnRef,
        null,
        null,
        null,
        null,
        null,
        responseCode,
        responseText,
        null,
        Now,
        amount,
        null);

    private static string RefundIdempotencyKey(
        InstallmentCancelClaimRecord claim,
        Guid originalPaymentGuid) =>
        $"{claim.OperationGuid:D}:refund:{originalPaymentGuid:D}";

    private static InstallmentRepaymentClaimIdentity RecoveryIdentity(
        string cashierId = "C02",
        string cashierName = "Recovery Cashier",
        string userGuid = "U02") => new(
            "S01",
            "POS-01",
            cashierId,
            cashierName,
            [],
            userGuid);

    private sealed class ThrowAfterRefundInsert : IInstallmentCancelClaimCommitFaultInjector
    {
        public Task AfterRefundsInsertedAsync(CancellationToken cancellationToken) =>
            throw new InjectedCommitFailure();
    }

    private sealed class InjectedCommitFailure : Exception;

    private sealed class CommitFixture : IAsyncDisposable
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-installment-cancel-claim-{Guid.NewGuid():N}.db");
        private readonly SqlSugarClient client;

        public CommitFixture()
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
                InstallmentRepaymentClaimEntity,
                InstallmentCancelClaimEntity>();
            client.CodeFirst.InitTables<StoreVoucher>();
            DbContext = CreateDbContext(client);
        }

        public HbposSqlSugarContext DbContext { get; }

        public Guid SourcePaymentGuid { get; private set; }

        public async Task<InstallmentCancelClaimRecord> SeedRefundPendingClaimAsync(
            PaymentMethodKind method = PaymentMethodKind.Cash,
            string? reference = null,
            IReadOnlyList<CardTransactionDto>? cardTransactions = null,
            bool paidOff = false,
            InstallmentCancelRefundMode refundMode = InstallmentCancelRefundMode.OriginalRoute)
        {
            var installmentGuid = Guid.NewGuid();
            await client.Insertable(new InstallmentOrderEntity
            {
                InstallmentGuid = installmentGuid.ToString("D"),
                InstallmentNumber = "INS-CANCEL-001",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                CashierId = "ORIGINAL",
                CashierName = "Original Cashier",
                CustomerName = "Cancel Customer",
                CustomerPhone = "0400000000",
                TotalAmount = paidOff ? 20m : 50m,
                MinimumDownPayment = 20m,
                DownPaymentAmount = 20m,
                PaidAmount = 20m,
                BalanceAmount = paidOff ? 0m : 30m,
                Status = paidOff ? (int)InstallmentStatus.PaidOff : (int)InstallmentStatus.Active,
                CreatedAt = Now.AddDays(-1).UtcDateTime,
                UpdatedAt = Now.AddDays(-1).UtcDateTime
            }).ExecuteCommandAsync();
            SourcePaymentGuid = Guid.NewGuid();
            await client.Insertable(new InstallmentPaymentEntity
            {
                PaymentGuid = SourcePaymentGuid.ToString("D"),
                InstallmentGuid = installmentGuid.ToString("D"),
                Method = (int)method,
                Amount = 20m,
                Reference = reference,
                Status = (int)InstallmentPaymentStatus.Recorded,
                RecordedAt = Now.AddDays(-1).UtcDateTime,
                CashierId = "ORIGINAL",
                CashierName = "Original Cashier",
                DeviceCode = "POS-01",
                CardTransactionsJson = cardTransactions is null
                    ? null
                    : System.Text.Json.JsonSerializer.Serialize(cardTransactions),
                IdempotencyKey = "down-payment"
            }).ExecuteCommandAsync();
            var installments = new SqlSugarInstallmentRepository(DbContext);
            var details = await installments.GetDetailsAsync(installmentGuid, CancellationToken.None);
            Assert.NotNull(details);
            var entity = new InstallmentCancelClaimEntity
            {
                InstallmentGuid = installmentGuid,
                OperationGuid = Guid.NewGuid(),
                StoreCode = "S01",
                ClaimantDeviceCode = "POS-01",
                CashierId = "C01",
                CashierName = "Claim Cashier",
                IdempotencyKey = "cancel-operation",
                Reason = "customer request",
                RefundPlanFingerprint = InstallmentCancelClaimFingerprint.Create(details!),
                Status = InstallmentCancelClaimStatus.RefundPending.ToString(),
                IsBlocking = true,
                CreatedAtUtc = Now.AddMinutes(-1).UtcDateTime,
                UpdatedAtUtc = Now.AddMinutes(-1).UtcDateTime,
                Revision = 2,
                RefundMode = InstallmentCancelClaimRecord.ToStoredRefundMode(refundMode)
            };
            await client.Insertable(entity).ExecuteCommandAsync();
            return new InstallmentCancelClaimRecord(
                entity.InstallmentGuid,
                entity.OperationGuid,
                entity.StoreCode,
                entity.ClaimantDeviceCode,
                entity.CashierId,
                entity.CashierName,
                entity.IdempotencyKey,
                entity.Reason,
                entity.RefundPlanFingerprint,
                InstallmentCancelClaimStatus.RefundPending,
                new DateTimeOffset(entity.CreatedAtUtc, TimeSpan.Zero),
                new DateTimeOffset(entity.UpdatedAtUtc, TimeSpan.Zero),
                null,
                null,
                entity.Revision,
                RefundMode: refundMode);
        }

        public async Task<string> SeedRefundVoucherAsync(
            string idempotencyKey,
            decimal amount,
            bool canonical = true)
        {
            var voucherCode = $"RF{Guid.NewGuid():N}"[..14].ToUpperInvariant();
            var marker = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(idempotencyKey));
            await client.Insertable(new StoreVoucher
            {
                StoreCode = "S01",
                VoucherCode = voucherCode,
                VoucherType = 3,
                Amount = amount,
                RemainingAmount = amount,
                Status = "1",
                IsDelete = false,
                Remark = canonical
                    ? $"RefundKey[{marker}] | Refund voucher"
                    : $"Refund voucher | RefundKey[{marker}]"
            }).ExecuteCommandAsync();
            return voucherCode;
        }

        public async Task<CommitState> ReadStateAsync(InstallmentCancelClaimRecord claim)
        {
            var installmentGuid = claim.InstallmentGuid.ToString("D");
            var order = await client.Queryable<InstallmentOrderEntity>()
                .FirstAsync(entity => entity.InstallmentGuid == installmentGuid);
            var claimEntity = await client.Queryable<InstallmentCancelClaimEntity>()
                .FirstAsync(entity => entity.OperationGuid == claim.OperationGuid);
            var refunds = await client.Queryable<InstallmentPaymentEntity>()
                .Where(entity => entity.InstallmentGuid == installmentGuid && entity.Amount < 0m)
                .ToListAsync();
            return new CommitState(
                order,
                claimEntity,
                refunds.SingleOrDefault() ?? new InstallmentPaymentEntity(),
                refunds.Count);
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
        InstallmentCancelClaimEntity Claim,
        InstallmentPaymentEntity Refund,
        int RefundCount);
}
