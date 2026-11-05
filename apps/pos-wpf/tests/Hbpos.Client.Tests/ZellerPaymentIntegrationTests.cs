using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

/// <summary>Zeller 接进现有收款代码的几处小改动：结果策略、退款路由、刷卡终端服务的拒绝分支、付款方式标签。</summary>
public sealed class ZellerPaymentIntegrationTests
{
    private const string ZellerPayment = "ZELLER:HBZ-799287a33dc24df1bca3d33eea19b35a:65c4b343-03e6-49ef-a54d-012e3f2c222c";

    // ── 结果策略 ──

    [Theory]
    [InlineData("resultUnknown", CardPaymentTerminalOutcome.ResultUnknown, CardPaymentErrorKind.ActiveSessionRequiresRecovery)]
    [InlineData("refundInProgress", CardPaymentTerminalOutcome.None, CardPaymentErrorKind.ActiveSessionRequiresRecovery)]
    [InlineData("cancelled", CardPaymentTerminalOutcome.Cancelled, CardPaymentErrorKind.None)]
    [InlineData("timeout", CardPaymentTerminalOutcome.TimedOut, CardPaymentErrorKind.Timeout)]
    [InlineData("terminalUnavailable", CardPaymentTerminalOutcome.None, CardPaymentErrorKind.ConnectionFailed)]
    [InlineData("busy", CardPaymentTerminalOutcome.None, CardPaymentErrorKind.ConnectionFailed)]
    [InlineData("declined", CardPaymentTerminalOutcome.None, CardPaymentErrorKind.CardDeclined)]
    [InlineData("refundDeclined", CardPaymentTerminalOutcome.None, CardPaymentErrorKind.CardDeclined)]
    [InlineData("setupRequired", CardPaymentTerminalOutcome.None, CardPaymentErrorKind.None)]
    [InlineData("originalUnverified", CardPaymentTerminalOutcome.None, CardPaymentErrorKind.None)]
    public void Zeller_results_are_classified_and_keep_their_own_status(
        string suffix,
        CardPaymentTerminalOutcome outcome,
        CardPaymentErrorKind errorKind)
    {
        var resolver = DefaultResolver();

        var result = resolver.Apply(PaymentTenderAttemptResult.Fail("payment.card.zeller." + suffix));

        Assert.Equal(outcome, result.CardResult!.Outcome);
        Assert.Equal(errorKind, result.CardResult.ErrorKind);
        Assert.True(result.CardResult.PreserveStatus);
    }

    [Fact]
    public void Only_a_zeller_result_unknown_requires_recovery()
    {
        var resolver = DefaultResolver();

        Assert.True(resolver.Apply(PaymentTenderAttemptResult.Fail("payment.card.zeller.resultUnknown")).CardResult!.RequiresRecovery);
        Assert.False(resolver.Apply(PaymentTenderAttemptResult.Fail("payment.card.zeller.refundInProgress")).CardResult!.RequiresRecovery);
        Assert.False(resolver.Apply(PaymentTenderAttemptResult.Fail("payment.card.zeller.notStarted")).CardResult!.RequiresRecovery);
    }

    // ── 退款路由 ──

    [Theory]
    [InlineData(CardProcessorKind.Zeller, ZellerPayment, CardProcessorKind.Zeller)]
    [InlineData(CardProcessorKind.Zeller, "ANZ:123456", CardProcessorKind.Linkly)]
    [InlineData(CardProcessorKind.Zeller, "ANZCLOUD:abc", CardProcessorKind.Linkly)]
    [InlineData(CardProcessorKind.Zeller, "SQ:payment-1", CardProcessorKind.Square)]
    [InlineData(CardProcessorKind.Zeller, "CARD-OTHER", CardProcessorKind.Zeller)]
    [InlineData(CardProcessorKind.Square, ZellerPayment, CardProcessorKind.Zeller)]
    [InlineData(CardProcessorKind.Square, "ANZ:123456", CardProcessorKind.Linkly)]
    [InlineData(CardProcessorKind.Square, "SQ:payment-1", CardProcessorKind.Square)]
    [InlineData(CardProcessorKind.Linkly, ZellerPayment, CardProcessorKind.Linkly)]
    [InlineData(CardProcessorKind.Linkly, "SQ:payment-1", CardProcessorKind.Linkly)]
    public void Refunds_route_to_the_original_processor_only_when_the_current_one_cannot_refund_it(
        CardProcessorKind current,
        string originalReference,
        CardProcessorKind expected)
    {
        var settings = CardTerminalSettings.FromEnvironment() with { Processor = current };

        Assert.Equal(expected, CardRefundProcessorRouting.Resolve(settings, originalReference).Processor);
    }

    [Fact]
    public void A_wrapped_zeller_refund_reference_still_routes_to_zeller()
    {
        var settings = CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Square };
        var wrapped = CardRefundReference.Format("ZELLERRF:HBZR-0f3c5d9a6b2e4c1d8e7f6a5b4c3d2e1f:b91ac661-0000-4000-8000-000000000002", ZellerPayment);

        Assert.Equal(CardProcessorKind.Zeller, CardRefundProcessorRouting.Resolve(settings, wrapped).Processor);
    }

    // ── 刷卡终端服务 ──

    [Fact]
    public async Task Configured_client_refuses_zeller_outside_the_payment_page_without_any_request()
    {
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://hbpos.test/") };
        var client = new ConfiguredCardTerminalClient(
            new StaticSettingsProvider(CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Zeller }),
            httpClient);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var sale = await client.AuthorizeAsync(10m, session);
        var refund = await client.RefundAsync(4m, session, ZellerPayment);

        Assert.False(sale.Approved);
        Assert.Equal("payment.card.zeller.paymentPageOnly", sale.StatusKey);
        Assert.False(sale.ResultUnknown);
        Assert.False(refund.Approved);
        Assert.Equal("payment.card.zeller.paymentPageOnly", refund.StatusKey);
        Assert.Equal(0, handler.Requests);
    }

    // ── 订单列表 ──

    [Theory]
    [InlineData(ZellerPayment, "Zeller")]
    [InlineData("ZELLERRF:HBZR-0f3c5d9a6b2e4c1d8e7f6a5b4c3d2e1f:b91ac661-0000-4000-8000-000000000002", "Zeller")]
    [InlineData("SQ:payment-1", "Square")]
    [InlineData("ANZ:123456", "ANZ Linkly")]
    [InlineData("CARD-OTHER", "Card")]
    public void Order_list_labels_zeller_card_payments(string reference, string expected)
    {
        var payment = new ReceiptPaymentLine(PaymentMethodKind.Card, 10m, reference);

        Assert.Equal(expected, payment.MethodLabel);
    }

    private static CardPaymentResultPolicyResolver DefaultResolver() =>
        new([
            new LinklyCardPaymentResultPolicy(),
            new SquareCardPaymentResultPolicy(),
            new ZellerCardPaymentResultPolicy(),
            new FallbackCardPaymentResultPolicy()
        ]);

    private sealed class StaticSettingsProvider(CardTerminalSettings settings) : ICardTerminalSettingsProvider
    {
        public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }
}
