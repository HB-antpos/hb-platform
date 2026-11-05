using System.Data;
using System.Text.Json;
using Hbpos.Api.Data;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using SqlSugar;

namespace Hbpos.Api.Services;

public sealed record InstallmentRepaymentClaimCommitResult(
    InstallmentRepaymentClaimRecord Claim,
    InstallmentAppendPaymentResponse CommitResponse,
    bool AlreadyRecorded);

public interface IInstallmentRepaymentClaimCommitRepository
{
    Task<InstallmentRepaymentClaimCommitResult> CommitAsync(
        InstallmentRepaymentClaimRecord expectedClaim,
        InstallmentRepaymentClaimCommitRequest request,
        InstallmentRepaymentClaimIdentity recoveryIdentity,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken);
}

public interface IInstallmentRepaymentClaimCommitFaultInjector
{
    Task AfterPaymentInsertedAsync(CancellationToken cancellationToken);
}

public sealed class NoOpInstallmentRepaymentClaimCommitFaultInjector
    : IInstallmentRepaymentClaimCommitFaultInjector
{
    public Task AfterPaymentInsertedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class InstallmentRepaymentClaimCommitRepositoryJson
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

public sealed class SqlSugarInstallmentRepaymentClaimCommitRepository(
    HbposSqlSugarContext dbContext,
    IInstallmentRepository installmentRepository,
    IInstallmentRepaymentClaimCommitFaultInjector faultInjector)
    : IInstallmentRepaymentClaimCommitRepository
{
    public async Task<InstallmentRepaymentClaimCommitResult> CommitAsync(
        InstallmentRepaymentClaimRecord expectedClaim,
        InstallmentRepaymentClaimCommitRequest request,
        InstallmentRepaymentClaimIdentity recoveryIdentity,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstallmentRepaymentClaimCommitEvidenceValidator.Validate(expectedClaim, request);
        var db = dbContext.PosmDb;
        await using var processLock = await InstallmentMutationLock.AcquireProcessAsync(
            expectedClaim.InstallmentGuid,
            cancellationToken);
        await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
        InstallmentRepaymentClaimRecord committedClaim;
        InstallmentAppendPaymentResponse commitResponse;
        var alreadyRecorded = false;
        try
        {
            await InstallmentMutationLock.AcquireDatabaseAsync(db, expectedClaim.InstallmentGuid);
            // 所有分期写统一先锁订单、后锁 claim，避免生命周期写与支付提交形成反向等待。
            var lockedOrder = await InstallmentMutationLock.LockOrderAsync(
                db,
                expectedClaim.InstallmentGuid,
                cancellationToken)
                ?? throw NotFound("Installment was not found while committing the repayment claim.");
            var claimEntity = await LockClaimAsync(db, expectedClaim.OperationGuid, cancellationToken)
                ?? throw NotFound("Repayment claim was not found while committing.");
            ValidateImmutableClaim(claimEntity, expectedClaim);
            var currentStatus = ParseStatus(claimEntity.Status);
            var existingPayment = await LockPaymentAsync(db, claimEntity, cancellationToken);
            if (currentStatus == InstallmentRepaymentClaimStatus.Committed)
            {
                ValidateExistingPayment(claimEntity, existingPayment);
                alreadyRecorded = true;
                if (!SameRecoveryIdentity(claimEntity, recoveryIdentity))
                {
                    var revision = claimEntity.Revision;
                    var affected = await db.Updateable<InstallmentRepaymentClaimEntity>()
                        .SetColumns(entity => entity.LastRecoveryCashierId == recoveryIdentity.CashierId)
                        .SetColumns(entity => entity.LastRecoveryCashierName == recoveryIdentity.CashierName)
                        .SetColumns(entity => entity.LastRecoveryCashierUserGuid == recoveryIdentity.CashierUserGuid)
                        .SetColumns(entity => entity.RecoveredAtUtc == committedAtUtc.UtcDateTime)
                        .SetColumns(entity => entity.UpdatedAtUtc == committedAtUtc.UtcDateTime)
                        .SetColumns(entity => entity.Revision == revision + 1)
                        .Where(entity => entity.OperationGuid == claimEntity.OperationGuid)
                        .Where(entity => entity.Revision == revision)
                        .Where(entity => entity.Status == InstallmentRepaymentClaimStatus.Committed.ToString())
                        .ExecuteCommandAsync(cancellationToken);
                    if (affected != 1)
                    {
                        throw Mismatch("Committed repayment claim changed during recovery audit update.");
                    }

                    claimEntity.LastRecoveryCashierId = recoveryIdentity.CashierId;
                    claimEntity.LastRecoveryCashierName = recoveryIdentity.CashierName;
                    claimEntity.LastRecoveryCashierUserGuid = recoveryIdentity.CashierUserGuid;
                    claimEntity.RecoveredAtUtc = committedAtUtc.UtcDateTime;
                    claimEntity.UpdatedAtUtc = committedAtUtc.UtcDateTime;
                    claimEntity.Revision = revision + 1;
                }
                committedClaim = Map(claimEntity);
                commitResponse = DeserializeCommitResponse(claimEntity, committedClaim);
                await db.Ado.CommitTranAsync();
            }
            else
            {
                if (currentStatus != InstallmentRepaymentClaimStatus.ProviderPending)
                {
                    throw Mismatch("Only a provider-pending repayment claim can be committed.");
                }

                InstallmentRepaymentClaimCommitEvidenceValidator.Validate(Map(claimEntity), request);

                var order = lockedOrder;
                ValidateInstallment(order, claimEntity);
                if (existingPayment is null)
                {
                    var amount = claimEntity.Amount;
                    if ((PaymentMethodKind)claimEntity.Method == PaymentMethodKind.Voucher)
                    {
                        var reference = NormalizeRequired(request.Reference, "reference");
                        var reservationToken = NormalizeRequired(request.ReservationToken, "reservationToken");
                        await SqlSugarStoreVoucherReservationService.ClaimInsideTransactionAsync(
                            db,
                            reservationToken,
                            claimEntity.StoreCode,
                            reference,
                            amount,
                            claimEntity.PaymentGuid.ToString("D"),
                            committedAtUtc,
                            cancellationToken);
                        await SqlSugarStoreVoucherRepository.RedeemInsideTransactionAsync(
                            db,
                            claimEntity.StoreCode,
                            reference,
                            amount,
                            claimEntity.CashierId,
                            cancellationToken);
                    }

                    existingPayment = new InstallmentPaymentEntity
                    {
                        PaymentGuid = claimEntity.PaymentGuid.ToString("D"),
                        InstallmentGuid = claimEntity.InstallmentGuid.ToString("D"),
                        Method = claimEntity.Method,
                        Amount = amount,
                        Reference = NormalizeOptional(request.Reference),
                        Status = (int)InstallmentPaymentStatus.Recorded,
                        RecordedAt = committedAtUtc.UtcDateTime,
                        CashierId = claimEntity.CashierId,
                        CashierName = claimEntity.CashierName,
                        DeviceCode = claimEntity.ClaimantDeviceCode,
                        CardTransactionsJson = request.CardTransactions is null
                            ? null
                            : JsonSerializer.Serialize(request.CardTransactions, InstallmentRepaymentClaimCommitRepositoryJson.Options),
                        IdempotencyKey = claimEntity.IdempotencyKey
                    };
                    await db.Insertable(existingPayment).ExecuteCommandAsync(cancellationToken);
                    // 故障注入点位于付款写入后、订单及 claim 更新前，用于证明事务回滚覆盖最危险窗口。
                    await faultInjector.AfterPaymentInsertedAsync(cancellationToken);

                    if ((PaymentMethodKind)claimEntity.Method == PaymentMethodKind.Voucher)
                    {
                        var reservationToken = NormalizeRequired(request.ReservationToken, "reservationToken");
                        await db.Updateable<StoreVoucherReservationEntity>()
                            .SetColumns(entity => entity.Status == "consumed")
                            .SetColumns(entity => entity.ConsumedAtUtc == committedAtUtc.UtcDateTime)
                            .Where(entity => entity.Token == reservationToken)
                            .Where(entity => entity.Status == "claimed")
                            .ExecuteCommandAsync(cancellationToken);
                    }
                }
                else
                {
                    ValidateExistingPayment(claimEntity, existingPayment);
                    alreadyRecorded = true;
                }

                var installmentGuidText = claimEntity.InstallmentGuid.ToString("D");
                var paidAmount = RoundCurrency(await db.Queryable<InstallmentPaymentEntity>()
                    .Where(payment =>
                        payment.InstallmentGuid == installmentGuidText &&
                        payment.Status == (int)InstallmentPaymentStatus.Recorded)
                    .SumAsync(payment => payment.Amount));
                var balanceAmount = RoundCurrency(Math.Max(0m, order.TotalAmount - paidAmount));
                var installmentStatus = balanceAmount == 0m
                    ? InstallmentStatus.PaidOff
                    : InstallmentStatus.Active;
                await db.Updateable<InstallmentOrderEntity>()
                    .SetColumns(entity => entity.PaidAmount == paidAmount)
                    .SetColumns(entity => entity.BalanceAmount == balanceAmount)
                    .SetColumns(entity => entity.Status == (int)installmentStatus)
                    .SetColumns(entity => entity.UpdatedAt == committedAtUtc.UtcDateTime)
                    .Where(entity => entity.InstallmentGuid == installmentGuidText)
                    .ExecuteCommandAsync(cancellationToken);

                var details = await installmentRepository.GetDetailsAsync(
                    expectedClaim.InstallmentGuid,
                    cancellationToken)
                    ?? throw NotFound("Installment disappeared while committing the repayment claim.");
                commitResponse = new InstallmentAppendPaymentResponse(
                    claimEntity.InstallmentGuid,
                    claimEntity.PaymentGuid,
                    details.PaidAmount,
                    details.BalanceAmount,
                    details.Status,
                    details,
                    AlreadyRecorded: alreadyRecorded,
                    Message: alreadyRecorded ? "AlreadyRecorded" : null);
                var commitResponseJson = JsonSerializer.Serialize(
                    commitResponse,
                    InstallmentRepaymentClaimCommitRepositoryJson.Options);

                var revision = claimEntity.Revision;
                var affectedClaims = await db.Updateable<InstallmentRepaymentClaimEntity>()
                    .SetColumns(entity => entity.Status == InstallmentRepaymentClaimStatus.Committed.ToString())
                    .SetColumns(entity => entity.IsBlocking == false)
                    .SetColumns(entity => entity.UpdatedAtUtc == committedAtUtc.UtcDateTime)
                    .SetColumns(entity => entity.ExpiresAtUtc == null)
                    .SetColumns(entity => entity.CommittedAtUtc == committedAtUtc.UtcDateTime)
                    .SetColumns(entity => entity.CommitResponseJson == commitResponseJson)
                    .SetColumns(entity => entity.LastRecoveryCashierId == recoveryIdentity.CashierId)
                    .SetColumns(entity => entity.LastRecoveryCashierName == recoveryIdentity.CashierName)
                    .SetColumns(entity => entity.LastRecoveryCashierUserGuid == recoveryIdentity.CashierUserGuid)
                    .SetColumns(entity => entity.RecoveredAtUtc == committedAtUtc.UtcDateTime)
                    .SetColumns(entity => entity.Revision == revision + 1)
                    .Where(entity => entity.OperationGuid == claimEntity.OperationGuid)
                    .Where(entity => entity.Revision == revision)
                    .Where(entity => entity.Status == InstallmentRepaymentClaimStatus.ProviderPending.ToString())
                    .ExecuteCommandAsync(cancellationToken);
                if (affectedClaims != 1)
                {
                    throw Mismatch("Repayment claim changed during atomic commit.");
                }

                claimEntity.Status = InstallmentRepaymentClaimStatus.Committed.ToString();
                claimEntity.IsBlocking = false;
                claimEntity.UpdatedAtUtc = committedAtUtc.UtcDateTime;
                claimEntity.ExpiresAtUtc = null;
                claimEntity.CommittedAtUtc = committedAtUtc.UtcDateTime;
                claimEntity.CommitResponseJson = commitResponseJson;
                claimEntity.LastRecoveryCashierId = recoveryIdentity.CashierId;
                claimEntity.LastRecoveryCashierName = recoveryIdentity.CashierName;
                claimEntity.LastRecoveryCashierUserGuid = recoveryIdentity.CashierUserGuid;
                claimEntity.RecoveredAtUtc = committedAtUtc.UtcDateTime;
                claimEntity.Revision = revision + 1;
                committedClaim = Map(claimEntity);
                await db.Ado.CommitTranAsync();
            }
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        return new InstallmentRepaymentClaimCommitResult(committedClaim, commitResponse, alreadyRecorded);
    }

    private static async Task<InstallmentRepaymentClaimEntity?> LockClaimAsync(
        ISqlSugarClient db,
        Guid operationGuid,
        CancellationToken cancellationToken)
    {
        var query = db.Queryable<InstallmentRepaymentClaimEntity>()
            .Where(entity => entity.OperationGuid == operationGuid);
        if (db.CurrentConnectionConfig.DbType == SqlSugar.DbType.SqlServer)
        {
            query = query.With(SqlWith.UpdLock);
        }

        return await query.FirstAsync(cancellationToken);
    }

    private static async Task<InstallmentPaymentEntity?> LockPaymentAsync(
        ISqlSugarClient db,
        InstallmentRepaymentClaimEntity claim,
        CancellationToken cancellationToken)
    {
        var paymentGuidText = claim.PaymentGuid.ToString("D");
        var installmentGuidText = claim.InstallmentGuid.ToString("D");
        var query = db.Queryable<InstallmentPaymentEntity>()
            .Where(entity =>
                entity.PaymentGuid == paymentGuidText ||
                (entity.InstallmentGuid == installmentGuidText && entity.IdempotencyKey == claim.IdempotencyKey));
        if (db.CurrentConnectionConfig.DbType == SqlSugar.DbType.SqlServer)
        {
            query = query.With(SqlWith.UpdLock);
        }

        return await query.FirstAsync(cancellationToken);
    }

    private static void ValidateImmutableClaim(
        InstallmentRepaymentClaimEntity actual,
        InstallmentRepaymentClaimRecord expected)
    {
        if (actual.InstallmentGuid != expected.InstallmentGuid ||
            actual.OperationGuid != expected.OperationGuid ||
            actual.PaymentGuid != expected.PaymentGuid ||
            !string.Equals(actual.StoreCode, expected.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.ClaimantDeviceCode, expected.ClaimantDeviceCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.CashierId, expected.CashierId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.CashierName, expected.CashierName, StringComparison.Ordinal) ||
            actual.Amount != expected.Amount ||
            actual.Method != (int)expected.Method ||
            !string.Equals(actual.IdempotencyKey, expected.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(actual.Fingerprint, expected.Fingerprint, StringComparison.Ordinal))
        {
            throw Mismatch("Stored repayment claim no longer matches its immutable fingerprint.");
        }
    }

    private static void ValidateInstallment(
        InstallmentOrderEntity order,
        InstallmentRepaymentClaimEntity claim)
    {
        if (!string.Equals(order.StoreCode, claim.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            order.Status != (int)InstallmentStatus.Active ||
            order.BalanceAmount <= 0m ||
            claim.Amount > order.BalanceAmount)
        {
            throw Mismatch("Installment state or scope does not match the repayment claim.");
        }
    }

    private static void ValidateExistingPayment(
        InstallmentRepaymentClaimEntity claim,
        InstallmentPaymentEntity? payment)
    {
        var amountMatches = payment?.Amount == claim.Amount;
        if (payment is null ||
            !string.Equals(payment.InstallmentGuid, claim.InstallmentGuid.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(payment.PaymentGuid, claim.PaymentGuid.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            payment.Method != claim.Method ||
            !amountMatches ||
            !string.Equals(payment.IdempotencyKey, claim.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(payment.DeviceCode, claim.ClaimantDeviceCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(payment.CashierId, claim.CashierId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(payment.CashierName, claim.CashierName, StringComparison.Ordinal))
        {
            throw Mismatch("Existing payment does not match the repayment claim.");
        }
    }

    private static bool SameRecoveryIdentity(
        InstallmentRepaymentClaimEntity claim,
        InstallmentRepaymentClaimIdentity identity) =>
        string.Equals(claim.LastRecoveryCashierId, identity.CashierId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(claim.LastRecoveryCashierName, identity.CashierName, StringComparison.Ordinal) &&
        string.Equals(claim.LastRecoveryCashierUserGuid, identity.CashierUserGuid, StringComparison.OrdinalIgnoreCase) &&
        claim.RecoveredAtUtc is not null;

    private static InstallmentRepaymentClaimRecord Map(InstallmentRepaymentClaimEntity entity)
    {
        return new InstallmentRepaymentClaimRecord(
            entity.InstallmentGuid,
            entity.OperationGuid,
            entity.PaymentGuid,
            entity.StoreCode,
            entity.ClaimantDeviceCode,
            entity.CashierId,
            entity.CashierName,
            entity.Amount,
            (PaymentMethodKind)entity.Method,
            entity.IdempotencyKey,
            entity.Fingerprint,
            ParseStatus(entity.Status),
            entity.Provider,
            entity.ProviderAttemptId,
            ToUtc(entity.CreatedAtUtc),
            ToUtc(entity.UpdatedAtUtc),
            entity.ExpiresAtUtc is null ? null : ToUtc(entity.ExpiresAtUtc.Value),
            entity.CommittedAtUtc is null ? null : ToUtc(entity.CommittedAtUtc.Value),
            entity.Revision,
            entity.CommitResponseJson,
            entity.LastRecoveryCashierId,
            entity.LastRecoveryCashierName,
            entity.LastRecoveryCashierUserGuid,
            entity.RecoveredAtUtc is null ? null : ToUtc(entity.RecoveredAtUtc.Value));
    }

    private static InstallmentAppendPaymentResponse DeserializeCommitResponse(
        InstallmentRepaymentClaimEntity entity,
        InstallmentRepaymentClaimRecord claim)
    {
        if (string.IsNullOrWhiteSpace(entity.CommitResponseJson))
        {
            throw Mismatch("Committed claim has no persisted commit response.");
        }

        InstallmentAppendPaymentResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<InstallmentAppendPaymentResponse>(
                entity.CommitResponseJson,
                InstallmentRepaymentClaimCommitRepositoryJson.Options);
        }
        catch (JsonException)
        {
            throw Mismatch("Persisted repayment commit response is invalid.");
        }

        if (response is null ||
            response.InstallmentGuid != claim.InstallmentGuid ||
            response.PaymentGuid != claim.PaymentGuid)
        {
            throw Mismatch("Persisted repayment commit response does not match the claim.");
        }

        return response;
    }

    private static InstallmentRepaymentClaimStatus ParseStatus(string value)
    {
        return Enum.TryParse<InstallmentRepaymentClaimStatus>(value, ignoreCase: true, out var status)
            ? status
            : throw Mismatch("Stored repayment claim status is invalid.");
    }

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static InstallmentRepaymentClaimException Mismatch(string message) => new(
        InstallmentRepaymentClaimErrorCodes.Mismatch,
        message);

    private static InstallmentRepaymentClaimException NotFound(string message) => new(
        InstallmentRepaymentClaimErrorCodes.NotFound,
        message);

    private static string NormalizeRequired(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InstallmentRepaymentClaimException(
                InstallmentRepaymentClaimErrorCodes.Invalid,
                $"{fieldName} is required.");
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal RoundCurrency(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}

[SugarTable("POSM_InstallmentRepaymentClaim")]
public sealed class InstallmentRepaymentClaimEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid OperationGuid { get; set; }

    public Guid InstallmentGuid { get; set; }

    public Guid PaymentGuid { get; set; }

    [SugarColumn(Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string ClaimantDeviceCode { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string CashierId { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string CashierName { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public int Method { get; set; }

    [SugarColumn(Length = 100)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string Fingerprint { get; set; } = string.Empty;

    [SugarColumn(Length = 32)]
    public string Status { get; set; } = string.Empty;

    public bool IsBlocking { get; set; }

    [SugarColumn(Length = 32, IsNullable = true)]
    public string? Provider { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? ProviderAttemptId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? ExpiresAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? CommittedAtUtc { get; set; }

    // 生产 SQL Server 由 schema initializer 创建 NVARCHAR(MAX)；不固定 CodeFirst 类型以兼容 SQLite 事务测试。
    [SugarColumn(IsNullable = true)]
    public string? CommitResponseJson { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? LastRecoveryCashierId { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? LastRecoveryCashierName { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? LastRecoveryCashierUserGuid { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? RecoveredAtUtc { get; set; }

    public long Revision { get; set; }
}

internal static class InstallmentRepaymentClaimCommitEvidenceValidator
{
    // 卡证据最多几条：一次续付只对应一笔刷卡；留余量给分步授权，再多就是异常请求。
    internal const int MaxCardTransactions = 10;

    // 单条卡证据序列化后的上限（含小票原文）；正常小票只有几 KB，超出只可能是异常请求。
    internal const int MaxCardTransactionJsonLength = 64 * 1024;

    // 与 InstallmentPaymentEntity.Reference 列宽一致，超长会在插入时被数据库截断报错。
    internal const int MaxReferenceLength = 200;

    // Cash/Voucher 由服务端权威记账；Card 与普通销售一样信任收银机上报的刷卡结果。
    // Card 只硬拒「正常流程不可能产生」的证据（为空、金额不符、通道不符、超长）：钱已经扣了，
    // 其余可疑之处（批准码、TxnRef 重复、Linkly 会话不符）只在入账后旁路登记，见 InstallmentRepaymentCardTenderVerifier。
    public static void Validate(
        InstallmentRepaymentClaimRecord claim,
        InstallmentRepaymentClaimCommitRequest request)
    {
        var provider = ProviderFamilyOf(claim.Provider);
        ValidateProviderForMethod(claim.Method, claim.Provider);
        switch (claim.Method)
        {
            case PaymentMethodKind.Cash:
                RequireProvider(provider, ProviderFamily.Cash, "cash");
                RejectCardEvidence(request);
                if (!string.IsNullOrWhiteSpace(request.ReservationToken))
                {
                    throw Invalid("Cash repayment must not contain a voucher reservation token.");
                }
                break;
            case PaymentMethodKind.Voucher:
                RequireProvider(provider, ProviderFamily.Voucher, "voucher");
                RejectCardEvidence(request);
                NormalizeRequired(request.Reference, "reference");
                NormalizeRequired(request.ReservationToken, "reservationToken");
                break;
            case PaymentMethodKind.Card:
                ValidateCardEvidence(claim, provider, request);
                break;
            default:
                throw Invalid("Repayment claim method is invalid.");
        }
    }

    public static void ValidateProviderForMethod(PaymentMethodKind method, string? providerName)
    {
        var provider = ProviderFamilyOf(providerName);
        switch (method)
        {
            case PaymentMethodKind.Cash:
                RequireProvider(provider, ProviderFamily.Cash, "cash");
                break;
            case PaymentMethodKind.Voucher:
                RequireProvider(provider, ProviderFamily.Voucher, "voucher");
                break;
            case PaymentMethodKind.Card:
                if (!IsCardFamily(provider))
                {
                    throw Invalid("Repayment claim provider does not match the card method.");
                }
                break;
            default:
                throw Invalid("Repayment claim method is invalid.");
        }
    }

    private static void ValidateCardEvidence(
        InstallmentRepaymentClaimRecord claim,
        ProviderFamily provider,
        InstallmentRepaymentClaimCommitRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ReservationToken))
        {
            throw Invalid("Card repayment must not contain a voucher reservation token.");
        }

        if (request.Reference is { Length: > MaxReferenceLength })
        {
            throw Invalid($"Card repayment reference must not exceed {MaxReferenceLength} characters.");
        }

        var transactions = request.CardTransactions;
        if (transactions is not { Count: > 0 })
        {
            throw Invalid("Card repayment requires card transaction evidence.");
        }

        if (transactions.Count > MaxCardTransactions)
        {
            throw Invalid($"Card repayment must not contain more than {MaxCardTransactions} card transactions.");
        }

        foreach (var transaction in transactions)
        {
            if (transaction is null)
            {
                throw Invalid("Card transaction evidence must not contain empty entries.");
            }

            if (JsonSerializer.Serialize(transaction, InstallmentRepaymentClaimCommitRepositoryJson.Options).Length >
                MaxCardTransactionJsonLength)
            {
                throw Invalid("Card transaction evidence is too large.");
            }

            // 认得出的处理器必须与 claim 登记的通道族一致；认不出的（如人工卡）只在旁路核对里登记。
            var evidenceFamily = ProviderFamilyOf(transaction.Processor);
            if (IsCardFamily(evidenceFamily) && evidenceFamily != provider)
            {
                throw Invalid("Card transaction processor does not match the repayment claim provider.");
            }
        }

        // 与普通销售口径一致：银行流水金额按绝对值合计，必须等于 claim 锁定的金额。
        var evidenceTotal = RoundCurrency(transactions.Sum(transaction => Math.Abs(transaction.Amount)));
        if (evidenceTotal != RoundCurrency(claim.Amount))
        {
            throw Invalid(
                $"Card transaction amounts sum to {evidenceTotal:0.00} but the repayment claim amount is {claim.Amount:0.00}.");
        }
    }

    private static void RejectCardEvidence(InstallmentRepaymentClaimCommitRequest request)
    {
        if (request.CardTransactions is { Count: > 0 })
        {
            throw Invalid("Non-card repayment must not contain card transaction evidence.");
        }
    }

    private static void RequireProvider(ProviderFamily actual, ProviderFamily expected, string method)
    {
        if (actual != expected)
        {
            throw Invalid($"Repayment claim provider does not match the {method} method.");
        }
    }

    private static bool IsCardFamily(ProviderFamily family) =>
        family is ProviderFamily.Square or ProviderFamily.Linkly or ProviderFamily.Zeller;

    private static ProviderFamily ProviderFamilyOf(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        switch (normalized)
        {
            case "cash":
                return ProviderFamily.Cash;
            case "voucher":
                return ProviderFamily.Voucher;
        }

        return InstallmentCardRepaymentProviders.NormalizeFamily(value) switch
        {
            InstallmentCardRepaymentProviders.Square => ProviderFamily.Square,
            InstallmentCardRepaymentProviders.Linkly => ProviderFamily.Linkly,
            InstallmentCardRepaymentProviders.Zeller => ProviderFamily.Zeller,
            _ => ProviderFamily.Unknown
        };
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"{fieldName} is required.");
        }

        return value.Trim();
    }

    private static decimal RoundCurrency(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static InstallmentRepaymentClaimException Invalid(string message) => new(
        InstallmentRepaymentClaimErrorCodes.Invalid,
        message);

    private enum ProviderFamily
    {
        Unknown,
        Cash,
        Voucher,
        Square,
        Linkly,
        Zeller
    }
}
