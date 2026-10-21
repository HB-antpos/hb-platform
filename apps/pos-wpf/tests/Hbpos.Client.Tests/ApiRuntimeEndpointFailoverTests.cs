using System.Net;
using Hbpos.Client.Wpf.Services;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class ApiRuntimeEndpointFailoverTests
{
    private const string PosEntry = "https://pos.hotbargain.vip/pos-api/";

    [Fact]
    public async Task Get_retries_on_the_direct_entry_when_the_primary_entry_cannot_connect()
    {
        var terminal = new ScriptedHandler(request => IsPos(request) ? throw ConnectFailure() : Ok());
        var (client, selector, _) = CreateClient(PosEntry, terminal);

        var response = await client.GetAsync("api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "pos.hotbargain.vip", "hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
        // 内层处理器（设备授权头）会修改请求：重试用的请求必须在发送前克隆，否则设备头会重复。
        Assert.All(terminal.Requests, request => Assert.Equal(new[] { "device-1" }, request.DeviceHeaders));
        Assert.True(selector.IsUsingFallback(new Uri(PosEntry)));
    }

    [Fact]
    public async Task Requests_during_the_cooldown_go_straight_to_the_direct_entry()
    {
        var terminal = new ScriptedHandler(request => IsPos(request) ? throw ConnectFailure() : Ok());
        var (client, _, _) = CreateClient(PosEntry, terminal);
        await client.GetAsync("api/v1/health");
        terminal.Requests.Clear();

        var response = await client.GetAsync("api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
    }

    [Fact]
    public async Task Post_is_never_retried_but_later_requests_use_the_direct_entry()
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
        Assert.Equal("hotbargain.vip", terminal.Requests[^1].Host);
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
        Assert.Equal(new[] { "pos.hotbargain.vip", "hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
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
    public async Task When_both_entries_cannot_connect_the_error_reaches_the_caller()
    {
        var terminal = new ScriptedHandler(_ => throw ConnectFailure());
        var (client, _, _) = CreateClient(PosEntry, terminal);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("api/v1/health"));

        Assert.Equal(new[] { "pos.hotbargain.vip", "hotbargain.vip" }, terminal.Requests.Select(r => r.Host));
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
    public async Task External_requests_are_forwarded_without_entry_selection()
    {
        var terminal = new ScriptedHandler(_ => Ok());
        var (client, _, _) = CreateClient(PosEntry, terminal);

        await client.GetAsync("https://cdn.example.com/image.png");

        Assert.Equal(new[] { "cdn.example.com" }, terminal.Requests.Select(r => r.Host));
    }

    private static (HttpClient Client, ApiTransportSelector Selector, FakeTimeProvider Time) CreateClient(
        string configuredAddress,
        ScriptedHandler terminal)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-22T00:00:00Z"));
        var state = new ApiRuntimeEndpointState(configuredAddress);
        var selector = new ApiTransportSelector(time);
        var handler = new ApiRuntimeEndpointHandler(state, selector)
        {
            InnerHandler = new AddDeviceHeaderHandler { InnerHandler = terminal }
        };
        return (new HttpClient(handler) { BaseAddress = state.StartupAddress }, selector, time);
    }

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
