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
            else if (!string.IsNullOrWhiteSpace(itemNumber))
            {
                result.ProductStatus = 2;
                summary.ProductNotExists++;
            }

            ApplyBarcodeStatus(result, barcode, data.BarcodeMatchCounts, summary);
            if (result.ProductStatus != 1)
                result.AutoPricing = detail.AutoPricing ?? true;

            ApplyAutoPricingPreview(
                result,
                detail,
                supplierStrategies,
                storeStrategies,
                globalStrategies
            );
            result.DefaultAction = SelectDefaultAction(result, detail, data.ProductCodesByBarcode);
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
        Dictionary<string, HashSet<string>> productCodesByBarcode)
    {
        var productExists = result.ProductStatus == 1;
        var barcodeNormal = result.BarcodeStatus == 1;
        var hasAdditionalBarcodes = LocalSupplierInvoicesBarcodeRules
            .DeserializeAdditionalBarcodes(detail.AdditionalBarcodesJson).Count > 0;
        if (productExists && hasAdditionalBarcodes)
        {
            return LocalSupplierInvoicesBarcodeRules.IsBarcodeOwnedByProduct(
                productCodesByBarcode,
                detail.Barcode,
                result.ProductInfo?.ProductCode)
                ? (int)DetailAction.AddMultiCode
                : (int)DetailAction.WaitForOperation;
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
