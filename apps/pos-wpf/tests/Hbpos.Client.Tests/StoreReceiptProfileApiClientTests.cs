using System.Net;
using System.Net.Http;
using System.Text;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>门店小票资料接口客户端：sync/ack 的路由、请求体与对旧服务端/缺字段响应的容错。</summary>
public sealed class StoreReceiptProfileApiClientTests
{
    [Fact]
    public async Task Sync_requests_the_known_version_and_parses_a_changed_profile()
    {
        var handler = new RecordingHandler(_ => Json("""
            {"success":true,"data":{"changed":true,"version":4,"profile":{
              "storeCode":"S001","storeName":"Sunnybank","brandName":"HB","address":"Shop 1\nBrisbane",
              "phone":"07 3000 0000","abn":"12 345 678 901","returnPolicy":"7 days","version":4,
              "publishedAt":"2026-10-07T01:02:03+00:00"}}}
            """));
        var client = CreateClient(handler);

        var result = await client.GetSyncAsync(3);

        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
        Assert.Equal("/api/v1/stores/current/receipt-profile/sync?knownVersion=3", handler.Requests.Single().PathAndQuery);
        Assert.True(result.Changed);
        Assert.Equal(4, result.Version);
        Assert.NotNull(result.Profile);
        Assert.Equal("S001", result.Profile!.StoreCode);
        Assert.Equal("Shop 1\nBrisbane", result.Profile.Address);
        Assert.Equal(4, result.Profile.Version);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 1, 2, 3, TimeSpan.Zero), result.Profile.PublishedAt);
    }

    [Fact]
    public async Task Sync_parses_voucher_and_installment_terms_and_treats_missing_terms_as_null()
    {
        var withTerms = CreateClient(new RecordingHandler(_ => Json("""
            {"success":true,"data":{"changed":true,"version":5,"profile":{
              "storeCode":"S001","storeName":"Sunnybank","brandName":"HB","address":null,
              "phone":null,"abn":null,"returnPolicy":null,"version":5,
              "voucherTerms":"Use at the issuing store only.\nNot redeemable for cash.",
              "installmentTerms":"Order total: $50.00 minimum."}}}
            """)));
        // 旧服务端 / 旧快照：JSON 里根本没有这两个字段。
        var withoutTerms = CreateClient(new RecordingHandler(_ => Json("""
            {"success":true,"data":{"changed":true,"version":4,"profile":{
              "storeCode":"S001","storeName":"Sunnybank","brandName":"HB","address":null,
              "phone":null,"abn":null,"returnPolicy":"7 days","version":4}}}
            """)));

        var parsed = (await withTerms.GetSyncAsync(4)).Profile!;
        var legacy = (await withoutTerms.GetSyncAsync(3)).Profile!;

        Assert.Equal("Use at the issuing store only.\nNot redeemable for cash.", parsed.VoucherTerms);
        Assert.Equal("Order total: $50.00 minimum.", parsed.InstallmentTerms);
        Assert.Null(legacy.VoucherTerms);
        Assert.Null(legacy.InstallmentTerms);
        Assert.Equal("7 days", legacy.ReturnPolicy);
    }

    [Fact]
    public async Task Sync_unchanged_response_has_no_profile()
    {
        var client = CreateClient(new RecordingHandler(_ => Json(
            """{"success":true,"data":{"changed":false,"version":4,"profile":null}}""")));

        var result = await client.GetSyncAsync(4);

        Assert.False(result.Changed);
        Assert.Equal(4, result.Version);
        Assert.Null(result.Profile);
    }

    [Theory]
    [InlineData("""{"success":true,"data":{}}""")]
    [InlineData("""{"success":true,"data":null}""")]
    [InlineData("""{"success":true}""")]
    public async Task Sync_tolerates_missing_changed_version_profile_and_data(string body)
    {
        var client = CreateClient(new RecordingHandler(_ => Json(body)));

        var result = await client.GetSyncAsync(0);

        // 契约生成类型里所有字段都是可选的：changed 缺失=false、version 缺失=0、profile 缺失=无资料。
        Assert.False(result.Changed);
        Assert.Equal(0, result.Version);
        Assert.Null(result.Profile);
    }

    [Fact]
    public async Task Sync_sends_negative_known_version_as_zero()
    {
        var handler = new RecordingHandler(_ => Json("""{"success":true,"data":{"changed":false,"version":0}}"""));
        var client = CreateClient(handler);

        await client.GetSyncAsync(-5);

        Assert.EndsWith("knownVersion=0", handler.Requests.Single().PathAndQuery, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>404 Not Found</body></html>")]
    [InlineData("""{"success":false,"errorCode":"NOT_FOUND","message":"nope"}""")]
    public async Task Sync_404_from_an_old_server_surfaces_as_not_found(string body)
    {
        var client = CreateClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() => client.GetSyncAsync(0));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task Sync_400_keeps_the_server_error_code()
    {
        var client = CreateClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"success":false,"errorCode":"STORE_PROFILE_INVALID_CHARACTERS","message":"bad chars"}""",
                Encoding.UTF8,
                "application/json")
        }));

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() => client.GetSyncAsync(1));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("STORE_PROFILE_INVALID_CHARACTERS", exception.ErrorCode);
    }

    [Fact]
    public async Task Ack_posts_the_version_as_json_without_store_or_device_parameters()
    {
        var handler = new RecordingHandler(_ => Json("""{"success":true,"data":{"appliedVersion":5}}"""));
        var client = CreateClient(handler);

        var result = await client.AckAsync(4);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/stores/current/receipt-profile/ack", request.PathAndQuery);
        Assert.Equal("""{"version":4}""", request.Body);
        Assert.Equal("application/json", request.ContentType);
        // 服务端单调不降，可能返回比请求更大的已应用版本。
        Assert.Equal(5, result.AppliedVersion);
    }

    [Fact]
    public async Task Ack_success_without_data_reports_the_requested_version()
    {
        var client = CreateClient(new RecordingHandler(_ => Json("""{"success":true}""")));

        var result = await client.AckAsync(4);

        Assert.Equal(4, result.AppliedVersion);
    }

    [Fact]
    public async Task Ack_400_version_invalid_surfaces_status_and_error_code()
    {
        var client = CreateClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"success":false,"errorCode":"RECEIPT_PROFILE_VERSION_INVALID","message":"invalid"}""",
                Encoding.UTF8,
                "application/json")
        }));

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() => client.AckAsync(9));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("RECEIPT_PROFILE_VERSION_INVALID", exception.ErrorCode);
    }

    [Fact]
    public async Task Get_current_reads_version_and_published_at_and_defaults_them_for_old_servers()
    {
        var withVersion = CreateClient(new RecordingHandler(_ => Json("""
            {"success":true,"data":{"storeCode":"S001","storeName":"Sunnybank","version":2,
             "publishedAt":"2026-10-07T01:02:03+00:00"}}
            """)));
        var legacy = CreateClient(new RecordingHandler(_ => Json(
            """{"success":true,"data":{"storeCode":"S001","storeName":"Sunnybank"}}""")));

        var published = await withVersion.GetCurrentAsync();
        var old = await legacy.GetCurrentAsync();

        Assert.Equal(2, published.Version);
        Assert.NotNull(published.PublishedAt);
        Assert.Equal(0, old.Version);
        Assert.Null(old.PublishedAt);
    }

    [Fact]
    public async Task Get_current_empty_data_still_throws()
    {
        var client = CreateClient(new RecordingHandler(_ => Json("""{"success":true,"data":null}""")));

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() => client.GetCurrentAsync());

        Assert.Contains("empty profile", exception.Message, StringComparison.Ordinal);
    }

    private static StoreReceiptProfileApiClient CreateClient(RecordingHandler handler)
    {
        return new StoreReceiptProfileApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed record RecordedRequest(HttpMethod Method, string PathAndQuery, string? Body, string? ContentType);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly List<RecordedRequest> _requests = [];

        public IReadOnlyList<RecordedRequest> Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // 请求内容会在响应返回后被释放，必须在这里读完。
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            _requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.PathAndQuery,
                body,
                request.Content?.Headers.ContentType?.MediaType));
            return responder(request);
        }
    }
}
