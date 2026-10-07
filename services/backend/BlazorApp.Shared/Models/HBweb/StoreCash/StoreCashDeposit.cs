using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 店长把现金存进银行的一次登记。多日现金可以混在一起存：CoveredFromDate～CoveredToDate 只用来定位
/// 「还有哪几个营业日没存」，不参与金额计算；现金池余额按流水算，不按营业日配对。
/// 合计金额 = 各张存单金额之和，每张存单的照片挂在附件表上。不硬删：作废只改 Status 并留痕。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移 20261008.001-store-cash-management 创建。
/// </summary>
[SugarTable("StoreCashDeposit")]
public sealed class StoreCashDeposit
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string DepositGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>实际存款日期（门店本地日期，时间部分恒为 0 点）。</summary>
    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime DepositDate { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? CoveredFromDate { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? CoveredToDate { get; set; }

    [SugarColumn(IsNullable = false, Length = 18, DecimalDigits = 2)]
    public decimal TotalAmount { get; set; }

    [SugarColumn(IsNullable = true, Length = 500)]
    public string? Note { get; set; }

    /// <summary>存款合计与建议存款额差异过大时店长填写的原因。</summary>
    [SugarColumn(IsNullable = true, Length = 500)]
    public string? OverrideReason { get; set; }

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

    /// <summary>客户端生成的请求号，全局唯一；重复提交（弱网重试）按它幂等返回已有记录。</summary>
    [SugarColumn(IsNullable = false, Length = 64)]
    public string ClientRequestId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string CreatedByUserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? CreatedByName { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime CreatedAtUtc { get; set; }
}
