using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>
/// 恢复中心列表的纯展示行。原始队列快照仍由 <see cref="CardRecoveryCenterViewModel.OpenAttempts" />
/// 保留用于定点操作，界面只绑定这里的本地化文本，避免把 provider/数据库枚举直接显示给收银员。
/// </summary>
public sealed class CardRecoveryQueueRowViewModel
{
    public CardRecoveryQueueRowViewModel(
        CardRecoveryQueueItem source,
        string operationTypeText,
        string channelText,
        string updatedAtText,
        string amountText,
        string statusText,
        string? statusPillText = null,
        CardRecoveryTone tone = CardRecoveryTone.Neutral,
        string? metaText = null)
    {
        Source = source;
        OperationTypeText = operationTypeText;
        ChannelText = channelText;
        UpdatedAtText = updatedAtText;
        AmountText = amountText;
        StatusText = statusText;
        StatusPillText = statusPillText ?? statusText;
        Tone = tone;
        MetaText = metaText ?? updatedAtText;
    }

    /// <summary>列表卡片上的状态标签；Square 退款已受理时显示“Square 处理中”，其余与 <see cref="StatusText" /> 相同。</summary>
    public string StatusPillText { get; }

    public CardRecoveryTone Tone { get; }

    /// <summary>卡片第二行：渠道 · 单号尾号 · 时间。</summary>
    public string MetaText { get; }

    public bool IsRefund => string.Equals(Source.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase);

    public CardRecoveryQueueItem Source { get; }

    public CardRecoveryAttemptKey Key => Source.Key;

    public string OperationTypeText { get; }

    public string ChannelText { get; }

    public string UpdatedAtText { get; }

    public string AmountText { get; }

    public string StatusText { get; }

    public string OrderText => CardRecoveryCenterViewModel.OrderReference(Source);
}

public sealed partial class CardRecoveryCenterViewModel : ObservableObject, IDisposable
{
    private const string ScreenName = "card-recovery-center";
    private static readonly JsonSerializerOptions DraftJsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly ICardPaymentRecoveryService _recoveryService;
    private readonly PosCartService _cart;
    private readonly PosSessionState _session;
    private readonly IOperationAuthorizationService _authorizationService;
    private readonly ILocalizationService? _localization;
    private readonly Action? _back;
    private readonly Action<int>? _openCountChanged;
    private readonly Func<CardRecoveryAttemptKey, CardPaymentRecoveryResult, Task>? _recoveryResultHandledAsync;
    private Task? _initialLoadTask;
    private CardRecoveryQueueItem? _selectedAttempt;
    private CardRecoveryQueueRowViewModel? _selectedRow;
    private bool _isBusy;
    private string _resolutionReason = string.Empty;
    private string _resolutionEvidence = string.Empty;
    private string _resolutionReference = string.Empty;
    private string _statusMessage = string.Empty;
    private IReadOnlyList<PosCartLineSnapshot> _selectedProductLines = [];
    private bool _isRebuildingRows;

    public CardRecoveryCenterViewModel(
        ICardPaymentRecoveryService recoveryService,
        PosCartService cart,
        PosSessionState session,
        IOperationAuthorizationService authorizationService,
        ILocalizationService? localization = null,
        Action? back = null,
        Action<int>? openCountChanged = null,
        Func<CardPaymentRecoveryResult, Task>? recoveryResultHandledAsync = null)
        : this(
            AdaptLegacyRecoveryResultHandler(recoveryResultHandledAsync),
            recoveryService,
            cart,
            session,
            authorizationService,
            localization,
            back,
            openCountChanged)
    {
    }

    internal static CardRecoveryCenterViewModel CreateWithKeyedRecoveryResultHandler(
        ICardPaymentRecoveryService recoveryService,
        PosCartService cart,
        PosSessionState session,
        IOperationAuthorizationService authorizationService,
        ILocalizationService? localization = null,
        Action? back = null,
        Action<int>? openCountChanged = null,
        Func<CardRecoveryAttemptKey, CardPaymentRecoveryResult, Task>? recoveryResultHandledAsync = null)
    {
        return new CardRecoveryCenterViewModel(
            recoveryResultHandledAsync,
            recoveryService,
            cart,
            session,
            authorizationService,
            localization,
            back,
            openCountChanged);
    }

    private CardRecoveryCenterViewModel(
        Func<CardRecoveryAttemptKey, CardPaymentRecoveryResult, Task>? recoveryResultHandledAsync,
        ICardPaymentRecoveryService recoveryService,
        PosCartService cart,
        PosSessionState session,
        IOperationAuthorizationService authorizationService,
        ILocalizationService? localization,
        Action? back,
        Action<int>? openCountChanged)
    {
        ArgumentNullException.ThrowIfNull(recoveryService);
        ArgumentNullException.ThrowIfNull(cart);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(authorizationService);

        _recoveryService = recoveryService;
        _cart = cart;
        _session = session;
        _authorizationService = authorizationService;
        _localization = localization;
        _back = back;
        _openCountChanged = openCountChanged;
        _recoveryResultHandledAsync = recoveryResultHandledAsync;
        BackCommand = new RelayCommand(
            () => _back?.Invoke(),
            () => !IsBusy);
        RefreshCommand = new AsyncRelayCommand(
            () => RefreshWithAuthorizationAsync("refresh"),
            () => !IsBusy);
        RecoverCommand = new AsyncRelayCommand(RecoverSelectedAsync, CanOperateOnSelection);
        ConfirmPaidCommand = new AsyncRelayCommand(
            () => ResolveSelectedAsync(
                CardRecoverySupervisorDecision.ConfirmProcessed,
                "resolve/confirm-paid"),
            CanResolveSelection);
        ConfirmNotPaidCommand = new AsyncRelayCommand(
            () => ResolveSelectedAsync(
                CardRecoverySupervisorDecision.ConfirmNotProcessed,
                "resolve/confirm-not-paid"),
            CanResolveSelection);
        ContinueWaitingCommand = new AsyncRelayCommand(
            () => ResolveSelectedAsync(
                CardRecoverySupervisorDecision.ContinueWaiting,
                "resolve/continue-waiting"),
            CanResolveSelection);
        if (_localization is not null)
        {
            _localization.CultureChanged += OnCultureChanged;
        }

        SetStatusResource("cardRecovery.center.status.ready", "Review an open card transaction.");
    }

    private static Func<CardRecoveryAttemptKey, CardPaymentRecoveryResult, Task>? AdaptLegacyRecoveryResultHandler(
        Func<CardPaymentRecoveryResult, Task>? recoveryResultHandledAsync)
    {
        if (recoveryResultHandledAsync is null)
        {
            return null;
        }

        // 公开构造函数保留旧回调契约；主线内部仍携带精确 attempt key 完成安全交接。
        return (_, result) => recoveryResultHandledAsync(result);
    }

    public ObservableCollection<CardRecoveryQueueItem> OpenAttempts { get; } = [];

    public ObservableCollection<CardRecoveryQueueRowViewModel> OpenAttemptRows { get; } = [];

    public CardRecoveryQueueItem? SelectedAttempt
    {
        get => _selectedAttempt;
        set
        {
            var previousKey = _selectedAttempt?.Key;
            if (SetProperty(ref _selectedAttempt, value))
            {
                if (previousKey != value?.Key)
                {
                    IsManualExpanded = false;
                    ResolutionReason = string.Empty;
                    ResolutionEvidence = string.Empty;
                    ResolutionReference = string.Empty;
                }

                var matchingRow = value is null
                    ? null
                    : OpenAttemptRows.FirstOrDefault(row => row.Key == value.Key);
                if (!ReferenceEquals(_selectedRow, matchingRow))
                {
                    _selectedRow = matchingRow;
                    OnPropertyChanged(nameof(SelectedRow));
                }
                UpdateSelectedProductLines(value?.OrderDraftJson);
                NotifySelectedAttemptProperties();
                NotifySelectionCommands();
            }
        }
    }

    public CardRecoveryQueueRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (value is null && _isRebuildingRows)
            {
                // ItemsSource 重建时 WPF TwoWay 绑定会短暂回写 null；保留 provider+AttemptGuid 选择，待重建完成后同步。
                return;
            }

            if (SetProperty(ref _selectedRow, value))
            {
                // ListBox 选择的是展示行，定点恢复/结案仍使用原始快照的 Key。
                if (!ReferenceEquals(_selectedAttempt, value?.Source))
                {
                    SelectedAttempt = value?.Source;
                }
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifySelectionCommands();
                OnPropertyChanged(nameof(AutoCheckText));
            }
        }
    }

    public string ResolutionReason
    {
        get => _resolutionReason;
        set => SetProperty(ref _resolutionReason, value);
    }

    public string ResolutionEvidence
    {
        get => _resolutionEvidence;
        set => SetProperty(ref _resolutionEvidence, value);
    }

    public string ResolutionReference
    {
        get => _resolutionReference;
        set => SetProperty(ref _resolutionReference, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string OpenCountText => string.Format(
        GetCulture(),
        T("cardRecovery.center.openCount", "{0} card transactions need attention"),
        OpenAttempts.Count);

    public bool HasSelection => SelectedAttempt is not null;
    public bool HasNoSelection => !HasSelection;
    public bool HasOpenAttempts => OpenAttempts.Count > 0;
    public bool HasNoOpenAttempts => OpenAttemptRows.Count == 0;
    public bool HasProductSnapshot => SelectedProductLines.Count > 0;
    public bool HasNoProductSnapshot => !HasProductSnapshot;
    public IReadOnlyList<PosCartLineSnapshot> SelectedProductLines => _selectedProductLines;
    public bool IsSquareRefundProcessing =>
        SelectedAttempt is { } attempt && HasSquareRefundPaymentEvidence(attempt);
    public bool CanShowSupervisorResolution =>
        SelectedAttempt is { IsOpen: true } attempt && IsSupervisorResolutionAllowed(attempt);
    public bool CanShowRecoveryOnlyGuidance =>
        SelectedAttempt is { IsOpen: true } attempt &&
        !IsSquareRefundProcessing &&
        !IsSupervisorResolutionAllowed(attempt);
    public string RecoveryOnlyGuidanceMessage => T(
        "cardRecovery.center.recoverOnly",
        "This transaction cannot be manually finalized. Use Recover to retry this saved transaction. Refresh and Back remain available; do not submit another card payment.");
    public string SquareRefundProcessingMessage => T(
        "cardRecovery.center.squareRefund.processing",
        "Square refund is already processing. Use Recover to check the latest status. Do not submit another refund.");
    private bool IsRefundSelection => string.Equals(
        SelectedAttempt?.OperationKind,
        "Refund",
        StringComparison.OrdinalIgnoreCase);
    private bool IsPaymentSelection =>
        string.Equals(SelectedAttempt?.OperationKind, "Sale", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedAttempt?.OperationKind, "ActiveSession", StringComparison.OrdinalIgnoreCase);
    private bool IsSquareRefundSelection =>
        IsRefundSelection && SelectedAttempt?.Processor == CardProcessorKind.Square;
    public string ResolutionSectionTitleText => IsRefundSelection
        ? T("cardRecovery.refund.section.title", "Supervisor refund reconciliation")
        : IsPaymentSelection
            ? T("cardRecovery.payment.section.title", "Supervisor payment reconciliation")
            : T("cardRecovery.center.resolution.title", "Supervisor resolution");
    public string ResolutionInstructionsText => IsSquareRefundSelection
        ? T(
            "cardRecovery.refund.section.squareInstructions",
            "Check the Square refund record before choosing an outcome. Confirm refunded requires a real Square refund reference; confirm not refunded requires bank evidence; continue waiting requires a supervisor note.")
        : IsRefundSelection
            ? T(
                "cardRecovery.refund.section.instructions",
                "Check the bank or terminal record before choosing one outcome. The refund remains locked until a supervisor decision is saved.")
            : IsPaymentSelection
                ? T(
                    "cardRecovery.payment.section.instructions",
                    "Check the bank result before unlocking this payment. Confirming paid requires a reference or evidence; confirming not paid requires evidence. A supervisor note is optional.")
                : T(
                    "cardRecovery.center.resolution.instructions",
                    "Confirm the bank or terminal evidence for this selected transaction. Each manual decision requires one-time supervisor authorization.");
    public string ResolutionReasonLabelText => IsSquareRefundSelection
        ? T(
            "cardRecovery.refund.field.squareNote",
            "Supervisor note (required when continuing to wait)")
        : IsRefundSelection
            ? T(
                "cardRecovery.refund.field.note",
                "Supervisor note (required when waiting; reference or note required when refunded)")
            : IsPaymentSelection
                ? T("cardRecovery.payment.field.note", "Supervisor note (optional)")
                : T("cardRecovery.center.input.reason", "Supervisor reason or note");
    public string ResolutionEvidenceLabelText => IsRefundSelection
        ? T(
            "cardRecovery.refund.field.evidence",
            "Bank evidence (required when no refund was processed)")
        : IsPaymentSelection
            ? T(
                "cardRecovery.payment.field.evidence",
                "Bank evidence (required when confirming not paid)")
            : T("cardRecovery.center.input.evidence", "Bank or terminal evidence");
    public string ResolutionReferenceLabelText => IsSquareRefundSelection
        ? T(
            "cardRecovery.refund.field.squareRefundReference",
            "Square refund reference (required when confirming refunded)")
        : IsRefundSelection
            ? T("cardRecovery.refund.field.refundReference", "Refund reference (when available)")
            : IsPaymentSelection
                ? T("cardRecovery.payment.field.paymentReference", "Payment reference (when available)")
                : T("cardRecovery.center.input.reference", "Payment or settlement reference");
    public string SelectedTypeText => MapOperationType(SelectedAttempt?.OperationKind);
    public string SelectedChannelText => MapChannel(SelectedAttempt?.Processor);
    public string SelectedAmountText => SelectedAttempt is null
        ? NoneText
        : FormatAmount(SelectedAttempt.Amount);
    public string SelectedCashierText => ValueOrNone(SelectedAttempt?.CashierId);
    public string SelectedTimeText => SelectedAttempt is null
        ? NoneText
        : SelectedAttempt.UpdatedAt.ToLocalTime().ToString("g", GetCulture());
    public string SelectedSessionText => ValueOrNone(
        Normalize(SelectedAttempt?.SessionId) ?? Normalize(SelectedAttempt?.CheckoutId));
    public string SelectedTxnText => ValueOrNone(
        Normalize(SelectedAttempt?.TxnRef) ?? Normalize(SelectedAttempt?.PaymentId));
    public string SelectedResponseCodeText => ValueOrNone(SelectedAttempt?.ResponseCode);
    public string SelectedResponseText => ValueOrNone(SelectedAttempt?.ResponseText);
    public string SelectedStatusText => MapStatus(SelectedAttempt?.Status);
    public string SelectedAttemptText => SelectedAttempt?.AttemptGuid.ToString("D") ?? NoneText;
    public string SelectedEnvironmentText => ValueOrNone(SelectedAttempt?.Environment);
    public string SelectedReferenceText => ValueOrNone(
        Normalize(SelectedAttempt?.PaymentReference) ?? Normalize(SelectedAttempt?.PaymentId));

    public IRelayCommand BackCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand RecoverCommand { get; }
    public IAsyncRelayCommand ConfirmPaidCommand { get; }
    public IAsyncRelayCommand ConfirmNotPaidCommand { get; }
    public IAsyncRelayCommand ContinueWaitingCommand { get; }

    public Task LoadAsync() =>
        _initialLoadTask ??= RefreshWithAuthorizationAsync("view");

    private async Task RefreshWithAuthorizationAsync(string action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            using var authorization = await _authorizationService.AuthorizeAsync(
                Permissions.PosTerminal.Payment.View,
                ScreenName,
                action,
                _session);
            if (authorization is null)
            {
                SetStatusResource(
                    "cardRecovery.center.status.authorizationRequired",
                    "Authorization is required.");
                return;
            }

            using var activation = authorization.Activate();
            await RefreshListCoreAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            SetStatusResource(
                "cardRecovery.center.status.refreshFailed",
                "Could not refresh card transactions. {0}",
                ex.Message);
            ConsoleLog.WriteError(
                "CardRecoveryCenter",
                $"refresh failed action={action} error={ex.GetType().Name}",
                exception: ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RecoverSelectedAsync()
    {
        var selected = SelectedAttempt;
        if (selected is null || !selected.IsOpen || IsBusy)
        {
            return;
        }

        var selectedKey = selected.Key;
        IsBusy = true;
        try
        {
            using var authorization = await _authorizationService.AuthorizeAsync(
                Permissions.PosTerminal.Payment.View,
                ScreenName,
                "recover",
                _session);
            if (authorization is null)
            {
                SetStatusResource(
                    "cardRecovery.center.status.authorizationRequired",
                    "Authorization is required.");
                return;
            }

            using var activation = authorization.Activate();
            if (SelectedAttempt?.Key != selectedKey)
            {
                SetStatusResource(
                    "cardRecovery.center.status.selectionChanged",
                    "The selected transaction changed. Select it again before continuing.");
                return;
            }

            var result = await _recoveryService.RecoverAsync(
                selectedKey,
                _cart,
                _session);
            var actionMessage = string.IsNullOrWhiteSpace(result.Message)
                ? T("cardRecovery.center.status.recoverNoResult", "The selected transaction is no longer open.")
                : result.Message;
            await TryRefreshListAfterOperationAsync(actionMessage, "recover");
            if (_recoveryResultHandledAsync is not null)
            {
                Func<Task> callback = () => _recoveryResultHandledAsync(selectedKey, result);
                var context =
                    $"targeted recovery callback processor={selected.Processor} attempt={selected.AttemptGuid:D}";
                if (result.Outcome == CardPaymentRecoveryOutcome.DraftRestored)
                {
                    await RunRecoveryResultHandoffAsync(callback, context);
                }
                else
                {
                    await TryHandleRecoveryResultAsync(selectedKey, result, "recover");
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            SetStatusResource(
                "cardRecovery.center.status.recoverFailed",
                "Could not check the selected transaction. {0}",
                ex.Message);
            ConsoleLog.WriteError(
                "CardRecoveryCenter",
                $"targeted recovery failed processor={selected.Processor} attempt={selected.AttemptGuid:D} error={ex.GetType().Name}",
                exception: ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanOperateOnSelection() => !IsBusy && SelectedAttempt is { IsOpen: true };

    private bool CanResolveSelection() =>
        !IsBusy &&
        SelectedAttempt is { IsOpen: true } attempt &&
        IsSupervisorResolutionAllowed(attempt);

    private static bool IsSupervisorResolutionAllowed(CardRecoveryQueueItem attempt)
    {
        if (string.Equals(
                attempt.Status,
                CardRecoveryPhases.FinalizePending,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isRefund = string.Equals(
            attempt.OperationKind,
            "Refund",
            StringComparison.OrdinalIgnoreCase);
        if (isRefund && HasSquareRefundPaymentEvidence(attempt))
        {
            return false;
        }

        if (attempt.Processor == CardProcessorKind.Linkly)
        {
            if (isRefund)
            {
                return StatusIs(
                    attempt.Status,
                    nameof(LocalCardPaymentAttemptStatus.Recovering),
                    nameof(LocalCardPaymentAttemptStatus.RequiresReview),
                    nameof(LocalCardPaymentAttemptStatus.SessionStarted));
            }

            return StatusIs(
                attempt.Status,
                nameof(LocalCardPaymentAttemptStatus.Pending),
                nameof(LocalCardPaymentAttemptStatus.SessionStarted),
                nameof(LocalCardPaymentAttemptStatus.Recovering),
                nameof(LocalCardPaymentAttemptStatus.RequiresReview));
        }

        if (attempt.Processor != CardProcessorKind.Square)
        {
            return false;
        }

        if (isRefund)
        {
            return StatusIs(
                attempt.Status,
                nameof(LocalSquarePaymentAttemptStatus.Recovering),
                nameof(LocalSquarePaymentAttemptStatus.Unknown),
                nameof(LocalSquarePaymentAttemptStatus.CheckoutCreated));
        }

        return string.Equals(attempt.OperationKind, "Sale", StringComparison.OrdinalIgnoreCase) &&
            StatusIs(
                attempt.Status,
                nameof(LocalSquarePaymentAttemptStatus.Pending),
                nameof(LocalSquarePaymentAttemptStatus.CheckoutCreated),
                nameof(LocalSquarePaymentAttemptStatus.Recovering),
                nameof(LocalSquarePaymentAttemptStatus.CheckoutCompleted),
                nameof(LocalSquarePaymentAttemptStatus.Unknown));
    }

    private static bool StatusIs(string status, params string[] allowed) =>
        allowed.Any(candidate => string.Equals(status, candidate, StringComparison.OrdinalIgnoreCase));

    private static bool HasSquareRefundPaymentEvidence(CardRecoveryQueueItem attempt) =>
        attempt.Processor == CardProcessorKind.Square &&
        string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase) &&
        (Normalize(attempt.PaymentId) is not null || Normalize(attempt.PaymentStatus) is not null);

    private async Task ResolveSelectedAsync(
        CardRecoverySupervisorDecision decision,
        string action)
    {
        var selected = SelectedAttempt;
        if (selected is null || !selected.IsOpen || IsBusy)
        {
            return;
        }

        var selectedKey = selected.Key;
        var reason = Normalize(ResolutionReason) ?? string.Empty;
        var evidence = Normalize(ResolutionEvidence);
        var reference = Normalize(ResolutionReference);
        IsBusy = true;
        try
        {
            var permissionCode = string.Equals(
                selected.OperationKind,
                "Refund",
                StringComparison.OrdinalIgnoreCase)
                ? Permissions.PosTerminal.Returns.Confirm
                : Permissions.PosTerminal.Payment.Confirm;
            using var authorization = await _authorizationService.AuthorizeAsync(
                permissionCode,
                ScreenName,
                action,
                _session);
            if (authorization is null)
            {
                SetStatusResource(
                    "cardRecovery.center.status.authorizationRequired",
                    "Authorization is required.");
                return;
            }

            using var activation = authorization.Activate();
            // 主管扫码期间列表选择可能变化，旧授权不得落到另一笔金融交易。
            if (SelectedAttempt?.Key != selectedKey)
            {
                SetStatusResource(
                    "cardRecovery.center.status.selectionChanged",
                    "The selected transaction changed. Select it again before continuing.");
                return;
            }

            var result = await _recoveryService.ResolveAsync(
                selectedKey,
                decision,
                reason,
                evidence,
                decision == CardRecoverySupervisorDecision.ContinueWaiting ? null : reference,
                _cart,
                _session);
            var actionMessage = string.IsNullOrWhiteSpace(result.Message)
                ? T("cardRecovery.center.status.resolveNoResult", "The resolution returned no message.")
                : result.Message;
            await TryRefreshListAfterOperationAsync(
                actionMessage,
                action,
                preserveActionMessageOnFailure: result.ResolutionPersisted && result.RecoveryResult is null);
            if (result.RecoveryResult is not null && _recoveryResultHandledAsync is not null)
            {
                var recoveryResult = result.RecoveryResult;
                Func<Task> callback = () => _recoveryResultHandledAsync(selectedKey, recoveryResult);
                var context =
                    $"targeted resolution callback processor={selected.Processor} attempt={selected.AttemptGuid:D} decision={decision}";
                if (recoveryResult.Outcome == CardPaymentRecoveryOutcome.DraftRestored)
                {
                    await RunRecoveryResultHandoffAsync(callback, context);
                }
                else
                {
                    await TryHandleRecoveryResultAsync(selectedKey, recoveryResult, action);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            SetStatusResource(
                "cardRecovery.center.status.resolveFailed",
                "Could not save the supervisor decision. {0}",
                ex.Message);
            ConsoleLog.WriteError(
                "CardRecoveryCenter",
                $"targeted resolution failed processor={selected.Processor} attempt={selected.AttemptGuid:D} decision={decision} error={ex.GetType().Name}",
                exception: ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TryRefreshListAfterOperationAsync(
        string actionMessage,
        string action,
        bool preserveActionMessageOnFailure = false)
    {
        try
        {
            await RefreshListCoreAsync(actionMessage);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 金融结果已经确定；刷新失败只显示队列告警，并继续交付原结果。
            try
            {
                if (preserveActionMessageOnFailure)
                {
                    SetLiteralStatus(actionMessage);
                }
                else
                {
                    SetStatusResource(
                        "cardRecovery.center.status.refreshFailed",
                        "Could not refresh card transactions. {0}",
                        ex.Message);
                }
            }
            catch (Exception statusException) when (
                statusException is not OutOfMemoryException and not StackOverflowException)
            {
                // UI 通知失败不能覆盖已确定的金融结果。
            }

            TryWritePostCommitWarning(
                $"refresh failed action={action} error={ex.GetType().Name}",
                ex);
        }
    }

    private async Task TryHandleRecoveryResultAsync(
        CardRecoveryAttemptKey selectedKey,
        CardPaymentRecoveryResult result,
        string action)
    {
        if (_recoveryResultHandledAsync is null)
        {
            return;
        }

        try
        {
            await _recoveryResultHandledAsync(selectedKey, result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 壳层回调属于提交后收尾；失败只能记录，不能把结果改写为恢复失败。
            TryWritePostCommitWarning(
                $"post-result callback failed action={action} processor={selectedKey.Processor} attempt={selectedKey.AttemptGuid:D} error={ex.GetType().Name}",
                ex);
        }
    }

    private async Task RunRecoveryResultHandoffAsync(Func<Task> action, string context)
    {
        try
        {
            await action();
        }
        catch (CardRecoveryDraftHandoffPostCommitException ex)
        {
            // 金融与订单状态已经耐久提交；这里只能保留锁并显示真实告警，不能提示再次执行金融恢复。
            try
            {
                SetLiteralStatus(ex.Message);
            }
            catch (Exception statusException) when (
                statusException is not OutOfMemoryException and
                not StackOverflowException)
            {
                TryWritePostCommitWarning(
                    $"committed draft handoff status failed context={context} error={statusException.GetType().Name}",
                    statusException);
            }

            TryWritePostCommitWarning(
                $"committed draft handoff remains locked context={context}",
                ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 草稿交接失败不是普通提交后刷新失败；必须显示失败关闭提示并保留恢复锁。
            try
            {
                SetStatusResource(
                    "cardRecovery.center.status.draftHandoffFailed",
                    "The recovery result was saved, but the payment draft could not be handed off safely. Run recovery again before taking another payment or refund. {0}",
                    ex.Message);
            }
            catch (Exception statusException) when (
                statusException is not OutOfMemoryException and
                not StackOverflowException)
            {
                TryWritePostCommitWarning(
                    $"draft handoff status failed context={context} error={statusException.GetType().Name}",
                    statusException);
            }

            TryWritePostCommitWarning(
                $"draft handoff failed context={context} error={ex.GetType().Name}",
                ex);
        }
    }

    private static void TryWritePostCommitWarning(string message, Exception exception)
    {
        try
        {
            ConsoleLog.WriteError("CardRecoveryCenter", message, exception: exception);
        }
        catch (Exception loggingException) when (
            loggingException is not OutOfMemoryException and
            not StackOverflowException)
        {
            // 提交后诊断日志自身失败只能被忽略，不能触发外层“保存失败”路径。
        }
    }

    private void NotifySelectionCommands()
    {
        BackCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        RecoverCommand.NotifyCanExecuteChanged();
        ConfirmPaidCommand.NotifyCanExecuteChanged();
        ConfirmNotPaidCommand.NotifyCanExecuteChanged();
        ContinueWaitingCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSelectedProductLines(string? orderDraftJson)
    {
        IReadOnlyList<PosCartLineSnapshot> lines = [];
        if (!string.IsNullOrWhiteSpace(orderDraftJson) &&
            CardRecoveryCartMaterializer.TryPrepare(
                orderDraftJson,
                DraftJsonOptions,
                out var draft) &&
            draft is not null)
        {
            // 与实际恢复共用隔离物化规则，历史 JSON 即使语法合法但语义缺失也不能击穿异常中心。
            lines = draft.CartSnapshot.Lines;
        }

        _selectedProductLines = lines;
        OnPropertyChanged(nameof(SelectedProductLines));
        OnPropertyChanged(nameof(HasProductSnapshot));
        OnPropertyChanged(nameof(HasNoProductSnapshot));
    }

    private void NotifySelectedAttemptProperties()
    {
        NotifyWorkspaceProperties();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(SelectedTypeText));
        OnPropertyChanged(nameof(SelectedChannelText));
        OnPropertyChanged(nameof(SelectedAmountText));
        OnPropertyChanged(nameof(SelectedCashierText));
        OnPropertyChanged(nameof(SelectedTimeText));
        OnPropertyChanged(nameof(SelectedSessionText));
        OnPropertyChanged(nameof(SelectedTxnText));
        OnPropertyChanged(nameof(SelectedResponseCodeText));
        OnPropertyChanged(nameof(SelectedResponseText));
        OnPropertyChanged(nameof(SelectedStatusText));
        OnPropertyChanged(nameof(SelectedAttemptText));
        OnPropertyChanged(nameof(SelectedEnvironmentText));
        OnPropertyChanged(nameof(SelectedReferenceText));
        OnPropertyChanged(nameof(IsSquareRefundProcessing));
        OnPropertyChanged(nameof(CanShowSupervisorResolution));
        OnPropertyChanged(nameof(CanShowRecoveryOnlyGuidance));
        OnPropertyChanged(nameof(RecoveryOnlyGuidanceMessage));
        OnPropertyChanged(nameof(SquareRefundProcessingMessage));
        OnPropertyChanged(nameof(ResolutionSectionTitleText));
        OnPropertyChanged(nameof(ResolutionInstructionsText));
        OnPropertyChanged(nameof(ResolutionReasonLabelText));
        OnPropertyChanged(nameof(ResolutionEvidenceLabelText));
        OnPropertyChanged(nameof(ResolutionReferenceLabelText));
    }

    private async Task RefreshListCoreAsync(string? actionMessage = null)
    {
        var selectedKey = SelectedAttempt?.Key ?? SelectedRow?.Key;
        var loadResult = _recoveryService is ICardRecoveryQueueLoader queueLoader
            ? await queueLoader.LoadHistoryQueueAsync(_session)
            : new CardRecoveryQueueLoadResult(
                await _recoveryService.ListHistoryAsync(_session),
                []);
        var failedProviders = loadResult.FailedProviders.ToHashSet();
        // 某 provider 读取失败时保留它最后一次成功展示的快照；只有成功读取的 provider
        // 才能用本次结果替换，避免把“加载失败”误报成“队列已清空”。
        var items = _history
            .Where(item => failedProviders.Contains(item.Processor))
            .Concat(loadResult.Items)
            .GroupBy(item => item.Key)
            .Select(group => group.Last())
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.CreatedAt)
            .ToArray();
        _history = items;
        if (loadResult.IsComplete) _lastRefresh = DateTimeOffset.Now;
        OpenAttempts.Clear();
        foreach (var item in items.Where(item => item.IsOpen))
        {
            OpenAttempts.Add(item);
        }

        RefreshDisplayRows(selectedKey);

        SelectedAttempt = OpenAttemptRows.FirstOrDefault(row => row.Key == selectedKey)?.Source
            ?? OpenAttemptRows.FirstOrDefault()?.Source;
        NotifyWorkspaceProperties();
        OnPropertyChanged(nameof(OpenCountText));
        OnPropertyChanged(nameof(HasOpenAttempts));
        OnPropertyChanged(nameof(HasNoOpenAttempts));
        // 部分结果不能覆盖 shell 中上一次完整计数，否则首次失败会把未知 provider 误报为 0。
        if (loadResult.IsComplete)
        {
            _openCountChanged?.Invoke(OpenAttempts.Count);
        }
        var providerWarning = failedProviders.Count == 0
            ? null
            : string.Format(
                GetCulture(),
                T(
                    "cardRecovery.center.status.providerRefreshFailed",
                    "{0} could not be refreshed. Last known transactions remain visible. Use Refresh to retry."),
                string.Join(", ", failedProviders.Select(provider => MapChannel(provider))));
        if (actionMessage is not null)
        {
            SetLiteralStatus(providerWarning is null
                ? actionMessage
                : $"{actionMessage} {providerWarning}");
        }
        else if (providerWarning is not null)
        {
            SetLiteralStatus(providerWarning);
        }
        else if (OpenAttempts.Count == 0)
        {
            SetStatusResource(
                "cardRecovery.center.status.empty",
                "No card transactions need attention.");
        }
        else
        {
            SetStatusResource(
                "cardRecovery.center.status.loaded",
                "Loaded {0} open card transactions.",
                OpenAttempts.Count);
        }
    }

    private void SetLiteralStatus(string value) => StatusMessage = value;

    private void SetStatusResource(string key, string fallback, params object[] args)
    {
        var format = T(key, fallback);
        StatusMessage = args.Length == 0
            ? format
            : string.Format(GetCulture(), format, args);
    }

    private string T(string key, string fallback)
    {
        var value = _localization?.T(key);
        return string.IsNullOrWhiteSpace(value) ||
               value == key ||
               (value.StartsWith("[[", StringComparison.Ordinal) &&
                value.EndsWith("]]", StringComparison.Ordinal))
            ? fallback
            : value;
    }

    private IFormatProvider GetCulture() =>
        _localization?.CurrentCulture ?? CultureInfo.CurrentCulture;

    private string NoneText => T("cardRecovery.center.value.none", "-");

    private string ValueOrNone(string? value) => Normalize(value) ?? NoneText;

    private void RefreshDisplayRows(CardRecoveryAttemptKey? preservedSelectionKey = null)
    {
        var selectedKey = preservedSelectionKey ?? SelectedAttempt?.Key ?? SelectedRow?.Key;
        _isRebuildingRows = true;
        try
        {
            OpenAttemptRows.Clear();
            foreach (var item in FilteredHistory())
            {
                OpenAttemptRows.Add(CreateDisplayRow(item));
            }
        }
        finally
        {
            _isRebuildingRows = false;
        }

        var selectedRow = selectedKey is null
            ? null
            : OpenAttemptRows.FirstOrDefault(row => row.Key == selectedKey.Value);
        if (!ReferenceEquals(_selectedRow, selectedRow))
        {
            _selectedRow = selectedRow;
            OnPropertyChanged(nameof(SelectedRow));
        }

        var selectedAttempt = selectedKey is null
            ? null
            : OpenAttemptRows.FirstOrDefault(item => item.Key == selectedKey.Value)?.Source;
        if (!ReferenceEquals(_selectedAttempt, selectedAttempt))
        {
            SelectedAttempt = selectedAttempt;
        }
    }

    private CardRecoveryQueueRowViewModel CreateDisplayRow(CardRecoveryQueueItem item) =>
        new(
            item,
            MapOperationType(item.OperationKind),
            MapChannel(item.Processor),
            item.UpdatedAt.ToString("g", GetCulture()),
            FormatAmount(item.Amount),
            MapStatus(item.Status),
            StatusPillFor(item),
            ToneFor(item),
            $"{MapChannel(item.Processor)} · {ShortOrder(OrderReference(item))} · {ListTimeText(item.UpdatedAt)}");

    private string MapOperationType(string? value)
    {
        var compact = Normalize(value) is { } normalized
            ? new string(normalized.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()
            : string.Empty;
        var key = compact switch
        {
            "sale" => "cardRecovery.center.type.sale",
            "refund" => "cardRecovery.center.type.refund",
            "activesession" => "cardRecovery.center.type.activeSession",
            _ => "cardRecovery.center.value.unknown"
        };
        return T(key, key == "cardRecovery.center.type.sale"
            ? "Card sale"
            : key == "cardRecovery.center.type.refund"
                ? "Card refund"
                : key == "cardRecovery.center.type.activeSession"
                    ? "Active terminal session"
                    : "Unknown");
    }

    private string MapChannel(CardProcessorKind? value)
    {
        var key = value switch
        {
            CardProcessorKind.Linkly => "cardRecovery.center.channel.linkly",
            CardProcessorKind.Square => "cardRecovery.center.channel.square",
            _ => "cardRecovery.center.value.unknown"
        };
        return T(key, key switch
        {
            "cardRecovery.center.channel.linkly" => "Linkly",
            "cardRecovery.center.channel.square" => "Square",
            _ => "Unknown"
        });
    }

    private string MapStatus(string? value)
    {
        var normalized = Normalize(value);
        var compact = normalized is not null
            ? new string(normalized.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()
            : string.Empty;
        var key = compact.Length == 0
            ? "cardRecovery.center.value.unknown"
            : compact switch
        {
            "none" => "cardRecovery.center.value.unknown",
            "pending" => "cardRecovery.center.transactionStatus.pending",
            "sessionstarted" => "cardRecovery.center.transactionStatus.sessionStarted",
            "recovering" => "cardRecovery.center.transactionStatus.recovering",
            "approved" => "cardRecovery.center.transactionStatus.approved",
            "requiresreview" => "cardRecovery.center.transactionStatus.requiresReview",
            "declined" => "cardRecovery.center.transactionStatus.declined",
            "timedout" => "cardRecovery.center.transactionStatus.timedOut",
            "cancelled" or "canceled" => "cardRecovery.center.transactionStatus.cancelled",
            "failed" => "cardRecovery.center.transactionStatus.failed",
            "ordercompleted" => "cardRecovery.center.transactionStatus.orderCompleted",
            "abandoned" => "cardRecovery.center.transactionStatus.abandoned",
            "checkoutcreated" => "cardRecovery.center.transactionStatus.checkoutCreated",
            "checkoutcompleted" => "cardRecovery.center.transactionStatus.checkoutCompleted",
            "paymentverified" => "cardRecovery.center.transactionStatus.paymentVerified",
            // Provider 队列会把该阶段放入 Status；它不是数据库状态枚举。
            "finalizepending" => "cardRecovery.status.finalizePending",
            _ => "cardRecovery.center.transactionStatus.unknown"
        };
        return T(key, key switch
        {
            "cardRecovery.center.transactionStatus.pending" => "Pending",
            "cardRecovery.center.transactionStatus.sessionStarted" => "Session started",
            "cardRecovery.center.transactionStatus.recovering" => "Checking result",
            "cardRecovery.center.transactionStatus.approved" => "Payment approved",
            "cardRecovery.center.transactionStatus.requiresReview" => "Needs supervisor review",
            "cardRecovery.center.transactionStatus.declined" => "Declined",
            "cardRecovery.center.transactionStatus.timedOut" => "Timed out",
            "cardRecovery.center.transactionStatus.cancelled" => "Cancelled",
            "cardRecovery.center.transactionStatus.failed" => "Failed",
            "cardRecovery.center.transactionStatus.orderCompleted" => "Order completed",
            "cardRecovery.center.transactionStatus.abandoned" => "Abandoned",
            "cardRecovery.center.transactionStatus.checkoutCreated" => "Checkout created",
            "cardRecovery.center.transactionStatus.checkoutCompleted" => "Checkout completed",
            "cardRecovery.center.transactionStatus.paymentVerified" => "Payment verified",
            "cardRecovery.status.finalizePending" => "Finalization pending",
            _ => "Unknown"
        });
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RefreshDisplayRows();
        NotifyWorkspaceProperties();
        OnPropertyChanged(nameof(OpenCountText));
        NotifySelectedAttemptProperties();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        if (_localization is not null)
        {
            _localization.CultureChanged -= OnCultureChanged;
        }
    }
}
