using System.Data;
using System.Text;
using System.Text.Json;
using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Data;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using SqlSugar;

namespace Hbpos.Api.Services;

public sealed record InstallmentCancelClaimCommitResult(
    InstallmentCancelClaimRecord Claim,
    InstallmentCancelClaimCommitResponse CommitResponse,
    bool AlreadyCancelled);

public interface IInstallmentCancelClaimCommitRepository
{
    Task<InstallmentCancelClaimCommitResult> CommitAsync(
        InstallmentCancelClaimRecord expectedClaim,
        InstallmentCancelClaimCommitRequest request,
        InstallmentRepaymentClaimIdentity identity,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken);
}

public interface IInstallmentCancelClaimCommitFaultInjector
{
    Task AfterRefundsInsertedAsync(CancellationToken cancellationToken);
}

public sealed class NoOpInstallmentCancelClaimCommitFaultInjector
    : IInstallmentCancelClaimCommitFaultInjector
{
    public Task AfterRefundsInsertedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class InstallmentCancelClaimCommitRepositoryJson
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

public sealed class SqlSugarInstallmentCancelClaimCommitRepository(
    HbposSqlSugarContext dbContext,
    IInstallmentRepository installmentRepository,
    IInstallmentCancelClaimCommitFaultInjector faultInjector)
    : IInstallmentCancelClaimCommitRepository
{
    public async Task<InstallmentCancelClaimCommitResult> CommitAsync(
        InstallmentCancelClaimRecord expectedClaim,
        InstallmentCancelClaimCommitRequest request,
        InstallmentRepaymentClaimIdentity identity,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = dbContext.PosmDb;
        await using var processLock = await InstallmentMutationLock.AcquireProcessAsync(
            expectedClaim.InstallmentGuid,
            cancellationToken);
        await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
        try
        {
            await InstallmentMutationLock.AcquireDatabaseAsync(db, expectedClaim.InstallmentGuid);
            var order = await InstallmentMutationLock.LockOrderAsync(
                db,
                expectedClaim.InstallmentGuid,
                cancellationToken)
                ?? throw NotFound("Installment was not found while committing the cancellation claim.");
            var claim = await LockClaimAsync(db, expectedClaim.OperationGuid, cancellationToken)
                ?? throw NotFound("Cancellation claim was not found while committing.");
            ValidateImmutableClaim(claim, expectedClaim);
            var status = ParseStatus(claim.Status);
            if (status == InstallmentCancelClaimStatus.Committed)
            {
                if (!SameRecoveryIdentity(claim, identity))
                {
                    var replayRevision = claim.Revision;
                    var replayAffected = await db.Updateable<InstallmentCancelClaimEntity>()
                        .SetColumns(entity => entity.LastRecoveryCashierId == identity.CashierId)
                        .SetColumns(entity => entity.LastRecoveryCashierName == identity.CashierName)
                        .SetColumns(entity => entity.LastRecoveryCashierUserGuid == identity.CashierUserGuid)
                        .SetColumns(entity => entity.RecoveredAtUtc == committedAtUtc.UtcDateTime)
                        .SetColumns(entity => entity.UpdatedAtUtc == committedAtUtc.UtcDateTime)
                        .SetColumns(entity => entity.Revision == replayRevision + 1)
                        .Where(entity => entity.OperationGuid == claim.OperationGuid)
                        .Where(entity => entity.Revision == replayRevision)
                        .Where(entity => entity.Status == InstallmentCancelClaimStatus.Committed.ToString())
                        .ExecuteCommandAsync(cancellationToken);
                    if (replayAffected != 1)
                    {
                        throw Mismatch("Committed cancellation claim changed during recovery audit update.");
                    }

                    claim.LastRecoveryCashierId = identity.CashierId;
                    claim.LastRecoveryCashierName = identity.CashierName;
                    claim.LastRecoveryCashierUserGuid = identity.CashierUserGuid;
                    claim.RecoveredAtUtc = committedAtUtc.UtcDateTime;
                    claim.UpdatedAtUtc = committedAtUtc.UtcDateTime;
                    claim.Revision = replayRevision + 1;
                }

                var committedClaim = Map(claim);
                var replay = DeserializeCommitResponse(claim, committedClaim);
                await db.Ado.CommitTranAsync();
                return new InstallmentCancelClaimCommitResult(
                    committedClaim,
                    replay,
                    replay.AlreadyCancelled);
            }

            if (status != InstallmentCancelClaimStatus.RefundPending)
            {
                throw Mismatch("Only a refund-pending cancellation claim can be committed.");
            }

            if (!InstallmentLifecycleRules.CanCancelWithRefund(order.Status, order.BalanceAmount) ||
                !string.Equals(order.StoreCode, claim.StoreCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    order.DeviceCode,
                    string.IsNullOrWhiteSpace(claim.OriginalDeviceCode)
                        ? claim.ClaimantDeviceCode
                        : claim.OriginalDeviceCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Mismatch("Installment state or original device scope no longer matches the cancellation claim.");
            }

            var lockedStatus = order.Status;

            var details = await installmentRepository.GetDetailsAsync(
                expectedClaim.InstallmentGuid,
                cancellationToken)
                ?? throw NotFound("Installment disappeared while committing the cancellation claim.");
            // 即使上游预检被旧客户端绕过，原子提交仍必须拒绝当前无法验证的退款方式。
            InstallmentCancelRefundExecutionPolicy.Validate(details, expectedClaim.RefundMode);
            if (!string.Equals(
                    InstallmentCancelClaimFingerprint.Create(details),
                    claim.RefundPlanFingerprint,
                    StringComparison.Ordinal))
            {
                throw Mismatch("Recorded installment payments changed after the cancellation claim was prepared.");
            }

            var cancelRequest = new InstallmentCancelRequest(
                claim.InstallmentGuid,
                claim.StoreCode,
                string.IsNullOrWhiteSpace(claim.OriginalDeviceCode)
                    ? claim.ClaimantDeviceCode
                    : claim.OriginalDeviceCode,
                identity.CashierId,
                identity.CashierName,
                committedAtUtc,
                request.Refunds,
                claim.Reason,
                claim.IdempotencyKey,
                expectedClaim.RefundMode);
            IReadOnlyList<InstallmentRefundPaymentCommandDto> normalizedRefunds;
            try
            {
                normalizedRefunds = InstallmentService.NormalizeAndValidateRefunds(details, cancelRequest);
            }
            catch (InvalidOperationException ex)
            {
                throw Invalid(ex.Message);
            }

            var refundBindings = ValidateRefundBindings(claim, details, normalizedRefunds, expectedClaim.RefundMode);
            await ValidateRefundEvidenceAsync(db, claim, refundBindings, cancellationToken);
            var refundPayments = normalizedRefunds
                .Select(refund => InstallmentService.MapRefundPayment(
                    refund,
                    identity.CashierId,
                    identity.CashierName,
                    claim.ClaimantDeviceCode,
                    committedAtUtc))
                .ToArray();
            await EnsureRefundGuidsAreUnusedAsync(db, refundPayments, cancellationToken);
            foreach (var refund in refundPayments)
            {
                await db.Insertable(MapPayment(claim.InstallmentGuid, refund))
                    .ExecuteCommandAsync(cancellationToken);
            }

            // 故障注入点证明退款行、订单取消和 claim 终态由同一事务共同回滚。
            await faultInjector.AfterRefundsInsertedAsync(cancellationToken);

            var installmentGuidText = claim.InstallmentGuid.ToString("D");
            var paidAmount = RoundCurrency(await db.Queryable<InstallmentPaymentEntity>()
                .Where(payment =>
                    payment.InstallmentGuid == installmentGuidText &&
                    payment.Status == (int)InstallmentPaymentStatus.Recorded)
                .SumAsync(payment => payment.Amount));
            var affectedOrders = await db.Updateable<InstallmentOrderEntity>()
                .SetColumns(entity => entity.PaidAmount == paidAmount)
                .SetColumns(entity => entity.BalanceAmount == 0m)
                .SetColumns(entity => entity.Status == (int)InstallmentStatus.Cancelled)
                .SetColumns(entity => entity.CancellationKind == (int)InstallmentCancellationKind.RefundCancel)
                .SetColumns(entity => entity.CancelledAt == committedAtUtc.UtcDateTime)
                .SetColumns(entity => entity.CancelledBy == identity.CashierName)
                .SetColumns(entity => entity.CancellationReason == claim.Reason)
                .SetColumns(entity => entity.CancellationIdempotencyKey == claim.IdempotencyKey)
                .SetColumns(entity => entity.UpdatedAt == committedAtUtc.UtcDateTime)
                .Where(entity => entity.InstallmentGuid == installmentGuidText)
                // 乐观并发：只允许从锁内读到的那个状态（进行中或已付清）转为取消，期间被提货等改动则失败。
                .Where(entity => entity.Status == lockedStatus)
                .ExecuteCommandAsync(cancellationToken);
            if (affectedOrders != 1)
            {
                throw Mismatch("Installment changed during atomic cancellation commit.");
            }

            var committedDetails = await installmentRepository.GetDetailsAsync(
                expectedClaim.InstallmentGuid,
                cancellationToken)
                ?? throw NotFound("Installment disappeared after cancellation commit.");
            var commitResponse = new InstallmentCancelClaimCommitResponse(
                committedDetails,
                AlreadyCancelled: false);
            InstallmentCancelClaimCommitSnapshotValidator.Validate(expectedClaim, commitResponse);
            var commitResponseJson = JsonSerializer.Serialize(
                commitResponse,
                InstallmentCancelClaimCommitRepositoryJson.Options);
            var revision = claim.Revision;
            var affectedClaims = await db.Updateable<InstallmentCancelClaimEntity>()
                .SetColumns(entity => entity.Status == InstallmentCancelClaimStatus.Committed.ToString())
                .SetColumns(entity => entity.IsBlocking == false)
                .SetColumns(entity => entity.UpdatedAtUtc == committedAtUtc.UtcDateTime)
                .SetColumns(entity => entity.ExpiresAtUtc == null)
                .SetColumns(entity => entity.CommittedAtUtc == committedAtUtc.UtcDateTime)
                .SetColumns(entity => entity.CommitResponseJson == commitResponseJson)
                .SetColumns(entity => entity.LastRecoveryCashierId == identity.CashierId)
                .SetColumns(entity => entity.LastRecoveryCashierName == identity.CashierName)
                .SetColumns(entity => entity.LastRecoveryCashierUserGuid == identity.CashierUserGuid)
                .SetColumns(entity => entity.RecoveredAtUtc == committedAtUtc.UtcDateTime)
                .SetColumns(entity => entity.Revision == revision + 1)
                .Where(entity => entity.OperationGuid == claim.OperationGuid)
                .Where(entity => entity.Revision == revision)
                .Where(entity => entity.Status == InstallmentCancelClaimStatus.RefundPending.ToString())
                .ExecuteCommandAsync(cancellationToken);
            if (affectedClaims != 1)
            {
                throw Mismatch("Cancellation claim changed during atomic commit.");
            }

            claim.Status = InstallmentCancelClaimStatus.Committed.ToString();
            claim.IsBlocking = false;
            claim.UpdatedAtUtc = committedAtUtc.UtcDateTime;
            claim.ExpiresAtUtc = null;
            claim.CommittedAtUtc = committedAtUtc.UtcDateTime;
            claim.CommitResponseJson = commitResponseJson;
            claim.LastRecoveryCashierId = identity.CashierId;
            claim.LastRecoveryCashierName = identity.CashierName;
            claim.LastRecoveryCashierUserGuid = identity.CashierUserGuid;
            claim.RecoveredAtUtc = committedAtUtc.UtcDateTime;
            claim.Revision = revision + 1;
            var committed = Map(claim);
            await db.Ado.CommitTranAsync();
            return new InstallmentCancelClaimCommitResult(committed, commitResponse, false);
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    private static async Task<InstallmentCancelClaimEntity?> LockClaimAsync(
        ISqlSugarClient db,
        Guid operationGuid,
        CancellationToken cancellationToken)
    {
        var query = db.Queryable<InstallmentCancelClaimEntity>()
            .Where(entity => entity.OperationGuid == operationGuid);
        if (db.CurrentConnectionConfig.DbType == SqlSugar.DbType.SqlServer)
        {
            query = query.With(SqlWith.UpdLock);
        }

        return await query.FirstAsync(cancellationToken);
    }

    private static async Task EnsureRefundGuidsAreUnusedAsync(
        ISqlSugarClient db,
        IReadOnlyList<InstallmentPaymentDto> refunds,
        CancellationToken cancellationToken)
    {
        foreach (var refund in refunds)
        {
            var paymentGuid = refund.PaymentGuid.ToString("D");
            var query = db.Queryable<InstallmentPaymentEntity>()
                .Where(payment => payment.PaymentGuid == paymentGuid);
            if (db.CurrentConnectionConfig.DbType == SqlSugar.DbType.SqlServer)
            {
                query = query.With(SqlWith.UpdLock);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (await query.AnyAsync())
            {
                throw Mismatch("Refund paymentGuid is already in use.");
            }
        }
    }

    private static IReadOnlyList<RefundBinding> ValidateRefundBindings(
        InstallmentCancelClaimEntity claim,
        InstallmentDetailsDto details,
        IReadOnlyList<InstallmentRefundPaymentCommandDto> refunds,
        InstallmentCancelRefundMode refundMode)
    {
        if (refunds.Select(refund => refund.PaymentGuid).Distinct().Count() != refunds.Count)
        {
            throw Invalid("Refund paymentGuid values must be unique.");
        }

        if (refunds.Any(refund => refund.PaymentGuid == Guid.Empty))
        {
            throw Invalid("Refund paymentGuid is required.");
        }

        var originals = details.Payments
            .Where(payment =>
                payment.Status == InstallmentPaymentStatus.Recorded &&
                payment.Amount > 0m)
            .ToDictionary(payment => payment.PaymentGuid);
        if (refunds.Count != originals.Count ||
            refunds.Select(refund => refund.OriginalPaymentGuid).Distinct().Count() != refunds.Count)
        {
            throw Mismatch("Refunds must map one-to-one to every original recorded payment.");
        }

        var idempotencyKeys = new HashSet<string>(StringComparer.Ordinal);
        var bindings = new List<RefundBinding>(refunds.Count);
        foreach (var refund in refunds)
        {
            if (refund.OriginalPaymentGuid == Guid.Empty ||
                !originals.TryGetValue(refund.OriginalPaymentGuid, out var original))
            {
                throw Mismatch("Refund originalPaymentGuid does not identify a refundable payment.");
            }

            // 每笔原付款仍一对一退回原金额；退款方式由 claim 固定（原路或统一改发代金券）。
            if (refund.Method != InstallmentLifecycleRules.ResolveRefundMethod(original.Method, refundMode) ||
                RoundCurrency(refund.Amount) != RoundCurrency(original.Amount))
            {
                throw Mismatch("Refund method or amount does not match its original payment.");
            }

            var expectedIdempotencyKey = $"{claim.OperationGuid:D}:refund:{original.PaymentGuid:D}";
            if (!string.Equals(refund.IdempotencyKey?.Trim(), expectedIdempotencyKey, StringComparison.Ordinal) ||
                !idempotencyKeys.Add(expectedIdempotencyKey))
            {
                throw Mismatch("Refund idempotencyKey does not match its original payment and cancellation operation.");
            }

            bindings.Add(new RefundBinding(refund, original));
        }

        return bindings;
    }

    private static async Task ValidateRefundEvidenceAsync(
        ISqlSugarClient db,
        InstallmentCancelClaimEntity claim,
        IReadOnlyList<RefundBinding> bindings,
        CancellationToken cancellationToken)
    {
        foreach (var binding in bindings)
        {
            switch (binding.Refund.Method)
            {
                case PaymentMethodKind.Cash:
                    RejectCardEvidence(binding.Refund, "Cash refund");
                    break;
                case PaymentMethodKind.Card:
                    RejectClientSuppliedCardRefundEvidence();
                    break;
                case PaymentMethodKind.Voucher:
                    RejectCardEvidence(binding.Refund, "Voucher refund");
                    await ValidateVoucherRefundEvidenceAsync(db, claim, binding.Refund, cancellationToken);
                    break;
                default:
                    throw Invalid("Refund method is invalid.");
            }
        }
    }

    private static void RejectClientSuppliedCardRefundEvidence()
    {
        // CardTransactions 来自客户端请求，不能作为扣款或退款已经成功的可信凭据。
        // 当前取消 claim 提交链路尚未把 Square/Linkly 的服务端退款回执接入同一事务，
        // 因此宁可拒绝卡退款，也绝不接受可伪造的客户端结果。
        throw Invalid("Card refund requires server-verified provider evidence and is not supported by this commit path.");
    }

    private static async Task ValidateVoucherRefundEvidenceAsync(
        ISqlSugarClient db,
        InstallmentCancelClaimEntity claim,
        InstallmentRefundPaymentCommandDto refund,
        CancellationToken cancellationToken)
    {
        const string referencePrefix = "VOUCHER_REFUND:";
        var reference = refund.Reference?.Trim();
        if (reference?.StartsWith(referencePrefix, StringComparison.OrdinalIgnoreCase) != true)
        {
            throw Invalid("Voucher refund reference is invalid.");
        }

        var voucherCode = reference[referencePrefix.Length..].Trim();
        if (voucherCode.Length == 0)
        {
            throw Invalid("Voucher refund code is required.");
        }

        var idempotencyKey = refund.IdempotencyKey!;
        var marker = StoreVoucherRefundMarker.Create(idempotencyKey);
        var voucher = await db.Queryable<StoreVoucher>()
            .Where(entity => entity.VoucherCode == voucherCode)
            .Where(entity => entity.StoreCode == claim.StoreCode)
            .Where(entity => entity.IsDelete == null || entity.IsDelete == false)
            .FirstAsync(cancellationToken);
        if (voucher is null ||
            RoundCurrency(voucher.Amount ?? 0m) != RoundCurrency(refund.Amount) ||
            RoundCurrency(voucher.RemainingAmount ?? 0m) != RoundCurrency(refund.Amount) ||
            voucher.VoucherType != 3 ||
            !string.Equals(voucher.Status, "1", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(voucher.Remark) ||
            !StoreVoucherRefundMarker.HasCanonicalPrefix(voucher.Remark, marker))
        {
            throw Invalid("Voucher refund has no matching server-issued voucher evidence.");
        }
    }

    private static void RejectCardEvidence(InstallmentRefundPaymentCommandDto refund, string label)
    {
        if (refund.CardTransactions is { Count: > 0 })
        {
            throw Invalid($"{label} must not contain card transaction evidence.");
        }
    }

    private sealed record RefundBinding(
        InstallmentRefundPaymentCommandDto Refund,
        InstallmentPaymentDto Original);


    private static InstallmentPaymentEntity MapPayment(
        Guid installmentGuid,
        InstallmentPaymentDto payment) => new()
    {
        PaymentGuid = payment.PaymentGuid.ToString("D"),
        InstallmentGuid = installmentGuid.ToString("D"),
        Method = (int)payment.Method,
        Amount = payment.Amount,
        Reference = payment.Reference,
        Status = (int)payment.Status,
        RecordedAt = payment.RecordedAt.UtcDateTime,
        CashierId = payment.CashierId,
        CashierName = payment.CashierName,
        DeviceCode = payment.DeviceCode,
        CardTransactionsJson = payment.CardTransactions is null
            ? null
            : JsonSerializer.Serialize(payment.CardTransactions, InstallmentCancelClaimCommitRepositoryJson.Options),
        IdempotencyKey = payment.IdempotencyKey
    };

    private static void ValidateImmutableClaim(
        InstallmentCancelClaimEntity actual,
        InstallmentCancelClaimRecord expected)
    {
        if (actual.InstallmentGuid != expected.InstallmentGuid ||
            actual.OperationGuid != expected.OperationGuid ||
            !string.Equals(actual.StoreCode, expected.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                string.IsNullOrWhiteSpace(actual.OriginalDeviceCode) ? actual.ClaimantDeviceCode : actual.OriginalDeviceCode,
                string.IsNullOrWhiteSpace(expected.OriginalDeviceCode)
                    ? expected.ClaimantDeviceCode
                    : expected.OriginalDeviceCode,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.ClaimantDeviceCode, expected.ClaimantDeviceCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.CashierId, expected.CashierId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.CashierName, expected.CashierName, StringComparison.Ordinal) ||
            !string.Equals(actual.IdempotencyKey, expected.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(actual.Reason, expected.Reason, StringComparison.Ordinal) ||
            !string.Equals(actual.RefundPlanFingerprint, expected.RefundPlanFingerprint, StringComparison.Ordinal) ||
            InstallmentCancelClaimRecord.ParseStoredRefundMode(actual.RefundMode) != expected.RefundMode)
        {
            throw Mismatch("Stored cancellation claim no longer matches its immutable facts.");
        }
    }

    private static InstallmentCancelClaimRecord Map(InstallmentCancelClaimEntity entity) => new(
        entity.InstallmentGuid,
        entity.OperationGuid,
        entity.StoreCode,
        entity.ClaimantDeviceCode,
        entity.CashierId,
        entity.CashierName,
        entity.IdempotencyKey,
        entity.Reason,
        entity.RefundPlanFingerprint,
        ParseStatus(entity.Status),
        ToUtc(entity.CreatedAtUtc),
        ToUtc(entity.UpdatedAtUtc),
        entity.ExpiresAtUtc is null ? null : ToUtc(entity.ExpiresAtUtc.Value),
        entity.CommittedAtUtc is null ? null : ToUtc(entity.CommittedAtUtc.Value),
        entity.Revision,
        entity.CommitResponseJson,
        entity.LastRecoveryCashierId,
        entity.LastRecoveryCashierName,
        entity.LastRecoveryCashierUserGuid,
        entity.RecoveredAtUtc is null ? null : ToUtc(entity.RecoveredAtUtc.Value),
        entity.OriginalDeviceCode,
        InstallmentCancelClaimRecord.ParseStoredRefundMode(entity.RefundMode));

    private static bool SameRecoveryIdentity(
        InstallmentCancelClaimEntity claim,
        InstallmentRepaymentClaimIdentity identity) =>
        string.Equals(claim.LastRecoveryCashierId, identity.CashierId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(claim.LastRecoveryCashierName, identity.CashierName, StringComparison.Ordinal) &&
        string.Equals(claim.LastRecoveryCashierUserGuid, identity.CashierUserGuid, StringComparison.OrdinalIgnoreCase) &&
        claim.RecoveredAtUtc is not null;

    private static InstallmentCancelClaimCommitResponse DeserializeCommitResponse(
        InstallmentCancelClaimEntity entity,
        InstallmentCancelClaimRecord claim)
    {
        if (string.IsNullOrWhiteSpace(entity.CommitResponseJson))
        {
            throw Mismatch("Committed cancellation claim has no persisted commit response.");
        }

        InstallmentCancelClaimCommitResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<InstallmentCancelClaimCommitResponse>(
                entity.CommitResponseJson,
                InstallmentCancelClaimCommitRepositoryJson.Options);
        }
        catch (JsonException)
        {
            throw Mismatch("Persisted cancellation commit response is invalid.");
        }

        if (response is null)
        {
            throw Mismatch("Persisted cancellation commit response does not match the claim.");
        }

        InstallmentCancelClaimCommitSnapshotValidator.Validate(claim, response);

        return response;
    }

    private static InstallmentCancelClaimStatus ParseStatus(string value) =>
        Enum.TryParse<InstallmentCancelClaimStatus>(value, ignoreCase: true, out var status)
            ? status
            : throw Mismatch("Stored cancellation claim status is invalid.");

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static decimal RoundCurrency(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static InstallmentCancelClaimException Mismatch(string message) => new(
        InstallmentCancelClaimErrorCodes.Mismatch,
        message);

    private static InstallmentCancelClaimException Invalid(string message) => new(
        InstallmentCancelClaimErrorCodes.Invalid,
        message);

    private static InstallmentCancelClaimException NotFound(string message) => new(
        InstallmentCancelClaimErrorCodes.NotFound,
        message);
}
