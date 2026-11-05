using System.Globalization;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.ViewModels;

public partial class PaymentViewModel
{
    [ObservableProperty]
    private bool _isManualCardDialogOpen;

    [ObservableProperty]
    private bool _isManualCardSuccessChecked;

    [ObservableProperty]
    private bool _isManualCardSavePending;

    [ObservableProperty]
    private decimal _manualCardAmount;

    [ObservableProperty]
    private string _manualCardDialogStatusMessage = string.Empty;

    private long _manualCardCartRevision;
    private int _manualCardEntryVersion;
    private PosSessionState? _manualCardSession;
    private PaymentTender[] _manualCardTenders = [];
    private Guid _manualCardConfirmationId;
    private bool _isManualCardSubmitting;
    private PaymentTender? _manualCardConfirmedTender;

    // 中文注释：分期续付的手动刷卡先占中央 claim 再开窗；这两个字段非空表示窗口背后有一笔已占用的续付操作。
    private Guid? _manualCardRepaymentOperationGuid;
    private InstallmentOrderSummary? _manualCardRepaymentOrder;

    public bool IsManualCardBackgroundEnabled => !IsManualCardDialogOpen && !IsManualCardSavePending;

    public IRelayCommand OpenManualCardCommand { get; private set; } = null!;
    public IRelayCommand CancelManualCardCommand { get; private set; } = null!;
    public IAsyncRelayCommand ConfirmManualCardCommand { get; private set; } = null!;
    public IAsyncRelayCommand RetryManualCardSaveCommand { get; private set; } = null!;

    private void InitializeManualCardCommands()
    {
        OpenManualCardCommand = new RelayCommand(OpenManualCard, CanOpenManualCard);
        CancelManualCardCommand = new RelayCommand(CancelManualCard, () => IsManualCardDialogOpen && !IsPaymentInteractionLocked);
        ConfirmManualCardCommand = new AsyncRelayCommand(ConfirmManualCardAsync,
            () => IsManualCardPaymentVisible && _paymentMethodSettingsReady &&
                  IsManualCardDialogOpen && IsManualCardSuccessChecked && !IsPaymentInteractionLocked);
        RetryManualCardSaveCommand = new AsyncRelayCommand(RetryManualCardSaveAsync,
            () => IsManualCardSavePending && !_isManualCardSubmitting && !IsShuttingDown && !RetryManualCardSaveCommand.IsRunning);
    }

    private bool CanOpenManualCard() =>
        IsManualCardPaymentVisible && IsPaymentInteractionEnabled && !_cardSession.IsActive &&
        !IsVoucherEntryDialogOpen && !IsInstallmentCustomerDialogOpen &&
        IsPaymentMode &&
        (IsInstallmentRepaymentMode
            ? Session.IsOnline && PaymentTenders.Count == 0 && GetPaymentTargetAmount() > 0m
            : !IsInstallmentPaymentEnabled &&
              _pendingVoucherUploadOrderGuid is null && _cart.RecoveryOwnerAttemptGuid is null &&
              !_cart.IsEmpty && !_cart.HasReturnLine && !_cart.HasNonIntegerQuantity && !_cart.HasZeroPriceLine &&
              !PaymentTenders.Any(tender => tender.Method == PaymentMethodKind.Card) &&
              GetExternalRemainingAmount() > 0m);

    private void OpenManualCard()
    {
        if (!CanOpenManualCard()) return;
        if (IsInstallmentRepaymentMode)
        {
            _ = OpenManualCardRepaymentAsync();
            return;
        }

        var amount = GetExternalRemainingAmount();
        if (!string.IsNullOrWhiteSpace(TenderAmountText) &&
            (!_workflowService.TryParseTenderedAmount(TenderAmountText, out var enteredAmount) || enteredAmount != amount))
        {
            SetStatus("payment.manualCard.finalAmountRequired");
            return;
        }

        // 确认的是窗口中冻结的金额和订单，不能把旧窗口的确认套到另一笔交易。
        ManualCardAmount = amount;
        _manualCardCartRevision = _cart.Revision;
        _manualCardEntryVersion = _paymentEntryVersion;
        _manualCardSession = Session;
        _manualCardTenders = PaymentTenders.ToArray();
        _manualCardConfirmationId = Guid.NewGuid();
        IsManualCardSuccessChecked = false;
        ManualCardDialogStatusMessage = string.Empty;
        IsManualCardDialogOpen = true;
    }

    /// <summary>
    /// 分期续付：先占中央 claim 再打开收款窗口。另一台收银机正在收款、余额变了、服务端没放开，都在收款之前拦下。
    /// </summary>
    private async Task OpenManualCardRepaymentAsync()
    {
        var order = _installmentRepaymentOrder;
        if (order is null) return;

        var target = GetPaymentTargetAmount();
        var amount = target;
        if (!string.IsNullOrWhiteSpace(TenderAmountText) &&
            !_workflowService.TryParseTenderedAmount(TenderAmountText, out amount))
        {
            SetStatus("payment.status.invalidAmount");
            return;
        }

        if (amount <= 0m || amount > target)
        {
            SetStatus("payment.status.invalidAmount");
            return;
        }

        IsPaymentInteractionLocked = true;
        try
        {
            using var grant = await AuthorizeAsync(Permissions.PosTerminal.Payment.Confirm,
                "manual-card-repayment", PaymentMethodKind.Card, amount);
            if (grant is null) return;
            using var activation = grant.Activate();
            if (!await _installmentOrderService.IsManualCardRepaymentSupportedAsync(Session))
            {
                SetStatus("payment.installment.status.manualCardRepaymentUnsupported");
                return;
            }

            var begin = await _installmentOrderService.BeginManualCardRepaymentAsync(order.OrderId, Session, amount);
            if (!begin.Succeeded || begin.OperationGuid is null)
            {
                SetStatus("payment.installment.status.actionFailed", begin.Message);
                return;
            }

            _manualCardRepaymentOperationGuid = begin.OperationGuid;
            _manualCardRepaymentOrder = order;
            ManualCardAmount = begin.Amount;
            IsManualCardSuccessChecked = false;
            ManualCardDialogStatusMessage = string.Empty;
            IsManualCardDialogOpen = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteError("InstallmentAudit", $"manual card repayment begin failed error={ex.GetType().Name}", null, ex);
            SetStatus("payment.installment.status.actionFailed", ex.Message);
        }
        finally
        {
            IsPaymentInteractionLocked = false;
            NotifyPaymentCommandStates();
        }
    }

    private async Task ConfirmManualCardRepaymentAsync(Guid operationGuid)
    {
        var order = _manualCardRepaymentOrder;
        if (order is null) return;

        IsPaymentInteractionLocked = true;
        _isManualCardSubmitting = true;
        var auditCart = OperationAuditEvents.CaptureCart(_cart.Lines);
        var tender = new PaymentTender(PaymentMethodKind.Card, ManualCardAmount, ManualCardPaymentReference.Format(operationGuid));
        try
        {
            using var grant = await AuthorizeAsync(Permissions.PosTerminal.Payment.Confirm,
                "confirm-payment", PaymentMethodKind.Card, ManualCardAmount);
            if (grant is null) return;
            using var activation = grant.Activate();
            var result = await _installmentOrderService.CompleteManualCardRepaymentAsync(operationGuid);
            ClearManualCardRepayment();
            await FinishInstallmentRepaymentAsync(order, tender, result, auditCart);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 中文注释：确认之后款项已经收了，结果不明也不能让收银员重收；窗口关闭，分期单保持占用等恢复或主管结案。
            ConsoleLog.WriteError("InstallmentAudit", $"manual card repayment confirm failed error={ex.GetType().Name}", null, ex);
            ClearManualCardRepayment();
            SetStatus("payment.installment.status.actionFailed", ex.Message);
        }
        finally
        {
            _isManualCardSubmitting = false;
            IsPaymentInteractionLocked = false;
            NotifyPaymentCommandStates();
        }
    }

    private async Task CancelManualCardRepaymentAsync(Guid operationGuid)
    {
        IsPaymentInteractionLocked = true;
        try
        {
            if (await _installmentOrderService.CancelManualCardRepaymentAsync(operationGuid))
            {
                ClearManualCardRepayment();
                SetStatus("payment.installment.status.manualCardRepaymentCancelled");
                return;
            }

            SetStatus("payment.installment.status.manualCardRepaymentCancelFailed");
            ManualCardDialogStatusMessage = StatusMessage;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            SetStatus("payment.installment.status.manualCardRepaymentCancelFailed");
            ManualCardDialogStatusMessage = StatusMessage;
        }
        finally
        {
            IsPaymentInteractionLocked = false;
            NotifyPaymentCommandStates();
        }
    }

    private void ClearManualCardRepayment()
    {
        _manualCardRepaymentOperationGuid = null;
        _manualCardRepaymentOrder = null;
        IsManualCardSuccessChecked = false;
        IsManualCardDialogOpen = false;
        TenderAmountText = string.Empty;
    }

    private void CancelManualCard()
    {
        if (!IsManualCardDialogOpen || IsPaymentInteractionLocked) return;
        if (_manualCardRepaymentOperationGuid is { } repaymentOperationGuid)
        {
            _ = CancelManualCardRepaymentAsync(repaymentOperationGuid);
            return;
        }

        IsManualCardSuccessChecked = false;
        IsManualCardDialogOpen = false;
    }

    private bool IsManualCardSnapshotCurrent() =>
        IsManualCardPaymentVisible && _paymentMethodSettingsReady &&
        !IsShuttingDown && !_cardSession.IsActive && !_cardSession.HasUnknownResult &&
        _manualCardEntryVersion == _paymentEntryVersion && _manualCardCartRevision == _cart.Revision &&
        IsManualCardSessionCurrent() &&
        _manualCardTenders.SequenceEqual(PaymentTenders) &&
        IsPaymentMode && !IsInstallmentPaymentEnabled && !IsInstallmentRepaymentMode &&
        ManualCardAmount == GetExternalRemainingAmount();

    private bool IsManualCardSessionCurrent() => _manualCardSession is not null &&
        _manualCardSession.StoreCode == Session.StoreCode && _manualCardSession.DeviceCode == Session.DeviceCode &&
        _manualCardSession.CashierId == Session.CashierId && _manualCardSession.CashierName == Session.CashierName;

    private bool CanSaveConfirmedManualCard() =>
        !IsShuttingDown && !_cardSession.IsActive && !_cardSession.HasUnknownResult &&
        _manualCardEntryVersion == _paymentEntryVersion && _manualCardCartRevision == _cart.Revision &&
        IsManualCardSessionCurrent() && _manualCardConfirmedTender is not null &&
        _manualCardTenders.Append(_manualCardConfirmedTender).SequenceEqual(PaymentTenders);

    private async Task ConfirmManualCardAsync()
    {
        if (!IsManualCardPaymentVisible || !_paymentMethodSettingsReady ||
            !IsManualCardDialogOpen || !IsManualCardSuccessChecked || IsPaymentInteractionLocked ||
            _cardSession.IsActive || _cardSession.HasUnknownResult || IsShuttingDown) return;

        if (_manualCardRepaymentOperationGuid is { } repaymentOperationGuid)
        {
            await ConfirmManualCardRepaymentAsync(repaymentOperationGuid);
            return;
        }

        // 在首个 await 前锁住所有付款入口；人工确认不创建或调用任何联网刷卡会话。
        IsPaymentInteractionLocked = true;
        _isManualCardSubmitting = true;
        try
        {
            using var cardGrant = await AuthorizeAsync(GetTenderPermission(PaymentMethodKind.Card),
                "manual-card-confirm", PaymentMethodKind.Card, ManualCardAmount);
            if (cardGrant is null) return;
            using var confirmGrant = await AuthorizeAsync(Permissions.PosTerminal.Payment.Confirm,
                "confirm-payment", PaymentMethodKind.Card, ManualCardAmount);
            if (confirmGrant is null) return;

            if (!IsManualCardSnapshotCurrent())
            {
                IsManualCardSuccessChecked = false;
                SetStatus("payment.manualCard.orderChanged");
                return;
            }

            using (cardGrant.Activate())
            {
                var result = await _workflowService.AddManualCardTenderAsync(Session, GetPaymentTargetAmount(),
                    _manualCardTenders, ManualCardAmount.ToString("0.00", CultureInfo.InvariantCulture),
                    _manualCardConfirmationId);
                if (!result.Succeeded || result.Tender is null)
                {
                    // 人工确认被拒（金额/状态校验不通过）同样是员工收款操作，需留痕。
                    OperationAuditEvents.RecordAction(_operationAuditLogger, OperationAuditTypes.PaymentTenderAdd,
                        "Failed", Session, OperationAuditEvents.CaptureCart(_cart.Lines),
                        reasonCode: "MANUAL_CARD_REJECTED", safeMessage: result.StatusKey, paymentMethod: "Card",
                        paymentAmount: ManualCardAmount, orderGuid: _manualCardConfirmationId.ToString("D"));
                    SetStatus(result.StatusKey, result.StatusMessage);
                    return;
                }

                _manualCardConfirmedTender = result.Tender;
                IsManualCardSavePending = true;
                PaymentTenders.Add(result.Tender);
                IsManualCardDialogOpen = false;
                TenderAmountText = string.Empty;
                RecalculateTenderSummary();
                // 先保留待落单状态，审计或 UI 通知异常也不能让已确认款项重新进入收款。
                OperationAuditEvents.RecordAction(_operationAuditLogger, OperationAuditTypes.PaymentTenderAdd,
                    "Succeeded", Session, OperationAuditEvents.CaptureCart(_cart.Lines),
                    reasonCode: "MANUAL_CARD_CONFIRMED", paymentMethod: "Card",
                    paymentAmount: result.Tender.Amount, orderGuid: _manualCardConfirmationId.ToString("D"));
            }

            using (confirmGrant.Activate())
            {
                if (!CanSaveConfirmedManualCard())
                {
                    SetStatus("payment.manualCard.orderChanged");
                    return;
                }
                await CompletePaymentFromTendersCoreAsync();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            SetStatus(IsManualCardSavePending ? "payment.manualCard.saveFailed" : "payment.status.tenderFailed");
        }
        finally
        {
            _isManualCardSubmitting = false;
            if (IsManualCardDialogOpen) ManualCardDialogStatusMessage = StatusMessage;
            IsPaymentInteractionLocked = IsManualCardSavePending || IsShuttingDown || _cardSession.HasUnknownResult;
            NotifyPaymentCommandStates();
        }
    }

    private async Task RetryManualCardSaveAsync()
    {
        if (!IsManualCardSavePending || _isManualCardSubmitting || IsShuttingDown) return;
        // 重试仅保存同一确认标识的订单，不再请求刷卡或重新添加 tender。
        IsPaymentInteractionLocked = true;
        _isManualCardSubmitting = true;
        try
        {
            using var grant = await AuthorizeAsync(Permissions.PosTerminal.Payment.Confirm, "retry-manual-card-save");
            if (grant is null) return;
            using var activation = grant.Activate();
            if (!CanSaveConfirmedManualCard())
            {
                SetStatus("payment.manualCard.orderChanged");
                return;
            }
            await CompletePaymentFromTendersCoreAsync();
        }
        finally
        {
            _isManualCardSubmitting = false;
            IsPaymentInteractionLocked = IsManualCardSavePending || IsShuttingDown || _cardSession.HasUnknownResult;
            NotifyPaymentCommandStates();
        }
    }

    partial void OnIsManualCardDialogOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPaymentInteractionEnabled));
        OnPropertyChanged(nameof(IsManualCardBackgroundEnabled));
        NotifyPaymentCommandStates();
    }

    partial void OnIsManualCardSuccessCheckedChanged(bool value) => ConfirmManualCardCommand.NotifyCanExecuteChanged();

    partial void OnIsManualCardSavePendingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsManualCardBackgroundEnabled));
        RetryManualCardSaveCommand.NotifyCanExecuteChanged();
    }
}
