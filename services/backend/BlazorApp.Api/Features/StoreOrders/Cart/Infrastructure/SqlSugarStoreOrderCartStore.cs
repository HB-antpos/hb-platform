using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.Cart.Application.Ports;
using BlazorApp.Api.Features.StoreOrders.Cart.Domain;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.SupplyNotices;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.Cart.Infrastructure;

internal sealed class SqlSugarStoreOrderCartStore(
    SqlSugarContext context,
    IStoreOrderActorContext actorContext
)
    : IStoreOrderCartQueryStore,
        IStoreOrderCartCommandStore,
        IStoreOrderCartPlacementPort
{
    private readonly ISqlSugarClient _db = context.Db;

    public async Task<StoreOrderCartDto?> GetFullAsync(StoreOrderCartScope scope)
    {
        var order = await FindActiveCartAsync(scope);
        if (order == null)
        {
            return null;
        }

        var store = await GetStoreByCodeOrGuidAsync(order.StoreCode);
        var details = await _db.Queryable<WareHouseOrderDetails>()
            .LeftJoin<Product>((detail, product) => detail.ProductCode == product.ProductCode)
            .LeftJoin<WarehouseProduct>(
                (detail, product, warehouseProduct) =>
                    detail.ProductCode == warehouseProduct.ProductCode
            )
            .LeftJoin<DomesticProduct>(
                (detail, product, warehouseProduct, domesticProduct) =>
                    warehouseProduct.ProductCode == domesticProduct.ProductCode
            )
            .LeftJoin<ProductGrade>(
                (detail, product, warehouseProduct, domesticProduct, grade) =>
                    detail.ProductCode == grade.ProductCode && !grade.IsDeleted
            )
            .Where(detail => detail.OrderGUID == order.OrderGUID && !detail.IsDeleted)
            .Select(
                (detail, product, warehouseProduct, domesticProduct, grade) =>
                    new StoreOrderCartItemDto
                    {
                        DetailGUID = detail.DetailGUID,
                        ProductCode = detail.ProductCode ?? string.Empty,
                        ItemNumber = product.ItemNumber,
                        Barcode = product.Barcode,
                        Grade = grade.Grade,
                        ProductName = product.ProductName,
                        ProductImage = product.ProductImage,
                        // 仓库是否仍在供货；false 时前端标明“已暂停供货”。左连接缺行按不可订处理。
                        IsActive = SqlFunc.IsNull(warehouseProduct.IsActive, false),
                        Price = detail.OEMPrice ?? 0,
                        Quantity = detail.Quantity ?? 0,
                        AllocQuantity = detail.AllocQuantity,
                        Amount = detail.OEMAmount ?? 0,
                        ImportPrice = detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0),
                        ImportAmount =
                            detail.ImportAmount
                            ?? (
                                (detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0))
                                * (detail.Quantity ?? 0)
                            ),
                        AllocatedImportAmount =
                            (detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0))
                            * (detail.AllocQuantity ?? 0),
                        Volume = domesticProduct.PackingQuantity > 0
                            ? domesticProduct.UnitVolume / domesticProduct.PackingQuantity
                            : domesticProduct.UnitVolume,
                        MinOrderQuantity = warehouseProduct.MinOrderQuantity ?? 1,
                    }
            )
            .ToListAsync();

        await ApplySupplyPlansAsync(details);
        foreach (var item in details)
        {
            if (!item.Volume.HasValue)
            {
                continue;
            }

            item.OrderVolume = StoreOrderCartRules.CalculateVolume(item.Volume, item.Quantity);
            item.AllocVolume = StoreOrderCartRules.CalculateVolume(
                item.Volume,
                item.AllocQuantity ?? 0
            );
            item.TotalVolume = item.OrderVolume;
        }

        return new StoreOrderCartDto
        {
            OrderGUID = order.OrderGUID,
            OrderNo = order.OrderNo,
            StoreCode = order.StoreCode,
            TotalAmount = order.OEMTotalAmount ?? 0,
            TotalQuantity = (int)details.Sum(item => item.Quantity),
            TotalSKU = details
                .Select(item => item.ProductCode)
                .Where(productCode => !string.IsNullOrWhiteSpace(productCode))
                .Distinct()
                .Count(),
            TotalImportAmount = details.Sum(item => item.ImportAmount),
            TotalAllocatedImportAmount = details.Sum(item => item.AllocatedImportAmount),
            TotalVolume = details.Sum(item => item.TotalVolume ?? 0),
            TotalOrderVolume = details.Sum(item => item.OrderVolume ?? 0),
            TotalAllocVolume = details.Sum(item => item.AllocVolume ?? 0),
            Remarks = order.Remarks,
            StoreAddress = store?.Address,
            StoreContactEmail = store?.ContactEmail,
            ShippingFee = order.ShippingFee,
            OrderDate = order.OrderDate,
            TotalAllocQuantity = (int)details.Sum(item => item.AllocQuantity ?? 0),
            FlowStatus = order.FlowStatus,
            Items = details,
        };
    }

    public async Task<StoreOrderCartDto?> GetSummaryAsync(StoreOrderCartScope scope)
    {
        var order = await FindActiveCartAsync(scope);
        if (order == null)
        {
            return null;
        }

        var store = await GetStoreByCodeOrGuidAsync(order.StoreCode);
        var detailRows = await _db.Queryable<WareHouseOrderDetails>()
            .LeftJoin<DomesticProduct>(
                (detail, domesticProduct) =>
                    detail.ProductCode == domesticProduct.ProductCode
            )
            .Where(detail => detail.OrderGUID == order.OrderGUID && !detail.IsDeleted)
            .Select(
                (detail, domesticProduct) =>
                    new CartSummaryReadRow
                    {
                        ProductCode = detail.ProductCode,
                        Quantity = detail.Quantity ?? 0,
                        AllocQuantity = detail.AllocQuantity ?? 0,
                        ImportAmount =
                            detail.ImportAmount
                            ?? ((detail.ImportPrice ?? 0) * (detail.Quantity ?? 0)),
                        AllocatedImportAmount =
                            (detail.ImportPrice ?? 0) * (detail.AllocQuantity ?? 0),
                        UnitVolume = domesticProduct.PackingQuantity > 0
                            ? domesticProduct.UnitVolume / domesticProduct.PackingQuantity
                            : domesticProduct.UnitVolume,
                    }
            )
            .ToListAsync();

        var totalVolume = detailRows.Sum(row => (row.UnitVolume ?? 0) * row.Quantity);
        var totalAllocVolume = detailRows.Sum(
            row => (row.UnitVolume ?? 0) * row.AllocQuantity
        );

        return new StoreOrderCartDto
        {
            OrderGUID = order.OrderGUID,
            OrderNo = order.OrderNo,
            StoreCode = order.StoreCode,
            TotalAmount = order.OEMTotalAmount ?? 0,
            TotalQuantity = (int)detailRows.Sum(row => row.Quantity),
            TotalImportAmount = detailRows.Sum(row => row.ImportAmount),
            TotalAllocatedImportAmount = detailRows.Sum(row => row.AllocatedImportAmount),
            TotalVolume = totalVolume,
            TotalOrderVolume = totalVolume,
            TotalAllocVolume = totalAllocVolume,
            Remarks = order.Remarks,
            StoreAddress = store?.Address,
            StoreContactEmail = store?.ContactEmail,
            ShippingFee = order.ShippingFee,
            OrderDate = order.OrderDate,
            TotalAllocQuantity = (int)detailRows.Sum(row => row.AllocQuantity),
            TotalSKU = detailRows
                .Select(row => row.ProductCode)
                .Where(productCode => !string.IsNullOrWhiteSpace(productCode))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            FlowStatus = order.FlowStatus,
            // Summary 明确不返回商品行，不能被调用方误当成 full cart。
            Items = new List<StoreOrderCartItemDto>(),
        };
    }

    public async Task<StoreOrderCartMutationResultDto> GetMutationResultAsync(
        StoreOrderCartMutationWrite write
    )
    {
        StoreOrderCartItemDto? changedItem = null;
        if (!write.Removed && !string.IsNullOrWhiteSpace(write.OrderGuid))
        {
            changedItem = await QueryChangedItemAsync(
                write.OrderGuid,
                write.ProductCode,
                write.DetailGuid
            );
        }

        return new StoreOrderCartMutationResultDto
        {
            ProductCode = write.ProductCode,
            Removed = write.Removed || changedItem == null,
            Summary = new StoreOrderCartMutationSummaryDto
            {
                OrderGUID = write.OrderGuid,
                StoreCode = write.StoreCode,
                TotalAmount = write.Summary.TotalAmount,
                TotalImportAmount = write.Summary.TotalImportAmount,
                TotalQuantity = (int)write.Summary.TotalQuantity,
                TotalSku = write.Summary.TotalSku,
                CartRevision = write.Summary.CartRevision,
            },
            ChangedItem = changedItem,
        };
    }

    public Task<StoreOrderCartMutationOutcome> AddAsync(
        StoreOrderCartScope scope,
        string productCode,
        decimal quantity,
        StoreOrderProductDto? knownProduct,
        bool omitNonPositiveNewDetail
    )
    {
        return AddCoreAsync(
            scope,
            productCode,
            quantity,
            knownProduct,
            omitNonPositiveNewDetail
        );
    }

    public async Task<StoreOrderCartMutationOutcome> SetQuantityAsync(
        StoreOrderCartScope scope,
        string productCode,
        decimal quantity,
        bool omitNonPositiveNewDetail
    )
    {
        var order = await FindActiveCartAsync(scope);
        if (order == null)
        {
            if (omitNonPositiveNewDetail && quantity <= 0)
            {
                return StoreOrderCartMutationOutcome.Completed(
                    EmptyMutation(scope, productCode)
                );
            }

            return await AddCoreAsync(
                scope,
                productCode,
                quantity,
                null,
                omitNonPositiveNewDetail
            );
        }

        var warehouseProduct = await _db.Queryable<WarehouseProduct>()
            .Where(product => product.ProductCode == productCode)
            .FirstAsync();
        if (warehouseProduct == null)
        {
            return StoreOrderCartMutationOutcome.ProductMissing();
        }

        var now = DateTime.Now;
        var actor = ResolveActorName();
        var detail = await FindActiveDetailAsync(order.OrderGUID, productCode);
        // 暂停供货的商品：分店侧不能新增或加量；减量、移除放行，让分店能把这行清掉。
        if (
            !warehouseProduct.IsActive
            && quantity > (detail?.Quantity ?? 0)
            && !StoreOrderSupplyGuard.CanOrderPausedProducts(actorContext)
        )
        {
            return StoreOrderCartMutationOutcome.SupplyPaused();
        }
        var removed = false;
        var detailGuid = detail?.DetailGUID;
        if (detail == null)
        {
            if (omitNonPositiveNewDetail && quantity <= 0)
            {
                removed = true;
            }
            else
            {
                detail = CreateDetail(
                    order.OrderGUID,
                    scope.StoreCode,
                    productCode,
                    quantity,
                    warehouseProduct.OEMPrice ?? 0,
                    warehouseProduct.ImportPrice ?? 0,
                    now,
                    actor
                );
                detailGuid = detail.DetailGUID;
                await _db.Insertable(detail).ExecuteCommandAsync();
            }
        }
        else
        {
            detail.Quantity = quantity;
            if (detail.Quantity <= 0)
            {
                removed = true;
                await SoftDeleteAsync(detail, actor, now);
            }
            else
            {
                UpdateDetailAmounts(detail, now, actor);
                await _db.Updateable(detail).ExecuteCommandAsync();
            }
        }

        var summary = await RecalculateAsync(order.OrderGUID, order.UpdatedAt);
        return StoreOrderCartMutationOutcome.Completed(
            new StoreOrderCartMutationWrite(
                order.OrderGUID,
                scope.StoreCode,
                productCode,
                detailGuid,
                removed,
                summary
            )
        );
    }

    public async Task<bool> RemoveAsync(StoreOrderCartScope scope, string detailGuid)
    {
        var order = await FindActiveCartAsync(scope);
        if (order == null)
        {
            return false;
        }

        var detail = await _db.Queryable<WareHouseOrderDetails>()
            .Where(candidate =>
                candidate.DetailGUID == detailGuid
                && candidate.OrderGUID == order.OrderGUID
                && candidate.StoreCode == scope.StoreCode
                && !candidate.IsDeleted
            )
            .FirstAsync();
        if (detail == null)
        {
            return false;
        }

        await SoftDeleteAsync(detail, ResolveActorName(), DateTime.Now);
        await RecalculateAsync(order.OrderGUID, order.UpdatedAt);
        return true;
    }

    public async Task<StoreOrderCartClearOutcome> ClearAsync(StoreOrderCartScope scope)
    {
        var cart = await FindActiveCartAsync(scope);
        if (cart == null)
        {
            return new StoreOrderCartClearOutcome(false);
        }

        await _db.Deleteable<WareHouseOrderDetails>()
            .Where(detail => detail.OrderGUID == cart.OrderGUID)
            .ExecuteCommandAsync();
        await _db.Deleteable<WareHouseOrder>()
            .Where(order => order.OrderGUID == cart.OrderGUID)
            .ExecuteCommandAsync();
        return new StoreOrderCartClearOutcome(true);
    }

    public async Task<StoreOrderCartSubmissionSnapshot?> GetActiveForSubmissionAsync(
        StoreOrderCartScope scope
    )
    {
        var order = await FindActiveCartAsync(scope);
        return order == null
            ? null
            : new StoreOrderCartSubmissionSnapshot(
                order.OrderGUID,
                order.FlowStatus,
                order.UpdatedAt
            );
    }

    public Task<int> CountActiveItemsAsync(string orderGuid)
    {
        return _db.Queryable<WareHouseOrderDetails>()
            .Where(detail => detail.OrderGUID == orderGuid && !detail.IsDeleted)
            .CountAsync();
    }

    public async Task<IReadOnlyList<StoreOrderSupplyPausedLine>> GetSupplyPausedLinesAsync(
        string orderGuid
    )
    {
        if (StoreOrderSupplyGuard.CanOrderPausedProducts(actorContext))
        {
            return Array.Empty<StoreOrderSupplyPausedLine>();
        }

        // 加购之后才被下架的商品会留在购物车里，提交时必须再查一次当前状态。
        var rows = await _db.Queryable<WareHouseOrderDetails>()
            .InnerJoin<WarehouseProduct>(
                (detail, warehouseProduct) => detail.ProductCode == warehouseProduct.ProductCode
            )
            .LeftJoin<Product>(
                (detail, warehouseProduct, product) => detail.ProductCode == product.ProductCode
            )
            .Where(
                (detail, warehouseProduct, product) =>
                    detail.OrderGUID == orderGuid
                    && !detail.IsDeleted
                    && !warehouseProduct.IsDeleted
                    && !warehouseProduct.IsActive
            )
            .Select(
                (detail, warehouseProduct, product) =>
                    new SupplyPausedLineRow
                    {
                        DetailGuid = detail.DetailGUID,
                        ProductCode = detail.ProductCode,
                        ItemNumber = product.ItemNumber,
                        ProductName = product.ProductName,
                        Quantity = detail.Quantity ?? 0,
                    }
            )
            .ToListAsync();

        var supplyPlans = await LoadOpenSupplyPlansAsync(
            rows.Select(row => row.ProductCode).ToList()
        );
        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ProductCode))
            .Select(row => new StoreOrderSupplyPausedLine(
                row.DetailGuid ?? string.Empty,
                row.ProductCode!,
                row.ItemNumber,
                row.ProductName,
                row.Quantity,
                supplyPlans.GetValueOrDefault(row.ProductCode!)
            ))
            .ToList();
    }

    public async Task<StoreOrderCartSplitResult> MoveLinesToNewCartAsync(
        StoreOrderCartScope scope,
        StoreOrderCartSubmissionSnapshot source,
        IReadOnlyCollection<string> detailGuids,
        DateTime now,
        string actor
    )
    {
        var guids = detailGuids.Where(guid => !string.IsNullOrWhiteSpace(guid)).Distinct().ToList();
        if (guids.Count == 0)
        {
            throw new InvalidOperationException("No cart lines to move.");
        }

        // 先建新车头再改明细归属，保证任何时刻明细都指向存在的表头。
        var newCart = CreateCartHeader(scope, now, actor);
        await _db.Insertable(newCart).ExecuteCommandAsync();

        var moved = await _db.Updateable<WareHouseOrderDetails>()
            .SetColumns(detail => new WareHouseOrderDetails
            {
                OrderGUID = newCart.OrderGUID,
                UpdatedAt = now,
                UpdatedBy = actor,
            })
            .Where(detail =>
                detail.OrderGUID == source.OrderGuid
                && !detail.IsDeleted
                && guids.Contains(detail.DetailGUID)
            )
            .ExecuteCommandAsync();
        if (moved != guids.Count)
        {
            throw new InvalidOperationException(
                $"Expected to move {guids.Count} cart lines but moved {moved}."
            );
        }

        // 新车版本号以旧车为基准递增，移动端按门店比较 cartRevision 时不会把新车当成旧数据。
        var newSummary = await RecalculateAsync(newCart.OrderGUID, source.UpdatedAt);
        await RecalculateAsync(source.OrderGuid, source.UpdatedAt);
        return new StoreOrderCartSplitResult(newCart.OrderGUID, moved, newSummary.CartRevision);
    }

    public Task<int> CompareExchangeSubmitAsync(
        StoreOrderCartSubmissionSnapshot snapshot,
        string orderNo,
        string? remarks,
        DateTime submittedAt,
        string submittedBy
    )
    {
        return _db.Updateable<WareHouseOrder>()
            .SetColumns(order => new WareHouseOrder
            {
                FlowStatus = 1,
                Remarks = remarks,
                OrderDate = submittedAt,
                UpdatedAt = submittedAt,
                UpdatedBy = submittedBy,
                OrderNo = orderNo,
            })
            .Where(order =>
                order.OrderGUID == snapshot.OrderGuid
                && !order.IsDeleted
                && order.FlowStatus == snapshot.FlowStatus
            )
            .ExecuteCommandAsync();
    }

    private async Task<StoreOrderCartMutationOutcome> AddCoreAsync(
        StoreOrderCartScope scope,
        string productCode,
        decimal quantity,
        StoreOrderProductDto? knownProduct,
        bool omitNonPositiveNewDetail
    )
    {
        var now = DateTime.Now;
        var actor = ResolveActorName();
        var order = await FindActiveCartAsync(scope);
        if (order == null)
        {
            order = CreateCartHeader(scope, now, actor);
            await _db.Insertable(order).ExecuteCommandAsync();
        }

        decimal price;
        decimal importPrice;
        if (
            knownProduct != null
            && string.Equals(
                knownProduct.ProductCode,
                productCode,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            price = knownProduct.OEMPrice ?? 0;
            importPrice = knownProduct.ImportPrice ?? 0;
        }
        else
        {
            var productPrice = await _db.Queryable<Product>()
                .InnerJoin<WarehouseProduct>(
                    (product, warehouseProduct) =>
                        product.ProductCode == warehouseProduct.ProductCode
                )
                .Where(
                    (product, warehouseProduct) => product.ProductCode == productCode
                )
                .Select(
                    (product, warehouseProduct) =>
                        new CartProductPriceRow
                        {
                            Price = warehouseProduct.OEMPrice,
                            ImportPrice = warehouseProduct.ImportPrice,
                            WarehouseIsActive = warehouseProduct.IsActive,
                        }
                )
                .FirstAsync();
            if (productPrice == null)
            {
                return StoreOrderCartMutationOutcome.ProductMissing();
            }

            // 加购一定是加量：暂停供货的商品分店侧一律拦截。
            // knownProduct 分支不用查：它来自订货选品查询，本身只返回在供货的商品。
            if (
                !productPrice.WarehouseIsActive
                && !StoreOrderSupplyGuard.CanOrderPausedProducts(actorContext)
            )
            {
                return StoreOrderCartMutationOutcome.SupplyPaused();
            }

            price = productPrice.Price ?? 0;
            importPrice = productPrice.ImportPrice ?? 0;
        }

        var detail = await FindActiveDetailAsync(order.OrderGUID, productCode);
        var removed = false;
        var detailGuid = detail?.DetailGUID;
        if (detail == null)
        {
            if (omitNonPositiveNewDetail && quantity <= 0)
            {
                removed = true;
            }
            else
            {
                detail = CreateDetail(
                    order.OrderGUID,
                    scope.StoreCode,
                    productCode,
                    quantity,
                    price,
                    importPrice,
                    now,
                    actor
                );
                detailGuid = detail.DetailGUID;
                await _db.Insertable(detail).ExecuteCommandAsync();
            }
        }
        else
        {
            detail.Quantity += quantity;
            if (detail.Quantity <= 0)
            {
                removed = true;
                await SoftDeleteAsync(detail, actor, now);
            }
            else
            {
                UpdateDetailAmounts(detail, now, actor);
                await _db.Updateable(detail).ExecuteCommandAsync();
            }
        }

        var summary = await RecalculateAsync(order.OrderGUID, order.UpdatedAt);
        return StoreOrderCartMutationOutcome.Completed(
            new StoreOrderCartMutationWrite(
                order.OrderGUID,
                scope.StoreCode,
                productCode,
                detailGuid,
                removed,
                summary
            )
        );
    }

    private async Task<StoreOrderCartMutationSummary> RecalculateAsync(
        string orderGuid,
        DateTime? previousUpdatedAt
    )
    {
        var row = await _db.Queryable<WareHouseOrderDetails>()
            .Where(detail => detail.OrderGUID == orderGuid && !detail.IsDeleted)
            .Select(detail => new CartMutationSummaryRow
            {
                TotalQuantity = SqlFunc.AggregateSum(detail.Quantity ?? 0),
                TotalSku = SqlFunc.AggregateDistinctCount(detail.ProductCode),
                TotalAmount = SqlFunc.AggregateSum(detail.OEMAmount ?? 0),
                TotalImportAmount = SqlFunc.AggregateSum(detail.ImportAmount ?? 0),
            })
            .FirstAsync();

        var totalAmount = row?.TotalAmount ?? 0;
        var totalImportAmount = row?.TotalImportAmount ?? 0;
        var (revisionAt, cartRevision) = StoreOrderCartRules.ResolveNextRevision(
            previousUpdatedAt
        );
        await _db.Updateable<WareHouseOrder>()
            .SetColumns(order => new WareHouseOrder
            {
                OEMTotalAmount = totalAmount,
                ImportTotalAmount = totalImportAmount,
                UpdatedAt = revisionAt,
            })
            .Where(order => order.OrderGUID == orderGuid)
            .ExecuteCommandAsync();

        return new StoreOrderCartMutationSummary(
            cartRevision,
            totalAmount,
            totalImportAmount,
            row?.TotalQuantity ?? 0,
            row?.TotalSku ?? 0
        );
    }

    private async Task<StoreOrderCartItemDto?> QueryChangedItemAsync(
        string orderGuid,
        string productCode,
        string? detailGuid
    )
    {
        var query = _db.Queryable<WareHouseOrderDetails>()
            .LeftJoin<Product>((detail, product) => detail.ProductCode == product.ProductCode)
            .LeftJoin<WarehouseProduct>(
                (detail, product, warehouseProduct) =>
                    detail.ProductCode == warehouseProduct.ProductCode
            )
            .LeftJoin<DomesticProduct>(
                (detail, product, warehouseProduct, domesticProduct) =>
                    warehouseProduct.ProductCode == domesticProduct.ProductCode
            )
            .LeftJoin<ProductGrade>(
                (detail, product, warehouseProduct, domesticProduct, grade) =>
                    detail.ProductCode == grade.ProductCode && !grade.IsDeleted
            )
            .Where(detail =>
                detail.OrderGUID == orderGuid
                && detail.ProductCode == productCode
                && !detail.IsDeleted
            );

        if (!string.IsNullOrWhiteSpace(detailGuid))
        {
            query = query.Where(detail => detail.DetailGUID == detailGuid);
        }

        var item = await query
            .Select(
                (detail, product, warehouseProduct, domesticProduct, grade) =>
                    new StoreOrderCartItemDto
                    {
                        DetailGUID = detail.DetailGUID,
                        ProductCode = detail.ProductCode ?? string.Empty,
                        ItemNumber = product.ItemNumber,
                        Barcode = product.Barcode,
                        Grade = grade.Grade,
                        ProductName = product.ProductName,
                        ProductImage = product.ProductImage,
                        // 仓库是否仍在供货；false 时前端标明“已暂停供货”。左连接缺行按不可订处理。
                        IsActive = SqlFunc.IsNull(warehouseProduct.IsActive, false),
                        Price = detail.OEMPrice ?? 0,
                        Quantity = detail.Quantity ?? 0,
                        AllocQuantity = detail.AllocQuantity,
                        Amount = detail.OEMAmount ?? 0,
                        ImportPrice = detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0),
                        ImportAmount =
                            detail.ImportAmount
                            ?? (
                                (detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0))
                                * (detail.Quantity ?? 0)
                            ),
                        AllocatedImportAmount =
                            (detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0))
                            * (detail.AllocQuantity ?? 0),
                        Volume = domesticProduct.PackingQuantity > 0
                            ? domesticProduct.UnitVolume / domesticProduct.PackingQuantity
                            : domesticProduct.UnitVolume,
                        MinOrderQuantity = warehouseProduct.MinOrderQuantity ?? 1,
                    }
            )
            .FirstAsync();

        if (item == null)
        {
            return null;
        }

        await ApplySupplyPlansAsync(new[] { item });
        if (item.Volume.HasValue)
        {
            item.OrderVolume = StoreOrderCartRules.CalculateVolume(item.Volume, item.Quantity);
            item.AllocVolume = StoreOrderCartRules.CalculateVolume(
                item.Volume,
                item.AllocQuantity ?? 0
            );
            item.TotalVolume = item.OrderVolume;
        }

        return item;
    }

    /// <summary>
    /// 给已下架的购物车行补上未关闭的供货计划；在供货的行不带说明，缺说明表时整体跳过。
    /// </summary>
    private async Task ApplySupplyPlansAsync(IReadOnlyCollection<StoreOrderCartItemDto> items)
    {
        var pausedCodes = items
            .Where(item => !item.IsActive && !string.IsNullOrWhiteSpace(item.ProductCode))
            .Select(item => item.ProductCode)
            .ToList();
        if (pausedCodes.Count == 0)
        {
            return;
        }

        var supplyPlans = await LoadOpenSupplyPlansAsync(pausedCodes);
        if (supplyPlans.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            if (!item.IsActive && supplyPlans.TryGetValue(item.ProductCode, out var plan))
            {
                item.SupplyPlan = plan;
            }
        }
    }

    /// <summary>
    /// 按商品编码批量读取未关闭供货说明的后续计划。说明表尚未建立时返回空，
    /// 调用方把所有下架行都当普通「暂停供货」处理。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> LoadOpenSupplyPlansAsync(
        IReadOnlyCollection<string?> productCodes
    )
    {
        var codes = productCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0 || !WarehouseProductSupplyNoticeWriter.IsSchemaReady(_db))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var rows = await _db.Queryable<WarehouseProductSupplyNotice>()
            .Where(notice => codes.Contains(notice.ProductCode) && notice.ClosedAtUtc == null)
            .Select(notice => new SupplyPlanRow
            {
                ProductCode = notice.ProductCode,
                SupplyPlan = notice.SupplyPlan,
            })
            .ToListAsync();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.ProductCode) && !string.IsNullOrWhiteSpace(row.SupplyPlan))
            {
                result[row.ProductCode] = row.SupplyPlan;
            }
        }

        return result;
    }

    private async Task<WareHouseOrder?> FindActiveCartAsync(StoreOrderCartScope scope)
    {
        var query = _db.Queryable<WareHouseOrder>()
            .Where(order =>
                order.StoreCode == scope.StoreCode
                && order.FlowStatus == 0
                && !order.IsDeleted
            );
        query = string.IsNullOrWhiteSpace(scope.CartOwnerUserGuid)
            ? query.Where(order => SqlFunc.IsNullOrEmpty(order.CartOwnerUserGuid))
            : query.Where(order => order.CartOwnerUserGuid == scope.CartOwnerUserGuid);
        return await query.FirstAsync();
    }

    private async Task<WareHouseOrderDetails?> FindActiveDetailAsync(
        string orderGuid,
        string productCode
    )
    {
        return await _db.Queryable<WareHouseOrderDetails>()
            .Where(detail =>
                detail.OrderGUID == orderGuid
                && detail.ProductCode == productCode
                && !detail.IsDeleted
            )
            .FirstAsync();
    }

    private async Task<Store?> GetStoreByCodeOrGuidAsync(string? storeCode)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return null;
        }

        var normalized = storeCode.Trim();
        return await _db.Queryable<Store>()
            .Where(store =>
                !store.IsDeleted
                && (store.StoreCode == normalized || store.StoreGUID == normalized)
            )
            .FirstAsync();
    }

    /// <summary>
    /// 新建一辆空购物车表头（FlowStatus=0）；加购建车与提交时承接保留行共用。
    /// </summary>
    private static WareHouseOrder CreateCartHeader(
        StoreOrderCartScope scope,
        DateTime now,
        string actor
    ) => new()
    {
        OrderGUID = UuidHelper.GenerateUuid7(),
        StoreCode = scope.StoreCode,
        CartOwnerUserGuid = scope.CartOwnerUserGuid,
        OrderDate = now,
        FlowStatus = 0,
        IsDeleted = false,
        CreatedAt = now,
        UpdatedAt = now,
        CreatedBy = actor,
        UpdatedBy = actor,
        OEMTotalAmount = 0,
        ImportTotalAmount = 0,
        ShippingFee = 0,
    };

    private static WareHouseOrderDetails CreateDetail(
        string orderGuid,
        string storeCode,
        string productCode,
        decimal quantity,
        decimal price,
        decimal importPrice,
        DateTime now,
        string actor
    ) => new()
    {
        DetailGUID = UuidHelper.GenerateUuid7(),
        OrderGUID = orderGuid,
        StoreCode = storeCode,
        ProductCode = productCode,
        Quantity = quantity,
        OEMPrice = price,
        OEMAmount = price * quantity,
        ImportPrice = importPrice,
        ImportAmount = importPrice * quantity,
        IsDeleted = false,
        CreatedAt = now,
        UpdatedAt = now,
        CreatedBy = actor,
        UpdatedBy = actor,
    };

    private static void UpdateDetailAmounts(
        WareHouseOrderDetails detail,
        DateTime now,
        string actor
    )
    {
        detail.OEMAmount = detail.Quantity * detail.OEMPrice;
        detail.ImportAmount = detail.Quantity * detail.ImportPrice;
        detail.UpdatedAt = now;
        detail.UpdatedBy = actor;
    }

    private async Task SoftDeleteAsync(
        WareHouseOrderDetails detail,
        string actor,
        DateTime now
    )
    {
        detail.IsDeleted = true;
        detail.UpdatedAt = now;
        detail.UpdatedBy = actor;
        await _db.Updateable(detail).ExecuteCommandAsync();
    }

    private string ResolveActorName()
    {
        return actorContext.ActorName;
    }

    private static StoreOrderCartMutationWrite EmptyMutation(
        StoreOrderCartScope scope,
        string productCode
    ) => new(
        string.Empty,
        scope.StoreCode,
        productCode,
        null,
        true,
        new StoreOrderCartMutationSummary(0, 0, 0, 0, 0)
    );

    private sealed class SupplyPausedLineRow
    {
        public string? DetailGuid { get; init; }
        public string? ProductCode { get; init; }
        public string? ItemNumber { get; init; }
        public string? ProductName { get; init; }
        public decimal Quantity { get; init; }
    }

    private sealed class SupplyPlanRow
    {
        public string? ProductCode { get; init; }
        public string? SupplyPlan { get; init; }
    }

    private sealed class CartProductPriceRow
    {
        public decimal? Price { get; init; }
        public decimal? ImportPrice { get; init; }
        public bool WarehouseIsActive { get; init; }
    }

    private sealed class CartMutationSummaryRow
    {
        public decimal TotalAmount { get; init; }
        public decimal TotalImportAmount { get; init; }
        public decimal TotalQuantity { get; init; }
        public int TotalSku { get; init; }
    }

    private sealed class CartSummaryReadRow
    {
        public string? ProductCode { get; init; }
        public decimal Quantity { get; init; }
        public decimal AllocQuantity { get; init; }
        public decimal ImportAmount { get; init; }
        public decimal AllocatedImportAmount { get; init; }
        public decimal? UnitVolume { get; init; }
    }
}
