using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class CustomerDisplayWindowPreferenceStoreTests
{
    private static readonly CustomerDisplayNormalBounds WorkArea = new(140, 240, 1000, 700);

    [Fact]
    public async Task LoadAsync_without_saved_values_defaults_to_closed_without_bounds()
    {
        var store = new CustomerDisplayWindowPreferenceStore(new InMemorySettingsRepository());

        var preference = await store.LoadAsync();

        Assert.Equal(CustomerDisplayWindowMode.Closed, preference.Mode);
        Assert.Null(preference.NormalBounds);
    }

    [Fact]
    public async Task Remembered_mode_and_bounds_round_trip_through_settings()
    {
        var repository = new InMemorySettingsRepository();
        var writer = new CustomerDisplayWindowPreferenceStore(repository);

        await writer.RememberModeAsync(CustomerDisplayWindowMode.Normal);
        await writer.RememberNormalBoundsAsync(new CustomerDisplayNormalBounds(1920.5, -12, 1024, 640.25));
        var preference = await new CustomerDisplayWindowPreferenceStore(repository).LoadAsync();

        Assert.Equal(CustomerDisplayWindowMode.Normal, preference.Mode);
        Assert.Equal(new CustomerDisplayNormalBounds(1920.5, -12, 1024, 640.25), preference.NormalBounds);
    }

    [Fact]
    public async Task Remembering_mode_only_does_not_write_bounds_key()
    {
        var repository = new InMemorySettingsRepository();
        var store = new CustomerDisplayWindowPreferenceStore(repository);

        await store.RememberModeAsync(CustomerDisplayWindowMode.Fullscreen);

        Assert.Equal("Fullscreen", repository.Values[CustomerDisplayWindowPreferenceStore.ModeKey]);
        Assert.False(repository.Values.ContainsKey(CustomerDisplayWindowPreferenceStore.NormalBoundsKey));
    }

    [Theory]
    [InlineData("Garbage")]
    [InlineData("42")]
    [InlineData("")]
    public async Task LoadAsync_with_unknown_mode_falls_back_to_closed(string storedMode)
    {
        var repository = new InMemorySettingsRepository();
        repository.Values[CustomerDisplayWindowPreferenceStore.ModeKey] = storedMode;

        var preference = await new CustomerDisplayWindowPreferenceStore(repository).LoadAsync();

        Assert.Equal(CustomerDisplayWindowMode.Closed, preference.Mode);
    }

    [Theory]
    [InlineData("1,2,3")]
    [InlineData("a,b,c,d")]
    [InlineData("0,0,0,500")]
    [InlineData("0,0,-1,500")]
    [InlineData("NaN,0,800,500")]
    public void ParseBounds_rejects_malformed_or_empty_sizes(string value)
    {
        Assert.Null(CustomerDisplayWindowPreferenceStore.ParseBounds(value));
    }

    [Fact]
    public async Task Change_made_while_loading_is_not_overwritten_by_stale_saved_value()
    {
        var repository = new InMemorySettingsRepository { HoldReads = true };
        repository.Values[CustomerDisplayWindowPreferenceStore.ModeKey] = "Fullscreen";
        var store = new CustomerDisplayWindowPreferenceStore(repository);

        var loading = store.LoadAsync();
        await store.RememberModeAsync(CustomerDisplayWindowMode.Closed);
        repository.ReleaseReads();
        var preference = await loading;

        Assert.Equal(CustomerDisplayWindowMode.Closed, preference.Mode);
        Assert.Equal(CustomerDisplayWindowMode.Closed, store.Current.Mode);
    }

    [Fact]
    public async Task Persist_failure_is_logged_and_keeps_in_memory_value()
    {
        var repository = new InMemorySettingsRepository { WriteException = new IOException("disk full") };
        var store = new CustomerDisplayWindowPreferenceStore(repository);

        await store.RememberModeAsync(CustomerDisplayWindowMode.Normal);

        Assert.Equal(CustomerDisplayWindowMode.Normal, store.Current.Mode);
    }

    [Fact]
    public void ResolveRestoredBounds_keeps_bounds_fully_inside_work_area()
    {
        var saved = new CustomerDisplayNormalBounds(300, 360, 820, 560);

        var restored = CustomerDisplayWindowPreferenceStore.ResolveRestoredBounds(WorkArea, saved, 800, 520);

        Assert.Equal(saved, restored);
    }

    [Fact]
    public void ResolveRestoredBounds_clamps_partially_offscreen_window_back_inside()
    {
        var saved = new CustomerDisplayNormalBounds(-200, 900, 900, 600);

        var restored = CustomerDisplayWindowPreferenceStore.ResolveRestoredBounds(WorkArea, saved, 800, 520);

        Assert.Equal(new CustomerDisplayNormalBounds(140, 340, 900, 600), restored);
    }

    [Fact]
    public void ResolveRestoredBounds_shrinks_oversized_window_and_enforces_minimum_size()
    {
        var oversized = CustomerDisplayWindowPreferenceStore.ResolveRestoredBounds(
            WorkArea,
            new CustomerDisplayNormalBounds(150, 250, 1600, 900),
            800,
            520);
        var undersized = CustomerDisplayWindowPreferenceStore.ResolveRestoredBounds(
            WorkArea,
            new CustomerDisplayNormalBounds(150, 250, 300, 200),
            800,
            520);

        Assert.Equal(WorkArea, oversized);
        Assert.Equal(new CustomerDisplayNormalBounds(150, 250, 800, 520), undersized);
    }

    [Fact]
    public void ResolveRestoredBounds_returns_null_when_window_was_on_another_display_or_never_saved()
    {
        Assert.Null(CustomerDisplayWindowPreferenceStore.ResolveRestoredBounds(
            WorkArea,
            new CustomerDisplayNormalBounds(3000, 0, 900, 600),
            800,
            520));
        Assert.Null(CustomerDisplayWindowPreferenceStore.ResolveRestoredBounds(WorkArea, null, 800, 520));
    }

    [Fact]
    public void ResolveRestoredBounds_returns_null_when_minimum_size_exceeds_work_area()
    {
        Assert.Null(CustomerDisplayWindowPreferenceStore.ResolveRestoredBounds(
            new CustomerDisplayNormalBounds(0, 0, 640, 480),
            new CustomerDisplayNormalBounds(0, 0, 600, 400),
            800,
            520));
    }

    private sealed class InMemorySettingsRepository : ILocalAppSettingsRepository
    {
        private readonly TaskCompletionSource _readGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public bool HoldReads { get; init; }

        public Exception? WriteException { get; init; }

        public void ReleaseReads() => _readGate.TrySetResult();

        public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            // 先取值再等待，模拟读库拿到的是修改前的旧值。
            var value = Values.TryGetValue(key, out var stored) ? stored : null;
            if (HoldReads)
            {
                await _readGate.Task;
            }

            return value;
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) =>
            SetValuesAsync(new Dictionary<string, string> { [key] = value }, cancellationToken);

        public Task SetValuesAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
        {
            if (WriteException is not null)
            {
                return Task.FromException(WriteException);
            }

            foreach (var (key, value) in values)
            {
                Values[key] = value;
            }

            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
