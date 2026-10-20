using System.Net;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

// 门店到新加坡 Hbpos API 往返约 0.3 秒：进付款页先建好连接、轮询缩短到 1 秒，让一笔 Square 刷卡少等几秒。
public sealed partial class ConfiguredCardTerminalClientTests
{
    [Fact]
    public async Task WarmUpAsync_sends_one_health_request_for_square()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(CloneRequest(request));
            return JsonResponse("""{ "success": true }""");
        });
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), CreateApiClient(handler));

        await client.WarmUpAsync();

        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        AssertHbposApiRequest(request, "api/v1/health");
        AssertNoSquareHeaders(request);
    }

    [Fact]
    public async Task WarmUpAsync_does_nothing_for_linkly()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(CloneRequest(request));
            return JsonResponse("""{ "success": true }""");
        });
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateLinklySettings()), CreateApiClient(handler));

        await client.WarmUpAsync();

        Assert.Empty(requests);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task WarmUpAsync_swallows_network_failures(Type exceptionType)
    {
        // 预热只是优化：网络差时必须静默失败，不能让进付款页报错或影响真正收款。
        var handler = new StubHttpMessageHandler(
            (Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)((_, _) =>
                throw (Exception)Activator.CreateInstance(exceptionType, "An error occurred while sending the request.")!));
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), CreateApiClient(handler));

        await client.WarmUpAsync();
    }

    [Fact]
    public async Task WarmUpAsync_swallows_server_errors()
    {
        var handler = new StubHttpMessageHandler((_, _) => JsonResponse(HttpStatusCode.BadGateway, """{ "success": false }"""));
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), CreateApiClient(handler));

        await client.WarmUpAsync();
    }

    [Fact]
    public async Task Checkout_status_is_polled_every_second_while_waiting_for_the_customer()
    {
        // 轮询间隔 1 秒：两次「进行中」后第三次读到完成，应在第 2 秒确认；原 2 秒间隔要到第 4 秒。
        var checkoutId = NewRetryCheckoutId();
        var harness = new SquareRetryHarness(new SquareRetryScript(checkoutId)
        {
            CheckoutSteps =
            [
                RetryCheckoutStep(checkoutId, "IN_PROGRESS"),
                RetryCheckoutStep(checkoutId, "IN_PROGRESS"),
                RetryCheckoutStep(checkoutId, "COMPLETED", RetryPaymentId)
            ],
            PaymentSteps = [RetryPaymentStep(RetryPaymentId, "COMPLETED")]
        });

        var result = await harness.RunToCompletionAsync();

        Assert.True(result.Approved);
        Assert.Equal(3, harness.Script.CheckoutGets);
        Assert.True(harness.Elapsed <= TimeSpan.FromSeconds(3), $"elapsed={harness.Elapsed}");
    }
}
