using System.Diagnostics;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.ProductPicker.Domain;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.ProductPicker.Infrastructure;

internal sealed class ProductPickerScanQueryStore(
    SqlSugarContext context,
    ProductPickerProductEnricher productEnricher,
    IProductPickerLocationLookup locationLookup
)
{
    private readonly ISqlSugarClient _db = context.Db;

    internal async Task<ProductPickerScanLookupData> LookupAsync(
        ProductPickerScanLookupInput input
    )
    {
        var barcode = input.Barcode;
        var lookupCodes = new[]
            {
                barcode,
                barcode.ToUpperInvariant(),
                barcode.ToLowerInvariant(),
            }
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var useSqlServerCaseInsensitiveCollation =
            _db.CurrentConnectionConfig.DbType == DbType.SqlServer;

        var exactQueryStopwatch = Stopwatch.StartNew();
        var allMatches = await QueryMatchesAsync(
            barcode,
            lookupCodes,
            "barcode",
            useSqlServerCaseInsensitiveCollation
        );
        var matchType = allMatches.Count > 0 ? "barcode" : null;

        if (allMatches.Count == 0)
        {
            // iOS 相机会把 0 开头的 EAN-13 去成 12 位；精确条码没有结果才回退前导 0 的另一种写法，
            // 先于货号 / 商品编码回退，避免纯数字条码被当成货号碰撞。
            var barcodeVariant = BarcodeVariants.GetLeadingZeroVariant(barcode);
            if (barcodeVariant != null)
            {
                allMatches = await QueryMatchesAsync(
                    barcodeVariant,
                    new List<string> { barcodeVariant },
                    "barcode",
                    useSqlServerCaseInsensitiveCollation
                );
                matchType = allMatches.Count > 0 ? "barcode" : null;
            }
        }

        if (allMatches.Count == 0)
        {
            allMatches = await QueryMatchesAsync(
                barcode,
                lookupCodes,
                "itemNumber",
                useSqlServerCaseInsensitiveCollation
            );
            matchType = allMatches.Count > 0 ? "fallback" : null;
        }

        if (allMatches.Count == 0)
        {
            allMatches = await QueryMatchesAsync(
                barcode,
                lookupCodes,
                "productCode",
                useSqlServerCaseInsensitiveCollation
            );
            matchType = allMatches.Count > 0 ? "fallback" : null;
        }

        if (allMatches.Count == 0 && locationLookup.IsEnabled)
        {
            // 只有条码、货号、商品编码均未命中时才允许解析货位。
            var locationResult = await locationLookup.LookupAsync(barcode);
            var locationProductCodes = locationResult?.ProductCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (locationResult != null)
            {
                matchType = locationResult.MatchType;
            }

            if (locationProductCodes.Count > 0)
            {
                allMatches = await QueryMatchesAsync(
                    barcode,
                    lookupCodes,
                    "productCodes",
                    useSqlServerCaseInsensitiveCollation,
                    locationProductCodes
                );
            }
        }
        exactQueryStopwatch.Stop();

        var buildStopwatch = Stopwatch.StartNew();
        // 等级一对多不能放大扫码候选；先按商品去重，再批量补等级。
        var distinctMatches = allMatches
            .GroupBy(product => product.ProductCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        await productEnricher.PopulateGradesAsync(
            _db,
            distinctMatches,
            Array.Empty<string>()
        );
        buildStopwatch.Stop();

        return new ProductPickerScanLookupData(
            barcode,
            matchType,
            distinctMatches,
            allMatches.Count,
            exactQueryStopwatch.ElapsedMilliseconds,
            buildStopwatch.ElapsedMilliseconds
        );
    }

    private async Task<List<StoreOrderProductDto>> QueryMatchesAsync(
        string barcode,
        List<string> lookupCodes,
        string matchField,
        bool useSqlServerCaseInsensitiveCollation,
        List<string>? productCodes = null
    )
    {
        var locationProductCodes = productCodes ?? new List<string>();
        var query = _db.Queryable<Product>()
            .InnerJoin<WarehouseProduct>(
                (product, warehouseProduct) =>
                    product.ProductCode == warehouseProduct.ProductCode
            )
            .LeftJoin<WarehouseCategory>(
                (product, warehouseProduct, category) =>
                    product.WarehouseCategoryGUID == category.CategoryGUID
            )
            .Where(
                (product, warehouseProduct, category) =>
                    product.IsActive
                    && !product.IsDeleted
                    && !warehouseProduct.IsDeleted
                    && warehouseProduct.IsActive
            );

        query = matchField switch
        {
            "barcode" => query
                .WhereIF(
                    useSqlServerCaseInsensitiveCollation,
                    (product, warehouseProduct, category) =>
                        product.Barcode != null && product.Barcode == barcode
                )
                .WhereIF(
                    !useSqlServerCaseInsensitiveCollation,
                    (product, warehouseProduct, category) =>
                        product.Barcode != null
                        && lookupCodes.Contains(product.Barcode)
                ),
            "itemNumber" => query
                .WhereIF(
                    useSqlServerCaseInsensitiveCollation,
                    (product, warehouseProduct, category) =>
                        product.ItemNumber != null && product.ItemNumber == barcode
                )
                .WhereIF(
                    !useSqlServerCaseInsensitiveCollation,
                    (product, warehouseProduct, category) =>
                        product.ItemNumber != null
                        && lookupCodes.Contains(product.ItemNumber)
                ),
            "productCode" => query
                .WhereIF(
                    useSqlServerCaseInsensitiveCollation,
                    (product, warehouseProduct, category) =>
                        product.ProductCode != null && product.ProductCode == barcode
                )
                .WhereIF(
                    !useSqlServerCaseInsensitiveCollation,
                    (product, warehouseProduct, category) =>
                        product.ProductCode != null
                        && lookupCodes.Contains(product.ProductCode)
                ),
            "productCodes" => query.Where(
                (product, warehouseProduct, category) =>
                    product.ProductCode != null
                    && locationProductCodes.Contains(product.ProductCode)
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(matchField),
                matchField,
                null
            ),
        };

        return await query
            .Select(
                (product, warehouseProduct, category) =>
                    new StoreOrderProductDto
                    {
                        ProductCode = product.ProductCode ?? string.Empty,
                        ItemNumber = product.ItemNumber,
                        Barcode = product.Barcode,
                        ProductName = product.ProductName,
                        ProductImage = product.ProductImage,
                        CategoryName = category.CategoryName,
                        WarehouseCategoryGUID = product.WarehouseCategoryGUID,
                        OEMPrice = warehouseProduct.OEMPrice,
                        MinOrderQuantity = warehouseProduct.MinOrderQuantity ?? 1,
                        StockQuantity = warehouseProduct.StockQuantity ?? 0,
                        PackQty = product.MiddlePackageQuantity,
                        ImportPrice = warehouseProduct.ImportPrice,
                    }
            )
            .ToListAsync();
    }
}
