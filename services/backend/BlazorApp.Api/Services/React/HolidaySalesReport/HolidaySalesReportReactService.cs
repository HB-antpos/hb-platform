using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;

namespace BlazorApp.Api.Services.React.HolidaySalesReport;

/// <summary>
/// 节日销售报告的取数与缓存；全部计算在 <see cref="HolidaySalesReportBuilder"/>（纯内存，有单测）。
///
/// 取数口径：
/// - 节日商品 = 我方分类 HalloWeen 及其子类，且本地供应商为 KFC（257）/ Brazco（243）。
/// - 销售读 ProductStoreDailySalesStatistic（商品 × 分店 × 日，退款已按净额扣除，分店已按设备回填），
///   节日季 8/1 到 11/15，当年进行中只到昨天；这张表有索引，整季约 3 万行、不到 1 秒。
/// - 进货读分店进货单明细：订单日期 3/1 ~ 10/31、未删除、不含仓库 1006（它那张是零售价目单，不是进货）。
///   进货单的入库状态在这类单据上一直是 0，所以不按状态筛，草稿也计入。
/// </summary>
public sealed class HolidaySalesReportReactService : IHolidaySalesReportReactService
{
    /// <summary>原始数据只有几万行，缓存 10 分钟；当天统计在滚动但报告只算到昨天，不会因缓存读到半天数据。</summary>
    private static readonly TimeSpan DatasetCacheDuration = TimeSpan.FromMinutes(10);

    private const int MinYear = 2024;
    private const string HolidayRootCategoryName = "HalloWeen";

    private readonly SqlSugarContext _context;
    private readonly IMemoryCache _cache;

    public HolidaySalesReportReactService(SqlSugarContext context, IMemoryCache cache)
    {
        _context = context;
        _cache = cache;
    }

    /// <summary>测试钩子：悉尼业务日期的「今天」。</summary>
    internal Func<DateTime> BusinessTodayProvider { get; set; } = SalesStatisticsBusinessDate.Today;

    public async Task<HolidaySalesReportDto> GetReportAsync(
        int year,
        IReadOnlyCollection<string>? branchScope,
        string? branchCode,
        string? supplierCode,
        bool forceRefresh,
        CancellationToken cancellationToken
    )
    {
        ValidateYear(year);
        var supplier = NormalizeSupplier(supplierCode);
        var branch = string.IsNullOrWhiteSpace(branchCode) ? null : branchCode.Trim();
        var dataset = await GetDatasetAsync(year, forceRefresh, cancellationToken);

        // 授权范围为空、或指定的单店不在授权范围内：返回空报告（控制器已拦截越权，这里是兜底）。
        if (branchScope is { Count: 0 }
            || (branch != null && branchScope != null
                && !branchScope.Any(code => string.Equals(code.Trim(), branch, StringComparison.OrdinalIgnoreCase))))
        {
            return HolidaySalesReportBuilder.Build(dataset, Array.Empty<string>(), branch, supplier);
        }

        return HolidaySalesReportBuilder.Build(dataset, branchScope, branch, supplier);
    }

    public async Task<HolidaySalesCategoryMapDto> GetCategoryMapAsync(
        int year,
        IReadOnlyCollection<string>? branchScope,
        CancellationToken cancellationToken
    )
    {
        ValidateYear(year);
        var dataset = await GetDatasetAsync(year, false, cancellationToken);
        return HolidaySalesReportBuilder.BuildCategoryMap(dataset, branchScope);
    }

    private void ValidateYear(int year)
    {
        var current = BusinessTodayProvider().Year;
        if (year < MinYear || year > current)
            throw new ArgumentException($"年份必须在 {MinYear} 到 {current} 之间");
    }

    private static string? NormalizeSupplier(string? supplierCode)
    {
        if (string.IsNullOrWhiteSpace(supplierCode)) return null;
        var code = supplierCode.Trim();
        if (code is HolidaySalesSuppliers.Kfc or HolidaySalesSuppliers.Brazco) return code;
        throw new ArgumentException("供应商只能是 257（KFC）或 243（Brazco）");
    }

    private async Task<HolidaySalesDataset> GetDatasetAsync(int year, bool forceRefresh, CancellationToken cancellationToken)
    {
        var today = BusinessTodayProvider().Date;
        var from = new DateTime(year, 8, 1);
        var seasonEnd = new DateTime(year, 11, 15);
        // 今天的统计还在滚动：当年进行中只算到昨天。
        var through = today.AddDays(-1) < seasonEnd ? today.AddDays(-1) : seasonEnd;
        var key = $"holiday-sales-report:{year}:{through:yyyyMMdd}";

        if (!forceRefresh && _cache.TryGetValue(key, out HolidaySalesDataset? cached) && cached != null)
            return cached;

        var dataset = await LoadDatasetAsync(year, from, through, cancellationToken);
        _cache.Set(key, dataset, DatasetCacheDuration);
        return dataset;
    }

    private async Task<HolidaySalesDataset> LoadDatasetAsync(int year, DateTime from, DateTime through, CancellationToken cancellationToken)
    {
        var db = _context.Db;
        var holidayDate = new DateTime(year, 10, 31);
        var empty = new HolidaySalesDataset { Year = year, HolidayDate = holidayDate, SalesFrom = from, SalesThrough = through };
        if (through < from) return empty; // 整个节日季都在未来

        cancellationToken.ThrowIfCancellationRequested();
        var rootGuid = await db.Ado.GetStringAsync(
            "SELECT TOP 1 CategoryGUID FROM ProductCategory WHERE CategoryName = @name AND ParentGUID IS NOT NULL AND ISNULL(IsDeleted, 0) = 0",
            new SugarParameter("@name", HolidayRootCategoryName));
        if (string.IsNullOrWhiteSpace(rootGuid)) return empty;

        // 节日商品集合：子查询复用在三条取数 SQL 里
        const string productSet =
            "SELECT p.ProductCode FROM Product p JOIN ProductCategory k ON k.CategoryGUID = p.ProductCategoryGUID "
            + "WHERE (k.CategoryGUID = @root OR k.ParentGUID = @root) AND p.LocalSupplierCode IN (@kfc, @brazco)";
        SugarParameter[] Common() =>
        [
            new("@root", rootGuid),
            new("@kfc", HolidaySalesSuppliers.Kfc),
            new("@brazco", HolidaySalesSuppliers.Brazco),
        ];

        var products = await db.Ado.SqlQueryAsync<ProductRow>(
            "SELECT p.ProductCode, ISNULL(p.ItemNumber, '') AS ItemNumber, ISNULL(p.ProductName, '') AS ProductName, "
            + "p.ProductImage AS ImageUrl, p.LocalSupplierCode AS SupplierCode, sc.CategoryName AS SupplierCategory "
            + "FROM Product p JOIN ProductCategory k ON k.CategoryGUID = p.ProductCategoryGUID "
            + "LEFT JOIN (SELECT a.ProductCode, MIN(c.CategoryName) AS CategoryName "
            + "           FROM LocalSupplierCategoryProductAssignment a JOIN LocalSupplierCategory c ON c.CategoryGUID = a.CategoryGUID "
            + "           WHERE c.Depth = 1 AND c.FullPath LIKE 'Halloween >%' GROUP BY a.ProductCode) sc ON sc.ProductCode = p.ProductCode "
            + "WHERE (k.CategoryGUID = @root OR k.ParentGUID = @root) AND p.LocalSupplierCode IN (@kfc, @brazco)",
            Common());

        cancellationToken.ThrowIfCancellationRequested();
        var salesParams = Common().Concat([new SugarParameter("@from", from), new SugarParameter("@toExclusive", through.AddDays(1))]).ToArray();
        var sales = await db.Ado.SqlQueryAsync<SalesRow>(
            "SELECT s.BranchCode, s.Date, s.ProductCode, CAST(SUM(s.TotalQuantity) AS decimal(18, 2)) AS Quantity, "
            + "CAST(SUM(s.TotalAmount) AS decimal(18, 4)) AS Amount "
            + "FROM ProductStoreDailySalesStatistic s "
            + $"WHERE s.Date >= @from AND s.Date < @toExclusive AND s.ProductCode IN ({productSet}) "
            + "GROUP BY s.BranchCode, s.Date, s.ProductCode",
            salesParams);

        cancellationToken.ThrowIfCancellationRequested();
        var purchaseParams = Common()
            .Concat([new SugarParameter("@pf", new DateTime(year, 3, 1)), new SugarParameter("@pt", new DateTime(year, 11, 1))])
            .ToArray();
        var purchases = await db.Ado.SqlQueryAsync<PurchaseRow>(
            "SELECT m.StoreCode AS BranchCode, d.ProductCode, CAST(SUM(ISNULL(d.Quantity, 0)) AS decimal(18, 2)) AS Quantity, "
            + "CAST(SUM(ISNULL(d.Quantity, 0) * ISNULL(d.PurchasePrice, 0)) AS decimal(18, 4)) AS Cost "
            + "FROM StoreLocalSupplierInvoiceDetails d JOIN StoreLocalSupplierInvoice m ON m.InvoiceGUID = d.InvoiceGUID "
            + "WHERE m.IsDeleted = 0 AND d.IsDeleted = 0 AND m.StoreCode <> '1006' AND m.StoreCode IS NOT NULL "
            + "AND m.OrderDate >= @pf AND m.OrderDate < @pt "
            + $"AND d.ProductCode IN ({productSet}) "
            + "GROUP BY m.StoreCode, d.ProductCode",
            purchaseParams);

        var stores = await db.Ado.SqlQueryAsync<StoreRow>("SELECT StoreCode, StoreName FROM Store WHERE ISNULL(IsDeleted, 0) = 0");

        return new HolidaySalesDataset
        {
            Year = year,
            HolidayDate = holidayDate,
            SalesFrom = from,
            SalesThrough = through,
            Products = products
                .Where(p => !string.IsNullOrWhiteSpace(p.ProductCode))
                .GroupBy(p => p.ProductCode, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(p => new HolidayProductInfo(p.ProductCode, p.ItemNumber, OneLine(p.ProductName), p.ImageUrl, p.SupplierCode, p.SupplierCategory))
                .ToList(),
            Sales = sales
                .Where(r => !string.IsNullOrWhiteSpace(r.BranchCode) && !string.IsNullOrWhiteSpace(r.ProductCode))
                .Select(r => new HolidaySalesRow(r.BranchCode.Trim(), r.Date, r.ProductCode, r.Quantity, r.Amount))
                .ToList(),
            Purchases = purchases
                .Where(r => !string.IsNullOrWhiteSpace(r.BranchCode) && !string.IsNullOrWhiteSpace(r.ProductCode))
                .Select(r => new HolidayPurchaseRow(r.BranchCode.Trim(), r.ProductCode, r.Quantity, r.Cost))
                .ToList(),
            StoreNames = stores
                .Where(s => !string.IsNullOrWhiteSpace(s.StoreCode))
                .GroupBy(s => s.StoreCode.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().StoreName ?? g.Key, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>商品名里偶尔带换行（供应商资料粘贴所致），报表里压成一行。</summary>
    private static string OneLine(string name) =>
        string.Join(' ', name.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private sealed class ProductRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string ItemNumber { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public string SupplierCode { get; set; } = string.Empty;
        public string? SupplierCategory { get; set; }
    }

    private sealed class SalesRow
    {
        public string BranchCode { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public string ProductCode { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal Amount { get; set; }
    }

    private sealed class PurchaseRow
    {
        public string BranchCode { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal Cost { get; set; }
    }

    private sealed class StoreRow
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
    }
}
