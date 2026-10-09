namespace Hbpos.Client.Wpf.Services.Facades;

public interface IPaymentTerminalFacade
{
    IVoucherApiClient? VoucherApiClient { get; }
    ICardTerminalClient? CardTerminalClient { get; }
    ICardTerminalSetupService? CardTerminalSetupService { get; }
    ILinklyTerminalDialogPresenter? LinklyTerminalDialogPresenter { get; }
    ICardPaymentRecoveryService? CardPaymentRecoveryService { get; }
    ICardRecoveryResultDialogService? CardRecoveryResultDialogService { get; }
    ILinklyFallbackPromptCoordinator? LinklyFallbackPromptCoordinator { get; }
    ISquareRefundSettlementService? SquareRefundSettlementService => null;
}
