using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class RemoteMaintenanceRegistrationResolverTests
{
    [Fact]
    public async Task DirectMatchDoesNotReadGrantOrAuthorizationCode()
    {
        using var fixture = new ResolverFixture();
        var registration = await fixture.AddRegistrationAsync("1042", "POS_1042_0200", "auth-never-read", status: 1);
        fixture.QueryLog.Clear();
        var result = await fixture.ResolveAsync(fixture.Snapshot(registration.ID));

        Assert.Equal(registration.ID, Assert.Single(result).Value.ID);
        Assert.DoesNotContain(fixture.QueryLog, sql => sql.Contains("POSM_DeviceActivationGrant", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fixture.QueryLog, sql => sql.Contains("设备授权码", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OrdinaryDisabledRegistrationDoesNotFallback()
    {
        using var fixture = new ResolverFixture();
        var source = await fixture.AddRegistrationAsync("1042", "POS_1042_0200", "old-auth", status: 0);
        var result = await fixture.ResolveAsync(fixture.Snapshot(source.ID));
        Assert.Empty(result);
    }

    [Fact]
    public async Task RebindToExistingLowerIdTargetResolvesTarget()
    {
        using var fixture = new ResolverFixture();
        var target = await fixture.AddRegistrationAsync("1042", "POS_1042_0200", "target-auth", status: 0);
        var source = await fixture.AddRegistrationAsync("1043", "POS_1043_0200", "source-auth", status: 0);
        await fixture.EnableAsync(target.ID);
        var at = DateTime.UtcNow.AddMinutes(1);
        await fixture.AddRebindAsync(at, source, target);

        var result = await fixture.ResolveAsync(fixture.Snapshot(source.ID));
        Assert.Equal(target.ID, Assert.Single(result).Value.ID);
    }

    [Fact]
    public async Task RebindToNewIdTargetResolvesNewestTarget()
    {
        using var fixture = new ResolverFixture();
        var source = await fixture.AddRegistrationAsync("1042", "POS_1042_0200", "source-auth", status: 0);
        var target = await fixture.AddRegistrationAsync("1043", "POS_1043_0200", "target-auth", status: 1);
        await fixture.AddRebindAsync(DateTime.UtcNow.AddMinutes(1), source, target);

        var result = await fixture.ResolveAsync(fixture.Snapshot(source.ID));
        Assert.Equal(target.ID, Assert.Single(result).Value.ID);
    }

    [Fact]
    public async Task CaseOnlyUnrelatedRegistrationAfterChainRejectsMapping()
    {
        using var fixture = new ResolverFixture();
        var source = await fixture.AddRegistrationAsync("1042", "POS_1042_0200", "source-auth", status: 0);
        var target = await fixture.AddRegistrationAsync("1043", "POS_1043_0200", "target-auth", status: 1);
        await fixture.AddRebindAsync(DateTime.UtcNow.AddMinutes(1), source, target);
        await fixture.AddRegistrationAsync("1044", "POS_1044_0200", "unrelated-auth", status: 0, hardwareId: "HW-RESOLVER-TEST");

        var result = await fixture.ResolveAsync(fixture.Snapshot(source.ID));
        Assert.Empty(result);
    }

    [Fact]
    public async Task HardwareCaseChangeInValidRebindStillResolves()
    {
        using var fixture = new ResolverFixture();
        var source = await fixture.AddRegistrationAsync("1042", "POS_1042_0200", "source-auth", status: 0, hardwareId: "hw-resolver-test");
        var target = await fixture.AddRegistrationAsync("1043", "POS_1043_0200", "target-auth", status: 1, hardwareId: "HW-RESOLVER-TEST");
        await fixture.AddRebindAsync(DateTime.UtcNow.AddMinutes(1), source, target);

        var result = await fixture.ResolveAsync(fixture.Snapshot(source.ID));
        Assert.Equal(target.ID, Assert.Single(result).Value.ID);
    }

    [Fact]
    public async Task MultipleRebindsCanReturnToEarlierStore()
    {
        using var fixture = new ResolverFixture();
        var first = await fixture.AddRegistrationAsync("1042", "POS_1042_0200", "first-auth", status: 0);
        var second = await fixture.AddRegistrationAsync("1043", "POS_1043_0200", "second-auth", status: 0);
        await fixture.EnableAsync(first.ID);
        var snapshot = fixture.Snapshot(first.ID);
        await fixture.AddRebindAsync(snapshot.RegisteredAtUtc.AddMinutes(1), first, second);
        await fixture.DisableAsync(first.ID);
        await fixture.EnableAsync(second.ID);
        await fixture.AddRebindAsync(snapshot.RegisteredAtUtc.AddMinutes(2), second, first);
        await fixture.DisableAsync(second.ID);
        await fixture.EnableAsync(first.ID);

        var result = await fixture.ResolveAsync(snapshot);
        Assert.Equal(first.ID, Assert.Single(result).Value.ID);
    }

    [Fact]
    public async Task UnknownRegistrationAfterChainRejectsMapping()
    {
        using var fixture = new ResolverFixture();
        var source = await fixture.AddRegistrationAsync("1042", "POS_1042_0200", "source-auth", status: 0);
        var target = await fixture.AddRegistrationAsync("1043", "POS_1043_0200", "target-auth", status: 1);
        await fixture.AddRebindAsync(DateTime.UtcNow.AddMinutes(1), source, target);
        await fixture.AddRegistrationAsync("1044", "POS_1044_0200", "unrelated-auth", status: 0);

        var result = await fixture.ResolveAsync(fixture.Snapshot(source.ID));
        Assert.Empty(result);
    }

    [Fact]
    public async Task ChangedCurrentAuthorizationCodeRejectsStaleGrant()
    {
        using var fixture = new ResolverFixture();
        var source = await fixture.AddRegistrationAsync("1042", "POS_1042_0200", "source-auth", status: 0);
        var target = await fixture.AddRegistrationAsync("1043", "POS_1043_0200", "target-auth", status: 1);
        await fixture.AddRebindAsync(DateTime.UtcNow.AddMinutes(1), source, target);
        await fixture.ChangeAuthorizationAsync(target.ID, "target-auth-reset-later");

        var result = await fixture.ResolveAsync(fixture.Snapshot(source.ID));
        Assert.Empty(result);
    }

    [Fact]
    public async Task AmbiguousSourceTupleRejectsChain()
    {
        using var fixture = new ResolverFixture();
        var source = await fixture.AddRegistrationAsync("1042", "POS_1042_0200", "source-auth", status: 0);
        await fixture.AddRegistrationAsync("1042", "POS_1042_0200", "duplicate-auth", status: 0);
        var target = await fixture.AddRegistrationAsync("1043", "POS_1043_0200", "target-auth", status: 1);
        await fixture.AddRebindAsync(DateTime.UtcNow.AddMinutes(1), source, target);

        var result = await fixture.ResolveAsync(fixture.Snapshot(source.ID));
        Assert.Empty(result);
    }

    private sealed class ResolverFixture : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly SqlSugarClient _db;
        private readonly POSMSqlSugarContext _context;
        public List<string> QueryLog { get; } = [];

        public ResolverFixture()
        {
            _connection.Open();
            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _connection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            });
            _db.CodeFirst.InitTables<POSM_设备注册信息表>();
            _db.Ado.ExecuteCommand("DROP TABLE \"POSM_设备注册信息表\"");
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
                    "创建时间" TEXT NOT NULL,
                    "最后修改时间" TEXT NULL,
                    "创建人" TEXT NULL,
                    "最后修改人" TEXT NULL
                )
                """);
            // SqlSugar 的 SQLite CodeFirst 对可空 DateTime 的推断依版本而异；保持授权 grant
            // 与生产模型相同的可空字段，避免测试数据库改变证明语义。
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
            _db.Aop.OnLogExecuting = (sql, _) => QueryLog.Add(sql);
            _context = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(POSMSqlSugarContext));
            typeof(POSMSqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_context, _db);
        }

        public RemoteMaintenanceDevice Snapshot(int registrationId) => new()
        {
            Id = Guid.NewGuid(),
            DeviceRegistrationId = registrationId,
            HardwareId = "hw-resolver-test",
            RegisteredAtUtc = DateTime.UtcNow,
        };

        public async Task<POSM_设备注册信息表> AddRegistrationAsync(
            string storeCode, string deviceCode, string authorizationCode, int status,
            string hardwareId = "hw-resolver-test")
        {
            var row = new POSM_设备注册信息表
            {
                设备硬件识别码 = hardwareId,
                系统设备编号 = deviceCode,
                分店代码 = storeCode,
                设备类型 = "POS",
                设备系统 = "Windows",
                设备状态 = status,
                设备授权码 = authorizationCode,
            };
            row.ID = await _db.Insertable(row).ExecuteReturnIdentityAsync();
            return row;
        }

        public Task EnableAsync(int id) => SetStatusAsync(id, 1);
        public Task DisableAsync(int id) => SetStatusAsync(id, 0);

        public Task ChangeAuthorizationAsync(int id, string authorizationCode) =>
            _db.Updateable<POSM_设备注册信息表>()
                .SetColumns(row => new POSM_设备注册信息表 { 设备授权码 = authorizationCode })
                .Where(row => row.ID == id)
                .ExecuteCommandAsync();

        public async Task AddRebindAsync(
            DateTime consumedAtUtc,
            POSM_设备注册信息表 source,
            POSM_设备注册信息表 target)
        {
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
                ConsumedHardwareId = target.设备硬件识别码,
                ConsumedDeviceCode = target.系统设备编号,
                ConsumedDeviceRegistrationId = target.ID,
                ConsumedAuthorizationHash = SHA256.HashData(Encoding.UTF8.GetBytes(target.设备授权码)),
                ConsumedDeviceSystem = "Windows",
                ConsumptionKind = "Rebind",
                PreviousStoreCode = source.分店代码,
                PreviousDeviceCode = source.系统设备编号,
            }).ExecuteCommandAsync();
        }

        public async Task<Dictionary<Guid, POSM_设备注册信息表>> ResolveAsync(RemoteMaintenanceDevice snapshot)
        {
            return await RemoteMaintenanceRegistrationResolver.ResolveAsync(
                _context, new[] { snapshot }, CancellationToken.None);
        }

        private Task SetStatusAsync(int id, int status) =>
            _db.Updateable<POSM_设备注册信息表>()
                .SetColumns(row => new POSM_设备注册信息表 { 设备状态 = status })
                .Where(row => row.ID == id)
                .ExecuteCommandAsync();

        public void Dispose()
        {
            _db.Dispose();
            _connection.Dispose();
        }
    }
}
