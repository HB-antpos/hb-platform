using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>
/// 直接覆盖付款主管结案输入规则：主管身份必填、确认已付需要银行凭据、
/// 确认未付需要证据且不得带付款参考号、继续等待不能把共享输入固化为付款证据。
/// </summary>
public sealed class CardPaymentSupervisorResolutionRulesTests
{
    private const string InvalidInputError = "The supervisor identity, note, evidence, or payment reference is invalid.";
    private static readonly Guid AttemptGuid = Guid.Parse("a7e2d6c4-1b3f-4e59-8c0a-5d9f2e7b6c13");

    [Fact]
    public void Confirm_paid_with_payment_reference_normalizes_every_text_field()
    {
        var resolution = Create(
            CardPaymentSupervisorDecision.ConfirmPaid,
            reason: "  Bank shows approved  ",
            operatorCashierId: "  C001 ",
            evidence: "   ",
            paymentReference: "  RRN-123456  ",
            operatorUserGuid: " 0f4c8a52-user ",
            operatorName: "  Alice  ");

        var ok = CardPaymentSupervisorResolutionRules.TryNormalize(resolution, out var normalized, out var error);

        Assert.True(ok);
        Assert.Equal(string.Empty, error);
        Assert.Equal("Bank shows approved", normalized.Reason);
        Assert.Null(normalized.Evidence);
        Assert.Equal("RRN-123456", normalized.PaymentReference);
        Assert.Equal("C001", normalized.OperatorCashierId);
        Assert.Equal("0f4c8a52-user", normalized.OperatorUserGuid);
        Assert.Equal("Alice", normalized.OperatorName);
        Assert.Equal(AttemptGuid, normalized.AttemptGuid);
        Assert.Equal(CardProcessorKind.Linkly, normalized.Processor);
    }

    [Fact]
    public void Confirm_paid_with_evidence_only_is_accepted_without_note()
    {
        var resolution = Create(
            CardPaymentSupervisorDecision.ConfirmPaid,
            reason: "",
            evidence: "Merchant portal screenshot #42",
            paymentReference: null);

        Assert.True(CardPaymentSupervisorResolutionRules.TryNormalize(resolution, out var normalized, out _));
        Assert.Equal(string.Empty, normalized.Reason);
        Assert.Equal("Merchant portal screenshot #42", normalized.Evidence);
        Assert.Null(normalized.PaymentReference);
    }

    [Fact]
    public void Confirm_paid_without_reference_or_evidence_is_rejected_even_with_note()
    {
        var resolution = Create(
            CardPaymentSupervisorDecision.ConfirmPaid,
            reason: "Customer says the card was charged",
            evidence: " ",
            paymentReference: "\t");

        Assert.False(CardPaymentSupervisorResolutionRules.TryNormalize(resolution, out _, out var error));
        Assert.Equal("Enter the bank payment reference or evidence before confirming payment.", error);
    }

    [Fact]
    public void Confirm_not_paid_requires_bank_evidence()
    {
        var resolution = Create(
            CardPaymentSupervisorDecision.ConfirmNotPaid,
            reason: "Terminal says declined",
            evidence: null,
            paymentReference: null);

        Assert.False(CardPaymentSupervisorResolutionRules.TryNormalize(resolution, out _, out var error));
        Assert.Equal("Enter bank evidence confirming that no payment was processed.", error);

        var withEvidence = resolution with { Evidence = " No settlement entry " };
        Assert.True(CardPaymentSupervisorResolutionRules.TryNormalize(withEvidence, out var normalized, out _));
        Assert.Equal("No settlement entry", normalized.Evidence);
        Assert.Null(normalized.PaymentReference);
    }

    [Fact]
    public void Confirm_not_paid_rejects_payment_reference_with_localized_message()
    {
        var resolution = Create(
            CardPaymentSupervisorDecision.ConfirmNotPaid,
            reason: "",
            evidence: "No settlement entry",
            paymentReference: "RRN-123456");
        var localizeCalls = new List<(string Key, string Fallback)>();

        var ok = CardPaymentSupervisorResolutionRules.TryNormalize(
            resolution,
            out _,
            out var error,
            (key, fallback) =>
            {
                localizeCalls.Add((key, fallback));
                return "本地化：确认未付款时不要填写付款参考号";
            });

        Assert.False(ok);
        Assert.Equal("本地化：确认未付款时不要填写付款参考号", error);
        var call = Assert.Single(localizeCalls);
        Assert.Equal("cardRecovery.linkly.notPaidPaymentReferenceNotAllowed", call.Key);
        Assert.Equal("Do not enter a payment reference when confirming that no payment was processed.", call.Fallback);
    }

    [Fact]
    public void Confirm_not_paid_payment_reference_error_uses_fallback_without_localizer()
    {
        var resolution = Create(
            CardPaymentSupervisorDecision.ConfirmNotPaid,
            reason: "",
            evidence: "No settlement entry",
            paymentReference: "RRN-123456");

        Assert.False(CardPaymentSupervisorResolutionRules.TryNormalize(resolution, out _, out var error));
        Assert.Equal("Do not enter a payment reference when confirming that no payment was processed.", error);
    }

    [Fact]
    public void Continue_waiting_drops_shared_payment_reference_and_needs_no_evidence()
    {
        // 恢复中心的付款参考号输入框是共享的；继续等待不是金融结论，不能把它当作付款证据落库。
        var resolution = Create(
            CardPaymentSupervisorDecision.ContinueWaiting,
            reason: "",
            evidence: null,
            paymentReference: "RRN-TYPED-EARLIER");

        Assert.True(CardPaymentSupervisorResolutionRules.TryNormalize(resolution, out var normalized, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Null(normalized.PaymentReference);
        Assert.Equal(CardPaymentSupervisorDecision.ContinueWaiting, normalized.Decision);
    }

    [Fact]
    public void Continue_waiting_still_validates_payment_reference_length_before_dropping_it()
    {
        var resolution = Create(
            CardPaymentSupervisorDecision.ContinueWaiting,
            reason: "Waiting for bank",
            evidence: null,
            paymentReference: new string('x', 201));

        Assert.False(CardPaymentSupervisorResolutionRules.TryNormalize(resolution, out var normalized, out var error));
        Assert.Equal(InvalidInputError, error);
        Assert.Null(normalized.PaymentReference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_operator_cashier_is_rejected_for_every_decision(string? operatorCashierId)
    {
        foreach (var decision in Enum.GetValues<CardPaymentSupervisorDecision>())
        {
            var resolution = Create(
                decision,
                reason: "note",
                evidence: "bank evidence",
                paymentReference: null,
                operatorCashierId: operatorCashierId!);

            Assert.False(CardPaymentSupervisorResolutionRules.TryNormalize(resolution, out var normalized, out var error));
            Assert.Equal(InvalidInputError, error);
            Assert.Equal(string.Empty, normalized.OperatorCashierId);
        }
    }

    [Theory]
    [InlineData(500, 1000, 200, true)]
    [InlineData(501, 1, 1, false)]
    [InlineData(1, 1001, 1, false)]
    [InlineData(1, 1, 201, false)]
    public void Length_limits_are_inclusive_after_trimming(
        int reasonLength,
        int evidenceLength,
        int referenceLength,
        bool expected)
    {
        var resolution = Create(
            CardPaymentSupervisorDecision.ConfirmPaid,
            reason: "  " + new string('r', reasonLength) + "  ",
            evidence: " " + new string('e', evidenceLength) + " ",
            paymentReference: "\t" + new string('x', referenceLength) + "\t");

        var ok = CardPaymentSupervisorResolutionRules.TryNormalize(resolution, out _, out var error);

        Assert.Equal(expected, ok);
        Assert.Equal(expected ? string.Empty : InvalidInputError, error);
    }

    [Fact]
    public void Identity_error_takes_precedence_over_decision_specific_errors()
    {
        var resolution = Create(
            CardPaymentSupervisorDecision.ConfirmNotPaid,
            reason: "",
            evidence: null,
            paymentReference: "RRN-1",
            operatorCashierId: " ");
        var localizeCalls = 0;

        Assert.False(CardPaymentSupervisorResolutionRules.TryNormalize(
            resolution,
            out _,
            out var error,
            (_, fallback) =>
            {
                localizeCalls++;
                return fallback;
            }));
        Assert.Equal(InvalidInputError, error);
        Assert.Equal(0, localizeCalls);
    }

    [Fact]
    public void Blank_optional_operator_fields_normalize_to_null()
    {
        var resolution = Create(
            CardPaymentSupervisorDecision.ContinueWaiting,
            reason: "Waiting",
            evidence: null,
            paymentReference: null,
            operatorUserGuid: "  ",
            operatorName: "");

        Assert.True(CardPaymentSupervisorResolutionRules.TryNormalize(resolution, out var normalized, out _));
        Assert.Null(normalized.OperatorUserGuid);
        Assert.Null(normalized.OperatorName);
    }

    private static CardPaymentSupervisorResolution Create(
        CardPaymentSupervisorDecision decision,
        string reason,
        string? evidence,
        string? paymentReference,
        string operatorCashierId = "C001",
        string? operatorUserGuid = null,
        string? operatorName = null) =>
        new(
            AttemptGuid,
            CardProcessorKind.Linkly,
            decision,
            reason,
            operatorCashierId,
            operatorUserGuid,
            operatorName,
            evidence,
            paymentReference);
}
