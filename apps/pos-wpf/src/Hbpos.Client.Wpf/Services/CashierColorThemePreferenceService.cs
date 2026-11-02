using Hbpos.Contracts.Cashiers;

namespace Hbpos.Client.Wpf.Services;

public enum CashierColorThemeOutcome
{
    /// <summary>已切到该员工自己的配色（服务端或本机缓存）。</summary>
    Applied,

    /// <summary>服务端确认该员工从未选过配色，本机也没有缓存：弹首次选择窗。</summary>
    NeedsChoice,

    /// <summary>无从得知员工偏好（离线无缓存、紧急授权、服务端不可用、已换人）：保持本机上次的配色，不弹窗。</summary>
    KeptCurrent
}

/// <summary>
/// 按收银员决定界面配色：员工偏好 → 本机上次使用的配色 → 默认蓝色（后两级由 <see cref="IColorThemeService"/> 的本机偏好兜底）。
/// 员工偏好以服务端为准，本机按员工缓存一份供离线登录使用；离线或上传失败时的选择记为待上传，下次在线登录优先补传。
/// 每次真正切换都经 <see cref="IColorThemeService.SelectAsync"/>，所以本机「上次使用的配色」始终是最近一位收银员的选择。
/// </summary>
public sealed class CashierColorThemePreferenceService(
    IColorThemeService colorThemes,
    ILocalAppSettingsRepository settings,
    ICashierSessionContext cashierSessions,
    ICashierPreferenceApiClient? preferenceApi = null)
{
    private const string UserSettingKeyPrefix = "Shell:ColorTheme:User:";
    private const string PendingUploadSuffix = ":PendingUpload";

    internal static string UserSettingKey(string userGuid) =>
        UserSettingKeyPrefix + userGuid.Trim().ToLowerInvariant();

    internal static string PendingUploadKey(string userGuid) => UserSettingKey(userGuid) + PendingUploadSuffix;

    /// <summary>收银员登录成功后调用：切到该员工的配色，或告知需要弹首次选择窗。</summary>
    public async Task<CashierColorThemeOutcome> ApplyForCashierAsync(
        CashierSessionDto session,
        CancellationToken cancellationToken = default)
    {
        // 紧急授权没有员工票据，也不代表真实员工：不读写任何员工偏好。
        if (!CanHoldPreference(session))
        {
            return CashierColorThemeOutcome.KeptCurrent;
        }

        var local = await ReadLocalAsync(session.UserGuid, cancellationToken);
        var canReachServer = preferenceApi is not null &&
            !session.IsOfflineCached &&
            !string.IsNullOrWhiteSpace(session.AuthorizationToken);
        if (!canReachServer)
        {
            return await ApplyIfStillCurrentAsync(session, local?.Theme, cancellationToken);
        }

        // 离线时改过的配色比服务端旧值新：先补传，成功与否都以本机这份为准。
        if (local is { PendingUpload: true })
        {
            if (await preferenceApi!.SaveColorThemeAsync(session.UserGuid, local.Value.Theme, cancellationToken))
            {
                await ClearPendingUploadAsync(session.UserGuid, cancellationToken);
            }

            return await ApplyIfStillCurrentAsync(session, local.Value.Theme, cancellationToken);
        }

        var remote = await preferenceApi!.GetColorThemeAsync(session.UserGuid, cancellationToken);
        switch (remote.Status)
        {
            case CashierColorThemeFetchStatus.Found when remote.Theme is { } theme:
                await WriteLocalAsync(session.UserGuid, theme, pendingUpload: false, cancellationToken);
                return await ApplyIfStillCurrentAsync(session, theme, cancellationToken);

            case CashierColorThemeFetchStatus.NotSet when local is { } cached:
                // 本机有缓存而服务端没有（例如偏好表刚迁移）：沿用缓存并补传一次。
                await preferenceApi.SaveColorThemeAsync(session.UserGuid, cached.Theme, cancellationToken);
                return await ApplyIfStillCurrentAsync(session, cached.Theme, cancellationToken);

            case CashierColorThemeFetchStatus.NotSet:
                return IsStillCurrent(session) ? CashierColorThemeOutcome.NeedsChoice : CashierColorThemeOutcome.KeptCurrent;

            default:
                return await ApplyIfStillCurrentAsync(session, local?.Theme, cancellationToken);
        }
    }

    /// <summary>首次选择窗确认：切换并保存为本机配色与该员工的偏好。</summary>
    public async Task SaveChoiceAsync(
        CashierSessionDto session,
        PosColorTheme theme,
        CancellationToken cancellationToken = default)
    {
        await colorThemes.SelectAsync(theme, cancellationToken);
        if (CanHoldPreference(session))
        {
            await SaveForUserAsync(session, theme, cancellationToken);
        }
    }

    /// <summary>标题栏配色菜单：切换并保存为本机配色；有收银员登录时同时记为该员工的偏好。</summary>
    public async Task SelectForCurrentCashierAsync(PosColorTheme theme, CancellationToken cancellationToken = default)
    {
        await colorThemes.SelectAsync(theme, cancellationToken);
        var session = cashierSessions.CurrentSession;
        if (session is not null && CanHoldPreference(session))
        {
            await SaveForUserAsync(session, theme, cancellationToken);
        }
    }

    private async Task SaveForUserAsync(CashierSessionDto session, PosColorTheme theme, CancellationToken cancellationToken)
    {
        // 先落本机并标记待上传；服务端确认后才清除标记，保证离线或失败时下次在线登录还能补传。
        await WriteLocalAsync(session.UserGuid, theme, pendingUpload: true, cancellationToken);
        var canReachServer = preferenceApi is not null &&
            !session.IsOfflineCached &&
            !string.IsNullOrWhiteSpace(session.AuthorizationToken);
        if (canReachServer && await preferenceApi!.SaveColorThemeAsync(session.UserGuid, theme, cancellationToken))
        {
            await ClearPendingUploadAsync(session.UserGuid, cancellationToken);
        }
    }

    private async Task<CashierColorThemeOutcome> ApplyIfStillCurrentAsync(
        CashierSessionDto session,
        PosColorTheme? theme,
        CancellationToken cancellationToken)
    {
        // 查询期间可能已换人或登出：旧员工的配色不能盖到新会话上。
        if (theme is null || !IsStillCurrent(session))
        {
            return CashierColorThemeOutcome.KeptCurrent;
        }

        await colorThemes.SelectAsync(theme.Value, cancellationToken);
        return CashierColorThemeOutcome.Applied;
    }

    private bool IsStillCurrent(CashierSessionDto session) =>
        string.Equals(cashierSessions.CurrentSession?.UserGuid, session.UserGuid, StringComparison.OrdinalIgnoreCase);

    private static bool CanHoldPreference(CashierSessionDto session) =>
        !session.IsEmergencyOverride && !string.IsNullOrWhiteSpace(session.UserGuid);

    private async Task<(PosColorTheme Theme, bool PendingUpload)?> ReadLocalAsync(
        string userGuid,
        CancellationToken cancellationToken)
    {
        try
        {
            var values = await settings.GetValuesAsync([UserSettingKey(userGuid), PendingUploadKey(userGuid)], cancellationToken);
            if (!values.TryGetValue(UserSettingKey(userGuid), out var stored) ||
                !ColorThemeService.TryParse(stored, out var theme))
            {
                return null;
            }

            return (theme, values.TryGetValue(PendingUploadKey(userGuid), out var pending) && pending == "1");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteError("ColorTheme", $"cashier preference cache read failed error={ex.GetType().Name}", exception: ex);
            return null;
        }
    }

    private async Task WriteLocalAsync(
        string userGuid,
        PosColorTheme theme,
        bool pendingUpload,
        CancellationToken cancellationToken)
    {
        try
        {
            await settings.SetValuesAsync(
                new Dictionary<string, string>
                {
                    [UserSettingKey(userGuid)] = theme.ToString(),
                    [PendingUploadKey(userGuid)] = pendingUpload ? "1" : "0"
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 本机缓存只影响离线登录时的配色，写失败不阻断收银。
            ConsoleLog.WriteError("ColorTheme", $"cashier preference cache write failed error={ex.GetType().Name}", exception: ex);
        }
    }

    private async Task ClearPendingUploadAsync(string userGuid, CancellationToken cancellationToken)
    {
        try
        {
            await settings.SetValueAsync(PendingUploadKey(userGuid), "0", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteError("ColorTheme", $"cashier preference cache write failed error={ex.GetType().Name}", exception: ex);
        }
    }
}
