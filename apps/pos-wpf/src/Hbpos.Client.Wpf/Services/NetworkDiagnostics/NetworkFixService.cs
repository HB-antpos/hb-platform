namespace Hbpos.Client.Wpf.Services.NetworkDiagnostics;

/// <summary>一次修复的"将要改什么"：展示给用户确认，也是备份与还原的依据。</summary>
public sealed record NetworkFixPlan(
    NetworkFixKind Kind,
    string Target,
    string Before,
    string After,
    bool NeedsAdministrator,
    int? AdapterIndex,
    string? AdapterName,
    IReadOnlyList<string> PreviousServers,
    bool PreviousAutomatic,
    int? PreviousAutoDetect);

public sealed record NetworkFixResult(bool Succeeded, bool Cancelled, string? Error)
{
    public static NetworkFixResult Done { get; } = new(true, false, null);

    public static NetworkFixResult UserCancelled { get; } = new(false, true, null);

    public static NetworkFixResult Failure(string error) => new(false, false, error);
}

/// <summary>已执行的修复及其原值：保存在本机，用于"Undo changes"。</summary>
public sealed record NetworkFixRecord(
    NetworkFixKind Kind,
    int? AdapterIndex,
    string? AdapterName,
    IReadOnlyList<string> PreviousServers,
    bool PreviousAutomatic,
    int? PreviousAutoDetect,
    DateTimeOffset AppliedAt);

public sealed record DnsSettings(IReadOnlyList<string> Servers, bool Automatic);

public interface IDnsConfigurator
{
    DnsSettings? GetCurrent(int adapterIndex);

    /// <summary>需要管理员权限；用户在 Windows 授权窗口里拒绝时返回 false，其他失败抛异常。</summary>
    Task<bool> SetServersAsync(int adapterIndex, IReadOnlyList<string> servers, CancellationToken cancellationToken);

    Task<bool> ResetToAutomaticAsync(int adapterIndex, CancellationToken cancellationToken);
}

public interface IProxyAutoDetectStore
{
    /// <summary>注册表里「自动检测设置」的值；null 表示从未设置（Windows 视为开启）。</summary>
    int? Get();

    void Set(int? value);
}

public interface INetworkFixStore
{
    IReadOnlyList<NetworkFixRecord> Load();

    void Save(IReadOnlyList<NetworkFixRecord> records);
}

public interface INetworkFixService
{
    /// <summary>算出这次修复要改什么；不可执行时返回 null（例如找不到正在用的网卡）。不修改任何设置。</summary>
    NetworkFixPlan? Plan(NetworkFixKind kind);

    Task<NetworkFixResult> ApplyAsync(NetworkFixPlan plan, CancellationToken cancellationToken = default);

    NetworkFixRecord? GetApplied(NetworkFixKind kind);

    Task<NetworkFixResult> UndoAsync(NetworkFixKind kind, CancellationToken cancellationToken = default);
}

public sealed class NetworkFixService(
    INetworkProbe probe,
    IDnsConfigurator dns,
    IProxyAutoDetectStore proxyStore,
    INetworkFixStore store,
    Func<DateTimeOffset>? clock = null) : INetworkFixService
{
    /// <summary>修复时写入的公共 DNS。固定值，不接受外部输入。</summary>
    public static readonly IReadOnlyList<string> PublicDnsServers = ["1.1.1.1", "8.8.8.8"];

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.Now);
    private readonly object _gate = new();

    public NetworkFixPlan? Plan(NetworkFixKind kind)
    {
        switch (kind)
        {
            case NetworkFixKind.PublicDns:
            {
                var local = probe.GetLocalNetwork();
                if (!local.HasNetwork || local.AdapterIndex is not { } index)
                {
                    return null;
                }

                var current = dns.GetCurrent(index);
                var before = current is null || current.Servers.Count == 0
                    ? "automatic (from the router)"
                    : current.Automatic
                        ? $"{string.Join(", ", current.Servers)} (automatic)"
                        : string.Join(", ", current.Servers);
                return new NetworkFixPlan(
                    kind,
                    $"DNS on {local.AdapterName ?? $"adapter {index}"}",
                    before,
                    string.Join(", ", PublicDnsServers),
                    NeedsAdministrator: true,
                    index,
                    local.AdapterName,
                    current?.Servers ?? [],
                    current?.Automatic ?? true,
                    null);
            }

            case NetworkFixKind.DisableProxyAutoDetect:
            {
                var current = proxyStore.Get();
                return new NetworkFixPlan(
                    kind,
                    "Proxy: automatic detection",
                    current == 0 ? "off" : "on",
                    "off",
                    NeedsAdministrator: false,
                    null,
                    null,
                    [],
                    false,
                    current);
            }

            default:
                return null;
        }
    }

    public async Task<NetworkFixResult> ApplyAsync(NetworkFixPlan plan, CancellationToken cancellationToken = default)
    {
        try
        {
            switch (plan.Kind)
            {
                case NetworkFixKind.PublicDns:
                    if (plan.AdapterIndex is not { } index)
                    {
                        return NetworkFixResult.Failure("No active network adapter.");
                    }

                    if (!await dns.SetServersAsync(index, PublicDnsServers, cancellationToken))
                    {
                        return NetworkFixResult.UserCancelled;
                    }

                    break;
                case NetworkFixKind.DisableProxyAutoDetect:
                    proxyStore.Set(0);
                    break;
                default:
                    return NetworkFixResult.Failure("Unsupported fix.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteWarning("NetworkFix", $"apply failed kind={plan.Kind} error={ex.GetType().Name}: {ex.Message}");
            return NetworkFixResult.Failure(ex.Message);
        }

        SaveRecord(new NetworkFixRecord(
            plan.Kind,
            plan.AdapterIndex,
            plan.AdapterName,
            plan.PreviousServers,
            plan.PreviousAutomatic,
            plan.PreviousAutoDetect,
            _now()));
        ConsoleLog.Write("NetworkFix", $"applied kind={plan.Kind} target={plan.Target} before={plan.Before} after={plan.After}");
        return NetworkFixResult.Done;
    }

    public NetworkFixRecord? GetApplied(NetworkFixKind kind)
    {
        lock (_gate)
        {
            return store.Load().LastOrDefault(record => record.Kind == kind);
        }
    }

    public async Task<NetworkFixResult> UndoAsync(NetworkFixKind kind, CancellationToken cancellationToken = default)
    {
        var record = GetApplied(kind);
        if (record is null)
        {
            return NetworkFixResult.Failure("Nothing to undo.");
        }

        try
        {
            switch (kind)
            {
                case NetworkFixKind.PublicDns:
                    if (record.AdapterIndex is not { } index)
                    {
                        return NetworkFixResult.Failure("No adapter recorded.");
                    }

                    var restored = record.PreviousAutomatic || record.PreviousServers.Count == 0
                        ? await dns.ResetToAutomaticAsync(index, cancellationToken)
                        : await dns.SetServersAsync(index, record.PreviousServers, cancellationToken);
                    if (!restored)
                    {
                        return NetworkFixResult.UserCancelled;
                    }

                    break;
                case NetworkFixKind.DisableProxyAutoDetect:
                    proxyStore.Set(record.PreviousAutoDetect);
                    break;
                default:
                    return NetworkFixResult.Failure("Unsupported fix.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteWarning("NetworkFix", $"undo failed kind={kind} error={ex.GetType().Name}: {ex.Message}");
            return NetworkFixResult.Failure(ex.Message);
        }

        lock (_gate)
        {
            store.Save(store.Load().Where(item => item.Kind != kind).ToArray());
        }

        ConsoleLog.Write("NetworkFix", $"undone kind={kind}");
        return NetworkFixResult.Done;
    }

    private void SaveRecord(NetworkFixRecord record)
    {
        lock (_gate)
        {
            // 同一种修复只保留最早的原值：重复修复后还原，应回到第一次修复之前的状态。
            var existing = store.Load();
            if (existing.Any(item => item.Kind == record.Kind))
            {
                return;
            }

            store.Save([.. existing, record]);
        }
    }
}
