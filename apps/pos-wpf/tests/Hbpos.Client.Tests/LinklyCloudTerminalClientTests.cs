using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class LinklyCloudTerminalClientTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(2, 1, 1)]
    public async Task PurchaseAsync_cancellation_before_submission_reports_not_submitted_without_another_post(
        int cancelAt, int expectedBindings, int expectedPosts)
    {
        using var cancellation = new CancellationTokenSource();
        var bindings = 0;
        var accessor = new LinklyPaymentAttemptContextAccessor();
        using var scope = accessor.Begin(new LinklyPaymentAttemptContext(
            Guid.NewGuid(),
            (_, _, _, _) =>
            {
                bindings++;
                if (cancelAt == 1) cancellation.Cancel();
                return Task.CompletedTask;
            },
            LinklyLocalTxnRef.Create('P', "cancel-before-post")));
        var apiClient = new FakeLinklyCloudApiClient
        {
            TransactionResult = NotSubmitted("original-session"),
            TransactionResponseTransform = result =>
            {
                if (cancelAt == 2) cancellation.Cancel();
                return result;
            }
        };
        var client = new LinklyCloudTerminalClient(apiClient, new FakeLinklyCloudSecretStore(),
            linklyPaymentAttemptContextAccessor: accessor);
        if (cancelAt == 0) cancellation.Cancel();

        // 中文注释：工作流依赖此明确边界释放未扣款订单，不能把仅已落库的身份当作已提交。
        await Assert.ThrowsAsync<CardTerminalNotSubmittedException>(() =>
            client.PurchaseAsync(10m, CreateSession(), CreateSettings(), cancellation.Token));

        Assert.Equal(expectedBindings, bindings);
        Assert.Equal(expectedPosts, apiClient.SendTransactionCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_unexpected_exception_after_submission_preserves_recovery_identity_without_retry()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            TransactionException = new InvalidOperationException("response lost after submission")
        };
        var client = new LinklyCloudTerminalClient(apiClient, new FakeLinklyCloudSecretStore());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(apiClient.LastTransactionSessionId, result.SessionId);
        Assert.Equal(Assert.Single(apiClient.SentTransactionTxnRefs), result.TxnRef);
        Assert.Equal(1, apiClient.SendTransactionCallCount);
    }

    [Theory]
    [InlineData(false, "session")]
    [InlineData(false, "reference")]
    [InlineData(false, "amount")]
    [InlineData(true, "session")]
    [InlineData(true, "reference")]
    [InlineData(true, "amount")]
    public async Task PurchaseAsync_checks_each_approved_identity_field_independently(bool poll, string mismatch)
    {
        var api = new FakeLinklyCloudApiClient
        {
            TransactionResult = poll ? Pending("session") : Approved("session", "reference"),
            TransactionResponseTransform = result => result.Outcome != LinklyCloudTransactionOutcome.Completed ? result : mismatch switch
            {
                "session" => result with { SessionId = "foreign-session" },
                "reference" => result with { TxnRef = "FOREIGN-TXN" },
                _ => result with { Amount = 11m }
            }
        };
        if (poll) api.TransactionStatusSequence.Enqueue(Approved("session", "reference"));
        var client = new LinklyCloudTerminalClient(api, new FakeLinklyCloudSecretStore(), TimeSpan.Zero);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(api.LastTransactionSessionId, result.SessionId);
        Assert.Equal(api.SentTransactionTxnRefs.Single(), result.TxnRef);
        Assert.Equal(1, api.SendTransactionCallCount);
        Assert.Equal(poll ? 1 : 0, api.GetTransactionCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_uses_pos_id_and_maps_approved_transaction()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            TransactionResult = new LinklyCloudTransactionResult(
                "session-1",
                true,
                "TXN-1",
                "123456",
                "VISA",
                "4",
                "411111******1234",
                "MID",
                "00",
                "APPROVED",
                "42",
                10m,
                "RFN-1")
        };
        var store = new FakeLinklyCloudSecretStore();
        var client = new LinklyCloudTerminalClient(apiClient, store);

        var result = await client.PurchaseAsync(
            10m,
            CreateSession(),
            CreateSettings() with { LinklyPosVendorId = null });

        Assert.True(result.Approved);
        Assert.Equal($"ANZCLOUD:{apiClient.SentTransactionTxnRefs.Single()}:RFN-1", result.Reference);
        Assert.Equal("pos-id-1", apiClient.LastPosId);
        Assert.Equal(CardTerminalEnvironment.Production, store.LastEnvironment);
        Assert.Equal("S01", store.LastStoreCode);
        Assert.Equal("TERM-1", store.LastDeviceCode);
        var transaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("ANZ", transaction.Processor);
        Assert.Equal("****1234", transaction.MaskedCardNumber);
        Assert.Equal("RFN-1", transaction.RefundReference);
    }

    [Fact]
    public async Task SettlementAsync_returns_unknown_without_recovery_or_resubmission()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            SettlementResult = new LinklyCloudSettlementResult("settlement-1", false, null, null, null, null)
            {
                Outcome = LinklyCloudSettlementOutcome.Unknown
            }
        };
        var client = new LinklyCloudTerminalClient(apiClient, new FakeLinklyCloudSecretStore(), TimeSpan.Zero);

        var result = await client.SettlementAsync(CreateSession(), CreateSettings());

        Assert.False(result.Succeeded);
        Assert.True(result.ResultUnknown);
        Assert.Equal(ProviderSubmissionState.Unknown, result.ProviderSubmissionState);
        Assert.Equal("settlement-1", result.SessionId);
        Assert.Equal(1, apiClient.SendSettlementCallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task SettlementAsync_returns_not_submitted_when_provider_rejects_start_with_4xx(
        HttpStatusCode statusCode)
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            SettlementException = new LinklyCloudApiException("settlement request rejected", statusCode)
        };
        var client = new LinklyCloudTerminalClient(apiClient, new FakeLinklyCloudSecretStore(), TimeSpan.Zero);

        var result = await client.SettlementAsync(CreateSession(), CreateSettings());

        Assert.False(result.Succeeded);
        Assert.False(result.ResultUnknown);
        Assert.Equal(ProviderSubmissionState.NotSubmitted, result.ProviderSubmissionState);
        Assert.Equal(1, apiClient.SendSettlementCallCount);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public async Task PurchaseAsync_closes_dialog_after_approved_direct_cloud_transaction()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            TransactionResult = Approved("session-1", "TXN-1")
        };
        var dialog = new FakeLinklyTerminalDialogService
        {
            ThrowIfFinalStateUsesCancelableToken = true
        };
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            localization: null,
            dialogService: dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(1, dialog.CloseCallCount);
        Assert.Contains(dialog.States, state => state.IsFinal);
    }

    [Fact]
    public async Task PurchaseAsync_treats_signature_approval_response_code_08_as_approved_direct_cloud_transaction()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            TransactionResult = SignatureApproved("session-signature", "TXN-SIGNATURE")
        };
        var dialog = new FakeLinklyTerminalDialogService();
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            localization: null,
            dialogService: dialog);

        var result = await client.PurchaseAsync(10.08m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal("08", result.ResponseCode);
        Assert.Equal("APPROVE WITH SIG", result.ResponseText);
        var transaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("08", transaction.ResponseCode);
        Assert.Equal("APPROVE WITH SIG", transaction.ResponseText);
        Assert.Equal(1, dialog.CloseCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_treats_json_success_true_as_approved_even_when_response_code_is_not_approved()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            TransactionResult = new LinklyCloudTransactionResult(
                "session-success",
                true,
                "TXN-SUCCESS",
                null,
                null,
                null,
                null,
                null,
                "50",
                "SYSTEM ERROR",
                null,
                10m,
                null)
        };
        var dialog = new FakeLinklyTerminalDialogService();
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            localization: null,
            dialogService: dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal("50", result.ResponseCode);
        Assert.Equal("SYSTEM ERROR", result.ResponseText);
    }

    [Fact]
    public async Task PurchaseAsync_keeps_dialog_open_after_declined_direct_cloud_transaction()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            TransactionResult = new LinklyCloudTransactionResult(
                "session-1",
                false,
                "TXN-1",
                null,
                null,
                null,
                null,
                null,
                "05",
                "DECLINED",
                null,
                10m,
                null)
        };
        var dialog = new FakeLinklyTerminalDialogService();
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            localization: null,
            dialogService: dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal(0, dialog.CloseCallCount);
        var finalState = Assert.Single(dialog.States.Where(state => state.IsFinal));
        Assert.Equal("DECLINED (05)", finalState.ResponseText);
    }

    [Fact]
    public async Task PurchaseAsync_keeps_retrying_status_poll_after_network_error_and_returns_approved_result()
    {
        // 单次断网不能让 POS 提前锁定为未知：顾客可能还在 PIN pad 上操作。
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionResultSequence.Enqueue(Pending("session-pending"));
        apiClient.TransactionStatusSequence.Enqueue(new HttpRequestException("status offline"));
        apiClient.TransactionStatusSequence.Enqueue(Pending("session-pending"));
        apiClient.TransactionStatusSequence.Enqueue(Approved("session-pending", "TXN-RETRY"));
        var timeProvider = new FakeTimeProvider();
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            timeProvider: timeProvider);

        var purchaseTask = client.PurchaseAsync(10m, CreateSession(), CreateSettings());
        await AdvanceUntilCompletedAsync(timeProvider, purchaseTask, TimeSpan.FromSeconds(1), maxSteps: 30);
        var result = await purchaseTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(1, apiClient.SendTransactionCallCount);
        Assert.Equal(3, apiClient.GetTransactionCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_waits_for_retry_after_before_polling_again_when_status_poll_is_rate_limited()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionResultSequence.Enqueue(Pending("session-pending"));
        apiClient.TransactionStatusSequence.Enqueue(new LinklyCloudApiException(
            "Linkly Cloud transaction status request failed with HTTP 429.",
            HttpStatusCode.TooManyRequests,
            detail: null,
            retryAfter: TimeSpan.FromSeconds(5)));
        apiClient.TransactionStatusSequence.Enqueue(Approved("session-pending", "TXN-429"));
        var timeProvider = new FakeTimeProvider();
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            timeProvider: timeProvider);

        var purchaseTask = client.PurchaseAsync(10m, CreateSession(), CreateSettings());
        await WaitUntilAsync(() => apiClient.GetTransactionCallCount == 1);
        var waited = TimeSpan.Zero;
        // 逐秒推进虚拟时间，直到第二次查询发出；遵循 Retry-After 时不可能在不足 5s 时重试。
        while (apiClient.GetTransactionCallCount < 2 && waited < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(20);
            if (apiClient.GetTransactionCallCount >= 2)
            {
                break;
            }

            timeProvider.Advance(TimeSpan.FromSeconds(1));
            waited += TimeSpan.FromSeconds(1);
            await Task.Delay(20);
        }

        var result = await purchaseTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.True(waited >= TimeSpan.FromSeconds(5), $"429 retried after only {waited}, Retry-After was ignored.");
        Assert.True(result.Approved);
        Assert.Equal(2, apiClient.GetTransactionCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_returns_result_unknown_only_after_business_wait_when_status_poll_keeps_failing()
    {
        var apiClient = new FakeLinklyCloudApiClient { StatusPollException = new HttpRequestException("status offline") };
        apiClient.TransactionResultSequence.Enqueue(Pending("session-pending"));
        var timeProvider = new FakeTimeProvider();
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            timeProvider: timeProvider);

        var purchaseTask = client.PurchaseAsync(10m, CreateSession(), CreateSettings());
        await WaitUntilAsync(() => apiClient.GetTransactionCallCount >= 1);
        // 业务等待窗口内只重试不放弃：先逐步推进到窗口前，仍未结束且已多次重试。
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(20);
            timeProvider.Advance(TimeSpan.FromSeconds(8));
        }

        await Task.Delay(50);
        Assert.False(purchaseTask.IsCompleted);
        Assert.True(apiClient.GetTransactionCallCount >= 5, "Expected the poll to keep retrying within the business wait window.");

        await AdvanceUntilCompletedAsync(timeProvider, purchaseTask, TimeSpan.FromSeconds(8), maxSteps: 40);
        var result = await purchaseTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.False(result.FallbackAllowed);
        Assert.Equal("linkly.cloud.resultUnknown", result.StatusKey);
        Assert.Equal(1, apiClient.SendTransactionCallCount);
        Assert.Equal(apiClient.LastTransactionSessionId, result.SessionId);
    }

    [Fact]
    public async Task PurchaseAsync_caller_cancel_during_status_poll_backoff_returns_unknown_without_further_polling()
    {
        var apiClient = new FakeLinklyCloudApiClient { StatusPollException = new HttpRequestException("status offline") };
        apiClient.TransactionResultSequence.Enqueue(Pending("session-pending"));
        var timeProvider = new FakeTimeProvider();
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            timeProvider: timeProvider);
        using var cancellation = new CancellationTokenSource();

        var purchaseTask = client.PurchaseAsync(10m, CreateSession(), CreateSettings(), cancellation.Token);
        await WaitUntilAsync(() => apiClient.GetTransactionCallCount == 1);
        cancellation.Cancel();
        var result = await purchaseTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(apiClient.LastTransactionSessionId, result.SessionId);
        Assert.Equal(1, apiClient.GetTransactionCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_does_not_retry_status_poll_on_bad_request()
    {
        // 官方：400 是我方请求格式错误，不得重试，也不代表交易未成功 -> 直接按结果未知交恢复。
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionResultSequence.Enqueue(Pending("session-pending"));
        apiClient.TransactionStatusSequence.Enqueue(new LinklyCloudApiException(
            "Linkly Cloud transaction status request failed with HTTP 400.",
            HttpStatusCode.BadRequest));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            timeProvider: new FakeTimeProvider());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings())
            .WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.True(result.ResultUnknown);
        Assert.Equal(1, apiClient.GetTransactionCallCount);
    }

    [Fact]
    public void ComputePollRetryDelay_backs_off_exponentially_with_cap_jitter_and_honors_retry_after()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), LinklyCloudTerminalClient.ComputePollRetryDelay(1, null, 0d));
        Assert.Equal(TimeSpan.FromSeconds(2), LinklyCloudTerminalClient.ComputePollRetryDelay(2, null, 0d));
        Assert.Equal(TimeSpan.FromSeconds(4), LinklyCloudTerminalClient.ComputePollRetryDelay(3, null, 0d));
        Assert.Equal(TimeSpan.FromSeconds(8), LinklyCloudTerminalClient.ComputePollRetryDelay(4, null, 0d));
        Assert.Equal(TimeSpan.FromSeconds(8), LinklyCloudTerminalClient.ComputePollRetryDelay(50, null, 0d));
        // 抖动只往后加（最多 25%）。
        Assert.Equal(TimeSpan.FromSeconds(1.25), LinklyCloudTerminalClient.ComputePollRetryDelay(1, null, 1d));
        // Retry-After 优先于指数退避，且不会被缩短；过小取 1s、过大取 15s。
        Assert.Equal(TimeSpan.FromSeconds(5), LinklyCloudTerminalClient.ComputePollRetryDelay(1, TimeSpan.FromSeconds(5), 0d));
        Assert.Equal(TimeSpan.FromSeconds(1), LinklyCloudTerminalClient.ComputePollRetryDelay(3, TimeSpan.Zero, 0d));
        Assert.Equal(TimeSpan.FromSeconds(15), LinklyCloudTerminalClient.ComputePollRetryDelay(1, TimeSpan.FromMinutes(5), 0d));
    }

    [Fact]
    public async Task PurchaseAsync_writes_rfn_amt_and_pcm_so_direct_sales_can_be_refunded()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        var client = new LinklyCloudTerminalClient(apiClient, new FakeLinklyCloudSecretStore());

        await client.PurchaseAsync(12.34m, CreateSession(), CreateSettings());

        var request = Assert.Single(apiClient.SentTransactionRequests);
        Assert.Equal("P", request.TxnType);
        var pad = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(request.PurchaseAnalysisData);
        // 与后端模式 EnsurePurchaseAnalysisData 一致：RFN=txnRef，AMT 9 位分，PCM=0000。
        Assert.Equal(request.TxnRef, pad["RFN"]);
        Assert.Equal("000001234", pad["AMT"]);
        Assert.Equal("0000", pad["PCM"]);
        Assert.False(pad.ContainsKey("OPR"));
    }

    [Theory]
    [InlineData("ANZCLOUD:TXN-ORIGINAL", "TXN-ORIGINAL")]
    [InlineData("ANZCLOUD:TXN-ORIGINAL:RFN-FROM-TERMINAL", "RFN-FROM-TERMINAL")]
    public async Task RefundAsync_uses_returned_rfn_or_falls_back_to_original_txn_ref(string originalReference, string expectedRfn)
    {
        var apiClient = new FakeLinklyCloudApiClient();
        var client = new LinklyCloudTerminalClient(apiClient, new FakeLinklyCloudSecretStore());

        await client.RefundAsync(5m, CreateSession(), CreateSettings(), originalReference);

        var request = Assert.Single(apiClient.SentTransactionRequests);
        Assert.Equal("R", request.TxnType);
        var pad = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(request.PurchaseAnalysisData);
        Assert.Equal(expectedRfn, pad["RFN"]);
        Assert.Equal("000000500", pad["AMT"]);
        Assert.Equal("0000", pad["PCM"]);
        Assert.Equal("C001|Cashier", pad["OPR"]);
    }

    [Theory]
    [InlineData("ANZCLOUD")]
    [InlineData("ANZCLOUD:")]
    [InlineData("ANZ:LOCAL-REF")]
    [InlineData("OTHER:TXN-1:RFN-1")]
    public async Task RefundAsync_still_rejects_references_that_are_not_cloud_references(string originalReference)
    {
        var apiClient = new FakeLinklyCloudApiClient();
        var client = new LinklyCloudTerminalClient(apiClient, new FakeLinklyCloudSecretStore());

        var result = await client.RefundAsync(5m, CreateSession(), CreateSettings(), originalReference);

        Assert.False(result.Approved);
        Assert.Equal("Linkly Cloud refund requires an original RFN reference.", result.Message);
        Assert.Equal(0, apiClient.SendTransactionCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_returns_unknown_with_original_identity_when_initial_result_identity_mismatches()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionIdentityEchoSequence.Enqueue(false);
        apiClient.TransactionResultSequence.Enqueue(Approved("foreign-session", "FOREIGN-TXN", amount: 11m));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(apiClient.LastTransactionSessionId, result.SessionId);
        Assert.Equal(apiClient.SentTransactionTxnRefs.Single(), result.TxnRef);
        Assert.Equal(0, apiClient.GetTransactionCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_returns_unknown_with_original_identity_when_polled_result_identity_mismatches()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionIdentityEchoSequence.Enqueue(true);
        apiClient.TransactionIdentityEchoSequence.Enqueue(false);
        apiClient.TransactionResultSequence.Enqueue(Pending("initial-session"));
        apiClient.TransactionStatusSequence.Enqueue(Approved("foreign-session", "FOREIGN-TXN", amount: 11m));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(apiClient.LastTransactionSessionId, result.SessionId);
        Assert.Equal(apiClient.SentTransactionTxnRefs.Single(), result.TxnRef);
        Assert.Equal(1, apiClient.GetTransactionCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_does_not_poll_when_initial_pending_session_mismatches_request()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionIdentityEchoSequence.Enqueue(false);
        apiClient.TransactionResultSequence.Enqueue(Pending("foreign-session"));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(apiClient.LastTransactionSessionId, result.SessionId);
        Assert.Equal(apiClient.SentTransactionTxnRefs.Single(), result.TxnRef);
        Assert.Equal(0, apiClient.GetTransactionCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_does_not_retry_when_not_submitted_session_mismatches_request()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionIdentityEchoSequence.Enqueue(false);
        apiClient.TransactionResultSequence.Enqueue(NotSubmitted("foreign-session"));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(apiClient.LastTransactionSessionId, result.SessionId);
        Assert.Equal(apiClient.SentTransactionTxnRefs.Single(), result.TxnRef);
        Assert.Equal(1, apiClient.SendTransactionCallCount);
        Assert.Equal(0, apiClient.GetTransactionCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_binds_direct_cloud_session_before_send_and_reuses_attempt_txn_ref_on_retry()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionResultSequence.Enqueue(Pending("session-first"));
        apiClient.TransactionStatusSequence.Enqueue(NotSubmitted("session-first"));
        apiClient.TransactionResultSequence.Enqueue(Approved("session-retry", "TXN-RETRY-RESULT"));
        var boundSessions = new List<(string SessionId, string? TxnRef, bool Cancellable)>();
        var attemptTxnRef = LinklyLocalTxnRef.Create('P', "attempt-cloud-direct-1");
        var accessor = new LinklyPaymentAttemptContextAccessor();
        using var scope = accessor.Begin(new LinklyPaymentAttemptContext(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            (sessionId, txnRef, _, cancellationToken) =>
            {
                boundSessions.Add((sessionId, txnRef, cancellationToken.CanBeCanceled));
                return Task.CompletedTask;
            },
            attemptTxnRef));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            linklyPaymentAttemptContextAccessor: accessor);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(2, apiClient.SendTransactionCallCount);
        Assert.Equal(2, boundSessions.Count);
        Assert.Equal(apiClient.SentTransactionSessionIds, boundSessions.Select(item => item.SessionId));
        Assert.All(boundSessions, item => Assert.Equal(attemptTxnRef, item.TxnRef));
        Assert.All(boundSessions, item => Assert.False(item.Cancellable));
        Assert.Equal([attemptTxnRef, attemptTxnRef], apiClient.SentTransactionTxnRefs);
    }

    [Fact]
    public async Task PurchaseAsync_returns_unknown_with_bound_identity_when_caller_cancels_after_direct_submission()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            PendingTransactionCompletion = new TaskCompletionSource<LinklyCloudTransactionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            ObservePendingTransactionCancellation = true
        };
        var boundSessions = new List<(string SessionId, string? TxnRef)>();
        var attemptTxnRef = LinklyLocalTxnRef.Create('P', "attempt-cloud-direct-cancel");
        var accessor = new LinklyPaymentAttemptContextAccessor();
        using var scope = accessor.Begin(new LinklyPaymentAttemptContext(
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            (sessionId, txnRef, _, _) =>
            {
                boundSessions.Add((sessionId, txnRef));
                return Task.CompletedTask;
            },
            attemptTxnRef));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            linklyPaymentAttemptContextAccessor: accessor);
        using var cancellation = new CancellationTokenSource();

        var purchaseTask = client.PurchaseAsync(10m, CreateSession(), CreateSettings(), cancellation.Token);
        await WaitUntilAsync(() => apiClient.SendTransactionCallCount == 1);
        cancellation.Cancel();
        var result = await purchaseTask;

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(apiClient.LastTransactionSessionId, result.SessionId);
        Assert.Equal(attemptTxnRef, result.TxnRef);
        var bound = Assert.Single(boundSessions);
        Assert.Equal(apiClient.LastTransactionSessionId, bound.SessionId);
        Assert.Equal(attemptTxnRef, bound.TxnRef);
        Assert.Equal(1, apiClient.SendTransactionCallCount);
    }

    [Fact]
    public async Task RecoverTransactionAsync_queries_only_original_session_and_maps_linkly_success()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        var txnRef = LinklyLocalTxnRef.Create('P', "attempt-cloud-direct-recovery");
        apiClient.TransactionStatusSequence.Enqueue(Approved("session-recover", txnRef));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.RecoverTransactionAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            "session-recover",
            txnRef);

        Assert.True(result.Approved);
        Assert.Equal("session-recover", result.SessionId);
        Assert.Equal(txnRef, result.TxnRef);
        Assert.Equal(0, apiClient.SendTransactionCallCount);
        Assert.Equal(1, apiClient.GetTransactionCallCount);
        Assert.Equal("session-recover", apiClient.LastGetTransactionSessionId);
    }

    [Fact]
    public async Task RecoverTransactionAsync_returns_unknown_for_identity_mismatch_without_resubmitting()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        var txnRef = LinklyLocalTxnRef.Create('P', "attempt-cloud-direct-mismatch");
        apiClient.TransactionStatusSequence.Enqueue(Approved("session-other", txnRef));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.RecoverTransactionAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            "session-expected",
            txnRef);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(0, apiClient.SendTransactionCallCount);
        Assert.Equal(1, apiClient.GetTransactionCallCount);
        Assert.Equal("session-expected", result.SessionId);
        Assert.Equal(txnRef, result.TxnRef);
    }

    [Fact]
    public async Task RecoverTransactionAsync_returns_unknown_for_pending_result_without_resubmitting()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        var txnRef = LinklyLocalTxnRef.Create('P', "attempt-cloud-direct-pending");
        apiClient.TransactionStatusSequence.Enqueue(Pending("session-pending-recovery"));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.RecoverTransactionAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            "session-pending-recovery",
            txnRef);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(0, apiClient.SendTransactionCallCount);
        Assert.Equal(1, apiClient.GetTransactionCallCount);
        Assert.Equal("session-pending-recovery", result.SessionId);
        Assert.Equal(txnRef, result.TxnRef);
    }

    [Fact]
    public async Task RecoverTransactionAsync_returns_unknown_for_invalid_json_without_resubmitting()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        var txnRef = LinklyLocalTxnRef.Create('P', "attempt-cloud-direct-invalid-json");
        apiClient.TransactionStatusSequence.Enqueue(new JsonException("invalid Linkly response"));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.RecoverTransactionAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            "session-invalid-json",
            txnRef);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(0, apiClient.SendTransactionCallCount);
        Assert.Equal(1, apiClient.GetTransactionCallCount);
        Assert.Equal("session-invalid-json", result.SessionId);
        Assert.Equal(txnRef, result.TxnRef);
    }

    [Fact]
    public async Task RecoverTransactionAsync_refreshes_token_after_authorization_failure_without_resubmitting()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        var txnRef = LinklyLocalTxnRef.Create('P', "attempt-cloud-direct-auth-refresh");
        apiClient.TransactionStatusSequence.Enqueue(new LinklyCloudApiException(
            "status token expired",
            HttpStatusCode.Unauthorized));
        apiClient.TransactionStatusSequence.Enqueue(Approved("session-auth-refresh", txnRef));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.RecoverTransactionAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            "session-auth-refresh",
            txnRef);

        Assert.True(result.Approved);
        Assert.Equal(2, apiClient.TokenCallCount);
        Assert.Equal(2, apiClient.GetTransactionCallCount);
        Assert.Equal(0, apiClient.SendTransactionCallCount);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    public async Task RecoverTransactionAsync_returns_unknown_for_transport_failures_without_resubmitting(string failure)
    {
        var apiClient = new FakeLinklyCloudApiClient();
        var txnRef = LinklyLocalTxnRef.Create('P', $"attempt-cloud-direct-{failure}");
        apiClient.TransactionStatusSequence.Enqueue(failure == "network"
            ? new HttpRequestException("status offline")
            : new OperationCanceledException("status timed out"));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.RecoverTransactionAsync(
            10m,
            CreateSession(),
            CreateSettings(),
            $"session-{failure}",
            txnRef);

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(0, apiClient.SendTransactionCallCount);
        Assert.Equal(1, apiClient.GetTransactionCallCount);
        Assert.Equal($"session-{failure}", result.SessionId);
        Assert.Equal(txnRef, result.TxnRef);
    }

    [Fact]
    public async Task RefundAsync_requires_original_rfn_reference()
    {
        var client = new LinklyCloudTerminalClient(
            new FakeLinklyCloudApiClient(),
            new FakeLinklyCloudSecretStore());

        var result = await client.RefundAsync(5m, CreateSession(), CreateSettings(), "ANZ:LOCAL-REF");

        Assert.False(result.Approved);
        Assert.Equal("Linkly Cloud refund requires an original RFN reference.", result.Message);
    }

    [Fact]
    public async Task PurchaseAsync_returns_clear_failure_when_token_request_is_unauthorized()
    {
        var client = new LinklyCloudTerminalClient(
            new FakeLinklyCloudApiClient
            {
                TokenException = new LinklyCloudApiException(
                    "Linkly Cloud token request failed with HTTP 401.",
                    HttpStatusCode.Unauthorized)
            },
            new FakeLinklyCloudSecretStore());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("Linkly Cloud pairing is invalid. Pair the terminal again.", result.Message);
    }

    [Fact]
    public async Task PurchaseAsync_rejects_mismatched_endpoint_before_token_request()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings() with
        {
            Environment = CardTerminalEnvironment.Production,
            LinklyCloudAuthBaseUrl = CardTerminalSettings.GetLinklyCloudAuthBaseUrl(CardTerminalEnvironment.Sandbox)
        });

        Assert.False(result.Approved);
        Assert.Equal(
            "Linkly Cloud Auth endpoint does not match the selected Production environment. Update the configured host and try again.",
            result.Message);
        Assert.Equal(0, apiClient.TokenCallCount);
    }

    [Fact]
    public async Task TestConnectionAsync_sends_logon_and_fails_when_logon_is_declined()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            LogonResult = new LinklyCloudLogonResult(
                false,
                "TF",
                "LOGON FAILED",
                "CAT-1",
                "CA-1",
                "1.0")
        };
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore());

        var result = await client.TestConnectionAsync(CreateSettings(), "S01", "TERM-1");

        Assert.False(result.Succeeded);
        Assert.Equal("LOGON FAILED (TF)", result.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_succeeds_when_logon_is_successful()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            LogonResult = new LinklyCloudLogonResult(
                true,
                "00",
                "APPROVED",
                "CAT-1",
                "CA-1",
                "1.0")
        };
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore());

        var result = await client.TestConnectionAsync(CreateSettings(), "S01", "TERM-1");

        Assert.True(result.Succeeded);
        Assert.Equal("APPROVED (00)", result.Message);
    }

    [Fact]
    public async Task PurchaseAsync_refreshes_token_when_recovery_status_is_unauthorized()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionResultSequence.Enqueue(Pending("session-1"));
        apiClient.TransactionStatusSequence.Enqueue(new LinklyCloudApiException(
            "Linkly Cloud transaction status request failed with HTTP 401.",
            HttpStatusCode.Unauthorized));
        apiClient.TransactionStatusSequence.Enqueue(Approved("session-1", "TXN-2"));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(2, apiClient.TokenCallCount);
        Assert.Equal(2, apiClient.GetTransactionCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_retries_once_when_recovery_status_reports_not_submitted()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionResultSequence.Enqueue(Pending("session-1"));
        apiClient.TransactionStatusSequence.Enqueue(new LinklyCloudTransactionResult(
            "session-1",
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null)
        {
            Outcome = LinklyCloudTransactionOutcome.NotSubmitted
        });
        apiClient.TransactionResultSequence.Enqueue(Approved("session-2", "TXN-3"));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(2, apiClient.SendTransactionCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_sends_direct_cancel_sendkey_when_dialog_requests_cancel()
    {
        var apiClient = new FakeLinklyCloudApiClient();
        apiClient.TransactionResultSequence.Enqueue(Pending("session-cancel-1"));
        apiClient.TransactionStatusSequence.Enqueue(new LinklyCloudTransactionResult(
            "session-cancel-1",
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            "CN",
            "CANCELLED",
            null,
            10m,
            null));
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.OkCancel, null));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            localization: null,
            dialogService: dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("CANCELLED (CN)", result.Message);
        Assert.Equal(1, dialog.CloseCallCount);
        Assert.Equal(1, apiClient.SendKeyCallCount);
        Assert.Equal(1, apiClient.GetTransactionCallCount);
        Assert.Equal(apiClient.LastTransactionSessionId, apiClient.LastSendKeySessionId);
        Assert.Equal(LinklyTerminalDialogKeys.OkCancel, apiClient.LastSendKeyKey);
        Assert.Contains(dialog.States, state =>
            state.SessionId == apiClient.LastTransactionSessionId &&
            state.IsInteractive &&
            !state.IsFinal);
    }

    [Fact]
    public async Task PurchaseAsync_does_not_show_direct_cancel_failed_message_when_cancel_sendkey_is_rejected_but_terminal_cancels()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            SendKeyException = new HttpRequestException("sendkey rejected"),
            PendingTransactionCompletion = new TaskCompletionSource<LinklyCloudTransactionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.OkCancel, null));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            localization: null,
            dialogService: dialog);

        var purchaseTask = client.PurchaseAsync(10m, CreateSession(), CreateSettings());
        await WaitUntilAsync(() => apiClient.SendKeyCallCount == 1 && dialog.States.Count >= 2);
        apiClient.PendingTransactionCompletion.SetResult(new LinklyCloudTransactionResult(
            apiClient.LastTransactionSessionId!,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            "CN",
            "CANCELLED",
            null,
            10m,
            null));
        var result = await purchaseTask;

        Assert.False(result.Approved);
        Assert.Equal("CANCELLED (CN)", result.Message);
        Assert.Equal(1, dialog.CloseCallCount);
        Assert.Equal(1, apiClient.SendKeyCallCount);
        Assert.Equal(0, apiClient.GetTransactionCallCount);
        Assert.DoesNotContain(dialog.States, state =>
            string.Equals(
                state.DisplayText,
                "Cancel request could not be sent. Try again or use the terminal.",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task PurchaseAsync_allows_direct_cancel_while_initial_transaction_request_is_pending()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            PendingTransactionCompletion = new TaskCompletionSource<LinklyCloudTransactionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.OkCancel, null));
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            localization: null,
            dialogService: dialog);

        var purchaseTask = client.PurchaseAsync(10m, CreateSession(), CreateSettings());
        await WaitUntilAsync(() => apiClient.SendKeyCallCount == 1);

        Assert.Equal(apiClient.LastTransactionSessionId, apiClient.LastSendKeySessionId);
        Assert.Equal(LinklyTerminalDialogKeys.OkCancel, apiClient.LastSendKeyKey);
        var pendingState = Assert.Single(dialog.States.Where(state => state.IsInteractive && !state.IsFinal));
        Assert.Equal(apiClient.LastTransactionSessionId, pendingState.SessionId);

        apiClient.PendingTransactionCompletion.SetResult(Approved(apiClient.LastTransactionSessionId!, "TXN-4"));
        var result = await purchaseTask;

        Assert.True(result.Approved);
    }

    [Fact]
    public async Task PurchaseAsync_yields_instead_of_hot_spinning_while_waiting_for_result_after_caller_cancel()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            // 不观察取消：取消后原 POST 仍在途，结果晚到。
            PendingTransactionCompletion = new TaskCompletionSource<LinklyCloudTransactionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var dialog = new PendingUpdateCountingDialogService();
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            TimeSpan.Zero,
            localization: null,
            dialogService: dialog);
        using var cancellation = new CancellationTokenSource();

        // 放到线程池上运行：旧实现取消后会同步空转，若占住测试同步上下文会把用例本身卡死，而不是明确失败。
        var purchaseTask = Task.Run(() => client.PurchaseAsync(10m, CreateSession(), CreateSettings(), cancellation.Token));
        try
        {
            await WaitUntilAsync(() => apiClient.SendTransactionCallCount == 1);
            cancellation.Cancel();
            var updatesAtCancel = dialog.PendingUpdateCount;
            var window = Stopwatch.StartNew();
            await Task.Delay(300);
            var updatesWhileWaiting = dialog.PendingUpdateCount - updatesAtCancel;
            var windowMilliseconds = window.Elapsed.TotalMilliseconds;

            // 取消后仍在等原交易结果时应按约 50ms 的节奏让出线程；按频率而不是绝对次数断言，CI 变慢也不会误报。
            // 同步空转每毫秒可轮询成千上万次，平均每 10ms 最多一次的上限足以区分两者。
            Assert.True(
                updatesWhileWaiting <= windowMilliseconds / 10 + 5,
                $"取消后等待循环在 {windowMilliseconds:F0}ms 内轮询了 {updatesWhileWaiting} 次，疑似同步空转");
        }
        finally
        {
            apiClient.PendingTransactionCompletion.TrySetResult(Approved(apiClient.LastTransactionSessionId!, "TXN-LATE"));
            await purchaseTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        }
    }

    [Fact]
    public async Task PurchaseAsync_does_not_use_short_configured_timeout_before_linkly_business_wait()
    {
        var apiClient = new FakeLinklyCloudApiClient
        {
            PendingTransactionCompletion = new TaskCompletionSource<LinklyCloudTransactionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            ObservePendingTransactionCancellation = true
        };
        var timeProvider = new FakeTimeProvider();
        var client = new LinklyCloudTerminalClient(
            apiClient,
            new FakeLinklyCloudSecretStore(),
            timeProvider: timeProvider);
        var settings = CreateSettings() with { TerminalTimeout = TimeSpan.FromMilliseconds(30) };

        using var cancellation = new CancellationTokenSource();
        var purchaseTask = client.PurchaseAsync(10m, CreateSession(), settings, cancellation.Token);
        try
        {
            // 先确认已提交再观察超时契约，避免仅因请求尚未开始就误判通过。
            await WaitUntilAsync(() => apiClient.SendTransactionCallCount == 1);

            // 中文注释：业务等待计时器在 FakeTimeProvider 上，Advance 会在本线程同步触发到期回调并级联取消链接令牌，
            // 因此推进后立即读取交易令牌即可确定结论，不需要墙钟等待。推进到业务等待前一刻，远超 30ms 的终端超时设置。
            timeProvider.Advance(LinklyTimeoutPolicy.BusinessWait - TimeSpan.FromMilliseconds(1));
            Assert.False(
                apiClient.LastTransactionCancellationToken.IsCancellationRequested,
                "Transaction wait was cancelled before the Linkly business wait.");
            Assert.False(purchaseTask.IsCompleted);

            apiClient.PendingTransactionCompletion.SetResult(Approved(apiClient.LastTransactionSessionId!, "TXN-5"));
            var result = await purchaseTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            Assert.True(result.Approved);
        }
        finally
        {
            // 断言失败也结束待响应交易，不能把后台任务带到后续测试。
            cancellation.Cancel();
            await purchaseTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        }
    }

    /// <summary>
    /// 逐步推进 FakeTimeProvider 直到任务结束。每步先让出线程，保证被测代码已登记好退避计时器再推进，
    /// 避免推进发生在计时器登记之前被漏掉。
    /// </summary>
    private static async Task AdvanceUntilCompletedAsync(
        FakeTimeProvider timeProvider,
        Task task,
        TimeSpan step,
        int maxSteps)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            await Task.Delay(20);
            if (task.IsCompleted)
            {
                break;
            }

            timeProvider.Advance(step);
        }

        await Task.Delay(20);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState(
            "HB POS",
            "S01",
            "Main",
            "TERM-1",
            "C001",
            "Cashier",
            true,
            0);
    }

    private static CardTerminalSettings CreateSettings()
    {
        return CardTerminalSettings.FromEnvironment() with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyConnectionMode = LinklyConnectionMode.Cloud,
            LinklyCloudSecret = "paired-secret",
            LinklyPosVendorId = "a256b7ec-709d-4c7d-8ffe-57cc7ca1fd22",
            TerminalTimeout = TimeSpan.FromSeconds(5)
        };
    }

    private static LinklyCloudTransactionResult Pending(string sessionId)
    {
        return new LinklyCloudTransactionResult(
            sessionId,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null)
        {
            Outcome = LinklyCloudTransactionOutcome.Pending
        };
    }

    private static LinklyCloudTransactionResult NotSubmitted(string sessionId)
    {
        return new LinklyCloudTransactionResult(
            sessionId,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null)
        {
            Outcome = LinklyCloudTransactionOutcome.NotSubmitted
        };
    }

    private static LinklyCloudTransactionResult Approved(string sessionId, string txnRef, decimal amount = 10m)
    {
        return new LinklyCloudTransactionResult(
            sessionId,
            true,
            txnRef,
            "123456",
            "VISA",
            "4",
            "4111111111111234",
            "MID",
            "00",
            "APPROVED",
            "42",
            amount,
            "RFN-1");
    }

    private static LinklyCloudTransactionResult SignatureApproved(string sessionId, string txnRef)
    {
        return new LinklyCloudTransactionResult(
            sessionId,
            true,
            txnRef,
            "123456",
            "VISA",
            "4",
            "4111111111111234",
            "MID",
            "08",
            "APPROVE WITH SIG",
            "42",
            10.08m,
            "RFN-SIG");
    }

    private sealed class FakeLinklyCloudSecretStore : ILinklyCloudSecretStore
    {
        public string? LastStoreCode { get; private set; }

        public string? LastDeviceCode { get; private set; }

        public CardTerminalEnvironment? LastEnvironment { get; private set; }

        public Task<string?> GetLinklyCloudSecretAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>("paired-secret");
        }

        public Task SaveLinklyCloudSecretAsync(
            CardTerminalEnvironment environment,
            string secret,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<LinklyCloudCredentialSettings> GetLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyCloudCredentialSettings(null, null, false));
        }

        public Task SaveLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<string> GetOrCreateLinklyCloudPosIdAsync(
            CardTerminalEnvironment environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken = default)
        {
            LastEnvironment = environment;
            LastStoreCode = storeCode;
            LastDeviceCode = deviceCode;
            return Task.FromResult("pos-id-1");
        }
    }

    private sealed class FakeLinklyCloudApiClient : ILinklyCloudApiClient
    {
        public string? LastPosId { get; private set; }

        public LinklyCloudApiException? TokenException { get; init; }

        public Exception? TransactionException { get; init; }

        public LinklyCloudApiException? SettlementException { get; init; }

        public int TokenCallCount { get; private set; }

        public int SendTransactionCallCount { get; private set; }

        public int SendSettlementCallCount { get; private set; }

        public string? LastTransactionSessionId { get; private set; }

        public CancellationToken LastTransactionCancellationToken { get; private set; }

        public string? LastGetTransactionSessionId { get; private set; }

        public List<string> SentTransactionSessionIds { get; } = [];

        public List<string> SentTransactionTxnRefs { get; } = [];

        public List<LinklyCloudTransactionRequest> SentTransactionRequests { get; } = [];

        public TaskCompletionSource<LinklyCloudTransactionResult>? PendingTransactionCompletion { get; init; }

        public bool ObservePendingTransactionCancellation { get; init; }

        public int GetTransactionCallCount { get; private set; }

        public int SendKeyCallCount { get; private set; }

        public Exception? SendKeyException { get; init; }

        public string? LastSendKeySessionId { get; private set; }

        public string? LastSendKeyKey { get; private set; }

        public Queue<LinklyCloudTransactionResult> TransactionResultSequence { get; } = [];

        public Queue<bool> TransactionIdentityEchoSequence { get; } = [];

        public Func<LinklyCloudTransactionResult, LinklyCloudTransactionResult>? TransactionResponseTransform { get; init; }

        public Queue<object> TransactionStatusSequence { get; } = [];

        // 状态队列耗尽后每次查询都抛出该异常（模拟持续断网）。
        public Exception? StatusPollException { get; init; }

        public LinklyCloudLogonResult LogonResult { get; init; } =
            new(true, "00", null, "CAT-1", "CA-1", "1.0");

        public LinklyCloudTransactionResult TransactionResult { get; init; } =
            new("session-1", false, null, null, null, null, null, null, "05", "DECLINED", null, null, null);

        public LinklyCloudSettlementResult SettlementResult { get; init; } =
            new("settlement-1", false, null, null, null, null);

        public Task<string> PairAsync(
            string authBaseUrl,
            string username,
            string password,
            string pairCode,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudToken> GetTokenAsync(
            CardTerminalSettings settings,
            string posId,
            CancellationToken cancellationToken = default)
        {
            TokenCallCount++;
            if (TokenException is not null)
            {
                throw TokenException;
            }

            LastPosId = posId;
            return Task.FromResult(new LinklyCloudToken("token", DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public Task<LinklyCloudStatusResult> SendStatusAsync(
            CardTerminalSettings settings,
            string token,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudLogonResult> SendLogonAsync(
            CardTerminalSettings settings,
            string token,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LogonResult);
        }

        public Task<LinklyCloudTransactionResult> SendTransactionAsync(
            CardTerminalSettings settings,
            string token,
            LinklyCloudTransactionRequest request,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            // 先记录令牌再递增计数：测试以计数作为"已提交"信号后读取令牌。
            LastTransactionCancellationToken = cancellationToken;
            SendTransactionCallCount++;
            LastTransactionSessionId = sessionId;
            SentTransactionSessionIds.Add(sessionId);
            SentTransactionTxnRefs.Add(request.TxnRef);
            SentTransactionRequests.Add(request);
            if (TransactionException is not null)
            {
                throw TransactionException;
            }
            if (PendingTransactionCompletion is not null)
            {
                var pending = ObservePendingTransactionCancellation
                    ? PendingTransactionCompletion.Task.WaitAsync(cancellationToken)
                    : PendingTransactionCompletion.Task;
                return EchoPendingResponseAsync(pending, sessionId, request.TxnRef);
            }

            var result = TransactionResultSequence.Count > 0
                ? TransactionResultSequence.Dequeue()
                : TransactionResult;
            return Task.FromResult(EchoTransactionIdentity(result, sessionId, request.TxnRef));
        }

        public Task<LinklyCloudTransactionResult> GetTransactionAsync(
            CardTerminalSettings settings,
            string token,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            GetTransactionCallCount++;
            LastGetTransactionSessionId = sessionId;
            if (TransactionStatusSequence.Count == 0)
            {
                if (StatusPollException is not null)
                {
                    throw StatusPollException;
                }

                throw new NotSupportedException();
            }

            var next = TransactionStatusSequence.Dequeue();
            if (next is LinklyCloudApiException exception)
            {
                throw exception;
            }

            if (next is Exception generalException)
            {
                throw generalException;
            }

            var result = (LinklyCloudTransactionResult)next;
            return Task.FromResult(LastTransactionSessionId is null
                ? result
                : EchoTransactionIdentity(result, sessionId, SentTransactionTxnRefs.Last()));
        }

        private async Task<LinklyCloudTransactionResult> EchoPendingResponseAsync(
            Task<LinklyCloudTransactionResult> pending, string sessionId, string txnRef) =>
            EchoTransactionIdentity(await pending, sessionId, txnRef);

        private LinklyCloudTransactionResult EchoTransactionIdentity(
            LinklyCloudTransactionResult result, string sessionId, string txnRef)
        {
            // 正常替身回显当前请求；错配回归显式保留外来身份，不能被测试适配掩盖。
            var echo = TransactionIdentityEchoSequence.Count == 0 || TransactionIdentityEchoSequence.Dequeue();
            var echoed = echo ? result with { SessionId = sessionId, TxnRef = result.TxnRef is null ? null : txnRef } : result;
            return TransactionResponseTransform?.Invoke(echoed) ?? echoed;
        }

        public Task<LinklyCloudSettlementResult> SendSettlementAsync(
            CardTerminalSettings settings,
            string token,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            SendSettlementCallCount++;
            if (SettlementException is not null)
            {
                throw SettlementException;
            }

            return Task.FromResult(SettlementResult);
        }

    public Task SendKeyAsync(
            CardTerminalSettings settings,
            string token,
            string sessionId,
            string key,
            string? data,
            CancellationToken cancellationToken = default)
        {
            SendKeyCallCount++;
            LastSendKeySessionId = sessionId;
            LastSendKeyKey = LinklyTerminalDialogKeys.Normalize(key);
            if (SendKeyException is not null)
            {
                throw SendKeyException;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 只统计非终态更新次数、不保存状态对象的对话框：用于度量等待循环的轮询频率。
    /// 故意不观察取消令牌，模拟不抛取消异常的对话框实现——等待循环必须自己让出线程。
    /// </summary>
    private sealed class PendingUpdateCountingDialogService : ILinklyTerminalDialogService
    {
        private int _pendingUpdateCount;

        public int PendingUpdateCount => Volatile.Read(ref _pendingUpdateCount);

        public CancellationToken LocalCancelToken => CancellationToken.None;

        public Task<LinklyTerminalDialogAction?> UpdateAsync(
            LinklyTerminalDialogState state,
            CancellationToken cancellationToken)
        {
            if (!state.IsFinal)
            {
                Interlocked.Increment(ref _pendingUpdateCount);
            }

            return Task.FromResult<LinklyTerminalDialogAction?>(null);
        }

        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeLinklyTerminalDialogService : ILinklyTerminalDialogService
    {
        public List<LinklyTerminalDialogState> States { get; } = [];

        public int CloseCallCount { get; private set; }

        public CancellationToken LocalCancelToken => CancellationToken.None;

        public bool ThrowIfFinalStateUsesCancelableToken { get; init; }

        private readonly Queue<LinklyTerminalDialogAction?> _actions = new();

        public void EnqueueAction(LinklyTerminalDialogAction? action)
        {
            _actions.Enqueue(action);
        }

        public Task<LinklyTerminalDialogAction?> UpdateAsync(
            LinklyTerminalDialogState state,
            CancellationToken cancellationToken)
        {
            if (ThrowIfFinalStateUsesCancelableToken && state.IsFinal && cancellationToken.CanBeCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            States.Add(state);
            return Task.FromResult(state.IsInteractive && _actions.Count > 0 ? _actions.Dequeue() : null);
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            CloseCallCount++;
            return Task.CompletedTask;
        }
    }
}
