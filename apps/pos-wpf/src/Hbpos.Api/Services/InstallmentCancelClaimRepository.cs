using Hbpos.Api.Data;
using Hbpos.Contracts.Installments;
using SqlSugar;

namespace Hbpos.Api.Services;

public sealed record InstallmentCancelClaimRecord(
    Guid InstallmentGuid,
    Guid OperationGuid,
    string StoreCode,
    string ClaimantDeviceCode,
    string CashierId,
    string CashierName,
    string IdempotencyKey,
    string? Reason,
    string RefundPlanFingerprint,
    InstallmentCancelClaimStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? CommittedAtUtc,
    long Revision,
    string? CommitResponseJson = null,
    string? LastRecoveryCashierId = null,
    string? LastRecoveryCashierName = null,
    string? LastRecoveryCashierUserGuid = null,
    DateTimeOffset? RecoveredAtUtc = null,
    string? OriginalDeviceCode = null,
    InstallmentCancelRefundMode RefundMode = InstallmentCancelRefundMode.OriginalRoute)
{
    // 列值为空的旧 claim 均是升级前创建的原路退款。
    internal static string? ToStoredRefundMode(InstallmentCancelRefundMode mode) =>
        mode == InstallmentCancelRefundMode.OriginalRoute ? null : mode.ToString();

    internal static InstallmentCancelRefundMode ParseStoredRefundMode(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? InstallmentCancelRefundMode.OriginalRoute
            : Enum.Parse<InstallmentCancelRefundMode>(value, ignoreCase: true);

    public static bool IsBlocking(InstallmentCancelClaimStatus status) =>
        status is InstallmentCancelClaimStatus.Prepared
            or InstallmentCancelClaimStatus.RefundPending
            or InstallmentCancelClaimStatus.Unknown;
}

public interface IInstallmentCancelClaimRepository
{
    Task<InstallmentCancelClaimRecord?> GetAsync(
        Guid operationGuid,
        CancellationToken cancellationToken);

    Task<InstallmentCancelClaimRecord?> GetBlockingAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken);

    Task<bool> TryInsertAsync(
        InstallmentCancelClaimRecord claim,
        CancellationToken cancellationToken);

    Task<bool> TryUpdateAsync(
        InstallmentCancelClaimRecord claim,
        long expectedRevision,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarInstallmentCancelClaimRepository(
    HbposSqlSugarContext dbContext,
    IInstallmentRepository installmentRepository) : IInstallmentCancelClaimRepository
{
    private const string SelectColumns = """
        [InstallmentGuid], [OperationGuid], [StoreCode], [OriginalDeviceCode], [ClaimantDeviceCode], [CashierId], [CashierName],
        [IdempotencyKey], [Reason], [RefundPlanFingerprint], [Status], [CreatedAtUtc], [UpdatedAtUtc],
        [ExpiresAtUtc], [CommittedAtUtc], [Revision], [CommitResponseJson], [LastRecoveryCashierId],
        [LastRecoveryCashierName], [LastRecoveryCashierUserGuid], [RecoveredAtUtc], [RefundMode]
        """;

    public async Task<InstallmentCancelClaimRecord?> GetAsync(
        Guid operationGuid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sql = $"""
            SELECT TOP 1 {SelectColumns}
            FROM [dbo].[POSM_InstallmentCancelClaim]
            WHERE [OperationGuid] = @OperationGuid;
            """;
        var row = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<ClaimRow>(
            sql,
            new SugarParameter("@OperationGuid", operationGuid));
        return row is null ? null : Map(row);
    }

    public async Task<InstallmentCancelClaimRecord?> GetBlockingAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sql = $"""
            SELECT TOP 1 {SelectColumns}
            FROM [dbo].[POSM_InstallmentCancelClaim]
            WHERE [InstallmentGuid] = @InstallmentGuid
              AND [IsBlocking] = 1
            ORDER BY [CreatedAtUtc], [OperationGuid];
            """;
        var row = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<ClaimRow>(
            sql,
            new SugarParameter("@InstallmentGuid", installmentGuid));
        return row is null ? null : Map(row);
    }

    public async Task<bool> TryInsertAsync(
        InstallmentCancelClaimRecord claim,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = dbContext.PosmDb;
        await using var processLock = await InstallmentMutationLock.AcquireProcessAsync(
            claim.InstallmentGuid,
            cancellationToken);
        await db.Ado.BeginTranAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            await InstallmentMutationLock.AcquireDatabaseAsync(db, claim.InstallmentGuid);
            var order = await InstallmentMutationLock.LockOrderAsync(db, claim.InstallmentGuid, cancellationToken);
            if (order is null ||
                !string.Equals(order.StoreCode, claim.StoreCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    order.DeviceCode,
                    string.IsNullOrWhiteSpace(claim.OriginalDeviceCode)
                        ? claim.ClaimantDeviceCode
                        : claim.OriginalDeviceCode,
                    StringComparison.OrdinalIgnoreCase) ||
                !InstallmentLifecycleRules.CanCancelWithRefund(order.Status, order.BalanceAmount))
            {
                await db.Ado.RollbackTranAsync();
                return false;
            }

            await InstallmentMutationLock.EnsureNoBlockingClaimAsync(
                db,
                claim.InstallmentGuid,
                cancellationToken);
            var current = await installmentRepository.GetDetailsAsync(claim.InstallmentGuid, cancellationToken);
            if (current is null ||
                !string.Equals(
                    InstallmentCancelClaimFingerprint.Create(current),
                    claim.RefundPlanFingerprint,
                    StringComparison.Ordinal))
            {
                await db.Ado.RollbackTranAsync();
                return false;
            }

            var inserted = await db.Insertable(MapEntity(claim)).ExecuteCommandAsync(cancellationToken) == 1;
            await db.Ado.CommitTranAsync();
            return inserted;
        }
        catch (InstallmentRepaymentClaimException ex)
            when (ex.Code == InstallmentRepaymentClaimErrorCodes.Busy)
        {
            await db.Ado.RollbackTranAsync();
            return false;
        }
        catch (Exception ex) when (IsUniqueConstraintViolation(ex))
        {
            await db.Ado.RollbackTranAsync();
            return false;
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task<bool> TryUpdateAsync(
        InstallmentCancelClaimRecord claim,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string sql = """
            UPDATE [dbo].[POSM_InstallmentCancelClaim]
            SET [Status] = @Status,
                [IsBlocking] = @IsBlocking,
                [UpdatedAtUtc] = @UpdatedAtUtc,
                [ExpiresAtUtc] = @ExpiresAtUtc,
                [CommittedAtUtc] = @CommittedAtUtc,
                [CommitResponseJson] = @CommitResponseJson,
                [LastRecoveryCashierId] = @LastRecoveryCashierId,
                [LastRecoveryCashierName] = @LastRecoveryCashierName,
                [LastRecoveryCashierUserGuid] = @LastRecoveryCashierUserGuid,
                [RecoveredAtUtc] = @RecoveredAtUtc,
                [Revision] = @Revision
            WHERE [OperationGuid] = @OperationGuid
              AND [Revision] = @ExpectedRevision;
            """;
        var parameters = ToParameters(claim)
            .Append(new SugarParameter("@ExpectedRevision", expectedRevision))
            .ToArray();
        try
        {
            return await dbContext.PosmDb.Ado.ExecuteCommandAsync(sql, parameters) == 1;
        }
        catch (Exception ex) when (IsUniqueConstraintViolation(ex))
        {
            return false;
        }
    }

    private static InstallmentCancelClaimEntity MapEntity(InstallmentCancelClaimRecord claim) => new()
    {
        InstallmentGuid = claim.InstallmentGuid,
        OperationGuid = claim.OperationGuid,
        StoreCode = claim.StoreCode,
        OriginalDeviceCode = string.IsNullOrWhiteSpace(claim.OriginalDeviceCode)
            ? claim.ClaimantDeviceCode
            : claim.OriginalDeviceCode,
        ClaimantDeviceCode = claim.ClaimantDeviceCode,
        CashierId = claim.CashierId,
        CashierName = claim.CashierName,
        IdempotencyKey = claim.IdempotencyKey,
        Reason = claim.Reason,
        RefundPlanFingerprint = claim.RefundPlanFingerprint,
        Status = claim.Status.ToString(),
        IsBlocking = InstallmentCancelClaimRecord.IsBlocking(claim.Status),
        CreatedAtUtc = claim.CreatedAtUtc.UtcDateTime,
        UpdatedAtUtc = claim.UpdatedAtUtc.UtcDateTime,
        ExpiresAtUtc = claim.ExpiresAtUtc?.UtcDateTime,
        CommittedAtUtc = claim.CommittedAtUtc?.UtcDateTime,
        Revision = claim.Revision,
        CommitResponseJson = claim.CommitResponseJson,
        LastRecoveryCashierId = claim.LastRecoveryCashierId,
        LastRecoveryCashierName = claim.LastRecoveryCashierName,
        LastRecoveryCashierUserGuid = claim.LastRecoveryCashierUserGuid,
        RecoveredAtUtc = claim.RecoveredAtUtc?.UtcDateTime,
        RefundMode = InstallmentCancelClaimRecord.ToStoredRefundMode(claim.RefundMode)
    };

    private static SugarParameter[] ToParameters(InstallmentCancelClaimRecord claim) =>
    [
        new("@InstallmentGuid", claim.InstallmentGuid),
        new("@OperationGuid", claim.OperationGuid),
        new("@StoreCode", claim.StoreCode),
        new(
            "@OriginalDeviceCode",
            string.IsNullOrWhiteSpace(claim.OriginalDeviceCode)
                ? claim.ClaimantDeviceCode
                : claim.OriginalDeviceCode),
        new("@ClaimantDeviceCode", claim.ClaimantDeviceCode),
        new("@CashierId", claim.CashierId),
        new("@CashierName", claim.CashierName),
        new("@IdempotencyKey", claim.IdempotencyKey),
        new("@Reason", claim.Reason),
        new("@RefundPlanFingerprint", claim.RefundPlanFingerprint),
        new("@Status", claim.Status.ToString()),
        new("@IsBlocking", InstallmentCancelClaimRecord.IsBlocking(claim.Status)),
        new("@CreatedAtUtc", claim.CreatedAtUtc.UtcDateTime),
        new("@UpdatedAtUtc", claim.UpdatedAtUtc.UtcDateTime),
        new("@ExpiresAtUtc", claim.ExpiresAtUtc?.UtcDateTime),
        new("@CommittedAtUtc", claim.CommittedAtUtc?.UtcDateTime),
        new("@Revision", claim.Revision),
        new("@CommitResponseJson", claim.CommitResponseJson),
        new("@LastRecoveryCashierId", claim.LastRecoveryCashierId),
        new("@LastRecoveryCashierName", claim.LastRecoveryCashierName),
        new("@LastRecoveryCashierUserGuid", claim.LastRecoveryCashierUserGuid),
        new("@RecoveredAtUtc", claim.RecoveredAtUtc?.UtcDateTime)
    ];

    private static InstallmentCancelClaimRecord Map(ClaimRow row) => new(
        row.InstallmentGuid,
        row.OperationGuid,
        row.StoreCode,
        row.ClaimantDeviceCode,
        row.CashierId,
        row.CashierName,
        row.IdempotencyKey,
        row.Reason,
        row.RefundPlanFingerprint,
        Enum.Parse<InstallmentCancelClaimStatus>(row.Status, ignoreCase: true),
        ToUtc(row.CreatedAtUtc),
        ToUtc(row.UpdatedAtUtc),
        row.ExpiresAtUtc is null ? null : ToUtc(row.ExpiresAtUtc.Value),
        row.CommittedAtUtc is null ? null : ToUtc(row.CommittedAtUtc.Value),
        row.Revision,
        row.CommitResponseJson,
        row.LastRecoveryCashierId,
        row.LastRecoveryCashierName,
        row.LastRecoveryCashierUserGuid,
        row.RecoveredAtUtc is null ? null : ToUtc(row.RecoveredAtUtc.Value),
        row.OriginalDeviceCode,
        InstallmentCancelClaimRecord.ParseStoredRefundMode(row.RefundMode));

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static bool IsUniqueConstraintViolation(Exception ex)
    {
        var text = ex.ToString();
        return text.Contains("2601", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("2627", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("UX_POSM_InstallmentCancelClaim", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ClaimRow
    {
        public Guid InstallmentGuid { get; set; }
        public Guid OperationGuid { get; set; }
        public string StoreCode { get; set; } = string.Empty;
        public string OriginalDeviceCode { get; set; } = string.Empty;
        public string ClaimantDeviceCode { get; set; } = string.Empty;
        public string CashierId { get; set; } = string.Empty;
        public string CashierName { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public string RefundPlanFingerprint { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public DateTime? CommittedAtUtc { get; set; }
        public long Revision { get; set; }
        public string? CommitResponseJson { get; set; }
        public string? LastRecoveryCashierId { get; set; }
        public string? LastRecoveryCashierName { get; set; }
        public string? LastRecoveryCashierUserGuid { get; set; }
        public DateTime? RecoveredAtUtc { get; set; }
        public string? RefundMode { get; set; }
    }
}

[SugarTable("POSM_InstallmentCancelClaim")]
public sealed class InstallmentCancelClaimEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid OperationGuid { get; set; }
    public Guid InstallmentGuid { get; set; }
    [SugarColumn(Length = 50)]
    public string StoreCode { get; set; } = string.Empty;
    [SugarColumn(Length = 50)]
    public string OriginalDeviceCode { get; set; } = string.Empty;
    [SugarColumn(Length = 50)]
    public string ClaimantDeviceCode { get; set; } = string.Empty;
    [SugarColumn(Length = 50)]
    public string CashierId { get; set; } = string.Empty;
    [SugarColumn(Length = 100)]
    public string CashierName { get; set; } = string.Empty;
    [SugarColumn(Length = 100)]
    public string IdempotencyKey { get; set; } = string.Empty;
    [SugarColumn(Length = 500, IsNullable = true)]
    public string? Reason { get; set; }
    [SugarColumn(Length = 71)]
    public string RefundPlanFingerprint { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string Status { get; set; } = string.Empty;
    public bool IsBlocking { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? ExpiresAtUtc { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? CommittedAtUtc { get; set; }
    public long Revision { get; set; }
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
    // 取消退款方式；NULL 表示原路退（兼容升级前的 claim）。创建后不可变。
    [SugarColumn(Length = 20, IsNullable = true)]
    public string? RefundMode { get; set; }
}
