using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class FaceMatchSqlServerFactAttribute : FactAttribute
{
    public const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public FaceMatchSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过人脸比对管理端的真实 SQL Server 集成测试。";
        }
    }
}

/// <summary>
/// 人脸比对管理端在真实 SQL Server 上的回归：表用迁移 20261014.001 的真实建表脚本创建（含 date / datetime2 列、自增审计主键、
/// 有效登记过滤唯一索引、外键级联），覆盖 SQLite 测不出的部分：条件 UPDATE 的 SQL 翻译、驱动层唯一冲突号（2601 / 2627）分类、
/// 日期列往返、分批删除的参数化 SQL。每个用例创建 GUID 命名的独立数据库并在结束时删除。
/// </summary>
[Trait("Category", "SQL")]
public sealed class FaceMatchSqlServerTests
{
    private static readonly FaceMatchActor Admin = FaceMatchTestHarness.Admin;

    [FaceMatchSqlServerFact]
    public async Task SQLServer_档案保存读取_date与datetime2列往返_再次保存是更新()
    {
        await using var server = await ServerDatabase.CreateAsync();
        using var h = server.CreateHarness();

        var request = new FaceStoreProfileUpdateRequest
        {
            Mode = FaceStoreMode.Match,
            PiaReference = "PIA-1",
            SignageInstalledOn = "2026-09-01",
            StaffNoticeDate = "2026-09-01",
            LegalSignoffBy = "Counsel",
            LegalSignoffAt = new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc),
            CrossBorderRegion = "ap-guangzhou",
            CrossBorderReviewRef = "XB-1",
            CrossBorderReviewedBy = "Counsel",
            CrossBorderReviewedAt = new DateTime(2026, 10, 11, 4, 5, 6, DateTimeKind.Utc),
        };
        var saved = await h.Profiles.UpdateAsync("2001", request, Admin);
        await h.Profiles.UpdateAsync("2001", request with { PiaReference = "PIA-2" }, Admin);

        var read = await h.Profiles.GetAsync("2001");
        Assert.Equal("PIA-2", read.PiaReference);
        Assert.Equal("2026-09-01", read.SignageInstalledOn);
        Assert.Equal(new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc), read.LegalSignoffAt);
        Assert.True(read.CrossBorderReviewValid);
        Assert.Equal("NSW", saved.Jurisdiction);
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.FaceStoreProfile"));
        Assert.Equal("2026-09-01", await server.ScalarAsync("SELECT CONVERT(varchar(10), SignageInstalledOn, 23) FROM dbo.FaceStoreProfile"));
        Assert.Equal("date", await server.ScalarAsync("SELECT t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('dbo.FaceStoreProfile') AND c.name = 'SignageInstalledOn'"));

        var list = await h.Profiles.ListAsync();
        Assert.Equal("2001", Assert.Single(list).StoreCode);
    }

    [FaceMatchSqlServerFact]
    public async Task SQLServer_审计_自增主键_Result长度受限_倒序分页()
    {
        await using var server = await ServerDatabase.CreateAsync();
        using var h = server.CreateHarness();

        for (var i = 0; i < 5; i++)
        {
            h.Time.Now = FaceMatchTestDatabase.Now.AddMinutes(i);
            await h.Audit.WriteAsync(Admin, "profile.update", "1013", $"t{i}", new string('r', 50));
        }

        var page = await h.Traffic.ListAuditAsync("1013", null, null, null, 1, 3);
        Assert.Equal(["t4", "t3", "t2"], page.Items.Select(a => a.TargetRef));
        Assert.True(page.HasMore);
        Assert.Equal(32, page.Items[0].Result.Length);
        Assert.True(page.Items[0].AuditId > page.Items[1].AuditId);
        Assert.Equal(FaceMatchTestDatabase.Now.AddMinutes(4), page.Items[0].AtUtc);
    }

    [FaceMatchSqlServerFact]
    public async Task SQLServer_同意登记撤回_端到端_唯一索引冲突2601被分类为已有有效登记()
    {
        await using var server = await ServerDatabase.CreateAsync();
        using var h = server.CreateHarness();
        h.Database.AddUser("u-q", "Quinn", "s-1013");
        var consent = await h.Staff.CreateConsentAsync(
            new FaceConsentCreateRequest { UserGuid = "u-q", ConsentVersion = "v1", SignedAtUtc = h.At(TimeSpan.FromDays(-1)), Method = "paper" },
            new FaceMatchUpload("s.jpg", "image/jpeg", FaceImageTestData.Jpeg), Admin);

        var enrollment = await h.Staff.CreateEnrollmentAsync(
            new FaceEnrollmentCreateRequest { UserGuid = "u-q", ConsentId = consent.ConsentId }, FaceMatchTestHarness.Photos(3), Admin);
        Assert.Equal("active", enrollment.StatusName);
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.FaceEnrollment WHERE Status = 1"));

        // 撤回后可以再登记；并发抢先写入同一员工有效登记 → 驱动层 2601 → 409。
        await h.Staff.WithdrawEnrollmentAsync(enrollment.EnrollmentId, new(), Admin);
        h.Library.FailOn = op =>
        {
            if (op == "CreatePerson")
            {
                server.ExecuteSync($"""
INSERT dbo.FaceEnrollment (EnrollmentId, UserGuid, ConsentId, TencentPersonId, PhotoObjectKeysJson, Status, EnrolledAtUtc, EnrolledBy)
VALUES (REPLICATE('e', 32), N'u-q', '{consent.ConsentId}', N'st_other', N'[]', 1, SYSUTCDATETIME(), N'race')
""");
            }

            return null;
        };
        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => h.Staff.CreateEnrollmentAsync(
            new FaceEnrollmentCreateRequest { UserGuid = "u-q", ConsentId = consent.ConsentId }, FaceMatchTestHarness.Photos(3), Admin));
        Assert.Equal("FACE_MATCH_ENROLLMENT_EXISTS", ex.Code);
        Assert.Empty(h.Library.PersonGroup);

        // 撤回同意：两处都删；同意记录保留。
        h.Library.FailOn = null;
        server.ExecuteSync("UPDATE dbo.FaceEnrollment SET Status = 2, WithdrawnAtUtc = SYSUTCDATETIME() WHERE EnrollmentId = REPLICATE('e', 32)");
        var again = await h.Staff.CreateEnrollmentAsync(
            new FaceEnrollmentCreateRequest { UserGuid = "u-q", ConsentId = consent.ConsentId }, FaceMatchTestHarness.Photos(3), Admin);
        var withdrawn = await h.Staff.WithdrawConsentAsync(consent.ConsentId, new() { Reason = "员工申请" }, Admin);
        Assert.Equal(1, withdrawn.WithdrawnEnrollmentCount);
        Assert.Equal("2", await server.ScalarAsync($"SELECT Status FROM dbo.FaceEnrollment WHERE EnrollmentId = '{again.EnrollmentId}'"));
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.FaceConsentRecord WHERE WithdrawnAtUtc IS NOT NULL"));
    }

    [FaceMatchSqlServerFact]
    public async Task SQLServer_保留清理_参数化批量删除_图片随外键级联_只删过期行()
    {
        await using var server = await ServerDatabase.CreateAsync();
        using var h = server.CreateHarness();
        server.ExecuteSync("INSERT dbo.FaceStoreProfile (StoreCode, Mode) VALUES (N'1013', 1)");
        server.ExecuteSync("""
INSERT dbo.FaceCamera (CameraId, StoreCode, TokenSha256) VALUES (N'cam-1', N'1013', REPLICATE('a', 64));
WITH n AS (SELECT TOP (1200) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.FaceCaptureEvent (CaptureId, CameraId, StoreCode, ReceivedAtUtc, BodySha256, BodyBytes, ParseStatus, RawStorage, CreatedAtUtc)
SELECT RIGHT(REPLICATE('0', 32) + CONVERT(varchar(12), i), 32), N'cam-1', N'1013', DATEADD(day, -20, SYSUTCDATETIME()), REPLICATE('b', 64), 1, 0, N'local', SYSUTCDATETIME() FROM n;
INSERT dbo.FaceCaptureEvent (CaptureId, CameraId, StoreCode, ReceivedAtUtc, BodySha256, BodyBytes, ParseStatus, RawStorage, CreatedAtUtc)
VALUES (REPLICATE('f', 32), N'cam-1', N'1013', SYSUTCDATETIME(), REPLICATE('b', 64), 1, 0, N'local', SYSUTCDATETIME());
INSERT dbo.FaceCaptureImage (ImageId, CaptureId, Kind, PartIndex, Storage, ObjectKey, ByteSize, Sha256, CreatedAtUtc)
SELECT RIGHT(REPLICATE('1', 32), 32), RIGHT(REPLICATE('0', 32) + '5', 32), 0, 1, N'local', N'images/x.jpg', 3, REPLICATE('b', 64), SYSUTCDATETIME();
INSERT dbo.FaceCaptureImage (ImageId, CaptureId, Kind, PartIndex, Storage, ObjectKey, ByteSize, Sha256, CreatedAtUtc)
VALUES (REPLICATE('2', 32), REPLICATE('f', 32), 0, 1, N'local', N'images/y.jpg', 3, REPLICATE('b', 64), SYSUTCDATETIME());
""");
        h.Time.Now = DateTime.UtcNow;   // 真实库里用 SYSUTCDATETIME 造的数据，用真实当前时间作为「现在」

        var deleted = await h.Maintenance.CleanupCaptureRecordsAsync(default);

        Assert.Equal(1200, deleted);
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.FaceCaptureEvent"));
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.FaceCaptureImage"));
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.FaceAuditLog WHERE Action = 'capture.retention-cleanup'"));
    }

    [FaceMatchSqlServerFact]
    public async Task SQLServer_名单批准_条件更新生效_其他类别两人批准_复核状态机_误报统计()
    {
        await using var server = await ServerDatabase.CreateAsync();
        using var h = server.CreateHarness();
        foreach (var id in new[] { "sub-1", "app-1", "app-2", "rev-a", "rev-b" })
        {
            h.Database.AddUser(id, id, "s-1013");
        }

        server.ExecuteSync("INSERT dbo.FaceStoreProfile (StoreCode, Mode, UnwelcomeListEnabled) VALUES (N'1013', 1, 1)");
        var submitter = new FaceMatchActor("sub-1", "sub-1", IsAdmin: true);
        var entry = await h.Unwelcome.CreateEntryAsync(new FaceUnwelcomeEntryCreateRequest
        {
            IncidentCategory = FaceIncidentCategory.Other,
            CategoryNote = "说明",
            EvidenceRef = "EVID-1",
            StoreScope = ["1013"],
            ExpiresAtUtc = h.At(TimeSpan.FromDays(100)),
        }, FaceMatchTestHarness.Photos(2), submitter);

        var first = await h.Unwelcome.ApproveEntryAsync(entry.EntryId, new FaceMatchActor("app-1", "app-1", true));
        Assert.Equal("pending", first.StatusName);
        var second = await h.Unwelcome.ApproveEntryAsync(entry.EntryId, new FaceMatchActor("app-2", "app-2", true));
        Assert.Equal("active", second.StatusName);
        Assert.Equal("app-1", await server.ScalarAsync($"SELECT ApprovedBy FROM dbo.FaceUnwelcomeEntry WHERE EntryId = '{entry.EntryId}'"));
        Assert.Equal("app-2", await server.ScalarAsync($"SELECT SecondApprovedBy FROM dbo.FaceUnwelcomeEntry WHERE EntryId = '{entry.EntryId}'"));

        // 接收端按设计列写入命中任务（不写 NotifiedAtUtc 等），主 API 复核。
        var taskId = new string('7', 32);
        server.ExecuteSync($"""
INSERT dbo.FaceMatchTask (TaskId, StoreCode, CameraId, CapturedAtUtc, EntryId, Score, ImageObjectKey, ReviewStatus, ImageDeleteAtUtc)
VALUES ('{taskId}', N'1013', N'cam-1', '{FaceMatchTestDatabase.Now.AddMinutes(-5):yyyy-MM-dd HH:mm:ss}', '{entry.EntryId}', 88.5, N'face-match/hit/1013/a.jpg', 0, '{FaceMatchTestDatabase.Now.AddDays(7):yyyy-MM-dd HH:mm:ss}')
""");
        h.Store.Objects["face-match/hit/1013/a.jpg"] = FaceImageTestData.Jpeg;
        var reviewerA = new FaceMatchActor("rev-a", "rev-a");
        var reviewerB = new FaceMatchActor("rev-b", "rev-b");

        var listed = await h.Unwelcome.ListTasksAsync("pending", null, 1, 20, reviewerA);
        Assert.Equal(89, Assert.Single(listed.Items).ScorePercent);
        var step1 = await h.Unwelcome.ReviewTaskAsync(taskId, new() { Verdict = "match" }, reviewerA);
        Assert.Equal("second", step1.NextStep);
        var same = await Assert.ThrowsAsync<FaceMatchException>(() => h.Unwelcome.ReviewTaskAsync(taskId, new() { Verdict = "match" }, reviewerA));
        Assert.Equal("FACE_MATCH_SAME_REVIEWER", same.Code);
        var done = await h.Unwelcome.ReviewTaskAsync(taskId, new() { Verdict = "notMatch" }, reviewerB);
        Assert.Equal("dismissed", done.ReviewStatusName);
        Assert.Equal("1", await server.ScalarAsync($"SELECT ReviewStatus FROM dbo.FaceMatchTask WHERE TaskId = '{taskId}'"));
        Assert.Equal("rev-b", await server.ScalarAsync($"SELECT SecondReviewerId FROM dbo.FaceMatchTask WHERE TaskId = '{taskId}'"));

        var stats = await h.Unwelcome.GetStatsAsync(submitter);
        Assert.Equal(1, stats.DismissedCount);
        Assert.Equal(1m, stats.DismissRate);
        Assert.Equal(1, stats.ScoreBuckets.Single(b => b.From == 85).DismissedCount);

        // 撤回：状态、DeletePerson、参考照片、键清空。
        var withdrawn = await h.Unwelcome.WithdrawEntryAsync(entry.EntryId, new(), submitter);
        Assert.Equal("withdrawn", withdrawn.StatusName);
        Assert.Equal("[]", await server.ScalarAsync($"SELECT PhotoObjectKeysJson FROM dbo.FaceUnwelcomeEntry WHERE EntryId = '{entry.EntryId}'"));
    }

    [FaceMatchSqlServerFact]
    public async Task SQLServer_命中提示_轮询未提示任务_发布后标记_访客库按门店本地午夜删除_客流按本地日归并()
    {
        await using var server = await ServerDatabase.CreateAsync();
        using var h = server.CreateHarness();
        var entryId = new string('3', 32);
        var taskId = new string('8', 32);
        server.ExecuteSync($"""
INSERT dbo.FaceMatchTask (TaskId, StoreCode, CameraId, CapturedAtUtc, EntryId, Score, ImageObjectKey, ReviewStatus, ImageDeleteAtUtc)
VALUES ('{taskId}', N'1013', N'cam-1', '{FaceMatchTestDatabase.Now.AddMinutes(-5):yyyy-MM-dd HH:mm:ss}', '{entryId}', 91.2, N'face-match/hit/1013/a.jpg', 0, '{FaceMatchTestDatabase.Now.AddDays(7):yyyy-MM-dd HH:mm:ss}')
""");
        h.Publisher.TransactionProbe = () => h.Db.Ado.IsAnyTran();

        Assert.Equal(1, await h.Notifier.PublishPendingAsync(default));

        Assert.Equal($"unwelcome-hit:{taskId}", Assert.Single(h.Publisher.Requests).DedupeKey);
        Assert.False(Assert.Single(h.Publisher.InTransaction));
        Assert.Equal("0", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.FaceMatchTask WHERE NotifiedAtUtc IS NULL"));
        Assert.Equal(0, await h.Notifier.PublishPendingAsync(default));

        // 访客库：date 列读成 DateTime，布里斯班 2026-10-14 的营业日在 2026-10-14T14:00Z 之后才可删。
        server.ExecuteSync("INSERT dbo.FaceVisitGroup (GroupId, StoreCode, BusinessDate, CreatedAtUtc, DeleteAfterUtc) VALUES (N'v1013_20261014', N'1013', '2026-10-14', '2026-10-13 15:00:00', '2026-10-20 00:00:00')");
        Assert.Equal(0, await h.Maintenance.CleanupVisitGroupsAsync(default));
        h.Time.Now = new DateTime(2026, 10, 14, 14, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, await h.Maintenance.CleanupVisitGroupsAsync(default));
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.FaceVisitGroup WHERE DeletedAtUtc IS NOT NULL"));

        // 客流：datetime2 整点桶按门店本地日归并（布里斯班 UTC+10）。
        server.ExecuteSync("INSERT dbo.FaceTrafficBucket (StoreCode, BucketStartUtc, CaptureCount, UniqueVisitorCount) VALUES (N'1013', '2026-10-13 13:00:00', 5, 2), (N'1013', '2026-10-13 14:00:00', 7, 3)");
        var traffic = await h.Traffic.GetTrafficAsync("1013", "2026-10-13", "2026-10-14", Admin);
        Assert.Equal([5, 7], traffic.Days.Select(d => d.Counts.CaptureCount));
        Assert.Equal(5, traffic.Days[0].Counts.CaptureCount);
        Assert.Equal(3, traffic.Days[1].Counts.UniqueVisitorCount);
    }

    private sealed class ServerDatabase : IAsyncDisposable
    {
        private readonly string _serverConnectionString;
        private readonly string _databaseName;

        private ServerDatabase(string serverConnectionString, string databaseName, string connectionString)
        {
            _serverConnectionString = serverConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<ServerDatabase> CreateAsync()
        {
            var server = Environment.GetEnvironmentVariable(FaceMatchSqlServerFactAttribute.ConnectionEnvironmentVariable)!;
            var name = $"hb_facematch_{Guid.NewGuid():N}";
            await ExecuteOnAsync(server, $"CREATE DATABASE [{name}] COLLATE Chinese_PRC_90_CI_AS;");
            var database = new ServerDatabase(server, name, new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString);
            using (var db = database.NewClient())
            {
                db.CodeFirst.InitTables(typeof(Store), typeof(User), typeof(UserStore));
            }

            // 真实迁移建表脚本：列类型、主键、过滤索引与生产一致，并跑一遍只读门禁。
            foreach (var sql in new[]
                     {
                         FaceCaptureSchema.ApplySql, FaceMatchSchema.ApplySql, AppNotificationSchema.ApplySql,
                         FaceMatchSchema.VerifySql,
                     })
            {
                await database.ExecuteAsync(sql);
            }

            return database;
        }

        public FaceMatchTestHarness CreateHarness()
        {
            var db = NewClient();
            var database = new FaceMatchTestDatabase(db, () => { });
            return new FaceMatchTestHarness(database);
        }

        private SqlSugarClient NewClient() =>
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
