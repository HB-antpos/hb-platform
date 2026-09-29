using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using AutoMapper;
using BlazorApp.Api.Cache;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.HBSalesRecord;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;
using ScheduledTaskStatus = BlazorApp.Shared.Models.HBweb.TaskStatus;

namespace BlazorApp.Api.Tests;

public sealed class SalesDashboardReportRevenueTests : IDisposable
{
    private readonly string _localDbPath;
    private readonly string _posmDbPath;
    private readonly string _hbSalesDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _posmConnection;
    private readonly SqliteConnection _hbSalesConnection;

    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarClient _posmDb;
    private readonly SqlSugarScope _hbSalesDb;
    private readonly HashSet<DateTime> _seededProductStatisticDates = new();
    private int _nextHbSalesRecordId;

    [Fact]
    public void 报表统计缺口刷新最多等待二百五十毫秒()
    {
        var waitField = typeof(SalesDashboardReactService).GetField(
            "REPORT_STATISTICS_REFRESH_WAIT",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        Assert.NotNull(waitField);
        Assert.Equal(TimeSpan.FromMilliseconds(250), Assert.IsType<TimeSpan>(waitField.GetValue(null)));
    }

    public SalesDashboardReportRevenueTests()
    {
        _localDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _posmDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _hbSalesDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _localConnection = new SqliteConnection($"Data Source={_localDbPath}");
        _posmConnection = new SqliteConnection($"Data Source={_posmDbPath}");
        _hbSalesConnection = new SqliteConnection($"Data Source={_hbSalesDbPath}");
        _localConnection.Open();
        _posmConnection.Open();
        _hbSalesConnection.Open();

        _localDb = new SqlSugarClient(CreateConnectionConfig(_localConnection.ConnectionString));
        _posmDb = new SqlSugarClient(CreateConnectionConfig(_posmConnection.ConnectionString));
        _hbSalesDb = new SqlSugarScope(CreateConnectionConfig(_hbSalesConnection.ConnectionString));
        _localDb.CodeFirst.InitTables(
            typeof(Store),
            typeof(WarehouseProduct),
            typeof(StoreRetailPrice),
            typeof(StoreSalesStatistic),
            typeof(HourlySalesStatistic),
            typeof(ProductStoreDailySalesStatistic),
            typeof(SalesStatisticRefreshState),
            typeof(AustralianSupplierStoreSalesDetail),
            typeof(ChinaSupplierStoreSalesDetail),
            typeof(StoreSupplierSalesDetail),
            typeof(HBLocalSupplier),
            typeof(ChinaSupplier),
            typeof(Product),
            typeof(ProductSetCode),
            typeof(StoreMultiCodeProduct)
        );
        CreateScheduledTaskLogTable(_localDb);
        _posmDb.CodeFirst.InitTables(
            typeof(SalesOrder),
            typeof(SalesOrderDetail),
            typeof(SalesReturnRecord),
            typeof(PaymentDetail),
            typeof(PosmProductSupplierMapping),
            typeof(POSM_设备注册信息表)
        );
        _hbSalesDb.CodeFirst.InitTables(typeof(SalesOrderMain), typeof(SalesOrderDetailRecord));
    }

    [Fact]
    public async Task GetBranchDailyPerformanceAsync_使用统计表并按对比区间偏移配对()
    {
        await SeedStoreSalesStatisticAsync(new DateTime(2026, 7, 1), "S1", "分店一", 100m, 5);
        await SeedStoreSalesStatisticAsync(new DateTime(2026, 7, 2), "S1", "分店一", 150m, 7);
        await SeedStoreSalesStatisticAsync(new DateTime(2026, 7, 1), "S2", "分店二", 999m, 20);
        await SeedStoreSalesStatisticAsync(new DateTime(2025, 7, 1), "S1", "分店一", 80m, 4);
        await SeedStoreSalesStatisticAsync(new DateTime(2025, 7, 2), "S1", "分店一", 200m, 8);
        await SeedSalesOrderAsync("daily-current-1", new DateTime(2026, 7, 1, 9, 0, 0), "S1");
        await SeedSalesOrderAsync("daily-current-2", new DateTime(2026, 7, 2, 9, 0, 0), "S1");
        await SeedHbSalesOrderAsync("daily-compare-1", new DateTime(2025, 7, 1, 9, 0, 0), "S1", 80m, 1m);
        await SeedHbSalesOrderAsync("daily-compare-2", new DateTime(2025, 7, 2, 9, 0, 0), "S1", 200m, 1m);
        var service = CreateService();

        var result = await service.GetBranchDailyPerformanceAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 2),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 2),
            },
            new List<string> { "S1" }
        );

        Assert.Collection(
            result,
            row =>
            {
                Assert.Equal(new DateTime(2026, 7, 1), row.Date);
                Assert.Equal("S1", row.BranchCode);
                Assert.Equal(100m, row.Revenue);
                Assert.Equal(80m, row.RevenueLY);
                Assert.Equal(5, row.OrderCount);
                Assert.Equal(4, row.OrderCountLY);
            },
            row =>
            {
                Assert.Equal(new DateTime(2026, 7, 2), row.Date);
                Assert.Equal("S1", row.BranchCode);
                Assert.Equal(150m, row.Revenue);
                Assert.Equal(200m, row.RevenueLY);
                Assert.Equal(7, row.OrderCount);
                Assert.Equal(8, row.OrderCountLY);
            }
        );
    }

    [Fact]
    public async Task GetBranchDailyPerformanceAsync_同期独有分店日期也返回并将本期补零()
    {
        var currentDate = new DateTime(2026, 7, 1);
        var compareDate = new DateTime(2025, 7, 1);
        await SeedStoreSalesStatisticAsync(compareDate, "S2", "分店二", 80m, 4);
        await SeedHbSalesOrderAsync("daily-compare-only", compareDate.AddHours(9), "S2", 80m, 1m);
        var service = CreateService();

        var result = await service.GetBranchDailyPerformanceAsync(
            new DateRangeDto
            {
                StartDate = currentDate,
                EndDate = currentDate,
                CompareStartDate = compareDate,
                CompareEndDate = compareDate,
            }
        );

        var row = Assert.Single(result.Items);
        Assert.Equal(currentDate, row.Date);
        Assert.Equal("S2", row.BranchCode);
        Assert.Equal(0m, row.Revenue);
        Assert.Equal(80m, row.RevenueLY);
        Assert.Equal(0, row.OrderCount);
        Assert.Equal(4, row.OrderCountLY);
    }

    [Fact]
    public async Task GetStatisticsFreshnessAsync_返回最近成功时间和最新运行状态()
    {
        var successfulAt = DateTime.SpecifyKind(
            SalesStatisticsBusinessDate.Today().AddHours(1).AddMinutes(30).AddSeconds(5),
            DateTimeKind.Utc
        );
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.RevenueReportPublished,
            Date = successfulAt.Date,
            Status = SalesStatisticRefreshStatus.Fresh,
            LastAggregatedAtUtc = successfulAt,
            LastCheckedAtUtc = successfulAt,
            CompletedAtUtc = successfulAt,
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();
        await SeedStatisticsTaskLogAsync(ScheduledTaskStatus.Success, successfulAt.AddMinutes(-2), successfulAt);
        await SeedStatisticsTaskLogAsync(
            ScheduledTaskStatus.Failed,
            successfulAt.AddMinutes(28),
            successfulAt.AddMinutes(29)
        );
        var service = CreateService();

        var result = await service.GetStatisticsFreshnessAsync();

        Assert.Equal(successfulAt, result.LastSuccessfulAtUtc);
        Assert.Equal(ScheduledTaskStatus.Failed, result.LatestRunStatus);
    }

    [Fact]
    public async Task GetStatisticsFreshnessAsync_超时Running对外标记Failed()
    {
        await SeedStatisticsTaskLogAsync(
            ScheduledTaskStatus.Running,
            DateTime.UtcNow.AddHours(-2),
            null
        );
        var service = CreateService();

        var result = await service.GetStatisticsFreshnessAsync();

        Assert.Equal(ScheduledTaskStatus.Failed, result.LatestRunStatus);
    }

    [Fact]
    public async Task GetStatisticsFreshnessAsync_昨天完成发布不能推进当天新鲜度()
    {
        var yesterdayPublishedAt = DateTime.SpecifyKind(
            SalesStatisticsBusinessDate.Today().AddDays(-1).AddHours(23),
            DateTimeKind.Utc
        );
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.RevenueReportPublished,
            Date = yesterdayPublishedAt.Date,
            Status = SalesStatisticRefreshStatus.Fresh,
            LastAggregatedAtUtc = yesterdayPublishedAt,
            LastCheckedAtUtc = yesterdayPublishedAt,
            CompletedAtUtc = yesterdayPublishedAt,
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();
        await SeedStatisticsTaskLogAsync(
            ScheduledTaskStatus.Skipped,
            SalesStatisticsBusinessDate.Today().AddHours(1),
            SalesStatisticsBusinessDate.Today().AddHours(1).AddMinutes(1)
        );

        var result = await CreateService().GetStatisticsFreshnessAsync();

        Assert.Null(result.LastSuccessfulAtUtc);
        Assert.Equal(ScheduledTaskStatus.Skipped, result.LatestRunStatus);
    }

    [Fact]
    public async Task StatisticsCacheVersion_范围内单一维度实际提交后必须变化()
    {
        var date = new DateTime(2026, 9, 14);
        var firstPublishedAt = new DateTime(2026, 9, 14, 4, 0, 0, DateTimeKind.Utc);
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.StoreSales,
            Date = date,
            Status = SalesStatisticRefreshStatus.Fresh,
            LastAggregatedAtUtc = firstPublishedAt,
            CompletedAtUtc = firstPublishedAt,
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();
        var service = CreateService();
        var range = new DateRangeDto { StartDate = date, EndDate = date };

        var before = await InvokeStatisticsCacheVersionAsync(service, range);
        var secondPublishedAt = firstPublishedAt.AddMinutes(30);
        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => row.LastAggregatedAtUtc == secondPublishedAt)
            .SetColumns(row => row.CompletedAtUtc == secondPublishedAt)
            .Where(row => row.Date == date && row.StatisticType == SalesStatisticType.StoreSales)
            .ExecuteCommandAsync();

        var after = await InvokeStatisticsCacheVersionAsync(service, range);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task GetStatisticsFreshness_返回服务层新鲜度信息()
    {
        var expected = new StatisticsFreshnessDto
        {
            LastSuccessfulAtUtc = new DateTime(2026, 7, 10, 1, 30, 0, DateTimeKind.Utc),
            LatestRunStatus = ScheduledTaskStatus.Success,
        };
        var service = new Mock<ISalesDashboardReactService>();
        service.Setup(item => item.GetStatisticsFreshnessAsync()).ReturnsAsync(expected);
        var controller = CreateController(service.Object, CreateUserService(Array.Empty<string>()));

        var response = await controller.GetStatisticsFreshness();

        Assert.Same(expected, ExtractAnonymousData<StatisticsFreshnessDto>(AssertOk(response).Value));
    }

    [Fact]
    public void GetStatisticsFreshness_要求商品经营分析权限()
    {
        var method = typeof(SalesDashboardController).GetMethod(nameof(SalesDashboardController.GetStatisticsFreshness));

        var authorize = Assert.Single(method!.GetCustomAttributes<AuthorizeAttribute>());

        Assert.Equal(Permissions.Reports.ProductMovementView, authorize.Policy);
    }

    [Fact]
    public async Task GetBranchDailyPerformanceAsync_最近成功时间变化时绕过旧缓存()
    {
        var date = new DateTime(2026, 7, 10);
        await SeedStoreSalesStatisticAsync(date, "S1", "分店一", 100m, 5);
        await SeedSalesOrderAsync("daily-cache-version", date.AddHours(9), "S1");
        var firstCommittedAt = new DateTime(2026, 7, 10, 0, 30, 0, DateTimeKind.Utc);
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.StoreSales,
            Date = date,
            Status = SalesStatisticRefreshStatus.Fresh,
            LastAggregatedAtUtc = firstCommittedAt,
            CompletedAtUtc = firstCommittedAt,
            LastCheckedAtUtc = firstCommittedAt,
        }).ExecuteCommandAsync();
        var service = CreateService();
        var range = new DateRangeDto { StartDate = date, EndDate = date };
        var first = await service.GetBranchDailyPerformanceAsync(range, new List<string> { "S1" });
        Assert.Equal(100m, Assert.Single(first).Revenue);

        await _localDb.Updateable<StoreSalesStatistic>()
            .SetColumns(row => row.TotalAmount == 200m)
            .Where(row => row.Date == date && row.BranchCode == "S1")
            .ExecuteCommandAsync();
        var secondCommittedAt = new DateTime(2026, 7, 10, 1, 0, 0, DateTimeKind.Utc);
        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => row.LastAggregatedAtUtc == secondCommittedAt)
            .SetColumns(row => row.CompletedAtUtc == secondCommittedAt)
            .SetColumns(row => row.LastCheckedAtUtc == secondCommittedAt)
            .Where(row => row.Date == date && row.StatisticType == SalesStatisticType.StoreSales)
            .ExecuteCommandAsync();

        var second = await service.GetBranchDailyPerformanceAsync(range, new List<string> { "S1" });

        Assert.Equal(200m, Assert.Single(second).Revenue);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_按分店代码聚合避免名称变化拆行()
    {
        await SeedStoreSalesStatisticAsync(new DateTime(2026, 7, 1), "S1", "Store A", 100m, 5);
        await SeedStoreSalesStatisticAsync(new DateTime(2026, 7, 2), "S1", "Store B", 150m, 5);
        await SeedStoreSalesStatisticAsync(new DateTime(2025, 7, 1), "S1", "Store Old", 80m, 4);
        await SeedStoreSalesStatisticAsync(new DateTime(2025, 7, 2), "S1", "Store Old", 70m, 3);
        await SeedSalesOrderAsync("rank-current-1", new DateTime(2026, 7, 1, 9, 0, 0), "S1");
        await SeedSalesOrderAsync("rank-current-2", new DateTime(2026, 7, 2, 9, 0, 0), "S1");
        await SeedHbSalesOrderAsync("rank-compare-1", new DateTime(2025, 7, 1, 9, 0, 0), "S1", 80m, 1m);
        await SeedHbSalesOrderAsync("rank-compare-2", new DateTime(2025, 7, 2, 9, 0, 0), "S1", 70m, 1m);
        var service = CreateService();

        var result = await service.GetExecutiveBranchPerformanceAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 2),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 2),
            },
            branchCodes: new List<string> { "S1" }
        );

        var row = Assert.Single(result.Items);
        Assert.Equal("S1", row.BranchCode);
        Assert.Equal(250m, row.Revenue);
        Assert.Equal(150m, row.RevenueLY);
        Assert.Equal(10, row.OrderCount);
        Assert.Equal(7, row.OrderCountLY);
        Assert.Equal(25m, row.Aov);
        Assert.NotEmpty(row.BranchName);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_历史统计缺失时重算并返回去年客单()
    {
        await SeedStoreAsync("S1", "Store A");
        await SeedStoreSalesStatisticAsync(new DateTime(2026, 7, 4), "S1", "Store A", 120m, 6);
        await SeedHbSalesOrderAsync("old-1", new DateTime(2025, 7, 5, 10, 15, 0), "S1", 40m, 2m);
        await SeedHbSalesOrderAsync("old-2", new DateTime(2025, 7, 5, 11, 30, 0), "S1", 60m, 3m);
        var service = CreateService();
        var range = new DateRangeDto
        {
            StartDate = new DateTime(2026, 7, 4),
            EndDate = new DateTime(2026, 7, 4),
            CompareStartDate = new DateTime(2025, 7, 5),
            CompareEndDate = new DateTime(2025, 7, 5),
        };

        var result = await service.GetExecutiveBranchPerformanceAsync(
            range,
            branchCodes: new List<string> { "S1" }
        );
        for (var attempt = 0; result.StatisticsPending && attempt < 200; attempt++)
        {
            await Task.Delay(50);
            result = await service.GetExecutiveBranchPerformanceAsync(
                range,
                branchCodes: new List<string> { "S1" }
            );
        }

        Assert.False(result.StatisticsPending);
        var row = Assert.Single(result.Items);
        Assert.Equal(100m, row.RevenueLY);
        Assert.Equal(2, row.OrderCountLY);
        Assert.Equal(50m, row.AovLY);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_全分店统计部分缺失时重算整日()
    {
        await SeedStoreAsync("S1", "Store A");
        await SeedStoreAsync("S2", "Store B");
        await SeedStoreSalesStatisticAsync(new DateTime(2026, 7, 4), "S1", "Store A", 10m, 1);
        await SeedPosmOrderWithPaymentAsync("current-1", new DateTime(2026, 7, 4, 10, 15, 0), "S1", 120m, 2);
        await SeedPosmOrderWithPaymentAsync("current-2", new DateTime(2026, 7, 4, 11, 30, 0), "S2", 80m, 1);
        var service = CreateService();

        var result = await service.GetExecutiveBranchPerformanceAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
                CompareStartDate = new DateTime(2025, 7, 5),
                CompareEndDate = new DateTime(2025, 7, 5),
            }
        );

        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, row => row.BranchCode == "S1" && row.Revenue == 120m);
        Assert.Contains(result.Items, row => row.BranchCode == "S2" && row.Revenue == 80m);
        Assert.False(result.StatisticsPending);
        Assert.Equal(2, result.StatisticsExpectedBranchCount);
        Assert.Equal(2, result.StatisticsSnapshotBranchCount);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_二十八家授权分店含零销售分店时补齐零值排行且不待完成()
    {
        var date = new DateTime(2026, 7, 6);
        var compareDate = new DateTime(2025, 7, 7);
        var branchCodes = Enumerable.Range(1, 28).Select(index => $"S{index:00}").ToList();

        foreach (var branchCode in branchCodes)
        {
            await SeedStoreAsync(branchCode, $"Store {branchCode}");
        }

        // S28 在本期和同期都没有销售来源，也没有 StoreSalesStatistic；其余授权分店均有来源和统计快照。
        foreach (var (branchCode, index) in branchCodes.Take(27).Select((code, index) => (code, index)))
        {
            var amount = 280m - index;
            await SeedStoreSalesStatisticAsync(date, branchCode, $"Store {branchCode}", amount, 1);
            await SeedStoreSalesStatisticAsync(compareDate, branchCode, $"Store {branchCode}", amount / 2, 1);
            await SeedPosmOrderWithPaymentAsync($"zero-current-{branchCode}", date.AddHours(9), branchCode, amount, 1);
            await SeedPosmOrderWithPaymentAsync($"zero-compare-{branchCode}", compareDate.AddHours(9), branchCode, amount / 2, 1);
            await SeedHbSalesOrderAsync(
                $"zero-hbsales-compare-{branchCode}",
                compareDate.AddHours(9),
                branchCode,
                amount / 2,
                1m
            );
        }

        var refreshDates = new List<DateTime>();
        var service = CreateService();
        service.StoreStatisticsRefreshTestInterceptor = refreshDate =>
        {
            refreshDates.Add(refreshDate);
            return Task.CompletedTask;
        };
        var result = await service.GetExecutiveBranchPerformanceAsync(
            new DateRangeDto
            {
                StartDate = date,
                EndDate = date,
                CompareStartDate = compareDate,
                CompareEndDate = compareDate,
            },
            branchCodes: branchCodes
        );

        Assert.False(result.StatisticsPending);
        Assert.Equal(28, result.Items.Count);
        Assert.Equal(28, result.StatisticsExpectedBranchCount);
        Assert.Equal(28, result.StatisticsSnapshotBranchCount);
        Assert.Equal(Enumerable.Range(1, 28), result.Items.Select(row => row.Rank));
        Assert.Empty(refreshDates);

        var zeroSalesBranch = Assert.Single(result.Items, row => row.BranchCode == "S28");
        Assert.Equal(28, zeroSalesBranch.Rank);
        Assert.Equal(0m, zeroSalesBranch.Revenue);
        Assert.Equal(0, zeroSalesBranch.OrderCount);
        Assert.Equal(0m, zeroSalesBranch.RevenueLY);
        Assert.Equal(0, zeroSalesBranch.OrderCountLY);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_全分店以启用门店目录补齐二十八家零销售行()
    {
        var date = new DateTime(2026, 7, 16);
        var branchCodes = Enumerable.Range(1, 28).Select(index => $"S{index:00}").ToList();
        foreach (var branchCode in branchCodes)
            await SeedStoreAsync(branchCode, $"Store {branchCode}");

        await SeedPosmOrderWithPaymentAsync("all-store-active-directory", date.AddHours(9), "S01", 88m, 1);
        await SeedStoreSalesStatisticAsync(date, "S01", "Store S01", 88m, 1);
        var service = CreateService();

        var result = await service.GetExecutiveBranchPerformanceAsync(
            new DateRangeDto { StartDate = date, EndDate = date }
        );

        Assert.False(result.StatisticsPending);
        Assert.Equal(28, result.Items.Count);
        Assert.Equal(28, result.StatisticsExpectedBranchCount);
        Assert.Equal(28, result.StatisticsSnapshotBranchCount);
        Assert.Equal(88m, Assert.Single(result.Items, row => row.BranchCode == "S01").Revenue);
        Assert.All(
            result.Items.Where(row => row.BranchCode != "S01"),
            row => Assert.Equal(0m, row.Revenue)
        );
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_全分店统计同数但分店身份陈旧时仍触发重算()
    {
        var date = new DateTime(2026, 7, 17);
        await SeedStoreAsync("S01", "Store S01");
        await SeedStoreAsync("S02", "Store S02");
        await SeedStoreAsync("S99", "Store S99");
        await SeedPosmOrderWithPaymentAsync("identity-current-s01", date.AddHours(9), "S01", 10m, 1);
        await SeedPosmOrderWithPaymentAsync("identity-current-s02", date.AddHours(10), "S02", 20m, 1);
        await SeedStoreSalesStatisticAsync(date, "S01", "Store S01", 10m, 1);
        await SeedStoreSalesStatisticAsync(date, "S99", "Store S99", 20m, 1);

        var refreshDates = new List<DateTime>();
        var service = CreateService();
        service.StoreStatisticsRefreshTestInterceptor = refreshDate =>
        {
            refreshDates.Add(refreshDate.Date);
            return Task.CompletedTask;
        };

        var result = await service.GetExecutiveBranchPerformanceAsync(
            new DateRangeDto { StartDate = date, EndDate = date }
        );

        Assert.Contains(date, refreshDates);
        Assert.True(result.StatisticsPending);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_二零二五年仅POSM来源身份也视为完整()
    {
        var date = new DateTime(2025, 7, 18);
        await SeedStoreAsync("S01", "Store S01");
        await SeedPosmOrderWithPaymentAsync("posm-only-source", date.AddHours(9), "S01", 88m, 1);
        await SeedStoreSalesStatisticAsync(date, "S01", "Store S01", 88m, 1);

        var refreshDates = new List<DateTime>();
        var service = CreateService();
        service.StoreStatisticsRefreshTestInterceptor = refreshDate =>
        {
            refreshDates.Add(refreshDate.Date);
            return Task.CompletedTask;
        };

        var result = await service.GetExecutiveBranchPerformanceAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            branchCodes: new List<string> { "S01" }
        );

        Assert.Empty(refreshDates);
        Assert.False(result.StatisticsPending);
        Assert.Equal(88m, Assert.Single(result.Items).Revenue);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_二零二五年仅HBSales来源身份也视为完整()
    {
        var date = new DateTime(2025, 7, 19);
        await SeedStoreAsync("S02", "Store S02");
        await SeedHbSalesOrderAsync("hb-only-source", date.AddHours(9), "S02", 66m, 1m);
        await SeedStoreSalesStatisticAsync(date, "S02", "Store S02", 66m, 1);

        var refreshDates = new List<DateTime>();
        var service = CreateService();
        service.StoreStatisticsRefreshTestInterceptor = refreshDate =>
        {
            refreshDates.Add(refreshDate.Date);
            return Task.CompletedTask;
        };

        var result = await service.GetExecutiveBranchPerformanceAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            branchCodes: new List<string> { "S02" }
        );

        Assert.Empty(refreshDates);
        Assert.False(result.StatisticsPending);
        Assert.Equal(66m, Assert.Single(result.Items).Revenue);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_二零二四年已验证HBSales来源身份也视为完整()
    {
        var date = new DateTime(2024, 9, 14);
        await SeedStoreAsync("S02", "Store S02");
        await SeedHbSalesOrderAsync("hb-only-source-2024", date.AddHours(9), "S02", 66m, 1m);
        await SeedStoreSalesStatisticAsync(date, "S02", "Store S02", 66m, 1);

        var refreshDates = new List<DateTime>();
        var service = CreateService();
        service.StoreStatisticsRefreshTestInterceptor = refreshDate =>
        {
            refreshDates.Add(refreshDate.Date);
            return Task.CompletedTask;
        };

        var result = await service.GetExecutiveBranchPerformanceAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            branchCodes: new List<string> { "S02" }
        );

        Assert.Empty(refreshDates);
        Assert.False(result.StatisticsPending);
        Assert.Equal(66m, Assert.Single(result.Items).Revenue);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_二零二五年POSM与HBSales来源身份取并集()
    {
        var date = new DateTime(2025, 7, 20);
        await SeedStoreAsync("S01", "Store S01");
        await SeedStoreAsync("S02", "Store S02");
        await SeedPosmOrderWithPaymentAsync("union-posm-source", date.AddHours(9), "S01", 88m, 1);
        await SeedHbSalesOrderAsync("union-hb-source", date.AddHours(10), "S02", 66m, 1m);
        await SeedStoreSalesStatisticAsync(date, "S01", "Store S01", 88m, 1);
        await SeedStoreSalesStatisticAsync(date, "S02", "Store S02", 66m, 1);

        var refreshDates = new List<DateTime>();
        var service = CreateService();
        service.StoreStatisticsRefreshTestInterceptor = refreshDate =>
        {
            refreshDates.Add(refreshDate.Date);
            return Task.CompletedTask;
        };

        var result = await service.GetExecutiveBranchPerformanceAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            branchCodes: new List<string> { "S01", "S02" }
        );

        Assert.Empty(refreshDates);
        Assert.False(result.StatisticsPending);
        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, row => row.BranchCode == "S01" && row.Revenue == 88m);
        Assert.Contains(result.Items, row => row.BranchCode == "S02" && row.Revenue == 66m);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_慢补算显式返回部分快照并在完成后二次请求完整()
    {
        var date = new DateTime(2026, 7, 6);
        await SeedStoreAsync("S1", "Store A");
        await SeedStoreAsync("S2", "Store B");
        await SeedStoreSalesStatisticAsync(date, "S1", "Store A", 10m, 1);
        await SeedPosmOrderWithPaymentAsync("pending-current-1", date.AddHours(10), "S1", 120m, 2);
        await SeedPosmOrderWithPaymentAsync("pending-current-2", date.AddHours(11), "S2", 80m, 1);

        var refreshStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseRefresh = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var refreshFinished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var refreshCoordinatorFinished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var service = CreateService();
        service.StoreStatisticsRefreshCompletedTestInterceptor = () =>
            refreshCoordinatorFinished.TrySetResult(true);
        service.StoreStatisticsRefreshTestInterceptor = async refreshDate =>
        {
            refreshStarted.TrySetResult(true);
            await releaseRefresh.Task;
            try
            {
                await SeedStoreSalesStatisticAsync(refreshDate, "S2", "Store B", 80m, 1);
            }
            finally
            {
                refreshFinished.TrySetResult(true);
            }
        };

        var range = new DateRangeDto { StartDate = date, EndDate = date };
        var partial = await service.GetExecutiveBranchPerformanceAsync(range);
        await refreshStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        releaseRefresh.TrySetResult(true);
        await refreshFinished.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        await refreshCoordinatorFinished.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        var partialRow = Assert.Single(partial.Items, row => row.BranchCode == "S1");
        Assert.Equal("S1", partialRow.BranchCode);
        Assert.Equal(10m, partialRow.Revenue);
        Assert.Contains(partial.Items, row => row.BranchCode == "S2" && row.Revenue == 0m);
        Assert.True(partial.StatisticsPending);
        Assert.Equal(2, partial.StatisticsExpectedBranchCount);
        Assert.Equal(2, partial.StatisticsSnapshotBranchCount);

        var completed = await service.GetExecutiveBranchPerformanceAsync(range);

        Assert.False(
            completed.StatisticsPending,
            $"items={completed.Items.Count}, expected={completed.StatisticsExpectedBranchCount}, snapshot={completed.StatisticsSnapshotBranchCount}"
        );
        Assert.Equal(2, completed.StatisticsExpectedBranchCount);
        Assert.Equal(2, completed.StatisticsSnapshotBranchCount);
        Assert.Equal(2, completed.Items.Count);
        Assert.Contains(completed.Items, row => row.BranchCode == "S1" && row.Revenue == 10m);
        Assert.Contains(completed.Items, row => row.BranchCode == "S2" && row.Revenue == 80m);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_慢补算完成后切换缓存代际并绕过旧快照()
    {
        var date = new DateTime(2026, 7, 9);
        await SeedStoreAsync("S1", "Store A");
        await SeedStoreAsync("S2", "Store B");
        await SeedStoreSalesStatisticAsync(date, "S1", "Store A", 10m, 1);
        await SeedPosmOrderWithPaymentAsync("generation-source-1", date.AddHours(10), "S1", 10m, 1);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var scope = CreateIsolatedServiceScope(cache);
        var service = scope.Service;
        var range = new DateRangeDto { StartDate = date, EndDate = date };

        // 先生成 10 分钟缓存，其中 S2 仍是零值；后续补算必须通过代际切换绕过这份旧快照。
        var cached = await service.GetExecutiveBranchPerformanceAsync(range);
        Assert.False(cached.StatisticsPending);
        Assert.Contains(cached.Items, row => row.BranchCode == "S2" && row.Revenue == 0m);

        await SeedPosmOrderWithPaymentAsync("generation-source-2", date.AddHours(11), "S2", 80m, 1);
        cache.Remove($"SalesSourceCoverage_POSM_{date:yyyyMMdd}_{date:yyyyMMdd}_ALL");

        var refreshStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseRefresh = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var refreshCoordinatorFinished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        service.StoreStatisticsRefreshCompletedTestInterceptor = () =>
            refreshCoordinatorFinished.TrySetResult(true);
        service.StoreStatisticsRefreshTestInterceptor = async refreshDate =>
        {
            refreshStarted.TrySetResult(true);
            await releaseRefresh.Task;
            await SeedStoreSalesStatisticAsync(refreshDate, "S2", "Store B", 80m, 1);
        };

        var pending = await service.GetExecutiveBranchPerformanceAsync(range);
        await refreshStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.True(pending.StatisticsPending);
        Assert.Contains(pending.Items, row => row.BranchCode == "S2" && row.Revenue == 0m);

        releaseRefresh.TrySetResult(true);
        await refreshCoordinatorFinished.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        var refreshed = await service.GetExecutiveBranchPerformanceAsync(range);

        Assert.False(refreshed.StatisticsPending);
        Assert.Contains(refreshed.Items, row => row.BranchCode == "S2" && row.Revenue == 80m);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_快速补算少写分店时仍标记待完成且不缓存()
    {
        var date = new DateTime(2026, 7, 7);
        await SeedStoreAsync("S1", "Store A");
        await SeedStoreAsync("S2", "Store B");
        await SeedStoreSalesStatisticAsync(date, "S1", "Store A", 10m, 1);
        await SeedPosmOrderWithPaymentAsync("quick-partial-current-1", date.AddHours(10), "S1", 120m, 2);
        await SeedPosmOrderWithPaymentAsync("quick-partial-current-2", date.AddHours(11), "S2", 80m, 1);

        var service = CreateService();
        service.StoreStatisticsRefreshTestInterceptor = async refreshDate =>
        {
            await _localDb.Deleteable<StoreSalesStatistic>()
                .Where(row => row.Date == refreshDate.Date)
                .ExecuteCommandAsync();
            // 模拟补算任务在 250ms 内结束，但意外只写入一家分店。
            await SeedStoreSalesStatisticAsync(refreshDate, "S1", "Store A", 120m, 1);
        };

        var range = new DateRangeDto { StartDate = date, EndDate = date };
        var first = await service.GetExecutiveBranchPerformanceAsync(range);
        var second = await service.GetExecutiveBranchPerformanceAsync(range);

        Assert.True(first.StatisticsPending);
        Assert.Equal(2, first.StatisticsExpectedBranchCount);
        Assert.Equal(2, first.StatisticsSnapshotBranchCount);
        Assert.True(second.StatisticsPending);
        Assert.Equal(2, second.StatisticsExpectedBranchCount);
        Assert.Equal(2, second.StatisticsSnapshotBranchCount);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_快速补算后无法核验来源身份时待完成且不缓存()
    {
        var date = new DateTime(2026, 7, 7);
        await SeedStoreAsync("S1", "Store A");
        await SeedPosmOrderWithPaymentAsync("quick-source-unverified", date.AddHours(10), "S1", 120m, 1);

        var refreshCount = 0;
        var service = CreateService();
        service.StoreStatisticsRefreshTestInterceptor = async refreshDate =>
        {
            refreshCount += 1;
            await SeedStoreSalesStatisticAsync(refreshDate, "S1", "Store A", 120m, 1);
        };
        // 补算完成后 POSM 来源暂不可读，排行不能把当前快照当作已核验结果缓存。
        service.ExpectedExecutiveBranchCodesTestInterceptor = () =>
            throw new InvalidOperationException("POSM 来源身份暂不可核验");

        var range = new DateRangeDto { StartDate = date, EndDate = date };
        var first = await service.GetExecutiveBranchPerformanceAsync(range);
        await _localDb.Updateable<StoreSalesStatistic>()
            .SetColumns(row => row.TotalAmount == 150m)
            .Where(row => row.Date == date && row.BranchCode == "S1")
            .ExecuteCommandAsync();
        var second = await service.GetExecutiveBranchPerformanceAsync(range);

        Assert.True(first.StatisticsPending);
        Assert.False(second.StatisticsPending);
        Assert.Equal(150m, Assert.Single(second.Items).Revenue);
        Assert.Equal(1, refreshCount);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_二十八分店补算漏掉真实销售分店时按身份标记待完成()
    {
        var date = new DateTime(2026, 7, 8);
        var branchCodes = Enumerable.Range(1, 28).Select(index => $"S{index:00}").ToList();
        foreach (var branchCode in branchCodes)
            await SeedStoreAsync(branchCode, $"Store {branchCode}");

        // S02 有真实 POSM 销售，但快速补算只写 S01；其余授权分店无销售，允许被补成零值行。
        await SeedPosmOrderWithPaymentAsync("identity-gap-sales", date.AddHours(10), "S02", 88m, 1);
        var service = CreateService();
        service.StoreStatisticsRefreshTestInterceptor = async refreshDate =>
        {
            await _localDb.Deleteable<StoreSalesStatistic>()
                .Where(row => row.Date == refreshDate.Date)
                .ExecuteCommandAsync();
            await SeedStoreSalesStatisticAsync(refreshDate, "S01", "Store S01", 0m, 0);
        };

        var result = await service.GetExecutiveBranchPerformanceAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            branchCodes: branchCodes
        );

        Assert.Equal(28, result.Items.Count);
        Assert.Equal(28, result.StatisticsExpectedBranchCount);
        Assert.Equal(28, result.StatisticsSnapshotBranchCount);
        Assert.True(result.StatisticsPending);
        Assert.Contains(result.Items, row => row.BranchCode == "S02" && row.Revenue == 0m);
    }

    [Fact]
    public async Task GetExecutiveHourlyTrafficAsync_使用小时统计表按分店和小时聚合()
    {
        await SeedHourlySalesStatisticAsync(new DateTime(2026, 7, 1), 9, "S1", "Store A", 100m, 4);
        await SeedHourlySalesStatisticAsync(new DateTime(2026, 7, 2), 9, "S1", "Store B", 50m, 2);
        await SeedHourlySalesStatisticAsync(new DateTime(2025, 7, 1), 9, "S1", "Store Old", 80m, 3);
        await SeedHourlySalesStatisticAsync(new DateTime(2025, 7, 2), 9, "S1", "Store Old", 20m, 1);
        await SeedSalesOrderAsync("hourly-group-current-1", new DateTime(2026, 7, 1, 9, 0, 0), "S1");
        await SeedSalesOrderAsync("hourly-group-current-2", new DateTime(2026, 7, 2, 9, 0, 0), "S1");
        await SeedSalesOrderAsync("hourly-group-compare-1", new DateTime(2025, 7, 1, 9, 0, 0), "S1");
        await SeedSalesOrderAsync("hourly-group-compare-2", new DateTime(2025, 7, 2, 9, 0, 0), "S1");
        var service = CreateService();

        var result = await service.GetExecutiveHourlyTrafficAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 2),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 2),
            },
            new List<string> { "S1" }
        );

        var row = Assert.Single(result);
        Assert.Equal("09:00", row.Hour);
        Assert.Equal("S1", row.BranchCode);
        Assert.Equal(150m, row.Revenue);
        Assert.Equal(100m, row.RevenueLY);
        Assert.Equal(6, row.OrderCount);
        Assert.Equal(4, row.OrderCountLY);
        Assert.Equal(100, row.Percentage);
        Assert.True(row.IsPeak);
    }

    [Fact]
    public async Task GetExecutiveHourlyTrafficAsync_同期独有分店小时也返回并将本期补零()
    {
        var currentDate = new DateTime(2026, 7, 1);
        var compareDate = new DateTime(2025, 7, 1);
        await SeedHourlySalesStatisticAsync(compareDate, 9, "S2", "Store B", 80m, 4);
        await SeedSalesOrderAsync("hourly-compare-only", compareDate.AddHours(9), "S2");
        var service = CreateService();

        var result = await service.GetExecutiveHourlyTrafficAsync(
            new DateRangeDto
            {
                StartDate = currentDate,
                EndDate = currentDate,
                CompareStartDate = compareDate,
                CompareEndDate = compareDate,
            }
        );

        var row = Assert.Single(result.Items);
        Assert.Equal("09:00", row.Hour);
        Assert.Equal("S2", row.BranchCode);
        Assert.Equal(0m, row.Revenue);
        Assert.Equal(80m, row.RevenueLY);
        Assert.Equal(0, row.OrderCount);
        Assert.Equal(4, row.OrderCountLY);
        Assert.Equal(0, row.Percentage);
        Assert.False(row.IsPeak);
    }

    [Fact]
    public async Task GetExecutiveHourlyTrafficAsync_全分店查询排除All汇总行()
    {
        await SeedHourlySalesStatisticAsync(new DateTime(2026, 7, 4), 10, "ALL", "All Stores", 300m, 10);
        await SeedHourlySalesStatisticAsync(new DateTime(2026, 7, 4), 10, "S1", "Store A", 120m, 4);
        await SeedHourlySalesStatisticAsync(new DateTime(2025, 7, 5), 10, "ALL", "All Stores", 200m, 8);
        await SeedHourlySalesStatisticAsync(new DateTime(2025, 7, 5), 10, "S1", "Store A", 80m, 2);
        await SeedSalesOrderAsync("hourly-all-current", new DateTime(2026, 7, 4, 10, 0, 0), "S1");
        await SeedSalesOrderAsync("hourly-all-compare", new DateTime(2025, 7, 5, 10, 0, 0), "S1");
        var service = CreateService();

        var result = await service.GetExecutiveHourlyTrafficAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
                CompareStartDate = new DateTime(2025, 7, 5),
                CompareEndDate = new DateTime(2025, 7, 5),
            }
        );

        var row = Assert.Single(result);
        Assert.Equal("S1", row.BranchCode);
        Assert.Equal(120m, row.Revenue);
        Assert.Equal(4, row.OrderCount);
        Assert.Equal(80m, row.RevenueLY);
        Assert.Equal(2, row.OrderCountLY);
    }

    [Fact]
    public async Task GetExecutiveHourlyTrafficAsync_全分店按启用门店目录排除停用门店_显式范围仍按请求()
    {
        await SeedStoreAsync("S1", "Store A");
        await _localDb.Insertable(new Store
        {
            StoreGUID = "store-S9",
            StoreCode = "S9",
            StoreName = "Closed Store",
            IsActive = false,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedHourlySalesStatisticAsync(new DateTime(2026, 7, 4), 10, "S1", "Store A", 120m, 4);
        await SeedHourlySalesStatisticAsync(new DateTime(2025, 7, 5), 10, "S1", "Store A", 80m, 2);
        // 停用门店今年已无销售，去年同期仍有分时：全分店视图不能把它算进同期。
        await SeedHourlySalesStatisticAsync(new DateTime(2025, 7, 5), 10, "S9", "Closed Store", 55m, 3);
        await SeedSalesOrderAsync("hourly-active-current", new DateTime(2026, 7, 4, 10, 0, 0), "S1");
        await SeedSalesOrderAsync("hourly-active-compare", new DateTime(2025, 7, 5, 10, 0, 0), "S1");
        await SeedSalesOrderAsync("hourly-closed-compare", new DateTime(2025, 7, 5, 10, 5, 0), "S9");
        var service = CreateService();
        var range = new DateRangeDto
        {
            StartDate = new DateTime(2026, 7, 4),
            EndDate = new DateTime(2026, 7, 4),
            CompareStartDate = new DateTime(2025, 7, 5),
            CompareEndDate = new DateTime(2025, 7, 5),
        };

        var allStores = await service.GetExecutiveHourlyTrafficAsync(range);

        var row = Assert.Single(allStores);
        Assert.Equal("S1", row.BranchCode);
        Assert.Equal(80m, row.RevenueLY);

        var explicitScope = await service.GetExecutiveHourlyTrafficAsync(range, new List<string> { "S1", "S9" });
        Assert.Equal(55m, Assert.Single(explicitScope, item => item.BranchCode == "S9").RevenueLY);
    }

    [Fact]
    public async Task GetExecutiveHourlyTrafficAsync_历史统计缺失时重算并返回去年客单数()
    {
        await SeedStoreAsync("S1", "Store A");
        await SeedHourlySalesStatisticAsync(new DateTime(2026, 7, 4), 10, "S1", "Store A", 200m, 4);
        await SeedPosmOrderWithPaymentAsync("old-hour-1", new DateTime(2025, 7, 5, 10, 10, 0), "S1", 35m, 1);
        await SeedPosmOrderWithPaymentAsync("old-hour-2", new DateTime(2025, 7, 5, 10, 40, 0), "S1", 45m, 2);
        var service = CreateService();

        var result = await service.GetExecutiveHourlyTrafficAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
                CompareStartDate = new DateTime(2025, 7, 5),
                CompareEndDate = new DateTime(2025, 7, 5),
            },
            new List<string> { "S1" }
        );

        var row = Assert.Single(result);
        Assert.Equal(80m, row.RevenueLY);
        Assert.Equal(2, row.OrderCountLY);
    }

    [Fact]
    public async Task GetExecutiveHourlyTrafficAsync_同期仅HBSales有单据时补算并返回去年小时数据()
    {
        // 2025-07-05 在 HBSales 历史窗口内，且该分店当天只有旧系统单据：
        // 完整性核验必须把它视为应有数据，补算后同期小时行才不会是 0。
        await SeedStoreAsync("S1", "Store A");
        // 本期用真实 POSM 订单，没有来源覆盖的裸统计行会被视为残留快照而重算掉。
        await SeedPosmOrderWithPaymentAsync("current-dual-1", new DateTime(2026, 7, 4, 10, 10, 0), "S1", 50m, 1);
        await SeedPosmOrderWithPaymentAsync("current-dual-2", new DateTime(2026, 7, 4, 10, 40, 0), "S1", 70m, 1);
        await SeedHbSalesOrderAsync("hb-old-hour-1", new DateTime(2025, 7, 5, 10, 10, 0), "S1", 35m, 1);
        await SeedHbSalesOrderAsync("hb-old-hour-2", new DateTime(2025, 7, 5, 10, 40, 0), "S1", 45m, 2);
        var service = CreateService();
        var range = new DateRangeDto
        {
            StartDate = new DateTime(2026, 7, 4),
            EndDate = new DateTime(2026, 7, 4),
            CompareStartDate = new DateTime(2025, 7, 5),
            CompareEndDate = new DateTime(2025, 7, 5),
        };

        var result = await service.GetExecutiveHourlyTrafficAsync(range, new List<string> { "S1" });
        for (var attempt = 0; result.StatisticsPending && attempt < 200; attempt++)
        {
            await Task.Delay(50);
            result = await service.GetExecutiveHourlyTrafficAsync(range, new List<string> { "S1" });
        }

        Assert.False(result.StatisticsPending);
        var row = Assert.Single(result);
        Assert.Equal("10:00", row.Hour);
        Assert.Equal(120m, row.Revenue);
        Assert.Equal(2, row.OrderCount);
        Assert.Equal(80m, row.RevenueLY);
        Assert.Equal(2, row.OrderCountLY);
    }

    [Fact]
    public async Task GetExecutiveHourlyTrafficAsync_本期小时客单缺失时重算当前日期()
    {
        await SeedStoreAsync("S1", "Store A");
        await SeedHourlySalesStatisticAsync(new DateTime(2026, 7, 4), 10, "S1", "Store A", 120m, 0);
        await SeedHourlySalesStatisticAsync(new DateTime(2025, 7, 5), 10, "S1", "Store A", 80m, 2);
        await SeedPosmOrderWithPaymentAsync("current-hour-1", new DateTime(2026, 7, 4, 10, 10, 0), "S1", 50m, 1);
        await SeedPosmOrderWithPaymentAsync("current-hour-2", new DateTime(2026, 7, 4, 10, 40, 0), "S1", 70m, 1);
        await SeedSalesOrderAsync("current-hour-compare", new DateTime(2025, 7, 5, 10, 0, 0), "S1");
        var service = CreateService();
        var range = new DateRangeDto
        {
            StartDate = new DateTime(2026, 7, 4),
            EndDate = new DateTime(2026, 7, 4),
            CompareStartDate = new DateTime(2025, 7, 5),
            CompareEndDate = new DateTime(2025, 7, 5),
        };

        var result = await service.GetExecutiveHourlyTrafficAsync(
            range,
            new List<string> { "S1" }
        );
        for (var attempt = 0; result.StatisticsPending && attempt < 200; attempt++)
        {
            await Task.Delay(50);
            result = await service.GetExecutiveHourlyTrafficAsync(
                range,
                new List<string> { "S1" }
            );
        }

        Assert.False(result.StatisticsPending);
        var row = Assert.Single(result);
        Assert.Equal("10:00", row.Hour);
        Assert.Equal(120m, row.Revenue);
        Assert.Equal(2, row.OrderCount);
        Assert.Equal(80m, row.RevenueLY);
        Assert.Equal(2, row.OrderCountLY);
    }

    [Fact]
    public async Task GetExecutiveHourlyTrafficAsync_快速补算漏写真实销售分店时仍返回待完成包络()
    {
        var date = new DateTime(2026, 7, 9);
        await SeedStoreAsync("S1", "Store A");
        await SeedStoreAsync("S2", "Store B");
        await SeedPosmOrderWithPaymentAsync("hourly-identity-gap", date.AddHours(9), "S2", 66m, 1);
        var service = CreateService();
        service.HourlyStatisticsRefreshTestInterceptor = async refreshDate =>
        {
            await _localDb.Deleteable<HourlySalesStatistic>()
                .Where(row => row.Date == refreshDate.Date)
                .ExecuteCommandAsync();
            await SeedHourlySalesStatisticAsync(refreshDate, 9, "S1", "Store A", 0m, 0);
        };

        var result = await service.GetExecutiveHourlyTrafficAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            new List<string> { "S1", "S2" }
        );

        Assert.True(result.StatisticsPending);
        Assert.Equal(result.Items.Count + 1, result.StatisticsExpectedItemCount);
        Assert.Equal(result.Items.Count, result.StatisticsSnapshotItemCount);
    }

    [Fact]
    public async Task GetExecutiveHourlyTrafficAsync_二十八家授权分店含零销售分店时空统计视为完整()
    {
        var date = new DateTime(2026, 7, 11);
        var branchCodes = Enumerable.Range(1, 28).Select(index => $"S{index:D2}").ToList();
        foreach (var branchCode in branchCodes)
        {
            await SeedStoreAsync(branchCode, $"Store {branchCode}");
        }

        // 只有 S01 在 POSM 有实际销售；其余授权分店没有日销售，小时统计不应要求逐店造零行。
        await SeedPosmOrderWithPaymentAsync("hourly-zero-sales", date.AddHours(9), "S01", 88m, 1);
        await SeedHourlySalesStatisticAsync(date, 9, "S01", "Store S01", 88m, 1);
        var service = CreateService();

        var result = await service.GetExecutiveHourlyTrafficAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            branchCodes
        );

        Assert.False(result.StatisticsPending);
        var row = Assert.Single(result.Items);
        Assert.Equal("S01", row.BranchCode);
        Assert.Equal(88m, row.Revenue);
    }

    [Fact]
    public async Task GetExecutiveHourlyTrafficAsync_全分店快速补算漏真实销售分店时仍待完成且不缓存()
    {
        var date = new DateTime(2026, 7, 12);
        await SeedStoreAsync("S01", "Store S01");
        await SeedStoreAsync("S02", "Store S02");
        await SeedPosmOrderWithPaymentAsync("hourly-all-s01", date.AddHours(9), "S01", 10m, 1);
        await SeedPosmOrderWithPaymentAsync("hourly-all-s02", date.AddHours(10), "S02", 20m, 1);
        var refreshCount = 0;
        var service = CreateService();
        service.HourlyStatisticsRefreshTestInterceptor = async refreshDate =>
        {
            refreshCount += 1;
            await _localDb.Deleteable<HourlySalesStatistic>()
                .Where(row => row.Date == refreshDate.Date)
                .ExecuteCommandAsync();
            // 模拟快速补算结束却少写 S02：必须继续标记 Pending，且不可写入缓存。
            await SeedHourlySalesStatisticAsync(refreshDate, 9, "S01", "Store S01", 10m, 1);
        };

        var range = new DateRangeDto { StartDate = date, EndDate = date };
        var first = await service.GetExecutiveHourlyTrafficAsync(range);
        var second = await service.GetExecutiveHourlyTrafficAsync(range);

        Assert.True(first.StatisticsPending);
        Assert.True(second.StatisticsPending);
        Assert.Equal(2, refreshCount);
    }

    [Fact]
    public async Task GetExecutiveHourlyTrafficAsync_稳定统计只扫描一次POSM覆盖后命中缓存()
    {
        var date = new DateTime(2026, 7, 13);
        await SeedStoreAsync("S1", "Store A");
        await SeedPosmOrderWithPaymentAsync("hourly-stable-coverage", date.AddHours(9), "S1", 88m, 1);
        await SeedHourlySalesStatisticAsync(date, 9, "S1", "Store A", 88m, 1);

        var coverageReadCount = 0;
        var service = CreateService();
        service.PosmStoreSalesCoverageReadTestInterceptor = () => coverageReadCount += 1;

        var range = new DateRangeDto { StartDate = date, EndDate = date };
        var first = await service.GetExecutiveHourlyTrafficAsync(range, new List<string> { "S1" });
        var second = await service.GetExecutiveHourlyTrafficAsync(range, new List<string> { "S1" });

        Assert.False(first.StatisticsPending);
        Assert.False(second.StatisticsPending);
        Assert.Equal(1, coverageReadCount);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_历史日期StoreSales完成快照跳过来源覆盖扫描()
    {
        var currentDate = new DateTime(2026, 7, 14);
        var compareDate = new DateTime(2025, 7, 15);
        await SeedStoreAsync("S1", "Store A");
        await SeedStoreSalesStatisticAsync(currentDate, "S1", "Store A", 88m, 1);
        await SeedStoreSalesStatisticAsync(compareDate, "S1", "Store A", 66m, 1);
        await _localDb.Insertable(new[]
        {
            CreateFreshStoreRefreshState(currentDate),
            CreateFreshStoreRefreshState(compareDate),
        }).ExecuteCommandAsync();
        var coverageReadCount = 0;
        var service = CreateService();
        service.PosmStoreSalesCoverageReadTestInterceptor = () => coverageReadCount += 1;
        _hbSalesDb.Aop.OnLogExecuting = (_, _) => throw new InvalidOperationException("历史完成快照不得读取 HBSales 来源");
        var result = await service.GetExecutiveBranchPerformanceAsync(
            new DateRangeDto
            {
                StartDate = currentDate,
                EndDate = currentDate,
                CompareStartDate = compareDate,
                CompareEndDate = compareDate,
            },
            branchCodes: new List<string> { "S1" }
        );

        Assert.False(result.StatisticsPending);
        Assert.Equal(88m, Assert.Single(result.Items).Revenue);
        Assert.Equal(66m, Assert.Single(result.Items).RevenueLY);
        Assert.Equal(0, coverageReadCount);
    }

    [Theory]
    [InlineData(null, true, true, true)]
    [InlineData(SalesStatisticRefreshStatus.Running, true, true, true)]
    [InlineData(SalesStatisticRefreshStatus.Failed, true, true, true)]
    [InlineData(SalesStatisticRefreshStatus.Stale, true, true, true)]
    [InlineData(SalesStatisticRefreshStatus.Fresh, false, true, true)]
    [InlineData(SalesStatisticRefreshStatus.Fresh, true, false, true)]
    [InlineData(SalesStatisticRefreshStatus.Fresh, true, true, false)]
    public async Task GetBranchDailyPerformanceAsync_历史快照证明不完整时仍核验来源(
        string? status, bool completed, bool aggregated, bool hasStatisticRows)
    {
        var date = DateTime.Today.AddDays(-2);
        await SeedStoreAsync("S1", "Store A");
        await SeedPosmOrderWithPaymentAsync("uncertified", date.AddHours(10), "S1", 88m, 1);
        if (hasStatisticRows)
            await SeedStoreSalesStatisticAsync(date, "S1", "Store A", 88m, 1);
        if (status != null)
        {
            var state = CreateFreshStoreRefreshState(date);
            state.Status = status;
            state.CompletedAtUtc = completed ? DateTime.UtcNow : null;
            state.LastAggregatedAtUtc = aggregated ? DateTime.UtcNow : null;
            await _localDb.Insertable(state).ExecuteCommandAsync();
        }

        var coverageReads = 0;
        var service = CreateService();
        service.PosmStoreSalesCoverageReadTestInterceptor = () => coverageReads++;
        service.StoreStatisticsRefreshTestInterceptor = _ => Task.CompletedTask;
        var result = await service.GetBranchDailyPerformanceAsync(
            new DateRangeDto { StartDate = date, EndDate = date }, new List<string> { "S1" });

        Assert.True(coverageReads > 0);
        Assert.Equal(!hasStatisticRows, result.StatisticsPending);
    }

    [Fact]
    public async Task GetBranchDailyPerformanceAsync_当天即使有完成快照仍核验来源()
    {
        var date = DateTime.Today;
        await SeedStoreAsync("S1", "Store A");
        await SeedStoreSalesStatisticAsync(date, "S1", "Store A", 88m, 1);
        await SeedPosmOrderWithPaymentAsync("today-fresh", date.AddHours(1), "S1", 88m, 1);
        await _localDb.Insertable(CreateFreshStoreRefreshState(date)).ExecuteCommandAsync();

        var coverageReads = 0;
        var service = CreateService();
        service.PosmStoreSalesCoverageReadTestInterceptor = () => coverageReads++;
        var result = await service.GetBranchDailyPerformanceAsync(
            new DateRangeDto { StartDate = date, EndDate = date }, new List<string> { "S1" });

        Assert.False(result.StatisticsPending);
        Assert.Equal(1, coverageReads);
    }

    [Fact]
    public async Task GetBranchDailyPerformanceAsync_混合区间只扫描未完成日期的来源()
    {
        var first = DateTime.Today.AddDays(-4);
        var middle = first.AddDays(1);
        var last = first.AddDays(2);
        await SeedStoreAsync("S1", "Store A");
        foreach (var date in new[] { first, middle, last })
            await SeedStoreSalesStatisticAsync(date, "S1", "Store A", 88m, 1);
        await _localDb.Insertable(new[]
        {
            CreateFreshStoreRefreshState(first), CreateFreshStoreRefreshState(last),
        }).ExecuteCommandAsync();
        await SeedPosmOrderWithPaymentAsync("mixed-middle", middle.AddHours(10), "S1", 88m, 1);

        var coverageReads = 0;
        var refreshDates = new List<DateTime>();
        var service = CreateService();
        service.PosmStoreSalesCoverageReadTestInterceptor = () => coverageReads++;
        service.StoreStatisticsRefreshTestInterceptor = date =>
        {
            refreshDates.Add(date);
            return Task.CompletedTask;
        };
        var result = await service.GetBranchDailyPerformanceAsync(
            new DateRangeDto { StartDate = first, EndDate = last }, new List<string> { "S1" });

        Assert.False(result.StatisticsPending);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(264m, result.Items.Sum(row => row.Revenue));
        Assert.Equal(1, coverageReads);
        // 已完成的首尾日期没有原始来源；若仍扫描整个区间，就会误触发它们的补算。
        Assert.Empty(refreshDates);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_完成历史快照保留授权范围内零销售分店()
    {
        var date = DateTime.Today.AddDays(-2);
        await SeedStoreAsync("S1", "Store A");
        await SeedStoreAsync("S2", "Store B");
        await SeedStoreSalesStatisticAsync(date, "S1", "Store A", 88m, 1);
        await _localDb.Insertable(CreateFreshStoreRefreshState(date)).ExecuteCommandAsync();
        var service = CreateService();
        service.PosmStoreSalesCoverageReadTestInterceptor = () => throw new InvalidOperationException("不应读取来源");

        var result = await service.GetExecutiveBranchPerformanceAsync(
            new DateRangeDto { StartDate = date, EndDate = date }, branchCodes: new List<string> { "S1", "S2" });

        Assert.False(result.StatisticsPending);
        Assert.Equal(2, result.StatisticsExpectedBranchCount);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(88m, result.Items.Single(row => row.BranchCode == "S1").Revenue);
        Assert.Equal(0m, result.Items.Single(row => row.BranchCode == "S2").Revenue);
    }

    [Fact]
    public async Task GetBranchDailyPerformanceAsync_快速补算漏写真实销售分店时仍返回待完成包络()
    {
        var date = new DateTime(2026, 7, 10);
        await SeedStoreAsync("S1", "Store A");
        await SeedStoreAsync("S2", "Store B");
        await SeedPosmOrderWithPaymentAsync("daily-identity-gap", date.AddHours(10), "S2", 77m, 1);
        var service = CreateService();
        service.StoreStatisticsRefreshTestInterceptor = async refreshDate =>
        {
            await _localDb.Deleteable<StoreSalesStatistic>()
                .Where(row => row.Date == refreshDate.Date)
                .ExecuteCommandAsync();
            await SeedStoreSalesStatisticAsync(refreshDate, "S1", "Store A", 0m, 0);
        };

        var result = await service.GetBranchDailyPerformanceAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            new List<string> { "S1", "S2" }
        );

        Assert.True(result.StatisticsPending);
        Assert.Equal(result.Items.Count + 1, result.StatisticsExpectedItemCount);
        Assert.Equal(result.Items.Count, result.StatisticsSnapshotItemCount);
    }

    [Fact]
    public async Task GetBranchDailyPerformanceAsync_稳定统计只扫描一次POSM覆盖后命中缓存()
    {
        var date = new DateTime(2026, 7, 14);
        await SeedStoreAsync("S1", "Store A");
        await SeedPosmOrderWithPaymentAsync("daily-stable-coverage", date.AddHours(10), "S1", 77m, 1);
        await SeedStoreSalesStatisticAsync(date, "S1", "Store A", 77m, 1);

        var coverageReadCount = 0;
        var service = CreateService();
        service.PosmStoreSalesCoverageReadTestInterceptor = () => coverageReadCount += 1;

        var range = new DateRangeDto { StartDate = date, EndDate = date };
        var first = await service.GetBranchDailyPerformanceAsync(range, new List<string> { "S1" });
        var second = await service.GetBranchDailyPerformanceAsync(range, new List<string> { "S1" });

        Assert.False(first.StatisticsPending);
        Assert.False(second.StatisticsPending);
        Assert.Equal(1, coverageReadCount);
    }

    [Fact]
    public async Task GetBranchDailyPerformanceAsync_POSM分店为空时按设备映射核验来源身份()
    {
        var date = new DateTime(2026, 7, 15);
        await SeedStoreAsync("S1", "Store A");
        await SeedDeviceAsync("DEVICE-S1", "S1");
        await SeedSalesOrderAsync("daily-device-fallback", date.AddHours(9), null, "device-s1");
        await SeedStoreSalesStatisticAsync(date, "S1", "Store A", 77m, 1);

        var refreshDates = new List<DateTime>();
        var service = CreateService();
        service.StoreStatisticsRefreshTestInterceptor = refreshDate =>
        {
            refreshDates.Add(refreshDate.Date);
            return Task.CompletedTask;
        };

        var result = await service.GetBranchDailyPerformanceAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            new List<string> { "S1" }
        );

        Assert.Empty(refreshDates);
        Assert.False(result.StatisticsPending);
        Assert.Equal(77m, Assert.Single(result.Items).Revenue);
    }

    [Fact]
    public async Task GetExecutiveHourlyTrafficAsync_POSM分店为空时按设备映射核验来源身份()
    {
        var date = new DateTime(2026, 7, 16);
        await SeedStoreAsync("S1", "Store A");
        await SeedDeviceAsync("DEVICE-S1", "S1");
        await SeedSalesOrderAsync("hourly-device-fallback", date.AddHours(9), null, "device-s1");
        await SeedHourlySalesStatisticAsync(date, 9, "S1", "Store A", 66m, 1);

        var refreshDates = new List<DateTime>();
        var service = CreateService();
        service.HourlyStatisticsRefreshTestInterceptor = refreshDate =>
        {
            refreshDates.Add(refreshDate.Date);
            return Task.CompletedTask;
        };

        var result = await service.GetExecutiveHourlyTrafficAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            new List<string> { "S1" }
        );

        Assert.Empty(refreshDates);
        Assert.False(result.StatisticsPending);
        Assert.Equal(66m, Assert.Single(result.Items).Revenue);
    }

    [Fact]
    public async Task 营业额排行分时日表共享当前与同期POSM覆盖快照()
    {
        var currentDate = new DateTime(2026, 7, 19);
        var compareDate = new DateTime(2024, 7, 19);
        await SeedStoreAsync("S1", "Store A");
        await SeedPosmOrderWithPaymentAsync("shared-current", currentDate.AddHours(9), "S1", 88m, 1);
        await SeedPosmOrderWithPaymentAsync("shared-compare", compareDate.AddHours(9), "S1", 66m, 1);
        await SeedStoreSalesStatisticAsync(currentDate, "S1", "Store A", 88m, 1);
        await SeedStoreSalesStatisticAsync(compareDate, "S1", "Store A", 66m, 1);
        await SeedHourlySalesStatisticAsync(currentDate, 9, "S1", "Store A", 88m, 1);
        await SeedHourlySalesStatisticAsync(compareDate, 9, "S1", "Store A", 66m, 1);

        var coverageReadCount = 0;
        var service = CreateService();
        service.PosmStoreSalesCoverageReadTestInterceptor = () => coverageReadCount += 1;
        var range = new DateRangeDto
        {
            StartDate = currentDate,
            EndDate = currentDate,
            CompareStartDate = compareDate,
            CompareEndDate = compareDate,
        };

        await service.GetExecutiveBranchPerformanceAsync(range, branchCodes: new List<string> { "S1" });
        await service.GetExecutiveHourlyTrafficAsync(range, new List<string> { "S1" });
        await service.GetBranchDailyPerformanceAsync(range, new List<string> { "S1" });

        Assert.Equal(2, coverageReadCount);
    }

    [Fact]
    public async Task POSM覆盖快照并发首读仅执行一次底层查询()
    {
        var date = new DateTime(2026, 7, 20);
        await SeedStoreAsync("S1", "Store A");
        await SeedPosmOrderWithPaymentAsync("single-flight", date.AddHours(9), "S1", 88m, 1);
        await SeedHourlySalesStatisticAsync(date, 9, "S1", "Store A", 88m, 1);

        using var sharedCache = new MemoryCache(new MemoryCacheOptions());
        using var firstScope = CreateIsolatedServiceScope(sharedCache);
        using var secondScope = CreateIsolatedServiceScope(sharedCache);
        var coverageReadCount = 0;
        Action countRead = () =>
        {
            Interlocked.Increment(ref coverageReadCount);
            Thread.Sleep(100);
        };
        firstScope.Service.PosmStoreSalesCoverageReadTestInterceptor = countRead;
        secondScope.Service.PosmStoreSalesCoverageReadTestInterceptor = countRead;
        var range = new DateRangeDto { StartDate = date, EndDate = date };

        var firstTask = Task.Run(() => firstScope.Service.GetExecutiveHourlyTrafficAsync(range, new List<string> { "S1" }));
        var secondTask = Task.Run(() => secondScope.Service.GetExecutiveHourlyTrafficAsync(range, new List<string> { "S1" }));
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.False(result.StatisticsPending));
        Assert.Equal(1, coverageReadCount);
    }

    [Fact]
    public async Task POSM覆盖读取失败不写入短缓存()
    {
        var date = new DateTime(2026, 7, 21);
        await SeedStoreSalesStatisticAsync(date, "S1", "Store A", 88m, 1);
        var coverageReadCount = 0;
        var service = CreateService();
        service.PosmStoreSalesCoverageReadTestInterceptor = () =>
        {
            coverageReadCount += 1;
            throw new InvalidOperationException("模拟来源读取失败");
        };
        // 本用例只关心覆盖缓存，重算桩化为立即完成，避免真实统计任务拖慢后台线程。
        service.StoreStatisticsRefreshTestInterceptor = _ => Task.CompletedTask;
        var range = new DateRangeDto { StartDate = date, EndDate = date };

        var first = await service.GetBranchDailyPerformanceAsync(range, new List<string> { "S1" });
        var readsAfterFirst = coverageReadCount;
        var second = await service.GetBranchDailyPerformanceAsync(range, new List<string> { "S1" });
        var readsBySecond = coverageReadCount - readsAfterFirst;

        Assert.True(first.StatisticsPending);
        Assert.True(second.StatisticsPending);
        // 每次调用的读取次数取决于后台重算能否在 250ms 等待窗口内完成（1 或 2 次），
        // 不断言精确值；只要求第二次调用仍真正读取来源，证明失败结果没有被短缓存复用。
        Assert.True(readsAfterFirst >= 1, $"首次调用应读取来源覆盖，实际 {readsAfterFirst} 次");
        Assert.True(readsBySecond >= 1, $"失败结果不应进入短缓存，第二次调用读取 {readsBySecond} 次");
    }

    [Fact]
    public async Task GetBranchDailyPerformanceAsync_来源当日为空但门店统计残留时重算且Pending()
    {
        var date = new DateTime(2026, 7, 22);
        await SeedStoreSalesStatisticAsync(date, "S1", "Store A", 88m, 1);
        var refreshDates = new List<DateTime>();
        var service = CreateService();
        service.StoreStatisticsRefreshTestInterceptor = refreshDate =>
        {
            refreshDates.Add(refreshDate.Date);
            return Task.CompletedTask;
        };

        var result = await service.GetBranchDailyPerformanceAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            new List<string> { "S1" }
        );

        Assert.Contains(date, refreshDates);
        Assert.True(result.StatisticsPending);
    }

    [Fact]
    public async Task GetExecutiveHourlyTrafficAsync_来源当日为空但分时统计残留时重算且Pending()
    {
        var date = new DateTime(2026, 7, 23);
        await SeedHourlySalesStatisticAsync(date, 9, "S1", "Store A", 88m, 1);
        var refreshDates = new List<DateTime>();
        var service = CreateService();
        service.HourlyStatisticsRefreshTestInterceptor = refreshDate =>
        {
            refreshDates.Add(refreshDate.Date);
            return Task.CompletedTask;
        };

        var result = await service.GetExecutiveHourlyTrafficAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            new List<string> { "S1" }
        );

        Assert.Contains(date, refreshDates);
        Assert.True(result.StatisticsPending);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformanceAsync_来源为空但排行残留时二次身份校验仍Pending且不缓存()
    {
        var date = new DateTime(2026, 7, 24);
        await SeedStoreAsync("S1", "Store A");
        await SeedStoreSalesStatisticAsync(date, "S1", "Store A", 88m, 1);
        var refreshCount = 0;
        var service = CreateService();
        service.StoreStatisticsRefreshTestInterceptor = _ =>
        {
            refreshCount += 1;
            return Task.CompletedTask;
        };
        var range = new DateRangeDto { StartDate = date, EndDate = date };

        var first = await service.GetExecutiveBranchPerformanceAsync(range);
        var second = await service.GetExecutiveBranchPerformanceAsync(range);

        Assert.True(first.StatisticsPending);
        Assert.True(second.StatisticsPending);
        Assert.Equal(2, refreshCount);
    }

    [Fact]
    public async Task 营业额报表接口不依赖分店商品统计表()
    {
        await SeedStoreAsync("S1", "Store A");
        await SeedStoreSalesStatisticAsync(new DateTime(2026, 7, 4), "S1", "Store A", 120m, 6);
        await SeedStoreSalesStatisticAsync(new DateTime(2025, 7, 5), "S1", "Store A", 80m, 4);
        await SeedHourlySalesStatisticAsync(new DateTime(2026, 7, 4), 10, "S1", "Store A", 60m, 3);
        await SeedHourlySalesStatisticAsync(new DateTime(2025, 7, 5), 10, "S1", "Store A", 40m, 2);
        await SeedSalesOrderAsync("independence-current", new DateTime(2026, 7, 4, 10, 0, 0), "S1");
        await SeedSalesOrderAsync("independence-compare", new DateTime(2025, 7, 5, 10, 0, 0), "S1");
        await _localDb.Ado.ExecuteCommandAsync("DROP TABLE ProductStoreDailySalesStatistic");
        var service = CreateService();
        var storeRefreshDates = new List<DateTime>();
        var hourlyRefreshDates = new List<DateTime>();
        service.StoreStatisticsRefreshTestInterceptor = date =>
        {
            storeRefreshDates.Add(date.Date);
            return Task.CompletedTask;
        };
        service.HourlyStatisticsRefreshTestInterceptor = date =>
        {
            hourlyRefreshDates.Add(date.Date);
            return Task.CompletedTask;
        };
        var dateRange = new DateRangeDto
        {
            StartDate = new DateTime(2026, 7, 4),
            EndDate = new DateTime(2026, 7, 4),
            CompareStartDate = new DateTime(2025, 7, 5),
            CompareEndDate = new DateTime(2025, 7, 5),
        };

        var summary = await service.GetExecutiveBranchPerformanceAsync(
            dateRange,
            branchCodes: new List<string> { "S1" }
        );
        var hourly = await service.GetExecutiveHourlyTrafficAsync(
            dateRange,
            new List<string> { "S1" }
        );
        var daily = await service.GetBranchDailyPerformanceAsync(
            dateRange,
            new List<string> { "S1" }
        );

        var summaryRow = Assert.Single(summary.Items);
        Assert.Equal(6, summaryRow.OrderCount);
        Assert.Equal(20m, summaryRow.Aov);
        Assert.Equal(4, summaryRow.OrderCountLY);
        Assert.Equal(20m, summaryRow.AovLY);

        var hourlyRow = Assert.Single(hourly);
        Assert.Equal(3, hourlyRow.OrderCount);
        Assert.Equal(2, hourlyRow.OrderCountLY);

        var dailyRow = Assert.Single(daily);
        Assert.Equal(6, dailyRow.OrderCount);
        Assert.Equal(4, dailyRow.OrderCountLY);
        Assert.Empty(storeRefreshDates);
        Assert.Empty(hourlyRefreshDates);
    }

    [Fact]
    public async Task GetProductReportStatisticStatusAsync_公开状态包装保留Fresh水位()
    {
        var completedAt = new DateTime(2026, 7, 2, 3, 4, 5, DateTimeKind.Utc);
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = new DateTime(2026, 7, 1),
            Status = SalesStatisticRefreshStatus.Fresh,
            CompletedAtUtc = completedAt,
            LastAggregatedAtUtc = completedAt,
        }).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.GetProductReportStatisticStatusAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 7, 1), EndDate = new DateTime(2026, 7, 1) }
        );

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        Assert.Null(result.StatisticMessage);
        Assert.NotEqual("none", result.CacheVersion);
        Assert.Equal(completedAt, result.StatisticUpdatedAt);
    }

    [Fact]
    public async Task GetProductReportStatisticStatusAsync_同期未完成时返回Pending并合并缓存版本()
    {
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = new DateTime(2026, 7, 1),
            Status = SalesStatisticRefreshStatus.Fresh,
            CompletedAtUtc = new DateTime(2026, 7, 2, 3, 4, 5, DateTimeKind.Utc),
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = new DateTime(2025, 7, 1),
            Status = SalesStatisticRefreshStatus.Pending,
        }).ExecuteCommandAsync();
        var service = CreateService();

        var currentOnly = await service.GetProductReportStatisticStatusAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 7, 1), EndDate = new DateTime(2026, 7, 1) }
        );
        var withCompare = await service.GetProductReportStatisticStatusAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 1),
            }
        );

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, currentOnly.StatisticStatus);
        Assert.Equal(SalesStatisticRefreshStatus.Pending, withCompare.StatisticStatus);
        Assert.NotEqual(currentOnly.CacheVersion, withCompare.CacheVersion);
    }

    [Theory]
    [InlineData(36)]
    [InlineData(366)]
    public async Task GetProductReportStatisticStatusAsync_供应商快照状态支持长区间并识别同期缺口(int dayCount)
    {
        var currentStart = new DateTime(2026, 7, 1);
        var compareStart = new DateTime(2025, 7, 1);
        var completedAt = new DateTime(2026, 8, 10, 1, 2, 3, DateTimeKind.Utc);
        var types = new[]
        {
            SalesStatisticType.ProductStoreDaily,
            SalesStatisticType.AustralianSupplierStoreSales,
            SalesStatisticType.ChinaSupplierStoreSales,
        };
        static SalesStatisticRefreshState State(
            string type,
            DateTime date,
            DateTime completedAt
        ) => new()
        {
            StatisticType = type,
            Date = date,
            Status = SalesStatisticRefreshStatus.Fresh,
            LastAggregatedAtUtc = completedAt,
            CompletedAtUtc = completedAt,
            SourceProductVersion = "product-version-36d",
        };

        var currentStates = Enumerable.Range(0, dayCount)
            .SelectMany(offset => types.Select(type => State(type, currentStart.AddDays(offset), completedAt)))
            .ToList();
        await _localDb.Insertable(currentStates).ExecuteCommandAsync();

        var currentOnly = await CreateService(useSupplierRollups: true).GetProductReportStatisticStatusAsync(
            new DateRangeDto
            {
                StartDate = currentStart,
                EndDate = currentStart.AddDays(dayCount - 1),
            }
        );
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, currentOnly.StatisticStatus);

        // 同期第 36 天缺国内供应商状态；366 天本期/同期交集复用已写入的状态行。
        // 超过旧 35 天上限时不能再被空日期集合误判 Fresh。
        var compareStates = Enumerable.Range(0, dayCount)
            .Where(offset => compareStart.AddDays(offset) < currentStart
                || compareStart.AddDays(offset) > currentStart.AddDays(dayCount - 1))
            .SelectMany(offset => types
                .Where(type => !(type == SalesStatisticType.ChinaSupplierStoreSales && offset == 35))
                .Select(type => State(type, compareStart.AddDays(offset), completedAt)))
            .ToList();
        await _localDb.Insertable(compareStates).ExecuteCommandAsync();

        var withCompare = await CreateService(useSupplierRollups: true).GetProductReportStatisticStatusAsync(
            new DateRangeDto
            {
                StartDate = currentStart,
                EndDate = currentStart.AddDays(dayCount - 1),
                CompareStartDate = compareStart,
                CompareEndDate = compareStart.AddDays(dayCount - 1),
            }
        );
        Assert.Equal(SalesStatisticRefreshStatus.Pending, withCompare.StatisticStatus);
    }

    [Fact]
    public async Task GetProductReportStatisticStatusAsync_供应商快照超限日期不能以空枚举标记完整()
    {
        var start = new DateTime(2026, 1, 1);
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService(useSupplierRollups: true)
            .GetProductReportStatisticStatusAsync(new DateRangeDto
            {
                StartDate = start,
                EndDate = start.AddDays(366),
            }));
    }

    [Fact]
    public async Task GetProductReportStatisticStatusAsync_失败状态不回显内部错误()
    {
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = new DateTime(2026, 7, 1),
            Status = SalesStatisticRefreshStatus.Failed,
            ErrorMessage = "SQL timeout at internal-server",
        }).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.GetProductReportStatisticStatusAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 7, 1), EndDate = new DateTime(2026, 7, 1) }
        );

        Assert.Equal(SalesStatisticRefreshStatus.Failed, result.StatisticStatus);
        Assert.Equal("商品统计处理失败，请稍后重试。", result.StatisticMessage);
        Assert.DoesNotContain("internal-server", result.StatisticMessage);
    }

    [Fact]
    public async Task GetSupplierStoreSalesAsync_当前Fresh但同期Pending时不读取统计聚合()
    {
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = new DateTime(2026, 7, 1),
            Status = SalesStatisticRefreshStatus.Fresh,
            CompletedAtUtc = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = new DateTime(2025, 7, 1),
            Status = SalesStatisticRefreshStatus.Pending,
        }).ExecuteCommandAsync();
        await _localDb.Ado.ExecuteCommandAsync("DROP TABLE ProductStoreDailySalesStatistic");
        var service = CreateService();

        var result = await service.GetSupplierStoreSalesAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 1),
            },
            new List<string> { "AUS1" }
        );

        Assert.Empty(result);
    }

    [Fact]
    public async Task 商品报表缓存键_同期完成更新后使用新的统计版本()
    {
        var currentDate = new DateTime(2026, 7, 1);
        var compareDate = new DateTime(2025, 7, 1);
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = currentDate,
            Status = SalesStatisticRefreshStatus.Fresh,
            CompletedAtUtc = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc),
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = compareDate,
            Status = SalesStatisticRefreshStatus.Fresh,
            CompletedAtUtc = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc),
        }).ExecuteCommandAsync();
        var dateRange = new DateRangeDto
        {
            StartDate = currentDate,
            EndDate = currentDate,
            CompareStartDate = compareDate,
            CompareEndDate = compareDate,
        };
        var service = CreateService();
        var before = await service.GetProductReportStatisticStatusAsync(dateRange);
        var beforeKey = SalesDashboardCacheKeys.SupplierRank(
            dateRange,
            new List<string> { "S1" },
            20,
            productStatisticCacheVersion: before.CacheVersion
        );

        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(state => state.CompletedAtUtc == new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc))
            .Where(state =>
                state.StatisticType == SalesStatisticType.ProductStoreDaily
                && state.Date >= compareDate
                && state.Date < compareDate.AddDays(1)
            )
            .ExecuteCommandAsync();
        var after = await service.GetProductReportStatisticStatusAsync(dateRange);
        var afterKey = SalesDashboardCacheKeys.SupplierRank(
            dateRange,
            new List<string> { "S1" },
            20,
            productStatisticCacheVersion: after.CacheVersion
        );

        Assert.NotEqual(before.CacheVersion, after.CacheVersion);
        Assert.NotEqual(beforeKey, afterKey);
    }

    [Fact]
    public async Task GetSupplierSalesRankAsync_返回客单数客单价和同期字段()
    {
        await SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS1", "P-AUS-1", "澳洲商品", 100m, 10, 2);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS1", "P-AUS-1B", "澳洲商品补充", 25m, 1, 1);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S2", "AUS1", "P-AUS-2", "澳洲商品二", 50m, 4, 3);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S1", "AUS1", "P-AUS-1", "澳洲商品", 90m, 9, 3);
        await SeedChinaSupplierAsync("CN-BOUNDARY", "边界中国供应商");
        await SeedSupplierMappingAsync("P-CN-BOUNDARY", "200", "CN-BOUNDARY");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "200", "P-CN-BOUNDARY", "中国边界商品", 999m, 9, 9);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "CN-BOUNDARY", "P-CN-BOUNDARY-DIRECT", "中国直接编码商品", 777m, 7, 7);
        var service = CreateService();

        var result = await service.GetSupplierSalesRankAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 1),
            },
            branchCodes: new List<string> { "S1", "S2" },
            topN: 1000
        );

        var row = Assert.Single(result, item => item.SupplierCode == "AUS1");
        Assert.Equal("AUS1", row.SupplierCode);
        Assert.Equal(175m, row.TotalAmount);
        Assert.Equal(6, row.OrderCount);
        Assert.Equal(175m / 6m, row.AverageTransaction);
        Assert.Equal(2, row.StoreCount);
        Assert.Equal(90m, row.CompareTotalAmount);
        Assert.Equal(3, row.CompareOrderCount);
        Assert.Equal(30m, row.CompareAverageTransaction);
        var chinaRow = Assert.Single(result, item => item.SupplierCode == "200");
        Assert.Equal(1776m, chinaRow.TotalAmount);
        Assert.Equal(16, chinaRow.OrderCount);
        Assert.DoesNotContain(result, item => item.SupplierCode == "CN-BOUNDARY");
    }

    [Fact]
    public async Task GetSupplierSalesRankAsync_商品统计为空时使用澳洲供应商统计表()
    {
        await SeedLocalSupplierAsync("AUS-FALLBACK", "澳洲兜底供应商");
        await SeedAustralianSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "AUS-FALLBACK", "澳洲兜底供应商", 123m, 7, 3);
        await SeedAustralianSupplierSalesAsync(new DateTime(2025, 7, 5), "S1", "AUS-FALLBACK", "澳洲兜底供应商", 80m, 4, 2);
        var service = CreateService();

        var result = await service.GetSupplierSalesRankAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
                CompareStartDate = new DateTime(2025, 7, 5),
                CompareEndDate = new DateTime(2025, 7, 5),
            },
            branchCodes: new List<string> { "S1" },
            topN: 1000
        );

        var row = Assert.Single(result);
        Assert.Equal("AUS-FALLBACK", row.SupplierCode);
        Assert.Equal(123m, row.TotalAmount);
        Assert.Equal(3, row.OrderCount);
        Assert.Equal(1, row.StoreCount);
        Assert.Equal(80m, row.CompareTotalAmount);
        Assert.Equal(2, row.CompareOrderCount);
    }

    [Fact]
    public async Task GetSupplierSalesRankAsync_商品统计为空时澳洲200不重复叠加中国拆分表()
    {
        await SeedLocalSupplierAsync("200", "hotbargain");
        await SeedChinaSupplierAsync("CN-FALLBACK", "中国兜底供应商");
        await SeedAustralianSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "200", "hotbargain", 100m, 10, 4);
        await SeedChinaSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "CN-FALLBACK", "中国兜底供应商", 100m, 10, 4);
        await SeedAustralianSupplierSalesAsync(new DateTime(2025, 7, 5), "S1", "200", "hotbargain", 50m, 5, 2);
        await SeedChinaSupplierSalesAsync(new DateTime(2025, 7, 5), "S1", "CN-FALLBACK", "中国兜底供应商", 50m, 5, 2);
        var service = CreateService();

        var result = await service.GetSupplierSalesRankAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
                CompareStartDate = new DateTime(2025, 7, 5),
                CompareEndDate = new DateTime(2025, 7, 5),
            },
            branchCodes: new List<string> { "S1" },
            topN: 1000
        );

        var row = Assert.Single(result);
        Assert.Equal("200", row.SupplierCode);
        Assert.Equal(100m, row.TotalAmount);
        Assert.Equal(4, row.OrderCount);
        Assert.Equal(50m, row.CompareTotalAmount);
        Assert.Equal(2, row.CompareOrderCount);
    }

    [Fact]
    public async Task GetSupplierSalesRankAsync_澳洲200同期缺失时从中国拆分表补同期()
    {
        await SeedLocalSupplierAsync("200", "hotbargain");
        await SeedLocalSupplierAsync("AUS-COMPARE", "澳洲同期供应商");
        await SeedChinaSupplierAsync("CN-FALLBACK", "中国兜底供应商");
        await SeedAustralianSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "200", "hotbargain", 100m, 10, 4);
        await SeedAustralianSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "AUS-COMPARE", "澳洲同期供应商", 60m, 6, 2);
        await SeedAustralianSupplierSalesAsync(new DateTime(2025, 7, 5), "S1", "AUS-COMPARE", "澳洲同期供应商", 30m, 3, 1);
        await SeedChinaSupplierSalesAsync(new DateTime(2025, 7, 5), "S1", "CN-FALLBACK", "中国兜底供应商", 50m, 5, 2);
        var service = CreateService();

        var result = await service.GetSupplierSalesRankAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
                CompareStartDate = new DateTime(2025, 7, 5),
                CompareEndDate = new DateTime(2025, 7, 5),
            },
            branchCodes: new List<string> { "S1" },
            topN: 1000
        );

        var row = Assert.Single(result, item => item.SupplierCode == "200");
        Assert.Equal("200", row.SupplierCode);
        Assert.Equal(100m, row.TotalAmount);
        Assert.Equal(50m, row.CompareTotalAmount);
        Assert.Equal(2, row.CompareOrderCount);
        Assert.Contains(result, item => item.SupplierCode == "AUS-COMPARE" && item.CompareTotalAmount == 30m);
    }

    [Fact]
    public async Task GetSupplierSalesRankAsync_同期商品统计缺200时从中国拆分表补200()
    {
        await SeedLocalSupplierAsync("200", "hotbargain");
        await SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await SeedChinaSupplierAsync("CN-COMPARE", "中国同期供应商");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 4), "S1", "200", "P-CN-CURRENT", "中国当前商品", 100m, 10, 4);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 4), "S1", "AUS1", "P-AUS-CURRENT", "澳洲当前商品", 60m, 6, 2);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 5), "S1", "AUS1", "P-AUS-COMPARE", "澳洲同期商品", 30m, 3, 1);
        await SeedChinaSupplierSalesAsync(new DateTime(2025, 7, 5), "S1", "CN-COMPARE", "中国同期供应商", 50m, 5, 2);
        var service = CreateService();

        var result = await service.GetSupplierSalesRankAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
                CompareStartDate = new DateTime(2025, 7, 5),
                CompareEndDate = new DateTime(2025, 7, 5),
            },
            branchCodes: new List<string> { "S1" },
            topN: 1000
        );

        var row = Assert.Single(result, item => item.SupplierCode == "200");
        Assert.Equal(100m, row.TotalAmount);
        Assert.Equal(50m, row.CompareTotalAmount);
        Assert.Equal(2, row.CompareOrderCount);
        Assert.Contains(result, item => item.SupplierCode == "AUS1" && item.CompareTotalAmount == 30m);
    }

    [Fact]
    public async Task GetSupplierSalesRankAsync_当前商品统计缺200时从中国拆分表补200()
    {
        await SeedLocalSupplierAsync("200", "hotbargain");
        await SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await SeedChinaSupplierAsync("CN-CURRENT", "中国当前供应商");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 4), "S1", "AUS1", "P-AUS-CURRENT", "澳洲当前商品", 60m, 6, 2);
        await SeedChinaSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "CN-CURRENT", "中国当前供应商", 100m, 10, 4);
        var service = CreateService();

        var result = await service.GetSupplierSalesRankAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
            },
            branchCodes: new List<string> { "S1" },
            topN: 1000
        );

        var chinaRow = Assert.Single(result, item => item.SupplierCode == "200");
        Assert.Equal(100m, chinaRow.TotalAmount);
        Assert.Equal(4, chinaRow.OrderCount);
        Assert.Contains(result, item => item.SupplierCode == "AUS1" && item.TotalAmount == 60m);
    }

    [Fact]
    public async Task GetSupplierSalesRankAsync_澳洲200部分商品统计时只补缺失日期()
    {
        await SeedLocalSupplierAsync("200", "hotbargain");
        await SeedChinaSupplierAsync("CN-PARTIAL", "中国部分统计供应商");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 4), "S1", "200", "P-CN-STAT", "中国已统计商品", 100m, 10, 4);
        await SeedChinaSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "CN-PARTIAL", "中国部分统计供应商", 100m, 10, 4);
        await SeedChinaSupplierSalesAsync(new DateTime(2026, 7, 5), "S1", "CN-PARTIAL", "中国部分统计供应商", 50m, 5, 2);
        var service = CreateService();

        var result = await service.GetSupplierSalesRankAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 5),
            },
            branchCodes: new List<string> { "S1" },
            topN: 1000
        );

        var chinaRow = Assert.Single(result, item => item.SupplierCode == "200");
        Assert.Equal(150m, chinaRow.TotalAmount);
        Assert.Equal(6, chinaRow.OrderCount);
    }

    [Fact]
    public async Task GetSupplierStoreSalesAsync_商品统计为空时澳洲200分店下钻不重复叠加中国拆分表()
    {
        await SeedLocalSupplierAsync("200", "hotbargain");
        await SeedChinaSupplierAsync("CN-FALLBACK", "中国兜底供应商");
        await SeedAustralianSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "200", "hotbargain", 100m, 10, 4);
        await SeedChinaSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "CN-FALLBACK", "中国兜底供应商", 100m, 10, 4);
        await SeedAustralianSupplierSalesAsync(new DateTime(2025, 7, 5), "S1", "200", "hotbargain", 50m, 5, 2);
        await SeedChinaSupplierSalesAsync(new DateTime(2025, 7, 5), "S1", "CN-FALLBACK", "中国兜底供应商", 50m, 5, 2);
        var service = CreateService();

        var result = await service.GetSupplierStoreSalesAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
                CompareStartDate = new DateTime(2025, 7, 5),
                CompareEndDate = new DateTime(2025, 7, 5),
            },
            new List<string> { "200" },
            new List<string> { "S1" }
        );

        var row = Assert.Single(result);
        Assert.Equal("200", row.SupplierCode);
        Assert.Equal(100m, row.TotalAmount);
        Assert.Equal(4, row.OrderCount);
        Assert.Equal(50m, row.CompareTotalAmount);
        Assert.Equal(2, row.CompareOrderCount);
    }

    [Fact]
    public async Task GetSupplierStoreSalesAsync_澳洲200同期缺失时从中国拆分表补同期()
    {
        await SeedLocalSupplierAsync("200", "hotbargain");
        await SeedLocalSupplierAsync("AUS-COMPARE", "澳洲同期供应商");
        await SeedChinaSupplierAsync("CN-FALLBACK", "中国兜底供应商");
        await SeedAustralianSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "200", "hotbargain", 100m, 10, 4);
        await SeedAustralianSupplierSalesAsync(new DateTime(2025, 7, 5), "S1", "AUS-COMPARE", "澳洲同期供应商", 30m, 3, 1);
        await SeedChinaSupplierSalesAsync(new DateTime(2025, 7, 5), "S1", "CN-FALLBACK", "中国兜底供应商", 50m, 5, 2);
        var service = CreateService();

        var result = await service.GetSupplierStoreSalesAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
                CompareStartDate = new DateTime(2025, 7, 5),
                CompareEndDate = new DateTime(2025, 7, 5),
            },
            new List<string> { "200" },
            new List<string> { "S1" }
        );

        var row = Assert.Single(result);
        Assert.Equal("200", row.SupplierCode);
        Assert.Equal(100m, row.TotalAmount);
        Assert.Equal(50m, row.CompareTotalAmount);
        Assert.Equal(2, row.CompareOrderCount);
    }

    [Fact]
    public async Task GetSupplierStoreSalesAsync_多供应商同期商品统计缺200时从中国拆分表补200()
    {
        await SeedLocalSupplierAsync("200", "hotbargain");
        await SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await SeedChinaSupplierAsync("CN-COMPARE", "中国同期供应商");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 4), "S1", "200", "P-CN-CURRENT", "中国当前商品", 100m, 10, 4);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 4), "S1", "AUS1", "P-AUS-CURRENT", "澳洲当前商品", 60m, 6, 2);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 5), "S1", "AUS1", "P-AUS-COMPARE", "澳洲同期商品", 30m, 3, 1);
        await SeedChinaSupplierSalesAsync(new DateTime(2025, 7, 5), "S1", "CN-COMPARE", "中国同期供应商", 50m, 5, 2);
        var service = CreateService();

        var result = await service.GetSupplierStoreSalesAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
                CompareStartDate = new DateTime(2025, 7, 5),
                CompareEndDate = new DateTime(2025, 7, 5),
            },
            new List<string> { "200", "AUS1" },
            new List<string> { "S1" }
        );

        var chinaRow = Assert.Single(result, item => item.SupplierCode == "200");
        Assert.Equal(100m, chinaRow.TotalAmount);
        Assert.Equal(50m, chinaRow.CompareTotalAmount);
        Assert.Equal(2, chinaRow.CompareOrderCount);
        Assert.Contains(result, item => item.SupplierCode == "AUS1" && item.CompareTotalAmount == 30m);
    }

    [Fact]
    public async Task GetSupplierStoreSalesAsync_多供应商当前商品统计缺200时从中国拆分表补200()
    {
        await SeedLocalSupplierAsync("200", "hotbargain");
        await SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await SeedChinaSupplierAsync("CN-CURRENT", "中国当前供应商");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 4), "S1", "AUS1", "P-AUS-CURRENT", "澳洲当前商品", 60m, 6, 2);
        await SeedChinaSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "CN-CURRENT", "中国当前供应商", 100m, 10, 4);
        var service = CreateService();

        var result = await service.GetSupplierStoreSalesAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
            },
            new List<string> { "200", "AUS1" },
            new List<string> { "S1" }
        );

        var chinaRow = Assert.Single(result, item => item.SupplierCode == "200");
        Assert.Equal(100m, chinaRow.TotalAmount);
        Assert.Equal(4, chinaRow.OrderCount);
        Assert.Contains(result, item => item.SupplierCode == "AUS1" && item.TotalAmount == 60m);
    }

    [Fact]
    public async Task GetSupplierSalesRankAsync_澳洲报表把中国货汇总到200()
    {
        await SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await SeedLocalSupplierAsync("200", "hotbargain");
        await SeedChinaSupplierAsync("CN1", "中国供应商一");
        await SeedChinaSupplierAsync("CN2", "中国供应商二");
        await SeedSupplierMappingAsync("P-CN-LEGACY", "200", "CN1");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS1", "P-AUS", "澳洲商品", 100m, 10, 2);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "200", "P-CN-LEGACY", "中国旧统计商品", 60m, 6, 3);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S2", "CN2", "P-CN-DIRECT", "中国直接统计商品", 40m, 4, 2);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S1", "200", "P-CN-LEGACY", "中国旧统计商品同期", 30m, 3, 1);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S2", "CN2", "P-CN-DIRECT", "中国直接统计商品同期", 20m, 2, 1);
        var service = CreateService();

        var result = await service.GetSupplierSalesRankAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 1),
            },
            branchCodes: new List<string> { "S1", "S2" },
            topN: 1000
        );

        var chinaRow = Assert.Single(result, row => row.SupplierCode == "200");
        Assert.Equal("hotbargain", chinaRow.SupplierName);
        Assert.Equal(100m, chinaRow.TotalAmount);
        Assert.Equal(10, chinaRow.TotalQuantity);
        Assert.Equal(5, chinaRow.OrderCount);
        Assert.Equal(2, chinaRow.StoreCount);
        Assert.Equal(50m, chinaRow.CompareTotalAmount);
        Assert.Equal(2, chinaRow.CompareOrderCount);
        Assert.DoesNotContain(result, row => row.SupplierCode == "CN2");
        Assert.Contains(result, row => row.SupplierCode == "AUS1" && row.TotalAmount == 100m);
    }

    [Fact]
    public async Task GetSupplierSalesRankAsync_软删除国内供应商的直写行仍汇总到200()
    {
        await SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await SeedLocalSupplierAsync("200", "hotbargain");
        // 国内供应商日后被软删除，它的历史直写销售仍是国内货，不能在澳洲报表里冒出一个叫 CN-SOFT 的供应商。
        await _localDb.Insertable(new ChinaSupplier
        {
            Guid = "china-CN-SOFT",
            SupplierCode = "CN-SOFT",
            SupplierName = "已删除国内供应商",
            IsDeleted = true,
        }).ExecuteCommandAsync();
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 2), "S1", "AUS1", "P-AUS", "澳洲商品", 100m, 10, 2);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 2), "S1", "CN-SOFT", "P-CN-SOFT", "软删除供应商商品", 40m, 4, 2);
        var service = CreateService();

        var result = await service.GetSupplierSalesRankAsync(
            new DateRangeDto { StartDate = new DateTime(2026, 7, 2), EndDate = new DateTime(2026, 7, 2) },
            branchCodes: new List<string> { "S1" },
            topN: 1000
        );

        var chinaRow = Assert.Single(result, row => row.SupplierCode == "200");
        Assert.Equal(40m, chinaRow.TotalAmount);
        Assert.DoesNotContain(result, row => row.SupplierCode == "CN-SOFT");
        Assert.Contains(result, row => row.SupplierCode == "AUS1" && row.TotalAmount == 100m);
    }

    [Fact]
    public async Task GetSupplierStoreSalesAsync_澳洲200分店下钻包含中国货()
    {
        await SeedLocalSupplierAsync("200", "hotbargain");
        await SeedChinaSupplierAsync("CN1", "中国供应商一");
        await SeedChinaSupplierAsync("CN2", "中国供应商二");
        await SeedSupplierMappingAsync("P-CN-LEGACY", "200", "CN1");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "200", "P-CN-LEGACY", "中国旧统计商品", 60m, 6, 3);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S2", "CN2", "P-CN-DIRECT", "中国直接统计商品", 40m, 4, 2);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S1", "200", "P-CN-LEGACY", "中国旧统计商品同期", 30m, 3, 1);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S2", "CN2", "P-CN-DIRECT", "中国直接统计商品同期", 20m, 2, 1);
        var service = CreateService();

        var result = await service.GetSupplierStoreSalesAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 1),
            },
            new List<string> { "200" },
            new List<string> { "S1", "S2" }
        );

        Assert.Equal(2, result.Count);
        var s1 = Assert.Single(result, row => row.BranchCode == "S1");
        Assert.Equal("200", s1.SupplierCode);
        Assert.Equal(60m, s1.TotalAmount);
        Assert.Equal(3, s1.OrderCount);
        Assert.Equal(30m, s1.CompareTotalAmount);
        var s2 = Assert.Single(result, row => row.BranchCode == "S2");
        Assert.Equal("200", s2.SupplierCode);
        Assert.Equal(40m, s2.TotalAmount);
        Assert.Equal(2, s2.OrderCount);
        Assert.Equal(20m, s2.CompareTotalAmount);
    }

    [Fact]
    public async Task GetChinaSupplierSalesRankAsync_全量排行不受大量Posm映射影响()
    {
        await SeedChinaSupplierAsync("CN-BULK", "中国大供应商");
        await SeedSupplierMappingsAsync(
            Enumerable
                .Range(0, 2205)
                .Select(index => ($"P-CN-BULK-{index}", "200", "CN-BULK"))
        );
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "200", "P-CN-BULK-2204", "中国大供应商商品", 10m, 1, 1);
        var service = CreateService();

        var result = await service.GetChinaSupplierSalesRankAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
            },
            branchCodes: new List<string> { "S1" },
            topN: 1000
        );

        var row = Assert.Single(result);
        Assert.Equal("CN-BULK", row.SupplierCode);
        Assert.Equal(10m, row.TotalAmount);
    }

    [Fact]
    public async Task GetChinaSupplierSalesRankAsync_返回客单数客单价和同期字段()
    {
        await SeedChinaSupplierAsync("CN1", "中国供应商");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "CN1", "P-CN-1", "中国商品", 120m, 12, 4);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S1", "CN1", "P-CN-1", "中国商品", 50m, 5, 2);
        var service = CreateService();

        var result = await service.GetChinaSupplierSalesRankAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 1),
            },
            branchCodes: new List<string> { "S1" },
            topN: 1000
        );

        var row = Assert.Single(result);
        Assert.Equal("CN1", row.SupplierCode);
        Assert.Equal(120m, row.TotalAmount);
        Assert.Equal(4, row.OrderCount);
        Assert.Equal(30m, row.AverageTransaction);
        Assert.Equal(50m, row.CompareTotalAmount);
        Assert.Equal(2, row.CompareOrderCount);
        Assert.Equal(25m, row.CompareAverageTransaction);
    }

    [Fact]
    public async Task GetChinaSupplierSalesRankAsync_商品统计为空时使用中国供应商统计表()
    {
        await SeedChinaSupplierAsync("CN-FALLBACK", "中国兜底供应商");
        await SeedChinaSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "CN-FALLBACK", "中国兜底供应商", 210m, 9, 5);
        await SeedChinaSupplierSalesAsync(new DateTime(2025, 7, 5), "S1", "CN-FALLBACK", "中国兜底供应商", 70m, 3, 2);
        var service = CreateService();

        var result = await service.GetChinaSupplierSalesRankAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
                CompareStartDate = new DateTime(2025, 7, 5),
                CompareEndDate = new DateTime(2025, 7, 5),
            },
            branchCodes: new List<string> { "S1" },
            topN: 1000
        );

        var row = Assert.Single(result);
        Assert.Equal("CN-FALLBACK", row.SupplierCode);
        Assert.Equal(210m, row.TotalAmount);
        Assert.Equal(5, row.OrderCount);
        Assert.Equal(1, row.StoreCount);
        Assert.Equal(70m, row.CompareTotalAmount);
        Assert.Equal(2, row.CompareOrderCount);
    }

    [Fact]
    public async Task GetChinaSupplierSalesRankAsync_字典和映射为空时仍读取中国供应商统计表()
    {
        await SeedChinaSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "CN-STAT-ONLY", "统计表供应商", 66m, 6, 2);
        var service = CreateService();

        var result = await service.GetChinaSupplierSalesRankAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
            },
            branchCodes: new List<string> { "S1" },
            topN: 1000
        );

        var row = Assert.Single(result);
        Assert.Equal("CN-STAT-ONLY", row.SupplierCode);
        Assert.Equal(66m, row.TotalAmount);
    }

    [Fact]
    public async Task GetChinaSupplierSalesRankAsync_商品统计存在但映射缺失时不走供应商统计兜底()
    {
        await SeedChinaSupplierAsync("CN-MISSING-MAP", "缺映射供应商");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 4), "S1", "200", "P-MISSING-MAP", "缺映射商品", 99m, 9, 3);
        await SeedChinaSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "CN-MISSING-MAP", "缺映射供应商", 210m, 9, 5);
        var service = CreateService();

        var result = await service.GetChinaSupplierSalesRankAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
            },
            branchCodes: new List<string> { "S1" },
            topN: 1000
        );

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetChinaSupplierBranchTotalsAsync_按分店汇总全部中国供应商且同期不限本期上榜()
    {
        await SeedStoreAsync("S1", "分店一");
        await SeedStoreAsync("S2", "分店二");
        await SeedChinaSupplierAsync("CN1", "直写供应商");
        await SeedChinaSupplierAsync("CN2", "旧200供应商");
        await SeedChinaSupplierAsync("CN-OLD", "去年供应商");
        await SeedSupplierMappingAsync("P-200", "200", "CN2");
        var current = new DateTime(2026, 7, 1);
        var compare = new DateTime(2025, 7, 1);
        // 本期：S1 有直写码与旧 200 两条通道；映射不到的 200 商品不属于中国货。
        await SeedProductStoreDailySalesAsync(current, "S1", "CN1", "P-CN1", "直写商品", 100m, 10, 4, totalCost: 60m, grossProfit: 40m);
        await SeedProductStoreDailySalesAsync(current, "S1", "200", "P-200", "旧200商品", 50m, 5, 2, totalCost: 30m, grossProfit: 20m);
        await SeedProductStoreDailySalesAsync(current, "S1", "200", "P-UNMAPPED", "澳洲200商品", 999m, 9, 3, totalCost: 500m, grossProfit: 499m);
        await SeedProductStoreDailySalesAsync(current, "S2", "CN1", "P-CN1", "直写商品", 30m, 3, 1, totalCost: 18m, grossProfit: 12m);
        // 同期：CN-OLD 本期没有销售，排行不会带出它的同期，但分店中国货同期必须计入。
        await SeedProductStoreDailySalesAsync(compare, "S1", "CN-OLD", "P-OLD", "去年商品", 70m, 7, 2, totalCost: 42m, grossProfit: 28m);
        await SeedProductStoreDailySalesAsync(compare, "S2", "CN1", "P-CN1", "直写商品", 20m, 2, 1);
        var service = CreateService();
        var range = new DateRangeDto
        {
            StartDate = current,
            EndDate = current,
            CompareStartDate = compare,
            CompareEndDate = compare,
        };
        var branches = new List<string> { "S1", "S2" };
        var status = await service.GetProductReportStatisticStatusAsync(range);

        var result = await service.GetChinaSupplierBranchTotalsAsync(range, branches, status);

        Assert.Equal(new[] { "S1", "S2" }, result.Select(row => row.BranchCode));
        var s1 = result[0];
        Assert.Equal("分店一", s1.BranchName);
        Assert.Equal(150m, s1.TotalAmount);
        Assert.Equal(15, s1.TotalQuantity);
        Assert.Equal(2, s1.SupplierCount);
        Assert.Equal(60m, s1.GrossProfit);
        Assert.Equal(0.4m, s1.GrossMarginRate);
        Assert.Equal("Complete", s1.CostStatus);
        Assert.Equal(70m, s1.CompareTotalAmount);
        Assert.Equal(7, s1.CompareTotalQuantity);
        Assert.Equal(28m, s1.CompareGrossProfit);
        var s2 = result[1];
        Assert.Equal(30m, s2.TotalAmount);
        Assert.Equal(20m, s2.CompareTotalAmount);
        Assert.Null(s2.CompareGrossProfit);
        Assert.Equal("Missing", s2.CompareCostStatus);

        // 本期与中国供应商排行同源同口径：分店合计之和等于排行合计。
        var rank = await service.GetChinaSupplierSalesRankAsync(range, branches, 1000);
        Assert.Equal(rank.Sum(row => row.TotalAmount), result.Sum(row => row.TotalAmount));
        Assert.Empty(await service.GetChinaSupplierBranchTotalsAsync(range, new List<string>(), status));
    }

    [Fact]
    public async Task GetChinaSupplierBranchTotalsAsync_只在同期有中国货的分店也返回且本期为零()
    {
        await SeedChinaSupplierAsync("CN1", "直写供应商");
        var current = new DateTime(2026, 7, 1);
        var compare = new DateTime(2025, 7, 1);
        await SeedProductStoreDailySalesAsync(current, "S1", "CN1", "P-CN1", "直写商品", 10m, 1, 1, totalCost: 6m, grossProfit: 4m);
        await SeedProductStoreDailySalesAsync(compare, "S3", "CN1", "P-CN1", "直写商品", 40m, 4, 2, totalCost: 24m, grossProfit: 16m);
        var service = CreateService();
        var range = new DateRangeDto
        {
            StartDate = current,
            EndDate = current,
            CompareStartDate = compare,
            CompareEndDate = compare,
        };
        var status = await service.GetProductReportStatisticStatusAsync(range);

        var result = await service.GetChinaSupplierBranchTotalsAsync(range, new List<string> { "S1", "S3" }, status);

        var s3 = Assert.Single(result, row => row.BranchCode == "S3");
        Assert.Equal("S3", s3.BranchName);
        Assert.Equal(0m, s3.TotalAmount);
        Assert.Equal("NoActivity", s3.CostStatus);
        Assert.Equal(40m, s3.CompareTotalAmount);
        Assert.Equal("Complete", s3.CompareCostStatus);
    }

    [Fact]
    public async Task 移动端中国页签预热后三块默认视图均命中缓存且自动清理不清版本化条目()
    {
        // 预热按悉尼业务日取今天/昨天，同期按 ISO 周同星期几；四个日期的三类统计都要处于同一 Fresh 版本。
        var today = SalesStatisticsBusinessDate.GetBusinessDate(DateTimeOffset.UtcNow);
        var completedAt = DateTime.UtcNow;
        var dates = new[] { today, today.AddDays(-1) }
            .SelectMany(date => new[] { date, MobileProductReportRange.LastYearSameIsoWeekday(date) })
            .Distinct()
            .ToList();
        var types = new[]
        {
            SalesStatisticType.ProductStoreDaily,
            SalesStatisticType.AustralianSupplierStoreSales,
            SalesStatisticType.ChinaSupplierStoreSales,
        };
        await _localDb.Insertable(dates.SelectMany(date => types.Select(type => new SalesStatisticRefreshState
        {
            StatisticType = type,
            Date = date,
            Status = SalesStatisticRefreshStatus.Fresh,
            LastAggregatedAtUtc = completedAt,
            CompletedAtUtc = completedAt,
            SourceProductVersion = "warmup-version",
        })).ToList()).ExecuteCommandAsync();
        await SeedStoreAsync("S1", "分店一");
        await SeedChinaSupplierAsync("CN1", "直写供应商");
        await SeedProductStoreDailySalesAsync(today, "S1", "CN1", "P-CN1", "直写商品", 100m, 10, 3, totalCost: 60m, grossProfit: 40m);
        await _localDb.Insertable(new ChinaSupplierStoreSalesDetail
        {
            Date = today, BranchCode = "S1", SupplierCode = "CN1", SupplierName = "直写供应商",
            TotalAmount = 100m, TotalQuantity = 10, OrderCount = 3, GrossProfit = 40m, TotalCost = 60m,
            StatisticRowCount = 1, CostedRowCount = 1, GrossProfitRowCount = 1, UpdateTime = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        var rollupReads = 0;
        var productReads = 0;
        _localDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("ChinaSupplierStoreSalesDetail", StringComparison.OrdinalIgnoreCase)) rollupReads++;
            if (sql.Contains("ProductStoreDailySalesStatistic", StringComparison.OrdinalIgnoreCase)) productReads++;
        };
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(useSupplierRollups: true, cache: cache);
        var storeOptions = new Mock<IProductMovementReportService>();
        storeOptions.Setup(x => x.GetStoreOptionsAsync(It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(new List<ProductMovementReportStoreOptionDto> { new() { Label = "分店一", Value = "S1" } });
        var warmer = new SalesDashboardCacheWarmer(service, NullLogger<SalesDashboardCacheWarmer>.Instance, cache, storeOptions.Object);

        await warmer.WarmUpMobileChinaTabAsync();
        Assert.True(rollupReads > 0, "预热必须真正读取供应商汇总表");
        Assert.True(productReads > 0, "预热必须真正读取商品日统计");
        var warmedRollupReads = rollupReads;
        var warmedProductReads = productReads;

        // 与移动端控制器完全相同的参数：显式分店列表、排行 topN=1000、商品页 1/20、中国范围、数量降序。
        var range = MobileProductReportRange.Day(today);
        var branches = new List<string> { "S1" };
        var status = await service.GetProductReportStatisticStatusAsync(range);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, status.StatisticStatus);
        var rank = await service.GetChinaSupplierSalesRankAsync(range, branches, 1000, null, status);
        var totals = await service.GetChinaSupplierBranchTotalsAsync(range, branches, status);
        var page = await service.GetEnhancedSalesProductDetailsAsync(range, branches, null, null, 1, 20, null, status, true, "quantity", "desc");
        Assert.Equal("CN1", Assert.Single(rank).SupplierCode);
        Assert.Equal(100m, Assert.Single(totals).TotalAmount);
        Assert.Equal("P-CN1", Assert.Single(page.Data).ProductCode);
        Assert.Equal(warmedRollupReads, rollupReads);
        Assert.Equal(warmedProductReads, productReads);

        // 统计刷新后的自动清理不能把按版本缓存的排行与分店合计清掉。
        await warmer.ClearCacheAsync();
        await service.GetChinaSupplierSalesRankAsync(range, branches, 1000, null, status);
        await service.GetChinaSupplierBranchTotalsAsync(range, branches, status);
        Assert.Equal(warmedRollupReads, rollupReads);
    }

    [Fact]
    public async Task GetSupplierStoreSalesAsync_返回分店客单数客单价和同期字段()
    {
        await SeedStoreAsync("S1", "分店一");
        await SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS1", "P-AUS-3", "分店澳洲商品", 100m, 10, 2);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S1", "AUS1", "P-AUS-3", "分店澳洲商品", 80m, 8, 4);
        var service = CreateService();

        var result = await service.GetSupplierStoreSalesAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 1),
            },
            new List<string> { "AUS1" },
            new List<string> { "S1" }
        );

        var row = Assert.Single(result);
        Assert.Equal("分店一", row.BranchName);
        Assert.Equal(2, row.OrderCount);
        Assert.Equal(50m, row.AverageTransaction);
        Assert.Equal(80m, row.CompareTotalAmount);
        Assert.Equal(4, row.CompareOrderCount);
        Assert.Equal(20m, row.CompareAverageTransaction);
    }

    [Fact]
    public async Task GetSupplierStoreSalesAsync_商品统计为空时使用澳洲供应商统计表()
    {
        await SeedStoreAsync("S1", "分店一");
        await SeedLocalSupplierAsync("AUS-FALLBACK", "澳洲兜底供应商");
        await SeedAustralianSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "AUS-FALLBACK", "澳洲兜底供应商", 123m, 7, 3);
        await SeedAustralianSupplierSalesAsync(new DateTime(2025, 7, 5), "S1", "AUS-FALLBACK", "澳洲兜底供应商", 80m, 4, 2);
        var service = CreateService();

        var result = await service.GetSupplierStoreSalesAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
                CompareStartDate = new DateTime(2025, 7, 5),
                CompareEndDate = new DateTime(2025, 7, 5),
            },
            new List<string> { "AUS-FALLBACK" },
            new List<string> { "S1" }
        );

        var row = Assert.Single(result);
        Assert.Equal("分店一", row.BranchName);
        Assert.Equal(123m, row.TotalAmount);
        Assert.Equal(3, row.OrderCount);
        Assert.Equal(80m, row.CompareTotalAmount);
        Assert.Equal(2, row.CompareOrderCount);
    }

    [Fact]
    public async Task GetChinaSupplierStoreSalesAsync_返回分店客单数客单价同期字段且缓存不串澳洲()
    {
        await SeedStoreAsync("S1", "分店一");
        await SeedLocalSupplierAsync("AUS1", "澳洲供应商");
        await SeedChinaSupplierAsync("CN1", "中国供应商");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS1", "P-AUS-SUP1", "澳洲供应商商品", 999m, 9, 9);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "CN1", "P-CN-SUP1", "中国供应商商品", 120m, 12, 4);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S1", "CN1", "P-CN-SUP1", "中国供应商商品", 50m, 5, 2);
        var service = CreateService();
        var dateRange = new DateRangeDto
        {
            StartDate = new DateTime(2026, 7, 1),
            EndDate = new DateTime(2026, 7, 1),
            CompareStartDate = new DateTime(2025, 7, 1),
            CompareEndDate = new DateTime(2025, 7, 1),
        };

        await service.GetSupplierStoreSalesAsync(dateRange, new List<string> { "AUS1" }, new List<string> { "S1" });
        var result = await service.GetChinaSupplierStoreSalesAsync(
            dateRange,
            new List<string> { "CN1" },
            new List<string> { "S1" }
        );

        var row = Assert.Single(result);
        Assert.Equal("分店一", row.BranchName);
        Assert.Equal(120m, row.TotalAmount);
        Assert.Equal(4, row.OrderCount);
        Assert.Equal(30m, row.AverageTransaction);
        Assert.Equal(50m, row.CompareTotalAmount);
        Assert.Equal(2, row.CompareOrderCount);
        Assert.Equal(25m, row.CompareAverageTransaction);
    }

    [Fact]
    public async Task GetChinaSupplierStoreSalesAsync_兼容旧统计表200供应商映射()
    {
        await SeedStoreAsync("S1", "分店一");
        await SeedChinaSupplierAsync("CN-LEGACY", "中国旧统计供应商");
        await SeedSupplierMappingAsync("P-CN-LEGACY", "200", "CN-LEGACY");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "200", "P-CN-LEGACY", "旧统计中国商品", 88m, 8, 2);
        var service = CreateService();

        var result = await service.GetChinaSupplierStoreSalesAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
            },
            new List<string> { "CN-LEGACY" },
            new List<string> { "S1" }
        );

        var row = Assert.Single(result);
        Assert.Equal("CN-LEGACY", row.SupplierCode);
        Assert.Equal(88m, row.TotalAmount);
    }

    [Fact]
    public async Task GetChinaSupplierStoreSalesAsync_商品统计为空时使用中国供应商统计表()
    {
        await SeedStoreAsync("S1", "分店一");
        await SeedChinaSupplierAsync("CN-FALLBACK", "中国兜底供应商");
        await SeedChinaSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "CN-FALLBACK", "中国兜底供应商", 210m, 9, 5);
        await SeedChinaSupplierSalesAsync(new DateTime(2025, 7, 5), "S1", "CN-FALLBACK", "中国兜底供应商", 70m, 3, 2);
        var service = CreateService();

        var result = await service.GetChinaSupplierStoreSalesAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
                CompareStartDate = new DateTime(2025, 7, 5),
                CompareEndDate = new DateTime(2025, 7, 5),
            },
            new List<string> { "CN-FALLBACK" },
            new List<string> { "S1" }
        );

        var row = Assert.Single(result);
        Assert.Equal("分店一", row.BranchName);
        Assert.Equal(210m, row.TotalAmount);
        Assert.Equal(5, row.OrderCount);
        Assert.Equal(70m, row.CompareTotalAmount);
        Assert.Equal(2, row.CompareOrderCount);
    }

    [Fact]
    public async Task GetChinaSupplierStoreSalesAsync_商品统计存在但映射缺失时不走供应商统计兜底()
    {
        await SeedStoreAsync("S1", "分店一");
        await SeedChinaSupplierAsync("CN-MISSING-MAP", "缺映射供应商");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 4), "S1", "200", "P-MISSING-MAP", "缺映射商品", 99m, 9, 3);
        await SeedChinaSupplierSalesAsync(new DateTime(2026, 7, 4), "S1", "CN-MISSING-MAP", "缺映射供应商", 210m, 9, 5);
        var service = CreateService();

        var result = await service.GetChinaSupplierStoreSalesAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 4),
                EndDate = new DateTime(2026, 7, 4),
            },
            new List<string> { "CN-MISSING-MAP" },
            new List<string> { "S1" }
        );

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetProductSalesByAllBranchesAsync_按分店范围汇总金额均价且缓存不串数据()
    {
        await SeedStoreAsync("S1", "分店一");
        await SeedStoreAsync("S2", "分店二");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS1", "P1", "商品一", 40m, 2, 1);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S2", "AUS1", "P1", "商品一", 90m, 3, 2);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S1", "AUS1", "P1", "商品一", 30m, 1, 1);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S2", "AUS1", "P1", "商品一", 60m, 2, 1);
        var service = CreateService();
        var dateRange = new DateRangeDto
        {
            StartDate = new DateTime(2026, 7, 1),
            EndDate = new DateTime(2026, 7, 1),
            CompareStartDate = new DateTime(2025, 7, 1),
            CompareEndDate = new DateTime(2025, 7, 1),
        };

        var first = await service.GetProductSalesByAllBranchesAsync(dateRange, "P1", new List<string> { "S1" });
        var second = await service.GetProductSalesByAllBranchesAsync(dateRange, "P1", new List<string> { "S2" });

        var firstRow = Assert.Single(first);
        Assert.Equal("S1", firstRow.BranchCode);
        Assert.Equal(2, firstRow.Quantity);
        Assert.Equal(40m, firstRow.SalesAmount);
        Assert.Equal(30m, firstRow.CompareSalesAmount);
        Assert.Equal(20m, firstRow.AverageUnitPrice);

        var secondRow = Assert.Single(second);
        Assert.Equal("S2", secondRow.BranchCode);
        Assert.Equal(3, secondRow.Quantity);
        Assert.Equal(90m, secondRow.SalesAmount);
        Assert.Equal(60m, secondRow.CompareSalesAmount);
        Assert.Equal(30m, secondRow.AverageUnitPrice);
        Assert.Equal(0, secondRow.DiscountedQuantity);
    }

    [Fact]
    public async Task GetProductSalesByAllBranchesAsync_返回同期独有分店()
    {
        await SeedStoreAsync("S1", "分店一");
        await SeedStoreAsync("S2", "分店二");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS1", "P1", "商品一", 40m, 2, 1);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S2", "AUS1", "P1", "商品一", 60m, 3, 1);
        var service = CreateService();

        var result = await service.GetProductSalesByAllBranchesAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 1),
            },
            "P1"
        );

        Assert.Equal(2, result.Count);
        var compareOnlyRow = Assert.Single(result, row => row.BranchCode == "S2");
        Assert.Equal(0, compareOnlyRow.Quantity);
        Assert.Equal(0m, compareOnlyRow.SalesAmount);
        Assert.Equal(60m, compareOnlyRow.CompareSalesAmount);
        Assert.Equal(3, GetIntProperty(compareOnlyRow, "CompareQuantity"));
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetailsAsync_按货号过滤商品明细()
    {
        await SeedProductAsync("P1", "HB001", "BAR001");
        await SeedProductAsync("P2", "HB002", "BAR002");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS1", "P1", "商品一", 40m, 2, 1, barcode: "BAR001");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS1", "P2", "商品二", 100m, 5, 1, barcode: "BAR002");
        var service = CreateService();

        var result = await service.GetEnhancedSalesProductDetailsAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
            },
            productSearch: "HB001"
        );

        var row = Assert.Single(result.Data);
        Assert.Equal(1, result.Total);
        Assert.Equal("P1", row.ProductCode);
        Assert.Equal("HB001", row.ItemNumber);
        Assert.Equal(2, row.Quantity);
        Assert.Equal(40m, row.SalesAmount);
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetailsAsync_按Posm明细条码过滤商品明细()
    {
        await SeedProductAsync("P3", "HB003", "MASTER-BAR");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS1", "P3", "扫码商品", 90m, 3, 1, barcode: "SCAN-ONLY-123");
        var service = CreateService();

        var result = await service.GetEnhancedSalesProductDetailsAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
            },
            productSearch: "SCAN-ONLY"
        );

        var row = Assert.Single(result.Data);
        Assert.Equal("P3", row.ProductCode);
        Assert.Equal("HB003", row.ItemNumber);
        Assert.Equal(90m, row.SalesAmount);
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetailsAsync_供应商筛选下搜索同时支持澳洲和中国()
    {
        await SeedProductAsync("P4", "HB004", "BAR004");
        await SeedProductAsync("P5", "HB005", "BAR005");
        await SeedSupplierMappingAsync("P4", "200", "CN1");
        await SeedSupplierMappingAsync("P5", "200", "CN2");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS1", "P4", "供应商商品", 80m, 4, 1, barcode: "BAR004");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS2", "P5", "其它商品", 120m, 6, 1, barcode: "BAR005");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "200", "P4", "中国供应商商品", 80m, 4, 1, barcode: "BAR004");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "200", "P5", "中国其它商品", 120m, 6, 1, barcode: "BAR005");
        var service = CreateService();
        var dateRange = new DateRangeDto
        {
            StartDate = new DateTime(2026, 7, 1),
            EndDate = new DateTime(2026, 7, 1),
        };

        var australia = await service.GetEnhancedSalesProductDetailsAsync(
            dateRange,
            localSupplierCodes: new List<string> { "AUS1" },
            productSearch: "HB004"
        );
        var china = await service.GetEnhancedSalesProductDetailsAsync(
            dateRange,
            chinaSupplierCodes: new List<string> { "CN1" },
            productSearch: "HB004"
        );

        Assert.Equal("P4", Assert.Single(australia.Data).ProductCode);
        Assert.Equal("P4", Assert.Single(china.Data).ProductCode);
        Assert.Equal(1, australia.Total);
        Assert.Equal(1, china.Total);
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetailsAsync_澳洲200筛选包含中国货商品()
    {
        await SeedChinaSupplierAsync("CN1", "中国供应商一");
        await SeedChinaSupplierAsync("CN2", "中国供应商二");
        await SeedProductAsync("P-CN-LEGACY", "HB-CN-LEGACY", "BAR-CN-LEGACY");
        await SeedProductAsync("P-CN-DIRECT", "HB-CN-DIRECT", "BAR-CN-DIRECT");
        await SeedSupplierMappingAsync("P-CN-LEGACY", "200", "CN1");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "200", "P-CN-LEGACY", "中国旧统计商品", 60m, 6, 3, barcode: "BAR-CN-LEGACY");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S2", "CN2", "P-CN-DIRECT", "中国直接统计商品", 40m, 4, 2, barcode: "BAR-CN-DIRECT");
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S2", "CN2", "P-CN-DIRECT", "中国直接统计商品同期", 20m, 2, 1, barcode: "BAR-CN-DIRECT");
        var service = CreateService();

        var result = await service.GetEnhancedSalesProductDetailsAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 1),
            },
            branchCodes: new List<string> { "S1", "S2" },
            localSupplierCodes: new List<string> { "200" },
            pageSize: 20
        );

        Assert.Equal(2, result.Total);
        var legacyRow = Assert.Single(result.Data, row => row.ProductCode == "P-CN-LEGACY");
        Assert.Equal(60m, legacyRow.SalesAmount);
        var directRow = Assert.Single(result.Data, row => row.ProductCode == "P-CN-DIRECT");
        Assert.Equal(40m, directRow.SalesAmount);
        Assert.Equal(20m, directRow.SalesAmountLY);
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetailsAsync_中国范围空供应商代码只返回全部中国货()
    {
        await SeedChinaSupplierAsync("CN1", "中国供应商一");
        await SeedChinaSupplierAsync("CN2", "中国供应商二");
        await SeedSupplierMappingAsync("P-CN-LEGACY", "200", "CN1");
        await SeedProductStoreDailySalesAsync(
            new DateTime(2026, 7, 1),
            "S1",
            "200",
            "P-CN-LEGACY",
            "中国旧统计商品",
            60m,
            6,
            3
        );
        await SeedProductStoreDailySalesAsync(
            new DateTime(2026, 7, 1),
            "S1",
            "CN2",
            "P-CN-DIRECT",
            "中国直接统计商品",
            40m,
            4,
            2
        );
        await SeedProductStoreDailySalesAsync(
            new DateTime(2026, 7, 1),
            "S1",
            "AUS1",
            "P-AUS",
            "纯澳洲商品",
            999m,
            9,
            9
        );
        var service = CreateService();

        var result = await service.GetEnhancedSalesProductDetailsAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
            },
            branchCodes: new List<string> { "S1" },
            pageSize: 20,
            chinaSupplierScope: true
        );

        Assert.Equal(2, result.Total);
        Assert.Contains(result.Data, row => row.ProductCode == "P-CN-LEGACY");
        Assert.Contains(result.Data, row => row.ProductCode == "P-CN-DIRECT");
        Assert.DoesNotContain(result.Data, row => row.ProductCode == "P-AUS");

        var narrowedResult = await service.GetEnhancedSalesProductDetailsAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
            },
            branchCodes: new List<string> { "S1" },
            chinaSupplierCodes: new List<string> { "CN1" },
            pageSize: 20,
            chinaSupplierScope: true
        );

        Assert.Equal("P-CN-LEGACY", Assert.Single(narrowedResult.Data).ProductCode);
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetailsAsync_中国范围无供应商目录时不退化为全部商品()
    {
        await SeedProductStoreDailySalesAsync(
            new DateTime(2026, 7, 1),
            "S1",
            "AUS1",
            "P-AUS",
            "纯澳洲商品",
            999m,
            9,
            9
        );
        var service = CreateService();

        var result = await service.GetEnhancedSalesProductDetailsAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
            },
            branchCodes: new List<string> { "S1" },
            pageSize: 20,
            chinaSupplierScope: true
        );

        Assert.Empty(result.Data);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetailsAsync_澳洲200筛选搜索中国货商品()
    {
        await SeedChinaSupplierAsync("CN1", "中国供应商一");
        await SeedChinaSupplierAsync("CN2", "中国供应商二");
        await SeedProductAsync("P-CN-LEGACY", "HB-CN-LEGACY", "BAR-CN-LEGACY");
        await SeedProductAsync("P-CN-DIRECT", "HB-CN-DIRECT", "BAR-CN-DIRECT");
        await SeedSupplierMappingAsync("P-CN-LEGACY", "200", "CN1");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "200", "P-CN-LEGACY", "中国旧统计商品", 60m, 6, 3, barcode: "BAR-CN-LEGACY");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S2", "CN2", "P-CN-DIRECT", "中国直接统计商品", 40m, 4, 2, barcode: "BAR-CN-DIRECT");
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S2", "CN2", "P-CN-DIRECT", "中国直接统计商品同期", 20m, 2, 1, barcode: "BAR-CN-DIRECT");
        var service = CreateService();

        var result = await service.GetEnhancedSalesProductDetailsAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 1),
            },
            branchCodes: new List<string> { "S1", "S2" },
            localSupplierCodes: new List<string> { "200" },
            pageSize: 20,
            productSearch: "HB-CN-DIRECT"
        );

        var row = Assert.Single(result.Data);
        Assert.Equal(1, result.Total);
        Assert.Equal("P-CN-DIRECT", row.ProductCode);
        Assert.Equal(40m, row.SalesAmount);
        Assert.Equal(20m, row.SalesAmountLY);
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetailsAsync_指定中国供应商大量映射不超参数且不混入其它供应商()
    {
        await SeedChinaSupplierAsync("CN-BIG", "中国大供应商");
        await SeedChinaSupplierAsync("CN-OTHER", "其它中国供应商");
        await SeedSupplierMappingsAsync(
            Enumerable
                .Range(0, 2205)
                .Select(index => ($"P-CN-BIG-{index}", "200", "CN-BIG"))
        );
        await SeedSupplierMappingAsync("P-CN-OTHER", "200", "CN-OTHER");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "200", "P-CN-BIG-2204", "大供应商旧统计", 10m, 1, 1);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S1", "200", "P-CN-BIG-2204", "大供应商旧统计同期", 5m, 1, 1);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "CN-BIG", "P-CN-BIG-DIRECT", "大供应商新统计", 20m, 2, 1);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "200", "P-CN-OTHER", "其它供应商商品", 999m, 9, 9);
        var service = CreateService();

        var result = await service.GetEnhancedSalesProductDetailsAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 1),
            },
            branchCodes: new List<string> { "S1" },
            chinaSupplierCodes: new List<string> { "CN-BIG" },
            pageSize: 20
        );

        Assert.Equal(2, result.Total);
        Assert.DoesNotContain(result.Data, row => row.ProductCode == "P-CN-OTHER");
        var legacyRow = Assert.Single(result.Data, row => row.ProductCode == "P-CN-BIG-2204");
        Assert.Equal(10m, legacyRow.SalesAmount);
        Assert.Equal(5m, legacyRow.SalesAmountLY);
        var directRow = Assert.Single(result.Data, row => row.ProductCode == "P-CN-BIG-DIRECT");
        Assert.Equal(20m, directRow.SalesAmount);
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetailsAsync_大量映射加宽泛搜索不超参数且不混入其它供应商()
    {
        await SeedChinaSupplierAsync("CN-BIG", "中国大供应商");
        await SeedChinaSupplierAsync("CN-OTHER", "其它中国供应商");
        await SeedProductsAsync(
            Enumerable
                .Range(0, 2205)
                .Select(index => ($"P-CN-BIG-{index}", $"HB-CN-BIG-{index}", $"BAR-CN-BIG-{index}"))
        );
        await SeedProductAsync("P-CN-BIG-DIRECT", "HB-CN-BIG-DIRECT", "BAR-CN-BIG-DIRECT");
        await SeedProductAsync("P-CN-OTHER", "HB-CN-BIG-OTHER", "BAR-CN-BIG-OTHER");
        await SeedSupplierMappingsAsync(
            Enumerable
                .Range(0, 2205)
                .Select(index => ($"P-CN-BIG-{index}", "200", "CN-BIG"))
        );
        await SeedSupplierMappingAsync("P-CN-OTHER", "200", "CN-OTHER");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "200", "P-CN-BIG-2204", "大供应商旧统计", 10m, 1, 1, barcode: "BAR-CN-BIG-2204");
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S1", "200", "P-CN-BIG-2204", "大供应商旧统计同期", 5m, 1, 1, barcode: "BAR-CN-BIG-2204");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "CN-BIG", "P-CN-BIG-DIRECT", "大供应商新统计", 20m, 2, 1, barcode: "BAR-CN-BIG-DIRECT");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "200", "P-CN-OTHER", "其它供应商商品", 999m, 9, 9, barcode: "BAR-CN-BIG-OTHER");
        var service = CreateService();

        var result = await service.GetEnhancedSalesProductDetailsAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 1),
            },
            branchCodes: new List<string> { "S1" },
            chinaSupplierCodes: new List<string> { "CN-BIG" },
            pageSize: 20,
            productSearch: "HB-CN-BIG"
        );

        Assert.Equal(2, result.Total);
        Assert.DoesNotContain(result.Data, row => row.ProductCode == "P-CN-OTHER");
        Assert.Contains(result.Data, row =>
            row.ProductCode == "P-CN-BIG-2204"
            && row.SalesAmount == 10m
            && row.SalesAmountLY == 5m
        );
        Assert.Contains(result.Data, row =>
            row.ProductCode == "P-CN-BIG-DIRECT"
            && row.SalesAmount == 20m
        );
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetailsAsync_搜索同时过滤对比期商品()
    {
        await SeedProductAsync("P6", "HB006", "BAR006");
        await SeedProductAsync("P7", "HB007", "BAR007");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS1", "P6", "匹配商品", 40m, 2, 1, barcode: "BAR006");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS1", "P7", "当前期不匹配", 100m, 5, 1, barcode: "BAR007");
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S1", "AUS1", "P6", "对比期匹配", 30m, 1, 1, barcode: "BAR006");
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S1", "AUS1", "P7", "对比期不匹配", 140m, 7, 1, barcode: "BAR007");
        var service = CreateService();

        var result = await service.GetEnhancedSalesProductDetailsAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 1),
            },
            productSearch: "HB006"
        );

        var row = Assert.Single(result.Data);
        Assert.Equal(1, result.Total);
        Assert.Equal("P6", row.ProductCode);
        Assert.Equal(2, row.Quantity);
        Assert.Equal(40m, row.SalesAmount);
        Assert.Equal(1, row.QuantityLY);
        Assert.Equal(30m, row.SalesAmountLY);
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetailsAsync_返回当前期和同期商品并集()
    {
        await SeedProductAsync("P8", "HB008", "BAR008");
        await SeedProductAsync("P9", "HB009", "BAR009");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "AUS1", "P8", "当前商品", 80m, 4, 1, barcode: "BAR008");
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S1", "AUS1", "P9", "同期商品", 50m, 2, 1, barcode: "BAR009");
        var service = CreateService();

        var result = await service.GetEnhancedSalesProductDetailsAsync(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
                CompareStartDate = new DateTime(2025, 7, 1),
                CompareEndDate = new DateTime(2025, 7, 1),
            }
        );

        Assert.Equal(2, result.Total);
        var currentRow = Assert.Single(result.Data, row => row.ProductCode == "P8");
        Assert.Equal(4, currentRow.Quantity);
        Assert.Equal(0, currentRow.QuantityLY);
        var compareOnlyRow = Assert.Single(result.Data, row => row.ProductCode == "P9");
        Assert.Equal("HB009", compareOnlyRow.ItemNumber);
        Assert.Equal(0, compareOnlyRow.Quantity);
        Assert.Equal(50m, compareOnlyRow.SalesAmountLY);
        Assert.Equal(2, compareOnlyRow.QuantityLY);
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetailsAsync_默认二十八分店澳洲筛选在数据库完成并集稳定分页()
    {
        var branchCodes = Enumerable.Range(1, 28).Select(index => $"S{index:00}").ToList();
        foreach (var productCode in new[] { "P-FAST-A", "P-FAST-B", "P-FAST-C", "P-FAST-D", "P-FAST-ONLY-COMPARE", "P-FAST-OUTSIDE" })
        {
            await SeedProductAsync(productCode, $"HB-{productCode}", $"BAR-{productCode}");
        }

        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S01", "AUS-FAST", "P-FAST-A", "当前毛利完整", 300m, 3, 1, totalCost: 180m, grossProfit: 120m);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S02", "AUS-FAST", "P-FAST-B", "当前成本缺失", 200m, 2, 1);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S03", "AUS-FAST", "P-FAST-C", "稳定排序一", 100m, 1, 1, totalCost: 60m, grossProfit: 40m);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S04", "AUS-FAST", "P-FAST-D", "稳定排序二", 100m, 1, 1, totalCost: 60m, grossProfit: 40m);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S05", "AUS-FAST", "P-FAST-B", "同期商品", 180m, 2, 1, totalCost: 100m, grossProfit: 80m);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S06", "AUS-FAST", "P-FAST-C", "同期稳定排序一", 100m, 1, 1, totalCost: 60m, grossProfit: 40m);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S07", "AUS-FAST", "P-FAST-D", "同期稳定排序二", 100m, 1, 1, totalCost: 60m, grossProfit: 40m);
        await SeedProductStoreDailySalesAsync(new DateTime(2025, 7, 1), "S08", "AUS-FAST", "P-FAST-ONLY-COMPARE", "同期独有且成本缺失", 250m, 2, 1);
        foreach (var branchCode in branchCodes.Skip(8))
        {
            // 真实填满 28 家分店，防止只传 28 个筛选值、实际仅测少量数据的伪覆盖。
            await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), branchCode, "AUS-FAST", "P-FAST-A", "二十八店规模数据", 1m, 1, 1, totalCost: 0.5m, grossProfit: 0.5m);
        }
        // 高额越权分店必须被 branchCodes 排除，否则总数与首行都会被污染。
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S99", "AUS-FAST", "P-FAST-OUTSIDE", "范围外商品", 999999m, 1, 1, totalCost: 1m, grossProfit: 999998m);

        var executedSql = new List<string>();
        _localDb.Aop.OnLogExecuting = (sql, _) => executedSql.Add(sql);
        var service = CreateService();
        var dateRange = new DateRangeDto
        {
            StartDate = new DateTime(2026, 7, 1),
            EndDate = new DateTime(2026, 7, 1),
            CompareStartDate = new DateTime(2025, 7, 1),
            CompareEndDate = new DateTime(2025, 7, 1),
        };

        var coldQueryStopwatch = Stopwatch.StartNew();
        var firstPage = await service.GetEnhancedSalesProductDetailsAsync(
            dateRange,
            branchCodes: branchCodes,
            localSupplierCodes: new List<string> { "AUS-FAST" },
            pageIndex: 1,
            pageSize: 2,
            productSearch: "HB-P-FAST"
        );
        coldQueryStopwatch.Stop();
        var secondPage = await service.GetEnhancedSalesProductDetailsAsync(
            dateRange,
            branchCodes: branchCodes,
            localSupplierCodes: new List<string> { "AUS-FAST" },
            pageIndex: 2,
            pageSize: 2,
            productSearch: "HB-P-FAST"
        );
        var thirdPage = await service.GetEnhancedSalesProductDetailsAsync(
            dateRange,
            branchCodes: branchCodes,
            localSupplierCodes: new List<string> { "AUS-FAST" },
            pageIndex: 3,
            pageSize: 2,
            productSearch: "HB-P-FAST"
        );

        Assert.Equal(5, firstPage.Total);
        Assert.Equal(new[] { "P-FAST-A", "P-FAST-B" }, firstPage.Data.Select(row => row.ProductCode));
        Assert.Equal(new[] { "P-FAST-C", "P-FAST-D" }, secondPage.Data.Select(row => row.ProductCode));
        Assert.Equal("P-FAST-ONLY-COMPARE", Assert.Single(thirdPage.Data).ProductCode);
        var pagedCodes = firstPage.Data
            .Concat(secondPage.Data)
            .Concat(thirdPage.Data)
            .Select(row => row.ProductCode)
            .ToList();
        Assert.Equal(5, pagedCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(80m, firstPage.Data[1].GrossProfitLY);
        Assert.Null(firstPage.Data[1].GrossProfit);
        Assert.Null(thirdPage.Data[0].GrossProfitLY);
        Assert.Contains(executedSql, sql => sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            coldQueryStopwatch.ElapsedMilliseconds < 2_000,
            $"28 家分店商品冷查询耗时 {coldQueryStopwatch.ElapsedMilliseconds}ms，超过 2000ms 回归门槛"
        );
    }

    // 期望顺序按“本期值 → 同期值 → 商品编码升序”手工推导，字母对应 P-SORT-{字母}；
    // 未知字段（含 itemNumber）和未知方向回退为金额降序。
    [Theory]
    [InlineData(null, null, "ABCFDEHIG")]
    [InlineData("amount", "desc", "ABCFDEHIG")]
    [InlineData("amount", "asc", "GHIEDFCBA")]
    [InlineData("quantity", "desc", "BACFDEHIG")]
    [InlineData("quantity", "asc", "GHIEDFCAB")]
    [InlineData("unitPrice", "desc", "ACFDEHIBG")]
    [InlineData("unitPrice", "asc", "GBHIEDFCA")]
    [InlineData("itemNumber", "sideways", "ABCFDEHIG")]
    public async Task GetEnhancedSalesProductDetailsAsync_Sort_快速路径跨页顺序稳定且无重复(
        string? sortField,
        string? sortOrder,
        string expectedOrder
    )
    {
        await SeedProductSortFixtureAsync("AUS-SORT");
        var executedSql = new List<string>();
        _localDb.Aop.OnLogExecuting = (sql, _) => executedSql.Add(sql);
        var service = CreateService();

        var pages = new List<PagedSalesProductDetailWithDiscountDto>();
        for (var pageIndex = 1; pageIndex <= 3; pageIndex++)
        {
            pages.Add(await service.GetEnhancedSalesProductDetailsAsync(
                CreateProductSortDateRange(),
                localSupplierCodes: new List<string> { "AUS-SORT" },
                pageIndex: pageIndex,
                pageSize: 4,
                sortField: sortField,
                sortOrder: sortOrder
            ));
        }

        AssertProductSortPages(pages, expectedOrder);
        // 快速路径在数据库内分页；均价排序必须使用固定的实数除法片段，而不是按 TEXT 或整数除法比较。
        Assert.Contains(executedSql, sql => sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
        var sort = ProductReportSort.Parse(sortField, sortOrder);
        if (sort.Field == ProductReportSortField.UnitPrice)
        {
            Assert.Contains(
                executedSql,
                sql => sql.Contains(
                    SalesDashboardReactService.BuildFastPathUnitPriceOrderBy(sort.Ascending),
                    StringComparison.Ordinal
                )
            );
        }
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetailsAsync_Sort_快速路径缓存按排序隔离()
    {
        await SeedProductSortFixtureAsync("AUS-SORT");
        var pagingQueryCount = 0;
        _localDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
                pagingQueryCount++;
        };
        var service = CreateService();

        async Task<string> FirstPageAsync(string? sortField, string? sortOrder)
        {
            var page = await service.GetEnhancedSalesProductDetailsAsync(
                CreateProductSortDateRange(),
                localSupplierCodes: new List<string> { "AUS-SORT" },
                pageIndex: 1,
                pageSize: 4,
                sortField: sortField,
                sortOrder: sortOrder
            );
            Assert.Equal(9, page.Total);
            return ToProductSortLetters(page.Data);
        }

        // 同一服务实例、同一页码：不同排序若共用缓存键，第二、三次会直接拿到默认排序的缓存页。
        Assert.Equal("ABCF", await FirstPageAsync(null, null));
        Assert.Equal("ACFD", await FirstPageAsync("unitPrice", "desc"));
        Assert.Equal("GHIE", await FirstPageAsync("quantity", "asc"));
        Assert.Equal(3, pagingQueryCount);
        // 再次请求默认排序命中自己的缓存，不被其它排序覆盖，也不再查询数据库分页。
        Assert.Equal("ABCF", await FirstPageAsync(null, null));
        Assert.Equal(3, pagingQueryCount);
    }

    [Theory]
    [InlineData(null, null, "ABCFDEHIG")]
    [InlineData("amount", "desc", "ABCFDEHIG")]
    [InlineData("amount", "asc", "GHIEDFCBA")]
    [InlineData("quantity", "desc", "BACFDEHIG")]
    [InlineData("quantity", "asc", "GHIEDFCAB")]
    [InlineData("unitPrice", "desc", "ACFDEHIBG")]
    [InlineData("unitPrice", "asc", "GBHIEDFCA")]
    [InlineData("itemNumber", "sideways", "ABCFDEHIG")]
    public async Task GetEnhancedSalesProductDetailsAsync_Sort_中国范围内存路径跨页顺序稳定且无重复(
        string? sortField,
        string? sortOrder,
        string expectedOrder
    )
    {
        // 未开启供应商汇总且为中国供应商范围时走内存排序分页，必须与数据库路径同一口径。
        await SeedChinaSupplierAsync("CN-SORT", "中国排序供应商");
        await SeedProductSortFixtureAsync("CN-SORT");
        var service = CreateService();

        var pages = new List<PagedSalesProductDetailWithDiscountDto>();
        for (var pageIndex = 1; pageIndex <= 3; pageIndex++)
        {
            pages.Add(await service.GetEnhancedSalesProductDetailsAsync(
                CreateProductSortDateRange(),
                pageIndex: pageIndex,
                pageSize: 4,
                chinaSupplierScope: true,
                sortField: sortField,
                sortOrder: sortOrder
            ));
        }

        AssertProductSortPages(pages, expectedOrder);
    }

    [Fact]
    public async Task 二十八分店生产形态_三十次冷请求报表旅程均在两秒内完成()
    {
        // 红灯约束：测试夹具必须同时覆盖完整月份、去年同期、28 家分店、
        // 14 个分时段、8 家供应商、24 个商品、缺失成本和越权范围外数据。
        var fixture = await SeedTwentyEightStorePerformanceFixtureAsync();

        await AssertTwentyEightStorePerformancePreconditionsAsync(fixture);
        await AssertThirtyColdSamplesUnderTwoSecondsAsync(
            "营业额首页",
            () => RunRevenueHomeJourneyAsync(fixture)
        );
        await AssertThirtyColdSamplesUnderTwoSecondsAsync(
            "十四段分时表",
            () => RunHourlyTrafficJourneyAsync(fixture)
        );
        await AssertThirtyColdSamplesUnderTwoSecondsAsync(
            "整月逐日表",
            () => RunMonthlyDailyJourneyAsync(fixture)
        );
        await AssertThirtyColdSamplesUnderTwoSecondsAsync(
            "商品首屏并行旅程",
            () => RunProductFirstScreenJourneyAsync(fixture)
        );
        await AssertThirtyColdSamplesUnderTwoSecondsAsync(
            "澳洲供应商二十八店下钻",
            () => RunAustralianSupplierDrilldownJourneyAsync(fixture)
        );
        await AssertThirtyColdSamplesUnderTwoSecondsAsync(
            "中国供应商排行",
            () => RunChinaSupplierRankJourneyAsync(fixture)
        );
        await AssertThirtyColdSamplesUnderTwoSecondsAsync(
            "中国供应商二十八店下钻",
            () => RunChinaSupplierDrilldownJourneyAsync(fixture)
        );
        await AssertThirtyColdSamplesUnderTwoSecondsAsync(
            "商品二十八店下钻",
            () => RunProductBranchDrilldownJourneyAsync(fixture)
        );
    }

    [Fact]
    public async Task 性能夹具_闭区间月末逐行参数化后保留二十八家分店()
    {
        var startDate = new DateTime(2026, 8, 30);
        var endDate = new DateTime(2026, 8, 31);
        var branchCodes = Enumerable.Range(1, 28).Select(index => $"EDGE-{index:D2}").ToList();
        await InsertInBatchesAsync(branchCodes.Select(branchCode => CreateStoreSalesStatistic(
            startDate, branchCode, branchCode, 100m, 1
        )));
        // 这组断言曾在末日也走 batch 时红灯为 0 行；保持与现有逐行 Seed helper 一致的绑定方式。
        await InsertIndividuallyAsync(branchCodes.Select(branchCode => CreateStoreSalesStatistic(
            endDate, branchCode, branchCode, 100m, 1
        )));
        await InsertPosmInBatchesAsync(
            new[] { startDate, endDate }.SelectMany(date =>
                branchCodes.Select(branchCode => new SalesOrder
                {
                    OrderGuid = $"edge-{date:yyyyMMdd}-{branchCode}",
                    OrderTime = date.AddHours(9),
                    BranchCode = branchCode,
                    Status = 1,
                    ActualAmount = 0m,
                    TotalAmount = 0m,
                })
            )
        );

        var service = CreateService();
        var result = await service.GetBranchDailyPerformanceAsync(
            new DateRangeDto { StartDate = startDate, EndDate = endDate },
            branchCodes
        );

        Assert.Equal(28 * 2, result.Items.Count);
        Assert.Equal(28, result.Items.Count(row => row.Date == endDate));
        Assert.DoesNotContain(result.Items, row => row.Date > endDate);
    }

    [Fact]
    public void EnhancedProductDetail_搜索词参与缓存键但不写入日志()
    {
        var logger = new RecordingLogger();
        SalesDashboardCacheKeys.SetLogger(logger);

        try
        {
            var dateRange = new DateRangeDto
            {
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2026, 7, 1),
            };

            var first = SalesDashboardCacheKeys.EnhancedProductDetail(
                dateRange,
                new List<string> { "S1" },
                localSupplierCodes: null,
                chinaSupplierCodes: null,
                pageIndex: 1,
                pageSize: 20,
                productSearch: "  SECRET-BARCODE  "
            );
            var sameNormalized = SalesDashboardCacheKeys.EnhancedProductDetail(
                dateRange,
                new List<string> { "S1" },
                localSupplierCodes: null,
                chinaSupplierCodes: null,
                pageIndex: 1,
                pageSize: 20,
                productSearch: "SECRET-BARCODE"
            );
            var otherSearch = SalesDashboardCacheKeys.EnhancedProductDetail(
                dateRange,
                new List<string> { "S1" },
                localSupplierCodes: null,
                chinaSupplierCodes: null,
                pageIndex: 1,
                pageSize: 20,
                productSearch: "OTHER-BARCODE"
            );

            Assert.Equal(first, sameNormalized);
            Assert.NotEqual(first, otherSearch);
            Assert.Contains(logger.Messages, message => message.Contains("HasProductSearch=True", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, message => message.Contains("SECRET-BARCODE", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, message => message.Contains("OTHER-BARCODE", StringComparison.Ordinal));
        }
        finally
        {
            SalesDashboardCacheKeys.SetLogger(NullLogger.Instance);
        }
    }

    [Fact]
    public void EnhancedProductDetail_中国供应商范围参与缓存键()
    {
        var dateRange = new DateRangeDto
        {
            StartDate = new DateTime(2026, 7, 1),
            EndDate = new DateTime(2026, 7, 1),
        };

        var allProducts = SalesDashboardCacheKeys.EnhancedProductDetail(
            dateRange,
            new List<string> { "S1" },
            localSupplierCodes: null,
            chinaSupplierCodes: null,
            pageIndex: 1,
            pageSize: 20,
            chinaSupplierScope: false
        );
        var chinaProducts = SalesDashboardCacheKeys.EnhancedProductDetail(
            dateRange,
            new List<string> { "S1" },
            localSupplierCodes: null,
            chinaSupplierCodes: null,
            pageIndex: 1,
            pageSize: 20,
            chinaSupplierScope: true
        );

        Assert.NotEqual(allProducts, chinaProducts);
    }

    [Fact]
    public void EnhancedProductDetail_Sort_排序参与缓存键且默认排序沿用原键()
    {
        var logger = new ConcurrentRecordingLogger();
        var dateRange = new DateRangeDto
        {
            StartDate = new DateTime(2026, 7, 1),
            EndDate = new DateTime(2026, 7, 1),
        };
        // 服务层只会传 ProductReportSort.CacheToken：默认排序为 null，其余为 5 个白名单标记。
        var sortTokens = new[] { "amount:asc", "quantity:desc", "quantity:asc", "unitPrice:desc", "unitPrice:asc" };
        string Key(string? sortToken) => SalesDashboardCacheKeys.EnhancedProductDetail(
            dateRange,
            new List<string> { "S1" },
            localSupplierCodes: null,
            chinaSupplierCodes: null,
            pageIndex: 1,
            pageSize: 20,
            productSearch: null,
            productStatisticCacheVersion: "v1",
            chinaSupplierScope: false,
            sortToken: sortToken
        );

        string omittedKey;
        string defaultKey;
        Dictionary<string, string> sortedKeys;
        SalesDashboardCacheKeys.SetLogger(logger);
        try
        {
            omittedKey = SalesDashboardCacheKeys.EnhancedProductDetail(
                dateRange,
                new List<string> { "S1" },
                localSupplierCodes: null,
                chinaSupplierCodes: null,
                pageIndex: 1,
                pageSize: 20,
                productSearch: null,
                productStatisticCacheVersion: "v1",
                chinaSupplierScope: false
            );
            defaultKey = Key(ProductReportSort.Parse(null, null).CacheToken);
            sortedKeys = sortTokens.ToDictionary(token => token, token => Key(token));
        }
        finally
        {
            SalesDashboardCacheKeys.SetLogger(NullLogger.Instance);
        }

        // 默认排序与不传参数的历史调用逐字节相同，缓存键和 single-flight 去重键都不变。
        Assert.Equal(omittedKey, defaultKey);
        Assert.Equal(sortTokens.Length, sortedKeys.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(defaultKey, sortedKeys.Values);
        var messages = logger.Messages;
        Assert.Contains(messages, message =>
            message.Contains(sortedKeys["unitPrice:asc"], StringComparison.Ordinal)
            && message.Contains("Sort=unitPrice:asc", StringComparison.Ordinal));
        Assert.Contains(messages, message =>
            message.Contains(defaultKey, StringComparison.Ordinal)
            && message.Contains("Sort=default", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetBranchDailyPerformance_普通用户请求无权限分店返回空数组()
    {
        var serviceMock = new Mock<ISalesDashboardReactService>();
        var controller = CreateController(
            serviceMock.Object,
            CreateUserService(new[] { "S1" })
        );

        var response = await controller.GetBranchDailyPerformance(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 7),
            branchCodes: new List<string> { "S2" }
        );

        var responseValue = AssertOk(response).Value;
        var data = ExtractAnonymousData<List<BranchDailyPerformanceDto>>(responseValue);
        Assert.Empty(data);
        Assert.False(GetBoolProperty(responseValue!, "statisticsPending"));
        Assert.Equal(0, GetIntProperty(responseValue!, "statisticsExpectedItemCount"));
        Assert.Equal(0, GetIntProperty(responseValue!, "statisticsSnapshotItemCount"));
        serviceMock.Verify(
            service => service.GetBranchDailyPerformanceAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task GetBranchDailyPerformance_统计表查询失败时返回服务器错误()
    {
        await _localDb.Ado.ExecuteCommandAsync("DROP TABLE StoreSalesStatistic");
        var controller = CreateController(CreateService(), CreateUserService(new[] { "S1" }));

        var response = await controller.GetBranchDailyPerformance(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 7),
            branchCodes: new List<string> { "S1" }
        );

        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformance_统计表查询失败时返回服务器错误()
    {
        await _localDb.Ado.ExecuteCommandAsync("DROP TABLE StoreSalesStatistic");
        var controller = CreateController(CreateService(), CreateUserService(new[] { "S1" }));

        var response = await controller.GetExecutiveBranchPerformance(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 7),
            branchCodes: new List<string> { "S1" }
        );

        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetExecutiveHourlyTraffic_统计表查询失败时返回服务器错误()
    {
        await _localDb.Ado.ExecuteCommandAsync("DROP TABLE HourlySalesStatistic");
        var controller = CreateController(CreateService(), CreateUserService(new[] { "S1" }));

        var response = await controller.GetExecutiveHourlyTraffic(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1),
            branchCodes: new List<string> { "S1" }
        );

        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetSupplierStoreSales_统计表查询失败时返回服务器错误()
    {
        await EnsureProductStatisticRefreshStateAsync(new DateTime(2026, 7, 1));
        await _localDb.Ado.ExecuteCommandAsync("DROP TABLE ProductStoreDailySalesStatistic");
        var controller = CreateController(CreateService(), CreateUserService(new[] { "S1" }));

        var response = await controller.GetSupplierStoreSales(
            new List<string> { "AUS1" },
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1),
            branchCodes: new List<string> { "S1" }
        );

        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetChinaSupplierStoreSales_统计表查询失败时返回服务器错误()
    {
        await SeedSupplierMappingAsync("P-CN-ERROR", "200", "CN-ERROR");
        await EnsureProductStatisticRefreshStateAsync(new DateTime(2026, 7, 1));
        await _localDb.Ado.ExecuteCommandAsync("DROP TABLE ProductStoreDailySalesStatistic");
        var controller = CreateController(CreateService(), CreateUserService(new[] { "S1" }));

        var response = await controller.GetChinaSupplierStoreSales(
            new List<string> { "CN-ERROR" },
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1),
            branchCodes: new List<string> { "S1" }
        );

        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetSupplierSalesRank_主查询失败时返回服务器错误而非Fresh空结果()
    {
        var date = new DateTime(2026, 7, 1);
        await EnsureProductStatisticRefreshStateAsync(date);
        await _localDb.Ado.ExecuteCommandAsync("DROP TABLE ProductStoreDailySalesStatistic");
        var controller = CreateController(CreateService(), CreateUserService(new[] { "S1" }));

        var response = await controller.GetSupplierSalesRank(
            date,
            date,
            branchCodes: new List<string> { "S1" }
        );

        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetChinaSupplierSalesRank_主查询失败时返回服务器错误而非Fresh空结果()
    {
        var date = new DateTime(2026, 7, 1);
        await EnsureProductStatisticRefreshStateAsync(date);
        await _localDb.Ado.ExecuteCommandAsync("DROP TABLE ProductStoreDailySalesStatistic");
        var controller = CreateController(CreateService(), CreateUserService(new[] { "S1" }));

        var response = await controller.GetChinaSupplierSalesRankAsync(
            date,
            date,
            branchCodes: new List<string> { "S1" }
        );

        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetChinaSupplierBranchTotals_主查询失败时返回服务器错误而非Fresh空结果()
    {
        var date = new DateTime(2026, 7, 1);
        await EnsureProductStatisticRefreshStateAsync(date);
        await _localDb.Ado.ExecuteCommandAsync("DROP TABLE ProductStoreDailySalesStatistic");
        var controller = CreateController(CreateService(), CreateUserService(new[] { "S1" }));

        var response = await controller.GetChinaSupplierBranchTotalsAsync(
            date,
            date,
            branchCodes: new List<string> { "S1" }
        );

        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetChinaSupplierBranchTotals_普通用户无分店时不调用服务层全量查询()
    {
        var serviceMock = new Mock<ISalesDashboardReactService>();
        var controller = CreateController(serviceMock.Object, CreateUserService(Array.Empty<string>()));

        var response = await controller.GetChinaSupplierBranchTotalsAsync(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1)
        );

        var responseValue = AssertOk(response).Value;
        Assert.Empty(ExtractAnonymousData<List<ChinaSupplierBranchTotalDto>>(responseValue));
        Assert.Equal("no-access", GetStringProperty(responseValue!, "CacheVersion"));
        serviceMock.Verify(
            service => service.GetChinaSupplierBranchTotalsAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<ProductReportStatisticStatusDto>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task GetChinaSupplierBranchTotals_返回统一包络且透传授权分店()
    {
        await SeedChinaSupplierAsync("CN1", "直写供应商");
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S1", "CN1", "P-CN1", "直写商品", 88m, 8, 2);
        await SeedProductStoreDailySalesAsync(new DateTime(2026, 7, 1), "S2", "CN1", "P-CN1", "直写商品", 12m, 1, 1);
        var controller = CreateController(CreateService(), CreateUserService(new[] { "S1" }));

        var response = await controller.GetChinaSupplierBranchTotalsAsync(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1),
            branchCodes: new List<string> { "S1", "S2" }
        );

        var responseValue = AssertOk(response).Value;
        var row = Assert.Single(ExtractAnonymousData<List<ChinaSupplierBranchTotalDto>>(responseValue));
        Assert.Equal("S1", row.BranchCode);
        Assert.Equal(88m, row.TotalAmount);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, GetStringProperty(responseValue!, "StatisticStatus"));
        Assert.False(string.IsNullOrWhiteSpace(GetStringProperty(responseValue!, "CacheVersion")));
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetails_主查询失败时返回服务器错误而非Fresh空结果()
    {
        var date = new DateTime(2026, 7, 1);
        await EnsureProductStatisticRefreshStateAsync(date);
        await _localDb.Ado.ExecuteCommandAsync("DROP TABLE ProductStoreDailySalesStatistic");
        var controller = CreateController(CreateService(), CreateUserService(new[] { "S1" }));

        var response = await controller.GetEnhancedSalesProductDetails(
            date,
            date,
            branchCodes: new List<string> { "S1" }
        );

        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetProductSalesByAllBranches_主查询失败时返回服务器错误而非Fresh空结果()
    {
        var date = new DateTime(2026, 7, 1);
        await EnsureProductStatisticRefreshStateAsync(date);
        await _localDb.Ado.ExecuteCommandAsync("DROP TABLE ProductStoreDailySalesStatistic");
        var controller = CreateController(CreateService(), CreateUserService(new[] { "S1" }));

        var response = await controller.GetProductSalesByAllBranches(
            date,
            date,
            productCode: "P-ERROR",
            branchCodes: new List<string> { "S1" }
        );

        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetAustralianSupplierStoreSalesDetails_主查询失败时返回服务器错误而非Fresh空结果()
    {
        var date = new DateTime(2026, 7, 1);
        await EnsureProductStatisticRefreshStateAsync(date);
        await _localDb.Ado.ExecuteCommandAsync("DROP TABLE AustralianSupplierStoreSalesDetail");
        var controller = CreateController(CreateService(), CreateUserService(new[] { "S1" }));

        var response = await controller.GetAustralianSupplierStoreSalesDetails(
            date,
            date,
            branchCodes: new List<string> { "S1" }
        );

        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetChinaSupplierStoreSalesDetails_主查询失败时返回服务器错误而非Fresh空结果()
    {
        var date = new DateTime(2026, 7, 1);
        await EnsureProductStatisticRefreshStateAsync(date);
        await _localDb.Ado.ExecuteCommandAsync("DROP TABLE ChinaSupplierStoreSalesDetail");
        var controller = CreateController(CreateService(), CreateUserService(new[] { "S1" }));

        var response = await controller.GetChinaSupplierStoreSalesDetails(
            date,
            date,
            branchCodes: new List<string> { "S1" }
        );

        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetExecutiveBranchPerformance_普通用户未传分店时只查用户分店()
    {
        List<string>? capturedBranchCodes = null;
        int? capturedTopN = -1;
        var expected = new ExecutiveBranchPerformanceResultDto
        {
            Items = new List<ExecutiveBranchPerformanceDto>(),
            StatisticsPending = true,
            StatisticsExpectedBranchCount = 2,
            StatisticsSnapshotBranchCount = 0,
        };
        var serviceMock = new Mock<ISalesDashboardReactService>();
        serviceMock
            .Setup(service => service.GetExecutiveBranchPerformanceAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<int?>(),
                It.IsAny<List<string>?>()
            ))
            .Callback<DateRangeDto, int?, List<string>?>((_, topN, branchCodes) =>
            {
                capturedTopN = topN;
                capturedBranchCodes = branchCodes;
            })
            .ReturnsAsync(expected);
        var controller = CreateController(serviceMock.Object, CreateUserService(new[] { "S1", "S3" }));

        var response = await controller.GetExecutiveBranchPerformance(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 7)
        );

        Assert.Equal(new[] { "S1", "S3" }, capturedBranchCodes);
        Assert.Null(capturedTopN);
        var responseValue = AssertOk(response).Value;
        Assert.Same(expected.Items, ExtractAnonymousData<List<ExecutiveBranchPerformanceDto>>(responseValue));
        Assert.True(GetBoolProperty(responseValue!, "StatisticsPending"));
        Assert.Equal(2, GetIntProperty(responseValue!, "StatisticsExpectedBranchCount"));
        Assert.Equal(0, GetIntProperty(responseValue!, "StatisticsSnapshotBranchCount"));
        // 默认营业额排行不能为商品页兼容性额外读取商品统计状态。
        serviceMock.Verify(
            service => service.GetProductReportStatisticStatusAsync(It.IsAny<DateRangeDto>()),
            Times.Never
        );
    }

    [Theory]
    [InlineData(SalesStatisticRefreshStatus.Fresh, false)]
    [InlineData(SalesStatisticRefreshStatus.Pending, true)]
    public async Task GetExecutiveBranchPerformance_商品元数据请求返回统计完整性契约(
        string status,
        bool expectedPending
    )
    {
        var statisticUpdatedAt = new DateTime(2026, 7, 8, 9, 10, 11, DateTimeKind.Utc);
        var serviceMock = new Mock<ISalesDashboardReactService>();
        serviceMock
            .Setup(service => service.GetExecutiveBranchPerformanceAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<int?>(),
                It.IsAny<List<string>?>()
            ))
            .ReturnsAsync(new ExecutiveBranchPerformanceResultDto());
        serviceMock
            .Setup(service => service.GetProductReportStatisticStatusAsync(It.IsAny<DateRangeDto>()))
            .ReturnsAsync(new ProductReportStatisticStatusDto
            {
                StatisticStatus = status,
                StatisticMessage = expectedPending ? "商品统计正在处理中，请稍后重试。" : null,
                StatisticUpdatedAt = statisticUpdatedAt,
                CacheVersion = $"product-{status.ToLowerInvariant()}",
            });
        var controller = CreateController(serviceMock.Object, CreateUserService(new[] { "S1" }));

        var response = await controller.GetExecutiveBranchPerformance(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1),
            includeProductStatisticMetadata: true
        );

        var value = AssertOk(response).Value!;
        Assert.Equal(status, GetStringProperty(value, "StatisticStatus"));
        Assert.Equal(
            expectedPending ? "商品统计正在处理中，请稍后重试。" : null,
            value.GetType().GetProperty("StatisticMessage")!.GetValue(value)
        );
        Assert.Equal(
            statisticUpdatedAt,
            value.GetType().GetProperty("StatisticUpdatedAt")!.GetValue(value)
        );
        Assert.Equal($"product-{status.ToLowerInvariant()}", GetStringProperty(value, "CacheVersion"));
        serviceMock.Verify(
            service => service.GetProductReportStatisticStatusAsync(It.IsAny<DateRangeDto>()),
            Times.Exactly(2)
        );
    }

    [Theory]
    [InlineData(SalesStatisticRefreshStatus.Fresh, SalesStatisticRefreshStatus.Pending)]
    [InlineData(SalesStatisticRefreshStatus.Failed, SalesStatisticRefreshStatus.Failed)]
    public async Task 商品摘要读取时版本变化不能返回完整新版本(string afterStatus, string expectedStatus)
    {
        var serviceMock = new Mock<ISalesDashboardReactService>();
        serviceMock.Setup(service => service.GetExecutiveBranchPerformanceAsync(
                It.IsAny<DateRangeDto>(), It.IsAny<int?>(), It.IsAny<List<string>?>()))
            .ReturnsAsync(new ExecutiveBranchPerformanceResultDto());
        serviceMock.SetupSequence(service => service.GetProductReportStatisticStatusAsync(It.IsAny<DateRangeDto>()))
            .ReturnsAsync(new ProductReportStatisticStatusDto
            {
                StatisticStatus = SalesStatisticRefreshStatus.Fresh, CacheVersion = "before",
            })
            .ReturnsAsync(new ProductReportStatisticStatusDto
            {
                StatisticStatus = afterStatus, CacheVersion = "after",
            });
        var controller = CreateController(serviceMock.Object, CreateUserService(new[] { "S1" }));
        var response = await controller.GetExecutiveBranchPerformance(
            new DateTime(2026, 7, 1), new DateTime(2026, 7, 1), includeProductStatisticMetadata: true);
        Assert.Equal(expectedStatus, GetStringProperty(AssertOk(response).Value!, "StatisticStatus"));
    }

    [Fact]
    public async Task GetExecutiveBranchPerformance_商品元数据请求无可访问分店仍返回完整空快照契约()
    {
        var serviceMock = new Mock<ISalesDashboardReactService>();
        var controller = CreateController(serviceMock.Object, CreateUserService(Array.Empty<string>()));

        var response = await controller.GetExecutiveBranchPerformance(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1),
            includeProductStatisticMetadata: true
        );

        var value = AssertOk(response).Value!;
        Assert.Empty(ExtractAnonymousData<List<ExecutiveBranchPerformanceDto>>(value));
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, GetStringProperty(value, "StatisticStatus"));
        Assert.Equal("当前账号没有可访问的分店范围", GetStringProperty(value, "StatisticMessage"));
        Assert.NotNull(value.GetType().GetProperty("StatisticUpdatedAt")!.GetValue(value));
        Assert.Equal("no-access", GetStringProperty(value, "CacheVersion"));
        serviceMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetExecutiveHourlyTraffic_普通用户请求分店时取权限交集()
    {
        List<string>? capturedBranchCodes = null;
        var serviceMock = new Mock<ISalesDashboardReactService>();
        serviceMock
            .Setup(service => service.GetExecutiveHourlyTrafficAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>()
            ))
            .Callback<DateRangeDto, List<string>?>((_, branchCodes) =>
                capturedBranchCodes = branchCodes
            )
            .ReturnsAsync(new ExecutiveReportResultDto<ExecutiveHourlyTrafficDto>());
        var controller = CreateController(serviceMock.Object, CreateUserService(new[] { "S1", "S3" }));

        await controller.GetExecutiveHourlyTraffic(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1),
            branchCodes: new List<string> { "S1", "S2" }
        );

        var branchCode = Assert.Single(capturedBranchCodes!);
        Assert.Equal("S1", branchCode);
    }

    [Fact]
    public async Task GetProductSalesByAllBranches_普通用户请求分店时取权限交集()
    {
        List<string>? capturedBranchCodes = null;
        var serviceMock = new Mock<ISalesDashboardReactService>();
        serviceMock
            .Setup(service => service.GetProductReportStatisticStatusAsync(It.IsAny<DateRangeDto>()))
            .ReturnsAsync(new ProductReportStatisticStatusDto
            {
                StatisticStatus = SalesStatisticRefreshStatus.Fresh,
                CacheVersion = "fresh-v1",
            });
        serviceMock
            .Setup(service => service.GetProductSalesByAllBranchesAsync(
                It.IsAny<DateRangeDto>(),
                "P1",
                It.IsAny<List<string>?>(),
                It.IsAny<ProductReportStatisticStatusDto>()
            ))
            .Callback<DateRangeDto, string, List<string>?, ProductReportStatisticStatusDto>((_, _, branchCodes, _) =>
                capturedBranchCodes = branchCodes
            )
            .ReturnsAsync(new List<ProductBranchSalesDto>());
        var controller = CreateController(serviceMock.Object, CreateUserService(new[] { "S1", "S3" }));

        await controller.GetProductSalesByAllBranches(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1),
            productCode: "P1",
            branchCodes: new List<string> { "S1", "S2" }
        );

        var branchCode = Assert.Single(capturedBranchCodes!);
        Assert.Equal("S1", branchCode);
    }

    [Fact]
    public async Task GetSupplierSalesRank_普通用户无分店时不调用服务层全量查询()
    {
        var serviceMock = new Mock<ISalesDashboardReactService>();
        var controller = CreateController(serviceMock.Object, CreateUserService(Array.Empty<string>()));

        var response = await controller.GetSupplierSalesRank(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1)
        );

        var responseValue = AssertOk(response).Value;
        var data = ExtractAnonymousData<List<SupplierSalesRankDto>>(responseValue);
        Assert.Empty(data);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, GetStringProperty(responseValue!, "StatisticStatus"));
        Assert.Equal("no-access", GetStringProperty(responseValue!, "CacheVersion"));
        Assert.NotNull(responseValue!.GetType().GetProperty("StatisticUpdatedAt")!.GetValue(responseValue));
        serviceMock.Verify(
            service => service.GetProductReportStatisticStatusAsync(It.IsAny<DateRangeDto>()),
            Times.Never
        );
        serviceMock.Verify(
            service => service.GetSupplierSalesRankAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<int>(),
                It.IsAny<string?>()
            ),
            Times.Never
        );
    }

    [Theory]
    [InlineData(SalesStatisticRefreshStatus.Pending)]
    [InlineData(SalesStatisticRefreshStatus.Stale)]
    [InlineData(SalesStatisticRefreshStatus.Failed)]
    public async Task GetSupplierSalesRank_非Fresh时返回空数据和统计状态且不查询聚合(
        string status
    )
    {
        var serviceMock = new Mock<ISalesDashboardReactService>();
        serviceMock
            .Setup(service => service.GetProductReportStatisticStatusAsync(It.IsAny<DateRangeDto>()))
            .ReturnsAsync(new ProductReportStatisticStatusDto
            {
                StatisticStatus = status,
                StatisticMessage = "通用状态提示",
                CacheVersion = $"version-{status}",
            });
        var controller = CreateController(serviceMock.Object, CreateUserService(new[] { "S1" }));

        var response = await controller.GetSupplierSalesRank(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1)
        );

        var responseValue = AssertOk(response).Value!;
        Assert.Empty(ExtractAnonymousData<List<SupplierSalesRankDto>>(responseValue));
        Assert.Equal(status, GetStringProperty(responseValue, "statisticStatus"));
        Assert.Equal($"version-{status}", GetStringProperty(responseValue, "cacheVersion"));
        serviceMock.Verify(
            service => service.GetSupplierSalesRankAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<ProductReportStatisticStatusDto>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task GetSupplierSalesRank_统计状态读取失败时返回服务器错误()
    {
        var serviceMock = new Mock<ISalesDashboardReactService>();
        serviceMock
            .Setup(service => service.GetProductReportStatisticStatusAsync(It.IsAny<DateRangeDto>()))
            .ThrowsAsync(new InvalidOperationException("state table unavailable"));
        var controller = CreateController(serviceMock.Object, CreateUserService(new[] { "S1" }));

        var response = await controller.GetSupplierSalesRank(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1)
        );

        Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, ((ObjectResult)response).StatusCode);
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetails_普通用户无分店时不调用服务层全量查询()
    {
        var serviceMock = new Mock<ISalesDashboardReactService>();
        var controller = CreateController(serviceMock.Object, CreateUserService(Array.Empty<string>()));

        var response = await controller.GetEnhancedSalesProductDetails(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1),
            pageIndex: 1,
            pageSize: 50
        );

        var data = ExtractAnonymousData<PagedSalesProductDetailWithDiscountDto>(AssertOk(response).Value);
        Assert.Empty(data.Data);
        serviceMock.Verify(
            service => service.GetEnhancedSalesProductDetailsAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()
            ),
            Times.Never
        );
        // 控制器实际调用的是带统计状态的重载，两个重载都必须未被调用。
        serviceMock.Verify(
            service => service.GetEnhancedSalesProductDetailsAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<ProductReportStatisticStatusDto>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task GetEnhancedSalesProductDetails_普通用户未传分店时只查用户分店()
    {
        List<string>? capturedBranchCodes = null;
        string? capturedProductSearch = null;
        var capturedChinaSupplierScope = false;
        string? capturedSortField = "not-called";
        string? capturedSortOrder = "not-called";
        var serviceMock = new Mock<ISalesDashboardReactService>();
        serviceMock
            .Setup(service => service.GetProductReportStatisticStatusAsync(It.IsAny<DateRangeDto>()))
            .ReturnsAsync(new ProductReportStatisticStatusDto
            {
                StatisticStatus = SalesStatisticRefreshStatus.Fresh,
                CacheVersion = "fresh-v1",
            });
        serviceMock
            .Setup(service => service.GetEnhancedSalesProductDetailsAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<ProductReportStatisticStatusDto>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()
            ))
            .Callback<DateRangeDto, List<string>?, List<string>?, List<string>?, int, int, string?, ProductReportStatisticStatusDto, bool, string?, string?>(
                (_, branchCodes, _, _, _, _, productSearch, _, chinaSupplierScope, sortField, sortOrder) =>
                {
                    capturedBranchCodes = branchCodes;
                    capturedProductSearch = productSearch;
                    capturedChinaSupplierScope = chinaSupplierScope;
                    capturedSortField = sortField;
                    capturedSortOrder = sortOrder;
                }
            )
            .ReturnsAsync(new PagedSalesProductDetailWithDiscountDto());
        var controller = CreateController(serviceMock.Object, CreateUserService(new[] { "S1", "S3" }));

        await controller.GetEnhancedSalesProductDetails(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1),
            pageIndex: 1,
            pageSize: 50,
            productSearch: "HB001",
            supplierScope: " China "
        );

        Assert.Equal(new[] { "S1", "S3" }, capturedBranchCodes);
        Assert.Equal("HB001", capturedProductSearch);
        Assert.True(capturedChinaSupplierScope);
        // 未传排序时透传 null，由服务层回退为金额降序。
        Assert.Null(capturedSortField);
        Assert.Null(capturedSortOrder);
    }

    [Theory]
    [InlineData(" UnitPrice ", "ASC", "unitPrice:asc")]
    [InlineData("unitPrice\r\n[FAKE] admin login", "asc", "amount:asc")]
    public async Task GetEnhancedSalesProductDetails_Sort_原样透传排序参数且日志只记录归一化标记(
        string sortField,
        string sortOrder,
        string expectedLoggedToken
    )
    {
        string? capturedSortField = "not-called";
        string? capturedSortOrder = "not-called";
        var serviceMock = new Mock<ISalesDashboardReactService>();
        serviceMock
            .Setup(service => service.GetProductReportStatisticStatusAsync(It.IsAny<DateRangeDto>()))
            .ReturnsAsync(new ProductReportStatisticStatusDto
            {
                StatisticStatus = SalesStatisticRefreshStatus.Fresh,
                CacheVersion = "fresh-v1",
            });
        serviceMock
            .Setup(service => service.GetEnhancedSalesProductDetailsAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<ProductReportStatisticStatusDto>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()
            ))
            .Callback<DateRangeDto, List<string>?, List<string>?, List<string>?, int, int, string?, ProductReportStatisticStatusDto, bool, string?, string?>(
                (_, _, _, _, _, _, _, _, _, capturedField, capturedOrder) =>
                {
                    capturedSortField = capturedField;
                    capturedSortOrder = capturedOrder;
                }
            )
            .ReturnsAsync(new PagedSalesProductDetailWithDiscountDto());
        // 与 CreateController 相同的装配，只把日志器换成可记录的实现，用来确认原始排序参数不进日志。
        var logger = new ConcurrentRecordingLogger<SalesDashboardController>();
        var controller = new SalesDashboardController(
            serviceMock.Object,
            logger,
            CreateUserService(new[] { "S1" }),
            Mock.Of<ISalesDashboardCacheWarmer>(),
            Mock.Of<IRoleService>()
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "user-1") },
                        "TestAuth"
                    )),
                },
            },
        };

        var response = await controller.GetEnhancedSalesProductDetails(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 1),
            pageIndex: 1,
            pageSize: 20,
            sortField: sortField,
            sortOrder: sortOrder
        );

        AssertOk(response);
        // 控制器不做归一化，原样交给服务层统一解析，避免两处白名单口径分叉。
        Assert.Equal(sortField, capturedSortField);
        Assert.Equal(sortOrder, capturedSortOrder);
        Assert.Contains(logger.Messages, message => message.Contains($"Sort={expectedLoggedToken}", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains(sortField, StringComparison.Ordinal));
        serviceMock.Verify(
            service => service.GetEnhancedSalesProductDetailsAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task GetCompactSalesBoard_普通用户无分店时拒绝且不调用服务()
    {
        var serviceMock = new Mock<ISalesDashboardReactService>();
        var controller = CreateController(serviceMock.Object, CreateUserService(Array.Empty<string>()));

        var response = await controller.GetCompactSalesBoard(
            new DateTime(2026, 8, 1),
            new DateTime(2026, 8, 1)
        );

        Assert.IsType<ForbidResult>(response);
        serviceMock.Verify(service => service.GetCompactSalesBoardAsync(
            It.IsAny<CompactSalesBoardQuery>()), Times.Never);
    }

    [Fact]
    public async Task GetCompactSalesBoard_普通用户请求越权分店时仅传授权交集()
    {
        CompactSalesBoardQuery? capturedQuery = null;
        var serviceMock = new Mock<ISalesDashboardReactService>();
        serviceMock.Setup(service => service.GetCompactSalesBoardAsync(It.IsAny<CompactSalesBoardQuery>()))
            .Callback<CompactSalesBoardQuery>(query => capturedQuery = query)
            .ReturnsAsync(new CompactSalesBoardDto());
        var controller = CreateController(serviceMock.Object, CreateUserService(new[] { "S1", "S3" }));

        var response = await controller.GetCompactSalesBoard(
            new DateTime(2026, 8, 1),
            new DateTime(2026, 8, 1),
            branchCodes: new List<string> { "S1", "S2" }
        );

        Assert.IsType<OkObjectResult>(response);
        Assert.Equal(new[] { "S1" }, capturedQuery?.BranchCodes);
    }

    [Fact]
    public async Task GetCompactSalesBoard_授权范围与联动选中项分开传给服务()
    {
        CompactSalesBoardQuery? capturedQuery = null;
        var serviceMock = new Mock<ISalesDashboardReactService>();
        serviceMock.Setup(service => service.GetCompactSalesBoardAsync(It.IsAny<CompactSalesBoardQuery>()))
            .Callback<CompactSalesBoardQuery>(query => capturedQuery = query)
            .ReturnsAsync(new CompactSalesBoardDto());
        var controller = CreateController(serviceMock.Object, CreateUserService(new[] { "S1", "S3" }));

        var response = await controller.GetCompactSalesBoard(
            new DateTime(2026, 8, 1),
            new DateTime(2026, 8, 2),
            selectedBranchCode: "S3",
            selectedChinaSupplierCode: "HB215",
            selectedProductCode: "P-1",
            keyword: "canvas",
            sortField: "quantity",
            sortOrder: "asc",
            pageIndex: 2,
            pageSize: 50,
            forceRefresh: true
        );

        Assert.IsType<OkObjectResult>(response);
        Assert.NotNull(capturedQuery);
        // 未显式请求分店时，普通用户的授权范围取自身关联分店，不能退化为全分店。
        Assert.Equal(new[] { "S1", "S3" }, capturedQuery!.BranchCodes);
        Assert.Equal(("S3", "HB215", "P-1"), (capturedQuery.SelectedBranchCode, capturedQuery.SelectedChinaSupplierCode, capturedQuery.SelectedProductCode));
        Assert.Equal(("canvas", "quantity", "asc"), (capturedQuery.Keyword, capturedQuery.SortField, capturedQuery.SortOrder));
        Assert.Equal((2, 50, true), (capturedQuery.PageIndex, capturedQuery.PageSize, capturedQuery.ForceRefresh));
        Assert.Equal(new DateTime(2026, 8, 2), capturedQuery.DateRange.EndDate);
    }

    private async Task<TwentyEightStorePerformanceFixture> SeedTwentyEightStorePerformanceFixtureAsync()
    {
        var branchCodes = Enumerable.Range(1, 28).Select(index => $"S{index:00}").ToList();
        var currentStart = new DateTime(2026, 8, 1);
        var compareStart = new DateTime(2025, 8, 1);
        const int monthDays = 31;

        foreach (var branchCode in branchCodes)
        {
            await SeedStoreAsync(branchCode, $"性能分店 {branchCode}");
        }
        await SeedStoreAsync("S99", "范围外性能分店");

        var australianSupplierCodes = Enumerable.Range(1, 8)
            .Select(index => $"AUS-PERF-{index:D2}")
            .ToList();
        var chinaSupplierCodes = Enumerable.Range(1, 8)
            .Select(index => $"CN-PERF-{index:D2}")
            .ToList();
        foreach (var supplierCode in australianSupplierCodes)
        {
            await SeedLocalSupplierAsync(supplierCode, $"澳洲性能供应商 {supplierCode}");
        }
        foreach (var supplierCode in chinaSupplierCodes)
        {
            await SeedChinaSupplierAsync(supplierCode, $"中国性能供应商 {supplierCode}");
        }

        var productCodes = Enumerable.Range(1, 24)
            .Select(index => $"P-PERF-{index:D2}")
            .ToList();
        await SeedProductsAsync(productCodes.Select(productCode => (
            productCode,
            $"HB-{productCode}",
            $"BAR-{productCode}"
        )));
        await SeedProductAsync("P-PERF-OUTSIDE", "HB-P-PERF-OUTSIDE", "BAR-P-PERF-OUTSIDE");
        await SeedSupplierMappingsAsync(productCodes.Select((productCode, index) => (
            productCode,
            index < 16 ? australianSupplierCodes[index % australianSupplierCodes.Count] : "200",
            index < 16 ? string.Empty : chinaSupplierCodes[(index - 16) % chinaSupplierCodes.Count]
        )));

        var storeStatistics = new List<StoreSalesStatistic>();
        var hourlyStatistics = new List<HourlySalesStatistic>();
        var productStatistics = new List<ProductStoreDailySalesStatistic>();
        var productRefreshStates = new List<SalesStatisticRefreshState>();
        var closingStoreStatistics = new List<StoreSalesStatistic>();
        var closingHourlyStatistics = new List<HourlySalesStatistic>();
        var closingProductStatistics = new List<ProductStoreDailySalesStatistic>();
        var closingProductRefreshStates = new List<SalesStatisticRefreshState>();
        var salesSourceOrders = new List<SalesOrder>();

        for (var dayOffset = 0; dayOffset < monthDays; dayOffset++)
        {
            var currentDate = currentStart.AddDays(dayOffset);
            var compareDate = compareStart.AddDays(dayOffset);
            var isClosingDay = dayOffset == monthDays - 1;
            var refreshStateTarget = isClosingDay ? closingProductRefreshStates : productRefreshStates;
            var storeStatisticTarget = isClosingDay ? closingStoreStatistics : storeStatistics;
            var hourlyStatisticTarget = isClosingDay ? closingHourlyStatistics : hourlyStatistics;
            var productStatisticTarget = isClosingDay ? closingProductStatistics : productStatistics;
            refreshStateTarget.AddRange(new[]
            {
                CreateFreshProductRefreshState(currentDate),
                CreateFreshProductRefreshState(compareDate),
            });

            for (var branchIndex = 0; branchIndex < branchCodes.Count; branchIndex++)
            {
                var branchCode = branchCodes[branchIndex];
                var branchName = $"性能分店 {branchCode}";
                storeStatisticTarget.Add(CreateStoreSalesStatistic(
                    currentDate,
                    branchCode,
                    branchName,
                    12_000m + (branchIndex * 100m) + dayOffset,
                    120 + branchIndex
                ));
                storeStatisticTarget.Add(CreateStoreSalesStatistic(
                    compareDate,
                    branchCode,
                    branchName,
                    10_800m + (branchIndex * 90m) + dayOffset,
                    110 + branchIndex
                ));
                salesSourceOrders.Add(new SalesOrder
                {
                    OrderGuid = $"perf-current-{dayOffset:D2}-{branchCode}",
                    OrderTime = currentDate.AddHours(9),
                    BranchCode = branchCode,
                    Status = 1,
                    ActualAmount = 0m,
                    TotalAmount = 0m,
                });
                salesSourceOrders.Add(new SalesOrder
                {
                    OrderGuid = $"perf-compare-{dayOffset:D2}-{branchCode}",
                    OrderTime = compareDate.AddHours(9),
                    BranchCode = branchCode,
                    Status = 1,
                    ActualAmount = 0m,
                    TotalAmount = 0m,
                });

                for (var hour = 8; hour < 22; hour++)
                {
                    hourlyStatisticTarget.Add(CreateHourlySalesStatistic(
                        currentDate,
                        hour,
                        branchCode,
                        branchName,
                        400m + ((hour - 8) * 25m) + branchIndex,
                        4 + (hour - 8)
                    ));
                    hourlyStatisticTarget.Add(CreateHourlySalesStatistic(
                        compareDate,
                        hour,
                        branchCode,
                        branchName,
                        360m + ((hour - 8) * 22m) + branchIndex,
                        3 + (hour - 8)
                    ));
                }

                for (var productIndex = 0; productIndex < productCodes.Count; productIndex++)
                {
                    var productCode = productCodes[productIndex];
                    var supplierCode = productIndex < 16
                        ? australianSupplierCodes[productIndex % australianSupplierCodes.Count]
                        : chinaSupplierCodes[(productIndex - 16) % chinaSupplierCodes.Count];
                    var currentAmount = 1_000m - (productIndex * 20m) + branchIndex + dayOffset;
                    var compareAmount = 900m - (productIndex * 18m) + branchIndex + dayOffset;
                    var hasCost = productIndex != 0;
                    productStatisticTarget.Add(CreateProductDailyStatistic(
                        currentDate,
                        branchCode,
                        supplierCode,
                        productCode,
                        $"性能商品 {productCode}",
                        currentAmount,
                        10 + productIndex,
                        2 + (productIndex % 3),
                        $"BAR-{productCode}",
                        hasCost ? decimal.Round(currentAmount * 0.6m, 2) : null,
                        hasCost ? decimal.Round(currentAmount * 0.4m, 2) : null
                    ));
                    productStatisticTarget.Add(CreateProductDailyStatistic(
                        compareDate,
                        branchCode,
                        supplierCode,
                        productCode,
                        $"性能商品 {productCode}",
                        compareAmount,
                        9 + productIndex,
                        2 + (productIndex % 3),
                        $"BAR-{productCode}",
                        hasCost ? decimal.Round(compareAmount * 0.6m, 2) : null,
                        hasCost ? decimal.Round(compareAmount * 0.4m, 2) : null
                    ));
                }
            }

            // 高额范围外数据用于证明每个旅程真正以授权 28 店为筛选边界。
            productStatisticTarget.Add(CreateProductDailyStatistic(
                currentDate, "S99", australianSupplierCodes[0], "P-PERF-OUTSIDE", "范围外商品", 999_999m,
                1, 1, "BAR-P-PERF-OUTSIDE", 1m, 999_998m
            ));
            productStatisticTarget.Add(CreateProductDailyStatistic(
                compareDate, "S99", australianSupplierCodes[0], "P-PERF-OUTSIDE", "范围外商品", 888_888m,
                1, 1, "BAR-P-PERF-OUTSIDE", 1m, 888_887m
            ));
        }

        // 种子阶段不计入性能样本；批量写入让测试稳定反映查询路径而不是逐行 I/O。
        await InsertInBatchesAsync(storeStatistics);
        await InsertInBatchesAsync(hourlyStatistics);
        await InsertInBatchesAsync(productStatistics);
        await InsertInBatchesAsync(productRefreshStates);
        // SqlSugar SQLite 的批量 DateTime 绑定会把闭区间末日写为带精度的 TEXT；
        // 最后一日改用逐行参数化写入，保持与既有日统计测试一致的可查询边界语义。
        await InsertIndividuallyAsync(closingStoreStatistics);
        await InsertIndividuallyAsync(closingHourlyStatistics);
        await InsertIndividuallyAsync(closingProductStatistics);
        await InsertIndividuallyAsync(closingProductRefreshStates);
        await InsertPosmInBatchesAsync(salesSourceOrders);

        return new TwentyEightStorePerformanceFixture(
            new DateRangeDto
            {
                StartDate = currentStart,
                EndDate = currentStart.AddDays(monthDays - 1),
                CompareStartDate = compareStart,
                CompareEndDate = compareStart.AddDays(monthDays - 1),
            },
            branchCodes,
            australianSupplierCodes[1],
            chinaSupplierCodes[0],
            productCodes[0],
            new ProductReportStatisticStatusDto
            {
                StatisticStatus = SalesStatisticRefreshStatus.Fresh,
                CacheVersion = "performance-fixture-v1",
            }
        );
    }

    private async Task AssertTwentyEightStorePerformancePreconditionsAsync(
        TwentyEightStorePerformanceFixture fixture
    )
    {
        var refreshStates = await _localDb.Queryable<SalesStatisticRefreshState>()
            .Where(state => state.StatisticType == SalesStatisticType.ProductStoreDaily)
            .ToListAsync();
        Assert.Equal(62, refreshStates.Count);
        Assert.All(refreshStates, state => Assert.Equal(SalesStatisticRefreshStatus.Fresh, state.Status));
        Assert.Equal(31, refreshStates.Count(state => state.Date >= fixture.DateRange.StartDate && state.Date <= fixture.DateRange.EndDate));
        Assert.Equal(31, refreshStates.Count(state => state.Date >= fixture.DateRange.CompareStartDate && state.Date <= fixture.DateRange.CompareEndDate));
        var service = CreateService();
        var publicProductStatus = await service.GetProductReportStatisticStatusAsync(fixture.DateRange);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, publicProductStatus.StatisticStatus);
        Assert.Equal(fixture.ProductStatisticStatus.StatisticStatus, publicProductStatus.StatisticStatus);
        Assert.False(string.IsNullOrWhiteSpace(publicProductStatus.CacheVersion));
        fixture.ProductStatisticStatus.CacheVersion = publicProductStatus.CacheVersion;
        fixture.ProductStatisticStatus.StatisticUpdatedAt = publicProductStatus.StatisticUpdatedAt;
        // Controller 先取得相同的公开 Fresh 水位，再把状态对象注入三个商品端点；
        // 性能样本沿用此形态，避免每个内部调用额外重复读取状态表。

        var revenue = await service.GetExecutiveBranchPerformanceAsync(
            fixture.DateRange,
            branchCodes: fixture.BranchCodes
        );
        Assert.Equal(28, revenue.Items.Count);
        Assert.DoesNotContain(revenue.Items, row => row.BranchCode == "S99");

        var hourly = await service.GetExecutiveHourlyTrafficAsync(fixture.DateRange, fixture.BranchCodes);
        Assert.Equal(28 * 14, hourly.Items.Count);
        Assert.Equal(14, hourly.Items.Select(row => row.Hour).Distinct().Count());

        var daily = await service.GetBranchDailyPerformanceAsync(fixture.DateRange, fixture.BranchCodes);
        Assert.Equal(28 * 31, daily.Items.Count);
        Assert.Equal(31, daily.Items.Select(row => row.Date).Distinct().Count());

        var suppliers = await service.GetSupplierSalesRankAsync(
            fixture.DateRange, fixture.BranchCodes, 20, null, fixture.ProductStatisticStatus
        );
        Assert.True(suppliers.Count >= 8, "澳洲供应商排行不得以空数据冒充快速返回。");
        var australianRank = Assert.Single(suppliers, row => row.SupplierCode == fixture.AustralianSupplierCode);
        Assert.NotNull(australianRank.GrossProfit);
        Assert.NotNull(australianRank.CompareGrossProfit);
        var chinaSuppliers = await service.GetChinaSupplierSalesRankAsync(
            fixture.DateRange, fixture.BranchCodes, 20, null, fixture.ProductStatisticStatus
        );
        Assert.True(chinaSuppliers.Count >= 8, "中国供应商排行不得以空数据冒充快速返回。");
        var chinaRank = Assert.Single(chinaSuppliers, row => row.SupplierCode == fixture.ChinaSupplierCode);
        Assert.NotNull(chinaRank.GrossProfit);
        Assert.NotNull(chinaRank.CompareGrossProfit);
        var products = await service.GetEnhancedSalesProductDetailsAsync(
            fixture.DateRange,
            fixture.BranchCodes,
            localSupplierCodes: null,
            chinaSupplierCodes: null,
            pageIndex: 1,
            pageSize: 20,
            productSearch: null,
            statisticStatus: fixture.ProductStatisticStatus
        );
        Assert.Equal(24, products.Total);
        Assert.Equal(20, products.Data.Count);
        Assert.Null(Assert.Single(products.Data, row => row.ProductCode == fixture.ProductCode).GrossProfit);

        var australianBranches = await service.GetSupplierStoreSalesAsync(
            fixture.DateRange,
            new List<string> { fixture.AustralianSupplierCode },
            fixture.BranchCodes,
            fixture.ProductStatisticStatus
        );
        Assert.Equal(28, australianBranches.Count);
        var chinaBranches = await service.GetChinaSupplierStoreSalesAsync(
            fixture.DateRange,
            new List<string> { fixture.ChinaSupplierCode },
            fixture.BranchCodes,
            fixture.ProductStatisticStatus
        );
        Assert.Equal(28, chinaBranches.Count);
        var productBranches = await service.GetProductSalesByAllBranchesAsync(
            fixture.DateRange,
            fixture.ProductCode,
            fixture.BranchCodes,
            fixture.ProductStatisticStatus
        );
        Assert.Equal(28, productBranches.Count);
    }

    private async Task RunRevenueHomeJourneyAsync(TwentyEightStorePerformanceFixture fixture)
    {
        using var requestScope = CreateIsolatedServiceScope();
        var result = await requestScope.Service.GetExecutiveBranchPerformanceAsync(
            fixture.DateRange,
            branchCodes: fixture.BranchCodes
        );
        Assert.Equal(28, result.Items.Count);
        Assert.DoesNotContain(result.Items, row => row.BranchCode == "S99");
    }

    private async Task RunHourlyTrafficJourneyAsync(TwentyEightStorePerformanceFixture fixture)
    {
        using var requestScope = CreateIsolatedServiceScope();
        var result = await requestScope.Service.GetExecutiveHourlyTrafficAsync(
            fixture.DateRange,
            fixture.BranchCodes
        );
        Assert.Equal(28 * 14, result.Items.Count);
        Assert.Equal(14, result.Items.Select(row => row.Hour).Distinct().Count());
    }

    private async Task RunMonthlyDailyJourneyAsync(TwentyEightStorePerformanceFixture fixture)
    {
        using var requestScope = CreateIsolatedServiceScope();
        var result = await requestScope.Service.GetBranchDailyPerformanceAsync(
            fixture.DateRange,
            fixture.BranchCodes
        );
        Assert.Equal(28 * 31, result.Items.Count);
        Assert.Equal(31, result.Items.Select(row => row.Date).Distinct().Count());
    }

    private async Task RunProductFirstScreenJourneyAsync(TwentyEightStorePerformanceFixture fixture)
    {
        // 移动端首屏的三个请求各自拥有请求 scope/cache；Task.WhenAll 的墙钟才是用户可见等待时间。
        using var totalScope = CreateIsolatedServiceScope();
        using var supplierScope = CreateIsolatedServiceScope();
        using var productScope = CreateIsolatedServiceScope();
        var totalTask = totalScope.Service.GetExecutiveBranchPerformanceAsync(
            fixture.DateRange,
            branchCodes: fixture.BranchCodes
        );
        var supplierTask = supplierScope.Service.GetSupplierSalesRankAsync(
            fixture.DateRange,
            fixture.BranchCodes,
            20,
            null,
            fixture.ProductStatisticStatus
        );
        var productTask = productScope.Service.GetEnhancedSalesProductDetailsAsync(
            fixture.DateRange,
            fixture.BranchCodes,
            localSupplierCodes: null,
            chinaSupplierCodes: null,
            pageIndex: 1,
            pageSize: 20,
            productSearch: null,
            statisticStatus: fixture.ProductStatisticStatus
        );

        await Task.WhenAll(totalTask, supplierTask, productTask);
        Assert.Equal(28, totalTask.Result.Items.Count);
        Assert.True(supplierTask.Result.Count >= 8);
        Assert.Equal(24, productTask.Result.Total);
        Assert.Equal(20, productTask.Result.Data.Count);
    }

    private async Task RunAustralianSupplierDrilldownJourneyAsync(TwentyEightStorePerformanceFixture fixture)
    {
        using var requestScope = CreateIsolatedServiceScope();
        var result = await requestScope.Service.GetSupplierStoreSalesAsync(
            fixture.DateRange,
            new List<string> { fixture.AustralianSupplierCode },
            fixture.BranchCodes,
            fixture.ProductStatisticStatus
        );
        Assert.Equal(28, result.Count);
        Assert.DoesNotContain(result, row => row.BranchCode == "S99");
    }

    private async Task RunChinaSupplierDrilldownJourneyAsync(TwentyEightStorePerformanceFixture fixture)
    {
        using var requestScope = CreateIsolatedServiceScope();
        var result = await requestScope.Service.GetChinaSupplierStoreSalesAsync(
            fixture.DateRange,
            new List<string> { fixture.ChinaSupplierCode },
            fixture.BranchCodes,
            fixture.ProductStatisticStatus
        );
        Assert.Equal(28, result.Count);
        Assert.DoesNotContain(result, row => row.BranchCode == "S99");
    }

    private async Task RunProductBranchDrilldownJourneyAsync(TwentyEightStorePerformanceFixture fixture)
    {
        using var requestScope = CreateIsolatedServiceScope();
        var result = await requestScope.Service.GetProductSalesByAllBranchesAsync(
            fixture.DateRange,
            fixture.ProductCode,
            fixture.BranchCodes,
            fixture.ProductStatisticStatus
        );
        Assert.Equal(28, result.Count);
        Assert.All(result, row => Assert.Null(row.GrossProfit));
        Assert.DoesNotContain(result, row => row.BranchCode == "S99");
    }

    private async Task RunChinaSupplierRankJourneyAsync(TwentyEightStorePerformanceFixture fixture)
    {
        using var requestScope = CreateIsolatedServiceScope();
        var result = await requestScope.Service.GetChinaSupplierSalesRankAsync(
            fixture.DateRange,
            fixture.BranchCodes,
            20,
            null,
            fixture.ProductStatisticStatus
        );
        Assert.True(result.Count >= 8);
        var target = Assert.Single(result, row => row.SupplierCode == fixture.ChinaSupplierCode);
        Assert.NotNull(target.GrossProfit);
        Assert.NotNull(target.CompareGrossProfit);
    }

    private static async Task AssertThirtyColdSamplesUnderTwoSecondsAsync(
        string journeyName,
        Func<Task> journey
    )
    {
        // 预热仅消除 JIT/SQLite 初始化噪声；每个正式样本仍由旅程重新创建 service/cache。
        await journey();
        var elapsedMilliseconds = new List<long>(capacity: 30);
        for (var sample = 0; sample < 30; sample++)
        {
            var stopwatch = Stopwatch.StartNew();
            await journey();
            stopwatch.Stop();
            elapsedMilliseconds.Add(stopwatch.ElapsedMilliseconds);
        }

        Assert.Equal(30, elapsedMilliseconds.Count);
        var sorted = elapsedMilliseconds.OrderBy(value => value).ToList();
        var p50 = NearestRank(sorted, 0.50);
        var p95 = NearestRank(sorted, 0.95);
        var max = sorted[^1];
        Console.WriteLine(
            $"[报表性能] {journeyName}: samples=30, p50={p50}ms, p95={p95}ms, max={max}ms"
        );
        Assert.True(p95 < 2_000, $"{journeyName} P95 {p95}ms，超过 2000ms 回归门槛。");
        Assert.True(max < 2_000, $"{journeyName} 30/30 最大值 {max}ms，超过 2000ms 回归门槛。");
    }

    private static long NearestRank(IReadOnlyList<long> sortedSamples, double percentile)
    {
        Assert.NotEmpty(sortedSamples);
        var index = Math.Clamp((int)Math.Ceiling(sortedSamples.Count * percentile) - 1, 0, sortedSamples.Count - 1);
        return sortedSamples[index];
    }

    private static StoreSalesStatistic CreateStoreSalesStatistic(
        DateTime date,
        string branchCode,
        string branchName,
        decimal totalAmount,
        int orderCount
    ) => new()
    {
        Date = date,
        BranchCode = branchCode,
        BranchName = branchName,
        TotalAmount = totalAmount,
        OrderCount = orderCount,
        AverageOrderValue = totalAmount / orderCount,
        TotalQuantity = orderCount,
        CustomerCount = orderCount,
        UpdateTime = DateTime.UtcNow,
    };

    private static HourlySalesStatistic CreateHourlySalesStatistic(
        DateTime date,
        int hour,
        string branchCode,
        string branchName,
        decimal totalAmount,
        int orderCount
    ) => new()
    {
        Date = date,
        Hour = hour,
        BranchCode = branchCode,
        BranchName = branchName,
        TotalAmount = totalAmount,
        OrderCount = orderCount,
        AverageOrderValue = totalAmount / orderCount,
        TotalQuantity = orderCount,
        CustomerCount = orderCount,
        UpdateTime = DateTime.UtcNow,
    };

    private static ProductStoreDailySalesStatistic CreateProductDailyStatistic(
        DateTime date,
        string branchCode,
        string supplierCode,
        string productCode,
        string productName,
        decimal totalAmount,
        int totalQuantity,
        int orderCount,
        string barcode,
        decimal? totalCost,
        decimal? grossProfit
    ) => new()
    {
        Date = date,
        BranchCode = branchCode,
        SupplierCode = supplierCode,
        ProductCode = productCode,
        ProductName = productName,
        Barcode = barcode,
        TotalAmount = totalAmount,
        TotalQuantity = totalQuantity,
        OrderCount = orderCount,
        TotalCost = totalCost,
        GrossProfit = grossProfit,
        CostSource = "PerformanceFixture",
        UpdateTime = DateTime.UtcNow,
    };

    private static SalesStatisticRefreshState CreateFreshProductRefreshState(DateTime date) => new()
    {
        StatisticType = SalesStatisticType.ProductStoreDaily,
        Date = date,
        Status = SalesStatisticRefreshStatus.Fresh,
        LastAggregatedAtUtc = DateTime.UtcNow,
        CompletedAtUtc = DateTime.UtcNow,
    };

    private static SalesStatisticRefreshState CreateFreshStoreRefreshState(DateTime date) => new()
    {
        StatisticType = SalesStatisticType.StoreSales,
        Date = date,
        Status = SalesStatisticRefreshStatus.Fresh,
        LastAggregatedAtUtc = DateTime.UtcNow,
        CompletedAtUtc = DateTime.UtcNow,
    };

    private async Task InsertInBatchesAsync<T>(IEnumerable<T> rows) where T : class, new()
    {
        foreach (var batch in rows.Chunk(500))
        {
            await _localDb.Insertable(batch.ToList()).ExecuteCommandAsync();
        }
    }

    private async Task InsertPosmInBatchesAsync<T>(IEnumerable<T> rows) where T : class, new()
    {
        foreach (var batch in rows.Chunk(500))
        {
            await _posmDb.Insertable(batch.ToList()).ExecuteCommandAsync();
        }
    }

    private async Task InsertIndividuallyAsync<T>(IEnumerable<T> rows) where T : class, new()
    {
        foreach (var row in rows)
        {
            await _localDb.Insertable(row).ExecuteCommandAsync();
        }
    }

    private sealed record TwentyEightStorePerformanceFixture(
        DateRangeDto DateRange,
        List<string> BranchCodes,
        string AustralianSupplierCode,
        string ChinaSupplierCode,
        string ProductCode,
        ProductReportStatisticStatusDto ProductStatisticStatus
    );

    private async Task SeedStoreSalesStatisticAsync(
        DateTime date,
        string branchCode,
        string branchName,
        decimal totalAmount,
        int orderCount
    )
    {
        await _localDb.Insertable(new StoreSalesStatistic
        {
            Date = date,
            BranchCode = branchCode,
            BranchName = branchName,
            TotalAmount = totalAmount,
            OrderCount = orderCount,
            AverageOrderValue = orderCount > 0 ? totalAmount / orderCount : 0,
            TotalQuantity = orderCount,
            CustomerCount = orderCount,
            UpdateTime = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private async Task SeedStatisticsTaskLogAsync(
        string status,
        DateTime startedAtUtc,
        DateTime? completedAtUtc
    )
    {
        await _localDb.Insertable(new ScheduledTaskLog
        {
            TaskType = TaskType.UpdateCurrentHourStatistics,
            Status = status,
            StartedAt = startedAtUtc,
            CompletedAt = completedAtUtc,
            ScheduledTime = startedAtUtc,
            TriggeredBy = TaskTrigger.Scheduled,
        }).ExecuteCommandAsync();
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

    private async Task SeedHourlySalesStatisticAsync(
        DateTime date,
        int hour,
        string branchCode,
        string branchName,
        decimal totalAmount,
        int orderCount
    )
    {
        await _localDb.Insertable(new HourlySalesStatistic
        {
            Date = date,
            Hour = hour,
            BranchCode = branchCode,
            BranchName = branchName,
            TotalAmount = totalAmount,
            OrderCount = orderCount,
            AverageOrderValue = orderCount > 0 ? totalAmount / orderCount : 0,
            TotalQuantity = orderCount,
            CustomerCount = orderCount,
            UpdateTime = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private async Task SeedStoreAsync(string storeCode, string storeName)
    {
        await _localDb.Insertable(new Store
        {
            StoreGUID = $"store-{storeCode}",
            StoreCode = storeCode,
            StoreName = storeName,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedLocalSupplierAsync(string supplierCode, string supplierName)
    {
        await _localDb.Insertable(new HBLocalSupplier
        {
            Guid = $"local-{supplierCode}",
            LocalSupplierCode = supplierCode,
            Name = supplierName,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedChinaSupplierAsync(string supplierCode, string supplierName)
    {
        await _localDb.Insertable(new ChinaSupplier
        {
            Guid = $"china-{supplierCode}",
            SupplierCode = supplierCode,
            SupplierName = supplierName,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    [Fact]
    public async Task 毛利字段从商品分店日统计聚合且成本缺失或旧统计回退保持Null()
    {
        await SeedStoreAsync("S1", "分店一");
        await SeedStoreAsync("S2", "分店二");
        await SeedLocalSupplierAsync("AUS-GP", "澳洲毛利供应商");
        await SeedLocalSupplierAsync("AUS-LEGACY", "澳洲旧统计供应商");
        await SeedLocalSupplierAsync("AUS-MISSING", "澳洲缺成本供应商");
        await SeedLocalSupplierAsync("AUS-GROSS-MISSING", "澳洲缺毛利供应商");
        await SeedLocalSupplierAsync("AUS-MIXED", "澳洲混合成本供应商");
        await SeedChinaSupplierAsync("CN-GP", "中国毛利供应商");
        await SeedChinaSupplierAsync("CN-LEGACY", "中国旧统计供应商");
        await SeedProductAsync("P-GP", "HB-GP", "BAR-GP");
        await SeedProductAsync("P-MISSING", "HB-MISSING", "BAR-MISSING");
        await SeedProductAsync("P-GROSS-MISSING", "HB-GROSS-MISSING", "BAR-GROSS-MISSING");
        await SeedProductAsync("P-MIXED", "HB-MIXED", "BAR-MIXED");

        await SeedProductStoreDailySalesAsync(
            new DateTime(2026, 7, 1), "S1", "AUS-GP", "P-GP", "毛利商品", 100m, 10, 5,
            totalCost: 60m, grossProfit: 40m
        );
        await SeedProductStoreDailySalesAsync(
            new DateTime(2025, 7, 1), "S1", "AUS-GP", "P-GP", "毛利商品同期", 80m, 8, 4,
            totalCost: 50m, grossProfit: 30m
        );
        await SeedProductStoreDailySalesAsync(
            new DateTime(2026, 7, 1), "S1", "CN-GP", "P-CN-GP", "中国毛利商品", 50m, 5, 2,
            totalCost: 25m, grossProfit: 25m
        );
        await SeedProductStoreDailySalesAsync(
            new DateTime(2025, 7, 1), "S1", "CN-GP", "P-CN-GP", "中国毛利商品同期", 40m, 4, 2,
            totalCost: 20m, grossProfit: 20m
        );
        await SeedProductStoreDailySalesAsync(
            new DateTime(2026, 7, 1), "S1", "AUS-MISSING", "P-MISSING", "缺成本商品", 60m, 6, 3
        );
        await SeedProductStoreDailySalesAsync(
            new DateTime(2026, 7, 1), "S1", "AUS-GROSS-MISSING", "P-GROSS-MISSING", "缺毛利商品", 60m, 6, 3,
            totalCost: 40m
        );
        await SeedProductStoreDailySalesAsync(
            new DateTime(2026, 7, 1), "S1", "AUS-MIXED", "P-MIXED", "混合成本商品", 30m, 3, 1,
            totalCost: 15m, grossProfit: 15m
        );
        await SeedProductStoreDailySalesAsync(
            new DateTime(2026, 7, 1), "S2", "AUS-MIXED", "P-MIXED", "混合成本商品", 20m, 2, 1
        );
        await SeedAustralianSupplierSalesAsync(
            new DateTime(2026, 7, 1), "S1", "AUS-LEGACY", "澳洲旧统计供应商", 20m, 2, 1
        );
        await SeedChinaSupplierSalesAsync(
            new DateTime(2026, 7, 1), "S1", "CN-LEGACY", "中国旧统计供应商", 20m, 2, 1
        );

        var dateRange = new DateRangeDto
        {
            StartDate = new DateTime(2026, 7, 1),
            EndDate = new DateTime(2026, 7, 1),
            CompareStartDate = new DateTime(2025, 7, 1),
            CompareEndDate = new DateTime(2025, 7, 1),
        };
        var service = CreateService();

        var australiaRankRows = await service.GetSupplierSalesRankAsync(dateRange);
        var australiaRank = Assert.Single(australiaRankRows, row => row.SupplierCode == "AUS-GP");
        Assert.Equal(40m, australiaRank.GrossProfit);
        Assert.Equal(0.4m, australiaRank.GrossMarginRate);
        Assert.Equal(30m, australiaRank.CompareGrossProfit);
        Assert.Equal(0.375m, australiaRank.CompareGrossMarginRate);
        var mixedAustraliaRank = Assert.Single(australiaRankRows, row => row.SupplierCode == "AUS-MIXED");
        Assert.Null(mixedAustraliaRank.GrossProfit);
        Assert.Null(mixedAustraliaRank.GrossMarginRate);
        var missingGrossProfitRank = Assert.Single(
            australiaRankRows,
            row => row.SupplierCode == "AUS-GROSS-MISSING"
        );
        Assert.Null(missingGrossProfitRank.GrossProfit);
        Assert.Null(missingGrossProfitRank.GrossMarginRate);

        var chinaRankRows = await service.GetChinaSupplierSalesRankAsync(dateRange);
        var chinaRank = Assert.Single(chinaRankRows, row => row.SupplierCode == "CN-GP");
        Assert.Equal(25m, chinaRank.GrossProfit);
        Assert.Equal(0.5m, chinaRank.GrossMarginRate);
        Assert.Equal(20m, chinaRank.CompareGrossProfit);
        Assert.Equal(0.5m, chinaRank.CompareGrossMarginRate);

        var australiaStore = Assert.Single(
            await service.GetSupplierStoreSalesAsync(dateRange, new List<string> { "AUS-GP" })
        );
        Assert.Equal(40m, australiaStore.GrossProfit);
        Assert.Equal(30m, australiaStore.CompareGrossProfit);

        var chinaStore = Assert.Single(
            await service.GetChinaSupplierStoreSalesAsync(dateRange, new List<string> { "CN-GP" })
        );
        Assert.Equal(25m, chinaStore.GrossProfit);
        Assert.Equal(20m, chinaStore.CompareGrossProfit);

        var products = await service.GetEnhancedSalesProductDetailsAsync(dateRange);
        var product = Assert.Single(products.Data, row => row.ProductCode == "P-GP");
        Assert.Equal(40m, product.GrossProfit);
        Assert.Equal(0.4m, product.GrossMarginRate);
        Assert.Equal(30m, product.GrossProfitLY);
        Assert.Equal(0.375m, product.GrossMarginRateLY);
        var missingCostProduct = Assert.Single(products.Data, row => row.ProductCode == "P-MISSING");
        Assert.Null(missingCostProduct.GrossProfit);
        Assert.Null(missingCostProduct.GrossMarginRate);
        var missingGrossProfitProduct = Assert.Single(
            products.Data,
            row => row.ProductCode == "P-GROSS-MISSING"
        );
        Assert.Null(missingGrossProfitProduct.GrossProfit);
        Assert.Null(missingGrossProfitProduct.GrossMarginRate);
        var mixedCostProduct = Assert.Single(products.Data, row => row.ProductCode == "P-MIXED");
        Assert.Null(mixedCostProduct.GrossProfit);
        Assert.Null(mixedCostProduct.GrossMarginRate);

        var branches = await service.GetProductSalesByAllBranchesAsync(dateRange, "P-GP");
        var branch = Assert.Single(branches);
        Assert.Equal(40m, branch.GrossProfit);
        Assert.Equal(0.4m, branch.GrossMarginRate);
        Assert.Equal(30m, branch.CompareGrossProfit);
        Assert.Equal(0.375m, branch.CompareGrossMarginRate);

        var legacyAustralia = Assert.Single(
            await service.GetSupplierStoreSalesAsync(dateRange, new List<string> { "AUS-LEGACY" })
        );
        Assert.Null(legacyAustralia.GrossProfit);
        var legacyChina = Assert.Single(
            await service.GetChinaSupplierStoreSalesAsync(dateRange, new List<string> { "CN-LEGACY" })
        );
        Assert.Null(legacyChina.GrossProfit);
    }

    private async Task SeedProductStoreDailySalesAsync(
        DateTime date,
        string branchCode,
        string supplierCode,
        string productCode,
        string productName,
        decimal totalAmount,
        int totalQuantity,
        int orderCount,
        string? barcode = null,
        decimal? totalCost = null,
        decimal? grossProfit = null
    )
    {
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = date.Date,
            BranchCode = branchCode,
            SupplierCode = supplierCode,
            ProductCode = productCode,
            ProductName = productName,
            Barcode = barcode,
            TotalAmount = totalAmount,
            TotalQuantity = totalQuantity,
            OrderCount = orderCount,
            TotalCost = totalCost,
            GrossProfit = grossProfit,
            CostSource = "Test",
            UpdateTime = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        await EnsureProductStatisticRefreshStateAsync(date);
    }

    private async Task SeedAustralianSupplierSalesAsync(
        DateTime date,
        string branchCode,
        string supplierCode,
        string supplierName,
        decimal totalAmount,
        int totalQuantity,
        int orderCount
    )
    {
        await _localDb.Insertable(new AustralianSupplierStoreSalesDetail
        {
            Date = date,
            BranchCode = branchCode,
            SupplierCode = supplierCode,
            SupplierName = supplierName,
            TotalAmount = totalAmount,
            TotalQuantity = totalQuantity,
            OrderCount = orderCount,
            UpdateTime = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await EnsureProductStatisticRefreshStateAsync(date);
    }

    private async Task SeedChinaSupplierSalesAsync(
        DateTime date,
        string branchCode,
        string supplierCode,
        string supplierName,
        decimal totalAmount,
        int totalQuantity,
        int orderCount
    )
    {
        await _localDb.Insertable(new ChinaSupplierStoreSalesDetail
        {
            Date = date,
            BranchCode = branchCode,
            SupplierCode = supplierCode,
            SupplierName = supplierName,
            TotalAmount = totalAmount,
            TotalQuantity = totalQuantity,
            OrderCount = orderCount,
            UpdateTime = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await EnsureProductStatisticRefreshStateAsync(date);
    }

    private async Task EnsureProductStatisticRefreshStateAsync(DateTime date)
    {
        var day = date.Date;
        if (!_seededProductStatisticDates.Add(day))
            return;

        var hasRefreshState = await _localDb.Queryable<SalesStatisticRefreshState>()
            .AnyAsync(state =>
                state.StatisticType == SalesStatisticType.ProductStoreDaily
                && state.Date >= day
                && state.Date < day.AddDays(1)
            );
        if (hasRefreshState)
            return;

        // 测试中的统计行代表已完成的统计产物，默认同时写入 Fresh 水位。
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = day,
            Status = SalesStatisticRefreshStatus.Fresh,
            LastAggregatedAtUtc = DateTime.UtcNow,
            CompletedAtUtc = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private async Task SeedStoreSupplierSalesAsync(
        DateTime date,
        string branchCode,
        string supplierCode,
        string supplierName,
        decimal totalAmount,
        int totalQuantity,
        int orderCount
    )
    {
        await _localDb.Insertable(new StoreSupplierSalesDetail
        {
            Date = date,
            BranchCode = branchCode,
            SupplierCode = supplierCode,
            SupplierName = supplierName,
            TotalAmount = totalAmount,
            TotalQuantity = totalQuantity,
            OrderCount = orderCount,
            UpdateTime = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private async Task SeedSalesOrderAsync(
        string orderGuid,
        DateTime orderTime,
        string? branchCode,
        string? deviceCode = null
    )
    {
        await _posmDb.Insertable(new SalesOrder
        {
            OrderGuid = orderGuid,
            OrderTime = orderTime,
            BranchCode = branchCode,
            DeviceCode = deviceCode,
            Status = 1,
            ActualAmount = 0m,
            TotalAmount = 0m,
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

    private async Task SeedSalesOrderDetailAsync(
        string detailGuid,
        string orderGuid,
        string productCode,
        int quantity,
        decimal actualAmount,
        decimal discountAmount,
        string? barcode = null,
        string? productName = null
    )
    {
        await _posmDb.Insertable(new SalesOrderDetail
        {
            OrderDetailGuid = detailGuid,
            OrderGuid = orderGuid,
            ProductCode = productCode,
            ProductName = productName ?? productCode,
            Barcode = barcode,
            Quantity = quantity,
            ActualAmount = actualAmount,
            DiscountAmount = discountAmount,
            Subtotal = actualAmount,
        }).ExecuteCommandAsync();
    }

    private async Task SeedPaymentDetailAsync(string paymentGuid, string orderGuid, decimal amount)
    {
        await _posmDb.Insertable(new PaymentDetail
        {
            PaymentGuid = paymentGuid,
            OrderGuid = orderGuid,
            Amount = amount,
            PaymentMethod = 1,
            CreatedTime = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private async Task SeedPosmOrderWithPaymentAsync(
        string orderGuid,
        DateTime orderTime,
        string branchCode,
        decimal amount,
        int quantity
    )
    {
        await SeedSalesOrderAsync(orderGuid, orderTime, branchCode);
        await SeedSalesOrderDetailAsync(
            $"detail-{orderGuid}",
            orderGuid,
            $"product-{orderGuid}",
            quantity,
            amount,
            0m
        );
        await SeedPaymentDetailAsync($"payment-{orderGuid}", orderGuid, amount);
    }

    private async Task SeedHbSalesOrderAsync(
        string orderNumber,
        DateTime checkoutAt,
        string branchCode,
        decimal amount,
        decimal quantity
    )
    {
        var id = Interlocked.Increment(ref _nextHbSalesRecordId);
        await _hbSalesDb.Insertable(new SalesOrderMain
        {
            ID = id,
            B分店代码 = branchCode,
            B销售单号 = orderNumber,
            B单据类型 = "1",
            B结账日期 = checkoutAt.Date,
            B结账时间 = checkoutAt.TimeOfDay,
            B合计金额 = amount,
        }).ExecuteCommandAsync();
        await _hbSalesDb.Insertable(new SalesOrderDetailRecord
        {
            ID = id,
            B分店代码 = branchCode,
            B分店代码_ID = branchCode,
            B销售单号 = orderNumber,
            B结账日期 = checkoutAt.Date,
            B结账时间 = checkoutAt.TimeOfDay,
            B产品编号 = $"product-{orderNumber}",
            B数量 = quantity,
            B合计金额 = amount,
        }).ExecuteCommandAsync();
    }

    private async Task SeedProductAsync(string productCode, string itemNumber, string barcode)
    {
        await _localDb.Insertable(new Product
        {
            UUID = productCode,
            ProductCode = productCode,
            ItemNumber = itemNumber,
            Barcode = barcode,
            ProductName = itemNumber,
        }).ExecuteCommandAsync();
    }

    private async Task SeedProductsAsync(IEnumerable<(string ProductCode, string ItemNumber, string Barcode)> rows)
    {
        var products = rows
            .Select(row => new Product
            {
                UUID = row.ProductCode,
                ProductCode = row.ProductCode,
                ItemNumber = row.ItemNumber,
                Barcode = row.Barcode,
                ProductName = row.ItemNumber,
            })
            .ToList();

        if (products.Any())
        {
            await _localDb.Insertable(products).ExecuteCommandAsync();
        }
    }

    private async Task SeedSupplierMappingAsync(string productCode, string localSupplierCode, string chinaSupplierCode)
    {
        await _posmDb.Insertable(new PosmProductSupplierMapping
        {
            ProductCode = productCode,
            LocalSupplierCode = localSupplierCode,
            ChinaSupplierCode = chinaSupplierCode,
            LastUpdateTime = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private async Task SeedSupplierMappingsAsync(
        IEnumerable<(string ProductCode, string LocalSupplierCode, string ChinaSupplierCode)> rows
    )
    {
        var entities = rows
            .Select(row => new PosmProductSupplierMapping
            {
                ProductCode = row.ProductCode,
                LocalSupplierCode = row.LocalSupplierCode,
                ChinaSupplierCode = row.ChinaSupplierCode,
                LastUpdateTime = DateTime.UtcNow,
            })
            .ToList();
        await _posmDb.Insertable(entities).ExecuteCommandAsync();
    }

    /// <summary>
    /// 商品明细排序夹具：P-SORT-A..I 共 9 个商品，本期 2026-07-01、同期 2025-07-01。
    /// A、C 本期和 G 同期拆在 S1/S2 两店以覆盖 SUM；金额全部是整数，整数除法或 TEXT 比较都会打乱均价顺序。
    /// 另有其它供应商的高额干扰商品，筛选失效时会排到金额降序首位并把 Total 变成 10。
    /// </summary>
    private async Task SeedProductSortFixtureAsync(string supplierCode)
    {
        var current = new DateTime(2026, 7, 1);
        var compare = new DateTime(2025, 7, 1);
        foreach (var letter in "ABCDEFGHI")
        {
            await SeedProductAsync($"P-SORT-{letter}", $"HB-SORT-{letter}", $"BAR-SORT-{letter}");
        }

        Task SeedAsync(DateTime date, string branchCode, char letter, decimal amount, int quantity) =>
            SeedProductStoreDailySalesAsync(
                date,
                branchCode,
                supplierCode,
                $"P-SORT-{letter}",
                $"排序商品{letter}",
                amount,
                quantity,
                1,
                barcode: $"BAR-SORT-{letter}"
            );

        await SeedAsync(current, "S1", 'A', 60m, 6);
        await SeedAsync(current, "S2", 'A', 40m, 4);
        await SeedAsync(current, "S1", 'B', 90m, 30);
        await SeedAsync(current, "S1", 'C', 20m, 3);
        await SeedAsync(current, "S2", 'C', 27m, 3);
        await SeedAsync(current, "S1", 'D', 39m, 5);
        await SeedAsync(current, "S2", 'E', 39m, 5);
        await SeedAsync(current, "S1", 'F', 47m, 6);
        await SeedAsync(current, "S1", 'H', 10m, 2);
        await SeedAsync(current, "S2", 'I', 10m, 2);
        await SeedAsync(compare, "S1", 'A', 50m, 5);
        await SeedAsync(compare, "S2", 'C', 20m, 2);
        await SeedAsync(compare, "S1", 'D', 36m, 3);
        await SeedAsync(compare, "S1", 'G', 50m, 2);
        await SeedAsync(compare, "S2", 'G', 30m, 2);
        await SeedProductStoreDailySalesAsync(current, "S1", "OTHER-SORT", "P-SORT-NOISE", "其它供应商干扰商品", 999m, 1, 1);
        await SeedProductStoreDailySalesAsync(compare, "S1", "OTHER-SORT", "P-SORT-NOISE", "其它供应商干扰商品", 999m, 1, 1);
    }

    /// <summary>排序夹具每个商品两店合计后的本期金额/数量与同期金额/数量。</summary>
    private static readonly IReadOnlyDictionary<char, (decimal Amount, int Quantity, decimal AmountLY, int QuantityLY)>
        ProductSortExpectedValues = new Dictionary<char, (decimal, int, decimal, int)>
        {
            ['A'] = (100m, 10, 50m, 5),
            ['B'] = (90m, 30, 0m, 0),
            ['C'] = (47m, 6, 20m, 2),
            ['D'] = (39m, 5, 36m, 3),
            ['E'] = (39m, 5, 0m, 0),
            ['F'] = (47m, 6, 0m, 0),
            ['G'] = (0m, 0, 80m, 4),
            ['H'] = (10m, 2, 0m, 0),
            ['I'] = (10m, 2, 0m, 0),
        };

    private static DateRangeDto CreateProductSortDateRange() => new()
    {
        StartDate = new DateTime(2026, 7, 1),
        EndDate = new DateTime(2026, 7, 1),
        CompareStartDate = new DateTime(2025, 7, 1),
        CompareEndDate = new DateTime(2025, 7, 1),
    };

    private static string ToProductSortLetters(IEnumerable<SalesProductDetailWithDiscountDto> rows) =>
        string.Concat(rows.Select(row => row.ProductCode.StartsWith("P-SORT-", StringComparison.Ordinal)
            ? row.ProductCode["P-SORT-".Length..]
            : $"[{row.ProductCode}]"));

    /// <summary>
    /// 三页（每页 4 条）拼接后必须恰好是期望顺序：无重复、无遗漏、Total 恒为 9，且每行金额数量按两店合计。
    /// </summary>
    private static void AssertProductSortPages(
        IReadOnlyList<PagedSalesProductDetailWithDiscountDto> pages,
        string expectedOrder
    )
    {
        var rows = pages.SelectMany(page => page.Data).ToList();
        Assert.Equal(expectedOrder, ToProductSortLetters(rows));
        Assert.All(pages, page => Assert.Equal(9, page.Total));
        Assert.Equal(new[] { 4, 4, 1 }, pages.Select(page => page.Data.Count));
        Assert.Equal(rows.Count, rows.Select(row => row.ProductCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var row in rows)
        {
            var letter = row.ProductCode[^1];
            var expected = ProductSortExpectedValues[letter];
            Assert.Equal($"HB-SORT-{letter}", row.ItemNumber);
            Assert.Equal(expected.Amount, row.SalesAmount);
            Assert.Equal(expected.Quantity, row.Quantity);
            Assert.Equal(expected.AmountLY, row.SalesAmountLY);
            Assert.Equal(expected.QuantityLY, row.QuantityLY);
        }
    }

    /// <summary>
    /// 线程安全的日志记录器。SalesDashboardCacheKeys 的日志器是静态的，并行测试类可能同时写入。
    /// </summary>
    private class ConcurrentRecordingLogger : ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            _messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class ConcurrentRecordingLogger<T> : ConcurrentRecordingLogger, ILogger<T>
    {
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private static async Task<string> InvokeStatisticsCacheVersionAsync(
        SalesDashboardReactService service,
        DateRangeDto dateRange
    )
    {
        var method = typeof(SalesDashboardReactService).GetMethod(
            "GetStatisticsCacheVersionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);

        var task = method!.Invoke(service, new object[] { dateRange }) as Task<string>;
        Assert.NotNull(task);
        return await task!;
    }

    private SalesDashboardReactService CreateService(bool useSupplierRollups = false, IMemoryCache? cache = null)
    {
        var localContext = CreateSqlSugarContext(_localDb);
        var posmContext = CreatePosmSqlSugarContext(_posmDb);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Reports:UseSupplierRollups"] = useSupplierRollups ? "true" : "false",
            })
            .Build();
        var services = new ServiceCollection()
            .AddSingleton(localContext)
            .AddSingleton(posmContext)
            .AddSingleton(new HBSalesRecordSqlSugarContext(_hbSalesDb))
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<ILogger<SalesStatisticsJobService>>(
                NullLogger<SalesStatisticsJobService>.Instance
            )
            .AddScoped<SalesStatisticsJobService>()
            .BuildServiceProvider();

        return new SalesDashboardReactService(
            localContext,
            posmContext,
            Mock.Of<IMapper>(),
            NullLogger<SalesDashboardReactService>.Instance,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            services.GetRequiredService<IServiceScopeFactory>(),
            configuration
        );
    }

    private IsolatedReportServiceScope CreateIsolatedServiceScope(IMemoryCache? cache = null)
    {
        // 并行首屏的每个请求都要有独立 SqliteConnection、SqlSugar client 和 MemoryCache，
        // 避免共享测试夹具连接产生线程安全假象。
        var localConnection = new SqliteConnection($"Data Source={_localDbPath};Mode=ReadWrite");
        var posmConnection = new SqliteConnection($"Data Source={_posmDbPath};Mode=ReadWrite");
        var hbSalesConnection = new SqliteConnection($"Data Source={_hbSalesDbPath};Mode=ReadWrite");
        localConnection.Open();
        posmConnection.Open();
        hbSalesConnection.Open();

        var localDb = new SqlSugarClient(CreateConnectionConfig(localConnection.ConnectionString));
        var posmDb = new SqlSugarClient(CreateConnectionConfig(posmConnection.ConnectionString));
        var hbSalesDb = new SqlSugarScope(CreateConnectionConfig(hbSalesConnection.ConnectionString));
        var localContext = CreateSqlSugarContext(localDb);
        var posmContext = CreatePosmSqlSugarContext(posmDb);
        var services = new ServiceCollection()
            .AddSingleton(localContext)
            .AddSingleton(posmContext)
            .AddSingleton(new HBSalesRecordSqlSugarContext(hbSalesDb))
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<ILogger<SalesStatisticsJobService>>(
                NullLogger<SalesStatisticsJobService>.Instance
            )
            .AddScoped<SalesStatisticsJobService>()
            .BuildServiceProvider();
        var service = new SalesDashboardReactService(
            localContext,
            posmContext,
            Mock.Of<IMapper>(),
            NullLogger<SalesDashboardReactService>.Instance,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            services.GetRequiredService<IServiceScopeFactory>()
        );
        return new IsolatedReportServiceScope(
            service,
            services,
            localConnection,
            posmConnection,
            hbSalesConnection
        );
    }

    private sealed class IsolatedReportServiceScope : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly SqliteConnection _localConnection;
        private readonly SqliteConnection _posmConnection;
        private readonly SqliteConnection _hbSalesConnection;

        public IsolatedReportServiceScope(
            SalesDashboardReactService service,
            ServiceProvider services,
            SqliteConnection localConnection,
            SqliteConnection posmConnection,
            SqliteConnection hbSalesConnection
        )
        {
            Service = service;
            _services = services;
            _localConnection = localConnection;
            _posmConnection = posmConnection;
            _hbSalesConnection = hbSalesConnection;
        }

        public SalesDashboardReactService Service { get; }

        public void Dispose()
        {
            _services.Dispose();
            _localConnection.Dispose();
            _posmConnection.Dispose();
            _hbSalesConnection.Dispose();
        }
    }

    [Fact]
    public async Task SalesDetailColumns_供应商栏保留候选并传递授权分店范围()
    {
        List<string>? capturedBranches = null;
        string? capturedBranch = null;
        var service = new Mock<ISalesDashboardReactService>();
        service.Setup(item => item.GetProductReportStatisticStatusAsync(It.IsAny<DateRangeDto>()))
            .ReturnsAsync(new ProductReportStatisticStatusDto
            {
                StatisticStatus = SalesStatisticRefreshStatus.Fresh,
                CacheVersion = "detail-v1",
            });
        service.Setup(item => item.GetSalesDetailColumnsAsync(
                It.IsAny<DateRangeDto>(), SalesDetailKind.Australia, SalesDetailSection.Suppliers,
                It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), 1, 20, It.IsAny<ProductReportStatisticStatusDto?>(), It.IsAny<CancellationToken>()
            ))
            .Callback<DateRangeDto, SalesDetailKind, SalesDetailSection, List<string>?, string?, string?, string?, string?, int, int, ProductReportStatisticStatusDto?, CancellationToken>(
                (_, _, _, branches, branch, _, _, _, _, _, _, _) =>
                {
                    capturedBranches = branches;
                    capturedBranch = branch;
                })
            .ReturnsAsync(new SalesDetailSectionResultDto
            {
                Rows = new List<SalesDetailRowDto> { new() { Code = "AU1", Name = "Supplier", Revenue = 12m } },
                Total = 1,
            });
        var controller = CreateSalesDetailColumnsController(service.Object, CreateUserService(new[] { "S1", "S2" }));

        var response = await controller.GetSalesDetailColumns(
            SalesDetailKind.Australia,
            new DateTime(2026, 9, 1),
            new DateTime(2026, 9, 6),
            branchCodes: new List<string> { "S1", "S2", "S9" },
            selectedBranchCode: "S1",
            section: "suppliers"
        );

        var envelope = AssertOk(response).Value!;
        var data = (SalesDetailSectionResultDto)envelope.GetType().GetProperty("Data")!.GetValue(envelope)!;
        Assert.Single(data.Rows);
        Assert.Equal(new[] { "S1", "S2" }, capturedBranches);
        Assert.Equal("S1", capturedBranch);
    }

    [Fact]
    public async Task SalesDetailColumns_未授权分店和未Fresh不执行聚合()
    {
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        service.Setup(item => item.GetProductReportStatisticStatusAsync(It.IsAny<DateRangeDto>()))
            .ReturnsAsync(new ProductReportStatisticStatusDto
            {
                StatisticStatus = SalesStatisticRefreshStatus.Pending,
                StatisticMessage = "pending",
                CacheVersion = "pending-v1",
            });
        var controller = CreateSalesDetailColumnsController(service.Object, CreateUserService(new[] { "S1" }));

        var denied = await controller.GetSalesDetailColumns(
            SalesDetailKind.Australia,
            new DateTime(2026, 9, 1),
            new DateTime(2026, 9, 6),
            selectedBranchCode: "S2",
            section: "summary"
        );
        var deniedValue = AssertOk(denied).Value!;
        var deniedData = (SalesDetailSectionResultDto)deniedValue.GetType().GetProperty("Data")!.GetValue(deniedValue)!;
        Assert.Empty(deniedData.Rows);
        service.Verify(item => item.GetProductReportStatisticStatusAsync(It.IsAny<DateRangeDto>()), Times.Never);

        var pending = await controller.GetSalesDetailColumns(
            SalesDetailKind.Australia,
            new DateTime(2026, 9, 1),
            new DateTime(2026, 9, 6),
            selectedBranchCode: "S1",
            section: "summary"
        );
        var pendingValue = AssertOk(pending).Value!;
        Assert.Equal(SalesStatisticRefreshStatus.Pending, GetStringProperty(pendingValue, "StatisticStatus"));
        service.Verify(item => item.GetSalesDetailColumnsAsync(
            It.IsAny<DateRangeDto>(), It.IsAny<SalesDetailKind>(), It.IsAny<SalesDetailSection>(),
            It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ProductReportStatisticStatusDto?>(), It.IsAny<CancellationToken>()
        ), Times.Never);
    }

    [Fact]
    public async Task SalesDetailColumns_取消请求在状态读取前终止()
    {
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        var controller = CreateSalesDetailColumnsController(service.Object, CreateUserService(new[] { "S1" }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.GetSalesDetailColumns(
            SalesDetailKind.Australia,
            new DateTime(2026, 9, 1),
            new DateTime(2026, 9, 6),
            section: "summary",
            cancellationToken: cancellation.Token
        ));
    }

    [Fact]
    public void SalesDetailColumns_沿用商品移动报表权限策略()
    {
        var authorize = typeof(SalesDetailColumnsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        Assert.Equal(Permissions.Reports.ProductMovementView, authorize.Policy);
    }

    [Fact]
    public async Task SalesDetailColumns_拒绝非法枚举和超长不成对日期()
    {
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        var controller = CreateSalesDetailColumnsController(service.Object, CreateUserService(new[] { "S1" }));

        var invalidKind = await controller.GetSalesDetailColumns(
            (SalesDetailKind)99, new DateTime(2026, 9, 1), new DateTime(2026, 9, 1)
        );
        Assert.IsType<BadRequestObjectResult>(invalidKind);

        var tooLong = await controller.GetSalesDetailColumns(
            SalesDetailKind.Australia, new DateTime(2025, 1, 1), new DateTime(2026, 1, 2)
        );
        Assert.IsType<BadRequestObjectResult>(tooLong);

        var unpairedCompare = await controller.GetSalesDetailColumns(
            SalesDetailKind.Australia, new DateTime(2026, 9, 1), new DateTime(2026, 9, 6),
            compareStartDate: new DateTime(2026, 8, 1)
        );
        Assert.IsType<BadRequestObjectResult>(unpairedCompare);

        var invalidSection = await controller.GetSalesDetailColumns(
            SalesDetailKind.Australia, new DateTime(2026, 9, 1), new DateTime(2026, 9, 1),
            section: "999"
        );
        Assert.IsType<BadRequestObjectResult>(invalidSection);

        var negativeSection = await controller.GetSalesDetailColumns(
            SalesDetailKind.Australia, new DateTime(2026, 9, 1), new DateTime(2026, 9, 1),
            section: "-1"
        );
        Assert.IsType<BadRequestObjectResult>(negativeSection);
    }

    [Fact]
    public async Task SalesDetailColumns_商品关键词支持供应商词且分页汇总不丢失()
    {
        var currentDate = new DateTime(2026, 7, 1);
        var compareDate = new DateTime(2025, 7, 1);
        await SeedStoreAsync("S1", "分店一");
        await SeedLocalSupplierAsync("AUS-HB", "Hot Bargain");
        await SeedProductAsync("P-HB-1", "HB-WOVEN-1", "9300000000001");
        await _localDb.Updateable<Product>()
            .SetColumns(product => product.LocalSupplierCode == "AUS-HB")
            .Where(product => product.ProductCode == "P-HB-1")
            .ExecuteCommandAsync();
        await SeedProductStoreDailySalesAsync(currentDate, "S1", "AUS-HB", "P-HB-1", "Woven Bag", 20m, 4, 2,
            barcode: "9300000000001", totalCost: 12m, grossProfit: 8m);
        await SeedProductStoreDailySalesAsync(compareDate, "S1", "AUS-HB", "P-HB-1", "Woven Bag", 10m, 2, 1,
            barcode: "9300000000001", totalCost: 6m, grossProfit: 4m);

        var result = await CreateService().GetSalesDetailColumnsAsync(
            new DateRangeDto
            {
                StartDate = currentDate,
                EndDate = currentDate,
                CompareStartDate = compareDate,
                CompareEndDate = compareDate,
            },
            SalesDetailKind.Australia,
            SalesDetailSection.Products,
            branchCodes: new List<string> { "S1" },
            selectedBranchCode: "S1",
            search: "9300000000001 Hot Bargain",
            pageIndex: 1,
            pageSize: 1
        );

        var row = Assert.Single(result.Rows);
        Assert.Equal("P-HB-1", row.Code);
        Assert.Equal(20m, row.Revenue);
        Assert.Equal(10m, row.CompareRevenue);
        Assert.Equal(5m, row.AverageUnitPrice);
        Assert.Equal(5m, row.CompareAverageUnitPrice);
        Assert.Equal(8m, row.GrossProfit);
        Assert.Equal(0.4m, row.GrossMarginRate);
        Assert.Equal(1, result.Total);
        Assert.Equal(20m, result.Summary!.Revenue);
    }

    [Fact]
    public async Task SalesDetailColumns_澳洲口径归并直接中国编码且商品分页按数量降序()
    {
        var date = new DateTime(2026, 7, 1);
        await SeedStoreAsync("S1", "分店一");
        await SeedLocalSupplierAsync("AUS-ONE", "澳洲供应商");
        await SeedChinaSupplierAsync("CN-ONE", "中国供应商");
        await _localDb.Updateable<ChinaSupplier>()
            .SetColumns(row => row.IsDeleted == true)
            .Where(row => row.SupplierCode == "CN-ONE")
            .ExecuteCommandAsync();
        await SeedSupplierMappingAsync("P-CN-LEGACY", "200", "CN-ONE");
        await SeedProductStoreDailySalesAsync(date, "S1", "AUS-ONE", "P-AU", "澳洲商品", 60m, 6, 3);
        await SeedProductStoreDailySalesAsync(date, "S1", "200", "P-CN-LEGACY", "国内旧编码", 20m, 2, 1);
        await SeedProductStoreDailySalesAsync(date, "S1", "CN-ONE", "P-CN-DIRECT", "国内直接编码", 20m, 20, 1);
        var range = new DateRangeDto { StartDate = date, EndDate = date };
        var service = CreateService();

        var australia = await service.GetSalesDetailColumnsAsync(
            range, SalesDetailKind.Australia, SalesDetailSection.Suppliers,
            branchCodes: new List<string> { "S1" }, selectedBranchCode: "S1"
        );
        var domesticAustraliaRow = Assert.Single(australia.Rows, row => row.Code == "200");
        Assert.Equal(40m, domesticAustraliaRow.Revenue);
        Assert.Equal(22, domesticAustraliaRow.Quantity);
        Assert.Equal(40m / 22m, domesticAustraliaRow.AverageUnitPrice);
        Assert.Equal(0.4m, domesticAustraliaRow.Share);
        Assert.Equal(100m, australia.Summary!.Revenue);

        var china = await service.GetSalesDetailColumnsAsync(
            range, SalesDetailKind.China, SalesDetailSection.Suppliers,
            branchCodes: new List<string> { "S1" }, selectedBranchCode: "S1"
        );
        var domesticChinaRow = Assert.Single(china.Rows, row => row.Code == "CN-ONE");
        Assert.Equal(40m, domesticChinaRow.Revenue);
        Assert.Equal("中国供应商", domesticChinaRow.Name);
        Assert.Equal(22, domesticChinaRow.Quantity);
        Assert.Equal(40m / 22m, domesticChinaRow.AverageUnitPrice);
        Assert.Equal(1m, domesticChinaRow.Share);
        Assert.Equal(0.4m, domesticChinaRow.ChinaShare);

        var branches = await service.GetSalesDetailColumnsAsync(
            range, SalesDetailKind.Australia, SalesDetailSection.Branches,
            branchCodes: new List<string> { "S1" }, selectedBranchCode: "S1"
        );
        var branch = Assert.Single(branches.Rows);
        Assert.Equal(28, branch.Quantity);
        Assert.Equal(100m / 28m, branch.AverageUnitPrice);

        var products = await service.GetSalesDetailColumnsAsync(
            range, SalesDetailKind.Australia, SalesDetailSection.Products,
            branchCodes: new List<string> { "S1" }, selectedBranchCode: "S1", pageSize: 10
        );
        Assert.Contains(products.Rows, row => row.Code == "P-CN-DIRECT" && row.Revenue == 20m);

        var topProductPage = await service.GetSalesDetailColumnsAsync(
            range, SalesDetailKind.Australia, SalesDetailSection.Products,
            branchCodes: new List<string> { "S1" }, selectedBranchCode: "S1", pageIndex: 1, pageSize: 1
        );
        var topProduct = Assert.Single(topProductPage.Rows);
        Assert.Equal("P-CN-DIRECT", topProduct.Code);
        Assert.Equal(20, topProduct.Quantity);
        Assert.Equal(20m, topProduct.Revenue);
        Assert.Equal(3, topProductPage.Total);
    }

    [Fact]
    public async Task SalesDetailColumns_超过2100个商品关键词仍由数据库筛选分页()
    {
        var date = new DateTime(2026, 7, 1);
        await SeedStoreAsync("S1", "分店一");
        await SeedProductsAsync(
            Enumerable.Range(0, 2205)
                .Select(index => ($"P-BULK-{index:0000}", $"BULK-SEARCH-{index:0000}", $"930000000{index:0000}"))
        );
        await SeedProductStoreDailySalesAsync(
            date, "S1", "AUS-BULK", "P-BULK-2204", "BULK-SEARCH-2204", 12m, 2, 1,
            barcode: "9300000002204"
        );

        var result = await CreateService().GetSalesDetailColumnsAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            SalesDetailKind.Australia,
            SalesDetailSection.Products,
            branchCodes: new List<string> { "S1" },
            search: "BULK-SEARCH",
            pageIndex: 1,
            pageSize: 1
        );

        Assert.Equal(1, result.Total);
        Assert.Equal("P-BULK-2204", Assert.Single(result.Rows).Code);
    }

    [Fact]
    public async Task SalesDetailColumns_无商品主表和旧映射仍支持直接中国供应商多词搜索()
    {
        var date = new DateTime(2026, 7, 1);
        await SeedStoreAsync("S1", "分店一");
        await SeedChinaSupplierAsync("CN-DIRECT", "Direct China Supplier");
        // 故意不写 Product 与 POSM 旧 200 映射，验证统计事实自身字段可检索。
        await SeedProductStoreDailySalesAsync(
            date, "S1", "CN-DIRECT", "P-NO-CATALOG", "Direct China Widget", 18m, 3, 1,
            barcode: "BC-DIRECT"
        );

        var result = await CreateService().GetSalesDetailColumnsAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            SalesDetailKind.China,
            SalesDetailSection.Products,
            branchCodes: new List<string> { "S1" },
            search: "CN-DIRECT China"
        );

        var row = Assert.Single(result.Rows);
        Assert.Equal("P-NO-CATALOG", row.Code);
        Assert.Equal(18m, row.Revenue);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task SalesDetailColumns_超过2100个旧映射供应商词使用单参数集合搜索()
    {
        var date = new DateTime(2026, 7, 1);
        await SeedStoreAsync("S1", "分店一");
        await SeedChinaSupplierAsync("CN-MAP-BULK", "Mapped China Bulk");
        await SeedSupplierMappingsAsync(
            Enumerable.Range(0, 2205)
                .Select(index => ($"P-MAP-{index:0000}", "200", "CN-MAP-BULK"))
        );
        // 只保留最后一个商品有销售，候选映射仍超过 2100。
        await SeedProductStoreDailySalesAsync(
            date, "S1", "200", "P-MAP-2204", "Mapped China Widget", 21m, 3, 1
        );

        var result = await CreateService().GetSalesDetailColumnsAsync(
            new DateRangeDto { StartDate = date, EndDate = date },
            SalesDetailKind.China,
            SalesDetailSection.Products,
            branchCodes: new List<string> { "S1" },
            search: "CN-MAP-BULK China",
            pageSize: 1
        );

        Assert.Equal(1, result.Total);
        Assert.Equal("P-MAP-2204", Assert.Single(result.Rows).Code);
    }

    [Fact]
    public async Task SalesDetailColumns_商品主表命中但统计事实不命中时不放宽关键词()
    {
        var date = new DateTime(2026, 7, 1);
        await SeedStoreAsync("S1", "分店一");
        await SeedProductAsync("P-MATCH", "MATCH-TOKEN", "BC-MATCH");
        await SeedProductAsync("P-OTHER", "OTHER", "BC-OTHER");
        await SeedProductStoreDailySalesAsync(date, "S1", "AUS-ONE", "P-OTHER", "Other", 9m, 1, 1);
        var service = CreateService();
        var range = new DateRangeDto { StartDate = date, EndDate = date };

        var empty = await service.GetSalesDetailColumnsAsync(
            range, SalesDetailKind.Australia, SalesDetailSection.Products,
            branchCodes: new List<string> { "S1" }, search: "MATCH-TOKEN"
        );
        Assert.Empty(empty.Rows);

        await SeedProductStoreDailySalesAsync(date, "S1", "AUS-ONE", "P-MATCH", "Match", 11m, 1, 1);
        var matched = await CreateService().GetSalesDetailColumnsAsync(
            range, SalesDetailKind.Australia, SalesDetailSection.Products,
            branchCodes: new List<string> { "S1" }, search: "MATCH-TOKEN"
        );
        Assert.Equal("P-MATCH", Assert.Single(matched.Rows).Code);
    }

    private static SalesDetailColumnsController CreateSalesDetailColumnsController(
        ISalesDashboardReactService service,
        IUserService userService
    )
    {
        var controller = new SalesDetailColumnsController(
            service,
            userService,
            NullLogger<SalesDetailColumnsController>.Instance
        );
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, "user-1") },
                "TestAuth"
            )),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static IUserService CreateUserService(IEnumerable<string> storeCodes)
    {
        var stores = storeCodes
            .Select(code => new UserStoreDto { StoreCode = code })
            .ToList();
        var userServiceMock = new Mock<IUserService>();
        userServiceMock
            .Setup(service => service.GetUserByGuidAsync("user-1"))
            .ReturnsAsync(ApiResponse<UserDetailDto>.OK(new UserDetailDto
            {
                UserGUID = "user-1",
                Username = "tester",
                Stores = stores,
            }));
        return userServiceMock.Object;
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

    private static T ExtractAnonymousData<T>(object? value)
    {
        Assert.NotNull(value);
        var dataProperty = value!
            .GetType()
            .GetProperty("data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        Assert.NotNull(dataProperty);
        var data = dataProperty!.GetValue(value);
        return Assert.IsType<T>(data);
    }

    private static OkObjectResult AssertOk(IActionResult result)
    {
        return Assert.IsType<OkObjectResult>(result);
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

    private static int GetIntProperty(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<int>(property.GetValue(target));
    }

    private static string? GetStringProperty(object target, string propertyName)
    {
        var property = target
            .GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        Assert.NotNull(property);
        return Assert.IsType<string>(property.GetValue(target));
    }

    private static bool GetBoolProperty(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<bool>(property.GetValue(target));
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
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
        _hbSalesConnection.Dispose();
        if (File.Exists(_localDbPath)) SqliteTempFileCleanup.DeleteIfExists(_localDbPath);
        if (File.Exists(_posmDbPath)) SqliteTempFileCleanup.DeleteIfExists(_posmDbPath);
        if (File.Exists(_hbSalesDbPath)) SqliteTempFileCleanup.DeleteIfExists(_hbSalesDbPath);
    }
}
