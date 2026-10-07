using SqlSugar;

namespace BlazorApp.Api.Models.DailyClose;

/// <summary>
/// POSM 库 dbo.POSM_DailyClose 的后台只读实体。
/// 表由 Hbpos.Api 启动时的 DailyCloseSchemaInitializer 创建，后台不建表、不写入，
/// 因此刻意不登记进 SqlSugarContext 的 tableTypes，也不参与后台启动时的 CodeFirst。
/// </summary>
[SugarTable("POSM_DailyClose")]
internal sealed class PosmDailyClose
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    public Guid DailyCloseGuid { get; set; }
    public string StoreCode { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>Wpf | Handheld | Ipad。</summary>
    public string ClientKind { get; set; } = string.Empty;

    /// <summary>Full | CashOnly | TraceOnly。</summary>
    public string DetailLevel { get; set; } = string.Empty;

    /// <summary>ClientUpload | AuditBackfill。</summary>
    public string DataSource { get; set; } = string.Empty;

    public string? BackfillBatch { get; set; }

    /// <summary>营业日（库内为 DATE）。</summary>
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

    /// <summary>实点 − 应有；正为长款，负为短款，NULL 表示没有金额（TraceOnly）。</summary>
    public decimal? CashDifference { get; set; }

    public decimal? NoteSubtotal { get; set; }
    public decimal? CoinSubtotal { get; set; }

    /// <summary>[{"denominationCents":10000,"quantity":8},...]，11 档；非 Full 为 NULL。</summary>
    public string? CashCountsJson { get; set; }

    public DateTime ReceivedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>日结记录查询参数非法（日期格式、区间、分页、枚举值），控制器统一映射为 400。</summary>
public sealed class DailyCloseRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
