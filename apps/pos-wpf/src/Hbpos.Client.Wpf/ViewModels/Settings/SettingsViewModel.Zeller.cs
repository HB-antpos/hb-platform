using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.Zeller;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>
/// 设置页的 Zeller 分页：检查配对、配对刷卡机、启用 Zeller。
/// </summary>
/// <remarks>
/// 中文注释：Zeller 没有要填的输入框，也没有「已修改未保存」的草稿——配对由 SDK 自己的窗口完成并保存在本机，
/// 我们只存「启用了 Zeller」这一个设置。所以这里不接自动保存，也没有未保存红点；
/// 启用时只改处理器，Square / Linkly 的设置原样保留。
/// 启用的前提是本次打开设置页后检查或配对成功过，和 Linkly 必须先测试连接再启用一样。
/// </remarks>
public sealed partial class SettingsViewModel
{
    private readonly ZellerSettingsCoordinator? _zellerCoordinator;
    private ZellerPairingCheck _zellerPairing = ZellerPairingCheck.NotChecked;
    private CancellationTokenSource? _zellerPairingCancellation;

    [ObservableProperty]
    private bool _isZellerPairingInProgress;

    [ObservableProperty]
    private bool _isZellerChecking;

    public IAsyncRelayCommand CheckZellerCommand { get; private set; } = null!;

    public IAsyncRelayCommand PairZellerCommand { get; private set; } = null!;

    public IAsyncRelayCommand EnableZellerCommand { get; private set; } = null!;

    public IRelayCommand CancelZellerPairingCommand { get; private set; } = null!;

    private void InitializeZellerCommands()
    {
        CheckZellerCommand = new AsyncRelayCommand(CheckZellerAsync, CanCheckZeller);
        PairZellerCommand = new AsyncRelayCommand(PairZellerAsync, CanPairZeller);
        EnableZellerCommand = new AsyncRelayCommand(EnableZellerAsync, CanEnableZeller);
        CancelZellerPairingCommand = new RelayCommand(CancelZellerPairing, () => IsZellerPairingInProgress);
    }

    public string ZellerTitleText => T("settings.zeller.title");

    /// <summary>没有注册 Zeller（例如测试里不注入）时，页签里的按钮都不可用。</summary>
    public bool IsZellerAvailable => _zellerCoordinator is not null;

    /// <summary>这台收银机当前启用的就是 Zeller。</summary>
    public bool IsZellerActive => _loadedConfiguration.Processor == CardProcessorKind.Zeller;

    public bool IsZellerPaired => _zellerPairing.IsPaired;

    public string ZellerStatusText
    {
        get
        {
            if (IsZellerPairingInProgress)
            {
                return T("settings.zeller.status.pairing");
            }

            if (IsZellerChecking)
            {
                return T("settings.zeller.status.checking");
            }

            return _zellerPairing.State switch
            {
                ZellerPairingState.Paired => T("settings.zeller.status.paired"),
                ZellerPairingState.NotPaired => T("settings.zeller.status.notPaired"),
                ZellerPairingState.Cancelled => T("settings.zeller.status.cancelled"),
                ZellerPairingState.Failed => string.IsNullOrWhiteSpace(_zellerPairing.Detail)
                    ? T("settings.zeller.status.failed")
                    : Format("settings.zeller.status.failedWithReason", _zellerPairing.Detail.ReplaceLineEndings(" ").Trim()),
                _ => T("settings.zeller.status.notChecked")
            };
        }
    }

    public string ZellerActiveText => IsZellerActive
        ? T("settings.zeller.active.yes")
        : T("settings.zeller.active.no");

    private bool CanCheckZeller() => IsZellerAvailable && !IsBusy && !IsZellerPairingInProgress && !IsZellerChecking;

    private bool CanPairZeller() => CanCheckZeller();

    private bool CanEnableZeller() => CanCheckZeller() && IsZellerPaired && !IsZellerActive;

    private async Task CheckZellerAsync()
    {
        if (_zellerCoordinator is null)
        {
            return;
        }

        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.PaymentTerminal, "check-zeller");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        // 检查和配对期间锁住整页（忙碌状态），店长不会同时点别的保存。
        await RunBusyAsync(async () =>
        {
            IsZellerChecking = true;
            try
            {
                _zellerPairing = await _zellerCoordinator.CheckAsync();
                SetStatus(_zellerPairing.IsPaired ? "settings.zeller.status.paired" : "settings.zeller.status.checkDone");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _zellerPairing = new ZellerPairingCheck(ZellerPairingState.Failed, Detail: ex.Message);
                ConsoleLog.WriteWarning("ZellerSettings", $"settings ui check zeller failed message={LogValue(ex.Message)}", exception: ex);
            }
            finally
            {
                IsZellerChecking = false;
            }
        }, "check zeller settings");
        RaiseZellerProperties();
    }

    private async Task PairZellerAsync()
    {
        if (_zellerCoordinator is null)
        {
            return;
        }

        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.PaymentTerminal, "pair-zeller");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await RunBusyAsync(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            _zellerPairingCancellation = cancellation;
            IsZellerPairingInProgress = true;
            try
            {
                _zellerPairing = await _zellerCoordinator.PairAsync(cancellation.Token);
                SetStatus(_zellerPairing.IsPaired ? "settings.zeller.status.paired" : "settings.zeller.status.checkDone");
            }
            catch (OperationCanceledException)
            {
                _zellerPairing = new ZellerPairingCheck(ZellerPairingState.Cancelled);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _zellerPairing = new ZellerPairingCheck(ZellerPairingState.Failed, Detail: ex.Message);
                ConsoleLog.WriteWarning("ZellerSettings", $"settings ui pair zeller failed message={LogValue(ex.Message)}", exception: ex);
            }
            finally
            {
                _zellerPairingCancellation = null;
                IsZellerPairingInProgress = false;
            }
        }, "pair zeller settings");
        RaiseZellerProperties();
    }

    private void CancelZellerPairing() => _zellerPairingCancellation?.Cancel();

    private async Task EnableZellerAsync()
    {
        if (_zellerCoordinator is null)
        {
            return;
        }

        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.PaymentTerminal, "enable-zeller");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await RunBusyAsync(async () =>
        {
            var saved = await _zellerCoordinator.EnableAsync(_loadedConfiguration, _zellerPairing);
            // 和 Square / Linkly 保存成功后一样：三处引用同一份新配置，再按「刚保存成功」刷新基线和界面。
            _squareState.LoadedConfiguration = saved;
            _linklyState.LoadedConfiguration = saved;
            _loadedConfiguration = saved;
            OnPaymentTerminalStateSynced();
            RaiseActivePaymentProviderProperties();
            RaiseCommandStates();
            SetStatus("settings.status.zellerEnabled");
        }, "enable zeller settings");
        RaiseZellerProperties();
    }

    /// <summary>刷新 Zeller 页签上随状态变化的属性和按钮。</summary>
    private void RaiseZellerProperties()
    {
        OnPropertyChanged(nameof(IsZellerAvailable));
        OnPropertyChanged(nameof(IsZellerActive));
        OnPropertyChanged(nameof(IsZellerPaired));
        OnPropertyChanged(nameof(ZellerStatusText));
        OnPropertyChanged(nameof(ZellerActiveText));
        RaiseZellerCommandStates();
    }

    private void RaiseZellerCommandStates()
    {
        CheckZellerCommand?.NotifyCanExecuteChanged();
        PairZellerCommand?.NotifyCanExecuteChanged();
        EnableZellerCommand?.NotifyCanExecuteChanged();
        CancelZellerPairingCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>服务商 TabControl 的下标约定：0 = Square，1 = Linkly，2 = Zeller。没启用任何一家时回到 0。</summary>
    internal static int ProviderTabIndexFor(CardProcessorKind processor) => processor switch
    {
        CardProcessorKind.Linkly => 1,
        CardProcessorKind.Zeller => 2,
        _ => 0
    };

    internal static SettingsPaymentSubTab SubTabForProviderIndex(int index) => index switch
    {
        1 => SettingsPaymentSubTab.Linkly,
        2 => SettingsPaymentSubTab.Zeller,
        _ => SettingsPaymentSubTab.Square
    };

    partial void OnIsZellerPairingInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(ZellerStatusText));
        RaiseZellerCommandStates();
    }

    partial void OnIsZellerCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(ZellerStatusText));
        RaiseZellerCommandStates();
    }
}
