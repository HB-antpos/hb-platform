using System.Collections.Concurrent;
using Hbpos.Api.Data;
using SqlSugar;

namespace Hbpos.Api.Services;

/// <summary>
/// 收银端访问 POS API 的三条入口代号，以及请求头 / 库值的白名单归一化。
/// edge＝EdgeOne（pos.hotbargain.vip）、aws＝AWS 悉尼边缘（pos-syd.hotbargain.vip）、direct＝直连新加坡（hotbargain.vip）。
/// </summary>
public static class PosApiEntryModes
{
    public const string Edge = "edge";
    public const string Aws = "aws";
    public const string Direct = "direct";

    /// <summary>
    /// 源站 nginx 按实际到达入口计算后传给 POS API 的请求头（nginx 会覆盖客户端自带的同名头）。
    /// 只用于后台展示，不参与任何鉴权。
    /// </summary>
    public const string HeaderName = "X-HB-Entry";

    /// <summary>
    /// 白名单归一化：去首尾空白、转小写后必须恰好是 edge / aws / direct，否则返回 null。
    /// 库里的脏值、请求头的乱码 / 多值（"edge,aws"）都落到 null，由调用方按「忽略 / 未设置」处理。
    /// </summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (string.Equals(trimmed, Edge, StringComparison.OrdinalIgnoreCase)) return Edge;
        if (string.Equals(trimmed, Aws, StringComparison.OrdinalIgnoreCase)) return Aws;
        if (string.Equals(trimmed, Direct, StringComparison.OrdinalIgnoreCase)) return Direct;
        return null;
    }

    /// <summary>读取请求里的 X-HB-Entry：只接受恰好一个头值且在白名单内，其余一律返回 null。</summary>
    public static string? ReadFromRequest(HttpRequest? request)
    {
        if (request is null || !request.Headers.TryGetValue(HeaderName, out var values) || values.Count != 1)
        {
            return null;
        }

        return Normalize(values[0]);
    }
}

// ───────────────────────── 下发：读分店首选入口 ─────────────────────────

/// <summary>读取 dbo.Store.PosApiEntryMode 现值（已按白名单归一化；NULL / 脏值 / 列不存在一律为 null）。</summary>
public interface IStoreApiEntryModeReader
{
    Task<string?> GetModeAsync(string storeCode, CancellationToken cancellationToken);
}

/// <summary>
/// 分店首选入口的进程内短缓存（单例）。receipt-profile/sync 每台设备每分钟一次，33 家店共用同一份值，
/// 缓存后同店同一时间窗内只查一次库。
/// 失效策略：纯 TTL（20 秒，必须 ≤ 30 秒），到期后下一次请求重新读库；不做主动失效——
/// 后台改设置由主 API 写库，POS API 是独立进程，无法通知；改设置后「客户端下次轮询（≤60 秒）+ 缓存（≤20 秒）」内生效。
/// 空值（未设置）同样缓存，避免未设置的店每分钟打库。
/// </summary>
public sealed class StoreApiEntryModeCache(TimeProvider timeProvider)
{
    internal static readonly TimeSpan Ttl = TimeSpan.FromSeconds(20);

    // 分店数量有限；这里只防御异常数据撑大字典。
    private const int MaxEntries = 4096;

    private readonly ConcurrentDictionary<string, (string? Mode, DateTimeOffset ExpiresAt)> entries =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string storeCode, out string? mode)
    {
        if (entries.TryGetValue(storeCode, out var entry) && timeProvider.GetUtcNow() < entry.ExpiresAt)
        {
            mode = entry.Mode;
            return true;
        }

        mode = null;
        return false;
    }

    public void Set(string storeCode, string? mode)
    {
        if (entries.Count >= MaxEntries)
        {
            entries.Clear();
        }

        entries[storeCode] = (mode, timeProvider.GetUtcNow() + Ttl);
    }
}

public sealed class SqlSugarStoreApiEntryModeReader(
    HbposSqlSugarContext dbContext,
    StoreApiEntryModeCache cache,
    ILogger<SqlSugarStoreApiEntryModeReader>? logger = null) : IStoreApiEntryModeReader
{
    // 与门店资料接口同口径：只认启用且未删除的分店。
    internal const string ReadModeSql = """
        SELECT TOP (1) [PosApiEntryMode]
        FROM [dbo].[Store]
        WHERE [StoreCode] = @StoreCode
          AND [IsActive] = 1
          AND ([IsDeleted] = 0 OR [IsDeleted] IS NULL)
        """;

    public async Task<string?> GetModeAsync(string storeCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return null;
        }

        var key = storeCode.Trim();
        if (cache.TryGet(key, out var cached))
        {
            return cached;
        }

        cancellationToken.ThrowIfCancellationRequested();
        string? mode;
        try
        {
            var raw = await dbContext.MainDb.Ado.SqlQuerySingleAsync<string?>(
                ReadModeSql,
                new SugarParameter("@StoreCode", key));
            // 库里是 NULL 或不在 edge / aws / direct 内的脏值一律当作「未设置」。
            mode = PosApiEntryModes.Normalize(raw);
        }
        catch (Exception exception) when (StoreReceiptProfileColumnFallback.IsInvalidColumnName(exception))
        {
            // 部署顺序出错（先发 Hbpos.Api、迁移尚未执行且启动补列失败）：按未设置处理，不能让三端轮询的同步接口 500。
            logger?.LogWarning(
                "Store PosApiEntryMode column is missing; treating the API entry as unset. Run the HBweb migration 20261024.003.");
            mode = null;
        }

        cache.Set(key, mode);
        return mode;
    }
}

// ───────────────────────── 回显：记录设备实际走的入口 ─────────────────────────

/// <summary>写设备表 LastApiEntry / LastApiEntryAt 的底层写入（便于单测替换）。</summary>
public interface IDeviceApiEntryWriter
{
    /// <summary>返回受影响行数；设备行不存在时为 0。</summary>
    Task<int> WriteAsync(
        string deviceCode,
        string storeCode,
        string? hardwareId,
        string entry,
        DateTime nowUtc,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarDeviceApiEntryWriter(HbposSqlSugarContext dbContext) : IDeviceApiEntryWriter
{
    public Task<int> WriteAsync(
        string deviceCode,
        string storeCode,
        string? hardwareId,
        string entry,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 与心跳上报同样的定位方式：硬件码（认证声明里有就带上）+ 设备编号 + 分店；生产表没有 (分店, 设备编号) 唯一约束，
        // 带硬件码能避开历史撞号。只写这两列，不动心跳 / 版本等其它运行态字段。
        const string sql = """
            UPDATE [POSM_设备注册信息表]
            SET [LastApiEntry] = @Entry,
                [LastApiEntryAt] = @NowUtc
            WHERE [系统设备编号] = @DeviceCode
              AND [分店代码] = @StoreCode
              AND (@HardwareId IS NULL OR [设备硬件识别码] = @HardwareId);
            """;

        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@Entry", entry),
            new SugarParameter("@NowUtc", nowUtc),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@HardwareId", string.IsNullOrWhiteSpace(hardwareId) ? null : hardwareId));
    }
}

/// <summary>
/// 每台设备最近一次写库的节流记录（单例，进程内）。
/// 规则：入口与上次不同 → 立即写；入口相同 → 距上次成功写入超过 10 分钟才再写；
/// 写失败时同入口 5 分钟内不重试（避免列还没补好时每分钟都打一次失败的 UPDATE）。
/// </summary>
public sealed class DeviceApiEntryThrottle(TimeProvider timeProvider)
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);

    // 设备数有限；这里只防御异常数据撑大字典。
    private const int MaxEntries = 20000;

    private readonly ConcurrentDictionary<string, (string Entry, DateTimeOffset NextWriteAt)> entries =
        new(StringComparer.Ordinal);

    public static string KeyOf(string storeCode, string deviceCode) => storeCode + "\u001f" + deviceCode;

    public bool ShouldWrite(string key, string entry)
    {
        return !entries.TryGetValue(key, out var last)
            || !string.Equals(last.Entry, entry, StringComparison.Ordinal)
            || timeProvider.GetUtcNow() >= last.NextWriteAt;
    }

    public void MarkWritten(string key, string entry) => Set(key, entry, RefreshInterval);

    public void MarkFailed(string key, string entry) => Set(key, entry, FailureBackoff);

    private void Set(string key, string entry, TimeSpan holdoff)
    {
        if (entries.Count >= MaxEntries)
        {
            entries.Clear();
        }

        entries[key] = (entry, timeProvider.GetUtcNow() + holdoff);
    }
}

public interface IDeviceApiEntryRecorder
{
    /// <summary>
    /// 尽力而为地记录设备实际入口；永不抛出（请求被取消除外），写库失败只记日志。
    /// <paramref name="headerValue"/> 已是白名单归一化后的值；null 表示头缺失 / 非法，直接忽略。
    /// </summary>
    Task RecordAsync(
        string? deviceCode,
        string? storeCode,
        string? hardwareId,
        string? headerValue,
        CancellationToken cancellationToken);
}

public sealed class DeviceApiEntryRecorder(
    IDeviceApiEntryWriter writer,
    DeviceApiEntryThrottle throttle,
    TimeProvider timeProvider,
    ILogger<DeviceApiEntryRecorder>? logger = null) : IDeviceApiEntryRecorder
{
    public async Task RecordAsync(
        string? deviceCode,
        string? storeCode,
        string? hardwareId,
        string? headerValue,
        CancellationToken cancellationToken)
    {
        // 头缺失、多值、不在白名单：忽略，不写库。
        var entry = PosApiEntryModes.Normalize(headerValue);
        var device = deviceCode?.Trim();
        var store = storeCode?.Trim();
        if (entry is null || string.IsNullOrEmpty(device) || string.IsNullOrEmpty(store))
        {
            return;
        }

        var key = DeviceApiEntryThrottle.KeyOf(store, device);
        if (!throttle.ShouldWrite(key, entry))
        {
            return;
        }

        try
        {
            await writer.WriteAsync(device, store, hardwareId?.Trim(), entry, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            throttle.MarkWritten(key, entry);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 请求已被客户端取消，响应本来就没人收，按取消处理。
            throw;
        }
        catch (Exception exception)
        {
            // 展示用的附带写入，失败绝不能影响 sync / runtime-status 的响应；节流退避后再试。
            throttle.MarkFailed(key, entry);
            logger?.LogWarning(exception, "Failed to record device API entry {Entry} for {DeviceCode}", entry, device);
        }
    }
}
