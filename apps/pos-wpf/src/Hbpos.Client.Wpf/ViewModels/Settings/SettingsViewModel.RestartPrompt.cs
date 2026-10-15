using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>设置页「日结后提醒重启」开关：设备级、立即保存、默认开启（存本机 AppSettings）。</summary>
public sealed partial class SettingsViewModel
{
    private readonly ISystemRestartSettingsStore? _systemRestartSettingsStore;
    private bool _isLoadingRestartPrompt;

    [ObservableProperty]
    private bool _isRestartPromptEnabled = true;

    /// <summary>没有存储（例如测试里不注入）时整张卡片隐藏。</summary>
    public bool IsRestartPromptSettingVisible => _systemRestartSettingsStore is not null;

    private async Task LoadRestartPromptAsync()
    {
        if (_systemRestartSettingsStore is null)
        {
            return;
        }

        // 加载期间赋值不能触发保存，否则每次进设置页都会回写一遍。
        _isLoadingRestartPrompt = true;
        try
        {
            IsRestartPromptEnabled = await _systemRestartSettingsStore.IsPromptEnabledAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 读不到就保持默认（开启），不影响设置页其它内容。
            ConsoleLog.WriteWarning("RestartAfterClose", $"load restart prompt setting failed error={ex.GetType().Name}");
        }
        finally
        {
            _isLoadingRestartPrompt = false;
        }
    }

    partial void OnIsRestartPromptEnabledChanged(bool value)
    {
        if (_isLoadingRestartPrompt || _systemRestartSettingsStore is null)
        {
            return;
        }

        _ = SaveRestartPromptAsync(value);
    }

    private async Task SaveRestartPromptAsync(bool enabled)
    {
        try
        {
            await _systemRestartSettingsStore!.SetPromptEnabledAsync(enabled);
            ConsoleLog.Write("RestartAfterClose", $"restart prompt setting changed enabled={enabled}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteWarning("RestartAfterClose", $"save restart prompt setting failed error={ex.GetType().Name}");
        }
    }
}
