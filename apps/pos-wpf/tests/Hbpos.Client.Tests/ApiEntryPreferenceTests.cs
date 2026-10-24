using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Stores;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>
/// 后台下发的首选入口：本机持久化、冷启动套用、服务端值覆盖，以及随 receipt-profile/sync 一起解析。
/// 服务会写 ConsoleLog，因此与其他全局日志用例串行。
/// </summary>
[Collection(GlobalLoggingTestCollection.Name)]
public sealed class ApiEntryPreferenceTests
{
    private static readonly Uri Configured = new("https://pos.hotbargain.vip/pos-api/");

    [Fact]
    public void Cold_start_without_a_remembered_value_uses_edge()
    {
        var store = new MemoryPreferenceStore();

        var selector = ApiEntryPreferenceService.CreateSelector(store);

        Assert.Equal("edge", selector.PreferredMode);
        Assert.Equal("pos.hotbargain.vip", selector.Resolve(Configured).Host);
    }

    [Fact]
    public void Cold_start_applies_the_remembered_value_before_any_server_response()
    {
        var store = new MemoryPreferenceStore { Value = "aws" };

        var selector = ApiEntryPreferenceService.CreateSelector(store);

        Assert.Equal("aws", selector.PreferredMode);
        Assert.Equal("pos-syd.hotbargain.vip", selector.Resolve(Configured).Host);
    }

    [Fact]
    public void A_corrupt_or_unreadable_store_falls_back_to_edge()
    {
        var selector = ApiEntryPreferenceService.CreateSelector(new MemoryPreferenceStore { ThrowOnLoad = true });

        Assert.Equal("edge", selector.PreferredMode);
    }

    [Theory]
    [InlineData("aws", "aws")]
    [InlineData("direct", "direct")]
    [InlineData("edge", "edge")]
    [InlineData(" Direct ", "direct")]
    public void A_valid_server_value_updates_the_selector_and_is_remembered(string serverValue, string expected)
    {
        var (service, selector, store) = Create();

        service.Apply(serverValue);

        Assert.Equal(expected, selector.PreferredMode);
        Assert.Equal(expected, store.Value);
    }

    [Fact]
    public void A_null_server_value_means_unset_so_edge_and_overrides_the_remembered_value()
    {
        var (service, selector, store) = Create(remembered: "direct");
        Assert.Equal("direct", selector.PreferredMode);

        service.Apply(null);

        Assert.Equal("edge", selector.PreferredMode);
        Assert.Null(store.Value);
        Assert.Equal(1, store.SaveCount);
    }

    [Theory]
    [InlineData("satellite")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("pos-syd.hotbargain.vip")]
    public void An_unknown_server_value_keeps_the_current_setting_and_the_remembered_value(string unknown)
    {
        var (service, selector, store) = Create(remembered: "aws");

        service.Apply(unknown);

        Assert.Equal("aws", selector.PreferredMode);
        Assert.Equal("aws", store.Value);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void The_same_value_is_not_written_to_disk_every_sync_round()
    {
        var (service, _, store) = Create();

        service.Apply("aws");
        service.Apply("aws");
        service.Apply("AWS");

        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public void A_failed_write_does_not_throw_and_is_retried_on_the_next_round()
    {
        var (service, selector, store) = Create();
        store.FailSave = true;

        service.Apply("aws");

        // 持久化失败不影响本次生效。
        Assert.Equal("aws", selector.PreferredMode);
        Assert.Null(store.Value);

        store.FailSave = false;
        service.Apply("aws");
        Assert.Equal("aws", store.Value);
    }

    [Fact]
    public void Store_exceptions_never_escape_apply()
    {
        var (service, selector, store) = Create();
        store.ThrowOnSave = true;

        service.Apply("direct");

        Assert.Equal("direct", selector.PreferredMode);
    }

    [Fact]
    public void The_file_store_round_trips_and_survives_missing_or_corrupt_files()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hbpos-entry-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "api-entry-mode.json");
            var store = new FileApiEntryPreferenceStore(path);
            Assert.Null(store.Load());

            Assert.True(store.TrySave("aws"));
            Assert.Equal("aws", new FileApiEntryPreferenceStore(path).Load());

            // 「后台未设置」也要能记住（覆盖之前的值）。
            Assert.True(store.TrySave(null));
            Assert.Null(store.Load());

            File.WriteAllText(path, "{ not json");
            Assert.Null(store.Load());

            File.WriteAllText(path, "{\"mode\":\"satellite\"}");
            Assert.Null(store.Load());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void The_file_store_reports_a_write_failure_instead_of_throwing()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hbpos-entry-{Guid.NewGuid():N}");
        var blocked = Path.Combine(directory, "api-entry-mode.json");
        Directory.CreateDirectory(blocked);
        try
        {
            // 目标路径本身是一个目录：替换文件必然失败。
            var store = new FileApiEntryPreferenceStore(blocked);

            Assert.False(store.TrySave("aws"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- 随 receipt-profile/sync 下发 ----

    [Fact]
    public async Task Sync_applies_the_entry_mode_even_when_the_receipt_profile_is_unchanged()
    {
        using var harness = new SyncHarness();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Unchanged(0) with { ApiEntryMode = "aws" });

        await harness.Service.SyncNowAsync();

        Assert.Equal(["aws"], harness.Entry.Applied);
    }

    [Fact]
    public async Task Sync_applies_the_entry_mode_when_the_receipt_profile_changed()
    {
        using var harness = new SyncHarness();
        harness.Api.OnSync = (_, _) => Task.FromResult(
            ProfileTestData.Changed(ProfileTestData.Profile(3)) with { ApiEntryMode = "direct" });

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Applied, result.Outcome);
        Assert.Equal(["direct"], harness.Entry.Applied);
    }

    [Fact]
    public async Task Sync_passes_null_and_unknown_values_through_so_the_service_decides()
    {
        using var harness = new SyncHarness();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Unchanged(0));
        await harness.Service.SyncNowAsync();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Unchanged(0) with { ApiEntryMode = "satellite" });
        await harness.Service.SyncNowAsync();

        Assert.Equal([null, "satellite"], harness.Entry.Applied);
    }

    [Fact]
    public async Task Sync_still_applies_the_entry_mode_when_the_profile_is_discarded()
    {
        using var harness = new SyncHarness();
        harness.Api.OnSync = (_, _) => Task.FromResult(
            new StoreReceiptProfileSyncDto(true, 0, null, "aws"));

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Discarded, result.Outcome);
        Assert.Equal(["aws"], harness.Entry.Applied);
    }

    [Fact]
    public async Task Sync_does_not_apply_the_entry_mode_when_the_request_fails()
    {
        using var harness = new SyncHarness();
        harness.Api.OnSync = (_, _) => throw new HttpRequestException("offline");

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Failed, result.Outcome);
        Assert.Empty(harness.Entry.Applied);
    }

    [Fact]
    public async Task Sync_does_not_apply_the_entry_mode_when_the_device_changed_store_during_the_request()
    {
        using var harness = new SyncHarness();
        harness.Api.OnSync = (_, _) =>
        {
            harness.Auth.Set(new DeviceAuthorizationContext("DEV1", "S002", "HW", "AUTH"));
            return Task.FromResult(ProfileTestData.Unchanged(0) with { ApiEntryMode = "aws" });
        };

        await harness.Service.SyncNowAsync();

        // 响应属于旧店的设置，不能套到新店上。
        Assert.Empty(harness.Entry.Applied);
    }

    [Fact]
    public async Task Sync_end_to_end_updates_the_real_selector_and_remembers_the_value()
    {
        var store = new MemoryPreferenceStore();
        var selector = new ApiTransportSelector();
        var entry = new ApiEntryPreferenceService(selector, store);
        using var harness = new SyncHarness(entry);
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Unchanged(0) with { ApiEntryMode = "direct" });

        await harness.Service.SyncNowAsync();

        Assert.Equal("direct", selector.PreferredMode);
        Assert.Equal("direct", store.Value);
        Assert.Equal("hotbargain.vip", selector.Resolve(Configured).Host);
    }

    private static (ApiEntryPreferenceService Service, ApiTransportSelector Selector, MemoryPreferenceStore Store) Create(
        string? remembered = null)
    {
        var store = new MemoryPreferenceStore { Value = remembered };
        var selector = ApiEntryPreferenceService.CreateSelector(store);
        return (new ApiEntryPreferenceService(selector, store), selector, store);
    }

    private sealed class MemoryPreferenceStore : IApiEntryPreferenceStore
    {
        public string? Value { get; set; }

        public bool ThrowOnLoad { get; set; }

        public bool ThrowOnSave { get; set; }

        public bool FailSave { get; set; }

        public int SaveCount { get; private set; }

        public string? Load() => ThrowOnLoad ? throw new IOException("boom") : Value;

        public bool TrySave(string? mode)
        {
            if (ThrowOnSave)
            {
                throw new IOException("boom");
            }

            if (FailSave)
            {
                return false;
            }

            SaveCount++;
            Value = mode;
            return true;
        }
    }

    private sealed class RecordingEntryPreference : IApiEntryPreferenceService
    {
        public List<string?> Applied { get; } = [];

        public void Apply(string? serverMode) => Applied.Add(serverMode);
    }

    private sealed class SyncHarness : IDisposable
    {
        public SyncHarness(IApiEntryPreferenceService? entry = null)
        {
            Auth = ProfileTestData.Auth("S001");
            Store = new ReceiptPrinterSettingsStore(new ProfileTestSettingsRepository(), Auth);
            Api = new ScriptedProfileApiClient();
            Entry = new RecordingEntryPreference();
            Service = new ReceiptProfileSyncService(
                Store,
                Api,
                Auth,
                new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero)),
                entry ?? Entry);
        }

        public DeviceAuthorizationState Auth { get; }

        public ReceiptPrinterSettingsStore Store { get; }

        public ScriptedProfileApiClient Api { get; }

        public RecordingEntryPreference Entry { get; }

        public ReceiptProfileSyncService Service { get; }

        public void Dispose()
        {
        }
    }
}
