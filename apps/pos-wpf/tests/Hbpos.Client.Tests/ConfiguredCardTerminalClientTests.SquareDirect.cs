using System.Net;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Square;

namespace Hbpos.Client.Tests;

// 直连 Square（默认关闭）与 ConfiguredCardTerminalClient 的接线：
// 付款流程里的 创建 / 轮询 / 取消 / 关闭 先问网关；网关返回 null 就照旧走 Hbpos API 中转（行为与未接入时完全一致）；
// 退款、恢复中心查询、付款详情永远不问网关。
public sealed partial class ConfiguredCardTerminalClientTests
{
    private static string DirectCheckoutJson(string checkoutId, string status, string? paymentId = null)
    {
        var paymentIds = paymentId is null ? string.Empty : $", \"payment_ids\": [ \"{paymentId}\" ]";
        return $$"""{ "checkout": { "id": "{{checkoutId}}", "status": "{{status}}", "amount_money": { "amount": 500, "currency": "AUD" }{{paymentIds}} } }""";
    }

    [Fact]
    public async Task Direct_gateway_serves_create_and_poll_so_the_relay_only_sees_the_payment_lookup()
    {
        var checkoutId = NewRetryCheckoutId();
        var gateway = new ScriptedDirectGateway((method, url, _) =>
        {
            if (method == HttpMethod.Post && url == "api/v1/square/checkouts")
            {
                return JsonResponse(DirectCheckoutJson(checkoutId, "PENDING"));
            }

            return url.StartsWith("api/v1/square/checkouts/", StringComparison.Ordinal)
                ? JsonResponse(DirectCheckoutJson(checkoutId, "COMPLETED", RetryPaymentId))
                : null;
        });
        var harness = new SquareRetryHarness(
            new SquareRetryScript(checkoutId)
            {
                PaymentSteps = [RetryPaymentStep(RetryPaymentId, "COMPLETED")]
            },
            directGateway: gateway);

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        // 创建与轮询都没有到过 Hbpos API；付款详情仍走服务端（金额 / 状态校验口径不变）。
        Assert.Equal(0, harness.Script.Creates);
        Assert.Equal(0, harness.Script.CheckoutGets);
        Assert.Equal(1, harness.Script.PaymentGets);
        var create = Assert.IsType<SquareCreateCheckoutRequest>(gateway.CreateBodies.Single());
        Assert.StartsWith("idem-retry-", create.IdempotencyKey, StringComparison.Ordinal);
        Assert.Equal("Production", create.Environment);
    }

    [Fact]
    public async Task Direct_gateway_declining_everything_keeps_relay_behaviour_with_the_same_idempotency_key()
    {
        var checkoutId = NewRetryCheckoutId();
        var gateway = new ScriptedDirectGateway((_, _, _) => null);
        var harness = new SquareRetryHarness(
            new SquareRetryScript(checkoutId)
            {
                CheckoutSteps = [RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
                PaymentSteps = [RetryPaymentStep(RetryPaymentId, "COMPLETED")]
            },
            directGateway: gateway);

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.Equal(1, harness.Script.Creates);
        Assert.Equal(1, harness.Script.CheckoutGets);
        // 直连失败回落中转：中转请求体里的幂等键必须就是直连那次用的键，Square 才会把它认作同一笔结账。
        var directCreate = Assert.IsType<SquareCreateCheckoutRequest>(gateway.CreateBodies.Single());
        Assert.Contains(directCreate.IdempotencyKey, harness.Script.CreateBodies.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_create_followed_by_failed_direct_poll_continues_through_the_relay()
    {
        var checkoutId = NewRetryCheckoutId();
        var gateway = new ScriptedDirectGateway((method, url, _) =>
            method == HttpMethod.Post && url == "api/v1/square/checkouts"
                ? JsonResponse(DirectCheckoutJson(checkoutId, "PENDING"))
                : null);
        var harness = new SquareRetryHarness(
            new SquareRetryScript(checkoutId)
            {
                CheckoutSteps = [RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)],
                PaymentSteps = [RetryPaymentStep(RetryPaymentId, "COMPLETED")]
            },
            directGateway: gateway);

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.Equal(0, harness.Script.Creates);
        Assert.Equal(1, harness.Script.CheckoutGets);
    }

    [Fact]
    public async Task Direct_gateway_is_asked_to_cancel_and_dismiss_when_the_cashier_cancels()
    {
        var checkoutId = NewRetryCheckoutId();
        var gateway = new ScriptedDirectGateway((method, url, _) =>
        {
            if (method == HttpMethod.Post && url == "api/v1/square/checkouts")
            {
                return JsonResponse(DirectCheckoutJson(checkoutId, "PENDING"));
            }

            if (url.EndsWith("/cancel", StringComparison.Ordinal))
            {
                return JsonResponse(DirectCheckoutJson(checkoutId, "CANCELED"));
            }

            return JsonResponse(DirectCheckoutJson(checkoutId, "IN_PROGRESS"));
        });
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId), directGateway: gateway);
        using var cts = new CancellationTokenSource();

        var authorize = harness.AuthorizeAsync(cts.Token);
        while (gateway.Calls.Count(call => call.Url.StartsWith("api/v1/square/checkouts/", StringComparison.Ordinal)
            && call.Method == HttpMethod.Get) == 0)
        {
            await Task.Delay(10);
            harness.Time.Advance(TimeSpan.FromSeconds(1));
        }

        await cts.CancelAsync();
        var result = await harness.AdvanceUntilCompletedAsync(authorize);

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        // 取消请求先问了网关（直连成功，中转没收到取消 / 关闭）。
        Assert.Contains(gateway.Calls, call => call.Url == $"api/v1/square/checkouts/{checkoutId}/cancel");
        Assert.Equal(0, harness.Script.Cancels);
        Assert.Equal(0, harness.Script.Dismisses);
    }

    [Fact]
    public async Task Refund_never_consults_the_direct_gateway()
    {
        var gateway = new ScriptedDirectGateway((_, _, _) => JsonResponse("{}"));
        var handler = new StubHttpMessageHandler((_, _) =>
            JsonResponse(CreateSquareRefundCreateJson("refund-1", "COMPLETED")));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler),
            squareDirectGateway: gateway);

        var result = await client.RefundAsync(12.34m, CreateSession(), "SQ:payment-1");

        Assert.True(result.Approved);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task Recovery_checkout_and_payment_queries_never_consult_the_direct_gateway()
    {
        var attempt = CreateSquareRecoveryAttempt("checkout-expected");
        var gateway = new ScriptedDirectGateway((_, _, _) => JsonResponse("{}"));
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.AbsolutePath.Contains("/payments/", StringComparison.Ordinal)
                ? JsonResponse("{ \"payment\": { \"id\": \"payment-expected\", \"status\": \"COMPLETED\", \"amount_money\": { \"amount\": 500, \"currency\": \"AUD\" } } }")
                : JsonResponse("{ \"checkout\": { \"id\": \"checkout-expected\", \"status\": \"COMPLETED\", \"amount_money\": { \"amount\": 500, \"currency\": \"AUD\" }, \"payment_ids\": [ \"payment-expected\" ] } }"));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler),
            squareDirectGateway: gateway);

        _ = await ((IInstallmentTerminalRecoveryClient)client).RecoverSquareAsync(attempt, CreateSession());

        // 恢复中心始终走服务端，哪怕本机开了直连。
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task WarmUpAsync_lets_the_direct_gateway_prepare_credentials_for_square_only()
    {
        var gateway = new ScriptedDirectGateway((_, _, _) => null);
        var handler = new StubHttpMessageHandler((_, _) => JsonResponse("""{ "success": true }"""));
        var square = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler),
            squareDirectGateway: gateway);
        var linkly = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateLinklySettings()),
            CreateApiClient(handler),
            squareDirectGateway: gateway);

        await linkly.WarmUpAsync();
        Assert.Empty(gateway.WarmUps);

        await square.WarmUpAsync();
        Assert.Equal([CardTerminalEnvironment.Production], gateway.WarmUps);
    }

    [Fact]
    public async Task WarmUpAsync_ignores_direct_gateway_failures()
    {
        var gateway = new ScriptedDirectGateway((_, _, _) => null) { WarmUpFailure = new HttpRequestException("down") };
        var handler = new StubHttpMessageHandler((_, _) => JsonResponse("""{ "success": true }"""));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler),
            squareDirectGateway: gateway);

        await client.WarmUpAsync();
    }

    private sealed class ScriptedDirectGateway(
        Func<HttpMethod, string, object?, HttpResponseMessage?> respond) : ISquareDirectGateway
    {
        private readonly List<(HttpMethod Method, string Url, object? Body)> _calls = [];
        private readonly List<CardTerminalEnvironment> _warmUps = [];

        public IReadOnlyList<(HttpMethod Method, string Url, object? Body)> Calls
        {
            get
            {
                lock (_calls)
                {
                    return _calls.ToArray();
                }
            }
        }

        public IReadOnlyList<CardTerminalEnvironment> WarmUps => _warmUps;

        public Exception? WarmUpFailure { get; init; }

        public IEnumerable<object?> CreateBodies => Calls
            .Where(call => call.Method == HttpMethod.Post && call.Url == "api/v1/square/checkouts")
            .Select(call => call.Body);

        public Task<HttpResponseMessage?> TrySendAsync(
            HttpMethod method,
            string relativeUrl,
            object? body,
            CancellationToken cancellationToken)
        {
            lock (_calls)
            {
                _calls.Add((method, relativeUrl, body));
            }

            return Task.FromResult(respond(method, relativeUrl, body));
        }

        public Task WarmUpAsync(CardTerminalEnvironment environment, CancellationToken cancellationToken)
        {
            _warmUps.Add(environment);
            return WarmUpFailure is null ? Task.CompletedTask : Task.FromException(WarmUpFailure);
        }

        public Task<SquareDirectRefreshOutcome> RefreshCredentialAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken) =>
            Task.FromResult(SquareDirectRefreshOutcome.Failed);
    }
}
