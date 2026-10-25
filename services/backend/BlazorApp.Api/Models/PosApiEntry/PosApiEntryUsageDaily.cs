using SqlSugar;

namespace BlazorApp.Api.Models.PosApiEntry;

/// <summary>
/// HBweb 库 dbo.PosApiEntryUsageDaily 的只读实体：收银端三条入口按（悉尼日期, 分店, 入口）的请求计数。
/// 表由迁移 20261026.004-pos-api-entry-usage 创建、由 Hbpos.Api 每 60 秒累加写入；主 API 只读，
/// 因此刻意不登记进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// </summary>
[SugarTable("PosApiEntryUsageDaily")]
internal sealed class PosApiEntryUsageDaily
{
    /// <summary>悉尼日期（库内为 date，只取日期部分）。</summary>
    [SugarColumn(IsPrimaryKey = true, ColumnDataType = "date")]
    public DateTime UsageDate { get; set; }

    /// <summary>分店代码；未鉴权请求（健康检查、激活等）为空串。</summary>
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>edge / aws / direct / unknown。</summary>
    [SugarColumn(IsPrimaryKey = true, Length = 16)]
    public string Entry { get; set; } = string.Empty;

    public long RequestCount { get; set; }

    public long Status4xxCount { get; set; }

    public long Status5xxCount { get; set; }

    public DateTime UpdatedAt { get; set; }
}
