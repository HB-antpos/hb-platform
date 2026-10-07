using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Hbpos.Contracts.AppUpdates;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Wpf.Services;

public interface IAppUpdateApiClient
{
    Task<AppUpdateCheckResponse> CheckAsync(
        AppUpdateCheckRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class AppUpdateApiClient(
    HttpClient httpClient,
    IAppUpdateDeviceCredentialProvider credentialProvider) : IAppUpdateApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<AppUpdateCheckResponse> CheckAsync(
        AppUpdateCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        var currentVersion = Uri.EscapeDataString(request.CurrentVersion);
        var channel = Uri.EscapeDataString(string.IsNullOrWhiteSpace(request.Channel) ? "production" : request.Channel);
        var credentials = await credentialProvider.GetCredentialsAsync(cancellationToken);
        using var checkRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/app-update/check?currentVersion={currentVersion}&channel={channel}");
        AddDeviceCredentialHeaders(checkRequest, credentials);
        using var response = await httpClient.SendAsync(checkRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // 检查频率低（启动/每 30-60 分钟/人工），失败直接记 Warning；errorCode 保持不变，状态码写进消息与上下文。
            var statusCode = (int)response.StatusCode;
            ConsoleLog.WriteWarning(
                "AppUpdate",
                $"app update check http error status={statusCode} currentVersion={request.CurrentVersion} channel={channel}",
                new ApplicationLogContext(
                    RequestPath: "api/app-update/check",
                    RequestMethod: "GET",
                    StatusCode: statusCode,
                    Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["errorCode"] = "LOCAL_APP_UPDATE_HTTP_ERROR"
                    }));
            return AppUpdateCheckResponse.Failed(
                request.CurrentVersion,
                "LOCAL_APP_UPDATE_HTTP_ERROR",
                $"Local app update check returned an unsuccessful status (HTTP {statusCode}).");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return AppUpdateCheckResponse.Failed(
                request.CurrentVersion,
                "LOCAL_APP_UPDATE_EMPTY_RESPONSE",
                "Local app update check returned an empty response.");
        }

        var direct = JsonSerializer.Deserialize<AppUpdateCheckResponse>(json, JsonOptions);
        if (direct is not null &&
            (direct.CheckFailed || direct.UpdateAvailable || !string.IsNullOrWhiteSpace(direct.CurrentVersion)))
        {
            return NormalizeCheckResponse(direct, request.CurrentVersion);
        }

        var wrapped = JsonSerializer.Deserialize<ApiResult<AppUpdateCheckResponse>>(json, JsonOptions);
        if (wrapped?.Success == true && wrapped.Data is not null)
        {
            return NormalizeCheckResponse(wrapped.Data, request.CurrentVersion);
        }

        if (wrapped?.Success == false)
        {
            // 中文注释：POS API 已把中心错误包装成 ApiResult，客户端必须保留失败态给设置页展示。
            return AppUpdateCheckResponse.Failed(
                request.CurrentVersion,
                wrapped.ErrorCode,
                wrapped.Message);
        }

        return AppUpdateCheckResponse.Failed(
            request.CurrentVersion,
            "LOCAL_APP_UPDATE_INVALID_RESPONSE",
            "Local app update check returned an unsupported response.");
    }

    private static void AddDeviceCredentialHeaders(
        HttpRequestMessage request,
        AppUpdateDeviceCredentials? credentials)
    {
        if (credentials is null)
        {
            return;
        }

        // 中文注释：更新检查使用独立缓存凭据，不写入通用 DeviceAuthorizationState。
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AuthorizationCode);
        request.Headers.TryAddWithoutValidation(DeviceAuthConstants.DeviceCodeHeader, credentials.DeviceCode);
        request.Headers.TryAddWithoutValidation(DeviceAuthConstants.StoreCodeHeader, credentials.StoreCode);
        request.Headers.TryAddWithoutValidation(DeviceAuthConstants.HardwareIdHeader, credentials.HardwareId);
    }

    private static AppUpdateCheckResponse NormalizeCheckResponse(
        AppUpdateCheckResponse response,
        string currentVersion)
    {
        return response with
        {
            CurrentVersion = string.IsNullOrWhiteSpace(response.CurrentVersion)
                ? currentVersion
                : response.CurrentVersion,
            TargetVersion = string.IsNullOrWhiteSpace(response.TargetVersion)
                ? currentVersion
                : response.TargetVersion
        };
    }
}
