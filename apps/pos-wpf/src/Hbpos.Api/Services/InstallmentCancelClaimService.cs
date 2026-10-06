using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlazorApp.Shared.Constants;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Services;

public static class InstallmentCancelClaimErrorCodes
{
    public const string Busy = "INSTALLMENT_MUTATION_BUSY";
    public const string RefundMethodUnsupported = "INSTALLMENT_CANCEL_REFUND_METHOD_UNSUPPORTED";
    public const string ClaimRequired = "INSTALLMENT_CANCEL_CLAIM_REQUIRED";
    public const string Mismatch = "INSTALLMENT_CANCEL_CLAIM_MISMATCH";
    public const string NotFound = "INSTALLMENT_CANCEL_CLAIM_NOT_FOUND";
    public const string Expired = "INSTALLMENT_CANCEL_CLAIM_EXPIRED";
    public const string Invalid = "INSTALLMENT_CANCEL_CLAIM_INVALID";
    public const string PermissionDenied = "INSTALLMENT_CANCEL_PERMISSION_DENIED";
}

public sealed class InstallmentCancelClaimException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class InstallmentCancelClaimOptions
{
    // 配置缺失时仍必须走 claim，防止旧 /cancel 路由因漏配而重新成为绕过入口。
    public bool Required { get; set; } = true;
}

public interface IInstallmentCancelClaimService
{
    Task<InstallmentCancelClaimDto> CreateAsync(
        Guid installmentGuid,
        InstallmentCancelClaimCreateRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken);

    Task<InstallmentCancelClaimDto> BeginRefundAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken);

    Task<InstallmentCancelClaimDto> GetAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken);

    Task<InstallmentCancelClaimDto> ResolveAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentCancelClaimResolveRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken);

    Task<InstallmentCancelClaimDto> CommitAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentCancelClaimCommitRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken);

    Task EnsureLegacyCancelAllowedAsync(Guid installmentGuid, CancellationToken cancellationToken);

    Task EnsureNoBlockingClaimAsync(Guid installmentGuid, CancellationToken cancellationToken);
}

public sealed class InstallmentCancelClaimService(
    IInstallmentCancelClaimRepository claimRepository,
    IInstallmentRepository installmentRepository,
    IInstallmentCancelClaimCommitRepository commitRepository,
    IOptions<InstallmentCancelClaimOptions> options,
    TimeProvider? timeProvider = null,
    IInstallmentRepaymentClaimService? repaymentClaimService = null,
    IOptions<InstallmentCrossDeviceLifecycleOptions>? lifecycleOptions = null) : IInstallmentCancelClaimService
{
    public const int PreparedClaimTtlSeconds = 120;
    private readonly InstallmentCancelClaimOptions _options = options.Value;
    private readonly InstallmentCrossDeviceLifecycleOptions _lifecycleOptions =
        lifecycleOptions?.Value ?? new InstallmentCrossDeviceLifecycleOptions();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<InstallmentCancelClaimDto> CreateAsync(
        Guid installmentGuid,
        InstallmentCancelClaimCreateRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken)
    {
        var normalizedIdentity = NormalizeIdentity(identity);
        ValidateCancelPermission(normalizedIdentity);
        var normalizedRequest = NormalizeCreateRequest(request);
        var existing = await claimRepository.GetAsync(normalizedRequest.OperationGuid, cancellationToken);
        if (existing is not null)
        {
            existing = await ExpirePreparedAsync(existing, cancellationToken);
            ValidateAccess(existing, installmentGuid, normalizedIdentity);
            ValidateImmutableRequest(existing, normalizedRequest);
            return Map(existing, alreadyExists: true);
        }

        var details = await GetRequiredInstallmentAsync(installmentGuid, cancellationToken);
        ValidateInstallmentForNewClaim(details, normalizedIdentity);
        var refundMode = InstallmentLifecycleRules.NormalizeRefundMode(normalizedRequest.RefundMode);
        InstallmentCancelRefundExecutionPolicy.Validate(details, refundMode);
        var authoritativeFingerprint = InstallmentCancelClaimFingerprint.Create(details);
        if (!string.Equals(
                authoritativeFingerprint,
                normalizedRequest.RefundPlanFingerprint,
                StringComparison.Ordinal))
        {
            throw Mismatch("refundPlanFingerprint does not match the current recorded installment payments.");
        }

        await EnsureNoBlockingClaimAsync(installmentGuid, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var claim = new InstallmentCancelClaimRecord(
            installmentGuid,
            normalizedRequest.OperationGuid,
            normalizedIdentity.StoreCode,
            normalizedIdentity.DeviceCode,
            normalizedIdentity.CashierId,
            normalizedIdentity.CashierName,
            normalizedRequest.IdempotencyKey,
            normalizedRequest.Reason,
            authoritativeFingerprint,
            InstallmentCancelClaimStatus.Prepared,
            now,
            now,
            now.AddSeconds(PreparedClaimTtlSeconds),
            CommittedAtUtc: null,
            Revision: 1,
            OriginalDeviceCode: details.DeviceCode,
            RefundMode: refundMode);
        if (await claimRepository.TryInsertAsync(claim, cancellationToken))
        {
            return Map(claim, alreadyExists: false);
        }

        existing = await claimRepository.GetAsync(normalizedRequest.OperationGuid, cancellationToken);
        if (existing is not null)
        {
            ValidateAccess(existing, installmentGuid, normalizedIdentity);
            ValidateImmutableRequest(existing, normalizedRequest);
            return Map(existing, alreadyExists: true);
        }

        throw Busy();
    }

    public async Task<InstallmentCancelClaimDto> BeginRefundAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken)
    {
        var current = await GetRequiredAccessibleClaimAsync(
            installmentGuid,
            operationGuid,
            identity,
            cancellationToken);
        current = await ExpirePreparedAsync(current, cancellationToken);
        var recoveryIdentity = NormalizeIdentity(identity);
        if (current.Status == InstallmentCancelClaimStatus.Prepared &&
            !string.Equals(current.CashierId, recoveryIdentity.CashierId, StringComparison.OrdinalIgnoreCase))
        {
            throw Mismatch("A prepared cancellation claim can only begin under its creating cashier.");
        }

        if (current.Status == InstallmentCancelClaimStatus.RefundPending)
        {
            await EnsureRefundExecutionSupportedAsync(installmentGuid, current.RefundMode, cancellationToken);
            current = await RecordRecoveryCashierAsync(current, recoveryIdentity, cancellationToken);
            return Map(current, alreadyExists: true);
        }

        if (current.Status == InstallmentCancelClaimStatus.Released &&
            current.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            throw new InstallmentCancelClaimException(
                InstallmentCancelClaimErrorCodes.Expired,
                "Prepared cancellation claim has expired.");
        }

        // Unknown 只能由同一 claim 显式恢复，不能超时释放或新建另一笔取消。
        if (current.Status is not InstallmentCancelClaimStatus.Prepared
            and not InstallmentCancelClaimStatus.Unknown)
        {
            throw Mismatch("Only a prepared or unknown cancellation claim can begin refund recovery.");
        }

        // Begin 成功是客户端调用退款 provider 的放行信号，必须基于最新权威账本再次复核。
        await EnsureRefundExecutionSupportedAsync(installmentGuid, current.RefundMode, cancellationToken);
        var updated = current with
        {
            Status = InstallmentCancelClaimStatus.RefundPending,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
            ExpiresAtUtc = null,
            LastRecoveryCashierId = recoveryIdentity.CashierId,
            LastRecoveryCashierName = recoveryIdentity.CashierName,
            LastRecoveryCashierUserGuid = recoveryIdentity.CashierUserGuid,
            RecoveredAtUtc = _timeProvider.GetUtcNow(),
            Revision = current.Revision + 1
        };
        updated = await UpdateOrReloadAsync(current, updated, cancellationToken);
        if (updated.Status != InstallmentCancelClaimStatus.RefundPending)
        {
            throw Mismatch("Cancellation claim changed while beginning refund recovery.");
        }

        return Map(updated, alreadyExists: false);
    }

    public async Task<InstallmentCancelClaimDto> GetAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken)
    {
        var current = await GetRequiredAccessibleClaimAsync(
            installmentGuid,
            operationGuid,
            identity,
            cancellationToken);
        current = await ExpirePreparedAsync(current, cancellationToken);
        if (current.Status is InstallmentCancelClaimStatus.RefundPending
            or InstallmentCancelClaimStatus.Unknown
            or InstallmentCancelClaimStatus.Committed)
        {
            current = await RecordRecoveryCashierAsync(
                current,
                NormalizeIdentity(identity),
                cancellationToken);
        }
        return Map(current, alreadyExists: true);
    }

    public async Task<InstallmentCancelClaimDto> ResolveAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentCancelClaimResolveRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken)
    {
        var current = await GetRequiredAccessibleClaimAsync(
            installmentGuid,
            operationGuid,
            identity,
            cancellationToken);
        current = await ExpirePreparedAsync(current, cancellationToken);
        var recoveryIdentity = NormalizeIdentity(identity);
        var targetStatus = request.Outcome switch
        {
            InstallmentCancelClaimResolveOutcome.Released => InstallmentCancelClaimStatus.Released,
            InstallmentCancelClaimResolveOutcome.Declined => InstallmentCancelClaimStatus.Declined,
            InstallmentCancelClaimResolveOutcome.Unknown => InstallmentCancelClaimStatus.Unknown,
            _ => throw Invalid("Unsupported cancellation claim outcome.")
        };
        if (current.Status == targetStatus)
        {
            return Map(current, alreadyExists: true);
        }

        if (request.Outcome == InstallmentCancelClaimResolveOutcome.Declined &&
            request.ApprovedRefunds is { Count: > 0 })
        {
            throw Invalid("A declined cancellation claim must declare zero approved refunds.");
        }

        var validTransition = request.Outcome switch
        {
            InstallmentCancelClaimResolveOutcome.Released =>
                current.Status == InstallmentCancelClaimStatus.Prepared,
            InstallmentCancelClaimResolveOutcome.Declined =>
                current.Status == InstallmentCancelClaimStatus.RefundPending,
            InstallmentCancelClaimResolveOutcome.Unknown =>
                current.Status == InstallmentCancelClaimStatus.RefundPending,
            _ => false
        };
        if (!validTransition)
        {
            throw Mismatch(request.Outcome == InstallmentCancelClaimResolveOutcome.Released
                ? "Only a prepared cancellation claim can be released."
                : "Only a refund-pending cancellation claim can be declined or marked unknown.");
        }

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
            throw Mismatch("Cancellation claim changed while resolving the refund outcome.");
        }

        return Map(updated, alreadyExists: false);
    }

    public async Task<InstallmentCancelClaimDto> CommitAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentCancelClaimCommitRequest request,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken)
    {
        var current = await GetRequiredAccessibleClaimAsync(
            installmentGuid,
            operationGuid,
            identity,
            cancellationToken);
        if (current.Status == InstallmentCancelClaimStatus.Committed)
        {
            current = await RecordRecoveryCashierAsync(
                current,
                NormalizeIdentity(identity),
                cancellationToken);
            return Map(current, alreadyExists: true);
        }

        if (current.Status != InstallmentCancelClaimStatus.RefundPending)
        {
            throw Mismatch("Only a refund-pending cancellation claim can be committed.");
        }

        if (request.Refunds is null || request.Refunds.Count == 0)
        {
            throw Invalid("refunds are required.");
        }

        var result = await commitRepository.CommitAsync(
            current,
            request,
            NormalizeIdentity(identity),
            _timeProvider.GetUtcNow(),
            cancellationToken);
        return Map(result.Claim, result.CommitResponse, result.AlreadyCancelled);
    }

    public async Task EnsureLegacyCancelAllowedAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        if (_options.Required)
        {
            throw new InstallmentCancelClaimException(
                InstallmentCancelClaimErrorCodes.ClaimRequired,
                "This server requires a cancellation claim before committing installment refunds.");
        }

        await EnsureNoBlockingClaimAsync(installmentGuid, cancellationToken);
    }

    public async Task EnsureNoBlockingClaimAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        if (repaymentClaimService is not null)
        {
            await repaymentClaimService.EnsureNoBlockingClaimAsync(installmentGuid, cancellationToken);
        }

        var blocking = await claimRepository.GetBlockingAsync(installmentGuid, cancellationToken);
        if (blocking is null)
        {
            return;
        }

        blocking = await ExpirePreparedAsync(blocking, cancellationToken);
        if (InstallmentCancelClaimRecord.IsBlocking(blocking.Status))
        {
            throw Busy();
        }
    }

    private async Task<InstallmentCancelClaimRecord> GetRequiredAccessibleClaimAsync(
        Guid installmentGuid,
        Guid operationGuid,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken)
    {
        if (operationGuid == Guid.Empty)
        {
            throw Invalid("operationGuid is required.");
        }

        var normalizedIdentity = NormalizeIdentity(identity);
        ValidateCancelPermission(normalizedIdentity);
        var current = await claimRepository.GetAsync(operationGuid, cancellationToken)
            ?? throw new InstallmentCancelClaimException(
                InstallmentCancelClaimErrorCodes.NotFound,
                "Cancellation claim was not found.");
        ValidateAccess(current, installmentGuid, normalizedIdentity);
        return current;
    }

    private async Task<InstallmentCancelClaimRecord> RecordRecoveryCashierAsync(
        InstallmentCancelClaimRecord current,
        InstallmentRepaymentClaimIdentity identity,
        CancellationToken cancellationToken)
    {
        if (string.Equals(current.LastRecoveryCashierId, identity.CashierId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.LastRecoveryCashierName, identity.CashierName, StringComparison.Ordinal) &&
            string.Equals(current.LastRecoveryCashierUserGuid, identity.CashierUserGuid, StringComparison.OrdinalIgnoreCase) &&
            current.RecoveredAtUtc is not null)
        {
            return current;
        }

        var now = _timeProvider.GetUtcNow();
        var updated = current with
        {
            LastRecoveryCashierId = identity.CashierId,
            LastRecoveryCashierName = identity.CashierName,
            LastRecoveryCashierUserGuid = identity.CashierUserGuid,
            RecoveredAtUtc = now,
            UpdatedAtUtc = now,
            Revision = current.Revision + 1
        };
        return await UpdateOrReloadAsync(current, updated, cancellationToken);
    }

    private async Task<InstallmentCancelClaimRecord> ExpirePreparedAsync(
        InstallmentCancelClaimRecord current,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (current.Status != InstallmentCancelClaimStatus.Prepared ||
            current.ExpiresAtUtc is null ||
            current.ExpiresAtUtc > now)
        {
            return current;
        }

        var released = current with
        {
            Status = InstallmentCancelClaimStatus.Released,
            UpdatedAtUtc = now,
            Revision = current.Revision + 1
        };
        return await UpdateOrReloadAsync(current, released, cancellationToken);
    }

    private async Task<InstallmentCancelClaimRecord> UpdateOrReloadAsync(
        InstallmentCancelClaimRecord current,
        InstallmentCancelClaimRecord updated,
        CancellationToken cancellationToken)
    {
        if (await claimRepository.TryUpdateAsync(updated, current.Revision, cancellationToken))
        {
            return updated;
        }

        return await claimRepository.GetAsync(current.OperationGuid, cancellationToken)
            ?? throw new InstallmentCancelClaimException(
                InstallmentCancelClaimErrorCodes.NotFound,
                "Cancellation claim disappeared during an update.");
    }

    private async Task<InstallmentDetailsDto> GetRequiredInstallmentAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        return await installmentRepository.GetDetailsAsync(installmentGuid, cancellationToken)
            ?? throw new InstallmentCancelClaimException(
                InstallmentCancelClaimErrorCodes.NotFound,
                "Installment was not found.");
    }

    private async Task EnsureRefundExecutionSupportedAsync(
        Guid installmentGuid,
        InstallmentCancelRefundMode refundMode,
        CancellationToken cancellationToken)
    {
        var details = await GetRequiredInstallmentAsync(installmentGuid, cancellationToken);
        InstallmentCancelRefundExecutionPolicy.Validate(details, refundMode);
    }

    private void ValidateInstallmentForNewClaim(
        InstallmentDetailsDto details,
        InstallmentRepaymentClaimIdentity identity)
    {
        if (!string.Equals(details.StoreCode, identity.StoreCode, StringComparison.OrdinalIgnoreCase))
        {
            throw Mismatch("Installment does not belong to the authenticated store.");
        }

        if (!string.Equals(details.DeviceCode, identity.DeviceCode, StringComparison.OrdinalIgnoreCase) &&
            !_lifecycleOptions.CancelRefundEnabled)
        {
            throw Mismatch("Cross-device installment cancellation refund is disabled.");
        }

        if (!InstallmentLifecycleRules.CanCancelWithRefund(details.Status, details.BalanceAmount))
        {
            throw Mismatch("Only active or paid-off installments that have not been picked up can start a cancellation claim.");
        }
    }

    private static void ValidateAccess(
        InstallmentCancelClaimRecord claim,
        Guid installmentGuid,
        InstallmentRepaymentClaimIdentity identity)
    {
        if (claim.InstallmentGuid != installmentGuid ||
            !string.Equals(claim.StoreCode, identity.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(claim.ClaimantDeviceCode, identity.DeviceCode, StringComparison.OrdinalIgnoreCase))
        {
            throw Mismatch("Authenticated identity does not match the cancellation claim.");
        }
    }

    private static void ValidateImmutableRequest(
        InstallmentCancelClaimRecord claim,
        InstallmentCancelClaimCreateRequest request)
    {
        if (!string.Equals(claim.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(claim.Reason, request.Reason, StringComparison.Ordinal) ||
            !string.Equals(claim.RefundPlanFingerprint, request.RefundPlanFingerprint, StringComparison.Ordinal) ||
            claim.RefundMode != InstallmentLifecycleRules.NormalizeRefundMode(request.RefundMode))
        {
            throw Mismatch("operationGuid is already bound to different cancellation facts.");
        }
    }

    private static InstallmentCancelClaimCreateRequest NormalizeCreateRequest(
        InstallmentCancelClaimCreateRequest request)
    {
        if (request.OperationGuid == Guid.Empty)
        {
            throw Invalid("operationGuid is required.");
        }

        var idempotencyKey = NormalizeRequired(request.IdempotencyKey, "idempotencyKey", 100);
        var reason = NormalizeOptional(request.Reason, 500, "reason");
        var refundPlanFingerprint = NormalizeRequired(
            request.RefundPlanFingerprint,
            "refundPlanFingerprint",
            128);
        if (!Regex.IsMatch(refundPlanFingerprint, "^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant))
        {
            throw Invalid("refundPlanFingerprint must be a lowercase sha256 digest.");
        }

        InstallmentCancelRefundMode refundMode;
        try
        {
            refundMode = InstallmentLifecycleRules.NormalizeRefundMode(request.RefundMode);
        }
        catch (InvalidOperationException ex)
        {
            throw Invalid(ex.Message);
        }

        return request with
        {
            IdempotencyKey = idempotencyKey,
            Reason = reason,
            RefundPlanFingerprint = refundPlanFingerprint,
            RefundMode = refundMode
        };
    }

    private static InstallmentRepaymentClaimIdentity NormalizeIdentity(
        InstallmentRepaymentClaimIdentity identity)
    {
        return new InstallmentRepaymentClaimIdentity(
            NormalizeRequired(identity.StoreCode, "authenticated storeCode", 50),
            NormalizeRequired(identity.DeviceCode, "authenticated deviceCode", 50),
            NormalizeRequired(identity.CashierId, "verified cashierId", 50),
            NormalizeRequired(identity.CashierName, "verified cashierName", 100),
            identity.PermissionCodes,
            NormalizeRequired(identity.CashierUserGuid, "verified cashierUserGuid", 50));
    }

    private static void ValidateCancelPermission(InstallmentRepaymentClaimIdentity identity)
    {
        if (identity.PermissionCodes is null ||
            !identity.PermissionCodes.Contains(
                Permissions.PosTerminal.Installments.Cancel,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InstallmentCancelClaimException(
                InstallmentCancelClaimErrorCodes.PermissionDenied,
                "The verified cashier does not currently have installment cancellation permission.");
        }
    }

    private static InstallmentCancelClaimDto Map(
        InstallmentCancelClaimRecord claim,
        bool alreadyExists)
    {
        InstallmentCancelClaimCommitResponse? commit = null;
        if (claim.Status == InstallmentCancelClaimStatus.Committed)
        {
            if (string.IsNullOrWhiteSpace(claim.CommitResponseJson))
            {
                throw Mismatch("Committed cancellation claim has no persisted commit response.");
            }

            try
            {
                commit = JsonSerializer.Deserialize<InstallmentCancelClaimCommitResponse>(
                    claim.CommitResponseJson,
                    InstallmentCancelClaimCommitRepositoryJson.Options);
            }
            catch (JsonException)
            {
                throw Mismatch("Persisted cancellation commit response is invalid.");
            }

            if (commit is null)
            {
                throw Mismatch("Persisted cancellation commit response does not match the claim.");
            }

            InstallmentCancelClaimCommitSnapshotValidator.Validate(claim, commit);
        }

        return Map(claim, commit, alreadyExists);
    }

    private static InstallmentCancelClaimDto Map(
        InstallmentCancelClaimRecord claim,
        InstallmentCancelClaimCommitResponse? commit,
        bool alreadyExists) => new(
            claim.InstallmentGuid,
            claim.OperationGuid,
            claim.IdempotencyKey,
            claim.RefundPlanFingerprint,
            claim.Status,
            claim.CreatedAtUtc,
            claim.UpdatedAtUtc,
            claim.ExpiresAtUtc,
            commit,
            alreadyExists,
            string.IsNullOrWhiteSpace(claim.OriginalDeviceCode)
                ? claim.ClaimantDeviceCode
                : claim.OriginalDeviceCode,
            claim.ClaimantDeviceCode,
            claim.RefundMode);

    private static InstallmentCancelClaimException Busy() => new(
        InstallmentCancelClaimErrorCodes.Busy,
        "Another installment mutation is already in progress.");

    private static InstallmentCancelClaimException Mismatch(string message) => new(
        InstallmentCancelClaimErrorCodes.Mismatch,
        message);

    private static InstallmentCancelClaimException Invalid(string message) => new(
        InstallmentCancelClaimErrorCodes.Invalid,
        message);

    private static string NormalizeRequired(string? value, string fieldName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"{fieldName} is required.");
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw Invalid($"{fieldName} must not exceed {maxLength} characters.");
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maxLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw Invalid($"{fieldName} must not exceed {maxLength} characters.");
        }

        return normalized;
    }
}

internal static class InstallmentCancelRefundExecutionPolicy
{
    internal static void Validate(InstallmentDetailsDto details, InstallmentCancelRefundMode refundMode)
    {
        if (refundMode == InstallmentCancelRefundMode.Voucher)
        {
            // 全部改发退款代金券：每笔退款都有服务端发券凭据可核验，刷卡原付款也能在同一原子提交里落账。
            return;
        }

        var unsupported = details.Payments.FirstOrDefault(payment =>
            payment.Status == InstallmentPaymentStatus.Recorded &&
            payment.Amount > 0m &&
            payment.Method is not PaymentMethodKind.Cash and not PaymentMethodKind.Voucher);
        if (unsupported is null)
        {
            return;
        }

        // 当前原子 commit 只接受现金和有服务端凭据的代金券；未知新支付方式同样必须 fail closed。
        throw new InstallmentCancelClaimException(
            InstallmentCancelClaimErrorCodes.RefundMethodUnsupported,
            $"Cancellation refunds for {unsupported.Method} original payments are not supported by the current server commit path.");
    }
}

internal static class InstallmentCancelClaimCommitSnapshotValidator
{
    internal static void Validate(
        InstallmentCancelClaimRecord claim,
        InstallmentCancelClaimCommitResponse response)
    {
        var details = response.Details;
        var invalidReason = details.InstallmentGuid != claim.InstallmentGuid
            ? "installment"
            : !string.Equals(details.StoreCode, claim.StoreCode, StringComparison.OrdinalIgnoreCase)
                ? "store"
            : !string.Equals(
                details.DeviceCode,
                string.IsNullOrWhiteSpace(claim.OriginalDeviceCode)
                    ? claim.ClaimantDeviceCode
                    : claim.OriginalDeviceCode,
                StringComparison.OrdinalIgnoreCase)
                ? "original-device"
            : details.Status != InstallmentStatus.Cancelled
                ? "status"
            : details.CancellationInfo?.Kind != InstallmentCancellationKind.RefundCancel
                ? "cancellation-kind"
            : !string.Equals(details.CancellationInfo.IdempotencyKey, claim.IdempotencyKey, StringComparison.Ordinal)
                ? "idempotency-key"
            : null;
        if (invalidReason is not null)
        {
            throw new InstallmentCancelClaimException(
                InstallmentCancelClaimErrorCodes.Mismatch,
                $"Persisted cancellation commit snapshot has invalid scope or status ({invalidReason}).");
        }

        var originalPayments = details.Payments
            .Where(payment => payment.Status == InstallmentPaymentStatus.Recorded && payment.Amount > 0m)
            .ToArray();
        var paidByMethod = originalPayments
            .GroupBy(payment => payment.Method)
            .ToDictionary(group => group.Key, group => RoundCurrency(group.Sum(payment => payment.Amount)));
        var refunds = details.Payments
            .Where(payment => payment.Status == InstallmentPaymentStatus.Recorded && payment.Amount < 0m)
            .ToArray();
        var expectedRefundKeys = originalPayments
            .Select(payment => $"{claim.OperationGuid:D}:refund:{payment.PaymentGuid:D}")
            .ToHashSet(StringComparer.Ordinal);
        if (refunds.Length == 0 || refunds.Length != expectedRefundKeys.Count ||
            refunds.Any(refund =>
                string.IsNullOrWhiteSpace(refund.IdempotencyKey) ||
                !expectedRefundKeys.Remove(refund.IdempotencyKey)))
        {
            throw new InstallmentCancelClaimException(
                InstallmentCancelClaimErrorCodes.Mismatch,
                "Persisted cancellation commit snapshot has no claim-bound refunds.");
        }

        var refundByMethod = refunds
            .GroupBy(refund => refund.Method)
            .ToDictionary(group => group.Key, group => RoundCurrency(-group.Sum(refund => refund.Amount)));
        if (!InstallmentLifecycleRules.RefundTotalsMatch(paidByMethod, refundByMethod, claim.RefundMode))
        {
            throw new InstallmentCancelClaimException(
                InstallmentCancelClaimErrorCodes.Mismatch,
                "Persisted cancellation commit snapshot refund facts do not match the original tenders.");
        }
    }

    private static decimal RoundCurrency(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}

internal static class InstallmentCancelClaimFingerprint
{
    internal static string Create(InstallmentDetailsDto details)
    {
        var payments = details.Payments
            .Where(payment =>
                payment.Status == InstallmentPaymentStatus.Recorded &&
                payment.Amount > 0m)
            .Select(payment => new
            {
                PaymentGuid = payment.PaymentGuid.ToString("D"),
                Method = MethodName(payment.Method),
                AmountCents = ToCents(payment.Amount)
            })
            .OrderBy(payment => payment.PaymentGuid, StringComparer.Ordinal)
            .ToArray();
        if (payments.Length == 0)
        {
            throw new InstallmentCancelClaimException(
                InstallmentCancelClaimErrorCodes.Mismatch,
                "Installment has no refundable recorded payments.");
        }

        // 手工拼接固定字段顺序与无空格 JSON，精确复现 iPad JSON.stringify 的 canonical material。
        var material = new StringBuilder();
        material.Append("{\"installmentGuid\":\"")
            .Append(details.InstallmentGuid.ToString("D"))
            .Append("\",\"payments\":[");
        for (var index = 0; index < payments.Length; index++)
        {
            if (index > 0)
            {
                material.Append(',');
            }

            var payment = payments[index];
            material.Append("[\"")
                .Append(payment.PaymentGuid)
                .Append("\",\"")
                .Append(payment.Method)
                .Append("\",")
                .Append(payment.AmountCents)
                .Append(']');
        }

        material.Append("]}");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())))
            .ToLowerInvariant();
        return $"sha256:{digest}";
    }

    private static string MethodName(PaymentMethodKind method) => method switch
    {
        PaymentMethodKind.Cash => "cash",
        PaymentMethodKind.Card => "card",
        PaymentMethodKind.Voucher => "voucher",
        _ => throw new InstallmentCancelClaimException(
            InstallmentCancelClaimErrorCodes.Mismatch,
            "Recorded installment payment method is invalid.")
    };

    private static long ToCents(decimal amount)
    {
        var cents = amount * 100m;
        if (cents != decimal.Truncate(cents))
        {
            throw new InstallmentCancelClaimException(
                InstallmentCancelClaimErrorCodes.Mismatch,
                "Recorded installment payment amount has fractional cents.");
        }

        return checked(decimal.ToInt64(cents));
    }
}
