using BlazorApp.Api.Services.Background;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 进货量 SQL 依赖 OPENJSON、临时表与 EffectivePurchaseDate 持久化列，SQLite 无法执行；
/// 这里锁定参数校验与 SQL 口径，实际执行已在生产库只读核验（见服务注释）。
/// </summary>
public sealed class BatchProductSalesPurchaseQuantityTests
{
    private static BatchProductSalesAnalysisService CreateService() => new(
        new Mock<ISqlSugarClient>(MockBehavior.Strict).Object,
        new Mock<IProductStoreDailyStatisticQueueService>(MockBehavior.Strict).Object,
        NullLogger<BatchProductSalesAnalysisService>.Instance);

    [Fact]
    public async Task 进货日期晚于今天时拒绝且不访问数据库()
    {
        var future = DateTime.Today.AddDays(30);
        await Assert.ThrowsAsync<BatchProductSalesAnalysisValidationException>(() => CreateService().GetPurchaseQuantitiesAsync(
            new BatchProductSalesPurchaseRequestDto { StartDate = future, EndDate = future, ProductCodes = ["P1"] }, null));
    }

    [Fact]
    public async Task 进货日期超过366天时拒绝()
    {
        var end = DateTime.Today.AddDays(-1);
        await Assert.ThrowsAsync<BatchProductSalesAnalysisValidationException>(() => CreateService().GetPurchaseQuantitiesAsync(
            new BatchProductSalesPurchaseRequestDto { StartDate = end.AddDays(-366), EndDate = end, ProductCodes = ["P1"] }, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3001)]
    public async Task 商品数量必须在1到3000之间(int count)
    {
        var day = DateTime.Today.AddDays(-1);
        await Assert.ThrowsAsync<BatchProductSalesAnalysisValidationException>(() => CreateService().GetPurchaseQuantitiesAsync(
            new BatchProductSalesPurchaseRequestDto
            {
                StartDate = day, EndDate = day,
                ProductCodes = Enumerable.Range(1, count).Select(index => $"P{index}").ToList(),
            }, null));
    }

    [Fact]
    public void Sql_总仓配货排除草稿并按出库日期优先取配货数量()
    {
        var sql = BatchProductSalesAnalysisService.PurchaseQuantitySql;
        Assert.Contains("COALESCE(h.[FlowStatus], -1) <> 0", sql, StringComparison.Ordinal);
        Assert.Contains("SUM(COALESCE(d.[AllocQuantity], 0)) AS [WarehouseQuantity]", sql, StringComparison.Ordinal);
        Assert.Contains("COALESCE(h.[OutboundDate], h.[OrderDate], h.[CreatedAt]) >= @StartDate", sql, StringComparison.Ordinal);
        Assert.Contains("COALESCE(h.[OutboundDate], h.[OrderDate], h.[CreatedAt]) < @EndDateExclusive", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_供应商进货按有效进货日期且只在缺编码时回查零售价()
    {
        var sql = BatchProductSalesAnalysisService.PurchaseQuantitySql;
        Assert.Contains("i.[EffectivePurchaseDate] >= CAST(@StartDate AS date)", sql, StringComparison.Ordinal);
        Assert.Contains("i.[EffectivePurchaseDate] < CAST(@EndDateExclusive AS date)", sql, StringComparison.Ordinal);
        Assert.Contains("ON NULLIF(d.[ProductCode], N'') IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("COALESCE(NULLIF(d.[ProductCode], N''), NULLIF(srp.[ProductCode], N''))", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_商品与分店清单走JSON单参数并只返回非零行()
    {
        var sql = BatchProductSalesAnalysisService.PurchaseQuantitySql;
        // 3000 个商品编码不能逐个绑定参数（SQL Server 上限 2100）。
        Assert.Contains("OPENJSON(@ProductCodes)", sql, StringComparison.Ordinal);
        Assert.Contains("OPENJSON(@StoreCodes)", sql, StringComparison.Ordinal);
        Assert.Contains("HAVING SUM(r.[WarehouseQuantity]) <> 0 OR SUM(r.[SupplierQuantity]) <> 0", sql, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE #BatchPurchaseProducts;", sql, StringComparison.Ordinal);
    }
}
