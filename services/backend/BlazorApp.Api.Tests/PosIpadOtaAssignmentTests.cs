using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// iPad OTA 三层决策（设备 → 分店 → 全局基线）与覆盖管理的行为测试。
/// 主库与 POSM 设备库各用一份临时 SQLite，和生产「两库」结构一致。
/// </summary>
public sealed class PosIpadOtaAssignmentTests : IDisposable
{
    private const string Runtime = "3.0.0";
    private const string OldUpdateId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    private readonly string _mainPath =
        Path.Combine(Path.GetTempPath(), $"ota-assign-main-{Guid.NewGuid():N}.db");
    private readonly string _posmPath =
        Path.Combine(Path.GetTempPath(), $"ota-assign-posm-{Guid.NewGuid():N}.db");
    private readonly ISqlSugarClient _db;
    private readonly ISqlSugarClient _posm;

    public PosIpadOtaAssignmentTests()
    {
        _db = NewClient(_mainPath);
        _db.CodeFirst.InitTables(
            typeof(PosIpadOtaRelease),
            typeof(PosIpadOtaRollout),
            typeof(PosIpadOtaRolloutTarget),
            typeof(PosIpadOtaAssignment),
            typeof(Store)
        );
        _posm = NewClient(_posmPath);
        _posm.CodeFirst.InitTables(typeof(POSM_设备注册信息表));
    }

    [Fact]
    public async Task 分店覆盖优先于全局基线_其他分店仍走基线()
    {
        var bri = await SeedStoreAsync("BRI");
        await SeedStoreAsync("SYD");
        var baseline = await SeedReleaseAsync("11111111");
        var pinned = await SeedReleaseAsync("22222222");
        var service = CreateService();
        await EnableBaselineAsync(service, baseline);

        var created = await service.CreateAssignmentAsync(
            StoreAssignment(bri, pinned),
            "admin"
        );
        Assert.True(created.Success, created.Message);

        var briDecision = await service.GetDecisionAsync(Decision("BRI"));
        var sydDecision = await service.GetDecisionAsync(Decision("SYD"));

        Assert.Equal(pinned.IosUpdateId, briDecision.IosUpdateId);
        Assert.Equal(
            created.Data!.PolicyVersion.ToString(),
            briDecision.PolicyVersion
        );
        Assert.Equal(baseline.IosUpdateId, sydDecision.IosUpdateId);
    }

    [Fact]
    public async Task 设备覆盖优先于分店覆盖_同店其他设备走分店覆盖()
    {
        var bri = await SeedStoreAsync("BRI");
        var storeRelease = await SeedReleaseAsync("22222222");
        var deviceRelease = await SeedReleaseAsync("33333333");
        var pinnedDevice = await SeedDeviceAsync("hw-1", "IPAD-1", "BRI");
        await SeedDeviceAsync("hw-2", "IPAD-2", "BRI");
        var service = CreateService();
        Assert.True((await service.CreateAssignmentAsync(StoreAssignment(bri, storeRelease), "admin")).Success);
        Assert.True((await service.CreateAssignmentAsync(DeviceAssignment(pinnedDevice, deviceRelease), "admin")).Success);

        var pinned = await service.GetDecisionAsync(Decision("BRI", "IPAD-1", "hw-1"));
        var other = await service.GetDecisionAsync(Decision("BRI", "IPAD-2", "hw-2"));
        var noIdentity = await service.GetDecisionAsync(Decision("BRI"));

        Assert.Equal(deviceRelease.IosUpdateId, pinned.IosUpdateId);
        Assert.Equal(storeRelease.IosUpdateId, other.IosUpdateId);
        // 没带设备身份（旧客户端 / 旧 POS API）时只看分店层，不出错。
        Assert.Equal(storeRelease.IosUpdateId, noIdentity.IosUpdateId);
    }

    [Fact]
    public async Task 命中层已是目标版本时返回none_不回落到基线()
    {
        await SeedStoreAsync("BRI");
        var baseline = await SeedReleaseAsync("11111111");
        var pinnedRelease = await SeedReleaseAsync("33333333");
        var device = await SeedDeviceAsync("hw-1", "IPAD-1", "BRI");
        var service = CreateService();
        await EnableBaselineAsync(service, baseline);
        Assert.True((await service.CreateAssignmentAsync(DeviceAssignment(device, pinnedRelease), "admin")).Success);

        var decision = await service.GetDecisionAsync(
            Decision("BRI", "IPAD-1", "hw-1", currentUpdateId: pinnedRelease.IosUpdateId)
        );

        // 设备已经在钉住的版本上：不能因为基线是另一个版本就把它拉走。
        Assert.Equal(AppUpdateStates.None, decision.State);
    }

    [Fact]
    public async Task 设备身份不符时不启用设备层_回落分店层()
    {
        var bri = await SeedStoreAsync("BRI");
        await SeedStoreAsync("SYD");
        var storeRelease = await SeedReleaseAsync("22222222");
        var deviceRelease = await SeedReleaseAsync("33333333");
        var device = await SeedDeviceAsync("hw-1", "IPAD-1", "BRI");
        await SeedDeviceAsync("hw-win", "WIN-1", "BRI", system: "Windows");
        await SeedDeviceAsync("hw-tab", "TAB-1", "BRI", system: "AndroidTablet");
        var service = CreateService();
        Assert.True((await service.CreateAssignmentAsync(StoreAssignment(bri, storeRelease), "admin")).Success);
        Assert.True((await service.CreateAssignmentAsync(DeviceAssignment(device, deviceRelease), "admin")).Success);

        var wrongCode = await service.GetDecisionAsync(Decision("BRI", "IPAD-9", "hw-1"));
        var wrongStore = await service.GetDecisionAsync(Decision("SYD", "IPAD-1", "hw-1"));
        var windowsDevice = await service.GetDecisionAsync(Decision("BRI", "WIN-1", "hw-win"));
        var androidTabletDevice = await service.GetDecisionAsync(Decision("BRI", "TAB-1", "hw-tab"));

        Assert.Equal(storeRelease.IosUpdateId, wrongCode.IosUpdateId);
        // 设备注册在 BRI，却用 SYD 的身份来问：设备层和 BRI 的分店层都不能命中。
        Assert.Equal(AppUpdateStates.None, wrongStore.State);
        Assert.Equal(storeRelease.IosUpdateId, windowsDevice.IosUpdateId);
        Assert.Equal(storeRelease.IosUpdateId, androidTabletDevice.IosUpdateId);
    }

    [Fact]
    public async Task 同硬件重新激活产生新注册ID_设备覆盖继续有效_换店后失效()
    {
        var bri = await SeedStoreAsync("BRI");
        await SeedStoreAsync("SYD");
        var storeRelease = await SeedReleaseAsync("22222222");
        var deviceRelease = await SeedReleaseAsync("33333333");
        var firstId = await SeedDeviceAsync("hw-1", "IPAD-1", "BRI");
        var service = CreateService();
        Assert.True((await service.CreateAssignmentAsync(StoreAssignment(bri, storeRelease), "admin")).Success);
        Assert.True((await service.CreateAssignmentAsync(DeviceAssignment(firstId, deviceRelease), "admin")).Success);

        // 重新激活：同硬件、同分店，新增一条注册记录。
        await SeedDeviceAsync("hw-1", "IPAD-1", "BRI");
        var afterReactivate = await service.GetDecisionAsync(Decision("BRI", "IPAD-1", "hw-1"));
        Assert.Equal(deviceRelease.IosUpdateId, afterReactivate.IosUpdateId);

        // 换店：最新注册记录在 SYD，旧的 BRI 设备覆盖不再命中。
        await SeedDeviceAsync("hw-1", "IPAD-1", "SYD");
        var afterMove = await service.GetDecisionAsync(Decision("SYD", "IPAD-1", "hw-1"));
        Assert.Equal(AppUpdateStates.None, afterMove.State);
    }

    [Fact]
    public async Task 过期或停用的覆盖被忽略_回落到基线()
    {
        var bri = await SeedStoreAsync("BRI");
        var baseline = await SeedReleaseAsync("11111111");
        var pinned = await SeedReleaseAsync("22222222");
        var service = CreateService();
        await EnableBaselineAsync(service, baseline);
        var created = await service.CreateAssignmentAsync(StoreAssignment(bri, pinned), "admin");
        Assert.True(created.Success, created.Message);

        // 创建接口不允许直接写过期时间，这里模拟「时间流逝」：把到期时间改到过去。
        var past = DateTime.UtcNow.AddMinutes(-1);
        await _db.Updateable<PosIpadOtaAssignment>()
            .SetColumns(item => item.ExpiresAtUtc == past)
            .Where(item => item.Id == created.Data!.Id)
            .ExecuteCommandAsync();
        var expired = await service.GetDecisionAsync(Decision("BRI"));
        Assert.Equal(baseline.IosUpdateId, expired.IosUpdateId);

        var disabled = await service.UpdateAssignmentAsync(
            created.Data!.Id,
            new PosIpadOtaAssignmentRequest
            {
                ReleaseId = pinned.Id,
                Enabled = false,
                ExpectedPolicyVersion = created.Data.PolicyVersion,
            },
            "admin"
        );
        Assert.True(disabled.Success, disabled.Message);
        var afterDisable = await service.GetDecisionAsync(Decision("BRI"));
        Assert.Equal(baseline.IosUpdateId, afterDisable.IosUpdateId);
    }

    [Fact]
    public async Task 同一目标重复创建被拒_修改需要版本号_删除后可重建()
    {
        var bri = await SeedStoreAsync("BRI");
        var first = await SeedReleaseAsync("22222222");
        var second = await SeedReleaseAsync("33333333");
        var service = CreateService();

        var created = await service.CreateAssignmentAsync(StoreAssignment(bri, first), "admin");
        Assert.True(created.Success, created.Message);
        var duplicate = await service.CreateAssignmentAsync(StoreAssignment(bri, second), "admin");
        Assert.False(duplicate.Success);
        Assert.Equal("OTA_ASSIGNMENT_TARGET_EXISTS", duplicate.ErrorCode);

        var missingVersion = await service.UpdateAssignmentAsync(
            created.Data!.Id,
            new PosIpadOtaAssignmentRequest { ReleaseId = second.Id },
            "admin"
        );
        Assert.Equal(AppUpdatePolicyErrorCodes.VersionRequired, missingVersion.ErrorCode);

        var stale = await service.UpdateAssignmentAsync(
            created.Data.Id,
            new PosIpadOtaAssignmentRequest
            {
                ReleaseId = second.Id,
                ExpectedPolicyVersion = created.Data.PolicyVersion + 99,
            },
            "admin"
        );
        Assert.Equal(AppUpdatePolicyErrorCodes.VersionConflict, stale.ErrorCode);

        var updated = await service.UpdateAssignmentAsync(
            created.Data.Id,
            new PosIpadOtaAssignmentRequest
            {
                ReleaseId = second.Id,
                ForceUpdate = true,
                ExpectedPolicyVersion = created.Data.PolicyVersion,
            },
            "admin"
        );
        Assert.True(updated.Success, updated.Message);
        Assert.Equal(second.Id, updated.Data!.ReleaseId);
        Assert.True(updated.Data.PolicyVersion > created.Data.PolicyVersion);

        var deleted = await service.DeleteAssignmentAsync(
            created.Data.Id,
            updated.Data.PolicyVersion,
            "admin"
        );
        Assert.True(deleted.Success);
        Assert.Empty((await service.GetAssignmentsAsync()).Data!);

        var recreated = await service.CreateAssignmentAsync(StoreAssignment(bri, first), "admin");
        Assert.True(recreated.Success, recreated.Message);
        Assert.True(recreated.Data!.PolicyVersion > updated.Data.PolicyVersion);
    }

    [Fact]
    public async Task 设备覆盖创建校验_只接受已启用的iPad设备()
    {
        await SeedStoreAsync("BRI");
        var release = await SeedReleaseAsync("22222222");
        var windows = await SeedDeviceAsync("hw-win", "WIN-1", "BRI", system: "Windows");
        var disabled = await SeedDeviceAsync("hw-off", "IPAD-OFF", "BRI", status: 0);
        var androidTablet = await SeedDeviceAsync("hw-tab", "TAB-1", "BRI", system: "AndroidTablet");
        var service = CreateService();

        var androidTabletResult = await service.CreateAssignmentAsync(DeviceAssignment(androidTablet, release), "admin");
        Assert.Equal("TARGET_DEVICES_INVALID", androidTabletResult.ErrorCode);
        var windowsResult = await service.CreateAssignmentAsync(DeviceAssignment(windows, release), "admin");
        var disabledResult = await service.CreateAssignmentAsync(DeviceAssignment(disabled, release), "admin");
        var missingResult = await service.CreateAssignmentAsync(DeviceAssignment(9999, release), "admin");

        Assert.Equal("TARGET_DEVICES_INVALID", windowsResult.ErrorCode);
        Assert.Equal("TARGET_DEVICES_INVALID", disabledResult.ErrorCode);
        Assert.Equal("TARGET_DEVICES_INVALID", missingResult.ErrorCode);
    }

    [Fact]
    public async Task 设备选项只列启用的iPad_且每个硬件只取最新注册()
    {
        var bri = await SeedStoreAsync("BRI");
        await SeedStoreAsync("SYD");
        await SeedDeviceAsync("hw-1", "IPAD-1", "BRI");
        var latest = await SeedDeviceAsync("hw-1", "IPAD-1", "BRI");
        await SeedDeviceAsync("hw-2", "IPAD-2", "SYD");
        await SeedDeviceAsync("hw-win", "WIN-1", "BRI", system: "Windows");
        // 安卓平板走手动 APK 升级，不能出现在 iPad OTA 设备选项里。
        await SeedDeviceAsync("hw-tab", "TAB-1", "BRI", system: "AndroidTablet");
        var service = CreateService();

        var all = await service.GetTargetDeviceOptionsAsync(null, null, 1, 20);
        var briOnly = await service.GetTargetDeviceOptionsAsync(bri.StoreGUID, null, 1, 20);

        Assert.True(all.Success);
        Assert.Equal(2, all.Data!.Total);
        Assert.Equal(new[] { "IPAD-1", "IPAD-2" }, all.Data.Items!.Select(item => item.SystemDeviceNumber).OrderBy(x => x));
        Assert.Single(briOnly.Data!.Items!);
        Assert.Equal(latest, briOnly.Data.Items![0].DeviceRegistrationId);
        Assert.Equal("Brisbane", briOnly.Data.Items[0].StoreName);
    }

    [Fact]
    public async Task 未注入设备库时设备层关闭_设备覆盖创建被拒()
    {
        var bri = await SeedStoreAsync("BRI");
        var release = await SeedReleaseAsync("22222222");
        var service = new PosIpadOtaPolicyService(
            _db,
            NullLogger<PosIpadOtaPolicyService>.Instance
        );
        Assert.True((await service.CreateAssignmentAsync(StoreAssignment(bri, release), "admin")).Success);

        var deviceResult = await service.CreateAssignmentAsync(DeviceAssignment(1, release), "admin");
        var decision = await service.GetDecisionAsync(Decision("BRI", "IPAD-1", "hw-1"));

        Assert.Equal("OTA_DEVICE_DB_UNAVAILABLE", deviceResult.ErrorCode);
        Assert.Equal(release.IosUpdateId, decision.IosUpdateId);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _mainPath, _posmPath })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 临时库删不掉不影响测试结论。
            }
        }
    }

    private PosIpadOtaPolicyService CreateService() =>
        new(_db, NullLogger<PosIpadOtaPolicyService>.Instance, _posm);

    private static ISqlSugarClient NewClient(string path) =>
        new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = $"DataSource={path}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            }
        );

    private static PosIpadOtaDecisionRequest Decision(
        string storeCode,
        string? deviceCode = null,
        string? hardwareId = null,
        string? currentUpdateId = OldUpdateId
    ) =>
        new()
        {
            StoreCode = storeCode,
            DeviceCode = deviceCode,
            HardwareId = hardwareId,
            RuntimeVersion = Runtime,
            CurrentUpdateId = currentUpdateId,
        };

    private static PosIpadOtaAssignmentRequest StoreAssignment(
        Store store,
        PosIpadOtaRelease release
    ) =>
        new()
        {
            TargetType = PosIpadOtaAssignmentTargetTypes.Store,
            StoreGuid = store.StoreGUID,
            ReleaseId = release.Id,
            Enabled = true,
        };

    private static PosIpadOtaAssignmentRequest DeviceAssignment(
        int deviceRegistrationId,
        PosIpadOtaRelease release
    ) =>
        new()
        {
            TargetType = PosIpadOtaAssignmentTargetTypes.Device,
            DeviceRegistrationId = deviceRegistrationId,
            ReleaseId = release.Id,
            Enabled = true,
        };

    private static async Task EnableBaselineAsync(
        PosIpadOtaPolicyService service,
        PosIpadOtaRelease release
    )
    {
        var result = await service.SetRolloutAsync(
            new PosIpadOtaRolloutRequest
            {
                ExpectedPolicyVersion = 0,
                Enabled = true,
                ReleaseId = release.Id,
                TargetScope = AppUpdateTargetScopes.All,
            },
            "admin"
        );
        Assert.True(result.Success, result.Message);
    }

    private async Task<Store> SeedStoreAsync(string code)
    {
        var store = new Store
        {
            StoreGUID = Guid.NewGuid().ToString(),
            StoreCode = code,
            StoreName = code == "BRI" ? "Brisbane" : code == "SYD" ? "Sydney" : code,
            IsActive = true,
            IsDeleted = false,
        };
        await _db.Insertable(store).ExecuteCommandAsync();
        return store;
    }

    private async Task<PosIpadOtaRelease> SeedReleaseAsync(string seed)
    {
        var groupId = $"{seed}-0000-4000-8000-000000000001";
        var release = new PosIpadOtaRelease
        {
            Id = Guid.NewGuid(),
            Environment = "production",
            UpdateGroupId = groupId,
            IosUpdateId = $"{seed}-0000-4000-8000-000000000002",
            Channel = $"pos-ipad-release-{seed}",
            RuntimeVersion = Runtime,
            GitCommitHash = "abc123",
            PublishedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            IsDeleted = false,
        };
        await _db.Insertable(release).ExecuteCommandAsync();
        return release;
    }

    private async Task<int> SeedDeviceAsync(
        string hardwareId,
        string deviceCode,
        string storeCode,
        string system = "iPadOS",
        int status = 1
    )
    {
        var device = new POSM_设备注册信息表
        {
            设备硬件识别码 = hardwareId,
            系统设备编号 = deviceCode,
            分店代码 = storeCode,
            设备类型 = "POS",
            设备系统 = system,
            设备状态 = status,
            设备授权码 = "auth",
            创建时间 = DateTime.Now,
        };
        return await _posm.Insertable(device).ExecuteReturnIdentityAsync();
    }
}
