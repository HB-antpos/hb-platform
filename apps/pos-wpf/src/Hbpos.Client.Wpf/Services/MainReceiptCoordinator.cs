using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>
/// Handles all receipt printing and cash drawer operations, consolidated from MainViewModel.
/// </summary>
internal sealed class MainReceiptCoordinator
{
    private readonly IReceiptPrintService _receiptPrintService;
    private readonly ICashDrawerService _cashDrawerService;
    private readonly ILocalizationService _localization;
    private readonly Action<string> _setStatusMessage;

    public MainReceiptCoordinator(
        IReceiptPrintService receiptPrintService,
        ICashDrawerService cashDrawerService,
        ILocalizationService localization,
        Action<string> setStatusMessage)
    {
        _receiptPrintService = receiptPrintService;
        _cashDrawerService = cashDrawerService;
        _localization = localization;
        _setStatusMessage = setStatusMessage;
    }

    public async Task<ReceiptPrintResult> PrintLatestAsync()
    {
        ReceiptPrintResult result;
        try
        {
            result = await _receiptPrintService.PrintLatestReceiptAsync(ReceiptPrintReason.LastReceipt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new ReceiptPrintResult(false, ex.Message);
        }

        ApplyReceiptPrintStatus(result);
        return result;
    }

    public async Task<ReceiptPrintResult> PrintReceiptAsync(Guid orderGuid, ReceiptPrintReason reason)
    {
        ReceiptPrintResult result;
        try
        {
            result = await _receiptPrintService.PrintReceiptAsync(orderGuid, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new ReceiptPrintResult(false, ex.Message, orderGuid);
        }

        ApplyReceiptPrintStatus(result);
        return result;
    }

    public async Task<ReceiptPrintResult> PrintSuccessAsync(Guid orderGuid) =>
        await PrintReceiptAsync(orderGuid, ReceiptPrintReason.Manual);

    public async Task PrintHistoryAsync(Guid orderGuid) =>
        await PrintReceiptAsync(orderGuid, ReceiptPrintReason.Reprint);

    public async Task<ReceiptPrintResult> PrintReceiptAsync(ReceiptDetails receipt, ReceiptPrintReason reason)
    {
        ReceiptPrintResult result;
        try
        {
            result = await _receiptPrintService.PrintReceiptAsync(receipt, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new ReceiptPrintResult(false, ex.Message, receipt.OrderGuid);
        }

        ApplyReceiptPrintStatus(result);
        return result;
    }

    public async Task<ReceiptPrintResult> OpenCashDrawerAsync()
    {
        try
        {
            return await _cashDrawerService.OpenAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReceiptPrintResult(false, ex.Message);
        }
    }

    public static bool ContainsCardPayment(LocalOrder order)
    {
        return order.Payments.Any(payment => payment.Method == PaymentMethodKind.Card);
    }

    public static bool ContainsCashPayment(LocalOrder order)
    {
        return order.Payments.Any(payment => payment.Method == PaymentMethodKind.Cash);
    }

    private void ApplyReceiptPrintStatus(ReceiptPrintResult result)
    {
        _setStatusMessage(result.Succeeded
            ? _localization.T("receipt.print.success")
            : string.Format(
                _localization.CurrentCulture,
                _localization.T("receipt.print.failed"),
                result.Message));
    }
}
