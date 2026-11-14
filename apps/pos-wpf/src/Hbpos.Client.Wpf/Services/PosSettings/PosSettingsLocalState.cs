using System.Text.Json;

namespace Hbpos.Client.Wpf.Services;

/// <summary>总部下发的一项设置：值与来源（global / store / device / default）。</summary>
public sealed record PosSettingStoredValue(bool Value, string Source);

/// <summary>
/// 本机保存的「总部下发设置」状态，存在本机 SQLite 的 AppSettings 键值表里（单个键、一份 JSON），不涉及表结构变更。
/// 做成不可变记录、整体替换：同步线程写入的同时，界面线程读到的永远是完整的某一版，不会读到写了一半的数据。
/// </summary>
/// <param name="StoreCode">快照所属门店；换店后旧快照一律作废。</param>
/// <param name="Hash">已应用的下发指纹（下次同步带给服务端，指纹没变就不用回传整份）。</param>
/// <param name="Values">总部下发的项（只含「已管理」的项；没出现的键 = 总部没设置，本机沿用自己的值）。</param>
/// <param name="SyncedAtUtc">最近一次成功同步的时间。</param>
/// <param name="Overrides">离线期间在业务开关页临时改过、还没被总部值覆盖的项（键 → 本机值）。</param>
/// <param name="OfflineOverrideCount">累计离线临时修改次数，只增不减（服务端按最大值记录，恢复联网后随回执上报）。</param>
/// <param name="AckedHash">已成功回执给服务端的指纹。</param>
/// <param name="AckedOverrideCount">已成功回执给服务端的累计离线修改次数。</param>
public sealed record PosSettingsLocalState(
    string? StoreCode,
    string? Hash,
    IReadOnlyDictionary<string, PosSettingStoredValue> Values,
    DateTimeOffset? SyncedAtUtc,
    IReadOnlyDictionary<string, bool> Overrides,
    int OfflineOverrideCount,
    string? AckedHash,
    int AckedOverrideCount)
{
    public static PosSettingsLocalState Empty { get; } = new(
        null,
        null,
        new Dictionary<string, PosSettingStoredValue>(),
        null,
        new Dictionary<string, bool>(),
        0,
        null,
        0);

    /// <summary>是否有来自总部的快照。</summary>
    public bool HasSnapshot => !string.IsNullOrWhiteSpace(Hash);

    /// <summary>换店：丢弃旧店的快照与覆盖记录，累计次数与回执水位一并清零（它们都是针对旧店的）。</summary>
    public PosSettingsLocalState ForStore(string? storeCode) => Empty with { StoreCode = storeCode };
}

/// <summary>读写 <see cref="PosSettingsLocalState"/> 的抽象（便于单测替换）。</summary>
public interface IPosSettingsLocalStore
{
    Task<PosSettingsLocalState> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(PosSettingsLocalState state, CancellationToken cancellationToken = default);
}

public sealed class PosSettingsLocalStore(ILocalAppSettingsRepository repository) : IPosSettingsLocalStore
{
    internal const string StorageKey = "PosSettings:State";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PosSettingsLocalState> LoadAsync(CancellationToken cancellationToken = default)
    {
        var raw = await repository.GetValueAsync(StorageKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return PosSettingsLocalState.Empty;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredState>(raw, JsonOptions);
            if (stored is null)
            {
                return PosSettingsLocalState.Empty;
            }

            return new PosSettingsLocalState(
                stored.StoreCode,
                stored.Hash,
                stored.Values ?? [],
                stored.SyncedAtUtc,
                stored.Overrides ?? [],
                Math.Max(0, stored.OfflineOverrideCount),
                stored.AckedHash,
                Math.Max(0, stored.AckedOverrideCount));
        }
        catch (JsonException)
        {
            // 中文注释：损坏的状态按「从未同步」处理：不锁定任何项，下一次同步会重新拉取完整设置。
            ConsoleLog.Write("PosSettings", "local state is corrupted, ignoring it");
            return PosSettingsLocalState.Empty;
        }
    }

    public Task SaveAsync(PosSettingsLocalState state, CancellationToken cancellationToken = default)
    {
        var stored = new StoredState(
            state.StoreCode,
            state.Hash,
            state.Values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            state.SyncedAtUtc,
            state.Overrides.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            state.OfflineOverrideCount,
            state.AckedHash,
            state.AckedOverrideCount);
        return repository.SetValueAsync(StorageKey, JsonSerializer.Serialize(stored, JsonOptions), cancellationToken);
    }

    /// <summary>落盘格式：字典用可变类型，反序列化更省事；对外仍是不可变记录。</summary>
    private sealed record StoredState(
        string? StoreCode,
        string? Hash,
        Dictionary<string, PosSettingStoredValue>? Values,
        DateTimeOffset? SyncedAtUtc,
        Dictionary<string, bool>? Overrides,
        int OfflineOverrideCount,
        string? AckedHash,
        int AckedOverrideCount);
}
