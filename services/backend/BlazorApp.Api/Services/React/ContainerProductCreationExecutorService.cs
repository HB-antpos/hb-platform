using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 货柜明细创建新商品执行服务。
    /// </summary>
    public class ContainerProductCreationExecutorService
        : IContainerProductCreationExecutorService
    {
        private readonly SqlSugarContext _context;
        private readonly HBSalesSqlSugarContext _hbSalesContext;
        private readonly IProductWarehouseReactService _productWarehouseService;
        private readonly ILogger<ContainerProductCreationExecutorService> _logger;
        private readonly IWarehouseProductChangeHistoryService _changeHistoryService;
        private const int ProductCreationLockAttemptCount = 6;
        private static readonly TimeSpan ProductCreationLockRetryBudget = TimeSpan.FromSeconds(60);

        public ContainerProductCreationExecutorService(
            SqlSugarContext context,
            HBSalesSqlSugarContext hbSalesContext,
            IProductWarehouseReactService productWarehouseService,
            ILogger<ContainerProductCreationExecutorService> logger,
            IWarehouseProductChangeHistoryService changeHistoryService
        )
        {
            _context = context;
            _hbSalesContext = hbSalesContext;
            _productWarehouseService = productWarehouseService;
            _logger = logger;
            _changeHistoryService = changeHistoryService;
        }

        public async Task<ContainerProductCreationResultDto> ExecuteAsync(
            ContainerProductCreationJobRequestDto request,
            CancellationToken cancellationToken = default
        )
        {
            return await ExecuteAsync(request, null, null, cancellationToken);
        }

        public async Task<ContainerProductCreationResultDto> ExecuteAsync(
            ContainerProductCreationJobRequestDto request,
            string? updatedBy,
            CancellationToken cancellationToken = default
        )
        {
            return await ExecuteAsync(request, null, updatedBy, cancellationToken);
        }

        public async Task<ContainerProductCreationResultDto> ExecuteAsync(
            ContainerProductCreationJobRequestDto request,
            string? actorUserGuid,
            string? updatedBy,
            CancellationToken cancellationToken = default
        )
        {
            // 整柜提交由一个外层事务保证创建、价格和完成状态原子性，不能在中途重试。
            if (request.SubmitContainer)
            {
                return await ExecuteOnceAsync(request, actorUserGuid, updatedBy, cancellationToken);
            }

            var retryStopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (var attempt = 1; attempt <= ProductCreationLockAttemptCount; attempt++)
            {
                try
                {
                    // 每次尝试都从入口重读货柜行并重建候选，禁止复用等锁前的身份快照。
                    return await ExecuteOnceAsync(request, actorUserGuid, updatedBy, cancellationToken);
                }
                catch (RetryableProductCreationLockConflictException conflict)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return AddProductCreationErrors(
                            conflict.Result,
                            conflict.Candidates,
                            "PRODUCT_CREATION_CANCELLED",
                            "商品创建已取消，未重试"
                        );
                    }

                    if (
                        attempt == ProductCreationLockAttemptCount
                        || retryStopwatch.Elapsed >= ProductCreationLockRetryBudget
                    )
                    {
                        return AddProductCreationErrors(
                            conflict.Result,
                            conflict.Candidates,
                            SetChildPurchasePriceMutationLock.BusyErrorCode,
                            "商品创建繁忙，等待其他商品操作超时，本次未创建新商品"
                        );
                    }

                    _logger.LogInformation(
                        conflict,
                        "货柜创建新商品遇到商品锁冲突，已确认回滚后重试: {OperationId}, {Attempt}",
                        request.OperationId,
                        attempt
                    );
                }
            }

            throw new InvalidOperationException("商品创建重试流程未返回结果");
        }

        private async Task<ContainerProductCreationResultDto> ExecuteOnceAsync(
            ContainerProductCreationJobRequestDto request,
            string? actorUserGuid,
            string? updatedBy,
            CancellationToken cancellationToken
        )
        {
            var result = new ContainerProductCreationResultDto();
            var effectiveUpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? "System" : updatedBy.Trim();
            var batchGuid = Guid.NewGuid();
            var isSubmitContainer = request.SubmitContainer;
            var normalizedDetailHguids = NormalizeDetailHguids(request.DetailHguids);

            if (string.IsNullOrWhiteSpace(request.ContainerGuid))
            {
                AddError(result, null, null, null, "MISSING_CONTAINER_GUID", "货柜 GUID 不能为空");
                return FinalizeResult(result);
            }

            if (!isSubmitContainer && normalizedDetailHguids.Count == 0)
            {
                AddError(result, null, null, null, "MISSING_DETAIL_HGUIDS", "明细 GUID 不能为空");
                return FinalizeResult(result);
            }

            var containerGuid = request.ContainerGuid.Trim();
            var submitTransactionStarted = false;
            try
            {
                if (isSubmitContainer)
                {
                    // 整柜提交是一个业务原子动作：创建、更新门店价、完成货柜必须一起提交或一起回滚。
                    await _context.Db.Ado.BeginTranAsync();
                    submitTransactionStarted = true;
                    // 统一锁序为货柜锁 → 商品锁，且整柜来源行必须在持锁事务内重读。
                    await ContainerMutationLock.AcquireContainersAsync(
                        _context.Db,
                        new[] { containerGuid }
                    );
                }

            // 非提交入口只用单条查询取得一致快照，且本执行器从不写 ContainerDetail。
            var rows = await LoadRowsAsync(containerGuid, normalizedDetailHguids, isSubmitContainer);
            if (isSubmitContainer && rows.Count == 0)
            {
                AddError(result, null, null, null, "EMPTY_CONTAINER_DETAILS", "当前货柜没有可提交的明细");
                return CompleteSubmitTransaction(
                    await FinalizeSubmitResultAsync(containerGuid, isSubmitContainer, result),
                    submitTransactionStarted
                );
            }

            var rowsByDetail = rows
                .Where(row => !string.IsNullOrWhiteSpace(row.DetailHguid))
                .GroupBy(row => row.DetailHguid!)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var detailHguid in isSubmitContainer ? new List<string>() : normalizedDetailHguids)
            {
                if (!rowsByDetail.ContainsKey(detailHguid))
                {
                    AddSkipped(result, null, null, detailHguid, "DETAIL_NOT_FOUND", "货柜明细不存在或不属于当前货柜");
                }
            }

            var productCodes = rows
                .Select(row => row.ProductCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            SetChildPurchasePriceLockScope? submitSetChildPurchasePriceLock = null;
            if (isSubmitContainer)
            {
                // 已持有货柜锁，再按稳定商品编码取锁并读取、写入主成本、关系和门店投影。
                submitSetChildPurchasePriceLock = productCodes.Count == 0
                    ? await SetChildPurchasePriceMutationLock.AcquireAllAsync(_context.Db)
                    // 批量建档内部同样获取身份锁；外层不得持 Shared 再升级为 Update，
                    // 否则会与已经等待同一商品身份锁的事务形成反向等待。
                    : await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(
                        _context.Db,
                        productCodes
                    );
            }
            var itemNumbers = rows
                .Select(row => row.ItemNumber)
                .Where(itemNumber => !string.IsNullOrWhiteSpace(itemNumber))
                .Select(itemNumber => itemNumber!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var existingProductCodes = productCodes.Count == 0
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : (
                    await _context.Db.Queryable<Product>()
                        .Where(product => product.ProductCode != null && productCodes.Contains(product.ProductCode))
                        .Select(product => product.ProductCode)
                        .ToListAsync()
                )
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var existingWarehouseProductCodes = productCodes.Count == 0
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : (
                    await _context.Db.Queryable<WarehouseProduct>()
                        .Where(product => product.ProductCode != null && productCodes.Contains(product.ProductCode))
                        .Select(product => product.ProductCode)
                        .ToListAsync()
                )
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var existingItemNumbers = itemNumbers.Count == 0
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : (
                    await _context.Db.Queryable<Product>()
                        .Where(product => product.ItemNumber != null && itemNumbers.Contains(product.ItemNumber))
                        .Select(product => product.ItemNumber)
                        .ToListAsync()
                )
                    .Where(itemNumber => !string.IsNullOrWhiteSpace(itemNumber))
                    .Select(itemNumber => itemNumber!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> beforeSnapshots =
                new Dictionary<string, WarehouseProductChangeSnapshotDto>(StringComparer.OrdinalIgnoreCase);
            if (isSubmitContainer && existingProductCodes.Count > 0)
            {
                beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(existingProductCodes);
            }

            var setRelationsByProductCode = productCodes.Count == 0
                ? new Dictionary<string, List<DomesticSetProduct>>(StringComparer.OrdinalIgnoreCase)
                : (
                    await _context.Db.Queryable<DomesticSetProduct>()
                        .Where(item => productCodes.Contains(item.ProductCode) && !item.IsDeleted)
                        .ToListAsync()
                )
                    .GroupBy(item => item.ProductCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var linkedSetChildDetailHguids = await EnsureSetRelationsFromContainerChildrenAsync(
                request.ContainerGuid.Trim(),
                rows,
                existingProductCodes,
                setRelationsByProductCode
            );

            var createItems = new List<CreateItemDto>();
            var sourceRows = new Dictionary<string, ContainerProductCreationSourceRow>(
                StringComparer.OrdinalIgnoreCase
            );
            var updateItems = new Dictionary<string, ContainerProductUpdateSource>(
                StringComparer.OrdinalIgnoreCase
            );
            var batchProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var batchItemNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                var productCode = row.ProductCode?.Trim();
                if (
                    isSubmitContainer
                    && !string.IsNullOrWhiteSpace(productCode)
                    && existingProductCodes.Contains(productCode)
                )
                {
                    var productType = NormalizeContainerProductType(
                        row.ContainerProductType,
                        row.DomesticProductType
                    );
                    if (productType == ContainerProductCreationProductType.Set)
                    {
                        await TryCompleteExistingSetProductCodesAsync(
                            row,
                            existingProductCodes,
                            setRelationsByProductCode,
                            result,
                            effectiveUpdatedBy,
                            submitSetChildPurchasePriceLock,
                            addResultItem: false
                        );
                    }

                    if (TryBuildUpdateItem(row, result, out var updateItem))
                    {
                        // 同一商品多条明细时以后出现的价格为准，避免重复更新同一商品。
                        updateItems[productCode] = new ContainerProductUpdateSource
                        {
                            Item = updateItem,
                            Row = row,
                        };
                    }
                    continue;
                }

                // 已存在的套装主商品不再按重复商品跳过；仅修正类型并补齐子码链路，不更新其他主档字段或价格。
                if (
                    await TryCompleteExistingSetProductCodesAsync(
                        row,
                        existingProductCodes,
                        setRelationsByProductCode,
                        result,
                        effectiveUpdatedBy,
                        submitSetChildPurchasePriceLock
                    )
                )
                {
                    continue;
                }

                if (!TryBuildCreateItem(
                    row,
                    existingProductCodes,
                    existingWarehouseProductCodes,
                    existingItemNumbers,
                    batchProductCodes,
                    batchItemNumbers,
                    setRelationsByProductCode,
                    linkedSetChildDetailHguids,
                    result,
                    // 仅「创建新商品」任务允许中文名称为空时用英文名称兜底；整柜提交沿用原口径。
                    allowEnglishNameFallback: !isSubmitContainer,
                    out var createItem
                ))
                {
                    continue;
                }

                createItems.Add(createItem);
                if (!string.IsNullOrWhiteSpace(createItem.ProductCode))
                {
                    sourceRows[createItem.ProductCode!] = row;
                }
            }

            if (createItems.Count > 0)
            {
                var normalCreationTransactionStarted = false;
                var normalCreationCommitAttempted = false;
                try
                {
                    if (!isSubmitContainer)
                    {
                        // 普通 job 由本执行器持有整批事务，锁失败时才能明确回滚后安全重试。
                        await _context.Db.Ado.BeginTranAsync();
                        normalCreationTransactionStarted = true;
                    }

                    var batchResult = await _productWarehouseService.BatchCreateAsync(
                        createItems,
                        // 普通 job 已在本方法开始事务；整柜提交沿用既有外层事务。
                        useTransaction: false,
                        // 保留整柜事务边界，同时将 job 捕获的真实操作人写入审计字段。
                        updatedBy: effectiveUpdatedBy,
                        auditSource: "ContainerSubmit",
                        sourceReference: containerGuid,
                        batchGuid: batchGuid,
                        actorUserGuid: actorUserGuid
                    );
                    if (!batchResult.Success)
                    {
                        if (
                            !isSubmitContainer
                            && !await TryRollbackNormalProductCreationTransactionAsync(
                                request.OperationId
                            )
                        )
                        {
                            return AddProductCreationUnknownStateError(result, createItems, sourceRows);
                        }
                        normalCreationTransactionStarted = false;
                        foreach (var error in batchResult.Errors)
                        {
                            AddError(result, null, null, null, "WAREHOUSE_BATCH_FAILED", error);
                        }
                        AddError(result, null, null, null, "WAREHOUSE_BATCH_FAILED", batchResult.Message);
                        return CompleteSubmitTransaction(
                            await FinalizeSubmitResultAsync(containerGuid, isSubmitContainer, result),
                            submitTransactionStarted
                        );
                    }

                    if (!isSubmitContainer)
                    {
                        // Commit 一旦开始便不能断言结果未写入，后续异常绝不进入自动重试。
                        normalCreationCommitAttempted = true;
                        await _context.Db.Ado.CommitTranAsync();
                        normalCreationTransactionStarted = false;
                        normalCreationCommitAttempted = false;
                    }

                    var skippedItemNumbers = batchResult.SkippedItems
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var item in createItems)
                    {
                        if (string.IsNullOrWhiteSpace(item.ProductCode))
                        {
                            continue;
                        }

                        if (skippedItemNumbers.Contains(item.ItemNumber))
                        {
                            continue;
                        }

                        if (sourceRows.TryGetValue(item.ProductCode!, out var row))
                        {
                            result.Created.Add(new ContainerProductCreationResultItemDto
                            {
                                ProductCode = item.ProductCode,
                                ItemNumber = item.ItemNumber,
                                DetailHguid = row.DetailHguid,
                                Message = "创建成功",
                            });
                        }
                    }

                    foreach (var skippedItem in batchResult.SkippedItems)
                    {
                        AddSkipped(result, null, skippedItem, null, "WAREHOUSE_SKIPPED", skippedItem);
                    }

                    foreach (var error in batchResult.Errors)
                    {
                        AddError(result, null, null, null, "WAREHOUSE_BATCH_FAILED", error);
                    }
                }
                catch (Exception ex)
                {
                    if (!isSubmitContainer && normalCreationCommitAttempted)
                    {
                        _logger.LogError(
                            ex,
                            "货柜创建新商品提交状态未知，禁止自动重试: {OperationId}",
                            request.OperationId
                        );
                        return AddProductCreationUnknownStateError(result, createItems, sourceRows);
                    }

                    if (
                        !isSubmitContainer
                        && normalCreationTransactionStarted
                        && !await TryRollbackNormalProductCreationTransactionAsync(request.OperationId)
                    )
                    {
                        return AddProductCreationUnknownStateError(result, createItems, sourceRows);
                    }

                    if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out var lockConflict))
                    {
                        if (
                            !isSubmitContainer
                            && (lockConflict?.ResultCode == -2 || ContainsCancellation(ex))
                        )
                        {
                            return AddProductCreationErrors(
                                result,
                                BuildProductCreationCandidates(createItems, sourceRows),
                                "PRODUCT_CREATION_CANCELLED",
                                "商品创建已取消，未重试"
                            );
                        }

                        if (!isSubmitContainer && IsRetryableProductCreationLockConflict(lockConflict))
                        {
                            throw new RetryableProductCreationLockConflictException(
                                ex,
                                result,
                                BuildProductCreationCandidates(createItems, sourceRows)
                            );
                        }

                        if (!isSubmitContainer)
                        {
                            return AddProductCreationErrors(
                                result,
                                BuildProductCreationCandidates(createItems, sourceRows),
                                "WAREHOUSE_BATCH_EXCEPTION",
                                ex.Message
                            );
                        }

                        throw;
                    }

                    if (ContainerMutationLock.TryResolveConflict(ex, out _))
                    {
                        throw;
                    }

                    _logger.LogError(ex, "货柜创建新商品批量写入失败: {OperationId}", request.OperationId);
                    AddError(result, null, null, null, "WAREHOUSE_BATCH_EXCEPTION", ex.Message);
                }
            }

            if (isSubmitContainer && updateItems.Count > 0)
            {
                await UpdateExistingProductsForSubmitAsync(
                    updateItems.Values.ToList(),
                    effectiveUpdatedBy,
                    result,
                    submitSetChildPurchasePriceLock!
                );
            }

            if (isSubmitContainer && existingProductCodes.Count > 0)
            {
                var afterSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(existingProductCodes);
                await _changeHistoryService.RecordChangesAsync(
                    beforeSnapshots,
                    afterSnapshots,
                    new WarehouseProductChangeHistoryContextDto
                    {
                        Action = "BatchUpdate",
                        Source = "ContainerSubmit",
                        SourceReference = containerGuid,
                        BatchGuid = batchGuid,
                        ActorUserGuid = actorUserGuid,
                        ActorName = effectiveUpdatedBy,
                        ActorType = !string.IsNullOrWhiteSpace(actorUserGuid)
                            || !string.Equals(
                                effectiveUpdatedBy,
                                "System",
                                StringComparison.OrdinalIgnoreCase
                            )
                                ? "User"
                                : "System",
                    }
                );
            }

            return CompleteSubmitTransaction(
                await FinalizeSubmitResultAsync(containerGuid, isSubmitContainer, result),
                submitTransactionStarted
            );
            }
            catch (ContainerSetGroupDataQualityException ex)
            {
                RollbackSubmitTransaction(submitTransactionStarted);
                _logger.LogWarning(ex, "货柜套装关系数据校验失败: {OperationId}", request.OperationId);
                AddError(result, ex.ProductCode, null, null, "SET_GROUP_DATA_QUALITY_ERROR", ex.Message);
                return submitTransactionStarted ? ClearRolledBackWrites(result) : FinalizeResult(result);
            }
            catch (Exception ex) when (isSubmitContainer)
            {
                RollbackSubmitTransaction(submitTransactionStarted);
                _logger.LogError(ex, "整柜提交事务失败: {OperationId}", request.OperationId);
                var reasonCode = ContainerMutationLock.TryResolveConflict(ex, out _)
                    ? ContainerMutationLock.BusyErrorCode
                    : SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
                        ? SetChildPurchasePriceMutationLock.BusyErrorCode
                        : "SUBMIT_CONTAINER_EXCEPTION";
                AddError(result, null, null, null, reasonCode, ex.Message);
                return submitTransactionStarted ? ClearRolledBackWrites(result) : FinalizeResult(result);
            }
        }

        private async Task<List<ContainerProductCreationSourceRow>> LoadRowsAsync(
            string containerGuid,
            List<string> detailHguids,
            bool loadAllContainerDetails
        )
        {
            var query = _context.Db.Queryable<ContainerDetail>()
                .LeftJoin<DomesticProduct>((detail, domestic) => detail.ProductCode == domestic.ProductCode)
                .LeftJoin<Product>((detail, domestic, localProduct) => detail.ProductCode == localProduct.ProductCode)
                .Where((detail, domestic, localProduct) =>
                    detail.ContainerCode == containerGuid
                    && !detail.IsDeleted
                );

            if (!loadAllContainerDetails)
            {
                query = query.Where((detail, domestic, localProduct) =>
                    detailHguids.Contains(detail.DetailCode)
                );
            }

            return await query.Select((detail, domestic, localProduct) => new ContainerProductCreationSourceRow
                {
                    DetailHguid = detail.DetailCode,
                    ProductCode = detail.ProductCode,
                    ContainerProductType = detail.ProductType,
                    MixedGroupCode = detail.MixedGroupCode,
                    SetQuantity = detail.SetQuantity,
                    DomesticPrice = detail.DomesticPrice,
                    ImportPrice = detail.ImportPrice,
                    OEMPrice = detail.OEMPrice,
                    Volume = detail.UnitVolume,
                    ItemNumber = domestic.HBProductNo,
                    ChineseName = domestic.ProductName,
                    EnglishName = domestic.EnglishProductName,
                    Barcode = domestic.Barcode,
                    ImageUrl = domestic.ProductImage,
                    DomesticProductType = domestic.ProductType,
                    WarehouseCategoryGUID = detail.TargetWarehouseCategoryGUID ?? localProduct.WarehouseCategoryGUID,
                })
                .ToListAsync();
        }

        private async Task<bool> TryCompleteExistingSetProductCodesAsync(
            ContainerProductCreationSourceRow row,
            HashSet<string> existingProductCodes,
            Dictionary<string, List<DomesticSetProduct>> setRelationsByProductCode,
            ContainerProductCreationResultDto result,
            string updatedBy,
            SetChildPurchasePriceLockScope? transactionLockScope,
            bool addResultItem = true
        )
        {
            var productCode = row.ProductCode?.Trim();
            var itemNumber = row.ItemNumber?.Trim();
            var productType = NormalizeContainerProductType(row.ContainerProductType, row.DomesticProductType);

            if (
                productType != ContainerProductCreationProductType.Set
                || string.IsNullOrWhiteSpace(productCode)
                || !existingProductCodes.Contains(productCode)
            )
            {
                return false;
            }

            var ownsTransaction = transactionLockScope == null && _context.Db.Ado.Transaction == null;
            if (ownsTransaction)
            {
                // 非整柜补码原先没有事务；单商品关系、投影和成本必须原子完成。
                await _context.Db.Ado.BeginTranAsync();
            }

            try
            {
                var setChildPurchasePriceLock = transactionLockScope
                    ?? await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        _context.Db,
                        new[] { productCode }
                    );
                var productTypeChanged = await EnsureExistingSetProductTypeAsync(productCode, updatedBy);

                // 已存在套装主商品每次按主商品编码实时查子项表，避免继续依赖货柜同组明细或旧缓存。
                var setRelations = await EnsureSetRelationsFromSetChildTableAsync(
                    productCode,
                    itemNumber,
                    setRelationsByProductCode
                );
                if (setRelations.Count == 0)
                {
                    if (addResultItem)
                    {
                        AddSkipped(result, productCode, itemNumber, row.DetailHguid, "SET_CHILD_NOT_FOUND", "未找到套装子项，已跳过");
                    }
                    if (ownsTransaction)
                    {
                        await _context.Db.Ado.CommitTranAsync();
                    }
                    return true;
                }

                // 按 DomesticSetProduct 检查子码三层完整性，缺 ProductSetCode 或分店多码时只补缺失层级。
                var changed = await EnsureProductSetCodesAndStoreMultiCodesAsync(
                    productCode,
                    setRelations,
                    setChildPurchasePriceLock,
                    updatedBy
                );
                if (addResultItem)
                {
                    result.Created.Add(new ContainerProductCreationResultItemDto
                    {
                        ProductCode = productCode,
                        ItemNumber = itemNumber,
                        DetailHguid = row.DetailHguid,
                        Message = changed || productTypeChanged ? "套装子码已补齐" : "套装子码已完整",
                    });
                }
                if (ownsTransaction)
                {
                    await _context.Db.Ado.CommitTranAsync();
                }
                return true;
            }
            catch
            {
                if (ownsTransaction)
                {
                    await _context.Db.Ado.RollbackTranAsync();
                }
                throw;
            }
        }

        private async Task<bool> EnsureExistingSetProductTypeAsync(
            string productCode,
            string updatedBy
        )
        {
            var product = await _context.Db.Queryable<Product>()
                .Where(item => item.ProductCode == productCode && !item.IsDeleted)
                .FirstAsync();
            if (product == null || product.ProductType == 1)
            {
                return false;
            }

            // 已存在套装主商品只修正 POS 商品类型，避免再次创建时改动价格、名称、图片等主档字段。
            product.ProductType = 1;
            product.UpdatedAt = DateTime.Now;
            product.UpdatedBy = updatedBy;
            await _context.Db.Updateable(product)
                .UpdateColumns(item => new { item.ProductType, item.UpdatedAt, item.UpdatedBy })
                .ExecuteCommandAsync();
            return true;
        }

        private async Task<List<DomesticSetProduct>> EnsureSetRelationsFromSetChildTableAsync(
            string productCode,
            string? productNo,
            Dictionary<string, List<DomesticSetProduct>> setRelationsByProductCode
        )
        {
            var localRelations = await LoadLocalSetRelationsAsync(productCode);
            if (localRelations.Count > 0)
            {
                setRelationsByProductCode[productCode] = localRelations;
                return localRelations;
            }

            // 本地子项表为空时，按主商品编码实时从 HBSales 套装子项表拉取并落本地。
            var hqRelations = await LoadSetRelationsFromHqAsync(productCode, productNo);
            if (hqRelations.Count == 0)
            {
                setRelationsByProductCode.Remove(productCode);
                return hqRelations;
            }

            // HQ 原始关系必须整组校验，禁止先过滤空键或合并重复键后再落库。
            ValidateSetRelationGroup(productCode, hqRelations);
            await _context.Db.Insertable(hqRelations).ExecuteCommandAsync();
            setRelationsByProductCode[productCode] = hqRelations;
            return hqRelations;
        }

        private async Task<List<DomesticSetProduct>> LoadLocalSetRelationsAsync(string productCode)
        {
            return await _context.Db.Queryable<DomesticSetProduct>()
                .Where(item => item.ProductCode == productCode && !item.IsDeleted)
                .ToListAsync();
        }

        private async Task<List<DomesticSetProduct>> LoadSetRelationsFromHqAsync(
            string productCode,
            string? productNo
        )
        {
            var hqRows = await _hbSalesContext.Db.Queryable<CPT_DIC_商品套装信息表>()
                .Where(row =>
                    row.商品编码 == productCode
                    && row.使用状态 == 1
                )
                .ToListAsync();

            return hqRows
                .Select(row => MapHqSetRelation(row, productCode, productNo))
                .ToList();
        }

        private static DomesticSetProduct MapHqSetRelation(
            CPT_DIC_商品套装信息表 row,
            string productCode,
            string? productNo
        )
        {
            var setProductCode = new[] { row.商品小货号, row.条形码, row.HGUID }
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?.Trim();
            var setProductNo = new[] { row.商品小货号, row.条形码, setProductCode }
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?.Trim();

            return new DomesticSetProduct
            {
                SetProductCode = setProductCode ?? string.Empty,
                ProductCode = productCode,
                ProductNo = productNo?.Trim(),
                SetProductNo = setProductNo ?? string.Empty,
                SetBarcode = row.条形码?.Trim(),
                DomesticPrice = row.国内价格,
                ImportPrice = row.进口价格,
                OEMPrice = row.贴牌价格,
                Remarks = row.备注?.Trim(),
                IsDeleted = false,
            };
        }

        private async Task<bool> EnsureProductSetCodesAndStoreMultiCodesAsync(
            string productCode,
            List<DomesticSetProduct> setRelations,
            SetChildPurchasePriceLockScope setChildPurchasePriceLock,
            string updatedBy
        )
        {
            var now = DateTime.Now;
            var changed = false;
            ValidateSetRelationGroup(productCode, setRelations);
            var validRelations = setRelations.ToList();

            var setProductCodes = validRelations
                .Select(relation => relation.SetProductCode!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var existingSetCodes = (
                await _context.Db.Queryable<ProductSetCode>()
                    .Where(code => code.ProductCode == productCode)
                    .ToListAsync()
            )
                .Where(code =>
                    setProductCodes.Contains(
                        code.SetProductCode?.Trim() ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                .ToList();
            // 同一业务键只允许一个活跃候选；历史墓碑只读保留，绝不能成为补码 upsert 的目标。
            var existingSetCodeMap = SelectActiveProductSetCodes(productCode, existingSetCodes);
            var productSetCodesToInsert = new List<ProductSetCode>();
            var productSetCodesToUpdate = new List<ProductSetCode>();

            foreach (var relation in validRelations)
            {
                if (existingSetCodeMap.TryGetValue(relation.SetProductCode!, out var existingSetCode))
                {
                    if (ShouldRefreshSetCode(existingSetCode, relation))
                    {
                        // 已有行只刷新关系展示字段和更新审计，身份、生命周期及成本均由原记录保留。
                        ApplyExistingSetCodeValues(existingSetCode, relation, now, updatedBy);
                        productSetCodesToUpdate.Add(existingSetCode);
                        changed = true;
                    }
                    continue;
                }

                productSetCodesToInsert.Add(
                    BuildProductSetCode(productCode, relation, now, updatedBy)
                );
                changed = true;
            }

            if (productSetCodesToInsert.Count > 0)
            {
                await _context.Db.Insertable(productSetCodesToInsert).ExecuteCommandAsync();
            }

            if (productSetCodesToUpdate.Count > 0)
            {
                await _context.Db.Updateable(productSetCodesToUpdate)
                    .UpdateColumns(code => new
                    {
                        code.SetItemNumber,
                        code.SetBarcode,
                        code.SetRetailPrice,
                        code.SetQuantity,
                        code.UpdatedAt,
                        code.UpdatedBy,
                    })
                    .ExecuteCommandAsync();
            }

            var activeStoreCodes = await _context.Db.Queryable<Store>()
                .Where(store => store.IsActive && !store.IsDeleted && store.StoreCode != null)
                .Select(store => store.StoreCode!)
                .ToListAsync();
            if (activeStoreCodes.Count == 0)
            {
                var globalRecalculation = await new SetChildPurchasePriceService(_context.Db).RecalculateGlobalLockedAsync(
                    setChildPurchasePriceLock,
                    new[] { productCode },
                    updatedBy: updatedBy
                );
                if (globalRecalculation.ProductSetCode.SkippedGroupCount > 0)
                {
                    throw new ContainerSetGroupDataQualityException(
                        productCode,
                        globalRecalculation.Errors.FirstOrDefault()?.Reason ?? "套装子项成本无法完整重算"
                    );
                }
                return changed;
            }

            var existingStoreMultiCodes = (
                await _context.Db.Queryable<StoreMultiCodeProduct>()
                    .Where(item => item.ProductCode == productCode)
                    .ToListAsync()
            )
                .Where(item =>
                    item.MultiCodeProductCode != null
                    && setProductCodes.Contains(
                        item.MultiCodeProductCode.Trim(),
                        StringComparer.OrdinalIgnoreCase
                    )
                    && item.StoreCode != null
                    && activeStoreCodes.Contains(
                        item.StoreCode.Trim(),
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                .ToList();
            var existingStoreMultiCodeMap = SelectActiveStoreMultiCodes(
                productCode,
                existingStoreMultiCodes
            );
            var storeMultiCodesToInsert = new List<StoreMultiCodeProduct>();
            var storeMultiCodesToUpdate = new List<StoreMultiCodeProduct>();

            foreach (var relation in validRelations)
            {
                foreach (var storeCode in activeStoreCodes)
                {
                    var key = BuildStoreMultiCodeKey(storeCode, relation.SetProductCode);
                    if (existingStoreMultiCodeMap.TryGetValue(key, out var existingStoreMultiCode))
                    {
                        if (ShouldRefreshStoreMultiCode(existingStoreMultiCode, storeCode, relation))
                        {
                            ApplyExistingStoreMultiCodeValues(
                                existingStoreMultiCode,
                                storeCode,
                                relation,
                                now,
                                updatedBy
                            );
                            storeMultiCodesToUpdate.Add(existingStoreMultiCode);
                            changed = true;
                        }
                        continue;
                    }

                    storeMultiCodesToInsert.Add(
                        BuildStoreMultiCode(productCode, storeCode, relation, now, updatedBy)
                    );
                    changed = true;
                }
            }

            if (storeMultiCodesToInsert.Count > 0)
            {
                await _context.Db.Insertable(storeMultiCodesToInsert).PageSize(1000).ExecuteCommandAsync();
            }

            if (storeMultiCodesToUpdate.Count > 0)
            {
                await _context.Db.Updateable(storeMultiCodesToUpdate)
                    .UpdateColumns(item => new
                    {
                        item.StoreMultiCodeProductCode,
                        item.MultiBarcode,
                        item.MultiCodeRetailPrice,
                        item.UpdatedAt,
                        item.UpdatedBy,
                    })
                    .ExecuteCommandAsync();
            }

            // 所有关系、零售价和门店投影写完后，锁内重读主成本并统一回填派生成本。
            var fullRecalculation = await new SetChildPurchasePriceService(_context.Db).RecalculateLockedAsync(
                setChildPurchasePriceLock,
                new[] { productCode },
                storeCodes: activeStoreCodes,
                updatedBy: updatedBy
            );
            if (
                fullRecalculation.ProductSetCode.SkippedGroupCount > 0
                || fullRecalculation.StoreMultiCodeProduct.SkippedGroupCount > 0
            )
            {
                throw new ContainerSetGroupDataQualityException(
                    productCode,
                    fullRecalculation.Errors.FirstOrDefault()?.Reason ?? "套装子项成本无法完整重算"
                );
            }
            return changed;
        }

        private static void ValidateSetRelationGroup(
            string productCode,
            IReadOnlyCollection<DomesticSetProduct> setRelations
        )
        {
            if (string.IsNullOrWhiteSpace(productCode))
            {
                throw new ContainerSetGroupDataQualityException(productCode, "套装主商品编码不能为空");
            }

            if (setRelations.Count == 0)
            {
                throw new ContainerSetGroupDataQualityException(productCode, "套装子项不能为空");
            }

            foreach (var relation in setRelations)
            {
                if (string.IsNullOrWhiteSpace(relation.SetProductCode))
                {
                    throw new ContainerSetGroupDataQualityException(productCode, "存在空的套装子项编码");
                }

                if (string.IsNullOrWhiteSpace(relation.SetProductNo))
                {
                    throw new ContainerSetGroupDataQualityException(
                        productCode,
                        $"套装子项 {relation.SetProductCode.Trim()} 的货号为空"
                    );
                }

                if (
                    !string.IsNullOrWhiteSpace(relation.ProductCode)
                    && !string.Equals(
                        relation.ProductCode.Trim(),
                        productCode.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    throw new ContainerSetGroupDataQualityException(
                        productCode,
                        $"套装子项 {relation.SetProductCode.Trim()} 属于其他主商品 {relation.ProductCode}"
                    );
                }

                relation.ProductCode = productCode.Trim();
                relation.SetProductCode = relation.SetProductCode.Trim();
                relation.SetProductNo = relation.SetProductNo.Trim();
            }

            var duplicateKey = setRelations
                .GroupBy(
                    relation => relation.SetProductCode.Trim().ToUpperInvariant(),
                    StringComparer.Ordinal
                )
                .FirstOrDefault(group => group.Count() > 1)
                ?.Key;
            if (duplicateKey != null)
            {
                throw new ContainerSetGroupDataQualityException(
                    productCode,
                    $"套装子项编码重复: {duplicateKey}"
                );
            }
        }

        private static Dictionary<string, ProductSetCode> SelectActiveProductSetCodes(
            string productCode,
            IEnumerable<ProductSetCode> rows
        )
        {
            var selectedRows = new Dictionary<string, ProductSetCode>(
                StringComparer.OrdinalIgnoreCase
            );
            foreach (
                var group in rows.GroupBy(
                    row => row.SetProductCode?.Trim() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase
                )
            )
            {
                var activeRows = group.Where(row => row.IsActive && !row.IsDeleted).ToList();
                if (activeRows.Count == 0)
                {
                    throw new ContainerSetGroupDataQualityException(
                        productCode,
                        $"套装子项 {group.Key} 仅存在停用或软删除的总部关系，禁止容器建品复活"
                    );
                }
                if (activeRows.Count > 1)
                {
                    throw new ContainerSetGroupDataQualityException(
                        productCode,
                        $"套装子项 {group.Key} 存在多条活跃总部关系"
                    );
                }

                var activeRow = activeRows[0];
                if (activeRow.SetType != 1)
                {
                    throw new ContainerSetGroupDataQualityException(
                        productCode,
                        $"套装子项 {activeRow.SetProductCode} 已被普通多码关系占用"
                    );
                }
                selectedRows.Add(group.Key, activeRow);
            }
            return selectedRows;
        }

        private static Dictionary<string, StoreMultiCodeProduct> SelectActiveStoreMultiCodes(
            string productCode,
            IEnumerable<StoreMultiCodeProduct> rows
        )
        {
            var selectedRows = new Dictionary<string, StoreMultiCodeProduct>(
                StringComparer.OrdinalIgnoreCase
            );
            foreach (
                var group in rows.GroupBy(
                    row => BuildStoreMultiCodeKey(row.StoreCode, row.MultiCodeProductCode),
                    StringComparer.OrdinalIgnoreCase
                )
            )
            {
                var activeRows = group.Where(row => row.IsActive && !row.IsDeleted).ToList();
                var sample = group.First();
                if (activeRows.Count == 0)
                {
                    throw new ContainerSetGroupDataQualityException(
                        productCode,
                        $"门店 {sample.StoreCode} 套装子项 {sample.MultiCodeProductCode} 仅存在停用或软删除的门店关系，禁止容器建品复活"
                    );
                }
                if (activeRows.Count > 1)
                {
                    throw new ContainerSetGroupDataQualityException(
                        productCode,
                        $"门店 {sample.StoreCode} 套装子项 {sample.MultiCodeProductCode} 存在多条活跃门店关系"
                    );
                }
                selectedRows.Add(group.Key, activeRows[0]);
            }
            return selectedRows;
        }

        private static ProductSetCode BuildProductSetCode(
            string productCode,
            DomesticSetProduct relation,
            DateTime now,
            string updatedBy
        )
        {
            // 新建行一次性初始化身份、类型、生命周期和创建审计；与已有行窄更新明确分离。
            return new ProductSetCode
            {
                SetCodeId = relation.SetProductCode!,
                ProductCode = productCode,
                SetProductCode = relation.SetProductCode!,
                SetItemNumber = relation.SetProductNo,
                SetBarcode = relation.SetBarcode,
                SetPurchasePrice = null,
                SetRetailPrice = relation.OEMPrice,
                SetQuantity = 1,
                SetType = 1,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = now,
                CreatedBy = updatedBy,
                UpdatedAt = now,
                UpdatedBy = updatedBy,
            };
        }

        private static void ApplyExistingSetCodeValues(
            ProductSetCode setCode,
            DomesticSetProduct relation,
            DateTime now,
            string updatedBy
        )
        {
            setCode.SetItemNumber = relation.SetProductNo;
            setCode.SetBarcode = relation.SetBarcode;
            setCode.SetRetailPrice = relation.OEMPrice;
            setCode.SetQuantity = 1;
            setCode.UpdatedAt = now;
            setCode.UpdatedBy = updatedBy;
        }

        private static StoreMultiCodeProduct BuildStoreMultiCode(
            string productCode,
            string storeCode,
            DomesticSetProduct relation,
            DateTime now,
            string updatedBy
        )
        {
            // 新建门店投影才设置 UUID、业务键、生命周期及定价默认值。
            return new StoreMultiCodeProduct
            {
                UUID = UuidHelper.GenerateUuid7(),
                StoreCode = storeCode,
                ProductCode = productCode,
                MultiCodeProductCode = relation.SetProductCode,
                StoreMultiCodeProductCode = storeCode + relation.SetProductCode,
                MultiBarcode = relation.SetBarcode,
                PurchasePrice = null,
                MultiCodeRetailPrice = relation.OEMPrice,
                DiscountRate = null,
                IsAutoPricing = false,
                IsSpecialProduct = false,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = now,
                CreatedBy = updatedBy,
                UpdatedAt = now,
                UpdatedBy = updatedBy,
            };
        }

        private static void ApplyExistingStoreMultiCodeValues(
            StoreMultiCodeProduct storeMultiCode,
            string storeCode,
            DomesticSetProduct relation,
            DateTime now,
            string updatedBy
        )
        {
            storeMultiCode.StoreMultiCodeProductCode = storeCode + relation.SetProductCode;
            storeMultiCode.MultiBarcode = relation.SetBarcode;
            storeMultiCode.MultiCodeRetailPrice = relation.OEMPrice;
            storeMultiCode.UpdatedAt = now;
            storeMultiCode.UpdatedBy = updatedBy;
        }

        private static bool ShouldRefreshSetCode(
            ProductSetCode setCode,
            DomesticSetProduct relation
        )
        {
            return setCode.SetItemNumber != relation.SetProductNo
                || setCode.SetBarcode != relation.SetBarcode
                || setCode.SetRetailPrice != relation.OEMPrice
                || setCode.SetQuantity != 1;
        }

        private static bool ShouldRefreshStoreMultiCode(
            StoreMultiCodeProduct storeMultiCode,
            string storeCode,
            DomesticSetProduct relation
        )
        {
            return storeMultiCode.StoreMultiCodeProductCode != storeCode + relation.SetProductCode
                || storeMultiCode.MultiBarcode != relation.SetBarcode
                || storeMultiCode.MultiCodeRetailPrice != relation.OEMPrice;
        }

        private static string BuildStoreMultiCodeKey(string? storeCode, string? multiCodeProductCode)
        {
            return $"{storeCode?.Trim()}|{multiCodeProductCode?.Trim()}";
        }

        private async Task<HashSet<string>> EnsureSetRelationsFromContainerChildrenAsync(
            string containerGuid,
            List<ContainerProductCreationSourceRow> rows,
            HashSet<string> existingProductCodes,
            Dictionary<string, List<DomesticSetProduct>> setRelationsByProductCode
        )
        {
            var linkedSetChildDetailHguids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var setMainRows = rows
                .Where(row =>
                    NormalizeContainerProductType(row.ContainerProductType, row.DomesticProductType)
                    == ContainerProductCreationProductType.Set
                )
                .Where(row =>
                    !string.IsNullOrWhiteSpace(row.ProductCode)
                    && !existingProductCodes.Contains(row.ProductCode.Trim())
                    && !string.IsNullOrWhiteSpace(row.ItemNumber)
                    && !string.IsNullOrWhiteSpace(row.MixedGroupCode)
                )
                .ToList();

            if (setMainRows.Count == 0)
            {
                return linkedSetChildDetailHguids;
            }

            var mixedGroupCodes = setMainRows
                .Select(row => row.MixedGroupCode!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var childRows = await LoadSetChildRowsAsync(containerGuid, mixedGroupCodes);
            var childRowsByGroup = childRows
                .Where(row => !string.IsNullOrWhiteSpace(row.MixedGroupCode))
                .GroupBy(row => row.MixedGroupCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var setChildProductCodes = childRows
                .Select(row => row.ProductCode?.Trim())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var existingSetProductCodes = setChildProductCodes.Count == 0
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : (
                    await _context.Db.Queryable<DomesticSetProduct>()
                        .Where(item => setChildProductCodes.Contains(item.SetProductCode) && !item.IsDeleted)
                        .Select(item => item.SetProductCode)
                        .ToListAsync()
                )
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var setRelationsToInsert = new List<DomesticSetProduct>();
            var pendingSetProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mainRow in setMainRows)
            {
                var mainProductCode = mainRow.ProductCode!.Trim();
                var mainItemNumber = mainRow.ItemNumber!.Trim();
                var mixedGroupCode = mainRow.MixedGroupCode!.Trim();

                if (!childRowsByGroup.TryGetValue(mixedGroupCode, out var sameGroupChildren))
                {
                    continue;
                }

                // 套装子项关系必须是完整组：原始子项编码为空，或标准化后重复时，
                // 不能跳过坏行后继续创建残缺套装，直接拒绝该主商品整组关系。
                var normalizedSetChildProductCodes = sameGroupChildren
                    .Select(childRow => childRow.ProductCode)
                    .ToList();
                if (
                    normalizedSetChildProductCodes.Any(string.IsNullOrWhiteSpace)
                    || normalizedSetChildProductCodes
                        .Select(code => code!.Trim().ToUpperInvariant())
                        .Distinct(StringComparer.Ordinal)
                        .Count() != normalizedSetChildProductCodes.Count
                )
                {
                    var conflictingKey = normalizedSetChildProductCodes
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!.Trim().ToUpperInvariant())
                        .GroupBy(code => code, StringComparer.Ordinal)
                        .FirstOrDefault(group => group.Count() > 1)
                        ?.Key;
                    throw new ContainerSetGroupDataQualityException(
                        mainProductCode,
                        conflictingKey == null
                            ? $"混装组 {mixedGroupCode} 存在空的套装子项编码"
                            : $"混装组 {mixedGroupCode} 的套装子项编码重复: {conflictingKey}"
                    );
                }

                var existingRelations = setRelationsByProductCode.TryGetValue(mainProductCode, out var relations)
                    ? relations
                    : new List<DomesticSetProduct>();
                if (existingRelations.Count > 0)
                {
                    MarkLinkedSetChildRows(sameGroupChildren, existingRelations, linkedSetChildDetailHguids);
                    continue;
                }

                var newRelations = new List<DomesticSetProduct>();
                foreach (var childRow in sameGroupChildren)
                {
                    var setProductCode = childRow.ProductCode?.Trim();
                    var setProductNo = childRow.ItemNumber?.Trim();
                    if (string.IsNullOrWhiteSpace(setProductNo))
                    {
                        throw new ContainerSetGroupDataQualityException(
                            mainProductCode,
                            $"套装子项 {setProductCode} 的货号为空"
                        );
                    }
                    if (existingSetProductCodes.Contains(setProductCode!))
                    {
                        throw new ContainerSetGroupDataQualityException(
                            mainProductCode,
                            $"套装子项编码 {setProductCode} 已被其他关系占用"
                        );
                    }
                    if (!pendingSetProductCodes.Add(setProductCode!))
                    {
                        throw new ContainerSetGroupDataQualityException(
                            mainProductCode,
                            $"本次创建中套装子项编码重复: {setProductCode}"
                        );
                    }

                    // 从同货柜同混装组的套装子项补齐国内套装关系，后续批量创建会复用它生成商品子码和分店子码。
                    newRelations.Add(new DomesticSetProduct
                    {
                        SetProductCode = setProductCode!,
                        ProductCode = mainProductCode,
                        ProductNo = mainItemNumber,
                        SetProductNo = setProductNo,
                        SetBarcode = childRow.Barcode,
                        DomesticPrice = childRow.DomesticPrice,
                        ImportPrice = childRow.ImportPrice,
                        OEMPrice = childRow.OEMPrice,
                        IsDeleted = false,
                    });
                    if (!string.IsNullOrWhiteSpace(childRow.DetailHguid))
                    {
                        linkedSetChildDetailHguids.Add(childRow.DetailHguid);
                    }
                }

                if (newRelations.Count > 0)
                {
                    setRelationsToInsert.AddRange(newRelations);
                    setRelationsByProductCode[mainProductCode] = newRelations;
                }
            }

            if (setRelationsToInsert.Count > 0)
            {
                await _context.Db.Insertable(setRelationsToInsert).ExecuteCommandAsync();
            }

            return linkedSetChildDetailHguids;
        }

        private sealed class ContainerSetGroupDataQualityException : InvalidOperationException
        {
            public ContainerSetGroupDataQualityException(string? productCode, string message)
                : base(message)
            {
                ProductCode = productCode;
            }

            public string? ProductCode { get; }
        }

        private async Task<List<ContainerProductCreationSourceRow>> LoadSetChildRowsAsync(
            string containerGuid,
            List<string> mixedGroupCodes
        )
        {
            if (mixedGroupCodes.Count == 0)
            {
                return new List<ContainerProductCreationSourceRow>();
            }

            return await _context.Db.Queryable<ContainerDetail>()
                .LeftJoin<DomesticProduct>((detail, domestic) => detail.ProductCode == domestic.ProductCode)
                .Where((detail, domestic) =>
                    detail.ContainerCode == containerGuid
                    && detail.MixedGroupCode != null
                    && mixedGroupCodes.Contains(detail.MixedGroupCode)
                    && detail.ProductType == "套装子商品"
                    && !detail.IsDeleted
                )
                .Select((detail, domestic) => new ContainerProductCreationSourceRow
                {
                    DetailHguid = detail.DetailCode,
                    ProductCode = detail.ProductCode,
                    ContainerProductType = detail.ProductType,
                    MixedGroupCode = detail.MixedGroupCode,
                    SetQuantity = detail.SetQuantity,
                    DomesticPrice = detail.DomesticPrice,
                    ImportPrice = detail.ImportPrice,
                    OEMPrice = detail.OEMPrice,
                    Volume = detail.UnitVolume,
                    ItemNumber = domestic.HBProductNo,
                    ChineseName = domestic.ProductName,
                    EnglishName = domestic.EnglishProductName,
                    Barcode = domestic.Barcode,
                    ImageUrl = domestic.ProductImage,
                    DomesticProductType = domestic.ProductType,
                })
                .ToListAsync();
        }

        private static void MarkLinkedSetChildRows(
            List<ContainerProductCreationSourceRow> sameGroupChildren,
            List<DomesticSetProduct> existingRelations,
            HashSet<string> linkedSetChildDetailHguids
        )
        {
            var existingSetProductCodes = existingRelations
                .Select(relation => relation.SetProductCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var childRow in sameGroupChildren)
            {
                var childProductCode = childRow.ProductCode?.Trim();
                if (
                    !string.IsNullOrWhiteSpace(childProductCode)
                    && existingSetProductCodes.Contains(childProductCode)
                    && !string.IsNullOrWhiteSpace(childRow.DetailHguid)
                )
                {
                    linkedSetChildDetailHguids.Add(childRow.DetailHguid);
                }
            }
        }

        private static bool TryBuildCreateItem(
            ContainerProductCreationSourceRow row,
            HashSet<string> existingProductCodes,
            HashSet<string> existingWarehouseProductCodes,
            HashSet<string> existingItemNumbers,
            HashSet<string> batchProductCodes,
            HashSet<string> batchItemNumbers,
            Dictionary<string, List<DomesticSetProduct>> setRelationsByProductCode,
            HashSet<string> linkedSetChildDetailHguids,
            ContainerProductCreationResultDto result,
            bool allowEnglishNameFallback,
            out CreateItemDto createItem
        )
        {
            createItem = new CreateItemDto();
            var productCode = row.ProductCode?.Trim();
            var itemNumber = row.ItemNumber?.Trim();

            if (string.IsNullOrWhiteSpace(productCode))
            {
                AddSkipped(result, null, itemNumber, row.DetailHguid, "MISSING_PRODUCT_CODE", "商品编码不能为空");
                return false;
            }

            if (string.IsNullOrWhiteSpace(itemNumber))
            {
                AddSkipped(result, productCode, null, row.DetailHguid, "MISSING_ITEM_NUMBER", "货号不能为空");
                return false;
            }

            var productType = NormalizeContainerProductType(row.ContainerProductType, row.DomesticProductType);
            if (productType == ContainerProductCreationProductType.SetChild)
            {
                if (
                    !string.IsNullOrWhiteSpace(row.DetailHguid)
                    && linkedSetChildDetailHguids.Contains(row.DetailHguid)
                )
                {
                    return false;
                }

                AddSkipped(result, productCode, itemNumber, row.DetailHguid, "MISSING_SET_RELATION", "套装子商品不单独创建；请选择对应套装主商品生成子码");
                return false;
            }

            // 有效商品名称：中文名称非空白用中文；「创建新商品」任务下中文为空/纯空白时用英文名称兜底。
            // 两者都为空只跳过本行（原因码沿用 MISSING_CHINESE_NAME，不阻断同批其它行），
            // 并且绝不拿货号/商品编码当名称写入商品主档。
            // 整柜提交（allowEnglishNameFallback=false）保持原口径：中文名称为空即作为阻断项，不启用兜底。
            string productName;
            if (!string.IsNullOrWhiteSpace(row.ChineseName))
            {
                productName = row.ChineseName;
            }
            else if (allowEnglishNameFallback && !string.IsNullOrWhiteSpace(row.EnglishName))
            {
                productName = row.EnglishName;
            }
            else
            {
                AddSkipped(
                    result,
                    productCode,
                    itemNumber,
                    row.DetailHguid,
                    "MISSING_CHINESE_NAME",
                    allowEnglishNameFallback ? "商品名称与英文名称均为空，已跳过" : "商品名称不能为空"
                );
                return false;
            }

            // 中文有、英文空：保持原口径，仍按英文名称缺失跳过（此处英文名称不参与兜底）。
            if (string.IsNullOrWhiteSpace(row.EnglishName))
            {
                AddSkipped(result, productCode, itemNumber, row.DetailHguid, "MISSING_ENGLISH_NAME", "英文名称不能为空");
                return false;
            }

            if (!row.ImportPrice.HasValue || row.ImportPrice.Value <= 0)
            {
                AddSkipped(result, productCode, itemNumber, row.DetailHguid, "INVALID_IMPORT_PRICE", "进口价格必须大于 0");
                return false;
            }

            if (!row.OEMPrice.HasValue || row.OEMPrice.Value <= 0)
            {
                AddSkipped(result, productCode, itemNumber, row.DetailHguid, "INVALID_OEM_PRICE", "零售价必须大于 0");
                return false;
            }

            if (existingProductCodes.Contains(productCode))
            {
                AddSkipped(result, productCode, itemNumber, row.DetailHguid, "DUPLICATE_PRODUCT_CODE", "本地商品已存在");
                return false;
            }

            if (!batchProductCodes.Add(productCode))
            {
                AddSkipped(result, productCode, itemNumber, row.DetailHguid, "DUPLICATE_PRODUCT_CODE", "本次提交中商品编码重复");
                return false;
            }

            if (existingItemNumbers.Contains(itemNumber))
            {
                AddSkipped(result, productCode, itemNumber, row.DetailHguid, "DUPLICATE_ITEM_NUMBER", "本地货号已存在");
                return false;
            }

            if (!batchItemNumbers.Add(itemNumber))
            {
                AddSkipped(result, productCode, itemNumber, row.DetailHguid, "DUPLICATE_ITEM_NUMBER", "本次提交中货号重复");
                return false;
            }

            if (existingWarehouseProductCodes.Contains(productCode))
            {
                AddSkipped(result, productCode, itemNumber, row.DetailHguid, "DUPLICATE_WAREHOUSE_PRODUCT", "仓库商品已存在");
                return false;
            }

            if (
                productType == ContainerProductCreationProductType.Set
                && !setRelationsByProductCode.ContainsKey(productCode)
            )
            {
                AddSkipped(result, productCode, itemNumber, row.DetailHguid, "MISSING_SET_RELATION", "缺少套装关系，已跳过");
                return false;
            }

            createItem = new CreateItemDto
            {
                ProductCode = productCode,
                ItemNumber = itemNumber,
                Barcode = row.Barcode,
                // 上面已保证有效商品名称非空，这里不再用货号兜底，避免把货号当名称写进商品主档。
                ChineseName = productName,
                EnglishName = row.EnglishName,
                DomesticPrice = row.DomesticPrice,
                OEMPrice = row.OEMPrice.Value,
                ImportPrice = row.ImportPrice.Value,
                Volume = row.Volume,
                ImageUrl = row.ImageUrl,
                WarehouseCategoryGUID = row.WarehouseCategoryGUID,
                ProductType = productType == ContainerProductCreationProductType.Set ? 1 : 0,
                IsSetProduct = productType == ContainerProductCreationProductType.Set,
            };
            return true;
        }

        private static bool TryBuildUpdateItem(
            ContainerProductCreationSourceRow row,
            ContainerProductCreationResultDto result,
            out UpdateItemDto updateItem
        )
        {
            updateItem = new UpdateItemDto();
            var productCode = row.ProductCode?.Trim();
            var itemNumber = row.ItemNumber?.Trim();

            if (string.IsNullOrWhiteSpace(productCode))
            {
                AddSkipped(result, null, itemNumber, row.DetailHguid, "MISSING_PRODUCT_CODE", "商品编码不能为空");
                return false;
            }

            var hasPriceOrVolume =
                row.DomesticPrice.HasValue
                || (row.ImportPrice.HasValue && row.ImportPrice.Value > 0)
                || (row.OEMPrice.HasValue && row.OEMPrice.Value > 0)
                || row.Volume.HasValue;
            if (!hasPriceOrVolume)
            {
                AddSkipped(result, productCode, itemNumber, row.DetailHguid, "NO_PRICE_FIELDS", "没有可更新的价格或体积字段");
                return false;
            }

            updateItem = new UpdateItemDto
            {
                ProductCode = productCode,
                ItemNumber = itemNumber,
                DomesticPrice = row.DomesticPrice,
                ImportPrice = row.ImportPrice.HasValue && row.ImportPrice.Value > 0 ? row.ImportPrice : null,
                OEMPrice = row.OEMPrice.HasValue && row.OEMPrice.Value > 0 ? row.OEMPrice : null,
                Volume = row.Volume,
                IsActive = null,
            };
            return true;
        }

        private async Task UpdateExistingProductsForSubmitAsync(
            List<ContainerProductUpdateSource> sources,
            string effectiveUpdatedBy,
            ContainerProductCreationResultDto result,
            SetChildPurchasePriceLockScope setChildPurchasePriceLock
        )
        {
            if (sources.Count == 0)
            {
                return;
            }

            var now = DateTime.Now;
            var productCodes = sources
                .Select(source => source.Item.ProductCode?.Trim())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var products = await _context.Db.Queryable<Product>()
                .Where(product => product.ProductCode != null && productCodes.Contains(product.ProductCode) && !product.IsDeleted)
                .ToListAsync();
            var productMap = products.ToDictionary(
                product => product.ProductCode!,
                product => product,
                StringComparer.OrdinalIgnoreCase
            );

            var warehouseProducts = await _context.Db.Queryable<WarehouseProduct>()
                .Where(product => product.ProductCode != null && productCodes.Contains(product.ProductCode) && !product.IsDeleted)
                .ToListAsync();
            var warehouseProductMap = warehouseProducts.ToDictionary(
                product => product.ProductCode!,
                product => product,
                StringComparer.OrdinalIgnoreCase
            );

            var productsToUpdate = new List<Product>();
            var warehouseProductsToUpdate = new List<WarehouseProduct>();
            var updatedSources = new List<ContainerProductUpdateSource>();

            foreach (var source in sources)
            {
                var item = source.Item;
                var productCode = item.ProductCode?.Trim();
                if (string.IsNullOrWhiteSpace(productCode))
                {
                    continue;
                }

                if (!productMap.TryGetValue(productCode, out var product))
                {
                    AddError(result, productCode, item.ItemNumber, source.Row.DetailHguid, "PRODUCT_NOT_FOUND", "本地商品不存在，无法更新价格");
                    continue;
                }

                if (!warehouseProductMap.TryGetValue(productCode, out var warehouseProduct))
                {
                    AddError(result, productCode, item.ItemNumber, source.Row.DetailHguid, "WAREHOUSE_PRODUCT_NOT_FOUND", "仓库商品不存在，无法更新仓库价格");
                    continue;
                }

                if (item.ImportPrice.HasValue)
                {
                    product.PurchasePrice = item.ImportPrice;
                    product.UpdatedAt = now;
                    product.UpdatedBy = effectiveUpdatedBy;
                    productsToUpdate.Add(product);
                }

                // 整柜提交只同步价格和体积，不触碰商品上下架、名称、英文名、条码或分类。
                if (item.DomesticPrice.HasValue)
                {
                    warehouseProduct.DomesticPrice = item.DomesticPrice;
                }
                if (item.ImportPrice.HasValue)
                {
                    warehouseProduct.ImportPrice = item.ImportPrice;
                }
                if (item.OEMPrice.HasValue)
                {
                    warehouseProduct.OEMPrice = item.OEMPrice;
                }
                if (item.Volume.HasValue)
                {
                    warehouseProduct.Volume = item.Volume;
                }
                // 整柜提交的已有商品同样必须保留实际操作人的审计信息。
                warehouseProduct.UpdatedBy = effectiveUpdatedBy;
                warehouseProduct.UpdatedAt = now;
                warehouseProductsToUpdate.Add(warehouseProduct);

                updatedSources.Add(source);
            }

            try
            {
                if (productsToUpdate.Count > 0)
                {
                    await _context.Db.Updateable(productsToUpdate)
                        .UpdateColumns(product => new
                        {
                            product.PurchasePrice,
                            product.UpdatedBy,
                            product.UpdatedAt,
                        })
                        .ExecuteCommandAsync();
                }

                if (warehouseProductsToUpdate.Count > 0)
                {
                    await _context.Db.Updateable(warehouseProductsToUpdate)
                        .UpdateColumns(product => new
                        {
                            product.DomesticPrice,
                            product.OEMPrice,
                            product.ImportPrice,
                            product.Volume,
                            product.UpdatedBy,
                            product.UpdatedAt,
                        })
                        .ExecuteCommandAsync();
                }

                await UpsertActiveStoreRetailPricesAsync(updatedSources, now);

                var updatedProductCodes = updatedSources
                    .Select(source => source.Item.ProductCode)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .ToList();
                if (updatedProductCodes.Count > 0)
                {
                    // 整柜主成本、门店主价全部写完后，在同一产品锁内重读并回填套装子项成本。
                    var recalculation = await new SetChildPurchasePriceService(
                        _context.Db
                    ).RecalculateLockedAsync(
                        setChildPurchasePriceLock,
                        updatedProductCodes,
                        storeCodes: null,
                        updatedBy: effectiveUpdatedBy
                    );
                    if (
                        recalculation.ProductSetCode.SkippedGroupCount > 0
                        || recalculation.StoreMultiCodeProduct.SkippedGroupCount > 0
                    )
                    {
                        var reason = recalculation.Errors.FirstOrDefault()?.Reason
                            ?? "整柜更新后的套装子项成本重算不完整";
                        throw new InvalidOperationException(reason);
                    }
                }

                foreach (var source in updatedSources)
                {
                    result.Updated.Add(new ContainerProductCreationResultItemDto
                    {
                        ProductCode = source.Item.ProductCode,
                        ItemNumber = source.Item.ItemNumber,
                        DetailHguid = source.Row.DetailHguid,
                        Message = "价格已更新",
                    });
                }
            }
            catch (Exception ex)
                when (
                    !ContainerMutationLock.TryResolveConflict(ex, out _)
                    && !SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
                )
            {
                _logger.LogError(ex, "整柜提交更新已有商品价格失败");
                AddError(result, null, null, null, "UPDATE_EXISTING_PRODUCTS_FAILED", ex.Message);
            }
        }

        private async Task UpsertActiveStoreRetailPricesAsync(
            List<ContainerProductUpdateSource> sources,
            DateTime now
        )
        {
            var activeStoreCodes = await LoadActiveStoreCodesAsync();
            if (activeStoreCodes.Count == 0 || sources.Count == 0)
            {
                return;
            }

            var productCodes = sources
                .Select(source => source.Item.ProductCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var existingRows = await _context.Db.Queryable<StoreRetailPrice>()
                .Where(row =>
                    row.StoreCode != null
                    && activeStoreCodes.Contains(row.StoreCode)
                    && row.ProductCode != null
                    && productCodes.Contains(row.ProductCode)
                    && !row.IsDeleted
                )
                .ToListAsync();
            var existingMap = existingRows
                .GroupBy(row => BuildStoreProductKey(row.StoreCode, row.ProductCode), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(row => row.UpdatedAt ?? row.CreatedAt).First(),
                    StringComparer.OrdinalIgnoreCase
                );

            var rowsToInsert = new List<StoreRetailPrice>();
            var rowsToUpdate = new List<StoreRetailPrice>();

            foreach (var source in sources)
            {
                var item = source.Item;
                if (string.IsNullOrWhiteSpace(item.ProductCode))
                {
                    continue;
                }

                foreach (var storeCode in activeStoreCodes)
                {
                    var key = BuildStoreProductKey(storeCode, item.ProductCode);
                    if (existingMap.TryGetValue(key, out var existing))
                    {
                        if (ApplyStoreRetailPriceValues(existing, item, now, updateActiveFlag: false))
                        {
                            rowsToUpdate.Add(existing);
                        }
                        continue;
                    }

                    var row = new StoreRetailPrice
                    {
                        UUID = UuidHelper.GenerateUuid7(),
                        StoreCode = storeCode,
                        ProductCode = item.ProductCode,
                        StoreProductCode = storeCode + item.ProductCode,
                        IsActive = true,
                        IsAutoPricing = false,
                        IsDeleted = false,
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    ApplyStoreRetailPriceValues(row, item, now, updateActiveFlag: true);
                    rowsToInsert.Add(row);
                    existingMap[key] = row;
                }
            }

            if (rowsToInsert.Count > 0)
            {
                await _context.Db.Insertable(rowsToInsert).ExecuteCommandAsync();
            }

            if (rowsToUpdate.Count > 0)
            {
                await _context.Db.Updateable(rowsToUpdate)
                    .UpdateColumns(row => new
                    {
                        row.PurchasePrice,
                        row.StoreRetailPriceValue,
                        row.UpdatedAt,
                    })
                    .ExecuteCommandAsync();
            }
        }

        private async Task<List<string>> LoadActiveStoreCodesAsync()
        {
            return (await _context.Db.Queryable<Store>()
                .Where(store => store.IsActive && !store.IsDeleted && store.StoreCode != null)
                .Select(store => store.StoreCode!)
                .ToListAsync())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool ApplyStoreRetailPriceValues(
            StoreRetailPrice row,
            UpdateItemDto item,
            DateTime now,
            bool updateActiveFlag
        )
        {
            var changed = false;
            if (item.ImportPrice.HasValue && row.PurchasePrice != item.ImportPrice)
            {
                row.PurchasePrice = item.ImportPrice;
                changed = true;
            }
            if (item.OEMPrice.HasValue && row.StoreRetailPriceValue != item.OEMPrice)
            {
                row.StoreRetailPriceValue = item.OEMPrice;
                changed = true;
            }
            if (updateActiveFlag)
            {
                row.IsActive = true;
                row.IsAutoPricing = false;
                changed = true;
            }
            if (changed)
            {
                row.UpdatedAt = now;
            }
            return changed;
        }

        private static string BuildStoreProductKey(string? storeCode, string? productCode)
        {
            return $"{storeCode?.Trim()}|{productCode?.Trim()}";
        }

        private static ContainerProductCreationProductType NormalizeContainerProductType(
            string? containerProductType,
            int? domesticProductType
        )
        {
            var normalized = containerProductType?.Trim();
            if (string.Equals(normalized, "套装子商品", StringComparison.OrdinalIgnoreCase))
            {
                return ContainerProductCreationProductType.SetChild;
            }

            if (string.Equals(normalized, "套装商品", StringComparison.OrdinalIgnoreCase))
            {
                return ContainerProductCreationProductType.Set;
            }

            return domesticProductType == 1
                ? ContainerProductCreationProductType.Set
                : ContainerProductCreationProductType.Normal;
        }

        private static List<string> NormalizeDetailHguids(IEnumerable<string>? detailHguids)
        {
            return (detailHguids ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private ContainerProductCreationResultDto CompleteSubmitTransaction(
            ContainerProductCreationResultDto result,
            bool submitTransactionStarted
        )
        {
            if (!submitTransactionStarted)
            {
                return result;
            }

            if (result.FailedCount > 0)
            {
                // 整柜提交有任何失败就回滚，避免创建/更新部分成功但货柜未完成造成数据半提交。
                RollbackSubmitTransaction(submitTransactionStarted);
                return ClearRolledBackWrites(result);
            }

            _context.Db.Ado.CommitTran();
            return result;
        }

        /// <summary>
        /// 整柜提交事务已回滚时，「已创建/已更新」的明细都没有落库；清空后重算计数，
        /// 避免结果仍显示「创建 N，更新 M」让用户误以为部分成功。跳过与失败明细保留，供用户修正。
        /// </summary>
        private static ContainerProductCreationResultDto ClearRolledBackWrites(
            ContainerProductCreationResultDto result
        )
        {
            result.Created.Clear();
            result.Updated.Clear();
            return FinalizeResult(result);
        }

        private void RollbackSubmitTransaction(bool submitTransactionStarted)
        {
            if (!submitTransactionStarted)
            {
                return;
            }

            try
            {
                _context.Db.Ado.RollbackTran();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "整柜提交事务回滚失败");
            }
        }

        private async Task<ContainerProductCreationResultDto> FinalizeSubmitResultAsync(
            string containerGuid,
            bool isSubmitContainer,
            ContainerProductCreationResultDto result
        )
        {
            if (isSubmitContainer)
            {
                PromoteBlockingSubmitSkipsToErrors(result);
            }
            FinalizeResult(result);
            if (!isSubmitContainer)
            {
                return result;
            }

            if (result.FailedCount > 0)
            {
                return result;
            }

            try
            {
                // 整柜提交只有在创建/更新没有失败时才推进货柜状态，避免失败明细被误标为完成。
                var container = await _context.Db.Queryable<Container>()
                    .Where(item => item.ContainerCode == containerGuid && !item.IsDeleted)
                    .FirstAsync();
                if (container == null)
                {
                    AddError(result, null, null, null, "CONTAINER_NOT_FOUND", "货柜不存在，无法标记为已完成");
                    return FinalizeResult(result);
                }

                container.Status = 2;
                container.UpdatedAt = DateTime.Now;
                await _context.Db.Updateable(container)
                    .UpdateColumns(item => new { item.Status, item.UpdatedAt })
                    .ExecuteCommandAsync();
                result.ContainerCompleted = true;
            }
            catch (Exception ex)
                when (
                    !ContainerMutationLock.TryResolveConflict(ex, out _)
                    && !SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
                )
            {
                _logger.LogError(ex, "整柜提交完成货柜状态失败: {ContainerGuid}", containerGuid);
                AddError(result, null, null, null, "COMPLETE_CONTAINER_FAILED", ex.Message);
            }

            return FinalizeResult(result);
        }

        private static void PromoteBlockingSubmitSkipsToErrors(
            ContainerProductCreationResultDto result
        )
        {
            var blockingReasonCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "MISSING_PRODUCT_CODE",
                "MISSING_ITEM_NUMBER",
                // 仅整柜提交会走到这里；「创建新商品」任务里两个名称均为空的行只作为 Skipped 返回，不会被升级为错误。
                "MISSING_CHINESE_NAME",
                "MISSING_ENGLISH_NAME",
                "INVALID_IMPORT_PRICE",
                "INVALID_OEM_PRICE",
                "DUPLICATE_PRODUCT_CODE",
                "DUPLICATE_ITEM_NUMBER",
                "DUPLICATE_WAREHOUSE_PRODUCT",
            };

            foreach (var skipped in result.Skipped)
            {
                if (
                    string.IsNullOrWhiteSpace(skipped.ReasonCode)
                    || !blockingReasonCodes.Contains(skipped.ReasonCode)
                    || result.Errors.Any(error =>
                        string.Equals(error.DetailHguid, skipped.DetailHguid, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(error.ReasonCode, skipped.ReasonCode, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    continue;
                }

                result.Errors.Add(new ContainerProductCreationResultItemDto
                {
                    ProductCode = skipped.ProductCode,
                    ItemNumber = skipped.ItemNumber,
                    DetailHguid = skipped.DetailHguid,
                    ReasonCode = skipped.ReasonCode,
                    Message = skipped.Message,
                });
            }
        }

        private static ContainerProductCreationResultDto FinalizeResult(
            ContainerProductCreationResultDto result
        )
        {
            result.CreatedCount = result.Created.Count;
            result.UpdatedCount = result.Updated.Count;
            result.SkippedCount = result.Skipped.Count;
            result.FailedCount = result.Errors.Count;
            return result;
        }

        private static void AddSkipped(
            ContainerProductCreationResultDto result,
            string? productCode,
            string? itemNumber,
            string? detailHguid,
            string reasonCode,
            string message
        )
        {
            result.Skipped.Add(new ContainerProductCreationResultItemDto
            {
                ProductCode = productCode,
                ItemNumber = itemNumber,
                DetailHguid = detailHguid,
                ReasonCode = reasonCode,
                Message = message,
            });
        }

        private static void AddError(
            ContainerProductCreationResultDto result,
            string? productCode,
            string? itemNumber,
            string? detailHguid,
            string reasonCode,
            string message
        )
        {
            result.Errors.Add(new ContainerProductCreationResultItemDto
            {
                ProductCode = productCode,
                ItemNumber = itemNumber,
                DetailHguid = detailHguid,
                ReasonCode = reasonCode,
                Message = message,
            });
        }

        private async Task<bool> TryRollbackNormalProductCreationTransactionAsync(
            string? operationId
        )
        {
            try
            {
                await _context.Db.Ado.RollbackTranAsync();
                return true;
            }
            catch (Exception rollbackException)
            {
                _logger.LogError(
                    rollbackException,
                    "货柜创建新商品回滚失败，无法确认是否写入，禁止自动重试: {OperationId}",
                    operationId
                );
                return false;
            }
        }

        private static bool ContainsCancellation(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is OperationCanceledException)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsRetryableProductCreationLockConflict(
            SetChildPurchasePriceLockException? conflict
        ) => conflict?.ResultCode is -1 or -3;

        private static List<ProductCreationCandidate> BuildProductCreationCandidates(
            IEnumerable<CreateItemDto> createItems,
            IReadOnlyDictionary<string, ContainerProductCreationSourceRow> sourceRows
        ) => createItems
            .Select(item =>
            {
                sourceRows.TryGetValue(item.ProductCode ?? string.Empty, out var sourceRow);
                return new ProductCreationCandidate(
                    item.ProductCode,
                    item.ItemNumber,
                    sourceRow?.DetailHguid
                );
            })
            .ToList();

        private static ContainerProductCreationResultDto AddProductCreationUnknownStateError(
            ContainerProductCreationResultDto result,
            IEnumerable<CreateItemDto> createItems,
            IReadOnlyDictionary<string, ContainerProductCreationSourceRow> sourceRows
        ) => AddProductCreationErrors(
            result,
            BuildProductCreationCandidates(createItems, sourceRows),
            "PRODUCT_CREATION_STATE_UNKNOWN",
            "商品创建提交或回滚状态未知，请查询后重试"
        );

        private static ContainerProductCreationResultDto AddProductCreationErrors(
            ContainerProductCreationResultDto result,
            IEnumerable<ProductCreationCandidate> candidates,
            string reasonCode,
            string message
        )
        {
            foreach (var candidate in candidates)
            {
                AddError(
                    result,
                    candidate.ProductCode,
                    candidate.ItemNumber,
                    candidate.DetailHguid,
                    reasonCode,
                    message
                );
            }

            return FinalizeResult(result);
        }

        private enum ContainerProductCreationProductType
        {
            Normal,
            Set,
            SetChild,
        }

        private sealed class ContainerProductCreationSourceRow
        {
            public string? DetailHguid { get; set; }
            public string? ProductCode { get; set; }
            public string? ContainerProductType { get; set; }
            public string? MixedGroupCode { get; set; }
            public decimal? SetQuantity { get; set; }
            public decimal? DomesticPrice { get; set; }
            public decimal? ImportPrice { get; set; }
            public decimal? OEMPrice { get; set; }
            public decimal? Volume { get; set; }
            public string? ItemNumber { get; set; }
            public string? ChineseName { get; set; }
            public string? EnglishName { get; set; }
            public string? Barcode { get; set; }
            public string? ImageUrl { get; set; }
            public int? DomesticProductType { get; set; }
            public string? WarehouseCategoryGUID { get; set; }
        }

        private sealed record ProductCreationCandidate(
            string? ProductCode,
            string? ItemNumber,
            string? DetailHguid
        );

        private sealed class RetryableProductCreationLockConflictException : Exception
        {
            internal RetryableProductCreationLockConflictException(
                Exception innerException,
                ContainerProductCreationResultDto result,
                List<ProductCreationCandidate> candidates
            )
                : base("货柜创建新商品等待商品锁超时", innerException)
            {
                Result = result;
                Candidates = candidates;
            }

            internal ContainerProductCreationResultDto Result { get; }
            internal List<ProductCreationCandidate> Candidates { get; }
        }

        private sealed class ContainerProductUpdateSource
        {
            public UpdateItemDto Item { get; set; } = new();
            public ContainerProductCreationSourceRow Row { get; set; } = new();
        }
    }
}
