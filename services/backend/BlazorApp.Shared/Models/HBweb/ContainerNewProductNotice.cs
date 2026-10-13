using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 新品到店每日推送：该门店「累计已见过的新品」集合，是「比昨天新增」的基线。
/// 基线必须是累计集合，不能是「昨天推送的增量」——否则前天已推、今天仍在列表里的商品会被当成新增重复推送。
/// 表由独立迁移 20261015.002-container-new-product-notice 创建，实体不进自动建表清单。
/// </summary>
[SugarTable("ContainerNewProductSeen")]
public sealed class ContainerNewProductSeen
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>首次见到它的门店本地日期（只取日期部分）；首次基线日记录的是基线当天。</summary>
    [SugarColumn(IsNullable = false, ColumnDataType = "date")]
    public DateTime FirstLocalDate { get; set; }

    /// <summary>首次见到它时，列表里该商品所在货柜（同商品多柜时取到店最早的一柜），仅供排查。</summary>
    [SugarColumn(IsNullable = false, Length = 50)]
    public string ContainerCode { get; set; } = string.Empty;

    /// <summary>首次见到它时发出的通知；基线日、或当天没有接收人时为空。</summary>
    [SugarColumn(IsNullable = true, Length = 32)]
    public string? NotificationId { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>新品到店每日推送：某门店某个本地日期已处理过的标记，同一天不重复处理（含基线日与零新增日）。</summary>
[SugarTable("ContainerNewProductRun")]
public sealed class ContainerNewProductRun
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>门店本地日期（只取日期部分）。</summary>
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, ColumnDataType = "date")]
    public DateTime LocalDate { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime ProcessedAtUtc { get; set; }

    /// <summary>当天相对累计已见集合新增的商品数。</summary>
    [SugarColumn(IsNullable = false)]
    public int NewCount { get; set; }

    /// <summary>首次基线：只登记当前列表、不发通知。</summary>
    [SugarColumn(IsNullable = false)]
    public bool IsBaseline { get; set; }

    /// <summary>当天发出（或幂等命中）的通知；没有新增、基线日、或没有接收人时为空。</summary>
    [SugarColumn(IsNullable = true, Length = 32)]
    public string? NotificationId { get; set; }
}
