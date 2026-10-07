using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Square;

namespace Hbpos.Client.Wpf.Services;

public interface ISquareTokenApiClient
{
    Task<SquareTokenStatusResponse> GetStatusAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);
}

public sealed class SquareTokenApiClient(HttpClient httpClient) : ISquareTokenApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SquareTokenStatusResponse> GetStatusAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        LogSquareToken($"token status request start environment={environment}");
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage sentResponse;
        try
        {
            sentResponse = await httpClient.GetAsync(
                $"api/v1/square/token?environment={Uri.EscapeDataString(environment.ToString())}",
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException ||
            (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // 断网或 HttpClient 自身超时（调用方取消不记）。
            LogSquareTokenFailure(
                $"token status request failed environment={environment} reason={(ex is HttpRequestException ? "network-error" : "timeout")} elapsedMs={stopwatch.ElapsedMilliseconds}",
                statusCode: null,
                errorCode: null,
                stopwatch.ElapsedMilliseconds,
                ex);
            throw;
        }

        using var response = sentResponse;
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();
        var elapsedMs = stopwatch.ElapsedMilliseconds;
        ApiResult<SquareTokenStatusResponse>? result = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<SquareTokenStatusResponse>>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                LogSquareTokenFailure(
                    $"token status request invalid json environment={environment} http={(int)response.StatusCode} elapsedMs={elapsedMs}",
                    (int)response.StatusCode,
                    errorCode: null,
                    elapsedMs,
                    ex);
                throw new CatalogApiException(
                    "Square token status API returned invalid JSON.",
                    response.StatusCode,
                    errorCode: null,
                    ex);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            LogSquareTokenFailure(
                $"token status request failed environment={environment} http={(int)response.StatusCode} errorCode={LogValue(result?.ErrorCode)} elapsedMs={elapsedMs}",
                (int)response.StatusCode,
                result?.ErrorCode,
                elapsedMs);
            throw new CatalogApiException(
                $"Square token status API request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        if (result is null)
        {
            LogSquareTokenFailure(
                $"token status request failed environment={environment} reason=empty-response elapsedMs={elapsedMs}",
                (int)response.StatusCode,
                errorCode: null,
                elapsedMs);
            throw new CatalogApiException("Square token status API returned an empty response.", response.StatusCode);
        }

        if (!result.Success)
        {
            LogSquareTokenFailure(
                $"token status request failed environment={environment} reason=api-failure errorCode={LogValue(result.ErrorCode)} elapsedMs={elapsedMs}",
                (int)response.StatusCode,
                result.ErrorCode,
                elapsedMs);
            throw new CatalogApiException(
                "Square token status API returned a failure response.",
                response.StatusCode,
                result.ErrorCode);
        }

        if (result.Data is null)
        {
            LogSquareTokenFailure(
                $"token status request failed environment={environment} reason=missing-status errorCode={LogValue(result.ErrorCode)} elapsedMs={elapsedMs}",
                (int)response.StatusCode,
                result.ErrorCode,
                elapsedMs);
            throw new CatalogApiException(
                "Square token status API returned no status.",
                response.StatusCode,
                result.ErrorCode);
        }

        LogSquareToken(
            $"token status request succeeded environment={environment} configured={result.Data.Configured} enabled={result.Data.Enabled} elapsedMs={elapsedMs}");
        return result.Data;
    }

    private static void LogSquareToken(string message)
    {
        ConsoleLog.Write("Square", message);
    }

    /// <summary>
    /// token 状态查询失败记 Warning；"后端未配置 token"属于预期业务状态，只记 Information。
    /// </summary>
    private static void LogSquareTokenFailure(
        string message,
        int? statusCode,
        string? errorCode,
        long elapsedMs,
        Exception? exception = null)
    {
        var context = new ApplicationLogContext(
            RequestPath: "api/v1/square/token",
            RequestMethod: "GET",
            StatusCode: statusCode,
            Properties: new Dictionary<string, object?>
            {
                ["errorCode"] = errorCode,
                ["elapsedMs"] = elapsedMs
            });
        if (string.Equals(errorCode, "SQUARE_TOKEN_NOT_CONFIGURED", StringComparison.OrdinalIgnoreCase))
        {
            ConsoleLog.WriteInformation("Square", message, context);
            return;
        }

        ConsoleLog.WriteWarning("Square", message, context, exception);
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value;
    }
}
