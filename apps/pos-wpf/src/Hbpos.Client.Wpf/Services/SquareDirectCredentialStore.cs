using System.Text.Json;

namespace Hbpos.Client.Wpf.Services;

/// <summary>总部下发给本机的直连 Square 凭据；AccessToken 只存在于内存与 DPAPI 密文中，绝不写日志。</summary>
public sealed record SquareDirectCredential(
    CardTerminalEnvironment Environment,
    string AccessToken,
    long CredentialVersion,
    DateTimeOffset ValidUntil)
{
    // 防止 record 自带 ToString 把令牌打进日志或异常信息。
    public override string ToString() =>
        $"SquareDirectCredential {{ Environment = {Environment}, Version = {CredentialVersion}, ValidUntil = {ValidUntil:O} }}";
}

/// <summary>最近一次向服务端领取凭据时，服务端门店开关的状态；从未领取过是 Unknown。</summary>
public enum SquareDirectServerState
{
    Unknown = 0,
    Enabled = 1,
    Disabled = 2
}

/// <summary>直连创建结账后尚未向服务端登记归属的结账（登记失败时落盘，等网络恢复后补登）。</summary>
public sealed record SquareDirectPendingOrigin(
    CardTerminalEnvironment Environment,
    string CheckoutId,
    DateTimeOffset CreatedAt);

/// <summary>
/// 直连 Square 的本机状态存储：本地开关（默认关闭）、凭据密文、服务端开关的最近状态、待补登的结账归属。
/// 纯逻辑，不依赖 WPF；凭据用 <see cref="IDeviceAuthorizationProtector"/>（Windows DPAPI 当前用户范围）加密后落库。
/// </summary>
public interface ISquareDirectCredentialStore
{
    Task<bool> IsLocalEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>设置本地开关。关闭时同时删除所有环境的凭据，确保令牌不在已关闭的机器上残留。</summary>
    Task SetLocalEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>读取凭据。已过期（到了 ValidUntil）或密文无法解密时视为不存在并顺手删除。</summary>
    Task<SquareDirectCredential?> LoadCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task SaveCredentialAsync(
        SquareDirectCredential credential,
        CancellationToken cancellationToken = default);

    Task DeleteCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<SquareDirectServerState> GetServerStateAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task SetServerStateAsync(
        CardTerminalEnvironment environment,
        SquareDirectServerState state,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SquareDirectPendingOrigin>> ListPendingOriginsAsync(
        CancellationToken cancellationToken = default);

    Task AddPendingOriginAsync(
        SquareDirectPendingOrigin pending,
        CancellationToken cancellationToken = default);

    Task RemovePendingOriginAsync(
        CardTerminalEnvironment environment,
        string checkoutId,
        CancellationToken cancellationToken = default);
}

public sealed class SquareDirectCredentialStore(
    ILocalAppSettingsRepository settingsRepository,
    IDeviceAuthorizationProtector protector,
    TimeProvider? timeProvider = null) : ISquareDirectCredentialStore
{
    private const string EnabledKey = "CardTerminal:SquareDirect:Enabled";
    private const string CredentialKeyPrefix = "CardTerminal:SquareDirect:CredentialProtected:";
    private const string ServerStateKeyPrefix = "CardTerminal:SquareDirect:ServerState:";
    private const string PendingOriginsKey = "CardTerminal:SquareDirect:PendingOrigins";

    // 待补登记录只为「登记失败又没等到网络恢复」兜底：数量、保留时间都有上限，避免无限增长。
    private const int MaxPendingOrigins = 20;
    private static readonly TimeSpan PendingOriginRetention = TimeSpan.FromHours(24);

    private static readonly CardTerminalEnvironment[] AllEnvironments =
        [CardTerminalEnvironment.Production, CardTerminalEnvironment.Sandbox];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim PendingGate = new(1, 1);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<bool> IsLocalEnabledAsync(CancellationToken cancellationToken = default)
    {
        var value = await settingsRepository.GetValueAsync(EnabledKey, cancellationToken);
        // 默认关闭：只有明确写入 "true" 才算开启，缺失、乱值都按关闭处理。
        return string.Equals(value?.Trim(), bool.TrueString, StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetLocalEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await settingsRepository.SetValueAsync(
            EnabledKey,
            enabled ? bool.TrueString : bool.FalseString,
            cancellationToken);
        if (!enabled)
        {
            foreach (var environment in AllEnvironments)
            {
                await DeleteCredentialAsync(environment, cancellationToken);
            }
        }
    }

    public async Task<SquareDirectCredential?> LoadCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        var protectedValue = await settingsRepository.GetValueAsync(GetCredentialKey(environment), cancellationToken);
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        CredentialPayload? payload = null;
        var json = protector.Unprotect(protectedValue);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                payload = JsonSerializer.Deserialize<CredentialPayload>(json, JsonOptions);
            }
            catch (JsonException)
            {
                payload = null;
            }
        }

        // 无法解密（换了 Windows 用户 / 机器）或内容损坏：令牌没有用了，直接删掉让下次重新领取。
        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            await DeleteCredentialAsync(environment, cancellationToken);
            return null;
        }

        // 到期（服务端给的 ValidUntil，约 12 小时）就删除：总部关掉开关后，本机最迟一个周期内丢掉令牌。
        if (_timeProvider.GetUtcNow() >= payload.ValidUntil)
        {
            await DeleteCredentialAsync(environment, cancellationToken);
            return null;
        }

        return new SquareDirectCredential(environment, payload.AccessToken, payload.CredentialVersion, payload.ValidUntil);
    }

    public async Task SaveCredentialAsync(
        SquareDirectCredential credential,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(
            new CredentialPayload(credential.AccessToken, credential.CredentialVersion, credential.ValidUntil),
            JsonOptions);
        var protectedValue = protector.Protect(json);
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            // 加密失败绝不退而求其次存明文。
            throw new InvalidOperationException("Square direct credential could not be protected.");
        }

        await settingsRepository.SetValueAsync(GetCredentialKey(credential.Environment), protectedValue, cancellationToken);
    }

    public Task DeleteCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        return settingsRepository.DeleteValueAsync(GetCredentialKey(environment), cancellationToken);
    }

    public async Task<SquareDirectServerState> GetServerStateAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        var value = await settingsRepository.GetValueAsync(GetServerStateKey(environment), cancellationToken);
        return Enum.TryParse<SquareDirectServerState>(value, ignoreCase: true, out var state) &&
            Enum.IsDefined(state)
                ? state
                : SquareDirectServerState.Unknown;
    }

    public Task SetServerStateAsync(
        CardTerminalEnvironment environment,
        SquareDirectServerState state,
        CancellationToken cancellationToken = default)
    {
        return state == SquareDirectServerState.Unknown
            ? settingsRepository.DeleteValueAsync(GetServerStateKey(environment), cancellationToken)
            : settingsRepository.SetValueAsync(GetServerStateKey(environment), state.ToString(), cancellationToken);
    }

    public async Task<IReadOnlyList<SquareDirectPendingOrigin>> ListPendingOriginsAsync(
        CancellationToken cancellationToken = default)
    {
        await PendingGate.WaitAsync(cancellationToken);
        try
        {
            return await ReadPendingOriginsCoreAsync(cancellationToken);
        }
        finally
        {
            PendingGate.Release();
        }
    }

    public async Task AddPendingOriginAsync(
        SquareDirectPendingOrigin pending,
        CancellationToken cancellationToken = default)
    {
        await PendingGate.WaitAsync(cancellationToken);
        try
        {
            var list = (await ReadPendingOriginsCoreAsync(cancellationToken)).ToList();
            list.RemoveAll(item => IsSame(item, pending.Environment, pending.CheckoutId));
            list.Add(pending);
            // 超出上限时丢最旧的；这些结账最终仍能通过服务端核实归属后补登。
            var trimmed = list.OrderByDescending(item => item.CreatedAt).Take(MaxPendingOrigins).ToList();
            await WritePendingOriginsCoreAsync(trimmed, cancellationToken);
        }
        finally
        {
            PendingGate.Release();
        }
    }

    public async Task RemovePendingOriginAsync(
        CardTerminalEnvironment environment,
        string checkoutId,
        CancellationToken cancellationToken = default)
    {
        await PendingGate.WaitAsync(cancellationToken);
        try
        {
            var list = (await ReadPendingOriginsCoreAsync(cancellationToken)).ToList();
            if (list.RemoveAll(item => IsSame(item, environment, checkoutId)) > 0)
            {
                await WritePendingOriginsCoreAsync(list, cancellationToken);
            }
        }
        finally
        {
            PendingGate.Release();
        }
    }

    private async Task<IReadOnlyList<SquareDirectPendingOrigin>> ReadPendingOriginsCoreAsync(
        CancellationToken cancellationToken)
    {
        var value = await settingsRepository.GetValueAsync(PendingOriginsKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<SquareDirectPendingOrigin>>(value, JsonOptions) ?? [];
            var cutoff = _timeProvider.GetUtcNow() - PendingOriginRetention;
            return items
                .Where(item => !string.IsNullOrWhiteSpace(item.CheckoutId) && item.CreatedAt >= cutoff)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private Task WritePendingOriginsCoreAsync(
        IReadOnlyList<SquareDirectPendingOrigin> items,
        CancellationToken cancellationToken)
    {
        return items.Count == 0
            ? settingsRepository.DeleteValueAsync(PendingOriginsKey, cancellationToken)
            : settingsRepository.SetValueAsync(
                PendingOriginsKey,
                JsonSerializer.Serialize(items, JsonOptions),
                cancellationToken);
    }

    private static bool IsSame(SquareDirectPendingOrigin item, CardTerminalEnvironment environment, string checkoutId) =>
        item.Environment == environment &&
        string.Equals(item.CheckoutId, checkoutId, StringComparison.Ordinal);

    private static string GetCredentialKey(CardTerminalEnvironment environment) => $"{CredentialKeyPrefix}{environment}";

    private static string GetServerStateKey(CardTerminalEnvironment environment) => $"{ServerStateKeyPrefix}{environment}";

    private sealed record CredentialPayload(string AccessToken, long CredentialVersion, DateTimeOffset ValidUntil)
    {
        public override string ToString() => "CredentialPayload { ... }";
    }
}
