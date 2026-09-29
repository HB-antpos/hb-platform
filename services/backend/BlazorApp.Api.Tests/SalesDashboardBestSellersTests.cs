using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using AutoMapper;
using BlazorApp.Api.Cache;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Background;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class SalesDashboardBestSellersTests : IDisposable
{
    private readonly string _localDbPath;
    private readonly string _posmDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _posmConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarClient _posmDb;

    public SalesDashboardBestSellersTests()
    {
        _localDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _posmDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _localConnection = new SqliteConnection($"Data Source={_localDbPath}");
        _posmConnection = new SqliteConnection($"Data Source={_posmDbPath}");
        _localConnection.Open();
        _posmConnection.Open();

        _localDb = new SqlSugarClient(CreateConnectionConfig(_localConnection.ConnectionString));
        _posmDb = new SqlSugarClient(CreateConnectionConfig(_posmConnection.ConnectionString));

        _localDb.CodeFirst.InitTables(
            typeof(Product),
            typeof(WarehouseProduct),
            // 直写开关打开时，读取器按「仓库商品 -> 国内商品」解析国内供应商。
            typeof(DomesticProduct),
            typeof(Store),
            typeof(StoreRetailPrice),
            typeof(StoreSalesStatistic),
            typeof(StoreSupplierSalesDetail),
            // 商品日统计会原子派生供应商汇总，测试库需包含完整的读写表。
            typeof(HBLocalSupplier),
            typeof(AustralianSupplierStoreSalesDetail),
            typeof(ChinaSupplierStoreSalesDetail),
            typeof(ProductStoreDailySalesStatistic),
            typeof(SalesStatisticRefreshState),
            typeof(ChinaSupplier)
        );
        CreateScheduledTaskLogTable(_localDb);
        _posmDb.CodeFirst.InitTables(
            typeof(SalesOrder),
            typeof(SalesOrderDetail),
            typeof(SalesReturnRecord),
            typeof(PaymentDetail),
            typeof(POSM_设备注册信息表),
            typeof(PosmProductSupplierMapping)
        );
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_选中供应商时联动分店与商品()
    {
        var date = new DateTime(2026, 8, 1);
        await SeedStatisticStateAsync(date, SalesStatisticRefreshStatus.Fresh);
        await SeedStoreAsync("S-COMPACT", "紧凑分店");
        await SeedProductAsync("P-COMPACT", "I-COMPACT", "B-COMPACT", "紧凑商品", true, true, 1);
        await _localDb.Insertable(new ChinaSupplier { Guid = "cn-compact", SupplierCode = "CN-COMPACT", SupplierName = "紧凑供应商" }).ExecuteCommandAsync();
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = date, BranchCode = "S-COMPACT", SupplierCode = "200", ProductCode = "P-COMPACT",
            ProductName = "统计商品", TotalQuantity = 4, TotalAmount = 40m, OrderCount = 1,
        }).ExecuteCommandAsync();
        await _posmDb.Insertable(new PosmProductSupplierMapping
        {
            ProductCode = "P-COMPACT", LocalSupplierCode = "200", ChinaSupplierCode = "CN-COMPACT",
        }).ExecuteCommandAsync();

        var service = CreateService();
        var supplierFirstMap = await service.GetChinaSupplierProductMapAsync(new[] { "CN-COMPACT" });
        Assert.Equal("CN-COMPACT", supplierFirstMap["P-COMPACT"]);
        Assert.Equal(1, await _localDb.Queryable<ProductStoreDailySalesStatistic>()
            .Where(row => row.Date >= date.Date && row.Date < date.Date.AddDays(1) && row.SupplierCode == "200" && row.ProductCode == "P-COMPACT")
            .CountAsync());

        var result = await service.GetCompactSalesBoardAsync(new CompactSalesBoardQuery
        {
            DateRange = new DateRangeDto { StartDate = date, EndDate = date },
            SelectedChinaSupplierCode = "CN-COMPACT",
        });

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        Assert.Equal(40m, Assert.Single(result.Stores).TotalAmount);
        Assert.Equal("CN-COMPACT", Assert.Single(result.ChinaSuppliers).SupplierCode);
        var product = Assert.Single(result.ProductDetails.Data);
        Assert.Equal("P-COMPACT", product.ProductCode);
        Assert.Equal("I-COMPACT", product.ItemNumber);
        Assert.Equal("紧凑商品", product.ProductName);
        Assert.Equal("紧凑供应商", product.ChinaSupplierName);
        Assert.Equal(10m, product.UnitPrice);
        Assert.Equal(40m, result.Summary.TotalAmount);
        Assert.Equal(1, result.Summary.ProductCount);
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_分店栏带区间总营业额作占比分母_选中供应商只收窄分子()
    {
        var date = new DateTime(2026, 8, 1);
        await SeedStatisticStateAsync(date, SalesStatisticRefreshStatus.Fresh);
        await SeedStoreAsync("S-SHARE-A", "占比分店A");
        await SeedStoreAsync("S-SHARE-B", "占比分店B");
        await SeedProductAsync("P-SHARE-1", "I-SHARE-1", "B-SHARE-1", "占比商品1", true, true, 1);
        await SeedProductAsync("P-SHARE-2", "I-SHARE-2", "B-SHARE-2", "占比商品2", true, true, 1);
        await _localDb.Insertable(new[]
        {
            new ChinaSupplier { Guid = "cn-share-a", SupplierCode = "CN-SHARE-A", SupplierName = "占比供应商A" },
            new ChinaSupplier { Guid = "cn-share-b", SupplierCode = "CN-SHARE-B", SupplierName = "占比供应商B" },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new[]
        {
            new ProductStoreDailySalesStatistic { Date = date, BranchCode = "S-SHARE-A", SupplierCode = "200", ProductCode = "P-SHARE-1", ProductName = "占比商品1", TotalQuantity = 4, TotalAmount = 40m, OrderCount = 1 },
            new ProductStoreDailySalesStatistic { Date = date, BranchCode = "S-SHARE-A", SupplierCode = "200", ProductCode = "P-SHARE-2", ProductName = "占比商品2", TotalQuantity = 6, TotalAmount = 60m, OrderCount = 1 },
            new ProductStoreDailySalesStatistic { Date = date, BranchCode = "S-SHARE-B", SupplierCode = "200", ProductCode = "P-SHARE-1", ProductName = "占比商品1", TotalQuantity = 3, TotalAmount = 30m, OrderCount = 1 },
        }).ExecuteCommandAsync();
        await _posmDb.Insertable(new[]
        {
            new PosmProductSupplierMapping { ProductCode = "P-SHARE-1", LocalSupplierCode = "200", ChinaSupplierCode = "CN-SHARE-A" },
            new PosmProductSupplierMapping { ProductCode = "P-SHARE-2", LocalSupplierCode = "200", ChinaSupplierCode = "CN-SHARE-B" },
        }).ExecuteCommandAsync();
        // 分店总营业额：区间外的日期、没有国内商品销售的分店都不应计入。
        await _localDb.Insertable(new[]
        {
            new StoreSalesStatistic { Date = date, BranchCode = "S-SHARE-A", BranchName = "占比分店A", TotalAmount = 400m },
            new StoreSalesStatistic { Date = date.AddDays(-1), BranchCode = "S-SHARE-A", BranchName = "占比分店A", TotalAmount = 999m },
            new StoreSalesStatistic { Date = date, BranchCode = "S-SHARE-B", BranchName = "占比分店B", TotalAmount = 300m },
            new StoreSalesStatistic { Date = date, BranchCode = "S-SHARE-C", BranchName = "占比分店C", TotalAmount = 500m },
        }).ExecuteCommandAsync();

        var service = CreateService();
        var range = new DateRangeDto { StartDate = date, EndDate = date };
        var all = await service.GetCompactSalesBoardAsync(new CompactSalesBoardQuery { DateRange = range });
        var storeA = Assert.Single(all.Stores, row => row.BranchCode == "S-SHARE-A");
        var storeB = Assert.Single(all.Stores, row => row.BranchCode == "S-SHARE-B");
        Assert.Equal(100m, storeA.TotalAmount);
        Assert.Equal(400m, storeA.BranchTotalAmount);
        Assert.Equal(30m, storeB.TotalAmount);
        Assert.Equal(300m, storeB.BranchTotalAmount);
        Assert.Equal(2, all.Stores.Count);

        // 选中供应商只收窄分子（国内商品金额），分母仍是分店总营业额。
        var filtered = await service.GetCompactSalesBoardAsync(new CompactSalesBoardQuery { DateRange = range, SelectedChinaSupplierCode = "CN-SHARE-A" });
        var filteredA = Assert.Single(filtered.Stores, row => row.BranchCode == "S-SHARE-A");
        Assert.Equal(40m, filteredA.TotalAmount);
        Assert.Equal(400m, filteredA.BranchTotalAmount);
    }

    [Fact]
    public async Task GetStatisticsFreshnessAsync_跳过或失败不推进最后完整发布快照时间()
    {
        var publishedAt = DateTime.SpecifyKind(
            SalesStatisticsBusinessDate.Today().AddHours(5),
            DateTimeKind.Utc
        );
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.RevenueReportPublished,
            Date = SalesStatisticsBusinessDate.Today(),
            Status = SalesStatisticRefreshStatus.Fresh,
            LastAggregatedAtUtc = publishedAt,
            CompletedAtUtc = publishedAt,
            LastCheckedAtUtc = publishedAt,
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ScheduledTaskLog
        {
            TaskType = TaskType.UpdateCurrentHourStatistics,
            Status = BlazorApp.Shared.Models.HBweb.TaskStatus.Skipped,
            StartedAt = publishedAt.AddHours(1),
            CompletedAt = publishedAt.AddHours(1),
            ScheduledTime = publishedAt.AddHours(1),
        }).ExecuteCommandAsync();

        var freshness = await CreateService().GetStatisticsFreshnessAsync();

        Assert.Equal(publishedAt, freshness.LastSuccessfulAtUtc);
        Assert.Equal(BlazorApp.Shared.Models.HBweb.TaskStatus.Skipped, freshness.LatestRunStatus);
    }

    [Fact]
    public async Task GetStatisticsFreshnessAsync_缺少完整发布记录时不能由局部状态伪造新鲜时间()
    {
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.HourlySales,
            Date = new DateTime(2026, 9, 14),
            Status = SalesStatisticRefreshStatus.Fresh,
            LastAggregatedAtUtc = DateTime.UtcNow,
            CompletedAtUtc = DateTime.UtcNow,
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();

        var freshness = await CreateService().GetStatisticsFreshnessAsync();

        Assert.Null(freshness.LastSuccessfulAtUtc);
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_统计水位相同则命中缓存_forceRefresh绕过缓存()
    {
        var date = new DateTime(2026, 8, 2);
        await SeedStatisticStateAsync(date, SalesStatisticRefreshStatus.Fresh);
        await SeedStoreAsync("S-CACHE", "缓存分店");
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = date, BranchCode = "S-CACHE", SupplierCode = "200", ProductCode = "P-CACHE",
            ProductName = "缓存商品", TotalQuantity = 1, TotalAmount = 10m, OrderCount = 1,
        }).ExecuteCommandAsync();
        await _posmDb.Insertable(new PosmProductSupplierMapping
        {
            ProductCode = "P-CACHE", LocalSupplierCode = "200", ChinaSupplierCode = "CN-CACHE",
        }).ExecuteCommandAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);
        var first = await service.GetCompactSalesBoardAsync(BoardQuery(date));
        await _localDb.Updateable<ProductStoreDailySalesStatistic>()
            .SetColumns(row => row.TotalAmount == 90m)
            .SetColumns(row => row.TotalQuantity == 9)
            .Where(row => row.ProductCode == "P-CACHE")
            .ExecuteCommandAsync();

        // 不同筛选组合复用同一份立方体：选中分店也不会绕过缓存。
        var cached = await service.GetCompactSalesBoardAsync(BoardQuery(date, query => query.SelectedBranchCode = "S-CACHE"));
        var refreshed = await service.GetCompactSalesBoardAsync(BoardQuery(date, query => query.ForceRefresh = true));

        Assert.False(first.FromCache);
        Assert.True(cached.FromCache);
        Assert.False(refreshed.FromCache);
        Assert.Equal(10m, Assert.Single(first.Stores).TotalAmount);
        Assert.Equal(10m, Assert.Single(cached.Stores).TotalAmount);
        Assert.Equal(90m, Assert.Single(refreshed.Stores).TotalAmount);
        Assert.Equal(9, Assert.Single(refreshed.Stores).TotalQuantity);
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_区间上限与销售明细一致为731天()
    {
        var start = new DateTime(2025, 1, 1);
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => CreateService().GetCompactSalesBoardAsync(
            new CompactSalesBoardQuery { DateRange = new DateRangeDto { StartDate = start, EndDate = start.AddDays(731) } }));

        Assert.Contains("731", exception.Message);
        // 恰好 731 天（含闰日的两年）可以查询；没有统计状态时按未发布返回，不抛异常。
        var twoYears = await CreateService().GetCompactSalesBoardAsync(
            new CompactSalesBoardQuery { DateRange = new DateRangeDto { StartDate = start, EndDate = start.AddDays(730) } });
        Assert.NotEqual(SalesStatisticRefreshStatus.Fresh, twoYears.StatisticStatus);
    }

    [Fact]
    public void SplitCompactSalesBoardSegments_按自然月切片且首尾不满月单独成片()
    {
        var segments = SalesDashboardReactService.SplitCompactSalesBoardSegments(new DateTime(2026, 3, 25), new DateTime(2026, 5, 10));

        Assert.Equal(new[]
        {
            (new DateTime(2026, 3, 25), new DateTime(2026, 4, 1), false),
            (new DateTime(2026, 4, 1), new DateTime(2026, 5, 1), true),
            (new DateTime(2026, 5, 1), new DateTime(2026, 5, 11), false),
        }, segments.Select(segment => (segment.Start, segment.EndExclusive, segment.IsWholeMonth)));
        var singleDay = Assert.Single(SalesDashboardReactService.SplitCompactSalesBoardSegments(new DateTime(2026, 9, 24), new DateTime(2026, 9, 24)));
        Assert.Equal((new DateTime(2026, 9, 24), new DateTime(2026, 9, 25)), (singleDay.Start, singleDay.EndExclusive));
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_按月分片缓存_跨区间只补读缺的月份且结果与整段一致()
    {
        await SeedCompactMonthsFixtureAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);
        var statements = CaptureStatisticStatements();

        var julyAugust = await service.GetCompactSalesBoardAsync(RangeQuery(new DateTime(2026, 7, 1), new DateTime(2026, 8, 31)));
        var firstReads = statements.Count;
        var augustSeptember = await service.GetCompactSalesBoardAsync(RangeQuery(new DateTime(2026, 8, 1), new DateTime(2026, 9, 10)));
        var secondReads = statements.Count - firstReads;
        _localDb.Aop.OnLogExecuting = null;
        // 不共享缓存的新实例按整段读取，作为等价性基准。
        var baseline = await CreateService().GetCompactSalesBoardAsync(RangeQuery(new DateTime(2026, 8, 1), new DateTime(2026, 9, 10)));

        Assert.Equal(2, firstReads);
        // 8 月整月分片已缓存，只补读 9 月 1–10 日这一片。
        Assert.Equal(1, secondReads);
        Assert.Equal(30m, julyAugust.Summary.TotalAmount);
        Assert.Equal(50m, augustSeptember.Summary.TotalAmount);
        Assert.Equal(
            baseline.ProductDetails.Data.Select(row => (row.ProductCode, row.TotalAmount, row.TotalQuantity, row.ChinaSupplierCode)),
            augustSeptember.ProductDetails.Data.Select(row => (row.ProductCode, row.TotalAmount, row.TotalQuantity, row.ChinaSupplierCode)));
        Assert.Equal(baseline.Stores.Select(row => (row.BranchCode, row.TotalAmount, row.ProductCount)),
            augustSeptember.Stores.Select(row => (row.BranchCode, row.TotalAmount, row.ProductCount)));
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_某日重新发布只重建所在月份_仅状态变化不重建()
    {
        await SeedCompactMonthsFixtureAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);
        var julyAugust = RangeQuery(new DateTime(2026, 7, 1), new DateTime(2026, 8, 31));
        await service.GetCompactSalesBoardAsync(julyAugust);
        var statements = CaptureStatisticStatements();

        // 8-10 重新发布：事实改为 $25，聚合时间前进。
        await _localDb.Updateable<ProductStoreDailySalesStatistic>()
            .SetColumns(row => row.TotalAmount == 25m)
            .Where(row => row.Date == new DateTime(2026, 8, 10))
            .ExecuteCommandAsync();
        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => row.LastAggregatedAtUtc == DateTime.UtcNow.AddMinutes(5))
            .Where(row => row.Date == new DateTime(2026, 8, 10))
            .ExecuteCommandAsync();
        var republished = await service.GetCompactSalesBoardAsync(julyAugust);
        var republishReads = statements.Count;

        // 8-10 进入重算排队：状态变了但聚合时间与来源版本未变，快照读到的仍是同一版事实。
        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => row.Status == SalesStatisticRefreshStatus.Queued)
            .SetColumns(row => row.SourceProductVersion == "published-v1")
            .Where(row => row.Date == new DateTime(2026, 8, 10))
            .ExecuteCommandAsync();
        var queuedIdentityChanged = await service.GetCompactSalesBoardAsync(julyAugust);
        var queuedIdentityReads = statements.Count - republishReads;
        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => row.CompletedAtUtc == DateTime.UtcNow.AddMinutes(9))
            .Where(row => row.Date == new DateTime(2026, 8, 10))
            .ExecuteCommandAsync();
        var queued = await service.GetCompactSalesBoardAsync(julyAugust);
        var queuedReads = statements.Count - republishReads - queuedIdentityReads;
        _localDb.Aop.OnLogExecuting = null;

        // 只有 8 月重读，7 月沿用分片缓存。
        Assert.Equal(1, republishReads);
        Assert.Equal(35m, republished.Summary.TotalAmount);
        // 写入来源版本改变了 8 月身份，重建一次；之后仅状态、完成时间变化不再重读统计表。
        Assert.Equal(1, queuedIdentityReads);
        Assert.Equal(0, queuedReads);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, queued.StatisticStatus);
        Assert.Equal(35m, queued.Summary.TotalAmount);
        Assert.Equal(35m, queuedIdentityChanged.Summary.TotalAmount);
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_强制刷新连分片缓存一起绕过()
    {
        await SeedCompactMonthsFixtureAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);
        var query = RangeQuery(new DateTime(2026, 7, 1), new DateTime(2026, 9, 10));
        await service.GetCompactSalesBoardAsync(query);
        // 手工修正统计而不改状态：只有强制刷新能读到。
        await _localDb.Updateable<ProductStoreDailySalesStatistic>()
            .SetColumns(row => row.TotalAmount == 11m)
            .Where(row => row.Date == new DateTime(2026, 7, 10))
            .ExecuteCommandAsync();
        var statements = CaptureStatisticStatements();

        var cached = await service.GetCompactSalesBoardAsync(RangeQuery(new DateTime(2026, 7, 1), new DateTime(2026, 9, 10)));
        var cachedReads = statements.Count;
        query.ForceRefresh = true;
        var refreshed = await service.GetCompactSalesBoardAsync(query);
        _localDb.Aop.OnLogExecuting = null;

        Assert.Equal(0, cachedReads);
        Assert.Equal(60m, cached.Summary.TotalAmount);
        Assert.Equal(3, statements.Count);
        Assert.Equal(61m, refreshed.Summary.TotalAmount);
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_跨月合并同一门店商品只有一格且归属取最近销售日()
    {
        var july = new DateTime(2026, 7, 20);
        var august = new DateTime(2026, 8, 5);
        await SeedStatisticStateAsync(july, SalesStatisticRefreshStatus.Fresh);
        await SeedStatisticStateAsync(august, SalesStatisticRefreshStatus.Fresh);
        await SeedStoreAsync("S-XM", "跨月分店");
        await _localDb.Insertable(new List<ChinaSupplier>
        {
            new() { Guid = "xm-old", SupplierCode = "CN-XM-OLD", SupplierName = "旧归属" },
            new() { Guid = "xm-new", SupplierCode = "CN-XM-NEW", SupplierName = "新归属" },
        }).ExecuteCommandAsync();
        await _posmDb.Insertable(new List<PosmProductSupplierMapping>
        {
            new() { ProductCode = "P-XM-SAME", LocalSupplierCode = "200", ChinaSupplierCode = "CN-XM-OLD" },
            new() { ProductCode = "P-XM-MOVED", LocalSupplierCode = "200", ChinaSupplierCode = "CN-XM-OLD" },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new List<ProductStoreDailySalesStatistic>
        {
            // 同一门店×商品×原始编码在两个月各有一行：合并后只算一格。
            new() { Date = july, BranchCode = "S-XM", SupplierCode = "200", ProductCode = "P-XM-SAME", TotalQuantity = 2, TotalAmount = 20m, OrderCount = 1 },
            new() { Date = august, BranchCode = "S-XM", SupplierCode = "200", ProductCode = "P-XM-SAME", TotalQuantity = 3, TotalAmount = 30m, OrderCount = 1 },
            // 7 月走映射归 CN-XM-OLD，8 月直写 CN-XM-NEW：整段归最近的 CN-XM-NEW。
            new() { Date = july, BranchCode = "S-XM", SupplierCode = "200", ProductCode = "P-XM-MOVED", TotalQuantity = 1, TotalAmount = 10m, OrderCount = 1 },
            new() { Date = august, BranchCode = "S-XM", SupplierCode = "CN-XM-NEW", ProductCode = "P-XM-MOVED", TotalQuantity = 4, TotalAmount = 40m, OrderCount = 1 },
        }).ExecuteCommandAsync();

        var result = await CreateService().GetCompactSalesBoardAsync(RangeQuery(july, august));

        var store = Assert.Single(result.Stores);
        Assert.Equal((100m, 2), (store.TotalAmount, store.ProductCount));
        Assert.Equal((50m, "CN-XM-OLD"), result.ProductDetails.Data.Where(row => row.ProductCode == "P-XM-SAME").Select(row => (row.TotalAmount, row.ChinaSupplierCode!)).Single());
        Assert.Equal((50m, "CN-XM-NEW"), result.ProductDetails.Data.Where(row => row.ProductCode == "P-XM-MOVED").Select(row => (row.TotalAmount, row.ChinaSupplierCode!)).Single());
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_对账失败但已聚合的日期照常出数并提示()
    {
        var firstDay = new DateTime(2026, 4, 8);
        var failedDay = firstDay.AddDays(1);
        await SeedStatisticStateAsync(firstDay, SalesStatisticRefreshStatus.Fresh);
        // 与生产 2026-04-09 相同：对账未通过，但当天商品事实已经聚合发布。
        await SeedStatisticStateAsync(failedDay, SalesStatisticRefreshStatus.Failed, "商品统计与分店营业额统计不一致");
        await SeedCompactDailyRowsAsync(firstDay, failedDay);

        var result = await CreateService().GetCompactSalesBoardAsync(new CompactSalesBoardQuery
        {
            DateRange = new DateRangeDto { StartDate = firstDay, EndDate = failedDay },
        });

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        Assert.Equal(30m, result.Summary.TotalAmount);
        Assert.Contains("2026-04-09", result.StatisticMessage);
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_失败且从未聚合的日期仍阻断()
    {
        var date = new DateTime(2026, 4, 10);
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = date,
            Status = SalesStatisticRefreshStatus.Failed,
            SourceTimeZone = "POSM_LOCAL",
            ErrorMessage = "首次聚合失败",
        }).ExecuteCommandAsync();
        await SeedCompactDailyRowsAsync(date);

        var result = await CreateService().GetCompactSalesBoardAsync(BoardQuery(date));

        Assert.Equal(SalesStatisticRefreshStatus.Failed, result.StatisticStatus);
        Assert.Empty(result.Stores);
        Assert.Equal(0m, result.Summary.TotalAmount);
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    public async Task GetCompactSalesBoardAsync_重算排队或运行中读取上一版已发布快照(string refreshingStatus)
    {
        var date = new DateTime(2026, 8, 20);
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = date,
            Status = refreshingStatus,
            SourceTimeZone = "POSM_LOCAL",
            SourceProductVersion = "published-v1",
            LastAggregatedAtUtc = DateTime.UtcNow.AddHours(-1),
        }).ExecuteCommandAsync();
        await SeedCompactDailyRowsAsync(date);

        var result = await CreateService().GetCompactSalesBoardAsync(BoardQuery(date));

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        Assert.Equal(15m, result.Summary.TotalAmount);
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_历史缺口只提示而最新状态之后的日期按未发布处理()
    {
        var gapDay = new DateTime(2025, 5, 4);
        var trackedDay = gapDay.AddDays(1);
        await SeedStatisticStateAsync(trackedDay, SalesStatisticRefreshStatus.Fresh);
        await SeedCompactDailyRowsAsync(trackedDay);
        var service = CreateService();

        // 早于最新状态、从未生成商品日统计的历史日期（生产 2025-05-04～05-31）不阻断长区间。
        var withGap = await service.GetCompactSalesBoardAsync(new CompactSalesBoardQuery
        {
            DateRange = new DateRangeDto { StartDate = gapDay, EndDate = trackedDay },
        });
        // 排在最新状态之后又没有状态的日期（尚未排队的今天）仍按未发布处理，与销售明细一致。
        var withUntracked = await service.GetCompactSalesBoardAsync(new CompactSalesBoardQuery
        {
            DateRange = new DateRangeDto { StartDate = trackedDay, EndDate = trackedDay.AddDays(1) },
        });

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, withGap.StatisticStatus);
        Assert.Equal(15m, withGap.Summary.TotalAmount);
        Assert.Contains("2025-05-04", withGap.StatisticMessage);
        Assert.Equal(SalesStatisticRefreshStatus.Pending, withUntracked.StatisticStatus);
        Assert.Empty(withUntracked.Stores);
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_统计时间按UTC标注且取区间内最近一次发布()
    {
        var firstDay = new DateTime(2026, 8, 21);
        var secondDay = firstDay.AddDays(1);
        var earlier = new DateTime(2026, 8, 22, 1, 0, 0, DateTimeKind.Utc);
        var latest = new DateTime(2026, 8, 23, 2, 0, 39, DateTimeKind.Utc);
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily, Date = firstDay, Status = SalesStatisticRefreshStatus.Fresh,
            SourceTimeZone = "POSM_LOCAL", LastAggregatedAtUtc = earlier, CompletedAtUtc = earlier,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily, Date = secondDay, Status = SalesStatisticRefreshStatus.Fresh,
            SourceTimeZone = "POSM_LOCAL", LastAggregatedAtUtc = latest, CompletedAtUtc = latest,
        }).ExecuteCommandAsync();

        var result = await CreateService().GetCompactSalesBoardAsync(new CompactSalesBoardQuery
        {
            DateRange = new DateRangeDto { StartDate = firstDay, EndDate = secondDay },
        });

        // 数据库读回的时间不带时区；未标注 UTC 时 JSON 不带 Z，浏览器会把 02:00 UTC 显示成本地 02:00。
        Assert.Equal(DateTimeKind.Utc, result.StatisticUpdatedAt!.Value.Kind);
        Assert.Equal(latest, result.StatisticUpdatedAt.Value);
        Assert.EndsWith("Z\"", System.Text.Json.JsonSerializer.Serialize(result.StatisticUpdatedAt));
    }

    [Fact]
    public void CompactSalesBoardCubeCacheKey_只由日期范围和统计水位决定()
    {
        var range = new DateRangeDto { StartDate = new DateTime(2026, 8, 3), EndDate = new DateTime(2026, 8, 3) };
        var sameDayWithTime = new DateRangeDto { StartDate = new DateTime(2026, 8, 3, 9, 30, 0), EndDate = new DateTime(2026, 8, 3, 18, 0, 0) };
        var first = SalesDashboardCacheKeys.CompactSalesBoardCube(range, "watermark");

        Assert.Equal(first, SalesDashboardCacheKeys.CompactSalesBoardCube(sameDayWithTime, "watermark"));
        Assert.NotEqual(first, SalesDashboardCacheKeys.CompactSalesBoardCube(range, "watermark-2"));
        Assert.NotEqual(first, SalesDashboardCacheKeys.CompactSalesBoardCube(
            new DateRangeDto { StartDate = range.StartDate, EndDate = range.EndDate.AddDays(1) }, "watermark"));
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_等额结果按代码稳定排序且分页无重复遗漏()
    {
        var date = new DateTime(2026, 8, 3);
        await SeedStatisticStateAsync(date, SalesStatisticRefreshStatus.Fresh);

        // 按反序写入，确保断言验证的是显式 tie-break，而不是数据库偶然的插入顺序。
        foreach (var index in Enumerable.Range(1, 41).Reverse())
        {
            var productCode = $"P-EQUAL-{index:D3}";
            await _localDb.Insertable(new ProductStoreDailySalesStatistic
            {
                Date = date,
                BranchCode = index % 2 == 0 ? "S-EQUAL-B" : "S-EQUAL-A",
                SupplierCode = "200",
                ProductCode = productCode,
                ProductName = productCode,
                TotalQuantity = 1,
                TotalAmount = 10m,
                OrderCount = 1,
            }).ExecuteCommandAsync();
            await _posmDb.Insertable(new PosmProductSupplierMapping
            {
                ProductCode = productCode,
                LocalSupplierCode = "200",
                ChinaSupplierCode = index % 2 == 0 ? "CN-EQUAL-B" : "CN-EQUAL-A",
            }).ExecuteCommandAsync();
        }

        var range = new DateRangeDto { StartDate = date, EndDate = date };
        var service = CreateService();
        var firstPage = await service.GetCompactSalesBoardAsync(new CompactSalesBoardQuery { DateRange = range, PageIndex = 1, PageSize = 20 });
        var secondPage = await service.GetCompactSalesBoardAsync(new CompactSalesBoardQuery { DateRange = range, PageIndex = 2, PageSize = 20 });
        var thirdPage = await service.GetCompactSalesBoardAsync(new CompactSalesBoardQuery { DateRange = range, PageIndex = 3, PageSize = 20 });

        Assert.Equal(new[] { "S-EQUAL-A", "S-EQUAL-B" }, firstPage.Stores.Select(row => row.BranchCode));
        Assert.Equal(new[] { "CN-EQUAL-A", "CN-EQUAL-B" }, firstPage.ChinaSuppliers.Select(row => row.SupplierCode));
        var productCodes = firstPage.ProductDetails.Data
            .Concat(secondPage.ProductDetails.Data)
            .Concat(thirdPage.ProductDetails.Data)
            .Select(row => row.ProductCode)
            .ToList();
        Assert.Equal(41, productCodes.Count);
        Assert.Equal(41, productCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(Enumerable.Range(1, 41).Select(index => $"P-EQUAL-{index:D3}"), productCodes);

        // 每页上限 500（与带图导出一致）：超出时钳到 500，一页即可取回全部 41 款且顺序不变。
        var widePage = await service.GetCompactSalesBoardAsync(new CompactSalesBoardQuery { DateRange = range, PageIndex = 1, PageSize = 900 });
        Assert.Equal(500, widePage.ProductDetails.PageSize);
        Assert.Equal(productCodes, widePage.ProductDetails.Data.Select(row => row.ProductCode));
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_cacheMiss期间清缓存仍返回数据但不登记或写入()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        var date = new DateTime(2026, 8, 3);
        await SeedStatisticStateAsync(date, SalesStatisticRefreshStatus.Fresh);
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = date, BranchCode = "S-CLEAR", SupplierCode = "200", ProductCode = "P-CLEAR",
            ProductName = "清理竞态商品", TotalQuantity = 1, TotalAmount = 10m, OrderCount = 1,
        }).ExecuteCommandAsync();
        await _posmDb.Insertable(new PosmProductSupplierMapping
        {
            ProductCode = "P-CLEAR", LocalSupplierCode = "200", ChinaSupplierCode = "CN-CLEAR",
        }).ExecuteCommandAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);
        service.ProductSalesAnalysisCacheWriteInterceptor = () => SalesDashboardCacheKeys.ClearActiveKeys();
        var first = await service.GetCompactSalesBoardAsync(BoardQuery(date));
        await _localDb.Updateable<ProductStoreDailySalesStatistic>()
            .SetColumns(row => row.TotalAmount == 20m)
            .Where(row => row.ProductCode == "P-CLEAR")
            .ExecuteCommandAsync();
        var second = await service.GetCompactSalesBoardAsync(BoardQuery(date));

        Assert.Equal(10m, Assert.Single(first.Stores).TotalAmount);
        Assert.Equal(20m, Assert.Single(second.Stores).TotalAmount);
        Assert.DoesNotContain(SalesDashboardCacheKeys.ActiveKeys, key => key.StartsWith("SalesDashboard:CompactSalesBoardCube:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_超过2100个国内供应商时一次读取名称和统计()
    {
        var date = new DateTime(2026, 8, 4);
        const int count = 2105;
        await SeedStatisticStateAsync(date, SalesStatisticRefreshStatus.Fresh);
        var statistics = Enumerable.Range(1, count).Select(index => new ProductStoreDailySalesStatistic
        {
            Date = date,
            BranchCode = "S-LARGE",
            SupplierCode = "200",
            ProductCode = $"P-LARGE-{index:D4}",
            ProductName = "大批量商品",
            TotalQuantity = 1,
            TotalAmount = 1m,
            OrderCount = 1,
        }).ToList();
        var mappings = Enumerable.Range(1, count).Select(index => new PosmProductSupplierMapping
        {
            ProductCode = $"P-LARGE-{index:D4}",
            LocalSupplierCode = "200",
            ChinaSupplierCode = $"CN-LARGE-{index:D4}",
        }).ToList();
        var suppliers = Enumerable.Range(1, count).Select(index => new ChinaSupplier
        {
            Guid = $"large-{index:D4}",
            SupplierCode = $"CN-LARGE-{index:D4}",
            SupplierName = $"大供应商{index:D4}",
        }).ToList();
        foreach (var batch in statistics.Chunk(250))
            await _localDb.Insertable(batch.ToList()).ExecuteCommandAsync();
        foreach (var batch in suppliers.Chunk(250))
            await _localDb.Insertable(batch.ToList()).ExecuteCommandAsync();
        foreach (var batch in mappings.Chunk(250))
            await _posmDb.Insertable(batch.ToList()).ExecuteCommandAsync();

        var result = await CreateService().GetCompactSalesBoardAsync(BoardQuery(date));

        Assert.Equal(count, result.ProductDetails.Total);
        Assert.Equal(count, result.ChinaSuppliers.Count);
        Assert.All(result.ChinaSuppliers, supplier => Assert.StartsWith("大供应商", supplier.SupplierName));
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_未筛选供应商时大映射集合单次聚合()
    {
        var date = new DateTime(2026, 8, 5);
        const int count = 2105;
        await SeedStatisticStateAsync(date, SalesStatisticRefreshStatus.Fresh);
        foreach (var batch in Enumerable.Range(1, count).Chunk(250))
        {
            await _localDb.Insertable(batch.Select(index => new ProductStoreDailySalesStatistic
            {
                Date = date,
                BranchCode = "S-UNFILTERED-LARGE",
                SupplierCode = "200",
                ProductCode = $"P-UNFILTERED-{index:D4}",
                ProductName = "未筛选大集合商品",
                TotalQuantity = 1,
                TotalAmount = 1m,
                OrderCount = 1,
            }).ToList()).ExecuteCommandAsync();
            await _posmDb.Insertable(batch.Select(index => new PosmProductSupplierMapping
            {
                ProductCode = $"P-UNFILTERED-{index:D4}",
                LocalSupplierCode = "200",
                ChinaSupplierCode = $"CN-UNFILTERED-{index:D4}",
            }).ToList()).ExecuteCommandAsync();
        }

        var statisticStatements = new List<string>();
        _localDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("ProductStoreDailySalesStatistic", StringComparison.OrdinalIgnoreCase))
                statisticStatements.Add(sql);
        };
        var result = await CreateService().GetCompactSalesBoardAsync(BoardQuery(date));
        _localDb.Aop.OnLogExecuting = null;

        Assert.Equal(count, result.ProductDetails.Total);
        Assert.Equal(count, result.ChinaSuppliers.Count);
        Assert.Equal(1m, Assert.Single(result.Stores).TotalAmount / count);
        // 只有立方体聚合 1 条读统计表：商品资料改为按立方体里的编码点查，不再回探统计表；聚合不带商品编码 IN 列表。
        var statisticStatement = Assert.Single(statisticStatements);
        Assert.DoesNotContain("P-UNFILTERED-", statisticStatement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_交叉筛选时各栏不被自身选中项收窄()
    {
        var date = await SeedCompactCrossFilterFixtureAsync(new DateTime(2026, 8, 6));
        var service = CreateService();

        var byBranch = await service.GetCompactSalesBoardAsync(BoardQuery(date, query => query.SelectedBranchCode = "S-A"));

        // 分店栏不受已选分店约束，仍保留两家，便于直接改选；未映射商品与非 200 行都不计入。
        Assert.Equal(new[] { ("S-A", 50m, 2), ("S-B", 35m, 2) },
            byBranch.Stores.Select(row => (row.BranchCode, row.TotalAmount, row.ProductCount)));
        Assert.Equal(new[] { ("CN-2", 30m, 1), ("CN-1", 20m, 1) },
            byBranch.ChinaSuppliers.Select(row => (row.SupplierCode, row.TotalAmount, row.ProductCount)));
        Assert.Equal(new[] { "P-3", "P-1" }, byBranch.ProductDetails.Data.Select(row => row.ProductCode));
        Assert.Equal(50m, byBranch.Summary.TotalAmount);
        Assert.Equal(3, byBranch.Summary.TotalQuantity);
        Assert.Equal((2, 1, 2), (byBranch.Summary.ProductCount, byBranch.Summary.StoreCount, byBranch.Summary.SupplierCount));
        Assert.Equal(85m, byBranch.Summary.OverallAmount);
        Assert.Equal(9, byBranch.Summary.OverallQuantity);

        var byBranchAndSupplier = await service.GetCompactSalesBoardAsync(BoardQuery(date, query =>
        {
            query.SelectedBranchCode = "S-A";
            query.SelectedChinaSupplierCode = "CN-1";
        }));

        Assert.Equal(new[] { ("S-B", 35m), ("S-A", 20m) },
            byBranchAndSupplier.Stores.Select(row => (row.BranchCode, row.TotalAmount)));
        Assert.Equal(new[] { "CN-2", "CN-1" }, byBranchAndSupplier.ChinaSuppliers.Select(row => row.SupplierCode));
        Assert.Equal("P-1", Assert.Single(byBranchAndSupplier.ProductDetails.Data).ProductCode);
        Assert.Equal(20m, byBranchAndSupplier.ProductDetails.ScopeAmount);
        Assert.Equal(20m, byBranchAndSupplier.Summary.TotalAmount);

        var byProduct = await service.GetCompactSalesBoardAsync(BoardQuery(date, query => query.SelectedProductCode = "P-1"));

        // 选中商品反查：分店栏只剩卖过该商品的分店，商品栏仍保留全部候选（同额按商品编码稳定排序）。
        Assert.Equal(new[] { ("S-A", 20m), ("S-B", 10m) }, byProduct.Stores.Select(row => (row.BranchCode, row.TotalAmount)));
        Assert.Equal(("CN-1", 30m), (Assert.Single(byProduct.ChinaSuppliers).SupplierCode, byProduct.ChinaSuppliers[0].TotalAmount));
        Assert.Equal(new[] { "P-1", "P-3", "P-2" }, byProduct.ProductDetails.Data.Select(row => row.ProductCode));
        Assert.Equal((30m, 2), (byProduct.Summary.TotalAmount, byProduct.Summary.StoreCount));
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_授权范围外的分店不可见且选中也不返回数据()
    {
        var date = await SeedCompactCrossFilterFixtureAsync(new DateTime(2026, 8, 7));
        var service = CreateService();

        var scoped = await service.GetCompactSalesBoardAsync(BoardQuery(date, query => query.BranchCodes = new List<string> { "s-b" }));
        var outOfScope = await service.GetCompactSalesBoardAsync(BoardQuery(date, query =>
        {
            query.BranchCodes = new List<string> { "S-B" };
            query.SelectedBranchCode = "S-A";
        }));
        var emptyScope = await service.GetCompactSalesBoardAsync(BoardQuery(date, query => query.BranchCodes = new List<string>()));

        Assert.Equal("S-B", Assert.Single(scoped.Stores).BranchCode);
        Assert.Equal(new[] { "P-1", "P-2" }, scoped.ProductDetails.Data.Select(row => row.ProductCode).OrderBy(code => code));
        Assert.Equal(35m, scoped.Summary.OverallAmount);
        Assert.Empty(outOfScope.ProductDetails.Data);
        Assert.Empty(outOfScope.ChinaSuppliers);
        Assert.Equal(0m, outOfScope.Summary.TotalAmount);
        Assert.Empty(emptyScope.Stores);
        Assert.Empty(emptyScope.ProductDetails.Data);
    }

    [Theory]
    [InlineData("quantity", "desc", new[] { "P-2", "P-1", "P-3" })]
    [InlineData("unitPrice", "asc", new[] { "P-2", "P-1", "P-3" })]
    [InlineData("unitPrice", "desc", new[] { "P-3", "P-1", "P-2" })]
    [InlineData("itemNumber", null, new[] { "P-1", "P-2", "P-3" })]
    [InlineData("itemNumber", "desc", new[] { "P-3", "P-2", "P-1" })]
    [InlineData("unknown", null, new[] { "P-1", "P-3", "P-2" })]
    public async Task GetCompactSalesBoardAsync_商品明细按字段全量排序(string sortField, string? sortOrder, string[] expected)
    {
        var date = await SeedCompactCrossFilterFixtureAsync(new DateTime(2026, 8, 8));

        var result = await CreateService().GetCompactSalesBoardAsync(BoardQuery(date, query =>
        {
            query.SortField = sortField;
            query.SortOrder = sortOrder;
        }));

        Assert.Equal(expected, result.ProductDetails.Data.Select(row => row.ProductCode));
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_关键词多词全部命中且只过滤商品栏()
    {
        var date = await SeedCompactCrossFilterFixtureAsync(new DateTime(2026, 8, 9));
        var service = CreateService();

        var canvas = await service.GetCompactSalesBoardAsync(BoardQuery(date, query => query.Keyword = " CANVAS "));
        var canvas60 = await service.GetCompactSalesBoardAsync(BoardQuery(date, query => query.Keyword = "canvas  60"));
        var byItemNumber = await service.GetCompactSalesBoardAsync(BoardQuery(date, query => query.Keyword = "it-3"));

        Assert.Equal(new[] { "P-1", "P-2" }, canvas.ProductDetails.Data.Select(row => row.ProductCode));
        Assert.Equal(2, canvas.ProductDetails.Total);
        // 占比分母不随关键词变化：仍是分店、供应商约束下全部商品的合计。
        Assert.Equal(85m, canvas.ProductDetails.ScopeAmount);
        Assert.Equal(2, canvas.Stores.Count);
        Assert.Equal(85m, canvas.Summary.TotalAmount);
        Assert.Equal("P-1", Assert.Single(canvas60.ProductDetails.Data).ProductCode);
        Assert.Equal("P-3", Assert.Single(byItemNumber.ProductDetails.Data).ProductCode);
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_直写国内供应商编码无需POSM映射即可计入看板()
    {
        var date = new DateTime(2026, 8, 11);
        await SeedStatisticStateAsync(date, SalesStatisticRefreshStatus.Fresh);
        await SeedStoreAsync("S-DIRECT", "直写分店");
        await SeedProductAsync("P-BOARD-DIRECT", "IT-BOARD-DIRECT", null, "直写商品", true, true, 1);
        await _localDb.Insertable(new List<ChinaSupplier>
        {
            new() { Guid = "board-direct", SupplierCode = "CN-BOARD", SupplierName = "直写供应商" },
            new() { Guid = "board-soft", SupplierCode = "CN-BOARD-SOFT", SupplierName = "已删除供应商", IsDeleted = true },
        }).ExecuteCommandAsync();
        // 整个 POSM 映射表为空：直写行自带国内供应商编码，看板不能因此返回空。
        await _localDb.Insertable(new List<ProductStoreDailySalesStatistic>
        {
            new() { Date = date, BranchCode = "S-DIRECT", SupplierCode = "CN-BOARD", ProductCode = "P-BOARD-DIRECT", TotalQuantity = 4, TotalAmount = 40m, OrderCount = 1 },
            new() { Date = date, BranchCode = "S-DIRECT", SupplierCode = "CN-BOARD-SOFT", ProductCode = "P-BOARD-SOFT", TotalQuantity = 1, TotalAmount = 5m, OrderCount = 1 },
            // 未映射的旧 200 行和澳洲供应商行仍不计入。
            new() { Date = date, BranchCode = "S-DIRECT", SupplierCode = "200", ProductCode = "P-BOARD-UNMAPPED", TotalQuantity = 9, TotalAmount = 99m, OrderCount = 1 },
            new() { Date = date, BranchCode = "S-DIRECT", SupplierCode = "105", ProductCode = "P-BOARD-DIRECT", TotalQuantity = 7, TotalAmount = 70m, OrderCount = 1 },
        }).ExecuteCommandAsync();

        var result = await CreateService().GetCompactSalesBoardAsync(BoardQuery(date));

        Assert.Equal(45m, result.Summary.TotalAmount);
        Assert.Equal(new[] { "CN-BOARD", "CN-BOARD-SOFT" }, result.ChinaSuppliers.Select(supplier => supplier.SupplierCode).OrderBy(code => code));
        Assert.Equal("已删除供应商", result.ChinaSuppliers.Single(supplier => supplier.SupplierCode == "CN-BOARD-SOFT").SupplierName);
        var product = result.ProductDetails.Data.Single(row => row.ProductCode == "P-BOARD-DIRECT");
        Assert.Equal("直写商品", product.ProductName);
        Assert.Equal("直写供应商", product.ChinaSupplierName);
        Assert.Equal(40m, product.TotalAmount);
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_新旧写法混存时每个门店商品只有一格且归属取最近销售日()
    {
        var firstDay = new DateTime(2026, 8, 12);
        var secondDay = firstDay.AddDays(1);
        await SeedStatisticStateAsync(firstDay, SalesStatisticRefreshStatus.Fresh);
        await SeedStatisticStateAsync(secondDay, SalesStatisticRefreshStatus.Fresh);
        await SeedStoreAsync("S-MIXED", "混存分店");
        await _localDb.Insertable(new List<ChinaSupplier>
        {
            new() { Guid = "mixed-old", SupplierCode = "CN-OLD", SupplierName = "旧归属" },
            new() { Guid = "mixed-new", SupplierCode = "CN-NEW", SupplierName = "新归属" },
        }).ExecuteCommandAsync();
        await _posmDb.Insertable(new List<PosmProductSupplierMapping>
        {
            new() { ProductCode = "P-SAME", LocalSupplierCode = "200", ChinaSupplierCode = "CN-OLD" },
            new() { ProductCode = "P-MOVED", LocalSupplierCode = "200", ChinaSupplierCode = "CN-OLD" },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new List<ProductStoreDailySalesStatistic>
        {
            // 切换前一天是旧 200 行，切换后是直写行，两种写法解析出同一个供应商。
            new() { Date = firstDay, BranchCode = "S-MIXED", SupplierCode = "200", ProductCode = "P-SAME", TotalQuantity = 2, TotalAmount = 20m, OrderCount = 1 },
            new() { Date = secondDay, BranchCode = "S-MIXED", SupplierCode = "CN-OLD", ProductCode = "P-SAME", TotalQuantity = 3, TotalAmount = 30m, OrderCount = 1 },
            // 归属中途变更：旧行映射到 CN-OLD，最近一天直写 CN-NEW，商品整体归最近的 CN-NEW。
            new() { Date = firstDay, BranchCode = "S-MIXED", SupplierCode = "200", ProductCode = "P-MOVED", TotalQuantity = 1, TotalAmount = 10m, OrderCount = 1 },
            new() { Date = secondDay, BranchCode = "S-MIXED", SupplierCode = "CN-NEW", ProductCode = "P-MOVED", TotalQuantity = 4, TotalAmount = 40m, OrderCount = 1 },
        }).ExecuteCommandAsync();

        var result = await CreateService().GetCompactSalesBoardAsync(new CompactSalesBoardQuery
        {
            DateRange = new DateRangeDto { StartDate = firstDay, EndDate = secondDay },
        });

        var store = Assert.Single(result.Stores);
        Assert.Equal(100m, store.TotalAmount);
        // 门店动销款数按格计数：两种写法的行必须合并成一格，否则同一商品会被数两次。
        Assert.Equal(2, store.ProductCount);
        Assert.Equal(2, result.ProductDetails.Total);
        var same = result.ProductDetails.Data.Single(row => row.ProductCode == "P-SAME");
        Assert.Equal(50m, same.TotalAmount);
        Assert.Equal("CN-OLD", same.ChinaSupplierCode);
        var moved = result.ProductDetails.Data.Single(row => row.ProductCode == "P-MOVED");
        Assert.Equal(50m, moved.TotalAmount);
        Assert.Equal("CN-NEW", moved.ChinaSupplierCode);
    }

    [Fact]
    public async Task GetCompactSalesBoardAsync_商品资料读取使用IsDeleted字面量以命中过滤索引()
    {
        var date = await SeedCompactCrossFilterFixtureAsync(new DateTime(2026, 8, 10));
        var productStatements = new List<string>();
        _localDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("ProductImage", StringComparison.OrdinalIgnoreCase))
                productStatements.Add(sql);
        };

        await CreateService().GetCompactSalesBoardAsync(BoardQuery(date));
        _localDb.Aop.OnLogExecuting = null;

        var sql = Assert.Single(productStatements);
        Assert.Contains("[IsDeleted] = 0", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@IsDeleted", sql, StringComparison.Ordinal);
        // 商品资料按立方体编码点查，不再对统计表做相关子查询。
        Assert.DoesNotContain("ProductStoreDailySalesStatistic", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static CompactSalesBoardQuery BoardQuery(DateTime date, Action<CompactSalesBoardQuery>? configure = null)
    {
        var query = new CompactSalesBoardQuery { DateRange = new DateRangeDto { StartDate = date, EndDate = date } };
        configure?.Invoke(query);
        return query;
    }

    private static CompactSalesBoardQuery RangeQuery(DateTime start, DateTime end) =>
        new() { DateRange = new DateRangeDto { StartDate = start, EndDate = end } };

    /// <summary>记录此后对统计表的聚合查询（状态与商品资料不读统计表，造数的 UPDATE 不计），用于断言分片是否被复用。</summary>
    private List<string> CaptureStatisticStatements()
    {
        var statements = new List<string>();
        _localDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("ProductStoreDailySalesStatistic", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase))
                statements.Add(sql);
        };
        return statements;
    }

    /// <summary>
    /// 跨三个月的分片夹具：2026-07-01～09-10 每天都有 Fresh 状态；
    /// 7-10 P-M1 $10、8-10 P-M1 $20、9-05 P-M2 $30，都映射到 CN-M。
    /// </summary>
    private async Task SeedCompactMonthsFixtureAsync()
    {
        for (var date = new DateTime(2026, 7, 1); date <= new DateTime(2026, 9, 10); date = date.AddDays(1))
            await SeedStatisticStateAsync(date, SalesStatisticRefreshStatus.Fresh);
        await SeedStoreAsync("S-M", "分片分店");
        await _localDb.Insertable(new ChinaSupplier { Guid = "cn-m", SupplierCode = "CN-M", SupplierName = "分片供应商" }).ExecuteCommandAsync();
        await _posmDb.Insertable(new List<PosmProductSupplierMapping>
        {
            new() { ProductCode = "P-M1", LocalSupplierCode = "200", ChinaSupplierCode = "CN-M" },
            new() { ProductCode = "P-M2", LocalSupplierCode = "200", ChinaSupplierCode = "CN-M" },
        }).ExecuteCommandAsync();
        foreach (var (date, product, amount) in new[]
        {
            (new DateTime(2026, 7, 10), "P-M1", 10m),
            (new DateTime(2026, 8, 10), "P-M1", 20m),
            (new DateTime(2026, 9, 5), "P-M2", 30m),
        })
        {
            await _localDb.Insertable(new ProductStoreDailySalesStatistic
            {
                Date = date, BranchCode = "S-M", SupplierCode = "200", ProductCode = product,
                ProductName = product, TotalQuantity = 1, TotalAmount = amount, OrderCount = 1,
            }).ExecuteCommandAsync();
        }
    }

    /// <summary>每个日期写一条已映射的 200 行（1 件 $15），用于只关心完整性判定的用例；状态行由调用方自行准备。</summary>
    private async Task SeedCompactDailyRowsAsync(params DateTime[] dates)
    {
        await SeedStoreAsync("S-DAY", "日期分店");
        await _localDb.Insertable(new ChinaSupplier { Guid = "cn-day", SupplierCode = "CN-DAY", SupplierName = "日期供应商" }).ExecuteCommandAsync();
        await _posmDb.Insertable(new PosmProductSupplierMapping
        {
            ProductCode = "P-DAY", LocalSupplierCode = "200", ChinaSupplierCode = "CN-DAY",
        }).ExecuteCommandAsync();
        foreach (var date in dates)
        {
            await _localDb.Insertable(new ProductStoreDailySalesStatistic
            {
                Date = date, BranchCode = "S-DAY", SupplierCode = "200", ProductCode = "P-DAY",
                ProductName = "日期商品", TotalQuantity = 1, TotalAmount = 15m, OrderCount = 1,
            }).ExecuteCommandAsync();
        }
    }

    /// <summary>
    /// 两家分店 × 两个国内供应商 × 三个商品的交叉筛选夹具，另含未映射商品与非 200 统计行作为干扰项。
    /// S-A：P-1(2 件 $20)、P-3(1 件 $30)；S-B：P-1(1 件 $10)、P-2(5 件 $25)。
    /// </summary>
    private async Task<DateTime> SeedCompactCrossFilterFixtureAsync(DateTime date)
    {
        await SeedStatisticStateAsync(date, SalesStatisticRefreshStatus.Fresh);
        await SeedStoreAsync("S-A", "分店A");
        await SeedStoreAsync("S-B", "分店B");
        await SeedProductAsync("P-1", "IT-1", null, "Canvas Frame 60*90cm", true, true, 1);
        await SeedProductAsync("P-2", "IT-2", null, "Canvas Board 20*25cm", true, true, 1);
        await SeedProductAsync("P-3", "IT-3", null, "Sketch Pad A4", true, true, 1);
        await _localDb.Insertable(new List<ChinaSupplier>
        {
            new() { Guid = "cross-cn-1", SupplierCode = "CN-1", SupplierName = "供应商一" },
            new() { Guid = "cross-cn-2", SupplierCode = "CN-2", SupplierName = "供应商二" },
        }).ExecuteCommandAsync();
        await _posmDb.Insertable(new List<PosmProductSupplierMapping>
        {
            new() { ProductCode = "P-1", LocalSupplierCode = "200", ChinaSupplierCode = "CN-1" },
            new() { ProductCode = "P-2", LocalSupplierCode = "200", ChinaSupplierCode = "CN-1" },
            new() { ProductCode = "P-3", LocalSupplierCode = "200", ChinaSupplierCode = "CN-2" },
        }).ExecuteCommandAsync();

        ProductStoreDailySalesStatistic Row(string branchCode, string supplierCode, string productCode, int quantity, decimal amount) => new()
        {
            Date = date, BranchCode = branchCode, SupplierCode = supplierCode, ProductCode = productCode,
            ProductName = productCode, TotalQuantity = quantity, TotalAmount = amount, OrderCount = 1,
        };
        await _localDb.Insertable(new List<ProductStoreDailySalesStatistic>
        {
            Row("S-A", "200", "P-1", 2, 20m),
            Row("S-A", "200", "P-3", 1, 30m),
            Row("S-B", "200", "P-1", 1, 10m),
            Row("S-B", "200", "P-2", 5, 25m),
            Row("S-A", "200", "P-UNMAPPED", 9, 99m),
            Row("S-B", "999", "P-2", 7, 70m),
        }).ExecuteCommandAsync();
        return date;
    }

    [Fact]
    public async Task GetBestSellersAsync_返回仓库状态条码起订量和参与统计分店数量()
    {
        await SeedProductAsync("P-BEST-1", "ITEM-1", "BAR-1", "热销一", productIsActive: true, warehouseIsActive: false, minOrderQuantity: 6);
        await SeedProductAsync("P-BEST-2", "ITEM-2", "BAR-2", "热销二", productIsActive: false, warehouseIsActive: true, minOrderQuantity: 3);
        await SeedStoreAsync("S1", "Store 1");
        await SeedStoreAsync("S2", "Store 2");
        await SeedStoreAsync("S3", "Store 3");

        await SeedSaleAsync("O-1", "D-1", "P-BEST-1", "S1", new DateTime(2026, 6, 1), 5, 10m);
        await SeedSaleAsync("O-2", "D-2", "P-BEST-1", "S2", new DateTime(2026, 6, 2), 7, 14m);
        await SeedSaleAsync("O-3", "D-3", "P-BEST-1", "S1", new DateTime(2026, 6, 3), 2, 4m);
        await SeedSaleAsync("O-4", "D-4", "P-BEST-2", "S3", new DateTime(2026, 6, 4), 20, 40m);
        await SeedBestSellerStatisticsFromPosmAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 8));

        var result = await CreateService().GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 6, 8) },
            new List<string> { "S1", "S2" },
            pageIndex: 1,
            pageSize: 10
        );

        var product = Assert.Single(result.Products);
        Assert.Equal("P-BEST-1", product.ProductCode);
        Assert.Equal("BAR-1", product.Barcode);
        Assert.False(product.IsActive);
        Assert.Equal(6, product.MinOrderQuantity);
        Assert.Equal(14, product.Quantity);
        Assert.Equal(28m, product.SalesAmount);
        Assert.Equal(2, product.BranchSalesCount);
        Assert.Collection(
            product.BranchSales,
            row =>
            {
                Assert.Equal("S1", row.BranchCode);
                Assert.Equal("Store 1", row.BranchName);
                Assert.Equal(7, row.Quantity);
            },
            row =>
            {
                Assert.Equal("S2", row.BranchCode);
                Assert.Equal("Store 2", row.BranchName);
                Assert.Equal(7, row.Quantity);
            }
        );
    }

    [Fact]
    public async Task GetBestSellersAsync_空权限分店列表直接返回空结果()
    {
        await SeedProductAsync("P-NO-STORE", "ITEM-NO-STORE", "BAR-NO-STORE", "无权限商品", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await SeedSaleAsync("O-NO-STORE", "D-NO-STORE", "P-NO-STORE", "S1", new DateTime(2026, 6, 1), 9, 18m);

        var result = await CreateService().GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 6, 8) },
            new List<string>(),
            pageIndex: 1,
            pageSize: 50
        );

        Assert.Empty(result.Products);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task GetBestSellersAsync_忽略软删除仓库库存状态()
    {
        await _localDb.Insertable(new Product
        {
            UUID = "P-SOFT-uuid",
            ProductCode = "P-SOFT",
            ItemNumber = "ITEM-SOFT",
            Barcode = "BAR-SOFT",
            ProductName = "软删除库存商品",
            LocalSupplierCode = "200",
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new WarehouseProduct
        {
            ProductCode = "P-SOFT",
            IsActive = false,
            MinOrderQuantity = 12,
            IsDeleted = true,
        }).ExecuteCommandAsync();
        await SeedSaleAsync("O-SOFT", "D-SOFT", "P-SOFT", "S1", new DateTime(2026, 6, 1), 4, 8m);
        await SeedBestSellerStatisticsFromPosmAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 8));

        var result = await CreateService().GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 6, 8) },
            null,
            pageIndex: 1,
            pageSize: 50
        );

        var product = Assert.Single(result.Products);
        Assert.Null(product.IsActive);
        Assert.Null(product.MinOrderQuantity);
    }

    [Fact]
    public async Task GetBestSellersAsync_仓库商品匹配不受本地供应商码限制()
    {
        await SeedProductAsync(
            "HB022-119",
            "HB022-119",
            "9525810220084",
            "TOY",
            productIsActive: true,
            warehouseIsActive: false,
            minOrderQuantity: 4,
            localSupplierCode: null
        );
        await SeedSaleAsync("O-HB022", "D-HB022", "HB022-119", "S1", new DateTime(2026, 6, 1), 9, 18m);
        await SeedBestSellerStatisticsFromPosmAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 8));

        var result = await CreateService().GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 6, 8) },
            null,
            pageIndex: 1,
            pageSize: 50
        );

        var product = Assert.Single(result.Products);
        Assert.Equal("9525810220084", product.Barcode);
        Assert.False(product.IsActive);
        Assert.Equal(4, product.MinOrderQuantity);
    }

    [Fact]
    public async Task GetBestSellersAsync_HBweb条码缺失时使用统计表条码和名称()
    {
        await SeedProductAsync(
            "P-POSM-BAR",
            "ITEM-POSM-BAR",
            barcode: null,
            name: "",
            productIsActive: true,
            warehouseIsActive: true,
            minOrderQuantity: 1
        );
        await SeedSaleAsync(
            "O-POSM-BAR",
            "D-POSM-BAR",
            "P-POSM-BAR",
            "S1",
            new DateTime(2026, 6, 1),
            3,
            6m,
            barcode: "POSM-BAR-001",
            productName: "POSM 商品名"
        );
        await SeedBestSellerStatisticsFromPosmAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 8));

        var result = await CreateService().GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 6, 8) },
            null,
            pageIndex: 1,
            pageSize: 50
        );

        var product = Assert.Single(result.Products);
        Assert.Equal("POSM-BAR-001", product.Barcode);
        Assert.Equal("POSM 商品名", product.ProductName);
    }

    [Fact]
    public async Task GetBestSellersAsync_订单分店为空时用设备注册分店统计StoresSold()
    {
        await SeedProductAsync("P-DEVICE-STORE", "ITEM-DEVICE-STORE", "BAR-DEVICE", "设备分店商品", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await SeedStoreAsync("S1", "Store 1");
        await SeedStoreAsync("S2", "Store 2");
        await SeedDeviceAsync("POS-1", "S1");
        await SeedDeviceAsync("POS-2", "S2");
        await SeedSaleAsync("O-DEVICE-1", "D-DEVICE-1", "P-DEVICE-STORE", null, new DateTime(2026, 6, 1), 5, 10m, deviceCode: "POS-1");
        await SeedSaleAsync("O-DEVICE-2", "D-DEVICE-2", "P-DEVICE-STORE", null, new DateTime(2026, 6, 2), 7, 14m, deviceCode: "POS-2");
        await SeedBestSellerStatisticsFromPosmAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 8));

        var result = await CreateService().GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 6, 8) },
            null,
            pageIndex: 1,
            pageSize: 50
        );

        var product = Assert.Single(result.Products);
        Assert.Equal(2, product.BranchSalesCount);
        Assert.Collection(
            product.BranchSales,
            row =>
            {
                Assert.Equal("S2", row.BranchCode);
                Assert.Equal("Store 2", row.BranchName);
                Assert.Equal(7, row.Quantity);
            },
            row =>
            {
                Assert.Equal("S1", row.BranchCode);
                Assert.Equal("Store 1", row.BranchName);
                Assert.Equal(5, row.Quantity);
            }
        );
    }

    [Fact]
    public async Task GetBestSellersAsync_数据库分页返回正确总数排名和当前页分店销量()
    {
        await SeedProductAsync("P-RANK-1", "ITEM-RANK-1", "BAR-RANK-1", "排名一", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await SeedProductAsync("P-RANK-2", "ITEM-RANK-2", "BAR-RANK-2", "排名二", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await SeedProductAsync("P-RANK-3", "ITEM-RANK-3", "BAR-RANK-3", "排名三", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await SeedStoreAsync("S1", "Store 1");
        await SeedStoreAsync("S2", "Store 2");

        await SeedSaleAsync("O-RANK-1", "D-RANK-1", "P-RANK-1", "S1", new DateTime(2026, 6, 1), 30, 60m);
        await SeedSaleAsync("O-RANK-2", "D-RANK-2", "P-RANK-2", "S1", new DateTime(2026, 6, 2), 12, 24m);
        await SeedSaleAsync("O-RANK-3", "D-RANK-3", "P-RANK-2", "S2", new DateTime(2026, 6, 3), 8, 16m);
        await SeedSaleAsync("O-RANK-4", "D-RANK-4", "P-RANK-3", "S1", new DateTime(2026, 6, 4), 5, 10m);
        await SeedBestSellerStatisticsFromPosmAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 8));

        var result = await CreateService().GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 6, 8) },
            null,
            pageIndex: 2,
            pageSize: 1
        );

        var product = Assert.Single(result.Products);
        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.TotalPages);
        Assert.Equal(2, product.Rank);
        Assert.Equal("P-RANK-2", product.ProductCode);
        Assert.Equal(20, product.Quantity);
        Assert.Equal(2, product.BranchSalesCount);
        Assert.Equal(new[] { "S1", "S2" }, product.BranchSales.Select(x => x.BranchCode).ToArray());
    }

    [Fact]
    public async Task GetBestSellersAsync_全平台同日期同分页命中缓存()
    {
        await SeedProductAsync("P-CACHE-1", "ITEM-CACHE-1", "BAR-CACHE-1", "缓存商品一", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await SeedSaleAsync("O-CACHE-1", "D-CACHE-1", "P-CACHE-1", "S1", new DateTime(2026, 6, 1), 5, 10m);
        await SeedBestSellerStatisticsFromPosmAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 8));

        var service = CreateService();
        var dateRange = new DateRangeDto { StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 6, 8) };
        var first = await service.GetBestSellersAsync(dateRange, null, pageIndex: 1, pageSize: 50);

        await SeedProductAsync("P-CACHE-2", "ITEM-CACHE-2", "BAR-CACHE-2", "缓存商品二", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = new DateTime(2026, 6, 2),
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-CACHE-2",
            ProductName = "缓存商品二",
            Barcode = "BAR-CACHE-2",
            TotalQuantity = 99,
            TotalAmount = 198m,
            OrderCount = 1,
        }).ExecuteCommandAsync();

        var second = await service.GetBestSellersAsync(dateRange, null, pageIndex: 1, pageSize: 50);

        var product = Assert.Single(second.Products);
        Assert.Equal(1, first.Total);
        Assert.Equal(1, second.Total);
        Assert.Equal("P-CACHE-1", product.ProductCode);
        Assert.Equal(5, product.Quantity);
    }

    [Fact]
    public async Task GetBestSellersAsync_不同分页参数使用独立缓存键()
    {
        await SeedProductAsync("P-CACHE-PAGE-1", "ITEM-CACHE-PAGE-1", "BAR-CACHE-PAGE-1", "分页缓存一", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await SeedProductAsync("P-CACHE-PAGE-2", "ITEM-CACHE-PAGE-2", "BAR-CACHE-PAGE-2", "分页缓存二", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await SeedSaleAsync("O-CACHE-PAGE-1", "D-CACHE-PAGE-1", "P-CACHE-PAGE-1", "S1", new DateTime(2026, 6, 1), 20, 40m);
        await SeedSaleAsync("O-CACHE-PAGE-2", "D-CACHE-PAGE-2", "P-CACHE-PAGE-2", "S1", new DateTime(2026, 6, 1), 10, 20m);
        await SeedBestSellerStatisticsFromPosmAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 8));

        var service = CreateService();
        var dateRange = new DateRangeDto { StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 6, 8) };
        var firstPage = await service.GetBestSellersAsync(dateRange, null, pageIndex: 1, pageSize: 1);

        await SeedProductAsync("P-CACHE-PAGE-3", "ITEM-CACHE-PAGE-3", "BAR-CACHE-PAGE-3", "分页缓存三", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await SeedSaleAsync("O-CACHE-PAGE-3", "D-CACHE-PAGE-3", "P-CACHE-PAGE-3", "S1", new DateTime(2026, 6, 2), 99, 198m);
        await SeedBestSellerStatisticsFromPosmAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 8));

        var widerPage = await service.GetBestSellersAsync(dateRange, null, pageIndex: 1, pageSize: 2);

        Assert.Equal("P-CACHE-PAGE-1", Assert.Single(firstPage.Products).ProductCode);
        Assert.Equal(2, widerPage.Products.Count);
        Assert.Equal(3, widerPage.Total);
        Assert.Equal("P-CACHE-PAGE-3", widerPage.Products[0].ProductCode);
    }

    [Fact]
    public void GetBestSellersAsync_热销商品缓存时间为30分钟()
    {
        var field = typeof(SalesDashboardReactService).GetField(
            "BEST_SELLERS_CACHE_DURATION",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        Assert.NotNull(field);
        Assert.Equal(TimeSpan.FromMinutes(30), field!.GetValue(null));
    }

    [Fact]
    public async Task GetBestSellersAsync_优先读取商品统计表并返回毛利和分店明细()
    {
        await SeedProductAsync("P-STAT", "ITEM-STAT", "BAR-HB", "统计商品", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 2);
        await SeedStoreAsync("S1", "Store 1");
        await SeedStoreAsync("S2", "Store 2");
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-STAT",
            ProductName = "POSM 统计商品",
            Barcode = "BAR-POSM",
            TotalQuantity = 4,
            TotalAmount = 20m,
            OrderCount = 1,
            UnitCostSnapshot = 2m,
            TotalCost = 8m,
            GrossProfit = 12m,
            GrossMarginRate = 0.6m,
            CostSource = "StoreRetailPrice",
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S2",
            SupplierCode = "200",
            ProductCode = "P-STAT",
            ProductName = "POSM 统计商品",
            Barcode = "BAR-POSM",
            TotalQuantity = 6,
            TotalAmount = 30m,
            OrderCount = 1,
            UnitCostSnapshot = 2m,
            TotalCost = 12m,
            GrossProfit = 18m,
            GrossMarginRate = 0.6m,
            CostSource = "StoreRetailPrice",
        }).ExecuteCommandAsync();
        await SeedStatisticStateAsync(new DateTime(2026, 6, 1), SalesStatisticRefreshStatus.Fresh);

        var result = await CreateService().GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 6, 1).AddDays(1).AddTicks(-1) },
            null,
            pageIndex: 1,
            pageSize: 50
        );

        var product = Assert.Single(result.Products);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        Assert.Equal("P-STAT", product.ProductCode);
        Assert.Equal("BAR-HB", product.Barcode);
        Assert.Equal(10, product.Quantity);
        Assert.Equal(50m, product.SalesAmount);
        Assert.Equal(20m, product.TotalCost);
        Assert.Equal(30m, product.GrossProfit);
        Assert.Equal(0.6m, product.GrossMarginRate);
        Assert.Equal(2, product.BranchSalesCount);
        Assert.Collection(
            product.BranchSales,
            row =>
            {
                Assert.Equal("S2", row.BranchCode);
                Assert.Equal(6, row.Quantity);
                Assert.Equal(30m, row.SalesAmount);
                Assert.Equal(18m, row.GrossProfit);
            },
            row =>
            {
                Assert.Equal("S1", row.BranchCode);
                Assert.Equal(4, row.Quantity);
                Assert.Equal(20m, row.SalesAmount);
                Assert.Equal(12m, row.GrossProfit);
            }
        );
    }

    [Fact]
    public async Task GetBestSellersAsync_直写国内供应商编码的统计行计入榜单分店明细和商品资料()
    {
        var date = new DateTime(2026, 6, 3);
        await SeedStatisticStateAsync(date, SalesStatisticRefreshStatus.Fresh);
        await SeedStoreAsync("S1", "Store 1");
        await SeedStoreAsync("S2", "Store 2");
        await _localDb.Insertable(new ChinaSupplier { Guid = "cn-best-direct", SupplierCode = "CN-BEST", SupplierName = "直写供应商" }).ExecuteCommandAsync();

        ProductStoreDailySalesStatistic Row(DateTime day, string branchCode, string supplierCode, string productCode, int quantity) => new()
        {
            Date = day, BranchCode = branchCode, SupplierCode = supplierCode, ProductCode = productCode,
            ProductName = $"统计名称-{productCode}", Barcode = $"BAR-{productCode}",
            TotalQuantity = quantity, TotalAmount = quantity * 2m, OrderCount = 1,
            UnitCostSnapshot = 1m, TotalCost = quantity, GrossProfit = quantity, CostSource = "StoreRetailPrice",
        };
        // 逐行插入：SQLite 的批量插入与单行插入写出的日期文本格式不同，会让「Date <= 结束日」的文本比较失真。
        foreach (var statistic in new[]
        {
            // 只有直写行的商品：没有 200 行也必须上榜，条码和品名只能从统计行取到。
            Row(date, "S1", "CN-BEST", "P-DIRECT", 7),
            Row(date, "S2", "CN-BEST", "P-DIRECT", 2),
            // 同一商品新旧写法混存（切换前后各一天）：数量合并成一行。
            Row(date, "S1", "200", "P-MIXED", 3),
            Row(date.AddDays(1), "S1", "CN-BEST", "P-MIXED", 4),
            // 澳洲供应商的销售不属于国内货，销量再大也不上榜。
            Row(date, "S1", "105", "P-AUSTRALIAN", 100),
        })
        {
            await _localDb.Insertable(statistic).ExecuteCommandAsync();
        }
        await SeedStatisticStateAsync(date.AddDays(1), SalesStatisticRefreshStatus.Fresh);

        var result = await CreateService().GetBestSellersAsync(
            new DateRangeDto { StartDate = date, EndDate = date.AddDays(1) },
            null,
            pageIndex: 1,
            pageSize: 50
        );

        Assert.Equal(2, result.Total);
        Assert.Equal(new[] { "P-DIRECT", "P-MIXED" }, result.Products.Select(product => product.ProductCode));
        var direct = result.Products[0];
        Assert.Equal(9, direct.Quantity);
        Assert.Equal("BAR-P-DIRECT", direct.Barcode);
        Assert.Equal("统计名称-P-DIRECT", direct.ProductName);
        Assert.Equal(new[] { "S1", "S2" }, direct.BranchSales.Select(row => row.BranchCode));
        Assert.Equal(7, result.Products[1].Quantity);
        Assert.Equal(7, Assert.Single(result.Products[1].BranchSales).Quantity);
    }

    [Fact]
    public async Task GetBestSellersAsync_软删除国内供应商的直写行仍计入热销榜()
    {
        var date = new DateTime(2026, 6, 5);
        await SeedStatisticStateAsync(date, SalesStatisticRefreshStatus.Fresh);
        await SeedStoreAsync("S1", "Store 1");
        await _localDb.Insertable(new ChinaSupplier
        {
            Guid = "cn-best-soft", SupplierCode = "CN-BEST-SOFT", SupplierName = "已删除供应商", IsDeleted = true,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = date, BranchCode = "S1", SupplierCode = "CN-BEST-SOFT", ProductCode = "P-SOFT",
            ProductName = "软删除供应商商品", TotalQuantity = 5, TotalAmount = 10m, OrderCount = 1,
        }).ExecuteCommandAsync();

        var result = await CreateService().GetBestSellersAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            null,
            pageIndex: 1,
            pageSize: 50
        );

        Assert.Equal("P-SOFT", Assert.Single(result.Products).ProductCode);
    }

    [Fact]
    public async Task GetBestSellersAsync_Fresh空统计结果不缓存()
    {
        var dateRange = new DateRangeDto { StartDate = new DateTime(2026, 6, 9), EndDate = new DateTime(2026, 6, 9) };
        await SeedStatisticStateAsync(new DateTime(2026, 6, 9), SalesStatisticRefreshStatus.Fresh);
        var service = CreateService();

        var first = await service.GetBestSellersAsync(dateRange, null, pageIndex: 1, pageSize: 50);

        Assert.Empty(first.Products);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, first.StatisticStatus);

        await SeedProductAsync("P-FRESH-LATE", "ITEM-FRESH-LATE", "BAR-FRESH-LATE", "后补统计商品", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = new DateTime(2026, 6, 9),
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-FRESH-LATE",
            ProductName = "后补统计商品",
            Barcode = "BAR-FRESH-LATE",
            TotalQuantity = 11,
            TotalAmount = 22m,
            OrderCount = 1,
        }).ExecuteCommandAsync();

        var second = await service.GetBestSellersAsync(dateRange, null, pageIndex: 1, pageSize: 50);

        var product = Assert.Single(second.Products);
        Assert.Equal("P-FRESH-LATE", product.ProductCode);
        Assert.Equal(11, product.Quantity);
    }

    [Fact]
    public async Task GetBestSellersAsync_POSM不可访问时仍只读统计表()
    {
        await SeedProductAsync("P-NO-POSM", "ITEM-NO-POSM", "BAR-NO-POSM", "无 POSM 统计商品", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = new DateTime(2026, 6, 10),
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-NO-POSM",
            ProductName = "无 POSM 统计商品",
            Barcode = "BAR-NO-POSM",
            TotalQuantity = 3,
            TotalAmount = 6m,
            OrderCount = 1,
        }).ExecuteCommandAsync();
        await SeedStatisticStateAsync(new DateTime(2026, 6, 10), SalesStatisticRefreshStatus.Fresh);
        await SeedStatisticStateAsync(new DateTime(2026, 6, 11), SalesStatisticRefreshStatus.Fresh);
        await SeedStatisticStateAsync(new DateTime(2026, 6, 12), SalesStatisticRefreshStatus.Failed, "对账失败");
        await SeedStatisticStateAsync(new DateTime(2026, 6, 13), SalesStatisticRefreshStatus.Pending);
        var service = CreateServiceWithBrokenPosm();

        var fresh = await service.GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 10), EndDate = new DateTime(2026, 6, 10) },
            null,
            pageIndex: 1,
            pageSize: 50
        );
        var freshEmpty = await service.GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 11), EndDate = new DateTime(2026, 6, 11) },
            null,
            pageIndex: 1,
            pageSize: 50
        );
        var failed = await service.GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 12), EndDate = new DateTime(2026, 6, 12) },
            null,
            pageIndex: 1,
            pageSize: 50
        );
        var pending = await service.GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 13), EndDate = new DateTime(2026, 6, 13) },
            null,
            pageIndex: 1,
            pageSize: 50
        );

        Assert.Equal("P-NO-POSM", Assert.Single(fresh.Products).ProductCode);
        Assert.Empty(freshEmpty.Products);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, freshEmpty.StatisticStatus);
        Assert.Empty(failed.Products);
        Assert.Equal(SalesStatisticRefreshStatus.Failed, failed.StatisticStatus);
        Assert.Empty(pending.Products);
        Assert.Equal(SalesStatisticRefreshStatus.Pending, pending.StatisticStatus);
    }

    [Fact]
    public async Task GetBestSellersAsync_统计状态失败时返回现有统计商品并保留告警()
    {
        await SeedProductAsync("P-FALLBACK", "ITEM-FALLBACK", "BAR-FALLBACK", "回退商品", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await SeedSaleAsync("O-FALLBACK", "D-FALLBACK", "P-FALLBACK", "S1", new DateTime(2026, 6, 1), 8, 16m);
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-FALLBACK",
            ProductName = "回退商品",
            Barcode = "BAR-FALLBACK",
            TotalQuantity = 8,
            TotalAmount = 16m,
            OrderCount = 1,
        }).ExecuteCommandAsync();
        await SeedStatisticStateAsync(new DateTime(2026, 6, 1), SalesStatisticRefreshStatus.Failed, "对账失败");

        var result = await CreateService().GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 6, 1) },
            null,
            pageIndex: 1,
            pageSize: 50
        );

        var product = Assert.Single(result.Products);
        Assert.Equal("P-FALLBACK", product.ProductCode);
        Assert.Equal(8, product.Quantity);
        Assert.Equal(1, result.Total);
        Assert.Equal(SalesStatisticRefreshStatus.Failed, result.StatisticStatus);
        Assert.Contains("对账失败", result.StatisticMessage);
    }

    [Fact]
    public async Task GetBestSellersAsync_Fresh缓存后状态失败应绕过缓存且失败结果不缓存()
    {
        var targetDate = new DateTime(2026, 6, 14);
        await SeedProductAsync("P-STATUS-CACHE", "ITEM-STATUS-CACHE", "BAR-STATUS-CACHE", "状态缓存商品", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = targetDate,
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-STATUS-CACHE",
            ProductName = "状态缓存商品",
            Barcode = "BAR-STATUS-CACHE",
            TotalQuantity = 5,
            TotalAmount = 10m,
            OrderCount = 1,
        }).ExecuteCommandAsync();
        await SeedStatisticStateAsync(targetDate, SalesStatisticRefreshStatus.Fresh);
        var service = CreateService();
        var range = new DateRangeDto { StartDate = targetDate, EndDate = targetDate };

        var fresh = await service.GetBestSellersAsync(range, null, pageIndex: 1, pageSize: 50);
        Assert.Equal(5, Assert.Single(fresh.Products).Quantity);

        await _localDb.Updateable<ProductStoreDailySalesStatistic>()
            .SetColumns(row => row.TotalQuantity == 9)
            .SetColumns(row => row.TotalAmount == 18m)
            .Where(row => row.Date == targetDate && row.ProductCode == "P-STATUS-CACHE")
            .ExecuteCommandAsync();
        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => row.Status == SalesStatisticRefreshStatus.Failed)
            .SetColumns(row => row.ErrorMessage == "对账失败")
            .Where(row => row.StatisticType == SalesStatisticType.ProductStoreDaily && row.Date == targetDate)
            .ExecuteCommandAsync();

        var failed = await service.GetBestSellersAsync(range, null, pageIndex: 1, pageSize: 50);
        Assert.Equal(9, Assert.Single(failed.Products).Quantity);
        Assert.Equal(SalesStatisticRefreshStatus.Failed, failed.StatisticStatus);
        Assert.Equal("对账失败", failed.StatisticMessage);

        await _localDb.Updateable<ProductStoreDailySalesStatistic>()
            .SetColumns(row => row.TotalQuantity == 12)
            .SetColumns(row => row.TotalAmount == 24m)
            .Where(row => row.Date == targetDate && row.ProductCode == "P-STATUS-CACHE")
            .ExecuteCommandAsync();

        var failedAgain = await service.GetBestSellersAsync(range, null, pageIndex: 1, pageSize: 50);
        Assert.Equal(12, Assert.Single(failedAgain.Products).Quantity);
        Assert.Equal(SalesStatisticRefreshStatus.Failed, failedAgain.StatisticStatus);

        await _localDb.Updateable<ProductStoreDailySalesStatistic>()
            .SetColumns(row => row.TotalQuantity == 15)
            .SetColumns(row => row.TotalAmount == 30m)
            .Where(row => row.Date == targetDate && row.ProductCode == "P-STATUS-CACHE")
            .ExecuteCommandAsync();
        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => row.Status == SalesStatisticRefreshStatus.Fresh)
            .SetColumns(row => row.ErrorMessage == null)
            .SetColumns(row => row.LastAggregatedAtUtc == DateTime.UtcNow.AddMinutes(1))
            .Where(row => row.StatisticType == SalesStatisticType.ProductStoreDaily && row.Date == targetDate)
            .ExecuteCommandAsync();

        var refreshed = await service.GetBestSellersAsync(range, null, pageIndex: 1, pageSize: 50);
        Assert.Equal(15, Assert.Single(refreshed.Products).Quantity);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, refreshed.StatisticStatus);
    }

    [Fact]
    public async Task GetBestSellersAsync_Fresh缓存后Pending和Stale均不返回旧排名()
    {
        var targetDate = new DateTime(2026, 6, 15);
        await SeedProductAsync("P-NON-FRESH-CACHE", "ITEM-NON-FRESH-CACHE", "BAR-NON-FRESH-CACHE", "非Fresh缓存商品", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = targetDate,
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-NON-FRESH-CACHE",
            ProductName = "非Fresh缓存商品",
            Barcode = "BAR-NON-FRESH-CACHE",
            TotalQuantity = 5,
            TotalAmount = 10m,
            OrderCount = 1,
        }).ExecuteCommandAsync();
        await SeedStatisticStateAsync(targetDate, SalesStatisticRefreshStatus.Fresh);
        var service = CreateService();
        var range = new DateRangeDto { StartDate = targetDate, EndDate = targetDate };

        var fresh = await service.GetBestSellersAsync(range, null, pageIndex: 1, pageSize: 50);
        Assert.Single(fresh.Products);

        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => row.Status == SalesStatisticRefreshStatus.Pending)
            .Where(row => row.StatisticType == SalesStatisticType.ProductStoreDaily && row.Date == targetDate)
            .ExecuteCommandAsync();

        var pending = await service.GetBestSellersAsync(range, null, pageIndex: 1, pageSize: 50);
        Assert.Empty(pending.Products);
        Assert.Equal(SalesStatisticRefreshStatus.Pending, pending.StatisticStatus);

        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => row.Status == SalesStatisticRefreshStatus.Running)
            .Where(row => row.StatisticType == SalesStatisticType.ProductStoreDaily && row.Date == targetDate)
            .ExecuteCommandAsync();

        var running = await service.GetBestSellersAsync(range, null, pageIndex: 1, pageSize: 50);
        Assert.Empty(running.Products);
        Assert.Equal(SalesStatisticRefreshStatus.Pending, running.StatisticStatus);

        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => row.Status == SalesStatisticRefreshStatus.Stale)
            .Where(row => row.StatisticType == SalesStatisticType.ProductStoreDaily && row.Date == targetDate)
            .ExecuteCommandAsync();

        var stale = await service.GetBestSellersAsync(range, null, pageIndex: 1, pageSize: 50);
        Assert.Empty(stale.Products);
        Assert.Equal(SalesStatisticRefreshStatus.Stale, stale.StatisticStatus);
    }

    [Fact]
    public async Task GetBestSellersAsync_版本化缓存仍可由销售看板清缓存清除()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        var targetDate = new DateTime(2026, 6, 17);
        await SeedProductAsync("P-CLEAR-CACHE", "ITEM-CLEAR-CACHE", "BAR-CLEAR-CACHE", "清缓存商品", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = targetDate,
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-CLEAR-CACHE",
            ProductName = "清缓存商品",
            Barcode = "BAR-CLEAR-CACHE",
            TotalQuantity = 5,
            TotalAmount = 10m,
            OrderCount = 1,
        }).ExecuteCommandAsync();
        await SeedStatisticStateAsync(targetDate, SalesStatisticRefreshStatus.Fresh);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);
        var range = new DateRangeDto { StartDate = targetDate, EndDate = targetDate };

        var cached = await service.GetBestSellersAsync(range, null, pageIndex: 1, pageSize: 50);
        Assert.Equal(5, Assert.Single(cached.Products).Quantity);
        await _localDb.Updateable<ProductStoreDailySalesStatistic>()
            .SetColumns(row => row.TotalQuantity == 9)
            .SetColumns(row => row.TotalAmount == 18m)
            .Where(row => row.Date == targetDate && row.ProductCode == "P-CLEAR-CACHE")
            .ExecuteCommandAsync();
        var beforeClear = await service.GetBestSellersAsync(range, null, pageIndex: 1, pageSize: 50);
        Assert.Equal(5, Assert.Single(beforeClear.Products).Quantity);

        var cacheWarmer = new SalesDashboardCacheWarmer(
            service,
            NullLogger<SalesDashboardCacheWarmer>.Instance,
            cache
        );
        await cacheWarmer.ClearCacheAsync();

        var afterClear = await service.GetBestSellersAsync(range, null, pageIndex: 1, pageSize: 50);
        Assert.Equal(9, Assert.Single(afterClear.Products).Quantity);
    }

    [Fact]
    public async Task GetBestSellersAsync_统计表包含结束日整天销售()
    {
        await SeedProductAsync("P-END-DATE", "ITEM-END-DATE", "BAR-END-DATE", "结束日商品", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await SeedSaleAsync("O-END-DATE", "D-END-DATE", "P-END-DATE", "S1", new DateTime(2026, 6, 8, 18, 30, 0), 5, 25m);
        await SeedBestSellerStatisticsFromPosmAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 8));

        var result = await CreateService().GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 6, 8) },
            null,
            pageIndex: 1,
            pageSize: 50
        );

        var product = Assert.Single(result.Products);
        Assert.Equal("P-END-DATE", product.ProductCode);
        Assert.Equal(5, product.Quantity);
        Assert.Equal(25m, product.SalesAmount);
    }

    [Fact]
    public async Task GetBestSellersAsync_统计表分店过滤使用设备映射参与排名()
    {
        await SeedProductAsync("P-DEVICE-RANK", "ITEM-DEVICE-RANK", "BAR-DEVICE-RANK", "设备分店商品", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await SeedStoreAsync("S1", "Store 1");
        await SeedDeviceAsync("DEVICE-S1", "S1");
        await SeedSaleAsync(
            "O-DEVICE-RANK",
            "D-DEVICE-RANK",
            "P-DEVICE-RANK",
            null,
            new DateTime(2026, 6, 1, 10, 0, 0),
            9,
            45m,
            deviceCode: "DEVICE-S1"
        );
        await SeedBestSellerStatisticsFromPosmAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 1));

        var result = await CreateService().GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 6, 1) },
            new List<string> { "S1" },
            pageIndex: 1,
            pageSize: 50
        );

        var product = Assert.Single(result.Products);
        Assert.Equal("P-DEVICE-RANK", product.ProductCode);
        Assert.Equal(9, product.Quantity);
        Assert.Equal(1, product.BranchSalesCount);
        Assert.Equal("S1", Assert.Single(product.BranchSales).BranchCode);
    }

    [Fact]
    public async Task UpdateProductStoreDailyStatistics_按分店进货价优先计算毛利并写入状态()
    {
        await SeedProductAsync("P-MARGIN", "ITEM-MARGIN", "BAR-MARGIN", "毛利商品", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await _localDb.Updateable<Product>()
            .SetColumns(p => p.PurchasePrice == 9m)
            .Where(p => p.ProductCode == "P-MARGIN")
            .ExecuteCommandAsync();
        await _localDb.Updateable<WarehouseProduct>()
            .SetColumns(p => p.ImportPrice == 8m)
            .Where(p => p.ProductCode == "P-MARGIN")
            .ExecuteCommandAsync();
        await _localDb.Insertable(new StoreRetailPrice
        {
            UUID = "srp-margin",
            StoreCode = "S1",
            ProductCode = "P-MARGIN",
            SupplierCode = "200",
            PurchasePrice = 2m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedSaleAsync("O-MARGIN", "D-MARGIN", "P-MARGIN", "S1", new DateTime(2026, 6, 1), 5, 25m);
        // 明确订单支付金额，验证毛利仍按生产支付口径计算。
        await _posmDb.Insertable(new PaymentDetail
        {
            PaymentGuid = "PAY-MARGIN",
            OrderGuid = "O-MARGIN",
            Amount = 25m,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new StoreSalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S1",
            BranchName = "Store 1",
            TotalQuantity = 5,
            TotalAmount = 25m,
            OrderCount = 1,
            CustomerCount = 1,
            AverageOrderValue = 25m,
        }).ExecuteCommandAsync();

        await CreateStatisticsJobService().UpdateProductStoreDailyStatistics(new DateTime(2026, 6, 1));

        var stat = await _localDb.Queryable<ProductStoreDailySalesStatistic>()
            .Where(x => x.Date == new DateTime(2026, 6, 1) && x.ProductCode == "P-MARGIN")
            .FirstAsync();
        Assert.NotNull(stat);
        Assert.Equal(25m, stat.TotalAmount);
        Assert.Equal("StoreRetailPrice", stat.CostSource);
        Assert.Equal(2m, stat.UnitCostSnapshot);
        Assert.Equal(10m, stat.TotalCost);
        Assert.Equal(15m, stat.GrossProfit);
        Assert.Equal(0.6m, stat.GrossMarginRate);

        var state = await _localDb.Queryable<SalesStatisticRefreshState>()
            .Where(x => x.StatisticType == SalesStatisticType.ProductStoreDaily && x.Date == new DateTime(2026, 6, 1))
            .FirstAsync();
        Assert.NotNull(state);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, state.Status);
        Assert.Equal("POSM_LOCAL", state.SourceTimeZone);
        Assert.NotNull(state.LastAggregatedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateProductStoreDailyStatistics_直写开关只改变国内货的SupplierCode不改变成本(bool writeDirectChinaSupplierCode)
    {
        var date = new DateTime(2026, 6, 2);
        await SeedDirectCodeFixtureAsync(date);

        await CreateStatisticsJobService(writeDirectChinaSupplierCode).UpdateProductStoreDailyStatistics(date);

        var rows = await _localDb.Queryable<ProductStoreDailySalesStatistic>()
            .Where(x => x.Date == date)
            .ToListAsync();
        Assert.Equal(5, rows.Count);
        var resolved = rows.Single(x => x.ProductCode == "P-W-RESOLVED");
        // 开关关闭时与改造前完全一致：国内货仍写 200。
        Assert.Equal(writeDirectChinaSupplierCode ? "CN-W" : "200", resolved.SupplierCode);
        // 成本始终按本地供应商 200 匹配分店价格表；拿 CN-W 去匹配会落到商品进价 9。
        Assert.Equal("StoreRetailPrice", resolved.CostSource);
        Assert.Equal(2m, resolved.UnitCostSnapshot);
        Assert.Equal(10m, resolved.TotalCost);
        Assert.Equal(15m, resolved.GrossProfit);
        // 解析不出国内供应商、编码不在国内供应商目录里、国内商品或仓库商品已软删除的，都保持 200（与映射同步的规则一致）。
        Assert.All(
            new[] { "P-W-NO-RELATION", "P-W-ORPHAN", "P-W-DELETED", "P-W-WAREHOUSE-DELETED" },
            productCode => Assert.Equal("200", rows.Single(x => x.ProductCode == productCode).SupplierCode));

        // 两张拆分表的口径不随开关变化：澳洲侧国内货恒为 200；国内侧直写行不再需要 POSM 映射。
        var australian = await _localDb.Queryable<AustralianSupplierStoreSalesDetail>().Where(x => x.Date == date).ToListAsync();
        Assert.Equal(65m, Assert.Single(australian, x => x.SupplierCode == "200").TotalAmount);
        var china = await _localDb.Queryable<ChinaSupplierStoreSalesDetail>().Where(x => x.Date == date).ToListAsync();
        if (writeDirectChinaSupplierCode)
            Assert.Equal(25m, Assert.Single(china, x => x.SupplierCode == "CN-W").TotalAmount);
        else
            Assert.Empty(china);

        var state = await _localDb.Queryable<SalesStatisticRefreshState>()
            .Where(x => x.StatisticType == SalesStatisticType.ProductStoreDaily && x.Date == date)
            .FirstAsync();
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, state.Status);
    }

    [Fact]
    public async Task UpdateProductStoreDailyStatistics_历史日期重算时归属编码变化不丢历史成本快照()
    {
        var date = new DateTime(2026, 6, 3);
        await SeedDirectCodeFixtureAsync(date);

        // 第一次：开关关闭，国内货写 200，成本取当时的分店进价 2。
        await CreateStatisticsJobService().UpdateProductStoreDailyStatistics(date);
        // 之后分店进价涨到 3。历史日期重算不能用它覆盖已审计的成本快照。
        await _localDb.Updateable<StoreRetailPrice>()
            .SetColumns(x => x.PurchasePrice == 3m)
            .Where(x => x.ProductCode == "P-W-RESOLVED")
            .ExecuteCommandAsync();

        async Task AssertSnapshotAsync(string expectedSupplierCode)
        {
            var row = Assert.Single(await _localDb.Queryable<ProductStoreDailySalesStatistic>()
                .Where(x => x.Date == date && x.ProductCode == "P-W-RESOLVED")
                .ToListAsync());
            Assert.Equal(expectedSupplierCode, row.SupplierCode);
            Assert.Equal(2m, row.UnitCostSnapshot);
            Assert.Equal(10m, row.TotalCost);
            Assert.Equal(15m, row.GrossProfit);
        }

        // 200 -> 直写：打开开关后重算，行键从 200 变成 CN-W。
        await CreateStatisticsJobService(writeDirectChinaSupplierCode: true).UpdateProductStoreDailyStatistics(date);
        await AssertSnapshotAsync("CN-W");

        // 直写 -> 另一国内编码：主数据里换了国内供应商后再重算。
        await _localDb.Updateable<DomesticProduct>()
            .SetColumns(x => x.SupplierCode == "CN-W2")
            .Where(x => x.ProductCode == "P-W-RESOLVED")
            .ExecuteCommandAsync();
        await CreateStatisticsJobService(writeDirectChinaSupplierCode: true).UpdateProductStoreDailyStatistics(date);
        await AssertSnapshotAsync("CN-W2");

        // 直写 -> 200：关掉开关后重算（回退路径）。
        await CreateStatisticsJobService().UpdateProductStoreDailyStatistics(date);
        await AssertSnapshotAsync("200");
    }

    [Fact]
    public async Task UpdateProductStoreDailyStatistics_当天营业中快照路径同样直写国内供应商编码()
    {
        // 当天走的是独立的营业中快照读取入口，开关必须一并传到。
        var today = SalesStatisticsBusinessDate.Today();
        await SeedDirectCodeFixtureAsync(today);

        await CreateStatisticsJobService(writeDirectChinaSupplierCode: true).UpdateProductStoreDailyStatistics(today);

        var resolved = Assert.Single(await _localDb.Queryable<ProductStoreDailySalesStatistic>()
            .Where(x => x.ProductCode == "P-W-RESOLVED")
            .ToListAsync());
        Assert.Equal("CN-W", resolved.SupplierCode);
        Assert.Equal("StoreRetailPrice", resolved.CostSource);
        Assert.Equal(10m, resolved.TotalCost);
    }

    /// <summary>
    /// 分店 S1 在指定日期卖出五个本地供应商为 200 的商品（每行 5 件），用于直写开关的端到端用例：
    /// P-W-RESOLVED 能解析出国内供应商 CN-W，有按 200 登记的分店进价 2（商品进价 9）；
    /// P-W-NO-RELATION 没有国内商品关系；P-W-ORPHAN 的国内编码不在国内供应商目录里；
    /// P-W-DELETED 的国内商品已软删除；P-W-WAREHOUSE-DELETED 的仓库商品已软删除。
    /// </summary>
    private async Task SeedDirectCodeFixtureAsync(DateTime date)
    {
        await _localDb.Insertable(new List<ChinaSupplier>
        {
            new() { Guid = "cn-w", SupplierCode = "CN-W", SupplierName = "直写国内供应商" },
            new() { Guid = "cn-w2", SupplierCode = "CN-W2", SupplierName = "变更后的国内供应商" },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new HBLocalSupplier { Guid = "local-200-w", LocalSupplierCode = "200", Name = "hotbargain" })
            .ExecuteCommandAsync();

        var products = new (string ProductCode, string? ChinaSupplierCode, bool RelationDeleted, decimal Amount)[]
        {
            ("P-W-RESOLVED", "CN-W", false, 25m),
            ("P-W-NO-RELATION", null, false, 10m),
            ("P-W-ORPHAN", "CN-NOT-IN-CATALOG", false, 10m),
            ("P-W-DELETED", "CN-W", true, 10m),
            ("P-W-WAREHOUSE-DELETED", "CN-W", false, 10m),
        };
        foreach (var product in products)
        {
            await SeedProductAsync(product.ProductCode, $"IT-{product.ProductCode}", null, product.ProductCode, true, true, 1);
            if (product.ChinaSupplierCode != null)
            {
                await _localDb.Insertable(new DomesticProduct
                {
                    ProductCode = product.ProductCode,
                    SupplierCode = product.ChinaSupplierCode,
                    IsDeleted = product.RelationDeleted,
                }).ExecuteCommandAsync();
            }
            await SeedSaleAsync($"O-{product.ProductCode}-{date:MMdd}", $"D-{product.ProductCode}-{date:MMdd}", product.ProductCode, "S1", date.AddHours(10), 5, product.Amount);
            await _posmDb.Insertable(new PaymentDetail
            {
                PaymentGuid = $"PAY-{product.ProductCode}-{date:MMdd}",
                OrderGuid = $"O-{product.ProductCode}-{date:MMdd}",
                Amount = product.Amount,
            }).ExecuteCommandAsync();
        }
        await _localDb.Updateable<WarehouseProduct>()
            .SetColumns(p => p.IsDeleted == true)
            .Where(p => p.ProductCode == "P-W-WAREHOUSE-DELETED")
            .ExecuteCommandAsync();
        await _localDb.Updateable<Product>()
            .SetColumns(p => p.PurchasePrice == 9m)
            .Where(p => p.ProductCode == "P-W-RESOLVED")
            .ExecuteCommandAsync();
        await _localDb.Insertable(new StoreRetailPrice
        {
            UUID = $"srp-w-{date:MMdd}",
            StoreCode = "S1",
            ProductCode = "P-W-RESOLVED",
            SupplierCode = "200",
            PurchasePrice = 2m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new StoreSalesStatistic
        {
            Date = date,
            BranchCode = "S1",
            BranchName = "Store 1",
            TotalQuantity = 25,
            TotalAmount = 65m,
            OrderCount = 5,
            CustomerCount = 5,
            AverageOrderValue = 13m,
        }).ExecuteCommandAsync();
    }

    [Fact]
    public async Task GetBestSellersAsync_商品统计重算中返回空结果避免读取旧统计()
    {
        await SeedProductAsync("P-QUEUED", "ITEM-QUEUED", "BAR-QUEUED", "重算商品", productIsActive: true, warehouseIsActive: true, minOrderQuantity: 1);
        await SeedSaleAsync("O-QUEUED", "D-QUEUED", "P-QUEUED", "S1", new DateTime(2026, 6, 1), 9, 18m);
        await SeedStatisticStateAsync(new DateTime(2026, 6, 1), SalesStatisticRefreshStatus.Queued);
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-QUEUED",
            ProductName = "旧统计商品",
            Barcode = "BAR-QUEUED",
            TotalQuantity = 100,
            TotalAmount = 200m,
            OrderCount = 1,
            CostSource = "Missing",
            UpdateTime = DateTime.Now,
        }).ExecuteCommandAsync();

        var result = await CreateService().GetBestSellersAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 6, 1) },
            null,
            pageIndex: 1,
            pageSize: 10
        );

        Assert.Empty(result.Products);
        Assert.Equal(0, result.Total);
        Assert.Equal(SalesStatisticRefreshStatus.Pending, result.StatisticStatus);
    }

    [Fact]
    public async Task GetProductStoreDailyStatisticStates_按日期状态筛选返回状态列表()
    {
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = new DateTime(2026, 6, 1),
            Status = SalesStatisticRefreshStatus.Fresh,
            LastSourceUploadTime = new DateTime(2026, 6, 1, 23, 0, 0),
            SourceTimeZone = "POSM_LOCAL",
            LastAggregatedAtUtc = new DateTime(2026, 6, 1, 14, 0, 0),
            LastCheckedAtUtc = new DateTime(2026, 6, 1, 14, 5, 0),
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = new DateTime(2026, 6, 2),
            Status = SalesStatisticRefreshStatus.Failed,
            SourceTimeZone = "POSM_LOCAL",
            ErrorMessage = "对账失败",
        }).ExecuteCommandAsync();

        var result = await CreateStatisticsController().GetProductStoreDailyStatisticStates(
            SalesStatisticType.ProductStoreDaily,
            new DateTime(2026, 6, 1),
            new DateTime(2026, 6, 1),
            SalesStatisticRefreshStatus.Fresh
        );

        var ok = AssertOk(result);
        var data = ExtractAnonymousData<List<SalesStatisticRefreshStateListItemDto>>(ok.Value);
        var row = Assert.Single(data);
        Assert.Equal(new DateTime(2026, 6, 1), row.Date);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, row.Status);
        Assert.Equal(new DateTime(2026, 6, 1, 23, 0, 0), row.LastSourceUploadTime);
    }

    [Fact]
    public async Task GetProductStoreDailyStatisticStates_大小写不同的筛选值仍能匹配()
    {
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = new DateTime(2026, 6, 1),
            Status = SalesStatisticRefreshStatus.Fresh,
        }).ExecuteCommandAsync();

        var result = await CreateStatisticsController().GetProductStoreDailyStatisticStates(
            "productstoredaily",
            new DateTime(2026, 6, 1),
            new DateTime(2026, 6, 1),
            "fresh"
        );

        var ok = AssertOk(result);
        var data = ExtractAnonymousData<List<SalesStatisticRefreshStateListItemDto>>(ok.Value);
        var row = Assert.Single(data);
        Assert.Equal(SalesStatisticType.ProductStoreDaily, row.StatisticType);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, row.Status);
    }

    [Fact]
    public async Task GetProductStoreDailyStatisticSummary_返回单日汇总和对账状态()
    {
        await SeedStatisticStateAsync(new DateTime(2026, 6, 1), SalesStatisticRefreshStatus.Fresh);
        await _localDb.Insertable(new StoreSalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S1",
            BranchName = "Store 1",
            TotalQuantity = 3,
            TotalAmount = 15m,
            OrderCount = 2,
            CustomerCount = 2,
            AverageOrderValue = 15m,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new StoreSalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S2",
            BranchName = "Store 2",
            TotalQuantity = 5,
            TotalAmount = 25m,
            OrderCount = 2,
            CustomerCount = 2,
            AverageOrderValue = 12.5m,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-SUM-1",
            TotalQuantity = 3,
            TotalAmount = 15m,
            GrossProfit = 6m,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S2",
            SupplierCode = "200",
            ProductCode = "P-SUM-2",
            TotalQuantity = 5,
            TotalAmount = 25m,
            GrossProfit = 10m,
        }).ExecuteCommandAsync();

        var result = await CreateStatisticsController()
            .GetProductStoreDailyStatisticSummary(new DateTime(2026, 6, 1));

        var ok = AssertOk(result);
        var data = ExtractAnonymousData<ProductStoreDailyStatisticSummaryDto>(ok.Value);
        Assert.Equal(2, data.RecordCount);
        Assert.Equal(8, data.TotalQuantity);
        Assert.Equal(40m, data.TotalAmount);
        Assert.Equal(16m, data.GrossProfit);
        Assert.Equal("Passed", data.ReconciliationStatus);
        Assert.Equal("Passed", data.SalesReconciliationStatus);
        Assert.Equal(40m, data.ProductTotalAmount);
        Assert.Equal(40m, data.StoreTotalAmount);
        Assert.Equal(0m, data.AmountDifference);
        Assert.Equal(8, data.ProductTotalQuantity);
        Assert.Equal(8, data.StoreTotalQuantity);
        Assert.Equal(0, data.QuantityDifference);
    }

    [Fact]
    public async Task GetProductStoreDailyStatisticSummary_零销售Fresh日应返回零汇总和通过状态()
    {
        var targetDate = new DateTime(2026, 6, 2);
        await SeedStatisticStateAsync(targetDate, SalesStatisticRefreshStatus.Fresh);

        var result = await CreateStatisticsController()
            .GetProductStoreDailyStatisticSummary(targetDate);

        var ok = AssertOk(result);
        var data = ExtractAnonymousData<ProductStoreDailyStatisticSummaryDto>(ok.Value);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, data.Status);
        Assert.Equal(0, data.RecordCount);
        Assert.Equal(0, data.TotalQuantity);
        Assert.Equal(0m, data.TotalAmount);
        Assert.Equal("Passed", data.ReconciliationStatus);
        Assert.Equal("Passed", data.SalesReconciliationStatus);
        Assert.Equal(0m, data.ProductTotalAmount);
        Assert.Equal(0m, data.StoreTotalAmount);
        Assert.Equal(0m, data.AmountDifference);
        Assert.Equal(0, data.ProductTotalQuantity);
        Assert.Equal(0, data.StoreTotalQuantity);
        Assert.Equal(0, data.QuantityDifference);
    }

    [Fact]
    public async Task GetProductStoreDailyStatisticSummary_零行Failed状态不应显示对账通过()
    {
        var targetDate = new DateTime(2026, 6, 3);
        await SeedStatisticStateAsync(targetDate, SalesStatisticRefreshStatus.Failed);

        var result = await CreateStatisticsController()
            .GetProductStoreDailyStatisticSummary(targetDate);

        var ok = AssertOk(result);
        var data = ExtractAnonymousData<ProductStoreDailyStatisticSummaryDto>(ok.Value);
        Assert.Equal(SalesStatisticRefreshStatus.Failed, data.Status);
        Assert.Equal("Failed", data.ReconciliationStatus);
        Assert.Equal("Failed", data.SalesReconciliationStatus);
    }

    [Fact]
    public async Task GetProductStoreDailyStatisticSummary_返回营业额差异和空供应商诊断()
    {
        await SeedStatisticStateAsync(
            new DateTime(2026, 6, 1),
            SalesStatisticRefreshStatus.Failed,
            "商品统计与分店营业额统计不一致: 2026-06-01 S1, 商品金额 30, 分店营业额 160, 金额差 130, 未匹配供应商金额 15.5, 未匹配供应商数量 3, 未匹配商品数 2"
        );
        await _localDb.Insertable(new StoreSalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S1",
            BranchName = "Store 1",
            TotalQuantity = 7,
            TotalAmount = 160m,
            OrderCount = 2,
            CustomerCount = 2,
            AverageOrderValue = 80m,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-DIFF-1",
            TotalQuantity = 4,
            TotalAmount = 30m,
            GrossProfit = 6m,
        }).ExecuteCommandAsync();

        var result = await CreateStatisticsController()
            .GetProductStoreDailyStatisticSummary(new DateTime(2026, 6, 1));

        var ok = AssertOk(result);
        var data = ExtractAnonymousData<ProductStoreDailyStatisticSummaryDto>(ok.Value);
        Assert.Equal("Failed", data.ReconciliationStatus);
        Assert.Equal("Failed", data.SalesReconciliationStatus);
        Assert.Equal(30m, data.ProductTotalAmount);
        Assert.Equal(160m, data.StoreTotalAmount);
        Assert.Equal(130m, data.AmountDifference);
        Assert.Equal(4, data.ProductTotalQuantity);
        Assert.Equal(7, data.StoreTotalQuantity);
        Assert.Equal(3, data.QuantityDifference);
        Assert.Equal(15.5m, data.UnmatchedSupplierAmount);
        Assert.Equal(3, data.UnmatchedSupplierQuantity);
        Assert.Equal(2, data.UnmatchedSupplierProductCount);
    }

    [Fact]
    public async Task GetProductStoreDailyStatisticSummary_分店金额差异在绝对容差内时营业额对账Passed()
    {
        await SeedStatisticStateAsync(new DateTime(2026, 6, 1), SalesStatisticRefreshStatus.Fresh);
        await _localDb.Insertable(new StoreSalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S1",
            BranchName = "Store 1",
            TotalQuantity = 10,
            TotalAmount = 1000m,
            OrderCount = 1,
            CustomerCount = 1,
            AverageOrderValue = 1000m,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-TOLERANCE-1",
            TotalQuantity = 10,
            TotalAmount = 920m,
        }).ExecuteCommandAsync();

        var result = await CreateStatisticsController()
            .GetProductStoreDailyStatisticSummary(new DateTime(2026, 6, 1));

        var ok = AssertOk(result);
        var data = ExtractAnonymousData<ProductStoreDailyStatisticSummaryDto>(ok.Value);
        Assert.Equal(920m, data.ProductTotalAmount);
        Assert.Equal(1000m, data.StoreTotalAmount);
        Assert.Equal(80m, data.AmountDifference);
        Assert.Equal("Passed", data.SalesReconciliationStatus);
    }

    [Fact]
    public async Task GetProductStoreDailyStatisticSummary_分店差异互相抵消时营业额对账仍Failed()
    {
        await SeedStatisticStateAsync(new DateTime(2026, 6, 1), SalesStatisticRefreshStatus.Failed);
        await _localDb.Insertable(new StoreSalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S1",
            BranchName = "Store 1",
            TotalQuantity = 10,
            TotalAmount = 100m,
            OrderCount = 1,
            CustomerCount = 1,
            AverageOrderValue = 100m,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new StoreSalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S2",
            BranchName = "Store 2",
            TotalQuantity = 10,
            TotalAmount = 400m,
            OrderCount = 1,
            CustomerCount = 1,
            AverageOrderValue = 400m,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-OFFSET-1",
            TotalQuantity = 5,
            TotalAmount = 250m,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S2",
            SupplierCode = "200",
            ProductCode = "P-OFFSET-2",
            TotalQuantity = 5,
            TotalAmount = 250m,
        }).ExecuteCommandAsync();

        var result = await CreateStatisticsController()
            .GetProductStoreDailyStatisticSummary(new DateTime(2026, 6, 1));

        var ok = AssertOk(result);
        var data = ExtractAnonymousData<ProductStoreDailyStatisticSummaryDto>(ok.Value);
        Assert.Equal(500m, data.ProductTotalAmount);
        Assert.Equal(500m, data.StoreTotalAmount);
        Assert.Equal(300m, data.AmountDifference);
        Assert.Equal("Failed", data.SalesReconciliationStatus);
    }

    [Fact]
    public async Task GetProductStoreDailyStatisticSummary_非Fresh状态有旧统计行时不显示对账通过()
    {
        await SeedStatisticStateAsync(new DateTime(2026, 6, 1), SalesStatisticRefreshStatus.Stale);
        await _localDb.Insertable(new StoreSalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S1",
            BranchName = "Store 1",
            TotalQuantity = 3,
            TotalAmount = 15m,
            OrderCount = 1,
            CustomerCount = 1,
            AverageOrderValue = 15m,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = new DateTime(2026, 6, 1),
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-STALE-1",
            TotalQuantity = 3,
            TotalAmount = 15m,
            GrossProfit = 6m,
        }).ExecuteCommandAsync();

        var result = await CreateStatisticsController()
            .GetProductStoreDailyStatisticSummary(new DateTime(2026, 6, 1));

        var ok = AssertOk(result);
        var data = ExtractAnonymousData<ProductStoreDailyStatisticSummaryDto>(ok.Value);
        Assert.Equal(SalesStatisticRefreshStatus.Stale, data.Status);
        Assert.Equal("Pending", data.ReconciliationStatus);
        Assert.Equal("Pending", data.SalesReconciliationStatus);
    }

    [Fact]
    public async Task GetProductStoreDailyStatisticSummary_补充退货应按净额对账()
    {
        var targetDate = new DateTime(2026, 6, 16);
        await SeedStatisticStateAsync(targetDate, SalesStatisticRefreshStatus.Fresh);
        await _localDb.Insertable(new StoreSalesStatistic
        {
            Date = targetDate,
            BranchCode = "S1",
            BranchName = "Store 1",
            TotalQuantity = 2,
            TotalAmount = 20m,
            OrderCount = 1,
            CustomerCount = 1,
            AverageOrderValue = 20m,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = targetDate,
            BranchCode = "S1",
            SupplierCode = "200",
            ProductCode = "P-SUM-RETURN",
            TotalQuantity = 1,
            TotalAmount = 10m,
        }).ExecuteCommandAsync();
        await SeedSaleAsync(
            "O-SUM-SALE",
            "D-SUM-SALE",
            "P-SUM-RETURN",
            "S1",
            targetDate.AddHours(9),
            2,
            20m
        );
        await SeedReturnRecordAsync(
            "O-SUM-RETURN",
            "D-SUM-RETURN",
            "O-SUM-SALE",
            "D-SUM-SALE",
            "P-SUM-RETURN",
            "S1",
            targetDate.AddHours(10),
            1m,
            10m
        );

        var result = await CreateStatisticsController()
            .GetProductStoreDailyStatisticSummary(targetDate);

        var ok = AssertOk(result);
        var data = ExtractAnonymousData<ProductStoreDailyStatisticSummaryDto>(ok.Value);
        Assert.Equal("Passed", data.SalesReconciliationStatus);
        Assert.Equal(10m, data.ProductTotalAmount);
        Assert.Equal(10m, data.StoreTotalAmount);
        Assert.Equal(0m, data.AmountDifference);
        Assert.Equal(1, data.ProductTotalQuantity);
        Assert.Equal(1, data.StoreTotalQuantity);
        Assert.Equal(0, data.QuantityDifference);
    }

    [Fact]
    public async Task BatchProductStoreDailyStatistics_超过范围上限返回BadRequest()
    {
        var result = await CreateStatisticsController().BatchProductStoreDailyStatistics(
            new BatchProductStoreDailyUpdateRequest
            {
                StartDate = new DateTime(2026, 1, 1),
                EndDate = new DateTime(2026, 2, 15),
            }
        );

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("一次最多重算", badRequest.Value?.ToString());
    }

    [Fact]
    public void BatchProductStoreDailyUpdateRequest_MaxConcurrency默认值为3()
    {
        var request = new BatchProductStoreDailyUpdateRequest();

        Assert.Equal(3, request.MaxConcurrency);
    }

    [Fact]
    public async Task TriggerProductStoreDailyStatistics_提交后立即返回排队日期()
    {
        var result = await CreateStatisticsController().TriggerProductStoreDailyStatistics(
            new ProductStoreDailyJobTriggerRequest
            {
                Date = new DateTime(2026, 6, 1),
            }
        );

        var ok = AssertOk(result);
        Assert.Equal(SalesStatisticRefreshStatus.Queued, ReadAnonymousProperty<string>(ok.Value, "status"));
        Assert.Contains("2026-06-01", ReadAnonymousProperty<List<string>>(ok.Value, "submittedDates"));
        Assert.Equal(
            ReadAnonymousProperty<Guid>(ok.Value, "taskId"),
            ReadAnonymousProperty<Guid>(ok.Value, "jobId")
        );
    }

    [Fact]
    public async Task BatchProductStoreDailyStatistics_执行中日期不重复提交()
    {
        var controller = CreateStatisticsController();
        await controller.TriggerProductStoreDailyStatistics(new ProductStoreDailyJobTriggerRequest
        {
            Date = new DateTime(2026, 6, 1),
        });

        var result = await controller.BatchProductStoreDailyStatistics(
            new BatchProductStoreDailyUpdateRequest
            {
                StartDate = new DateTime(2026, 6, 1),
                EndDate = new DateTime(2026, 6, 1),
            }
        );

        var ok = AssertOk(result);
        Assert.Empty(ReadAnonymousProperty<List<string>>(ok.Value, "submittedDates"));
        Assert.Contains("2026-06-01", ReadAnonymousProperty<List<string>>(ok.Value, "skippedDates"));
    }

    [Fact]
    public async Task BatchProductStoreDailyStatistics_允许指定MaxConcurrency并返回排队日期()
    {
        var result = await CreateStatisticsController().BatchProductStoreDailyStatistics(
            new BatchProductStoreDailyUpdateRequest
            {
                StartDate = new DateTime(2026, 6, 2),
                EndDate = new DateTime(2026, 6, 2),
                MaxConcurrency = 4,
            }
        );

        var ok = AssertOk(result);
        Assert.Equal(SalesStatisticRefreshStatus.Queued, ReadAnonymousProperty<string>(ok.Value, "status"));
        Assert.Contains("2026-06-02", ReadAnonymousProperty<List<string>>(ok.Value, "submittedDates"));
    }

    [Fact]
    public async Task UpdateStoreStatistics_分店销量使用明细数量而不是订单头ItemCount()
    {
        await SeedStoreAsync("S1", "Store 1");
        await SeedPosmOrderAsync(
            "O-STORE-QTY",
            "S1",
            new DateTime(2026, 6, 1),
            itemCount: 1,
            details: new[]
            {
                ("D-STORE-QTY-1", "P-STORE-QTY-1", 2, 20m),
                ("D-STORE-QTY-2", "P-STORE-QTY-2", 3, 30m),
            },
            payments: new[] { ("PAY-STORE-QTY-1", 50m) }
        );

        await CreateStatisticsJobService().UpdateStoreStatistics(new DateTime(2026, 6, 1));

        var stat = await _localDb.Queryable<StoreSalesStatistic>()
            .Where(s => s.Date == new DateTime(2026, 6, 1) && s.BranchCode == "S1")
            .FirstAsync();
        Assert.NotNull(stat);
        Assert.Equal(5, stat.TotalQuantity);
        Assert.Equal(50m, stat.TotalAmount);
        Assert.Equal(1, stat.OrderCount);
        Assert.Equal(1, stat.CustomerCount);
    }

    [Fact]
    public async Task UpdateStoreStatistics_多支付明细不重复放大销量和订单数()
    {
        await SeedStoreAsync("S1", "Store 1");
        await SeedPosmOrderAsync(
            "O-MULTI-PAY",
            "S1",
            new DateTime(2026, 6, 1),
            itemCount: 1,
            details: new[] { ("D-MULTI-PAY-1", "P-MULTI-PAY-1", 4, 40m) },
            payments: new[]
            {
                ("PAY-MULTI-PAY-1", 25m),
                ("PAY-MULTI-PAY-2", 15m),
            }
        );

        await CreateStatisticsJobService().UpdateStoreStatistics(new DateTime(2026, 6, 1));

        var stat = await _localDb.Queryable<StoreSalesStatistic>()
            .Where(s => s.Date == new DateTime(2026, 6, 1) && s.BranchCode == "S1")
            .FirstAsync();
        Assert.NotNull(stat);
        Assert.Equal(4, stat.TotalQuantity);
        Assert.Equal(40m, stat.TotalAmount);
        Assert.Equal(1, stat.OrderCount);
        Assert.Equal(1, stat.CustomerCount);
    }

    [Fact]
    public async Task UpdateStoreStatistics_空订单分店时使用设备分店并支持分店过滤()
    {
        await SeedStoreAsync("S2", "Store 2");
        await SeedDeviceAsync("POS-S2", "S2");
        await SeedPosmOrderAsync(
            "O-DEVICE-STORE-STAT",
            null,
            new DateTime(2026, 6, 1),
            itemCount: 1,
            details: new[] { ("D-DEVICE-STORE-STAT-1", "P-DEVICE-STORE-STAT-1", 6, 60m) },
            payments: new[] { ("PAY-DEVICE-STORE-STAT-1", 60m) },
            deviceCode: "POS-S2"
        );

        await CreateStatisticsJobService().UpdateStoreStatistics(
            new DateTime(2026, 6, 1),
            new List<string> { "S2" }
        );

        var stat = await _localDb.Queryable<StoreSalesStatistic>()
            .Where(s => s.Date == new DateTime(2026, 6, 1) && s.BranchCode == "S2")
            .FirstAsync();
        Assert.NotNull(stat);
        Assert.Equal("Store 2", stat.BranchName);
        Assert.Equal(6, stat.TotalQuantity);
        Assert.Equal(60m, stat.TotalAmount);
    }

    [Fact]
    public async Task GetBestSellers_普通用户传入请求分店时仍查询全平台热销榜()
    {
        List<string>? capturedBranchCodes = null;
        var serviceMock = new Mock<ISalesDashboardReactService>();
        serviceMock
            .Setup(x => x.GetBestSellersAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<int>(),
                It.IsAny<int>()
            ))
            .Callback<DateRangeDto, List<string>?, int, int>((_, branchCodes, _, _) => capturedBranchCodes = branchCodes)
            .ReturnsAsync(new BestSellerResponseDto());

        var userServiceMock = new Mock<IUserService>();
        userServiceMock
            .Setup(x => x.GetUserByGuidAsync("user-1"))
            .ReturnsAsync(ApiResponse<UserDetailDto>.OK(new UserDetailDto
            {
                UserGUID = "user-1",
                Username = "tester",
                Stores = new List<UserStoreDto>
                {
                    new() { StoreCode = "S1" },
                    new() { StoreCode = "S3" },
                },
            }));

        var controller = CreateController(serviceMock.Object, userServiceMock.Object);

        var response = await controller.GetBestSellers(
            new DateTime(2026, 6, 1),
            new DateTime(2026, 6, 8),
            new List<string> { "S1", "S2" },
            pageIndex: 1,
            pageSize: 50
        );

        Assert.IsType<OkObjectResult>(response);
        Assert.Null(capturedBranchCodes);
    }

    [Fact]
    public async Task GetBestSellers_普通用户请求无权限分店时仍查询全平台热销榜()
    {
        List<string>? capturedBranchCodes = new List<string> { "unexpected" };
        var serviceMock = new Mock<ISalesDashboardReactService>();
        serviceMock
            .Setup(x => x.GetBestSellersAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<int>(),
                It.IsAny<int>()
            ))
            .Callback<DateRangeDto, List<string>?, int, int>((_, branchCodes, _, _) => capturedBranchCodes = branchCodes)
            .ReturnsAsync(new BestSellerResponseDto());

        var userServiceMock = new Mock<IUserService>();
        userServiceMock
            .Setup(x => x.GetUserByGuidAsync("user-1"))
            .ReturnsAsync(ApiResponse<UserDetailDto>.OK(new UserDetailDto
            {
                UserGUID = "user-1",
                Username = "tester",
                Stores = new List<UserStoreDto>
                {
                    new() { StoreCode = "S3" },
                },
            }));

        var controller = CreateController(serviceMock.Object, userServiceMock.Object);

        var response = await controller.GetBestSellers(
            new DateTime(2026, 6, 1),
            new DateTime(2026, 6, 8),
            new List<string> { "S1", "S2" },
            pageIndex: 2,
            pageSize: 50
        );

        serviceMock.Verify(
            x => x.GetBestSellersAsync(It.IsAny<DateRangeDto>(), It.IsAny<List<string>?>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Once
        );
        Assert.IsType<OkObjectResult>(response);
        Assert.Null(capturedBranchCodes);
    }

    [Fact]
    public async Task GetBestSellers_普通用户没有关联分店时仍查询全平台热销榜()
    {
        List<string>? capturedBranchCodes = new List<string> { "unexpected" };
        var serviceMock = new Mock<ISalesDashboardReactService>();
        serviceMock
            .Setup(x => x.GetBestSellersAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<int>(),
                It.IsAny<int>()
            ))
            .Callback<DateRangeDto, List<string>?, int, int>((_, branchCodes, _, _) => capturedBranchCodes = branchCodes)
            .ReturnsAsync(new BestSellerResponseDto());

        var userServiceMock = new Mock<IUserService>();
        userServiceMock
            .Setup(x => x.GetUserByGuidAsync("user-1"))
            .ReturnsAsync(ApiResponse<UserDetailDto>.OK(new UserDetailDto
            {
                UserGUID = "user-1",
                Username = "tester",
                Stores = new List<UserStoreDto>(),
            }));

        var controller = CreateController(serviceMock.Object, userServiceMock.Object);

        var response = await controller.GetBestSellers(
            new DateTime(2026, 6, 1),
            new DateTime(2026, 6, 8),
            branchCodes: null,
            pageIndex: 1,
            pageSize: 50
        );

        serviceMock.Verify(
            x => x.GetBestSellersAsync(It.IsAny<DateRangeDto>(), It.IsAny<List<string>?>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Once
        );
        Assert.IsType<OkObjectResult>(response);
        Assert.Null(capturedBranchCodes);
    }

    private async Task SeedProductAsync(
        string productCode,
        string itemNumber,
        string? barcode,
        string name,
        bool productIsActive,
        bool warehouseIsActive,
        int minOrderQuantity,
        string? localSupplierCode = "200"
    )
    {
        await _localDb.Insertable(new Product
        {
            UUID = $"{productCode}-uuid",
            ProductCode = productCode,
            ItemNumber = itemNumber,
            Barcode = barcode,
            ProductName = name,
            ProductImage = $"{productCode}.jpg",
            LocalSupplierCode = localSupplierCode,
            IsActive = productIsActive,
            IsDeleted = false,
        }).ExecuteCommandAsync();

        await _localDb.Insertable(new WarehouseProduct
        {
            ProductCode = productCode,
            IsActive = warehouseIsActive,
            MinOrderQuantity = minOrderQuantity,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedStoreAsync(string storeCode, string storeName)
    {
        await _localDb.Insertable(new Store
        {
            StoreGUID = $"{storeCode}-guid",
            StoreCode = storeCode,
            StoreName = storeName,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedDeviceAsync(string deviceCode, string branchCode)
    {
        await _posmDb.Insertable(new POSM_设备注册信息表
        {
            系统设备编号 = deviceCode,
            设备硬件识别码 = $"{deviceCode}-hardware",
            分店代码 = branchCode,
            设备类型 = "POS",
            设备系统 = "Windows",
            设备状态 = 1,
            设备授权码 = $"{deviceCode}-auth",
        }).ExecuteCommandAsync();
    }

    private async Task SeedStatisticStateAsync(
        DateTime date,
        string status,
        string? errorMessage = null
    )
    {
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = date.Date,
            Status = status,
            SourceTimeZone = "POSM_LOCAL",
            LastAggregatedAtUtc = DateTime.UtcNow,
            LastCheckedAtUtc = DateTime.UtcNow,
            ErrorMessage = errorMessage,
        }).ExecuteCommandAsync();
    }

    private async Task SeedBestSellerStatisticsFromPosmAsync(DateTime startDate, DateTime endDate)
    {
        var start = startDate.Date;
        var end = endDate.Date;
        var endExclusive = end.AddDays(1);

        await _localDb.Deleteable<ProductStoreDailySalesStatistic>()
            .Where(s => s.Date >= start && s.Date <= end && s.SupplierCode == "200")
            .ExecuteCommandAsync();
        await _localDb.Deleteable<SalesStatisticRefreshState>()
            .Where(s => s.StatisticType == SalesStatisticType.ProductStoreDaily && s.Date >= start && s.Date <= end)
            .ExecuteCommandAsync();

        var salesRows = await _posmDb.Queryable<SalesOrderDetail>()
            .LeftJoin<SalesOrder>((d, o) => d.OrderGuid == o.OrderGuid)
            .Where((d, o) =>
                o.Status == 1
                && d.SupplierCode == "200"
                && o.OrderTime >= start
                && o.OrderTime < endExclusive
            )
            .Select((d, o) => new
            {
                d.OrderGuid,
                d.ProductCode,
                d.Barcode,
                d.ProductName,
                d.Quantity,
                d.ActualAmount,
                o.BranchCode,
                o.DeviceCode,
                o.OrderTime,
            })
            .ToListAsync();

        var deviceCodes = salesRows
            .Where(x => string.IsNullOrWhiteSpace(x.BranchCode) && !string.IsNullOrWhiteSpace(x.DeviceCode))
            .Select(x => x.DeviceCode!)
            .Distinct()
            .ToList();
        var deviceBranchMap = deviceCodes.Any()
            ? (await _posmDb.Queryable<POSM_设备注册信息表>()
                .Where(d => deviceCodes.Contains(d.系统设备编号))
                .Select(d => new { d.系统设备编号, d.分店代码 })
                .ToListAsync())
                .Where(x => !string.IsNullOrWhiteSpace(x.系统设备编号))
                .GroupBy(x => x.系统设备编号)
                .ToDictionary(
                    x => x.Key,
                    x => x.Select(row => row.分店代码).FirstOrDefault(code => !string.IsNullOrWhiteSpace(code)) ?? string.Empty
                )
            : new Dictionary<string, string>();

        // 测试辅助：把 POSM fixture 转成统计表行，验证 Best Sellers 运行时不再读取 POSM。
        var statisticRows = salesRows
            .Where(x => x.OrderTime.HasValue && !string.IsNullOrWhiteSpace(x.ProductCode))
            .Select(x => new
            {
                Date = x.OrderTime!.Value.Date,
                BranchCode = ResolveTestBranchCode(x.BranchCode, x.DeviceCode, deviceBranchMap),
                x.OrderGuid,
                x.ProductCode,
                x.Barcode,
                x.ProductName,
                Quantity = x.Quantity ?? 0,
                ActualAmount = x.ActualAmount ?? 0m,
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.BranchCode))
            .GroupBy(x => new { x.Date, x.BranchCode, x.ProductCode })
            .Select(group => new ProductStoreDailySalesStatistic
            {
                Date = group.Key.Date,
                BranchCode = group.Key.BranchCode,
                SupplierCode = "200",
                ProductCode = group.Key.ProductCode,
                ProductName = group.Select(x => x.ProductName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                Barcode = group.Select(x => x.Barcode).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                TotalQuantity = group.Sum(x => x.Quantity),
                TotalAmount = group.Sum(x => x.ActualAmount),
                OrderCount = group.Select(x => x.OrderGuid).Distinct().Count(),
                UpdateTime = DateTime.Now,
            })
            .ToList();

        if (statisticRows.Any())
        {
            await _localDb.Insertable(statisticRows).ExecuteCommandAsync();
        }

        for (var date = start; date <= end; date = date.AddDays(1))
        {
            await SeedStatisticStateAsync(date, SalesStatisticRefreshStatus.Fresh);
        }
    }

    private static string ResolveTestBranchCode(
        string? branchCode,
        string? deviceCode,
        Dictionary<string, string> deviceBranchMap
    )
    {
        if (!string.IsNullOrWhiteSpace(branchCode))
            return branchCode;

        return !string.IsNullOrWhiteSpace(deviceCode) && deviceBranchMap.TryGetValue(deviceCode, out var mappedBranch)
            ? mappedBranch
            : string.Empty;
    }

    private async Task SeedSaleAsync(
        string orderGuid,
        string detailGuid,
        string productCode,
        string? branchCode,
        DateTime orderTime,
        int quantity,
        decimal actualAmount,
        string? barcode = null,
        string? productName = null,
        string? deviceCode = null
    )
    {
        await _posmDb.Insertable(new SalesOrder
        {
            OrderGuid = orderGuid,
            BranchCode = branchCode,
            DeviceCode = deviceCode,
            OrderTime = orderTime,
            Status = 1,
        }).ExecuteCommandAsync();

        await _posmDb.Insertable(new SalesOrderDetail
        {
            OrderDetailGuid = detailGuid,
            OrderGuid = orderGuid,
            ProductCode = productCode,
            SupplierCode = "200",
            Barcode = barcode,
            ProductName = productName,
            Quantity = quantity,
            ActualAmount = actualAmount,
        }).ExecuteCommandAsync();
    }

    private async Task SeedReturnRecordAsync(
        string returnOrderGuid,
        string returnDetailGuid,
        string originalOrderGuid,
        string originalDetailGuid,
        string productCode,
        string? branchCode,
        DateTime orderTime,
        decimal returnQuantity,
        decimal returnAmount
    )
    {
        await _posmDb.Insertable(new SalesOrder
        {
            OrderGuid = returnOrderGuid,
            BranchCode = branchCode,
            OrderTime = orderTime,
            Status = 1,
            LastUploadTime = orderTime.AddMinutes(5),
        }).ExecuteCommandAsync();
        await _posmDb.Insertable(new SalesReturnRecord
        {
            ReturnDetailGuid = returnDetailGuid,
            ReturnOrderGuid = returnOrderGuid,
            OriginalOrderGuid = originalOrderGuid,
            OriginalOrderDetailGuid = originalDetailGuid,
            ProductCode = productCode,
            ReturnQuantity = returnQuantity,
            ReturnAmount = returnAmount,
            CreatedTime = orderTime.AddMinutes(6),
            UpdatedTime = orderTime.AddMinutes(7),
        }).ExecuteCommandAsync();
    }

    private async Task SeedPosmOrderAsync(
        string orderGuid,
        string? branchCode,
        DateTime orderTime,
        int itemCount,
        IEnumerable<(string DetailGuid, string ProductCode, int Quantity, decimal ActualAmount)> details,
        IEnumerable<(string PaymentGuid, decimal Amount)> payments,
        string? deviceCode = null
    )
    {
        await _posmDb.Insertable(new SalesOrder
        {
            OrderGuid = orderGuid,
            BranchCode = branchCode,
            DeviceCode = deviceCode,
            OrderTime = orderTime,
            Status = 1,
            ItemCount = itemCount,
        }).ExecuteCommandAsync();

        foreach (var detail in details)
        {
            await _posmDb.Insertable(new SalesOrderDetail
            {
                OrderDetailGuid = detail.DetailGuid,
                OrderGuid = orderGuid,
                ProductCode = detail.ProductCode,
                SupplierCode = "200",
                Quantity = detail.Quantity,
                ActualAmount = detail.ActualAmount,
            }).ExecuteCommandAsync();
        }

        foreach (var payment in payments)
        {
            await _posmDb.Insertable(new PaymentDetail
            {
                PaymentGuid = payment.PaymentGuid,
                OrderGuid = orderGuid,
                Amount = payment.Amount,
            }).ExecuteCommandAsync();
        }
    }

    private SalesDashboardReactService CreateService(IMemoryCache? cache = null)
    {
        return new SalesDashboardReactService(
            CreateSqlSugarContext(_localDb),
            CreatePosmSqlSugarContext(_posmDb),
            Mock.Of<IMapper>(),
            NullLogger<SalesDashboardReactService>.Instance,
            cache ?? new MemoryCache(new MemoryCacheOptions())
        );
    }

    private SalesDashboardReactService CreateServiceWithBrokenPosm()
    {
        var brokenPosmDb = new SqlSugarClient(CreateConnectionConfig("Data Source=/dev/null/hbposm-forbidden.db"));
        return new SalesDashboardReactService(
            CreateSqlSugarContext(_localDb),
            CreatePosmSqlSugarContext(brokenPosmDb),
            Mock.Of<IMapper>(),
            NullLogger<SalesDashboardReactService>.Instance,
            new MemoryCache(new MemoryCacheOptions())
        );
    }

    private SalesStatisticsJobService CreateStatisticsJobService(bool writeDirectChinaSupplierCode = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ScheduledTasks:MaxConcurrentUpdates"] = "2",
                ["ScheduledTasks:MaxDaysForConcurrentUpdate"] = "30",
                ["ScheduledTasks:MaxDaysPerChunk"] = "7",
                ["SalesStatistics:WriteDirectChinaSupplierCode"] = writeDirectChinaSupplierCode ? "true" : "false",
            })
            .Build();

        return new SalesStatisticsJobService(
            CreatePosmSqlSugarContext(_posmDb),
            CreateSqlSugarContext(_localDb),
            NullLogger<SalesStatisticsJobService>.Instance,
            configuration,
            Mock.Of<IServiceScopeFactory>()
        );
    }

    private StatisticsJobTriggerController CreateStatisticsController()
    {
        var context = CreateSqlSugarContext(_localDb);
        var taskLogService = new ScheduledTaskLogService(
            context,
            NullLogger<ScheduledTaskLogService>.Instance
        );
        return new StatisticsJobTriggerController(
            CreateStatisticsJobService(),
            taskLogService,
            context,
            NullLogger<StatisticsJobTriggerController>.Instance,
            new ProductStoreDailyStatisticQueueService(
                context,
                taskLogService,
                Mock.Of<IServiceScopeFactory>(),
                NullLogger<ProductStoreDailyStatisticQueueService>.Instance
            ),
            CreateAlignmentService(context),
            new SalesStatisticsAlignmentBackgroundRecalculateService(
                new ScheduledTaskLogService(context, NullLogger<ScheduledTaskLogService>.Instance),
                Mock.Of<IServiceScopeFactory>(),
                NullLogger<SalesStatisticsAlignmentBackgroundRecalculateService>.Instance
            )
        );
    }

    private SalesStatisticsAlignmentService CreateAlignmentService(SqlSugarContext context)
    {
        return new SalesStatisticsAlignmentService(
            context,
            CreatePosmSqlSugarContext(_posmDb),
            new ScheduledTaskLeaseService(
                context,
                Options.Create(new ScheduledTaskOptions { InstanceId = "test-api" }),
                NullLogger<ScheduledTaskLeaseService>.Instance
            ),
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<SalesStatisticsAlignmentService>.Instance
        );
    }

    private static T ExtractAnonymousData<T>(object? value)
    {
        Assert.NotNull(value);
        var dataProperty = value!.GetType().GetProperty("data");
        Assert.NotNull(dataProperty);
        var data = dataProperty!.GetValue(value);
        return Assert.IsType<T>(data);
    }

    private static T ReadAnonymousProperty<T>(object? value, string propertyName)
    {
        Assert.NotNull(value);
        var property = value!.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        var propertyValue = property!.GetValue(value);
        return Assert.IsType<T>(propertyValue);
    }

    private static OkObjectResult AssertOk(IActionResult result)
    {
        if (result is OkObjectResult ok)
        {
            return ok;
        }

        if (result is ObjectResult objectResult)
        {
            throw new Xunit.Sdk.XunitException(
                $"Expected OkObjectResult, got {result.GetType().Name}: {objectResult.Value}"
            );
        }

        throw new Xunit.Sdk.XunitException($"Expected OkObjectResult, got {result.GetType().Name}");
    }

    private static SalesDashboardController CreateController(
        ISalesDashboardReactService service,
        IUserService userService
    )
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "user-1"),
            },
            "TestAuth"
        ));

        var controller = new SalesDashboardController(
            service,
            NullLogger<SalesDashboardController>.Instance,
            userService,
            Mock.Of<ISalesDashboardCacheWarmer>(),
            Mock.Of<IRoleService>()
        );
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static ConnectionConfig CreateConnectionConfig(string connectionString)
    {
        return new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        };
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static void CreateScheduledTaskLogTable(ISqlSugarClient db)
    {
        db.Ado.ExecuteCommand(
            """
            CREATE TABLE IF NOT EXISTS ScheduledTaskLog (
                Id TEXT PRIMARY KEY,
                TaskType TEXT NOT NULL,
                TaskParameters TEXT NULL,
                Status TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NULL,
                DurationMs INTEGER NULL,
                ErrorMessage TEXT NULL,
                RetryCount INTEGER NOT NULL,
                CanRetry INTEGER NOT NULL,
                ScheduledTime TEXT NOT NULL,
                TriggeredBy TEXT NULL,
                CreatedAt TEXT NOT NULL,
                CreatedBy TEXT NULL,
                UpdatedAt TEXT NULL,
                UpdatedBy TEXT NULL,
                IsDeleted INTEGER NULL
            );
            """
        );
    }

    private static POSMSqlSugarContext CreatePosmSqlSugarContext(ISqlSugarClient db)
    {
        var context = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(POSMSqlSugarContext));
        typeof(POSMSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _localConnection.Dispose();
        _posmConnection.Dispose();
        if (File.Exists(_localDbPath)) SqliteTempFileCleanup.DeleteIfExists(_localDbPath);
        if (File.Exists(_posmDbPath)) SqliteTempFileCleanup.DeleteIfExists(_posmDbPath);
    }
}
