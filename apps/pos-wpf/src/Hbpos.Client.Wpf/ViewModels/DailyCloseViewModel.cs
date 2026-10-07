using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class DailyCloseViewModel : ObservableObject, IDisposable
{
    private const int MaxCashCountDigits = 9;
    private const int HistoryTabIndex = 0;
    private const int LinklySettlementTabIndex = 1;

    private readonly IDailyCloseService _dailyCloseService;
    private readonly IDailyClosePrintService _dailyClosePrintService;
    private readonly ILinklySettlementService? _linklySettlementService;
    private readonly ILocalizationService? _localization;
    private readonly ICashierSessionContext _cashierSessionContext;
    private readonly bool _enforcePermissions;
    private readonly Action? _returnToPos;
    private readonly IOperationAuditLogger? _operationAuditLogger;
    private readonly IOperationAuthorizationService? _operationAuthorizationService;
    private readonly Func<DateTime, Task<bool>>? _confirmLinklySettlementAsync;
    private DailyCloseReport? _currentReport;
    private DailyCloseDraftScope? _dailyCloseDraftScope;
    private int _archivePreviewVersion;
    private bool _replaceKeypadOnNextInput;

    [ObservableProperty]
    private PosSessionState _session;

    [ObservableProperty]
    private DateTime? _selectedDate = DateTime.Today;

    [ObservableProperty]
    private string _keypadBuffer = string.Empty;

    [ObservableProperty]
    private CashDenominationEntryViewModel? _selectedCashDenomination;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private bool _hasDailyCloseDraft;

    [ObservableProperty]
    private bool _isCashCountWorkspaceOpen;

    [ObservableProperty]
    private bool _isDiscardDailyCloseDraftConfirmationOpen;

    // 保存日结后会自动发送 Linkly 结算时，在保存按钮上提示收银员。
    [ObservableProperty]
    private bool _willAutoSendLinklySettlement;

    [ObservableProperty]
    private decimal _expectedCashAmount;

    [ObservableProperty]
    private decimal _grossAmount;

    [ObservableProperty]
    private decimal _netAmount;

    [ObservableProperty]
    private decimal _refundAmount;

    [ObservableProperty]
    private decimal _returnQuantity;

    [ObservableProperty]
    private int _transactionCount;

    private DailyCloseArchiveListItemViewModel? _selectedArchive;
    private LocalLinklySettlementRecord? _selectedSettlement;
    private LocalLinklySettlementManualResolution? _pendingSettlementManualResolution;

    public DailyCloseViewModel(
        IDailyCloseService dailyCloseService,
        IDailyClosePrintService dailyClosePrintService,
        PosSessionState session,
        ILocalizationService? localization = null,
        Action? returnToPos = null,
        ICashierSessionContext? cashierSessionContext = null,
        bool enforcePermissionsWhenNoCashier = false,
        IOperationAuditLogger? operationAuditLogger = null,
        IOperationAuthorizationService? operationAuthorizationService = null,
        ILinklySettlementService? linklySettlementService = null,
        Func<DateTime, Task<bool>>? confirmLinklySettlementAsync = null)
    {
        _dailyCloseService = dailyCloseService;
        _dailyClosePrintService = dailyClosePrintService;
        _session = session;
        _localization = localization;
        _cashierSessionContext = cashierSessionContext ?? new CashierSessionContext();
        _enforcePermissions = enforcePermissionsWhenNoCashier;
        _operationAuditLogger = operationAuditLogger;
        _operationAuthorizationService = operationAuthorizationService;
        _linklySettlementService = linklySettlementService;
        _confirmLinklySettlementAsync = confirmLinklySettlementAsync;
        if (session.CashierSession is not null)
        {
            _cashierSessionContext.SetCurrent(session.CashierSession);
        }

        _returnToPos = returnToPos;

        foreach (var denomination in _dailyCloseService.Denominations)
        {
            var entry = new CashDenominationEntryViewModel(denomination.Value, denomination.Label, denomination.Kind);
            entry.PropertyChanged += OnDenominationChanged;
            Denominations.Add(entry);
        }

        RefreshSummaryCommand = new AsyncRelayCommand(RefreshSummaryAsync, () => !IsBusy);
        SaveAndPrintCommand = new AsyncRelayCommand(SaveAndPrintAsync, CanSaveAndPrint);
        LoadHistoryCommand = new AsyncRelayCommand(LoadHistoryAsync, () => !IsBusy);
        CreateOrResumeDailyCloseCommand = new AsyncRelayCommand(CreateOrResumeDailyCloseAsync, () => !IsBusy);
        CloseCashCountWorkspaceCommand = new RelayCommand(
            CloseCashCountWorkspace,
            () => !IsBusy && IsCashCountWorkspaceOpen);
        RequestDiscardDailyCloseDraftCommand = new RelayCommand(
            RequestDiscardDailyCloseDraft,
            () => !IsBusy && HasDailyCloseDraft && IsCashCountWorkspaceOpen && !IsDiscardDailyCloseDraftConfirmationOpen);
        ConfirmDiscardDailyCloseDraftCommand = new RelayCommand(
            ConfirmDiscardDailyCloseDraft,
            () => !IsBusy && HasDailyCloseDraft && IsDiscardDailyCloseDraftConfirmationOpen);
        CancelDiscardDailyCloseDraftCommand = new RelayCommand(
            CancelDiscardDailyCloseDraft,
            () => !IsBusy && IsDiscardDailyCloseDraftConfirmationOpen);
        ReprintSelectedArchiveCommand = new AsyncRelayCommand(ReprintSelectedArchiveAsync, CanReprintSelectedArchive);
        SettleAndPrintCommand = new AsyncRelayCommand(SettleAndPrintAsync, CanSettleAndPrint);
        LoadSettlementHistoryCommand = new AsyncRelayCommand(LoadSettlementHistoryAsync, () => !IsBusy && _linklySettlementService is not null);
        ReprintSelectedSettlementCommand = new AsyncRelayCommand(ReprintSelectedSettlementAsync, CanReprintSelectedSettlement);
        PrepareSettlementManualResolutionCommand = new RelayCommand<LocalLinklySettlementManualResolution>(
            PrepareSettlementManualResolution,
            CanPrepareSettlementManualResolution);
        ConfirmSettlementManualResolutionCommand = new AsyncRelayCommand(
            ConfirmSettlementManualResolutionAsync,
            CanConfirmSettlementManualResolution);
        CancelSettlementManualResolutionCommand = new RelayCommand(
            CancelSettlementManualResolution,
            CanCancelSettlementManualResolution);
        OpenCashCountDialogCommand = new RelayCommand<CashDenominationEntryViewModel>(
            OpenCashCountDialog,
            CanOpenCashCountDialog);
        CancelCashCountDialogCommand = new RelayCommand(
            CancelCashCountDialog,
            () => !IsBusy && IsCashCountDialogOpen);
        KeypadInputCommand = new RelayCommand<string>(AppendKeypadInput, _ => !IsBusy && IsCashCountDialogOpen);
        KeypadBackspaceCommand = new RelayCommand(BackspaceKeypad, () => !IsBusy && IsCashCountDialogOpen && KeypadBuffer.Length > 0);
        KeypadClearCommand = new RelayCommand(ClearKeypad, () => !IsBusy && IsCashCountDialogOpen && KeypadBuffer.Length > 0);
        ApplyDenominationCommand = new RelayCommand<CashDenominationEntryViewModel>(ApplyDenominationCount, CanApplyDenominationCount);
        ReturnToPosCommand = new RelayCommand(() => _returnToPos?.Invoke(), () => _returnToPos is not null);
        StatusMessage = T("dailyClose.status.ready", "Review daily close history or create a new daily close.");
    }

    public ObservableCollection<CashDenominationEntryViewModel> Denominations { get; } = [];

    public ObservableCollection<DailyClosePaymentSummaryItemViewModel> PaymentSummaries { get; } = [];

    public ObservableCollection<DailyCloseArchiveListItemViewModel> Archives { get; } = [];

    public ObservableCollection<ReceiptPreviewRow> ArchivePreviewRows { get; } = [];

    public ObservableCollection<LocalLinklySettlementRecord> Settlements { get; } = [];

    public ObservableCollection<string> SettlementReceiptPreviewLines { get; } = [];

    public ObservableCollection<CashDenominationCount> SelectedArchiveNoteCounts { get; } = [];

    public ObservableCollection<CashDenominationCount> SelectedArchiveCoinCounts { get; } = [];

    public IAsyncRelayCommand RefreshSummaryCommand { get; }

    public IAsyncRelayCommand SaveAndPrintCommand { get; }

    public IAsyncRelayCommand LoadHistoryCommand { get; }

    public IAsyncRelayCommand CreateOrResumeDailyCloseCommand { get; }

    public IRelayCommand CloseCashCountWorkspaceCommand { get; }

    public IRelayCommand RequestDiscardDailyCloseDraftCommand { get; }

    public IRelayCommand ConfirmDiscardDailyCloseDraftCommand { get; }

    public IRelayCommand CancelDiscardDailyCloseDraftCommand { get; }

    public IAsyncRelayCommand ReprintSelectedArchiveCommand { get; }

    public IAsyncRelayCommand SettleAndPrintCommand { get; }

    public IAsyncRelayCommand LoadSettlementHistoryCommand { get; }

    public IAsyncRelayCommand ReprintSelectedSettlementCommand { get; }

    public IRelayCommand<LocalLinklySettlementManualResolution> PrepareSettlementManualResolutionCommand { get; }

    public IAsyncRelayCommand ConfirmSettlementManualResolutionCommand { get; }

    public IRelayCommand CancelSettlementManualResolutionCommand { get; }

    public IRelayCommand<CashDenominationEntryViewModel> OpenCashCountDialogCommand { get; }

    public IRelayCommand CancelCashCountDialogCommand { get; }

    public IRelayCommand<string> KeypadInputCommand { get; }

    public IRelayCommand KeypadBackspaceCommand { get; }

    public IRelayCommand KeypadClearCommand { get; }

    public IRelayCommand<CashDenominationEntryViewModel> ApplyDenominationCommand { get; }

    public IRelayCommand ReturnToPosCommand { get; }

    public IEnumerable<CashDenominationEntryViewModel> NoteDenominations => Denominations.Where(item => item.Kind == CashDenominationKind.Note);

    public IEnumerable<CashDenominationEntryViewModel> CoinDenominations => Denominations.Where(item => item.Kind == CashDenominationKind.Coin);

    public decimal NoteSubtotal => NoteDenominations.Sum(item => item.Subtotal);

    public decimal CoinSubtotal => CoinDenominations.Sum(item => item.Subtotal);

    public decimal CountedCashAmount => NoteSubtotal + CoinSubtotal;

    public bool IsCashCountDialogOpen => SelectedCashDenomination is not null;

    public bool CanChangeBusinessDate => !IsBusy && !HasDailyCloseDraft;

    public bool IsHistoryTabSelected => SelectedTabIndex == HistoryTabIndex;

    public bool IsLinklySettlementTabSelected => SelectedTabIndex == LinklySettlementTabIndex;

    public int CashCountDialogQuantity =>
        int.TryParse(KeypadBuffer, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity)
            ? quantity
            : 0;

    public decimal CashCountDialogSubtotal => decimal.Round(
        (SelectedCashDenomination?.Value ?? 0m) * CashCountDialogQuantity,
        2,
        MidpointRounding.AwayFromZero);

    public decimal CashDifference => CountedCashAmount - ExpectedCashAmount;

    public CashDifferenceStatus CashDifferenceStatus => CashDifferenceStatusResolver.From(CashDifference);

    public bool IsBusinessDateToday => BusinessDate == DateTime.Today;

    public bool HasSelectedArchive => SelectedArchive is not null;

    public int DenominationCount => Denominations.Count;

    public int SelectedCashDenominationPosition =>
        SelectedCashDenomination is null ? 0 : Denominations.IndexOf(SelectedCashDenomination) + 1;

    public string BusinessDateText => BusinessDate.ToString("ddd, dd MMM yyyy", CultureInfo.CurrentCulture);

    public DailyCloseArchiveListItemViewModel? SelectedArchive
    {
        get => _selectedArchive;
        set
        {
            if (SetProperty(ref _selectedArchive, value))
            {
                ReprintSelectedArchiveCommand.NotifyCanExecuteChanged();
                _ = ApplySelectedArchiveAsync(value, CancellationToken.None);
            }
        }
    }

    public LocalLinklySettlementRecord? SelectedSettlement
    {
        get => _selectedSettlement;
        set
        {
            if (SetProperty(ref _selectedSettlement, value))
            {
                SettlementReceiptPreviewLines.ReplaceWith(value?.ReceiptTexts ?? []);
                ClearPendingSettlementManualResolution();
                ReprintSelectedSettlementCommand.NotifyCanExecuteChanged();
                PrepareSettlementManualResolutionCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(IsSettlementManualResolutionVisible));
            }
        }
    }

    public bool IsSettlementManualResolutionVisible =>
        CanPrepareSettlementManualResolution(LocalLinklySettlementManualResolution.ConfirmedSucceeded);

    public bool IsSettlementManualResolutionConfirmationVisible =>
        _pendingSettlementManualResolution is not null &&
        IsSettlementManualResolutionVisible;

    public string SettlementManualResolutionPrompt => _pendingSettlementManualResolution switch
    {
        LocalLinklySettlementManualResolution.ConfirmedSucceeded => T(
            "dailyClose.linklySettlement.manual.confirmSucceeded",
            "Confirm that the settlement succeeded at the terminal. This only updates the saved record; it will not send another settlement."),
        LocalLinklySettlementManualResolution.ConfirmedFailed => T(
            "dailyClose.linklySettlement.manual.confirmFailed",
            "Confirm that the settlement failed at the terminal. This only updates the saved record; it will not send another settlement."),
        LocalLinklySettlementManualResolution.ConfirmedNotSubmitted => T(
            "dailyClose.linklySettlement.manual.confirmNotSubmitted",
            "Confirm that the settlement was not submitted. This only updates the saved record; it will not send another settlement."),
        _ => string.Empty
    };

    private DateTime BusinessDate => (SelectedDate ?? DateTime.Today).Date;

    partial void OnSessionChanged(PosSessionState? oldValue, PosSessionState newValue)
    {
        if (newValue.CashierSession is not null)
        {
            _cashierSessionContext.SetCurrent(newValue.CashierSession);
        }

        if (HasDailyCloseDraft && (oldValue is null || !HasSameDraftIdentity(oldValue, newValue)))
        {
            ClearDailyCloseDraft(clearReportSnapshot: true);
            SelectedTabIndex = HistoryTabIndex;
            StatusMessage = T(
                "dailyClose.status.draftIdentityChanged",
                "The daily close draft was discarded because the store, terminal, or cashier changed.");
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        SelectedTabIndex = HistoryTabIndex;
        CloseCashCountDialog();
        IsDiscardDailyCloseDraftConfirmationOpen = false;
        IsCashCountWorkspaceOpen = false;

        if (HasDailyCloseDraft && !IsCurrentDraftScope())
        {
            ClearDailyCloseDraft(clearReportSnapshot: true);
        }

        // 日结页面实例会跨天复用，进入页面时没有未完成草稿就回到当天，
        // 避免客户端隔夜不重启时把今天的点钞存成首次打开那天的日结。
        if (!HasDailyCloseDraft && SelectedDate?.Date != DateTime.Today)
        {
            SelectedDate = DateTime.Today;
        }

        // 每次进入都默认选中最新存档，避免沿用上次选中的旧存档导致重打错单。
        await SelectArchiveAsync(null, cancellationToken);
        await LoadHistoryAsync(cancellationToken);
        try
        {
            await RefreshSettlementsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
        }
    }

    public async Task CreateOrResumeDailyCloseAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        if (HasDailyCloseDraft && IsCurrentDraftScope())
        {
            IsDiscardDailyCloseDraftConfirmationOpen = false;
            IsCashCountWorkspaceOpen = true;
            StatusMessage = T("dailyClose.status.draftResumed", "Daily close draft resumed.");
            await RefreshAutoSettlementHintAsync(cancellationToken);
            return;
        }

        if (HasDailyCloseDraft)
        {
            ClearDailyCloseDraft(clearReportSnapshot: true);
        }

        SelectedTabIndex = HistoryTabIndex;
        IsCashCountWorkspaceOpen = false;
        IsDiscardDailyCloseDraftConfirmationOpen = false;
        ClearCashCountWorkspaceSnapshot();
        IsBusy = true;
        StatusMessage = T("dailyClose.status.draftPreparing", "Preparing a new daily close...");
        var draftPrepared = false;

        try
        {
            var report = await _dailyCloseService.LoadReportAsync(Session, BusinessDate, cancellationToken);
            ApplyReport(report);
            await RefreshArchivesAsync(cancellationToken);

            _dailyCloseDraftScope = CreateDraftScope(Session, BusinessDate);
            HasDailyCloseDraft = true;
            draftPrepared = true;
            await RefreshAutoSettlementHintAsync(cancellationToken);
            StatusMessage = T("dailyClose.status.draftReady", "New daily close ready for cash counting.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ClearDailyCloseDraft(clearReportSnapshot: true);
            SelectedTabIndex = HistoryTabIndex;
            StatusMessage = ex.Message;
        }
        catch (OperationCanceledException)
        {
            ClearDailyCloseDraft(clearReportSnapshot: true);
            SelectedTabIndex = HistoryTabIndex;
            throw;
        }
        finally
        {
            IsBusy = false;
            if (draftPrepared)
            {
                IsCashCountWorkspaceOpen = true;
            }
        }
    }

    private void CloseCashCountWorkspace()
    {
        if (IsBusy || !IsCashCountWorkspaceOpen)
        {
            return;
        }

        CloseCashCountDialog();
        IsDiscardDailyCloseDraftConfirmationOpen = false;
        IsCashCountWorkspaceOpen = false;
        SelectedTabIndex = HistoryTabIndex;
        StatusMessage = T(
            "dailyClose.status.draftPreserved",
            "Daily close draft preserved. Select Continue Daily Close when you are ready.");
    }

    private void RequestDiscardDailyCloseDraft()
    {
        if (IsBusy || !HasDailyCloseDraft || !IsCashCountWorkspaceOpen)
        {
            return;
        }

        CloseCashCountDialog();
        IsDiscardDailyCloseDraftConfirmationOpen = true;
    }

    private void ConfirmDiscardDailyCloseDraft()
    {
        if (IsBusy || !HasDailyCloseDraft || !IsDiscardDailyCloseDraftConfirmationOpen)
        {
            return;
        }

        ClearDailyCloseDraft(clearReportSnapshot: true);
        SelectedTabIndex = HistoryTabIndex;
        StatusMessage = T("dailyClose.status.draftDiscarded", "Daily close draft discarded.");
    }

    private void CancelDiscardDailyCloseDraft()
    {
        if (IsBusy || !IsDiscardDailyCloseDraftConfirmationOpen)
        {
            return;
        }

        IsDiscardDailyCloseDraftConfirmationOpen = false;
    }

    private bool IsCurrentDraftScope()
    {
        return _dailyCloseDraftScope is not null &&
               HasSameDraftIdentity(_dailyCloseDraftScope, Session) &&
               _dailyCloseDraftScope.BusinessDate == BusinessDate;
    }

    private static bool HasSameDraftIdentity(PosSessionState oldValue, PosSessionState newValue)
    {
        return string.Equals(oldValue.StoreCode, newValue.StoreCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(oldValue.DeviceCode, newValue.DeviceCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(oldValue.CashierId, newValue.CashierId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSameDraftIdentity(DailyCloseDraftScope scope, PosSessionState session)
    {
        return string.Equals(scope.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(scope.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(scope.CashierId, session.CashierId, StringComparison.OrdinalIgnoreCase);
    }

    private static DailyCloseDraftScope CreateDraftScope(PosSessionState session, DateTime businessDate)
    {
        return new DailyCloseDraftScope(
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            businessDate.Date);
    }

    private void ClearDailyCloseDraft(bool clearReportSnapshot)
    {
        _dailyCloseDraftScope = null;
        IsDiscardDailyCloseDraftConfirmationOpen = false;
        IsCashCountWorkspaceOpen = false;
        HasDailyCloseDraft = false;
        WillAutoSendLinklySettlement = false;
        ClearCashCounts();

        if (clearReportSnapshot)
        {
            ClearReportSnapshot();
        }
    }

    private void ClearCashCountWorkspaceSnapshot()
    {
        ClearCashCounts();
        ClearReportSnapshot();
    }

    private void ClearReportSnapshot()
    {
        _currentReport = null;
        PaymentSummaries.Clear();
        ExpectedCashAmount = 0m;
        GrossAmount = 0m;
        NetAmount = 0m;
        RefundAmount = 0m;
        ReturnQuantity = 0m;
        TransactionCount = 0;
        SaveAndPrintCommand.NotifyCanExecuteChanged();
    }

    public async Task RefreshSummaryAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = T("dailyClose.status.refreshing", "Refreshing daily close summary...");

        try
        {
            var report = await _dailyCloseService.LoadReportAsync(Session, BusinessDate, cancellationToken);
            ApplyReport(report);
            await RefreshArchivesAsync(cancellationToken);
            StatusMessage = T("dailyClose.status.refreshed", "Daily close summary refreshed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveAndPrintAsync(CancellationToken cancellationToken = default)
    {
        using var authorization = await ViewModelOperationAuthorization.AuthorizeAsync(
            _operationAuthorizationService,
            TryRequirePermission,
            Permissions.PosTerminal.DailyClose.Save,
            "daily-close",
            "save-and-print",
            Session,
            cancellationToken);
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        if (!CanSaveAndPrint())
        {
            return;
        }

        IsBusy = true;
        StatusMessage = T("dailyClose.status.saving", "Saving and printing daily close...");

        var auditRecorded = false;
        var correlation = OperationAuditEvents.CreateCorrelation();
        try
        {
            var archive = await _dailyCloseService.SaveAsync(Session, BusinessDate, BuildCashCounts(), cancellationToken);
            // 以存档金额为准记录应有、实点与差异，避免界面草稿值与实际保存值不一致。
            OperationAuditEvents.RecordDailyCloseSave(
                _operationAuditLogger,
                Session,
                archive.DailyCloseGuid,
                archive.Report.SystemCashAmount,
                archive.CountedCashAmount,
                archive.CashDifference,
                correlation.CorrelationId,
                correlation.TraceId);
            auditRecorded = true;
            ClearDailyCloseDraft(clearReportSnapshot: true);
            SelectedTabIndex = HistoryTabIndex;

            ReceiptPrintResult printResult;
            try
            {
                printResult = await _dailyClosePrintService.PrintAsync(archive, ReceiptPrintReason.Manual, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ConsoleLog.WriteError(
                    "DailyCloseAudit",
                    $"daily close printing failed error={ex.GetType().Name}",
                    new ApplicationLogContext(TraceId: correlation.TraceId),
                    ex);
                printResult = new ReceiptPrintResult(false, ex.Message);
            }

            var dailyCloseStatus = printResult.Succeeded
                ? T("dailyClose.status.savedPrinted", "Daily close saved and sent to printer.")
                : Format(
                    "dailyClose.status.savedPrintFailed",
                    "Daily close saved, but printing failed: {0}",
                    printResult.Message);
            if (await ShouldAutoSettleAfterDailyCloseAsync(correlation.TraceId, cancellationToken))
            {
                // 集成 Linkly 刷卡（未开手动刷卡）时，保存日结即视为收银员确认结算，不再二次弹窗；
                // 结算较慢且结果须让收银员看到，所以留在结算页签而不是回收银台。
                await RefreshArchivesAsync(cancellationToken, archive.DailyCloseGuid);
                SelectedTabIndex = LinklySettlementTabIndex;
                StatusMessage = $"{dailyCloseStatus} {T("dailyClose.linklySettlement.sending", "Sending Linkly settlement...")}";
                var settlementStatus = await RunLinklySettlementAsync(cancellationToken);
                StatusMessage = $"{dailyCloseStatus} {settlementStatus}";
                return;
            }

            if (printResult.Succeeded)
            {
                StatusMessage = dailyCloseStatus;
                if (_returnToPos is null)
                {
                    await RefreshArchivesAsync(cancellationToken, archive.DailyCloseGuid);
                }
                else
                {
                    _returnToPos.Invoke();
                }

                return;
            }

            SelectedTabIndex = HistoryTabIndex;
            await RefreshArchivesAsync(cancellationToken, archive.DailyCloseGuid);
            StatusMessage = dailyCloseStatus;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!auditRecorded)
            {
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.DailyCloseSave,
                    "Failed",
                    Session,
                    reasonCode: "SAVE_FAILED",
                    safeMessage: ex.GetType().Name,
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
            }

            ConsoleLog.WriteError(
                "DailyCloseAudit",
                $"daily close save/print failed error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = T("dailyClose.status.historyLoading", "Loading daily close history...");

        try
        {
            await RefreshArchivesAsync(cancellationToken);
            StatusMessage = Archives.Count == 0
                ? T("dailyClose.status.historyEmpty", "No daily close archives found for this business date.")
                : Format(
                    "dailyClose.status.historyLoaded",
                    "Loaded {0} daily close archive(s).",
                    Archives.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SelectArchiveAsync(
        DailyCloseArchiveListItemViewModel? archive,
        CancellationToken cancellationToken = default)
    {
        if (SetProperty(ref _selectedArchive, archive, nameof(SelectedArchive)))
        {
            ReprintSelectedArchiveCommand.NotifyCanExecuteChanged();
        }

        await ApplySelectedArchiveAsync(archive, cancellationToken);
    }

    private async Task ReprintSelectedArchiveAsync(CancellationToken cancellationToken = default)
    {
        using var authorization = await ViewModelOperationAuthorization.AuthorizeAsync(
            _operationAuthorizationService,
            TryRequirePermission,
            Permissions.PosTerminal.DailyClose.Reprint,
            "daily-close",
            "reprint-archive",
            Session,
            cancellationToken);
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        if (!CanReprintSelectedArchive())
        {
            return;
        }

        IsBusy = true;
        StatusMessage = T("dailyClose.status.reprinting", "Reprinting daily close archive...");

        var correlation = OperationAuditEvents.CreateCorrelation();
        try
        {
            var result = await _dailyClosePrintService.PrintAsync(SelectedArchive!.Archive, ReceiptPrintReason.Reprint, cancellationToken);
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.DailyCloseReprint,
                result.Succeeded ? "Succeeded" : "Failed",
                Session,
                reasonCode: "REPRINT",
                safeMessage: result.Succeeded ? null : result.Message,
                orderGuid: SelectedArchive.Archive.DailyCloseGuid.ToString("D"),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            StatusMessage = result.Succeeded
                ? T("dailyClose.status.reprintPrinted", "Daily close archive sent to printer.")
                : Format(
                    "dailyClose.status.reprintFailed",
                    "Daily close reprint failed: {0}",
                    result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.DailyCloseReprint,
                "Failed",
                Session,
                reasonCode: "REPRINT_EXCEPTION",
                safeMessage: ex.GetType().Name,
                orderGuid: SelectedArchive?.Archive.DailyCloseGuid.ToString("D"),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            ConsoleLog.WriteError(
                "DailyCloseAudit",
                $"daily close reprint failed error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: correlation.TraceId),
                ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SettleAndPrintAsync(CancellationToken cancellationToken = default)
    {
        if (!IsBusy && _linklySettlementService is not null && BusinessDate != DateTime.Today)
        {
            // 页面跨过零点后按钮状态可能过时：明确提示只能结算今天，而不是静默不响应；也不先弹授权。
            StatusMessage = Format(
                "dailyClose.linklySettlement.notToday",
                "Linkly settlement is only available when the business date is today. Change the business date to {0:yyyy-MM-dd} and try again.",
                DateTime.Today);
            SettleAndPrintCommand.NotifyCanExecuteChanged();
            return;
        }

        using var authorization = await ViewModelOperationAuthorization.AuthorizeAsync(
            _operationAuthorizationService,
            TryRequirePermission,
            Permissions.PosTerminal.DailyClose.Save,
            "daily-close",
            "linkly-settlement",
            Session,
            cancellationToken);
        if (authorization is null || !CanSettleAndPrint())
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        // 刷卡机日结会向支付终端提交当日结算，必须由收银员再次明确确认。
        if (_confirmLinklySettlementAsync is null ||
            !await _confirmLinklySettlementAsync(BusinessDate))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = T("dailyClose.linklySettlement.sending", "Sending Linkly settlement...");
        try
        {
            StatusMessage = await RunLinklySettlementAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 手动结算与日结后自动结算共用：调用终端、记审计、刷新列表，返回给收银员看的结果文案。
    // 不负责授权与 IsBusy，由调用方在各自流程里处理。
    private async Task<string> RunLinklySettlementAsync(CancellationToken cancellationToken)
    {
        var correlation = OperationAuditEvents.CreateCorrelation();
        var auditRecorded = false;
        try
        {
            var execution = await _linklySettlementService!.SettleAndPrintAsync(Session, BusinessDate, cancellationToken);
            var settlementSucceeded = execution.Settlement.Status == LocalLinklySettlementStatus.Succeeded &&
                execution.PrintResult?.Succeeded == true;
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.LinklySettlement,
                settlementSucceeded ? "Succeeded" : "Failed",
                Session,
                reasonCode: execution.ResultUnknown
                    ? "RESULT_UNKNOWN"
                    : execution.ReusedFinalEvidence
                        ? "REUSED_FINAL_EVIDENCE"
                        : execution.Settlement.Status == LocalLinklySettlementStatus.Pending
                            ? "SETTLEMENT_BLOCKED"
                            : execution.Settlement.Status == LocalLinklySettlementStatus.Failed
                                ? "SETTLEMENT_FAILED"
                                : execution.PrintResult?.Succeeded == false
                                    ? "PRINT_FAILED"
                                    : "SETTLEMENT_COMPLETED",
                safeMessage: execution.PrintResult?.Succeeded == false ? execution.PrintResult.Message : null,
                orderGuid: execution.Settlement.SettlementGuid.ToString("D"),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            auditRecorded = true;
            await RefreshSettlementsAsync(cancellationToken, execution.Settlement.SettlementGuid);

            return execution.ResultUnknown
                ? T(
                    "dailyClose.linklySettlement.unknown",
                    "Settlement result is unknown. Do not submit it again.")
                : execution.ReusedFinalEvidence
                    ? T(
                        "dailyClose.linklySettlement.reusedFinalEvidence",
                        "The existing final settlement record was retained. Reprint it manually if needed.")
                : execution.Settlement.Status switch
            {
                LocalLinklySettlementStatus.Pending => T(
                    "dailyClose.linklySettlement.blocked",
                    "An unresolved Linkly settlement already exists for this business date."),
                LocalLinklySettlementStatus.Unknown => T(
                    "dailyClose.linklySettlement.unknown",
                    "Settlement result is unknown. Do not submit it again."),
                LocalLinklySettlementStatus.Failed when IsPinpadOfflineSettlement(execution.Settlement) => T(
                    "dailyClose.linklySettlement.pinpadOffline",
                    "The card terminal (PINpad) is offline, so the Linkly settlement was not sent. Check the terminal connection, then press Settle & Print on the Linkly Settlement tab to retry."),
                LocalLinklySettlementStatus.Failed when execution.Settlement.ReceiptTexts.Count == 0 => Format(
                    "dailyClose.linklySettlement.failedNoReceipt",
                    "Settlement failed: {0}",
                    execution.Settlement.ResponseText ?? "No bank settlement receipt is available."),
                LocalLinklySettlementStatus.Failed when execution.PrintResult?.Succeeded == true => Format(
                    "dailyClose.linklySettlement.failedReceiptPrinted",
                    "Settlement failed, but the bank response receipt was printed: {0}",
                    execution.Settlement.ResponseText ?? "No response detail."),
                LocalLinklySettlementStatus.Failed => Format(
                    "dailyClose.linklySettlement.failedPrintFailed",
                    "Settlement failed and its receipt could not be printed: {0}",
                    execution.PrintResult?.Message ?? "Unknown printer error."),
                _ when execution.Settlement.ReceiptTexts.Count == 0 => T(
                    "dailyClose.linklySettlement.noReceipt",
                    "Settlement result saved. No bank settlement receipt is available to print."),
                _ when execution.PrintResult?.Succeeded == true => T(
                    "dailyClose.linklySettlement.succeededPrinted",
                    "Settlement saved and sent to the POS printer."),
                _ => Format(
                    "dailyClose.linklySettlement.succeededPrintFailed",
                    "Settlement saved, but printing failed: {0}",
                    execution.PrintResult?.Message ?? "Unknown printer error.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (!auditRecorded)
            {
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.LinklySettlement,
                    "Failed",
                    Session,
                    reasonCode: "SETTLEMENT_EXCEPTION",
                    safeMessage: ex.GetType().Name,
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
            }
            return ex.Message;
        }
    }

    private async Task LoadSettlementHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || _linklySettlementService is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = T("dailyClose.linklySettlement.historyLoading", "Loading Linkly settlement history...");
        try
        {
            await RefreshSettlementsAsync(cancellationToken);
            StatusMessage = Settlements.Count == 0
                ? T("dailyClose.linklySettlement.historyEmpty", "No Linkly settlement records found for this business date.")
                : Format(
                    "dailyClose.linklySettlement.historyLoaded",
                    "Loaded {0} Linkly settlement record(s).",
                    Settlements.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReprintSelectedSettlementAsync(CancellationToken cancellationToken = default)
    {
        using var authorization = await ViewModelOperationAuthorization.AuthorizeAsync(
            _operationAuthorizationService,
            TryRequirePermission,
            Permissions.PosTerminal.DailyClose.Reprint,
            "daily-close",
            "reprint-linkly-settlement",
            Session,
            cancellationToken);
        if (authorization is null || !CanReprintSelectedSettlement())
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        IsBusy = true;
        StatusMessage = T("dailyClose.linklySettlement.reprinting", "Reprinting Linkly settlement receipt...");
        var correlation = OperationAuditEvents.CreateCorrelation();
        var auditRecorded = false;
        try
        {
            var result = await _linklySettlementService!.ReprintAsync(SelectedSettlement!, cancellationToken);
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.LinklySettlementReprint,
                result.Succeeded ? "Succeeded" : "Failed",
                Session,
                reasonCode: "REPRINT",
                safeMessage: result.Succeeded ? null : result.Message,
                orderGuid: SelectedSettlement!.SettlementGuid.ToString("D"),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            auditRecorded = true;
            await RefreshSettlementsAsync(cancellationToken, SelectedSettlement!.SettlementGuid);
            StatusMessage = result.Succeeded
                ? T("dailyClose.linklySettlement.reprintPrinted", "Linkly settlement receipt sent to the POS printer.")
                : Format(
                    "dailyClose.linklySettlement.reprintFailed",
                    "Linkly settlement reprint failed: {0}",
                    result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!auditRecorded)
            {
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.LinklySettlementReprint,
                    "Failed",
                    Session,
                    reasonCode: "REPRINT_EXCEPTION",
                    safeMessage: ex.GetType().Name,
                    orderGuid: SelectedSettlement?.SettlementGuid.ToString("D"),
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
            }
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void PrepareSettlementManualResolution(LocalLinklySettlementManualResolution resolution)
    {
        if (!CanPrepareSettlementManualResolution(resolution))
        {
            return;
        }

        _pendingSettlementManualResolution = resolution;
        OnPropertyChanged(nameof(IsSettlementManualResolutionConfirmationVisible));
        OnPropertyChanged(nameof(SettlementManualResolutionPrompt));
        ConfirmSettlementManualResolutionCommand.NotifyCanExecuteChanged();
        CancelSettlementManualResolutionCommand.NotifyCanExecuteChanged();
        StatusMessage = T(
            "dailyClose.linklySettlement.manual.confirmRequired",
            "Review the supervisor decision, then confirm it. This will not send another settlement.");
    }

    private async Task ConfirmSettlementManualResolutionAsync(CancellationToken cancellationToken = default)
    {
        if (!CanConfirmSettlementManualResolution())
        {
            return;
        }

        using var authorization = await ViewModelOperationAuthorization.AuthorizeAsync(
            _operationAuthorizationService,
            TryRequirePermission,
            Permissions.PosTerminal.DailyClose.Save,
            "daily-close",
            "resolve-linkly-settlement",
            Session,
            cancellationToken);
        if (authorization is null || !CanConfirmSettlementManualResolution())
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        var resolution = _pendingSettlementManualResolution!.Value;
        var settlement = SelectedSettlement!;
        IsBusy = true;
        var correlation = OperationAuditEvents.CreateCorrelation();
        var auditRecorded = false;
        try
        {
            var result = await _linklySettlementService!.ResolveUncertainAsync(
                Session,
                settlement,
                resolution,
                cancellationToken);
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                OperationAuditTypes.LinklySettlement,
                result.Resolved ? "Succeeded" : "Failed",
                Session,
                reasonCode: resolution.ToString(),
                safeMessage: result.Resolved ? null : result.Message,
                orderGuid: settlement.SettlementGuid.ToString("D"),
                correlationId: correlation.CorrelationId,
                traceId: correlation.TraceId);
            auditRecorded = true;
            await RefreshSettlementsAsync(cancellationToken, settlement.SettlementGuid);
            if (result.Resolved)
            {
                ClearPendingSettlementManualResolution();
            }

            StatusMessage = result.Resolved
                ? T(
                    "dailyClose.linklySettlement.manual.resolved",
                    "Supervisor decision saved and queued for upload. No new settlement was sent.")
                : result.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!auditRecorded)
            {
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.LinklySettlement,
                    "Failed",
                    Session,
                    reasonCode: resolution.ToString(),
                    safeMessage: ex.GetType().Name,
                    orderGuid: settlement.SettlementGuid.ToString("D"),
                    correlationId: correlation.CorrelationId,
                    traceId: correlation.TraceId);
            }
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CancelSettlementManualResolution()
    {
        if (!CanCancelSettlementManualResolution())
        {
            return;
        }

        ClearPendingSettlementManualResolution();
        StatusMessage = T(
            "dailyClose.linklySettlement.manual.cancelled",
            "Supervisor decision was not saved.");
    }

    partial void OnSelectedDateChanged(DateTime? value)
    {
        ClearDailyCloseDraft(clearReportSnapshot: true);
        Archives.Clear();
        SelectedArchive = null;
        ArchivePreviewRows.Clear();
        SelectedArchiveNoteCounts.Clear();
        SelectedArchiveCoinCounts.Clear();
        Settlements.Clear();
        SelectedSettlement = null;
        ClearPendingSettlementManualResolution();
        SettlementReceiptPreviewLines.Clear();
        OnPropertyChanged(nameof(BusinessDateText));
        OnPropertyChanged(nameof(IsBusinessDateToday));
        StatusMessage = Format(
            "dailyClose.status.dateChanged",
            "Switched to {0:yyyy-MM-dd}. Refresh history or create a new daily close.",
            BusinessDate);
        SaveAndPrintCommand.NotifyCanExecuteChanged();
        SettleAndPrintCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsHistoryTabSelected));
        OnPropertyChanged(nameof(IsLinklySettlementTabSelected));
        // “是否今天”随时间变化，切换页签时重新评估结算按钮，避免跨零点后仍显示可点。
        SettleAndPrintCommand?.NotifyCanExecuteChanged();
    }

    partial void OnKeypadBufferChanged(string value)
    {
        KeypadBackspaceCommand.NotifyCanExecuteChanged();
        KeypadClearCommand.NotifyCanExecuteChanged();
        ApplyDenominationCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CashCountDialogQuantity));
        OnPropertyChanged(nameof(CashCountDialogSubtotal));
    }

    partial void OnSelectedCashDenominationChanged(
        CashDenominationEntryViewModel? oldValue,
        CashDenominationEntryViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        OnPropertyChanged(nameof(IsCashCountDialogOpen));
        OnPropertyChanged(nameof(SelectedCashDenominationPosition));
        OnPropertyChanged(nameof(CashCountDialogQuantity));
        OnPropertyChanged(nameof(CashCountDialogSubtotal));
        OpenCashCountDialogCommand.NotifyCanExecuteChanged();
        CancelCashCountDialogCommand.NotifyCanExecuteChanged();
        KeypadInputCommand.NotifyCanExecuteChanged();
        KeypadBackspaceCommand.NotifyCanExecuteChanged();
        KeypadClearCommand.NotifyCanExecuteChanged();
        ApplyDenominationCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanChangeBusinessDate));
        RefreshSummaryCommand.NotifyCanExecuteChanged();
        SaveAndPrintCommand.NotifyCanExecuteChanged();
        LoadHistoryCommand.NotifyCanExecuteChanged();
        CreateOrResumeDailyCloseCommand.NotifyCanExecuteChanged();
        CloseCashCountWorkspaceCommand.NotifyCanExecuteChanged();
        RequestDiscardDailyCloseDraftCommand.NotifyCanExecuteChanged();
        ConfirmDiscardDailyCloseDraftCommand.NotifyCanExecuteChanged();
        CancelDiscardDailyCloseDraftCommand.NotifyCanExecuteChanged();
        ReprintSelectedArchiveCommand.NotifyCanExecuteChanged();
        SettleAndPrintCommand.NotifyCanExecuteChanged();
        LoadSettlementHistoryCommand.NotifyCanExecuteChanged();
        ReprintSelectedSettlementCommand.NotifyCanExecuteChanged();
        PrepareSettlementManualResolutionCommand.NotifyCanExecuteChanged();
        ConfirmSettlementManualResolutionCommand.NotifyCanExecuteChanged();
        CancelSettlementManualResolutionCommand.NotifyCanExecuteChanged();
        OpenCashCountDialogCommand.NotifyCanExecuteChanged();
        CancelCashCountDialogCommand.NotifyCanExecuteChanged();
        KeypadInputCommand.NotifyCanExecuteChanged();
        KeypadBackspaceCommand.NotifyCanExecuteChanged();
        KeypadClearCommand.NotifyCanExecuteChanged();
        ApplyDenominationCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasDailyCloseDraftChanged(bool value)
    {
        OnPropertyChanged(nameof(CanChangeBusinessDate));
        SaveAndPrintCommand.NotifyCanExecuteChanged();
        RequestDiscardDailyCloseDraftCommand.NotifyCanExecuteChanged();
        ConfirmDiscardDailyCloseDraftCommand.NotifyCanExecuteChanged();
        OpenCashCountDialogCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCashCountWorkspaceOpenChanged(bool value)
    {
        if (!value)
        {
            CloseCashCountDialog();
            IsDiscardDailyCloseDraftConfirmationOpen = false;
        }
        else if (HasDailyCloseDraft && SelectedCashDenomination is null && Denominations.Count > 0)
        {
            // 数字键盘常驻在工作区内，打开时直接从第一个面额开始清点。
            SelectCashDenomination(Denominations[0]);
        }

        CloseCashCountWorkspaceCommand.NotifyCanExecuteChanged();
        RequestDiscardDailyCloseDraftCommand.NotifyCanExecuteChanged();
        OpenCashCountDialogCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDiscardDailyCloseDraftConfirmationOpenChanged(bool value)
    {
        RequestDiscardDailyCloseDraftCommand.NotifyCanExecuteChanged();
        ConfirmDiscardDailyCloseDraftCommand.NotifyCanExecuteChanged();
        CancelDiscardDailyCloseDraftCommand.NotifyCanExecuteChanged();
    }

    partial void OnExpectedCashAmountChanged(decimal value)
    {
        OnPropertyChanged(nameof(CashDifference));
        OnPropertyChanged(nameof(CashDifferenceStatus));
    }

    private void ApplyReport(DailyCloseReport report)
    {
        _currentReport = report;
        PaymentSummaries.ReplaceWith(report.PaymentSummaries.Select(summary => new DailyClosePaymentSummaryItemViewModel(
            summary.MethodLabel,
            summary.SalesAmount,
            summary.RefundAmount,
            summary.NetAmount,
            summary.TransactionCount)));
        ExpectedCashAmount = report.SystemCashAmount;
        GrossAmount = report.SalesAmount;
        NetAmount = report.NetAmount;
        RefundAmount = report.RefundAmount;
        ReturnQuantity = report.ReturnQuantity;
        TransactionCount = report.OrderCount;
        RaiseCashTotalsChanged();
    }

    private async Task RefreshArchivesAsync(
        CancellationToken cancellationToken,
        Guid? preferredArchiveGuid = null)
    {
        var selectedArchiveGuid = preferredArchiveGuid ?? SelectedArchive?.DailyCloseGuid;
        var archives = await _dailyCloseService.GetArchivesAsync(Session, BusinessDate, cancellationToken);
        var items = archives.Select(archive => new DailyCloseArchiveListItemViewModel(archive)).ToList();
        Archives.ReplaceWith(items);

        var selected = items.FirstOrDefault(item => item.DailyCloseGuid == selectedArchiveGuid) ?? items.FirstOrDefault();
        await SelectArchiveAsync(selected, cancellationToken);
    }

    private async Task RefreshSettlementsAsync(
        CancellationToken cancellationToken,
        Guid? preferredSettlementGuid = null)
    {
        if (_linklySettlementService is null)
        {
            Settlements.Clear();
            SelectedSettlement = null;
            SettleAndPrintCommand.NotifyCanExecuteChanged();
            return;
        }

        var selectedSettlementGuid = preferredSettlementGuid ?? SelectedSettlement?.SettlementGuid;
        var settlements = await _linklySettlementService.GetHistoryAsync(Session, BusinessDate, cancellationToken);
        Settlements.ReplaceWith(settlements);
        SelectedSettlement = settlements.FirstOrDefault(item => item.SettlementGuid == selectedSettlementGuid) ?? settlements.FirstOrDefault();
        SettleAndPrintCommand.NotifyCanExecuteChanged();
    }

    private bool CanSaveAndPrint()
    {
        return !IsBusy &&
               HasDailyCloseDraft &&
               IsCurrentDraftScope() &&
               _currentReport is not null;
    }

    private bool CanReprintSelectedArchive()
    {
        return !IsBusy && SelectedArchive is not null;
    }

    private static bool IsPinpadOfflineSettlement(LocalLinklySettlementRecord settlement)
    {
        // EFT-Client 在刷卡机离线时立即回 PF、无小票，结算没有到达银行；原文 "PINpad Offline" 只保留在记录里作证据，
        // 界面改为中英文说明和下一步操作。带小票的 PF 不归为离线，仍显示银行原文。
        return string.Equals(settlement.ResponseCode?.Trim(), "PF", StringComparison.OrdinalIgnoreCase) &&
            settlement.ReceiptTexts.Count == 0;
    }

    private async Task RefreshAutoSettlementHintAsync(CancellationToken cancellationToken)
    {
        // 只是提示；保存时会按当时的设置与结算记录重新判断，以保存那一刻为准。
        WillAutoSendLinklySettlement = await ShouldAutoSettleAfterDailyCloseAsync(traceId: null, cancellationToken);
    }

    private async Task<bool> ShouldAutoSettleAfterDailyCloseAsync(string? traceId, CancellationToken cancellationToken)
    {
        if (_linklySettlementService is null)
        {
            return false;
        }

        try
        {
            return await _linklySettlementService.ShouldAutoSettleAfterDailyCloseAsync(Session, BusinessDate, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 读设置失败不能影响已保存的日结；跳过自动结算，收银员仍可在结算页签手动结算。
            ConsoleLog.WriteError(
                "DailyCloseAudit",
                $"linkly auto settlement check failed error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: traceId),
                ex);
            return false;
        }
    }

    private bool CanSettleAndPrint()
    {
        return !IsBusy &&
               _linklySettlementService is not null &&
               BusinessDate == DateTime.Today;
    }

    private bool CanReprintSelectedSettlement()
    {
        return !IsBusy &&
               _linklySettlementService is not null &&
               SelectedSettlement is { ReceiptTexts.Count: > 0 };
    }

    private bool CanPrepareSettlementManualResolution(LocalLinklySettlementManualResolution resolution)
    {
        _ = resolution;
        return !IsBusy &&
            _linklySettlementService is not null &&
            IsManualSettlementResolutionEligible(SelectedSettlement);
    }

    private bool CanConfirmSettlementManualResolution()
    {
        return _pendingSettlementManualResolution is not null &&
            CanPrepareSettlementManualResolution(_pendingSettlementManualResolution.Value);
    }

    private bool CanCancelSettlementManualResolution()
    {
        return !IsBusy && _pendingSettlementManualResolution is not null;
    }

    private void ClearPendingSettlementManualResolution()
    {
        if (_pendingSettlementManualResolution is null)
        {
            return;
        }

        _pendingSettlementManualResolution = null;
        OnPropertyChanged(nameof(IsSettlementManualResolutionConfirmationVisible));
        OnPropertyChanged(nameof(SettlementManualResolutionPrompt));
        ConfirmSettlementManualResolutionCommand?.NotifyCanExecuteChanged();
        CancelSettlementManualResolutionCommand?.NotifyCanExecuteChanged();
    }

    private static bool IsManualSettlementResolutionEligible(LocalLinklySettlementRecord? settlement)
    {
        return settlement is not null &&
            settlement.Status is LocalLinklySettlementStatus.Pending or LocalLinklySettlementStatus.Unknown &&
            (string.Equals(settlement.ConnectionMode, LinklyConnectionMode.LocalIp.ToString(), StringComparison.Ordinal) ||
             string.Equals(settlement.ConnectionMode, LinklyConnectionMode.CloudDirectSync.ToString(), StringComparison.Ordinal));
    }

    private async Task ApplySelectedArchiveAsync(
        DailyCloseArchiveListItemViewModel? selectedArchive,
        CancellationToken cancellationToken)
    {
        var previewVersion = Interlocked.Increment(ref _archivePreviewVersion);
        OnPropertyChanged(nameof(HasSelectedArchive));
        ArchivePreviewRows.Clear();
        SelectedArchiveNoteCounts.Clear();
        SelectedArchiveCoinCounts.Clear();

        if (selectedArchive is null)
        {
            ReprintSelectedArchiveCommand.NotifyCanExecuteChanged();
            return;
        }

        var normalizedCounts = NormalizeCashCounts(selectedArchive.Archive.CashCounts);
        SelectedArchiveNoteCounts.ReplaceWith(normalizedCounts.Where(count => count.Kind == CashDenominationKind.Note));
        SelectedArchiveCoinCounts.ReplaceWith(normalizedCounts.Where(count => count.Kind == CashDenominationKind.Coin));

        try
        {
            var document = await _dailyClosePrintService.BuildDocumentAsync(selectedArchive.Archive, ReceiptPrintReason.Reprint, cancellationToken);
            if (previewVersion != _archivePreviewVersion)
            {
                return;
            }

            ArchivePreviewRows.ReplaceWith(document.PreviewRows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (previewVersion == _archivePreviewVersion)
            {
                StatusMessage = ex.Message;
            }
        }
        finally
        {
            ReprintSelectedArchiveCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanOpenCashCountDialog(CashDenominationEntryViewModel? denomination)
    {
        // 键盘常驻后点另一个面额即切换，未应用的输入随之放弃。
        return !IsBusy &&
               HasDailyCloseDraft &&
               IsCashCountWorkspaceOpen &&
               denomination is not null;
    }

    private void OpenCashCountDialog(CashDenominationEntryViewModel? denomination)
    {
        if (!CanOpenCashCountDialog(denomination))
        {
            return;
        }

        SelectCashDenomination(denomination!);
    }

    private void SelectCashDenomination(CashDenominationEntryViewModel denomination)
    {
        SelectedCashDenomination = denomination;
        KeypadBuffer = denomination.Count == 0
            ? string.Empty
            : denomination.Count.ToString(CultureInfo.InvariantCulture);
        _replaceKeypadOnNextInput = true;
    }

    private void CancelCashCountDialog()
    {
        if (IsBusy)
        {
            return;
        }

        CloseCashCountDialog();
    }

    private void CloseCashCountDialog()
    {
        _replaceKeypadOnNextInput = false;
        KeypadBuffer = string.Empty;
        SelectedCashDenomination = null;
    }

    private void AppendKeypadInput(string? input)
    {
        if (IsBusy || !IsCashCountDialogOpen || string.IsNullOrWhiteSpace(input) || !input.All(char.IsDigit))
        {
            return;
        }

        var nextBuffer = _replaceKeypadOnNextInput || KeypadBuffer == "0"
            ? input
            : KeypadBuffer + input;
        if (nextBuffer.Length > MaxCashCountDigits)
        {
            return;
        }

        KeypadBuffer = nextBuffer;
        _replaceKeypadOnNextInput = false;
    }

    private void BackspaceKeypad()
    {
        if (IsBusy || !IsCashCountDialogOpen)
        {
            return;
        }

        _replaceKeypadOnNextInput = false;
        if (KeypadBuffer.Length > 0)
        {
            KeypadBuffer = KeypadBuffer[..^1];
        }
    }

    private void ClearKeypad()
    {
        if (IsBusy || !IsCashCountDialogOpen)
        {
            return;
        }

        _replaceKeypadOnNextInput = false;
        KeypadBuffer = string.Empty;
    }

    private bool CanApplyDenominationCount(CashDenominationEntryViewModel? denomination)
    {
        var target = denomination ?? SelectedCashDenomination;
        return !IsBusy &&
               SelectedCashDenomination is not null &&
               ReferenceEquals(target, SelectedCashDenomination) &&
               int.TryParse(KeypadBuffer, NumberStyles.None, CultureInfo.InvariantCulture, out var count) &&
               count >= 0;
    }

    private void ApplyDenominationCount(CashDenominationEntryViewModel? denomination)
    {
        var target = denomination ?? SelectedCashDenomination;
        if (IsBusy ||
            SelectedCashDenomination is null ||
            !ReferenceEquals(target, SelectedCashDenomination) ||
            !int.TryParse(KeypadBuffer, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
            count < 0)
        {
            return;
        }

        target.Count = count;
        RaiseCashTotalsChanged();

        // 应用后自动进入下一面额；最后一个面额应用完即结束选择。
        var nextIndex = Denominations.IndexOf(target) + 1;
        if (nextIndex > 0 && nextIndex < Denominations.Count)
        {
            SelectCashDenomination(Denominations[nextIndex]);
        }
        else
        {
            CloseCashCountDialog();
        }
    }

    private IReadOnlyList<CashDenominationCount> BuildCashCounts()
    {
        return Denominations
            .Select(item => new CashDenominationCount(item.Value, item.Label, item.Kind, item.Count))
            .ToList();
    }

    private static IReadOnlyList<CashDenominationCount> NormalizeCashCounts(IReadOnlyList<CashDenominationCount> cashCounts)
    {
        return DailyCloseService.AustralianDenominations
            .Select(denomination =>
            {
                var count = cashCounts.FirstOrDefault(item => item.Kind == denomination.Kind && item.Value == denomination.Value);
                return count ?? new CashDenominationCount(denomination.Value, denomination.Label, denomination.Kind, 0);
            })
            .ToList();
    }

    private void ClearCashCounts()
    {
        foreach (var denomination in Denominations)
        {
            denomination.Count = 0;
        }

        CloseCashCountDialog();
        RaiseCashTotalsChanged();
    }

    private void OnDenominationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CashDenominationEntryViewModel.Subtotal))
        {
            RaiseCashTotalsChanged();
        }
    }

    private void RaiseCashTotalsChanged()
    {
        OnPropertyChanged(nameof(NoteSubtotal));
        OnPropertyChanged(nameof(CoinSubtotal));
        OnPropertyChanged(nameof(CountedCashAmount));
        OnPropertyChanged(nameof(CashDifference));
        OnPropertyChanged(nameof(CashDifferenceStatus));
        SaveAndPrintCommand.NotifyCanExecuteChanged();
    }

    private string T(string key, string fallback)
    {
        return _localization?.T(key) ?? fallback;
    }

    private string Format(string key, string fallback, params object[] args)
    {
        return string.Format(
            _localization?.CurrentCulture ?? CultureInfo.CurrentCulture,
            _localization?.T(key) ?? fallback,
            args);
    }

    private bool TryRequirePermission(string permissionCode)
    {
        if ((!_enforcePermissions && _cashierSessionContext.CurrentSession is null && Session.CashierSession is null) ||
            _cashierSessionContext.RequirePermission(permissionCode, out var message))
        {
            return true;
        }

        // 中文注释：日结保存会写本地归档，执行前必须再次校验权限。
        var operationType = permissionCode switch
        {
            Permissions.PosTerminal.DailyClose.Save => OperationAuditTypes.DailyCloseSave,
            Permissions.PosTerminal.DailyClose.Reprint => OperationAuditTypes.DailyCloseReprint,
            _ => null
        };
        if (operationType is not null)
        {
            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                operationType,
                "Denied",
                Session,
                reasonCode: "PERMISSION_DENIED",
                safeMessage: message);
        }

        StatusMessage = message;
        return false;
    }

    public void Dispose()
    {
        foreach (var entry in Denominations)
        {
            entry.PropertyChanged -= OnDenominationChanged;
        }

        Denominations.Clear();
    }

    private sealed record DailyCloseDraftScope(
        string StoreCode,
        string DeviceCode,
        string CashierId,
        DateTime BusinessDate);
}

public sealed partial class CashDenominationEntryViewModel : ObservableObject
{
    public CashDenominationEntryViewModel(decimal value, string label, CashDenominationKind kind)
    {
        Value = value;
        Label = label;
        Kind = kind;
    }

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isSelected;

    public decimal Value { get; }

    public string Label { get; }

    public CashDenominationKind Kind { get; }

    public bool IsCoin => Kind == CashDenominationKind.Coin;

    public decimal Amount => Value;

    public decimal Subtotal => decimal.Round(Value * Count, 2, MidpointRounding.AwayFromZero);

    partial void OnCountChanged(int value)
    {
        OnPropertyChanged(nameof(Subtotal));
    }
}

public sealed record DailyClosePaymentSummaryItemViewModel(
    string Label,
    decimal SalesAmount,
    decimal RefundAmount,
    decimal NetAmount,
    int TransactionCount);

public sealed record DailyCloseArchiveListItemViewModel(DailyCloseArchive Archive)
{
    public Guid DailyCloseGuid => Archive.DailyCloseGuid;

    public DateTimeOffset SavedAt => Archive.SavedAt;

    public string OperatorName => Archive.Report.CashierName;

    public decimal CountedCashAmount => Archive.CountedCashAmount;

    public decimal CashDifference => Archive.CashDifference;

    public string ClosedAtDisplay => SavedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public string ClosedTimeDisplay => SavedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    public string SavedAtDisplay => Archive.SavedAtDisplay;

    public string ShortArchiveId => Archive.ShortArchiveId;

    public decimal ExpectedCashAmount => Archive.Report.SystemCashAmount;

    public decimal NetAmount => Archive.Report.NetAmount;

    public int OrderCount => Archive.Report.OrderCount;

    public CashDifferenceStatus DifferenceStatus => CashDifferenceStatusResolver.From(CashDifference);
}

public enum CashDifferenceStatus
{
    Balanced,
    Over,
    Short
}

public static class CashDifferenceStatusResolver
{
    // 现金差额 = 实盘 - 应有：负数为短款，正数为长款。
    public static CashDifferenceStatus From(decimal cashDifference) => cashDifference switch
    {
        < 0m => CashDifferenceStatus.Short,
        > 0m => CashDifferenceStatus.Over,
        _ => CashDifferenceStatus.Balanced
    };
}
