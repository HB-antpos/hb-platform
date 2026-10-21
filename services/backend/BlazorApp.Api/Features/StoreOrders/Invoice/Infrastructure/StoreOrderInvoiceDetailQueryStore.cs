using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.Invoice.Application.Ports;
using BlazorApp.Api.Features.StoreOrders.Invoice.Domain;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.Invoice.Infrastructure;

internal sealed class StoreOrderInvoiceDetailQueryStore(
    SqlSugarContext context,
    IStoreOrderAccessScope accessScope
) : IStoreOrderInvoiceDetailQueryStore
{
    private readonly ISqlSugarClient _db = context.Db;

    public async Task<StoreOrderInvoiceDetailReadResult> GetAsync(
        string orderGuid,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accessibleStoreCodes = await accessScope.GetAccessibleStoreCodesAsync();

        cancellationToken.ThrowIfCancellationRequested();
        var header = await _db.Queryable<WareHouseOrder>()
            .LeftJoin<Store>((order, store) =>
                order.StoreCode == store.StoreCode
                || order.StoreCode == store.StoreGUID
            )
            .Where(order => order.OrderGUID == orderGuid && !order.IsDeleted)
            .Select((order, store) => new StoreOrderInvoiceHeaderSnapshot
            {
                OrderGuid = order.OrderGUID,
                OrderNo = order.OrderNo,
                StoreCode = order.StoreCode,
                StoreName = store.StoreName,
                OemTotalAmount = order.OEMTotalAmount,
                ShippingFee = order.ShippingFee,
                Remarks = order.Remarks,
                StoreAddress = store.Address,
                StoreContactEmail = store.ContactEmail,
                OrderDate = order.OrderDate,
                OutboundDate = order.OutboundDate,
                FlowStatus = order.FlowStatus,
            })
            .FirstAsync();
        if (header == null)
        {
            return StoreOrderInvoiceDetailReadResult.NotFound("Order not found");
        }

        if (
            accessibleStoreCodes != null
            && !string.IsNullOrWhiteSpace(header.StoreCode)
            && !accessibleStoreCodes.Contains(
                header.StoreCode,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            return StoreOrderInvoiceDetailReadResult.NotFound(
                "You do not have access to this order"
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        // 发票只加载附件实际消费的字段；不读取货位、等级、体积或翻译数据。
        var lines = await _db.Queryable<WareHouseOrderDetails>()
            .LeftJoin<Product>((line, product) =>
                line.ProductCode == product.ProductCode
            )
            .LeftJoin<WarehouseProduct>((line, product, warehouseProduct) =>
                line.ProductCode == warehouseProduct.ProductCode
            )
            .Where(line => line.OrderGUID == header.OrderGuid && !line.IsDeleted)
            .OrderBy((line, product, warehouseProduct) => product.ItemNumber)
            .OrderBy((line, product, warehouseProduct) => line.DetailGUID)
            .Select((line, product, warehouseProduct) =>
                new StoreOrderInvoiceLineSnapshot
                {
                    DetailGuid = line.DetailGUID,
                    ProductCode = line.ProductCode,
                    ItemNumber = product.ItemNumber,
                    Barcode = product.Barcode,
                    ProductName = product.ProductName,
                    Quantity = line.Quantity,
                    AllocQuantity = line.AllocQuantity,
                    DetailImportPrice = line.ImportPrice,
                    WarehouseImportPrice = warehouseProduct.ImportPrice,
                    StoredImportAmount = line.ImportAmount,
                    RetailPrice = product.RetailPrice,
                    ReturnReason = line.ReturnReason,
                    ReturnDisposition = line.ReturnDisposition,
                    ReturnNote = line.ReturnNote,
                }
            )
            .ToListAsync();

        cancellationToken.ThrowIfCancellationRequested();
        return StoreOrderInvoiceDetailReadResult.Found(
            new StoreOrderInvoiceDetailSnapshot(header, lines)
        );
    }
}
