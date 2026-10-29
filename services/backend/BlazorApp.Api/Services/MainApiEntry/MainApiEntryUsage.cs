using System.Collections.Concurrent;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.PosApiEntry;
using SqlSugar;

namespace BlazorApp.Api.Services.MainApiEntry;

// 主 API（Web 后台 + 员工 App，/api/*）三条入口（edge / aws / direct）的用量统计。
// 与收银端 Hbpos.Api 的 PosApiEntryUsage.cs 同构（POS API 与主 API 是两个互不引用的项目，所以各写一份；改其一时请同步另一份）：
// 中间件在每个 /api/* 请求结束后把 (悉尼日期, 入口, 客户端类别) 的请求数 / 4xx / 5xx 累加进进程内计数器，
// 后台服务每 60 秒把增量批量 upsert 到 HBweb 库 dbo.MainApiEntryUsageDaily（迁移 20261027.001），优雅停机时再刷一次。
// 差异：没有分店维度（Web 用户是员工账号），改为客户端类别 web / app / other，由 User-Agent 粗分，只记数、不存 UA 原文 / IP / 用户。
// 统计是纯展示用途：任何一步失败都只记日志，绝不能影响响应。

/// <summary>统计口径里的「悉尼日期」：与收银端 / 后台日统计一致，按 Australia/Sydney 切日。</summary>
public static class MainApiEntryUsageClock
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

/// <summary>
/// 客户端类别（白名单，固定三个取值，防止 User-Agent 被乱写撑大统计表）：
/// web＝浏览器（Web 后台）；app＝员工 App（React Native / Expo，非浏览器的原生网络栈）；other＝脚本、监控、爬虫及其余。
/// </summary>
public static class MainApiClientKinds
{
    public const string Web = "web";
    public const string App = "app";
    public const string Other = "other";

    /// <summary>输出顺序固定。</summary>
    public static readonly IReadOnlyList<string> All = [Web, App, Other];

    // 只检查 UA 前 512 个字符：正常 UA 都在 200 字符内，防止超长垃圾头拖慢热路径。
    private const int MaxInspectedLength = 512;

    // 员工 App（apps/mobile，Expo + React Native + axios）没有设置自定义 User-Agent 或客户端标识头，
    // 请求走 React Native 的原生网络栈，UA 由系统给出：Android 为 okhttp/x.y.z，iOS 为 「应用名/构建号 CFNetwork/x Darwin/y」。
    // Expo Go / 部分库会带 Expo、ReactNative 字样；Dalvik/ 是 Android 的 HttpURLConnection 默认 UA。
    private static readonly string[] AppMarkers = ["okhttp", "cfnetwork", "expo", "reactnative", "react-native", "dalvik/"];

    // 监控 / 爬虫 / 命令行工具：即便伪装成 Mozilla 也归 other，避免把探活流量算成 Web 用户。
    private static readonly string[] AutomationMarkers =
    [
        "bot", "spider", "crawl", "monitor", "uptime", "probe", "pingdom", "curl/", "wget/", "python", "go-http-client", "java/", "postman", "httpclient",
    ];

    /// <summary>
    /// 粗分客户端类别。顺序：先认 App 的原生网络栈标记（它们都不带 Mozilla），再排除自动化工具，
    /// 最后以 Mozilla/ 或 Opera/ 开头视为浏览器；空 UA 与其余都归 other。永不返回白名单以外的值。
    /// </summary>
    public static string Classify(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return Other;
        }

        var text = userAgent.Length > MaxInspectedLength ? userAgent[..MaxInspectedLength] : userAgent;
        foreach (var marker in AppMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return App;
            }
        }

        foreach (var marker in AutomationMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return Other;
            }
        }

        var trimmed = text.TrimStart();
        return trimmed.StartsWith("Mozilla/", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Opera/", StringComparison.OrdinalIgnoreCase)
            ? Web
            : Other;
    }

    /// <summary>白名单归一化（库里的脏值 / 未知值落到 other）。</summary>
    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();
        return trimmed is Web or App or Other ? trimmed : Other;
    }
}

/// <summary>主 API 入口请求头：源站 nginx 在 /api/ location 里按实际到达入口写入（并覆盖客户端自带的同名头）。</summary>
public static class MainApiEntryHeader
{
    public const string Name = "X-HB-Entry";

    /// <summary>读取请求里的 X-HB-Entry：只接受恰好一个头值且在白名单内（edge / aws / direct），其余返回 null。</summary>
    public static string? ReadFromRequest(HttpRequest? request)
    {
        if (request is null || !request.Headers.TryGetValue(Name, out var values) || values.Count != 1)
        {
            return null;
        }

        return PosApiEntryModes.Normalize(values[0]);
    }
}

/// <summary>计数器的聚合键：悉尼日期 + 入口（edge / aws / direct / unknown）+ 客户端类别（web / app / other）。</summary>
public readonly record struct MainApiEntryUsageKey(DateOnly Date, string Entry, string ClientKind);

/// <summary>一段时间内累计的增量；4xx / 5xx 是 Requests 的子集。</summary>
public readonly record struct MainApiEntryUsageDelta(long Requests, long Status4xx, long Status5xx)
{
    public bool IsEmpty => Requests == 0 && Status4xx == 0 && Status5xx == 0;
}

/// <summary>
/// 进程内用量计数器（单例）。热路径只有一次字典查找 + Interlocked 加法，不加锁、不分配（命中已有键时）。
/// 键的取值被白名单限定（入口 4 种 × 类别 3 种 × 日期），理论上只有十几个键；
/// <see cref="MaxKeys"/> 只是防御性上限，超过后丢弃新键的计数并累计丢弃次数。
/// </summary>
public sealed class MainApiEntryUsageCounter
{
    public const string UnknownEntry = "unknown";

    internal const int MaxKeys = 2_000;

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
        public MainApiEntryUsageDelta Take() => new(
            Interlocked.Exchange(ref requests, 0),
            Interlocked.Exchange(ref status4xx, 0),
            Interlocked.Exchange(ref status5xx, 0));
    }

    private readonly ConcurrentDictionary<MainApiEntryUsageKey, Cell> cells = new();
    private int keyCount;
    private long droppedRequests;

    /// <summary>因键数达到上限而被丢弃计数的请求数（仅供诊断）。</summary>
    public long DroppedRequests => Interlocked.Read(ref droppedRequests);

    /// <summary>当前持有的键数（仅供测试 / 诊断）。</summary>
    internal int KeyCount => Volatile.Read(ref keyCount);

    /// <summary>
    /// 记录一个请求。入口与客户端类别都按白名单归一化（非法 → unknown / other）；
    /// 状态码 400~499 记 4xx，500 及以上记 5xx，其余只计请求数。
    /// </summary>
    public void Record(DateOnly date, string? entry, string? clientKind, int statusCode)
    {
        var key = new MainApiEntryUsageKey(date, NormalizeEntry(entry), MainApiClientKinds.Normalize(clientKind));
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
    public List<KeyValuePair<MainApiEntryUsageKey, MainApiEntryUsageDelta>> Drain(DateOnly today)
    {
        var result = new List<KeyValuePair<MainApiEntryUsageKey, MainApiEntryUsageDelta>>();
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
    public void Restore(IEnumerable<KeyValuePair<MainApiEntryUsageKey, MainApiEntryUsageDelta>> deltas)
    {
        foreach (var (key, delta) in deltas)
        {
            GetOrCreateCell(key, enforceLimit: false)!.Add(delta.Requests, delta.Status4xx, delta.Status5xx);
        }
    }

    private Cell? GetOrCreateCell(MainApiEntryUsageKey key, bool enforceLimit)
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
}

/// <summary>
/// 统计中间件：放在管线最外层，这样限流 429、鉴权 401 也会计入；只统计 /api/*（含健康检查），
/// 不统计静态资源、Swagger 等非业务路径。整个记录过程包在 try/catch 里：统计出任何问题都不能影响响应。
/// </summary>
public sealed class MainApiEntryUsageMiddleware(
    RequestDelegate next,
    MainApiEntryUsageCounter counter,
    TimeProvider timeProvider,
    ILogger<MainApiEntryUsageMiddleware> logger)
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
            // 客户端主动断开引起的取消按 nginx 习惯记 499（落在 4xx），其余算服务端 5xx。
            statusOverride = exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested
                ? 499
                : 500;
            throw;
        }
        finally
        {
            RecordSafely(context, statusOverride);
        }
    }

    private void RecordSafely(HttpContext context, int statusOverride)
    {
        try
        {
            counter.Record(
                MainApiEntryUsageClock.SydneyDate(timeProvider.GetUtcNow()),
                MainApiEntryHeader.ReadFromRequest(context.Request),
                MainApiClientKinds.Classify(context.Request.Headers.UserAgent.ToString()),
                statusOverride != 0 ? statusOverride : context.Response.StatusCode);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to record main API entry usage.");
        }
    }
}

// ───────────────────────── 落库 ─────────────────────────

public interface IMainApiEntryUsageWriter
{
    /// <summary>把增量累加进 dbo.MainApiEntryUsageDaily（不存在的行插入，存在的行累加）。</summary>
    Task UpsertAsync(
        IReadOnlyList<KeyValuePair<MainApiEntryUsageKey, MainApiEntryUsageDelta>> deltas,
        DateTime nowUtc,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarMainApiEntryUsageWriter : IMainApiEntryUsageWriter
{
    // 每批行数：每行 6 个参数，100 行 ≈ 600 个，远低于 SQL Server 的 2100 参数上限。
    internal const int RowsPerBatch = 100;

    private readonly ISqlSugarClient db;

    public SqlSugarMainApiEntryUsageWriter(SqlSugarContext dbContext)
        : this(dbContext.Db)
    {
    }

    // 供集成测试直接传入连到隔离库的客户端。
    internal SqlSugarMainApiEntryUsageWriter(ISqlSugarClient db)
    {
        this.db = db;
    }

    public async Task UpsertAsync(
        IReadOnlyList<KeyValuePair<MainApiEntryUsageKey, MainApiEntryUsageDelta>> deltas,
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
                parameters.Add(new SugarParameter($"@E{i}", key.Entry, System.Data.DbType.String));
                parameters.Add(new SugarParameter($"@K{i}", key.ClientKind, System.Data.DbType.String));
                parameters.Add(new SugarParameter($"@R{i}", delta.Requests));
                parameters.Add(new SugarParameter($"@C{i}", delta.Status4xx));
                parameters.Add(new SugarParameter($"@F{i}", delta.Status5xx));
            }

            await db.Ado.ExecuteCommandAsync(BuildUpsertSql(batch.Count), parameters);
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
                UPDATE [dbo].[MainApiEntryUsageDaily] WITH (UPDLOCK, SERIALIZABLE)
                SET [RequestCount] = [RequestCount] + @R{i},
                    [Status4xxCount] = [Status4xxCount] + @C{i},
                    [Status5xxCount] = [Status5xxCount] + @F{i},
                    [UpdatedAt] = @Now
                WHERE [UsageDate] = @D{i} AND [Entry] = @E{i} AND [ClientKind] = @K{i};
                IF @@ROWCOUNT = 0
                    INSERT INTO [dbo].[MainApiEntryUsageDaily]
                        ([UsageDate], [Entry], [ClientKind], [RequestCount], [Status4xxCount], [Status5xxCount], [UpdatedAt])
                    VALUES (@D{i}, @E{i}, @K{i}, @R{i}, @C{i}, @F{i}, @Now);
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
public sealed class MainApiEntryUsageFlushService : BackgroundService
{
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(10);

    private readonly MainApiEntryUsageCounter counter;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<MainApiEntryUsageFlushService> logger;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private DateTimeOffset lastFailureLoggedAt = DateTimeOffset.MinValue;

    public MainApiEntryUsageFlushService(
        MainApiEntryUsageCounter counter,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<MainApiEntryUsageFlushService> logger,
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
        var deltas = counter.Drain(MainApiEntryUsageClock.SydneyDate(now));
        if (deltas.Count == 0)
        {
            return true;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var writer = scope.ServiceProvider.GetRequiredService<IMainApiEntryUsageWriter>();
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
                    "Failed to flush main API entry usage ({Rows} rows kept for retry). If the table is missing, run the HBweb migration 20261027.001.",
                    deltas.Count);
            }

            return false;
        }
    }
}
