using Microsoft.Data.SqlClient;

namespace BlazorApp.FaceIngest;

/// <summary>
/// FaceStoreProfile 一行（设计第 13.3、14.4 节的列；接收端只读取与自己决策相关的列，未校验，字段可能为空）。
/// 列名与含义严格照设计，不自行增列。
/// </summary>
internal sealed record FaceStoreProfileRow(
    string StoreCode,
    byte Mode,
    bool EvaluateMatch,
    bool StaffExclusionEnabled,
    bool TrafficDedupEnabled,
    bool UnwelcomeListEnabled,
    string? AllowedSubjects,
    string? CrossBorderRegion,
    string? CrossBorderReviewRef,
    string? CrossBorderReviewedBy,
    DateTime? CrossBorderReviewedAt,
    string? TimeZoneId,
    string? UnwelcomeAssessmentRef,
    string? UnwelcomeLegalSignoffBy,
    DateTime? UnwelcomeLegalSignoffAt);

/// <summary>读取门店档案。抽成接口是为了测试能用假仓储模拟「首次读取失败 / 刷新失败 / 档案变化」。</summary>
internal interface IFaceStoreProfileRepository
{
    /// <summary>读取 FaceStoreProfile 全表。连不上、超时、无权限、表不存在等一律抛异常，由调用方保留旧快照。</summary>
    Task<IReadOnlyList<FaceStoreProfileRow>> LoadAllAsync(CancellationToken cancellationToken);
}

/// <summary>基于 Microsoft.Data.SqlClient 的实现；受限账号 face_ingest_app 对 FaceStoreProfile 只有 SELECT。</summary>
internal sealed class SqlFaceStoreProfileRepository : IFaceStoreProfileRepository
{
    /// <summary>读全表（30 家店规模，不分页）。SQL 用常量便于审查，无任何拼接。</summary>
    internal const string LoadAllSql = """
SELECT [StoreCode], [Mode], [EvaluateMatch], [StaffExclusionEnabled], [TrafficDedupEnabled], [UnwelcomeListEnabled],
       [AllowedSubjects], [CrossBorderRegion], [CrossBorderReviewRef], [CrossBorderReviewedBy], [CrossBorderReviewedAt],
       [TimeZoneId], [UnwelcomeAssessmentRef], [UnwelcomeLegalSignoffBy], [UnwelcomeLegalSignoffAt]
FROM [dbo].[FaceStoreProfile];
""";

    private const int CommandTimeoutSeconds = 15;

    private readonly string _connectionString;

    public SqlFaceStoreProfileRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<FaceStoreProfileRow>> LoadAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(LoadAllSql, connection) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<FaceStoreProfileRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FaceStoreProfileRow(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? (byte)0 : Convert.ToByte(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture),
                !reader.IsDBNull(2) && reader.GetBoolean(2),
                !reader.IsDBNull(3) && reader.GetBoolean(3),
                !reader.IsDBNull(4) && reader.GetBoolean(4),
                !reader.IsDBNull(5) && reader.GetBoolean(5),
                ReadString(reader, 6),
                ReadString(reader, 7),
                ReadString(reader, 8),
                ReadString(reader, 9),
                ReadDateTime(reader, 10),
                ReadString(reader, 11),
                ReadString(reader, 12),
                ReadString(reader, 13),
                ReadDateTime(reader, 14)));
        }

        return rows;
    }

    private static string? ReadString(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime? ReadDateTime(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);
}

/// <summary>
/// 门店档案来源。业务口径（设计第 13.5 节）：
/// - 每 <c>FaceMatch:ProfileRefreshSeconds</c>（默认 30）秒整表读一次，内存缓存，整份替换快照；
/// - <b>从未成功读取过</b>时 <see cref="Snapshot"/> 为 null，调用方一律按 Off；
/// - 刷新失败 → 保留上一份快照继续使用，节流记 Error；
/// - 本类只负责「读到什么」，怎么解释档案（降级、闸门）在 <see cref="StoreModePolicy"/>。
/// </summary>
internal sealed class FaceStoreProfileSource
{
    private static readonly TimeSpan FailureLogWindow = TimeSpan.FromMinutes(5);

    private readonly IFaceStoreProfileRepository _repository;
    private readonly ILogger<FaceStoreProfileSource> _logger;
    private readonly ThrottledLogGate _failureLogGate;
    private IReadOnlyDictionary<string, FaceStoreProfileRow>? _snapshot;
    private int _attempts;
    private long _consecutiveFailures;

    public FaceStoreProfileSource(IFaceStoreProfileRepository repository, TimeProvider timeProvider, ILogger<FaceStoreProfileSource> logger)
    {
        _repository = repository;
        _logger = logger;
        _failureLogGate = new ThrottledLogGate(timeProvider, FailureLogWindow);
    }

    /// <summary>最近一次成功读取的快照（按门店编码，大小写不敏感）；从未成功为 null。</summary>
    public IReadOnlyDictionary<string, FaceStoreProfileRow>? Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>是否至少完成过一次读取尝试（成功或失败）。启动瞬间还没尝试过时，处理器先等一轮，避免把「还没读」当成「读不到」。</summary>
    public bool HasAttempted => Volatile.Read(ref _attempts) > 0;

    /// <summary>刷新一次。成功返回 true；失败返回 false 并保留旧快照。不向外抛异常（取消除外）。</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FaceStoreProfileRow> rows;
        try
        {
            rows = await _repository.LoadAllAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _attempts);
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            if (_failureLogGate.TryAcquire("profile-refresh-failed", out var suppressed))
            {
                // 中文注释：SqlClient 的异常信息不含密码；连接串本身绝不进日志。
                _logger.LogError(
                    ex,
                    "Face store profile refresh failed; {State}. consecutiveFailures={ConsecutiveFailures} suppressedSinceLastLog={SuppressedCount}",
                    Snapshot is null ? "never loaded, all stores are treated as Off" : "keeping previous snapshot",
                    failures,
                    suppressed);
            }

            return false;
        }

        var map = new Dictionary<string, FaceStoreProfileRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            // 中文注释：门店编码不合法或重复的行忽略（重复时保留第一行，主键保证库里不会重复；这里只是防御）。
            if (FaceIngestSettings.IsValidStoreCode(row.StoreCode))
            {
                map.TryAdd(row.StoreCode, row);
            }
        }

        Volatile.Write(ref _snapshot, map);
        Interlocked.Increment(ref _attempts);
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        return true;
    }
}

/// <summary>
/// 档案后台刷新：启动后立即读一次，之后每 ProfileRefreshSeconds 秒一次。首次失败时按较短间隔（不超过 5 秒）重试，
/// 尽快脱离「读不到按 Off」的状态。没配数据库时什么都不做。整轮异常只记 Error，不让服务崩溃。
/// </summary>
internal sealed class FaceStoreProfileRefreshService : BackgroundService
{
    private static readonly TimeSpan NotReadyRetryInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _services;
    private readonly FaceIngestSettingsMonitor _ingestSettings;
    private readonly FaceMatchSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FaceStoreProfileRefreshService> _logger;

    public FaceStoreProfileRefreshService(
        IServiceProvider services,
        FaceIngestSettingsMonitor ingestSettings,
        FaceMatchSettings settings,
        TimeProvider timeProvider,
        ILogger<FaceStoreProfileRefreshService> logger)
    {
        _services = services;
        _ingestSettings = ingestSettings;
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_ingestSettings.Current.DatabaseConfigured)
        {
            return;
        }

        await Task.Yield();
        var source = _services.GetRequiredService<FaceStoreProfileSource>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await source.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Face store profile refresh round failed unexpectedly; will retry.");
            }

            var interval = TimeSpan.FromSeconds(Math.Max(1, _settings.ProfileRefreshSeconds));
            if (source.Snapshot is null && interval > NotReadyRetryInterval)
            {
                interval = NotReadyRetryInterval;
            }

            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
