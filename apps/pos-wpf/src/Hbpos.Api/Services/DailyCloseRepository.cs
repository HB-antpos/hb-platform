using Hbpos.Api.Data;
using SqlSugar;

namespace Hbpos.Api.Services;

/// <summary>POSM_DailyClose.DetailLevel 的取值；Full 之外都是回填占位，允许被客户端完整上传覆盖。</summary>
internal static class DailyCloseDetailLevels
{
    public const string Full = "Full";
    public const string CashOnly = "CashOnly";
    public const string TraceOnly = "TraceOnly";
}

/// <summary>POSM_DailyClose.DataSource 的取值。</summary>
internal static class DailyCloseDataSources
{
    public const string ClientUpload = "ClientUpload";
    public const string AuditBackfill = "AuditBackfill";
}

/// <summary>
/// POSM_DailyClose 的一行。时间列统一是 UTC 的 DateTime（SQL Server 返回 Kind=Unspecified，比较时只看 Ticks）；
/// BusinessDate 是 DATE 列，只取 .Date。非 Full 的回填行里金额类列可能为 NULL，所以这些列都是可空类型。
/// </summary>
internal sealed class PosmDailyCloseRecord
{
    public long Id { get; set; }

    public Guid DailyCloseGuid { get; set; }

    public string StoreCode { get; set; } = string.Empty;

    public string DeviceCode { get; set; } = string.Empty;

    public string ClientKind { get; set; } = string.Empty;

    public string DetailLevel { get; set; } = DailyCloseDetailLevels.Full;

    public string DataSource { get; set; } = DailyCloseDataSources.ClientUpload;

    public string? BackfillBatch { get; set; }

    public DateTime BusinessDate { get; set; }

    public bool BusinessDateInferred { get; set; }

    public DateTime? PeriodFromUtc { get; set; }

    public DateTime? PeriodToUtc { get; set; }

    public string CashierId { get; set; } = string.Empty;

    public string CashierName { get; set; } = string.Empty;

    public DateTime SavedAtUtc { get; set; }

    public string? AppVersion { get; set; }

    public int? OrderCount { get; set; }

    public decimal? ReturnQuantity { get; set; }

    public decimal? CashSalesAmount { get; set; }

    public decimal? CashRefundAmount { get; set; }

    public decimal? CashNetAmount { get; set; }

    public decimal? CardSalesAmount { get; set; }

    public decimal? CardRefundAmount { get; set; }

    public decimal? CardNetAmount { get; set; }

    public decimal? VoucherSalesAmount { get; set; }

    public decimal? VoucherRefundAmount { get; set; }

    public decimal? VoucherNetAmount { get; set; }

    public decimal? RefundAmount { get; set; }

    public decimal? ExpectedCashAmount { get; set; }

    public decimal? CountedCashAmount { get; set; }

    public decimal? CashDifference { get; set; }

    public decimal? NoteSubtotal { get; set; }

    public decimal? CoinSubtotal { get; set; }

    public string? CashCountsJson { get; set; }

    public DateTime ReceivedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

internal interface IDailyCloseRepository
{
    /// <summary>按全局唯一的 DailyCloseGuid 读取；故意不带 scope 条件，调用方要靠它识别跨设备冲突。</summary>
    Task<PosmDailyCloseRecord?> GetByGuidAsync(Guid dailyCloseGuid, CancellationToken cancellationToken);

    /// <summary>插入新行；DailyCloseGuid 唯一索引冲突（并发重复上传）时返回 false。</summary>
    Task<bool> TryInsertAsync(PosmDailyCloseRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// 用完整客户端数据覆盖同 scope 下尚未 Full 的回填占位；
    /// 影响 0 行（已被并发覆盖或不再是占位）时返回 false，由调用方重读重试。
    /// </summary>
    Task<bool> TryReplacePlaceholderAsync(PosmDailyCloseRecord record, CancellationToken cancellationToken);
}

internal sealed class SqlSugarDailyCloseRepository(
    HbposSqlSugarContext dbContext) : IDailyCloseRepository
{
    private const string SelectColumns = """
        [Id], [DailyCloseGuid], [StoreCode], [DeviceCode], [ClientKind], [DetailLevel], [DataSource], [BackfillBatch],
        [BusinessDate], [BusinessDateInferred], [PeriodFromUtc], [PeriodToUtc], [CashierId], [CashierName],
        [SavedAtUtc], [AppVersion], [OrderCount], [ReturnQuantity],
        [CashSalesAmount], [CashRefundAmount], [CashNetAmount],
        [CardSalesAmount], [CardRefundAmount], [CardNetAmount],
        [VoucherSalesAmount], [VoucherRefundAmount], [VoucherNetAmount],
        [RefundAmount], [ExpectedCashAmount], [CountedCashAmount], [CashDifference],
        [NoteSubtotal], [CoinSubtotal], [CashCountsJson], [ReceivedAtUtc], [UpdatedAtUtc]
        """;

    internal const string SelectByGuidSql = $"""
        SELECT TOP 1 {SelectColumns}
        FROM [dbo].[POSM_DailyClose]
        WHERE [DailyCloseGuid] = @DailyCloseGuid;
        """;

    internal const string InsertSql = """
        INSERT INTO [dbo].[POSM_DailyClose] (
            [DailyCloseGuid], [StoreCode], [DeviceCode], [ClientKind], [DetailLevel], [DataSource], [BackfillBatch],
            [BusinessDate], [BusinessDateInferred], [PeriodFromUtc], [PeriodToUtc], [CashierId], [CashierName],
            [SavedAtUtc], [AppVersion], [OrderCount], [ReturnQuantity],
            [CashSalesAmount], [CashRefundAmount], [CashNetAmount],
            [CardSalesAmount], [CardRefundAmount], [CardNetAmount],
            [VoucherSalesAmount], [VoucherRefundAmount], [VoucherNetAmount],
            [RefundAmount], [ExpectedCashAmount], [CountedCashAmount], [CashDifference],
            [NoteSubtotal], [CoinSubtotal], [CashCountsJson], [ReceivedAtUtc], [UpdatedAtUtc])
        VALUES (
            @DailyCloseGuid, @StoreCode, @DeviceCode, @ClientKind, @DetailLevel, @DataSource, @BackfillBatch,
            @BusinessDate, @BusinessDateInferred, @PeriodFromUtc, @PeriodToUtc, @CashierId, @CashierName,
            @SavedAtUtc, @AppVersion, @OrderCount, @ReturnQuantity,
            @CashSalesAmount, @CashRefundAmount, @CashNetAmount,
            @CardSalesAmount, @CardRefundAmount, @CardNetAmount,
            @VoucherSalesAmount, @VoucherRefundAmount, @VoucherNetAmount,
            @RefundAmount, @ExpectedCashAmount, @CountedCashAmount, @CashDifference,
            @NoteSubtotal, @CoinSubtotal, @CashCountsJson, @ReceivedAtUtc, @UpdatedAtUtc);
        """;

    // 覆盖占位：整行换成客户端完整数据，并把回填标记（BackfillBatch / BusinessDateInferred / DataSource）清回客户端上传口径。
    // WHERE 里的 [DetailLevel] <> N'Full' 是并发守卫：只允许覆盖一次，已被别的请求覆盖成 Full 时影响 0 行。
    // StoreCode / DeviceCode 不在 SET 里，且必须与 claims 一致才会命中，避免跨设备改写。
    internal const string ReplacePlaceholderSql = """
        UPDATE [dbo].[POSM_DailyClose]
        SET [ClientKind] = @ClientKind,
            [DetailLevel] = N'Full',
            [DataSource] = N'ClientUpload',
            [BackfillBatch] = NULL,
            [BusinessDate] = @BusinessDate,
            [BusinessDateInferred] = 0,
            [PeriodFromUtc] = @PeriodFromUtc,
            [PeriodToUtc] = @PeriodToUtc,
            [CashierId] = @CashierId,
            [CashierName] = @CashierName,
            [SavedAtUtc] = @SavedAtUtc,
            [AppVersion] = @AppVersion,
            [OrderCount] = @OrderCount,
            [ReturnQuantity] = @ReturnQuantity,
            [CashSalesAmount] = @CashSalesAmount,
            [CashRefundAmount] = @CashRefundAmount,
            [CashNetAmount] = @CashNetAmount,
            [CardSalesAmount] = @CardSalesAmount,
            [CardRefundAmount] = @CardRefundAmount,
            [CardNetAmount] = @CardNetAmount,
            [VoucherSalesAmount] = @VoucherSalesAmount,
            [VoucherRefundAmount] = @VoucherRefundAmount,
            [VoucherNetAmount] = @VoucherNetAmount,
            [RefundAmount] = @RefundAmount,
            [ExpectedCashAmount] = @ExpectedCashAmount,
            [CountedCashAmount] = @CountedCashAmount,
            [CashDifference] = @CashDifference,
            [NoteSubtotal] = @NoteSubtotal,
            [CoinSubtotal] = @CoinSubtotal,
            [CashCountsJson] = @CashCountsJson,
            [ReceivedAtUtc] = @ReceivedAtUtc,
            [UpdatedAtUtc] = @UpdatedAtUtc
        WHERE [DailyCloseGuid] = @DailyCloseGuid
          AND [StoreCode] = @StoreCode
          AND [DeviceCode] = @DeviceCode
          AND [DetailLevel] <> N'Full';
        """;

    public async Task<PosmDailyCloseRecord?> GetByGuidAsync(
        Guid dailyCloseGuid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<PosmDailyCloseRecord>(
            SelectByGuidSql,
            new SugarParameter("@DailyCloseGuid", dailyCloseGuid));
    }

    public async Task<bool> TryInsertAsync(
        PosmDailyCloseRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await dbContext.PosmDb.Ado.ExecuteCommandAsync(InsertSql, ToParameters(record)) == 1;
        }
        catch (Exception ex) when (IsUniqueConstraintViolation(ex))
        {
            return false;
        }
    }

    public async Task<bool> TryReplacePlaceholderAsync(
        PosmDailyCloseRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await dbContext.PosmDb.Ado.ExecuteCommandAsync(ReplacePlaceholderSql, ToParameters(record)) == 1;
    }

    /// <summary>
    /// 统一用显式 DbType：SugarParameter 对 DateTime 默认按 SQL datetime（约 3.3ms 精度）发送，
    /// 会让 DATETIME2(7) 列丢精度，导致重试上传的幂等比较误判成内容冲突；NULL 也需要带类型。
    /// </summary>
    private static SugarParameter[] ToParameters(PosmDailyCloseRecord record)
    {
        return
        [
            new("@DailyCloseGuid", record.DailyCloseGuid),
            new("@StoreCode", record.StoreCode),
            new("@DeviceCode", record.DeviceCode),
            new("@ClientKind", record.ClientKind),
            new("@DetailLevel", record.DetailLevel),
            new("@DataSource", record.DataSource),
            new("@BackfillBatch", record.BackfillBatch) { DbType = System.Data.DbType.String },
            new("@BusinessDate", record.BusinessDate.Date) { DbType = System.Data.DbType.Date },
            new("@BusinessDateInferred", record.BusinessDateInferred),
            Utc("@PeriodFromUtc", record.PeriodFromUtc),
            Utc("@PeriodToUtc", record.PeriodToUtc),
            new("@CashierId", record.CashierId),
            new("@CashierName", record.CashierName),
            Utc("@SavedAtUtc", record.SavedAtUtc),
            new("@AppVersion", record.AppVersion) { DbType = System.Data.DbType.String },
            new("@OrderCount", record.OrderCount) { DbType = System.Data.DbType.Int32 },
            Money("@ReturnQuantity", record.ReturnQuantity),
            Money("@CashSalesAmount", record.CashSalesAmount),
            Money("@CashRefundAmount", record.CashRefundAmount),
            Money("@CashNetAmount", record.CashNetAmount),
            Money("@CardSalesAmount", record.CardSalesAmount),
            Money("@CardRefundAmount", record.CardRefundAmount),
            Money("@CardNetAmount", record.CardNetAmount),
            Money("@VoucherSalesAmount", record.VoucherSalesAmount),
            Money("@VoucherRefundAmount", record.VoucherRefundAmount),
            Money("@VoucherNetAmount", record.VoucherNetAmount),
            Money("@RefundAmount", record.RefundAmount),
            Money("@ExpectedCashAmount", record.ExpectedCashAmount),
            Money("@CountedCashAmount", record.CountedCashAmount),
            Money("@CashDifference", record.CashDifference),
            Money("@NoteSubtotal", record.NoteSubtotal),
            Money("@CoinSubtotal", record.CoinSubtotal),
            new("@CashCountsJson", record.CashCountsJson) { DbType = System.Data.DbType.String },
            Utc("@ReceivedAtUtc", record.ReceivedAtUtc),
            Utc("@UpdatedAtUtc", record.UpdatedAtUtc)
        ];
    }

    private static SugarParameter Utc(string name, DateTime? value) =>
        new(name, value is null ? null : (object)DateTime.SpecifyKind(value.Value, DateTimeKind.Utc))
        {
            DbType = System.Data.DbType.DateTime2
        };

    private static SugarParameter Money(string name, decimal? value) =>
        new(name, value is null ? null : (object)value.Value)
        {
            DbType = System.Data.DbType.Decimal
        };

    /// <summary>
    /// 只把 SQL Server 的唯一键冲突（2601 / 2627）当成「并发重复插入」；
    /// 其它异常（超时、CHECK 约束等）必须原样抛出，不能被当成可重试。SqlSugar 可能包一层，所以沿内部异常链找。
    /// </summary>
    internal static bool IsUniqueConstraintViolation(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
            {
                return true;
            }
        }

        return ex.ToString().Contains("UX_POSM_DailyClose_Guid", StringComparison.OrdinalIgnoreCase);
    }
}
