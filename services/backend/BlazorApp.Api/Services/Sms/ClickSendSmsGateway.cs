using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Services.Sms;

/// <summary>服务商返回的结果。ErrorCode 只放服务商错误码或 HTTP 状态，不放号码与正文。</summary>
public sealed record SmsGatewayResult(bool Success, string? ProviderMessageId, string? ErrorCode);

/// <summary>短信服务商适配层。换服务商只需要新增一个实现并改 Program.cs 的注册。</summary>
public interface ISmsGateway
{
    /// <param name="from">发件方（字母发件名或 E.164 号码）。必须由调用方保证已通过白名单校验；服务商要求每条短信都带 from。</param>
    Task<SmsGatewayResult> SendAsync(string toE164, string body, string from, CancellationToken cancellationToken);
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

    public async Task<SmsGatewayResult> SendAsync(string toE164, string body, string from, CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/v3/sms/send";
        // 请求里必须带 from（省略会被 ClickSend 以 HTTP 400 拒绝）；字母发件名与 E.164 号码都原样传。
        var payload = JsonSerializer.Serialize(new
        {
            messages = new[]
            {
                new { source = "hb-platform", from, to = toE164, body },
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
            string? responseMessage = null;
            IReadOnlyList<string> rejectedWebsites = Array.Empty<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    topCode = ReadString(doc.RootElement, "response_code");
                    // response_msg 是服务商给的失败原因（例如 2026-11-09 的 "contains not safe to send websites"），只用于日志。
                    responseMessage = ReadString(doc.RootElement, "response_msg");
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                    {
                        rejectedWebsites = ReadRejectedWebsites(data);
                        if (data.TryGetProperty("messages", out var messages)
                            && messages.ValueKind == JsonValueKind.Array
                            && messages.GetArrayLength() > 0
                            && messages[0].ValueKind == JsonValueKind.Object)
                        {
                            messageStatus = ReadString(messages[0], "status");
                            messageId = ReadString(messages[0], "message_id");
                        }
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
            // 失败原因必须进日志：只记错误码无法区分 400 的真正原因（2026-11-09 排查靠手工重放请求才看到
            // "not safe to send websites"）。服务商说明经 SanitizeReason 清洗（遮蔽长数字 / 邮箱、去控制字符、截断），
            // 被拒网址只留主机名；仍不记号码、正文与凭据。
            _logger.LogWarning(
                "短信服务商返回失败，HTTP {Status}，错误码 {ErrorCode}，服务商说明：{Reason}，被拒网址：{RejectedWebsites}",
                (int)response.StatusCode,
                errorCode,
                SanitizeReason(responseMessage),
                rejectedWebsites.Count == 0 ? "-" : string.Join(", ", rejectedWebsites)
            );
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

    private const int MaxReasonLength = 200;
    private const int MaxLoggedWebsites = 5;
    private const int MaxHostLength = 100;

    // 5 位以上数字（允许夹单个空格 / 连字符，覆盖 "0412 345 678"、"+61 412-345-678"）：手机号、消息 ID 之类，日志里一律遮蔽。
    private static readonly Regex DigitRun = new(@"\+?\d(?:[\s-]?\d){4,}", RegexOptions.Compiled);
    private static readonly Regex EmailLike = new(@"[^\s@]+@[^\s@]+", RegexOptions.Compiled);

    /// <summary>
    /// 服务商 response_msg 进日志前的清洗：服务商返回的是自由文本，可能回显号码 / 邮箱，也可能带换行（日志注入）。
    /// 先遮蔽再截断，避免截断点把半个号码留在日志里；空值返回 "-"。
    /// </summary>
    private static string SanitizeReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";

        var masked = DigitRun.Replace(EmailLike.Replace(value, "***"), "***");
        var builder = new StringBuilder(Math.Min(masked.Length, MaxReasonLength + 3));
        var lastWasSpace = false;
        foreach (var ch in masked.Trim())
        {
            // 控制字符（含换行）与连续空白统一压成一个空格，保证一条日志只占一行。
            var isSpace = char.IsControl(ch) || char.IsWhiteSpace(ch);
            if (isSpace && lastWasSpace) continue;
            builder.Append(isSpace ? ' ' : ch);
            lastWasSpace = isSpace;
            if (builder.Length >= MaxReasonLength)
            {
                builder.Append("...");
                break;
            }
        }

        return builder.Length == 0 ? "-" : builder.ToString();
    }

    /// <summary>
    /// 读取 data.rejected_websites（被判为不安全的网址）：只留主机名——路径和 query 里可能带签字链接令牌，
    /// 不是合法主机名字符的项直接丢弃，最多 5 项。
    /// </summary>
    private static IReadOnlyList<string> ReadRejectedWebsites(JsonElement data)
    {
        if (!data.TryGetProperty("rejected_websites", out var list) || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var hosts = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (hosts.Count >= MaxLoggedWebsites) break;
            if (item.ValueKind != JsonValueKind.String) continue;

            var raw = item.GetString()?.Trim() ?? string.Empty;
            var schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd >= 0) raw = raw[(schemeEnd + 3)..];
            var hostEnd = raw.IndexOfAny(new[] { '/', '?', '#' });
            var host = hostEnd >= 0 ? raw[..hostEnd] : raw;
            if (host.Length is 0 or > MaxHostLength || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) continue;
            hosts.Add(host.ToLowerInvariant());
        }

        return hosts;
    }

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
