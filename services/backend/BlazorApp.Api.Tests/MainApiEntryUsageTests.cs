using System.Reflection;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Models.PosApiEntry;
using BlazorApp.Api.Services.MainApiEntry;
using BlazorApp.Api.Services.PosApiEntry;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 主 API 入口用量统计：客户端类别判定、进程内计数器、中间件、60 秒落库服务、upsert SQL 形状，
/// 以及只读查询（日期 / 粒度 / 类别校验、周一起算分桶、占比、类别拆分）与控制器路由 / 权限。
/// 与 Hbpos.Api 的 PosApiEntryUsageTests 同构；真实 SQL Server 上的建表与 upsert 见 SchemaMigrationSqlServerIntegrationTests。
/// </summary>
public sealed class MainApiEntryUsageTests : IDisposable
{
    private static readonly DateOnly D1 = new(2026, 10, 24);
    private static readonly DateOnly D2 = new(2026, 10, 25);

    // 2026-10-24 04:00 UTC＝悉尼 15:00（夏令时 UTC+11），悉尼「今天」是 2026-10-24（周六）。
    private static readonly DateTime Now = new(2026, 10, 24, 4, 0, 0, DateTimeKind.Utc);

    private readonly string _mainPath = Path.Combine(Path.GetTempPath(), $"hb-mainusage-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _mainDb;
    private readonly MainApiEntryUsageService _service;

    public MainApiEntryUsageTests()
    {
        _mainDb = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_mainPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _mainDb.CodeFirst.InitTables<MainApiEntryUsageDaily>();
        _service = new MainApiEntryUsageService(_mainDb, new FixedTimeProvider(Now));
    }

    public void Dispose()
    {
        _mainDb.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_mainPath);
    }

    // ───────────────────────── 悉尼日期 ─────────────────────────

    [Fact]
    public void SydneyDate_cuts_the_day_at_sydney_midnight_not_utc()
    {
        Assert.Equal(D1, MainApiEntryUsageClock.SydneyDate(new DateTimeOffset(2026, 10, 24, 12, 59, 59, TimeSpan.Zero)));
        Assert.Equal(D2, MainApiEntryUsageClock.SydneyDate(new DateTimeOffset(2026, 10, 24, 13, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 6, 10), MainApiEntryUsageClock.SydneyDate(new DateTimeOffset(2026, 6, 10, 13, 59, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 6, 11), MainApiEntryUsageClock.SydneyDate(new DateTimeOffset(2026, 6, 10, 14, 0, 0, TimeSpan.Zero)));
    }

    // ───────────────────────── 客户端类别 ─────────────────────────

    [Theory]
    // 浏览器（Web 后台）
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36", "web")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "web")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0", "web")]
    [InlineData("Opera/9.80 (Windows NT 6.1) Presto/2.12.388 Version/12.16", "web")]
    // 员工 App：React Native 原生网络栈
    [InlineData("okhttp/4.12.0", "app")]
    [InlineData("HB/1 CFNetwork/1568.100.1 Darwin/24.0.0", "app")]
    [InlineData("Expo/2.31.2 CFNetwork/1568.100.1 Darwin/24.0.0", "app")]
    [InlineData("Dalvik/2.1.0 (Linux; U; Android 14; Pixel 8 Build/AP1A.240305.019)", "app")]
    // 脚本、监控、爬虫、空值
    [InlineData("curl/8.4.0", "other")]
    [InlineData("python-requests/2.31.0", "other")]
    [InlineData("Go-http-client/1.1", "other")]
    [InlineData("Mozilla/5.0 (compatible; UptimeRobot/2.0; http://www.uptimerobot.com/)", "other")]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", "other")]
    [InlineData("", "other")]
    [InlineData("   ", "other")]
    [InlineData(null, "other")]
    [InlineData("totally-custom-agent", "other")]
    public void ClientKind_classifies_user_agents_into_the_fixed_whitelist(string? userAgent, string expected)
    {
        Assert.Equal(expected, MainApiClientKinds.Classify(userAgent));
    }

    [Fact]
    public void ClientKind_never_returns_a_value_outside_the_whitelist_even_for_garbage()
    {
        var garbage = new[] { new string('M', 5000), "Mozilla/" + new string('x', 5000), "\u0000\u0001", "okhttp" + new string('z', 9000) };
        foreach (var userAgent in garbage)
        {
            Assert.Contains(MainApiClientKinds.Classify(userAgent), MainApiClientKinds.All);
        }

        Assert.Equal("other", MainApiClientKinds.Normalize("Mozilla/5.0"));
        Assert.Equal("web", MainApiClientKinds.Normalize(" WEB "));
    }

    // ───────────────────────── 计数器 ─────────────────────────

    [Fact]
    public void Counter_classifies_status_codes_and_aggregates_per_key()
    {
        var counter = new MainApiEntryUsageCounter();
        foreach (var status in new[] { 200, 204, 404, 429, 500, 503, 302 })
        {
            counter.Record(D1, "direct", "web", status);
        }

        var row = Assert.Single(counter.Drain(D1));
        Assert.Equal(new MainApiEntryUsageKey(D1, "direct", "web"), row.Key);
        // 3xx 只算请求数，既不是 4xx 也不是 5xx。
        Assert.Equal(new MainApiEntryUsageDelta(7, 2, 2), row.Value);
    }

    [Fact]
    public void Counter_keeps_separate_rows_per_date_entry_and_client_kind()
    {
        var counter = new MainApiEntryUsageCounter();
        counter.Record(D1, "edge", "app", 200);
        counter.Record(D1, "edge", "web", 200);
        counter.Record(D1, "direct", "web", 200);
        counter.Record(D2, "edge", "app", 200);

        Assert.Equal(4, counter.Drain(D2).Count);
    }

    [Fact]
    public void Counter_normalizes_entry_and_client_kind_so_the_key_space_stays_tiny()
    {
        var counter = new MainApiEntryUsageCounter();
        counter.Record(D1, " AWS ", " App ", 200);
        counter.Record(D1, "aws", "app", 200);
        // 缺失 / 非白名单入口归 unknown；非白名单类别归 other。
        counter.Record(D1, null, null, 401);
        counter.Record(D1, "tencent", "Mozilla/5.0 something", 200);

        var rows = counter.Drain(D1).ToDictionary(r => r.Key, r => r.Value);
        Assert.Equal(new MainApiEntryUsageDelta(2, 0, 0), rows[new MainApiEntryUsageKey(D1, "aws", "app")]);
        Assert.Equal(new MainApiEntryUsageDelta(2, 1, 0), rows[new MainApiEntryUsageKey(D1, "unknown", "other")]);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Drain_returns_each_increment_once_and_recycles_old_empty_dates()
    {
        var counter = new MainApiEntryUsageCounter();
        counter.Record(D1, "edge", "web", 200);
        Assert.Single(counter.Drain(D1));
        Assert.Empty(counter.Drain(D1));
        Assert.Equal(1, counter.KeyCount);

        // 跨日后：D1 的键已清空 → 回收，字典不会无限积累历史键。
        Assert.Empty(counter.Drain(D2));
        Assert.Equal(0, counter.KeyCount);
    }

    [Fact]
    public void Drain_keeps_a_not_yet_flushed_old_date_until_it_has_been_taken()
    {
        var counter = new MainApiEntryUsageCounter();
        counter.Record(D1, "edge", "web", 200);
        Assert.Equal(1, Assert.Single(counter.Drain(D2)).Value.Requests);
    }

    [Fact]
    public void Restore_puts_failed_deltas_back_so_nothing_is_lost()
    {
        var counter = new MainApiEntryUsageCounter();
        counter.Record(D1, "edge", "web", 200);
        counter.Record(D1, "edge", "web", 500);
        var taken = counter.Drain(D1);

        counter.Record(D1, "edge", "web", 200);
        counter.Restore(taken);

        Assert.Equal(new MainApiEntryUsageDelta(3, 0, 1), Assert.Single(counter.Drain(D1)).Value);
    }

    [Fact]
    public void Counter_stops_creating_keys_at_the_cap_but_keeps_counting_existing_ones()
    {
        var counter = new MainApiEntryUsageCounter();
        // 键由 (日期, 入口, 类别) 组成；用不同日期撑满上限来验证防御逻辑。
        for (var i = 0; i < MainApiEntryUsageCounter.MaxKeys; i++)
        {
            counter.Record(D1.AddDays(i), "edge", "web", 200);
        }

        Assert.Equal(MainApiEntryUsageCounter.MaxKeys, counter.KeyCount);
        counter.Record(D1.AddDays(MainApiEntryUsageCounter.MaxKeys), "edge", "web", 200);
        Assert.Equal(MainApiEntryUsageCounter.MaxKeys, counter.KeyCount);
        Assert.Equal(1, counter.DroppedRequests);

        counter.Record(D1, "edge", "web", 200);
        Assert.Equal(1, counter.DroppedRequests);
    }

    [Fact]
    public async Task Counter_is_correct_under_concurrent_recording_and_draining()
    {
        var counter = new MainApiEntryUsageCounter();
        const int threads = 8;
        const int perThread = 20_000;
        var drained = new List<MainApiEntryUsageDelta>();

        var writers = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
            {
                counter.Record(D1, "direct", "web", i % 10 == 0 ? 500 : 200);
            }
        })).ToArray();
        var drainer = Task.Run(() =>
        {
            while (!writers.All(w => w.IsCompleted))
            {
                lock (drained) drained.AddRange(counter.Drain(D1).Select(r => r.Value));
            }
        });
        await Task.WhenAll(writers);
        await drainer;
        lock (drained) drained.AddRange(counter.Drain(D1).Select(r => r.Value));

        Assert.Equal(threads * perThread, drained.Sum(d => d.Requests));
        Assert.Equal(threads * perThread / 10, drained.Sum(d => d.Status5xx));
    }

    // ───────────────────────── 中间件 ─────────────────────────

    private static (MainApiEntryUsageMiddleware Middleware, MainApiEntryUsageCounter Counter) CreateMiddleware(
        RequestDelegate next, TimeProvider? clock = null)
    {
        var counter = new MainApiEntryUsageCounter();
        var middleware = new MainApiEntryUsageMiddleware(
            next,
            counter,
            clock ?? new FixedTimeProvider(new DateTime(2026, 10, 24, 1, 0, 0, DateTimeKind.Utc)),
            NullLogger<MainApiEntryUsageMiddleware>.Instance);
        return (middleware, counter);
    }

    private static DefaultHttpContext Context(string path, string? entry = null, string? userAgent = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (entry is not null)
        {
            context.Request.Headers[MainApiEntryHeader.Name] = entry;
        }

        if (userAgent is not null)
        {
            context.Request.Headers.UserAgent = userAgent;
        }

        return context;
    }

    [Fact]
    public async Task Middleware_counts_api_requests_with_entry_client_kind_and_status()
    {
        var (middleware, counter) = CreateMiddleware(context =>
        {
            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(Context("/api/react/v1/users", entry: "direct", userAgent: "Mozilla/5.0 Chrome/130"));

        var row = Assert.Single(counter.Drain(D1));
        Assert.Equal(new MainApiEntryUsageKey(D1, "direct", "web"), row.Key);
        Assert.Equal(new MainApiEntryUsageDelta(1, 1, 0), row.Value);
    }

    [Fact]
    public async Task Middleware_counts_staff_app_requests_as_app_and_health_checks_without_headers_as_unknown_other()
    {
        var (middleware, counter) = CreateMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(Context("/api/react/v1/mobile/home", entry: "edge", userAgent: "okhttp/4.12.0"));
        // 没有 X-HB-Entry（nginx 还没加该行，或绕过 nginx）且没有 UA：unknown / other。
        await middleware.InvokeAsync(Context("/API/health"));

        var rows = counter.Drain(D1).ToDictionary(r => r.Key, r => r.Value);
        Assert.Equal(1, rows[new MainApiEntryUsageKey(D1, "edge", "app")].Requests);
        Assert.Equal(1, rows[new MainApiEntryUsageKey(D1, "unknown", "other")].Requests);
    }

    [Fact]
    public async Task Middleware_ignores_a_client_supplied_multi_value_or_illegal_entry_header()
    {
        var (middleware, counter) = CreateMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(Context("/api/x", entry: "edge,aws", userAgent: "okhttp/4"));
        await middleware.InvokeAsync(Context("/api/x", entry: "tencent", userAgent: "okhttp/4"));

        var row = Assert.Single(counter.Drain(D1));
        Assert.Equal(new MainApiEntryUsageKey(D1, "unknown", "app"), row.Key);
        Assert.Equal(2, row.Value.Requests);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/swagger/index.html")]
    [InlineData("/assets/index.js")]
    [InlineData("/apiary")]
    public async Task Middleware_ignores_non_api_paths(string path)
    {
        var called = false;
        var (middleware, counter) = CreateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(Context(path, entry: "edge", userAgent: "Mozilla/5.0"));

        Assert.True(called);
        Assert.Empty(counter.Drain(D1));
    }

    [Fact]
    public async Task Middleware_counts_an_escaping_exception_as_5xx_and_rethrows()
    {
        var (middleware, counter) = CreateMiddleware(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(Context("/api/orders", entry: "direct", userAgent: "Mozilla/5.0")));

        Assert.Equal(new MainApiEntryUsageDelta(1, 0, 1), Assert.Single(counter.Drain(D1)).Value);
    }

    [Fact]
    public async Task Middleware_counts_a_client_abort_as_4xx_not_5xx()
    {
        using var cts = new CancellationTokenSource();
        var context = Context("/api/orders", entry: "edge", userAgent: "okhttp/4");
        context.RequestAborted = cts.Token;
        var (middleware, counter) = CreateMiddleware(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        Assert.Equal(new MainApiEntryUsageDelta(1, 1, 0), Assert.Single(counter.Drain(D1)).Value);
    }

    [Fact]
    public async Task Middleware_never_breaks_the_response_when_recording_fails()
    {
        var (middleware, counter) = CreateMiddleware(
            context =>
            {
                context.Response.StatusCode = 200;
                return Task.CompletedTask;
            },
            new ThrowingTimeProvider());

        await middleware.InvokeAsync(Context("/api/health", entry: "edge"));

        Assert.Empty(counter.Drain(D1));
    }

    // ───────────────────────── 落库服务 ─────────────────────────

    private sealed class RecordingWriter : IMainApiEntryUsageWriter
    {
        public bool Fail { get; set; }
        public List<IReadOnlyList<KeyValuePair<MainApiEntryUsageKey, MainApiEntryUsageDelta>>> Batches { get; } = [];

        public Task UpsertAsync(
            IReadOnlyList<KeyValuePair<MainApiEntryUsageKey, MainApiEntryUsageDelta>> deltas,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("Invalid object name 'dbo.MainApiEntryUsageDaily'.");
            }

            Batches.Add(deltas);
            return Task.CompletedTask;
        }
    }

    private static MainApiEntryUsageFlushService CreateFlushService(
        MainApiEntryUsageCounter counter,
        RecordingWriter writer,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IMainApiEntryUsageWriter>(_ => writer);
        var provider = services.BuildServiceProvider();
        return new MainApiEntryUsageFlushService(
            counter,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(new DateTime(2026, 10, 24, 1, 0, 0, DateTimeKind.Utc)),
            NullLogger<MainApiEntryUsageFlushService>.Instance,
            delay);
    }

    [Fact]
    public async Task Flush_writes_pending_deltas_once_and_skips_when_nothing_is_pending()
    {
        var counter = new MainApiEntryUsageCounter();
        var writer = new RecordingWriter();
        var service = CreateFlushService(counter, writer);

        Assert.True(await service.FlushAsync(CancellationToken.None));
        Assert.Empty(writer.Batches);

        counter.Record(D1, "edge", "web", 200);
        counter.Record(D1, "edge", "web", 500);
        Assert.True(await service.FlushAsync(CancellationToken.None));
        Assert.Equal(new MainApiEntryUsageDelta(2, 0, 1), Assert.Single(Assert.Single(writer.Batches)).Value);

        Assert.True(await service.FlushAsync(CancellationToken.None));
        Assert.Single(writer.Batches);
    }

    [Fact]
    public async Task Flush_keeps_the_counts_when_the_write_fails_and_retries_them_next_time()
    {
        var counter = new MainApiEntryUsageCounter();
        var writer = new RecordingWriter { Fail = true };
        var service = CreateFlushService(counter, writer);

        counter.Record(D1, "edge", "web", 200);
        Assert.False(await service.FlushAsync(CancellationToken.None));
        counter.Record(D1, "edge", "web", 200);
        Assert.False(await service.FlushAsync(CancellationToken.None));
        Assert.Empty(writer.Batches);

        writer.Fail = false;
        Assert.True(await service.FlushAsync(CancellationToken.None));
        Assert.Equal(2, Assert.Single(Assert.Single(writer.Batches)).Value.Requests);
    }

    [Fact]
    public async Task Service_flushes_on_every_interval_and_once_more_on_graceful_stop()
    {
        var counter = new MainApiEntryUsageCounter();
        var writer = new RecordingWriter();
        var delayCalls = 0;
        var secondTickReached = new TaskCompletionSource();
        var neverReleased = new TaskCompletionSource();
        var service = CreateFlushService(counter, writer, async (interval, token) =>
        {
            Assert.Equal(MainApiEntryUsageFlushService.FlushInterval, interval);
            if (Interlocked.Increment(ref delayCalls) == 1)
            {
                return; // 第一个周期立即到期
            }

            secondTickReached.TrySetResult();
            await neverReleased.Task.WaitAsync(token); // 之后一直等到停机取消
        });

        counter.Record(D1, "edge", "web", 200);
        await service.StartAsync(CancellationToken.None);
        await secondTickReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(writer.Batches);

        counter.Record(D1, "edge", "web", 200);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(2, writer.Batches.Count);
    }

    // ───────────────────────── upsert SQL 形状 ─────────────────────────

    [Fact]
    public void BuildUpsertSql_wraps_every_row_in_one_serializable_transaction()
    {
        var sql = SqlSugarMainApiEntryUsageWriter.BuildUpsertSql(2);

        Assert.Contains("SET XACT_ABORT ON;", sql);
        Assert.Contains("BEGIN TRANSACTION;", sql);
        Assert.Contains("COMMIT TRANSACTION;", sql);
        Assert.Equal(2, CountOccurrences(sql, "WITH (UPDLOCK, SERIALIZABLE)"));
        Assert.Equal(2, CountOccurrences(sql, "IF @@ROWCOUNT = 0"));
        // 累加而不是覆盖。
        Assert.Contains("[RequestCount] = [RequestCount] + @R1", sql);
        Assert.Contains("[Status4xxCount] = [Status4xxCount] + @C1", sql);
        Assert.Contains("[Status5xxCount] = [Status5xxCount] + @F1", sql);
        Assert.Contains("[Entry] = @E1 AND [ClientKind] = @K1", sql);
        Assert.DoesNotContain("@R2", sql);
    }

    // ───────────────────────── 查询：参数校验 ─────────────────────────

    [Fact]
    public async Task Query_defaults_to_the_last_30_days_including_today_by_day()
    {
        var result = await _service.GetUsageAsync(null, null, null, null);

        Assert.True(result.Success);
        Assert.Equal("2026-09-25", result.Data!.StartDate);
        Assert.Equal("2026-10-24", result.Data.EndDate);
        Assert.Equal("day", result.Data.Granularity);
        Assert.Equal("Australia/Sydney", result.Data.TimeZone);
        Assert.Equal(30, result.Data.Buckets.Count);
        Assert.Equal(["web", "app", "other"], result.Data.ClientKinds.Select(k => k.ClientKind));
    }

    [Fact]
    public async Task Query_with_only_one_end_derives_the_other_by_30_days()
    {
        var onlyStart = await _service.GetUsageAsync("2026-10-01", null, "day", null);
        Assert.Equal("2026-10-30", onlyStart.Data!.EndDate);

        var onlyEnd = await _service.GetUsageAsync(null, "2026-10-30", "day", null);
        Assert.Equal("2026-10-01", onlyEnd.Data!.StartDate);
    }

    [Theory]
    [InlineData("2026/10/01", "2026-10-10")]
    [InlineData("2026-10-1", "2026-10-10")]
    [InlineData("not-a-date", "2026-10-10")]
    [InlineData("2026-10-01", "2026-02-30")]
    [InlineData("0001-01-01", "2026-10-10")]
    [InlineData("2026-10-01", "3000-01-01")]
    public async Task Query_rejects_illegal_dates(string start, string end)
    {
        var result = await _service.GetUsageAsync(start, end, "day", null);

        Assert.False(result.Success);
        Assert.Equal(MainApiEntryUsageErrorCodes.InvalidDate, result.ErrorCode);
    }

    [Fact]
    public async Task Query_rejects_reversed_range_too_long_range_bad_granularity_and_bad_client_kind()
    {
        Assert.Equal(
            MainApiEntryUsageErrorCodes.InvalidRange,
            (await _service.GetUsageAsync("2026-10-10", "2026-10-09", "day", null)).ErrorCode);

        // 含首尾 400 天可以，401 天不行。
        Assert.True((await _service.GetUsageAsync("2025-09-20", "2026-10-24", "day", null)).Success);
        Assert.Equal(
            MainApiEntryUsageErrorCodes.RangeTooLarge,
            (await _service.GetUsageAsync("2025-09-19", "2026-10-24", "day", null)).ErrorCode);

        Assert.Equal(
            MainApiEntryUsageErrorCodes.InvalidGranularity,
            (await _service.GetUsageAsync(null, null, "year", null)).ErrorCode);
        Assert.Equal(
            MainApiEntryUsageErrorCodes.InvalidClientKind,
            (await _service.GetUsageAsync(null, null, "day", "desktop")).ErrorCode);
    }

    // ───────────────────────── 查询：汇总 ─────────────────────────

    [Fact]
    public async Task Query_aggregates_entries_shares_and_client_kinds()
    {
        Seed("2026-10-20", "direct", "web", 60, 3, 1);
        Seed("2026-10-20", "edge", "app", 30, 2, 0);
        Seed("2026-10-21", "edge", "app", 10, 0, 4);
        Seed("2026-10-21", "unknown", "other", 4);
        // 范围外的行不计入。
        Seed("2026-10-19", "direct", "web", 999);

        var result = await _service.GetUsageAsync("2026-10-20", "2026-10-21", "day", null);

        var data = result.Data!;
        Assert.Equal(new[] { "2026-10-20", "2026-10-21" }, data.Buckets.Select(b => b.BucketStart));
        Assert.Equal(104, data.Total.Requests);
        Assert.Equal(5, data.Total.Status4xx);
        Assert.Equal(5, data.Total.Status5xx);
        Assert.Equal(["edge", "aws", "direct", "unknown"], data.Entries.Select(e => e.Entry));
        Assert.Equal(40, data.Entries[0].Requests);
        Assert.Equal(0.60 / 1.04, data.Entries[2].Share, 6);

        var web = data.ClientKinds.Single(k => k.ClientKind == "web");
        var app = data.ClientKinds.Single(k => k.ClientKind == "app");
        var other = data.ClientKinds.Single(k => k.ClientKind == "other");
        Assert.Equal(60, web.Total.Requests);
        Assert.Equal(40, app.Total.Requests);
        Assert.Equal(4, other.Total.Requests);
        Assert.Equal(60d / 104, web.Share, 6);
        // 类别内的入口占比以该类别自身请求数为分母：员工 App 全部走 edge。
        Assert.Equal(1d, app.Entries.Single(e => e.Entry == "edge").Share, 6);
        Assert.Equal(60, data.Buckets[0].Entries.Single(e => e.Entry == "direct").Requests);
    }

    [Fact]
    public async Task Query_filters_by_client_kind()
    {
        Seed("2026-10-20", "direct", "web", 60);
        Seed("2026-10-20", "edge", "app", 30);

        var result = await _service.GetUsageAsync("2026-10-20", "2026-10-20", "day", " APP ");

        Assert.Equal("app", result.Data!.ClientKind);
        Assert.Equal(30, result.Data.Total.Requests);
        Assert.Equal(0, result.Data.ClientKinds.Single(k => k.ClientKind == "web").Total.Requests);
    }

    [Fact]
    public async Task Query_buckets_weeks_from_monday_and_months_by_calendar_and_marks_partial_buckets()
    {
        // 2026-10-24 是周六；本周一是 10-19。
        Seed("2026-10-18", "direct", "web", 1);
        Seed("2026-10-19", "direct", "web", 2);
        Seed("2026-10-24", "direct", "web", 4);

        var weekly = (await _service.GetUsageAsync("2026-10-18", "2026-10-24", "week", null)).Data!;
        Assert.Equal(new[] { "2026-10-12", "2026-10-19" }, weekly.Buckets.Select(b => b.BucketStart));
        Assert.Equal(new[] { 1L, 6L }, weekly.Buckets.Select(b => b.Total.Requests));
        Assert.True(weekly.Buckets[0].Partial);
        Assert.True(weekly.Buckets[1].Partial);

        var monthly = (await _service.GetUsageAsync("2026-09-30", "2026-10-24", "month", null)).Data!;
        Assert.Equal(new[] { "2026-09-01", "2026-10-01" }, monthly.Buckets.Select(b => b.BucketStart));
        Assert.Equal(7, monthly.Buckets[1].Total.Requests);
    }

    [Fact]
    public async Task Query_folds_dirty_rows_into_unknown_and_other_without_losing_requests()
    {
        Seed("2026-10-20", "tencent", "robot", 5);

        var data = (await _service.GetUsageAsync("2026-10-20", "2026-10-20", "day", null)).Data!;

        Assert.Equal(5, data.Entries.Single(e => e.Entry == "unknown").Requests);
        Assert.Equal(5, data.ClientKinds.Single(k => k.ClientKind == "other").Total.Requests);
    }

    [Fact]
    public async Task Query_with_no_data_returns_zero_filled_buckets_and_zero_shares()
    {
        var data = (await _service.GetUsageAsync("2026-10-20", "2026-10-22", "day", null)).Data!;

        Assert.Equal(3, data.Buckets.Count);
        Assert.Equal(0, data.Total.Requests);
        Assert.All(data.Entries, e => Assert.Equal(0d, e.Share));
        Assert.All(data.ClientKinds, k => Assert.Equal(0d, k.Share));
    }

    // ───────────────────────── 控制器 ─────────────────────────

    [Fact]
    public void Controller_route_and_permission_follow_device_registration_view()
    {
        var route = Assert.Single(typeof(MainApiEntryUsageController).GetCustomAttributes<RouteAttribute>());
        Assert.Equal("api/react/v1/main-api-entry/usage", route.Template);
        Assert.Contains(typeof(MainApiEntryUsageController).GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy is null);

        var get = typeof(MainApiEntryUsageController).GetMethod(nameof(MainApiEntryUsageController.GetUsage))!;
        Assert.NotNull(get.GetCustomAttribute<HttpGetAttribute>());
        Assert.Equal(Permissions.DeviceRegistration.View, get.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public async Task Controller_passes_success_through_and_maps_validation_failures_to_400()
    {
        Seed("2026-10-20", "direct", "web", 10);
        var controller = new MainApiEntryUsageController(_service);

        var ok = Assert.IsType<OkObjectResult>(
            await controller.GetUsage("2026-10-20", "2026-10-20", "day", null, CancellationToken.None));
        Assert.Equal(10, Assert.IsType<ApiResponse<MainApiEntryUsageResultDto>>(ok.Value).Data!.Total.Requests);

        var bad = Assert.IsType<BadRequestObjectResult>(
            await controller.GetUsage("garbage", null, "day", null, CancellationToken.None));
        Assert.Equal(400, bad.StatusCode);
    }

    // ───────────────────────── 迁移与写入端的约定 ─────────────────────────

    [Fact]
    public void Whitelists_in_code_match_the_migration_check_constraints()
    {
        foreach (var entry in PosApiEntryUsageService.EntryOrder)
        {
            Assert.Contains($"N'{entry}'", MainApiEntryUsageSchema.ApplySql, StringComparison.Ordinal);
        }

        foreach (var kind in MainApiClientKinds.All)
        {
            Assert.Contains($"N'{kind}'", MainApiEntryUsageSchema.ApplySql, StringComparison.Ordinal);
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    private void Seed(string date, string entry, string clientKind, long requests, long status4xx = 0, long status5xx = 0) =>
        _mainDb.Insertable(new MainApiEntryUsageDaily
        {
            UsageDate = DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
            Entry = entry,
            ClientKind = clientKind,
            RequestCount = requests,
            Status4xxCount = status4xx,
            Status5xxCount = status5xx,
            UpdatedAt = Now,
        }).ExecuteCommand();

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("clock failure");
    }
}
