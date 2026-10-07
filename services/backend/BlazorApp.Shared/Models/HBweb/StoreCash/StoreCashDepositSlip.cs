using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 一次存款登记下的一张存单：金额 + 照片（照片在附件表，OwnerType = Slip）。
/// 后续银行对账按存单粒度匹配银行入账流水，所以存单金额不能被合并。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移 20261008.001-store-cash-management 创建。
/// </summary>
[SugarTable("StoreCashDepositSlip")]
public sealed class StoreCashDepositSlip
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string SlipGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(IsNullable = false, Length = 50)]
    public string DepositGuid { get; set; } = string.Empty;

    /// <summary>冗余的门店代码，便于按分店范围直接过滤存单。</summary>
    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 18, DecimalDigits = 2)]
    public decimal Amount { get; set; }

    /// <summary>银行存单上的流水号或凭条号，可选。</summary>
    [SugarColumn(IsNullable = true, Length = 100)]
    public string? SlipNo { get; set; }

    [SugarColumn(IsNullable = false)]
    public int SortOrder { get; set; }
}
