using SqlSugar;

namespace BlazorApp.Api.Models.PosApiEntry;

/// <summary>
/// HBweb 库 dbo.MainApiEntryUsageDaily 的实体：主 API（Web 后台 + 员工 App）三条入口按（悉尼日期, 入口, 客户端类别）的请求计数。
/// 表由迁移 20261027.001-main-api-entry-usage 创建、由主 API 自己每 60 秒累加写入（写入走原生 SQL upsert）并只读查询，
/// 因此刻意不登记进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// </summary>
[SugarTable("MainApiEntryUsageDaily")]
internal sealed class MainApiEntryUsageDaily
{
    /// <summary>悉尼日期（库内为 date，只取日期部分）。</summary>
    [SugarColumn(IsPrimaryKey = true, ColumnDataType = "date")]
    public DateTime UsageDate { get; set; }

    /// <summary>edge / aws / direct / unknown。</summary>
    [SugarColumn(IsPrimaryKey = true, Length = 16)]
    public string Entry { get; set; } = string.Empty;

    /// <summary>web / app / other。</summary>
    [SugarColumn(IsPrimaryKey = true, Length = 16)]
    public string ClientKind { get; set; } = string.Empty;

    public long RequestCount { get; set; }

    public long Status4xxCount { get; set; }

    public long Status5xxCount { get; set; }

    public DateTime UpdatedAt { get; set; }
}
