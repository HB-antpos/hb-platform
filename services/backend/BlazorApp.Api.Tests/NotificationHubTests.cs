using System.Security.Claims;
using System.Text.Encodings.Web;
using BlazorApp.Api.Features.Notifications;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 在进程内起真实的 SignalR 端点，验证个人分组投递与令牌类型限制；JWT 解析由测试鉴权方案替代。
/// </summary>
public sealed class NotificationHubTests : IAsyncLifetime
{
    private const string TestScheme = "Test";
    private const string UserHeader = "X-Test-User";
    private const string TokenUseHeader = "X-Test-TokenUse";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private IHost _host = null!;
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddSignalR();
                    services.AddAuthentication(TestScheme)
                        .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(TestScheme, _ => { });
                    services.AddAuthorization();
                    services.AddSingleton<INotificationRealtimeDispatcher, SignalRNotificationRealtimeDispatcher>();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapHub<NotificationHub>(NotificationHub.Path));
                }))
            .StartAsync();
        _server = _host.GetTestServer();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task 投递只到达目标账号_账号标识不区分大小写()
    {
        await using var alice = Connect("Alice-GUID");
        await using var bob = Connect("bob-guid");
        var aliceReceived = Subscribe(alice);
        var bobReceived = Subscribe(bob);
        await alice.StartAsync();
        await bob.StartAsync();

        await Dispatcher.SendToUsersAsync(["alice-guid"], new NotificationDto { Id = "n1", Title = "改价" });

        var delivered = await aliceReceived.Task.WaitAsync(WaitTimeout);
        Assert.Equal("n1", delivered.Id);
        Assert.Equal("改价", delivered.Title);
        // 给 Bob 留出同样的投递窗口，确认没有串到别人。
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.False(bobReceived.Task.IsCompleted);
    }

    [Fact]
    public async Task 未登录无法连接()
    {
        await using var anonymous = Connect(userGuid: null);

        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
    }

    [Fact]
    public async Task 浏览器扩展令牌连接被断开_收不到通知()
    {
        await using var extension = Connect("ext-user", tokenUse: "browser_extension");
        var received = Subscribe(extension);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        extension.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        try
        {
            await extension.StartAsync();
            await closed.Task.WaitAsync(WaitTimeout);
        }
        catch (Exception) when (extension.State == HubConnectionState.Disconnected)
        {
            // 握手阶段就被服务端中止也算拒绝。
        }

        Assert.Equal(HubConnectionState.Disconnected, extension.State);
        await Dispatcher.SendToUsersAsync(["ext-user"], new NotificationDto { Id = "n2" });
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.False(received.Task.IsCompleted);
    }

    private INotificationRealtimeDispatcher Dispatcher =>
        _host.Services.GetRequiredService<INotificationRealtimeDispatcher>();

    private HubConnection Connect(string? userGuid, string? tokenUse = null)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_server.BaseAddress, NotificationHub.Path.TrimStart('/')), options =>
            {
                // TestServer 不支持真实 WebSocket 升级，用长轮询走同一套鉴权与分组逻辑。
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _server.CreateHandler();
                if (userGuid != null)
                {
                    options.Headers[UserHeader] = userGuid;
                }

                if (tokenUse != null)
                {
                    options.Headers[TokenUseHeader] = tokenUse;
                }
            })
            .Build();
    }

    private static TaskCompletionSource<NotificationDto> Subscribe(HubConnection connection)
    {
        var received = new TaskCompletionSource<NotificationDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<NotificationDto>(NotificationHub.NotificationReceivedMethod, dto => received.TrySetResult(dto));
        return received;
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder
    ) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userGuid = Request.Headers[UserHeader].FirstOrDefault();
            if (string.IsNullOrEmpty(userGuid))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new("userId", userGuid) };
            var tokenUse = Request.Headers[TokenUseHeader].FirstOrDefault();
            if (!string.IsNullOrEmpty(tokenUse))
            {
                claims.Add(new Claim("token_use", tokenUse));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, TestScheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, TestScheme)));
        }
    }
}
