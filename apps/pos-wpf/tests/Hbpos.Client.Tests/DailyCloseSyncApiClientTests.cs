using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.DailyClose;

namespace Hbpos.Client.Tests;

/// <summary>API 客户端：假 HttpMessageHandler 覆盖请求形状与状态码/错误体解析，再端到端验证状态码分类。</summary>
public sealed class DailyCloseSyncApiClientTests
{
    private static readonly Uri BaseAddress = new("https://api.example/pos-api/");

    [Fact]
    public async Task SyncAsync_posts_the_request_as_camel_case_json_to_the_daily_close_endpoint()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"accepted":true,"alreadySynced":false,"replacedPlaceholder":true}""");
        });
        var client = new DailyCloseSyncApiClient(new HttpClient(handler) { BaseAddress = BaseAddress });
        var request = SampleRequest();

        var response = await client.SyncAsync(request);

        Assert.Equal(new DailyCloseSyncResponse(true, false, true), response);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        // 相对路径拼在带前缀的基地址上：不能丢掉 /pos-api/ 前缀。
        Assert.Equal("https://api.example/pos-api/api/v1/daily-closes/sync", captured.RequestUri!.ToString());
        using var document = JsonDocument.Parse(body!);
        Assert.Equal(request.DailyCloseGuid, document.RootElement.GetProperty("dailyCloseGuid").GetGuid());
        Assert.Equal("Wpf", document.RootElement.GetProperty("clientKind").GetString());
        Assert.Equal(11, document.RootElement.GetProperty("cashCounts").GetArrayLength());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_CASH_COUNTS")]
    [InlineData(HttpStatusCode.Unauthorized, "DEVICE_AUTH_REQUIRED")]
    [InlineData(HttpStatusCode.Forbidden, "DEVICE_SCOPE_FORBIDDEN")]
    [InlineData(HttpStatusCode.Conflict, "DAILY_CLOSE_CONTENT_CONFLICT")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "PAYLOAD_TOO_LARGE")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "UNPROCESSABLE")]
    [InlineData(HttpStatusCode.InternalServerError, "SERVER_ERROR")]
    public async Task SyncAsync_throws_with_status_and_the_code_message_error_body(HttpStatusCode statusCode, string code)
    {
        var handler = new StubHandler(_ => Task.FromResult(
            Json(statusCode, $$"""{"code":"{{code}}","message":"details for {{code}}"}""")));
        var client = new DailyCloseSyncApiClient(new HttpClient(handler) { BaseAddress = BaseAddress });

        var exception = await Assert.ThrowsAsync<DailyCloseUploadApiException>(() => client.SyncAsync(SampleRequest()));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal(code, exception.ErrorCode);
        Assert.Equal($"details for {code}", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Bad Gateway</html>")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("""{"code":42,"message":["x"]}""")]
    public async Task SyncAsync_falls_back_to_a_generic_message_when_the_error_body_is_not_code_message_json(string body)
    {
        var handler = new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(body, Encoding.UTF8, "text/html") }));
        var client = new DailyCloseSyncApiClient(new HttpClient(handler) { BaseAddress = BaseAddress });

        var exception = await Assert.ThrowsAsync<DailyCloseUploadApiException>(() => client.SyncAsync(SampleRequest()));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Null(exception.ErrorCode);
        Assert.Equal("Daily close sync failed with HTTP 502.", exception.Message);
    }

    [Fact]
    public async Task SyncAsync_reports_an_empty_success_body_as_an_error()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "null")));
        var client = new DailyCloseSyncApiClient(new HttpClient(handler) { BaseAddress = BaseAddress });

        var exception = await Assert.ThrowsAsync<DailyCloseUploadApiException>(() => client.SyncAsync(SampleRequest()));

        Assert.Equal("EMPTY_SYNC_RESPONSE", exception.ErrorCode);
    }

    [Fact]
    public async Task SyncAsync_lets_transport_failures_propagate_for_the_upload_service_to_classify()
    {
        var failing = new DailyCloseSyncApiClient(new HttpClient(new StubHandler(_ =>
            throw new HttpRequestException("connection refused"))) { BaseAddress = BaseAddress });
        var timingOut = new DailyCloseSyncApiClient(new HttpClient(new StubHandler(_ =>
            throw new TaskCanceledException("timeout"))) { BaseAddress = BaseAddress });

        await Assert.ThrowsAsync<HttpRequestException>(() => failing.SyncAsync(SampleRequest()));
        await Assert.ThrowsAsync<TaskCanceledException>(() => timingOut.SyncAsync(SampleRequest()));
    }

    /// <summary>
    /// 端到端：真实 API 客户端 + 真实上传服务 + 真实 SQLite，验证每个状态码最终落成什么本地状态。
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.OK, null, "Synced", 0)]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_CASH_COUNTS", "Rejected", 1)]
    [InlineData(HttpStatusCode.Conflict, "DAILY_CLOSE_SCOPE_CONFLICT", "Rejected", 1)]
    [InlineData(HttpStatusCode.Conflict, "DAILY_CLOSE_CONTENT_CONFLICT", "Rejected", 1)]
    [InlineData(HttpStatusCode.Conflict, "DAILY_CLOSE_SYNC_CONCURRENT_UPDATE", "Pending", 1)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, null, "Rejected", 1)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "UNPROCESSABLE", "Rejected", 1)]
    [InlineData(HttpStatusCode.Unauthorized, "DEVICE_AUTH_REQUIRED", "Pending", 0)]
    [InlineData(HttpStatusCode.Forbidden, "DEVICE_SCOPE_FORBIDDEN", "Pending", 0)]
    [InlineData(HttpStatusCode.InternalServerError, null, "Pending", 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, "Pending", 1)]
    [InlineData(HttpStatusCode.RequestTimeout, null, "Pending", 1)]
    [InlineData(HttpStatusCode.TooManyRequests, null, "Pending", 1)]
    [InlineData(HttpStatusCode.NotFound, null, "Pending", 1)]
    public async Task Status_codes_end_up_in_the_expected_local_upload_state(
        HttpStatusCode statusCode,
        string? code,
        string expectedStatus,
        int expectedAttemptCount)
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guid = await fixture.InsertDailyCloseAsync();
        var handler = new StubHandler(_ => Task.FromResult(statusCode == HttpStatusCode.OK
            ? Json(statusCode, """{"accepted":true,"alreadySynced":false,"replacedPlaceholder":false}""")
            : Json(statusCode, code is null ? string.Empty : $$"""{"code":"{{code}}","message":"m"}""")));
        var service = fixture.CreateService(
            new DailyCloseSyncApiClient(new HttpClient(handler) { BaseAddress = BaseAddress }),
            new MutableTimeProvider(new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero)));

        var result = await service.ExecutePendingAsync();

        var row = await fixture.ReadUploadRowAsync(guid);
        Assert.Equal(expectedStatus, row.Status);
        // 401/403 保持 Pending 且不增尝试次数，并中断批次；其余失败消耗一次尝试。
        Assert.Equal(expectedAttemptCount == 0 && expectedStatus == "Pending", result.WasInterrupted);
        Assert.Equal(statusCode == HttpStatusCode.OK ? 1 : expectedAttemptCount, row.AttemptCount);
    }

    private static DailyCloseSyncRequest SampleRequest()
    {
        return new DailyCloseSyncRequest(
            1,
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "S001",
            "POS-01",
            "Wpf",
            new DateOnly(2026, 5, 28),
            new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.FromHours(10)),
            new DateTimeOffset(2026, 5, 29, 0, 0, 0, TimeSpan.FromHours(10)),
            new DateTimeOffset(2026, 5, 28, 22, 30, 15, TimeSpan.FromHours(10)),
            "C001",
            "Alice",
            "1.0.47",
            3,
            1m,
            5m,
            [
                new DailyCloseTenderSync("Cash", 90m, 0m, 90m),
                new DailyCloseTenderSync("Card", 50m, 5m, 45m),
                new DailyCloseTenderSync("Voucher", 10m, 0m, 10m)
            ],
            DailyCloseContractConstants.DenominationCents
                .Select(cents => new DailyCloseCashCountSync(cents, 0))
                .ToList(),
            0m,
            0m,
            0m,
            -90m);
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }
}
