using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;

namespace BlazorApp.Api.Features.LocalSupplierInvoices;

/// <summary>商品审核纯规则边界：输入预加载数据，输出 DTO 结果，不执行 SQL 或写入。</summary>
internal sealed class LocalSupplierInvoicesProductReviewEvaluator
{
    private readonly IAutoPricingService _autoPricingService;

    public LocalSupplierInvoicesProductReviewEvaluator(LocalSupplierInvoicesDependencies dependencies)
    {
        _autoPricingService = dependencies.AutoPricingService;
    }

    public async Task<LocalSupplierInvoicesProductReviewEvaluation> EvaluateAsync(
        LocalSupplierInvoicesProductReviewData data)
    {
        var allStrategies = await _autoPricingService.GetAllActiveStrategiesAsync();
        var supplierStrategies = allStrategies.Where(strategy => strategy.Targets?.Any(target =>
            target.TargetType == "Supplier" && target.TargetCode == data.Header.SupplierCode) ?? false).ToList();
        var storeStrategies = allStrategies.Where(strategy => strategy.Targets?.Any(target =>
            target.TargetType == "Store" && target.TargetCode == data.Header.StoreCode) ?? false).ToList();
        var globalStrategies = allStrategies.Where(strategy =>
            strategy.Level == "Global" || strategy.Targets == null || strategy.Targets.Count == 0).ToList();

        var results = new List<ProductCheckResultDto>(data.Details.Count);
        var summary = new CheckProductsSummaryDto { Total = data.Details.Count };
        foreach (var detail in data.Details)
        {
            var itemNumber = detail.ItemNumber?.Trim();
            var barcode = detail.Barcode?.Trim();
            var result = new ProductCheckResultDto
            {
                DetailGuid = detail.DetailGUID,
                ProductStatus = 0,
                BarcodeStatus = 0,
                ExistingProductCount = 0,
            };

            if (!string.IsNullOrWhiteSpace(itemNumber)
                && data.ProductsByItemNumber.TryGetValue(itemNumber, out var product))
            {
                ApplyProductMatch(result, detail, product, data.StorePricesByProductCode, summary);
            }
            else if (TryGetLinkedProduct(detail, barcode, data, out var linkedProduct))
            {
                // 货号对不上主档，但明细已关联的商品拥有本行条码（手动选用的已有商品），沿用该关联。
                ApplyProductMatch(result, detail, linkedProduct, data.StorePricesByProductCode, summary);
            }
            else if (TryGetSameItemNumberBarcodeOwner(itemNumber, barcode, data.BarcodeOwnerProducts, out var barcodeOwner))
            {
                // 本供应商下货号对不上，但条码只被一个存活商品占用且货号完全相同：就是同一个商品挂在别的供应商名下，
                // 直接按已有商品处理（回填商品编码，默认更新进货价），不再停在「等待操作」，也避免再新建出同货号同条码的重复商品。
                // 条码被多个商品占用或货号不同的情况信心不够，仍走下面的建议选用，由人确认。
                ApplyProductMatch(result, detail, barcodeOwner, data.StorePricesByProductCode, summary);
            }
            else if (!string.IsNullOrWhiteSpace(itemNumber))
            {
                result.ProductStatus = 2;
                summary.ProductNotExists++;
            }

            ApplyBarcodeStatus(result, barcode, data.BarcodeMatchCounts, summary);
            ApplySuggestedExistingProducts(result, itemNumber, barcode, data.BarcodeOwnerProducts);
            if (result.ProductStatus != 1)
                result.AutoPricing = detail.AutoPricing ?? true;

            ApplyAutoPricingPreview(
                result,
                detail,
                supplierStrategies,
                storeStrategies,
                globalStrategies
            );
            result.DefaultAction = SelectDefaultAction(result, detail, data.ProductCodesByBarcode, data.AdditionalBarcodeOwners);
            results.Add(result);
        }

        return new LocalSupplierInvoicesProductReviewEvaluation(results, summary);
    }

    /// <summary>
    /// 明细已有商品编码且该商品仍有效、并拥有本行条码（主条码或本店多码）时，视为人工关联的已有商品。
    /// 要求条码佐证，避免改了条码或商品被删后仍挂着过期编码。
    /// </summary>
    private static bool TryGetLinkedProduct(
        StoreLocalSupplierInvoiceDetails detail,
        string? barcode,
        LocalSupplierInvoicesProductReviewData data,
        out Product product)
    {
        product = null!;
        var productCode = detail.ProductCode?.Trim();
        if (string.IsNullOrWhiteSpace(productCode)
            || !data.LinkedProductsByCode.TryGetValue(productCode, out var linked)
            || !LocalSupplierInvoicesBarcodeRules.IsBarcodeOwnedByProduct(
                data.ProductCodesByBarcode, barcode, linked.ProductCode))
            return false;

        product = linked;
        return true;
    }

    /// <summary>
    /// 条码只被一个存活商品作为主条码占用，且该商品货号与明细货号完全相同（忽略大小写和首尾空格）时，视为同一商品。
    /// 要求「唯一占用者」：多个商品共用同一条码时无法判断是哪一个，交给人选。
    /// </summary>
    private static bool TryGetSameItemNumberBarcodeOwner(
        string? itemNumber,
        string? barcode,
        Dictionary<string, List<Product>> ownersByBarcode,
        out Product owner)
    {
        owner = null!;
        if (string.IsNullOrWhiteSpace(itemNumber)
            || string.IsNullOrWhiteSpace(barcode)
            || !ownersByBarcode.TryGetValue(barcode.Trim(), out var owners)
            || owners.Count != 1
            || !string.Equals(owners[0].ItemNumber?.Trim(), itemNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        owner = owners[0];
        return true;
    }

    private static void ApplyProductMatch(
        ProductCheckResultDto result,
        StoreLocalSupplierInvoiceDetails detail,
        Product product,
        Dictionary<string, StoreRetailPrice> storePricesByCode,
        CheckProductsSummaryDto summary)
    {
        result.ProductStatus = 1;
        result.ExistingProductCount = 1;
        summary.ProductExists++;
        result.ProductInfo = new ProductCheckInfoDto
        {
            ProductCode = product.ProductCode,
            ProductName = product.ProductName,
            ProductImage = product.ProductImage,
        };

        StoreRetailPrice? storePrice = null;
        if (!string.IsNullOrWhiteSpace(product.ProductCode)
            && storePricesByCode.TryGetValue(product.ProductCode, out storePrice))
        {
            result.ProductInfo.PurchasePrice = storePrice.PurchasePrice;
            result.ProductInfo.RetailPrice = storePrice.StoreRetailPriceValue;
            result.ProductInfo.StoreProductCode = storePrice.StoreProductCode;
            // 用户已手动设置的自动定价开关优先于分店默认值。
            result.AutoPricing = detail.AutoPricing ?? storePrice.IsAutoPricing;
            result.IsSpecialProduct = storePrice.IsSpecialProduct;
            result.DiscountRate = storePrice.DiscountRate;
        }

        var lastPurchasePrice = LocalSupplierInvoicesRules.IsPositiveValue(storePrice?.PurchasePrice)
            ? storePrice!.PurchasePrice
            : product.PurchasePrice;
        result.LastPurchasePrice = LocalSupplierInvoicesRules.IsPositiveValue(lastPurchasePrice)
            ? lastPurchasePrice
            : null;
    }

    private static void ApplyBarcodeStatus(
        ProductCheckResultDto result,
        string? barcode,
        Dictionary<string, int> barcodeMatchCounts,
        CheckProductsSummaryDto summary)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return;

        if (barcodeMatchCounts.TryGetValue(barcode, out var matchCount))
        {
            result.BarcodeMatchCount = matchCount;
            result.BarcodeStatus = result.ProductStatus == 1
                ? matchCount > 0 ? 1 : 2
                : matchCount == 0 ? 1 : 2;
        }
        else
        {
            result.BarcodeStatus = result.ProductStatus == 1 ? 2 : 1;
        }

        if (result.BarcodeStatus == 1)
            summary.BarcodeNormal++;
        else
            summary.BarcodeAbnormal++;
    }

    /// <summary>
    /// 本供应商下货号不存在、但条码已是其他存活商品的主条码时，给出「建议选用的已有商品」。
    /// 货号匹配按供应商限定是对的（不同供应商可以用相同货号表示不同商品），但条码在全局应该唯一；
    /// 条码已被占用时若仍新建，就会建出同货号同条码的重复商品。这里只提示，不改状态和默认操作，也不自动关联。
    /// </summary>
    private static void ApplySuggestedExistingProducts(
        ProductCheckResultDto result,
        string? itemNumber,
        string? barcode,
        Dictionary<string, List<Product>> ownersByBarcode)
    {
        if (result.ProductStatus != 2
            || string.IsNullOrWhiteSpace(barcode)
            || !ownersByBarcode.TryGetValue(barcode.Trim(), out var owners))
            return;

        result.SuggestedExistingProducts = owners
            .Select(product => new ProductCheckSuggestedProductDto
            {
                ProductCode = product.ProductCode,
                ItemNumber = product.ItemNumber,
                ProductName = product.ProductName,
                LocalSupplierCode = product.LocalSupplierCode,
                ProductImage = product.ProductImage,
                SameItemNumber = !string.IsNullOrWhiteSpace(itemNumber)
                    && string.Equals(product.ItemNumber?.Trim(), itemNumber.Trim(), StringComparison.OrdinalIgnoreCase),
            })
            .OrderByDescending(item => item.SameItemNumber)
            .ThenBy(item => item.ItemNumber, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }

    private void ApplyAutoPricingPreview(
        ProductCheckResultDto result,
        StoreLocalSupplierInvoiceDetails detail,
        List<PricingStrategy> supplierStrategies,
        List<PricingStrategy> storeStrategies,
        List<PricingStrategy> globalStrategies)
    {
        if ((result.AutoPricing ?? detail.AutoPricing) != true
            || !detail.PurchasePrice.HasValue
            || detail.PurchasePrice <= 0)
            return;

        var purchasePrice = detail.PurchasePrice.Value;
        var strategy = _autoPricingService.FindBestStrategyForPrice(
            purchasePrice,
            supplierStrategies,
            storeStrategies,
            globalStrategies
        );
        result.PricingFloatRate = _autoPricingService.CalculateRate(purchasePrice, strategy);
        result.NewAutoRetailPrice = _autoPricingService.CalculateRetailPrice(purchasePrice, strategy);
    }

    private static int SelectDefaultAction(
        ProductCheckResultDto result,
        StoreLocalSupplierInvoiceDetails detail,
        Dictionary<string, HashSet<string>> productCodesByBarcode,
        Dictionary<string, HashSet<string>> additionalBarcodeOwners)
    {
        var productExists = result.ProductStatus == 1;
        var barcodeNormal = result.BarcodeStatus == 1;
        var productCode = result.ProductInfo?.ProductCode;
        // 关键位置：副码都已是本行商品的多码时不算「待添加」，按普通已有商品处理（默认更新进货价）。
        var hasAdditionalBarcodes = LocalSupplierInvoicesBarcodeRules
            .DeserializeAdditionalBarcodes(detail.AdditionalBarcodesJson)
            .Any(barcode => !LocalSupplierInvoicesBarcodeRules.IsBarcodeOwnedByProduct(additionalBarcodeOwners, barcode, productCode));
        if (productExists && hasAdditionalBarcodes)
        {
            if (LocalSupplierInvoicesBarcodeRules.IsBarcodeOwnedByProduct(
                productCodesByBarcode,
                detail.Barcode,
                result.ProductInfo?.ProductCode))
                return (int)DetailAction.AddMultiCode;

            // 主条码不归本商品时与执行校验保持同一口径：已被别的商品占用（主条码或分店多码）才会被拒绝，需要人处理；
            // 没有任何商品占用（主档条码是货号占位、或主条码是新条码）时执行会把主条码一并登记为多码，直接给「添加多码」。
            var mainBarcode = detail.Barcode?.Trim();
            // 主条码为空时执行校验会拒绝（主条码不能为空），仍留给人补条码。
            var needsManualHandling = string.IsNullOrWhiteSpace(mainBarcode)
                || (productCodesByBarcode.TryGetValue(mainBarcode, out var occupiers) && occupiers.Count > 0);
            return needsManualHandling
                ? (int)DetailAction.WaitForOperation
                : (int)DetailAction.AddMultiCode;
        }

        if (!detail.PurchasePrice.HasValue || detail.PurchasePrice <= 0)
            return 0;
        if (!productExists && barcodeNormal)
            return (int)DetailAction.CreateProduct;
        if (productExists && !barcodeNormal)
            return (int)DetailAction.AddMultiCode;
        if (productExists && barcodeNormal)
            return (int)DetailAction.UpdatePurchasePrice;
        return (int)DetailAction.WaitForOperation;
    }
}

internal sealed record LocalSupplierInvoicesProductReviewEvaluation(
    List<ProductCheckResultDto> Results,
    CheckProductsSummaryDto Summary);
