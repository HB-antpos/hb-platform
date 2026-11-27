using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.ProductHistory.Infrastructure;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace BlazorApp.Api.Features.ProductInsights;

/// <summary>单店商品进销只读查询。所有日期均按调用方已确认的当地业务日含首尾处理。</summary>
public sealed class StoreProductInsightQueryService(
    SqlSugarContext context,
    IServiceProvider serviceProvider
)
{
    private readonly ISqlSugarClient _db = context.Db;

    internal async Task<(DateTime StartDate, DateTime EndDate)?> GetDefaultRangeAsync(
        string storeCode,
        int days,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 复用既有 ProductSalesHistoryQueryStore 的门店时区推导，避免由设备时区决定业务日期。
        var salesHistoryQueryStore = serviceProvider.GetRequiredService<ProductSalesHistoryQueryStore>();
        var salesContext = await salesHistoryQueryStore.GetActiveStoreSalesContextAsync(storeCode);
        if (salesContext == null)
        {
            return null;
        }

        // 区间含首尾：days 天 = 结束日往前 days - 1 天。
        return (salesContext.EndDate.AddDays(-(days - 1)), salesContext.EndDate);
    }

    public async Task<StoreProductInsightDto?> GetAsync(
        string storeCode,
        string productCode,
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = await _db.Queryable<Store>()
            .Where(item => item.StoreCode == storeCode && !item.IsDeleted && item.IsActive)
            .Select(item => new ProductInsightStoreDto
            {
                StoreCode = item.StoreCode,
                StoreName = item.StoreName,
            })
            .FirstAsync();
        var product = await _db.Queryable<Product>()
            .LeftJoin<HBLocalSupplier>((item, supplier) => item.LocalSupplierCode == supplier.LocalSupplierCode && !supplier.IsDeleted)
            .Where((item, supplier) => item.ProductCode == productCode && !item.IsDeleted)
            .Select((item, supplier) => new ProductInsightProductDto
            {
                ProductCode = item.ProductCode!,
                ProductName = item.ProductName,
                ItemNumber = item.ItemNumber,
                Barcode = item.Barcode,
                ProductImage = item.ProductImage,
                LocalSupplierCode = item.LocalSupplierCode,
                LocalSupplierName = supplier.Name,
            })
            .FirstAsync();
        if (store == null || product == null)
        {
            return null;
        }

        var result = new StoreProductInsightDto
        {
            Range = new ProductInsightRangeDto
            {
                StartDate = startDate.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                EndDate = endDate.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            },
            GeneratedAt = DateTime.UtcNow,
            Store = store,
            Product = product,
            SourceType = ResolveSourceType(product.LocalSupplierCode),
        };

        var sales = await GetSalesAsync(storeCode, productCode, startDate, endDate, cancellationToken);
        result.Sales = sales.Sales;
        result.SalesStatisticLastUpdatedAt = sales.LastUpdatedAt;
        if (result.SourceType == "warehouse")
        {
            result.Warehouse = await GetWarehouseAsync(storeCode, productCode, startDate, endDate, cancellationToken);
        }
        else
        {
            result.Purchases = await GetPurchasesAsync(storeCode, productCode, startDate, endDate, cancellationToken);
        }
        return result;
    }

    /// <summary>
    /// 跨分店汇总本区间进货量，口径与单店 <see cref="GetAsync"/> 完全一致：
    /// 本地供应商商品按进货单明细数量（入库日优先、否则订单日），仓库商品（供应商 200）按已出库订单的配货数量（出库日）。
    /// 先按单据取回再在内存按分店求和，复用同一组查询构造器，避免两处口径漂移。
    /// </summary>
    public async Task<ProductBranchPurchaseSummary> GetBranchPurchasesAsync(
        string productCode,
        IReadOnlyCollection<string> storeCodes,
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localSupplierCode = await _db.Queryable<Product>()
            .Where(item => item.ProductCode == productCode && !item.IsDeleted)
            .Select(item => item.LocalSupplierCode)
            .FirstAsync();
        var sourceType = ResolveSourceType(localSupplierCode);
        var summary = new ProductBranchPurchaseSummary { SourceType = sourceType };
        var codes = storeCodes.Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (codes.Count == 0)
        {
            return summary;
        }

        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<(string? StoreCode, decimal Quantity)> movements;
        if (sourceType == "warehouse")
        {
            var rows = await BuildWarehouseDeliveryQuery(codes, productCode, startDate.Date, endDate.Date.AddDays(1)).ToListAsync();
            // 与单店相同，再用规则函数复核出库状态与日期，防止 SQLite / MergeTable 的可空日期比较差异。
            movements = rows
                .Where(item => StoreProductInsightRules.IsWarehouseDelivery(item.FlowStatus, item.OutboundDate, item.AllocQuantity, startDate, endDate))
                .Select(item => (item.StoreCode, item.AllocQuantity));
        }
        else
        {
            var rows = await BuildValidPurchaseQuery(codes, productCode)
                .Where(item => item.InboundDate >= startDate.Date && item.InboundDate < endDate.Date.AddDays(1))
                .ToListAsync();
            movements = rows.Select(item => (item.StoreCode, item.Quantity));
        }

        foreach (var (storeCode, quantity) in movements)
        {
            var key = storeCode?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }
            summary.QuantityByStore[key] = summary.QuantityByStore.GetValueOrDefault(key) + quantity;
        }
        return summary;
    }

    internal static string ResolveSourceType(string? localSupplierCode) =>
        string.Equals(localSupplierCode?.Trim(), "200", StringComparison.Ordinal) ? "warehouse" : "local";

    private async Task<(ProductInsightSalesDto Sales, DateTime? LastUpdatedAt)> GetSalesAsync(string storeCode, string productCode, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await _db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(item => item.BranchCode == storeCode && item.ProductCode == productCode && item.Date >= startDate.Date && item.Date < endDate.Date.AddDays(1))
            .GroupBy(item => item.Date)
            .Select(item => new ProductInsightDailySalesDto
            {
                Date = item.Date,
                Quantity = SqlFunc.AggregateSum(item.TotalQuantity),
                Amount = SqlFunc.AggregateSum(item.TotalAmount),
            })
            .OrderBy(item => item.Date, OrderByType.Desc)
            .ToListAsync();
        var lastUpdatedAt = await _db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(item => item.BranchCode == storeCode && item.ProductCode == productCode && item.Date >= startDate.Date && item.Date < endDate.Date.AddDays(1))
            .MaxAsync(item => (DateTime?)item.UpdateTime);
        return (new ProductInsightSalesDto
        {
            Quantity = rows.Sum(item => item.Quantity),
            Amount = rows.Sum(item => item.Amount),
            Records = rows,
        }, lastUpdatedAt);
    }

    private async Task<ProductInsightPurchasesDto> GetPurchasesAsync(string storeCode, string productCode, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
    {
        var currentRows = await BuildValidPurchaseQuery(new List<string> { storeCode }, productCode)
            .Where(item => item.InboundDate >= startDate.Date && item.InboundDate < endDate.Date.AddDays(1))
            .OrderBy(item => item.InboundDate, OrderByType.Desc)
            .OrderBy(item => item.Id, OrderByType.Desc)
            .ToListAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var lastRow = await BuildValidPurchaseQuery(new List<string> { storeCode }, productCode)
            .Where(item => item.InboundDate < endDate.Date.AddDays(1))
            .OrderBy(item => item.InboundDate, OrderByType.Desc)
            .OrderBy(item => item.Id, OrderByType.Desc)
            .FirstAsync();
        return new ProductInsightPurchasesDto
        {
            Quantity = currentRows.Sum(item => item.Quantity),
            DocumentCount = currentRows.Count,
            Records = currentRows.Select(ToMovement).ToList(),
            // 最近历史进货不受查询区间限制，且从不混入范围内合计。
            LastRecord = lastRow == null || !StoreProductInsightRules.IsHistoricalRecordEligible(lastRow.InboundDate, endDate)
                ? null
                : ToMovement(lastRow),
        };
    }

    private ISugarQueryable<PurchaseRow> BuildValidPurchaseQuery(List<string> storeCodes, string productCode)
    {
        // 本地进货按进货单明细数量统计，不以入库状态筛掉单据；该数量不代表实收数量。
        // 与既有商品动销报表一致，业务日期优先入库日，未填时使用订单日，不以创建日期冒充进货日期。
        return _db.Queryable<StoreLocalSupplierInvoiceDetails>()
            .InnerJoin<StoreLocalSupplierInvoice>((detail, invoice) => detail.InvoiceGUID == invoice.InvoiceGUID)
            // 历史详情可能只保存 StoreRetailPrice.UUID；回退必须锁定同一分店，允许停用价目但排除软删映射。
            .LeftJoin<StoreRetailPrice>((detail, invoice, price) => detail.StoreProductCode == price.UUID && price.StoreCode == invoice.StoreCode && !price.IsDeleted)
            .LeftJoin<HBLocalSupplier>((detail, invoice, price, supplier) => invoice.SupplierCode == supplier.LocalSupplierCode && !supplier.IsDeleted)
            .Where((detail, invoice, price, supplier) =>
                storeCodes.Contains(invoice.StoreCode)
                && SqlFunc.IIF(SqlFunc.IsNullOrEmpty(detail.ProductCode), price.ProductCode, detail.ProductCode) == productCode
                && !invoice.IsDeleted && !detail.IsDeleted
                && (invoice.InboundDate != null || invoice.OrderDate != null))
            .GroupBy((detail, invoice, price, supplier) => new { invoice.InvoiceGUID, invoice.StoreCode, invoice.InvoiceNo, invoice.InboundDate, invoice.OrderDate, invoice.SupplierCode, supplier.Name })
            .Select((detail, invoice, price, supplier) => new PurchaseRow
            {
                Id = invoice.InvoiceGUID,
                StoreCode = invoice.StoreCode,
                DocumentNo = invoice.InvoiceNo,
                InboundDate = SqlFunc.IIF(invoice.InboundDate != null, invoice.InboundDate!.Value, invoice.OrderDate!.Value),
                SupplierCode = invoice.SupplierCode,
                SupplierName = supplier.Name,
                Quantity = SqlFunc.AggregateSum(detail.Quantity ?? 0m),
            })
            .MergeTable();
    }

    private async Task<ProductInsightWarehouseDto> GetWarehouseAsync(string storeCode, string productCode, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
    {
        var rows = await BuildWarehouseOrderQuery(new List<string> { storeCode }, productCode)
            .Where(item => item.OrderDate >= startDate.Date && item.OrderDate < endDate.Date.AddDays(1))
            .OrderBy(item => item.OrderDate, OrderByType.Desc)
            .ToListAsync();
        // 送货按实际出库日，而非订货日统计；日期条件在分组前下推，避免 SQLite 对 MergeTable 的 nullable 日期比较漏行。
        var deliveryRows = await BuildWarehouseDeliveryQuery(new List<string> { storeCode }, productCode, startDate.Date, endDate.Date.AddDays(1))
            .OrderBy(item => item.OutboundDate, OrderByType.Desc)
            .ToListAsync();
        var deliveries = deliveryRows
            .Where(item => StoreProductInsightRules.IsWarehouseDelivery(item.FlowStatus, item.OutboundDate, item.AllocQuantity, startDate, endDate))
            .Select(ToDeliveryMovement)
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();
        var lastDeliveryRow = await BuildWarehouseDeliveryQuery(new List<string> { storeCode }, productCode, null, endDate.Date.AddDays(1))
            .OrderBy(item => item.OutboundDate, OrderByType.Desc)
            .FirstAsync();
        return new ProductInsightWarehouseDto
        {
            OrderedQuantity = rows.Sum(item => item.Quantity),
            DeliveredQuantity = deliveries.Sum(item => item.Quantity),
            Orders = rows.Select(item => new ProductInsightOrderDto
            {
                Id = item.Id,
                Date = item.OrderDate,
                DocumentNo = item.DocumentNo ?? item.Id,
                Quantity = item.Quantity,
                DeliveredQuantity = item.FlowStatus == 2 && item.OutboundDate.HasValue ? item.AllocQuantity : 0,
                DeliveryDate = item.FlowStatus == 2 ? item.OutboundDate : null,
                Status = ToWarehouseStatus(item.FlowStatus),
            }).ToList(),
            Deliveries = deliveries,
            LastDelivery = lastDeliveryRow == null || !StoreProductInsightRules.IsHistoricalRecordEligible(lastDeliveryRow.OutboundDate!.Value, endDate)
                ? null
                : ToDeliveryMovement(lastDeliveryRow),
        };
    }

    private ISugarQueryable<WarehouseOrderRow> BuildWarehouseOrderQuery(List<string> storeCodes, string productCode)
    {
        return BuildWarehouseOrderBaseQuery(storeCodes, productCode)
            .GroupBy((detail, order) => new { order.OrderGUID, order.StoreCode, order.OrderNo, order.OrderDate, order.OutboundDate, order.FlowStatus })
            .Select((detail, order) => new WarehouseOrderRow
            {
                Id = order.OrderGUID,
                StoreCode = order.StoreCode,
                DocumentNo = order.OrderNo,
                OrderDate = order.OrderDate!.Value,
                OutboundDate = order.OutboundDate,
                FlowStatus = order.FlowStatus,
                Quantity = SqlFunc.AggregateSum(detail.Quantity ?? 0m),
                AllocQuantity = SqlFunc.AggregateSum(detail.AllocQuantity ?? 0m),
            })
            .MergeTable();
    }

    private ISugarQueryable<WarehouseOrderRow> BuildWarehouseDeliveryQuery(List<string> storeCodes, string productCode, DateTime? startDate, DateTime endExclusive)
    {
        var query = BuildWarehouseOrderBaseQuery(storeCodes, productCode)
            .Where((detail, order) => order.FlowStatus == 2 && order.OutboundDate != null && detail.AllocQuantity > 0);
        if (startDate.HasValue)
        {
            query = query.Where((detail, order) => order.OutboundDate >= startDate.Value);
        }
        return query
            .Where((detail, order) => order.OutboundDate < endExclusive)
            .GroupBy((detail, order) => new { order.OrderGUID, order.StoreCode, order.OrderNo, order.OrderDate, order.OutboundDate, order.FlowStatus })
            .Select((detail, order) => new WarehouseOrderRow
            {
                Id = order.OrderGUID,
                StoreCode = order.StoreCode,
                DocumentNo = order.OrderNo,
                OrderDate = order.OrderDate!.Value,
                OutboundDate = order.OutboundDate,
                FlowStatus = order.FlowStatus,
                Quantity = SqlFunc.AggregateSum(detail.Quantity ?? 0m),
                AllocQuantity = SqlFunc.AggregateSum(detail.AllocQuantity ?? 0m),
            })
            .MergeTable();
    }

    private ISugarQueryable<WareHouseOrderDetails, WareHouseOrder> BuildWarehouseOrderBaseQuery(List<string> storeCodes, string productCode)
    {
        return _db.Queryable<WareHouseOrderDetails>()
            .InnerJoin<WareHouseOrder>((detail, order) => detail.OrderGUID == order.OrderGUID)
            .Where((detail, order) => storeCodes.Contains(order.StoreCode) && detail.ProductCode == productCode && order.FlowStatus > 0 && !order.IsDeleted && !detail.IsDeleted && order.OrderDate != null);
    }

    private static ProductInsightMovementDto ToMovement(PurchaseRow row) => new()
    {
        Id = row.Id,
        Date = row.InboundDate,
        DocumentNo = string.IsNullOrWhiteSpace(row.DocumentNo) ? row.Id : row.DocumentNo,
        Quantity = row.Quantity,
        SupplierName = row.SupplierName ?? row.SupplierCode,
    };

    private static ProductInsightMovementDto ToDeliveryMovement(WarehouseOrderRow row) => new()
    {
        Id = row.Id,
        Date = row.OutboundDate!.Value,
        DocumentNo = string.IsNullOrWhiteSpace(row.DocumentNo) ? row.Id : row.DocumentNo,
        Quantity = row.AllocQuantity,
        SupplierName = "200",
    };

    private static string ToWarehouseStatus(int? flowStatus) => flowStatus switch
    {
        1 => "submitted",
        2 => "completed",
        3 => "allocating",
        _ => "unknown",
    };

    private sealed class PurchaseRow { public string Id { get; set; } = string.Empty; public string? StoreCode { get; set; } public string? DocumentNo { get; set; } public DateTime InboundDate { get; set; } public string? SupplierCode { get; set; } public string? SupplierName { get; set; } public decimal Quantity { get; set; } }
    private sealed class WarehouseOrderRow { public string Id { get; set; } = string.Empty; public string? StoreCode { get; set; } public string? DocumentNo { get; set; } public DateTime OrderDate { get; set; } public DateTime? OutboundDate { get; set; } public int? FlowStatus { get; set; } public decimal Quantity { get; set; } public decimal AllocQuantity { get; set; } }
}

/// <summary>跨分店进货汇总：SourceType 为 local（本地进货单）或 warehouse（仓库配货出库）。</summary>
public sealed class ProductBranchPurchaseSummary
{
    public string SourceType { get; init; } = "local";
    public Dictionary<string, decimal> QuantityByStore { get; } = new(StringComparer.OrdinalIgnoreCase);
}
