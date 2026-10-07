using BlazorApp.Shared.Constants;
using Hbpos.Api.Services;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class InstallmentCancelClaimServiceTests
{
    private static readonly Guid InstallmentGuid = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly DateTimeOffset InitialNow = DateTimeOffset.Parse("2026-08-04T01:00:00Z");

    [Fact]
    public void Refund_plan_fingerprint_exactly_matches_the_iPad_canonical_test_vector()
    {
        var details = Details(payments:
        [
            Payment(Guid.Parse("40000000-0000-4000-8000-000000000001"), PaymentMethodKind.Voucher, 7.25m),
            Payment(Guid.Parse("20000000-0000-4000-8000-000000000001"), PaymentMethodKind.Cash, 20m),
            Payment(Guid.Parse("30000000-0000-4000-8000-000000000001"), PaymentMethodKind.Card, 10.50m)
        ]);

        var fingerprint = InstallmentCancelClaimFingerprint.Create(details);

        Assert.Equal("sha256:e71e70a0dde391c395f87e43cbeb12056488ad6fbbd76622ba77761cf2b816e4", fingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Card_and_mixed_tender_are_rejected_before_a_cancel_claim_can_begin(bool mixedTender)
    {
        var payments = new List<InstallmentPaymentDto>
        {
            Payment(Guid.NewGuid(), PaymentMethodKind.Card, 10m)
        };
        if (mixedTender)
        {
            payments.Add(Payment(Guid.NewGuid(), PaymentMethodKind.Cash, 10m));
        }

        var harness = CreateHarness(payments: payments);
        var request = CreateRequest(InstallmentCancelClaimFingerprint.Create(harness.Installments.Details));

        var error = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            harness.Service.CreateAsync(InstallmentGuid, request, Identity(), CancellationToken.None));

        Assert.Equal(InstallmentCancelClaimErrorCodes.RefundMethodUnsupported, error.Code);
        Assert.Empty(harness.Repository.Records);
    }

    [Fact]
    public async Task Begin_rechecks_authoritative_payments_before_releasing_the_client_to_refund_providers()
    {
        var harness = CreateHarness();
        var request = CreateRequest(InstallmentCancelClaimFingerprint.Create(harness.Installments.Details));
        await harness.Service.CreateAsync(InstallmentGuid, request, Identity(), CancellationToken.None);
        harness.Installments.Details = Details(
        [
            Payment(Guid.NewGuid(), PaymentMethodKind.Cash, 10m),
            Payment(Guid.NewGuid(), PaymentMethodKind.Card, 10m)
        ]);

        var error = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            harness.Service.BeginRefundAsync(
                InstallmentGuid,
                request.OperationGuid,
                Identity(),
                CancellationToken.None));

        Assert.Equal(InstallmentCancelClaimErrorCodes.RefundMethodUnsupported, error.Code);
        Assert.Equal(InstallmentCancelClaimStatus.Prepared, harness.Repository.Records[request.OperationGuid].Status);
    }

    [Fact]
    public async Task Voucher_mode_claim_for_card_original_can_be_created_and_begin_refund()
    {
        var harness = CreateHarness(payments:
        [
            Payment(Guid.NewGuid(), PaymentMethodKind.Cash, 10m),
            Payment(Guid.NewGuid(), PaymentMethodKind.Card, 10m)
        ]);
        var fingerprint = InstallmentCancelClaimFingerprint.Create(harness.Installments.Details);
        var request = CreateRequest(fingerprint) with { RefundMode = InstallmentCancelRefundMode.Voucher };

        var created = await harness.Service.CreateAsync(InstallmentGuid, request, Identity(), CancellationToken.None);
        var begun = await harness.Service.BeginRefundAsync(
            InstallmentGuid,
            request.OperationGuid,
            Identity(),
            CancellationToken.None);

        Assert.Equal(InstallmentCancelRefundMode.Voucher, created.RefundMode);
        Assert.Equal(InstallmentCancelClaimStatus.RefundPending, begun.Status);
        Assert.Equal(InstallmentCancelRefundMode.Voucher, harness.Repository.Records[request.OperationGuid].RefundMode);

        // 同一 operationGuid 换成原路退属于改写不可变事实，必须拒绝。
        var changed = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            harness.Service.CreateAsync(
                InstallmentGuid,
                request with { RefundMode = null },
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentCancelClaimErrorCodes.Mismatch, changed.Code);
    }

    [Fact]
    public async Task Original_route_claim_for_card_original_still_fails_closed_and_invalid_mode_is_rejected()
    {
        var harness = CreateHarness(payments: [Payment(Guid.NewGuid(), PaymentMethodKind.Card, 20m)]);
        var fingerprint = InstallmentCancelClaimFingerprint.Create(harness.Installments.Details);

        var unsupported = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            harness.Service.CreateAsync(InstallmentGuid, CreateRequest(fingerprint), Identity(), CancellationToken.None));
        Assert.Equal(InstallmentCancelClaimErrorCodes.RefundMethodUnsupported, unsupported.Code);

        var invalid = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            harness.Service.CreateAsync(
                InstallmentGuid,
                CreateRequest(fingerprint) with { RefundMode = (InstallmentCancelRefundMode)99 },
                Identity(),
                CancellationToken.None));
        Assert.NotEqual(InstallmentCancelClaimErrorCodes.RefundMethodUnsupported, invalid.Code);
    }

    [Theory]
    [InlineData(PaymentMethodKind.Cash)]
    [InlineData(PaymentMethodKind.Voucher)]
    public async Task Cash_and_voucher_claims_can_begin_refund(PaymentMethodKind method)
    {
        var harness = CreateHarness(payments: [Payment(Guid.NewGuid(), method, 20m)]);
        var request = CreateRequest(InstallmentCancelClaimFingerprint.Create(harness.Installments.Details));
        await harness.Service.CreateAsync(InstallmentGuid, request, Identity(), CancellationToken.None);

        var begun = await harness.Service.BeginRefundAsync(
            InstallmentGuid,
            request.OperationGuid,
            Identity(),
            CancellationToken.None);

        Assert.Equal(InstallmentCancelClaimStatus.RefundPending, begun.Status);
    }

    [Fact]
    public async Task Cross_device_create_allows_same_store_and_persists_original_and_executor()
    {
        var harness = CreateHarness(crossDeviceLifecycleEnabled: true);
        var fingerprint = InstallmentCancelClaimFingerprint.Create(harness.Installments.Details);
        var request = CreateRequest(fingerprint);

        var created = await harness.Service.CreateAsync(
            InstallmentGuid,
            request,
            Identity(deviceCode: "POS-02"),
            CancellationToken.None);

        Assert.Equal(InstallmentCancelClaimStatus.Prepared, created.Status);
        Assert.Equal("POS-01", created.OriginalDeviceCode);
        Assert.Equal("POS-02", created.ExecutingDeviceCode);
        Assert.Equal("POS-01", harness.Repository.Records[request.OperationGuid].OriginalDeviceCode);
        Assert.Equal("POS-02", harness.Repository.Records[request.OperationGuid].ClaimantDeviceCode);

        var otherStore = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            harness.Service.CreateAsync(
                InstallmentGuid,
                CreateRequest(fingerprint),
                Identity(storeCode: "S02", deviceCode: "POS-99"),
                CancellationToken.None));
        Assert.Equal(InstallmentCancelClaimErrorCodes.Mismatch, otherStore.Code);
    }

    [Fact]
    public async Task Create_allows_paid_off_installment_but_rejects_picked_up_installment()
    {
        var harness = CreateHarness();
        harness.Installments.Details = Details() with
        {
            TotalAmount = 20m,
            BalanceAmount = 0m,
            Status = InstallmentStatus.PaidOff
        };
        var fingerprint = InstallmentCancelClaimFingerprint.Create(harness.Installments.Details);

        var created = await harness.Service.CreateAsync(
            InstallmentGuid,
            CreateRequest(fingerprint),
            Identity(),
            CancellationToken.None);
        Assert.Equal(InstallmentCancelClaimStatus.Prepared, created.Status);

        var pickedUpHarness = CreateHarness();
        pickedUpHarness.Installments.Details = harness.Installments.Details with { Status = InstallmentStatus.PickedUp };
        var pickedUp = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            pickedUpHarness.Service.CreateAsync(
                InstallmentGuid,
                CreateRequest(fingerprint),
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentCancelClaimErrorCodes.Mismatch, pickedUp.Code);
    }

    [Fact]
    public async Task Create_validates_the_authoritative_plan_and_expires_after_120_seconds()
    {
        var harness = CreateHarness();
        var fingerprint = InstallmentCancelClaimFingerprint.Create(harness.Installments.Details);
        var request = CreateRequest(fingerprint);

        var created = await harness.Service.CreateAsync(
            InstallmentGuid,
            request,
            Identity(),
            CancellationToken.None);

        Assert.Equal(InstallmentCancelClaimStatus.Prepared, created.Status);
        Assert.Equal(InitialNow.AddSeconds(120), created.ExpiresAtUtc);

        var mismatch = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            harness.Service.CreateAsync(
                InstallmentGuid,
                request with { RefundPlanFingerprint = $"sha256:{new string('0', 64)}" },
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentCancelClaimErrorCodes.Mismatch, mismatch.Code);

        harness.Time.UtcNow = InitialNow.AddSeconds(121);
        await harness.Service.CreateAsync(InstallmentGuid, CreateRequest(fingerprint), Identity(), CancellationToken.None);
        Assert.Equal(InstallmentCancelClaimStatus.Released, harness.Repository.Records[request.OperationGuid].Status);
    }

    [Fact]
    public async Task Refund_pending_and_unknown_never_expire_and_resolve_only_allows_the_locked_transitions()
    {
        var harness = CreateHarness();
        var request = CreateRequest(InstallmentCancelClaimFingerprint.Create(harness.Installments.Details));
        await harness.Service.CreateAsync(InstallmentGuid, request, Identity(), CancellationToken.None);

        var pending = await harness.Service.BeginRefundAsync(InstallmentGuid, request.OperationGuid, Identity(), CancellationToken.None);
        Assert.Equal(InstallmentCancelClaimStatus.RefundPending, pending.Status);
        Assert.Null(pending.ExpiresAtUtc);

        var invalidRelease = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            harness.Service.ResolveAsync(
                InstallmentGuid,
                request.OperationGuid,
                new InstallmentCancelClaimResolveRequest(InstallmentCancelClaimResolveOutcome.Released),
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentCancelClaimErrorCodes.Mismatch, invalidRelease.Code);

        var approvedRefund = new InstallmentRefundPaymentCommandDto(
            Guid.NewGuid(), PaymentMethodKind.Cash, 20m, null, [], request.IdempotencyKey);
        var falseDecline = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            harness.Service.ResolveAsync(
                InstallmentGuid,
                request.OperationGuid,
                new InstallmentCancelClaimResolveRequest(
                    InstallmentCancelClaimResolveOutcome.Declined,
                    [approvedRefund]),
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentCancelClaimErrorCodes.Invalid, falseDecline.Code);

        var unknown = await harness.Service.ResolveAsync(
            InstallmentGuid,
            request.OperationGuid,
            new InstallmentCancelClaimResolveRequest(InstallmentCancelClaimResolveOutcome.Unknown),
            Identity(),
            CancellationToken.None);
        harness.Time.UtcNow = InitialNow.AddDays(30);
        Assert.Equal(InstallmentCancelClaimStatus.Unknown, unknown.Status);

        var busy = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            harness.Service.CreateAsync(
                InstallmentGuid,
                CreateRequest(InstallmentCancelClaimFingerprint.Create(harness.Installments.Details)),
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentCancelClaimErrorCodes.Busy, busy.Code);

        var resumed = await harness.Service.BeginRefundAsync(InstallmentGuid, request.OperationGuid, Identity(), CancellationToken.None);
        Assert.Equal(InstallmentCancelClaimStatus.RefundPending, resumed.Status);
    }

    [Fact]
    public async Task Permission_is_revalidated_before_side_effects_and_prepared_cannot_change_cashier()
    {
        var harness = CreateHarness();
        var fingerprint = InstallmentCancelClaimFingerprint.Create(harness.Installments.Details);

        var denied = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            harness.Service.CreateAsync(
                InstallmentGuid,
                CreateRequest(fingerprint),
                Identity(hasCancelPermission: false),
                CancellationToken.None));
        Assert.Equal(InstallmentCancelClaimErrorCodes.PermissionDenied, denied.Code);
        Assert.Empty(harness.Repository.Records);

        var request = CreateRequest(fingerprint);
        await harness.Service.CreateAsync(InstallmentGuid, request, Identity(), CancellationToken.None);
        var wrongCashier = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            harness.Service.BeginRefundAsync(
                InstallmentGuid,
                request.OperationGuid,
                Identity(cashierId: "C02", cashierName: "Relief Cashier", cashierUserGuid: "U02"),
                CancellationToken.None));
        Assert.Equal(InstallmentCancelClaimErrorCodes.Mismatch, wrongCashier.Code);
        Assert.Equal(InstallmentCancelClaimStatus.Prepared, harness.Repository.Records[request.OperationGuid].Status);

        await harness.Service.BeginRefundAsync(InstallmentGuid, request.OperationGuid, Identity(), CancellationToken.None);
        await harness.Service.ResolveAsync(
            InstallmentGuid,
            request.OperationGuid,
            new InstallmentCancelClaimResolveRequest(InstallmentCancelClaimResolveOutcome.Unknown),
            Identity(),
            CancellationToken.None);
        var relief = Identity(cashierId: "C02", cashierName: "Relief Cashier", cashierUserGuid: "U02");
        await harness.Service.BeginRefundAsync(
            InstallmentGuid,
            request.OperationGuid,
            relief,
            CancellationToken.None);

        var recovered = harness.Repository.Records[request.OperationGuid];
        Assert.Equal(InstallmentCancelClaimStatus.RefundPending, recovered.Status);
        Assert.Equal("C02", recovered.LastRecoveryCashierId);
        Assert.Equal("Relief Cashier", recovered.LastRecoveryCashierName);
        Assert.Equal("U02", recovered.LastRecoveryCashierUserGuid);
        Assert.NotNull(recovered.RecoveredAtUtc);
        Assert.Equal("C01", recovered.CashierId);
        Assert.Equal("Cashier One", recovered.CashierName);
    }

    [Fact]
    public async Task Commit_replays_the_persisted_snapshot_without_executing_the_mutation_twice()
    {
        var harness = CreateHarness();
        var request = CreateRequest(InstallmentCancelClaimFingerprint.Create(harness.Installments.Details));
        var originalPaymentGuid = Guid.Parse("20000000-0000-4000-8000-000000000001");
        await harness.Service.CreateAsync(InstallmentGuid, request, Identity(), CancellationToken.None);
        await harness.Service.BeginRefundAsync(InstallmentGuid, request.OperationGuid, Identity(), CancellationToken.None);
        var commit = new InstallmentCancelClaimCommitRequest(
        [
            new InstallmentRefundPaymentCommandDto(
                Guid.Parse("50000000-0000-4000-8000-000000000001"),
                PaymentMethodKind.Cash,
                20m,
                null,
                [],
                $"{request.OperationGuid:D}:refund:{originalPaymentGuid:D}",
                originalPaymentGuid)
        ]);

        var first = await harness.Service.CommitAsync(InstallmentGuid, request.OperationGuid, commit, Identity(), CancellationToken.None);
        var createReplay = await harness.Service.CreateAsync(
            InstallmentGuid,
            request,
            Identity(),
            CancellationToken.None);
        Assert.Equal(InstallmentCancelClaimStatus.Committed, createReplay.Status);
        Assert.NotNull(createReplay.Commit);
        // 提交后账本即使被测试替身改写，GET/commit 重放也必须返回 claim 中持久化的原始快照。
        harness.Installments.Details = Details();
        var replay = await harness.Service.CommitAsync(InstallmentGuid, request.OperationGuid, commit, Identity(), CancellationToken.None);

        Assert.Equal(InstallmentCancelClaimStatus.Committed, first.Status);
        Assert.Equal(InstallmentStatus.Cancelled, first.Commit?.Details.Status);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(first.Commit),
            System.Text.Json.JsonSerializer.Serialize(replay.Commit));
        Assert.Equal(1, harness.CommitRepository.CommitCount);
    }

    [Fact]
    public async Task Legacy_cancel_required_gate_and_all_other_mutations_fail_closed_while_blocking()
    {
        var required = CreateHarness(required: true);
        var requiredError = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            required.Service.EnsureLegacyCancelAllowedAsync(InstallmentGuid, CancellationToken.None));
        Assert.Equal(InstallmentCancelClaimErrorCodes.ClaimRequired, requiredError.Code);

        var harness = CreateHarness();
        var request = CreateRequest(InstallmentCancelClaimFingerprint.Create(harness.Installments.Details));
        await harness.Service.CreateAsync(InstallmentGuid, request, Identity(), CancellationToken.None);

        var busy = await Assert.ThrowsAsync<InstallmentCancelClaimException>(() =>
            harness.Service.EnsureNoBlockingClaimAsync(InstallmentGuid, CancellationToken.None));
        Assert.Equal(InstallmentCancelClaimErrorCodes.Busy, busy.Code);
    }

    private static Harness CreateHarness(
        bool required = false,
        IReadOnlyList<InstallmentPaymentDto>? payments = null,
        bool crossDeviceLifecycleEnabled = false)
    {
        var repository = new MemoryRepository();
        var installments = new FakeInstallmentRepository(Details(payments));
        var commitRepository = new FakeCommitRepository(repository, installments);
        var time = new MutableTimeProvider(InitialNow);
        var service = new InstallmentCancelClaimService(
            repository,
            installments,
            commitRepository,
            Options.Create(new InstallmentCancelClaimOptions { Required = required }),
            time,
            lifecycleOptions: Options.Create(new InstallmentCrossDeviceLifecycleOptions
            {
                CancelRefundEnabled = crossDeviceLifecycleEnabled
            }));
        return new Harness(service, repository, installments, commitRepository, time);
    }

    private static InstallmentCancelClaimCreateRequest CreateRequest(string fingerprint) => new(
        Guid.NewGuid(),
        Guid.NewGuid().ToString("D"),
        "customer changed mind",
        fingerprint);

    private static InstallmentRepaymentClaimIdentity Identity(
        string storeCode = "S01",
        string deviceCode = "POS-01",
        string cashierId = "C01",
        string cashierName = "Cashier One",
        string cashierUserGuid = "U01",
        bool hasCancelPermission = true) => new(
            storeCode,
            deviceCode,
            cashierId,
            cashierName,
            hasCancelPermission ? [Permissions.PosTerminal.Installments.Cancel] : [],
            cashierUserGuid);

    private static InstallmentDetailsDto Details(IReadOnlyList<InstallmentPaymentDto>? payments = null) => new(
        InstallmentGuid,
        "IP-001",
        "S01",
        "POS-01",
        "C00",
        "Original Cashier",
        "Customer",
        "0400000000",
        InitialNow.AddDays(-1),
        50m,
        20m,
        20m,
        20m,
        30m,
        InstallmentStatus.Active,
        [],
        payments ?? [Payment(Guid.Parse("20000000-0000-4000-8000-000000000001"), PaymentMethodKind.Cash, 20m)],
        null);

    private static InstallmentPaymentDto Payment(
        Guid paymentGuid,
        PaymentMethodKind method,
        decimal amount,
        InstallmentPaymentStatus status = InstallmentPaymentStatus.Recorded) => new(
            paymentGuid,
            method,
            amount,
            null,
            status,
            InitialNow,
            "C00",
            "POS-01");

    private sealed record Harness(
        InstallmentCancelClaimService Service,
        MemoryRepository Repository,
        FakeInstallmentRepository Installments,
        FakeCommitRepository CommitRepository,
        MutableTimeProvider Time);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class MemoryRepository : IInstallmentCancelClaimRepository
    {
        public Dictionary<Guid, InstallmentCancelClaimRecord> Records { get; } = [];

        public Task<InstallmentCancelClaimRecord?> GetAsync(Guid operationGuid, CancellationToken cancellationToken) =>
            Task.FromResult(Records.GetValueOrDefault(operationGuid));

        public Task<InstallmentCancelClaimRecord?> GetBlockingAsync(Guid installmentGuid, CancellationToken cancellationToken) =>
            Task.FromResult(Records.Values.FirstOrDefault(record =>
                record.InstallmentGuid == installmentGuid && InstallmentCancelClaimRecord.IsBlocking(record.Status)));

        public Task<bool> TryInsertAsync(InstallmentCancelClaimRecord claim, CancellationToken cancellationToken)
        {
            if (Records.ContainsKey(claim.OperationGuid) ||
                Records.Values.Any(record => record.InstallmentGuid == claim.InstallmentGuid && InstallmentCancelClaimRecord.IsBlocking(record.Status)))
            {
                return Task.FromResult(false);
            }

            Records.Add(claim.OperationGuid, claim);
            return Task.FromResult(true);
        }

        public Task<bool> TryUpdateAsync(InstallmentCancelClaimRecord claim, long expectedRevision, CancellationToken cancellationToken)
        {
            if (!Records.TryGetValue(claim.OperationGuid, out var current) || current.Revision != expectedRevision)
            {
                return Task.FromResult(false);
            }

            Records[claim.OperationGuid] = claim;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeCommitRepository(
        MemoryRepository repository,
        FakeInstallmentRepository installments) : IInstallmentCancelClaimCommitRepository
    {
        public int CommitCount { get; private set; }

        public Task<InstallmentCancelClaimCommitResult> CommitAsync(
            InstallmentCancelClaimRecord expectedClaim,
            InstallmentCancelClaimCommitRequest request,
            InstallmentRepaymentClaimIdentity identity,
            DateTimeOffset committedAtUtc,
            CancellationToken cancellationToken)
        {
            CommitCount++;
            var details = installments.Details with
            {
                PaidAmount = 0m,
                Status = InstallmentStatus.Cancelled,
                BalanceAmount = 0m,
                Payments = installments.Details.Payments.Concat(
                    request.Refunds.Select(refund => InstallmentService.MapRefundPayment(
                        refund,
                        expectedClaim.CashierId,
                        expectedClaim.CashierName,
                        expectedClaim.ClaimantDeviceCode,
                        committedAtUtc))).ToArray(),
                CancellationInfo = new InstallmentCancellationInfoDto(
                    InstallmentCancellationKind.RefundCancel,
                    committedAtUtc,
                    identity.CashierName,
                    expectedClaim.Reason,
                    expectedClaim.IdempotencyKey)
            };
            installments.Details = details;
            var response = new InstallmentCancelClaimCommitResponse(details, AlreadyCancelled: false);
            var committed = expectedClaim with
            {
                Status = InstallmentCancelClaimStatus.Committed,
                UpdatedAtUtc = committedAtUtc,
                ExpiresAtUtc = null,
                CommittedAtUtc = committedAtUtc,
                Revision = expectedClaim.Revision + 1,
                CommitResponseJson = System.Text.Json.JsonSerializer.Serialize(response)
            };
            repository.Records[committed.OperationGuid] = committed;
            return Task.FromResult(new InstallmentCancelClaimCommitResult(committed, response, false));
        }
    }

    private sealed class FakeInstallmentRepository(InstallmentDetailsDto details) : IInstallmentRepository
    {
        public InstallmentDetailsDto Details { get; set; } = details;

        public Task<InstallmentDetailsDto?> GetDetailsAsync(Guid installmentGuid, CancellationToken cancellationToken) =>
            Task.FromResult<InstallmentDetailsDto?>(installmentGuid == Details.InstallmentGuid ? Details : null);

        public Task CreateAsync(InstallmentDetailsDto details, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentDetailsDto> AppendPaymentAsync(Guid installmentGuid, InstallmentPaymentDto payment, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentDetailsDto> ConfirmPickupAsync(Guid installmentGuid, DateTimeOffset pickedUpAt, string pickedUpBy, string? note, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentDetailsDto> CancelWithRefundAsync(Guid installmentGuid, IReadOnlyList<InstallmentPaymentDto> refunds, InstallmentCancellationInfoDto cancellationInfo, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentDetailsDto> VoidAsync(Guid installmentGuid, InstallmentCancellationInfoDto cancellationInfo, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentPaymentLookup?> FindPaymentAsync(Guid paymentGuid, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentPaymentLookup?> FindPaymentByIdempotencyKeyAsync(Guid installmentGuid, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentHistoryQueryResponse> QueryAsync(InstallmentHistoryQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
