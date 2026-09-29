using System.Net;
using System.Text;
using System.Text.Json;
using Hbpos.Api.Services;
using Hbpos.Contracts.RemoteMaintenance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

/// <summary>
/// 直接覆盖远程维护网关：配置守卫、转发到中心的地址与鉴权头、中心拒绝时的错误码透传、
/// 网络异常与调用方取消的区分，以及 artifact 下载流对 HTTP 响应生命周期的接管。
/// 只在 HttpMessageHandler 边界替身，不连接真实中心。
/// </summary>
public sealed class RemoteMaintenanceGatewayTests
{
    private const string CenterBaseUrl = "https://center.example.test/";
    private const string InterApiKey = "inter-api-key-001";
    private static readonly Guid OperationId = Guid.Parse("5f3b2c1d-9e8a-4b7c-8d6e-1a2b3c4d5e6f");

    [Theory]
    [InlineData(false, CenterBaseUrl, InterApiKey)]
    [InlineData(true, "", InterApiKey)]
    [InlineData(true, "   ", InterApiKey)]
    [InlineData(true, CenterBaseUrl, "")]
    [InlineData(true, CenterBaseUrl, "  ")]
    public async Task Unconfigured_gateway_fails_all_operations_without_calling_center(
        bool enabled,
        string centerBaseUrl,
        string interApiKey)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("center must not be called"));
        var gateway = CreateGateway(handler, enabled, centerBaseUrl, interApiKey);

        var prepare = await gateway.PrepareAsync("HW-001", PrepareRequest(), CancellationToken.None);
        var commit = await gateway.CommitAsync("HW-001", CommitRequest(), CancellationToken.None);
        var download = await gateway.DownloadArtifactAsync("HW-001", "rustdesk", CancellationToken.None);

        AssertFailure(prepare, "REMOTE_MAINTENANCE_DISABLED", statusCode: 0);
        AssertFailure(commit, "REMOTE_MAINTENANCE_DISABLED", statusCode: 0);
        // 下载直接返回 503；prepare/commit 返回 0，由控制器按错误码映射为 503。
        AssertFailure(download, "REMOTE_MAINTENANCE_DISABLED", statusCode: 503);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("center.example.test")]
    [InlineData("not a url")]
    public async Task Non_absolute_center_url_is_rejected_without_calling_center(string centerBaseUrl)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("center must not be called"));
        var gateway = CreateGateway(handler, centerBaseUrl: centerBaseUrl);

        var prepare = await gateway.PrepareAsync("HW-001", PrepareRequest(), CancellationToken.None);
        var download = await gateway.DownloadArtifactAsync("HW-001", "status-agent", CancellationToken.None);

        AssertFailure(prepare, "REMOTE_MAINTENANCE_GATEWAY_INVALID", statusCode: 0);
        AssertFailure(download, "REMOTE_MAINTENANCE_GATEWAY_INVALID", statusCode: 503);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Prepare_posts_camel_case_request_with_trimmed_hardware_id_and_inter_api_key()
    {
        var response = PrepareResponse();
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, response));
        var gateway = CreateGateway(handler);

        var result = await gateway.PrepareAsync("  HW-001\t", PrepareRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.Code);
        // 网关原样返回中心数据；下载地址改写由控制器负责，不在网关层。
        Assert.Equal(response, result.Data);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://center.example.test/api/remote-maintenance/prepare"), request.Uri);
        Assert.Equal(InterApiKey, request.Headers["X-HBPOS-Remote-Maintenance-Key"]);
        Assert.Equal("HW-001", request.Headers["X-HBPOS-Hardware-Id"]);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(OperationId, body.RootElement.GetProperty("operationId").GetGuid());
        Assert.Equal("POS-PC-01", body.RootElement.GetProperty("computerName").GetString());
    }

    [Fact]
    public async Task Commit_posts_to_commit_endpoint_and_returns_center_response()
    {
        var committed = new RemoteMaintenanceCommitResponse(Guid.NewGuid(), "monitor-token", "https://center.example.test/heartbeat");
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, committed));
        var gateway = CreateGateway(handler);

        var result = await gateway.CommitAsync("HW-002", CommitRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(committed, result.Data);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://center.example.test/api/remote-maintenance/commit"), request.Uri);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("123456789", body.RootElement.GetProperty("rustdeskId").GetString());
        Assert.Equal("1.4.2", body.RootElement.GetProperty("clientVersion").GetString());
        Assert.Equal("one-time-password", body.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task Center_base_url_path_is_kept_when_it_ends_with_slash()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, PrepareResponse()));
        var gateway = CreateGateway(handler, centerBaseUrl: "https://center.example.test/hb/");

        await gateway.PrepareAsync("HW-001", PrepareRequest(), CancellationToken.None);

        Assert.Equal(
            new Uri("https://center.example.test/hb/api/remote-maintenance/prepare"),
            Assert.Single(handler.Requests).Uri);
    }

    [Fact]
    public async Task Successful_response_with_null_body_is_reported_as_empty()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        });
        var gateway = CreateGateway(handler);

        var result = await gateway.PrepareAsync("HW-001", PrepareRequest(), CancellationToken.None);

        AssertFailure(result, "REMOTE_MAINTENANCE_EMPTY_RESPONSE", statusCode: 0);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "REMOTE_MAINTENANCE_PASSWORD_MISMATCH")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "REMOTE_MAINTENANCE_NOT_READY")]
    [InlineData(HttpStatusCode.Forbidden, "REMOTE_MAINTENANCE_DEVICE_NOT_ALLOWED")]
    public async Task Center_rejection_passes_through_error_code_and_status(HttpStatusCode status, string code)
    {
        var handler = new RecordingHandler(_ => JsonText(status, $$"""{"code":"{{code}}","message":"center detail"}"""));
        var gateway = CreateGateway(handler);

        var prepare = await gateway.PrepareAsync("HW-001", PrepareRequest(), CancellationToken.None);
        var commit = await gateway.CommitAsync("HW-001", CommitRequest(), CancellationToken.None);

        AssertFailure(prepare, code, (int)status);
        // 中心的原始 message 不透传给终端，只给固定的本地化提示。
        Assert.Equal("远程维护中心拒绝请求", prepare.Message);
        AssertFailure(commit, code, (int)status);
    }

    [Theory]
    [InlineData("<html>bad gateway</html>")]
    [InlineData("""{"message":"no code"}""")]
    [InlineData("")]
    public async Task Center_rejection_without_readable_code_uses_generic_rejected_code(string body)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        });
        var gateway = CreateGateway(handler);

        var result = await gateway.PrepareAsync("HW-001", PrepareRequest(), CancellationToken.None);

        AssertFailure(result, "REMOTE_MAINTENANCE_GATEWAY_REJECTED", 502);
    }

    [Fact]
    public async Task Oversized_rejection_body_is_not_parsed_for_code()
    {
        var padding = new string('x', 20_000);
        var handler = new RecordingHandler(_ => JsonText(
            HttpStatusCode.Conflict,
            $$"""{"code":"REMOTE_MAINTENANCE_PASSWORD_MISMATCH","padding":"{{padding}}"}"""));
        var gateway = CreateGateway(handler);

        var result = await gateway.CommitAsync("HW-001", CommitRequest(), CancellationToken.None);

        AssertFailure(result, "REMOTE_MAINTENANCE_GATEWAY_REJECTED", 409);
    }

    [Fact]
    public async Task Network_failure_and_invalid_success_json_become_unavailable()
    {
        var networkGateway = CreateGateway(new RecordingHandler(_ => throw new HttpRequestException("connection refused")));
        var invalidJsonGateway = CreateGateway(new RecordingHandler(_ => JsonText(HttpStatusCode.OK, "{not-json")));

        var network = await networkGateway.PrepareAsync("HW-001", PrepareRequest(), CancellationToken.None);
        var invalidJson = await invalidJsonGateway.CommitAsync("HW-001", CommitRequest(), CancellationToken.None);

        AssertFailure(network, "REMOTE_MAINTENANCE_GATEWAY_UNAVAILABLE", 503);
        AssertFailure(invalidJson, "REMOTE_MAINTENANCE_GATEWAY_UNAVAILABLE", 503);
    }

    [Fact]
    public async Task Http_timeout_without_caller_cancellation_is_unavailable_not_cancelled()
    {
        // HttpClient 超时表现为 TaskCanceledException，但调用方并未取消，必须按中心不可用处理。
        var handler = new RecordingHandler(_ => throw new TaskCanceledException("HttpClient.Timeout elapsed"));
        var gateway = CreateGateway(handler);

        var prepare = await gateway.PrepareAsync("HW-001", PrepareRequest(), CancellationToken.None);
        var download = await gateway.DownloadArtifactAsync("HW-001", "rustdesk", CancellationToken.None);

        AssertFailure(prepare, "REMOTE_MAINTENANCE_GATEWAY_UNAVAILABLE", 503);
        AssertFailure(download, "REMOTE_MAINTENANCE_GATEWAY_UNAVAILABLE", 503);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_being_reported_as_unavailable()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var gateway = CreateGateway(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gateway.PrepareAsync("HW-001", PrepareRequest(), cancellation.Token));
    }

    [Fact]
    public async Task Download_caller_cancellation_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var gateway = CreateGateway(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gateway.DownloadArtifactAsync("HW-001", "rustdesk", cancellation.Token));
    }

    [Theory]
    [InlineData("RustDesk")]
    [InlineData("status_agent")]
    [InlineData("../secrets")]
    [InlineData("")]
    public async Task Download_rejects_unknown_artifact_kind_without_calling_center(string kind)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("center must not be called"));
        var gateway = CreateGateway(handler);

        var result = await gateway.DownloadArtifactAsync("HW-001", kind, CancellationToken.None);

        AssertFailure(result, "REMOTE_MAINTENANCE_ARTIFACT_KIND_INVALID", 400);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("rustdesk")]
    [InlineData("status-agent")]
    public async Task Download_streams_artifact_and_disposing_stream_releases_response(string kind)
    {
        var payload = Encoding.UTF8.GetBytes("artifact-bytes-" + kind);
        TrackingResponse? response = null;
        var handler = new RecordingHandler(_ =>
        {
            response = new TrackingResponse(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            return response;
        });
        var gateway = CreateGateway(handler);

        var result = await gateway.DownloadArtifactAsync(" HW-003 ", kind, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri($"https://center.example.test/api/remote-maintenance/artifacts/{kind}"), request.Uri);
        Assert.Equal(InterApiKey, request.Headers["X-HBPOS-Remote-Maintenance-Key"]);
        Assert.Equal("HW-003", request.Headers["X-HBPOS-Hardware-Id"]);

        var stream = result.Data!;
        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        using (var copy = new MemoryStream())
        {
            await stream.CopyToAsync(copy);
            Assert.Equal(payload, copy.ToArray());
        }

        // 响应头已读取但正文由调用方流式消费；在流释放前响应必须保持打开。
        Assert.False(response!.IsDisposed);
        await stream.DisposeAsync();
        Assert.True(response.IsDisposed);
    }

    [Fact]
    public async Task Download_synchronous_dispose_also_releases_response()
    {
        TrackingResponse? response = null;
        var handler = new RecordingHandler(_ => response = new TrackingResponse(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        });
        var gateway = CreateGateway(handler);

        var result = await gateway.DownloadArtifactAsync("HW-001", "rustdesk", CancellationToken.None);
        result.Data!.Dispose();

        Assert.True(response!.IsDisposed);
    }

    [Fact]
    public async Task Download_rejection_disposes_response_and_passes_through_code()
    {
        TrackingResponse? response = null;
        var handler = new RecordingHandler(_ => response = new TrackingResponse(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"code":"REMOTE_MAINTENANCE_ARTIFACT_MISSING"}""", Encoding.UTF8, "application/json")
        });
        var gateway = CreateGateway(handler);

        var result = await gateway.DownloadArtifactAsync("HW-001", "status-agent", CancellationToken.None);

        AssertFailure(result, "REMOTE_MAINTENANCE_ARTIFACT_MISSING", 404);
        Assert.Equal("远程维护文件下载失败", result.Message);
        Assert.True(response!.IsDisposed);
    }

    [Fact]
    public async Task Download_rejection_without_code_uses_generic_rejected_code()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("oops")
        });
        var gateway = CreateGateway(handler);

        var result = await gateway.DownloadArtifactAsync("HW-001", "rustdesk", CancellationToken.None);

        AssertFailure(result, "REMOTE_MAINTENANCE_GATEWAY_REJECTED", 500);
    }

    [Fact]
    public async Task Download_network_failure_is_unavailable()
    {
        var gateway = CreateGateway(new RecordingHandler(_ => throw new HttpRequestException("dns failure")));

        var result = await gateway.DownloadArtifactAsync("HW-001", "rustdesk", CancellationToken.None);

        AssertFailure(result, "REMOTE_MAINTENANCE_GATEWAY_UNAVAILABLE", 503);
    }

    private static RemoteMaintenanceGateway CreateGateway(
        RecordingHandler handler,
        bool enabled = true,
        string centerBaseUrl = CenterBaseUrl,
        string interApiKey = InterApiKey) =>
        new(
            new HttpClient(handler),
            Options.Create(new RemoteMaintenanceGatewayOptions
            {
                Enabled = enabled,
                CenterBaseUrl = centerBaseUrl,
                InterApiKey = interApiKey
            }),
            NullLogger<RemoteMaintenanceGateway>.Instance);

    private static void AssertFailure<T>(RemoteMaintenanceGatewayResult<T> result, string code, int statusCode)
    {
        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(code, result.Code);
        Assert.Equal(statusCode, result.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    private static RemoteMaintenancePrepareRequest PrepareRequest() => new(OperationId, "POS-PC-01");

    private static RemoteMaintenanceCommitRequest CommitRequest() =>
        new(OperationId, "123456789", "1.4.2", "one-time-password");

    private static RemoteMaintenancePrepareResponse PrepareResponse()
    {
        var rustdesk = new RemoteMaintenanceArtifact("1.4.2", "rustdesk.exe", "https://center.example.test/files/rustdesk", "ab12", 1024);
        var agent = new RemoteMaintenanceArtifact("2.0.0", "agent.exe", "https://center.example.test/files/agent", "cd34", 2048);
        return new RemoteMaintenancePrepareResponse(
            OperationId,
            Guid.Parse("0e1d2c3b-4a59-6877-8695-a4b3c2d1e0f9"),
            new RemoteMaintenanceClientConfig("id.example.test", "relay.example.test", "public-key"),
            new RemoteMaintenanceManifest("id.example.test", "relay.example.test", "public-key", rustdesk, agent));
    }

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T value) =>
        JsonText(status, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static HttpResponseMessage JsonText(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? Uri,
        IReadOnlyDictionary<string, string> Headers,
        string? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value)),
                body));
            return respond(request);
        }
    }

    private sealed class TrackingResponse(HttpStatusCode status) : HttpResponseMessage(status)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
