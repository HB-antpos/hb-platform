using System.Globalization;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 「日结后提示重启」的设备级本地状态：开关（默认开）与「最近一次提示的营业日」（每营业日最多提示一次）。
/// 存在本机 SQLite 的 AppSettings 键值表，不涉及表结构变更。
/// </summary>
public interface ISystemRestartSettingsStore
{
    Task<bool> IsPromptEnabledAsync(CancellationToken cancellationToken = default);

    Task SetPromptEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    Task<DateOnly?> GetLastPromptBusinessDateAsync(CancellationToken cancellationToken = default);

    Task MarkPromptedAsync(DateOnly businessDate, CancellationToken cancellationToken = default);
}

public sealed class SystemRestartSettingsStore(ILocalAppSettingsRepository settings) : ISystemRestartSettingsStore
{
    internal const string EnabledKey = "Maintenance:RestartAfterDailyClosePrompt";
    internal const string LastPromptBusinessDateKey = "Maintenance:RestartPrompt:LastPromptBusinessDate";

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
}
