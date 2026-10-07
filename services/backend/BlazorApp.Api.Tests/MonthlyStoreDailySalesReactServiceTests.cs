using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 月度日销售下载页的数据口径：营业额取分店日统计，刷卡/现金取 POSM 支付明细，
/// 两边对不上（旧系统来源）时只给营业额、拆分留空；缺数留空而不是 0。
/// </summary>
public sealed class MonthlyStoreDailySalesReactServiceTests : IDisposable
{
    private const int Cash = 1;
    private const int Card = 2;
    private const int Voucher = 3;

    private readonly string _localDbPath;
    private readonly string _posmDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _posmConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarClient _posmDb;
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public MonthlyStoreDailySalesReactServiceTests()
    {
        _localDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _posmDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _localConnection = new SqliteConnection($"Data Source={_localDbPath}");
        _posmConnection = new SqliteConnection($"Data Source={_posmDbPath}");
        _localConnection.Open();
        _posmConnection.Open();
        _localDb = new SqlSugarClient(CreateConfig(_localConnection.ConnectionString));
        _posmDb = new SqlSugarClient(CreateConfig(_posmConnection.ConnectionString));
        _localDb.CodeFirst.InitTables(typeof(StoreSalesStatistic), typeof(SalesStatisticRefreshState));
        _posmDb.CodeFirst.InitTables(typeof(SalesOrder), typeof(PaymentDetail), typeof(POSM_设备注册信息表));
    }

    [Fact]
    public async Task 刷卡现金其他与营业额逐日对上_退款按净额_非支付状态订单不计入()
    {
        await SeedFreshStatesAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 3));
        // 9-1：已支付单 现金30+刷卡60+代金券10，分期付单 刷卡40，已取消单 刷卡999（不计入）
        await SeedOrderAsync("o1", new DateTime(2026, 9, 1, 9, 0, 0), "S1", null, 1, (Cash, 30m), (Card, 60m), (Voucher, 10m));
        await SeedOrderAsync("o2", new DateTime(2026, 9, 1, 10, 0, 0), "S1", null, 4, (Card, 40m));
        await SeedOrderAsync("o3", new DateTime(2026, 9, 1, 11, 0, 0), "S1", null, 2, (Card, 999m));
        await SeedStatAsync("2026-09-01", "S1", "分店一", 140m);
        // 9-2：退款支付是负数，按净额进入对应支付方式
        await SeedOrderAsync("o4", new DateTime(2026, 9, 2, 9, 0, 0), "S1", null, 1, (Card, 50m), (Cash, -20m));
        await SeedStatAsync("2026-09-02", "S1", "分店一", 30m);

        var result = await CreateService().GetMonthlyStoreDailySalesAsync("2026-09", null, false, default);

        var store = Assert.Single(result.Stores);
        Assert.Equal("S1", store.BranchCode);
        Assert.Equal("分店一", store.BranchName);
        Assert.Equal("2026-09-30", result.CountedThroughDate);
        Assert.Equal(30, result.CountedDays);
        Assert.Equal(30, store.Days.Count);

        var day1 = store.Days[0];
        Assert.Equal("2026-09-01", day1.Date);
        Assert.Equal(140m, day1.Revenue);
        Assert.Equal(100m, day1.Card);
        Assert.Equal(30m, day1.Cash);
        Assert.Equal(10m, day1.Other);

        var day2 = store.Days[1];
        Assert.Equal(30m, day2.Revenue);
        Assert.Equal(50m, day2.Card);
        Assert.Equal(-20m, day2.Cash);
        Assert.Equal(0m, day2.Other);

        // 9-3 没有销售：状态已发布 → 休业日是 0，不是缺数
        var day3 = store.Days[2];
        Assert.Equal(0m, day3.Revenue);
        Assert.Equal(0m, day3.Card);
        Assert.Equal(0m, day3.Cash);
        Assert.Equal(0m, day3.Other);
    }

    [Fact]
    public async Task 营业额含旧系统来源时只给营业额_刷卡现金其他留空()
    {
        await SeedFreshStatesAsync(new DateTime(2026, 4, 1), new DateTime(2026, 4, 1));
        // 统计营业额 500（HBSales 旧系统 300 + POSM 200），POSM 支付合计只有 200：拆分不可靠
        await SeedOrderAsync("o1", new DateTime(2026, 4, 1, 9, 0, 0), "S1", null, 1, (Cash, 50m), (Card, 150m));
        await SeedStatAsync("2026-04-01", "S1", "分店一", 500m);

        var result = await CreateService().GetMonthlyStoreDailySalesAsync("2026-04", null, false, default);

        var day = Assert.Single(result.Stores).Days[0];
        Assert.Equal(500m, day.Revenue);
        Assert.Null(day.Card);
        Assert.Null(day.Cash);
        Assert.Null(day.Other);
    }

    [Fact]
    public async Task 统计未发布的日子营业额为空_已发布无销售的日子为零()
    {
        // 9-5 状态 Pending；9-6 Running 且全店没有统计行（没有上一版可展示）；9-7 状态缺失但早于最新有状态日期
        await SeedFreshStatesAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 4));
        await SeedStateAsync("2026-09-05", SalesStatisticRefreshStatus.Pending, lastAggregated: DateTime.UtcNow, completed: null);
        await SeedStateAsync("2026-09-06", SalesStatisticRefreshStatus.Running, lastAggregated: DateTime.UtcNow, completed: null);
        await SeedFreshStatesAsync(new DateTime(2026, 9, 8), new DateTime(2026, 9, 9));
        await SeedStatAsync("2026-09-01", "S1", "分店一", 10m);
        await SeedOrderAsync("o1", new DateTime(2026, 9, 1, 9, 0, 0), "S1", null, 1, (Cash, 10m));

        var result = await CreateService().GetMonthlyStoreDailySalesAsync("2026-09", null, false, default);

        var days = Assert.Single(result.Stores).Days;
        Assert.Equal(10m, days[0].Revenue);
        Assert.Equal(0m, days[1].Revenue); // 9-2 已发布、无销售
        Assert.Null(days[4].Revenue);      // 9-5 Pending
        Assert.Null(days[4].Card);
        Assert.Null(days[5].Revenue);      // 9-6 Running 且没有任何统计行
        Assert.Equal(0m, days[6].Revenue); // 9-7 无状态行、早于最新状态日期 → 历史已发布
        Assert.Equal(0m, days[7].Revenue);
        // 9-10 起没有状态行、也晚于最新有状态日期（9-9）→ 尚未发布
        Assert.Null(days[9].Revenue);
        Assert.Null(days[29].Revenue);
    }

    [Fact]
    public async Task 当月进行中只计入到昨天_未来月份没有已计入日期()
    {
        await SeedFreshStatesAsync(new DateTime(2026, 10, 1), new DateTime(2026, 10, 6));
        await SeedStatAsync("2026-10-01", "S1", "分店一", 20m);
        await SeedOrderAsync("o1", new DateTime(2026, 10, 1, 9, 0, 0), "S1", null, 1, (Card, 20m));
        var service = CreateService(); // 今天 = 2026-10-07

        var october = await service.GetMonthlyStoreDailySalesAsync("2026-10", null, false, default);
        var november = await service.GetMonthlyStoreDailySalesAsync("2026-11", null, false, default);

        Assert.Equal(31, october.DaysInMonth);
        Assert.Equal("2026-10-06", october.CountedThroughDate);
        Assert.Equal(6, october.CountedDays);
        var days = Assert.Single(october.Stores).Days;
        Assert.Equal(6, days.Count);
        Assert.Equal("2026-10-06", days[^1].Date);
        Assert.Equal(20m, days[0].Card);

        Assert.Null(november.CountedThroughDate);
        Assert.Equal(0, november.CountedDays);
        Assert.Empty(november.Stores);
    }

    [Fact]
    public async Task 只返回授权范围内的分店_空范围没有分店()
    {
        await SeedFreshStatesAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 1));
        await SeedStatAsync("2026-09-01", "S1", "分店一", 0m);
        await SeedStatAsync("2026-09-01", "S2", "分店二", 0m);
        await SeedStatAsync("2026-09-01", "S3", "分店三", 0m);
        var service = CreateService();

        var scoped = await service.GetMonthlyStoreDailySalesAsync("2026-09", new[] { "s3", "S1" }, false, default);
        var none = await service.GetMonthlyStoreDailySalesAsync("2026-09", Array.Empty<string>(), false, default);
        var all = await service.GetMonthlyStoreDailySalesAsync("2026-09", null, false, default);

        Assert.Equal(new[] { "S1", "S3" }, scoped.Stores.Select(store => store.BranchCode));
        Assert.Empty(none.Stores);
        Assert.Equal(30, none.CountedDays); // 范围为空仍给出月份信息，只是没有分店
        Assert.Equal(new[] { "S1", "S2", "S3" }, all.Stores.Select(store => store.BranchCode));
    }

    [Fact]
    public async Task 授权范围不影响某天是否已发布的判断()
    {
        // 9-1 只有 S2 有统计行；只看 S1 的用户，9-1 仍应是「已发布、无销售」而不是缺数
        await SeedStateAsync("2026-09-01", SalesStatisticRefreshStatus.Queued, lastAggregated: DateTime.UtcNow, completed: null);
        await SeedStatAsync("2026-09-01", "S2", "分店二", 5m);
        await SeedStatAsync("2026-09-02", "S1", "分店一", 0m);

        var result = await CreateService().GetMonthlyStoreDailySalesAsync("2026-09", new[] { "S1" }, false, default);

        var days = Assert.Single(result.Stores).Days;
        Assert.Equal(0m, days[0].Revenue);
    }

    [Fact]
    public async Task 订单没有分店编码时按设备注册表回填分店()
    {
        await SeedFreshStatesAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 1));
        await _posmDb.Insertable(new POSM_设备注册信息表
        {
            系统设备编号 = "D1",
            设备硬件识别码 = "D1-hardware",
            分店代码 = "S1",
            设备类型 = "POS",
            设备系统 = "Windows",
            设备状态 = 1,
            设备授权码 = "D1-auth",
        }).ExecuteCommandAsync();
        await SeedOrderAsync("o1", new DateTime(2026, 9, 1, 9, 0, 0), null, "d1", 1, (Card, 80m));
        await SeedStatAsync("2026-09-01", "S1", "分店一", 80m);

        var result = await CreateService().GetMonthlyStoreDailySalesAsync("2026-09", null, false, default);

        var day = Assert.Single(result.Stores).Days[0];
        Assert.Equal(80m, day.Revenue);
        Assert.Equal(80m, day.Card);
        Assert.Equal(0m, day.Cash);
    }

    [Fact]
    public async Task 刷卡现金汇总缓存_强制刷新才重新读取POSM()
    {
        await SeedFreshStatesAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 1));
        await SeedStatAsync("2026-09-01", "S1", "分店一", 60m);
        await SeedOrderAsync("o1", new DateTime(2026, 9, 1, 9, 0, 0), "S1", null, 1, (Card, 60m));
        var service = CreateService();

        var first = await service.GetMonthlyStoreDailySalesAsync("2026-09", null, false, default);
        await _posmDb.Deleteable<PaymentDetail>().ExecuteCommandAsync();
        var cached = await service.GetMonthlyStoreDailySalesAsync("2026-09", null, false, default);
        var refreshed = await service.GetMonthlyStoreDailySalesAsync("2026-09", null, true, default);

        Assert.Equal(60m, first.Stores[0].Days[0].Card);
        Assert.Equal(60m, cached.Stores[0].Days[0].Card);
        // 强制刷新后 POSM 已没有支付，与营业额 60 对不上 → 拆分留空
        Assert.Null(refreshed.Stores[0].Days[0].Card);
    }

    [Fact]
    public async Task 分店名取最近一天的名称_分店按编码排序()
    {
        await SeedFreshStatesAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 2));
        await SeedStatAsync("2026-09-01", "B2", "旧名", 0m);
        await SeedStatAsync("2026-09-02", "B2", "新名", 0m);
        await SeedStatAsync("2026-09-01", "A1", "甲店", 0m);

        var result = await CreateService().GetMonthlyStoreDailySalesAsync("2026-09", null, false, default);

        Assert.Equal(new[] { "A1", "B2" }, result.Stores.Select(store => store.BranchCode));
        Assert.Equal("新名", result.Stores[1].BranchName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-13")]
    [InlineData("2026-9-1")]
    [InlineData("abc")]
    [InlineData("2019-12")]
    public async Task 月份格式无效抛出参数异常(string month)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateService().GetMonthlyStoreDailySalesAsync(month, null, false, default));
    }

    private MonthlyStoreDailySalesReactService CreateService() =>
        new(CreateContext<SqlSugarContext>(_localDb), CreateContext<POSMSqlSugarContext>(_posmDb), _cache)
        {
            BusinessTodayProvider = () => new DateTime(2026, 10, 7),
        };

    private async Task SeedStatAsync(string date, string branchCode, string branchName, decimal amount)
    {
        await _localDb.Insertable(new StoreSalesStatistic
        {
            Date = DateTime.Parse(date),
            BranchCode = branchCode,
            BranchName = branchName,
            TotalAmount = amount,
        }).ExecuteCommandAsync();
    }

    private async Task SeedFreshStatesAsync(DateTime from, DateTime to)
    {
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            await SeedStateAsync(
                day.ToString("yyyy-MM-dd"),
                SalesStatisticRefreshStatus.Fresh,
                lastAggregated: DateTime.UtcNow,
                completed: DateTime.UtcNow);
        }
    }

    private async Task SeedStateAsync(string date, string status, DateTime? lastAggregated, DateTime? completed)
    {
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.StoreSales,
            Date = DateTime.Parse(date),
            Status = status,
            LastAggregatedAtUtc = lastAggregated,
            CompletedAtUtc = completed,
        }).ExecuteCommandAsync();
    }

    private async Task SeedOrderAsync(
        string orderGuid,
        DateTime orderTime,
        string? branchCode,
        string? deviceCode,
        int status,
        params (int Method, decimal Amount)[] payments
    )
    {
        await _posmDb.Insertable(new SalesOrder
        {
            OrderGuid = orderGuid,
            OrderTime = orderTime,
            BranchCode = branchCode,
            DeviceCode = deviceCode,
            Status = status,
        }).ExecuteCommandAsync();
        foreach (var (method, amount) in payments)
        {
            await _posmDb.Insertable(new PaymentDetail
            {
                PaymentGuid = Guid.NewGuid().ToString(),
                OrderGuid = orderGuid,
                PaymentMethod = method,
                Amount = amount,
            }).ExecuteCommandAsync();
        }
    }

    private static ConnectionConfig CreateConfig(string connectionString) => new()
    {
        ConnectionString = connectionString,
        DbType = DbType.Sqlite,
        IsAutoCloseConnection = false,
        InitKeyType = InitKeyType.Attribute,
    };

    /// <summary>数据上下文的构造函数要真实连接串；测试直接把已建好的 SQLite 客户端写进私有字段。</summary>
    private static TContext CreateContext<TContext>(ISqlSugarClient db)
        where TContext : class
    {
        var context = (TContext)RuntimeHelpers.GetUninitializedObject(typeof(TContext));
        typeof(TContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _cache.Dispose();
        _localConnection.Dispose();
        _posmConnection.Dispose();
        if (File.Exists(_localDbPath)) SqliteTempFileCleanup.DeleteIfExists(_localDbPath);
        if (File.Exists(_posmDbPath)) SqliteTempFileCleanup.DeleteIfExists(_posmDbPath);
    }
}
