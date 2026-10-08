using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class StoreProductMaintenanceReactService : IStoreProductMaintenanceReactService
    {
        // 仅显式标记的业务错误允许返回客户端；数据库和事务异常只记录服务端日志。
        private sealed class StoreProductMaintenanceBusinessException : InvalidOperationException
        {
            public StoreProductMaintenanceBusinessException(string message)
                : base(message) { }
        }

        public const string SetCodeSnapshotConflictErrorCode = "SET_CODE_SNAPSHOT_CONFLICT";
        private readonly ISqlSugarClient _db;
        private readonly ILogger<StoreProductMaintenanceReactService> _logger;
        private readonly IAutoPricingService _autoPricingService;
        private readonly IMemoryCache _cache;
        private readonly IWarehouseProductChangeHistoryService _changeHistoryService;
        private readonly ICurrentUserService _currentUserService;
        private readonly IProductMaintenanceHqProjectionWriter _hqProjectionWriter;
        private const string PricingStrategiesCacheKey = "StoreProductMaintenance:PricingStrategies:Active";
        private static readonly TimeSpan PricingStrategiesCacheDuration = TimeSpan.FromSeconds(60);
        // Unicode 空格分隔符（普通空格之外）；只统一空格种类，不删除或合并字符。
        private const string ItemNumberSpaceVariants = "\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u202f\u205f\u3000";

        public StoreProductMaintenanceReactService(
            SqlSugarContext context,
            ILogger<StoreProductMaintenanceReactService> logger,
            IAutoPricingService autoPricingService,
            IMemoryCache cache,
            IWarehouseProductChangeHistoryService changeHistoryService,
            ICurrentUserService currentUserService,
            IProductMaintenanceHqProjectionWriter hqProjectionWriter
        )
        {
            _db = context.Db;
            _logger = logger;
            _autoPricingService = autoPricingService;
            _cache = cache;
            _changeHistoryService = changeHistoryService;
            _currentUserService = currentUserService;
            _hqProjectionWriter = hqProjectionWriter;
        }

        public async Task<ApiResponse<List<StoreProductLookupItemDto>>> LookupAsync(
            StoreProductLookupRequestDto request,
            List<string>? accessibleStoreCodes,
            CancellationToken cancellationToken = default
        )
        {
            var totalSw = Stopwatch.StartNew();
            try
            {
                var keyword = request.Keyword?.Trim();
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    _logger.LogDebug("StoreProductMaintenance lookup skipped because keyword is empty");
                    return ApiResponse<List<StoreProductLookupItemDto>>.Error("查询内容不能为空");
                }

                var selectedStoreCodes = ResolveScopedStoreCodes(request.StoreCode, accessibleStoreCodes);
                _logger.LogDebug(
                    "StoreProductMaintenance lookup started keyword={Keyword} requestedStore={RequestedStore} scope={Scope}",
                    keyword,
                    request.StoreCode,
                    FormatStoreScope(selectedStoreCodes)
                );
                if (selectedStoreCodes != null && selectedStoreCodes.Count == 0)
                {
                    _logger.LogDebug(
                        "StoreProductMaintenance lookup returned empty because store scope is NONE keyword={Keyword}",
                        keyword
                    );
                    return ApiResponse<List<StoreProductLookupItemDto>>.OK(new List<StoreProductLookupItemDto>());
                }

                var matchSw = Stopwatch.StartNew();
                var hits = (await QueryLookupHitsAsync(keyword, selectedStoreCodes))
                    // SQL Server 的排序规则可能忽略大小写；保留原快照字典的精确编码匹配语义。
                    .Where(item => !string.IsNullOrWhiteSpace(item.ProductCode)
                        && string.Equals(item.ProductCode, item.MatchedProductCode, StringComparison.Ordinal))
                    .ToList();
                matchSw.Stop();

                var productMatches = hits.Count(item =>
                    item.MatchSource is "ProductBarcode" or "ItemNumber"
                );
                var setMatches = hits.Count(item => item.MatchSource == "SetBarcode");
                var clearanceMatches = hits.Count(item => item.MatchSource == "ClearanceBarcode");

                var normalized = hits
                    .Select(item => new StoreProductLookupItemDto
                    {
                        ProductCode = item.ProductCode,
                        ProductName = item.ProductName,
                        ItemNumber = item.ItemNumber,
                        Barcode = item.Barcode,
                        ProductImage = item.ProductImage,
                        Grade = item.Grade,
                        ProductTypeLabel = NormalizeProductTypeLabel(item.ProductType?.ToString()),
                        MatchSource = item.MatchSource,
                        MatchValue = item.MatchValue,
                    })
                    .GroupBy(item => $"{item.ProductCode}|{item.MatchSource}|{item.Barcode}|{item.ItemNumber}")
                    .Select(group =>
                    {
                        return group.First();
                    })
                    .OrderBy(item => item.ProductName)
                    .ToList();

                _logger.LogInformation(
                    "StoreProductMaintenance lookup timing keyword={Keyword} requestedStore={RequestedStore} scope={Scope} lookup_match_ms={LookupMatchMs} product_hits={ProductHits} set_hits={SetHits} clearance_hits={ClearanceHits} raw_hit_count={RawHitCount} deduped_result_count={DedupedResultCount} total_ms={TotalMs}",
                    keyword,
                    request.StoreCode,
                    FormatStoreScope(selectedStoreCodes),
                    matchSw.ElapsedMilliseconds,
                    productMatches,
                    setMatches,
                    clearanceMatches,
                    hits.Count,
                    normalized.Count,
                    totalSw.ElapsedMilliseconds
                );

                return ApiResponse<List<StoreProductLookupItemDto>>.OK(normalized);
            }
            catch (Exception ex) when (ClientAbortDetector.IsClientAbort(ex, cancellationToken))
            {
                // 调用方已取消（请求被客户端中止）：直接上抛，不记错误；全局过滤器按 499 处理。
                // 认证阶段已把请求令牌留在 SqlSugar ADO 上，本方法内的查询都会随它取消（OCE 或用户取消的 SqlException）。
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "商品查询失败: {Keyword}", request.Keyword);
                return ApiResponse<List<StoreProductLookupItemDto>>.Error($"商品查询失败: {ex.Message}");
            }
        }

        public async Task<ApiResponse<StoreProductScanLabelResultDto>> ScanLabelAsync(
            StoreProductLookupRequestDto request,
            List<string>? accessibleStoreCodes,
            CancellationToken cancellationToken = default
        )
        {
            var totalSw = Stopwatch.StartNew();
            var keyword = request.Keyword?.Trim();
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return ApiResponse<StoreProductScanLabelResultDto>.Error("查询内容不能为空");
            }

            try
            {
                // 复用既有精确候选查询和授权门店范围；本入口只读，不补建价格、不写仓库价。
                var candidatesResult = await LookupAsync(
                    new StoreProductLookupRequestDto { Keyword = keyword, StoreCode = request.StoreCode },
                    accessibleStoreCodes,
                    cancellationToken
                );
                var candidates = candidatesResult.Data ?? new List<StoreProductLookupItemDto>();
                if (!candidatesResult.Success)
                {
                    return ApiResponse<StoreProductScanLabelResultDto>.Error(candidatesResult.Message);
                }

                var result = new StoreProductScanLabelResultDto { Candidates = candidates };
                var productCodes = candidates
                    .Select(x => x.ProductCode)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (productCodes.Count == 1)
                {
                    var productCode = productCodes[0];
                    var detailResult = await GetFastDetailAsync(
                        productCode,
                        request.StoreCode,
                        accessibleStoreCodes,
                        cancellationToken
                    );
                    if (detailResult.Success && detailResult.Data != null)
                    {
                        result.Detail = detailResult.Data;
                        var matchingCandidates = candidates.Where(x =>
                            string.Equals(x.ProductCode, productCode, StringComparison.Ordinal));
                        // 同一商品存在多个不同命中来源时不猜测打印目标，避免条码冲突导致错价签。
                        if (matchingCandidates.Count() == 1)
                        {
                            var matchedCandidate = matchingCandidates.Single();
                            result.PrintTarget = await BuildScanLabelPrintTargetAsync(
                                keyword,
                                matchedCandidate.MatchSource,
                                result.Detail,
                                request.StoreCode,
                                accessibleStoreCodes
                            );
                        }
                    }
                }

                _logger.LogInformation(
                    "StoreProductMaintenance scan-label completed requestedStore={RequestedStore} candidate_count={CandidateCount} unique_product_count={UniqueProductCount} printable={Printable} total_ms={TotalMs}",
                    request.StoreCode,
                    candidates.Count,
                    productCodes.Count,
                    result.PrintTarget != null,
                    totalSw.ElapsedMilliseconds
                );
                return ApiResponse<StoreProductScanLabelResultDto>.OK(result);
            }
            catch (Exception ex) when (ClientAbortDetector.IsClientAbort(ex, cancellationToken))
            {
                // 调用方已取消（请求被客户端中止）：直接上抛，不记错误；全局过滤器按 499 处理。
                // 认证阶段已把请求令牌留在 SqlSugar ADO 上，本方法内的查询都会随它取消（OCE 或用户取消的 SqlException）。
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "扫码价签查询失败");
                return ApiResponse<StoreProductScanLabelResultDto>.Error("扫码价签查询失败，请稍后重试");
            }
        }

        private async Task<StoreProductPrintTargetDto?> BuildScanLabelPrintTargetAsync(
            string keyword,
            string? matchSource,
            StoreProductDetailDto detail,
            string? requestedStoreCode,
            List<string>? accessibleStoreCodes
        )
        {
            var storeCode = detail.StorePrice?.StoreCode
                ?? detail.ClearancePrice?.StoreCode
                ?? ResolveFastDetailTargetStoreCode(
                    requestedStoreCode,
                    ResolveScopedStoreCodes(requestedStoreCode, accessibleStoreCodes)
                );

            if (string.Equals(matchSource, "ClearanceBarcode", StringComparison.Ordinal))
            {
                var clearance = detail.ClearancePrice;
                return clearance?.ClearancePrice is > 0
                    && !string.IsNullOrWhiteSpace(clearance.ClearanceBarcode)
                    ? new StoreProductPrintTargetDto
                    {
                        Kind = "clearance",
                        Barcode = clearance.ClearanceBarcode!,
                        RetailPrice = clearance.ClearancePrice.Value,
                        ProductCode = detail.ProductCode,
                        CodeId = clearance.Uuid,
                        StoreCode = clearance.StoreCode,
                    }
                    : null;
            }

            if (string.Equals(matchSource, "ProductBarcode", StringComparison.Ordinal)
                || string.Equals(matchSource, "ItemNumber", StringComparison.Ordinal))
            {
                var price = detail.StorePrice;
                return price?.RetailPrice is > 0
                    && !string.IsNullOrWhiteSpace(detail.Barcode)
                    ? new StoreProductPrintTargetDto
                    {
                        Kind = "product",
                        Barcode = detail.Barcode!,
                        RetailPrice = price.RetailPrice.Value,
                        DiscountRate = price.DiscountRate,
                        ProductCode = detail.ProductCode,
                        CodeId = price.Uuid,
                        StoreCode = price.StoreCode ?? storeCode,
                    }
                    : null;
            }

            if (!string.Equals(matchSource, "SetBarcode", StringComparison.Ordinal))
            {
                return null;
            }

            var setRows = await _db.Queryable<ProductSetCode>()
                .Where(x => !x.IsDeleted && x.ProductCode == detail.ProductCode && x.SetBarcode == keyword)
                .Select(x => new
                {
                    x.SetCodeId,
                    x.SetProductCode,
                    x.SetBarcode,
                    x.SetRetailPrice,
                    x.SetPurchasePrice,
                    x.IsActive,
                })
                .ToListAsync();
            var set = setRows.Count == 1 ? setRows[0] : null;
            if (set == null || !set.IsActive || string.IsNullOrWhiteSpace(set.SetBarcode))
            {
                return null;
            }

            decimal? retailPrice = set.SetRetailPrice;
            decimal? discountRate = null;
            string? codeId = set.SetCodeId;
            var kind = "set";
            var resolvedBarcode = set.SetBarcode!;
            if (detail.ProductType == 2)
            {
                var resolvedSetProductCode = ResolveSetProductCode(set.SetProductCode, set.SetCodeId);
                var projectionQuery = _db.Queryable<StoreMultiCodeProduct>()
                    .Where(x => !x.IsDeleted
                        && x.IsActive
                        && x.ProductCode == detail.ProductCode
                        && x.MultiCodeProductCode == resolvedSetProductCode);
                if (!string.IsNullOrWhiteSpace(storeCode))
                {
                    projectionQuery = projectionQuery.Where(x => x.StoreCode == storeCode);
                }
                var projections = await projectionQuery.ToListAsync();
                var projection = projections.Count == 1 ? projections[0] : null;
                if (projection == null
                    || !string.Equals(projection.MultiBarcode, keyword, StringComparison.Ordinal))
                {
                    return null;
                }
                retailPrice = projection.MultiCodeRetailPrice;
                discountRate = projection.DiscountRate;
                codeId = projection.UUID;
                kind = "multi";
                resolvedBarcode = projection.MultiBarcode!;
            }

            return retailPrice is > 0
                ? new StoreProductPrintTargetDto
                {
                    Kind = kind,
                    Barcode = resolvedBarcode,
                    RetailPrice = retailPrice.Value,
                    DiscountRate = discountRate,
                    ProductCode = detail.ProductCode,
                    CodeId = codeId,
                    StoreCode = storeCode,
                }
                : null;
        }

        public async Task<ApiResponse<StoreProductDetailDto>> GetDetailAsync(
            string productCode,
            string? storeCode,
            List<string>? accessibleStoreCodes,
            bool includeCodes = true
        )
        {
            var totalSw = Stopwatch.StartNew();
            try
            {
                if (string.IsNullOrWhiteSpace(productCode))
                {
                    return ApiResponse<StoreProductDetailDto>.Error("商品编码不能为空");
                }

                var selectedStoreCodes = ResolveScopedStoreCodes(storeCode, accessibleStoreCodes);
                _logger.LogDebug(
                    "StoreProductMaintenance detail started productCode={ProductCode} requestedStore={RequestedStore} scope={Scope}",
                    productCode,
                    storeCode,
                    FormatStoreScope(selectedStoreCodes)
                );
                if (selectedStoreCodes != null && selectedStoreCodes.Count == 0)
                {
                    return ApiResponse<StoreProductDetailDto>.Error("当前账号或设备无权访问该分店");
                }

                var productSw = Stopwatch.StartNew();
                var product = await _db.Queryable<Product>()
                    .LeftJoin<ProductGrade>((p, pg) => p.ProductCode == pg.ProductCode && !pg.IsDeleted)
                    .LeftJoin<HBLocalSupplier>((p, pg, ls) =>
                        p.LocalSupplierCode == ls.LocalSupplierCode && !ls.IsDeleted
                    )
                    .Where((p, pg, ls) => p.ProductCode == productCode && !p.IsDeleted)
                    .Select((p, pg, ls) => new
                    {
                        p.ProductCode,
                        p.ProductName,
                        p.ItemNumber,
                        p.Barcode,
                        p.ProductImage,
                        p.ProductType,
                        p.LocalSupplierCode,
                        LocalSupplierName = ls.Name,
                        Grade = pg.Grade,
                    })
                    .FirstAsync();
                productSw.Stop();

                if (product == null)
                {
                    return ApiResponse<StoreProductDetailDto>.Error("商品不存在");
                }

                StoreProductStorePriceDto? storePrice = null;
                StoreRetailPrice? storePriceEntity = null;
                var storePriceSw = Stopwatch.StartNew();
                if (selectedStoreCodes == null || selectedStoreCodes.Count > 0)
                {
                    storePriceEntity = await QueryStorePriceAsync(productCode, selectedStoreCodes);
                }
                storePriceSw.Stop();

                StoreProductClearancePriceDto? clearancePrice = null;
                StoreClearancePrice? clearancePriceEntity = null;
                var clearanceSw = Stopwatch.StartNew();
                if (selectedStoreCodes == null || selectedStoreCodes.Count > 0)
                {
                    clearancePriceEntity = await QueryClearancePriceAsync(productCode, selectedStoreCodes);
                }
                clearanceSw.Stop();

                var setCodesSw = Stopwatch.StartNew();
                var productSetCodes = includeCodes
                    ? await QueryProductSetCodesAsync(productCode, null, null)
                    : new List<ProductSetCode>();
                var codeCounts = includeCodes
                    ? new ProductSetCodeCounts
                    {
                        TotalSetCodeCount = productSetCodes.Count,
                    }
                    : await QueryProductSetCodeCountsAsync(productCode);
                setCodesSw.Stop();

                var currentStoreCode = ResolveCurrentStoreCode(storeCode, selectedStoreCodes, storePriceEntity);
                var pricingStrategyLoadSw = Stopwatch.StartNew();
                var pricingContext = await CreatePricingContextAsync();
                pricingStrategyLoadSw.Stop();

                var storeNameLoadSw = Stopwatch.StartNew();
                var storeNameMap = await QueryStoreNamesAsync(
                    storePriceEntity?.StoreCode,
                    clearancePriceEntity?.StoreCode,
                    currentStoreCode
                );
                storeNameLoadSw.Stop();

                if (storePriceEntity != null)
                {
                    storeNameMap.TryGetValue(storePriceEntity.StoreCode ?? string.Empty, out var storeName);
                    storePrice = BuildStorePriceDto(
                        storePriceEntity,
                        product.LocalSupplierCode,
                        storeName,
                        pricingContext
                    );
                }

                if (clearancePriceEntity != null)
                {
                    storeNameMap.TryGetValue(clearancePriceEntity.StoreCode ?? string.Empty, out var storeName);
                    clearancePrice = BuildClearancePriceDto(clearancePriceEntity, storeName);
                }

                var projectionSw = Stopwatch.StartNew();
                var projections = includeCodes
                    ? await QueryProjectedStoreMultiCodesAsync(productSetCodes, currentStoreCode)
                    : new List<StoreMultiCodeProduct>();
                projectionSw.Stop();
                var projectionMap = projections.ToDictionary(
                    p => ResolveSetProductCode(p.MultiCodeProductCode, p.UUID),
                    p => p
                );

                var setCodes = new List<StoreProductSetCodeDto>();
                var multiCodes = new List<StoreProductMultiCodeDto>();
                var buildResponseSw = Stopwatch.StartNew();
                foreach (var setCode in productSetCodes)
                {
                    var setProductCode = ResolveSetProductCode(setCode.SetProductCode, setCode.SetCodeId);
                    projectionMap.TryGetValue(setProductCode, out var projection);
                    if (product.ProductType == 2)
                    {
                        multiCodes.Add(
                            BuildMultiCodeDto(
                                setCode,
                                projection,
                                product.LocalSupplierCode,
                                pricingContext
                            )
                        );
                        continue;
                    }

                    setCodes.Add(BuildSetCodeDto(setCode, projection));
                }
                buildResponseSw.Stop();

                var detail = new StoreProductDetailDto
                {
                    ProductCode = product.ProductCode ?? string.Empty,
                    ProductName = product.ProductName,
                    ItemNumber = product.ItemNumber,
                    Barcode = product.Barcode,
                    ProductImage = product.ProductImage,
                    ProductType = product.ProductType,
                    ProductTypeLabel = NormalizeProductTypeLabel(product.ProductType?.ToString()),
                    Grade = product.Grade,
                    LocalSupplierCode = product.LocalSupplierCode,
                    LocalSupplierName = product.LocalSupplierName,
                    StorePrice = storePrice,
                    ClearancePrice = clearancePrice,
                    MultiCodes = multiCodes,
                    SetCodes = setCodes,
                    SetCodeCount = product.ProductType == 2 ? 0 : codeCounts.TotalSetCodeCount,
                    MultiCodeCount = product.ProductType == 2 ? codeCounts.TotalSetCodeCount : 0,
                    CodesIncluded = includeCodes,
                };

                _logger.LogInformation(
                    "StoreProductMaintenance detail timing productCode={ProductCode} requestedStore={RequestedStore} scope={Scope} includeCodes={IncludeCodes} product_ms={ProductMs} storePrice_ms={StorePriceMs} clearance_ms={ClearanceMs} productSetCodes_ms={ProductSetCodesMs} pricing_strategy_load_ms={PricingStrategyLoadMs} store_name_load_ms={StoreNameLoadMs} projection_query_ms={ProjectionQueryMs} response_build_ms={ResponseBuildMs} set_count={SetCount} multi_count={MultiCount} total_ms={TotalMs}",
                    productCode,
                    storeCode,
                    FormatStoreScope(selectedStoreCodes),
                    includeCodes,
                    productSw.ElapsedMilliseconds,
                    storePriceSw.ElapsedMilliseconds,
                    clearanceSw.ElapsedMilliseconds,
                    setCodesSw.ElapsedMilliseconds,
                    pricingStrategyLoadSw.ElapsedMilliseconds,
                    storeNameLoadSw.ElapsedMilliseconds,
                    projectionSw.ElapsedMilliseconds,
                    buildResponseSw.ElapsedMilliseconds,
                    setCodes.Count,
                    multiCodes.Count,
                    totalSw.ElapsedMilliseconds
                );

                return ApiResponse<StoreProductDetailDto>.OK(detail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取商品详情失败: {ProductCode}", productCode);
                return ApiResponse<StoreProductDetailDto>.Error($"获取商品详情失败: {ex.Message}");
            }
        }

        public async Task<ApiResponse<StoreProductDetailDto>> GetFastDetailAsync(
            string productCode,
            string? storeCode,
            List<string>? accessibleStoreCodes,
            CancellationToken cancellationToken = default
        )
        {
            var totalSw = Stopwatch.StartNew();
            try
            {
                var normalizedProductCode = productCode?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedProductCode))
                {
                    return ApiResponse<StoreProductDetailDto>.Error("商品编码不能为空");
                }

                var selectedStoreCodes = ResolveScopedStoreCodes(storeCode, accessibleStoreCodes);
                if (selectedStoreCodes != null && selectedStoreCodes.Count == 0)
                {
                    return ApiResponse<StoreProductDetailDto>.Error("当前账号或设备无权访问该分店");
                }

                var targetStoreCode = ResolveFastDetailTargetStoreCode(storeCode, selectedStoreCodes);
                var baseSw = Stopwatch.StartNew();
                var fastRow = await QueryFastDetailBaseAsync(normalizedProductCode, targetStoreCode);
                baseSw.Stop();
                if (fastRow == null)
                {
                    return ApiResponse<StoreProductDetailDto>.Error("商品不存在");
                }

                var pricingContextRequired =
                    !string.IsNullOrWhiteSpace(fastRow.StorePriceUuid)
                    && fastRow.PurchasePrice.HasValue
                    && fastRow.PurchasePrice.Value > 0;
                PricingContext? pricingContext = null;
                var pricingStrategyLoadSw = Stopwatch.StartNew();
                if (pricingContextRequired)
                {
                    pricingContext = await CreatePricingContextAsync();
                }
                pricingStrategyLoadSw.Stop();

                var responseBuildSw = Stopwatch.StartNew();
                var storePriceEntity = BuildStorePriceEntity(fastRow);
                var clearancePriceEntity = BuildClearancePriceEntity(fastRow);
                var totalSetCodes = fastRow.TotalSetCodeCount;
                int setCodeCount = 0;
                int multiCodeCount = 0;
                if (totalSetCodes > 0)
                {
                    if (fastRow.ProductType == 2)
                        multiCodeCount = totalSetCodes;
                    else
                        setCodeCount = totalSetCodes;
                }
                var detail = new StoreProductDetailDto
                {
                    ProductCode = fastRow.ProductCode ?? string.Empty,
                    ProductName = fastRow.ProductName ?? string.Empty,
                    ItemNumber = fastRow.ItemNumber,
                    Barcode = fastRow.Barcode,
                    ProductImage = fastRow.ProductImage,
                    ProductType = fastRow.ProductType,
                    ProductTypeLabel = NormalizeProductTypeLabel(fastRow.ProductType?.ToString()),
                    Grade = fastRow.Grade,
                    LocalSupplierCode = fastRow.LocalSupplierCode,
                    LocalSupplierName = fastRow.LocalSupplierName,
                    StorePrice = storePriceEntity == null
                        ? null
                        : BuildStorePriceDto(
                            storePriceEntity,
                            fastRow.LocalSupplierCode,
                            fastRow.StoreName,
                            pricingContext
                        ),
                    ClearancePrice = clearancePriceEntity == null
                        ? null
                        : BuildClearancePriceDto(clearancePriceEntity, fastRow.ClearanceStoreName),
                    SetCodes = new List<StoreProductSetCodeDto>(),
                    MultiCodes = new List<StoreProductMultiCodeDto>(),
                    SetCodeCount = setCodeCount,
                    MultiCodeCount = multiCodeCount,
                    CodesIncluded = false,
                };
                responseBuildSw.Stop();

                _logger.LogInformation(
                    "StoreProductMaintenance fast-detail timing productCode={ProductCode} requestedStore={RequestedStore} scope={Scope} targetStore={TargetStore} pricing_context_required={PricingContextRequired} base_ms={BaseMs} pricing_strategy_load_ms={PricingStrategyLoadMs} response_build_ms={ResponseBuildMs} set_count={SetCount} multi_count={MultiCount} total_ms={TotalMs}",
                    normalizedProductCode,
                    storeCode,
                    FormatStoreScope(selectedStoreCodes),
                    targetStoreCode,
                    pricingContextRequired,
                    baseSw.ElapsedMilliseconds,
                    pricingStrategyLoadSw.ElapsedMilliseconds,
                    responseBuildSw.ElapsedMilliseconds,
                    detail.SetCodeCount,
                    detail.MultiCodeCount,
                    totalSw.ElapsedMilliseconds
                );

                return ApiResponse<StoreProductDetailDto>.OK(detail);
            }
            catch (Exception ex) when (ClientAbortDetector.IsClientAbort(ex, cancellationToken))
            {
                // 调用方已取消（请求被客户端中止）：直接上抛，不记错误；全局过滤器按 499 处理。
                // 认证阶段已把请求令牌留在 SqlSugar ADO 上，本方法内的查询都会随它取消（OCE 或用户取消的 SqlException）。
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取商品快详情失败: {ProductCode}", productCode);
                return ApiResponse<StoreProductDetailDto>.Error($"获取商品详情失败: {ex.Message}");
            }
        }

        public async Task<ApiResponse<StoreProductStorePriceDto>> EnsureStorePriceAsync(
            string productCode,
            string? storeCode,
            string updatedBy,
            List<string>? accessibleStoreCodes
        )
        {
            var normalizedProductCode = productCode?.Trim();
            var normalizedStoreCode = storeCode?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedProductCode))
                return ApiResponse<StoreProductStorePriceDto>.Error("商品编码不能为空");
            // 补建必须指定当前分店，不能从多店权限中猜测目标或批量创建。
            if (string.IsNullOrWhiteSpace(normalizedStoreCode))
                return ApiResponse<StoreProductStorePriceDto>.Error("请选择需要补建价格的分店");
            if (!CanAccessStore(normalizedStoreCode, accessibleStoreCodes))
                return ApiResponse<StoreProductStorePriceDto>.Error("当前账号或设备无权修改该分店商品");

            try
            {
                await _db.Ado.BeginTranAsync();
                try
                {
                    await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        _db, new[] { normalizedProductCode }
                    );
                    // 与价格同步入口保持主商品在前、分店价格在后的取锁顺序。
                    var product = await WithWarehouseSyncUpdateLock(_db.Queryable<Product>()
                            .Where(x => x.ProductCode == normalizedProductCode && !x.IsDeleted))
                        .FirstAsync();
                    var query = _db.Queryable<StoreRetailPrice>()
                        .Where(x => x.ProductCode == normalizedProductCode
                            && x.StoreCode == normalizedStoreCode && !x.IsDeleted);
                    if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
                    {
                        // 业务锁后复查，并保护缺失业务键的范围，防止并发扫码或同步任务重复插入。
                        query = query.With("UPDLOCK, HOLDLOCK");
                    }
                    var existing = await query.Take(2).ToListAsync();
                    if (existing.Count > 1)
                        throw new StoreProductMaintenanceBusinessException("当前分店存在多条商品价格记录，请先处理重复记录");

                    var entity = existing.FirstOrDefault();
                    ProductHqSyncOperationStatusDto? hqSync = null;
                    if (entity == null)
                    {
                        var store = await _db.Queryable<Store>()
                            .Where(x => x.StoreCode == normalizedStoreCode && !x.IsDeleted && x.IsActive)
                            .FirstAsync();
                        if (store == null)
                            throw new StoreProductMaintenanceBusinessException("分店不存在或已停用");

                        if (product == null)
                            throw new StoreProductMaintenanceBusinessException("商品不存在");

                        var now = DateTime.UtcNow;
                        // 与商品完整性补建一致：继承主档默认值，保留空价格，不借用其他分店价格。
                        // 软删除历史不复活；已有有效行（含停用行）完全保留。
                        entity = new StoreRetailPrice
                        {
                            StoreCode = store.StoreCode,
                            ProductCode = product.ProductCode,
                            StoreProductCode = store.StoreCode + product.ProductCode,
                            SupplierCode = product.LocalSupplierCode,
                            PurchasePrice = product.PurchasePrice,
                            StoreRetailPriceValue = product.RetailPrice,
                            DiscountRate = 1m,
                            IsActive = product.IsActive,
                            IsAutoPricing = product.IsAutoPricing,
                            IsSpecialProduct = product.IsSpecialProduct,
                            IsDeleted = false,
                            CreatedAt = now,
                            UpdatedAt = now,
                            CreatedBy = updatedBy,
                            UpdatedBy = updatedBy,
                        };
                        // 低于自动定价器的最低成本时保留主档售价，补建不强制改价。
                        if (entity.IsAutoPricing && entity.PurchasePrice >= 0.1m)
                        {
                            var strategy = await _autoPricingService.FindStrategyForPriceAsync(
                                entity.PurchasePrice.Value, entity.SupplierCode, entity.StoreCode
                            );
                            entity.StoreRetailPriceValue = _autoPricingService.CalculateRetailPrice(
                                entity.PurchasePrice.Value, strategy
                            );
                        }

                        await _db.Insertable(entity).ExecuteCommandAsync();
                        // 本入口只补主价格；多码/套装已有价格和状态不属于本次补建范围。
                        hqSync = await EnqueueHqProjectionAsync(
                            ProductMaintenanceHqOperationKinds.StorePriceUpdated,
                            entity.ProductCode!,
                            new[] { entity.StoreCode! },
                            new[]
                            {
                                ProductMaintenanceHqFieldMasks.StorePurchasePrice,
                                ProductMaintenanceHqFieldMasks.StoreRetailPrice,
                                ProductMaintenanceHqFieldMasks.StoreDiscountRate,
                                ProductMaintenanceHqFieldMasks.StoreAutoPricing,
                                ProductMaintenanceHqFieldMasks.StoreSpecialProduct,
                                ProductMaintenanceHqFieldMasks.StoreActive,
                            },
                            "react-store-product-maintenance.ensure-store-price",
                            updatedBy,
                            accessibleStoreCodes
                        );
                    }

                    var dto = await BuildStorePriceDtoAsync(entity, entity.SupplierCode);
                    dto.HqSync = hqSync;
                    await _db.Ado.CommitTranAsync();
                    return ApiResponse<StoreProductStorePriceDto>.OK(dto);
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "补建分店商品价格失败: {ProductCode}/{StoreCode}", productCode, storeCode);
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<StoreProductStorePriceDto>();
                return ApiResponse<StoreProductStorePriceDto>.Error(
                    ResolveSafeMutationFailureMessage(ex, "补建分店商品价格失败，请稍后重试")
                );
            }
        }

        public Task<ApiResponse<StoreProductStorePriceDto>> UpdateStorePriceAsync(
            string uuid,
            UpdateStoreProductPriceDto request,
            string updatedBy,
            List<string>? accessibleStoreCodes
        ) => UpdateStorePriceAsync(uuid, request, updatedBy, accessibleStoreCodes, enqueueHqProjection: true);

        public async Task<ApiResponse<StoreProductStorePriceDto>> UpdateStorePriceAsync(
            string uuid,
            UpdateStoreProductPriceDto request,
            string updatedBy,
            List<string>? accessibleStoreCodes,
            bool enqueueHqProjection
        )
        {
            try
            {
                var entity = await _db.Queryable<StoreRetailPrice>()
                    .Where(x => x.UUID == uuid && !x.IsDeleted)
                    .FirstAsync();

                if (entity == null)
                {
                    return ApiResponse<StoreProductStorePriceDto>.Error("分店商品记录不存在");
                }

                if (!CanAccessStore(entity.StoreCode, accessibleStoreCodes))
                {
                    return ApiResponse<StoreProductStorePriceDto>.Error("当前账号或设备无权修改该分店商品");
                }

                if (request.PurchasePrice.HasValue && request.PurchasePrice.Value < 0)
                {
                    return ApiResponse<StoreProductStorePriceDto>.Error("进货价不能为负数");
                }

                if (request.RetailPrice.HasValue && request.RetailPrice.Value < 0)
                {
                    return ApiResponse<StoreProductStorePriceDto>.Error("零售价不能为负数");
                }

                if (
                    request.DiscountRate.HasValue
                    && (request.DiscountRate.Value < 0 || request.DiscountRate.Value > 1)
                )
                {
                    return ApiResponse<StoreProductStorePriceDto>.Error("折扣率必须在 0 到 1 之间");
                }

                ProductHqSyncOperationStatusDto? hqSync = null;
                StoreProductStorePriceDto? dto = null;
                await _db.Ado.BeginTranAsync();
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        _db,
                        new[] { entity.ProductCode }
                    );
                    // 等锁后重新读取目标行，禁止把锁前加载的实体整行写回。
                    entity = await _db.Queryable<StoreRetailPrice>()
                        .Where(x => x.UUID == uuid && !x.IsDeleted)
                        .FirstAsync();
                    if (entity == null)
                    {
                        throw new StoreProductMaintenanceBusinessException("分店商品记录在等待业务锁期间已变化");
                    }
                    if (!CanAccessStore(entity.StoreCode, accessibleStoreCodes))
                    {
                        throw new StoreProductMaintenanceBusinessException("当前账号或设备无权修改该分店商品");
                    }

                    entity.PurchasePrice = request.PurchasePrice;
                    entity.StoreRetailPriceValue = request.RetailPrice;
                    entity.DiscountRate = request.DiscountRate;
                    if (request.IsAutoPricing.HasValue) entity.IsAutoPricing = request.IsAutoPricing.Value;
                    if (request.IsSpecialProduct.HasValue) entity.IsSpecialProduct = request.IsSpecialProduct.Value;
                    if (request.IsActive.HasValue) entity.IsActive = request.IsActive.Value;

                    var supplierCode = entity.SupplierCode;
                    if (string.IsNullOrWhiteSpace(supplierCode))
                    {
                        supplierCode = await _db.Queryable<Product>()
                            .Where(p => p.ProductCode == entity.ProductCode)
                            .Select(p => p.LocalSupplierCode)
                            .FirstAsync();
                    }
                    if (entity.IsAutoPricing && entity.PurchasePrice.HasValue)
                    {
                        var strategy = await _autoPricingService.FindStrategyForPriceAsync(
                            entity.PurchasePrice.Value, supplierCode, entity.StoreCode
                        );
                        entity.StoreRetailPriceValue = _autoPricingService.CalculateRetailPrice(
                            entity.PurchasePrice.Value, strategy
                        );
                    }

                    entity.UpdatedAt = DateTime.UtcNow;
                    entity.UpdatedBy = updatedBy;
                    // 仅写本入口拥有的字段，保留库存、门店归属等并发更新。
                    await _db.Updateable(entity)
                        .UpdateColumns(x => new
                        {
                            x.PurchasePrice,
                            x.StoreRetailPriceValue,
                            x.DiscountRate,
                            x.IsAutoPricing,
                            x.IsSpecialProduct,
                            x.IsActive,
                            x.UpdatedAt,
                            x.UpdatedBy,
                        })
                        .ExecuteCommandAsync();
                    await SyncCurrentStoreProjectedRecordsAsync(entity, updatedBy, lockScope);
                    // 价格更新通知可通过配置关闭 HQ 同步；其余入口恒为 true，行为不变。
                    if (enqueueHqProjection)
                    {
                        hqSync = await EnqueueHqProjectionAsync(
                            ProductMaintenanceHqOperationKinds.StorePriceUpdated,
                            entity.ProductCode!,
                            new[] { entity.StoreCode! },
                            ProductMaintenanceHqFieldMasks.StorePriceAndMultiCode,
                            "react-store-product-maintenance.update-store-price",
                            updatedBy,
                            accessibleStoreCodes
                        );
                    }
                    dto = await BuildStorePriceDtoAsync(entity, entity.SupplierCode);
                    dto.HqSync = hqSync;
                    await _db.Ado.CommitTranAsync();
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }

                return ApiResponse<StoreProductStorePriceDto>.OK(dto!, "保存成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新分店商品失败: {Uuid}", uuid);
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<StoreProductStorePriceDto>();
                return ApiResponse<StoreProductStorePriceDto>.Error(
                    ResolveSafeMutationFailureMessage(ex, "更新分店商品失败，请稍后重试")
                );
            }
        }

        public async Task<ApiResponse<SyncStoreProductWarehousePriceResultDto>> SyncWarehousePriceAsync(
            string uuid,
            SyncStoreProductWarehousePriceRequestDto request,
            string updatedBy,
            List<string>? accessibleStoreCodes
        )
        {
            await _db.Ado.BeginTranAsync();
            try
            {
                var result = await SyncWarehousePriceWithinTransactionAsync(
                    uuid,
                    request,
                    updatedBy,
                    accessibleStoreCodes
                );
                if (result.Success)
                {
                    await _db.Ado.CommitTranAsync();
                }
                else
                {
                    await _db.Ado.RollbackTranAsync();
                }

                return result;
            }
            catch (Exception ex)
            {
                await _db.Ado.RollbackTranAsync();
                _logger.LogError(ex, "分店商品仓库价格对账失败: {Uuid}", uuid);
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<SyncStoreProductWarehousePriceResultDto>();
                return ApiResponse<SyncStoreProductWarehousePriceResultDto>.Error(
                    "仓库价格对账失败，请稍后重试"
                );
            }
        }

        private async Task<ApiResponse<SyncStoreProductWarehousePriceResultDto>> SyncWarehousePriceWithinTransactionAsync(
            string uuid,
            SyncStoreProductWarehousePriceRequestDto request,
            string updatedBy,
            List<string>? accessibleStoreCodes
        )
        {
            // 定位读取只取锁序所需字段，保持普通 READ COMMITTED，不提前占用分店价更新锁。
            var locator = await _db.Ado.SqlQuerySingleAsync<StoreRetailPrice>(
                "SELECT UUID, StoreCode, ProductCode FROM StoreRetailPrice WHERE UUID = @uuid AND (IsDeleted = 0 OR IsDeleted IS NULL)",
                new { uuid }
            );
            if (locator == null)
            {
                return ApiResponse<SyncStoreProductWarehousePriceResultDto>.Error(
                    "分店商品记录不存在"
                );
            }

            if (!CanAccessStore(locator.StoreCode, accessibleStoreCodes))
            {
                return ApiResponse<SyncStoreProductWarehousePriceResultDto>.Error(
                    "当前账号或设备无权修改该分店商品"
                );
            }

            var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                _db,
                new[] { locator.ProductCode }
            );

            // 与仓库商品写链路保持 Product → WarehouseProduct → StoreRetailPrice 的固定锁序。
            var product = await WithWarehouseSyncUpdateLock(
                    _db.Queryable<Product>()
                        .Where(x => x.ProductCode == locator.ProductCode && !x.IsDeleted)
                )
                .FirstAsync();
            var warehouseProduct = await WithWarehouseSyncUpdateLock(
                    _db.Queryable<WarehouseProduct>()
                        .Where(x => x.ProductCode == locator.ProductCode && !x.IsDeleted)
                )
                .FirstAsync();
            var entity = await WithWarehouseSyncUpdateLock(
                    _db.Queryable<StoreRetailPrice>().Where(x => x.UUID == uuid && !x.IsDeleted)
                )
                .FirstAsync();
            if (entity == null)
            {
                return ApiResponse<SyncStoreProductWarehousePriceResultDto>.Error(
                    "分店商品记录已变化",
                    "PRICE_VERSION_CONFLICT"
                );
            }

            // 最终锁定后必须先复核权限，避免并发换店时把无权分店价格放进冲突响应。
            if (!CanAccessStore(entity.StoreCode, accessibleStoreCodes))
            {
                return ApiResponse<SyncStoreProductWarehousePriceResultDto>.Error(
                    "当前账号或设备无权修改该分店商品"
                );
            }

            if (
                !string.Equals(entity.ProductCode, locator.ProductCode, StringComparison.Ordinal)
                || !string.Equals(entity.StoreCode, locator.StoreCode, StringComparison.Ordinal)
            )
            {
                return await BuildWarehousePriceConflictAsync(
                    entity,
                    entity.SupplierCode,
                    null,
                    null
                );
            }

            if (product == null)
            {
                return ApiResponse<SyncStoreProductWarehousePriceResultDto>.Error("商品记录不存在");
            }

            var supplierCode = product.LocalSupplierCode?.Trim();
            var previousPurchasePrice = entity.PurchasePrice;
            var previousRetailPrice = entity.StoreRetailPriceValue;
            var discountRate = entity.DiscountRate;
            if (!string.Equals(supplierCode, "200", StringComparison.Ordinal))
            {
                var notApplicable = await BuildWarehousePriceSyncResultAsync(
                    entity,
                    supplierCode,
                    "not_applicable",
                    false,
                    false,
                    false,
                    null,
                    null,
                    previousPurchasePrice,
                    previousRetailPrice,
                    discountRate
                );
                return ApiResponse<SyncStoreProductWarehousePriceResultDto>.OK(notApplicable);
            }

            var warehousePurchasePrice = NormalizeSourcePrice(warehouseProduct?.ImportPrice);
            var warehouseRetailPrice = NormalizeSourcePrice(warehouseProduct?.OEMPrice);
            if (
                warehouseProduct == null
                || (!warehousePurchasePrice.HasValue && !warehouseRetailPrice.HasValue)
            )
            {
                var missingSource = await BuildWarehousePriceSyncResultAsync(
                    entity,
                    supplierCode,
                    "missing_source",
                    false,
                    false,
                    false,
                    warehousePurchasePrice,
                    warehouseRetailPrice,
                    previousPurchasePrice,
                    previousRetailPrice,
                    discountRate
                );
                return ApiResponse<SyncStoreProductWarehousePriceResultDto>.OK(missingSource);
            }

            var purchaseChanged = warehousePurchasePrice.HasValue
                && !PricesEqual(entity.PurchasePrice, warehousePurchasePrice);
            var retailChanged = warehouseRetailPrice.HasValue
                && !PricesEqual(entity.StoreRetailPriceValue, warehouseRetailPrice);

            // 确认请求必须基于用户看到的完整快照；任一来源或目标值变化都拒绝写入。
            if (
                request.ConfirmRetailPrice
                && !WarehousePriceSnapshotMatches(
                    request,
                    warehousePurchasePrice,
                    warehouseRetailPrice,
                    entity.PurchasePrice,
                    entity.StoreRetailPriceValue,
                    entity.DiscountRate
                )
            )
            {
                return await BuildWarehousePriceConflictAsync(
                    entity,
                    supplierCode,
                    warehousePurchasePrice,
                    warehouseRetailPrice
                );
            }

            var purchaseUpdated = purchaseChanged;
            var retailUpdated = request.ConfirmRetailPrice && retailChanged;
            ProductHqSyncOperationStatusDto? hqSync = null;
            if (purchaseUpdated || retailUpdated)
            {
                var previousIsAutoPricing = entity.IsAutoPricing;
                var previousIsSpecialProduct = entity.IsSpecialProduct;
                var previousIsActive = entity.IsActive;
                var previousUpdatedAt = entity.UpdatedAt;
                var previousSupplierCode = entity.SupplierCode;
                var previousStoreCode = entity.StoreCode;
                var previousProductCode = entity.ProductCode;

                if (purchaseUpdated)
                {
                    entity.PurchasePrice = warehousePurchasePrice;
                }

                if (retailUpdated)
                {
                    entity.StoreRetailPriceValue = warehouseRetailPrice;
                }

                entity.UpdatedAt = DateTime.UtcNow;
                entity.UpdatedBy = updatedBy;
                // 仅写本入口负责的价格与更新审计列；WHERE 同时承担乐观并发兜底。
                var affectedRows = await _db.Updateable(entity)
                    .UpdateColumns(x => new
                    {
                        x.PurchasePrice,
                        x.StoreRetailPriceValue,
                        x.UpdatedAt,
                        x.UpdatedBy,
                    })
                    .Where(x =>
                        x.UUID == uuid
                        && !x.IsDeleted
                        && x.StoreCode == previousStoreCode
                        && x.ProductCode == previousProductCode
                        && x.SupplierCode == previousSupplierCode
                        && x.PurchasePrice == previousPurchasePrice
                        && x.StoreRetailPriceValue == previousRetailPrice
                        && x.DiscountRate == discountRate
                        && x.IsAutoPricing == previousIsAutoPricing
                        && x.IsSpecialProduct == previousIsSpecialProduct
                        && x.IsActive == previousIsActive
                        && x.UpdatedAt == previousUpdatedAt
                    )
                    .ExecuteCommandAsync();
                if (affectedRows == 0)
                {
                    var latestEntity = await WithWarehouseSyncUpdateLock(
                            _db.Queryable<StoreRetailPrice>()
                                .Where(x => x.UUID == uuid && !x.IsDeleted)
                        )
                        .FirstAsync();
                    if (latestEntity == null)
                    {
                        return ApiResponse<SyncStoreProductWarehousePriceResultDto>.Error(
                            "分店商品记录已变化",
                            "PRICE_VERSION_CONFLICT"
                        );
                    }

                    return await BuildWarehousePriceConflictAsync(
                        latestEntity,
                        supplierCode,
                        warehousePurchasePrice,
                        warehouseRetailPrice
                    );
                }

                await SyncCurrentStoreProjectedPriceRecordsAsync(entity, updatedBy, lockScope);
                hqSync = await EnqueueHqProjectionAsync(
                    ProductMaintenanceHqOperationKinds.WarehousePriceSynced,
                    entity.ProductCode!,
                    new[] { entity.StoreCode! },
                    ProductMaintenanceHqFieldMasks.StorePriceAndMultiCode,
                    "react-store-product-maintenance.sync-warehouse-price",
                    updatedBy,
                    accessibleStoreCodes
                );
            }

            var confirmationRequired = !request.ConfirmRetailPrice && retailChanged;
            var status = confirmationRequired ? "confirmation_required" : "synced";
            var result = await BuildWarehousePriceSyncResultAsync(
                entity,
                supplierCode,
                status,
                purchaseUpdated,
                retailUpdated,
                confirmationRequired,
                warehousePurchasePrice,
                warehouseRetailPrice,
                previousPurchasePrice,
                previousRetailPrice,
                discountRate
            );
            result.HqSync = hqSync;
            return ApiResponse<SyncStoreProductWarehousePriceResultDto>.OK(result);
        }

        private async Task<ApiResponse<SyncStoreProductWarehousePriceResultDto>> BuildWarehousePriceConflictAsync(
            StoreRetailPrice entity,
            string? supplierCode,
            decimal? warehousePurchasePrice,
            decimal? warehouseRetailPrice
        )
        {
            var retailChanged = warehouseRetailPrice.HasValue
                && !PricesEqual(entity.StoreRetailPriceValue, warehouseRetailPrice);
            var latest = await BuildWarehousePriceSyncResultAsync(
                entity,
                supplierCode,
                retailChanged ? "confirmation_required" : "synced",
                false,
                false,
                retailChanged,
                warehousePurchasePrice,
                warehouseRetailPrice,
                entity.PurchasePrice,
                entity.StoreRetailPriceValue,
                entity.DiscountRate
            );
            return new ApiResponse<SyncStoreProductWarehousePriceResultDto>
            {
                Success = false,
                ErrorCode = "PRICE_VERSION_CONFLICT",
                Message = "仓库或分店价格已变化，请按最新价格重新确认",
                Data = latest,
            };
        }

        private ISugarQueryable<T> WithWarehouseSyncUpdateLock<T>(ISugarQueryable<T> queryable)
        {
            return _db.CurrentConnectionConfig.DbType == DbType.SqlServer
                ? queryable.With(SqlWith.UpdLock)
                : queryable;
        }

        private async Task<SyncStoreProductWarehousePriceResultDto> BuildWarehousePriceSyncResultAsync(
            StoreRetailPrice entity,
            string? supplierCode,
            string status,
            bool purchaseUpdated,
            bool retailUpdated,
            bool retailConfirmationRequired,
            decimal? warehousePurchasePrice,
            decimal? warehouseRetailPrice,
            decimal? previousStorePurchasePrice,
            decimal? previousStoreRetailPrice,
            decimal? discountRate
        )
        {
            return new SyncStoreProductWarehousePriceResultDto
            {
                Status = status,
                PurchaseUpdated = purchaseUpdated,
                RetailUpdated = retailUpdated,
                RetailConfirmationRequired = retailConfirmationRequired,
                StorePrice = await BuildStorePriceDtoAsync(entity, supplierCode),
                WarehousePurchasePrice = warehousePurchasePrice,
                WarehouseRetailPrice = warehouseRetailPrice,
                PreviousStorePurchasePrice = previousStorePurchasePrice,
                PreviousStoreRetailPrice = previousStoreRetailPrice,
                DiscountRate = discountRate,
                PreviousDiscountedRetailPrice = CalculateDiscountedPrice(
                    previousStoreRetailPrice,
                    discountRate
                ),
                NewDiscountedRetailPrice = CalculateDiscountedPrice(
                    warehouseRetailPrice ?? previousStoreRetailPrice,
                    discountRate
                ),
            };
        }

        private static bool WarehousePriceSnapshotMatches(
            SyncStoreProductWarehousePriceRequestDto request,
            decimal? warehousePurchasePrice,
            decimal? warehouseRetailPrice,
            decimal? storePurchasePrice,
            decimal? storeRetailPrice,
            decimal? discountRate
        )
        {
            return PricesEqual(request.ExpectedWarehousePurchasePrice, warehousePurchasePrice)
                && PricesEqual(request.ExpectedWarehouseRetailPrice, warehouseRetailPrice)
                && PricesEqual(request.ExpectedStorePurchasePrice, storePurchasePrice)
                && PricesEqual(request.ExpectedStoreRetailPrice, storeRetailPrice)
                && DiscountRatesEqual(request.ExpectedDiscountRate, discountRate);
        }

        private static decimal? NormalizeSourcePrice(decimal? value)
        {
            if (!value.HasValue)
            {
                return null;
            }

            var rounded = RoundPrice(value.Value);
            return rounded > 0 ? rounded : null;
        }

        private static bool PricesEqual(decimal? left, decimal? right)
        {
            return RoundNullablePrice(left) == RoundNullablePrice(right);
        }

        private static bool SetCodeSnapshotMatches(
            IReadOnlyCollection<ProductSetCode> currentItems,
            IReadOnlyCollection<SaveStoreProductSetCodeSnapshotItemDto> expectedItems
        )
        {
            if (currentItems.Count != expectedItems.Count)
            {
                return false;
            }

            var expectedById = expectedItems.ToDictionary(
                item => item.SetCodeId!.Trim(),
                StringComparer.Ordinal
            );
            return currentItems.All(current =>
                expectedById.TryGetValue(current.SetCodeId, out var expected)
                && string.Equals(
                    current.SetBarcode?.Trim() ?? string.Empty,
                    expected.Barcode?.Trim() ?? string.Empty,
                    StringComparison.Ordinal
                )
                && current.SetType == expected.SetType
                && current.IsActive == expected.IsActive
                && (current.SetType != 1 || PricesEqual(current.SetRetailPrice, expected.RetailPrice))
            );
        }

        private static bool DiscountRatesEqual(decimal? left, decimal? right)
        {
            return RoundNullableDiscountRate(left) == RoundNullableDiscountRate(right);
        }

        private static decimal? RoundNullablePrice(decimal? value)
        {
            return value.HasValue ? RoundPrice(value.Value) : null;
        }

        private static decimal? RoundNullableDiscountRate(decimal? value)
        {
            return value.HasValue
                ? Math.Round(value.Value, 4, MidpointRounding.AwayFromZero)
                : null;
        }

        private static decimal RoundPrice(decimal value)
        {
            return Math.Round(value, 2, MidpointRounding.AwayFromZero);
        }

        private static decimal? CalculateDiscountedPrice(decimal? retailPrice, decimal? discountRate)
        {
            if (!retailPrice.HasValue)
            {
                return null;
            }

            // DiscountRate 表示减免比例（例如 0.2 为减 20%），不是支付比例。
            var discountReductionRate = discountRate ?? 0m;
            return RoundPrice(retailPrice.Value * (1m - discountReductionRate));
        }

        public async Task<ApiResponse<StoreProductCodePageDto<StoreProductSetCodeDto>>> GetSetCodesAsync(
            string productCode,
            string? storeCode,
            int page,
            int pageSize,
            string? keyword,
            List<string>? accessibleStoreCodes
        )
        {
            try
            {
                var pageResult = await QueryProductCodesPageAsync(
                    productCode,
                    storeCode,
                    1,
                    page,
                    pageSize,
                    keyword,
                    accessibleStoreCodes
                );
                if (pageResult.ErrorMessage != null)
                {
                    return ApiResponse<StoreProductCodePageDto<StoreProductSetCodeDto>>.Error(pageResult.ErrorMessage);
                }

                var projections = await QueryProjectedStoreMultiCodesAsync(
                    pageResult.SetCodes,
                    pageResult.StoreCode
                );
                var projectionMap = projections.ToDictionary(
                    p => ResolveSetProductCode(p.MultiCodeProductCode, p.UUID),
                    p => p
                );
                var items = pageResult.SetCodes
                    .Select(setCode =>
                    {
                        var setProductCode = ResolveSetProductCode(setCode.SetProductCode, setCode.SetCodeId);
                        projectionMap.TryGetValue(setProductCode, out var projection);
                        return BuildSetCodeDto(setCode, projection);
                    })
                    .ToList();

                return ApiResponse<StoreProductCodePageDto<StoreProductSetCodeDto>>.OK(
                    BuildCodePage(items, pageResult.TotalCount, pageResult.Page, pageResult.PageSize)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取套装条码分页失败: {ProductCode}", productCode);
                return ApiResponse<StoreProductCodePageDto<StoreProductSetCodeDto>>.Error(
                    $"获取套装条码失败: {ex.Message}"
                );
            }
        }

        public async Task<ApiResponse<StoreProductCodePageDto<StoreProductMultiCodeDto>>> GetMultiCodesAsync(
            string productCode,
            string? storeCode,
            int page,
            int pageSize,
            string? keyword,
            List<string>? accessibleStoreCodes
        )
        {
            try
            {
                var pageResult = await QueryMultiCodePageAsync(
                    productCode,
                    storeCode,
                    page,
                    pageSize,
                    keyword,
                    accessibleStoreCodes
                );
                if (pageResult.ErrorMessage != null)
                {
                    return ApiResponse<StoreProductCodePageDto<StoreProductMultiCodeDto>>.Error(pageResult.ErrorMessage);
                }

                var projections = await QueryProjectedStoreMultiCodesAsync(
                    pageResult.SetCodes,
                    pageResult.StoreCode
                );
                var projectionMap = projections.ToDictionary(
                    p => ResolveSetProductCode(p.MultiCodeProductCode, p.UUID),
                    p => p
                );
                var pricingContext = await CreatePricingContextAsync();
                var items = pageResult.SetCodes
                    .Select(setCode =>
                    {
                        var setProductCode = ResolveSetProductCode(setCode.SetProductCode, setCode.SetCodeId);
                        projectionMap.TryGetValue(setProductCode, out var projection);
                        return BuildMultiCodeDto(
                            setCode,
                            projection,
                            pageResult.LocalSupplierCode,
                            pricingContext
                        );
                    })
                    .ToList();

                return ApiResponse<StoreProductCodePageDto<StoreProductMultiCodeDto>>.OK(
                    BuildCodePage(items, pageResult.TotalCount, pageResult.Page, pageResult.PageSize)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取多码条码分页失败: {ProductCode}", productCode);
                return ApiResponse<StoreProductCodePageDto<StoreProductMultiCodeDto>>.Error(
                    $"获取多码条码失败: {ex.Message}"
                );
            }
        }

        public async Task<ApiResponse<EvaluateStoreProductAutoPricingResultDto>> EvaluateAutoPricingAsync(
            EvaluateStoreProductAutoPricingDto request,
            List<string>? accessibleStoreCodes
        )
        {
            try
            {
                var productCode = request.ProductCode?.Trim();
                if (string.IsNullOrWhiteSpace(productCode))
                {
                    return ApiResponse<EvaluateStoreProductAutoPricingResultDto>.Error("商品编码不能为空");
                }

                var scopedStoreCodes = ResolveScopedStoreCodes(request.StoreCode, accessibleStoreCodes);
                if (scopedStoreCodes != null && scopedStoreCodes.Count == 0)
                {
                    return ApiResponse<EvaluateStoreProductAutoPricingResultDto>.OK(
                        new EvaluateStoreProductAutoPricingResultDto
                        {
                            ProductCode = productCode,
                            StoreCode = request.StoreCode,
                        }
                    );
                }

                var entity = await QueryStorePriceAsync(productCode, scopedStoreCodes);
                if (entity == null)
                {
                    return ApiResponse<EvaluateStoreProductAutoPricingResultDto>.OK(
                        new EvaluateStoreProductAutoPricingResultDto
                        {
                            ProductCode = productCode,
                            StoreCode = request.StoreCode,
                        }
                    );
                }

                var result = new EvaluateStoreProductAutoPricingResultDto
                {
                    ProductCode = productCode,
                    StoreCode = entity.StoreCode,
                    StorePriceUuid = entity.UUID,
                    CurrentRetailPrice = entity.StoreRetailPriceValue,
                    CurrentRetailPriceFormatted = FormatPrice(entity.StoreRetailPriceValue),
                    DiscountRate = entity.DiscountRate,
                    IsAutoPricing = entity.IsAutoPricing,
                    // 与补建和定价器的最低成本一致，避免补建后的扫码评估再次报错。
                    HasValidPurchasePrice = entity.PurchasePrice.HasValue && entity.PurchasePrice.Value >= 0.1m,
                    ShouldUpdate = false,
                };

                if (!request.ForceAutoPricing && !entity.IsAutoPricing)
                {
                    return ApiResponse<EvaluateStoreProductAutoPricingResultDto>.OK(result);
                }

                if (!result.HasValidPurchasePrice)
                {
                    return ApiResponse<EvaluateStoreProductAutoPricingResultDto>.OK(result);
                }

                var supplierCode = await ResolveStorePriceSupplierCodeAsync(entity);
                var strategy = await _autoPricingService.FindStrategyForPriceAsync(
                    entity.PurchasePrice!.Value,
                    supplierCode,
                    entity.StoreCode
                );
                var recalculatedRetailPrice = _autoPricingService.CalculateRetailPrice(
                    entity.PurchasePrice.Value,
                    strategy
                );

                result.RecalculatedRetailPrice = recalculatedRetailPrice;
                result.RecalculatedRetailPriceFormatted = FormatPrice(recalculatedRetailPrice);
                result.ShouldUpdate =
                    result.RecalculatedRetailPriceFormatted != result.CurrentRetailPriceFormatted;

                return ApiResponse<EvaluateStoreProductAutoPricingResultDto>.OK(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "评估自动价失败: {ProductCode}", request.ProductCode);
                return ApiResponse<EvaluateStoreProductAutoPricingResultDto>.Error(
                    $"评估自动价失败: {ex.Message}"
                );
            }
        }

        public async Task<ApiResponse<StoreProductTypeUpdateResultDto>> UpdateProductTypeAsync(
            string productCode,
            UpdateStoreProductTypeDto request,
            string updatedBy,
            List<string>? accessibleStoreCodes
        )
        {
            try
            {
                var normalizedProductCode = productCode?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedProductCode))
                {
                    return ApiResponse<StoreProductTypeUpdateResultDto>.Error("商品编码不能为空");
                }

                if (request.ProductType < 0 || request.ProductType > 2)
                {
                    return ApiResponse<StoreProductTypeUpdateResultDto>.Error("商品类型无效");
                }

                var scopedStoreCodes = ResolveScopedStoreCodes(request.StoreCode, accessibleStoreCodes);
                if (scopedStoreCodes != null && scopedStoreCodes.Count == 0)
                {
                    return ApiResponse<StoreProductTypeUpdateResultDto>.Error("当前账号或设备无权修改该分店商品");
                }

                var storePriceEntity = await QueryStorePriceAsync(normalizedProductCode, scopedStoreCodes);
                if (scopedStoreCodes != null && storePriceEntity == null)
                {
                    return ApiResponse<StoreProductTypeUpdateResultDto>.Error("当前账号或设备无权修改该分店商品");
                }

                var product = await _db.Queryable<Product>()
                    .Where(p => p.ProductCode == normalizedProductCode && !p.IsDeleted)
                    .FirstAsync();

                if (product == null)
                {
                    return ApiResponse<StoreProductTypeUpdateResultDto>.Error("商品不存在");
                }

                var domesticProduct = await _db.Queryable<DomesticProduct>()
                    .Where(dp => dp.ProductCode == normalizedProductCode && !dp.IsDeleted)
                    .FirstAsync();

                var now = DateTime.UtcNow;
                var actorName = _currentUserService.GetCurrentUsername();
                if (string.IsNullOrWhiteSpace(actorName))
                {
                    actorName = "System";
                }
                var actorUserGuid = _currentUserService.GetCurrentUserGuid();
                var isSystemActor = string.IsNullOrWhiteSpace(actorUserGuid)
                    && string.Equals(
                        actorName,
                        "System",
                        StringComparison.OrdinalIgnoreCase
                    );

                ProductHqSyncOperationStatusDto? hqSync = null;
                await _db.Ado.BeginTranAsync();
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        _db,
                        new[] { normalizedProductCode }
                    );
                    product = await _db.Queryable<Product>()
                        .Where(item => item.ProductCode == normalizedProductCode && !item.IsDeleted)
                        .FirstAsync();
                    if (product == null)
                    {
                        throw new StoreProductMaintenanceBusinessException("商品在等待业务锁期间已变化");
                    }
                    if (product.ProductType != request.ProductType
                        && await _db.Queryable<ProductSetCode>()
                            .Where(item => item.ProductCode == normalizedProductCode && !item.IsDeleted)
                            .AnyAsync())
                    {
                        throw new StoreProductMaintenanceBusinessException("商品已有多码/套装条码，不能直接切换商品类型");
                    }
                    domesticProduct = await _db.Queryable<DomesticProduct>()
                        .Where(item => item.ProductCode == normalizedProductCode && !item.IsDeleted)
                        .FirstAsync();

                    // 两张业务表和审计历史使用同一事务，避免历史缺失或类型不同步。
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        new[] { normalizedProductCode }
                    );

                    product.ProductType = request.ProductType;
                    product.UpdatedAt = now;
                    product.UpdatedBy = actorName;
                    await _db.Updateable(product)
                        .UpdateColumns(p => new { p.ProductType, p.UpdatedAt, p.UpdatedBy })
                        .ExecuteCommandAsync();

                    if (domesticProduct != null)
                    {
                        domesticProduct.ProductType = request.ProductType;
                        domesticProduct.UpdatedAt = now;
                        domesticProduct.UpdatedBy = actorName;
                        await _db.Updateable(domesticProduct)
                            .UpdateColumns(dp => new { dp.ProductType, dp.UpdatedAt, dp.UpdatedBy })
                            .ExecuteCommandAsync();
                    }

                    var afterSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        new[] { normalizedProductCode }
                    );
                    await _changeHistoryService.RecordChangesAsync(
                        beforeSnapshots,
                        afterSnapshots,
                        new WarehouseProductChangeHistoryContextDto
                        {
                            Action = "Update",
                            Source = "StoreProductMaintenance",
                            SourceReference = normalizedProductCode,
                            ActorUserGuid = string.IsNullOrWhiteSpace(actorUserGuid)
                                ? null
                                : actorUserGuid,
                            ActorName = actorName,
                            ActorType = isSystemActor ? "System" : "User",
                            OccurredAtUtc = now,
                        }
                    );
                    hqSync = await EnqueueHqProjectionAsync(
                        ProductMaintenanceHqOperationKinds.ProductTypeUpdated,
                        normalizedProductCode,
                        Array.Empty<string>(),
                        new[] { ProductMaintenanceHqFieldMasks.ProductType },
                        "react-store-product-maintenance.update-product-type",
                        updatedBy,
                        accessibleStoreCodes
                    );
                    await _db.Ado.CommitTranAsync();
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }

                return ApiResponse<StoreProductTypeUpdateResultDto>.OK(
                    new StoreProductTypeUpdateResultDto
                    {
                        ProductCode = normalizedProductCode,
                        ProductType = request.ProductType,
                        ProductTypeLabel = NormalizeProductTypeLabel(request.ProductType.ToString()),
                        HqSync = hqSync,
                    },
                    "保存成功"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新商品类型失败: {ProductCode}", productCode);
                // 与本服务其他写入口一致：成本锁繁忙返回 BUSY，由控制器转 409 友好提示。
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<StoreProductTypeUpdateResultDto>();
                return ApiResponse<StoreProductTypeUpdateResultDto>.Error(
                    ResolveSafeMutationFailureMessage(ex, "更新商品类型失败，请稍后重试")
                );
            }
        }

        public async Task<ApiResponse<StoreProductMultiCodeDto>> UpdateMultiCodeAsync(
            string uuid,
            UpdateStoreProductMultiCodeDto request,
            string updatedBy,
            List<string>? accessibleStoreCodes
        )
        {
            try
            {
                var entity = await _db.Queryable<StoreMultiCodeProduct>()
                    .Where(x => x.UUID == uuid && !x.IsDeleted)
                    .FirstAsync();

                if (entity == null)
                {
                    return ApiResponse<StoreProductMultiCodeDto>.Error("多码商品记录不存在");
                }

                if (!CanAccessStore(entity.StoreCode, accessibleStoreCodes))
                {
                    return ApiResponse<StoreProductMultiCodeDto>.Error("当前账号或设备无权修改该分店多码");
                }

                if (request.RetailPrice.HasValue && request.RetailPrice.Value < 0)
                {
                    return ApiResponse<StoreProductMultiCodeDto>.Error("零售价不能为负数");
                }

                var requestedBarcode = request.Barcode?.Trim();
                if (request.Barcode != null && string.IsNullOrWhiteSpace(requestedBarcode))
                {
                    return ApiResponse<StoreProductMultiCodeDto>.Error("条码不能为空");
                }
                if (requestedBarcode?.Length > 50)
                {
                    return ApiResponse<StoreProductMultiCodeDto>.Error("条码长度不能超过 50 个字符");
                }

                var expectedStoreCode = entity.StoreCode;
                var expectedProductCode = entity.ProductCode;
                var expectedMultiCodeProductCode = entity.MultiCodeProductCode;
                if (
                    string.IsNullOrWhiteSpace(expectedStoreCode)
                    || string.IsNullOrWhiteSpace(expectedProductCode)
                    || string.IsNullOrWhiteSpace(expectedMultiCodeProductCode)
                )
                {
                    return ApiResponse<StoreProductMultiCodeDto>.Error(
                        "多码商品记录缺少门店、商品或多码商品编码"
                    );
                }

                string? supplierCode = null;
                ProductHqSyncOperationStatusDto? hqSync = null;
                StoreProductMultiCodeDto? dto = null;
                await _db.Ado.BeginTranAsync();
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        _db,
                        new[] { expectedProductCode }
                    );
                    // 等锁后复读并核对完整关系身份，禁止拿旧商品锁修改已迁移到其他组的记录。
                    entity = await _db.Queryable<StoreMultiCodeProduct>()
                        .Where(x => x.UUID == uuid && !x.IsDeleted)
                        .FirstAsync();
                    if (
                        entity == null
                        || !string.Equals(
                            entity.StoreCode,
                            expectedStoreCode,
                            StringComparison.Ordinal
                        )
                        || !string.Equals(
                            entity.ProductCode,
                            expectedProductCode,
                            StringComparison.Ordinal
                        )
                        || !string.Equals(
                            entity.MultiCodeProductCode,
                            expectedMultiCodeProductCode,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        throw new StoreProductMaintenanceBusinessException(
                            "分店多码记录在等待业务锁期间已离开原门店商品关系"
                        );
                    }
                    if (!CanAccessStore(entity.StoreCode, accessibleStoreCodes))
                    {
                        throw new StoreProductMaintenanceBusinessException("当前账号或设备无权修改该分店多码");
                    }

                    var isDerivedCostRelation = await _db.Queryable<ProductSetCode>()
                        .Where(x =>
                            x.ProductCode == expectedProductCode
                            && x.SetProductCode == expectedMultiCodeProductCode
                            && (x.SetType == 1 || x.SetType == 2)
                        )
                        .AnyAsync();
                    if (
                        !isDerivedCostRelation
                        && request.PurchasePrice.HasValue
                        && request.PurchasePrice.Value < 0
                    )
                    {
                        throw new StoreProductMaintenanceBusinessException("进货价不能为负数");
                    }

                    // 只要业务键曾归属于 Type1/Type2，成本就不能由客户端决定；停用或软删除关系也不得借编辑入口改写旧成本。
                    if (!isDerivedCostRelation)
                    {
                        entity.PurchasePrice = request.PurchasePrice;
                    }
                    if (requestedBarcode != null)
                    {
                        // 旧数据可能没有 ProductSetCode，只能按当前门店多码 UUID 原子更新条码。
                        entity.MultiBarcode = requestedBarcode;
                    }
                    entity.MultiCodeRetailPrice = request.RetailPrice;
                    if (request.IsAutoPricing.HasValue)
                    {
                        entity.IsAutoPricing = request.IsAutoPricing.Value;
                    }
                    if (request.IsSpecialProduct.HasValue)
                    {
                        entity.IsSpecialProduct = request.IsSpecialProduct.Value;
                    }
                    if (request.IsActive.HasValue)
                    {
                        entity.IsActive = request.IsActive.Value;
                    }

                    supplierCode = await _db.Queryable<StoreRetailPrice>()
                        .Where(x =>
                            x.ProductCode == expectedProductCode
                            && x.StoreCode == expectedStoreCode
                            && !x.IsDeleted
                        )
                        .Select(x => x.SupplierCode)
                        .FirstAsync();
                    if (string.IsNullOrWhiteSpace(supplierCode))
                    {
                        supplierCode = await _db.Queryable<Product>()
                            .Where(p => p.ProductCode == expectedProductCode && !p.IsDeleted)
                            .Select(p => p.LocalSupplierCode)
                            .FirstAsync();
                    }

                    if (entity.IsAutoPricing && entity.PurchasePrice.HasValue)
                    {
                        var strategy = await _autoPricingService.FindStrategyForPriceAsync(
                            entity.PurchasePrice.Value,
                            supplierCode,
                            expectedStoreCode
                        );
                        entity.MultiCodeRetailPrice = _autoPricingService.CalculateRetailPrice(
                            entity.PurchasePrice.Value,
                            strategy
                        );
                    }

                    entity.UpdatedAt = DateTime.UtcNow;
                    entity.UpdatedBy = updatedBy;
                    var affectedRows = await _db.Updateable(entity)
                        .UpdateColumns(x => new
                        {
                            x.MultiBarcode,
                            x.PurchasePrice,
                            x.MultiCodeRetailPrice,
                            x.IsAutoPricing,
                            x.IsSpecialProduct,
                            x.IsActive,
                            x.UpdatedAt,
                            x.UpdatedBy,
                        })
                        .Where(x =>
                            x.UUID == uuid
                            && !x.IsDeleted
                            && x.StoreCode == expectedStoreCode
                            && x.ProductCode == expectedProductCode
                            && x.MultiCodeProductCode == expectedMultiCodeProductCode
                        )
                        .ExecuteCommandAsync();
                    if (affectedRows != 1)
                    {
                        throw new StoreProductMaintenanceBusinessException("分店多码记录在事务内已变化");
                    }

                    // 普通多码更新也必须验证同一门店商品组，任何无法重算都会由当前事务整体回滚。
                    var writeback = await new SetChildPurchasePriceService(_db)
                        .RecalculateStoreGroupsLockedAsync(
                            lockScope,
                            new (string? StoreCode, string? ProductCode)[]
                            {
                                (expectedStoreCode, expectedProductCode),
                            },
                            updatedBy
                        );
                    EnsureNoSkippedStoreSetGroups(writeback);
                    entity = await _db.Queryable<StoreMultiCodeProduct>()
                        .Where(x =>
                            x.UUID == uuid
                            && !x.IsDeleted
                            && x.StoreCode == expectedStoreCode
                            && x.ProductCode == expectedProductCode
                            && x.MultiCodeProductCode == expectedMultiCodeProductCode
                        )
                        .FirstAsync();
                    if (entity == null)
                    {
                        throw new StoreProductMaintenanceBusinessException("分店多码记录在重算后已变化");
                    }
                    hqSync = await EnqueueHqProjectionAsync(
                        ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
                        expectedProductCode,
                        new[] { expectedStoreCode },
                        new[] { ProductMaintenanceHqFieldMasks.StoreMultiCodes },
                        "react-store-product-maintenance.update-multi-code",
                        updatedBy,
                        accessibleStoreCodes
                    );
                    dto = await BuildLegacyMultiCodeDtoAsync(entity, supplierCode);
                    dto.HqSync = hqSync;
                    await _db.Ado.CommitTranAsync();
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }

                return ApiResponse<StoreProductMultiCodeDto>.OK(dto!, "保存成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新分店多码失败: {Uuid}", uuid);
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<StoreProductMultiCodeDto>();
                return ApiResponse<StoreProductMultiCodeDto>.Error(
                    ResolveSafeMutationFailureMessage(ex, "更新分店多码失败，请稍后重试")
                );
            }
        }

        public async Task<ApiResponse<StoreProductSetCodeDto>> CreateSetCodeAsync(
            CreateStoreProductSetCodeDto request,
            string updatedBy,
            List<string>? accessibleStoreCodes
        )
        {
            try
            {
                if (!CanAccessStore(request.StoreCode, accessibleStoreCodes))
                {
                    return ApiResponse<StoreProductSetCodeDto>.Error("当前账号或设备无权修改该分店商品");
                }

                if (request.ProductType is < 1 or > 2)
                {
                    return ApiResponse<StoreProductSetCodeDto>.Error("商品类型无效");
                }

                if (string.IsNullOrWhiteSpace(request.ProductCode) || string.IsNullOrWhiteSpace(request.Barcode))
                {
                    return ApiResponse<StoreProductSetCodeDto>.Error("商品编码和条码不能为空");
                }

                if (request.ProductType == 1 && (!request.RetailPrice.HasValue || request.RetailPrice.Value <= 0))
                {
                    return ApiResponse<StoreProductSetCodeDto>.Error("套装零售价必须大于 0");
                }

                var normalizedProductCode = request.ProductCode.Trim();
                Product? product = null;
                ProductSetCode? setCode = null;
                ProductHqSyncOperationStatusDto? hqSync = null;
                StoreProductSetCodeDto? refreshed = null;
                await _db.Ado.BeginTranAsync();
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        _db,
                        new[] { normalizedProductCode }
                    );
                    // 父商品、兄弟编号和所有派生标识都必须在产品业务锁内读取/生成，避免并发创建冲突。
                    product = await _db.Queryable<Product>()
                        .Where(p => p.ProductCode == normalizedProductCode && !p.IsDeleted)
                        .FirstAsync();
                    if (product == null)
                    {
                        await _db.Ado.RollbackTranAsync();
                        return ApiResponse<StoreProductSetCodeDto>.Error("商品不存在");
                    }

                    var existingSetNumbers = await _db.Queryable<ProductSetCode>()
                        .Where(x => x.ProductCode == normalizedProductCode && !x.IsDeleted)
                        .Select(x => x.SetItemNumber)
                        .ToListAsync();
                    var lockedMainPrice = await QueryStorePriceByStoreAsync(
                        normalizedProductCode,
                        request.StoreCode
                    );
                    if (accessibleStoreCodes != null && lockedMainPrice == null)
                    {
                        throw new StoreProductMaintenanceBusinessException("当前账号或设备无权修改该分店商品");
                    }
                    var now = DateTime.UtcNow;
                    setCode = new ProductSetCode
                    {
                        SetCodeId = UuidHelper.GenerateUuid7(),
                        ProductCode = normalizedProductCode,
                        SetProductCode = UuidHelper.GenerateUuid7(),
                        SetItemNumber = ItemNumberHelper.GenerateSetItemNumber(
                            product.ItemNumber ?? normalizedProductCode,
                            existingSetNumbers
                        ),
                        SetBarcode = request.Barcode.Trim(),
                        // Type1 成本恒为空；Type2 仅使用锁内读取的门店主商品成本。
                        SetPurchasePrice = request.ProductType == 1
                            ? null
                            : lockedMainPrice?.PurchasePrice,
                        SetRetailPrice = request.ProductType == 1
                            ? request.RetailPrice
                            : lockedMainPrice?.StoreRetailPriceValue,
                        SetQuantity = 1,
                        SetType = request.ProductType,
                        IsActive = request.IsActive ?? true,
                        CreatedAt = now,
                        UpdatedAt = now,
                        CreatedBy = updatedBy,
                        UpdatedBy = updatedBy,
                        IsDeleted = false,
                    };
                    await _db.Insertable(setCode).ExecuteCommandAsync();
                    await SyncSetCodeAcrossStoresAsync(setCode, updatedBy, lockScope);
                    var hqStoreCodes = await QueryProductProjectionStoreCodesAsync(
                        normalizedProductCode
                    );
                    hqSync = await EnqueueHqProjectionAsync(
                        ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
                        normalizedProductCode,
                        hqStoreCodes,
                        BuildSetCodeHqFieldMask(hqStoreCodes),
                        "react-store-product-maintenance.create-set-code",
                        updatedBy,
                        accessibleStoreCodes
                    );
                    refreshed = await QueryProjectedSetCodeAsync(
                        setCode.SetCodeId,
                        request.StoreCode,
                        product.LocalSupplierCode
                    );
                    refreshed.HqSync = hqSync;
                    await _db.Ado.CommitTranAsync();
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }

                return ApiResponse<StoreProductSetCodeDto>.OK(refreshed!, "保存成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "新增条码失败: {ProductCode}", request.ProductCode);
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<StoreProductSetCodeDto>();
                return ApiResponse<StoreProductSetCodeDto>.Error(
                    ResolveSafeMutationFailureMessage(ex, "新增条码失败，请稍后重试")
                );
            }
        }

        public async Task<ApiResponse<StoreProductSetCodeDto>> UpdateSetCodeAsync(
            string setCodeId,
            UpdateStoreProductSetCodeDto request,
            string updatedBy,
            List<string>? accessibleStoreCodes
        )
        {
            try
            {
                if (!CanAccessStore(request.StoreCode, accessibleStoreCodes))
                {
                    return ApiResponse<StoreProductSetCodeDto>.Error("当前账号或设备无权修改该分店商品");
                }

                var setCode = await _db.Queryable<ProductSetCode>()
                    .Where(x => x.SetCodeId == setCodeId && !x.IsDeleted)
                    .FirstAsync();
                if (setCode == null)
                {
                    return ApiResponse<StoreProductSetCodeDto>.Error("条码记录不存在");
                }

                if (setCode.SetType == 1 && (!request.RetailPrice.HasValue || request.RetailPrice.Value <= 0))
                {
                    return ApiResponse<StoreProductSetCodeDto>.Error("套装零售价必须大于 0");
                }

                var mainStorePrice = await QueryStorePriceByStoreAsync(setCode.ProductCode, request.StoreCode);
                if (accessibleStoreCodes != null && mainStorePrice == null)
                {
                    return ApiResponse<StoreProductSetCodeDto>.Error("当前账号或设备无权修改该分店商品");
                }
                setCode.SetBarcode = request.Barcode?.Trim();
                setCode.SetRetailPrice = setCode.SetType == 2
                    ? mainStorePrice?.StoreRetailPriceValue
                    : request.RetailPrice;
                if (setCode.SetType == 2)
                {
                    setCode.SetPurchasePrice = mainStorePrice?.PurchasePrice;
                }
                if (request.IsActive.HasValue)
                {
                    setCode.IsActive = request.IsActive.Value;
                }

                setCode.UpdatedAt = DateTime.UtcNow;
                setCode.UpdatedBy = updatedBy;
                ProductHqSyncOperationStatusDto? hqSync = null;
                StoreProductSetCodeDto? refreshed = null;
                await _db.Ado.BeginTranAsync();
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        _db,
                        new[] { setCode.ProductCode }
                    );
                    var pendingBarcode = setCode.SetBarcode;
                    var pendingRetailPrice = setCode.SetRetailPrice;
                    var pendingIsActive = setCode.IsActive;
                    setCode = await _db.Queryable<ProductSetCode>()
                        .Where(x => x.SetCodeId == setCodeId && !x.IsDeleted)
                        .FirstAsync();
                    if (setCode == null)
                    {
                        throw new StoreProductMaintenanceBusinessException("条码记录在等待业务锁期间已变化");
                    }
                    var lockedMainStorePrice = await QueryStorePriceByStoreAsync(
                        setCode.ProductCode,
                        request.StoreCode
                    );
                    if (accessibleStoreCodes != null && lockedMainStorePrice == null)
                    {
                        throw new StoreProductMaintenanceBusinessException("当前账号或设备无权修改该分店商品");
                    }
                    setCode.SetBarcode = pendingBarcode;
                    setCode.SetRetailPrice = setCode.SetType == 2
                        ? lockedMainStorePrice?.StoreRetailPriceValue
                        : pendingRetailPrice;
                    if (setCode.SetType == 2)
                    {
                        setCode.SetPurchasePrice = lockedMainStorePrice?.PurchasePrice;
                    }
                    setCode.IsActive = pendingIsActive;
                    setCode.UpdatedAt = DateTime.UtcNow;
                    setCode.UpdatedBy = updatedBy;
                    await _db.Updateable(setCode)
                        .UpdateColumns(x => new
                        {
                            x.SetBarcode,
                            x.SetPurchasePrice,
                            x.SetRetailPrice,
                            x.IsActive,
                            x.UpdatedAt,
                            x.UpdatedBy,
                        })
                        .ExecuteCommandAsync();
                    await SyncSetCodeAcrossStoresAsync(setCode, updatedBy, lockScope);
                    var hqStoreCodes = await QueryProductProjectionStoreCodesAsync(
                        setCode.ProductCode
                    );
                    hqSync = await EnqueueHqProjectionAsync(
                        ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
                        setCode.ProductCode,
                        hqStoreCodes,
                        BuildSetCodeHqFieldMask(hqStoreCodes),
                        "react-store-product-maintenance.update-set-code",
                        updatedBy,
                        accessibleStoreCodes
                    );
                    var supplierCode = await _db.Queryable<Product>()
                        .Where(p => p.ProductCode == setCode.ProductCode && !p.IsDeleted)
                        .Select(p => p.LocalSupplierCode)
                        .FirstAsync();
                    refreshed = await QueryProjectedSetCodeAsync(
                        setCode.SetCodeId,
                        request.StoreCode,
                        supplierCode
                    );
                    refreshed.HqSync = hqSync;
                    await _db.Ado.CommitTranAsync();
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }

                return ApiResponse<StoreProductSetCodeDto>.OK(refreshed!, "保存成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新条码失败: {SetCodeId}", setCodeId);
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<StoreProductSetCodeDto>();
                return ApiResponse<StoreProductSetCodeDto>.Error(
                    ResolveSafeMutationFailureMessage(ex, "更新条码失败，请稍后重试")
                );
            }
        }

        public async Task<ApiResponse<SaveStoreProductSetCodeSnapshotResultDto>> SaveSetCodeSnapshotAsync(
            SaveStoreProductSetCodeSnapshotDto request,
            string updatedBy,
            List<string>? accessibleStoreCodes
        )
        {
            request.ExpectedItems ??= new List<SaveStoreProductSetCodeSnapshotItemDto>();
            request.Items ??= new List<SaveStoreProductSetCodeSnapshotItemDto>();
            var productCode = request.ProductCode?.Trim();
            var storeCode = request.StoreCode?.Trim();
            if (string.IsNullOrWhiteSpace(productCode) || string.IsNullOrWhiteSpace(storeCode))
            {
                return ApiResponse<SaveStoreProductSetCodeSnapshotResultDto>.Error("商品编码和分店编码不能为空");
            }
            if (accessibleStoreCodes != null)
            {
                return ApiResponse<SaveStoreProductSetCodeSnapshotResultDto>.Error("仅管理员可以保存商品条码快照");
            }
            if (request.ProductType is < 1 or > 2 || request.ExpectedProductType is < 0 or > 2)
            {
                return ApiResponse<SaveStoreProductSetCodeSnapshotResultDto>.Error("商品类型无效");
            }
            if (request.ExpectedItems.Count > 10_000 || request.Items.Count > 10_000)
            {
                return ApiResponse<SaveStoreProductSetCodeSnapshotResultDto>.Error("条码数量超过上限");
            }

            var expectedIds = new HashSet<string>(StringComparer.Ordinal);
            if (request.ExpectedItems.Any(item =>
                string.IsNullOrWhiteSpace(item.SetCodeId)
                || !expectedIds.Add(item.SetCodeId.Trim())
                || item.SetType is < 1 or > 2))
            {
                return ApiResponse<SaveStoreProductSetCodeSnapshotResultDto>.Error("期望条码快照无效");
            }
            var targetIds = new HashSet<string>(StringComparer.Ordinal);
            var targetBarcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (request.Items.Any(item =>
                string.IsNullOrWhiteSpace(item.Barcode)
                || item.Barcode.Trim().Length > 50
                || !targetBarcodes.Add(item.Barcode.Trim())
                || (!string.IsNullOrWhiteSpace(item.SetCodeId) && !targetIds.Add(item.SetCodeId.Trim()))
                || item.SetType != request.ProductType
                || (request.ProductType == 1 && (!item.RetailPrice.HasValue || item.RetailPrice <= 0))))
            {
                return ApiResponse<SaveStoreProductSetCodeSnapshotResultDto>.Error("待保存条码快照无效");
            }

            try
            {
                await _db.Ado.BeginTranAsync();
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        _db,
                        new[] { productCode }
                    );
                    var product = await _db.Queryable<Product>()
                        .Where(item => item.ProductCode == productCode && !item.IsDeleted)
                        .FirstAsync();
                    var storeExists = await _db.Queryable<Store>()
                        .Where(item => item.StoreCode == storeCode && item.IsActive && !item.IsDeleted)
                        .AnyAsync();
                    if (product == null || !storeExists)
                    {
                        throw new InvalidOperationException(product == null ? "商品不存在" : "分店不存在或已停用");
                    }

                    var currentItems = await _db.Queryable<ProductSetCode>()
                        .Where(item => item.ProductCode == productCode && !item.IsDeleted)
                        .OrderBy(item => item.SetCodeId)
                        .ToListAsync();
                    if (product.ProductType != request.ExpectedProductType
                        || !SetCodeSnapshotMatches(currentItems, request.ExpectedItems))
                    {
                        await _db.Ado.RollbackTranAsync();
                        return ApiResponse<SaveStoreProductSetCodeSnapshotResultDto>.Error(
                            "商品条码已变化，请重新加载后重试",
                            SetCodeSnapshotConflictErrorCode
                        );
                    }
                    var beforeSetCodeAuditSnapshot = BuildSetCodeAuditSnapshot(currentItems);
                    if (product.ProductType != request.ProductType && currentItems.Count > 0)
                    {
                        throw new InvalidOperationException("商品已有多码/套装条码，不能直接切换商品类型");
                    }
                    if (request.Items.Any(item => !string.IsNullOrWhiteSpace(item.SetCodeId)
                        && currentItems.All(current => current.SetCodeId != item.SetCodeId.Trim())))
                    {
                        throw new InvalidOperationException("待保存条码包含不属于当前商品的记录");
                    }

                    var now = DateTime.UtcNow;
                    var actor = string.IsNullOrWhiteSpace(updatedBy) ? "System" : updatedBy.Trim();
                    var actorUserGuid = _currentUserService.GetCurrentUserGuid();
                    var isSystemActor = string.IsNullOrWhiteSpace(actorUserGuid)
                        && string.Equals(actor, "System", StringComparison.OrdinalIgnoreCase);
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(new[] { productCode });
                    product.ProductType = request.ProductType;
                    product.UpdatedAt = now;
                    product.UpdatedBy = actor;
                    await _db.Updateable(product)
                        .UpdateColumns(item => new { item.ProductType, item.UpdatedAt, item.UpdatedBy })
                        .ExecuteCommandAsync();

                    var domestic = await _db.Queryable<DomesticProduct>()
                        .Where(item => item.ProductCode == productCode && !item.IsDeleted)
                        .FirstAsync();
                    if (domestic != null)
                    {
                        domestic.ProductType = request.ProductType;
                        domestic.UpdatedAt = now;
                        domestic.UpdatedBy = actor;
                        await _db.Updateable(domestic)
                            .UpdateColumns(item => new { item.ProductType, item.UpdatedAt, item.UpdatedBy })
                            .ExecuteCommandAsync();
                    }

                    var currentById = currentItems.ToDictionary(item => item.SetCodeId, StringComparer.Ordinal);
                    var retainedIds = request.Items.Where(item => !string.IsNullOrWhiteSpace(item.SetCodeId))
                        .Select(item => item.SetCodeId!.Trim()).ToHashSet(StringComparer.Ordinal);
                    var removedItems = currentItems.Where(item => !retainedIds.Contains(item.SetCodeId)).ToList();
                    foreach (var removed in removedItems)
                    {
                        removed.IsDeleted = true;
                        removed.IsActive = false;
                        removed.UpdatedAt = now;
                        removed.UpdatedBy = actor;
                    }
                    if (removedItems.Count > 0)
                    {
                        foreach (var batch in removedItems.Chunk(100))
                        {
                            await _db.Updateable(batch.ToList())
                                .UpdateColumns(item => new { item.IsDeleted, item.IsActive, item.UpdatedAt, item.UpdatedBy })
                                .ExecuteCommandAsync();
                        }
                        var removedCodes = removedItems
                            .Select(item => ResolveSetProductCode(item.SetProductCode, item.SetCodeId))
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                        foreach (var batch in removedCodes.Chunk(500))
                        {
                            var childCodes = batch.ToList();
                            await _db.Deleteable<StoreMultiCodeProduct>()
                                .Where(item => item.ProductCode == productCode && item.MultiCodeProductCode != null
                                    && childCodes.Contains(item.MultiCodeProductCode) && !item.IsDeleted)
                                .ExecuteCommandAsync();
                        }
                    }

                    var existingSetNumbers = currentItems.Select(item => item.SetItemNumber).ToList();
                    var mainStorePrice = request.ProductType == 2
                        ? await QueryStorePriceByStoreAsync(productCode, storeCode)
                        : null;
                    var targets = new List<ProductSetCode>();
                    var updates = new List<ProductSetCode>();
                    var inserts = new List<ProductSetCode>();
                    foreach (var item in request.Items)
                    {
                        var entity = !string.IsNullOrWhiteSpace(item.SetCodeId)
                            ? currentById[item.SetCodeId.Trim()]
                            : new ProductSetCode
                            {
                                SetCodeId = UuidHelper.GenerateUuid7(),
                                ProductCode = productCode,
                                SetProductCode = UuidHelper.GenerateUuid7(),
                                SetItemNumber = ItemNumberHelper.GenerateSetItemNumber(product.ItemNumber ?? productCode, existingSetNumbers),
                                SetQuantity = 1,
                                CreatedAt = now,
                                CreatedBy = actor,
                            };
                        if (string.IsNullOrWhiteSpace(item.SetCodeId))
                        {
                            existingSetNumbers.Add(entity.SetItemNumber);
                            inserts.Add(entity);
                        }
                        else
                        {
                            updates.Add(entity);
                        }
                        entity.SetBarcode = item.Barcode.Trim();
                        entity.SetType = request.ProductType;
                        entity.SetPurchasePrice = request.ProductType == 1 ? null : mainStorePrice?.PurchasePrice;
                        entity.SetRetailPrice = request.ProductType == 1 ? item.RetailPrice : mainStorePrice?.StoreRetailPriceValue;
                        entity.IsActive = item.IsActive;
                        entity.IsDeleted = false;
                        entity.UpdatedAt = now;
                        entity.UpdatedBy = actor;
                        targets.Add(entity);
                    }
                    if (updates.Count > 0)
                    {
                        foreach (var batch in updates.Chunk(100))
                        {
                            await _db.Updateable(batch.ToList()).UpdateColumns(item => new {
                                item.SetBarcode, item.SetPurchasePrice, item.SetRetailPrice, item.SetType,
                                item.IsActive, item.UpdatedAt, item.UpdatedBy
                            }).ExecuteCommandAsync();
                        }
                    }
                    if (inserts.Count > 0)
                    {
                        foreach (var batch in inserts.Chunk(100))
                        {
                            await _db.Insertable(batch.ToList()).ExecuteCommandAsync();
                        }
                    }
                    await SyncSetCodesAcrossStoresAsync(targets, actor, preserveExistingProjectionMetadata: true);
                    if (targets.Count > 0 || removedItems.Count > 0)
                    {
                        var writeback = await new SetChildPurchasePriceService(_db).RecalculateLockedAsync(
                            lockScope, new[] { productCode }, storeCodes: null, updatedBy: actor
                        );
                        EnsureNoSkippedStoreSetGroups(writeback);
                    }
                    var afterSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(new[] { productCode });
                    await _changeHistoryService.RecordChangesAsync(
                        beforeSnapshots, afterSnapshots,
                        new WarehouseProductChangeHistoryContextDto
                        {
                            Action = "Update", Source = "StoreProductMaintenanceSetCodeSnapshot",
                            SourceReference = productCode, ActorName = actor,
                            ActorUserGuid = string.IsNullOrWhiteSpace(actorUserGuid) ? null : actorUserGuid,
                            ActorType = isSystemActor ? "System" : "User", OccurredAtUtc = now,
                        }
                    );
                    var persisted = await _db.Queryable<ProductSetCode>()
                        .Where(item => item.ProductCode == productCode && !item.IsDeleted)
                        .OrderBy(item => item.SetCodeId).ToListAsync();
                    await RecordSetCodeSnapshotAuditAsync(
                        productCode, storeCode, beforeSetCodeAuditSnapshot,
                        BuildSetCodeAuditSnapshot(persisted), actorUserGuid, actor, isSystemActor, now
                    );
                    var projections = await QueryProjectedStoreMultiCodesAsync(persisted, storeCode);
                    var projectionMap = projections.ToDictionary(
                        item => ResolveSetProductCode(item.MultiCodeProductCode, item.UUID), item => item, StringComparer.Ordinal
                    );
                    var hqStoreCodes = await QueryProductProjectionStoreCodesAsync(productCode);
                    var hqFields = BuildSetCodeHqFieldMask(hqStoreCodes).ToList();
                    hqFields.Insert(0, ProductMaintenanceHqFieldMasks.ProductType);
                    var tombstones = removedItems
                        .Select(item => new ProductHqSyncOutboxTombstoneDto(
                            ProductMaintenanceHqResourceKinds.ProductSetCode,
                            null,
                            ResolveSetProductCode(item.SetProductCode, item.SetCodeId)
                        ))
                        .ToList();
                    var hqSync = await EnqueueHqProjectionAsync(
                        ProductMaintenanceHqOperationKinds.SetCodeSnapshot,
                        productCode,
                        hqStoreCodes,
                        hqFields,
                        "react-store-product-maintenance.save-set-code-snapshot",
                        actor,
                        accessibleStoreCodes,
                        tombstones
                    );
                    await _db.Ado.CommitTranAsync();
                    return ApiResponse<SaveStoreProductSetCodeSnapshotResultDto>.OK(new()
                    {
                        ProductCode = productCode, StoreCode = storeCode, ProductType = request.ProductType,
                        Items = persisted.Select(item =>
                        {
                            projectionMap.TryGetValue(ResolveSetProductCode(item.SetProductCode, item.SetCodeId), out var projection);
                            return BuildSetCodeDto(item, projection);
                        }).ToList(),
                        HqSync = hqSync,
                    }, "保存成功");
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存商品条码快照失败: {ProductCode}", productCode);
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                {
                    return BuildSetChildPurchasePriceBusyResponse<SaveStoreProductSetCodeSnapshotResultDto>();
                }
                return ApiResponse<SaveStoreProductSetCodeSnapshotResultDto>.Error("保存商品条码快照失败，请稍后重试");
            }
        }

        public async Task<ApiResponse<DeleteStoreProductSetCodeResultDto>> DeleteSetCodeAsync(
            string setCodeId,
            string updatedBy,
            List<string>? accessibleStoreCodes
        )
        {
            try
            {
                var setCode = await _db.Queryable<ProductSetCode>()
                    .Where(x => x.SetCodeId == setCodeId && !x.IsDeleted)
                    .FirstAsync();
                if (setCode == null)
                {
                    return ApiResponse<DeleteStoreProductSetCodeResultDto>.Error("条码记录不存在");
                }

                if (accessibleStoreCodes != null && accessibleStoreCodes.Count == 0)
                {
                    return ApiResponse<DeleteStoreProductSetCodeResultDto>.Error("当前账号或设备无权修改该分店商品");
                }
                if (
                    accessibleStoreCodes != null
                    && await QueryStorePriceAsync(setCode.ProductCode, accessibleStoreCodes) == null
                )
                {
                    return ApiResponse<DeleteStoreProductSetCodeResultDto>.Error("当前账号或设备无权修改该分店商品");
                }

                setCode.IsDeleted = true;
                setCode.IsActive = false;
                setCode.UpdatedAt = DateTime.UtcNow;
                setCode.UpdatedBy = updatedBy;
                ProductHqSyncOperationStatusDto? hqSync = null;
                await _db.Ado.BeginTranAsync();
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        _db,
                        new[] { setCode.ProductCode }
                    );
                    setCode = await _db.Queryable<ProductSetCode>()
                        .Where(x => x.SetCodeId == setCodeId && !x.IsDeleted)
                        .FirstAsync();
                    if (setCode == null)
                    {
                        throw new StoreProductMaintenanceBusinessException("条码记录在等待业务锁期间已变化");
                    }
                    if (
                        accessibleStoreCodes != null
                        && await QueryStorePriceAsync(setCode.ProductCode, accessibleStoreCodes) == null
                    )
                    {
                        throw new StoreProductMaintenanceBusinessException("当前账号或设备无权修改该分店商品");
                    }
                    setCode.IsDeleted = true;
                    setCode.IsActive = false;
                    setCode.UpdatedAt = DateTime.UtcNow;
                    setCode.UpdatedBy = updatedBy;
                    await _db.Updateable(setCode)
                        .UpdateColumns(x => new { x.IsDeleted, x.IsActive, x.UpdatedAt, x.UpdatedBy })
                        .ExecuteCommandAsync();

                    var setProductCode = ResolveSetProductCode(setCode.SetProductCode, setCode.SetCodeId);
                    await _db.Deleteable<StoreMultiCodeProduct>()
                        .Where(x =>
                            x.ProductCode == setCode.ProductCode
                            && x.MultiCodeProductCode == setProductCode
                            && !x.IsDeleted
                        )
                        .ExecuteCommandAsync();

                    if (setCode.SetType == 1 || setCode.SetType == 2)
                    {
                        var writeback = await new SetChildPurchasePriceService(_db)
                            .RecalculateLockedAsync(
                                lockScope,
                                new[] { setCode.ProductCode },
                                storeCodes: null,
                                updatedBy: updatedBy
                            );
                        EnsureNoSkippedStoreSetGroups(writeback);
                    }
                    hqSync = await EnqueueHqProjectionAsync(
                        ProductMaintenanceHqOperationKinds.ProductCodesDeleted,
                        setCode.ProductCode,
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        "react-store-product-maintenance.delete-set-code",
                        updatedBy,
                        accessibleStoreCodes,
                        new[]
                        {
                            new ProductHqSyncOutboxTombstoneDto(
                                ProductMaintenanceHqResourceKinds.ProductSetCode,
                                null,
                                setProductCode
                            ),
                        }
                    );
                    await _db.Ado.CommitTranAsync();
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }

                return ApiResponse<DeleteStoreProductSetCodeResultDto>.OK(
                    new DeleteStoreProductSetCodeResultDto
                    {
                        Deleted = true,
                        HqSync = hqSync,
                    },
                    "删除成功"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除条码失败: {SetCodeId}", setCodeId);
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<DeleteStoreProductSetCodeResultDto>();
                return ApiResponse<DeleteStoreProductSetCodeResultDto>.Error(
                    ResolveSafeMutationFailureMessage(ex, "删除条码失败，请稍后重试")
                );
            }
        }

        public async Task<ApiResponse<StoreProductClearancePriceDto>> UpsertClearancePriceAsync(
            string productCode,
            UpsertStoreProductClearancePriceDto request,
            string updatedBy,
            List<string>? accessibleStoreCodes
        )
        {
            var operation = "update";
            try
            {
                if (!CanAccessStore(request.StoreCode, accessibleStoreCodes))
                {
                    return ApiResponse<StoreProductClearancePriceDto>.Error("当前账号或设备无权修改该分店商品");
                }

                await _db.Ado.BeginTranAsync();
                try
                {
                    await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        _db,
                        new[] { productCode }
                    );
                    if (
                        accessibleStoreCodes != null
                        && await QueryStorePriceByStoreAsync(productCode, request.StoreCode) == null
                    )
                    {
                        await _db.Ado.RollbackTranAsync();
                        return ApiResponse<StoreProductClearancePriceDto>.Error(
                            "当前账号或设备无权修改该分店商品"
                        );
                    }
                    var product = await _db.Queryable<Product>()
                        .Where(p => p.ProductCode == productCode && !p.IsDeleted)
                        .FirstAsync();
                    if (product == null)
                    {
                        await _db.Ado.RollbackTranAsync();
                        return ApiResponse<StoreProductClearancePriceDto>.Error("商品不存在");
                    }

                    var entities = await _db.Queryable<StoreClearancePrice>()
                        .Where(x =>
                            x.ProductCode == productCode
                            && x.StoreCode == request.StoreCode
                            && !x.IsDeleted
                        )
                        .OrderBy(x => x.CreatedAt)
                        .OrderBy(x => x.UUID)
                        .ToListAsync();
                    var entity = entities.FirstOrDefault();
                    ProductHqSyncOperationStatusDto hqSync;
                    if (!request.ClearancePrice.HasValue)
                    {
                        operation = "delete";
                        entity ??= new StoreClearancePrice
                        {
                            UUID = string.Empty,
                            StoreCode = request.StoreCode,
                            ProductCode = productCode,
                        };
                        if (entities.Count > 0)
                        {
                            await _db.Deleteable<StoreClearancePrice>()
                                .Where(item =>
                                    item.ProductCode == productCode
                                    && item.StoreCode == request.StoreCode
                                    && !item.IsDeleted
                                )
                                .ExecuteCommandAsync();
                        }
                        entity.ClearancePrice = null;
                        hqSync = await EnqueueHqProjectionAsync(
                            ProductMaintenanceHqOperationKinds.ClearancePriceDeleted,
                            productCode,
                            new[] { request.StoreCode.Trim() },
                            new[] { ProductMaintenanceHqFieldMasks.StoreClearancePrice },
                            "react-store-product-maintenance.delete-clearance-price",
                            updatedBy,
                            accessibleStoreCodes,
                            new[]
                            {
                                new ProductHqSyncOutboxTombstoneDto(
                                    ProductMaintenanceHqResourceKinds.StoreClearancePrice,
                                    request.StoreCode.Trim(),
                                    entity.ClearanceBarcode ?? productCode
                                ),
                            }
                        );
                    }
                    else
                    {
                        if (entity == null)
                        {
                            operation = "create";
                            entity = new StoreClearancePrice
                            {
                                UUID = UuidHelper.GenerateUuid7(),
                                StoreCode = request.StoreCode,
                                ProductCode = productCode,
                                ClearanceBarcode = await GenerateClearanceBarcodeAsync(request.StoreCode),
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow,
                                CreatedBy = updatedBy,
                                UpdatedBy = updatedBy,
                                IsDeleted = false,
                                ClearancePrice = request.ClearancePrice,
                            };
                            await _db.Insertable(entity).ExecuteCommandAsync();
                        }
                        else
                        {
                            entity.ClearancePrice = request.ClearancePrice;
                            entity.UpdatedAt = DateTime.UtcNow;
                            entity.UpdatedBy = updatedBy;
                            await _db.Updateable(entity).ExecuteCommandAsync();
                            if (entities.Count > 1)
                            {
                                await _db.Deleteable<StoreClearancePrice>()
                                    .Where(item =>
                                        item.ProductCode == productCode
                                        && item.StoreCode == request.StoreCode
                                        && !item.IsDeleted
                                        && item.UUID != entity.UUID
                                    )
                                    .ExecuteCommandAsync();
                            }
                        }
                        hqSync = await EnqueueHqProjectionAsync(
                            ProductMaintenanceHqOperationKinds.ClearancePriceUpdated,
                            productCode,
                            new[] { request.StoreCode.Trim() },
                            new[] { ProductMaintenanceHqFieldMasks.StoreClearancePrice },
                            "react-store-product-maintenance.upsert-clearance-price",
                            updatedBy,
                            accessibleStoreCodes
                        );
                    }

                    var result = await BuildClearancePriceDtoAsync(entity);
                    result.HqSync = hqSync;
                    await _db.Ado.CommitTranAsync();
                    _logger.LogInformation(
                        "StoreProductMaintenance clearance price saved productCode={ProductCode} storeCode={StoreCode} operation={Operation} clearanceBarcode={ClearanceBarcode} targetTable=StoreClearancePrice noMultiCodeProjection=true",
                        productCode,
                        request.StoreCode,
                        operation,
                        entity.ClearanceBarcode
                    );
                    return ApiResponse<StoreProductClearancePriceDto>.OK(result, "保存成功");
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "保存清货价失败: productCode={ProductCode} storeCode={StoreCode} operation={Operation}",
                    productCode,
                    request.StoreCode,
                    operation
                );
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                {
                    return BuildSetChildPurchasePriceBusyResponse<StoreProductClearancePriceDto>();
                }
                return ApiResponse<StoreProductClearancePriceDto>.Error("保存清货价失败，请稍后重试");
            }
        }

        private Task<ProductHqSyncOperationStatusDto> EnqueueHqProjectionAsync(
            string operationKind,
            string productCode,
            IReadOnlyCollection<string>? targetStoreCodes,
            IReadOnlyCollection<string> fieldMask,
            string source,
            string updatedBy,
            IReadOnlyCollection<string>? authorizedStoreCodes,
            IReadOnlyCollection<ProductHqSyncOutboxTombstoneDto>? tombstones = null
        )
        {
            var isDevice = updatedBy.StartsWith("device:", StringComparison.OrdinalIgnoreCase);
            return _hqProjectionWriter.EnqueueAsync(
                _db,
                new ProductMaintenanceHqMutationRequest
                {
                    OperationKind = operationKind,
                    ProductCode = productCode,
                    TargetStoreCodes = targetStoreCodes,
                    AuthorizedStoreCodes = authorizedStoreCodes,
                    FieldMask = fieldMask,
                    Tombstones = tombstones ?? Array.Empty<ProductHqSyncOutboxTombstoneDto>(),
                    RequestedByUserGuid = isDevice ? null : _currentUserService.GetCurrentUserGuid(),
                    RequestedByDeviceId = isDevice ? updatedBy["device:".Length..] : null,
                    Source = source,
                    OccurredAtUtc = DateTime.UtcNow,
                }
            );
        }

        private async Task<IReadOnlyCollection<string>> QueryProductProjectionStoreCodesAsync(
            string productCode
        )
        {
            var storeCodes = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(item =>
                    item.ProductCode == productCode
                    && item.StoreCode != null
                    && !item.IsDeleted
                )
                .Select(item => item.StoreCode)
                .ToListAsync();
            return storeCodes
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IReadOnlyCollection<string> BuildSetCodeHqFieldMask(
            IReadOnlyCollection<string> storeCodes
        ) => storeCodes.Count == 0
            ? new[] { ProductMaintenanceHqFieldMasks.ProductSetCodes }
            : new[]
            {
                ProductMaintenanceHqFieldMasks.ProductSetCodes,
                ProductMaintenanceHqFieldMasks.StoreMultiCodes,
            };

        private async Task<StoreRetailPrice?> QueryStorePriceAsync(
            string productCode,
            List<string>? accessibleStoreCodes
        )
        {
            var query = _db.Queryable<StoreRetailPrice>()
                .Where(x => x.ProductCode == productCode && !x.IsDeleted);
            if (accessibleStoreCodes != null)
            {
                query = query.Where(x => x.StoreCode != null && accessibleStoreCodes.Contains(x.StoreCode));
            }

            return await query.OrderBy(x => x.StoreCode).FirstAsync();
        }

        private async Task<ProductCodePageQueryResult> QueryProductCodesPageAsync(
            string productCode,
            string? storeCode,
            int? setType,
            int page,
            int pageSize,
            string? keyword,
            List<string>? accessibleStoreCodes
        )
        {
            var normalizedProductCode = productCode?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedProductCode))
            {
                return ProductCodePageQueryResult.Fail("商品编码不能为空");
            }

            var selectedStoreCodes = ResolveScopedStoreCodes(storeCode, accessibleStoreCodes);
            if (selectedStoreCodes != null && selectedStoreCodes.Count == 0)
            {
                return ProductCodePageQueryResult.Fail("当前账号或设备无权访问该分店");
            }

            var product = await _db.Queryable<Product>()
                .Where(p => p.ProductCode == normalizedProductCode && !p.IsDeleted)
                .Select(p => new { p.ProductCode, p.LocalSupplierCode })
                .FirstAsync();
            if (product == null)
            {
                return ProductCodePageQueryResult.Fail("商品不存在");
            }

            var storePriceEntity = await QueryStorePriceAsync(normalizedProductCode, selectedStoreCodes);
            var currentStoreCode = ResolveCurrentStoreCode(storeCode, selectedStoreCodes, storePriceEntity);
            var pageIndex = Math.Max(1, page);
            var normalizedPageSize = Math.Clamp(pageSize, 1, 100);
            RefAsync<int> totalCount = 0;
            var setCodes = await BuildProductSetCodeQuery(normalizedProductCode, setType, keyword)
                .OrderBy(s => s.SetBarcode)
                .OrderBy(s => s.SetCodeId)
                .Select(s => new ProductSetCode
                {
                    SetCodeId = s.SetCodeId,
                    ProductCode = s.ProductCode,
                    SetProductCode = s.SetProductCode,
                    SetItemNumber = s.SetItemNumber,
                    SetBarcode = s.SetBarcode,
                    SetPurchasePrice = s.SetPurchasePrice,
                    SetRetailPrice = s.SetRetailPrice,
                    SetQuantity = s.SetQuantity,
                    SetType = s.SetType,
                    IsActive = s.IsActive,
                })
                .ToPageListAsync(pageIndex, normalizedPageSize, totalCount);

            return new ProductCodePageQueryResult
            {
                SetCodes = setCodes,
                TotalCount = totalCount,
                Page = pageIndex,
                PageSize = normalizedPageSize,
                StoreCode = currentStoreCode,
                LocalSupplierCode = product.LocalSupplierCode,
            };
        }

        private async Task<ProductCodePageQueryResult> QueryMultiCodePageAsync(
            string productCode,
            string? storeCode,
            int page,
            int pageSize,
            string? keyword,
            List<string>? accessibleStoreCodes
        )
        {
            var normalizedProductCode = productCode?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedProductCode))
            {
                return ProductCodePageQueryResult.Fail("商品编码不能为空");
            }

            var selectedStoreCodes = ResolveScopedStoreCodes(storeCode, accessibleStoreCodes);
            if (selectedStoreCodes != null && selectedStoreCodes.Count == 0)
            {
                return ProductCodePageQueryResult.Fail("当前账号或设备无权访问该分店");
            }

            var product = await _db.Queryable<Product>()
                .Where(p => p.ProductCode == normalizedProductCode && !p.IsDeleted)
                .Select(p => new { p.ProductCode, p.LocalSupplierCode })
                .FirstAsync();
            if (product == null)
            {
                return ProductCodePageQueryResult.Fail("商品不存在");
            }

            var storePriceEntity = await QueryStorePriceAsync(normalizedProductCode, selectedStoreCodes);
            var currentStoreCode = ResolveCurrentStoreCode(storeCode, selectedStoreCodes, storePriceEntity);
            if (string.IsNullOrWhiteSpace(currentStoreCode))
            {
                return ProductCodePageQueryResult.Fail("当前商品没有可用的分店上下文");
            }

            var pageIndex = Math.Max(1, page);
            var normalizedPageSize = Math.Clamp(pageSize, 1, 100);
            RefAsync<int> totalCount = 0;
            var setCodes = await BuildProductSetCodeQuery(normalizedProductCode, 2, keyword)
                .OrderBy(s => s.SetBarcode)
                .OrderBy(s => s.SetCodeId)
                .Select(s => new ProductSetCode
                {
                    SetCodeId = s.SetCodeId,
                    ProductCode = s.ProductCode,
                    SetProductCode = s.SetProductCode,
                    SetItemNumber = s.SetItemNumber,
                    SetBarcode = s.SetBarcode,
                    SetPurchasePrice = s.SetPurchasePrice,
                    SetRetailPrice = s.SetRetailPrice,
                    SetQuantity = s.SetQuantity,
                    SetType = s.SetType,
                    IsActive = s.IsActive,
                })
                .ToPageListAsync(pageIndex, normalizedPageSize, totalCount);

            return new ProductCodePageQueryResult
            {
                SetCodes = setCodes,
                TotalCount = totalCount,
                Page = pageIndex,
                PageSize = normalizedPageSize,
                StoreCode = currentStoreCode,
                LocalSupplierCode = product.LocalSupplierCode,
            };
        }

        private ISugarQueryable<ProductSetCode> BuildProductSetCodeQuery(
            string productCode,
            int? setType,
            string? keyword
        )
        {
            var query = _db.Queryable<ProductSetCode>()
                .Where(s => s.ProductCode == productCode && !s.IsDeleted);
            if (setType.HasValue)
            {
                // SqlSugar 无法可靠翻译可空参数的 Value；先固化为局部值以保证类型过滤进入 SQL。
                var targetSetType = setType.GetValueOrDefault();
                query = query.Where(s => s.SetType == targetSetType);
            }

            var normalizedKeyword = keyword?.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedKeyword))
            {
                query = query.Where(s =>
                    (s.SetBarcode != null && s.SetBarcode.Contains(normalizedKeyword))
                    || (s.SetItemNumber != null && s.SetItemNumber.Contains(normalizedKeyword))
                );
            }

            return query;
        }

        private async Task<List<ProductSetCode>> QueryProductSetCodesAsync(
            string productCode,
            int? setType,
            string? keyword
        )
        {
            return await BuildProductSetCodeQuery(productCode, setType, keyword)
                .OrderBy(s => s.SetBarcode)
                .OrderBy(s => s.SetCodeId)
                .Select(s => new ProductSetCode
                {
                    SetCodeId = s.SetCodeId,
                    ProductCode = s.ProductCode,
                    SetProductCode = s.SetProductCode,
                    SetItemNumber = s.SetItemNumber,
                    SetBarcode = s.SetBarcode,
                    SetPurchasePrice = s.SetPurchasePrice,
                    SetRetailPrice = s.SetRetailPrice,
                    SetQuantity = s.SetQuantity,
                    SetType = s.SetType,
                    IsActive = s.IsActive,
                })
                .ToListAsync();
        }

        private async Task<ProductSetCodeCounts> QueryProductSetCodeCountsAsync(string productCode)
        {
            var rows = await _db.Ado.SqlQueryAsync<ProductSetCodeCounts>(
                """
                SELECT ISNULL(COUNT(1), 0) AS TotalSetCodeCount
                FROM [ProductSetCode]
                WHERE ProductCode = @ProductCode AND IsDeleted = 0
                """,
                new SugarParameter("@ProductCode", productCode)
            );

            return rows.FirstOrDefault() ?? new ProductSetCodeCounts();
        }

        private async Task<FastDetailBaseRow?> QueryFastDetailBaseAsync(
            string productCode,
            string? targetStoreCode
        )
        {
            var rows = await _db.Ado.SqlQueryAsync<FastDetailBaseRow>(
                """
                SELECT TOP 1
                    p.ProductCode AS ProductCode,
                    p.ProductName AS ProductName,
                    p.ItemNumber AS ItemNumber,
                    p.Barcode AS Barcode,
                    p.ProductImage AS ProductImage,
                    p.ProductType AS ProductType,
                    p.LocalSupplierCode AS LocalSupplierCode,
                    ls.Name AS LocalSupplierName,
                    pg.Grade AS Grade,
                    sp.UUID AS StorePriceUuid,
                    sp.StoreCode AS StorePriceStoreCode,
                    storePriceStore.StoreName AS StoreName,
                    sp.ProductCode AS StorePriceProductCode,
                    sp.StoreProductCode AS StoreProductCode,
                    sp.SupplierCode AS SupplierCode,
                    sp.PurchasePrice AS PurchasePrice,
                    sp.StoreRetailPriceValue AS StoreRetailPriceValue,
                    sp.DiscountRate AS DiscountRate,
                    sp.IsAutoPricing AS IsAutoPricing,
                    sp.IsSpecialProduct AS IsSpecialProduct,
                    sp.IsActive AS StorePriceIsActive,
                    cp.UUID AS ClearanceUuid,
                    cp.StoreCode AS ClearanceStoreCode,
                    clearanceStore.StoreName AS ClearanceStoreName,
                    cp.ProductCode AS ClearanceProductCode,
                    cp.ClearanceBarcode AS ClearanceBarcode,
                    cp.ClearancePrice AS ClearancePrice,
                    ISNULL(codeCounts.TotalSetCodeCount, 0) AS TotalSetCodeCount
                FROM [Product] p
                LEFT JOIN [ProductGrade] pg
                    ON pg.ProductCode = p.ProductCode AND ISNULL(pg.IsDeleted, 0) = 0
                LEFT JOIN [LocalSupplier] ls
                    ON ls.LocalSupplierCode = p.LocalSupplierCode AND ISNULL(ls.IsDeleted, 0) = 0
                OUTER APPLY (
                    SELECT COUNT(1) AS TotalSetCodeCount
                    FROM [ProductSetCode] psc
                    WHERE psc.ProductCode = p.ProductCode
                        AND ISNULL(psc.IsDeleted, 0) = 0
                ) codeCounts
                OUTER APPLY (
                    SELECT TOP 1 srp.*
                    FROM [StoreRetailPrice] srp
                    WHERE srp.ProductCode = p.ProductCode
                        AND ISNULL(srp.IsDeleted, 0) = 0
                        AND (@StoreCode IS NULL OR srp.StoreCode = @StoreCode)
                    ORDER BY srp.StoreCode
                ) sp
                LEFT JOIN [Store] storePriceStore
                    ON storePriceStore.StoreCode = sp.StoreCode
                    AND ISNULL(storePriceStore.IsDeleted, 0) = 0
                OUTER APPLY (
                    SELECT TOP 1 scp.*
                    FROM [StoreClearancePrice] scp
                    WHERE scp.ProductCode = p.ProductCode
                        AND ISNULL(scp.IsDeleted, 0) = 0
                        AND (@StoreCode IS NULL OR scp.StoreCode = @StoreCode)
                    ORDER BY scp.StoreCode
                ) cp
                LEFT JOIN [Store] clearanceStore
                    ON clearanceStore.StoreCode = cp.StoreCode
                    AND ISNULL(clearanceStore.IsDeleted, 0) = 0
                WHERE p.ProductCode = @ProductCode AND ISNULL(p.IsDeleted, 0) = 0
                """,
                new SugarParameter("@ProductCode", productCode),
                new SugarParameter("@StoreCode", (object?)targetStoreCode ?? DBNull.Value)
            );

            return rows.FirstOrDefault();
        }

        private static StoreProductCodePageDto<T> BuildCodePage<T>(
            List<T> items,
            int totalCount,
            int page,
            int pageSize
        )
        {
            return new StoreProductCodePageDto<T>
            {
                Items = items,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize,
                HasMore = page * pageSize < totalCount,
            };
        }

        private async Task<List<LookupHit>> QueryLookupHitsAsync(
            string keyword,
            List<string>? selectedStoreCodes
        )
        {
            var parameters = new List<SugarParameter>
            {
                new("@Keyword", keyword),
            };
            var itemNumberPredicate = "p.ItemNumber = @Keyword";
            if (keyword.Any(character => character == ' ' || ItemNumberSpaceVariants.Contains(character)))
            {
                // 仅含空格的货号启用兼容查询，普通货号及所有条码继续走原来的精确匹配。
                var normalizedKeyword = keyword;
                var normalizedColumn = "p.ItemNumber";
                for (var i = 0; i < ItemNumberSpaceVariants.Length; i++)
                {
                    var space = ItemNumberSpaceVariants[i];
                    normalizedKeyword = normalizedKeyword.Replace(space, ' ');
                    var parameterName = "@ItemNumberSpace" + i;
                    parameters.Add(new SugarParameter(parameterName, space.ToString()));
                    normalizedColumn = $"REPLACE({normalizedColumn}, {parameterName}, ' ')";
                }

                // 保留无函数的 LIKE 前缀条件，让数据库有机会先通过货号索引缩小候选范围。
                // 空格只占一个字符；其余通配符必须转义，最终仍由统一空格后的相等比较判定。
                var candidatePattern = normalizedKeyword
                    .Replace("\\", "\\\\")
                    .Replace("%", "\\%")
                    .Replace("_", "\\_")
                    .Replace("[", "\\[")
                    .Replace(' ', '_');
                parameters.Add(new SugarParameter("@ItemNumberPattern", candidatePattern));
                parameters.Add(new SugarParameter("@NormalizedItemNumber", normalizedKeyword));
                itemNumberPredicate = $"(p.ItemNumber = @Keyword OR (p.ItemNumber LIKE @ItemNumberPattern ESCAPE '\\' AND {normalizedColumn} = @NormalizedItemNumber))";
            }
            var storeFilterSql = string.Empty;
            if (selectedStoreCodes != null)
            {
                var storeParameterNames = new List<string>();
                for (var i = 0; i < selectedStoreCodes.Count; i++)
                {
                    var parameterName = "@StoreCode" + i;
                    storeParameterNames.Add(parameterName);
                    parameters.Add(new SugarParameter(parameterName, selectedStoreCodes[i]));
                }

                storeFilterSql =
                    storeParameterNames.Count == 0
                        ? " AND 1 = 0"
                        : $" AND c.StoreCode IN ({string.Join(", ", storeParameterNames)})";
            }

            var sql = new StringBuilder();
            sql.AppendLine(
                $"""
                SELECT
                    p.ProductCode AS ProductCode,
                    p.ItemNumber AS ItemNumber,
                    p.Barcode AS Barcode,
                    'ProductBarcode' AS MatchSource,
                    @Keyword AS MatchValue
                FROM [Product] p
                WHERE p.IsDeleted = 0 AND p.Barcode = @Keyword

                UNION ALL

                SELECT
                    p.ProductCode AS ProductCode,
                    p.ItemNumber AS ItemNumber,
                    p.Barcode AS Barcode,
                    'ItemNumber' AS MatchSource,
                    @Keyword AS MatchValue
                FROM [Product] p
                WHERE p.IsDeleted = 0 AND {itemNumberPredicate}

                UNION ALL

                SELECT
                    s.ProductCode AS ProductCode,
                    s.SetItemNumber AS ItemNumber,
                    s.SetBarcode AS Barcode,
                    'SetBarcode' AS MatchSource,
                    @Keyword AS MatchValue
                FROM [ProductSetCode] s
                WHERE s.IsDeleted = 0 AND s.SetBarcode = @Keyword

                UNION ALL

                SELECT
                    c.ProductCode AS ProductCode,
                    CAST(NULL AS nvarchar(200)) AS ItemNumber,
                    c.ClearanceBarcode AS Barcode,
                    'ClearanceBarcode' AS MatchSource,
                    @Keyword AS MatchValue
                FROM [StoreClearancePrice] c
                WHERE c.IsDeleted = 0 AND c.ClearanceBarcode = @Keyword
                """
            );
            sql.AppendLine(storeFilterSql);

            // 命中来源与商品资料在同一条 SQL 中读取，省去逐请求补查商品快照的网络往返。
            // 套码、清货条码仍需关联未删除的主商品，避免孤立记录成为可打印候选。
            var lookupSql = $"""
                WITH lookupHits AS (
                    {sql}
                )
                SELECT
                    p.ProductCode AS ProductCode,
                    h.ProductCode AS MatchedProductCode,
                    p.ProductName AS ProductName,
                    COALESCE(h.ItemNumber, p.ItemNumber) AS ItemNumber,
                    COALESCE(h.Barcode, p.Barcode) AS Barcode,
                    p.ProductImage AS ProductImage,
                    pg.Grade AS Grade,
                    p.ProductType AS ProductType,
                    h.MatchSource AS MatchSource,
                    h.MatchValue AS MatchValue
                FROM lookupHits h
                INNER JOIN [Product] p ON p.ProductCode = h.ProductCode AND p.IsDeleted = 0
                LEFT JOIN [ProductGrade] pg ON pg.ProductCode = p.ProductCode AND pg.IsDeleted = 0
                """;
            return await _db.Ado.SqlQueryAsync<LookupHit>(lookupSql, parameters.ToArray());
        }

        private async Task<StoreRetailPrice?> QueryStorePriceByStoreAsync(
            string productCode,
            string? storeCode
        )
        {
            if (string.IsNullOrWhiteSpace(storeCode))
            {
                return null;
            }

            return await _db.Queryable<StoreRetailPrice>()
                .Where(x =>
                    x.ProductCode == productCode
                    && x.StoreCode == storeCode
                    && !x.IsDeleted
                )
                .FirstAsync();
        }

        private async Task<string?> ResolveStorePriceSupplierCodeAsync(StoreRetailPrice entity)
        {
            if (!string.IsNullOrWhiteSpace(entity.SupplierCode))
            {
                return entity.SupplierCode;
            }

            return await _db.Queryable<Product>()
                .Where(p => p.ProductCode == entity.ProductCode)
                .Select(p => p.LocalSupplierCode)
                .FirstAsync();
        }

        private async Task<StoreClearancePrice?> QueryClearancePriceAsync(
            string productCode,
            List<string>? accessibleStoreCodes
        )
        {
            var query = _db.Queryable<StoreClearancePrice>()
                .Where(x => x.ProductCode == productCode && !x.IsDeleted);
            if (accessibleStoreCodes != null)
            {
                query = query.Where(x => x.StoreCode != null && accessibleStoreCodes.Contains(x.StoreCode));
            }

            return await query.OrderBy(x => x.StoreCode).FirstAsync();
        }

        private async Task<Dictionary<string, string?>> QueryStoreNamesAsync(params string?[] storeCodes)
        {
            var normalizedCodes = storeCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (normalizedCodes.Count == 0)
            {
                return new Dictionary<string, string?>(StringComparer.Ordinal);
            }

            var rows = await _db.Queryable<Store>()
                .Where(s => !s.IsDeleted && s.StoreCode != null && normalizedCodes.Contains(s.StoreCode))
                .Select(s => new { s.StoreCode, s.StoreName })
                .ToListAsync();

            return rows
                .Where(x => !string.IsNullOrWhiteSpace(x.StoreCode))
                .GroupBy(x => x.StoreCode!, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (string?)group.First().StoreName,
                    StringComparer.Ordinal
                );
        }

        private async Task<StoreProductStorePriceDto> BuildStorePriceDtoAsync(
            StoreRetailPrice entity,
            string? fallbackSupplierCode
        )
        {
            var storeName = await _db.Queryable<Store>()
                .Where(s => s.StoreCode == entity.StoreCode && !s.IsDeleted)
                .Select(s => s.StoreName)
                .FirstAsync();

            var dto = new StoreProductStorePriceDto
            {
                Uuid = entity.UUID,
                StoreCode = entity.StoreCode,
                StoreName = storeName,
                ProductCode = entity.ProductCode,
                StoreProductCode = entity.StoreProductCode,
                SupplierCode = entity.SupplierCode ?? fallbackSupplierCode,
                PurchasePrice = entity.PurchasePrice,
                RetailPrice = entity.StoreRetailPriceValue,
                DiscountRate = entity.DiscountRate,
                IsAutoPricing = entity.IsAutoPricing,
                IsSpecialProduct = entity.IsSpecialProduct,
                IsActive = entity.IsActive,
            };

            await FillPricingFieldsAsync(
                dto.PurchasePrice,
                dto.SupplierCode,
                entity.StoreCode,
                assign: pricing =>
                {
                    dto.Rate = pricing.Rate;
                    dto.StrategySourceLabel = pricing.StrategySourceLabel;
                    dto.StrategyRuleLabel = pricing.StrategyRuleLabel;
                }
            );

            return dto;
        }

        private StoreProductStorePriceDto BuildStorePriceDto(
            StoreRetailPrice entity,
            string? fallbackSupplierCode,
            string? storeName,
            PricingContext? pricingContext
        )
        {
            var dto = new StoreProductStorePriceDto
            {
                Uuid = entity.UUID,
                StoreCode = entity.StoreCode,
                StoreName = storeName,
                ProductCode = entity.ProductCode,
                StoreProductCode = entity.StoreProductCode,
                SupplierCode = entity.SupplierCode ?? fallbackSupplierCode,
                PurchasePrice = entity.PurchasePrice,
                RetailPrice = entity.StoreRetailPriceValue,
                DiscountRate = entity.DiscountRate,
                IsAutoPricing = entity.IsAutoPricing,
                IsSpecialProduct = entity.IsSpecialProduct,
                IsActive = entity.IsActive,
            };

            if (pricingContext != null)
            {
                FillPricingFields(
                    pricingContext,
                    dto.PurchasePrice,
                    dto.SupplierCode,
                    entity.StoreCode,
                    assign: pricing =>
                    {
                        dto.Rate = pricing.Rate;
                        dto.StrategySourceLabel = pricing.StrategySourceLabel;
                        dto.StrategyRuleLabel = pricing.StrategyRuleLabel;
                    }
                );
            }

            return dto;
        }

        private static StoreRetailPrice? BuildStorePriceEntity(FastDetailBaseRow row)
        {
            if (string.IsNullOrWhiteSpace(row.StorePriceUuid))
            {
                return null;
            }

            return new StoreRetailPrice
            {
                UUID = row.StorePriceUuid,
                StoreCode = row.StorePriceStoreCode,
                ProductCode = row.StorePriceProductCode ?? row.ProductCode,
                StoreProductCode = row.StoreProductCode,
                SupplierCode = row.SupplierCode,
                PurchasePrice = row.PurchasePrice,
                StoreRetailPriceValue = row.StoreRetailPriceValue,
                DiscountRate = row.DiscountRate,
                IsAutoPricing = row.IsAutoPricing ?? false,
                IsSpecialProduct = row.IsSpecialProduct ?? false,
                IsActive = row.StorePriceIsActive ?? true,
            };
        }

        private StoreProductMultiCodeDto BuildMultiCodeDto(
            ProductSetCode setCode,
            StoreMultiCodeProduct? entity,
            string? fallbackSupplierCode,
            PricingContext pricingContext
        )
        {
            var dto = new StoreProductMultiCodeDto
            {
                Uuid = entity?.UUID ?? setCode.SetCodeId,
                SetCodeId = setCode.SetCodeId,
                StoreCode = entity?.StoreCode,
                ProductCode = setCode.ProductCode,
                MultiCodeProductCode = ResolveSetProductCode(setCode.SetProductCode, setCode.SetCodeId),
                StoreMultiCodeProductCode = entity?.StoreMultiCodeProductCode,
                Barcode = entity?.MultiBarcode ?? setCode.SetBarcode,
                PurchasePrice = entity?.PurchasePrice ?? setCode.SetPurchasePrice,
                RetailPrice = entity?.MultiCodeRetailPrice ?? setCode.SetRetailPrice,
                DiscountRate = entity?.DiscountRate,
                IsAutoPricing = entity?.IsAutoPricing ?? false,
                IsSpecialProduct = entity?.IsSpecialProduct ?? false,
                IsActive = entity?.IsActive ?? setCode.IsActive,
            };

            FillPricingFields(
                pricingContext,
                dto.PurchasePrice,
                fallbackSupplierCode,
                entity?.StoreCode,
                assign: pricing =>
                {
                    dto.Rate = pricing.Rate;
                    dto.StrategySourceLabel = pricing.StrategySourceLabel;
                    dto.StrategyRuleLabel = pricing.StrategyRuleLabel;
                }
            );

            return dto;
        }

        private async Task<StoreProductMultiCodeDto> BuildLegacyMultiCodeDtoAsync(
            StoreMultiCodeProduct entity,
            string? fallbackSupplierCode
        )
        {
            var dto = new StoreProductMultiCodeDto
            {
                Uuid = entity.UUID,
                SetCodeId = string.Empty,
                StoreCode = entity.StoreCode,
                ProductCode = entity.ProductCode,
                MultiCodeProductCode = entity.MultiCodeProductCode,
                StoreMultiCodeProductCode = entity.StoreMultiCodeProductCode,
                Barcode = entity.MultiBarcode,
                PurchasePrice = entity.PurchasePrice,
                RetailPrice = entity.MultiCodeRetailPrice,
                DiscountRate = entity.DiscountRate,
                IsAutoPricing = entity.IsAutoPricing,
                IsSpecialProduct = entity.IsSpecialProduct,
                IsActive = entity.IsActive,
            };

            await FillPricingFieldsAsync(
                dto.PurchasePrice,
                fallbackSupplierCode,
                entity.StoreCode,
                assign: pricing =>
                {
                    dto.Rate = pricing.Rate;
                    dto.StrategySourceLabel = pricing.StrategySourceLabel;
                    dto.StrategyRuleLabel = pricing.StrategyRuleLabel;
                }
            );

            return dto;
        }

        private StoreProductSetCodeDto BuildSetCodeDto(
            ProductSetCode setCode,
            StoreMultiCodeProduct? projection
        )
        {
            return new StoreProductSetCodeDto
            {
                SetCodeId = setCode.SetCodeId,
                ProductCode = setCode.ProductCode,
                SetProductCode = ResolveSetProductCode(setCode.SetProductCode, setCode.SetCodeId),
                SetItemNumber = setCode.SetItemNumber,
                SetBarcode = projection?.MultiBarcode ?? setCode.SetBarcode,
                SetPurchasePrice = projection?.PurchasePrice ?? setCode.SetPurchasePrice,
                SetRetailPrice = projection?.MultiCodeRetailPrice ?? setCode.SetRetailPrice,
                SetQuantity = setCode.SetQuantity,
                SetType = setCode.SetType,
                SetTypeDescription = ResolveSetTypeDescription(setCode.SetType),
                IsActive = projection?.IsActive ?? setCode.IsActive,
            };
        }

        private StoreProductClearancePriceDto BuildClearancePriceDto(
            StoreClearancePrice entity,
            string? storeName
        )
        {
            return new StoreProductClearancePriceDto
            {
                Uuid = entity.UUID,
                StoreCode = entity.StoreCode,
                StoreName = storeName,
                ProductCode = entity.ProductCode,
                ClearanceBarcode = entity.ClearanceBarcode,
                ClearancePrice = entity.ClearancePrice,
            };
        }

        private static StoreClearancePrice? BuildClearancePriceEntity(FastDetailBaseRow row)
        {
            if (string.IsNullOrWhiteSpace(row.ClearanceUuid))
            {
                return null;
            }

            return new StoreClearancePrice
            {
                UUID = row.ClearanceUuid,
                StoreCode = row.ClearanceStoreCode,
                ProductCode = row.ClearanceProductCode ?? row.ProductCode,
                ClearanceBarcode = row.ClearanceBarcode,
                ClearancePrice = row.ClearancePrice,
            };
        }

        private async Task<StoreProductClearancePriceDto> BuildClearancePriceDtoAsync(
            StoreClearancePrice entity
        )
        {
            var storeName = await _db.Queryable<Store>()
                .Where(s => s.StoreCode == entity.StoreCode && !s.IsDeleted)
                .Select(s => s.StoreName)
                .FirstAsync();

            return new StoreProductClearancePriceDto
            {
                Uuid = entity.UUID,
                StoreCode = entity.StoreCode,
                StoreName = storeName,
                ProductCode = entity.ProductCode,
                ClearanceBarcode = entity.ClearanceBarcode,
                ClearancePrice = entity.ClearancePrice,
            };
        }

        private async Task FillPricingFieldsAsync(
            decimal? purchasePrice,
            string? supplierCode,
            string? storeCode,
            Action<(decimal? Rate, string? StrategySourceLabel, string? StrategyRuleLabel)> assign
        )
        {
            // 倍率只用于展示；不支持自动定价的低成本商品仍须能够查询、补建价格。
            if (!purchasePrice.HasValue || purchasePrice.Value < 0.1m)
            {
                assign((null, null, null));
                return;
            }

            var strategy = await _autoPricingService.FindStrategyForPriceAsync(
                purchasePrice.Value,
                supplierCode,
                storeCode
            );

            var rate = _autoPricingService.CalculateRate(purchasePrice.Value, strategy);
            var rule = strategy?.Details?.FirstOrDefault(d =>
                purchasePrice.Value >= d.MinPrice && purchasePrice.Value <= d.MaxPrice
            );

            assign(
                (
                    rate,
                    strategy?.Name ?? "自动定价策略",
                    rule == null ? null : $"{rule.MinPrice:0.##} - {rule.MaxPrice:0.##}"
                )
            );
        }

        private void FillPricingFields(
            PricingContext pricingContext,
            decimal? purchasePrice,
            string? supplierCode,
            string? storeCode,
            Action<(decimal? Rate, string? StrategySourceLabel, string? StrategyRuleLabel)> assign
        )
        {
            if (!purchasePrice.HasValue || purchasePrice.Value < 0.1m)
            {
                assign((null, null, null));
                return;
            }

            var strategy = FindStrategyForPrice(
                pricingContext,
                purchasePrice.Value,
                supplierCode,
                storeCode
            );
            var rate = _autoPricingService.CalculateRate(purchasePrice.Value, strategy);
            var rule = strategy?.Details?.FirstOrDefault(d =>
                purchasePrice.Value >= d.MinPrice && purchasePrice.Value <= d.MaxPrice
            );

            assign(
                (
                    rate,
                    strategy?.Name ?? "自动定价策略",
                    rule == null ? null : $"{rule.MinPrice:0.##} - {rule.MaxPrice:0.##}"
                )
            );
        }

        private async Task<PricingContext> CreatePricingContextAsync()
        {
            var strategies = await _cache.GetOrCreateAsync(
                PricingStrategiesCacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = PricingStrategiesCacheDuration;
                    return await _autoPricingService.GetAllActiveStrategiesAsync();
                }
            );
            return new PricingContext(strategies ?? new List<PricingStrategy>());
        }

        private PricingStrategy? FindStrategyForPrice(
            PricingContext pricingContext,
            decimal purchasePrice,
            string? supplierCode,
            string? storeCode
        )
        {
            var supplierStrategies = string.IsNullOrWhiteSpace(supplierCode)
                ? pricingContext.Empty
                : pricingContext.Strategies
                    .Where(s =>
                        s.Targets?.Any(t =>
                            t.TargetType == "Supplier" && t.TargetCode == supplierCode
                        ) ?? false
                    )
                    .ToList();

            var storeStrategies = string.IsNullOrWhiteSpace(storeCode)
                ? pricingContext.Empty
                : pricingContext.Strategies
                    .Where(s =>
                        s.Targets?.Any(t => t.TargetType == "Store" && t.TargetCode == storeCode)
                        ?? false
                    )
                    .ToList();

            var globalStrategies = pricingContext.Strategies
                .Where(s => s.Level == "Global" || (s.Targets == null || s.Targets.Count == 0))
                .ToList();

            return _autoPricingService.FindBestStrategyForPrice(
                purchasePrice,
                supplierStrategies,
                storeStrategies,
                globalStrategies
            );
        }

        private async Task<List<StoreMultiCodeProduct>> QueryProjectedStoreMultiCodesAsync(
            List<ProductSetCode> setCodes,
            string? storeCode
        )
        {
            if (string.IsNullOrWhiteSpace(storeCode) || setCodes.Count == 0)
            {
                return new List<StoreMultiCodeProduct>();
            }

            var setProductCodes = setCodes
                .Select(s => ResolveSetProductCode(s.SetProductCode, s.SetCodeId))
                .Distinct()
                .ToList();
            var parentProductCodes = setCodes
                .Select(s => s.ProductCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (parentProductCodes.Count != 1)
            {
                throw new InvalidOperationException("门店套装投影查询必须限定单一父商品");
            }
            var parentProductCode = parentProductCodes[0];
            var existing = new List<StoreMultiCodeProduct>();
            foreach (var batch in setProductCodes.Chunk(500))
            {
                var childCodes = batch.ToList();
                existing.AddRange(await _db.Queryable<StoreMultiCodeProduct>()
                    .Where(x =>
                        x.StoreCode == storeCode
                        && x.ProductCode == parentProductCode
                        && x.MultiCodeProductCode != null
                        && childCodes.Contains(x.MultiCodeProductCode)
                        && !x.IsDeleted
                    )
                    .Select(x => new StoreMultiCodeProduct
                    {
                        UUID = x.UUID,
                        StoreCode = x.StoreCode,
                        ProductCode = x.ProductCode,
                        MultiCodeProductCode = x.MultiCodeProductCode,
                        StoreMultiCodeProductCode = x.StoreMultiCodeProductCode,
                        MultiBarcode = x.MultiBarcode,
                        PurchasePrice = x.PurchasePrice,
                        MultiCodeRetailPrice = x.MultiCodeRetailPrice,
                        DiscountRate = x.DiscountRate,
                        IsAutoPricing = x.IsAutoPricing,
                        IsSpecialProduct = x.IsSpecialProduct,
                        IsActive = x.IsActive,
                    })
                    .ToListAsync());
            }

            var existingMap = existing.ToDictionary(
                x => ResolveSetProductCode(x.MultiCodeProductCode, x.UUID),
                x => x
            );

            return existingMap.Values.OrderBy(x => x.MultiBarcode).ToList();
        }

        private async Task SyncCurrentStoreProjectedRecordsAsync(
            StoreRetailPrice mainStorePrice,
            string updatedBy,
            SetChildPurchasePriceLockScope lockScope
        )
        {
            if (string.IsNullOrWhiteSpace(mainStorePrice.StoreCode) || string.IsNullOrWhiteSpace(mainStorePrice.ProductCode))
            {
                return;
            }

            var setCodes = await _db.Queryable<ProductSetCode>()
                .Where(x => x.ProductCode == mainStorePrice.ProductCode && !x.IsDeleted)
                .ToListAsync();
            if (setCodes.Count == 0)
            {
                return;
            }

            var currentStoreRecords = await QueryProjectedStoreMultiCodesAsync(
                setCodes,
                mainStorePrice.StoreCode
            );
            var projectionMap = currentStoreRecords.ToDictionary(
                x => ResolveSetProductCode(x.MultiCodeProductCode, x.UUID),
                x => x
            );
            var inserts = new List<StoreMultiCodeProduct>();
            var updates = new List<StoreMultiCodeProduct>();
            foreach (var setCode in setCodes)
            {
                var setProductCode = ResolveSetProductCode(setCode.SetProductCode, setCode.SetCodeId);
                if (!projectionMap.TryGetValue(setProductCode, out var existing))
                {
                    inserts.Add(
                        BuildProjectedStoreMultiCode(
                            setCode,
                            mainStorePrice,
                            mainStorePrice.StoreCode!,
                            updatedBy
                        )
                    );
                    continue;
                }

                ApplyProjectionValues(existing, setCode, mainStorePrice, updatedBy);
                updates.Add(existing);
            }

            if (inserts.Count > 0)
            {
                await _db.Insertable(inserts).ExecuteCommandAsync();
            }

            if (updates.Count > 0)
            {
                await _db.Updateable(updates)
                    .UpdateColumns(x => new
                    {
                        x.ProductCode,
                        x.MultiCodeProductCode,
                        x.StoreMultiCodeProductCode,
                        x.MultiBarcode,
                        x.PurchasePrice,
                        x.MultiCodeRetailPrice,
                        x.DiscountRate,
                        x.IsAutoPricing,
                        x.IsSpecialProduct,
                        x.IsActive,
                        x.UpdatedAt,
                        x.UpdatedBy,
                    })
                    .ExecuteCommandAsync();
            }

            if (setCodes.Any(x => x.SetType == 1 || x.SetType == 2))
            {
                var writeback = await new SetChildPurchasePriceService(_db)
                    .RecalculateStoresLockedAsync(
                        lockScope,
                        new[] { mainStorePrice.ProductCode! },
                        new[] { mainStorePrice.StoreCode! },
                        updatedBy
                    );
                EnsureNoSkippedStoreSetGroups(writeback);
            }
        }

        private async Task SyncCurrentStoreProjectedPriceRecordsAsync(
            StoreRetailPrice mainStorePrice,
            string updatedBy,
            SetChildPurchasePriceLockScope lockScope
        )
        {
            if (
                string.IsNullOrWhiteSpace(mainStorePrice.StoreCode)
                || string.IsNullOrWhiteSpace(mainStorePrice.ProductCode)
            )
            {
                return;
            }

            var setCodes = await WithWarehouseSyncUpdateLock(
                    _db.Queryable<ProductSetCode>()
                        .Where(x =>
                            x.ProductCode == mainStorePrice.ProductCode
                            && x.IsActive
                            && !x.IsDeleted
                        )
                )
                .ToListAsync();
            if (setCodes.Count == 0)
            {
                return;
            }

            var setProductCodes = setCodes
                .Select(x => ResolveSetProductCode(x.SetProductCode, x.SetCodeId))
                .Distinct()
                .ToList();
            var currentStoreRecords = await WithWarehouseSyncUpdateLock(
                    _db.Queryable<StoreMultiCodeProduct>()
                        .Where(x =>
                            x.StoreCode == mainStorePrice.StoreCode
                            && x.ProductCode == mainStorePrice.ProductCode
                            && x.MultiCodeProductCode != null
                            && setProductCodes.Contains(x.MultiCodeProductCode)
                            && x.IsActive
                            && !x.IsDeleted
                        )
                )
                .ToListAsync();
            var projectionMap = currentStoreRecords.ToDictionary(
                x => ResolveSetProductCode(x.MultiCodeProductCode, x.UUID),
                x => x
            );
            var updates = new List<StoreMultiCodeProduct>();
            foreach (var setCode in setCodes)
            {
                var setProductCode = ResolveSetProductCode(setCode.SetProductCode, setCode.SetCodeId);
                if (!projectionMap.TryGetValue(setProductCode, out var existing))
                {
                    // 普通价格维护不得复活停用/软删除投影，也不得补建缺失关系。
                    continue;
                }

                var nextRetailPrice = setCode.SetType == 2
                    ? mainStorePrice.StoreRetailPriceValue
                    : setCode.SetRetailPrice;
                var nextPurchasePrice = setCode.SetType == 2
                    ? mainStorePrice.PurchasePrice
                    : existing.PurchasePrice;
                if (
                    PricesEqual(existing.PurchasePrice, nextPurchasePrice)
                    && PricesEqual(existing.MultiCodeRetailPrice, nextRetailPrice)
                )
                {
                    continue;
                }

                // 既有派生记录只同步价格，折扣、自动价、特殊商品、启用状态和创建审计全部保留。
                existing.PurchasePrice = nextPurchasePrice;
                existing.MultiCodeRetailPrice = nextRetailPrice;
                existing.UpdatedAt = DateTime.UtcNow;
                existing.UpdatedBy = updatedBy;
                updates.Add(existing);
            }

            if (updates.Count > 0)
            {
                await _db.Updateable(updates)
                    .UpdateColumns(x => new
                    {
                        x.PurchasePrice,
                        x.MultiCodeRetailPrice,
                        x.UpdatedAt,
                        x.UpdatedBy,
                    })
                    .ExecuteCommandAsync();
            }

            if (setCodes.Any(x => x.SetType == 1 || x.SetType == 2))
            {
                var writeback = await new SetChildPurchasePriceService(_db)
                    .RecalculateStoresLockedAsync(
                        lockScope,
                        new[] { mainStorePrice.ProductCode! },
                        new[] { mainStorePrice.StoreCode! },
                        updatedBy
                    );
                EnsureNoSkippedStoreSetGroups(writeback);
            }
        }

        private async Task SyncSetCodeAcrossStoresAsync(
            ProductSetCode setCode,
            string updatedBy,
            SetChildPurchasePriceLockScope lockScope,
            bool recalculateSetCosts = true,
            bool preserveExistingProjectionMetadata = false
        )
        {
            var activeStores = await _db.Queryable<Store>()
                .Where(s => s.IsActive && !s.IsDeleted && s.StoreCode != null)
                .Select(s => s.StoreCode!)
                .ToListAsync();
            if (activeStores.Count == 0)
            {
                if (recalculateSetCosts && (setCode.SetType == 1 || setCode.SetType == 2))
                {
                    var writeback = await new SetChildPurchasePriceService(_db)
                        .RecalculateLockedAsync(
                            lockScope,
                            new[] { setCode.ProductCode },
                            storeCodes: null,
                            updatedBy: updatedBy
                        );
                    EnsureNoSkippedStoreSetGroups(writeback);
                }
                return;
            }

            var mainStorePrices = await _db.Queryable<StoreRetailPrice>()
                .Where(x =>
                    x.ProductCode == setCode.ProductCode
                    && x.StoreCode != null
                    && activeStores.Contains(x.StoreCode)
                    && !x.IsDeleted
                )
                .ToListAsync();
            var mainPriceMap = mainStorePrices.ToDictionary(x => x.StoreCode!, x => x);

            var setProductCode = ResolveSetProductCode(setCode.SetProductCode, setCode.SetCodeId);
            var existing = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x =>
                    x.ProductCode == setCode.ProductCode
                    && x.MultiCodeProductCode == setProductCode
                    && x.StoreCode != null
                    && activeStores.Contains(x.StoreCode)
                    && !x.IsDeleted
                )
                .ToListAsync();
            var existingMap = existing.ToDictionary(x => x.StoreCode!, x => x);

            var inserts = new List<StoreMultiCodeProduct>();
            var updates = new List<StoreMultiCodeProduct>();
            foreach (var activeStoreCode in activeStores)
            {
                mainPriceMap.TryGetValue(activeStoreCode, out var mainPrice);
                if (existingMap.TryGetValue(activeStoreCode, out var current))
                {
                    var discountRate = current.DiscountRate;
                    var isAutoPricing = current.IsAutoPricing;
                    var isSpecialProduct = current.IsSpecialProduct;
                    ApplyProjectionValues(current, setCode, mainPrice, updatedBy);
                    if (preserveExistingProjectionMetadata)
                    {
                        // 快照维护只负责条码、派生价格和启停状态，不覆盖分店自己的定价策略字段。
                        current.DiscountRate = discountRate;
                        current.IsAutoPricing = isAutoPricing;
                        current.IsSpecialProduct = isSpecialProduct;
                    }
                    updates.Add(current);
                    continue;
                }

                inserts.Add(BuildProjectedStoreMultiCode(setCode, mainPrice, activeStoreCode, updatedBy));
            }

            if (inserts.Count > 0)
            {
                await _db.Insertable(inserts).ExecuteCommandAsync();
            }

            if (updates.Count > 0)
            {
                await _db.Updateable(updates)
                    .UpdateColumns(x => new
                    {
                        x.ProductCode,
                        x.MultiCodeProductCode,
                        x.StoreMultiCodeProductCode,
                        x.MultiBarcode,
                        x.PurchasePrice,
                        x.MultiCodeRetailPrice,
                        x.DiscountRate,
                        x.IsAutoPricing,
                        x.IsSpecialProduct,
                        x.IsActive,
                        x.UpdatedAt,
                        x.UpdatedBy,
                    })
                    .ExecuteCommandAsync();
            }

            if (recalculateSetCosts && (setCode.SetType == 1 || setCode.SetType == 2))
            {
                var writeback = await new SetChildPurchasePriceService(_db)
                    .RecalculateLockedAsync(
                        lockScope,
                        new[] { setCode.ProductCode },
                        storeCodes: null,
                        updatedBy: updatedBy
                    );
                EnsureNoSkippedStoreSetGroups(writeback);
            }
        }

        // 快照保存会携带大量子码；批量路径只用于该端点，避免对每个子码重复读取门店、主价格和投影。
        private async Task SyncSetCodesAcrossStoresAsync(
            IReadOnlyCollection<ProductSetCode> setCodes,
            string updatedBy,
            bool preserveExistingProjectionMetadata
        )
        {
            if (setCodes.Count == 0) return;
            var productCode = setCodes.First().ProductCode;
            var stores = await _db.Queryable<Store>()
                .Where(s => s.IsActive && !s.IsDeleted && s.StoreCode != null)
                .Select(s => s.StoreCode!).ToListAsync();
            if (stores.Count == 0) return;
            var storeSet = stores.ToHashSet(StringComparer.Ordinal);
            var prices = (await _db.Queryable<StoreRetailPrice>()
                .Where(x => x.ProductCode == productCode && x.StoreCode != null && !x.IsDeleted)
                .ToListAsync())
                .Where(x => storeSet.Contains(x.StoreCode!))
                .ToList();
            var priceMap = prices.ToDictionary(x => x.StoreCode!, x => x);
            const int queryBatchSize = 500;
            const int writeBatchSize = 100;
            var inserts = new List<StoreMultiCodeProduct>();
            var updates = new List<StoreMultiCodeProduct>();
            foreach (var batch in setCodes.Chunk(queryBatchSize))
            {
                var codeMap = batch.ToDictionary(
                    item => ResolveSetProductCode(item.SetProductCode, item.SetCodeId), item => item, StringComparer.Ordinal);
                var childCodes = codeMap.Keys.ToList();
                var existing = await _db.Queryable<StoreMultiCodeProduct>()
                    .Where(x => x.ProductCode == productCode && x.MultiCodeProductCode != null
                        && childCodes.Contains(x.MultiCodeProductCode) && x.StoreCode != null && !x.IsDeleted)
                    .ToListAsync();
                existing = existing.Where(item => storeSet.Contains(item.StoreCode!)).ToList();
                var existingMap = existing.ToDictionary(
                    item => $"{item.StoreCode}\u001f{item.MultiCodeProductCode}", item => item, StringComparer.Ordinal);
                foreach (var setCode in batch)
                {
                    var childCode = ResolveSetProductCode(setCode.SetProductCode, setCode.SetCodeId);
                    foreach (var storeCode in stores)
                    {
                        priceMap.TryGetValue(storeCode, out var price);
                        if (existingMap.TryGetValue($"{storeCode}\u001f{childCode}", out var current))
                        {
                            var discountRate = current.DiscountRate;
                            var isAutoPricing = current.IsAutoPricing;
                            var isSpecialProduct = current.IsSpecialProduct;
                            ApplyProjectionValues(current, setCode, price, updatedBy);
                            if (preserveExistingProjectionMetadata)
                            {
                                current.DiscountRate = discountRate;
                                current.IsAutoPricing = isAutoPricing;
                                current.IsSpecialProduct = isSpecialProduct;
                            }
                            updates.Add(current);
                        }
                        else
                        {
                            inserts.Add(BuildProjectedStoreMultiCode(setCode, price, storeCode, updatedBy));
                        }
                    }
                }
            }
            foreach (var batch in inserts.Chunk(writeBatchSize))
            {
                await _db.Insertable(batch.ToList()).ExecuteCommandAsync();
            }
            foreach (var batch in updates.Chunk(writeBatchSize))
            {
                await _db.Updateable(batch.ToList()).UpdateColumns(x => new
                {
                    x.ProductCode, x.MultiCodeProductCode, x.StoreMultiCodeProductCode, x.MultiBarcode,
                    x.PurchasePrice, x.MultiCodeRetailPrice, x.DiscountRate, x.IsAutoPricing,
                    x.IsSpecialProduct, x.IsActive, x.UpdatedAt, x.UpdatedBy,
                }).ExecuteCommandAsync();
            }
        }

        private static string BuildSetCodeAuditSnapshot(IEnumerable<ProductSetCode> items)
        {
            return JsonSerializer.Serialize(items.OrderBy(item => item.SetCodeId, StringComparer.Ordinal)
                .Select(item => new
                {
                    setCodeId = item.SetCodeId,
                    setProductCode = ResolveSetProductCode(item.SetProductCode, item.SetCodeId),
                    setItemNumber = item.SetItemNumber,
                    barcode = item.SetBarcode?.Trim(),
                    retailPrice = RoundNullablePrice(item.SetRetailPrice),
                    setType = item.SetType,
                    isActive = item.IsActive,
                }));
        }

        private async Task RecordSetCodeSnapshotAuditAsync(
            string productCode, string storeCode, string beforeSnapshot, string afterSnapshot,
            string? actorUserGuid, string actorName, bool isSystemActor, DateTime occurredAtUtc
        )
        {
            if (string.Equals(beforeSnapshot, afterSnapshot, StringComparison.Ordinal)) return;
            await _db.Insertable(new WarehouseProductChangeHistory
            {
                EventGuid = Guid.NewGuid(), ProductCode = productCode, Action = "Update",
                Source = "StoreProductMaintenanceSetCodeSnapshot", SourceReference = $"{productCode}@{storeCode}",
                ActorUserGuid = string.IsNullOrWhiteSpace(actorUserGuid) ? null : actorUserGuid,
                ActorName = actorName, ActorType = isSystemActor ? "System" : "User",
                OccurredAtUtc = occurredAtUtc,
                ChangesJson = JsonSerializer.Serialize(new[] { new WarehouseProductChangeItemDto
                {
                    FieldKey = "productSetCodes", ValueType = "json", BeforeValue = beforeSnapshot, AfterValue = afterSnapshot,
                }}),
            }).ExecuteCommandAsync();
        }

        private static ApiResponse<T> BuildSetChildPurchasePriceBusyResponse<T>()
        {
            return ApiResponse<T>.Error(
                "套装商品正在被其他操作修改，请稍后重试",
                SetChildPurchasePriceMutationLock.BusyErrorCode
            );
        }

        private static string ResolveSafeMutationFailureMessage(
            Exception exception,
            string fallbackMessage
        )
        {
            if (exception is ProductMaintenanceHqEnqueueException or StoreProductMaintenanceBusinessException)
            {
                return exception.Message;
            }

            // 成本重算服务的这个固定前缀属于可操作的业务冲突；其余异常不得透传内部细节。
            return exception is InvalidOperationException
                && exception.Message.StartsWith(
                    "套装子项成本无法完整重算，主商品:",
                    StringComparison.Ordinal
                )
                ? exception.Message
                : fallbackMessage;
        }

        private static void EnsureNoSkippedStoreSetGroups(
            SetChildPurchasePriceWritebackResultDto writeback
        )
        {
            if (
                writeback.ProductSetCode.SkippedGroupCount == 0
                && writeback.StoreMultiCodeProduct.SkippedGroupCount == 0
            )
            {
                return;
            }

            // 目标门店套装组不能带着未分摊的子项成本提交，交由外层事务整体回滚。
            throw new StoreProductMaintenanceBusinessException(
                writeback.Errors.FirstOrDefault()?.Reason ?? "目标套装组无法重算"
            );
        }

        private StoreMultiCodeProduct BuildProjectedStoreMultiCode(
            ProductSetCode setCode,
            StoreRetailPrice? mainStorePrice,
            string storeCode,
            string updatedBy
        )
        {
            var projection = new StoreMultiCodeProduct
            {
                UUID = UuidHelper.GenerateUuid7(),
                StoreCode = storeCode,
                ProductCode = setCode.ProductCode,
                MultiCodeProductCode = ResolveSetProductCode(setCode.SetProductCode, setCode.SetCodeId),
                StoreMultiCodeProductCode = storeCode + ResolveSetProductCode(setCode.SetProductCode, setCode.SetCodeId),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                CreatedBy = updatedBy,
                UpdatedBy = updatedBy,
                IsDeleted = false,
            };
            ApplyProjectionValues(projection, setCode, mainStorePrice, updatedBy);
            return projection;
        }

        private void ApplyProjectionValues(
            StoreMultiCodeProduct projection,
            ProductSetCode setCode,
            StoreRetailPrice? mainStorePrice,
            string updatedBy
        )
        {
            projection.ProductCode = setCode.ProductCode;
            projection.MultiCodeProductCode = ResolveSetProductCode(setCode.SetProductCode, setCode.SetCodeId);
            projection.StoreMultiCodeProductCode = (projection.StoreCode ?? string.Empty) + projection.MultiCodeProductCode;
            projection.MultiBarcode = setCode.SetBarcode;
            projection.DiscountRate = null;
            projection.IsAutoPricing = false;
            projection.IsSpecialProduct = false;
            projection.IsActive = setCode.IsActive;

            if (setCode.SetType == 2)
            {
                projection.PurchasePrice = mainStorePrice?.PurchasePrice;
                projection.MultiCodeRetailPrice = mainStorePrice?.StoreRetailPriceValue;
            }
            else
            {
                projection.MultiCodeRetailPrice = setCode.SetRetailPrice;
                // 既有成本保持不动，新行先为空；兄弟子项齐备后由统一服务整组分摊。
            }

            projection.UpdatedAt = DateTime.UtcNow;
            projection.UpdatedBy = updatedBy;
        }

        private async Task<StoreProductSetCodeDto> QueryProjectedSetCodeAsync(
            string setCodeId,
            string? storeCode,
            string? fallbackSupplierCode
        )
        {
            var setCode = await _db.Queryable<ProductSetCode>()
                .Where(x => x.SetCodeId == setCodeId && !x.IsDeleted)
                .FirstAsync();
            var setProductCode = ResolveSetProductCode(setCode.SetProductCode, setCode.SetCodeId);
            var projection = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x =>
                    x.StoreCode == storeCode
                    && x.ProductCode == setCode.ProductCode
                    && x.MultiCodeProductCode == setProductCode
                    && !x.IsDeleted
                )
                .FirstAsync();

            if (setCode.SetType == 2)
            {
                var pricingContext = await CreatePricingContextAsync();
                var multi = BuildMultiCodeDto(
                    setCode,
                    projection,
                    fallbackSupplierCode,
                    pricingContext
                );
                return new StoreProductSetCodeDto
                {
                    SetCodeId = multi.SetCodeId,
                    ProductCode = multi.ProductCode ?? string.Empty,
                    SetProductCode = multi.MultiCodeProductCode ?? string.Empty,
                    SetItemNumber = setCode.SetItemNumber,
                    SetBarcode = multi.Barcode,
                    SetPurchasePrice = multi.PurchasePrice,
                    SetRetailPrice = multi.RetailPrice,
                    SetQuantity = setCode.SetQuantity,
                    SetType = setCode.SetType,
                    SetTypeDescription = ResolveSetTypeDescription(setCode.SetType),
                    IsActive = multi.IsActive,
                };
            }

            return BuildSetCodeDto(setCode, projection);
        }

        private async Task<string> GenerateClearanceBarcodeAsync(string? storeCode)
        {
            if (string.IsNullOrWhiteSpace(storeCode))
            {
                throw new InvalidOperationException("缺少分店代码，无法生成清货条码");
            }

            var localNow = DateTime.Now;
            var storeSegment = StoreProductMaintenanceClearanceBarcodeHelper.NormalizeStoreCodeSegment(
                storeCode
            );
            var dateSegment = StoreProductMaintenanceClearanceBarcodeHelper.FormatDateSegment(localNow);
            var attemptedRandomSegments = new HashSet<int>();
            string? barcode = null;

            for (var attempt = 1; attempt <= StoreProductMaintenanceClearanceBarcodeHelper.MaxRandomAttempts; attempt++)
            {
                var nextRandom = Random.Shared.Next(0, 1000);
                if (!attemptedRandomSegments.Add(nextRandom))
                {
                    continue;
                }

                var candidate = StoreProductMaintenanceClearanceBarcodeHelper.GenerateBarcodeForRandom(
                    storeCode,
                    localNow,
                    nextRandom
                );

                if (!await ClearanceBarcodeExistsAsync(candidate))
                {
                    barcode = candidate;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(barcode))
            {
                throw new InvalidOperationException(
                    $"清货条码生成失败: 分店 {storeCode} 在当天可用随机段已耗尽"
                );
            }

            _logger.LogInformation(
                "StoreProductMaintenance generated clearance barcode storeCode={StoreCode} storeSegment={StoreSegment} dateSegment={DateSegment} attemptedRandomCount={AttemptedRandomCount} maxRandomAttempts={MaxRandomAttempts} clearanceBarcode={ClearanceBarcode}",
                storeCode,
                storeSegment,
                dateSegment,
                attemptedRandomSegments.Count,
                StoreProductMaintenanceClearanceBarcodeHelper.MaxRandomAttempts,
                barcode
            );
            return barcode;
        }

        private async Task<bool> ClearanceBarcodeExistsAsync(string barcode)
        {
            if (
                await _db.Queryable<Product>()
                    .Where(p => !p.IsDeleted && p.Barcode == barcode)
                    .AnyAsync()
            )
            {
                return true;
            }

            if (
                await _db.Queryable<ProductSetCode>()
                    .Where(x => !x.IsDeleted && x.SetBarcode == barcode)
                    .AnyAsync()
            )
            {
                return true;
            }

            return await _db.Queryable<StoreClearancePrice>()
                .Where(x => !x.IsDeleted && x.ClearanceBarcode == barcode)
                .AnyAsync();
        }

        private static string ResolveSetProductCode(string? setProductCode, string fallback)
        {
            return string.IsNullOrWhiteSpace(setProductCode) ? fallback : setProductCode;
        }

        private static string? ResolveCurrentStoreCode(
            string? requestedStoreCode,
            List<string>? selectedStoreCodes,
            StoreRetailPrice? storePriceEntity
        )
        {
            if (!string.IsNullOrWhiteSpace(requestedStoreCode))
            {
                return requestedStoreCode;
            }

            if (!string.IsNullOrWhiteSpace(storePriceEntity?.StoreCode))
            {
                return storePriceEntity.StoreCode;
            }

            return selectedStoreCodes?.FirstOrDefault();
        }

        private static string? ResolveFastDetailTargetStoreCode(
            string? requestedStoreCode,
            List<string>? selectedStoreCodes
        )
        {
            if (!string.IsNullOrWhiteSpace(requestedStoreCode))
            {
                return requestedStoreCode;
            }

            return selectedStoreCodes?.FirstOrDefault();
        }

        private static List<string>? ResolveScopedStoreCodes(
            string? requestedStoreCode,
            List<string>? accessibleStoreCodes
        )
        {
            if (!string.IsNullOrWhiteSpace(requestedStoreCode))
            {
                if (accessibleStoreCodes == null)
                {
                    return new List<string> { requestedStoreCode };
                }

                return accessibleStoreCodes.Contains(requestedStoreCode)
                    ? new List<string> { requestedStoreCode }
                    : new List<string>();
            }

            return accessibleStoreCodes;
        }

        private static bool CanAccessStore(string? storeCode, List<string>? accessibleStoreCodes)
        {
            if (accessibleStoreCodes == null)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(storeCode) && accessibleStoreCodes.Contains(storeCode);
        }

        private static string FormatPrice(decimal? value)
        {
            return value.HasValue ? value.Value.ToString("0.00") : string.Empty;
        }

        private static string FormatStoreScope(List<string>? storeCodes)
        {
            if (storeCodes == null)
            {
                return "ALL";
            }

            return storeCodes.Count == 0 ? "NONE" : string.Join(",", storeCodes);
        }

        private static string? NormalizeProductTypeLabel(string? rawValue)
        {
            return int.TryParse(rawValue, out var productType)
                ? StoreProductMaintenanceSyncHelper.NormalizeProductTypeLabel(productType)
                : rawValue;
        }

        private static string ResolveSetTypeDescription(int setType)
        {
            return setType switch
            {
                1 => "套装",
                2 => "多码",
                _ => "未知类型",
            };
        }

        private sealed class LookupHit
        {
            public string ProductCode { get; set; } = string.Empty;
            public string MatchedProductCode { get; set; } = string.Empty;
            public string ProductName { get; set; } = string.Empty;
            public string? ItemNumber { get; set; }
            public string? Barcode { get; set; }
            public string? ProductImage { get; set; }
            public string? Grade { get; set; }
            public int? ProductType { get; set; }
            public string MatchSource { get; set; } = string.Empty;
            public string MatchValue { get; set; } = string.Empty;
        }

        private sealed class ProductSetCodeCounts
        {
            public int TotalSetCodeCount { get; set; }
        }

        private sealed class FastDetailBaseRow
        {
            public string? ProductCode { get; set; }
            public string? ProductName { get; set; }
            public string? ItemNumber { get; set; }
            public string? Barcode { get; set; }
            public string? ProductImage { get; set; }
            public int? ProductType { get; set; }
            public string? LocalSupplierCode { get; set; }
            public string? LocalSupplierName { get; set; }
            public string? Grade { get; set; }
            public string? StorePriceUuid { get; set; }
            public string? StorePriceStoreCode { get; set; }
            public string? StoreName { get; set; }
            public string? StorePriceProductCode { get; set; }
            public string? StoreProductCode { get; set; }
            public string? SupplierCode { get; set; }
            public decimal? PurchasePrice { get; set; }
            public decimal? StoreRetailPriceValue { get; set; }
            public decimal? DiscountRate { get; set; }
            public bool? IsAutoPricing { get; set; }
            public bool? IsSpecialProduct { get; set; }
            public bool? StorePriceIsActive { get; set; }
            public string? ClearanceUuid { get; set; }
            public string? ClearanceStoreCode { get; set; }
            public string? ClearanceStoreName { get; set; }
            public string? ClearanceProductCode { get; set; }
            public string? ClearanceBarcode { get; set; }
            public decimal? ClearancePrice { get; set; }
            public int TotalSetCodeCount { get; set; }
        }

        private sealed class ProductCodePageQueryResult
        {
            public List<ProductSetCode> SetCodes { get; set; } = new();
            public int TotalCount { get; set; }
            public int Page { get; set; }
            public int PageSize { get; set; }
            public string? StoreCode { get; set; }
            public string? LocalSupplierCode { get; set; }
            public string? ErrorMessage { get; set; }

            public static ProductCodePageQueryResult Fail(string message)
            {
                return new ProductCodePageQueryResult { ErrorMessage = message };
            }
        }

        private sealed class PricingContext
        {
            public PricingContext(List<PricingStrategy> strategies)
            {
                Strategies = strategies;
            }

            public List<PricingStrategy> Strategies { get; }

            public List<PricingStrategy> Empty { get; } = new();
        }
    }
}
