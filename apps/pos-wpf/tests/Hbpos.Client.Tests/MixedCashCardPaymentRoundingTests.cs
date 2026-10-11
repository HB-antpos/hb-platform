using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

/// <summary>
/// 回归：现金找零按 5 分取整（如 $0.99 → $1.00）时，「先收部分现金、剩余刷卡」的混合支付必须能完成。
/// 之前付款页用取整后的剩余应收去校验卡金额，导致卡的合法金额（总额 − 现金）永远小于它而被拒绝。
/// </summary>
public sealed class MixedCashCardPaymentRoundingTests
{
    [Theory]
    [InlineData(0.99, "0.50")]
    [InlineData(0.99, "0.45")]
    [InlineData(0.99, "0.20")]
    [InlineData(1.01, "0.50")]
    [InlineData(2.03, "1.00")]
    [InlineData(10.03, "5.00")]
    public async Task Partial_cash_then_card_completes_when_total_needs_cash_rounding(double totalValue, string cashText)
    {
        var total = (decimal)totalValue;
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: true);
        cloudApi.ReleaseApprovedResult();
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var cart = fixture.CreateSaleCart(total);
        using var viewModel = fixture.CreatePaymentViewModel(cart);

        viewModel.TenderAmountText = cashText;
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        var cashTender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Cash, cashTender.Method);

        // 卡金额默认带入「总额 − 已收现金」，必须被接受并发送到终端，而不是提示卡必须是最后一笔；
        // 卡付足后订单会自动完成并落库，所以这里直接核对落库结果。
        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.DoesNotContain("cardMustBeFinalTender", viewModel.StatusMessage);
        Assert.Equal(1, cloudApi.SendCount);
        var savedSummary = Assert.Single(await fixture.OrderRepository.GetRecentOrdersAsync());
        var savedOrder = await fixture.OrderRepository.GetOrderAsync(savedSummary.OrderGuid);
        Assert.NotNull(savedOrder);
        Assert.Equal(2, savedOrder!.Payments.Count);
        Assert.Equal(total - cashTender.Amount, savedOrder.Payments.Single(payment => payment.Method == PaymentMethodKind.Card).Amount);
        Assert.Equal(total, savedOrder.Payments.Sum(payment => payment.Amount));
    }
}
