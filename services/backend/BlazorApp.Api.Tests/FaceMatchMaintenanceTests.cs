using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 后台任务：访客库清理、员工库完整性、Capture 记录保留、名单到期与提醒、命中图到期删除、撤回补偿、地域改动作废审查、
/// 命中提示发布，以及调度的空转行为。时间全部来自假的 TimeProvider；门店营业日 / 夏令时用例不依赖机器时区。
/// 基准时间 2026-10-14 02:00 UTC = 布里斯班 12:00（UTC+10）= 悉尼 13:00（夏令时 UTC+11）。
/// </summary>
public sealed class FaceMatchMaintenanceTests : IDisposable
{
    private readonly FaceMatchTestHarness _h = new();

    public FaceMatchMaintenanceTests()
    {
        _h.Database.AddUser("sub-1", "Sub Mitter", "s-1013");
        _h.Database.AddUser("app-1", "App Rover", "s-1013");
        _h.Database.AddUser("rev-a", "Reviewer A", "s-1013");
    }

    public void Dispose() => _h.Dispose();

    // ---------- 访客库 ----------

    [Fact]
    public async Task 访客库_营业日结束后删库_更新DeletedAtUtc_写审计()
    {
        SeedGroup("v1013_20261013", "1013", "2026-10-13", deleteAfter: FaceMatchTestDatabase.Now.AddDays(1));

        var deleted = await _h.Maintenance.CleanupVisitGroupsAsync(default);

        Assert.Equal(1, deleted);
        Assert.Contains("DeleteGroup:v1013_20261013", _h.Library.Calls);
        var row = await _h.Db.Queryable<FaceVisitGroup>().SingleAsync();
        Assert.Equal(FaceMatchTestDatabase.Now, row.DeletedAtUtc);
        var audit = Assert.Single(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());
        Assert.Equal((FaceMatchAuditActions.VisitGroupDelete, "system", "v1013_20261013", "1013"), (audit.Action, audit.ActorId, audit.TargetRef, audit.StoreCode));
    }

    [Fact]
    public async Task 访客库_今天的库在门店本地午夜之前不删_午夜一过就删_布里斯班UTC加10()
    {
        SeedGroup("v1013_20261014", "1013", "2026-10-14", deleteAfter: FaceMatchTestDatabase.Now.AddDays(5));

        Assert.Equal(0, await _h.Maintenance.CleanupVisitGroupsAsync(default));

        // 布里斯班 2026-10-15 00:00 = 2026-10-14T14:00Z。
        _h.Time.Now = new DateTime(2026, 10, 14, 13, 59, 59, DateTimeKind.Utc);
        Assert.Equal(0, await _h.Maintenance.CleanupVisitGroupsAsync(default));
        _h.Time.Now = new DateTime(2026, 10, 14, 14, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, await _h.Maintenance.CleanupVisitGroupsAsync(default));
    }

    [Fact]
    public async Task 访客库_悉尼夏令时按UTC加11计算本地午夜()
    {
        SeedGroup("v2001_20261014", "2001", "2026-10-14", deleteAfter: FaceMatchTestDatabase.Now.AddDays(5));

        // 悉尼 2026-10-15 00:00（夏令时 UTC+11）= 2026-10-14T13:00Z；若误按 UTC+10 会在 14:00Z 才删。
        _h.Time.Now = new DateTime(2026, 10, 14, 12, 59, 0, DateTimeKind.Utc);
        Assert.Equal(0, await _h.Maintenance.CleanupVisitGroupsAsync(default));
        _h.Time.Now = new DateTime(2026, 10, 14, 13, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, await _h.Maintenance.CleanupVisitGroupsAsync(default));
    }

    [Fact]
    public async Task 访客库_接收端写的DeleteAfterUtc先到也会删()
    {
        SeedGroup("v1013_20261014", "1013", "2026-10-14", deleteAfter: FaceMatchTestDatabase.Now.AddMinutes(-1));

        Assert.Equal(1, await _h.Maintenance.CleanupVisitGroupsAsync(default));
    }

    [Fact]
    public async Task 访客库_腾讯云删除失败留到下一轮_已删除的行与不可用时都不调用腾讯云()
    {
        SeedGroup("v1013_20261012", "1013", "2026-10-12", deleteAfter: FaceMatchTestDatabase.Now.AddDays(-1));
        SeedGroup("v1013_20261011", "1013", "2026-10-11", deleteAfter: FaceMatchTestDatabase.Now.AddDays(-2), deletedAt: FaceMatchTestDatabase.Now.AddDays(-1));
        _h.Library.FailOn = op => op == "DeleteGroup" ? FakeFaceLibrary.Failure(FaceLibraryFailureKind.Unavailable) : null;

        Assert.Equal(0, await _h.Maintenance.CleanupVisitGroupsAsync(default));
        Assert.Null((await _h.Db.Queryable<FaceVisitGroup>().SingleAsync(g => g.GroupId == "v1013_20261012")).DeletedAtUtc);
        Assert.Equal(["DeleteGroup:v1013_20261012"], _h.Library.Calls);

        _h.Library.FailOn = null;
        Assert.Equal(1, await _h.Maintenance.CleanupVisitGroupsAsync(default));

        _h.Library.Calls.Clear();
        _h.Library.Available = false;
        SeedGroup("v1013_20261010", "1013", "2026-10-10", deleteAfter: FaceMatchTestDatabase.Now.AddDays(-3));
        Assert.Equal(0, await _h.Maintenance.CleanupVisitGroupsAsync(default));
        Assert.Empty(_h.Library.Calls);
    }

    [Fact]
    public async Task 访客库_扫描腾讯云里遗漏的过期库_只动访客库命名且归属我们门店的库()
    {
        _h.Library.Groups["staff"] = [];
        _h.Library.Groups["unwelcome"] = [];
        _h.Library.Groups["v1013_20261011"] = [];       // 库表漏记、早于两天：删
        _h.Library.Groups["v1013_20261013"] = [];       // 昨天：保留
        _h.Library.Groups["v1013_20261014"] = [];       // 今天：保留
        _h.Library.Groups["vendor_20200101"] = [];      // 命名像但不是我们的门店：不动
        _h.Library.Groups["v9876_20200101"] = [];       // 门店不存在：不动
        _h.Library.Groups["other-system"] = [];
        SeedGroup("v1013_20261010", "1013", "2026-10-10", deleteAfter: FaceMatchTestDatabase.Now.AddDays(-3));
        _h.Library.Groups["v1013_20261010"] = [];       // 库表有登记：删，并把登记标记为已删除

        var deleted = await _h.Maintenance.SweepOrphanVisitGroupsAsync(default);

        Assert.Equal(2, deleted);
        Assert.DoesNotContain("v1013_20261011", _h.Library.Groups.Keys);
        Assert.DoesNotContain("v1013_20261010", _h.Library.Groups.Keys);
        Assert.Equal(["other-system", "staff", "unwelcome", "v1013_20261013", "v1013_20261014", "v9876_20200101", "vendor_20200101"],
            _h.Library.Groups.Keys.Order().ToArray());
        Assert.NotNull((await _h.Db.Queryable<FaceVisitGroup>().SingleAsync(g => g.GroupId == "v1013_20261010")).DeletedAtUtc);
    }

    // ---------- 员工库完整性 ----------

    [Fact]
    public async Task 员工库完整性_人数一致不告警_不一致写审计与警告()
    {
        SeedActiveEnrollment("st_a");
        SeedActiveEnrollment("st_b");
        _h.Library.Groups["staff"] = ["st_a", "st_b"];

        Assert.True(await _h.Maintenance.CheckStaffLibraryIntegrityAsync(default));
        Assert.Empty(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());

        _h.Library.Groups["staff"].Add("st_rogue");   // 员工库被旁路写入了一个人
        Assert.False(await _h.Maintenance.CheckStaffLibraryIntegrityAsync(default));

        var audit = Assert.Single(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());
        Assert.Equal(FaceMatchAuditActions.EnrollmentIntegrity, audit.Action);
        Assert.Equal("mismatch", audit.Result);
        Assert.Equal("library=3;db=2", audit.TargetRef);
        Assert.Contains(_h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("不一致"));
    }

    [Fact]
    public async Task 员工库完整性_库不存在且无登记算一致_腾讯云失败或不可用返回null不写审计()
    {
        Assert.True(await _h.Maintenance.CheckStaffLibraryIntegrityAsync(default));

        _h.Library.FailOn = op => op == "GetPersonCount" ? FakeFaceLibrary.Failure(FaceLibraryFailureKind.Unavailable) : null;
        Assert.Null(await _h.Maintenance.CheckStaffLibraryIntegrityAsync(default));
        _h.Library.FailOn = null;
        _h.Library.Available = false;
        Assert.Null(await _h.Maintenance.CheckStaffLibraryIntegrityAsync(default));

        Assert.Empty(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());
    }

    // ---------- Capture 记录保留清理 ----------

    [Fact]
    public async Task 保留清理_没有任何门店启用时空转_有门店启用后按天数删除事件与图片()
    {
        SeedCapture("old-1", daysAgo: 8);
        SeedCapture("old-2", daysAgo: 30);
        SeedCapture("new-1", daysAgo: 6);

        Assert.Equal(0, await _h.Maintenance.CleanupCaptureRecordsAsync(default));
        Assert.Equal(3, await _h.Db.Queryable<FaceCaptureEvent>().CountAsync());

        SeedProfile("1013", FaceStoreMode.Capture);
        Assert.Equal(2, await _h.Maintenance.CleanupCaptureRecordsAsync(default));

        Assert.Equal(["new-1"], (await _h.Db.Queryable<FaceCaptureEvent>().ToListAsync()).Select(e => e.CaptureId));
        Assert.Equal(["img-new-1"], (await _h.Db.Ado.SqlQueryAsync<string>("SELECT ImageId FROM FaceCaptureImage")));
        var audit = Assert.Single(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());
        Assert.Equal(FaceMatchAuditActions.CaptureRetention, audit.Action);
        Assert.Equal("deleted=2", audit.Result);
    }

    [Fact]
    public async Task 保留清理_天数可配置_配置非法回到7天_关闭模式的档案不算启用()
    {
        SeedProfile("1013", FaceStoreMode.Off);
        SeedCapture("old", daysAgo: 4);
        Assert.Equal(0, await _h.Maintenance.CleanupCaptureRecordsAsync(default));

        _h.Db.Updateable<FaceStoreProfile>().SetColumns(p => p.Mode == FaceStoreMode.Match).Where(p => p.StoreCode == "1013").ExecuteCommand();
        _h.Settings.Options.CaptureRetentionDays = 0;
        Assert.Equal(0, await _h.Maintenance.CleanupCaptureRecordsAsync(default));     // 4 天 < 默认 7 天

        _h.Settings.Options.CaptureRetentionDays = 3;
        Assert.Equal(1, await _h.Maintenance.CleanupCaptureRecordsAsync(default));
    }

    [Fact]
    public async Task 保留清理_大量过期行分批删完()
    {
        SeedProfile("1013", FaceStoreMode.Capture);
        _h.Db.Insertable(Enumerable.Range(0, 1200).Select(i => new FaceCaptureEvent
        {
            CaptureId = $"{i:D32}",
            CameraId = "cam-1",
            ReceivedAtUtc = FaceMatchTestDatabase.Now.AddDays(-20).AddSeconds(i),
        }).ToList()).ExecuteCommand();
        SeedCapture("new-1", daysAgo: 1);

        Assert.Equal(1200, await _h.Maintenance.CleanupCaptureRecordsAsync(default));

        Assert.Equal(1, await _h.Db.Queryable<FaceCaptureEvent>().CountAsync());
    }

    // ---------- 名单条目 ----------

    [Fact]
    public async Task 名单到期_自动停用_DeletePerson_删参考照片_写审计()
    {
        var active = SeedEntry(FaceUnwelcomeEntryStatus.Active, expiresInDays: -1, photos: 2);
        var pending = SeedEntry(FaceUnwelcomeEntryStatus.Pending, expiresInDays: -2, photos: 1);
        var stillValid = SeedEntry(FaceUnwelcomeEntryStatus.Active, expiresInDays: 40, photos: 1);
        _h.Library.PersonGroup[$"uw_{active}"] = "unwelcome";
        _h.Library.Groups["unwelcome"] = [$"uw_{active}", $"uw_{stillValid}"];

        var processed = await _h.Maintenance.ProcessUnwelcomeEntriesAsync(default);

        Assert.Equal(4, processed);   // 2 条停用 + 2 条清理完成
        Assert.Equal(FaceUnwelcomeEntryStatus.Expired, (await LoadEntry(active)).Status);
        Assert.Equal(FaceUnwelcomeEntryStatus.Expired, (await LoadEntry(pending)).Status);
        Assert.Equal(FaceUnwelcomeEntryStatus.Active, (await LoadEntry(stillValid)).Status);
        Assert.Equal("[]", (await LoadEntry(active)).PhotoObjectKeysJson);
        Assert.Contains($"DeletePerson:uw_{active}", _h.Library.Calls);
        Assert.DoesNotContain($"DeletePerson:uw_{stillValid}", _h.Library.Calls);
        Assert.DoesNotContain(_h.Store.Objects.Keys, k => k.Contains(active) || k.Contains(pending));
        Assert.Contains(stillValid, _h.Store.Objects.Keys.Select(k => k.Split('/')[2]));
        Assert.Equal(2, (await _h.Db.Queryable<FaceAuditLog>().Where(a => a.Action == FaceMatchAuditActions.EntryExpire).ToListAsync()).Count);
    }

    [Fact]
    public async Task 名单到期时腾讯云不可用_状态已是Expired_清理待完成_恢复后重试清理干净()
    {
        var entry = SeedEntry(FaceUnwelcomeEntryStatus.Active, expiresInDays: -1, photos: 2);
        _h.Library.Available = false;

        await _h.Maintenance.ProcessUnwelcomeEntriesAsync(default);
        var after = await LoadEntry(entry);
        Assert.Equal(FaceUnwelcomeEntryStatus.Expired, after.Status);
        Assert.Equal(2, FaceMatchRules.ParseKeys(after.PhotoObjectKeysJson).Count);

        _h.Library.Available = true;
        Assert.Equal(1, await _h.Maintenance.ProcessUnwelcomeEntriesAsync(default));
        Assert.Equal("[]", (await LoadEntry(entry)).PhotoObjectKeysJson);
        Assert.Empty(_h.Store.Objects);
        // 再跑一次什么都不做。
        Assert.Equal(0, await _h.Maintenance.ProcessUnwelcomeEntriesAsync(default));
    }

    [Fact]
    public async Task 已撤回但没清干净的条目_后台重试清理()
    {
        var entry = SeedEntry(FaceUnwelcomeEntryStatus.Withdrawn, expiresInDays: 30, photos: 2);

        Assert.Equal(1, await _h.Maintenance.ProcessUnwelcomeEntriesAsync(default));

        Assert.Equal("[]", (await LoadEntry(entry)).PhotoObjectKeysJson);
        Assert.Empty(_h.Store.Objects);
    }

    [Fact]
    public async Task 到期前30天提醒_发给提交人与批准人_幂等键固定_文案不含姓名与事件细节()
    {
        var due = SeedEntry(FaceUnwelcomeEntryStatus.Active, expiresInDays: 25, photos: 1, reviewDueInDays: -5);
        SeedEntry(FaceUnwelcomeEntryStatus.Active, expiresInDays: 90, photos: 1, reviewDueInDays: 60);
        SeedEntry(FaceUnwelcomeEntryStatus.Pending, expiresInDays: 25, photos: 1, reviewDueInDays: -5);
        SeedEntry(FaceUnwelcomeEntryStatus.Active, expiresInDays: -1, photos: 1, reviewDueInDays: -31);

        var sent = await _h.Maintenance.PublishExpiryRemindersAsync(default);

        Assert.Equal(1, sent);
        var request = Assert.Single(_h.Publisher.Requests);
        Assert.Equal(AppNotificationTypes.FaceUnwelcomeExpiring, request.Type);
        Assert.Equal($"unwelcome-expiry:{due}", request.DedupeKey);
        Assert.Equal(["app-1", "sub-1"], request.Audience.UserGuids.Order().ToArray());
        Assert.Null(request.Audience.StoreCode);
        Assert.Contains("25 天", request.Body);
        Assert.Equal(due, request.Data!["entryId"]);
        var text = request.Title + request.Body;
        Assert.DoesNotContain("Sub Mitter", text);
        Assert.DoesNotContain("EVID", text);

        // 再跑一轮：同一幂等键被通知平台去重，不算新发。
        Assert.Equal(0, await _h.Maintenance.PublishExpiryRemindersAsync(default));
    }

    [Fact]
    public async Task 到期提醒发布失败_只记日志_不抛出_也不影响其他条目()
    {
        SeedEntry(FaceUnwelcomeEntryStatus.Active, expiresInDays: 25, photos: 1, reviewDueInDays: -5);
        _h.Publisher.Failure = new InvalidOperationException("hub down");

        Assert.Equal(0, await _h.Maintenance.PublishExpiryRemindersAsync(default));

        Assert.Contains(_h.Logs.Entries, e => e.Level == LogLevel.Warning);
    }

    // ---------- 命中图到期删除 ----------

    [Fact]
    public async Task 命中图到期删除_置空对象键_写审计_未到期的不动()
    {
        var expired = SeedTask("face-match/hit/1013/a.jpg", deleteInDays: -1);
        var driven = SeedTask("face-match/hit/1013/b.jpg", deleteInDays: 0);   // 驳回后提前到期（等于现在）
        var future = SeedTask("face-match/hit/1013/c.jpg", deleteInDays: 3);
        var cleared = SeedTask("", deleteInDays: -9);

        Assert.Equal(2, await _h.Maintenance.DeleteExpiredTaskImagesAsync(default));

        Assert.Equal("", (await LoadTask(expired)).ImageObjectKey);
        Assert.Equal("", (await LoadTask(driven)).ImageObjectKey);
        Assert.Equal("face-match/hit/1013/c.jpg", (await LoadTask(future)).ImageObjectKey);
        Assert.Equal(["face-match/hit/1013/a.jpg", "face-match/hit/1013/b.jpg"], _h.Store.Deletes.Order().ToArray());
        Assert.Equal(2, (await _h.Db.Queryable<FaceAuditLog>().Where(a => a.Action == FaceMatchAuditActions.TaskImageDelete).ToListAsync()).Count);
        _ = cleared;
    }

    [Fact]
    public async Task 命中图到期删除_绝不借命中任务删到登记照同意书参考照片_形态非法的键跳过()
    {
        var enrollmentKey = $"face-match/enrollment/{new string('a', 32)}/0.jpg";
        var consentKey = $"face-match/consent/{new string('b', 32)}/scan.jpg";
        var unwelcomeKey = $"face-match/unwelcome/{new string('c', 32)}/0.jpg";
        foreach (var key in new[] { enrollmentKey, consentKey, unwelcomeKey })
        {
            _h.Store.Objects[key] = FaceImageTestData.Jpeg;
            SeedTask(key, deleteInDays: -1);
        }

        var traversal = SeedTask("face-match/hit/../enrollment/x.jpg", deleteInDays: -1);

        Assert.Equal(0, await _h.Maintenance.DeleteExpiredTaskImagesAsync(default));

        Assert.Empty(_h.Store.Deletes);
        Assert.Equal(3, _h.Store.Objects.Count);
        Assert.Equal("face-match/hit/../enrollment/x.jpg", (await LoadTask(traversal)).ImageObjectKey);
    }

    [Fact]
    public async Task 命中图到期删除_私有桶未配置时空转_删除失败保留键下轮重试()
    {
        var task = SeedTask("face-match/hit/1013/a.jpg", deleteInDays: -1);

        _h.Store.Configured = false;
        Assert.Equal(0, await _h.Maintenance.DeleteExpiredTaskImagesAsync(default));
        Assert.Empty(_h.Store.Deletes);

        _h.Store.Configured = true;
        _h.Store.FailOn = (op, _) => op == "delete" ? new FaceMatchStoreException(FaceMatchStoreFailureKind.Denied, "x") : null;
        Assert.Equal(0, await _h.Maintenance.DeleteExpiredTaskImagesAsync(default));
        Assert.Equal("face-match/hit/1013/a.jpg", (await LoadTask(task)).ImageObjectKey);

        _h.Store.FailOn = null;
        Assert.Equal(1, await _h.Maintenance.DeleteExpiredTaskImagesAsync(default));
    }

    // ---------- 撤回补偿 ----------

    [Fact]
    public async Task 同意已撤回而腾讯云删除未完成的登记_恢复后由后台任务补删()
    {
        var consentId = SeedConsent("rev-a", withdrawn: true);
        var keys = new[] { "face-match/enrollment/" + new string('d', 32) + "/0.jpg" };
        _h.Store.Objects[keys[0]] = FaceImageTestData.Jpeg;
        var enrollment = SeedActiveEnrollment("st_pending", consentId, keys);
        _h.Library.Groups["staff"] = ["st_pending"];
        _h.Library.PersonGroup["st_pending"] = "staff";
        _h.Library.Available = false;

        Assert.Equal(0, await _h.Maintenance.RetryEnrollmentDeletionsAsync(default));

        _h.Library.Available = true;
        Assert.Equal(1, await _h.Maintenance.RetryEnrollmentDeletionsAsync(default));
        var row = await _h.Db.Queryable<FaceEnrollment>().SingleAsync(e => e.EnrollmentId == enrollment);
        Assert.Equal(FaceEnrollmentStatus.Withdrawn, row.Status);
        Assert.Equal("[]", row.PhotoObjectKeysJson);
        Assert.Empty(_h.Library.PersonGroup);
        Assert.Empty(_h.Store.Objects);
        Assert.Equal(0, await _h.Maintenance.RetryEnrollmentDeletionsAsync(default));
    }

    [Fact]
    public async Task 已撤回登记留在私有桶里的登记照_后台任务补删()
    {
        var key = "face-match/enrollment/" + new string('e', 32) + "/0.jpg";
        _h.Store.Objects[key] = FaceImageTestData.Jpeg;
        var enrollment = SeedActiveEnrollment("st_x", SeedConsent("rev-a", withdrawn: false), [key], FaceEnrollmentStatus.Withdrawn);

        Assert.Equal(1, await _h.Maintenance.RetryEnrollmentDeletionsAsync(default));

        Assert.Equal("[]", (await _h.Db.Queryable<FaceEnrollment>().SingleAsync(e => e.EnrollmentId == enrollment)).PhotoObjectKeysJson);
        Assert.Empty(_h.Store.Objects);
    }

    // ---------- 地域改动作废审查 ----------

    [Fact]
    public async Task 地域改动后_只清空审查记录并写审计_不再强制关闭任何档案()
    {
        // 2026-10-13 用户决定 Match 闸门改为警告：作废审查不再把依赖它的档案关成 Off，模式与各开关原样保留。
        SeedProfile("1013", FaceStoreMode.Match, region: "ap-shanghai", staff: true, dedup: true, unwelcome: true);
        SeedProfile("2001", FaceStoreMode.Capture, region: "ap-shanghai", evaluate: true);
        // Capture 下开了名单（只处理内部知情人员，不依赖跨境审查）：同样只作废审查，名单开关保留。
        SeedProfile("2002", FaceStoreMode.Capture, region: "ap-shanghai", unwelcome: true);
        SeedProfile("9001", FaceStoreMode.Match, region: "ap-guangzhou", dedup: true);

        var count = await _h.Maintenance.InvalidateStaleCrossBorderReviewsAsync(default);

        Assert.Equal(3, count);
        var match = await _h.Db.Queryable<FaceStoreProfile>().SingleAsync(p => p.StoreCode == "1013");
        Assert.Equal(FaceStoreMode.Match, match.Mode);
        Assert.True(match.StaffExclusionEnabled && match.TrafficDedupEnabled && match.UnwelcomeListEnabled);
        Assert.Null(match.CrossBorderRegion);
        Assert.Null(match.CrossBorderReviewRef);
        Assert.Null(match.CrossBorderReviewedBy);
        Assert.Null(match.CrossBorderReviewedAt);
        Assert.Equal("system", match.UpdatedBy);
        var capture = await _h.Db.Queryable<FaceStoreProfile>().SingleAsync(p => p.StoreCode == "2001");
        Assert.Equal(FaceStoreMode.Capture, capture.Mode);
        Assert.True(capture.EvaluateMatch);
        Assert.Null(capture.CrossBorderRegion);
        var captureList = await _h.Db.Queryable<FaceStoreProfile>().SingleAsync(p => p.StoreCode == "2002");
        Assert.Equal(FaceStoreMode.Capture, captureList.Mode);
        Assert.True(captureList.UnwelcomeListEnabled);
        Assert.Null(captureList.CrossBorderRegion);
        // 地域一致的不动（连审查记录都保留）。
        var untouched = await _h.Db.Queryable<FaceStoreProfile>().SingleAsync(p => p.StoreCode == "9001");
        Assert.Equal(FaceStoreMode.Match, untouched.Mode);
        Assert.True(untouched.TrafficDedupEnabled);
        Assert.Equal("ap-guangzhou", untouched.CrossBorderRegion);
        var audits = await _h.Db.Queryable<FaceAuditLog>().Where(a => a.Action == FaceMatchAuditActions.ProfileInvalidated).ToListAsync();
        Assert.Equal(3, audits.Count);
        // 审计结论一律是 review-cleared，不会再出现 disabled。
        Assert.All(audits, a => Assert.Equal("review-cleared", a.Result));
        Assert.Equal(["1013", "2001", "2002"], audits.Select(a => a.StoreCode ?? string.Empty).Order().ToArray());
    }

    [Fact]
    public async Task 地域改动作废审查后_档案保存的闸门报告把审查缺口显示为警告_不阻塞()
    {
        SeedProfile("1013", FaceStoreMode.Match, region: "ap-shanghai", dedup: true);
        await _h.Maintenance.InvalidateStaleCrossBorderReviewsAsync(default);

        var profile = await _h.Profiles.GetAsync("1013");

        Assert.Equal(FaceStoreMode.Match, profile.Mode);
        Assert.True(profile.Gates.CanSave);
        Assert.True(profile.Gates.HasWarnings);
        Assert.Contains("crossBorderRegion", profile.Gates.Facets.Single(f => f.Facet == "match").Warnings);
        Assert.Empty(profile.Gates.Facets.Single(f => f.Facet == "match").Missing);
    }

    [Fact]
    public async Task 配置地域无效或功能未开启时_不触碰任何门店档案()
    {
        SeedProfile("1013", FaceStoreMode.Match, region: "ap-shanghai");

        _h.Settings.Options.Tencent.Region = "ap-beijing";
        Assert.Equal(0, await _h.Maintenance.InvalidateStaleCrossBorderReviewsAsync(default));
        _h.Settings.Options.Tencent.Region = null;
        Assert.Equal(0, await _h.Maintenance.InvalidateStaleCrossBorderReviewsAsync(default));
        _h.Settings.Options.Tencent.Region = "ap-guangzhou";
        _h.Settings.Options.Tencent.AdminSecretKey = null;
        Assert.Equal(0, await _h.Maintenance.InvalidateStaleCrossBorderReviewsAsync(default));

        Assert.Equal(FaceStoreMode.Match, (await _h.Db.Queryable<FaceStoreProfile>().SingleAsync()).Mode);
    }

    // ---------- 命中提示发布 ----------

    [Fact]
    public async Task 命中提示_文案固定_发给该店持有复核权限的人_幂等键_在事务之外_提交后标记已提示()
    {
        var task = SeedTask("face-match/hit/1013/a.jpg", deleteInDays: 7, score: 87.5m);
        _h.Publisher.TransactionProbe = () => _h.Db.Ado.IsAnyTran();

        var handled = await _h.Notifier.PublishPendingAsync(default);

        Assert.Equal(1, handled);
        var request = Assert.Single(_h.Publisher.Requests);
        Assert.Equal(AppNotificationTypes.FaceUnwelcomeHit, request.Type);
        Assert.Equal("疑似匹配（相似度 88%），不是同一个人的概率，必须由两人人工确认", request.Body);
        Assert.Equal($"unwelcome-hit:{task}", request.DedupeKey);
        Assert.Equal("1013", request.Audience.StoreCode);
        Assert.Equal(Permissions.FaceMatch.Unwelcome.Review, request.Audience.RequiredPermission);
        Assert.Equal(AppNotificationPriorities.High, request.Priority);
        Assert.Equal("1013", request.StoreCode);
        Assert.Equal(new Dictionary<string, string> { ["taskId"] = task, ["storeCode"] = "1013" }, request.Data);
        Assert.Equal(FaceMatchTestDatabase.Now.AddDays(7), request.ExpiresAtUtc);
        Assert.DoesNotContain(false, new[] { !_h.Publisher.InTransaction.Single() });   // 发布时不在任何数据库事务里
        Assert.False(Assert.Single(_h.Publisher.InTransaction));
        Assert.DoesNotMatch("姓名|事件|证据|EVID", request.Title + request.Body);
        Assert.Equal(FaceMatchTestDatabase.Now, (await LoadTask(task)).NotifiedAtUtc);

        // 已提示的任务不会重复发布。
        Assert.Equal(0, await _h.Notifier.PublishPendingAsync(default));
        Assert.Single(_h.Publisher.Requests);
    }

    [Fact]
    public async Task 命中提示_发布失败只记日志_不标记已提示_下一轮重试_不影响其他任务()
    {
        var first = SeedTask("face-match/hit/1013/a.jpg", deleteInDays: 7, capturedMinutesAgo: 20);
        var second = SeedTask("face-match/hit/1013/b.jpg", deleteInDays: 7, capturedMinutesAgo: 10);
        _h.Publisher.Failure = new InvalidOperationException("db blip");

        Assert.Equal(0, await _h.Notifier.PublishPendingAsync(default));

        Assert.Equal(2, _h.Publisher.Requests.Count);   // 两条都尝试了，没有因为第一条失败而中断
        Assert.Null((await LoadTask(first)).NotifiedAtUtc);
        Assert.Null((await LoadTask(second)).NotifiedAtUtc);
        var warnings = _h.Logs.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.DoesNotContain("相似度", _h.Logs.Dump());

        _h.Publisher.Failure = null;
        Assert.Equal(2, await _h.Notifier.PublishPendingAsync(default));
        Assert.NotNull((await LoadTask(first)).NotifiedAtUtc);
        Assert.NotNull((await LoadTask(second)).NotifiedAtUtc);
    }

    [Fact]
    public async Task 命中提示_暂无接收人则图片到期前继续重试_到期后不再发直接标记()
    {
        var task = SeedTask("face-match/hit/1013/a.jpg", deleteInDays: 2);
        _h.Publisher.NoRecipients = true;

        Assert.Equal(0, await _h.Notifier.PublishPendingAsync(default));
        Assert.Null((await LoadTask(task)).NotifiedAtUtc);

        _h.Time.Now = _h.Time.Now.AddDays(3);
        var before = _h.Publisher.Requests.Count;
        Assert.Equal(1, await _h.Notifier.PublishPendingAsync(default));
        Assert.Equal(before, _h.Publisher.Requests.Count);
        Assert.NotNull((await LoadTask(task)).NotifiedAtUtc);
    }

    [Fact]
    public async Task 命中提示_已复核结束的任务直接标记已处理_不再发()
    {
        var task = SeedTask("face-match/hit/1013/a.jpg", deleteInDays: 7, status: FaceMatchReviewStatus.Dismissed);

        Assert.Equal(1, await _h.Notifier.PublishPendingAsync(default));

        Assert.Empty(_h.Publisher.Requests);
        Assert.NotNull((await LoadTask(task)).NotifiedAtUtc);
    }

    [Fact]
    public async Task 命中提示_配合真实通知发布器_写入通知并按幂等键去重_收件人只含该店有复核权限的人()
    {
        var roleService = new Mock<BlazorApp.Api.Interfaces.IRoleService>(MockBehavior.Strict);
        roleService.Setup(r => r.UserHasPermissionAsync("rev-a", Permissions.FaceMatch.Unwelcome.Review)).ReturnsAsync(ApiResponse<bool>.OK(true));
        roleService.Setup(r => r.UserHasPermissionAsync("sub-1", Permissions.FaceMatch.Unwelcome.Review)).ReturnsAsync(ApiResponse<bool>.OK(false));
        roleService.Setup(r => r.UserHasPermissionAsync("app-1", Permissions.FaceMatch.Unwelcome.Review)).ReturnsAsync(ApiResponse<bool>.OK(false));
        var ctx = _h.Database.Context;
        var realPublisher = new NotificationPublisher(
            ctx,
            new NotificationRecipientResolver(ctx, roleService.Object, NullLogger<NotificationRecipientResolver>.Instance),
            new NoopDispatcher(),
            _h.Time,
            NullLogger<NotificationPublisher>.Instance);
        var notifier = new FaceHitNotifier(ctx, realPublisher, _h.Time, NullLogger<FaceHitNotifier>.Instance);
        var task = SeedTask("face-match/hit/1013/a.jpg", deleteInDays: 7, score: 91m);

        Assert.Equal(1, await notifier.PublishPendingAsync(default));

        var notification = await _h.Db.Queryable<AppNotification>().SingleAsync();
        Assert.Equal($"unwelcome-hit:{task}", notification.DedupeKey);
        Assert.Equal("1013", notification.StoreCode);
        Assert.Equal("疑似匹配（相似度 91%），不是同一个人的概率，必须由两人人工确认", notification.Body);
        var recipients = await _h.Db.Queryable<AppNotificationRecipient>().ToListAsync();
        Assert.Equal(["rev-a"], recipients.Select(r => r.UserGUID));

        // 即使有人把 NotifiedAtUtc 清掉重发，也会被同一幂等键去重。
        _h.Db.Updateable<FaceMatchTask>().SetColumns(t => t.NotifiedAtUtc == null).Where(t => t.TaskId == task).ExecuteCommand();
        Assert.Equal(1, await notifier.PublishPendingAsync(default));
        Assert.Single(await _h.Db.Queryable<AppNotification>().ToListAsync());
    }

    // ---------- 调度 ----------

    [Fact]
    public async Task 调度_什么都没配置时每个节拍都空转_不调用腾讯云也不删除任何东西()
    {
        _h.Settings.Options.Tencent = new FaceMatchTencentOptions();
        SeedCapture("old", daysAgo: 30);
        var service = CreateScheduler(out var time);

        for (var i = 0; i < 400; i++)
        {
            await service.TickAsync(default);
            time.Now = time.Now.AddMinutes(5);
        }

        Assert.Empty(_h.Library.Calls);
        Assert.Equal(1, await _h.Db.Queryable<FaceCaptureEvent>().CountAsync());   // 没有门店启用：保留清理不运行
        Assert.Empty(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());
        Assert.Empty(_h.Publisher.Requests);
    }

    [Fact]
    public async Task 调度_命中提示每个节拍都跑_其余任务按各自间隔到点才跑()
    {
        var service = CreateScheduler(out var time);
        SeedProfile("1013", FaceStoreMode.Capture);
        SeedGroup("v1013_20261012", "1013", "2026-10-12", deleteAfter: FaceMatchTestDatabase.Now.AddDays(-1));

        await service.TickAsync(default);                       // 第一次出现：安排在「现在 + 间隔」之后
        Assert.Empty(_h.Library.Calls);

        time.Now = time.Now.AddMinutes(5);
        await service.TickAsync(default);                       // 访客库清理到点
        Assert.Contains("DeleteGroup:v1013_20261012", _h.Library.Calls);

        var task = SeedTask("face-match/hit/1013/a.jpg", deleteInDays: 7);
        time.Now = time.Now.AddSeconds(30);
        await service.TickAsync(default);                       // 命中提示每个节拍都跑
        Assert.NotNull((await LoadTask(task)).NotifiedAtUtc);
    }

    [Fact]
    public async Task 调度_总开关关闭时节拍什么都不做_单个任务异常不影响其他任务()
    {
        var service = CreateScheduler(out var time);
        var task = SeedTask("face-match/hit/1013/a.jpg", deleteInDays: 7);
        _h.Settings.Options.BackgroundEnabled = false;
        await service.TickAsync(default);
        Assert.Null((await LoadTask(task)).NotifiedAtUtc);

        _h.Settings.Options.BackgroundEnabled = true;
        _h.Db.Ado.ExecuteCommand("DROP TABLE FaceVisitGroup");   // 访客库任务会因表不存在而抛异常
        await service.TickAsync(default);
        time.Now = time.Now.AddMinutes(6);
        await service.TickAsync(default);

        Assert.NotNull((await LoadTask(task)).NotifiedAtUtc);
        Assert.Contains(_h.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("visit-groups"));
    }

    // ---------- 辅助 ----------

    private FaceMatchBackgroundService CreateScheduler(out FaceCameraAdminTests.MutableTimeProvider time)
    {
        var clock = new FaceCameraAdminTests.MutableTimeProvider(FaceMatchTestDatabase.Now);
        time = clock;
        // 调度里每个任务用独立作用域：这里的作用域工厂总是返回同一批共享替身与同一个库。
        var services = new ServiceCollection();
        services.AddSingleton(_h.Database.Context);
        services.AddSingleton<IFaceLibrary>(_h.Library);
        services.AddSingleton<IFaceMatchObjectStore>(_h.Store);
        services.AddSingleton<IFaceMatchSettingsProvider>(_h.Settings);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<INotificationPublisher>(_h.Publisher);
        services.AddSingleton<IFaceMatchAudit>(sp => new FaceMatchAudit(_h.Database.Context, clock, _h.Logs.CreateLogger<FaceMatchAudit>()));
        services.AddSingleton(sp => new FaceStaffService(
            _h.Database.Context, _h.Library, _h.Store, _h.Settings, sp.GetRequiredService<IFaceMatchAudit>(), clock, _h.Logs.CreateLogger<FaceStaffService>()));
        services.AddSingleton(sp => new FaceUnwelcomeService(
            _h.Database.Context, _h.Library, _h.Store, _h.Settings, sp.GetRequiredService<IFaceMatchAudit>(), clock, _h.Logs.CreateLogger<FaceUnwelcomeService>()));
        services.AddSingleton(sp => new FaceMatchMaintenance(
            _h.Database.Context, _h.Library, _h.Store, _h.Settings, sp.GetRequiredService<FaceStaffService>(),
            sp.GetRequiredService<FaceUnwelcomeService>(), _h.Publisher, sp.GetRequiredService<IFaceMatchAudit>(), clock,
            _h.Logs.CreateLogger<FaceMatchMaintenance>()));
        services.AddSingleton(sp => new FaceHitNotifier(_h.Database.Context, _h.Publisher, clock, _h.Logs.CreateLogger<FaceHitNotifier>()));
        var provider = services.BuildServiceProvider();
        return new FaceMatchBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(), _h.Settings, clock, _h.Logs.CreateLogger<FaceMatchBackgroundService>());
    }

    private void SeedProfile(
        string storeCode, byte mode, string? region = null, bool dedup = false, bool unwelcome = false, bool evaluate = false, bool staff = false) =>
        _h.Db.Insertable(new FaceStoreProfile
        {
            StoreCode = storeCode,
            Mode = mode,
            EvaluateMatch = evaluate,
            StaffExclusionEnabled = staff,
            TrafficDedupEnabled = dedup,
            UnwelcomeListEnabled = unwelcome,
            CrossBorderRegion = region,
            CrossBorderReviewRef = region == null ? null : "XB-1",
            CrossBorderReviewedBy = region == null ? null : "Counsel",
            CrossBorderReviewedAt = region == null ? null : FaceMatchTestDatabase.Now.AddDays(-2),
            UpdatedAtUtc = FaceMatchTestDatabase.Now.AddDays(-2),
        }).ExecuteCommand();

    private void SeedGroup(string groupId, string storeCode, string businessDate, DateTime deleteAfter, DateTime? deletedAt = null) =>
        _h.Db.Insertable(new FaceVisitGroup
        {
            GroupId = groupId,
            StoreCode = storeCode,
            BusinessDate = DateTime.Parse(businessDate),
            CreatedAtUtc = FaceMatchTestDatabase.Now.AddDays(-1),
            DeleteAfterUtc = deleteAfter,
            DeletedAtUtc = deletedAt,
            VisitorCount = 3,
        }).ExecuteCommand();

    private string SeedActiveEnrollment(string personId, string? consentId = null, string[]? keys = null, byte status = FaceEnrollmentStatus.Active)
    {
        var id = FaceMatchRules.NewId();
        _h.Db.Insertable(new FaceEnrollment
        {
            EnrollmentId = id,
            UserGuid = $"user-{personId}",
            ConsentId = consentId ?? SeedConsent("rev-a", withdrawn: false),
            TencentPersonId = personId,
            PhotoObjectKeysJson = FaceMatchRules.SerializeKeys(keys ?? []),
            Status = status,
            EnrolledAtUtc = FaceMatchTestDatabase.Now.AddDays(-10),
            EnrolledBy = "seed",
        }).ExecuteCommand();
        return id;
    }

    private string SeedConsent(string userGuid, bool withdrawn)
    {
        var id = FaceMatchRules.NewId();
        _h.Db.Insertable(new FaceConsentRecord
        {
            ConsentId = id,
            UserGuid = userGuid,
            ConsentVersion = "v1",
            SignedAtUtc = FaceMatchTestDatabase.Now.AddDays(-20),
            Method = "paper",
            WithdrawnAtUtc = withdrawn ? FaceMatchTestDatabase.Now.AddDays(-1) : null,
            CreatedAtUtc = FaceMatchTestDatabase.Now.AddDays(-20),
            CreatedBy = "seed",
        }).ExecuteCommand();
        return id;
    }

    private void SeedCapture(string captureId, int daysAgo)
    {
        _h.Db.Insertable(new FaceCaptureEvent
        {
            CaptureId = captureId,
            CameraId = "cam-1",
            ReceivedAtUtc = FaceMatchTestDatabase.Now.AddDays(-daysAgo),
        }).ExecuteCommand();
        _h.Db.Ado.ExecuteCommand($"INSERT INTO FaceCaptureImage (ImageId, CaptureId) VALUES ('img-{captureId}', '{captureId}')");
    }

    private string SeedEntry(byte status, int expiresInDays, int photos, int reviewDueInDays = 0)
    {
        var id = FaceMatchRules.NewId();
        var keys = Enumerable.Range(0, photos).Select(i => FaceMatchObjectKeys.Unwelcome(id, i, "jpg")).ToList();
        foreach (var key in keys)
        {
            _h.Store.Objects[key] = FaceImageTestData.Jpeg;
        }

        _h.Db.Insertable(new FaceUnwelcomeEntry
        {
            EntryId = id,
            TencentPersonId = $"uw_{id}",
            IncidentCategory = FaceIncidentCategory.ViolenceOrThreat,
            EvidenceRef = "EVID-SEED",
            StoreScope = "1013",
            SubmittedBy = "sub-1",
            SubmittedAtUtc = FaceMatchTestDatabase.Now.AddDays(-50),
            ApprovedBy = status == FaceUnwelcomeEntryStatus.Pending ? null : "app-1",
            ApprovedAtUtc = status == FaceUnwelcomeEntryStatus.Pending ? null : FaceMatchTestDatabase.Now.AddDays(-49),
            ReviewDueAtUtc = FaceMatchTestDatabase.Now.AddDays(reviewDueInDays),
            ExpiresAtUtc = FaceMatchTestDatabase.Now.AddDays(expiresInDays),
            Status = status,
            PhotoObjectKeysJson = FaceMatchRules.SerializeKeys(keys),
        }).ExecuteCommand();
        return id;
    }

    private string SeedTask(
        string imageKey, int deleteInDays, decimal score = 90m, byte status = FaceMatchReviewStatus.Pending, int capturedMinutesAgo = 5)
    {
        var id = FaceMatchRules.NewId();
        _h.Db.Insertable(new FaceMatchTask
        {
            TaskId = id,
            StoreCode = "1013",
            CameraId = "cam-1",
            CapturedAtUtc = FaceMatchTestDatabase.Now.AddMinutes(-capturedMinutesAgo),
            EntryId = new string('1', 32),
            Score = score,
            ImageObjectKey = imageKey,
            ReviewStatus = status,
            ImageDeleteAtUtc = FaceMatchTestDatabase.Now.AddDays(deleteInDays),
        }).ExecuteCommand();
        if (imageKey.StartsWith("face-match/hit/") && !imageKey.Contains(".."))
        {
            _h.Store.Objects[imageKey] = FaceImageTestData.Jpeg;
        }

        return id;
    }

    private Task<FaceUnwelcomeEntry> LoadEntry(string id) => _h.Db.Queryable<FaceUnwelcomeEntry>().SingleAsync(e => e.EntryId == id);

    private Task<FaceMatchTask> LoadTask(string id) => _h.Db.Queryable<FaceMatchTask>().SingleAsync(t => t.TaskId == id);

    private sealed class NoopDispatcher : INotificationRealtimeDispatcher
    {
        public Task SendToUsersAsync(IReadOnlyCollection<string> userGuids, NotificationDto notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
