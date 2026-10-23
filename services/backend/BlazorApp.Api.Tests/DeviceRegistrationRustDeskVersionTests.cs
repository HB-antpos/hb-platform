using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Mappings;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 设备管理分页列表带出 RustDesk 客户端版本与远程维护下发版本，且不带任何凭据字段。
/// </summary>
public sealed class DeviceRegistrationRustDeskVersionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public DeviceRegistrationRustDeskVersionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        // 实体有 nvarchar(max) 列，SQLite 建不了，这里手工建等价表。
        _db.Ado.ExecuteCommand(
            """
            CREATE TABLE HBweb_RemoteMaintenanceDevice (
              Id TEXT PRIMARY KEY, DeviceRegistrationId INTEGER NOT NULL, HardwareId TEXT NOT NULL,
              StoreCode TEXT NOT NULL, DeviceCode TEXT NOT NULL, ComputerName TEXT NOT NULL,
              RustdeskId TEXT NULL, ClientVersion TEXT NULL, AgentVersion TEXT NULL, ServiceStatus TEXT NOT NULL,
              LastSeenAtUtc TEXT NULL, RegisteredAtUtc TEXT NOT NULL, LastAcceptedSequence INTEGER NOT NULL,
              LastAcceptedAtUtc TEXT NULL, LastOperationId TEXT NULL, MonitorTokenHash TEXT NULL,
              CredentialCiphertext TEXT NULL, CommitResponseCiphertext TEXT NULL, IsDeleted INTEGER NOT NULL)
            """);
    }

    [Fact]
    public async Task GetDevicesPaged_按注册ID或硬件号带出远程版本_无记录为null()
    {
        await SeedRemoteAsync(registrationId: 1, hardwareId: "HW-1", clientVersion: "1.4.2");
        // 设备重新登记后注册 ID 变了，按硬件号对上。
        await SeedRemoteAsync(registrationId: 99, hardwareId: "HW-2", clientVersion: "1.3.9");
        await SeedRemoteAsync(registrationId: 3, hardwareId: "HW-3", clientVersion: "1.4.0", isDeleted: true);

        var service = new Mock<IDeviceRegistrationService>();
        service
            .Setup(x => x.GetDevicesPagedAsync(1, 20, null, null, null, null, null))
            .ReturnsAsync((new List<POSM_设备注册信息表>
            {
                CreateDevice(1, "HW-1"),
                CreateDevice(2, "HW-2"),
                CreateDevice(3, "HW-3"),
            }, 3));

        var controller = new DeviceRegistrationController(
            service.Object,
            Mock.Of<ILogger<DeviceRegistrationController>>(),
            new MapperConfiguration(cfg => cfg.AddProfile<DeviceMappingProfile>(), NullLoggerFactory.Instance).CreateMapper(),
            Mock.Of<IStoreService>(),
            remoteMaintenanceService: CreateRemoteMaintenanceService("1.4.2"));

        var ok = Assert.IsType<OkObjectResult>(await controller.GetDevicesPaged());
        var data = ok.Value!.GetType().GetProperty("data")!.GetValue(ok.Value)!;
        var devices = Assert.IsType<List<DeviceListItemDto>>(data.GetType().GetProperty("devices")!.GetValue(data));
        Assert.Equal("1.4.2", devices.Single(d => d.Id == 1).RustDeskClientVersion);
        Assert.Equal("1.3.9", devices.Single(d => d.Id == 2).RustDeskClientVersion);
        Assert.Null(devices.Single(d => d.Id == 3).RustDeskClientVersion);
        Assert.Equal("1.4.2", data.GetType().GetProperty("rustDeskLatestVersion")!.GetValue(data));
        // 列表项只多一个版本号字段，不能带出远程维护的 ID / 密码。
        Assert.DoesNotContain(
            typeof(DeviceListItemDto).GetProperties(),
            p => p.Name.Contains("Rustdesk", StringComparison.OrdinalIgnoreCase)
                && p.Name != nameof(DeviceListItemDto.RustDeskClientVersion));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private Task SeedRemoteAsync(int registrationId, string hardwareId, string clientVersion, bool isDeleted = false)
    {
        return _db.Insertable(new RemoteMaintenanceDevice
        {
            Id = Guid.NewGuid(),
            DeviceRegistrationId = registrationId,
            HardwareId = hardwareId,
            StoreCode = "S1",
            DeviceCode = $"D{registrationId}",
            ComputerName = $"PC{registrationId}",
            RustdeskId = "123456789",
            ClientVersion = clientVersion,
            CredentialCiphertext = "secret",
            RegisteredAtUtc = DateTime.UtcNow,
            IsDeleted = isDeleted,
        }).ExecuteCommandAsync();
    }

    private static POSM_设备注册信息表 CreateDevice(int id, string hardwareId) => new()
    {
        ID = id,
        设备硬件识别码 = hardwareId,
        系统设备编号 = $"SYS-{id}",
        设备授权码 = "AUTH",
        设备状态 = 1,
        设备类型 = "POS",
        设备系统 = "Windows",
        分店代码 = "S1",
        创建时间 = DateTime.UtcNow,
    };

    private RemoteMaintenanceService CreateRemoteMaintenanceService(string rustdeskVersion)
    {
        var context = CreateContext<SqlSugarContext>(_db);
        return new RemoteMaintenanceService(
            context,
            CreateContext<POSMSqlSugarContext>(_db),
            null!,
            Options.Create(new RemoteMaintenanceOptions
            {
                Enabled = true,
                RustdeskArtifact = new RemoteMaintenanceArtifactOptions { Version = rustdeskVersion },
            }),
            NullLogger<RemoteMaintenanceService>.Instance,
            new RemoteMaintenanceSchemaReadiness(context, NullLogger<RemoteMaintenanceSchemaReadiness>.Instance));
    }

    private static T CreateContext<T>(ISqlSugarClient db)
    {
        var context = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        typeof(T).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, db);
        return context;
    }
}
