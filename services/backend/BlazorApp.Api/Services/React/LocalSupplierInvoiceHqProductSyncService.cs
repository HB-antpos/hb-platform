using System.Diagnostics;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class LocalSupplierInvoiceHqProductSyncService : ILocalSupplierInvoiceHqProductSyncService
    {
        private const int LocalWriteBatchSize = 500;
        private const int HqWriteBatchSize = 40;

        private readonly SqlSugarContext _context;
        private readonly HqSqlSugarContext _hqContext;
        private readonly ILogger<LocalSupplierInvoiceHqProductSyncService> _logger;
        private readonly IWarehouseProductChangeHistoryService _changeHistoryService;

        public LocalSupplierInvoiceHqProductSyncService(
            SqlSugarContext context,
            HqSqlSugarContext hqContext,
            ILogger<LocalSupplierInvoiceHqProductSyncService> logger,
            IWarehouseProductChangeHistoryService changeHistoryService
        )
        {
            _context = context;
            _hqContext = hqContext;
            _logger = logger;
            _changeHistoryService = changeHistoryService;
        }

        public async Task<ApiResponse<EnsureHqProductsResult>> EnsureHqProductsAsync(
            string invoiceGuid,
            EnsureHqProductsRequest request,
            string updatedBy
        )
        {
            var result = new EnsureHqProductsResult();
            var detailGuids = request.DetailGuids
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();
            var targetStoreCodes = request.TargetStoreCodes
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();

            if (detailGuids.Count == 0)
                return ApiResponse<EnsureHqProductsResult>.Error("请选择要同步的明细", "VALIDATION_ERROR", result);
            if (targetStoreCodes.Count == 0)
                return ApiResponse<EnsureHqProductsResult>.Error("请选择目标分店", "VALIDATION_ERROR", result);

            result.Total = detailGuids.Count;

            try
            {
                var db = _context.Db;
                var header = await db.Queryable<StoreLocalSupplierInvoice>()
                    .Where(x => x.InvoiceGUID == invoiceGuid && x.IsDeleted == false)
                    .FirstAsync();
                if (header == null)
                    return ApiResponse<EnsureHqProductsResult>.Error("进货单不存在", "NOT_FOUND", result);

                var details = await db.Queryable<StoreLocalSupplierInvoiceDetails>()
                    .Where(x =>
                        x.InvoiceGUID == invoiceGuid
                        && detailGuids.Contains(x.DetailGUID)
                        && x.IsDeleted == false
                    )
                    .ToListAsync();
                if (details.Count == 0)
                    return ApiResponse<EnsureHqProductsResult>.Error("未找到要同步的明细", "NOT_FOUND", result);

                var activeStoreCodes = await db.Queryable<Store>()
                    .Where(x => x.IsActive && x.IsDeleted == false)
                    .Select(x => x.StoreCode)
                    .ToListAsync();
                activeStoreCodes = activeStoreCodes
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct()
                    .ToList();
                if (activeStoreCodes.Count == 0)
                    return ApiResponse<EnsureHqProductsResult>.Error("未找到启用分店", "NO_ACTIVE_STORE", result);

                var invalidTargetStores = targetStoreCodes
                    .Where(storeCode => !activeStoreCodes.Contains(storeCode))
                    .ToList();
                if (invalidTargetStores.Count > 0)
                {
                    return ApiResponse<EnsureHqProductsResult>.Error(
                        $"目标分店不存在或未启用：{string.Join(", ", invalidTargetStores)}",
                        "INVALID_TARGET_STORE",
                        result
                    );
                }

                var syncItems = new List<PreparedSyncItem>();
                var candidateProductCodes = details.Select(_ => UuidHelper.GenerateUuid7()).ToList();
                var auditBatchGuid = Guid.NewGuid();
                await db.Ado.BeginTranAsync();
                try
                {
                    // 本次可能创建新的商品编码，无法在锁前完整确定产品集合；总闸覆盖本地写入和派生成本校正。
                    var childCostLockScope = await SetChildPurchasePriceMutationLock.AcquireAllAsync(db);
                    for (var index = 0; index < details.Count; index++)
                    {
                        var detail = details[index];
                        var prepared = await PrepareLocalProductAsync(
                            header,
                            detail,
                            activeStoreCodes,
                            targetStoreCodes,
                            updatedBy,
                            result,
                            candidateProductCodes[index]
                        );
                        if (prepared != null)
                            syncItems.Add(prepared);
                    }

                    var preparedProductCodes = SetChildPurchasePriceMutationLock.NormalizeProductCodes(
                        syncItems
                        .Select(item => item.Product.ProductCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!)
                    );
                    if (preparedProductCodes.Count > 0)
                    {
                        // 所有受影响商品都必须校正；结构冲突或缺成本会抛错并回滚本地事务，禁止静默跳过。
                        await new SetChildPurchasePriceService(db).RecalculateLockedAsync(
                            childCostLockScope,
                            preparedProductCodes,
                            activeStoreCodes,
                            updatedBy
                        );
                    }

                    await RecordCreatedLocalProductHistoryAsync(
                        syncItems,
                        invoiceGuid,
                        auditBatchGuid,
                        actorUserGuid: null,
                        actorName: updatedBy
                    );
                    await db.Ado.CommitTranAsync();
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }

                syncItems = await AttachDomesticSupplierCodesAsync(db, syncItems);

                foreach (var item in syncItems)
                {
                    try
                    {
                        await SyncHqProductAsync(item, activeStoreCodes, targetStoreCodes, updatedBy, result);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "同步进货单明细商品到HQ失败 DetailGuid={DetailGuid}",
                            item.Detail.DetailGUID
                        );
                        AddError(result, item.Detail.DetailGUID, null, $"同步HQ失败：{ex.Message}");
                    }
                }

                if (result.Failed > 0)
                {
                    return ApiResponse<EnsureHqProductsResult>.Error(
                        "同步商品到HQ部分失败",
                        "HQ_SYNC_PARTIAL_FAILED",
                        result
                    );
                }

                return ApiResponse<EnsureHqProductsResult>.OK(result, "同步商品到HQ完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步商品到HQ异常");
                return ApiResponse<EnsureHqProductsResult>.Error(
                    $"同步商品到HQ失败: {ex.Message}",
                    "HQ_SYNC_ERROR",
                    result
                );
            }
        }

        public async Task<ApiResponse<UpdateHqProductsResult>> UpdateHqProductsAsync(
            string invoiceGuid,
            UpdateHqProductsRequest? request,
            string updatedBy
        ) => await UpdateHqProductsAsyncCore(
            invoiceGuid,
            request,
            updatedBy,
            actorUserGuid: null,
            actorName: updatedBy
        );

        public async Task<ApiResponse<UpdateHqProductsResult>> UpdateHqProductsAsync(
            string invoiceGuid,
            UpdateHqProductsRequest? request,
            string? actorUserGuid,
            string actorName
        ) => await UpdateHqProductsAsyncCore(
            invoiceGuid,
            request,
            actorName,
            actorUserGuid,
            actorName
        );

        public Task<ApiResponse<UpdateHqProductsResult>> UpdateHqProductsAsync(
            string invoiceGuid,
            UpdateHqProductsRequest? request,
            string? actorUserGuid,
            string actorName,
            int lockWaitMilliseconds
        ) => UpdateHqProductsAsyncCore(
            invoiceGuid, request, actorName, actorUserGuid, actorName, lockWaitMilliseconds
        );

        private async Task<ApiResponse<UpdateHqProductsResult>> UpdateHqProductsAsyncCore(
            string invoiceGuid,
            UpdateHqProductsRequest? request,
            string updatedBy,
            string? actorUserGuid,
            string actorName,
            int lockWaitMilliseconds = 10_000
        )
        {
            var result = new UpdateHqProductsResult();
            if (lockWaitMilliseconds < 0 || lockWaitMilliseconds > 10_000)
                return ApiResponse<UpdateHqProductsResult>.Error("锁等待预算必须在0至10000毫秒之间", "VALIDATION_ERROR", result);
            // HQ字段更新直接写总部价格表，入口先兜底空payload，避免异常绕过可展示的失败结果。
            if (request == null)
                return ApiResponse<UpdateHqProductsResult>.Error("请求参数不能为空", "VALIDATION_ERROR", result);

            var updateFields = request.UpdateFields;
            var detailGuids = (request.DetailGuids ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();
            var targetStoreCodes = (request.TargetStoreCodes ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (detailGuids.Count == 0)
                return ApiResponse<UpdateHqProductsResult>.Error("请选择要更新的明细", "VALIDATION_ERROR", result);
            if (targetStoreCodes.Count == 0)
                return ApiResponse<UpdateHqProductsResult>.Error("请选择目标分店", "VALIDATION_ERROR", result);
            if (updateFields == null || !HasAnyUpdateField(updateFields))
                return ApiResponse<UpdateHqProductsResult>.Error("请选择要更新的HQ字段", "VALIDATION_ERROR", result);

            result.Total = detailGuids.Count;
            var totalStopwatch = Stopwatch.StartNew();
            long localPreparationMs = 0;
            long localPriceMs = 0;
            var hqProductAndPriceElapsed = TimeSpan.Zero;
            var hqMultiCodeElapsed = TimeSpan.Zero;

            try
            {
                var db = _context.Db;
                var header = await db.Queryable<StoreLocalSupplierInvoice>()
                    .Where(x => x.InvoiceGUID == invoiceGuid && x.IsDeleted == false)
                    .FirstAsync();
                if (header == null)
                    return ApiResponse<UpdateHqProductsResult>.Error("进货单不存在", "NOT_FOUND", result);

                var details = await db.Queryable<StoreLocalSupplierInvoiceDetails>()
                    .Where(x =>
                        x.InvoiceGUID == invoiceGuid
                        && detailGuids.Contains(x.DetailGUID)
                        && x.IsDeleted == false
                    )
                    .ToListAsync();
                if (details.Count == 0)
                    return ApiResponse<UpdateHqProductsResult>.Error("未找到要更新的明细", "NOT_FOUND", result);

                // 保留停用和软删除档案用于目标校验；仅 HBweb 完全没有档案的分店允许跳过。
                var localStores = await db.Queryable<Store>()
                    .Select(x => new { x.StoreCode, x.IsActive, x.IsDeleted })
                    .ToListAsync();
                var localStoreCodes = localStores
                    .Where(x => !string.IsNullOrWhiteSpace(x.StoreCode))
                    .Select(x => x.StoreCode.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var skippedStoreCodes = targetStoreCodes
                    .Where(storeCode => !localStoreCodes.Contains(storeCode))
                    .ToList();
                if (skippedStoreCodes.Count > 0)
                {
                    _logger.LogInformation(
                        "更新HQ商品跳过HBweb无档案分店 InvoiceGuid={InvoiceGuid} StoreCodes={StoreCodes}",
                        invoiceGuid,
                        string.Join(", ", skippedStoreCodes)
                    );
                }
                targetStoreCodes = targetStoreCodes.Where(localStoreCodes.Contains).ToList();
                if (targetStoreCodes.Count == 0)
                {
                    return ApiResponse<UpdateHqProductsResult>.Error(
                        "没有可更新的 HBweb 分店",
                        "VALIDATION_ERROR",
                        result
                    );
                }

                var activeStoreCodes = localStores
                    .Where(x => x.IsActive && !x.IsDeleted && !string.IsNullOrWhiteSpace(x.StoreCode))
                    .Select(x => x.StoreCode.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var activeStoreCodeMap = activeStoreCodes.ToDictionary(
                    storeCode => storeCode,
                    StringComparer.OrdinalIgnoreCase
                );

                var invalidTargetStores = targetStoreCodes
                    .Where(storeCode => !activeStoreCodeMap.ContainsKey(storeCode))
                    .ToList();
                if (invalidTargetStores.Count > 0)
                {
                    return ApiResponse<UpdateHqProductsResult>.Error(
                        $"目标分店不存在或未启用：{string.Join(", ", invalidTargetStores)}",
                        "INVALID_TARGET_STORE",
                        result
                    );
                }
                // 使用 HBweb 档案中的规范编码，后续所有 HQ 分店写入共用同一目标集合。
                targetStoreCodes = targetStoreCodes.Select(storeCode => activeStoreCodeMap[storeCode]).ToList();

                var updateItems = new List<PreparedSyncItem>();
                var candidateProductCodes = details.Select(_ => UuidHelper.GenerateUuid7()).ToList();
                var auditBatchGuid = Guid.NewGuid();
                var hbwebCreatedBeforeLocalTransaction = result.HbwebCreated;
                var relationRepair = new SetChildStoreRelationRepairResult();
                // 预解析不持有事务，避免等待业务锁时用共享行锁阻挡先持锁的写入者。
                var initialProducts = await ResolveHqUpdateProductsAsync(db, header, details);
                await db.Ado.BeginTranAsync();
                try
                {
                    SetChildPurchasePriceLockScope childCostLockScope;
                    List<Product?>? lockedProducts;
                    var lockStopwatch = Stopwatch.StartNew();
                    try
                    {
                        (childCostLockScope, lockedProducts) = await AcquireHqUpdateCostLockAsync(
                            db, header, details, initialProducts, candidateProductCodes, lockWaitMilliseconds
                        );
                    }
                    catch (SetChildPurchasePriceLockException ex) when (ex.ResultCode is -1 or -3)
                    {
                        // 只有获锁阶段且回滚成功，才允许后台任务重试；此时尚无本地业务写入或 HQ 写入。
                        // 回滚异常必须继续向外传播，不能把提交状态不明的请求标成可重试。
                        await db.Ado.RollbackTranAsync();
                        _logger.LogWarning(
                            "更新HQ商品等待成本锁 InvoiceGuid={InvoiceGuid} Resource={Resource} ProductCount={ProductCount} InitialNewProductCount={InitialNewProductCount} WaitMs={WaitMs} ResultCode={ResultCode} Stage=LocalLock",
                            invoiceGuid, ex.Resource, details.Count, initialProducts.Count(product => product == null), lockStopwatch.ElapsedMilliseconds, ex.ResultCode
                        );
                        return ApiResponse<UpdateHqProductsResult>.Error(
                            "商品更新繁忙，等待其他成本操作超时，本次未更新 HQ 商品",
                            "HQ_UPDATE_COST_LOCK_BUSY",
                            result
                        );
                    }
                    _logger.LogInformation(
                        "更新HQ商品已获取成本锁 InvoiceGuid={InvoiceGuid} LockAll={LockAll} ProductCount={ProductCount} WaitMs={WaitMs} Stage=LocalLock",
                        invoiceGuid, childCostLockScope.LocksAllProducts, details.Count, lockStopwatch.ElapsedMilliseconds
                    );
                    var localPreparationStopwatch = Stopwatch.StartNew();
                    for (var index = 0; index < details.Count; index++)
                    {
                        var detail = details[index];
                        var prepared = await PrepareLocalProductForHqUpdateAsync(
                            header,
                            detail,
                            updatedBy,
                            result,
                            candidateProductCodes[index],
                            childCostLockScope,
                            lockedProducts?[index]
                        );
                        if (prepared != null)
                            updateItems.Add(prepared);
                    }
                    localPreparationStopwatch.Stop();
                    localPreparationMs = localPreparationStopwatch.ElapsedMilliseconds;

                    var localPriceStopwatch = Stopwatch.StartNew();

                    var preparedProductCodes = SetChildPurchasePriceMutationLock.NormalizeProductCodes(
                        updateItems
                        .Select(item => item.Product.ProductCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!)
                    );
                    if (preparedProductCodes.Count > 0)
                    {
                        var costWriteback = new SetChildPurchasePriceService(db);
                        var preparedProducts = updateItems
                            .Where(item => !string.IsNullOrWhiteSpace(item.Product.ProductCode))
                            .GroupBy(item => item.Product.ProductCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(group => group.Key, group => group.First().Product, StringComparer.OrdinalIgnoreCase);
                        var warehouseCostProductCodes = preparedProducts
                            .Where(pair => pair.Value.PurchasePrice.GetValueOrDefault() <= 0m)
                            .Select(pair => pair.Key)
                            .ToList();
                        var warehouseRows = new List<WarehouseProduct>();
                        foreach (var codeBatch in warehouseCostProductCodes.Chunk(LocalWriteBatchSize))
                        {
                            var codes = codeBatch.ToList();
                            warehouseRows.AddRange(
                                await db.Queryable<WarehouseProduct>()
                                    .Where(row => codes.Contains(row.ProductCode) && !row.IsDeleted)
                                    .ToListAsync()
                            );
                        }
                        var warehousePurchasePrices = warehouseRows
                            .GroupBy(row => row.ProductCode.Trim(), StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                group => group.Key,
                                group => group.First().ImportPrice.GetValueOrDefault(),
                                StringComparer.OrdinalIgnoreCase
                            );
                        // 补齐门禁与正式全局重算使用同一成本来源，不把本单进货价写入既有主档。
                        var repairPurchasePrices = preparedProducts.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value.PurchasePrice.GetValueOrDefault() > 0m
                                ? pair.Value.PurchasePrice!.Value
                                : warehousePurchasePrices.GetValueOrDefault(pair.Key),
                            StringComparer.OrdinalIgnoreCase
                        );
                        // 明确覆盖全部 HBweb 启用分店，包括尚无门店主价格和多码投影的组。
                        var repairGroups = activeStoreCodes
                            .SelectMany(storeCode => preparedProductCodes.Select(productCode =>
                                (StoreCode: (string?)storeCode, ProductCode: (string?)productCode)
                            ))
                            .ToList();
                        relationRepair = await costWriteback.RepairMissingStoreRelationsLockedAsync(
                            childCostLockScope,
                            repairPurchasePrices,
                            updatedBy,
                            exactStoreGroups: repairGroups
                        );
                        if (relationRepair.Failures.Count > 0)
                        {
                            var reasons = string.Join(
                                "；",
                                relationRepair.Failures.Values
                                    .OrderBy(failure => failure.ProductCode, StringComparer.OrdinalIgnoreCase)
                                    .Select(failure =>
                                        $"{failure.ProductCode} / 分店 {failure.StoreCode ?? "启用分店"} [{failure.Code}]: {failure.Message}"
                                    )
                            );
                            throw new InvalidOperationException($"门店套装或多码关系无法安全补齐。{reasons}");
                        }

                        // 补齐不替代严格校验；成本或结构仍不合法时，新增关系随本地事务整体回滚。
                        await costWriteback.RecalculateLockedAsync(
                            childCostLockScope,
                            preparedProductCodes,
                            activeStoreCodes,
                            updatedBy
                        );
                    }
                    localPriceStopwatch.Stop();
                    localPriceMs = localPriceStopwatch.ElapsedMilliseconds;

                    await db.Ado.CommitTranAsync();
                }
                catch
                {
                    // 本地主档、价格和历史记录同事务回滚，返回计数必须同步恢复。
                    result.HbwebCreated = hbwebCreatedBeforeLocalTransaction;
                    await db.Ado.RollbackTranAsync();
                    throw;
                }

                if (relationRepair.AutoRepairedRelationCount > 0)
                {
                    _logger.LogInformation(
                        "更新HQ商品已提交门店子项补齐 InvoiceGuid={InvoiceGuid} RepairedGroupCount={RepairedGroupCount} RepairedRelationCount={RepairedRelationCount}",
                        invoiceGuid,
                        relationRepair.AutoRepairedStoreGroupCount,
                        relationRepair.AutoRepairedRelationCount
                    );
                }
                updateItems = await AttachDomesticSupplierCodesAsync(db, updateItems);

                foreach (var item in updateItems)
                {
                    try
                    {
                        var hqPriceStartedAt = Stopwatch.GetTimestamp();
                        try
                        {
                            await UpdateHqStorePricesAsync(
                                item,
                                activeStoreCodes,
                                targetStoreCodes,
                                updateFields,
                                updatedBy,
                                result
                            );
                        }
                        finally
                        {
                            hqProductAndPriceElapsed += Stopwatch.GetElapsedTime(hqPriceStartedAt);
                        }

                        var hqMultiCodeStartedAt = Stopwatch.GetTimestamp();
                        try
                        {
                            await SyncHqMultiCodesAsync(item, targetStoreCodes, updatedBy, result);
                        }
                        finally
                        {
                            hqMultiCodeElapsed += Stopwatch.GetElapsedTime(hqMultiCodeStartedAt);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "更新HQ商品字段失败 DetailGuid={DetailGuid}",
                            item.Detail.DetailGUID
                        );
                        AddError(result, item.Detail.DetailGUID, null, $"更新HQ商品失败：{ex.Message}");
                    }
                }

                if (result.Failed > 0)
                {
                    LogUpdateHqProductsPerformance(
                        invoiceGuid,
                        details.Count,
                        activeStoreCodes.Count,
                        targetStoreCodes.Count,
                        result,
                        localPreparationMs,
                        localPriceMs,
                        (long)hqProductAndPriceElapsed.TotalMilliseconds,
                        (long)hqMultiCodeElapsed.TotalMilliseconds,
                        totalStopwatch.ElapsedMilliseconds
                    );
                    return ApiResponse<UpdateHqProductsResult>.Error(
                        "更新HQ商品部分失败",
                        "HQ_UPDATE_PARTIAL_FAILED",
                        result
                    );
                }

                LogUpdateHqProductsPerformance(
                    invoiceGuid,
                    details.Count,
                    activeStoreCodes.Count,
                    targetStoreCodes.Count,
                    result,
                    localPreparationMs,
                    localPriceMs,
                    (long)hqProductAndPriceElapsed.TotalMilliseconds,
                    (long)hqMultiCodeElapsed.TotalMilliseconds,
                    totalStopwatch.ElapsedMilliseconds
                );
                return ApiResponse<UpdateHqProductsResult>.OK(result, "更新HQ商品完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "更新HQ商品异常 InvoiceGuid={InvoiceGuid} TotalMs={TotalMs}",
                    invoiceGuid,
                    totalStopwatch.ElapsedMilliseconds
                );
                return ApiResponse<UpdateHqProductsResult>.Error(
                    $"更新HQ商品失败: {ex.Message}",
                    "HQ_UPDATE_ERROR",
                    result
                );
            }
        }

        /// <summary>
        /// 已有商品使用共享总闸，新商品使用身份总闸；两者均按编码锁住实际商品。
        /// 匹配变化时先释放整个事务再重新规划，绝不在已持商品锁上追加乱序锁或升级总闸。
        /// </summary>
        private async Task<(SetChildPurchasePriceLockScope Scope, List<Product?>? Products)> AcquireHqUpdateCostLockAsync(
            ISqlSugarClient db,
            StoreLocalSupplierInvoice header,
            List<StoreLocalSupplierInvoiceDetails> details,
            List<Product?> products,
            List<string> candidateProductCodes,
            int lockWaitMilliseconds
        )
        {
            var lockStopwatch = Stopwatch.StartNew();
            var remainingBudget = () => (int)Math.Max(0L, lockWaitMilliseconds - lockStopwatch.ElapsedMilliseconds);
            while (true)
            {
                var hasNewProducts = products.Any(product => string.IsNullOrWhiteSpace(product?.ProductCode));
                var productCodes = products.Select(product => product?.ProductCode);
                var scope = hasNewProducts
                    ? await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(
                        db, productCodes.Concat(candidateProductCodes), remainingBudget())
                    : await SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
                        db, productCodes, remainingBudget());
                var lockedProducts = await ResolveHqUpdateProductsAsync(db, header, details);
                var identitiesUnchanged = products.Zip(lockedProducts).All(pair =>
                    string.Equals(
                        pair.First?.ProductCode?.Trim(), pair.Second?.ProductCode?.Trim(), StringComparison.OrdinalIgnoreCase
                    )
                );
                if (identitiesUnchanged)
                {
                    // 候选编码预先纳入锁集，同批重复货号后续复用首条新建商品时仍受保护。
                    scope.EnsureCovers(db, hasNewProducts
                        ? lockedProducts.Select(product => product?.ProductCode).Concat(candidateProductCodes)
                        : lockedProducts.Select(product => product!.ProductCode));
                    return (scope, lockedProducts);
                }

                await db.Ado.RollbackTranAsync();
                products = lockedProducts;
                await db.Ado.BeginTranAsync();
                if (remainingBudget() == 0)
                    throw new SetChildPurchasePriceLockException("HB:SetChildPurchasePrice:IdentityChanged", -1);
            }
        }

        private static async Task<List<Product?>> ResolveHqUpdateProductsAsync(
            ISqlSugarClient db,
            StoreLocalSupplierInvoice header,
            IEnumerable<StoreLocalSupplierInvoiceDetails> details
        )
        {
            var products = new List<Product?>();
            foreach (var detail in details)
            {
                products.Add(await FindExistingProductAsync(
                    db, detail.ProductCode, header.SupplierCode ?? detail.SupplierCode, detail.ItemNumber, detail.Barcode
                ));
            }
            return products;
        }

        private async Task<PreparedSyncItem?> PrepareLocalProductForHqUpdateAsync(
            StoreLocalSupplierInvoice header,
            StoreLocalSupplierInvoiceDetails detail,
            string updatedBy,
            UpdateHqProductsResult result,
            string generatedProductCode,
            SetChildPurchasePriceLockScope lockScope,
            Product? lockedProduct = null
        )
        {
            var db = _context.Db;
            var product = lockedProduct ?? await FindExistingProductAsync(
                db,
                detail.ProductCode,
                header.SupplierCode ?? detail.SupplierCode,
                detail.ItemNumber,
                detail.Barcode
            );

            // 动态匹配只可复用本次已锁住的商品；任何旁路写入引入的新身份都必须回滚，不能越界写入。
            lockScope.EnsureCovers(db, new[] { product?.ProductCode ?? generatedProductCode });
            // 「更新HQ商品」只负责补写 HQ，不再隐式新建本地商品：新建统一走「新建商品」（批量执行），
            // 那条路径会同事务写主档、全部启用分店价、明细编码回填与本单 Create 变更历史，避免两套建商品语义不一致。
            // 因此这里匹配不到主档就逐行报错跳过。
            if (product == null)
            {
                AddError(result, detail.DetailGUID, detail.StoreCode, "未找到商品主档，请先执行「新建商品」");
                return null;
            }
            const bool isNewProduct = false;

            // 更新HQ商品链路不整行回写本单明细，避免把“上次进货价”等明细字段改成本次操作值。
            // 这里只在内存中补齐商品编码，供后续成本回算和 HQ 价格写入使用，不落库。
            detail.ProductCode = product.ProductCode;
            detail.StoreProductCode ??= BuildStoreProductCode(detail.StoreCode, product.ProductCode!);

            return new PreparedSyncItem(detail, product, isNewProduct);
        }

        private async Task<PreparedSyncItem?> PrepareLocalProductAsync(
            StoreLocalSupplierInvoice header,
            StoreLocalSupplierInvoiceDetails detail,
            List<string> activeStoreCodes,
            List<string> targetStoreCodes,
            string updatedBy,
            EnsureHqProductsResult result,
            string generatedProductCode
        )
        {
            if (detail.PurchasePrice == null || detail.PurchasePrice <= 0)
            {
                AddError(result, detail.DetailGUID, detail.StoreCode, "进货价必须大于0");
                return null;
            }

            var db = _context.Db;
            var now = DateTime.UtcNow;
            var product = await FindExistingProductAsync(
                db,
                detail.ProductCode,
                header.SupplierCode ?? detail.SupplierCode,
                detail.ItemNumber,
                detail.Barcode
            );

            var isNewProduct = product == null;
            if (product == null)
            {
                if (string.IsNullOrWhiteSpace(detail.ItemNumber))
                {
                    AddError(result, detail.DetailGUID, detail.StoreCode, "新建商品货号不能为空");
                    return null;
                }
                if (string.IsNullOrWhiteSpace(detail.Barcode))
                {
                    AddError(result, detail.DetailGUID, detail.StoreCode, "新建商品条码不能为空");
                    return null;
                }

                product = new Product
                {
                    UUID = generatedProductCode,
                    ProductCode = generatedProductCode,
                    ProductCategoryGUID = detail.ProductCategoryGUID,
                    LocalSupplierCode = header.SupplierCode ?? detail.SupplierCode,
                    ItemNumber = detail.ItemNumber,
                    Barcode = detail.Barcode,
                    ProductName = detail.ProductName ?? string.Empty,
                    ProductType = 0,
                    PurchasePrice = detail.PurchasePrice,
                    RetailPrice = ResolveRetailPrice(detail),
                    IsAutoPricing = detail.AutoPricing ?? true,
                    IsSpecialProduct = detail.IsSpecialProduct ?? false,
                    ProductImage = detail.ProductImage,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedBy = updatedBy,
                    UpdatedBy = updatedBy,
                };

                await db.Insertable(product).ExecuteCommandAsync();
                result.HbwebCreated++;
            }
            else
            {
                // 已有商品是全局主档，这里只绑定明细并更新目标分店价格，避免越过分店范围修改商品资料。
            }

            if (string.IsNullOrWhiteSpace(product.ProductCode))
            {
                throw new InvalidOperationException("同步分店进货单时商品编码不能为空");
            }

            var productCode = product.ProductCode;
            detail.ProductCode = productCode;
            detail.StoreProductCode ??= BuildStoreProductCode(detail.StoreCode, productCode);
            detail.LastPurchasePrice = detail.PurchasePrice ?? product.PurchasePrice;
            detail.UpdatedAt = now;
            detail.UpdatedBy = updatedBy;
            await db.Updateable(detail).ExecuteCommandAsync();

            // 新建商品为所有启用分店创建价格；已有商品只更新/补齐目标分店。
            var localPriceScope = isNewProduct ? activeStoreCodes : targetStoreCodes;
            await UpsertLocalStorePricesAsync(detail, product, localPriceScope, updatedBy);

            return new PreparedSyncItem(detail, product, isNewProduct);
        }

        private async Task RecordCreatedLocalProductHistoryAsync(
            IEnumerable<PreparedSyncItem> preparedItems,
            string invoiceGuid,
            Guid batchGuid,
            string? actorUserGuid,
            string actorName
        )
        {
            var createdProducts = preparedItems
                .Where(item => item.IsNewProduct && !string.IsNullOrWhiteSpace(item.Product.ProductCode))
                .GroupBy(item => item.Product.ProductCode!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First().Product)
                .ToList();
            if (createdProducts.Count == 0)
            {
                return;
            }

            // 候选编码只会分配给本流程刚插入的 Product，因此创建前必定为空；直接从内存实体
            // 构造创建后快照，避免本地事务内再读取三张商品表并扩大锁范围。
            var beforeSnapshots = new Dictionary<string, WarehouseProductChangeSnapshotDto>(
                StringComparer.OrdinalIgnoreCase
            );
            var afterSnapshots = createdProducts.ToDictionary(
                product => product.ProductCode!,
                CreateCreatedProductSnapshot,
                StringComparer.OrdinalIgnoreCase
            );

            // 历史服务在同一本地事务内落库；一旦失败，商品主档、明细和分店价格全部回滚。
            await _changeHistoryService.RecordChangesAsync(
                beforeSnapshots,
                afterSnapshots,
                new WarehouseProductChangeHistoryContextDto
                {
                    Action = "Create",
                    Source = "LocalSupplierInvoiceHqProductSync",
                    SourceReference = invoiceGuid,
                    BatchGuid = batchGuid,
                    // 后台任务没有可靠的请求上下文，审计身份必须来自提交时显式传入的快照。
                    ActorUserGuid = actorUserGuid,
                    ActorName = actorName,
                    OccurredAtUtc = DateTime.UtcNow,
                }
            );
        }

        private static WarehouseProductChangeSnapshotDto CreateCreatedProductSnapshot(Product product)
        {
            var productCode = product.ProductCode;
            if (string.IsNullOrWhiteSpace(productCode))
            {
                throw new InvalidOperationException("新建商品历史快照的商品编码不能为空");
            }

            var productSource = new WarehouseProductChangeSourceValuesDto
            {
                ImportPrice = product.PurchasePrice,
                RetailPrice = product.RetailPrice,
                LocalSupplierCode = product.LocalSupplierCode,
                ProductName = product.ProductName,
                EnglishName = product.EnglishName,
                ItemNumber = product.ItemNumber,
                Barcode = product.Barcode,
                ProductType = product.ProductType,
                ProductCategoryGuid = product.ProductCategoryGUID,
                WarehouseCategoryGuid = product.WarehouseCategoryGUID,
                MiddlePackageQuantity = product.MiddlePackageQuantity,
                ProductImage = product.ProductImage,
                IsAutoPricing = product.IsAutoPricing,
                IsActive = product.IsActive,
            };

            return new WarehouseProductChangeSnapshotDto
            {
                ProductCode = productCode,
                ProductSource = productSource,
                ImportPrice = productSource.ImportPrice,
                RetailPrice = productSource.RetailPrice,
                LocalSupplierCode = productSource.LocalSupplierCode,
                ProductName = productSource.ProductName,
                EnglishName = productSource.EnglishName,
                ItemNumber = productSource.ItemNumber,
                Barcode = productSource.Barcode,
                ProductType = productSource.ProductType,
                ProductCategoryGuid = productSource.ProductCategoryGuid,
                WarehouseCategoryGuid = productSource.WarehouseCategoryGuid,
                MiddlePackageQuantity = productSource.MiddlePackageQuantity,
                ProductImage = productSource.ProductImage,
                IsAutoPricing = productSource.IsAutoPricing,
                IsActive = productSource.IsActive,
            };
        }

        private async Task UpsertLocalStorePricesAsync(
            StoreLocalSupplierInvoiceDetails detail,
            Product product,
            List<string> storeCodes,
            string updatedBy
        )
        {
            var db = _context.Db;
            var now = DateTime.UtcNow;
            var productCode = product.ProductCode!;
            var existingPrices = await db.Queryable<StoreRetailPrice>()
                .Where(x =>
                    x.StoreCode != null
                    && storeCodes.Contains(x.StoreCode)
                    && x.ProductCode == productCode
                    && x.IsDeleted == false
                )
                .ToListAsync();
            var existingByStore = existingPrices
                .Where(x => !string.IsNullOrWhiteSpace(x.StoreCode))
                .ToDictionary(x => x.StoreCode!, x => x);

            foreach (var storeCode in storeCodes)
            {
                if (existingByStore.TryGetValue(storeCode, out var existing))
                {
                    existing.SupplierCode = product.LocalSupplierCode ?? detail.SupplierCode;
                    existing.PurchasePrice = detail.PurchasePrice;
                    existing.StoreRetailPriceValue = ResolveRetailPrice(detail);
                    existing.DiscountRate = detail.DiscountRate;
                    existing.IsAutoPricing = detail.AutoPricing ?? existing.IsAutoPricing;
                    existing.IsSpecialProduct = detail.IsSpecialProduct ?? existing.IsSpecialProduct;
                    existing.UpdatedAt = now;
                    existing.UpdatedBy = updatedBy;
                    await db.Updateable(existing).ExecuteCommandAsync();
                    continue;
                }

                await db.Insertable(new StoreRetailPrice
                {
                    UUID = UuidHelper.GenerateUuid7(),
                    StoreCode = storeCode,
                    ProductCode = productCode,
                    StoreProductCode = BuildStoreProductCode(storeCode, productCode),
                    SupplierCode = product.LocalSupplierCode ?? detail.SupplierCode,
                    PurchasePrice = detail.PurchasePrice,
                    StoreRetailPriceValue = ResolveRetailPrice(detail),
                    DiscountRate = detail.DiscountRate,
                    IsActive = true,
                    IsAutoPricing = detail.AutoPricing ?? true,
                    IsSpecialProduct = detail.IsSpecialProduct ?? false,
                    IsDeleted = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedBy = updatedBy,
                    UpdatedBy = updatedBy,
                }).ExecuteCommandAsync();
            }
        }

        private async Task SyncHqProductAsync(
            PreparedSyncItem item,
            List<string> activeStoreCodes,
            List<string> targetStoreCodes,
            string updatedBy,
            EnsureHqProductsResult result
        )
        {
            var hqDb = _hqContext.Db;
            var detail = item.Detail;
            var product = item.Product;
            var productCode = product.ProductCode!;
            var now = DateTime.UtcNow;
            var domesticSupplierCode = item.DomesticSupplierCode;
            var hqProduct = await hqDb.Queryable<DIC_商品信息字典表>()
                .Where(x => x.H商品编码 == productCode)
                .FirstAsync();
            var hqProductExisted = hqProduct != null;

            if (hqProductExisted)
            {
                result.HqExisting++;
                // HQ 商品字典是全局主档；只在 CBP 供应商缺失或误写 200 时补国内供应商编码。
                await PatchHqCbpSupplierCodeIfNeededAsync(
                    hqDb,
                    hqProduct!,
                    domesticSupplierCode,
                    updatedBy,
                    now
                );
                result.HqSynced++;
            }
            else
            {
                await hqDb.Insertable(new DIC_商品信息字典表
                {
                    HGUID = product.UUID,
                    H商品标签GUID = detail.ProductTagGUID ?? string.Empty,
                    H商品分类码GUID = product.ProductCategoryGUID ?? string.Empty,
                    H供货商编码 = product.LocalSupplierCode ?? string.Empty,
                    H商品编码 = productCode,
                    H货号 = product.ItemNumber ?? string.Empty,
                    H主条形码 = product.Barcode ?? string.Empty,
                    H商品名称 = product.ProductName ?? string.Empty,
                    H商品类型 = product.ProductType ?? 0,
                    H大写名称 = product.ProductName ?? string.Empty,
                    H规格 = detail.Specification ?? string.Empty,
                    H单位 = detail.Unit ?? string.Empty,
                    H进货价 = product.PurchasePrice ?? 0,
                    H零售价 = product.RetailPrice ?? ResolveRetailPrice(detail),
                    H是否自动定价 = product.IsAutoPricing,
                    H商品图片 = product.ProductImage ?? string.Empty,
                    中包数量 = product.MiddlePackageQuantity ?? 0,
                    H腾讯云图地址 = string.Empty,
                    H使用状态 = product.IsActive,
                    H是否特殊商品 = product.IsSpecialProduct,
                    H进货单主表GUID = detail.InvoiceGUID ?? string.Empty,
                    H进货单详情GUID = detail.DetailGUID,
                    CBP商品中文名称 = product.ProductName ?? string.Empty,
                    CBP供应商编码 = domesticSupplierCode ?? string.Empty,
                    CBP商品分类码GUID = product.WarehouseCategoryGUID ?? string.Empty,
                    FGC_Creator = updatedBy,
                    FGC_CreateDate = now,
                    FGC_LastModifier = updatedBy,
                    FGC_LastModifyDate = now,
                    FGC_UpdateHelp = string.Empty,
                }).IgnoreColumns(x => x.ID).ExecuteCommandAsync();
                result.HqCreated++;
            }

            // 新建HQ商品为所有启用分店创建价格；已有HQ商品只更新/补齐目标分店。
            var hqPriceScope = hqProductExisted ? targetStoreCodes : activeStoreCodes;
            await UpsertHqStorePricesAsync(detail, product, hqPriceScope, updatedBy, result);
        }

        private async Task UpdateHqStorePricesAsync(
            PreparedSyncItem item,
            List<string> activeStoreCodes,
            List<string> targetStoreCodes,
            UpdateToStorePricesFields updateFields,
            string updatedBy,
            UpdateHqProductsResult result
        )
        {
            var hqDb = _hqContext.Db;
            var detail = item.Detail;
            var product = item.Product;
            var productCode = product.ProductCode!;
            var now = DateTime.UtcNow;
            var domesticSupplierCode = item.DomesticSupplierCode;

            var hqProduct = await FindExistingHqProductAsync(
                hqDb,
                productCode,
                product.LocalSupplierCode ?? detail.SupplierCode,
                product.ItemNumber ?? detail.ItemNumber,
                product.Barcode ?? detail.Barcode
            );
            var hqProductCode = hqProduct?.H商品编码 ?? productCode;
            if (hqProduct == null)
            {
                await hqDb.Insertable(new DIC_商品信息字典表
                {
                    HGUID = product.UUID,
                    H商品标签GUID = detail.ProductTagGUID ?? string.Empty,
                    H商品分类码GUID = product.ProductCategoryGUID ?? string.Empty,
                    H供货商编码 = product.LocalSupplierCode ?? string.Empty,
                    H商品编码 = productCode,
                    H货号 = product.ItemNumber ?? string.Empty,
                    H主条形码 = product.Barcode ?? string.Empty,
                    H商品名称 = product.ProductName ?? string.Empty,
                    H商品类型 = product.ProductType ?? 0,
                    H大写名称 = product.ProductName ?? string.Empty,
                    H规格 = detail.Specification ?? string.Empty,
                    H单位 = detail.Unit ?? string.Empty,
                    H进货价 = product.PurchasePrice ?? 0,
                    H零售价 = product.RetailPrice ?? ResolveRetailPrice(detail),
                    H是否自动定价 = product.IsAutoPricing,
                    H商品图片 = product.ProductImage ?? string.Empty,
                    中包数量 = product.MiddlePackageQuantity ?? 0,
                    H腾讯云图地址 = string.Empty,
                    H使用状态 = product.IsActive,
                    H是否特殊商品 = product.IsSpecialProduct,
                    H进货单主表GUID = detail.InvoiceGUID ?? string.Empty,
                    H进货单详情GUID = detail.DetailGUID,
                    CBP商品中文名称 = product.ProductName ?? string.Empty,
                    CBP供应商编码 = domesticSupplierCode ?? string.Empty,
                    CBP商品分类码GUID = product.WarehouseCategoryGUID ?? string.Empty,
                    FGC_Creator = updatedBy,
                    FGC_CreateDate = now,
                    FGC_LastModifier = updatedBy,
                    FGC_LastModifyDate = now,
                    FGC_UpdateHelp = string.Empty,
                }).IgnoreColumns(x => x.ID).ExecuteCommandAsync();
                result.HqCreated++;
            }
            else
            {
                await PatchHqCbpSupplierCodeIfNeededAsync(
                    hqDb,
                    hqProduct,
                    domesticSupplierCode,
                    updatedBy,
                    now
                );
                result.HqExisting++;
                result.HqSynced++;
            }

            // 新建HQ商品后要补齐所有启用分店价格；已有HQ商品只更新用户选择的目标分店。
            var hqPriceScope = hqProduct == null ? activeStoreCodes : targetStoreCodes;
            var existingPrices = await hqDb.Queryable<DIC_商品零售价表>()
                .Where(x => hqPriceScope.Contains(x.H分店代码) && x.H商品编码 == hqProductCode)
                .ToListAsync();
            var existingByStore = existingPrices.ToDictionary(x => x.H分店代码, x => x);
            var inserts = new List<HqStorePriceWrite>();
            var updates = new List<HqStorePriceWrite>();

            foreach (var storeCode in hqPriceScope)
            {
                if (!existingByStore.TryGetValue(storeCode, out var price))
                {
                    price = BuildHqStorePrice(detail, product, storeCode, updatedBy, now, hqProductCode);
                    if (
                        !ApplyAllHqFieldsForInsert(
                            price,
                            detail,
                            product,
                            updateFields,
                            result,
                            storeCode,
                            out var insertCounts
                        )
                    )
                    {
                        continue;
                    }
                    inserts.Add(new HqStorePriceWrite(price, detail.DetailGUID, storeCode, insertCounts));
                    continue;
                }

                if (
                    !ApplySelectedHqFields(
                        price,
                        detail,
                        product,
                        updateFields,
                        result,
                        storeCode,
                        out var updateCounts
                    )
                )
                {
                    continue;
                }
                price.FGC_LastModifier = updatedBy;
                price.FGC_LastModifyDate = now;
                updates.Add(new HqStorePriceWrite(price, detail.DetailGUID, storeCode, updateCounts));
            }

            await ExecuteHqStorePriceInsertBatchesAsync(inserts, updateFields, result);
            await ExecuteHqStorePriceUpdateBatchesAsync(updates, updateFields, result);
        }

        private async Task ExecuteHqStorePriceInsertBatchesAsync(
            List<HqStorePriceWrite> writes,
            UpdateToStorePricesFields updateFields,
            UpdateHqProductsResult result
        )
        {
            var hqDb = _hqContext.Db;
            foreach (var batch in writes.Chunk(HqWriteBatchSize))
            {
                var batchWrites = batch.ToList();
                try
                {
                    var affectedRows = await hqDb.Insertable(batchWrites.Select(write => write.Price).ToList())
                        .IgnoreColumns(price => price.ID)
                        .ExecuteCommandAsync();
                    EnsureExpectedAffectedRows(
                        affectedRows,
                        batchWrites.Count,
                        "批量新增HQ分店价格"
                    );
                    RegisterHqStorePriceWriteSuccesses(result, batchWrites);
                }
                catch (Exception batchException)
                {
                    _logger.LogWarning(
                        batchException,
                        "批量新增HQ分店价格失败，降级逐条处理 Count={Count}",
                        batchWrites.Count
                    );
                    foreach (var write in batchWrites)
                    {
                        try
                        {
                            await UpsertHqStorePriceAfterBatchFailureAsync(write, updateFields);
                            RegisterHqStorePriceWriteSuccess(result, write);
                        }
                        catch (Exception ex)
                        {
                            AddError(
                                result,
                                write.DetailGuid,
                                write.StoreCode,
                                $"更新HQ分店价格失败：{ex.Message}"
                            );
                        }
                    }
                }
            }
        }

        private async Task ExecuteHqStorePriceUpdateBatchesAsync(
            List<HqStorePriceWrite> writes,
            UpdateToStorePricesFields updateFields,
            UpdateHqProductsResult result
        )
        {
            var hqDb = _hqContext.Db;
            // 同一请求中不同明细可能只有部分字段有效；按实际字段掩码分组，避免写回本行已跳过字段的旧值。
            foreach (var writeGroup in writes.GroupBy(write => write.Counts))
            {
                var updateColumns = BuildHqPriceUpdateColumns(writeGroup.Key, updateFields);
                foreach (var batch in writeGroup.Chunk(HqWriteBatchSize))
                {
                    var batchWrites = batch.ToList();
                    try
                    {
                        var affectedRows = await hqDb.Updateable(batchWrites.Select(write => write.Price).ToList())
                            .UpdateColumns(updateColumns)
                            .ExecuteCommandAsync();
                        EnsureExpectedAffectedRows(
                            affectedRows,
                            batchWrites.Count,
                            "批量更新HQ分店价格"
                        );
                        RegisterHqStorePriceWriteSuccesses(result, batchWrites);
                    }
                    catch (Exception batchException)
                    {
                        _logger.LogWarning(
                            batchException,
                            "批量更新HQ分店价格失败，降级逐条处理 Count={Count}",
                            batchWrites.Count
                        );
                        foreach (var write in batchWrites)
                        {
                            try
                            {
                                await UpsertHqStorePriceAfterBatchFailureAsync(write, updateFields);
                                RegisterHqStorePriceWriteSuccess(result, write);
                            }
                            catch (Exception ex)
                            {
                                AddError(
                                    result,
                                    write.DetailGuid,
                                    write.StoreCode,
                                    $"更新HQ分店价格失败：{ex.Message}"
                                );
                            }
                        }
                    }
                }
            }
        }

        private async Task UpsertHqStorePriceAfterBatchFailureAsync(
            HqStorePriceWrite write,
            UpdateToStorePricesFields updateFields
        )
        {
            var hqDb = _hqContext.Db;
            var desired = write.Price;
            var existing = await hqDb.Queryable<DIC_商品零售价表>()
                .Where(price =>
                    price.H分店代码 == desired.H分店代码
                    && price.H商品编码 == desired.H商品编码
                )
                .FirstAsync();
            if (existing == null)
            {
                var affectedRows = await hqDb.Insertable(desired)
                    .IgnoreColumns(price => price.ID)
                    .ExecuteCommandAsync();
                EnsureExpectedAffectedRows(affectedRows, 1, "新增HQ分店价格");
                return;
            }

            CopySelectedHqPriceFields(existing, desired, write.Counts);
            existing.FGC_LastModifier = desired.FGC_LastModifier;
            existing.FGC_LastModifyDate = desired.FGC_LastModifyDate;
            var updatedRows = await hqDb.Updateable(existing)
                .UpdateColumns(BuildHqPriceUpdateColumns(write.Counts, updateFields))
                .ExecuteCommandAsync();
            EnsureExpectedAffectedRows(updatedRows, 1, "更新HQ分店价格");
        }

        private static void CopySelectedHqPriceFields(
            DIC_商品零售价表 target,
            DIC_商品零售价表 source,
            HqFieldUpdateCounts counts
        )
        {
            if (counts.PurchasePrice > 0)
                target.H进货价 = source.H进货价;
            if (counts.RetailPrice > 0)
                target.H分店零售价 = source.H分店零售价;
            if (counts.AutoPricing > 0)
                target.H是否自动定价 = source.H是否自动定价;
            if (counts.SpecialProduct > 0)
                target.H是否特殊商品 = source.H是否特殊商品;
            if (counts.DiscountRate > 0)
                target.H折扣率 = source.H折扣率;
        }

        private static string[] BuildHqPriceUpdateColumns(UpdateToStorePricesFields updateFields)
        {
            var columns = new List<string>
            {
                nameof(DIC_商品零售价表.FGC_LastModifier),
                nameof(DIC_商品零售价表.FGC_LastModifyDate),
            };
            if (updateFields.UpdatePurchasePrice)
                columns.Add(nameof(DIC_商品零售价表.H进货价));
            if (updateFields.UpdateRetailPrice)
                columns.Add(nameof(DIC_商品零售价表.H分店零售价));
            if (updateFields.UpdateIsAutoPricing)
                columns.Add(nameof(DIC_商品零售价表.H是否自动定价));
            if (updateFields.UpdateIsSpecialProduct)
                columns.Add(nameof(DIC_商品零售价表.H是否特殊商品));
            if (updateFields.UpdateDiscountRate)
                columns.Add(nameof(DIC_商品零售价表.H折扣率));
            return columns.ToArray();
        }

        private static string[] BuildHqPriceUpdateColumns(
            HqFieldUpdateCounts counts,
            UpdateToStorePricesFields updateFields
        )
        {
            var columns = BuildHqPriceUpdateColumns(updateFields).ToHashSet(StringComparer.Ordinal);
            if (counts.PurchasePrice == 0)
                columns.Remove(nameof(DIC_商品零售价表.H进货价));
            if (counts.RetailPrice == 0)
                columns.Remove(nameof(DIC_商品零售价表.H分店零售价));
            if (counts.AutoPricing == 0)
                columns.Remove(nameof(DIC_商品零售价表.H是否自动定价));
            if (counts.SpecialProduct == 0)
                columns.Remove(nameof(DIC_商品零售价表.H是否特殊商品));
            if (counts.DiscountRate == 0)
                columns.Remove(nameof(DIC_商品零售价表.H折扣率));
            return columns.ToArray();
        }

        private static void RegisterHqStorePriceWriteSuccesses(
            UpdateHqProductsResult result,
            List<HqStorePriceWrite> writes
        )
        {
            foreach (var write in writes)
                RegisterHqStorePriceWriteSuccess(result, write);
        }

        private static void RegisterHqStorePriceWriteSuccess(
            UpdateHqProductsResult result,
            HqStorePriceWrite write
        )
        {
            result.Updated++;
            result.HqPurchasePricesUpdated += write.Counts.PurchasePrice;
            result.HqRetailPricesUpdated += write.Counts.RetailPrice;
            result.HqAutoPricingUpdated += write.Counts.AutoPricing;
            result.HqSpecialProductsUpdated += write.Counts.SpecialProduct;
            result.HqDiscountRatesUpdated += write.Counts.DiscountRate;
        }

        private async Task SyncHqMultiCodesAsync(
            PreparedSyncItem item,
            List<string> targetStoreCodes,
            string updatedBy,
            UpdateHqProductsResult result
        )
        {
            var productCode = item.Product.ProductCode;
            if (string.IsNullOrWhiteSpace(productCode))
                return;

            var localDb = _context.Db;
            var hqDb = _hqContext.Db;
            var now = DateTime.UtcNow;
            var storeCodes = targetStoreCodes
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();

            var productSetCodes = await localDb.Queryable<ProductSetCode>()
                .Where(x =>
                    x.ProductCode == productCode
                    && x.IsDeleted == false
                    && x.SetBarcode != null
                )
                .ToListAsync();
            // 两个业务键都为空时无法安全重查，避免写入后形成不可去重的HQ多码记录。
            productSetCodes = productSetCodes
                .Where(x =>
                    NormalizeCaseInsensitiveValue(x.SetProductCode) != null
                    || NormalizeCaseInsensitiveValue(x.SetBarcode) != null
                )
                .ToList();
            if (productSetCodes.Count == 0)
                return;

            var storeMultiCodes = await localDb.Queryable<StoreMultiCodeProduct>()
                .Where(x =>
                    x.ProductCode == productCode
                    && x.StoreCode != null
                    && storeCodes.Contains(x.StoreCode)
                    && x.IsDeleted == false
                    && x.MultiBarcode != null
                )
                .ToListAsync();
            var storeMultiByCode = storeMultiCodes
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.StoreCode)
                    && !string.IsNullOrWhiteSpace(x.MultiCodeProductCode)
                )
                .GroupBy(x => BuildHqStoreMultiCodeKey(x.StoreCode, x.ProductCode, x.MultiCodeProductCode))
                .ToDictionary(g => g.Key, g => g.First());
            var storeMultiByBarcode = storeMultiCodes
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.StoreCode)
                    && !string.IsNullOrWhiteSpace(x.MultiBarcode)
                )
                .GroupBy(x => BuildHqStoreMultiCodeKey(
                    x.StoreCode,
                    x.ProductCode,
                    NormalizeCaseInsensitiveValue(x.MultiBarcode)
                ))
                .ToDictionary(g => g.Key, g => g.First());

            var existingHqProductSetCodes = await hqDb.Queryable<DIC_一品多码表>()
                .Where(x => x.H商品编码 == productCode)
                .ToListAsync();
            var hqProductSetByCode = existingHqProductSetCodes
                .Where(x => !string.IsNullOrWhiteSpace(x.H多码商品编号))
                .GroupBy(x => x.H多码商品编号!)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var hqProductSetByBarcode = existingHqProductSetCodes
                .Where(x => !string.IsNullOrWhiteSpace(x.H多条形码))
                .GroupBy(x => NormalizeCaseInsensitiveValue(x.H多条形码)!)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var productSetInserts = new List<HqEntityWrite<DIC_一品多码表>>();
            var productSetUpdates = new List<HqEntityWrite<DIC_一品多码表>>();
            var syncableProductSetCodes = new List<ProductSetCode>();

            foreach (var productSetCode in productSetCodes)
            {
                var mapped = BuildHqProductSetCode(item, productSetCode, updatedBy, now);
                DIC_一品多码表? existing;
                try
                {
                    existing = FindExistingHqProductSetCode(
                        productSetCode,
                        hqProductSetByCode,
                        hqProductSetByBarcode
                    );
                }
                catch (InvalidOperationException ex)
                {
                    AddError(
                        result,
                        item.Detail.DetailGUID,
                        null,
                        $"同步HQ商品多码失败：{ex.Message}"
                    );
                    continue;
                }

                syncableProductSetCodes.Add(productSetCode);
                if (existing == null)
                {
                    productSetInserts.Add(
                        new HqEntityWrite<DIC_一品多码表>(mapped, item.Detail.DetailGUID, null)
                    );
                    continue;
                }

                ApplyHqProductSetCode(existing, mapped);
                productSetUpdates.Add(
                    new HqEntityWrite<DIC_一品多码表>(existing, item.Detail.DetailGUID, null)
                );
            }

            await ExecuteHqEntityBatchesWithFallbackAsync(
                productSetInserts,
                entities => hqDb.Insertable(entities).IgnoreColumns(entity => entity.ID).ExecuteCommandAsync(),
                entity => UpsertHqProductSetCodeAfterBatchFailureAsync(entity),
                count => result.HqProductSetCodesCreated += count,
                outcome =>
                {
                    if (outcome == HqFallbackWriteOutcome.Created)
                        result.HqProductSetCodesCreated++;
                    else
                        result.HqProductSetCodesUpdated++;
                },
                result,
                "新增HQ商品多码"
            );
            await ExecuteHqEntityBatchesWithFallbackAsync(
                productSetUpdates,
                entities => hqDb.Updateable(entities)
                    .UpdateColumns(BuildHqProductSetCodeUpdateColumns())
                    .ExecuteCommandAsync(),
                entity => UpsertHqProductSetCodeAfterBatchFailureAsync(entity),
                count => result.HqProductSetCodesUpdated += count,
                outcome =>
                {
                    if (outcome == HqFallbackWriteOutcome.Created)
                        result.HqProductSetCodesCreated++;
                    else
                        result.HqProductSetCodesUpdated++;
                },
                result,
                "更新HQ商品多码"
            );

            var existingHqStoreMultiCodes = await hqDb.Queryable<DIC_分店一品多码表>()
                .Where(x =>
                    x.H商品编码 == productCode
                    && x.H分店代码 != null
                    && storeCodes.Contains(x.H分店代码)
                )
                .ToListAsync();
            var hqStoreByCode = existingHqStoreMultiCodes
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.H分店代码)
                    && !string.IsNullOrWhiteSpace(x.H多码商品编码)
                )
                .GroupBy(x => BuildHqStoreMultiCodeKey(x.H分店代码, x.H商品编码, x.H多码商品编码))
                .ToDictionary(g => g.Key, g => g.First());
            var hqStoreByBarcode = existingHqStoreMultiCodes
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.H分店代码)
                    && !string.IsNullOrWhiteSpace(x.H多条形码)
                )
                .GroupBy(x => BuildHqStoreMultiCodeKey(
                    x.H分店代码,
                    x.H商品编码,
                    NormalizeCaseInsensitiveValue(x.H多条形码)
                ))
                .ToDictionary(g => g.Key, g => g.First());
            var storeMultiCodeInserts = new List<HqEntityWrite<DIC_分店一品多码表>>();
            var storeMultiCodeUpdates = new List<HqEntityWrite<DIC_分店一品多码表>>();

            foreach (var storeCode in storeCodes)
            {
                foreach (var productSetCode in syncableProductSetCodes)
                {
                    var storeMultiCode = FindLocalStoreMultiCode(
                        storeCode,
                        productCode,
                        productSetCode,
                        storeMultiByCode,
                        storeMultiByBarcode
                    );
                    var mapped = BuildHqStoreMultiCode(
                        item,
                        productSetCode,
                        storeMultiCode,
                        storeCode,
                        updatedBy,
                        now
                    );
                    DIC_分店一品多码表? existing;
                    try
                    {
                        existing = FindExistingHqStoreMultiCode(
                            storeCode,
                            productCode,
                            productSetCode,
                            hqStoreByCode,
                            hqStoreByBarcode
                        );
                    }
                    catch (InvalidOperationException ex)
                    {
                        AddError(
                            result,
                            item.Detail.DetailGUID,
                            storeCode,
                            $"同步HQ分店多码失败：{ex.Message}"
                        );
                        continue;
                    }
                    if (existing == null)
                    {
                        storeMultiCodeInserts.Add(
                            new HqEntityWrite<DIC_分店一品多码表>(
                                mapped,
                                item.Detail.DetailGUID,
                                storeCode
                            )
                        );
                        continue;
                    }

                    ApplyHqStoreMultiCode(existing, mapped);
                    storeMultiCodeUpdates.Add(
                        new HqEntityWrite<DIC_分店一品多码表>(
                            existing,
                            item.Detail.DetailGUID,
                            storeCode
                        )
                    );
                }
            }

            await ExecuteHqEntityBatchesWithFallbackAsync(
                storeMultiCodeInserts,
                entities => hqDb.Insertable(entities).IgnoreColumns(entity => entity.ID).ExecuteCommandAsync(),
                entity => UpsertHqStoreMultiCodeAfterBatchFailureAsync(entity),
                count => result.HqStoreMultiCodesCreated += count,
                outcome =>
                {
                    if (outcome == HqFallbackWriteOutcome.Created)
                        result.HqStoreMultiCodesCreated++;
                    else
                        result.HqStoreMultiCodesUpdated++;
                },
                result,
                "新增HQ分店多码"
            );
            await ExecuteHqEntityBatchesWithFallbackAsync(
                storeMultiCodeUpdates,
                entities => hqDb.Updateable(entities)
                    .UpdateColumns(BuildHqStoreMultiCodeUpdateColumns())
                    .ExecuteCommandAsync(),
                entity => UpsertHqStoreMultiCodeAfterBatchFailureAsync(entity),
                count => result.HqStoreMultiCodesUpdated += count,
                outcome =>
                {
                    if (outcome == HqFallbackWriteOutcome.Created)
                        result.HqStoreMultiCodesCreated++;
                    else
                        result.HqStoreMultiCodesUpdated++;
                },
                result,
                "更新HQ分店多码"
            );
        }

        private async Task ExecuteHqEntityBatchesWithFallbackAsync<T>(
            List<HqEntityWrite<T>> writes,
            Func<List<T>, Task<int>> batchWriter,
            Func<T, Task<HqFallbackWriteOutcome>> singleWriter,
            Action<int> registerBatchSuccess,
            Action<HqFallbackWriteOutcome> registerSingleSuccess,
            UpdateHqProductsResult result,
            string operationName
        )
        {
            foreach (var batch in writes.Chunk(HqWriteBatchSize))
            {
                var batchWrites = batch.ToList();
                try
                {
                    var affectedRows = await batchWriter(
                        batchWrites.Select(write => write.Entity).ToList()
                    );
                    EnsureExpectedAffectedRows(affectedRows, batchWrites.Count, operationName);
                    registerBatchSuccess(batchWrites.Count);
                }
                catch (Exception batchException)
                {
                    _logger.LogWarning(
                        batchException,
                        "{OperationName}批量写入失败，降级逐条处理 Count={Count}",
                        operationName,
                        batchWrites.Count
                    );
                    foreach (var write in batchWrites)
                    {
                        try
                        {
                            var outcome = await singleWriter(write.Entity);
                            registerSingleSuccess(outcome);
                        }
                        catch (Exception ex)
                        {
                            AddError(
                                result,
                                write.DetailGuid,
                                write.StoreCode,
                                $"{operationName}失败：{ex.Message}"
                            );
                        }
                    }
                }
            }
        }

        private async Task<HqFallbackWriteOutcome> UpsertHqProductSetCodeAfterBatchFailureAsync(
            DIC_一品多码表 desired
        )
        {
            var hqDb = _hqContext.Db;
            var existing = await FindHqProductSetCodeForFallbackAsync(desired);
            if (existing == null)
            {
                var affectedRows = await hqDb.Insertable(desired)
                    .IgnoreColumns(row => row.ID)
                    .ExecuteCommandAsync();
                EnsureExpectedAffectedRows(affectedRows, 1, "新增HQ商品多码");
                return HqFallbackWriteOutcome.Created;
            }

            ApplyHqProductSetCode(existing, desired);
            var updatedRows = await hqDb.Updateable(existing)
                .UpdateColumns(BuildHqProductSetCodeUpdateColumns())
                .ExecuteCommandAsync();
            EnsureExpectedAffectedRows(updatedRows, 1, "更新HQ商品多码");
            return HqFallbackWriteOutcome.Updated;
        }

        private async Task<HqFallbackWriteOutcome> UpsertHqStoreMultiCodeAfterBatchFailureAsync(
            DIC_分店一品多码表 desired
        )
        {
            var hqDb = _hqContext.Db;
            var existing = await FindHqStoreMultiCodeForFallbackAsync(desired);
            if (existing == null)
            {
                var affectedRows = await hqDb.Insertable(desired)
                    .IgnoreColumns(row => row.ID)
                    .ExecuteCommandAsync();
                EnsureExpectedAffectedRows(affectedRows, 1, "新增HQ分店多码");
                return HqFallbackWriteOutcome.Created;
            }

            ApplyHqStoreMultiCode(existing, desired);
            var updatedRows = await hqDb.Updateable(existing)
                .UpdateColumns(BuildHqStoreMultiCodeUpdateColumns())
                .ExecuteCommandAsync();
            EnsureExpectedAffectedRows(updatedRows, 1, "更新HQ分店多码");
            return HqFallbackWriteOutcome.Updated;
        }

        private async Task<DIC_一品多码表?> FindHqProductSetCodeForFallbackAsync(
            DIC_一品多码表 desired
        )
        {
            var candidates = await _hqContext.Db.Queryable<DIC_一品多码表>()
                .Where(row => row.H商品编码 == desired.H商品编码)
                .ToListAsync();
            var codeKey = NormalizeCaseInsensitiveValue(desired.H多码商品编号);
            var barcodeKey = NormalizeCaseInsensitiveValue(desired.H多条形码);
            var byCode = codeKey == null
                ? null
                : candidates.FirstOrDefault(row =>
                    NormalizeCaseInsensitiveValue(row.H多码商品编号) == codeKey
                );
            var byBarcode = barcodeKey == null
                ? null
                : candidates.FirstOrDefault(row =>
                    NormalizeCaseInsensitiveValue(row.H多条形码) == barcodeKey
                );

            EnsureFallbackKeysDoNotConflict(byCode, byBarcode, "HQ商品多码");
            return byCode ?? byBarcode;
        }

        private async Task<DIC_分店一品多码表?> FindHqStoreMultiCodeForFallbackAsync(
            DIC_分店一品多码表 desired
        )
        {
            var candidates = await _hqContext.Db.Queryable<DIC_分店一品多码表>()
                .Where(row =>
                    row.H分店代码 == desired.H分店代码
                    && row.H商品编码 == desired.H商品编码
                )
                .ToListAsync();
            var codeKey = NormalizeCaseInsensitiveValue(desired.H多码商品编码);
            var barcodeKey = NormalizeCaseInsensitiveValue(desired.H多条形码);
            var byCode = codeKey == null
                ? null
                : candidates.FirstOrDefault(row =>
                    NormalizeCaseInsensitiveValue(row.H多码商品编码) == codeKey
                );
            var byBarcode = barcodeKey == null
                ? null
                : candidates.FirstOrDefault(row =>
                    NormalizeCaseInsensitiveValue(row.H多条形码) == barcodeKey
                );

            EnsureFallbackKeysDoNotConflict(byCode, byBarcode, "HQ分店多码");
            return byCode ?? byBarcode;
        }

        private static void EnsureFallbackKeysDoNotConflict<T>(
            T? byCode,
            T? byBarcode,
            string entityName
        ) where T : class
        {
            if (byCode == null || byBarcode == null || ReferenceEquals(byCode, byBarcode))
                return;

            throw new InvalidOperationException($"{entityName}业务键冲突：商品编号和条码匹配到不同记录");
        }

        private static void EnsureExpectedAffectedRows(
            int affectedRows,
            int expectedRows,
            string operationName
        )
        {
            if (affectedRows == expectedRows)
                return;

            throw new InvalidOperationException(
                $"{operationName}影响行数异常：预期{expectedRows}行，实际影响{affectedRows}行"
            );
        }

        private static string[] BuildHqProductSetCodeUpdateColumns()
        {
            return
            [
                nameof(DIC_一品多码表.HGUID),
                nameof(DIC_一品多码表.H商品编码),
                nameof(DIC_一品多码表.H多码商品编号),
                nameof(DIC_一品多码表.H供应商编码),
                nameof(DIC_一品多码表.H主条形码),
                nameof(DIC_一品多码表.H多条形码),
                nameof(DIC_一品多码表.H进货价),
                nameof(DIC_一品多码表.H一品多码零售价),
                nameof(DIC_一品多码表.H使用状态),
                nameof(DIC_一品多码表.H是否自动定价),
                nameof(DIC_一品多码表.FGC_LastModifier),
                nameof(DIC_一品多码表.FGC_LastModifyDate),
                nameof(DIC_一品多码表.FGC_UpdateHelp),
            ];
        }

        private static string[] BuildHqStoreMultiCodeUpdateColumns()
        {
            return
            [
                nameof(DIC_分店一品多码表.HGUID),
                nameof(DIC_分店一品多码表.H分店代码),
                nameof(DIC_分店一品多码表.H商品编码),
                nameof(DIC_分店一品多码表.H分店商品编码),
                nameof(DIC_分店一品多码表.H多码商品编码),
                nameof(DIC_分店一品多码表.H分店多码商品编码),
                nameof(DIC_分店一品多码表.H供应商编码),
                nameof(DIC_分店一品多码表.H主条形码),
                nameof(DIC_分店一品多码表.H多条形码),
                nameof(DIC_分店一品多码表.H进货价),
                nameof(DIC_分店一品多码表.H折扣率),
                nameof(DIC_分店一品多码表.H一品多码零售价),
                nameof(DIC_分店一品多码表.H自动新价格),
                nameof(DIC_分店一品多码表.H是否自动定价),
                nameof(DIC_分店一品多码表.H是否特殊商品),
                nameof(DIC_分店一品多码表.H使用状态),
                nameof(DIC_分店一品多码表.FGC_LastModifier),
                nameof(DIC_分店一品多码表.FGC_LastModifyDate),
            ];
        }

        private async Task UpsertHqStorePricesAsync(
            StoreLocalSupplierInvoiceDetails detail,
            Product product,
            List<string> storeCodes,
            string updatedBy,
            EnsureHqProductsResult result
        )
        {
            var hqDb = _hqContext.Db;
            var productCode = product.ProductCode!;
            var now = DateTime.UtcNow;
            var existingPrices = await hqDb.Queryable<DIC_商品零售价表>()
                .Where(x => storeCodes.Contains(x.H分店代码) && x.H商品编码 == productCode)
                .ToListAsync();
            var existingByStore = existingPrices.ToDictionary(x => x.H分店代码, x => x);

            foreach (var storeCode in storeCodes)
            {
                try
                {
                    if (existingByStore.TryGetValue(storeCode, out var existing))
                    {
                        existing.H分店商品编码 = BuildStoreProductCode(storeCode, productCode);
                        existing.H供应商编码 = product.LocalSupplierCode ?? detail.SupplierCode ?? string.Empty;
                        existing.H分店供应商编码 = BuildStoreSupplierCode(
                            storeCode,
                            product.LocalSupplierCode ?? detail.SupplierCode
                        );
                        existing.H进货价 = detail.PurchasePrice ?? product.PurchasePrice ?? 0;
                        existing.H分店零售价 = ResolveRetailPrice(detail);
                        existing.H折扣率 = detail.DiscountRate ?? existing.H折扣率;
                        existing.H使用状态 = true;
                        existing.H是否自动定价 = detail.AutoPricing ?? existing.H是否自动定价;
                        existing.H是否特殊商品 = detail.IsSpecialProduct ?? existing.H是否特殊商品;
                        existing.FGC_LastModifier = updatedBy;
                        existing.FGC_LastModifyDate = now;
                        await hqDb.Updateable(existing).ExecuteCommandAsync();
                        result.HqPurchasePricesUpdated++;
                        continue;
                    }

                    await hqDb.Insertable(new DIC_商品零售价表
                    {
                        HGUID = UuidHelper.GenerateUuid7(),
                        H分店代码 = storeCode,
                        H商品编码 = productCode,
                        H分店商品编码 = BuildStoreProductCode(storeCode, productCode),
                        H供应商编码 = product.LocalSupplierCode ?? detail.SupplierCode ?? string.Empty,
                        H分店供应商编码 = BuildStoreSupplierCode(
                            storeCode,
                            product.LocalSupplierCode ?? detail.SupplierCode
                        ),
                        H进货价 = detail.PurchasePrice ?? product.PurchasePrice ?? 0,
                        H分店零售价 = ResolveRetailPrice(detail),
                        H库存 = 0,
                        H库存金额 = 0,
                        H库存预警数 = 0,
                        H商品缺货日期 = DateTime.MinValue,
                        H是否缺货状态 = false,
                        H最小订货量 = 0,
                        H最小订货量合计金额 = 0,
                        H活动类型 = string.Empty,
                        H满减活动代码 = string.Empty,
                        H活动开始日期 = DateTime.MinValue,
                        H活动结束日期 = DateTime.MinValue,
                        H折扣率 = detail.DiscountRate ?? 0,
                        H满减数量 = 0,
                        H满减金额 = 0,
                        H多码数量 = 0,
                        H使用状态 = true,
                        H是否自动定价 = detail.AutoPricing ?? false,
                        H自动新价格 = detail.NewAutoRetailPrice ?? 0,
                        H盘点入库记录数 = 0,
                        H是否特殊商品 = detail.IsSpecialProduct ?? false,
                        H动态销售数量 = 0,
                        H动态销售额 = 0,
                        H动态成本 = 0,
                        H动态毛利 = 0,
                        H动态毛利率 = 0,
                        H动态销售占比 = 0,
                        FGC_Creator = updatedBy,
                        FGC_CreateDate = now,
                        FGC_LastModifier = updatedBy,
                        FGC_LastModifyDate = now,
                    }).IgnoreColumns(x => x.ID).ExecuteCommandAsync();
                    result.HqPurchasePricesUpdated++;
                }
                catch (Exception ex)
                {
                    AddError(result, detail.DetailGUID, storeCode, $"同步HQ分店价格失败：{ex.Message}");
                }
            }
        }

        private static DIC_一品多码表? FindExistingHqProductSetCode(
            ProductSetCode productSetCode,
            Dictionary<string, DIC_一品多码表> hqProductSetByCode,
            Dictionary<string, DIC_一品多码表> hqProductSetByBarcode
        )
        {
            DIC_一品多码表? byCode = null;
            if (!string.IsNullOrWhiteSpace(productSetCode.SetProductCode))
                hqProductSetByCode.TryGetValue(productSetCode.SetProductCode, out byCode);
            var barcodeKey = NormalizeCaseInsensitiveValue(productSetCode.SetBarcode);
            DIC_一品多码表? byBarcode = null;
            if (barcodeKey != null)
                hqProductSetByBarcode.TryGetValue(barcodeKey, out byBarcode);

            EnsureFallbackKeysDoNotConflict(byCode, byBarcode, "HQ商品多码");
            return byCode ?? byBarcode;
        }

        private static StoreMultiCodeProduct? FindLocalStoreMultiCode(
            string storeCode,
            string productCode,
            ProductSetCode productSetCode,
            Dictionary<string, StoreMultiCodeProduct> storeMultiByCode,
            Dictionary<string, StoreMultiCodeProduct> storeMultiByBarcode
        )
        {
            if (!string.IsNullOrWhiteSpace(productSetCode.SetProductCode))
            {
                var codeKey = BuildHqStoreMultiCodeKey(
                    storeCode,
                    productCode,
                    productSetCode.SetProductCode
                );
                if (storeMultiByCode.TryGetValue(codeKey, out var byCode))
                    return byCode;
            }

            var barcodeKey = NormalizeCaseInsensitiveValue(productSetCode.SetBarcode);
            if (barcodeKey == null)
                return null;

            return storeMultiByBarcode.TryGetValue(
                BuildHqStoreMultiCodeKey(storeCode, productCode, barcodeKey),
                out var byBarcode
            )
                ? byBarcode
                : null;
        }

        private static DIC_分店一品多码表? FindExistingHqStoreMultiCode(
            string storeCode,
            string productCode,
            ProductSetCode productSetCode,
            Dictionary<string, DIC_分店一品多码表> hqStoreByCode,
            Dictionary<string, DIC_分店一品多码表> hqStoreByBarcode
        )
        {
            DIC_分店一品多码表? byCode = null;
            if (!string.IsNullOrWhiteSpace(productSetCode.SetProductCode))
            {
                var codeKey = BuildHqStoreMultiCodeKey(
                    storeCode,
                    productCode,
                    productSetCode.SetProductCode
                );
                hqStoreByCode.TryGetValue(codeKey, out byCode);
            }

            var barcodeKey = NormalizeCaseInsensitiveValue(productSetCode.SetBarcode);
            DIC_分店一品多码表? byBarcode = null;
            if (barcodeKey != null)
            {
                hqStoreByBarcode.TryGetValue(
                    BuildHqStoreMultiCodeKey(storeCode, productCode, barcodeKey),
                    out byBarcode
                );
            }

            EnsureFallbackKeysDoNotConflict(byCode, byBarcode, "HQ分店多码");
            return byCode ?? byBarcode;
        }

        private static DIC_一品多码表 BuildHqProductSetCode(
            PreparedSyncItem item,
            ProductSetCode productSetCode,
            string updatedBy,
            DateTime now
        )
        {
            var detail = item.Detail;
            var product = item.Product;
            return new DIC_一品多码表
            {
                HGUID = productSetCode.SetCodeId,
                H商品编码 = product.ProductCode ?? string.Empty,
                H多码商品编号 = productSetCode.SetProductCode ?? string.Empty,
                H供应商编码 = product.LocalSupplierCode ?? detail.SupplierCode ?? string.Empty,
                H主条形码 = product.Barcode ?? detail.Barcode ?? string.Empty,
                H多条形码 = productSetCode.SetBarcode ?? string.Empty,
                // 全局多码成本仅取已回算的关系表，不允许回退商品主档或进货单明细成本。
                H进货价 = productSetCode.SetPurchasePrice ?? 0,
                H一品多码零售价 =
                    productSetCode.SetRetailPrice ?? product.RetailPrice ?? ResolveRetailPrice(detail),
                H使用状态 = productSetCode.IsActive,
                H是否自动定价 = product.IsAutoPricing,
                FGC_Creator = updatedBy,
                FGC_CreateDate = now,
                FGC_LastModifier = updatedBy,
                FGC_LastModifyDate = now,
                FGC_UpdateHelp = "分店进货单更新HQ商品同步",
            };
        }

        private static DIC_分店一品多码表 BuildHqStoreMultiCode(
            PreparedSyncItem item,
            ProductSetCode productSetCode,
            StoreMultiCodeProduct? storeMultiCode,
            string storeCode,
            string updatedBy,
            DateTime now
        )
        {
            var detail = item.Detail;
            var product = item.Product;
            var productCode = product.ProductCode ?? string.Empty;
            var multiCodeProductCode =
                storeMultiCode?.MultiCodeProductCode
                ?? productSetCode.SetProductCode
                ?? UuidHelper.GenerateUuid7();
            return new DIC_分店一品多码表
            {
                HGUID = storeMultiCode?.UUID ?? UuidHelper.GenerateUuid7(),
                H分店代码 = storeCode,
                H商品编码 = productCode,
                H分店商品编码 = BuildStoreProductCode(storeCode, productCode),
                H多码商品编码 = multiCodeProductCode,
                H分店多码商品编码 =
                    storeMultiCode?.StoreMultiCodeProductCode ?? storeCode + multiCodeProductCode,
                H供应商编码 = product.LocalSupplierCode ?? detail.SupplierCode ?? string.Empty,
                H主条形码 = product.Barcode ?? detail.Barcode ?? string.Empty,
                H多条形码 = storeMultiCode?.MultiBarcode ?? productSetCode.SetBarcode ?? string.Empty,
                // 门店多码成本仅取已回算的门店投影，缺值不能用其他层级成本掩盖数据不一致。
                H进货价 = storeMultiCode?.PurchasePrice ?? 0,
                H折扣率 = storeMultiCode?.DiscountRate ?? detail.DiscountRate ?? 0,
                H一品多码零售价 =
                    storeMultiCode?.MultiCodeRetailPrice
                    ?? productSetCode.SetRetailPrice
                    ?? product.RetailPrice
                    ?? ResolveRetailPrice(detail),
                H库存 = 0,
                H库存金额 = 0,
                H自动新价格 = detail.NewAutoRetailPrice ?? 0,
                H库存预警数 = 0,
                H商品缺货日期 = DateTime.MinValue,
                H是否缺货状态 = false,
                H最小订货量 = 0,
                H最小订货量合计金额 = 0,
                H活动类型 = string.Empty,
                H满减活动代码 = string.Empty,
                H活动开始日期 = DateTime.MinValue,
                H活动结束日期 = DateTime.MinValue,
                H满减数量 = 0,
                H满减金额 = 0,
                H是否自动定价 = storeMultiCode?.IsAutoPricing ?? product.IsAutoPricing,
                H是否特殊商品 = storeMultiCode?.IsSpecialProduct ?? product.IsSpecialProduct,
                H商品柜组号 = string.Empty,
                H使用状态 = storeMultiCode?.IsActive ?? productSetCode.IsActive,
                H动态销售数量 = 0,
                H动态销售额 = 0,
                H动态成本 = 0,
                H动态毛利 = 0,
                H动态毛利率 = 0,
                H动态销售占比 = 0,
                FGC_Creator = updatedBy,
                FGC_CreateDate = now,
                FGC_LastModifier = updatedBy,
                FGC_LastModifyDate = now,
            };
        }

        private static void ApplyHqProductSetCode(
            DIC_一品多码表 existing,
            DIC_一品多码表 mapped
        )
        {
            existing.HGUID = string.IsNullOrWhiteSpace(existing.HGUID) ? mapped.HGUID : existing.HGUID;
            existing.H商品编码 = mapped.H商品编码;
            existing.H多码商品编号 = mapped.H多码商品编号;
            existing.H供应商编码 = mapped.H供应商编码;
            existing.H主条形码 = mapped.H主条形码;
            existing.H多条形码 = mapped.H多条形码;
            existing.H进货价 = mapped.H进货价;
            existing.H一品多码零售价 = mapped.H一品多码零售价;
            existing.H使用状态 = mapped.H使用状态;
            existing.H是否自动定价 = mapped.H是否自动定价;
            existing.FGC_LastModifier = mapped.FGC_LastModifier;
            existing.FGC_LastModifyDate = mapped.FGC_LastModifyDate;
            existing.FGC_UpdateHelp = mapped.FGC_UpdateHelp;
        }

        private static void ApplyHqStoreMultiCode(
            DIC_分店一品多码表 existing,
            DIC_分店一品多码表 mapped
        )
        {
            var existingStoreMultiCode = existing.H分店多码商品编码;
            existing.HGUID = string.IsNullOrWhiteSpace(existing.HGUID) ? mapped.HGUID : existing.HGUID;
            existing.H分店代码 = mapped.H分店代码;
            existing.H商品编码 = mapped.H商品编码;
            existing.H分店商品编码 = mapped.H分店商品编码;
            existing.H多码商品编码 = mapped.H多码商品编码;
            existing.H分店多码商品编码 = string.IsNullOrWhiteSpace(existingStoreMultiCode)
                ? mapped.H分店多码商品编码
                : existingStoreMultiCode;
            existing.H供应商编码 = mapped.H供应商编码;
            existing.H主条形码 = mapped.H主条形码;
            existing.H多条形码 = mapped.H多条形码;
            existing.H进货价 = mapped.H进货价;
            existing.H折扣率 = mapped.H折扣率;
            existing.H一品多码零售价 = mapped.H一品多码零售价;
            existing.H自动新价格 = mapped.H自动新价格;
            existing.H是否自动定价 = mapped.H是否自动定价;
            existing.H是否特殊商品 = mapped.H是否特殊商品;
            existing.H使用状态 = mapped.H使用状态;
            existing.FGC_LastModifier = mapped.FGC_LastModifier;
            existing.FGC_LastModifyDate = mapped.FGC_LastModifyDate;
        }

        private static string BuildHqStoreMultiCodeKey(
            string? storeCode,
            string? productCode,
            string? multiCodeOrBarcode
        )
        {
            return string.Join(
                "\u001f",
                NormalizeCaseInsensitiveValue(storeCode) ?? string.Empty,
                NormalizeCaseInsensitiveValue(productCode) ?? string.Empty,
                NormalizeCaseInsensitiveValue(multiCodeOrBarcode) ?? string.Empty
            );
        }

        private static DIC_商品零售价表 BuildHqStorePrice(
            StoreLocalSupplierInvoiceDetails detail,
            Product product,
            string storeCode,
            string updatedBy,
            DateTime now,
            string? hqProductCode = null
        )
        {
            var productCode = hqProductCode ?? product.ProductCode!;
            return new DIC_商品零售价表
            {
                HGUID = UuidHelper.GenerateUuid7(),
                H分店代码 = storeCode,
                H商品编码 = productCode,
                H分店商品编码 = BuildStoreProductCode(storeCode, productCode),
                H供应商编码 = product.LocalSupplierCode ?? detail.SupplierCode ?? string.Empty,
                H分店供应商编码 = BuildStoreSupplierCode(
                    storeCode,
                    product.LocalSupplierCode ?? detail.SupplierCode
                ),
                H进货价 = detail.PurchasePrice ?? product.PurchasePrice ?? 0,
                H分店零售价 = ResolveRetailPrice(detail),
                H库存 = 0,
                H库存金额 = 0,
                H库存预警数 = 0,
                H商品缺货日期 = DateTime.MinValue,
                H是否缺货状态 = false,
                H最小订货量 = 0,
                H最小订货量合计金额 = 0,
                H活动类型 = string.Empty,
                H满减活动代码 = string.Empty,
                H活动开始日期 = DateTime.MinValue,
                H活动结束日期 = DateTime.MinValue,
                H折扣率 = detail.DiscountRate ?? 0,
                H满减数量 = 0,
                H满减金额 = 0,
                H多码数量 = 0,
                H使用状态 = true,
                H是否自动定价 = detail.AutoPricing ?? false,
                H自动新价格 = detail.NewAutoRetailPrice ?? 0,
                H盘点入库记录数 = 0,
                H是否特殊商品 = detail.IsSpecialProduct ?? false,
                H动态销售数量 = 0,
                H动态销售额 = 0,
                H动态成本 = 0,
                H动态毛利 = 0,
                H动态毛利率 = 0,
                H动态销售占比 = 0,
                FGC_Creator = updatedBy,
                FGC_CreateDate = now,
                FGC_LastModifier = updatedBy,
                FGC_LastModifyDate = now,
            };
        }

        private static bool ApplySelectedHqFields(
            DIC_商品零售价表 price,
            StoreLocalSupplierInvoiceDetails detail,
            Product product,
            UpdateToStorePricesFields updateFields,
            UpdateHqProductsResult result,
            string storeCode,
            out HqFieldUpdateCounts counts
        )
        {
            var updated = false;
            var skippedFields = new List<string>();
            var purchasePriceUpdated = 0;
            var retailPriceUpdated = 0;
            var autoPricingUpdated = 0;
            var specialProductUpdated = 0;
            var discountRateUpdated = 0;
            if (updateFields.UpdatePurchasePrice)
            {
                var value = ResolvePurchasePriceForUpdate(detail, product, updateFields);
                if (IsPositiveValue(value))
                {
                    price.H进货价 = value!.Value;
                    purchasePriceUpdated = 1;
                    updated = true;
                }
                else
                {
                    skippedFields.Add("进货价为空或为0");
                }
            }

            if (updateFields.UpdateRetailPrice)
            {
                var value = ResolveRetailPriceForUpdate(detail, updateFields);
                if (IsPositiveValue(value))
                {
                    price.H分店零售价 = value!.Value;
                    retailPriceUpdated = 1;
                    updated = true;
                }
                else
                {
                    skippedFields.Add("零售价为空或为0");
                }
            }

            if (updateFields.UpdateIsAutoPricing)
            {
                var value = ResolveAutoPricingForUpdate(detail, updateFields);
                if (value.HasValue)
                {
                    price.H是否自动定价 = value.Value;
                    autoPricingUpdated = 1;
                    updated = true;
                }
                else
                {
                    skippedFields.Add("自动定价为空");
                }
            }

            if (updateFields.UpdateIsSpecialProduct)
            {
                var value = ResolveSpecialProductForUpdate(detail, updateFields);
                if (value.HasValue)
                {
                    price.H是否特殊商品 = value.Value;
                    specialProductUpdated = 1;
                    updated = true;
                }
                else
                {
                    skippedFields.Add("特殊商品为空");
                }
            }

            if (updateFields.UpdateDiscountRate)
            {
                var value = ResolveDiscountRateForUpdate(detail, updateFields);
                if (IsPositiveValue(value))
                {
                    price.H折扣率 = value!.Value;
                    discountRateUpdated = 1;
                    updated = true;
                }
                else
                {
                    skippedFields.Add("折扣率为空或为0");
                }
            }

            if (!updated)
            {
                AddSkipped(result, detail.DetailGUID, storeCode, string.Join("，", skippedFields));
            }

            counts = new HqFieldUpdateCounts(
                purchasePriceUpdated,
                retailPriceUpdated,
                autoPricingUpdated,
                specialProductUpdated,
                discountRateUpdated
            );
            return updated;
        }

        private static bool ApplyAllHqFieldsForInsert(
            DIC_商品零售价表 price,
            StoreLocalSupplierInvoiceDetails detail,
            Product product,
            UpdateToStorePricesFields updateFields,
            UpdateHqProductsResult result,
            string storeCode,
            out HqFieldUpdateCounts counts
        )
        {
            // 新插入价格行没有旧值可保留，使用本次更新字段优先补齐整条HQ分店价格记录。
            return ApplySelectedHqFields(
                price,
                detail,
                product,
                updateFields,
                result,
                storeCode,
                out counts
            );
        }

        private static bool HasAnyUpdateField(UpdateToStorePricesFields updateFields)
        {
            return updateFields.UpdatePurchasePrice
                || updateFields.UpdateRetailPrice
                || updateFields.UpdateIsAutoPricing
                || updateFields.UpdateIsSpecialProduct
                || updateFields.UpdateDiscountRate;
        }

        private static decimal? ResolvePurchasePriceForUpdate(
            StoreLocalSupplierInvoiceDetails detail,
            Product product,
            UpdateToStorePricesFields updateFields
        )
        {
            return NormalizePositiveValue(updateFields.PurchasePrice ?? detail.PurchasePrice ?? product.PurchasePrice);
        }

        private static decimal? ResolveRetailPriceForUpdate(
            StoreLocalSupplierInvoiceDetails detail,
            UpdateToStorePricesFields updateFields
        )
        {
            return NormalizePositiveValue(updateFields.RetailPrice ?? detail.RetailPrice ?? detail.NewAutoRetailPrice);
        }

        private static bool? ResolveAutoPricingForUpdate(
            StoreLocalSupplierInvoiceDetails detail,
            UpdateToStorePricesFields updateFields
        )
        {
            return updateFields.IsAutoPricing ?? detail.AutoPricing ?? false;
        }

        private static bool? ResolveSpecialProductForUpdate(
            StoreLocalSupplierInvoiceDetails detail,
            UpdateToStorePricesFields updateFields
        )
        {
            return updateFields.IsSpecialProduct ?? detail.IsSpecialProduct;
        }

        private static decimal? ResolveDiscountRateForUpdate(
            StoreLocalSupplierInvoiceDetails detail,
            UpdateToStorePricesFields updateFields
        )
        {
            return NormalizePositiveValue(updateFields.DiscountRate ?? detail.DiscountRate);
        }

        private static decimal? NormalizePositiveValue(decimal? value)
        {
            return IsPositiveValue(value) ? value : null;
        }

        private static bool IsPositiveValue(decimal? value)
        {
            return value.HasValue && value.Value > 0;
        }

        private static decimal ResolvePurchasePriceForInsert(
            StoreLocalSupplierInvoiceDetails detail,
            Product product,
            UpdateToStorePricesFields updateFields
        )
        {
            return updateFields.PurchasePrice ?? detail.PurchasePrice ?? product.PurchasePrice ?? 0;
        }

        private static decimal ResolveRetailPriceForInsert(
            StoreLocalSupplierInvoiceDetails detail,
            UpdateToStorePricesFields updateFields
        )
        {
            return updateFields.RetailPrice ?? ResolveRetailPrice(detail);
        }

        private static bool ResolveAutoPricingForInsert(
            StoreLocalSupplierInvoiceDetails detail,
            UpdateToStorePricesFields updateFields
        )
        {
            return updateFields.IsAutoPricing ?? detail.AutoPricing ?? false;
        }

        private static bool ResolveSpecialProductForInsert(
            StoreLocalSupplierInvoiceDetails detail,
            UpdateToStorePricesFields updateFields
        )
        {
            return updateFields.IsSpecialProduct ?? detail.IsSpecialProduct ?? false;
        }

        private static decimal ResolveDiscountRateForInsert(
            StoreLocalSupplierInvoiceDetails detail,
            UpdateToStorePricesFields updateFields
        )
        {
            return updateFields.DiscountRate ?? detail.DiscountRate ?? 0;
        }

        private static decimal ResolveRetailPrice(StoreLocalSupplierInvoiceDetails detail)
        {
            if (detail.AutoPricing == true && detail.NewAutoRetailPrice.GetValueOrDefault() > 0)
                return detail.NewAutoRetailPrice!.Value;
            if (detail.RetailPrice.GetValueOrDefault() > 0)
                return detail.RetailPrice!.Value;
            if (detail.NewAutoRetailPrice.GetValueOrDefault() > 0)
                return detail.NewAutoRetailPrice!.Value;
            return (detail.PurchasePrice ?? 0) * 2.5m;
        }

        private static string BuildStoreProductCode(string? storeCode, string productCode)
        {
            return $"{storeCode ?? string.Empty}{productCode}";
        }

        private static string BuildStorePriceKey(string? storeCode, string? productCode)
        {
            return string.Join(
                "\u001f",
                NormalizeCaseInsensitiveValue(storeCode) ?? string.Empty,
                NormalizeCaseInsensitiveValue(productCode) ?? string.Empty
            );
        }

        private static string BuildStoreSupplierCode(string storeCode, string? supplierCode)
        {
            return $"{storeCode}{supplierCode ?? string.Empty}";
        }

        private static string? NormalizeCaseInsensitiveValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return value.Trim().ToUpperInvariant();
        }

        private static string? NormalizeCode(string? value)
        {
            var normalized = value?.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static async Task<List<PreparedSyncItem>> AttachDomesticSupplierCodesAsync(
            ISqlSugarClient db,
            List<PreparedSyncItem> items
        )
        {
            if (items.Count == 0)
                return items;

            var supplierCodes = await ResolveDomesticSupplierCodesAsync(
                db,
                items.Select(item => item.Product.ProductCode)
            );

            return items
                .Select(item =>
                {
                    var productCode = NormalizeCode(item.Product.ProductCode);
                    // CBP 供应商编码只认国内商品主档供应商，批量预取避免明细循环内反复查库。
                    string? domesticSupplierCode = null;
                    if (productCode != null)
                    {
                        supplierCodes.TryGetValue(productCode, out domesticSupplierCode);
                    }

                    return item with { DomesticSupplierCode = domesticSupplierCode };
                })
                .ToList();
        }

        private static async Task<Dictionary<string, string>> ResolveDomesticSupplierCodesAsync(
            ISqlSugarClient db,
            IEnumerable<string?> productCodes
        )
        {
            var normalizedProductCodes = productCodes
                .Select(NormalizeCode)
                .OfType<string>()
                .Distinct()
                .ToList();
            if (normalizedProductCodes.Count == 0)
                return new Dictionary<string, string>();

            var products = await db.Queryable<DomesticProduct>()
                .Where(product =>
                    normalizedProductCodes.Contains(product.ProductCode)
                    && product.IsDeleted == false
                )
                .Select(product => new { product.ProductCode, product.SupplierCode })
                .ToListAsync();

            return products
                .Select(product => new
                {
                    ProductCode = NormalizeCode(product.ProductCode),
                    SupplierCode = NormalizeCode(product.SupplierCode),
                })
                .Where(product => product.ProductCode != null && product.SupplierCode != null)
                .GroupBy(product => product.ProductCode!)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().SupplierCode!
                );
        }

        private static async Task PatchHqCbpSupplierCodeIfNeededAsync(
            ISqlSugarClient hqDb,
            DIC_商品信息字典表 hqProduct,
            string? domesticSupplierCode,
            string updatedBy,
            DateTime now
        )
        {
            if (
                string.IsNullOrWhiteSpace(domesticSupplierCode)
                || !ShouldPatchHqCbpSupplierCode(hqProduct.CBP供应商编码)
            )
            {
                return;
            }

            await hqDb.Updateable<DIC_商品信息字典表>()
                .SetColumns(row => new DIC_商品信息字典表
                {
                    // CBP 供应商编码对应国内供应商，不能沿用本地默认供应商 200。
                    CBP供应商编码 = domesticSupplierCode,
                    FGC_LastModifier = updatedBy,
                    FGC_LastModifyDate = now,
                })
                .Where(row => row.ID == hqProduct.ID)
                .ExecuteCommandAsync();
        }

        private static bool ShouldPatchHqCbpSupplierCode(string? value)
        {
            var normalized = NormalizeCode(value);
            return normalized == null || normalized == "200";
        }

        private static ISugarQueryable<Product> ApplySupplierFilter(
            ISugarQueryable<Product> query,
            string? supplierCode
        )
        {
            if (string.IsNullOrWhiteSpace(supplierCode))
                return query;

            return query.Where(product => product.LocalSupplierCode == supplierCode);
        }

        private static ISugarQueryable<Product> ApplyCaseInsensitiveCodeFilter(
            ISugarQueryable<Product> query,
            string? itemNumber,
            string? barcode
        )
        {
            var normalizedItemNumber = NormalizeCaseInsensitiveValue(itemNumber);
            var normalizedBarcode = NormalizeCaseInsensitiveValue(barcode);

            if (normalizedItemNumber != null && normalizedBarcode != null)
            {
                return query.Where(product =>
                    SqlFunc.ToUpper(product.ItemNumber) == normalizedItemNumber
                    || SqlFunc.ToUpper(product.Barcode) == normalizedBarcode
                );
            }

            if (normalizedItemNumber != null)
            {
                return query.Where(product =>
                    SqlFunc.ToUpper(product.ItemNumber) == normalizedItemNumber
                );
            }

            return query.Where(product => SqlFunc.ToUpper(product.Barcode) == normalizedBarcode);
        }

        private static async Task<Product?> FindExistingProductAsync(
            ISqlSugarClient db,
            string? productCode,
            string? supplierCode,
            string? itemNumber,
            string? barcode
        )
        {
            if (!string.IsNullOrWhiteSpace(productCode))
            {
                var productByCode = await db.Queryable<Product>()
                    .Where(product => product.ProductCode == productCode && product.IsDeleted == false)
                    .FirstAsync();
                if (productByCode != null)
                    return productByCode;
            }

            if (string.IsNullOrWhiteSpace(itemNumber) && string.IsNullOrWhiteSpace(barcode))
                return null;

            var productQuery = ApplySupplierFilter(
                db.Queryable<Product>().Where(product => product.IsDeleted == false),
                supplierCode
            );

            productQuery = ApplyCaseInsensitiveCodeFilter(productQuery, itemNumber, barcode);
            return await productQuery.FirstAsync();
        }

        private static async Task<DIC_商品信息字典表?> FindExistingHqProductAsync(
            ISqlSugarClient hqDb,
            string? productCode,
            string? supplierCode,
            string? itemNumber,
            string? barcode
        )
        {
            if (!string.IsNullOrWhiteSpace(productCode))
            {
                var productByCode = await hqDb.Queryable<DIC_商品信息字典表>()
                    .Where(product => product.H商品编码 == productCode)
                    .FirstAsync();
                if (productByCode != null)
                    return productByCode;
            }

            if (string.IsNullOrWhiteSpace(itemNumber) && string.IsNullOrWhiteSpace(barcode))
                return null;

            var normalizedItemNumber = NormalizeCaseInsensitiveValue(itemNumber);
            var normalizedBarcode = NormalizeCaseInsensitiveValue(barcode);
            var query = hqDb.Queryable<DIC_商品信息字典表>();

            if (!string.IsNullOrWhiteSpace(supplierCode))
                query = query.Where(product => product.H供货商编码 == supplierCode);

            // HQ 商品编码可能与本地编码不同，插入前按业务唯一字段兜底避免大小写重复。
            if (normalizedItemNumber != null && normalizedBarcode != null)
            {
                query = query.Where(product =>
                    SqlFunc.ToUpper(product.H货号) == normalizedItemNumber
                    || SqlFunc.ToUpper(product.H主条形码) == normalizedBarcode
                );
            }
            else if (normalizedItemNumber != null)
            {
                query = query.Where(product => SqlFunc.ToUpper(product.H货号) == normalizedItemNumber);
            }
            else
            {
                query = query.Where(product => SqlFunc.ToUpper(product.H主条形码) == normalizedBarcode);
            }

            return await query.FirstAsync();
        }

        private static void AddError(
            EnsureHqProductsResult result,
            string detailGuid,
            string? storeCode,
            string message
        )
        {
            result.Failed++;
            result.Errors.Add(new EnsureHqProductError
            {
                DetailGuid = detailGuid,
                StoreCode = storeCode,
                Message = message,
            });
        }

        private static void AddSkipped(
            EnsureHqProductsResult result,
            string detailGuid,
            string? storeCode,
            string message
        )
        {
            result.Skipped++;
            result.Errors.Add(new EnsureHqProductError
            {
                DetailGuid = detailGuid,
                StoreCode = storeCode,
                Message = $"{message}，已跳过",
            });
        }

        private void LogUpdateHqProductsPerformance(
            string invoiceGuid,
            int detailCount,
            int activeStoreCount,
            int targetStoreCount,
            UpdateHqProductsResult result,
            long localPreparationMs,
            long localPriceMs,
            long hqProductAndPriceMs,
            long hqMultiCodeMs,
            long totalMs
        )
        {
            _logger.LogInformation(
                "更新HQ商品性能 InvoiceGuid={InvoiceGuid} DetailCount={DetailCount} ActiveStoreCount={ActiveStoreCount} TargetStoreCount={TargetStoreCount} NewLocalProducts={NewLocalProducts} NewHqProducts={NewHqProducts} UpdatedPriceRows={UpdatedPriceRows} Failed={Failed} LocalPreparationMs={LocalPreparationMs} LocalPriceMs={LocalPriceMs} HqProductAndPriceMs={HqProductAndPriceMs} HqMultiCodeMs={HqMultiCodeMs} TotalMs={TotalMs}",
                invoiceGuid,
                detailCount,
                activeStoreCount,
                targetStoreCount,
                result.HbwebCreated,
                result.HqCreated,
                result.Updated,
                result.Failed,
                localPreparationMs,
                localPriceMs,
                hqProductAndPriceMs,
                hqMultiCodeMs,
                totalMs
            );
        }

        private sealed record PreparedSyncItem(
            StoreLocalSupplierInvoiceDetails Detail,
            Product Product,
            bool IsNewProduct,
            string? DomesticSupplierCode = null
        );

        private sealed record HqStorePriceWrite(
            DIC_商品零售价表 Price,
            string DetailGuid,
            string StoreCode,
            HqFieldUpdateCounts Counts
        );

        private sealed record HqEntityWrite<T>(T Entity, string DetailGuid, string? StoreCode);

        private readonly record struct HqFieldUpdateCounts(
            int PurchasePrice,
            int RetailPrice,
            int AutoPricing,
            int SpecialProduct,
            int DiscountRate
        );

        private enum HqFallbackWriteOutcome
        {
            Created,
            Updated,
        }
    }
}
