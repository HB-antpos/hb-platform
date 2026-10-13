using BlazorApp.Shared.DTOs;
using SqlSugar;

namespace BlazorApp.Api.Interfaces.React;

public interface IProductMaintenanceHqProjectionWriter
{
    Task<ProductHqSyncOperationStatusDto> EnqueueAsync(
        ISqlSugarClient transactionDb,
        ProductMaintenanceHqMutationRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ProductHqSyncOutboxExecutionResult> ApplyAsync(
        ProductHqSyncOutboxWorkItemDto workItem,
        CancellationToken cancellationToken = default
    );
}

public sealed class ProductMaintenanceHqMutationRequest
{
    public string OperationKind { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public IReadOnlyCollection<string>? TargetStoreCodes { get; init; }
    public IReadOnlyCollection<string>? AuthorizedStoreCodes { get; init; }
    public IReadOnlyCollection<string> FieldMask { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<ProductHqSyncOutboxTombstoneDto> Tombstones { get; init; } =
        Array.Empty<ProductHqSyncOutboxTombstoneDto>();
    public string? RequestedByUserGuid { get; init; }
    public string? RequestedByDeviceId { get; init; }
    public string Source { get; init; } = string.Empty;
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

public static class ProductMaintenanceHqOperationKinds
{
    public const string ProductCreated = "product-created";
    public const string ProductTypeUpdated = "product-type-updated";
    public const string ProductNameUpdated = "product-name-updated";
    public const string StorePriceUpdated = "store-price-updated";
    public const string WarehousePriceSynced = "warehouse-price-synced";
    public const string ProductCodesUpdated = "product-codes-updated";
    public const string ProductCodesDeleted = "product-codes-deleted";
    public const string SetCodeSnapshot = "set-code-snapshot";
    public const string ClearancePriceUpdated = "clearance-price-updated";
    public const string ClearancePriceDeleted = "clearance-price-deleted";
}

public static class ProductMaintenanceHqFieldMasks
{
    public const string All = "all";
    public const string ProductType = "productType";
    /// <summary>中文名称（Product.ProductName）；HQ 侧只更新「大写名称」列，英文显示名不受影响。</summary>
    public const string ProductName = "productName";
    public const string StorePurchasePrice = "storePurchasePrice";
    public const string StoreRetailPrice = "storeRetailPrice";
    public const string StoreDiscountRate = "storeDiscountRate";
    public const string StoreAutoPricing = "storeAutoPricing";
    public const string StoreSpecialProduct = "storeSpecialProduct";
    public const string StoreActive = "storeActive";
    public const string ProductSetCodes = "productSetCodes";
    public const string StoreMultiCodes = "storeMultiCodes";
    public const string StoreClearancePrice = "storeClearancePrice";

    public static readonly IReadOnlyList<string> StorePriceAndMultiCode = new[]
    {
        StorePurchasePrice,
        StoreRetailPrice,
        StoreDiscountRate,
        StoreAutoPricing,
        StoreSpecialProduct,
        StoreActive,
        StoreMultiCodes,
    };
}

public static class ProductMaintenanceHqResourceKinds
{
    public const string ProductSetCode = "product-set-code";
    public const string StoreMultiCode = "store-multi-code";
    public const string StoreClearancePrice = "store-clearance-price";
}
