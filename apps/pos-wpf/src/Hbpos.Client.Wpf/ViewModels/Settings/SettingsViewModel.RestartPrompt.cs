using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>设置页「日结后提醒重启」开关：设备级、立即保存、默认开启（存本机 AppSettings）。</summary>
public sealed partial class SettingsViewModel
{
    private readonly ISystemRestartSettingsStore? _systemRestartSettingsStore;
    private readonly Func<IPostDailyCloseRestartCoordinator?>? _restartCoordinatorProvider;
    private bool _isLoadingRestartPrompt;

    [ObservableProperty]
    private bool _isRestartPromptEnabled = true;

    /// <summary>没有存储（例如测试里不注入）时整张卡片隐藏。</summary>
    public bool IsRestartPromptSettingVisible => _systemRestartSettingsStore is not null;

    /// <summary>「重启电脑」测试按钮：没有协调器（例如测试里不注入）时隐藏。</summary>
    public bool IsRestartComputerTestVisible => _restartCoordinatorProvider is not null;

    /// <summary>
    /// 设置页「重启电脑」测试：走与日结后提示相同的安全检查与可取消倒计时，通过后真的重启本机。
    /// 需要设备管理权限（没有权限时由授权流程要求主管扫码），避免收银员随手重启。
    /// </summary>
    public IAsyncRelayCommand RestartComputerCommand { get; }

    private bool CanRestartComputer() => !IsBusy && _restartCoordinatorProvider is not null;

    private async Task RestartComputerAsync(CancellationToken cancellationToken)
    {
        var coordinator = _restartCoordinatorProvider?.Invoke();
        if (coordinator is null)
        {
            return;
        }

        using var permissionGrant = await AuthorizeAsync(
            Permissions.PosTerminal.Settings.DeviceRegistration, "restart-computer");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        // 流程自己用浮层展示进度与结果；成功时应用随后退出，这里不需要再设状态文案。
        await coordinator.RunTestAsync(Session, cancellationToken);
    }

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
