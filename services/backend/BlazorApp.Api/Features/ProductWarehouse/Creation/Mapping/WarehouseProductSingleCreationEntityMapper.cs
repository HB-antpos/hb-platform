using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Features.ProductWarehouse;

/// <summary>
/// 单商品创建请求到各持久化实体的集中映射；写入顺序仍由 Command Writer 决定。
/// </summary>
internal static class WarehouseProductSingleCreationEntityMapper
{
    internal static Product CreateProduct(
        string productCode,
        string itemNumber,
        string? barcode,
        string? imageUrl,
        CreateSingleProductRequestDto request,
        string updatedBy,
        DateTime now
    ) =>
        new()
        {
            ProductCode = productCode,
            ItemNumber = itemNumber,
            Barcode = barcode,
            LocalSupplierCode = "200",
            ProductName = request.ChineseName,
            EnglishName = request.EnglishName,
            PurchasePrice = request.ImportPrice,
            ProductImage = imageUrl,
            IsAutoPricing = false,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = updatedBy,
            UpdatedBy = updatedBy,
        };

    internal static DomesticProduct CreateDomesticProduct(
        string productCode,
        string itemNumber,
        string? barcode,
        string? imageUrl,
        CreateSingleProductRequestDto request,
        string updatedBy,
        DateTime now
    ) =>
        new()
        {
            ProductCode = productCode,
            HBProductNo = itemNumber,
            Barcode = barcode,
            ProductName = request.ChineseName,
            EnglishProductName = request.EnglishName,
            SupplierCode = request.SupplierCode ?? request.SupplierId?.ToString(),
            DomesticPrice = request.DomesticPrice,
            OEMPrice = request.OEMPrice,
            ImportPrice = request.ImportPrice,
            UnitVolume = request.Volume,
            ProductSpecification = NormalizeSpecification(request.ProductSpecification),
            PackingQuantity = request.PackingQuantity,
            MiddlePackQuantity = request.MiddlePackQuantity,
            ProductType = (int)request.ProductType,
            ProductImage = imageUrl,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = updatedBy,
            UpdatedBy = updatedBy,
        };

    internal static void ApplyDomesticProductUpdate(
        DomesticProduct domesticProduct,
        string? barcode,
        CreateSingleProductRequestDto request,
        string updatedBy,
        DateTime now
    )
    {
        domesticProduct.ProductName = request.ChineseName;
        domesticProduct.EnglishProductName = request.EnglishName;
        domesticProduct.Barcode = barcode;
        domesticProduct.DomesticPrice = request.DomesticPrice;
        domesticProduct.OEMPrice = request.OEMPrice;
        domesticProduct.ImportPrice = request.ImportPrice;
        domesticProduct.UnitVolume = request.Volume;
        // 规格/装箱数/中包数量是可选项：请求没传就保留库里已有值，不能当作清空。
        var specification = NormalizeSpecification(request.ProductSpecification);
        if (specification != null)
        {
            domesticProduct.ProductSpecification = specification;
        }
        if (request.PackingQuantity.HasValue)
        {
            domesticProduct.PackingQuantity = request.PackingQuantity;
        }
        if (request.MiddlePackQuantity.HasValue)
        {
            domesticProduct.MiddlePackQuantity = request.MiddlePackQuantity;
        }
        domesticProduct.ProductType = (int)request.ProductType;
        domesticProduct.UpdatedAt = now;
        domesticProduct.UpdatedBy = updatedBy;
    }

    internal static WarehouseProduct CreateWarehouseProduct(
        string productCode,
        CreateSingleProductRequestDto request,
        string updatedBy,
        DateTime now
    ) =>
        new()
        {
            ProductCode = productCode,
            DomesticPrice = request.DomesticPrice,
            OEMPrice = request.OEMPrice,
            ImportPrice = request.ImportPrice,
            Volume = request.Volume,
            PackingQuantity = request.PackingQuantity,
            StockQuantity = 0,
            IsActive = request.IsActive,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = updatedBy,
            UpdatedBy = updatedBy,
        };

    internal static void ApplyWarehouseProductUpdate(
        WarehouseProduct warehouseProduct,
        CreateSingleProductRequestDto request,
        string updatedBy,
        DateTime now
    )
    {
        warehouseProduct.DomesticPrice = request.DomesticPrice;
        warehouseProduct.OEMPrice = request.OEMPrice;
        warehouseProduct.ImportPrice = request.ImportPrice;
        warehouseProduct.Volume = request.Volume;
        if (request.PackingQuantity.HasValue)
        {
            warehouseProduct.PackingQuantity = request.PackingQuantity;
        }
        warehouseProduct.IsActive = request.IsActive;
        warehouseProduct.UpdatedAt = now;
        warehouseProduct.UpdatedBy = updatedBy;
    }

    internal static (DomesticSetProduct Domestic, ProductSetCode Global) CreateSetRows(
        string productCode,
        SetItemDto setItem,
        int setType,
        decimal defaultImportPrice,
        decimal defaultRetailPrice,
        DateTime now
    )
    {
        var domestic = new DomesticSetProduct
        {
            ProductCode = productCode,
            SetProductCode = UuidHelper.GenerateUuid7(),
            SetProductNo = setItem.ItemNumber,
            SetBarcode = setItem.Barcode,
            ImportPrice = setItem.PurchasePrice,
            OEMPrice = setItem.RetailPrice,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var global = new ProductSetCode
        {
            SetCodeId = setItem.ProductCode,
            ProductCode = productCode,
            SetProductCode = domestic.SetProductCode,
            SetItemNumber = setItem.ItemNumber,
            SetBarcode = setItem.Barcode,
            SetPurchasePrice = IsCostDerivedSetType(setType)
                ? null
                : setItem.PurchasePrice ?? defaultImportPrice,
            SetRetailPrice = setItem.RetailPrice ?? defaultRetailPrice,
            SetQuantity = (int)setItem.Quantity,
            SetType = setType,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        return (domestic, global);
    }

    internal static StoreMultiCodeProduct CreateProjectedSetRow(
        string productCode,
        string storeCode,
        ProductSetCode setCode,
        string updatedBy,
        DateTime now
    ) =>
        new()
        {
            UUID = UuidHelper.GenerateUuid7(),
            ProductCode = productCode,
            StoreCode = storeCode,
            MultiCodeProductCode = setCode.SetProductCode,
            StoreMultiCodeProductCode = storeCode + setCode.SetProductCode,
            MultiBarcode = setCode.SetBarcode,
            PurchasePrice = IsCostDerivedSetType(setCode.SetType)
                ? null
                : setCode.SetPurchasePrice,
            MultiCodeRetailPrice = setCode.SetRetailPrice,
            IsAutoPricing = false,
            IsSpecialProduct = false,
            IsActive = setCode.IsActive,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = updatedBy,
            UpdatedBy = updatedBy,
        };

    internal static StoreMultiCodeProduct CreateMultiCodeRow(
        string productCode,
        string storeCode,
        string barcode,
        ProductSetCode matchedSetCode,
        MultiCodeItemDto item,
        DateTime now
    ) =>
        new()
        {
            UUID = UuidHelper.GenerateUuid7(),
            ProductCode = productCode,
            StoreCode = storeCode,
            MultiCodeProductCode = matchedSetCode.SetProductCode,
            StoreMultiCodeProductCode = storeCode + matchedSetCode.SetProductCode,
            MultiBarcode = barcode,
            MultiCodeRetailPrice = item.RetailPrice,
            // Type1/Type2 子项成本由统一服务按主成本派生，不能直接采纳客户端值。
            PurchasePrice = IsCostDerivedSetType(matchedSetCode.SetType)
                ? null
                : item.PurchasePrice,
            DiscountRate = item.DiscountRate,
            IsAutoPricing = item.AutoPricing,
            IsSpecialProduct = item.IsSpecialProduct,
            IsActive = item.IsActive,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

    internal static StoreRetailPrice CreateRequestedStorePrice(
        string productCode,
        StorePriceDto storePrice,
        DateTime now
    )
    {
        // 入口已用 ValidateStorePrices 拦截无效分店；这里再兜底，宁可抛错也不写 "0" 号分店。
        var storeCode =
            WarehouseProductSingleCreationValidator.ResolveStoreCode(storePrice)
            ?? throw new InvalidOperationException("分店价格缺少有效的分店代码");
        return new()
        {
            ProductCode = productCode,
            StoreCode = storeCode,
            StoreProductCode = storeCode + productCode,
            SupplierCode = "200",
            PurchasePrice = storePrice.PurchasePrice,
            StoreRetailPriceValue = storePrice.RetailPrice,
            DiscountRate = storePrice.DiscountRate,
            IsAutoPricing = storePrice.AutoPricing,
            IsSpecialProduct = storePrice.IsSpecialProduct,
            IsActive = storePrice.IsActive,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    internal static StoreRetailPrice CreateDefaultStorePrice(
        string productCode,
        string storeCode,
        decimal importPrice,
        decimal retailPrice,
        DateTime now
    ) =>
        new()
        {
            ProductCode = productCode,
            StoreCode = storeCode,
            StoreProductCode = storeCode + productCode,
            SupplierCode = "200",
            PurchasePrice = importPrice,
            StoreRetailPriceValue = retailPrice,
            DiscountRate = 0,
            IsAutoPricing = false,
            IsSpecialProduct = false,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

    /// <summary>规格去掉首尾空白，全空白视为未填写。</summary>
    private static string? NormalizeSpecification(string? specification) =>
        string.IsNullOrWhiteSpace(specification) ? null : specification.Trim();

    private static bool IsCostDerivedSetType(int setType) => setType is 1 or 2;
}
