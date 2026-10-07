using System.Net;
using System.Net.Http;
using System.Text;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

/// <summary>订单上传与代金券释放的联网失败日志：级别、TraceId、节流与券号脱敏。</summary>
[Collection(ConsoleLogGlobalStateTestCollection.Name)]
public sealed class OrderVoucherNetworkLoggingTests
{
    private static readonly PosSessionState Session = new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    [Fact]
    public async Task Voucher_release_failure_logs_warning_without_full_voucher_code_or_token()
    {
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            null!,
            null!,
            voucherTenderClient: new ReleaseThrowingVoucherTenderClient());
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        bool released;
        try
        {
            released = await workflow.ReleaseVoucherTenderAsync(
                new PaymentTender(PaymentMethodKind.Voucher, 5m, "VOUCHER:HBVC20267788:LOCK-SECRET-TOKEN:15.00"),
                Session);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        Assert.False(released);
        var entry = Assert.Single(sink.Entries, item => item.Category == "VoucherRelease");
        Assert.Equal("Warning", entry.Level);
        Assert.Contains("voucherTail=7788", entry.Message);
        Assert.DoesNotContain("HBVC20267788", entry.Message);
        Assert.DoesNotContain("LOCK-SECRET-TOKEN", entry.Message);
        Assert.DoesNotContain("token=", entry.Message);
        Assert.Equal(nameof(HttpRequestException), entry.ExceptionType);
    }

    [Fact]
    public async Task Order_sync_gateway_html_keeps_json_exception_but_logs_status_and_body()
    {
        var orderGuid = Guid.NewGuid();
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html><body>502 Bad Gateway</body></html>", Encoding.UTF8, "text/html")
        });
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            // 异常形状与改动前一致（JsonException），上传状态流转不受日志改动影响。
            await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => client.SyncAsync(CreateRequest(orderGuid)));
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        // 抛出前已经把状态码与截断正文写进中心日志。
        var entry = Assert.Single(sink.Entries, item =>
            item.Category == "OrderSync" && item.TraceId == orderGuid.ToString("D") && item.Level != "Information");
        Assert.Equal("Error", entry.Level);
        Assert.Equal(502, entry.StatusCode);
        Assert.Contains("reason=invalid-json", entry.Message);
        Assert.Contains("body=<html><body>502 Bad Gateway</body></html>", entry.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Order_sync_client_errors_are_warnings_not_errors(HttpStatusCode statusCode)
    {
        var orderGuid = Guid.NewGuid();
        var client = CreateClient(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                """{"success":false,"message":"rejected","errorCode":"ORDER_REJECTED"}""",
                Encoding.UTF8,
                "application/json")
        });
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            await Assert.ThrowsAsync<CatalogApiException>(() => client.SyncAsync(CreateRequest(orderGuid)));
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var entry = Assert.Single(sink.Entries, item =>
            item.Category == "OrderSync" && item.TraceId == orderGuid.ToString("D") && item.Level != "Information");
        Assert.Equal("Warning", entry.Level);
        Assert.Equal((int)statusCode, entry.StatusCode);
        Assert.Equal("ORDER_REJECTED", entry.Properties!["errorCode"]);
        Assert.Contains("message=rejected", entry.Message);
    }

    [Fact]
    public async Task Repeated_order_sync_failures_are_throttled_and_reset_after_success()
    {
        var orderGuid = Guid.NewGuid();
        var fail = true;
        var client = CreateClient(_ => fail
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(
                    """{"success":false,"message":"boom","errorCode":"ORDER_SYNC_FAILED"}""",
                    Encoding.UTF8,
                    "application/json")
            }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"success\":true,\"data\":{\"orderGuid\":\"" + orderGuid.ToString("D") +
                    "\",\"accepted\":true,\"alreadySynced\":false}}",
                    Encoding.UTF8,
                    "application/json")
            });
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            // Failed 订单每 15 秒重试一次且不退避：5 次连续失败只在第 1、2、4 次写 Error。
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Assert.ThrowsAsync<CatalogApiException>(() => client.SyncAsync(CreateRequest(orderGuid)));
            }

            Assert.Equal(3, CountErrors(sink, orderGuid));

            fail = false;
            await client.SyncAsync(CreateRequest(orderGuid));

            // 成功后计数清零，下一次失败重新从第 1 次开始告警。
            fail = true;
            await Assert.ThrowsAsync<CatalogApiException>(() => client.SyncAsync(CreateRequest(orderGuid)));
            Assert.Equal(4, CountErrors(sink, orderGuid));
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        // 被节流的那几次仍保留 Information 级别的逐次记录，便于本机排查。
        Assert.Equal(
            6,
            sink.Entries.Count(item =>
                item.Category == "OrderSync" &&
                item.Level == "Information" &&
                item.Message.StartsWith($"http sync failed orderGuid={orderGuid:D}", StringComparison.Ordinal)));
    }

    private static int CountErrors(RecordingApplicationLogSink sink, Guid orderGuid) =>
        sink.Entries.Count(item =>
            item.Category == "OrderSync" &&
            item.Level == "Error" &&
            item.TraceId == orderGuid.ToString("D"));

    private static OrderSyncApiClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://pos-api.example.com/")
        });

    private static OrderSyncRequest CreateRequest(Guid orderGuid) =>
        new(
            orderGuid,
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.Parse("2026-10-08T00:00:00Z"),
            10m,
            0m,
            10m,
            [],
            []);

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class ReleaseThrowingVoucherTenderClient : IVoucherTenderClient
    {
        public Task<PaymentAuthorizationResult> RedeemAsync(
            decimal amount,
            PosSessionState session,
            string? voucherCode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> IssueRefundAsync(
            decimal amount,
            PosSessionState session,
            string orderReference,
            string idempotencyKey,
            string? reason = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ReleaseAsync(
            PosSessionState session,
            string voucherCode,
            string reservationToken,
            CancellationToken cancellationToken = default) =>
            Task.FromException<bool>(new HttpRequestException("connection refused"));
    }
}
