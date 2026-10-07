using System.Net;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Stores;

namespace Hbpos.Client.Tests;

/// <summary>
/// 总部下发小票资料相关测试共用的内存设置库：与生产的 LocalAppSettingsRepository 一样，
/// 所有写入（含单键写入）都走一次批量写，并支持注入写入失败、阻塞一次批量写来验证原子性与互斥。
/// </summary>
internal sealed class ProfileTestSettingsRepository : ILocalAppSettingsRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private TaskCompletionSource? _blockNextBatch;

    public int BatchWriteCount { get; private set; }

    /// <summary>为 true 时下一次批量写在写入任何键之前抛异常（模拟事务整体回滚）。</summary>
    public bool FailNextBatch { get; set; }

    /// <summary>被 <see cref="BlockNextBatch"/> 阻塞的那次批量写已进入时完成。</summary>
    public TaskCompletionSource BlockedWriteEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(_values, StringComparer.Ordinal);
        }
    }

    public string? Peek(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value) ? value : null;
        }
    }

    /// <summary>让下一次批量写在进入后一直等到返回的 TCS 被完成才真正落盘。</summary>
    public TaskCompletionSource BlockNextBatch()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _blockNextBatch = release;
        }

        return release;
    }

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Peek(key));
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        return SetValuesAsync(new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value }, cancellationToken);
    }

    public async Task SetValuesAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? block;
        lock (_gate)
        {
            BatchWriteCount++;
            if (FailNextBatch)
            {
                FailNextBatch = false;
                throw new InvalidOperationException("simulated settings write failure");
            }

            block = _blockNextBatch;
            _blockNextBatch = null;
        }

        if (block is not null)
        {
            BlockedWriteEntered.TrySetResult();
            await block.Task;
        }

        lock (_gate)
        {
            foreach (var (key, value) in values)
            {
                _values[key] = value;
            }
        }
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _values.Remove(key);
        }

        return Task.CompletedTask;
    }
}

/// <summary>可编排的 sync/ack 接口替身，记录每次调用的版本号。</summary>
internal sealed class ScriptedProfileApiClient : IStoreReceiptProfileApiClient
{
    private readonly object _gate = new();
    private readonly List<int> _syncKnownVersions = [];
    private readonly List<int> _ackVersions = [];

    public Func<int, CancellationToken, Task<StoreReceiptProfileSyncDto>> OnSync { get; set; } =
        (_, _) => Task.FromResult(ProfileTestData.Unchanged(0));

    public Func<int, CancellationToken, Task<StoreReceiptProfileAckResultDto>> OnAck { get; set; } =
        (version, _) => Task.FromResult(new StoreReceiptProfileAckResultDto(version));

    public IReadOnlyList<int> SyncKnownVersions
    {
        get
        {
            lock (_gate)
            {
                return _syncKnownVersions.ToArray();
            }
        }
    }

    public IReadOnlyList<int> AckVersions
    {
        get
        {
            lock (_gate)
            {
                return _ackVersions.ToArray();
            }
        }
    }

    public Task<StoreReceiptProfileDto> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<StoreReceiptProfileSyncDto> GetSyncAsync(int knownVersion, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _syncKnownVersions.Add(knownVersion);
        }

        return OnSync(knownVersion, cancellationToken);
    }

    public Task<StoreReceiptProfileAckResultDto> AckAsync(int version, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _ackVersions.Add(version);
        }

        return OnAck(version, cancellationToken);
    }
}

internal static class ProfileTestData
{
    // 刻意用一眼能认出的内容，日志断言靠它们判断「资料内容没有写进日志」。
    public const string Address = "Shop 1/99 Secret Street\r\nBrisbane QLD 4000";
    public const string Phone = "07 3000 9999";
    public const string Abn = "12 345 678 901";

    public static StoreReceiptProfileDto Profile(
        int version,
        string? storeCode = "S001",
        string? storeName = "Sunnybank",
        string? brandName = "HB Brand",
        string? address = Address,
        string? phone = Phone,
        string? abn = Abn,
        string? returnPolicy = "Return within 7 days",
        string? voucherTerms = null,
        string? installmentTerms = null) =>
        new(
            storeCode!,
            storeName!,
            brandName,
            address,
            phone,
            abn,
            returnPolicy,
            version,
            DateTimeOffset.UnixEpoch,
            voucherTerms,
            installmentTerms);

    public static StoreReceiptProfileSyncDto Changed(StoreReceiptProfileDto profile) =>
        new(true, profile.Version, profile);

    public static StoreReceiptProfileSyncDto Unchanged(int version) => new(false, version, null);

    public static DeviceAuthorizationState Auth(string storeCode)
    {
        var state = new DeviceAuthorizationState();
        state.Set(new DeviceAuthorizationContext("DEV1", storeCode, "HW", "AUTH"));
        return state;
    }

    public static CatalogApiException Http(HttpStatusCode statusCode, string? errorCode = null) =>
        new($"HTTP {(int)statusCode}", statusCode, errorCode);
}
