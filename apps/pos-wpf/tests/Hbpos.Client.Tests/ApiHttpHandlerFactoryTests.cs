using System.Net;
using System.Net.Sockets;
using System.Text;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class ApiHttpHandlerFactoryTests
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);

    [Fact]
    public async Task Connects_to_a_listening_server_and_returns_the_response()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptSocketAsync();
            var buffer = new byte[4096];
            _ = await connection.ReceiveAsync(buffer);
            await connection.SendAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        });
        using var client = new HttpClient(ApiHttpHandlerFactory.Create(ConnectTimeout));

        var body = await client.GetStringAsync($"http://127.0.0.1:{port}/");

        Assert.Equal("ok", body);
        await server.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
    }

    [Fact]
    public async Task A_refused_connection_surfaces_as_the_connection_error_the_failover_recognises()
    {
        // 先监听再释放端口，保证目标端口确实没有服务（连接被拒绝）。
        int port;
        using (var temporary = new TcpListener(IPAddress.Loopback, 0))
        {
            temporary.Start();
            port = ((IPEndPoint)temporary.LocalEndpoint).Port;
        }

        using var client = new HttpClient(ApiHttpHandlerFactory.Create(ConnectTimeout));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync($"http://127.0.0.1:{port}/"));

        // 故障转移处理器只认「连接阶段失败」；建连超时同样会被转成这个类型（见 ApiHttpHandlerFactory 的说明）。
        Assert.Equal(HttpRequestError.ConnectionError, exception.HttpRequestError);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_connect_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new HttpClient(ApiHttpHandlerFactory.Create(ConnectTimeout));

        // 调用方主动取消必须以取消异常结束，不能被包装成 HttpRequestException，否则会误触发入口切换。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("http://127.0.0.1:9/", cancellation.Token));
    }
}
