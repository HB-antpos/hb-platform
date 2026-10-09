using System.Net;
using Hbpos.Api.Services;
using Hbpos.Contracts.Linkly;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

// M27：回调可达性没有端到端验证。Nginx /pos-api/ 被 WAF/白名单/前缀改写拦住时，设备 API 正常、健康检查仍绿，
// 按键提示和回单却静默丢失。自探测经公网地址带 bearer 向随机 sessionId 发一次 POST。
[Collection("LinklyLogThrottle")]
public sealed class LinklyCloudCallbackReachabilityProbeTests
{
    private static readonly Uri BaseUri = new("https://hotbargain.vip/pos-api/");

    public LinklyCloudCallbackReachabilityProbeTests() => Hbpos.Api.Logging.LogThrottle.Shared.ResetForTests();

    [Fact]
    public async Task Probe_posts_to_the_public_notification_route_with_bearer_and_a_random_session()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var probe = new HttpLinklyCloudCallbackReachabilityProbe(new HttpClient(handler), new ManualClock());

        var result = await probe.ProbeAsync("Production", BaseUri, "prod-bearer", CancellationToken.None);

        Assert.True(result.Reachable);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer prod-bearer", request.Authorization);
        var segments = request.Uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // /pos-api/api/v1/linkly/cloud-notifications/{environment}/{randomSessionId}/display
        Assert.Equal(["pos-api", "api", "v1", "linkly", "cloud-notifications", "Production"], segments[..6]);
        Assert.Equal("display", segments[^1]);
        Assert.True(Guid.TryParse(segments[^2], out _), "sessionId 必须是随机 GUID，不能碰真实会话");
        Assert.Equal(string.Empty, request.Uri.Query);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized-through-public-url")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden-by-proxy-or-waf")]
    [InlineData(HttpStatusCode.NotFound, "route-not-found-check-proxy-prefix")]
    [InlineData(HttpStatusCode.BadGateway, "http-502")]
    public async Task Probe_classifies_proxy_and_application_failures(HttpStatusCode status, string expectedReason)
    {
        var probe = new HttpLinklyCloudCallbackReachabilityProbe(
            new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(status))),
            new ManualClock());

        var result = await probe.ProbeAsync("Production", BaseUri, "bearer", CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal((int)status, result.HttpStatus);
    }

    [Fact]
    public async Task Probe_reports_connection_failures_without_leaking_urls_or_bearer()
    {
        var probe = new HttpLinklyCloudCallbackReachabilityProbe(
            new HttpClient(new RecordingHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new HttpRequestException(
                "https://hotbargain.vip/pos-api/ secret-bearer", new System.Net.Sockets.SocketException())))),
            new ManualClock());

        var result = await probe.ProbeAsync("Production", BaseUri, "secret-bearer", CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Equal("unreachable-SocketException", result.Reason);
        Assert.DoesNotContain("secret-bearer", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_caches_success_for_minutes_and_failure_only_briefly()
    {
        var clock = new ManualClock();
        var responses = new Queue<HttpStatusCode>([HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.OK]);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(responses.Dequeue()));
        var probe = new HttpLinklyCloudCallbackReachabilityProbe(new HttpClient(handler), clock);

        Assert.True((await probe.ProbeAsync("Production", BaseUri, "b", CancellationToken.None)).Reachable);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True((await probe.ProbeAsync("Production", BaseUri, "b", CancellationToken.None)).Reachable);
        Assert.Single(handler.Requests);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False((await probe.ProbeAsync("Production", BaseUri, "b", CancellationToken.None)).Reachable);
        Assert.Equal(2, handler.Requests.Count);

        // 失败只缓存 30 秒：修好 Nginx 后很快转绿。
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.False((await probe.ProbeAsync("Production", BaseUri, "b", CancellationToken.None)).Reachable);
        Assert.Equal(2, handler.Requests.Count);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True((await probe.ProbeAsync("Production", BaseUri, "b", CancellationToken.None)).Reachable);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Probe_shares_one_request_between_concurrent_callers_and_survives_caller_cancellation()
    {
        var gate = new TaskCompletionSource();
        var handler = new RecordingHandler(async _ =>
        {
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var probe = new HttpLinklyCloudCallbackReachabilityProbe(new HttpClient(handler), new ManualClock());
        using var cancelFirst = new CancellationTokenSource();

        var first = probe.ProbeAsync("Production", BaseUri, "b", cancelFirst.Token);
        var second = probe.ProbeAsync("Production", BaseUri, "b", CancellationToken.None);
        await cancelFirst.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        gate.SetResult();

        Assert.True((await second).Reachable);
        Assert.Single(handler.Requests);
        // 取消的调用方没有把缓存条目永远留在“进行中”：之后的调用直接命中缓存结果。
        Assert.True((await probe.ProbeAsync("Production", BaseUri, "b", CancellationToken.None)).Reachable);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Probe_logs_failures_as_warning_so_central_logging_receives_them()
    {
        var logger = new CapturingLogger();
        var probe = new HttpLinklyCloudCallbackReachabilityProbe(
            new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))),
            new ManualClock(),
            logger);

        await probe.ProbeAsync("Production", BaseUri, "bearer", CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("linkly-callback-unreachable:forbidden-by-proxy-or-waf", entry.EventId.Name);
        Assert.DoesNotContain("bearer", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Health_lists_callback_probe_failure_but_stays_ready_because_it_is_advisory()
    {
        var service = CreateService(new FixedProbe(new LinklyCloudCallbackProbeResult(false, "forbidden-by-proxy-or-waf", 403)));

        var response = await service.GetHealthAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        var check = Assert.Single(response.Checks, item => item.Code == "CALLBACK_REACHABILITY");
        Assert.False(check.IsReady);
        Assert.Contains("forbidden-by-proxy-or-waf", check.Message, StringComparison.Ordinal);
        Assert.True(response.IsReady, "回调丢失只影响按键提示/回单，不能因此阻止刷卡");
    }

    [Fact]
    public async Task Health_adds_a_passing_probe_check_when_the_callback_is_reachable()
    {
        var service = CreateService(new FixedProbe(new LinklyCloudCallbackProbeResult(true, "ok", 200)));

        var response = await service.GetHealthAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.Contains(response.Checks, item => item.Code == "CALLBACK_REACHABILITY" && item.IsReady);
        Assert.True(response.IsReady);
    }

    [Fact]
    public async Task Health_does_not_probe_when_the_public_url_or_bearer_is_not_configured()
    {
        var probe = new FixedProbe(new LinklyCloudCallbackProbeResult(true, "ok", 200));
        var noUrl = CreateService(probe, publicUrl: "https://localhost/callback/");
        var noBearer = CreateService(probe, sandboxBearer: null);

        await noUrl.GetHealthAsync("S01", "POS-01", "Sandbox", CancellationToken.None);
        await noBearer.GetHealthAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Health_still_blocks_on_real_misconfiguration_alongside_the_probe()
    {
        var service = CreateService(
            new FixedProbe(new LinklyCloudCallbackProbeResult(true, "ok", 200)),
            credentialMissing: true);

        var response = await service.GetHealthAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.False(response.IsReady);
        Assert.Contains(response.Checks, item => item.Code == "STORE_CREDENTIAL" && !item.IsReady);
    }

    private static LinklyCloudBackendAsyncService CreateService(
        ILinklyCloudCallbackReachabilityProbe probe,
        string publicUrl = "https://public.example/callback/",
        string? sandboxBearer = "sandbox-notify",
        bool credentialMissing = false) =>
        new(
            repository: null!,
            transport: null!,
            tokenProvider: null!,
            credentialRepository: new FixedCredentialRepository(credentialMissing),
            terminalCredentialRepository: new FixedTerminalCredentialRepository(),
            options: Options.Create(new LinklyCloudBackendAsyncOptions
            {
                SandboxNotificationBearer = sandboxBearer,
                PublicNotificationBaseUrl = publicUrl
            }),
            logger: null,
            terminalService: null,
            callbackReachabilityProbe: probe);

    private sealed class FixedProbe(LinklyCloudCallbackProbeResult result) : ILinklyCloudCallbackReachabilityProbe
    {
        public int Calls { get; private set; }

        public Task<LinklyCloudCallbackProbeResult> ProbeAsync(
            string environment,
            Uri publicNotificationBaseUri,
            string bearer,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class FixedCredentialRepository(bool missing) : ILinklyCloudCredentialRepository
    {
        public Task<LinklyCloudCredentialRecord?> GetByStoreCodeAsync(
            string storeCode, string environment, CancellationToken cancellationToken) =>
            Task.FromResult<LinklyCloudCredentialRecord?>(missing
                ? null
                : new LinklyCloudCredentialRecord { Username = "user", Password = "password" });

        public Task<LinklyCloudCredentialRecord> UpsertAsync(
            string storeCode, string environment, string username, string password,
            DateTime updatedAt, string? updatedBy, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTerminalCredentialRepository : ILinklyCloudBackendTerminalCredentialRepository
    {
        public Task<LinklyCloudBackendTerminalCredentialRecord?> GetByDeviceAsync(
            string environment, string storeCode, string deviceCode, CancellationToken cancellationToken) =>
            Task.FromResult<LinklyCloudBackendTerminalCredentialRecord?>(new LinklyCloudBackendTerminalCredentialRecord
            {
                Secret = "secret",
                PosId = "11111111-1111-4111-8111-111111111111"
            });

        public Task<LinklyCloudBackendTerminalCredentialRecord> UpsertAsync(
            string environment, string storeCode, string deviceCode, string secret, string posId,
            DateTime updatedAt, string? updatedBy, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AcquireLegacyPairingLeaseAsync(
            string environment, string storeCode, Guid attemptId, DateTime leaseExpiresAt,
            DateTime now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReleaseLegacyPairingLeaseAsync(
            string environment, string storeCode, Guid attemptId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendTerminalCredentialRecord> CompleteLegacyPairingAsync(
            string environment, string storeCode, string deviceCode, Guid attemptId, DateTime now,
            string secret, string posId, string? updatedBy, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now += delta;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this(request => Task.FromResult(respond(request)))
        {
        }

        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => this.respond = respond;

        public List<(HttpMethod Method, Uri Uri, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString()));
            }

            return await respond(request);
        }
    }

    private sealed class CapturingLogger : ILogger<HttpLinklyCloudCallbackReachabilityProbe>
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, formatter(state, exception)));
    }
}
