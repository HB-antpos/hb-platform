using System.Data;
using System.Linq;
using System.Text.Json;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BlazorApp.Api.Features.LocalSupplierInvoices
{
    internal sealed class LocalSupplierInvoicesProductExecutionStore
    {
        private readonly LocalSupplierInvoicesDependencies _dependencies;
        private SqlSugarContext _context => _dependencies.Context;
        private HqSqlSugarContext _hqContext => _dependencies.HqContext;
        private IMapper _mapper => _dependencies.Mapper;
        private ILogger _logger => _dependencies.Logger;
        private IAutoPricingService _autoPricingService => _dependencies.AutoPricingService;
        private IWarehouseProductChangeHistoryService _changeHistoryService => _dependencies.ChangeHistoryService;
        private ILocalSupplierInvoiceHqProductSyncService? _hqProductSyncService => _dependencies.HqProductSyncService;

        public LocalSupplierInvoicesProductExecutionStore(LocalSupplierInvoicesDependencies dependencies)
        {
            _dependencies = dependencies;
        }

        private static PastedDetailItemDto NormalizePastedDetailItem(PastedDetailItemDto item)
        {
            var normalizedBarcodes = NormalizePastedBarcodes(item.Barcode, item.AdditionalBarcodes);
            return new PastedDetailItemDto
            {
                // 关键位置：粘贴来源不可控，入库前统一收敛到明细表字段长度，避免单个脏单元格拖垮整批粘贴。
                ItemNumber = NormalizePastedItemNumber(item.ItemNumber),
                Barcode = normalizedBarcodes.PrimaryBarcode,
                AdditionalBarcodes = normalizedBarcodes.AdditionalBarcodes,
                ProductName = NormalizePastedTextField(item.ProductName, 200),
                Quantity = item.Quantity,
                PurchasePrice = item.PurchasePrice,
                NewAutoRetailPrice = item.NewAutoRetailPrice,
                RetailPrice = item.RetailPrice,
            };
        }

        private static string? NormalizePastedItemNumber(string? value)
        {
            var normalized = NormalizePastedTextField(value, 500);
            if (string.IsNullOrWhiteSpace(normalized))
                return null;

            return NormalizePastedTextField(normalized.TrimStart('\''), 50);
        }

        private static (string? PrimaryBarcode, List<string> AdditionalBarcodes) NormalizePastedBarcodes(
            string? primaryBarcode,
            IEnumerable<string>? additionalBarcodes
        )
        {
            var primaryCandidates = SplitPastedBarcodeCandidates(primaryBarcode).ToList();
            var normalizedPrimaryBarcode = primaryCandidates.FirstOrDefault();
            var secondaryCandidates = primaryCandidates
                .Skip(1)
                .Concat((additionalBarcodes ?? Enumerable.Empty<string>()).SelectMany(SplitPastedBarcodeCandidates));

            return (
                normalizedPrimaryBarcode,
                NormalizeAdditionalBarcodeValues(normalizedPrimaryBarcode, secondaryCandidates)
            );
        }

        private static IEnumerable<string> SplitPastedBarcodeCandidates(string? value)
        {
            var normalized = NormalizePastedBarcodeSource(value);
            if (string.IsNullOrWhiteSpace(normalized))
                yield break;

            foreach (var barcode in normalized
                .Split(new[] { ',', '，', ';', '；', '、' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Select(x => NormalizePastedTextField(x, 50))
                .Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                yield return barcode!;
            }
        }

        private static string? NormalizePastedBarcodeSource(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var normalized = value
                .Trim()
                .TrimStart('\'')
                .Replace("条码", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("barcode", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("bar code", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("ean", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("upc", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(":", string.Empty)
                .Replace("：", string.Empty);

            return string.Concat(normalized.Where(ch => !char.IsWhiteSpace(ch)));
        }

        private static List<string> NormalizeAdditionalBarcodeValues(
            string? primaryBarcode,
            IEnumerable<string>? values
        )
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(primaryBarcode))
                seen.Add(primaryBarcode.Trim());

            foreach (var barcode in values ?? Enumerable.Empty<string>())
            {
                var normalized = NormalizePastedTextField(barcode, 50);
                if (string.IsNullOrWhiteSpace(normalized))
                    continue;
                if (!seen.Add(normalized))
                    continue;
                result.Add(normalized);
            }

            return result;
        }

        private static string? SerializeAdditionalBarcodes(
            string? primaryBarcode,
            IEnumerable<string>? additionalBarcodes
        )
        {
            var normalizedPrimaryBarcode = SplitPastedBarcodeCandidates(primaryBarcode).FirstOrDefault();
            var normalizedAdditionalBarcodes = NormalizeAdditionalBarcodeValues(
                normalizedPrimaryBarcode,
                (additionalBarcodes ?? Enumerable.Empty<string>()).SelectMany(SplitPastedBarcodeCandidates)
            );

            return normalizedAdditionalBarcodes.Count > 0
                ? JsonSerializer.Serialize(normalizedAdditionalBarcodes)
                : null;
        }

        private static List<string> DeserializeAdditionalBarcodes(string? additionalBarcodesJson)
        {
            if (string.IsNullOrWhiteSpace(additionalBarcodesJson))
                return new List<string>();

            try
            {
                var values = JsonSerializer.Deserialize<List<string>>(additionalBarcodesJson);
                return NormalizeAdditionalBarcodeValues(null, values);
            }
            catch (JsonException)
            {
                return NormalizeAdditionalBarcodeValues(
                    null,
                    SplitPastedBarcodeCandidates(additionalBarcodesJson)
                );
            }
        }

        private static void PopulateAdditionalBarcodes(IEnumerable<LocalSupplierInvoiceItemDto> items)
        {
            foreach (var item in items)
            {
                item.AdditionalBarcodes = DeserializeAdditionalBarcodes(item.AdditionalBarcodesJson);
            }
        }

        private static List<string> GetDetailBarcodesForMultiCode(StoreLocalSupplierInvoiceDetails detail)
        {
            var additionalBarcodes = DeserializeAdditionalBarcodes(detail.AdditionalBarcodesJson);
            if (additionalBarcodes.Count > 0)
                return additionalBarcodes;

            // 关键位置：旧流程没有副条码字段，仍然使用明细 Barcode 新增一条多码，保持历史行为。
            return string.IsNullOrWhiteSpace(detail.Barcode)
                ? new List<string>()
                : NormalizeAdditionalBarcodeValues(null, new[] { detail.Barcode });
        }

        private static string? NormalizePastedTextField(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var normalized = string.Join(
                " ",
                value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            ).Trim();

            if (normalized.Length <= maxLength)
                return normalized;

            return normalized[..maxLength];
        }

        private static bool IsLikelyPastedHeaderItem(PastedDetailItemDto item)
        {
            var mappedCells = 0;
            var headerCells = 0;

            CountHeaderCell(item.ItemNumber, new[] { "itemno", "itemnumber", "item", "货号" }, ref mappedCells, ref headerCells);
            CountHeaderCell(item.Barcode, new[] { "barcode", "条码" }, ref mappedCells, ref headerCells);
            CountHeaderCell(item.ProductName, new[] { "description", "desc", "productname", "商品名称" }, ref mappedCells, ref headerCells);

            // 关键位置：兼容旧前端或接口直传，供应商表头不能落成一条假明细。
            return mappedCells > 0 && mappedCells == headerCells && headerCells >= 2;
        }

        private static void CountHeaderCell(
            string? value,
            IReadOnlyCollection<string> headers,
            ref int mappedCells,
            ref int headerCells
        )
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            mappedCells++;
            var normalized = NormalizePastedHeaderLabel(value);
            if (normalized != null && headers.Contains(normalized))
                headerCells++;
        }

        private static string? NormalizePastedHeaderLabel(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return string.Concat(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit));
        }


        internal async Task<BatchOperationResult> BatchCreateProductsAsync(
            List<StoreLocalSupplierInvoiceDetails> details,
            StoreLocalSupplierInvoice header,
            string userName,
            Dictionary<string, int> newProductProductTypeByDetailGuid
        )
        {
            var result = new BatchOperationResult();
            var db = _context.Db;
            var now = DateTime.UtcNow;

            // 获取所有激活分店
            var activeStores = await db.Queryable<Store>()
                .Where(s => s.IsActive == true)
                .Select(s => s.StoreCode)
                .ToListAsync();

            var productsToCreate = new List<Product>();
            var storePricesToCreate = new List<StoreRetailPrice>();
            var multiCodesToCreate = new List<StoreMultiCodeProduct>();
            var productSetCodesToCreate = new List<ProductSetCode>();
            var detailsToBackfill = new List<StoreLocalSupplierInvoiceDetails>();
            var pricingStrategyCache = new Dictionary<decimal, PricingStrategy?>();

            foreach (var detail in details)
            {
                // 验证必填字段
                if (string.IsNullOrWhiteSpace(detail.ItemNumber))
                {
                    result.Errors.Add($"创建商品失败：货号不能为空");
                    result.FailedCount++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(detail.Barcode))
                {
                    result.Errors.Add($"创建商品失败：条码不能为空");
                    result.FailedCount++;
                    continue;
                }

                if (detail.PurchasePrice == null || detail.PurchasePrice <= 0)
                {
                    result.Errors.Add($"创建商品失败：进货价必须大于0");
                    result.FailedCount++;
                    continue;
                }

                // 生成商品 UUID
                var productUUID = UuidHelper.GenerateUuid7();

                // 计算零售价：根据自动定价标志决定使用哪个零售价
                decimal calculatedRetailPrice = 0;
                if (detail.AutoPricing == true)
                {
                    // 自动定价开启时，使用新自动零售价
                    if (detail.NewAutoRetailPrice.HasValue && detail.NewAutoRetailPrice > 0)
                    {
                        calculatedRetailPrice = detail.NewAutoRetailPrice.Value;
                    }
                    else if (detail.PurchasePrice.HasValue && detail.PurchasePrice > 0)
                    {
                        // 同一批常有大量相同进货价，按价格缓存策略，避免创建商品时重复查策略。
                        if (!pricingStrategyCache.TryGetValue(detail.PurchasePrice.Value, out var strategy))
                        {
                            strategy = await _autoPricingService.FindStrategyForPriceAsync(
                                detail.PurchasePrice.Value,
                                header.SupplierCode,
                                null
                            );
                            pricingStrategyCache[detail.PurchasePrice.Value] = strategy;
                        }

                        calculatedRetailPrice = _autoPricingService.CalculateRetailPrice(
                            detail.PurchasePrice.Value,
                            strategy
                        );
                    }
                    else
                    {
                        calculatedRetailPrice = (detail.PurchasePrice ?? 0) * 2.5m; // 默认加价 250%
                    }
                }
                else
                {
                    // 自动定价关闭时，使用指定零售价
                    if (detail.RetailPrice.HasValue && detail.RetailPrice > 0)
                    {
                        calculatedRetailPrice = detail.RetailPrice.Value;
                    }
                    else
                    {
                        calculatedRetailPrice = (detail.PurchasePrice ?? 0) * 2.5m; // 默认加价 250%
                    }
                }

                var additionalBarcodes = DeserializeAdditionalBarcodes(detail.AdditionalBarcodesJson);
                var selectedProductType = 0;
                if (additionalBarcodes.Count > 0)
                {
                    // 关键位置：新商品带副码时，主档类型必须来自用户确认弹窗；默认由前端给 2=多码。
                    selectedProductType = newProductProductTypeByDetailGuid.TryGetValue(
                        detail.DetailGUID,
                        out var requestedProductType
                    )
                        ? requestedProductType
                        : 2;
                }

                var product = new Product
                {
                    UUID = productUUID,
                    ProductCode = productUUID, // ProductCode 使用 UUID
                    ItemNumber = detail.ItemNumber,
                    Barcode = detail.Barcode,
                    ProductName = detail.ProductName ?? string.Empty,
                    LocalSupplierCode = header.SupplierCode,
                    PurchasePrice = detail.PurchasePrice ?? 0,
                    RetailPrice = calculatedRetailPrice,
                    IsAutoPricing = detail.AutoPricing ?? true,
                    IsSpecialProduct = detail.IsSpecialProduct ?? false,
                    ProductImage = detail.ProductImage,
                    ProductType = selectedProductType,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedBy = userName,
                    UpdatedBy = userName,
                };
                productsToCreate.Add(product);

                // 为所有激活分店创建 StoreRetailPrice
                foreach (var storeCode in activeStores)
                {
                    var storePrice = new StoreRetailPrice
                    {
                        UUID = UuidHelper.GenerateUuid7(),
                        StoreCode = storeCode,
                        ProductCode = productUUID,
                        StoreProductCode = storeCode + productUUID,
                        SupplierCode = header.SupplierCode,
                        PurchasePrice = detail.PurchasePrice ?? 0,
                        StoreRetailPriceValue = calculatedRetailPrice,
                        IsAutoPricing = detail.AutoPricing ?? true,
                        IsSpecialProduct = detail.IsSpecialProduct ?? false,
                        DiscountRate = detail.DiscountRate,
                        IsActive = true,
                        IsDeleted = false,
                        CreatedAt = now,
                        UpdatedAt = now,
                        CreatedBy = userName,
                        UpdatedBy = userName,
                    };
                    storePricesToCreate.Add(storePrice);
                }

                foreach (var additionalBarcode in additionalBarcodes)
                {
                    AppendMultiCodeEntities(
                        detail,
                        productUUID,
                        additionalBarcode,
                        activeStores,
                        calculatedRetailPrice,
                        now,
                        userName,
                        productSetCodesToCreate,
                        multiCodesToCreate
                    );
                }

                // 关键位置：明细回填新主档编码，否则明细页关联不到商品、新品数一直不减；
                // 重新「商品检测」虽能回填，但会把已执行的 99 重置成待执行，不能依赖它。
                detail.ProductCode = productUUID;
                detail.StoreProductCode = string.IsNullOrWhiteSpace(header.StoreCode)
                    ? null
                    : header.StoreCode + productUUID;
                detail.ExistingProductCount = 1;
                detailsToBackfill.Add(detail);

                result.SuccessCount++;
                result.AddedMultiCodeCount += additionalBarcodes.Count;
                result.SuccessfulDetailGuids.Add(detail.DetailGUID);
                result.ChangedProductCodes.Add(productUUID);
            }

            if (productsToCreate.Count > 0)
            {
                await db.Fastest<Product>().BulkCopyAsync(productsToCreate);
            }

            if (storePricesToCreate.Count > 0)
            {
                await db.Fastest<StoreRetailPrice>().BulkCopyAsync(storePricesToCreate);
            }

            if (productSetCodesToCreate.Count > 0)
            {
                await db.Fastest<ProductSetCode>().BulkCopyAsync(productSetCodesToCreate);
            }

            if (multiCodesToCreate.Count > 0)
            {
                await db.Fastest<StoreMultiCodeProduct>().BulkCopyAsync(multiCodesToCreate);
            }

            if (detailsToBackfill.Count > 0)
            {
                // 与调用方随后写入的 ActivityType=99 处于同一事务（CommandWriter 持有），任一失败整笔回滚；
                // UpdatedAt/UpdatedBy 统一由 BatchUpdateDetailActivityTypeAsync 写入。
                await db.Updateable(detailsToBackfill)
                    .UpdateColumns(new[]
                    {
                        nameof(StoreLocalSupplierInvoiceDetails.ProductCode),
                        nameof(StoreLocalSupplierInvoiceDetails.StoreProductCode),
                        nameof(StoreLocalSupplierInvoiceDetails.ExistingProductCount),
                    })
                    .ExecuteCommandAsync();
            }

            return result;
        }

        internal async Task<BatchOperationResult> BatchUpdatePurchasePriceAsync(
            List<StoreLocalSupplierInvoiceDetails> details,
            string userName
        )
        {
            var result = new BatchOperationResult();
            var db = _context.Db;
            var now = DateTime.UtcNow;
            var validDetails = new List<StoreLocalSupplierInvoiceDetails>();

            foreach (var detail in details)
            {
                if (detail.PurchasePrice == null || detail.PurchasePrice <= 0)
                {
                    result.Errors.Add($"更新进货价跳过：{detail.DetailGUID} 新进货价为空或为0");
                    result.SkippedCount++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(detail.ProductCode))
                {
                    result.Errors.Add($"更新进货价失败：未找到商品编码");
                    result.FailedCount++;
                    continue;
                }

                validDetails.Add(detail);
            }

            if (validDetails.Count == 0)
            {
                return result;
            }

            var productCodes = validDetails
                .Select(x => x.ProductCode!)
                .Distinct()
                .ToList();
            var storeCodes = validDetails
                .Select(x => x.StoreCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();

            // 明细 ProductCode 与商品 ProductCode 保持一致，不能混用 UUID；先批量查出再批量更新。
            var productsByCode = (await db.Queryable<Product>()
                    .Where(p =>
                        p.ProductCode != null
                        && productCodes.Contains(p.ProductCode)
                        && p.IsDeleted == false
                    )
                    .ToListAsync())
                .Where(p => !string.IsNullOrWhiteSpace(p.ProductCode))
                .GroupBy(p => p.ProductCode!)
                .ToDictionary(g => g.Key, g => g.First());

            var storePricesByKey = (await db.Queryable<StoreRetailPrice>()
                    .Where(srp =>
                        srp.ProductCode != null
                        && srp.StoreCode != null
                        && productCodes.Contains(srp.ProductCode)
                        && storeCodes.Contains(srp.StoreCode)
                        && srp.IsDeleted == false
                    )
                    .ToListAsync())
                .GroupBy(srp => $"{srp.ProductCode}\u001f{srp.StoreCode}")
                .ToDictionary(g => g.Key, g => g.First());

            var productsToUpdate = new Dictionary<string, Product>();
            var storePricesToUpdate = new Dictionary<string, StoreRetailPrice>();
            foreach (var detail in validDetails)
            {
                var storePriceKey = $"{detail.ProductCode}\u001f{detail.StoreCode}";
                if (
                    !productsByCode.TryGetValue(detail.ProductCode!, out var product)
                    || !storePricesByKey.TryGetValue(storePriceKey, out var storePrice)
                )
                {
                    result.Errors.Add($"更新进货价失败：商品或分店价格未更新");
                    result.FailedCount++;
                    continue;
                }

                var purchasePrice = detail.PurchasePrice.GetValueOrDefault();
                product.PurchasePrice = purchasePrice;
                product.UpdatedAt = now;
                product.UpdatedBy = userName;
                storePrice.PurchasePrice = purchasePrice;
                storePrice.UpdatedAt = now;
                storePrice.UpdatedBy = userName;

                productsToUpdate[detail.ProductCode!] = product;
                storePricesToUpdate[storePriceKey] = storePrice;
                result.SuccessCount++;
                result.SuccessfulDetailGuids.Add(detail.DetailGUID);
                result.ChangedProductCodes.Add(detail.ProductCode!);
            }

            if (productsToUpdate.Count > 0)
            {
                await db.Updateable(productsToUpdate.Values.ToList()).ExecuteCommandAsync();
            }

            if (storePricesToUpdate.Count > 0)
            {
                await db.Updateable(storePricesToUpdate.Values.ToList()).ExecuteCommandAsync();
            }

            return result;
        }

        internal async Task<BatchOperationResult> BatchUpdateItemNumberAsync(
            List<StoreLocalSupplierInvoiceDetails> details,
            Dictionary<string, string> productItemNumbers,
            string userName
        )
        {
            var result = new BatchOperationResult();
            var db = _context.Db;
            var now = DateTime.UtcNow;
            var validDetails = new List<StoreLocalSupplierInvoiceDetails>();

            foreach (var detail in details)
            {
                if (string.IsNullOrWhiteSpace(detail.ItemNumber))
                {
                    result.Errors.Add($"更新货号失败：新货号不能为空");
                    result.FailedCount++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(detail.ProductCode))
                {
                    result.Errors.Add($"更新货号失败：未找到商品编码");
                    result.FailedCount++;
                    continue;
                }

                validDetails.Add(detail);
            }

            if (validDetails.Count == 0)
            {
                return result;
            }

            var productCodes = validDetails
                .Select(x => x.ProductCode!)
                .Distinct()
                .ToList();
            // 明细 ProductCode 与商品 ProductCode 保持一致，不能混用 UUID；商品一次查出后批量更新货号。
            var productsByCode = (await db.Queryable<Product>()
                    .Where(p =>
                        p.ProductCode != null
                        && productCodes.Contains(p.ProductCode)
                        && p.IsDeleted == false
                    )
                    .ToListAsync())
                .Where(p => !string.IsNullOrWhiteSpace(p.ProductCode))
                .GroupBy(p => p.ProductCode!)
                .ToDictionary(g => g.Key, g => g.First());

            var productsToUpdate = new Dictionary<string, Product>();
            foreach (var detail in validDetails)
            {
                if (!productsByCode.TryGetValue(detail.ProductCode!, out var product))
                {
                    result.Errors.Add($"更新货号失败：商品未更新");
                    result.FailedCount++;
                    continue;
                }

                product.ItemNumber = detail.ItemNumber;
                product.UpdatedAt = now;
                product.UpdatedBy = userName;
                productsToUpdate[detail.ProductCode!] = product;
                result.SuccessCount++;
                result.SuccessfulDetailGuids.Add(detail.DetailGUID);
                result.ChangedProductCodes.Add(detail.ProductCode!);
            }

            if (productsToUpdate.Count > 0)
            {
                await db.Updateable(productsToUpdate.Values.ToList()).ExecuteCommandAsync();

                // 按条码解析出商品的行原本没有商品编码，回写到明细，单据上能看出改的是哪个商品。
                // UpdatedAt/UpdatedBy 统一由 BatchUpdateDetailActivityTypeAsync 写入。
                var succeeded = result.SuccessfulDetailGuids.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var detailsToBackfill = validDetails.Where(detail => succeeded.Contains(detail.DetailGUID)).ToList();
                await db.Updateable(detailsToBackfill)
                    .UpdateColumns(new[] { nameof(StoreLocalSupplierInvoiceDetails.ProductCode) })
                    .ExecuteCommandAsync();
            }

            return result;
        }

        internal async Task<BatchOperationResult> BatchAddMultiCodesAsync(
            List<StoreLocalSupplierInvoiceDetails> details,
            StoreLocalSupplierInvoice header,
            string userName
        )
        {
            var result = new BatchOperationResult();
            var db = _context.Db;
            var now = DateTime.UtcNow;

            // 1. 获取所有有效分店
            var activeStores = await db.Queryable<Store>()
                .Where(s => s.IsActive == true)
                .Select(s => s.StoreCode)
                .ToListAsync();

            // 2. 收集需要修改商品类型的数据
            var productCodesToUpdate = details
                .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                .Select(x => x.ProductCode!)
                .Distinct()
                .ToList();

            if (productCodesToUpdate.Count > 0)
            {
                var products = await db.Queryable<Product>()
                    .Where(p =>
                        p.ProductCode != null
                        && productCodesToUpdate.Contains(p.ProductCode)
                        && p.IsDeleted == false
                    )
                    .Select(p => new { p.ProductCode, p.ProductType })
                    .ToListAsync();

                var uuidsToUpdate = products
                    .Where(p => p.ProductType != 2)
                    .Select(p => p.ProductCode)
                    .ToList();

                if (uuidsToUpdate.Count > 0)
                {
                    // 关键位置：一品多码主档统一标记为 2=多码，避免被误写成 1=套装。
                    await db.Updateable<Product>()
                        .SetColumns(p => p.ProductType == 2)
                        .SetColumns(p => p.UpdatedAt == now)
                        .SetColumns(p => p.UpdatedBy == userName)
                        .Where(p => uuidsToUpdate.Contains(p.ProductCode) && p.IsDeleted == false)
                        .ExecuteCommandAsync();
                }
            }

            // 3. 准备创建 StoreMultiCodeProduct 和 ProductSetCode
            var multiCodesToCreate = new List<StoreMultiCodeProduct>();
            var productSetCodesToCreate = new List<ProductSetCode>();

            // 关键位置：多码可能早已挂在同一商品上（之前执行过或其它入口加过），已有的关系与分店记录跳过，
            // 只补缺失的分店，并沿用已有关系的多码商品编码，保证总部与分店仍指向同一个多码商品。
            // 按大写比较，与校验口径（SqlFunc.ToUpper）一致，不依赖数据库排序规则是否区分大小写。
            var requestedBarcodes = details
                .SelectMany(GetDetailBarcodesForMultiCode)
                .Select(x => x.Trim().ToUpperInvariant())
                .Where(x => x.Length > 0)
                .Distinct()
                .ToList();
            var existingSetProductCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var existingStoreMultiCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (requestedBarcodes.Count > 0)
            {
                var existingSets = await db.Queryable<ProductSetCode>()
                    .Where(x => x.IsDeleted == false && x.SetType == 2 && x.SetBarcode != null && requestedBarcodes.Contains(SqlFunc.ToUpper(x.SetBarcode)))
                    .Select(x => new { x.ProductCode, x.SetBarcode, x.SetProductCode })
                    .ToListAsync();
                foreach (var set in existingSets)
                    existingSetProductCodes.TryAdd(BuildMultiCodeKey(set.ProductCode, set.SetBarcode), set.SetProductCode);

                var existingStores = await db.Queryable<StoreMultiCodeProduct>()
                    .Where(x => x.IsDeleted == false && x.MultiBarcode != null && requestedBarcodes.Contains(SqlFunc.ToUpper(x.MultiBarcode)))
                    .Select(x => new { x.StoreCode, x.ProductCode, x.MultiBarcode })
                    .ToListAsync();
                foreach (var store in existingStores)
                    existingStoreMultiCodes.Add(BuildStoreMultiCodeKey(store.StoreCode, store.ProductCode, store.MultiBarcode));
            }

            foreach (var detail in details)
            {
                if (string.IsNullOrWhiteSpace(detail.ProductCode))
                {
                    result.Errors.Add($"添加多码失败：未找到商品编码");
                    result.FailedCount++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(detail.Barcode))
                {
                    result.Errors.Add($"添加多码失败：条码不能为空");
                    result.FailedCount++;
                    continue;
                }
                var barcodesToAdd = GetDetailBarcodesForMultiCode(detail);
                if (barcodesToAdd.Count == 0)
                {
                    result.Errors.Add($"添加多码失败：条码不能为空");
                    result.FailedCount++;
                    continue;
                }

                foreach (var barcodeToAdd in barcodesToAdd)
                {
                    existingSetProductCodes.TryGetValue(
                        BuildMultiCodeKey(detail.ProductCode, barcodeToAdd),
                        out var existingSetProductCode
                    );
                    var missingStores = activeStores
                        .Where(storeCode => !existingStoreMultiCodes.Contains(
                            BuildStoreMultiCodeKey(storeCode, detail.ProductCode, barcodeToAdd)))
                        .ToList();
                    AppendMultiCodeEntities(
                        detail,
                        detail.ProductCode!,
                        barcodeToAdd,
                        missingStores,
                        detail.RetailPrice,
                        now,
                        userName,
                        productSetCodesToCreate,
                        multiCodesToCreate,
                        existingSetProductCode
                    );
                }

                result.SuccessCount += barcodesToAdd.Count;
                result.SuccessfulDetailGuids.Add(detail.DetailGUID);
                result.ChangedProductCodes.Add(detail.ProductCode!);
            }

            // 4. 批量插入 StoreMultiCodeProduct
            if (multiCodesToCreate.Count > 0)
            {
                await db.Fastest<StoreMultiCodeProduct>().BulkCopyAsync(multiCodesToCreate);
            }

            // 5. 批量插入 ProductSetCode
            if (productSetCodesToCreate.Count > 0)
            {
                await db.Fastest<ProductSetCode>().BulkCopyAsync(productSetCodesToCreate);
            }

            return result;
        }

        private static string BuildMultiCodeKey(string? productCode, string? barcode) =>
            $"{productCode?.Trim()}|{barcode?.Trim()}";

        private static string BuildStoreMultiCodeKey(string? storeCode, string? productCode, string? barcode) =>
            $"{storeCode?.Trim()}|{productCode?.Trim()}|{barcode?.Trim()}";

        private static void AppendMultiCodeEntities(
            StoreLocalSupplierInvoiceDetails detail,
            string productCode,
            string barcodeToAdd,
            IEnumerable<string> activeStores,
            decimal? retailPrice,
            DateTime now,
            string userName,
            List<ProductSetCode> productSetCodesToCreate,
            List<StoreMultiCodeProduct> multiCodesToCreate,
            string? existingSetProductCode = null
        )
        {
            // 已有同商品的一品多码关系时不再新建，分店补录沿用它的多码商品编码。
            var multiCodeProductCode = existingSetProductCode ?? UuidHelper.GenerateUuid7();
            if (existingSetProductCode == null)
            {
                // 关键位置：总部一品多码和分店一品多码使用同一个多码商品编码，后续 HQ 同步按它做幂等匹配。
                productSetCodesToCreate.Add(new ProductSetCode
                {
                    SetCodeId = UuidHelper.GenerateUuid7(),
                    ProductCode = productCode,
                    SetProductCode = multiCodeProductCode,
                    SetItemNumber = detail.ItemNumber ?? string.Empty,
                    SetBarcode = barcodeToAdd,
                    // Type2 的关系成本只能由父商品回算，创建时不能把进货单明细成本当作最终成本写入。
                    SetPurchasePrice = null,
                    SetRetailPrice = retailPrice,
                    SetQuantity = 1,
                    SetType = 2,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedBy = userName,
                    UpdatedBy = userName,
                });
            }

            foreach (var storeCode in activeStores.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
            {
                multiCodesToCreate.Add(new StoreMultiCodeProduct
                {
                    UUID = UuidHelper.GenerateUuid7(),
                    StoreCode = storeCode,
                    ProductCode = productCode,
                    MultiCodeProductCode = multiCodeProductCode,
                    StoreMultiCodeProductCode = storeCode + multiCodeProductCode,
                    MultiBarcode = barcodeToAdd,
                    // 与全局关系保持相同规则，门店投影等待同事务统一回算。
                    PurchasePrice = null,
                    MultiCodeRetailPrice = retailPrice,
                    DiscountRate = detail.DiscountRate,
                    IsAutoPricing = detail.AutoPricing ?? true,
                    IsSpecialProduct = detail.IsSpecialProduct ?? false,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedBy = userName,
                    UpdatedBy = userName,
                });
            }
        }

        internal async Task BatchUpdateDetailActivityTypeAsync(
            List<string> detailGuids,
            string userName
        )
        {
            var db = _context.Db;
            var now = DateTime.UtcNow;

            await db.Updateable<StoreLocalSupplierInvoiceDetails>()
                .SetColumns(x => x.ActivityType == 99)
                .SetColumns(x => x.UpdatedAt == now)
                .SetColumns(x => x.UpdatedBy == userName)
                .Where(x => detailGuids.Contains(x.DetailGUID))
                .ExecuteCommandAsync();
        }

        internal sealed class BatchOperationResult
        {
            public int SuccessCount { get; set; }
            public int AddedMultiCodeCount { get; set; }
            public int FailedCount { get; set; }
            public int SkippedCount { get; set; }
            public List<string> Errors { get; set; } = new();
            public List<string> SuccessfulDetailGuids { get; set; } = new();
            public HashSet<string> ChangedProductCodes { get; set; } = new(StringComparer.Ordinal);
        }

    }
}
