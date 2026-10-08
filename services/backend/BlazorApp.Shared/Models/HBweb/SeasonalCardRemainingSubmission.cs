using BlazorApp.Shared.DTOs;
using SqlSugar;

namespace BlazorApp.Shared.Models
{
    [SugarTable("SeasonalCardRemainingSubmission")]
    public class SeasonalCardRemainingSubmission : BaseEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
        public string SubmissionGuid { get; set; } = Guid.NewGuid().ToString();

        [SugarColumn(IsNullable = false, Length = 50)]
        public string StoreCode { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 50)]
        public string CatalogGuid { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 100)]
        public string CatalogCode { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false)]
        public SeasonalCardType CardType { get; set; }

        [SugarColumn(IsNullable = false)]
        public SeasonalCardPriceOptionType PriceOption { get; set; }

        [SugarColumn(IsNullable = false, Length = 20)]
        public string PriceLabel { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false)]
        public decimal UnitPrice { get; set; }

        [SugarColumn(IsNullable = false)]
        public int SeasonYear { get; set; }

        [SugarColumn(IsNullable = false)]
        public int RemainingQuantity { get; set; }

        [SugarColumn(IsNullable = true, Length = 500)]
        public string? Remark { get; set; }

        [SugarColumn(IsNullable = false)]
        public DateTime SubmittedAt { get; set; }

        [SugarColumn(IsNullable = false, Length = 50)]
        public string SubmittedByUserGuid { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 100)]
        public string SubmittedByName { get; set; } = string.Empty;

        // 以下三列由迁移 20261009.001 加出（可空）：批量填报前的历史行没有供应商与批次号。
        // 供应商编码对应 LocalSupplier.LocalSupplierCode；名称为提交时服务端写入的快照。
        [SugarColumn(IsNullable = true, Length = 64)]
        public string? LocalSupplierCode { get; set; }

        [SugarColumn(IsNullable = true, Length = 128)]
        public string? SupplierName { get; set; }

        // 同一次整组提交（一个节日 + 供应商的全部价格）共用一个批次号；覆盖判定与历史时间线都按批次计。
        [SugarColumn(IsNullable = true, Length = 50)]
        public string? BatchGuid { get; set; }
    }
}
