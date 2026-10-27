using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Services.Myob;

/// <summary>MYOB token 端点返回；refresh 响应没有 user 字段，且 refresh_token 可能轮换，调用方必须落库新值。</summary>
public sealed record MyobTokenResponse(string AccessToken, string? RefreshToken, int ExpiresInSeconds);

/// <summary>GET /{cf_uri}/Company 的摘要：公司名与 ABN / TFN（只有 11 位数字才是 ABN，由调用方判断）。</summary>
public sealed record MyobCompanyInfo(string CompanyName, string? AbnOrTfn);

/// <summary>
/// 调用 MYOB 失败。Code 取自 <see cref="MyobErrorCodes"/>；IsAuthorizationRevoked＝refresh token 已失效（invalid_grant），
/// 需要公司文件 Administrator 重新授权，调用方据此把连接标成 NeedsReauth。
/// 消息里绝不包含 token / secret / 授权码。
/// </summary>
public sealed class MyobApiException(
    string code,
    string message,
    HttpStatusCode? statusCode = null,
    bool isAuthorizationRevoked = false) : Exception(message)
{
    public string Code { get; } = code;
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public bool IsAuthorizationRevoked { get; } = isAuthorizationRevoked;
}

public interface IMyobApiClient
{
    /// <summary>授权码换 token（授权码 2~5 分钟过期，回调里必须立即调用）。</summary>
    Task<MyobTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken);

    Task<MyobTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

    Task<MyobCompanyInfo> GetCompanyAsync(string businessId, string accessToken, CancellationToken cancellationToken);
}

/// <summary>
/// MYOB Business API v2 的最小客户端（阶段 0 只需 OAuth 与 Company）。
/// 所有请求经同一个最小间隔限流（MYOB 每个 API Key 每秒 8 次）。
/// </summary>
public sealed class MyobApiClient : IMyobApiClient
{
    private const int MaxErrorTextLength = 200;

    // 进程内共用：限流针对 API Key，而不是针对某个连接。
    private static readonly SemaphoreSlim RateGate = new(1, 1);
    private static long _lastRequestTimestamp;

    // MYOB 文档示例里 expires_in 是带引号的字符串，所以允许从字符串读数字；属性名大小写不敏感。
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly MyobOptions _options;

    public MyobApiClient(HttpClient http, IOptions<MyobOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public Task<MyobTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken) =>
        PostTokenAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["scope"] = _options.Scopes,
                ["code"] = code,
                ["redirect_uri"] = _options.RedirectUri,
                ["grant_type"] = "authorization_code",
            },
            MyobErrorCodes.TokenExchangeFailed,
            cancellationToken);

    public Task<MyobTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        PostTokenAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            },
            MyobErrorCodes.TokenExchangeFailed,
            cancellationToken);

    public async Task<MyobCompanyInfo> GetCompanyAsync(
        string businessId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(businessId)}/Company";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("x-myobapi-key", _options.ClientId);
        request.Headers.TryAddWithoutValidation("x-myobapi-version", "v2");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new MyobApiException(
                MyobErrorCodes.ApiCallFailed,
                $"MYOB 公司信息接口返回 {(int)response.StatusCode}",
                response.StatusCode);
        }

        CompanyPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CompanyPayload>(body, JsonOptions);
        }
        catch (JsonException)
        {
            throw new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB 公司信息接口返回了无法解析的内容", response.StatusCode);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.CompanyName))
        {
            throw new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB 公司信息接口没有返回公司名称", response.StatusCode);
        }

        return new MyobCompanyInfo(payload.CompanyName.Trim(), payload.AbnOrTfn);
    }

    private async Task<MyobTokenResponse> PostTokenAsync(
        Dictionary<string, string> form,
        string failureCode,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            TokenErrorPayload? error = null;
            try
            {
                error = JsonSerializer.Deserialize<TokenErrorPayload>(body, JsonOptions);
            }
            catch (JsonException)
            {
                // 非 JSON 的错误体（网关错误页等）：只按状态码处理，不回显原文。
            }

            // invalid_grant＝refresh token 已过期 / 被撤销 / 已用过：只有这种情况才需要重新授权；
            // invalid_client 等是我方配置（Key / Secret）问题，重新授权也解决不了，按普通失败处理。
            var revoked = string.Equals(error?.Error, "invalid_grant", StringComparison.OrdinalIgnoreCase);
            var detail = string.IsNullOrWhiteSpace(error?.Error)
                ? string.Empty
                : $"：{Truncate(error!.Error!)}";
            throw new MyobApiException(
                revoked ? MyobErrorCodes.NeedsReauthorization : failureCode,
                $"MYOB 令牌接口返回 {(int)response.StatusCode}{detail}",
                response.StatusCode,
                revoked);
        }

        TokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TokenPayload>(body, JsonOptions);
        }
        catch (JsonException)
        {
            throw new MyobApiException(failureCode, "MYOB 令牌接口返回了无法解析的内容", response.StatusCode);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            throw new MyobApiException(failureCode, "MYOB 令牌接口没有返回 access_token", response.StatusCode);
        }

        return new MyobTokenResponse(
            payload.AccessToken,
            string.IsNullOrWhiteSpace(payload.RefreshToken) ? null : payload.RefreshToken,
            payload.ExpiresIn > 0 ? payload.ExpiresIn : 1200);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await WaitForRateSlotAsync(cancellationToken);
        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new MyobApiException(MyobErrorCodes.ApiCallFailed, "无法连接 MYOB 服务");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MyobApiException(MyobErrorCodes.ApiCallFailed, "连接 MYOB 服务超时");
        }
    }

    /// <summary>串行等待：保证相邻两次请求的发起时间至少相隔 MinRequestIntervalMs。</summary>
    private async Task WaitForRateSlotAsync(CancellationToken cancellationToken)
    {
        if (_options.MinRequestIntervalMs <= 0)
        {
            return;
        }

        await RateGate.WaitAsync(cancellationToken);
        try
        {
            var interval = TimeSpan.FromMilliseconds(_options.MinRequestIntervalMs);
            var last = Interlocked.Read(ref _lastRequestTimestamp);
            if (last != 0)
            {
                var wait = interval - Stopwatch.GetElapsedTime(last);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken);
                }
            }

            Interlocked.Exchange(ref _lastRequestTimestamp, Stopwatch.GetTimestamp());
        }
        finally
        {
            RateGate.Release();
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaxErrorTextLength ? value : value[..MaxErrorTextLength];

    private sealed class TokenPayload
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private sealed class TokenErrorPayload
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed class CompanyPayload
    {
        public string? CompanyName { get; set; }

        [JsonPropertyName("ABNOrTFN")]
        public string? AbnOrTfn { get; set; }
    }
}
