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
    public async Task 进货日期超过426天时拒绝()
    {
        // 进货区间 = 销售区间（最多 366 天）向前多 60 天，上限 426 天。
        var end = DateTime.Today.AddDays(-1);
        await Assert.ThrowsAsync<BatchProductSalesAnalysisValidationException>(() => CreateService().GetPurchaseQuantitiesAsync(
            new BatchProductSalesPurchaseRequestDto { StartDate = end.AddDays(-BatchProductSalesAnalysisService.MaxPurchaseDays), EndDate = end, ProductCodes = ["P1"] }, null));
        await Assert.ThrowsAsync<BatchProductSalesAnalysisValidationException>(() => CreateService().GetPurchaseDetailsAsync(
            new BatchProductSalesPurchaseDetailRequestDto { StartDate = end.AddDays(-BatchProductSalesAnalysisService.MaxPurchaseDays), EndDate = end, ProductCodes = ["P1"] }, null));
    }

    [Fact]
    public void 进货区间上限比销量多60天_销量仍是366天()
    {
        Assert.Equal(426, BatchProductSalesAnalysisService.MaxPurchaseDays);
        var end = DateTime.Today.AddDays(-2);
        var purchaseMax = new BatchProductSalesScopeDto { StartDate = end.AddDays(-(BatchProductSalesAnalysisService.MaxPurchaseDays - 1)), EndDate = end };
        Assert.Equal(purchaseMax.StartDate.Date, BatchProductSalesAnalysisService.ValidateRange(purchaseMax, BatchProductSalesAnalysisService.MaxPurchaseDays).StartDate);
        // 同样的 426 天给销量（默认上限）仍要拒绝。
        Assert.Throws<BatchProductSalesAnalysisValidationException>(() => BatchProductSalesAnalysisService.ValidateRange(purchaseMax));
    }

    [Fact]
    public async Task 进货明细_未来日期与商品数量越界时拒绝且不访问数据库()
    {
        var future = DateTime.Today.AddDays(30);
        await Assert.ThrowsAsync<BatchProductSalesAnalysisValidationException>(() => CreateService().GetPurchaseDetailsAsync(
            new BatchProductSalesPurchaseDetailRequestDto { StartDate = future, EndDate = future, ProductCodes = ["P1"] }, null));
        var day = DateTime.Today.AddDays(-1);
        await Assert.ThrowsAsync<BatchProductSalesAnalysisValidationException>(() => CreateService().GetPurchaseDetailsAsync(
            new BatchProductSalesPurchaseDetailRequestDto { StartDate = day, EndDate = day, ProductCodes = [] }, null));
    }

    [Fact]
    public void Sql_进货明细与汇总共用同一筛选口径()
    {
        var detail = BatchProductSalesAnalysisService.PurchaseDetailSql;
        // 这些条件决定「算不算进货、算在哪天」，明细与汇总必须逐字一致，逐笔合计才会等于进货量。
        foreach (var fragment in new[]
        {
            "COALESCE(h.[IsDeleted], 0) = 0",
            "COALESCE(h.[FlowStatus], -1) <> 0",
            "COALESCE(h.[OutboundDate], h.[OrderDate], h.[CreatedAt]) >= @StartDate",
            "COALESCE(h.[OutboundDate], h.[OrderDate], h.[CreatedAt]) < @EndDateExclusive",
            "COALESCE(d.[IsDeleted], 0) = 0",
            "i.[EffectivePurchaseDate] >= CAST(@StartDate AS date)",
            "i.[EffectivePurchaseDate] < CAST(@EndDateExclusive AS date)",
            "COALESCE(NULLIF(d.[ProductCode], N''), NULLIF(srp.[ProductCode], N''))",
            "OPENJSON(@ProductCodes)",
            "OPENJSON(@StoreCodes)",
        })
        {
            Assert.Contains(fragment, BatchProductSalesAnalysisService.PurchaseQuantitySql, StringComparison.Ordinal);
            Assert.Contains(fragment, detail, StringComparison.Ordinal);
        }
        Assert.Contains("SUM(COALESCE(d.[AllocQuantity], 0)) AS [Quantity]", detail, StringComparison.Ordinal);
        Assert.Contains("SELECT TOP (@Limit)", detail, StringComparison.Ordinal);
        Assert.Contains("ORDER BY r.[PurchaseDate] DESC", detail, StringComparison.Ordinal);
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
