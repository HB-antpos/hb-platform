using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class CatalogSyncStatusServiceTests
{
    private static readonly DateTimeOffset SucceededAt = new(2026, 9, 26, 4, 32, 0, TimeSpan.Zero);

    [Fact]
    public async Task MarkSucceededAsync_persists_time_that_a_new_instance_loads_for_the_same_store()
    {
        var repository = new InMemoryAppSettingsRepository();
        var clock = new MutableTimeProvider(SucceededAt);
        var service = new CatalogSyncStatusService(repository, clock);

        service.MarkStarted("s01");
        await service.MarkSucceededAsync("s01");

        Assert.Equal(new CatalogSyncStatus(SucceededAt, false, null, null), service.GetStatus("S01"));
        Assert.Equal(
            SucceededAt.ToString("O"),
            repository.Values[CatalogSyncStatusService.LastSucceededAtKeyPrefix + "S01"]);

        var restarted = new CatalogSyncStatusService(repository, clock);
        Assert.Equal(CatalogSyncStatus.Empty, restarted.GetStatus("S01"));
        var loaded = await restarted.LoadAsync(" S01 ");

        Assert.Equal(SucceededAt, loaded.LastSucceededAt);
        Assert.Equal(SucceededAt, restarted.GetStatus("s01").LastSucceededAt);
        Assert.Null((await restarted.LoadAsync("S02")).LastSucceededAt);
    }

    [Fact]
    public async Task Failure_keeps_last_success_until_the_next_success_clears_it()
    {
        var clock = new MutableTimeProvider(SucceededAt);
        var service = new CatalogSyncStatusService(new InMemoryAppSettingsRepository(), clock);
        service.MarkStarted("S01");
        await service.MarkSucceededAsync("S01");

        var failedAt = SucceededAt.AddHours(1);
        clock.UtcNow = failedAt;
        service.MarkStarted("S01");
        Assert.True(service.GetStatus("S01").IsSyncing);
        service.MarkFailed("S01", "network timeout");

        Assert.Equal(
            new CatalogSyncStatus(SucceededAt, false, failedAt, "network timeout"),
            service.GetStatus("S01"));

        var retriedAt = failedAt.AddMinutes(5);
        clock.UtcNow = retriedAt;
        service.MarkStarted("S01");
        await service.MarkSucceededAsync("S01");

        Assert.Equal(new CatalogSyncStatus(retriedAt, false, null, null), service.GetStatus("S01"));
    }

    [Fact]
    public void Canceled_sync_only_clears_the_running_state()
    {
        var service = new CatalogSyncStatusService(new InMemoryAppSettingsRepository());
        var changes = 0;
        service.Changed += (_, _) => changes++;

        service.MarkStarted("S01");
        service.MarkCanceled("S01");

        Assert.Equal(CatalogSyncStatus.Empty, service.GetStatus("S01"));
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Persist_failure_keeps_in_memory_success_without_throwing()
    {
        var repository = new InMemoryAppSettingsRepository { FailWrites = true };
        var service = new CatalogSyncStatusService(repository, new MutableTimeProvider(SucceededAt));

        service.MarkStarted("S01");
        await service.MarkSucceededAsync("S01");

        Assert.Equal(SucceededAt, service.GetStatus("S01").LastSucceededAt);
        Assert.Empty(repository.Values);
    }

    [Fact]
    public async Task Blank_store_code_is_ignored()
    {
        var repository = new InMemoryAppSettingsRepository();
        var service = new CatalogSyncStatusService(repository);

        service.MarkStarted(" ");
        await service.MarkSucceededAsync(string.Empty);

        Assert.Equal(CatalogSyncStatus.Empty, service.GetStatus(string.Empty));
        Assert.Equal(CatalogSyncStatus.Empty, await service.LoadAsync(" "));
        Assert.Empty(repository.Values);
    }

    internal sealed class InMemoryAppSettingsRepository : ILocalAppSettingsRepository
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool FailWrites { get; set; }

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            Values.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            if (FailWrites)
            {
                throw new IOException("database is locked");
            }

            Values[key] = value;
            return Task.CompletedTask;
        }

        public async Task SetValuesAsync(
            IReadOnlyDictionary<string, string> values,
            CancellationToken cancellationToken = default)
        {
            foreach (var (key, value) in values)
            {
                await SetValueAsync(key, value, cancellationToken);
            }
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }

    internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
