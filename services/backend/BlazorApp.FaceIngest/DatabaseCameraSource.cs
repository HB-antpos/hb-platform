using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace BlazorApp.FaceIngest;

/// <summary>FaceCamera 表的一行（未校验，字段可能为空或不合法）。</summary>
internal sealed record FaceCameraRow(string? CameraId, string? StoreCode, string? TokenSha256, bool Enabled);

/// <summary>读取摄像机注册表。抽成接口是为了测试能用假仓储模拟「首次加载失败 / 刷新失败 / 非法行」。</summary>
internal interface IFaceCameraRepository
{
    /// <summary>读取 FaceCamera 全表。连不上、超时、无权限等一律抛异常，由调用方保留旧快照。</summary>
    Task<IReadOnlyList<FaceCameraRow>> LoadAllAsync(CancellationToken cancellationToken);
}

/// <summary>基于 Microsoft.Data.SqlClient 的实现；受限账号对 FaceCamera 只有 SELECT + UPDATE(LastSeenAtUtc)。</summary>
internal sealed class SqlFaceCameraRepository : IFaceCameraRepository
{
    /// <summary>读全表（30 台规模，不分页）。SQL 用常量便于审查。</summary>
    internal const string LoadAllSql = """
SELECT [CameraId], [StoreCode], [TokenSha256], [Enabled]
FROM [dbo].[FaceCamera];
""";

    private const int CommandTimeoutSeconds = 15;

    private readonly string _connectionString;

    public SqlFaceCameraRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<FaceCameraRow>> LoadAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(LoadAllSql, connection) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<FaceCameraRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FaceCameraRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                !reader.IsDBNull(3) && reader.GetBoolean(3)));
        }

        return rows;
    }
}

/// <summary>
/// 数据库摄像机来源（配置了 <c>FaceIngest:Database:ConnectionString</c> 时启用）。业务口径：
/// - 读 FaceCamera 全表，内存缓存，由 <see cref="CameraRegistryRefreshService"/> 每 CameraRefreshSeconds 秒刷新；
/// - 从未成功加载过 → <see cref="Current"/> 为 null，接收端点回 503（摄像机断网续传缓存后重推，不丢数据）；
/// - 刷新失败 → 保留上一份合法快照继续服务，节流记 Error；
/// - 校验规则与 cameras.json 一致；非法行（编号 / 门店 / 哈希格式不对、编号或哈希重复）只跳过该行并节流记 Error，
///   不让整表失效。哈希重复时涉及的行全部跳过：无法判断哪台才是真的，宁可都 401 也不能认错摄像机。
/// </summary>
internal sealed class DatabaseCameraSource : ICameraRegistrySource
{
    public const string Name = "database";

    private static readonly TimeSpan FailureLogWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InvalidRowLogWindow = TimeSpan.FromMinutes(15);

    private readonly IFaceCameraRepository _repository;
    private readonly ILogger<DatabaseCameraSource> _logger;
    private readonly ThrottledLogGate _failureLogGate;
    private readonly ThrottledLogGate _invalidRowLogGate;
    private CameraRegistry? _current;
    private string? _lastSignature;
    private long _consecutiveFailures;

    public DatabaseCameraSource(IFaceCameraRepository repository, TimeProvider timeProvider, ILogger<DatabaseCameraSource> logger)
    {
        _repository = repository;
        _logger = logger;
        _failureLogGate = new ThrottledLogGate(timeProvider, FailureLogWindow);
        _invalidRowLogGate = new ThrottledLogGate(timeProvider, InvalidRowLogWindow);
    }

    public string SourceName => Name;

    public CameraRegistry? Current => Volatile.Read(ref _current);

    /// <summary>
    /// 刷新一次。成功返回 true 并整份替换快照；失败返回 false、保留旧快照。不向外抛异常（取消除外）。
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FaceCameraRow> rows;
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
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            if (_failureLogGate.TryAcquire("refresh-failed", out var suppressed))
            {
                // 中文注释：SqlClient 的异常信息不含密码；连接串本身绝不进日志。
                _logger.LogError(
                    ex,
                    "Face ingest camera registry refresh from database failed; {State}. consecutiveFailures={ConsecutiveFailures} suppressedSinceLastLog={SuppressedCount}",
                    Current is null ? "source not ready yet, ingest endpoint answers 503" : "keeping previous snapshot",
                    failures,
                    suppressed);
            }

            return false;
        }

        var cameras = BuildRegistry(rows, out var problems);
        foreach (var problem in problems)
        {
            if (_invalidRowLogGate.TryAcquire(problem, out var suppressed))
            {
                _logger.LogError(
                    "Face ingest FaceCamera row skipped: {Problem} suppressedSinceLastLog={SuppressedCount}",
                    problem,
                    suppressed);
            }
        }

        var registry = new CameraRegistry(cameras);
        var wasReady = Current is not null;
        Volatile.Write(ref _current, registry);
        Interlocked.Exchange(ref _consecutiveFailures, 0);

        // 中文注释：30 秒一刷，内容没变就不刷屏；首次就绪或清单变化时记一条 Information。
        var signature = ComputeSignature(cameras);
        if (!wasReady || !string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            _lastSignature = signature;
            _logger.LogInformation(
                "Face ingest camera registry loaded source=database trigger={Trigger} cameras={CameraCount} enabledCameras={EnabledCameraCount} skippedRows={SkippedRowCount}",
                wasReady ? "change" : "first-load",
                cameras.Count,
                cameras.Count(static camera => camera.Enabled),
                rows.Count - cameras.Count);
        }

        return true;
    }

    /// <summary>按 P0 规则校验每一行，返回合法摄像机与问题描述（问题描述不含 token 哈希）。</summary>
    internal static List<CameraRegistration> BuildRegistry(IReadOnlyList<FaceCameraRow> rows, out List<string> problems)
    {
        problems = [];
        var candidates = new List<(int Index, string CameraId, string StoreCode, string TokenHash, bool Enabled)>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var cameraId = row.CameraId ?? string.Empty;
            var label = FaceIngestSettings.IsValidCameraId(cameraId)
                ? $"camera '{cameraId}'"
                : string.Create(CultureInfo.InvariantCulture, $"row #{index + 1}");
            if (!FaceIngestSettings.IsValidCameraId(cameraId))
            {
                problems.Add($"{label}: CameraId must match ^[a-z0-9][a-z0-9-]{{0,63}}$.");
                continue;
            }

            var storeCode = row.StoreCode?.Trim() ?? string.Empty;
            if (!FaceIngestSettings.IsValidStoreCode(storeCode))
            {
                problems.Add($"{label}: StoreCode must be 1-32 characters of [A-Za-z0-9_-].");
                continue;
            }

            var tokenHash = row.TokenSha256?.Trim() ?? string.Empty;
            if (!FaceIngestSettings.IsValidTokenHash(tokenHash))
            {
                problems.Add($"{label}: TokenSha256 must be 64 hexadecimal characters.");
                continue;
            }

            candidates.Add((index, cameraId, storeCode, tokenHash.ToLowerInvariant(), row.Enabled));
        }

        var duplicateIds = candidates.GroupBy(static c => c.CameraId, StringComparer.Ordinal)
            .Where(static g => g.Count() > 1)
            .Select(static g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        var duplicateHashes = candidates.GroupBy(static c => c.TokenHash, StringComparer.Ordinal)
            .Where(static g => g.Count() > 1)
            .Select(static g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var cameras = new List<CameraRegistration>();
        foreach (var candidate in candidates)
        {
            if (duplicateIds.Contains(candidate.CameraId))
            {
                problems.Add($"camera '{candidate.CameraId}': CameraId is duplicated.");
                continue;
            }

            if (duplicateHashes.Contains(candidate.TokenHash))
            {
                problems.Add($"camera '{candidate.CameraId}': TokenSha256 duplicates another camera.");
                continue;
            }

            cameras.Add(new CameraRegistration(
                candidate.CameraId,
                candidate.StoreCode,
                candidate.Enabled,
                Convert.FromHexString(candidate.TokenHash)));
        }

        return cameras;
    }

    private static string ComputeSignature(IReadOnlyList<CameraRegistration> cameras)
    {
        // 中文注释：只用于判断「清单是否变化」，取哈希后不保留明细（哈希字节本身也不进日志）。
        var builder = new StringBuilder();
        foreach (var camera in cameras.OrderBy(static camera => camera.CameraId, StringComparer.Ordinal))
        {
            builder.Append(camera.CameraId).Append('|').Append(camera.StoreCode).Append('|')
                .Append(camera.Enabled ? '1' : '0').Append('|').Append(Convert.ToHexString(camera.TokenSha256)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

/// <summary>
/// 数据库摄像机来源的后台刷新：启动后立即加载一次，之后每 CameraRefreshSeconds 秒刷新。
/// 首次加载失败时按较短间隔（不超过 5 秒）重试，尽快结束 503 状态。整轮异常不让服务崩溃。
/// json 来源（没配数据库）时什么都不做、立即结束。
/// </summary>
internal sealed class CameraRegistryRefreshService : BackgroundService
{
    private static readonly TimeSpan NotReadyRetryInterval = TimeSpan.FromSeconds(5);

    private readonly DatabaseCameraSource? _source;
    private readonly FaceIngestSettingsMonitor _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CameraRegistryRefreshService> _logger;

    public CameraRegistryRefreshService(
        ICameraRegistrySource source,
        FaceIngestSettingsMonitor settings,
        TimeProvider timeProvider,
        ILogger<CameraRegistryRefreshService> logger)
    {
        _source = source as DatabaseCameraSource;
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_source is null)
        {
            return;
        }

        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _source.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Face ingest camera registry refresh round failed unexpectedly; will retry.");
            }

            var interval = TimeSpan.FromSeconds(Math.Max(1, _settings.Current.CameraRefreshSeconds));
            if (_source.Current is null && interval > NotReadyRetryInterval)
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
