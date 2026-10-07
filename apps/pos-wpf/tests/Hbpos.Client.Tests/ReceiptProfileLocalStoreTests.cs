using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>
/// 总部下发小票资料在本机的存储规则：原子写入、版本只对绑定门店有效、换店清空、
/// 只读（已下发）时保存不得用草稿覆盖资料字段。
/// </summary>
public sealed class ReceiptProfileLocalStoreTests
{
    private static readonly ReceiptProfileFields SampleFields = new(
        "HQ Brand", "Sunnybank", "Shop 1\r\nBrisbane", "07 3000 0000", "12 345 678 901", "Return within 7 days");

    [Fact]
    public async Task Apply_writes_six_fields_version_and_binding_in_one_batch()
    {
        var repository = new ProfileTestSettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, ProfileTestData.Auth("S001"));

        var applied = await store.ApplyHeadquartersProfileAsync("S001", SampleFields, 3);

        Assert.True(applied);
        // 六个字段 + 版本 + 回执版本清零 + 绑定门店必须是同一次批量写（本地库里是一个事务）。
        Assert.Equal(1, repository.BatchWriteCount);
        Assert.Equal("S001", repository.Peek("ReceiptPrinter:ProfileStoreCode"));
        Assert.Equal("3", repository.Peek("ReceiptPrinter:ProfileVersion"));
        Assert.Equal("0", repository.Peek("ReceiptPrinter:ProfileAckedVersion"));
        Assert.Equal("HQ Brand", repository.Peek("ReceiptPrinter:S001:BrandName"));
        Assert.Equal("Sunnybank", repository.Peek("ReceiptPrinter:S001:StoreName"));
        Assert.Equal("Shop 1\r\nBrisbane", repository.Peek("ReceiptPrinter:S001:StoreAddress"));
        Assert.Equal("07 3000 0000", repository.Peek("ReceiptPrinter:S001:StorePhone"));
        Assert.Equal("12 345 678 901", repository.Peek("ReceiptPrinter:S001:Abn"));
        Assert.Equal("Return within 7 days", repository.Peek("ReceiptPrinter:S001:ReturnPolicy"));

        var loaded = await store.LoadAsync();
        Assert.Equal(3, loaded.ProfileVersion);
        Assert.Equal("HQ Brand", loaded.BrandName);
        Assert.Equal("Shop 1\r\nBrisbane", loaded.StoreAddress);
        Assert.Equal("Return within 7 days", loaded.ReturnPolicy);
    }

    [Fact]
    public async Task Apply_failure_leaves_no_half_written_profile()
    {
        var repository = new ProfileTestSettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, ProfileTestData.Auth("S001"));
        repository.FailNextBatch = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ApplyHeadquartersProfileAsync("S001", SampleFields, 3));

        // 批量写整体失败：没有任何一个键落盘，设置页仍是「从未下发」，打印设置不受影响。
        Assert.Empty(repository.Snapshot());
        var loaded = await store.LoadAsync();
        Assert.Equal(0, loaded.ProfileVersion);
        Assert.Equal(string.Empty, loaded.BrandName);
        Assert.Equal(ReceiptProfileLocalState.None, await store.LoadProfileStateAsync("S001"));
    }

    [Fact]
    public async Task Apply_newer_version_replaces_fields_and_resets_acked_version()
    {
        var repository = new ProfileTestSettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, ProfileTestData.Auth("S001"));
        await store.ApplyHeadquartersProfileAsync("S001", SampleFields, 3);
        Assert.True(await store.MarkProfileAckedAsync("S001", 3));

        await store.ApplyHeadquartersProfileAsync("S001", SampleFields with { BrandName = "New Brand" }, 4);

        var state = await store.LoadProfileStateAsync("S001");
        Assert.Equal(new ReceiptProfileLocalState(4, 0), state);
        Assert.True(state.NeedsAck);
        Assert.Equal("New Brand", (await store.LoadAsync()).BrandName);
    }

    [Fact]
    public async Task Apply_lower_version_after_server_rebuild_still_resets_acked_version()
    {
        var repository = new ProfileTestSettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, ProfileTestData.Auth("S001"));
        await store.ApplyHeadquartersProfileAsync("S001", SampleFields, 9);
        await store.MarkProfileAckedAsync("S001", 9);

        // 服务端快照表被重建后版本回退：以服务端为准，回执版本必须清零才能对新版本重新回执。
        await store.ApplyHeadquartersProfileAsync("S001", SampleFields, 1);

        Assert.Equal(new ReceiptProfileLocalState(1, 0), await store.LoadProfileStateAsync("S001"));
    }

    [Fact]
    public async Task Apply_for_a_store_the_device_is_no_longer_bound_to_writes_nothing()
    {
        var repository = new ProfileTestSettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, ProfileTestData.Auth("S002"));

        var applied = await store.ApplyHeadquartersProfileAsync("S001", SampleFields, 3);

        Assert.False(applied);
        Assert.Equal(0, repository.BatchWriteCount);
        Assert.Empty(repository.Snapshot());
    }

    [Fact]
    public async Task Profile_version_is_only_valid_for_the_bound_store()
    {
        var repository = new ProfileTestSettingsRepository();
        var auth = ProfileTestData.Auth("S001");
        var store = new ReceiptPrinterSettingsStore(repository, auth);
        await store.ApplyHeadquartersProfileAsync("S001", SampleFields, 3);
        await store.MarkProfileAckedAsync("S001", 3);

        auth.Set(new DeviceAuthorizationContext("DEV1", "S002", "HW", "AUTH"));

        // 换店：旧店的版本与资料一律不属于新店，设置页可编辑，同步从版本 0 重新拉取。
        var loaded = await store.LoadAsync();
        Assert.Equal(0, loaded.ProfileVersion);
        Assert.Equal(string.Empty, loaded.BrandName);
        Assert.Equal("S002", loaded.StoreName);
        Assert.Equal(ReceiptProfileLocalState.None, await store.LoadProfileStateAsync("S002"));

        // 同一台设备换回旧店之前，旧店状态没有被读取侧误删。
        Assert.Equal(new ReceiptProfileLocalState(3, 3), await store.LoadProfileStateAsync("S001"));
    }

    [Fact]
    public async Task Save_after_store_change_rebinds_and_clears_the_old_stores_version()
    {
        var repository = new ProfileTestSettingsRepository();
        var auth = ProfileTestData.Auth("S001");
        var store = new ReceiptPrinterSettingsStore(repository, auth);
        await store.ApplyHeadquartersProfileAsync("S001", SampleFields, 4);
        await store.MarkProfileAckedAsync("S001", 4);
        auth.Set(new DeviceAuthorizationContext("DEV1", "S002", "HW", "AUTH"));

        await store.SaveAsync(ReceiptPrinterSettings.Default with { BrandName = "S2 Brand", StoreName = "S2 Store" });

        // 重新绑定到新店时旧店版本必须在同一次写入里清零，否则旧版本会被新店继承而锁成只读。
        Assert.Equal("S002", repository.Peek("ReceiptPrinter:ProfileStoreCode"));
        Assert.Equal("0", repository.Peek("ReceiptPrinter:ProfileVersion"));
        Assert.Equal("0", repository.Peek("ReceiptPrinter:ProfileAckedVersion"));
        var loaded = await store.LoadAsync();
        Assert.Equal(0, loaded.ProfileVersion);
        Assert.Equal("S2 Brand", loaded.BrandName);
        Assert.Equal(ReceiptProfileLocalState.None, await store.LoadProfileStateAsync("S002"));
    }

    [Fact]
    public async Task Save_while_profile_is_headquarters_managed_only_writes_hardware_settings()
    {
        var repository = new ProfileTestSettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, ProfileTestData.Auth("S001"));
        await store.ApplyHeadquartersProfileAsync("S001", SampleFields, 3);
        var writesBefore = repository.BatchWriteCount;

        // 设置页里可能是过期的草稿（甚至被改过）：保存只落硬件设置，资料字段与版本保持下发内容。
        await store.SaveAsync(ReceiptPrinterSettings.Default with
        {
            PrinterPort = "USB,COM7",
            CutDistance = 90,
            PrintBankReceiptText = false,
            BrandName = "STALE DRAFT",
            StoreName = "STALE DRAFT",
            StoreAddress = "STALE DRAFT",
            StorePhone = "STALE DRAFT",
            Abn = "STALE DRAFT",
            ReturnPolicy = "STALE DRAFT",
            ProfileVersion = 99
        });

        Assert.Equal(writesBefore + 1, repository.BatchWriteCount);
        var loaded = await store.LoadAsync();
        Assert.Equal("USB,COM7", loaded.PrinterPort);
        Assert.Equal(90, loaded.CutDistance);
        Assert.False(loaded.PrintBankReceiptText);
        Assert.Equal("HQ Brand", loaded.BrandName);
        Assert.Equal("Sunnybank", loaded.StoreName);
        Assert.Equal("Shop 1\r\nBrisbane", loaded.StoreAddress);
        Assert.Equal("07 3000 0000", loaded.StorePhone);
        Assert.Equal("12 345 678 901", loaded.Abn);
        Assert.Equal("Return within 7 days", loaded.ReturnPolicy);
        Assert.Equal(3, loaded.ProfileVersion);
    }

    [Fact]
    public async Task Save_without_headquarters_profile_still_writes_profile_fields_and_ignores_passed_version()
    {
        var repository = new ProfileTestSettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, ProfileTestData.Auth("S001"));

        await store.SaveAsync(ReceiptPrinterSettings.Default with
        {
            BrandName = "Manual Brand",
            StoreName = "Manual Store",
            ProfileVersion = 9
        });

        // 从未下发：行为与引入下发之前完全一致（可手工保存资料），且调用方传入的版本不会被当真。
        var loaded = await store.LoadAsync();
        Assert.Equal("Manual Brand", loaded.BrandName);
        Assert.Equal("Manual Store", loaded.StoreName);
        Assert.Equal(0, loaded.ProfileVersion);
        Assert.Equal(1, repository.BatchWriteCount);
    }

    [Fact]
    public async Task Mark_acked_only_records_the_currently_applied_version()
    {
        var repository = new ProfileTestSettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, ProfileTestData.Auth("S001"));
        await store.ApplyHeadquartersProfileAsync("S001", SampleFields, 3);

        // 回执期间资料已是别的版本：旧回执不能算在新版本头上。
        Assert.False(await store.MarkProfileAckedAsync("S001", 2));
        Assert.Equal(new ReceiptProfileLocalState(3, 0), await store.LoadProfileStateAsync("S001"));

        Assert.True(await store.MarkProfileAckedAsync("S001", 3));
        var state = await store.LoadProfileStateAsync("S001");
        Assert.Equal(new ReceiptProfileLocalState(3, 3), state);
        Assert.False(state.NeedsAck);
    }

    [Fact]
    public async Task Mark_acked_ignores_a_store_the_device_is_not_bound_to()
    {
        var repository = new ProfileTestSettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, ProfileTestData.Auth("S001"));
        await store.ApplyHeadquartersProfileAsync("S001", SampleFields, 3);

        Assert.False(await store.MarkProfileAckedAsync("S002", 3));
        Assert.Equal(new ReceiptProfileLocalState(3, 0), await store.LoadProfileStateAsync("S001"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-3")]
    [InlineData("0")]
    [InlineData("")]
    public async Task Corrupt_version_value_reads_as_never_applied(string stored)
    {
        var repository = new ProfileTestSettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, ProfileTestData.Auth("S001"));
        await store.ApplyHeadquartersProfileAsync("S001", SampleFields, 3);
        await repository.SetValueAsync("ReceiptPrinter:ProfileVersion", stored);

        // 脏值不能把资料锁成只读：按 0 处理，设置页恢复可编辑。
        Assert.Equal(0, (await store.LoadAsync()).ProfileVersion);
        Assert.Equal(0, (await store.LoadProfileStateAsync("S001")).Version);
    }

    [Fact]
    public async Task Unscoped_legacy_store_never_reports_a_profile_version()
    {
        var repository = new ProfileTestSettingsRepository();
        await repository.SetValueAsync("ReceiptPrinter:ProfileVersion", "5");
        var store = new ReceiptPrinterSettingsStore(repository);

        Assert.Equal(0, (await store.LoadAsync()).ProfileVersion);
    }

    [Fact]
    public async Task Hardware_settings_survive_apply()
    {
        var repository = new ProfileTestSettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, ProfileTestData.Auth("S001"));
        await store.SaveAsync(ReceiptPrinterSettings.Default with
        {
            PrinterPort = "USB,COM3",
            CutDistance = 80,
            PrintBankReceiptText = false
        });

        await store.ApplyHeadquartersProfileAsync("S001", SampleFields, 2);

        var loaded = await store.LoadAsync();
        Assert.Equal("USB,COM3", loaded.PrinterPort);
        Assert.Equal(80, loaded.CutDistance);
        Assert.False(loaded.PrintBankReceiptText);
        Assert.Equal("HQ Brand", loaded.BrandName);
    }

    [Fact]
    public async Task Applied_profile_and_versions_survive_an_app_restart_in_the_real_sqlite_store()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-receipt-profile-{Guid.NewGuid():N}.db");
        try
        {
            var first = new ReceiptPrinterSettingsStore(
                new LocalAppSettingsRepository(new LocalSqliteStore(databasePath)),
                ProfileTestData.Auth("S001"));
            await first.ApplyHeadquartersProfileAsync("S001", SampleFields, 3);
            await first.MarkProfileAckedAsync("S001", 3);

            // 模拟重启：全新的连接、仓储与存储实例读同一个库文件。
            var restarted = new ReceiptPrinterSettingsStore(
                new LocalAppSettingsRepository(new LocalSqliteStore(databasePath)),
                ProfileTestData.Auth("S001"));
            var loaded = await restarted.LoadAsync();

            Assert.Equal(3, loaded.ProfileVersion);
            Assert.Equal("HQ Brand", loaded.BrandName);
            Assert.Equal("Shop 1\r\nBrisbane", loaded.StoreAddress);
            Assert.Equal(new ReceiptProfileLocalState(3, 3), await restarted.LoadProfileStateAsync("S001"));
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Save_and_apply_are_serialized_so_fields_and_version_never_disagree()
    {
        var repository = new ProfileTestSettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, ProfileTestData.Auth("S001"));
        var release = repository.BlockNextBatch();

        // 保存先读到「从未下发」并停在写入中；此时同步写入新版本必须等保存完成，
        // 否则保存会用旧草稿覆盖同步刚写的六个字段，而版本仍是新的。
        var save = store.SaveAsync(ReceiptPrinterSettings.Default with { BrandName = "Stale Draft", StoreName = "Draft" });
        await repository.BlockedWriteEntered.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var apply = store.ApplyHeadquartersProfileAsync("S001", SampleFields, 3);

        await Task.Delay(100);
        Assert.Equal(1, repository.BatchWriteCount);
        Assert.False(apply.IsCompleted);

        release.SetResult();
        await save.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.True(await apply.WaitAsync(AsyncTestWaitSupport.DefaultTimeout));

        var loaded = await store.LoadAsync();
        Assert.Equal(3, loaded.ProfileVersion);
        Assert.Equal("HQ Brand", loaded.BrandName);
        Assert.Equal("Sunnybank", loaded.StoreName);
    }
}
