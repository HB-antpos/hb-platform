using System.Net.Http;
using System.Text;
using System.Text.Json;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.PosSettings;

namespace Hbpos.Client.Wpf.Services;

public interface IPosSettingsApiClient
{
    /// <summary>
    /// 带本机已应用的指纹拉取总部下发的设置：指纹没变时服务端只回 <c>Changed=false</c>。
    /// 服务端未部署该接口时抛出 StatusCode=404 的 <see cref="CatalogApiException"/>，由调用方退避。
    /// </summary>
    Task<PosSettingsSyncDto> GetSyncAsync(string? knownHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// 回执：本机已应用的指纹、各开关此刻的实际值、累计离线临时修改次数。门店与设备由认证头决定，不传任何门店 / 设备参数。
    /// 服务端不认这份回执时返回 400（<see cref="CatalogApiException.StatusCode"/>）。
    /// </summary>
    Task<PosSettingsAckResultDto> AckAsync(PosSettingsAckRequest request, CancellationToken cancellationToken = default);
}

/// <summary>收银端设置下发接口（路径与 Hbpos.Api 的 PosSettingsController 一致）。</summary>
public sealed class PosSettingsApiClient(HttpClient httpClient) : IPosSettingsApiClient
{
    internal const string SyncPath = "api/v1/stores/current/pos-settings/sync";
    internal const string AckPath = "api/v1/stores/current/pos-settings/ack";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PosSettingsSyncDto> GetSyncAsync(string? knownHash, CancellationToken cancellationToken = default)
    {
        // 指纹是十六进制文本，不含需要转义的字符；仍做一次转义，避免将来格式变化时拼出坏 URL。
        var query = string.IsNullOrWhiteSpace(knownHash)
            ? string.Empty
            : $"?knownHash={Uri.EscapeDataString(knownHash.Trim())}";
        using var response = await httpClient.GetAsync(SyncPath + query, cancellationToken);
        var data = await ReadDataAsync<PosSettingsSyncDto>(response, cancellationToken);

        // data 缺失按「没有变化、无指纹」处理：调用方看到空指纹不会写入任何设置。
        return data ?? new PosSettingsSyncDto(false, string.Empty, null);
    }

    public async Task<PosSettingsAckResultDto> AckAsync(
        PosSettingsAckRequest request,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(AckPath, content, cancellationToken);
        var data = await ReadDataAsync<PosSettingsAckResultDto>(response, cancellationToken);

        // 成功但没有 data：服务端已接受回执，按请求的指纹返回。
        return data ?? new PosSettingsAckResultDto(request.Hash);
    }

    private static async Task<T?> ReadDataAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<T>? result = null;

        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new CatalogApiException(
                    "POS settings API returned invalid JSON.",
                    response.StatusCode,
                    errorCode: null,
                    ex);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CatalogApiException(
                result?.Message ?? $"POS settings API request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        if (result is null || !result.Success)
        {
            throw new CatalogApiException(
                result?.Message ?? "POS settings API returned a failure response.",
                response.StatusCode,
                result?.ErrorCode);
        }

        return result.Data;
    }
}
