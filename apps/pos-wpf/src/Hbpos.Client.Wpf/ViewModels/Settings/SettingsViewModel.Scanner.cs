using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>
/// 设置页「扫码枪灵敏度」：Raw Input 通道同一条码在这段时间内的第二次读码按重复丢弃。
/// 设备级、改了直接保存（存本机 AppSettings），范围 100～1000ms、步进 100、默认 600；滑块与加减按钮绑定同一个值。
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>滑块拖动时会连续改值，停顿这么久再落库，避免每个中间档位都写一次。</summary>
    private static readonly TimeSpan ScannerAutoSaveDelay = TimeSpan.FromMilliseconds(300);

    private readonly IScannerSettingsStore? _scannerSettingsStore;
    private bool _isLoadingScannerSettings;

    [ObservableProperty]
    private int _scannerRepeatWindowMs = ScannerSettingsStore.DefaultRepeatWindowMs;

    /// <summary>没有存储（例如测试里不注入）时整张卡片隐藏。</summary>
    public bool IsScannerSettingVisible => _scannerSettingsStore is not null;

    public int ScannerRepeatWindowMinMs => ScannerSettingsStore.MinRepeatWindowMs;

    public int ScannerRepeatWindowMaxMs => ScannerSettingsStore.MaxRepeatWindowMs;

    public int ScannerRepeatWindowStepMs => ScannerSettingsStore.RepeatWindowStepMs;

    public string ScannerRepeatWindowText => Format("settings.scanner.valueFormat", ScannerRepeatWindowMs);

    public IRelayCommand DecreaseScannerRepeatWindowCommand { get; private set; } = null!;

    public IRelayCommand IncreaseScannerRepeatWindowCommand { get; private set; } = null!;

    private void InitializeScannerCommands()
    {
        DecreaseScannerRepeatWindowCommand = new RelayCommand(
            () => ScannerRepeatWindowMs = StepScannerRepeatWindow(-ScannerRepeatWindowStepMs),
            () => ScannerRepeatWindowMs > ScannerRepeatWindowMinMs);
        IncreaseScannerRepeatWindowCommand = new RelayCommand(
            () => ScannerRepeatWindowMs = StepScannerRepeatWindow(ScannerRepeatWindowStepMs),
            () => ScannerRepeatWindowMs < ScannerRepeatWindowMaxMs);
    }

    private int StepScannerRepeatWindow(int delta) => ScannerSettingsStore.Clamp(ScannerRepeatWindowMs + delta);

    private async Task LoadScannerSettingsAsync()
    {
        if (_scannerSettingsStore is null)
        {
            return;
        }

        // 加载期间赋值不能触发保存，否则每次进设置页都会回写一遍。
        _isLoadingScannerSettings = true;
        try
        {
            await _scannerSettingsStore.LoadAsync();
            ScannerRepeatWindowMs = _scannerSettingsStore.RepeatWindowMs;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 读不到就保持默认，不影响设置页其它内容。
            ConsoleLog.WriteWarning("RawScanner", $"load scanner settings page failed error={ex.GetType().Name}");
        }
        finally
        {
            _isLoadingScannerSettings = false;
        }
    }

    partial void OnScannerRepeatWindowMsChanged(int value)
    {
        // 滑块绑定的是 double，落到 int 属性上可能带来越界 / 非档位值；统一夹回范围并对齐到步进档位。
        var snapped = SnapScannerRepeatWindow(value);
        if (snapped != value)
        {
            ScannerRepeatWindowMs = snapped;
            return;
        }

        OnPropertyChanged(nameof(ScannerRepeatWindowText));
        DecreaseScannerRepeatWindowCommand?.NotifyCanExecuteChanged();
        IncreaseScannerRepeatWindowCommand?.NotifyCanExecuteChanged();

        if (_isLoadingScannerSettings || _scannerSettingsStore is null)
        {
            return;
        }

        // 自动保存开启时交给保存跟踪器：失败会显示在卡片上，离开设置页时提示。
        if (_autoSaveEnabled)
        {
            MarkAutoSaveChanged(ScannerSaveKey, ScannerAutoSaveDelay);
            return;
        }

        _ = SaveScannerSettingsAsync(value);
    }

    private static int SnapScannerRepeatWindow(int value)
    {
        var clamped = ScannerSettingsStore.Clamp(value);
        var steps = (int)Math.Round(
            (clamped - ScannerSettingsStore.MinRepeatWindowMs) / (double)ScannerSettingsStore.RepeatWindowStepMs);
        return ScannerSettingsStore.Clamp(
            ScannerSettingsStore.MinRepeatWindowMs + steps * ScannerSettingsStore.RepeatWindowStepMs);
    }

    private async Task SaveScannerSettingsAsync(int milliseconds)
    {
        try
        {
            await _scannerSettingsStore!.SetRepeatWindowMsAsync(milliseconds);
            ConsoleLog.Write("RawScanner", $"scanner repeat window changed ms={milliseconds}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteWarning("RawScanner", $"save scanner settings failed error={ex.GetType().Name}");
        }
    }

    private async Task<SettingsSaveResult> SaveScannerForAutoSaveAsync(CancellationToken cancellationToken)
    {
        if (_scannerSettingsStore is null)
        {
            return SettingsSaveResult.Success;
        }

        var milliseconds = ScannerRepeatWindowMs;
        try
        {
            await _scannerSettingsStore.SetRepeatWindowMsAsync(milliseconds, cancellationToken);
            ConsoleLog.Write("RawScanner", $"scanner repeat window changed ms={milliseconds}");
            return SettingsSaveResult.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteWarning("RawScanner", $"save scanner settings failed error={ex.GetType().Name}");
            return SettingsSaveResult.Failure(ex.Message);
        }
    }
}
