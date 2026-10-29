using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using Hbpos.Api.Data;
using Hbpos.Contracts.Devices;
using SqlSugar;

namespace Hbpos.Api.Services;

// 收银端三条入口（edge / aws / direct）的用量统计：
// 中间件在每个 /api/* 请求结束后把 (悉尼日期, 分店, 入口) 的请求数 / 4xx / 5xx 累加进进程内计数器，
// 后台服务每 60 秒把增量批量 upsert 到 HBweb 库 dbo.PosApiEntryUsageDaily（迁移 20261026.004），优雅停机时再刷一次。
// 统计是纯展示用途：任何一步失败都只记日志，绝不能影响响应。

/// <summary>统计口径里的「悉尼日期」：与后台日统计一致，按 Australia/Sydney 切日。</summary>
public static class PosApiEntryUsageClock
{
    private static readonly TimeZoneInfo SydneyTimeZone = ResolveSydneyTimeZone();

    private static TimeZoneInfo ResolveSydneyTimeZone()
    {
        foreach (var id in new[] { "Australia/Sydney", "AUS Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        // 极端环境没有时区数据库：退化为固定 UTC+10，宁可夏令时期间日界线偏 1 小时也不能让统计抛异常。
        return TimeZoneInfo.CreateCustomTimeZone("AEST", TimeSpan.FromHours(10), "AEST", "AEST");
    }

    public static DateOnly SydneyDate(DateTimeOffset utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, SydneyTimeZone).DateTime);
}

/// <summary>计数器的聚合键：悉尼日期 + 分店代码（未鉴权为空串）+ 入口（edge / aws / direct / unknown）。</summary>
public readonly record struct PosApiEntryUsageKey(DateOnly Date, string StoreCode, string Entry);

/// <summary>一段时间内累计的增量；4xx / 5xx 是 RequestCount 的子集。</summary>
public readonly record struct PosApiEntryUsageDelta(long Requests, long Status4xx, long Status5xx)
{
    public bool IsEmpty => Requests == 0 && Status4xx == 0 && Status5xx == 0;
}

/// <summary>
/// 进程内用量计数器（单例）。热路径只有一次字典查找 + Interlocked 加法，不加锁、不分配（命中已有键时）。
/// 内存上限：键数（日期 × 分店 × 入口）超过 <see cref="MaxKeys"/> 时丢弃新键的计数，只累计丢弃次数，防止异常数据撑爆内存。
/// </summary>
public sealed class PosApiEntryUsageCounter
{
    /// <summary>未带 / 带了非法 X-HB-Entry（绕过 nginx 的直连等）的入口代号。</summary>
    public const string UnknownEntry = "unknown";

    /// <summary>分店代码长度上限，与 dbo.Store.StoreCode（nvarchar(50)）一致。</summary>
    public const int MaxStoreCodeLength = 50;

    // 正常规模：33 家店 × 4 个入口 × 2~3 个日期，约几百个键；2 万远高于合理上限。
    internal const int MaxKeys = 20_000;

    private sealed class Cell
    {
        private long requests;
        private long status4xx;
        private long status5xx;

        public void Add(long request, long s4xx, long s5xx)
        {
            if (request != 0) Interlocked.Add(ref requests, request);
            if (s4xx != 0) Interlocked.Add(ref status4xx, s4xx);
            if (s5xx != 0) Interlocked.Add(ref status5xx, s5xx);
        }

        /// <summary>原子取走当前累计值并清零（Exchange 保证并发 Add 不会丢失）。</summary>
        public PosApiEntryUsageDelta Take() => new(
            Interlocked.Exchange(ref requests, 0),
            Interlocked.Exchange(ref status4xx, 0),
            Interlocked.Exchange(ref status5xx, 0));
    }

    private readonly ConcurrentDictionary<PosApiEntryUsageKey, Cell> cells = new();
    private int keyCount;
    private long droppedRequests;

    /// <summary>因键数达到上限而被丢弃计数的请求数（仅供诊断）。</summary>
    public long DroppedRequests => Interlocked.Read(ref droppedRequests);

    /// <summary>当前持有的键数（仅供测试 / 诊断）。</summary>
    internal int KeyCount => Volatile.Read(ref keyCount);

    /// <summary>
    /// 记录一个请求。入口按白名单归一化（非法 → unknown），分店代码去空白并截断；
    /// 状态码 400~499 记 4xx，500 及以上记 5xx，其余只计请求数。
    /// </summary>
    public void Record(DateOnly date, string? storeCode, string? entry, int statusCode)
    {
        var key = new PosApiEntryUsageKey(date, NormalizeStoreCode(storeCode), NormalizeEntry(entry));
        var cell = GetOrCreateCell(key, enforceLimit: true);
        if (cell is null)
        {
            Interlocked.Increment(ref droppedRequests);
            return;
        }

        cell.Add(1, statusCode is >= 400 and < 500 ? 1 : 0, statusCode >= 500 ? 1 : 0);
    }

    /// <summary>
    /// 取走所有键的累计增量（返回非空增量）。早于 <paramref name="today"/> 的日期键一旦已清空就回收，
    /// 避免跨日后字典里永远留着历史键；回收时若恰好有迟到的计数，一并取走并作为增量返回，不丢数。
    /// </summary>
    public List<KeyValuePair<PosApiEntryUsageKey, PosApiEntryUsageDelta>> Drain(DateOnly today)
    {
        var result = new List<KeyValuePair<PosApiEntryUsageKey, PosApiEntryUsageDelta>>();
        foreach (var (key, cell) in cells)
        {
            var delta = cell.Take();
            if (!delta.IsEmpty)
            {
                result.Add(new(key, delta));
                continue;
            }

            if (key.Date < today && cells.TryRemove(key, out var removed))
            {
                Interlocked.Decrement(ref keyCount);
                var late = removed.Take();
                if (!late.IsEmpty)
                {
                    result.Add(new(key, late));
                }
            }
        }

        return result;
    }

    /// <summary>写库失败后把取走的增量放回去，下个周期重试。放回不受键数上限约束（这些键本来就在统计范围内）。</summary>
    public void Restore(IEnumerable<KeyValuePair<PosApiEntryUsageKey, PosApiEntryUsageDelta>> deltas)
    {
        foreach (var (key, delta) in deltas)
        {
            GetOrCreateCell(key, enforceLimit: false)!.Add(delta.Requests, delta.Status4xx, delta.Status5xx);
        }
    }

    private Cell? GetOrCreateCell(PosApiEntryUsageKey key, bool enforceLimit)
    {
        while (true)
        {
            if (cells.TryGetValue(key, out var existing))
            {
                return existing;
            }

            if (enforceLimit && Volatile.Read(ref keyCount) >= MaxKeys)
            {
                return null;
            }

            var created = new Cell();
            if (cells.TryAdd(key, created))
            {
                Interlocked.Increment(ref keyCount);
                return created;
            }
            // TryAdd 失败说明别的线程刚加了同一个键，回到循环顶部取它。
        }
    }

    internal static string NormalizeEntry(string? entry) => PosApiEntryModes.Normalize(entry) ?? UnknownEntry;

    internal static string NormalizeStoreCode(string? storeCode)
    {
        var trimmed = storeCode?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        return trimmed.Length > MaxStoreCodeLength ? trimmed[..MaxStoreCodeLength] : trimmed;
    }
}

/// <summary>
/// 统计中间件：放在管线最外层，这样限流 429、鉴权 401 也会计入；只统计 /api/*（含 /api/v1/health），
/// 不统计 OpenAPI / Swagger 等非业务路径。分店代码取已鉴权设备的 StoreCode claim（HttpContext.User 在 next 返回后仍可读），
/// 未鉴权请求记空串。整个记录过程包在 try/catch 里：统计出任何问题都不能影响响应。
/// </summary>
public sealed class PosApiEntryUsageMiddleware(
    RequestDelegate next,
    PosApiEntryUsageCounter counter,
    TimeProvider timeProvider,
    ILogger<PosApiEntryUsageMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var statusOverride = 0;
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            // 异常冒泡出管线时响应状态码还是默认的 200（Kestrel 事后才改成 500），所以这里显式覆盖：
            // 客户端主动断开引起的异常按 nginx 习惯记 499（落在 4xx），其余算服务端 5xx。
            statusOverride = IsClientDisconnect(exception, context) ? 499 : 500;
            throw;
        }
        finally
        {
            RecordSafely(context, statusOverride);
        }
    }

    /// <summary>
    /// 异常是否由客户端中途断开连带引起。边缘机（nginx）在客户端断开时会立刻关闭到源站的连接，
    /// 这类异常不能记成服务端 5xx，否则只走边缘机入口的线路会凭空多出 5xx。
    /// </summary>
    private static bool IsClientDisconnect(Exception exception, HttpContext context)
    {
        // 连接已断开（RequestAborted 已触发）时，底层抛出的异常类型五花八门：
        // OperationCanceledException、SqlClient 的 SqlException "Operation cancelled by user"……
        // 所以只认 RequestAborted，不按异常类型猜。
        if (context.RequestAborted.IsCancellationRequested)
        {
            return true;
        }

        // 请求体还没读完客户端就走了：Kestrel 抛 BadHttpRequestException（Unexpected end of request content），
        // 这时 RequestAborted 不一定已经触发，单独识别。其他 BadHttpRequestException（如请求体过大）不在此列。
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is BadHttpRequestException
                && current.Message.Contains("Unexpected end of request content", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void RecordSafely(HttpContext context, int statusOverride)
    {
        try
        {
            counter.Record(
                PosApiEntryUsageClock.SydneyDate(timeProvider.GetUtcNow()),
                context.User?.FindFirstValue(DeviceAuthConstants.StoreCodeClaim),
                PosApiEntryModes.ReadFromRequest(context.Request),
                statusOverride != 0 ? statusOverride : context.Response.StatusCode);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to record POS API entry usage.");
        }
    }
}

// ───────────────────────── 落库 ─────────────────────────

public interface IPosApiEntryUsageWriter
{
    /// <summary>把增量累加进 dbo.PosApiEntryUsageDaily（不存在的行插入，存在的行累加）。</summary>
    Task UpsertAsync(
        IReadOnlyList<KeyValuePair<PosApiEntryUsageKey, PosApiEntryUsageDelta>> deltas,
        DateTime nowUtc,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarPosApiEntryUsageWriter(HbposSqlSugarContext dbContext) : IPosApiEntryUsageWriter
{
    // 每批行数：每行 6 个参数，100 行 ≈ 600 个，远低于 SQL Server 的 2100 参数上限。
    internal const int RowsPerBatch = 100;

    public async Task UpsertAsync(
        IReadOnlyList<KeyValuePair<PosApiEntryUsageKey, PosApiEntryUsageDelta>> deltas,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < deltas.Count; offset += RowsPerBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = deltas.Skip(offset).Take(RowsPerBatch).ToList();
            var parameters = new List<SugarParameter> { new("@Now", nowUtc) };
            for (var i = 0; i < batch.Count; i++)
            {
                var (key, delta) = (batch[i].Key, batch[i].Value);
                parameters.Add(new SugarParameter($"@D{i}", key.Date.ToDateTime(TimeOnly.MinValue)) { DbType = System.Data.DbType.Date });
                parameters.Add(new SugarParameter($"@S{i}", key.StoreCode, System.Data.DbType.String));
                parameters.Add(new SugarParameter($"@E{i}", key.Entry, System.Data.DbType.String));
                parameters.Add(new SugarParameter($"@R{i}", delta.Requests));
                parameters.Add(new SugarParameter($"@C{i}", delta.Status4xx));
                parameters.Add(new SugarParameter($"@F{i}", delta.Status5xx));
            }

            await dbContext.MainDb.Ado.ExecuteCommandAsync(BuildUpsertSql(batch.Count), parameters);
        }
    }

    /// <summary>
    /// 一批 N 行的 upsert 脚本：每行「UPDATE（UPDLOCK + SERIALIZABLE 锁住键范围）→ 没命中则 INSERT」，
    /// 整批在一个事务里（XACT_ABORT ON，任何一行出错整批回滚，由调用方放回计数器重试，不会重复累加）。
    /// UPDLOCK + SERIALIZABLE 保证多实例并发写同一键时不会双插主键冲突。
    /// </summary>
    internal static string BuildUpsertSql(int rowCount)
    {
        var sql = new StringBuilder();
        sql.AppendLine("SET NOCOUNT ON;");
        sql.AppendLine("SET XACT_ABORT ON;");
        sql.AppendLine("BEGIN TRANSACTION;");
        for (var i = 0; i < rowCount; i++)
        {
            sql.AppendLine($"""
                UPDATE [dbo].[PosApiEntryUsageDaily] WITH (UPDLOCK, SERIALIZABLE)
                SET [RequestCount] = [RequestCount] + @R{i},
                    [Status4xxCount] = [Status4xxCount] + @C{i},
                    [Status5xxCount] = [Status5xxCount] + @F{i},
                    [UpdatedAt] = @Now
                WHERE [UsageDate] = @D{i} AND [StoreCode] = @S{i} AND [Entry] = @E{i};
                IF @@ROWCOUNT = 0
                    INSERT INTO [dbo].[PosApiEntryUsageDaily]
                        ([UsageDate], [StoreCode], [Entry], [RequestCount], [Status4xxCount], [Status5xxCount], [UpdatedAt])
                    VALUES (@D{i}, @S{i}, @E{i}, @R{i}, @C{i}, @F{i}, @Now);
                """);
        }

        sql.AppendLine("COMMIT TRANSACTION;");
        return sql.ToString();
    }
}

/// <summary>
/// 每 60 秒把计数器里的增量批量写库；停机时再刷一次（最多等 5 秒）。
/// 写库失败（表还没迁移、数据库暂时不可用等）只记日志并把增量放回计数器，下个周期重试；
/// 失败日志按 10 分钟限频，避免迁移前每分钟刷一条。
/// </summary>
public sealed class PosApiEntryUsageFlushService : BackgroundService
{
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(10);

    private readonly PosApiEntryUsageCounter counter;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<PosApiEntryUsageFlushService> logger;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private DateTimeOffset lastFailureLoggedAt = DateTimeOffset.MinValue;

    public PosApiEntryUsageFlushService(
        PosApiEntryUsageCounter counter,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<PosApiEntryUsageFlushService> logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        this.counter = counter;
        this.scopeFactory = scopeFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.delayAsync = delayAsync ?? Task.Delay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await delayAsync(FlushInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await FlushAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // 优雅停机的最后一刷：不用已经被取消的停机令牌，单独给 5 秒；超时 / 失败只记日志（这一小段计数随进程丢失）。
        using var timeout = new CancellationTokenSource(ShutdownFlushTimeout);
        await FlushAsync(timeout.Token);
    }

    /// <summary>刷一次；永不抛出。返回 true 表示没有待写增量或写入成功。</summary>
    public async Task<bool> FlushAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var deltas = counter.Drain(PosApiEntryUsageClock.SydneyDate(now));
        if (deltas.Count == 0)
        {
            return true;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var writer = scope.ServiceProvider.GetRequiredService<IPosApiEntryUsageWriter>();
            await writer.UpsertAsync(deltas, now.UtcDateTime, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            // 事务整批回滚，所以整批放回计数器是安全的（不会重复累加）。
            counter.Restore(deltas);
            if (exception is not OperationCanceledException && now - lastFailureLoggedAt >= FailureLogInterval)
            {
                lastFailureLoggedAt = now;
                logger.LogWarning(
                    exception,
                    "Failed to flush POS API entry usage ({Rows} rows kept for retry). If the table is missing, run the HBweb migration 20261026.004.",
                    deltas.Count);
            }

            return false;
        }
    }
}
