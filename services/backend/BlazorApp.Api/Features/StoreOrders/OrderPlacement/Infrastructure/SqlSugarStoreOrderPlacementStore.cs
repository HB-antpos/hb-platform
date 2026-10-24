using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.OrderPlacement.Application.Ports;
using BlazorApp.Api.Features.StoreOrders.OrderPlacement.Domain;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.OrderPlacement.Infrastructure;

internal sealed class SqlSugarStoreOrderPlacementStore(SqlSugarContext context)
    : IStoreOrderPlacementOrderStore
{
    private readonly ISqlSugarClient _db = context.Db;

    public async Task<ApiResponse<T>> ExecuteInTransactionAsync<T>(
        Func<Task<ApiResponse<T>>> command
    )
    {
        var transactionStarted = false;
        try
        {
            await _db.Ado.BeginTranAsync();
            transactionStarted = true;
            var response = await command();
            if (!response.Success)
            {
                await _db.Ado.RollbackTranAsync();
                transactionStarted = false;
                return response;
            }

            await _db.Ado.CommitTranAsync();
            transactionStarted = false;
            return response;
        }
        catch
        {
            if (transactionStarted)
            {
                await _db.Ado.RollbackTranAsync();
            }

            throw;
        }
    }

    public async Task<string> InsertCreatedOrderAsync(
        string storeCode,
        string? remarks,
        string orderNo,
        DateTime now,
        string actorName
    )
    {
        var order = new WareHouseOrder
        {
            OrderGUID = UuidHelper.GenerateUuid7(),
            StoreCode = storeCode,
            OrderDate = now,
            FlowStatus = 1,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = actorName,
            OEMTotalAmount = 0,
            ImportTotalAmount = 0,
            ShippingFee = 0,
            OrderNo = orderNo,
            Remarks = remarks,
        };
        await _db.Insertable(order).ExecuteCommandAsync();
        return order.OrderGUID;
    }

    public async Task<StoreOrderCopySource?> GetCopySourceAsync(string sourceOrderGuid)
    {
        // StoreGate 等待完成后、命令事务内重读源订单及状态，禁止使用等待前快照。
        var sourceOrder = await _db.Queryable<WareHouseOrder>()
            .Where(order => order.OrderGUID == sourceOrderGuid && !order.IsDeleted)
            .FirstAsync();
        if (sourceOrder == null)
        {
            return null;
        }

        var sourceDetails = await _db.Queryable<WareHouseOrderDetails>()
            .Where(detail => detail.OrderGUID == sourceOrderGuid && !detail.IsDeleted)
            .ToListAsync();
        return new StoreOrderCopySource(
            sourceOrder.OrderGUID,
            sourceOrder.OrderNo,
            sourceOrder.FlowStatus,
            sourceDetails
        );
    }

    public async Task<CopyOrderResultDto> InsertCopiedOrderAsync(
        StoreOrderCopySource source,
        string targetStoreCode,
        bool copyOrderQuantity,
        bool copyAllocQuantity,
        string orderNo,
        DateTime now,
        string actorName
    )
    {
        var newOrder = new WareHouseOrder
        {
            OrderGUID = UuidHelper.GenerateUuid7(),
            StoreCode = targetStoreCode,
            OrderDate = now,
            FlowStatus = 1,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = actorName,
            OEMTotalAmount = 0,
            ImportTotalAmount = 0,
            ShippingFee = 0,
            OrderNo = orderNo,
            Remarks = $"Copied from {source.OrderNo}",
        };

        // 退货行（发货数量为负）不随复制订单带走：退货是针对原订单发生的一次性事实，
        // 复制过去既丢了原因（复制不带退货字段），又会让新订单凭空多出一笔冲减。
        var newDetails = source.Details
            .Where(sourceDetail => (sourceDetail.AllocQuantity ?? 0) >= 0)
            .Select(sourceDetail =>
        {
            var detail = new WareHouseOrderDetails
            {
                DetailGUID = UuidHelper.GenerateUuid7(),
                OrderGUID = newOrder.OrderGUID,
                StoreCode = targetStoreCode,
                ProductCode = sourceDetail.ProductCode,
                Quantity = copyOrderQuantity ? sourceDetail.Quantity : 0,
                OEMPrice = sourceDetail.OEMPrice,
                OEMAmount = 0,
                AllocQuantity = copyAllocQuantity ? sourceDetail.AllocQuantity : 0,
                ImportPrice = sourceDetail.ImportPrice,
                ImportAmount = 0,
                IsDeleted = false,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = actorName,
                UpdatedBy = actorName,
            };
            detail.OEMAmount = detail.AllocQuantity * detail.OEMPrice;
            detail.ImportAmount = (detail.Quantity ?? 0) * (detail.ImportPrice ?? 0);
            return detail;
        }).ToList();

        newOrder.OEMTotalAmount = newDetails.Sum(detail => detail.OEMAmount);
        newOrder.ImportTotalAmount = newDetails.Sum(detail => detail.ImportAmount);
        await _db.Insertable(newOrder).ExecuteCommandAsync();
        if (newDetails.Count > 0)
        {
            await _db.Insertable(newDetails).ExecuteCommandAsync();
        }

        return new CopyOrderResultDto
        {
            OrderGUID = newOrder.OrderGUID,
            OrderNo = newOrder.OrderNo,
        };
    }
}
