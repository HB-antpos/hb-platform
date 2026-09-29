using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class LocalSupplierInvoiceSalesAnalysisService
        : ILocalSupplierInvoiceSalesAnalysisService
    {
        private readonly ISqlSugarClient _db;
        private readonly ILogger<LocalSupplierInvoiceSalesAnalysisService> _logger;

        public LocalSupplierInvoiceSalesAnalysisService(
            SqlSugarContext context,
            ILogger<LocalSupplierInvoiceSalesAnalysisService> logger
        )
        {
            _db = context.Db;
            _logger = logger;
        }

        public async Task<ApiResponse<LocalSupplierInvoiceSalesAnalysisResponseDto>> GetAnalysisAsync(
            string invoiceGuid
        )
        {
            try
            {
                var headerSql = LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildHeader(invoiceGuid);
                var headerRows = await _db.Ado.SqlQueryAsync<LocalSupplierInvoiceSalesAnalysisHeaderRow>(
                    headerSql.Sql,
                    headerSql.Parameters.ToArray()
                );
                var header = headerRows.FirstOrDefault();
                if (header == null)
                {
                    return ApiResponse<LocalSupplierInvoiceSalesAnalysisResponseDto>.Error(
                        "进货单不存在",
                        "NOT_FOUND"
                    );
                }

                var detailSql = LocalSupplierInvoiceSalesAnalysisSqlBuilder.Build(invoiceGuid);
                var rows = await _db.Ado.SqlQueryAsync<LocalSupplierInvoiceSalesAnalysisItemRow>(
                    detailSql.Sql,
                    detailSql.Parameters.ToArray()
                );

                return ApiResponse<LocalSupplierInvoiceSalesAnalysisResponseDto>.OK(
                    new LocalSupplierInvoiceSalesAnalysisResponseDto
                    {
                        InvoiceGUID = header.InvoiceGUID,
                        InvoiceNo = header.InvoiceNo,
                        StoreCode = header.StoreCode,
                        StoreName = header.StoreName,
                        SupplierCode = header.SupplierCode,
                        SupplierName = header.SupplierName,
                        OrderDate = header.OrderDate,
                        InboundDate = header.InboundDate,
                        AnalysisDate = header.AnalysisDate,
                        Items = rows.Cast<LocalSupplierInvoiceSalesAnalysisItemDto>().ToList(),
                        SalesStatisticLastUpdate = rows
                            .Select(item => item.SalesStatisticLastUpdate)
                            .Where(value => value.HasValue)
                            .DefaultIfEmpty()
                            .Max(),
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分店进货单销量分析查询失败 InvoiceGuid={InvoiceGuid}", invoiceGuid);
                return ApiResponse<LocalSupplierInvoiceSalesAnalysisResponseDto>.Error(
                    "分店进货单销量分析查询失败",
                    "QUERY_ERROR"
                );
            }
        }

        public async Task<ApiResponse<LocalSupplierPurchaseSalesAnalysisResponseDto>> GetPurchaseSalesAnalysisAsync(
            LocalSupplierPurchaseSalesAnalysisQueryDto query,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            try
            {
                var normalized = LocalSupplierInvoiceSalesAnalysisSqlBuilder.NormalizePurchaseSalesAnalysisQuery(
                    query
                );
                var validation =
                    LocalSupplierInvoiceSalesAnalysisSqlBuilder.ValidatePurchaseSalesAnalysisQuery(
                        normalized
                    );
                if (!validation.IsValid)
                {
                    return ApiResponse<LocalSupplierPurchaseSalesAnalysisResponseDto>.Error(
                        validation.Message ?? "进货订单日期范围无效",
                        "VALIDATION_ERROR"
                    );
                }

                var sql = LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildPurchaseSalesAnalysis(
                    normalized,
                    scopedStoreCodes,
                    GetBrisbaneToday()
                );

                _logger.LogInformation(
                    "查询分店供应商进货销量分析 StoreScope={StoreScope}, StoreCode={StoreCode}, SupplierCode={SupplierCode}, SortBy={SortBy}, SortOrder={SortOrder}, Page={Page}, PageSize={PageSize}",
                    scopedStoreCodes == null ? "ALL" : string.Join(",", scopedStoreCodes),
                    normalized.StoreCode ?? "ALL",
                    normalized.SupplierCode ?? "ALL",
                    normalized.SortBy,
                    normalized.SortOrder,
                    normalized.Page,
                    normalized.PageSize
                );

                // 进货销量分析是重查询，分段计时以便定位慢在主查询还是逐日补充。
                var pagedStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var rows = await _db.Ado.SqlQueryAsync<LocalSupplierPurchaseSalesAnalysisSqlRow>(
                    sql.PagedSql,
                    sql.Parameters.ToArray()
                );
                pagedStopwatch.Stop();

                // 分页结果本身已带总数与统计更新时间；只有当前页为空（无数据或页码越界）才需要单独汇总。
                var totalCount = rows.Count > 0 ? rows[0].TotalCount : 0;
                var salesStatisticLastUpdate = rows.Count > 0
                    ? rows[0].OverallSalesStatisticLastUpdate
                    : null;
                if (rows.Count == 0)
                {
                    var summaryRows =
                        await _db.Ado.SqlQueryAsync<LocalSupplierPurchaseSalesAnalysisSummaryRow>(
                            sql.SummarySql,
                            sql.Parameters.ToArray()
                        );
                    var summary = summaryRows.FirstOrDefault();
                    totalCount = summary?.TotalCount ?? 0;
                    salesStatisticLastUpdate = summary?.SalesStatisticLastUpdate;
                }

                // 逐日序列只为当前页补充，不改分页 SQL 与排序；失败时降级为空序列，不影响主查询。
                var items = rows.Select(row => row.ToDto()).ToList();
                var dailyStopwatch = System.Diagnostics.Stopwatch.StartNew();
                if (normalized.IncludeDailySales)
                {
                    await AttachDailySeriesAsync(items);
                }
                dailyStopwatch.Stop();

                _logger.LogInformation(
                    "分店供应商进货销量分析耗时 PagedMs={PagedMs} DailyMs={DailyMs} IncludeDaily={IncludeDaily} Rows={Rows} Total={Total}",
                    pagedStopwatch.ElapsedMilliseconds,
                    dailyStopwatch.ElapsedMilliseconds,
                    normalized.IncludeDailySales,
                    items.Count,
                    totalCount
                );

                return ApiResponse<LocalSupplierPurchaseSalesAnalysisResponseDto>.OK(
                    new LocalSupplierPurchaseSalesAnalysisResponseDto
                    {
                        Items = items,
                        Total = totalCount,
                        Page = normalized.Page,
                        PageSize = normalized.PageSize,
                        SalesStatisticLastUpdate = salesStatisticLastUpdate,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分店供应商进货销量分析查询失败");
                return ApiResponse<LocalSupplierPurchaseSalesAnalysisResponseDto>.Error(
                    "分店供应商进货销量分析查询失败",
                    "QUERY_ERROR"
                );
            }
        }

        public async Task<List<LocalSupplierPurchaseSalesAnalysisStoreOptionDto>> GetStoreOptionsAsync(
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var sql = LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildPurchaseSalesAnalysisStoreOptions(
                scopedStoreCodes
            );

            return await _db.Ado.SqlQueryAsync<LocalSupplierPurchaseSalesAnalysisStoreOptionDto>(
                sql.Sql,
                sql.Parameters.ToArray()
            );
        }

        public async Task<List<LocalSupplierPurchaseSalesAnalysisSupplierOptionDto>> GetSupplierOptionsAsync(
            IReadOnlyList<string>? scopedStoreCodes,
            string? storeCode
        )
        {
            var sql =
                LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildPurchaseSalesAnalysisSupplierOptions(
                    storeCode,
                    scopedStoreCodes
                );

            return await _db.Ado.SqlQueryAsync<LocalSupplierPurchaseSalesAnalysisSupplierOptionDto>(
                sql.Sql,
                sql.Parameters.ToArray()
            );
        }

        public async Task<List<LocalSupplierCategoryNodeDto>> GetPurchaseSalesAnalysisCategoryTreeAsync(string supplierCode)
        {
            var supplier = supplierCode.Trim();
            var nodes = new List<LocalSupplierCategoryNodeDto>();
            if (string.Equals(supplier, "200", StringComparison.OrdinalIgnoreCase))
            {
                var categories = await _db.Queryable<WarehouseCategory>()
                    .Where(category => category.IsDeleted == false && category.IsActive)
                    .Select(category => new { category.CategoryGUID, category.ParentGUID, category.CategoryName, category.SortOrder })
                    .ToListAsync();
                nodes.AddRange(categories.Select(category => new LocalSupplierCategoryNodeDto {
                    CategoryGuid = category.CategoryGUID, ParentGuid = category.ParentGUID,
                    Name = category.CategoryName, SortOrder = category.SortOrder, IsActive = true
                }));
            }
            else
            {
                var categories = await _db.Queryable<LocalSupplierCategory>()
                    .Where(category => category.LocalSupplierCode == supplier && category.IsDeleted == false && category.IsActive)
                    .Select(category => new { category.CategoryGUID, category.ParentGUID, category.CategoryName, category.SortOrder, category.FullPath, category.Depth })
                    .ToListAsync();
                nodes.AddRange(categories.Select(category => new LocalSupplierCategoryNodeDto {
                    CategoryGuid = category.CategoryGUID, ParentGuid = category.ParentGUID,
                    Name = category.CategoryName, SortOrder = category.SortOrder, FullPath = category.FullPath,
                    Depth = category.Depth, IsActive = true
                }));
            }
            var byGuid = nodes.ToDictionary(node => node.CategoryGuid, StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes) node.Children = new List<LocalSupplierCategoryNodeDto>();
            var roots = new List<LocalSupplierCategoryNodeDto>();
            foreach (var node in nodes)
            {
                if (!string.IsNullOrWhiteSpace(node.ParentGuid) && byGuid.TryGetValue(node.ParentGuid, out var parent)) parent.Children.Add(node);
                else roots.Add(node);
            }
            static void Sort(List<LocalSupplierCategoryNodeDto> list)
            {
                list.Sort((a, b) => (a.SortOrder ?? int.MaxValue) != (b.SortOrder ?? int.MaxValue)
                    ? (a.SortOrder ?? int.MaxValue).CompareTo(b.SortOrder ?? int.MaxValue)
                    : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var node in list) Sort(node.Children);
            }
            Sort(roots);
            return roots;
        }

        /// <summary>
        /// 为当前页每行填充进货事件与逐日销量序列。
        /// 窗口：上次进货日（没有则最近进货日前 30 天）到布里斯班业务日期今天。
        /// </summary>
        /// <remarks>internal 且允许注入 today，仅为了让测试固定业务日期；生产调用不传。</remarks>
        internal async Task AttachDailySeriesAsync(
            List<LocalSupplierPurchaseSalesAnalysisRowDto> items,
            DateTime? referenceToday = null
        )
        {
            var today = (referenceToday ?? GetBrisbaneToday()).Date;
            var windows =
                new List<(LocalSupplierPurchaseSalesAnalysisRowDto Row, DateTime Start, DateTime End)>();

            foreach (var item in items)
            {
                // 没有最近进货日期就无法确定窗口，两个列表保持为空。
                if (!item.LatestPurchaseDate.HasValue)
                {
                    continue;
                }

                // 进货事件只依赖分页行本身，不受逐日查询成败影响。
                item.Purchases = LocalSupplierPurchaseSalesDailySeriesBuilder.BuildPurchaseEvents(
                    item.PreviousPurchaseDate,
                    item.PreviousPurchaseQty,
                    item.LatestPurchaseDate,
                    item.LatestPurchaseQty
                );

                var (start, end) = LocalSupplierPurchaseSalesDailySeriesBuilder.ResolveWindow(
                    item.PreviousPurchaseDate,
                    item.LatestPurchaseDate.Value,
                    today
                );
                windows.Add((item, start, end));
            }

            if (windows.Count == 0)
            {
                return;
            }

            try
            {
                // 同一页正常只有一个门店；仍按行的 StoreCode 分组，每个门店只发一条 SQL，绝不逐行查询。
                foreach (
                    var storeGroup in windows.GroupBy(
                        window => window.Row.StoreCode,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                {
                    var storeCode = storeGroup.Key;
                    var productCodes = storeGroup
                        .Select(window => window.Row.ProductCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (string.IsNullOrWhiteSpace(storeCode) || productCodes.Count == 0)
                    {
                        continue;
                    }

                    var minStart = storeGroup.Min(window => window.Start);
                    // 结束日用半开区间（< 次日零点）表达「包含 end 当天」，不依赖 Date 列是否带时间部分或数据库的日期精度。
                    var endExclusive = storeGroup.Max(window => window.End).AddDays(1);

                    var statistics = await _db.Queryable<ProductStoreDailySalesStatistic>()
                        .Where(stat =>
                            stat.BranchCode == storeCode
                            && productCodes.Contains(stat.ProductCode)
                            && stat.Date >= minStart
                            && stat.Date < endExclusive
                        )
                        .Select(stat => new DailyQuantityRow
                        {
                            ProductCode = stat.ProductCode,
                            Date = stat.Date,
                            TotalQuantity = stat.TotalQuantity,
                        })
                        .ToListAsync();

                    // 同一商品同一天可能有多条不同 SupplierCode 的统计记录，必须按 商品+日期 求和；退货负数原样保留。
                    var quantitiesByProduct = statistics
                        .GroupBy(stat => stat.ProductCode, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            productGroup => productGroup.Key,
                            productGroup =>
                                (IReadOnlyDictionary<DateTime, int>)productGroup
                                    .GroupBy(stat => stat.Date.Date)
                                    .ToDictionary(
                                        dayGroup => dayGroup.Key,
                                        dayGroup => dayGroup.Sum(stat => stat.TotalQuantity)
                                    ),
                            StringComparer.OrdinalIgnoreCase
                        );

                    foreach (var window in storeGroup)
                    {
                        var quantities = quantitiesByProduct.TryGetValue(
                            window.Row.ProductCode,
                            out var found
                        )
                            ? found
                            : EmptyDailyQuantities;
                        window.Row.DailySales =
                            LocalSupplierPurchaseSalesDailySeriesBuilder.BuildDailySeries(
                                window.Start,
                                window.End,
                                quantities
                            );
                    }
                }
            }
            catch (Exception ex)
            {
                // 逐日序列只是图表增强数据：失败时整页统一回退为空列表，避免出现半页有图半页无图，也不让主查询失败。
                _logger.LogError(ex, "分店供应商进货销量分析逐日销量序列查询失败");
                foreach (var window in windows)
                {
                    window.Row.DailySales = new List<LocalSupplierPurchaseSalesDailyPointDto>();
                }
            }
        }

        private static readonly IReadOnlyDictionary<DateTime, int> EmptyDailyQuantities =
            new Dictionary<DateTime, int>();

        /// <summary>布里斯班业务日期今天；与批量货号销量分析保持同一口径。</summary>
        private static DateTime GetBrisbaneToday()
        {
            try
            {
                return TimeZoneInfo
                    .ConvertTimeFromUtc(
                        DateTime.UtcNow,
                        TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")
                    )
                    .Date;
            }
            catch (TimeZoneNotFoundException)
            {
                return DateTime.UtcNow.Date;
            }
        }

        private sealed class DailyQuantityRow
        {
            public string ProductCode { get; set; } = string.Empty;
            public DateTime Date { get; set; }
            public int TotalQuantity { get; set; }
        }

        private sealed class LocalSupplierInvoiceSalesAnalysisHeaderRow
        {
            public string InvoiceGUID { get; set; } = string.Empty;
            public string? InvoiceNo { get; set; }
            public string? StoreCode { get; set; }
            public string? StoreName { get; set; }
            public string? SupplierCode { get; set; }
            public string? SupplierName { get; set; }
            public DateTime? OrderDate { get; set; }
            public DateTime? InboundDate { get; set; }
            public DateTime? AnalysisDate { get; set; }
        }

        private sealed class LocalSupplierInvoiceSalesAnalysisItemRow
            : LocalSupplierInvoiceSalesAnalysisItemDto { }

        private sealed class LocalSupplierPurchaseSalesAnalysisSqlRow
            : LocalSupplierPurchaseSalesAnalysisRowDto
        {
            public int TotalCount { get; set; }
            public DateTime? OverallSalesStatisticLastUpdate { get; set; }

            // 窗口列只服务于分页响应头，不能随行数据一起序列化给前端。
            public LocalSupplierPurchaseSalesAnalysisRowDto ToDto() =>
                new()
                {
                    StoreCode = StoreCode,
                    StoreName = StoreName,
                    ProductCode = ProductCode,
                    ItemNumber = ItemNumber,
                    Barcode = Barcode,
                    ProductName = ProductName,
                    ProductImage = ProductImage,
                    SupplierCode = SupplierCode,
                    SupplierName = SupplierName,
                    LatestPurchaseDate = LatestPurchaseDate,
                    LatestPurchaseQty = LatestPurchaseQty,
                    PreviousPurchaseDate = PreviousPurchaseDate,
                    PreviousPurchaseQty = PreviousPurchaseQty,
                    PurchaseIntervalDays = PurchaseIntervalDays,
                    SalesBetweenPurchases = SalesBetweenPurchases,
                    SalesQty30 = SalesQty30,
                    SalesQty60 = SalesQty60,
                    SalesQty90 = SalesQty90,
                    TotalSalesSinceLatestPurchase = TotalSalesSinceLatestPurchase,
                    SalesStatisticLastUpdate = SalesStatisticLastUpdate,
                };
        }

        private sealed class LocalSupplierPurchaseSalesAnalysisSummaryRow
        {
            public int TotalCount { get; set; }
            public DateTime? SalesStatisticLastUpdate { get; set; }
        }
    }

    public static class LocalSupplierInvoiceSalesAnalysisSqlBuilder
    {
        public const int DefaultPurchaseOrderDateRangeDays = 180;
        public const int MaxPurchaseOrderDateRangeDays = 366;

        private static readonly HashSet<int> AllowedPageSizes = new() { 50, 100, 200 };

        private static readonly Dictionary<string, string> PurchaseSalesSortColumns =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["itemNumber"] = "ItemNumber",
                ["productName"] = "ProductName",
                ["latestPurchaseDate"] = "LatestPurchaseDate",
                ["previousPurchaseDate"] = "PreviousPurchaseDate",
                ["purchaseIntervalDays"] = "PurchaseIntervalDays",
                ["salesBetweenPurchases"] = "SalesBetweenPurchases",
                ["salesQty30"] = "SalesQty30",
                ["salesQty60"] = "SalesQty60",
                ["salesQty90"] = "SalesQty90",
                // 总销量 = 最近进货当天起至今的累计净销量，与页面图表、售出比同一口径。
                ["totalSalesSinceLatestPurchase"] = "TotalSalesSinceLatestPurchase",
            };

        public static LocalSupplierInvoiceSalesAnalysisSqlBuildResult BuildHeader(string invoiceGuid)
        {
            var parameters = BuildParameters(invoiceGuid);
            var sql =
                "SELECT TOP 1\n"
                + "    h.InvoiceGUID AS InvoiceGUID,\n"
                + "    h.InvoiceNo AS InvoiceNo,\n"
                + "    h.StoreCode AS StoreCode,\n"
                + "    st.StoreName AS StoreName,\n"
                + "    h.SupplierCode AS SupplierCode,\n"
                + "    sup.Name AS SupplierName,\n"
                + "    h.OrderDate AS OrderDate,\n"
                + "    h.InboundDate AS InboundDate,\n"
                + "    CAST(COALESCE(h.InboundDate, h.OrderDate, h.CreatedAt) AS date) AS AnalysisDate\n"
                + "FROM [StoreLocalSupplierInvoice] h\n"
                + "LEFT JOIN [Store] st\n"
                + "    ON st.StoreCode = h.StoreCode\n"
                + "    AND st.IsDeleted = 0\n"
                + "LEFT JOIN [LocalSupplier] sup\n"
                + "    ON sup.LocalSupplierCode = h.SupplierCode\n"
                + "    AND sup.IsDeleted = 0\n"
                + "WHERE\n"
                + "    h.InvoiceGUID = @InvoiceGuid\n"
                + "    AND h.IsDeleted = 0";

            return new LocalSupplierInvoiceSalesAnalysisSqlBuildResult
            {
                Sql = sql,
                Parameters = parameters,
            };
        }

        public static LocalSupplierInvoiceSalesAnalysisSqlBuildResult Build(string invoiceGuid)
        {
            var parameters = BuildParameters(invoiceGuid);
            var sql =
                "WITH CurrentDetails AS (\n"
                + "    SELECT\n"
                + "        d.DetailGUID,\n"
                + "        d.InvoiceGUID,\n"
                + "        COALESCE(NULLIF(d.StoreCode, N''), NULLIF(h.StoreCode, N''), NULLIF(srp.StoreCode, N'')) AS StoreCode,\n"
                + "        -- 本次进货后销量以入库日优先；未入库时回退到订单日。\n"
                + "        CAST(COALESCE(h.InboundDate, h.OrderDate, h.CreatedAt) AS date) AS AnalysisDate,\n"
                + "        COALESCE(NULLIF(d.ProductCode, N''), NULLIF(srp.ProductCode, N'')) AS ProductCode,\n"
                + "        NULLIF(d.ItemNumber, N'') AS ItemNumber,\n"
                + "        COALESCE(NULLIF(d.Barcode, N''), NULLIF(p.Barcode, N'')) AS Barcode,\n"
                + "        COALESCE(NULLIF(d.ProductName, N''), NULLIF(p.ProductName, N'')) AS ProductName,\n"
                + "        -- 进货明细的 ProductImage 是代码层忽略字段，实际图片只从 Product 表读取。\n"
                + "        NULLIF(p.ProductImage, N'') AS ProductImage,\n"
                + "        NULLIF(d.Specification, N'') AS Specification,\n"
                + "        NULLIF(d.Unit, N'') AS Unit,\n"
                + "        d.Quantity,\n"
                + "        d.PurchasePrice,\n"
                + "        d.RetailPrice,\n"
                + "        d.Amount\n"
                + "    FROM [StoreLocalSupplierInvoiceDetails] d\n"
                + "    INNER JOIN [StoreLocalSupplierInvoice] h\n"
                + "        ON h.InvoiceGUID = d.InvoiceGUID\n"
                + "        AND h.IsDeleted = 0\n"
                + "    LEFT JOIN [StoreRetailPrice] srp\n"
                + "        ON srp.UUID = d.StoreProductCode\n"
                + "        AND srp.IsDeleted = 0\n"
                + "    LEFT JOIN [Product] p\n"
                + "        ON p.ProductCode = COALESCE(NULLIF(d.ProductCode, N''), NULLIF(srp.ProductCode, N''))\n"
                + "        AND p.IsDeleted = 0\n"
                + "    -- 删除标记统一写成 IsDeleted = 0，才能命中明细表 InvoiceGUID 过滤索引，避免 65 万行全表扫描。\n"
                + "    WHERE\n"
                + "        d.InvoiceGUID = @InvoiceGuid\n"
                + "        AND d.IsDeleted = 0\n"
                + "),\n"
                + "CurrentProducts AS (\n"
                + "    SELECT\n"
                + "        cd.StoreCode,\n"
                + "        cd.ProductCode,\n"
                + "        MIN(cd.AnalysisDate) AS AnalysisDate\n"
                + "    FROM CurrentDetails cd\n"
                + "    WHERE\n"
                + "        cd.StoreCode IS NOT NULL\n"
                + "        AND cd.ProductCode IS NOT NULL\n"
                + "    GROUP BY\n"
                + "        cd.StoreCode,\n"
                + "        cd.ProductCode\n"
                + "),\n"
                + "PreviousPurchase AS (\n"
                + "    SELECT\n"
                + "        cp.StoreCode,\n"
                + "        cp.ProductCode,\n"
                + "        MAX(COALESCE(pi.InboundDate, pi.OrderDate)) AS PreviousPurchaseDate\n"
                + "    FROM CurrentProducts cp\n"
                + "    -- 历史明细已有商品编码，直接按商品命中 (ProductCode, InvoiceGUID) 过滤索引，再回连单据校验门店与日期；\n"
                + "    -- 列上不能包 NULLIF，且要显式写 ProductCode <> N'' 才能匹配过滤索引定义，否则会按门店枚举全部单据逐张回表。\n"
                + "    INNER JOIN [StoreLocalSupplierInvoiceDetails] pd\n"
                + "        ON pd.ProductCode = cp.ProductCode\n"
                + "        AND pd.ProductCode <> N''\n"
                + "        AND pd.IsDeleted = 0\n"
                + "    INNER JOIN [StoreLocalSupplierInvoice] pi\n"
                + "        ON pi.InvoiceGUID = pd.InvoiceGUID\n"
                + "        AND pi.StoreCode = cp.StoreCode\n"
                + "        AND pi.IsDeleted = 0\n"
                + "        AND COALESCE(pi.InboundDate, pi.OrderDate) IS NOT NULL\n"
                + "        AND CAST(COALESCE(pi.InboundDate, pi.OrderDate) AS date) < cp.AnalysisDate\n"
                + "        AND pi.InvoiceGUID <> @InvoiceGuid\n"
                + "    GROUP BY\n"
                + "        cp.StoreCode,\n"
                + "        cp.ProductCode\n"
                + "),\n"
                + "SalesMetrics AS (\n"
                + "    SELECT\n"
                + "        cp.StoreCode,\n"
                + "        cp.ProductCode,\n"
                + "        SUM(CASE WHEN s.Date >= DATEADD(day, 1, cp.AnalysisDate) AND s.Date < DATEADD(day, 31, cp.AnalysisDate) THEN COALESCE(s.TotalQuantity, 0) ELSE 0 END) AS SalesQty30,\n"
                + "        SUM(CASE WHEN s.Date >= DATEADD(day, 1, cp.AnalysisDate) AND s.Date < DATEADD(day, 61, cp.AnalysisDate) THEN COALESCE(s.TotalQuantity, 0) ELSE 0 END) AS SalesQty60,\n"
                + "        SUM(CASE WHEN s.Date >= DATEADD(day, 1, cp.AnalysisDate) AND s.Date < DATEADD(day, 91, cp.AnalysisDate) THEN COALESCE(s.TotalQuantity, 0) ELSE 0 END) AS SalesQty90,\n"
                + "        SUM(CASE WHEN pp.PreviousPurchaseDate IS NOT NULL AND s.Date >= CAST(pp.PreviousPurchaseDate AS date) AND s.Date < DATEADD(day, 1, cp.AnalysisDate) THEN COALESCE(s.TotalQuantity, 0) ELSE 0 END) AS SalesSincePreviousPurchase,\n"
                + "        SUM(CASE WHEN pp.PreviousPurchaseDate IS NOT NULL AND s.Date >= CASE WHEN DATEADD(day, -29, cp.AnalysisDate) > CAST(pp.PreviousPurchaseDate AS date) THEN DATEADD(day, -29, cp.AnalysisDate) ELSE CAST(pp.PreviousPurchaseDate AS date) END AND s.Date < DATEADD(day, 1, cp.AnalysisDate) THEN COALESCE(s.TotalQuantity, 0) ELSE 0 END) AS SalesSincePreviousPurchase30,\n"
                + "        SUM(CASE WHEN pp.PreviousPurchaseDate IS NOT NULL AND s.Date >= CASE WHEN DATEADD(day, -59, cp.AnalysisDate) > CAST(pp.PreviousPurchaseDate AS date) THEN DATEADD(day, -59, cp.AnalysisDate) ELSE CAST(pp.PreviousPurchaseDate AS date) END AND s.Date < DATEADD(day, 1, cp.AnalysisDate) THEN COALESCE(s.TotalQuantity, 0) ELSE 0 END) AS SalesSincePreviousPurchase60,\n"
                + "        SUM(CASE WHEN pp.PreviousPurchaseDate IS NOT NULL AND s.Date >= CASE WHEN DATEADD(day, -89, cp.AnalysisDate) > CAST(pp.PreviousPurchaseDate AS date) THEN DATEADD(day, -89, cp.AnalysisDate) ELSE CAST(pp.PreviousPurchaseDate AS date) END AND s.Date < DATEADD(day, 1, cp.AnalysisDate) THEN COALESCE(s.TotalQuantity, 0) ELSE 0 END) AS SalesSincePreviousPurchase90,\n"
                + "        MAX(s.UpdateTime) AS SalesStatisticLastUpdate\n"
                + "    FROM CurrentProducts cp\n"
                + "    LEFT JOIN PreviousPurchase pp\n"
                + "        ON pp.StoreCode = cp.StoreCode\n"
                + "        AND pp.ProductCode = cp.ProductCode\n"
                + "    LEFT JOIN [ProductStoreDailySalesStatistic] s\n"
                + "        ON s.BranchCode = cp.StoreCode\n"
                + "        AND s.ProductCode = cp.ProductCode\n"
                + "        -- 日销售统计只有日期粒度：进货后30/60/90统计本次进货次日起未来窗口，历史区间仍截止到本次进货日。\n"
                + "        AND (\n"
                + "            (s.Date >= DATEADD(day, 1, cp.AnalysisDate) AND s.Date < DATEADD(day, 91, cp.AnalysisDate))\n"
                + "            OR (pp.PreviousPurchaseDate IS NOT NULL AND s.Date >= CAST(pp.PreviousPurchaseDate AS date) AND s.Date < DATEADD(day, 1, cp.AnalysisDate))\n"
                + "        )\n"
                + "    GROUP BY\n"
                + "        cp.StoreCode,\n"
                + "        cp.ProductCode\n"
                + ")\n"
                + "SELECT\n"
                + "    cd.DetailGUID AS DetailGUID,\n"
                + "    cd.ProductCode AS ProductCode,\n"
                + "    cd.ItemNumber AS ItemNumber,\n"
                + "    cd.Barcode AS Barcode,\n"
                + "    cd.ProductName AS ProductName,\n"
                + "    cd.ProductImage AS ProductImage,\n"
                + "    cd.Specification AS Specification,\n"
                + "    cd.Unit AS Unit,\n"
                + "    cd.Quantity AS Quantity,\n"
                + "    cd.PurchasePrice AS PurchasePrice,\n"
                + "    cd.RetailPrice AS RetailPrice,\n"
                + "    cd.Amount AS Amount,\n"
                + "    COALESCE(sm.SalesQty30, 0) AS SalesQty30,\n"
                + "    COALESCE(sm.SalesQty60, 0) AS SalesQty60,\n"
                + "    COALESCE(sm.SalesQty90, 0) AS SalesQty90,\n"
                + "    pp.PreviousPurchaseDate AS PreviousPurchaseDate,\n"
                + "    CASE WHEN pp.PreviousPurchaseDate IS NULL THEN NULL ELSE DATEDIFF(day, pp.PreviousPurchaseDate, cd.AnalysisDate) END AS PreviousToCurrentDays,\n"
                + "    CASE WHEN pp.PreviousPurchaseDate IS NULL THEN NULL ELSE COALESCE(sm.SalesSincePreviousPurchase, 0) END AS SalesSincePreviousPurchase,\n"
                + "    CASE WHEN pp.PreviousPurchaseDate IS NULL THEN NULL ELSE COALESCE(sm.SalesSincePreviousPurchase30, 0) END AS SalesSincePreviousPurchase30,\n"
                + "    CASE WHEN pp.PreviousPurchaseDate IS NULL THEN NULL ELSE COALESCE(sm.SalesSincePreviousPurchase60, 0) END AS SalesSincePreviousPurchase60,\n"
                + "    CASE WHEN pp.PreviousPurchaseDate IS NULL THEN NULL ELSE COALESCE(sm.SalesSincePreviousPurchase90, 0) END AS SalesSincePreviousPurchase90,\n"
                + "    sm.SalesStatisticLastUpdate AS SalesStatisticLastUpdate\n"
                + "FROM CurrentDetails cd\n"
                + "LEFT JOIN PreviousPurchase pp\n"
                + "    ON pp.StoreCode = cd.StoreCode\n"
                + "    AND pp.ProductCode = cd.ProductCode\n"
                + "LEFT JOIN SalesMetrics sm\n"
                + "    ON sm.StoreCode = cd.StoreCode\n"
                + "    AND sm.ProductCode = cd.ProductCode\n"
                + "ORDER BY\n"
                + "    cd.ProductCode,\n"
                + "    cd.DetailGUID";

            return new LocalSupplierInvoiceSalesAnalysisSqlBuildResult
            {
                Sql = sql,
                Parameters = parameters,
            };
        }

        public static LocalSupplierPurchaseSalesAnalysisQueryDto NormalizePurchaseSalesAnalysisQuery(
            LocalSupplierPurchaseSalesAnalysisQueryDto query,
            DateTime? referenceDate = null
        )
        {
            var normalizedSortBy = NormalizeText(query.SortBy);
            if (
                string.IsNullOrWhiteSpace(normalizedSortBy)
                || !PurchaseSalesSortColumns.ContainsKey(normalizedSortBy)
            )
            {
                normalizedSortBy = "totalSalesSinceLatestPurchase";
            }

            var normalizedSortOrder = string.Equals(
                NormalizeText(query.SortOrder),
                "asc",
                StringComparison.OrdinalIgnoreCase
            )
                ? "asc"
                : "desc";

            var pageSize = AllowedPageSizes.Contains(query.PageSize) ? query.PageSize : 100;
            var today = (referenceDate ?? DateTime.Today).Date;
            var orderDateEnd = query.OrderDateEnd?.Date ?? today;
            var orderDateStart = query.OrderDateStart?.Date
                ?? orderDateEnd.AddDays(-DefaultPurchaseOrderDateRangeDays);

            return new LocalSupplierPurchaseSalesAnalysisQueryDto
            {
                StoreCode = NormalizeText(query.StoreCode),
                SupplierCode = NormalizeText(query.SupplierCode),
                SupplierCategoryGuids = query.SupplierCategoryGuids?
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                OrderDateStart = orderDateStart,
                OrderDateEnd = orderDateEnd,
                Keyword = NormalizeText(query.Keyword),
                SortBy = normalizedSortBy,
                SortOrder = normalizedSortOrder,
                Page = query.Page <= 0 ? 1 : query.Page,
                PageSize = pageSize,
                IncludeDailySales = query.IncludeDailySales,
            };
        }

        public static (bool IsValid, string? Message) ValidatePurchaseSalesAnalysisQuery(
            LocalSupplierPurchaseSalesAnalysisQueryDto query
        )
        {
            if (string.IsNullOrWhiteSpace(query.StoreCode))
            {
                return (false, "分店不能为空。");
            }

            if (string.IsNullOrWhiteSpace(query.SupplierCode))
            {
                return (false, "供应商不能为空。");
            }

            if (query.SupplierCategoryGuids?.Count > 100)
            {
                return (false, "供应商分类最多选择 100 个。");
            }

            if (!query.OrderDateStart.HasValue || !query.OrderDateEnd.HasValue)
            {
                return (false, "进货订单日期范围不能为空。");
            }

            if (query.OrderDateStart.Value.Date > query.OrderDateEnd.Value.Date)
            {
                return (false, "进货订单开始日期不能晚于结束日期。");
            }

            var rangeDays = (query.OrderDateEnd.Value.Date - query.OrderDateStart.Value.Date).TotalDays;
            if (rangeDays > MaxPurchaseOrderDateRangeDays)
            {
                return (
                    false,
                    $"进货订单日期范围不能超过 {MaxPurchaseOrderDateRangeDays} 天。"
                );
            }

            return (true, null);
        }

        public static LocalSupplierPurchaseSalesAnalysisSqlBuildResult BuildPurchaseSalesAnalysis(
            LocalSupplierPurchaseSalesAnalysisQueryDto query,
            IReadOnlyList<string>? scopedStoreCodes,
            DateTime? referenceToday = null
        )
        {
            var normalized = NormalizePurchaseSalesAnalysisQuery(query);
            var validation = ValidatePurchaseSalesAnalysisQuery(normalized);
            if (!validation.IsValid)
            {
                throw new ArgumentException(validation.Message, nameof(query));
            }

            var parameters = new List<SugarParameter>
            {
                new("@Offset", (normalized.Page - 1) * normalized.PageSize),
                new("@PageSize", normalized.PageSize),
                // 总销量要统计到"今天"，用半开区间（< 次日零点）表达包含当天，不依赖 Date 列的时间部分。
                new("@SalesWindowEndExclusive", (referenceToday ?? DateTime.UtcNow).Date.AddDays(1)),
            };

            if (normalized.OrderDateStart.HasValue)
            {
                parameters.Add(new SugarParameter("@OrderDateStart", normalized.OrderDateStart.Value));
            }

            if (normalized.OrderDateEnd.HasValue)
            {
                parameters.Add(
                    new SugarParameter("@OrderDateEndExclusive", normalized.OrderDateEnd.Value.AddDays(1))
                );
            }

            if (!string.IsNullOrWhiteSpace(normalized.SupplierCode))
            {
                parameters.Add(new SugarParameter("@SupplierCode", normalized.SupplierCode));
            }

            if (!string.IsNullOrWhiteSpace(normalized.Keyword))
            {
                parameters.Add(
                    new SugarParameter(
                        "@Keyword",
                        "%" + EscapeLikeValue(normalized.Keyword) + "%"
                    )
                );
            }

            var categoryGuidParameterNames = new List<string>();
            foreach (var guid in normalized.SupplierCategoryGuids ?? new List<string>())
            {
                var parameterName = $"@SupplierCategoryGuid{categoryGuidParameterNames.Count}";
                parameters.Add(new SugarParameter(parameterName, guid));
                categoryGuidParameterNames.Add(parameterName);
            }

            var storeCodes = ResolveStoreCodes(normalized.StoreCode, scopedStoreCodes);
            var storeParameterNames = AddStoreParameters(parameters, storeCodes);
            var invoiceStoreFilter = BuildStoreFilter("h.StoreCode", storeParameterNames);
            var invoiceDateFilter = BuildInvoiceDateFilter(normalized);
            var productFilter = BuildProductFilter(normalized);
            var orderBy = BuildPurchaseSalesOrderBy(normalized);
            var categoryFilter = BuildSupplierCategoryFilter(categoryGuidParameterNames);
            var categoryCte = categoryGuidParameterNames.Count == 0
                ? string.Empty
                : "\nLocalCategoryScope AS (\n"
                    + "    SELECT c.CategoryGUID FROM [LocalSupplierCategory] c\n"
                    + "    WHERE @SupplierCode <> N'200' AND c.LocalSupplierCode = @SupplierCode AND c.IsDeleted = 0 AND c.IsActive = 1\n"
                    + "      AND c.CategoryGUID IN (" + string.Join(", ", categoryGuidParameterNames) + ")\n"
                    + "    UNION ALL\n"
                    + "    SELECT child.CategoryGUID FROM [LocalSupplierCategory] child\n"
                    + "    INNER JOIN LocalCategoryScope parent ON child.ParentGUID = parent.CategoryGUID\n"
                    + "    WHERE child.LocalSupplierCode = @SupplierCode AND child.IsDeleted = 0 AND child.IsActive = 1\n"
                    + "), WarehouseCategoryScope AS (\n"
                    + "    SELECT c.CategoryGUID FROM [WarehouseCategory] c\n"
                    + "    WHERE @SupplierCode = N'200' AND c.IsDeleted = 0 AND c.IsActive = 1\n"
                    + "      AND c.CategoryGUID IN (" + string.Join(", ", categoryGuidParameterNames) + ")\n"
                    + "    UNION ALL\n"
                    + "    SELECT child.CategoryGUID FROM [WarehouseCategory] child\n"
                    + "    INNER JOIN WarehouseCategoryScope parent ON child.ParentGUID = parent.CategoryGUID\n"
                    + "    WHERE child.IsDeleted = 0 AND child.IsActive = 1\n"
                    + "),\n";

            var coreSql =
                $$"""
WITH FilteredInvoices AS (
    SELECT
        h.InvoiceGUID,
        h.StoreCode,
        h.OrderDate,
        CAST(COALESCE(h.InboundDate, h.OrderDate, h.CreatedAt) AS date) AS PurchaseDate
    FROM [StoreLocalSupplierInvoice] h
    -- 删除标记统一写成 IsDeleted = 0：实体层该列不可空且库内没有 NULL，
    -- 而 COALESCE(IsDeleted, 0) = 0 会让优化器放弃所有 WHERE IsDeleted = 0 的过滤索引，退化成 65 万行明细全表扫描。
    WHERE
        h.IsDeleted = 0
        AND CAST(COALESCE(h.InboundDate, h.OrderDate, h.CreatedAt) AS date) IS NOT NULL{{invoiceStoreFilter}}{{invoiceDateFilter}}
),{{categoryCte}}
DetailResolved AS (
    -- 先把明细与分店零售价关联、确定最终 ProductCode，再去 JOIN Product。
    -- 原先 JOIN Product 的条件是跨 d/srp 两表的 COALESCE 表达式，优化器无法用 ProductCode 索引，
    -- 只能整表扫描后哈希；拆开后条件变成单列等值，执行计划更稳定（实测耗时波动从 1.4~6.6s 收敛到 2.6~2.7s）。
    SELECT
        fi.StoreCode,
        fi.PurchaseDate,
        d.Quantity,
        d.ItemNumber AS DetailItemNumber,
        d.Barcode AS DetailBarcode,
        d.ProductName AS DetailProductName,
        srp.SupplierCode AS RetailSupplierCode,
        COALESCE(NULLIF(d.ProductCode, N''), NULLIF(srp.ProductCode, N'')) AS ProductCode
    FROM FilteredInvoices fi
    INNER JOIN [StoreLocalSupplierInvoiceDetails] d
        ON d.InvoiceGUID = fi.InvoiceGUID
        AND d.IsDeleted = 0
    LEFT JOIN [StoreRetailPrice] srp
        ON srp.UUID = d.StoreProductCode
        AND srp.IsDeleted = 0
    WHERE NULLIF(fi.StoreCode, N'') IS NOT NULL{{categoryFilter}}
),
PurchaseDailyAggregation AS (
    SELECT
        dr.StoreCode AS StoreCode,
        COALESCE(NULLIF(st.StoreName, N''), dr.StoreCode) AS StoreName,
        dr.ProductCode AS ProductCode,
        COALESCE(NULLIF(dr.DetailItemNumber, N''), NULLIF(p.ItemNumber, N'')) AS ItemNumber,
        COALESCE(NULLIF(dr.DetailBarcode, N''), NULLIF(p.Barcode, N'')) AS Barcode,
        COALESCE(NULLIF(dr.DetailProductName, N''), NULLIF(p.ProductName, N'')) AS ProductName,
        NULLIF(p.ProductImage, N'') AS ProductImage,
        COALESCE(NULLIF(p.LocalSupplierCode, N''), NULLIF(dr.RetailSupplierCode, N'')) AS SupplierCode,
        NULLIF(sup.Name, N'') AS SupplierName,
        dr.PurchaseDate AS PurchaseDate,
        SUM(COALESCE(dr.Quantity, 0)) AS PurchaseQty
    FROM DetailResolved dr
    LEFT JOIN [Product] p
        ON p.ProductCode = dr.ProductCode
        AND p.IsDeleted = 0
    LEFT JOIN [Store] st
        ON st.StoreCode = dr.StoreCode
        AND st.IsDeleted = 0
    LEFT JOIN [LocalSupplier] sup
        ON sup.LocalSupplierCode = COALESCE(NULLIF(p.LocalSupplierCode, N''), NULLIF(dr.RetailSupplierCode, N''))
        AND sup.IsDeleted = 0
    WHERE
        NULLIF(dr.ProductCode, N'') IS NOT NULL
        AND COALESCE(NULLIF(p.LocalSupplierCode, N''), NULLIF(dr.RetailSupplierCode, N'')) = @SupplierCode
        AND NULLIF(COALESCE(NULLIF(p.LocalSupplierCode, N''), NULLIF(dr.RetailSupplierCode, N'')), N'') IS NOT NULL
    GROUP BY
        dr.StoreCode,
        COALESCE(NULLIF(st.StoreName, N''), dr.StoreCode),
        dr.ProductCode,
        COALESCE(NULLIF(dr.DetailItemNumber, N''), NULLIF(p.ItemNumber, N'')),
        COALESCE(NULLIF(dr.DetailBarcode, N''), NULLIF(p.Barcode, N'')),
        COALESCE(NULLIF(dr.DetailProductName, N''), NULLIF(p.ProductName, N'')),
        NULLIF(p.ProductImage, N''),
        COALESCE(NULLIF(p.LocalSupplierCode, N''), NULLIF(dr.RetailSupplierCode, N'')),
        NULLIF(sup.Name, N''),
        dr.PurchaseDate
),
RankedPurchases AS (
    SELECT
        pda.StoreCode,
        pda.StoreName,
        pda.ProductCode,
        pda.ItemNumber,
        pda.Barcode,
        pda.ProductName,
        pda.ProductImage,
        pda.SupplierCode,
        pda.SupplierName,
        pda.PurchaseDate,
        pda.PurchaseQty,
        -- 在同一次排名中读取上次进货，避免多次展开进货汇总并重复扫描明细、商品表。
        LEAD(pda.PurchaseDate) OVER (
            PARTITION BY pda.StoreCode, pda.ProductCode
            ORDER BY pda.PurchaseDate DESC
        ) AS PreviousPurchaseDate,
        LEAD(pda.PurchaseQty) OVER (
            PARTITION BY pda.StoreCode, pda.ProductCode
            ORDER BY pda.PurchaseDate DESC
        ) AS PreviousPurchaseQty,
        ROW_NUMBER() OVER (
            PARTITION BY pda.StoreCode, pda.ProductCode
            ORDER BY pda.PurchaseDate DESC
        ) AS PurchaseRank
    FROM PurchaseDailyAggregation pda
),
LatestPurchases AS (
    SELECT
        rp.StoreCode,
        rp.StoreName,
        rp.ProductCode,
        rp.ItemNumber,
        rp.Barcode,
        rp.ProductName,
        rp.ProductImage,
        rp.SupplierCode,
        rp.SupplierName,
        rp.PurchaseDate AS LatestPurchaseDate,
        rp.PurchaseQty AS LatestPurchaseQty,
        rp.PreviousPurchaseDate,
        rp.PreviousPurchaseQty
    FROM RankedPurchases rp
    WHERE rp.PurchaseRank = 1
),
FinalRows AS (
    SELECT
        lp.StoreCode AS StoreCode,
        lp.StoreName AS StoreName,
        lp.ProductCode AS ProductCode,
        lp.ItemNumber AS ItemNumber,
        lp.Barcode AS Barcode,
        lp.ProductName AS ProductName,
        lp.ProductImage AS ProductImage,
        lp.SupplierCode AS SupplierCode,
        lp.SupplierName AS SupplierName,
        lp.LatestPurchaseDate AS LatestPurchaseDate,
        CAST(lp.LatestPurchaseQty AS decimal(18, 2)) AS LatestPurchaseQty,
        lp.PreviousPurchaseDate AS PreviousPurchaseDate,
        CAST(lp.PreviousPurchaseQty AS decimal(18, 2)) AS PreviousPurchaseQty,
        CASE
            WHEN lp.PreviousPurchaseDate IS NULL THEN NULL
            ELSE DATEDIFF(day, lp.PreviousPurchaseDate, lp.LatestPurchaseDate)
        END AS PurchaseIntervalDays,
        CASE
            WHEN lp.PreviousPurchaseDate IS NULL THEN NULL
            ELSE COALESCE(sm.SalesBetweenPurchases, 0)
        END AS SalesBetweenPurchases,
        COALESCE(sm.SalesQty30, 0) AS SalesQty30,
        COALESCE(sm.SalesQty60, 0) AS SalesQty60,
        COALESCE(sm.SalesQty90, 0) AS SalesQty90,
        COALESCE(sm.TotalSalesSinceLatestPurchase, 0) AS TotalSalesSinceLatestPurchase,
        sm.SalesStatisticLastUpdate AS SalesStatisticLastUpdate
    FROM LatestPurchases lp
    -- 上次到最近、最近到 90 天是连续区间；保留各销量窗口的半开边界与退货负数。
    OUTER APPLY (
        SELECT SUM(daily.SalesQty30) AS SalesQty30,
               SUM(daily.SalesQty60) AS SalesQty60,
               SUM(daily.SalesQty90) AS SalesQty90,
               SUM(daily.TotalSalesSinceLatestPurchase) AS TotalSalesSinceLatestPurchase,
               SUM(daily.SalesBetweenPurchases) AS SalesBetweenPurchases,
               MAX(daily.UpdateTime) AS SalesStatisticLastUpdate
        -- 先投影再聚合，避免 SQL Server 将外部日期引用与销量列视为非法混合聚合。
        FROM (
            SELECT
                CASE WHEN s.Date >= lp.LatestPurchaseDate AND s.Date < DATEADD(day, 30, lp.LatestPurchaseDate) THEN COALESCE(s.TotalQuantity, 0) ELSE 0 END AS SalesQty30,
                CASE WHEN s.Date >= lp.LatestPurchaseDate AND s.Date < DATEADD(day, 60, lp.LatestPurchaseDate) THEN COALESCE(s.TotalQuantity, 0) ELSE 0 END AS SalesQty60,
                CASE WHEN s.Date >= lp.LatestPurchaseDate AND s.Date < DATEADD(day, 90, lp.LatestPurchaseDate) THEN COALESCE(s.TotalQuantity, 0) ELSE 0 END AS SalesQty90,
                CASE WHEN lp.PreviousPurchaseDate IS NOT NULL AND s.Date >= lp.PreviousPurchaseDate AND s.Date < lp.LatestPurchaseDate THEN COALESCE(s.TotalQuantity, 0) ELSE 0 END AS SalesBetweenPurchases,
                -- 总销量不设上界；实际上界由下面 WHERE 的窗口末端（90 天窗口与今天取较晚者）决定。
                CASE WHEN s.Date >= lp.LatestPurchaseDate THEN COALESCE(s.TotalQuantity, 0) ELSE 0 END AS TotalSalesSinceLatestPurchase,
                s.UpdateTime
            FROM [ProductStoreDailySalesStatistic] s
            WHERE s.BranchCode = lp.StoreCode
              AND s.ProductCode = lp.ProductCode
              AND s.Date >= COALESCE(lp.PreviousPurchaseDate, lp.LatestPurchaseDate)
              AND s.Date < CASE
                              WHEN DATEADD(day, 90, lp.LatestPurchaseDate) > @SalesWindowEndExclusive
                                  THEN DATEADD(day, 90, lp.LatestPurchaseDate)
                              ELSE @SalesWindowEndExclusive
                          END
        ) daily
    ) sm
)
""";

            var pagedSql =
                coreSql
                + $$"""
SELECT
    StoreCode,
    StoreName,
    ProductCode,
    ItemNumber,
    Barcode,
    ProductName,
    ProductImage,
    SupplierCode,
    SupplierName,
    LatestPurchaseDate,
    LatestPurchaseQty,
    PreviousPurchaseDate,
    PreviousPurchaseQty,
    PurchaseIntervalDays,
    SalesBetweenPurchases,
    SalesQty30,
    SalesQty60,
    SalesQty90,
    TotalSalesSinceLatestPurchase,
    SalesStatisticLastUpdate,
    -- 总数与统计更新时间随分页一起带出，避免同一套 CTE 为汇总再完整执行一遍。
    COUNT(1) OVER () AS TotalCount,
    MAX(SalesStatisticLastUpdate) OVER () AS OverallSalesStatisticLastUpdate
FROM FinalRows
ORDER BY
    {{orderBy}}
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
""";

            var summarySql =
                coreSql
                + """
SELECT
    COUNT(1) AS TotalCount,
    MAX(SalesStatisticLastUpdate) AS SalesStatisticLastUpdate
FROM FinalRows
""";

            return new LocalSupplierPurchaseSalesAnalysisSqlBuildResult
            {
                PagedSql = pagedSql,
                SummarySql = summarySql,
                Parameters = parameters,
            };
        }

        public static LocalSupplierInvoiceSalesAnalysisSqlBuildResult BuildPurchaseSalesAnalysisStoreOptions(
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var parameters = new List<SugarParameter>();
            var storeParameterNames = AddStoreParameters(parameters, scopedStoreCodes);
            var storeFilter = BuildStoreFilter("h.StoreCode", storeParameterNames);

            // 下拉候选从报表真实进货数据取，避免主数据 Active 标记缺失时页面无可选分店。
            var sql =
                $$"""
SELECT
    COALESCE(NULLIF(st.StoreName, N''), source.StoreCode) AS Label,
    source.StoreCode AS Value
FROM (
    SELECT DISTINCT
        h.StoreCode
    FROM [StoreLocalSupplierInvoice] h
    WHERE
        h.IsDeleted = 0
        AND NULLIF(h.StoreCode, N'') IS NOT NULL{{storeFilter}}
) source
LEFT JOIN [Store] st
    ON st.StoreCode = source.StoreCode
    AND st.IsDeleted = 0
ORDER BY
    source.StoreCode
""";

            return new LocalSupplierInvoiceSalesAnalysisSqlBuildResult
            {
                Sql = sql,
                Parameters = parameters,
            };
        }

        public static LocalSupplierInvoiceSalesAnalysisSqlBuildResult BuildPurchaseSalesAnalysisSupplierOptions(
            string? storeCode,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var parameters = new List<SugarParameter>();
            var storeCodes = ResolveStoreCodes(storeCode, scopedStoreCodes);
            var storeParameterNames = AddStoreParameters(parameters, storeCodes);
            var storeFilter = BuildStoreFilter("h.StoreCode", storeParameterNames);

            // 供应商候选和主查询保持同一口径：商品主供应商优先，分店价格表供应商兜底。
            // 先把门店全部历史明细收敛成不重复的 (商品编码, 分店价格 UUID) 对，再回填价格表与商品表，
            // 避免对几万行明细逐行查 470 万行的分店价格表。
            var sql =
                $$"""
WITH StorePairs AS (
    SELECT DISTINCT
        NULLIF(d.ProductCode, N'') AS ProductCode,
        d.StoreProductCode
    FROM [StoreLocalSupplierInvoice] h
    INNER JOIN [StoreLocalSupplierInvoiceDetails] d
        ON d.InvoiceGUID = h.InvoiceGUID
        AND d.IsDeleted = 0
    WHERE
        h.IsDeleted = 0
        AND NULLIF(h.StoreCode, N'') IS NOT NULL{{storeFilter}}
),
ResolvedPairs AS (
    SELECT
        COALESCE(sp.ProductCode, NULLIF(srp.ProductCode, N'')) AS ProductCode,
        NULLIF(srp.SupplierCode, N'') AS PriceSupplierCode
    FROM StorePairs sp
    LEFT JOIN [StoreRetailPrice] srp
        ON srp.UUID = sp.StoreProductCode
        AND srp.IsDeleted = 0
)
SELECT
    COALESCE(NULLIF(sup.Name, N''), source.SupplierCode) AS Label,
    source.SupplierCode AS Value
FROM (
    SELECT DISTINCT
        COALESCE(NULLIF(p.LocalSupplierCode, N''), rp.PriceSupplierCode) AS SupplierCode
    FROM ResolvedPairs rp
    LEFT JOIN [Product] p
        ON p.ProductCode = rp.ProductCode
        AND p.IsDeleted = 0
    WHERE
        rp.ProductCode IS NOT NULL
        AND COALESCE(NULLIF(p.LocalSupplierCode, N''), rp.PriceSupplierCode) IS NOT NULL
) source
LEFT JOIN [LocalSupplier] sup
    ON sup.LocalSupplierCode = source.SupplierCode
    AND sup.IsDeleted = 0
ORDER BY
    Label,
    Value
""";

            return new LocalSupplierInvoiceSalesAnalysisSqlBuildResult
            {
                Sql = sql,
                Parameters = parameters,
            };
        }

        public static IReadOnlyList<string>? AddStoreParametersForStoreOptions(
            List<SugarParameter> parameters,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            return AddStoreParameters(parameters, scopedStoreCodes);
        }

        public static string BuildStoreFilterForStoreOptions(
            string column,
            IReadOnlyList<string>? storeParameterNames
        )
        {
            return BuildStoreFilter(column, storeParameterNames);
        }

        public static bool ContainsWriteKeyword(string sql)
        {
            var upper = sql.ToUpperInvariant();
            var unsafeWords = new[]
            {
                " INSERT ",
                " UPDATE ",
                " DELETE ",
                " MERGE ",
                " CREATE ",
                " ALTER ",
                " DROP ",
                " TRUNCATE ",
                " EXEC ",
            };
            return unsafeWords.Any(upper.Contains);
        }

        private static IReadOnlyList<string>? ResolveStoreCodes(
            string? requestedStoreCode,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var normalizedScopedStores = scopedStoreCodes?
                .Select(NormalizeText)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var normalizedRequestedStore = NormalizeText(requestedStoreCode);
            if (!string.IsNullOrWhiteSpace(normalizedRequestedStore))
            {
                if (normalizedScopedStores == null)
                {
                    return new[] { normalizedRequestedStore };
                }

                // service/builder 层再做一次门店 scope 交集，避免未来复用时绕过 controller 校验。
                return normalizedScopedStores.Contains(
                    normalizedRequestedStore,
                    StringComparer.OrdinalIgnoreCase
                )
                    ? new[] { normalizedRequestedStore }
                    : Array.Empty<string>();
            }

            if (normalizedScopedStores == null)
            {
                return null;
            }

            return normalizedScopedStores;
        }

        private static IReadOnlyList<string>? AddStoreParameters(
            List<SugarParameter> parameters,
            IReadOnlyList<string>? storeCodes
        )
        {
            if (storeCodes == null)
            {
                return null;
            }

            if (storeCodes.Count == 0)
            {
                return Array.Empty<string>();
            }

            var names = new List<string>(storeCodes.Count);
            for (var index = 0; index < storeCodes.Count; index++)
            {
                var parameterName = "@StoreCode" + index;
                names.Add(parameterName);
                parameters.Add(new SugarParameter(parameterName, storeCodes[index]));
            }

            return names;
        }

        private static string BuildStoreFilter(
            string column,
            IReadOnlyList<string>? storeParameterNames
        )
        {
            if (storeParameterNames == null)
            {
                return string.Empty;
            }

            if (storeParameterNames.Count == 0)
            {
                return "\n        AND 1 = 0";
            }

            return
                "\n        AND "
                + column
                + " IN ("
                + string.Join(", ", storeParameterNames)
                + ")";
        }

        private static string BuildInvoiceDateFilter(LocalSupplierPurchaseSalesAnalysisQueryDto query)
        {
            var builder = new StringBuilder();

            if (query.OrderDateStart.HasValue)
            {
                builder.Append("\n        AND h.OrderDate >= @OrderDateStart");
            }

            if (query.OrderDateEnd.HasValue)
            {
                builder.Append("\n        AND h.OrderDate < @OrderDateEndExclusive");
            }

            return builder.ToString();
        }

        private static string BuildProductFilter(LocalSupplierPurchaseSalesAnalysisQueryDto query)
        {
            var builder = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(query.SupplierCode))
            {
                // 供应商筛选必须优先取商品主供应商，再回退到分店价格表供应商。
                builder.Append(
                    "\n        AND COALESCE(NULLIF(p.LocalSupplierCode, N''), NULLIF(srp.SupplierCode, N'')) = @SupplierCode"
                );
            }

            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                builder.Append(
                    "\n        AND (\n"
                        + "            COALESCE(NULLIF(d.ProductCode, N''), NULLIF(srp.ProductCode, N'')) LIKE @Keyword ESCAPE '\\'\n"
                        + "            OR COALESCE(NULLIF(d.ItemNumber, N''), NULLIF(p.ItemNumber, N'')) LIKE @Keyword ESCAPE '\\'\n"
                        + "            OR COALESCE(NULLIF(d.ProductName, N''), NULLIF(p.ProductName, N'')) LIKE @Keyword ESCAPE '\\'\n"
                        + "            OR COALESCE(NULLIF(d.Barcode, N''), NULLIF(p.Barcode, N'')) LIKE @Keyword ESCAPE '\\'\n"
                        + "        )"
                );
            }

            return builder.ToString();
        }

        private static string BuildSupplierCategoryFilter(IReadOnlyList<string> categoryParameterNames)
        {
            if (categoryParameterNames.Count == 0)
            {
                return string.Empty;
            }

            var productCode = "COALESCE(NULLIF(d.ProductCode, N''), NULLIF(srp.ProductCode, N''))";
            return "\n        AND EXISTS (\n"
                + "            SELECT 1 FROM [Product] categoryProduct\n"
                + "            WHERE categoryProduct.ProductCode = " + productCode + "\n"
                + "              AND categoryProduct.IsDeleted = 0\n"
                + "              AND ((@SupplierCode = N'200' AND (categoryProduct.LocalSupplierCode = N'200' OR categoryProduct.LocalSupplierCode IS NULL OR categoryProduct.LocalSupplierCode = N'') AND EXISTS (SELECT 1 FROM WarehouseCategoryScope wc WHERE wc.CategoryGUID = categoryProduct.WarehouseCategoryGUID))\n"
                + "                OR (@SupplierCode <> N'200' AND EXISTS (SELECT 1 FROM [LocalSupplierCategoryProductAssignment] assignment INNER JOIN LocalCategoryScope lc ON lc.CategoryGUID = assignment.CategoryGUID WHERE assignment.ProductCode = categoryProduct.ProductCode AND assignment.LocalSupplierCode = categoryProduct.LocalSupplierCode AND assignment.LocalSupplierCode = @SupplierCode)))\n"
                + "        )";
        }

        private static string BuildPurchaseSalesOrderBy(
            LocalSupplierPurchaseSalesAnalysisQueryDto query
        )
        {
            var sortBy = NormalizeText(query.SortBy);
            if (
                string.IsNullOrWhiteSpace(sortBy)
                || !PurchaseSalesSortColumns.TryGetValue(sortBy, out var column)
            )
            {
                column = PurchaseSalesSortColumns["totalSalesSinceLatestPurchase"];
            }

            var direction = string.Equals(query.SortOrder, "asc", StringComparison.OrdinalIgnoreCase)
                ? "ASC"
                : "DESC";

            return $"{column} {direction}, StoreCode ASC, ProductCode ASC";
        }

        private static string EscapeLikeValue(string value)
        {
            return value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal)
                .Replace("[", "\\[", StringComparison.Ordinal);
        }

        private static List<SugarParameter> BuildParameters(string invoiceGuid)
        {
            if (string.IsNullOrWhiteSpace(invoiceGuid))
            {
                throw new ArgumentException("进货单 GUID 不能为空。", nameof(invoiceGuid));
            }

            return new List<SugarParameter> { new("@InvoiceGuid", invoiceGuid.Trim()) };
        }

        private static string? NormalizeText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    /// <summary>
    /// 进货销量分析图表的逐日序列与进货事件构造器；纯静态、无数据库依赖，便于单测。
    /// </summary>
    public static class LocalSupplierPurchaseSalesDailySeriesBuilder
    {
        /// <summary>没有上次进货时，窗口从最近进货日往前回看的天数。</summary>
        public const int FallbackLookbackDays = 30;

        /// <summary>
        /// 计算单行的图表窗口：start 为上次进货日，没有则取最近进货日前 30 天；end 为业务日期今天，但不得早于 start。
        /// </summary>
        public static (DateTime Start, DateTime End) ResolveWindow(
            DateTime? previousPurchaseDate,
            DateTime latestPurchaseDate,
            DateTime today
        )
        {
            var start = (
                previousPurchaseDate ?? latestPurchaseDate.AddDays(-FallbackLookbackDays)
            ).Date;
            var end = today.Date;

            // 进货日期被录成未来日期时 end 会早于 start，此时收敛为 start 当天的单点窗口。
            if (end < start)
            {
                end = start;
            }

            return (start, end);
        }

        /// <summary>
        /// 生成 start 到 end（首尾都包含）的逐日列表，缺失日期补 0，负数（退货）原样保留。
        /// 约定：start 晚于 end 时返回空列表，调用方应先用 ResolveWindow 保证窗口有效。
        /// </summary>
        public static List<LocalSupplierPurchaseSalesDailyPointDto> BuildDailySeries(
            DateTime start,
            DateTime end,
            IReadOnlyDictionary<DateTime, int> quantitiesByDate
        )
        {
            var startDate = start.Date;
            var endDate = end.Date;
            var series = new List<LocalSupplierPurchaseSalesDailyPointDto>();
            if (startDate > endDate)
            {
                return series;
            }

            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                series.Add(
                    new LocalSupplierPurchaseSalesDailyPointDto
                    {
                        Date = date,
                        Quantity = quantitiesByDate.TryGetValue(date, out var quantity)
                            ? quantity
                            : 0,
                    }
                );
            }

            return series;
        }

        /// <summary>
        /// 生成窗口内的进货事件：上次进货的日期与数量都有值才加入，随后加入最近进货；数量四舍五入取整（远离零）。
        /// </summary>
        public static List<LocalSupplierPurchaseSalesPurchaseEventDto> BuildPurchaseEvents(
            DateTime? previousPurchaseDate,
            decimal? previousPurchaseQty,
            DateTime? latestPurchaseDate,
            decimal? latestPurchaseQty
        )
        {
            var events = new List<LocalSupplierPurchaseSalesPurchaseEventDto>();
            if (!latestPurchaseDate.HasValue)
            {
                return events;
            }

            if (previousPurchaseDate.HasValue && previousPurchaseQty.HasValue)
            {
                events.Add(
                    new LocalSupplierPurchaseSalesPurchaseEventDto
                    {
                        Date = previousPurchaseDate.Value.Date,
                        Quantity = RoundQuantity(previousPurchaseQty.Value),
                    }
                );
            }

            events.Add(
                new LocalSupplierPurchaseSalesPurchaseEventDto
                {
                    Date = latestPurchaseDate.Value.Date,
                    // 最近进货数量理论上不为空；为空时按 0 处理，保证图表仍能标出进货日。
                    Quantity = RoundQuantity(latestPurchaseQty ?? 0m),
                }
            );

            return events;
        }

        private static int RoundQuantity(decimal quantity)
        {
            return (int)Math.Round(quantity, MidpointRounding.AwayFromZero);
        }
    }

    public sealed class LocalSupplierInvoiceSalesAnalysisSqlBuildResult
    {
        public string Sql { get; set; } = string.Empty;
        public List<SugarParameter> Parameters { get; set; } = new();
    }

    public sealed class LocalSupplierPurchaseSalesAnalysisSqlBuildResult
    {
        public string PagedSql { get; set; } = string.Empty;
        public string SummarySql { get; set; } = string.Empty;
        public List<SugarParameter> Parameters { get; set; } = new();
    }
}
