using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 销售明细「商品在各分店的表现」抽屉的两个独立读取：销售曲线（日销量）与进货明细（每一批）。
/// 窗口 = 销售开始日前 60 天 ~ 销售结束日；两者各自请求、各自失败，不拖慢也不阻断分店表格。
/// </summary>
public partial class SalesDashboardReactService
{
    /// <summary>规范化后的抽屉窗口，校验销售区间合法且窗口不超过上限。</summary>
    internal static (DateTime WindowStart, DateTime WindowEnd) ResolveProductDrawerWindow(DateRangeDto salesRange)
    {
        ArgumentNullException.ThrowIfNull(salesRange);
        var start = salesRange.StartDate.Date;
        var end = salesRange.EndDate.Date;
        if (start == default || end == default || start > end)
            throw new ArgumentException("日期范围无效");
        var windowStart = start.AddDays(-SalesDetailProductWindow.PurchaseLookbackDays);
        if ((end - windowStart).TotalDays + 1 > SalesDetailProductWindow.MaxWindowDays)
            throw new ArgumentException($"日期范围不能超过 {SalesDetailProductWindow.MaxWindowDays - SalesDetailProductWindow.PurchaseLookbackDays} 天");
        return (windowStart, end);
    }

    private static string NormalizeDrawerProductCode(string? productCode)
    {
        var code = productCode?.Trim();
        if (string.IsNullOrEmpty(code))
            throw new ArgumentException("商品编码不能为空");
        return code;
    }

    /// <summary>商品销售曲线（日）。销量口径同销售明细：商品日统计的数量合计，按授权分店（或所选单店）汇总。</summary>
    public async Task<ProductReportResponseDto<SalesDetailProductTrendDto>> GetSalesDetailProductTrendAsync(
        DateRangeDto salesRange,
        string productCode,
        List<string>? branchCodes,
        string? selectedBranchCode,
        CancellationToken cancellationToken = default)
    {
        var (windowStart, windowEnd) = ResolveProductDrawerWindow(salesRange);
        var code = NormalizeDrawerProductCode(productCode);
        var windowRange = new DateRangeDto { StartDate = windowStart, EndDate = windowEnd };
        var stateRows = await ReadCompactSalesBoardStatusRowsAsync(windowRange);
        var status = BuildSalesDetailReportStatus(stateRows, windowRange, false);
        var result = new SalesDetailProductTrendDto
        {
            ProductCode = code,
            BranchCode = string.IsNullOrWhiteSpace(selectedBranchCode) ? null : selectedBranchCode.Trim(),
            WindowStart = windowStart,
            WindowEnd = windowEnd,
        };
        var response = new ProductReportResponseDto<SalesDetailProductTrendDto>
        {
            Data = result,
            StatisticStatus = status.StatisticStatus,
            StatisticMessage = status.StatisticMessage,
            StatisticUpdatedAt = status.StatisticUpdatedAt,
            CacheVersion = status.CacheVersion,
        };
        // 授权范围为空时不读取；统计未就绪时只带回状态，由前端提示，不把半截数据画成曲线。
        if (branchCodes is { Count: 0 } || status.StatisticStatus != SalesStatisticRefreshStatus.Fresh)
            return response;

        var endExclusive = windowEnd.AddDays(1);
        var rows = await _context.Db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(row => row.ProductCode == code && row.Date >= windowStart && row.Date < endExclusive)
            .GroupBy(row => new { row.Date, row.BranchCode })
            .Select(row => new ProductTrendSqlRow
            {
                Date = row.Date,
                BranchCode = row.BranchCode,
                Quantity = SqlFunc.AggregateSum(row.TotalQuantity),
            })
            .ToListAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // 分店代码可能带空格：在内存里去空格后再按授权范围、所选单店过滤。
        var scope = branchCodes?.Select(item => item.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var single = result.BranchCode;
        result.Daily = rows
            .Where(row =>
            {
                var branch = (row.BranchCode ?? string.Empty).Trim();
                return (scope == null || scope.Contains(branch))
                    && (single == null || string.Equals(single, branch, StringComparison.OrdinalIgnoreCase));
            })
            .GroupBy(row => row.Date.Date)
            .Select(group => new SalesDetailProductTrendDayDto { Date = group.Key, Quantity = group.Sum(row => (decimal)row.Quantity) })
            .Where(day => day.Quantity != 0m)
            .OrderBy(day => day.Date)
            .ToList();
        return response;
    }

    private sealed class ProductTrendSqlRow
    {
        public DateTime Date { get; set; }
        public string? BranchCode { get; set; }
        public int Quantity { get; set; }
    }

    private sealed class DrawerPurchaseSqlRow
    {
        public string? StoreCode { get; set; }
        public string? DocumentNo { get; set; }
        public DateTime? PrimaryDate { get; set; }
        public DateTime? OrderDate { get; set; }
        public DateTime CreatedAt { get; set; }
        public decimal? Quantity { get; set; }
    }

    /// <summary>
    /// 商品进货明细：窗口内每一批进货（总仓配货 + 供应商进货单）。
    /// 总仓：非草稿、未删除订单的配货数量，日期取出库日期、无则订单日期、再无则创建时间（同进货金额看板）；
    /// 供应商：分店进货单明细数量，日期取入库日期、无则订单日期、再无则创建时间（同分店进货销量分析），
    /// 明细缺商品编码时按零售价 UUID 回查（同批量货号销量）。配货为空的订单按 0 计，口径不兜底。
    /// 日期回退在过滤里拆成三支，各日期列的索引都能用。
    /// </summary>
    public async Task<SalesDetailProductPurchasesDto> GetSalesDetailProductPurchasesAsync(
        DateRangeDto salesRange,
        string productCode,
        List<string>? branchCodes,
        CancellationToken cancellationToken = default)
    {
        var (windowStart, windowEnd) = ResolveProductDrawerWindow(salesRange);
        var code = NormalizeDrawerProductCode(productCode);
        var result = new SalesDetailProductPurchasesDto { ProductCode = code, WindowStart = windowStart, WindowEnd = windowEnd };
        if (branchCodes is { Count: 0 })
            return result;
        var start = windowStart;
        var endExclusive = windowEnd.AddDays(1);

        var warehouseRows = await _context.Db.Queryable<WareHouseOrderDetails>()
            .InnerJoin<WareHouseOrder>((detail, header) => detail.OrderGUID == header.OrderGUID)
            .Where((detail, header) =>
                detail.IsDeleted == false
                && header.IsDeleted == false
                && (header.FlowStatus == null || header.FlowStatus != 0)
                && detail.AllocQuantity != null
                && detail.AllocQuantity != 0
                && detail.ProductCode == code
                && header.StoreCode != null
                && (
                    (header.OutboundDate >= start && header.OutboundDate < endExclusive)
                    || (header.OutboundDate == null && header.OrderDate >= start && header.OrderDate < endExclusive)
                    || (header.OutboundDate == null && header.OrderDate == null && header.CreatedAt >= start && header.CreatedAt < endExclusive)
                ))
            .GroupBy((detail, header) => new { header.StoreCode, header.OrderNo, header.OutboundDate, header.OrderDate, header.CreatedAt })
            .Select((detail, header) => new DrawerPurchaseSqlRow
            {
                StoreCode = header.StoreCode,
                DocumentNo = header.OrderNo,
                PrimaryDate = header.OutboundDate,
                OrderDate = header.OrderDate,
                CreatedAt = header.CreatedAt,
                Quantity = SqlFunc.AggregateSum(detail.AllocQuantity),
            })
            .ToListAsync(cancellationToken);

        var supplierRows = await ReadDrawerSupplierInvoiceRowsAsync(code, start, endExclusive, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var scope = branchCodes?.Select(item => item.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<SalesDetailProductPurchaseBatchDto> Convert(IEnumerable<DrawerPurchaseSqlRow> rows, string source) => rows
            .Where(row => row.Quantity is not null and not 0m && !string.IsNullOrWhiteSpace(row.StoreCode))
            .Select(row => new SalesDetailProductPurchaseBatchDto
            {
                BranchCode = row.StoreCode!.Trim(),
                Date = (row.PrimaryDate ?? row.OrderDate ?? row.CreatedAt).Date,
                Source = source,
                DocumentNo = string.IsNullOrWhiteSpace(row.DocumentNo) ? null : row.DocumentNo.Trim(),
                Quantity = row.Quantity!.Value,
            })
            .Where(batch => batch.Date >= start && batch.Date <= windowEnd)
            .Where(batch => scope == null || scope.Contains(batch.BranchCode));
        result.Batches = Convert(warehouseRows, "warehouse")
            .Concat(Convert(supplierRows, "supplier"))
            .OrderByDescending(batch => batch.Date)
            .ThenBy(batch => batch.BranchCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(batch => batch.Source, StringComparer.Ordinal)
            .ToList();

        var storeCodes = result.Batches.Select(batch => batch.BranchCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (storeCodes.Count > 0)
        {
            var stores = await _context.Db.Queryable<Store>()
                .Where(store => store.StoreCode != null && storeCodes.Contains(store.StoreCode))
                .Select(store => new { store.StoreCode, store.StoreName })
                .ToListAsync(cancellationToken);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var store in stores)
            {
                var storeCode = store.StoreCode?.Trim();
                if (!string.IsNullOrEmpty(storeCode) && !string.IsNullOrWhiteSpace(store.StoreName) && !names.ContainsKey(storeCode))
                    names[storeCode] = store.StoreName.Trim();
            }
            result.Stores = storeCodes
                .Select(storeCode => new SalesDetailProductPurchaseStoreDto { Code = storeCode, Name = names.TryGetValue(storeCode, out var name) ? name : storeCode })
                .OrderBy(store => store.Code, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return result;
    }

    /// <summary>供应商进货单明细：先按商品编码读，再补读「明细缺编码、按零售价 UUID 回查到该商品」的行。</summary>
    private async Task<List<DrawerPurchaseSqlRow>> ReadDrawerSupplierInvoiceRowsAsync(
        string code, DateTime start, DateTime endExclusive, CancellationToken cancellationToken)
    {
        async Task<List<DrawerPurchaseSqlRow>> Read(Func<ISugarQueryable<StoreLocalSupplierInvoiceDetails, StoreLocalSupplierInvoice>, ISugarQueryable<StoreLocalSupplierInvoiceDetails, StoreLocalSupplierInvoice>> narrow)
        {
            var query = _context.Db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .InnerJoin<StoreLocalSupplierInvoice>((detail, header) => detail.InvoiceGUID == header.InvoiceGUID)
                .Where((detail, header) =>
                    detail.IsDeleted == false
                    && header.IsDeleted == false
                    && detail.Quantity != null
                    && detail.Quantity != 0
                    && (
                        (header.InboundDate >= start && header.InboundDate < endExclusive)
                        || (header.InboundDate == null && header.OrderDate >= start && header.OrderDate < endExclusive)
                        || (header.InboundDate == null && header.OrderDate == null && header.CreatedAt >= start && header.CreatedAt < endExclusive)
                    ));
            return await narrow(query)
                .GroupBy((detail, header) => new { header.StoreCode, header.InvoiceNo, header.InboundDate, header.OrderDate, header.CreatedAt })
                .Select((detail, header) => new DrawerPurchaseSqlRow
                {
                    StoreCode = header.StoreCode,
                    DocumentNo = header.InvoiceNo,
                    PrimaryDate = header.InboundDate,
                    OrderDate = header.OrderDate,
                    CreatedAt = header.CreatedAt,
                    Quantity = SqlFunc.AggregateSum(detail.Quantity),
                })
                .ToListAsync(cancellationToken);
        }

        var rows = await Read(query => query.Where((detail, header) => detail.ProductCode == code));
        var retailUuids = await _context.Db.Queryable<StoreRetailPrice>()
            .Where(price => price.ProductCode == code && price.IsDeleted == false)
            .Select(price => price.UUID)
            .ToListAsync(cancellationToken);
        for (var i = 0; i < retailUuids.Count; i += 500)
        {
            var batch = retailUuids.GetRange(i, Math.Min(500, retailUuids.Count - i));
            rows.AddRange(await Read(query => query.Where((detail, header) =>
                (detail.ProductCode == null || detail.ProductCode == "")
                && detail.StoreProductCode != null
                && batch.Contains(detail.StoreProductCode))));
        }
        return rows;
    }
}
