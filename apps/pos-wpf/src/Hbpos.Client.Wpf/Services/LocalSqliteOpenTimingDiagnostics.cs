using System.Globalization;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 本地库「打开连接」耗时的只读采样，用来在真机上量化
/// 「关连接池 + WAL，每次打开/关闭都付出文件打开与 -wal/-shm 重建代价」这一怀疑，再决定要不要改成连接池。
/// 只统计与记日志，不改变任何连接行为；日志频率受限，不会刷屏。
/// </summary>
internal sealed class LocalSqliteOpenTimingDiagnostics
{
    // 每累计这么多次打开，或距上次摘要超过窗口，就输出一条摘要。
    private const int SummaryEveryOpens = 50;
    private static readonly TimeSpan SummaryWindow = TimeSpan.FromSeconds(60);
    // 单次打开（含 PRAGMA）超过该阈值记一条慢日志，且慢日志最多每 10 秒一条。
    private static readonly TimeSpan SlowThreshold = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SlowLogInterval = TimeSpan.FromSeconds(10);

    internal static LocalSqliteOpenTimingDiagnostics Shared { get; } = new(
        static (message, isWarning) =>
        {
            if (isWarning)
            {
                ConsoleLog.WriteWarning("LocalDb", message);
            }
            else
            {
                ConsoleLog.Write("LocalDb", message);
            }
        },
        TimeProvider.System);

    private readonly object _gate = new();
    private readonly Action<string, bool> _emit;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _windowStartedAt;
    private DateTimeOffset _lastSlowLogAt = DateTimeOffset.MinValue;
    private int _count;
    private int _slowCount;
    private double _openTotalMs;
    private double _openMaxMs;
    private double _configureTotalMs;
    private double _configureMaxMs;

    internal LocalSqliteOpenTimingDiagnostics(Action<string, bool> emit, TimeProvider timeProvider)
    {
        _emit = emit;
        _timeProvider = timeProvider;
        _windowStartedAt = timeProvider.GetUtcNow();
    }

    /// <summary>
    /// 记录一次成功打开：<paramref name="open"/> 为 OpenAsync，<paramref name="configure"/> 为 busy_timeout / journal_mode 两条 PRAGMA。
    /// 任何异常都在内部吞掉，诊断不能影响本地读写。
    /// </summary>
    internal void Record(TimeSpan open, TimeSpan configure)
    {
        try
        {
            string? summary = null;
            string? slow = null;
            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow();
                var openMs = open.TotalMilliseconds;
                var configureMs = configure.TotalMilliseconds;
                _count++;
                _openTotalMs += openMs;
                _configureTotalMs += configureMs;
                _openMaxMs = Math.Max(_openMaxMs, openMs);
                _configureMaxMs = Math.Max(_configureMaxMs, configureMs);

                if (open + configure >= SlowThreshold)
                {
                    _slowCount++;
                    if (now - _lastSlowLogAt >= SlowLogInterval)
                    {
                        _lastSlowLogAt = now;
                        slow = string.Create(
                            CultureInfo.InvariantCulture,
                            $"local sqlite open slow openMs={openMs:F1} configureMs={configureMs:F1}");
                    }
                }

                if (_count >= SummaryEveryOpens || now - _windowStartedAt >= SummaryWindow)
                {
                    summary = string.Create(
                        CultureInfo.InvariantCulture,
                        $"local sqlite open summary count={_count} windowSec={(now - _windowStartedAt).TotalSeconds:F0} " +
                        $"openAvgMs={_openTotalMs / _count:F2} openMaxMs={_openMaxMs:F1} " +
                        $"configureAvgMs={_configureTotalMs / _count:F2} configureMaxMs={_configureMaxMs:F1} " +
                        $"slowOver50ms={_slowCount}");
                    _windowStartedAt = now;
                    _count = 0;
                    _slowCount = 0;
                    _openTotalMs = 0;
                    _openMaxMs = 0;
                    _configureTotalMs = 0;
                    _configureMaxMs = 0;
                }
            }

            // 写日志放在锁外，避免日志管道反过来拖慢后续打开连接。
            if (slow is not null)
            {
                _emit(slow, true);
            }

            if (summary is not null)
            {
                _emit(summary, false);
            }
        }
        catch
        {
            // 诊断只读：任何失败都不能向调用方冒泡。
        }
    }
}
