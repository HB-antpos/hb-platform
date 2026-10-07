using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 现金池的期初与盘点记录。
/// Opening：每店至多一条有效，表示 EntryDate 当天开始时店里已有的现金（点钱前已取出的备用金不计入），
/// 现金池只统计 EntryDate 及以后的日结、存款和支出；上线前的日结不追溯。
/// Count：店长盘点，表示 EntryDate 当天结束时店里实际现金，与现金池余额比对出差异。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移 20261008.001-store-cash-management 创建。
/// </summary>
[SugarTable("StoreCashBalanceEntry")]
public sealed class StoreCashBalanceEntry
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string EntryGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>Opening / Count。</summary>
    [SugarColumn(IsNullable = false, Length = 20)]
    public string EntryType { get; set; } = "Count";

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime EntryDate { get; set; }

    [SugarColumn(IsNullable = false, Length = 18, DecimalDigits = 2)]
    public decimal Amount { get; set; }

    /// <summary>仅盘点：录入当时按现金池算出的应有余额，用来留存当时的差异依据；日结未接入时为 NULL。</summary>
    [SugarColumn(IsNullable = true, Length = 18, DecimalDigits = 2)]
    public decimal? ExpectedAmount { get; set; }

    [SugarColumn(IsNullable = true, Length = 500)]
    public string? Note { get; set; }

    /// <summary>Active / Voided。</summary>
    [SugarColumn(IsNullable = false, Length = 20)]
    public string Status { get; set; } = "Active";

    [SugarColumn(IsNullable = true, Length = 500)]
    public string? VoidReason { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? VoidedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? VoidedByUserGuid { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? VoidedByName { get; set; }

    /// <summary>客户端请求号，全局唯一，幂等键。</summary>
    [SugarColumn(IsNullable = false, Length = 64)]
    public string ClientRequestId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string CreatedByUserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? CreatedByName { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime CreatedAtUtc { get; set; }
}
