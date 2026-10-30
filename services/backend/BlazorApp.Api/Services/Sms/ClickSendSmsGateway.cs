using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Services.Sms;

/// <summary>服务商返回的结果。ErrorCode 只放服务商错误码或 HTTP 状态，不放号码与正文。</summary>
public sealed record SmsGatewayResult(bool Success, string? ProviderMessageId, string? ErrorCode);

/// <summary>短信服务商适配层。换服务商只需要新增一个实现并改 Program.cs 的注册。</summary>
public interface ISmsGateway
{
    Task<SmsGatewayResult> SendAsync(string toE164, string body, CancellationToken cancellationToken);
}

/// <summary>
/// ClickSend REST v3（不引入 SDK）：POST /v3/sms/send，Basic 认证（用户名:API Key），JSON 体 messages[]。
/// 注意 ClickSend 的 HTTP 200 只代表请求被受理，单条短信是否成功要看 data.messages[0].status，
/// 所以成功必须同时满足：HTTP 2xx、顶层 response_code=SUCCESS、该条 status=SUCCESS。
/// 日志只记 HTTP 状态与服务商错误码，不记号码、正文与凭据。
/// </summary>
internal sealed class ClickSendSmsGateway : ISmsGateway
{
    private const string SuccessCode = "SUCCESS";

    private readonly HttpClient _http;
    private readonly SmsOptions _options;
    private readonly ILogger<ClickSendSmsGateway> _logger;

    public ClickSendSmsGateway(HttpClient http, IOptions<SmsOptions> options, ILogger<ClickSendSmsGateway> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SmsGatewayResult> SendAsync(string toE164, string body, CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/v3/sms/send";
        var payload = JsonSerializer.Serialize(new
        {
            messages = new[]
            {
                new { source = "hb-platform", from = _options.From, to = toE164, body },
            },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Username}:{_options.ApiKey}")));

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            string? topCode = null;
            string? messageStatus = null;
            string? messageId = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    topCode = ReadString(doc.RootElement, "response_code");
                    if (doc.RootElement.TryGetProperty("data", out var data)
                        && data.ValueKind == JsonValueKind.Object
                        && data.TryGetProperty("messages", out var messages)
                        && messages.ValueKind == JsonValueKind.Array
                        && messages.GetArrayLength() > 0
                        && messages[0].ValueKind == JsonValueKind.Object)
                    {
                        messageStatus = ReadString(messages[0], "status");
                        messageId = ReadString(messages[0], "message_id");
                    }
                }
            }
            catch (JsonException)
            {
                // 响应不是 JSON（网关错误页等）：按 HTTP 状态处理。
            }

            if (response.IsSuccessStatusCode
                && string.Equals(topCode, SuccessCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(messageStatus, SuccessCode, StringComparison.OrdinalIgnoreCase))
            {
                return new SmsGatewayResult(true, messageId, null);
            }

            // 错误码优先级：单条状态（号码无效、余额不足、发件方不允许等）> 顶层码 > HTTP 状态。
            var errorCode = !response.IsSuccessStatusCode
                ? (topCode is null ? $"HTTP_{(int)response.StatusCode}" : $"CLICKSEND_{Sanitize(topCode)}")
                : messageStatus is not null
                    ? $"CLICKSEND_{Sanitize(messageStatus)}"
                    : topCode is not null && !string.Equals(topCode, SuccessCode, StringComparison.OrdinalIgnoreCase)
                        ? $"CLICKSEND_{Sanitize(topCode)}"
                        : "CLICKSEND_NO_MESSAGE";
            _logger.LogWarning("短信服务商返回失败，HTTP {Status}，错误码 {ErrorCode}", (int)response.StatusCode, errorCode);
            return new SmsGatewayResult(false, null, errorCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 只记异常类型；异常消息里可能带请求地址，不写进日志。
            _logger.LogError("短信服务商请求异常：{ExceptionType}", ex.GetType().Name);
            return new SmsGatewayResult(false, null, "GATEWAY_EXCEPTION");
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// 服务商状态文本进入日志与记录表前只保留大写字母、数字和下划线并截断：
    /// 防止对方返回的自由文本（可能夹带号码）原样落库，也保证不超过 ErrorCode 列宽。
    /// </summary>
    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (builder.Length >= 40) break;
            if (char.IsAsciiLetterOrDigit(ch)) builder.Append(char.ToUpperInvariant(ch));
            else if (ch is '_' or ' ' or '-') builder.Append('_');
        }

        return builder.Length == 0 ? "UNKNOWN" : builder.ToString();
    }
}
