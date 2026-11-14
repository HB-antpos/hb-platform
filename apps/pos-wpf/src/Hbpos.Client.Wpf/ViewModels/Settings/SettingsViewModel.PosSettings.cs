using System.Collections.ObjectModel;
using System.Windows.Threading;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>
/// 设置页「业务开关」分类：展示总部下发的 9 个业务开关（锁定 / 离线可改 / 离线临时修改），并让原来散在
/// 付款方式、小票打印机、系统维护、Zeller 页里的同名开关跟着锁定。
/// 业务值与锁定判断都在 <see cref="IPosSettingsService"/>；这里只做界面状态，
/// 并保证「同步把总部值写穿到本机存储」之后，旧页面的控件从存储重新读值，不会被页面里的旧草稿再覆盖回去。
/// <see cref="_posSettingsService"/> 为 null（测试 / 预览）时整个分类不出现，旧开关保持纯本机行为。
/// </summary>
public sealed partial class SettingsViewModel
{
    private static readonly IReadOnlyDictionary<string, string> PosSettingTextSuffixes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [PosSettingKeys.CashMovementEnabled] = "cashMovement",
        [PosSettingKeys.NoSaleRescan] = "noSaleRescan",
        [PosSettingKeys.ReturnsRescan] = "returnsRescan",
        [PosSettingKeys.BankReceipt] = "bankReceipt",
        [PosSettingKeys.ManualCard] = "manualCard",
        [PosSettingKeys.Voucher] = "voucher",
        [PosSettingKeys.OrderList] = "orderList",
        [PosSettingKeys.AutoRestart] = "autoRestart",
        [PosSettingKeys.ZellerUnmatchedRefund] = "zellerUnmatchedRefund"
    };

    private readonly Dictionary<string, PosSettingRowViewModel> _posSettingRowsByKey = new(StringComparer.Ordinal);

    /// <summary>「刷新 + 重读存储」串行执行，避免后台同步事件与用户操作交错回显。</summary>
    private readonly SemaphoreSlim _posSettingsReloadGate = new(1, 1);

    private Dispatcher? _posSettingsDispatcher;
    private PosSettingsView _posSettingsView = PosSettingsView.Empty;
    private Task _lastPosSettingToggleTask = Task.CompletedTask;

    [ObservableProperty]
    private bool _isPosSettingsSyncing;

    /// <summary>本机有总部设置服务时才显示「业务开关」分类。</summary>
    public bool IsPosSettingsAvailable => _posSettingsService is not null;

    public bool IsPosSettingsSelected => SelectedCategory == SettingsCategory.PosSettings;

    public IAsyncRelayCommand SelectPosSettingsCommand { get; private set; } = null!;

    public IAsyncRelayCommand SyncPosSettingsNowCommand { get; private set; } = null!;

    /// <summary>「业务开关」页的 9 行（顺序同 <c>PosSettingCatalog.All</c> 中 WPF 支持的项）。</summary>
    public ObservableCollection<PosSettingRowViewModel> PosSettingRows { get; } = [];

    // 旧页面里同名开关对应的行：始终非 null（没有服务时永远是「未锁定」），XAML 绑定不会产生绑定错误。
    public PosSettingRowViewModel ManualCardSettingRow => _posSettingRowsByKey[PosSettingKeys.ManualCard];

    public PosSettingRowViewModel VoucherSettingRow => _posSettingRowsByKey[PosSettingKeys.Voucher];

    public PosSettingRowViewModel OrderListSettingRow => _posSettingRowsByKey[PosSettingKeys.OrderList];

    public PosSettingRowViewModel BankReceiptSettingRow => _posSettingRowsByKey[PosSettingKeys.BankReceipt];

    public PosSettingRowViewModel NoSaleRescanSettingRow => _posSettingRowsByKey[PosSettingKeys.NoSaleRescan];

    public PosSettingRowViewModel ReturnsRescanSettingRow => _posSettingRowsByKey[PosSettingKeys.ReturnsRescan];

    public PosSettingRowViewModel AutoRestartSettingRow => _posSettingRowsByKey[PosSettingKeys.AutoRestart];

    public PosSettingRowViewModel ZellerUnmatchedRefundSettingRow => _posSettingRowsByKey[PosSettingKeys.ZellerUnmatchedRefund];

    public string PosSettingsTitleText => T("settings.category.posSettings");

    public string PosSettingsSyncButtonText => T(IsPosSettingsSyncing
        ? "settings.posSettings.syncing"
        : "settings.posSettings.syncNow");

    public string PosSettingsOfflineTitleText => T("settings.posSettings.offline.title");

    public string PosSettingsOfflineDetailText => T("settings.posSettings.offline.detail");

    /// <summary>离线中、且总部下发过值：页面顶部显示黄条。</summary>
    public bool IsPosSettingsOfflineBannerVisible => _posSettingsView.ShowsOfflineBanner;

    /// <summary>右上角「总部设置 · 最近同步 HH:mm」；从未同步显示「尚未同步」。</summary>
    public string PosSettingsLastSyncedText => _posSettingsView.LastSyncedAtUtc is { } syncedAt
        ? Format(
            "settings.posSettings.syncedAt",
            syncedAt.ToLocalTime().ToString("HH:mm", _localization?.CurrentCulture ?? System.Globalization.CultureInfo.CurrentCulture))
        : T("settings.posSettings.neverSynced");

    private void InitializePosSettings()
    {
        // 行始终按目录建好（与是否有服务无关），旧页面的锁定绑定因此永不为 null。
        foreach (var definition in PosSettingCatalog.All)
        {
            if (!PosSettingCatalog.IsSupportedBy(definition.Key, PosSettingClientKinds.Wpf) ||
                !PosSettingTextSuffixes.TryGetValue(definition.Key, out var suffix))
            {
                continue;
            }

            var row = new PosSettingRowViewModel(
                definition.Key,
                "settings.posSettings.item." + suffix,
                (key, args) => Format(key, args),
                SetPosSettingAsync);
            _posSettingRowsByKey[definition.Key] = row;
            PosSettingRows.Add(row);
        }

        SelectPosSettingsCommand = new AsyncRelayCommand(SelectPosSettingsAsync);
        SyncPosSettingsNowCommand = new AsyncRelayCommand(SyncPosSettingsNowAsync, () => !IsPosSettingsSyncing);

        if (_posSettingsService is null)
        {
            return;
        }

        // 总部设置变化事件来自线程池；只有 WPF 调度上下文才需要切回 UI 线程（写法同小票资料同步）。
        _posSettingsDispatcher = SynchronizationContext.Current is DispatcherSynchronizationContext
            ? Dispatcher.CurrentDispatcher
            : null;
        _posSettingsService.Changed += OnPosSettingsChanged;
        _posSettingsView = _posSettingsService.Current;
        ApplyPosSettingsView(_posSettingsView);
    }

    private void ReleasePosSettings()
    {
        if (_posSettingsService is not null)
        {
            _posSettingsService.Changed -= OnPosSettingsChanged;
        }
    }

    // ── 锁定判断（旧页面的保存与回写用）──

    /// <summary>该项当前是否被总部锁定（以服务的内存快照为准，没有服务一律为未锁定）。</summary>
    private bool IsPosSettingLocked(string key) =>
        _posSettingsService?.Current.Find(key) is { IsLocked: true };

    /// <summary>
    /// 旧页面保存时用：被锁定的项一律取服务里的值而不是界面上的草稿，
    /// 这样自动保存「整份写回」时不会把同步刚写穿的总部值覆盖回旧值。
    /// </summary>
    private bool EffectivePosSettingValue(string key, bool fieldValue) =>
        _posSettingsService?.Current.Find(key) is { IsLocked: true } locked ? locked.Value : fieldValue;

    // ── 页面操作 ──

    private async Task SelectPosSettingsAsync()
    {
        // 与「付款方式」同级权限：进入分类时授权一次（离开页面后 ResetCategoryAfterLoad 会要求重新授权）。
        await SelectCategoryAsync(SettingsCategory.PosSettings, Permissions.PosTerminal.Settings.PaymentTerminal);
        if (SelectedCategory == SettingsCategory.PosSettings)
        {
            await ReloadPosSettingsAsync(refreshService: true);
        }
    }

    private async Task SyncPosSettingsNowAsync()
    {
        if (_posSettingsService is null || IsPosSettingsSyncing)
        {
            return;
        }

        IsPosSettingsSyncing = true;
        try
        {
            var result = await _posSettingsService.SyncNowAsync();
            SetStatus(PosSettingsSyncStatusKey(result.Outcome));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 服务内部已把失败转成结果；这里兜底本机异常。
            ConsoleLog.WriteWarning("PosSettings", $"sync now failed error={ex.GetType().Name}");
            SetStatus(PosSettingsSyncStatusKey(PosSettingsSyncOutcome.Failed));
        }
        finally
        {
            IsPosSettingsSyncing = false;
        }

        // 同步可能刚把总部值写进各开关的存储：界面与旧控件一并重读。
        await ReloadPosSettingsAsync(refreshService: false);
    }

    private static string PosSettingsSyncStatusKey(PosSettingsSyncOutcome outcome) => outcome switch
    {
        PosSettingsSyncOutcome.Applied => "settings.posSettings.sync.applied",
        PosSettingsSyncOutcome.UpToDate => "settings.posSettings.sync.upToDate",
        PosSettingsSyncOutcome.Discarded => "settings.posSettings.sync.discarded",
        PosSettingsSyncOutcome.ServerUnsupported => "settings.posSettings.sync.serverUnsupported",
        PosSettingsSyncOutcome.NotReady => "settings.posSettings.sync.notReady",
        _ => "settings.posSettings.sync.failed"
    };

    partial void OnIsPosSettingsSyncingChanged(bool value)
    {
        OnPropertyChanged(nameof(PosSettingsSyncButtonText));
        SyncPosSettingsNowCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 开关被拨动：交给服务保存。被锁定 / 失败时开关回滚并提示；成功后重读（服务可能把值写穿到旧页面的存储）。
    /// 返回的任务同时记在 <see cref="PosSettingToggleCompletion"/>，供测试等待。
    /// </summary>
    private Task SetPosSettingAsync(PosSettingRowViewModel row, bool value)
    {
        var task = SetPosSettingCoreAsync(row, value);
        _lastPosSettingToggleTask = task;
        return task;
    }

    internal Task PosSettingToggleCompletion => _lastPosSettingToggleTask;

    private async Task SetPosSettingCoreAsync(PosSettingRowViewModel row, bool value)
    {
        if (_posSettingsService is null)
        {
            row.SetValueSilently(!value);
            return;
        }

        var outcome = PosSettingChangeOutcome.Failed;
        try
        {
            // actor 只用于审计（离线临时修改）；没有登录收银员时为 null。
            outcome = await _posSettingsService.TrySetAsync(row.Key, value, _cashierSessionContext.CurrentSession);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteWarning("PosSettings", $"set setting failed key={row.Key} error={ex.GetType().Name}");
        }

        if (outcome != PosSettingChangeOutcome.Applied)
        {
            // 先把开关退回拨动前的值；随后重读会再以服务里的真实值为准。
            row.SetValueSilently(!value);
            SetStatus(outcome == PosSettingChangeOutcome.Locked
                ? "settings.posSettings.toggle.locked"
                : "settings.posSettings.toggle.failed");
        }

        await ReloadPosSettingsAsync(refreshService: false);
    }

    // ── 服务状态 → 界面 ──

    private void OnPosSettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_posSettingsDispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => _ = ReloadPosSettingsAsync(refreshService: false)));
            return;
        }

        // 这里不再调 RefreshAsync：服务触发 Changed 时 Current 已是最新，再刷新可能反过来再次触发事件。
        _ = ReloadPosSettingsAsync(refreshService: false);
    }

    /// <summary>
    /// 刷新「业务开关」页与旧页面开关：可选先让服务从存储重建状态（进入页面时），再套用 <see cref="IPosSettingsService.Current"/>，
    /// 最后从各开关真正的本机存储重读旧控件的值。
    /// </summary>
    private async Task ReloadPosSettingsAsync(bool refreshService)
    {
        if (_posSettingsService is null || _disposed)
        {
            return;
        }

        await _posSettingsReloadGate.WaitAsync();
        try
        {
            if (refreshService)
            {
                await _posSettingsService.RefreshAsync();
            }

            if (_disposed)
            {
                return;
            }

            ApplyPosSettingsView(_posSettingsService.Current);
            await ReloadPosSettingValuesFromStoresAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteWarning("PosSettings", $"reload failed error={ex.GetType().Name}");
        }
        finally
        {
            _posSettingsReloadGate.Release();
        }
    }

    private void ApplyPosSettingsView(PosSettingsView view)
    {
        _posSettingsView = view;
        foreach (var item in view.Items)
        {
            if (_posSettingRowsByKey.TryGetValue(item.Key, out var row))
            {
                row.ApplyState(item);
            }
        }

        OnPropertyChanged(nameof(PosSettingsLastSyncedText));
        OnPropertyChanged(nameof(IsPosSettingsOfflineBannerVisible));
        // Zeller 开关是否可点依赖锁定状态（旧页面绑定 CanChangeZellerUnmatchedRefund）。
        OnPropertyChanged(nameof(CanChangeZellerUnmatchedRefund));
    }

    /// <summary>
    /// 从各开关真正的本机存储重读旧控件的值（同步会把总部值写穿到这些存储）。
    /// 先把排队中的自动保存写完，读取在抑制自动保存之外完成（避免长时间 await 期间吞掉用户的其它修改），
    /// 只在最后同步赋值那一小段抑制自动保存——回显存储内容不是用户修改，不能触发保存。
    /// 只刷新这 8 个开关的值，其它可能有未保存草稿的字段（端口、门店信息等）不动。
    /// </summary>
    private async Task ReloadPosSettingValuesFromStoresAsync()
    {
        await FlushAutoSaveBeforeReloadAsync();

        PaymentMethodSettings? payment = null;
        bool? restartPrompt = null;
        ReceiptPrinterSettings? receipt = null;
        bool? zellerUnmatchedRefund = null;

        if (_paymentMethodSettingsService is not null)
        {
            payment = await TryReadAsync("payment methods", () => _paymentMethodSettingsService.LoadAsync());
        }

        if (_systemRestartSettingsStore is not null)
        {
            restartPrompt = await TryReadAsync<bool?>("restart prompt", async () => await _systemRestartSettingsStore.IsPromptEnabledAsync());
        }

        if (_receiptPrinterSettingsStore is not null)
        {
            receipt = await TryReadAsync("receipt options", () => _receiptPrinterSettingsStore.LoadAsync());
        }

        var configuration = await TryReadAsync("zeller options", () => _setupService.LoadConfigurationAsync());
        if (configuration is not null)
        {
            zellerUnmatchedRefund = configuration.ZellerAllowUnmatchedRefund;
        }

        if (_disposed)
        {
            return;
        }

        using (SuppressAutoSave())
        {
            if (payment is not null)
            {
                UseManualCard = payment.UseManualCard;
                VoucherEnabled = payment.VoucherEnabled;
                ShowOrderList = payment.ShowOrderList;
                RaiseActivePaymentProviderProperties();
            }

            if (restartPrompt is { } restart)
            {
                // 与加载共用同一个「正在回显」标志：赋值不会再触发保存。
                _isLoadingRestartPrompt = true;
                try
                {
                    IsRestartPromptEnabled = restart;
                }
                finally
                {
                    _isLoadingRestartPrompt = false;
                }
            }

            if (receipt is not null)
            {
                ReceiptPrintBankReceiptText = receipt.PrintBankReceiptText;
                ReceiptNoSaleCashDrawerRequiresRescan = receipt.NoSaleCashDrawerRequiresRescan;
                ReceiptReturnsRequireRescan = receipt.ReturnsRequireRescan;
            }
        }

        if (zellerUnmatchedRefund is { } allow && allow != _zellerAllowUnmatchedRefund)
        {
            // Zeller 开关不走自动保存，直接复用加载时的回显写法。
            _zellerAllowUnmatchedRefund = allow;
            OnPropertyChanged(nameof(ZellerAllowUnmatchedRefund));
            OnPropertyChanged(nameof(ZellerUnmatchedRefundStateText));
        }
    }

    /// <summary>读存储失败只记日志、保持界面当前值，不影响其它开关的重读。</summary>
    private static async Task<T?> TryReadAsync<T>(string name, Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteWarning("PosSettings", $"reload {name} failed error={ex.GetType().Name}");
            return default;
        }
    }

    private void RaisePosSettingsLocalizedProperties()
    {
        foreach (var row in PosSettingRows)
        {
            row.RefreshText();
        }

        OnPropertyChanged(nameof(PosSettingsTitleText));
        OnPropertyChanged(nameof(PosSettingsSyncButtonText));
        OnPropertyChanged(nameof(PosSettingsOfflineTitleText));
        OnPropertyChanged(nameof(PosSettingsOfflineDetailText));
        OnPropertyChanged(nameof(PosSettingsLastSyncedText));
    }
}
