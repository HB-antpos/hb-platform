using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.ProductHistory.Infrastructure;
using BlazorApp.Api.Services.React;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace BlazorApp.Api.Features.ProductInsights;

/// <summary>
/// 季节商品查询：进货与销量分别按各自区间统计，理论存货 = 累计进货 − 累计销量。
/// 进货口径与单店商品进销一致：仓库商品取仓库实际出库送货，其余取分店本地进货单明细数量。
/// 为满足 1.5 秒内出结果，每类数据只查一次（全部分店一起取），再在内存里拆分当前分店与其他分店。
/// </summary>
public sealed class SeasonalProductInsightQueryService(
    SqlSugarContext context,
    IServiceProvider serviceProvider
)
{
    private readonly ISqlSugarClient _db = context.Db;

    /// <summary>门店当地「今天」；门店不存在或停用返回 null。</summary>
    internal async Task<DateTime?> GetStoreTodayAsync(string storeCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 复用既有门店时区推导，避免由设备或服务器时区决定业务日期。
        var salesHistoryQueryStore = serviceProvider.GetRequiredService<ProductSalesHistoryQueryStore>();
        var salesContext = await salesHistoryQueryStore.GetActiveStoreSalesContextAsync(storeCode);
        return salesContext?.EndDate.Date;
    }

    public async Task<SeasonalInsightLookupDto> LookupAsync(
        string storeCode,
        string keyword,
        (DateTime Start, DateTime End) inboundRange,
        (DateTime Start, DateTime End) salesRange,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = keyword.Trim();
        var limit = SeasonalProductInsightRules.MaxCandidates + 1;

        // 扫码或输入完整条码时只返回条码精确命中的商品，不混入货号碰巧包含该串的商品。
        var codes = await FindBarcodeMatchesAsync(normalized, limit);
        var matchMode = "barcode";
        if (codes.Count == 0)
        {
            // iOS 相机会把 0 开头的 EAN-13 去成 12 位；精确条码没有结果才回退前导 0 的另一种写法。
            var barcodeVariant = BarcodeVariants.GetLeadingZeroVariant(normalized);
            if (barcodeVariant != null)
            {
                codes = await FindBarcodeMatchesAsync(barcodeVariant, limit);
            }
        }
        if (codes.Count == 0)
        {
            matchMode = "itemNumber";
            codes = await FindItemNumberMatchesAsync(normalized, limit);
        }
        codes = codes.Distinct(StringComparer.Ordinal).ToList();
        var truncated = codes.Count > SeasonalProductInsightRules.MaxCandidates;
        codes = codes.Take(SeasonalProductInsightRules.MaxCandidates).ToList();

        // 第二段只按最多 20 个编码回表取名称与图片，保持第一段的匹配顺序。
        var loaded = codes.Count == 0
            ? []
            : await _db.Queryable<Product>()
                .Where(item => codes.Contains(item.ProductCode!) && !item.IsDeleted)
                .Select(item => new ProductRow
                {
                    ProductCode = item.ProductCode!,
                    ProductName = item.ProductName,
                    ItemNumber = item.ItemNumber,
                    Barcode = item.Barcode,
                    ProductImage = item.ProductImage,
                    LocalSupplierCode = item.LocalSupplierCode,
                })
                .ToListAsync();
        var byCode = loaded
            .GroupBy(row => row.ProductCode, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var candidates = codes.Where(byCode.ContainsKey).Select(code => byCode[code]).ToList();

        var stocks = await LoadStoreStocksAsync(storeCode, candidates, inboundRange, salesRange, cancellationToken);
        return new SeasonalInsightLookupDto
        {
            MatchMode = matchMode,
            Truncated = truncated,
            Ranges = ToRanges(inboundRange, salesRange),
            Items = candidates.Select(row => new SeasonalInsightCandidateDto
            {
                ProductCode = row.ProductCode,
                ProductName = row.ProductName,
                ItemNumber = row.ItemNumber,
                Barcode = row.Barcode,
                ProductImage = row.ProductImage,
                TheoreticalStock = stocks.GetValueOrDefault(row.ProductCode),
            }).ToList(),
        };
    }

    private async Task<List<string>> FindBarcodeMatchesAsync(string barcode, int limit)
    {
        var query = _db.Queryable<Product>();
        // IsDeleted 写成字面量才能命中以 IsDeleted = 0 过滤的条码索引（参数化会让过滤索引失效）。
        query = _db.CurrentConnectionConfig.DbType == DbType.SqlServer
            ? query.Where("[IsDeleted] = 0 AND [Barcode] = @seasonalBarcode AND [ProductCode] IS NOT NULL", new { seasonalBarcode = barcode })
            : query.Where(item => !item.IsDeleted && item.Barcode == barcode && item.ProductCode != null);
        return await query
            .OrderBy(item => item.ItemNumber)
            .OrderBy(item => item.ProductCode)
            .Select(item => item.ProductCode!)
            .Take(limit)
            .ToListAsync();
    }

    private async Task<List<string>> FindItemNumberMatchesAsync(string keyword, int limit)
    {
        var upper = keyword.ToUpperInvariant();
        var query = _db.Queryable<Product>();
        if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
        {
            // 只扫「货号 + 编码」的窄过滤索引（约 4.6 千页，原宽索引约 1.8 万页，冷缓存差数倍）；
            // 包含匹配走 BIN2 逐字节比较（CI 排序规则前导通配约 1.4 秒，BIN2 约 0.2 秒）。
            query = query.Where(
                "[IsDeleted] = 0 AND [ItemNumber] IS NOT NULL AND UPPER(CAST([ItemNumber] AS nvarchar(4000))) COLLATE "
                    + LocalSupplierProductSalesAnalysisService.SqlServerBinaryCollation
                    + " LIKE @seasonalPattern AND [ProductCode] IS NOT NULL",
                new { seasonalPattern = LocalSupplierProductSalesAnalysisService.BuildSqlServerLikePattern(upper) }
            );
        }
        else
        {
            query = query.Where(item =>
                !item.IsDeleted && item.ProductCode != null && item.ItemNumber != null && item.ItemNumber.ToUpper().Contains(upper));
        }

        // 货号完全相等的排最前，保证截断时精确结果不被挤掉。
        return await query
            .OrderBy(item => SqlFunc.IIF(item.ItemNumber == keyword, 0, 1))
            .OrderBy(item => item.ItemNumber)
            .OrderBy(item => item.ProductCode)
            .Select(item => item.ProductCode!)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<SeasonalProductInsightDto?> GetAsync(
        string storeCode,
        string productCode,
        (DateTime Start, DateTime End) inboundRange,
        (DateTime Start, DateTime End) salesRange,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = await _db.Queryable<Store>()
            .Where(item => item.StoreCode == storeCode && !item.IsDeleted && item.IsActive)
            .Select(item => new ProductInsightStoreDto { StoreCode = item.StoreCode, StoreName = item.StoreName })
            .FirstAsync();
        var product = await _db.Queryable<Product>()
            .Where(item => item.ProductCode == productCode && !item.IsDeleted)
            .Select(item => new ProductRow
            {
                ProductCode = item.ProductCode!,
                ProductName = item.ProductName,
                ItemNumber = item.ItemNumber,
                Barcode = item.Barcode,
                ProductImage = item.ProductImage,
                LocalSupplierCode = item.LocalSupplierCode,
            })
            .FirstAsync();
        if (store == null || product == null)
        {
            return null;
        }

        var codes = new List<string> { product.ProductCode };
        var isWarehouse = SeasonalProductInsightRules.IsWarehouseSource(product.LocalSupplierCode);
        var sales = await LoadSalesAsync(codes, null, salesRange, cancellationToken);
        var inbound = isWarehouse
            ? await LoadWarehouseInboundAsync(codes, null, inboundRange, cancellationToken)
            : await LoadLocalInboundAsync(codes, null, inboundRange, cancellationToken);

        var currentInbound = inbound.Where(line => SameStore(line.StoreCode, storeCode)).ToList();
        var currentSales = sales.Where(line => SameStore(line.BranchCode, storeCode)).ToList();
        var inboundQuantity = currentInbound.Sum(line => line.Quantity);
        var salesQuantity = currentSales.Sum(line => (decimal)line.Quantity);

        return new SeasonalProductInsightDto
        {
            GeneratedAt = DateTime.UtcNow,
            Store = store,
            Product = new SeasonalInsightProductDto
            {
                ProductCode = product.ProductCode,
                ProductName = product.ProductName,
                ItemNumber = product.ItemNumber,
                Barcode = product.Barcode,
                ProductImage = product.ProductImage,
                SourceType = isWarehouse ? "warehouse" : "local",
            },
            Ranges = ToRanges(inboundRange, salesRange),
            Inbound = new SeasonalInsightInboundDto
            {
                Quantity = inboundQuantity,
                DocumentCount = currentInbound.Count,
                Records = currentInbound
                    .OrderByDescending(line => line.Date)
                    .ThenByDescending(line => line.DocumentNo, StringComparer.Ordinal)
                    .Select(line => new SeasonalInsightInboundRecordDto
                    {
                        Id = line.DocumentId,
                        Date = line.Date,
                        DocumentNo = string.IsNullOrWhiteSpace(line.DocumentNo) ? line.DocumentId : line.DocumentNo,
                        Quantity = line.Quantity,
                    })
                    .ToList(),
            },
            Sales = new SeasonalInsightSalesDto
            {
                Quantity = salesQuantity,
                Amount = currentSales.Sum(line => line.Amount),
                Daily = currentSales
                    .GroupBy(line => line.Date.Date)
                    .OrderBy(group => group.Key)
                    .Select(group => new ProductInsightDailySalesDto
                    {
                        Date = group.Key,
                        Quantity = group.Sum(line => line.Quantity),
                        Amount = group.Sum(line => line.Amount),
                    })
                    .ToList(),
            },
            TheoreticalStock = SeasonalProductInsightRules.TheoreticalStock(inboundQuantity, salesQuantity),
            Branches = await BuildBranchesAsync(storeCode, inbound, sales, cancellationToken),
        };
    }

    private async Task<List<SeasonalInsightBranchDto>> BuildBranchesAsync(
        string currentStoreCode,
        List<InboundLine> inbound,
        List<SalesLine> sales,
        CancellationToken cancellationToken
    )
    {
        // 其他分店 = 进货区间内有进货记录的全部分店（不含当前分店），销量仍按销售区间统计。
        var inboundByStore = inbound
            .Where(line => !string.IsNullOrWhiteSpace(line.StoreCode) && !SameStore(line.StoreCode, currentStoreCode))
            .GroupBy(line => line.StoreCode!.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity), StringComparer.Ordinal);
        if (inboundByStore.Count == 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        var storeCodes = inboundByStore.Keys.ToList();
        var names = (await _db.Queryable<Store>()
                .Where(item => storeCodes.Contains(item.StoreCode) && !item.IsDeleted)
                .Select(item => new ProductInsightStoreDto { StoreCode = item.StoreCode, StoreName = item.StoreName })
                .ToListAsync())
            .GroupBy(item => item.StoreCode.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().StoreName, StringComparer.Ordinal);
        var salesByStore = sales
            .GroupBy(line => line.BranchCode.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(line => (decimal)line.Quantity), StringComparer.Ordinal);

        return inboundByStore
            .Select(pair =>
            {
                var sold = salesByStore.GetValueOrDefault(pair.Key);
                return new SeasonalInsightBranchDto
                {
                    StoreCode = pair.Key,
                    StoreName = names.GetValueOrDefault(pair.Key) ?? pair.Key,
                    InboundQuantity = pair.Value,
                    SalesQuantity = sold,
                    TheoreticalStock = SeasonalProductInsightRules.TheoreticalStock(pair.Value, sold),
                };
            })
            .OrderByDescending(item => item.TheoreticalStock)
            .ThenBy(item => item.StoreCode, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>候选列表用：批量计算当前分店多个商品的理论存货。</summary>
    private async Task<Dictionary<string, decimal>> LoadStoreStocksAsync(
        string storeCode,
        List<ProductRow> products,
        (DateTime Start, DateTime End) inboundRange,
        (DateTime Start, DateTime End) salesRange,
        CancellationToken cancellationToken
    )
    {
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (products.Count == 0)
        {
            return result;
        }

        var warehouseCodes = products.Where(item => SeasonalProductInsightRules.IsWarehouseSource(item.LocalSupplierCode)).Select(item => item.ProductCode).ToList();
        var localCodes = products.Select(item => item.ProductCode).Except(warehouseCodes, StringComparer.Ordinal).ToList();
        var inbound = new List<InboundLine>();
        if (warehouseCodes.Count > 0)
        {
            inbound.AddRange(await LoadWarehouseInboundAsync(warehouseCodes, storeCode, inboundRange, cancellationToken));
        }
        if (localCodes.Count > 0)
        {
            inbound.AddRange(await LoadLocalInboundAsync(localCodes, storeCode, inboundRange, cancellationToken));
        }
        var sales = await LoadSalesAsync(products.Select(item => item.ProductCode).ToList(), storeCode, salesRange, cancellationToken);

        foreach (var product in products)
        {
            var inQuantity = inbound.Where(line => line.ProductCode == product.ProductCode).Sum(line => line.Quantity);
            var soldQuantity = sales.Where(line => line.ProductCode == product.ProductCode).Sum(line => (decimal)line.Quantity);
            result[product.ProductCode] = SeasonalProductInsightRules.TheoreticalStock(inQuantity, soldQuantity);
        }
        return result;
    }

    private async Task<List<SalesLine>> LoadSalesAsync(
        List<string> productCodes,
        string? storeCode,
        (DateTime Start, DateTime End) range,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = range.Start.Date;
        var endExclusive = range.End.Date.AddDays(1);
        // 商品编码内联为 IN 字面量，可直接定位 (ProductCode, Date) 覆盖索引，无需按日期扫全部门店。
        var query = _db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(item => productCodes.Contains(item.ProductCode) && item.Date >= start && item.Date < endExclusive);
        if (storeCode != null)
        {
            query = query.Where(item => item.BranchCode == storeCode);
        }
        return await query
            .GroupBy(item => new { item.ProductCode, item.BranchCode, item.Date })
            .Select(item => new SalesLine
            {
                ProductCode = item.ProductCode,
                BranchCode = item.BranchCode,
                Date = item.Date,
                Quantity = SqlFunc.AggregateSum(item.TotalQuantity),
                Amount = SqlFunc.AggregateSum(item.TotalAmount),
            })
            .ToListAsync();
    }

    private async Task<List<InboundLine>> LoadLocalInboundAsync(
        List<string> productCodes,
        string? storeCode,
        (DateTime Start, DateTime End) range,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = range.Start.Date;
        var endExclusive = range.End.Date.AddDays(1);
        // 与单店商品进销一致：按进货单明细数量统计，不以入库状态筛单；业务日期优先入库日、否则订单日。
        // 明细缺商品编码时按同一分店的 StoreRetailPrice.UUID 回退。回退不在 SQL 里联接价目表：
        // SQL Server 会对每行明细都去价目表定位一次（2026-09 生产实测约 1 万逻辑读），而生产上缺编码的明细
        // UUID 也为空、回退从未命中，所以先取「编码命中 + 缺编码但有 UUID」的明细，再按需批量解析 UUID。
        var query = _db.Queryable<StoreLocalSupplierInvoiceDetails>()
            .InnerJoin<StoreLocalSupplierInvoice>((detail, invoice) => detail.InvoiceGUID == invoice.InvoiceGUID)
            .Where((detail, invoice) =>
                !invoice.IsDeleted && !detail.IsDeleted
                && (invoice.InboundDate != null || invoice.OrderDate != null)
                && SqlFunc.IIF(invoice.InboundDate != null, invoice.InboundDate!.Value, invoice.OrderDate!.Value) >= start
                && SqlFunc.IIF(invoice.InboundDate != null, invoice.InboundDate!.Value, invoice.OrderDate!.Value) < endExclusive
                && (productCodes.Contains(detail.ProductCode!)
                    || ((detail.ProductCode == null || detail.ProductCode == "")
                        && detail.StoreProductCode != null && detail.StoreProductCode != "")));
        if (storeCode != null)
        {
            query = query.Where((detail, invoice) => invoice.StoreCode == storeCode);
        }
        var rawLines = await query
            .Select((detail, invoice) => new LocalDetailLine
            {
                ProductCode = detail.ProductCode,
                StoreProductCode = detail.StoreProductCode,
                StoreCode = invoice.StoreCode,
                DocumentId = invoice.InvoiceGUID,
                DocumentNo = invoice.InvoiceNo,
                Date = SqlFunc.IIF(invoice.InboundDate != null, invoice.InboundDate!.Value, invoice.OrderDate!.Value),
                Quantity = detail.Quantity ?? 0m,
            })
            .ToListAsync();

        var fallbackPrices = await LoadFallbackPricesAsync(rawLines, cancellationToken);
        var lines = rawLines
            .Select(line => new InboundLine
            {
                ProductCode = ResolveLocalProductCode(line, fallbackPrices) ?? string.Empty,
                StoreCode = line.StoreCode,
                DocumentId = line.DocumentId,
                DocumentNo = line.DocumentNo,
                Date = line.Date,
                Quantity = line.Quantity,
            })
            .Where(line => productCodes.Contains(line.ProductCode, StringComparer.Ordinal));
        return MergeByDocument(lines);
    }

    /// <summary>只为缺商品编码的明细批量解析零售价 UUID；允许停用价目，排除软删映射。</summary>
    private async Task<Dictionary<string, (string? StoreCode, string? ProductCode)>> LoadFallbackPricesAsync(
        List<LocalDetailLine> lines,
        CancellationToken cancellationToken
    )
    {
        var uuids = lines
            .Where(line => string.IsNullOrEmpty(line.ProductCode) && !string.IsNullOrEmpty(line.StoreProductCode))
            .Select(line => line.StoreProductCode!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var result = new Dictionary<string, (string? StoreCode, string? ProductCode)>(StringComparer.Ordinal);
        // 每批 500 个字面量，避免超长 IN 列表的编译开销。
        foreach (var batch in uuids.Chunk(500))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchList = batch.ToList();
            var rows = await _db.Queryable<StoreRetailPrice>()
                .Where(item => batchList.Contains(item.UUID) && !item.IsDeleted)
                .Select(item => new { item.UUID, item.StoreCode, item.ProductCode })
                .ToListAsync();
            foreach (var row in rows)
            {
                result[row.UUID] = (row.StoreCode, row.ProductCode);
            }
        }
        return result;
    }

    private static string? ResolveLocalProductCode(
        LocalDetailLine line,
        Dictionary<string, (string? StoreCode, string? ProductCode)> fallbackPrices
    )
    {
        if (!string.IsNullOrEmpty(line.ProductCode))
        {
            return line.ProductCode;
        }
        // 回退必须锁定同一分店，跨店的同 UUID 映射不算。
        return line.StoreProductCode != null
            && fallbackPrices.TryGetValue(line.StoreProductCode, out var price)
            && SameStore(price.StoreCode, line.StoreCode ?? string.Empty)
                ? price.ProductCode
                : null;
    }

    private async Task<List<InboundLine>> LoadWarehouseInboundAsync(
        List<string> productCodes,
        string? storeCode,
        (DateTime Start, DateTime End) range,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = range.Start.Date;
        var endExclusive = range.End.Date.AddDays(1);
        // 与单店商品进销一致：仓库商品按实际出库日统计已配货数量，未出库的订货不算进货。
        var query = _db.Queryable<WareHouseOrderDetails>()
            .InnerJoin<WareHouseOrder>((detail, order) => detail.OrderGUID == order.OrderGUID)
            .Where((detail, order) =>
                productCodes.Contains(detail.ProductCode!)
                && !detail.IsDeleted && !order.IsDeleted
                && order.FlowStatus == 2 && order.OrderDate != null
                && order.OutboundDate != null && order.OutboundDate >= start && order.OutboundDate < endExclusive
                && detail.AllocQuantity > 0);
        if (storeCode != null)
        {
            query = query.Where((detail, order) => order.StoreCode == storeCode);
        }
        var lines = await query
            .Select((detail, order) => new InboundLine
            {
                ProductCode = detail.ProductCode!,
                StoreCode = order.StoreCode,
                DocumentId = order.OrderGUID,
                DocumentNo = order.OrderNo,
                Date = order.OutboundDate!.Value,
                Quantity = detail.AllocQuantity ?? 0m,
            })
            .ToListAsync();
        return MergeByDocument(lines);
    }

    /// <summary>同一单据同一商品的多行明细合并为一条进货记录。</summary>
    private static List<InboundLine> MergeByDocument(IEnumerable<InboundLine> lines) =>
        lines
            .GroupBy(line => (line.ProductCode, Store: line.StoreCode?.Trim() ?? string.Empty, line.DocumentId))
            .Select(group => new InboundLine
            {
                ProductCode = group.Key.ProductCode,
                StoreCode = group.Key.Store,
                DocumentId = group.Key.DocumentId,
                DocumentNo = group.First().DocumentNo,
                Date = group.First().Date,
                Quantity = group.Sum(line => line.Quantity),
            })
            .ToList();

    private static bool SameStore(string? left, string right) =>
        string.Equals(left?.Trim(), right.Trim(), StringComparison.Ordinal);

    private static SeasonalInsightRangesDto ToRanges(
        (DateTime Start, DateTime End) inboundRange,
        (DateTime Start, DateTime End) salesRange
    ) => new()
    {
        Inbound = new ProductInsightRangeDto
        {
            StartDate = SeasonalProductInsightRules.FormatDate(inboundRange.Start),
            EndDate = SeasonalProductInsightRules.FormatDate(inboundRange.End),
        },
        Sales = new ProductInsightRangeDto
        {
            StartDate = SeasonalProductInsightRules.FormatDate(salesRange.Start),
            EndDate = SeasonalProductInsightRules.FormatDate(salesRange.End),
        },
    };

    private sealed class ProductRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductImage { get; set; }
        public string? LocalSupplierCode { get; set; }
    }

    private sealed class SalesLine
    {
        public string ProductCode { get; set; } = string.Empty;
        public string BranchCode { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public int Quantity { get; set; }
        public decimal Amount { get; set; }
    }

    private sealed class LocalDetailLine
    {
        public string? ProductCode { get; set; }
        public string? StoreProductCode { get; set; }
        public string? StoreCode { get; set; }
        public string DocumentId { get; set; } = string.Empty;
        public string? DocumentNo { get; set; }
        public DateTime Date { get; set; }
        public decimal Quantity { get; set; }
    }

    private sealed class InboundLine
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? StoreCode { get; set; }
        public string DocumentId { get; set; } = string.Empty;
        public string? DocumentNo { get; set; }
        public DateTime Date { get; set; }
        public decimal Quantity { get; set; }
    }
}
