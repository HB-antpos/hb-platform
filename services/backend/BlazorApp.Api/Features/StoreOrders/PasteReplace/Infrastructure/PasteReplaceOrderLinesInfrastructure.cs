using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.PasteReplace.Domain;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.PasteReplace.Infrastructure;

internal interface IPasteReplaceOrderLinesInfrastructure
{
    Task<WareHouseOrder?> GetEditableOrderAsync(string orderGuid);

    Task<PasteReplaceMutationPlan> PrepareAsync(
        WareHouseOrder order,
        IReadOnlyCollection<ProductQuantityDto> items,
        string targetField
    );

    Task ApplyAsync(PasteReplaceMutationPlan plan);
}

internal sealed class PasteReplaceOrderLinesInfrastructure(
    SqlSugarContext context,
    IHttpContextAccessor httpContextAccessor
) : IPasteReplaceOrderLinesInfrastructure
{
    private readonly ISqlSugarClient _db = context.Db;

    public async Task<WareHouseOrder?> GetEditableOrderAsync(string orderGuid)
    {
        var order = await _db.Queryable<WareHouseOrder>()
            .Where(candidate => candidate.OrderGUID == orderGuid && !candidate.IsDeleted)
            .FirstAsync();

        // 允许购物车、已提交、配货中继续编辑；已完成订单保持只读。
        return order != null && (order.FlowStatus == 0 || order.FlowStatus == 1 || order.FlowStatus == 3)
            ? order
            : null;
    }

    public async Task<PasteReplaceMutationPlan> PrepareAsync(
        WareHouseOrder order,
        IReadOnlyCollection<ProductQuantityDto> items,
        string targetField
    )
    {
        var importableItems = PasteReplaceOrderLinesRules.NormalizeImportableItems(items);
        if (importableItems.Count == 0)
        {
            return new PasteReplaceMutationPlan(
                order.OrderGUID,
                Array.Empty<string>(),
                Array.Empty<WareHouseOrderDetails>(),
                Array.Empty<WareHouseOrderDetails>()
            );
        }

        var now = DateTime.Now;
        var currentUser = httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "System";
        var productCodes = importableItems
            .Select(item => item.ProductCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Excel 粘贴常见 200+ 行，批量读取基础资料和现有明细，避免逐行查询。
        var existingDetails = await _db.Queryable<WareHouseOrderDetails>()
            .Where(detail =>
                detail.OrderGUID == order.OrderGUID
                && detail.ProductCode != null
                && productCodes.Contains(detail.ProductCode)
            )
            .ToListAsync();
        var detailByProductCode = existingDetails
            .Where(detail => !string.IsNullOrWhiteSpace(detail.ProductCode))
            .GroupBy(detail => detail.ProductCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(detail => detail.IsDeleted ? 1 : 0).First(),
                StringComparer.OrdinalIgnoreCase
            );

        var warehouseProducts = await _db.Queryable<WarehouseProduct>()
            .Where(product => productCodes.Contains(product.ProductCode))
            .ToListAsync();
        var warehouseProductByCode = warehouseProducts
            .GroupBy(product => product.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase
            );

        var productCodesMissingWarehouse = productCodes
            .Where(code => !warehouseProductByCode.ContainsKey(code))
            .ToList();
        var productMasterCodes = productCodesMissingWarehouse.Count == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : (await _db.Queryable<Product>()
                .Where(product =>
                    product.ProductCode != null
                    && productCodesMissingWarehouse.Contains(product.ProductCode)
                )
                .Select(product => product.ProductCode!)
                .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var productCode in productCodesMissingWarehouse)
        {
            if (!productMasterCodes.Contains(productCode))
            {
                throw new Exception($"Product {productCode} not found");
            }
        }

        var insertedDetails = new List<WareHouseOrderDetails>();
        var touchedDetails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var detailsToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in importableItems)
        {
            var warehouseProduct = warehouseProductByCode.TryGetValue(
                item.ProductCode,
                out var existingWarehouseProduct
            )
                ? existingWarehouseProduct
                : new WarehouseProduct
                {
                    ProductCode = item.ProductCode,
                    OEMPrice = 0,
                    ImportPrice = 0,
                    MinOrderQuantity = 1,
                };

            // 退货行（发货数量为负）不属于粘贴范围：粘贴不能录入退货，也不能覆盖 / 清零已有退货行。
            // 活的退货行整行跳过；已软删的退货行被「复活」时先清掉退货痕迹，当作普通空行处理。
            if (
                detailByProductCode.TryGetValue(item.ProductCode, out var returnLine)
                && (returnLine.AllocQuantity ?? 0) < 0
            )
            {
                if (!returnLine.IsDeleted)
                {
                    continue;
                }

                returnLine.AllocQuantity = 0;
                returnLine.ReturnReason = null;
                returnLine.ReturnDisposition = null;
                returnLine.ReturnNote = null;
            }

            var isNewDetail = false;
            if (!detailByProductCode.TryGetValue(item.ProductCode, out var detail))
            {
                isNewDetail = true;
                detail = new WareHouseOrderDetails
                {
                    DetailGUID = UuidHelper.GenerateUuid7(),
                    OrderGUID = order.OrderGUID,
                    StoreCode = order.StoreCode,
                    ProductCode = item.ProductCode,
                    Quantity = 0,
                    AllocQuantity = 0,
                    OEMPrice = warehouseProduct.OEMPrice ?? 0,
                    OEMAmount = 0,
                    ImportPrice = item.ImportPrice ?? warehouseProduct.ImportPrice ?? 0,
                    ImportAmount = 0,
                    IsDeleted = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedBy = currentUser,
                    UpdatedBy = currentUser,
                };
                detailByProductCode[item.ProductCode] = detail;
                insertedDetails.Add(detail);
            }
            else if (item.ImportPrice.HasValue)
            {
                detail.ImportPrice = item.ImportPrice.Value;
            }

            // 再次粘贴软删商品时复活原明细，沿用原有 DetailGUID。
            detail.IsDeleted = false;

            var nextQuantity = item.Quantity;
            if (
                string.Equals(
                    item.Action,
                    StoreOrderPasteActions.Append,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                // 同一批重复货号按输入顺序叠加当前内存值。
                nextQuantity = string.Equals(
                    targetField,
                    StoreOrderPasteTargetFields.Quantity,
                    StringComparison.OrdinalIgnoreCase
                )
                    ? (detail.Quantity ?? 0) + item.Quantity
                    : (detail.AllocQuantity ?? 0) + item.Quantity;
            }

            if (
                string.Equals(
                    targetField,
                    StoreOrderPasteTargetFields.Quantity,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                detail.Quantity = nextQuantity;
            }
            else
            {
                detail.AllocQuantity = nextQuantity;
            }

            var allocQuantity = detail.AllocQuantity ?? 0;
            detail.OEMAmount = allocQuantity * (detail.OEMPrice ?? 0);
            // ImportAmount 是订货金额；发货/发票金额只在 DTO 中按 AllocQuantity 派生。
            detail.ImportAmount = PasteReplaceOrderLinesRules.CalculateImportAmount(
                detail.Quantity,
                detail.ImportPrice
            );
            detail.UpdatedAt = now;
            detail.UpdatedBy = currentUser;

            // 空行 = 订货数量 <= 0 且发货数量恰好为 0（发货数量为负的是退货行，上面已跳过，不会走到这里）。
            if ((detail.Quantity ?? 0) <= 0 && allocQuantity == 0)
            {
                if (!isNewDetail && !string.IsNullOrWhiteSpace(detail.DetailGUID))
                {
                    detailsToDelete.Add(detail.DetailGUID);
                }
                continue;
            }

            if (!isNewDetail && !string.IsNullOrWhiteSpace(detail.DetailGUID))
            {
                touchedDetails.Add(detail.DetailGUID);
            }
        }

        var detailsToInsert = insertedDetails
            .Where(detail => (detail.Quantity ?? 0) > 0 || (detail.AllocQuantity ?? 0) > 0)
            .ToList();
        var detailsToUpdate = existingDetails
            .Where(detail =>
                !string.IsNullOrWhiteSpace(detail.DetailGUID)
                && touchedDetails.Contains(detail.DetailGUID)
                && !detailsToDelete.Contains(detail.DetailGUID)
            )
            .ToList();

        return new PasteReplaceMutationPlan(
            order.OrderGUID,
            detailsToDelete.ToList(),
            detailsToUpdate,
            detailsToInsert
        );
    }

    public async Task ApplyAsync(PasteReplaceMutationPlan plan)
    {
        try
        {
            // Command 的唯一事务边界：明细写入与订单金额/修订号一起提交或回滚。
            _db.Ado.BeginTran();

            if (plan.DetailGuidsToDelete.Count > 0)
            {
                var detailGuids = plan.DetailGuidsToDelete.ToList();
                await _db.Deleteable<WareHouseOrderDetails>()
                    .Where(detail => detailGuids.Contains(detail.DetailGUID))
                    .ExecuteCommandAsync();
            }

            if (plan.DetailsToUpdate.Count > 0)
            {
                await _db.Updateable(plan.DetailsToUpdate.ToList()).ExecuteCommandAsync();
            }

            if (plan.DetailsToInsert.Count > 0)
            {
                await _db.Insertable(plan.DetailsToInsert.ToList()).ExecuteCommandAsync();
            }

            await UpdateOrderTotalAsync(plan.OrderGuid);
            _db.Ado.CommitTran();
        }
        catch
        {
            _db.Ado.RollbackTran();
            throw;
        }
    }

    private async Task<PasteReplaceOrderTotalRow?> UpdateOrderTotalAsync(
        string orderGuid,
        DateTime? previousUpdatedAt = null
    )
    {
        var summaryRow = await _db.Queryable<WareHouseOrderDetails>()
            .Where(detail => detail.OrderGUID == orderGuid && !detail.IsDeleted)
            .Select(detail => new PasteReplaceOrderTotalRow
            {
                TotalQuantity = SqlFunc.AggregateSum(detail.Quantity ?? 0),
                TotalSku = SqlFunc.AggregateDistinctCount(detail.ProductCode),
                TotalAmount = SqlFunc.AggregateSum(detail.OEMAmount ?? 0),
                TotalImportAmount = SqlFunc.AggregateSum(detail.ImportAmount ?? 0),
            })
            .FirstAsync();

        var totalOem = summaryRow?.TotalAmount ?? 0;
        var totalImport = summaryRow?.TotalImportAmount ?? 0;
        var (revisionAt, cartRevision) = ResolveNextCartRevision(previousUpdatedAt);
        if (summaryRow != null)
        {
            summaryRow.CartRevision = cartRevision;
        }

        await _db.Updateable<WareHouseOrder>()
            .SetColumns(order => new WareHouseOrder
            {
                OEMTotalAmount = totalOem,
                ImportTotalAmount = totalImport,
                UpdatedAt = revisionAt,
            })
            .Where(order => order.OrderGUID == orderGuid)
            .ExecuteCommandAsync();

        return summaryRow;
    }

    private static (DateTime RevisionAt, long CartRevision) ResolveNextCartRevision(
        DateTime? previousUpdatedAt,
        DateTime? nowOverride = null
    )
    {
        var now = nowOverride ?? DateTime.Now;
        var nowRevision = ToCartRevision(now);
        var previousRevision = previousUpdatedAt.HasValue
            ? ToCartRevision(previousUpdatedAt.Value)
            : 0;
        var nextRevision = Math.Max(nowRevision, previousRevision + 1);

        return (
            DateTimeOffset.FromUnixTimeMilliseconds(nextRevision).LocalDateTime,
            nextRevision
        );
    }

    private static long ToCartRevision(DateTime revisionAt)
    {
        return new DateTimeOffset(revisionAt).ToUnixTimeMilliseconds();
    }

    private sealed class PasteReplaceOrderTotalRow
    {
        public long CartRevision { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal TotalImportAmount { get; set; }
        public decimal TotalQuantity { get; set; }
        public int TotalSku { get; set; }
    }
}
