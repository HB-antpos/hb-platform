using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 商品 HQ 同步统一实现。
    /// 旧入口只委托到这里，避免继续使用 Product/价格/分店多码混合同步链路。
    /// </summary>
    public partial class ProductHqSyncService : IProductHqSyncService
    {
        private const string ShadowTableName = "Product_Shadow";
        private const int HqReadBatchSize = 5000;
        private const int HqCodeBatchSize = 500;
        private const int WriteBatchSize = 1000;
        private const int HqWriteBatchSize = 40;
        private const string HqFieldItemNumber = "itemNumber";
        private const string HqFieldBarcode = "barcode";
        private const string HqFieldProductName = "productName";
        private const string HqFieldEnglishName = "englishName";
        private const string HqFieldProductType = "productType";
        private const string HqFieldImage = "image";
        private const string HqFieldPurchasePrice = "purchasePrice";
        private const string HqFieldRetailPrice = "retailPrice";
        private const string HqFieldMiddlePackQuantity = "middlePackQuantity";
        private const string HqFieldSupplierCode = "supplierCode";
        private const string HqFieldStorePurchasePrice = "storePurchasePrice";
        private const string HqFieldStoreRetailPrice = "storeRetailPrice";
        private const string HqFieldInventoryDomesticPrice = "inventoryDomesticPrice";
        private const string HqFieldInventoryImportPrice = "inventoryImportPrice";
        private const string HqFieldInventoryOemPrice = "inventoryOemPrice";
        private const string HqFieldProductSetCodes = "productSetCodes";
        private const string HqFieldStoreMultiCodes = "storeMultiCodes";
        private static readonly SemaphoreSlim SyncLock = new(1, 1);

        private readonly SqlSugarContext _localContext;
        private readonly HqSqlSugarContext _hqContext;
        private readonly IMapper _mapper;
        private readonly ILogger<ProductHqSyncService> _logger;
        private readonly IWarehouseProductChangeHistoryService _changeHistoryService;
        private readonly ICurrentUserService _currentUserService;

        public ProductHqSyncService(
            SqlSugarContext localContext,
            HqSqlSugarContext hqContext,
            IMapper mapper,
            ILogger<ProductHqSyncService> logger,
            IWarehouseProductChangeHistoryService changeHistoryService,
            ICurrentUserService currentUserService
        )
        {
            _localContext = localContext;
            _hqContext = hqContext;
            _mapper = mapper;
            _logger = logger;
            _changeHistoryService = changeHistoryService;
            _currentUserService = currentUserService;
        }

        private async Task<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>
            CaptureProductSnapshotsAsync(
                IEnumerable<string> productCodes,
                CancellationToken cancellationToken = default
            )
        {
            return await _changeHistoryService.CaptureSnapshotsAsync(
                productCodes,
                cancellationToken
            );
        }

        private async Task RecordProductChangesAsync(
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> beforeSnapshots,
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> afterSnapshots,
            string action,
            string source,
            Guid batchGuid,
            DateTime occurredAtUtc,
            string? actorUserGuid = null,
            string? actorName = null,
            CancellationToken cancellationToken = default
        )
        {
            // 未显式传入服务端身份时读取当前请求；真正计划任务通过 actorName=System 显式标记。
            var resolvedActorName = string.IsNullOrWhiteSpace(actorName)
                ? _currentUserService.GetCurrentUsername()
                : actorName.Trim();
            if (string.IsNullOrWhiteSpace(resolvedActorName))
            {
                resolvedActorName = "System";
            }
            var resolvedActorGuid = string.IsNullOrWhiteSpace(actorUserGuid)
                ? _currentUserService.GetCurrentUserGuid()
                : actorUserGuid.Trim();
            var isSystem = string.IsNullOrWhiteSpace(resolvedActorGuid)
                && string.Equals(
                    resolvedActorName,
                    "System",
                    StringComparison.OrdinalIgnoreCase
                );
            await _changeHistoryService.RecordChangesAsync(
                beforeSnapshots,
                afterSnapshots,
                new WarehouseProductChangeHistoryContextDto
                {
                    Action = action,
                    Source = source,
                    BatchGuid = batchGuid,
                    ActorUserGuid = string.IsNullOrWhiteSpace(resolvedActorGuid)
                        ? null
                        : resolvedActorGuid,
                    ActorName = resolvedActorName,
                    ActorType = isSystem ? "System" : "User",
                    OccurredAtUtc = occurredAtUtc,
                },
                cancellationToken
            );
        }

        private sealed class PushToHqUpdateFieldSelection
        {
            private readonly HashSet<string>? _fields;

            public PushToHqUpdateFieldSelection(List<string>? fields)
            {
                var normalized = (fields ?? new List<string>())
                    .Select(NormalizeCode)
                    .Where(field => !string.IsNullOrWhiteSpace(field))
                    .Select(field => field!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                _fields = normalized.Count == 0 ? null : normalized;
            }

            public bool IsAll => _fields == null;

            public bool Has(string field) => IsAll || _fields!.Contains(field);

            public bool HasAny(params string[] fields) => IsAll || fields.Any(field => _fields!.Contains(field));

            /// <summary>
            /// 是否包含分店维度字段（分店价格、分店供应商列、分店一品多码）。
            /// 缺省/空字段列表等于全字段，同样视为包含分店维度。
            /// </summary>
            public bool HasStoreDimensionFields => HasAny(
                HqFieldSupplierCode,
                HqFieldStorePurchasePrice,
                HqFieldStoreRetailPrice,
                HqFieldStoreMultiCodes
            );
        }

        public Task<ApiResponse<HqProductSyncResult>> SyncFullAsync()
        {
            return SyncFullAsync(null, null);
        }

        public async Task<ApiResponse<HqProductSyncResult>> SyncFullAsync(
            string? actorUserGuid,
            string? actorName
        )
        {
            if (!await SyncLock.WaitAsync(0))
            {
                return ApiResponse<HqProductSyncResult>.Error(
                    "已有商品HQ同步任务正在执行，请稍后再试",
                    SetChildPurchasePriceMutationLock.BusyErrorCode
                );
            }

            var startedAt = DateTime.UtcNow;
            var result = new HqProductSyncResult();
            var db = _localContext.Db;
            var originalTimeout = db.Ado.CommandTimeOut;
            db.Ado.CommandTimeOut = 1800;
            var transactionStarted = false;
            var auditBatchGuid = Guid.NewGuid();
            var auditOccurredAtUtc = DateTime.UtcNow;

            try
            {
                _hqContext.CheckConnection();
                var activeHqProductCodes = (await QueryActiveHqProductsAsync())
                    .Select(row => NormalizeCode(row.H商品编码))
                    .Where(code => code != null)
                    .Select(code => code!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var localActiveProductCodes = (await db.Queryable<Product>()
                        .Where(row => !row.IsDeleted && row.ProductCode != null)
                        .Select(row => row.ProductCode)
                        .ToListAsync())
                    .Select(NormalizeCode)
                    .Where(code => code != null)
                    .Select(code => code!);
                // 全量影子表交换会停用 HQ 已移除的本地商品，审计集合必须包含同步前本地编码。
                var auditProductCodes = activeHqProductCodes
                    .Concat(localActiveProductCodes)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                db.Ado.BeginTran();
                transactionStarted = true;
                var childCostLockScope = await SetChildPurchasePriceMutationLock.AcquireAllAsync(db);
                var beforeSnapshots = await CaptureProductSnapshotsAsync(auditProductCodes);
                if (db.CurrentConnectionConfig.DbType == DbType.SqlServer)
                {
                    await SyncFullWithShadowAsync(db, result);
                }
                else
                {
                    // 非 SQL Server 测试环境没有存储过程，仍保持“只处理 Product”的行为语义。
                    await SyncFullDirectAsync(db, result);
                }

                var activeLocalProductCodeSet = (await db.Queryable<Product>()
                        .Where(row => row.IsActive && !row.IsDeleted && row.ProductCode != null)
                        .Select(row => row.ProductCode)
                        .ToListAsync())
                    .Select(NormalizeCode)
                    .Where(code => code != null)
                    .Select(code => code!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var setParentProductCodes = (await db.Queryable<ProductSetCode>()
                        // Type1 按比例分摊，Type2 直接继承父成本；两类都必须进入同事务重算。
                        .Where(row =>
                            (row.SetType == 1 || row.SetType == 2)
                            && row.IsActive
                            && !row.IsDeleted
                        )
                        .Select(row => row.ProductCode)
                        .ToListAsync())
                    .Select(NormalizeCode)
                    .Where(code => code != null)
                    .Select(code => code!)
                    .Where(activeLocalProductCodeSet.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (setParentProductCodes.Count > 0)
                {
                    var recalculation = await new SetChildPurchasePriceService(
                        db
                    ).RecalculateLockedAsync(
                        childCostLockScope,
                        setParentProductCodes,
                        null,
                        ResolveSetChildPurchasePriceActor(actorName)
                    );
                    EnsureSetChildPurchasePriceRecalculated(
                        recalculation,
                        setParentProductCodes
                    );
                }

                var afterSnapshots = await CaptureProductSnapshotsAsync(auditProductCodes);
                await RecordProductChangesAsync(
                    beforeSnapshots,
                    afterSnapshots,
                    "Update",
                    "ProductHqSync.Full",
                    auditBatchGuid,
                    auditOccurredAtUtc,
                    actorUserGuid,
                    actorName
                );
                db.Ado.CommitTran();
                transactionStarted = false;

                result.DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                return ApiResponse<HqProductSyncResult>.OK(result, "商品HQ全量同步完成");
            }
            catch (Exception ex)
            {
                if (transactionStarted)
                {
                    try
                    {
                        db.Ado.RollbackTran();
                    }
                    catch (Exception rollbackException)
                    {
                        _logger.LogError(rollbackException, "商品HQ全量同步回滚失败");
                    }
                }
                _logger.LogError(ex, "商品HQ全量同步失败");
                result.DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                result.Errors.Add(ex.Message);
                // 套装成本锁冲突必须作为可重试的 busy 语义向上透传，不能降级成普通同步失败。
                var errorCode = SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
                    ? SetChildPurchasePriceMutationLock.BusyErrorCode
                    : "PRODUCT_HQ_FULL_SYNC_ERROR";
                return ApiResponse<HqProductSyncResult>.Error(
                    $"商品HQ全量同步失败: {ex.Message}",
                    errorCode,
                    result
                );
            }
            finally
            {
                db.Ado.CommandTimeOut = originalTimeout;
                SyncLock.Release();
            }
        }

        public Task<ApiResponse<HqProductSyncResult>> SyncIncrementalAsync(DateTime? startDate = null)
        {
            return SyncIncrementalAsync(startDate, null, null);
        }

        public async Task<ApiResponse<HqProductSyncResult>> SyncIncrementalAsync(
            DateTime? startDate,
            string? actorUserGuid,
            string? actorName
        )
        {
            if (!await SyncLock.WaitAsync(0))
            {
                return ApiResponse<HqProductSyncResult>.Error(
                    "已有商品HQ同步任务正在执行，请稍后再试",
                    SetChildPurchasePriceMutationLock.BusyErrorCode
                );
            }

            var startedAt = DateTime.UtcNow;
            var result = new HqProductSyncResult();
            var db = _localContext.Db;
            var originalTimeout = db.Ado.CommandTimeOut;
            db.Ado.CommandTimeOut = 1800;

            try
            {
                _hqContext.CheckConnection();
                var effectiveStart = startDate ?? DateTime.UtcNow.AddDays(-30);
                var auditBatchGuid = Guid.NewGuid();
                var auditOccurredAtUtc = DateTime.UtcNow;

                db.Ado.BeginTran();
                try
                {
                    // 增量同步没有可靠的本地商品过滤边界，使用全局闸锁保证锁内重读与重算一致。
                    var childCostLockScope = await SetChildPurchasePriceMutationLock.AcquireAllAsync(db);
                    var changedHqProductCodes = (await _hqContext.Db.Queryable<DIC_商品信息字典表>()
                            .Where(row =>
                                row.H使用状态 == true
                                && !string.IsNullOrEmpty(row.H商品编码)
                                && row.FGC_LastModifyDate >= effectiveStart
                            )
                            .Select(row => row.H商品编码)
                            .ToListAsync())
                        .Select(NormalizeCode)
                        .Where(code => code != null)
                        .Select(code => code!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var activeHqCodeSet = (await _hqContext.Db.Queryable<DIC_商品信息字典表>()
                            .Where(row => row.H使用状态 == true && !string.IsNullOrEmpty(row.H商品编码))
                            .Select(row => row.H商品编码)
                            .ToListAsync())
                        .Select(NormalizeCode)
                        .Where(code => code != null)
                        .Select(code => code!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var staleLocalProductCodes = (await db.Queryable<Product>()
                            .Where(row => !row.IsDeleted && row.ProductCode != null)
                            .Select(row => row.ProductCode)
                            .ToListAsync())
                        .Select(NormalizeCode)
                        .Where(code => code != null)
                        .Select(code => code!)
                        .Where(code => !activeHqCodeSet.Contains(code));
                    var auditProductCodes = changedHqProductCodes
                        .Concat(staleLocalProductCodes)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var beforeSnapshots = await CaptureProductSnapshotsAsync(auditProductCodes);
                    var productSnapshot = await SyncProductsIncrementalCoreAsync(
                        db,
                        effectiveStart,
                        result
                    );
                    await SyncProductSetCodesIncrementalCoreAsync(
                        db,
                        effectiveStart,
                        productSnapshot.ActiveProductCodes,
                        productSnapshot.SoftDeletedProductCodes,
                        result
                    );
                    if (changedHqProductCodes.Count > 0)
                    {
                        var recalculation = await new SetChildPurchasePriceService(db)
                            .RecalculateLockedAsync(
                                childCostLockScope,
                                changedHqProductCodes,
                                null,
                                ResolveSetChildPurchasePriceActor(actorName)
                            );
                        EnsureSetChildPurchasePriceRecalculated(
                            recalculation,
                            changedHqProductCodes
                        );
                    }
                    var afterSnapshots = await CaptureProductSnapshotsAsync(auditProductCodes);
                    await RecordProductChangesAsync(
                        beforeSnapshots,
                        afterSnapshots,
                        "Update",
                        "ProductHqSync.Incremental",
                        auditBatchGuid,
                        auditOccurredAtUtc,
                        actorUserGuid,
                        actorName
                    );
                    db.Ado.CommitTran();
                }
                catch
                {
                    db.Ado.RollbackTran();
                    throw;
                }

                result.DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                return ApiResponse<HqProductSyncResult>.OK(result, "商品HQ增量同步完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "商品HQ增量同步失败");
                result.DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                result.Errors.Add(ex.Message);
                // 与全量同步一致，锁竞争由调用方决定重试，不应被标记为不可重试的业务失败。
                var errorCode = SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
                    ? SetChildPurchasePriceMutationLock.BusyErrorCode
                    : "PRODUCT_HQ_INCREMENTAL_SYNC_ERROR";
                return ApiResponse<HqProductSyncResult>.Error(
                    $"商品HQ增量同步失败: {ex.Message}",
                    errorCode,
                    result
                );
            }
            finally
            {
                db.Ado.CommandTimeOut = originalTimeout;
                SyncLock.Release();
            }
        }

        public async Task<ApiResponse<HqProductSyncResult>> SyncSelectedFromHqAsync(
            List<string> productCodes
        )
        {
            if (productCodes == null || productCodes.Count == 0)
            {
                return ApiResponse<HqProductSyncResult>.Error(
                    "商品编码列表不能为空",
                    "PRODUCT_HQ_SELECTED_SYNC_EMPTY_CODES"
                );
            }

            if (!await SyncLock.WaitAsync(0))
            {
                return ApiResponse<HqProductSyncResult>.Error(
                    "已有商品HQ同步任务正在执行，请稍后再试",
                    SetChildPurchasePriceMutationLock.BusyErrorCode
                );
            }

            var startedAt = DateTime.UtcNow;
            var result = new HqProductSyncResult();
            var db = _localContext.Db;
            var originalTimeout = db.Ado.CommandTimeOut;
            db.Ado.CommandTimeOut = 1800;

            try
            {
                _hqContext.CheckConnection();
                var requestedCodes = productCodes
                    .Select(NormalizeCode)
                    .Where(code => code != null)
                    .Select(code => code!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (requestedCodes.Count == 0)
                {
                    return ApiResponse<HqProductSyncResult>.Error(
                        "商品编码列表不能为空",
                        "PRODUCT_HQ_SELECTED_SYNC_EMPTY_CODES"
                    );
                }

                var selectedLocalProducts = await db.Queryable<Product>()
                    .Where(row =>
                        row.ProductCode != null
                        && requestedCodes.Contains(row.ProductCode)
                        && !row.IsDeleted
                    )
                    .ToListAsync();
                selectedLocalProducts = DeduplicateByBusinessKey(
                    selectedLocalProducts,
                    row => row.ProductCode
                );
                result.TotalLocalProducts = selectedLocalProducts.Count;

                var selectedLocalByCode = selectedLocalProducts
                    .Where(row => NormalizeCode(row.ProductCode) != null)
                    .ToDictionary(row => NormalizeCode(row.ProductCode)!, StringComparer.OrdinalIgnoreCase);
                foreach (var requestedCode in requestedCodes)
                {
                    if (!selectedLocalByCode.ContainsKey(requestedCode))
                    {
                        result.Errors.Add($"本地商品不存在或已删除: {requestedCode}");
                    }
                }

                if (selectedLocalProducts.Count == 0)
                {
                    result.DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                    return ApiResponse<HqProductSyncResult>.Error(
                        "未找到有效的本地商品",
                        "PRODUCT_HQ_SELECTED_SYNC_NO_PRODUCTS",
                        result
                    );
                }

                var selectedLocalCodes = selectedLocalByCode.Keys.ToList();
                var directHqRows = await _hqContext.Db.Queryable<DIC_商品信息字典表>()
                    .Where(row =>
                        row.H商品编码 != null
                        && selectedLocalCodes.Contains(row.H商品编码)
                        && row.H使用状态 == true
                    )
                    .ToListAsync();
                var directHqCodes = directHqRows
                    .Select(row => NormalizeCode(row.H商品编码))
                    .Where(code => code != null)
                    .Select(code => code!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var resolvedHqCodes = new HashSet<string>(directHqCodes, StringComparer.OrdinalIgnoreCase);
                var needFallback = selectedLocalProducts
                    .Where(row =>
                        NormalizeCode(row.ProductCode) is { } code
                        && !directHqCodes.Contains(code)
                    )
                    .ToList();

                if (needFallback.Count > 0)
                {
                    // 兜底只用于找 HQ 商品编码：分店零售价表提供供应商，商品字典表提供货号。
                    var fallbackMatches = await ResolveHqCodesBySupplierItemAsync(needFallback);
                    foreach (var localProduct in needFallback)
                    {
                        var localCode = NormalizeCode(localProduct.ProductCode);
                        var key = BuildSupplierItemKey(localProduct.LocalSupplierCode, localProduct.ItemNumber);
                        if (localCode == null || key == null)
                        {
                            result.Errors.Add($"商品缺少供应商或货号，无法兜底匹配: {localCode ?? "(空编码)"}");
                            continue;
                        }

                        if (!fallbackMatches.TryGetValue(key, out var matchedCodes) || matchedCodes.Count == 0)
                        {
                            result.Errors.Add($"HQ未找到供应商+货号匹配: {localProduct.LocalSupplierCode}/{localProduct.ItemNumber}");
                            continue;
                        }

                        if (matchedCodes.Count > 1)
                        {
                            result.Errors.Add($"HQ供应商+货号匹配到多个商品，已跳过: {localProduct.LocalSupplierCode}/{localProduct.ItemNumber}");
                            continue;
                        }

                        resolvedHqCodes.Add(matchedCodes[0]);
                    }
                }

                if (resolvedHqCodes.Count == 0)
                {
                    result.DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                    return ApiResponse<HqProductSyncResult>.Error(
                        "未匹配到可同步的HQ商品",
                        "PRODUCT_HQ_SELECTED_SYNC_NO_HQ_MATCH",
                        result
                    );
                }

                var resolvedHqCodeList = resolvedHqCodes.ToList();
                var hqProducts = await _hqContext.Db.Queryable<DIC_商品信息字典表>()
                    .Where(row =>
                        row.H商品编码 != null
                        && resolvedHqCodeList.Contains(row.H商品编码)
                        && row.H使用状态 == true
                    )
                    .ToListAsync();
                hqProducts = hqProducts
                    .GroupBy(row => row.H商品编码!)
                    .Select(group => group.OrderByDescending(row => row.FGC_LastModifyDate).First())
                    .ToList();
                result.TotalHqProducts = hqProducts.Count;
                var auditProductCodes = hqProducts
                    .Select(row => NormalizeCode(row.H商品编码))
                    .Where(code => code != null)
                    .Select(code => code!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var auditBatchGuid = Guid.NewGuid();
                var auditOccurredAtUtc = DateTime.UtcNow;

                db.Ado.BeginTran();
                try
                {
                    // 指定同步允许 HQ 在身份安全时迁移 Type2 父子键；GUID 可能命中请求范围外的旧父商品，
                    // 因此使用全局闸锁覆盖源、目标两侧，避免在不完整锁范围内跨父商品改键。
                    var childCostLockScope = await SetChildPurchasePriceMutationLock.AcquireAllAsync(db);
                    var beforeSnapshots = await CaptureProductSnapshotsAsync(auditProductCodes);
                    var activeProductCodes = await UpsertSelectedProductsFromHqAsync(db, hqProducts, result);
                    await SyncSelectedProductSetCodesFromHqAsync(db, activeProductCodes, result);
                    await SyncSelectedStoreRetailPricesFromHqAsync(db, activeProductCodes, result);
                    await SyncSelectedStoreMultiCodesFromHqAsync(db, activeProductCodes, result);
                    var recalculation = await new SetChildPurchasePriceService(db)
                        .RecalculateLockedAsync(
                            childCostLockScope,
                            activeProductCodes,
                            null,
                            ResolveSetChildPurchasePriceActor(null)
                        );
                    EnsureSetChildPurchasePriceRecalculated(recalculation, activeProductCodes);
                    var afterSnapshots = await CaptureProductSnapshotsAsync(auditProductCodes);
                    await RecordProductChangesAsync(
                        beforeSnapshots,
                        afterSnapshots,
                        "Update",
                        "ProductHqSync.Selected",
                        auditBatchGuid,
                        auditOccurredAtUtc
                    );
                    db.Ado.CommitTran();
                }
                catch
                {
                    db.Ado.RollbackTran();
                    throw;
                }

                result.DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                return ApiResponse<HqProductSyncResult>.OK(result, "选中商品HQ同步完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "选中商品HQ同步失败");
                result.DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                result.Errors.Add(ex.Message);
                // 选中同步同样可能在同一事务内争用套装成本锁，必须保留统一错误码。
                var errorCode = SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
                    ? SetChildPurchasePriceMutationLock.BusyErrorCode
                    : "PRODUCT_HQ_SELECTED_SYNC_ERROR";
                return ApiResponse<HqProductSyncResult>.Error(
                    $"选中商品HQ同步失败: {ex.Message}",
                    errorCode,
                    result
                );
            }
            finally
            {
                db.Ado.CommandTimeOut = originalTimeout;
                SyncLock.Release();
            }
        }

        private string ResolveSetChildPurchasePriceActor(string? actorName)
        {
            var resolved = string.IsNullOrWhiteSpace(actorName)
                ? _currentUserService.GetCurrentUsername()
                : actorName.Trim();
            return string.IsNullOrWhiteSpace(resolved) ? "System" : resolved;
        }

        private static void EnsureSetChildPurchasePriceRecalculated(
            SetChildPurchasePriceWritebackResultDto recalculation,
            IEnumerable<string> productCodes
        )
        {
            if (
                recalculation.ProductSetCode.SkippedGroupCount == 0
                && recalculation.StoreMultiCodeProduct.SkippedGroupCount == 0
            )
            {
                return;
            }

            var affectedCodes = string.Join(
                ", ",
                productCodes.Distinct(StringComparer.OrdinalIgnoreCase)
            );
            var reasons = string.Join(
                "；",
                recalculation.Errors.Select(error =>
                    $"{error.TableName}/{error.StoreCode ?? "总部"}/{error.ProductCode}: {error.Reason}"
                )
            );
            throw new InvalidOperationException(
                $"HQ 同步后的套装子项成本无法完整重算，主商品: {affectedCodes}。{reasons}"
            );
        }

        private async Task<Dictionary<string, List<string>>> ResolveHqCodesBySupplierItemAsync(
            List<Product> localProducts
        )
        {
            var supplierCodes = localProducts
                .Select(row => NormalizeCode(row.LocalSupplierCode))
                .Where(code => code != null)
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var itemNumbers = localProducts
                .Select(row => NormalizeCode(row.ItemNumber))
                .Where(code => code != null)
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (supplierCodes.Count == 0 || itemNumbers.Count == 0)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            var candidates = await _hqContext.Db.Queryable<DIC_商品零售价表, DIC_商品信息字典表>(
                    (retail, product) => new JoinQueryInfos(
                        JoinType.Inner,
                        retail.H商品编码 == product.H商品编码
                    )
                )
                .Where((retail, product) =>
                    retail.H使用状态 == true
                    && product.H使用状态 == true
                    && supplierCodes.Contains(retail.H供应商编码)
                    && product.H货号 != null
                    && itemNumbers.Contains(product.H货号)
                    && !string.IsNullOrEmpty(product.H商品编码)
                )
                .Select((retail, product) => new SupplierItemHqProductMatch
                {
                    SupplierCode = retail.H供应商编码,
                    ItemNumber = product.H货号,
                    ProductCode = product.H商品编码,
                })
                .ToListAsync();

            return candidates
                .Select(row => new
                {
                    Key = BuildSupplierItemKey(row.SupplierCode, row.ItemNumber),
                    ProductCode = NormalizeCode(row.ProductCode),
                })
                .Where(row => row.Key != null && row.ProductCode != null)
                .GroupBy(row => row.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(row => row.ProductCode!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase
                );
        }

        private async Task<List<string>> UpsertSelectedProductsFromHqAsync(
            ISqlSugarClient db,
            List<DIC_商品信息字典表> hqProducts,
            HqProductSyncResult result
        )
        {
            var productCodes = hqProducts
                .Select(row => NormalizeCode(row.H商品编码))
                .Where(code => code != null)
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var localRows = await db.Queryable<Product>()
                .Where(row => row.ProductCode != null && productCodes.Contains(row.ProductCode))
                .ToListAsync();
            var localByCode = localRows
                .Where(row => NormalizeCode(row.ProductCode) != null)
                .GroupBy(row => NormalizeCode(row.ProductCode)!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var hqRow in hqProducts)
            {
                var code = NormalizeCode(hqRow.H商品编码);
                if (code == null)
                {
                    continue;
                }

                if (localByCode.TryGetValue(code, out var local))
                {
                    ApplyProductUpdate(hqRow, local);
                    await db.Updateable(local).ExecuteCommandAsync();
                    result.ProductsUpdated++;
                    continue;
                }

                var product = MapNewProduct(hqRow);
                await db.Insertable(product).ExecuteCommandAsync();
                localByCode[code] = product;
                result.ProductsAdded++;
            }

            return productCodes;
        }

        private async Task SyncSelectedProductSetCodesFromHqAsync(
            ISqlSugarClient db,
            List<string> productCodes,
            HqProductSyncResult result
        )
        {
            if (productCodes.Count == 0)
            {
                return;
            }

            var hqRows = await _hqContext.Db.Queryable<DIC_一品多码表>()
                .Where(row =>
                    row.H使用状态 == true
                    && row.H商品编码 != null
                    && productCodes.Contains(row.H商品编码)
                    && !string.IsNullOrEmpty(row.H多码商品编号)
                )
                .ToListAsync();
            var sourcePreflight = ProductSetCodeIdentityResolver.PreflightSource(
                hqRows,
                row => row.HGUID,
                row => row.H商品编码,
                row => row.H多码商品编号,
                row => row.FGC_LastModifyDate,
                row => row.ID
            );
            AddProductSetCodeSourceConflictErrors(result, sourcePreflight.Conflicts);
            hqRows = sourcePreflight.Rows.ToList();
            if (hqRows.Count == 0)
            {
                return;
            }

            // GUID 可能指向选中商品范围外的旧父商品；必须全表解析双身份，
            // 否则会把安全迁移误判成新增并撞主键，或漏掉跨父商品 Type1 保护。
            var localRows = await db.Queryable<ProductSetCode>().ToListAsync();
            var identityIndex = ProductSetCodeIdentityResolver.CreateIndex(localRows);
            var byGuid = localRows
                .Where(row => !string.IsNullOrWhiteSpace(row.SetCodeId))
                .GroupBy(row => row.SetCodeId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var hqRow in hqRows)
            {
                var resolution = identityIndex.Resolve(
                    hqRow.HGUID,
                    hqRow.H商品编码,
                    hqRow.H多码商品编号
                );
                if (resolution.Kind == ProductSetCodeIdentityMatchKind.Conflict)
                {
                    // 必须先拒绝 GUID/业务键交叉命中，不能让 Type1 保护掩盖另一条被占用记录。
                    AddProductSetCodeLocalConflictError(result, resolution);
                    continue;
                }

                var local = resolution.MatchedRow;
                if (local?.SetType == 1)
                {
                    // 选中同步同样不得让 HQ 普通多码改变任意状态的本地 Type1 套装关系。
                    continue;
                }
                if (local == null)
                {
                    local = MapNewProductSetCode(hqRow);
                    await db.Insertable(local).ExecuteCommandAsync();
                    result.ProductSetCodesAdded++;
                    byGuid[local.SetCodeId] = local;
                    identityIndex.Add(local);
                    continue;
                }

                // GuidOnly 表示目标父子键尚未被占用；KeyOnly 表示目标 GUID 尚未被占用。
                // 只有这些安全解析结果才允许 Type2 重键、迁移或复活。
                var previousIdentity = ProductSetCodeIdentityResolver.CreateIdentity(local);
                await NormalizeProductSetCodeIdAsync(db, local, hqRow.HGUID, byGuid);
                ApplyProductSetCodeUpdate(hqRow, local);
                await db.Updateable(local).ExecuteCommandAsync();
                identityIndex.Reindex(local, previousIdentity);
                result.ProductSetCodesUpdated++;
            }
        }

        private async Task SyncSelectedStoreRetailPricesFromHqAsync(
            ISqlSugarClient db,
            List<string> productCodes,
            HqProductSyncResult result
        )
        {
            var activeStoreCodes = await GetActiveLocalStoreCodesAsync(db);
            if (productCodes.Count == 0 || activeStoreCodes.Count == 0)
            {
                return;
            }

            var hqRows = await _hqContext.Db.Queryable<DIC_商品零售价表>()
                .Where(row =>
                    row.H使用状态 == true
                    && productCodes.Contains(row.H商品编码)
                    && activeStoreCodes.Contains(row.H分店代码)
                )
                .ToListAsync();
            if (hqRows.Count == 0)
            {
                return;
            }

            var localRows = await db.Queryable<StoreRetailPrice>()
                .Where(row => row.ProductCode != null && productCodes.Contains(row.ProductCode))
                .ToListAsync();
            var localByKey = localRows
                .Select(row => new
                {
                    Key = BuildStoreProductKey(row.StoreCode, row.ProductCode),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!)
                .ToDictionary(group => group.Key, group => group.First().Row, StringComparer.OrdinalIgnoreCase);

            foreach (var hqRow in hqRows)
            {
                var key = BuildStoreProductKey(hqRow.H分店代码, hqRow.H商品编码);
                if (key == null)
                {
                    continue;
                }

                if (localByKey.TryGetValue(key, out var local))
                {
                    ApplyStoreRetailPriceUpdate(hqRow, local);
                    await db.Updateable(local).ExecuteCommandAsync();
                    result.StoreRetailPricesUpdated++;
                    continue;
                }

                local = MapNewStoreRetailPrice(hqRow);
                await db.Insertable(local).ExecuteCommandAsync();
                localByKey[key] = local;
                result.StoreRetailPricesCreated++;
            }
        }

        private async Task SyncSelectedStoreMultiCodesFromHqAsync(
            ISqlSugarClient db,
            List<string> productCodes,
            HqProductSyncResult result
        )
        {
            var activeStoreCodes = await GetActiveLocalStoreCodesAsync(db);
            if (productCodes.Count == 0 || activeStoreCodes.Count == 0)
            {
                return;
            }

            var hqRows = await _hqContext.Db.Queryable<DIC_分店一品多码表>()
                .Where(row =>
                    row.H使用状态 == true
                    && row.H商品编码 != null
                    && productCodes.Contains(row.H商品编码)
                    && row.H分店代码 != null
                    && activeStoreCodes.Contains(row.H分店代码)
                    && !string.IsNullOrEmpty(row.H多码商品编码)
                )
                .ToListAsync();
            if (hqRows.Count == 0)
            {
                return;
            }

            var localRows = await db.Queryable<StoreMultiCodeProduct>()
                .Where(row => row.ProductCode != null && productCodes.Contains(row.ProductCode))
                .ToListAsync();
            var protectedSetKeys = (await db.Queryable<ProductSetCode>()
                    .Where(row =>
                        row.SetType == 1
                        && productCodes.Contains(row.ProductCode)
                    )
                    .ToListAsync())
                .Select(row => BuildProductSetCodeBusinessKey(row.ProductCode, row.SetProductCode))
                .Where(key => key != null)
                .Select(key => key!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var localByKey = localRows
                .Select(row => new
                {
                    Key = BuildStoreMultiCodeKey(
                        row.StoreCode,
                        row.ProductCode,
                        row.MultiCodeProductCode
                    ),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!)
                .ToDictionary(group => group.Key, group => group.First().Row, StringComparer.OrdinalIgnoreCase);

            foreach (var hqRow in hqRows)
            {
                var protectedSetKey = BuildProductSetCodeBusinessKey(
                    hqRow.H商品编码,
                    hqRow.H多码商品编码
                );
                if (protectedSetKey != null && protectedSetKeys.Contains(protectedSetKey))
                {
                    // 门店多码表没有 SetType，必须通过全局套装关系保护派生成本。
                    continue;
                }
                var key = BuildStoreMultiCodeKey(
                    hqRow.H分店代码,
                    hqRow.H商品编码,
                    hqRow.H多码商品编码
                );
                if (key == null)
                {
                    continue;
                }

                if (localByKey.TryGetValue(key, out var local))
                {
                    ApplyStoreMultiCodeUpdate(hqRow, local);
                    await db.Updateable(local).ExecuteCommandAsync();
                    result.StoreMultiCodesUpdated++;
                    continue;
                }

                local = MapNewStoreMultiCode(hqRow);
                await db.Insertable(local).ExecuteCommandAsync();
                localByKey[key] = local;
                result.StoreMultiCodesCreated++;
            }
        }

        public async Task<ApiResponse<PushProductsToHqResult>> PushToHqAsync(
            PushProductsToHqRequest request
        )
        {
            if (
                request == null
                || (
                    (request.ProductCodes == null || request.ProductCodes.Count == 0)
                    && (request.Items == null || request.Items.Count == 0)
                )
            )
            {
                return ApiResponse<PushProductsToHqResult>.Error(
                    "推送商品列表不能为空",
                    "PRODUCT_HQ_PUSH_EMPTY_CODES"
                );
            }

            if (!await SyncLock.WaitAsync(0))
            {
                return ApiResponse<PushProductsToHqResult>.Error(
                    "已有商品HQ同步任务正在执行，请稍后再试",
                    SetChildPurchasePriceMutationLock.BusyErrorCode
                );
            }

            var startedAt = DateTime.UtcNow;
            var result = new PushProductsToHqResult();
            var localDb = _localContext.Db;
            var hqDb = _hqContext.Db;
            var originalTimeout = hqDb.Ado.CommandTimeOut;
            hqDb.Ado.CommandTimeOut = 1800;
            var localTransactionStarted = false;

            try
            {
                _hqContext.CheckConnection();
                var updateFields = new PushToHqUpdateFieldSelection(request.UpdateFields);
                var resolvedSelection = await ResolvePushSelectionAsync(localDb, request, result);
                var products = resolvedSelection.Products;
                var inventoryCandidates = resolvedSelection.InventoryCandidates;
                var domesticProductImages = resolvedSelection.DomesticProductImages;
                var domesticSupplierCodes = resolvedSelection.DomesticSupplierCodes;
                if (result.TotalCount == 0)
                {
                    // 统一在服务层记录业务失败关键信息，方便中心日志按错误码和耗时检索。
                    LogPushToHqBusinessFailure(
                        "PRODUCT_HQ_PUSH_EMPTY_CODES",
                        result,
                        "推送商品列表不能为空"
                    );
                    return ApiResponse<PushProductsToHqResult>.Error(
                        "推送商品列表不能为空",
                        "PRODUCT_HQ_PUSH_EMPTY_CODES"
                    );
                }
                result.TotalLocalProducts = products.Count;

                if (products.Count == 0)
                {
                    result.FailedCount = result.TotalCount;
                    result.DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                    LogPushToHqBusinessFailure(
                        "PRODUCT_HQ_PUSH_NO_PRODUCTS",
                        result,
                        "未找到有效的本地商品"
                    );
                    return new ApiResponse<PushProductsToHqResult>
                    {
                        Success = false,
                        Message = "未找到有效的本地商品",
                        ErrorCode = "PRODUCT_HQ_PUSH_NO_PRODUCTS",
                        Data = result,
                        Details = result,
                    };
                }

                if (resolvedSelection.ItemFailureCount > 0)
                {
                    result.SuccessCount = 0;
                    result.FailedCount = result.TotalCount;
                    result.DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                    LogPushToHqBusinessFailure(
                        "PRODUCT_HQ_PUSH_ITEM_ERRORS",
                        result,
                        "推送候选包含错误，未写入HQ"
                    );
                    return new ApiResponse<PushProductsToHqResult>
                    {
                        Success = false,
                        Message = "推送候选包含错误，未写入HQ",
                        ErrorCode = "PRODUCT_HQ_PUSH_ITEM_ERRORS",
                        Data = result,
                        Details = result,
                    };
                }

                var activeProductCodes = products
                    .Select(row => NormalizeCode(row.ProductCode))
                    .Where(code => code != null)
                    .Select(code => code!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // 必须在读取 HQ 分店和主档存在性前取得跨写路径商品锁，
                // 防止窄投影与完整推送并发进入 update-if-zero-insert 分支。
                await using var hqMutationLock = await ProductHqMutationExecutionLock.AcquireAsync(
                    hqDb,
                    activeProductCodes
                );
                if (hqMutationLock == null)
                {
                    return ApiResponse<PushProductsToHqResult>.Error(
                        "商品 HQ 同步正忙，请稍后重试",
                        SetChildPurchasePriceMutationLock.BusyErrorCode
                    );
                }

                var activeStoreCodes = (await hqDb.Queryable<HqBranch>()
                    .Select(row => row.BranchCode)
                    .ToListAsync())
                    // 推送到 HQ 时以 HQ 分店表为准，避免本地门店资料缺失导致 HQ 分店价格不完整。
                    .Select(NormalizeCode)
                    .Where(code => code != null)
                    .Select(code => code!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var targetStoreResolution = ResolveTargetStoreCodes(
                    request,
                    activeStoreCodes,
                    updateFields,
                    result
                );
                if (targetStoreResolution.Failed)
                {
                    result.SuccessCount = 0;
                    result.FailedCount = result.TotalCount;
                    result.DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                    LogPushToHqBusinessFailure(
                        targetStoreResolution.ErrorCode!,
                        result,
                        targetStoreResolution.Message!
                    );
                    return new ApiResponse<PushProductsToHqResult>
                    {
                        Success = false,
                        Message = targetStoreResolution.Message ?? string.Empty,
                        ErrorCode = targetStoreResolution.ErrorCode,
                        Data = result,
                        Details = result,
                    };
                }

                await localDb.Ado.BeginTranAsync();
                localTransactionStarted = true;
                var localLockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                    localDb,
                    activeProductCodes
                );

                // 锁内重新解析商品，禁止把取锁前的旧实体快照推送到 HQ。
                var lockedResult = new PushProductsToHqResult();
                resolvedSelection = await ResolvePushSelectionAsync(localDb, request, lockedResult);
                result = lockedResult;
                products = resolvedSelection.Products;
                inventoryCandidates = resolvedSelection.InventoryCandidates;
                domesticProductImages = resolvedSelection.DomesticProductImages;
                domesticSupplierCodes = resolvedSelection.DomesticSupplierCodes;
                result.TotalLocalProducts = products.Count;
                if (
                    result.TotalCount == 0
                    || products.Count == 0
                    || resolvedSelection.ItemFailureCount > 0
                )
                {
                    throw new InvalidOperationException("商品在等待业务锁期间已变化，请重新选择后重试");
                }

                activeProductCodes = products
                    .Select(row => NormalizeCode(row.ProductCode))
                    .Where(code => code != null)
                    .Select(code => code!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                localLockScope.EnsureCovers(localDb, activeProductCodes);

                if (updateFields.Has(HqFieldProductSetCodes))
                {
                    var recalculation = await new SetChildPurchasePriceService(
                        localDb
                    ).RecalculateGlobalLockedAsync(
                        localLockScope,
                        activeProductCodes,
                        ResolveSetChildPurchasePriceActor(null)
                    );
                    EnsureSetChildPurchasePriceRecalculated(
                        recalculation,
                        activeProductCodes
                    );
                }

                if (updateFields.Has(HqFieldStoreMultiCodes))
                {
                    var existingHqProductCodesForScope = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase
                    );
                    foreach (var codeBatch in activeProductCodes.Chunk(HqCodeBatchSize))
                    {
                        var codes = codeBatch.ToList();
                        var existingCodes = await hqDb.Queryable<DIC_商品信息字典表>()
                            .Where(row => row.H商品编码 != null && codes.Contains(row.H商品编码))
                            .Select(row => row.H商品编码)
                            .ToListAsync();
                        foreach (var code in existingCodes)
                        {
                            var normalizedCode = NormalizeCode(code);
                            if (normalizedCode != null)
                            {
                                existingHqProductCodesForScope.Add(normalizedCode);
                            }
                        }
                    }

                    var targetStoresForExistingProducts =
                        targetStoreResolution.StoreCodes ?? activeStoreCodes;
                    var exactStoreGroups = activeProductCodes
                        .SelectMany(productCode =>
                            (existingHqProductCodesForScope.Contains(productCode)
                                    ? targetStoresForExistingProducts
                                    : activeStoreCodes)
                                .Select(storeCode =>
                                    (StoreCode: (string?)storeCode, ProductCode: (string?)productCode)
                                )
                        )
                        .ToList();
                    var costWriteback = new SetChildPurchasePriceService(localDb);

                    // 普通多码（Type2）的成本可以由同维度主商品确定，但 HQ 分店行仍需稳定的本地投影身份。
                    // 只在本次已锁定的精确目标组内补齐纯 Type2，或首次发送到 HQ 的纯 Type1 套装。
                    var activeSetRows = new List<ProductSetCode>();
                    foreach (var codeBatch in activeProductCodes.Chunk(HqCodeBatchSize))
                    {
                        var codes = codeBatch.ToList();
                        activeSetRows.AddRange(
                            await localDb.Queryable<ProductSetCode>()
                                .Where(row =>
                                    codes.Contains(row.ProductCode)
                                    && row.IsActive
                                    && !row.IsDeleted
                                )
                                .ToListAsync()
                        );
                    }
                    var type2OnlyProductCodes = activeSetRows
                        .Where(row => !string.IsNullOrWhiteSpace(row.ProductCode))
                        .GroupBy(row => row.ProductCode.Trim(), StringComparer.OrdinalIgnoreCase)
                        .Where(group => group.All(row => row.SetType == 2))
                        .Select(group => group.Key)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    // 本地新建只初始化启用门店，HQ 首次发送却覆盖全部 HQ 分店；
                    // 仅允许类型一致的新套装补齐缺少的投影，已有套装和混合关系仍严格校验。
                    var newSetProductCodes = products
                        .Where(product =>
                            product.ProductType == 1
                            && NormalizeCode(product.ProductCode) != null
                            && !existingHqProductCodesForScope.Contains(
                                NormalizeCode(product.ProductCode)!
                            )
                        )
                        .Select(product => NormalizeCode(product.ProductCode)!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var newType1ProductCodes = activeSetRows
                        .Where(row => !string.IsNullOrWhiteSpace(row.ProductCode))
                        .GroupBy(row => row.ProductCode.Trim(), StringComparer.OrdinalIgnoreCase)
                        .Where(group =>
                            newSetProductCodes.Contains(group.Key)
                            && group.All(row => row.SetType == 1)
                        )
                        .Select(group => group.Key);
                    var repairableProductCodes = type2OnlyProductCodes
                        .Union(newType1ProductCodes, StringComparer.OrdinalIgnoreCase)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var repairGroups = exactStoreGroups
                        .Where(group =>
                            group.ProductCode != null
                            && repairableProductCodes.Contains(group.ProductCode)
                        )
                        .ToList();

                    if (repairGroups.Count > 0)
                    {
                        var repairProductCodes = repairGroups
                            .Select(group => group.ProductCode!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var warehouseRows = new List<WarehouseProduct>();
                        foreach (var codeBatch in repairProductCodes.Chunk(HqCodeBatchSize))
                        {
                            var codes = codeBatch.ToList();
                            warehouseRows.AddRange(
                                await localDb.Queryable<WarehouseProduct>()
                                    .Where(row =>
                                        codes.Contains(row.ProductCode) && !row.IsDeleted
                                    )
                                    .ToListAsync()
                            );
                        }
                        var warehouseImportPrices = warehouseRows
                            .Where(row =>
                                !string.IsNullOrWhiteSpace(row.ProductCode)
                                && row.ImportPrice.GetValueOrDefault() > 0m
                            )
                            .GroupBy(row => row.ProductCode.Trim(), StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                group => group.Key,
                                group => group.First().ImportPrice!.Value,
                                StringComparer.OrdinalIgnoreCase
                            );
                        var repairPurchasePrices = products
                            .Select(product => new
                            {
                                ProductCode = NormalizeCode(product.ProductCode),
                                Product = product,
                            })
                            .Where(item =>
                                item.ProductCode != null
                                && repairableProductCodes.Contains(item.ProductCode)
                            )
                            .ToDictionary(
                                item => item.ProductCode!,
                                item => item.Product.PurchasePrice.GetValueOrDefault() > 0m
                                    ? item.Product.PurchasePrice!.Value
                                    : warehouseImportPrices.GetValueOrDefault(item.ProductCode!),
                                StringComparer.OrdinalIgnoreCase
                            );
                        // Repair 内部按商品编码构造 SQL IN 条件；沿用 HQ 查询批次，避免大批量推送超过 SQL Server 参数上限。
                        // Type1 必须有有效全局主成本；纯 Type2 继续允许仅有门店主成本，分组后分别调用原有门禁。
                        foreach (var repairProductBatch in repairProductCodes
                            .GroupBy(productCode => type2OnlyProductCodes.Contains(productCode))
                            .SelectMany(group => group.Chunk(HqCodeBatchSize)))
                        {
                            var allowType2StoreParentPurchasePrice =
                                type2OnlyProductCodes.Contains(repairProductBatch[0]);
                            var batchProductCodes = repairProductBatch.ToHashSet(
                                StringComparer.OrdinalIgnoreCase
                            );
                            var batchPurchasePrices = repairPurchasePrices
                                .Where(pair => batchProductCodes.Contains(pair.Key))
                                .ToDictionary(
                                    pair => pair.Key,
                                    pair => pair.Value,
                                    StringComparer.OrdinalIgnoreCase
                                );
                            var batchGroups = repairGroups
                                .Where(group =>
                                    group.ProductCode != null
                                    && batchProductCodes.Contains(group.ProductCode)
                                )
                                .ToList();
                            var repair =
                                await costWriteback.RepairMissingStoreRelationsLockedAsync(
                                    localLockScope,
                                    batchPurchasePrices,
                                    ResolveSetChildPurchasePriceActor(null),
                                    exactStoreGroups: batchGroups,
                                    allowType2StoreParentPurchasePrice: allowType2StoreParentPurchasePrice
                                );
                            if (repair.Failures.Count > 0)
                            {
                                var reasons = string.Join(
                                    "；",
                                    repair.Failures.Values
                                        .OrderBy(
                                            failure => failure.ProductCode,
                                            StringComparer.OrdinalIgnoreCase
                                        )
                                        .Select(failure =>
                                            $"{failure.ProductCode} / 分店 {failure.StoreCode ?? "目标分店"} [{failure.Code}]: {failure.Message}"
                                        )
                                );
                                throw new InvalidOperationException(
                                    $"目标分店多码关系无法安全补齐。{reasons}"
                                );
                            }
                        }
                    }

                    var recalculation = await costWriteback.RecalculateStoreGroupsLockedAsync(
                        localLockScope,
                        exactStoreGroups,
                        ResolveSetChildPurchasePriceActor(null)
                    );
                    EnsureSetChildPurchasePriceRecalculated(
                        recalculation,
                        activeProductCodes
                    );
                }

                // 校正后锁内重读两张派生成本表，再映射到 HQ；不再使用取锁前快照。
                var productSetCodes = new List<ProductSetCode>();
                foreach (var codeBatch in activeProductCodes.Chunk(HqCodeBatchSize))
                {
                    var codes = codeBatch.ToList();
                    productSetCodes.AddRange(
                        await localDb.Queryable<ProductSetCode>()
                            .Where(row => codes.Contains(row.ProductCode) && !row.IsDeleted)
                            .ToListAsync()
                    );
                }
                productSetCodes = DeduplicateByBusinessKey(
                    productSetCodes,
                    row => BuildProductSetCodeBusinessKey(row.ProductCode, row.SetProductCode)
                );

                var storeMultiCodes = new List<StoreMultiCodeProduct>();
                foreach (var codeBatch in activeProductCodes.Chunk(HqCodeBatchSize))
                {
                    var codes = codeBatch.ToList();
                    foreach (var storeBatch in activeStoreCodes.Chunk(HqCodeBatchSize))
                    {
                        var stores = storeBatch.ToList();
                        storeMultiCodes.AddRange(
                            await localDb.Queryable<StoreMultiCodeProduct>()
                                .Where(row =>
                                    row.ProductCode != null
                                    && codes.Contains(row.ProductCode)
                                    && row.StoreCode != null
                                    && stores.Contains(row.StoreCode)
                                    && !row.IsDeleted
                                )
                                .ToListAsync()
                        );
                    }
                }
                storeMultiCodes = DeduplicateByBusinessKey(
                    storeMultiCodes,
                    row => BuildStoreMultiCodeKey(row.StoreCode, row.ProductCode, row.MultiCodeProductCode)
                );

                hqDb.Ado.BeginTran();
                try
                {
                    // 事务内、紧邻 upsert 前快照 HQ 已存在商品：已有商品的分店维度只写目标分店，
                    // 新 HQ 商品始终为全部 HQ 分店创建必要记录，混合批次按新旧商品拆分。
                    // 快照与写入同事务并尽量缩短判定窗口；跨实例/HQ 外部写入仍依赖数据库业务键约束兜底。
                    var existingHqProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var codeBatch in activeProductCodes.Chunk(HqCodeBatchSize))
                    {
                        var codes = codeBatch.ToList();
                        var existingCodes = await hqDb.Queryable<DIC_商品信息字典表>()
                            .Where(row => row.H商品编码 != null && codes.Contains(row.H商品编码))
                            .Select(row => row.H商品编码)
                            .ToListAsync();
                        foreach (var code in existingCodes)
                        {
                            var normalizedCode = NormalizeCode(code);
                            if (normalizedCode != null)
                            {
                                existingHqProductCodes.Add(normalizedCode);
                            }
                        }
                    }
                    var storeCodesByProduct = new Dictionary<string, List<string>>(
                        StringComparer.OrdinalIgnoreCase
                    );
                    foreach (var productCode in activeProductCodes)
                    {
                        storeCodesByProduct[productCode] = existingHqProductCodes.Contains(productCode)
                            ? targetStoreResolution.StoreCodes ?? activeStoreCodes
                            : activeStoreCodes;
                    }

                    await UpsertHqProductsAsync(
                        hqDb,
                        products,
                        inventoryCandidates,
                        domesticProductImages,
                        domesticSupplierCodes,
                        updateFields,
                        result,
                        existingHqProductCodes
                    );
                    if (updateFields.HasAny(HqFieldStorePurchasePrice, HqFieldStoreRetailPrice, HqFieldSupplierCode))
                    {
                        await UpsertHqRetailPricesAsync(
                            hqDb,
                            products,
                            inventoryCandidates,
                            storeCodesByProduct,
                            activeStoreCodes,
                            updateFields,
                            result
                        );
                    }
                    if (updateFields.HasAny(HqFieldProductSetCodes, HqFieldSupplierCode))
                    {
                        await UpsertHqProductSetCodesAsync(
                            hqDb,
                            products,
                            productSetCodes,
                            result,
                            updateFields: updateFields
                        );
                    }
                    if (updateFields.HasAny(HqFieldStoreMultiCodes, HqFieldSupplierCode))
                    {
                        await UpsertHqStoreMultiCodesAsync(
                            hqDb,
                            products,
                            productSetCodes,
                            storeMultiCodes,
                            storeCodesByProduct,
                            activeStoreCodes,
                            result,
                            updateFields: updateFields
                        );
                    }
                    if (updateFields.HasAny(HqFieldInventoryDomesticPrice, HqFieldInventoryImportPrice, HqFieldInventoryOemPrice))
                    {
                        await UpsertHqWarehouseInventoriesAsync(
                            hqDb,
                            products,
                            inventoryCandidates,
                            updateFields,
                            result
                        );
                    }
                    await localDb.Ado.CommitTranAsync();
                    localTransactionStarted = false;
                    hqDb.Ado.CommitTran();
                }
                catch (Exception operationException)
                {
                    RollbackPushTransaction(
                        hqDb.Ado.RollbackTran,
                        result,
                        operationException
                    );
                    throw;
                }

                result.DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                result.SuccessCount = products.Count;
                result.FailedCount = result.TotalCount - result.SuccessCount;
                _logger.LogInformation(
                    "商品推送HQ完成: DurationMs={DurationMs}, SuccessCount={SuccessCount}, ActiveStoreCount={ActiveStoreCount}, ProductsAdded={ProductsAdded}, ProductsUpdated={ProductsUpdated}, StoreRetailPricesCreated={StoreRetailPricesCreated}, StoreRetailPricesUpdated={StoreRetailPricesUpdated}, ProductSetCodesCreated={ProductSetCodesCreated}, ProductSetCodesUpdated={ProductSetCodesUpdated}, StoreMultiCodesCreated={StoreMultiCodesCreated}, StoreMultiCodesUpdated={StoreMultiCodesUpdated}, WarehouseInventoriesCreated={WarehouseInventoriesCreated}, WarehouseInventoriesUpdated={WarehouseInventoriesUpdated}",
                    result.DurationMs,
                    result.SuccessCount,
                    activeStoreCodes.Count,
                    result.ProductsAdded,
                    result.ProductsUpdated,
                    result.StoreRetailPricesCreated,
                    result.StoreRetailPricesUpdated,
                    result.ProductSetCodesCreated,
                    result.ProductSetCodesUpdated,
                    result.StoreMultiCodesCreated,
                    result.StoreMultiCodesUpdated,
                    result.WarehouseInventoriesCreated,
                    result.WarehouseInventoriesUpdated
                );
                return ApiResponse<PushProductsToHqResult>.OK(result, "商品推送HQ完成");
            }
            catch (Exception ex)
            {
                if (localTransactionStarted)
                {
                    await localDb.Ado.RollbackTranAsync();
                    localTransactionStarted = false;
                }
                result.DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                result.Errors.Add(ex.Message);
                result.FailedCount = result.TotalCount;
                var errorCode = SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
                    ? SetChildPurchasePriceMutationLock.BusyErrorCode
                    : "PRODUCT_HQ_PUSH_ERROR";
                _logger.LogError(
                    ex,
                    "商品推送HQ异常失败: ErrorCode={ErrorCode}, FailedCount={FailedCount}, FirstFailureReason={FirstFailureReason}, DurationMs={DurationMs}",
                    errorCode,
                    result.FailedCount,
                    GetFirstPushFailureReason(result, ex.Message),
                    result.DurationMs
                );
                return new ApiResponse<PushProductsToHqResult>
                {
                    Success = false,
                    Message = $"商品推送HQ失败: {ex.Message}",
                    ErrorCode = errorCode,
                    Data = result,
                    Details = result,
                };
            }
            finally
            {
                hqDb.Ado.CommandTimeOut = originalTimeout;
                SyncLock.Release();
            }
        }

        private void LogPushToHqBusinessFailure(
            string errorCode,
            PushProductsToHqResult result,
            string fallbackReason
        )
        {
            // 业务失败不带请求明细，只保留可检索字段，避免日志噪音和额外敏感暴露。
            _logger.LogWarning(
                "商品推送HQ业务失败: ErrorCode={ErrorCode}, FailedCount={FailedCount}, FirstFailureReason={FirstFailureReason}, DurationMs={DurationMs}",
                errorCode,
                result.FailedCount,
                GetFirstPushFailureReason(result, fallbackReason),
                result.DurationMs
            );
        }

        /// <summary>
        /// 加载商品推送 HQ 的分店选项：直接来自 HQ 分店表，非空、大小写不敏感去重、按编码排序。
        /// </summary>
        public async Task<List<ProductHqStoreOptionDto>> GetHqStoreOptionsAsync()
        {
            _hqContext.CheckConnection();
            var branches = await _hqContext.Db.Queryable<HqBranch>().ToListAsync();
            return branches
                .Select(row => new ProductHqStoreOptionDto
                {
                    StoreCode = NormalizeCode(row.BranchCode) ?? string.Empty,
                    StoreName = NormalizeCode(row.BranchName) ?? string.Empty,
                })
                // 只排除空编码；名称为空仍保留，前端会回退显示编码。
                .Where(option => !string.IsNullOrWhiteSpace(option.StoreCode))
                .GroupBy(option => option.StoreCode, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(option => option.StoreCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 解析显式目标分店编码：trim、大小写不敏感去重并映射为 HQ 规范编码。
        /// 未知编码整批拒绝；显式空数组仅在请求只含全局字段时允许。
        /// </summary>
        private static TargetStoreCodeResolution ResolveTargetStoreCodes(
            PushProductsToHqRequest request,
            List<string> activeStoreCodes,
            PushToHqUpdateFieldSelection updateFields,
            PushProductsToHqResult result
        )
        {
            if (request.TargetStoreCodes == null)
            {
                return TargetStoreCodeResolution.AllStores();
            }

            var requestedCodes = request.TargetStoreCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var resolvedCodes = requestedCodes
                .Select(requested => activeStoreCodes.FirstOrDefault(canonical =>
                    string.Equals(canonical, requested, StringComparison.OrdinalIgnoreCase)
                ))
                .Where(code => code != null)
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var unknownCodes = requestedCodes
                .Where(requested => !activeStoreCodes.Contains(requested, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (unknownCodes.Count > 0)
            {
                result.Errors.Add($"未知HQ分店编码: {string.Join(", ", unknownCodes)}");
                return TargetStoreCodeResolution.Rejected(
                    "PRODUCT_HQ_PUSH_UNKNOWN_STORE_CODES",
                    "包含未知HQ分店编码，未写入HQ"
                );
            }

            if (resolvedCodes.Count == 0 && updateFields.HasStoreDimensionFields)
            {
                result.Errors.Add("显式指定分店为空时，仅允许全局字段更新");
                return TargetStoreCodeResolution.Rejected(
                    "PRODUCT_HQ_PUSH_EMPTY_TARGET_STORES",
                    "显式指定分店为空，仅允许全局字段更新"
                );
            }

            return TargetStoreCodeResolution.Explicit(resolvedCodes);
        }

        private static string GetFirstPushFailureReason(
            PushProductsToHqResult result,
            string fallbackReason
        )
        {
            var rawReason = result.Errors.FirstOrDefault(error => !string.IsNullOrWhiteSpace(error))
                ?? fallbackReason;
            return NormalizePushFailureReason(rawReason);
        }

        internal void RollbackPushTransaction(
            Action rollback,
            PushProductsToHqResult result,
            Exception operationException
        )
        {
            try
            {
                rollback();
            }
            catch (Exception rollbackException)
            {
                // 回滚异常只能作为附加诊断，不能覆盖真正导致整单失败的写入异常。
                _logger.LogError(
                    rollbackException,
                    "商品推送HQ事务回滚失败: ErrorCode={ErrorCode}, OriginalExceptionType={OriginalExceptionType}",
                    "PRODUCT_HQ_PUSH_ROLLBACK_ERROR",
                    operationException.GetType().Name
                );
            }
            finally
            {
                ResetPushWriteCounts(result);
            }
        }

        private static void ResetPushWriteCounts(PushProductsToHqResult result)
        {
            // HQ 写入使用整单事务；回滚后统计必须与实际提交数一致归零。
            result.ProductsAdded = 0;
            result.ProductsUpdated = 0;
            result.StoreRetailPricesCreated = 0;
            result.StoreRetailPricesUpdated = 0;
            result.ProductSetCodesCreated = 0;
            result.ProductSetCodesUpdated = 0;
            result.StoreMultiCodesCreated = 0;
            result.StoreMultiCodesUpdated = 0;
            result.WarehouseInventoriesCreated = 0;
            result.WarehouseInventoriesUpdated = 0;
        }

        private static string NormalizePushFailureReason(string rawReason)
        {
            if (rawReason.Contains("商品不存在或已删除", StringComparison.Ordinal))
            {
                return "商品不存在";
            }

            if (rawReason.Contains("未找到匹配商品", StringComparison.Ordinal))
            {
                return "商品不存在";
            }

            if (rawReason.Contains("多条本地商品", StringComparison.Ordinal))
            {
                return "商品匹配冲突";
            }

            if (rawReason.Contains("商品编码为空", StringComparison.Ordinal))
            {
                return "商品编码为空";
            }

            var separatorIndex = rawReason.IndexOf(':');
            return separatorIndex > 0 ? rawReason[..separatorIndex].Trim() : rawReason;
        }

        private async Task<PushToHqSelection> ResolvePushSelectionAsync(
            ISqlSugarClient localDb,
            PushProductsToHqRequest request,
            PushProductsToHqResult result
        )
        {
            var rawRequestItems = (request.Items ?? new List<PushProductsToHqItem>())
                .Where(item =>
                    item != null
                    && (
                        !string.IsNullOrWhiteSpace(item.ProductCode)
                        || !string.IsNullOrWhiteSpace(item.LocalSupplierCode)
                        || !string.IsNullOrWhiteSpace(item.ItemNumber)
                    )
                )
                .ToList();
            var itemProductCodeSet = rawRequestItems
                .Select(item => NormalizeCode(item.ProductCode))
                .Where(code => code != null)
                .Select(code => code!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var normalizedCodes = (request.ProductCodes ?? new List<string>())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                // items 是新契约主来源；productCodes 只补充旧入口或额外编码，避免同一商品被重复统计为失败。
                .Where(code => !itemProductCodeSet.Contains(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var requestItems = rawRequestItems;

            var requestedProductCodes = normalizedCodes
                .Concat(
                    requestItems
                        .Select(item => NormalizeCode(item.ProductCode))
                        .Where(code => code != null)
                        .Select(code => code!)
                )
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var requestedSupplierCodes = requestItems
                .Where(item => string.IsNullOrWhiteSpace(item.ProductCode))
                .Select(item => NormalizeCode(item.LocalSupplierCode))
                .Where(code => code != null)
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var requestedItemNumbers = requestItems
                .Where(item => string.IsNullOrWhiteSpace(item.ProductCode))
                .Select(item => NormalizeCode(item.ItemNumber))
                .Where(code => code != null)
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var queriedProducts = new List<Product>();
            foreach (var codeBatch in requestedProductCodes.Chunk(HqCodeBatchSize))
            {
                var codes = codeBatch.ToList();
                queriedProducts.AddRange(
                    await localDb.Queryable<Product>()
                        .Where(row =>
                            !row.IsDeleted
                            && row.ProductCode != null
                            && codes.Contains(row.ProductCode)
                        )
                        .ToListAsync()
                );
            }

            foreach (var supplierBatch in requestedSupplierCodes.Chunk(HqCodeBatchSize))
            {
                var suppliers = supplierBatch.ToList();
                foreach (var itemBatch in requestedItemNumbers.Chunk(HqCodeBatchSize))
                {
                    var itemNumbers = itemBatch.ToList();
                    queriedProducts.AddRange(
                        await localDb.Queryable<Product>()
                            .Where(row =>
                                !row.IsDeleted
                                && row.LocalSupplierCode != null
                                && suppliers.Contains(row.LocalSupplierCode)
                                && row.ItemNumber != null
                                && itemNumbers.Contains(row.ItemNumber)
                            )
                            .ToListAsync()
                    );
                }
            }
            var deduplicatedProducts = queriedProducts
                .Select(row => new
                {
                    Key = NormalizeCode(row.ProductCode) ?? NormalizeCode(row.UUID),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => item.Row.UpdatedAt ?? item.Row.CreatedAt)
                    .ThenByDescending(item => item.Row.CreatedAt)
                    .First()
                    .Row)
                .ToList();
            var productsByCode = deduplicatedProducts
                .Where(row => NormalizeCode(row.ProductCode) != null)
                .ToDictionary(row => NormalizeCode(row.ProductCode)!, StringComparer.OrdinalIgnoreCase);
            var productsBySupplierItem = deduplicatedProducts
                .Select(row => new
                {
                    Key = BuildSupplierItemKey(row.LocalSupplierCode, row.ItemNumber),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Row).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );

            var resolvedProductCodes = new List<string>();
            var inventoryCandidates = new Dictionary<string, PushProductsToHqItem>(
                StringComparer.OrdinalIgnoreCase
            );
            var failedCandidateCount = 0;
            var itemFailureCount = 0;

            foreach (var productCode in normalizedCodes)
            {
                if (productsByCode.TryGetValue(productCode, out var product))
                {
                    AppendResolvedProductCode(resolvedProductCodes, product.ProductCode);
                    continue;
                }

                result.Errors.Add($"商品不存在或已删除: {productCode}");
                failedCandidateCount++;
            }

            foreach (var item in requestItems)
            {
                var errorCountBeforeResolve = result.Errors.Count;
                // 前端 IsNewProduct 可能来自未刷新的页面状态；后端只信任本地 Product 实时匹配结果。
                var matchedProduct = ResolveMatchedProduct(productsByCode, productsBySupplierItem, item, result);
                var finalProductCode = NormalizeCode(matchedProduct?.ProductCode);
                if (matchedProduct == null)
                {
                    if (result.Errors.Count > errorCountBeforeResolve)
                    {
                        failedCandidateCount++;
                        itemFailureCount++;
                    }
                    continue;
                }

                if (finalProductCode == null)
                {
                    result.Errors.Add($"匹配成功但最终商品编码为空: {DescribePushItem(item)}");
                    failedCandidateCount++;
                    itemFailureCount++;
                    continue;
                }

                AppendResolvedProductCode(resolvedProductCodes, finalProductCode);
                if (!inventoryCandidates.ContainsKey(finalProductCode))
                {
                    inventoryCandidates[finalProductCode] = new PushProductsToHqItem
                    {
                        ProductCode = finalProductCode,
                        LocalSupplierCode = NormalizeCode(item.LocalSupplierCode),
                        ItemNumber = NormalizeCode(item.ItemNumber),
                        ProductName = NormalizeCode(item.ProductName),
                        EnglishName = NormalizeCode(item.EnglishName),
                        Barcode = NormalizeCode(item.Barcode),
                        ImageUrl = NormalizeCode(item.ImageUrl),
                        DomesticPrice = item.DomesticPrice,
                        ImportPrice = item.ImportPrice,
                        OemPrice = item.OemPrice,
                        IsNewProduct = false,
                    };
                }
            }

            // 旧 ProductCodes 入口没有候选价格时，补一个仅带商品资料的库存候选，
            // 这样仍能创建/更新价格记录，但不会伪造仓库状态去改 HQ/POS 启用状态。
            foreach (var resolvedProductCode in resolvedProductCodes)
            {
                if (
                    inventoryCandidates.ContainsKey(resolvedProductCode)
                    || !productsByCode.TryGetValue(resolvedProductCode, out var resolvedProduct)
                )
                {
                    continue;
                }

                inventoryCandidates[resolvedProductCode] = new PushProductsToHqItem
                {
                    ProductCode = resolvedProductCode,
                    ProductName = NormalizeCode(resolvedProduct.ProductName),
                    EnglishName = NormalizeCode(resolvedProduct.EnglishName),
                    Barcode = NormalizeCode(resolvedProduct.Barcode),
                    ImageUrl = NormalizeCode(resolvedProduct.ProductImage),
                    IsNewProduct = false,
                };
            }

            result.TotalCount = resolvedProductCodes.Count + failedCandidateCount;
            var products = resolvedProductCodes
                .Where(productsByCode.ContainsKey)
                .Select(code => productsByCode[code])
                .ToList();
            var productCodesForDomesticData = products
                .Select(row => NormalizeCode(row.ProductCode))
                .Where(code => code != null)
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var domesticProductRows = new List<DomesticProduct>();
            foreach (var codeBatch in productCodesForDomesticData.Chunk(HqCodeBatchSize))
            {
                var batch = codeBatch.ToList();
                domesticProductRows.AddRange(await localDb.Queryable<DomesticProduct>()
                    .Where(row => batch.Contains(row.ProductCode) && !row.IsDeleted)
                    .ToListAsync());
            }
            var domesticProductImages = domesticProductRows
                .Where(row => !string.IsNullOrWhiteSpace(row.ProductImage))
                .GroupBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => NormalizeCode(group.First().ProductImage) ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase
                );
            var domesticSupplierCodes = domesticProductRows
                .Where(row => !string.IsNullOrWhiteSpace(row.SupplierCode))
                .GroupBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => NormalizeCode(group.First().SupplierCode) ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase
                );
            return new PushToHqSelection(
                products,
                inventoryCandidates,
                domesticProductImages,
                domesticSupplierCodes,
                itemFailureCount
            );
        }

        private static Product? ResolveMatchedProduct(
            IReadOnlyDictionary<string, Product> productsByCode,
            IReadOnlyDictionary<string, List<Product>> productsBySupplierItem,
            PushProductsToHqItem item,
            PushProductsToHqResult result
        )
        {
            var productCode = NormalizeCode(item.ProductCode);
            if (productCode != null)
            {
                if (productsByCode.TryGetValue(productCode, out var matchedByCode))
                {
                    return matchedByCode;
                }

                result.Errors.Add($"商品不存在或已删除: {productCode}");
                return null;
            }

            var supplierItemKey = BuildSupplierItemKey(item.LocalSupplierCode, item.ItemNumber);
            if (supplierItemKey == null)
            {
                result.Errors.Add($"商品候选缺少有效匹配键: {DescribePushItem(item)}");
                return null;
            }

            if (!productsBySupplierItem.TryGetValue(supplierItemKey, out var matchedProducts))
            {
                result.Errors.Add($"未找到匹配商品: {DescribePushItem(item)}");
                return null;
            }

            if (matchedProducts.Count != 1)
            {
                result.Errors.Add($"匹配到多条本地商品: {DescribePushItem(item)}");
                return null;
            }

            return matchedProducts[0];
        }

        private static void AppendResolvedProductCode(List<string> productCodes, string? productCode)
        {
            var normalizedProductCode = NormalizeCode(productCode);
            if (
                normalizedProductCode == null
                || productCodes.Contains(normalizedProductCode, StringComparer.OrdinalIgnoreCase)
            )
            {
                return;
            }

            productCodes.Add(normalizedProductCode);
        }

        private static string DescribePushItem(PushProductsToHqItem item)
        {
            var productCode = NormalizeCode(item.ProductCode);
            if (productCode != null)
            {
                return $"商品编码={productCode}";
            }

            return $"供应商={NormalizeCode(item.LocalSupplierCode) ?? "NULL"}, 货号={NormalizeCode(item.ItemNumber) ?? "NULL"}";
        }

        private static async Task UpsertHqProductsAsync(
            ISqlSugarClient hqDb,
            List<Product> products,
            IReadOnlyDictionary<string, PushProductsToHqItem> pushCandidates,
            IReadOnlyDictionary<string, string> domesticProductImages,
            IReadOnlyDictionary<string, string> domesticSupplierCodes,
            PushToHqUpdateFieldSelection updateFields,
            HqProductSyncResult result,
            HashSet<string> existingProductCodes,
            string auditUser = "HBweb"
        )
        {
            var existingCodes = existingProductCodes;
            var effectiveAuditUser = NormalizeCode(auditUser) ?? "HBweb";

            var inserts = new List<DIC_商品信息字典表>();

            // 已存在的商品编码中，仅在当前字段掩码下确有可写字段时才需要更新。
            var updateProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var product in products)
            {
                var code = NormalizeCode(product.ProductCode);
                if (code == null || !existingCodes.Contains(code))
                {
                    continue;
                }

                if (
                    updateFields.IsAll
                    || updateFields.HasAny(
                        HqFieldItemNumber,
                        HqFieldBarcode,
                        HqFieldProductName,
                        HqFieldEnglishName,
                        HqFieldProductType,
                        HqFieldImage,
                        HqFieldPurchasePrice,
                        HqFieldRetailPrice,
                        HqFieldMiddlePackQuantity,
                        HqFieldSupplierCode
                    )
                )
                {
                    updateProductCodes.Add(code);
                }
            }

            // 收集每个业务键对应的全部物理主键 ID（重复业务键需全部更新），并按是否写回 CBP 供应商编码分组。
            var existingRows = new List<DIC_商品信息字典表>();
            foreach (var codeBatch in updateProductCodes.Chunk(HqCodeBatchSize))
            {
                var codes = codeBatch.ToList();
                existingRows.AddRange(
                    await hqDb.Queryable<DIC_商品信息字典表>()
                        .Where(row => row.H商品编码 != null && codes.Contains(row.H商品编码))
                        .Select(row => new DIC_商品信息字典表
                        {
                            ID = row.ID,
                            H商品编码 = row.H商品编码,
                        })
                        .ToListAsync()
                );
            }
            var existingIdsByCode = existingRows
                .Select(row => new
                {
                    Code = NormalizeCode(row.H商品编码),
                    Row = row,
                })
                .Where(item => item.Code != null)
                .GroupBy(item => item.Code!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Row).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );

            var productUpdateColumns = BuildHqProductUpdateColumns(updateFields);
            var productUpdateColumnsWithCbp = productUpdateColumns
                .Append(nameof(DIC_商品信息字典表.CBP供应商编码))
                .ToArray();
            var productUpdates = new List<DIC_商品信息字典表>();
            var productUpdatesNoCbp = new List<DIC_商品信息字典表>();

            foreach (var product in products)
            {
                var code = NormalizeCode(product.ProductCode);
                if (code == null)
                {
                    continue;
                }

                var hqProduct = MapProductToHqProduct(
                    product,
                    ResolveDomesticSupplierCode(product, domesticSupplierCodes),
                    ResolvePushCandidate(product, pushCandidates),
                    ResolveDomesticProductImage(product, domesticProductImages)
                );
                hqProduct.FGC_Creator = effectiveAuditUser;
                hqProduct.FGC_LastModifier = effectiveAuditUser;
                if (!existingCodes.Contains(code))
                {
                    inserts.Add(hqProduct);
                    existingCodes.Add(code);
                    continue;
                }

                if (
                    !updateFields.IsAll
                    && !updateFields.HasAny(
                        HqFieldItemNumber,
                        HqFieldBarcode,
                        HqFieldProductName,
                        HqFieldEnglishName,
                        HqFieldProductType,
                        HqFieldImage,
                        HqFieldPurchasePrice,
                        HqFieldRetailPrice,
                        HqFieldMiddlePackQuantity,
                        HqFieldSupplierCode
                    )
                )
                {
                    continue;
                }

                var updateWithCbp =
                    updateFields.Has(HqFieldSupplierCode)
                    && !string.IsNullOrWhiteSpace(hqProduct.CBP供应商编码);
                var physicalIds = existingIdsByCode.GetValueOrDefault(code);
                if (physicalIds == null || physicalIds.Count == 0)
                {
                    throw new InvalidOperationException("HQ 商品更新快照与物理记录不一致，请重试");
                }

                foreach (var physicalRow in physicalIds)
                {
                    var updateEntity = new DIC_商品信息字典表
                    {
                        ID = physicalRow.ID,
                        FGC_LastModifier = hqProduct.FGC_LastModifier,
                        FGC_LastModifyDate = hqProduct.FGC_LastModifyDate,
                    };

                    if (updateFields.IsAll)
                    {
                        updateEntity.H货号 = hqProduct.H货号;
                        updateEntity.H主条形码 = hqProduct.H主条形码;
                        updateEntity.H商品名称 = hqProduct.H商品名称;
                        updateEntity.H大写名称 = hqProduct.H大写名称;
                        updateEntity.H商品类型 = hqProduct.H商品类型;
                        updateEntity.H规格 = hqProduct.H规格;
                        updateEntity.H单位 = hqProduct.H单位;
                        updateEntity.H进货价 = hqProduct.H进货价;
                        updateEntity.H零售价 = hqProduct.H零售价;
                        updateEntity.H是否自动定价 = hqProduct.H是否自动定价;
                        updateEntity.H商品图片 = hqProduct.H商品图片;
                        updateEntity.中包数量 = hqProduct.中包数量;
                        updateEntity.H是否特殊商品 = hqProduct.H是否特殊商品;
                        updateEntity.H供货商编码 = hqProduct.H供货商编码;
                    }
                    else
                    {
                        if (updateFields.Has(HqFieldItemNumber))
                        {
                            updateEntity.H货号 = hqProduct.H货号;
                        }
                        if (updateFields.Has(HqFieldBarcode))
                        {
                            updateEntity.H主条形码 = hqProduct.H主条形码;
                        }
                        if (updateFields.Has(HqFieldEnglishName))
                        {
                            updateEntity.H商品名称 = hqProduct.H商品名称;
                        }
                        if (updateFields.Has(HqFieldProductName))
                        {
                            // 中文名只更新 HQ 大写名称；英文显示名由 englishName 字段单独控制。
                            updateEntity.H大写名称 = hqProduct.H大写名称;
                        }
                        if (updateFields.Has(HqFieldProductType))
                        {
                            updateEntity.H商品类型 = hqProduct.H商品类型;
                        }
                        if (updateFields.Has(HqFieldPurchasePrice))
                        {
                            updateEntity.H进货价 = hqProduct.H进货价;
                        }
                        if (updateFields.Has(HqFieldRetailPrice))
                        {
                            updateEntity.H零售价 = hqProduct.H零售价;
                        }
                        if (updateFields.Has(HqFieldImage))
                        {
                            updateEntity.H商品图片 = hqProduct.H商品图片;
                        }
                        if (updateFields.Has(HqFieldMiddlePackQuantity))
                        {
                            updateEntity.中包数量 = hqProduct.中包数量;
                        }
                        if (updateFields.Has(HqFieldSupplierCode))
                        {
                            updateEntity.H供货商编码 = hqProduct.H供货商编码;
                        }
                    }

                    if (updateWithCbp)
                    {
                        updateEntity.CBP供应商编码 = hqProduct.CBP供应商编码;
                        productUpdates.Add(updateEntity);
                    }
                    else
                    {
                        productUpdatesNoCbp.Add(updateEntity);
                    }
                }

                result.ProductsUpdated++;
            }

            if (productUpdates.Count > 0)
            {
                foreach (var batch in productUpdates.Chunk(HqWriteBatchSize))
                {
                    await hqDb.Updateable(batch.ToList())
                        .UpdateColumns(productUpdateColumnsWithCbp)
                        .ExecuteCommandAsync();
                }
            }
            if (productUpdatesNoCbp.Count > 0)
            {
                foreach (var batch in productUpdatesNoCbp.Chunk(HqWriteBatchSize))
                {
                    await hqDb.Updateable(batch.ToList())
                        .UpdateColumns(productUpdateColumns)
                        .ExecuteCommandAsync();
                }
            }

            if (inserts.Count > 0)
            {
                foreach (var batch in inserts.Chunk(HqWriteBatchSize))
                {
                    await hqDb.Insertable(batch.ToList())
                        .IgnoreColumns(row => row.ID)
                        .ExecuteCommandAsync();
                }
                result.ProductsAdded += inserts.Count;
            }
        }

        private static string[] BuildHqProductUpdateColumns(PushToHqUpdateFieldSelection updateFields)
        {
            if (updateFields.IsAll)
            {
                return new[]
                {
                    nameof(DIC_商品信息字典表.H货号),
                    nameof(DIC_商品信息字典表.H主条形码),
                    nameof(DIC_商品信息字典表.H商品名称),
                    nameof(DIC_商品信息字典表.H大写名称),
                    nameof(DIC_商品信息字典表.H商品类型),
                    nameof(DIC_商品信息字典表.H规格),
                    nameof(DIC_商品信息字典表.H单位),
                    nameof(DIC_商品信息字典表.H进货价),
                    nameof(DIC_商品信息字典表.H零售价),
                    nameof(DIC_商品信息字典表.H是否自动定价),
                    nameof(DIC_商品信息字典表.H商品图片),
                    nameof(DIC_商品信息字典表.中包数量),
                    nameof(DIC_商品信息字典表.H是否特殊商品),
                    nameof(DIC_商品信息字典表.H供货商编码),
                    nameof(DIC_商品信息字典表.FGC_LastModifier),
                    nameof(DIC_商品信息字典表.FGC_LastModifyDate),
                };
            }

            var columns = new List<string>
            {
                nameof(DIC_商品信息字典表.FGC_LastModifier),
                nameof(DIC_商品信息字典表.FGC_LastModifyDate),
            };
            if (updateFields.Has(HqFieldItemNumber))
            {
                columns.Add(nameof(DIC_商品信息字典表.H货号));
            }
            if (updateFields.Has(HqFieldBarcode))
            {
                columns.Add(nameof(DIC_商品信息字典表.H主条形码));
            }
            if (updateFields.Has(HqFieldEnglishName))
            {
                columns.Add(nameof(DIC_商品信息字典表.H商品名称));
            }
            if (updateFields.Has(HqFieldProductName))
            {
                columns.Add(nameof(DIC_商品信息字典表.H大写名称));
            }
            if (updateFields.Has(HqFieldProductType))
            {
                columns.Add(nameof(DIC_商品信息字典表.H商品类型));
            }
            if (updateFields.Has(HqFieldPurchasePrice))
            {
                columns.Add(nameof(DIC_商品信息字典表.H进货价));
            }
            if (updateFields.Has(HqFieldRetailPrice))
            {
                columns.Add(nameof(DIC_商品信息字典表.H零售价));
            }
            if (updateFields.Has(HqFieldImage))
            {
                columns.Add(nameof(DIC_商品信息字典表.H商品图片));
            }
            if (updateFields.Has(HqFieldMiddlePackQuantity))
            {
                columns.Add(nameof(DIC_商品信息字典表.中包数量));
            }
            if (updateFields.Has(HqFieldSupplierCode))
            {
                columns.Add(nameof(DIC_商品信息字典表.H供货商编码));
            }
            return columns.ToArray();
        }

        private static string[] BuildHqRetailPriceUpdateColumns(PushToHqUpdateFieldSelection updateFields)
        {
            if (updateFields.IsAll)
            {
                return new[]
                {
                    nameof(DIC_商品零售价表.H分店商品编码),
                    nameof(DIC_商品零售价表.H供应商编码),
                    nameof(DIC_商品零售价表.H分店供应商编码),
                    nameof(DIC_商品零售价表.H进货价),
                    nameof(DIC_商品零售价表.H分店零售价),
                    nameof(DIC_商品零售价表.H是否自动定价),
                    nameof(DIC_商品零售价表.FGC_LastModifier),
                    nameof(DIC_商品零售价表.FGC_LastModifyDate),
                };
            }

            var columns = new List<string>
            {
                nameof(DIC_商品零售价表.FGC_LastModifier),
                nameof(DIC_商品零售价表.FGC_LastModifyDate),
            };
            if (updateFields.Has(HqFieldStorePurchasePrice))
            {
                columns.Add(nameof(DIC_商品零售价表.H进货价));
            }
            if (updateFields.Has(HqFieldStoreRetailPrice))
            {
                columns.Add(nameof(DIC_商品零售价表.H分店零售价));
            }
            if (updateFields.Has(HqFieldSupplierCode))
            {
                columns.Add(nameof(DIC_商品零售价表.H分店商品编码));
                columns.Add(nameof(DIC_商品零售价表.H供应商编码));
                columns.Add(nameof(DIC_商品零售价表.H分店供应商编码));
            }
            return columns.ToArray();
        }

        private static async Task UpsertHqWarehouseInventoriesAsync(
            ISqlSugarClient hqDb,
            List<Product> products,
            IReadOnlyDictionary<string, PushProductsToHqItem> inventoryCandidates,
            PushToHqUpdateFieldSelection updateFields,
            PushProductsToHqResult result,
            string auditUser = "HBweb"
        )
        {
            var inventoryProductCodes = products
                .Select(row => NormalizeCode(row.ProductCode))
                .Where(code => code != null && inventoryCandidates.ContainsKey(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (inventoryProductCodes.Count == 0)
            {
                return;
            }

            var productByCode = products
                .Where(row => NormalizeCode(row.ProductCode) != null)
                .ToDictionary(row => NormalizeCode(row.ProductCode)!, StringComparer.OrdinalIgnoreCase);
            var effectiveAuditUser = NormalizeCode(auditUser) ?? "HBweb";
            var existingInventories = new List<CBP_DIC_商品库存表>();
            foreach (var codeBatch in inventoryProductCodes.Chunk(HqCodeBatchSize))
            {
                var codes = codeBatch.ToList();
                existingInventories.AddRange(
                    await hqDb.Queryable<CBP_DIC_商品库存表>()
                        .Where(row => row.H商品编码 != null && codes.Contains(row.H商品编码))
                        .ToListAsync()
                );
            }
            var existingInventoryByCode = existingInventories
                .Where(row => !string.IsNullOrWhiteSpace(row.H商品编码))
                .ToDictionary(row => row.H商品编码!, StringComparer.OrdinalIgnoreCase);
            var now = DateTime.Now;
            var inserts = new List<CBP_DIC_商品库存表>();
            var inventoryUpdates = new List<CBP_DIC_商品库存表>();
            var inventoryUpdateColumns = new List<string>
            {
                nameof(CBP_DIC_商品库存表.FGC_LastModifier),
                nameof(CBP_DIC_商品库存表.FGC_LastModifyDate),
            };
            if (updateFields.IsAll || updateFields.Has(HqFieldInventoryDomesticPrice))
            {
                inventoryUpdateColumns.Add(nameof(CBP_DIC_商品库存表.H国内价格));
            }
            if (updateFields.IsAll || updateFields.Has(HqFieldInventoryImportPrice))
            {
                inventoryUpdateColumns.Add(nameof(CBP_DIC_商品库存表.H进口价格));
            }
            if (updateFields.IsAll || updateFields.Has(HqFieldInventoryOemPrice))
            {
                inventoryUpdateColumns.Add(nameof(CBP_DIC_商品库存表.H贴牌价格));
            }

            foreach (var productCode in inventoryProductCodes)
            {
                var candidate = inventoryCandidates[productCode];
                if (!productByCode.TryGetValue(productCode, out var product))
                {
                    continue;
                }

                if (existingInventoryByCode.TryGetValue(productCode, out var existingInventory))
                {
                    var updateEntity = new CBP_DIC_商品库存表
                    {
                        ID = existingInventory.ID,
                        FGC_LastModifier = effectiveAuditUser,
                        FGC_LastModifyDate = now,
                    };
                    if (updateFields.IsAll || updateFields.Has(HqFieldInventoryDomesticPrice))
                    {
                        updateEntity.H国内价格 = candidate.DomesticPrice ?? existingInventory.H国内价格;
                    }
                    if (updateFields.IsAll || updateFields.Has(HqFieldInventoryImportPrice))
                    {
                        updateEntity.H进口价格 = candidate.ImportPrice ?? existingInventory.H进口价格;
                    }
                    if (updateFields.IsAll || updateFields.Has(HqFieldInventoryOemPrice))
                    {
                        updateEntity.H贴牌价格 = candidate.OemPrice ?? existingInventory.H贴牌价格;
                    }
                    inventoryUpdates.Add(updateEntity);
                    result.WarehouseInventoriesUpdated++;
                    continue;
                }

                inserts.Add(new CBP_DIC_商品库存表
                {
                    HGUID = Guid.NewGuid().ToString(),
                    H商品编码 = productCode,
                    H国内价格 = candidate.DomesticPrice,
                    H进口价格 = candidate.ImportPrice,
                    H贴牌价格 = candidate.OemPrice,
                    H库存 = 0,
                    H最小订货量 = 0,
                    H库存金额 = 0,
                    H库存预警数 = 0,
                    // 新增库存记录仍按本地商品启用状态初始化，后续货柜发送不再改动该状态。
                    H使用状态 = product.IsActive ? 1 : 0,
                    FGC_Creator = effectiveAuditUser,
                    FGC_CreateDate = now,
                    FGC_LastModifier = effectiveAuditUser,
                    FGC_LastModifyDate = now,
                });
            }

            if (inventoryUpdates.Count > 0)
            {
                foreach (var batch in inventoryUpdates.Chunk(HqWriteBatchSize))
                {
                    await hqDb.Updateable(batch.ToList())
                        .UpdateColumns(inventoryUpdateColumns.ToArray())
                        .ExecuteCommandAsync();
                }
            }

            if (inserts.Count > 0)
            {
                foreach (var batch in inserts.Chunk(HqWriteBatchSize))
                {
                    await hqDb.Insertable(batch.ToList())
                        .IgnoreColumns(row => row.ID)
                        .ExecuteCommandAsync();
                }
                result.WarehouseInventoriesCreated += inserts.Count;
            }
        }

        private static async Task UpsertHqRetailPricesAsync(
            ISqlSugarClient hqDb,
            List<Product> products,
            IReadOnlyDictionary<string, PushProductsToHqItem> pushCandidates,
            IReadOnlyDictionary<string, List<string>> storeCodesByProduct,
            List<string> activeStoreCodes,
            PushToHqUpdateFieldSelection updateFields,
            HqProductSyncResult result
        )
        {
            if (
                activeStoreCodes.Count == 0
                || !storeCodesByProduct.Values.Any(storeCodes => storeCodes.Count > 0)
            )
            {
                return;
            }

            var productCodes = products
                .Select(row => NormalizeCode(row.ProductCode))
                .Where(code => code != null)
                .Select(code => code!)
                .ToList();
            var existingRows = new List<DIC_商品零售价表>();
            foreach (var codeBatch in productCodes.Chunk(HqCodeBatchSize))
            {
                var codes = codeBatch.ToList();
                foreach (var storeBatch in activeStoreCodes.Chunk(HqCodeBatchSize))
                {
                    var stores = storeBatch.ToList();
                    existingRows.AddRange(
                        await hqDb.Queryable<DIC_商品零售价表>()
                            .Where(row =>
                                stores.Contains(row.H分店代码)
                                && codes.Contains(row.H商品编码)
                            )
                            .Select(row => new DIC_商品零售价表
                            {
                                ID = row.ID,
                                H分店代码 = row.H分店代码,
                                H商品编码 = row.H商品编码,
                            })
                            .ToListAsync()
                    );
                }
            }
            var existingRowsByKey = existingRows
                .Select(row => new
                {
                    Key = BuildStoreProductKey(row.H分店代码, row.H商品编码),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Row).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );
            var existingKeys = existingRowsByKey.Keys
                .Where(key => key != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var inserts = new List<DIC_商品零售价表>();
            var retailPriceUpdates = new List<DIC_商品零售价表>();
            var retailPriceUpdateColumns = BuildHqRetailPriceUpdateColumns(updateFields);

            foreach (var product in products)
            {
                var productCode = NormalizeCode(product.ProductCode);
                if (
                    productCode == null
                    || !storeCodesByProduct.TryGetValue(productCode, out var storeCodes)
                )
                {
                    continue;
                }

                foreach (var storeCode in storeCodes)
                {
                    var key = BuildStoreProductKey(storeCode, productCode);
                    if (key == null)
                    {
                        continue;
                    }

                    var hqPrice = MapProductToHqRetailPrice(
                        product,
                        storeCode,
                        ResolvePushCandidate(product, pushCandidates)
                    );
                    if (!existingKeys.Contains(key))
                    {
                        inserts.Add(hqPrice);
                        existingKeys.Add(key);
                        continue;
                    }

                    var physicalIds = existingRowsByKey.GetValueOrDefault(key);
                    if (physicalIds == null || physicalIds.Count == 0)
                    {
                        result.StoreRetailPricesUpdated++;
                        continue;
                    }

                    foreach (var physicalRow in physicalIds)
                    {
                        // 已有 HQ 分店零售价只更新价格/供应商/自动定价和修改信息，不覆盖特殊商品、库存、活动和动态销售字段。
                        var updateEntity = new DIC_商品零售价表
                        {
                            ID = physicalRow.ID,
                            FGC_LastModifier = hqPrice.FGC_LastModifier,
                            FGC_LastModifyDate = hqPrice.FGC_LastModifyDate,
                        };

                        if (updateFields.IsAll)
                        {
                            updateEntity.H分店商品编码 = hqPrice.H分店商品编码;
                            updateEntity.H供应商编码 = hqPrice.H供应商编码;
                            updateEntity.H分店供应商编码 = hqPrice.H分店供应商编码;
                            updateEntity.H进货价 = hqPrice.H进货价;
                            updateEntity.H分店零售价 = hqPrice.H分店零售价;
                            updateEntity.H是否自动定价 = hqPrice.H是否自动定价;
                        }
                        else
                        {
                            if (updateFields.Has(HqFieldStorePurchasePrice))
                            {
                                updateEntity.H进货价 = hqPrice.H进货价;
                            }
                            if (updateFields.Has(HqFieldStoreRetailPrice))
                            {
                                updateEntity.H分店零售价 = hqPrice.H分店零售价;
                            }
                            if (updateFields.Has(HqFieldSupplierCode))
                            {
                                updateEntity.H分店商品编码 = hqPrice.H分店商品编码;
                                updateEntity.H供应商编码 = hqPrice.H供应商编码;
                                updateEntity.H分店供应商编码 = hqPrice.H分店供应商编码;
                            }
                        }

                        retailPriceUpdates.Add(updateEntity);
                    }

                    result.StoreRetailPricesUpdated++;
                }
            }

            if (retailPriceUpdates.Count > 0)
            {
                foreach (var batch in retailPriceUpdates.Chunk(HqWriteBatchSize))
                {
                    await hqDb.Updateable(batch.ToList())
                        .UpdateColumns(retailPriceUpdateColumns)
                        .ExecuteCommandAsync();
                }
            }

            if (inserts.Count > 0)
            {
                foreach (var batch in inserts.Chunk(HqWriteBatchSize))
                {
                    await hqDb.Insertable(batch.ToList())
                        .IgnoreColumns(row => row.ID)
                        .ExecuteCommandAsync();
                }
                result.StoreRetailPricesCreated += inserts.Count;
            }
        }

        private static async Task UpsertHqProductSetCodesAsync(
            ISqlSugarClient hqDb,
            List<Product> products,
            List<ProductSetCode> productSetCodes,
            HqProductSyncResult result,
            string auditUser = "HBweb",
            PushToHqUpdateFieldSelection? updateFields = null
        )
        {
            if (productSetCodes.Count == 0)
            {
                return;
            }

            updateFields ??= new PushToHqUpdateFieldSelection(null);

            var productByCode = products
                .Where(row => NormalizeCode(row.ProductCode) != null)
                .ToDictionary(row => NormalizeCode(row.ProductCode)!, StringComparer.OrdinalIgnoreCase);
            var productCodes = productByCode.Keys.ToList();
            var effectiveAuditUser = NormalizeCode(auditUser) ?? "HBweb";
            var existingRows = new List<DIC_一品多码表>();
            foreach (var codeBatch in productCodes.Chunk(HqCodeBatchSize))
            {
                var codes = codeBatch.ToList();
                existingRows.AddRange(
                    await hqDb.Queryable<DIC_一品多码表>()
                        .Where(row => row.H商品编码 != null && codes.Contains(row.H商品编码))
                        .Select(row => new DIC_一品多码表
                        {
                            ID = row.ID,
                            HGUID = row.HGUID,
                            H商品编码 = row.H商品编码,
                            H多码商品编号 = row.H多码商品编号,
                            H多条形码 = row.H多条形码,
                        })
                        .ToListAsync()
                );
            }
            var existingByBusinessKey = existingRows
                .Select(row => new
                {
                    Key = BuildProductSetCodeBusinessKey(row.H商品编码, row.H多码商品编号),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Row).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );
            var existingByGuidKey = existingRows
                .Select(row => new
                {
                    Key = BuildProductSetCodeBusinessKey(row.H商品编码, row.HGUID),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Row).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );
            var existingByBarcodeKey = existingRows
                .Select(row => new
                {
                    Key = BuildProductSetCodeBusinessKey(row.H商品编码, row.H多条形码),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Row).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );
            var inserts = new List<DIC_一品多码表>();
            var productSetCodeUpdates = new List<DIC_一品多码表>();
            var productSetCodeUpdateColumns = new List<string>
            {
                nameof(DIC_一品多码表.FGC_LastModifier),
                nameof(DIC_一品多码表.FGC_LastModifyDate),
            };
            if (updateFields.Has(HqFieldSupplierCode))
            {
                productSetCodeUpdateColumns.Add(nameof(DIC_一品多码表.H供应商编码));
            }
            if (updateFields.Has(HqFieldProductSetCodes))
            {
                productSetCodeUpdateColumns.AddRange(new[]
                {
                    nameof(DIC_一品多码表.H主条形码),
                    nameof(DIC_一品多码表.H多条形码),
                    nameof(DIC_一品多码表.H进货价),
                    nameof(DIC_一品多码表.H一品多码零售价),
                    nameof(DIC_一品多码表.H使用状态),
                    nameof(DIC_一品多码表.H是否自动定价),
                });
            }

            foreach (var setCode in productSetCodes)
            {
                var productCode = NormalizeCode(setCode.ProductCode);
                var setProductCode = NormalizeCode(setCode.SetProductCode);
                var key = BuildProductSetCodeBusinessKey(setCode.ProductCode, setCode.SetProductCode);
                if (
                    productCode == null
                    || setProductCode == null
                    || key == null
                    || !productByCode.TryGetValue(productCode, out var product)
                )
                {
                    continue;
                }

                var lookupGuidKey = BuildProductSetCodeBusinessKey(
                    productCode,
                    NormalizeCode(setCode.SetCodeId)
                );
                var lookupBarcodeKey = BuildProductSetCodeBusinessKey(
                    productCode,
                    NormalizeCode(setCode.SetBarcode)
                );
                var existingRowsForUpdate =
                    existingByBusinessKey.GetValueOrDefault(key)
                    ?? (lookupGuidKey == null ? null : existingByGuidKey.GetValueOrDefault(lookupGuidKey))
                    ?? (lookupBarcodeKey == null ? null : existingByBarcodeKey.GetValueOrDefault(lookupBarcodeKey));
                if (
                    existingRowsForUpdate != null
                    && !updateFields.Has(HqFieldProductSetCodes)
                )
                {
                    var now = DateTime.Now;
                    foreach (var existing in existingRowsForUpdate)
                    {
                        // 仅同步供应商时不计算未选中的套装成本，避免无关成本投影阻断更新。
                        productSetCodeUpdates.Add(new DIC_一品多码表
                        {
                            ID = existing.ID,
                            H供应商编码 = ResolveHqSupplierCode(product),
                            FGC_LastModifier = effectiveAuditUser,
                            FGC_LastModifyDate = now,
                        });
                    }
                    result.ProductSetCodesUpdated++;
                    continue;
                }

                // SetType=1 已由本地统一服务分摊，SetType=2 已同步为父商品成本；HQ 映射不得再次计算。
                // 缺少 HQ 记录时仍完整映射并创建，因此本地必填派生成本缺失会明确失败。
                var hqSetCode = MapProductSetCodeToHq(setCode, product);
                hqSetCode.FGC_Creator = effectiveAuditUser;
                hqSetCode.FGC_LastModifier = effectiveAuditUser;
                var mappedGuidKey = BuildProductSetCodeBusinessKey(productCode, hqSetCode.HGUID);
                var mappedBarcodeKey = BuildProductSetCodeBusinessKey(productCode, hqSetCode.H多条形码);
                if (existingRowsForUpdate == null)
                {
                    inserts.Add(hqSetCode);
                    existingByBusinessKey[key] = new List<DIC_一品多码表> { hqSetCode };
                    if (mappedGuidKey != null)
                    {
                        existingByGuidKey[mappedGuidKey] = new List<DIC_一品多码表> { hqSetCode };
                    }
                    if (mappedBarcodeKey != null)
                    {
                        existingByBarcodeKey[mappedBarcodeKey] = new List<DIC_一品多码表> { hqSetCode };
                    }
                    continue;
                }

                foreach (var existing in existingRowsForUpdate)
                {
                    productSetCodeUpdates.Add(new DIC_一品多码表
                    {
                        ID = existing.ID,
                        H供应商编码 = hqSetCode.H供应商编码,
                        H主条形码 = hqSetCode.H主条形码,
                        H多条形码 = hqSetCode.H多条形码,
                        H进货价 = hqSetCode.H进货价,
                        H一品多码零售价 = hqSetCode.H一品多码零售价,
                        H使用状态 = hqSetCode.H使用状态,
                        H是否自动定价 = hqSetCode.H是否自动定价,
                        FGC_LastModifier = hqSetCode.FGC_LastModifier,
                        FGC_LastModifyDate = hqSetCode.FGC_LastModifyDate,
                    });
                }
                result.ProductSetCodesUpdated++;
            }

            if (productSetCodeUpdates.Count > 0)
            {
                var uniqueUpdates = productSetCodeUpdates
                    .GroupBy(row => row.ID)
                    .Select(group => group.Last());
                foreach (var batch in uniqueUpdates.Chunk(HqWriteBatchSize))
                {
                    await hqDb.Updateable(batch.ToList())
                        // 已有记录严格按字段掩码更新；新增记录仍由下方完整写入。
                        .UpdateColumns(productSetCodeUpdateColumns.ToArray())
                        .ExecuteCommandAsync();
                }
            }

            if (inserts.Count > 0)
            {
                foreach (var batch in inserts.Chunk(HqWriteBatchSize))
                {
                    await hqDb.Insertable(batch.ToList())
                        .IgnoreColumns(row => row.ID)
                        .ExecuteCommandAsync();
                }
                result.ProductSetCodesCreated += inserts.Count;
            }
        }

        private static async Task UpsertHqStoreMultiCodesAsync(
            ISqlSugarClient hqDb,
            List<Product> products,
            List<ProductSetCode> productSetCodes,
            List<StoreMultiCodeProduct> storeMultiCodes,
            IReadOnlyDictionary<string, List<string>> storeCodesByProduct,
            List<string> activeStoreCodes,
            HqProductSyncResult result,
            string auditUser = "HBweb",
            PushToHqUpdateFieldSelection? updateFields = null
        )
        {
            if (
                productSetCodes.Count == 0
                || activeStoreCodes.Count == 0
                || !storeCodesByProduct.Values.Any(storeCodes => storeCodes.Count > 0)
            )
            {
                return;
            }

            updateFields ??= new PushToHqUpdateFieldSelection(null);

            var productByCode = products
                .Where(row => NormalizeCode(row.ProductCode) != null)
                .ToDictionary(row => NormalizeCode(row.ProductCode)!, StringComparer.OrdinalIgnoreCase);
            var productCodes = productByCode.Keys.ToList();
            var effectiveAuditUser = NormalizeCode(auditUser) ?? "HBweb";
            var existingRows = new List<DIC_分店一品多码表>();
            foreach (var codeBatch in productCodes.Chunk(HqCodeBatchSize))
            {
                var codes = codeBatch.ToList();
                foreach (var storeBatch in activeStoreCodes.Chunk(HqCodeBatchSize))
                {
                    var stores = storeBatch.ToList();
                    existingRows.AddRange(
                        await hqDb.Queryable<DIC_分店一品多码表>()
                            .Where(row =>
                                row.H分店代码 != null
                                && stores.Contains(row.H分店代码)
                                && row.H商品编码 != null
                                && codes.Contains(row.H商品编码)
                            )
                            .Select(row => new DIC_分店一品多码表
                            {
                                ID = row.ID,
                                HGUID = row.HGUID,
                                H分店代码 = row.H分店代码,
                                H商品编码 = row.H商品编码,
                                H多码商品编码 = row.H多码商品编码,
                                H分店多码商品编码 = row.H分店多码商品编码,
                                H多条形码 = row.H多条形码,
                            })
                            .ToListAsync()
                    );
                }
            }
            var existingByBusinessKey = existingRows
                .Select(row => new
                {
                    Key = BuildStoreMultiCodeKey(row.H分店代码, row.H商品编码, row.H多码商品编码),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Row).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );
            var existingByGuidKey = existingRows
                .Select(row => new
                {
                    Key = BuildStoreMultiCodeKey(row.H分店代码, row.H商品编码, row.HGUID),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Row).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );
            var existingByBarcodeKey = existingRows
                .Select(row => new
                {
                    Key = BuildStoreMultiCodeKey(row.H分店代码, row.H商品编码, row.H多条形码),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Row).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );
            var existingByStoreMultiProductKey = existingRows
                .Select(row => new
                {
                    Key = BuildStoreMultiCodeKey(row.H分店代码, row.H商品编码, row.H分店多码商品编码),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Row).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );
            var storeMultiCodeByKey = storeMultiCodes
                .Select(row => new
                {
                    Key = BuildStoreMultiCodeKey(
                        row.StoreCode,
                        row.ProductCode,
                        row.MultiCodeProductCode
                    ),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!)
                .ToDictionary(group => group.Key, group => group.First().Row, StringComparer.OrdinalIgnoreCase);
            var inserts = new List<DIC_分店一品多码表>();
            var storeMultiCodeUpdates = new List<DIC_分店一品多码表>();
            var storeMultiCodeUpdateColumns = new List<string>
            {
                nameof(DIC_分店一品多码表.FGC_LastModifier),
                nameof(DIC_分店一品多码表.FGC_LastModifyDate),
            };
            if (updateFields.Has(HqFieldSupplierCode))
            {
                storeMultiCodeUpdateColumns.Add(nameof(DIC_分店一品多码表.H供应商编码));
            }
            if (updateFields.Has(HqFieldStoreMultiCodes))
            {
                storeMultiCodeUpdateColumns.AddRange(new[]
                {
                    nameof(DIC_分店一品多码表.H分店商品编码),
                    nameof(DIC_分店一品多码表.H分店多码商品编码),
                    nameof(DIC_分店一品多码表.H主条形码),
                    nameof(DIC_分店一品多码表.H多条形码),
                    nameof(DIC_分店一品多码表.H进货价),
                    nameof(DIC_分店一品多码表.H折扣率),
                    nameof(DIC_分店一品多码表.H一品多码零售价),
                    nameof(DIC_分店一品多码表.H是否自动定价),
                    nameof(DIC_分店一品多码表.H是否特殊商品),
                    nameof(DIC_分店一品多码表.H使用状态),
                });
            }

            foreach (var productGroup in productSetCodes
                .Select(setCode => new
                {
                    ProductCode = NormalizeCode(setCode.ProductCode),
                    SetCode = setCode,
                })
                .Where(item =>
                    item.ProductCode != null && productByCode.ContainsKey(item.ProductCode!)
                )
                .GroupBy(item => item.ProductCode!, StringComparer.OrdinalIgnoreCase))
            {
                var productCode = productGroup.Key;
                if (!storeCodesByProduct.TryGetValue(productCode, out var storeCodes))
                {
                    continue;
                }

                var product = productByCode[productCode];
                foreach (var storeCode in storeCodes)
                {
                    foreach (var setCode in productGroup.Select(item => item.SetCode))
                    {
                        var multiCode = NormalizeCode(setCode.SetProductCode);
                        var key = BuildStoreMultiCodeKey(storeCode, productCode, multiCode);
                        if (multiCode == null || key == null)
                        {
                            continue;
                        }

                        storeMultiCodeByKey.TryGetValue(key, out var storeMultiCode);
                        var lookupGuidKey = BuildStoreMultiCodeKey(
                            storeCode,
                            productCode,
                            NormalizeCode(storeMultiCode?.UUID)
                        );
                        var lookupBarcodeKey = BuildStoreMultiCodeKey(
                            storeCode,
                            productCode,
                            NormalizeCode(storeMultiCode?.MultiBarcode)
                                ?? NormalizeCode(setCode.SetBarcode)
                        );
                        var lookupStoreMultiProductKey = BuildStoreMultiCodeKey(
                            storeCode,
                            productCode,
                            NormalizeCode(storeMultiCode?.StoreMultiCodeProductCode)
                                ?? storeCode + multiCode
                        );
                        var existingRowsForUpdate =
                            existingByBusinessKey.GetValueOrDefault(key)
                            ?? (lookupGuidKey == null ? null : existingByGuidKey.GetValueOrDefault(lookupGuidKey))
                            ?? (lookupBarcodeKey == null ? null : existingByBarcodeKey.GetValueOrDefault(lookupBarcodeKey))
                            ?? (lookupStoreMultiProductKey == null
                                ? null
                                : existingByStoreMultiProductKey.GetValueOrDefault(lookupStoreMultiProductKey));
                        if (
                            existingRowsForUpdate != null
                            && !updateFields.Has(HqFieldStoreMultiCodes)
                        )
                        {
                            var now = DateTime.Now;
                            foreach (var existing in existingRowsForUpdate)
                            {
                                // 仅同步供应商时不解析未选中的分店套装成本。
                                storeMultiCodeUpdates.Add(new DIC_分店一品多码表
                                {
                                    ID = existing.ID,
                                    H供应商编码 = ResolveHqSupplierCode(product),
                                    FGC_LastModifier = effectiveAuditUser,
                                    FGC_LastModifyDate = now,
                                });
                            }
                            result.StoreMultiCodesUpdated++;
                            continue;
                        }

                        var hqStoreMultiCode = MapStoreMultiCodeToHq(
                            storeCode,
                            product,
                            setCode,
                            storeMultiCode
                        );
                        hqStoreMultiCode.FGC_Creator = effectiveAuditUser;
                        hqStoreMultiCode.FGC_LastModifier = effectiveAuditUser;
                        var mappedGuidKey = BuildStoreMultiCodeKey(
                            storeCode,
                            productCode,
                            hqStoreMultiCode.HGUID
                        );
                        var mappedBarcodeKey = BuildStoreMultiCodeKey(
                            storeCode,
                            productCode,
                            hqStoreMultiCode.H多条形码
                        );
                        var mappedStoreMultiProductKey = BuildStoreMultiCodeKey(
                            storeCode,
                            productCode,
                            hqStoreMultiCode.H分店多码商品编码
                        );
                        if (existingRowsForUpdate == null)
                        {
                            inserts.Add(hqStoreMultiCode);
                            existingByBusinessKey[key] = new List<DIC_分店一品多码表> { hqStoreMultiCode };
                            if (mappedGuidKey != null)
                            {
                                existingByGuidKey[mappedGuidKey] = new List<DIC_分店一品多码表> { hqStoreMultiCode };
                            }
                            if (mappedBarcodeKey != null)
                            {
                                existingByBarcodeKey[mappedBarcodeKey] = new List<DIC_分店一品多码表> { hqStoreMultiCode };
                            }
                            if (mappedStoreMultiProductKey != null)
                            {
                                existingByStoreMultiProductKey[mappedStoreMultiProductKey] = new List<DIC_分店一品多码表> { hqStoreMultiCode };
                            }
                            continue;
                        }

                        foreach (var existing in existingRowsForUpdate)
                        {
                            // 兼容历史错码命中时，每条物理记录都保留自己的 HQ 既有业务编码。
                            // 分店一品多码更新不触碰库存、活动和动态销售字段。
                            storeMultiCodeUpdates.Add(new DIC_分店一品多码表
                            {
                                ID = existing.ID,
                                H分店商品编码 = hqStoreMultiCode.H分店商品编码,
                                H分店多码商品编码 = existing.H分店多码商品编码,
                                H供应商编码 = hqStoreMultiCode.H供应商编码,
                                H主条形码 = hqStoreMultiCode.H主条形码,
                                H多条形码 = hqStoreMultiCode.H多条形码,
                                H进货价 = hqStoreMultiCode.H进货价,
                                H折扣率 = hqStoreMultiCode.H折扣率,
                                H一品多码零售价 = hqStoreMultiCode.H一品多码零售价,
                                H是否自动定价 = hqStoreMultiCode.H是否自动定价,
                                H是否特殊商品 = hqStoreMultiCode.H是否特殊商品,
                                H使用状态 = hqStoreMultiCode.H使用状态,
                                FGC_LastModifier = hqStoreMultiCode.FGC_LastModifier,
                                FGC_LastModifyDate = hqStoreMultiCode.FGC_LastModifyDate,
                            });
                        }
                        result.StoreMultiCodesUpdated++;
                    }
                }
            }

            if (storeMultiCodeUpdates.Count > 0)
            {
                var uniqueUpdates = storeMultiCodeUpdates
                    .GroupBy(row => row.ID)
                    .Select(group => group.Last());
                foreach (var batch in uniqueUpdates.Chunk(HqWriteBatchSize))
                {
                    await hqDb.Updateable(batch.ToList())
                        // 供应商单字段同步不得覆盖分店多码的价格、条码和状态。
                        .UpdateColumns(storeMultiCodeUpdateColumns.ToArray())
                        .ExecuteCommandAsync();
                }
            }

            if (inserts.Count > 0)
            {
                foreach (var batch in inserts.Chunk(HqWriteBatchSize))
                {
                    await hqDb.Insertable(batch.ToList())
                        .IgnoreColumns(row => row.ID)
                        .ExecuteCommandAsync();
                }
                result.StoreMultiCodesCreated += inserts.Count;
            }
        }

        private async Task SyncFullWithShadowAsync(ISqlSugarClient db, HqProductSyncResult result)
        {
            // 影子表会替换整张 Product；先保存 Web 正数成本，避免 HQ 空成本在全量切换时抹掉它。
            var existingProducts = await db.Queryable<Product>()
                .Where(row => row.ProductCode != null)
                .ToListAsync();
            var existingProductsByCode = existingProducts
                .Where(row => NormalizeCode(row.ProductCode) != null)
                .GroupBy(row => NormalizeCode(row.ProductCode)!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var syncRunId = await db.Ado.SqlQuerySingleAsync<long>(
                """
                DECLARE @SyncRunId BIGINT;
                EXEC dbo.usp_ProductShadow_Prepare
                    @SyncRunId = @SyncRunId OUTPUT,
                    @TriggeredBy = N'ProductHqSyncService',
                    @DropExistingShadow = 1;
                SELECT @SyncRunId;
                """
            );

            var hqRows = await QueryActiveHqProductsAsync();
            var products = hqRows
                .Select(row =>
                {
                    var code = NormalizeCode(row.H商品编码);
                    return MapNewProduct(
                        row,
                        code != null ? existingProductsByCode.GetValueOrDefault(code) : null
                    );
                })
                .ToList();
            foreach (var batch in products.Chunk(WriteBatchSize))
            {
                await db.Fastest<Product>()
                    .AS(ShadowTableName)
                    .PageSize(WriteBatchSize)
                    .BulkCopyAsync(batch.ToList());
            }

            await db.Ado.ExecuteCommandAsync(
                "EXEC dbo.usp_ProductShadow_Validate @SyncRunId, @SourceRowCount",
                new SugarParameter("@SyncRunId", syncRunId),
                new SugarParameter("@SourceRowCount", hqRows.Count)
            );
            await db.Ado.ExecuteCommandAsync(
                "EXEC dbo.usp_ProductShadow_Swap @SyncRunId",
                new SugarParameter("@SyncRunId", syncRunId)
            );

            var run = await db.Ado.SqlQuerySingleAsync<ProductShadowRunRow>(
                "SELECT SyncRunId, SourceRowCount, ShadowRowCount, BackupTableName FROM dbo.ProductHqSyncRun WHERE SyncRunId = @SyncRunId",
                new SugarParameter("@SyncRunId", syncRunId)
            );

            result.SyncRunId = syncRunId;
            result.SourceRowCount = run?.SourceRowCount ?? hqRows.Count;
            result.ShadowRowCount = run?.ShadowRowCount ?? products.Count;
            result.ProductsSwapped = true;
            result.BackupTableName = run?.BackupTableName;
            result.ProductsAdded = products.Count;
            result.TotalHqProducts = hqRows.Count;
        }

        private async Task SyncFullDirectAsync(ISqlSugarClient db, HqProductSyncResult result)
        {
            var hqRows = await QueryActiveHqProductsAsync();
            result.SourceRowCount = hqRows.Count;
            result.TotalHqProducts = hqRows.Count;

            var activeHqCodes = hqRows
                .Select(row => NormalizeCode(row.H商品编码))
                .Where(code => code != null)
                .Select(code => code!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var localRows = await db.Queryable<Product>()
                .Where(row => row.ProductCode != null)
                .ToListAsync();
            var localByCode = localRows
                .Where(row => !string.IsNullOrWhiteSpace(row.ProductCode))
                .GroupBy(row => row.ProductCode!)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var hqRow in hqRows)
            {
                var code = NormalizeCode(hqRow.H商品编码);
                if (code == null)
                {
                    continue;
                }

                if (localByCode.TryGetValue(code, out var local))
                {
                    ApplyProductUpdate(hqRow, local);
                    await db.Updateable(local).ExecuteCommandAsync();
                    result.ProductsUpdated++;
                }
                else
                {
                    await db.Insertable(MapNewProduct(hqRow)).ExecuteCommandAsync();
                    result.ProductsAdded++;
                }
            }

            var now = DateTime.UtcNow;
            var softDeleteRows = localRows
                .Where(row =>
                    !row.IsDeleted
                    && !string.IsNullOrWhiteSpace(row.ProductCode)
                    && !activeHqCodes.Contains(row.ProductCode!)
                )
                .ToList();
            foreach (var row in softDeleteRows)
            {
                row.IsDeleted = true;
                row.IsActive = false;
                row.UpdatedAt = now;
                await db.Updateable(row).ExecuteCommandAsync();
            }

            result.ProductsSoftDeleted = softDeleteRows.Count;
            result.ShadowRowCount = hqRows.Count;
            result.ProductsSwapped = true;
        }

        private async Task<ProductIncrementalSnapshot> SyncProductsIncrementalCoreAsync(
            ISqlSugarClient db,
            DateTime effectiveStart,
            HqProductSyncResult result
        )
        {
            var hqIndexRows = await _hqContext.Db.Queryable<DIC_商品信息字典表>()
                .Where(row => row.H使用状态 == true && !string.IsNullOrEmpty(row.H商品编码))
                .Select(row => new DIC_商品信息字典表
                {
                    H商品编码 = row.H商品编码,
                    FGC_LastModifyDate = row.FGC_LastModifyDate,
                })
                .ToListAsync();
            var activeHqCodes = hqIndexRows
                .Select(row => NormalizeCode(row.H商品编码))
                .Where(code => code != null)
                .Select(code => code!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            result.TotalHqProducts = activeHqCodes.Count;

            var changedRows = await _hqContext.Db.Queryable<DIC_商品信息字典表>()
                .Where(row => row.FGC_LastModifyDate >= effectiveStart)
                .ToListAsync();
            var activeChangedRows = changedRows
                .Where(row => row.H使用状态 && !string.IsNullOrWhiteSpace(row.H商品编码))
                .GroupBy(row => row.H商品编码!)
                .Select(group => group.OrderByDescending(row => row.FGC_LastModifyDate).First())
                .ToList();

            var localRows = await db.Queryable<Product>()
                .Where(row => row.ProductCode != null)
                .ToListAsync();
            result.TotalLocalProducts = localRows.Count;
            var localByCode = localRows
                .Where(row => !string.IsNullOrWhiteSpace(row.ProductCode))
                .GroupBy(row => row.ProductCode!)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var hqRow in activeChangedRows)
            {
                var code = NormalizeCode(hqRow.H商品编码);
                if (code == null)
                {
                    continue;
                }

                if (localByCode.TryGetValue(code, out var local))
                {
                    ApplyProductUpdate(hqRow, local);
                    await db.Updateable(local).ExecuteCommandAsync();
                    result.ProductsUpdated++;
                }
                else
                {
                    var product = MapNewProduct(hqRow);
                    await db.Insertable(product).ExecuteCommandAsync();
                    localByCode[code] = product;
                    result.ProductsAdded++;
                }
            }

            var now = DateTime.UtcNow;
            var softDeletedCodes = localRows
                .Where(row =>
                    !row.IsDeleted
                    && !string.IsNullOrWhiteSpace(row.ProductCode)
                    && !activeHqCodes.Contains(row.ProductCode!)
                )
                .Select(row => row.ProductCode!)
                .ToList();

            if (softDeletedCodes.Count > 0)
            {
                var affected = await db.Updateable<Product>()
                    .SetColumns(row => new Product
                    {
                        IsDeleted = true,
                        IsActive = false,
                        UpdatedAt = now,
                    })
                    .Where(row => softDeletedCodes.Contains(row.ProductCode!))
                    .ExecuteCommandAsync();
                result.ProductsSoftDeleted = affected;

                var associationResult = await SoftDeleteProductAssociationsAsync(
                    db,
                    softDeletedCodes,
                    now
                );
                result.StoreRetailPricesDeleted += associationResult.StoreRetailPricesDeleted;
                result.StoreMultiCodesDeleted += associationResult.StoreMultiCodesDeleted;
            }

            return new ProductIncrementalSnapshot(activeHqCodes, softDeletedCodes);
        }

        private static async Task<ProductAssociationDeleteResult> SoftDeleteProductAssociationsAsync(
            ISqlSugarClient db,
            List<string> productCodes,
            DateTime now
        )
        {
            var retailPricesDeleted = 0;
            var storeMultiCodesDeleted = 0;
            var normalizedCodes = productCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var chunk in normalizedCodes.Chunk(1000))
            {
                var codes = chunk.ToList();

                // 商品被 HQ 删除时，直接按 ProductCode 清理分店价格，避免前台继续查到孤儿价格。
                retailPricesDeleted += await db.Updateable<StoreRetailPrice>()
                    .SetColumns(row => new StoreRetailPrice
                    {
                        IsDeleted = true,
                        IsActive = false,
                        UpdatedAt = now,
                    })
                    .Where(row =>
                        !row.IsDeleted
                        && row.ProductCode != null
                        && codes.Contains(row.ProductCode)
                    )
                    .ExecuteCommandAsync();

                // 分店一品多码同样随商品删除软删；ProductSetCode 仍由专用同步链路处理。
                storeMultiCodesDeleted += await db.Updateable<StoreMultiCodeProduct>()
                    .SetColumns(row => new StoreMultiCodeProduct
                    {
                        IsDeleted = true,
                        IsActive = false,
                        UpdatedAt = now,
                    })
                    .Where(row =>
                        !row.IsDeleted
                        && row.ProductCode != null
                        && codes.Contains(row.ProductCode)
                    )
                    .ExecuteCommandAsync();
            }

            return new ProductAssociationDeleteResult(retailPricesDeleted, storeMultiCodesDeleted);
        }

        private async Task SyncProductSetCodesIncrementalCoreAsync(
            ISqlSugarClient db,
            DateTime effectiveStart,
            HashSet<string> activeProductCodes,
            List<string> softDeletedProductCodes,
            HqProductSyncResult result
        )
        {
            var hqCurrentIdentityRows = await _hqContext.Db.Queryable<DIC_一品多码表>()
                .Where(row =>
                    !string.IsNullOrEmpty(row.H商品编码)
                    && !string.IsNullOrEmpty(row.H多码商品编号)
                )
                .ToListAsync();
            hqCurrentIdentityRows = hqCurrentIdentityRows
                .Where(row => activeProductCodes.Contains(row.H商品编码!))
                .ToList();
            var currentSourcePreflight = ProductSetCodeIdentityResolver.PreflightSource(
                hqCurrentIdentityRows,
                row => row.HGUID,
                row => row.H商品编码,
                row => row.H多码商品编号,
                row => row.FGC_LastModifyDate,
                row => row.ID
            );
            AddProductSetCodeSourceConflictErrors(result, currentSourcePreflight.Conflicts);

            // 当前有效键用于缺失清理；停用行只参与身份冲突预检，不能让一个窗口内
            // 的停用成员借同 GUID 误删窗口外仍有效的另一条关系。
            var hqCurrentRows = hqCurrentIdentityRows
                .Where(row => row.H使用状态 == true)
                .ToList();

            var hqCurrentGuidKeys = hqCurrentRows
                .Select(row => NormalizeCode(row.HGUID))
                .Where(key => key != null)
                .Select(key => key!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hqCurrentBusinessKeys = hqCurrentRows
                .Select(row => BuildProductSetCodeBusinessKey(row.H商品编码, row.H多码商品编号))
                .Where(key => key != null)
                .Select(key => key!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var localRows = await db.Queryable<ProductSetCode>().ToListAsync();
            var identityIndex = ProductSetCodeIdentityResolver.CreateIndex(localRows);
            var byGuid = localRows
                .Where(row => !string.IsNullOrWhiteSpace(row.SetCodeId))
                .GroupBy(row => row.SetCodeId)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var changedRows = await _hqContext.Db.Queryable<DIC_一品多码表>()
                .Where(row => row.FGC_LastModifyDate >= effectiveStart)
                .ToListAsync();
            var changedSourcePreflight = ProductSetCodeIdentityResolver.PreflightSource(
                changedRows,
                row => row.HGUID,
                row => row.H商品编码,
                row => row.H多码商品编号,
                row => row.FGC_LastModifyDate,
                row => row.ID
            );
            AddProductSetCodeSourceConflictErrors(result, changedSourcePreflight.Conflicts);
            var preservedConflictGuids = new HashSet<string>(
                currentSourcePreflight.ConflictingGuids,
                StringComparer.OrdinalIgnoreCase
            );
            preservedConflictGuids.UnionWith(changedSourcePreflight.ConflictingGuids);
            var preservedConflictBusinessKeys = new HashSet<string>(
                currentSourcePreflight.ConflictingBusinessKeys,
                StringComparer.OrdinalIgnoreCase
            );
            preservedConflictBusinessKeys.UnionWith(
                changedSourcePreflight.ConflictingBusinessKeys
            );

            var softDeletedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hqRow in changedSourcePreflight.Rows)
            {
                var sourceIdentity = ProductSetCodeIdentityResolver.CreateIdentity(
                    hqRow.HGUID,
                    hqRow.H商品编码,
                    hqRow.H多码商品编号
                );
                if (
                    (sourceIdentity.Guid != null
                        && currentSourcePreflight.ConflictingGuids.Contains(sourceIdentity.Guid))
                    || (sourceIdentity.BusinessKey != null
                        && currentSourcePreflight.ConflictingBusinessKeys.Contains(
                            sourceIdentity.BusinessKey
                        ))
                )
                {
                    // 冲突可能跨越增量时间边界；完整当前快照已判定冲突时，
                    // 即使窗口内只出现其中一个成员也不能单独写入。
                    continue;
                }

                var resolution = identityIndex.Resolve(
                    hqRow.HGUID,
                    hqRow.H商品编码,
                    hqRow.H多码商品编号
                );
                if (resolution.Kind == ProductSetCodeIdentityMatchKind.Conflict)
                {
                    // 冲突行必须在 Type1、软删、重键和后续缺失清理之前短路，并显式保留双方。
                    AddProductSetCodeLocalConflictError(result, resolution);
                    PreserveProductSetCodeConflictMatches(
                        resolution,
                        preservedConflictGuids,
                        preservedConflictBusinessKeys
                    );
                    continue;
                }

                var local = resolution.MatchedRow;
                if (local?.SetType == 1)
                {
                    // 本地 Type1 属于人工套装关系；无论启用、停用或已删除，
                    // HQ 普通多码都不能借 GUID 或父子业务键复用、降级、恢复或软删。
                    continue;
                }
                if (hqRow.H使用状态 != true)
                {
                    if (local != null)
                    {
                        await SoftDeleteProductSetCodeAsync(db, local, softDeletedIds);
                    }
                    continue;
                }

                if (
                    string.IsNullOrWhiteSpace(hqRow.H商品编码)
                    || string.IsNullOrWhiteSpace(hqRow.H多码商品编号)
                    || !activeProductCodes.Contains(hqRow.H商品编码!)
                )
                {
                    continue;
                }

                if (local == null)
                {
                    local = MapNewProductSetCode(hqRow);
                    await db.Insertable(local).ExecuteCommandAsync();
                    result.ProductSetCodesAdded++;
                    byGuid[local.SetCodeId] = local;
                    identityIndex.Add(local);
                    continue;
                }

                // 只有 None/GuidOnly/KeyOnly/SameRecord 的安全结果才能进入 Type2 权威恢复或迁移。
                var previousIdentity = ProductSetCodeIdentityResolver.CreateIdentity(local);
                await NormalizeProductSetCodeIdAsync(db, local, hqRow.HGUID, byGuid);
                ApplyProductSetCodeUpdate(hqRow, local);
                await db.Updateable(local).ExecuteCommandAsync();
                identityIndex.Reindex(local, previousIdentity);
                result.ProductSetCodesUpdated++;
            }

            var now = DateTime.UtcNow;
            var softDeletedProductCodeSet = softDeletedProductCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (
                var local in localRows.Where(row =>
                    !row.IsDeleted && row.SetType != 1
                )
            )
            {
                var hasGuidMatch =
                    !string.IsNullOrWhiteSpace(local.SetCodeId)
                    && hqCurrentGuidKeys.Contains(local.SetCodeId);
                var businessKey = BuildProductSetCodeBusinessKey(local.ProductCode, local.SetProductCode);
                var hasBusinessMatch =
                    businessKey != null && hqCurrentBusinessKeys.Contains(businessKey);
                var productWasDeleted = softDeletedProductCodeSet.Contains(local.ProductCode);
                var preserveForConflict =
                    (!string.IsNullOrWhiteSpace(local.SetCodeId)
                        && preservedConflictGuids.Contains(local.SetCodeId))
                    || (businessKey != null
                        && preservedConflictBusinessKeys.Contains(businessKey));

                if (
                    !preserveForConflict
                    && (productWasDeleted || (!hasGuidMatch && !hasBusinessMatch))
                )
                {
                    local.IsDeleted = true;
                    local.IsActive = false;
                    local.UpdatedAt = now;
                    await db.Updateable(local).ExecuteCommandAsync();
                    softDeletedIds.Add(local.SetCodeId);
                }
            }

            result.ProductSetCodesSoftDeleted = softDeletedIds.Count;
        }

        private async Task<List<DIC_商品信息字典表>> QueryActiveHqProductsAsync()
        {
            var rows = new List<DIC_商品信息字典表>();
            var lastId = 0;
            while (true)
            {
                var batch = await _hqContext.Db.Queryable<DIC_商品信息字典表>()
                    .Where(row =>
                        row.ID > lastId
                        && row.H使用状态 == true
                        && !string.IsNullOrEmpty(row.H商品编码)
                    )
                    .OrderBy(row => row.ID)
                    .Take(HqReadBatchSize)
                    .ToListAsync();

                if (batch.Count == 0)
                {
                    break;
                }

                rows.AddRange(batch);
                lastId = batch[^1].ID;
            }

            return rows
                .GroupBy(row => row.H商品编码!)
                .Select(group => group.OrderByDescending(row => row.FGC_LastModifyDate).First())
                .ToList();
        }

        private Product MapNewProduct(
            DIC_商品信息字典表 hqRow,
            Product? existing = null
        )
        {
            var product = _mapper.Map<Product>(hqRow);
            product.UUID = NormalizeCode(hqRow.HGUID) ?? UuidHelper.GenerateUuid7();
            product.ProductCode = NormalizeCode(hqRow.H商品编码);
            product.EnglishName = Truncate(hqRow.H大写名称, 200);
            product.IsDeleted = false;
            product.CreatedAt = hqRow.FGC_CreateDate == default ? DateTime.UtcNow : hqRow.FGC_CreateDate;
            product.UpdatedAt = DateTime.UtcNow;
            product.PurchasePrice = HqPurchasePriceSyncGuard.PreservePositiveForOrdinaryProduct(
                existing?.PurchasePrice,
                product.PurchasePrice,
                existing?.ProductType,
                product.ProductType
            );
            return product;
        }

        private void ApplyProductUpdate(DIC_商品信息字典表 hqRow, Product local)
        {
            var uuid = local.UUID;
            var createdAt = local.CreatedAt;
            var createdBy = local.CreatedBy;
            var existingPurchasePrice = local.PurchasePrice;
            var existingProductType = local.ProductType;
            _mapper.Map(hqRow, local);
            local.UUID = uuid;
            local.ProductCode = NormalizeCode(hqRow.H商品编码);
            local.CreatedAt = createdAt;
            local.CreatedBy = createdBy;
            local.EnglishName = Truncate(hqRow.H大写名称, 200);
            local.IsDeleted = false;
            local.UpdatedAt = DateTime.UtcNow;
            local.PurchasePrice = HqPurchasePriceSyncGuard.PreservePositiveForOrdinaryProduct(
                existingPurchasePrice,
                local.PurchasePrice,
                existingProductType,
                local.ProductType
            );
        }

        private ProductSetCode MapNewProductSetCode(DIC_一品多码表 hqRow)
        {
            var row = _mapper.Map<ProductSetCode>(hqRow);
            row.SetCodeId = NormalizeCode(hqRow.HGUID) ?? UuidHelper.GenerateUuid7();
            row.ProductCode = NormalizeCode(hqRow.H商品编码) ?? string.Empty;
            row.SetProductCode = NormalizeCode(hqRow.H多码商品编号) ?? string.Empty;
            row.SetItemNumber = row.SetProductCode;
            row.SetBarcode = NormalizeCode(hqRow.H多条形码) ?? NormalizeCode(hqRow.H主条形码);
            // HQ 普通多码映射为 Type2；其外部成本不是本地最终成本，后续统一成本链路会使用主商品成本。
            row.SetPurchasePrice = null;
            row.SetQuantity = 1;
            row.SetType = 2;
            row.IsActive = hqRow.H使用状态 ?? true;
            row.IsDeleted = false;
            row.CreatedAt = hqRow.FGC_CreateDate ?? DateTime.UtcNow;
            row.CreatedBy = hqRow.FGC_Creator;
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = hqRow.FGC_LastModifier;
            return row;
        }

        private static void ApplyProductSetCodeUpdate(DIC_一品多码表 hqRow, ProductSetCode local)
        {
            local.ProductCode = NormalizeCode(hqRow.H商品编码) ?? string.Empty;
            local.SetProductCode = NormalizeCode(hqRow.H多码商品编号) ?? string.Empty;
            local.SetItemNumber = local.SetProductCode;
            local.SetBarcode = NormalizeCode(hqRow.H多条形码) ?? NormalizeCode(hqRow.H主条形码);
            // Type2 不能把 HQ 快照成本当成最终值，避免后续推送回流旧成本。
            local.SetPurchasePrice = null;
            local.SetRetailPrice = hqRow.H一品多码零售价;
            local.SetQuantity = local.SetQuantity <= 0 ? 1 : local.SetQuantity;
            local.SetType = 2;
            local.IsActive = hqRow.H使用状态 ?? true;
            local.IsDeleted = false;
            local.UpdatedAt = DateTime.UtcNow;
            local.UpdatedBy = hqRow.FGC_LastModifier;
        }

        private static void AddProductSetCodeSourceConflictErrors(
            HqProductSyncResult result,
            IEnumerable<ProductSetCodeSourceConflict> conflicts
        )
        {
            foreach (var conflict in conflicts)
            {
                var message = conflict.ToErrorMessage();
                if (!result.Errors.Contains(message))
                {
                    result.Errors.Add(message);
                }
            }
        }

        private static void AddProductSetCodeLocalConflictError(
            HqProductSyncResult result,
            ProductSetCodeIdentityResolution resolution
        )
        {
            var message =
                "本地 ProductSetCode 身份冲突，GUID 与父子业务键命中不同记录，已保留原记录："
                + $"GUID={resolution.Guid ?? "(空)"}，"
                + $"业务键={(resolution.BusinessKey == null ? "(空)" : ProductSetCodeIdentityResolver.FormatBusinessKey(resolution.BusinessKey))}，"
                + $"本地记录={ProductSetCodeIdentityResolver.FormatLocalRecords(resolution.AllMatches)}";
            if (!result.Errors.Contains(message))
            {
                result.Errors.Add(message);
            }
        }

        private static void PreserveProductSetCodeConflictMatches(
            ProductSetCodeIdentityResolution resolution,
            HashSet<string> preservedGuids,
            HashSet<string> preservedBusinessKeys
        )
        {
            foreach (var row in resolution.AllMatches)
            {
                var identity = ProductSetCodeIdentityResolver.CreateIdentity(row);
                if (identity.Guid != null)
                {
                    preservedGuids.Add(identity.Guid);
                }
                if (identity.BusinessKey != null)
                {
                    preservedBusinessKeys.Add(identity.BusinessKey);
                }
            }
        }

        private static ProductSetCode? FindProductSetCode(
            DIC_一品多码表 hqRow,
            Dictionary<string, ProductSetCode> byGuid,
            Dictionary<string, ProductSetCode> byBusinessKey
        )
        {
            var hguid = NormalizeCode(hqRow.HGUID);
            if (hguid != null && byGuid.TryGetValue(hguid, out var guidMatch))
            {
                return guidMatch;
            }

            var businessKey = BuildProductSetCodeBusinessKey(hqRow.H商品编码, hqRow.H多码商品编号);
            return businessKey != null && byBusinessKey.TryGetValue(businessKey, out var businessMatch)
                ? businessMatch
                : null;
        }

        private static async Task SoftDeleteProductSetCodeAsync(
            ISqlSugarClient db,
            ProductSetCode local,
            HashSet<string> softDeletedIds
        )
        {
            if (local.IsDeleted)
            {
                return;
            }

            local.IsDeleted = true;
            local.IsActive = false;
            local.UpdatedAt = DateTime.UtcNow;
            await db.Updateable(local).ExecuteCommandAsync();
            softDeletedIds.Add(local.SetCodeId);
        }

        private static async Task NormalizeProductSetCodeIdAsync(
            ISqlSugarClient db,
            ProductSetCode local,
            string? hguid,
            Dictionary<string, ProductSetCode> byGuid
        )
        {
            var normalizedGuid = NormalizeCode(hguid);
            if (
                normalizedGuid == null
                || local.SetCodeId == normalizedGuid
                || byGuid.ContainsKey(normalizedGuid)
            )
            {
                return;
            }

            var oldId = local.SetCodeId;
            await db.Ado.ExecuteCommandAsync(
                "UPDATE ProductSetCode SET SetCodeId = @NewId WHERE SetCodeId = @OldId",
                new SugarParameter("@NewId", normalizedGuid),
                new SugarParameter("@OldId", oldId)
            );
            byGuid.Remove(oldId);
            local.SetCodeId = normalizedGuid;
            byGuid[normalizedGuid] = local;
        }

        private static List<T> DeduplicateByBusinessKey<T>(
            IEnumerable<T> rows,
            Func<T, string?> keySelector
        )
            where T : BaseEntity
        {
            // 本地异常重复数据不应在一次 HQ upsert 中制造重复记录；同键取最近更新的一条。
            return rows
                .Select(row => new
                {
                    Key = NormalizeCode(keySelector(row)),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => item.Row.UpdatedAt ?? item.Row.CreatedAt)
                    .ThenByDescending(item => item.Row.CreatedAt)
                    .First()
                    .Row)
                .ToList();
        }

        private static string ResolveDomesticSupplierCode(
            Product product,
            IReadOnlyDictionary<string, string> domesticSupplierCodes
        )
        {
            var productCode = NormalizeCode(product.ProductCode);
            return productCode != null && domesticSupplierCodes.TryGetValue(productCode, out var supplierCode)
                ? NormalizeCode(supplierCode) ?? string.Empty
                : string.Empty;
        }

        private static string ResolveHqSupplierCode(Product product)
        {
            // HQ 普通供应商链路使用本地商品供应商；仅主档为空时才回退历史默认编码 200。
            return NormalizeCode(product.LocalSupplierCode) ?? "200";
        }

        private static PushProductsToHqItem? ResolvePushCandidate(
            Product product,
            IReadOnlyDictionary<string, PushProductsToHqItem> pushCandidates
        )
        {
            var productCode = NormalizeCode(product.ProductCode);
            return productCode != null && pushCandidates.TryGetValue(productCode, out var candidate)
                ? candidate
                : null;
        }

        private static string? ResolveDomesticProductImage(
            Product product,
            IReadOnlyDictionary<string, string> domesticProductImages
        )
        {
            var productCode = NormalizeCode(product.ProductCode);
            return productCode != null && domesticProductImages.TryGetValue(productCode, out var imageUrl)
                ? NormalizeCode(imageUrl)
                : null;
        }

        private static DIC_商品信息字典表 MapProductToHqProduct(
            Product product,
            string? domesticSupplierCode,
            PushProductsToHqItem? candidate = null,
            string? domesticProductImage = null
        )
        {
            var now = DateTime.Now;
            var productCode = NormalizeCode(product.ProductCode) ?? string.Empty;
            var productName = NormalizeCode(candidate?.ProductName)
                ?? NormalizeCode(product.ProductName)
                ?? string.Empty;
            var displayName = NormalizeCode(candidate?.EnglishName)
                ?? NormalizeCode(product.EnglishName)
                ?? productName;
            var purchasePrice = candidate?.ImportPrice ?? product.PurchasePrice ?? 0;
            var retailPrice = candidate?.OemPrice ?? product.RetailPrice ?? 0;
            return new DIC_商品信息字典表
            {
                HGUID = NormalizeCode(product.UUID) ?? UuidHelper.GenerateUuid7(),
                H商品标签GUID = string.Empty,
                H商品分类码GUID = string.Empty,
                H商品编码 = productCode,
                H货号 = NormalizeCode(candidate?.ItemNumber) ?? NormalizeCode(product.ItemNumber) ?? string.Empty,
                H主条形码 = NormalizeCode(candidate?.Barcode) ?? NormalizeCode(product.Barcode) ?? string.Empty,
                H商品名称 = displayName,
                H大写名称 = productName,
                H商品类型 = product.ProductType ?? 0,
                H规格 = string.Empty,
                H单位 = "个",
                H进货价 = purchasePrice,
                H零售价 = retailPrice,
                H是否自动定价 = product.IsAutoPricing,
                // 货柜页图片来自 DomesticProduct；Product 主档未补图时不能把 HQ 图片覆盖为空。
                H商品图片 = NormalizeCode(candidate?.ImageUrl)
                    ?? NormalizeCode(product.ProductImage)
                    ?? NormalizeCode(domesticProductImage)
                    ?? string.Empty,
                H腾讯云图地址 = string.Empty,
                中包数量 = product.MiddlePackageQuantity ?? 0,
                // 货柜发送 HQ 不再使用仓库上下架状态覆盖 HQ/POS 商品启用状态。
                H使用状态 = product.IsActive,
                H是否特殊商品 = product.IsSpecialProduct,
                H进货单主表GUID = string.Empty,
                H进货单详情GUID = string.Empty,
                H供货商编码 = ResolveHqSupplierCode(product),
                CBP商品中文名称 = productName,
                // CBP 供应商编码只对应国内供应商，不与 Product.LocalSupplierCode 混用。
                CBP供应商编码 = NormalizeCode(domesticSupplierCode) ?? string.Empty,
                CBP商品分类码GUID = string.Empty,
                FGC_Creator = "HBweb",
                FGC_CreateDate = now,
                FGC_LastModifier = "HBweb",
                FGC_LastModifyDate = now,
                FGC_UpdateHelp = string.Empty,
            };
        }

        private static DIC_商品零售价表 MapProductToHqRetailPrice(
            Product product,
            string storeCode,
            PushProductsToHqItem? candidate = null
        )
        {
            var now = DateTime.Now;
            var defaultDate = new DateTime(1900, 1, 1);
            var productCode = NormalizeCode(product.ProductCode) ?? string.Empty;
            var supplierCode = ResolveHqSupplierCode(product);
            return new DIC_商品零售价表
            {
                HGUID = UuidHelper.GenerateUuid7(),
                H分店代码 = storeCode,
                H商品编码 = productCode,
                H分店商品编码 = storeCode + productCode,
                H供应商编码 = supplierCode,
                H分店供应商编码 = storeCode + supplierCode,
                H进货价 = candidate?.ImportPrice ?? product.PurchasePrice ?? 0,
                H分店零售价 = candidate?.OemPrice ?? product.RetailPrice ?? 0,
                H库存 = 0,
                H库存金额 = 0,
                H库存预警数 = 0,
                H商品缺货日期 = defaultDate,
                H是否缺货状态 = false,
                H最小订货量 = 0,
                H最小订货量合计金额 = 0,
                H活动类型 = string.Empty,
                H满减活动代码 = string.Empty,
                H活动开始日期 = defaultDate,
                H活动结束日期 = defaultDate,
                H折扣率 = 0,
                H满减数量 = 0,
                H满减金额 = 0,
                H多码数量 = 0,
                // 分店价格新增记录按本地商品状态初始化，后续货柜发送不再更新该字段。
                H使用状态 = product.IsActive,
                H是否自动定价 = product.IsAutoPricing,
                H自动新价格 = 0,
                H盘点入库记录数 = 0,
                H是否特殊商品 = product.IsSpecialProduct,
                H动态销售数量 = 0,
                H动态销售额 = 0,
                H动态成本 = 0,
                H动态毛利 = 0,
                H动态毛利率 = 0,
                H动态销售占比 = 0,
                FGC_Creator = "HBweb",
                FGC_CreateDate = now,
                FGC_LastModifier = "HBweb",
                FGC_LastModifyDate = now,
            };
        }

        private static StoreRetailPrice MapNewStoreRetailPrice(DIC_商品零售价表 hqRow)
        {
            var row = new StoreRetailPrice
            {
                UUID = NormalizeCode(hqRow.HGUID) ?? UuidHelper.GenerateUuid7(),
                CreatedAt = hqRow.FGC_CreateDate == default ? DateTime.UtcNow : hqRow.FGC_CreateDate,
                CreatedBy = hqRow.FGC_Creator,
            };
            ApplyStoreRetailPriceUpdate(hqRow, row);
            return row;
        }

        private static void ApplyStoreRetailPriceUpdate(
            DIC_商品零售价表 hqRow,
            StoreRetailPrice local
        )
        {
            var existingPurchasePrice = local.PurchasePrice;
            local.StoreCode = NormalizeCode(hqRow.H分店代码);
            local.ProductCode = NormalizeCode(hqRow.H商品编码);
            local.StoreProductCode = NormalizeCode(hqRow.H分店商品编码);
            local.SupplierCode = NormalizeCode(hqRow.H供应商编码);
            // 分店主成本供套装/多码重算读取；只保护已有正数，不触碰子项成本字段。
            local.PurchasePrice = HqPurchasePriceSyncGuard.PreservePositive(
                existingPurchasePrice,
                hqRow.H进货价
            );
            local.StoreRetailPriceValue = hqRow.H分店零售价;
            // 总部折扣率可能是百分数（50）或脏值（1 / 100），入库前统一成标准小数。
            local.DiscountRate = StoreDiscountRate.Normalize(hqRow.H折扣率);
            local.IsActive = hqRow.H使用状态;
            local.IsAutoPricing = hqRow.H是否自动定价;
            local.IsSpecialProduct = hqRow.H是否特殊商品;
            local.IsDeleted = false;
            local.UpdatedAt = DateTime.UtcNow;
            local.UpdatedBy = hqRow.FGC_LastModifier;
        }

        private static StoreMultiCodeProduct MapNewStoreMultiCode(DIC_分店一品多码表 hqRow)
        {
            var row = new StoreMultiCodeProduct
            {
                UUID = NormalizeCode(hqRow.HGUID) ?? UuidHelper.GenerateUuid7(),
                CreatedAt = hqRow.FGC_CreateDate ?? DateTime.UtcNow,
                CreatedBy = hqRow.FGC_Creator,
            };
            ApplyStoreMultiCodeUpdate(hqRow, row);
            return row;
        }

        private static void ApplyStoreMultiCodeUpdate(
            DIC_分店一品多码表 hqRow,
            StoreMultiCodeProduct local
        )
        {
            local.StoreCode = NormalizeCode(hqRow.H分店代码);
            local.ProductCode = NormalizeCode(hqRow.H商品编码);
            local.MultiCodeProductCode = NormalizeCode(hqRow.H多码商品编码);
            local.StoreMultiCodeProductCode = NormalizeCode(hqRow.H分店多码商品编码);
            local.MultiBarcode = NormalizeCode(hqRow.H多条形码);
            // HQ 多码成本不是本地最终值；关系同步完成后 Type1 统一分摊，Type2 推送时回退已校正主成本。
            local.PurchasePrice = null;
            local.MultiCodeRetailPrice = hqRow.H一品多码零售价;
            local.DiscountRate = hqRow.H折扣率;
            local.IsAutoPricing = hqRow.H是否自动定价 ?? false;
            local.IsSpecialProduct = hqRow.H是否特殊商品 ?? false;
            local.IsActive = hqRow.H使用状态 ?? true;
            local.IsDeleted = false;
            local.UpdatedAt = DateTime.UtcNow;
            local.UpdatedBy = hqRow.FGC_LastModifier;
        }

        private static DIC_一品多码表 MapProductSetCodeToHq(
            ProductSetCode setCode,
            Product product
        )
        {
            var now = DateTime.Now;
            return new DIC_一品多码表
            {
                HGUID = NormalizeCode(setCode.SetCodeId) ?? UuidHelper.GenerateUuid7(),
                H商品编码 = NormalizeCode(setCode.ProductCode),
                H多码商品编号 = NormalizeCode(setCode.SetProductCode),
                H供应商编码 = ResolveHqSupplierCode(product),
                H主条形码 = NormalizeCode(product.Barcode),
                H多条形码 = NormalizeCode(setCode.SetBarcode),
                H进货价 = ResolveProductSetPurchasePriceForHq(setCode, product),
                H一品多码零售价 = setCode.SetRetailPrice ?? product.RetailPrice ?? 0,
                H使用状态 = setCode.IsActive,
                H是否自动定价 = product.IsAutoPricing,
                FGC_Creator = "HBweb",
                FGC_CreateDate = now,
                FGC_LastModifier = "HBweb",
                FGC_LastModifyDate = now,
            };
        }

        private static async Task<List<string>> GetActiveLocalStoreCodesAsync(ISqlSugarClient db)
        {
            var activeStoreCodes = await db.Queryable<Store>()
                .Where(row => row.IsActive && !row.IsDeleted && row.StoreCode != null)
                .Select(row => row.StoreCode)
                .ToListAsync();
            return activeStoreCodes
                .Select(NormalizeCode)
                .Where(code => code != null)
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static DIC_分店一品多码表 MapStoreMultiCodeToHq(
            string storeCode,
            Product product,
            ProductSetCode setCode,
            StoreMultiCodeProduct? storeMultiCode
        )
        {
            var now = DateTime.Now;
            var productCode = NormalizeCode(product.ProductCode) ?? string.Empty;
            var multiCode = NormalizeCode(setCode.SetProductCode) ?? string.Empty;
            var storeMultiProductCode =
                NormalizeCode(storeMultiCode?.StoreMultiCodeProductCode) ?? storeCode + multiCode;

            return new DIC_分店一品多码表
            {
                HGUID = NormalizeCode(storeMultiCode?.UUID) ?? UuidHelper.GenerateUuid7(),
                H分店代码 = storeCode,
                H商品编码 = productCode,
                H分店商品编码 = storeCode + productCode,
                H多码商品编码 = multiCode,
                H分店多码商品编码 = storeMultiProductCode,
                H供应商编码 = ResolveHqSupplierCode(product),
                H主条形码 = NormalizeCode(product.Barcode),
                H多条形码 = NormalizeCode(storeMultiCode?.MultiBarcode) ?? NormalizeCode(setCode.SetBarcode),
                H进货价 = ResolveStoreMultiPurchasePriceForHq(
                    storeCode,
                    setCode,
                    storeMultiCode,
                    product
                ),
                H折扣率 = storeMultiCode?.DiscountRate ?? 0,
                H一品多码零售价 =
                    storeMultiCode?.MultiCodeRetailPrice ?? setCode.SetRetailPrice ?? product.RetailPrice ?? 0,
                H库存 = 0,
                H库存金额 = 0,
                H自动新价格 = 0,
                H库存预警数 = 0,
                H商品缺货日期 = null,
                H是否缺货状态 = false,
                H最小订货量 = 0,
                H最小订货量合计金额 = 0,
                H活动类型 = string.Empty,
                H满减活动代码 = string.Empty,
                H活动开始日期 = null,
                H活动结束日期 = null,
                H满减数量 = 0,
                H满减金额 = 0,
                H是否自动定价 = storeMultiCode?.IsAutoPricing ?? product.IsAutoPricing,
                H是否特殊商品 = storeMultiCode?.IsSpecialProduct ?? product.IsSpecialProduct,
                H商品柜组号 = string.Empty,
                H使用状态 = storeMultiCode?.IsActive ?? setCode.IsActive,
                H动态销售数量 = 0,
                H动态销售额 = 0,
                H动态成本 = 0,
                H动态毛利 = 0,
                H动态毛利率 = 0,
                H动态销售占比 = 0,
                FGC_Creator = "HBweb",
                FGC_CreateDate = now,
                FGC_LastModifier = "HBweb",
                FGC_LastModifyDate = now,
            };
        }

        private static decimal ResolveProductSetPurchasePriceForHq(
            ProductSetCode setCode,
            Product product
        )
        {
            if (setCode.SetType != 1)
            {
                return setCode.SetPurchasePrice ?? product.PurchasePrice ?? 0;
            }

            if (setCode.SetPurchasePrice.HasValue && setCode.SetPurchasePrice.Value >= 0)
            {
                return setCode.SetPurchasePrice.Value;
            }

            throw new InvalidOperationException(
                $"套装子项成本尚未校正，不能推送 HQ: {setCode.ProductCode}/{setCode.SetProductCode}"
            );
        }

        private static decimal ResolveStoreMultiPurchasePriceForHq(
            string storeCode,
            ProductSetCode setCode,
            StoreMultiCodeProduct? storeMultiCode,
            Product product
        )
        {
            if (setCode.SetType != 1)
            {
                return storeMultiCode?.PurchasePrice
                    ?? setCode.SetPurchasePrice
                    ?? product.PurchasePrice
                    ?? 0;
            }

            if (
                storeMultiCode?.PurchasePrice.HasValue == true
                && storeMultiCode.PurchasePrice.Value >= 0
            )
            {
                return storeMultiCode.PurchasePrice.Value;
            }

            throw new InvalidOperationException(
                $"门店套装子项成本尚未校正，不能推送 HQ: {storeCode}/{setCode.ProductCode}/{setCode.SetProductCode}"
            );
        }

        private static string? BuildProductSetCodeBusinessKey(string? productCode, string? setProductCode)
        {
            var normalizedProductCode = NormalizeCode(productCode);
            var normalizedSetProductCode = NormalizeCode(setProductCode);
            return normalizedProductCode == null || normalizedSetProductCode == null
                ? null
                : $"{normalizedProductCode}\u001F{normalizedSetProductCode}";
        }

        private static string? BuildStoreProductKey(string? storeCode, string? productCode)
        {
            var normalizedStoreCode = NormalizeCode(storeCode);
            var normalizedProductCode = NormalizeCode(productCode);
            return normalizedStoreCode == null || normalizedProductCode == null
                ? null
                : $"{normalizedStoreCode}\u001F{normalizedProductCode}";
        }

        private static string? BuildStoreMultiCodeKey(
            string? storeCode,
            string? productCode,
            string? multiCode
        )
        {
            var normalizedStoreCode = NormalizeCode(storeCode);
            var normalizedProductCode = NormalizeCode(productCode);
            var normalizedMultiCode = NormalizeCode(multiCode);
            return normalizedStoreCode == null || normalizedProductCode == null || normalizedMultiCode == null
                ? null
                : $"{normalizedStoreCode}\u001F{normalizedProductCode}\u001F{normalizedMultiCode}";
        }

        private static string? BuildSupplierItemKey(string? supplierCode, string? itemNumber)
        {
            var normalizedSupplierCode = NormalizeCode(supplierCode);
            var normalizedItemNumber = NormalizeCode(itemNumber);
            return normalizedSupplierCode == null || normalizedItemNumber == null
                ? null
                : $"{normalizedSupplierCode}\u001F{normalizedItemNumber}";
        }

        private static string? NormalizeCode(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string? Truncate(string? value, int maxLength)
        {
            var normalized = NormalizeCode(value);
            return normalized == null || normalized.Length <= maxLength
                ? normalized
                : normalized[..maxLength];
        }

        private sealed record ProductIncrementalSnapshot(
            HashSet<string> ActiveProductCodes,
            List<string> SoftDeletedProductCodes
        );

        private sealed record ProductAssociationDeleteResult(
            int StoreRetailPricesDeleted,
            int StoreMultiCodesDeleted
        );

        private sealed record PushToHqSelection(
            List<Product> Products,
            Dictionary<string, PushProductsToHqItem> InventoryCandidates,
            Dictionary<string, string> DomesticProductImages,
            Dictionary<string, string> DomesticSupplierCodes,
            int ItemFailureCount
        );

        private sealed record TargetStoreCodeResolution(
            List<string>? StoreCodes,
            string? ErrorCode,
            string? Message
        )
        {
            public bool Failed => ErrorCode != null;

            public static TargetStoreCodeResolution AllStores() => new(null, null, null);

            public static TargetStoreCodeResolution Explicit(List<string> storeCodes) =>
                new(storeCodes, null, null);

            public static TargetStoreCodeResolution Rejected(string errorCode, string message) =>
                new(null, errorCode, message);
        }

        private sealed class SupplierItemHqProductMatch
        {
            public string? SupplierCode { get; set; }
            public string? ItemNumber { get; set; }
            public string? ProductCode { get; set; }
        }

        private sealed class ProductShadowRunRow
        {
            public long SyncRunId { get; set; }
            public long SourceRowCount { get; set; }
            public long ShadowRowCount { get; set; }
            public string? BackupTableName { get; set; }
        }
    }
}
