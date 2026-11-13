using Hbpos.Api.Data;
using SqlSugar;

namespace Hbpos.Api.Services;

/// <summary>POSM_CashMovement 一行（已校验、已规整）。</summary>
internal sealed class PosmCashMovementRecord
{
    public long Id { get; set; }
    public Guid MovementGuid { get; set; }
    public string StoreCode { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string ClientKind { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    public Guid? VoidsMovementGuid { get; set; }
    public DateTime BusinessDate { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string CashierId { get; set; } = string.Empty;
    public string CashierName { get; set; } = string.Empty;
    public string? AuthorizedById { get; set; }
    public string? AuthorizedByName { get; set; }
    public string? DisplayNumber { get; set; }
    public string? AppVersion { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
}

/// <summary>插入结果：成功、MovementGuid 已存在、或该原记录已被别的记录冲正。</summary>
internal enum CashMovementInsertOutcome
{
    Inserted,
    DuplicateGuid,
    AlreadyVoided
}

internal interface ICashMovementRepository
{
    /// <summary>按全局唯一的 MovementGuid 读取；不带 scope 条件，调用方据此识别跨设备冲突。</summary>
    Task<PosmCashMovementRecord?> GetByGuidAsync(Guid movementGuid, CancellationToken cancellationToken);

    Task<CashMovementInsertOutcome> TryInsertAsync(PosmCashMovementRecord record, CancellationToken cancellationToken);
}

internal sealed class SqlSugarCashMovementRepository(
    HbposSqlSugarContext dbContext) : ICashMovementRepository
{
    internal const string SelectByGuidSql = """
        SELECT TOP 1 [Id], [MovementGuid], [StoreCode], [DeviceCode], [ClientKind], [Direction], [ReasonCode],
            [Amount], [Note], [VoidsMovementGuid], [BusinessDate], [OccurredAtUtc], [CashierId], [CashierName],
            [AuthorizedById], [AuthorizedByName], [DisplayNumber], [AppVersion], [ReceivedAtUtc]
        FROM [dbo].[POSM_CashMovement]
        WHERE [MovementGuid] = @MovementGuid;
        """;

    internal const string InsertSql = """
        INSERT INTO [dbo].[POSM_CashMovement] (
            [MovementGuid], [StoreCode], [DeviceCode], [ClientKind], [Direction], [ReasonCode],
            [Amount], [Note], [VoidsMovementGuid], [BusinessDate], [OccurredAtUtc], [CashierId], [CashierName],
            [AuthorizedById], [AuthorizedByName], [DisplayNumber], [AppVersion], [ReceivedAtUtc])
        VALUES (
            @MovementGuid, @StoreCode, @DeviceCode, @ClientKind, @Direction, @ReasonCode,
            @Amount, @Note, @VoidsMovementGuid, @BusinessDate, @OccurredAtUtc, @CashierId, @CashierName,
            @AuthorizedById, @AuthorizedByName, @DisplayNumber, @AppVersion, @ReceivedAtUtc);
        """;

    public async Task<PosmCashMovementRecord?> GetByGuidAsync(Guid movementGuid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<PosmCashMovementRecord>(
            SelectByGuidSql,
            new SugarParameter("@MovementGuid", movementGuid));
    }

    public async Task<CashMovementInsertOutcome> TryInsertAsync(
        PosmCashMovementRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await dbContext.PosmDb.Ado.ExecuteCommandAsync(InsertSql, ToParameters(record));
            return CashMovementInsertOutcome.Inserted;
        }
        catch (Exception ex) when (UniqueIndexName(ex) is { } index)
        {
            // 两个唯一索引含义不同：Guid 重复按幂等重读处理；冲正索引冲突说明原记录已被另一条记录冲正。
            return index.Contains("UX_POSM_CashMovement_Voids", StringComparison.OrdinalIgnoreCase)
                ? CashMovementInsertOutcome.AlreadyVoided
                : CashMovementInsertOutcome.DuplicateGuid;
        }
    }

    // 显式 DbType：DateTime 默认按 SQL datetime 发送会丢 DATETIME2(7) 精度，影响幂等比较；NULL 也要带类型。
    private static SugarParameter[] ToParameters(PosmCashMovementRecord record) =>
    [
        new("@MovementGuid", record.MovementGuid),
        new("@StoreCode", record.StoreCode),
        new("@DeviceCode", record.DeviceCode),
        new("@ClientKind", record.ClientKind),
        new("@Direction", record.Direction),
        new("@ReasonCode", record.ReasonCode),
        new("@Amount", record.Amount) { DbType = System.Data.DbType.Decimal },
        new("@Note", record.Note) { DbType = System.Data.DbType.String },
        new("@VoidsMovementGuid", record.VoidsMovementGuid) { DbType = System.Data.DbType.Guid },
        new("@BusinessDate", record.BusinessDate.Date) { DbType = System.Data.DbType.Date },
        new("@OccurredAtUtc", DateTime.SpecifyKind(record.OccurredAtUtc, DateTimeKind.Utc)) { DbType = System.Data.DbType.DateTime2 },
        new("@CashierId", record.CashierId),
        new("@CashierName", record.CashierName),
        new("@AuthorizedById", record.AuthorizedById) { DbType = System.Data.DbType.String },
        new("@AuthorizedByName", record.AuthorizedByName) { DbType = System.Data.DbType.String },
        new("@DisplayNumber", record.DisplayNumber) { DbType = System.Data.DbType.String },
        new("@AppVersion", record.AppVersion) { DbType = System.Data.DbType.String },
        new("@ReceivedAtUtc", DateTime.SpecifyKind(record.ReceivedAtUtc, DateTimeKind.Utc)) { DbType = System.Data.DbType.DateTime2 }
    ];

    /// <summary>只把 SQL Server 唯一键冲突（2601 / 2627）识别出来，返回异常文本以便区分是哪个索引；其它异常原样抛出。</summary>
    internal static string? UniqueIndexName(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 } sqlException)
            {
                return sqlException.Message;
            }
        }

        var text = ex.ToString();
        return text.Contains("UX_POSM_CashMovement_", StringComparison.OrdinalIgnoreCase) ? text : null;
    }
}
