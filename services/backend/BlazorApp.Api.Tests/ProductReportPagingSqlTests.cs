using BlazorApp.Api.Services.React;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ProductReportPagingSqlTests
{
    [Fact]
    public void Sql_UsesJsonParametersAndOneStablePagedUnion()
    {
        var sql = SalesDashboardReactService.BuildProductReportPagingSql(includeCompare: true);

        Assert.Contains("OPENJSON(@Branches)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OPENJSON(@LocalSuppliers)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OPENJSON(@ChinaProductMap)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNION ALL SELECT * FROM CompareSource", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY a.[CurrentSalesAmount] DESC, a.[CompareSalesAmount] DESC, a.[ProductCode] ASC", sql);
        Assert.Contains("Windowed AS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT(*) OVER ()", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROW_NUMBER() OVER", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INTO #ProductReportAggregates", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FROM #ProductReportAggregates AS a", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OPTION (RECOMPILE)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DROP TABLE #ProductReportAggregates", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BEGIN TRY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BEGIN CATCH", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("windowed.[TotalCount] <= @PageOffset", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY page.[RowNumber] ASC", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Counted AS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PageRows AS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INTO #ProductReportPage", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INNER JOIN #ProductReportPage AS requested", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LEFT JOIN Aggregated AS detail", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountOccurrences(sql, "INTO #ProductReportAggregates"));
        Assert.DoesNotContain("IN ('", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sql_WithoutCompareDoesNotReferenceCompareParameters()
    {
        var sql = SalesDashboardReactService.BuildProductReportPagingSql(includeCompare: false);

        Assert.DoesNotContain("@CompareStart", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CompareSource", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UNION ALL SELECT * FROM CompareSource", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChinaSql_FiltersDirectAndLegacyRowsWithinTheSamePeriodScan()
    {
        var sql = SalesDashboardReactService.BuildProductReportPagingSql(includeCompare: true);

        Assert.DoesNotContain("CurrentLegacySource AS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CompareLegacySource AS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("s.[SupplierCode] = N'200'", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FROM #ProductReportAggregates AS a", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SearchLiteralEscapesSqlLikeCharacters()
    {
        Assert.Equal(@"a\%\_b\[c", SalesDashboardReactService.EscapeLikeValue(@"a%_b[c"));
    }

    [Fact]
    public void ProductDetailPageSizeCeilingCoversMobile100_200_500Choices()
    {
        // 移动端商品报告提供 100/200/500 三档；上限低于 500 会让后端夹小 pageSize，前端总页数与实际行数对不上。
        Assert.True(SalesDashboardReactService.EnhancedProductDetailMaxPageSize >= 500);
    }

    [Fact]
    public void MappingKeepsTotalForAnOutOfRangePageAndAppliesGrossProfitCompleteness()
    {
        var rows = new[]
        {
            new SalesDashboardReactService.ProductReportPagingSqlRow
            {
                HasData = false,
                TotalCount = 21,
            },
        };

        var page = SalesDashboardReactService.MapProductReportPagingRows(rows, pageIndex: 2, pageSize: 20);

        Assert.Equal(21, page.Total);
        Assert.Equal(2, page.PageIndex);
        Assert.Empty(page.Data);

        var mapped = SalesDashboardReactService.ToSalesProductDetailWithDiscount(
            new SalesDashboardReactService.ProductReportPagingSqlRow
            {
                HasData = true,
                ProductCode = "P-1",
                CurrentQuantity = 2,
                CurrentSalesAmount = 10m,
                CurrentGrossProfit = 3m,
                CurrentStatisticRowCount = 2,
                CurrentCostedRowCount = 1,
                CurrentGrossProfitRowCount = 1,
                CompareQuantity = 1,
                CompareSalesAmount = 5m,
                CompareGrossProfit = 2m,
                CompareStatisticRowCount = 1,
                CompareCostedRowCount = 1,
                CompareGrossProfitRowCount = 1,
            }
        );

        Assert.Null(mapped.GrossProfit);
        Assert.Equal(2m, mapped.GrossProfitLY);
        Assert.Null(mapped.GrossMarginRate);
        Assert.Equal(.4m, mapped.GrossMarginRateLY);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sql_SortDefaultEqualsAmountDescendingAndRanksByAmountOnly(bool includeCompare)
    {
        var defaultSql = SalesDashboardReactService.BuildProductReportPagingSql(includeCompare);
        var amountDescendingSql = SalesDashboardReactService.BuildProductReportPagingSql(
            includeCompare,
            ProductReportSort.Parse("amount", "desc")
        );

        Assert.Equal(defaultSql, amountDescendingSql);
        Assert.Contains(
            "ORDER BY a.[CurrentSalesAmount] DESC, a.[CompareSalesAmount] DESC, a.[ProductCode] ASC",
            defaultSql,
            StringComparison.Ordinal
        );
        // 默认排序的排名阶段只汇总销售额，不能因为新增排序而多汇总数量列。
        var ranking = GetRankingSegment(defaultSql);
        Assert.DoesNotContain("[CurrentQuantity]", ranking, StringComparison.Ordinal);
        Assert.DoesNotContain("[CompareQuantity]", ranking, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("amount", "asc", "a.[CurrentSalesAmount] ASC, a.[CompareSalesAmount] ASC, a.[ProductCode] ASC", false)]
    [InlineData("quantity", "desc", "a.[CurrentQuantity] DESC, a.[CompareQuantity] DESC, a.[ProductCode] ASC", true)]
    [InlineData("quantity", "asc", "a.[CurrentQuantity] ASC, a.[CompareQuantity] ASC, a.[ProductCode] ASC", true)]
    [InlineData(
        "unitPrice",
        "desc",
        "CASE WHEN a.[CurrentQuantity] > 0 THEN a.[CurrentSalesAmount] / a.[CurrentQuantity] ELSE 0 END DESC, "
            + "CASE WHEN a.[CompareQuantity] > 0 THEN a.[CompareSalesAmount] / a.[CompareQuantity] ELSE 0 END DESC, "
            + "a.[ProductCode] ASC",
        true
    )]
    [InlineData(
        "unitPrice",
        "asc",
        "CASE WHEN a.[CurrentQuantity] > 0 THEN a.[CurrentSalesAmount] / a.[CurrentQuantity] ELSE 0 END ASC, "
            + "CASE WHEN a.[CompareQuantity] > 0 THEN a.[CompareSalesAmount] / a.[CompareQuantity] ELSE 0 END ASC, "
            + "a.[ProductCode] ASC",
        true
    )]
    public void Sql_SortSelectsWhitelistedRankingOrderAndQuantityColumns(
        string sortField,
        string sortOrder,
        string expectedOrderBy,
        bool requiresQuantity
    )
    {
        var sort = ProductReportSort.Parse(sortField, sortOrder);

        Assert.Equal(requiresQuantity, sort.RequiresQuantity);
        Assert.Equal(expectedOrderBy, SalesDashboardReactService.BuildProductReportRankingOrderBy(sort));
        foreach (var includeCompare in new[] { true, false })
        {
            var sql = SalesDashboardReactService.BuildProductReportPagingSql(includeCompare, sort);

            Assert.Equal(1, CountOccurrences(sql, "ORDER BY " + expectedOrderBy));
            // 排名只决定页码，本页明细仍按排名输出。
            Assert.Contains("ORDER BY page.[RowNumber] ASC", sql, StringComparison.Ordinal);
            var ranking = GetRankingSegment(sql);
            Assert.Equal(requiresQuantity, ranking.Contains("[CurrentQuantity]", StringComparison.Ordinal));
            Assert.Equal(requiresQuantity, ranking.Contains("[CompareQuantity]", StringComparison.Ordinal));
            if (requiresQuantity)
            {
                Assert.Contains(
                    "SUM(CASE WHEN [Period] = 0 THEN [TotalQuantity] ELSE 0 END) AS [CurrentQuantity]",
                    ranking,
                    StringComparison.Ordinal
                );
                Assert.Contains(
                    "SUM(CASE WHEN [Period] = 1 THEN [TotalQuantity] ELSE 0 END) AS [CompareQuantity]",
                    ranking,
                    StringComparison.Ordinal
                );
            }

            if (!includeCompare)
            {
                // 无同期时 [CompareQuantity] 仍作为 0 列存在，但不能引用同期参数或同期来源。
                Assert.DoesNotContain("@CompareStart", sql, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("CompareSource", sql, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Theory]
    [InlineData("amount; DROP TABLE x--", "desc", "amount", "desc")]
    [InlineData("itemNumber", "desc; DROP TABLE x--", "amount", "desc")]
    [InlineData("quantity]; DROP TABLE x--", "asc", "amount", "asc")]
    [InlineData("unitPrice", "ASC; DROP TABLE x--", "unitPrice", "desc")]
    public void Sql_SortInputNeverReachesSqlText(
        string sortField,
        string sortOrder,
        string canonicalField,
        string canonicalOrder
    )
    {
        var sql = SalesDashboardReactService.BuildProductReportPagingSql(
            includeCompare: true,
            ProductReportSort.Parse(sortField, sortOrder)
        );

        Assert.DoesNotContain("DROP TABLE x", sql, StringComparison.OrdinalIgnoreCase);
        // 异常输入只能落到白名单排序：生成的 SQL 与规范参数请求逐字相同。
        Assert.Equal(
            SalesDashboardReactService.BuildProductReportPagingSql(
                includeCompare: true,
                ProductReportSort.Parse(canonicalField, canonicalOrder)
            ),
            sql
        );
    }

    [Theory]
    [InlineData(
        true,
        "CASE WHEN [CurrentQuantity] > 0 THEN [CurrentSalesAmount] * 1.0 / [CurrentQuantity] ELSE 0 END ASC, "
            + "CASE WHEN [CompareQuantity] > 0 THEN [CompareSalesAmount] * 1.0 / [CompareQuantity] ELSE 0 END ASC"
    )]
    [InlineData(
        false,
        "CASE WHEN [CurrentQuantity] > 0 THEN [CurrentSalesAmount] * 1.0 / [CurrentQuantity] ELSE 0 END DESC, "
            + "CASE WHEN [CompareQuantity] > 0 THEN [CompareSalesAmount] * 1.0 / [CompareQuantity] ELSE 0 END DESC"
    )]
    public void Sql_FastPathUnitPriceOrderByIsConstantRealDivision(bool ascending, string expected)
    {
        // 快速路径不带商品编码兜底，由调用方追加 ProductCode ASC；乘 1.0 防止 SQLite 整数除法。
        Assert.Equal(expected, SalesDashboardReactService.BuildFastPathUnitPriceOrderBy(ascending));
    }

    [Theory]
    [InlineData(null, null, "Amount", false)]
    [InlineData("", "", "Amount", false)]
    [InlineData("   ", "   ", "Amount", false)]
    [InlineData("amount", "desc", "Amount", false)]
    [InlineData("amount", "asc", "Amount", true)]
    [InlineData(" UnitPrice ", " ASC ", "UnitPrice", true)]
    [InlineData("unitprice", "Ascend", "UnitPrice", true)]
    [InlineData("QUANTITY", "DESC", "Quantity", false)]
    [InlineData("quantity", "ascend", "Quantity", true)]
    [InlineData("Quantity", "descend", "Quantity", false)]
    [InlineData("itemNumber", "asc", "Amount", true)]
    [InlineData("junk", "sideways", "Amount", false)]
    [InlineData("unit price", "desc", "Amount", false)]
    [InlineData("amount; DROP TABLE x--", "asc", "Amount", true)]
    [InlineData("quantity", "asc; DROP TABLE x--", "Quantity", false)]
    public void ProductReportSort_ParseWhitelistsFieldAndDirection(
        string? sortField,
        string? sortOrder,
        string expectedField,
        bool expectedAscending
    )
    {
        var sort = ProductReportSort.Parse(sortField, sortOrder);

        Assert.Equal(Enum.Parse<ProductReportSortField>(expectedField), sort.Field);
        Assert.Equal(expectedAscending, sort.Ascending);
    }

    [Theory]
    [InlineData("amount", "desc", "amount:desc", null, true, false)]
    [InlineData("amount", "asc", "amount:asc", "amount:asc", false, false)]
    [InlineData("quantity", "desc", "quantity:desc", "quantity:desc", false, true)]
    [InlineData("quantity", "asc", "quantity:asc", "quantity:asc", false, true)]
    [InlineData("unitPrice", "desc", "unitPrice:desc", "unitPrice:desc", false, true)]
    [InlineData(" UNITPRICE ", "ASCEND", "unitPrice:asc", "unitPrice:asc", false, true)]
    [InlineData("amount; DROP TABLE x--", "asc; DROP TABLE x--", "amount:desc", null, true, false)]
    public void ProductReportSort_TokenIsNormalizedWhitelistConstant(
        string sortField,
        string sortOrder,
        string expectedToken,
        string? expectedCacheToken,
        bool expectedIsDefault,
        bool expectedRequiresQuantity
    )
    {
        var sort = ProductReportSort.Parse(sortField, sortOrder);

        // Token 会写入日志和缓存键，只能是 6 个归一化常量之一，不能回显原始输入。
        Assert.Equal(expectedToken, sort.Token);
        Assert.Equal(expectedCacheToken, sort.CacheToken);
        Assert.Equal(expectedIsDefault, sort.IsDefault);
        Assert.Equal(expectedRequiresQuantity, sort.RequiresQuantity);
    }

    [Fact]
    public void ProductReportSort_DefaultValueEqualsHistoricalAmountDescending()
    {
        var unset = default(ProductReportSort);

        // 未显式传排序的调用（default 参数）必须等同历史金额降序。
        Assert.Equal(unset, ProductReportSort.Parse(null, null));
        Assert.Equal(ProductReportSortField.Amount, unset.Field);
        Assert.False(unset.Ascending);
        Assert.True(unset.IsDefault);
        Assert.False(unset.RequiresQuantity);
        Assert.Equal("amount", unset.FieldName);
        Assert.Equal("amount:desc", unset.Token);
        Assert.Null(unset.CacheToken);
    }

    [Fact]
    public void ProductReportSort_ValueOfFollowsAverageUnitPriceRule()
    {
        var amount = ProductReportSort.Parse("amount", "desc");
        var quantity = ProductReportSort.Parse("quantity", "desc");
        var unitPrice = ProductReportSort.Parse("unitPrice", "desc");

        Assert.Equal(47m, amount.ValueOf(47m, 6));
        Assert.Equal(6m, quantity.ValueOf(47m, 6));
        Assert.Equal(47m / 6m, unitPrice.ValueOf(47m, 6));
        // 整数金额也必须按实数比较：47/6 > 39/5，整数除法会把两者都截断成 7。
        Assert.True(unitPrice.ValueOf(47m, 6) > unitPrice.ValueOf(39m, 5));
        Assert.Equal(0m, unitPrice.ValueOf(10m, 0));
        Assert.Equal(0m, unitPrice.ValueOf(10m, -2));
    }

    private static string GetRankingSegment(string sql)
    {
        // 排名阶段是写入 #ProductReportAggregates 之前的全部文本（筛选临时表、来源 CTE 与排名汇总列）。
        var end = sql.IndexOf("INTO #ProductReportAggregates", StringComparison.Ordinal);
        Assert.True(end > 0, "分页 SQL 缺少排名汇总临时表。");
        return sql[..end];
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }

        return count;
    }
}
