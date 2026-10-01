using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class MobileAppBuildCosChecksumSqlServerFactAttribute : FactAttribute
{
    internal const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public MobileAppBuildCosChecksumSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过 APK 镜像校验值写回的 SQL Server 验证。";
        }
    }
}

[Trait("Category", "SQL")]
public sealed class MobileAppBuildCosChecksumSqlServerTests
{
    private const string Sha256 = "5ce1912d1589a0545febc0054b4225eb16bd2ef308b27104f7a0902e1d7cf873";
    private const long FileSize = 173913208;

    [MobileAppBuildCosChecksumSqlServerFact]
    public async Task 镜像成功写回校验值_旧版本写回缺值的行可被补算补齐()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            MobileAppBuildCosChecksumSqlServerFactAttribute.ConnectionEnvironmentVariable)!;
        var databaseName = $"HbMobileAppBuild_{Guid.NewGuid():N}";
        // 测试会建库删库，只允许指向本机隔离实例。
        var dataSource = new SqlConnectionStringBuilder(baseConnectionString).DataSource;
        Assert.True(
            dataSource.StartsWith("127.0.0.1", StringComparison.Ordinal)
                || dataSource.StartsWith("localhost", StringComparison.OrdinalIgnoreCase),
            $"SQL Server 集成测试只允许回环地址: {dataSource}");
        var masterConnectionString = BuildConnectionString(baseConnectionString, "master");
        await ExecuteAsync(masterConnectionString, $"CREATE DATABASE [{databaseName}] COLLATE Chinese_PRC_90_CI_AS;");
        try
        {
            using var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = BuildConnectionString(baseConnectionString, databaseName),
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });
            db.CodeFirst.InitTables<MobileAppBuild>();
            var service = new MobileAppBuildService(
                db,
                Options.Create(new EasWebhookOptions()),
                NullLogger<MobileAppBuildService>.Instance
            );
            var now = new DateTime(2026, 9, 30, 0, 50, 0, DateTimeKind.Utc);

            // 1) 当前版本：SetColumns 闭包里的 SHA-256/大小在 SQL Server 上正常写入。
            var current = await InsertPendingAsync(db, "build-current", now.AddMinutes(-1));
            var currentJob = await service.ClaimNextCosMirrorJobAsync(now, 3, TimeSpan.FromMinutes(30));
            Assert.Equal(current, currentJob!.Id);
            await service.CompleteCosMirrorSuccessAsync(currentJob, MirrorResult("build-current", now));
            var currentSaved = await db.Queryable<MobileAppBuild>().SingleAsync(x => x.Id == current);
            Assert.Equal(Sha256, currentSaved.ArtifactSha256);
            Assert.Equal(FileSize, currentSaved.ArtifactSize);

            // 2) 复现生产：08-05 镜像的旧容器认领后，只按旧 SetColumns 写回 COS 地址与状态。
            var legacy = await InsertPendingAsync(db, "build-legacy", now.AddMinutes(-2));
            var legacyJob = await service.ClaimNextCosMirrorJobAsync(now, 3, TimeSpan.FromMinutes(30));
            Assert.Equal(legacy, legacyJob!.Id);
            var legacyResult = MirrorResult("build-legacy", now);
            await db.Updateable<MobileAppBuild>()
                .SetColumns(x => new MobileAppBuild
                {
                    CosArtifactUrl = legacyResult.ArtifactUrl,
                    CosObjectKey = legacyResult.ObjectKey,
                    CosMirroredAt = legacyResult.MirroredAt,
                    CosMirrorStatus = MobileAppBuildService.CosMirrorStatusSucceeded,
                    CosMirrorError = null,
                })
                .Where(x => x.Id == legacyJob.Id && x.ArtifactUrl == legacyJob.ArtifactUrl)
                .ExecuteCommandAsync();
            var legacySaved = await db.Queryable<MobileAppBuild>().SingleAsync(x => x.Id == legacy);
            Assert.Equal(MobileAppBuildService.CosMirrorStatusSucceeded, legacySaved.CosMirrorStatus);
            Assert.NotNull(legacySaved.CosArtifactUrl);
            Assert.Null(legacySaved.ArtifactSha256);
            Assert.Null(legacySaved.ArtifactSize);
            // 普通镜像队列要求 COS 地址为空，这类行永远不会被重新镜像。
            Assert.Null(await service.ClaimNextCosMirrorJobAsync(now.AddHours(1), 3, TimeSpan.FromMinutes(30)));

            // 3) 补算队列认领它并补齐，补齐后不再认领。
            var backfillJob = await service.ClaimNextCosChecksumBackfillJobAsync(
                now.AddHours(1),
                6,
                TimeSpan.FromMinutes(30)
            );
            Assert.Equal(legacy, backfillJob!.Id);
            await service.CompleteCosChecksumBackfillSuccessAsync(
                backfillJob,
                new MobileAppBuildArtifactChecksum { Sha256 = Sha256, FileSize = FileSize }
            );
            legacySaved = await db.Queryable<MobileAppBuild>().SingleAsync(x => x.Id == legacy);
            Assert.Equal(Sha256, legacySaved.ArtifactSha256);
            Assert.Equal(FileSize, legacySaved.ArtifactSize);
            Assert.Equal(MobileAppBuildService.CosMirrorStatusSucceeded, legacySaved.CosMirrorStatus);
            Assert.Null(await service.ClaimNextCosChecksumBackfillJobAsync(now.AddHours(2), 6, TimeSpan.FromMinutes(30)));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(
                masterConnectionString,
                $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];");
        }
    }

    private static async Task<Guid> InsertPendingAsync(SqlSugarClient db, string easBuildId, DateTime completedAt)
    {
        var id = Guid.NewGuid();
        await db.Insertable(new MobileAppBuild
        {
            Id = id,
            AppKey = "pos-handheld",
            EasBuildId = easBuildId,
            AccountName = "hotbargain",
            ProjectName = "hb-pos-handheld",
            Platform = "android",
            Status = "finished",
            BuildProfile = "production",
            ArtifactUrl = $"https://expo.dev/artifacts/eas/{easBuildId}.apk",
            CosMirrorStatus = MobileAppBuildService.CosMirrorStatusPending,
            CompletedAt = completedAt,
            ReceivedAt = completedAt,
            CreatedAt = completedAt,
        }).ExecuteCommandAsync();
        return id;
    }

    private static MobileAppBuildArtifactMirrorResult MirrorResult(string easBuildId, DateTime mirroredAt) => new()
    {
        ArtifactUrl = $"https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/mobile-app-builds/production/{easBuildId}.apk",
        ObjectKey = $"mobile-app-builds/production/{easBuildId}.apk",
        Sha256 = Sha256,
        FileSize = FileSize,
        MirroredAt = mirroredAt,
    };

    private static string BuildConnectionString(string baseConnectionString, string databaseName) =>
        new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = databaseName }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
