using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Api.Tests;

public sealed class CardTenderEvidenceCheckerTests
{
    [Fact]
    public void CheckConsistency_returns_nothing_for_an_approved_card_payment_with_matching_amounts()
    {
        var request = CardTenderTestData.Request(CardTenderTestData.CardPayment(12.50m));

        Assert.Empty(CardTenderEvidenceChecker.CheckConsistency(request));
    }

    [Fact]
    public void CheckConsistency_flags_card_payment_without_any_transaction()
    {
        foreach (IReadOnlyList<CardTransactionDto>? transactions in new IReadOnlyList<CardTransactionDto>?[] { null, [] })
        {
            var payment = CardTenderTestData.CardPayment(12.50m) with { CardTransactions = transactions };

            var issue = Assert.Single(CardTenderEvidenceChecker.CheckConsistency(CardTenderTestData.Request(payment)));

            Assert.Equal(CardTenderIssueTypes.NoCardTransactions, issue.IssueType);
            Assert.Equal(CardTenderIssueSources.OrderSync, issue.Source);
            Assert.Equal(payment.PaymentGuid.ToString("D"), issue.PaymentGuid);
        }
    }

    [Fact]
    public void CheckConsistency_flags_transaction_amounts_that_do_not_sum_to_the_payment()
    {
        var payment = CardTenderTestData.CardPayment(12.50m) with
        {
            CardTransactions = [CardTenderTestData.Transaction(10.00m)]
        };

        var issue = Assert.Single(CardTenderEvidenceChecker.CheckConsistency(CardTenderTestData.Request(payment)));

        Assert.Equal(CardTenderIssueTypes.CardAmountMismatch, issue.IssueType);
        Assert.Equal(CardTenderIssueSeverities.Error, issue.Severity);
    }

    [Fact]
    public void CheckConsistency_accepts_split_transactions_and_negative_refund_amounts()
    {
        var split = CardTenderTestData.CardPayment(12.50m) with
        {
            CardTransactions = [CardTenderTestData.Transaction(10.00m), CardTenderTestData.Transaction(2.50m)]
        };
        var refund = CardTenderTestData.CardPayment(-5.00m) with
        {
            CardTransactions = [CardTenderTestData.Transaction(5.00m)]
        };

        Assert.Empty(CardTenderEvidenceChecker.CheckConsistency(CardTenderTestData.Request(split, refund)));
    }

    [Theory]
    [InlineData("05")]
    [InlineData("")]
    [InlineData(null)]
    public void CheckConsistency_flags_linkly_transactions_without_an_approval_response_code(string? responseCode)
    {
        var payment = CardTenderTestData.CardPayment(12.50m) with
        {
            CardTransactions = [CardTenderTestData.Transaction(12.50m) with { ResponseCode = responseCode }]
        };

        var issue = Assert.Single(CardTenderEvidenceChecker.CheckConsistency(CardTenderTestData.Request(payment)));

        Assert.Equal(CardTenderIssueTypes.CardNotApproved, issue.IssueType);
    }

    [Theory]
    [InlineData("00")]
    [InlineData("08")]
    [InlineData("11")]
    public void CheckConsistency_accepts_every_linkly_approval_response_code(string responseCode)
    {
        var payment = CardTenderTestData.CardPayment(12.50m) with
        {
            CardTransactions = [CardTenderTestData.Transaction(12.50m) with { ResponseCode = responseCode }]
        };

        Assert.Empty(CardTenderEvidenceChecker.CheckConsistency(CardTenderTestData.Request(payment)));
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("Square")]
    public void CheckConsistency_does_not_require_a_bank_response_code_for_manual_or_square(string processor)
    {
        // 人工确认没有银行响应码；Square 的批准状态在 ResponseText，不带 ISO 响应码。
        var payment = CardTenderTestData.CardPayment(12.50m) with
        {
            CardTransactions = [CardTenderTestData.Transaction(12.50m) with { Processor = processor, ResponseCode = null }]
        };

        Assert.Empty(CardTenderEvidenceChecker.CheckConsistency(CardTenderTestData.Request(payment)));
    }

    [Fact]
    public void CheckConsistency_ignores_cash_and_zero_amount_card_payments()
    {
        var cash = new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Cash, 5m, null);
        var zeroCard = new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Card, 0m, null);

        Assert.Empty(CardTenderEvidenceChecker.CheckConsistency(CardTenderTestData.Request(cash, zeroCard)));
    }
}

public sealed class CardTenderOrderVerifierTests
{
    private const string SessionId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task VerifyAsync_links_an_approved_matching_session_and_records_nothing()
    {
        var repository = new FakeCardTenderRepository { Sessions = [ApprovedSession()] };
        var request = RequestFor(12.50m);

        await CreateVerifier(repository).VerifyAsync(request);

        Assert.Empty(repository.UpsertedIssues);
        var link = Assert.Single(repository.Links);
        Assert.Equal(7, link.SessionRowId);
        Assert.Equal(request.OrderGuid.ToString("D"), link.OrderGuid);
    }

    [Fact]
    public async Task VerifyAsync_registers_issue_when_no_session_matches_the_reference()
    {
        var repository = new FakeCardTenderRepository();

        await CreateVerifier(repository).VerifyAsync(RequestFor(12.50m));

        var issue = Assert.Single(repository.UpsertedIssues);
        Assert.Equal(CardTenderIssueTypes.SessionNotFound, issue.IssueType);
        Assert.Equal(SessionId, issue.SessionId);
        Assert.Equal("Sandbox", issue.Environment);
        Assert.Empty(repository.Links);
    }

    [Theory]
    [InlineData("SupervisorResolved", false)]
    [InlineData("Cancelled", false)]
    [InlineData("Failed", false)]
    [InlineData("Pending", null)]
    [InlineData("Completed", false)]
    [InlineData("Completed", null)]
    public async Task VerifyAsync_registers_issue_when_session_is_not_an_approved_transaction(string status, bool? success)
    {
        var session = ApprovedSession();
        session.Status = status;
        session.TransactionSuccess = success;
        var repository = new FakeCardTenderRepository { Sessions = [session] };

        await CreateVerifier(repository).VerifyAsync(RequestFor(12.50m));

        var issue = Assert.Single(repository.UpsertedIssues);
        Assert.Equal(CardTenderIssueTypes.SessionNotApproved, issue.IssueType);
        Assert.Equal(CardTenderIssueSeverities.Error, issue.Severity);
        // 会话身份明确，仍然回链订单，避免对账把它当成「无订单」。
        Assert.Single(repository.Links);
    }

    [Fact]
    public async Task VerifyAsync_registers_issue_when_the_session_is_not_a_purchase_transaction()
    {
        var session = ApprovedSession();
        session.OperationType = "Settlement";
        var repository = new FakeCardTenderRepository { Sessions = [session] };

        await CreateVerifier(repository).VerifyAsync(RequestFor(12.50m));

        Assert.Equal(CardTenderIssueTypes.SessionNotApproved, Assert.Single(repository.UpsertedIssues).IssueType);
    }

    [Fact]
    public async Task VerifyAsync_registers_issue_when_the_session_amount_differs_from_the_payment()
    {
        var session = ApprovedSession();
        session.RequestAmountCents = 1000;
        var repository = new FakeCardTenderRepository { Sessions = [session] };

        await CreateVerifier(repository).VerifyAsync(RequestFor(12.50m));

        var issue = Assert.Single(repository.UpsertedIssues);
        Assert.Equal(CardTenderIssueTypes.SessionAmountMismatch, issue.IssueType);
        Assert.Contains("1000", issue.Detail);
        Assert.Contains("1250", issue.Detail);
    }

    [Fact]
    public async Task VerifyAsync_registers_issue_when_payment_sign_contradicts_the_session_txn_type()
    {
        var refundSession = ApprovedSession();
        refundSession.RequestTxnType = "R";
        var repository = new FakeCardTenderRepository { Sessions = [refundSession] };

        await CreateVerifier(repository).VerifyAsync(RequestFor(12.50m));

        Assert.Equal(CardTenderIssueTypes.SessionTxnTypeMismatch, Assert.Single(repository.UpsertedIssues).IssueType);
    }

    [Fact]
    public async Task VerifyAsync_accepts_a_refund_session_for_a_negative_payment()
    {
        var refundSession = ApprovedSession();
        refundSession.RequestTxnType = "R";
        var repository = new FakeCardTenderRepository { Sessions = [refundSession] };

        await CreateVerifier(repository).VerifyAsync(RequestFor(-12.50m));

        Assert.Empty(repository.UpsertedIssues);
    }

    [Fact]
    public async Task VerifyAsync_registers_issue_when_uploaded_txn_ref_is_not_the_session_txn_ref()
    {
        // WPF 恢复路径在拿不到卡明细时会用会话号兜底拼 TxnRef。
        var request = RequestFor(12.50m, transactionTxnRef: SessionId);
        var repository = new FakeCardTenderRepository { Sessions = [ApprovedSession()] };

        await CreateVerifier(repository).VerifyAsync(request);

        Assert.Equal(CardTenderIssueTypes.SessionTxnRefMismatch, Assert.Single(repository.UpsertedIssues).IssueType);
    }

    [Fact]
    public async Task VerifyAsync_registers_error_and_does_not_link_a_session_from_another_store()
    {
        var session = ApprovedSession();
        session.StoreCode = "S99";
        var repository = new FakeCardTenderRepository { Sessions = [session] };

        await CreateVerifier(repository).VerifyAsync(RequestFor(12.50m));

        var issue = Assert.Single(repository.UpsertedIssues);
        Assert.Equal(CardTenderIssueTypes.SessionScopeMismatch, issue.IssueType);
        Assert.Equal(CardTenderIssueSeverities.Error, issue.Severity);
        Assert.Empty(repository.Links);
    }

    [Fact]
    public async Task VerifyAsync_registers_warning_but_still_links_a_session_from_another_device_in_the_same_store()
    {
        var session = ApprovedSession();
        session.DeviceCode = "POS99";
        var repository = new FakeCardTenderRepository { Sessions = [session] };

        await CreateVerifier(repository).VerifyAsync(RequestFor(12.50m));

        var issue = Assert.Single(repository.UpsertedIssues);
        Assert.Equal(CardTenderIssueTypes.SessionScopeMismatch, issue.IssueType);
        Assert.Equal(CardTenderIssueSeverities.Warning, issue.Severity);
        Assert.Single(repository.Links);
    }

    [Fact]
    public async Task VerifyAsync_registers_issue_when_the_session_is_already_linked_to_another_order()
    {
        var session = ApprovedSession();
        var otherOrder = Guid.NewGuid().ToString("D");
        session.OrderGuid = otherOrder;
        var repository = new FakeCardTenderRepository { Sessions = [session] };

        await CreateVerifier(repository).VerifyAsync(RequestFor(12.50m));

        var issue = Assert.Single(repository.UpsertedIssues);
        Assert.Equal(CardTenderIssueTypes.SessionLinkedToOtherOrder, issue.IssueType);
        Assert.Contains(otherOrder, issue.Detail);
    }

    [Fact]
    public async Task VerifyAsync_is_idempotent_when_the_session_is_already_linked_to_this_order()
    {
        var request = RequestFor(12.50m);
        var session = ApprovedSession();
        session.OrderGuid = request.OrderGuid.ToString("D");
        var repository = new FakeCardTenderRepository { Sessions = [session] };

        await CreateVerifier(repository).VerifyAsync(request);

        Assert.Empty(repository.UpsertedIssues);
    }

    [Fact]
    public async Task VerifyAsync_does_not_look_up_sessions_for_non_backend_card_references()
    {
        var repository = new FakeCardTenderRepository();
        var payment = CardTenderTestData.CardPayment(12.50m) with { Reference = "ANZCLOUD:2610090000" };

        await CreateVerifier(repository).VerifyAsync(CardTenderTestData.Request(payment));

        Assert.Equal(0, repository.FindCalls);
        Assert.Empty(repository.UpsertedIssues);
    }

    [Fact]
    public async Task VerifyAsync_registers_consistency_issues_together_with_session_issues()
    {
        var payment = CardTenderTestData.CardPayment(12.50m) with
        {
            Reference = BackendReference(),
            CardTransactions = []
        };
        var repository = new FakeCardTenderRepository { Sessions = [ApprovedSession()] };

        await CreateVerifier(repository).VerifyAsync(CardTenderTestData.Request(payment));

        Assert.Equal(
            [CardTenderIssueTypes.NoCardTransactions],
            repository.UpsertedIssues.Select(issue => issue.IssueType));
    }

    [Fact]
    public async Task VerifyAsync_never_throws_when_the_repository_fails()
    {
        var repository = new FakeCardTenderRepository { ThrowOnFind = true };

        await CreateVerifier(repository).VerifyAsync(RequestFor(12.50m));

        Assert.Empty(repository.UpsertedIssues);
    }

    [Fact]
    public void Issue_dedup_key_is_stable_per_issue_type_order_payment_and_session()
    {
        var first = new CardTenderIssue(
            CardTenderIssueTypes.SessionNotApproved, "Error", "OrderSync", "S01", "POS01", "Sandbox", SessionId, null, "o", "p", 1m, "a");
        var second = first with { Detail = "different detail", Amount = 2m };

        Assert.Equal(first.DedupKey, second.DedupKey);
        Assert.NotEqual(first.DedupKey, (first with { IssueType = CardTenderIssueTypes.SessionAmountMismatch }).DedupKey);
    }

    private static CardTenderOrderVerifier CreateVerifier(FakeCardTenderRepository repository) => new(repository);

    private static string BackendReference() =>
        LinklyBackendPaymentReference.Format("2610090001", SessionId, "Sandbox", null);

    private static OrderSyncRequest RequestFor(decimal amount, string transactionTxnRef = "2610090001")
    {
        var payment = CardTenderTestData.CardPayment(amount) with
        {
            Reference = BackendReference(),
            CardTransactions = [CardTenderTestData.Transaction(Math.Abs(amount)) with { TxnRef = transactionTxnRef }]
        };
        return CardTenderTestData.Request(payment);
    }

    private static CardTenderSessionFact ApprovedSession() => new()
    {
        Id = 7,
        Environment = "Sandbox",
        StoreCode = "S01",
        DeviceCode = "POS01",
        SessionId = SessionId,
        Status = "Completed",
        TxnRef = "2610090001",
        RequestTxnType = "P",
        RequestAmountCents = 1250,
        TransactionSuccess = true,
        OperationType = "Transaction"
    };

    private sealed class FakeCardTenderRepository : ICardTenderReconciliationRepository
    {
        public IReadOnlyList<CardTenderSessionFact> Sessions { get; init; } = [];

        public bool ThrowOnFind { get; init; }

        public int FindCalls { get; private set; }

        public List<(long SessionRowId, string OrderGuid)> Links { get; } = [];

        public List<CardTenderIssue> UpsertedIssues { get; } = [];

        public Task<IReadOnlyList<CardTenderSessionFact>> FindSessionsAsync(
            string environment,
            string storeCode,
            string sessionId,
            CancellationToken cancellationToken)
        {
            FindCalls++;
            if (ThrowOnFind)
            {
                throw new InvalidOperationException("db down");
            }

            return Task.FromResult(Sessions);
        }

        public Task<string?> TryLinkSessionToOrderAsync(long sessionRowId, string orderGuid, CancellationToken cancellationToken)
        {
            Links.Add((sessionRowId, orderGuid));
            var existing = Sessions.FirstOrDefault(session => session.Id == sessionRowId)?.OrderGuid;
            return Task.FromResult<string?>(existing ?? orderGuid);
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
    }
}

internal static class CardTenderTestData
{
    public static PaymentSyncDto CardPayment(decimal amount) => new(
        Guid.NewGuid(),
        PaymentMethodKind.Card,
        amount,
        "ANZ:2610090001",
        CardTransactions: [Transaction(Math.Abs(amount))]);

    public static CardTransactionDto Transaction(decimal amount) => new(
        "ANZ",
        "2610090001",
        "123456",
        "Visa",
        null,
        "****1234",
        "MERCHANT",
        "00",
        "APPROVED",
        "000001",
        DateTimeOffset.Parse("2026-10-09T01:00:00Z"),
        amount,
        null);

    public static OrderSyncRequest Request(params PaymentSyncDto[] payments) => new(
        Guid.NewGuid(),
        "S01",
        "POS01",
        "C01",
        "Cashier",
        DateTimeOffset.Parse("2026-10-09T01:00:00Z"),
        12.50m,
        0m,
        12.50m,
        [
            new OrderLineSyncDto(
                Guid.NewGuid(), "P01", null, "Apple", "BAR01", 1m, 12.50m, 0m, 12.50m, PriceSourceKind.StoreRetailPrice)
        ],
        payments);
}
