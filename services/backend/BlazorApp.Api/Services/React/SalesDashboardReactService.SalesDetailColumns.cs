using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Api.Interfaces.React;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

public partial class SalesDashboardReactService
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IMemoryCache, SalesDetailQueryFlights>
        SalesDetailFlights = new();

    private sealed class SalesDetailFactRow
    {
        public string SupplierCode { get; set; } = string.Empty;
        public string BranchCode { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public string? ProductName { get; set; }
        public string? Barcode { get; set; }
        public int Quantity { get; set; }
        public decimal Revenue { get; set; }
        public int OrderCount { get; set; }
        public decimal? GrossProfit { get; set; }
        public int StatisticRowCount { get; set; }
        public int CostedRowCount { get; set; }
        public int GrossProfitRowCount { get; set; }
    }

    private sealed class SalesDetailBucket
    {
        public string Code { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? ItemNumber { get; set; }
        public string? ProductImage { get; set; }
        public decimal Revenue { get; set; }
        public int Quantity { get; set; }
        public int OrderCount { get; set; }
        public decimal GrossProfit { get; set; }
        public int StatisticRowCount { get; set; }
        public int CostedRowCount { get; set; }
        public int GrossProfitRowCount { get; set; }
        public decimal CompareRevenue { get; set; }
        public int CompareQuantity { get; set; }
        public int CompareOrderCount { get; set; }
        public decimal CompareGrossProfit { get; set; }
        public int CompareStatisticRowCount { get; set; }
        public int CompareCostedRowCount { get; set; }
        public int CompareGrossProfitRowCount { get; set; }
        public HashSet<string> ProductCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> CompareProductCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SalesDetailDenominator
    {
        public decimal AllRevenue { get; set; }
        public decimal ChinaRevenue { get; set; }
        public decimal CompareAllRevenue { get; set; }
        public decimal CompareChinaRevenue { get; set; }
    }

    // 只在一次三栏完整读取内复用目录和 POSM 映射；请求结束后随上下文释放，避免跨请求读到旧映射。
    private sealed class SalesDetailLookupContext
    {
        private readonly SalesDashboardReactService _service;
        private readonly DateRangeDto _range;
        private readonly List<string>? _branches;
        private Task<ProductSalesChinaCatalog>? _chinaCatalog;
        private Task<Dictionary<string, string>>? _chinaSupplierProductMap;

        public SalesDetailLookupContext(SalesDashboardReactService service, DateRangeDto range, List<string>? branches)
        {
            _service = service;
            _range = range;
            _branches = branches;
        }

        public Task<ProductSalesChinaCatalog> GetChinaCatalogAsync()
        {
            return _chinaCatalog ??= _service.GetProductSalesChinaCatalogAsync();
        }

        public Task<Dictionary<string, string>> GetChinaSupplierProductMapAsync()
        {
            return _chinaSupplierProductMap ??= LoadChinaSupplierProductMapAsync();
        }

        private async Task<Dictionary<string, string>> LoadChinaSupplierProductMapAsync()
        {
            if (_branches is { Count: 0 })
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // 只读取两期和分店范围内实际出现的旧 200 商品，避免跨库传输全目录映射。
            var products = await _service.GetReportLegacyChinaProductCodesAsync(_range, _branches ?? new());
            return (await _service.ReadReportChinaSupplierMappingsAsync(products, includeAllSupplierCodes: false)).ProductMap;
        }
    }

    public async Task<SalesDetailSectionResultDto> GetSalesDetailColumnsAsync(
        DateRangeDto dateRange,
        SalesDetailKind kind,
        SalesDetailSection section,
        List<string>? branchCodes = null,
        string? selectedBranchCode = null,
        string? selectedSupplierCode = null,
        string? selectedProductCode = null,
        string? search = null,
        int pageIndex = 1,
        int pageSize = 20,
        ProductReportStatisticStatusDto? statisticStatus = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDateRange(dateRange);
        if (pageIndex < 1)
            throw new ArgumentException("pageIndex 必须大于 0", nameof(pageIndex));
        pageSize = Math.Clamp(pageSize, 1, 100);

        var status = statisticStatus ?? await GetProductReportStatisticStatusAsync(dateRange);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(status.StatisticStatus, SalesStatisticRefreshStatus.Fresh, StringComparison.OrdinalIgnoreCase))
            return new SalesDetailSectionResultDto();

        // 没有「查看国内供应商名称」权限的账号，关键词不匹配国内供应商名称（见 ApplySalesDetailSearchFilterAsync）。
        // 结果会进缓存并在并发请求间共用，所以带关键词时要把这个开关写进缓存键，两类账号不能拿到对方的结果。
        var chinaNameSearchAllowed =
            string.IsNullOrWhiteSpace(search) || DomesticSupplierInfoMasking.IsNameSearchAllowed;

        return await ReadSalesDetailCompleteReportAsync(
            dateRange,
            status,
            version => BuildSalesDetailColumnsCacheKey(
                dateRange, kind, section, branchCodes, selectedBranchCode,
                selectedSupplierCode, selectedProductCode, search, pageIndex, pageSize, version,
                chinaNameSearchAllowed
            ),
            (executor, sharedToken) => executor.ComputeSalesDetailColumnsAsync(
                dateRange, kind, section, branchCodes, selectedBranchCode,
                selectedSupplierCode, selectedProductCode, search, pageIndex, pageSize, sharedToken
            ),
            () => new SalesDetailSectionResultDto(),
            cancellationToken
        );
    }

    // 共享计算使用独立作用域和协作式 10 秒预算，最后一个调用方离开时取消查询。
    private async Task<T> ReadSalesDetailCompleteReportAsync<T>(
        DateRangeDto range,
        ProductReportStatisticStatusDto status,
        Func<string, string> queryKey,
        Func<SalesDashboardReactService, CancellationToken, Task<T>> read,
        Func<T> empty,
        CancellationToken callerCancellationToken
    ) where T : class
    {
        var before = await GetProductReportStatisticStatusAsync(range);
        CopyReportStatisticStatus(before, status);
        if (!IsProductStatisticFresh(before))
            return empty();
        var key = queryKey($"complete:{before.CacheVersion}");
        if (_cache.TryGetValue<T>(key, out var cached) && cached != null)
            return cached;
        try
        {
            return await SalesDetailFlights.GetValue(_cache, _ => new()).RunAsync(key, async sharedToken =>
            {
                using var scope = _serviceScopeFactory?.CreateScope();
                var executor = scope?.ServiceProvider.GetService<ISalesDashboardReactService>() as SalesDashboardReactService ?? this;
                var ado = executor._context.Db.Ado;
                var previousTimeout = ado.CommandTimeOut;
                // 只调整新报表作用域，旧 catalog helper 未贯穿令牌时每条 SQL 仍有超时上限。
                if (scope != null)
                    ado.CommandTimeOut = Math.Min(previousTimeout > 0 ? previousTimeout : 8, 8);
                try
                {
                    var value = await executor.ReadReportSnapshotAsync(async () =>
                    {
                        sharedToken.ThrowIfCancellationRequested();
                        var data = await read(executor, sharedToken);
                        sharedToken.ThrowIfCancellationRequested();
                        var after = await executor.GetProductReportStatisticStatusAsync(range);
                        sharedToken.ThrowIfCancellationRequested();
                        if (!IsProductStatisticFresh(after) || after.CacheVersion != before.CacheVersion)
                            throw new ReportSnapshotChangedException();
                        return data;
                    });
                    sharedToken.ThrowIfCancellationRequested();
                    _cache.Set(key, value, DETAIL_CACHE_DURATION);
                    return value;
                }
                finally
                {
                    if (scope != null)
                        ado.CommandTimeOut = previousTimeout;
                }
            }, TimeSpan.FromSeconds(10), callerCancellationToken);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ReportSnapshotChangedException)
        {
            status.StatisticStatus = SalesStatisticRefreshStatus.Pending;
            status.StatisticMessage = "统计版本正在更新。";
            return empty();
        }
        catch (Exception error)
        {
            _logger.LogError(error, "读取三栏销售明细失败");
            status.StatisticStatus = SalesStatisticRefreshStatus.Failed;
            status.StatisticMessage = "统计读取失败，请稍后重试。";
            return empty();
        }
    }

    private static string BuildSalesDetailColumnsCacheKey(
        DateRangeDto range,
        SalesDetailKind kind,
        SalesDetailSection section,
        IEnumerable<string>? branches,
        string? selectedBranch,
        string? selectedSupplier,
        string? selectedProduct,
        string? search,
        int pageIndex,
        int pageSize,
        string? version,
        bool chinaNameSearchAllowed = true
    )
    {
        static string[] Codes(IEnumerable<string>? values) => (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            kind,
            section,
            start = range.StartDate.Date,
            end = range.EndDate.Date,
            compareStart = range.CompareStartDate?.Date,
            compareEnd = range.CompareEndDate?.Date,
            range.CompareMode,
            branches = Codes(branches),
            selectedBranch = selectedBranch?.Trim(),
            selectedSupplier = selectedSupplier?.Trim(),
            selectedProduct = selectedProduct?.Trim(),
            search = search?.Trim(),
            pageIndex,
            pageSize,
            version,
            // 只有「带关键词且不允许按国内供应商名称搜索」时才写入，其余情况的缓存键与原来完全一致。
            chinaNameSearchBlocked = chinaNameSearchAllowed ? (bool?)null : true,
        });
        return $"SalesDetailColumns:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))}";
    }

    private async Task<SalesDetailSectionResultDto> ComputeSalesDetailColumnsAsync(
        DateRangeDto range,
        SalesDetailKind kind,
        SalesDetailSection section,
        List<string>? branchCodes,
        string? selectedBranchCode,
        string? selectedSupplierCode,
        string? selectedProductCode,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var authorizedBranches = branchCodes == null ? null : NormalizeCodes(branchCodes);
        var selectedBranch = string.IsNullOrWhiteSpace(selectedBranchCode)
            ? authorizedBranches
            : new List<string> { selectedBranchCode.Trim() };

        // 每栏忽略自身的选择条件，以保留可反查候选。
        var factBranchCodes = section == SalesDetailSection.Branches ? authorizedBranches : selectedBranch;
        var lookups = new SalesDetailLookupContext(this, range, factBranchCodes);
        var factSupplier = section == SalesDetailSection.Suppliers ? null : NormalizeValue(selectedSupplierCode);
        var factProduct = section == SalesDetailSection.Products ? null : NormalizeValue(selectedProductCode);
        var factSearch = section is SalesDetailSection.Products or SalesDetailSection.Summary ? search : null;

        var productCodes = (HashSet<string>?)null;
        if (factProduct != null)
        {
            if (productCodes != null && !productCodes.Contains(factProduct))
                return new SalesDetailSectionResultDto();
            productCodes ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            productCodes.Add(factProduct);
        }

        // 供应商/分店无商品过滤时直接读取已完成的供应商汇总表，避免扫描商品日统计明细。
        if (_useSupplierRollups && productCodes == null && factSearch == null
            && (section is SalesDetailSection.Suppliers or SalesDetailSection.Branches))
        {
            return await BuildSalesDetailRollupSectionAsync(
                range, kind, section, factBranchCodes, factSupplier, pageIndex, pageSize, lookups, cancellationToken
            );
        }
        if (_useSupplierRollups && productCodes == null && factSearch == null && section == SalesDetailSection.Summary)
        {
            return await BuildSalesDetailRollupSummaryAsync(range, kind, factBranchCodes, factSupplier, cancellationToken);
        }

        if (section == SalesDetailSection.Summary)
        {
            return await BuildSalesDetailSummaryAsync(
                range, kind, factBranchCodes, factSupplier, productCodes, factSearch, pageIndex, pageSize, lookups, cancellationToken
            );
        }

        var productPageCodes = productCodes;
        var productTotal = 0;
        if (section == SalesDetailSection.Products)
        {
            var productPage = await QuerySalesDetailProductPageAsync(
                range, kind, factBranchCodes, factSupplier, productCodes, factSearch, pageIndex, pageSize, lookups, cancellationToken
            );
            productPageCodes = productPage.Codes;
            productTotal = productPage.Total;
        }
        var current = await QuerySalesDetailFactsAsync(
            range.StartDate.Date, range.EndDate.Date, kind, factBranchCodes, factSupplier, productPageCodes, factSearch, lookups, cancellationToken
        );
        var compare = HasCompare(range)
            ? await QuerySalesDetailFactsAsync(
                range.CompareStartDate!.Value.Date, range.CompareEndDate!.Value.Date,
                kind, factBranchCodes, factSupplier, productPageCodes, factSearch, lookups, cancellationToken
            )
            : new List<SalesDetailFactRow>();

        var buckets = AggregateSalesDetailFacts(current, compare, section);
        await FillSalesDetailNamesAsync(buckets, section, kind, lookups, cancellationToken);

        SalesDetailDenominator? denominator = null;
        if (section == SalesDetailSection.Suppliers)
            denominator = await QuerySalesDetailDenominatorAsync(range, factBranchCodes, lookups, cancellationToken);

        // 商品必须在完整结果集分页后仍按数量排序；供应商和分店继续按营业额排序。
        var orderedBuckets = section == SalesDetailSection.Products
            ? buckets.OrderByDescending(row => row.Quantity)
                .ThenByDescending(row => row.CompareQuantity)
                .ThenBy(row => row.Code, StringComparer.OrdinalIgnoreCase)
            : buckets.OrderByDescending(row => row.Revenue)
                .ThenByDescending(row => row.CompareRevenue)
                .ThenBy(row => row.Code, StringComparer.OrdinalIgnoreCase);
        var rows = orderedBuckets
            .Select(row => ToSalesDetailRow(row, section, kind, denominator, HasCompare(range)))
            .ToList();

        if (section == SalesDetailSection.Products)
        {
            var total = productTotal;
            var pageRows = rows;
            return new SalesDetailSectionResultDto
            {
                Rows = pageRows,
                Total = total,
                Summary = SumSalesDetailRows(pageRows, "page", "当前页商品"),
                OrderCountNote = null,
            };
        }

        var summary = SumSalesDetailRows(rows, "summary", "当前筛选汇总");
        if (section == SalesDetailSection.Branches && string.IsNullOrWhiteSpace(factSupplier) && factProduct == null)
        {
            summary.OrderCount = null;
            summary.CompareOrderCount = null;
        }
        return new SalesDetailSectionResultDto
        {
            Rows = rows,
            Total = rows.Count,
            Summary = summary,
            OrderCountNote = section == SalesDetailSection.Branches && string.IsNullOrWhiteSpace(factSupplier) && factProduct == null
                ? "跨供应商商品订单未做收据去重，客单数返回 null。"
                : null,
        };
    }

    private async Task<SalesDetailSectionResultDto> BuildSalesDetailRollupSectionAsync(
        DateRangeDto range,
        SalesDetailKind kind,
        SalesDetailSection section,
        List<string>? branches,
        string? supplier,
        int pageIndex,
        int pageSize,
        SalesDetailLookupContext lookups,
        CancellationToken cancellationToken
    )
    {
        var byBranch = section == SalesDetailSection.Branches;
        cancellationToken.ThrowIfCancellationRequested();
        var supplierFilter = string.IsNullOrWhiteSpace(supplier) ? null : new List<string> { supplier };
        var rawCurrent = await QuerySupplierRollupRowsAsync(kind == SalesDetailKind.China, byBranch, range.StartDate, range.EndDate, branches, supplierFilter, cancellationToken);
        var rawCompare = HasCompare(range)
            ? await QuerySupplierRollupRowsAsync(kind == SalesDetailKind.China, byBranch, range.CompareStartDate!.Value, range.CompareEndDate!.Value, branches, supplierFilter, cancellationToken)
            : new List<SupplierRollupReadRow>();
        cancellationToken.ThrowIfCancellationRequested();
        static string Key(SupplierRollupReadRow row, bool byBranch) => byBranch ? row.BranchCode : row.SupplierCode;
        var current = rawCurrent.GroupBy(row => Key(row, byBranch), StringComparer.OrdinalIgnoreCase)
            .Select(group => AggregateSalesDetailRollupRows(group, byBranch, !string.IsNullOrWhiteSpace(supplier))).ToList();
        var compare = rawCompare.GroupBy(row => Key(row, byBranch), StringComparer.OrdinalIgnoreCase)
            .Select(group => AggregateSalesDetailRollupRows(group, byBranch, !string.IsNullOrWhiteSpace(supplier))).ToList();
        var compareMap = compare.ToDictionary(row => Key(row, byBranch), StringComparer.OrdinalIgnoreCase);
        var buckets = new List<SalesDetailBucket>();
        foreach (var row in current)
        {
            var bucket = CreateSalesDetailRollupBucket(row, false, byBranch);
            if (compareMap.TryGetValue(Key(row, byBranch), out var compareRow))
                MergeSalesDetailRollupBucket(bucket, compareRow, true, byBranch);
            buckets.Add(bucket);
        }
        foreach (var row in compare.Where(row => !current.Any(currentRow => Key(currentRow, byBranch).Equals(Key(row, byBranch), StringComparison.OrdinalIgnoreCase))))
        {
            var bucket = CreateSalesDetailRollupBucket(row, true, byBranch);
            buckets.Add(bucket);
        }
        await FillSalesDetailNamesAsync(buckets, section, kind, lookups, cancellationToken);
        var denominator = section == SalesDetailSection.Suppliers
            ? await QuerySalesDetailDenominatorAsync(range, branches, lookups, cancellationToken)
            : null;
        var rows = buckets.OrderByDescending(row => row.Revenue).ThenBy(row => row.Code, StringComparer.OrdinalIgnoreCase)
            .Select(row => ToSalesDetailRow(row, section, kind, denominator, HasCompare(range))).ToList();
        if (section == SalesDetailSection.Branches && string.IsNullOrWhiteSpace(supplier))
        {
            foreach (var row in rows)
            {
                row.OrderCount = null;
                row.CompareOrderCount = null;
                row.AverageTransaction = null;
                row.CompareAverageTransaction = null;
            }
        }
        var summary = SumSalesDetailRows(rows, "summary", "当前筛选汇总");
        return new SalesDetailSectionResultDto
        {
            Rows = rows,
            Total = rows.Count,
            Summary = summary,
            OrderCountNote = null,
        };
    }

    private async Task<SalesDetailSectionResultDto> BuildSalesDetailRollupSummaryAsync(
        DateRangeDto range,
        SalesDetailKind kind,
        List<string>? branches,
        string? supplier,
        CancellationToken cancellationToken
    )
    {
        var supplierFilter = string.IsNullOrWhiteSpace(supplier) ? null : new List<string> { supplier };
        cancellationToken.ThrowIfCancellationRequested();
        var current = await QuerySupplierRollupRowsAsync(kind == SalesDetailKind.China, true, range.StartDate, range.EndDate, branches, supplierFilter, cancellationToken);
        var compare = HasCompare(range)
            ? await QuerySupplierRollupRowsAsync(kind == SalesDetailKind.China, true, range.CompareStartDate!.Value, range.CompareEndDate!.Value, branches, supplierFilter, cancellationToken)
            : new List<SupplierRollupReadRow>();
        cancellationToken.ThrowIfCancellationRequested();
        var currentRevenue = current.Sum(row => row.TotalAmount);
        var compareRevenue = HasCompare(range) ? compare.Sum(row => row.TotalAmount) : (decimal?)null;
        var currentQuantity = current.Sum(row => row.TotalQuantity);
        var compareQuantity = HasCompare(range) ? compare.Sum(row => row.TotalQuantity) : (int?)null;
        var currentOrders = string.IsNullOrWhiteSpace(supplier) ? (int?)null : current.Sum(row => row.OrderCount);
        var compareOrders = HasCompare(range) && !string.IsNullOrWhiteSpace(supplier) ? compare.Sum(row => row.OrderCount) : (int?)null;
        var currentGross = current.Count > 0 && current.All(row => RollupProfit(row).HasValue) ? current.Sum(row => RollupProfit(row) ?? 0m) : (decimal?)null;
        var compareGross = HasCompare(range) && compare.Count > 0 && compare.All(row => RollupProfit(row).HasValue) ? compare.Sum(row => RollupProfit(row) ?? 0m) : (decimal?)null;
        var summary = new SalesDetailRowDto
        {
            Code = "summary",
            Name = "当前筛选汇总",
            Revenue = currentRevenue,
            CompareRevenue = compareRevenue,
            Quantity = currentQuantity,
            CompareQuantity = compareQuantity,
            OrderCount = currentOrders,
            CompareOrderCount = compareOrders,
            AverageTransaction = currentOrders > 0 ? currentRevenue / currentOrders : null,
            CompareAverageTransaction = compareRevenue.HasValue && compareOrders > 0 ? compareRevenue.Value / compareOrders : null,
            AverageUnitPrice = currentQuantity > 0 ? currentRevenue / currentQuantity : null,
            CompareAverageUnitPrice = compareRevenue.HasValue && compareQuantity > 0 ? compareRevenue.Value / compareQuantity : null,
            GrossProfit = currentGross,
            CompareGrossProfit = compareGross,
            GrossMarginRate = CalculateGrossMarginRate(currentRevenue, currentGross),
            CompareGrossMarginRate = compareRevenue.HasValue ? CalculateGrossMarginRate(compareRevenue.Value, compareGross) : null,
        };
        return new SalesDetailSectionResultDto
        {
            Rows = new List<SalesDetailRowDto> { summary },
            Total = 1,
            Summary = summary,
            OrderCountNote = string.IsNullOrWhiteSpace(supplier) ? "跨供应商范围未做收据去重，客单数返回 null。" : null,
        };
    }

    private static SalesDetailBucket CreateSalesDetailRollupBucket(SupplierRollupReadRow row, bool compare, bool byBranch)
    {
        var bucket = new SalesDetailBucket { Code = byBranch ? row.BranchCode : row.SupplierCode };
        MergeSalesDetailRollupBucket(bucket, row, compare, byBranch);
        return bucket;
    }

    private static SupplierRollupReadRow AggregateSalesDetailRollupRows(
        IEnumerable<SupplierRollupReadRow> rows,
        bool byBranch,
        bool ordersSafe
    )
    {
        var list = rows.ToList();
        var first = list[0];
        return new SupplierRollupReadRow
        {
            SupplierCode = byBranch ? string.Empty : first.SupplierCode,
            BranchCode = byBranch ? first.BranchCode : string.Empty,
            TotalAmount = list.Sum(row => row.TotalAmount),
            TotalQuantity = list.Sum(row => row.TotalQuantity),
            // 同一分店跨供应商的汇总没有收据去重，调用方会将 0 视为不可用。
            OrderCount = ordersSafe ? list.Sum(row => row.OrderCount) : 0,
            StoreCount = list.Select(row => row.BranchCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            GrossProfit = list.All(row => RollupProfit(row).HasValue) ? list.Sum(row => row.GrossProfit ?? 0m) : null,
            StatisticRowCount = list.Sum(row => row.StatisticRowCount),
            CostedRowCount = list.Sum(row => row.CostedRowCount),
            GrossProfitRowCount = list.Sum(row => row.GrossProfitRowCount),
            InvalidRowCount = list.Sum(row => row.InvalidRowCount),
        };
    }

    private static void MergeSalesDetailRollupBucket(SalesDetailBucket bucket, SupplierRollupReadRow row, bool compare, bool byBranch)
    {
        if (compare)
        {
            bucket.CompareRevenue += row.TotalAmount;
            bucket.CompareQuantity += row.TotalQuantity;
            bucket.CompareOrderCount += row.OrderCount;
            bucket.CompareGrossProfit += row.GrossProfit ?? 0m;
            bucket.CompareStatisticRowCount += row.StatisticRowCount;
            bucket.CompareCostedRowCount += row.CostedRowCount;
            bucket.CompareGrossProfitRowCount += row.GrossProfitRowCount;
            bucket.CompareProductCodes.Add("rollup");
        }
        else
        {
            bucket.Revenue += row.TotalAmount;
            bucket.Quantity += row.TotalQuantity;
            bucket.OrderCount += row.OrderCount;
            bucket.GrossProfit += row.GrossProfit ?? 0m;
            bucket.StatisticRowCount += row.StatisticRowCount;
            bucket.CostedRowCount += row.CostedRowCount;
            bucket.GrossProfitRowCount += row.GrossProfitRowCount;
            bucket.ProductCodes.Add("rollup");
        }
    }

    private async Task<SalesDetailSectionResultDto> BuildSalesDetailSummaryAsync(
        DateRangeDto range,
        SalesDetailKind kind,
        List<string>? branches,
        string? supplier,
        HashSet<string>? productCodes,
        string? search,
        int pageIndex,
        int pageSize,
        SalesDetailLookupContext lookups,
        CancellationToken cancellationToken
    )
    {
        var current = await QuerySalesDetailFactsAsync(range.StartDate.Date, range.EndDate.Date, kind, branches, supplier, productCodes, search, lookups, cancellationToken);
        var compare = HasCompare(range)
            ? await QuerySalesDetailFactsAsync(range.CompareStartDate!.Value.Date, range.CompareEndDate!.Value.Date, kind, branches, supplier, productCodes, search, lookups, cancellationToken)
            : new List<SalesDetailFactRow>();
        var buckets = AggregateSalesDetailFacts(current, compare, SalesDetailSection.Summary);
        var summary = SumSalesDetailRows(
            buckets.Select(row => ToSalesDetailRow(row, SalesDetailSection.Summary, kind, null, HasCompare(range))).ToList(),
            "summary",
            "当前筛选汇总"
        );
        // 日统计的订单数按商品/分店记录，跨商品直接相加会重复计数；只有单商品范围才回显客单数。
        var canUseOrderCount = productCodes?.Count == 1;
        if (!canUseOrderCount)
        {
            summary.OrderCount = null;
            summary.CompareOrderCount = null;
        }
        return new SalesDetailSectionResultDto
        {
            Rows = new List<SalesDetailRowDto> { summary },
            Total = 1,
            Summary = summary,
            OrderCountNote = canUseOrderCount ? null : "跨商品/供应商范围未做收据去重，客单数返回 null。",
        };
    }

    private async Task<List<SalesDetailFactRow>> QuerySalesDetailFactsAsync(
        DateTime start,
        DateTime end,
        SalesDetailKind kind,
        List<string>? branchCodes,
        string? supplierCode,
        IReadOnlyCollection<string>? productCodes,
        string? search,
        SalesDetailLookupContext lookups,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (branchCodes != null && branchCodes.Count == 0)
            return new List<SalesDetailFactRow>();
        if (productCodes != null && productCodes.Count == 0)
            return new List<SalesDetailFactRow>();

        var chinaCatalog = await lookups.GetChinaCatalogAsync();
        var chinaCodes = chinaCatalog.Codes;
        var chinaMap = kind == SalesDetailKind.China
            ? await lookups.GetChinaSupplierProductMapAsync()
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = _context.Db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(row => row.Date >= start && row.Date <= end);
        if (branchCodes != null)
            query = query.Where(row => branchCodes.Contains(row.BranchCode));
        if (productCodes != null)
            query = query.Where(row => productCodes.Contains(row.ProductCode));

        // 供应商代码/旧 200 商品集合统一用 JSON 集合连接，避免超过 SQL Server 2100 参数。
        query = ApplySalesDetailKindScope(query, kind, supplierCode, chinaCodes, chinaMap);

        query = await ApplySalesDetailSearchFilterAsync(query, kind, search, lookups, cancellationToken);

        var factsQuery = query
            .GroupBy(row => new { row.SupplierCode, row.BranchCode, row.ProductCode })
            .Select(group => new SalesDetailFactRow
            {
                SupplierCode = group.SupplierCode,
                BranchCode = group.BranchCode,
                ProductCode = group.ProductCode,
                ProductName = SqlFunc.AggregateMax(group.ProductName),
                Barcode = SqlFunc.AggregateMax(group.Barcode),
                Quantity = SqlFunc.AggregateSum(group.TotalQuantity),
                Revenue = SqlFunc.AggregateSum(group.TotalAmount),
                OrderCount = SqlFunc.AggregateSum(group.OrderCount),
                GrossProfit = SqlFunc.AggregateSum(group.GrossProfit),
                StatisticRowCount = SqlFunc.AggregateCount(group.ProductCode),
                CostedRowCount = SqlFunc.AggregateCount(group.TotalCost),
                GrossProfitRowCount = SqlFunc.AggregateCount(group.GrossProfit),
            });
        var rows = await ReadSalesDetailQueryAsync(factsQuery, kind, cancellationToken);

        return rows
            .Select(row =>
            {
                if (kind == SalesDetailKind.China && row.SupplierCode == "200")
                    row.SupplierCode = chinaMap.GetValueOrDefault(row.ProductCode, string.Empty);
                else if (kind == SalesDetailKind.Australia && chinaCodes.Contains(row.SupplierCode))
                    row.SupplierCode = "200";
                return row;
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.SupplierCode)
                && (string.IsNullOrWhiteSpace(supplierCode)
                    || row.SupplierCode.Equals(supplierCode.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private async Task<ISugarQueryable<ProductStoreDailySalesStatistic>> ApplySalesDetailSearchFilterAsync(
        ISugarQueryable<ProductStoreDailySalesStatistic> query,
        SalesDetailKind kind,
        string? search,
        SalesDetailLookupContext lookups,
        CancellationToken cancellationToken
    )
    {
        var tokens = search?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
        if (tokens.Length == 0)
            return query;
        // 本方法运行在「按缓存键共用」的计算里；缓存键已区分这个开关，所以这里读到的值与发起方一致。
        var chinaNameSearchAllowed = DomesticSupplierInfoMasking.IsNameSearchAllowed;
        var localNames = kind == SalesDetailKind.Australia
            ? (await _context.Db.Queryable<HBLocalSupplier>()
                .Where(supplier => !supplier.IsDeleted)
                .Select(supplier => new { supplier.LocalSupplierCode, supplier.Name })
                .ToListAsync(cancellationToken))
                .Where(supplier => !string.IsNullOrWhiteSpace(supplier.LocalSupplierCode))
                .ToDictionary(supplier => supplier.LocalSupplierCode, supplier => supplier.Name ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 澳洲口径也要支持历史中国供应商名称反查 200；目录含软删/停用代码。
        var chinaCatalog = await lookups.GetChinaCatalogAsync();
        var chinaMap = await lookups.GetChinaSupplierProductMapAsync();
        for (var tokenIndex = 0; tokenIndex < tokens.Length; tokenIndex++)
        {
            var token = tokens[tokenIndex];
            cancellationToken.ThrowIfCancellationRequested();
            var supplierCodes = localNames
                .Where(supplier => ($"{supplier.Key} {supplier.Value}").Contains(token, StringComparison.OrdinalIgnoreCase))
                .Select(supplier => supplier.Key).ToList();
            // 没有名称权限时只按国内供应商代码匹配，不能拿关键词试探被遮掉的名称。
            var chinaSupplierCodes = chinaCatalog.Names
                .Where(item => (chinaNameSearchAllowed ? $"{item.Key} {item.Value}" : item.Key)
                    .Contains(token, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Key)
                .ToList();
            var chinaProducts = chinaMap
                .Where(item => chinaSupplierCodes.Contains(item.Value, StringComparer.OrdinalIgnoreCase))
                .Select(item => item.Key)
                .ToList();
            // 每个 token 是 OR；多个 token 通过多次 Where 形成 AND。
            // 商品主表子查询交给 SqlSugar 生成外层别名，避免手写 raw SQL 误把内层
            // ProductCode 与自身比较而放宽成全表命中。
            var predicate = Expressionable.Create<ProductStoreDailySalesStatistic>()
                .Or(row => (row.ProductCode != null && row.ProductCode.Contains(token))
                    || (row.SupplierCode != null && row.SupplierCode.Contains(token))
                    || (row.ProductName != null && row.ProductName.Contains(token))
                    || (row.Barcode != null && row.Barcode.Contains(token)))
                .Or(row => SqlFunc.Subqueryable<Product>().Where(product =>
                    product.ProductCode == row.ProductCode
                    && ((product.ProductCode != null && product.ProductCode.Contains(token))
                    || (product.ItemNumber != null && product.ItemNumber.Contains(token))
                    || (product.Barcode != null && product.Barcode.Contains(token))
                    || product.ProductName.Contains(token)
                    || (product.EnglishName != null && product.EnglishName.Contains(token))
                    || (supplierCodes.Count > 0 && supplierCodes.Contains(product.LocalSupplierCode!)))).Any());
            if (chinaSupplierCodes.Count > 0)
                predicate = predicate.Or(row => chinaSupplierCodes.Contains(row.SupplierCode!));
            var directQuery = query.Clone().Where(predicate.ToExpression());
            if (chinaProducts.Count > 0)
            {
                // POSM 在另一数据库，映射商品用单个 JSON 集合参数回连本地统计。
                var parameterName = $"@sdcSearchProducts{tokenIndex}";
                var jsonRows = _context.Db.CurrentConnectionConfig.DbType == DbType.SqlServer
                    ? $"OPENJSON({parameterName})"
                    : $"json_each({parameterName})";
                var mappedQuery = query.Clone().Where(
                    $"[ProductCode] IN (SELECT [value] FROM {jsonRows})",
                    new[] { new SugarParameter(parameterName, JsonSerializer.Serialize(chinaProducts)) }
                );
                query = _context.Db.Union(directQuery, mappedQuery).MergeTable();
            }
            else
            {
                query = directQuery;
            }
        }
        return query;
    }

    private ISugarQueryable<ProductStoreDailySalesStatistic> ApplySalesDetailKindScope(
        ISugarQueryable<ProductStoreDailySalesStatistic> query,
        SalesDetailKind kind,
        string? supplierCode,
        HashSet<string> chinaCodes,
        Dictionary<string, string> chinaMap
    )
    {
        var target = NormalizeValue(supplierCode);
        if (kind == SalesDetailKind.Australia && target == null)
            return query; // 澳洲默认包含全部销售，国内行只在结果归并为 200。

        var rows = _context.Db.CurrentConnectionConfig.DbType == DbType.SqlServer
            ? "OPENJSON({0})"
            : "json_each({0})";
        var parameters = new List<SugarParameter>();
        string Set(string name, IEnumerable<string> values)
        {
            var parameter = $"@{name}";
            parameters.Add(new SugarParameter(parameter, JsonSerializer.Serialize(
                values.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList())));
            return string.Format(rows, parameter);
        }

        string Equality(string valueName) => $"[SupplierCode] = {valueName}";
        var local200 = new SugarParameter("@sdcLocal200", "200");
        parameters.Add(local200);
        string? predicate;
        if (kind == SalesDetailKind.Australia)
        {
            if (target!.Equals("200", StringComparison.OrdinalIgnoreCase))
            {
                var chinaSet = Set("sdcChinaCodes", chinaCodes);
                predicate = $"({Equality("@sdcLocal200")} OR [SupplierCode] IN (SELECT [value] FROM {chinaSet}))";
            }
            else
            {
                var targetParameter = new SugarParameter("@sdcTargetSupplier", target);
                parameters.Add(targetParameter);
                predicate = Equality("@sdcTargetSupplier");
            }
        }
        else
        {
            var direct = target == null ? chinaCodes : new HashSet<string>(new[] { target }, StringComparer.OrdinalIgnoreCase);
            var directSet = Set("sdcChinaCodes", direct);
            var mapped = target == null
                ? chinaMap.Keys
                : chinaMap.Where(item => item.Value.Equals(target, StringComparison.OrdinalIgnoreCase)).Select(item => item.Key);
            var mappedList = mapped.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var mappedSet = Set("sdcLegacyProducts", mappedList);
            predicate = target == null
                ? $"([SupplierCode] IN (SELECT [value] FROM {directSet}) OR ([SupplierCode] = @sdcLocal200 AND [ProductCode] IN (SELECT [value] FROM {mappedSet})))"
                : $"([SupplierCode] = @sdcTargetSupplier OR ([SupplierCode] = @sdcLocal200 AND [ProductCode] IN (SELECT [value] FROM {mappedSet})))";
            if (target != null)
                parameters.Add(new SugarParameter("@sdcTargetSupplier", target));
        }
        return query.Where(predicate!, parameters.ToArray());
    }

    private async Task<(HashSet<string> Codes, int Total)> QuerySalesDetailProductPageAsync(
        DateRangeDto range,
        SalesDetailKind kind,
        List<string>? branches,
        string? supplierCode,
        IReadOnlyCollection<string>? allowedProductCodes,
        string? search,
        int pageIndex,
        int pageSize,
        SalesDetailLookupContext lookups,
        CancellationToken cancellationToken
    )
    {
        var currentQuery = await BuildSalesDetailProductStatisticQueryAsync(
            range.StartDate.Date, range.EndDate.Date, kind, branches, supplierCode, allowedProductCodes
            , search, lookups, cancellationToken
        );
        var periods = new List<ISugarQueryable<ProductReportProductAggregateRow>>
        {
            BuildProductReportProductAggregateQuery(currentQuery, 0),
        };
        if (HasCompare(range))
        {
            var compareQuery = await BuildSalesDetailProductStatisticQueryAsync(
                range.CompareStartDate!.Value.Date, range.CompareEndDate!.Value.Date,
                kind, branches, supplierCode, allowedProductCodes
                , search, lookups, cancellationToken
            );
            periods.Add(BuildProductReportProductAggregateQuery(compareQuery, 1));
        }
        var periodAggregate = periods.Count == 1
            ? periods[0]
            : _context.Db.UnionAll(periods.ToArray()).MergeTable();
        var combined = periodAggregate.GroupBy(row => row.ProductCode)
            .Select(row => new ProductReportProductCombinedAggregateRow
            {
                ProductCode = row.ProductCode,
                CurrentProductName = SqlFunc.AggregateMax(SqlFunc.IIF(row.Period == 0, row.ProductName, null)),
                CompareProductName = SqlFunc.AggregateMax(SqlFunc.IIF(row.Period == 1, row.ProductName, null)),
                CurrentQuantity = SqlFunc.AggregateSum(SqlFunc.IIF(row.Period == 0, row.Quantity, 0)),
                CurrentSalesAmount = SqlFunc.AggregateSum(SqlFunc.IIF(row.Period == 0, row.SalesAmount, 0m)),
                CurrentOrderCount = SqlFunc.AggregateSum(SqlFunc.IIF(row.Period == 0, row.OrderCount, 0)),
                CurrentGrossProfit = SqlFunc.AggregateSum(SqlFunc.IIF(row.Period == 0, row.GrossProfit, null)),
                CurrentStatisticRowCount = SqlFunc.AggregateSum(SqlFunc.IIF(row.Period == 0, row.StatisticRowCount, 0)),
                CurrentCostedRowCount = SqlFunc.AggregateSum(SqlFunc.IIF(row.Period == 0, row.CostedRowCount, 0)),
                CurrentGrossProfitRowCount = SqlFunc.AggregateSum(SqlFunc.IIF(row.Period == 0, row.GrossProfitRowCount, 0)),
                CompareQuantity = SqlFunc.AggregateSum(SqlFunc.IIF(row.Period == 1, row.Quantity, 0)),
                CompareSalesAmount = SqlFunc.AggregateSum(SqlFunc.IIF(row.Period == 1, row.SalesAmount, 0m)),
                CompareOrderCount = SqlFunc.AggregateSum(SqlFunc.IIF(row.Period == 1, row.OrderCount, 0)),
                CompareGrossProfit = SqlFunc.AggregateSum(SqlFunc.IIF(row.Period == 1, row.GrossProfit, null)),
                CompareStatisticRowCount = SqlFunc.AggregateSum(SqlFunc.IIF(row.Period == 1, row.StatisticRowCount, 0)),
                CompareCostedRowCount = SqlFunc.AggregateSum(SqlFunc.IIF(row.Period == 1, row.CostedRowCount, 0)),
                CompareGrossProfitRowCount = SqlFunc.AggregateSum(SqlFunc.IIF(row.Period == 1, row.GrossProfitRowCount, 0)),
            }).MergeTable();
        cancellationToken.ThrowIfCancellationRequested();
        var total = (await ReadSalesDetailQueryAsync(
            combined.Clone().Select(row => SqlFunc.AggregateCount(1)), kind, cancellationToken)).Single();
        // 排序必须发生在数据库 Skip/Take 之前，保证跨页按本期数量全量降序。
        var pageQuery = combined
            .OrderBy(row => row.CurrentQuantity, OrderByType.Desc)
            .OrderBy(row => row.CompareQuantity, OrderByType.Desc)
            .OrderBy(row => row.ProductCode, OrderByType.Asc)
            .Skip((pageIndex - 1) * pageSize)
            .Take(pageSize);
        var pageRows = await ReadSalesDetailQueryAsync(pageQuery, kind, cancellationToken);
        return (pageRows.Select(row => row.ProductCode).ToHashSet(StringComparer.OrdinalIgnoreCase), total);
    }

    private Task<List<T>> ReadSalesDetailQueryAsync<T>(
        ISugarQueryable<T> query, SalesDetailKind kind, CancellationToken cancellationToken)
    {
        if (kind != SalesDetailKind.China || _context.Db.CurrentConnectionConfig.DbType != DbType.SqlServer)
            return query.ToListAsync(cancellationToken);

        // OPENJSON 默认低估映射集合，容易逐行重复解析上万商品；哈希连接让集合一次参与匹配。
        // 仅调整国内三栏读取的执行计划，仍沿用原参数、分页、取消令牌和外层快照事务。
        var sql = query.ToSql();
        return _context.Db.Ado.SqlQueryAsync<T>(sql.Key + " OPTION (RECOMPILE, HASH JOIN)", sql.Value.ToArray(), cancellationToken);
    }

    private async Task<ISugarQueryable<ProductStoreDailySalesStatistic>> BuildSalesDetailProductStatisticQueryAsync(
        DateTime start,
        DateTime end,
        SalesDetailKind kind,
        List<string>? branches,
        string? supplierCode,
        IReadOnlyCollection<string>? allowedProductCodes,
        string? search,
        SalesDetailLookupContext lookups,
        CancellationToken cancellationToken
    )
    {
        var chinaCatalog = await lookups.GetChinaCatalogAsync();
        var chinaCodes = chinaCatalog.Codes;
        var chinaMap = kind == SalesDetailKind.China
            ? await lookups.GetChinaSupplierProductMapAsync()
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = _context.Db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(row => row.Date >= start && row.Date <= end);
        if (branches != null)
            query = query.Where(row => branches.Contains(row.BranchCode));
        if (allowedProductCodes != null)
        {
            var list = allowedProductCodes.ToList();
            query = query.Where(row => list.Contains(row.ProductCode));
        }
        query = ApplySalesDetailKindScope(query, kind, supplierCode, chinaCodes, chinaMap);
        query = await ApplySalesDetailSearchFilterAsync(query, kind, search, lookups, cancellationToken);
        return query;
    }

    private static List<SalesDetailBucket> AggregateSalesDetailFacts(
        IEnumerable<SalesDetailFactRow> current,
        IEnumerable<SalesDetailFactRow> compare,
        SalesDetailSection section
    )
    {
        static string Key(SalesDetailFactRow row, SalesDetailSection type) => type switch
        {
            SalesDetailSection.Suppliers => row.SupplierCode,
            SalesDetailSection.Branches => row.BranchCode,
            _ => row.ProductCode,
        };
        var buckets = new Dictionary<string, SalesDetailBucket>(StringComparer.OrdinalIgnoreCase);
        void Add(IEnumerable<SalesDetailFactRow> rows, bool isCompare)
        {
            foreach (var fact in rows)
            {
                var key = Key(fact, section).Trim();
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new SalesDetailBucket { Code = key, Name = section == SalesDetailSection.Products ? fact.ProductName : null };
                    buckets[key] = bucket;
                }
                if (isCompare)
                {
                    bucket.CompareProductCodes.Add(fact.ProductCode);
                    bucket.CompareRevenue += fact.Revenue;
                    bucket.CompareQuantity += fact.Quantity;
                    bucket.CompareOrderCount += fact.OrderCount;
                    bucket.CompareGrossProfit += fact.GrossProfit ?? 0m;
                    bucket.CompareStatisticRowCount += fact.StatisticRowCount;
                    bucket.CompareCostedRowCount += fact.CostedRowCount;
                    bucket.CompareGrossProfitRowCount += fact.GrossProfitRowCount;
                }
                else
                {
                    bucket.ProductCodes.Add(fact.ProductCode);
                    bucket.Revenue += fact.Revenue;
                    bucket.Quantity += fact.Quantity;
                    bucket.OrderCount += fact.OrderCount;
                    bucket.GrossProfit += fact.GrossProfit ?? 0m;
                    bucket.StatisticRowCount += fact.StatisticRowCount;
                    bucket.CostedRowCount += fact.CostedRowCount;
                    bucket.GrossProfitRowCount += fact.GrossProfitRowCount;
                }
            }
        }
        Add(current, false);
        Add(compare, true);
        return buckets.Values.ToList();
    }

    private async Task FillSalesDetailNamesAsync(
        List<SalesDetailBucket> buckets,
        SalesDetailSection section,
        SalesDetailKind kind,
        SalesDetailLookupContext lookups,
        CancellationToken cancellationToken
    )
    {
        if (section == SalesDetailSection.Suppliers)
        {
            Dictionary<string, string> names;
            if (kind == SalesDetailKind.China)
            {
                // 三栏需展示期间历史名称，不能因供应商已软删而退化成代码。
                var catalog = await lookups.GetChinaCatalogAsync();
                names = buckets.ToDictionary(
                    bucket => bucket.Code,
                    bucket => catalog.Names.GetValueOrDefault(bucket.Code, bucket.Code),
                    StringComparer.OrdinalIgnoreCase
                );
            }
            else
            {
                names = await GetAustralianSupplierNameMapAsync(buckets.Select(bucket => bucket.Code));
            }
            foreach (var bucket in buckets)
                bucket.Name = names.GetValueOrDefault(bucket.Code, bucket.Code);
        }
        else if (section == SalesDetailSection.Branches)
        {
            var names = await GetStoreNameMapAsync(buckets.Select(bucket => bucket.Code)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
            foreach (var bucket in buckets)
                bucket.Name = names.GetValueOrDefault(bucket.Code, bucket.Code);
        }
        else if (section == SalesDetailSection.Products)
        {
            var codes = buckets.Select(bucket => bucket.Code).ToList();
            var metadata = await _context.Db.Queryable<Product>()
                .Where(product => product.ProductCode != null && codes.Contains(product.ProductCode))
                .Select(product => new
                {
                    Code = product.ProductCode!,
                    product.ProductName,
                    product.ItemNumber,
                    product.ProductImage,
                })
            .ToListAsync(cancellationToken);
            var map = metadata.GroupBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var bucket in buckets)
            {
                if (!map.TryGetValue(bucket.Code, out var item))
                    continue;
                bucket.Name = string.IsNullOrWhiteSpace(item.ProductName) ? bucket.Name : item.ProductName;
                bucket.ItemNumber = item.ItemNumber;
                bucket.ProductImage = item.ProductImage;
            }
        }
    }

    private async Task<SalesDetailDenominator> QuerySalesDetailDenominatorAsync(
        DateRangeDto range,
        List<string>? branches,
        SalesDetailLookupContext lookups,
        CancellationToken cancellationToken
    )
    {
        if (_useSupplierRollups)
        {
            var rollupAustralia = await QuerySupplierRollupRowsAsync(false, false, range.StartDate, range.EndDate, branches, null, cancellationToken);
            var rollupChina = await QuerySupplierRollupRowsAsync(true, false, range.StartDate, range.EndDate, branches, null, cancellationToken);
            var rollupResult = new SalesDetailDenominator
            {
                // 澳洲 rollup 已包含 200/国内货的澳洲销售，不能再把中国 rollup 重复相加。
                AllRevenue = rollupAustralia.Sum(row => row.TotalAmount),
                ChinaRevenue = rollupChina.Sum(row => row.TotalAmount),
            };
            if (HasCompare(range))
            {
                var compareRollupAustralia = await QuerySupplierRollupRowsAsync(false, false, range.CompareStartDate!.Value, range.CompareEndDate!.Value, branches, null, cancellationToken);
                var compareRollupChina = await QuerySupplierRollupRowsAsync(true, false, range.CompareStartDate.Value, range.CompareEndDate.Value, branches, null, cancellationToken);
                rollupResult.CompareAllRevenue = compareRollupAustralia.Sum(row => row.TotalAmount);
                rollupResult.CompareChinaRevenue = compareRollupChina.Sum(row => row.TotalAmount);
            }
            return rollupResult;
        }
        var currentAustralia = await QuerySalesDetailFactsAsync(range.StartDate.Date, range.EndDate.Date, SalesDetailKind.Australia, branches, null, null, null, lookups, cancellationToken);
        var currentChina = await QuerySalesDetailFactsAsync(range.StartDate.Date, range.EndDate.Date, SalesDetailKind.China, branches, null, null, null, lookups, cancellationToken);
        var result = new SalesDetailDenominator
        {
            // AU 查询已包含直接中国编码归并后的 200 行，China 仅作为拆分分子。
            AllRevenue = currentAustralia.Sum(row => row.Revenue),
            ChinaRevenue = currentChina.Sum(row => row.Revenue),
        };
        if (HasCompare(range))
        {
            var compareAustralia = await QuerySalesDetailFactsAsync(range.CompareStartDate!.Value.Date, range.CompareEndDate!.Value.Date, SalesDetailKind.Australia, branches, null, null, null, lookups, cancellationToken);
            var compareChina = await QuerySalesDetailFactsAsync(range.CompareStartDate.Value.Date, range.CompareEndDate.Value.Date, SalesDetailKind.China, branches, null, null, null, lookups, cancellationToken);
            result.CompareAllRevenue = compareAustralia.Sum(row => row.Revenue);
            result.CompareChinaRevenue = compareChina.Sum(row => row.Revenue);
        }
        return result;
    }

    private static SalesDetailRowDto ToSalesDetailRow(
        SalesDetailBucket bucket,
        SalesDetailSection section,
        SalesDetailKind kind,
        SalesDetailDenominator? denominator,
        bool hasCompare
    )
    {
        var currentGrossProfit = CompleteGrossProfit(bucket.GrossProfit, bucket.StatisticRowCount, bucket.CostedRowCount, bucket.GrossProfitRowCount);
        var compareGrossProfit = hasCompare
            ? CompleteGrossProfit(bucket.CompareGrossProfit, bucket.CompareStatisticRowCount, bucket.CompareCostedRowCount, bucket.CompareGrossProfitRowCount)
            : null;
        var row = new SalesDetailRowDto
        {
            Code = bucket.Code,
            Name = bucket.Name ?? bucket.Code,
            ItemNumber = bucket.ItemNumber,
            ProductImage = bucket.ProductImage,
            Revenue = bucket.Revenue,
            CompareRevenue = hasCompare ? bucket.CompareRevenue : null,
            Quantity = bucket.Quantity,
            CompareQuantity = hasCompare ? bucket.CompareQuantity : null,
            OrderCount = bucket.ProductCodes.Count == 1 ? bucket.OrderCount : null,
            CompareOrderCount = hasCompare && bucket.CompareProductCodes.Count == 1 ? bucket.CompareOrderCount : null,
            GrossProfit = currentGrossProfit,
            CompareGrossProfit = compareGrossProfit,
            GrossMarginRate = CalculateGrossMarginRate(bucket.Revenue, currentGrossProfit),
            CompareGrossMarginRate = hasCompare ? CalculateGrossMarginRate(bucket.CompareRevenue, compareGrossProfit) : null,
        };
        // 三栏都展示商品数量和商品均价，供应商/分店仍保留客单字段供汇总接口兼容。
        row.AverageUnitPrice = bucket.Quantity > 0 ? bucket.Revenue / bucket.Quantity : null;
        row.CompareAverageUnitPrice = hasCompare && bucket.CompareQuantity > 0
            ? bucket.CompareRevenue / bucket.CompareQuantity
            : null;
        if (section != SalesDetailSection.Products)
        {
            row.AverageTransaction = bucket.ProductCodes.Count == 1 && bucket.OrderCount > 0
                ? bucket.Revenue / bucket.OrderCount
                : null;
            row.CompareAverageTransaction = hasCompare && bucket.CompareProductCodes.Count == 1 && bucket.CompareOrderCount > 0
                ? bucket.CompareRevenue / bucket.CompareOrderCount
                : null;
        }
        if (section == SalesDetailSection.Suppliers && denominator != null)
        {
            var primary = kind == SalesDetailKind.China ? denominator.ChinaRevenue : denominator.AllRevenue;
            var comparePrimary = kind == SalesDetailKind.China ? denominator.CompareChinaRevenue : denominator.CompareAllRevenue;
            row.Share = primary > 0m ? row.Revenue / primary : null;
            row.CompareShare = hasCompare && comparePrimary > 0m && row.CompareRevenue.HasValue
                ? row.CompareRevenue.Value / comparePrimary
                : null;
            if (kind == SalesDetailKind.China)
            {
                row.ChinaShare = denominator.AllRevenue > 0m ? row.Revenue / denominator.AllRevenue : null;
                row.CompareChinaShare = hasCompare && denominator.CompareAllRevenue > 0m && row.CompareRevenue.HasValue
                    ? row.CompareRevenue.Value / denominator.CompareAllRevenue
                    : null;
            }
        }
        return row;
    }

    private static SalesDetailRowDto SumSalesDetailRows(
        IEnumerable<SalesDetailRowDto> rows,
        string code,
        string name
    )
    {
        var list = rows.ToList();
        var currentQuantity = list.Sum(row => row.Quantity);
        var compareQuantity = list.Any(row => row.CompareQuantity.HasValue) ? list.Sum(row => row.CompareQuantity ?? 0) : (int?)null;
        var currentOrders = list.Count > 0 && list.All(row => row.OrderCount.HasValue) ? list.Sum(row => row.OrderCount!.Value) : (int?)null;
        var compareOrders = list.Count > 0 && list.Any(row => row.CompareOrderCount.HasValue) && list.All(row => row.CompareOrderCount.HasValue)
            ? list.Sum(row => row.CompareOrderCount!.Value)
            : (int?)null;
        var revenue = list.Sum(row => row.Revenue);
        var compareRevenue = list.Any(row => row.CompareRevenue.HasValue) ? list.Sum(row => row.CompareRevenue ?? 0m) : (decimal?)null;
        var gross = list.Count > 0 && list.All(row => row.GrossProfit.HasValue) ? list.Sum(row => row.GrossProfit!.Value) : (decimal?)null;
        var compareGross = list.Count > 0 && list.Any(row => row.CompareGrossProfit.HasValue) && list.All(row => row.CompareGrossProfit.HasValue)
            ? list.Sum(row => row.CompareGrossProfit!.Value)
            : (decimal?)null;
        return new SalesDetailRowDto
        {
            Code = code,
            Name = name,
            Revenue = revenue,
            CompareRevenue = compareRevenue,
            Quantity = currentQuantity,
            CompareQuantity = compareQuantity,
            OrderCount = currentOrders,
            CompareOrderCount = compareOrders,
            AverageTransaction = currentOrders > 0 ? revenue / currentOrders : null,
            CompareAverageTransaction = compareRevenue.HasValue && compareOrders > 0 ? compareRevenue.Value / compareOrders : null,
            AverageUnitPrice = currentQuantity > 0 ? revenue / currentQuantity : null,
            CompareAverageUnitPrice = compareRevenue.HasValue && compareQuantity > 0 ? compareRevenue.Value / compareQuantity : null,
            GrossProfit = gross,
            CompareGrossProfit = compareGross,
            GrossMarginRate = CalculateGrossMarginRate(revenue, gross),
            CompareGrossMarginRate = compareRevenue.HasValue ? CalculateGrossMarginRate(compareRevenue.Value, compareGross) : null,
        };
    }

    private static decimal? CompleteGrossProfit(decimal grossProfit, int rows, int costed, int complete)
    {
        return rows > 0 && rows == costed && rows == complete ? grossProfit : null;
    }

    private static bool HasCompare(DateRangeDto range) => range.CompareStartDate.HasValue && range.CompareEndDate.HasValue;

    private static string? NormalizeValue(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
