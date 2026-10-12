using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Features.FaceCameras;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class FaceCameraSqlServerFactAttribute : FactAttribute
{
    public const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public FaceCameraSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过人脸摄像机后台管理的真实 SQL Server 集成测试。";
        }
    }
}

/// <summary>
/// 摄像机后台管理在真实 SQL Server 上的回归：表用迁移 20261013.001 的真实建表脚本创建（含唯一索引与列类型），
/// 覆盖 SQLite 测不出的部分：统计 SQL、datetimeoffset 列映射、驱动层唯一冲突号（2627 / 2601）分类。
/// 每个用例创建 GUID 命名的独立数据库并在结束时删除。
/// </summary>
[Trait("Category", "SQL")]
public sealed class FaceCameraAdminSqlServerTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 2, 0, 0, DateTimeKind.Utc);
    private static readonly FaceCameraActor Actor = new("user-admin-1", "admin.tester");

    [FaceCameraSqlServerFact]
    public async Task SQLServer_创建_统计_最近抓拍_走真实表结构()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        var service = CreateService(db);

        var created = await service.CreateAsync(new FaceCameraCreateRequest { CameraId = "cam-1", StoreCode = "1013", Note = "正门" }, Actor);
        await service.CreateAsync(new FaceCameraCreateRequest { CameraId = "cam-2", StoreCode = "1013" }, Actor);
        await ExecuteAsync(database, $"UPDATE dbo.FaceCamera SET LastSeenAtUtc = '{Now.AddMinutes(-9):yyyy-MM-dd HH:mm:ss}' WHERE CameraId = 'cam-1'");
        await InsertEventAsync(database, "e01", "cam-1", Now.AddHours(-1), 3, 0, null, "2026-10-13T12:59:00+10:00");
        await InsertEventAsync(database, "e02", "cam-1", Now.AddHours(-2), 5, 0, "e01", null);
        await InsertEventAsync(database, "e03", "cam-1", Now.AddHours(-3), 0, 2, null, null);
        await InsertEventAsync(database, "e04", "cam-1", Now.AddHours(-24).AddSeconds(-1), 9, 0, null, null);
        await InsertEventAsync(database, "e05", "cam-2", Now.AddDays(-5), 1, 0, null, null);

        var list = await service.ListAsync();
        var cam1 = list.Items.Single(item => item.CameraId == "cam-1");
        var cam2 = list.Items.Single(item => item.CameraId == "cam-2");

        Assert.True(cam1.Online);
        Assert.Equal("Parramatta", cam1.StoreName);
        Assert.Equal(3, cam1.CaptureCount24h);
        Assert.Equal(3, cam1.FaceCount24h);
        Assert.Equal(1, cam1.ParseFailureCount24h);
        Assert.Equal(Now.AddHours(-1), cam1.LastEventAtUtc);
        Assert.Equal(0, cam2.CaptureCount24h);
        Assert.Equal(Now.AddDays(-5), cam2.LastEventAtUtc);
        Assert.False(cam2.Online);

        var page = await service.ListCapturesAsync("cam-1", 1, 2);
        Assert.Equal(["e01", "e02"], page.Items.Select(item => item.CaptureId));
        Assert.True(page.HasMore);
        Assert.Equal(TimeSpan.FromHours(10), page.Items[0].CameraEventTime!.Value.Offset);
        Assert.True(page.Items[1].IsDuplicate);

        // 库里只有哈希，不含明文。
        var stored = await database.ScalarAsync("SELECT TokenSha256 FROM dbo.FaceCamera WHERE CameraId = 'cam-1'");
        Assert.Equal(FaceCameraRules.HashToken(created.Token), stored);
        Assert.Equal(0, int.Parse(await database.ScalarAsync($"SELECT COUNT(*) FROM dbo.FaceCamera WHERE Note LIKE '%{created.Token}%' OR CameraId LIKE '%{created.Token}%'")));
    }

    [FaceCameraSqlServerFact]
    public async Task SQLServer_主键冲突与哈希唯一索引冲突都被正确分类()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        var service = CreateService(db);
        await service.CreateAsync(new FaceCameraCreateRequest { CameraId = "other", StoreCode = "1013" }, Actor);
        await service.CreateAsync(new FaceCameraCreateRequest { CameraId = "cam-1", StoreCode = "1013" }, Actor);

        // 1) 插入前被别人抢先写了同名 CameraId → 主键冲突 → 409 ID_EXISTS。
        var raced = false;
        db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (raced || !sql.TrimStart().StartsWith("INSERT INTO", StringComparison.OrdinalIgnoreCase)) return;
            raced = true;
            database.ExecuteSync("INSERT INTO dbo.FaceCamera (CameraId, StoreCode, TokenSha256) VALUES ('cam-2', '1013', REPLICATE('c', 64))");
        };
        var idEx = await Assert.ThrowsAsync<FaceCameraException>(() =>
            service.CreateAsync(new FaceCameraCreateRequest { CameraId = "cam-2", StoreCode = "1013" }, Actor));
        db.Aop.OnLogExecuting = null;
        Assert.Equal("FACE_CAMERA_ID_EXISTS", idEx.Code);

        // 2) 轮换时第一次生成的哈希被别人占住 → 唯一索引冲突 → 重试换新 token，最终成功。
        var collisions = 0;
        db.Aop.OnLogExecuting = (sql, parameters) =>
        {
            if (collisions > 0 || !sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)) return;
            var hash = parameters.First(parameter => parameter.ParameterName.Contains("TokenSha256", StringComparison.OrdinalIgnoreCase)).Value!.ToString()!;
            collisions++;
            database.ExecuteSync($"UPDATE dbo.FaceCamera SET TokenSha256 = '{hash}' WHERE CameraId = 'other'");
        };
        var rotated = await service.RotateTokenAsync("cam-1", Actor);
        db.Aop.OnLogExecuting = null;
        Assert.Equal(1, collisions);
        Assert.Equal(FaceCameraRules.HashToken(rotated.Token), await database.ScalarAsync("SELECT TokenSha256 FROM dbo.FaceCamera WHERE CameraId = 'cam-1'"));
    }

    private static FaceCameraAdminService CreateService(ISqlSugarClient db) =>
        new(
            FaceCameraAdminTests.CreateSqlSugarContext(db),
            new FaceCameraAdminTests.MutableTimeProvider(Now),
            new ConfigurationBuilder().Build(),
            new AuditCapturingLoggerProvider().CreateLogger<FaceCameraAdminService>());

    private static Task InsertEventAsync(
        IsolatedDatabase database, string captureId, string cameraId, DateTime receivedAt, int faceCount, int parseStatus,
        string? duplicateOf, string? cameraEventTime) =>
        ExecuteAsync(database, $"""
INSERT INTO dbo.FaceCaptureEvent
 (CaptureId, CameraId, StoreCode, ReceivedAtUtc, CameraEventTime, BodySha256, BodyBytes, DuplicateOfCaptureId, FaceCount, ParseStatus, RawStorage, CreatedAtUtc)
VALUES
 ('{captureId}', '{cameraId}', '1013', '{receivedAt:yyyy-MM-dd HH:mm:ss}', {(cameraEventTime == null ? "NULL" : $"'{cameraEventTime}'")},
  REPLICATE('a', 64), 100, {(duplicateOf == null ? "NULL" : $"'{duplicateOf}'")}, {faceCount}, {parseStatus}, 'local', '{receivedAt:yyyy-MM-dd HH:mm:ss}')
""");

    private static Task ExecuteAsync(IsolatedDatabase database, string sql) => database.ExecuteAsync(sql);

    private sealed class IsolatedDatabase : IAsyncDisposable
    {
        private readonly string _serverConnectionString;
        private readonly string _databaseName;

        private IsolatedDatabase(string serverConnectionString, string databaseName, string connectionString)
        {
            _serverConnectionString = serverConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<IsolatedDatabase> CreateAsync()
        {
            var server = Environment.GetEnvironmentVariable(FaceCameraSqlServerFactAttribute.ConnectionEnvironmentVariable)!;
            var name = $"hb_facecam_{Guid.NewGuid():N}";
            await ExecuteOnAsync(server, $"CREATE DATABASE [{name}] COLLATE Chinese_PRC_90_CI_AS;");
            var database = new IsolatedDatabase(server, name, new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString);
            using (var db = database.CreateClient())
            {
                db.CodeFirst.InitTables(typeof(Store));
                db.Insertable(new Store { StoreGUID = "s-1013", StoreCode = "1013", StoreName = "Parramatta" }).ExecuteCommand();
            }

            // 真实迁移建表脚本：列类型、主键、唯一索引与生产一致，并跑一遍只读门禁。
            await database.ExecuteAsync(FaceCaptureSchema.ApplySql);
            await database.ExecuteAsync(FaceCaptureSchema.VerifySql);
            return database;
        }

        public SqlSugarClient CreateClient() =>
            new(new ConnectionConfig
            {
                ConnectionString = ConnectionString,
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });

        public Task ExecuteAsync(string sql) => ExecuteOnAsync(ConnectionString, sql);

        public void ExecuteSync(string sql) => ExecuteOnAsync(ConnectionString, sql).GetAwaiter().GetResult();

        public async Task<string> ScalarAsync(string sql)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
        }

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await ExecuteOnAsync(_serverConnectionString,
                $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];");
        }

        private static async Task ExecuteOnAsync(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }
}
