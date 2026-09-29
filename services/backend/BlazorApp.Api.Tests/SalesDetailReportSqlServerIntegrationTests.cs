using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Background;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class SalesDetailReportSqlServerFactAttribute : FactAttribute
{
    private const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public SalesDetailReportSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过真实 SQL Server 销售明细验证。";
    }
}

[Trait("Category", "SQL")]
public sealed class SalesDetailReportSqlServerIntegrationTests
{
    private static readonly DateTime SeedDate = new(2026, 9, 9);
    private static readonly DateTime CompareDate = new(2026, 9, 8);
    private const string SqlServerTestConnectionEnvVar = "HB_TEST_SQLSERVER_CONNECTION";

    [SalesDetailReportSqlServerFact]
    public async Task 分店栏单商品精确筛选按授权门店范围且统计商品码去空格()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedStoreAsync("B1", "授权店");
        await fixture.SeedStoreAsync("B2", "范围外店");
        await fixture.SeedChinaSupplierAsync("C1", "国内供应商");
        await fixture.SeedProductAsync("P-ONE", "商品一");
        await fixture.SeedProductAsync("P-TWO", "商品二");
        await fixture.SeedFactAsync(SeedDate, "B1", "C1", " P-ONE ", 1, 10m, "统计一");
        await fixture.SeedFactAsync(SeedDate, "B1", "C1", "P-TWO", 3, 30m, "统计二");
        await fixture.SeedFactAsync(SeedDate, "B2", "C1", "P-ONE", 9, 90m, "统计一");

        var result = await fixture.CreateService().GetSalesDetailReportAsync(
            Range(), SalesDetailKind.China, branchCodes: new() { "B1" },
            selectedProductCode: "P-ONE", sections: new[] { SalesDetailSection.Branches });

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        var branch = Assert.Single(result.Data!.Branches!.Rows);
        Assert.Equal("B1", branch.Code);
        Assert.Equal(10m, branch.Revenue);
        Assert.Equal(1, branch.Quantity);
        Assert.DoesNotContain(result.Data.Branches.Rows, row => row.Code == "B2");
    }

    [SalesDetailReportSqlServerFact]
    public async Task 不同销售种类和所选供应商保持供应商归属口径()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedStoreAsync("B1", "测试店");
        await fixture.SeedChinaSupplierAsync("C1", "国内一");
        await fixture.SeedLocalSupplierAsync("200", "HB仓库");
        await fixture.SeedProductAsync("P-CN", "国内商品");
        await fixture.SeedProductAsync("P-AU", "澳洲商品");
        await fixture.SeedMappingAsync("P-CN", "C1");
        await fixture.SeedFactAsync(SeedDate, "B1", "200", "P-CN", 2, 20m, "国内统计商品");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-AU", 5, 50m, "澳洲统计商品");

        var china = await fixture.CreateService().GetSalesDetailReportAsync(
            Range(), SalesDetailKind.China, branchCodes: new() { "B1" },
            selectedSupplierCode: "C1", selectedProductCode: "P-CN",
            sections: new[] { SalesDetailSection.Branches });
        var australia = await fixture.CreateService().GetSalesDetailReportAsync(
            Range(), SalesDetailKind.Australia, branchCodes: new() { "B1" },
            selectedSupplierCode: "200", selectedProductCode: "P-CN",
            sections: new[] { SalesDetailSection.Branches });

        var chinaBranch = Assert.Single(china.Data!.Branches!.Rows);
        Assert.Equal("B1", chinaBranch.Code);
        Assert.Equal(20m, chinaBranch.Revenue);
        var australiaBranch = Assert.Single(australia.Data!.Branches!.Rows);
        Assert.Equal("B1", australiaBranch.Code);
        Assert.Equal(20m, australiaBranch.Revenue);
    }

    [SalesDetailReportSqlServerFact]
    public async Task 关键词多个token可分别命中商品和国内供应商字段()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedStoreAsync("B1", "测试店");
        await fixture.SeedChinaSupplierAsync("C2", "Beta 国内供应商");
        await fixture.SeedChinaSupplierAsync("C2", "Beta 国内供应商重复记录");
        await fixture.SeedProductAsync("P-MULTI", "普通商品", englishName: "Alpha 英文名", itemNumber: "货号");
        await fixture.SeedFactAsync(SeedDate, "B1", "C2", "P-MULTI", 4, 40m, "统计名称", "统计条码");
        await fixture.EnableProjectionAsync(SeedDate);

        var result = await fixture.CreateService().GetSalesDetailReportAsync(
            Range(), SalesDetailKind.China, branchCodes: new() { "B1" }, search: "Alpha Beta",
            sections: new[] { SalesDetailSection.Products });

        var product = Assert.Single(result.Data!.Products!.Rows);
        Assert.Equal("P-MULTI", product.Code);
        Assert.Equal(40m, product.Revenue);
        Assert.Equal(1, result.Data.Products.Total);
    }

    [SalesDetailReportSqlServerFact]
    public async Task 重复ProductCode元信息同时命中搜索也不会放大汇总和商品销售事实()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedStoreAsync("B1", "测试店");
        await fixture.SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await fixture.SeedProductAsync("P-DUP", "商品主记录 Alpha", uuid: "product-dup-a");
        await fixture.SeedProductAsync("P-DUP", "商品重复记录 Alpha", uuid: "product-dup-b");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-DUP", 2, 100m, "统计商品");
        await fixture.EnableProjectionAsync(SeedDate);

        var result = await fixture.CreateService().GetSalesDetailReportAsync(
            Range(), SalesDetailKind.Australia, branchCodes: new() { "B1" },
            search: "Alpha", sections: new[] { SalesDetailSection.Summary, SalesDetailSection.Products });

        var product = Assert.Single(result.Data!.Products!.Rows);
        Assert.Equal("P-DUP", product.Code);
        Assert.Equal(100m, product.Revenue);
        Assert.Equal(2, product.Quantity);
        Assert.Equal(1, result.Data.Products.Total);
        Assert.Equal(100m, result.Data.Summary!.Summary!.Revenue);
        Assert.Equal(2, result.Data.Summary.Summary.Quantity);
    }

    [SalesDetailReportSqlServerFact]
    public async Task 关键词投影表缺失时接口拒绝整段事实回退()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedStoreAsync("B1", "测试店");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-ONE", 2, 20m, "Alpha 商品");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.CreateService().GetSalesDetailReportAsync(
                Range(), SalesDetailKind.Australia, branchCodes: new() { "B1" }, search: "Alpha"));
        Assert.Contains("已拒绝整段事实回退", error.Message);
    }

    [SalesDetailReportSqlServerFact]
    public async Task 全量请求带selectedProduct时产品候选仍保留而分店栏按商品收窄()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedStoreAsync("B1", "测试店");
        await fixture.SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await fixture.SeedProductAsync("P-SELECT", "已选商品");
        await fixture.SeedProductAsync("P-OTHER", "其他商品");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-SELECT", 1, 10m, "已选统计商品");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-OTHER", 3, 30m, "其他统计商品");

        var result = await fixture.CreateService().GetSalesDetailReportAsync(
            Range(), SalesDetailKind.Australia, branchCodes: new() { "B1" },
            selectedProductCode: " P-SELECT ");

        var branch = Assert.Single(result.Data!.Branches!.Rows);
        Assert.Equal(10m, branch.Revenue);
        Assert.Equal(1, branch.Quantity);
        Assert.Equal(new[] { "P-OTHER", "P-SELECT" }, result.Data.Products!.Rows.Select(row => row.Code).OrderBy(code => code));
        Assert.Equal(2, result.Data.Products.Total);
    }

    [SalesDetailReportSqlServerFact]
    public async Task 分店栏本期同期单商品指标和缺成本语义与全量请求一致()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedFreshStateAsync(CompareDate);
        await fixture.SeedStoreAsync("B1", "测试店");
        await fixture.SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await fixture.SeedProductAsync("P-ROLL", "跨期商品");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-ROLL", 3, 30m, "本期统计商品", totalCost: 12m, grossProfit: 18m);
        await fixture.SeedFactAsync(CompareDate, "B1", "AUS1", "P-ROLL", 2, 20m, "同期统计商品", totalCost: null, grossProfit: null, useAmountAsDefaultGrossProfit: false);

        var branchesOnly = await fixture.CreateService().GetSalesDetailReportAsync(
            Range(SeedDate, CompareDate), SalesDetailKind.Australia, branchCodes: new() { "B1" },
            selectedProductCode: "P-ROLL", sections: new[] { SalesDetailSection.Branches });
        var full = await fixture.CreateService().GetSalesDetailReportAsync(
            Range(SeedDate, CompareDate), SalesDetailKind.Australia, branchCodes: new() { "B1" },
            selectedProductCode: "P-ROLL");

        var branch = Assert.Single(branchesOnly.Data!.Branches!.Rows);
        Assert.Equal(30m, branch.Revenue);
        Assert.Equal(20m, branch.CompareRevenue);
        Assert.Equal(3, branch.Quantity);
        Assert.Equal(2, branch.CompareQuantity);
        Assert.Equal(30m, branch.AverageTransaction);
        Assert.Equal(20m, branch.CompareAverageTransaction);
        Assert.Equal(10m, branch.AverageUnitPrice);
        Assert.Equal(10m, branch.CompareAverageUnitPrice);
        Assert.Equal(18m, branch.GrossProfit);
        Assert.Null(branch.CompareGrossProfit); // 同期缺成本时不能伪造毛利和毛利率。
        Assert.Equal(0.6m, branch.GrossMarginRate);
        Assert.Null(branch.CompareGrossMarginRate);

        var fullBranch = Assert.Single(full.Data!.Branches!.Rows);
        Assert.Equal(branch.Revenue, fullBranch.Revenue);
        Assert.Equal(branch.CompareRevenue, fullBranch.CompareRevenue);
        Assert.Equal(branch.Quantity, fullBranch.Quantity);
        Assert.Equal(branch.CompareQuantity, fullBranch.CompareQuantity);
        Assert.Equal(branch.AverageTransaction, fullBranch.AverageTransaction);
        Assert.Equal(branch.CompareAverageTransaction, fullBranch.CompareAverageTransaction);
        Assert.Equal(branch.AverageUnitPrice, fullBranch.AverageUnitPrice);
        Assert.Equal(branch.CompareAverageUnitPrice, fullBranch.CompareAverageUnitPrice);
        Assert.Equal(branch.GrossProfit, fullBranch.GrossProfit);
        Assert.Null(fullBranch.CompareGrossProfit);
        Assert.Equal(branch.GrossMarginRate, fullBranch.GrossMarginRate);
        Assert.Null(fullBranch.CompareGrossMarginRate);
    }

    [SalesDetailReportSqlServerFact]
    public async Task 日投影搜索与原查询七结果集一致并保留空白权限与跨日最大名称语义()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        var earlier = SeedDate.AddDays(-2);
        foreach (var date in new[] { earlier, CompareDate, SeedDate }) await fixture.SeedFreshStateAsync(date);
        await fixture.SeedStoreAsync("B1", "授权店");
        await fixture.SeedStoreAsync("B2", "范围外店");
        await fixture.SeedLocalSupplierAsync("AUS1", "Beta 澳洲供应商");
        await fixture.SeedLocalSupplierAsync("200", "HB仓库");
        await fixture.SeedChinaSupplierAsync("C1", "Beta 国内供应商");
        await fixture.SeedProductAsync("P-ONE", "普通资料");
        await fixture.SeedProductAsync("P-TWO", "Alpha 商品", uuid: "a");
        await fixture.SeedProductAsync("P-TWO", "Alpha 重复商品", uuid: "b");
        await fixture.SeedMappingAsync("P-ONE", "C1");
        await fixture.SeedFactAsync(CompareDate, "B1", "200", "P-ONE", 2, 20m, "Alpha 旧名称", "Z-OLD");
        await fixture.SeedFactAsync(SeedDate, "B1", "200", "P-ONE", -1, -10m, "Zulu 当前名称", "A-NEW");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-TWO", 3, 30m, "普通名称");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", " P-TWO", 1, 5m, "普通名称");
        await fixture.SeedFactAsync(SeedDate, " B1", "AUS1", "P-TWO", 100, 999m, "权限外原始门店码");
        await fixture.SeedFactAsync(SeedDate, "B2", "AUS1", "P-TWO", 10, 100m, "范围外");
        await fixture.SeedFactAsync(earlier, "B1", "C1", "P-ONE", 4, 40m, "同期名称", totalCost: null,
            grossProfit: null, useAmountAsDefaultGrossProfit: false);
        var range = new DateRangeDto { StartDate = CompareDate, EndDate = SeedDate, CompareStartDate = earlier, CompareEndDate = earlier };
        await fixture.EnableProjectionAsync(earlier, CompareDate, SeedDate);

        foreach (var kind in new[] { SalesDetailKind.Australia, SalesDetailKind.China })
        // 较长候选词可能位于末尾，也可能只命中历史条码；其余词仍可分别命中供应商和商品资料。
        foreach (var query in new[] { ("Alpha", (string?)null), ("Alpha Beta", (string?)null),
            ("Beta Alpha", (string?)null), ("Beta Z-OLD", (string?)null), ("Beta Zulu", (string?)null), ("商品 Beta", (string?)null),
            ("P-ONE", (string?)null), ("Beta", "P-ONE") })
        {
            var original = await fixture.ReadRawReportAsync(range, kind, query.Item1, query.Item2, projected: false);
            var projected = await fixture.ReadRawReportAsync(range, kind, query.Item1, query.Item2, projected: true);
            Assert.Equal(original, projected);
        }
        var compressed = await fixture.CreateService().GetSalesDetailReportAsync(range, SalesDetailKind.Australia,
            branchCodes: new() { "B1" }, search: "Alpha");
        Assert.Equal(35m, compressed.Data!.Summary!.Summary!.Revenue);
        Assert.Equal("P-TWO", Assert.Single(compressed.Data.Products!.Rows).Code);
        Assert.Equal(1, compressed.Data.Products.Total);
        Assert.Equal(45m, compressed.Data.Suppliers!.Rows.Sum(row => row.Revenue));
        Assert.Equal(DateTimeKind.Utc, compressed.StatisticUpdatedAt!.Value.Kind);
        Assert.EndsWith("Z\"", JsonSerializer.Serialize(compressed.StatisticUpdatedAt));
    }

    [SalesDetailReportSqlServerFact]
    public async Task 最长供应商候选经仓库映射且晚查重复商品资料仍与原查询七集一致()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedStoreAsync("B1", "授权店");
        await fixture.SeedChinaSupplierAsync("C1", "WholesaleSupplierAnchor");
        await fixture.SeedProductAsync("P-SELECT", "选中商品");
        await fixture.SeedProductAsync("P-LATE", "普通目录", uuid: "a");
        await fixture.SeedProductAsync("P-LATE", "重复目录", itemNumber: "X7", uuid: "b");
        await fixture.SeedMappingAsync("P-SELECT", "C1");
        await fixture.SeedMappingAsync("P-LATE", "C1");
        await fixture.SeedFactAsync(SeedDate, "B1", "200", "P-SELECT", 1, 10m, "普通事实");
        await fixture.SeedFactAsync(SeedDate, "B1", "200", " P-LATE ", 3, 30m, "空白码事实");
        await fixture.EnableProjectionAsync(SeedDate);

        const string search = "X7 WholesaleSupplierAnchor";
        var original = await fixture.ReadRawReportAsync(
            Range(), SalesDetailKind.China, search, "P-SELECT", projected: false);
        var projected = await fixture.ReadRawReportAsync(
            Range(), SalesDetailKind.China, search, "P-SELECT", projected: true);
        Assert.Equal(original, projected); // fixture 同时断言固定返回七个结果集。

        using var document = JsonDocument.Parse(projected);
        var sets = document.RootElement;
        Assert.Equal(7, sets.GetArrayLength());
        Assert.Equal(0m, sets[1][0][4].GetDecimal()); // 选中商品不匹配短词，关键词筛选后的汇总为空。
        Assert.Equal(10m, sets[2][0][4].GetDecimal()); // 左两栏忽略关键词，仍只按选中商品统计。
        Assert.Equal(10m, sets[3][0][4].GetDecimal());
        Assert.Equal("P-LATE", sets[4][0][0].GetString()); // selectedProduct 不缩小商品候选。
        Assert.Equal(30m, sets[4][0][4].GetDecimal()); // 重复 Product 元数据不能放大销售金额。
        Assert.Equal(1, sets[5][0][0].GetInt32());
    }

    [SalesDetailReportSqlServerFact]
    public async Task 规范商品码候选扩回全部原始码并保持跨原始码最大名称金额与选中语义()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedStoreAsync("B1", "授权店");
        await fixture.SeedLocalSupplierAsync("AUS1", "Aus");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", " P-CROSS", 1, 10m, "Alpha");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-CROSS", 2, 20m, "Zulu");
        await fixture.EnableProjectionAsync(SeedDate);

        foreach (var query in new[] { (Search: "Alpha", Selected: (string?)null),
            (Search: "Alpha Aus", Selected: (string?)null), (Search: "Alpha Aus", Selected: (string?)"P-CROSS") })
        {
            var original = await fixture.ReadRawReportAsync(
                Range(), SalesDetailKind.Australia, query.Search, query.Selected, projected: false);
            var projected = await fixture.ReadRawReportAsync(
                Range(), SalesDetailKind.Australia, query.Search, query.Selected, projected: true);
            Assert.Equal(original, projected);
        }

        using var selected = JsonDocument.Parse(await fixture.ReadRawReportAsync(
            Range(), SalesDetailKind.Australia, "Alpha Aus", "P-CROSS", projected: true));
        var sets = selected.RootElement;
        Assert.Equal(0m, sets[1][0][4].GetDecimal()); // 规范码聚合后的 MAX 名称是 Zulu，不能用局部 raw 的 Alpha 命中。
        Assert.Equal(30m, sets[2][0][4].GetDecimal()); // 左两栏仍保留选中规范商品码下的全部 raw 金额。
        Assert.Equal(30m, sets[3][0][4].GetDecimal());
        Assert.Equal(0, sets[4].GetArrayLength());
        Assert.Equal(0, sets[5][0][0].GetInt32());
    }

    [SalesDetailReportSqlServerFact]
    public async Task 多词预筛保留当前重复商品资料供应商命中与选中商品()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedStoreAsync("B1", "授权店");
        await fixture.SeedLocalSupplierAsync("AUS-META", "普通供应商");
        await fixture.SeedLocalSupplierAsync("AUS-SUP", "SupplierNeedle");
        await fixture.SeedLocalSupplierAsync("AUS-SELECT", "选中供应商");
        await fixture.SeedProductAsync("P-META", "普通目录", uuid: "meta-a");
        await fixture.SeedProductAsync("P-META", "重复目录", englishName: "MetaNeedle", uuid: "meta-b");
        await fixture.SeedProductAsync("P-SUP", "供应商商品", itemNumber: "LateCode");
        await fixture.SeedProductAsync("P-SELECT", "选中商品");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS-META", "P-META", 1, 10m, "VeryLongAnchor");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS-SUP", "P-SUP", 2, 20m, "VeryLongAnchor");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS-SELECT", "P-SELECT", 4, 40m, "普通事实");
        await fixture.EnableProjectionAsync(SeedDate);

        async Task<JsonDocument> ReadEquivalentAsync(string search, string? selectedProduct = null)
        {
            var original = await fixture.ReadRawReportAsync(
                Range(), SalesDetailKind.Australia, search, selectedProduct, projected: false);
            var projected = await fixture.ReadRawReportAsync(
                Range(), SalesDetailKind.Australia, search, selectedProduct, projected: true);
            Assert.Equal(original, projected);
            return JsonDocument.Parse(projected);
        }

        // 候选供应商为空且其余词也不命中供应商时，重复 Product 中任一当前资料命中即可保留规范码。
        using (var metadata = await ReadEquivalentAsync("VeryLongAnchor MetaNeedle"))
        {
            Assert.Equal("P-META", metadata.RootElement[4][0][0].GetString());
            Assert.Equal(10m, metadata.RootElement[4][0][4].GetDecimal());
            Assert.Equal(1, metadata.RootElement[5][0][0].GetInt32());
        }
        // 其余词命中当前供应商时必须跳过商品预筛，留给最终跨字段 AND 判断。
        using (var supplier = await ReadEquivalentAsync("VeryLongAnchor SupplierNeedle"))
        {
            Assert.Equal("P-SUP", supplier.RootElement[4][0][0].GetString());
            Assert.Equal(20m, supplier.RootElement[4][0][4].GetDecimal());
        }
        // anchor 已命中候选供应商时，仍在读取事实后查剩余商品资料。
        using (var lateMetadata = await ReadEquivalentAsync("SupplierNeedle LateCode"))
        {
            Assert.Equal("P-SUP", lateMetadata.RootElement[4][0][0].GetString());
            Assert.Equal(20m, lateMetadata.RootElement[4][0][4].GetDecimal());
        }
        // selectedProduct 即使不命中任一关键词，也必须留在事实中供供应商和分店两栏使用。
        using (var selected = await ReadEquivalentAsync("VeryLongAnchor MetaNeedle", "P-SELECT"))
        {
            Assert.Equal(0m, selected.RootElement[1][0][4].GetDecimal());
            Assert.Equal(40m, selected.RootElement[2][0][4].GetDecimal());
            Assert.Equal(40m, selected.RootElement[3][0][4].GetDecimal());
            Assert.Equal("P-META", selected.RootElement[4][0][0].GetString());
        }
    }

    [SalesDetailReportSqlServerFact]
    public async Task 名称发布标识或映射变化时投影跳过该日期并提示而不回退原查询()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedStoreAsync("B1", "授权店");
        await fixture.SeedChinaSupplierAsync("C1", "国内供应商");
        await fixture.SeedProductAsync("P-ONE", "普通资料");
        await fixture.SeedMappingAsync("P-ONE", "C1");
        await fixture.SeedFactAsync(SeedDate, "B1", "200", "P-ONE", 2, 20m, "Alpha 原名称");
        await fixture.EnableProjectionAsync(SeedDate);
        await fixture.ChangeStatisticNameAsync(SeedDate, "Zulu 新名称");
        var stale = await fixture.ReadRawReportWithSkippedAsync(Range(), SalesDetailKind.China, "Zulu", null, projected: true);
        Assert.Equal(new[] { SeedDate }, stale.Skipped);
        using (var staleJson = JsonDocument.Parse(stale.Json))
        {
            Assert.Equal(0, staleJson.RootElement[4].GetArrayLength());
            Assert.Equal(0m, staleJson.RootElement[1][0][4].GetDecimal());
        }
        // 接口不再整份回退原查询：该日不计入结果，只在提示里列出。
        var result = await fixture.CreateService().GetSalesDetailReportAsync(Range(), SalesDetailKind.China,
            branchCodes: new() { "B1" }, search: "Zulu");
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        Assert.Empty(result.Data!.Products!.Rows);
        Assert.Contains("2026-09-09", result.StatisticMessage);
        Assert.Contains("缺少可用的关键词查询投影", result.StatisticMessage);

        await fixture.RefreshProjectionAsync(SeedDate);
        Assert.Empty((await fixture.ReadRawReportWithSkippedAsync(Range(), SalesDetailKind.China, "Zulu", null, projected: true)).Skipped);
        await fixture.SeedMappingAsync("P-ONE", "C1"); // 旧 SQL 会放大重复映射，签名必须也能识别重复项。
        var remapped = await fixture.ReadRawReportWithSkippedAsync(Range(), SalesDetailKind.China, "Zulu", null, projected: true);
        Assert.Equal(new[] { SeedDate }, remapped.Skipped);
    }

    [SalesDetailReportSqlServerFact]
    public async Task 澳洲普通商品搜索允许无放大的映射变化但中国搜索与分类变化跳过旧投影日期()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedStoreAsync("B1", "授权店");
        await fixture.SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await fixture.SeedChinaSupplierAsync("C1", "国内一");
        await fixture.SeedChinaSupplierAsync("C2", "国内二");
        await fixture.SeedProductAsync("P-ONE", "普通资料");
        await fixture.SeedMappingAsync("P-ONE", "C1");
        await fixture.SeedFactAsync(SeedDate, "B1", "200", "P-ONE", 2, 20m, "Alpha 商品");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-TWO", 1, 5m, "其他商品");
        await fixture.EnableProjectionAsync(SeedDate);
        await fixture.ChangeMappingAsync("P-ONE", "C2");
        // 放行仅承诺对外澳洲数据一致；不比较澳洲 DTO 从未消费的两个中国分母。
        using var original = JsonDocument.Parse(await fixture.ReadRawReportAsync(Range(), SalesDetailKind.Australia, "Alpha", null, projected: false));
        var projectedRead = await fixture.ReadRawReportWithSkippedAsync(Range(), SalesDetailKind.Australia, "Alpha", null, projected: true);
        Assert.Empty(projectedRead.Skipped);
        using var projected = JsonDocument.Parse(projectedRead.Json);
        for (var i = 0; i < 6; i++) Assert.Equal(original.RootElement[i].GetRawText(), projected.RootElement[i].GetRawText());
        foreach (var column in new[] { 0, 2 }) Assert.Equal(original.RootElement[6][0][column].GetDecimal(), projected.RootElement[6][0][column].GetDecimal());
        var china = await fixture.ReadRawReportWithSkippedAsync(Range(), SalesDetailKind.China, "Alpha", null, projected: true);
        Assert.Equal(new[] { SeedDate }, china.Skipped);
        var supplierWord = await fixture.ReadRawReportWithSkippedAsync(Range(), SalesDetailKind.Australia, "国内二", null, projected: true);
        Assert.Equal(new[] { SeedDate }, supplierWord.Skipped);
        await fixture.SeedChinaSupplierAsync("AUS1", "改变分类");
        var reclassified = await fixture.ReadRawReportWithSkippedAsync(Range(), SalesDetailKind.Australia, "Alpha", null, projected: true);
        Assert.Equal(new[] { SeedDate }, reclassified.Skipped);
    }

    [SalesDetailReportSqlServerFact]
    public async Task 批末完成时间变化不使相同商品事实投影失效()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedStoreAsync("B1", "授权店");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-ONE", 2, 20m, "Alpha 商品");
        await fixture.SetPublishStatusAsync(SeedDate, "ProvisionalFresh");
        await fixture.EnableProjectionAsync(SeedDate);
        await fixture.SetPublishStatusAsync(SeedDate, "Fresh");
        Assert.Equal(await fixture.ReadRawReportAsync(Range(), SalesDetailKind.Australia, "Alpha", null, projected: false),
            await fixture.ReadRawReportAsync(Range(), SalesDetailKind.Australia, "Alpha", null, projected: true));
    }

    [SalesDetailReportSqlServerFact]
    public async Task 无关商品映射或供应商新增不淘汰历史中国查询但实际映射变化跳过用到它的日期()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedFreshStateAsync(CompareDate);
        await fixture.SeedStoreAsync("B1", "授权店");
        await fixture.SeedChinaSupplierAsync("C1", "Alpha 国内供应商");
        await fixture.SeedChinaSupplierAsync("C2", "另一供应商");
        await fixture.SeedMappingAsync("P-ONE", "C1");
        await fixture.SeedFactAsync(SeedDate, "B1", "200", "P-ONE", 2, 20m, "Alpha 商品");
        await fixture.SeedFactAsync(CompareDate, "B1", "200", "P-ONE", 1, 5m, "Alpha 商品");
        await fixture.EnableProjectionAsync(SeedDate, CompareDate);
        await fixture.SeedMappingAsync("UNRELATED", "C2");
        await fixture.SeedChinaSupplierAsync("UNRELATED", "新增未销售供应商");
        foreach (var kind in new[] { SalesDetailKind.Australia, SalesDetailKind.China })
            Assert.Equal(await fixture.ReadRawReportAsync(Range(SeedDate, CompareDate), kind, "Alpha", null, projected: false),
                await fixture.ReadRawReportAsync(Range(SeedDate, CompareDate), kind, "Alpha", null, projected: true));
        await fixture.ChangeMappingAsync("P-ONE", "C2");
        var changed = await fixture.ReadRawReportWithSkippedAsync(Range(SeedDate, CompareDate), SalesDetailKind.China, "Alpha", null, projected: true);
        Assert.Equal(new[] { CompareDate, SeedDate }, changed.Skipped);
    }

    [SalesDetailReportSqlServerFact]
    public async Task 下次日任务排队和运行时仍读取相同的已发布投影()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedStoreAsync("B1", "授权店");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-ONE", 2, 20m, "Alpha 商品");
        await fixture.EnableProjectionAsync(SeedDate);
        foreach (var status in new[] { "Queued", "Running" })
        {
            await fixture.BeginNextPublishAsync(SeedDate, status);
            Assert.Equal(await fixture.ReadRawReportAsync(Range(), SalesDetailKind.Australia, "Alpha", null, projected: false),
                await fixture.ReadRawReportAsync(Range(), SalesDetailKind.Australia, "Alpha", null, projected: true));
        }
        await fixture.ChangeStatisticNameAsync(SeedDate, "Zulu 新名称");
        var changed = await fixture.ReadRawReportWithSkippedAsync(Range(), SalesDetailKind.Australia, "Zulu", null, projected: true);
        Assert.Equal(new[] { SeedDate }, changed.Skipped);
    }

    [SalesDetailReportSqlServerFact]
    public async Task 关键词查询跳过失败日且旧投影不会触发整段事实回退()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedFreshStateAsync(CompareDate);
        await fixture.SeedStoreAsync("B1", "授权店");
        await fixture.SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await fixture.SeedProductAsync("P-ONE", "Alpha 商品");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-ONE", 3, 30m, "Alpha 商品");
        await fixture.SeedFactAsync(CompareDate, "B1", "AUS1", "P-ONE", 2, 20m, "Alpha 商品");
        var failedJobId = Guid.Parse("12345678-1234-1234-1234-1234567890ab");
        var firstCheck = new DateTime(2026, 9, 10, 1, 2, 3, 120, DateTimeKind.Utc);
        await fixture.EnableProjectionAsync(SeedDate, CompareDate);
        await fixture.SetFailedPublishedStateAsync(CompareDate, failedJobId, firstCheck);

        var range = Range(SeedDate, CompareDate);
        async Task AssertSkippedAsync()
        {
            Assert.Equal(
                await fixture.ReadRawReportAsync(range, SalesDetailKind.Australia, "Alpha", null, projected: false),
                await fixture.ReadRawReportAsync(range, SalesDetailKind.Australia, "Alpha", null, projected: true));
            var response = await fixture.CreateService().GetSalesDetailReportAsync(
                range, SalesDetailKind.Australia, branchCodes: new() { "B1" }, search: "Alpha");
            Assert.Equal(SalesStatisticRefreshStatus.Fresh, response.StatisticStatus);
            Assert.Contains("2026-09-08", response.StatisticMessage);
            Assert.Contains("已跳过", response.StatisticMessage);
            Assert.Equal(30m, response.Data!.Summary!.Summary!.Revenue);
            Assert.Equal(0m, response.Data.Summary.Summary.CompareRevenue);
            Assert.Equal(30m, Assert.Single(response.Data.Suppliers!.Rows).Revenue);
            Assert.Equal(30m, Assert.Single(response.Data.Branches!.Rows).Revenue);
            Assert.Equal(30m, Assert.Single(response.Data.Products!.Rows).Revenue);
        }

        // 已有的失败日日投影不能将其旧金额带回报表。
        await AssertSkippedAsync();
        // 即使失败日缺投影行，也只跳过该日，不触发覆盖异常和整段事实回退。
        await fixture.DeleteProjectionStateAsync(CompareDate);
        await AssertSkippedAsync();

        await fixture.AdvanceFailedLastCheckedAsync(CompareDate);
        await AssertSkippedAsync();

        await fixture.RemoveLastAggregationAsync(CompareDate);
        await AssertSkippedAsync();
    }

    [SalesDetailReportSqlServerFact]
    public async Task 投影范围超过731天拒绝执行且缺少发布时间的日期被跳过但无任务标识仍可使用()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedStoreAsync("B1", "授权店");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-ONE", 2, 20m, "Alpha 商品");
        await fixture.SetPublishIdentityAsync(SeedDate, missingAggregation: false);
        await fixture.EnableProjectionAsync(SeedDate);
        Assert.Equal(await fixture.ReadRawReportAsync(Range(), SalesDetailKind.Australia, "Alpha", null, projected: false),
            await fixture.ReadRawReportAsync(Range(), SalesDetailKind.Australia, "Alpha", null, projected: true));
        // 页面上限是两年（731 天）；更长的关键词请求明确报错，不读取整段原始事实。
        foreach (var days in new[] { 732, 1001 })
        foreach (var compare in new[] { false, true })
        {
            var range = Range();
            if (compare) { range.CompareStartDate = SeedDate.AddDays(1 - days); range.CompareEndDate = SeedDate; }
            else range.StartDate = SeedDate.AddDays(1 - days);
            var error = await Assert.ThrowsAsync<SqlException>(() => fixture.ReadRawReportAsync(range, SalesDetailKind.Australia, "Alpha", null, projected: true));
            Assert.Equal(51012, error.Number);
            Assert.Contains("日期范围超限", error.Message);
        }
        await fixture.SetPublishIdentityAsync(SeedDate, missingAggregation: true);
        await fixture.RefreshProjectionAsync(SeedDate);
        var unpublished = await fixture.ReadRawReportWithSkippedAsync(Range(), SalesDetailKind.Australia, "Alpha", null, projected: true);
        Assert.Equal(new[] { SeedDate }, unpublished.Skipped);
    }

    [SalesDetailReportSqlServerFact]
    public async Task 商品改映射只跳过卖过该商品的日期其余日期继续读投影()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedFreshStateAsync(CompareDate);
        await fixture.SeedStoreAsync("B1", "授权店");
        await fixture.SeedChinaSupplierAsync("C1", "国内一");
        await fixture.SeedChinaSupplierAsync("C2", "国内二");
        await fixture.SeedMappingAsync("P-ONE", "C1");
        await fixture.SeedMappingAsync("P-TWO", "C1");
        await fixture.SeedFactAsync(SeedDate, "B1", "200", "P-ONE", 2, 20m, "Alpha 一号");
        await fixture.SeedFactAsync(CompareDate, "B1", "200", "P-TWO", 1, 5m, "Alpha 二号");
        await fixture.EnableProjectionAsync(SeedDate, CompareDate);
        await fixture.ChangeMappingAsync("P-ONE", "C2");

        var read = await fixture.ReadRawReportWithSkippedAsync(Range(SeedDate, CompareDate), SalesDetailKind.China, "Alpha", null, projected: true);
        Assert.Equal(new[] { SeedDate }, read.Skipped);
        using var json = JsonDocument.Parse(read.Json);
        // 本期（SeedDate）被跳过，同期（CompareDate）的 P-TWO 仍从投影候选读出。
        var product = Assert.Single(json.RootElement[4].EnumerateArray());
        Assert.Equal("P-TWO", product[0].GetString());
        Assert.Equal(0m, product[4].GetDecimal());
        Assert.Equal(5m, product[5].GetDecimal());
    }

    [SalesDetailReportSqlServerFact]
    public async Task 对账失败日期缺少投影时关键词查询按失败日跳过并只提示一次()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedFreshStateAsync(CompareDate);
        await fixture.SeedStoreAsync("B1", "授权店");
        await fixture.SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-ONE", 2, 20m, "Hoodie 本期");
        await fixture.SeedFactAsync(CompareDate, "B1", "AUS1", "P-ONE", 1, 5m, "Hoodie 同期");
        await fixture.EnableProjectionAsync(SeedDate, CompareDate);
        // 生产 2026-04-09：对账失败但已聚合，发布状态为 Failed，日投影随之撤销。
        await fixture.SetPublishStatusAsync(CompareDate, "Failed");
        await fixture.RefreshProjectionAsync(CompareDate);

        var result = await fixture.CreateService().GetSalesDetailReportAsync(Range(SeedDate, CompareDate), SalesDetailKind.Australia,
            branchCodes: new() { "B1" }, search: "Hoodie");
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        var row = Assert.Single(result.Data!.Products!.Rows);
        Assert.Equal(20m, row.Revenue);
        Assert.Equal(0m, row.CompareRevenue);
        // 失败日按"统计失败已跳过"提示一次，不再重复成"缺少投影"或"对账未通过、金额可能有出入"。
        Assert.Contains("2026-09-08", result.StatisticMessage);
        Assert.Contains("已跳过", result.StatisticMessage);
        Assert.DoesNotContain("缺少可用的关键词查询投影", result.StatisticMessage);
        Assert.DoesNotContain("对账未通过", result.StatisticMessage);
    }

    [SalesDetailReportSqlServerFact]
    public async Task 全分店关键词查询的供应商分店与分母读月投影且与无关键词页面一致()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedStoreAsync("B1", "一店");
        await fixture.SeedStoreAsync("B2", "二店");
        await fixture.SeedLocalSupplierAsync("A1", "澳洲一");
        await fixture.SeedChinaSupplierAsync("C1", "国内一");
        await fixture.SeedMappingAsync("P-ONE", "C1");
        var days = Enumerable.Range(0, 3).Select(offset => SeedDate.AddDays(-offset)).ToList();
        foreach (var day in days)
        {
            await fixture.SeedFreshStateAsync(day);
            await fixture.SeedFactAsync(day, "B1", "200", "P-ONE", 2, 20m, "Hoodie 仓库款");
            await fixture.SeedFactAsync(day, "B2", "A1", "P-TWO", 3, 30m, "普通商品");
        }
        await fixture.EnableProjectionAsync(days.ToArray());
        await fixture.EnableMonthlyProjectionAsync();
        await fixture.CatchUpProjectionAsync();
        // 最早一天对账失败：关键词结果与月投影三栏都排除它（失败日在守卫前整体删除，不进投影跳过清单）。
        await fixture.SetPublishStatusAsync(days[2], "Failed");
        await fixture.RefreshProjectionAsync(days[2]);
        var range = new DateRangeDto { StartDate = days[2], EndDate = days[0] };

        foreach (var kind in new[] { SalesDetailKind.Australia, SalesDetailKind.China })
        {
            var keyword = await fixture.ReadRawReportWithSkippedAsync(range, kind, "Hoodie", null, projected: true, allBranches: true);
            Assert.Empty(keyword.Skipped);
            using var keywordJson = JsonDocument.Parse(keyword.Json);
            using var plainJson = JsonDocument.Parse(await fixture.ReadUnscopedReportAsync(range, kind, null, null, monthly: true));
            foreach (var set in new[] { 2, 3, 6 })
                Assert.Equal(plainJson.RootElement[set].GetRawText(), keywordJson.RootElement[set].GetRawText());
            var product = Assert.Single(keywordJson.RootElement[4].EnumerateArray());
            Assert.Equal("P-ONE", product[0].GetString());
            Assert.Equal(40m, product[4].GetDecimal());
        }
    }

    [SalesDetailReportSqlServerFact]
    public async Task 空结果回归_日投影压缩读取无销售事实时返回完整零汇总()
    {
        foreach (var compare in new[] { false, true })
        {
            await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
            await fixture.SeedFreshStateAsync(SeedDate);
            await fixture.SeedFreshStateAsync(CompareDate);
            await fixture.SeedStoreAsync("B1", "授权店");
            await fixture.EnableProjectionAsync(SeedDate, CompareDate);

            // 必须经真实 SQL 的压缩 JSON 读取；普通 reader 会把 DBNull 转成 0，无法复现此回归。
            var result = await fixture.CreateService().GetSalesDetailReportAsync(
                Range(SeedDate, compare ? CompareDate : null), SalesDetailKind.Australia,
                branchCodes: new() { "B1" }, search: "HB246-CC-007");

            Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
            var summary = result.Data!.Summary!.Summary!;
            Assert.Equal(0m, summary.Revenue);
            Assert.Equal(0, summary.Quantity);
            Assert.Equal(compare ? 0m : (decimal?)null, summary.CompareRevenue);
            Assert.Equal(compare ? 0 : (int?)null, summary.CompareQuantity);
            Assert.Null(summary.GrossProfit);
            Assert.Null(summary.CompareGrossProfit);
            Assert.Empty(result.Data.Suppliers!.Rows);
            Assert.Empty(result.Data.Branches!.Rows);
            Assert.Empty(result.Data.Products!.Rows);
            Assert.Equal(0, result.Data.Products.Total);
        }
    }

    [SalesDetailReportSqlServerFact]
    public async Task 空结果回归_日投影关键词未命中仍保留供应商分店全量范围()
    {
        foreach (var compare in new[] { false, true })
        {
            await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
            await fixture.SeedFreshStateAsync(SeedDate);
            await fixture.SeedFreshStateAsync(CompareDate);
            await fixture.SeedStoreAsync("B1", "授权店");
            await fixture.SeedLocalSupplierAsync("AUS1", "澳洲供应商");
            await fixture.SeedFactAsync(SeedDate, "B1", "AUS1", "P-OTHER", 2, 20m, "其他商品",
                totalCost: null, grossProfit: null, useAmountAsDefaultGrossProfit: false);
            await fixture.SeedFactAsync(CompareDate, "B1", "AUS1", "P-OTHER", 1, 10m, "其他商品",
                totalCost: null, grossProfit: null, useAmountAsDefaultGrossProfit: false);
            await fixture.EnableProjectionAsync(SeedDate, CompareDate);

            var result = await fixture.CreateService().GetSalesDetailReportAsync(
                Range(SeedDate, compare ? CompareDate : null), SalesDetailKind.Australia,
                branchCodes: new() { "B1" }, search: "HB246-CC-007");

            Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
            var summary = result.Data!.Summary!.Summary!;
            Assert.Equal(0m, summary.Revenue);
            Assert.Equal(0, summary.Quantity);
            Assert.Equal(compare ? 0m : (decimal?)null, summary.CompareRevenue);
            Assert.Null(summary.GrossProfit);
            Assert.Null(summary.CompareGrossProfit);
            Assert.Empty(result.Data.Products!.Rows);
            Assert.Equal(0, result.Data.Products.Total);
            // 关键词只过滤汇总与商品，不能顺手清空仍有销售的供应商和分店。
            foreach (var row in new[] { Assert.Single(result.Data.Suppliers!.Rows), Assert.Single(result.Data.Branches!.Rows) })
            {
                Assert.Equal(20m, row.Revenue);
                Assert.Equal(compare ? 10m : (decimal?)null, row.CompareRevenue);
                Assert.Null(row.GrossProfit);
                Assert.Null(row.CompareGrossProfit);
            }
        }
    }

    [SalesDetailReportSqlServerFact]
    public async Task 空结果回归_默认分组汇总六项统计计数为零且利润保持空值()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        await fixture.SeedFreshStateAsync(CompareDate);
        await fixture.SeedStoreAsync("B1", "授权店");
        var json = await fixture.ReadRawReportAsync(Range(SeedDate, CompareDate),
            SalesDetailKind.Australia, "", null, projected: false);
        using var results = JsonDocument.Parse(json);
        var summary = Assert.Single(results.RootElement[1].EnumerateArray());
        for (var column = 12; column <= 17; column++) Assert.Equal(0, summary[column].GetInt32());
        Assert.Equal(JsonValueKind.Null, summary[10].ValueKind);
        Assert.Equal(JsonValueKind.Null, summary[11].ValueKind);
    }


    [SalesDetailReportSqlServerFact]
    public async Task 月查询跳过失败日且整月投影不再读入失败金额()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        var month = new DateTime(2026, 7, 1);
        var day = new DateTime(2026, 7, 15);
        var freshDay = day.AddDays(1);
        var range = new DateRangeDto { StartDate = month, EndDate = month.AddMonths(1).AddDays(-1) };
        await fixture.SeedFreshStateAsync(day);
        await fixture.SeedFreshStateAsync(freshDay);
        await fixture.SeedStoreAsync("B1", "一店");
        await fixture.SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await fixture.SeedProductAsync("P-ONE", "商品一");
        await fixture.SeedFactAsync(day, "B1", "AUS1", "P-ONE", 2, 20m, "商品一");
        await fixture.SeedFactAsync(freshDay, "B1", "AUS1", "P-ONE", 3, 30m, "商品一");
        await fixture.EnableMonthlyProjectionAsync();
        await fixture.RefreshDailyAsync(day);
        await fixture.RefreshDailyAsync(freshDay);
        Assert.Contains(month, await fixture.ReadStaleMonthsAsync());
        await fixture.RefreshMonthlyAsync(month);
        await fixture.SetFailedPublishedStateAsync(
            day,
            Guid.Parse("12345678-1234-1234-1234-1234567890ab"),
            new DateTime(2026, 7, 16, 1, 2, 3, 120, DateTimeKind.Utc));

        var raw = await fixture.ReadUnscopedReportAsync(
            range, SalesDetailKind.Australia, null, null, monthly: false);
        Assert.Equal(raw, await fixture.ReadUnscopedReportAsync(
            range, SalesDetailKind.Australia, null, null, monthly: true));

        // 月表已包含失败日金额，销售明细必须避开整个月表。
        await fixture.TamperSalesDetailMonthAsync(month, 1000m);
        Assert.Equal(raw, await fixture.ReadUnscopedReportAsync(
            range, SalesDetailKind.Australia, null, null, monthly: true));
        var response = await fixture.CreateService().GetSalesDetailReportAsync(
            new DateRangeDto { StartDate = day, EndDate = freshDay }, SalesDetailKind.Australia);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, response.StatisticStatus);
        Assert.Contains("2026-07-15", response.StatisticMessage);
        Assert.Contains("已跳过", response.StatisticMessage);
        Assert.Equal(30m, response.Data!.Summary!.Summary!.Revenue);

        await fixture.AdvanceFailedLastCheckedAsync(day);
        Assert.Equal(raw, await fixture.ReadUnscopedReportAsync(
            range, SalesDetailKind.Australia, null, null, monthly: true));

        // 失败日失去已发布聚合时间后仍需跳过。
        await fixture.RemoveLastAggregationAsync(day);
        Assert.Equal(await fixture.ReadUnscopedReportAsync(
                range, SalesDetailKind.Australia, null, null, monthly: false),
            await fixture.ReadUnscopedReportAsync(
            range, SalesDetailKind.Australia, null, null, monthly: true));
    }

    [SalesDetailReportSqlServerFact]
    public async Task 月投影与原查询七结果集一致并在月身份或映射变化时自动退回日事实()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedStoreAsync("B1", "一店");
        await fixture.SeedStoreAsync("B2", "二店");
        await fixture.SeedChinaSupplierAsync("C1", "国内一");
        await fixture.SeedChinaSupplierAsync("C2", "国内二");
        await fixture.SeedLocalSupplierAsync("200", "HB仓库");
        await fixture.SeedLocalSupplierAsync("A1", "澳洲一");
        foreach (var code in new[] { "P-ONE", "P-TWO", "P-THREE", "P-FOUR", "P-FIVE" })
            await fixture.SeedProductAsync(code, $"商品{code}", $"Product {code}", $"IT-{code}");
        await fixture.SeedMappingAsync("P-ONE", "C1");
        // 本期 2025-06-20～08-10：六月和八月不满月、七月整月；同期 2024 年同样日期。
        var days = new[] { 20, 25, 30 }.Select(d => new DateTime(2025, 6, d))
            .Concat(new[] { 1, 15, 31 }.Select(d => new DateTime(2025, 7, d)))
            .Concat(new[] { 1, 5, 10 }.Select(d => new DateTime(2025, 8, d))).ToList();
        var seed = 0;
        foreach (var day in days.Concat(days.Select(d => d.AddYears(-1))))
        {
            await fixture.SeedFreshStateAsync(day);
            foreach (var branch in new[] { "B1", "B2" })
            {
                seed++;
                await fixture.SeedFactAsync(day, branch, "200", "P-ONE", 1 + seed % 5, 10m + seed, totalCost: 4m, grossProfit: 6m);
                await fixture.SeedFactAsync(day, branch, "C1", "P-TWO", 2 + seed % 3, 20m + seed, grossProfit: null, useAmountAsDefaultGrossProfit: false);
                await fixture.SeedFactAsync(day, branch, "A1", "P-THREE", 3, 30m + seed);
                if (branch == "B1") await fixture.SeedFactAsync(day, branch, "200", "P-FOUR", 1, 40m + seed);
                if (day.Day % 2 == 0) await fixture.SeedFactAsync(day, branch, "A1", "P-FIVE", 4, 50m + seed);
            }
        }
        await fixture.EnableMonthlyProjectionAsync();
        // 日表还没生成时有数据的月份不会列为待办（没有统计状态行的空月份随时可汇总成空状态）；先逐日重算，月份才可汇总。
        Assert.DoesNotContain(new DateTime(2025, 7, 1), await fixture.ReadStaleMonthsAsync());
        var staleDays = await fixture.ReadStaleDaysAsync();
        Assert.Equal(days.Count * 2, staleDays.Count);
        Assert.Equal(staleDays.OrderByDescending(d => d).ToList(), staleDays);
        foreach (var day in staleDays) await fixture.RefreshDailyAsync(day);
        Assert.Empty(await fixture.ReadStaleDaysAsync());
        var stale = await fixture.ReadStaleMonthsAsync();
        Assert.Contains(new DateTime(2025, 7, 1), stale);
        foreach (var month in stale) await fixture.RefreshMonthlyAsync(month);
        Assert.Empty(await fixture.ReadStaleMonthsAsync());

        var range = new DateRangeDto
        {
            StartDate = new DateTime(2025, 6, 20), EndDate = new DateTime(2025, 8, 10),
            CompareStartDate = new DateTime(2024, 6, 20), CompareEndDate = new DateTime(2024, 8, 10),
        };
        var noCompare = new DateRangeDto { StartDate = range.StartDate, EndDate = range.EndDate };
        async Task AssertEquivalentAsync(string label)
        {
            foreach (var kind in new[] { SalesDetailKind.China, SalesDetailKind.Australia })
            foreach (var period in new[] { range, noCompare })
            foreach (var supplier in new string?[] { null, "200", "C1" })
            foreach (var product in new string?[] { null, "P-ONE" })
            {
                var raw = await fixture.ReadUnscopedReportAsync(period, kind, supplier, product, monthly: false);
                var monthly = await fixture.ReadUnscopedReportAsync(period, kind, supplier, product, monthly: true);
                Assert.True(raw == monthly, $"{label} kind={kind} compare={period.CompareStartDate.HasValue} supplier={supplier} product={product}\n原查询: {raw}\n月投影: {monthly}");
            }
            Assert.Equal(
                await fixture.ReadUnscopedReportAsync(range, SalesDetailKind.China, null, null, monthly: false, SalesDetailSection.Products),
                await fixture.ReadUnscopedReportAsync(range, SalesDetailKind.China, null, null, monthly: true, SalesDetailSection.Products));
        }
        await AssertEquivalentAsync("全部月份有效");

        // 七月某天重新发布：七月身份变化，该日退回日事实、七月其余日期读日表；日表未追上前月份不列为待办。
        await fixture.ChangeStatisticNameAsync(new DateTime(2025, 7, 15), "改名");
        Assert.Equal(new[] { new DateTime(2025, 7, 15) }, await fixture.ReadStaleDaysAsync());
        Assert.Empty(await fixture.ReadStaleMonthsAsync());
        var notReady = await Assert.ThrowsAsync<SqlException>(() => fixture.RefreshMonthlyAsync(new DateTime(2025, 7, 1)));
        Assert.Equal(SalesDetailQueryMonthlyProjection.DaysNotReadyErrorNumber, notReady.Number);
        await AssertEquivalentAsync("七月某日身份失效");
        // 该日重算后七月整月读日表，月份进入待办；汇总后回到月表。
        await fixture.RefreshDailyAsync(new DateTime(2025, 7, 15));
        Assert.Equal(new[] { new DateTime(2025, 7, 1) }, await fixture.ReadStaleMonthsAsync());
        await AssertEquivalentAsync("七月日表有效月表失效");
        await fixture.RefreshMonthlyAsync(new DateTime(2025, 7, 1));
        Assert.Empty(await fixture.ReadStaleMonthsAsync());
        await AssertEquivalentAsync("七月重新汇总");
        // 映射变化：商品粒度仍读月表并在查询时用新映射解析，分店粒度全部退回日事实；全部日期待重算。
        await fixture.ChangeMappingAsync("P-ONE", "C2");
        Assert.Equal(days.Count * 2, (await fixture.ReadStaleDaysAsync()).Count);
        Assert.DoesNotContain(new DateTime(2025, 7, 1), await fixture.ReadStaleMonthsAsync());
        await AssertEquivalentAsync("映射变化");
        await fixture.CatchUpProjectionAsync();
        await AssertEquivalentAsync("重算后");
        // 新增一天（无月表覆盖、无日表）：读日事实；追上后读日表。
        var added = new DateTime(2025, 8, 9);
        await fixture.SeedFreshStateAsync(added);
        await fixture.SeedFactAsync(added, "B2", "200", "P-ONE", 7, 70m, totalCost: 30m, grossProfit: 40m);
        await AssertEquivalentAsync("新增日期未覆盖");
        await fixture.CatchUpProjectionAsync();
        await AssertEquivalentAsync("新增日期已覆盖");
    }

    private static readonly DateRangeDto ScopedRange = new()
    {
        StartDate = new DateTime(2025, 6, 20), EndDate = new DateTime(2025, 8, 10),
        CompareStartDate = new DateTime(2024, 6, 20), CompareEndDate = new DateTime(2024, 8, 10),
    };

    /// <summary>三店、跨整月与边缘日的事实：含尾随空格门店码、利润全空与部分为空的商品，供分店范围和多选供应商对照。</summary>
    private static async Task SeedScopedFactsAsync(SalesDetailSqlServerFixture fixture)
    {
        foreach (var (code, name) in new[] { ("B1", "一店"), ("B2", "二店"), ("B3", "三店") }) await fixture.SeedStoreAsync(code, name);
        await fixture.SeedChinaSupplierAsync("C1", "国内一");
        await fixture.SeedChinaSupplierAsync("C2", "国内二");
        await fixture.SeedLocalSupplierAsync("200", "HB仓库");
        await fixture.SeedLocalSupplierAsync("A1", "澳洲一");
        foreach (var code in new[] { "P-ONE", "P-TWO", "P-THREE", "P-FOUR", "P-SIX" })
            await fixture.SeedProductAsync(code, $"商品{code}", $"Product {code}", $"IT-{code}");
        await fixture.SeedMappingAsync("P-ONE", "C1");
        await fixture.SeedMappingAsync("P-FOUR", "C2");
        var days = new[] { 20, 30 }.Select(d => new DateTime(2025, 6, d))
            .Concat(new[] { 1, 15, 31 }.Select(d => new DateTime(2025, 7, d)))
            .Concat(new[] { 2, 10 }.Select(d => new DateTime(2025, 8, d))).ToList();
        var seed = 0;
        foreach (var day in days.Concat(days.Select(d => d.AddYears(-1))))
        {
            await fixture.SeedFreshStateAsync(day);
            foreach (var branch in new[] { "B1", "B2", "B3" })
            {
                seed++;
                // B2 的一部分事实带尾随空格，与授权/选中门店码比较时应视为同一家店。
                var raw = branch == "B2" && day.Day % 2 == 0 ? "B2 " : branch;
                await fixture.SeedFactAsync(day, raw, "200", "P-ONE", 1 + seed % 5, 10m + seed, totalCost: 4m, grossProfit: 6m);
                await fixture.SeedFactAsync(day, raw, "C1", "P-TWO", 2 + seed % 3, 20m + seed, grossProfit: null, useAmountAsDefaultGrossProfit: false);
                if (branch != "B3") await fixture.SeedFactAsync(day, raw, "A1", "P-THREE", 3, 30m + seed);
                if (branch == "B1") await fixture.SeedFactAsync(day, raw, "200", "P-FOUR", 1, 40m + seed);
                // P-SIX：B1 利润全空、B2/B3 有利润，范围只含 B1 时减法必须得到 NULL 而不是 0。
                await fixture.SeedFactAsync(day, raw, "A1", "P-SIX", 1, 5m + seed, grossProfit: branch == "B1" ? null : 2m, useAmountAsDefaultGrossProfit: false);
            }
        }
    }

    [SalesDetailReportSqlServerFact]
    public async Task 分店范围选中分店与多选供应商走月投影与原查询一致且减法与直接读取结果相同()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await SeedScopedFactsAsync(fixture);
        await fixture.EnableMonthlyProjectionAsync();
        await fixture.CatchUpProjectionAsync();
        var noCompare = new DateRangeDto { StartDate = ScopedRange.StartDate, EndDate = ScopedRange.EndDate };
        var scopes = new (string[]? Branches, string? Selected)[]
        {
            (null, "B1"), (null, "B2"), (new[] { "B1" }, null), (new[] { "B1", "B2" }, null), (new[] { "B1", "B2" }, "B2"), (new[] { "B2", "B3" }, null),
        };
        async Task AssertEquivalentAsync(string label)
        {
            foreach (var kind in new[] { SalesDetailKind.Australia, SalesDetailKind.China })
            foreach (var range in new[] { ScopedRange, noCompare })
            foreach (var (branches, selected) in scopes)
            foreach (var suppliers in new[] { Array.Empty<string>(), kind == SalesDetailKind.China ? new[] { "C1", "C2" } : new[] { "200", "A1" } })
            foreach (var product in new string?[] { null, "P-ONE" })
            {
                var raw = await fixture.ReadFilteredReportAsync(SalesDetailSqlServerFixture.ReportSqlMode.Raw, range, kind, branches, selected, suppliers, selectedProduct: product);
                // 分别强制走"范围内逐日读取"与"全部减去范围外"两条分支。
                foreach (var threshold in new[] { long.MaxValue / 4, long.MinValue / 4 })
                {
                    var monthly = await fixture.ReadFilteredReportAsync(SalesDetailSqlServerFixture.ReportSqlMode.Monthly, range, kind, branches, selected, suppliers,
                        selectedProduct: product, complementThreshold: threshold);
                    Assert.True(raw.Json == monthly.Json,
                        $"{label} kind={kind} compare={range.CompareStartDate.HasValue} branches={string.Join(",", branches ?? Array.Empty<string>())} selected={selected} suppliers={string.Join(",", suppliers)} product={product} threshold={threshold}\n原查询: {raw.Json}\n月投影: {monthly.Json}");
                }
            }
        }
        await AssertEquivalentAsync("全部月份有效");
        // 映射变化后分店粒度退回日事实、商品粒度按新映射解析，两条分支仍须与原查询一致。
        await fixture.ChangeMappingAsync("P-ONE", "C2");
        await AssertEquivalentAsync("映射变化未重算");
        // 统计失败日：原查询逐行排除，月投影避开含失败日的月份，范围内逐日读取与"全部减范围外"两边也都不含它。
        await fixture.SetPublishStatusAsync(new DateTime(2025, 7, 15), "Failed");
        await fixture.SetPublishStatusAsync(new DateTime(2024, 6, 30), "Failed");
        await AssertEquivalentAsync("含统计失败日");
    }

    [SalesDetailReportSqlServerFact]
    public async Task 分类筛选先预取分类商品事实与直接扫描事实表的七个结果集一致()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedStoreAsync("B1", "一店");
        await fixture.SeedStoreAsync("B2", "二店");
        await fixture.SeedChinaSupplierAsync("C1", "国内一");
        await fixture.SeedLocalSupplierAsync("A1", "澳洲一");
        await fixture.SeedLocalSupplierAsync("A2", "澳洲二");
        await fixture.SeedCategorizedProductAsync("P-CAT", "分类商品", "A1", null, "CAT-A");
        await fixture.SeedCategorizedProductAsync("P-OTHER", "未分类商品", "A1", null);
        await fixture.SeedCategorizedProductAsync("P-WH", "仓库分类商品", "200", "WC-1");
        await fixture.SeedCategorizedProductAsync("P-WH2", "无供应商仓库商品", null, "WC-1");
        await fixture.SeedCategorizedProductAsync("P-NOWH", "其他仓库分类", "200", "WC-2");
        await fixture.SeedMappingAsync("P-WH", "C1");
        foreach (var day in new[] { SeedDate, CompareDate })
        {
            await fixture.SeedFreshStateAsync(day);
            foreach (var branch in new[] { "B1", "B2" })
            {
                // B2 的事实商品码带尾随空格：按商品索引预取时仍须与分类商品码匹配。
                await fixture.SeedFactAsync(day, branch, "A1", branch == "B2" ? "P-CAT " : "P-CAT", 2, 20m, "CAT 商品");
                await fixture.SeedFactAsync(day, branch, "A2", "P-CAT", 1, 9m, "CAT 其他供应商");
                await fixture.SeedFactAsync(day, branch, "A1", "P-OTHER", 3, 30m, "其他");
                await fixture.SeedFactAsync(day, branch, "200", "P-WH", 4, 40m, "仓库 CAT");
                await fixture.SeedFactAsync(day, branch, "A2", "P-WH", 1, 11m, "仓库非200");
                await fixture.SeedFactAsync(day, branch, "C1", "P-WH2", 2, 22m, "国内原始");
                await fixture.SeedFactAsync(day, branch, "200", "P-NOWH", 5, 50m, "别的仓库分类");
            }
        }
        var filters = new (string[] Supplier, string[] Warehouse)[]
        {
            (new[] { "CAT-A" }, Array.Empty<string>()), (Array.Empty<string>(), new[] { "WC-1" }),
            (new[] { "CAT-A" }, new[] { "WC-1" }), (new[] { "__sales_detail_no_matching_category__" }, Array.Empty<string>()),
        };
        foreach (var kind in new[] { SalesDetailKind.Australia, SalesDetailKind.China })
        foreach (var (supplierCategories, warehouseCategories) in filters)
        foreach (var search in new string?[] { null, "CAT" })
        foreach (var (branches, selected) in new (string[]? Branches, string? Selected)[] { (null, null), (new[] { "B1" }, null), (null, "B2") })
        {
            var raw = await fixture.ReadFilteredReportAsync(SalesDetailSqlServerFixture.ReportSqlMode.Raw, Range(SeedDate, CompareDate), kind, branches, selected,
                supplierCategories: supplierCategories, warehouseCategories: warehouseCategories, search: search);
            var prefetch = await fixture.ReadFilteredReportAsync(SalesDetailSqlServerFixture.ReportSqlMode.CategoryPrefetch, Range(SeedDate, CompareDate), kind, branches, selected,
                supplierCategories: supplierCategories, warehouseCategories: warehouseCategories, search: search);
            Assert.True(raw.Json == prefetch.Json,
                $"kind={kind} supplier={string.Join(",", supplierCategories)} warehouse={string.Join(",", warehouseCategories)} search={search} branches={string.Join(",", branches ?? Array.Empty<string>())} selected={selected}\n原查询: {raw.Json}\n预取: {prefetch.Json}");
        }
    }

    [SalesDetailReportSqlServerFact]
    public async Task 关键词加授权分店选中分店或多选供应商时日投影路径与原查询一致()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await SeedScopedFactsAsync(fixture);
        // 生产每天都有发布状态；无销售的日期也补上状态与空投影，关键词路径才不会把它们当作缺投影跳过。
        var factDays = new[] { 20, 30 }.Select(d => new DateTime(2025, 6, d))
            .Concat(new[] { 1, 15, 31 }.Select(d => new DateTime(2025, 7, d)))
            .Concat(new[] { 2, 10 }.Select(d => new DateTime(2025, 8, d))).ToList();
        factDays.AddRange(factDays.Select(d => d.AddYears(-1)).ToList());
        var allDays = new[] { (ScopedRange.StartDate, ScopedRange.EndDate), (ScopedRange.CompareStartDate!.Value, ScopedRange.CompareEndDate!.Value) }
            .SelectMany(p => Enumerable.Range(0, (p.Item2 - p.Item1).Days + 1).Select(offset => p.Item1.AddDays(offset))).ToList();
        foreach (var day in allDays.Except(factDays)) await fixture.SeedFreshStateAsync(day);
        await fixture.EnableProjectionAsync(allDays.ToArray());
        await fixture.EnableMonthlyProjectionAsync();
        await fixture.CatchUpProjectionAsync();
        async Task AssertEquivalentAsync(string label)
        {
            foreach (var kind in new[] { SalesDetailKind.Australia, SalesDetailKind.China })
            foreach (var (branches, selected) in new (string[]? Branches, string? Selected)[] { (null, null), (new[] { "B1" }, null), (new[] { "B1", "B2" }, "B2"), (null, "B3") })
            foreach (var suppliers in new[] { Array.Empty<string>(), kind == SalesDetailKind.China ? new[] { "C1", "C2" } : new[] { "200", "A1" } })
            foreach (var search in new[] { "P-ONE", "商品P" })
            {
                var raw = await fixture.ReadFilteredReportAsync(SalesDetailSqlServerFixture.ReportSqlMode.Raw, ScopedRange, kind, branches, selected, suppliers, search: search);
                var projected = await fixture.ReadFilteredReportAsync(SalesDetailSqlServerFixture.ReportSqlMode.Projected, ScopedRange, kind, branches, selected, suppliers, search: search);
                Assert.Empty(projected.Skipped);
                Assert.True(raw.Json == projected.Json,
                    $"{label} kind={kind} branches={string.Join(",", branches ?? Array.Empty<string>())} selected={selected} suppliers={string.Join(",", suppliers)} search={search}\n原查询: {raw.Json}\n日投影: {projected.Json}");
            }
        }
        await AssertEquivalentAsync("全部日期有效");
        // 统计失败日在守卫前整体删除，不进投影跳过清单；关键词结果与月投影三栏都排除它，与原查询一致。
        await fixture.SetPublishStatusAsync(new DateTime(2025, 7, 15), "Failed");
        await AssertEquivalentAsync("含统计失败日");
    }

    [SalesDetailReportSqlServerFact]
    public async Task 月投影表缺失时查询批次抛出51014供调用方回退()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await fixture.SeedFreshStateAsync(SeedDate);
        var error = await Assert.ThrowsAsync<SqlException>(() =>
            fixture.ReadUnscopedReportAsync(Range(), SalesDetailKind.Australia, null, null, monthly: true));
        Assert.Equal(SalesDetailQueryMonthlyProjection.MissingSchemaErrorNumber, error.Number);
    }

    private static DateRangeDto Range() => Range(SeedDate, null);

    private static DateRangeDto Range(DateTime currentDate, DateTime? compareDate)
        => new()
        {
            StartDate = currentDate, EndDate = currentDate,
            CompareStartDate = compareDate, CompareEndDate = compareDate,
        };

    [SalesDetailReportSqlServerFact]
    public async Task 紧凑看板在统计快照内聚合_重算中的日期读上一版且不被写锁阻塞()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        var firstDay = SeedDate;
        var secondDay = SeedDate.AddDays(1);
        await fixture.SeedFreshStateAsync(firstDay);
        await fixture.SeedFreshStateAsync(secondDay);
        await fixture.SeedStoreAsync("B1", "分店一");
        await fixture.SeedChinaSupplierAsync("C1", "国内供应商一");
        await fixture.SeedChinaSupplierAsync("C2", "国内供应商二");
        await fixture.SeedProductAsync("P-MAP", "映射商品", itemNumber: "IT-MAP");
        await fixture.SeedProductAsync("P-DIRECT", "直写商品", itemNumber: "IT-DIRECT");
        // 同编码的软删除旧资料不能覆盖在用资料。
        await fixture.SeedProductAsync("P-DIRECT", "已删除的旧资料", itemNumber: "IT-OLD", uuid: "deleted-direct");
        await fixture.MarkProductDeletedAsync("deleted-direct");
        await fixture.SeedMappingAsync("P-MAP", "C1");
        await fixture.SeedFactAsync(firstDay, "B1", "200", "P-MAP", 2, 20m);
        await fixture.SeedFactAsync(secondDay, "B1", "200", "P-MAP", 1, 10m);
        await fixture.SeedFactAsync(secondDay, "B1", "C2", "P-DIRECT", 3, 30m);
        // 澳洲供应商行不属于国内编码族。
        await fixture.SeedFactAsync(firstDay, "B1", "105", "P-MAP", 9, 90m);

        // 第二天开始重算：状态置为 Running，另一个连接删掉当天事实但尚未提交（与日统计整日替换相同）。
        await fixture.BeginNextPublishAsync(secondDay, SalesStatisticRefreshStatus.Running);
        await using var pendingRewrite = await fixture.BeginUncommittedFactDeleteAsync(secondDay);
        var boardTask = fixture.CreateService().GetCompactSalesBoardAsync(new CompactSalesBoardQuery
        {
            DateRange = new DateRangeDto { StartDate = firstDay, EndDate = secondDay },
        });
        // 快照读取不取共享锁：若退回已提交读会被写锁挡住，这里用超时把阻塞变成明确失败。
        Assert.Same(boardTask, await Task.WhenAny(boardTask, Task.Delay(TimeSpan.FromSeconds(15))));
        var result = await boardTask;

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        Assert.Equal(DateTimeKind.Utc, result.StatisticUpdatedAt!.Value.Kind);
        // 未提交的删除不可见：读到的是上一版已发布的完整事实。
        Assert.Equal(60m, result.Summary.TotalAmount);
        Assert.Equal(6, result.Summary.TotalQuantity);
        Assert.Equal(new[] { ("C1", 30m), ("C2", 30m) },
            result.ChinaSuppliers.Select(row => (row.SupplierCode, row.TotalAmount)).OrderBy(row => row.SupplierCode));
        var mapped = result.ProductDetails.Data.Single(row => row.ProductCode == "P-MAP");
        Assert.Equal(("IT-MAP", "映射商品", "C1"), (mapped.ItemNumber, mapped.ProductName, mapped.ChinaSupplierCode));
        var direct = result.ProductDetails.Data.Single(row => row.ProductCode == "P-DIRECT");
        Assert.Equal(("IT-DIRECT", "直写商品", "C2"), (direct.ItemNumber, direct.ProductName, direct.ChinaSupplierCode));
    }

    [SalesDetailReportSqlServerFact]
    public async Task 紧凑看板按月分桶一次补读缺失分片_跨区间复用且与强制刷新结果一致()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        var days = new[] { new DateTime(2026, 7, 30), new DateTime(2026, 7, 31), new DateTime(2026, 8, 1), new DateTime(2026, 8, 15), new DateTime(2026, 9, 1) };
        // 区间内每天都要有状态，否则最新状态之后的日期按未发布处理。
        for (var day = days[0]; day <= days[^1]; day = day.AddDays(1))
            await fixture.SeedFreshStateAsync(day);
        await fixture.SeedStoreAsync("B1", "分店一");
        await fixture.SeedChinaSupplierAsync("C1", "国内供应商一");
        await fixture.SeedChinaSupplierAsync("C2", "国内供应商二");
        await fixture.SeedProductAsync("P-MAP", "映射商品", itemNumber: "IT-MAP");
        await fixture.SeedProductAsync("P-DIRECT", "直写商品", itemNumber: "IT-DIRECT");
        await fixture.SeedMappingAsync("P-MAP", "C1");
        await fixture.SeedFactAsync(days[0], "B1", "200", "P-MAP", 1, 10m);
        await fixture.SeedFactAsync(days[1], "B1", "200", "P-MAP", 1, 10m);
        await fixture.SeedFactAsync(days[2], "B1", "200", "P-MAP", 2, 20m);
        await fixture.SeedFactAsync(days[3], "B1", "C2", "P-DIRECT", 3, 30m);
        await fixture.SeedFactAsync(days[4], "B1", "200", "P-MAP", 1, 5m);
        var service = fixture.CreateService();
        CompactSalesBoardQuery Query(DateTime start, DateTime end, bool force = false) =>
            new() { DateRange = new DateRangeDto { StartDate = start, EndDate = end }, ForceRefresh = force };

        // 7-30～8-31：7 月不满月分片 + 8 月整月分片，一条按月分桶的语句读回。
        var julyAugust = await service.GetCompactSalesBoardAsync(Query(days[0], new DateTime(2026, 8, 31)));
        // 8-01～9-01：8 月分片复用，只补读 9 月 1 日。
        var augustSeptember = await service.GetCompactSalesBoardAsync(Query(days[2], days[4]));
        var forced = await service.GetCompactSalesBoardAsync(Query(days[2], days[4], force: true));

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, julyAugust.StatisticStatus);
        Assert.Equal((70m, 7), (julyAugust.Summary.TotalAmount, julyAugust.Summary.TotalQuantity));
        Assert.Equal((55m, 6), (augustSeptember.Summary.TotalAmount, augustSeptember.Summary.TotalQuantity));
        Assert.Equal(
            forced.ProductDetails.Data.Select(row => (row.ProductCode, row.TotalAmount, row.TotalQuantity, row.ChinaSupplierCode)),
            augustSeptember.ProductDetails.Data.Select(row => (row.ProductCode, row.TotalAmount, row.TotalQuantity, row.ChinaSupplierCode)));
        Assert.Equal(("C1", 25m), augustSeptember.ChinaSuppliers.Where(row => row.SupplierCode == "C1").Select(row => (row.SupplierCode, row.TotalAmount)).Single());
        // 同一门店×商品跨两个月合并为一格。
        Assert.Equal(2, Assert.Single(augustSeptember.Stores).ProductCount);
    }

    [SalesDetailReportSqlServerFact]
    public async Task 紧凑看板数据库端聚合与内存立方体逐项一致_含月表可用与身份失效()
    {
        await using var fixture = await SalesDetailSqlServerFixture.CreateAsync();
        await SeedCompactBoardEquivalenceAsync(fixture);
        var server = fixture.CreateService();
        var memory = fixture.CreateService();
        memory.ForceCompactBoardInMemory = true;
        var start = new DateTime(2026, 6, 10);
        var end = new DateTime(2026, 8, 20);
        var july = new DateTime(2026, 7, 1);

        async Task AssertAllEquivalentAsync(string stage)
        {
            foreach (var (name, query) in CompactBoardQueryMatrix(start, end))
            {
                query.ForceRefresh = true;
                var expected = CompactBoardSnapshot(await memory.GetCompactSalesBoardAsync(query));
                var actual = CompactBoardSnapshot(await server.GetCompactSalesBoardAsync(query));
                Assert.True(expected == actual, $"{stage} / {name}\nexpected: {expected}\nactual:   {actual}");
            }
        }

        // 月表未部署：整段读日事实。
        await AssertAllEquivalentAsync("无月表");

        // 分店栏占比分母：区间内分店总营业额；选中供应商只收窄分子；尾随空格的代码合并；无统计的分店为 0。
        var totals = (await server.GetCompactSalesBoardAsync(new CompactSalesBoardQuery { DateRange = new DateRangeDto { StartDate = start, EndDate = end }, SelectedChinaSupplierCode = "C1", ForceRefresh = true }))
            .Stores.ToDictionary(row => row.BranchCode, row => row.BranchTotalAmount);
        Assert.Equal(3000m, totals["B1"]);
        Assert.Equal(750m, totals["B2"]);
        Assert.Equal(0m, totals["B3"]);

        // 月表部署后 7 月整月可用（6 月、8 月是不满月，仍读日事实）。
        await fixture.EnableCompactBoardMonthlyAsync();
        var stale = await fixture.ReadCompactBoardStaleMonthsAsync();
        // 待办从最早状态所在月列到当前月、最近的在前。
        Assert.Equal(new[] { new DateTime(2026, 8, 1), july, new DateTime(2026, 6, 1) }, stale.Where(month => month <= new DateTime(2026, 8, 1)));
        foreach (var month in stale)
            await fixture.RefreshCompactBoardMonthAsync(month);
        Assert.Empty(await fixture.ReadCompactBoardStaleMonthsAsync());
        await AssertAllEquivalentAsync("月表可用");

        // 篡改 7 月月表：只有真的读了月表，整月区间的结果才会随之变化。
        var julyQuery = new CompactSalesBoardQuery { DateRange = new DateRangeDto { StartDate = july, EndDate = new DateTime(2026, 7, 31) }, ForceRefresh = true };
        var julyBaseline = (await server.GetCompactSalesBoardAsync(julyQuery)).Summary.OverallAmount;
        await fixture.TamperCompactBoardMonthAsync(july, 1000m);
        var tampered = (await server.GetCompactSalesBoardAsync(julyQuery)).Summary.OverallAmount;
        Assert.True(tampered > julyBaseline, $"7 月整月应读月表：{julyBaseline} → {tampered}");

        // 7 月某日重新发布（身份变化）：该月不再可用，退回日事实，结果回到与内存立方体一致。
        await fixture.TouchPublishAsync(new DateTime(2026, 7, 15));
        Assert.Equal(julyBaseline, (await server.GetCompactSalesBoardAsync(julyQuery)).Summary.OverallAmount);
        Assert.Contains(july, await fixture.ReadCompactBoardStaleMonthsAsync());
        await AssertAllEquivalentAsync("月表身份失效");

        // worker 追上后再次可用，结果仍一致。
        await fixture.RefreshCompactBoardMonthAsync(july);
        await AssertAllEquivalentAsync("月表重建后");
    }

    private static IEnumerable<(string Name, CompactSalesBoardQuery Query)> CompactBoardQueryMatrix(DateTime start, DateTime end)
    {
        CompactSalesBoardQuery Q(Action<CompactSalesBoardQuery>? configure = null, DateTime? from = null, DateTime? to = null)
        {
            var query = new CompactSalesBoardQuery { DateRange = new DateRangeDto { StartDate = from ?? start, EndDate = to ?? end }, PageSize = 20 };
            configure?.Invoke(query);
            return query;
        }
        yield return ("默认", Q());
        yield return ("第 2 页", Q(q => q.PageIndex = 2));
        yield return ("数量降序", Q(q => { q.SortField = "quantity"; q.SortOrder = "desc"; }));
        yield return ("单价升序", Q(q => { q.SortField = "unitPrice"; q.SortOrder = "asc"; }));
        yield return ("货号默认", Q(q => q.SortField = "itemNumber"));
        yield return ("货号降序", Q(q => { q.SortField = "itemNumber"; q.SortOrder = "desc"; }));
        yield return ("选中分店", Q(q => q.SelectedBranchCode = "B2"));
        yield return ("选中供应商", Q(q => q.SelectedChinaSupplierCode = "C1"));
        yield return ("选中已删供应商", Q(q => q.SelectedChinaSupplierCode = "C3"));
        yield return ("选中商品", Q(q => q.SelectedProductCode = "P20"));
        yield return ("分店加供应商", Q(q => { q.SelectedBranchCode = "B1"; q.SelectedChinaSupplierCode = "C2"; }));
        yield return ("三者全选", Q(q => { q.SelectedBranchCode = "B1"; q.SelectedChinaSupplierCode = "C1"; q.SelectedProductCode = "P03"; }));
        yield return ("关键词", Q(q => q.Keyword = " widget  1 "));
        yield return ("关键词无命中", Q(q => q.Keyword = "no-such-product"));
        yield return ("授权范围", Q(q => q.BranchCodes = new List<string> { "B1", "B3" }));
        yield return ("授权范围外选中", Q(q => { q.BranchCodes = new List<string> { "B1" }; q.SelectedBranchCode = "B2"; }));
        yield return ("不存在的选中项", Q(q => q.SelectedBranchCode = "B9"));
        yield return ("整月", Q(from: new DateTime(2026, 7, 1), to: new DateTime(2026, 7, 31)));
        yield return ("单日", Q(from: new DateTime(2026, 7, 5), to: new DateTime(2026, 7, 5)));
    }

    /// <summary>把看板结果规整成可比较的文本：金额统一到 4 位小数，避免 decimal 尾零差异。</summary>
    private static string CompactBoardSnapshot(CompactSalesBoardDto board)
    {
        static string M(decimal value) => decimal.Round(value, 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        return JsonSerializer.Serialize(new
        {
            board.StatisticStatus,
            Summary = new { A = M(board.Summary.TotalAmount), board.Summary.TotalQuantity, board.Summary.ProductCount, board.Summary.StoreCount, board.Summary.SupplierCount, O = M(board.Summary.OverallAmount), board.Summary.OverallQuantity },
            Stores = board.Stores.Select(s => new { s.BranchCode, s.BranchName, A = M(s.TotalAmount), T = M(s.BranchTotalAmount), s.TotalQuantity, s.ProductCount }),
            Suppliers = board.ChinaSuppliers.Select(s => new { s.SupplierCode, s.SupplierName, A = M(s.TotalAmount), s.TotalQuantity, s.ProductCount }),
            board.ProductDetails.Total,
            Scope = M(board.ProductDetails.ScopeAmount),
            Products = board.ProductDetails.Data.Select(p => new { p.ProductCode, p.ItemNumber, p.ProductName, p.ChinaSupplierCode, p.ChinaSupplierName, p.TotalQuantity, A = M(p.TotalAmount), U = M(p.UnitPrice) }),
        });
    }

    /// <summary>
    /// 等价性夹具：2026-06-01～08-31 每天 Fresh；三家分店（B3 无分店资料）、三个国内供应商（C3 已软删除）、24 个商品。
    /// 覆盖映射行、直写行、未映射 200 行与澳洲行（不计入）、归属中途变更、同日直写优先、无商品资料、同额并列。
    /// </summary>
    private static async Task SeedCompactBoardEquivalenceAsync(SalesDetailSqlServerFixture fixture)
    {
        for (var day = new DateTime(2026, 6, 1); day <= new DateTime(2026, 8, 31); day = day.AddDays(1))
            await fixture.SeedFreshStateAsync(day);
        await fixture.SeedStoreAsync("B1", "分店一");
        await fixture.SeedStoreAsync("B2", "分店二");
        await fixture.SeedChinaSupplierAsync("C1", "供应商一");
        await fixture.SeedChinaSupplierAsync("C2", "供应商二");
        await fixture.SeedChinaSupplierAsync("C3", "已删供应商", isDeleted: true);
        for (var i = 1; i <= 24; i++)
        {
            var code = $"P{i:D2}";
            // P24 没有商品资料：名称、货号为空，货号排序退回商品编码。
            if (i != 24)
                await fixture.SeedProductAsync(code, $"Widget {i:D2}", itemNumber: $"IT-{25 - i:D2}");
            if (i <= 12 || i is 20 or 21 or 23 or 24)
                await fixture.SeedMappingAsync(code, i is 23 or 24 ? "C2" : "C1");
        }
        await fixture.SeedProductAsync("P03", "已删除的旧资料", itemNumber: "IT-OLD", uuid: "p03-deleted");
        await fixture.MarkProductDeletedAsync("p03-deleted");

        var branches = new[] { "B1", "B2", "B3" };
        var dates = new[] { new DateTime(2026, 6, 5), new DateTime(2026, 6, 12), new DateTime(2026, 7, 3), new DateTime(2026, 7, 20), new DateTime(2026, 8, 8), new DateTime(2026, 8, 18) };
        for (var i = 1; i <= 18; i++)
        {
            var code = $"P{i:D2}";
            var supplier = i <= 12 ? "200" : "C2";
            for (var d = 0; d < dates.Length; d++)
            {
                var branch = branches[(i + d) % 3];
                // 金额制造并列：每 5 个商品一档，同档靠商品编码定序。
                await fixture.SeedFactAsync(dates[d], branch, supplier, code, 1 + (i + d) % 4, 10m * (1 + i / 5) + d);
            }
        }
        // P19：未映射的 200 行，不计入。P22：澳洲供应商行，不计入。
        await fixture.SeedFactAsync(dates[2], "B1", "200", "P19", 5, 500m);
        await fixture.SeedFactAsync(dates[2], "B1", "105", "P22", 5, 500m);
        // P20：6 月走映射归 C1，8 月直写 C2；整段归最近的 C2，区间只含 6 月时仍归 C1。
        await fixture.SeedFactAsync(dates[1], "B1", "200", "P20", 2, 40m);
        await fixture.SeedFactAsync(dates[4], "B2", "C2", "P20", 3, 60m);
        // P21：同一天既有映射（C1）又有直写（C3，已软删除）：直写优先。
        await fixture.SeedFactAsync(dates[3], "B1", "200", "P21", 1, 25m);
        await fixture.SeedFactAsync(dates[3], "B3", "C3", "P21", 2, 35m);
        // P23、P24：映射到 C2；P24 无商品资料。
        await fixture.SeedFactAsync(dates[3], "B2", "200", "P23", 4, 44m);
        await fixture.SeedFactAsync(dates[5], "B3", "200", "P24", 3, 33m);
        // 分店总营业额（占比分母）：6-05 在默认区间（6-10 起）之外；B2 的代码带尾随空格，应与 B2 合并。
        await fixture.SeedStoreRevenueAsync(new DateTime(2026, 6, 5), "B1", 700m);
        await fixture.SeedStoreRevenueAsync(new DateTime(2026, 6, 12), "B1", 1000m);
        await fixture.SeedStoreRevenueAsync(new DateTime(2026, 7, 20), "B1", 2000m);
        await fixture.SeedStoreRevenueAsync(new DateTime(2026, 7, 3), "B2", 500m);
        await fixture.SeedStoreRevenueAsync(new DateTime(2026, 8, 8), "B2 ", 250m);
    }

    private sealed class SalesDetailSqlServerFixture : IAsyncDisposable
    {
        private readonly string _masterConnectionString;
        private readonly string _databaseName;
        private readonly string _databaseConnectionString;
        private readonly SqlSugarClient _db;
        private readonly SqlSugarClient _posmDb;
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());

        private SalesDetailSqlServerFixture(string masterConnectionString, string databaseName, string databaseConnectionString)
        {
            _masterConnectionString = masterConnectionString;
            _databaseName = databaseName;
            _databaseConnectionString = databaseConnectionString;
            _db = new SqlSugarClient(CreateConnectionConfig(databaseConnectionString));
            _posmDb = new SqlSugarClient(CreateConnectionConfig(databaseConnectionString));
        }

        public static async Task<SalesDetailSqlServerFixture> CreateAsync()
        {
            var baseConnectionString = Environment.GetEnvironmentVariable(SqlServerTestConnectionEnvVar);
            if (string.IsNullOrWhiteSpace(baseConnectionString))
                throw new InvalidOperationException($"未配置 {SqlServerTestConnectionEnvVar}。");
            EnsureLoopbackSqlServer(baseConnectionString);

            var databaseName = $"HbSalesDetail_{Guid.NewGuid():N}";
            var masterConnectionString = BuildConnectionString(baseConnectionString, "master");
            var databaseConnectionString = BuildConnectionString(baseConnectionString, databaseName);
            await ExecuteNonQueryAsync(masterConnectionString, $"CREATE DATABASE {QuoteSqlServerName(databaseName)};");
            try
            {
                await ExecuteNonQueryAsync(databaseConnectionString, "ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;");
                await ExecuteNonQueryAsync(databaseConnectionString, SchemaSql);
                return new SalesDetailSqlServerFixture(masterConnectionString, databaseName, databaseConnectionString);
            }
            catch
            {
                await DropDatabaseAsync(masterConnectionString, databaseName);
                throw;
            }
        }

        public SalesDashboardReactService CreateService()
        {
            return new SalesDashboardReactService(
                CreateSqlSugarContext(_db), CreatePosmSqlSugarContext(_posmDb), Mock.Of<IMapper>(),
                NullLogger<SalesDashboardReactService>.Instance, _cache);
        }

        // 所有测试数据都使用参数写入，避免把测试输入拼接进 SQL。
        public Task SeedFreshStateAsync(DateTime date) => ExecuteNonQueryAsync(_databaseConnectionString, """
            INSERT INTO [dbo].[SalesStatisticRefreshState]
                ([StatisticType], [Date], [Status], [LastAggregatedAtUtc], [CompletedAtUtc], [SourceProductVersion], [JobId])
            VALUES (N'ProductStoreDaily', @date, N'Fresh', SYSUTCDATETIME(), SYSUTCDATETIME(), N'test-source-v1', NEWID());
            """, ("@date", date));

        public async Task EnableProjectionAsync(params DateTime[] dates)
        {
            await ExecuteNonQueryAsync(_databaseConnectionString, SalesDetailQueryProjection.CreateSchemaSql);
            foreach (var date in dates) await RefreshProjectionAsync(date);
        }

        public async Task RefreshProjectionAsync(DateTime date)
        {
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            await using var command = new SqlCommand(SalesDetailQueryProjection.BuildRefreshDaySql(_databaseName), connection, transaction);
            command.Parameters.AddWithValue("@sdpDate", date);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        public Task ChangeStatisticNameAsync(DateTime date, string name) => ExecuteNonQueryAsync(_databaseConnectionString, """
            BEGIN TRANSACTION;
            UPDATE dbo.ProductStoreDailySalesStatistic SET ProductName=@name WHERE [Date]=@date;
            UPDATE dbo.SalesStatisticRefreshState SET JobId=NEWID(),LastAggregatedAtUtc=SYSUTCDATETIME(),CompletedAtUtc=SYSUTCDATETIME()
            WHERE [Date]=@date AND StatisticType='ProductStoreDaily';
            COMMIT TRANSACTION;
            """, ("@date", date), ("@name", name));

        public Task SetPublishStatusAsync(DateTime date, string status) => ExecuteNonQueryAsync(_databaseConnectionString,
            "UPDATE dbo.SalesStatisticRefreshState SET Status=@status,CompletedAtUtc=DATEADD(second,1,CompletedAtUtc) WHERE [Date]=@date AND StatisticType='ProductStoreDaily';",
            ("@date", date), ("@status", status));

        public Task SetPublishIdentityAsync(DateTime date, bool missingAggregation) => ExecuteNonQueryAsync(_databaseConnectionString,
            "UPDATE dbo.SalesStatisticRefreshState SET JobId=NULL,LastAggregatedAtUtc=CASE WHEN @missing=1 THEN NULL ELSE LastAggregatedAtUtc END WHERE [Date]=@date AND StatisticType='ProductStoreDaily';",
            ("@date", date), ("@missing", missingAggregation));

        public Task BeginNextPublishAsync(DateTime date, string status) => ExecuteNonQueryAsync(_databaseConnectionString,
            "UPDATE dbo.SalesStatisticRefreshState SET JobId=NEWID(),Status=@status WHERE [Date]=@date AND StatisticType='ProductStoreDaily';",
            ("@date", date), ("@status", status));

        public Task SetFailedPublishedStateAsync(DateTime date, Guid jobId, DateTime lastCheckedAtUtc) =>
            ExecuteNonQueryAsync(_databaseConnectionString, """
                UPDATE dbo.SalesStatisticRefreshState
                SET Status=N'Failed', SourceProductVersion=NULL, JobId=@jobId, LastCheckedAtUtc=@lastCheckedAtUtc
                WHERE [Date]=@date AND StatisticType=N'ProductStoreDaily';
                """, ("@date", date), ("@jobId", jobId), ("@lastCheckedAtUtc", lastCheckedAtUtc));

        public Task AdvanceFailedLastCheckedAsync(DateTime date) => ExecuteNonQueryAsync(_databaseConnectionString, """
            UPDATE dbo.SalesStatisticRefreshState
            SET LastCheckedAtUtc=DATEADD(second, 1, LastCheckedAtUtc)
            WHERE [Date]=@date AND StatisticType=N'ProductStoreDaily';
            """, ("@date", date));

        public Task RemoveLastAggregationAsync(DateTime date) => ExecuteNonQueryAsync(_databaseConnectionString, """
            UPDATE dbo.SalesStatisticRefreshState
            SET LastAggregatedAtUtc=NULL
            WHERE [Date]=@date AND StatisticType=N'ProductStoreDaily';
            """, ("@date", date));

        public Task DeleteProjectionStateAsync(DateTime date) => ExecuteNonQueryAsync(_databaseConnectionString,
            "DELETE FROM dbo.SalesDetailQueryProjectionState WHERE [Date]=@date;",
            ("@date", date));

        public Task ChangeMappingAsync(string product, string supplier) => ExecuteNonQueryAsync(_databaseConnectionString,
            "UPDATE dbo.posm_product_supplier_mapping SET ChinaSupplierCode=@supplier WHERE ProductCode=@product AND LocalSupplierCode='200';",
            ("@product", product), ("@supplier", supplier));

        public async Task<string> ReadRawReportAsync(DateRangeDto range, SalesDetailKind kind, string search, string? selectedProduct, bool projected)
            => (await ReadRawReportWithSkippedAsync(range, kind, search, selectedProduct, projected)).Json;

        /// <summary>读取七个栏位结果集；日投影路径多出的第八个结果集是守卫跳过的日期，单独返回。</summary>
        public async Task<(string Json, List<DateTime> Skipped)> ReadRawReportWithSkippedAsync(DateRangeDto range, SalesDetailKind kind,
            string search, string? selectedProduct, bool projected, bool allBranches = false)
        {
            var builder = typeof(SalesDashboardReactService).GetMethod("BuildSalesDetailReportSqlServerCore", BindingFlags.Static | BindingFlags.NonPublic)!;
            var sql = (string)builder.Invoke(null, new object?[] { _databaseName, range, kind, allBranches ? null : new[] { "B1" }, null, null,
                selectedProduct, search, 1, 20, Enum.GetValues<SalesDetailSection>().ToHashSet(), null, projected, false })!;
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("SET NOCOUNT ON;SET TRANSACTION ISOLATION LEVEL SNAPSHOT;BEGIN TRANSACTION;" + sql + ";COMMIT TRANSACTION;", connection);
            command.Parameters.AddWithValue("@sdrCurrentStart", range.StartDate);
            command.Parameters.AddWithValue("@sdrCurrentEnd", range.EndDate.AddDays(1));
            command.Parameters.AddWithValue("@sdrCompareStart", (object?)range.CompareStartDate ?? DBNull.Value);
            command.Parameters.AddWithValue("@sdrCompareEnd", (object?)range.CompareEndDate?.AddDays(1) ?? DBNull.Value);
            command.Parameters.AddWithValue("@sdrHasCompare", range.CompareStartDate.HasValue ? 1 : 0);
            command.Parameters.AddWithValue("@sdrKind", kind == SalesDetailKind.China ? 1 : 0);
            if (!allBranches)
            {
                command.Parameters.AddWithValue("@sdrBranch0", "B1");
                command.Parameters.AddWithValue("@sdrSelectedBranch0", "B1");
            }
            if (selectedProduct != null) command.Parameters.AddWithValue("@sdrSelectedProduct", selectedProduct);
            var tokens = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length; i++) command.Parameters.AddWithValue($"@sdrSearch{i}", $"%{tokens[i]}%");
            var sets = new List<List<object?[]>>();
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                if (reader.FieldCount == 0) continue;
                var rows = new List<object?[]>();
                while (await reader.ReadAsync())
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    // 投影使用更宽的 decimal 精度；比较数值本身，不把无意义的小数尾零视为差异。
                    rows.Add(values.Select(value => value switch
                    {
                        DBNull => null,
                        decimal number => (object)decimal.Parse(number.ToString("G29", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture),
                        _ => value,
                    }).ToArray());
                }
                sets.Add(rows);
            } while (await reader.NextResultAsync());
            Assert.Equal(projected ? 8 : 7, sets.Count);
            var skipped = projected ? sets[7].Select(row => ((DateTime)row[0]!).Date).ToList() : new List<DateTime>();
            return (JsonSerializer.Serialize(sets.Take(7)), skipped);
        }


        public Task EnableMonthlyProjectionAsync() => ExecuteNonQueryAsync(_databaseConnectionString, SalesDetailQueryMonthlyProjection.CreateSchemaSql);

        public async Task RefreshMonthlyAsync(DateTime month)
        {
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            await SalesDetailMonthlyProjectionWorker.RefreshMonthAsync(connection, _databaseName, month, CancellationToken.None);
        }

        public async Task RefreshDailyAsync(DateTime day)
        {
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            await SalesDetailMonthlyProjectionWorker.RefreshDayAsync(connection, _databaseName, day, CancellationToken.None);
        }

        public async Task<List<DateTime>> ReadStaleDaysAsync(int maxDays = 1000)
        {
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            return await SalesDetailMonthlyProjectionWorker.ReadStaleDaysAsync(connection, _databaseName, maxDays, CancellationToken.None);
        }

        public async Task<List<DateTime>> ReadStaleMonthsAsync()
        {
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            return await SalesDetailMonthlyProjectionWorker.ReadStaleMonthsAsync(connection, _databaseName, CancellationToken.None);
        }

        /// <summary>模拟 worker 一轮：先逐日重算待办日期，再汇总待办月份。</summary>
        public async Task CatchUpProjectionAsync()
        {
            foreach (var day in await ReadStaleDaysAsync()) await RefreshDailyAsync(day);
            foreach (var month in await ReadStaleMonthsAsync()) await RefreshMonthlyAsync(month);
            Assert.Empty(await ReadStaleDaysAsync());
            Assert.Empty(await ReadStaleMonthsAsync());
        }

        /// <summary>全部分店范围（管理员）下读取原查询或月投影的七个结果集。</summary>
        public async Task<string> ReadUnscopedReportAsync(DateRangeDto range, SalesDetailKind kind, string? selectedSupplier,
            string? selectedProduct, bool monthly, params SalesDetailSection[] sections)
        {
            var wanted = sections.Length == 0 ? Enum.GetValues<SalesDetailSection>().ToHashSet() : sections.ToHashSet();
            string sql;
            if (monthly)
                sql = SalesDashboardReactService.BuildSalesDetailReportSqlMonthly(_databaseName, range, kind, selectedSupplier, selectedProduct, 1, 20, wanted);
            else
            {
                var builder = typeof(SalesDashboardReactService).GetMethod("BuildSalesDetailReportSqlServerCore", BindingFlags.Static | BindingFlags.NonPublic)!;
                sql = (string)builder.Invoke(null, new object?[] { _databaseName, range, kind, null, null, selectedSupplier,
                    selectedProduct, null, 1, 20, wanted, null, false, false })!;
            }
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("SET NOCOUNT ON;SET TRANSACTION ISOLATION LEVEL SNAPSHOT;BEGIN TRANSACTION;" + sql + ";COMMIT TRANSACTION;", connection);
            command.Parameters.AddWithValue("@sdrCurrentStart", range.StartDate);
            command.Parameters.AddWithValue("@sdrCurrentEnd", range.EndDate.AddDays(1));
            command.Parameters.AddWithValue("@sdrCompareStart", (object?)range.CompareStartDate ?? DBNull.Value);
            command.Parameters.AddWithValue("@sdrCompareEnd", (object?)range.CompareEndDate?.AddDays(1) ?? DBNull.Value);
            command.Parameters.AddWithValue("@sdrHasCompare", range.CompareStartDate.HasValue ? 1 : 0);
            command.Parameters.AddWithValue("@sdrKind", kind == SalesDetailKind.China ? 1 : 0);
            if (selectedSupplier != null) command.Parameters.AddWithValue("@sdrSelectedSupplier", selectedSupplier);
            if (selectedProduct != null) command.Parameters.AddWithValue("@sdrSelectedProduct", selectedProduct);
            return await ReadResultSetsAsync(command);
        }

        private static async Task<string> ReadResultSetsAsync(SqlCommand command)
        {
            var sets = await ReadNormalizedResultSetsAsync(command);
            Assert.Equal(7, sets.Count);
            return JsonSerializer.Serialize(sets);
        }

        private static async Task<List<List<object?[]>>> ReadNormalizedResultSetsAsync(SqlCommand command)
        {
            var sets = new List<List<object?[]>>();
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                if (reader.FieldCount == 0) continue;
                var rows = new List<object?[]>();
                while (await reader.ReadAsync())
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    // 月表的 bigint/decimal(38,4) 与原查询的 int/decimal(18,2) 只比较数值本身。
                    rows.Add(values.Select(value => value switch
                    {
                        DBNull => null,
                        decimal number => (object)decimal.Parse(number.ToString("G29", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture),
                        long number => (object)number,
                        int number => (object)(long)number,
                        _ => value,
                    }).ToArray());
                }
                sets.Add(rows);
            } while (await reader.NextResultAsync());
            return sets;
        }

        public Task SeedStoreAsync(string code, string name) => ExecuteNonQueryAsync(_databaseConnectionString,
            "INSERT INTO [dbo].[Store] ([StoreCode], [StoreName], [IsActive], [IsDeleted]) VALUES (@code, @name, 1, 0);",
            ("@code", code), ("@name", name));

        public Task SeedStoreRevenueAsync(DateTime date, string branchCode, decimal amount) => ExecuteNonQueryAsync(_databaseConnectionString,
            "INSERT INTO [dbo].[StoreSalesStatistic] ([Date], [BranchCode], [BranchName], [TotalAmount]) VALUES (@date, @code, @code, @amount);",
            ("@date", date), ("@code", branchCode), ("@amount", amount));

        public Task SeedChinaSupplierAsync(string code, string name, bool isDeleted = false) => ExecuteNonQueryAsync(_databaseConnectionString,
            "INSERT INTO [dbo].[ChinaSupplier] ([SupplierCode], [SupplierName], [IsDeleted]) VALUES (@code, @name, @deleted);",
            ("@code", code), ("@name", name), ("@deleted", isDeleted));

        public Task EnableCompactBoardMonthlyAsync() =>
            ExecuteNonQueryAsync(_databaseConnectionString, BlazorApp.Api.Data.SchemaMigrations.CompactBoardMonthlySchema.ApplySql);

        public async Task RefreshCompactBoardMonthAsync(DateTime month)
        {
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            await CompactBoardMonthlyProjectionWorker.RefreshMonthAsync(connection, month, CancellationToken.None);
        }

        public async Task<List<DateTime>> ReadCompactBoardStaleMonthsAsync()
        {
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            return await CompactBoardMonthlyProjectionWorker.ReadStaleMonthsAsync(connection, 100, CancellationToken.None);
        }

        /// <summary>直接改月表金额，用来证明查询确实读了月表（事实表不变）。</summary>
        public Task TamperCompactBoardMonthAsync(DateTime month, decimal delta) => ExecuteNonQueryAsync(_databaseConnectionString,
            "UPDATE [dbo].[CompactBoardMonthlyCell] SET [Amount] = [Amount] + @delta WHERE [Month] = @month;",
            ("@delta", delta), ("@month", month));

        /// <summary>只推进某日的聚合时间（事实不变），让该月身份失效。</summary>
        public Task TouchPublishAsync(DateTime date) => ExecuteNonQueryAsync(_databaseConnectionString,
            "UPDATE dbo.SalesStatisticRefreshState SET LastAggregatedAtUtc = DATEADD(second, 7, LastAggregatedAtUtc) WHERE [Date] = @date AND StatisticType = 'ProductStoreDaily';",
            ("@date", date));

        public Task SeedLocalSupplierAsync(string code, string name) => ExecuteNonQueryAsync(_databaseConnectionString,
            "INSERT INTO [dbo].[LocalSupplier] ([LocalSupplierCode], [Name], [IsDeleted]) VALUES (@code, @name, 0);",
            ("@code", code), ("@name", name));

        /// <summary>污染销售明细月投影金额，用来证明查询是否仍读取旧月表。</summary>
        public Task TamperSalesDetailMonthAsync(DateTime month, decimal delta) => ExecuteNonQueryAsync(_databaseConnectionString, """
            UPDATE [dbo].[SalesDetailQueryMonthlyProduct]
            SET [Revenue] = [Revenue] + @delta
            WHERE [Month] = @month;
            UPDATE [dbo].[SalesDetailQueryMonthlyBranch]
            SET [Revenue] = [Revenue] + @delta
            WHERE [Month] = @month;
            """, ("@month", month), ("@delta", delta));

        public Task SeedCategorizedProductAsync(string code, string name, string? localSupplierCode, string? warehouseCategory, string? supplierCategory = null)
            => ExecuteNonQueryAsync(_databaseConnectionString, """
                INSERT INTO [dbo].[Product] ([UUID], [ProductCode], [ProductName], [LocalSupplierCode], [WarehouseCategoryGUID])
                VALUES (@uuid, @code, @name, @supplier, @warehouse);
                IF @category IS NOT NULL
                    INSERT INTO [dbo].[LocalSupplierCategoryProductAssignment] ([ProductCode], [LocalSupplierCode], [CategoryGUID])
                    VALUES (@code, @supplier, @category);
                """, ("@uuid", $"product-{Guid.NewGuid():N}"), ("@code", code), ("@name", name), ("@supplier", localSupplierCode),
                ("@warehouse", warehouseCategory), ("@category", supplierCategory));

        public enum ReportSqlMode { Raw, CategoryPrefetch, Monthly, Projected }

        /// <summary>
        /// 按服务同样的参数约定生成并执行某条读取路径的 SQL，返回七个栏位结果集与（日投影路径的）跳过日期。
        /// Raw 是不做任何预取、直接扫描事实表的原查询，用作其余路径的对照基准。
        /// </summary>
        public async Task<(string Json, List<DateTime> Skipped)> ReadFilteredReportAsync(ReportSqlMode mode, DateRangeDto range, SalesDetailKind kind,
            string[]? branches = null, string? selectedBranch = null, string[]? suppliers = null, string[]? supplierCategories = null,
            string[]? warehouseCategories = null, string? selectedProduct = null, string? search = null, long? complementThreshold = null)
        {
            var wanted = Enum.GetValues<SalesDetailSection>().ToHashSet();
            var supplierList = suppliers ?? Array.Empty<string>();
            string sql;
            if (mode == ReportSqlMode.Monthly)
                sql = SalesDashboardReactService.BuildSalesDetailReportSqlMonthly(_databaseName, range, kind, supplierList.FirstOrDefault(), selectedProduct, 1, 20,
                    wanted, supplierList, branches, selectedBranch, complementThreshold ?? SalesDashboardReactService.SalesDetailScopeComplementThresholdRows);
            else
            {
                var builder = typeof(SalesDashboardReactService).GetMethod("BuildSalesDetailReportSqlServerCoreWithFilters", BindingFlags.Static | BindingFlags.NonPublic)!;
                sql = (string)builder.Invoke(null, new object?[] { _databaseName, range, kind, branches, selectedBranch, supplierList.FirstOrDefault(),
                    selectedProduct, search, 1, 20, wanted, null, mode == ReportSqlMode.Projected, false, supplierList,
                    supplierCategories ?? Array.Empty<string>(), warehouseCategories ?? Array.Empty<string>(), mode == ReportSqlMode.CategoryPrefetch })!;
            }
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("SET NOCOUNT ON;SET TRANSACTION ISOLATION LEVEL SNAPSHOT;BEGIN TRANSACTION;" + sql + ";COMMIT TRANSACTION;", connection);
            command.Parameters.AddWithValue("@sdrCurrentStart", range.StartDate);
            command.Parameters.AddWithValue("@sdrCurrentEnd", range.EndDate.AddDays(1));
            command.Parameters.AddWithValue("@sdrCompareStart", (object?)range.CompareStartDate ?? DBNull.Value);
            command.Parameters.AddWithValue("@sdrCompareEnd", (object?)range.CompareEndDate?.AddDays(1) ?? DBNull.Value);
            command.Parameters.AddWithValue("@sdrHasCompare", range.CompareStartDate.HasValue ? 1 : 0);
            command.Parameters.AddWithValue("@sdrKind", kind == SalesDetailKind.China ? 1 : 0);
            if (selectedBranch != null) command.Parameters.AddWithValue("@sdrSelectedBranch", selectedBranch);
            if (supplierList.Length > 0) command.Parameters.AddWithValue("@sdrSelectedSupplier", supplierList[0]);
            if (supplierList.Length > 1) for (var i = 0; i < supplierList.Length; i++) command.Parameters.AddWithValue($"@sdrSelectedSupplier{i}", supplierList[i]);
            for (var i = 0; i < (supplierCategories?.Length ?? 0); i++) command.Parameters.AddWithValue($"@sdrCategorySupplier{i}", supplierCategories![i]);
            for (var i = 0; i < (warehouseCategories?.Length ?? 0); i++) command.Parameters.AddWithValue($"@sdrCategoryWarehouse{i}", warehouseCategories![i]);
            if (selectedProduct != null) command.Parameters.AddWithValue("@sdrSelectedProduct", selectedProduct);
            var tokens = search?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            for (var i = 0; i < tokens.Length; i++) command.Parameters.AddWithValue($"@sdrSearch{i}", $"%{tokens[i]}%");
            for (var i = 0; i < (branches?.Length ?? 0); i++)
            {
                command.Parameters.AddWithValue($"@sdrBranch{i}", branches![i]);
                command.Parameters.AddWithValue($"@sdrSelectedBranch{i}", branches[i]);
            }
            var sets = await ReadNormalizedResultSetsAsync(command);
            Assert.Equal(mode == ReportSqlMode.Projected ? 8 : 7, sets.Count);
            var skipped = mode == ReportSqlMode.Projected ? sets[7].Select(row => ((DateTime)row[0]!).Date).ToList() : new List<DateTime>();
            return (JsonSerializer.Serialize(sets.Take(7)), skipped);
        }

        public Task SeedProductAsync(string code, string name, string? englishName = null, string? itemNumber = null, string? uuid = null)
            => ExecuteNonQueryAsync(_databaseConnectionString, """
                INSERT INTO [dbo].[Product]
                    ([UUID], [ProductCode], [ProductName], [EnglishName], [ItemNumber], [Barcode], [LocalSupplierCode], [ProductImage])
                VALUES (@uuid, @code, @name, @englishName, @itemNumber, NULL, NULL, NULL);
                """, ("@uuid", uuid ?? $"product-{Guid.NewGuid():N}"), ("@code", code), ("@name", name),
                ("@englishName", englishName), ("@itemNumber", itemNumber));

        public Task MarkProductDeletedAsync(string uuid) => ExecuteNonQueryAsync(_databaseConnectionString,
            "UPDATE [dbo].[Product] SET [IsDeleted] = 1 WHERE [UUID] = @uuid;", ("@uuid", uuid));

        /// <summary>在独立连接上删除某日事实但不提交，模拟日统计整日替换进行中；释放时回滚。</summary>
        public async Task<IAsyncDisposable> BeginUncommittedFactDeleteAsync(DateTime date)
        {
            var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            await using (var command = new SqlCommand("DELETE FROM [dbo].[ProductStoreDailySalesStatistic] WHERE [Date] = @date;", connection, transaction))
            {
                command.Parameters.AddWithValue("@date", date);
                await command.ExecuteNonQueryAsync();
            }
            return new PendingWrite(connection, transaction);
        }

        private sealed class PendingWrite(SqlConnection connection, SqlTransaction transaction) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await transaction.RollbackAsync();
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
            }
        }

        public Task SeedMappingAsync(string productCode, string chinaSupplierCode) => ExecuteNonQueryAsync(_databaseConnectionString,
            "INSERT INTO [dbo].[posm_product_supplier_mapping] ([ProductCode], [LocalSupplierCode], [ChinaSupplierCode], [IsDeleted]) VALUES (@product, N'200', @china, 0);",
            ("@product", productCode), ("@china", chinaSupplierCode));

        public Task SeedFactAsync(DateTime date, string branch, string supplier, string product, int quantity, decimal amount,
            string? productName = null, string? barcode = null, decimal? totalCost = null, decimal? grossProfit = null,
            bool useAmountAsDefaultGrossProfit = true)
            => ExecuteNonQueryAsync(_databaseConnectionString, """
                INSERT INTO [dbo].[ProductStoreDailySalesStatistic]
                    ([Date], [BranchCode], [SupplierCode], [ProductCode], [ProductName], [Barcode], [TotalQuantity], [TotalAmount], [OrderCount], [TotalCost], [GrossProfit], [CostSource], [UpdateTime])
                VALUES (@date, @branch, @supplier, @product, @productName, @barcode, @quantity, @amount, 1, @totalCost, @grossProfit, N'Test', SYSUTCDATETIME());
                """, ("@date", date), ("@branch", branch), ("@supplier", supplier), ("@product", product),
                ("@productName", productName), ("@barcode", barcode), ("@quantity", quantity), ("@amount", amount),
                ("@totalCost", totalCost), ("@grossProfit", useAmountAsDefaultGrossProfit ? grossProfit ?? amount : grossProfit));

        public async ValueTask DisposeAsync()
        {
            _cache.Dispose();
            _db.Dispose();
            _posmDb.Dispose();
            await DropDatabaseAsync(_masterConnectionString, _databaseName);
        }

        private static readonly string SchemaSql = """
            SET NOCOUNT ON;
            CREATE TABLE [dbo].[Store] (
                [StoreCode] nvarchar(50) NOT NULL PRIMARY KEY,
                [StoreName] nvarchar(100) NOT NULL,
                [IsActive] bit NOT NULL,
                [IsDeleted] bit NOT NULL
            );
            CREATE TABLE [dbo].[LocalSupplier] (
                [LocalSupplierCode] nvarchar(64) NOT NULL PRIMARY KEY,
                [Name] nvarchar(128) NOT NULL,
                [IsDeleted] bit NOT NULL
            );
            CREATE TABLE [dbo].[ChinaSupplier] (
                [SupplierCode] nvarchar(50) NULL,
                [SupplierName] nvarchar(200) NULL,
                [IsDeleted] bit NOT NULL CONSTRAINT [DF_ChinaSupplier_IsDeleted] DEFAULT (0)
            );
            CREATE TABLE [dbo].[Product] (
                [UUID] nvarchar(50) NOT NULL PRIMARY KEY,
                [ProductCode] nvarchar(50) NULL,
                [ProductName] nvarchar(200) NULL,
                [EnglishName] nvarchar(200) NULL,
                [ItemNumber] nvarchar(50) NULL,
                [Barcode] nvarchar(50) NULL,
                [LocalSupplierCode] nvarchar(50) NULL,
                [ProductImage] nvarchar(200) NULL,
                [WarehouseCategoryGUID] nvarchar(100) NULL,
                [IsDeleted] bit NOT NULL CONSTRAINT [DF_Product_IsDeleted] DEFAULT (0)
            );
            CREATE TABLE [dbo].[LocalSupplierCategoryProductAssignment] (
                [ProductCode] nvarchar(100) NOT NULL,
                [LocalSupplierCode] nvarchar(128) NOT NULL,
                [CategoryGUID] nvarchar(100) NOT NULL,
                CONSTRAINT [PK_LocalSupplierCategoryProductAssignment] PRIMARY KEY ([ProductCode], [LocalSupplierCode])
            );
            CREATE TABLE [dbo].[posm_product_supplier_mapping] (
                [ProductCode] nvarchar(50) NOT NULL,
                [LocalSupplierCode] nvarchar(50) NOT NULL,
                [ChinaSupplierCode] nvarchar(50) NULL,
                [IsDeleted] bit NOT NULL
            );
            CREATE TABLE [dbo].[ProductStoreDailySalesStatistic] (
                [Date] datetime2(7) NOT NULL,
                [BranchCode] nvarchar(50) NOT NULL,
                [SupplierCode] nvarchar(50) NOT NULL,
                [ProductCode] nvarchar(50) NOT NULL,
                [ProductName] nvarchar(255) NULL,
                [Barcode] nvarchar(100) NULL,
                [TotalQuantity] int NOT NULL,
                [TotalAmount] decimal(18,2) NOT NULL,
                [OrderCount] int NOT NULL,
                [TotalCost] decimal(18,2) NULL,
                [GrossProfit] decimal(18,2) NULL,
                [CostSource] nvarchar(50) NOT NULL,
                [UpdateTime] datetime2(7) NOT NULL,
                CONSTRAINT [PK_ProductStoreDailySalesStatistic] PRIMARY KEY ([Date], [BranchCode], [SupplierCode], [ProductCode])
            );
            CREATE TABLE [dbo].[StoreSalesStatistic] (
                [Date] datetime2(7) NOT NULL,
                [BranchCode] nvarchar(50) NOT NULL,
                [BranchName] nvarchar(100) NOT NULL,
                [TotalAmount] decimal(18,2) NOT NULL,
                CONSTRAINT [PK_StoreSalesStatistic] PRIMARY KEY ([Date], [BranchCode])
            );
            CREATE TABLE [dbo].[SalesStatisticRefreshState] (
                [StatisticType] nvarchar(80) NOT NULL,
                [Date] datetime2(7) NOT NULL,
                [Status] nvarchar(20) NOT NULL,
                [LastAggregatedAtUtc] datetime2(7) NULL,
                [CompletedAtUtc] datetime2(7) NULL,
                [SourceProductVersion] nvarchar(64) NULL,
                [JobId] uniqueidentifier NULL,
                [LastCheckedAtUtc] datetime2(7) NULL,
                CONSTRAINT [PK_SalesStatisticRefreshState] PRIMARY KEY ([StatisticType], [Date])
            );
            """;

        private static ConnectionConfig CreateConnectionConfig(string connectionString) => new()
        {
            ConnectionString = connectionString, DbType = DbType.SqlServer,
            IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute,
        };

        private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
        {
            var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
            typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, db);
            return context;
        }

        private static POSMSqlSugarContext CreatePosmSqlSugarContext(ISqlSugarClient db)
        {
            var context = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(POSMSqlSugarContext));
            typeof(POSMSqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, db);
            return context;
        }

        private static async Task ExecuteNonQueryAsync(string connectionString, string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        private static string BuildConnectionString(string connectionString, string databaseName)
        {
            var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = databaseName };
            return builder.ConnectionString;
        }

        private static void EnsureLoopbackSqlServer(string connectionString)
        {
            var dataSource = new SqlConnectionStringBuilder(connectionString).DataSource.Trim();
            if (dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) dataSource = dataSource[4..];
            var parts = dataSource.Split(',', 2, StringSplitOptions.TrimEntries);
            var host = parts[0].Trim().Trim('[', ']');
            if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && !host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{SqlServerTestConnectionEnvVar} 必须指向 localhost 或 127.0.0.1。");
            if (parts.Length == 2 && (!int.TryParse(parts[1], out var port) || port is < 1 or > 65535))
                throw new InvalidOperationException($"{SqlServerTestConnectionEnvVar} 的 SQL Server 端口无效。");
        }

        private static async Task DropDatabaseAsync(string masterConnectionString, string databaseName)
        {
            var quotedName = QuoteSqlServerName(databaseName);
            await ExecuteNonQueryAsync(masterConnectionString, $"""
                IF DB_ID(N'{databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE {quotedName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE {quotedName};
                END;
                """);
        }

        private static string QuoteSqlServerName(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
    }
}
