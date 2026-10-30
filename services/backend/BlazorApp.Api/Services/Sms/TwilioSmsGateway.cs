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
/// Twilio Programmable Messaging（REST，不引入 SDK）：POST /2010-04-01/Accounts/{sid}/Messages.json，
/// Basic 认证，表单字段 To / From / Body。日志只记 HTTP 状态与服务商错误码，不记号码、正文与凭据。
/// </summary>
internal sealed class TwilioSmsGateway : ISmsGateway
{
    private readonly HttpClient _http;
    private readonly SmsOptions _options;
    private readonly ILogger<TwilioSmsGateway> _logger;

    public TwilioSmsGateway(HttpClient http, IOptions<SmsOptions> options, ILogger<TwilioSmsGateway> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SmsGatewayResult> SendAsync(string toE164, string body, CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/2010-04-01/Accounts/{Uri.EscapeDataString(_options.AccountSid)}/Messages.json";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["To"] = toE164,
                ["From"] = _options.From,
                ["Body"] = body,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_options.AccountSid}:{_options.AuthToken}")));

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            string? messageSid = null;
            int? providerCode = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("sid", out var sid) && sid.ValueKind == JsonValueKind.String)
                        messageSid = sid.GetString();
                    if (doc.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number)
                        providerCode = code.GetInt32();
                }
            }
            catch (JsonException)
            {
                // 响应不是 JSON（网关错误页等）：按 HTTP 状态处理。
            }

            if (response.IsSuccessStatusCode) return new SmsGatewayResult(true, messageSid, null);

            var errorCode = providerCode is null ? $"HTTP_{(int)response.StatusCode}" : $"TWILIO_{providerCode}";
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
}
