using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Features.StoreOrders.Invoice.Domain;

internal sealed record StoreOrderInvoiceDetailValidationResult(
    bool IsValid,
    string? OrderGuid,
    string? ErrorMessage
)
{
    internal static StoreOrderInvoiceDetailValidationResult Valid(string orderGuid) =>
        new(true, orderGuid, null);

    internal static StoreOrderInvoiceDetailValidationResult Invalid(string errorMessage) =>
        new(false, null, errorMessage);
}

internal sealed record StoreOrderInvoiceDetailReadResult(
    bool Success,
    StoreOrderInvoiceDetailSnapshot? Detail,
    string? ErrorMessage
)
{
    internal static StoreOrderInvoiceDetailReadResult Found(
        StoreOrderInvoiceDetailSnapshot detail
    ) => new(true, detail, null);

    internal static StoreOrderInvoiceDetailReadResult NotFound(string errorMessage) =>
        new(false, null, errorMessage);
}

internal sealed record StoreOrderInvoiceDetailSnapshot(
    StoreOrderInvoiceHeaderSnapshot Header,
    IReadOnlyList<StoreOrderInvoiceLineSnapshot> Lines
);

internal sealed class StoreOrderInvoiceHeaderSnapshot
{
    public string OrderGuid { get; set; } = string.Empty;
    public string? OrderNo { get; set; }
    public string? StoreCode { get; set; }
    public string? StoreName { get; set; }
    public decimal? OemTotalAmount { get; set; }
    public decimal? ShippingFee { get; set; }
    public string? Remarks { get; set; }
    public string? StoreAddress { get; set; }
    public string? StoreContactEmail { get; set; }
    public DateTime? OrderDate { get; set; }
    public DateTime? OutboundDate { get; set; }
    public int? FlowStatus { get; set; }
}

internal sealed class StoreOrderInvoiceLineSnapshot
{
    public string DetailGuid { get; set; } = string.Empty;
    public string? ProductCode { get; set; }
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public string? ProductName { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? AllocQuantity { get; set; }
    public decimal? DetailImportPrice { get; set; }
    public decimal? WarehouseImportPrice { get; set; }
    public decimal? StoredImportAmount { get; set; }
    public decimal? RetailPrice { get; set; }
    public string? ReturnReason { get; set; }
    public int? ReturnDisposition { get; set; }
    public string? ReturnNote { get; set; }
}

internal static class StoreOrderInvoiceDetailRules
{
    internal static StoreOrderCartDto ToDto(StoreOrderInvoiceDetailSnapshot detail)
    {
        var items = detail.Lines.Select(ToItemDto).ToList();
        var netAmount = items.Sum(item => item.AllocatedImportAmount);
        return new StoreOrderCartDto
        {
            OrderGUID = detail.Header.OrderGuid,
            OrderNo = detail.Header.OrderNo,
            StoreCode = detail.Header.StoreCode,
            StoreName = detail.Header.StoreName,
            TotalAmount = detail.Header.OemTotalAmount ?? 0m,
            TotalQuantity = (int)items.Sum(item => item.Quantity),
            // 订货金额保留历史持久值优先的规则，不能用发货数量重算。
            TotalImportAmount = items.Sum(item => item.ImportAmount),
            // 发票金额始终按实际配货数量计算，与订货金额保持独立；退货行（发货数量为负）按原价冲减，
            // 所以这是净额：TotalAllocatedImportAmount = TotalShipImportAmount + TotalReturnImportAmount。
            TotalAllocatedImportAmount = netAmount,
            TotalShipImportAmount = items
                .Where(item => (item.AllocQuantity ?? 0m) > 0m)
                .Sum(item => item.AllocatedImportAmount),
            TotalReturnImportAmount = items
                .Where(item => StoreOrderReturnRules.IsReturnLine(item.AllocQuantity))
                .Sum(item => item.AllocatedImportAmount),
            ReturnLineCount = items.Count(item => StoreOrderReturnRules.IsReturnLine(item.AllocQuantity)),
            TotalReturnQuantity = items
                .Where(item => StoreOrderReturnRules.IsReturnLine(item.AllocQuantity))
                .Sum(item => -(item.AllocQuantity ?? 0m)),
            // 单据类型按净额判定（净额 < 0 为退款单），邮件主题、附件名、前端发票页共用同一判定。
            DocumentType = StoreOrderReturnRules.ResolveDocumentType(netAmount),
            ShippingFee = detail.Header.ShippingFee,
            Remarks = detail.Header.Remarks,
            StoreAddress = detail.Header.StoreAddress,
            StoreContactEmail = detail.Header.StoreContactEmail,
            OrderDate = detail.Header.OrderDate,
            OutboundDate = detail.Header.OutboundDate,
            TotalAllocQuantity = (int)items.Sum(item => item.AllocQuantity ?? 0m),
            TotalSKU = detail.Lines
                .Select(line => line.ProductCode)
                .Where(productCode => !string.IsNullOrWhiteSpace(productCode))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            FlowStatus = detail.Header.FlowStatus,
            Items = items,
        };
    }

    private static StoreOrderCartItemDto ToItemDto(StoreOrderInvoiceLineSnapshot line)
    {
        var quantity = line.Quantity ?? 0m;
        var allocatedQuantity = line.AllocQuantity ?? 0m;
        var importPrice = line.DetailImportPrice ?? line.WarehouseImportPrice ?? 0m;

        return new StoreOrderCartItemDto
        {
            DetailGUID = line.DetailGuid,
            ProductCode = line.ProductCode ?? string.Empty,
            ItemNumber = line.ItemNumber,
            Barcode = line.Barcode,
            ProductName = line.ProductName,
            Quantity = quantity,
            AllocQuantity = line.AllocQuantity,
            ImportPrice = importPrice,
            ImportAmount = line.StoredImportAmount ?? (importPrice * quantity),
            AllocatedImportAmount = importPrice * allocatedQuantity,
            RRP = line.RetailPrice,
            ReturnReason = line.ReturnReason,
            ReturnDisposition = line.ReturnDisposition,
            ReturnNote = line.ReturnNote,
        };
    }
}
