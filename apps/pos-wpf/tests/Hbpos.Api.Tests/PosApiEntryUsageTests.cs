using System.Security.Claims;
using Hbpos.Api.Services;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

/// <summary>
/// 收银入口用量统计（POS API 侧）：悉尼日期口径、进程内计数器（分类 / 归一化 / 取走 / 放回 / 上限）、
/// 中间件（路径过滤 / 入口头 / 分店 claim / 异常与统计失败不影响响应）、60 秒落库服务（失败保留重试）、upsert SQL 形状。
/// 真实 SQL Server 上的 upsert 见 PosApiEntryUsageSqlServerIntegrationTests。
/// </summary>
public sealed class PosApiEntryUsageTests
{
    private static readonly DateOnly D1 = new(2026, 10, 24);
    private static readonly DateOnly D2 = new(2026, 10, 25);

    // ---------------------------------------------------------------- 悉尼日期

    [Fact]
    public void SydneyDate_cuts_the_day_at_sydney_midnight_not_utc()
    {
        // 2026-10 悉尼是夏令时 UTC+11：UTC 12:59 仍是 24 日 23:59，13:00 已是 25 日 00:00。
        Assert.Equal(D1, PosApiEntryUsageClock.SydneyDate(new DateTimeOffset(2026, 10, 24, 12, 59, 59, TimeSpan.Zero)));
        Assert.Equal(D2, PosApiEntryUsageClock.SydneyDate(new DateTimeOffset(2026, 10, 24, 13, 0, 0, TimeSpan.Zero)));
        // 非夏令时（6 月，UTC+10）：UTC 13:59 仍是当天，14:00 才跨日。
        Assert.Equal(new DateOnly(2026, 6, 10), PosApiEntryUsageClock.SydneyDate(new DateTimeOffset(2026, 6, 10, 13, 59, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 6, 11), PosApiEntryUsageClock.SydneyDate(new DateTimeOffset(2026, 6, 10, 14, 0, 0, TimeSpan.Zero)));
    }

    // ---------------------------------------------------------------- 计数器

    [Fact]
    public void Counter_classifies_status_codes_and_aggregates_per_key()
    {
        var counter = new PosApiEntryUsageCounter();
        counter.Record(D1, "S001", "edge", 200);
        counter.Record(D1, "S001", "edge", 204);
        counter.Record(D1, "S001", "edge", 404);
        counter.Record(D1, "S001", "edge", 429);
        counter.Record(D1, "S001", "edge", 500);
        counter.Record(D1, "S001", "edge", 503);
        // 3xx 只算请求数，既不是 4xx 也不是 5xx。
        counter.Record(D1, "S001", "edge", 302);

        var rows = counter.Drain(D1);
        var row = Assert.Single(rows);
        Assert.Equal(new PosApiEntryUsageKey(D1, "S001", "edge"), row.Key);
        Assert.Equal(new PosApiEntryUsageDelta(7, 2, 2), row.Value);
    }

    [Fact]
    public void Counter_keeps_separate_rows_per_date_store_and_entry()
    {
        var counter = new PosApiEntryUsageCounter();
        counter.Record(D1, "S001", "edge", 200);
        counter.Record(D1, "S001", "aws", 200);
        counter.Record(D1, "S002", "edge", 200);
        counter.Record(D2, "S001", "edge", 200);

        Assert.Equal(4, counter.Drain(D2).Count);
    }

    [Fact]
    public void Counter_normalizes_entry_and_store_code()
    {
        var counter = new PosApiEntryUsageCounter();
        counter.Record(D1, "  S001 ", "AWS", 200);
        counter.Record(D1, "S001", " aws ", 200);
        // 缺失 / 非白名单入口一律归 unknown；拿不到分店记空串。
        counter.Record(D1, null, null, 401);
        counter.Record(D1, "", "tencent", 200);
        counter.Record(D1, "x" + new string('y', 80), "edge", 200);

        var rows = counter.Drain(D1).ToDictionary(r => r.Key, r => r.Value);
        Assert.Equal(new PosApiEntryUsageDelta(2, 0, 0), rows[new PosApiEntryUsageKey(D1, "S001", "aws")]);
        Assert.Equal(new PosApiEntryUsageDelta(2, 1, 0), rows[new PosApiEntryUsageKey(D1, "", "unknown")]);
        var longKey = Assert.Single(rows.Keys, key => key.StoreCode.Length > 10);
        Assert.Equal(PosApiEntryUsageCounter.MaxStoreCodeLength, longKey.StoreCode.Length);
    }

    [Fact]
    public void Drain_returns_each_increment_once_and_recycles_old_empty_dates()
    {
        var counter = new PosApiEntryUsageCounter();
        counter.Record(D1, "S001", "edge", 200);
        Assert.Single(counter.Drain(D1));
        // 已取走：当天再取为空，且当天的键保留（后面还会有请求）。
        Assert.Empty(counter.Drain(D1));
        Assert.Equal(1, counter.KeyCount);

        // 跨日后：D1 的键已清空 → 回收，字典不会无限积累历史键。
        Assert.Empty(counter.Drain(D2));
        Assert.Equal(0, counter.KeyCount);
    }

    [Fact]
    public void Drain_keeps_a_not_yet_flushed_old_date_until_it_has_been_taken()
    {
        var counter = new PosApiEntryUsageCounter();
        counter.Record(D1, "S001", "edge", 200);
        // 跨日后第一次取：D1 还有增量 → 照常返回，不能因为日期过期就丢。
        var rows = counter.Drain(D2);
        Assert.Equal(1, Assert.Single(rows).Value.Requests);
    }

    [Fact]
    public void Restore_puts_failed_deltas_back_so_nothing_is_lost()
    {
        var counter = new PosApiEntryUsageCounter();
        counter.Record(D1, "S001", "edge", 200);
        counter.Record(D1, "S001", "edge", 500);
        var taken = counter.Drain(D1);

        // 取走之后又来了新请求，随后写库失败放回：两部分必须合并。
        counter.Record(D1, "S001", "edge", 200);
        counter.Restore(taken);

        var merged = Assert.Single(counter.Drain(D1));
        Assert.Equal(new PosApiEntryUsageDelta(3, 0, 1), merged.Value);
    }

    [Fact]
    public void Counter_stops_creating_keys_at_the_cap_but_keeps_counting_existing_ones()
    {
        var counter = new PosApiEntryUsageCounter();
        for (var i = 0; i < PosApiEntryUsageCounter.MaxKeys; i++)
        {
            counter.Record(D1, "S" + i, "edge", 200);
        }

        Assert.Equal(PosApiEntryUsageCounter.MaxKeys, counter.KeyCount);
        counter.Record(D1, "ONE-TOO-MANY", "edge", 200);
        Assert.Equal(PosApiEntryUsageCounter.MaxKeys, counter.KeyCount);
        Assert.Equal(1, counter.DroppedRequests);

        // 已存在的键不受上限影响。
        counter.Record(D1, "S0", "edge", 200);
        Assert.Equal(1, counter.DroppedRequests);
    }

    [Fact]
    public async Task Counter_is_correct_under_concurrent_recording_and_draining()
    {
        var counter = new PosApiEntryUsageCounter();
        const int threads = 8;
        const int perThread = 20_000;
        var drained = new List<PosApiEntryUsageDelta>();

        var writers = Enumerable.Range(0, threads).Select(thread => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
            {
                counter.Record(D1, "S001", "edge", i % 10 == 0 ? 500 : 200);
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

    // ---------------------------------------------------------------- 中间件

    private static (PosApiEntryUsageMiddleware Middleware, PosApiEntryUsageCounter Counter) CreateMiddleware(
        RequestDelegate next, TimeProvider? clock = null)
    {
        var counter = new PosApiEntryUsageCounter();
        var middleware = new PosApiEntryUsageMiddleware(
            next,
            counter,
            clock ?? new PosApiEntryTests.MutableTimeProvider(new DateTimeOffset(2026, 10, 24, 1, 0, 0, TimeSpan.Zero)),
            NullLogger<PosApiEntryUsageMiddleware>.Instance);
        return (middleware, counter);
    }

    private static DefaultHttpContext Context(string path, string? entry = null, string? storeCode = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (entry is not null)
        {
            context.Request.Headers[PosApiEntryModes.HeaderName] = entry;
        }

        if (storeCode is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(DeviceAuthConstants.StoreCodeClaim, storeCode)], "test"));
        }

        return context;
    }

    [Fact]
    public async Task Middleware_counts_api_requests_with_entry_store_and_status()
    {
        var (middleware, counter) = CreateMiddleware(context =>
        {
            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(Context("/api/v1/catalog/products", entry: "aws", storeCode: "S001"));

        var row = Assert.Single(counter.Drain(D1));
        // 2026-10-24 01:00 UTC＝悉尼 12:00（同一天）。
        Assert.Equal(new PosApiEntryUsageKey(D1, "S001", "aws"), row.Key);
        Assert.Equal(new PosApiEntryUsageDelta(1, 1, 0), row.Value);
    }

    [Fact]
    public async Task Middleware_reads_the_store_claim_after_the_pipeline_has_authenticated()
    {
        // 真实管线里认证中间件在 next 内部才设置 User：必须在 next 返回之后再读 claim。
        var (middleware, counter) = CreateMiddleware(context =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(DeviceAuthConstants.StoreCodeClaim, "S009")], "test"));
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(Context("/api/v1/health", entry: "edge"));

        Assert.Equal("S009", Assert.Single(counter.Drain(D1)).Key.StoreCode);
    }

    [Fact]
    public async Task Middleware_counts_health_checks_and_unauthenticated_requests_with_empty_store_and_unknown_entry()
    {
        var (middleware, counter) = CreateMiddleware(context =>
        {
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        });

        // 没有 X-HB-Entry（绕过 nginx）、没有鉴权：入口 unknown、分店空串。
        await middleware.InvokeAsync(Context("/API/V1/health"));

        var row = Assert.Single(counter.Drain(D1));
        Assert.Equal(new PosApiEntryUsageKey(D1, "", "unknown"), row.Key);
        Assert.Equal(new PosApiEntryUsageDelta(1, 1, 0), row.Value);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/swagger/index.html")]
    [InlineData("/apiary")]
    public async Task Middleware_ignores_non_api_paths(string path)
    {
        var called = false;
        var (middleware, counter) = CreateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(Context(path, entry: "edge", storeCode: "S001"));

        Assert.True(called);
        Assert.Empty(counter.Drain(D1));
    }

    [Fact]
    public async Task Middleware_counts_an_escaping_exception_as_5xx_and_rethrows()
    {
        var (middleware, counter) = CreateMiddleware(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(Context("/api/v1/orders", entry: "direct", storeCode: "S001")));

        // 异常冒泡时响应状态码还是默认 200，必须显式记成 500。
        Assert.Equal(new PosApiEntryUsageDelta(1, 0, 1), Assert.Single(counter.Drain(D1)).Value);
    }

    [Fact]
    public async Task Middleware_counts_a_client_abort_as_4xx_not_5xx()
    {
        using var cts = new CancellationTokenSource();
        var context = Context("/api/v1/catalog/products", entry: "edge", storeCode: "S001");
        context.RequestAborted = cts.Token;
        var (middleware, counter) = CreateMiddleware(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        Assert.Equal(new PosApiEntryUsageDelta(1, 1, 0), Assert.Single(counter.Drain(D1)).Value);
    }

    [Fact]
    public async Task Middleware_counts_any_exception_thrown_after_the_client_disconnected_as_4xx()
    {
        // 线上实例：客户端断开后 SqlClient 抛 SqlException "Operation cancelled by user"，
        // 不是 OperationCanceledException；只要 RequestAborted 已触发就是客户端断开。
        using var cts = new CancellationTokenSource();
        var context = Context("/api/v1/devices/runtime-status", entry: "aws", storeCode: "S001");
        context.RequestAborted = cts.Token;
        var (middleware, counter) = CreateMiddleware(_ =>
        {
            cts.Cancel();
            throw new InvalidOperationException("Operation cancelled by user.");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        Assert.Equal(new PosApiEntryUsageDelta(1, 1, 0), Assert.Single(counter.Drain(D1)).Value);
    }

    [Fact]
    public async Task Middleware_counts_unexpected_end_of_request_content_as_4xx_even_before_abort_is_signalled()
    {
        // 请求体没传完客户端就断了：Kestrel 抛 BadHttpRequestException，此时 RequestAborted 未必已触发。
        var (middleware, counter) = CreateMiddleware(_ =>
            throw new BadHttpRequestException("Unexpected end of request content.", StatusCodes.Status400BadRequest));

        await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            middleware.InvokeAsync(Context("/api/v1/devices/runtime-status", entry: "aws", storeCode: "S001")));

        Assert.Equal(new PosApiEntryUsageDelta(1, 1, 0), Assert.Single(counter.Drain(D1)).Value);
    }

    [Fact]
    public async Task Middleware_counts_unexpected_end_of_request_content_wrapped_in_another_exception_as_4xx()
    {
        var (middleware, counter) = CreateMiddleware(_ =>
            throw new InvalidOperationException(
                "binding failed",
                new BadHttpRequestException("Unexpected end of request content.", StatusCodes.Status400BadRequest)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(Context("/api/v1/orders", entry: "aws", storeCode: "S001")));

        Assert.Equal(new PosApiEntryUsageDelta(1, 1, 0), Assert.Single(counter.Drain(D1)).Value);
    }

    [Fact]
    public async Task Middleware_still_counts_other_bad_requests_and_real_failures_as_5xx_when_the_client_is_still_connected()
    {
        // 不能把所有异常都算成断开：客户端仍连着时，其他 BadHttpRequestException 和 SQL 超时之类仍记 5xx。
        var (middleware, counter) = CreateMiddleware(_ =>
            throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

        await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            middleware.InvokeAsync(Context("/api/v1/orders", entry: "aws", storeCode: "S001")));

        Assert.Equal(new PosApiEntryUsageDelta(1, 0, 1), Assert.Single(counter.Drain(D1)).Value);
    }

    [Fact]
    public async Task Middleware_never_breaks_the_response_when_recording_fails()
    {
        // 时钟抛异常：统计失败必须被吞掉，响应照常。
        var (middleware, counter) = CreateMiddleware(
            context =>
            {
                context.Response.StatusCode = 200;
                return Task.CompletedTask;
            },
            new ThrowingTimeProvider());

        await middleware.InvokeAsync(Context("/api/v1/health", entry: "edge"));

        Assert.Empty(counter.Drain(D1));
    }

    // ---------------------------------------------------------------- 落库服务

    private sealed class RecordingWriter : IPosApiEntryUsageWriter
    {
        public bool Fail { get; set; }
        public List<IReadOnlyList<KeyValuePair<PosApiEntryUsageKey, PosApiEntryUsageDelta>>> Batches { get; } = [];

        public Task UpsertAsync(
            IReadOnlyList<KeyValuePair<PosApiEntryUsageKey, PosApiEntryUsageDelta>> deltas,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("Invalid object name 'dbo.PosApiEntryUsageDaily'.");
            }

            Batches.Add(deltas);
            return Task.CompletedTask;
        }
    }

    private static PosApiEntryUsageFlushService CreateFlushService(
        PosApiEntryUsageCounter counter,
        RecordingWriter writer,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IPosApiEntryUsageWriter>(_ => writer);
        var provider = services.BuildServiceProvider();
        return new PosApiEntryUsageFlushService(
            counter,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new PosApiEntryTests.MutableTimeProvider(new DateTimeOffset(2026, 10, 24, 1, 0, 0, TimeSpan.Zero)),
            NullLogger<PosApiEntryUsageFlushService>.Instance,
            delay);
    }

    [Fact]
    public async Task Flush_writes_pending_deltas_once_and_skips_when_nothing_is_pending()
    {
        var counter = new PosApiEntryUsageCounter();
        var writer = new RecordingWriter();
        var service = CreateFlushService(counter, writer);

        Assert.True(await service.FlushAsync(CancellationToken.None));
        Assert.Empty(writer.Batches);

        counter.Record(D1, "S001", "edge", 200);
        counter.Record(D1, "S001", "edge", 500);
        Assert.True(await service.FlushAsync(CancellationToken.None));
        var batch = Assert.Single(writer.Batches);
        Assert.Equal(new PosApiEntryUsageDelta(2, 0, 1), Assert.Single(batch).Value);

        // 已写过的增量不会再写第二遍。
        Assert.True(await service.FlushAsync(CancellationToken.None));
        Assert.Single(writer.Batches);
    }

    [Fact]
    public async Task Flush_keeps_the_counts_when_the_write_fails_and_retries_them_next_time()
    {
        var counter = new PosApiEntryUsageCounter();
        var writer = new RecordingWriter { Fail = true };
        var service = CreateFlushService(counter, writer);

        counter.Record(D1, "S001", "edge", 200);
        // 失败（例如迁移还没执行）：不抛出、返回 false，计数保留。
        Assert.False(await service.FlushAsync(CancellationToken.None));
        counter.Record(D1, "S001", "edge", 200);
        Assert.False(await service.FlushAsync(CancellationToken.None));
        Assert.Empty(writer.Batches);

        // 恢复后一次性写入全部累计，没有丢也没有重复。
        writer.Fail = false;
        Assert.True(await service.FlushAsync(CancellationToken.None));
        Assert.Equal(2, Assert.Single(Assert.Single(writer.Batches)).Value.Requests);
    }

    [Fact]
    public async Task Service_flushes_on_every_interval_and_once_more_on_graceful_stop()
    {
        var counter = new PosApiEntryUsageCounter();
        var writer = new RecordingWriter();
        var delayCalls = 0;
        var firstTickReached = new TaskCompletionSource();
        var releaseSecondTick = new TaskCompletionSource();
        var service = CreateFlushService(counter, writer, async (interval, token) =>
        {
            Assert.Equal(PosApiEntryUsageFlushService.FlushInterval, interval);
            if (Interlocked.Increment(ref delayCalls) == 1)
            {
                return; // 第一个周期立即到期
            }

            firstTickReached.TrySetResult();
            await releaseSecondTick.Task.WaitAsync(token); // 之后一直等到停机取消
        });

        counter.Record(D1, "S001", "edge", 200);
        await service.StartAsync(CancellationToken.None);
        await firstTickReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(writer.Batches);

        // 周期之后又来了请求，停机时必须补刷一次。
        counter.Record(D1, "S001", "edge", 200);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(2, writer.Batches.Count);
    }

    // ---------------------------------------------------------------- upsert SQL 形状

    [Fact]
    public void BuildUpsertSql_wraps_every_row_in_one_serializable_transaction()
    {
        var sql = SqlSugarPosApiEntryUsageWriter.BuildUpsertSql(2);

        Assert.Contains("SET XACT_ABORT ON;", sql);
        Assert.Contains("BEGIN TRANSACTION;", sql);
        Assert.Contains("COMMIT TRANSACTION;", sql);
        Assert.Equal(2, CountOccurrences(sql, "WITH (UPDLOCK, SERIALIZABLE)"));
        Assert.Equal(2, CountOccurrences(sql, "IF @@ROWCOUNT = 0"));
        // 累加而不是覆盖：多个 POS API 实例 / 重试都不会互相冲掉。
        Assert.Contains("[RequestCount] = [RequestCount] + @R1", sql);
        Assert.Contains("[Status4xxCount] = [Status4xxCount] + @C1", sql);
        Assert.Contains("[Status5xxCount] = [Status5xxCount] + @F1", sql);
        Assert.DoesNotContain("@R2", sql);
    }

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

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("clock failure");
    }
}
