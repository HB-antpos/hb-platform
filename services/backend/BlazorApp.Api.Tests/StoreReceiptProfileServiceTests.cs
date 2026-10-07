using BlazorApp.Api.Services.StoreReceiptProfiles;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 门店小票资料下发服务（SQLite）：status 三态、设备应用情况的内存关联、publish 版本号与原子性、
/// 守卫口径、并发冲突映射。主库与 POSM 库各用一个 SQLite 文件，和生产两库分离的结构一致。
/// </summary>
public sealed class StoreReceiptProfileServiceTests : IDisposable
{
    private readonly string _mainDbPath;
    private readonly string _posmDbPath;
    private readonly SqliteConnection _mainConnection;
    private readonly SqliteConnection _posmConnection;
    private readonly SqlSugarClient _mainDb;
    private readonly SqlSugarClient _posmDb;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero));

    public StoreReceiptProfileServiceTests()
    {
        _mainDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-main.db");
        _posmDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-posm.db");
        _mainConnection = new SqliteConnection($"Data Source={_mainDbPath}");
        _posmConnection = new SqliteConnection($"Data Source={_posmDbPath}");
        _mainConnection.Open();
        _posmConnection.Open();
        _mainDb = CreateClient(_mainConnection.ConnectionString);
        _posmDb = CreateClient(_posmConnection.ConnectionString);
        // 两张新表主键不是自增，可直接 CodeFirst；生产由版本号迁移建表。
        _mainDb.CodeFirst.InitTables<Store, StoreReceiptProfileRelease, PosReceiptProfileAck>();
        _posmDb.CodeFirst.InitTables<POSM_设备注册信息表>();
    }

    // ───────────────────────── status ─────────────────────────

    [Fact]
    public async Task Status_从未下发_synced_pending三态各自正确()
    {
        var never = await SeedStoreAsync("g-never", "S001", "新店");
        var synced = await SeedStoreAsync("g-synced", "S002", "同步店", address: "1 Main St");
        var pending = await SeedStoreAsync("g-pending", "S003", "待发店", phone: "0400");
        await PublishAsync("g-synced", "g-pending");
        // pending：下发后又改了电话；synced：下发后没动。
        pending.Phone = "0499";
        await _mainDb.Updateable(pending).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.GetStatusAsync(new[] { "g-never", "g-synced", "g-pending" });

        Assert.True(result.Success);
        var items = result.Data!;
        Assert.Equal(new[] { "g-never", "g-synced", "g-pending" }, items.Select(item => item.StoreGuid));
        Assert.Equal(StoreReceiptProfileStatuses.Never, items[0].Status);
        Assert.Equal(0, items[0].LatestVersion);
        Assert.Null(items[0].Latest);
        Assert.Null(items[0].PublishedAtUtc);
        Assert.Null(items[0].PublishedBy);
        Assert.Equal("新店", items[0].Current.StoreName);
        Assert.Equal(0, items[0].DeviceApplied);

        Assert.Equal(StoreReceiptProfileStatuses.Synced, items[1].Status);
        Assert.Equal(1, items[1].LatestVersion);
        Assert.Equal("1 Main St", items[1].Latest!.Address);
        Assert.Equal(new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc), items[1].PublishedAtUtc);
        Assert.Equal("tester", items[1].PublishedBy);

        Assert.Equal(StoreReceiptProfileStatuses.Pending, items[2].Status);
        Assert.Equal("0499", items[2].Current.Phone);
        Assert.Equal("0400", items[2].Latest!.Phone);
        Assert.Equal("S003", items[2].StoreCode);
    }

    [Fact]
    public async Task Status_null与空串与纯空白视为相同_大小写与实质差异视为不同()
    {
        var store = await SeedStoreAsync("g-1", "S001", "门店", brandName: null, address: null);
        await PublishAsync("g-1");
        var service = CreateService();

        async Task<string> StatusAfterAsync(Action<Store> change)
        {
            change(store);
            await _mainDb.Updateable(store).ExecuteCommandAsync();
            var result = await service.GetStatusAsync(new[] { "g-1" });
            return result.Data!.Single().Status;
        }

        Assert.Equal(StoreReceiptProfileStatuses.Synced, await StatusAfterAsync(s => s.BrandName = ""));
        Assert.Equal(StoreReceiptProfileStatuses.Synced, await StatusAfterAsync(s => s.BrandName = "   "));
        Assert.Equal(StoreReceiptProfileStatuses.Synced, await StatusAfterAsync(s => s.Address = " \t "));
        // 首尾空白不算实质修改。
        Assert.Equal(StoreReceiptProfileStatuses.Synced, await StatusAfterAsync(s => s.StoreName = "  门店  "));
        Assert.Equal(StoreReceiptProfileStatuses.Pending, await StatusAfterAsync(s => s.BrandName = "HB"));
        // 快照里 BrandName 仍为 null，Store 改成 "HB" 即 pending；再下发后改大小写也是 pending（区分大小写）。
        await PublishAsync("g-1");
        Assert.Equal(StoreReceiptProfileStatuses.Synced, await StatusAfterAsync(s => s.BrandName = "HB "));
        Assert.Equal(StoreReceiptProfileStatuses.Pending, await StatusAfterAsync(s => s.BrandName = "hb"));
    }

    [Fact]
    public async Task Status_空串快照与null当前值一致_确认框里current与latest同口径()
    {
        // 即使 Store 里存的是空白字符串，current 也输出归一后的 null，与 latest 逐字段相同。
        await SeedStoreAsync("g-1", "S001", "门店", brandName: "  ", phone: "");
        await PublishAsync("g-1");

        var item = (await CreateService().GetStatusAsync(new[] { "g-1" })).Data!.Single();

        Assert.Equal(StoreReceiptProfileStatuses.Synced, item.Status);
        Assert.Null(item.Current.BrandName);
        Assert.Null(item.Current.Phone);
        Assert.Null(item.Latest!.BrandName);
        Assert.Null(item.Latest.Phone);
    }

    [Fact]
    public async Task Status_找不到或已软删的门店不返回且顺序与请求一致()
    {
        await SeedStoreAsync("g-a", "S001", "A");
        await SeedStoreAsync("g-deleted", "S002", "已删", isDeleted: true);
        await SeedStoreAsync("g-b", "S003", "B");

        var result = await CreateService().GetStatusAsync(new[] { "g-b", "g-missing", "g-deleted", "g-a" });

        Assert.True(result.Success);
        Assert.Equal(new[] { "g-b", "g-a" }, result.Data!.Select(item => item.StoreGuid));
    }

    [Fact]
    public async Task Status_全部找不到时返回空数组而不是错误()
    {
        var result = await CreateService().GetStatusAsync(new[] { "nobody" });

        Assert.True(result.Success);
        Assert.Empty(result.Data!);
    }

    [Fact]
    public async Task Status_停用门店仍可查询状态()
    {
        await SeedStoreAsync("g-off", "S001", "停用店", isActive: false);

        var result = await CreateService().GetStatusAsync(new[] { "g-off" });

        Assert.Single(result.Data!);
    }

    [Fact]
    public async Task Status_deviceTotal只算POS且启用_deviceApplied按已应用最新版本内存关联()
    {
        await SeedStoreAsync("g-1", "S001", "门店", address: "A");
        await SeedStoreAsync("g-2", "S002", "别店", address: "B");
        await PublishAsync("g-1"); // S001 → v1
        await PublishAsync("g-2"); // S002 → v1
        var store1 = await _mainDb.Queryable<Store>().FirstAsync(store => store.StoreGUID == "g-1");
        store1.Address = "A2";
        await _mainDb.Updateable(store1).ExecuteCommandAsync();
        await PublishAsync("g-1"); // S001 → v2

        await SeedDeviceAsync("dev-ok-new", "S001", "POS", "Windows", 1);
        await SeedDeviceAsync("dev-ok-old", "S001", "POS", "Android", 1);
        await SeedDeviceAsync("dev-no-ack", "S001", "POS", "iPadOS", 1);
        await SeedDeviceAsync("dev-lowercase", "S001", "pos", "Windows", 1);
        await SeedDeviceAsync("dev-disabled", "S001", "POS", "Windows", 0);
        await SeedDeviceAsync("dev-locked", "S001", "POS", "Windows", 2);
        await SeedDeviceAsync("dev-pending", "S001", "POS", "Windows", -1);
        await SeedDeviceAsync("dev-pda", "S001", "PDA", "Android", 1);
        await SeedDeviceAsync("dev-other-store", "S002", "POS", "Windows", 1);
        await SeedAckAsync("dev-ok-new", "S001", 2);
        await SeedAckAsync("dev-ok-old", "S001", 1); // 落后一版：不算已应用
        await SeedAckAsync("dev-lowercase", "S001", 5); // 版本超前也算已应用（>=）
        await SeedAckAsync("dev-disabled", "S001", 2); // 非启用设备不计入
        await SeedAckAsync("dev-pda", "S001", 2);
        await SeedAckAsync("dev-other-store", "S002", 1);

        var items = (await CreateService().GetStatusAsync(new[] { "g-1", "g-2" })).Data!;

        var first = items[0];
        Assert.Equal(2, first.LatestVersion);
        // 启用的 POS：ok-new / ok-old / no-ack / lowercase；禁用、锁定、待确认、PDA、别店都不计。
        Assert.Equal(4, first.DeviceTotal);
        Assert.Equal(2, first.DeviceApplied);
        var second = items[1];
        Assert.Equal(1, second.DeviceTotal);
        Assert.Equal(1, second.DeviceApplied);
    }

    [Fact]
    public async Task Status_从未下发时deviceApplied恒为0即使存在回执()
    {
        await SeedStoreAsync("g-1", "S001", "门店");
        await SeedDeviceAsync("dev-1", "S001", "POS", "Windows", 1);
        await SeedAckAsync("dev-1", "S001", 3);

        var item = (await CreateService().GetStatusAsync(new[] { "g-1" })).Data!.Single();

        Assert.Equal(1, item.DeviceTotal);
        Assert.Equal(0, item.DeviceApplied);
    }

    [Fact]
    public async Task Status_设备换店后旧店回执不计入新店()
    {
        // 版本号是「每家店各自从 1 递增」：设备从 S001 搬到 S002，Ack 里还留着 S001 的 v9，
        // 对 S002 没有任何意义，不能因为 9 >= S002 的最新版本就算「已应用」。
        await SeedStoreAsync("g-1", "S001", "旧店", address: "A");
        await SeedStoreAsync("g-2", "S002", "新店", address: "B");
        await PublishAsync("g-1");
        await PublishAsync("g-2");
        await SeedDeviceAsync("dev-moved", "S002", "POS", "Windows", 1);
        await SeedAckAsync("dev-moved", "S001", 9);
        await SeedDeviceAsync("dev-fresh", "S002", "POS", "Android", 1);
        await SeedAckAsync("dev-fresh", "S002", 1);
        var service = CreateService();

        // 两家店一起查（旧店回执会被读到、但门店不一致）与单独查新店（旧店回执根本不会被读到）结果相同。
        foreach (var guids in new[] { new[] { "g-1", "g-2" }, new[] { "g-2" } })
        {
            var items = (await service.GetStatusAsync(guids)).Data!;
            var newStore = items.Single(item => item.StoreGuid == "g-2");
            Assert.Equal(2, newStore.DeviceTotal);
            Assert.Equal(1, newStore.DeviceApplied);
        }

        var devices = (await service.GetDevicesAsync("g-2")).Data!.Devices;
        var moved = devices.Single(device => device.DeviceCode == "dev-moved");
        Assert.Null(moved.AppliedVersion);
        Assert.Null(moved.AppliedAtUtc);
        Assert.False(moved.UpToDate);
        var fresh = devices.Single(device => device.DeviceCode == "dev-fresh");
        Assert.Equal(1, fresh.AppliedVersion);
        Assert.True(fresh.UpToDate);
    }

    [Fact]
    public async Task Status_回执门店代码比较忽略大小写与首尾空白_真正不同的门店不算()
    {
        await SeedStoreAsync("g-1", "S001", "门店");
        await PublishAsync("g-1");
        await SeedDeviceAsync("dev-case", "S001", "POS", "Windows", 1);
        await SeedDeviceAsync("dev-other", "S001", "POS", "Windows", 1);
        // 回执 StoreCode 写成小写加空白，仍属于同一家店；写成别的店则不算。
        await SeedAckAsync("dev-case", " s001 ", 1);
        await SeedAckAsync("dev-other", "S002", 1);

        var item = (await CreateService().GetStatusAsync(new[] { "g-1" })).Data!.Single();
        var devices = (await CreateService().GetDevicesAsync("g-1")).Data!.Devices;

        Assert.Equal(2, item.DeviceTotal);
        Assert.Equal(1, item.DeviceApplied);
        Assert.True(devices.Single(device => device.DeviceCode == "dev-case").UpToDate);
        Assert.Null(devices.Single(device => device.DeviceCode == "dev-other").AppliedVersion);
    }

    // ───────────────────────── devices ─────────────────────────

    [Fact]
    public async Task Devices_门店不存在或已软删返回STORE_NOT_FOUND()
    {
        await SeedStoreAsync("g-deleted", "S001", "已删", isDeleted: true);
        var service = CreateService();

        var missing = await service.GetDevicesAsync("g-missing");
        var deleted = await service.GetDevicesAsync("g-deleted");
        var blank = await service.GetDevicesAsync("  ");

        foreach (var result in new[] { missing, deleted, blank })
        {
            Assert.False(result.Success);
            Assert.Equal(StoreReceiptProfileErrorCodes.StoreNotFound, result.ErrorCode);
        }
    }

    [Fact]
    public async Task Devices_只列该店启用的POS设备并给出clientKind与upToDate()
    {
        await SeedStoreAsync("g-1", "S001", "门店", address: "A");
        await PublishAsync("g-1");
        var store = await _mainDb.Queryable<Store>().FirstAsync(item => item.StoreGUID == "g-1");
        store.Address = "A2";
        await _mainDb.Updateable(store).ExecuteCommandAsync();
        await PublishAsync("g-1"); // v2
        await SeedDeviceAsync("dev-b", "S001", "POS", "Android", 1, isOnline: true, heartbeat: new DateTime(2026, 10, 7, 1, 2, 3));
        await SeedDeviceAsync("dev-a", "S001", "POS", "Windows", 1);
        await SeedDeviceAsync("dev-c", "S001", "POS", "iOS", 1);
        await SeedDeviceAsync("dev-d", "S001", "POS", "Mac", 1);
        await SeedDeviceAsync("dev-off", "S001", "POS", "Windows", 0);
        await SeedDeviceAsync("dev-pda", "S001", "PDA", "Android", 1);
        await SeedDeviceAsync("dev-elsewhere", "S002", "POS", "Windows", 1);
        await SeedAckAsync("dev-a", "S001", 2);
        await SeedAckAsync("dev-b", "S001", 1);

        var result = await CreateService().GetDevicesAsync("g-1");

        Assert.True(result.Success);
        var data = result.Data!;
        Assert.Equal("g-1", data.StoreGuid);
        Assert.Equal("S001", data.StoreCode);
        Assert.Equal(2, data.LatestVersion);
        Assert.Equal(new[] { "dev-a", "dev-b", "dev-c", "dev-d" }, data.Devices.Select(device => device.DeviceCode));

        var a = data.Devices[0];
        Assert.Equal("wpf", a.ClientKind);
        Assert.Equal("Windows", a.DeviceSystem);
        Assert.Equal(1, a.DeviceStatus);
        Assert.False(a.IsOnline);
        Assert.Null(a.LastHeartbeatAt);
        Assert.Equal(2, a.AppliedVersion);
        Assert.Equal(DateTimeKind.Utc, a.AppliedAtUtc!.Value.Kind);
        Assert.True(a.UpToDate);

        var b = data.Devices[1];
        Assert.Equal("handheld", b.ClientKind);
        Assert.True(b.IsOnline);
        Assert.Equal(new DateTime(2026, 10, 7, 1, 2, 3), b.LastHeartbeatAt);
        Assert.Equal(1, b.AppliedVersion);
        Assert.False(b.UpToDate); // 落后一版

        Assert.Equal("ipad", data.Devices[2].ClientKind);
        Assert.Null(data.Devices[2].AppliedVersion);
        Assert.False(data.Devices[2].UpToDate);
        Assert.Equal("other", data.Devices[3].ClientKind);
    }

    [Fact]
    public async Task Devices_从未下发时latestVersion为0且全部不是upToDate()
    {
        await SeedStoreAsync("g-1", "S001", "门店");
        await SeedDeviceAsync("dev-1", "S001", "POS", "Windows", 1);
        await SeedAckAsync("dev-1", "S001", 4);

        var data = (await CreateService().GetDevicesAsync("g-1")).Data!;

        Assert.Equal(0, data.LatestVersion);
        var device = Assert.Single(data.Devices);
        Assert.Equal(4, device.AppliedVersion);
        Assert.False(device.UpToDate);
    }

    [Theory]
    [InlineData("Windows", "wpf")]
    [InlineData("windows", "wpf")]
    [InlineData(" WINDOWS ", "wpf")]
    [InlineData("iPadOS", "ipad")]
    [InlineData("ipados", "ipad")]
    [InlineData("iOS", "ipad")]
    [InlineData("IOS", "ipad")]
    [InlineData("Android", "handheld")]
    [InlineData("android", "handheld")]
    [InlineData("Mac", "other")]
    [InlineData("", "other")]
    [InlineData(null, "other")]
    public void ResolveClientKind_与回执写入口径一致(string? deviceSystem, string expected)
    {
        Assert.Equal(expected, StoreReceiptProfileService.ResolveClientKind(deviceSystem));
    }

    // ───────────────────────── publish ─────────────────────────

    [Fact]
    public async Task Publish_首次下发版本为1_再次无变化为unchanged不生成新版本_有变化版本加1()
    {
        await SeedStoreAsync("g-1", "S001", "门店", brandName: "HB", address: "A");
        var service = CreateService();

        var first = await service.PublishAsync(new[] { "g-1" }, "alice");
        Assert.True(first.Success);
        Assert.Equal(1, first.Data!.RequestedCount);
        Assert.Equal(1, first.Data.PublishedCount);
        Assert.Equal(0, first.Data.UnchangedCount);
        var item = Assert.Single(first.Data.Items);
        Assert.Equal(("g-1", "S001", "published", 1), (item.StoreGuid, item.StoreCode, item.Outcome, item.Version));

        var again = await service.PublishAsync(new[] { "g-1" }, "alice");
        Assert.True(again.Success);
        Assert.Equal(0, again.Data!.PublishedCount);
        Assert.Equal(1, again.Data.UnchangedCount);
        Assert.Equal(("unchanged", 1), (again.Data.Items[0].Outcome, again.Data.Items[0].Version));
        Assert.Equal(1, await _mainDb.Queryable<StoreReceiptProfileRelease>().CountAsync());

        _time.Advance(TimeSpan.FromMinutes(5));
        var store = await _mainDb.Queryable<Store>().FirstAsync(row => row.StoreGUID == "g-1");
        store.ReturnPolicy = "7 days";
        await _mainDb.Updateable(store).ExecuteCommandAsync();
        var third = await service.PublishAsync(new[] { "g-1" }, "bob");
        Assert.Equal(("published", 2), (third.Data!.Items[0].Outcome, third.Data.Items[0].Version));

        var rows = await _mainDb.Queryable<StoreReceiptProfileRelease>().OrderBy(row => row.Version).ToListAsync();
        Assert.Equal(new[] { 1, 2 }, rows.Select(row => row.Version));
        Assert.Equal("alice", rows[0].PublishedBy);
        Assert.Equal("bob", rows[1].PublishedBy);
        Assert.Null(rows[0].ReturnPolicy);
        Assert.Equal("7 days", rows[1].ReturnPolicy);
        Assert.Equal(new DateTime(2026, 10, 7, 3, 5, 0), rows[1].PublishedAtUtc);
        Assert.Equal("HB", rows[1].BrandName);
        Assert.Equal("A", rows[1].Address);
    }

    [Fact]
    public async Task Publish_批量混合_新版本与unchanged各自计数且顺序与请求一致()
    {
        await SeedStoreAsync("g-1", "S001", "一号");
        await SeedStoreAsync("g-2", "S002", "二号");
        await SeedStoreAsync("g-3", "S003", "三号");
        var service = CreateService();
        await service.PublishAsync(new[] { "g-2" }, "alice"); // S002 已是最新
        var changed = await _mainDb.Queryable<Store>().FirstAsync(row => row.StoreGUID == "g-3");
        await service.PublishAsync(new[] { "g-3" }, "alice");
        changed.Phone = "0400";
        await _mainDb.Updateable(changed).ExecuteCommandAsync(); // S003 将是 v2

        var result = await service.PublishAsync(new[] { "g-3", "g-1", "g-2" }, "alice");

        Assert.True(result.Success);
        Assert.Equal(3, result.Data!.RequestedCount);
        Assert.Equal(2, result.Data.PublishedCount);
        Assert.Equal(1, result.Data.UnchangedCount);
        Assert.Equal(
            new[] { ("g-3", "published", 2), ("g-1", "published", 1), ("g-2", "unchanged", 1) },
            result.Data.Items.Select(item => (item.StoreGuid, item.Outcome, item.Version)).ToArray()
        );
    }

    [Fact]
    public async Task Publish_快照存归一后的值_首尾空白去掉_空白可空字段存null()
    {
        await SeedStoreAsync("g-1", "S001", "  门店  ", brandName: "   ", address: "  1 Main St\r\nSydney  ", phone: " 02 ", abn: "", returnPolicy: "\t7 days\t");

        await CreateService().PublishAsync(new[] { "g-1" }, "alice");

        var row = await _mainDb.Queryable<StoreReceiptProfileRelease>().SingleAsync();
        Assert.Equal("门店", row.StoreName);
        Assert.Null(row.BrandName);
        Assert.Equal("1 Main St\r\nSydney", row.Address);
        Assert.Equal("02", row.Phone);
        Assert.Null(row.ABN);
        Assert.Equal("7 days", row.ReturnPolicy);
    }

    [Fact]
    public async Task Publish_PublishedBy截断到100个字符()
    {
        await SeedStoreAsync("g-1", "S001", "门店");

        await CreateService().PublishAsync(new[] { "g-1" }, new string('x', 150));

        var row = await _mainDb.Queryable<StoreReceiptProfileRelease>().SingleAsync();
        Assert.Equal(100, row.PublishedBy!.Length);
    }

    [Fact]
    public async Task Publish_批量原子_一家店非法则整批不写入并列出全部原因()
    {
        await SeedStoreAsync("g-ok", "S001", "好店");
        await SeedStoreAsync("g-bad", "S002", "坏\u0007店");
        await SeedStoreAsync("g-off", "S003", "停用店", isActive: false);
        var service = CreateService();

        var result = await service.PublishAsync(new[] { "g-ok", "g-bad", "g-off", "g-missing" }, "alice");

        Assert.False(result.Success);
        Assert.Equal(StoreReceiptProfileErrorCodes.NotPublishable, result.ErrorCode);
        Assert.Null(result.Data);
        var details = Assert.IsType<List<StoreReceiptProfilePublishErrorDetailDto>>(result.Details);
        Assert.Equal(
            new[]
            {
                ("g-bad", "S002", StoreReceiptProfileErrorCodes.InvalidCharacters),
                ("g-off", "S003", StoreReceiptProfileErrorCodes.StoreInactive),
                ("g-missing", (string?)null, StoreReceiptProfileErrorCodes.StoreNotFound),
            },
            details.Select(detail => (detail.StoreGuid, detail.StoreCode, detail.ErrorCode)).ToArray()
        );
        Assert.All(details, detail => Assert.False(string.IsNullOrWhiteSpace(detail.Message)));
        // 好店也不能被写入。
        Assert.Equal(0, await _mainDb.Queryable<StoreReceiptProfileRelease>().CountAsync());
    }

    [Theory]
    [InlineData("storeName", "门\u0003店")]
    [InlineData("brandName", "品\u0009牌")] // 单行字段连 TAB 也不行
    [InlineData("brandName", "品\n牌")]
    [InlineData("phone", "02\r12")]
    [InlineData("abn", "12\u001f34")]
    [InlineData("address", "地址\u0001")]
    [InlineData("address", "地址\u007f")]
    [InlineData("returnPolicy", "政策\u001b")]
    [InlineData("returnPolicy", "政策\u0085")]
    public async Task Publish_含控制字符被拒绝_口径与Hbpos守卫一致(string field, string value)
    {
        await SeedStoreAsync("g-1", "S001", "门店", configure: store =>
        {
            switch (field)
            {
                case "storeName": store.StoreName = value; break;
                case "brandName": store.BrandName = value; break;
                case "phone": store.Phone = value; break;
                case "abn": store.ABN = value; break;
                case "address": store.Address = value; break;
                case "returnPolicy": store.ReturnPolicy = value; break;
            }
        });

        var result = await CreateService().PublishAsync(new[] { "g-1" }, "alice");

        Assert.False(result.Success);
        Assert.Equal(StoreReceiptProfileErrorCodes.NotPublishable, result.ErrorCode);
        var detail = Assert.Single(Assert.IsType<List<StoreReceiptProfilePublishErrorDetailDto>>(result.Details));
        Assert.Equal(StoreReceiptProfileErrorCodes.InvalidCharacters, detail.ErrorCode);
        Assert.Equal(0, await _mainDb.Queryable<StoreReceiptProfileRelease>().CountAsync());
    }

    [Fact]
    public async Task Publish_地址与退货政策允许换行与制表符()
    {
        await SeedStoreAsync("g-1", "S001", "门店", address: "1 Main St\r\nSydney\tNSW", returnPolicy: "Line1\nLine2\tend");

        var result = await CreateService().PublishAsync(new[] { "g-1" }, "alice");

        Assert.True(result.Success, result.Message);
        var row = await _mainDb.Queryable<StoreReceiptProfileRelease>().SingleAsync();
        Assert.Equal("1 Main St\r\nSydney\tNSW", row.Address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Publish_门店名称为空或纯空白被拒绝(string storeName)
    {
        await SeedStoreAsync("g-1", "S001", storeName);

        var result = await CreateService().PublishAsync(new[] { "g-1" }, "alice");

        Assert.False(result.Success);
        var detail = Assert.Single(Assert.IsType<List<StoreReceiptProfilePublishErrorDetailDto>>(result.Details));
        Assert.Equal(StoreReceiptProfileErrorCodes.StoreNameRequired, detail.ErrorCode);
    }

    [Fact]
    public async Task Publish_门店代码为空或含控制字符被拒绝()
    {
        await SeedStoreAsync("g-empty", "", "无代码店");
        await SeedStoreAsync("g-ctrl", "S\u0002001", "控制字符店");
        var service = CreateService();

        var empty = await service.PublishAsync(new[] { "g-empty" }, "alice");
        var ctrl = await service.PublishAsync(new[] { "g-ctrl" }, "alice");

        Assert.Equal(
            StoreReceiptProfileErrorCodes.StoreCodeRequired,
            Assert.Single(Assert.IsType<List<StoreReceiptProfilePublishErrorDetailDto>>(empty.Details)).ErrorCode
        );
        Assert.Equal(
            StoreReceiptProfileErrorCodes.InvalidCharacters,
            Assert.Single(Assert.IsType<List<StoreReceiptProfilePublishErrorDetailDto>>(ctrl.Details)).ErrorCode
        );
    }

    [Fact]
    public async Task Publish_已软删门店被拒绝为STORE_NOT_FOUND()
    {
        await SeedStoreAsync("g-del", "S001", "已删", isDeleted: true);

        var result = await CreateService().PublishAsync(new[] { "g-del" }, "alice");

        Assert.False(result.Success);
        var detail = Assert.Single(Assert.IsType<List<StoreReceiptProfilePublishErrorDetailDto>>(result.Details));
        Assert.Equal(StoreReceiptProfileErrorCodes.StoreNotFound, detail.ErrorCode);
    }

    [Fact]
    public async Task Publish_版本号并发冲突返回409语义错误码且整批回滚()
    {
        await SeedStoreAsync("g-1", "S001", "一号");
        await SeedStoreAsync("g-2", "S002", "二号");
        // 模拟「读完最大版本之后、写入之前有人抢先写了 (S002, 1)」：钩子在同一事务里先插入冲突行。
        var service = CreateService(beforeInsert: async () =>
        {
            await _mainDb.Insertable(new StoreReceiptProfileRelease
            {
                StoreCode = "S002",
                Version = 1,
                StoreName = "别人抢先写的",
                PublishedAtUtc = DateTime.UtcNow,
            }).ExecuteCommandAsync();
        });

        var result = await service.PublishAsync(new[] { "g-1", "g-2" }, "alice");

        Assert.False(result.Success);
        Assert.Equal(StoreReceiptProfileErrorCodes.PublishConflict, result.ErrorCode);
        Assert.Null(result.Data);
        // 整批（含没有冲突的 S001 以及钩子里那一行）都随事务回滚。
        Assert.Equal(0, await _mainDb.Queryable<StoreReceiptProfileRelease>().CountAsync());

        // 冲突后重试（无钩子）即可成功，版本号从 1 开始。
        var retry = await CreateService().PublishAsync(new[] { "g-1", "g-2" }, "alice");
        Assert.True(retry.Success);
        Assert.All(retry.Data!.Items, item => Assert.Equal(1, item.Version));
    }

    [Fact]
    public async Task Publish_非冲突异常照常抛出且事务回滚()
    {
        await SeedStoreAsync("g-1", "S001", "一号");
        var service = CreateService(beforeInsert: () => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync(new[] { "g-1" }, "alice"));

        Assert.Equal(0, await _mainDb.Queryable<StoreReceiptProfileRelease>().CountAsync());
        // 回滚后连接可继续正常使用。
        Assert.True((await CreateService().PublishAsync(new[] { "g-1" }, "alice")).Success);
    }

    [Fact]
    public async Task Publish_不改Store本身()
    {
        await SeedStoreAsync("g-1", "S001", "门店", brandName: "  HB  ");

        await CreateService().PublishAsync(new[] { "g-1" }, "alice");

        var store = await _mainDb.Queryable<Store>().SingleAsync();
        Assert.Equal("  HB  ", store.BrandName);
    }

    // ───────────────────────── 请求参数校验 ─────────────────────────

    public static IEnumerable<object?[]> InvalidRequests()
    {
        yield return new object?[] { null };
        yield return new object?[] { Array.Empty<string>() };
        yield return new object?[] { Enumerable.Range(0, 101).Select(index => $"g-{index}").ToArray() };
        yield return new object?[] { new[] { "g-1", "g-1" } };
        yield return new object?[] { new[] { "g-1", " G-1 " } };
        yield return new object?[] { new[] { "g-1", "" } };
        yield return new object?[] { new[] { "g-1", "   " } };
        yield return new object?[] { new string?[] { "g-1", null } };
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Status与Publish_非法请求返回INVALID_RECEIPT_PROFILE_REQUEST(string?[]? guids)
    {
        await SeedStoreAsync("g-1", "S001", "门店");
        var service = CreateService();
        IReadOnlyList<string>? list = guids?.Select(guid => guid!).ToList();

        var status = await service.GetStatusAsync(list);
        var publish = await service.PublishAsync(list, "alice");

        Assert.False(status.Success);
        Assert.Equal(StoreReceiptProfileErrorCodes.InvalidRequest, status.ErrorCode);
        Assert.False(publish.Success);
        Assert.Equal(StoreReceiptProfileErrorCodes.InvalidRequest, publish.ErrorCode);
        Assert.Equal(0, await _mainDb.Queryable<StoreReceiptProfileRelease>().CountAsync());
    }

    [Fact]
    public async Task 请求边界_恰好100个门店可以通过校验()
    {
        var guids = Enumerable.Range(0, 100).Select(index => $"g-{index}").ToList();

        var status = await CreateService().GetStatusAsync(guids);

        Assert.True(status.Success);
        Assert.Empty(status.Data!);
    }

    [Fact]
    public async Task 请求中的GUID首尾空白会被忽略()
    {
        await SeedStoreAsync("g-1", "S001", "门店");

        var status = await CreateService().GetStatusAsync(new[] { " g-1 " });

        Assert.Equal("g-1", Assert.Single(status.Data!).StoreGuid);
    }

    // ───────────────────────── 守卫 ─────────────────────────

    [Theory]
    [InlineData("S001", "门店", "HB", "1 Main\r\nSt\t2", "02 1234", "12345678901", "7 days\nreturn", null)]
    [InlineData("S001", "门店", null, null, null, null, null, null)]
    [InlineData("S\u0001", "门店", null, null, null, null, null, "StoreCode")]
    [InlineData("S001", "门\t店", null, null, null, null, null, "StoreName")]
    [InlineData("S001", "门店", "品\r牌", null, null, null, null, "BrandName")]
    [InlineData("S001", "门店", null, null, "02\n12", null, null, "Phone")]
    [InlineData("S001", "门店", null, null, null, "12\t34", null, "ABN")]
    [InlineData("S001", "门店", null, "地\u0000址", null, null, null, "Address")]
    [InlineData("S001", "门店", null, null, null, null, "政\u009f策", "ReturnPolicy")]
    public void Guard_口径与Hbpos的StoreReceiptProfileGuard一致(
        string storeCode,
        string storeName,
        string? brandName,
        string? address,
        string? phone,
        string? abn,
        string? returnPolicy,
        string? expectedInvalidField)
    {
        Assert.Equal(
            expectedInvalidField,
            StoreReceiptProfileGuard.FindInvalidField(storeCode, storeName, brandName, address, phone, abn, returnPolicy)
        );
    }

    [Fact]
    public void IsPublishConflict_识别主键冲突文案_不误判普通异常()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY); INSERT INTO t VALUES (1);";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO t VALUES (1);";
        var duplicate = Assert.ThrowsAny<Exception>(() => command.ExecuteNonQuery());

        Assert.True(StoreReceiptProfileService.IsPublishConflict(duplicate));
        Assert.True(StoreReceiptProfileService.IsPublishConflict(new InvalidOperationException("outer", duplicate)));
        Assert.False(StoreReceiptProfileService.IsPublishConflict(new InvalidOperationException("other")));
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private StoreReceiptProfileService CreateService(Func<Task>? beforeInsert = null) =>
        new(
            _mainDb,
            _posmDb,
            NullLogger<StoreReceiptProfileService>.Instance,
            _time,
            beforeInsert
        );

    private async Task PublishAsync(params string[] storeGuids)
    {
        var result = await CreateService().PublishAsync(storeGuids, "tester");
        Assert.True(result.Success, result.Message);
    }

    private async Task<Store> SeedStoreAsync(
        string guid,
        string code,
        string name,
        string? brandName = null,
        string? address = null,
        string? phone = null,
        string? abn = null,
        string? returnPolicy = null,
        bool isActive = true,
        bool isDeleted = false,
        Action<Store>? configure = null)
    {
        var store = new Store
        {
            StoreGUID = guid,
            StoreCode = code,
            StoreName = name,
            BrandName = brandName,
            Address = address,
            Phone = phone,
            ABN = abn,
            ReturnPolicy = returnPolicy,
            IsActive = isActive,
            IsDeleted = isDeleted,
        };
        configure?.Invoke(store);
        await _mainDb.Insertable(store).ExecuteCommandAsync();
        return store;
    }

    private Task SeedDeviceAsync(
        string deviceCode,
        string storeCode,
        string deviceType,
        string deviceSystem,
        int status,
        bool isOnline = false,
        DateTime? heartbeat = null) =>
        _posmDb.Insertable(new POSM_设备注册信息表
        {
            设备硬件识别码 = $"hw-{deviceCode}",
            系统设备编号 = deviceCode,
            分店代码 = storeCode,
            设备类型 = deviceType,
            设备系统 = deviceSystem,
            设备状态 = status,
            设备授权码 = "auth",
            是否在线 = isOnline,
            最后心跳时间 = heartbeat,
        }).ExecuteCommandAsync();

    private Task SeedAckAsync(string deviceCode, string storeCode, int appliedVersion) =>
        _mainDb.Insertable(new PosReceiptProfileAck
        {
            DeviceCode = deviceCode,
            StoreCode = storeCode,
            AppliedVersion = appliedVersion,
            AppliedAtUtc = new DateTime(2026, 10, 7, 2, 0, 0),
            ClientKind = "wpf",
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
        TryDelete(_mainDbPath);
        TryDelete(_posmDbPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 临时库删除失败不影响断言。
        }
    }

    /// <summary>可手动推进的时间源，用来断言 PublishedAtUtc。</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
