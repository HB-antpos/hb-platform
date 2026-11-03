using System.Data;
using System.Linq;
using System.Text.Json;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.LocalSupplierInvoices.Details.BatchUpdate;
using BlazorApp.Api.Features.LocalSupplierInvoices.Details.BatchUpdate.Infrastructure;
using BlazorApp.Api.Features.LocalSupplierInvoices.Details.BatchUpsert;
using BlazorApp.Api.Features.LocalSupplierInvoices.Details.BatchUpsert.Infrastructure;
using BlazorApp.Api.Features.LocalSupplierInvoices.Details.Mutations.Infrastructure;
using BlazorApp.Api.Features.LocalSupplierInvoices.Details.Paste;
using BlazorApp.Api.Features.LocalSupplierInvoices.Details.Paste.Infrastructure;
using BlazorApp.Api.Services.Pricing;
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
    internal sealed class LocalSupplierInvoicesDetailsHandler
    {
        private readonly LocalSupplierInvoicesDependencies _dependencies;
        private SqlSugarContext _context => _dependencies.Context;
        private HqSqlSugarContext _hqContext => _dependencies.HqContext;
        private IMapper _mapper => _dependencies.Mapper;
        private ILogger _logger => _dependencies.Logger;
        private IAutoPricingService _autoPricingService => _dependencies.AutoPricingService;
        private IWarehouseProductChangeHistoryService _changeHistoryService => _dependencies.ChangeHistoryService;
        private ILocalSupplierInvoiceHqProductSyncService? _hqProductSyncService => _dependencies.HqProductSyncService;

        public LocalSupplierInvoicesDetailsHandler(LocalSupplierInvoicesDependencies dependencies)
        {
            _dependencies = dependencies;
        }

        public async Task<ApiResponse<BatchResultDto>> BatchUpsertDetailsAsync(
            string invoiceGuid,
            List<InvoiceDetailUpsertItemDto> items,
            string updatedBy
        )
        {
            try
            {
                var store = new LocalSupplierInvoiceBatchUpsertTransactionStore(_context.Db);
                var initialHeader = await store.LoadInitialHeaderAsync(invoiceGuid);
                if (initialHeader == null)
                    return ApiResponse<BatchResultDto>.Error("单据不存在", "NOT_FOUND");

                var plan = LocalSupplierInvoiceBatchUpsertPlan.Create(
                    invoiceGuid,
                    items,
                    updatedBy,
                    DateTime.UtcNow,
                    SerializeAdditionalBarcodes
                );
                var execution = await store.ExecuteAsync(initialHeader, plan);
                if (execution.Failure != null)
                {
                    return ApiResponse<BatchResultDto>.Error(
                        execution.Failure.Message,
                        execution.Failure.ErrorCode
                    );
                }

                return ApiResponse<BatchResultDto>.OK(
                    new BatchResultDto
                    {
                        Inserted = execution.Inserted,
                        Updated = execution.Updated,
                        Failed = 0,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量Upsert进货单明细失败");
                var message = ex.InnerException?.Message ?? ex.Message ?? "批量失败";
                return ApiResponse<BatchResultDto>.Error(message, "BATCH_UPSERT_ERROR");
            }
        }

        public async Task<ApiResponse<BatchResultDto>> BatchUpdateDetailsAsync(
            string invoiceGuid,
            BatchUpdateInvoiceDetailsRequest request,
            string updatedBy
        )
        {
            try
            {
                var validation = LocalSupplierInvoiceBatchUpdateValidator.ValidateRequest(request);
                if (validation.Failure != null)
                {
                    return ApiResponse<BatchResultDto>.Error(
                        validation.Failure.Message,
                        validation.Failure.ErrorCode
                    );
                }

                var plan = LocalSupplierInvoiceBatchUpdatePlan.Create(
                    invoiceGuid,
                    validation.DetailGuids,
                    validation.EditFields,
                    updatedBy,
                    DateTime.UtcNow
                );
                var store = new LocalSupplierInvoiceBatchUpdateTransactionStore(_context.Db);
                var initialState = await store.LoadInitialStateAsync(
                    invoiceGuid,
                    validation.DetailGuids
                );
                if (initialState.Details.Count == 0)
                    return ApiResponse<BatchResultDto>.Error("没有找到要更新的明细", "NOT_FOUND");

                var execution = await store.ExecuteAsync(
                    initialState,
                    plan,
                    ApplyAutoPricingPreviewAsync
                );
                if (execution.Failure != null)
                {
                    return ApiResponse<BatchResultDto>.Error(
                        execution.Failure.Message,
                        execution.Failure.ErrorCode
                    );
                }

                return ApiResponse<BatchResultDto>.OK(
                    new BatchResultDto
                    {
                        Inserted = 0,
                        Updated = execution.Updated,
                        Failed = execution.Failed,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量编辑进货单明细失败");
                return ApiResponse<BatchResultDto>.Error("批量更新失败", "BATCH_UPDATE_ERROR");
            }
        }

        private async Task ApplyAutoPricingPreviewAsync(
            StoreLocalSupplierInvoiceDetails detail,
            string? supplierCode,
            string? storeCode
        )
        {
            if (detail.AutoPricing != true || !detail.PurchasePrice.HasValue || detail.PurchasePrice <= 0)
                return;

            // 低于自动定价下限（0.10）的行：清空预览让保存成功，原因由检测结果与界面行内提示说明；
            // 不能让 CalculateRate 抛异常把整批编辑变成「批量更新失败」。
            if (!AutoPricingRules.SupportsAutoPricing(detail.PurchasePrice))
            {
                detail.PricingFloatRate = null;
                detail.NewAutoRetailPrice = null;
                return;
            }

            var strategy = await _autoPricingService.FindStrategyForPriceAsync(
                detail.PurchasePrice.Value,
                supplierCode ?? detail.SupplierCode,
                storeCode ?? detail.StoreCode
            );
            detail.PricingFloatRate = _autoPricingService.CalculateRate(
                detail.PurchasePrice.Value,
                strategy
            );
            detail.NewAutoRetailPrice = _autoPricingService.CalculateRetailPrice(
                detail.PurchasePrice.Value,
                strategy
            );
        }


        public async Task<ApiResponse<BatchResultDto>> PasteDetailsAsync(
            PasteDetailsRequest dto,
            string updatedBy
        )
        {
            try
            {
                var store = new LocalSupplierInvoicePasteTransactionStore(_context.Db);
                var initialHeader = await store.LoadInitialHeaderAsync(dto.InvoiceGuid);
                if (initialHeader == null)
                    return ApiResponse<BatchResultDto>.Error("订单不存在", "NOT_FOUND");

                var plan = LocalSupplierInvoicePastePlan.Create(
                    dto,
                    updatedBy,
                    DateTime.UtcNow,
                    IsLikelyPastedHeaderItem,
                    NormalizePastedDetailItem,
                    SerializeAdditionalBarcodes
                );
                var execution = await store.ExecuteAsync(initialHeader, plan);
                if (execution.Failure != null)
                {
                    return ApiResponse<BatchResultDto>.Error(
                        execution.Failure.Message,
                        execution.Failure.ErrorCode
                    );
                }

                return ApiResponse<BatchResultDto>.OK(
                    new BatchResultDto
                    {
                        Inserted = execution.Inserted,
                        Updated = 0,
                        Failed = 0,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "粘贴数据失败");
                return ApiResponse<BatchResultDto>.Error($"粘贴失败：{ex.Message}", "PASTE_ERROR");
            }
        }

        public async Task<ApiResponse<bool>> UpdateDetailActionAsync(
            string invoiceGuid,
            string detailGuid,
            int action
        )
        {
            try
            {
                if (!LocalSupplierInvoicesRules.IsClientSelectableDetailAction(action))
                    return ApiResponse<bool>.Error("操作类型无效", "VALIDATION_ERROR");

                var store = new LocalSupplierInvoiceDetailsMutationTransactionStore(
                    _context.Db
                );
                var execution = await store.ExecuteUpdateActionAsync(
                    invoiceGuid,
                    detailGuid,
                    action,
                    DateTime.UtcNow
                );
                if (execution.Failure != null)
                {
                    return ApiResponse<bool>.Error(
                        execution.Failure.Message,
                        execution.Failure.ErrorCode
                    );
                }

                return ApiResponse<bool>.OK(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新明细操作类型失败");
                return ApiResponse<bool>.Error("更新失败", "UPDATE_ERROR");
            }
        }

        public async Task<ApiResponse<BatchResultDto>> BatchUpdateDetailActionAsync(
            string invoiceGuid,
            BatchUpdateDetailActionRequest dto
        )
        {
            try
            {
                if (!LocalSupplierInvoicesRules.IsClientSelectableDetailAction(dto.Action))
                    return ApiResponse<BatchResultDto>.Error("操作类型无效", "VALIDATION_ERROR");

                if (dto.DetailGuids == null || dto.DetailGuids.Count == 0)
                {
                    return ApiResponse<BatchResultDto>.Error("未选择任何明细", "VALIDATION_ERROR");
                }

                var store = new LocalSupplierInvoiceDetailsMutationTransactionStore(
                    _context.Db
                );
                var execution = await store.ExecuteBatchUpdateActionAsync(
                    invoiceGuid,
                    dto.DetailGuids,
                    dto.Action,
                    DateTime.UtcNow
                );
                if (execution.Failure != null)
                {
                    return ApiResponse<BatchResultDto>.Error(
                        execution.Failure.Message,
                        execution.Failure.ErrorCode
                    );
                }

                return ApiResponse<BatchResultDto>.OK(
                    new BatchResultDto
                    {
                        Updated = execution.Affected,
                        Inserted = 0,
                        Failed = execution.Failed,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新操作类型失败");
                return ApiResponse<BatchResultDto>.Error("批量更新失败", "BATCH_UPDATE_ERROR");
            }
        }

        public async Task<ApiResponse<bool>> DeleteDetailsAsync(
            string invoiceGuid,
            List<string> detailGuids,
            string updatedBy
        )
        {
            try
            {
                if (detailGuids == null || detailGuids.Count == 0)
                    return ApiResponse<bool>.Error("未选择任何明细", "VALIDATION_ERROR");

                var store = new LocalSupplierInvoiceDetailsMutationTransactionStore(
                    _context.Db
                );
                var execution = await store.ExecuteDeleteAsync(
                    invoiceGuid,
                    detailGuids,
                    updatedBy,
                    DateTime.UtcNow
                );
                if (execution.Failure != null)
                {
                    return ApiResponse<bool>.Error(
                        execution.Failure.Message,
                        execution.Failure.ErrorCode
                    );
                }

                return ApiResponse<bool>.OK(
                    true,
                    $"成功删除 {execution.Affected} 条明细"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除明细失败");
                return ApiResponse<bool>.Error("删除失败", "DELETE_ERROR");
            }
        }

        public async Task<
            ApiResponse<GetBarcodeAbnormalDetailsResponse>
        > GetBarcodeAbnormalDetailsAsync(string invoiceGuid)
        {
            try
            {
                var db = _context.Db;

                var header = await db.Queryable<StoreLocalSupplierInvoice>()
                    .Where(x => x.InvoiceGUID == invoiceGuid && x.IsDeleted == false)
                    .FirstAsync();

                if (header == null)
                    return ApiResponse<GetBarcodeAbnormalDetailsResponse>.Error(
                        "订单不存在",
                        "NOT_FOUND"
                    );

                var details = await db.Queryable<StoreLocalSupplierInvoiceDetails>()
                    .Where(x => x.InvoiceGUID == invoiceGuid && x.IsDeleted == false)
                    .ToListAsync();

                var barcodes = details
                    .Select(x => x.Barcode?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .Distinct()
                    .ToList();

                var itemNumbers = details
                    .Select(x => x.ItemNumber?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .Distinct()
                    .ToList();

                var productByItemNumber = new Dictionary<string, Product>();
                if (itemNumbers.Count > 0)
                {
                    var products = await LocalSupplierInvoicesQueryHelper.QueryInChunksAsync<
                        Product,
                        string
                    >(
                        itemNumbers,
                        1000,
                        async chunk =>
                            await db.Queryable<Product>()
                                .Where(p =>
                                    p.LocalSupplierCode == header.SupplierCode
                                    && p.ItemNumber != null
                                    && chunk.Contains(p.ItemNumber)
                                    && p.IsDeleted == false
                                )
                                .ToListAsync()
                    );
                    foreach (var p in products)
                    {
                        if (!string.IsNullOrWhiteSpace(p.ItemNumber))
                            productByItemNumber[p.ItemNumber] = p;
                    }
                }

                var productByBarcode = new Dictionary<string, List<string>>();
                if (barcodes.Count > 0)
                {
                    var prods = await LocalSupplierInvoicesQueryHelper.QueryInChunksAsync<
                        Product,
                        string
                    >(
                        barcodes,
                        1000,
                        async chunk =>
                            await db.Queryable<Product>()
                                .Where(p =>
                                    p.IsDeleted == false
                                    && p.Barcode != null
                                    && chunk.Contains(p.Barcode)
                                )
                                .ToListAsync()
                    );
                    foreach (var p in prods)
                    {
                        if (!string.IsNullOrWhiteSpace(p.Barcode))
                        {
                            if (!productByBarcode.ContainsKey(p.Barcode))
                                productByBarcode[p.Barcode] = new List<string>();
                            if (!string.IsNullOrWhiteSpace(p.ProductCode))
                            {
                                productByBarcode[p.Barcode].Add(p.ProductCode);
                            }
                        }
                    }

                    var multiCodes = await LocalSupplierInvoicesQueryHelper.QueryInChunksAsync<
                        StoreMultiCodeProduct,
                        string
                    >(
                        barcodes,
                        1000,
                        async chunk =>
                            await db.Queryable<StoreMultiCodeProduct>()
                                .Where(x =>
                                    x.StoreCode == header.StoreCode
                                    && x.MultiBarcode != null
                                    && chunk.Contains(x.MultiBarcode)
                                    && x.IsDeleted == false
                                )
                                .ToListAsync()
                    );
                    foreach (var mc in multiCodes)
                    {
                        if (!string.IsNullOrWhiteSpace(mc.MultiBarcode))
                        {
                            if (!productByBarcode.ContainsKey(mc.MultiBarcode))
                                productByBarcode[mc.MultiBarcode] = new List<string>();
                            if (!string.IsNullOrWhiteSpace(mc.ProductCode))
                            {
                                productByBarcode[mc.MultiBarcode].Add(mc.ProductCode);
                            }
                        }
                    }
                }

                var productCodes = new HashSet<string>();
                foreach (var codes in productByBarcode.Values)
                {
                    foreach (var code in codes)
                        productCodes.Add(code);
                }

                var productDetails = new Dictionary<string, Product>();
                if (productCodes.Count > 0)
                {
                    var prods = await LocalSupplierInvoicesQueryHelper.QueryInChunksAsync<
                        Product,
                        string
                    >(
                        productCodes.ToList(),
                        1000,
                        async chunk =>
                            await db.Queryable<Product>()
                                .Where(p =>
                                    p.ProductCode != null
                                    && chunk.Contains(p.ProductCode)
                                    && p.IsDeleted == false
                                )
                                .ToListAsync()
                    );
                    foreach (var p in prods)
                    {
                        if (!string.IsNullOrWhiteSpace(p.ProductCode))
                        {
                            productDetails[p.ProductCode] = p;
                        }
                    }
                }

                var supplierCodes = productDetails
                    .Values.Select(p => p.LocalSupplierCode)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct()
                    .ToList();

                var suppliers = new Dictionary<string, string>();
                if (supplierCodes.Count > 0)
                {
                    var supplierList = await db.Queryable<HBLocalSupplier>()
                        .Where(x =>
                            supplierCodes.Contains(x.LocalSupplierCode) && x.IsDeleted == false
                        )
                        .Select(x => new { x.LocalSupplierCode, x.Name })
                        .ToListAsync();
                    foreach (var s in supplierList)
                        suppliers[s.LocalSupplierCode] = s.Name;
                }

                var result = new GetBarcodeAbnormalDetailsResponse();
                var abnormalDetails = new List<BarcodeAbnormalDetailDto>();

                foreach (var detail in details)
                {
                    var itemNumber = detail.ItemNumber?.Trim();
                    var barcode = detail.Barcode?.Trim();

                    string? matchedProductCode = null;
                    if (
                        !string.IsNullOrWhiteSpace(itemNumber)
                        && productByItemNumber.TryGetValue(itemNumber, out var product)
                    )
                    {
                        matchedProductCode = product.ProductCode;
                    }

                    if (string.IsNullOrWhiteSpace(barcode))
                        continue;

                    if (!productByBarcode.TryGetValue(barcode, out var matchedCodes))
                        continue;

                    var productStatus =
                        !string.IsNullOrWhiteSpace(itemNumber)
                        && productByItemNumber.ContainsKey(itemNumber)
                            ? 1
                            : 2;

                    bool isAbnormal = false;
                    if (productStatus == 1 && !string.IsNullOrWhiteSpace(matchedProductCode))
                    {
                        isAbnormal = !matchedCodes.Contains(matchedProductCode);
                    }

                    if (!isAbnormal)
                        continue;

                    var detailDto = new BarcodeAbnormalDetailDto
                    {
                        DetailGuid = detail.DetailGUID,
                        ItemNumber = detail.ItemNumber ?? string.Empty,
                        Barcode = detail.Barcode ?? string.Empty,
                        ProductName = detail.ProductName ?? string.Empty,
                        ProductStatus = productStatus,
                        MatchedProductCode = matchedProductCode,
                    };

                    foreach (var code in matchedCodes)
                    {
                        if (productDetails.TryGetValue(code, out var matchedProduct))
                        {
                            detailDto.MatchedProducts.Add(
                                new BarcodeAbnormalMatchedProductDto
                                {
                                    ProductCode = matchedProduct.ProductCode ?? string.Empty,
                                    ProductName = matchedProduct.ProductName ?? string.Empty,
                                    SupplierCode = matchedProduct.LocalSupplierCode ?? string.Empty,
                                    SupplierName = suppliers.GetValueOrDefault(
                                        matchedProduct.LocalSupplierCode ?? string.Empty
                                    ),
                                    ItemNumber = matchedProduct.ItemNumber,
                                    Barcode = matchedProduct.Barcode ?? string.Empty,
                                    ProductImage = matchedProduct.ProductImage,
                                    IsMultiCode = false,
                                    IsBundle = false,
                                }
                            );
                        }
                    }

                    abnormalDetails.Add(detailDto);
                }

                result.Details = abnormalDetails;
                return ApiResponse<GetBarcodeAbnormalDetailsResponse>.OK(result, "获取成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取条码异常明细失败");
                return ApiResponse<GetBarcodeAbnormalDetailsResponse>.Error(
                    "获取失败",
                    "GET_ERROR"
                );
            }
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

    }
}
