using System.Net;
using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class ApiRuntimeEndpointFailoverTests
{
    private const string PosEntry = "https://pos.hotbargain.vip/pos-api/";

    [Fact]
    public async Task Get_retries_on_the_next_entry_when_the_primary_entry_cannot_connect()
    {
        var terminal = new ScriptedHandler(request => IsPos(request) ? throw ConnectFailure() : Ok());
        var (client, selector, _) = CreateClient(PosEntry, terminal);

        var response = await client.GetAsync("api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "pos.hotbargain.vip", "pos-syd.hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
        // 内层处理器（设备授权头）会修改请求：重试用的请求必须在发送前克隆，否则设备头会重复。
        Assert.All(terminal.Requests, request => Assert.Equal(new[] { "device-1" }, request.DeviceHeaders));
        Assert.True(selector.IsUsingFallback(new Uri(PosEntry)));
    }

    [Fact]
    public async Task Requests_during_the_cooldown_go_straight_to_the_next_entry()
    {
        var terminal = new ScriptedHandler(request => IsPos(request) ? throw ConnectFailure() : Ok());
        var (client, _, _) = CreateClient(PosEntry, terminal);
        await client.GetAsync("api/v1/health");
        terminal.Requests.Clear();

        var response = await client.GetAsync("api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "pos-syd.hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
    }

    [Fact]
    public async Task Post_is_never_retried_but_later_requests_use_the_next_entry()
    {
        var terminal = new ScriptedHandler(request => IsPos(request) ? throw ConnectFailure() : Ok());
        var (client, _, _) = CreateClient(PosEntry, terminal);

        // 写请求即使连接失败也不自动重试：杜绝任何重复提交的可能，由调用方既有的离线队列负责重发。
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostAsync("api/v1/orders", new StringContent("{}")));
        Assert.Equal(new[] { "pos.hotbargain.vip" }, terminal.Requests.Select(r => r.Host));

        // 但这次失败让后续请求直接改走备用入口。
        var next = await client.GetAsync("api/v1/health");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal("pos-syd.hotbargain.vip", terminal.Requests[^1].Host);
    }

    [Fact]
    public async Task A_normal_error_status_from_the_primary_entry_is_returned_untouched()
    {
        var terminal = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var (client, selector, _) = CreateClient(PosEntry, terminal);

        var response = await client.GetAsync("api/v1/health");

        // 503 是源站自己的回答（请求已送达），不是线路问题，换入口也没有意义。
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(terminal.Requests);
        Assert.False(selector.IsUsingFallback(new Uri(PosEntry)));
    }

    [Fact]
    public async Task Edge_origin_failure_status_fails_over_for_get()
    {
        var terminal = new ScriptedHandler(request =>
            IsPos(request) ? new HttpResponseMessage((HttpStatusCode)524) : Ok());
        var (client, selector, _) = CreateClient(PosEntry, terminal);

        var response = await client.GetAsync("api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "pos.hotbargain.vip", "pos-syd.hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
        Assert.True(selector.IsUsingFallback(new Uri(PosEntry)));
    }

    [Fact]
    public async Task Edge_origin_failure_status_is_returned_for_post_but_flips_later_requests()
    {
        var terminal = new ScriptedHandler(request =>
            IsPos(request) ? new HttpResponseMessage((HttpStatusCode)524) : Ok());
        var (client, selector, _) = CreateClient(PosEntry, terminal);

        var response = await client.PostAsync("api/v1/orders", new StringContent("{}"));

        // 524 表示源站可能还在处理这个写请求：不重试，原样交给调用方；但后续请求改走备用入口。
        Assert.Equal((HttpStatusCode)524, response.StatusCode);
        Assert.Single(terminal.Requests);
        Assert.True(selector.IsUsingFallback(new Uri(PosEntry)));
    }

    [Fact]
    public async Task When_every_entry_cannot_connect_only_one_retry_is_made_and_the_error_reaches_the_caller()
    {
        var terminal = new ScriptedHandler(_ => throw ConnectFailure());
        var (client, selector, _) = CreateClient(PosEntry, terminal);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("api/v1/health"));

        // 只换到顺序中的下一个入口重试一次（不是把三个入口都试一遍：避免一次请求被拖到 3 × 4 秒建连超时）。
        Assert.Equal(new[] { "pos.hotbargain.vip", "pos-syd.hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
        // 重试那次的连接失败也要记入冷却，后续请求才会绕开它。
        terminal.Requests.Clear();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("api/v1/health"));
        Assert.Equal(new[] { "hotbargain.vip", "pos.hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
        Assert.True(selector.IsUsingFallback(new Uri(PosEntry)));
    }

    [Fact]
    public async Task Addresses_outside_the_alias_group_are_never_retried()
    {
        var terminal = new ScriptedHandler(_ => throw ConnectFailure());
        var (client, selector, _) = CreateClient("https://old.example.com/pos-api/", terminal);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("api/v1/health"));

        Assert.Single(terminal.Requests);
        Assert.False(selector.IsUsingFallback(new Uri("https://old.example.com/pos-api/")));
    }

    [Fact]
    public async Task The_primary_entry_is_tried_again_after_the_cooldown_and_restored_on_success()
    {
        var primaryDown = true;
        var terminal = new ScriptedHandler(request =>
            IsPos(request) && primaryDown ? throw ConnectFailure() : Ok());
        var (client, selector, time) = CreateClient(PosEntry, terminal);
        await client.GetAsync("api/v1/health");
        Assert.True(selector.IsUsingFallback(new Uri(PosEntry)));

        primaryDown = false;
        time.Advance(TimeSpan.FromMinutes(3));
        terminal.Requests.Clear();
        var response = await client.GetAsync("api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "pos.hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
        Assert.False(selector.IsUsingFallback(new Uri(PosEntry)));
    }

    [Fact]
    public async Task A_post_after_the_cooldown_does_not_probe_the_primary_entry_but_a_get_does()
    {
        var terminal = new ScriptedHandler(request => IsPos(request) ? throw ConnectFailure() : Ok());
        var (client, _, time) = CreateClient(PosEntry, terminal);
        await client.GetAsync("api/v1/health"); // 主入口连接失败，进入 2 分钟冷却。
        time.Advance(TimeSpan.FromMinutes(3)); // 冷却已过，但主入口其实仍然不通。
        terminal.Requests.Clear();

        // 写请求（收款、同步）失败了不会重放，不能拿它去试探一个可能仍然坏掉的主入口。
        var post = await client.PostAsync("api/v1/orders", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal(new[] { "pos-syd.hotbargain.vip" }, terminal.Requests.Select(r => r.Host));

        // 读请求负责试探：先碰主入口，失败后当场换备用入口重试。
        terminal.Requests.Clear();
        var get = await client.GetAsync("api/v1/health");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(new[] { "pos.hotbargain.vip", "pos-syd.hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
    }

    [Fact]
    public void Late_failures_from_concurrent_requests_do_not_escalate_the_backoff()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-22T00:00:00Z"));
        var selector = new ApiTransportSelector(time);
        var configured = new Uri(PosEntry);

        // 一次边缘故障时，同时在途的 6 个请求各自失败：只能算一级冷却，不能被顶到 30 分钟。
        for (var index = 0; index < 6; index++)
        {
            selector.ReportConnectFailure(configured, configured);
        }

        Assert.True(selector.IsUsingFallback(configured));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(selector.IsUsingFallback(configured), "冷却仍是 2 分钟");
    }

    [Fact]
    public async Task Requests_start_on_the_preferred_entry_whichever_alias_is_configured()
    {
        foreach (var (mode, expectedHost) in new[]
                 {
                     ("edge", "pos.hotbargain.vip"),
                     ("aws", "pos-syd.hotbargain.vip"),
                     ("direct", "hotbargain.vip"),
                     (null, "pos.hotbargain.vip")
                 })
        {
            var terminal = new ScriptedHandler(_ => Ok());
            var (client, _, _) = CreateClient("https://hotbargain.vip/pos-api/", terminal, mode);

            await client.GetAsync("api/v1/health");

            Assert.Equal(new[] { expectedHost }, terminal.Requests.Select(r => r.Host));
        }
    }

    [Fact]
    public async Task When_the_preferred_aws_entry_cannot_connect_get_retries_once_on_edge()
    {
        var terminal = new ScriptedHandler(request =>
            IsHost(request, "pos-syd.hotbargain.vip") ? throw ConnectFailure() : Ok());
        var (client, selector, _) = CreateClient(PosEntry, terminal, "aws");

        var response = await client.GetAsync("api/v1/health");

        // 首选 aws 的顺序是 aws → edge → direct：aws 连不上后只换到 edge 重试一次。
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "pos-syd.hotbargain.vip", "pos.hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
        Assert.True(selector.IsUsingFallback(new Uri(PosEntry)));
    }

    [Fact]
    public async Task When_the_preferred_direct_entry_cannot_connect_get_retries_once_on_aws()
    {
        var terminal = new ScriptedHandler(request =>
            IsHost(request, "hotbargain.vip") ? throw ConnectFailure() : Ok());
        var (client, _, _) = CreateClient(PosEntry, terminal, "direct");

        await client.GetAsync("api/v1/health");

        // 首选 direct 的顺序是 direct → aws → edge：直连坏了先走基本不花钱的 AWS，而不是按请求计费的 EdgeOne。
        Assert.Equal(new[] { "hotbargain.vip", "pos-syd.hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
    }

    [Theory]
    [InlineData("aws", "pos-syd.hotbargain.vip")]
    [InlineData("direct", "hotbargain.vip")]
    public async Task Edge_origin_failure_status_codes_only_count_for_the_edgeone_entry(string mode, string host)
    {
        // 520~530 是 EdgeOne 自己的「回源失败」码；AWS 边缘（nginx 回源失败返回 502/504）与直连入口不会产生它们，
        // 这些入口返回此类状态码时按普通响应处理：原样交给调用方，不重试、不进入冷却。
        var terminal = new ScriptedHandler(request =>
            IsHost(request, host) ? new HttpResponseMessage((HttpStatusCode)524) : Ok());
        var (client, selector, _) = CreateClient(PosEntry, terminal, mode);

        var response = await client.GetAsync("api/v1/health");

        Assert.Equal((HttpStatusCode)524, response.StatusCode);
        Assert.Equal(new[] { host }, terminal.Requests.Select(r => r.Host));
        Assert.False(selector.IsUsingFallback(new Uri(PosEntry)));
    }

    [Fact]
    public async Task Gateway_errors_from_the_aws_entry_are_returned_untouched()
    {
        var terminal = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var (client, selector, _) = CreateClient(PosEntry, terminal, "aws");

        var response = await client.GetAsync("api/v1/health");

        // 502/504 与源站自己可能返回的码重叠：不当作边缘故障，只靠连接失败判定。
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Single(terminal.Requests);
        Assert.False(selector.IsUsingFallback(new Uri(PosEntry)));
    }

    [Fact]
    public async Task A_connect_failure_during_the_status_triggered_retry_is_not_retried_again()
    {
        var terminal = new ScriptedHandler(request =>
            IsPos(request)
                ? new HttpResponseMessage((HttpStatusCode)522)
                : throw ConnectFailure());
        var (client, _, _) = CreateClient(PosEntry, terminal);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("api/v1/health"));

        // edge 报 522 → 换 aws 重试一次 → aws 连不上：错误直接抛给调用方，不会再去试 direct 或回头试 edge。
        Assert.Equal(new[] { "pos.hotbargain.vip", "pos-syd.hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
    }

    [Fact]
    public async Task A_post_never_probes_an_entry_that_failed_before_even_after_its_cooldown()
    {
        var terminal = new ScriptedHandler(request =>
            IsPos(request) || IsHost(request, "pos-syd.hotbargain.vip") ? throw ConnectFailure() : Ok());
        var (client, _, time) = CreateClient(PosEntry, terminal);
        // edge 与 aws 都连不上：各自进入冷却（只重试一次，所以这次读请求本身会失败，direct 还没被碰过）。
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("api/v1/health"));
        terminal.Requests.Clear();
        time.Advance(TimeSpan.FromMinutes(3)); // 两个入口的冷却都已结束，但都还没被读请求确认恢复。

        var post = await client.PostAsync("api/v1/orders", new StringContent("{}"));

        // 写请求不探路：直接走从未失败过的 direct。
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal(new[] { "hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
    }

    [Fact]
    public async Task Changing_the_preferred_entry_resets_cooldowns_so_the_next_get_uses_the_new_preference()
    {
        var terminal = new ScriptedHandler(request => IsPos(request) ? throw ConnectFailure() : Ok());
        var (client, selector, _) = CreateClient(PosEntry, terminal);
        await client.GetAsync("api/v1/health"); // edge 进入冷却。
        terminal.Requests.Clear();

        selector.SetPreferredMode("direct");
        await client.GetAsync("api/v1/health");

        Assert.Equal(new[] { "hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
        Assert.False(selector.IsUsingFallback(new Uri(PosEntry)));
    }

    [Fact]
    public async Task External_requests_are_forwarded_without_entry_selection()
    {
        var terminal = new ScriptedHandler(_ => Ok());
        var (client, _, _) = CreateClient(PosEntry, terminal);

        await client.GetAsync("https://cdn.example.com/image.png");

        Assert.Equal(new[] { "cdn.example.com" }, terminal.Requests.Select(r => r.Host));
    }

    private static (HttpClient Client, ApiTransportSelector Selector, FakeTimeProvider Time) CreateClient(
        string configuredAddress,
        ScriptedHandler terminal,
        string? preferredMode = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-22T00:00:00Z"));
        var state = new ApiRuntimeEndpointState(configuredAddress);
        var selector = new ApiTransportSelector(time);
        selector.SetPreferredMode(preferredMode);
        var handler = new ApiRuntimeEndpointHandler(state, selector)
        {
            InnerHandler = new AddDeviceHeaderHandler { InnerHandler = terminal }
        };
        return (new HttpClient(handler) { BaseAddress = state.StartupAddress }, selector, time);
    }

    private static bool IsHost(HttpRequestMessage request, string host) =>
        string.Equals(request.RequestUri!.Host, host, StringComparison.OrdinalIgnoreCase);

    private static bool IsPos(HttpRequestMessage request) =>
        string.Equals(request.RequestUri!.Host, "pos.hotbargain.vip", StringComparison.OrdinalIgnoreCase);

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK);

    private static HttpRequestException ConnectFailure() =>
        new(HttpRequestError.ConnectionError, "connection attempt failed");

    // 模拟生产里排在故障转移处理器内层的设备授权处理器：每次发送都会给请求追加设备头。
    private sealed class AddDeviceHeaderHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Add("X-Device-Code", "device-1");
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Host, string Method, string[] DeviceHeaders)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!.Host,
                request.Method.Method,
                request.Headers.TryGetValues("X-Device-Code", out var values) ? values.ToArray() : []));
            return Task.FromResult(respond(request));
        }
    }
}
