using BlazorApp.Api.Services.PosSettings;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 收银端设置下发（管理侧）服务（SQLite，主库与 POSM 库各一个文件，与生产两库分离一致）：
/// 三层读写、保存记修改记录、清除、范围与管理员限制、设备应用状态（与合并指纹对齐）。
/// </summary>
public sealed class PosSettingsServiceTests : IDisposable
{
    private readonly string _mainDbPath;
    private readonly string _posmDbPath;
    private readonly SqliteConnection _mainConnection;
    private readonly SqliteConnection _posmConnection;
    private readonly SqlSugarClient _mainDb;
    private readonly SqlSugarClient _posmDb;
    private readonly FixedTime _time = new(new DateTimeOffset(2026, 11, 14, 1, 0, 0, TimeSpan.Zero));

    private static readonly PosSettingsActor Admin = new("admin", true, []);
    private static PosSettingsActor Manager(params string[] stores) => new("manager", false, stores);

    public PosSettingsServiceTests()
    {
        _mainDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-pos-settings-main.db");
        _posmDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-pos-settings-posm.db");
        _mainConnection = new SqliteConnection($"Data Source={_mainDbPath}");
        _posmConnection = new SqliteConnection($"Data Source={_posmDbPath}");
        _mainConnection.Open();
        _posmConnection.Open();
        _mainDb = CreateClient(_mainConnection.ConnectionString);
        _posmDb = CreateClient(_posmConnection.ConnectionString);
        _mainDb.CodeFirst.InitTables<Store, PosSettingAck>();
        _mainDb.CodeFirst.InitTables<PosSettingValue>();
        // 修改记录的主键是 bigint 自增，SQLite 只认 INTEGER PRIMARY KEY，所以测试库手工建这张表（生产由迁移建表）。
        _mainDb.Ado.ExecuteCommand(
            "CREATE TABLE PosSettingChange (Id INTEGER PRIMARY KEY AUTOINCREMENT, Scope TEXT NOT NULL, ScopeKey TEXT NOT NULL, "
            + "SettingKey TEXT NOT NULL, OldValue INTEGER NULL, NewValue INTEGER NULL, ChangedAtUtc TEXT NOT NULL, ChangedBy TEXT NULL)");
        _posmDb.CodeFirst.InitTables<POSM_设备注册信息表>();
        _mainDb.Insertable(new List<Store>
        {
            new() { StoreGUID = "g-1", StoreCode = "S001", StoreName = "一号店" },
            new() { StoreGUID = "g-2", StoreCode = "S002", StoreName = "二号店" },
        }).ExecuteCommand();
    }

    private PosSettingsService CreateService() => new(_mainDb, _posmDb, _time);

    private static SavePosSettingsRequest Save(params (string Key, bool? Value)[] items) =>
        new() { Values = items.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal) };

    // ───────────────────────── 保存与修改记录 ─────────────────────────

    [Fact]
    public async Task SaveStore_写入设置并记修改记录_值没变化不重复记录()
    {
        var service = CreateService();

        var first = await service.SaveStoreAsync(Admin, "S001", Save((PosSettingKeys.CashMovementEnabled, true)), default);
        var again = await service.SaveStoreAsync(Admin, "S001", Save((PosSettingKeys.CashMovementEnabled, true)), default);

        Assert.True(first.Success, first.Message);
        Assert.True(again.Success);
        Assert.True(first.Data!.Overrides[PosSettingKeys.CashMovementEnabled]);
        Assert.Equal(new PosSettingEffectiveValueDto { Value = true, Source = PosSettingScopes.Store }, first.Data.Effective[PosSettingKeys.CashMovementEnabled], new EffectiveComparer());
        var changes = (await service.ListChangesAsync(Admin, null, null, 50, default)).Data!;
        var change = Assert.Single(changes);
        Assert.Null(change.OldValue);
        Assert.True(change.NewValue);
        Assert.Equal("admin", change.ChangedByName);
        Assert.Equal(PosSettingScopes.Store, change.Scope);
        Assert.Equal("S001", change.ScopeKey);
    }

    [Fact]
    public async Task SaveStore_null表示清除_回到跟随全局并记录旧值到清除()
    {
        var service = CreateService();
        await service.SaveGlobalAsync(Admin, Save((PosSettingKeys.ManualCard, false)), default);
        await service.SaveStoreAsync(Admin, "S001", Save((PosSettingKeys.ManualCard, true)), default);

        var cleared = await service.SaveStoreAsync(Admin, "S001", Save((PosSettingKeys.ManualCard, null)), default);

        Assert.Empty(cleared.Data!.Overrides);
        var effective = cleared.Data.Effective[PosSettingKeys.ManualCard];
        Assert.False(effective.Value);
        Assert.Equal(PosSettingScopes.Global, effective.Source);
        var last = (await service.ListChangesAsync(Admin, PosSettingScopes.Store, "S001", 50, default)).Data!.First();
        Assert.True(last.OldValue);
        Assert.Null(last.NewValue);
    }

    [Fact]
    public async Task Save_拒绝未知键_空请求_且不留下任何改动()
    {
        var service = CreateService();

        var unknown = await service.SaveStoreAsync(Admin, "S001", Save((PosSettingKeys.ManualCard, true), ("no.such.key", true)), default);
        var empty = await service.SaveStoreAsync(Admin, "S001", new SavePosSettingsRequest(), default);

        Assert.False(unknown.Success);
        Assert.Equal(PosSettingsErrorCodes.InvalidRequest, unknown.ErrorCode);
        Assert.False(empty.Success);
        Assert.Empty((await service.GetStoreAsync(Admin, "S001", default)).Data!.Overrides);
        Assert.Empty((await service.ListChangesAsync(Admin, null, null, 50, default)).Data!);
    }

    // ───────────────────────── 范围与管理员 ─────────────────────────

    [Fact]
    public async Task SaveGlobal_只有管理员能改()
    {
        var service = CreateService();

        var denied = await service.SaveGlobalAsync(Manager("S001"), Save((PosSettingKeys.ManualCard, true)), default);
        var allowed = await service.SaveGlobalAsync(Admin, Save((PosSettingKeys.ManualCard, true)), default);

        Assert.Equal(PosSettingsErrorCodes.Forbidden, denied.ErrorCode);
        Assert.True(allowed.Success);
        Assert.True(allowed.Data!.Values[PosSettingKeys.ManualCard]);
    }

    [Fact]
    public async Task 店长只能看和改自己关联门店_越权与不存在的门店不暴露区别之外的信息()
    {
        var service = CreateService();
        var manager = Manager("S001");

        var own = await service.SaveStoreAsync(manager, "S001", Save((PosSettingKeys.OrderList, true)), default);
        var other = await service.SaveStoreAsync(manager, "S002", Save((PosSettingKeys.OrderList, true)), default);
        var otherRead = await service.GetStoreAsync(manager, "S002", default);
        var stores = (await service.ListStoresAsync(manager, default)).Data!;
        var adminStores = (await service.ListStoresAsync(Admin, default)).Data!;

        Assert.True(own.Success);
        Assert.Equal(PosSettingsErrorCodes.Forbidden, other.ErrorCode);
        Assert.Equal(PosSettingsErrorCodes.Forbidden, otherRead.ErrorCode);
        Assert.Equal(["S001"], stores.Select(item => item.StoreCode));
        Assert.Equal(["S001", "S002"], adminStores.Select(item => item.StoreCode));
        // 非管理员的修改记录只含自己门店层。
        await service.SaveGlobalAsync(Admin, Save((PosSettingKeys.Voucher, true)), default);
        var visibleChanges = (await service.ListChangesAsync(manager, null, null, 50, default)).Data!;
        Assert.All(visibleChanges, item => Assert.Equal(PosSettingScopes.Store, item.Scope));
    }

    // ───────────────────────── 设备应用情况 ─────────────────────────

    [Fact]
    public async Task 设备状态_从未回执_已应用最新_有新改动变待同步()
    {
        var service = CreateService();
        await SeedDeviceAsync("ORI-01", "S001", "Windows");
        await SeedDeviceAsync("IPAD-02", "S001", "iPadOS");
        await service.SaveStoreAsync(Admin, "S001", Save((PosSettingKeys.CashMovementEnabled, true)), default);

        var before = (await service.ListDevicesAsync(Admin, "S001", default)).Data!;
        Assert.All(before, item => Assert.Equal(PosSettingDeviceStatuses.Never, item.Status));

        // 设备按服务端合并出的指纹回执 → 已应用最新。
        var layer = await service.GetStoreAsync(Admin, "S001", default);
        await SeedAckAsync("ORI-01", "S001", ExpectedHash(layer.Data!.Overrides, PosSettingClientKinds.Wpf), offlineCount: 2);
        var applied = (await service.ListDevicesAsync(Admin, "S001", default)).Data!;
        Assert.Equal(PosSettingDeviceStatuses.Applied, applied.Single(item => item.DeviceCode == "ORI-01").Status);
        Assert.Equal(PosSettingDeviceStatuses.Never, applied.Single(item => item.DeviceCode == "IPAD-02").Status);
        Assert.Equal(2, applied.Single(item => item.DeviceCode == "ORI-01").OfflineOverrideCount);

        // 门店又改了一项：已回执的设备变成「待同步」。
        await service.SaveStoreAsync(Admin, "S001", Save((PosSettingKeys.ManualCard, true)), default);
        var pending = (await service.ListDevicesAsync(Admin, "S001", default)).Data!;
        Assert.Equal(PosSettingDeviceStatuses.Pending, pending.Single(item => item.DeviceCode == "ORI-01").Status);
    }

    [Fact]
    public async Task 设备状态_WPF专属项的改动不影响iPad设备的应用状态()
    {
        var service = CreateService();
        await SeedDeviceAsync("IPAD-02", "S001", "iPadOS");
        await service.SaveStoreAsync(Admin, "S001", Save((PosSettingKeys.CashMovementEnabled, true)), default);
        var store = await service.GetStoreAsync(Admin, "S001", default);
        await SeedAckAsync("IPAD-02", "S001", ExpectedHash(store.Data!.Overrides, PosSettingClientKinds.Ipad));

        // 返回收银再扫码是 WPF 专属项：iPad 的指纹不变，仍然是已应用。
        await service.SaveStoreAsync(Admin, "S001", Save((PosSettingKeys.ReturnsRescan, true)), default);

        var status = (await service.ListDevicesAsync(Admin, "S001", default)).Data!.Single();
        Assert.Equal(PosSettingDeviceStatuses.Applied, status.Status);
    }

    [Fact]
    public async Task 设备换店后旧店的回执不计入新店()
    {
        var service = CreateService();
        await SeedDeviceAsync("ORI-01", "S002", "Windows");
        // 回执里记的是旧店 S001。
        await SeedAckAsync("ORI-01", "S001", ExpectedHash(new Dictionary<string, bool>(), PosSettingClientKinds.Wpf));

        var status = (await service.ListDevicesAsync(Admin, "S002", default)).Data!.Single();

        Assert.Equal(PosSettingDeviceStatuses.Never, status.Status);
        Assert.Null(status.AppliedAtUtc);
    }

    [Fact]
    public async Task 设备覆盖_保存后详情给出应生效值与设备回报()
    {
        var service = CreateService();
        await SeedDeviceAsync("ORI-01", "S001", "Windows");
        await service.SaveGlobalAsync(Admin, Save((PosSettingKeys.Voucher, false)), default);
        await service.SaveStoreAsync(Admin, "S001", Save((PosSettingKeys.Voucher, true)), default);
        await SeedAckAsync("ORI-01", "S001", "0".PadLeft(64, '0'), effectiveJson: "{\"payment.voucher\":false,\"no.such\":true}");

        var saved = await service.SaveDeviceAsync(Manager("S001"), "ORI-01", Save((PosSettingKeys.Voucher, false)), default);

        Assert.True(saved.Success, saved.Message);
        var detail = saved.Data!;
        Assert.True(detail.Status.HasDeviceOverride);
        var effective = detail.Effective[PosSettingKeys.Voucher];
        Assert.False(effective.Value);
        Assert.Equal(PosSettingScopes.Device, effective.Source);
        // 设备回报只保留清单里认识的键。
        Assert.Equal([PosSettingKeys.Voucher], detail.Reported.Keys);
        Assert.False(detail.Reported[PosSettingKeys.Voucher]);
        // 其它门店的店长不能改这台设备。
        var denied = await service.SaveDeviceAsync(Manager("S002"), "ORI-01", Save((PosSettingKeys.Voucher, true)), default);
        Assert.Equal(PosSettingsErrorCodes.Forbidden, denied.ErrorCode);
        var missing = await service.GetDeviceAsync(Admin, "NO-SUCH", default);
        Assert.Equal(PosSettingsErrorCodes.NotFound, missing.ErrorCode);
    }

    [Fact]
    public async Task 门店列表_汇总覆盖项与设备应用数()
    {
        var service = CreateService();
        await SeedDeviceAsync("ORI-01", "S001", "Windows");
        await SeedDeviceAsync("ORI-02", "S001", "Windows");
        await service.SaveStoreAsync(Admin, "S001", Save((PosSettingKeys.CashMovementEnabled, true)), default);
        await service.SaveDeviceAsync(Admin, "ORI-02", Save((PosSettingKeys.CashMovementEnabled, false)), default);
        await SeedAckAsync("ORI-01", "S001", ExpectedHash(new Dictionary<string, bool> { [PosSettingKeys.CashMovementEnabled] = true }, PosSettingClientKinds.Wpf));

        var summary = (await service.ListStoresAsync(Admin, default)).Data!.Single(item => item.StoreCode == "S001");

        Assert.Equal(2, summary.DeviceTotal);
        Assert.Equal(1, summary.DeviceApplied);
        Assert.Equal(1, summary.DeviceOverrideCount);
        Assert.True(summary.Overrides[PosSettingKeys.CashMovementEnabled]);
    }

    // ───────────────────────── 辅助 ─────────────────────────

    /// <summary>按店层设置算「设备该拿到的指纹」：全局空，只有门店层。与 POS API 下发用的是同一份合并 + 指纹代码。</summary>
    private static string ExpectedHash(IReadOnlyDictionary<string, bool> store, string clientKind) =>
        PosSettingMerger.ComputeHash(PosSettingMerger.Managed(PosSettingMerger.Merge(null, store, null, clientKind)));

    private Task SeedDeviceAsync(string deviceCode, string storeCode, string deviceSystem) =>
        _posmDb.Insertable(new POSM_设备注册信息表
        {
            设备硬件识别码 = $"hw-{deviceCode}",
            系统设备编号 = deviceCode,
            分店代码 = storeCode,
            设备类型 = "POS",
            设备系统 = deviceSystem,
            设备状态 = 1,
            设备授权码 = "auth",
        }).ExecuteCommandAsync();

    private Task SeedAckAsync(string deviceCode, string storeCode, string hash, int offlineCount = 0, string? effectiveJson = null) =>
        _mainDb.Insertable(new PosSettingAck
        {
            DeviceCode = deviceCode,
            StoreCode = storeCode,
            AppliedHash = hash,
            EffectiveJson = effectiveJson,
            ClientKind = "wpf",
            AppVersion = "1.0.120",
            AppliedAtUtc = new DateTime(2026, 11, 14, 0, 30, 0),
            OfflineOverrideCount = offlineCount,
        }).ExecuteCommandAsync();

    private static SqlSugarClient CreateClient(string connectionString) =>
        new(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });

    public void Dispose()
    {
        _mainDb.Dispose();
        _posmDb.Dispose();
        _mainConnection.Dispose();
        _posmConnection.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _mainDbPath, _posmDbPath })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 临时库删不掉不影响断言。
            }
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class EffectiveComparer : IEqualityComparer<PosSettingEffectiveValueDto>
    {
        public bool Equals(PosSettingEffectiveValueDto? x, PosSettingEffectiveValueDto? y) =>
            x is not null && y is not null && x.Value == y.Value && x.Source == y.Source;

        public int GetHashCode(PosSettingEffectiveValueDto obj) => HashCode.Combine(obj.Value, obj.Source);
    }
}
