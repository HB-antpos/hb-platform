using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using SqlSugar;
using System.Text.RegularExpressions;
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
        // 非管理员：「提交人 ≠ 批准人」「Other 需要两位不同的批准人」只对非管理员生效（管理员一个人批准即可）。
        var submitter = new FaceMatchActor("sub-1", "sub-1");
        var entry = await h.Unwelcome.CreateEntryAsync(new FaceUnwelcomeEntryCreateRequest
        {
            IncidentCategory = FaceIncidentCategory.Other,
            CategoryNote = "说明",
            EvidenceRef = "EVID-1",
            StoreScope = ["1013"],
            ExpiresAtUtc = h.At(TimeSpan.FromDays(100)),
        }, FaceMatchTestHarness.Photos(2), submitter);

        var first = await h.Unwelcome.ApproveEntryAsync(entry.EntryId, new FaceMatchActor("app-1", "app-1"));
        Assert.Equal("pending", first.StatusName);
        var second = await h.Unwelcome.ApproveEntryAsync(entry.EntryId, new FaceMatchActor("app-2", "app-2"));
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
        // 一个人就够：第一位复核人判断「不是同一个人」即驳回；已结案后别人再提交是 409。
        var done = await h.Unwelcome.ReviewTaskAsync(taskId, new() { Verdict = "notMatch" }, reviewerA);
        Assert.Equal("dismissed", done.ReviewStatusName);
        Assert.Equal("none", done.NextStep);
        Assert.Equal("1", await server.ScalarAsync($"SELECT ReviewStatus FROM dbo.FaceMatchTask WHERE TaskId = '{taskId}'"));
        Assert.Equal("rev-a", await server.ScalarAsync($"SELECT FirstReviewerId FROM dbo.FaceMatchTask WHERE TaskId = '{taskId}'"));
        var closed = await Assert.ThrowsAsync<FaceMatchException>(() => h.Unwelcome.ReviewTaskAsync(taskId, new() { Verdict = "match" }, reviewerB));
        Assert.Equal("FACE_MATCH_TASK_CLOSED", closed.Code);

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

    // ---------- 分时客流投影（FaceTrafficBucket → FaceTrafficHourly） ----------

    private static FaceTrafficHourlyProjector NewProjector(FaceMatchTestHarness h) =>
        new(h.Database.Context, h.Time, h.Logs.CreateLogger<FaceTrafficHourlyProjector>());

    private const string SydneyDstEndBuckets = """
INSERT dbo.FaceTrafficBucket (StoreCode, BucketStartUtc, CaptureCount, StaffMatchCount, UniqueVisitorCount, RepeatCount, LowQualityCount, ApiFailureCount) VALUES
(N'2001', '2027-04-03 13:00:00', 1, 0, 0, 0, 0, 0),
(N'2001', '2027-04-03 14:00:00', 2, 0, 0, 0, 0, 0),
(N'2001', '2027-04-03 15:00:00', 4, 1, 2, 1, 0, 0),
(N'2001', '2027-04-03 16:00:00', 6, 2, 1, 0, 1, 1),
(N'2001', '2027-04-03 17:00:00', 8, 0, 0, 0, 0, 0)
""";

    [FaceMatchSqlServerFact]
    public async Task SQLServer_分时投影_夏令时结束日两个UTC小时相加_幂等_晚到更新_改时区后窗口内被覆盖()
    {
        await using var server = await ServerDatabase.CreateAsync();
        using var h = server.CreateHarness();
        var projector = NewProjector(h);
        h.Time.Now = new DateTime(2027, 4, 4, 5, 0, 0, DateTimeKind.Utc);   // 悉尼 2027-04-04 15:00 AEST
        server.ExecuteSync(SydneyDstEndBuckets);

        var first = await projector.ProjectRecentAsync(default);

        // 悉尼 2027-04-04 本地 02 点出现两次（15:00Z=AEDT、16:00Z=AEST）：两个 UTC 桶相加落在 StatHour=2。
        Assert.Equal(new FaceTrafficHourlyProjection(false, 1, 0, 4, 0, 0), first);
        Assert.Equal("4", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.FaceTrafficHourly WHERE StoreCode = N'2001'"));
        Assert.Equal("10|3|3|1|1|1|5|Australia/Sydney", await server.ScalarAsync(
            "SELECT CONCAT(CaptureCount, '|', StaffMatchCount, '|', UniqueVisitorCount, '|', RepeatCount, '|', LowQualityCount, '|', ApiFailureCount, '|', VisitCount, '|', TimeZoneId) " +
            "FROM dbo.FaceTrafficHourly WHERE StoreCode = N'2001' AND StatDate = '2027-04-04' AND StatHour = 2"));
        Assert.Equal("date", await server.ScalarAsync("SELECT t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('dbo.FaceTrafficHourly') AND c.name = 'StatDate'"));

        // 幂等：再跑一遍什么都不改（包括 ComputedAtUtc）。
        var before = await server.ScalarAsync("SELECT CONVERT(varchar(40), MAX(ComputedAtUtc), 126) FROM dbo.FaceTrafficHourly");
        h.Time.Now = h.Time.Now.AddMinutes(10);
        var again = await projector.ProjectRecentAsync(default);
        Assert.Equal(0, again.RowsChanged);
        Assert.Equal(before, await server.ScalarAsync("SELECT CONVERT(varchar(40), MAX(ComputedAtUtc), 126) FROM dbo.FaceTrafficHourly"));

        // 晚到的计数：覆盖那一行，其余不动。
        server.ExecuteSync("UPDATE dbo.FaceTrafficBucket SET CaptureCount = 7 WHERE StoreCode = N'2001' AND BucketStartUtc = '2027-04-03 16:00:00'");
        h.Time.Now = h.Time.Now.AddMinutes(10);
        var late = await projector.ProjectRecentAsync(default);
        Assert.Equal(new FaceTrafficHourlyProjection(false, 1, 0, 0, 1, 0), late);
        Assert.Equal("11|6", await server.ScalarAsync(
            "SELECT CONCAT(CaptureCount, '|', VisitCount) FROM dbo.FaceTrafficHourly WHERE StoreCode = N'2001' AND StatDate = '2027-04-04' AND StatHour = 2"));

        // 改时区（悉尼 → 布里斯班）：窗口内整体被新时区覆盖，新增 04-03 23 点一行，四行被更新。
        server.ExecuteSync("UPDATE dbo.Store SET TimeZoneId = N'Australia/Brisbane' WHERE StoreCode = N'2001'");
        h.Time.Now = h.Time.Now.AddMinutes(10);
        var changed = await projector.ProjectRecentAsync(default);
        Assert.Equal(new FaceTrafficHourlyProjection(false, 1, 0, 1, 4, 0), changed);
        Assert.Equal("5", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.FaceTrafficHourly WHERE StoreCode = N'2001' AND TimeZoneId = N'Australia/Brisbane'"));
        Assert.Equal("0", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.FaceTrafficHourly WHERE TimeZoneId = N'Australia/Sydney'"));
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.FaceTrafficHourly WHERE StoreCode = N'2001' AND StatDate = '2027-04-03' AND StatHour = 23"));
    }

    [FaceMatchSqlServerFact]
    public async Task SQLServer_分时投影_分块批写_手工回填整段_历史补齐_表缺失不抛异常()
    {
        await using var server = await ServerDatabase.CreateAsync();
        using var h = server.CreateHarness();
        var projector = NewProjector(h);
        // 布里斯班 2026-10-01 00:00（= 09-30T14:00Z）起连续 288 个小时 = 12 天，超过一个批写分块（100 行）。
        server.ExecuteSync("""
WITH n AS (SELECT TOP (288) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) - 1 AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.FaceTrafficBucket (StoreCode, BucketStartUtc, CaptureCount, StaffMatchCount, UniqueVisitorCount, RepeatCount, LowQualityCount, ApiFailureCount)
SELECT N'1013', DATEADD(hour, i, '2026-09-30 14:00:00'), 1, 0, 0, 0, 0, 0 FROM n;
""");

        var rebuilt = await projector.RebuildAsync("1013", new DateTime(2026, 10, 1), new DateTime(2026, 10, 12), default);

        Assert.Equal(new FaceTrafficHourlyProjection(false, 1, 0, 288, 0, 0), rebuilt);
        Assert.Equal("288|288|12", await server.ScalarAsync(
            "SELECT CONCAT(COUNT(*), '|', SUM(CaptureCount), '|', COUNT(DISTINCT StatDate)) FROM dbo.FaceTrafficHourly WHERE StoreCode = N'1013'"));

        // 全部被覆盖（计数改成 2）：按日期区间 + 小时列表删除再分块插入，行数不变、总和翻倍。
        server.ExecuteSync("UPDATE dbo.FaceTrafficBucket SET CaptureCount = 2");
        var replaced = await projector.RebuildAsync("1013", new DateTime(2026, 10, 1), new DateTime(2026, 10, 12), default);
        Assert.Equal(new FaceTrafficHourlyProjection(false, 1, 0, 0, 288, 0), replaced);
        Assert.Equal("288|576", await server.ScalarAsync("SELECT CONCAT(COUNT(*), '|', SUM(CaptureCount)) FROM dbo.FaceTrafficHourly WHERE StoreCode = N'1013'"));

        // 历史补齐：最早的 3 天分时行丢了，最早的计数桶比最早的分时行还早 → 补出 72 行；已有的日期不动。
        server.ExecuteSync("DELETE FROM dbo.FaceTrafficHourly WHERE StatDate < '2026-10-04'");
        Assert.Equal("216", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.FaceTrafficHourly"));
        var backfilled = await projector.BackfillMissingHistoryAsync(default);
        Assert.Equal(new FaceTrafficHourlyProjection(false, 1, 0, 72, 0, 0), backfilled);
        Assert.Equal("288|576", await server.ScalarAsync("SELECT CONCAT(COUNT(*), '|', SUM(CaptureCount)) FROM dbo.FaceTrafficHourly"));
        Assert.Equal(0, (await projector.BackfillMissingHistoryAsync(default)).RowsChanged);

        // 定期重算：窗口是最近 3 个本地日（基准「现在」布里斯班 2026-10-14 12:00 → 10-12 ~ 10-14），已是最新，不写库。
        var recent = await projector.ProjectRecentAsync(default);
        Assert.Equal(0, recent.RowsChanged);
        Assert.Equal(0, recent.StoresFailed);

        // 只读查询：本地日期区间、门店过滤。
        var queried = await projector.QueryAsync("1013", new DateTime(2026, 10, 11), new DateTime(2026, 10, 12), default);
        Assert.Equal(48, queried.Count);
        Assert.Equal(new DateTime(2026, 10, 11), queried[0].StatDate);
        Assert.Equal(23, queried[^1].StatHour);

        // 迁移未执行（表不存在）：不抛异常，直接标记缺表。
        server.ExecuteSync("DROP TABLE dbo.FaceTrafficHourly");
        Assert.True((await projector.ProjectRecentAsync(default)).SchemaMissing);
        Assert.True((await projector.BackfillMissingHistoryAsync(default)).SchemaMissing);
        Assert.True((await projector.RebuildAsync(null, new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), default)).SchemaMissing);
    }

    [FaceMatchSqlServerFact]
    public async Task SQLServer_分时表与营业额JOIN_文档示例可执行_应急回填SQL与应用口径逐行一致()
    {
        await using var server = await ServerDatabase.CreateAsync();
        using var h = server.CreateHarness();
        var projector = NewProjector(h);
        h.Time.Now = new DateTime(2027, 4, 4, 5, 0, 0, DateTimeKind.Utc);
        server.ExecuteSync(SydneyDstEndBuckets);
        await projector.ProjectRecentAsync(default);

        // 营业额（主库 HourlySalesStatistic，门店本地墙钟的 Date / Hour）：夏令时结束日 02 点的营业额已在 Hour=2 合并。
        h.Db.Insertable(new List<HourlySalesStatistic>
        {
            new() { Date = new DateTime(2027, 4, 4), Hour = 2, BranchCode = "2001", BranchName = "Bankstown", TotalAmount = 123.45m, OrderCount = 5, CustomerCount = 5, UpdateTime = h.Time.Now },
            new() { Date = new DateTime(2027, 4, 4), Hour = 10, BranchCode = "2001", BranchName = "Bankstown", TotalAmount = 50m, OrderCount = 2, CustomerCount = 2, UpdateTime = h.Time.Now },
            new() { Date = new DateTime(2027, 4, 4), Hour = 2, BranchCode = "ALL", BranchName = "All Stores", TotalAmount = 999m, OrderCount = 99, CustomerCount = 99, UpdateTime = h.Time.Now },
            new() { Date = new DateTime(2027, 4, 4), Hour = 1, BranchCode = "1013", BranchName = "Orion", TotalAmount = 77m, OrderCount = 3, CustomerCount = 3, UpdateTime = h.Time.Now },
        }).ExecuteCommand();

        // 文档里的 JOIN 示例原样执行（只把日期换成测试数据的日期）。
        var joinSql = ReadRunbookSql("face-traffic-hourly-join");
        Assert.Contains("'2026-10-01'", joinSql);
        joinSql = joinSql.Replace("'2026-10-01'", "'2027-04-02'").Replace("'2026-10-14'", "'2027-04-04'");
        var rows = await server.QueryRowsAsync(joinSql);

        // 列：StoreCode, StatDate, StatHour, VisitCount, UniqueVisitorCount, TotalAmount, OrderCount, OrdersPerVisit
        var byKey = rows.ToDictionary(r => $"{r[0]}|{r[1]}|{r[2]}");
        Assert.Equal(6, rows.Count);                                    // 2001 的 0/1/2/3 点客流 + 2001 的 10 点只有营业额 + 1013 的 1 点只有营业额；ALL 行被排除
        Assert.DoesNotContain(rows, r => r[0] == "ALL");
        Assert.Equal(["5", "3", "123.4500", "5", "1.0000"], byKey["2001|2027-04-04|2"].Skip(3));   // 02 点：客流与营业额对上同一行
        Assert.Equal(["0", "0", "50.0000", "2", ""], byKey["2001|2027-04-04|10"].Skip(3));          // 只有营业额
        Assert.Equal(["8", "0", "0.0000", "0", "0.0000"], byKey["2001|2027-04-04|3"].Skip(3));      // 只有客流
        Assert.Equal(["0", "0", "77.0000", "3", ""], byKey["1013|2027-04-04|1"].Skip(3));

        // 文档里的应急回填 SQL：对同一段数据重新生成，必须与应用写出的行逐行逐列一致（ComputedAtUtc 除外）。
        const string snapshot = "SELECT STRING_AGG(CONCAT(StatDate, '/', StatHour, '/', CaptureCount, '/', StaffMatchCount, '/', UniqueVisitorCount, '/', RepeatCount, '/', LowQualityCount, '/', ApiFailureCount, '/', VisitCount, '/', TimeZoneId), ';') WITHIN GROUP (ORDER BY StatDate, StatHour) FROM dbo.FaceTrafficHourly WHERE StoreCode = N'2001'";
        var appRows = await server.ScalarAsync(snapshot);
        Assert.Contains("2027-04-04/2/10/3/3/1/1/1/5/Australia/Sydney", appRows);
        server.ExecuteSync("DELETE FROM dbo.FaceTrafficHourly");
        server.ExecuteSync(ReadRunbookSql("face-traffic-hourly-backfill"));
        Assert.Equal(appRows, await server.ScalarAsync(snapshot));
    }

    /// <summary>读取 runbook 里带 <c>&lt;!-- sql:名字 --&gt;</c> 标记的 SQL 代码块：文档里的示例必须真的能跑。</summary>
    private static string ReadRunbookSql(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "docs", "runbooks", "face-ingest.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var text = File.ReadAllText(Path.Combine(directory!.FullName, "docs", "runbooks", "face-ingest.md"));
        var match = Regex.Match(text, "<!-- sql:" + Regex.Escape(name) + @" -->\s*```sql\r?\n(?<sql>.*?)\r?\n```", RegexOptions.Singleline);
        Assert.True(match.Success, $"runbook 里找不到 sql:{name} 代码块");
        return match.Groups["sql"].Value;
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
                // HourlySalesStatistic：营业额分时表（主库实体，CodeFirst 建表），用来验证 runbook 里与营业额 JOIN 的示例。
                db.CodeFirst.InitTables(typeof(Store), typeof(User), typeof(UserStore), typeof(HourlySalesStatistic));
            }

            // 真实迁移建表脚本：列类型、主键、过滤索引与生产一致，并跑一遍只读门禁。
            foreach (var sql in new[]
                     {
                         FaceCaptureSchema.ApplySql, FaceMatchSchema.ApplySql, AppNotificationSchema.ApplySql,
                         FaceMatchSchema.VerifySql,
                         FaceTrafficHourlySchema.ApplySql, FaceTrafficHourlySchema.VerifySql,
                         // 新实体会 SELECT FaceStoreProfile.UnwelcomeAutoConfirmScore：与生产一样先执行这条迁移再用新版代码。
                         FaceUnwelcomeAutoConfirmSchema.ApplySql, FaceUnwelcomeAutoConfirmSchema.VerifySql,
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

        /// <summary>执行一段查询，按行返回每列的文本（NULL 返回空串）；小数按数据库返回的格式。</summary>
        public async Task<List<List<string>>> QueryRowsAsync(string sql)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<List<string>>();
            while (await reader.ReadAsync())
            {
                var row = new List<string>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    row.Add(value is DBNull ? string.Empty : value is DateTime date ? date.ToString("yyyy-MM-dd") : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                }

                rows.Add(row);
            }

            return rows;
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
