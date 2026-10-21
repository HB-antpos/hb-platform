using BlazorApp.Api.Services;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 「已支付但没有明细的订单」对账扣除：分店营业额按订单支付金额计入，商品统计无法归属，
/// 差额超过容差会让当天统计永久 Failed，区间包含这一天的商品报告整份打不开（2026-04-09 分店 1003 的真实案例）。
/// </summary>
public sealed class SalesStatisticsHeaderOnlyOrderReconciliationTests
{
    private static readonly DateTime Date = new(2026, 4, 9);

    private static ProductStoreDailySourceRow DetailRow(string order, string branch, decimal amount) => new()
    {
        Date = Date,
        OrderGuid = order,
        DetailGuid = $"D-{order}",
        BranchCode = branch,
        SupplierCode = "SUP",
        ProductCode = $"P-{order}",
        ProductName = "商品",
        Quantity = 1,
        ActualAmount = amount,
    };

    // LEFT JOIN 出来的无明细订单：明细列全为空。
    private static ProductStoreDailySourceRow HeaderOnlyRow(string order, string? branch, string? device = null) => new()
    {
        Date = Date,
        OrderGuid = order,
        BranchCode = branch,
        DeviceCode = device,
    };

    private static ProductStoreDailyRefreshInput Input(
        IReadOnlyList<ProductStoreDailySourceRow> rows,
        Dictionary<string, decimal> payments,
        Dictionary<string, string>? deviceBranchMap = null) => new(
        Date,
        rows,
        new HashSet<ProductStoreDailySourceRow>(),
        payments,
        rows.Where(row => !string.IsNullOrWhiteSpace(row.DetailGuid))
            .GroupBy(row => row.OrderGuid!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.ActualAmount), StringComparer.OrdinalIgnoreCase),
        deviceBranchMap ?? new Dictionary<string, string>(),
        new List<StoreCostRow>(),
        new List<ProductCostRow>(),
        new List<WarehouseCostRow>(),
        null);

    private static IEnumerable<ProductStoreDailyBranchRollup> ProductRollups(ProductStoreDailyRefreshBuildResult build) =>
        build.Statistics.Select(row => new ProductStoreDailyBranchRollup(row.BranchCode, row.TotalAmount, row.TotalQuantity));

    [Fact]
    public void Build_无明细订单按支付金额生成分店负向扣除项()
    {
        var rows = new[] { DetailRow("O1", "1003", 100m), HeaderOnlyRow("O2", "1003") };
        var payments = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["O1"] = 100m, ["O2"] = 118.89m };

        var build = new SalesStatisticsProductStoreDailyBuilder().Build(Input(rows, payments));

        var deduction = Assert.Single(build.HeaderOnlyOrderDeductions);
        Assert.Equal("1003", deduction.BranchCode);
        Assert.Equal(-118.89m, deduction.TotalAmount);
        Assert.Equal(0, deduction.TotalQuantity);
        // 无明细订单不产生商品统计行，普通订单照常按支付金额归属。
        Assert.Equal(100m, build.Statistics.Sum(row => row.TotalAmount));
    }

    [Fact]
    public void 复现0409场景_扣除前对账失败_扣除后通过()
    {
        var rows = new[] { DetailRow("O1", "1003", 2485.80m), HeaderOnlyRow("O2", "1003") };
        var payments = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["O1"] = 2485.80m, ["O2"] = 118.89m };
        var build = new SalesStatisticsProductStoreDailyBuilder().Build(Input(rows, payments));
        var store = new[] { new ProductStoreDailyBranchRollup("1003", 2604.69m, 24) };

        var before = ProductStoreDailyReconciliationCalculator.Calculate(Date, ProductRollups(build), store);
        Assert.Equal(SalesStatisticRefreshStatus.Failed, before.Status);
        Assert.Equal(118.89m, before.AmountDifference);

        var effectiveStore = ProductStoreDailyReconciliationCalculator.ApplyExistingStoreAdjustments(
            store, build.SupplementalReturnAdjustments.Concat(build.HeaderOnlyOrderDeductions));
        var after = ProductStoreDailyReconciliationCalculator.Calculate(Date, ProductRollups(build), effectiveStore);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, after.Status);
        Assert.Null(after.ErrorMessage);
        Assert.Equal(0m, after.AmountDifference);
    }

    [Fact]
    public void 扣除后仍存在的真差额照常失败()
    {
        var rows = new[] { DetailRow("O1", "1003", 100m), HeaderOnlyRow("O2", "1003") };
        var payments = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["O1"] = 100m, ["O2"] = 118.89m };
        var build = new SalesStatisticsProductStoreDailyBuilder().Build(Input(rows, payments));
        // 分店营业额 500：扣掉无明细的 118.89 后仍比商品多 281.11，超过 100 元容差，不能被掩盖。
        var store = new[] { new ProductStoreDailyBranchRollup("1003", 500m, 2) };

        var effectiveStore = ProductStoreDailyReconciliationCalculator.ApplyExistingStoreAdjustments(
            store, build.HeaderOnlyOrderDeductions);
        var result = ProductStoreDailyReconciliationCalculator.Calculate(Date, ProductRollups(build), effectiveStore);

        Assert.Equal(SalesStatisticRefreshStatus.Failed, result.Status);
        Assert.Equal(281.11m, result.AmountDifference);
        Assert.True(ProductStoreDailyReconciliationCalculator.IsReconciliationFailure(result.ErrorMessage));
    }

    [Fact]
    public void 没有无明细订单时扣除项为空且行为不变()
    {
        var rows = new[] { DetailRow("O1", "1003", 100m) };
        var payments = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["O1"] = 100m };

        var build = new SalesStatisticsProductStoreDailyBuilder().Build(Input(rows, payments));

        Assert.Empty(build.HeaderOnlyOrderDeductions);
    }

    [Fact]
    public void 扣除规则_按订单号去重_按分店汇总_缺支付记录与空订单号不扣()
    {
        var payments = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["A1"] = 60m,
            ["A2"] = 40m,
            ["B1"] = 10m,
            ["DEV1"] = 5m,
        };
        var orders = new[]
        {
            new SalesStatisticsProductStoreDailyDomainRules.HeaderOnlyOrder("A1", "1003", null),
            // 同一订单多行（大小写与空白不同）只算一次。
            new SalesStatisticsProductStoreDailyDomainRules.HeaderOnlyOrder(" a1 ", "1003", null),
            new SalesStatisticsProductStoreDailyDomainRules.HeaderOnlyOrder("A2", "1003", null),
            new SalesStatisticsProductStoreDailyDomainRules.HeaderOnlyOrder("B1", "1005", null),
            // 没有分店编码时按设备解析分店。
            new SalesStatisticsProductStoreDailyDomainRules.HeaderOnlyOrder("DEV1", null, "POS_1021_1"),
            // 查不到支付记录、订单号为空：都不扣。
            new SalesStatisticsProductStoreDailyDomainRules.HeaderOnlyOrder("NOPAY", "1003", null),
            new SalesStatisticsProductStoreDailyDomainRules.HeaderOnlyOrder(null, "1003", null),
        };
        var deviceMap = new Dictionary<string, string> { ["POS_1021_1"] = "1021" };

        var result = SalesStatisticsProductStoreDailyDomainRules
            .BuildHeaderOnlyOrderDeductions(orders, deviceMap, payments)
            .ToDictionary(item => item.BranchCode, item => item.TotalAmount);

        Assert.Equal(3, result.Count);
        Assert.Equal(-100m, result["1003"]);
        Assert.Equal(-10m, result["1005"]);
        Assert.Equal(-5m, result["1021"]);
    }

    [Fact]
    public void HBSales来源行不参与无明细订单判定()
    {
        var hbSalesRow = HeaderOnlyRow("H1", "1003");
        hbSalesRow.IsHBSalesSource = true;
        var payments = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["H1"] = 50m };

        var build = new SalesStatisticsProductStoreDailyBuilder().Build(Input(new[] { hbSalesRow }, payments));

        Assert.Empty(build.HeaderOnlyOrderDeductions);
    }
}
