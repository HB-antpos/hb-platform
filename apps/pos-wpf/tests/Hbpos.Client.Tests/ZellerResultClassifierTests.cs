using Hbpos.Client.Wpf.Services.Zeller;

namespace Hbpos.Client.Tests;

/// <summary>
/// Zeller 结果分类器：SDK 的返回和按 reference 查询的结果如何变成「批准 / 拒付 / 未付款 / 结果未知」。
/// 判错的代价是重复扣款或已扣款却没建单，所以把 2026-11-05 真机验证里出现过的每种情况都固定成用例。
/// </summary>
public sealed class ZellerResultClassifierTests
{
    private const string Reference = "HBZ-799287a33dc24df1bca3d33eea19b35a";
    private const string Uuid = "65c4b343-03e6-49ef-a54d-012e3f2c222c";
    private static readonly ZellerAttemptExpectation Purchase = new(ZellerTransactionType.Purchase, 100, Reference);
    private static readonly ZellerAttemptExpectation Refund = new(ZellerTransactionType.Refund, 100, Reference);

    private static ZellerTransaction Transaction(
        ZellerTransactionStatus status = ZellerTransactionStatus.Approved,
        int amountCents = 100,
        string? reference = Reference,
        string? uuid = Uuid,
        ZellerTransactionType type = ZellerTransactionType.Purchase) =>
        new(status, type, amountCents, uuid, reference);

    // ── 调用返回了交易 ──

    [Fact]
    public void Approved_transaction_with_matching_amount_and_reference_is_approved()
    {
        var transaction = Transaction() with { ReceiptText = "APPROVED", ApprovalCode = "456321" };

        var classification = ZellerResultClassifier.ClassifyCall(ZellerCallResult.FromTransaction(transaction), Purchase);

        Assert.Equal(ZellerCallDecision.Approved, classification.Decision);
        Assert.Same(transaction, classification.Transaction);
        Assert.False(classification.RebuildTerminalFirst);
    }

    [Theory]
    [InlineData(ZellerTransactionStatus.Declined)]
    [InlineData(ZellerTransactionStatus.Failed)]
    public void Declined_transaction_arrives_as_a_transaction_and_must_not_be_treated_as_paid(ZellerTransactionStatus status)
    {
        // 真机：余额不足的扣款返回的是成交类响应，Status=Declined，code 51。
        var classification = ZellerResultClassifier.ClassifyCall(
            ZellerCallResult.FromTransaction(Transaction(status) with { ResponseCode = "51" }),
            Purchase);

        Assert.Equal(ZellerCallDecision.Declined, classification.Decision);
        Assert.Equal(ZellerFailureKind.BankDeclined, classification.FailureKind);
    }

    [Fact]
    public void Approved_transaction_with_a_different_amount_is_unknown()
    {
        var classification = ZellerResultClassifier.ClassifyCall(
            ZellerCallResult.FromTransaction(Transaction(amountCents: 1000)),
            Purchase);

        Assert.Equal(ZellerCallDecision.Unknown, classification.Decision);
        Assert.Equal(ZellerUnknownReason.AmountMismatch, classification.UnknownReason);
    }

    [Theory]
    [InlineData("HBZ-00000000000000000000000000000001")]
    [InlineData(null)]
    [InlineData("")]
    public void Transaction_for_another_reference_is_unknown_and_rebuilds_the_terminal(string? reference)
    {
        // 并发串线时，SDK 把别的调用的结果交给过不相干的调用。
        var classification = ZellerResultClassifier.ClassifyCall(
            ZellerCallResult.FromTransaction(Transaction(reference: reference)),
            Purchase);

        Assert.Equal(ZellerCallDecision.Unknown, classification.Decision);
        Assert.Equal(ZellerUnknownReason.ReferenceMismatch, classification.UnknownReason);
        Assert.True(classification.RebuildTerminalFirst);
    }

    [Fact]
    public void Refund_transaction_returned_to_a_purchase_is_unknown()
    {
        var classification = ZellerResultClassifier.ClassifyCall(
            ZellerCallResult.FromTransaction(Transaction(type: ZellerTransactionType.Refund)),
            Purchase);

        Assert.Equal(ZellerCallDecision.Unknown, classification.Decision);
        Assert.Equal(ZellerUnknownReason.ReferenceMismatch, classification.UnknownReason);
    }

    [Fact]
    public void Approved_transaction_without_a_transaction_uuid_is_unknown()
    {
        var classification = ZellerResultClassifier.ClassifyCall(
            ZellerCallResult.FromTransaction(Transaction(uuid: " ")),
            Purchase);

        Assert.Equal(ZellerCallDecision.Unknown, classification.Decision);
        Assert.Equal(ZellerUnknownReason.MissingTransactionUuid, classification.UnknownReason);
    }

    [Fact]
    public void Unrecognised_status_and_missing_transaction_are_unknown()
    {
        var unrecognised = ZellerResultClassifier.ClassifyCall(
            ZellerCallResult.FromTransaction(Transaction(ZellerTransactionStatus.Unknown)),
            Purchase);
        var missing = ZellerResultClassifier.ClassifyCall(new ZellerCallResult(ZellerCallOutcome.Transaction), Purchase);

        Assert.Equal(ZellerUnknownReason.UnrecognisedStatus, unrecognised.UnknownReason);
        Assert.Equal(ZellerCallDecision.Unknown, missing.Decision);
    }

    // ── 调用返回了错误 ──

    public static TheoryData<ZellerErrorType, ZellerCallDecision, ZellerFailureKind> ErrorMappings => new()
    {
        { ZellerErrorType.OperationRequestInvalid, ZellerCallDecision.NotSubmitted, ZellerFailureKind.InvalidRequest },
        { ZellerErrorType.OperationBusy, ZellerCallDecision.NotSubmitted, ZellerFailureKind.Busy },
        { ZellerErrorType.SetupRequired, ZellerCallDecision.VerifyThenNotPaid, ZellerFailureKind.SetupRequired },
        { ZellerErrorType.FeatureNotAvailable, ZellerCallDecision.VerifyThenNotPaid, ZellerFailureKind.SetupRequired },
        { ZellerErrorType.PermissionsMissing, ZellerCallDecision.VerifyThenNotPaid, ZellerFailureKind.SetupRequired },
        { ZellerErrorType.Cancelled, ZellerCallDecision.VerifyThenNotPaid, ZellerFailureKind.Cancelled },
        { ZellerErrorType.InactivityTimeout, ZellerCallDecision.VerifyThenNotPaid, ZellerFailureKind.Timeout },
        { ZellerErrorType.ConnectionFailure, ZellerCallDecision.VerifyThenNotPaid, ZellerFailureKind.TerminalUnavailable },
        { ZellerErrorType.PairedDeviceBusy, ZellerCallDecision.VerifyThenNotPaid, ZellerFailureKind.TerminalUnavailable },
        { ZellerErrorType.PairedDeviceNotAvailable, ZellerCallDecision.VerifyThenNotPaid, ZellerFailureKind.TerminalUnavailable },
        { ZellerErrorType.CardDeclined, ZellerCallDecision.VerifyThenDeclined, ZellerFailureKind.BankDeclined },
        { ZellerErrorType.TransactionDeclined, ZellerCallDecision.VerifyThenDeclined, ZellerFailureKind.BankDeclined },
        { ZellerErrorType.CardBlocked, ZellerCallDecision.VerifyThenDeclined, ZellerFailureKind.CardProblem },
        { ZellerErrorType.CardNotSupported, ZellerCallDecision.VerifyThenDeclined, ZellerFailureKind.CardProblem },
        { ZellerErrorType.CardReadFailure, ZellerCallDecision.VerifyThenDeclined, ZellerFailureKind.CardProblem },
        { ZellerErrorType.CardInsertRequired, ZellerCallDecision.VerifyThenDeclined, ZellerFailureKind.CardProblem },
        { ZellerErrorType.NetworkFailure, ZellerCallDecision.Unknown, ZellerFailureKind.ResultUnknown },
        { ZellerErrorType.OperationFailure, ZellerCallDecision.Unknown, ZellerFailureKind.ResultUnknown },
        { ZellerErrorType.OperationInterrupted, ZellerCallDecision.Unknown, ZellerFailureKind.ResultUnknown },
        { ZellerErrorType.OperationResponseInvalid, ZellerCallDecision.Unknown, ZellerFailureKind.ResultUnknown },
        { ZellerErrorType.TransactionNotFound, ZellerCallDecision.Unknown, ZellerFailureKind.ResultUnknown },
        { ZellerErrorType.TransactionResponseTimeout, ZellerCallDecision.Unknown, ZellerFailureKind.ResultUnknown },
        { ZellerErrorType.TransactionResponseInvalid, ZellerCallDecision.Unknown, ZellerFailureKind.ResultUnknown },
        { ZellerErrorType.TransactionNetworkFailure, ZellerCallDecision.Unknown, ZellerFailureKind.ResultUnknown },
        { ZellerErrorType.TransactionSystemFailure, ZellerCallDecision.Unknown, ZellerFailureKind.ResultUnknown },
        { ZellerErrorType.TransactionFailure, ZellerCallDecision.Unknown, ZellerFailureKind.ResultUnknown },
        { ZellerErrorType.Unrecognised, ZellerCallDecision.Unknown, ZellerFailureKind.ResultUnknown }
    };

    [Theory]
    [MemberData(nameof(ErrorMappings))]
    public void Purchase_error_maps_to_the_documented_decision(
        ZellerErrorType errorType,
        ZellerCallDecision decision,
        ZellerFailureKind failureKind)
    {
        var classification = ZellerResultClassifier.ClassifyCall(ZellerCallResult.FromError(errorType), Purchase);

        Assert.Equal(decision, classification.Decision);
        Assert.Equal(failureKind, classification.FailureKind);
    }

    [Fact]
    public void Every_sdk_error_type_has_an_explicit_expectation()
    {
        // SDK 升级后新增错误类型时，这里会提醒把它加进映射表，而不是悄悄落到默认分支。
        var covered = ErrorMappings.Select(row => (ZellerErrorType)row[0]).ToHashSet();

        Assert.Equal(Enum.GetValues<ZellerErrorType>().ToHashSet(), covered);
        Assert.Equal(26, Enum.GetValues<ZellerErrorType>().Count(type => type != ZellerErrorType.Unrecognised));
    }

    [Fact]
    public void Network_failure_is_never_treated_as_not_paid()
    {
        // 真机：一次 NetworkFailure 返回时钱已经扣了，另一次查到 0 笔。只能靠查询。
        var classification = ZellerResultClassifier.ClassifyCall(
            ZellerCallResult.FromError(ZellerErrorType.NetworkFailure, reachedTerminal: true),
            Purchase);

        Assert.Equal(ZellerCallDecision.Unknown, classification.Decision);
        Assert.Equal(ZellerUnknownReason.SdkError, classification.UnknownReason);
    }

    [Theory]
    [InlineData(ZellerErrorType.ConnectionFailure)]
    [InlineData(ZellerErrorType.PairedDeviceBusy)]
    [InlineData(ZellerErrorType.PairedDeviceNotAvailable)]
    public void Terminal_connection_errors_after_the_request_reached_the_terminal_are_unknown(ZellerErrorType errorType)
    {
        var classification = ZellerResultClassifier.ClassifyCall(
            ZellerCallResult.FromError(errorType, reachedTerminal: true),
            Purchase);

        Assert.Equal(ZellerCallDecision.Unknown, classification.Decision);
        Assert.Equal(ZellerUnknownReason.TerminalConnectionLost, classification.UnknownReason);
    }

    [Fact]
    public void Operation_busy_reports_a_breached_serial_gate_and_asks_for_a_rebuild()
    {
        // 真机 8 次全中：出现 Busy 时，当时进行中的那一笔结果已经丢失。
        var classification = ZellerResultClassifier.ClassifyCall(ZellerCallResult.FromError(ZellerErrorType.OperationBusy), Purchase);

        Assert.True(classification.SerialGateBreached);
        Assert.True(classification.RebuildTerminalFirst);
    }

    [Fact]
    public void Response_meant_for_another_call_asks_for_a_rebuild()
    {
        var classification = ZellerResultClassifier.ClassifyCall(
            ZellerCallResult.FromError(ZellerErrorType.OperationResponseInvalid, "unhandled response type"),
            Purchase);

        Assert.Equal(ZellerCallDecision.Unknown, classification.Decision);
        Assert.True(classification.RebuildTerminalFirst);
    }

    [Fact]
    public void Refund_of_a_missing_original_transaction_is_rejected_without_retry()
    {
        var classification = ZellerResultClassifier.ClassifyCall(ZellerCallResult.FromError(ZellerErrorType.TransactionNotFound), Refund);

        Assert.Equal(ZellerCallDecision.NotSubmitted, classification.Decision);
        Assert.Equal(ZellerFailureKind.OriginalTransactionNotFound, classification.FailureKind);
    }

    [Fact]
    public void Error_without_a_type_is_unknown()
    {
        var classification = ZellerResultClassifier.ClassifyCall(new ZellerCallResult(ZellerCallOutcome.Error), Purchase);

        Assert.Equal(ZellerCallDecision.Unknown, classification.Decision);
    }

    // ── 调用没有正常返回 ──

    [Fact]
    public void Call_abandoned_by_the_watchdog_is_unknown_and_rebuilds_before_any_query()
    {
        var classification = ZellerResultClassifier.ClassifyCall(ZellerCallResult.Abandoned(reachedTerminal: true), Purchase);

        Assert.Equal(ZellerCallDecision.Unknown, classification.Decision);
        Assert.Equal(ZellerUnknownReason.WatchdogTimeout, classification.UnknownReason);
        Assert.True(classification.RebuildTerminalFirst);
    }

    [Fact]
    public void Sdk_exception_is_unknown_and_rebuilds_before_any_query()
    {
        var classification = ZellerResultClassifier.ClassifyCall(ZellerCallResult.Faulted("boom"), Purchase);

        Assert.Equal(ZellerCallDecision.Unknown, classification.Decision);
        Assert.Equal(ZellerUnknownReason.SdkException, classification.UnknownReason);
        Assert.True(classification.RebuildTerminalFirst);
    }

    // ── 按 reference 查询之后 ──

    [Theory]
    [InlineData(ZellerCallDecision.VerifyThenNotPaid, ZellerVerificationDecision.NotPaid)]
    [InlineData(ZellerCallDecision.VerifyThenDeclined, ZellerVerificationDecision.Declined)]
    [InlineData(ZellerCallDecision.Unknown, ZellerVerificationDecision.KeepUnknown)]
    public void No_transactions_closes_only_what_the_sdk_already_said_was_not_paid(
        ZellerCallDecision pending,
        ZellerVerificationDecision expected)
    {
        // 真机：取消、超时、刷卡机离线查到的都是 0 笔；断网的那几笔是已扣款，所以未知的不能靠 0 笔收尾。
        var verification = ZellerResultClassifier.ClassifyVerification(pending, ZellerQueryResult.Found(), Purchase);

        Assert.Equal(expected, verification.Decision);
        Assert.Equal(ZellerUnknownReason.None, verification.UnknownReason);
    }

    [Theory]
    [InlineData(ZellerCallDecision.VerifyThenNotPaid)]
    [InlineData(ZellerCallDecision.VerifyThenDeclined)]
    [InlineData(ZellerCallDecision.Unknown)]
    public void One_approved_transaction_found_is_approved_whatever_the_sdk_returned(ZellerCallDecision pending)
    {
        // 查到的交易没有 ReceiptText 和 ApprovalCode，但照样算批准。
        var found = Transaction();

        var verification = ZellerResultClassifier.ClassifyVerification(pending, ZellerQueryResult.Found(found), Purchase);

        Assert.Equal(ZellerVerificationDecision.Approved, verification.Decision);
        Assert.Same(found, verification.Transaction);
    }

    [Fact]
    public void Declined_transaction_found_is_declined()
    {
        var verification = ZellerResultClassifier.ClassifyVerification(
            ZellerCallDecision.Unknown,
            ZellerQueryResult.Found(Transaction(ZellerTransactionStatus.Declined)),
            Purchase);

        Assert.Equal(ZellerVerificationDecision.Declined, verification.Decision);
    }

    [Fact]
    public void Declined_then_approved_under_one_reference_counts_as_one_approval()
    {
        // 有人值守模式下点 Try Again 会在同一个 reference 下重来；以 Approved 的笔数为准。
        var approved = Transaction(uuid: "98b690b7-5eaa-41ec-8ff8-168c60364aac");

        var verification = ZellerResultClassifier.ClassifyVerification(
            ZellerCallDecision.Unknown,
            ZellerQueryResult.Found(approved, Transaction(ZellerTransactionStatus.Declined)),
            Purchase);

        Assert.Equal(ZellerVerificationDecision.Approved, verification.Decision);
        Assert.Same(approved, verification.Transaction);
    }

    [Fact]
    public void Two_approved_transactions_under_one_reference_stay_unknown_for_a_supervisor()
    {
        // 真机 S11：Zeller 不去重，同一个 reference 成交了两笔。
        var verification = ZellerResultClassifier.ClassifyVerification(
            ZellerCallDecision.Unknown,
            ZellerQueryResult.Found(Transaction(), Transaction(uuid: "98b690b7-5eaa-41ec-8ff8-168c60364aac")),
            Purchase);

        Assert.Equal(ZellerVerificationDecision.KeepUnknown, verification.Decision);
        Assert.Equal(ZellerUnknownReason.MultipleApproved, verification.UnknownReason);
    }

    [Fact]
    public void Approved_transaction_found_with_a_different_amount_stays_unknown()
    {
        var verification = ZellerResultClassifier.ClassifyVerification(
            ZellerCallDecision.VerifyThenNotPaid,
            ZellerQueryResult.Found(Transaction(amountCents: 99)),
            Purchase);

        Assert.Equal(ZellerVerificationDecision.KeepUnknown, verification.Decision);
        Assert.Equal(ZellerUnknownReason.AmountMismatch, verification.UnknownReason);
    }

    [Fact]
    public void Failed_query_is_not_evidence_of_no_payment()
    {
        var verification = ZellerResultClassifier.ClassifyVerification(
            ZellerCallDecision.VerifyThenNotPaid,
            ZellerQueryResult.Failed(ZellerErrorType.NetworkFailure),
            Purchase);

        Assert.Equal(ZellerVerificationDecision.KeepUnknown, verification.Decision);
        Assert.Equal(ZellerUnknownReason.VerificationFailed, verification.UnknownReason);
    }

    [Fact]
    public void Transactions_for_other_references_or_other_types_are_ignored()
    {
        var verification = ZellerResultClassifier.ClassifyVerification(
            ZellerCallDecision.VerifyThenNotPaid,
            ZellerQueryResult.Found(
                Transaction(reference: "HBZ-00000000000000000000000000000001"),
                Transaction(type: ZellerTransactionType.Refund)),
            Purchase);

        Assert.Equal(ZellerVerificationDecision.NotPaid, verification.Decision);
    }

    [Fact]
    public void Unrecognised_transaction_under_the_reference_stays_unknown()
    {
        var verification = ZellerResultClassifier.ClassifyVerification(
            ZellerCallDecision.VerifyThenNotPaid,
            ZellerQueryResult.Found(Transaction(ZellerTransactionStatus.Unknown)),
            Purchase);

        Assert.Equal(ZellerVerificationDecision.KeepUnknown, verification.Decision);
        Assert.Equal(ZellerUnknownReason.UnrecognisedStatus, verification.UnknownReason);
    }

    [Theory]
    [InlineData(ZellerCallDecision.Approved)]
    [InlineData(ZellerCallDecision.Declined)]
    [InlineData(ZellerCallDecision.NotSubmitted)]
    public void Final_decisions_cannot_be_verified(ZellerCallDecision pending)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ZellerResultClassifier.ClassifyVerification(pending, ZellerQueryResult.Found(), Purchase));
    }

    [Fact]
    public void Refund_verification_looks_at_refund_transactions_only()
    {
        var refund = Transaction(type: ZellerTransactionType.Refund);

        var verification = ZellerResultClassifier.ClassifyVerification(
            ZellerCallDecision.Unknown,
            ZellerQueryResult.Found(refund),
            Refund);

        Assert.Equal(ZellerVerificationDecision.Approved, verification.Decision);
        Assert.Same(refund, verification.Transaction);
    }

    // ── 未付款收尾之后的复核 ──

    [Fact]
    public void Late_recheck_with_no_transactions_keeps_the_attempt_closed()
    {
        var recheck = ZellerResultClassifier.ClassifyLateRecheck(ZellerQueryResult.Found(), Purchase);

        Assert.Equal(ZellerVerificationDecision.NotPaid, recheck.Decision);
        Assert.Equal(ZellerUnknownReason.None, recheck.UnknownReason);
    }

    [Fact]
    public void Late_recheck_that_finds_an_approval_reopens_the_attempt()
    {
        var found = Transaction();

        var recheck = ZellerResultClassifier.ClassifyLateRecheck(ZellerQueryResult.Found(found), Purchase);

        Assert.Equal(ZellerVerificationDecision.KeepUnknown, recheck.Decision);
        Assert.Equal(ZellerUnknownReason.LateApproval, recheck.UnknownReason);
        Assert.Same(found, recheck.Transaction);
    }

    [Fact]
    public void Late_recheck_that_cannot_query_stays_closed_but_flags_the_failure()
    {
        var recheck = ZellerResultClassifier.ClassifyLateRecheck(ZellerQueryResult.Failed(ZellerErrorType.NetworkFailure), Purchase);

        Assert.Equal(ZellerVerificationDecision.NotPaid, recheck.Decision);
        Assert.Equal(ZellerUnknownReason.VerificationFailed, recheck.UnknownReason);
    }

    [Fact]
    public void Late_recheck_that_finds_two_approvals_reopens_the_attempt()
    {
        var recheck = ZellerResultClassifier.ClassifyLateRecheck(
            ZellerQueryResult.Found(Transaction(), Transaction(uuid: "98b690b7-5eaa-41ec-8ff8-168c60364aac")),
            Purchase);

        Assert.Equal(ZellerVerificationDecision.KeepUnknown, recheck.Decision);
        Assert.Equal(ZellerUnknownReason.MultipleApproved, recheck.UnknownReason);
    }

    // ── 模型 ──

    [Fact]
    public void Error_type_values_match_the_sdk_so_the_gateway_can_convert_by_number()
    {
        Assert.Equal(0, (int)ZellerErrorType.NetworkFailure);
        Assert.Equal(3, (int)ZellerErrorType.OperationBusy);
        Assert.Equal(7, (int)ZellerErrorType.ConnectionFailure);
        Assert.Equal(12, (int)ZellerErrorType.Cancelled);
        Assert.Equal(13, (int)ZellerErrorType.InactivityTimeout);
        Assert.Equal(19, (int)ZellerErrorType.TransactionNotFound);
        Assert.Equal(25, (int)ZellerErrorType.TransactionFailure);
    }
}
