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
        IsPaymentMode && !IsInstallmentPaymentEnabled && !IsInstallmentRepaymentMode &&
        _pendingVoucherUploadOrderGuid is null && _cart.RecoveryOwnerAttemptGuid is null &&
        !_cart.IsEmpty && !_cart.HasReturnLine && !_cart.HasNonIntegerQuantity && !_cart.HasZeroPriceLine &&
        !PaymentTenders.Any(tender => tender.Method == PaymentMethodKind.Card) &&
        GetExternalRemainingAmount() > 0m;

    private void OpenManualCard()
    {
        if (!CanOpenManualCard()) return;

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

    private void CancelManualCard()
    {
        if (!IsManualCardDialogOpen || IsPaymentInteractionLocked) return;
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
