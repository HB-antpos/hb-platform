using System.Globalization;

namespace Hbpos.Client.Wpf.Services;

public sealed record CatalogSyncStatus(
    DateTimeOffset? LastSucceededAt,
    bool IsSyncing,
    DateTimeOffset? LastFailedAt,
    string? LastFailureMessage)
{
    public static CatalogSyncStatus Empty { get; } = new(null, false, null, null);

    public bool IsLastAttemptFailed => LastFailedAt is not null;
}

public interface ICatalogSyncStatusService
{
    event EventHandler? Changed;

    CatalogSyncStatus GetStatus(string storeCode);

    Task<CatalogSyncStatus> LoadAsync(string storeCode, CancellationToken cancellationToken = default);

    void MarkStarted(string storeCode);

    void MarkCanceled(string storeCode);

    void MarkFailed(string storeCode, string message);

    Task MarkSucceededAsync(string storeCode);
}

public sealed class CatalogSyncStatusService(
    ILocalAppSettingsRepository settingsRepository,
    TimeProvider? timeProvider = null) : ICatalogSyncStatusService
{
    internal const string LastSucceededAtKeyPrefix = "CatalogSync:LastSucceededAt:";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    public CatalogSyncStatus GetStatus(string storeCode)
    {
        var key = NormalizeStoreCode(storeCode);
        if (key is null)
        {
            return CatalogSyncStatus.Empty;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) ? entry.ToStatus() : CatalogSyncStatus.Empty;
        }
    }

    public async Task<CatalogSyncStatus> LoadAsync(string storeCode, CancellationToken cancellationToken = default)
    {
        var key = NormalizeStoreCode(storeCode);
        if (key is null)
        {
            return CatalogSyncStatus.Empty;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var loadedEntry) && loadedEntry.PersistedLoaded)
            {
                return loadedEntry.ToStatus();
            }
        }

        var value = await settingsRepository.GetValueAsync(LastSucceededAtKeyPrefix + key, cancellationToken)
            .ConfigureAwait(false);
        var persistedAt = DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : (DateTimeOffset?)null;

        CatalogSyncStatus status;
        var changed = false;
        lock (_gate)
        {
            var entry = GetOrCreateEntry(key);
            entry.PersistedLoaded = true;
            // 读取期间可能刚完成一次同步，只接受比内存更新的持久化时间。
            if (persistedAt is not null && (entry.LastSucceededAt is null || persistedAt > entry.LastSucceededAt))
            {
                entry.LastSucceededAt = persistedAt;
                changed = true;
            }

            status = entry.ToStatus();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return status;
    }

    public void MarkStarted(string storeCode)
    {
        Update(storeCode, entry => entry.ActiveCount++);
    }

    public void MarkCanceled(string storeCode)
    {
        Update(storeCode, entry => entry.ActiveCount = Math.Max(0, entry.ActiveCount - 1));
    }

    public void MarkFailed(string storeCode, string message)
    {
        var failedAt = _timeProvider.GetUtcNow();
        Update(storeCode, entry =>
        {
            entry.ActiveCount = Math.Max(0, entry.ActiveCount - 1);
            entry.LastFailedAt = failedAt;
            entry.LastFailureMessage = message;
        });
    }

    public async Task MarkSucceededAsync(string storeCode)
    {
        var key = NormalizeStoreCode(storeCode);
        if (key is null)
        {
            return;
        }

        var succeededAt = _timeProvider.GetUtcNow();
        Update(key, entry =>
        {
            entry.ActiveCount = Math.Max(0, entry.ActiveCount - 1);
            entry.LastSucceededAt = succeededAt;
            entry.LastFailedAt = null;
            entry.LastFailureMessage = null;
        });

        try
        {
            await settingsRepository.SetValueAsync(
                    LastSucceededAtKeyPrefix + key,
                    succeededAt.ToString("O", CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 同步时间只用于展示，写入失败不能让已完成的商品同步变成失败。
            ConsoleLog.Write("CatalogSync", $"save last sync time failed store={key} error={ex.Message}");
        }
    }

    private void Update(string storeCode, Action<Entry> apply)
    {
        var key = NormalizeStoreCode(storeCode);
        if (key is null)
        {
            return;
        }

        lock (_gate)
        {
            apply(GetOrCreateEntry(key));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Entry GetOrCreateEntry(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry();
            _entries[key] = entry;
        }

        return entry;
    }

    private static string? NormalizeStoreCode(string? storeCode)
    {
        return string.IsNullOrWhiteSpace(storeCode) ? null : storeCode.Trim().ToUpperInvariant();
    }

    private sealed class Entry
    {
        public DateTimeOffset? LastSucceededAt { get; set; }

        public int ActiveCount { get; set; }

        public DateTimeOffset? LastFailedAt { get; set; }

        public string? LastFailureMessage { get; set; }

        public bool PersistedLoaded { get; set; }

        public CatalogSyncStatus ToStatus() => new(LastSucceededAt, ActiveCount > 0, LastFailedAt, LastFailureMessage);
    }
}
