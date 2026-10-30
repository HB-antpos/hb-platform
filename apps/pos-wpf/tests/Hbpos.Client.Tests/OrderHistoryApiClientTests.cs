using System.Diagnostics;
using System.Net;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class OrderHistoryApiClientTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task QueryAsync_returns_stable_timeout_feedback_at_two_second_deadline()
    {
        using var httpClient = new HttpClient(new BlockingHandler())
        {
            BaseAddress = new Uri("https://hbpos.test/")
        };
        var client = new OrderHistoryApiClient(httpClient);
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.QueryAsync(new OrderHistoryQueryRequest("S001")));

        stopwatch.Stop();
        Assert.Equal(TimeSpan.FromSeconds(2), OrderHistoryApiClient.QueryTimeout);
        Assert.Equal(HttpStatusCode.RequestTimeout, exception.StatusCode);
        Assert.Equal("ORDER_HISTORY_QUERY_TIMEOUT", exception.ErrorCode);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task QueryAsync_preserves_caller_cancellation()
    {
        using var httpClient = new HttpClient(new BlockingHandler())
        {
            BaseAddress = new Uri("https://hbpos.test/")
        };
        var client = new OrderHistoryApiClient(httpClient);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.QueryAsync(new OrderHistoryQueryRequest("S001"), cancellation.Token));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(50, true)]
    public async Task QueryAsync_sends_skip_only_after_the_first_page(int skip, bool expectSkipParameter)
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://hbpos.test/")
        };
        var client = new OrderHistoryApiClient(httpClient);

        await client.QueryAsync(new OrderHistoryQueryRequest("S001", Take: 51, Skip: skip));

        var query = handler.LastRequestUri?.Query ?? string.Empty;
        Assert.Contains("take=51", query);
        Assert.Equal(expectSkipParameter, query.Contains($"skip={skip}", StringComparison.Ordinal));
        Assert.Equal(expectSkipParameter, query.Contains("skip=", StringComparison.Ordinal));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"success":true,"data":{"orders":[]}}""",
                    System.Text.Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
