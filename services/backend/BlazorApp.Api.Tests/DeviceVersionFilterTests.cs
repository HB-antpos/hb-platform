using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Mappings;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 设备注册列表的版本筛选：WPF 更新策略快照、按版本 / 状态筛选分页、版本汇总。
/// 策略和分店走真实 SQLite，设备列表走 Moq，这样每台设备的版本和分店都可控。
/// </summary>
public sealed class DeviceVersionFilterTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"device-version-{Guid.NewGuid():N}.db");
    private readonly ISqlSugarClient _db;
    private readonly WpfAppReleaseService _releaseService;
    private readonly Mock<IDeviceRegistrationService> _deviceService = new();

    public DeviceVersionFilterTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables<WpfAppRelease, WpfUpdatePolicy, WpfUpdatePolicyTarget, Store>();
        _releaseService = new WpfAppReleaseService(_db, NullLogger<WpfAppReleaseService>.Instance);
    }

    [Fact]
    public async Task GetPolicyTargetSnapshotAsync_没有策略返回null()
    {
        Assert.Null(await _releaseService.GetPolicyTargetSnapshotAsync());
    }

    [Fact]
    public async Task GetPolicyTargetSnapshotAsync_分店范围把StoreGUID解析成分店代码()
    {
        await SeedStoreAsync("guid-bri", "BRI");
        await SeedStoreAsync("guid-syd", "SYD");
        await SetStoresPolicyAsync("1.0.96", "guid-bri");

        var snapshot = await _releaseService.GetPolicyTargetSnapshotAsync();

        Assert.NotNull(snapshot);
        Assert.Equal("1.0.96", snapshot!.TargetVersion);
        Assert.Equal("stores", snapshot.Scope);
        Assert.True(snapshot.Targets(1, "BRI"));
        Assert.False(snapshot.Targets(1, "SYD"));
    }

    [Fact]
    public async Task GetDevicesPaged_无版本条件_保持数据库分页并补上版本状态()
    {
        await SeedStoreAsync("guid-bri", "BRI");
        await SeedStoreAsync("guid-syd", "SYD");
        await SetStoresPolicyAsync("1.0.96", "guid-bri");
        ReturnPage(
            Device(1, "BRI", "1.0.96"),
            Device(2, "BRI", "1.0.95"),
            Device(3, "SYD", "1.0.95"),
            Device(4, "BRI", null),
            Device(5, "BRI", "1.0.95", type: "Mobile", system: "Android")
        );

        var data = ReadData(await CreateController().GetDevicesPaged());

        var devices = data.Devices;
        Assert.Equal(DeviceVersionStateNames.Latest, devices.Single(d => d.Id == 1).VersionState);
        Assert.Equal(DeviceVersionStateNames.Behind, devices.Single(d => d.Id == 2).VersionState);
        Assert.Equal(DeviceVersionStateNames.NotTargeted, devices.Single(d => d.Id == 3).VersionState);
        Assert.Equal(DeviceVersionStateNames.Unreported, devices.Single(d => d.Id == 4).VersionState);
        Assert.Null(devices.Single(d => d.Id == 5).VersionState);
        Assert.Equal("1.0.96", devices.Single(d => d.Id == 1).TargetAppVersion);
        Assert.Null(devices.Single(d => d.Id == 5).TargetAppVersion);
        // 没有版本条件时必须还是走原来的 (page, pageSize) 分页，不能退化成全量扫描。
        _deviceService.Verify(
            x => x.GetDevicesPagedAsync(1, 20, null, null, null, null, null),
            Times.Once
        );
        _deviceService.Verify(
            x => x.GetDevicesPagedAsync(1, 5000, null, null, null, null, null),
            Times.Never
        );
    }

    [Fact]
    public async Task GetDevicesPaged_按版本状态筛选_总数和分页一致()
    {
        await SeedStoreAsync("guid-bri", "BRI");
        await SetStoresPolicyAsync("1.0.96", "guid-bri");
        ReturnAll(
            Device(1, "BRI", "1.0.96"),
            Device(2, "BRI", "1.0.95"),
            Device(3, "BRI", "1.0.94"),
            Device(4, "BRI", "1.0.93"),
            Device(5, "SYD", "1.0.90")
        );

        var first = ReadData(await CreateController().GetDevicesPaged(page: 1, pageSize: 2, versionState: "behind"));
        var second = ReadData(await CreateController().GetDevicesPaged(page: 2, pageSize: 2, versionState: "behind"));

        Assert.Equal(3, first.Total);
        Assert.Equal(new[] { 2, 3 }, first.Devices.Select(d => d.Id));
        Assert.Equal(new[] { 4 }, second.Devices.Select(d => d.Id));
        Assert.Equal(2, first.TotalPages);
    }

    [Fact]
    public async Task GetDevicesPaged_按指定版本筛选_分店外设备也能被筛出()
    {
        await SeedStoreAsync("guid-bri", "BRI");
        await SetStoresPolicyAsync("1.0.96", "guid-bri");
        ReturnAll(
            Device(1, "BRI", "1.0.96"),
            Device(2, "BRI", "1.0.95"),
            Device(3, "SYD", "v1.0.95+sha"),
            Device(4, "SYD", "1.0.90")
        );

        var data = ReadData(await CreateController().GetDevicesPaged(appVersions: "1.0.95,1.0.96"));
        Assert.Equal(new[] { 1, 2, 3 }, data.Devices.Select(d => d.Id).OrderBy(id => id));

        var withState = ReadData(
            await CreateController().GetDevicesPaged(appVersions: "1.0.95", versionState: "notTargeted"));
        Assert.Equal(new[] { 3 }, withState.Devices.Select(d => d.Id));
    }

    [Fact]
    public async Task GetDevicesPaged_未知版本状态返回400()
    {
        var result = await CreateController().GetDevicesPaged(versionState: "oops");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetDevicesPaged_没有策略服务时不报错_只标未上报()
    {
        ReturnPage(Device(1, "BRI", "1.0.95"), Device(2, "BRI", null));

        var data = ReadData(await CreateController(withReleaseService: false).GetDevicesPaged());

        Assert.Null(data.Devices.Single(d => d.Id == 1).VersionState);
        Assert.Equal(DeviceVersionStateNames.Unreported, data.Devices.Single(d => d.Id == 2).VersionState);
    }

    [Fact]
    public async Task GetMobileAdminVersionSummary_返回目标版本_分组和状态计数()
    {
        await SeedStoreAsync("guid-bri", "BRI");
        await SetStoresPolicyAsync("1.0.96", "guid-bri");
        ReturnAll(
            Device(1, "BRI", "1.0.96"),
            Device(2, "BRI", "1.0.95"),
            Device(3, "BRI", "1.0.95"),
            Device(4, "SYD", "1.0.95"),
            Device(5, "BRI", null),
            Device(6, "BRI", null, type: "Mobile", system: "Android")
        );

        var ok = Assert.IsType<OkObjectResult>(await CreateController().GetMobileAdminVersionSummary());
        var summary = Assert.IsType<DeviceVersionSummaryDto>(
            ok.Value!.GetType().GetProperty("data")!.GetValue(ok.Value));

        Assert.Equal("1.0.96", summary.TargetVersion);
        Assert.Equal("stores", summary.TargetScope);
        Assert.Equal(6, summary.Total);
        Assert.Equal(
            new (string?, int)[] { ("1.0.96", 1), ("1.0.95", 3), (null, 1) },
            summary.Items.Select(item => (item.AppVersion, item.Total))
        );
        Assert.Equal(1, summary.States.Latest);
        Assert.Equal(2, summary.States.Behind);
        Assert.Equal(1, summary.States.NotTargeted);
        Assert.Equal(1, summary.States.Unreported);
        Assert.Equal(1, summary.States.NotApplicable);
    }

    private void ReturnPage(params POSM_设备注册信息表[] devices) =>
        _deviceService
            .Setup(x => x.GetDevicesPagedAsync(1, 20, null, null, null, null, null))
            .ReturnsAsync((devices.ToList(), devices.Length));

    private void ReturnAll(params POSM_设备注册信息表[] devices) =>
        _deviceService
            .Setup(x => x.GetDevicesPagedAsync(1, 5000, null, null, null, null, null))
            .ReturnsAsync((devices.ToList(), devices.Length));

    private DeviceRegistrationController CreateController(bool withReleaseService = true) =>
        new(
            _deviceService.Object,
            Mock.Of<ILogger<DeviceRegistrationController>>(),
            new MapperConfiguration(cfg => cfg.AddProfile<DeviceMappingProfile>(), NullLoggerFactory.Instance)
                .CreateMapper(),
            Mock.Of<IStoreService>(),
            wpfAppReleaseService: withReleaseService ? _releaseService : null);

    private sealed record PagedData(List<DeviceListItemDto> Devices, int Total, int TotalPages);

    private static PagedData ReadData(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = ok.Value!.GetType().GetProperty("data")!.GetValue(ok.Value)!;
        var devices = Assert.IsType<List<DeviceListItemDto>>(data.GetType().GetProperty("devices")!.GetValue(data));
        var pagination = data.GetType().GetProperty("pagination")!.GetValue(data)!;
        return new PagedData(
            devices,
            (int)pagination.GetType().GetProperty("total")!.GetValue(pagination)!,
            (int)pagination.GetType().GetProperty("totalPages")!.GetValue(pagination)!);
    }

    private static POSM_设备注册信息表 Device(
        int id,
        string storeCode,
        string? version,
        string type = "POS",
        string system = "Windows") => new()
    {
        ID = id,
        设备硬件识别码 = $"HW-{id}",
        系统设备编号 = $"SYS-{id}",
        设备授权码 = "AUTH",
        设备状态 = 1,
        设备类型 = type,
        设备系统 = system,
        分店代码 = storeCode,
        应用版本 = version,
        创建时间 = DateTime.UtcNow.AddMinutes(-id),
    };

    private Task SeedStoreAsync(string guid, string code) =>
        _db.Insertable(new Store
        {
            StoreGUID = guid,
            StoreCode = code,
            StoreName = code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();

    private async Task SetStoresPolicyAsync(string targetVersion, params string[] storeGuids)
    {
        foreach (var version in new[] { "1.0.90", targetVersion })
        {
            var created = await _releaseService.CreateReleaseAsync(
                new WpfAppReleaseCreateRequest
                {
                    Channel = "production",
                    Version = version,
                    FileName = $"hbpos-{version}.exe",
                    FileSize = 100,
                    Sha256 = new string('a', 64),
                    DownloadUrl = $"https://example.test/wpf/hbpos-{version}.exe",
                    InstallerType = "exe",
                },
                "tester");
            Assert.True(created.Success);
        }

        var targeting = new WpfAppReleaseTargetingService(
            _releaseService,
            CreateContext<SqlSugarContext>(_db),
            CreateContext<POSMSqlSugarContext>(_db));
        var policy = await targeting.SetPolicyAsync(
            new WpfUpdatePolicyRequest
            {
                Channel = "production",
                TargetVersion = targetVersion,
                MinimumSupportedVersion = "1.0.90",
                TargetScope = "stores",
                TargetStoreGuids = storeGuids.ToList(),
            },
            "admin");
        Assert.True(policy.Success, policy.Message);
    }

    private static T CreateContext<T>(ISqlSugarClient db)
        where T : class
    {
        var context = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        typeof(T).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }
}
