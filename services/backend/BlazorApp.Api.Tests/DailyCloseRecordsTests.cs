using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorApp.Api.Authorization;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.DailyClose;
using BlazorApp.Api.Services.DailyCloses;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 日结记录后台只读查询：分店范围、状态分类与计数、汇总口径、日期区间、分页排序、「第 N 次」序号与详情口径。
/// POSM 与 HBweb 都用同一个 SQLite 内存库，查询全部走简单条件与简单聚合，保证 SQLite 与 SQL Server 行为一致。
/// </summary>
public sealed class DailyCloseQueryServiceTests : IDisposable
{
    // 固定「现在」：2026-10-06 20:00 UTC = 悉尼 2026-10-07 07:00（夏令时 UTC+11），默认窗口为 10-01 至 10-07。
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 20, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public DailyCloseQueryServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        // 表由 Hbpos.Api 建，这里手写与生产同列名的 SQLite 版本。
        _db.Ado.ExecuteCommand("""
            CREATE TABLE POSM_DailyClose (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DailyCloseGuid TEXT NOT NULL,
                StoreCode TEXT NOT NULL,
                DeviceCode TEXT NOT NULL,
                ClientKind TEXT NOT NULL,
                DetailLevel TEXT NOT NULL,
                DataSource TEXT NOT NULL,
                BackfillBatch TEXT NULL,
                BusinessDate TEXT NOT NULL,
                BusinessDateInferred INTEGER NOT NULL,
                PeriodFromUtc TEXT NULL,
                PeriodToUtc TEXT NULL,
                CashierId TEXT NOT NULL,
                CashierName TEXT NOT NULL,
                SavedAtUtc TEXT NOT NULL,
                AppVersion TEXT NULL,
                OrderCount INTEGER NULL,
                ReturnQuantity NUMERIC NULL,
                CashSalesAmount NUMERIC NULL,
                CashRefundAmount NUMERIC NULL,
                CashNetAmount NUMERIC NULL,
                CardSalesAmount NUMERIC NULL,
                CardRefundAmount NUMERIC NULL,
                CardNetAmount NUMERIC NULL,
                VoucherSalesAmount NUMERIC NULL,
                VoucherRefundAmount NUMERIC NULL,
                VoucherNetAmount NUMERIC NULL,
                RefundAmount NUMERIC NULL,
                ExpectedCashAmount NUMERIC NULL,
                CountedCashAmount NUMERIC NULL,
                CashDifference NUMERIC NULL,
                NoteSubtotal NUMERIC NULL,
                CoinSubtotal NUMERIC NULL,
                CashCountsJson TEXT NULL,
                ReceivedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """);
        _db.CodeFirst.InitTables(typeof(Store));
    }

    // ---------- 日期区间 ----------

    [Fact]
    public async Task GetListAsync_DefaultsToLastSevenDaysEndingSydneyToday()
    {
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 9, 30), difference: 0m);
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 1), difference: 0m);
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 7), difference: 0m);
        // 悉尼「今天」是 10-07；10-08 的记录（时钟漂移的设备）不在默认窗口内。
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 8), difference: 0m);

        var result = await CreateService("Admin").GetListAsync(new DailyCloseQueryDto());

        Assert.Equal(2, result.Total);
        Assert.Equal(
            [new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 1)],
            result.Items.Select(item => item.BusinessDate));
        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.PageSize);
    }

    [Fact]
    public async Task GetListAsync_OnlyFromGivenDefaultsEndToTodayAndOnlyToGivenDefaultsStartToSixDaysBefore()
    {
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 9, 25), difference: 0m);
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2), difference: 0m);
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 7), difference: 0m);
        var service = CreateService("Admin");

        var fromOnly = await service.GetListAsync(new DailyCloseQueryDto { BusinessDateFrom = "2026-10-01" });
        var toOnly = await service.GetListAsync(new DailyCloseQueryDto { BusinessDateTo = "2026-09-28" });

        Assert.Equal(2, fromOnly.Total);
        var only = Assert.Single(toOnly.Items);
        Assert.Equal(new DateOnly(2026, 9, 25), only.BusinessDate);
    }

    [Fact]
    public async Task GetListAsync_AcceptsExactly93DaysAndRejectsMore()
    {
        var service = CreateService("Admin");

        var ok = await service.GetListAsync(new DailyCloseQueryDto
        {
            BusinessDateFrom = "2026-07-01",
            BusinessDateTo = "2026-10-01",
        });
        var tooLong = await Assert.ThrowsAsync<DailyCloseRequestException>(() => service.GetListAsync(
            new DailyCloseQueryDto { BusinessDateFrom = "2026-06-30", BusinessDateTo = "2026-10-01" }));

        Assert.Equal(0, ok.Total);
        Assert.Equal("INVALID_QUERY", tooLong.Code);
    }

    [Theory]
    [InlineData("2026-10-08", "2026-10-01", null, null, null, 1, 20)]   // 起始晚于结束
    [InlineData("2026/10/01", "2026-10-02", null, null, null, 1, 20)]   // 日期格式
    [InlineData("2026-10-01", "not-a-date", null, null, null, 1, 20)]
    [InlineData(null, null, "bogus", null, null, 1, 20)]                // status
    [InlineData(null, null, null, "Android", null, 1, 20)]              // clientKind
    [InlineData(null, null, null, null, null, 0, 20)]                   // page
    [InlineData(null, null, null, null, null, 1, 0)]                    // pageSize 下限
    [InlineData(null, null, null, null, null, 1, 101)]                  // pageSize 上限
    public async Task GetListAsync_RejectsInvalidParameters(
        string? from, string? to, string? status, string? clientKind, string? keyword, int page, int pageSize)
    {
        var service = CreateService("Admin");

        var exception = await Assert.ThrowsAsync<DailyCloseRequestException>(() => service.GetListAsync(
            new DailyCloseQueryDto
            {
                BusinessDateFrom = from,
                BusinessDateTo = to,
                Status = status,
                ClientKind = clientKind,
                Keyword = keyword,
                Page = page,
                PageSize = pageSize,
            }));

        Assert.Equal("INVALID_QUERY", exception.Code);
    }

    [Fact]
    public async Task GetListAsync_InvalidParametersAreRejectedEvenWithoutAccess()
    {
        // 参数校验与权限无关：越权账号与有权账号对非法参数的反应一致。
        var service = CreateService("StoreStaff");

        await Assert.ThrowsAsync<DailyCloseRequestException>(() => service.GetListAsync(
            new DailyCloseQueryDto { PageSize = 500 }));
    }

    // ---------- 状态分类、计数与汇总 ----------

    [Fact]
    public async Task GetListAsync_ClassifiesStatusAndCountsIgnoreStatusFilter()
    {
        await SeedFourStatusesAsync();
        var service = CreateService("Admin");

        var all = await service.GetListAsync(Query());
        var shortOnly = await service.GetListAsync(Query(status: "short"));
        var overOnly = await service.GetListAsync(Query(status: "over"));
        var evenOnly = await service.GetListAsync(Query(status: "even"));
        var noneOnly = await service.GetListAsync(Query(status: "none"));

        // counts 不受 status 影响，五次查询里完全一致。
        foreach (var result in new[] { all, shortOnly, overOnly, evenOnly, noneOnly })
        {
            Assert.Equal(4, result.Counts.All);
            Assert.Equal(1, result.Counts.Short);
            Assert.Equal(1, result.Counts.Over);
            Assert.Equal(1, result.Counts.Even);
            Assert.Equal(1, result.Counts.None);
        }

        Assert.Equal(4, all.Total);
        Assert.Equal(
            ["even", "none", "over", "short"],
            all.Items.Select(item => item.DifferenceKind).Order(StringComparer.Ordinal));

        Assert.Equal("short", Assert.Single(shortOnly.Items).DifferenceKind);
        Assert.Equal(1, shortOnly.Total);
        Assert.Equal("over", Assert.Single(overOnly.Items).DifferenceKind);
        Assert.Equal("even", Assert.Single(evenOnly.Items).DifferenceKind);
        var none = Assert.Single(noneOnly.Items);
        Assert.Equal("none", none.DifferenceKind);
        Assert.Null(none.CashDifference);
    }

    [Fact]
    public async Task GetListAsync_StatusAllAndStatusIgnoreCaseAreEquivalent()
    {
        await SeedFourStatusesAsync();
        var service = CreateService("Admin");

        var explicitAll = await service.GetListAsync(Query(status: "ALL"));
        var upperShort = await service.GetListAsync(Query(status: "Short"));

        Assert.Equal(4, explicitAll.Total);
        Assert.Equal(1, upperShort.Total);
    }

    [Fact]
    public async Task GetListAsync_TotalsSumOnlyRowsWithDifferenceAndFollowStatusFilter()
    {
        // 应有 100 / 实点 95 / 差异 -5（短款）；应有 200 / 实点 203 / 差异 +3（长款）；
        // 应有 50 / 实点 50 / 差异 0（持平）；无金额（TraceOnly）不参与汇总。
        await SeedFourStatusesAsync();
        var service = CreateService("Admin");

        var all = await service.GetListAsync(Query());
        var shortOnly = await service.GetListAsync(Query(status: "short"));
        var noneOnly = await service.GetListAsync(Query(status: "none"));

        Assert.Equal(350m, all.Totals.ExpectedCash);
        Assert.Equal(348m, all.Totals.CountedCash);
        Assert.Equal(-2m, all.Totals.Difference);
        Assert.Equal(100m, shortOnly.Totals.ExpectedCash);
        Assert.Equal(95m, shortOnly.Totals.CountedCash);
        Assert.Equal(-5m, shortOnly.Totals.Difference);
        Assert.Equal(0m, noneOnly.Totals.ExpectedCash);
        Assert.Equal(0m, noneOnly.Totals.CountedCash);
        Assert.Equal(0m, noneOnly.Totals.Difference);
    }

    [Fact]
    public async Task GetListAsync_TotalsKeepTwoDecimalCents()
    {
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2), expected: 100.10m, counted: 100.05m, difference: -0.05m);
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 3), expected: 200.20m, counted: 200.30m, difference: 0.10m);

        var result = await CreateService("Admin").GetListAsync(Query());

        Assert.Equal(300.30m, result.Totals.ExpectedCash);
        Assert.Equal(300.35m, result.Totals.CountedCash);
        Assert.Equal(0.05m, result.Totals.Difference);
    }

    [Fact]
    public async Task GetListAsync_TraceOnlyRowHasNoAmountsAndIsClassifiedNone()
    {
        await InsertAsync(
            "BRI", "POS-1", new DateTime(2026, 10, 2),
            detailLevel: "TraceOnly", dataSource: "AuditBackfill", clientKind: "Wpf",
            expected: null, counted: null, difference: null, cardNet: null, orderCount: null,
            dateInferred: true);

        var result = await CreateService("Admin").GetListAsync(Query());

        var item = Assert.Single(result.Items);
        Assert.Equal("TraceOnly", item.DetailLevel);
        Assert.Equal("AuditBackfill", item.DataSource);
        Assert.True(item.BusinessDateInferred);
        Assert.Null(item.ExpectedCashAmount);
        Assert.Null(item.CountedCashAmount);
        Assert.Null(item.CashDifference);
        Assert.Null(item.CardNetAmount);
        Assert.Null(item.OrderCount);
        Assert.Equal("none", item.DifferenceKind);
        Assert.Equal(1, result.Counts.None);
        Assert.Equal(0m, result.Totals.Difference);
    }

    [Fact]
    public async Task GetListAsync_ReturnsEmptyTotalsAndCountsWhenNothingMatches()
    {
        var result = await CreateService("Admin").GetListAsync(Query());

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.Counts.All);
        Assert.Equal(0m, result.Totals.ExpectedCash);
    }

    // ---------- 其余筛选 ----------

    [Fact]
    public async Task GetListAsync_FiltersByDeviceClientKindAndCashierKeywordAndCountsFollowThem()
    {
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2), clientKind: "Wpf", cashierId: "1001", cashierName: "Alice Wong", difference: -1m);
        await InsertAsync("BRI", "PDA-1", new DateTime(2026, 10, 2), clientKind: "Handheld", cashierId: "1002", cashierName: "Bob Lee", difference: 2m);
        await InsertAsync("BRI", "IPAD-1", new DateTime(2026, 10, 2), clientKind: "Ipad", cashierId: "2003", cashierName: "Carol", difference: 0m);
        var service = CreateService("Admin");

        var device = await service.GetListAsync(Query(deviceCode: "PDA-1"));
        var kind = await service.GetListAsync(Query(clientKind: "ipad"));
        var byName = await service.GetListAsync(Query(keyword: "alice"));
        var byId = await service.GetListAsync(Query(keyword: "200"));
        var bobOver = await service.GetListAsync(Query(keyword: "Bob", status: "short"));

        Assert.Equal("PDA-1", Assert.Single(device.Items).DeviceCode);
        Assert.Equal(1, device.Counts.All);
        Assert.Equal(1, device.Counts.Over);
        Assert.Equal("IPAD-1", Assert.Single(kind.Items).DeviceCode);
        Assert.Equal("Ipad", kind.Items[0].ClientKind);
        Assert.Equal("POS-1", Assert.Single(byName.Items).DeviceCode);
        Assert.Equal("IPAD-1", Assert.Single(byId.Items).DeviceCode);
        // 关键字生效、status 过滤后无记录，但 counts 仍反映关键字过滤后的各状态数量。
        Assert.Empty(bobOver.Items);
        Assert.Equal(0, bobOver.Total);
        Assert.Equal(1, bobOver.Counts.All);
        Assert.Equal(1, bobOver.Counts.Over);
    }

    [Fact]
    public async Task GetListAsync_KeywordIsParameterizedAndDoesNotInjectSql()
    {
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2), cashierName: "Alice", difference: 0m);

        var result = await CreateService("Admin").GetListAsync(Query(keyword: "%' OR 1=1 --"));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Counts.All);
    }

    // ---------- 分页与排序 ----------

    [Fact]
    public async Task GetListAsync_SortsByBusinessDateThenSavedAtThenIdDescendingAndPaginates()
    {
        var saved = new DateTime(2026, 10, 3, 8, 0, 0, DateTimeKind.Utc);
        var oldDay = await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2), savedAtUtc: saved.AddHours(5), cashierName: "d-old-day");
        var tieFirst = await InsertAsync("BRI", "POS-2", new DateTime(2026, 10, 3), savedAtUtc: saved, cashierName: "c-tie-first");
        var tieSecond = await InsertAsync("BRI", "POS-3", new DateTime(2026, 10, 3), savedAtUtc: saved, cashierName: "b-tie-second");
        var later = await InsertAsync("BRI", "POS-4", new DateTime(2026, 10, 3), savedAtUtc: saved.AddHours(1), cashierName: "a-later");
        var service = CreateService("Admin");

        var page1 = await service.GetListAsync(Query(page: 1, pageSize: 3));
        var page2 = await service.GetListAsync(Query(page: 2, pageSize: 3));
        var page3 = await service.GetListAsync(Query(page: 3, pageSize: 3));

        // 营业日降序；同日按保存时间降序；保存时间相同按 Id 降序。
        Assert.Equal(
            [later.DailyCloseGuid, tieSecond.DailyCloseGuid, tieFirst.DailyCloseGuid],
            page1.Items.Select(item => item.DailyCloseGuid));
        Assert.Equal([oldDay.DailyCloseGuid], page2.Items.Select(item => item.DailyCloseGuid));
        Assert.Equal(4, page1.Total);
        Assert.Equal(4, page2.Total);
        Assert.Equal(3, page2.PageSize);
        Assert.Equal(2, page2.Page);
        // 超出范围的页：条目为空，总数与计数保留。
        Assert.Empty(page3.Items);
        Assert.Equal(4, page3.Total);
        Assert.Equal(4, page3.Counts.All);
    }

    [Fact]
    public async Task GetListAsync_PageSizeHundredIsAcceptedAndDefaultIsTwenty()
    {
        for (var index = 0; index < 25; index++)
        {
            await InsertAsync("BRI", $"POS-{index}", new DateTime(2026, 10, 2));
        }

        var service = CreateService("Admin");
        var defaultPage = await service.GetListAsync(Query());
        var big = await service.GetListAsync(Query(pageSize: 100));

        Assert.Equal(20, defaultPage.Items.Count);
        Assert.Equal(25, defaultPage.Total);
        Assert.Equal(25, big.Items.Count);
    }

    // ---------- 第 N 次序号 ----------

    [Fact]
    public async Task GetListAsync_SaveSequenceCountsMultipleSavesInSameStoreDeviceDay()
    {
        var day = new DateTime(2026, 10, 3);
        var morning = await InsertAsync("BRI", "POS-1", day, savedAtUtc: Utc(2026, 10, 3, 1), cashierName: "morning");
        var evening = await InsertAsync("BRI", "POS-1", day, savedAtUtc: Utc(2026, 10, 3, 9), cashierName: "evening");
        var noon = await InsertAsync("BRI", "POS-1", day, savedAtUtc: Utc(2026, 10, 3, 5), cashierName: "noon");

        var result = await CreateService("Admin").GetListAsync(Query());

        var byGuid = result.Items.ToDictionary(item => item.DailyCloseGuid);
        Assert.Equal(1, byGuid[morning.DailyCloseGuid].SaveSequence);
        Assert.Equal(2, byGuid[noon.DailyCloseGuid].SaveSequence);
        Assert.Equal(3, byGuid[evening.DailyCloseGuid].SaveSequence);
        Assert.All(result.Items, item => Assert.Equal(3, item.SaveCountInDay));
    }

    [Fact]
    public async Task GetListAsync_SaveSequenceTieOnSavedTimeFallsBackToId()
    {
        var saved = Utc(2026, 10, 3, 3);
        var first = await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 3), savedAtUtc: saved);
        var second = await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 3), savedAtUtc: saved);

        var result = await CreateService("Admin").GetListAsync(Query());

        var byGuid = result.Items.ToDictionary(item => item.DailyCloseGuid);
        Assert.Equal(1, byGuid[first.DailyCloseGuid].SaveSequence);
        Assert.Equal(2, byGuid[second.DailyCloseGuid].SaveSequence);
    }

    [Fact]
    public async Task GetListAsync_SaveSequenceIsIsolatedAcrossStoresDevicesAndDays()
    {
        var day = new DateTime(2026, 10, 3);
        var a1 = await InsertAsync("BRI", "POS-1", day, savedAtUtc: Utc(2026, 10, 3, 1));
        var a2 = await InsertAsync("BRI", "POS-1", day, savedAtUtc: Utc(2026, 10, 3, 2));
        var otherDevice = await InsertAsync("BRI", "POS-2", day, savedAtUtc: Utc(2026, 10, 3, 3));
        var otherStore = await InsertAsync("SYD", "POS-1", day, savedAtUtc: Utc(2026, 10, 3, 4));
        var otherDay = await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 4), savedAtUtc: Utc(2026, 10, 3, 5));

        var result = await CreateService("Admin").GetListAsync(Query());

        var byGuid = result.Items.ToDictionary(item => item.DailyCloseGuid);
        Assert.Equal((1, 2), (byGuid[a1.DailyCloseGuid].SaveSequence, byGuid[a1.DailyCloseGuid].SaveCountInDay));
        Assert.Equal((2, 2), (byGuid[a2.DailyCloseGuid].SaveSequence, byGuid[a2.DailyCloseGuid].SaveCountInDay));
        Assert.Equal((1, 1), (byGuid[otherDevice.DailyCloseGuid].SaveSequence, byGuid[otherDevice.DailyCloseGuid].SaveCountInDay));
        Assert.Equal((1, 1), (byGuid[otherStore.DailyCloseGuid].SaveSequence, byGuid[otherStore.DailyCloseGuid].SaveCountInDay));
        Assert.Equal((1, 1), (byGuid[otherDay.DailyCloseGuid].SaveSequence, byGuid[otherDay.DailyCloseGuid].SaveCountInDay));
    }

    [Fact]
    public async Task GetListAsync_SaveSequenceIgnoresListFiltersAndPageBoundaries()
    {
        // 同一分店设备营业日有三次日结，第二次是长款：按 status=short 过滤后列表只剩第 1、3 次，
        // 但序号仍按整组算——第 3 次的序号是 3，而不是过滤后的 2。
        var day = new DateTime(2026, 10, 3);
        var first = await InsertAsync("BRI", "POS-1", day, savedAtUtc: Utc(2026, 10, 3, 1), expected: 100m, counted: 95m, difference: -5m, cashierName: "Ann");
        await InsertAsync("BRI", "POS-1", day, savedAtUtc: Utc(2026, 10, 3, 2), expected: 100m, counted: 103m, difference: 3m, cashierName: "Bea");
        var third = await InsertAsync("BRI", "POS-1", day, savedAtUtc: Utc(2026, 10, 3, 3), expected: 100m, counted: 90m, difference: -10m, cashierName: "Ann");
        var service = CreateService("Admin");

        var shortOnly = await service.GetListAsync(Query(status: "short"));
        var byAnn = await service.GetListAsync(Query(keyword: "Ann"));
        // 分页把同一分组拆到两页，序号仍需正确。
        var page1 = await service.GetListAsync(Query(page: 1, pageSize: 1));
        var page3 = await service.GetListAsync(Query(page: 3, pageSize: 1));

        Assert.Equal(2, shortOnly.Items.Count);
        Assert.Equal(3, shortOnly.Items.Single(item => item.DailyCloseGuid == third.DailyCloseGuid).SaveSequence);
        Assert.Equal(1, shortOnly.Items.Single(item => item.DailyCloseGuid == first.DailyCloseGuid).SaveSequence);
        Assert.All(shortOnly.Items, item => Assert.Equal(3, item.SaveCountInDay));
        Assert.Equal([1, 3], byAnn.Items.Select(item => item.SaveSequence).Order());
        Assert.Equal(3, Assert.Single(page1.Items).SaveSequence);
        Assert.Equal(1, Assert.Single(page3.Items).SaveSequence);
        Assert.Equal(3, page1.Items[0].SaveCountInDay);
    }

    [Fact]
    public async Task GetListAsync_SaveSequenceIncludesGroupRowsOutsideDateFilterNeverCrossingDays()
    {
        // 营业日不同的记录属于不同分组，窗口只含 10-03 时 10-02 那条不会影响序号。
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2), savedAtUtc: Utc(2026, 10, 2, 1));
        var target = await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 3), savedAtUtc: Utc(2026, 10, 3, 1));

        var result = await CreateService("Admin").GetListAsync(new DailyCloseQueryDto
        {
            BusinessDateFrom = "2026-10-03",
            BusinessDateTo = "2026-10-03",
        });

        var item = Assert.Single(result.Items);
        Assert.Equal(target.DailyCloseGuid, item.DailyCloseGuid);
        Assert.Equal((1, 1), (item.SaveSequence, item.SaveCountInDay));
    }

    // ---------- 分店范围 ----------

    [Fact]
    public async Task GetListAsync_AdminSeesAllStoresAndCanFilterByRequestedStores()
    {
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2));
        await InsertAsync("SYD", "POS-1", new DateTime(2026, 10, 2));
        await InsertAsync("MEL", "POS-1", new DateTime(2026, 10, 2));

        foreach (var role in new[] { "Admin", "管理员", "SuperAdmin", "超级管理员" })
        {
            var all = await CreateService(role).GetListAsync(Query());
            Assert.Equal(3, all.Total);
        }

        var some = await CreateService("Admin").GetListAsync(Query(storeCodes: "BRI, MEL"));
        Assert.Equal(["BRI", "MEL"], some.Items.Select(item => item.StoreCode).Order(StringComparer.Ordinal));
        Assert.Equal(2, some.Counts.All);
    }

    [Theory]
    [InlineData("StoreManager")]
    [InlineData("店长")]
    [InlineData("经理")]
    public async Task GetListAsync_StoreManagerSeesOnlyAssignedStores(string role)
    {
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2));
        await InsertAsync("SYD", "POS-1", new DateTime(2026, 10, 2));
        await InsertAsync("MEL", "POS-1", new DateTime(2026, 10, 2));
        var service = CreateService(role, assignedStoreCodes: ["BRI", "SYD"]);

        var result = await service.GetListAsync(Query());

        Assert.Equal(["BRI", "SYD"], result.Items.Select(item => item.StoreCode).Order(StringComparer.Ordinal));
        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Counts.All);
    }

    [Fact]
    public async Task GetListAsync_StoreManagerRequestedStoresIntersectWithVisibleScope()
    {
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2));
        await InsertAsync("SYD", "POS-1", new DateTime(2026, 10, 2));
        await InsertAsync("MEL", "POS-1", new DateTime(2026, 10, 2));
        var service = CreateService("StoreManager", assignedStoreCodes: ["BRI", "SYD"]);

        // 请求里夹带越权分店 MEL：只返回交集（BRI），越权分店不报错也不泄露。
        var partial = await service.GetListAsync(Query(storeCodes: "BRI,MEL"));
        // 忽略大小写与首尾空格。
        var caseInsensitive = await service.GetListAsync(Query(storeCodes: " syd "));

        Assert.Equal("BRI", Assert.Single(partial.Items).StoreCode);
        Assert.Equal(1, partial.Counts.All);
        Assert.Equal("SYD", Assert.Single(caseInsensitive.Items).StoreCode);
    }

    [Fact]
    public async Task GetListAsync_StoreManagerWithEmptyIntersectionReturnsEmptyWithoutError()
    {
        await InsertAsync("MEL", "POS-1", new DateTime(2026, 10, 2), difference: -3m);
        var service = CreateService("StoreManager", assignedStoreCodes: ["BRI"]);

        var result = await service.GetListAsync(Query(storeCodes: "MEL"));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.Counts.All);
        Assert.Equal(0, result.Counts.Short);
        Assert.Equal(0m, result.Totals.Difference);
        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.PageSize);
    }

    [Theory]
    [InlineData("StoreManager", false)]   // 店长没有分配任何分店
    [InlineData("StoreStaff", true)]      // 非管理员也非店长
    [InlineData("WarehouseManager", true)]
    public async Task GetListAsync_ReturnsEmptyWhenAccountHasNoVisibleStores(string role, bool scopeAllowed)
    {
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2));
        var service = CreateService(
            role,
            assignedStoreCodes: scopeAllowed ? new[] { "BRI" } : Array.Empty<string>(),
            scopeAllowed: scopeAllowed);

        var result = await service.GetListAsync(Query());

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.Counts.All);
    }

    [Fact]
    public async Task GetListAsync_ReturnsEmptyForUnauthenticatedUser()
    {
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2));
        var service = CreateService("Admin", authenticated: false);

        var result = await service.GetListAsync(Query());

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Counts.All);
    }

    // ---------- 门店名称与时区 ----------

    [Fact]
    public async Task GetListAsync_FillsStoreNameAndTimeZoneAndLeavesUnknownStoresNull()
    {
        await _db.Insertable(new Store { StoreCode = "BRI", StoreName = "Brisbane CBD", TimeZoneId = "Australia/Brisbane" }).ExecuteCommandAsync();
        await _db.Insertable(new Store { StoreCode = "SYD", StoreName = "Sydney", TimeZoneId = null }).ExecuteCommandAsync();
        await _db.Insertable(new Store { StoreCode = "DEL", StoreName = "Deleted Store", TimeZoneId = "Australia/Sydney", IsDeleted = true }).ExecuteCommandAsync();
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2));
        await InsertAsync("SYD", "POS-1", new DateTime(2026, 10, 2));
        await InsertAsync("DEL", "POS-1", new DateTime(2026, 10, 2));
        await InsertAsync("ZZZ", "POS-1", new DateTime(2026, 10, 2));

        var result = await CreateService("Admin").GetListAsync(Query());

        var byStore = result.Items.ToDictionary(item => item.StoreCode);
        Assert.Equal("Brisbane CBD", byStore["BRI"].StoreName);
        Assert.Equal("Australia/Brisbane", byStore["BRI"].StoreTimeZoneId);
        Assert.Equal("Sydney", byStore["SYD"].StoreName);
        Assert.Null(byStore["SYD"].StoreTimeZoneId);
        Assert.Null(byStore["DEL"].StoreName);
        Assert.Null(byStore["ZZZ"].StoreName);
        Assert.Null(byStore["ZZZ"].StoreTimeZoneId);
    }

    [Fact]
    public async Task GetListAsync_StoreLookupFailureDoesNotBreakTheList()
    {
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2));
        // 门店库不可用（表不存在）：名称与时区给 null，列表主体照常返回。
        await _db.Ado.ExecuteCommandAsync("DROP TABLE Store");

        var result = await CreateService("Admin").GetListAsync(Query());

        var item = Assert.Single(result.Items);
        Assert.Null(item.StoreName);
        Assert.Null(item.StoreTimeZoneId);
    }

    [Fact]
    public async Task GetListAsync_WorksWithoutHbwebConnection()
    {
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2));

        var result = await CreateService("Admin", withHbwebDb: false).GetListAsync(Query());

        Assert.Null(Assert.Single(result.Items).StoreName);
    }

    // ---------- 详情 ----------

    [Fact]
    public async Task GetDetailAsync_FullRecordReturnsTendersCashCountsAndTotals()
    {
        await _db.Insertable(new Store { StoreCode = "BRI", StoreName = "Brisbane CBD", TimeZoneId = "Australia/Brisbane" }).ExecuteCommandAsync();
        var row = await InsertAsync(
            "BRI", "POS-1", new DateTime(2026, 10, 3),
            savedAtUtc: Utc(2026, 10, 3, 9), expected: 1230.50m, counted: 1228.50m, difference: -2m,
            cardNet: 410.25m, orderCount: 87,
            cashCountsJson: """
                [{"denominationCents":5,"quantity":3},{"denominationCents":10000,"quantity":8},{"denominationCents":200,"quantity":7},
                 {"denominationCents":500,"quantity":4},{"denominationCents":2000,"quantity":1},{"denominationCents":100,"quantity":0}]
                """,
            noteSubtotal: 820m, coinSubtotal: 14.15m);
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 3), savedAtUtc: Utc(2026, 10, 3, 2));

        var detail = await CreateService("Admin").GetDetailAsync(row.DailyCloseGuid);

        Assert.NotNull(detail);
        Assert.Equal(row.DailyCloseGuid, detail.DailyCloseGuid);
        Assert.Equal("Brisbane CBD", detail.StoreName);
        Assert.Equal("Australia/Brisbane", detail.StoreTimeZoneId);
        Assert.Equal(new DateOnly(2026, 10, 3), detail.BusinessDate);
        Assert.Equal(2, detail.SaveSequence);
        Assert.Equal(2, detail.SaveCountInDay);
        Assert.Equal("short", detail.DifferenceKind);
        Assert.Equal(87, detail.OrderCount);
        Assert.Equal(1230.50m, detail.ExpectedCashAmount);
        Assert.Equal(1228.50m, detail.CountedCashAmount);
        Assert.Equal(-2m, detail.CashDifference);
        Assert.Equal("1.0.47", detail.AppVersion);
        Assert.Equal(12.5m, detail.ReturnQuantity);
        Assert.Equal(30m, detail.RefundAmount);
        Assert.Equal(820m, detail.NoteSubtotal);
        Assert.Equal(14.15m, detail.CoinSubtotal);
        Assert.Equal(DateTimeKind.Utc, detail.SavedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, detail.ReceivedAtUtc.Kind);
        Assert.NotNull(detail.PeriodFromUtc);
        Assert.NotNull(detail.PeriodToUtc);

        // 付款方式固定 Cash、Card、Voucher 三条，来自三组列。
        Assert.Equal(["Cash", "Card", "Voucher"], detail.Tenders.Select(item => item.Method));
        var cash = detail.Tenders[0];
        Assert.Equal((1500m, 100m, 1400m), (cash.SalesAmount, cash.RefundAmount, cash.NetAmount));
        var card = detail.Tenders[1];
        Assert.Equal((420m, 9.75m, 410.25m), (card.SalesAmount, card.RefundAmount, card.NetAmount));
        var voucher = detail.Tenders[2];
        Assert.Equal((10m, 0m, 10m), (voucher.SalesAmount, voucher.RefundAmount, voucher.NetAmount));

        // 面额明细按面额降序，附小计与纸币/硬币类别（≥5 元为纸币）。
        Assert.Equal([10000, 2000, 500, 200, 100, 5], detail.CashCounts.Select(item => item.DenominationCents));
        Assert.Equal(["Note", "Note", "Note", "Coin", "Coin", "Coin"], detail.CashCounts.Select(item => item.Kind));
        Assert.Equal([800m, 20m, 20m, 14m, 0m, 0.15m], detail.CashCounts.Select(item => item.SubtotalAmount));
        Assert.Equal([8, 1, 4, 7, 0, 3], detail.CashCounts.Select(item => item.Quantity));
    }

    [Theory]
    [InlineData("CashOnly")]
    [InlineData("TraceOnly")]
    public async Task GetDetailAsync_NonFullRecordHasEmptyTendersAndCashCounts(string detailLevel)
    {
        var hasAmounts = detailLevel == "CashOnly";
        var row = await InsertAsync(
            "BRI", "POS-1", new DateTime(2026, 10, 3),
            detailLevel: detailLevel, dataSource: "AuditBackfill", clientKind: "Wpf", dateInferred: true,
            expected: hasAmounts ? 500m : null, counted: hasAmounts ? 498m : null, difference: hasAmounts ? -2m : null,
            cardNet: null, orderCount: null,
            // 即使库里残留了面额 JSON 与付款列，非 Full 也不能返回明细。
            cashCountsJson: """[{"denominationCents":10000,"quantity":1}]""",
            fillTenders: false, appVersion: null);

        var detail = await CreateService("Admin").GetDetailAsync(row.DailyCloseGuid);

        Assert.NotNull(detail);
        Assert.Equal(detailLevel, detail.DetailLevel);
        Assert.True(detail.BusinessDateInferred);
        Assert.Empty(detail.Tenders);
        Assert.Empty(detail.CashCounts);
        Assert.Equal(hasAmounts ? -2m : (decimal?)null, detail.CashDifference);
        Assert.Equal(hasAmounts ? "short" : "none", detail.DifferenceKind);
        Assert.Null(detail.AppVersion);
        Assert.Null(detail.OrderCount);
        Assert.Null(detail.CardNetAmount);
    }

    [Fact]
    public async Task GetDetailAsync_CorruptCashCountsJsonYieldsEmptyListInsteadOfFailing()
    {
        var row = await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 3), cashCountsJson: "{not json");

        var detail = await CreateService("Admin").GetDetailAsync(row.DailyCloseGuid);

        Assert.NotNull(detail);
        Assert.Empty(detail.CashCounts);
        Assert.Equal(3, detail.Tenders.Count);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNullForMissingAndOutOfScopeRecordsAlike()
    {
        var mine = await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 3));
        var other = await InsertAsync("MEL", "POS-1", new DateTime(2026, 10, 3));
        var manager = CreateService("StoreManager", assignedStoreCodes: ["BRI"]);

        var visible = await manager.GetDetailAsync(mine.DailyCloseGuid);
        var outOfScope = await manager.GetDetailAsync(other.DailyCloseGuid);
        var missing = await manager.GetDetailAsync(Guid.NewGuid());
        var empty = await manager.GetDetailAsync(Guid.Empty);
        var adminSeesOther = await CreateService("Admin").GetDetailAsync(other.DailyCloseGuid);
        var staff = await CreateService("StoreStaff").GetDetailAsync(mine.DailyCloseGuid);

        Assert.NotNull(visible);
        // 无权限与不存在无法区分：都是 null（控制器映射为 404）。
        Assert.Null(outOfScope);
        Assert.Null(missing);
        Assert.Null(empty);
        Assert.NotNull(adminSeesOther);
        Assert.Null(staff);
    }

    // ---------- 序列化口径 ----------

    [Fact]
    public async Task ListItem_SerializesCamelCaseDateOnlyAndUtcInstants()
    {
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 3), savedAtUtc: Utc(2026, 10, 3, 9, 30), difference: -5m);
        var result = await CreateService("Admin").GetListAsync(Query());
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var json = JsonSerializer.Serialize(result, options);

        Assert.Contains("\"businessDate\":\"2026-10-03\"", json, StringComparison.Ordinal);
        Assert.Contains("\"differenceKind\":\"short\"", json, StringComparison.Ordinal);
        Assert.Contains("\"saveSequence\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"saveCountInDay\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"savedAtUtc\":\"2026-10-03T09:30:00Z\"", json, StringComparison.Ordinal);
        Assert.Contains("\"counts\":{\"all\":1,\"short\":1,\"over\":0,\"even\":0,\"none\":0}", json, StringComparison.Ordinal);
        Assert.Contains("\"totals\":{\"expectedCash\":", json, StringComparison.Ordinal);
    }

    // ---------- 辅助 ----------

    private async Task SeedFourStatusesAsync()
    {
        await InsertAsync("BRI", "POS-1", new DateTime(2026, 10, 2), expected: 100m, counted: 95m, difference: -5m, cashierName: "short");
        await InsertAsync("BRI", "POS-2", new DateTime(2026, 10, 2), expected: 200m, counted: 203m, difference: 3m, cashierName: "over");
        await InsertAsync("BRI", "POS-3", new DateTime(2026, 10, 2), expected: 50m, counted: 50m, difference: 0m, cashierName: "even");
        await InsertAsync(
            "BRI", "POS-4", new DateTime(2026, 10, 2),
            detailLevel: "TraceOnly", dataSource: "AuditBackfill",
            expected: null, counted: null, difference: null, cashierName: "none");
    }

    private static DailyCloseQueryDto Query(
        string? status = null,
        string? storeCodes = null,
        string? deviceCode = null,
        string? clientKind = null,
        string? keyword = null,
        int page = 1,
        int pageSize = 20) => new()
    {
        BusinessDateFrom = "2026-10-01",
        BusinessDateTo = "2026-10-07",
        Status = status,
        StoreCodes = storeCodes,
        DeviceCode = deviceCode,
        ClientKind = clientKind,
        Keyword = keyword,
        Page = page,
        PageSize = pageSize,
    };

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private DailyCloseQueryService CreateService(
        string role,
        IReadOnlyList<string>? assignedStoreCodes = null,
        bool scopeAllowed = true,
        bool authenticated = true,
        bool withHbwebDb = true)
    {
        var identity = authenticated
            ? new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "test-user"), new Claim(ClaimTypes.Role, role)],
                "Test")
            : new ClaimsIdentity();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        var scope = new FakeStoreScopeService(new CurrentUserManageableStoreScope
        {
            IsAllowed = scopeAllowed,
            IsAuthenticated = authenticated,
            StoreCodes = assignedStoreCodes ?? [],
        });
        return new DailyCloseQueryService(
            _db,
            withHbwebDb ? _db : null,
            scope,
            accessor,
            logger: null,
            timeProvider: new FixedTimeProvider(FixedNow));
    }

    /// <summary>默认写入一条完整（Full）日结：应有 100、实点 100、差异 0，付款与面额明细齐全。</summary>
    private async Task<PosmDailyClose> InsertAsync(
        string storeCode,
        string deviceCode,
        DateTime businessDate,
        DateTime? savedAtUtc = null,
        string clientKind = "Wpf",
        string detailLevel = "Full",
        string dataSource = "ClientUpload",
        string cashierId = "1001",
        string cashierName = "Alice",
        decimal? expected = 100m,
        decimal? counted = 100m,
        decimal? difference = 0m,
        decimal? cardNet = 410.25m,
        int? orderCount = 12,
        string? cashCountsJson = """[{"denominationCents":10000,"quantity":1}]""",
        decimal? noteSubtotal = 100m,
        decimal? coinSubtotal = 0m,
        bool dateInferred = false,
        bool fillTenders = true,
        string? appVersion = "1.0.47")
    {
        var saved = savedAtUtc ?? new DateTime(businessDate.Year, businessDate.Month, businessDate.Day, 10, 0, 0, DateTimeKind.Utc);
        var row = new PosmDailyClose
        {
            DailyCloseGuid = Guid.NewGuid(),
            StoreCode = storeCode,
            DeviceCode = deviceCode,
            ClientKind = clientKind,
            DetailLevel = detailLevel,
            DataSource = dataSource,
            BackfillBatch = dataSource == "AuditBackfill" ? "batch-1" : null,
            BusinessDate = businessDate.Date,
            BusinessDateInferred = dateInferred,
            PeriodFromUtc = saved.AddHours(-8),
            PeriodToUtc = saved,
            CashierId = cashierId,
            CashierName = cashierName,
            SavedAtUtc = saved,
            AppVersion = appVersion,
            OrderCount = orderCount,
            ReturnQuantity = fillTenders ? 12.5m : null,
            CashSalesAmount = fillTenders ? 1500m : null,
            CashRefundAmount = fillTenders ? 100m : null,
            CashNetAmount = fillTenders ? 1400m : null,
            CardSalesAmount = fillTenders ? 420m : null,
            CardRefundAmount = fillTenders ? 9.75m : null,
            CardNetAmount = cardNet,
            VoucherSalesAmount = fillTenders ? 10m : null,
            VoucherRefundAmount = fillTenders ? 0m : null,
            VoucherNetAmount = fillTenders ? 10m : null,
            RefundAmount = fillTenders ? 30m : null,
            ExpectedCashAmount = expected,
            CountedCashAmount = counted,
            CashDifference = difference,
            NoteSubtotal = noteSubtotal,
            CoinSubtotal = coinSubtotal,
            CashCountsJson = cashCountsJson,
            ReceivedAtUtc = saved.AddMinutes(1),
            UpdatedAtUtc = saved.AddMinutes(1),
        };
        row.Id = await _db.Insertable(row).ExecuteReturnBigIdentityAsync();
        return row;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeStoreScopeService(CurrentUserManageableStoreScope scope)
        : ICurrentUserManageableStoreScopeService
    {
        public Task<CurrentUserManageableStoreScope> GetScopeAsync() => Task.FromResult(scope);

        public Task<CurrentUserManageableStoreScope> GetAssignedStoreScopeAsync() => Task.FromResult(scope);

        public Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync() => Task.FromResult(scope.StoreCodes);

        public Task<bool> CanAccessStoreCodeAsync(string storeCode) => Task.FromResult(scope.CanAccessStoreCode(storeCode));

        public Task<bool> CanAccessOrderAsync(string orderGuid) => Task.FromResult(false);

        public Task<bool> CanManageStoreAsync(string storeGuid) => Task.FromResult(false);

        public Task<bool> CanManageUserAsync(string userGuid) => Task.FromResult(false);
    }
}

/// <summary>日结记录控制器：授权元数据、路由与错误映射（400 / 404）。</summary>
public sealed class PosDailyClosesControllerTests
{
    [Fact]
    public void Controller_RequiresDedicatedPermissionAndNotAnyPosTerminalPrefix()
    {
        var controllerType = typeof(PosDailyClosesController);

        var authorize = controllerType.GetCustomAttributes<AuthorizeAttribute>(inherit: false).ToList();
        var route = Assert.Single(controllerType.GetCustomAttributes<RouteAttribute>(inherit: false));

        var policy = Assert.Single(authorize).Policy;
        Assert.Equal(Permissions.DailyCloseRecords.View, policy);
        Assert.Equal("DailyCloseRecords.View", policy);
        // 不能借用收银机上的日结权限，也不能带 PosTerminal 前缀（会被当作收银机权限下发）。
        Assert.DoesNotContain("PosTerminal", policy, StringComparison.Ordinal);
        Assert.Equal("api/react/v1/pos-daily-closes", route.Template);
        Assert.Empty(controllerType.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Fact]
    public void EveryHttpActionIsReadOnlyAndNotAnonymousOrOverridingPolicy()
    {
        var actions = typeof(PosDailyClosesController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: false).Any())
            .ToList();

        Assert.Equal(2, actions.Count);
        Assert.All(actions, method =>
        {
            var verbs = method.GetCustomAttributes<HttpMethodAttribute>(inherit: false)
                .SelectMany(attribute => attribute.HttpMethods)
                .ToList();
            Assert.Equal(["GET"], verbs);
            Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: false));
            // 方法级不再覆盖策略，整个控制器只认 DailyCloseRecords.View。
            Assert.Empty(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        });
    }

    [Fact]
    public void DetailRouteConstrainsGuid()
    {
        var detail = typeof(PosDailyClosesController).GetMethod(nameof(PosDailyClosesController.GetDetail))!;

        var template = Assert.Single(detail.GetCustomAttributes<HttpGetAttribute>(inherit: false)).Template;

        Assert.Equal("{dailyCloseGuid:guid}", template);
    }

    [Fact]
    public async Task GetList_ReturnsOkWithServiceResult()
    {
        var service = new Mock<IDailyCloseQueryService>();
        var expected = new DailyCloseListResultDto { Total = 3, Page = 1, PageSize = 20 };
        service
            .Setup(item => item.GetListAsync(It.IsAny<DailyCloseQueryDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var response = await new PosDailyClosesController(service.Object)
            .GetList(new DailyCloseQueryDto(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<ApiResponse<DailyCloseListResultDto>>(ok.Value);
        Assert.True(body.Success);
        Assert.Same(expected, body.Data);
    }

    [Fact]
    public async Task GetList_MapsInvalidQueryTo400WithCode()
    {
        var service = new Mock<IDailyCloseQueryService>();
        service
            .Setup(item => item.GetListAsync(It.IsAny<DailyCloseQueryDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DailyCloseRequestException("INVALID_QUERY", "营业日期区间最长 93 天，请缩小范围。"));

        var response = await new PosDailyClosesController(service.Object)
            .GetList(new DailyCloseQueryDto(), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(response.Result);
        var body = Assert.IsType<ApiResponse<DailyCloseListResultDto>>(bad.Value);
        Assert.False(body.Success);
        Assert.Equal("INVALID_QUERY", body.ErrorCode);
    }

    [Fact]
    public async Task GetDetail_Returns404WhenServiceReturnsNullAndOkOtherwise()
    {
        var service = new Mock<IDailyCloseQueryService>();
        var found = Guid.NewGuid();
        service
            .Setup(item => item.GetDetailAsync(found, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DailyCloseDetailDto { DailyCloseGuid = found });
        service
            .Setup(item => item.GetDetailAsync(It.Is<Guid>(id => id != found), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DailyCloseDetailDto?)null);
        var controller = new PosDailyClosesController(service.Object);

        var ok = await controller.GetDetail(found, CancellationToken.None);
        var missing = await controller.GetDetail(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(ok.Result);
        var notFound = Assert.IsType<NotFoundObjectResult>(missing.Result);
        var body = Assert.IsType<ApiResponse<DailyCloseDetailDto>>(notFound.Value);
        Assert.Equal("NOT_FOUND", body.ErrorCode);
    }
}

/// <summary>日结记录权限：独立顶层码、种子、角色模板与入库脚本口径。</summary>
public sealed class DailyCloseRecordsPermissionTests
{
    private const string Code = "DailyCloseRecords.View";

    [Fact]
    public void PermissionCode_IsTopLevelAndNotUnderPosTerminalPrefix()
    {
        Assert.Equal(Code, Permissions.DailyCloseRecords.View);
        // 带 Permissions.PosTerminal. 前缀的码会被当作收银机权限下发。
        Assert.False(Code.StartsWith("Permissions.PosTerminal.", StringComparison.OrdinalIgnoreCase));
        // 与收银机上的日结权限是两回事。
        Assert.NotEqual(Permissions.PosTerminal.DailyClose.View, Permissions.DailyCloseRecords.View);
    }

    [Fact]
    public void Seed_RegistersPermissionWithExpectedNameCategoryAndDescription()
    {
        var seed = Assert.Single(PermissionSeedData.AllPermissions, item => item.Code == Code);

        Assert.Equal("查看日结记录", seed.Name);
        Assert.Equal("POS 日结", seed.Category);
        Assert.Contains("/pos-admin/daily-closes", seed.Description, StringComparison.Ordinal);
        Assert.Contains("员工 App", seed.Description, StringComparison.Ordinal);
        Assert.Contains(Permissions.GetAllPermissions(), item => item.Code == Code);
    }

    [Fact]
    public void Seed_DoesNotRegisterAnyPosTerminalPrefixedCodeForDailyCloseRecords()
    {
        Assert.DoesNotContain(
            PermissionSeedData.AllPermissions,
            item => item.Code.StartsWith("Permissions.PosTerminal.", StringComparison.OrdinalIgnoreCase)
                && item.Code.Contains("DailyCloseRecords", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StoreManagerAndChineseStoreManagerTemplatesContainItButManagerAndOthersDoNot()
    {
        var templates = PermissionSeedData.RolePermissionTemplates;

        Assert.Contains(Code, TemplateOf(templates, "StoreManager").PermissionCodes);
        Assert.Contains(Code, TemplateOf(templates, "店长").PermissionCodes);
        // 经理由管理员按需在角色管理里再授，模板不含。
        Assert.DoesNotContain(Code, TemplateOf(templates, "经理").PermissionCodes);
        foreach (var other in templates.Where(item => item.RoleName is not ("StoreManager" or "店长")))
        {
            Assert.DoesNotContain(Code, other.PermissionCodes);
        }
    }

    [Fact]
    public void ChineseStoreManagerTemplateOnlyAddsDailyCloseOverManagerTemplate()
    {
        var templates = PermissionSeedData.RolePermissionTemplates;

        var manager = TemplateOf(templates, "经理").PermissionCodes;
        var storeManagerCn = TemplateOf(templates, "店长").PermissionCodes;

        Assert.Equal(manager.Append(Code).Order(StringComparer.Ordinal), storeManagerCn.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PolicyProvider_ResolvesPolicyNameAsPermissionRequirement()
    {
        // 策略由 PermissionPolicyProvider 按名称自动生成（策略名即权限码），不需要显式 AddPolicy。
        var provider = new PermissionPolicyProvider(Options.Create(new AuthorizationOptions()));

        var policy = await provider.GetPolicyAsync(Permissions.DailyCloseRecords.View);

        Assert.NotNull(policy);
        var requirement = Assert.Single(policy.Requirements.OfType<PermissionRequirement>());
        Assert.Equal(Code, requirement.Permission);
    }

    [Fact]
    public void MigrationScript_RegistersOnlyThisPermissionAndGrantsOnlyStoreManagerAndChineseStoreManager()
    {
        var script = File.ReadAllText(ResolveMigrationPath());

        Assert.Contains("N'DailyCloseRecords.View'", script, StringComparison.Ordinal);
        Assert.Contains("N'POS 日结'", script, StringComparison.Ordinal);
        Assert.Contains("@Actor nvarchar(100) = N'Migration_20261007_DailyCloseRecords'", script, StringComparison.Ordinal);
        Assert.Contains("DB_NAME() <> N'HBweb'", script, StringComparison.Ordinal);
        Assert.Contains("BEGIN TRANSACTION", script, StringComparison.Ordinal);
        Assert.Contains("SET XACT_ABORT ON", script, StringComparison.Ordinal);
        // 角色只有 StoreManager 与 店长；不含 经理（注释里的说明不带 N'' 引号）。
        Assert.Contains("(N'StoreManager'), (N'店长')", script, StringComparison.Ordinal);
        Assert.DoesNotContain("N'经理'", script, StringComparison.Ordinal);
        // 只写权限表与角色授予表，不碰用户直授权限。
        Assert.DoesNotContain("HbwebSysUserPermissions", script, StringComparison.OrdinalIgnoreCase);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, @"INSERT INTO dbo\.HbwebSysPermissions\b"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, @"INSERT INTO dbo\.HBwebSysRolePermissions\b"));
        // 不能登记任何带 PosTerminal 前缀的码（注释里的说明不带 N'' 引号）。
        Assert.DoesNotContain("N'Permissions.PosTerminal", script, StringComparison.Ordinal);
    }

    private static RolePermissionTemplateDefinition TemplateOf(
        IReadOnlyList<RolePermissionTemplateDefinition> templates,
        string roleName) =>
        Assert.Single(templates, item => item.RoleName == roleName);

    private static string ResolveMigrationPath([System.Runtime.CompilerServices.CallerFilePath] string testFilePath = "")
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("无法解析测试文件目录");
        return Path.GetFullPath(
            Path.Combine(
                testDirectory,
                "..",
                "BlazorApp.Api",
                "Data",
                "Migrations",
                "20261007_AddDailyCloseRecordsPermission.sql"));
    }
}
