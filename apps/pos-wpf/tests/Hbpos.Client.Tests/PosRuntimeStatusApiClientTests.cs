using System.Net;
using System.Net.Http;
using System.Text.Json;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class PosRuntimeStatusApiClientTests
{
    [Fact]
    public async Task ReportAsync_sends_current_app_version_with_each_heartbeat()
    {
        var handler = new CapturingHandler();
        var client = new PosRuntimeStatusApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") },
            new FixedAppVersionProvider("1.0.51"));

        await client.ReportAsync(new PosRuntimeStatusReport(true, "CASHIER-1", "Alice"));

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://localhost:5000/api/v1/devices/runtime-status", handler.RequestUri?.ToString());
        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.True(root.GetProperty("isOnline").GetBoolean());
        Assert.Equal("CASHIER-1", root.GetProperty("currentCashierId").GetString());
        Assert.Equal("Alice", root.GetProperty("currentCashierName").GetString());
        // 后台设备列表靠这个字段显示 WPF 当前版本。
        Assert.Equal("1.0.51", root.GetProperty("appVersion").GetString());
    }

    [Fact]
    public async Task ReportAsync_without_version_provider_sends_null_app_version()
    {
        // 未提供版本时必须发 null 而不是空串：服务端据此保留库里上次上报的版本。
        var handler = new CapturingHandler();
        var client = new PosRuntimeStatusApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") });

        await client.ReportAsync(new PosRuntimeStatusReport(false, null, null));

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("appVersion").ValueKind);
    }

    private sealed class FixedAppVersionProvider(string version) : IAppVersionProvider
    {
        public string CurrentVersion => version;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            // 请求体在请求发送后会被释放，必须在处理器内读取。
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
