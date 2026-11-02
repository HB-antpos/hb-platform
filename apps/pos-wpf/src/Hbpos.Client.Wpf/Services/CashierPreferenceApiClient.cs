using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Common;

namespace Hbpos.Client.Wpf.Services;

public enum CashierColorThemeFetchStatus
{
    /// <summary>服务端有该员工保存的配色。</summary>
    Found,

    /// <summary>服务端确认该员工从未选过配色：只有这种情况才弹首次选择窗。</summary>
    NotSet,

    /// <summary>旧版服务端没有该接口、偏好表未迁移、网络失败或应答不属于本次请求的员工：一律沿用本机主题。</summary>
    Unavailable
}

public sealed record CashierColorThemeFetchResult(CashierColorThemeFetchStatus Status, PosColorTheme? Theme = null)
{
    public static CashierColorThemeFetchResult Unavailable { get; } = new(CashierColorThemeFetchStatus.Unavailable);
}

public interface ICashierPreferenceApiClient
{
    /// <summary>读取当前收银员票据对应员工的配色；应答里的员工与 <paramref name="expectedUserGuid"/> 不一致时按不可用处理。</summary>
    Task<CashierColorThemeFetchResult> GetColorThemeAsync(string expectedUserGuid, CancellationToken cancellationToken = default);

    /// <summary>保存配色；返回服务端是否确认写入。失败不抛异常，由调用方保留本地待上传标记。</summary>
    Task<bool> SaveColorThemeAsync(string userGuid, PosColorTheme theme, CancellationToken cancellationToken = default);
}

/// <summary>
/// 收银员配色偏好接口。收银员票据由 DeviceAuthorizationMessageHandler 按当前会话附加，
/// 所以读取时核对应答员工、保存时把员工放进请求体，由服务端拒绝切换收银员后迟到的旧请求。
/// </summary>
public sealed class CashierPreferenceApiClient(HttpClient httpClient) : ICashierPreferenceApiClient
{
    internal const string ColorThemePath = "api/v1/cashiers/me/preferences/color-theme";
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CashierColorThemeFetchResult> GetColorThemeAsync(
        string expectedUserGuid,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(ColorThemePath, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                ConsoleLog.Write("CashierPreference", $"color theme fetch skipped status={(int)response.StatusCode}");
                return CashierColorThemeFetchResult.Unavailable;
            }

            var preference = await ReadPreferenceAsync(response, cancellationToken);
            if (preference is null ||
                !string.Equals(preference.UserGuid, expectedUserGuid, StringComparison.OrdinalIgnoreCase))
            {
                return CashierColorThemeFetchResult.Unavailable;
            }

            // 服务端只会返回固定白名单里的值或 null；客户端仍按枚举再校验一次，认不出的值等同未设置。
            return ColorThemeService.TryParse(preference.ColorTheme, out var theme)
                ? new CashierColorThemeFetchResult(CashierColorThemeFetchStatus.Found, theme)
                : new CashierColorThemeFetchResult(CashierColorThemeFetchStatus.NotSet);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsoleLog.Write("CashierPreference", $"color theme fetch failed error={ex.GetType().Name}");
            return CashierColorThemeFetchResult.Unavailable;
        }
    }

    public async Task<bool> SaveColorThemeAsync(
        string userGuid,
        PosColorTheme theme,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PutAsJsonAsync(
                ColorThemePath,
                new CashierColorThemePreferenceUpdateRequest(userGuid, theme.ToString()),
                JsonOptions,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                ConsoleLog.Write("CashierPreference", $"color theme save rejected status={(int)response.StatusCode}");
                return false;
            }

            var preference = await ReadPreferenceAsync(response, cancellationToken);
            return preference is not null &&
                string.Equals(preference.UserGuid, userGuid, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsoleLog.Write("CashierPreference", $"color theme save failed error={ex.GetType().Name}");
            return false;
        }
    }

    // 2xx 只有带 ApiResult 信封且 success=true 才算服务端结论（Wi-Fi 认证页等非 POS API 应答按不可用处理）。
    private static async Task<CashierColorThemePreferenceDto?> ReadPreferenceAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(content);
        if (!DeviceApiClient.IsApiResultEnvelope(document.RootElement))
        {
            return null;
        }

        var result = document.RootElement.Deserialize<ApiResult<CashierColorThemePreferenceDto>>(JsonOptions);
        return result is { Success: true, Data: not null } ? result.Data : null;
    }
}
