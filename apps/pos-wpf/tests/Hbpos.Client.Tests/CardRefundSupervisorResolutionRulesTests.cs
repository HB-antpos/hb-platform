using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>
/// 直接覆盖退款主管结案输入规则：三种结论各自要求的最少证据、长度上限（按 Trim 后计算）
/// 以及规范化结果。Linkly 与 Square 的 ResolveRefundAsync 共用这套规则。
/// </summary>
public sealed class CardRefundSupervisorResolutionRulesTests
{
    private static readonly Guid AttemptGuid = Guid.Parse("3b0c9a55-2f4e-4c58-9d0e-7a1f6b2c8d01");

    [Theory]
    [InlineData(CardProcessorKind.Linkly)]
    [InlineData(CardProcessorKind.Square)]
    public void Confirm_refunded_with_bank_reference_only_is_accepted_and_trimmed(CardProcessorKind processor)
    {
        var resolution = Create(
            CardRefundSupervisorDecision.ConfirmRefunded,
            reason: "   ",
            evidence: null,
            refundReference: "  RRN-778899  ",
            processor: processor);

        var ok = CardRefundSupervisorResolutionRules.TryNormalize(resolution, out var normalized, out var error);

        Assert.True(ok);
        Assert.Equal(string.Empty, error);
        Assert.Equal(string.Empty, normalized.Reason);
        Assert.Null(normalized.Evidence);
        Assert.Equal("RRN-778899", normalized.RefundReference);
        Assert.Equal(AttemptGuid, normalized.AttemptGuid);
        Assert.Equal(processor, normalized.Processor);
        Assert.Equal(CardRefundSupervisorDecision.ConfirmRefunded, normalized.Decision);
    }

    [Fact]
    public void Confirm_refunded_with_supervisor_note_only_is_accepted()
    {
        var resolution = Create(
            CardRefundSupervisorDecision.ConfirmRefunded,
            reason: "  Checked bank portal, refund settled  ",
            evidence: "\t",
            refundReference: "");

        var ok = CardRefundSupervisorResolutionRules.TryNormalize(resolution, out var normalized, out _);

        Assert.True(ok);
        Assert.Equal("Checked bank portal, refund settled", normalized.Reason);
        Assert.Null(normalized.Evidence);
        Assert.Null(normalized.RefundReference);
    }

    [Fact]
    public void Confirm_refunded_without_reference_or_note_is_rejected_even_with_evidence()
    {
        var resolution = Create(
            CardRefundSupervisorDecision.ConfirmRefunded,
            reason: " ",
            evidence: "Bank statement screenshot",
            refundReference: null);

        var ok = CardRefundSupervisorResolutionRules.TryNormalize(resolution, out var normalized, out var error);

        Assert.False(ok);
        Assert.Equal("Enter the bank refund reference or a supervisor note before confirming the refund.", error);
        // 失败时 out 值依旧是规范化后的输入，调用方只使用 error。
        Assert.Equal("Bank statement screenshot", normalized.Evidence);
    }

    [Fact]
    public void Confirm_not_refunded_requires_bank_evidence()
    {
        var withoutEvidence = Create(
            CardRefundSupervisorDecision.ConfirmNotRefunded,
            reason: "Terminal shows declined",
            evidence: "   ",
            refundReference: null);
        var withEvidence = withoutEvidence with { Evidence = "  No refund entry for this reference  " };

        Assert.False(CardRefundSupervisorResolutionRules.TryNormalize(withoutEvidence, out _, out var error));
        Assert.Equal("Enter the bank evidence confirming that no refund was processed.", error);

        Assert.True(CardRefundSupervisorResolutionRules.TryNormalize(withEvidence, out var normalized, out var noError));
        Assert.Equal(string.Empty, noError);
        Assert.Equal("No refund entry for this reference", normalized.Evidence);
        Assert.Equal("Terminal shows declined", normalized.Reason);
    }

    [Fact]
    public void Confirm_not_refunded_does_not_require_supervisor_note()
    {
        var resolution = Create(
            CardRefundSupervisorDecision.ConfirmNotRefunded,
            reason: "",
            evidence: "Bank portal: no matching refund",
            refundReference: null);

        Assert.True(CardRefundSupervisorResolutionRules.TryNormalize(resolution, out var normalized, out _));
        Assert.Equal(string.Empty, normalized.Reason);
    }

    [Fact]
    public void Continue_waiting_requires_supervisor_note()
    {
        var withoutNote = Create(
            CardRefundSupervisorDecision.ContinueWaiting,
            reason: "  ",
            evidence: "Bank evidence is not needed to keep waiting",
            refundReference: "RRN-1");
        var withNote = withoutNote with { Reason = " Bank still processing " };

        Assert.False(CardRefundSupervisorResolutionRules.TryNormalize(withoutNote, out _, out var error));
        Assert.Equal("Enter a supervisor note before keeping the refund locked.", error);

        Assert.True(CardRefundSupervisorResolutionRules.TryNormalize(withNote, out var normalized, out _));
        Assert.Equal("Bank still processing", normalized.Reason);
    }

    [Theory]
    [InlineData(500, 1000, 200, true)]
    [InlineData(501, 0, 0, false)]
    [InlineData(0, 1001, 0, false)]
    [InlineData(0, 0, 201, false)]
    public void Length_limits_are_inclusive_and_apply_to_every_decision(
        int reasonLength,
        int evidenceLength,
        int referenceLength,
        bool expected)
    {
        foreach (var decision in Enum.GetValues<CardRefundSupervisorDecision>())
        {
            // 至少给出每种结论要求的字段，确保失败只可能来自长度限制。
            var resolution = Create(
                decision,
                reason: "n" + new string('r', Math.Max(0, reasonLength - 1)),
                evidence: "e" + new string('e', Math.Max(0, evidenceLength - 1)),
                refundReference: referenceLength == 0 ? "R" : new string('x', referenceLength));

            var ok = CardRefundSupervisorResolutionRules.TryNormalize(resolution, out _, out var error);

            Assert.Equal(expected, ok);
            if (!expected)
            {
                Assert.Equal("The supervisor note, evidence, or refund reference is too long.", error);
            }
        }
    }

    [Fact]
    public void Length_limits_are_measured_after_trimming()
    {
        var resolution = Create(
            CardRefundSupervisorDecision.ContinueWaiting,
            reason: "   " + new string('r', 500) + "   ",
            evidence: "\t" + new string('e', 1000) + "\n",
            refundReference: " " + new string('x', 200) + " ");

        Assert.True(CardRefundSupervisorResolutionRules.TryNormalize(resolution, out var normalized, out _));
        Assert.Equal(500, normalized.Reason.Length);
        Assert.Equal(1000, normalized.Evidence!.Length);
        Assert.Equal(200, normalized.RefundReference!.Length);
    }

    [Fact]
    public void Length_error_takes_precedence_over_missing_evidence()
    {
        var resolution = Create(
            CardRefundSupervisorDecision.ConfirmNotRefunded,
            reason: new string('r', 501),
            evidence: null,
            refundReference: null);

        Assert.False(CardRefundSupervisorResolutionRules.TryNormalize(resolution, out _, out var error));
        Assert.Equal("The supervisor note, evidence, or refund reference is too long.", error);
    }

    // 注意：退款 ContinueWaiting / ConfirmNotRefunded 目前会原样保留 RefundReference，
    // 与付款规则（继续等待丢弃、确认未付拒绝参考号）不对称，已作为疑似问题另行上报；
    // 这里不写断言固化该行为，也不提交 Skip 用例（CI 要求 total == executed == passed）。

    private static CardRefundSupervisorResolution Create(
        CardRefundSupervisorDecision decision,
        string reason,
        string? evidence,
        string? refundReference,
        CardProcessorKind processor = CardProcessorKind.Linkly) =>
        new(AttemptGuid, processor, decision, reason, evidence, refundReference);
}
