using System.Collections.ObjectModel;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services;

/// <summary>
/// 后台重算在新作用域中只需要这两个动作，不暴露完整兼容服务。
/// </summary>
internal interface ISalesStatisticsRecalculationExecutor
{
    Task MarkProductStatisticJobRunningAsync(Guid jobId, DateTime date);
    Task UpdateProductStoreDailyStatisticsAsync(DateTime? date);
}

/// <summary>
/// 报表缺口修复只需要两类刷新动作；调用方不得解析兼容门面或完整应用协调器。
/// </summary>
internal interface ISalesStatisticsRefreshOperations
{
    Task UpdateStoreStatisticsAsync(DateTime date, List<string>? branchCodes = null);
    Task UpdateHourlyStatisticsAsync(DateTime date, int? hour = null);
}

internal sealed record HBSales2025DailySnapshotSignature(
    DateTime Date,
    int RowCount,
    DateTime? MainLastModifiedAt,
    DateTime? MainCreatedAt,
    DateTime? DetailLastModifiedAt,
    DateTime? DetailCreatedAt,
    string Checksum
);

internal sealed record Posm2025DailyTableSignature(
    int RowCount,
    DateTime? LastModifiedAt,
    DateTime? CreatedAt,
    string Checksum
);

internal sealed record Posm2025DailySnapshotSignature(
    DateTime Date,
    Posm2025DailyTableSignature Orders,
    Posm2025DailyTableSignature Details,
    Posm2025DailyTableSignature Payments,
    Posm2025DailyTableSignature SalesReturns
);

internal sealed class HBSales2025BatchSnapshot
{
    private readonly IReadOnlyDictionary<DateTime, IReadOnlyList<ProductStoreDailySourceRow>> _rowsByDate;

    internal HBSales2025BatchSnapshot(
        IReadOnlyDictionary<DateTime, List<ProductStoreDailySourceRow>> rowsByDate,
        IReadOnlyDictionary<DateTime, HBSales2025DailySnapshotSignature> signatures
    )
    {
        _rowsByDate = new ReadOnlyDictionary<DateTime, IReadOnlyList<ProductStoreDailySourceRow>>(
            rowsByDate.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<ProductStoreDailySourceRow>)Array.AsReadOnly(entry.Value.ToArray())
            )
        );
        Signatures = new ReadOnlyDictionary<DateTime, HBSales2025DailySnapshotSignature>(
            signatures.ToDictionary(entry => entry.Key, entry => entry.Value)
        );
    }

    internal IReadOnlyDictionary<DateTime, HBSales2025DailySnapshotSignature> Signatures { get; }

    internal IReadOnlyList<ProductStoreDailySourceRow> GetRows(DateTime date) =>
        _rowsByDate.TryGetValue(date.Date, out var rows)
            ? rows
            : throw new InvalidOperationException($"批量快照不包含日期: {date:yyyy-MM-dd}");

    internal HBSales2025DailySnapshotSignature GetSignature(DateTime date) =>
        Signatures.TryGetValue(date.Date, out var signature)
            ? signature
            : throw new InvalidOperationException($"批量快照不包含签名: {date:yyyy-MM-dd}");
}

internal sealed class Posm2025DailySnapshot
{
    internal Posm2025DailySnapshot(
        IReadOnlyList<ProductStoreDailySourceRow> detailRows,
        IReadOnlyList<ProductStoreDailySourceRow> supplementalReturnRows,
        IReadOnlyList<StoreStatisticPaymentRow> paymentRows,
        IReadOnlyList<StoreStatisticOrderRow> orderRows,
        Dictionary<string, string> deviceBranchMap,
        Posm2025DailySnapshotSignature signature
    )
    {
        DetailRows = Array.AsReadOnly(detailRows.ToArray());
        SupplementalReturnRows = Array.AsReadOnly(supplementalReturnRows.ToArray());
        PaymentRows = Array.AsReadOnly(paymentRows.ToArray());
        OrderRows = Array.AsReadOnly(orderRows.ToArray());
        DeviceBranchMap = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(deviceBranchMap, StringComparer.Ordinal)
        );
        Signature = signature;
    }

    internal IReadOnlyList<ProductStoreDailySourceRow> DetailRows { get; }
    internal IReadOnlyList<ProductStoreDailySourceRow> SupplementalReturnRows { get; }
    internal IReadOnlyList<StoreStatisticPaymentRow> PaymentRows { get; }
    internal IReadOnlyList<StoreStatisticOrderRow> OrderRows { get; }
    internal IReadOnlyDictionary<string, string> DeviceBranchMap { get; }
    internal Posm2025DailySnapshotSignature Signature { get; }
}

internal class StoreCostRow
{
    public string? StoreCode { get; set; }
    public string? SupplierCode { get; set; }
    public string? ProductCode { get; set; }
    /// <summary>价格表没有单位列时为空；有单位的来源只允许同单位回退。</summary>
    public string? PricingUnit { get; set; }
    public bool PricingUnitKnown { get; set; }
    /// <summary>历史日期允许使用已停用但未删除的当前分店成本；跨店回退仍要求有效行。</summary>
    public bool IsActive { get; set; } = true;
    public decimal? PurchasePrice { get; set; }
}

internal class ProductCostRow
{
    public string? ProductCode { get; set; }
    public decimal? PurchasePrice { get; set; }
}

internal class WarehouseCostRow
{
    public string ProductCode { get; set; } = string.Empty;
    public decimal? ImportPrice { get; set; }
}

internal class ProductStatisticDiagnosticRow
{
    public string BranchCode { get; set; } = string.Empty;
    public decimal UnmatchedSupplierAmount { get; set; }
    public int UnmatchedSupplierQuantity { get; set; }
    public int UnmatchedSupplierProductCount { get; set; }
}

internal class ProductStatisticDiagnostics
{
    public decimal UnmatchedSupplierAmount { get; set; }
    public int UnmatchedSupplierQuantity { get; set; }
    public int UnmatchedSupplierProductCount { get; set; }
    public Dictionary<string, ProductStatisticDiagnosticRow> BranchDiagnostics { get; set; } = new();
}

internal class ProductStoreDailySourceRow
{
    public bool IsHBSalesSource { get; set; }
    public DateTime Date { get; set; }
    public string? OrderGuid { get; set; }
    /// <summary>
    /// POSM 来源行所属订单的 Status；HBSales 与补充退货行为空。
    /// 退款单（Status=3）的负数明细计入金额/数量，但不能让商品的订单数多算一单。
    /// </summary>
    public int? OrderStatus { get; set; }
    public string? HBSalesOrderNumber { get; set; }
    public string? DetailGuid { get; set; }
    /// <summary>HBSales 明细原始 ID；物化后再格式化为签名键，避免 SQL Server 生成 nvarchar(max) CAST。</summary>
    public long HBSalesDetailId { get; set; }
    public string? BranchCode { get; set; }
    public string? DeviceCode { get; set; }
    public DateTime? HBSalesMainLastModifiedAt { get; set; }
    public DateTime? HBSalesMainCreatedAt { get; set; }
    public DateTime? HBSalesDetailLastModifiedAt { get; set; }
    public DateTime? HBSalesDetailCreatedAt { get; set; }
    public DateTime? OrderLastUploadTime { get; set; }
    public string? ProductCode { get; set; }
    public string? ItemNumber { get; set; }
    public string? SupplierCode { get; set; }
    public string? ProductName { get; set; }
    public string? Barcode { get; set; }
    /// <summary>POSM/HBSales 原明细价格证据，不使用销售额倒推普通商品成本。</summary>
    public decimal? OriginalUnitPrice { get; set; }
    public decimal? OriginalSubtotal { get; set; }
    public string? PricingUnit { get; set; }
    // 保留来源字段别名，便于旧快照/测试构造数据时明确表达来源。
    public decimal? Price { get; set; }
    public decimal? Subtotal { get; set; }
    public decimal? HBSalesUnitPrice { get; set; }
    public decimal? HBSalesOriginalAmount { get; set; }
    /// <summary>HBSales 明细原始合计金额；退货行归一化后仍供来源签名保留原值。</summary>
    public decimal? HBSalesSaleAmount { get; set; }
    /// <summary>HBSales 明细折扣率；供折扣快照来源签名复用已读取的日明细。</summary>
    public decimal? HBSalesDiscountRate { get; set; }
    public string? PriceLookupCode { get; set; }
    /// <summary>原销售明细数量；退货行的 Quantity 是退货量，不能拿来反推原价。</summary>
    public decimal? OriginalSaleQuantity { get; set; }
    /// <summary>退货成本是否已关联到唯一且身份一致的原销售明细。</summary>
    public bool OriginalSaleCostEvidence { get; set; } = true;
    public string? OriginalHBSalesOrderNumber { get; set; }
    public string? HBSalesReturnCode { get; set; }
    public decimal Quantity { get; set; }
    public decimal ActualAmount { get; set; }
    public DateTime? DetailLastUploadTime { get; set; }
    public DateTime? SourceCreatedAt { get; set; }
    public DateTime? SourceUpdatedAt { get; set; }
    public string? DocumentType { get; set; }
}

internal class StoreStatisticPaymentRow
{
    public string? PaymentGuid { get; set; }
    public string? OrderGuid { get; set; }
    public string? BranchCode { get; set; }
    public string? DeviceCode { get; set; }
    public decimal Amount { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastUploadTime { get; set; }
}

internal class StoreStatisticQuantityRow
{
    public string? OrderGuid { get; set; }
    public string? BranchCode { get; set; }
    public string? DeviceCode { get; set; }
    public int Quantity { get; set; }
}

internal class StoreStatisticOrderRow
{
    public string? OrderGuid { get; set; }
    public string? BranchCode { get; set; }
    public string? DeviceCode { get; set; }
    public DateTime? OrderTime { get; set; }
    public int? Status { get; set; }
    public DateTime? LastUploadTime { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

internal class HBSalesSourceWatermarkRow
{
    public DateTime? MainLastModifiedAt { get; set; }
    public DateTime? MainCreatedAt { get; set; }
    public DateTime? DetailLastModifiedAt { get; set; }
    public DateTime? DetailCreatedAt { get; set; }
}

internal class HBSalesStoreAggregateRow
{
    public string? BranchCode { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalQuantity { get; set; }
    public int OrderCount { get; set; }
}

/// <summary>HBSales 按销售单聚合的分时来源行；小时在内存中按结账时间分桶。</summary>
internal class HBSalesHourlySourceRow
{
    public string? BranchCode { get; set; }
    public string? SalesOrderNo { get; set; }
    public string? DocumentType { get; set; }
    public TimeSpan? DetailCheckoutTime { get; set; }
    public TimeSpan? MainCheckoutTime { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalQuantity { get; set; }
}

/// <summary>HBSales 按分店与小时聚合后的分时来源行。</summary>
internal class HBSalesHourlyAggregateRow
{
    public string BranchCode { get; set; } = string.Empty;
    public int Hour { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalQuantity { get; set; }
    public int OrderCount { get; set; }
}

internal class HourlyStatisticSourceRow
{
    public DateTime Date { get; set; }
    public int Hour { get; set; }
    public string? BranchCode { get; set; }
    public string? DeviceCode { get; set; }
    public decimal TotalAmount { get; set; }
    public int TotalQuantity { get; set; }
    public int OrderCount { get; set; }
    public int CustomerCount { get; set; }
}

internal class OrderAmountRow
{
    public string? OrderGuid { get; set; }
    public decimal Amount { get; set; }
}

internal class StoreSupplierSourceRow
{
    public DateTime Date { get; set; }
    public string? BranchCode { get; set; }
    public string? DeviceCode { get; set; }
    public string? OrderGuid { get; set; }
    /// <summary>订单 Status；退款单（3）只冲减金额/数量，不计订单数。</summary>
    public int? OrderStatus { get; set; }
    public string? DetailSupplierCode { get; set; }
    public string? LocalSupplierCode { get; set; }
    public string? ChinaSupplierCode { get; set; }
    public decimal ActualAmount { get; set; }
    public decimal Quantity { get; set; }
}

internal class StoreSupplierResolvedRow
{
    public DateTime Date { get; set; }
    public string BranchCode { get; set; } = string.Empty;
    public string SupplierCode { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public bool IsDomestic { get; set; }
    public string? OrderGuid { get; set; }
    /// <summary>订单 Status；退款单（3）只冲减金额/数量，不计订单数。</summary>
    public int? OrderStatus { get; set; }
    public decimal TotalAmount { get; set; }
    public int TotalQuantity { get; set; }
}
