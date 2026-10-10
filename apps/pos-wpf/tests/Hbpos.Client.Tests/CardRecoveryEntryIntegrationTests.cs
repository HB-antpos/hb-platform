using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class CardRecoveryEntryIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_overlay_recovery_entry_finalizes_not_paid_and_allows_cash_sale(
        bool decisionAlreadyPersisted)
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: false);
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var cart = fixture.CreateSaleCart();
        using var payment = fixture.CreatePaymentViewModel(cart);
        var recovery = fixture.CreateRecoveryService();
        var openedWithEmptyCart = false;
        // 复用生产 Presenter 的持久化资格核验和最终交接；打印等未参与本场景的依赖留空。
        var presenter = new CardRecoveryPresenter(
            recovery,
            cardRecoveryResultDialogService: null,
            receiptQueryService: null!,
            receiptPrinterSettingsStore: null,
            receiptTextFormatter: null!,
            localization: new LocalizationService(),
            linklyFallbackPromptCoordinator: null,
            linklyBankReceiptPrinter: null,
            mainChildViewModelFactory: null!,
            cart: cart,
            setPaymentRecoveryBlocked: payment.SetCurrentCardRecoveryRequired,
            navigateToPaymentOnDraft: () =>
            {
                payment.PrepareForEntry(fixture.Session);
                return Task.CompletedTask;
            },
            getSession: () => fixture.Session,
            tryApplyCardRecoveryDraft: payment.TryApplyRecoveredPaymentProjection,
            completeRecoveredDraftHandoffAsync: (key, cancellation) =>
                recovery.CompleteDraftHandoffAsync(key.AttemptGuid, cart, cancellation));
        payment.ConfigureCardPaymentHandoff(
            presenter.PrepareCardPaymentHandoffAsync,
            presenter.HandoffCardPaymentAsync,
            () => openedWithEmptyCart = cart.IsEmpty);

        var cardTask = payment.SelectCardCommand.ExecuteAsync(null);
        try
        {
            await cloudApi.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // 手动取消时终端结果确认不了（取消键后查询不可用）：保守按结果未知进异常中心。
            cloudApi.StatusQueriesUnavailable = true;
            payment.CancelCommand.Execute(null);
            await cardTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(payment.IsCardPaymentRecoveryRequired);
            var queued = Assert.Single(await recovery.ListOpenAsync(fixture.Session));
            cloudApi.StatusQueriesUnavailable = false;
            // 主管结案前会向 Linkly 实时核验：只有查到终态“未批准”，才允许确认未付款。
            cloudApi.GetTransactionReportsDecline = true;
            var resolution = new CardPaymentSupervisorResolution(
                queued.AttemptGuid,
                CardProcessorKind.Linkly,
                CardPaymentSupervisorDecision.ConfirmNotPaid,
                string.Empty,
                "SUPERVISOR-TEST",
                Evidence: "Test bank reconciliation confirms no charge");

            if (decisionAlreadyPersisted)
            {
                // 复现旧版已卡住的现场：主管决议成功落库，但原购物车阻止草稿发布。
                var pending = await recovery.ResolvePaymentAsync(resolution, cart, fixture.Session);
                Assert.True(pending.ResolutionPersisted);
                Assert.False(pending.Succeeded);
                Assert.True(pending.LockRetained);
                var persisted = await fixture.AttemptRepository.GetAttemptAsync(queued.AttemptGuid);
                Assert.NotNull(persisted);
                Assert.Equal("SUPERVISOR_CONFIRMED_NOT_PAID", persisted!.ResponseCode);
                Assert.Equal(CardRecoveryPhases.FinalizePending, persisted.RecoveryPhase);
            }

            payment.CloseCardPaymentErrorOverlayCommand.Execute(null);
            Assert.Single(cart.Lines);
            Assert.True(payment.IsPaymentInteractionLocked);
            await payment.OpenCardRecoveryCenterCommand.ExecuteAsync(null);

            Assert.True(openedWithEmptyCart);
            Assert.Empty(cart.Lines);
            Assert.False(payment.IsCardPaymentRecoveryRequired);
            var retained = Assert.Single(await recovery.ListOpenAsync(fixture.Session));
            Assert.Equal(queued.AttemptGuid, retained.AttemptGuid);
            Assert.Equal(queued.OrderDraftJson, retained.OrderDraftJson);

            var result = decisionAlreadyPersisted
                ? await recovery.RecoverAttemptAsync(queued.AttemptGuid, cart, fixture.Session)
                : (await recovery.ResolvePaymentAsync(resolution, cart, fixture.Session)).RecoveryResult;
            Assert.NotNull(result);
            Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, result!.Outcome);
            Assert.Equal(10m, Assert.Single(cart.Lines).UnitPrice);
            Assert.Null(await presenter.HandoffRecoveredCardDraftFromRecoveryCenterAsync(result));

            Assert.Empty(await recovery.ListOpenAsync(fixture.Session));
            Assert.Null(cart.RecoveryOwnerAttemptGuid);
            Assert.False(payment.IsPaymentInteractionLocked);
            Assert.True(payment.BackToPosCommand.CanExecute(null));
            Assert.True(payment.SelectCashCommand.CanExecute(null));
            await payment.SelectCashCommand.ExecuteAsync(null);
            await payment.ConfirmPaymentCommand.ExecuteAsync(null);
            var saved = Assert.Single(await fixture.OrderRepository.GetRecentOrdersAsync());
            var order = await fixture.OrderRepository.GetOrderAsync(saved.OrderGuid);
            Assert.NotNull(order);
            var tender = Assert.Single(order!.Payments);
            Assert.Equal(PaymentMethodKind.Cash, tender.Method);
            Assert.Equal(10m, tender.Amount);
            Assert.Equal(1, cloudApi.SendCount);
            // 唯一一次 GET 就是主管结案前的只读实时核验。
            Assert.Equal(1, cloudApi.GetTransactionCount);
        }
        finally
        {
            if (payment.IsCardPaymentInProgress)
            {
                payment.CancelCommand.Execute(null);
            }
            cloudApi.ReleaseApprovedResult();
            await cardTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
