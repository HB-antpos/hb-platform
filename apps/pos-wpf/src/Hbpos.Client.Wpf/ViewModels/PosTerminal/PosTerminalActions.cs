using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

internal sealed record PosTerminalActions(
    Action? OpenPayment,
    Action? OpenReturns,
    Func<Task>? OpenSpecialProductsAsync,
    // 返回落库的挂单 Guid（失败或未落库为 null），供 ORDER_HOLD 审计写入 OrderGuid。
    Func<Task<Guid?>>? HoldOrderAsync,
    Func<Task>? RecallOrderAsync,
    Func<Task>? OpenHistoryAsync,
    Func<Task>? OpenDailyCloseAsync,
    Func<Task>? OpenSettingsAsync,
    Action? OpenCustomerDisplay,
    Func<Task<ReceiptPrintResult>>? PrintLastReceiptAsync,
    Func<Task<ReceiptPrintResult>>? OpenCashDrawerAsync,
    Func<Task>? ExitApplicationAsync,
    Func<Task>? ReregisterDeviceAsync,
    Func<Task>? LockCashierAsync,
    Func<Task>? OpenCardRecoveryCenterAsync)
{
    public bool CanPrintLastReceipt => PrintLastReceiptAsync is not null;

    public bool CanOpenCashDrawer => OpenCashDrawerAsync is not null;

    public bool CanExitApplication => ExitApplicationAsync is not null;

    public static PosTerminalActions FromLegacyCallbacks(
        Action? onOpenPayment,
        Action? onOpenReturns,
        Func<Task>? onOpenSpecialProductsAsync,
        Func<Task<Guid?>>? onHoldOrderAsync,
        Func<Task>? onRecallOrderAsync,
        Func<Task>? onOpenHistoryAsync,
        Func<Task>? onOpenDailyCloseAsync,
        Func<Task>? onOpenSettingsAsync,
        Action? onOpenCustomerDisplay,
        Func<Task<ReceiptPrintResult>>? onPrintLastReceiptAsync,
        Func<Task<ReceiptPrintResult>>? onOpenCashDrawerAsync,
        Func<Task>? onExitApplicationAsync,
        Func<Task>? onReregisterDeviceAsync,
        Func<Task>? onLockCashierAsync,
        Func<Task>? onOpenCardRecoveryCenterAsync)
    {
        // 中文注释：先收束构造器里的跨页面/外部动作，避免 VM 长期直接持有一串散落回调。
        return new PosTerminalActions(
            onOpenPayment,
            onOpenReturns,
            onOpenSpecialProductsAsync,
            onHoldOrderAsync,
            onRecallOrderAsync,
            onOpenHistoryAsync,
            onOpenDailyCloseAsync,
            onOpenSettingsAsync,
            onOpenCustomerDisplay,
            onPrintLastReceiptAsync,
            onOpenCashDrawerAsync,
            onExitApplicationAsync,
            onReregisterDeviceAsync,
            onLockCashierAsync,
            onOpenCardRecoveryCenterAsync);
    }
}
