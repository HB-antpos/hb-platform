using System.Net;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

// Square 创建结账（POST）遇到断网 / 单次超时 / 408、429、5xx 时，用同一幂等键、同一请求体有界重发，
// 并经 CardTerminalProgressScope 把「网络不稳，正在重试 n/3」报告给付款页。
// 复用 SquareTransientRetry 的虚拟时钟替身：退避等待由测试推进，不靠墙钟硬等。
public sealed partial class ConfiguredCardTerminalClientTests
{
    // 首发 + 2 次重试；与 CardTerminalService 里的 MaxSquareCreateCheckoutAttempts 保持一致。
    private const int CreateExpectedAttemptsWhenExhausted = 3;

    [Fact]
    public async Task Create_checkout_retries_network_error_with_same_request_then_completes()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CreateSteps =
            [
                RetryThrowStep(() => new HttpRequestException("An error occurred while sending the request.")),
                RetryCreatedStep(checkoutId)
            ],
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
            PaymentSteps = [RetryPaymentStep(RetryPaymentId, "COMPLETED")]
        });
        var progress = new RecordingCardTerminalProgress();

        PaymentAuthorizationResult result;
        using (CardTerminalProgressScope.Begin(progress))
        {
            result = await harness.RunToCompletionAsync();
        }

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(2, harness.Script.Creates);
        // 重发必须是同一个幂等键、同一份请求体（含 referenceId），否则 Square 可能建出第二笔结账。
        var bodies = harness.Script.CreateBodies;
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);
        Assert.Contains("idem-retry-", bodies[0], StringComparison.Ordinal);
        Assert.Equal(0, harness.Repository.MarkFailedCount);
        Assert.Equal(
            new[]
            {
                new CardTerminalProgress(CardTerminalProgressKind.NetworkRetrying, 2, 3),
                new CardTerminalProgress(CardTerminalProgressKind.NetworkRecovered)
            },
            progress.Reports);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Create_checkout_retries_transient_http_status_then_completes(HttpStatusCode transientStatus)
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CreateSteps = [RetryStatusStep(transientStatus), RetryCreatedStep(checkoutId)],
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
            PaymentSteps = [RetryPaymentStep(RetryPaymentId, "COMPLETED")]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.Equal(2, harness.Script.Creates);
        Assert.Equal(harness.Script.CreateBodies[0], harness.Script.CreateBodies[1]);
    }

    [Fact]
    public async Task Create_checkout_retries_single_request_timeout_then_completes()
    {
        // HttpClient 自身超时：抛 TaskCanceledException，但调用方令牌与总预算都没取消。
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CreateSteps =
            [
                RetryThrowStep(() => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException())),
                RetryCreatedStep(checkoutId)
            ],
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
            PaymentSteps = [RetryPaymentStep(RetryPaymentId, "COMPLETED")]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.Equal(2, harness.Script.Creates);
    }

    [Fact]
    public async Task Create_checkout_marks_unknown_after_network_retries_are_exhausted()
    {
        var checkoutId = NewRetryCheckoutId();
        using var logs = new ConsoleLogCapture();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CreateSteps = [RetryThrowStep(() => new HttpRequestException("An error occurred while sending the request."))]
        });
        var progress = new RecordingCardTerminalProgress();

        PaymentAuthorizationResult result;
        using (CardTerminalProgressScope.Begin(progress))
        {
            result = await harness.RunToCompletionAsync();
        }

        // 重试用尽仍按原语义：请求可能已到达 Square，判结果未知交给恢复中心，提示不变。
        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square terminal communication failed.", result.Message);
        Assert.Equal(CreateExpectedAttemptsWhenExhausted, harness.Script.Creates);
        Assert.Equal(0, harness.Script.CheckoutGets);
        Assert.Equal(1, harness.Repository.MarkFailedCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, harness.Repository.FailedStatus);
        Assert.Equal(
            new[]
            {
                new CardTerminalProgress(CardTerminalProgressKind.NetworkRetrying, 2, 3),
                new CardTerminalProgress(CardTerminalProgressKind.NetworkRetrying, 3, 3)
            },
            progress.Reports);
        AssertContainsLogLine(logs.Lines, "checkout create transient failure, retrying attempt=2/3 cause=network-error");
        AssertContainsLogLine(logs.Lines, "checkout create transient retries exhausted attempts=3 cause=network-error");
    }

    [Fact]
    public async Task Create_checkout_marks_unknown_after_http_retries_are_exhausted()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CreateSteps = [RetryStatusStep(HttpStatusCode.ServiceUnavailable)]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square checkout failed with HTTP 503.", result.Message);
        Assert.Equal(CreateExpectedAttemptsWhenExhausted, harness.Script.Creates);
        Assert.Equal(0, harness.Script.CheckoutGets);
    }

    // 明确的客户端错误重发也不会变好：只发一次、不等待（改动前后都应通过）。
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Create_checkout_does_not_retry_client_errors(HttpStatusCode clientError)
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CreateSteps = [RetryStatusStep(clientError)]
        });
        var progress = new RecordingCardTerminalProgress();

        PaymentAuthorizationResult result;
        using (CardTerminalProgressScope.Begin(progress))
        {
            result = await harness.RunWithoutAdvancingTimeAsync();
        }

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal($"Square checkout failed with HTTP {(int)clientError}.", result.Message);
        Assert.Equal(1, harness.Script.Creates);
        Assert.Equal(TimeSpan.Zero, harness.Elapsed);
        Assert.Empty(progress.Reports);
    }

    [Fact]
    public async Task Create_checkout_caller_cancel_during_retry_backoff_stops_resending()
    {
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CreateSteps = [RetryThrowStep(() => new HttpRequestException("An error occurred while sending the request."))]
        });
        using var cancellation = new CancellationTokenSource();

        var authorizeTask = harness.AuthorizeAsync(cancellation.Token);
        await WaitUntilAsync(() => harness.Script.Creates >= 1);
        // 不推进虚拟时间：能拿到结果说明取消打断了退避等待，没有再发第二次创建。
        cancellation.Cancel();
        var result = await authorizeTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.False(result.Approved);
        // 首发可能已到达 Square 又没拿到 checkoutId：保持原有「已提交、结果未知」收尾。
        Assert.True(result.ResultUnknown);
        Assert.Equal(1, harness.Script.Creates);
        Assert.Equal(TimeSpan.Zero, harness.Elapsed);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, harness.Repository.FailedStatus);
    }

    [Fact]
    public async Task Create_checkout_retry_never_extends_the_total_wait_budget()
    {
        // 总预算 2 秒：首发失败等 1 秒、第二次失败再等约 2 秒时预算到期，不能发出第三次。
        var checkoutId = NewRetryCheckoutId();
        var budget = TimeSpan.FromSeconds(2);
        var harness = new SquareRetryHarness(
            new SquareRetryScript(checkoutId)
            {
                CreateSteps = [RetryThrowStep(() => new HttpRequestException("An error occurred while sending the request."))]
            },
            budget);

        var result = await harness.RunToCompletionAsync();

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(2, harness.Script.Creates);
        Assert.InRange(harness.Elapsed, budget, budget + TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Create_checkout_without_progress_scope_still_retries()
    {
        // 分期、恢复中心等没有付款页订阅的入口：报告是空操作，不影响重试本身。
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CreateSteps =
            [
                RetryThrowStep(() => new HttpRequestException("An error occurred while sending the request.")),
                RetryCreatedStep(checkoutId)
            ],
            CheckoutSteps = [RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
            PaymentSteps = [RetryPaymentStep(RetryPaymentId, "COMPLETED")]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.Equal(2, harness.Script.Creates);
    }

    private static Func<HttpResponseMessage> RetryCreatedStep(string checkoutId) =>
        () => JsonResponse($$"""{ "checkout": { "id": "{{checkoutId}}", "status": "PENDING" } }""");

    /// <summary>同步记录报告的进度替身（不用 Progress&lt;T&gt;，避免异步投递导致断言时序不稳）。</summary>
    private sealed class RecordingCardTerminalProgress : IProgress<CardTerminalProgress>
    {
        private readonly object _sync = new();
        private readonly List<CardTerminalProgress> _reports = [];

        public IReadOnlyList<CardTerminalProgress> Reports
        {
            get
            {
                lock (_sync)
                {
                    return _reports.ToArray();
                }
            }
        }

        public void Report(CardTerminalProgress value)
        {
            lock (_sync)
            {
                _reports.Add(value);
            }
        }
    }
}
