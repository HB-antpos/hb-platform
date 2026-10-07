using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 店长动用现金的支出：现金工资（Salary）、现金购物（Purchase）、T2、其他（Other）。
/// 店长录入即生效，不走审核；风控靠限制补录天数、购物必须拍收据、财务事后打核对标记（ReviewStatus，不影响生效）。
/// T2 对店长只开放自己管理的分店最近 14 天，可见性规则集中在 CashVisibilityRules，列表、详情、汇总、导出都要经过它。
/// 不硬删：作废只改 Status 并留痕。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移 20261008.001-store-cash-management 创建。
/// </summary>
[SugarTable("StoreCashExpense")]
public sealed class StoreCashExpense
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string ExpenseGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>支出发生日期（门店本地日期，时间部分恒为 0 点）。</summary>
    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime ExpenseDate { get; set; }

    /// <summary>Salary / Purchase / T2 / Other。</summary>
    [SugarColumn(IsNullable = false, Length = 20)]
    public string Category { get; set; } = "Other";

    [SugarColumn(IsNullable = false, Length = 18, DecimalDigits = 2)]
    public decimal Amount { get; set; }

    /// <summary>工资支出可选关联的员工。</summary>
    [SugarColumn(IsNullable = true, Length = 50)]
    public string? PayeeUserGuid { get; set; }

    /// <summary>收款人姓名或商家名称，可选；关联员工时由服务端补成员工姓名。</summary>
    [SugarColumn(IsNullable = true, Length = 100)]
    public string? PayeeName { get; set; }

    [SugarColumn(IsNullable = true, Length = 500)]
    public string? Note { get; set; }

    /// <summary>None / Reviewed / Flagged：财务事后核对标记，不影响支出生效。</summary>
    [SugarColumn(IsNullable = false, Length = 20)]
    public string ReviewStatus { get; set; } = "None";

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? ReviewedByUserGuid { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? ReviewedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 500)]
    public string? ReviewNote { get; set; }

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

    /// <summary>客户端生成的请求号，全局唯一；重复提交按它幂等返回已有记录。</summary>
    [SugarColumn(IsNullable = false, Length = 64)]
    public string ClientRequestId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string CreatedByUserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? CreatedByName { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime CreatedAtUtc { get; set; }
}
