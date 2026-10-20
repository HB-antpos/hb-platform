using System.Net;
using System.Net.Http;
using System.Text.Json;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Square;

namespace Hbpos.Client.Wpf.Services;

/// <summary>Hbpos API 对直连请求的明确拒绝（HTTP 非 2xx 或 success=false）；断网 / 超时不会包装成它，按原异常抛出。</summary>
public sealed class SquareDirectApiException(HttpStatusCode statusCode, string? errorCode, string message)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? ErrorCode { get; } = errorCode;

    /// <summary>总部没有为本门店打开直连开关。</summary>
    public bool IsDisabled => string.Equals(ErrorCode, SquareDirectApiClient.DisabledErrorCode, StringComparison.OrdinalIgnoreCase);
}

/// <summary>收银机向 Hbpos API 领取直连凭据 / 登记直连结账归属（POS API PR #896）。</summary>
public interface ISquareDirectApiClient
{
    /// <summary>领取直连凭据。门店开关没开抛 <see cref="SquareDirectApiException.IsDisabled"/> 为 true 的异常。</summary>
    Task<SquareDirectCredentialResponse> GetCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    /// <summary>直连创建结账后登记归属；服务端会向 Square 核实 reference_id 属于本设备。</summary>
    Task RegisterOriginAsync(
        CardTerminalEnvironment environment,
        string checkoutId,
        CancellationToken cancellationToken = default);
}

public sealed class SquareDirectApiClient(HttpClient httpClient) : ISquareDirectApiClient
{
    public const string DisabledErrorCode = "SQUARE_DIRECT_DISABLED";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SquareDirectCredentialResponse> GetCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        // 响应体含令牌：这里和调用方都不记录响应内容，失败路径只记错误码。
        return await PostAsync<SquareDirectCredentialResponse>(
            "api/v1/square/direct-credential",
            new SquareDirectCredentialRequest(environment.ToString()),
            "direct-credential",
            cancellationToken);
    }

    public async Task RegisterOriginAsync(
        CardTerminalEnvironment environment,
        string checkoutId,
        CancellationToken cancellationToken = default)
    {
        await PostAsync<SquareDirectOriginResponse>(
            $"api/v1/square/checkouts/{Uri.EscapeDataString(checkoutId)}/origin",
            new SquareDirectOriginRequest(environment.ToString()),
            "direct-origin",
            cancellationToken);
    }

    private async Task<T> PostAsync<T>(
        string relativeUrl,
        object body,
        string operation,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = System.Net.Http.Json.JsonContent.Create(body, options: JsonOptions)
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var content = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken);

        ApiResult<T>? result = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOptions);
            }
            catch (JsonException)
            {
                // 响应体不是预期 JSON（网关错误页等）：按下面的状态码分支处理，不把原文带进异常以免泄露令牌。
                result = null;
            }
        }

        if (!response.IsSuccessStatusCode || result is null || !result.Success || result.Data is null)
        {
            throw new SquareDirectApiException(
                response.StatusCode,
                result?.ErrorCode,
                $"Square {operation} request failed with HTTP {(int)response.StatusCode}"
                + (string.IsNullOrWhiteSpace(result?.ErrorCode) ? "." : $" ({result.ErrorCode})."));
        }

        return result.Data;
    }
}
