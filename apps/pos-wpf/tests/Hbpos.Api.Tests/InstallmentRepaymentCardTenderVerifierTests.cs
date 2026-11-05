using Hbpos.Api.Services;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Api.Tests;

public sealed class InstallmentRepaymentCardTenderVerifierTests
{
    private const string SessionId = "session-installment-1";

    [Fact]
    public async Task Clean_square_repayment_records_no_issue()
    {
        var repository = new FakeRepository();
        var verifier = new InstallmentRepaymentCardTenderVerifier(repository);
        var claim = Claim("Square");

        await verifier.VerifyAsync(claim, Commit(null, Transaction("Square", "SQ-PAY-1", 12.50m, null)));

        Assert.Empty(repository.UpsertedIssues);
        Assert.Equal(["SQ-PAY-1"], repository.TxnRefLookups);
    }

    [Fact]
    public async Task Unapproved_linkly_response_code_is_recorded_against_the_installment()
    {
        var repository = new FakeRepository();
        var verifier = new InstallmentRepaymentCardTenderVerifier(repository);
        var claim = Claim("Linkly");

        await verifier.VerifyAsync(claim, Commit("ANZ:TXN-1", Transaction("ANZ", "TXN-1", 12.50m, "05")));

        var issue = Assert.Single(repository.UpsertedIssues);
        Assert.Equal(CardTenderIssueTypes.CardNotApproved, issue.IssueType);
        Assert.Equal(CardTenderIssueSources.InstallmentRepayment, issue.Source);
        Assert.Equal(claim.InstallmentGuid.ToString("D"), issue.OrderGuid);
        Assert.Equal(claim.PaymentGuid.ToString("D"), issue.PaymentGuid);
        Assert.Equal("POS-01", issue.DeviceCode);
    }

    [Fact]
    public async Task Card_approval_already_recorded_on_another_payment_is_flagged_as_duplicate()
    {
        var repository = new FakeRepository { OtherPayments = ["other-payment"] };
        var verifier = new InstallmentRepaymentCardTenderVerifier(repository);

        await verifier.VerifyAsync(Claim("Zeller"), Commit("ZELLER:REF:UUID", Transaction("Zeller", "UUID", 12.50m, null)));

        var issue = Assert.Single(repository.UpsertedIssues);
        Assert.Equal(CardTenderIssueTypes.DuplicateCardTxnRef, issue.IssueType);
        Assert.Equal("UUID", issue.TxnRef);
        Assert.Contains("other-payment", issue.Detail);
    }

    [Fact]
    public async Task Linkly_backend_session_is_checked_and_linked_to_the_installment()
    {
        var repository = new FakeRepository { Sessions = [ApprovedSession(requestAmountCents: 999)] };
        var verifier = new InstallmentRepaymentCardTenderVerifier(repository);
        var claim = Claim("Linkly");
        var reference = LinklyBackendPaymentReference.Format("TXN-1", SessionId, "Sandbox", null);

        await verifier.VerifyAsync(claim, Commit(reference, Transaction("ANZ", "TXN-1", 12.50m, "00")));

        Assert.Equal([(7L, claim.InstallmentGuid.ToString("D"))], repository.Links);
        var issue = Assert.Single(repository.UpsertedIssues);
        Assert.Equal(CardTenderIssueTypes.SessionAmountMismatch, issue.IssueType);
        Assert.Equal(SessionId, issue.SessionId);
        Assert.Equal("Sandbox", issue.Environment);
    }

    [Fact]
    public async Task Verification_failures_never_escape_to_the_committed_repayment()
    {
        var repository = new FakeRepository { ThrowOnLookup = true };
        var verifier = new InstallmentRepaymentCardTenderVerifier(repository);

        await verifier.VerifyAsync(Claim("Square"), Commit(null, Transaction("Square", "SQ-PAY-1", 12.50m, null)));

        Assert.Empty(repository.UpsertedIssues);
    }

    private static InstallmentRepaymentClaimRecord Claim(string provider) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "S01",
        "POS-01",
        "C01",
        "Cashier One",
        12.50m,
        PaymentMethodKind.Card,
        "idem-1",
        new string('a', 64),
        InstallmentRepaymentClaimStatus.Committed,
        provider,
        "attempt-1",
        DateTimeOffset.Parse("2026-11-05T00:00:00Z"),
        DateTimeOffset.Parse("2026-11-05T00:00:00Z"),
        null,
        DateTimeOffset.Parse("2026-11-05T00:01:00Z"),
        3);

    private static InstallmentRepaymentClaimCommitRequest Commit(string? reference, CardTransactionDto transaction) =>
        new(Reference: reference, CardTransactions: [transaction]);

    private static CardTransactionDto Transaction(string processor, string txnRef, decimal amount, string? responseCode) => new(
        processor,
        txnRef,
        "AUTH",
        "VISA",
        null,
        "****1234",
        "MERCHANT",
        responseCode,
        "APPROVED",
        null,
        DateTimeOffset.Parse("2026-11-05T00:00:30Z"),
        amount,
        null);

    private static CardTenderSessionFact ApprovedSession(long requestAmountCents) => new()
    {
        Id = 7,
        Environment = "Sandbox",
        StoreCode = "S01",
        DeviceCode = "POS-01",
        SessionId = SessionId,
        Status = "Completed",
        TxnRef = "TXN-1",
        RequestTxnType = "P",
        RequestAmountCents = requestAmountCents,
        TransactionSuccess = true,
        OperationType = "Transaction"
    };

    private sealed class FakeRepository : ICardTenderReconciliationRepository
    {
        public IReadOnlyList<CardTenderSessionFact> Sessions { get; init; } = [];

        public IReadOnlyList<string> OtherPayments { get; init; } = [];

        public bool ThrowOnLookup { get; init; }

        public List<string> TxnRefLookups { get; } = [];

        public List<(long SessionRowId, string OrderGuid)> Links { get; } = [];

        public List<CardTenderIssue> UpsertedIssues { get; } = [];

        public Task<IReadOnlyList<CardTenderSessionFact>> FindSessionsAsync(
            string environment, string storeCode, string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(Sessions);

        public Task<string?> TryLinkSessionToOrderAsync(long sessionRowId, string orderGuid, CancellationToken cancellationToken)
        {
            Links.Add((sessionRowId, orderGuid));
            return Task.FromResult<string?>(orderGuid);
        }

        public Task UpsertIssuesAsync(IReadOnlyList<CardTenderIssue> issues, CancellationToken cancellationToken)
        {
            UpsertedIssues.AddRange(issues);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CardTenderOrphanSessionCandidate>> FindApprovedSessionsWithoutOrderAsync(
            DateTime completedAfterUtc, DateTime completedBeforeUtc, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CardTenderOrphanSessionCandidate>>([]);

        public Task<string?> FindOrderGuidByBackendPaymentAsync(
            string environment, string sessionId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<int> ResolveIssuesForLinkedSessionsAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<IReadOnlyList<string>> FindOtherPaymentsWithCardTxnRefAsync(
            string txnRef, Guid excludePaymentGuid, CancellationToken cancellationToken)
        {
            if (ThrowOnLookup)
            {
                throw new InvalidOperationException("db down");
            }

            TxnRefLookups.Add(txnRef);
            return Task.FromResult(OtherPayments);
        }
    }
}
