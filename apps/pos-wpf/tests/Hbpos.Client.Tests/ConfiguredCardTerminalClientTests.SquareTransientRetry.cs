using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

// Square 刷卡等待期间，「查询结账状态」与「查询付款详情」两个幂等读取遇到瞬时错误时的有界重试。
// 时间走 FakeTimeProvider：退避等待与总预算（TerminalTimeout）都由测试推进虚拟时间，不靠墙钟硬等。
// 响应体沿用生产环境真实格式（Hbpos API 的 ApiResult 包装）。
public sealed partial class ConfiguredCardTerminalClientTests
{
    private const string RetryPaymentId = "payment-retry";

    // 首次查询 + 10 次重试；与 CardTerminalService 里的 MaxSquareTransientQueryRetries 保持一致。
    private const int RetryExpectedAttemptsWhenExhausted = 11;

    // ===== 结账状态轮询 =====

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Checkout_poll_retries_transient_http_status_then_succeeds(HttpStatusCode transientStatus)
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryStatusStep(transientStatus), RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
            PaymentSteps = [RetryPaymentStep(RetryPaymentId, "COMPLETED")]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(2, harness.Script.CheckoutGets);
        Assert.Equal(1, harness.Script.PaymentGets);
        Assert.Equal(0, harness.Script.Cancels);
        Assert.Equal(0, harness.Repository.MarkFailedCount);
    }

    [Fact]
    public async Task Checkout_poll_retries_network_error_then_succeeds()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps =
            [
                RetryThrowStep(() => new HttpRequestException("Connection reset by peer.")),
                RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)
            ],
            PaymentSteps = [RetryPaymentStep(RetryPaymentId, "COMPLETED")]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(2, harness.Script.CheckoutGets);
        Assert.Equal(0, harness.Repository.MarkFailedCount);
    }

    [Fact]
    public async Task Checkout_poll_retries_single_request_timeout_then_succeeds()
    {
        // HttpClient 自身超时或 API 端点切换窗口内被统一取消的请求：抛 TaskCanceledException，但调用方令牌与总预算都没取消。
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps =
            [
                RetryThrowStep(() => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException())),
                RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)
            ],
            PaymentSteps = [RetryPaymentStep(RetryPaymentId, "COMPLETED")]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(2, harness.Script.CheckoutGets);
        Assert.Equal(0, harness.Script.Cancels);
        Assert.Equal(0, harness.Repository.MarkFailedCount);
    }

    [Fact]
    public async Task Checkout_poll_retries_invalid_response_body_then_succeeds()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps =
            [
                RetryRawStep(HttpStatusCode.OK, "<html><body>502 Bad Gateway</body></html>"),
                RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)
            ],
            PaymentSteps = [RetryPaymentStep(RetryPaymentId, "COMPLETED")]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(2, harness.Script.CheckoutGets);
        Assert.Equal(0, harness.Repository.MarkFailedCount);
    }

    [Fact]
    public async Task Checkout_poll_resets_retry_count_after_a_successful_poll()
    {
        // 连续失败才累计：共 12 次 503，但中间有一次成功轮询，任何一段都没到 10 次，所以仍能等到终态。
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps =
            [
                .. Enumerable.Repeat(RetryStatusStep(HttpStatusCode.ServiceUnavailable), 6),
                RetryCheckoutStep(checkoutId, "IN_PROGRESS"),
                .. Enumerable.Repeat(RetryStatusStep(HttpStatusCode.ServiceUnavailable), 6),
                RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)
            ],
            PaymentSteps = [RetryPaymentStep(RetryPaymentId, "COMPLETED")]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.Equal(14, harness.Script.CheckoutGets);
        Assert.Equal(0, harness.Repository.MarkFailedCount);
    }

    [Fact]
    public async Task Checkout_poll_marks_unknown_after_http_retries_are_exhausted()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryStatusStep(HttpStatusCode.ServiceUnavailable)]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square checkout status failed with HTTP 503.", result.Message);
        Assert.Equal(RetryExpectedAttemptsWhenExhausted, harness.Script.CheckoutGets);
        Assert.Equal(0, harness.Script.PaymentGets);
        // 重试用尽走原有「结果未知」语义：不在这里取消终端结账，交给恢复流程。
        Assert.Equal(0, harness.Script.Cancels);
        Assert.Equal(1, harness.Repository.MarkFailedCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, harness.Repository.FailedStatus);
    }

    [Fact]
    public async Task Checkout_poll_marks_unknown_after_network_retries_are_exhausted()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryThrowStep(() => new HttpRequestException("No connection could be made."))]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square terminal communication failed.", result.Message);
        Assert.Equal(RetryExpectedAttemptsWhenExhausted, harness.Script.CheckoutGets);
        Assert.Equal(0, harness.Script.Cancels);
        Assert.Equal(1, harness.Repository.MarkFailedCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, harness.Repository.FailedStatus);
    }

    [Fact]
    public async Task Checkout_poll_gives_up_after_two_invalid_response_retries()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryRawStep(HttpStatusCode.OK, "not json at all")]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square terminal returned an invalid response.", result.Message);
        // 首次 + 2 次重试。
        Assert.Equal(3, harness.Script.CheckoutGets);
        Assert.Equal(0, harness.Script.Cancels);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, harness.Repository.FailedStatus);
    }

    // 保持原行为：明确的客户端错误不重试，立即判未知（改动前后都应通过）。
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Checkout_poll_does_not_retry_client_errors(HttpStatusCode clientError)
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryStatusStep(clientError)]
        });

        var result = await harness.RunWithoutAdvancingTimeAsync();

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal($"Square checkout status failed with HTTP {(int)clientError}.", result.Message);
        Assert.Equal(1, harness.Script.CheckoutGets);
        Assert.Equal(TimeSpan.Zero, harness.Elapsed);
        Assert.Equal(0, harness.Script.Cancels);
        Assert.Equal(1, harness.Repository.MarkFailedCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, harness.Repository.FailedStatus);
    }

    [Fact]
    public async Task Checkout_poll_retry_never_extends_the_total_wait_budget()
    {
        // 总预算 30 秒：网关一直 503，退避重试不能越过预算，到期照常走「超时后取消终端结账」。
        var checkoutId = NewRetryCheckoutId();
        var budget = TimeSpan.FromSeconds(30);
        var harness = new SquareRetryHarness(
            new SquareRetryScript(checkoutId)
            {
                CheckoutSteps = [RetryStatusStep(HttpStatusCode.BadGateway)]
            },
            budget);

        var result = await harness.RunToCompletionAsync();

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("payment.card.squareTerminalNotPickedUp", result.StatusKey);
        Assert.Equal(1, harness.Script.Cancels);
        Assert.Equal(LocalSquarePaymentAttemptStatus.TimedOut, harness.Repository.FailedStatus);
        // 预算到期时重试还没用尽（用尽需要 11 次查询、约 63 秒以上的退避）。
        Assert.True(harness.Script.CheckoutGets < RetryExpectedAttemptsWhenExhausted,
            $"checkout polls={harness.Script.CheckoutGets}");
        // 虚拟时间只比预算多出收尾推进的几步，绝不是重试用尽所需的 60+ 秒。
        Assert.InRange(harness.Elapsed, budget, budget + TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Checkout_poll_caller_cancel_during_retry_backoff_cancels_terminal_checkout()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryStatusStep(HttpStatusCode.ServiceUnavailable)]
        });
        using var cancellation = new CancellationTokenSource();

        var authorizeTask = harness.AuthorizeAsync(cancellation.Token);
        await WaitUntilAsync(() => harness.Script.CheckoutGets >= 1);
        // 不推进虚拟时间：能拿到结果说明取消打断了退避等待，而不是等退避结束。
        cancellation.Cancel();
        var result = await authorizeTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("payment.card.squareCanceledSeller", result.StatusKey);
        Assert.Equal(1, harness.Script.CheckoutGets);
        Assert.Equal(1, harness.Script.Cancels);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Canceled, harness.Repository.FailedStatus);
        Assert.Equal(TimeSpan.Zero, harness.Elapsed);
    }

    [Fact]
    public async Task Checkout_poll_logs_retry_only_when_failure_cause_changes()
    {
        var checkoutId = NewRetryCheckoutId();
        using var logs = new ConsoleLogCapture();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps =
            [
                RetryStatusStep(HttpStatusCode.ServiceUnavailable),
                RetryStatusStep(HttpStatusCode.ServiceUnavailable),
                RetryStatusStep(HttpStatusCode.ServiceUnavailable),
                RetryStatusStep(HttpStatusCode.BadGateway),
                RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)
            ],
            PaymentSteps = [RetryPaymentStep(RetryPaymentId, "COMPLETED")]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        var squareLines = logs.Lines
            .Where(line => line.Contains("[HBPOS][Client][Square]", StringComparison.Ordinal) &&
                line.Contains(checkoutId, StringComparison.Ordinal))
            .ToArray();
        // 4 次连续失败：503 → 503 → 503 → 502，原因只变了两次，只写两行重试日志；恢复时再写一行收尾。
        var retryLines = squareLines.Where(line => line.Contains("checkout status transient failure, retrying", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, retryLines.Length);
        Assert.Contains("cause=http=503", retryLines[0], StringComparison.Ordinal);
        Assert.Contains("cause=http=502", retryLines[1], StringComparison.Ordinal);
        Assert.Single(squareLines, line => line.Contains("checkout status recovered after 4 transient failure(s)", StringComparison.Ordinal));
    }

    // ===== 付款详情查询 =====

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Payment_lookup_retries_transient_http_status_then_verifies(HttpStatusCode transientStatus)
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
            PaymentSteps = [RetryStatusStep(transientStatus), RetryPaymentStep(RetryPaymentId, "COMPLETED")]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.Equal($"SQ:{RetryPaymentId}", result.Reference);
        Assert.Equal(1, harness.Script.CheckoutGets);
        Assert.Equal(2, harness.Script.PaymentGets);
        Assert.Equal(0, harness.Repository.MarkFailedCount);
    }

    [Fact]
    public async Task Payment_lookup_retries_network_error_then_verifies()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
            PaymentSteps =
            [
                RetryThrowStep(() => new HttpRequestException("Connection reset by peer.")),
                RetryPaymentStep(RetryPaymentId, "COMPLETED")
            ]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.Equal(2, harness.Script.PaymentGets);
        Assert.Equal(0, harness.Repository.MarkFailedCount);
    }

    [Fact]
    public async Task Payment_lookup_retries_single_request_timeout_then_verifies()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
            PaymentSteps =
            [
                RetryThrowStep(() => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException())),
                RetryPaymentStep(RetryPaymentId, "COMPLETED")
            ]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(2, harness.Script.PaymentGets);
        Assert.Equal(0, harness.Repository.MarkFailedCount);
    }

    [Fact]
    public async Task Payment_lookup_retries_invalid_response_body_then_verifies()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
            PaymentSteps =
            [
                RetryRawStep(HttpStatusCode.OK, "<html>Bad Gateway</html>"),
                RetryPaymentStep(RetryPaymentId, "COMPLETED")
            ]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.Equal(2, harness.Script.PaymentGets);
        Assert.Equal(0, harness.Repository.MarkFailedCount);
    }

    [Fact]
    public async Task Payment_lookup_marks_unknown_after_http_retries_are_exhausted()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
            PaymentSteps = [RetryStatusStep(HttpStatusCode.ServiceUnavailable)]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square payment failed with HTTP 503.", result.Message);
        Assert.Equal(RetryExpectedAttemptsWhenExhausted, harness.Script.PaymentGets);
        Assert.Equal(0, harness.Script.Cancels);
        Assert.Equal(1, harness.Repository.MarkFailedCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, harness.Repository.FailedStatus);
    }

    [Fact]
    public async Task Payment_lookup_marks_unknown_after_network_retries_are_exhausted()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
            PaymentSteps = [RetryThrowStep(() => new HttpRequestException("No connection could be made."))]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square terminal communication failed.", result.Message);
        Assert.Equal(RetryExpectedAttemptsWhenExhausted, harness.Script.PaymentGets);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, harness.Repository.FailedStatus);
    }

    [Fact]
    public async Task Payment_lookup_gives_up_after_two_invalid_response_retries()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
            PaymentSteps = [RetryRawStep(HttpStatusCode.OK, "not json at all")]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square terminal returned an invalid response.", result.Message);
        Assert.Equal(3, harness.Script.PaymentGets);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, harness.Repository.FailedStatus);
    }

    // 保持原行为：明确的客户端错误不重试，立即判未知（改动前后都应通过）。
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Payment_lookup_does_not_retry_client_errors(HttpStatusCode clientError)
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
            PaymentSteps = [RetryStatusStep(clientError)]
        });

        var result = await harness.RunWithoutAdvancingTimeAsync();

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal($"Square payment failed with HTTP {(int)clientError}.", result.Message);
        Assert.Equal(1, harness.Script.PaymentGets);
        Assert.Equal(TimeSpan.Zero, harness.Elapsed);
        Assert.Equal(1, harness.Repository.MarkFailedCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, harness.Repository.FailedStatus);
    }

    // ===== 测试替身 =====

    private static string NewRetryCheckoutId() => $"checkout-retry-{Guid.NewGuid():N}";

    private static Func<HttpResponseMessage> RetryStatusStep(HttpStatusCode statusCode) =>
        () => JsonResponse(statusCode, """{ "success": false, "data": null }""");

    private static Func<HttpResponseMessage> RetryRawStep(HttpStatusCode statusCode, string body) =>
        () => JsonResponse(statusCode, body);

    private static Func<HttpResponseMessage> RetryThrowStep(Func<Exception> createException) =>
        () => throw createException();

    private static Func<HttpResponseMessage> RetryCheckoutStep(string checkoutId, string status, string? paymentId = null)
    {
        var paymentIds = paymentId is null ? "[]" : $"[ \"{paymentId}\" ]";
        var json = $$"""
            { "success": true, "data": { "checkoutId": "{{checkoutId}}", "environment": "Production", "status": "{{status}}", "amountMoney": { "amount": 500, "currency": "AUD" }, "paymentIds": {{paymentIds}} } }
            """;
        return () => JsonResponse(json);
    }

    private static Func<HttpResponseMessage> RetryPaymentStep(string paymentId, string status)
    {
        var json = $$"""
            { "success": true, "data": { "paymentId": "{{paymentId}}", "status": "{{status}}", "approvedMoney": { "amount": 500, "currency": "AUD" }, "totalMoney": { "amount": 500, "currency": "AUD" }, "cardBrand": "VISA", "maskedCardNumber": "****1111", "authCode": "RETRY1" } }
            """;
        return () => JsonResponse(json);
    }

    /// <summary>
    /// 按请求类型编排的 Hbpos API 替身：创建结账默认固定成功（给了 CreateSteps 就按脚本返回）；
    /// 结账状态 / 付款详情各按脚本逐次返回，脚本用完后重复最后一步；
    /// 取消与 dismiss 固定返回 CANCELED（超时 / 取消收尾时能拿到明确的取消结论，不进入 15 秒确认窗口）。
    /// </summary>
    private sealed class SquareRetryScript(string checkoutId)
    {
        private readonly ConcurrentQueue<string> _createBodies = new();
        private int _creates;
        private int _checkoutGets;
        private int _paymentGets;
        private int _cancels;
        private int _dismisses;

        public IReadOnlyList<Func<HttpResponseMessage>> CreateSteps { get; init; } = [];

        /// <summary>取消请求返回的结账状态；默认 CANCELED（终态，无需再关闭）。CANCEL_REQUESTED 会让客户端继续发关闭请求。</summary>
        public string CancelResponseStatus { get; init; } = "CANCELED";

        /// <summary>取消 / 关闭请求在虚拟时钟上的耗时，模拟「门店到新加坡网络慢」；零表示立即返回。</summary>
        public TimeSpan CancelDelay { get; init; }

        public TimeSpan DismissDelay { get; init; }

        /// <summary>由 SquareRetryHarness 注入的虚拟时钟，延迟用它等，测试推进时间即可，不靠墙钟硬等。</summary>
        public TimeProvider? DelayClock { get; set; }

        public IReadOnlyList<Func<HttpResponseMessage>> CheckoutSteps { get; init; } = [];

        public IReadOnlyList<Func<HttpResponseMessage>> PaymentSteps { get; init; } = [];

        public int Creates => Volatile.Read(ref _creates);

        /// <summary>每次创建结账请求的原始请求体，按发送顺序。</summary>
        public IReadOnlyList<string> CreateBodies => _createBodies.ToArray();

        public int CheckoutGets => Volatile.Read(ref _checkoutGets);

        public int PaymentGets => Volatile.Read(ref _paymentGets);

        public int Cancels => Volatile.Read(ref _cancels);

        public int Dismisses => Volatile.Read(ref _dismisses);

        public string CreatedCheckoutJson() =>
            $$"""{ "checkout": { "id": "{{checkoutId}}", "status": "PENDING" } }""";

        public HttpMessageHandler CreateHandler() => new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                if (path.EndsWith("/cancel", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _cancels);
                    await DelayAsync(CancelDelay, cancellationToken);
                    return JsonResponse(CheckoutJson(CancelResponseStatus));
                }

                if (path.EndsWith("/dismiss", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _dismisses);
                    await DelayAsync(DismissDelay, cancellationToken);
                    return JsonResponse(CanceledCheckoutJson());
                }

                _createBodies.Enqueue(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty);
                if (CreateSteps.Count == 0)
                {
                    Interlocked.Increment(ref _creates);
                    return JsonResponse(CreatedCheckoutJson());
                }

                return NextStep(CreateSteps, ref _creates);
            }

            if (path.Contains("/api/v1/square/payments/", StringComparison.Ordinal))
            {
                return NextStep(PaymentSteps, ref _paymentGets);
            }

            return NextStep(CheckoutSteps, ref _checkoutGets);
        });

        private string CanceledCheckoutJson() => CheckoutJson("CANCELED");

        private string CheckoutJson(string status) =>
            $$"""{ "checkout": { "id": "{{checkoutId}}", "status": "{{status}}" } }""";

        private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            delay > TimeSpan.Zero && DelayClock is not null
                ? Task.Delay(delay, DelayClock, cancellationToken)
                : Task.CompletedTask;

        private static HttpResponseMessage NextStep(IReadOnlyList<Func<HttpResponseMessage>> steps, ref int counter)
        {
            var index = Interlocked.Increment(ref counter) - 1;
            return steps[Math.Min(index, steps.Count - 1)]();
        }
    }

    /// <summary>被测客户端 + 虚拟时钟 + 记录用仓储。默认总预算 10 分钟，远大于重试用尽所需时间。</summary>
    private sealed class SquareRetryHarness
    {
        private readonly DateTimeOffset _startedAt;

        public SquareRetryHarness(SquareRetryScript script, TimeSpan? terminalTimeout = null, TimeSpan? cancelConfirmWindow = null, ISquareDirectGateway? directGateway = null)
        {
            Script = script;
            Time = new FakeTimeProvider();
            script.DelayClock = Time;
            _startedAt = Time.GetUtcNow();
            Repository = new RecordingFailureSquarePaymentAttemptRepository();
            Context = new SquarePaymentAttemptContextAccessor();
            Client = new ConfiguredCardTerminalClient(
                new StaticCardTerminalSettingsProvider(CreateSquareSettings(terminalTimeout ?? TimeSpan.FromMinutes(10))),
                CreateApiClient(script.CreateHandler()),
                squarePaymentAttemptContextAccessor: Context,
                squarePaymentAttemptRepository: Repository,
                squareDirectGateway: directGateway)
            {
                TimeProvider = Time,
                // 取消确认窗口走真实时间：需要走到这一步的用例传入很短的窗口，避免真等 15 秒。
                SquareCancelConfirmWindow = cancelConfirmWindow ?? TimeSpan.FromSeconds(15)
            };
        }

        public SquareRetryScript Script { get; }

        public FakeTimeProvider Time { get; }

        public RecordingFailureSquarePaymentAttemptRepository Repository { get; }

        public SquarePaymentAttemptContextAccessor Context { get; }

        public ConfiguredCardTerminalClient Client { get; }

        /// <summary>被测流程已经消耗的虚拟时间。</summary>
        public TimeSpan Elapsed => Time.GetUtcNow() - _startedAt;

        public async Task<PaymentAuthorizationResult> AuthorizeAsync(CancellationToken cancellationToken = default)
        {
            // attempt 上下文在整个付款期间保持有效，失败时才会写入本地仓储替身。
            using var scope = Context.Begin(new SquarePaymentAttemptContext(Guid.NewGuid(), $"idem-retry-{Guid.NewGuid():N}"));
            return await Client.AuthorizeAsync(5m, CreateSession(), cancellationToken);
        }

        /// <summary>
        /// 启动付款并逐秒推进虚拟时间直到结束。每步先让出真实线程时间，保证被测代码已登记好退避 / 轮询计时器再推进；
        /// 以真实时间上限兜底，避免被测代码卡住时测试挂死。
        /// </summary>
        public Task<PaymentAuthorizationResult> RunToCompletionAsync() => AdvanceUntilCompletedAsync(AuthorizeAsync());

        /// <summary>已启动（可能已被调用方取消）的付款：逐秒推进虚拟时间直到结束，同样以真实时间上限兜底。</summary>
        public async Task<PaymentAuthorizationResult> AdvanceUntilCompletedAsync(Task<PaymentAuthorizationResult> authorizeTask)
        {
            var watch = Stopwatch.StartNew();
            while (!authorizeTask.IsCompleted && watch.Elapsed < AsyncTestWaitSupport.DefaultTimeout)
            {
                await Task.Delay(10);
                if (authorizeTask.IsCompleted)
                {
                    break;
                }

                Time.Advance(TimeSpan.FromSeconds(1));
            }

            return await authorizeTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        }

        /// <summary>
        /// 不推进虚拟时间直接等待结果：被测流程只要进入退避 / 轮询等待就会一直挂住直到超时失败，
        /// 用来证明某条路径（例如 4xx）根本没有等待、没有重试。
        /// </summary>
        public Task<PaymentAuthorizationResult> RunWithoutAdvancingTimeAsync() =>
            AuthorizeAsync().WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
    }
}
