using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Mappings;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class DeviceRegistrationReactServiceTests : IDisposable
{
    private readonly string _mainDbPath;
    private readonly string _posmDbPath;
    private readonly SqliteConnection _mainConnection;
    private readonly SqliteConnection _posmConnection;
    private readonly SqlSugarClient _mainDb;
    private readonly SqlSugarClient _posmDb;

    public DeviceRegistrationReactServiceTests()
    {
        _mainDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-main.db");
        _posmDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-posm.db");
        _mainConnection = new SqliteConnection($"Data Source={_mainDbPath}");
        _posmConnection = new SqliteConnection($"Data Source={_posmDbPath}");
        _mainConnection.Open();
        _posmConnection.Open();

        _mainDb = CreateClient(_mainConnection.ConnectionString);
        _posmDb = CreateClient(_posmConnection.ConnectionString);
        _mainDb.CodeFirst.InitTables<Store>();
        _posmDb.CodeFirst.InitTables<POSM_设备注册信息表>();
    }

    [Fact]
    public void DeviceRegistrationReactDtos_DoNotExposeAuthCode()
    {
        Assert.DoesNotContain(
            typeof(DeviceRegistrationListDto).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.Name == "设备授权码"
        );
        Assert.DoesNotContain(
            typeof(DeviceRegistrationDetailDto).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.Name == "设备授权码"
        );
        Assert.DoesNotContain(
            typeof(UpdateDeviceRegistrationDto).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.Name == "设备状态"
        );
    }

    [Fact]
    public void DeviceMappingProfile_UsesWindowsForLegacyBlankAndPreservesUnknownSystems()
    {
        var mapper = new MapperConfiguration(
            config => config.AddProfile<DeviceMappingProfile>(),
            NullLoggerFactory.Instance
        ).CreateMapper();

        var legacy = mapper.Map<DeviceListItemDto>(new POSM_设备注册信息表
        {
            设备系统 = " ",
            是否允许交易 = false,
        });
        var unknown = mapper.Map<DeviceListItemDto>(new POSM_设备注册信息表
        {
            设备系统 = " VisionOS ",
        });

        Assert.Equal("Windows", legacy.DeviceSystem);
        Assert.False(legacy.AllowTransactions);
        Assert.Equal("VisionOS", unknown.DeviceSystem);
    }

    [Fact]
    public void DeviceMappingProfile_MapsReportedClientVersionAndLeavesUnreportedEmpty()
    {
        var mapper = new MapperConfiguration(
            config => config.AddProfile<DeviceMappingProfile>(),
            NullLoggerFactory.Instance
        ).CreateMapper();

        var reported = mapper.Map<DeviceListItemDto>(new POSM_设备注册信息表 { 应用版本 = "1.0.51" });
        var unreported = mapper.Map<DeviceListItemDto>(new POSM_设备注册信息表());

        Assert.Equal("1.0.51", reported.AppVersion);
        Assert.Null(unreported.AppVersion);
    }

    [Fact]
    public void DeviceMappingProfile_MarksRuntimeTimesAsUtcSoJsonCarriesZ()
    {
        var mapper = new MapperConfiguration(
            config => config.AddProfile<DeviceMappingProfile>(),
            NullLoggerFactory.Instance
        ).CreateMapper();

        // 库里读出来的 DateTime Kind 是 Unspecified，但存的是 UTC（容器时区为 UTC）
        var dto = mapper.Map<DeviceListItemDto>(new POSM_设备注册信息表
        {
            最后心跳时间 = new DateTime(2026, 10, 8, 2, 0, 33, DateTimeKind.Unspecified),
            收银员登录时间 = new DateTime(2026, 10, 8, 1, 55, 0, DateTimeKind.Unspecified),
        });

        Assert.Equal(DateTimeKind.Utc, dto.LastHeartbeatAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, dto.CashierLoginAt!.Value.Kind);
        // 只改 Kind，不能改数值
        Assert.Equal(new DateTime(2026, 10, 8, 2, 0, 33), dto.LastHeartbeatAt);
        Assert.Equal(new DateTime(2026, 10, 8, 1, 55, 0), dto.CashierLoginAt);

        // 与接口一致的序列化（camelCase）必须带 Z，否则浏览器会把它当本地时间（悉尼偏差 10–11 小时）
        var json = System.Text.Json.JsonSerializer.Serialize(
            dto,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            }
        );
        Assert.Contains("\"lastHeartbeatAt\":\"2026-10-08T02:00:33Z\"", json);
        Assert.Contains("\"cashierLoginAt\":\"2026-10-08T01:55:00Z\"", json);
    }

    [Fact]
    public void DeviceMappingProfile_KeepsUnreportedRuntimeTimesNull()
    {
        var mapper = new MapperConfiguration(
            config => config.AddProfile<DeviceMappingProfile>(),
            NullLoggerFactory.Instance
        ).CreateMapper();

        var dto = mapper.Map<DeviceListItemDto>(new POSM_设备注册信息表());

        Assert.Null(dto.LastHeartbeatAt);
        Assert.Null(dto.CashierLoginAt);
    }

    [Fact]
    public void DeviceRuntimeStatusSchemaMigrator_AddsNullableAppVersionColumn()
    {
        var sql = string.Join("\n", DeviceRuntimeStatusSchemaMigrator.SqlScriptsForTests);

        Assert.Contains("COL_LENGTH(N'dbo.POSM_设备注册信息表', N'应用版本') IS NULL", sql);
        Assert.Contains("ADD [应用版本] NVARCHAR(50) NULL", sql);
    }

    [Fact]
    public void DeviceRuntimeStatusSchemaMigrator_AddsTransactionPermissionWithAllowedDefault()
    {
        var sql = string.Join("\n", DeviceRuntimeStatusSchemaMigrator.SqlScriptsForTests);

        Assert.Contains("COL_LENGTH(N'dbo.POSM_设备注册信息表', N'是否允许交易') IS NULL", sql);
        Assert.Contains("ADD [是否允许交易] BIT NOT NULL", sql);
        Assert.Contains("DEFAULT (1) WITH VALUES", sql);
        Assert.Contains("ERROR_NUMBER() NOT IN (2705, 2714)", sql);
        Assert.Contains("OR COL_LENGTH(N'dbo.POSM_设备注册信息表', N'是否允许交易') IS NULL", sql);
    }

    [Fact]
    public async Task GetGridDataAsync_FiltersPlatformCategoriesAndNormalizesLegacyDisplay()
    {
        await _posmDb.Insertable(new[]
        {
            CreateDevice(1, "hardware-windows", "Windows"),
            CreateDevice(2, "hardware-legacy", " "),
            CreateDevice(3, "hardware-ipados", "iPadOS"),
            CreateDevice(4, "hardware-android", "Android"),
            CreateDevice(5, "hardware-unknown", "VisionOS"),
            CreateDevice(6, "hardware-windows-spaced", " Windows "),
            CreateDevice(7, "hardware-ipados-spaced", " iPadOS "),
        }).ExecuteCommandAsync();
        var service = CreateService();

        var windows = await service.GetGridDataAsync(CreateSystemFilterRequest("Windows"));
        var ipados = await service.GetGridDataAsync(CreateSystemFilterRequest("iPadOS"));
        var other = await service.GetGridDataAsync(CreateSystemFilterRequest("Other"));

        Assert.True(windows.Success);
        Assert.Equal(3, windows.Total);
        Assert.All(windows.Items!, item => Assert.Equal("Windows", item.设备系统));
        Assert.True(ipados.Success);
        Assert.Equal(2, ipados.Total);
        Assert.All(ipados.Items!, item => Assert.Equal("iPadOS", item.设备系统));
        Assert.True(other.Success);
        Assert.Equal(2, other.Total);
        Assert.Equal(
            new[] { "Android", "VisionOS" },
            other.Items!.Select(item => item.设备系统).OrderBy(value => value)
        );
    }

    [Fact]
    public async Task UpdateAsync_UpdatesEditableFieldsWithoutChangingStatus()
    {
        await _posmDb.Insertable(new POSM_设备注册信息表
        {
            ID = 1,
            设备硬件识别码 = "hardware-001",
            系统设备编号 = "SYS-001",
            设备授权码 = "AUTH-SECRET",
            设备状态 = (int)DeviceStatus.启用,
            设备类型 = "Mobile",
            设备系统 = "Android",
            分店代码 = "1004",
            是否允许交易 = true,
            备注 = "old remark",
            创建时间 = DateTime.UtcNow,
            创建人 = "creator",
        }).ExecuteCommandAsync();
        await _mainDb.Insertable(new Store
        {
            StoreGUID = Guid.NewGuid().ToString("N"),
            StoreCode = "1004",
            StoreName = "Store 1004",
            IsActive = true,
        }).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.UpdateAsync(
            1,
            new UpdateDeviceRegistrationDto
            {
                设备类型 = "PDA",
                设备系统 = "Android",
                是否允许交易 = false,
                备注 = "new remark",
            },
            "manager"
        );

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("PDA", result.Data!.设备类型);
        Assert.Equal("Android", result.Data.设备系统);
        Assert.False(result.Data.是否允许交易);
        Assert.Equal("new remark", result.Data.备注);
        Assert.Equal((int)DeviceStatus.启用, result.Data.设备状态);

        var saved = await _posmDb.Queryable<POSM_设备注册信息表>()
            .SingleAsync(device => device.ID == 1);
        Assert.Equal((int)DeviceStatus.启用, saved.设备状态);
        Assert.False(saved.是否允许交易);
        Assert.Equal("AUTH-SECRET", saved.设备授权码);
        Assert.Equal("manager", saved.最后修改人);
        Assert.NotNull(saved.最后修改时间);
    }

    [Fact]
    public async Task UpdateAsync_OmittedTransactionPermissionPreservesExistingValue()
    {
        var device = CreateDevice(1, "hardware-transaction-disabled", "iPadOS");
        device.是否允许交易 = false;
        await _posmDb.Insertable(device).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.UpdateAsync(
            1,
            new UpdateDeviceRegistrationDto
            {
                设备类型 = "POS",
                设备系统 = "iPadOS",
                备注 = "keep disabled",
            },
            "manager"
        );

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(result.Data!.是否允许交易);
        var saved = await _posmDb.Queryable<POSM_设备注册信息表>()
            .SingleAsync(item => item.ID == 1);
        Assert.False(saved.是否允许交易);
    }

    [Fact]
    public async Task UpdateAsync_AllowsIpadOsForPendingRegistration()
    {
        var pendingDevice = CreateDevice(1, "hardware-ipad", "Windows");
        pendingDevice.设备状态 = (int)DeviceStatus.待确认;
        await _posmDb.Insertable(pendingDevice).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.UpdateAsync(
            1,
            new UpdateDeviceRegistrationDto
            {
                设备类型 = "POS",
                设备系统 = "iPadOS",
                备注 = "iPad POS",
            },
            "manager"
        );

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("iPadOS", result.Data!.设备系统);
        var saved = await _posmDb.Queryable<POSM_设备注册信息表>()
            .SingleAsync(device => device.ID == 1);
        Assert.Equal("iPadOS", saved.设备系统);
        Assert.Equal((int)DeviceStatus.待确认, saved.设备状态);
    }

    [Fact]
    public async Task UpdateAsync_RejectsPlatformChangeAfterApprovalWithoutMutatingIdentity()
    {
        var enabledDevice = CreateDevice(1, "hardware-ipad", "iPadOS");
        await _posmDb.Insertable(enabledDevice).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.UpdateAsync(
            1,
            new UpdateDeviceRegistrationDto
            {
                设备类型 = "POS",
                设备系统 = "Windows",
                备注 = "must not save",
            },
            "manager"
        );

        Assert.False(result.Success);
        var saved = await _posmDb.Queryable<POSM_设备注册信息表>()
            .SingleAsync(device => device.ID == 1);
        Assert.Equal("iPadOS", saved.设备系统);
        Assert.Equal("AUTH-001", saved.设备授权码);
        Assert.Equal((int)DeviceStatus.启用, saved.设备状态);
        Assert.Null(saved.最后修改人);
        Assert.Null(saved.最后修改时间);
    }

    [Fact]
    public async Task UpdateAsync_AllowsEnabledLegacyIosToCanonicalizeWithoutMutatingIdentity()
    {
        var enabledDevice = CreateDevice(1, "hardware-ipad", "iOS");
        await _posmDb.Insertable(enabledDevice).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.UpdateAsync(
            1,
            new UpdateDeviceRegistrationDto
            {
                设备类型 = "POS",
                设备系统 = "iPadOS",
                备注 = "canonicalized",
            },
            "manager"
        );

        Assert.True(result.Success);
        var saved = await _posmDb.Queryable<POSM_设备注册信息表>()
            .SingleAsync(device => device.ID == 1);
        Assert.Equal("iPadOS", saved.设备系统);
        Assert.Equal("hardware-ipad", saved.设备硬件识别码);
        Assert.Equal("SYS-001", saved.系统设备编号);
        Assert.Equal("AUTH-001", saved.设备授权码);
        Assert.Equal("1004", saved.分店代码);
        Assert.Equal((int)DeviceStatus.启用, saved.设备状态);
        Assert.Equal("manager", saved.最后修改人);
    }

    [Theory]
    [InlineData((int)DeviceStatus.禁用)]
    [InlineData((int)DeviceStatus.锁定)]
    [InlineData((int)DeviceStatus.未注册)]
    public async Task UpdateAsync_RejectsLegacyIosCanonicalizationWhenNotEnabled(int status)
    {
        var disabledDevice = CreateDevice(1, "hardware-ipad", "iOS");
        disabledDevice.设备状态 = status;
        await _posmDb.Insertable(disabledDevice).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.UpdateAsync(
            1,
            new UpdateDeviceRegistrationDto
            {
                设备类型 = "POS",
                设备系统 = "iPadOS",
                备注 = "must not save",
            },
            "manager"
        );

        Assert.False(result.Success);
        var saved = await _posmDb.Queryable<POSM_设备注册信息表>()
            .SingleAsync(device => device.ID == 1);
        Assert.Equal("iOS", saved.设备系统);
        Assert.Equal("AUTH-001", saved.设备授权码);
        Assert.Equal(status, saved.设备状态);
        Assert.Null(saved.最后修改人);
    }

    [Theory]
    [InlineData("Mobile")]
    [InlineData("PDA")]
    [InlineData("Admin")]
    public async Task UpdateAsync_RejectsLegacyIosCanonicalizationForNonPosDevice(string deviceType)
    {
        var enabledDevice = CreateDevice(1, "hardware-ios", "iOS");
        enabledDevice.设备类型 = deviceType;
        await _posmDb.Insertable(enabledDevice).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.UpdateAsync(
            1,
            new UpdateDeviceRegistrationDto
            {
                设备类型 = deviceType,
                设备系统 = "iPadOS",
                备注 = "must not save",
            },
            "manager"
        );

        Assert.False(result.Success);
        var saved = await _posmDb.Queryable<POSM_设备注册信息表>()
            .SingleAsync(device => device.ID == 1);
        Assert.Equal("iOS", saved.设备系统);
        Assert.Equal(deviceType, saved.设备类型);
        Assert.Equal("AUTH-001", saved.设备授权码);
        Assert.Null(saved.最后修改人);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("Android")]
    public async Task UpdateAsync_RejectsEnabledLegacyIosChangingToDifferentPlatform(
        string requestedDeviceSystem
    )
    {
        var enabledDevice = CreateDevice(1, "hardware-ipad", "iOS");
        await _posmDb.Insertable(enabledDevice).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.UpdateAsync(
            1,
            new UpdateDeviceRegistrationDto
            {
                设备类型 = "POS",
                设备系统 = requestedDeviceSystem,
                备注 = "must not save",
            },
            "manager"
        );

        Assert.False(result.Success);
        var saved = await _posmDb.Queryable<POSM_设备注册信息表>()
            .SingleAsync(device => device.ID == 1);
        Assert.Equal("iOS", saved.设备系统);
        Assert.Equal("AUTH-001", saved.设备授权码);
        Assert.Null(saved.最后修改人);
    }

    [Fact]
    public async Task UpdateAsync_RejectsLegacyIosCanonicalizationWhenRequestChangesDeviceType()
    {
        var enabledDevice = CreateDevice(1, "hardware-ipad", "iOS");
        await _posmDb.Insertable(enabledDevice).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.UpdateAsync(
            1,
            new UpdateDeviceRegistrationDto
            {
                设备类型 = "Mobile",
                设备系统 = "iPadOS",
                备注 = "must not save",
            },
            "manager"
        );

        Assert.False(result.Success);
        var saved = await _posmDb.Queryable<POSM_设备注册信息表>()
            .SingleAsync(device => device.ID == 1);
        Assert.Equal("POS", saved.设备类型);
        Assert.Equal("iOS", saved.设备系统);
        Assert.Equal("AUTH-001", saved.设备授权码);
        Assert.Null(saved.最后修改人);
    }

    [Fact]
    public async Task UpdateAsync_RejectsInvalidEditableFieldValues()
    {
        await _posmDb.Insertable(new POSM_设备注册信息表
        {
            ID = 1,
            设备硬件识别码 = "hardware-002",
            系统设备编号 = "SYS-002",
            设备授权码 = "AUTH-SECRET-2",
            设备状态 = (int)DeviceStatus.启用,
            设备类型 = "Mobile",
            设备系统 = "Android",
            分店代码 = "1005",
            备注 = "old remark",
            创建时间 = DateTime.UtcNow,
            创建人 = "creator",
        }).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.UpdateAsync(
            1,
            new UpdateDeviceRegistrationDto
            {
                设备类型 = "Tablet",
                设备系统 = "Linux",
                备注 = "should not save",
            },
            "manager"
        );

        Assert.False(result.Success);
        var saved = await _posmDb.Queryable<POSM_设备注册信息表>()
            .SingleAsync(device => device.ID == 1);
        Assert.Equal("Mobile", saved.设备类型);
        Assert.Equal("Android", saved.设备系统);
        Assert.Equal("old remark", saved.备注);
        Assert.Null(saved.最后修改人);
        Assert.Null(saved.最后修改时间);
    }

    public void Dispose()
    {
        _mainDb.Dispose();
        _posmDb.Dispose();
        _mainConnection.Dispose();
        _posmConnection.Dispose();
        DeleteIfExists(_mainDbPath);
        DeleteIfExists(_posmDbPath);
    }

    private static SqlSugarClient CreateClient(string connectionString) =>
        new(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });

    private DeviceRegistrationReactService CreateService()
    {
        var mainContext = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(SqlSugarContext)
        );
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(mainContext, _mainDb);

        var posmContext = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(POSMSqlSugarContext)
        );
        typeof(POSMSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(posmContext, _posmDb);

        return new DeviceRegistrationReactService(
            posmContext,
            mainContext,
            NullLogger<DeviceRegistrationReactService>.Instance
        );
    }

    private static GridRequestDto CreateSystemFilterRequest(string value) =>
        new()
        {
            StartRow = 0,
            PageSize = 20,
            FilterModel = new Dictionary<string, FilterModelDto>
            {
                ["设备系统"] = new()
                {
                    FilterType = "text",
                    Type = "equals",
                    Filter = value,
                },
            },
        };

    private static POSM_设备注册信息表 CreateDevice(
        int id,
        string hardwareId,
        string deviceSystem
    ) =>
        new()
        {
            ID = id,
            设备硬件识别码 = hardwareId,
            系统设备编号 = $"SYS-{id:000}",
            设备授权码 = $"AUTH-{id:000}",
            设备状态 = (int)DeviceStatus.启用,
            设备类型 = "POS",
            设备系统 = deviceSystem,
            分店代码 = "1004",
            创建时间 = DateTime.UtcNow,
        };

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            SqliteTempFileCleanup.DeleteIfExists(path);
        }
    }
}
