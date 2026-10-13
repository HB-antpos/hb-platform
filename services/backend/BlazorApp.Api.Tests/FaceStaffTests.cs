using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 员工同意与登记：先有有效同意、NSW 14 天通知、3–5 张登记照、腾讯云与私有桶的回滚、撤回时两处都删、审计与日志不含敏感信息。
/// 「今天」固定为 2026-10-14 02:00 UTC，用例不依赖机器时区。
/// </summary>
public sealed class FaceStaffTests : IDisposable
{
    private readonly FaceMatchTestHarness _h = new();
    private readonly FaceMatchActor _actor = FaceMatchTestHarness.Admin;

    public FaceStaffTests()
    {
        _h.Database.AddUser("u-q", "Quinn QLD", "s-1013");
        _h.Database.AddUser("u-n", "Nora NSW", "s-2001");
    }

    public void Dispose() => _h.Dispose();

    // ---------- 员工候选 ----------

    [Fact]
    public async Task 员工候选_只含该门店启用账号_带NSW标记与登记状态_支持关键字()
    {
        _h.Database.AddUser("u-q2", "Quentin", "s-1013");
        _h.Db.Updateable<User>().SetColumns(u => u.IsActive == false).Where(u => u.UserGUID == "u-q2").ExecuteCommand();
        _h.Database.AddUser("u-q3", "Zed Zulu", "s-1013");
        var consent = await NewConsentAsync("u-q");
        await _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor);

        var qld = await _h.Staff.ListStaffAsync("1013", null);
        var nsw = await _h.Staff.ListStaffAsync("2001", null);
        var filtered = await _h.Staff.ListStaffAsync("1013", "zulu");

        Assert.Equal(["Quinn QLD", "Zed Zulu"], qld.Select(s => s.DisplayName));
        Assert.Equal("active", qld.Single(s => s.UserGuid == "u-q").EnrollmentState);
        Assert.Equal("none", qld.Single(s => s.UserGuid == "u-q3").EnrollmentState);
        Assert.All(qld, s => Assert.False(s.RequiresNswNotice));
        Assert.True(Assert.Single(nsw).RequiresNswNotice);
        Assert.Equal("u-q3", Assert.Single(filtered).UserGuid);
    }

    [Fact]
    public async Task 员工候选_门店编码非法_400()
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Staff.ListStaffAsync("bad code", null));
        Assert.Equal(400, ex.StatusCode);
    }

    // ---------- 同意 ----------

    [Fact]
    public async Task 纸质同意_扫描件存私有桶独立前缀_库里只存键_审计只有编号()
    {
        var dto = await _h.Staff.CreateConsentAsync(ConsentRequest("u-q"), Scan(), _actor);

        Assert.True(dto.Active);
        Assert.True(dto.HasEvidence);
        Assert.Equal("paper", dto.Method);
        Assert.Equal("Quinn QLD", dto.UserName);
        var row = await _h.Db.Queryable<FaceConsentRecord>().SingleAsync(c => c.ConsentId == dto.ConsentId);
        Assert.Equal($"face-match/consent/{dto.ConsentId}/scan.jpg", row.EvidenceObjectKey);
        Assert.Equal(FaceImageTestData.Jpeg, _h.Store.Objects[row.EvidenceObjectKey!]);
        Assert.Equal("admin.tester", row.CreatedBy);
        var audit = Assert.Single(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());
        Assert.Equal(FaceMatchAuditActions.ConsentCreate, audit.Action);
        Assert.Equal(dto.ConsentId, audit.TargetRef);
        AssertNoSecrets(audit.TargetRef + audit.Action + audit.Result);
    }

    [Fact]
    public async Task 电子同意可以没有扫描件()
    {
        var dto = await _h.Staff.CreateConsentAsync(ConsentRequest("u-q") with { Method = "electronic" }, null, _actor);

        Assert.False(dto.HasEvidence);
        Assert.Empty(_h.Store.Puts);
    }

    [Fact]
    public async Task 纸质同意必须有扫描件_扫描件类型与大小受限()
    {
        var missing = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Staff.CreateConsentAsync(ConsentRequest("u-q"), null, _actor));
        Assert.Equal("FACE_MATCH_FIELD_INVALID", missing.Code);

        var notDoc = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Staff.CreateConsentAsync(
            ConsentRequest("u-q"), new FaceMatchUpload("x.exe", "application/octet-stream", [1, 2, 3, 4]), _actor));
        Assert.Equal(400, notDoc.StatusCode);

        var pdf = await _h.Staff.CreateConsentAsync(
            ConsentRequest("u-q"), new FaceMatchUpload("c.pdf", "application/pdf", "%PDF-1.7 fake"u8.ToArray()), _actor);
        Assert.EndsWith("/scan.pdf", (await _h.Db.Queryable<FaceConsentRecord>().SingleAsync(c => c.ConsentId == pdf.ConsentId)).EvidenceObjectKey);

        var huge = new byte[FaceMatchRules.MaxConsentScanBytes + 1];
        huge[0] = 0xFF; huge[1] = 0xD8; huge[2] = 0xFF;
        var tooLarge = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateConsentAsync(ConsentRequest("u-q"), new FaceMatchUpload("big.jpg", "image/jpeg", huge), _actor));
        Assert.Equal(400, tooLarge.StatusCode);
    }

    [Fact]
    public async Task 私有桶没配置时上传扫描件被拒_什么都不写()
    {
        _h.Store.Configured = false;

        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Staff.CreateConsentAsync(ConsentRequest("u-q"), Scan(), _actor));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("FACE_MATCH_STORE_NOT_CONFIGURED", ex.Code);
        Assert.Empty(await _h.Db.Queryable<FaceConsentRecord>().ToListAsync());
        Assert.Empty(_h.Store.Puts);
    }

    [Theory]
    [InlineData(null, "FACE_MATCH_NSW_NOTICE_REQUIRED")]
    [InlineData("2026-10-01", "FACE_MATCH_NSW_NOTICE_TOO_RECENT")]
    [InlineData("2026-10-14", "FACE_MATCH_NSW_NOTICE_TOO_RECENT")]
    public async Task NSW员工的同意_通知日期缺失或不满14天_拒绝(string? noticeDate, string code)
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateConsentAsync(ConsentRequest("u-n") with { NswNoticeDate = noticeDate }, Scan(), _actor));

        Assert.Equal(code, ex.Code);
        Assert.Empty(await _h.Db.Queryable<FaceConsentRecord>().ToListAsync());
        Assert.Empty(_h.Store.Puts);
    }

    [Fact]
    public async Task NSW员工的同意_通知恰好满14天通过_QLD员工不需要通知日期()
    {
        var nsw = await _h.Staff.CreateConsentAsync(ConsentRequest("u-n") with { NswNoticeDate = "2026-09-30" }, Scan(), _actor);
        var qld = await _h.Staff.CreateConsentAsync(ConsentRequest("u-q") with { NswNoticeDate = null }, Scan(), _actor);

        Assert.Equal("2026-09-30", nsw.NswNoticeDate);
        Assert.Null(qld.NswNoticeDate);
    }

    [Fact]
    public async Task 同意记录字段校验()
    {
        foreach (var (request, field) in new (FaceConsentCreateRequest, string)[]
                 {
                     (ConsentRequest("u-q") with { UserGuid = null }, "userGuid"),
                     (ConsentRequest("u-q") with { ConsentVersion = " " }, "consentVersion"),
                     (ConsentRequest("u-q") with { ConsentVersion = new string('v', 33) }, "consentVersion"),
                     (ConsentRequest("u-q") with { SignedAtUtc = null }, "signedAtUtc"),
                     (ConsentRequest("u-q") with { SignedAtUtc = _h.At(TimeSpan.FromDays(5)) }, "signedAtUtc"),
                     (ConsentRequest("u-q") with { Method = "fax" }, "method"),
                     (ConsentRequest("u-q") with { NswNoticeDate = "not-a-date" }, "nswNoticeDate"),
                 })
        {
            var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Staff.CreateConsentAsync(request, Scan(), _actor));
            Assert.Equal(400, ex.StatusCode);
            Assert.Contains(field, System.Text.Json.JsonSerializer.Serialize(ex.Details));
        }

        var unknown = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Staff.CreateConsentAsync(ConsentRequest("nobody"), Scan(), _actor));
        Assert.Equal(404, unknown.StatusCode);
    }

    // ---------- 登记 ----------

    [Fact]
    public async Task 登记成功_腾讯云只收到不透明编号_三张照片存私有桶_审计只有编号()
    {
        var consent = await NewConsentAsync("u-q");

        var dto = await _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(4), _actor);

        Assert.Equal("active", dto.StatusName);
        Assert.Equal(4, dto.PhotoCount);
        var row = await _h.Db.Queryable<FaceEnrollment>().SingleAsync(e => e.EnrollmentId == dto.EnrollmentId);
        Assert.Equal(FaceEnrollmentStatus.Active, row.Status);
        Assert.Equal($"st_{dto.EnrollmentId}", row.TencentPersonId);
        var keys = FaceMatchRules.ParseKeys(row.PhotoObjectKeysJson);
        Assert.Equal(4, keys.Count);
        Assert.All(keys, key => Assert.StartsWith($"face-match/enrollment/{dto.EnrollmentId}/", key));
        Assert.All(keys, key => Assert.True(_h.Store.Objects.ContainsKey(key)));
        // 腾讯云：员工库已创建、第一张建人、其余三张追加；PersonName 只是登记编号，不含姓名 / 员工编号。
        Assert.Contains("EnsureGroup:staff", _h.Library.Calls);
        Assert.Contains($"CreatePerson:{row.TencentPersonId}", _h.Library.Calls);
        Assert.Contains($"CreateFaces:{row.TencentPersonId}:3", _h.Library.Calls);
        Assert.Equal(dto.EnrollmentId, _h.Library.PersonNames[row.TencentPersonId]);
        Assert.DoesNotContain("Quinn", _h.Library.PersonNames[row.TencentPersonId]);
        Assert.DoesNotContain("u-q", _h.Library.PersonNames[row.TencentPersonId]);
        var audits = await _h.Db.Queryable<FaceAuditLog>().Where(a => a.Action == FaceMatchAuditActions.EnrollmentCreate).ToListAsync();
        Assert.Equal(dto.EnrollmentId, Assert.Single(audits).TargetRef);
        AssertNoSecrets(string.Join("|", (await _h.Db.Queryable<FaceAuditLog>().ToListAsync()).Select(a => a.TargetRef + a.Result + a.Action)));
        AssertNoSecrets(_h.Logs.Dump());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public async Task 登记照张数必须3到5张(int count)
    {
        var consent = await NewConsentAsync("u-q");

        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(count), _actor));

        Assert.Equal("FACE_MATCH_FIELD_INVALID", ex.Code);
        Assert.Empty(_h.Library.Calls);
        Assert.Empty(await _h.Db.Queryable<FaceEnrollment>().ToListAsync());
    }

    [Fact]
    public async Task 登记照不是图片或超过5MB_拒绝且不碰腾讯云()
    {
        var consent = await NewConsentAsync("u-q");
        var bad = _h.PhotoSet(3);
        bad[1] = new FaceMatchUpload("x.gif", "image/gif", "GIF89a....."u8.ToArray());
        var huge = _h.PhotoSet(3);
        var bytes = new byte[FaceMatchRules.MaxImageBytes + 1];
        FaceImageTestData.Jpeg.CopyTo(bytes, 0);
        huge[2] = new FaceMatchUpload("big.jpg", "image/jpeg", bytes);

        foreach (var photos in new[] { bad, huge })
        {
            var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), photos, _actor));
            Assert.Equal(400, ex.StatusCode);
        }

        Assert.Empty(_h.Library.Calls);
        Assert.DoesNotContain(_h.Store.Puts, key => key.Contains("enrollment"));
    }

    [Fact]
    public async Task 功能未开启或私有桶未配置_登记返回503_什么都不做()
    {
        var consent = await NewConsentAsync("u-q");

        _h.Settings.Options.Tencent.AdminSecretKey = null;
        var disabled = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor));
        Assert.Equal("FACE_MATCH_DISABLED", disabled.Code);
        Assert.Equal(503, disabled.StatusCode);

        _h.Settings.Options.Tencent.AdminSecretKey = "fake-admin-key-material";
        _h.Store.Configured = false;
        var noStore = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor));
        Assert.Equal("FACE_MATCH_STORE_NOT_CONFIGURED", noStore.Code);

        Assert.Empty(_h.Library.Calls);
        Assert.DoesNotContain(_h.Store.Puts, key => key.Contains("enrollment"));
        Assert.Empty(await _h.Db.Queryable<FaceEnrollment>().ToListAsync());
    }

    [Fact]
    public async Task 登记必须先有有效同意_且同意属于该员工()
    {
        var other = await NewConsentAsync("u-n", "2026-09-01");
        var mine = await NewConsentAsync("u-q");

        var noConsent = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateEnrollmentAsync(Enroll("u-q", new string('9', 32)), _h.PhotoSet(3), _actor));
        var wrongOwner = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateEnrollmentAsync(Enroll("u-q", other.ConsentId), _h.PhotoSet(3), _actor));
        await _h.Staff.WithdrawConsentAsync(mine.ConsentId, new(), _actor);
        var withdrawn = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateEnrollmentAsync(Enroll("u-q", mine.ConsentId), _h.PhotoSet(3), _actor));

        Assert.Equal(404, noConsent.StatusCode);
        Assert.Equal(404, wrongOwner.StatusCode);
        Assert.Equal("FACE_MATCH_CONSENT_WITHDRAWN", withdrawn.Code);
        Assert.DoesNotContain(_h.Library.Calls, c => c.StartsWith("CreatePerson"));
    }

    [Fact]
    public async Task 每人只能有一条有效登记_撤回后可以再登记()
    {
        var consent = await NewConsentAsync("u-q");
        var first = await _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor);

        var dup = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor));
        Assert.Equal("FACE_MATCH_ENROLLMENT_EXISTS", dup.Code);

        await _h.Staff.WithdrawEnrollmentAsync(first.EnrollmentId, new(), _actor);
        var again = await _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor);
        Assert.Equal("active", again.StatusName);
        Assert.Equal(2, await _h.Db.Queryable<FaceEnrollment>().CountAsync());
    }

    [Fact]
    public async Task NSW员工登记_同意里的通知日期不满14天仍被拒()
    {
        var consent = await NewConsentAsync("u-n", "2026-09-30");
        // 通知日期在登记之前被篡改成不满 14 天的值：登记时再校验一次。
        await _h.Db.Updateable<FaceConsentRecord>()
            .SetColumns(c => c.NswNoticeDate == new DateTime(2026, 10, 10))
            .Where(c => c.ConsentId == consent.ConsentId)
            .ExecuteCommandAsync();

        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateEnrollmentAsync(Enroll("u-n", consent.ConsentId), _h.PhotoSet(3), _actor));

        Assert.Equal("FACE_MATCH_NSW_NOTICE_TOO_RECENT", ex.Code);
        Assert.Empty(_h.Library.Calls);
    }

    // ---------- 登记失败的回滚 ----------

    [Fact]
    public async Task 腾讯云建人失败_已上传的照片被删除_库里没有登记_映射为照片不可用()
    {
        var consent = await NewConsentAsync("u-q");
        _h.Library.FailOn = op => op == "CreatePerson" ? FakeFaceLibrary.Failure(FaceLibraryFailureKind.InvalidImage, "InvalidParameterValue.NoFaceInPhoto") : null;

        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor));

        Assert.Equal(422, ex.StatusCode);
        Assert.Equal("FACE_MATCH_PHOTO_UNUSABLE", ex.Code);
        Assert.Empty(await _h.Db.Queryable<FaceEnrollment>().ToListAsync());
        Assert.Equal(3, _h.Store.Puts.Count(key => key.Contains("enrollment")));
        Assert.Equal(3, _h.Store.Deletes.Count(key => key.Contains("enrollment")));
        Assert.DoesNotContain(_h.Store.Objects.Keys, k => k.Contains("enrollment"));
    }

    [Fact]
    public async Task 追加人脸失败_腾讯云人员被回滚删除_照片被删除()
    {
        var consent = await NewConsentAsync("u-q");
        _h.Library.FailOn = op => op == "CreateFaces" ? FakeFaceLibrary.Failure(FaceLibraryFailureKind.Unavailable, "InternalError") : null;

        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor));

        Assert.Equal("FACE_MATCH_TENCENT_UNAVAILABLE", ex.Code);
        Assert.Contains(_h.Library.Calls, c => c.StartsWith("DeletePerson:st_"));
        Assert.Empty(_h.Library.PersonGroup);
        Assert.DoesNotContain(_h.Store.Objects.Keys, k => k.Contains("enrollment"));
        Assert.Empty(await _h.Db.Queryable<FaceEnrollment>().ToListAsync());
    }

    [Fact]
    public async Task 部分照片没通过人脸检测_整体回滚()
    {
        var consent = await NewConsentAsync("u-q");
        _h.Library.CreateFacesDrop = 1;

        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(4), _actor));

        Assert.Equal("FACE_MATCH_PHOTO_UNUSABLE", ex.Code);
        Assert.Empty(_h.Library.PersonGroup);
        Assert.DoesNotContain(_h.Store.Objects.Keys, k => k.Contains("enrollment"));
        Assert.Empty(await _h.Db.Queryable<FaceEnrollment>().ToListAsync());
    }

    [Fact]
    public async Task 第二张照片上传私有桶失败_已传的被删_不会去腾讯云建人()
    {
        var consent = await NewConsentAsync("u-q");
        _h.Store.FailOn = (op, key) => op == "put" && key.EndsWith("/1.png")
            ? new FaceMatchStoreException(FaceMatchStoreFailureKind.Denied, "denied")
            : null;

        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor));

        Assert.Equal("FACE_MATCH_STORE_DENIED", ex.Code);
        Assert.DoesNotContain(_h.Library.Calls, c => c.StartsWith("CreatePerson"));
        Assert.DoesNotContain(_h.Store.Objects.Keys, k => k.Contains("enrollment"));
    }

    [Fact]
    public async Task 并发的另一次登记抢先写入_本次返回409并完整回滚()
    {
        var consent = await NewConsentAsync("u-q");
        _h.Library.FailOn = op =>
        {
            if (op == "CreatePerson")
            {
                // 模拟别的请求在本次建人之后、写库之前抢先落库了同一员工的有效登记。
                _h.Db.Insertable(new FaceEnrollment
                {
                    EnrollmentId = new string('e', 32), UserGuid = "u-q", ConsentId = consent.ConsentId, TencentPersonId = "st_other",
                    PhotoObjectKeysJson = "[]", Status = FaceEnrollmentStatus.Active, EnrolledAtUtc = FaceMatchTestDatabase.Now, EnrolledBy = "race",
                }).ExecuteCommand();
            }

            return null;
        };

        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor));

        Assert.Equal("FACE_MATCH_ENROLLMENT_EXISTS", ex.Code);
        Assert.Single(await _h.Db.Queryable<FaceEnrollment>().ToListAsync());
        Assert.Empty(_h.Library.PersonGroup);
        Assert.DoesNotContain(_h.Store.Objects.Keys, k => k.Contains("enrollment"));
    }

    // ---------- 撤回登记 ----------

    [Fact]
    public async Task 撤回登记_腾讯云删人_照片删除_键清空_状态已撤回_审计()
    {
        var consent = await NewConsentAsync("u-q");
        var enrollment = await _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor);

        var after = await _h.Staff.WithdrawEnrollmentAsync(enrollment.EnrollmentId, new() { Reason = "离职" }, _actor);

        Assert.Equal("withdrawn", after.StatusName);
        var row = await _h.Db.Queryable<FaceEnrollment>().SingleAsync(e => e.EnrollmentId == enrollment.EnrollmentId);
        Assert.Equal(FaceEnrollmentStatus.Withdrawn, row.Status);
        Assert.Equal(FaceMatchTestDatabase.Now, row.WithdrawnAtUtc);
        Assert.Equal("[]", row.PhotoObjectKeysJson);
        Assert.Empty(_h.Library.PersonGroup);
        Assert.DoesNotContain(_h.Store.Objects.Keys, k => k.Contains("enrollment"));
        Assert.Contains(await _h.Db.Queryable<FaceAuditLog>().ToListAsync(), a => a.Action == FaceMatchAuditActions.EnrollmentWithdraw && a.Result == "ok");
    }

    [Fact]
    public async Task 撤回登记时腾讯云删除失败_登记保持有效_照片保留_返回502()
    {
        var consent = await NewConsentAsync("u-q");
        var enrollment = await _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor);
        _h.Library.FailOn = op => op == "DeletePerson" ? FakeFaceLibrary.Failure(FaceLibraryFailureKind.Unavailable) : null;

        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Staff.WithdrawEnrollmentAsync(enrollment.EnrollmentId, new(), _actor));

        Assert.Equal("FACE_MATCH_WITHDRAW_PENDING", ex.Code);
        var row = await _h.Db.Queryable<FaceEnrollment>().SingleAsync(e => e.EnrollmentId == enrollment.EnrollmentId);
        Assert.Equal(FaceEnrollmentStatus.Active, row.Status);
        Assert.Equal(3, FaceMatchRules.ParseKeys(row.PhotoObjectKeysJson).Count);
        Assert.Equal(3, _h.Store.Objects.Keys.Count(k => k.Contains("enrollment")));
    }

    [Fact]
    public async Task 撤回登记时腾讯云已删但私有桶删除失败_状态已撤回_照片键保留等后台重试()
    {
        var consent = await NewConsentAsync("u-q");
        var enrollment = await _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor);
        _h.Store.FailOn = (op, _) => op == "delete" ? new FaceMatchStoreException(FaceMatchStoreFailureKind.Error, "boom") : null;

        var after = await _h.Staff.WithdrawEnrollmentAsync(enrollment.EnrollmentId, new(), _actor);

        Assert.Equal("withdrawn", after.StatusName);
        var row = await _h.Db.Queryable<FaceEnrollment>().SingleAsync(e => e.EnrollmentId == enrollment.EnrollmentId);
        Assert.Equal(3, FaceMatchRules.ParseKeys(row.PhotoObjectKeysJson).Count);
        Assert.Empty(_h.Library.PersonGroup);
    }

    [Fact]
    public async Task 重复撤回同一登记是幂等的()
    {
        var consent = await NewConsentAsync("u-q");
        var enrollment = await _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor);
        await _h.Staff.WithdrawEnrollmentAsync(enrollment.EnrollmentId, new(), _actor);
        var deletes = _h.Library.Calls.Count(c => c.StartsWith("DeletePerson"));

        var again = await _h.Staff.WithdrawEnrollmentAsync(enrollment.EnrollmentId, new(), _actor);

        Assert.Equal("withdrawn", again.StatusName);
        Assert.Equal(deletes, _h.Library.Calls.Count(c => c.StartsWith("DeletePerson")));
        var missing = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Staff.WithdrawEnrollmentAsync(new string('0', 32), new(), _actor));
        Assert.Equal(404, missing.StatusCode);
    }

    // ---------- 撤回同意 ----------

    [Fact]
    public async Task 撤回同意_腾讯云与登记照两处都删_同意记录保留并记撤回时间()
    {
        var consent = await NewConsentAsync("u-q");
        var enrollment = await _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor);

        var result = await _h.Staff.WithdrawConsentAsync(consent.ConsentId, new() { Reason = "员工申请" }, _actor);

        Assert.False(result.Consent.Active);
        Assert.Equal("员工申请", result.Consent.WithdrawnReason);
        Assert.Equal(1, result.WithdrawnEnrollmentCount);
        Assert.Equal(0, result.PendingRemoteDeletionCount);
        Assert.Empty(_h.Library.PersonGroup);
        Assert.DoesNotContain(_h.Store.Objects.Keys, k => k.Contains("enrollment"));
        var row = await _h.Db.Queryable<FaceEnrollment>().SingleAsync(e => e.EnrollmentId == enrollment.EnrollmentId);
        Assert.Equal(FaceEnrollmentStatus.Withdrawn, row.Status);
        // 同意书扫描件留作证据，不随撤回删除。
        var consentRow = await _h.Db.Queryable<FaceConsentRecord>().SingleAsync(c => c.ConsentId == consent.ConsentId);
        Assert.NotNull(consentRow.EvidenceObjectKey);
        Assert.True(_h.Store.Objects.ContainsKey(consentRow.EvidenceObjectKey!));
        Assert.Contains(await _h.Db.Queryable<FaceAuditLog>().ToListAsync(), a => a.Action == FaceMatchAuditActions.ConsentWithdraw && a.Result == "ok");
    }

    [Fact]
    public async Task 撤回同意时腾讯云不可用_同意先标记撤回_登记显示待删除_如实返回待处理数()
    {
        var consent = await NewConsentAsync("u-q");
        await _h.Staff.CreateEnrollmentAsync(Enroll("u-q", consent.ConsentId), _h.PhotoSet(3), _actor);
        _h.Library.Available = false;

        var result = await _h.Staff.WithdrawConsentAsync(consent.ConsentId, new(), _actor);

        Assert.False(result.Consent.Active);
        Assert.Equal(0, result.WithdrawnEnrollmentCount);
        Assert.Equal(1, result.PendingRemoteDeletionCount);
        var enrollments = await _h.Staff.ListEnrollmentsAsync("u-q", activeOnly: true);
        var pending = Assert.Single(enrollments);
        Assert.True(pending.PendingRemoteDeletion);
        Assert.Contains(await _h.Db.Queryable<FaceAuditLog>().ToListAsync(), a => a.Result == "ok-pending-remote-deletion");
    }

    [Fact]
    public async Task 重复撤回同意是幂等的_已撤回的同意不会被改写()
    {
        var consent = await NewConsentAsync("u-q");
        await _h.Staff.WithdrawConsentAsync(consent.ConsentId, new() { Reason = "第一次" }, _actor);
        _h.Time.Now = _h.Time.Now.AddHours(1);

        var again = await _h.Staff.WithdrawConsentAsync(consent.ConsentId, new() { Reason = "第二次" }, _actor);

        Assert.Equal("第一次", again.Consent.WithdrawnReason);
        Assert.Equal(FaceMatchTestDatabase.Now, again.Consent.WithdrawnAtUtc);
        var missing = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Staff.WithdrawConsentAsync("nope", new(), _actor));
        Assert.Equal(404, missing.StatusCode);
    }

    [Fact]
    public async Task 列表接口_默认不含已撤回的同意_可选包含()
    {
        var a = await NewConsentAsync("u-q");
        var b = await NewConsentAsync("u-q");
        await _h.Staff.WithdrawConsentAsync(a.ConsentId, new(), _actor);

        Assert.Equal([b.ConsentId], (await _h.Staff.ListConsentsAsync("u-q", includeWithdrawn: false)).Select(c => c.ConsentId));
        Assert.Equal(2, (await _h.Staff.ListConsentsAsync("u-q", includeWithdrawn: true)).Count);
        Assert.Equal(2, (await _h.Staff.ListConsentsAsync(null, includeWithdrawn: true)).Count);
    }

    // ---------- 辅助 ----------

    private async Task<FaceConsentDto> NewConsentAsync(string userGuid, string? noticeDate = null) =>
        await _h.Staff.CreateConsentAsync(ConsentRequest(userGuid) with { NswNoticeDate = noticeDate }, Scan(), _actor);

    private FaceConsentCreateRequest ConsentRequest(string userGuid) => new()
    {
        UserGuid = userGuid,
        ConsentVersion = "v1.0",
        SignedAtUtc = _h.At(TimeSpan.FromDays(-1)),
        Method = "paper",
    };

    private static FaceMatchUpload Scan() => new("scan.jpg", "image/jpeg", FaceImageTestData.Jpeg);

    private static FaceEnrollmentCreateRequest Enroll(string userGuid, string consentId) => new() { UserGuid = userGuid, ConsentId = consentId };

    private static void AssertNoSecrets(string text)
    {
        foreach (var forbidden in new[] { "face-match/", "fake-admin-key-material", "FAKE-ADMIN-ID", "enrollment/", "consent/" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }
}

internal static class FaceStaffTestExtensions
{
    /// <summary>满足 3–5 张要求的一组登记照（jpeg / png 交替）。</summary>
    public static List<FaceMatchUpload> PhotoSet(this FaceMatchTestHarness harness, int count) => FaceMatchTestHarness.Photos(count);
}
