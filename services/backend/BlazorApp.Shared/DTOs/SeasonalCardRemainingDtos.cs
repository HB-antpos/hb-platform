namespace BlazorApp.Shared.DTOs
{
    public enum SeasonalCardType
    {
        Christmas = 1,
        ValentinesDay = 2,
        MothersDay = 3,
        Easter = 4,
        FathersDay = 5,
    }

    public enum SeasonalCardPriceOptionType
    {
        FixedOneDollar = 1,
        FixedTwoDollars = 2,
        FixedThreeDollars = 3,
        Other = 4,
    }

    public class SeasonalCardCatalogDto
    {
        public string CatalogGuid { get; set; } = string.Empty;
        public string CatalogCode { get; set; } = string.Empty;
        public SeasonalCardType CardType { get; set; }
        public string CardTypeName { get; set; } = string.Empty;
        public SeasonalCardPriceOptionType PriceOption { get; set; }
        public string PriceOptionName { get; set; } = string.Empty;
        public string PriceLabel { get; set; } = string.Empty;
        public decimal? FixedUnitPrice { get; set; }
        public bool AllowsCustomUnitPrice { get; set; }
        public bool IsEnabled { get; set; }
        public int SortOrder { get; set; }
    }

    public class CreateSeasonalCardRemainingSubmissionDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string CatalogGuid { get; set; } = string.Empty;
        public int SeasonYear { get; set; }
        public int RemainingQuantity { get; set; }
        public decimal? CustomUnitPrice { get; set; }
        public string? Remark { get; set; }
    }

    public class SeasonalCardRemainingSubmissionQueryDto
    {
        public string? StoreCode { get; set; }
        public SeasonalCardType? CardType { get; set; }
        public int? SeasonYear { get; set; }
        public string? LocalSupplierCode { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }

    public class SeasonalCardRemainingSubmissionDto
    {
        public string SubmissionGuid { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public string CatalogGuid { get; set; } = string.Empty;
        public string CatalogCode { get; set; } = string.Empty;
        public SeasonalCardType CardType { get; set; }
        public string CardTypeName { get; set; } = string.Empty;
        public SeasonalCardPriceOptionType PriceOption { get; set; }
        public string PriceOptionName { get; set; } = string.Empty;
        public string PriceLabel { get; set; } = string.Empty;
        public int SeasonYear { get; set; }
        public int RemainingQuantity { get; set; }
        public decimal UnitPrice { get; set; }
        public string? Remark { get; set; }
        public string SubmittedByUserGuid { get; set; } = string.Empty;
        public string SubmittedByName { get; set; } = string.Empty;
        public DateTime SubmittedAt { get; set; }
        public string? LocalSupplierCode { get; set; }
        public string? SupplierName { get; set; }
        public string? BatchGuid { get; set; }
    }

    // ---- 批量填报：一个分店 + 年份 + 节日 + 供应商的全部价格一次整组提交 ----

    public class SeasonalCardBatchItemDto
    {
        public string CatalogGuid { get; set; } = string.Empty;
        public int RemainingQuantity { get; set; }
        public decimal? CustomUnitPrice { get; set; }
    }

    public class CreateSeasonalCardRemainingBatchDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public int SeasonYear { get; set; }
        public SeasonalCardType CardType { get; set; }
        public string LocalSupplierCode { get; set; } = string.Empty;

        /// <summary>客户端预填时看到的当前生效批次号（没填过为 null）；与服务端最新批次不一致即判定为已被他人更新。</summary>
        public string? ExpectedPreviousBatchGuid { get; set; }

        public string? Remark { get; set; }
        public List<SeasonalCardBatchItemDto> Items { get; set; } = new();
    }

    public class SeasonalCardBatchLineDto
    {
        public string SubmissionGuid { get; set; } = string.Empty;
        public string CatalogGuid { get; set; } = string.Empty;
        public SeasonalCardPriceOptionType PriceOption { get; set; }
        public string PriceLabel { get; set; } = string.Empty;
        public decimal UnitPrice { get; set; }
        public int RemainingQuantity { get; set; }
    }

    public class SeasonalCardBatchDto
    {
        /// <summary>批次号；批量填报前的历史单条记录没有批次号，为 null。</summary>
        public string? BatchGuid { get; set; }
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public int SeasonYear { get; set; }
        public SeasonalCardType CardType { get; set; }
        public string CardTypeName { get; set; } = string.Empty;
        public string? LocalSupplierCode { get; set; }
        public string? SupplierName { get; set; }
        public string? Remark { get; set; }
        public string SubmittedByName { get; set; } = string.Empty;
        public DateTime SubmittedAt { get; set; }
        public int TotalQuantity { get; set; }
        public decimal TotalAmount { get; set; }

        /// <summary>是否为该组合当前生效的数据（统计以它为准）；被更新的批次为 false。</summary>
        public bool IsCurrent { get; set; }

        public List<SeasonalCardBatchLineDto> Lines { get; set; } = new();
    }

    public class SeasonalCardOverviewQueryDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public int SeasonYear { get; set; }
        public string LocalSupplierCode { get; set; } = string.Empty;
    }

    public class SeasonalCardOverviewHolidayDto
    {
        public SeasonalCardType CardType { get; set; }
        public string CardTypeName { get; set; } = string.Empty;

        /// <summary>该节日 + 供应商当前生效的批次；没填过为 null。</summary>
        public SeasonalCardBatchDto? CurrentBatch { get; set; }
    }

    public class SeasonalCardOverviewDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public int SeasonYear { get; set; }
        public string LocalSupplierCode { get; set; } = string.Empty;
        public string? SupplierName { get; set; }
        public List<SeasonalCardOverviewHolidayDto> Holidays { get; set; } = new();
    }

    // ---- 后台分店填报统计 ----

    public class SeasonalCardStatsQueryDto
    {
        public int SeasonYear { get; set; }
        public SeasonalCardType CardType { get; set; }
        public string? LocalSupplierCode { get; set; }
        public SeasonalCardPriceOptionType? PriceOption { get; set; }
        public List<string>? StoreCodes { get; set; }
    }

    public class SeasonalCardStatsStoreRefDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
    }

    public class SeasonalCardStatsSupplierRefDto
    {
        public string? LocalSupplierCode { get; set; }
        public string SupplierName { get; set; } = string.Empty;
    }

    public class SeasonalCardStatsPriceQuantityDto
    {
        public SeasonalCardPriceOptionType PriceOption { get; set; }
        public string PriceLabel { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Amount { get; set; }
    }

    public class SeasonalCardStatsStoreRowDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
        public bool IsFilled { get; set; }
        public List<SeasonalCardStatsPriceQuantityDto> Prices { get; set; } = new();
        public int TotalQuantity { get; set; }
        public decimal TotalAmount { get; set; }
        public List<SeasonalCardStatsSupplierRefDto> Suppliers { get; set; } = new();
        public DateTime? LastSubmittedAt { get; set; }
        public string? LastSubmittedByName { get; set; }
    }

    public class SeasonalCardStatsSupplierTotalDto
    {
        public string? LocalSupplierCode { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public int StoreCount { get; set; }
        public int Quantity { get; set; }
        public decimal Amount { get; set; }
    }

    public class SeasonalCardStatsSummaryDto
    {
        public int SeasonYear { get; set; }
        public SeasonalCardType CardType { get; set; }
        public string CardTypeName { get; set; } = string.Empty;
        public int StoreCount { get; set; }
        public int FilledStoreCount { get; set; }
        public int UnfilledStoreCount { get; set; }
        public int TotalQuantity { get; set; }
        public decimal TotalAmount { get; set; }
        public List<SeasonalCardStatsStoreRowDto> Stores { get; set; } = new();
        public List<SeasonalCardStatsPriceQuantityDto> PriceTotals { get; set; } = new();
        public List<SeasonalCardStatsSupplierTotalDto> SupplierTotals { get; set; } = new();
        public List<SeasonalCardStatsStoreRefDto> UnfilledStores { get; set; } = new();

        /// <summary>不参与统计的分店（测试店、仓库），供页面注明口径。</summary>
        public List<SeasonalCardStatsStoreRefDto> ExcludedStores { get; set; } = new();
    }

    public class SeasonalCardStatsStoreDetailQueryDto
    {
        public int SeasonYear { get; set; }
        public SeasonalCardType CardType { get; set; }
    }

    public class SeasonalCardStatsStoreDetailDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
        public int SeasonYear { get; set; }
        public SeasonalCardType CardType { get; set; }
        public string CardTypeName { get; set; } = string.Empty;
        public bool IsFilled { get; set; }
        public int TotalQuantity { get; set; }
        public decimal TotalAmount { get; set; }

        /// <summary>每个供应商当前生效的数据（供应商 × 价格矩阵）。</summary>
        public List<SeasonalCardBatchDto> CurrentBatches { get; set; } = new();

        /// <summary>全部提交批次，按提交时间倒序；IsCurrent 标出仍生效的批次。</summary>
        public List<SeasonalCardBatchDto> History { get; set; } = new();

        /// <summary>去年同节日当前生效的剩余数量合计；去年没填为 null。</summary>
        public int? PreviousYearTotalQuantity { get; set; }
    }
}
