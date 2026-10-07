using Hbpos.Api.Data;
using SqlSugar;

namespace Hbpos.Api.Services;

internal sealed class PosmLinklySettlementRecord
{
    public long Id { get; set; }

    public Guid SettlementGuid { get; set; }

    public string StoreCode { get; set; } = string.Empty;

    public string DeviceCode { get; set; } = string.Empty;

    public DateTime BusinessDate { get; set; }

    public string ConnectionMode { get; set; } = string.Empty;

    public string Environment { get; set; } = string.Empty;

    public string? ProviderSessionId { get; set; }

    public string? ProviderSubmissionState { get; set; }

    public long? CloudBackendSessionId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? ResponseCode { get; set; }

    public string? SettlementReceiptTexts { get; set; }

    public string? ResponseText { get; set; }

    public string? SettlementData { get; set; }

    public string ReceiptTextsJson { get; set; } = "[]";

    public DateTimeOffset RequestedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public DateTimeOffset? FirstPrintedAtUtc { get; set; }

    public DateTimeOffset? LastPrintedAtUtc { get; set; }

    public int PrintCount { get; set; }

    public string? LastPrintError { get; set; }

    public long ClientRevision { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed class LinklyCloudBackendSettlementFact
{
    public long Id { get; set; }

    public string Status { get; set; } = string.Empty;

    public bool? OperationSuccess { get; set; }

    public string? ResponseCode { get; set; }

    public string? SettlementReceiptTexts { get; set; }
}

internal interface ILinklySettlementRepository
{
    Task<PosmLinklySettlementRecord?> GetAsync(
        string storeCode,
        string deviceCode,
        Guid settlementGuid,
        CancellationToken cancellationToken);

    Task<PosmLinklySettlementRecord?> GetByProviderSessionAsync(
        string connectionMode,
        string environment,
        string storeCode,
        string deviceCode,
        string providerSessionId,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSettlementFact?> GetCloudBackendSettlementAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string providerSessionId,
        CancellationToken cancellationToken);

    Task<bool> TryInsertAsync(PosmLinklySettlementRecord settlement, CancellationToken cancellationToken);

    Task<bool> TryUpdateAsync(
        PosmLinklySettlementRecord settlement,
        long expectedRevision,
        CancellationToken cancellationToken);
}

internal sealed class SqlSugarLinklySettlementRepository(
    HbposSqlSugarContext dbContext) : ILinklySettlementRepository
{
    private const string SelectColumns = """
        [Id], [SettlementGuid], [StoreCode], [DeviceCode], [BusinessDate], [ConnectionMode], [Environment],
        [ProviderSessionId], [ProviderSubmissionState], [CloudBackendSessionId], [Status], [ResponseCode], [ResponseText], [SettlementData],
        [ReceiptTextsJson], [RequestedAtUtc], [CompletedAtUtc], [FirstPrintedAtUtc], [LastPrintedAtUtc],
        [PrintCount], [LastPrintError], [ClientRevision], [ReceivedAtUtc], [UpdatedAtUtc]
        """;

    internal const string SelectCloudBackendSettlementSql = """
        SELECT TOP 1
            session.[Id], session.[Status], session.[OperationSuccess], session.[ResponseCode],
            session.[SettlementReceiptTexts]
        FROM [dbo].[POSM_LinklyCloudBackendSession] session
        WHERE session.[Environment] = @Environment
          AND session.[StoreCode] = @StoreCode
          AND session.[DeviceCode] = @DeviceCode
          AND session.[SessionId] = @ProviderSessionId
          AND session.[OperationType] = N'Settlement';
        """;

    public async Task<PosmLinklySettlementRecord?> GetAsync(
        string storeCode,
        string deviceCode,
        Guid settlementGuid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sql = $"""
            SELECT TOP 1 {SelectColumns}
            FROM [dbo].[POSM_LinklySettlement]
            WHERE [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [SettlementGuid] = @SettlementGuid;
            """;
        return AsUtc(await dbContext.PosmDb.Ado.SqlQuerySingleAsync<PosmLinklySettlementRecord>(
            sql,
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@SettlementGuid", settlementGuid)));
    }

    public async Task<PosmLinklySettlementRecord?> GetByProviderSessionAsync(
        string connectionMode,
        string environment,
        string storeCode,
        string deviceCode,
        string providerSessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sql = $"""
            SELECT TOP 1 {SelectColumns}
            FROM [dbo].[POSM_LinklySettlement]
            WHERE [ConnectionMode] = @ConnectionMode
              AND [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [ProviderSessionId] = @ProviderSessionId;
            """;
        return AsUtc(await dbContext.PosmDb.Ado.SqlQuerySingleAsync<PosmLinklySettlementRecord>(
            sql,
            new SugarParameter("@ConnectionMode", connectionMode),
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@ProviderSessionId", providerSessionId)));
    }

    public async Task<LinklyCloudBackendSettlementFact?> GetCloudBackendSettlementAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string providerSessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudBackendSettlementFact>(
            SelectCloudBackendSettlementSql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@ProviderSessionId", providerSessionId));
    }

    public async Task<bool> TryInsertAsync(
        PosmLinklySettlementRecord settlement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string sql = """
            INSERT INTO [dbo].[POSM_LinklySettlement] (
                [SettlementGuid], [StoreCode], [DeviceCode], [BusinessDate], [ConnectionMode], [Environment],
                [ProviderSessionId], [ProviderSubmissionState], [CloudBackendSessionId], [Status], [ResponseCode], [ResponseText],
                [SettlementData], [ReceiptTextsJson], [RequestedAtUtc], [CompletedAtUtc], [FirstPrintedAtUtc],
                [LastPrintedAtUtc], [PrintCount], [LastPrintError], [ClientRevision], [ReceivedAtUtc], [UpdatedAtUtc])
            VALUES (
                @SettlementGuid, @StoreCode, @DeviceCode, @BusinessDate, @ConnectionMode, @Environment,
                @ProviderSessionId, @ProviderSubmissionState, @CloudBackendSessionId, @Status, @ResponseCode, @ResponseText,
                @SettlementData, @ReceiptTextsJson, @RequestedAtUtc, @CompletedAtUtc, @FirstPrintedAtUtc,
                @LastPrintedAtUtc, @PrintCount, @LastPrintError, @ClientRevision, @ReceivedAtUtc, @UpdatedAtUtc);
            """;
        try
        {
            return await dbContext.PosmDb.Ado.ExecuteCommandAsync(sql, ToParameters(settlement)) == 1;
        }
        catch (Exception ex) when (IsUniqueConstraintViolation(ex))
        {
            return false;
        }
    }

    public async Task<bool> TryUpdateAsync(
        PosmLinklySettlementRecord settlement,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string sql = """
            UPDATE [dbo].[POSM_LinklySettlement]
            SET [ProviderSessionId] = @ProviderSessionId,
                [ProviderSubmissionState] = @ProviderSubmissionState,
                [CloudBackendSessionId] = @CloudBackendSessionId,
                [Status] = @Status,
                [ResponseCode] = @ResponseCode,
                [ResponseText] = @ResponseText,
                [SettlementData] = @SettlementData,
                [ReceiptTextsJson] = @ReceiptTextsJson,
                [CompletedAtUtc] = @CompletedAtUtc,
                [FirstPrintedAtUtc] = @FirstPrintedAtUtc,
                [LastPrintedAtUtc] = @LastPrintedAtUtc,
                [PrintCount] = @PrintCount,
                [LastPrintError] = @LastPrintError,
                [ClientRevision] = @ClientRevision,
                [UpdatedAtUtc] = @UpdatedAtUtc
            WHERE [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [SettlementGuid] = @SettlementGuid
              AND [ClientRevision] = @ExpectedRevision;
            """;
        var parameters = ToParameters(settlement)
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

    /// <summary>
    /// 时间戳统一用显式 DbType.DateTime2：SugarParameter 对 DateTime 默认按 SQL datetime（1/300 秒精度）发送，
    /// 写进 DATETIME2(7) 列的值会被舍入，之后更高修订号的不可变字段比较、同修订幂等比较就会把
    /// 「库里舍入值」与「客户端原值」判成不同（2026-10 线上 IMMUTABLE_FIELDS_CONFLICT 的根因）。
    /// </summary>
    internal static SugarParameter[] ToParameters(PosmLinklySettlementRecord settlement)
    {
        return
        [
            new("@SettlementGuid", settlement.SettlementGuid),
            new("@StoreCode", settlement.StoreCode),
            new("@DeviceCode", settlement.DeviceCode),
            new("@BusinessDate", settlement.BusinessDate.Date),
            new("@ConnectionMode", settlement.ConnectionMode),
            new("@Environment", settlement.Environment),
            new("@ProviderSessionId", settlement.ProviderSessionId),
            new("@ProviderSubmissionState", settlement.ProviderSubmissionState),
            new("@CloudBackendSessionId", settlement.CloudBackendSessionId),
            new("@Status", settlement.Status),
            new("@ResponseCode", settlement.ResponseCode),
            new("@ResponseText", settlement.ResponseText),
            new("@SettlementData", settlement.SettlementData),
            new("@ReceiptTextsJson", settlement.ReceiptTextsJson),
            Utc("@RequestedAtUtc", settlement.RequestedAtUtc),
            Utc("@CompletedAtUtc", settlement.CompletedAtUtc),
            Utc("@FirstPrintedAtUtc", settlement.FirstPrintedAtUtc),
            Utc("@LastPrintedAtUtc", settlement.LastPrintedAtUtc),
            new("@PrintCount", settlement.PrintCount),
            new("@LastPrintError", settlement.LastPrintError),
            new("@ClientRevision", settlement.ClientRevision),
            Utc("@ReceivedAtUtc", settlement.ReceivedAtUtc),
            Utc("@UpdatedAtUtc", settlement.UpdatedAtUtc)
        ];
    }

    /// <summary>
    /// 库里存的是 UTC 墙钟（DATETIME2 不带时区），SqlSugar 读回 DateTimeOffset 时会按进程本地时区补偏移；
    /// 这里把墙钟原样重新解释为 UTC。线上容器是 UTC 时等同空操作，但不能让比较结果依赖机器时区。
    /// </summary>
    internal static PosmLinklySettlementRecord? AsUtc(PosmLinklySettlementRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        record.RequestedAtUtc = AsUtc(record.RequestedAtUtc);
        record.CompletedAtUtc = record.CompletedAtUtc is { } completed ? AsUtc(completed) : null;
        record.FirstPrintedAtUtc = record.FirstPrintedAtUtc is { } first ? AsUtc(first) : null;
        record.LastPrintedAtUtc = record.LastPrintedAtUtc is { } last ? AsUtc(last) : null;
        record.ReceivedAtUtc = AsUtc(record.ReceivedAtUtc);
        record.UpdatedAtUtc = AsUtc(record.UpdatedAtUtc);
        return record;
    }

    private static DateTimeOffset AsUtc(DateTimeOffset value) =>
        new(DateTime.SpecifyKind(value.DateTime, DateTimeKind.Utc));

    private static SugarParameter Utc(string name, DateTimeOffset? value) =>
        new(name, value is null ? null : (object)value.Value.UtcDateTime)
        {
            DbType = System.Data.DbType.DateTime2
        };

    private static bool IsUniqueConstraintViolation(Exception ex)
    {
        var text = ex.ToString();
        return text.Contains("2601", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("2627", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("UX_POSM_LinklySettlement", StringComparison.OrdinalIgnoreCase);
    }
}
