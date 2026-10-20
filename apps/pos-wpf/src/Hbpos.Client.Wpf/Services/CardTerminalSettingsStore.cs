using System.Net.Http;
using System.Text.Json;

namespace Hbpos.Client.Wpf.Services;

public sealed class CardTerminalSettingsStore(
    ILocalAppSettingsRepository settingsRepository,
    IDeviceAuthorizationProtector protector,
    ISquareTokenApiClient? squareTokenApiClient = null) : ICardTerminalSettingsStore
{
    private static readonly SemaphoreSlim LinklyCloudPosIdGate = new(1, 1);

    private const string ProcessorKey = "CardTerminal:Processor";
    private const string EnvironmentKey = "CardTerminal:Environment";
    private const string LinklyHostKey = "CardTerminal:LinklyHost";
    private const string LinklyPortKey = "CardTerminal:LinklyPort";
    private const string LinklyConnectionModeKey = "CardTerminal:LinklyConnectionMode";
    private const string LinklyConnectionModePriorityKey = "CardTerminal:LinklyConnectionModePriority";
    private const string LinklyCloudSecretKeyPrefix = "CardTerminal:LinklyCloudSecretProtected:";
    private const string LinklyCloudUsernameKeyPrefix = "CardTerminal:LinklyCloudUsername:";
    private const string LinklyCloudPasswordKeyPrefix = "CardTerminal:LinklyCloudPasswordProtected:";
    private const string LinklyCloudCredentialSnapshotKeyPrefix = "CardTerminal:LinklyCloudCredentialSnapshot:";
    private const string LinklyCloudPosIdKeyPrefix = "CardTerminal:LinklyCloudPosId:";
    private const string LegacySquareTokenKey = "CardTerminal:SquareAccessTokenProtected";
    private const string SquareTokenKeyPrefix = "CardTerminal:SquareAccessTokenProtected:";
    private const string SquareLocationIdKey = "CardTerminal:SquareLocationId";
    private const string SquareDeviceIdKey = "CardTerminal:SquareDeviceId";
    private const string TimeoutSecondsKey = "CardTerminal:TimeoutSeconds";

    public Task<CardTerminalConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        return LoadConfigurationAsync(includeSquareTokenStatus: true, cancellationToken);
    }

    // 付款页只要处理器 / 环境 / Linkly 模式，不需要「后端是否已配置令牌」，省掉一次到新加坡的往返（约 0.9 秒）。
    public Task<CardTerminalConfiguration> LoadWithoutTokenStatusAsync(CancellationToken cancellationToken = default)
    {
        return LoadConfigurationAsync(includeSquareTokenStatus: false, cancellationToken);
    }

    private async Task<CardTerminalConfiguration> LoadConfigurationAsync(
        bool includeSquareTokenStatus,
        CancellationToken cancellationToken)
    {
        var environmentSettings = CardTerminalSettings.FromEnvironment();

        var processor = ParseProcessor(
            await settingsRepository.GetValueAsync(ProcessorKey, cancellationToken),
            environmentSettings.Processor);
        var terminalEnvironment = ParseEnvironment(
            await settingsRepository.GetValueAsync(EnvironmentKey, cancellationToken),
            environmentSettings.Environment);
        var linklyHost = NormalizeText(
            await settingsRepository.GetValueAsync(LinklyHostKey, cancellationToken),
            environmentSettings.LinklyHost);
        var linklyPort = ParsePort(
            await settingsRepository.GetValueAsync(LinklyPortKey, cancellationToken),
            environmentSettings.LinklyPort);
        var linklyConnectionMode = ParseLinklyConnectionMode(
            await settingsRepository.GetValueAsync(LinklyConnectionModeKey, cancellationToken),
            environmentSettings.LinklyConnectionMode);
        var linklyConnectionModePriority = CardTerminalSettings.ParseLinklyConnectionModePriority(
            await settingsRepository.GetValueAsync(LinklyConnectionModePriorityKey, cancellationToken),
            linklyConnectionMode);
        linklyConnectionMode = linklyConnectionModePriority[0];
        var squareLocationId = NormalizeText(
            await settingsRepository.GetValueAsync(SquareLocationIdKey, cancellationToken),
            environmentSettings.SquareLocationId);
        var squareDeviceId = NormalizeText(
            await settingsRepository.GetValueAsync(SquareDeviceIdKey, cancellationToken),
            environmentSettings.SquareDeviceId);
        var timeoutSeconds = ParseTimeoutSeconds(
            await settingsRepository.GetValueAsync(TimeoutSecondsKey, cancellationToken),
            (int)Math.Max(1, environmentSettings.TerminalTimeout.TotalSeconds));
        var protectedToken = await ReadProtectedSquareAccessTokenAsync(terminalEnvironment, cancellationToken);
        var protectedLinklySecret = await ReadProtectedLinklyCloudSecretAsync(terminalEnvironment, cancellationToken);
        var hasLegacyProtectedToken = !string.IsNullOrWhiteSpace(protectedToken);
        var hasSquareTokenConfigured = includeSquareTokenStatus
            ? await ResolveSquareTokenConfiguredAsync(
                processor,
                terminalEnvironment,
                hasLegacyProtectedToken,
                cancellationToken)
            : hasLegacyProtectedToken;

        return new CardTerminalConfiguration(
            processor,
            terminalEnvironment,
            linklyHost,
            linklyPort,
            squareLocationId,
            squareDeviceId,
            hasSquareTokenConfigured,
            timeoutSeconds,
            linklyConnectionMode,
            !string.IsNullOrWhiteSpace(protectedLinklySecret),
            linklyConnectionModePriority);
    }

    public async Task SaveAsync(
        CardTerminalConfiguration configuration,
        string? squareAccessToken,
        CancellationToken cancellationToken = default)
    {
        var linklyPriority = configuration.LinklyConnectionModePriority is null ||
            configuration.LinklyConnectionModePriority.Count == 0
            ? CardTerminalSettings.NormalizeLinklyConnectionModePriority(null, configuration.LinklyConnectionMode)
            : CardTerminalSettings.NormalizeLinklyConnectionModePriority(
                configuration.LinklyConnectionModePriority,
                configuration.LinklyConnectionModePriority[0]);
        var primaryLinklyMode = linklyPriority[0];
        await settingsRepository.SetValuesAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProcessorKey] = configuration.Processor.ToString(),
                [EnvironmentKey] = configuration.Environment.ToString(),
                [LinklyHostKey] = NormalizeText(configuration.LinklyHost, CardTerminalConfiguration.Default.LinklyHost),
                [LinklyPortKey] = NormalizePort(configuration.LinklyPort).ToString(),
                [LinklyConnectionModeKey] = CardTerminalSettings.FormatLinklyConnectionMode(primaryLinklyMode),
                [LinklyConnectionModePriorityKey] = CardTerminalSettings.FormatLinklyConnectionModePriority(linklyPriority),
                [SquareLocationIdKey] = configuration.SquareLocationId?.Trim() ?? string.Empty,
                [SquareDeviceIdKey] = configuration.SquareDeviceId?.Trim() ?? string.Empty,
                [TimeoutSecondsKey] = NormalizeTimeoutSeconds(configuration.TerminalTimeoutSeconds).ToString(),
            },
            cancellationToken);

        // Square token 已移到 Hbpos API；保存设置时不再把新 access token 写入本机缓存。
        _ = squareAccessToken;
    }

    public Task<string?> GetSquareAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // 兼容旧接口签名，但运行时不再向 WPF 暴露 Square bearer token。
        _ = cancellationToken;
        return Task.FromResult<string?>(null);
    }

    public Task<string?> GetTokenAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        return GetSquareAccessTokenAsync(environment, forceRefresh: false, cancellationToken);
    }

    public Task<string?> RefreshTokenAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        return GetSquareAccessTokenAsync(environment, forceRefresh: true, cancellationToken);
    }

    public Task<string?> GetSquareAccessTokenAsync(
        CardTerminalEnvironment environment,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        _ = forceRefresh;
        _ = environment;
        _ = cancellationToken;
        // 兼容旧接口签名，但运行时不再向 WPF 暴露 Square bearer token。
        return Task.FromResult<string?>(null);
    }

    public async Task<string?> GetLinklyCloudSecretAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        var protectedSecret = await ReadProtectedLinklyCloudSecretAsync(environment, cancellationToken);
        return string.IsNullOrWhiteSpace(protectedSecret)
            ? null
            : protector.Unprotect(protectedSecret);
    }

    public async Task SaveLinklyCloudSecretAsync(
        CardTerminalEnvironment environment,
        string secret,
        CancellationToken cancellationToken = default)
    {
        var protectedSecret = protector.Protect(secret.Trim());
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            throw new InvalidOperationException("Linkly Cloud secret could not be protected.");
        }

        await settingsRepository.SetValueAsync(
            GetLinklyCloudSecretKey(environment),
            protectedSecret,
            cancellationToken);
        LogLinklyCloud($"protected secret saved environment={environment}");
    }

    public async Task<string> GetOrCreateLinklyCloudPosIdAsync(
        CardTerminalEnvironment environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken = default)
    {
        await LinklyCloudPosIdGate.WaitAsync(cancellationToken);
        try
        {
            // 进入创建边界后使用不可取消写入，避免 SQLite 已提交但调用方收到取消并生成第二个身份。
            var key = GetLinklyCloudPosIdKey(environment, storeCode, deviceCode);
            var existing = await settingsRepository.GetValueAsync(key, CancellationToken.None);
            if (IsUuidV4(existing))
            {
                LogLinklyCloud($"posId reused environment={environment} store={LogValue(storeCode)} device={LogValue(deviceCode)} posId={ShortId(existing)}");
                return existing!.Trim();
            }

            // 仅生产环境兼容旧版无环境 key，读取成功后立即写入新 key，避免沙箱误用生产 POS ID。
            if (environment == CardTerminalEnvironment.Production)
            {
                var legacyKey = GetLegacyLinklyCloudPosIdKey(storeCode, deviceCode);
                var legacy = await settingsRepository.GetValueAsync(legacyKey, CancellationToken.None);
                if (IsUuidV4(legacy))
                {
                    var migrated = legacy!.Trim();
                    await settingsRepository.SetValueAsync(key, migrated, CancellationToken.None);
                    LogLinklyCloud($"posId migrated environment={environment} store={LogValue(storeCode)} device={LogValue(deviceCode)} posId={ShortId(migrated)}");
                    return migrated;
                }
            }

            var posId = Guid.NewGuid().ToString("D");
            await settingsRepository.SetValueAsync(key, posId, CancellationToken.None);
            LogLinklyCloud($"posId generated environment={environment} store={LogValue(storeCode)} device={LogValue(deviceCode)} posId={ShortId(posId)} replacedInvalid={!string.IsNullOrWhiteSpace(existing)}");
            return posId;
        }
        finally
        {
            LinklyCloudPosIdGate.Release();
        }
    }

    public async Task<LinklyCloudCredentialSettings> GetLinklyCloudCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        var snapshotEntry = await settingsRepository.GetEntryAsync(
            GetLinklyCloudCredentialSnapshotKey(environment),
            cancellationToken);
        var usernameEntry = await settingsRepository.GetEntryAsync(
            GetLinklyCloudUsernameKey(environment),
            cancellationToken);
        var passwordEntry = await settingsRepository.GetEntryAsync(
            GetLinklyCloudPasswordKey(environment),
            cancellationToken);
        var legacyUsername = NormalizeOptional(usernameEntry?.Value);
        var legacyProtectedPassword = passwordEntry?.Value;
        var hasCompleteLegacyCredential =
            !string.IsNullOrWhiteSpace(legacyUsername) &&
            !string.IsNullOrWhiteSpace(legacyProtectedPassword);

        if (TryParseLinklyCloudCredentialSnapshot(
                snapshotEntry?.Value,
                out var snapshotUsername,
                out var snapshotPassword))
        {
            // 旧版本只会依次更新两个拆分 key。只有两项都严格晚于快照，才视为一次完整的回滚版本保存；
            // 单项较新代表可能只写入了一半，必须继续使用原子快照，避免拼出混代凭据。
            if (hasCompleteLegacyCredential &&
                IsStrictlyNewer(usernameEntry, snapshotEntry) &&
                IsStrictlyNewer(passwordEntry, snapshotEntry))
            {
                return new LinklyCloudCredentialSettings(
                    legacyUsername,
                    protector.Unprotect(legacyProtectedPassword),
                    true);
            }

            return new LinklyCloudCredentialSettings(
                snapshotUsername,
                protector.Unprotect(snapshotPassword),
                true);
        }

        // 兼容升级前分别保存的两个 key；快照缺失或损坏时维持原有回退行为。
        var password = string.IsNullOrWhiteSpace(legacyProtectedPassword)
            ? null
            : protector.Unprotect(legacyProtectedPassword);

        return new LinklyCloudCredentialSettings(
            legacyUsername,
            password,
            !string.IsNullOrWhiteSpace(legacyProtectedPassword));
    }

    public async Task SaveLinklyCloudCredentialAsync(
        CardTerminalEnvironment environment,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("Linkly Cloud username is required.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Linkly Cloud password is required.");
        }

        var protectedPassword = protector.Protect(password.Trim());
        if (string.IsNullOrWhiteSpace(protectedPassword))
        {
            throw new InvalidOperationException("Linkly Cloud password could not be protected.");
        }

        await settingsRepository.SetValuesAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GetLinklyCloudUsernameKey(environment)] = username.Trim(),
                [GetLinklyCloudPasswordKey(environment)] = protectedPassword,
                [GetLinklyCloudCredentialSnapshotKey(environment)] = JsonSerializer.Serialize(
                    new[] { username.Trim(), protectedPassword }),
            },
            cancellationToken);
        LogLinklyCloud($"protected cloud api credential saved environment={environment} hasUsername=true");
    }

    private async Task<string?> ReadProtectedSquareAccessTokenAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken)
    {
        var protectedToken = await settingsRepository.GetValueAsync(GetSquareTokenKey(environment), cancellationToken);
        if (!string.IsNullOrWhiteSpace(protectedToken))
        {
            return protectedToken;
        }

        return environment == CardTerminalEnvironment.Production
            ? await settingsRepository.GetValueAsync(LegacySquareTokenKey, cancellationToken)
            : null;
    }

    private Task<string?> ReadProtectedLinklyCloudSecretAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken)
    {
        return settingsRepository.GetValueAsync(GetLinklyCloudSecretKey(environment), cancellationToken);
    }

    private static string GetSquareTokenKey(CardTerminalEnvironment environment)
    {
        return $"{SquareTokenKeyPrefix}{environment}";
    }

    private static string GetLinklyCloudSecretKey(CardTerminalEnvironment environment)
    {
        return $"{LinklyCloudSecretKeyPrefix}{environment}";
    }

    private static string GetLinklyCloudUsernameKey(CardTerminalEnvironment environment)
    {
        return $"{LinklyCloudUsernameKeyPrefix}{environment}";
    }

    private static string GetLinklyCloudPasswordKey(CardTerminalEnvironment environment)
    {
        return $"{LinklyCloudPasswordKeyPrefix}{environment}";
    }

    private static string GetLinklyCloudCredentialSnapshotKey(CardTerminalEnvironment environment)
    {
        return $"{LinklyCloudCredentialSnapshotKeyPrefix}{environment}";
    }

    private static bool TryParseLinklyCloudCredentialSnapshot(
        string? snapshot,
        out string? username,
        out string? protectedPassword)
    {
        username = null;
        protectedPassword = null;
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            return false;
        }

        try
        {
            var values = JsonSerializer.Deserialize<string[]>(snapshot);
            if (values is not { Length: 2 } ||
                string.IsNullOrWhiteSpace(values[0]) ||
                string.IsNullOrWhiteSpace(values[1]))
            {
                return false;
            }

            username = values[0].Trim();
            protectedPassword = values[1];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsStrictlyNewer(
        LocalAppSettingEntry? candidate,
        LocalAppSettingEntry? baseline)
    {
        return candidate?.UpdatedAt is { } candidateUpdatedAt &&
               baseline?.UpdatedAt is { } baselineUpdatedAt &&
               candidateUpdatedAt > baselineUpdatedAt;
    }

    private static string GetLinklyCloudPosIdKey(
        CardTerminalEnvironment environment,
        string storeCode,
        string deviceCode)
    {
        return $"{LinklyCloudPosIdKeyPrefix}{environment}:{NormalizeKeyPart(storeCode)}:{NormalizeKeyPart(deviceCode)}";
    }

    private static string GetLegacyLinklyCloudPosIdKey(string storeCode, string deviceCode)
    {
        return $"{LinklyCloudPosIdKeyPrefix}{NormalizeKeyPart(storeCode)}:{NormalizeKeyPart(deviceCode)}";
    }

    public async Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(includeSquareTokenStatus: false, cancellationToken);
        // 付款运行时只需要知道 Square 是否由后端配置；不再把旧本地 bearer 放入 settings。
        string? squareAccessToken = null;
        var linklyCloudSecret = await GetLinklyCloudSecretAsync(configuration.Environment, cancellationToken);
        var environmentSettings = CardTerminalSettings.FromEnvironment();

        return new CardTerminalSettings(
            configuration.Processor,
            configuration.Environment,
            configuration.LinklyHost,
            configuration.LinklyPort,
            squareAccessToken,
            configuration.SquareLocationId,
            configuration.SquareDeviceId,
            CardTerminalSettings.GetSquareApiBaseUrl(configuration.Environment),
            TimeSpan.FromSeconds(NormalizeTimeoutSeconds(configuration.TerminalTimeoutSeconds)),
            CardTerminalSettings.NormalizeLinklyConnectionMode(configuration.LinklyConnectionMode),
            linklyCloudSecret,
            CardTerminalSettings.ResolveLinklyCloudAuthBaseUrl(configuration.Environment),
            CardTerminalSettings.ResolveLinklyCloudRestBaseUrl(configuration.Environment),
            environmentSettings.LinklyPosName,
            environmentSettings.LinklyPosVersion,
            CardTerminalSettings.ResolveLinklyPosVendorId(configuration.Environment),
            CardTerminalSettings.NormalizeLinklyConnectionModePriority(
                configuration.LinklyConnectionModePriority,
                configuration.LinklyConnectionMode));
    }

    private async Task<bool> ResolveSquareTokenConfiguredAsync(
        CardProcessorKind processor,
        CardTerminalEnvironment environment,
        bool hasLegacyProtectedToken,
        CancellationToken cancellationToken)
    {
        if (processor != CardProcessorKind.Square || squareTokenApiClient is null)
        {
            return hasLegacyProtectedToken;
        }

        try
        {
            // 设置页状态以“后端是否已配置 token”为准；本地缓存仅作为兼容回退。
            var status = await squareTokenApiClient.GetStatusAsync(environment, cancellationToken);
            return status.Configured;
        }
        catch (CatalogApiException ex) when (string.Equals(ex.ErrorCode, "SQUARE_TOKEN_NOT_CONFIGURED", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or CatalogApiException)
        {
            ConsoleLog.WriteError(
                "CardTerminalSettings",
                $"square token status lookup failed environment={environment} error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
            return hasLegacyProtectedToken;
        }
    }

    private static CardProcessorKind ParseProcessor(string? value, CardProcessorKind fallback)
    {
        return Enum.TryParse<CardProcessorKind>(value, ignoreCase: true, out var processor)
            ? processor
            : fallback;
    }

    private static CardTerminalEnvironment ParseEnvironment(string? value, CardTerminalEnvironment fallback)
    {
        return Enum.TryParse<CardTerminalEnvironment>(value, ignoreCase: true, out var environment)
            ? environment
            : fallback;
    }

    private static LinklyConnectionMode ParseLinklyConnectionMode(string? value, LinklyConnectionMode fallback)
    {
        return CardTerminalSettings.NormalizeLinklyConnectionMode(value, fallback);
    }

    private static string NormalizeText(string? value, string? fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback ?? string.Empty : value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int ParsePort(string? value, int fallback)
    {
        return int.TryParse(value, out var port) ? NormalizePort(port) : NormalizePort(fallback);
    }

    private static int NormalizePort(int port)
    {
        return port is > 0 and <= 65535 ? port : 2011;
    }

    private static int ParseTimeoutSeconds(string? value, int fallback)
    {
        return int.TryParse(value, out var seconds) ? NormalizeTimeoutSeconds(seconds) : NormalizeTimeoutSeconds(fallback);
    }

    private static int NormalizeTimeoutSeconds(int seconds)
    {
        return seconds > 0 ? seconds : CardTerminalConfiguration.Default.TerminalTimeoutSeconds;
    }

    private static string NormalizeKeyPart(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        return string.Concat(trimmed.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
    }

    private static bool IsUuidV4(string? value)
    {
        return Guid.TryParse(value, out var parsed) &&
            parsed.ToString("D").Equals(value.Trim(), StringComparison.OrdinalIgnoreCase) &&
            ((parsed.ToByteArray()[7] >> 4) & 0x0F) == 4;
    }

    private static void LogLinklyCloud(string message)
    {
        ConsoleLog.Write("LinklyCloud", $"settings-store {message}");
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<null>";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 8 ? trimmed : $"{trimmed[..8]}...";
    }
}
