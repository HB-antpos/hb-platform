using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorApp.Shared.Constants;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Services;

public static class InstallmentRepaymentClaimErrorCodes
{
    public const string Busy = "INSTALLMENT_REPAYMENT_BUSY";
    public const string ClaimRequired = "INSTALLMENT_REPAYMENT_CLAIM_REQUIRED";
    public const string Mismatch = "INSTALLMENT_REPAYMENT_CLAIM_MISMATCH";
    public const string NotFound = "INSTALLMENT_REPAYMENT_CLAIM_NOT_FOUND";
    public const string Expired = "INSTALLMENT_REPAYMENT_CLAIM_EXPIRED";
    public const string Invalid = "INSTALLMENT_REPAYMENT_CLAIM_INVALID";
    public const string PaymentMethodUnsupported = "INSTALLMENT_REPAYMENT_PAYMENT_METHOD_UNSUPPORTED";
    public const string PermissionDenied = "INSTALLMENT_REPAYMENT_PERMISSION_DENIED";
}

public sealed class InstallmentRepaymentClaimException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class InstallmentRepaymentClaimOptions
{
    public bool Required { get; set; } = true;

    public bool CrossDeviceEnabled { get; set; } = true;
}

public sealed record InstallmentRepaymentClaimIdentity(
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    IReadOnlyCollection<string>? PermissionCodes = null,
    string? CashierUserGuid = null);

public interface IInstallmentRepaymentClaimService
{
    InstallmentRepaymentCapabilitiesResponse GetCapabilities();

    Task<InstallmentRepaymentClaimDto> CreateAsync(
        Guid installmentGuid,
        InstallmentRepaymentClaimCreateRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken);

    Task<InstallmentRepaymentClaimDto> BeginProviderAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimBeginProviderRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken);

    Task<InstallmentRepaymentClaimDto> PrepareProviderAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimPrepareProviderRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken);

    Task<InstallmentRepaymentClaimDto> GetAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken);

    Task<InstallmentRepaymentClaimDto> ResolveAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimResolveRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken);

    Task<InstallmentRepaymentClaimDto> CommitAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimCommitRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken);

    Task EnsureLegacyAppendAllowedAsync(Guid installmentGuid, CancellationToken cancellationToken);

    Task EnsureNoBlockingClaimAsync(Guid installmentGuid, CancellationToken cancellationToken);
}

public sealed class InstallmentRepaymentClaimService(
    IInstallmentRepaymentClaimRepository claimRepository,
    IInstallmentRepository installmentRepository,
    IInstallmentRepaymentClaimCommitRepository commitRepository,
    IOptions<InstallmentRepaymentClaimOptions> options,
    TimeProvider? timeProvider = null,
    IOptions<InstallmentCancelClaimOptions>? cancelOptions = null,
    IOptions<InstallmentCrossDeviceLifecycleOptions>? lifecycleOptions = null) : IInstallmentRepaymentClaimService
{
    public const int PreparedClaimTtlSeconds = 120;
    private readonly InstallmentRepaymentClaimOptions _options = options.Value;
    private readonly InstallmentCancelClaimOptions _cancelOptions =
        cancelOptions?.Value ?? new InstallmentCancelClaimOptions();
    private readonly InstallmentCrossDeviceLifecycleOptions _lifecycleOptions =
        lifecycleOptions?.Value ?? new InstallmentCrossDeviceLifecycleOptions();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public InstallmentRepaymentCapabilitiesResponse GetCapabilities()
    {
        return new InstallmentRepaymentCapabilitiesResponse(
            RepaymentClaimsSupported: true,
            RepaymentClaimsRequired: _options.Required,
            CardRepaymentSupported: false,
            CrossDeviceRepaymentEnabled: _options.CrossDeviceEnabled,
            PreparedClaimTtlSeconds: PreparedClaimTtlSeconds,
            CancelClaimsSupported: true,
            CancelClaimsRequired: _cancelOptions.Required,
            CancelPreparedClaimTtlSeconds: InstallmentCancelClaimService.PreparedClaimTtlSeconds,
            CrossDeviceCancelRefundEnabled: _lifecycleOptions.CancelRefundEnabled,
            CrossDeviceVoidEnabled: _lifecycleOptions.VoidEnabled,
            CrossDevicePickupEnabled: _lifecycleOptions.PickupEnabled,
            RepaymentClaimPrepareProviderV1: true,
            AmendLinesSupported: true);
    }

    public async Task<InstallmentRepaymentClaimDto> CreateAsync(
        Guid installmentGuid,
        InstallmentRepaymentClaimCreateRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken)
    {
        var normalizedIdentity = NormalizeIdentity(identity);
        var normalizedRequest = NormalizeCreateRequest(request);
        EnsureServerVerifiablePaymentMethod(normalizedRequest.Method);
        ValidatePaymentPermissions(normalizedIdentity, normalizedRequest.Method);
        var details = await GetRequiredInstallmentAsync(installmentGuid, cancellationToken);
        ValidateInstallmentForNewClaim(details, normalizedIdentity, normalizedRequest.Amount);
        var fingerprint = CreateFingerprint(installmentGuid, normalizedRequest);

        var existing = await claimRepository.GetAsync(normalizedRequest.OperationGuid, cancellationToken);
        if (existing is not null)
        {
            existing = await ExpirePreparedAsync(existing, cancellationToken);
            ValidateAccess(existing, installmentGuid, normalizedIdentity);
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw Mismatch("operationGuid is already bound to different repayment facts.");
            }

            return await MapAsync(existing, alreadyExists: true);
        }

        await EnsureNoBlockingClaimAsync(installmentGuid, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var claim = new InstallmentRepaymentClaimRecord(
            installmentGuid,
            normalizedRequest.OperationGuid,
            normalizedRequest.PaymentGuid,
            normalizedIdentity.StoreCode,
            normalizedIdentity.DeviceCode,
            normalizedIdentity.CashierId,
            normalizedIdentity.CashierName,
            normalizedRequest.Amount,
            normalizedRequest.Method,
            normalizedRequest.IdempotencyKey,
            fingerprint,
            InstallmentRepaymentClaimStatus.Prepared,
            Provider: null,
            ProviderAttemptId: null,
            now,
            now,
            now.AddSeconds(PreparedClaimTtlSeconds),
            CommittedAtUtc: null,
            Revision: 1);
        var insertSnapshot = new InstallmentRepaymentClaimInsertSnapshot(
            details.Status,
            details.PaidAmount,
            details.BalanceAmount);
        if (await claimRepository.TryInsertAsync(claim, insertSnapshot, cancellationToken))
        {
            return await MapAsync(claim, alreadyExists: false);
        }

        // 唯一键竞争可能是同一 operation 的安全重试，也可能是同一分期的另一个 blocking claim。
        existing = await claimRepository.GetAsync(normalizedRequest.OperationGuid, cancellationToken);
        if (existing is not null)
        {
            ValidateAccess(existing, installmentGuid, normalizedIdentity);
            if (string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return await MapAsync(existing, alreadyExists: true);
            }

            throw Mismatch("operationGuid is already bound to different repayment facts.");
        }

        throw await ClassifyInsertFailureAsync(claim, cancellationToken);
    }

    public async Task<InstallmentRepaymentClaimDto> PrepareProviderAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimPrepareProviderRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePrepareProviderRequest(operationGuid, request);
        var normalizedIdentity = NormalizeIdentity(identity);
        var existing = await claimRepository.GetAsync(operationGuid, cancellationToken);
        if (existing is not null)
        {
            return await PrepareExistingProviderAsync(
                existing,
                installmentGuid,
                normalized.CreateRequest,
                normalized.Provider,
                normalized.ProviderAttemptId,
                normalizedIdentity,
                cancellationToken);
        }

        EnsureServerVerifiablePaymentMethod(normalized.CreateRequest.Method);
        ValidatePaymentPermissions(normalizedIdentity, normalized.CreateRequest.Method);
        InstallmentRepaymentClaimCommitEvidenceValidator.ValidateProviderForMethod(
            normalized.CreateRequest.Method,
            normalized.Provider);
        var details = await GetRequiredInstallmentAsync(installmentGuid, cancellationToken);
        ValidateInstallmentForNewClaim(details, normalizedIdentity, normalized.CreateRequest.Amount);
        await EnsureNoBlockingClaimAsync(installmentGuid, cancellationToken);

        var now = _timeProvider.GetUtcNow();
        // 新入口把 claim 和 provider binding 一次写入，ProviderPending 才是第一份可恢复事实。
        var claim = new InstallmentRepaymentClaimRecord(
            installmentGuid,
            operationGuid,
            normalized.CreateRequest.PaymentGuid,
            normalizedIdentity.StoreCode,
            normalizedIdentity.DeviceCode,
            normalizedIdentity.CashierId,
            normalizedIdentity.CashierName,
            normalized.CreateRequest.Amount,
            normalized.CreateRequest.Method,
            normalized.CreateRequest.IdempotencyKey,
            CreateFingerprint(installmentGuid, normalized.CreateRequest),
            InstallmentRepaymentClaimStatus.ProviderPending,
            normalized.Provider,
            normalized.ProviderAttemptId,
            now,
            now,
            ExpiresAtUtc: null,
            CommittedAtUtc: null,
            Revision: 1,
            LastRecoveryCashierId: normalizedIdentity.CashierId,
            LastRecoveryCashierName: normalizedIdentity.CashierName,
            LastRecoveryCashierUserGuid: normalizedIdentity.CashierUserGuid,
            RecoveredAtUtc: now);
        var insertSnapshot = new InstallmentRepaymentClaimInsertSnapshot(
            details.Status,
            details.PaidAmount,
            details.BalanceAmount);
        if (await claimRepository.TryInsertAsync(claim, insertSnapshot, cancellationToken))
        {
            return await MapAsync(claim, alreadyExists: false);
        }

        // 竞争失败后只允许恢复同 operation 的同 binding；其他 blocking operation 仍返回 busy。
        existing = await claimRepository.GetAsync(operationGuid, cancellationToken);
        if (existing is not null)
        {
            return await PrepareExistingProviderAsync(
                existing,
                installmentGuid,
                normalized.CreateRequest,
                normalized.Provider,
                normalized.ProviderAttemptId,
                normalizedIdentity,
                cancellationToken);
        }

        throw await ClassifyInsertFailureAsync(claim, cancellationToken);
    }

    private async Task<InstallmentRepaymentClaimDto> PrepareExistingProviderAsync(
        InstallmentRepaymentClaimRecord current,
        Guid installmentGuid,
        InstallmentRepaymentClaimCreateRequest normalizedRequest,
        string provider,
        string providerAttemptId,
        InstallmentRepaymentClaimIdentity normalizedIdentity,
        CancellationToken cancellationToken)
    {
        ValidateAccess(current, installmentGuid, normalizedIdentity);
        if (!string.Equals(
                current.Fingerprint,
                CreateFingerprint(installmentGuid, normalizedRequest),
                StringComparison.Ordinal))
        {
            throw Mismatch("operationGuid is already bound to different repayment facts.");
        }

        EnsureServerVerifiablePaymentMethod(current.Method);
        InstallmentRepaymentClaimCommitEvidenceValidator.ValidateProviderForMethod(current.Method, provider);
        current = await ExpirePreparedAsync(current, cancellationToken);

        // provider 事实锁定后，精确重放属于原 operation 恢复，不受后续 cashier 权限撤销影响。
        if (current.Status is InstallmentRepaymentClaimStatus.ProviderPending
            or InstallmentRepaymentClaimStatus.Unknown
            or InstallmentRepaymentClaimStatus.Committed)
        {
            if (!ProviderBindingMatches(current, provider, providerAttemptId))
            {
                throw Mismatch("Provider attempt does not match the existing claim.");
            }
        }

        switch (current.Status)
        {
            case InstallmentRepaymentClaimStatus.Prepared:
                ValidatePaymentPermissions(normalizedIdentity, current.Method);
                if (!string.Equals(current.CashierId, normalizedIdentity.CashierId, StringComparison.OrdinalIgnoreCase))
                {
                    throw Mismatch("A prepared repayment claim can only begin under its creating cashier; release it before changing cashier.");
                }

                return await BindPreparedProviderAsync(
                    current,
                    provider,
                    providerAttemptId,
                    normalizedIdentity,
                    cancellationToken);

            case InstallmentRepaymentClaimStatus.ProviderPending:
                current = await RecordRecoveryCashierAsync(current, normalizedIdentity, cancellationToken);
                return await MapAsync(current, alreadyExists: true);

            case InstallmentRepaymentClaimStatus.Unknown:
                var resumed = current with
                {
                    Status = InstallmentRepaymentClaimStatus.ProviderPending,
                    UpdatedAtUtc = _timeProvider.GetUtcNow(),
                    LastRecoveryCashierId = normalizedIdentity.CashierId,
                    LastRecoveryCashierName = normalizedIdentity.CashierName,
                    LastRecoveryCashierUserGuid = normalizedIdentity.CashierUserGuid,
                    RecoveredAtUtc = _timeProvider.GetUtcNow(),
                    Revision = current.Revision + 1
                };
                if (await claimRepository.TryUpdateAsync(resumed, current.Revision, cancellationToken))
                {
                    return await MapAsync(resumed, alreadyExists: false);
                }

                return await MapProviderCasReplayAsync(
                    current,
                    provider,
                    providerAttemptId,
                    normalizedIdentity,
                    cancellationToken);

            case InstallmentRepaymentClaimStatus.Committed:
                current = await RecordRecoveryCashierAsync(current, normalizedIdentity, cancellationToken);
                return await MapAsync(current, alreadyExists: true);

            case InstallmentRepaymentClaimStatus.Released:
            case InstallmentRepaymentClaimStatus.Declined:
                throw Mismatch("Released or declined repayment claims cannot be prepared again.");

            default:
                throw Mismatch("Repayment claim is in an unsupported state.");
        }
    }

    private async Task<InstallmentRepaymentClaimDto> BindPreparedProviderAsync(
        InstallmentRepaymentClaimRecord current,
        string provider,
        string providerAttemptId,
        InstallmentRepaymentClaimIdentity normalizedIdentity,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var updated = current with
        {
            Status = InstallmentRepaymentClaimStatus.ProviderPending,
            Provider = provider,
            ProviderAttemptId = providerAttemptId,
            UpdatedAtUtc = now,
            ExpiresAtUtc = null,
            LastRecoveryCashierId = normalizedIdentity.CashierId,
            LastRecoveryCashierName = normalizedIdentity.CashierName,
            LastRecoveryCashierUserGuid = normalizedIdentity.CashierUserGuid,
            RecoveredAtUtc = now,
            Revision = current.Revision + 1
        };
        if (await claimRepository.TryUpdateAsync(updated, current.Revision, cancellationToken))
        {
            return await MapAsync(updated, alreadyExists: false);
        }

        return await MapProviderCasReplayAsync(
            current,
            provider,
            providerAttemptId,
            normalizedIdentity,
            cancellationToken);
    }

    public async Task<InstallmentRepaymentClaimDto> BeginProviderAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimBeginProviderRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken)
    {
        var provider = NormalizeRequired(request.Provider, "provider");
        if (provider.Length > 32)
        {
            throw Invalid("provider must not exceed 32 characters.");
        }

        var providerAttemptId = NormalizeRequired(request.ProviderAttemptId, "providerAttemptId");
        if (providerAttemptId.Length > 128)
        {
            throw Invalid("providerAttemptId must not exceed 128 characters.");
        }
        var currentIdentity = NormalizeIdentity(identity);
        var current = await GetRequiredAccessibleClaimAsync(installmentGuid, operationGuid, identity, cancellationToken);
        ValidatePaymentPermissions(currentIdentity, current.Method);
        EnsureServerVerifiablePaymentMethod(current.Method);
        current = await ExpirePreparedAsync(current, cancellationToken);
        InstallmentRepaymentClaimCommitEvidenceValidator.ValidateProviderForMethod(current.Method, provider);
        if (current.Status == InstallmentRepaymentClaimStatus.Prepared &&
            !string.Equals(current.CashierId, currentIdentity.CashierId, StringComparison.OrdinalIgnoreCase))
        {
            throw Mismatch("A prepared repayment claim can only begin under its creating cashier; release it before changing cashier.");
        }
        if (current.Status == InstallmentRepaymentClaimStatus.Released && current.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            throw new InstallmentRepaymentClaimException(
                InstallmentRepaymentClaimErrorCodes.Expired,
                "Prepared repayment claim has expired.");
        }

        if (current.Status is InstallmentRepaymentClaimStatus.ProviderPending
            or InstallmentRepaymentClaimStatus.Unknown)
        {
            if (string.Equals(current.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(current.ProviderAttemptId, providerAttemptId, StringComparison.Ordinal))
            {
                if (current.Status == InstallmentRepaymentClaimStatus.ProviderPending)
                {
                    current = await RecordRecoveryCashierAsync(current, currentIdentity, cancellationToken);
                    return await MapAsync(current, alreadyExists: true);
                }

                var resumed = current with
                {
                    Status = InstallmentRepaymentClaimStatus.ProviderPending,
                    UpdatedAtUtc = _timeProvider.GetUtcNow(),
                    LastRecoveryCashierId = currentIdentity.CashierId,
                    LastRecoveryCashierName = currentIdentity.CashierName,
                    LastRecoveryCashierUserGuid = currentIdentity.CashierUserGuid,
                    RecoveredAtUtc = _timeProvider.GetUtcNow(),
                    Revision = current.Revision + 1
                };
                resumed = await UpdateOrReloadAsync(current, resumed, cancellationToken);
                if (resumed.Status != InstallmentRepaymentClaimStatus.ProviderPending)
                {
                    throw Mismatch("Repayment claim changed while resuming provider recovery.");
                }

                return await MapAsync(resumed, alreadyExists: false);
            }

            throw Mismatch("Provider attempt does not match the existing claim.");
        }

        if (current.Status != InstallmentRepaymentClaimStatus.Prepared)
        {
            throw Mismatch("Only a prepared repayment claim can begin a provider attempt.");
        }

        var updated = current with
        {
            Status = InstallmentRepaymentClaimStatus.ProviderPending,
            Provider = provider,
            ProviderAttemptId = providerAttemptId,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
            ExpiresAtUtc = null,
            LastRecoveryCashierId = currentIdentity.CashierId,
            LastRecoveryCashierName = currentIdentity.CashierName,
            LastRecoveryCashierUserGuid = currentIdentity.CashierUserGuid,
            RecoveredAtUtc = _timeProvider.GetUtcNow(),
            Revision = current.Revision + 1
        };
        updated = await UpdateOrReloadAsync(current, updated, cancellationToken);
        if (updated.Status != InstallmentRepaymentClaimStatus.ProviderPending ||
            !string.Equals(updated.Provider, provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(updated.ProviderAttemptId, providerAttemptId, StringComparison.Ordinal))
        {
            throw Mismatch("Repayment claim changed while beginning the provider attempt.");
        }

        return await MapAsync(updated, alreadyExists: false);
    }

    public async Task<InstallmentRepaymentClaimDto> GetAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken)
    {
        var current = await GetRequiredAccessibleClaimAsync(installmentGuid, operationGuid, identity, cancellationToken);
        ValidateReadPermissions(identity, current);
        current = await ExpirePreparedAsync(current, cancellationToken);
        return await MapAsync(current, alreadyExists: true);
    }

    public async Task<InstallmentRepaymentClaimDto> ResolveAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimResolveRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken)
    {
        var current = await GetRequiredAccessibleClaimAsync(installmentGuid, operationGuid, identity, cancellationToken);
        var requiresCashNotCollectedEvidence =
            request.Outcome == InstallmentRepaymentClaimResolveOutcome.Released &&
            RequiresCashNotCollectedEvidence(current);
        if (requiresCashNotCollectedEvidence)
        {
            // provider 已锁定后只能由原设备显式证明“未收现”并持有取消权限；绝不自动释放现金 claim。
            ValidateCashNotCollectedRelease(current, request, NormalizeIdentity(identity));
        }
        else
        {
            ValidateContinuationPermissions(identity, current);
        }

        current = await ExpirePreparedAsync(current, cancellationToken);
        var targetStatus = request.Outcome switch
        {
            InstallmentRepaymentClaimResolveOutcome.Released => InstallmentRepaymentClaimStatus.Released,
            InstallmentRepaymentClaimResolveOutcome.Declined => InstallmentRepaymentClaimStatus.Declined,
            InstallmentRepaymentClaimResolveOutcome.Unknown => InstallmentRepaymentClaimStatus.Unknown,
            _ => throw new InstallmentRepaymentClaimException(
                InstallmentRepaymentClaimErrorCodes.Invalid,
                "Unsupported repayment claim outcome.")
        };
        if (current.Status == targetStatus)
        {
            return await MapAsync(current, alreadyExists: true);
        }

        var validTransition = request.Outcome switch
        {
            InstallmentRepaymentClaimResolveOutcome.Released =>
                current.Status is InstallmentRepaymentClaimStatus.Prepared
                    or InstallmentRepaymentClaimStatus.ProviderPending
                    or InstallmentRepaymentClaimStatus.Unknown,
            InstallmentRepaymentClaimResolveOutcome.Declined =>
                current.Status == InstallmentRepaymentClaimStatus.ProviderPending,
            InstallmentRepaymentClaimResolveOutcome.Unknown =>
                current.Status == InstallmentRepaymentClaimStatus.ProviderPending,
            _ => false
        };
        if (!validTransition)
        {
            throw Mismatch(request.Outcome == InstallmentRepaymentClaimResolveOutcome.Released
                ? "Only a prepared claim can be released."
                : "Only a provider-pending claim can be declined or marked unknown.");
        }

        var recoveryIdentity = NormalizeIdentity(identity);
        var updated = current with
        {
            Status = targetStatus,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
            ExpiresAtUtc = null,
            LastRecoveryCashierId = recoveryIdentity.CashierId,
            LastRecoveryCashierName = recoveryIdentity.CashierName,
            LastRecoveryCashierUserGuid = recoveryIdentity.CashierUserGuid,
            RecoveredAtUtc = _timeProvider.GetUtcNow(),
            Revision = current.Revision + 1
        };
        updated = await UpdateOrReloadAsync(current, updated, cancellationToken);
        if (updated.Status != targetStatus)
        {
            throw Mismatch("Repayment claim changed while resolving the provider outcome.");
        }

        return await MapAsync(updated, alreadyExists: false);
    }

    private static bool RequiresCashNotCollectedEvidence(InstallmentRepaymentClaimRecord current) =>
        current.Status is InstallmentRepaymentClaimStatus.ProviderPending
            or InstallmentRepaymentClaimStatus.Unknown ||
        current.Status == InstallmentRepaymentClaimStatus.Released &&
        (!string.IsNullOrWhiteSpace(current.Provider) ||
         !string.IsNullOrWhiteSpace(current.ProviderAttemptId));

    private static void ValidateCashNotCollectedRelease(
        InstallmentRepaymentClaimRecord current,
        InstallmentRepaymentClaimResolveRequest request,
        InstallmentRepaymentClaimIdentity identity)
    {
        if (!request.CashNotCollectedConfirmed)
        {
            throw Mismatch("Cash provider claims require explicit confirmation that cash was not collected before release.");
        }

        var providerAttemptId = NormalizeRequired(
            request.ProviderAttemptId,
            "providerAttemptId",
            128);
        if (current.Method != PaymentMethodKind.Cash ||
            !string.Equals(current.Provider, "cash", StringComparison.OrdinalIgnoreCase))
        {
            throw Mismatch("Only a cash provider claim can use cash-not-collected release.");
        }

        if (!string.Equals(current.ProviderAttemptId, providerAttemptId, StringComparison.Ordinal))
        {
            throw Mismatch("Provider attempt does not match the existing claim.");
        }

        var permissions = identity.PermissionCodes?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (permissions is null ||
            !permissions.Contains(Permissions.PosTerminal.Installments.Cancel))
        {
            throw PermissionDenied("Verified cashier lacks the installment cancellation permission.");
        }
    }

    public async Task<InstallmentRepaymentClaimDto> CommitAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimCommitRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken)
    {
        var current = await GetRequiredAccessibleClaimAsync(installmentGuid, operationGuid, identity, cancellationToken);
        ValidateContinuationPermissions(identity, current);
        EnsureServerVerifiablePaymentMethod(current.Method);
        if (current.Status == InstallmentRepaymentClaimStatus.Committed)
        {
            current = await RecordRecoveryCashierAsync(
                current,
                NormalizeIdentity(identity),
                cancellationToken);
            return await MapAsync(current, alreadyExists: true);
        }

        if (current.Status != InstallmentRepaymentClaimStatus.ProviderPending)
        {
            throw Mismatch("Only a provider-pending repayment claim can be committed.");
        }

        InstallmentRepaymentClaimCommitEvidenceValidator.Validate(current, request);

        var result = await commitRepository.CommitAsync(
            current,
            request,
            NormalizeIdentity(identity),
            _timeProvider.GetUtcNow(),
            cancellationToken);
        return Map(
            result.Claim,
            result.CommitResponse,
            alreadyExists: result.AlreadyRecorded);
    }

    public async Task EnsureLegacyAppendAllowedAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        if (_options.Required)
        {
            throw new InstallmentRepaymentClaimException(
                InstallmentRepaymentClaimErrorCodes.ClaimRequired,
                "This server requires a repayment claim before committing an installment payment.");
        }

        await EnsureNoBlockingClaimAsync(installmentGuid, cancellationToken);
    }

    public async Task EnsureNoBlockingClaimAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        var blocking = await claimRepository.GetBlockingAsync(installmentGuid, cancellationToken);
        if (blocking is null)
        {
            return;
        }

        blocking = await ExpirePreparedAsync(blocking, cancellationToken);
        if (IsBlocking(blocking.Status))
        {
            throw Busy();
        }
    }

    private async Task<InstallmentRepaymentClaimRecord> GetRequiredAccessibleClaimAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken)
    {
        if (operationGuid == Guid.Empty)
        {
            throw new InstallmentRepaymentClaimException(
                InstallmentRepaymentClaimErrorCodes.Invalid,
                "operationGuid is required.");
        }

        var normalizedIdentity = NormalizeIdentity(identity);
        var current = await claimRepository.GetAsync(operationGuid, cancellationToken)
            ?? throw new InstallmentRepaymentClaimException(
                InstallmentRepaymentClaimErrorCodes.NotFound,
                "Repayment claim was not found.");
        ValidateAccess(current, installmentGuid, normalizedIdentity);
        return current;
    }

    private async Task<InstallmentRepaymentClaimDto> MapProviderCasReplayAsync(
        InstallmentRepaymentClaimRecord expected,
        string provider,
        string providerAttemptId,
        InstallmentRepaymentClaimIdentity normalizedIdentity,
        CancellationToken cancellationToken)
    {
        var reloaded = await claimRepository.GetAsync(expected.OperationGuid, cancellationToken)
            ?? throw new InstallmentRepaymentClaimException(
                InstallmentRepaymentClaimErrorCodes.NotFound,
                "Repayment claim disappeared during provider recovery.");
        ValidateAccess(reloaded, expected.InstallmentGuid, normalizedIdentity);
        if (!ImmutableFactsMatch(expected, reloaded) ||
            reloaded.Status is not (InstallmentRepaymentClaimStatus.ProviderPending
                or InstallmentRepaymentClaimStatus.Committed) ||
            !ProviderBindingMatches(reloaded, provider, providerAttemptId))
        {
            throw Mismatch("Repayment claim changed while binding or resuming the provider attempt.");
        }

        return await MapAsync(reloaded, alreadyExists: true);
    }

    private async Task<InstallmentRepaymentClaimException> ClassifyInsertFailureAsync(
        InstallmentRepaymentClaimRecord candidate,
        CancellationToken cancellationToken)
    {
        var permanentConflict = await claimRepository.GetPermanentConflictAsync(candidate, cancellationToken);
        if (permanentConflict is not null)
        {
            return Mismatch("Repayment claim payment, idempotency, or provider attempt is already bound to another operation.");
        }

        var blocking = await claimRepository.GetBlockingAsync(candidate.InstallmentGuid, cancellationToken);
        if (blocking is not null)
        {
            blocking = await ExpirePreparedAsync(blocking, cancellationToken);
            if (blocking.OperationGuid != candidate.OperationGuid && IsBlocking(blocking.Status))
            {
                return Busy();
            }
        }

        return Mismatch("Repayment claim could not be created because the persisted installment facts changed.");
    }

    private async Task<InstallmentRepaymentClaimRecord> ExpirePreparedAsync(
        InstallmentRepaymentClaimRecord current,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (current.Status != InstallmentRepaymentClaimStatus.Prepared ||
            current.ExpiresAtUtc is null ||
            current.ExpiresAtUtc > now)
        {
            return current;
        }

        var released = current with
        {
            Status = InstallmentRepaymentClaimStatus.Released,
            UpdatedAtUtc = now,
            Revision = current.Revision + 1
        };
        return await UpdateOrReloadAsync(current, released, cancellationToken);
    }

    private async Task<InstallmentRepaymentClaimRecord> UpdateOrReloadAsync(
        InstallmentRepaymentClaimRecord current,
        InstallmentRepaymentClaimRecord updated,
        CancellationToken cancellationToken)
    {
        if (await claimRepository.TryUpdateAsync(updated, current.Revision, cancellationToken))
        {
            return updated;
        }

        return await claimRepository.GetAsync(current.OperationGuid, cancellationToken)
            ?? throw new InstallmentRepaymentClaimException(
                InstallmentRepaymentClaimErrorCodes.NotFound,
                "Repayment claim disappeared during an update.");
    }

    private async Task<InstallmentDetailsDto> GetRequiredInstallmentAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        return await installmentRepository.GetDetailsAsync(installmentGuid, cancellationToken)
            ?? throw new InstallmentRepaymentClaimException(
                InstallmentRepaymentClaimErrorCodes.NotFound,
                "Installment was not found.");
    }

    private void ValidateInstallmentForNewClaim(
        InstallmentDetailsDto details,
        InstallmentRepaymentClaimIdentity identity,
        decimal amount)
    {
        if (!string.Equals(details.StoreCode, identity.StoreCode, StringComparison.OrdinalIgnoreCase))
        {
            throw Mismatch("Installment does not belong to the authenticated store.");
        }

        if (!_options.CrossDeviceEnabled &&
            !string.Equals(details.DeviceCode, identity.DeviceCode, StringComparison.OrdinalIgnoreCase))
        {
            throw Mismatch("Cross-device installment repayment is disabled.");
        }

        if (details.Status != InstallmentStatus.Active || details.BalanceAmount <= 0m)
        {
            throw Mismatch("Only active unpaid installments can start a repayment claim.");
        }

        if (amount > details.BalanceAmount)
        {
            throw Mismatch("Repayment claim amount cannot exceed the installment balance.");
        }
    }

    private void ValidateAccess(
        InstallmentRepaymentClaimRecord claim,
        Guid installmentGuid,
        InstallmentRepaymentClaimIdentity identity)
    {
        if (claim.InstallmentGuid != installmentGuid ||
            !string.Equals(claim.StoreCode, identity.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(claim.ClaimantDeviceCode, identity.DeviceCode, StringComparison.OrdinalIgnoreCase))
        {
            throw Mismatch("Authenticated identity does not match the repayment claim.");
        }
    }

    private Task<InstallmentRepaymentClaimDto> MapAsync(
        InstallmentRepaymentClaimRecord claim,
        bool alreadyExists)
    {
        InstallmentAppendPaymentResponse? commit = null;
        if (claim.Status == InstallmentRepaymentClaimStatus.Committed)
        {
            if (string.IsNullOrWhiteSpace(claim.CommitResponseJson))
            {
                throw Mismatch("Committed claim has no persisted commit response.");
            }

            try
            {
                commit = JsonSerializer.Deserialize<InstallmentAppendPaymentResponse>(
                    claim.CommitResponseJson,
                    InstallmentRepaymentClaimCommitRepositoryJson.Options);
            }
            catch (JsonException)
            {
                throw Mismatch("Persisted repayment commit response is invalid.");
            }

            if (commit is null ||
                commit.InstallmentGuid != claim.InstallmentGuid ||
                commit.PaymentGuid != claim.PaymentGuid)
            {
                throw Mismatch("Persisted repayment commit response does not match the claim.");
            }
        }

        return Task.FromResult(Map(claim, commit, alreadyExists));
    }

    private static InstallmentRepaymentClaimDto Map(
        InstallmentRepaymentClaimRecord claim,
        InstallmentAppendPaymentResponse? commit,
        bool alreadyExists)
    {
        return new InstallmentRepaymentClaimDto(
            claim.InstallmentGuid,
            claim.OperationGuid,
            claim.PaymentGuid,
            claim.Amount,
            claim.Method,
            claim.IdempotencyKey,
            claim.Status,
            claim.Provider,
            claim.ProviderAttemptId,
            claim.CreatedAtUtc,
            claim.UpdatedAtUtc,
            claim.ExpiresAtUtc,
            commit,
            alreadyExists);
    }

    private static InstallmentRepaymentClaimCreateRequest NormalizeCreateRequest(
        InstallmentRepaymentClaimCreateRequest request)
    {
        if (request.OperationGuid == Guid.Empty)
        {
            throw Invalid("operationGuid is required.");
        }

        if (request.PaymentGuid == Guid.Empty)
        {
            throw Invalid("paymentGuid is required.");
        }

        var amount = RoundCurrency(request.Amount);
        if (amount <= 0m)
        {
            throw Invalid("amount must be greater than zero.");
        }

        if (!Enum.IsDefined(request.Method))
        {
            throw Invalid("method is invalid.");
        }

        return request with
        {
            Amount = amount,
            IdempotencyKey = NormalizeRequired(request.IdempotencyKey, "idempotencyKey", 100)
        };
    }

    private static (InstallmentRepaymentClaimCreateRequest CreateRequest, string Provider, string ProviderAttemptId)
        NormalizePrepareProviderRequest(
            Guid operationGuid,
            InstallmentRepaymentClaimPrepareProviderRequest request)
    {
        var createRequest = NormalizeCreateRequest(new InstallmentRepaymentClaimCreateRequest(
            operationGuid,
            request.PaymentGuid,
            request.Amount,
            request.Method,
            request.IdempotencyKey));
        return (
            createRequest,
            NormalizeRequired(request.Provider, "provider", 32),
            NormalizeRequired(request.ProviderAttemptId, "providerAttemptId", 128));
    }

    private static bool ProviderBindingMatches(
        InstallmentRepaymentClaimRecord claim,
        string provider,
        string providerAttemptId) =>
        string.Equals(claim.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(claim.ProviderAttemptId, providerAttemptId, StringComparison.Ordinal);

    private static bool ImmutableFactsMatch(
        InstallmentRepaymentClaimRecord expected,
        InstallmentRepaymentClaimRecord actual) =>
        expected.InstallmentGuid == actual.InstallmentGuid &&
        expected.OperationGuid == actual.OperationGuid &&
        expected.PaymentGuid == actual.PaymentGuid &&
        string.Equals(expected.StoreCode, actual.StoreCode, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.ClaimantDeviceCode, actual.ClaimantDeviceCode, StringComparison.OrdinalIgnoreCase) &&
        expected.Amount == actual.Amount &&
        expected.Method == actual.Method &&
        string.Equals(expected.IdempotencyKey, actual.IdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(expected.Fingerprint, actual.Fingerprint, StringComparison.Ordinal);

    private static void ValidateContinuationPermissions(
        InstallmentRepaymentClaimIdentity identity,
        InstallmentRepaymentClaimRecord current)
    {
        if (current.Status is InstallmentRepaymentClaimStatus.ProviderPending
            or InstallmentRepaymentClaimStatus.Unknown
            or InstallmentRepaymentClaimStatus.Committed)
        {
            return;
        }

        ValidatePaymentPermissions(NormalizeIdentity(identity), current.Method);
    }

    private static void ValidateReadPermissions(
        InstallmentRepaymentClaimIdentity identity,
        InstallmentRepaymentClaimRecord current)
    {
        var normalizedIdentity = NormalizeIdentity(identity);
        if (current.Status == InstallmentRepaymentClaimStatus.Released &&
            current.Method == PaymentMethodKind.Cash &&
            string.Equals(current.Provider, "cash", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(current.ProviderAttemptId) &&
            normalizedIdentity.PermissionCodes?.Contains(
                Permissions.PosTerminal.Installments.Cancel,
                StringComparer.OrdinalIgnoreCase) == true)
        {
            // 仅 GET 恢复探测允许原设备的取消主管读取显式现金 release；不扩大普通 Released 或写路径权限。
            return;
        }

        ValidateContinuationPermissions(normalizedIdentity, current);
    }

    private static InstallmentRepaymentClaimIdentity NormalizeIdentity(
        InstallmentRepaymentClaimIdentity identity)
    {
        return new InstallmentRepaymentClaimIdentity(
            NormalizeRequired(identity.StoreCode, "authenticated storeCode", 50),
            NormalizeRequired(identity.DeviceCode, "authenticated deviceCode", 50),
            NormalizeRequired(identity.CashierId, "verified cashierId", 50),
            NormalizeRequired(identity.CashierName, "verified cashierName", 100),
            identity.PermissionCodes?
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            NormalizeRequired(identity.CashierUserGuid, "verified cashierUserGuid", 50));
    }

    private static string CreateFingerprint(
        Guid installmentGuid,
        InstallmentRepaymentClaimCreateRequest request)
    {
        var material = string.Join(
            "|",
            installmentGuid.ToString("D"),
            request.OperationGuid.ToString("D"),
            request.PaymentGuid.ToString("D"),
            request.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            ((int)request.Method).ToString(CultureInfo.InvariantCulture),
            request.IdempotencyKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static bool IsBlocking(InstallmentRepaymentClaimStatus status) =>
        status is InstallmentRepaymentClaimStatus.Prepared
            or InstallmentRepaymentClaimStatus.ProviderPending
            or InstallmentRepaymentClaimStatus.Unknown;

    private static InstallmentRepaymentClaimException Busy() => new(
        InstallmentRepaymentClaimErrorCodes.Busy,
        "Another repayment claim is blocking this installment.");

    private static InstallmentRepaymentClaimException Mismatch(string message) => new(
        InstallmentRepaymentClaimErrorCodes.Mismatch,
        message);

    private static InstallmentRepaymentClaimException Invalid(string message) => new(
        InstallmentRepaymentClaimErrorCodes.Invalid,
        message);

    private static InstallmentRepaymentClaimException PaymentMethodUnsupported(string message) => new(
        InstallmentRepaymentClaimErrorCodes.PaymentMethodUnsupported,
        message);

    private static InstallmentRepaymentClaimException PermissionDenied(string message) => new(
        InstallmentRepaymentClaimErrorCodes.PermissionDenied,
        message);

    private static string NormalizeRequired(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"{fieldName} is required.");
        }

        return value.Trim();
    }

    private static string NormalizeRequired(string? value, string fieldName, int maxLength)
    {
        var normalized = NormalizeRequired(value, fieldName);
        if (normalized.Length > maxLength)
        {
            throw Invalid($"{fieldName} must not exceed {maxLength} characters.");
        }

        return normalized;
    }

    private static void ValidatePaymentPermissions(
        InstallmentRepaymentClaimIdentity identity,
        PaymentMethodKind method)
    {
        var permissions = identity.PermissionCodes?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var methodPermission = method switch
        {
            PaymentMethodKind.Cash => Permissions.PosTerminal.Payment.TakeCash,
            PaymentMethodKind.Card => Permissions.PosTerminal.Payment.TakeCard,
            PaymentMethodKind.Voucher => Permissions.PosTerminal.Payment.TakeVoucher,
            _ => throw Invalid("method is invalid.")
        };
        if (permissions is null ||
            !permissions.Contains(Permissions.PosTerminal.Installments.AddRepayment) ||
            !permissions.Contains(Permissions.PosTerminal.Payment.Confirm) ||
            !permissions.Contains(methodPermission))
        {
            throw PermissionDenied("Verified cashier lacks the required payment permissions.");
        }
    }

    private static void EnsureServerVerifiablePaymentMethod(PaymentMethodKind method)
    {
        if (method == PaymentMethodKind.Card)
        {
            // 当前 Card 结果仅由客户端提交，服务端无法向 Square/Linkly 核验；在权威绑定完成前必须拒绝。
            throw PaymentMethodUnsupported("Card installment repayment is unavailable until provider evidence can be verified by the server.");
        }
    }

    private async Task<InstallmentRepaymentClaimRecord> RecordRecoveryCashierAsync(
        InstallmentRepaymentClaimRecord current,
        InstallmentRepaymentClaimIdentity normalizedIdentity,
        CancellationToken cancellationToken)
    {
        if (string.Equals(current.LastRecoveryCashierId, normalizedIdentity.CashierId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.LastRecoveryCashierName, normalizedIdentity.CashierName, StringComparison.Ordinal) &&
            string.Equals(current.LastRecoveryCashierUserGuid, normalizedIdentity.CashierUserGuid, StringComparison.OrdinalIgnoreCase) &&
            current.RecoveredAtUtc is not null)
        {
            return current;
        }

        var updated = current with
        {
            LastRecoveryCashierId = normalizedIdentity.CashierId,
            LastRecoveryCashierName = normalizedIdentity.CashierName,
            LastRecoveryCashierUserGuid = normalizedIdentity.CashierUserGuid,
            RecoveredAtUtc = _timeProvider.GetUtcNow(),
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
            Revision = current.Revision + 1
        };
        return await UpdateOrReloadAsync(current, updated, cancellationToken);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal RoundCurrency(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}
