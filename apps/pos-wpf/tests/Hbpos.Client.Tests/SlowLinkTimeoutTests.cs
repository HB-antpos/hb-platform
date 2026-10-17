using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Client.Tests;

/// <summary>
/// 慢线路（1012 店新设备：浏览器要等好几秒才出健康检查）下，激活 / 在线探测 / 收银员登录不能在 3 秒就判失败。
/// </summary>
public sealed class SlowLinkTimeoutTests
{
    private const string ActivationCode = "HBDEV1-0123456789ABCDEFGHJKMNPQRS-6789ABCDEFGHJKMNPQRSTVWXYZ";

    [Fact]
    public void Timeout_constants_tolerate_a_link_that_needs_several_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), DeviceApiClient.DefaultRequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), DeviceApiClient.VerifyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), ConnectivityApiClient.ProbeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), CashierLoginApiClient.RequestTimeout);

        // 都必须明显大于原来的 3 秒，且 verify / 探测不能比一次性操作更宽松。
        Assert.All(
            new[]
            {
                DeviceApiClient.VerifyTimeout,
                ConnectivityApiClient.ProbeTimeout,
                CashierLoginApiClient.RequestTimeout,
                ApiServerSettingsService.ConnectionTimeout
            },
            timeout => Assert.True(timeout > TimeSpan.FromSeconds(3)));
        Assert.True(DeviceApiClient.VerifyTimeout < DeviceApiClient.DefaultRequestTimeout);
    }

    [Fact]
    public void Service_registration_uses_the_relaxed_timeouts_for_the_http_clients()
    {
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], PreviewMode: true, InitialScreen: null, InitialCulture: null));
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        Assert.Equal(DeviceApiClient.DefaultRequestTimeout, factory.CreateClient(nameof(IDeviceApiClient)).Timeout);
        Assert.Equal(ConnectivityApiClient.ProbeTimeout, factory.CreateClient(nameof(IConnectivityApiClient)).Timeout);
        Assert.Equal(CashierLoginApiClient.RequestTimeout, factory.CreateClient(nameof(ICashierLoginApiClient)).Timeout);
    }

    [Fact]
    public async Task Redeem_succeeds_when_the_server_answers_after_the_old_three_second_limit()
    {
        var client = new DeviceApiClient(new HttpClient(new DelayedHandler(TimeSpan.FromSeconds(3.2)))
        {
            BaseAddress = new Uri("https://pos.example.test/"),
            Timeout = DeviceApiClient.DefaultRequestTimeout
        });

        var result = await client.RedeemActivationCodeAsync(new DeviceActivationCodeRedeemRequest(
            ActivationCode,
            "HW-001",
            "POS-TILL-01",
            DeviceSystems.Windows));

        Assert.True(result.IsAllowed);
        Assert.Equal("AUTH-001", result.AuthorizationCode);
    }

    [Fact]
    public async Task Verify_keeps_a_short_cap_so_startup_is_not_held_by_a_black_holed_network()
    {
        var client = new DeviceApiClient(new HttpClient(new HangingHandler())
        {
            BaseAddress = new Uri("https://pos.example.test/"),
            Timeout = DeviceApiClient.DefaultRequestTimeout
        });
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.VerifyAsync(
            new DeviceVerifyRequest("POS-001", "1002", "HW-001", "AUTH-001")));

        stopwatch.Stop();
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(25));
    }

    [Fact]
    public async Task Caller_cancellation_of_verify_is_still_reported_as_cancellation_not_a_timeout()
    {
        var client = new DeviceApiClient(new HttpClient(new HangingHandler())
        {
            BaseAddress = new Uri("https://pos.example.test/"),
            Timeout = DeviceApiClient.DefaultRequestTimeout
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.VerifyAsync(
            new DeviceVerifyRequest("POS-001", "1002", "HW-001", "AUTH-001"),
            cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.NotNull(exception);
    }

    private sealed class DelayedHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(ApiResult<DeviceActivationCodeRedeemResponse>.Ok(
                    new DeviceActivationCodeRedeemResponse(
                        "POS-001",
                        "1002",
                        "Lutwyche",
                        1,
                        true,
                        "Enabled",
                        "AUTH-001",
                        DeviceActivationReasonCodes.Activated)))
            };
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
