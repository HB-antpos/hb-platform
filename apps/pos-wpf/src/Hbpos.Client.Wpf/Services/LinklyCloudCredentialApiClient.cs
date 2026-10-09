using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Wpf.Services;

public interface ILinklyCloudCredentialApiClient
{
    Task<LinklyCloudCredentialUpsertResponse> UpsertCredentialAsync(
        CardTerminalEnvironment environment,
        string username,
        string password,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudBackendTerminalCredentialResponse> UpsertBackendTerminalCredentialAsync(
        CardTerminalEnvironment environment,
        string secret,
        string posId,
        CancellationToken cancellationToken = default);
}

public sealed class LinklyCloudCredentialApiClient(HttpClient httpClient) : ILinklyCloudCredentialApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<LinklyCloudCredentialUpsertResponse> UpsertCredentialAsync(
        CardTerminalEnvironment environment,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        Log(
            $"backend credential upsert start environment={environment} hasUsername={!string.IsNullOrWhiteSpace(username)} hasPassword=REDACTED");
        var stopwatch = Stopwatch.StartNew();
        // 请求体含用户名/密码，只记是否填写与耗时，正文不进日志。
        using var response = await SendWithFailureLogAsync(
            "backend credential upsert",
            environment,
            stopwatch,
            () => httpClient.PutAsJsonAsync(
                "api/v1/linkly/cloud-credential",
                new LinklyCloudCredentialUpsertRequest(environment.ToString(), username, password),
                JsonOptions,
                cancellationToken),
            cancellationToken);
        var result = await ReadApiResultAsync<LinklyCloudCredentialUpsertResponse>(response, stopwatch, cancellationToken);
        var payload = EnsureSuccess(
            result,
            response.StatusCode,
            "Linkly credential API save",
            $"environment={environment}");
        Log(
            $"backend credential upsert succeeded environment={environment} store={LogValue(payload.StoreCode)} updatedAt={payload.UpdatedAt:O}");
        return payload;
    }

    public async Task<LinklyCloudBackendTerminalCredentialResponse> UpsertBackendTerminalCredentialAsync(
        CardTerminalEnvironment environment,
        string secret,
        string posId,
        CancellationToken cancellationToken = default)
    {
        Log(
            $"backend terminal credential upsert start environment={environment} hasSecret={!string.IsNullOrWhiteSpace(secret)} posId={LogValue(posId)}");
        var stopwatch = Stopwatch.StartNew();
        using var response = await SendWithFailureLogAsync(
            "backend terminal credential upsert",
            environment,
            stopwatch,
            () => httpClient.PutAsJsonAsync(
                "api/v1/linkly/cloud-backend/terminal",
                new LinklyCloudBackendTerminalCredentialUpsertRequest(environment.ToString(), secret, posId),
                JsonOptions,
                cancellationToken),
            cancellationToken);
        var result = await ReadApiResultAsync<LinklyCloudBackendTerminalCredentialResponse>(response, stopwatch, cancellationToken);
        var payload = EnsureSuccess(
            result,
            response.StatusCode,
            "Linkly backend terminal credential API save",
            $"environment={environment} posId={LogValue(posId)}");
        Log(
            $"backend terminal credential upsert succeeded environment={environment} store={LogValue(payload.StoreCode)} device={LogValue(payload.DeviceCode)} posId={LogValue(payload.PosId)} updatedAt={payload.UpdatedAt:O}");
        return payload;
    }

    /// <summary>
    /// 发送阶段断网/超时原先没有任何日志；记下操作、环境、耗时与异常后原样抛出（调用方取消只记 Information）。
    /// </summary>
    private static async Task<HttpResponseMessage> SendWithFailureLogAsync(
        string operation,
        CardTerminalEnvironment environment,
        Stopwatch stopwatch,
        Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        try
        {
            return await send();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            var callerCancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            LinklyJsonLog.Write(
                "LinklyCloud",
                "cloud-credential-api",
                operation,
                callerCancelled ? "cancelled" : "failed",
                environment: environment,
                success: false,
                reason: ex is HttpRequestException ? "network-error" : callerCancelled ? "cancelled" : "timeout",
                elapsedMs: stopwatch.ElapsedMilliseconds,
                exception: ex,
                level: callerCancelled ? LinklyLogLevel.Information : null);
            throw;
        }
    }

    private static async Task<ApiResult<T>?> ReadApiResultAsync<T>(
        HttpResponseMessage response,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        Log($"backend credential response http={(int)response.StatusCode} elapsedMs={stopwatch.ElapsedMilliseconds}");
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOptions);
        }
        catch (JsonException ex)
        {
            Log($"backend credential response invalid-json http={(int)response.StatusCode}");
            throw new CatalogApiException(
                "Linkly credential API returned invalid JSON.",
                response.StatusCode,
                errorCode: null,
                ex);
        }
    }

    private static T EnsureSuccess<T>(
        ApiResult<T>? result,
        System.Net.HttpStatusCode statusCode,
        string operationName,
        string logContext)
    {
        if (result is null)
        {
            Log($"{operationName} failed {logContext} http={(int)statusCode} reason=empty-response");
            throw new CatalogApiException($"{operationName} returned an empty response.", statusCode);
        }

        if ((int)statusCode < 200 || (int)statusCode >= 300)
        {
            Log($"{operationName} failed {logContext} http={(int)statusCode} errorCode={LogValue(result.ErrorCode)}");
            throw new CatalogApiException(
                $"{operationName} request failed with HTTP {(int)statusCode}.",
                statusCode,
                result.ErrorCode);
        }

        if (!result.Success || result.Data is null)
        {
            Log($"{operationName} failed {logContext} http={(int)statusCode} errorCode={LogValue(result.ErrorCode)}");
            throw new CatalogApiException(
                $"{operationName} returned a failure response.",
                statusCode,
                result.ErrorCode);
        }

        return result.Data;
    }

    private static void Log(string message)
    {
        LinklyJsonLog.WriteMessage("LinklyCloud", "cloud-credential-api", message);
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }
}
