using BlazorApp.Shared.Constants;
using Hbpos.Api.Services;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class InstallmentRepaymentClaimServiceTests
{
    private static readonly DateTimeOffset InitialNow = DateTimeOffset.Parse("2026-08-04T00:00:00Z");

    [Fact]
    public void Capabilities_default_to_required_repayment_claims_and_fail_closed_lifecycle_switches()
    {
        var harness = CreateHarness(crossDeviceEnabled: true);

        var capabilities = harness.Service.GetCapabilities();

        Assert.True(capabilities.RepaymentClaimsSupported);
        Assert.True(capabilities.RepaymentClaimsRequired);
        Assert.True(capabilities.CrossDeviceRepaymentEnabled);
        Assert.False(capabilities.CrossDeviceCancelRefundEnabled);
        Assert.False(capabilities.CrossDeviceVoidEnabled);
        Assert.False(capabilities.CrossDevicePickupEnabled);
        Assert.False(capabilities.CardRepaymentSupported);
        Assert.True(capabilities.RepaymentClaimPrepareProviderV1);
        Assert.Equal(120, capabilities.PreparedClaimTtlSeconds);
    }

    [Fact]
    public async Task Prepare_provider_creates_provider_pending_without_writing_payment_and_replays_same_binding()
    {
        var harness = CreateHarness();
        var create = CreateRequest(method: PaymentMethodKind.Cash);
        var request = PrepareRequest(create, provider: "cash", providerAttemptId: "attempt-prepare");
        var before = await harness.Installments.GetDetailsAsync(harness.InstallmentGuid, CancellationToken.None);

        var first = await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            request,
            Identity(),
            CancellationToken.None);
        var replay = await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            request,
            Identity(),
            CancellationToken.None);

        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending, first.Status);
        Assert.Equal("cash", first.Provider);
        Assert.Equal("attempt-prepare", first.ProviderAttemptId);
        Assert.False(first.AlreadyExists);
        Assert.True(replay.AlreadyExists);
        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending, replay.Status);
        Assert.Equal(0, harness.Installments.AppendCount);
        Assert.Equal(before, await harness.Installments.GetDetailsAsync(harness.InstallmentGuid, CancellationToken.None));
    }

    [Fact]
    public async Task Prepare_provider_binds_prepared_claim_and_recovers_unknown_only_for_the_same_binding()
    {
        var harness = CreateHarness();
        var create = CreateRequest(method: PaymentMethodKind.Cash);
        var request = PrepareRequest(create, provider: "cash", providerAttemptId: "attempt-recover");
        await harness.Service.CreateAsync(harness.InstallmentGuid, create, Identity(), CancellationToken.None);

        var prepared = await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            request,
            Identity(),
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending, prepared.Status);

        await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Unknown),
            Identity(),
            CancellationToken.None);

        var recovered = await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            request,
            Identity(cashierId: "RECOVERY", cashierName: "Recovery Cashier", cashierUserGuid: "U-RECOVERY"),
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending, recovered.Status);

        var wrongBinding = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.PrepareProviderAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                request with { ProviderAttemptId = "different-attempt" },
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, wrongBinding.Code);
    }

    [Fact]
    public async Task Prepare_provider_returns_persisted_commit_and_rejects_released_declined_or_fact_mismatch()
    {
        var committedHarness = CreateHarness();
        var committedCreate = CreateRequest(method: PaymentMethodKind.Cash);
        var committedRequest = PrepareRequest(committedCreate, provider: "cash", providerAttemptId: "attempt-commit");
        await committedHarness.Service.PrepareProviderAsync(
            committedHarness.InstallmentGuid,
            committedCreate.OperationGuid,
            committedRequest,
            Identity(),
            CancellationToken.None);
        var committed = await committedHarness.Service.CommitAsync(
            committedHarness.InstallmentGuid,
            committedCreate.OperationGuid,
            new InstallmentRepaymentClaimCommitRequest(),
            Identity(),
            CancellationToken.None);

        var committedReplay = await committedHarness.Service.PrepareProviderAsync(
            committedHarness.InstallmentGuid,
            committedCreate.OperationGuid,
            committedRequest,
            Identity(),
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Committed, committedReplay.Status);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(committed.Commit),
            System.Text.Json.JsonSerializer.Serialize(committedReplay.Commit));
        Assert.True(committedReplay.AlreadyExists);
        Assert.Equal(1, committedHarness.Installments.AppendCount);

        foreach (var outcome in new[]
        {
            InstallmentRepaymentClaimResolveOutcome.Released,
            InstallmentRepaymentClaimResolveOutcome.Declined
        })
        {
            var harness = CreateHarness();
            var create = CreateRequest(method: PaymentMethodKind.Cash);
            var request = PrepareRequest(create, provider: "cash", providerAttemptId: $"attempt-{outcome}");
            await harness.Service.CreateAsync(harness.InstallmentGuid, create, Identity(), CancellationToken.None);
            if (outcome == InstallmentRepaymentClaimResolveOutcome.Declined)
            {
                await harness.Service.BeginProviderAsync(
                    harness.InstallmentGuid,
                    create.OperationGuid,
                    new InstallmentRepaymentClaimBeginProviderRequest("cash", request.ProviderAttemptId),
                    Identity(),
                    CancellationToken.None);
            }

            await harness.Service.ResolveAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                new InstallmentRepaymentClaimResolveRequest(outcome),
                Identity(),
                CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
                harness.Service.PrepareProviderAsync(
                    harness.InstallmentGuid,
                    create.OperationGuid,
                    request,
                    Identity(),
                    CancellationToken.None));
            Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, exception.Code);
        }

        var mismatchHarness = CreateHarness();
        var mismatchCreate = CreateRequest(method: PaymentMethodKind.Cash);
        var mismatchRequest = PrepareRequest(mismatchCreate, provider: "cash", providerAttemptId: "attempt-facts");
        await mismatchHarness.Service.PrepareProviderAsync(
            mismatchHarness.InstallmentGuid,
            mismatchCreate.OperationGuid,
            mismatchRequest,
            Identity(),
            CancellationToken.None);
        var factsMismatch = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            mismatchHarness.Service.PrepareProviderAsync(
                mismatchHarness.InstallmentGuid,
                mismatchCreate.OperationGuid,
                mismatchRequest with { Amount = mismatchRequest.Amount + 1m },
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, factsMismatch.Code);
    }

    [Fact]
    public async Task Prepare_provider_maps_other_operation_to_busy_and_other_device_to_access_mismatch()
    {
        var harness = CreateHarness();
        var first = CreateRequest(method: PaymentMethodKind.Cash);
        await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            first.OperationGuid,
            PrepareRequest(first, provider: "cash", providerAttemptId: "attempt-first"),
            Identity(),
            CancellationToken.None);

        var otherOperation = CreateRequest(method: PaymentMethodKind.Cash);
        var busy = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.PrepareProviderAsync(
                harness.InstallmentGuid,
                otherOperation.OperationGuid,
                PrepareRequest(otherOperation, provider: "cash", providerAttemptId: "attempt-other"),
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Busy, busy.Code);

        var otherDevice = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.PrepareProviderAsync(
                harness.InstallmentGuid,
                first.OperationGuid,
                PrepareRequest(first, provider: "cash", providerAttemptId: "attempt-first"),
                Identity(deviceCode: "POS-99"),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, otherDevice.Code);
    }

    [Fact]
    public async Task Prepare_prepared_CAS_loser_replays_the_same_winning_binding()
    {
        var harness = CreateHarness();
        var create = CreateRequest(method: PaymentMethodKind.Cash);
        var request = PrepareRequest(create, provider: "cash", providerAttemptId: "attempt-prepared-race");
        await harness.Service.CreateAsync(harness.InstallmentGuid, create, Identity(), CancellationToken.None);
        harness.Repository.ConcurrentUpdateFactory = candidate => candidate;

        var replay = await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            request,
            Identity(),
            CancellationToken.None);

        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending, replay.Status);
        Assert.Equal("attempt-prepared-race", replay.ProviderAttemptId);
        Assert.True(replay.AlreadyExists);
    }

    [Fact]
    public async Task Prepare_unknown_CAS_loser_replays_the_same_winning_binding()
    {
        var harness = CreateHarness();
        var create = CreateRequest(method: PaymentMethodKind.Cash);
        var request = PrepareRequest(create, provider: "cash", providerAttemptId: "attempt-unknown-race");
        await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            request,
            Identity(),
            CancellationToken.None);
        await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Unknown),
            Identity(),
            CancellationToken.None);
        harness.Repository.ConcurrentUpdateFactory = candidate => candidate;

        var replay = await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            request,
            Identity(),
            CancellationToken.None);

        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending, replay.Status);
        Assert.True(replay.AlreadyExists);
    }

    [Fact]
    public async Task Prepare_CAS_loser_rejects_a_different_binding_or_changed_immutable_facts()
    {
        var bindingHarness = CreateHarness();
        var bindingCreate = CreateRequest(method: PaymentMethodKind.Cash);
        var bindingRequest = PrepareRequest(
            bindingCreate,
            provider: "cash",
            providerAttemptId: "attempt-binding-race");
        await bindingHarness.Service.CreateAsync(
            bindingHarness.InstallmentGuid,
            bindingCreate,
            Identity(),
            CancellationToken.None);
        bindingHarness.Repository.ConcurrentUpdateFactory = candidate =>
            candidate with { ProviderAttemptId = "different-winning-attempt" };

        var bindingMismatch = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            bindingHarness.Service.PrepareProviderAsync(
                bindingHarness.InstallmentGuid,
                bindingCreate.OperationGuid,
                bindingRequest,
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, bindingMismatch.Code);

        var factsHarness = CreateHarness();
        var factsCreate = CreateRequest(method: PaymentMethodKind.Cash);
        var factsRequest = PrepareRequest(factsCreate, provider: "cash", providerAttemptId: "attempt-facts-race");
        await factsHarness.Service.CreateAsync(
            factsHarness.InstallmentGuid,
            factsCreate,
            Identity(),
            CancellationToken.None);
        factsHarness.Repository.ConcurrentUpdateFactory = candidate =>
            candidate with { Fingerprint = new string('F', 64) };

        var factsMismatch = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            factsHarness.Service.PrepareProviderAsync(
                factsHarness.InstallmentGuid,
                factsCreate.OperationGuid,
                factsRequest,
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, factsMismatch.Code);
    }

    [Fact]
    public async Task Permanent_insert_fact_conflicts_are_mismatch_while_only_a_live_other_operation_is_busy()
    {
        var paymentHarness = CreateHarness();
        var paymentOwner = CreateRequest(method: PaymentMethodKind.Cash);
        await paymentHarness.Service.CreateAsync(
            paymentHarness.InstallmentGuid,
            paymentOwner,
            Identity(),
            CancellationToken.None);
        await paymentHarness.Service.ResolveAsync(
            paymentHarness.InstallmentGuid,
            paymentOwner.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Released),
            Identity(),
            CancellationToken.None);
        var paymentConflict = CreateRequest(method: PaymentMethodKind.Cash) with
        {
            PaymentGuid = paymentOwner.PaymentGuid
        };
        var paymentException = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            paymentHarness.Service.PrepareProviderAsync(
                paymentHarness.InstallmentGuid,
                paymentConflict.OperationGuid,
                PrepareRequest(paymentConflict, "cash", "attempt-payment-conflict"),
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, paymentException.Code);

        var idempotencyHarness = CreateHarness();
        var idempotencyOwner = CreateRequest(method: PaymentMethodKind.Cash);
        await idempotencyHarness.Service.CreateAsync(
            idempotencyHarness.InstallmentGuid,
            idempotencyOwner,
            Identity(),
            CancellationToken.None);
        await idempotencyHarness.Service.ResolveAsync(
            idempotencyHarness.InstallmentGuid,
            idempotencyOwner.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Released),
            Identity(),
            CancellationToken.None);
        var idempotencyConflict = CreateRequest(method: PaymentMethodKind.Cash) with
        {
            IdempotencyKey = idempotencyOwner.IdempotencyKey
        };
        var idempotencyException = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            idempotencyHarness.Service.CreateAsync(
                idempotencyHarness.InstallmentGuid,
                idempotencyConflict,
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, idempotencyException.Code);

        var providerHarness = CreateHarness();
        var providerOwner = CreateRequest(method: PaymentMethodKind.Cash);
        var providerOwnerRequest = PrepareRequest(providerOwner, "cash", "attempt-provider-conflict");
        await providerHarness.Service.PrepareProviderAsync(
            providerHarness.InstallmentGuid,
            providerOwner.OperationGuid,
            providerOwnerRequest,
            Identity(),
            CancellationToken.None);
        await providerHarness.Service.ResolveAsync(
            providerHarness.InstallmentGuid,
            providerOwner.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Declined),
            Identity(),
            CancellationToken.None);
        var providerConflict = CreateRequest(method: PaymentMethodKind.Cash);
        var providerException = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            providerHarness.Service.PrepareProviderAsync(
                providerHarness.InstallmentGuid,
                providerConflict.OperationGuid,
                PrepareRequest(providerConflict, "cash", providerOwnerRequest.ProviderAttemptId),
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, providerException.Code);

        var busyHarness = CreateHarness();
        var blocking = CreateRequest(method: PaymentMethodKind.Cash);
        await busyHarness.Service.PrepareProviderAsync(
            busyHarness.InstallmentGuid,
            blocking.OperationGuid,
            PrepareRequest(blocking, "cash", "attempt-blocking"),
            Identity(),
            CancellationToken.None);
        var other = CreateRequest(method: PaymentMethodKind.Cash);
        var busyException = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            busyHarness.Service.PrepareProviderAsync(
                busyHarness.InstallmentGuid,
                other.OperationGuid,
                PrepareRequest(other, "cash", "attempt-other-operation"),
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Busy, busyException.Code);
    }

    [Fact]
    public async Task Insert_failure_without_a_permanent_conflict_or_live_operation_is_mismatch()
    {
        var harness = CreateHarness();
        harness.Repository.FailNextInsert = true;
        var create = CreateRequest(method: PaymentMethodKind.Cash);

        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.PrepareProviderAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                PrepareRequest(create, "cash", "attempt-stale-snapshot"),
                Identity(),
                CancellationToken.None));

        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, exception.Code);
    }

    [Fact]
    public async Task Create_is_idempotent_only_for_the_same_identity_and_immutable_fingerprint()
    {
        var harness = CreateHarness();
        var request = CreateRequest();

        var first = await harness.Service.CreateAsync(
            harness.InstallmentGuid,
            request,
            Identity(),
            CancellationToken.None);
        var replay = await harness.Service.CreateAsync(
            harness.InstallmentGuid,
            request,
            Identity(),
            CancellationToken.None);

        Assert.Equal(InstallmentRepaymentClaimStatus.Prepared, first.Status);
        Assert.Equal(InitialNow.AddSeconds(120), first.ExpiresAtUtc);
        Assert.True(replay.AlreadyExists);

        var mismatch = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.CreateAsync(
                harness.InstallmentGuid,
                request with { Amount = request.Amount + 1m },
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, mismatch.Code);
    }

    [Fact]
    public async Task Card_create_fails_closed_before_claim_order_or_payment_state_changes()
    {
        var harness = CreateHarness();
        var before = await harness.Installments.GetDetailsAsync(
            harness.InstallmentGuid,
            CancellationToken.None);
        var request = CreateRequest(amount: 10m, method: PaymentMethodKind.Card);

        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.CreateAsync(
                harness.InstallmentGuid,
                request,
                Identity(),
                CancellationToken.None));

        Assert.Equal(
            InstallmentRepaymentClaimErrorCodes.PaymentMethodUnsupported,
            exception.Code);
        Assert.Null(await harness.Repository.GetAsync(request.OperationGuid, CancellationToken.None));
        Assert.Null(await harness.Repository.GetBlockingAsync(harness.InstallmentGuid, CancellationToken.None));
        Assert.Equal(0, harness.Installments.AppendCount);
        var after = await harness.Installments.GetDetailsAsync(
            harness.InstallmentGuid,
            CancellationToken.None);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Prepared_expires_after_120_seconds_but_provider_pending_and_unknown_never_auto_expire()
    {
        var harness = CreateHarness();
        var first = CreateRequest();
        await harness.Service.CreateAsync(harness.InstallmentGuid, first, Identity(), CancellationToken.None);

        harness.Time.UtcNow = InitialNow.AddSeconds(121);
        var second = CreateRequest();
        await harness.Service.CreateAsync(harness.InstallmentGuid, second, Identity(), CancellationToken.None);
        var released = await harness.Repository.GetAsync(first.OperationGuid, CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Released, released?.Status);

        await harness.Service.BeginProviderAsync(
            harness.InstallmentGuid,
            second.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest("cash", "attempt-1"),
            Identity(),
            CancellationToken.None);
        harness.Time.UtcNow = InitialNow.AddHours(12);

        var pendingBusy = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.CreateAsync(harness.InstallmentGuid, CreateRequest(), Identity(), CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Busy, pendingBusy.Code);

        await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            second.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Unknown),
            Identity(),
            CancellationToken.None);
        harness.Time.UtcNow = InitialNow.AddDays(30);

        var unknownBusy = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.CreateAsync(harness.InstallmentGuid, CreateRequest(), Identity(), CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Busy, unknownBusy.Code);
    }

    [Fact]
    public async Task Resolve_only_releases_prepared_and_only_declines_or_marks_unknown_after_provider_begin()
    {
        var harness = CreateHarness();
        var prepared = CreateRequest();
        await harness.Service.CreateAsync(harness.InstallmentGuid, prepared, Identity(), CancellationToken.None);

        var released = await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            prepared.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Released),
            Identity(),
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Released, released.Status);
        var releasedReplay = await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            prepared.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Released),
            Identity(),
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Released, releasedReplay.Status);
        Assert.True(releasedReplay.AlreadyExists);

        var pending = CreateRequest(method: PaymentMethodKind.Cash);
        await harness.Service.CreateAsync(harness.InstallmentGuid, pending, Identity(), CancellationToken.None);
        await harness.Service.BeginProviderAsync(
            harness.InstallmentGuid,
            pending.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest("cash", "cash-attempt-1"),
            Identity(),
            CancellationToken.None);

        var invalidRelease = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.ResolveAsync(
                harness.InstallmentGuid,
                pending.OperationGuid,
                new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Released),
                Identity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, invalidRelease.Code);

        var declined = await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            pending.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Declined),
            Identity(),
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Declined, declined.Status);
    }

    [Theory]
    [InlineData(InstallmentRepaymentClaimStatus.ProviderPending)]
    [InlineData(InstallmentRepaymentClaimStatus.Unknown)]
    public async Task Cash_provider_claim_can_be_explicitly_released_only_after_confirming_cash_was_not_collected(
        InstallmentRepaymentClaimStatus startingStatus)
    {
        var harness = CreateHarness();
        var create = CreateRequest(method: PaymentMethodKind.Cash);
        const string attemptId = "cash-attempt-release";
        await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            PrepareRequest(create, "cash", attemptId),
            Identity(),
            CancellationToken.None);
        if (startingStatus == InstallmentRepaymentClaimStatus.Unknown)
        {
            await harness.Service.ResolveAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Unknown),
                Identity(permissionCodes: []),
                CancellationToken.None);
        }

        var releaseRequest = new InstallmentRepaymentClaimResolveRequest(
            InstallmentRepaymentClaimResolveOutcome.Released,
            CashNotCollectedConfirmed: true,
            ProviderAttemptId: attemptId);
        var released = await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            releaseRequest,
            CashReleaseIdentity(),
            CancellationToken.None);
        var exactReplay = await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            releaseRequest,
            CashReleaseIdentity(),
            CancellationToken.None);

        Assert.Equal(InstallmentRepaymentClaimStatus.Released, released.Status);
        Assert.Equal(InstallmentRepaymentClaimStatus.Released, exactReplay.Status);
        Assert.True(exactReplay.AlreadyExists);

        var mismatchedReplay = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.ResolveAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                releaseRequest with { ProviderAttemptId = "another-attempt" },
                CashReleaseIdentity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, mismatchedReplay.Code);
    }

    [Theory]
    [InlineData(false, "cash-attempt-evidence", InstallmentRepaymentClaimErrorCodes.Mismatch)]
    [InlineData(true, null, InstallmentRepaymentClaimErrorCodes.Invalid)]
    [InlineData(true, "wrong-attempt", InstallmentRepaymentClaimErrorCodes.Mismatch)]
    public async Task Cash_provider_release_rejects_missing_or_mismatched_evidence(
        bool cashNotCollectedConfirmed,
        string? providerAttemptId,
        string expectedErrorCode)
    {
        var harness = CreateHarness();
        var create = CreateRequest(method: PaymentMethodKind.Cash);
        const string attemptId = "cash-attempt-evidence";
        await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            PrepareRequest(create, "cash", attemptId),
            Identity(),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.ResolveAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                new InstallmentRepaymentClaimResolveRequest(
                    InstallmentRepaymentClaimResolveOutcome.Released,
                    cashNotCollectedConfirmed,
                    providerAttemptId),
                CashReleaseIdentity(),
                CancellationToken.None));

        Assert.Equal(expectedErrorCode, exception.Code);
        var stored = await harness.Repository.GetAsync(create.OperationGuid, CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending, stored?.Status);
    }

    [Fact]
    public async Task Cash_provider_release_requires_cancel_permission_on_every_attempt()
    {
        var harness = CreateHarness();
        var create = CreateRequest(method: PaymentMethodKind.Cash);
        const string attemptId = "cash-attempt-permission";
        var releaseRequest = new InstallmentRepaymentClaimResolveRequest(
            InstallmentRepaymentClaimResolveOutcome.Released,
            CashNotCollectedConfirmed: true,
            ProviderAttemptId: attemptId);
        await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            PrepareRequest(create, "cash", attemptId),
            Identity(),
            CancellationToken.None);

        var denied = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.ResolveAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                releaseRequest,
                Identity(permissionCodes: []),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.PermissionDenied, denied.Code);

        await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            releaseRequest,
            CashReleaseIdentity(),
            CancellationToken.None);
        var revokedReplay = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.ResolveAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                releaseRequest,
                Identity(permissionCodes: []),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.PermissionDenied, revokedReplay.Code);
    }

    [Fact]
    public async Task Explicit_cash_release_can_be_read_and_replayed_by_original_device_cancel_supervisor()
    {
        var harness = CreateHarness();
        var create = CreateRequest(method: PaymentMethodKind.Cash);
        const string attemptId = "cash-attempt-supervisor-read";
        var releaseRequest = new InstallmentRepaymentClaimResolveRequest(
            InstallmentRepaymentClaimResolveOutcome.Released,
            CashNotCollectedConfirmed: true,
            ProviderAttemptId: attemptId);
        await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            PrepareRequest(create, "cash", attemptId),
            Identity(),
            CancellationToken.None);
        await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            releaseRequest,
            CashReleaseIdentity(),
            CancellationToken.None);

        var recovered = await harness.Service.GetAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            CashReleaseIdentity(),
            CancellationToken.None);
        var exactReplay = await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            releaseRequest,
            CashReleaseIdentity(),
            CancellationToken.None);

        Assert.Equal(InstallmentRepaymentClaimStatus.Released, recovered.Status);
        Assert.True(recovered.AlreadyExists);
        Assert.Equal(InstallmentRepaymentClaimStatus.Released, exactReplay.Status);
        Assert.True(exactReplay.AlreadyExists);

        var crossDevice = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.GetAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                CashReleaseIdentity(deviceCode: "POS-99"),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, crossDevice.Code);
    }

    [Fact]
    public async Task Cancel_permission_does_not_expand_get_for_ordinary_or_non_cash_released_claims()
    {
        var ordinaryHarness = CreateHarness();
        var ordinaryCash = CreateRequest(method: PaymentMethodKind.Cash);
        await ordinaryHarness.Service.CreateAsync(
            ordinaryHarness.InstallmentGuid,
            ordinaryCash,
            Identity(),
            CancellationToken.None);
        await ordinaryHarness.Service.ResolveAsync(
            ordinaryHarness.InstallmentGuid,
            ordinaryCash.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Released),
            Identity(),
            CancellationToken.None);

        var ordinaryDenied = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            ordinaryHarness.Service.GetAsync(
                ordinaryHarness.InstallmentGuid,
                ordinaryCash.OperationGuid,
                CashReleaseIdentity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.PermissionDenied, ordinaryDenied.Code);

        var voucherHarness = CreateHarness();
        var voucher = CreateRequest(method: PaymentMethodKind.Voucher);
        await voucherHarness.Service.PrepareProviderAsync(
            voucherHarness.InstallmentGuid,
            voucher.OperationGuid,
            PrepareRequest(voucher, "voucher", "voucher-attempt-released"),
            Identity(),
            CancellationToken.None);
        var providerBoundVoucher = await voucherHarness.Repository.GetAsync(
            voucher.OperationGuid,
            CancellationToken.None);
        Assert.NotNull(providerBoundVoucher);
        Assert.True(await voucherHarness.Repository.TryUpdateAsync(
            providerBoundVoucher! with
            {
                Status = InstallmentRepaymentClaimStatus.Released,
                Revision = providerBoundVoucher.Revision + 1
            },
            providerBoundVoucher.Revision,
            CancellationToken.None));

        var nonCashDenied = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            voucherHarness.Service.GetAsync(
                voucherHarness.InstallmentGuid,
                voucher.OperationGuid,
                CashReleaseIdentity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.PermissionDenied, nonCashDenied.Code);
    }

    [Fact]
    public async Task Cash_provider_release_rejects_cross_device_and_non_cash_claims()
    {
        var cashHarness = CreateHarness();
        var cash = CreateRequest(method: PaymentMethodKind.Cash);
        await cashHarness.Service.PrepareProviderAsync(
            cashHarness.InstallmentGuid,
            cash.OperationGuid,
            PrepareRequest(cash, "cash", "cash-attempt-device"),
            Identity(),
            CancellationToken.None);
        var crossDevice = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            cashHarness.Service.ResolveAsync(
                cashHarness.InstallmentGuid,
                cash.OperationGuid,
                new InstallmentRepaymentClaimResolveRequest(
                    InstallmentRepaymentClaimResolveOutcome.Released,
                    CashNotCollectedConfirmed: true,
                    ProviderAttemptId: "cash-attempt-device"),
                CashReleaseIdentity(deviceCode: "POS-99"),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, crossDevice.Code);

        var voucherHarness = CreateHarness();
        var voucher = CreateRequest(method: PaymentMethodKind.Voucher);
        await voucherHarness.Service.PrepareProviderAsync(
            voucherHarness.InstallmentGuid,
            voucher.OperationGuid,
            PrepareRequest(voucher, "voucher", "voucher-attempt"),
            Identity(),
            CancellationToken.None);
        var nonCash = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            voucherHarness.Service.ResolveAsync(
                voucherHarness.InstallmentGuid,
                voucher.OperationGuid,
                new InstallmentRepaymentClaimResolveRequest(
                    InstallmentRepaymentClaimResolveOutcome.Released,
                    CashNotCollectedConfirmed: true,
                    ProviderAttemptId: "voucher-attempt"),
                CashReleaseIdentity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, nonCash.Code);
    }

    [Theory]
    [InlineData(InstallmentRepaymentClaimStatus.Committed)]
    [InlineData(InstallmentRepaymentClaimStatus.Declined)]
    public async Task Cash_provider_release_rejects_terminal_payment_outcomes(
        InstallmentRepaymentClaimStatus terminalStatus)
    {
        var harness = CreateHarness();
        var create = CreateRequest(method: PaymentMethodKind.Cash);
        const string attemptId = "cash-attempt-terminal";
        await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            PrepareRequest(create, "cash", attemptId),
            Identity(),
            CancellationToken.None);
        if (terminalStatus == InstallmentRepaymentClaimStatus.Committed)
        {
            await harness.Service.CommitAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                new InstallmentRepaymentClaimCommitRequest(),
                Identity(),
                CancellationToken.None);
        }
        else
        {
            await harness.Service.ResolveAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Declined),
                Identity(),
                CancellationToken.None);
        }

        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.ResolveAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                new InstallmentRepaymentClaimResolveRequest(
                    InstallmentRepaymentClaimResolveOutcome.Released,
                    CashNotCollectedConfirmed: true,
                    ProviderAttemptId: attemptId),
                PaymentAndCancelIdentity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, exception.Code);
    }

    [Fact]
    public async Task Commit_is_idempotent_and_allows_only_configured_same_store_cross_device_recovery()
    {
        var denied = CreateHarness(crossDeviceEnabled: false, installmentDeviceCode: "POS-01");
        var deniedException = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            denied.Service.CreateAsync(
                denied.InstallmentGuid,
                CreateRequest(method: PaymentMethodKind.Cash),
                Identity(deviceCode: "POS-02"),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, deniedException.Code);

        var harness = CreateHarness(crossDeviceEnabled: true, installmentDeviceCode: "POS-01");
        var identity = Identity(deviceCode: "POS-02");
        var request = CreateRequest(amount: 10m, method: PaymentMethodKind.Cash);
        await harness.Service.CreateAsync(harness.InstallmentGuid, request, identity, CancellationToken.None);
        await harness.Service.BeginProviderAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest("cash", "attempt-cross-device"),
            identity,
            CancellationToken.None);

        var first = await harness.Service.CommitAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimCommitRequest(),
            identity,
            CancellationToken.None);
        var replay = await harness.Service.CommitAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimCommitRequest(),
            identity,
            CancellationToken.None);

        Assert.Equal(InstallmentRepaymentClaimStatus.Committed, first.Status);
        Assert.Equal(30m, first.Commit?.PaidAmount);
        Assert.Equal(InstallmentRepaymentClaimStatus.Committed, replay.Status);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(first.Commit),
            System.Text.Json.JsonSerializer.Serialize(replay.Commit));
        Assert.False(replay.Commit?.AlreadyRecorded);
        Assert.Equal(1, harness.Installments.AppendCount);

        var crossStore = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.GetAsync(
                harness.InstallmentGuid,
                request.OperationGuid,
                Identity(storeCode: "S02", deviceCode: "POS-02"),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, crossStore.Code);
    }

    [Fact]
    public async Task Existing_cross_device_claim_stays_bound_to_the_original_claimant_when_config_or_display_name_changes()
    {
        var harness = CreateHarness(crossDeviceEnabled: true, installmentDeviceCode: "POS-01");
        var claimant = Identity(deviceCode: "POS-02", cashierName: "Original Display Name");
        var request = CreateRequest();
        await harness.Service.CreateAsync(harness.InstallmentGuid, request, claimant, CancellationToken.None);

        // 配置只控制新建 claim；已建立的恢复链必须继续由原设备和稳定 CashierId 完成。
        var claimsNowOptional = new InstallmentRepaymentClaimService(
            harness.Repository,
            harness.Installments,
            harness.CommitRepository,
            Options.Create(new InstallmentRepaymentClaimOptions { CrossDeviceEnabled = false }),
            harness.Time);
        var renamedClaimant = claimant with { CashierName = "Renamed Cashier" };

        var recovered = await claimsNowOptional.GetAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            renamedClaimant,
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Prepared, recovered.Status);

        var otherDevice = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            claimsNowOptional.GetAsync(
                harness.InstallmentGuid,
                request.OperationGuid,
                renamedClaimant with { DeviceCode = "POS-03" },
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, otherDevice.Code);

        var otherCashier = await claimsNowOptional.GetAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            renamedClaimant with { CashierId = "C02", CashierUserGuid = "U02" },
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Prepared, otherCashier.Status);

        await claimsNowOptional.BeginProviderAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest("cash", "attempt-config-toggle"),
            renamedClaimant,
            CancellationToken.None);
    }

    [Fact]
    public async Task Unknown_requires_the_same_provider_attempt_to_resume_before_commit_or_decline()
    {
        var harness = CreateHarness();
        var identity = Identity();
        var request = CreateRequest();
        await harness.Service.CreateAsync(harness.InstallmentGuid, request, identity, CancellationToken.None);
        await harness.Service.BeginProviderAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest("cash", "attempt-unknown"),
            identity,
            CancellationToken.None);
        await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Unknown),
            identity,
            CancellationToken.None);

        var directCommit = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.CommitAsync(
                harness.InstallmentGuid,
                request.OperationGuid,
                new InstallmentRepaymentClaimCommitRequest(Reference: "APPROVED"),
                identity,
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, directCommit.Code);

        var directDecline = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.ResolveAsync(
                harness.InstallmentGuid,
                request.OperationGuid,
                new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Declined),
                identity,
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, directDecline.Code);

        var wrongAttempt = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.BeginProviderAsync(
                harness.InstallmentGuid,
                request.OperationGuid,
                new InstallmentRepaymentClaimBeginProviderRequest("cash", "different-attempt"),
                identity,
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, wrongAttempt.Code);

        var resumed = await harness.Service.BeginProviderAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest("cash", "attempt-unknown"),
            identity,
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending, resumed.Status);

        var committed = await harness.Service.CommitAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimCommitRequest(),
            identity,
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Committed, committed.Status);
    }

    [Fact]
    public async Task Cash_claim_amount_cannot_exceed_balance_and_is_not_treated_as_tendered_amount()
    {
        var harness = CreateHarness();

        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.CreateAsync(
                harness.InstallmentGuid,
                CreateRequest(amount: 80.01m, method: PaymentMethodKind.Cash),
                Identity(),
                CancellationToken.None));

        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, exception.Code);
        Assert.Null(await harness.Repository.GetBlockingAsync(harness.InstallmentGuid, CancellationToken.None));
    }

    [Theory]
    [InlineData(PaymentMethodKind.Cash, "card")]
    [InlineData(PaymentMethodKind.Cash, "voucher")]
    [InlineData(PaymentMethodKind.Voucher, "cash")]
    public async Task Begin_rejects_provider_that_does_not_match_the_claim_method_before_provider_side_effects(
        PaymentMethodKind method,
        string provider)
    {
        var harness = CreateHarness();
        var request = CreateRequest(method: method);
        await harness.Service.CreateAsync(harness.InstallmentGuid, request, Identity(), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.BeginProviderAsync(
                harness.InstallmentGuid,
                request.OperationGuid,
                new InstallmentRepaymentClaimBeginProviderRequest(provider, "attempt-1"),
                Identity(),
                CancellationToken.None));

        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Invalid, exception.Code);
        Assert.Equal(0, harness.Installments.AppendCount);
        var stored = await harness.Repository.GetAsync(request.OperationGuid, CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Prepared, stored?.Status);
        Assert.Null(stored?.Provider);
    }

    [Fact]
    public async Task Legacy_card_claim_cannot_begin_a_provider_attempt()
    {
        var harness = CreateHarness();
        var claim = await SeedLegacyCardClaimAsync(harness, InstallmentRepaymentClaimStatus.Prepared);

        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.BeginProviderAsync(
                harness.InstallmentGuid,
                claim.OperationGuid,
                new InstallmentRepaymentClaimBeginProviderRequest("linkly", "attempt-card"),
                Identity(),
                CancellationToken.None));

        Assert.Equal(InstallmentRepaymentClaimErrorCodes.PaymentMethodUnsupported, exception.Code);
        var stored = await harness.Repository.GetAsync(claim.OperationGuid, CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Prepared, stored?.Status);
        Assert.Null(stored?.Provider);
        Assert.Equal(0, harness.Installments.AppendCount);
    }

    [Fact]
    public async Task Provider_pending_card_claim_commits_client_evidence_even_after_card_repayment_is_disabled()
    {
        // 开关只管新开始的刷卡续付：已登记通道（可能已扣款）的 claim 关掉开关后也必须能入账，否则会永久挡住该分期。
        var harness = CreateHarness();
        var claim = await SeedLegacyCardClaimAsync(harness, InstallmentRepaymentClaimStatus.ProviderPending);

        var committed = await harness.Service.CommitAsync(
            harness.InstallmentGuid,
            claim.OperationGuid,
            new InstallmentRepaymentClaimCommitRequest(
                Reference: "APPROVED",
                CardTransactions: [CardTransaction("ANZ", "TXN-LINKLY", claim.Amount)]),
            Identity(),
            CancellationToken.None);

        Assert.Equal(InstallmentRepaymentClaimStatus.Committed, committed.Status);
        Assert.Equal(1, harness.Installments.AppendCount);
        Assert.Equal(1, harness.Verifier.Calls);
    }

    [Fact]
    public async Task Supervisor_on_another_device_confirms_not_charged_and_unblocks_the_installment()
    {
        var harness = CreateHarness();
        var claim = await SeedLegacyCardClaimAsync(harness, InstallmentRepaymentClaimStatus.ProviderPending);
        var supervisor = SupervisorIdentity(deviceCode: "POS-09");

        var blocking = await harness.Service.GetBlockingForSupervisorAsync(harness.InstallmentGuid, supervisor, CancellationToken.None);
        var resolved = await harness.Service.SupervisorResolveAsync(
            harness.InstallmentGuid,
            claim.OperationGuid,
            new InstallmentRepaymentClaimSupervisorResolveRequest(
                InstallmentRepaymentSupervisorDecision.ConfirmedNotCharged,
                "Bank portal shows no charge"),
            supervisor,
            CancellationToken.None);

        Assert.Equal(claim.OperationGuid, blocking?.OperationGuid);
        Assert.Equal("POS-01", blocking?.ClaimantDeviceCode);
        Assert.Equal(InstallmentRepaymentClaimStatus.Declined, resolved.Status);
        Assert.Null(await harness.Repository.GetBlockingAsync(harness.InstallmentGuid, CancellationToken.None));
        Assert.Null(await harness.Service.GetBlockingForSupervisorAsync(harness.InstallmentGuid, supervisor, CancellationToken.None));
        Assert.Equal(0, harness.Installments.AppendCount);
        var stored = await harness.Repository.GetAsync(claim.OperationGuid, CancellationToken.None);
        Assert.Equal("SUP01", stored?.LastRecoveryCashierId);
        var audit = Assert.Single(harness.Verifier.SupervisorResolutions);
        Assert.Equal(InstallmentRepaymentSupervisorDecision.ConfirmedNotCharged, audit.Decision);
        Assert.Equal("Bank portal shows no charge", audit.Reason);
    }

    [Fact]
    public async Task Supervisor_confirms_an_unknown_card_repayment_as_charged_and_records_it_once()
    {
        var harness = CreateHarness();
        var claim = await SeedLegacyCardClaimAsync(harness, InstallmentRepaymentClaimStatus.ProviderPending);
        await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            claim.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Unknown),
            Identity(),
            CancellationToken.None);
        var request = new InstallmentRepaymentClaimSupervisorResolveRequest(
            InstallmentRepaymentSupervisorDecision.ConfirmedCharged,
            "Linkly portal shows approved",
            "RRN-123456");

        var first = await harness.Service.SupervisorResolveAsync(
            harness.InstallmentGuid,
            claim.OperationGuid,
            request,
            SupervisorIdentity(deviceCode: "POS-09"),
            CancellationToken.None);
        var replay = await harness.Service.SupervisorResolveAsync(
            harness.InstallmentGuid,
            claim.OperationGuid,
            request,
            SupervisorIdentity(deviceCode: "POS-09"),
            CancellationToken.None);

        Assert.Equal(InstallmentRepaymentClaimStatus.Committed, first.Status);
        Assert.NotNull(first.Commit);
        Assert.Equal(InstallmentRepaymentClaimStatus.Committed, replay.Status);
        Assert.True(replay.AlreadyExists);
        Assert.Equal(1, harness.Installments.AppendCount);
        var details = await harness.Installments.GetDetailsAsync(harness.InstallmentGuid, CancellationToken.None);
        var payment = Assert.Single(details!.Payments, item => item.PaymentGuid == claim.PaymentGuid);
        Assert.Equal("SUPERVISOR:RRN-123456", payment?.Reference);
        var evidence = Assert.Single(payment!.CardTransactions!);
        Assert.Equal("ANZ Supervisor", evidence.Processor);
        Assert.Equal(claim.Amount, evidence.Amount);
        Assert.Single(harness.Verifier.SupervisorResolutions);
    }

    [Fact]
    public async Task Supervisor_resolution_requires_the_installment_cancel_permission()
    {
        var harness = CreateHarness();
        var claim = await SeedLegacyCardClaimAsync(harness, InstallmentRepaymentClaimStatus.ProviderPending);

        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.SupervisorResolveAsync(
                harness.InstallmentGuid,
                claim.OperationGuid,
                new InstallmentRepaymentClaimSupervisorResolveRequest(InstallmentRepaymentSupervisorDecision.ConfirmedNotCharged, "no"),
                Identity(),
                CancellationToken.None));

        Assert.Equal(InstallmentRepaymentClaimErrorCodes.PermissionDenied, exception.Code);
        Assert.Equal(
            InstallmentRepaymentClaimStatus.ProviderPending,
            (await harness.Repository.GetAsync(claim.OperationGuid, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task Supervisor_cannot_resolve_another_store_or_a_claim_that_never_reached_the_terminal()
    {
        var harness = CreateHarness(cardRepaymentProviders: "linkly");
        var pending = await SeedLegacyCardClaimAsync(harness, InstallmentRepaymentClaimStatus.ProviderPending);

        var otherStore = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.SupervisorResolveAsync(
                harness.InstallmentGuid,
                pending.OperationGuid,
                new InstallmentRepaymentClaimSupervisorResolveRequest(InstallmentRepaymentSupervisorDecision.ConfirmedCharged, "x"),
                SupervisorIdentity(storeCode: "S02"),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, otherStore.Code);

        var missingReason = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.SupervisorResolveAsync(
                harness.InstallmentGuid,
                pending.OperationGuid,
                new InstallmentRepaymentClaimSupervisorResolveRequest(InstallmentRepaymentSupervisorDecision.ConfirmedCharged, " "),
                SupervisorIdentity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Invalid, missingReason.Code);

        var preparedHarness = CreateHarness(cardRepaymentProviders: "linkly");
        var preparedClaim = await SeedLegacyCardClaimAsync(preparedHarness, InstallmentRepaymentClaimStatus.Prepared);
        var notStarted = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            preparedHarness.Service.SupervisorResolveAsync(
                preparedHarness.InstallmentGuid,
                preparedClaim.OperationGuid,
                new InstallmentRepaymentClaimSupervisorResolveRequest(InstallmentRepaymentSupervisorDecision.ConfirmedNotCharged, "x"),
                SupervisorIdentity(),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, notStarted.Code);
    }

    private static InstallmentRepaymentClaimIdentity SupervisorIdentity(string storeCode = "S01", string deviceCode = "POS-01") =>
        Identity(
            storeCode: storeCode,
            deviceCode: deviceCode,
            cashierId: "SUP01",
            cashierName: "Supervisor",
            permissionCodes: [Permissions.PosTerminal.Installments.Cancel],
            cashierUserGuid: "U-SUP");

    [Fact]
    public async Task Capabilities_list_enabled_card_providers_in_fixed_order_and_ignore_unknown_entries()
    {
        var harness = CreateHarness(cardRepaymentProviders: " Zeller ; square, tyro,ZELLER");

        var capabilities = harness.Service.GetCapabilities();

        Assert.True(capabilities.CardRepaymentSupported);
        Assert.Equal(["square", "zeller"], capabilities.CardRepaymentProviders);
    }

    [Fact]
    public void Capabilities_keep_card_repayment_disabled_by_default()
    {
        var capabilities = CreateHarness().Service.GetCapabilities();

        Assert.False(capabilities.CardRepaymentSupported);
        Assert.NotNull(capabilities.CardRepaymentProviders);
        Assert.Empty(capabilities.CardRepaymentProviders!);
    }

    [Theory]
    [InlineData("square", "Square", "Square")]
    [InlineData("linkly", "Linkly", "ANZ")]
    [InlineData("linkly", "linkly-cloud", "ANZ")]
    [InlineData("zeller", "Zeller", "Zeller")]
    [InlineData("manual", "Manual", "Manual")]
    public async Task Enabled_card_provider_can_create_begin_and_commit_a_repayment(
        string enabledProvider,
        string claimProvider,
        string evidenceProcessor)
    {
        var harness = CreateHarness(cardRepaymentProviders: enabledProvider);
        var request = CreateRequest(amount: 10m, method: PaymentMethodKind.Card);

        var created = await harness.Service.CreateAsync(harness.InstallmentGuid, request, Identity(), CancellationToken.None);
        var begun = await harness.Service.BeginProviderAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest(claimProvider, "attempt-card"),
            Identity(),
            CancellationToken.None);
        var committed = await harness.Service.CommitAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimCommitRequest(
                Reference: "CARD-REF",
                CardTransactions: [CardTransaction(evidenceProcessor, "TXN-1", 10m)]),
            Identity(),
            CancellationToken.None);

        Assert.Equal(InstallmentRepaymentClaimStatus.Prepared, created.Status);
        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending, begun.Status);
        Assert.Equal(InstallmentRepaymentClaimStatus.Committed, committed.Status);
        Assert.Equal(1, harness.Installments.AppendCount);
        Assert.Equal(1, harness.Verifier.Calls);
    }

    [Fact]
    public async Task Begin_rejects_a_card_provider_that_is_not_enabled_and_keeps_the_claim_prepared()
    {
        var harness = CreateHarness(cardRepaymentProviders: "square");
        var request = CreateRequest(amount: 10m, method: PaymentMethodKind.Card);
        await harness.Service.CreateAsync(harness.InstallmentGuid, request, Identity(), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.BeginProviderAsync(
                harness.InstallmentGuid,
                request.OperationGuid,
                new InstallmentRepaymentClaimBeginProviderRequest("Linkly", "attempt-card"),
                Identity(),
                CancellationToken.None));

        Assert.Equal(InstallmentRepaymentClaimErrorCodes.PaymentMethodUnsupported, exception.Code);
        var stored = await harness.Repository.GetAsync(request.OperationGuid, CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Prepared, stored?.Status);
        Assert.Null(stored?.Provider);

        // 确定性拒绝后客户端释放 claim，分期不再被挡住。
        var released = await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Released),
            Identity(),
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Released, released.Status);
    }

    [Fact]
    public async Task Prepare_provider_rejects_a_card_provider_that_is_not_enabled_before_writing_a_claim()
    {
        var harness = CreateHarness(cardRepaymentProviders: "linkly");
        var create = CreateRequest(amount: 10m, method: PaymentMethodKind.Card);

        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.PrepareProviderAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                PrepareRequest(create, "square", "attempt-square"),
                Identity(),
                CancellationToken.None));

        Assert.Equal(InstallmentRepaymentClaimErrorCodes.PaymentMethodUnsupported, exception.Code);
        Assert.Null(await harness.Repository.GetAsync(create.OperationGuid, CancellationToken.None));
    }

    [Fact]
    public async Task Provider_pending_card_claim_replays_its_binding_after_card_repayment_is_disabled()
    {
        var harness = CreateHarness();
        var claim = await SeedLegacyCardClaimAsync(harness, InstallmentRepaymentClaimStatus.ProviderPending);

        var replay = await harness.Service.BeginProviderAsync(
            harness.InstallmentGuid,
            claim.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest("linkly", "legacy-attempt"),
            Identity(),
            CancellationToken.None);

        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending, replay.Status);
        Assert.True(replay.AlreadyExists);
    }

    public static TheoryData<string, InstallmentRepaymentClaimCommitRequest> InvalidCardEvidence => new()
    {
        { "no evidence", new InstallmentRepaymentClaimCommitRequest(Reference: "APPROVED") },
        {
            "amount mismatch",
            new InstallmentRepaymentClaimCommitRequest(
                Reference: "APPROVED",
                CardTransactions: [CardTransaction("ANZ", "TXN-1", 9.99m)])
        },
        {
            "processor of another provider",
            new InstallmentRepaymentClaimCommitRequest(
                Reference: "APPROVED",
                CardTransactions: [CardTransaction("Square", "TXN-1", 10m)])
        },
        {
            "voucher reservation",
            new InstallmentRepaymentClaimCommitRequest(
                Reference: "APPROVED",
                ReservationToken: "reservation-1",
                CardTransactions: [CardTransaction("ANZ", "TXN-1", 10m)])
        },
        {
            "reference too long",
            new InstallmentRepaymentClaimCommitRequest(
                Reference: new string('R', 201),
                CardTransactions: [CardTransaction("ANZ", "TXN-1", 10m)])
        },
    };

    [Theory]
    [MemberData(nameof(InvalidCardEvidence))]
    public async Task Card_commit_rejects_evidence_a_normal_terminal_flow_cannot_produce(
        string scenario,
        InstallmentRepaymentClaimCommitRequest request)
    {
        var harness = CreateHarness(cardRepaymentProviders: "linkly");
        var claim = await SeedLegacyCardClaimAsync(harness, InstallmentRepaymentClaimStatus.ProviderPending);

        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.CommitAsync(
                harness.InstallmentGuid,
                claim.OperationGuid,
                request,
                Identity(),
                CancellationToken.None));

        Assert.True(exception.Code == InstallmentRepaymentClaimErrorCodes.Invalid, scenario);
        Assert.Equal(0, harness.Installments.AppendCount);
        var stored = await harness.Repository.GetAsync(claim.OperationGuid, CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending, stored?.Status);
        Assert.Equal(0, harness.Verifier.Calls);
    }

    [Fact]
    public async Task Card_commit_accepts_split_evidence_and_unrecognized_processors_for_later_review()
    {
        var harness = CreateHarness(cardRepaymentProviders: "linkly");
        var claim = await SeedLegacyCardClaimAsync(harness, InstallmentRepaymentClaimStatus.ProviderPending);

        var committed = await harness.Service.CommitAsync(
            harness.InstallmentGuid,
            claim.OperationGuid,
            new InstallmentRepaymentClaimCommitRequest(
                Reference: "APPROVED",
                CardTransactions:
                [
                    CardTransaction("ANZ", "TXN-1", 4m),
                    CardTransaction("Offline Terminal", "OFFLINE-1", 6m, null, null)
                ]),
            Identity(),
            CancellationToken.None);

        Assert.Equal(InstallmentRepaymentClaimStatus.Committed, committed.Status);
        Assert.Equal(1, harness.Verifier.Calls);
    }

    [Theory]
    [InlineData(PaymentMethodKind.Cash)]
    [InlineData(PaymentMethodKind.Voucher)]
    public async Task Non_card_commit_rejects_card_transaction_evidence(PaymentMethodKind method)
    {
        var harness = CreateHarness();
        var request = CreateRequest(method: method);
        await harness.Service.CreateAsync(harness.InstallmentGuid, request, Identity(), CancellationToken.None);
        var provider = method == PaymentMethodKind.Cash ? "cash" : "voucher";
        await harness.Service.BeginProviderAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest(provider, "attempt-non-card"),
            Identity(),
            CancellationToken.None);

        var invalid = ValidCommitRequest(method) with
        {
            CardTransactions = [CardTransaction("Square", "TXN-1", request.Amount)]
        };
        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.CommitAsync(
                harness.InstallmentGuid,
                request.OperationGuid,
                invalid,
                Identity(),
                CancellationToken.None));

        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Invalid, exception.Code);
        Assert.Equal(0, harness.Installments.AppendCount);
    }

    [Fact]
    public async Task Create_and_begin_enforce_schema_lengths_at_the_boundary()
    {
        var maxStoreCode = new string('s', 50);
        var maxDeviceCode = new string('d', 50);
        var maxHarness = CreateHarness(
            installmentDeviceCode: maxDeviceCode,
            installmentStoreCode: maxStoreCode);
        var maxRequest = CreateRequest() with { IdempotencyKey = new string('i', 100) };
        await maxHarness.Service.CreateAsync(maxHarness.InstallmentGuid, maxRequest, Identity(
            storeCode: maxStoreCode,
            deviceCode: maxDeviceCode,
            cashierId: new string('c', 50),
            cashierName: new string('n', 100),
            cashierUserGuid: new string('u', 50)), CancellationToken.None);
        await maxHarness.Service.BeginProviderAsync(
            maxHarness.InstallmentGuid,
            maxRequest.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest("cash", new string('a', 128)),
            Identity(
                storeCode: maxStoreCode,
                deviceCode: maxDeviceCode,
                cashierId: new string('c', 50),
                cashierName: new string('n', 100),
                cashierUserGuid: new string('u', 50)),
            CancellationToken.None);

        var cases = new (InstallmentRepaymentClaimCreateRequest Request, InstallmentRepaymentClaimIdentity Identity)[]
        {
            (CreateRequest() with { IdempotencyKey = new string('i', 101) }, Identity()),
            (CreateRequest(), Identity(storeCode: new string('s', 51))),
            (CreateRequest(), Identity(deviceCode: new string('d', 51))),
            (CreateRequest(), Identity(cashierId: new string('c', 51))),
            (CreateRequest(), Identity(cashierName: new string('n', 101))),
            (CreateRequest(), Identity(cashierUserGuid: new string('u', 51))),
        };
        foreach (var item in cases)
        {
            var harness = CreateHarness();
            var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
                harness.Service.CreateAsync(
                    harness.InstallmentGuid,
                    item.Request,
                    item.Identity,
                    CancellationToken.None));
            Assert.Equal(InstallmentRepaymentClaimErrorCodes.Invalid, exception.Code);
            Assert.Null(await harness.Repository.GetBlockingAsync(harness.InstallmentGuid, CancellationToken.None));
        }

        foreach (var begin in new[]
        {
            new InstallmentRepaymentClaimBeginProviderRequest(new string('p', 32), "attempt"),
            new InstallmentRepaymentClaimBeginProviderRequest(new string('p', 33), "attempt"),
            new InstallmentRepaymentClaimBeginProviderRequest("cash", new string('a', 129)),
        })
        {
            var harness = CreateHarness();
            var request = CreateRequest(method: PaymentMethodKind.Cash);
            await harness.Service.CreateAsync(harness.InstallmentGuid, request, Identity(), CancellationToken.None);
            var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
                harness.Service.BeginProviderAsync(
                    harness.InstallmentGuid,
                    request.OperationGuid,
                    begin,
                    Identity(),
                    CancellationToken.None));
            Assert.Equal(InstallmentRepaymentClaimErrorCodes.Invalid, exception.Code);
        }
    }

    [Fact]
    public async Task Every_mutation_requires_confirm_and_the_method_specific_permission()
    {
        foreach (var method in new[] { PaymentMethodKind.Cash, PaymentMethodKind.Voucher })
        {
            var requiredMethodPermission = method switch
            {
                PaymentMethodKind.Cash => Permissions.PosTerminal.Payment.TakeCash,
                PaymentMethodKind.Card => Permissions.PosTerminal.Payment.TakeCard,
                PaymentMethodKind.Voucher => Permissions.PosTerminal.Payment.TakeVoucher,
                _ => throw new ArgumentOutOfRangeException(nameof(method))
            };
            foreach (var missing in new[]
            {
                requiredMethodPermission,
                Permissions.PosTerminal.Payment.Confirm,
                Permissions.PosTerminal.Installments.AddRepayment,
            })
            {
                var permissions = DefaultPaymentPermissions().Where(code => code != missing).ToArray();
                var harness = CreateHarness();
                var request = CreateRequest(method: method);
                var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
                    harness.Service.CreateAsync(
                        harness.InstallmentGuid,
                        request,
                        Identity(permissionCodes: permissions),
                        CancellationToken.None));
                Assert.Equal(InstallmentRepaymentClaimErrorCodes.PermissionDenied, exception.Code);
                Assert.Null(await harness.Repository.GetBlockingAsync(harness.InstallmentGuid, CancellationToken.None));
            }
        }
    }

    [Fact]
    public async Task Original_device_current_authorized_cashier_can_resume_while_payment_audit_stays_with_creator()
    {
        var harness = CreateHarness();
        var creator = Identity(cashierId: "CREATOR", cashierName: "Creator");
        var recoveryCashier = Identity(
            cashierId: "RECOVERY",
            cashierName: "Recovery Cashier",
            cashierUserGuid: "USER-RECOVERY");
        var request = CreateRequest(method: PaymentMethodKind.Cash);
        await harness.Service.CreateAsync(harness.InstallmentGuid, request, creator, CancellationToken.None);
        await harness.Service.BeginProviderAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest("cash", "cash-attempt"),
            creator,
            CancellationToken.None);
        await harness.Service.BeginProviderAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest("cash", "cash-attempt"),
            recoveryCashier,
            CancellationToken.None);

        var committed = await harness.Service.CommitAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimCommitRequest(),
            recoveryCashier,
            CancellationToken.None);

        Assert.Equal(InstallmentRepaymentClaimStatus.Committed, committed.Status);
        var payment = Assert.Single(committed.Commit!.Details.Payments);
        Assert.Equal("CREATOR", payment.CashierId);
        Assert.Equal("Creator", payment.CashierName);
        var storedClaim = await harness.Repository.GetAsync(request.OperationGuid, CancellationToken.None);
        Assert.Equal("RECOVERY", storedClaim?.LastRecoveryCashierId);
        Assert.Equal("Recovery Cashier", storedClaim?.LastRecoveryCashierName);
        Assert.Equal("USER-RECOVERY", storedClaim?.LastRecoveryCashierUserGuid);
        Assert.Equal(InitialNow, storedClaim?.RecoveredAtUtc);
    }

    [Fact]
    public async Task Different_cashier_cannot_begin_a_prepared_claim_but_can_release_it()
    {
        var harness = CreateHarness();
        var creator = Identity(cashierId: "CREATOR", cashierName: "Creator");
        var other = Identity(cashierId: "OTHER", cashierName: "Other", cashierUserGuid: "USER-OTHER");
        var request = CreateRequest(method: PaymentMethodKind.Cash);
        await harness.Service.CreateAsync(harness.InstallmentGuid, request, creator, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.BeginProviderAsync(
                harness.InstallmentGuid,
                request.OperationGuid,
                new InstallmentRepaymentClaimBeginProviderRequest("cash", "attempt-other"),
                other,
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, exception.Code);

        var released = await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Released),
            other,
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Released, released.Status);
    }

    [Theory]
    [InlineData(PaymentMethodKind.Cash)]
    [InlineData(PaymentMethodKind.Voucher)]
    public async Task Begin_rechecks_the_current_cashier_permissions(PaymentMethodKind method)
    {
        var requiredMethodPermission = method switch
        {
            PaymentMethodKind.Cash => Permissions.PosTerminal.Payment.TakeCash,
            PaymentMethodKind.Card => Permissions.PosTerminal.Payment.TakeCard,
            PaymentMethodKind.Voucher => Permissions.PosTerminal.Payment.TakeVoucher,
            _ => throw new ArgumentOutOfRangeException(nameof(method))
        };
        var provider = method switch
        {
            PaymentMethodKind.Cash => "cash",
            PaymentMethodKind.Card => "square",
            PaymentMethodKind.Voucher => "voucher",
            _ => throw new ArgumentOutOfRangeException(nameof(method))
        };
        var harness = CreateHarness();
        var request = CreateRequest(method: method);
        await harness.Service.CreateAsync(harness.InstallmentGuid, request, Identity(), CancellationToken.None);

        var beginDenied = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.BeginProviderAsync(
                harness.InstallmentGuid,
                request.OperationGuid,
                new InstallmentRepaymentClaimBeginProviderRequest(provider, "attempt-permission"),
                Identity(permissionCodes: DefaultPaymentPermissions().Where(code => code != requiredMethodPermission).ToArray()),
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.PermissionDenied, beginDenied.Code);

        await harness.Service.BeginProviderAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest(provider, "attempt-permission"),
            Identity(),
            CancellationToken.None);
    }

    [Fact]
    public async Task Provider_pending_and_committed_continuation_survive_permission_revocation_on_the_original_device()
    {
        var harness = CreateHarness();
        var request = CreateRequest(method: PaymentMethodKind.Cash);
        await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            PrepareRequest(request, "cash", "attempt-revoked-continuation"),
            Identity(),
            CancellationToken.None);
        var revoked = Identity(permissionCodes: []);

        var pending = await harness.Service.GetAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            revoked,
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending, pending.Status);

        var committed = await harness.Service.CommitAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimCommitRequest(),
            revoked,
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Committed, committed.Status);

        var replay = await harness.Service.CommitAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimCommitRequest(),
            revoked,
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Committed, replay.Status);
        Assert.True(replay.AlreadyExists);
        Assert.Equal(1, harness.Installments.AppendCount);
    }

    [Fact]
    public async Task Provider_pending_resolve_and_unknown_get_survive_revocation_but_cross_device_still_fails()
    {
        var harness = CreateHarness();
        var request = CreateRequest(method: PaymentMethodKind.Cash);
        await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            PrepareRequest(request, "cash", "attempt-revoked-resolve"),
            Identity(),
            CancellationToken.None);
        var revoked = Identity(permissionCodes: []);

        var unknown = await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Unknown),
            revoked,
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Unknown, unknown.Status);

        var recovered = await harness.Service.GetAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            revoked,
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Unknown, recovered.Status);

        var crossDevice = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.GetAsync(
                harness.InstallmentGuid,
                request.OperationGuid,
                revoked with { DeviceCode = "POS-99" },
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, crossDevice.Code);
    }

    [Fact]
    public async Task Revoked_permissions_reject_new_create_prepare_and_begin_before_or_after_binding()
    {
        var revoked = Identity(permissionCodes: []);
        var createHarness = CreateHarness();
        var create = CreateRequest(method: PaymentMethodKind.Cash);
        var createDenied = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            createHarness.Service.CreateAsync(
                createHarness.InstallmentGuid,
                create,
                revoked,
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.PermissionDenied, createDenied.Code);

        var prepareHarness = CreateHarness();
        var prepare = CreateRequest(method: PaymentMethodKind.Cash);
        var prepareRequest = PrepareRequest(prepare, "cash", "attempt-permission-revoked");
        var prepareDenied = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            prepareHarness.Service.PrepareProviderAsync(
                prepareHarness.InstallmentGuid,
                prepare.OperationGuid,
                prepareRequest,
                revoked,
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.PermissionDenied, prepareDenied.Code);

        await prepareHarness.Service.CreateAsync(
            prepareHarness.InstallmentGuid,
            prepare,
            Identity(),
            CancellationToken.None);
        var beginPreparedDenied = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            prepareHarness.Service.BeginProviderAsync(
                prepareHarness.InstallmentGuid,
                prepare.OperationGuid,
                new InstallmentRepaymentClaimBeginProviderRequest("cash", prepareRequest.ProviderAttemptId),
                revoked,
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.PermissionDenied, beginPreparedDenied.Code);
        var preparePreparedDenied = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            prepareHarness.Service.PrepareProviderAsync(
                prepareHarness.InstallmentGuid,
                prepare.OperationGuid,
                prepareRequest,
                revoked,
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.PermissionDenied, preparePreparedDenied.Code);

        await prepareHarness.Service.BeginProviderAsync(
            prepareHarness.InstallmentGuid,
            prepare.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest("cash", prepareRequest.ProviderAttemptId),
            Identity(),
            CancellationToken.None);
        var beginPendingDenied = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            prepareHarness.Service.BeginProviderAsync(
                prepareHarness.InstallmentGuid,
                prepare.OperationGuid,
                new InstallmentRepaymentClaimBeginProviderRequest("cash", prepareRequest.ProviderAttemptId),
                revoked,
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.PermissionDenied, beginPendingDenied.Code);
    }

    [Fact]
    public async Task Locked_provider_prepare_replays_survive_revocation_but_still_require_exact_facts_and_device()
    {
        var harness = CreateHarness();
        var create = CreateRequest(method: PaymentMethodKind.Cash);
        var request = PrepareRequest(create, "cash", "attempt-revoked-prepare-replay");
        await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            request,
            Identity(),
            CancellationToken.None);
        var revoked = Identity(permissionCodes: []);

        var pendingReplay = await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            request,
            revoked,
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending, pendingReplay.Status);
        Assert.True(pendingReplay.AlreadyExists);

        var factsMismatch = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.PrepareProviderAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                request with { Amount = request.Amount + 1m },
                revoked,
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, factsMismatch.Code);

        var crossDevice = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            harness.Service.PrepareProviderAsync(
                harness.InstallmentGuid,
                create.OperationGuid,
                request,
                revoked with { DeviceCode = "POS-99" },
                CancellationToken.None));
        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Mismatch, crossDevice.Code);

        await harness.Service.ResolveAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Unknown),
            revoked,
            CancellationToken.None);
        var unknownReplay = await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            request,
            revoked,
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.ProviderPending, unknownReplay.Status);

        var committed = await harness.Service.CommitAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            new InstallmentRepaymentClaimCommitRequest(),
            revoked,
            CancellationToken.None);
        var committedReplay = await harness.Service.PrepareProviderAsync(
            harness.InstallmentGuid,
            create.OperationGuid,
            request,
            revoked,
            CancellationToken.None);
        Assert.Equal(InstallmentRepaymentClaimStatus.Committed, committedReplay.Status);
        Assert.True(committedReplay.AlreadyExists);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(committed.Commit),
            System.Text.Json.JsonSerializer.Serialize(committedReplay.Commit));
    }

    [Fact]
    public async Task Committed_service_replay_keeps_the_original_response_after_pickup()
    {
        var harness = CreateHarness();
        var request = CreateRequest(method: PaymentMethodKind.Cash);
        await harness.Service.CreateAsync(harness.InstallmentGuid, request, Identity(), CancellationToken.None);
        await harness.Service.BeginProviderAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimBeginProviderRequest("cash", "cash-replay"),
            Identity(),
            CancellationToken.None);
        var committed = await harness.Service.CommitAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimCommitRequest(),
            Identity(),
            CancellationToken.None);
        harness.Installments.ChangeStatus(InstallmentStatus.PickedUp);
        harness.Time.UtcNow = InitialNow.AddMinutes(10);
        var recoveryIdentity = Identity(
            cashierId: "RECOVERY",
            cashierName: "Recovery Cashier",
            cashierUserGuid: "USER-RECOVERY");

        var replay = await harness.Service.CommitAsync(
            harness.InstallmentGuid,
            request.OperationGuid,
            new InstallmentRepaymentClaimCommitRequest(),
            recoveryIdentity,
            CancellationToken.None);

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(committed.Commit),
            System.Text.Json.JsonSerializer.Serialize(replay.Commit));
        Assert.Equal(InstallmentStatus.Active, replay.Commit?.Status);
        var storedClaim = await harness.Repository.GetAsync(request.OperationGuid, CancellationToken.None);
        Assert.Equal("RECOVERY", storedClaim?.LastRecoveryCashierId);
        Assert.Equal("USER-RECOVERY", storedClaim?.LastRecoveryCashierUserGuid);
        Assert.Equal(InitialNow.AddMinutes(10), storedClaim?.RecoveredAtUtc);
    }

    private static Harness CreateHarness(
        bool crossDeviceEnabled = false,
        string installmentDeviceCode = "POS-01",
        string installmentStoreCode = "S01",
        string? cardRepaymentProviders = null)
    {
        var installmentGuid = Guid.NewGuid();
        var repository = new InMemoryClaimRepository();
        var installments = new FakeInstallmentRepository(CreateDetails(
            installmentGuid,
            installmentDeviceCode,
            installmentStoreCode));
        var time = new MutableFakeTimeProvider(InitialNow);
        var commitRepository = new InMemoryCommitRepository(repository, installments);
        var verifier = new CountingCardTenderVerifier();
        var service = new InstallmentRepaymentClaimService(
            repository,
            installments,
            commitRepository,
            Options.Create(new InstallmentRepaymentClaimOptions
            {
                CrossDeviceEnabled = crossDeviceEnabled,
                CardRepaymentProviders = cardRepaymentProviders
            }),
            time,
            cardTenderVerifier: verifier);
        return new Harness(installmentGuid, service, repository, installments, commitRepository, time, verifier);
    }

    private static InstallmentRepaymentClaimCreateRequest CreateRequest(
        decimal amount = 10m,
        PaymentMethodKind method = PaymentMethodKind.Cash)
    {
        return new InstallmentRepaymentClaimCreateRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            amount,
            method,
            $"action-{Guid.NewGuid():N}");
    }

    private static InstallmentRepaymentClaimPrepareProviderRequest PrepareRequest(
        InstallmentRepaymentClaimCreateRequest create,
        string provider,
        string providerAttemptId) => new(
        create.PaymentGuid,
        create.Amount,
        create.Method,
        create.IdempotencyKey,
        provider,
        providerAttemptId);

    private static async Task<InstallmentRepaymentClaimRecord> SeedLegacyCardClaimAsync(
        Harness harness,
        InstallmentRepaymentClaimStatus status)
    {
        var providerPending = status == InstallmentRepaymentClaimStatus.ProviderPending;
        var claim = new InstallmentRepaymentClaimRecord(
            harness.InstallmentGuid,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "S01",
            "POS-01",
            "C01",
            "Cashier One",
            10m,
            PaymentMethodKind.Card,
            $"legacy-card-{Guid.NewGuid():N}",
            new string('a', 64),
            status,
            providerPending ? "linkly" : null,
            providerPending ? "legacy-attempt" : null,
            InitialNow.AddMinutes(-1),
            InitialNow.AddMinutes(-1),
            providerPending ? null : InitialNow.AddMinutes(1),
            null,
            providerPending ? 2 : 1);
        var inserted = await harness.Repository.TryInsertAsync(
            claim,
            new InstallmentRepaymentClaimInsertSnapshot(
                InstallmentStatus.Active,
                PaidAmount: 20m,
                BalanceAmount: 80m),
            CancellationToken.None);
        Assert.True(inserted);
        return claim;
    }

    private static InstallmentRepaymentClaimIdentity Identity(
        string storeCode = "S01",
        string deviceCode = "POS-01",
        string cashierId = "C01",
        string cashierName = "Cashier One",
        IReadOnlyCollection<string>? permissionCodes = null,
        string cashierUserGuid = "U01") => new(
            storeCode,
            deviceCode,
            cashierId,
            cashierName,
            permissionCodes ?? DefaultPaymentPermissions(),
            cashierUserGuid);

    private static InstallmentRepaymentClaimIdentity CashReleaseIdentity(
        string deviceCode = "POS-01") =>
        Identity(
            deviceCode: deviceCode,
            permissionCodes: [Permissions.PosTerminal.Installments.Cancel]);

    private static InstallmentRepaymentClaimIdentity PaymentAndCancelIdentity() =>
        Identity(permissionCodes:
        [
            .. DefaultPaymentPermissions(),
            Permissions.PosTerminal.Installments.Cancel
        ]);

    private static string[] DefaultPaymentPermissions() =>
    [
        Permissions.PosTerminal.Payment.Confirm,
        Permissions.PosTerminal.Installments.AddRepayment,
        Permissions.PosTerminal.Payment.TakeCash,
        Permissions.PosTerminal.Payment.TakeCard,
        Permissions.PosTerminal.Payment.TakeVoucher,
    ];

    private static InstallmentRepaymentClaimCommitRequest ValidCommitRequest(
        PaymentMethodKind method) => method switch
        {
            PaymentMethodKind.Cash => new InstallmentRepaymentClaimCommitRequest(),
            PaymentMethodKind.Voucher => new InstallmentRepaymentClaimCommitRequest(
                Reference: "VOUCHER-1",
                ReservationToken: "reservation-1"),
            _ => throw new ArgumentOutOfRangeException(nameof(method))
        };

    private static CardTransactionDto CardTransaction(string processor, string? txnRef, decimal amount)
    {
        var square = string.Equals(processor, "square", StringComparison.OrdinalIgnoreCase);
        return CardTransaction(
            processor,
            txnRef,
            amount,
            square ? null : "00",
            square ? "COMPLETED" : "APPROVED");
    }

    private static CardTransactionDto CardTransaction(
        string processor,
        string? txnRef,
        decimal amount,
        string? responseCode,
        string? responseText) => new(
        processor,
        txnRef,
        AuthCode: "AUTH",
        CardType: "VISA",
        CardBin: 412345,
        MaskedCardNumber: "****1234",
        MerchantId: "MERCHANT",
        ResponseCode: responseCode,
        ResponseText: responseText,
        Stan: "123456",
        BankDateTime: InitialNow,
        amount,
        ReceiptText: "approved");

    private static InstallmentDetailsDto CreateDetails(
        Guid installmentGuid,
        string deviceCode,
        string storeCode)
    {
        return new InstallmentDetailsDto(
            installmentGuid,
            "INS-001",
            storeCode,
            deviceCode,
            "C01",
            "Cashier One",
            "Customer One",
            "0400000000",
            InitialNow.AddDays(-1),
            100m,
            20m,
            20m,
            20m,
            80m,
            InstallmentStatus.Active,
            [],
            [],
            null);
    }

    private sealed record Harness(
        Guid InstallmentGuid,
        InstallmentRepaymentClaimService Service,
        InMemoryClaimRepository Repository,
        FakeInstallmentRepository Installments,
        InMemoryCommitRepository CommitRepository,
        MutableFakeTimeProvider Time,
        CountingCardTenderVerifier Verifier);

    private sealed class CountingCardTenderVerifier : IInstallmentRepaymentCardTenderVerifier
    {
        public int Calls { get; private set; }

        public Task VerifyAsync(InstallmentRepaymentClaimRecord claim, InstallmentRepaymentClaimCommitRequest request)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public List<(InstallmentRepaymentSupervisorDecision Decision, string Reason)> SupervisorResolutions { get; } = [];

        public Task RecordSupervisorResolutionAsync(
            InstallmentRepaymentClaimRecord claim,
            InstallmentRepaymentSupervisorDecision decision,
            string reason,
            string? reference,
            InstallmentRepaymentClaimIdentity supervisor)
        {
            SupervisorResolutions.Add((decision, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class MutableFakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class InMemoryClaimRepository : IInstallmentRepaymentClaimRepository
    {
        private readonly Dictionary<Guid, InstallmentRepaymentClaimRecord> claims = [];

        public Func<InstallmentRepaymentClaimRecord, InstallmentRepaymentClaimRecord>?
            ConcurrentUpdateFactory { get; set; }

        public bool FailNextInsert { get; set; }

        public Task<InstallmentRepaymentClaimRecord?> GetAsync(Guid operationGuid, CancellationToken cancellationToken)
        {
            claims.TryGetValue(operationGuid, out var claim);
            return Task.FromResult(claim);
        }

        public Task<InstallmentRepaymentClaimRecord?> GetBlockingAsync(
            Guid installmentGuid,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(claims.Values.FirstOrDefault(claim =>
                claim.InstallmentGuid == installmentGuid &&
                claim.Status is InstallmentRepaymentClaimStatus.Prepared
                    or InstallmentRepaymentClaimStatus.ProviderPending
                    or InstallmentRepaymentClaimStatus.Unknown));
        }

        public Task<InstallmentRepaymentClaimRecord?> GetPermanentConflictAsync(
            InstallmentRepaymentClaimRecord candidate,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(claims.Values.FirstOrDefault(existing =>
                existing.OperationGuid != candidate.OperationGuid &&
                (existing.PaymentGuid == candidate.PaymentGuid ||
                 (existing.InstallmentGuid == candidate.InstallmentGuid &&
                  string.Equals(existing.IdempotencyKey, candidate.IdempotencyKey, StringComparison.Ordinal)) ||
                 (candidate.Provider is not null &&
                  candidate.ProviderAttemptId is not null &&
                  string.Equals(existing.Provider, candidate.Provider, StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(existing.ProviderAttemptId, candidate.ProviderAttemptId, StringComparison.Ordinal)))));
        }

        public Task<bool> TryInsertAsync(
            InstallmentRepaymentClaimRecord claim,
            InstallmentRepaymentClaimInsertSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            if (FailNextInsert)
            {
                FailNextInsert = false;
                return Task.FromResult(false);
            }

            if (claims.ContainsKey(claim.OperationGuid) ||
                claims.Values.Any(existing => existing.PaymentGuid == claim.PaymentGuid) ||
                claims.Values.Any(existing =>
                    existing.InstallmentGuid == claim.InstallmentGuid &&
                    string.Equals(existing.IdempotencyKey, claim.IdempotencyKey, StringComparison.Ordinal)) ||
                claims.Values.Any(existing =>
                    claim.Provider is not null &&
                    claim.ProviderAttemptId is not null &&
                    string.Equals(existing.Provider, claim.Provider, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.ProviderAttemptId, claim.ProviderAttemptId, StringComparison.Ordinal)) ||
                claims.Values.Any(existing =>
                    existing.InstallmentGuid == claim.InstallmentGuid &&
                    existing.Status is InstallmentRepaymentClaimStatus.Prepared
                        or InstallmentRepaymentClaimStatus.ProviderPending
                        or InstallmentRepaymentClaimStatus.Unknown))
            {
                return Task.FromResult(false);
            }

            claims[claim.OperationGuid] = claim;
            return Task.FromResult(true);
        }

        public Task<bool> TryUpdateAsync(
            InstallmentRepaymentClaimRecord claim,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            if (!claims.TryGetValue(claim.OperationGuid, out var current) || current.Revision != expectedRevision)
            {
                return Task.FromResult(false);
            }

            if (ConcurrentUpdateFactory is not null)
            {
                var factory = ConcurrentUpdateFactory;
                ConcurrentUpdateFactory = null;
                claims[claim.OperationGuid] = factory(claim);
                return Task.FromResult(false);
            }

            claims[claim.OperationGuid] = claim;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeInstallmentRepository(InstallmentDetailsDto initial) : IInstallmentRepository
    {
        private InstallmentDetailsDto details = initial;

        public int AppendCount { get; private set; }

        public void ChangeStatus(InstallmentStatus status)
        {
            details = details with { Status = status };
        }

        public Task CreateAsync(InstallmentDetailsDto value, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InstallmentDetailsDto> AppendPaymentAsync(
            Guid installmentGuid,
            InstallmentPaymentDto payment,
            CancellationToken cancellationToken)
        {
            if (details.Payments.All(existing => existing.PaymentGuid != payment.PaymentGuid))
            {
                AppendCount++;
                var paid = details.PaidAmount + payment.Amount;
                details = details with
                {
                    PaidAmount = paid,
                    BalanceAmount = Math.Max(0m, details.TotalAmount - paid),
                    Payments = details.Payments.Concat([payment]).ToList()
                };
            }

            return Task.FromResult(details);
        }

        public Task<InstallmentDetailsDto> ConfirmPickupAsync(Guid installmentGuid, DateTimeOffset pickedUpAt, string pickedUpBy, string? note, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InstallmentDetailsDto> CancelWithRefundAsync(Guid installmentGuid, IReadOnlyList<InstallmentPaymentDto> refunds, InstallmentCancellationInfoDto cancellationInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InstallmentDetailsDto> VoidAsync(Guid installmentGuid, InstallmentCancellationInfoDto cancellationInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InstallmentPaymentLookup?> FindPaymentAsync(Guid paymentGuid, CancellationToken cancellationToken)
        {
            var payment = details.Payments.FirstOrDefault(existing => existing.PaymentGuid == paymentGuid);
            return Task.FromResult(payment is null ? null : new InstallmentPaymentLookup(details.InstallmentGuid, payment));
        }

        public Task<InstallmentPaymentLookup?> FindPaymentByIdempotencyKeyAsync(Guid installmentGuid, string idempotencyKey, CancellationToken cancellationToken)
        {
            var payment = details.Payments.FirstOrDefault(existing => existing.IdempotencyKey == idempotencyKey);
            return Task.FromResult(payment is null ? null : new InstallmentPaymentLookup(details.InstallmentGuid, payment));
        }

        public Task<InstallmentHistoryQueryResponse> QueryAsync(InstallmentHistoryQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InstallmentDetailsDto?> GetDetailsAsync(Guid installmentGuid, CancellationToken cancellationToken)
        {
            return Task.FromResult<InstallmentDetailsDto?>(
                details.InstallmentGuid == installmentGuid ? details : null);
        }
    }

    private sealed class InMemoryCommitRepository(
        InMemoryClaimRepository claims,
        FakeInstallmentRepository installments) : IInstallmentRepaymentClaimCommitRepository
    {
        public async Task<InstallmentRepaymentClaimCommitResult> CommitAsync(
            InstallmentRepaymentClaimRecord expectedClaim,
            InstallmentRepaymentClaimCommitRequest request,
            InstallmentRepaymentClaimIdentity recoveryIdentity,
            DateTimeOffset committedAtUtc,
            CancellationToken cancellationToken)
        {
            var current = await claims.GetAsync(expectedClaim.OperationGuid, cancellationToken)
                ?? throw new InvalidOperationException();
            var existing = await installments.FindPaymentAsync(current.PaymentGuid, cancellationToken);
            var alreadyRecorded = existing is not null;
            if (!alreadyRecorded)
            {
                await installments.AppendPaymentAsync(
                    current.InstallmentGuid,
                    new InstallmentPaymentDto(
                        current.PaymentGuid,
                        current.Method,
                        current.Amount,
                        request.Reference,
                        InstallmentPaymentStatus.Recorded,
                        committedAtUtc,
                        current.CashierId,
                        current.ClaimantDeviceCode,
                        request.CardTransactions,
                        current.IdempotencyKey,
                        request.ReservationToken,
                        current.CashierName),
                    cancellationToken);
            }

            var details = await installments.GetDetailsAsync(current.InstallmentGuid, cancellationToken)
                ?? throw new InvalidOperationException();
            var response = new InstallmentAppendPaymentResponse(
                current.InstallmentGuid,
                current.PaymentGuid,
                details.PaidAmount,
                details.BalanceAmount,
                details.Status,
                details,
                AlreadyRecorded: alreadyRecorded,
                Message: alreadyRecorded ? "AlreadyRecorded" : null);
            var responseJson = System.Text.Json.JsonSerializer.Serialize(
                response,
                InstallmentRepaymentClaimCommitRepositoryJson.Options);
            var committed = current with
            {
                Status = InstallmentRepaymentClaimStatus.Committed,
                UpdatedAtUtc = committedAtUtc,
                ExpiresAtUtc = null,
                CommittedAtUtc = committedAtUtc,
                CommitResponseJson = responseJson,
                LastRecoveryCashierId = recoveryIdentity.CashierId,
                LastRecoveryCashierName = recoveryIdentity.CashierName,
                LastRecoveryCashierUserGuid = recoveryIdentity.CashierUserGuid,
                RecoveredAtUtc = committedAtUtc,
                Revision = current.Revision + 1
            };
            await claims.TryUpdateAsync(committed, current.Revision, cancellationToken);
            return new InstallmentRepaymentClaimCommitResult(committed, response, alreadyRecorded);
        }
    }
}
