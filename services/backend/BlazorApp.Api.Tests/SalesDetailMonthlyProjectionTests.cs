using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class SalesDetailMonthlyProjectionTests
{
    [Fact]
    public void 月身份只含日期版本与聚合时间且按日期排序()
    {
        var sql = SalesDetailQueryMonthlyProjection.BuildMonthIdentitySql("@m");
        Assert.Contains("HASHBYTES('SHA2_256'", sql);
        Assert.Contains("r.[SourceProductVersion] [v]", sql);
        Assert.Contains("CONVERT(varchar(27), r.[LastAggregatedAtUtc], 126) [t]", sql);
        Assert.Contains("ORDER BY r.[Date]", sql);
        Assert.DoesNotContain("[Status]", sql);
        Assert.DoesNotContain("[JobId]", sql);
    }

    [Fact]
    public void 日身份按版本与聚合时间空值相等比较且待办日期最近优先()
    {
        var matches = SalesDetailQueryMonthlyProjection.BuildDayIdentityMatchesSql("ds", "r");
        Assert.Equal("ds.[ProjectionSchemaVersion] = 2 AND NOT EXISTS (SELECT ds.[SourceProductVersion], ds.[SourceLastAggregatedAtUtc] EXCEPT SELECT r.[SourceProductVersion], r.[LastAggregatedAtUtc])", matches);
        var stale = SalesDetailQueryMonthlyProjection.BuildStaleDaysSql("POSM");
        Assert.Contains("SELECT TOP (@sdmMaxDays) CONVERT(date, r.[Date]) [Day]", stale);
        Assert.Contains("ds.[MappingVersion] <> @sdmMappingVersion", stale);
        Assert.Contains("OR NOT (" + matches + ")", stale);
        Assert.Contains("ORDER BY r.[Date] DESC", stale);
    }

    [Fact]
    public void 单日重算按聚集主键读当日事实并替换三张日表()
    {
        var sql = SalesDetailQueryMonthlyProjection.BuildRefreshDaySql("POSM");
        Assert.Contains("[POSM].[dbo].[posm_product_supplier_mapping]", sql);
        Assert.Contains("IF @sdmHasState = 0 RETURN;", sql);
        Assert.Contains("WHERE s.[Date] >= @sdmDayStart AND s.[Date] < @sdmDayEnd", sql);
        Assert.Contains("GROUP BY s.[SupplierCode], s.[BranchCode], s.[ProductCode]", sql);
        Assert.DoesNotContain("WITH (INDEX(", sql);
        Assert.DoesNotContain("OPTIMIZE FOR", sql);
        foreach (var table in new[] { "SalesDetailQueryDailyProduct", "SalesDetailQueryDailyBranch", "SalesDetailQueryDailyState" })
        {
            Assert.Contains($"DELETE FROM [dbo].[{table}] WHERE [Date] = @sdmDay;", sql);
            Assert.Contains($"INSERT INTO [dbo].[{table}]", sql);
        }
        Assert.Contains("MIN(r.[ProductCode]), MAX(r.[ProductCode])", sql);
        Assert.Contains("VALUES (@sdmDay, 2, @sdmVersion, @sdmAggregatedAt, @sdmMappingVersion", sql);
    }

    [Fact]
    public void 整月由日表汇总且日表未就绪时抛51015()
    {
        var sql = SalesDetailQueryMonthlyProjection.BuildRefreshMonthSql("POSM");
        Assert.Contains("THROW 51015,", sql);
        Assert.Contains("FROM [dbo].[SalesDetailQueryDailyProduct]\nWHERE [Date] >= @sdmMonthStart AND [Date] < @sdmMonthEnd\nGROUP BY [RawSupplierCode], [ProductCode];", sql);
        Assert.Contains("FROM [dbo].[SalesDetailQueryDailyBranch]", sql);
        Assert.Contains("MIN([MinProductCode]), MAX([MaxProductCode])", sql);
        Assert.DoesNotContain("ProductStoreDailySalesStatistic", sql);
        Assert.DoesNotContain("MappingUse", sql);
        foreach (var table in new[] { "SalesDetailQueryMonthlyProduct", "SalesDetailQueryMonthlyBranch", "SalesDetailQueryMonthlyState" })
        {
            Assert.Contains($"DELETE FROM [dbo].[{table}] WHERE [Month] = @sdmMonthStart;", sql);
            Assert.Contains($"INSERT INTO [dbo].[{table}]", sql);
        }
        Assert.Contains("VALUES (@sdmMonthStart, 2, @sdmDayIdentity, @sdmDayCount, @sdmMappingVersion", sql);
        var stale = SalesDetailQueryMonthlyProjection.BuildStaleMonthsSql("POSM");
        Assert.Contains("st.[DayIdentity] <> ident.[Identity]", stale);
        Assert.Contains("st.[MappingVersion] <> @sdmMappingVersion", stale);
        Assert.Contains("AND NOT EXISTS (SELECT 1 FROM [dbo].[SalesStatisticRefreshState] r", stale);
        Assert.Contains("ORDER BY m.[Month] DESC", stale);
    }

    [Fact]
    public void 月投影路径服务全部无关键词请求含授权分店与选中分店()
    {
        var all = Enum.GetValues<SalesDetailSection>().ToHashSet();
        Assert.True(SalesDashboardReactService.UsesMonthlyProjection(null, null, null, all));
        Assert.True(SalesDashboardReactService.UsesMonthlyProjection("  ", null, "", all));
        Assert.False(SalesDashboardReactService.UsesMonthlyProjection("English", null, null, all));
        // 授权分店与选中分店由分店粒度事实过滤、商品栏按范围读日事实，不再退回全表扫描。
        Assert.True(SalesDashboardReactService.UsesMonthlyProjection(null, new[] { "S1" }, null, all));
        Assert.True(SalesDashboardReactService.UsesMonthlyProjection(null, null, "S1", all));
        Assert.False(SalesDashboardReactService.UsesMonthlyProjection(null, null, null, new HashSet<SalesDetailSection> { SalesDetailSection.Branches }));
        Assert.True(SalesDashboardReactService.UsesMonthlyProjection(null, null, null, new HashSet<SalesDetailSection> { SalesDetailSection.Products }));
    }

    [Fact]
    public void 预聚合查询先校验表存在再按月表日表日事实三级拼装七个结果集()
    {
        var range = new DateRangeDto
        {
            StartDate = new DateTime(2025, 9, 23), EndDate = new DateTime(2026, 9, 22),
            CompareStartDate = new DateTime(2024, 9, 23), CompareEndDate = new DateTime(2025, 9, 22),
        };
        var sql = SalesDashboardReactService.BuildSalesDetailReportSqlMonthly(
            "POSM", range, SalesDetailKind.China, null, null, 2, 20, Enum.GetValues<SalesDetailSection>().ToHashSet());
        Assert.StartsWith("IF OBJECT_ID(N'dbo.SalesDetailQueryMonthlyProduct', N'U') IS NULL", sql);
        Assert.Contains("OBJECT_ID(N'dbo.SalesDetailQueryDailyState', N'U') IS NULL", sql);
        Assert.Contains("THROW 51014,", sql);
        Assert.Contains("INTO #sdmMonths", sql);
        Assert.Contains("st.[ProjectionSchemaVersion]=2", sql);
        Assert.Contains("st.[MappingVersion]=@sdmMappingVersion", sql);
        Assert.Contains("failed.[Status]=N'Failed'", sql);
        Assert.Contains("failed.[Date] >= CONVERT(datetime, m.[Month])", sql);
        Assert.Contains("INTO #sdmDays", sql);
        Assert.Contains("failed.[Date]=CONVERT(datetime, d.[Day])", sql);
        Assert.Contains("WHEN dv.[DayValid]=1 THEN 1 ELSE 2 END AS tinyint) [ProductSource]", sql);
        Assert.Contains("WHEN dv.[DayValid]=1 AND dv.[MappingValid]=1 THEN 1 ELSE 2 END AS tinyint) [BranchSource]", sql);
        Assert.Contains("INNER JOIN [ProductStoreDailySalesStatistic] s ON s.[Date]>=d.[DayStart] AND s.[Date]<d.[DayEnd]", sql);
        Assert.Contains("WHERE d.[ProductSource]=2 OR d.[BranchSource]=2", sql);
        Assert.DoesNotContain("WITH (INDEX(", sql);
        Assert.DoesNotContain("OPTIMIZE FOR", sql);
        Assert.Contains("INNER JOIN #sdmMonths mo ON mo.[Month]=mp.[Month] AND mo.[ProductValid]=1", sql);
        Assert.Contains("INNER JOIN #sdmDays d ON d.[Day]=dp.[Date] AND d.[ProductSource]=1", sql);
        Assert.Contains("FROM #sdmBaseFacts WHERE [IsProductEdge]=1", sql);
        Assert.Contains("INNER JOIN #sdmMonths mo ON mo.[Month]=b.[Month] AND mo.[BranchValid]=1", sql);
        Assert.Contains("INNER JOIN #sdmDays d ON d.[Day]=db.[Date] AND d.[BranchSource]=1", sql);
        Assert.Contains("WHERE e.[IsBranchEdge]=1", sql);
        // 没有选中商品时汇总、供应商与分母从分店粒度事实求和，商品数用商品码范围判断。
        Assert.Contains("SELECT 'summary' [Code], '当前筛选汇总' [Name]", sql);
        Assert.Contains("MIN(CASE WHEN [Period]=0 THEN [MinProductCode] END) IS NULL THEN 0 WHEN MIN(CASE WHEN [Period]=0 THEN [MinProductCode] END) = MAX(CASE WHEN [Period]=0 THEN [MaxProductCode] END) THEN 1 ELSE 2 END [CurrentProductCount], CASE WHEN MIN(CASE WHEN [Period]=1 THEN [MinProductCode] END) IS NULL THEN 0 WHEN MIN(CASE WHEN [Period]=1 THEN [MinProductCode] END) = MAX(CASE WHEN [Period]=1 THEN [MaxProductCode] END) THEN 1 ELSE 2 END [CompareProductCount]\nFROM #sdmBranchFacts f WHERE [SupplierCode] IS NOT NULL\n", sql);
        Assert.Contains("FROM #sdmBranchFacts f WHERE [SupplierCode] IS NOT NULL\nGROUP BY f.[SupplierCode]", sql);
        Assert.Contains("FROM #sdmBranchFacts f WHERE [AustralianSupplierCode] IS NOT NULL;", sql);
        Assert.Contains("FROM #sdmProductFacts WHERE [SupplierCode] IS NOT NULL\nGROUP BY [ProductCode]", sql);
        Assert.Contains("OFFSET 20 ROWS FETCH NEXT 20 ROWS ONLY", sql);
        Assert.Contains("SELECT [StatisticType],[Date],[Status],[LastAggregatedAtUtc],[CompletedAtUtc],[SourceProductVersion] FROM [SalesStatisticRefreshState]", sql);
        Assert.EndsWith("DROP TABLE #sdmBranchFacts;", sql);

        // 选中商品时分店栏改按商品索引直接读日事实，汇总与供应商按商品过滤所以回到商品粒度事实。
        var selected = SalesDashboardReactService.BuildSalesDetailReportSqlMonthly(
            "POSM", range, SalesDetailKind.Australia, "200", "P-ONE", 1, 20, Enum.GetValues<SalesDetailSection>().ToHashSet());
        Assert.Contains("AND s.[ProductCode]=@sdrSelectedProduct", selected);
        Assert.DoesNotContain("mo.[BranchValid]=1", selected);
        Assert.Contains("FROM #sdmProductFacts f WHERE [SupplierCode] IS NOT NULL AND [SupplierCode] = @sdrSelectedSupplier AND [ProductCode] = @sdrSelectedProduct\n", selected);
        Assert.Contains("FROM #sdmProductFacts f WHERE [SupplierCode] IS NOT NULL AND [ProductCode] = @sdrSelectedProduct\nGROUP BY f.[SupplierCode]", selected);
        Assert.Contains("FROM #sdmProductFacts f WHERE [AustralianSupplierCode] IS NOT NULL;", selected);
        Assert.Contains("FROM #sdmProductFacts WHERE [SupplierCode] IS NOT NULL AND [SupplierCode] = @sdrSelectedSupplier\nGROUP BY [ProductCode]", selected);
        // 不带同期时对比列为常量。
        var single = SalesDashboardReactService.BuildSalesDetailReportSqlMonthly(
            "POSM", new DateRangeDto { StartDate = range.StartDate, EndDate = range.EndDate }, SalesDetailKind.Australia, null, null, 1, 20,
            new HashSet<SalesDetailSection> { SalesDetailSection.Products });
        Assert.Contains("0 [CompareRevenue]", single);
        Assert.Contains("SELECT 0,0,0,0;", single);
        Assert.Contains("SELECT TOP 0 CAST(NULL AS nvarchar(50)) [Code]", single);
    }
}
