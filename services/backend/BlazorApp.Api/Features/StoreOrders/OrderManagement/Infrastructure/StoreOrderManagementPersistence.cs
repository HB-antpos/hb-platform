using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Domain;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.OrderManagement.Infrastructure;

internal sealed class StoreOrderManagementPersistence(
    SqlSugarContext context,
    IStoreOrderActorContext actorContext,
    IStoreOrderProductCostCoordinator productCostCoordinator,
    IStoreOrderReturnPermission? returnPermission = null
)
{
    private readonly ISqlSugarClient _db = context.Db;

    /// <summary>
    /// 当前账号能否录入 / 修改 / 删除退货行。权限判定未注入（旧构造路径、测试）时按「无权限」处理，只会更严。
    /// </summary>
    internal async Task<bool> CanRecordReturnsAsync()
    {
        return returnPermission != null && await returnPermission.CanRecordReturnsAsync();
    }

    internal async Task<WareHouseOrder?> GetEditableOrderAsync(string orderGuid)
    {
        var order = await _db.Queryable<WareHouseOrder>()
            .Where(item => item.OrderGUID == orderGuid && !item.IsDeleted)
            .FirstAsync();

        return order != null && StoreOrderManagementRules.IsEditableOrder(order)
            ? order
            : null;
    }

    internal async Task<WareHouseOrder?> GetOrderAsync(string orderGuid)
    {
        return await _db.Queryable<WareHouseOrder>()
            .Where(item => item.OrderGUID == orderGuid && !item.IsDeleted)
            .FirstAsync();
    }

    /// <summary>
    /// 新增 / 更新订单明细。quantity 是「发货数量」（接口字段名仍叫 quantity）：
    /// 单笔 / 批量新增（isUpdate=false）里 quantity &gt;= 0 时沿用旧语义（按最小订货量累加，quantity 本身不参与）；
    /// quantity &lt; 0 表示录入退货行，按「设置」处理（幂等，重复提交不会叠加）。
    /// 退货行规则见 <see cref="StoreOrderReturnRules.Evaluate"/>；校验失败抛 <see cref="StoreOrderReturnRuleException"/>，
    /// 由行命令存储转成失败结果并回滚事务。
    /// </summary>
    internal async Task AddOrUpdateDetailAsync(
        WareHouseOrder order,
        string productCode,
        decimal quantity,
        decimal? importPrice,
        bool isUpdate,
        bool isBatch = false,
        decimal? originalQuantity = null,
        StoreOrderReturnLineRequest returnRequest = default
    )
    {
        var now = DateTime.Now;
        var currentUser = actorContext.ActorName;
        var warehouseProduct = await _db.Queryable<WarehouseProduct>()
            .Where(item => item.ProductCode == productCode)
            .FirstAsync();

        if (warehouseProduct == null)
        {
            var product = await _db.Queryable<Product>()
                .Where(item => item.ProductCode == productCode)
                .FirstAsync();
            if (product == null)
            {
                throw new Exception($"Product {productCode} not found");
            }

            warehouseProduct = new WarehouseProduct
            {
                OEMPrice = 0,
                ImportPrice = 0,
                MinOrderQuantity = 1,
            };
        }

        var minimumOrderQuantity = StoreOrderManagementRules.NormalizeMinimumOrderQuantity(
            warehouseProduct.MinOrderQuantity
        );
        // 本次是否改动发货数量：更新（非批量，或批量里明确传了 quantity）会改；批量里只改价格时不改。
        var changesAllocQuantity = isUpdate && (!isBatch || originalQuantity.HasValue);
        // 非更新路径里 quantity < 0 = 录入退货行；更新路径里 quantity < 0 同样是退货（权限等由规则判定）。
        var isReturnWrite = quantity < 0 && (changesAllocQuantity || !isUpdate);

        var oemPrice = warehouseProduct.OEMPrice ?? 0;
        var finalImportPrice = importPrice ?? warehouseProduct.ImportPrice ?? 0;
        var detail = await _db.Queryable<WareHouseOrderDetails>()
            .Where(item =>
                item.OrderGUID == order.OrderGUID
                && item.ProductCode == productCode
                && !item.IsDeleted
            )
            .FirstAsync();

        if (detail == null)
        {
            // 本单还没有该商品。退货行：订货数量 0、发货数量为负，进口价默认取该店该商品上次正发货的价格。
            // 普通行：沿用旧行为，发货数量默认取最小订货量。
            var newAllocQuantity = isReturnWrite ? quantity : minimumOrderQuantity;
            var decision = await EvaluateReturnWriteAsync(
                newAllocQuantity,
                existing: null,
                returnRequest
            );

            if (decision.IsReturnLine && !importPrice.HasValue)
            {
                finalImportPrice =
                    await ResolveLastShippedImportPriceAsync(order, productCode)
                    ?? warehouseProduct.ImportPrice
                    ?? 0;
            }

            detail = new WareHouseOrderDetails
            {
                DetailGUID = UuidHelper.GenerateUuid7(),
                OrderGUID = order.OrderGUID,
                StoreCode = order.StoreCode,
                ProductCode = productCode,
                Quantity = 0,
                OEMPrice = oemPrice,
                // 金额口径与已有明细一致：OEMAmount 按发货数量，退货行因此为负数（冲减）。
                OEMAmount = oemPrice * (decision.IsReturnLine ? quantity : minimumOrderQuantity),
                AllocQuantity = newAllocQuantity,
                ImportPrice = finalImportPrice,
                ImportAmount = 0,
                ReturnReason = decision.Reason,
                ReturnDisposition = decision.Disposition,
                ReturnNote = decision.Note,
                IsDeleted = false,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = currentUser,
                UpdatedBy = currentUser,
            };
            await _db.Insertable(detail).ExecuteCommandAsync();
            return;
        }

        decimal? nextAllocQuantity;
        if (isReturnWrite || changesAllocQuantity)
        {
            nextAllocQuantity = quantity;
        }
        else if (isUpdate)
        {
            // 批量里只改价格：发货数量不变，但仍要过退货规则（无权限者不能借改价格动退货行）。
            nextAllocQuantity = detail.AllocQuantity;
        }
        else
        {
            // 旧的「加入」语义是在发货数量上加最小订货量；退货行上不能这么加（会悄悄抵消退货）。
            if (StoreOrderReturnRules.IsReturnLine(detail.AllocQuantity))
            {
                throw new StoreOrderReturnRuleException(
                    "该商品在本单是退货行，不能再加入发货；请先修改或删除退货行"
                );
            }

            nextAllocQuantity = detail.AllocQuantity + minimumOrderQuantity;
        }

        // nextAllocQuantity 为 NULL（历史脏数据）时按 0 处理，不触发退货规则。
        var returnDecision = await EvaluateReturnWriteAsync(
            nextAllocQuantity ?? 0,
            detail,
            returnRequest
        );

        detail.AllocQuantity = nextAllocQuantity;
        if (isUpdate && importPrice.HasValue)
        {
            detail.ImportPrice = importPrice.Value;
        }

        if (
            StoreOrderManagementRules.ShouldSoftDeleteExistingDetail(
                detail.Quantity,
                detail.AllocQuantity
            )
        )
        {
            // 软删的行不再保留退货字段，避免日后被复用时带出陈旧原因。
            detail.ReturnReason = null;
            detail.ReturnDisposition = null;
            detail.ReturnNote = null;
            await SoftDeleteOrderDetailAsync(detail, currentUser, now);
            return;
        }

        detail.ReturnReason = returnDecision.Reason;
        detail.ReturnDisposition = returnDecision.Disposition;
        detail.ReturnNote = returnDecision.Note;
        detail.OEMAmount = detail.AllocQuantity * detail.OEMPrice;
        detail.ImportAmount = StoreOrderManagementRules.CalculateOrderImportAmount(
            detail.Quantity,
            detail.ImportPrice
        );
        detail.UpdatedAt = now;
        detail.UpdatedBy = currentUser;
        await _db.Updateable(detail).ExecuteCommandAsync();
    }

    /// <summary>
    /// 退货行写入判定的统一入口：只有真正涉及退货（写入负数、或动到已有退货行）时才去查权限，
    /// 普通订货编辑不会多一次授权查询。校验失败抛 <see cref="StoreOrderReturnRuleException"/>。
    /// </summary>
    internal async Task<StoreOrderReturnWriteDecision> EvaluateReturnWriteAsync(
        decimal newAllocQuantity,
        WareHouseOrderDetails? existing,
        StoreOrderReturnLineRequest returnRequest
    )
    {
        var touchesReturn =
            newAllocQuantity < 0 || StoreOrderReturnRules.IsReturnLine(existing?.AllocQuantity);
        var canRecordReturns = touchesReturn && await CanRecordReturnsAsync();
        var decision = StoreOrderReturnRules.Evaluate(
            canRecordReturns,
            newAllocQuantity,
            existing?.Quantity,
            existing?.AllocQuantity,
            existing?.ReturnReason,
            existing?.ReturnDisposition,
            existing?.ReturnNote,
            returnRequest
        );
        if (decision.Error != null)
        {
            throw new StoreOrderReturnRuleException(decision.Error);
        }

        return decision;
    }

    /// <summary>
    /// 该店该商品最近一次「正发货」的进口价（退货按原价冲减）。取不到返回 null，调用方再回落商品进口价。
    /// 注意：条件只用 &gt; 0 / &gt; 0 的直接比较，不写 x.Col.HasValue &amp;&amp; …（会翻译成 [Col]&lt;&gt;''，SQL Server 把 '' 当 0，
    /// 值为 0 的行会被误排除，SQLite 测试发现不了，见 #904）。
    /// </summary>
    internal async Task<decimal?> ResolveLastShippedImportPriceAsync(
        WareHouseOrder order,
        string productCode
    )
    {
        var storeCode = order.StoreCode;
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return null;
        }

        var lastPrice = await BuildLastShippedImportPriceQuery(
                _db,
                storeCode,
                productCode,
                order.OrderGUID
            )
            .FirstAsync();
        return lastPrice != null && lastPrice.ImportPrice > 0 ? lastPrice.ImportPrice : null;
    }

    /// <summary>最近一次正发货明细的进口价查询（单独成方法，便于做 SQL Server 方言 SQL 文本断言）。</summary>
    internal static ISugarQueryable<LastShippedImportPriceRow> BuildLastShippedImportPriceQuery(
        ISqlSugarClient db,
        string storeCode,
        string productCode,
        string excludedOrderGuid
    )
    {
        return db.Queryable<WareHouseOrderDetails>()
            .Where(item =>
                item.StoreCode == storeCode
                && item.ProductCode == productCode
                && item.OrderGUID != excludedOrderGuid
                && !item.IsDeleted
                && item.AllocQuantity > 0
                && item.ImportPrice > 0
            )
            .OrderBy(item => item.CreatedAt, OrderByType.Desc)
            .Take(1)
            .Select(item => new LastShippedImportPriceRow { ImportPrice = item.ImportPrice ?? 0 });
    }

    internal sealed class LastShippedImportPriceRow
    {
        public decimal ImportPrice { get; set; }
    }

    internal async Task UpdateOrderTotalAsync(string orderGuid)
    {
        var summary = await _db.Queryable<WareHouseOrderDetails>()
            .Where(item => item.OrderGUID == orderGuid && !item.IsDeleted)
            .Select(item => new StoreOrderTotalsRow
            {
                TotalQuantity = SqlFunc.AggregateSum(item.Quantity ?? 0),
                TotalSku = SqlFunc.AggregateDistinctCount(item.ProductCode),
                TotalAmount = SqlFunc.AggregateSum(item.OEMAmount ?? 0),
                TotalImportAmount = SqlFunc.AggregateSum(item.ImportAmount ?? 0),
            })
            .FirstAsync();

        var totalAmount = summary?.TotalAmount ?? 0;
        var totalImportAmount = summary?.TotalImportAmount ?? 0;
        var revisionAt = StoreOrderManagementRules.ResolveOrderRevisionAt(DateTime.Now);
        await _db.Updateable<WareHouseOrder>()
            .SetColumns(item => new WareHouseOrder
            {
                OEMTotalAmount = totalAmount,
                ImportTotalAmount = totalImportAmount,
                UpdatedAt = revisionAt,
            })
            .Where(item => item.OrderGUID == orderGuid)
            .ExecuteCommandAsync();
    }

    internal async Task SyncImportPriceToProductTablesAsync(
        string productCode,
        decimal importPrice
    )
    {
        var normalizedProductCode = productCode.Trim();
        if (string.IsNullOrWhiteSpace(normalizedProductCode) || importPrice <= 0)
        {
            return;
        }

        var now = DateTime.Now;
        var currentUser = actorContext.ActorName;
        var product = await productCostCoordinator
            .WithUpdateLock(
                _db.Queryable<Product>()
                    .Where(item =>
                        item.ProductCode == normalizedProductCode && !item.IsDeleted
                    )
            )
            .FirstAsync();
        if (product == null)
        {
            throw new Exception($"Product {normalizedProductCode} not found");
        }

        await _db.Updateable<Product>()
            .SetColumns(item => item.PurchasePrice == importPrice)
            .SetColumns(item => item.UpdatedAt == now)
            .SetColumns(item => item.UpdatedBy == currentUser)
            .Where(item => item.ProductCode == normalizedProductCode && !item.IsDeleted)
            .ExecuteCommandAsync();

        var warehouseProduct = await productCostCoordinator
            .WithUpdateLock(
                _db.Queryable<WarehouseProduct>()
                    .Where(item =>
                        item.ProductCode == normalizedProductCode && !item.IsDeleted
                    )
            )
            .FirstAsync();
        if (warehouseProduct == null)
        {
            warehouseProduct = new WarehouseProduct
            {
                ProductCode = normalizedProductCode,
                OEMPrice = 0,
                ImportPrice = importPrice,
                MinOrderQuantity = 1,
                IsActive = product.IsActive,
                CreatedAt = now,
                CreatedBy = currentUser,
                UpdatedAt = now,
                UpdatedBy = currentUser,
                IsDeleted = false,
            };
            await _db.Insertable(warehouseProduct).ExecuteCommandAsync();
        }
        else
        {
            await _db.Updateable<WarehouseProduct>()
                .SetColumns(item => item.ImportPrice == importPrice)
                .SetColumns(item => item.UpdatedAt == now)
                .SetColumns(item => item.UpdatedBy == currentUser)
                .Where(item =>
                    item.ProductCode == normalizedProductCode && !item.IsDeleted
                )
                .ExecuteCommandAsync();
        }

        product = await productCostCoordinator
            .WithUpdateLock(
                _db.Queryable<Product>()
                    .Where(item =>
                        item.ProductCode == normalizedProductCode && !item.IsDeleted
                    )
            )
            .FirstAsync();
        if (product == null)
        {
            throw new Exception($"Product {normalizedProductCode} not found after update");
        }

        await UpsertActiveStoreRetailPurchasePricesAsync(
            product,
            normalizedProductCode,
            importPrice,
            now,
            currentUser
        );
    }

    private async Task UpsertActiveStoreRetailPurchasePricesAsync(
        Product product,
        string productCode,
        decimal importPrice,
        DateTime now,
        string currentUser
    )
    {
        var activeStoreCodes = (await _db.Queryable<Store>()
                .Where(store => store.IsActive && !store.IsDeleted)
                .Select(store => store.StoreCode)
                .ToListAsync())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (activeStoreCodes.Count == 0)
        {
            return;
        }

        var existingPrices = await _db.Queryable<StoreRetailPrice>()
            .Where(price =>
                price.ProductCode == productCode
                && price.StoreCode != null
                && activeStoreCodes.Contains(price.StoreCode)
                && !price.IsDeleted
            )
            .ToListAsync();
        var existingStoreCodes = existingPrices
            .Select(price => price.StoreCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var price in existingPrices)
        {
            price.PurchasePrice = importPrice;
            price.UpdatedAt = now;
            price.UpdatedBy = currentUser;
        }

        if (existingPrices.Count > 0)
        {
            await _db.Updateable(existingPrices)
                .UpdateColumns(price => new
                {
                    price.PurchasePrice,
                    price.UpdatedAt,
                    price.UpdatedBy,
                })
                .ExecuteCommandAsync();
        }

        var pricesToInsert = activeStoreCodes
            .Where(storeCode => !existingStoreCodes.Contains(storeCode))
            .Select(storeCode => new StoreRetailPrice
            {
                UUID = UuidHelper.GenerateUuid7(),
                StoreCode = storeCode,
                ProductCode = productCode,
                StoreProductCode = storeCode + productCode,
                SupplierCode = product.LocalSupplierCode,
                PurchasePrice = importPrice,
                StoreRetailPriceValue = product.RetailPrice,
                DiscountRate = null,
                IsActive = product.IsActive,
                IsAutoPricing = product.IsAutoPricing,
                IsSpecialProduct = product.IsSpecialProduct,
                CreatedAt = now,
                CreatedBy = currentUser,
                UpdatedAt = now,
                UpdatedBy = currentUser,
                IsDeleted = false,
            })
            .ToList();

        if (pricesToInsert.Count > 0)
        {
            await _db.Insertable(pricesToInsert).ExecuteCommandAsync();
        }
    }

    private async Task SoftDeleteOrderDetailAsync(
        WareHouseOrderDetails detail,
        string currentUser,
        DateTime now
    )
    {
        detail.IsDeleted = true;
        detail.UpdatedAt = now;
        detail.UpdatedBy = currentUser;
        await _db.Updateable(detail).ExecuteCommandAsync();
    }

    private sealed class StoreOrderTotalsRow
    {
        public decimal TotalQuantity { get; set; }

        public int TotalSku { get; set; }

        public decimal TotalAmount { get; set; }

        public decimal TotalImportAmount { get; set; }
    }
}
