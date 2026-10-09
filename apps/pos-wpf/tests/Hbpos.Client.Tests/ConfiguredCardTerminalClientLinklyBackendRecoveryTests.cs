using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

/// <summary>
/// 分期（以及其他走 IInstallmentTerminalRecoveryClient 的调用方）恢复 Linkly attempt 时，
/// 后端模式必须用状态接口已核验的 CardTransaction 认定批准，直连模式必须走直连客户端而不是后端会话查询。
/// </summary>
public sealed class ConfiguredCardTerminalClientLinklyBackendRecoveryTests
{
    private const string SessionId = "7d9f6c1e-0000-4000-8000-000000000001";

    private static readonly PosSessionState Session = new("HB POS", "S001", "Main", "POS-01", "C001", "Alice", true, 0);

    private static readonly Guid AttemptGuid = Guid.Parse("f588a502-9129-e8ad-8627-3d9e1b2b892a");

    private static string DerivedTxnRef => LinklyLocalTxnRef.Create('P', AttemptGuid.ToString("D"));

    [Fact]
    public async Task Backend_completed_success_with_verified_card_transaction_is_recovered_as_approved()
    {
        var backend = new FakeBackend { Status = CreateCompletedStatus(DerivedTxnRef, amountCents: 4000) };
        var client = CreateClient(backend, LinklyConnectionMode.CloudBackendAsync);

        var result = await client.RecoverLinklyAsync(CreateAttempt(LinklyConnectionMode.CloudBackendAsync, SessionId), Session);

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(40m, result.AuthorizedAmount);
        var transaction = Assert.Single(result.CardTransactions!);
        Assert.Equal(DerivedTxnRef, transaction.TxnRef);
        Assert.Equal(40m, transaction.Amount);
        Assert.Equal("00", transaction.ResponseCode);
        Assert.Equal(SessionId, result.SessionId);
        Assert.Equal(DerivedTxnRef, result.TxnRef);
        Assert.Contains(SessionId, result.Reference);
    }

    [Fact]
    public async Task Backend_completed_success_with_mismatched_card_amount_stays_unknown()
    {
        // 状态接口里的金额与 attempt 不一致：不能用 attempt 的请求金额补造批准。
        var backend = new FakeBackend { Status = CreateCompletedStatus(DerivedTxnRef, amountCents: 3900) };
        var client = CreateClient(backend, LinklyConnectionMode.CloudBackendAsync);

        var result = await client.RecoverLinklyAsync(CreateAttempt(LinklyConnectionMode.CloudBackendAsync, SessionId), Session);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task Backend_completed_success_without_card_evidence_stays_unknown()
    {
        var backend = new FakeBackend { Status = CreateCompletedStatus(DerivedTxnRef, amountCents: null) with { CardTransaction = null } };
        var client = CreateClient(backend, LinklyConnectionMode.CloudBackendAsync);

        var result = await client.RecoverLinklyAsync(CreateAttempt(LinklyConnectionMode.CloudBackendAsync, SessionId), Session);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task Backend_completed_success_for_another_txn_ref_stays_unknown()
    {
        var backend = new FakeBackend { Status = CreateCompletedStatus("P0000000000000XX", amountCents: 4000) };
        var client = CreateClient(backend, LinklyConnectionMode.CloudBackendAsync);

        var result = await client.RecoverLinklyAsync(CreateAttempt(LinklyConnectionMode.CloudBackendAsync, SessionId), Session);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task Backend_declined_session_is_reported_as_explicit_rejection()
    {
        var declined = CreateCompletedStatus(DerivedTxnRef, amountCents: 4000) with
        {
            TransactionSuccess = false,
            ResponseCode = "05",
            ResponseText = "DECLINED"
        };
        var backend = new FakeBackend { Status = declined };
        var client = CreateClient(backend, LinklyConnectionMode.CloudBackendAsync);

        var result = await client.RecoverLinklyAsync(CreateAttempt(LinklyConnectionMode.CloudBackendAsync, SessionId), Session);

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("05", result.ResponseCode);
    }

    [Fact]
    public async Task Backend_attempt_without_session_id_claims_the_unacknowledged_session_by_derived_txn_ref()
    {
        // POST 已受理但响应丢失：本地没有 SessionId，只有发请求前落库的派生引用。
        var backend = new FakeBackend { Resumable = CreateCompletedStatus(DerivedTxnRef, amountCents: 4000) };
        var client = CreateClient(backend, LinklyConnectionMode.CloudBackendAsync);

        var result = await client.RecoverLinklyAsync(CreateAttempt(LinklyConnectionMode.CloudBackendAsync, sessionId: null), Session);

        Assert.True(result.Approved);
        Assert.Equal(1, backend.ResumableCalls);
        Assert.Equal(0, backend.StatusCalls);
    }

    [Fact]
    public async Task Backend_attempt_without_session_id_does_not_claim_a_session_with_another_txn_ref()
    {
        var backend = new FakeBackend { Resumable = CreateCompletedStatus("P0000000000000XX", amountCents: 4000) };
        var client = CreateClient(backend, LinklyConnectionMode.CloudBackendAsync);

        var result = await client.RecoverLinklyAsync(CreateAttempt(LinklyConnectionMode.CloudBackendAsync, sessionId: null), Session);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task Backend_attempt_without_session_id_and_txn_ref_is_not_guessed_from_the_device_session()
    {
        var backend = new FakeBackend { Resumable = CreateCompletedStatus(DerivedTxnRef, amountCents: 4000) };
        var client = CreateClient(backend, LinklyConnectionMode.CloudBackendAsync);

        var result = await client.RecoverLinklyAsync(
            CreateAttempt(LinklyConnectionMode.CloudBackendAsync, sessionId: null, txnRef: null),
            Session);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(0, backend.ResumableCalls);
        Assert.Equal(0, backend.StatusCalls);
    }

    [Fact]
    public async Task Direct_cloud_attempt_recovers_through_the_direct_client_and_never_queries_the_backend()
    {
        var backend = new FakeBackend();
        var cloud = new FakeCloud
        {
            Result = new PaymentAuthorizationResult(
                true,
                $"ANZCLOUD:{DerivedTxnRef}",
                AuthorizedAmount: 40m,
                CardTransactions:
                [
                    new CardTransactionDto("ANZ", DerivedTxnRef, null, null, null, null, null, "00", "APPROVED", null, DateTimeOffset.UtcNow, 40m, null)
                ],
                Processor: "ANZ",
                TxnType: "P",
                TxnRef: DerivedTxnRef)
        };
        var client = CreateClient(backend, LinklyConnectionMode.CloudDirectSync, cloud);

        var result = await client.RecoverLinklyAsync(CreateAttempt(LinklyConnectionMode.CloudDirectSync, SessionId), Session);

        Assert.True(result.Approved);
        Assert.Equal(1, cloud.RecoverCalls);
        Assert.Equal(SessionId, cloud.LastSessionId);
        Assert.Equal(DerivedTxnRef, cloud.LastTxnRef);
        Assert.Equal(40m, cloud.LastAmount);
        Assert.Equal(LinklyConnectionMode.CloudDirectSync, cloud.LastSettings!.LinklyConnectionMode);
        Assert.Equal(0, backend.StatusCalls);
        Assert.Equal(0, backend.ResumableCalls);
    }

    [Fact]
    public async Task Direct_cloud_attempt_without_a_bound_session_stays_unknown_without_any_query()
    {
        var backend = new FakeBackend();
        var cloud = new FakeCloud();
        var client = CreateClient(backend, LinklyConnectionMode.CloudDirectSync, cloud);

        var result = await client.RecoverLinklyAsync(CreateAttempt(LinklyConnectionMode.CloudDirectSync, sessionId: null), Session);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(0, cloud.RecoverCalls);
        Assert.Equal(0, backend.StatusCalls);
    }

    [Fact]
    public async Task Direct_cloud_recovery_with_a_conflicting_identity_stays_unknown()
    {
        var cloud = new FakeCloud
        {
            Result = new PaymentAuthorizationResult(
                true,
                "ANZCLOUD:POTHERTXNREF0001",
                AuthorizedAmount: 40m,
                CardTransactions:
                [
                    new CardTransactionDto("ANZ", "POTHERTXNREF0001", null, null, null, null, null, "00", "APPROVED", null, DateTimeOffset.UtcNow, 40m, null)
                ],
                Processor: "ANZ",
                TxnType: "P",
                TxnRef: "POTHERTXNREF0001")
        };
        var client = CreateClient(new FakeBackend(), LinklyConnectionMode.CloudDirectSync, cloud);

        var result = await client.RecoverLinklyAsync(CreateAttempt(LinklyConnectionMode.CloudDirectSync, SessionId), Session);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    private static ConfiguredCardTerminalClient CreateClient(
        FakeBackend backend,
        LinklyConnectionMode settingsMode,
        FakeCloud? cloud = null)
    {
        var settings = CardTerminalSettings.FromEnvironment() with
        {
            Processor = CardProcessorKind.Linkly,
            Environment = CardTerminalEnvironment.Sandbox,
            LinklyConnectionMode = settingsMode
        };
        return new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            new HttpClient(new ThrowingHandler()),
            linklyBackendTerminalClient: backend,
            linklyCloudTerminalClient: cloud);
    }

    private static LocalCardPaymentAttempt CreateAttempt(
        LinklyConnectionMode mode,
        string? sessionId,
        string? txnRef = "derived")
    {
        var now = DateTimeOffset.UtcNow;
        return new LocalCardPaymentAttempt(
            AttemptGuid,
            sessionId,
            txnRef == "derived" ? DerivedTxnRef : txnRef,
            CardProcessorKind.Linkly.ToString(),
            CardTerminalEnvironment.Sandbox.ToString(),
            CardTerminalSettings.FormatLinklyConnectionMode(mode),
            "P",
            40m,
            LocalCardPaymentAttemptStatus.Recovering,
            "{}",
            "S001",
            "POS-01",
            "C001",
            null,
            null,
            null,
            now.AddMinutes(-2),
            now.AddMinutes(-1),
            null,
            null,
            "Repayment",
            Guid.NewGuid());
    }

    private static LinklyCloudBackendSessionResponse CreateCompletedStatus(string txnRef, long? amountCents)
    {
        return new LinklyCloudBackendSessionResponse(
            "Sandbox",
            "S001",
            "POS-01",
            SessionId,
            "Completed",
            txnRef,
            "00",
            "APPROVED",
            null,
            "APPROVED",
            false,
            false,
            false,
            false,
            false,
            null,
            null,
            null,
            "RECEIPT",
            0,
            null,
            null,
            200,
            [],
            TransactionSuccess: true)
        {
            CardTransaction = new LinklyCloudBackendCardTransactionDto(
                txnRef,
                null,
                "AUTH01",
                "VISA",
                "************1234",
                "MID01",
                "00",
                "APPROVED",
                "000123",
                DateTimeOffset.UtcNow,
                amountCents)
        };
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP must not be called.");
    }

    private sealed class FakeBackend : ILinklyBackendTerminalClient
    {
        public LinklyCloudBackendSessionResponse? Status { get; init; }

        public LinklyCloudBackendSessionResponse? Resumable { get; init; }

        public int StatusCalls { get; private set; }

        public int ResumableCalls { get; private set; }

        public Task<LinklyConnectionTestResult> TestConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyConnectionTestResult> TestTransactionStatusAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> PurchaseAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default)
        {
            ResumableCalls++;
            return Task.FromResult(Resumable);
        }

        public Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> ResumeSessionUntilFinalAsync(
            CardTerminalSettings settings,
            LinklyCloudBackendSessionResponse activeStatus,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            StatusCalls++;
            return Status is null
                ? Task.FromException<LinklyCloudBackendSessionResponse>(new HttpRequestException("not found"))
                : Task.FromResult(Status);
        }

        public Task AcknowledgeSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AcknowledgeSupervisorResolvedSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCloud : ILinklyCloudTerminalClient
    {
        public PaymentAuthorizationResult Result { get; init; } = new(false, ResultUnknown: true);

        public int RecoverCalls { get; private set; }

        public decimal LastAmount { get; private set; }

        public string? LastSessionId { get; private set; }

        public string? LastTxnRef { get; private set; }

        public CardTerminalSettings? LastSettings { get; private set; }

        public Task<LinklyConnectionTestResult> TestConnectionAsync(
            CardTerminalSettings settings,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> PurchaseAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> RecoverTransactionAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string sessionId,
            string txnRef,
            CancellationToken cancellationToken = default)
        {
            RecoverCalls++;
            LastAmount = amount;
            LastSessionId = sessionId;
            LastTxnRef = txnRef;
            LastSettings = settings;
            return Task.FromResult(Result);
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
