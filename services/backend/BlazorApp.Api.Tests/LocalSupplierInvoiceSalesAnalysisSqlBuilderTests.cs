using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using Xunit;

namespace BlazorApp.Api.Tests;

public class LocalSupplierInvoiceSalesAnalysisSqlBuilderTests
{
    // 测试 SQL 片段时统一换行，避免 Windows/Unix 换行差异影响断言。
    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    [Fact]
    public void BuildHeader_ShouldJoinRealLocalSupplierTable()
    {
        var sql = LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildHeader("invoice-guid-001");

        Assert.Contains("[LocalSupplier] sup", sql.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[HBLocalSupplier]", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("AND h.IsDeleted = 0", sql.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COALESCE(h.IsDeleted", sql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(LocalSupplierInvoiceSalesAnalysisSqlBuilder.ContainsWriteKeyword(sql.Sql));
    }

    [Fact]
    public void Build_ShouldScopeByInvoiceAndCompareSalesWindows()
    {
        var sql = LocalSupplierInvoiceSalesAnalysisSqlBuilder.Build("invoice-guid-001");

        Assert.Contains(
            sql.Parameters,
            parameter =>
                parameter.ParameterName == "@InvoiceGuid"
                && (string)parameter.Value == "invoice-guid-001"
        );
        Assert.Contains("[StoreLocalSupplierInvoiceDetails]", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("[ProductStoreDailySalesStatistic]", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("CurrentProducts AS", sql.Sql, StringComparison.OrdinalIgnoreCase);
        // 删除标记必须能命中 WHERE IsDeleted = 0 过滤索引；本次单据与历史单据的明细都不能用 COALESCE 包一层。
        Assert.Contains("AND d.IsDeleted = 0", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("AND pd.IsDeleted = 0", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("AND pi.IsDeleted = 0", sql.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COALESCE(d.IsDeleted", sql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COALESCE(pd.IsDeleted", sql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "CAST(COALESCE(h.InboundDate, h.OrderDate, h.CreatedAt) AS date) AS AnalysisDate",
            sql.Sql,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains(
            "MAX(COALESCE(pi.InboundDate, pi.OrderDate)) AS PreviousPurchaseDate",
            sql.Sql,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains("pi.InvoiceGUID <> @InvoiceGuid", sql.Sql, StringComparison.OrdinalIgnoreCase);
        // 历史明细按商品编码走过滤索引：列上不能包 NULLIF，且要显式声明非空串以匹配索引过滤条件。
        Assert.Contains("ON pd.ProductCode = cp.ProductCode", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("AND pd.ProductCode <> N''", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("ON pi.InvoiceGUID = pd.InvoiceGUID", sql.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("NULLIF(pd.ProductCode", sql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "NULLIF(p.ProductImage, N'') AS ProductImage",
            sql.Sql,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains("cd.ProductImage AS ProductImage", sql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NULLIF(d.ProductImage", sql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FROM CurrentProducts cp", sql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ON pp.StoreCode = cp.StoreCode", sql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ON pp.StoreCode = cd.StoreCode", sql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "LEFT JOIN [StoreRetailPrice] psrp",
            sql.Sql,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain("NULLIF(psrp.ProductCode", sql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "s.Date >= DATEADD(day, 1, cp.AnalysisDate) AND s.Date < DATEADD(day, 31, cp.AnalysisDate)",
            sql.Sql,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains(
            "s.Date >= DATEADD(day, 1, cp.AnalysisDate) AND s.Date < DATEADD(day, 61, cp.AnalysisDate)",
            sql.Sql,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains(
            "s.Date >= DATEADD(day, 1, cp.AnalysisDate) AND s.Date < DATEADD(day, 91, cp.AnalysisDate)",
            sql.Sql,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains(
            "s.Date < DATEADD(day, 1, cp.AnalysisDate) THEN COALESCE(s.TotalQuantity, 0)",
            sql.Sql,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains("SalesQty30", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("SalesQty60", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("SalesQty90", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("SalesSincePreviousPurchase30", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("SalesSincePreviousPurchase60", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("SalesSincePreviousPurchase90", sql.Sql, StringComparison.Ordinal);
        Assert.False(LocalSupplierInvoiceSalesAnalysisSqlBuilder.ContainsWriteKeyword(sql.Sql));
    }

    [Fact]
    public void BuildPurchaseSalesAnalysis_ShouldUseOrderDateParametersAndSupplierPriority()
    {
        var sql = LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildPurchaseSalesAnalysis(
            new LocalSupplierPurchaseSalesAnalysisQueryDto
            {
                StoreCode = "1001",
                SupplierCode = "200",
                OrderDateStart = new DateTime(2026, 6, 1),
                OrderDateEnd = new DateTime(2026, 6, 30),
                Keyword = "ABC%'_",
                Page = 2,
                PageSize = 200,
                SortBy = "salesQty60",
                SortOrder = "asc",
            },
            null
        );

        Assert.Contains("h.OrderDate >= @OrderDateStart", sql.PagedSql, StringComparison.Ordinal);
        Assert.Contains("h.OrderDate < @OrderDateEndExclusive", sql.PagedSql, StringComparison.Ordinal);
        // 删除标记必须是可命中 WHERE IsDeleted = 0 过滤索引的写法，COALESCE 会让明细表退化为全表扫描。
        Assert.Contains("h.IsDeleted = 0", sql.PagedSql, StringComparison.Ordinal);
        Assert.Contains("AND d.IsDeleted = 0", sql.PagedSql, StringComparison.Ordinal);
        Assert.Contains("AND srp.IsDeleted = 0", sql.PagedSql, StringComparison.Ordinal);
        Assert.Contains("AND p.IsDeleted = 0", sql.PagedSql, StringComparison.Ordinal);
        Assert.DoesNotContain("COALESCE(h.IsDeleted", sql.PagedSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COALESCE(d.IsDeleted", sql.PagedSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COALESCE(h.IsDeleted", sql.SummarySql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COALESCE(d.IsDeleted", sql.SummarySql, StringComparison.OrdinalIgnoreCase);
        // 供应商取值仍是「商品档案优先、分店零售价兜底」；零售价来源列在 DetailResolved 中别名为 RetailSupplierCode。
        Assert.Contains(
            "COALESCE(NULLIF(p.LocalSupplierCode, N''), NULLIF(dr.RetailSupplierCode, N'')) = @SupplierCode",
            sql.PagedSql,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "NULLIF(COALESCE(NULLIF(p.LocalSupplierCode, N''), NULLIF(dr.RetailSupplierCode, N'')), N'') IS NOT NULL",
            sql.PagedSql,
            StringComparison.Ordinal
        );
        Assert.Contains("srp.SupplierCode AS RetailSupplierCode", sql.PagedSql, StringComparison.Ordinal);
        // Product 必须用单列等值条件 JOIN，才能走 ProductCode 索引而不是整表扫描。
        Assert.Contains("ON p.ProductCode = dr.ProductCode", sql.PagedSql, StringComparison.Ordinal);
        Assert.Contains(
            "NULLIF(p.ProductImage, N'') AS ProductImage",
            sql.PagedSql,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("NULLIF(d.ProductImage", sql.PagedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sql.Parameters, p => p.ParameterName == "@OrderDateStart");
        Assert.Contains(sql.Parameters, p => p.ParameterName == "@OrderDateEndExclusive");
        Assert.Contains(
            sql.Parameters,
            p => p.ParameterName == "@StoreCode0" && (string)p.Value == "1001"
        );
        Assert.Contains(
            sql.Parameters,
            p => p.ParameterName == "@SupplierCode" && (string)p.Value == "200"
        );
        Assert.Contains(
            sql.Parameters,
            p =>
                p.ParameterName == "@Keyword"
                && ((string)p.Value).Contains("\\%", StringComparison.Ordinal)
                && ((string)p.Value).Contains("\\_", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void BuildPurchaseSalesAnalysis_分类多选生成完整CTE并按当前供应商归属过滤()
    {
        var sql = LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildPurchaseSalesAnalysis(
            new LocalSupplierPurchaseSalesAnalysisQueryDto
            {
                StoreCode = "1001",
                SupplierCode = "240",
                SupplierCategoryGuids = new List<string> { "CAT-1", "CAT-2" },
            },
            null);

        Assert.Matches(@"WarehouseCategoryScope AS \([\s\S]*?\)\s*,\s*DetailResolved AS", sql.PagedSql);
        Assert.Contains("assignment.LocalSupplierCode = categoryProduct.LocalSupplierCode", sql.PagedSql);
        Assert.Contains("assignment.LocalSupplierCode = @SupplierCode", sql.PagedSql);
        Assert.Contains(sql.Parameters, parameter => parameter.ParameterName == "@SupplierCategoryGuid0" && (string)parameter.Value == "CAT-1");
        Assert.Contains(sql.Parameters, parameter => parameter.ParameterName == "@SupplierCategoryGuid1" && (string)parameter.Value == "CAT-2");
        Assert.Contains("WarehouseCategoryScope AS", sql.SummarySql);
    }

    [Fact]
    public void BuildPurchaseSalesAnalysis_ShouldAggregatePurchasesByDateAndPickLatestTwoRows()
    {
        var sql = LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildPurchaseSalesAnalysis(
            new LocalSupplierPurchaseSalesAnalysisQueryDto
            {
                StoreCode = "1001",
                SupplierCode = "200",
                Page = 1,
                PageSize = 100,
                SortBy = "latestPurchaseDate",
            },
            new[] { "1001", "1002" }
        );
        var pagedSql = NormalizeLineEndings(sql.PagedSql);

        Assert.Contains("SUM(COALESCE(dr.Quantity, 0)) AS PurchaseQty", sql.PagedSql, StringComparison.Ordinal);
        Assert.Contains("dr.PurchaseDate AS PurchaseDate", sql.PagedSql, StringComparison.Ordinal);
        Assert.Contains(
            "ROW_NUMBER() OVER (\n            PARTITION BY pda.StoreCode, pda.ProductCode\n            ORDER BY pda.PurchaseDate DESC\n        ) AS PurchaseRank",
            pagedSql,
            StringComparison.Ordinal
        );
        Assert.Contains("WHERE rp.PurchaseRank = 1", sql.PagedSql, StringComparison.Ordinal);
        Assert.Contains("LEAD(pda.PurchaseDate) OVER (", sql.PagedSql, StringComparison.Ordinal);
        Assert.Contains("LEAD(pda.PurchaseQty) OVER (", sql.PagedSql, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviousPurchases AS (", sql.PagedSql, StringComparison.Ordinal);
        Assert.DoesNotContain("SalesMetrics AS (", sql.PagedSql, StringComparison.Ordinal);
        Assert.Contains(
            "AND h.StoreCode IN (@StoreCode0)",
            sql.PagedSql,
            StringComparison.Ordinal
        );
        // 分页 SQL 自带总数与统计更新时间，服务层只在当前页为空时才回退到汇总 SQL。
        Assert.Contains("COUNT(1) OVER () AS TotalCount", sql.PagedSql, StringComparison.Ordinal);
        Assert.Contains(
            "MAX(SalesStatisticLastUpdate) OVER () AS OverallSalesStatisticLastUpdate",
            sql.PagedSql,
            StringComparison.Ordinal
        );
        Assert.Contains("COUNT(1) AS TotalCount", sql.SummarySql, StringComparison.Ordinal);
        Assert.Contains(
            "MAX(SalesStatisticLastUpdate) AS SalesStatisticLastUpdate",
            sql.SummarySql,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void BuildPurchaseSalesAnalysis_ShouldUseSameDaySalesWindowAndHalfOpenPreviousRange()
    {
        var sql = LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildPurchaseSalesAnalysis(
            new LocalSupplierPurchaseSalesAnalysisQueryDto
            {
                StoreCode = "1001",
                SupplierCode = "200",
                Page = 1,
                PageSize = 50,
            },
            null
        );

        Assert.Contains(
            "s.Date >= lp.LatestPurchaseDate AND s.Date < DATEADD(day, 30, lp.LatestPurchaseDate)",
            sql.PagedSql,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "s.Date >= lp.LatestPurchaseDate AND s.Date < DATEADD(day, 60, lp.LatestPurchaseDate)",
            sql.PagedSql,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "s.Date >= lp.LatestPurchaseDate AND s.Date < DATEADD(day, 90, lp.LatestPurchaseDate)",
            sql.PagedSql,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "s.Date >= lp.PreviousPurchaseDate AND s.Date < lp.LatestPurchaseDate",
            sql.PagedSql,
            StringComparison.Ordinal
        );
        Assert.Contains("s.Date >= COALESCE(lp.PreviousPurchaseDate, lp.LatestPurchaseDate)", sql.PagedSql);
        Assert.Contains("SUM(daily.SalesQty30)", sql.PagedSql);
        Assert.False(LocalSupplierInvoiceSalesAnalysisSqlBuilder.ContainsWriteKeyword(sql.PagedSql));
    }

    [Fact]
    public void BuildPurchaseSalesAnalysis_ShouldAggregateTotalSalesSinceLatestPurchaseAndAllowSortingByIt()
    {
        var referenceToday = new DateTime(2026, 6, 25);
        var result = LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildPurchaseSalesAnalysis(
            new LocalSupplierPurchaseSalesAnalysisQueryDto
            {
                StoreCode = "1001",
                SupplierCode = "SUP01",
                SortBy = "totalSalesSinceLatestPurchase",
                SortOrder = "desc",
            },
            null,
            referenceToday
        );

        // 总销量以最近进货当天为起点、不设自身上界；窗口末端由 90 天与"今天"取较晚者，避免久未进货的商品被截断。
        Assert.Contains(
            "CASE WHEN s.Date >= lp.LatestPurchaseDate THEN COALESCE(s.TotalQuantity, 0) ELSE 0 END AS TotalSalesSinceLatestPurchase",
            result.PagedSql
        );
        Assert.Contains("SUM(daily.TotalSalesSinceLatestPurchase) AS TotalSalesSinceLatestPurchase", result.PagedSql);
        Assert.Contains("@SalesWindowEndExclusive", result.PagedSql);
        Assert.Contains("ORDER BY\n    TotalSalesSinceLatestPurchase DESC", result.PagedSql);

        // 半开区间：窗口末端取"今天的次日零点"，保证包含今天当天的销量。
        var windowEnd = Assert.Single(result.Parameters, parameter => parameter.ParameterName == "@SalesWindowEndExclusive");
        Assert.Equal(new DateTime(2026, 6, 26), windowEnd.Value);

        // 30/60/90 天窗口口径不受影响。
        Assert.Contains("DATEADD(day, 30, lp.LatestPurchaseDate)", result.PagedSql);
        Assert.Contains("DATEADD(day, 90, lp.LatestPurchaseDate)", result.PagedSql);
    }

    [Fact]
    public void NormalizePurchaseSalesAnalysisQuery_ShouldKeepTotalSalesSortAndRejectRetiredIntervalDaysAlias()
    {
        var total = LocalSupplierInvoiceSalesAnalysisSqlBuilder.NormalizePurchaseSalesAnalysisQuery(
            new LocalSupplierPurchaseSalesAnalysisQueryDto { SortBy = "totalSalesSinceLatestPurchase", SortOrder = "desc" }
        );
        Assert.Equal("totalSalesSinceLatestPurchase", total.SortBy);
        Assert.Equal("desc", total.SortOrder);

        // 间隔天数列已从页面下线，但后端仍兼容旧请求，不应回落为默认排序。
        var legacy = LocalSupplierInvoiceSalesAnalysisSqlBuilder.NormalizePurchaseSalesAnalysisQuery(
            new LocalSupplierPurchaseSalesAnalysisQueryDto { SortBy = "purchaseIntervalDays", SortOrder = "asc" }
        );
        Assert.Equal("purchaseIntervalDays", legacy.SortBy);
    }

    [Fact]
    public void NormalizePurchaseSalesAnalysisQuery_ShouldWhitelistSortFieldAndRestrictPageSize()
    {
        var normalized =
            LocalSupplierInvoiceSalesAnalysisSqlBuilder.NormalizePurchaseSalesAnalysisQuery(
                new LocalSupplierPurchaseSalesAnalysisQueryDto
                {
                    Page = 0,
                    PageSize = 70,
                    SortBy = "drop table",
                    SortOrder = "weird",
                }
            );

        Assert.Equal(1, normalized.Page);
        Assert.Equal(100, normalized.PageSize);
        // 默认排序已改为总销量降序。
        Assert.Equal("totalSalesSinceLatestPurchase", normalized.SortBy);
        Assert.Equal("desc", normalized.SortOrder);
        Assert.Equal(DateTime.Today.AddDays(-180), normalized.OrderDateStart);
        Assert.Equal(DateTime.Today, normalized.OrderDateEnd);
    }

    [Fact]
    public void NormalizePurchaseSalesAnalysisQuery_ShouldDefaultOrderDateRangeFromReferenceDate()
    {
        var normalized =
            LocalSupplierInvoiceSalesAnalysisSqlBuilder.NormalizePurchaseSalesAnalysisQuery(
                new LocalSupplierPurchaseSalesAnalysisQueryDto(),
                new DateTime(2026, 6, 25)
            );

        Assert.Equal(new DateTime(2025, 12, 27), normalized.OrderDateStart);
        Assert.Equal(new DateTime(2026, 6, 25), normalized.OrderDateEnd);
    }

    [Fact]
    public void ValidatePurchaseSalesAnalysisQuery_ShouldRejectOverMaxDateRange()
    {
        var normalized =
            LocalSupplierInvoiceSalesAnalysisSqlBuilder.NormalizePurchaseSalesAnalysisQuery(
                new LocalSupplierPurchaseSalesAnalysisQueryDto
                {
                    StoreCode = "1001",
                    SupplierCode = "200",
                    OrderDateStart = new DateTime(2025, 1, 1),
                    OrderDateEnd = new DateTime(2026, 6, 25),
                }
            );

        var validation =
            LocalSupplierInvoiceSalesAnalysisSqlBuilder.ValidatePurchaseSalesAnalysisQuery(
                normalized
            );

        Assert.False(validation.IsValid);
        Assert.Throws<ArgumentException>(() =>
            LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildPurchaseSalesAnalysis(
                normalized,
                null
            )
        );
    }

    [Fact]
    public void ValidatePurchaseSalesAnalysisQuery_ShouldRejectMissingStore()
    {
        var normalized =
            LocalSupplierInvoiceSalesAnalysisSqlBuilder.NormalizePurchaseSalesAnalysisQuery(
                new LocalSupplierPurchaseSalesAnalysisQueryDto
                {
                    SupplierCode = "200",
                    Page = 1,
                    PageSize = 100,
                }
            );

        var validation =
            LocalSupplierInvoiceSalesAnalysisSqlBuilder.ValidatePurchaseSalesAnalysisQuery(
                normalized
            );

        Assert.False(validation.IsValid);
        Assert.Equal("分店不能为空。", validation.Message);
    }

    [Fact]
    public void ValidatePurchaseSalesAnalysisQuery_ShouldRejectMissingSupplier()
    {
        var normalized =
            LocalSupplierInvoiceSalesAnalysisSqlBuilder.NormalizePurchaseSalesAnalysisQuery(
                new LocalSupplierPurchaseSalesAnalysisQueryDto
                {
                    StoreCode = "1001",
                    Page = 1,
                    PageSize = 100,
                }
            );

        var validation =
            LocalSupplierInvoiceSalesAnalysisSqlBuilder.ValidatePurchaseSalesAnalysisQuery(
                normalized
            );

        Assert.False(validation.IsValid);
        Assert.Equal("供应商不能为空。", validation.Message);
    }

    [Fact]
    public void ValidatePurchaseSalesAnalysisQuery_ShouldRejectMoreThan100SupplierCategories()
    {
        var normalized =
            LocalSupplierInvoiceSalesAnalysisSqlBuilder.NormalizePurchaseSalesAnalysisQuery(
                new LocalSupplierPurchaseSalesAnalysisQueryDto
                {
                    StoreCode = "1001",
                    SupplierCode = "240",
                    SupplierCategoryGuids = Enumerable.Range(1, 101)
                        .Select(index => $"CAT-{index}")
                        .ToList(),
                }
            );

        Assert.Equal(101, normalized.SupplierCategoryGuids?.Count);

        var validation =
            LocalSupplierInvoiceSalesAnalysisSqlBuilder.ValidatePurchaseSalesAnalysisQuery(
                normalized
            );

        Assert.False(validation.IsValid);
        Assert.Equal("供应商分类最多选择 100 个。", validation.Message);
        Assert.Throws<ArgumentException>(() =>
            LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildPurchaseSalesAnalysis(
                normalized,
                null
            )
        );
    }

    [Fact]
    public void BuildPurchaseSalesAnalysis_ShouldIntersectRequestedStoreWithScope()
    {
        var sql = LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildPurchaseSalesAnalysis(
            new LocalSupplierPurchaseSalesAnalysisQueryDto
            {
                StoreCode = "9999",
                SupplierCode = "200",
                Page = 1,
                PageSize = 100,
            },
            new[] { "1001" }
        );

        Assert.Contains("AND 1 = 0", sql.PagedSql, StringComparison.Ordinal);
        Assert.DoesNotContain("@StoreCode0", sql.PagedSql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPurchaseSalesAnalysis_ShouldKeepWhitelistedSortAndContainsWriteKeywordFalse()
    {
        var sql = LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildPurchaseSalesAnalysis(
            new LocalSupplierPurchaseSalesAnalysisQueryDto
            {
                StoreCode = "1001",
                SupplierCode = "200",
                Page = 1,
                PageSize = 50,
                SortBy = "salesBetweenPurchases",
                SortOrder = "asc",
            },
            null
        );
        var pagedSql = NormalizeLineEndings(sql.PagedSql);

        Assert.Contains(
            "ORDER BY\n    SalesBetweenPurchases ASC, StoreCode ASC, ProductCode ASC",
            pagedSql,
            StringComparison.Ordinal
        );
        Assert.False(LocalSupplierInvoiceSalesAnalysisSqlBuilder.ContainsWriteKeyword(sql.PagedSql));
    }

    [Fact]
    public void BuildPurchaseSalesAnalysisStoreOptions_ShouldUseInvoiceDataAndScopeStores()
    {
        var sql = LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildPurchaseSalesAnalysisStoreOptions(
            new[] { "1001", "1002" }
        );

        Assert.Contains("FROM [StoreLocalSupplierInvoice] h", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN [Store] st", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("AND h.StoreCode IN (@StoreCode0, @StoreCode1)", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("h.IsDeleted = 0", sql.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COALESCE(h.IsDeleted", sql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COALESCE(s.IsActive", sql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sql.Parameters, p => p.ParameterName == "@StoreCode0" && (string)p.Value == "1001");
        Assert.False(LocalSupplierInvoiceSalesAnalysisSqlBuilder.ContainsWriteKeyword(sql.Sql));
    }

    [Fact]
    public void BuildPurchaseSalesAnalysisSupplierOptions_ShouldMatchAnalysisSupplierFallback()
    {
        var sql = LocalSupplierInvoiceSalesAnalysisSqlBuilder.BuildPurchaseSalesAnalysisSupplierOptions(
            "1001",
            null
        );

        Assert.Contains("FROM [StoreLocalSupplierInvoice] h", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("[StoreRetailPrice] srp", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("[LocalSupplier] sup", sql.Sql, StringComparison.Ordinal);
        // 先按 (商品编码, 分店价格 UUID) 去重再回填，供应商仍是商品主供应商优先、价格表供应商兜底。
        Assert.Contains("WITH StorePairs AS (", sql.Sql, StringComparison.Ordinal);
        Assert.Contains("SELECT DISTINCT\n        NULLIF(d.ProductCode, N'') AS ProductCode", NormalizeLineEndings(sql.Sql), StringComparison.Ordinal);
        Assert.Contains("NULLIF(srp.SupplierCode, N'') AS PriceSupplierCode", sql.Sql, StringComparison.Ordinal);
        Assert.Contains(
            "COALESCE(NULLIF(p.LocalSupplierCode, N''), rp.PriceSupplierCode) AS SupplierCode",
            sql.Sql,
            StringComparison.Ordinal
        );
        Assert.Contains("AND d.IsDeleted = 0", sql.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COALESCE(d.IsDeleted", sql.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AND h.StoreCode IN (@StoreCode0)", sql.Sql, StringComparison.Ordinal);
        Assert.Contains(sql.Parameters, p => p.ParameterName == "@StoreCode0" && (string)p.Value == "1001");
        Assert.False(LocalSupplierInvoiceSalesAnalysisSqlBuilder.ContainsWriteKeyword(sql.Sql));
    }

    [Fact]
    public void Build_ShouldRejectBlankInvoiceGuid()
    {
        Assert.Throws<ArgumentException>(() =>
            LocalSupplierInvoiceSalesAnalysisSqlBuilder.Build(" ")
        );
    }
}
