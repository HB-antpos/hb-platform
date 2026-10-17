using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>离开设置页时，用户在「有修改没保存成功」弹窗里的选择。</summary>
public enum SettingsLeaveChoice
{
    Stay,
    Discard,
    Retry
}

/// <summary>离开提示弹窗里的一行：位置（分类 › 分区）与原因。</summary>
public sealed record SettingsLeaveItem(string LocationText, string DetailText, bool IsFailed);

/// <summary>
/// 设置页「改了直接保存」与「没保存成功时离开要提示」。
/// - 普通本机设置（重启提醒、付款方式三个开关、小票打印机全部字段）：开关立即保存，文本停顿 1.5 秒保存。
/// - 影响收款或会切换服务器的设置（Square、Linkly、API 服务器地址）：只记「草稿」，仍由页面上的
///   「启用 Square / 启用 Linkly / 保存地址」完成测试、授权与写入；离开时一并提示。
/// - 进入受权限保护的分类时已经授权过一次，自动保存是本机写入，不再逐次要求扫码。
/// </summary>
public sealed partial class SettingsViewModel
{
    internal const string RestartPromptSaveKey = "data.restartPrompt";
    internal const string PaymentMethodsSaveKey = "payment.methods";
    internal const string SquareSaveKey = "payment.square";
    internal const string LinklySaveKey = "payment.linkly";
    internal const string ReceiptHardwareSaveKey = "receipt.hardware";
    internal const string ReceiptStoreSaveKey = "receipt.store";
    internal const string ReceiptTermsSaveKey = "receipt.terms";
    internal const string ServerAddressSaveKey = "device.serverAddress";

    /// <summary>文本框停顿多久后自动保存。</summary>
    internal static readonly TimeSpan TextAutoSaveDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>离开时最多等在途保存多久，超时就当作「没保存成功」进入提示。</summary>
    internal static readonly TimeSpan LeaveFlushTimeout = TimeSpan.FromSeconds(3);

    private SettingsAutoSaveTracker _autoSave = new();
    private bool _autoSaveEnabled;
    private int _autoSaveSuppression;
    private bool _isLeaving;
    private TaskCompletionSource<SettingsLeaveChoice>? _leaveDialogCompletion;
    private CardTerminalConfiguration? _squareDraftBaseline;
    private CardTerminalConfiguration? _linklyDraftBaseline;
    private CardTerminalConfiguration? _lastSyncedTerminalConfiguration;

    [ObservableProperty]
    private bool _isLeaveDialogOpen;

    [ObservableProperty]
    private string _leaveDialogTitleText = string.Empty;

    [ObservableProperty]
    private bool _leaveDialogCanRetry;

    [ObservableProperty]
    private bool _leaveDialogCanDiscard = true;

    /// <summary>离开提示里逐项列出的未保存修改。</summary>
    public ObservableCollection<SettingsLeaveItem> LeaveDialogItems { get; } = [];

    public IRelayCommand LeaveStayCommand { get; private set; } = null!;

    public IRelayCommand LeaveDiscardCommand { get; private set; } = null!;

    public IRelayCommand LeaveRetryCommand { get; private set; } = null!;

    /// <summary>页面操作条上「N 项未保存 · 重试」的重试。</summary>
    public IAsyncRelayCommand RetryFailedSavesCommand { get; private set; } = null!;

    /// <summary>是否启用自动保存（生产装配时开启；旧单元测试默认关闭以保持原有的显式保存语义）。</summary>
    public bool IsAutoSaveEnabled => _autoSaveEnabled;

    private bool IsAutoSaveSuppressed => _autoSaveSuppression > 0;

    private void InitializeAutoSave(bool enabled, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _autoSaveEnabled = enabled;
        _autoSave = new SettingsAutoSaveTracker(delay);
        LeaveStayCommand = new RelayCommand(() => CompleteLeaveDialog(SettingsLeaveChoice.Stay));
        LeaveDiscardCommand = new RelayCommand(() => CompleteLeaveDialog(SettingsLeaveChoice.Discard));
        LeaveRetryCommand = new RelayCommand(() => CompleteLeaveDialog(SettingsLeaveChoice.Retry));
        RetryFailedSavesCommand = new AsyncRelayCommand(() => _autoSave.RetryFailedAsync());

        if (!enabled)
        {
            return;
        }

        _autoSave.Register(RestartPromptSaveKey, SettingsCategory.DataMaintenance, "settings.autosave.section.restartPrompt", SaveRestartPromptForAutoSaveAsync);
        _autoSave.Register(PaymentMethodsSaveKey, SettingsCategory.PaymentTerminal, "settings.autosave.section.paymentMethods", SavePaymentMethodsForAutoSaveAsync);
        _autoSave.RegisterExplicit(SquareSaveKey, SettingsCategory.PaymentTerminal, "settings.autosave.section.square");
        _autoSave.RegisterExplicit(LinklySaveKey, SettingsCategory.PaymentTerminal, "settings.autosave.section.linkly");
        // 小票三个分区共用一个写入（存储是一整份设置），分开登记是为了各分页单独显示状态与离开提示的位置。
        _autoSave.Register(ReceiptHardwareSaveKey, SettingsCategory.ReceiptPrinter, "settings.autosave.section.receiptHardware", SaveReceiptPrinterForAutoSaveAsync);
        _autoSave.Register(ReceiptStoreSaveKey, SettingsCategory.ReceiptPrinter, "settings.autosave.section.receiptStore", SaveReceiptPrinterForAutoSaveAsync);
        _autoSave.Register(ReceiptTermsSaveKey, SettingsCategory.ReceiptPrinter, "settings.autosave.section.receiptTerms", SaveReceiptPrinterForAutoSaveAsync);
        _autoSave.RegisterExplicit(ServerAddressSaveKey, SettingsCategory.DeviceRegistration, "settings.autosave.section.serverAddress");
        _autoSave.Changed += (_, _) => RaiseAutoSaveProperties();
        if (_apiServerSettings is not null)
        {
            _apiServerSettings.PropertyChanged += OnApiServerSettingsPropertyChanged;
        }
    }

    // ── 页面与分区的保存状态（XAML 绑定）──

    /// <summary>当前分类的总体保存状态：Saved / Saving / Unsaved；当前分类没有可保存的设置时为 None。</summary>
    public string PageSaveStatus
    {
        get
        {
            var entries = _autoSave.Snapshot().Where(e => e.Category == SelectedCategory).ToArray();
            if (entries.Length == 0)
            {
                return "None";
            }

            if (entries.Any(e => e.State is SettingsSaveState.Failed or SettingsSaveState.Draft))
            {
                return "Unsaved";
            }

            return entries.Any(e => e.State is SettingsSaveState.Pending or SettingsSaveState.Saving) ? "Saving" : "Saved";
        }
    }

    public string PageSaveStatusText => PageSaveStatus switch
    {
        "Saving" => T("settings.autosave.page.saving"),
        "Unsaved" => string.Format(CultureInfo.CurrentCulture, T("settings.autosave.page.unsaved"), UnsavedCountFor(SelectedCategory)),
        "Saved" => T("settings.autosave.page.saved"),
        _ => string.Empty
    };

    public bool IsPageSaveStatusVisible => PageSaveStatus != "None";

    /// <summary>页面状态里「重试」只对保存失败有意义（草稿要点页面上的按钮）。</summary>
    public bool CanRetryPageSaves => _autoSave.Snapshot().Any(e => e.Category == SelectedCategory && e.State == SettingsSaveState.Failed);

    public int DataMaintenanceUnsavedCount => UnsavedCountFor(SettingsCategory.DataMaintenance);

    public int PaymentTerminalUnsavedCount => UnsavedCountFor(SettingsCategory.PaymentTerminal);

    public int ReceiptPrinterUnsavedCount => UnsavedCountFor(SettingsCategory.ReceiptPrinter);

    public int DeviceRegistrationUnsavedCount => UnsavedCountFor(SettingsCategory.DeviceRegistration);

    public bool HasUnsavedSettings => _autoSave.Unsaved().Count > 0;

    public string RestartPromptSaveStateText => SectionStateText(RestartPromptSaveKey);

    public string PaymentMethodsSaveStateText => SectionStateText(PaymentMethodsSaveKey);

    public string SquareSaveStateText => SectionStateText(SquareSaveKey);

    public string LinklySaveStateText => SectionStateText(LinklySaveKey);

    public string ReceiptHardwareSaveStateText => SectionStateText(ReceiptHardwareSaveKey);

    public string ReceiptStoreSaveStateText => SectionStateText(ReceiptStoreSaveKey);

    public string ReceiptTermsSaveStateText => SectionStateText(ReceiptTermsSaveKey);

    public string ServerAddressSaveStateText => SectionStateText(ServerAddressSaveKey);

    public string RestartPromptSaveState => _autoSave.GetState(RestartPromptSaveKey).ToString();

    public string PaymentMethodsSaveState => _autoSave.GetState(PaymentMethodsSaveKey).ToString();

    public string SquareSaveState => _autoSave.GetState(SquareSaveKey).ToString();

    public string LinklySaveState => _autoSave.GetState(LinklySaveKey).ToString();

    public string ReceiptHardwareSaveState => _autoSave.GetState(ReceiptHardwareSaveKey).ToString();

    public string ReceiptStoreSaveState => _autoSave.GetState(ReceiptStoreSaveKey).ToString();

    public string ReceiptTermsSaveState => _autoSave.GetState(ReceiptTermsSaveKey).ToString();

    public string ServerAddressSaveState => _autoSave.GetState(ServerAddressSaveKey).ToString();

    private int UnsavedCountFor(SettingsCategory category) =>
        _autoSave.Snapshot().Count(e => e.Category == category && e.State is SettingsSaveState.Failed or SettingsSaveState.Draft);

    private string SectionStateText(string key) => _autoSave.GetState(key) switch
    {
        SettingsSaveState.Pending or SettingsSaveState.Saving => T("settings.autosave.section.saving"),
        SettingsSaveState.Failed => T("settings.autosave.section.failed"),
        SettingsSaveState.Draft => T("settings.autosave.section.draft"),
        _ => string.Empty
    };

    private void RaiseAutoSaveProperties()
    {
        OnPropertyChanged(nameof(PageSaveStatus));
        OnPropertyChanged(nameof(PageSaveStatusText));
        OnPropertyChanged(nameof(IsPageSaveStatusVisible));
        OnPropertyChanged(nameof(CanRetryPageSaves));
        OnPropertyChanged(nameof(DataMaintenanceUnsavedCount));
        OnPropertyChanged(nameof(PaymentTerminalUnsavedCount));
        OnPropertyChanged(nameof(ReceiptPrinterUnsavedCount));
        OnPropertyChanged(nameof(DeviceRegistrationUnsavedCount));
        OnPropertyChanged(nameof(HasUnsavedSettings));
        RaiseSubTabUnsavedProperties();
        foreach (var name in new[]
                 {
                     nameof(RestartPromptSaveStateText), nameof(PaymentMethodsSaveStateText), nameof(SquareSaveStateText),
                     nameof(LinklySaveStateText), nameof(ReceiptHardwareSaveStateText), nameof(ReceiptStoreSaveStateText),
                     nameof(ReceiptTermsSaveStateText), nameof(ServerAddressSaveStateText),
                     nameof(RestartPromptSaveState), nameof(PaymentMethodsSaveState), nameof(SquareSaveState),
                     nameof(LinklySaveState), nameof(ReceiptHardwareSaveState), nameof(ReceiptStoreSaveState),
                     nameof(ReceiptTermsSaveState), nameof(ServerAddressSaveState)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // ── 改动挂钩 ──

    /// <summary>加载 / 回显存储内容期间的赋值不是用户修改，不能触发保存或草稿。</summary>
    private AutoSaveSuppression SuppressAutoSave() => new(this);

    private void MarkAutoSaveChanged(string key, TimeSpan delay)
    {
        if (!_autoSaveEnabled || IsAutoSaveSuppressed)
        {
            return;
        }

        _autoSave.MarkChanged(key, delay);
    }

    partial void OnUseManualCardChanged(bool value) => MarkAutoSaveChanged(PaymentMethodsSaveKey, TimeSpan.Zero);

    partial void OnVoucherEnabledChanged(bool value) => MarkAutoSaveChanged(PaymentMethodsSaveKey, TimeSpan.Zero);

    partial void OnShowOrderListChanged(bool value) => MarkAutoSaveChanged(PaymentMethodsSaveKey, TimeSpan.Zero);

    partial void OnReceiptPrinterPortTextChanged(string value) => MarkAutoSaveChanged(ReceiptHardwareSaveKey, TextAutoSaveDelay);

    partial void OnReceiptPrintBankReceiptTextChanged(bool value) => MarkAutoSaveChanged(ReceiptHardwareSaveKey, TimeSpan.Zero);

    partial void OnReceiptNoSaleCashDrawerRequiresRescanChanged(bool value) => MarkAutoSaveChanged(ReceiptHardwareSaveKey, TimeSpan.Zero);

    partial void OnReceiptReturnsRequireRescanChanged(bool value) => MarkAutoSaveChanged(ReceiptHardwareSaveKey, TimeSpan.Zero);

    partial void OnReceiptBrandNameTextChanged(string value) => MarkAutoSaveChanged(ReceiptStoreSaveKey, TextAutoSaveDelay);

    partial void OnReceiptStoreNameTextChanged(string value) => MarkAutoSaveChanged(ReceiptStoreSaveKey, TextAutoSaveDelay);

    partial void OnReceiptStoreAddressTextChanged(string value) => MarkAutoSaveChanged(ReceiptStoreSaveKey, TextAutoSaveDelay);

    partial void OnReceiptStorePhoneTextChanged(string value) => MarkAutoSaveChanged(ReceiptStoreSaveKey, TextAutoSaveDelay);

    partial void OnReceiptAbnTextChanged(string value) => MarkAutoSaveChanged(ReceiptStoreSaveKey, TextAutoSaveDelay);

    partial void OnReceiptReturnPolicyTextChanged(string value) => MarkAutoSaveChanged(ReceiptTermsSaveKey, TextAutoSaveDelay);

    partial void OnReceiptVoucherTermsTextChanged(string value) => MarkAutoSaveChanged(ReceiptTermsSaveKey, TextAutoSaveDelay);

    partial void OnReceiptInstallmentTermsTextChanged(string value) => MarkAutoSaveChanged(ReceiptTermsSaveKey, TextAutoSaveDelay);

    /// <summary>
    /// 加载完成后：Square / Linkly 的比较基线都取当前已保存配置。
    /// </summary>
    private void ResetPaymentTerminalDraftBaselines()
    {
        _squareDraftBaseline = _loadedConfiguration;
        _linklyDraftBaseline = _loadedConfiguration;
        _lastSyncedTerminalConfiguration = _loadedConfiguration;
    }

    /// <summary>
    /// Square / Linkly 协调器同步回来之后调用：已保存配置换了新对象，说明刚保存成功。
    /// 保存 Square 时配置里的 Linkly 字段取自界面上尚未保存的输入（模式还会落回默认），
    /// 所以只更新「本次启用的那一家」的基线，另一家仍按它上次保存 / 加载时的值比较。
    /// </summary>
    private void OnPaymentTerminalStateSynced()
    {
        if (!ReferenceEquals(_loadedConfiguration, _lastSyncedTerminalConfiguration))
        {
            _lastSyncedTerminalConfiguration = _loadedConfiguration;
            if (_loadedConfiguration.Processor == CardProcessorKind.Square)
            {
                _squareDraftBaseline = _loadedConfiguration;
            }
            else
            {
                _linklyDraftBaseline = _loadedConfiguration;
            }
        }

        RefreshPaymentTerminalDraftStates();
    }

    /// <summary>
    /// Square / Linkly 草稿：拿当前输入与基线比较，不同就是草稿、相同就清除。
    /// 用比较而不是「谁改过」，这样程序自动选中已保存的门店 / 设备、或改回原值时都不会误报。
    /// </summary>
    private void RefreshPaymentTerminalDraftStates()
    {
        if (!_autoSaveEnabled || IsAutoSaveSuppressed || _syncingSquare || _syncingLinkly)
        {
            return;
        }

        SetDraft(SquareSaveKey, IsSquareDraft());
        SetDraft(LinklySaveKey, IsLinklyDraft());
    }

    private void SetDraft(string key, bool isDraft)
    {
        var current = _autoSave.GetState(key);
        if (isDraft && current != SettingsSaveState.Draft)
        {
            _autoSave.MarkChanged(key, TimeSpan.Zero);
        }
        else if (!isDraft && current == SettingsSaveState.Draft)
        {
            _autoSave.MarkSaved(key);
        }
    }

    private bool IsSquareDraft()
    {
        var saved = _squareDraftBaseline ?? _loadedConfiguration;
        if (IsSquareSandbox != (saved.Environment == CardTerminalEnvironment.Sandbox))
        {
            return true;
        }

        if (SelectedSquareLocation is not null &&
            !string.Equals(SelectedSquareLocation.Id, saved.SquareLocationId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (SelectedSquareDevice is not null &&
            !SquareDeviceIdNormalizer.AreEquivalent(SelectedSquareDevice.Id, saved.SquareDeviceId))
        {
            return true;
        }

        // 超时秒数两家共用：算在当前启用的那一家头上，并与当前生效的配置比较。
        return _loadedConfiguration.Processor == CardProcessorKind.Square && IsTimeoutDraft(_loadedConfiguration);
    }

    private bool IsLinklyDraft()
    {
        var saved = _linklyDraftBaseline ?? _loadedConfiguration;
        if (IsLinklySandbox != (saved.Environment == CardTerminalEnvironment.Sandbox) ||
            !string.Equals(LinklyHostText?.Trim(), saved.LinklyHost, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(LinklyPortText?.Trim(), saved.LinklyPort.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            SelectedLinklyMode != ToSettingsMode(saved.LinklyConnectionMode))
        {
            return true;
        }

        // 与 LoadAsync 回显顺序一致：先按已存优先级规整，再把已存的主模式提到第一位。
        var savedPriority = CardTerminalSettings
            .NormalizeLinklyConnectionModePriority(saved.LinklyConnectionModePriority, saved.LinklyConnectionMode)
            .Select(ToSettingsMode)
            .ToList();
        var savedPrimary = ToSettingsMode(saved.LinklyConnectionMode);
        if (savedPriority.Remove(savedPrimary))
        {
            savedPriority.Insert(0, savedPrimary);
        }

        if (!LinklyModePriorityItems.Select(item => item.Mode).SequenceEqual(savedPriority))
        {
            return true;
        }

        return _loadedConfiguration.Processor != CardProcessorKind.Square && IsTimeoutDraft(_loadedConfiguration);
    }

    private bool IsTimeoutDraft(CardTerminalConfiguration saved) =>
        !string.Equals(
            TimeoutSecondsText?.Trim(),
            saved.TerminalTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    private void OnApiServerSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ApiServerSettingsViewModel.HasUnsavedAddress) && !IsAutoSaveSuppressed)
        {
            RefreshServerAddressDraftState();
        }
    }

    /// <summary>
    /// 服务器地址草稿以 ApiServerSettingsViewModel 的判断为准：等待重启期间重新进入设置页时，
    /// 共享 VM 会保留已保存待生效的地址和用户后来的输入，不能简单清零。
    /// </summary>
    private void RefreshServerAddressDraftState()
    {
        if (_autoSaveEnabled && _apiServerSettings is not null)
        {
            SetDraft(ServerAddressSaveKey, _apiServerSettings.HasUnsavedAddress);
        }
    }

    // ── 自动保存写入（本机写入，失败返回原因，不弹窗、不置整页忙碌）──

    private async Task<SettingsSaveResult> SaveRestartPromptForAutoSaveAsync(CancellationToken cancellationToken)
    {
        if (_systemRestartSettingsStore is null)
        {
            return SettingsSaveResult.Success;
        }

        var enabled = IsRestartPromptEnabled;
        try
        {
            await _systemRestartSettingsStore.SetPromptEnabledAsync(enabled, cancellationToken);
            ConsoleLog.Write("RestartAfterClose", $"restart prompt setting changed enabled={enabled}");
            return SettingsSaveResult.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteWarning("RestartAfterClose", $"save restart prompt setting failed error={ex.GetType().Name}");
            return SettingsSaveResult.Failure(ex.Message);
        }
    }

    private async Task<SettingsSaveResult> SavePaymentMethodsForAutoSaveAsync(CancellationToken cancellationToken)
    {
        if (_paymentMethodSettingsService is null)
        {
            return SettingsSaveResult.Success;
        }

        // 三个开关作为同一份本机配置写入；进入「支付终端」分类时已授权。
        var settings = new PaymentMethodSettings(UseManualCard, VoucherEnabled, ShowOrderList);
        try
        {
            await _paymentMethodSettingsService.SaveAsync(settings);
            RaiseActivePaymentProviderProperties();
            ConsoleLog.Write("PaymentSettings", $"payment methods autosaved manualCard={settings.UseManualCard} voucher={settings.VoucherEnabled} orderList={settings.ShowOrderList}");
            return SettingsSaveResult.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteWarning("PaymentSettings", $"payment methods autosave failed error={ex.GetType().Name}", exception: ex);
            return SettingsSaveResult.Failure(ex.Message);
        }
    }

    private async Task<SettingsSaveResult> SaveReceiptPrinterForAutoSaveAsync(CancellationToken cancellationToken)
    {
        if (_receiptPrinterSettingsStore is null)
        {
            return SettingsSaveResult.Success;
        }

        try
        {
            // 只写入、不回显：回显会打断正在输入的文本框；总部下发时存储层本来就只保存硬件部分。
            await _receiptPrinterSettingsStore.SaveAsync(CreateReceiptPrinterSettingsFromFields());
            return SettingsSaveResult.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteWarning("ReceiptPrinter", $"receipt printer autosave failed error={ex.GetType().Name}", exception: ex);
            return SettingsSaveResult.Failure(ex.Message);
        }
    }

    // ── 进入 / 释放 ──

    private async Task FlushAutoSaveBeforeReloadAsync()
    {
        if (_autoSaveEnabled)
        {
            await _autoSave.FlushAsync(LeaveFlushTimeout);
        }
    }

    /// <summary>
    /// 设置页 VM 是缓存的，每次进入都会调 LoadAsync。上次停在受权限保护的分类时回到「数据维护」：
    /// 自动保存不再逐次授权，必须让下一位收银员重新点进分类（重新走授权），不能沿用上一次的授权直接修改。
    /// 停在服务商分页时，按加载回显的当前服务商同步分页，避免分页条与内容不一致。
    /// </summary>
    private void ResetCategoryAfterLoad()
    {
        if (_autoSaveEnabled &&
            SelectedCategory is SettingsCategory.PaymentTerminal or SettingsCategory.ReceiptPrinter or SettingsCategory.DeviceRegistration)
        {
            SelectedCategory = SettingsCategory.DataMaintenance;
        }

        if (IsProviderSubTabSelected)
        {
            SelectedPaymentSubTab = SelectedProviderTabIndex == 1 ? SettingsPaymentSubTab.Linkly : SettingsPaymentSubTab.Square;
        }
    }

    private void ReleaseAutoSave()
    {
        if (_apiServerSettings is not null)
        {
            _apiServerSettings.PropertyChanged -= OnApiServerSettingsPropertyChanged;
        }

        // 还在等防抖的修改直接写入（不等待），不随页面释放丢掉。
        _ = _autoSave.FlushAsync(LeaveFlushTimeout);
    }

    // ── 离开设置页 ──

    private async Task ReturnToPosAsync()
    {
        if (!await ConfirmLeaveSettingsAsync())
        {
            return;
        }

        ReturnToPos();
    }

    /// <summary>
    /// 离开设置页前：先把在等防抖 / 正在写入的保存跑完（最多 3 秒），仍有保存失败、草稿或没写完的项就弹窗，
    /// 让用户选「重试保存 / 放弃修改并离开 / 留在本页」。返回 true 表示可以离开。
    /// </summary>
    internal async Task<bool> ConfirmLeaveSettingsAsync()
    {
        if (!_autoSaveEnabled)
        {
            return true;
        }

        if (_isLeaving)
        {
            return false;
        }

        _isLeaving = true;
        try
        {
            await _autoSave.FlushAsync(LeaveFlushTimeout);
            while (true)
            {
                var entries = _autoSave.Snapshot().Where(e => e.State != SettingsSaveState.Saved).ToArray();
                if (entries.Length == 0)
                {
                    return true;
                }

                var choice = await ShowLeaveDialogAsync(entries);
                if (choice == SettingsLeaveChoice.Stay)
                {
                    return false;
                }

                if (choice == SettingsLeaveChoice.Discard)
                {
                    await DiscardUnsavedSettingsAsync();
                    return true;
                }

                // 重试同样限时：存储卡住时不能让「返回收银」一直处于执行中，超时后回到弹窗再选。
                await Task.WhenAny(_autoSave.RetryFailedAsync(), Task.Delay(LeaveFlushTimeout));
                await _autoSave.FlushAsync(LeaveFlushTimeout);
            }
        }
        finally
        {
            _isLeaving = false;
        }
    }

    private Task<SettingsLeaveChoice> ShowLeaveDialogAsync(IReadOnlyList<SettingsSaveEntry> entries)
    {
        LeaveDialogItems.Clear();
        foreach (var entry in entries)
        {
            var location = $"{CategoryTitle(entry.Category)} › {T(entry.LabelKey)}";
            var detail = entry.State switch
            {
                SettingsSaveState.Failed when !string.IsNullOrWhiteSpace(entry.Message) =>
                    string.Format(CultureInfo.CurrentCulture, T("settings.autosave.leave.failed"), entry.Message),
                SettingsSaveState.Failed => T("settings.autosave.leave.failedNoReason"),
                SettingsSaveState.Draft => T("settings.autosave.leave.draft"),
                _ => T("settings.autosave.leave.saving")
            };
            LeaveDialogItems.Add(new SettingsLeaveItem(location, detail, entry.State != SettingsSaveState.Draft));
        }

        LeaveDialogTitleText = string.Format(CultureInfo.CurrentCulture, T("settings.autosave.leave.title"), entries.Count);
        // 只有草稿时重试没有意义（草稿要点页面上的按钮），隐藏「重试保存」。
        LeaveDialogCanRetry = entries.Any(e => e.State != SettingsSaveState.Draft);
        // 还有写入没结束时不给「放弃」：那次写入随后完成，被放弃的值反而会落盘。
        LeaveDialogCanDiscard = entries.All(e => e.State is SettingsSaveState.Failed or SettingsSaveState.Draft);
        _leaveDialogCompletion = new TaskCompletionSource<SettingsLeaveChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsLeaveDialogOpen = true;
        return _leaveDialogCompletion.Task;
    }

    private void CompleteLeaveDialog(SettingsLeaveChoice choice)
    {
        var completion = Interlocked.Exchange(ref _leaveDialogCompletion, null);
        IsLeaveDialogOpen = false;
        completion?.TrySetResult(choice);
    }

    /// <summary>放弃未保存的修改：从存储重新加载（界面回到当前生效的值），状态全部清零。</summary>
    private async Task DiscardUnsavedSettingsAsync()
    {
        // 等待重启时 LoadAsync 不会覆盖地址输入框，先单独还原地址。
        _apiServerSettings?.DiscardUnsavedAddress();
        await LoadAsync();
        _autoSave.ResetAll();
    }

    private string CategoryTitle(SettingsCategory category) => category switch
    {
        SettingsCategory.DataMaintenance => T("settings.category.dataMaintenance"),
        SettingsCategory.PaymentTerminal => T("settings.category.paymentTerminal"),
        SettingsCategory.ReceiptPrinter => T("settings.category.receiptPrinter"),
        SettingsCategory.DeviceRegistration => T("settings.category.deviceRegistrationNav"),
        // 远程维护 / App 下载没有可保存的设置，不会出现在离开提示里。
        _ => T("settings.category.mobileAppDownload")
    };

    private readonly struct AutoSaveSuppression : IDisposable
    {
        private readonly SettingsViewModel _owner;

        public AutoSaveSuppression(SettingsViewModel owner)
        {
            _owner = owner;
            _owner._autoSaveSuppression++;
        }

        public void Dispose() => _owner._autoSaveSuppression--;
    }
}
