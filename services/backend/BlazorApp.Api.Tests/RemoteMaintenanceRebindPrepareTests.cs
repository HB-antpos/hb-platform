using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 设备改绑到别的分店后，WPF 用原操作号对账时远程维护记录要跟到新注册。
/// 用真实 SQLite 跑 SqlSugar，并建上与生产相同的过滤唯一索引，软删除和迁移的先后顺序错了会直接报错。
/// </summary>
public sealed class RemoteMaintenanceRebindPrepareTests
{
    private const string HardwareId = "hw-rebind-test";

    [Fact]
    public async Task Prepare_AfterRebind_MovesCommittedRowToNewRegistration()
    {
        using var fixture = new RebindFixture();
        var (source, target) = await fixture.AddReboundRegistrationsAsync();
        var committed = await fixture.AddCommittedRowAsync(source);

        var result = await fixture.PrepareAsync(committed.LastOperationId!.Value);

        // DeviceId 不变，WPF 本机日志里保存的设备号才能继续对上。
        Assert.True(result.Success, result.Code);
        Assert.Equal(committed.Id, result.Data!.DeviceId);
        var row = Assert.Single(await fixture.ActiveRowsAsync());
        Assert.Equal(committed.Id, row.Id);
        Assert.Equal(target.ID, row.DeviceRegistrationId);
        Assert.Equal("1016", row.StoreCode);
        Assert.Equal("POS_1016_0738", row.DeviceCode);
        Assert.Equal(committed.RustdeskId, row.RustdeskId);
        Assert.Equal(committed.MonitorTokenHash, row.MonitorTokenHash);
        Assert.Equal(committed.CredentialCiphertext, row.CredentialCiphertext);
        Assert.Equal(committed.CommitResponseCiphertext, row.CommitResponseCiphertext);
        Assert.Equal(committed.LastAcceptedSequence, row.LastAcceptedSequence);
    }

    [Fact]
    public async Task Prepare_WithPlaceholderLeftByEarlierReconcile_HealsBothRows()
    {
        using var fixture = new RebindFixture();
        var (source, target) = await fixture.AddReboundRegistrationsAsync();
        var committed = await fixture.AddCommittedRowAsync(source);
        // 线上已有的状态：旧版后端在新注册下留了一条没有 RustDesk ID 的空记录，操作号与旧记录相同。
        var placeholder = await fixture.AddPlaceholderAsync(target, committed.LastOperationId);

        var result = await fixture.PrepareAsync(committed.LastOperationId!.Value);

        Assert.True(result.Success, result.Code);
        Assert.Equal(committed.Id, result.Data!.DeviceId);
        var row = Assert.Single(await fixture.ActiveRowsAsync());
        Assert.Equal(committed.Id, row.Id);
        Assert.Equal(target.ID, row.DeviceRegistrationId);
        Assert.True((await fixture.RowAsync(placeholder.Id)).IsDeleted);
    }

    [Fact]
    public async Task Prepare_RepeatedAfterMove_IsIdempotent()
    {
        using var fixture = new RebindFixture();
        var (source, target) = await fixture.AddReboundRegistrationsAsync();
        var committed = await fixture.AddCommittedRowAsync(source);

        await fixture.PrepareAsync(committed.LastOperationId!.Value);
        var result = await fixture.PrepareAsync(committed.LastOperationId!.Value);

        Assert.True(result.Success, result.Code);
        Assert.Equal(committed.Id, result.Data!.DeviceId);
        Assert.Equal(target.ID, Assert.Single(await fixture.ActiveRowsAsync()).DeviceRegistrationId);
    }

    [Fact]
    public async Task Prepare_WithoutRebindEvidence_DoesNotTakeOverOldRow()
    {
        using var fixture = new RebindFixture();
        var source = await fixture.AddRegistrationAsync("1012", "POS_1012_0715", "source-auth", status: 0);
        var target = await fixture.AddRegistrationAsync("1016", "POS_1016_0738", "target-auth", status: 1);
        var committed = await fixture.AddCommittedRowAsync(source);

        var result = await fixture.PrepareAsync(committed.LastOperationId!.Value);

        // 没有已消费的改绑记录就证明不了是同一次迁移，凭据和 token 不能跟过去。
        Assert.True(result.Success, result.Code);
        Assert.NotEqual(committed.Id, result.Data!.DeviceId);
        Assert.Equal(source.ID, (await fixture.RowAsync(committed.Id)).DeviceRegistrationId);
        Assert.Equal(target.ID, (await fixture.RowAsync(result.Data.DeviceId)).DeviceRegistrationId);
    }

    [Fact]
    public async Task Prepare_WithDifferentOperation_DoesNotTakeOverOldRow()
    {
        using var fixture = new RebindFixture();
        var (source, target) = await fixture.AddReboundRegistrationsAsync();
        var committed = await fixture.AddCommittedRowAsync(source);

        var result = await fixture.PrepareAsync(Guid.NewGuid());

        // 操作号不同说明本机日志已经不是那次安装，旧凭据不能交给这次操作。
        Assert.True(result.Success, result.Code);
        Assert.NotEqual(committed.Id, result.Data!.DeviceId);
        Assert.Equal(source.ID, (await fixture.RowAsync(committed.Id)).DeviceRegistrationId);
        Assert.Equal(target.ID, (await fixture.RowAsync(result.Data.DeviceId)).DeviceRegistrationId);
    }

    [Fact]
    public async Task Prepare_WhenNewRegistrationAlreadyCommitted_LeavesBothRowsAlone()
    {
        using var fixture = new RebindFixture();
        var (source, target) = await fixture.AddReboundRegistrationsAsync();
        var oldRow = await fixture.AddCommittedRowAsync(source);
        var currentRow = await fixture.AddCommittedRowAsync(target, oldRow.LastOperationId);

        var result = await fixture.PrepareAsync(oldRow.LastOperationId!.Value);

        Assert.True(result.Success, result.Code);
        Assert.Equal(currentRow.Id, result.Data!.DeviceId);
        Assert.Equal(source.ID, (await fixture.RowAsync(oldRow.Id)).DeviceRegistrationId);
        Assert.False((await fixture.RowAsync(currentRow.Id)).IsDeleted);
    }

    private sealed class RebindFixture : IDisposable
    {
        private static readonly DateTime InstalledAtUtc = new(2026, 10, 17, 7, 18, 5, DateTimeKind.Utc);
        private readonly string _artifactPath = Path.GetTempFileName();
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly SqlSugarClient _db;
        private readonly RemoteMaintenanceService _service;

        public RebindFixture()
        {
            _connection.Open();
            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _connection.ConnectionString,
                DbType = SqlSugar.DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            });
            // 实体的 nvarchar(max) 列 SQLite CodeFirst 建不出来，表结构手工对齐生产迁移。
            _db.Ado.ExecuteCommand("""
                CREATE TABLE HBweb_RemoteMaintenanceDevice (
                    Id TEXT NOT NULL PRIMARY KEY,
                    DeviceRegistrationId INTEGER NOT NULL,
                    HardwareId TEXT NOT NULL COLLATE NOCASE,
                    StoreCode TEXT NOT NULL,
                    DeviceCode TEXT NOT NULL,
                    ComputerName TEXT NOT NULL,
                    RustdeskId TEXT NULL,
                    ClientVersion TEXT NULL,
                    AgentVersion TEXT NULL,
                    ObservedRustDeskVersion TEXT NULL,
                    ServiceStatus TEXT NOT NULL,
                    LastSeenAtUtc TEXT NULL,
                    RegisteredAtUtc TEXT NOT NULL,
                    LastAcceptedSequence INTEGER NOT NULL,
                    LastAcceptedAtUtc TEXT NULL,
                    LastOperationId TEXT NULL,
                    MonitorTokenHash TEXT NULL,
                    CredentialCiphertext TEXT NULL,
                    CommitResponseCiphertext TEXT NULL,
                    IsDeleted INTEGER NOT NULL
                )
                """);
            _db.Ado.ExecuteCommand("""
                CREATE UNIQUE INDEX UX_HBweb_RemoteMaintenanceDevice_DeviceRegistrationId
                    ON HBweb_RemoteMaintenanceDevice(DeviceRegistrationId) WHERE IsDeleted = 0
                """);
            // 可空列按生产模型手工建，SqlSugar 的 SQLite CodeFirst 推断可空 DateTime 依版本而异。
            _db.Ado.ExecuteCommand("""
                CREATE TABLE "POSM_设备注册信息表" (
                    ID INTEGER PRIMARY KEY AUTOINCREMENT,
                    "设备硬件识别码" TEXT NOT NULL COLLATE NOCASE,
                    "系统设备编号" TEXT NOT NULL,
                    "分店代码" TEXT NULL,
                    "设备类型" TEXT NOT NULL,
                    "设备系统" TEXT NOT NULL,
                    "设备状态" INTEGER NOT NULL,
                    "是否允许交易" INTEGER NOT NULL,
                    "设备授权码" TEXT NOT NULL,
                    "备注" TEXT NULL,
                    "是否在线" INTEGER NOT NULL,
                    "最后心跳时间" TEXT NULL,
                    "当前收银员ID" TEXT NULL,
                    "当前收银员姓名" TEXT NULL,
                    "收银员登录时间" TEXT NULL,
                    "应用版本" TEXT NULL,
                    "系统开机秒数" INTEGER NULL,
                    "客户端内存MB" INTEGER NULL,
                    "客户端句柄数" INTEGER NULL,
                    "LastApiEntry" TEXT NULL,
                    "LastApiEntryAt" TEXT NULL,
                    "创建时间" TEXT NOT NULL,
                    "最后修改时间" TEXT NULL,
                    "创建人" TEXT NULL,
                    "最后修改人" TEXT NULL
                )
                """);
            _db.Ado.ExecuteCommand("""
                CREATE TABLE POSM_DeviceActivationGrant (
                    GrantId TEXT NOT NULL PRIMARY KEY,
                    SecretHash BLOB NOT NULL,
                    StoreCode TEXT NOT NULL,
                    DeviceSystem TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    CreatedBy TEXT NOT NULL,
                    Reason TEXT NOT NULL,
                    ExpiresAtUtc TEXT NOT NULL,
                    RevokedAtUtc TEXT NULL,
                    RevokedBy TEXT NULL,
                    RevokeReason TEXT NULL,
                    ConsumedAtUtc TEXT NULL,
                    ConsumedHardwareId TEXT NULL COLLATE NOCASE,
                    ConsumedDeviceCode TEXT NULL,
                    ConsumedDeviceRegistrationId INTEGER NULL,
                    ConsumedAuthorizationHash BLOB NULL,
                    ConsumedDeviceSystem TEXT NULL,
                    ConsumptionKind TEXT NULL,
                    PreviousStoreCode TEXT NULL,
                    PreviousDeviceCode TEXT NULL,
                    RowVersion BLOB NULL
                )
                """);

            // 就绪检查的 SQL 是 SQL Server 专用，单独给它一个只回答「已就绪」的上下文。
            var ado = new Mock<IAdo>(MockBehavior.Strict);
            ado.Setup(x => x.GetIntAsync(It.IsAny<string>(), It.Is<SugarParameter[]>(p => p.Length == 0))).ReturnsAsync(1);
            var readinessDb = new Mock<ISqlSugarClient>(MockBehavior.Strict);
            readinessDb.SetupGet(x => x.Ado).Returns(ado.Object);

            File.WriteAllBytes(_artifactPath, [1, 2, 3]);
            var artifact = new RemoteMaintenanceArtifactOptions
            {
                Version = "test", FileName = "test.exe", Path = _artifactPath,
                SizeBytes = 3, Sha256 = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 }))
            };
            _service = new RemoteMaintenanceService(
                CreateContext<SqlSugarContext>(_db),
                CreateContext<POSMSqlSugarContext>(_db),
                null!,
                Options.Create(new RemoteMaintenanceOptions
                {
                    Enabled = true, PublicKey = "test", RustdeskArtifact = artifact, StatusAgentArtifact = artifact
                }),
                NullLogger<RemoteMaintenanceService>.Instance,
                new RemoteMaintenanceSchemaReadiness(
                    CreateContext<SqlSugarContext>(readinessDb.Object),
                    NullLogger<RemoteMaintenanceSchemaReadiness>.Instance));
        }

        public Task<RemoteMaintenanceResult<RemoteMaintenancePrepareResponseDto>> PrepareAsync(Guid operationId) =>
            _service.PrepareAsync(new RemoteMaintenancePrepareInternalRequest(operationId, HardwareId, "DESKTOP-TEST"));

        public async Task<POSM_设备注册信息表> AddRegistrationAsync(
            string storeCode, string deviceCode, string authorizationCode, int status)
        {
            var row = new POSM_设备注册信息表
            {
                设备硬件识别码 = HardwareId,
                系统设备编号 = deviceCode,
                分店代码 = storeCode,
                设备类型 = "POS",
                设备系统 = "Windows",
                设备状态 = status,
                设备授权码 = authorizationCode,
                创建时间 = InstalledAtUtc,
            };
            row.ID = await _db.Insertable(row).ExecuteReturnIdentityAsync();
            return row;
        }

        public async Task<(POSM_设备注册信息表 Source, POSM_设备注册信息表 Target)> AddReboundRegistrationsAsync()
        {
            var source = await AddRegistrationAsync("1012", "POS_1012_0715", "source-auth", status: 0);
            var target = await AddRegistrationAsync("1016", "POS_1016_0738", "target-auth", status: 1);
            var consumedAtUtc = InstalledAtUtc.AddDays(4);
            await _db.Insertable(new DeviceActivationCodeGrant
            {
                GrantId = Guid.NewGuid(),
                SecretHash = SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))),
                StoreCode = target.分店代码!,
                DeviceSystem = "Windows",
                CreatedAtUtc = consumedAtUtc.AddMinutes(-1),
                CreatedBy = "test",
                Reason = "test",
                ExpiresAtUtc = consumedAtUtc.AddDays(1),
                ConsumedAtUtc = consumedAtUtc,
                ConsumedHardwareId = HardwareId,
                ConsumedDeviceCode = target.系统设备编号,
                ConsumedDeviceRegistrationId = target.ID,
                ConsumedAuthorizationHash = SHA256.HashData(Encoding.UTF8.GetBytes(target.设备授权码)),
                ConsumedDeviceSystem = "Windows",
                ConsumptionKind = "Rebind",
                PreviousStoreCode = source.分店代码,
                PreviousDeviceCode = source.系统设备编号,
            }).ExecuteCommandAsync();
            return (source, target);
        }

        public async Task<RemoteMaintenanceDevice> AddCommittedRowAsync(
            POSM_设备注册信息表 registration, Guid? operationId = null)
        {
            var row = new RemoteMaintenanceDevice
            {
                Id = Guid.NewGuid(),
                DeviceRegistrationId = registration.ID,
                HardwareId = HardwareId,
                StoreCode = registration.分店代码!,
                DeviceCode = registration.系统设备编号,
                ComputerName = "DESKTOP-TEST",
                RustdeskId = "337614965",
                ClientVersion = "1.4.9",
                ServiceStatus = "running",
                RegisteredAtUtc = InstalledAtUtc,
                LastAcceptedSequence = 42,
                LastOperationId = operationId ?? Guid.NewGuid(),
                MonitorTokenHash = "token-hash",
                CredentialCiphertext = "credential-cipher",
                CommitResponseCiphertext = "commit-cipher",
            };
            await _db.Insertable(row).ExecuteCommandAsync();
            return row;
        }

        public async Task<RemoteMaintenanceDevice> AddPlaceholderAsync(
            POSM_设备注册信息表 registration, Guid? operationId)
        {
            var row = new RemoteMaintenanceDevice
            {
                Id = Guid.NewGuid(),
                DeviceRegistrationId = registration.ID,
                HardwareId = HardwareId,
                StoreCode = registration.分店代码!,
                DeviceCode = registration.系统设备编号,
                ComputerName = "DESKTOP-TEST",
                RegisteredAtUtc = InstalledAtUtc.AddDays(5),
                LastOperationId = operationId,
            };
            await _db.Insertable(row).ExecuteCommandAsync();
            return row;
        }

        public Task<List<RemoteMaintenanceDevice>> ActiveRowsAsync() =>
            _db.Queryable<RemoteMaintenanceDevice>().Where(x => !x.IsDeleted).ToListAsync();

        public Task<RemoteMaintenanceDevice> RowAsync(Guid id) =>
            _db.Queryable<RemoteMaintenanceDevice>().FirstAsync(x => x.Id == id);

        private static T CreateContext<T>(ISqlSugarClient db)
        {
            // 仅替换数据库边界，真实业务方法照常运行。
            var context = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            typeof(T).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, db);
            return context;
        }

        public void Dispose()
        {
            _db.Dispose();
            _connection.Dispose();
            File.Delete(_artifactPath);
        }
    }
}
