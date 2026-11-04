using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Security;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class RemoteMaintenanceCommitTests
{
    private const string OldRustdeskId = "500717782";
    private const string NewRustdeskId = "116920848";
    private const string Password = "test-password";

    [Fact]
    public async Task Commit_AfterReinstallWithNewRustdeskId_RefreshesIdAndReplaysOriginalResponse()
    {
        using var fixture = new CommitFixture();

        var result = await fixture.Service.CommitAsync(fixture.Request(NewRustdeskId, "1.4.9-hb1", Password));

        Assert.True(result.Success, result.Code);
        // 监控 token 与首次提交相同，已装好的状态服务不用重新登记。
        Assert.Equal(fixture.OriginalResponse.MonitorToken, result.Data!.MonitorToken);
        Assert.Equal(fixture.OriginalResponse.HeartbeatUrl, result.Data.HeartbeatUrl);
        Assert.Equal(1, fixture.UpdateCount);

        // 执行服务实际生成的参数化 UPDATE，核对只改 ID/版本且受同一已提交操作约束。
        var generated = fixture.Update.ToSql();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE HBweb_RemoteMaintenanceDevice (
                Id TEXT PRIMARY KEY, RustdeskId TEXT, ClientVersion TEXT, MonitorTokenHash TEXT,
                LastOperationId TEXT, CommitResponseCiphertext TEXT, IsDeleted INTEGER);
            INSERT INTO HBweb_RemoteMaintenanceDevice VALUES (@id, @oldId, '1.4.9', 'hash', @op, 'cipher', 0);
            """;
        setup.Parameters.AddWithValue("@id", fixture.Row.Id);
        setup.Parameters.AddWithValue("@oldId", OldRustdeskId);
        setup.Parameters.AddWithValue("@op", fixture.Row.LastOperationId!.Value);
        setup.ExecuteNonQuery();
        using var command = connection.CreateCommand();
        command.CommandText = generated.Key;
        foreach (var parameter in generated.Value)
            command.Parameters.AddWithValue(parameter.ParameterName, parameter.Value ?? DBNull.Value);
        Assert.Equal(1, command.ExecuteNonQuery());
        using (var readback = connection.CreateCommand())
        {
            readback.CommandText = "SELECT RustdeskId, ClientVersion, MonitorTokenHash, CommitResponseCiphertext FROM HBweb_RemoteMaintenanceDevice";
            using var reader = readback.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(NewRustdeskId, reader.GetString(0));
            Assert.Equal("1.4.9-hb1", reader.GetString(1));
            Assert.Equal("hash", reader.GetString(2));
            Assert.Equal("cipher", reader.GetString(3));
        }
        // 其他操作号或已删除设备必须被数据库条件挡住。
        using var mutate = connection.CreateCommand();
        mutate.CommandText = "UPDATE HBweb_RemoteMaintenanceDevice SET LastOperationId = @other";
        mutate.Parameters.AddWithValue("@other", Guid.NewGuid());
        mutate.ExecuteNonQuery();
        Assert.Equal(0, command.ExecuteNonQuery());
        mutate.CommandText = "UPDATE HBweb_RemoteMaintenanceDevice SET LastOperationId = @op, IsDeleted = 1";
        mutate.Parameters.AddWithValue("@op", fixture.Row.LastOperationId!.Value);
        mutate.ExecuteNonQuery();
        Assert.Equal(0, command.ExecuteNonQuery());
    }

    [Fact]
    public async Task Commit_RetryWithSameClientInfo_ReplaysWithoutWriting()
    {
        using var fixture = new CommitFixture();

        var result = await fixture.Service.CommitAsync(fixture.Request(OldRustdeskId, "1.4.9", Password));

        Assert.True(result.Success, result.Code);
        Assert.Equal(fixture.OriginalResponse.MonitorToken, result.Data!.MonitorToken);
        Assert.Equal(0, fixture.UpdateCount);
    }

    [Fact]
    public async Task Commit_WithNewRustdeskIdButWrongPassword_IsRejectedWithoutWriting()
    {
        using var fixture = new CommitFixture();

        var result = await fixture.Service.CommitAsync(fixture.Request(NewRustdeskId, "1.4.9", "wrong-password"));

        Assert.False(result.Success);
        Assert.Equal("REMOTE_MAINTENANCE_PASSWORD_MISMATCH", result.Code);
        Assert.Equal(0, fixture.UpdateCount);
    }

    [Fact]
    public async Task Commit_WithNewRustdeskIdWhenRefreshLosesRace_ReturnsConflict()
    {
        using var fixture = new CommitFixture(affectedRows: 0);

        var result = await fixture.Service.CommitAsync(fixture.Request(NewRustdeskId, "1.4.9", Password));

        Assert.False(result.Success);
        Assert.Equal("REMOTE_MAINTENANCE_OPERATION_CONFLICT", result.Code);
        Assert.Equal(1, fixture.UpdateCount);
    }

    private sealed class CommitFixture : IDisposable
    {
        private readonly SqlSugarClient _sql = new(new ConnectionConfig
        {
            ConnectionString = "Server=127.0.0.1;Database=unused;Integrated Security=true;",
            DbType = SqlSugar.DbType.SqlServer,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = true
        });

        public RemoteMaintenanceDevice Row { get; }
        public RemoteMaintenanceCommitResponseDto OriginalResponse { get; }
        public IUpdateable<RemoteMaintenanceDevice> Update { get; }
        public RemoteMaintenanceService Service { get; }
        public int UpdateCount { get; private set; }

        public CommitFixture(int affectedRows = 1)
        {
            var protector = RemoteMaintenanceDataProtection.CreateProtector(new EphemeralDataProtectionProvider());
            var deviceId = Guid.NewGuid();
            OriginalResponse = new RemoteMaintenanceCommitResponseDto
            {
                DeviceId = deviceId, MonitorToken = "original-token", HeartbeatUrl = "https://example.test/heartbeat"
            };
            // 已提交的设备：首次 commit 登记了旧 ID，并保存了密码与提交响应密文。
            Row = new RemoteMaintenanceDevice
            {
                Id = deviceId, DeviceRegistrationId = 2007, HardwareId = "test-hardware",
                LastOperationId = Guid.NewGuid(), RustdeskId = OldRustdeskId, ClientVersion = "1.4.9",
                MonitorTokenHash = "hash",
                CredentialCiphertext = protector.ProtectPassword(Password),
                CommitResponseCiphertext = protector.ProtectOperationResponse(JsonSerializer.Serialize(OriginalResponse))
            };

            Update = _sql.Updateable<RemoteMaintenanceDevice>();
            var writer = new Mock<IUpdateable<RemoteMaintenanceDevice>>(MockBehavior.Strict);
            writer.Setup(x => x.SetColumns(It.IsAny<Expression<Func<RemoteMaintenanceDevice, RemoteMaintenanceDevice>>>()))
                .Callback<Expression<Func<RemoteMaintenanceDevice, RemoteMaintenanceDevice>>>(columns => Update.SetColumns(columns))
                .Returns(writer.Object);
            writer.Setup(x => x.Where(It.IsAny<Expression<Func<RemoteMaintenanceDevice, bool>>>()))
                .Callback<Expression<Func<RemoteMaintenanceDevice, bool>>>(predicate => Update.Where(predicate))
                .Returns(writer.Object);
            writer.Setup(x => x.ExecuteCommandAsync()).Callback(() => UpdateCount++).ReturnsAsync(affectedRows);
            var devices = new Mock<ISugarQueryable<RemoteMaintenanceDevice>>(MockBehavior.Strict);
            devices.Setup(x => x.FirstAsync(It.IsAny<Expression<Func<RemoteMaintenanceDevice, bool>>>())).ReturnsAsync(Row);
            var ado = new Mock<IAdo>(MockBehavior.Strict);
            ado.Setup(x => x.GetIntAsync(It.IsAny<string>(), It.Is<SugarParameter[]>(p => p.Length == 0))).ReturnsAsync(1);
            var main = new Mock<ISqlSugarClient>(MockBehavior.Strict);
            main.SetupGet(x => x.Ado).Returns(ado.Object);
            main.Setup(x => x.Queryable<RemoteMaintenanceDevice>()).Returns(devices.Object);
            main.Setup(x => x.Updateable<RemoteMaintenanceDevice>()).Returns(writer.Object);

            var registrations = new Mock<ISugarQueryable<POSM_设备注册信息表>>(MockBehavior.Strict);
            registrations.Setup(x => x.Where(It.IsAny<Expression<Func<POSM_设备注册信息表, bool>>>())).Returns(registrations.Object);
            registrations.Setup(x => x.OrderByDescending(It.IsAny<Expression<Func<POSM_设备注册信息表, object>>>())).Returns(registrations.Object);
            registrations.Setup(x => x.FirstAsync()).ReturnsAsync(new POSM_设备注册信息表
            {
                ID = Row.DeviceRegistrationId, 分店代码 = "1042", 系统设备编号 = "POS_1042_2007",
                设备状态 = 1, 设备类型 = "POS", 设备系统 = "Windows"
            });
            var posm = new Mock<ISqlSugarClient>(MockBehavior.Strict);
            posm.Setup(x => x.Queryable<POSM_设备注册信息表>()).Returns(registrations.Object);
            var context = CreateContext<SqlSugarContext>(main.Object);
            Service = new RemoteMaintenanceService(context, CreateContext<POSMSqlSugarContext>(posm.Object), protector,
                Options.Create(new RemoteMaintenanceOptions { Enabled = true }), NullLogger<RemoteMaintenanceService>.Instance,
                new RemoteMaintenanceSchemaReadiness(context, NullLogger<RemoteMaintenanceSchemaReadiness>.Instance));
        }

        public RemoteMaintenanceCommitInternalRequest Request(string rustdeskId, string clientVersion, string password) =>
            new(Row.LastOperationId!.Value, Row.HardwareId, rustdeskId, clientVersion, password);

        private static T CreateContext<T>(ISqlSugarClient db)
        {
            // 仅替换数据库边界，真实业务方法和 SqlServer SQL 生成器照常运行。
            var context = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            typeof(T).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, db);
            return context;
        }

        public void Dispose() => _sql.Dispose();
    }
}
