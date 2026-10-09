namespace Hbpos.Client.Wpf.Services.Facades;

public sealed class PaymentTerminalFacade : IPaymentTerminalFacade
{
    public IVoucherApiClient? VoucherApiClient { get; }
    public ICardTerminalClient? CardTerminalClient { get; }
    public ICardTerminalSetupService? CardTerminalSetupService { get; }
    public ILinklyTerminalDialogPresenter? LinklyTerminalDialogPresenter { get; }
    public ICardPaymentRecoveryService? CardPaymentRecoveryService { get; }
    public ICardRecoveryResultDialogService? CardRecoveryResultDialogService { get; }
    public ILinklyFallbackPromptCoordinator? LinklyFallbackPromptCoordinator { get; }
    public ISquareRefundSettlementService? SquareRefundSettlementService { get; }

    public PaymentTerminalFacade(
        IVoucherApiClient? voucherApiClient,
        ICardTerminalClient? cardTerminalClient,
        ICardTerminalSetupService? cardTerminalSetupService,
        ILinklyTerminalDialogPresenter? linklyTerminalDialogPresenter,
        ICardPaymentRecoveryService? cardPaymentRecoveryService,
        ICardRecoveryResultDialogService? cardRecoveryResultDialogService,
        ILinklyFallbackPromptCoordinator? linklyFallbackPromptCoordinator,
        ISquareRefundSettlementService? squareRefundSettlementService = null)
    {
        SquareRefundSettlementService = squareRefundSettlementService;
        VoucherApiClient = voucherApiClient;
        CardTerminalClient = cardTerminalClient;
        CardTerminalSetupService = cardTerminalSetupService;
        LinklyTerminalDialogPresenter = linklyTerminalDialogPresenter;
        CardPaymentRecoveryService = cardPaymentRecoveryService;
        CardRecoveryResultDialogService = cardRecoveryResultDialogService;
        LinklyFallbackPromptCoordinator = linklyFallbackPromptCoordinator;
    }
}
