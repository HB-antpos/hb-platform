using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 把 WarehouseProduct 的进口价和 OEM 价批量 upsert 到指定分店价格表。
/// </summary>
public sealed class WarehouseStorePriceSyncService : IWarehouseStorePriceSyncService
{
    private const string DefaultSupplierCode = "200";
    private const int QueryBatchSize = 800;
    // StoreRetailPrice 新增包含约 16 个持久化字段，100 行可留在 SQL Server 2100 参数上限内。
    private const int WriteBatchSize = 100;

    private readonly SqlSugarContext _context;
    private readonly IProductHqSyncService _hqSyncService;
    private readonly ILogger<WarehouseStorePriceSyncService> _logger;

    private readonly IStorePriceUpdateTaskService? _priceTaskService;

    public WarehouseStorePriceSyncService(
        SqlSugarContext context,
        IProductHqSyncService hqSyncService,
        ILogger<WarehouseStorePriceSyncService> logger,
        // 可选：仓库价批量下发覆盖分店价后，为分店登记「待换标签」通知。
        IStorePriceUpdateTaskService? priceTaskService = null
    )
    {
        _context = context;
        _hqSyncService = hqSyncService;
        _logger = logger;
        _priceTaskService = priceTaskService;
    }

    public async Task<List<WarehouseStorePriceSyncTargetStoreDto>> GetTargetStoresAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stores = await _context.Db.Queryable<Store>()
            .Where(store => store.IsActive && !store.IsDeleted)
            .ToListAsync();
        cancellationToken.ThrowIfCancellationRequested();

        return stores
            .Select(store => new WarehouseStorePriceSyncTargetStoreDto
            {
                StoreCode = NormalizeCode(store.StoreCode) ?? string.Empty,
                StoreName = NormalizeText(store.StoreName) ?? string.Empty,
            })
            .Where(store => store.StoreCode.Length > 0)
            .GroupBy(store => store.StoreCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(store => store.StoreCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<int> GetAllProductCountAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = await _context.Db.Queryable<WarehouseProduct>()
            .Where(product => !product.IsDeleted)
            .CountAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return count;
    }

    public async Task<ApiResponse<WarehouseStorePriceSyncResultDto>> ExecuteAsync(
        WarehouseStorePriceSyncRequestDto request,
        string updatedBy,
        CancellationToken cancellationToken = default
    )
    {
        var result = new WarehouseStorePriceSyncResultDto();
        var normalizedRequest = NormalizeRequest(request);
        var validation = ValidateScope(normalizedRequest);
        if (validation != null)
        {
            result.Errors.Add(validation);
            return Failure(validation.Message, validation.Code, result);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var targetResolution = await ResolveLocalTargetStoresAsync(
            normalizedRequest.TargetStoreCodes,
            cancellationToken
        );
        result.TargetStoreCodes = targetResolution.CanonicalCodes.ToList();
        result.TargetStoreCount = result.TargetStoreCodes.Count;
        if (targetResolution.Errors.Count > 0)
        {
            result.Errors.AddRange(targetResolution.Errors);
            return Failure("包含无效的本地目标分店，未写入价格", "INVALID_TARGET_STORES", result);
        }

        var selection = await SelectWarehouseProductsAsync(normalizedRequest, cancellationToken);
        result.RequestedProductCount = selection.RequestedCount;
        result.Errors.AddRange(selection.Errors);
        var eligibleProducts = selection.Products
            .Where(product => product.ImportPrice.HasValue && product.OEMPrice.HasValue)
            .ToList();
        result.EligibleProductCount = eligibleProducts.Count;
        // 未找到的指定商品已经作为失败明细返回，不再重复计入“缺价跳过”。
        result.SkippedProductCount = selection.Products.Count - result.EligibleProductCount;

        foreach (var product in selection.Products.Where(product =>
            !product.ImportPrice.HasValue || !product.OEMPrice.HasValue
        ))
        {
            var missingFields = new List<string>(2);
            if (!product.ImportPrice.HasValue)
            {
                missingFields.Add(nameof(WarehouseProduct.ImportPrice));
            }
            if (!product.OEMPrice.HasValue)
            {
                missingFields.Add(nameof(WarehouseProduct.OEMPrice));
            }

            result.Errors.Add(new WarehouseStorePriceSyncErrorDto
            {
                Stage = "ProductSelection",
                ProductCode = product.ProductCode,
                Code = "MISSING_PRICE",
                Message = $"缺少字段：{string.Join(", ", missingFields)}，已跳过该商品",
            });
        }

        if (eligibleProducts.Count == 0)
        {
            result.Errors.Add(new WarehouseStorePriceSyncErrorDto
            {
                Stage = "ProductSelection",
                Code = "NO_ELIGIBLE_PRODUCTS",
                Message = "没有 ImportPrice 和 OEMPrice 均有效的仓库商品",
            });
            return Failure("没有可同步的仓库商品", "NO_ELIGIBLE_PRODUCTS", result);
        }

        var hqTargetStoreCodes = result.TargetStoreCodes;
        if (normalizedRequest.SyncToHq)
        {
            var hqValidation = await _hqSyncService.ValidateWarehouseStorePriceTargetsAsync(
                result.TargetStoreCodes,
                cancellationToken
            );
            var hqValidationResult = ExtractResult(hqValidation);
            if (!hqValidation.Success || hqValidationResult == null)
            {
                if (hqValidationResult != null)
                {
                    result.Errors.AddRange(hqValidationResult.Errors);
                }
                if (!result.Errors.Any(error => error.Stage == "HqValidation"))
                {
                    result.Errors.Add(new WarehouseStorePriceSyncErrorDto
                    {
                        Stage = "HqValidation",
                        Code = hqValidation.ErrorCode ?? "HQ_VALIDATION_FAILED",
                        Message = hqValidation.Message,
                    });
                }

                return Failure(
                    hqValidation.Message,
                    hqValidation.ErrorCode ?? "HQ_VALIDATION_FAILED",
                    result
                );
            }

            hqTargetStoreCodes = hqValidationResult.CanonicalTargetStoreCodes;
        }

        var effectiveUpdatedBy = NormalizeText(updatedBy) ?? "system";
        var now = DateTime.UtcNow;
        var localDb = _context.Db;
        await localDb.Ado.BeginTranAsync();
        try
        {
            // 全量必须持有总闸；有限商品范围只按稳定顺序持有对应主商品锁。
            var lockScope = normalizedRequest.ApplyToAllProducts
                ? await SetChildPurchasePriceMutationLock.AcquireAllAsync(localDb)
                : await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                    localDb,
                    eligibleProducts.Select(product => product.ProductCode)
                );
            // 锁内重新读取仓库价格，禁止把等待业务锁之前的旧快照写入目标门店。
            var lockedSelection = await SelectWarehouseProductsAsync(
                normalizedRequest,
                cancellationToken
            );
            var lockedEligibleProducts = lockedSelection.Products
                .Where(product => product.ImportPrice.HasValue && product.OEMPrice.HasValue)
                .ToList();
            if (!normalizedRequest.ApplyToAllProducts)
            {
                var expectedCodes = SetChildPurchasePriceMutationLock.NormalizeProductCodes(
                    eligibleProducts.Select(product => product.ProductCode)
                );
                var lockedCodes = SetChildPurchasePriceMutationLock.NormalizeProductCodes(
                    lockedEligibleProducts.Select(product => product.ProductCode)
                );
                if (!expectedCodes.SequenceEqual(lockedCodes, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        "等待商品锁期间仓库商品或价格状态已变化，请重新读取后重试"
                    );
                }
            }

            eligibleProducts = lockedEligibleProducts;
            var productMetadata = await LoadProductMetadataAsync(
                eligibleProducts,
                cancellationToken
            );
            // 后台 job 没有 HttpContext；保护显式审计字段，避免全局 AOP 把操作人覆盖成 System。
            using var auditScope = SqlSugarAuditScope.PreserveExplicitAuditFields();
            var overwrittenPrices = new List<StorePriceOverwrite>();
            var writeCounts = await UpsertLocalPricesAsync(
                localDb,
                eligibleProducts,
                productMetadata,
                result.TargetStoreCodes,
                effectiveUpdatedBy,
                now,
                cancellationToken,
                overwrittenPrices
            );
            await RecordStorePriceOverwritesAsync(overwrittenPrices, effectiveUpdatedBy);
            result.LocalCreatedCount = writeCounts.Created;
            result.LocalUpdatedCount = writeCounts.Updated;
            // 只重算本请求指定的门店-主商品组；结构或主成本异常会由统一服务抛出并回滚本次写入。
            await new SetChildPurchasePriceService(localDb).RecalculateStoreGroupsLockedAsync(
                lockScope,
                result.TargetStoreCodes.SelectMany(storeCode => eligibleProducts.Select(product =>
                    (StoreCode: (string?)storeCode, ProductCode: (string?)product.ProductCode)
                )),
                effectiveUpdatedBy
            );
            await localDb.Ado.CommitTranAsync();
            result.LocalCommitted = true;
        }
        catch (Exception ex)
        {
            try
            {
                await localDb.Ado.RollbackTranAsync();
            }
            catch (Exception rollbackEx)
            {
                _logger.LogError(rollbackEx, "仓库价格同步本地事务回滚失败");
            }

            var errorCode = SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
                ? SetChildPurchasePriceMutationLock.BusyErrorCode
                : "LOCAL_WRITE_FAILED";
            var errorMessage = errorCode == SetChildPurchasePriceMutationLock.BusyErrorCode
                ? "套装子项成本正在被其他操作更新，请稍后重试"
                : "本地分店价格写入失败";
            _logger.LogError(ex, "仓库价格同步本地写入失败: ErrorCode={ErrorCode}", errorCode);
            result.Errors.Add(new WarehouseStorePriceSyncErrorDto
            {
                Stage = "LocalWrite",
                Code = errorCode,
                Message = errorMessage,
            });
            return Failure(errorMessage, errorCode, result);
        }

        if (!normalizedRequest.SyncToHq)
        {
            return ApiResponse<WarehouseStorePriceSyncResultDto>.OK(
                result,
                "仓库商品价格已同步到本地分店"
            );
        }

        ApiResponse<WarehouseStorePriceHqSyncResultDto> hqResponse;
        try
        {
            hqResponse = await _hqSyncService.SyncWarehouseStorePricesAsync(
                new WarehouseStorePriceHqSyncRequestDto
                {
                    Products = eligibleProducts
                        .Select(product => new WarehouseStorePriceHqProductDto
                        {
                            ProductCode = product.ProductCode,
                            ImportPrice = product.ImportPrice!.Value,
                            OemPrice = product.OEMPrice!.Value,
                        })
                        .ToList(),
                    TargetStoreCodes = hqTargetStoreCodes.ToList(),
                    UpdatedBy = effectiveUpdatedBy,
                },
                cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            // 本地事务已经提交，HQ 阶段取消只能报告部分成功，不能伪装成本地回滚。
            result.HqSucceeded = false;
            result.Errors.Add(new WarehouseStorePriceSyncErrorDto
            {
                Stage = "HqWrite",
                Code = "HQ_WRITE_CANCELLED",
                Message = "HQ 价格同步已取消",
            });
            return Failure("HQ 价格同步已取消，本地写入已保留", "HQ_WRITE_CANCELLED", result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "仓库价格同步 HQ 阶段运行失败，本地写入已保留");
            result.HqSucceeded = false;
            result.Errors.Add(new WarehouseStorePriceSyncErrorDto
            {
                Stage = "HqWrite",
                Code = "HQ_WRITE_EXCEPTION",
                Message = "HQ 价格同步失败",
            });
            return Failure("HQ 价格同步失败，本地写入已保留", "HQ_WRITE_EXCEPTION", result);
        }
        var hqResult = ExtractResult(hqResponse);
        if (hqResult != null)
        {
            result.HqCreatedCount = hqResult.HqCreatedCount;
            result.HqUpdatedCount = hqResult.HqUpdatedCount;
            result.HqProvisionedProductCount = hqResult.HqProvisionedProductCount;
            result.Errors.AddRange(hqResult.Errors);
        }

        if (!hqResponse.Success)
        {
            result.HqSucceeded = false;
            if (hqResult == null)
            {
                result.Errors.Add(new WarehouseStorePriceSyncErrorDto
                {
                    Stage = "HqWrite",
                    Code = hqResponse.ErrorCode ?? "HQ_WRITE_FAILED",
                    Message = hqResponse.Message,
                });
            }

            return Failure(
                hqResponse.Message,
                hqResponse.ErrorCode ?? "HQ_WRITE_FAILED",
                result
            );
        }

        result.HqSucceeded = true;
        return ApiResponse<WarehouseStorePriceSyncResultDto>.OK(
            result,
            "仓库商品价格已同步到本地分店和 HQ"
        );
    }

    private static WarehouseStorePriceSyncRequestDto NormalizeRequest(
        WarehouseStorePriceSyncRequestDto? request
    )
    {
        request ??= new WarehouseStorePriceSyncRequestDto();
        return new WarehouseStorePriceSyncRequestDto
        {
            ApplyToAllProducts = request.ApplyToAllProducts,
            ProductCodes = NormalizeCodes(request.ProductCodes),
            TargetStoreCodes = NormalizeCodes(request.TargetStoreCodes),
            // HQ 断开后按“只写本地”执行，旧页面仍勾选同步 HQ 时也不会报错。
            SyncToHq = request.SyncToHq && LegacyDatabaseGate.HqEnabled,
        };
    }

    private static WarehouseStorePriceSyncErrorDto? ValidateScope(
        WarehouseStorePriceSyncRequestDto request
    )
    {
        if (
            (request.ApplyToAllProducts && request.ProductCodes.Count > 0)
            || (!request.ApplyToAllProducts && request.ProductCodes.Count == 0)
        )
        {
            return new WarehouseStorePriceSyncErrorDto
            {
                Stage = "RequestValidation",
                Code = "INVALID_PRODUCT_SCOPE",
                Message = "全量处理时 ProductCodes 必须为空，指定处理时 ProductCodes 必须非空",
            };
        }

        return request.TargetStoreCodes.Count == 0
            ? new WarehouseStorePriceSyncErrorDto
            {
                Stage = "RequestValidation",
                Code = "TARGET_STORES_REQUIRED",
                Message = "目标分店不能为空",
            }
            : null;
    }

    private async Task<TargetStoreResolution> ResolveLocalTargetStoresAsync(
        IReadOnlyCollection<string> requestedCodes,
        CancellationToken cancellationToken
    )
    {
        var stores = await GetTargetStoresAsync(cancellationToken);
        var canonicalByCode = stores.ToDictionary(
            store => store.StoreCode,
            store => store.StoreCode,
            StringComparer.OrdinalIgnoreCase
        );
        var canonicalCodes = new List<string>();
        var errors = new List<WarehouseStorePriceSyncErrorDto>();
        foreach (var requestedCode in requestedCodes)
        {
            if (canonicalByCode.TryGetValue(requestedCode, out var canonicalCode))
            {
                canonicalCodes.Add(canonicalCode);
                continue;
            }

            errors.Add(new WarehouseStorePriceSyncErrorDto
            {
                Stage = "LocalValidation",
                StoreCode = requestedCode,
                Code = "INVALID_TARGET_STORE",
                Message = $"本地分店不存在、已停用或已删除: {requestedCode}",
            });
        }

        return new TargetStoreResolution(canonicalCodes, errors);
    }

    private async Task<ProductSelection> SelectWarehouseProductsAsync(
        WarehouseStorePriceSyncRequestDto request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ApplyToAllProducts)
        {
            var allProducts = await _context.Db.Queryable<WarehouseProduct>()
                .Where(product => !product.IsDeleted)
                .ToListAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return new ProductSelection(allProducts.Count, allProducts, new());
        }

        var matchedProducts = new List<WarehouseProduct>();
        var normalizedCodes = request.ProductCodes
            .Select(code => code.ToUpperInvariant())
            .ToList();
        foreach (var codeBatch in normalizedCodes.Chunk(QueryBatchSize))
        {
            var batch = codeBatch.ToList();
            matchedProducts.AddRange(await _context.Db.Queryable<WarehouseProduct>()
                .Where(product =>
                    !product.IsDeleted
                    && batch.Contains(SqlFunc.ToUpper(product.ProductCode.Trim()))
                )
                .ToListAsync());
            cancellationToken.ThrowIfCancellationRequested();
        }

        var productByCode = matchedProducts
            .GroupBy(product => product.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var selected = new List<WarehouseProduct>();
        var errors = new List<WarehouseStorePriceSyncErrorDto>();
        foreach (var code in request.ProductCodes)
        {
            if (productByCode.TryGetValue(code, out var product))
            {
                selected.Add(product);
                continue;
            }

            errors.Add(new WarehouseStorePriceSyncErrorDto
            {
                Stage = "ProductSelection",
                ProductCode = code,
                Code = "PRODUCT_NOT_FOUND",
                Message = "未找到未删除的仓库商品",
            });
        }

        return new ProductSelection(request.ProductCodes.Count, selected, errors);
    }

    private async Task<Dictionary<string, Product>> LoadProductMetadataAsync(
        IReadOnlyCollection<WarehouseProduct> warehouseProducts,
        CancellationToken cancellationToken
    )
    {
        if (warehouseProducts.Count == 0)
        {
            return new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
        }

        var codes = warehouseProducts
            .Select(product => product.ProductCode.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var products = new List<Product>();
        foreach (var codeBatch in codes.Chunk(QueryBatchSize))
        {
            var batch = codeBatch.ToList();
            products.AddRange(await _context.Db.Queryable<Product>()
                .Where(product =>
                    product.ProductCode != null
                    && batch.Contains(SqlFunc.ToUpper(product.ProductCode.Trim()))
                    && !product.IsDeleted
                )
                .ToListAsync());
            cancellationToken.ThrowIfCancellationRequested();
        }

        return products
            .Where(product => !string.IsNullOrWhiteSpace(product.ProductCode))
            .GroupBy(product => product.ProductCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(product => product.UpdatedAt ?? product.CreatedAt)
                    .ThenByDescending(product => product.CreatedAt)
                    .First(),
                StringComparer.OrdinalIgnoreCase
            );
    }

    private static async Task<LocalWriteCounts> UpsertLocalPricesAsync(
        ISqlSugarClient db,
        IReadOnlyCollection<WarehouseProduct> warehouseProducts,
        IReadOnlyDictionary<string, Product> productMetadata,
        IReadOnlyCollection<string> targetStoreCodes,
        string updatedBy,
        DateTime now,
        CancellationToken cancellationToken,
        ICollection<StorePriceOverwrite>? overwrittenPrices = null
    )
    {
        if (warehouseProducts.Count == 0)
        {
            return new LocalWriteCounts(0, 0);
        }

        var productCodes = warehouseProducts
            .Select(product => product.ProductCode.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var normalizedTargetCodes = targetStoreCodes
            .Select(code => code.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existingRows = new List<StoreRetailPrice>();
        foreach (var codeBatch in productCodes.Chunk(QueryBatchSize))
        {
            var batch = codeBatch.ToList();
            foreach (var storeBatch in normalizedTargetCodes.Chunk(QueryBatchSize))
            {
                var stores = storeBatch.ToList();
                existingRows.AddRange(await db.Queryable<StoreRetailPrice>()
                    .Where(row =>
                        row.ProductCode != null
                        && row.StoreCode != null
                        && batch.Contains(SqlFunc.ToUpper(row.ProductCode.Trim()))
                        && stores.Contains(SqlFunc.ToUpper(row.StoreCode.Trim()))
                        && !row.IsDeleted
                    )
                    .ToListAsync());
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        var targetSet = targetStoreCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingByKey = existingRows
            .Where(row =>
                !string.IsNullOrWhiteSpace(row.StoreCode)
                && !string.IsNullOrWhiteSpace(row.ProductCode)
                && targetSet.Contains(row.StoreCode!)
            )
            .GroupBy(row => BuildLocalBusinessKey(row.StoreCode, row.ProductCode))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(row => row.UpdatedAt ?? row.CreatedAt)
                    .ThenByDescending(row => row.UUID)
                    .First(),
                StringComparer.OrdinalIgnoreCase
            );
        var updates = new List<StoreRetailPrice>();
        var inserts = new List<StoreRetailPrice>();

        foreach (var warehouseProduct in warehouseProducts)
        {
            productMetadata.TryGetValue(warehouseProduct.ProductCode, out var product);
            var supplierCode = NormalizeCode(product?.LocalSupplierCode) ?? DefaultSupplierCode;
            foreach (var storeCode in targetStoreCodes)
            {
                var key = BuildLocalBusinessKey(storeCode, warehouseProduct.ProductCode);
                if (existingByKey.TryGetValue(key, out var existing))
                {
                    // 关键位置：已有行严格只改四个业务字段和审计字段，保留库存、状态及映射信息。
                    // 成本已正确时不写审计字段，避免无效同步刷新更新时间和操作人。
                    if (!HasLocalPriceDifference(existing, warehouseProduct))
                    {
                        continue;
                    }

                    // 覆盖前的零售价/折扣就是货架标签上的价格，下面会就地改写，必须先留存。
                    overwrittenPrices?.Add(new StorePriceOverwrite(
                        existing.StoreCode!,
                        existing.ProductCode!,
                        existing.StoreRetailPriceValue,
                        existing.DiscountRate
                    ));
                    existing.PurchasePrice = warehouseProduct.ImportPrice;
                    existing.StoreRetailPriceValue = warehouseProduct.OEMPrice;
                    existing.DiscountRate = 0m;
                    existing.IsAutoPricing = false;
                    existing.UpdatedAt = now;
                    existing.UpdatedBy = updatedBy;
                    updates.Add(existing);
                    continue;
                }

                var created = new StoreRetailPrice
                {
                    UUID = UuidHelper.GenerateUuid7(),
                    StoreCode = storeCode,
                    ProductCode = warehouseProduct.ProductCode,
                    StoreProductCode = storeCode + warehouseProduct.ProductCode,
                    SupplierCode = supplierCode,
                    PurchasePrice = warehouseProduct.ImportPrice,
                    StoreRetailPriceValue = warehouseProduct.OEMPrice,
                    DiscountRate = 0m,
                    IsAutoPricing = false,
                    IsSpecialProduct = product?.IsSpecialProduct ?? false,
                    IsActive = warehouseProduct.IsActive,
                    IsDeleted = false,
                    CreatedAt = now,
                    CreatedBy = updatedBy,
                    UpdatedAt = now,
                    UpdatedBy = updatedBy,
                };
                inserts.Add(created);
                existingByKey[key] = created;
            }
        }

        if (updates.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await db.Updateable(updates)
                .UpdateColumns(row => new
                {
                    row.PurchasePrice,
                    row.StoreRetailPriceValue,
                    row.DiscountRate,
                    row.IsAutoPricing,
                    row.UpdatedAt,
                    row.UpdatedBy,
                })
                .PageSize(WriteBatchSize)
                .ExecuteCommandAsync();
        }

        if (inserts.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await db.Insertable(inserts)
                .PageSize(WriteBatchSize)
                .ExecuteCommandAsync();
        }

        return new LocalWriteCounts(inserts.Count, updates.Count);
    }

    /// <summary>
    /// 与本次同步共用同一连接和事务：同步回滚时通知一并回滚。
    /// 登记失败只记日志，不影响价格同步；移动端列表与定时对账会兜底。
    /// </summary>
    private async Task RecordStorePriceOverwritesAsync(
        IReadOnlyCollection<StorePriceOverwrite> overwrites,
        string updatedBy
    )
    {
        if (_priceTaskService == null || overwrites.Count == 0)
        {
            return;
        }

        try
        {
            await _priceTaskService.RecordStoreOverwritesAsync(
                overwrites,
                new PriceTaskInitiator(updatedBy, "WarehouseAutoSync")
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "登记分店待换标签通知失败: Count={Count}", overwrites.Count);
        }
    }

    private static bool HasLocalPriceDifference(
        StoreRetailPrice existing,
        WarehouseProduct warehouseProduct
    ) =>
        existing.PurchasePrice != warehouseProduct.ImportPrice
        || existing.StoreRetailPriceValue != warehouseProduct.OEMPrice
        || existing.DiscountRate != 0m
        || existing.IsAutoPricing;

    private static ApiResponse<WarehouseStorePriceSyncResultDto> Failure(
        string message,
        string code,
        WarehouseStorePriceSyncResultDto result
    )
    {
        return new ApiResponse<WarehouseStorePriceSyncResultDto>
        {
            Success = false,
            Message = message,
            ErrorCode = code,
            Details = result,
        };
    }

    private static T? ExtractResult<T>(ApiResponse<T> response)
        where T : class
    {
        return response.Data ?? response.Details as T;
    }

    private static List<string> NormalizeCodes(IEnumerable<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Select(NormalizeCode)
            .Where(value => value != null)
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildLocalBusinessKey(string? storeCode, string? productCode)
    {
        return string.Join(
            "|",
            NormalizeCode(storeCode)?.ToUpperInvariant() ?? string.Empty,
            NormalizeCode(productCode)?.ToUpperInvariant() ?? string.Empty
        );
    }

    private static string? NormalizeCode(string? value) => NormalizeText(value);

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record TargetStoreResolution(
        List<string> CanonicalCodes,
        List<WarehouseStorePriceSyncErrorDto> Errors
    );

    private sealed record ProductSelection(
        int RequestedCount,
        List<WarehouseProduct> Products,
        List<WarehouseStorePriceSyncErrorDto> Errors
    );

    private sealed record LocalWriteCounts(int Created, int Updated);
}
