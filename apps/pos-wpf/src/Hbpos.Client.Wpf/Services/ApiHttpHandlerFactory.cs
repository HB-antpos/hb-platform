using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 创建带「建连超时」的 SocketsHttpHandler。
/// 为什么不用 SocketsHttpHandler.ConnectTimeout：实测（线路黑洞：SYN 被丢弃）它超时后抛的是
/// TaskCanceledException（内含 TimeoutException），与 HttpClient.Timeout 触发的异常类型完全相同，
/// 无法区分「建连超时」和「整个请求超时」，入口故障转移就无从判断。
/// 这里自己接管建连：超时时主动抛 SocketException(TimedOut)，SocketsHttpHandler 会把它包装成
/// HttpRequestException(ConnectionError)，故障转移处理器据此稳定识别「连接阶段失败」。
/// </summary>
internal static class ApiHttpHandlerFactory
{
    public static SocketsHttpHandler Create(TimeSpan connectTimeout)
    {
        return new SocketsHttpHandler
        {
            // 与 HttpClientFactory 默认处理器保持一致：不使用 Cookie 容器。
            UseCookies = false,
            ConnectCallback = (context, cancellationToken) =>
                ConnectAsync(context.DnsEndPoint, connectTimeout, cancellationToken)
        };
    }

    internal static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endPoint,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectTimeout);

        // 与 SocketsHttpHandler 默认建连方式一致：关闭 Nagle，按 DnsEndPoint 解析并依次尝试各个地址
        // （域名解析的耗时也计入建连超时）。
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endPoint, timeout.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 不是调用方取消，而是我们的建连超时到了。
            socket.Dispose();
            throw new SocketException((int)SocketError.TimedOut);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
