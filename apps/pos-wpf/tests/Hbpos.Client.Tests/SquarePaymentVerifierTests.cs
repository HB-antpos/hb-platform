using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>
/// 直接覆盖 Square 付款核验：只有 COMPLETED 且金额（分）与币种都与请求一致才算已付。
/// 核验顺序固定为 状态 → 金额 → 币种，恢复服务据 Failure 区分状态未终结与金额/币种冲突。
/// </summary>
public sealed class SquarePaymentVerifierTests
{
    [Theory]
    [InlineData("COMPLETED", "AUD", "AUD")]
    [InlineData("completed", "aud", "AUD")]
    [InlineData("Completed", "AUD", "aud")]
    public void Completed_payment_with_matching_amount_and_currency_is_verified(
        string status,
        string paymentCurrency,
        string requestedCurrency)
    {
        var outcome = SquarePaymentVerifier.Verify(status, 1234, paymentCurrency, 1234, requestedCurrency);

        Assert.True(outcome.Verified);
        Assert.Equal(SquarePaymentVerificationFailure.None, outcome.Failure);
        Assert.Null(outcome.Message);
    }

    [Theory]
    [InlineData("APPROVED")]
    [InlineData("PENDING")]
    [InlineData("CANCELED")]
    [InlineData("FAILED")]
    [InlineData("COMPLETED ")]
    [InlineData("")]
    public void Non_completed_status_fails_with_status_in_message(string status)
    {
        // APPROVED 仅是授权未扣款；带空格的 COMPLETED 也不能被宽松匹配。
        var outcome = SquarePaymentVerifier.Verify(status, 1234, "AUD", 1234, "AUD");

        Assert.False(outcome.Verified);
        Assert.Equal(SquarePaymentVerificationFailure.Status, outcome.Failure);
        Assert.Equal($"Square payment status is {status}.", outcome.Message);
    }

    [Fact]
    public void Status_failure_takes_precedence_over_amount_and_currency_mismatch()
    {
        var outcome = SquarePaymentVerifier.Verify("PENDING", 999, "NZD", 1234, "AUD");

        Assert.Equal(SquarePaymentVerificationFailure.Status, outcome.Failure);
    }

    [Theory]
    [InlineData(1233, 1234)]
    [InlineData(1235, 1234)]
    [InlineData(0, 1234)]
    [InlineData(-1234, 1234)]
    [InlineData(123400, 1234)]
    public void Any_cent_difference_fails_amount_check(long paymentCents, long requestedCents)
    {
        var outcome = SquarePaymentVerifier.Verify("COMPLETED", paymentCents, "AUD", requestedCents, "AUD");

        Assert.False(outcome.Verified);
        Assert.Equal(SquarePaymentVerificationFailure.Amount, outcome.Failure);
        Assert.Equal("Square payment amount did not match the requested amount.", outcome.Message);
    }

    [Fact]
    public void Amount_failure_takes_precedence_over_currency_mismatch()
    {
        var outcome = SquarePaymentVerifier.Verify("COMPLETED", 1000, "USD", 1234, "AUD");

        Assert.Equal(SquarePaymentVerificationFailure.Amount, outcome.Failure);
    }

    [Theory]
    [InlineData("USD", "AUD")]
    [InlineData("AU", "AUD")]
    [InlineData("", "AUD")]
    public void Different_currency_fails_currency_check(string paymentCurrency, string requestedCurrency)
    {
        var outcome = SquarePaymentVerifier.Verify("COMPLETED", 1234, paymentCurrency, 1234, requestedCurrency);

        Assert.False(outcome.Verified);
        Assert.Equal(SquarePaymentVerificationFailure.Currency, outcome.Failure);
        Assert.Equal("Square payment currency did not match the requested currency.", outcome.Message);
    }

    [Fact]
    public void Zero_amount_completed_payment_matching_zero_request_is_verified()
    {
        // 核验器只比较一致性，不负责拒绝零额请求（由上游金额校验负责）。
        var outcome = SquarePaymentVerifier.Verify("COMPLETED", 0, "AUD", 0, "AUD");

        Assert.True(outcome.Verified);
    }
}
