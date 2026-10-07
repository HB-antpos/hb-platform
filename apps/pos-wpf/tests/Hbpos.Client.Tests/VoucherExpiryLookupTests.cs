using System.Net;
using System.Text;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class VoucherExpiryLookupTests
{
    private const string FoundJson =
        """
        {
          "success": true,
          "data": {
            "found": true,
            "voucher": {
              "voucherCode": "RF123",
              "storeCode": "S001",
              "voucherType": 3,
              "amount": 8.00,
              "remainingAmount": 8.00,
              "status": "1",
              "expiredAt": "2027-01-05T12:59:59Z",
              "customerCode": null,
              "discountRate": 0,
              "remark": null
            }
          }
        }
        """;

    [Fact]
    public async Task Returns_the_voucher_own_expiry_from_the_server()
    {
        HttpRequestMessage? captured = null;
        var lookup = CreateLookup((request, _) =>
        {
            captured = request;
            return Task.FromResult(Json(FoundJson));
        });

        var expiry = await lookup.FindExpiryAsync(" S001 ", " RF123 ", CancellationToken.None);

        // 取服务端那张券自己的到期时刻（旧券/新券规则不同，不能反推）。
        Assert.Equal(DateTimeOffset.Parse("2027-01-05T12:59:59Z"), expiry);
        Assert.Contains("api/v1/vouchers/RF123", captured!.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("storeCode=S001", captured.RequestUri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Returns_null_when_server_does_not_find_the_voucher()
    {
        var lookup = CreateLookup((_, _) => Task.FromResult(Json(
            """{ "success": true, "data": { "found": false, "voucher": null, "message": "not found" } }""")));

        Assert.Null(await lookup.FindExpiryAsync("S001", "RF999", CancellationToken.None));
    }

    [Fact]
    public async Task Returns_null_instead_of_throwing_when_the_request_fails()
    {
        var lookup = CreateLookup((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom", Encoding.UTF8, "text/plain")
        }));

        Assert.Null(await lookup.FindExpiryAsync("S001", "RF123", CancellationToken.None));
    }

    [Fact]
    public async Task Returns_null_instead_of_throwing_when_the_network_is_down()
    {
        var lookup = CreateLookup((_, _) => throw new HttpRequestException("offline"));

        Assert.Null(await lookup.FindExpiryAsync("S001", "RF123", CancellationToken.None));
    }

    [Fact]
    public async Task Gives_up_after_its_own_timeout_without_blocking_the_print()
    {
        var lookup = CreateLookup(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json(FoundJson);
        }, timeout: TimeSpan.FromMilliseconds(50));

        var result = await lookup.FindExpiryAsync("S001", "RF123", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(result);
    }

    [Fact]
    public async Task Propagates_cancellation_requested_by_the_caller()
    {
        var lookup = CreateLookup((_, _) => Task.FromResult(Json(FoundJson)));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lookup.FindExpiryAsync("S001", "RF123", cancelled.Token));
    }

    [Theory]
    [InlineData("", "RF123")]
    [InlineData("S001", " ")]
    public async Task Skips_the_request_for_blank_store_or_voucher_code(string storeCode, string voucherCode)
    {
        var requestCount = 0;
        var lookup = CreateLookup((_, _) =>
        {
            requestCount++;
            return Task.FromResult(Json(FoundJson));
        });

        Assert.Null(await lookup.FindExpiryAsync(storeCode, voucherCode, CancellationToken.None));
        Assert.Equal(0, requestCount);
    }

    private static VoucherExpiryLookup CreateLookup(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        TimeSpan? timeout = null)
    {
        var client = new VoucherApiClient(new HttpClient(new StubHandler(responder))
        {
            BaseAddress = new Uri("http://localhost/")
        });
        return timeout is { } value
            ? new VoucherExpiryLookup(client) { Timeout = value }
            : new VoucherExpiryLookup(client);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }
}
