using System.Net.Http;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 设备激活失败的具体原因文本（纯逻辑，不依赖 WPF）：激活页只给一句笼统的话时，现场无法判断是码、设备、网络还是服务端的问题。
/// </summary>
internal static class DeviceActivationFailureDescriber
{
    /// <summary>
    /// 通用失败时附带的具体原因：HTTP 状态码 + 服务端原因码；没有原因码时用客户端自己的描述
    /// （如「returned incomplete credentials」），但跳过只是复述状态码的那句。没有任何可说的信息时返回 null。
    /// </summary>
    internal static string? DescribeRejection(CatalogApiException exception)
    {
        var parts = new List<string>();
        if (exception.StatusCode is { } status)
        {
            parts.Add($"HTTP {(int)status}");
        }

        if (!string.IsNullOrWhiteSpace(exception.ErrorCode))
        {
            parts.Add(exception.ErrorCode);
        }
        else if (!string.IsNullOrWhiteSpace(exception.Message)
                 && !exception.Message.StartsWith("Device API request failed with HTTP", StringComparison.Ordinal))
        {
            parts.Add(exception.Message.TrimEnd('.'));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// 非业务异常的原因文本。HttpRequestException 的外层消息常只有「An error occurred while sending the request.」，
    /// 真正的 DNS / SSL / 连接被拒在内层异常里，一并带出来才能判断；本客户端自己抛的「服务不可达」文案已含状态码，原样返回。
    /// </summary>
    internal static string DescribeTransportFailure(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
        if (exception is not HttpRequestException || exception is DeviceApiUnavailableException)
        {
            return message;
        }

        var innermost = exception;
        while (innermost.InnerException is { } next)
        {
            innermost = next;
        }

        return !ReferenceEquals(innermost, exception)
               && !string.IsNullOrWhiteSpace(innermost.Message)
               && !message.Contains(innermost.Message, StringComparison.Ordinal)
            ? $"{message} ({innermost.Message})"
            : message;
    }
}
