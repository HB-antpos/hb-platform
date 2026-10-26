using System;
using System.Collections.Generic;

namespace BlazorApp.Shared.DTOs
{
    public static class PromotionStoreScopeTypes
    {
        public const string StoreOnly = "StoreOnly";
        public const string MultiStore = "MultiStore";
        public const string Headquarters = "Headquarters";
    }

    public class PromotionListDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime EffectiveStart { get; set; }
        public DateTime EffectiveEnd { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsExclusive { get; set; }
        public int Priority { get; set; }
        public int ApplyQuantity { get; set; }
        public decimal FixedPrice { get; set; }
        public int ProductsCount { get; set; }
        public int StoresCount { get; set; }
        public string? ScopeType { get; set; }
        public bool CanEditInStoreScope { get; set; }
        public bool CanCopyToStore { get; set; }
    }

    public class PromotionDetailDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime EffectiveStart { get; set; }
        public DateTime EffectiveEnd { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsExclusive { get; set; }
        public int Priority { get; set; }
        public int ApplyQuantity { get; set; }
        public decimal FixedPrice { get; set; }
        public int? MaxApplicationsPerOrder { get; set; }
        public List<PromotionProductItemDto> Products { get; set; } = new();
        public List<PromotionStoreItemDto> Stores { get; set; } = new();
        public string? ScopeType { get; set; }
        public bool CanEditInStoreScope { get; set; }
        public bool CanCopyToStore { get; set; }
    }

    public class PromotionProductItemDto
    {
        public string Id { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public int UnitWeight { get; set; } = 1;
    }

    public class PromotionStoreItemDto
    {
        public string Id { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
    }

    public class CreatePromotionDto
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime EffectiveStart { get; set; }
        public DateTime EffectiveEnd { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool IsExclusive { get; set; } = true;
        public int Priority { get; set; } = 0;
        public int ApplyQuantity { get; set; }
        public decimal FixedPrice { get; set; }
        public int? MaxApplicationsPerOrder { get; set; }
        public List<PromotionProductItemDto> Products { get; set; } = new();
        public List<PromotionStoreItemDto> Stores { get; set; } = new();
    }

    public class UpdatePromotionDto
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime EffectiveStart { get; set; }
        public DateTime EffectiveEnd { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool IsExclusive { get; set; } = true;
        public int Priority { get; set; } = 0;
        public int ApplyQuantity { get; set; }
        public decimal FixedPrice { get; set; }
        public int? MaxApplicationsPerOrder { get; set; }
        public List<PromotionProductItemDto> Products { get; set; } = new();
        public List<PromotionStoreItemDto> Stores { get; set; } = new();
    }

    public class StorePromotionGridRequestDto : GridRequestDto
    {
        public string StoreCode { get; set; } = string.Empty;

        /// <summary>
        /// 店级列表的状态分段：active（生效中）、upcoming（未开始）、disabled（已停用且未结束）、
        /// ended（已结束）；为空或 all 表示全部。取值见 <see cref="PromotionStoreStatusFilters"/>。
        /// </summary>
        public string? Status { get; set; }
    }

    public static class PromotionStoreStatusFilters
    {
        public const string All = "all";
        public const string Active = "active";
        public const string Upcoming = "upcoming";
        public const string Disabled = "disabled";
        public const string Ended = "ended";
    }

    /// <summary>店级列表各状态分段的数量（与 Status 筛选口径一致）。</summary>
    public class PromotionStoreStatusCountsDto
    {
        public int Active { get; set; }
        public int Upcoming { get; set; }
        public int Disabled { get; set; }
        public int Ended { get; set; }
        public int Total { get; set; }
    }

    public class CopyStorePromotionRequestDto
    {
        public string SourcePromotionId { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string? Name { get; set; }
    }

    public class PromotionEvaluateRequest
    {
        public string StoreCode { get; set; } = string.Empty;
        public List<CartItemInputDto> Items { get; set; } = new();
    }

    public class CartItemInputDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public int Qty { get; set; }
        public decimal UnitPrice { get; set; }
    }

    public class PromotionEvaluateResponse
    {
        public List<AppliedPromotionInfo> AppliedPromotions { get; set; } = new();
        public List<PriceAdjustmentDto> AdjustedItems { get; set; } = new();
        public decimal TotalDiscount { get; set; }
    }

    public class AppliedPromotionInfo
    {
        public string PromotionId { get; set; } = string.Empty;
        public int AppliedBundles { get; set; }
    }

    public class PriceAdjustmentDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public int QtyAdjusted { get; set; }
        public decimal AdjustedUnitPrice { get; set; }
    }
}
