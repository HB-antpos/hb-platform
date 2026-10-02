using System.Globalization;
using System.Runtime.ExceptionServices;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>
/// Encapsulates Card payment state machine — CTS lifecycle, cancellation tracking,
/// error overlay classification, Linkly fallback prompts, and result recovery.
/// PaymentViewModel delegates all Card-specific logic here so that
/// <c>AddTenderByMethodAsync</c> no longer mixes three payment methods inline.
/// </summary>
internal sealed class CardPaymentSession
{
    private readonly object _lifetimeSync = new();
    private CancellationTokenSource? _activeCardPaymentCts;
    private CancellationTokenSource? _manuallyCancelledCardPaymentCts;
    private CancellationTokenSource? _shutdownCancellationSource;
    private Task _shutdownCancellationTask = Task.CompletedTask;
    private bool _cardPaymentCancellationRequested;
    private bool _awaitingLateCardResultAfterManualCancel;
    private bool _cardPaymentResultUnknownRequiresRecovery;
    private CardPaymentHandoffCandidate? _cardPaymentHandoffCandidate;
    private bool _cardPaymentHandoffQualificationPending;
    private bool _cardPaymentHandoffInProgress;
    private long _cardPaymentHandoffQualificationGeneration;
    private bool _disposed;
    private CardRecoveryAttemptKey? _recoveryAttemptKey;
    private Guid? _recoveryOrderGuid;
    private string? _unknownResultStatusKey;
    private string? _unknownResultStatusMessage;
    private TaskCompletionSource<bool>? _pendingLinklyFallbackPrompt;

    private readonly PaymentViewModel _vm;

    public CardPaymentSession(PaymentViewModel vm)
    {
        _vm = vm;
    }

    // ── Read-only queries ──

    public bool IsActive => _activeCardPaymentCts is not null;
    public bool HasUnknownResult => _cardPaymentResultUnknownRequiresRecovery;
    public bool IsAwaitingLateResult => _awaitingLateCardResultAfterManualCancel;
    public bool HasPendingFallbackPrompt => _pendingLinklyFallbackPrompt is not null;

    public CardRecoveryAttemptKey? RecoveryAttemptKey => _recoveryAttemptKey;

    public Guid? RecoveryOrderGuid => _recoveryOrderGuid;

    public void SetPersistenceRecoveryOrder(Guid orderGuid)
    {
        // 中文注释：落盘未知后保留工作流冻结的订单身份，恢复成功只能解除该订单的付款页锁。
        if (orderGuid != Guid.Empty)
        {
            _recoveryOrderGuid = orderGuid;
        }
    }

    // ── State accessors (used by PaymentViewModel) ──

    public void SetResultUnknownRecoveryRequired(bool value)
    {
        _cardPaymentResultUnknownRequiresRecovery = value;
        _vm.OnCardResultUnknownRecoveryChanged(value);
        if (!value)
        {
            Interlocked.Increment(ref _cardPaymentHandoffQualificationGeneration);
            _cardPaymentHandoffQualificationPending = false;
            _cardPaymentHandoffCandidate = null;
            _recoveryAttemptKey = null;
            _recoveryOrderGuid = null;
            _unknownResultStatusKey = null;
            _unknownResultStatusMessage = null;
            if (_vm.CardPaymentErrorOverlay is { PrimaryActionKind:
                    CardPaymentErrorOverlayPrimaryActionKind.RecoverPrevious } overlay)
            {
                overlay.IsOpen = false;
                _vm.CardPaymentErrorPrimaryActionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public CancellationTokenSource? ActiveCardPaymentCts => _activeCardPaymentCts;

    // ── Card payment entry: BEGIN ──

    public CancellationTokenSource BeginCardPayment()
    {
        Interlocked.Increment(ref _cardPaymentHandoffQualificationGeneration);
        _cardPaymentHandoffCandidate = null;
        _cardPaymentHandoffQualificationPending = false;
        _recoveryAttemptKey = null;
        _recoveryOrderGuid = null;
        _cardPaymentCancellationRequested = false;
        _awaitingLateCardResultAfterManualCancel = false;
        _activeCardPaymentCts?.Dispose();
        _activeCardPaymentCts = new CancellationTokenSource();
        _vm.IsCardPaymentInProgress = true;
        _vm.IsPaymentInteractionLocked = true;
        _vm.SetStatus("payment.status.cardProcessing");
        return _activeCardPaymentCts;
    }

    // ── Card payment exit: END (finally block) ──

    public void EndCardPayment(CancellationTokenSource? cardPaymentCts)
    {
        CancellationTokenSource? active;
        Task shutdownCancellationTask;
        lock (_lifetimeSync)
        {
            if (!ReferenceEquals(_activeCardPaymentCts, cardPaymentCts))
            {
                return;
            }

            active = _activeCardPaymentCts;
            _activeCardPaymentCts = null;
            if (ReferenceEquals(_manuallyCancelledCardPaymentCts, cardPaymentCts))
            {
                _manuallyCancelledCardPaymentCts = null;
            }

            shutdownCancellationTask = ReferenceEquals(_shutdownCancellationSource, active)
                ? _shutdownCancellationTask
                : Task.CompletedTask;
            if (ReferenceEquals(_shutdownCancellationSource, active))
            {
                _shutdownCancellationSource = null;
            }
        }

        _cardPaymentCancellationRequested = false;
        _vm.IsCardPaymentInProgress = false;
        _vm.IsPaymentInteractionLocked = _vm.IsShuttingDown || HasUnknownResult;
        DisposeAfterCancellation(active, shutdownCancellationTask);
        _vm.NotifyPaymentCommandStates();
    }

    // ── Manual cancellation ──

    public void Cancel()
    {
        if (_activeCardPaymentCts is null || _activeCardPaymentCts.IsCancellationRequested)
        {
            return;
        }

        _cardPaymentCancellationRequested = true;
        _awaitingLateCardResultAfterManualCancel = true;
        _manuallyCancelledCardPaymentCts = _activeCardPaymentCts;
        _activeCardPaymentCts.Cancel();
        _vm.IsCardPaymentInProgress = false;
        _vm.IsPaymentInteractionLocked = false;
        _vm.SetStatus("payment.status.cardCancelled");
        _vm.NotifyPaymentCommandStates();
    }

    public Task CancelForShutdownAsync()
    {
        lock (_lifetimeSync)
        {
            if (_activeCardPaymentCts is null || _activeCardPaymentCts.IsCancellationRequested)
            {
                return _shutdownCancellationTask;
            }

            var cancellation = _activeCardPaymentCts;
            _shutdownCancellationSource = cancellation;
            // 退出取消不是人工撤销：不清空 session、不释放交互锁，让工作流把已越过提交边界的结果持久化为待恢复。
            _shutdownCancellationTask = Task.Run(() =>
            {
                try
                {
                    cancellation.Cancel();
                }
                catch (Exception ex)
                {
                    var fatalException = FindFatalException(ex);
                    if (fatalException is not null)
                    {
                        // CancellationTokenSource 会把回调异常包装进 AggregateException；
                        // OOM/SO 必须按原异常继续传播，不能让进程在不可靠状态下继续退出流程。
                        ExceptionDispatchInfo.Capture(fatalException).Throw();
                    }

                    ConsoleLog.WriteError(
                        "Shutdown",
                        $"card payment shutdown cancellation failed error={ex.GetType().Name} message={ex.Message}",
                        exception: ex);
                }
            });
            return _shutdownCancellationTask;
        }
    }

    public void DetachCanceledActiveCardPayment()
    {
        if (_activeCardPaymentCts?.IsCancellationRequested != true)
        {
            return;
        }

        if (ReferenceEquals(_manuallyCancelledCardPaymentCts, _activeCardPaymentCts))
        {
            _manuallyCancelledCardPaymentCts = null;
        }

        _activeCardPaymentCts = null;
        _cardPaymentCancellationRequested = false;
    }

    // ── Cancellation classification ──

    public bool IsManualCancellation(CancellationTokenSource? cardPaymentCts)
    {
        return _cardPaymentCancellationRequested || ReferenceEquals(_manuallyCancelledCardPaymentCts, cardPaymentCts);
    }

    public void SetCancellationStatus(bool wasManuallyCancelled)
    {
        _vm.SetStatus(wasManuallyCancelled ? "payment.status.cardCancelled" : "payment.status.cardTimedOut");
    }

    public void ResetManualCancellationState()
    {
        _awaitingLateCardResultAfterManualCancel = false;
    }

    // ── Exception handling ──

    public void HandleOperationCanceledException(CancellationTokenSource? cardPaymentCts, int paymentEntryVersion)
    {
        if (!_vm.IsCurrentPaymentEntry(paymentEntryVersion))
        {
            return;
        }

        SetCancellationStatus(IsManualCancellation(cardPaymentCts));
        ResetManualCancellationState();
        _vm.NotifyPaymentCommandStates();
    }

    public void HandleUnexpectedException(Exception ex, int paymentEntryVersion)
    {
        ConsoleLog.Write("CardPayment", $"unexpected card payment exception: {ex}");
        if (!_vm.IsCurrentPaymentEntry(paymentEntryVersion))
        {
            return;
        }

        var overlay = CardPaymentErrorOverlayViewModel.Unexpected();
        overlay.IsOpen = true;
        _vm.CardPaymentErrorOverlay = overlay;
        _vm.SetStatus("payment.card.status.failed", ex.Message);
        _vm.NotifyPaymentCommandStates();
    }

    // ── Result classification ──

    public async Task<bool> TryHandleCancelledResultAsync(
        PaymentTenderAttemptResult result,
        CancellationTokenSource? cardPaymentCts,
        bool cardPaymentWasManuallyCancelled)
    {
        if (cardPaymentCts?.IsCancellationRequested != true)
        {
            return false;
        }

        if (result.Succeeded && result.Tender is not null)
        {
            return false;
        }

        if (result.CardResult?.RequiresRecovery == true)
        {
            return await TryHandleFailedResultAsync(result);
        }

        if (cardPaymentWasManuallyCancelled && result.CardResult?.Outcome == CardPaymentTerminalOutcome.Cancelled)
        {
            SetCancellationStatus(wasManuallyCancelled: true);
        }
        else if (!cardPaymentWasManuallyCancelled)
        {
            SetCancellationStatus(wasManuallyCancelled: false);
        }
        else
        {
            _vm.SetStatus(result.StatusKey, result.StatusMessage);
        }

        ResetManualCancellationState();
        _vm.NotifyPaymentCommandStates();
        return true;
    }

    public async Task<bool> TryHandleFailedResultAsync(PaymentTenderAttemptResult result)
    {
        if (result.CardResult?.RequiresRecovery == true)
        {
            var qualificationGeneration = Interlocked.Increment(
                ref _cardPaymentHandoffQualificationGeneration);
            SetResultUnknownRecoveryRequired(true);
            _cardPaymentHandoffCandidate = null;
            CardRecoveryAttemptKey? recoveryAttemptKey = result.RecoveryAttemptKey is { AttemptGuid: var attemptGuid } key &&
                attemptGuid != Guid.Empty
                ? key
                : null;
            Guid? recoveryOrderGuid = result.RecoveryOrderGuid is { } orderGuid && orderGuid != Guid.Empty
                ? orderGuid
                : null;
            _recoveryAttemptKey = recoveryAttemptKey;
            _recoveryOrderGuid = recoveryOrderGuid;
            _unknownResultStatusKey = result.StatusKey;
            _unknownResultStatusMessage = result.StatusMessage;
            var shouldQualifyHandoff =
                recoveryAttemptKey is not null &&
                recoveryOrderGuid is not null &&
                _vm.NavigationActions.PrepareCardPaymentHandoffAsync is not null;
            _cardPaymentHandoffQualificationPending = shouldQualifyHandoff;
            _vm.IsPaymentInteractionLocked = true;
            RestoreUnknownResultStatus();
            ResetManualCancellationState();
            ShowOverlayIfTerminalError(result);
            if (shouldQualifyHandoff &&
                recoveryAttemptKey is { } capturedAttemptKey &&
                recoveryOrderGuid is { } capturedOrderGuid &&
                _vm.CardPaymentErrorOverlay is { } targetOverlay)
            {
                if (!await PrepareCardPaymentHandoffAsync(
                        qualificationGeneration,
                        capturedAttemptKey,
                        capturedOrderGuid,
                        targetOverlay))
                {
                    return true;
                }
            }
            else if (qualificationGeneration != Volatile.Read(
                         ref _cardPaymentHandoffQualificationGeneration))
            {
                return true;
            }
            else if (shouldQualifyHandoff)
            {
                // 没有可绑定的目标遮罩时结束本代资格窗口，避免永久保持 pending。
                _cardPaymentHandoffQualificationPending = false;
            }
            _vm.NotifyPaymentCommandStates();
            return true;
        }

        ShowOverlayIfTerminalError(result);

        if (TrySetCardTerminalFailureStatus(result))
        {
            ResetManualCancellationState();
            _vm.NotifyPaymentCommandStates();
            return true;
        }

        _vm.SetStatus(result.StatusKey, result.StatusMessage);
        ResetManualCancellationState();
        _vm.NotifyPaymentCommandStates();
        return true;
    }

    private bool TrySetCardTerminalFailureStatus(PaymentTenderAttemptResult result)
    {
        var disposition = result.CardResult;
        if (disposition?.PreserveStatus == true)
        {
            return false;
        }

        if (disposition?.Outcome == CardPaymentTerminalOutcome.Cancelled)
        {
            _vm.SetStatus("payment.status.cardCancelled");
            return true;
        }

        if (disposition?.Outcome == CardPaymentTerminalOutcome.TimedOut)
        {
            _vm.SetStatus("payment.status.cardTimedOut");
            return true;
        }

        return false;
    }

    // ── Error overlay ──

    public void CloseErrorOverlay()
    {
        CompletePendingLinklyFallbackPrompt(confirmed: false);
        if (_vm.CardPaymentErrorOverlay is { } overlay)
        {
            if (overlay.PrimaryActionKind == CardPaymentErrorOverlayPrimaryActionKind.RecoverPrevious)
            {
                Interlocked.Increment(ref _cardPaymentHandoffQualificationGeneration);
                _cardPaymentHandoffQualificationPending = false;
                _cardPaymentHandoffCandidate = null;
                // 关闭前转为“打开异常中心”动作，保证当前锁定订单关闭后仍可继续恢复。
                ConfigureRecoveryCenterPrimaryAction(overlay);
            }

            overlay.IsOpen = false;
        }

        ReleaseFallbackPromptLockIfIdle();
        _vm.CardPaymentErrorPrimaryActionCommand.NotifyCanExecuteChanged();
    }

    public bool CanExecuteErrorPrimaryAction()
    {
        if (_vm.CardPaymentErrorOverlay is not { HasPrimaryAction: true } overlay)
        {
            return false;
        }

        return overlay.PrimaryActionKind switch
        {
            CardPaymentErrorOverlayPrimaryActionKind.ConfirmFallback => overlay.IsOpen,
            CardPaymentErrorOverlayPrimaryActionKind.RecoverPrevious =>
                _cardPaymentResultUnknownRequiresRecovery &&
                !_cardPaymentHandoffQualificationPending &&
                !_cardPaymentHandoffInProgress &&
                !_vm.IsShuttingDown &&
                (_cardPaymentHandoffCandidate is not null
                    ? _vm.NavigationActions.HandoffCardPaymentAsync is not null
                    : _vm.OpenCardRecoveryCenterCommand.CanExecute(null)),
            _ => false
        };
    }

    private async Task<bool> PrepareCardPaymentHandoffAsync(
        long qualificationGeneration,
        CardRecoveryAttemptKey recoveryAttemptKey,
        Guid recoveryOrderGuid,
        CardPaymentErrorOverlayViewModel targetOverlay)
    {
        if (qualificationGeneration != Volatile.Read(ref _cardPaymentHandoffQualificationGeneration) ||
            _disposed ||
            _vm.IsShuttingDown ||
            !_cardPaymentResultUnknownRequiresRecovery ||
            !Equals(_recoveryAttemptKey, recoveryAttemptKey) ||
            _recoveryOrderGuid != recoveryOrderGuid ||
            !ReferenceEquals(_vm.CardPaymentErrorOverlay, targetOverlay) ||
            targetOverlay.PrimaryActionKind != CardPaymentErrorOverlayPrimaryActionKind.RecoverPrevious)
        {
            return false;
        }

        var prepare = _vm.NavigationActions.PrepareCardPaymentHandoffAsync;
        if (prepare is null)
        {
            _cardPaymentHandoffCandidate = null;
            _cardPaymentHandoffQualificationPending = false;
            ConfigureRecoveryCenterPrimaryAction(targetOverlay);
            _vm.CardPaymentErrorPrimaryActionCommand.NotifyCanExecuteChanged();
            return true;
        }

        CardPaymentHandoffCandidate? candidate = null;
        Exception? qualificationException = null;
        try
        {
            candidate = await prepare(_vm.CreateCardPaymentHandoffRequest());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            qualificationException = ex;
        }

        if (qualificationGeneration != Volatile.Read(ref _cardPaymentHandoffQualificationGeneration) ||
            _disposed ||
            _vm.IsShuttingDown ||
            !_cardPaymentResultUnknownRequiresRecovery ||
            !Equals(_recoveryAttemptKey, recoveryAttemptKey) ||
            _recoveryOrderGuid != recoveryOrderGuid ||
            !ReferenceEquals(_vm.CardPaymentErrorOverlay, targetOverlay) ||
            targetOverlay.PrimaryActionKind != CardPaymentErrorOverlayPrimaryActionKind.RecoverPrevious)
        {
            return false;
        }

        _cardPaymentHandoffCandidate = candidate;
        _cardPaymentHandoffQualificationPending = false;
        if (qualificationException is not null)
        {
            // 查询失败必须保持当前订单锁定；不能把持久化异常升级成清单或导航动作。
            ConsoleLog.WriteError(
                "CardPayment",
                $"card payment handoff qualification failed error={qualificationException.GetType().Name}",
                exception: qualificationException);
            RestoreUnknownResultStatus();
        }

        if (_cardPaymentHandoffCandidate is not null)
        {
            ConfigureHandoffPrimaryAction(targetOverlay);
        }
        else
        {
            ConfigureRecoveryCenterPrimaryAction(targetOverlay);
        }

        _vm.CardPaymentErrorPrimaryActionCommand.NotifyCanExecuteChanged();
        return true;
    }

    public bool CanOpenRecoveryCenter() =>
        !_disposed &&
        !_vm.IsShuttingDown &&
        !_cardPaymentHandoffQualificationPending &&
        !_cardPaymentHandoffInProgress &&
        !_vm.CardPaymentErrorPrimaryActionCommand.IsRunning &&
        _vm.NavigationActions.OpenCardRecoveryCenter is not null;

    public async Task OpenRecoveryCenterAsync()
    {
        if (!CanOpenRecoveryCenter())
        {
            return;
        }

        await OpenRecoveryCenterCoreAsync();
    }

    private async Task OpenRecoveryCenterCoreAsync()
    {
        if (_cardPaymentResultUnknownRequiresRecovery &&
            _recoveryAttemptKey is { } attemptKey &&
            _recoveryOrderGuid is { } orderGuid &&
            _vm.CardPaymentErrorOverlay is { PrimaryActionKind: CardPaymentErrorOverlayPrimaryActionKind.RecoverPrevious } overlay)
        {
            // 关闭弹窗会失效旧资格；进入恢复中心时重新核验持久化的原订单，不能按相同商品猜测身份。
            var generation = Interlocked.Increment(ref _cardPaymentHandoffQualificationGeneration);
            _cardPaymentHandoffQualificationPending = true;
            if (!await PrepareCardPaymentHandoffAsync(generation, attemptKey, orderGuid, overlay))
            {
                return;
            }
        }

        // 原订单已安全留在恢复队列后才释放活动购物车，避免确认未支付的草稿被同一笔订单挡住。
        if (_cardPaymentHandoffCandidate is { } candidate &&
            !await TryHandoffCardPaymentAsync(candidate))
        {
            return;
        }

        _vm.NavigationActions.OpenCardRecoveryCenter?.Invoke();
    }

    public async Task ExecuteErrorPrimaryActionAsync()
    {
        if (!CanExecuteErrorPrimaryAction())
        {
            return;
        }

        if (_vm.CardPaymentErrorOverlay?.PrimaryActionKind == CardPaymentErrorOverlayPrimaryActionKind.ConfirmFallback)
        {
            CompletePendingLinklyFallbackPrompt(confirmed: true);
            _vm.CardPaymentErrorOverlay.IsOpen = false;
            ReleaseFallbackPromptLockIfIdle();
            _vm.CardPaymentErrorPrimaryActionCommand.NotifyCanExecuteChanged();
            return;
        }

        var candidate = _cardPaymentHandoffCandidate;
        if (candidate is null)
        {
            await OpenRecoveryCenterCoreAsync();
            _vm.CardPaymentErrorPrimaryActionCommand.NotifyCanExecuteChanged();
            return;
        }

        await TryHandoffCardPaymentAsync(candidate);
    }

    private async Task<bool> TryHandoffCardPaymentAsync(CardPaymentHandoffCandidate candidate)
    {
        var handoff = _vm.NavigationActions.HandoffCardPaymentAsync;
        if (handoff is null || _cardPaymentHandoffInProgress)
        {
            return false;
        }

        var completed = false;
        _cardPaymentHandoffInProgress = true;
        try
        {
            _vm.SetStatus("payment.card.error.overlay.activeSession.handingOff");
            if (!await handoff(candidate, _vm.CreateCardPaymentHandoffRequest()))
            {
                RestoreUnknownResultStatus();
            }
            else if (!(completed = _vm.CompleteCardPaymentHandoff()))
            {
                RestoreUnknownResultStatus();
                _vm.IsPaymentInteractionLocked = true;
                _vm.NotifyPaymentCommandStates();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError(
                "CardPayment",
                $"card payment handoff failed error={ex.GetType().Name}",
                exception: ex);
            SetResultUnknownRecoveryRequired(true);
            _vm.IsPaymentInteractionLocked = true;
            RestoreUnknownResultStatus();
            _vm.NotifyPaymentCommandStates();
        }
        finally
        {
            // 只释放入口互斥；致命异常继续原样传播，不能被通知异常覆盖。
            _cardPaymentHandoffInProgress = false;
        }

        // 致命异常会绕过上面的普通异常 catch；不得在 finally 中执行 UI 通知覆盖原异常。
        _vm.CardPaymentErrorPrimaryActionCommand.NotifyCanExecuteChanged();
        _vm.OpenCardRecoveryCenterCommand.NotifyCanExecuteChanged();
        return completed;
    }

    private void RestoreUnknownResultStatus()
    {
        _vm.SetStatus(
            _unknownResultStatusKey ?? "payment.card.resultUnknown",
            _unknownResultStatusMessage);
    }

    // ── Linkly fallback ──

    public Task<bool> ConfirmLinklyFallbackAsync(
        LinklyFallbackPromptRequest request,
        CancellationToken cancellationToken)
    {
        CompletePendingLinklyFallbackPrompt(confirmed: false);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(false);
        }

        var prompt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingLinklyFallbackPrompt = prompt;
        _vm.CardPaymentErrorOverlay = CardPaymentErrorOverlayViewModel.Fallback(FormatLinklyFallbackPromptMessage(request));
        _vm.CardPaymentErrorOverlay.IsOpen = true;
        _vm.IsPaymentInteractionLocked = true;
        _vm.CardPaymentErrorPrimaryActionCommand.NotifyCanExecuteChanged();
        _vm.NotifyPaymentCommandStates();
        cancellationToken.Register(() => CompletePendingLinklyFallbackPrompt(confirmed: false));
        return prompt.Task;
    }

    public void CompletePendingLinklyFallbackPrompt(bool confirmed)
    {
        var prompt = _pendingLinklyFallbackPrompt;
        if (prompt is null)
        {
            return;
        }

        _pendingLinklyFallbackPrompt = null;
        prompt.TrySetResult(confirmed);
    }

    public void ReleaseFallbackPromptLockIfIdle()
    {
        if (_activeCardPaymentCts is null && !_cardPaymentResultUnknownRequiresRecovery)
        {
            _vm.IsPaymentInteractionLocked = false;
            _vm.NotifyPaymentCommandStates();
        }
    }

    private string FormatLinklyFallbackPromptMessage(LinklyFallbackPromptRequest request)
    {
        return string.Format(
            CultureInfo.CurrentCulture,
            _vm.T("payment.card.error.overlay.fallback.message"),
            FormatLinklyModeDisplayName(request.NextMode.ToString()));
    }

    private string FormatLinklyModeDisplayName(string? modeText)
    {
        var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(modeText, LinklyConnectionMode.LocalIp);
        var key = mode switch
        {
            LinklyConnectionMode.CloudDirectSync => "settings.linkly.mode.cloudDirectSync",
            LinklyConnectionMode.CloudBackendAsync => "settings.linkly.mode.cloudBackendAsync",
            _ => "settings.linkly.mode.localIp"
        };

        return _vm.T(key);
    }

    private void ShowOverlayIfTerminalError(PaymentTenderAttemptResult result)
    {
        var overlay = result.CardResult?.ErrorKind switch
        {
            CardPaymentErrorKind.ConnectionFailed => CardPaymentErrorOverlayViewModel.ConnectionFailed(),
            CardPaymentErrorKind.PinpadOffline => CardPaymentErrorOverlayViewModel.PinpadOffline(),
            CardPaymentErrorKind.CloudCommunicationFailed => CardPaymentErrorOverlayViewModel.CloudCommunicationFailed(),
            CardPaymentErrorKind.ActiveSessionRequiresRecovery => CreateUnqualifiedRecoveryOverlay(),
            CardPaymentErrorKind.SquareCommunicationFailed => CardPaymentErrorOverlayViewModel.SquareCommunicationFailed(),
            CardPaymentErrorKind.Timeout => CardPaymentErrorOverlayViewModel.Timeout(),
            CardPaymentErrorKind.CardDeclined => CardPaymentErrorOverlayViewModel.CardDeclined(result.StatusMessage),
            _ => null
        };
        if (overlay is null)
            return;

        overlay.IsOpen = true;
        if (overlay.PrimaryActionKind == CardPaymentErrorOverlayPrimaryActionKind.RecoverPrevious)
        {
            ConfigureRecoveryCenterPrimaryAction(overlay);
        }

        _vm.CardPaymentErrorOverlay = overlay;
        _vm.CardPaymentErrorPrimaryActionCommand.NotifyCanExecuteChanged();
    }

    private void ConfigureRecoveryCenterPrimaryAction(CardPaymentErrorOverlayViewModel overlay)
    {
        overlay.PrimaryButtonTextKey =
            "payment.card.error.overlay.activeSession.openRecoveryCenter";
        overlay.HasPrimaryAction =
            !_cardPaymentHandoffQualificationPending &&
            _cardPaymentResultUnknownRequiresRecovery &&
            _vm.OpenCardRecoveryCenterCommand.CanExecute(null);
    }

    private void ConfigureHandoffPrimaryAction(CardPaymentErrorOverlayViewModel overlay)
    {
        overlay.PrimaryButtonTextKey =
            "payment.card.error.overlay.activeSession.handoff";
        overlay.HasPrimaryAction =
            !_vm.IsShuttingDown &&
            _vm.NavigationActions.HandoffCardPaymentAsync is not null;
    }

    private static CardPaymentErrorOverlayViewModel CreateUnqualifiedRecoveryOverlay()
    {
        var overlay = CardPaymentErrorOverlayViewModel.ActiveSessionRequiresRecovery();
        // 创建时先收起旧“恢复上一笔”入口；调用方核验现有导航后再一次性填充动作状态。
        overlay.HasPrimaryAction = false;
        return overlay;
    }

    // ── Dispose support ──

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _cardPaymentHandoffQualificationGeneration);
        _cardPaymentHandoffQualificationPending = false;
        _cardPaymentHandoffCandidate = null;
        CompletePendingLinklyFallbackPrompt(confirmed: false);
        CancellationTokenSource? orphanedManuallyCancelled;
        CancellationTokenSource? orphanedShutdownCancellation;
        Task shutdownCancellationTask;
        lock (_lifetimeSync)
        {
            shutdownCancellationTask = _shutdownCancellationTask;
            // active CTS 仍可能被 AddTenderAsync 使用；只有真实工作流 finally 的 EndCardPayment 可以释放它。
            orphanedManuallyCancelled = ReferenceEquals(
                _manuallyCancelledCardPaymentCts,
                _activeCardPaymentCts)
                ? null
                : _manuallyCancelledCardPaymentCts;
            if (orphanedManuallyCancelled is not null)
            {
                _manuallyCancelledCardPaymentCts = null;
            }

            orphanedShutdownCancellation = ReferenceEquals(
                _shutdownCancellationSource,
                _activeCardPaymentCts)
                ? null
                : _shutdownCancellationSource;
            if (orphanedShutdownCancellation is not null)
            {
                _shutdownCancellationSource = null;
            }
        }

        DisposeAfterCancellation(orphanedManuallyCancelled, shutdownCancellationTask);
        if (!ReferenceEquals(orphanedShutdownCancellation, orphanedManuallyCancelled))
        {
            DisposeAfterCancellation(orphanedShutdownCancellation, shutdownCancellationTask);
        }
    }

    private static void DisposeAfterCancellation(
        CancellationTokenSource? cancellation,
        Task cancellationTask)
    {
        if (cancellation is null)
        {
            return;
        }

        if (cancellationTask.IsCompleted)
        {
            cancellation.Dispose();
            return;
        }

        _ = cancellationTask.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Exception? FindFatalException(Exception exception)
    {
        if (exception is OutOfMemoryException or StackOverflowException)
        {
            return exception;
        }

        if (exception is AggregateException aggregateException)
        {
            foreach (var innerException in aggregateException.InnerExceptions)
            {
                var fatalException = FindFatalException(innerException);
                if (fatalException is not null)
                {
                    return fatalException;
                }
            }

            return null;
        }

        return exception.InnerException is null
            ? null
            : FindFatalException(exception.InnerException);
    }
}
