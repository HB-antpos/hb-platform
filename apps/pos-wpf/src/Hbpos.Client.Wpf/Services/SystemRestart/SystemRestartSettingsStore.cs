using System.Globalization;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 「日结后凌晨自动重启」的设备级本地状态：开关（默认开）、「最近一次排定重启的营业日」（每营业日最多排一次），
/// 已排定的重启时间（POS 中途重开也仍然有效），以及「当晚凌晨重启已处理」的日期（开机时长兜底每晚最多一次）。
/// 存在本机 SQLite 的 AppSettings 键值表，不涉及表结构变更。
/// </summary>
public interface ISystemRestartSettingsStore
{
    Task<bool> IsPromptEnabledAsync(CancellationToken cancellationToken = default);

    Task SetPromptEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    Task<DateOnly?> GetLastPromptBusinessDateAsync(CancellationToken cancellationToken = default);

    Task MarkPromptedAsync(DateOnly businessDate, CancellationToken cancellationToken = default);

    /// <summary>已排定的凌晨重启时间；没有排定或值无法解析时返回 null。</summary>
    Task<DateTimeOffset?> GetScheduledRestartAtAsync(CancellationToken cancellationToken = default);

    Task SetScheduledRestartAtAsync(DateTimeOffset restartAt, CancellationToken cancellationToken = default);

    Task ClearScheduledRestartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 最近一次「当晚的凌晨重启已经处理完」（倒计时被取消，或已下发重启命令）的本地日历日期。
    /// 用来防止开机时长兜底在同一个凌晨窗口里反复重新排定、反复弹倒计时。
    /// </summary>
    Task<DateOnly?> GetLastOvernightHandledDateAsync(CancellationToken cancellationToken = default);

    Task MarkOvernightHandledAsync(DateOnly localDate, CancellationToken cancellationToken = default);
}

public sealed class SystemRestartSettingsStore(ILocalAppSettingsRepository settings) : ISystemRestartSettingsStore
{
    internal const string EnabledKey = "Maintenance:RestartAfterDailyClosePrompt";
    internal const string LastPromptBusinessDateKey = "Maintenance:RestartPrompt:LastPromptBusinessDate";
    internal const string ScheduledRestartAtKey = "Maintenance:RestartPrompt:ScheduledRestartAt";
    internal const string LastOvernightHandledDateKey = "Maintenance:RestartPrompt:LastOvernightHandledDate";

    public async Task<bool> IsPromptEnabledAsync(CancellationToken cancellationToken = default)
    {
        var raw = await settings.GetValueAsync(EnabledKey, cancellationToken);
        // 默认开启：从未保存过、或值无法解析时按「开」处理；只有显式存成 false 才关闭。
        return !bool.TryParse(raw, out var enabled) || enabled;
    }

    public Task SetPromptEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        return settings.SetValueAsync(EnabledKey, enabled ? "true" : "false", cancellationToken);
    }

    public async Task<DateOnly?> GetLastPromptBusinessDateAsync(CancellationToken cancellationToken = default)
    {
        var raw = await settings.GetValueAsync(LastPromptBusinessDateKey, cancellationToken);
        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    public Task MarkPromptedAsync(DateOnly businessDate, CancellationToken cancellationToken = default)
    {
        return settings.SetValueAsync(
            LastPromptBusinessDateKey,
            businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            cancellationToken);
    }

    public async Task<DateTimeOffset?> GetScheduledRestartAtAsync(CancellationToken cancellationToken = default)
    {
        var raw = await settings.GetValueAsync(ScheduledRestartAtKey, cancellationToken);
        return DateTimeOffset.TryParseExact(raw, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var restartAt)
            ? restartAt
            : null;
    }

    public Task SetScheduledRestartAtAsync(DateTimeOffset restartAt, CancellationToken cancellationToken = default)
    {
        // 往返格式带时区偏移，跨夏令时切换也能还原成同一个时刻。
        return settings.SetValueAsync(
            ScheduledRestartAtKey,
            restartAt.ToString("O", CultureInfo.InvariantCulture),
            cancellationToken);
    }

    public Task ClearScheduledRestartAsync(CancellationToken cancellationToken = default)
    {
        return settings.DeleteValueAsync(ScheduledRestartAtKey, cancellationToken);
    }

    public async Task<DateOnly?> GetLastOvernightHandledDateAsync(CancellationToken cancellationToken = default)
    {
        var raw = await settings.GetValueAsync(LastOvernightHandledDateKey, cancellationToken);
        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    public Task MarkOvernightHandledAsync(DateOnly localDate, CancellationToken cancellationToken = default)
    {
        return settings.SetValueAsync(
            LastOvernightHandledDateKey,
            localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            cancellationToken);
    }
}
