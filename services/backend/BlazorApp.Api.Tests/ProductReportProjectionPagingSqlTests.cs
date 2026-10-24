using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 商品报告「投影版」分页 SQL（月 / 日预聚合投影替代扫日统计）的结构与启用条件。
/// 结果口径在生产库上用旧 / 新两条 SQL 对同一组参数逐行逐列比对过（中国供应商、全部商品、指定供应商、数量排序、
/// 整月、最近 7 天、翻页，共 7 个场景，数值列完全一致）；这里守住 SQL 结构不被改坏，以及什么时候才允许走投影。
/// </summary>
public sealed class ProductReportProjectionPagingSqlTests
{
    // 最终结果集必须与旧 SQL 同名同列，否则 SqlSugar 映射到 ProductReportPagingSqlRow 会丢字段。
    private static readonly string[] OutputColumns =
    {
        "HasData", "TotalCount", "ProductCode", "CurrentProductName", "CompareProductName",
        "CurrentQuantity", "CurrentSalesAmount", "CurrentOrderCount", "CurrentGrossProfit",
        "CurrentStatisticRowCount", "CurrentCostedRowCount", "CurrentGrossProfitRowCount",
        "CompareQuantity", "CompareSalesAmount", "CompareOrderCount", "CompareGrossProfit",
        "CompareStatisticRowCount", "CompareCostedRowCount", "CompareGrossProfitRowCount",
    };

    private static string FinalSelect(string sql)
    {
        var start = sql.LastIndexOf("SELECT page.[HasData]", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到最终结果集");
        var end = sql.IndexOf("FROM #ProductReportPage", start, StringComparison.Ordinal);
        return sql.Substring(start, end - start);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 最终结果集与旧SQL列名一致(bool includeCompare)
    {
        var select = FinalSelect(SalesDashboardReactService.BuildProductReportProjectionPagingSql(includeCompare));

        foreach (var column in OutputColumns)
            Assert.True(
                select.Contains($"[{column}]", StringComparison.Ordinal),
                $"最终结果集缺少列 {column}");
    }

    [Fact]
    public void 事实来自月表_日投影_日统计兜底三段()
    {
        var sql = SalesDashboardReactService.BuildProductReportProjectionPagingSql(true);

        Assert.Contains("[dbo].[SalesDetailQueryMonthlyProduct]", sql);
        Assert.Contains("[dbo].[SalesDetailQueryDailyProduct]", sql);
        Assert.Contains("#sdmMonths", sql);
        Assert.Contains("#sdmDays", sql);
        // 日统计只允许作为「日投影没追上的日期」的兜底，以及本页商品名的逐商品查询；不能再有按整个区间的大扫描。
        Assert.Contains("d.[ProductSource] = 2", sql);
        Assert.DoesNotContain("s.[Date] >= @CurrentStart AND s.[Date] < @CurrentEndExclusive\n                    AND s.[Supplier", sql);
    }

    [Fact]
    public void 没有同期时不引用同期参数()
    {
        var sql = SalesDashboardReactService.BuildProductReportProjectionPagingSql(false);

        // 调用方只在有同期时才提供 @CompareStart / @CompareEndExclusive，引用了会让整条 SQL 因缺参数失败。
        Assert.DoesNotContain("@CompareStart", sql.Replace("@sdrCompareStart", string.Empty));
        Assert.DoesNotContain("@CompareEndExclusive", sql);
        Assert.Contains("@sdrHasCompare int = 0", sql);
    }

    [Fact]
    public void 有同期时展开同期参数并带同期商品名()
    {
        var sql = SalesDashboardReactService.BuildProductReportProjectionPagingSql(true);

        Assert.Contains("@sdrHasCompare int = 1", sql);
        Assert.Contains("@sdrCompareStart datetime = @CompareStart", sql);
        Assert.Contains("compareName", sql);
    }

    [Fact]
    public void 正常结束与异常分支都清理全部临时表()
    {
        var sql = SalesDashboardReactService.BuildProductReportProjectionPagingSql(true);
        var tryEnd = sql.IndexOf("END TRY", StringComparison.Ordinal);
        var normal = sql.Substring(0, tryEnd);
        var catchBlock = sql.Substring(tryEnd);

        foreach (var table in new[]
        {
            "#ProductReportPage", "#ProductReportAggregates", "#sdmDays", "#sdmMonths",
            "#ProductReportChinaMap", "#ProductReportLocalFilter", "#ProductReportChinaFilter",
        })
        {
            Assert.Contains($"DROP TABLE {table};", normal);
            Assert.Contains($"DROP TABLE {table};", catchBlock);
        }
    }

    [Fact]
    public void 投影缺表时抛出专用错误号以便回退()
    {
        var sql = SalesDashboardReactService.BuildProductReportProjectionPagingSql(true);

        Assert.Contains("THROW 51014", sql);
    }

    [Fact]
    public void 排序方式沿用旧SQL的排名片段()
    {
        var byAmount = SalesDashboardReactService.BuildProductReportProjectionPagingSql(true);
        var byQuantity = SalesDashboardReactService.BuildProductReportProjectionPagingSql(
            true, ProductReportSort.Parse("quantity", "asc"));

        Assert.Contains("a.[CurrentSalesAmount] DESC", byAmount);
        Assert.Contains("a.[CurrentQuantity] ASC", byQuantity);
    }

    [Theory]
    [InlineData("2025-10-21", "2026-10-21", "2024-10-22", "2025-10-22", true)]   // 整年 + 同期
    [InlineData("2025-10-21", "2026-10-21", null, null, true)]                   // 整年无同期
    [InlineData("2024-01-01", "2026-01-01", null, null, false)]                  // 超过 731 天
    [InlineData("2025-10-21", "2026-10-21", "2022-01-01", "2024-12-31", false)]  // 同期超过 731 天
    public void 投影只服务不超过731天的区间(string start, string end, string? compareStart, string? compareEnd, bool expected)
    {
        var range = new DateRangeDto
        {
            StartDate = DateTime.Parse(start),
            EndDate = DateTime.Parse(end),
            CompareStartDate = compareStart is null ? null : DateTime.Parse(compareStart),
            CompareEndDate = compareEnd is null ? null : DateTime.Parse(compareEnd),
        };

        Assert.Equal(expected, SalesDashboardReactService.ProductReportProjectionRangeSupported(range));
    }

    [Fact]
    public void 非投影缺表错误不会触发回退()
    {
        Assert.False(SalesDashboardReactService.IsMissingMonthlyProjectionError(new InvalidOperationException("other")));
        Assert.False(SalesDashboardReactService.IsMissingMonthlyProjectionError(
            new Exception("outer", new InvalidOperationException("inner"))));
    }
}
