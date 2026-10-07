using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Stores;

namespace Hbpos.Client.Wpf.Services;

public interface IStoreReceiptProfileApiClient
{
    Task<StoreReceiptProfileDto> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按版本号轻量轮询总部下发的小票资料。服务端未部署该接口时会抛出 StatusCode=404 的
    /// <see cref="CatalogApiException"/>，由调用方决定退避；响应字段缺失时按「无变化、版本 0、无资料」容错。
    /// </summary>
    Task<StoreReceiptProfileSyncDto> GetSyncAsync(int knownVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// 回报「本设备已应用到版本 N」。门店与设备由认证头决定，不传任何分店/设备参数；
    /// 服务端不认该版本时返回 400（<see cref="CatalogApiException.StatusCode"/>）。
    /// </summary>
    Task<StoreReceiptProfileAckResultDto> AckAsync(int version, CancellationToken cancellationToken = default);
}

public sealed class StoreReceiptProfileApiClient(HttpClient httpClient) : IStoreReceiptProfileApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StoreReceiptProfileDto> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("api/v1/stores/current/receipt-profile", cancellationToken);
        var data = await ReadDataAsync<StoreReceiptProfileDto>(response, cancellationToken);

        return data ?? throw new CatalogApiException(
            "Store receipt profile API returned an empty profile.",
            response.StatusCode);
    }

    public async Task<StoreReceiptProfileSyncDto> GetSyncAsync(
        int knownVersion,
        CancellationToken cancellationToken = default)
    {
        // 负数没有意义，与服务端「缺省/非法/负数按 0」的口径一致。
        var known = Math.Max(0, knownVersion).ToString(CultureInfo.InvariantCulture);
        using var response = await httpClient.GetAsync(
            $"api/v1/stores/current/receipt-profile/sync?knownVersion={known}",
            cancellationToken);
        var data = await ReadDataAsync<StoreReceiptProfileSyncDto>(response, cancellationToken);

        // data 缺失按「没有变化」处理：调用方不会因此写入任何资料。
        return data ?? new StoreReceiptProfileSyncDto(false, 0, null);
    }

    public async Task<StoreReceiptProfileAckResultDto> AckAsync(
        int version,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new StoreReceiptProfileAckRequest(version), JsonOptions);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(
            "api/v1/stores/current/receipt-profile/ack",
            content,
            cancellationToken);
        var data = await ReadDataAsync<StoreReceiptProfileAckResultDto>(response, cancellationToken);

        // 成功但没有 data：服务端已接受回执，按请求的版本返回。
        return data ?? new StoreReceiptProfileAckResultDto(version);
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
                    "Store receipt profile API returned invalid JSON.",
                    response.StatusCode,
                    errorCode: null,
                    ex);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CatalogApiException(
                result?.Message ?? $"Store receipt profile API request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        if (result is null || !result.Success)
        {
            throw new CatalogApiException(
                result?.Message ?? "Store receipt profile API returned a failure response.",
                response.StatusCode,
                result?.ErrorCode);
        }

        return result.Data;
    }
}
