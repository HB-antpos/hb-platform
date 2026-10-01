using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

internal sealed class CardRecoveryDraftHandoffPostCommitException(string message)
    : InvalidOperationException(message);

internal sealed class CardRecoveryPresenter
{
    private enum ActiveSessionRecoveryDialogAction
    {
        Close,
        Retry,
        ManualConfirm
    }

    private readonly ICardPaymentRecoveryService? _cardPaymentRecoveryService;
    private readonly ICardRecoveryResultDialogService? _cardRecoveryResultDialogService;
    private readonly IReceiptQueryService _receiptQueryService;
    private readonly IReceiptPrinterSettingsStore? _receiptPrinterSettingsStore;
    private readonly IReceiptTextFormatter _receiptTextFormatter;
    private readonly ILocalizationService _localization;
    private readonly ILinklyFallbackPromptCoordinator? _linklyFallbackPromptCoordinator;
    private readonly ILinklyBankReceiptPrinter? _linklyBankReceiptPrinter;
    private readonly MainChildViewModelFactory _mainChildViewModelFactory;
    private readonly PosCartService _cart;
    private readonly Action<string?>? _setStatusMessage;
    private readonly Action<bool, string?>? _setPaymentRecoveryBlocked;
    private readonly Func<Window?>? _getOwner;
    private readonly Func<Task>? _navigateToPaymentOnDraft;
    private readonly Func<PosSessionState>? _getSession;
    private readonly Action<PosSessionState>? _setSession;
    private readonly Action<LocalOrder>? _onCardRecoveryOrderCompleted;
    private readonly Action<IReadOnlyList<PaymentTender>?, string?>? _onCardRecoveryDraftRestored;
    private readonly Func<bool, IReadOnlyList<PaymentTender>?, string?, bool>? _tryApplyCardRecoveryDraft;
    private readonly Func<CardRecoveryAttemptKey, CancellationToken, Task<bool>>? _completeRecoveredDraftHandoffAsync;
    private readonly Action<bool>? _setAlternativeRefundMethodRequired;
    private readonly Func<Task>? _refreshPendingSyncAsync;
    private readonly Func<ReceiptDetails, ReceiptPrintReason, Task<ReceiptPrintResult>>? _printReceiptAsync;
    private readonly Func<bool>? _canPrintReceipt;
    private readonly Action? _notifyShowCashPaymentCanExecuteChanged;
    private readonly Action? _notifyPrintRecoveredReceiptCanExecuteChanged;
    private readonly Action<string>? _notifyPropertyChanged;
    private readonly IOperationAuthorizationService? _operationAuthorizationService;
    private readonly IOperationAuditLogger? _operationAuditLogger;
    private readonly Func<string, bool>? _requirePermission;

    private Task<CardPaymentRecoveryResult>? _cardPaymentRecoveryTask;
    private ReceiptDetails? _cardRecoveryDialogReceipt;
    private TaskCompletionSource<ActiveSessionRecoveryDialogAction>? _activeSessionRecoveryDialogActionSource;

    public CardRecoveryPresenter(
        ICardPaymentRecoveryService? cardPaymentRecoveryService,
        ICardRecoveryResultDialogService? cardRecoveryResultDialogService,
        IReceiptQueryService receiptQueryService,
        IReceiptPrinterSettingsStore? receiptPrinterSettingsStore,
        IReceiptTextFormatter receiptTextFormatter,
        ILocalizationService localization,
        ILinklyFallbackPromptCoordinator? linklyFallbackPromptCoordinator,
        ILinklyBankReceiptPrinter? linklyBankReceiptPrinter,
        MainChildViewModelFactory mainChildViewModelFactory,
        PosCartService cart,
        Action<string?>? setStatusMessage = null,
        Action<bool, string?>? setPaymentRecoveryBlocked = null,
        Func<Window?>? getOwner = null,
        Func<Task>? navigateToPaymentOnDraft = null,
        Func<PosSessionState>? getSession = null,
        Action<PosSessionState>? setSession = null,
        Action<LocalOrder>? onCardRecoveryOrderCompleted = null,
        Action<IReadOnlyList<PaymentTender>?, string?>? onCardRecoveryDraftRestored = null,
        Func<bool, IReadOnlyList<PaymentTender>?, string?, bool>? tryApplyCardRecoveryDraft = null,
        Func<CardRecoveryAttemptKey, CancellationToken, Task<bool>>? completeRecoveredDraftHandoffAsync = null,
        Func<Task>? refreshPendingSyncAsync = null,
        Func<ReceiptDetails, ReceiptPrintReason, Task<ReceiptPrintResult>>? printReceiptAsync = null,
        Func<bool>? canPrintReceipt = null,
        Action? notifyShowCashPaymentCanExecuteChanged = null,
        Action? notifyPrintRecoveredReceiptCanExecuteChanged = null,
        Action<string>? notifyPropertyChanged = null,
        IOperationAuthorizationService? operationAuthorizationService = null,
        IOperationAuditLogger? operationAuditLogger = null,
        Func<string, bool>? requirePermission = null,
        Action<bool>? setAlternativeRefundMethodRequired = null)
    {
        _cardPaymentRecoveryService = cardPaymentRecoveryService;
        _cardRecoveryResultDialogService = cardRecoveryResultDialogService;
        _receiptQueryService = receiptQueryService;
        _receiptPrinterSettingsStore = receiptPrinterSettingsStore;
        _receiptTextFormatter = receiptTextFormatter;
        _localization = localization;
        _linklyFallbackPromptCoordinator = linklyFallbackPromptCoordinator;
        _linklyBankReceiptPrinter = linklyBankReceiptPrinter;
        _mainChildViewModelFactory = mainChildViewModelFactory;
        _cart = cart;
        _setStatusMessage = setStatusMessage;
        _setPaymentRecoveryBlocked = setPaymentRecoveryBlocked;
        _getOwner = getOwner;
        _navigateToPaymentOnDraft = navigateToPaymentOnDraft;
        _getSession = getSession;
        _setSession = setSession;
        _onCardRecoveryOrderCompleted = onCardRecoveryOrderCompleted;
        _onCardRecoveryDraftRestored = onCardRecoveryDraftRestored;
        _tryApplyCardRecoveryDraft = tryApplyCardRecoveryDraft;
        _completeRecoveredDraftHandoffAsync = completeRecoveredDraftHandoffAsync;
        _setAlternativeRefundMethodRequired = setAlternativeRefundMethodRequired;
        _refreshPendingSyncAsync = refreshPendingSyncAsync;
        _printReceiptAsync = printReceiptAsync;
        _canPrintReceipt = canPrintReceipt;
        _notifyShowCashPaymentCanExecuteChanged = notifyShowCashPaymentCanExecuteChanged;
        _notifyPrintRecoveredReceiptCanExecuteChanged = notifyPrintRecoveredReceiptCanExecuteChanged;
        _notifyPropertyChanged = notifyPropertyChanged;
        _operationAuthorizationService = operationAuthorizationService;
        _operationAuditLogger = operationAuditLogger;
        _requirePermission = requirePermission;

        CloseCardRecoveryResultDialogCommand = new RelayCommand(CloseCardRecoveryResultDialog);
        PrintRecoveredReceiptCommand = new AsyncRelayCommand(PrintRecoveredReceiptAsync, CanPrintRecoveredReceipt);
        RetryActiveSessionRecoveryCommand = new RelayCommand(
            () => CompleteActiveSessionRecoveryDialog(ActiveSessionRecoveryDialogAction.Retry));
        ResolveCardRefundCommand = new AsyncRelayCommand<CardRefundSupervisorDecision>(ResolveCardRefundAsync);
        ResolveCardPaymentCommand = new AsyncRelayCommand<CardPaymentSupervisorDecision>(ResolveCardPaymentAsync);

        if (_cardRecoveryResultDialogService is not null)
        {
            _cardRecoveryResultDialogService.DialogRequested += OnCardRecoveryResultDialogRequested;
        }
    }

    // ---- State properties ----

    public CardRecoveryResultDialogViewModel? CardRecoveryResultDialog { get; set; }

    public async Task<CardPaymentHandoffCandidate?> PrepareCardPaymentHandoffAsync(
        CardPaymentHandoffRequest request)
    {
        if (_cardPaymentRecoveryService is null)
        {
            return null;
        }

        var openAttempts = await _cardPaymentRecoveryService.ListOpenAsync(request.Session, CancellationToken.None);
        return CardPaymentHandoffQualification.SelectCandidate(openAttempts, request);
    }

    public async Task<bool> HandoffCardPaymentAsync(
        CardPaymentHandoffCandidate candidate,
        CardPaymentHandoffRequest request)
    {
        if (_cardPaymentRecoveryService is null)
        {
            return false;
        }

        try
        {
            var openAttempts = await _cardPaymentRecoveryService.ListOpenAsync(request.Session, CancellationToken.None);
            // 确认瞬间再次定点核验，避免队列刷新、GUID 漂移或草稿损坏后误清当前订单。
            return CardPaymentHandoffQualification.CandidateStillMatches(openAttempts, candidate, request);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "CardRecovery",
                $"card payment handoff confirmation failed attemptGuid={candidate.AttemptGuid} error={ex.GetType().Name}",
                exception: ex);
            return false;
        }
    }

    public bool IsCardRecoveryResultDialogOpen { get; set; }

    // ---- Commands ----

    public IRelayCommand CloseCardRecoveryResultDialogCommand { get; }

    public IAsyncRelayCommand PrintRecoveredReceiptCommand { get; }

    public IRelayCommand RetryActiveSessionRecoveryCommand { get; }

    public IAsyncRelayCommand<CardRefundSupervisorDecision> ResolveCardRefundCommand { get; }

    public IAsyncRelayCommand<CardPaymentSupervisorDecision> ResolveCardPaymentCommand { get; }

    // ---- Public methods ----

    public async Task<bool> RecoverCardPaymentAttemptAsync(bool navigateToPaymentOnDraft)
    {
        if (_cardPaymentRecoveryService is null)
        {
            return false;
        }

        _setPaymentRecoveryBlocked?.Invoke(
            true,
            "Checking the previous card transaction. Please do not collect payment again.");
        var recoveryTask = _cardPaymentRecoveryTask;
        if (recoveryTask is null)
        {
            recoveryTask = _cardPaymentRecoveryService.RecoverLatestAsync(_cart, GetSession(), CancellationToken.None);
            _cardPaymentRecoveryTask = recoveryTask;
        }

        CardPaymentRecoveryResult result;
        try
        {
            result = await recoveryTask;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_cardPaymentRecoveryTask, recoveryTask))
            {
                _cardPaymentRecoveryTask = null;
            }

            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (ReferenceEquals(_cardPaymentRecoveryTask, recoveryTask))
            {
                _cardPaymentRecoveryTask = null;
            }

            ConsoleLog.WriteError(
                "CardRecovery",
                $"recover latest card payment failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
            TrySetPaymentRecoveryBlocked(true, ex.Message);
            throw;
        }

        if ((ShouldRetryCardPaymentRecovery(result.Outcome) || result.Outcome == CardPaymentRecoveryOutcome.DraftRestored) &&
            ReferenceEquals(_cardPaymentRecoveryTask, recoveryTask))
        {
            _cardPaymentRecoveryTask = null;
        }

        try
        {
            if (result.Outcome == CardPaymentRecoveryOutcome.DraftRestored)
            {
                var paymentPageUnlocked = await HandoffRecoveredCardDraftAsync(
                    result,
                    navigateToPaymentOnDraft);

                if (!paymentPageUnlocked)
                {
                    ReportDraftHandoffPostCommitWarning(
                        result,
                        "recovered card draft unlock");
                    return true;
                }

                // 中文注释：付款页交接和命令刷新成功后，提示与结果对话才属于提交后动作。
                RunPostCommitAction(
                    () => _setStatusMessage?.Invoke(result.Message),
                    "recovered card draft status");
                RunPostCommitAction(
                    () => ShowRecoveredCardDraftDialog(result),
                    "recovered card draft dialog");
                return true;
            }

            ApplyRecoveryStatus(result);
            if (result.Outcome == CardPaymentRecoveryOutcome.None)
            {
                return false;
            }

            if (result.Outcome == CardPaymentRecoveryOutcome.OrderCompleted && result.Order is not null)
            {
                if (_refreshPendingSyncAsync is not null)
                {
                    await _refreshPendingSyncAsync();
                }

                LogRecoveredCardOrderCompleted(result.Order);
                var printResult = await PrintRecoveredCardReceiptAsync(result.Order);
                _onCardRecoveryOrderCompleted?.Invoke(result.Order);
                await ShowRecoveredCardOrderDialogAsync(result, printResult);
                if (result.HasPostCommitWarning)
                {
                    // 打印、同步等收尾失败不能覆盖“金融提交已完成”的安全提示。
                    ApplyRecoveryStatus(result);
                }

                _notifyShowCashPaymentCanExecuteChanged?.Invoke();
                return true;
            }

            if (result.Outcome == CardPaymentRecoveryOutcome.Unknown)
            {
                ShowRecoveredCardFailureDialog(result);
            }

            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var durableOutcomeWithoutRecoveryOwner = result.Outcome is
                CardPaymentRecoveryOutcome.OrderCompleted or
                CardPaymentRecoveryOutcome.None;
            if (durableOutcomeWithoutRecoveryOwner)
            {
                // 中文注释：服务已返回不需要恢复锁的耐久结果（尤其 OrderCompleted/None）后，
                // UI、打印或命令通知失败只能记为 post-commit 告警，绝不能制造没有开放 attempt 的新锁。
                TrySetPaymentRecoveryBlocked(false, result.Message);
                TryWritePostCommitWarning(
                    $"recovery result projection failed after durable outcome={result.Outcome} error={ex.GetType().Name}",
                    ex);
                return result.Outcome != CardPaymentRecoveryOutcome.None;
            }

            TrySetPaymentRecoveryBlocked(true, ex.Message);
            throw;
        }
    }

    private async Task ResolveCardRefundAsync(CardRefundSupervisorDecision decision)
    {
        var dialog = CardRecoveryResultDialog;
        var details = dialog?.RefundDetails;
        if (dialog is null || details is null || _cardPaymentRecoveryService is null)
        {
            return;
        }

        if (dialog.IsRefundResolutionBusy)
        {
            return;
        }

        dialog.RefundResolutionMessage = string.Empty;
        dialog.IsRefundResolutionBusy = true;
        try
        {
            using var authorization = await ViewModelOperationAuthorization.AuthorizeAsync(
                _operationAuthorizationService,
                _requirePermission ?? DenyPermission,
                Permissions.PosTerminal.Returns.Confirm,
                "card-recovery",
                $"resolve-refund/{decision.ToString().ToLowerInvariant()}",
                GetSession(),
                CancellationToken.None);
            if (authorization is null)
            {
                dialog.RefundResolutionMessage = _localization.T("cardRecovery.refund.authorizationRequired");
                return;
            }

            using var activation = authorization.Activate();
            var resolution = new CardRefundSupervisorResolution(
                details.AttemptGuid,
                details.Processor,
                decision,
                dialog.RefundSupervisorNote,
                dialog.RefundEvidence,
                dialog.RefundReference);
            var result = await _cardPaymentRecoveryService.ResolveRefundAsync(
                resolution,
                _cart,
                GetSession(),
                CancellationToken.None);
            if (result.RecoveryResult is not { Outcome: CardPaymentRecoveryOutcome.DraftRestored })
            {
                dialog.RefundResolutionMessage = result.Message;
            }
            var hasCompletedBusinessRecovery = result.Succeeded &&
                result.RecoveryResult is
                    { Outcome: CardPaymentRecoveryOutcome.DraftRestored } or
                    { Outcome: CardPaymentRecoveryOutcome.OrderCompleted, Order: not null };
            // FinalizePending 会保留 attempt 锁，但已发布的真实恢复结果仍必须交给付款页继续完成。
            if ((result.LockRetained && !hasCompletedBusinessRecovery) ||
                (!result.Succeeded && !result.ResolutionPersisted))
            {
                return;
            }

            _cardPaymentRecoveryTask = null;
            if (result.RecoveryResult is { Outcome: CardPaymentRecoveryOutcome.DraftRestored } recoveredDraft)
            {
                // 主管结案的 DraftRestored 与普通恢复必须经过同一关键交接；交接失败时保留主管对话。
                var paymentPageUnlocked = await HandoffRecoveredCardDraftAsync(
                    recoveredDraft,
                    navigateToPaymentOnDraft: true);
                if (!paymentPageUnlocked)
                {
                    ReportDraftHandoffPostCommitWarning(
                        recoveredDraft,
                        $"refund resolution draft unlock attemptGuid={details.AttemptGuid:D}");
                    RunPostCommitAction(
                        CloseCardRecoveryResultDialog,
                        $"refund resolution dialog close after committed unlock warning attemptGuid={details.AttemptGuid:D}");
                    return;
                }

                RunPostCommitAction(
                    () => _setStatusMessage?.Invoke(result.Message),
                    $"refund resolution status attemptGuid={details.AttemptGuid:D}");
                RunPostCommitAction(
                    CloseCardRecoveryResultDialog,
                    $"refund resolution dialog close attemptGuid={details.AttemptGuid:D}");
                RunPostCommitAction(
                    () => ShowRecoveredCardDraftDialog(recoveredDraft),
                    $"refund resolution draft dialog attemptGuid={details.AttemptGuid:D}");
                return;
            }

            RunPostCommitAction(
                () => _setStatusMessage?.Invoke(result.Message),
                $"refund resolution status attemptGuid={details.AttemptGuid:D}");
            RunPostCommitAction(
                CloseCardRecoveryResultDialog,
                $"refund resolution dialog close attemptGuid={details.AttemptGuid:D}");
            if (result.RecoveryResult is not null)
            {
                await RunPostCommitActionAsync(
                    () => ApplyResolvedRefundRecoveryAsync(result.RecoveryResult),
                    $"refund resolution result application attemptGuid={details.AttemptGuid:D}");
            }
            else
            {
                RunPostCommitAction(
                    () => _setPaymentRecoveryBlocked?.Invoke(false, result.Message),
                    $"refund resolution unlock attemptGuid={details.AttemptGuid:D}");
            }
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException and
            not OutOfMemoryException and
            not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "CardRecovery",
                $"resolve card refund failed attemptGuid={details.AttemptGuid} decision={decision} error={ex.GetType().Name}",
                exception: ex);
            dialog.RefundResolutionMessage = _localization.T("cardRecovery.refund.resolveFailed");
        }
        finally
        {
            dialog.IsRefundResolutionBusy = false;
        }
    }

    private async Task ResolveCardPaymentAsync(CardPaymentSupervisorDecision decision)
    {
        var dialog = CardRecoveryResultDialog;
        var details = dialog?.PaymentSupervisorDetails;
        if (dialog is null || details is null || _cardPaymentRecoveryService is null)
        {
            return;
        }

        if (dialog.IsRefundResolutionBusy)
        {
            return;
        }

        dialog.RefundResolutionMessage = string.Empty;
        dialog.IsRefundResolutionBusy = true;
        try
        {
            var session = GetSession();
            using var authorization = await ViewModelOperationAuthorization.AuthorizeAsync(
                _operationAuthorizationService,
                _requirePermission ?? DenyPermission,
                Permissions.PosTerminal.Payment.Confirm,
                "card-recovery",
                $"resolve-payment/{decision.ToString().ToLowerInvariant()}",
                session,
                CancellationToken.None);
            if (authorization is null)
            {
                dialog.RefundResolutionMessage = _localization.T("cardRecovery.payment.authorizationRequired");
                return;
            }

            using var activation = authorization.Activate();
            var authorizer = OperationAuthorizationScope.CurrentAuthorizationContext?.AuthorizingSession ??
                session.CashierSession;
            if (authorizer is null)
            {
                dialog.RefundResolutionMessage = _localization.T("cardRecovery.payment.authorizationRequired");
                return;
            }

            var resolution = new CardPaymentSupervisorResolution(
                details.AttemptGuid,
                details.Processor,
                decision,
                dialog.RefundSupervisorNote,
                authorizer.CashierId,
                authorizer.UserGuid,
                authorizer.CashierName,
                dialog.RefundEvidence,
                dialog.RefundReference);
            var result = await _cardPaymentRecoveryService.ResolvePaymentAsync(
                resolution,
                _cart,
                session,
                CancellationToken.None);
            if (result.RecoveryResult is not { Outcome: CardPaymentRecoveryOutcome.DraftRestored })
            {
                dialog.RefundResolutionMessage = result.Message;
            }
            var hasCompletedBusinessRecovery = result.Succeeded &&
                result.RecoveryResult is
                    { Outcome: CardPaymentRecoveryOutcome.DraftRestored } or
                    { Outcome: CardPaymentRecoveryOutcome.OrderCompleted, Order: not null };
            // 保留异常记录只阻止重新收款，不能吞掉已经完成的订单或已发布草稿。
            if ((result.LockRetained && !hasCompletedBusinessRecovery) ||
                (!result.Succeeded && !result.ResolutionPersisted))
            {
                return;
            }

            _cardPaymentRecoveryTask = null;
            if (result.RecoveryResult is { Outcome: CardPaymentRecoveryOutcome.DraftRestored } recoveredDraft)
            {
                // 主管结案的 DraftRestored 与普通恢复必须经过同一关键交接；交接失败时保留主管对话。
                var paymentPageUnlocked = await HandoffRecoveredCardDraftAsync(
                    recoveredDraft,
                    navigateToPaymentOnDraft: true);
                if (!paymentPageUnlocked)
                {
                    ReportDraftHandoffPostCommitWarning(
                        recoveredDraft,
                        $"payment resolution draft unlock attemptGuid={details.AttemptGuid:D}");
                    RunPostCommitAction(
                        CloseCardRecoveryResultDialog,
                        $"payment resolution dialog close after committed unlock warning attemptGuid={details.AttemptGuid:D}");
                    return;
                }

                RunPostCommitAction(
                    () => _setStatusMessage?.Invoke(result.Message),
                    $"payment resolution status attemptGuid={details.AttemptGuid:D}");
                RunPostCommitAction(
                    CloseCardRecoveryResultDialog,
                    $"payment resolution dialog close attemptGuid={details.AttemptGuid:D}");
                RunPostCommitAction(
                    () => ShowRecoveredCardDraftDialog(recoveredDraft),
                    $"payment resolution draft dialog attemptGuid={details.AttemptGuid:D}");
                return;
            }

            RunPostCommitAction(
                () => _setStatusMessage?.Invoke(result.Message),
                $"payment resolution status attemptGuid={details.AttemptGuid:D}");
            RunPostCommitAction(
                CloseCardRecoveryResultDialog,
                $"payment resolution dialog close attemptGuid={details.AttemptGuid:D}");
            if (result.RecoveryResult is not null)
            {
                await RunPostCommitActionAsync(
                    () => ApplyResolvedRefundRecoveryAsync(result.RecoveryResult),
                    $"payment resolution result application attemptGuid={details.AttemptGuid:D}");
            }
            else
            {
                RunPostCommitAction(
                    () => _setPaymentRecoveryBlocked?.Invoke(false, result.Message),
                    $"payment resolution unlock attemptGuid={details.AttemptGuid:D}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "CardRecovery",
                $"resolve card payment failed attemptGuid={details.AttemptGuid} decision={decision} error={ex.GetType().Name}",
                exception: ex);
            dialog.RefundResolutionMessage = _localization.T("cardRecovery.payment.resolveFailed");
        }
        finally
        {
            dialog.IsRefundResolutionBusy = false;
        }
    }

    public async Task<bool> RecoverActiveCardPaymentSessionFromPaymentAsync()
    {
        if (_cardPaymentRecoveryService is null)
        {
            return false;
        }

        while (true)
        {
            var result = await _cardPaymentRecoveryService.RecoverActiveSessionAsync(_cart, GetSession(), CancellationToken.None);
            ApplyRecoveryStatus(result);

            if (result.Outcome == CardPaymentRecoveryOutcome.None)
            {
                return true;
            }

            if (result.Outcome == CardPaymentRecoveryOutcome.DraftRestored)
            {
                // 付款页主动恢复的是旧 active session，不能把恢复结果自动混入当前购物车。
                ShowRecoveredCardDraftDialog(result);
                return true;
            }

            if (result.Outcome is CardPaymentRecoveryOutcome.ActiveSessionApproved or CardPaymentRecoveryOutcome.ActiveSessionNotPaid)
            {
                var printResult = await PrintActiveSessionBankReceiptAsync(result);
                if (!printResult.Succeeded)
                {
                    _setStatusMessage?.Invoke(FormatActiveSessionPrintFailureMessage(result.Message, printResult.Message));
                }

                ShowRecoveredActiveSessionDialog(result, printResult);
                return true;
            }

            if (result.Outcome == CardPaymentRecoveryOutcome.ActiveSessionManuallyCleared)
            {
                return true;
            }

            if (result.Outcome is CardPaymentRecoveryOutcome.Unknown or CardPaymentRecoveryOutcome.Checking)
            {
                var action = await ShowActiveSessionUnresolvedDialogAsync(result);
                if (action == ActiveSessionRecoveryDialogAction.Retry)
                {
                    continue;
                }

                return false;
            }

            // 其它已确认结果表示上一笔不再处于未知状态，付款页可以解除本地阻塞。
            return true;
        }
    }

    internal async Task<string?> HandoffRecoveredCardDraftFromRecoveryCenterAsync(
        CardPaymentRecoveryResult result)
    {
        if (result.Outcome != CardPaymentRecoveryOutcome.DraftRestored)
        {
            throw new ArgumentException(
                "Only a restored card draft can use the recovery-center handoff.",
                nameof(result));
        }

        var paymentPageUnlocked = await HandoffRecoveredCardDraftAsync(
            result,
            navigateToPaymentOnDraft: true);
        if (!paymentPageUnlocked)
        {
            return ReportDraftHandoffPostCommitWarning(
                result,
                "recovery center draft unlock");
        }

        RunPostCommitAction(
            () => _setStatusMessage?.Invoke(result.Message),
            "recovery center draft status");
        return null;
    }

    private async Task<bool> HandoffRecoveredCardDraftAsync(
        CardPaymentRecoveryResult result,
        bool navigateToPaymentOnDraft)
    {
        // 中文注释：优先记录 provider + attempt 的精确 owner；仅对旧 GUID publication 保留兼容回滚。
        var capturedOwnerAttemptKey = _cart.RecoveryOwnerAttemptKey;
        var capturedOwnerAttemptGuid = capturedOwnerAttemptKey?.AttemptGuid ??
            _cart.RecoveryOwnerAttemptGuid;
        var ownerWasDurablyReleased = false;
        try
        {
            if (result.UpdatedSession is not null)
            {
                _setSession?.Invoke(result.UpdatedSession);
            }

            var hasRestoredTenders = result.RestoredTenders is { Count: > 0 };
            if ((navigateToPaymentOnDraft || hasRestoredTenders) && _navigateToPaymentOnDraft is not null)
            {
                await _navigateToPaymentOnDraft();
            }

            // Recovery Center 可能刚创建付款页；导航完成后立即锁页，再开始任何可观察投影。
            _setPaymentRecoveryBlocked?.Invoke(true, result.Message);

            if (_tryApplyCardRecoveryDraft is not null)
            {
                if (!_tryApplyCardRecoveryDraft(
                        result.RequiresAlternativeRefundMethod,
                        result.RestoredTenders,
                        result.Message))
                {
                    throw new InvalidOperationException("The recovered card draft was not projected to the payment page.");
                }
            }
            else
            {
                // 兼容未接入复合投影的旧调用方；生产 MainViewModel 使用上面的原子回调。
                _setAlternativeRefundMethodRequired?.Invoke(result.RequiresAlternativeRefundMethod);
                _onCardRecoveryDraftRestored?.Invoke(
                    result.RestoredTenders,
                    hasRestoredTenders || navigateToPaymentOnDraft ? result.Message : null);
            }

            // 该命令刷新属于关键交接；成功后才允许解除付款页恢复锁。
            _notifyShowCashPaymentCanExecuteChanged?.Invoke();
            if (result.DraftHandoffKey is CardRecoveryAttemptKey handoffKey)
            {
                if (capturedOwnerAttemptKey != handoffKey ||
                    _completeRecoveredDraftHandoffAsync is null ||
                    !await _completeRecoveredDraftHandoffAsync(handoffKey, CancellationToken.None))
                {
                    throw new InvalidOperationException(
                        "The recovered card draft handoff could not be durably finalized.");
                }

                // 服务返回成功仍必须验证精确 owner 已释放；否则禁止把状态不匹配误报为 durable handoff。
                if (_cart.RecoveryOwnerAttemptKey is not null ||
                    _cart.RecoveryOwnerAttemptGuid is not null)
                {
                    throw new InvalidOperationException(
                        "The recovered card draft owner was not durably released.");
                }

                ownerWasDurablyReleased = true;
            }

            try
            {
                _setPaymentRecoveryBlocked?.Invoke(false, null);
                return true;
            }
            catch (Exception ex) when (
                ownerWasDurablyReleased &&
                ex is not OutOfMemoryException and
                not StackOverflowException)
            {
                var warning = BuildDraftHandoffPostCommitWarning(result);
                // 中文注释：生产回调会先清除恢复状态和交互锁，再发送 UI 通知；
                // owner 已耐久释放后若通知失败，绝不能重新制造一个没有开放 attempt 的永久锁。
                TrySetPaymentRecoveryBlocked(false, warning);
                TryWritePostCommitWarning(
                    $"payment recovery unlock failed after durable draft handoff attemptGuid={capturedOwnerAttemptGuid:D} error={ex.GetType().Name}",
                    ex);
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            RollbackCapturedRecoveryPublication(
                capturedOwnerAttemptKey,
                capturedOwnerAttemptGuid);
            TrySetPaymentRecoveryBlocked(
                true,
                string.IsNullOrWhiteSpace(ex.Message) ? result.Message : ex.Message);
            throw;
        }
    }

    private string ReportDraftHandoffPostCommitWarning(
        CardPaymentRecoveryResult result,
        string context)
    {
        var warning = BuildDraftHandoffPostCommitWarning(result);
        RunPostCommitAction(
            () => _setStatusMessage?.Invoke(warning),
            context);
        return warning;
    }

    private string BuildDraftHandoffPostCommitWarning(CardPaymentRecoveryResult result)
    {
        var warning = _localization.T("cardRecovery.draftHandoff.unlockWarning");
        return string.IsNullOrWhiteSpace(result.Message)
            ? warning
            : $"{result.Message} {warning}";
    }

    private void ApplyRecoveryStatus(CardPaymentRecoveryResult result)
    {
        var statusMessage = result.HasPostCommitWarning &&
            result.Outcome == CardPaymentRecoveryOutcome.OrderCompleted
                ? _localization.T("payment.status.completedWarning")
                : result.Message;
        var blocked = result.Outcome is CardPaymentRecoveryOutcome.Checking or CardPaymentRecoveryOutcome.Unknown;
        _setPaymentRecoveryBlocked?.Invoke(blocked, statusMessage);
        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            _setStatusMessage?.Invoke(statusMessage);
        }

        if (result.UpdatedSession is not null)
        {
            _setSession?.Invoke(result.UpdatedSession);
        }
    }

    public void DetachDialogService()
    {
        if (_cardRecoveryResultDialogService is not null)
        {
            _cardRecoveryResultDialogService.DialogRequested -= OnCardRecoveryResultDialogRequested;
        }
    }

    // ---- Private methods ----

    private PosSessionState GetSession()
    {
        return _getSession?.Invoke() ?? new PosSessionState("HB POS", "1002", "Main Branch", "Terminal 04", "C001", "Alice", false, 0);
    }

    private async Task<ReceiptPrintResult> PrintRecoveredCardReceiptAsync(LocalOrder order)
    {
        var evidence = GetCardRecoveryEvidence(order);
        LinklyJsonLog.Write(
            "CardRecovery",
            "card-recovery",
            "power-fail-recovery-print",
            "request",
            direction: "request",
            sessionId: evidence.SessionId,
            request: new
            {
                reason = ReceiptPrintReason.CardAuto.ToString(),
                orderGuid = order.OrderGuid
            },
            details: new
            {
                timestamp = DateTimeOffset.Now,
                certCase = "4.1.3",
                orderGuid = order.OrderGuid,
                transactionReference = evidence.TransactionReference,
                evidence.TxnRef,
                evidence.SessionId,
                reason = "4.1.3"
            });

        var printResult = await PrintReceiptAsync(ReceiptQueryService.CreateReceipt(order), ReceiptPrintReason.CardAuto);
        LinklyJsonLog.Write(
            "CardRecovery",
            "card-recovery",
            "power-fail-recovery-print",
            "response",
            direction: "response",
            sessionId: evidence.SessionId,
            success: printResult.Succeeded,
            reason: printResult.Succeeded ? null : "receipt-print-failed",
            response: new
            {
                printResult.Succeeded,
                printResult.Message,
                printResult.OrderGuid
            },
            details: new
            {
                timestamp = DateTimeOffset.Now,
                certCase = "4.1.3",
                orderGuid = order.OrderGuid,
                transactionReference = evidence.TransactionReference,
                evidence.TxnRef,
                evidence.SessionId,
                reason = "4.1.3"
            });
        return printResult;
    }

    private async Task ShowRecoveredCardOrderDialogAsync(
        CardPaymentRecoveryResult result,
        ReceiptPrintResult printResult)
    {
        if (result.Order is null)
        {
            return;
        }

        var receipt = ReceiptQueryService.CreateReceipt(result.Order);
        _cardRecoveryDialogReceipt = receipt;
        var previewRows = await BuildReceiptPreviewRowsAsync(receipt);
        var details = result.DialogDetails;
        var printMessage = printResult.Succeeded
            ? _localization.T("cardRecovery.dialog.message.autoPrintSucceeded")
            : string.Format(
                _localization.CurrentCulture,
                _localization.T("cardRecovery.dialog.message.autoPrintFailed"),
                printResult.Message);

        ShowCardRecoveryResultDialog(new CardRecoveryResultDialogViewModel(
            _localization.T("cardRecovery.dialog.title.completed"),
            printMessage,
            printResult.Succeeded ? CardRecoveryResultSeverity.Success : CardRecoveryResultSeverity.Warning,
            result.Order.OrderGuid,
            result.Order.ActualAmount,
            details?.SessionId ?? GetCardRecoveryEvidence(result.Order).SessionId,
            details?.TxnRef ?? GetCardRecoveryEvidence(result.Order).TxnRef,
            details?.ResponseCode ?? GetCardRecoveryResponseCode(result.Order),
            details?.ResponseText ?? GetCardRecoveryResponseText(result.Order),
            details?.Timestamp ?? DateTimeOffset.Now,
            previewRows,
            canPrintReceipt: CanUsePrintReceiptPermission(),
            printButtonText: _localization.T("cardRecovery.dialog.action.printReceipt")));
    }

    private void ShowRecoveredCardDraftDialog(CardPaymentRecoveryResult result)
    {
        var details = result.DialogDetails;
        ShowCardRecoveryResultDialog(new CardRecoveryResultDialogViewModel(
            _localization.T("cardRecovery.dialog.title.draftRestored"),
            string.IsNullOrWhiteSpace(result.Message)
                ? _localization.T("cardRecovery.dialog.message.draftRestoredFallback")
                : result.Message,
            CardRecoveryResultSeverity.Warning,
            orderGuid: null,
            amount: details?.Amount,
            sessionId: details?.SessionId,
            txnRef: details?.TxnRef,
            responseCode: details?.ResponseCode,
            responseText: details?.ResponseText,
            timestamp: details?.Timestamp ?? DateTimeOffset.Now));
    }

    private async Task ApplyResolvedRefundRecoveryAsync(CardPaymentRecoveryResult result)
    {
        ApplyRecoveryStatus(result);

        if (result.Outcome == CardPaymentRecoveryOutcome.OrderCompleted && result.Order is not null)
        {
            if (_refreshPendingSyncAsync is not null)
            {
                await _refreshPendingSyncAsync();
            }

            LogRecoveredCardOrderCompleted(result.Order);
            var printResult = await PrintRecoveredCardReceiptAsync(result.Order);
            _onCardRecoveryOrderCompleted?.Invoke(result.Order);
            await ShowRecoveredCardOrderDialogAsync(result, printResult);
            if (result.HasPostCommitWarning)
            {
                ApplyRecoveryStatus(result);
            }

            _notifyShowCashPaymentCanExecuteChanged?.Invoke();
            return;
        }

        if (result.Outcome == CardPaymentRecoveryOutcome.DraftRestored)
        {
            if (_navigateToPaymentOnDraft is not null)
            {
                await _navigateToPaymentOnDraft();
            }

            _setAlternativeRefundMethodRequired?.Invoke(result.RequiresAlternativeRefundMethod);
            _onCardRecoveryDraftRestored?.Invoke(result.RestoredTenders, result.Message);
            _notifyShowCashPaymentCanExecuteChanged?.Invoke();
            ShowRecoveredCardDraftDialog(result);
            return;
        }

        if (result.Outcome == CardPaymentRecoveryOutcome.Unknown)
        {
            ShowRecoveredCardFailureDialog(result);
        }
    }

    private void RollbackCapturedRecoveryPublication(
        CardRecoveryAttemptKey? capturedOwnerAttemptKey,
        Guid? capturedOwnerAttemptGuid)
    {
        if (capturedOwnerAttemptGuid is not Guid attemptGuid)
        {
            return;
        }

        try
        {
            if (capturedOwnerAttemptKey is CardRecoveryAttemptKey attemptKey)
            {
                _cart.RollbackRecoveryPublication(attemptKey);
            }
            else
            {
                _cart.RollbackRecoveryPublication(attemptGuid);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWritePostCommitWarning(
                $"recovered card draft publication rollback failed attemptGuid={attemptGuid:D} error={ex.GetType().Name}",
                ex);
        }
    }

    private void TrySetPaymentRecoveryBlocked(bool blocked, string? message)
    {
        try
        {
            _setPaymentRecoveryBlocked?.Invoke(blocked, message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWritePostCommitWarning(
                $"payment recovery lock fallback failed blocked={blocked} error={ex.GetType().Name}",
                ex);
        }
    }

    private static void RunPostCommitAction(Action action, string context)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 金融决定已经提交；UI、旁路审计或订阅者失败只能作为提交后警告记录。
            TryWritePostCommitWarning(
                $"post-commit action failed context={context} error={ex.GetType().Name}",
                ex);
        }
    }

    private static async Task RunPostCommitActionAsync(Func<Task> action, string context)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TryWritePostCommitWarning(
                $"post-commit action failed context={context} error={ex.GetType().Name}",
                ex);
        }
    }

    private static void TryWritePostCommitWarning(string message, Exception exception)
    {
        try
        {
            ConsoleLog.WriteError("CardRecovery", message, exception: exception);
        }
        catch (Exception loggingException) when (
            loggingException is not OutOfMemoryException and
            not StackOverflowException)
        {
            // 诊断链自身失败也不能反向覆盖已经提交的金融决定。
        }
    }

    private void ShowRecoveredCardFailureDialog(CardPaymentRecoveryResult result)
    {
        var details = result.DialogDetails;
        var refundDetails = result.RefundDetails;
        ShowCardRecoveryResultDialog(new CardRecoveryResultDialogViewModel(
            _localization.T("cardRecovery.dialog.title.unknown"),
            string.IsNullOrWhiteSpace(result.Message)
                ? _localization.T("cardRecovery.dialog.message.failedFallback")
                : result.Message,
            CardRecoveryResultSeverity.Error,
            orderGuid: refundDetails?.OperationGuid,
            amount: details?.Amount,
            sessionId: details?.SessionId,
            txnRef: details?.TxnRef,
            responseCode: details?.ResponseCode,
            responseText: details?.ResponseText,
            timestamp: details?.Timestamp ?? DateTimeOffset.Now,
            refundDetails: refundDetails,
            paymentSupervisorDetails: result.PaymentSupervisorDetails));
    }

    private Task<ActiveSessionRecoveryDialogAction> ShowActiveSessionUnresolvedDialogAsync(CardPaymentRecoveryResult result)
    {
        var details = result.DialogDetails;
        var source = new TaskCompletionSource<ActiveSessionRecoveryDialogAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeSessionRecoveryDialogActionSource?.TrySetResult(ActiveSessionRecoveryDialogAction.Close);
        _activeSessionRecoveryDialogActionSource = source;

        ShowCardRecoveryResultDialog(new CardRecoveryResultDialogViewModel(
            _localization.T("cardRecovery.dialog.title.unknown"),
            string.IsNullOrWhiteSpace(result.Message)
                ? _localization.T("cardRecovery.dialog.message.failedFallback")
                : result.Message,
            CardRecoveryResultSeverity.Error,
            orderGuid: null,
            amount: details?.Amount,
            sessionId: details?.SessionId,
            txnRef: details?.TxnRef,
            responseCode: details?.ResponseCode,
            responseText: details?.ResponseText,
            timestamp: details?.Timestamp ?? DateTimeOffset.Now,
            canRetryRecovery: true,
            retryButtonText: _localization.T("cardRecovery.dialog.action.retryRecovery"),
            paymentSupervisorDetails: result.PaymentSupervisorDetails));

        return source.Task;
    }

    private async Task<bool> ManuallyClearActiveSessionAsync(CardPaymentRecoveryResult result)
    {
        var sessionId = result.DialogDetails?.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            _setStatusMessage?.Invoke(_localization.T("cardRecovery.linkly.activeSessionManualClearMissing"));
            return false;
        }

        // 关键逻辑：人工确认只负责清掉旧 active session，不保存订单、不添加 tender。
        var clearResult = await _cardPaymentRecoveryService!.ManuallyClearActiveSessionAsync(
            sessionId,
            GetSession(),
            CancellationToken.None);
        ApplyRecoveryStatus(clearResult);
        if (clearResult.Outcome == CardPaymentRecoveryOutcome.ActiveSessionManuallyCleared)
        {
            return true;
        }

        ShowRecoveredCardFailureDialog(clearResult);
        return false;
    }

    private async Task<ReceiptPrintResult> PrintActiveSessionBankReceiptAsync(CardPaymentRecoveryResult result)
    {
        var receipt = result.BankReceipt;
        if (receipt is null)
        {
            return new ReceiptPrintResult(false, _localization.T("cardRecovery.linkly.activeSessionReceiptMissing"));
        }

        if (_linklyBankReceiptPrinter is null)
        {
            return new ReceiptPrintResult(false, _localization.T("cardRecovery.linkly.activeSessionReceiptPrinterMissing"));
        }

        // 关键逻辑：active session 没有本地订单，恢复时只能打印 Linkly 银行凭证，不能拼接 POS 商品小票。
        return await _linklyBankReceiptPrinter.PrintAsync(
            receipt.Environment,
            receipt.SessionId,
            receipt.ReceiptText,
            receipt.Kind,
            responseCode: receipt.ResponseCode,
            responseText: receipt.ResponseText);
    }

    private void ShowRecoveredActiveSessionDialog(
        CardPaymentRecoveryResult result,
        ReceiptPrintResult printResult)
    {
        var details = result.DialogDetails;
        var isApproved = result.Outcome == CardPaymentRecoveryOutcome.ActiveSessionApproved;
        var message = printResult.Succeeded
            ? result.Message
            : FormatActiveSessionPrintFailureMessage(result.Message, printResult.Message);

        ShowCardRecoveryResultDialog(new CardRecoveryResultDialogViewModel(
            isApproved
                ? _localization.T("cardRecovery.dialog.title.activeSessionApproved")
                : _localization.T("cardRecovery.dialog.title.activeSessionNotPaid"),
            message,
            isApproved && printResult.Succeeded ? CardRecoveryResultSeverity.Success : CardRecoveryResultSeverity.Warning,
            orderGuid: null,
            amount: details?.Amount,
            sessionId: details?.SessionId,
            txnRef: details?.TxnRef,
            responseCode: details?.ResponseCode,
            responseText: details?.ResponseText,
            timestamp: details?.Timestamp ?? DateTimeOffset.Now));
    }

    private string FormatActiveSessionPrintFailureMessage(string recoveryMessage, string printMessage)
    {
        var failure = string.Format(
            _localization.CurrentCulture,
            _localization.T("cardRecovery.linkly.activeSessionReceiptPrintFailed"),
            printMessage);

        return string.IsNullOrWhiteSpace(recoveryMessage)
            ? failure
            : $"{recoveryMessage} {failure}";
    }

    private void OnCardRecoveryResultDialogRequested(object? sender, CardRecoveryResultDialogViewModel dialog)
    {
        ShowCardRecoveryResultDialog(dialog);
    }

    private void ShowCardRecoveryResultDialog(CardRecoveryResultDialogViewModel dialog)
    {
        CardRecoveryResultDialog = dialog;
        IsCardRecoveryResultDialogOpen = true;
        RunPostCommitAction(
            () => _notifyPropertyChanged?.Invoke(nameof(CardRecoveryResultDialog)),
            "show recovery dialog property notification");
        RunPostCommitAction(
            () => _notifyPropertyChanged?.Invoke(nameof(IsCardRecoveryResultDialogOpen)),
            "show recovery dialog visibility notification");
        RunPostCommitAction(
            () => _notifyPrintRecoveredReceiptCanExecuteChanged?.Invoke(),
            "show recovery dialog print command notification");
    }

    private void CloseCardRecoveryResultDialog()
    {
        var activeSessionSource = _activeSessionRecoveryDialogActionSource;
        _activeSessionRecoveryDialogActionSource = null;
        IsCardRecoveryResultDialogOpen = false;
        CardRecoveryResultDialog = null;
        _cardRecoveryDialogReceipt = null;
        try
        {
            RunPostCommitAction(
                () => _notifyPropertyChanged?.Invoke(nameof(IsCardRecoveryResultDialogOpen)),
                "close recovery dialog visibility notification");
            RunPostCommitAction(
                () => _notifyPropertyChanged?.Invoke(nameof(CardRecoveryResultDialog)),
                "close recovery dialog property notification");
            RunPostCommitAction(
                () => _notifyPrintRecoveredReceiptCanExecuteChanged?.Invoke(),
                "close recovery dialog print command notification");
        }
        finally
        {
            // 任一 UI 订阅者失败都不能遗失 active-session 等待者的完成信号。
            activeSessionSource?.TrySetResult(ActiveSessionRecoveryDialogAction.Close);
        }
    }

    private void CompleteActiveSessionRecoveryDialog(ActiveSessionRecoveryDialogAction action)
    {
        var activeSessionSource = _activeSessionRecoveryDialogActionSource;
        _activeSessionRecoveryDialogActionSource = null;
        try
        {
            CloseCardRecoveryResultDialog();
        }
        finally
        {
            // 即使关闭弹窗时 UI 通知抛出致命异常，也必须先释放 active-session 等待者。
            activeSessionSource?.TrySetResult(action);
        }
    }

    private bool CanPrintRecoveredReceipt()
    {
        return CardRecoveryResultDialog?.CanPrintReceipt == true &&
            _cardRecoveryDialogReceipt is not null &&
            CanUsePrintReceiptPermission();
    }

    private bool CanUsePrintReceiptPermission()
    {
        // 关键逻辑：恢复弹窗可能在主页面之外出现，打印按钮也必须服从当前收银员权限。
        return _canPrintReceipt?.Invoke() ?? true;
    }

    private async Task PrintRecoveredReceiptAsync()
    {
        // 关键逻辑：命令可能被测试或脚本直接调用，不能只依赖按钮禁用来拦截无权限手动打印。
        var receipt = _cardRecoveryDialogReceipt;
        if (receipt is null || !CanPrintRecoveredReceipt())
        {
            return;
        }

        var result = await PrintReceiptAsync(receipt, ReceiptPrintReason.CardAuto);
        // 恢复弹窗里的打印是员工手动触发的补打，与其他补打入口一样按打印结果留痕。
        OperationAuditEvents.RecordAction(
            _operationAuditLogger,
            OperationAuditTypes.ReceiptReprint,
            result.Succeeded ? "Succeeded" : "Failed",
            GetSession(),
            reasonCode: "CARD_RECOVERY",
            safeMessage: result.Succeeded ? null : result.Message,
            orderGuid: receipt.OrderGuid.ToString("D"));
    }

    private async Task<IReadOnlyList<ReceiptPreviewRow>> BuildReceiptPreviewRowsAsync(ReceiptDetails receipt)
    {
        var settings = ReceiptPrinterSettings.Default;
        if (_receiptPrinterSettingsStore is not null)
        {
            try
            {
                settings = await _receiptPrinterSettingsStore.LoadAsync();
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException and
                not OutOfMemoryException and
                not StackOverflowException)
            {
                settings = ReceiptPrinterSettings.Default;
            }
        }

        try
        {
            return _receiptTextFormatter.Build(receipt, settings, receipt.SoldAt).PreviewRows;
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException and
            not OutOfMemoryException and
            not StackOverflowException)
        {
            return [];
        }
    }

    private async Task<ReceiptPrintResult> PrintReceiptAsync(ReceiptDetails receipt, ReceiptPrintReason reason)
    {
        return await _printReceiptAsync!(receipt, reason);
    }

    private static string? GetCardRecoveryResponseCode(LocalOrder order)
    {
        return order.Payments
            .FirstOrDefault(payment => payment.Method == PaymentMethodKind.Card)?
            .CardTransactions?
            .FirstOrDefault()?
            .ResponseCode;
    }

    private static string? GetCardRecoveryResponseText(LocalOrder order)
    {
        return order.Payments
            .FirstOrDefault(payment => payment.Method == PaymentMethodKind.Card)?
            .CardTransactions?
            .FirstOrDefault()?
            .ResponseText;
    }

    private static void LogRecoveredCardOrderCompleted(LocalOrder order)
    {
        var evidence = GetCardRecoveryEvidence(order);
        LinklyJsonLog.Write(
            "CardRecovery",
            "card-recovery",
            "power-fail-recovery",
            "order-completed",
            sessionId: evidence.SessionId,
            success: true,
            details: new
            {
                timestamp = DateTimeOffset.Now,
                certCase = "4.1.2",
                orderGuid = order.OrderGuid,
                transactionReference = evidence.TransactionReference,
                evidence.TxnRef,
                evidence.SessionId,
                reason = "4.1.2"
            });
    }

    private static CardRecoveryEvidence GetCardRecoveryEvidence(LocalOrder order)
    {
        var cardPayment = order.Payments.FirstOrDefault(payment => payment.Method == PaymentMethodKind.Card);
        var cardTransaction = cardPayment?.CardTransactions?.FirstOrDefault();
        var txnRef = NormalizeEvidenceValue(cardTransaction?.TxnRef) ?? TryReadLinklyBackendTxnRef(cardPayment?.Reference);
        var sessionId = LinklyBackendPaymentReference.TryGetPrintMarker(cardPayment?.Reference, out _, out var markerSessionId)
            ? NormalizeEvidenceValue(markerSessionId)
            : null;
        return new CardRecoveryEvidence(
            NormalizeEvidenceValue(sessionId) ?? NormalizeEvidenceValue(txnRef) ?? order.OrderGuid.ToString("D"),
            NormalizeEvidenceValue(txnRef),
            NormalizeEvidenceValue(sessionId));
    }

    private static string? TryReadLinklyBackendTxnRef(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) ||
            !reference.StartsWith($"{LinklyBackendPaymentReference.Prefix}:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = reference.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? NormalizeEvidenceValue(parts[1]) : null;
    }

    private static string? NormalizeEvidenceValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool DenyPermission(string permissionCode) => false;

    private sealed record CardRecoveryEvidence(string TransactionReference, string? TxnRef, string? SessionId);

    private static bool ShouldRetryCardPaymentRecovery(CardPaymentRecoveryOutcome outcome)
    {
        return outcome is CardPaymentRecoveryOutcome.None or
            CardPaymentRecoveryOutcome.Checking or
            CardPaymentRecoveryOutcome.Unknown;
    }
}
