using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Domain;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.OrderManagement.Infrastructure;

internal sealed class SqlSugarStoreOrderLineCommandStore(
    SqlSugarContext context,
    StoreOrderTransactionExecutor transactionExecutor,
    StoreOrderManagementPersistence persistence,
    IStoreOrderActorContext actorContext,
    IStoreOrderProductCostCoordinator productCostCoordinator,
    IWarehouseProductChangeHistoryService changeHistoryService
) : IStoreOrderLineCommandStore
{
    private readonly ISqlSugarClient _db = context.Db;

    /// <summary>
    /// 在事务内执行命令；退货规则校验失败（<see cref="StoreOrderReturnRuleException"/>）转成带中文文案的失败结果，
    /// 事务由执行器按失败结果回滚，不当成服务器异常记日志。
    /// </summary>
    private Task<StoreOrderManagementResult<T>> ExecuteGuardedAsync<T>(
        Func<Task<StoreOrderManagementResult<T>>> command
    )
    {
        return transactionExecutor.ExecuteAsync(async () =>
        {
            try
            {
                return await command();
            }
            catch (StoreOrderReturnRuleException exception)
            {
                return StoreOrderManagementResult<T>.Fail(exception.Message);
            }
        });
    }

    public Task<StoreOrderManagementResult<bool>> AddOrderLineAsync(
        AddOrderLineInput input
    )
    {
        return ExecuteGuardedAsync(async () =>
        {
            var order = await persistence.GetEditableOrderAsync(input.OrderGuid);
            if (order == null)
            {
                return StoreOrderManagementResult<bool>.Fail(
                    "Order not found or not editable"
                );
            }

            // 单笔新增沿用旧契约：请求中的 ImportPrice 不参与写入。
            await persistence.AddOrUpdateDetailAsync(
                order,
                input.ProductCode,
                input.Quantity,
                importPrice: null,
                isUpdate: false,
                returnRequest: input.ReturnRequest
            );
            await persistence.UpdateOrderTotalAsync(order.OrderGUID);
            return StoreOrderManagementResult<bool>.Ok(true);
        });
    }

    public Task<StoreOrderManagementResult<bool>> BatchAddOrderLineAsync(
        BatchAddOrderLineInput input
    )
    {
        return ExecuteGuardedAsync(async () =>
        {
            var order = await persistence.GetEditableOrderAsync(input.OrderGuid);
            if (order == null)
            {
                return StoreOrderManagementResult<bool>.Fail(
                    "Order not found or not editable"
                );
            }

            foreach (var item in input.Items)
            {
                await persistence.AddOrUpdateDetailAsync(
                    order,
                    item.ProductCode,
                    item.Quantity,
                    item.ImportPrice,
                    isUpdate: false,
                    returnRequest: item.ReturnRequest
                );
            }

            await persistence.UpdateOrderTotalAsync(order.OrderGUID);
            return StoreOrderManagementResult<bool>.Ok(true);
        });
    }

    public Task<StoreOrderManagementResult<bool>> UpdateOrderLineAsync(
        UpdateOrderLineInput input
    )
    {
        return ExecuteGuardedAsync(async () =>
        {
            var order = await persistence.GetEditableOrderAsync(input.OrderGuid);
            if (order == null)
            {
                return StoreOrderManagementResult<bool>.Fail(
                    "Order not found or not editable"
                );
            }

            var syncImportPrice = input.SyncImportPrice && input.ImportPrice.HasValue;
            var batchGuid = Guid.NewGuid();
            StoreOrderProductCostMutationScope? costScope = null;
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>? beforeSnapshots = null;
            if (syncImportPrice)
            {
                costScope = await productCostCoordinator.AcquireProductsAsync(
                    new[] { input.ProductCode }
                );
                beforeSnapshots = await changeHistoryService.CaptureSnapshotsAsync(
                    new[] { input.ProductCode }
                );
            }

            await persistence.AddOrUpdateDetailAsync(
                order,
                input.ProductCode,
                input.Quantity,
                input.ImportPrice,
                isUpdate: true,
                returnRequest: input.ReturnRequest
            );

            if (syncImportPrice)
            {
                await persistence.SyncImportPriceToProductTablesAsync(
                    input.ProductCode,
                    input.ImportPrice!.Value
                );
                await productCostCoordinator.RecalculateAsync(
                    costScope!,
                    actorContext.ActorName
                );
            }

            await persistence.UpdateOrderTotalAsync(order.OrderGUID);
            if (syncImportPrice)
            {
                var afterSnapshots = await changeHistoryService.CaptureSnapshotsAsync(
                    new[] { input.ProductCode }
                );
                await changeHistoryService.RecordChangesAsync(
                    beforeSnapshots!,
                    afterSnapshots,
                    new WarehouseProductChangeHistoryContextDto
                    {
                        Action = "Update",
                        Source = "StoreOrderImportPriceVariance",
                        SourceReference = $"StoreOrderImportPriceVariance:{order.OrderGUID}:{input.ProductCode}",
                        BatchGuid = batchGuid,
                        ActorName = actorContext.ActorName,
                    }
                );
            }

            return StoreOrderManagementResult<bool>.Ok(true);
        });
    }

    public Task<StoreOrderManagementResult<bool>> RemoveOrderLineAsync(
        RemoveOrderLineInput input
    )
    {
        return ExecuteGuardedAsync(async () =>
        {
            var order = await persistence.GetEditableOrderAsync(input.OrderGuid);
            if (order == null)
            {
                return StoreOrderManagementResult<bool>.Fail(
                    "Order not found or not editable"
                );
            }

            var detail = await _db.Queryable<WareHouseOrderDetails>()
                .Where(item =>
                    item.OrderGUID == input.OrderGuid
                    && item.DetailGUID == input.DetailGuid
                    && !item.IsDeleted
                )
                .FirstAsync();
            if (detail == null)
            {
                return StoreOrderManagementResult<bool>.Fail("Order line not found");
            }

            // 退货行只有持「分店订单退货」权限者才能删除，否则无权限者可以借删行抹掉别人录的退货。
            if (
                StoreOrderReturnRules.IsReturnLine(detail.AllocQuantity)
                && !await persistence.CanRecordReturnsAsync()
            )
            {
                return StoreOrderManagementResult<bool>.Fail(
                    "没有「分店订单退货」权限，不能修改或删除退货行"
                );
            }

            // 删掉的行不再保留退货字段，避免日后被复用时带出陈旧原因。
            detail.ReturnReason = null;
            detail.ReturnDisposition = null;
            detail.ReturnNote = null;
            detail.IsDeleted = true;
            detail.UpdatedAt = DateTime.Now;
            detail.UpdatedBy = actorContext.ActorName;
            await _db.Updateable(detail).ExecuteCommandAsync();
            await persistence.UpdateOrderTotalAsync(order.OrderGUID);
            return StoreOrderManagementResult<bool>.Ok(true);
        });
    }

    public Task<StoreOrderManagementResult<bool>> BatchUpdateOrderLineAsync(
        BatchUpdateOrderLineInput input
    )
    {
        return ExecuteGuardedAsync(async () =>
        {
            var order = await persistence.GetEditableOrderAsync(input.OrderGuid);
            if (order == null)
            {
                return StoreOrderManagementResult<bool>.Fail(
                    "Order not found or not editable"
                );
            }

            if (StoreOrderManagementRules.CanUseDetailGuidQuantityBatchUpdate(input))
            {
                return await UpdateOrderLinesByDetailGuidAsync(order, input);
            }

            var syncProductCodes = input.Items
                .Where(item => item.SyncImportPrice && item.ImportPrice.HasValue)
                .Select(item => item.ProductCode?.Trim())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var batchGuid = Guid.NewGuid();
            var costScope = await productCostCoordinator.AcquireProductsAsync(
                syncProductCodes
            );
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>? beforeSnapshots = null;
            if (syncProductCodes.Count > 0)
            {
                beforeSnapshots = await changeHistoryService.CaptureSnapshotsAsync(
                    syncProductCodes
                );
            }

            foreach (var item in input.Items)
            {
                await persistence.AddOrUpdateDetailAsync(
                    order,
                    item.ProductCode,
                    item.Quantity ?? 0,
                    item.ImportPrice,
                    isUpdate: true,
                    isBatch: true,
                    originalQuantity: item.Quantity,
                    returnRequest: item.ReturnRequest
                );

                if (item.SyncImportPrice && item.ImportPrice.HasValue)
                {
                    await persistence.SyncImportPriceToProductTablesAsync(
                        item.ProductCode,
                        item.ImportPrice.Value
                    );
                }
            }

            if (syncProductCodes.Count > 0)
            {
                await productCostCoordinator.RecalculateAsync(
                    costScope,
                    actorContext.ActorName
                );
            }

            await persistence.UpdateOrderTotalAsync(order.OrderGUID);
            if (syncProductCodes.Count > 0)
            {
                var afterSnapshots = await changeHistoryService.CaptureSnapshotsAsync(
                    syncProductCodes
                );
                await changeHistoryService.RecordChangesAsync(
                    beforeSnapshots!,
                    afterSnapshots,
                    new WarehouseProductChangeHistoryContextDto
                    {
                        Action = "Update",
                        Source = "StoreOrderImportPriceVariance",
                        SourceReference = $"StoreOrderImportPriceVariance:{order.OrderGUID}:{string.Join(",", syncProductCodes)}",
                        BatchGuid = batchGuid,
                        ActorName = actorContext.ActorName,
                    }
                );
            }

            return StoreOrderManagementResult<bool>.Ok(true);
        });
    }

    public Task<StoreOrderManagementResult<RefreshOrderLineImportPricesResult>> RefreshOrderLineImportPricesAsync(
        RefreshOrderLineImportPricesInput input
    )
    {
        return ExecuteGuardedAsync(async () =>
        {
            var orderExists = await _db.Queryable<WareHouseOrder>()
                .Where(item => item.OrderGUID == input.OrderGuid && !item.IsDeleted)
                .AnyAsync();
            if (!orderExists)
            {
                return StoreOrderManagementResult<RefreshOrderLineImportPricesResult>.Fail(
                    "Order not found"
                );
            }

            var requestedDetailGuids = input.DetailGuids.ToList();
            var detailQuery = _db.Queryable<WareHouseOrderDetails>()
                .Where(item => item.OrderGUID == input.OrderGuid && !item.IsDeleted);
            if (requestedDetailGuids.Count > 0)
            {
                detailQuery = detailQuery.Where(item =>
                    requestedDetailGuids.Contains(item.DetailGUID)
                );
            }

            var details = await detailQuery.ToListAsync();
            if (details.Count == 0)
            {
                return StoreOrderManagementResult<RefreshOrderLineImportPricesResult>.Ok(
                    new RefreshOrderLineImportPricesResult(0, 0, 0, 0)
                );
            }

            var productCodes = details
                .Select(item => item.ProductCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var warehousePrices = productCodes.Count == 0
                ? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
                : (await _db.Queryable<WarehouseProduct>()
                    .Where(item =>
                        productCodes.Contains(item.ProductCode) && !item.IsDeleted
                    )
                    .Select(item => new { item.ProductCode, item.ImportPrice })
                    .ToListAsync())
                .Where(item => !string.IsNullOrWhiteSpace(item.ProductCode))
                .ToDictionary(
                    item => item.ProductCode,
                    item => item.ImportPrice ?? 0,
                    StringComparer.OrdinalIgnoreCase
                );

            var now = DateTime.Now;
            var unchangedCount = 0;
            var skippedCount = 0;
            var missingWarehousePriceCount = 0;
            var changedDetails = new List<WareHouseOrderDetails>();
            foreach (var detail in details)
            {
                if (
                    string.IsNullOrWhiteSpace(detail.ProductCode)
                    || !warehousePrices.TryGetValue(
                        detail.ProductCode,
                        out var warehouseImportPrice
                    )
                    || warehouseImportPrice <= 0
                )
                {
                    skippedCount += 1;
                    missingWarehousePriceCount += 1;
                    continue;
                }

                var expectedImportAmount = StoreOrderManagementRules.CalculateOrderImportAmount(
                    detail.Quantity,
                    warehouseImportPrice
                );
                var importPriceMatches =
                    detail.ImportPrice.HasValue
                    && detail.ImportPrice.Value == warehouseImportPrice;
                var importAmountMatches =
                    detail.ImportAmount.HasValue
                    && detail.ImportAmount.Value == expectedImportAmount;
                if (importPriceMatches && importAmountMatches)
                {
                    unchangedCount += 1;
                    continue;
                }

                detail.ImportPrice = warehouseImportPrice;
                detail.ImportAmount = expectedImportAmount;
                detail.UpdatedAt = now;
                detail.UpdatedBy = actorContext.ActorName;
                changedDetails.Add(detail);
            }

            if (changedDetails.Count > 0)
            {
                await _db.Updateable(changedDetails).ExecuteCommandAsync();
                await persistence.UpdateOrderTotalAsync(input.OrderGuid);
            }

            return StoreOrderManagementResult<RefreshOrderLineImportPricesResult>.Ok(
                new RefreshOrderLineImportPricesResult(
                    changedDetails.Count,
                    unchangedCount,
                    skippedCount,
                    missingWarehousePriceCount
                )
            );
        });
    }

    private async Task<StoreOrderManagementResult<bool>> UpdateOrderLinesByDetailGuidAsync(
        WareHouseOrder order,
        BatchUpdateOrderLineInput input
    )
    {
        var detailGuids = input.Items.Select(item => item.DetailGuid!.Trim()).ToList();
        if (detailGuids.Count != detailGuids.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            return StoreOrderManagementResult<bool>.Fail(
                "Duplicate order line detailGUID"
            );
        }

        var details = await _db.Queryable<WareHouseOrderDetails>()
            .Where(detail =>
                detail.OrderGUID == order.OrderGUID
                && detailGuids.Contains(detail.DetailGUID)
                && !detail.IsDeleted
            )
            .ToListAsync();
        if (details.Count != detailGuids.Count)
        {
            return StoreOrderManagementResult<bool>.Fail(
                "Some order lines were not found"
            );
        }

        var now = DateTime.Now;
        var detailMap = details.ToDictionary(
            detail => detail.DetailGUID,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var item in input.Items)
        {
            var detail = detailMap[item.DetailGuid!.Trim()];
            var allocatedQuantity = item.Quantity!.Value;
            // 发货数量为负 = 退货行：必须持权限、订货数量为 0、带有效原因；规则失败抛异常，整批回滚。
            // 已有退货行被改回 0 / 正数同样需要权限（防止无权限者抹掉退货）。
            var returnDecision = await persistence.EvaluateReturnWriteAsync(
                allocatedQuantity,
                detail,
                item.ReturnRequest
            );
            detail.AllocQuantity = allocatedQuantity;
            detail.ReturnReason = returnDecision.Reason;
            detail.ReturnDisposition = returnDecision.Disposition;
            detail.ReturnNote = returnDecision.Note;
            detail.OEMAmount = allocatedQuantity * (detail.OEMPrice ?? 0);
            detail.ImportAmount = StoreOrderManagementRules.CalculateOrderImportAmount(
                detail.Quantity,
                detail.ImportPrice
            );
            detail.UpdatedAt = now;
            detail.UpdatedBy = actorContext.ActorName;
            if (
                StoreOrderManagementRules.ShouldSoftDelete(
                    detail.Quantity,
                    allocatedQuantity
                )
            )
            {
                detail.IsDeleted = true;
                detail.ReturnReason = null;
                detail.ReturnDisposition = null;
                detail.ReturnNote = null;
            }
        }

        await _db.Updateable(details).ExecuteCommandAsync();
        await persistence.UpdateOrderTotalAsync(order.OrderGUID);
        return StoreOrderManagementResult<bool>.Ok(true);
    }
}
