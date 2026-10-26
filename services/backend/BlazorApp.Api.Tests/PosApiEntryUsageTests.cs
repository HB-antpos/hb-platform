using System.Reflection;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.PosApiEntry;
using BlazorApp.Api.Services.PosApiEntry;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 收银入口用量统计（主 API 侧）：日期 / 粒度 / 范围校验、周（周一起算）与月（自然月）分桶、
/// 入口占比与 4xx / 5xx 汇总、分店明细、分店范围收敛、控制器路由与权限。
/// 用 SQLite 承载只读实体；真实 SQL Server 上的建表迁移见 SchemaMigrationSqlServerIntegrationTests。
/// </summary>
public sealed class PosApiEntryUsageTests : IDisposable
{
    // 2026-10-24 04:00 UTC＝悉尼 15:00（夏令时 UTC+11），悉尼「今天」是 2026-10-24（周六）。
    private static readonly DateTime Now = new(2026, 10, 24, 4, 0, 0, DateTimeKind.Utc);

    private readonly string _mainPath = Path.Combine(Path.GetTempPath(), $"hb-posusage-main-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _mainDb;
    private readonly FakeScope _scope = new() { Scope = Scope(isAdmin: true) };
    private readonly PosApiEntryUsageService _service;

    public PosApiEntryUsageTests()
    {
        _mainDb = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_mainPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _mainDb.CodeFirst.InitTables<Store>();
        _mainDb.CodeFirst.InitTables<PosApiEntryUsageDaily>();
        _service = new PosApiEntryUsageService(_mainDb, _scope, new FixedTimeProvider(Now));
    }

    public void Dispose()
    {
        _mainDb.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_mainPath);
    }

    // ───────────────────────── 参数校验 ─────────────────────────

    [Fact]
    public async Task 未传日期时默认最近30天含今天_粒度默认按日()
    {
        var result = await _service.GetUsageAsync(null, null, null, null);

        Assert.True(result.Success);
        Assert.Equal("2026-09-25", result.Data!.StartDate);
        Assert.Equal("2026-10-24", result.Data.EndDate);
        Assert.Equal("day", result.Data.Granularity);
        Assert.Equal("Australia/Sydney", result.Data.TimeZone);
        Assert.Equal(30, result.Data.Buckets.Count);
    }

    [Fact]
    public async Task 只传一端时另一端按30天推算()
    {
        var onlyStart = await _service.GetUsageAsync("2026-10-01", null, "day", null);
        Assert.Equal("2026-10-30", onlyStart.Data!.EndDate);

        var onlyEnd = await _service.GetUsageAsync(null, "2026-10-30", "day", null);
        Assert.Equal("2026-10-01", onlyEnd.Data!.StartDate);
    }

    [Theory]
    [InlineData("2026/10/01", "2026-10-10")]
    [InlineData("2026-10-1", "2026-10-10")]
    [InlineData("2026-13-01", "2026-10-10")]
    [InlineData("not-a-date", "2026-10-10")]
    [InlineData("2026-10-01", "2026-02-30")]
    // 窗口外的日期（0001-01-01、1999 年、3000 年）一律当非法日期，不能让 AddDays 越界抛异常。
    [InlineData("0001-01-01", "2026-10-10")]
    [InlineData("1999-12-31", "2026-10-10")]
    [InlineData("2026-10-01", "3000-01-01")]
    public async Task 日期格式非法或越出窗口_返回400类错误(string start, string end)
    {
        var result = await _service.GetUsageAsync(start, end, "day", null);

        Assert.False(result.Success);
        Assert.Equal(PosApiEntryUsageErrorCodes.InvalidDate, result.ErrorCode);
        Assert.Equal(400, PosApiEntryUsageController.MapStatusCode(result.ErrorCode));
    }

    [Fact]
    public async Task 开始晚于结束_报范围错误()
    {
        var result = await _service.GetUsageAsync("2026-10-10", "2026-10-09", "day", null);

        Assert.Equal(PosApiEntryUsageErrorCodes.InvalidRange, result.ErrorCode);
    }

    [Fact]
    public async Task 范围最多400天_含首尾()
    {
        // 2025-09-20 ~ 2026-10-24 共 400 天（含首尾）：允许；再多一天拒绝。
        var ok = await _service.GetUsageAsync("2025-09-20", "2026-10-24", "month", null);
        Assert.True(ok.Success);

        var tooLarge = await _service.GetUsageAsync("2025-09-19", "2026-10-24", "month", null);
        Assert.False(tooLarge.Success);
        Assert.Equal(PosApiEntryUsageErrorCodes.RangeTooLarge, tooLarge.ErrorCode);
    }

    [Theory]
    [InlineData("hour")]
    [InlineData("year")]
    [InlineData("1")]
    public async Task 粒度只认day_week_month(string granularity)
    {
        var result = await _service.GetUsageAsync("2026-10-01", "2026-10-10", granularity, null);

        Assert.Equal(PosApiEntryUsageErrorCodes.InvalidGranularity, result.ErrorCode);
    }

    [Fact]
    public async Task 粒度大小写和空白被归一化()
    {
        var result = await _service.GetUsageAsync("2026-10-01", "2026-10-10", " WEEK ", null);

        Assert.True(result.Success);
        Assert.Equal("week", result.Data!.Granularity);
    }

    // ───────────────────────── 分桶 ─────────────────────────

    [Fact]
    public void 周从周一算起_周日归上一周()
    {
        // 2026-10-19 是周一；10-25 周日仍属该周，10-26 周一进入下一周。
        Assert.Equal(new DateOnly(2026, 10, 19), PosApiEntryUsageService.BucketStartOf(new DateOnly(2026, 10, 19), "week"));
        Assert.Equal(new DateOnly(2026, 10, 19), PosApiEntryUsageService.BucketStartOf(new DateOnly(2026, 10, 25), "week"));
        Assert.Equal(new DateOnly(2026, 10, 26), PosApiEntryUsageService.BucketStartOf(new DateOnly(2026, 10, 26), "week"));
        Assert.Equal(new DateOnly(2026, 10, 25), PosApiEntryUsageService.BucketEndOf(new DateOnly(2026, 10, 19), "week"));
    }

    [Fact]
    public void 月按自然月_月末含闰年二月()
    {
        Assert.Equal(new DateOnly(2026, 10, 1), PosApiEntryUsageService.BucketStartOf(new DateOnly(2026, 10, 31), "month"));
        Assert.Equal(new DateOnly(2026, 10, 31), PosApiEntryUsageService.BucketEndOf(new DateOnly(2026, 10, 1), "month"));
        Assert.Equal(new DateOnly(2028, 2, 29), PosApiEntryUsageService.BucketEndOf(new DateOnly(2028, 2, 1), "month"));
        Assert.Equal(new DateOnly(2026, 12, 31), PosApiEntryUsageService.BucketEndOf(new DateOnly(2026, 12, 1), "month"));
    }

    [Fact]
    public async Task 周粒度_首尾不完整周标partial_并包含没有数据的周()
    {
        // 范围 10-21（周三）~ 11-09（周一）：周一起点为 10-19 / 10-26 / 11-02 / 11-09，共 4 个周。
        Seed("2026-10-21", "S001", "edge", 5);
        Seed("2026-10-25", "S001", "edge", 7); // 周日，仍在 10-19 那一周
        Seed("2026-11-09", "S001", "aws", 3);

        var result = (await _service.GetUsageAsync("2026-10-21", "2026-11-09", "week", null)).Data!;

        Assert.Equal(["2026-10-19", "2026-10-26", "2026-11-02", "2026-11-09"], result.Buckets.Select(b => b.BucketStart));
        Assert.Equal(12, result.Buckets[0].Total.Requests);
        Assert.Equal(0, result.Buckets[1].Total.Requests);
        Assert.Equal(0, result.Buckets[2].Total.Requests);
        Assert.Equal(3, result.Buckets[3].Total.Requests);
        // 首周（10-19 起）和末周（11-09 起、到 11-15）都没被范围完整覆盖；中间两周完整。
        Assert.Equal([true, false, false, true], result.Buckets.Select(b => b.Partial));
        Assert.Equal("2026-10-25", result.Buckets[0].BucketEnd);
    }

    [Fact]
    public async Task 月粒度_跨月汇总且整月完整的桶不标partial()
    {
        Seed("2026-09-30", "S001", "edge", 1);
        Seed("2026-10-01", "S001", "edge", 2);
        Seed("2026-10-31", "S001", "aws", 4);
        Seed("2026-11-01", "S001", "direct", 8);

        var result = (await _service.GetUsageAsync("2026-09-30", "2026-11-01", "month", null)).Data!;

        Assert.Equal(["2026-09-01", "2026-10-01", "2026-11-01"], result.Buckets.Select(b => b.BucketStart));
        Assert.Equal([1L, 6L, 8L], result.Buckets.Select(b => b.Total.Requests));
        Assert.Equal([true, false, true], result.Buckets.Select(b => b.Partial));
    }

    // ───────────────────────── 汇总口径 ─────────────────────────

    [Fact]
    public async Task 每个桶固定输出四个入口_占比与4xx_5xx按入口汇总()
    {
        Seed("2026-10-20", "S001", "edge", 60, 3, 1);
        Seed("2026-10-20", "S002", "edge", 20, 1, 0);
        Seed("2026-10-20", "S001", "aws", 15, 0, 0);
        Seed("2026-10-20", "S001", "direct", 5, 0, 2);

        var result = (await _service.GetUsageAsync("2026-10-20", "2026-10-20", "day", null)).Data!;

        var bucket = Assert.Single(result.Buckets);
        Assert.Equal(["edge", "aws", "direct", "unknown"], bucket.Entries.Select(e => e.Entry));
        Assert.Equal(new[] { 80L, 15L, 5L, 0L }, bucket.Entries.Select(e => e.Requests).ToArray());
        Assert.Equal(new[] { 0.8, 0.15, 0.05, 0.0 }, bucket.Entries.Select(e => Math.Round(e.Share, 4)).ToArray());
        Assert.Equal(new[] { 4L, 0L, 0L, 0L }, bucket.Entries.Select(e => e.Status4xx).ToArray());
        Assert.Equal(new[] { 1L, 0L, 2L, 0L }, bucket.Entries.Select(e => e.Status5xx).ToArray());
        Assert.Equal((100L, 4L, 3L), (bucket.Total.Requests, bucket.Total.Status4xx, bucket.Total.Status5xx));
        // 范围合计与单桶一致。
        Assert.Equal(100, result.Total.Requests);
        Assert.Equal(0.8, Math.Round(result.Entries[0].Share, 4));
    }

    [Fact]
    public async Task 没有数据时占比为0_不出现NaN()
    {
        var result = (await _service.GetUsageAsync("2026-10-01", "2026-10-02", "day", null)).Data!;

        Assert.All(result.Buckets, bucket => Assert.All(bucket.Entries, entry => Assert.Equal(0d, entry.Share)));
        Assert.All(result.Entries, entry => Assert.Equal(0d, entry.Share));
        Assert.Empty(result.Stores);
    }

    [Fact]
    public async Task 库里的非法入口值并入unknown_不丢请求数()
    {
        // CHECK 约束正常不会放行脏值；这里模拟约束被绕过的历史数据，展示口径仍要守恒。
        Seed("2026-10-20", "S001", "tencent", 9);
        Seed("2026-10-20", "S001", "unknown", 1);

        var result = (await _service.GetUsageAsync("2026-10-20", "2026-10-20", "day", null)).Data!;

        Assert.Equal(10, result.Entries.Single(e => e.Entry == "unknown").Requests);
        Assert.Equal(10, result.Total.Requests);
    }

    [Fact]
    public async Task 只汇总范围内的日期()
    {
        Seed("2026-10-09", "S001", "edge", 100);
        Seed("2026-10-10", "S001", "edge", 1);
        Seed("2026-10-11", "S001", "edge", 2);
        Seed("2026-10-12", "S001", "edge", 100);

        var result = (await _service.GetUsageAsync("2026-10-10", "2026-10-11", "day", null)).Data!;

        Assert.Equal(3, result.Total.Requests);
    }

    // ───────────────────────── 分店明细与范围 ─────────────────────────

    [Fact]
    public async Task 分店明细按请求数降序_带分店名_空串分店表示未归属()
    {
        SeedStore("S001", "旗舰店");
        SeedStore("S002", "二店");
        Seed("2026-10-20", "S001", "edge", 10);
        Seed("2026-10-20", "S002", "aws", 40);
        Seed("2026-10-20", "", "unknown", 5);

        var stores = (await _service.GetUsageAsync("2026-10-20", "2026-10-20", "day", null)).Data!.Stores;

        Assert.Equal(["S002", "S001", ""], stores.Select(s => s.StoreCode));
        Assert.Equal(["二店", "旗舰店", ""], stores.Select(s => s.StoreName));
        Assert.Equal(40, stores[0].Entries.Single(e => e.Entry == "aws").Requests);
    }

    [Fact]
    public async Task 指定分店时只统计该分店()
    {
        Seed("2026-10-20", "S001", "edge", 10);
        Seed("2026-10-20", "S002", "edge", 90);
        Seed("2026-10-20", "", "unknown", 7);

        var result = (await _service.GetUsageAsync("2026-10-20", "2026-10-20", "day", " S001 ")).Data!;

        Assert.Equal("S001", result.StoreCode);
        Assert.Equal(10, result.Total.Requests);
        Assert.Equal(["S001"], result.Stores.Select(s => s.StoreCode));
    }

    [Fact]
    public async Task 非管理员只看到可管理分店_看不到未归属分店的请求()
    {
        Seed("2026-10-20", "S001", "edge", 10);
        Seed("2026-10-20", "S002", "edge", 90);
        Seed("2026-10-20", "", "unknown", 7);
        _scope.Scope = Scope(isAdmin: false, storeCodes: ["S001"]);

        var result = (await _service.GetUsageAsync("2026-10-20", "2026-10-20", "day", null)).Data!;

        Assert.Equal(10, result.Total.Requests);
        Assert.Equal(["S001"], result.Stores.Select(s => s.StoreCode));
    }

    [Fact]
    public async Task 非管理员指定越权分店_拒绝且映射403()
    {
        Seed("2026-10-20", "S002", "edge", 90);
        _scope.Scope = Scope(isAdmin: false, storeCodes: ["S001"]);

        var result = await _service.GetUsageAsync("2026-10-20", "2026-10-20", "day", "S002");

        Assert.False(result.Success);
        Assert.Equal(PosApiEntryErrorCodes.StoreForbidden, result.ErrorCode);
        Assert.Equal(403, PosApiEntryUsageController.MapStatusCode(result.ErrorCode));
    }

    [Fact]
    public async Task 非管理员没有可管理分店_返回空结果而不是全部()
    {
        Seed("2026-10-20", "S001", "edge", 10);
        _scope.Scope = Scope(isAdmin: false, storeCodes: []);

        var result = await _service.GetUsageAsync("2026-10-20", "2026-10-20", "day", null);

        Assert.True(result.Success);
        Assert.Equal(0, result.Data!.Total.Requests);
        Assert.Empty(result.Data.Stores);
    }

    [Fact]
    public async Task 账号没有分店范围权限时拒绝()
    {
        _scope.Scope = new CurrentUserManageableStoreScope { IsAllowed = false, Message = "无权限" };

        var result = await _service.GetUsageAsync(null, null, null, null);

        Assert.False(result.Success);
        Assert.Equal(PosApiEntryErrorCodes.ScopeForbidden, result.ErrorCode);
    }

    // ───────────────────────── 控制器 ─────────────────────────

    [Fact]
    public void Controller_路由与权限沿用设备管理查看权限()
    {
        var route = Assert.Single(typeof(PosApiEntryUsageController).GetCustomAttributes<RouteAttribute>());
        Assert.Equal("api/react/v1/pos-api-entry/usage", route.Template);
        Assert.Contains(typeof(PosApiEntryUsageController).GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy is null);

        var get = typeof(PosApiEntryUsageController).GetMethod(nameof(PosApiEntryUsageController.GetUsage))!;
        Assert.NotNull(get.GetCustomAttribute<HttpGetAttribute>());
        Assert.Equal(Permissions.DeviceRegistration.View, get.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public async Task Controller_成功透传_失败映射状态码()
    {
        Seed("2026-10-20", "S001", "edge", 10);
        var controller = new PosApiEntryUsageController(_service);

        var ok = Assert.IsType<OkObjectResult>(
            await controller.GetUsage("2026-10-20", "2026-10-20", "day", null, CancellationToken.None));
        Assert.Equal(10, Assert.IsType<ApiResponse<PosApiEntryUsageResultDto>>(ok.Value).Data!.Total.Requests);

        var bad = Assert.IsType<ObjectResult>(
            await controller.GetUsage("garbage", null, "day", null, CancellationToken.None));
        Assert.Equal(400, bad.StatusCode);
    }

    // ───────────────────────── 迁移与 Hbpos.Api 写入的约定 ─────────────────────────

    [Fact]
    public void 入口白名单与Hbpos写入端一致_unknown仅用于识别不出的请求()
    {
        Assert.Equal(["edge", "aws", "direct", "unknown"], PosApiEntryUsageService.EntryOrder);
        // 迁移里的 CHECK 约束必须覆盖 Hbpos.Api 会写入的全部入口代号。
        foreach (var entry in PosApiEntryUsageService.EntryOrder)
        {
            Assert.Contains($"N'{entry}'", PosApiEntryUsageSchema.ApplySql, StringComparison.Ordinal);
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    private void Seed(string date, string storeCode, string entry, long requests, long status4xx = 0, long status5xx = 0) =>
        _mainDb.Insertable(new PosApiEntryUsageDaily
        {
            UsageDate = DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
            StoreCode = storeCode,
            Entry = entry,
            RequestCount = requests,
            Status4xxCount = status4xx,
            Status5xxCount = status5xx,
            UpdatedAt = Now,
        }).ExecuteCommand();

    private void SeedStore(string code, string name) =>
        _mainDb.Insertable(new Store
        {
            StoreGUID = "g-" + code,
            StoreCode = code,
            StoreName = name,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommand();

    private static CurrentUserManageableStoreScope Scope(bool isAdmin, string[]? storeCodes = null) =>
        new()
        {
            IsAllowed = true,
            IsAuthenticated = true,
            IsAdmin = isAdmin,
            StoreCodes = storeCodes ?? [],
        };

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class FakeScope : ICurrentUserManageableStoreScopeService
    {
        public required CurrentUserManageableStoreScope Scope { get; set; }
        public Task<CurrentUserManageableStoreScope> GetScopeAsync() => Task.FromResult(Scope);
        public Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync() => Task.FromResult(Scope.StoreCodes);
        public Task<bool> CanAccessStoreCodeAsync(string storeCode) => Task.FromResult(Scope.CanAccessStoreCode(storeCode));
        public Task<bool> CanAccessOrderAsync(string orderGuid) => Task.FromResult(false);
        public Task<bool> CanManageStoreAsync(string storeGuid) => Task.FromResult(false);
        public Task<bool> CanManageUserAsync(string userGuid) => Task.FromResult(false);
    }
}
