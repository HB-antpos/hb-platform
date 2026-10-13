using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 不受欢迎人员名单：入名单硬条件、提交人 ≠ 批准人、「其他」两人批准、启用闸门、撤回 / 到期的清理、
/// 复核状态机（一个人就够）、命中图与参考照片的服务端转发与审计、误报统计。「今天」固定为 2026-10-14 02:00 UTC。
/// </summary>
public sealed class FaceUnwelcomeTests : IDisposable
{
    private readonly FaceMatchTestHarness _h = new();

    private static readonly FaceMatchActor Submitter = new("sub-1", "Sub Mitter", IsAdmin: true);
    private static readonly FaceMatchActor Approver = new("app-1", "App Rover", IsAdmin: true);
    private static readonly FaceMatchActor Approver2 = new("app-2", "App Rover Two", IsAdmin: true);
    // 非管理员：保持「提交人 ≠ 批准人」「Other 需要两位不同批准人」的原规则。
    private static readonly FaceMatchActor StaffSubmitter = new("psub-1", "Staff Submitter");
    private static readonly FaceMatchActor StaffApprover = new("papp-1", "Staff Approver");
    private static readonly FaceMatchActor StaffApprover2 = new("papp-2", "Staff Approver Two");
    private static readonly FaceMatchActor ReviewerA = new("rev-a", "Reviewer A");
    private static readonly FaceMatchActor ReviewerB = new("rev-b", "Reviewer B");

    private const string HitKey = "face-match/hit/1013/2026/10/14/task-image.jpg";

    public FaceUnwelcomeTests()
    {
        foreach (var user in new[] { Submitter, Approver, Approver2, StaffSubmitter, StaffApprover, StaffApprover2, ReviewerA, ReviewerB })
        {
            _h.Database.AddUser(user.Id, user.Name, "s-1013");
        }
    }

    public void Dispose() => _h.Dispose();

    // ---------- 提交 ----------

    [Fact]
    public async Task 提交_待批准_参考照片存私有桶独立前缀_不调用腾讯云_到期前30天提醒()
    {
        var expires = _h.At(TimeSpan.FromDays(200));

        var dto = await _h.Unwelcome.CreateEntryAsync(NewEntry() with { ExpiresAtUtc = expires }, _h.PhotoSet(2), Submitter);

        Assert.Equal("pending", dto.StatusName);
        Assert.Equal("violenceOrThreat", dto.IncidentCategoryName);
        Assert.Equal(1, dto.ApprovalsNeeded);
        Assert.Equal(0, dto.ApprovalsDone);
        Assert.Equal(2, dto.PhotoCount);
        Assert.Equal(["1013"], dto.StoreScope);
        Assert.Equal(expires.AddDays(-30), dto.ReviewDueAtUtc);
        var row = await _h.Db.Queryable<FaceUnwelcomeEntry>().SingleAsync(e => e.EntryId == dto.EntryId);
        Assert.Equal($"uw_{dto.EntryId}", row.TencentPersonId);
        Assert.Equal("sub-1", row.SubmittedBy);
        Assert.All(FaceMatchRules.ParseKeys(row.PhotoObjectKeysJson), key =>
        {
            Assert.StartsWith($"face-match/unwelcome/{dto.EntryId}/", key);
            Assert.True(_h.Store.Objects.ContainsKey(key));
        });
        Assert.Empty(_h.Library.Calls);
        var audit = Assert.Single(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());
        Assert.Equal(FaceMatchAuditActions.EntrySubmit, audit.Action);
        Assert.Equal(dto.EntryId, audit.TargetRef);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task 事件类别只能是三个枚举值_没有一般偷窃这一项(int category)
    {
        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Unwelcome.CreateEntryAsync(NewEntry() with { IncidentCategory = category }, _h.PhotoSet(1), Submitter));

        Assert.Equal("FACE_MATCH_FIELD_INVALID", ex.Code);
        Assert.Contains("incidentCategory", System.Text.Json.JsonSerializer.Serialize(ex.Details));
        Assert.Equal([1, 2, 3], new[] { (int)FaceIncidentCategory.ViolenceOrThreat, FaceIncidentCategory.OrganisedOrRepeatTheft, FaceIncidentCategory.Other });
    }

    [Fact]
    public async Task 其他类别必须填写说明_并需要两位批准人()
    {
        var noNote = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.CreateEntryAsync(
            NewEntry() with { IncidentCategory = FaceIncidentCategory.Other }, _h.PhotoSet(1), Submitter));
        Assert.Equal(400, noNote.StatusCode);

        var dto = await _h.Unwelcome.CreateEntryAsync(
            NewEntry() with { IncidentCategory = FaceIncidentCategory.Other, CategoryNote = "其他严重事件说明" }, _h.PhotoSet(1), Submitter);
        Assert.Equal(2, dto.ApprovalsNeeded);
        Assert.Equal("其他严重事件说明", dto.CategoryNote);
    }

    [Fact]
    public async Task 证据引用与到期日必填_到期日上限12个月()
    {
        foreach (var request in new[]
                 {
                     NewEntry() with { EvidenceRef = null },
                     NewEntry() with { EvidenceRef = "   " },
                     NewEntry() with { EvidenceRef = new string('E', 101) },
                     NewEntry() with { ExpiresAtUtc = null },
                     NewEntry() with { ExpiresAtUtc = _h.At(TimeSpan.FromDays(-1)) },
                     NewEntry() with { ExpiresAtUtc = FaceMatchTestDatabase.Now.AddMonths(12).AddMinutes(1) },
                 })
        {
            var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.CreateEntryAsync(request, _h.PhotoSet(1), Submitter));
            Assert.Equal("FACE_MATCH_FIELD_INVALID", ex.Code);
        }

        var edge = await _h.Unwelcome.CreateEntryAsync(
            NewEntry() with { ExpiresAtUtc = FaceMatchTestDatabase.Now.AddMonths(12) }, _h.PhotoSet(1), Submitter);
        Assert.Equal("pending", edge.StatusName);
        Assert.Empty(await _h.Db.Queryable<FaceUnwelcomeEntry>().Where(e => e.EvidenceRef == "   ").ToListAsync());
    }

    [Fact]
    public async Task 生效门店必须存在_管理员以外只能为自己的门店提交()
    {
        foreach (var scope in new[] { Array.Empty<string>(), ["0000"], [FaceMatchTestDatabase.DeletedStore], ["bad code"] })
        {
            var ex = await Assert.ThrowsAsync<FaceMatchException>(
                () => _h.Unwelcome.CreateEntryAsync(NewEntry() with { StoreScope = scope }, _h.PhotoSet(1), Submitter));
            Assert.Equal(400, ex.StatusCode);
        }

        var forbidden = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Unwelcome.CreateEntryAsync(NewEntry() with { StoreScope = ["2001"] }, _h.PhotoSet(1), ReviewerA));
        Assert.Equal(403, forbidden.StatusCode);

        var ok = await _h.Unwelcome.CreateEntryAsync(NewEntry() with { StoreScope = ["2001", "1013", "1013"] }, _h.PhotoSet(1), Submitter);
        Assert.Equal(["1013", "2001"], ok.StoreScope);
    }

    [Fact]
    public async Task 参考照片张数类型大小受限_私有桶未配置返回503_上传失败会清理已传的()
    {
        foreach (var photos in new[] { new List<FaceMatchUpload>(), FaceMatchTestHarness.Photos(6), [new FaceMatchUpload("x", "image/gif", "GIF89a....."u8.ToArray())] })
        {
            var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.CreateEntryAsync(NewEntry(), photos, Submitter));
            Assert.Equal(400, ex.StatusCode);
        }

        _h.Store.Configured = false;
        var noStore = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.CreateEntryAsync(NewEntry(), _h.PhotoSet(1), Submitter));
        Assert.Equal("FACE_MATCH_STORE_NOT_CONFIGURED", noStore.Code);
        _h.Store.Configured = true;

        _h.Store.FailOn = (op, key) => op == "put" && key.EndsWith("/1.png") ? new FaceMatchStoreException(FaceMatchStoreFailureKind.Error, "x") : null;
        var failed = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.CreateEntryAsync(NewEntry(), _h.PhotoSet(2), Submitter));
        Assert.Equal("FACE_MATCH_STORE_ERROR", failed.Code);
        Assert.Empty(_h.Store.Objects);
        Assert.Empty(await _h.Db.Queryable<FaceUnwelcomeEntry>().ToListAsync());
    }

    // ---------- 批准 ----------

    [Fact]
    public async Task 非管理员的提交人不能批准自己的条目()
    {
        var entry = await SubmitAsync(by: StaffSubmitter);

        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ApproveEntryAsync(entry.EntryId, StaffSubmitter));

        Assert.Equal("FACE_MATCH_APPROVER_IS_SUBMITTER", ex.Code);
        Assert.Empty(_h.Library.Calls);
    }

    [Fact]
    public async Task 生效门店都没通过名单启用闸门_批准被拒_照片不会发往腾讯云()
    {
        var entry = await SubmitAsync();

        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Approver));

        Assert.Equal("FACE_MATCH_UNWELCOME_NOT_ENABLED", ex.Code);
        Assert.Empty(_h.Library.Calls);
        Assert.Empty(_h.Store.Reads);
        Assert.Equal(FaceUnwelcomeEntryStatus.Pending, (await Load(entry.EntryId)).Status);
    }

    [Fact]
    public async Task 功能未开启或私有桶未配置_批准返回503()
    {
        EnableUnwelcome("1013");
        var entry = await SubmitAsync();

        _h.Settings.Options.Tencent.Region = null;
        var disabled = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Approver));
        Assert.Equal("FACE_MATCH_DISABLED", disabled.Code);

        _h.Settings.Options.Tencent.Region = "ap-guangzhou";
        _h.Store.Configured = false;
        var noStore = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Approver));
        Assert.Equal("FACE_MATCH_STORE_NOT_CONFIGURED", noStore.Code);
        Assert.Empty(_h.Library.Calls);
    }

    [Fact]
    public async Task 批准通过_腾讯云建人_条目生效_审计()
    {
        EnableUnwelcome("1013");
        var entry = await SubmitAsync(photos: 3);

        var approved = await _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Approver);

        Assert.Equal("active", approved.StatusName);
        Assert.Equal("App Rover", approved.ApprovedByName);
        Assert.Equal(1, approved.ApprovalsDone);
        var row = await Load(entry.EntryId);
        Assert.Equal(FaceUnwelcomeEntryStatus.Active, row.Status);
        Assert.Equal("app-1", row.ApprovedBy);
        Assert.Equal(FaceMatchTestDatabase.Now, row.ApprovedAtUtc);
        Assert.Contains("EnsureGroup:unwelcome", _h.Library.Calls);
        Assert.Contains($"CreatePerson:uw_{entry.EntryId}", _h.Library.Calls);
        Assert.Contains($"CreateFaces:uw_{entry.EntryId}:2", _h.Library.Calls);
        // 腾讯云只收到不透明编号：PersonName 是条目编号，不含事件类别、证据编号或姓名。
        Assert.Equal(entry.EntryId, _h.Library.PersonNames[$"uw_{entry.EntryId}"]);
        Assert.Contains(await _h.Db.Queryable<FaceAuditLog>().ToListAsync(), a => a.Action == FaceMatchAuditActions.EntryApprove && a.Result == "active");
    }

    [Fact]
    public async Task 其他类别_非管理员第一位只记录_第二位不同的批准人才生效()
    {
        EnableUnwelcome("1013");
        var entry = await SubmitAsync(category: FaceIncidentCategory.Other, note: "说明", by: StaffSubmitter);

        var first = await _h.Unwelcome.ApproveEntryAsync(entry.EntryId, StaffApprover);
        Assert.Equal("pending", first.StatusName);
        Assert.Equal(1, first.ApprovalsDone);
        Assert.Equal(2, first.ApprovalsNeeded);
        Assert.Empty(_h.Library.Calls);

        var same = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ApproveEntryAsync(entry.EntryId, StaffApprover));
        Assert.Equal("FACE_MATCH_ALREADY_APPROVED_BY_YOU", same.Code);
        var submitter = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ApproveEntryAsync(entry.EntryId, StaffSubmitter));
        Assert.Equal("FACE_MATCH_APPROVER_IS_SUBMITTER", submitter.Code);

        var second = await _h.Unwelcome.ApproveEntryAsync(entry.EntryId, StaffApprover2);
        Assert.Equal("active", second.StatusName);
        Assert.Equal("Staff Approver", second.ApprovedByName);
        Assert.Equal("Staff Approver Two", second.SecondApprovedByName);
        Assert.Equal(2, second.ApprovalsDone);
        var row = await Load(entry.EntryId);
        Assert.Equal("papp-1", row.ApprovedBy);
        Assert.Equal("papp-2", row.SecondApprovedBy);
        Assert.Contains($"CreatePerson:uw_{entry.EntryId}", _h.Library.Calls);
    }

    // ---------- 管理员一个人批准即可生效（2026-10-13 用户决定） ----------

    [Fact]
    public async Task 管理员一个人批准即可生效_可以批准自己提交的条目_审计留痕()
    {
        EnableUnwelcome("1013");
        var entry = await SubmitAsync();   // 提交人 Submitter 是管理员

        var approved = await _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Submitter);

        Assert.Equal("active", approved.StatusName);
        Assert.Equal("sub-1", (await Load(entry.EntryId)).ApprovedBy);
        Assert.Contains($"CreatePerson:uw_{entry.EntryId}", _h.Library.Calls);
        Assert.Contains(
            await _h.Db.Queryable<FaceAuditLog>().ToListAsync(),
            a => a.Action == FaceMatchAuditActions.EntryApprove && a.Result == "active;self-approved");
    }

    [Fact]
    public async Task 管理员批准其他类别_一位即生效_不需要第二位_审计标记单人批准()
    {
        EnableUnwelcome("1013");
        var entry = await SubmitAsync(category: FaceIncidentCategory.Other, note: "说明");

        var approved = await _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Approver);

        Assert.Equal("active", approved.StatusName);
        // 已生效的单人批准条目按实际显示 1 / 1，不会出现「已生效 1 / 2」。
        Assert.Equal(1, approved.ApprovalsNeeded);
        Assert.Equal(1, approved.ApprovalsDone);
        var row = await Load(entry.EntryId);
        Assert.Equal("app-1", row.ApprovedBy);
        Assert.Null(row.SecondApprovedBy);
        Assert.Contains($"CreatePerson:uw_{entry.EntryId}", _h.Library.Calls);
        Assert.Contains(
            await _h.Db.Queryable<FaceAuditLog>().ToListAsync(),
            a => a.Action == FaceMatchAuditActions.EntryApprove && a.Result == "active;admin-solo");
    }

    [Fact]
    public async Task 其他类别_非管理员已批准一次后_管理员一位即可补完生效()
    {
        EnableUnwelcome("1013");
        var entry = await SubmitAsync(category: FaceIncidentCategory.Other, note: "说明", by: StaffSubmitter);
        await _h.Unwelcome.ApproveEntryAsync(entry.EntryId, StaffApprover);

        var finished = await _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Approver);

        Assert.Equal("active", finished.StatusName);
        var row = await Load(entry.EntryId);
        Assert.Equal("papp-1", row.ApprovedBy);
        Assert.Equal("app-1", row.SecondApprovedBy);
        Assert.Equal(2, finished.ApprovalsDone);
    }

    [Fact]
    public async Task 管理员的批准不绕过其它校验_未开名单_已生效_已过期照样拒绝()
    {
        var entry = await SubmitAsync();
        var notEnabled = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Submitter));
        Assert.Equal("FACE_MATCH_UNWELCOME_NOT_ENABLED", notEnabled.Code);
        Assert.Empty(_h.Library.Calls);

        EnableUnwelcome("1013");
        await _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Submitter);
        var again = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Submitter));
        Assert.Equal("FACE_MATCH_ENTRY_NOT_PENDING", again.Code);

        var pending = await SubmitAsync();
        _h.Time.Now = _h.Time.Now.AddDays(130);
        var expired = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ApproveEntryAsync(pending.EntryId, Submitter));
        Assert.Equal("FACE_MATCH_ENTRY_EXPIRED", expired.Code);
    }

    [Fact]
    public async Task 已生效或已过期的条目不能再批准()
    {
        EnableUnwelcome("1013");
        var entry = await SubmitAsync();
        await _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Approver);

        var again = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Approver2));
        Assert.Equal("FACE_MATCH_ENTRY_NOT_PENDING", again.Code);

        var pending = await SubmitAsync();
        _h.Time.Now = _h.Time.Now.AddDays(130);
        var expired = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ApproveEntryAsync(pending.EntryId, Approver));
        Assert.Equal("FACE_MATCH_ENTRY_EXPIRED", expired.Code);
    }

    [Fact]
    public async Task 腾讯云建人失败_条目保持待批准_没有残留人员()
    {
        EnableUnwelcome("1013");
        var entry = await SubmitAsync();
        _h.Library.FailOn = op => op == "CreatePerson" ? FakeFaceLibrary.Failure(FaceLibraryFailureKind.InvalidImage, "InvalidParameterValue.NoFaceInPhoto") : null;

        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Approver));

        Assert.Equal("FACE_MATCH_PHOTO_UNUSABLE", ex.Code);
        var row = await Load(entry.EntryId);
        Assert.Equal(FaceUnwelcomeEntryStatus.Pending, row.Status);
        Assert.Null(row.ApprovedBy);
        Assert.Empty(_h.Library.PersonGroup);
    }

    [Fact]
    public async Task 批准期间条目被并发撤回_本次批准失败_腾讯云人员被回滚()
    {
        EnableUnwelcome("1013");
        var entry = await SubmitAsync();
        _h.Library.FailOn = op =>
        {
            if (op == "CreatePerson")
            {
                _h.Db.Updateable<FaceUnwelcomeEntry>().SetColumns(e => e.Status == FaceUnwelcomeEntryStatus.Withdrawn)
                    .Where(e => e.EntryId == entry.EntryId).ExecuteCommand();
            }

            return null;
        };

        var ex = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Approver));

        Assert.Equal("FACE_MATCH_ENTRY_CHANGED", ex.Code);
        Assert.Empty(_h.Library.PersonGroup);
        Assert.Equal(FaceUnwelcomeEntryStatus.Withdrawn, (await Load(entry.EntryId)).Status);
    }

    // ---------- 撤回 ----------

    [Fact]
    public async Task 撤回生效中的条目_状态先改_腾讯云删人_参考照片删除_键清空()
    {
        EnableUnwelcome("1013");
        var entry = await SubmitAsync(photos: 2);
        await _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Approver);

        var after = await _h.Unwelcome.WithdrawEntryAsync(entry.EntryId, new() { Reason = "误入" }, Submitter);

        Assert.Equal("withdrawn", after.StatusName);
        Assert.False(after.CleanupPending);
        var row = await Load(entry.EntryId);
        Assert.Equal("[]", row.PhotoObjectKeysJson);
        Assert.Empty(_h.Library.PersonGroup);
        Assert.Empty(_h.Store.Objects);
        Assert.Contains(await _h.Db.Queryable<FaceAuditLog>().ToListAsync(), a => a.Action == FaceMatchAuditActions.EntryWithdraw);
    }

    [Fact]
    public async Task 撤回时腾讯云或私有桶删除失败_状态仍然是已撤回_标记清理待完成()
    {
        EnableUnwelcome("1013");
        var entry = await SubmitAsync(photos: 1);
        await _h.Unwelcome.ApproveEntryAsync(entry.EntryId, Approver);
        _h.Library.FailOn = op => op == "DeletePerson" ? FakeFaceLibrary.Failure(FaceLibraryFailureKind.Unavailable) : null;

        var after = await _h.Unwelcome.WithdrawEntryAsync(entry.EntryId, new(), Submitter);

        Assert.Equal("withdrawn", after.StatusName);
        Assert.True(after.CleanupPending);
        Assert.Equal(1, after.PhotoCount);
        Assert.Equal(FaceUnwelcomeEntryStatus.Withdrawn, (await Load(entry.EntryId)).Status);
    }

    [Fact]
    public async Task 重复撤回是幂等的_陌生编号404()
    {
        var entry = await SubmitAsync();
        await _h.Unwelcome.WithdrawEntryAsync(entry.EntryId, new(), Submitter);
        var calls = _h.Library.Calls.Count;

        var again = await _h.Unwelcome.WithdrawEntryAsync(entry.EntryId, new(), Submitter);

        Assert.Equal("withdrawn", again.StatusName);
        Assert.Equal(calls, _h.Library.Calls.Count);
        var missing = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.WithdrawEntryAsync(new string('f', 32), new(), Submitter));
        Assert.Equal(404, missing.StatusCode);
    }

    [Fact]
    public async Task 申诉说明可以设置_过长被拒()
    {
        var entry = await SubmitAsync();

        var dto = await _h.Unwelcome.SetDisputeNoteAsync(entry.EntryId, new() { Note = "本人来电查询" }, Submitter);
        Assert.Equal("本人来电查询", dto.DisputeNote);

        var ex = await Assert.ThrowsAsync<FaceMatchException>(
            () => _h.Unwelcome.SetDisputeNoteAsync(entry.EntryId, new() { Note = new string('x', 501) }, Submitter));
        Assert.Equal(400, ex.StatusCode);
    }

    // ---------- 列表 ----------

    [Fact]
    public async Task 列表_状态与门店筛选_canApprove按提交人与批准进度计算_管理员不受限_即将到期标记()
    {
        EnableUnwelcome("1013");
        var pending = await SubmitAsync();
        // 「其他」由非管理员批准一次，仍是待批准（第一位）。
        var other = await SubmitAsync(category: FaceIncidentCategory.Other, note: "n", by: StaffSubmitter);
        var active = await SubmitAsync(expiresInDays: 20);
        await _h.Unwelcome.ApproveEntryAsync(active.EntryId, Approver);
        await _h.Unwelcome.ApproveEntryAsync(other.EntryId, StaffApprover);

        var all = await _h.Unwelcome.ListEntriesAsync(null, null, 1, 20, Approver, canApprove: true);
        var forApprover = all.Items.ToDictionary(e => e.EntryId);
        Assert.True(forApprover[pending.EntryId].CanApprove);
        Assert.True(forApprover[other.EntryId].CanApprove);    // 管理员一位即可补完生效
        Assert.False(forApprover[active.EntryId].CanApprove);  // 已生效
        Assert.True(forApprover[active.EntryId].ExpiringSoon);
        Assert.False(forApprover[pending.EntryId].ExpiringSoon);

        // 管理员提交人：自己提交的条目也能批准（管理员一个人批准即可）。
        var forAdminSubmitter = await _h.Unwelcome.ListEntriesAsync("pending", "1013", 1, 20, Submitter, canApprove: true);
        Assert.Equal(2, forAdminSubmitter.Items.Count);
        Assert.All(forAdminSubmitter.Items, e => Assert.True(e.CanApprove));
        // 非管理员提交人：自己提交的不能批准；已批准过一次的不能再批准一次。
        var forStaffSubmitter = await _h.Unwelcome.ListEntriesAsync("pending", "1013", 1, 20, StaffSubmitter, canApprove: true);
        Assert.False(forStaffSubmitter.Items.Single(e => e.EntryId == other.EntryId).CanApprove);
        var forStaffApprover = await _h.Unwelcome.ListEntriesAsync("pending", "1013", 1, 20, StaffApprover, canApprove: true);
        Assert.False(forStaffApprover.Items.Single(e => e.EntryId == other.EntryId).CanApprove);
        Assert.True(forStaffApprover.Items.Single(e => e.EntryId == pending.EntryId).CanApprove);
        var noRights = await _h.Unwelcome.ListEntriesAsync(null, null, 1, 20, Approver2, canApprove: false);
        Assert.All(noRights.Items, e => Assert.False(e.CanApprove));

        Assert.Single((await _h.Unwelcome.ListEntriesAsync("active", null, 1, 20, Approver, true)).Items);
        Assert.Single((await _h.Unwelcome.ListEntriesAsync("1", null, 1, 20, Approver, true)).Items);   // 数值同样可用
        Assert.Empty((await _h.Unwelcome.ListEntriesAsync(null, "2001", 1, 20, Approver, true)).Items);
        var bad = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ListEntriesAsync("bogus", null, 1, 20, Approver, true));
        Assert.Equal(400, bad.StatusCode);
    }

    [Fact]
    public async Task 列表_分页_非管理员只看到与自己门店有交集的条目()
    {
        for (var i = 0; i < 3; i++) await SubmitAsync();
        await _h.Unwelcome.CreateEntryAsync(NewEntry() with { StoreScope = ["2001"] }, _h.PhotoSet(1), Submitter);

        var page1 = await _h.Unwelcome.ListEntriesAsync(null, null, 1, 2, Submitter, false);
        var page2 = await _h.Unwelcome.ListEntriesAsync(null, null, 2, 2, Submitter, false);
        Assert.Equal(2, page1.Items.Count);
        Assert.True(page1.HasMore);
        Assert.Equal(2, page2.Items.Count);
        Assert.False(page2.HasMore);

        var store1013Only = await _h.Unwelcome.ListEntriesAsync(null, null, 1, 20, ReviewerA, false);
        Assert.Equal(3, store1013Only.Items.Count);
        Assert.All(store1013Only.Items, e => Assert.Contains("1013", e.StoreScope));
    }

    // ---------- 复核任务 ----------

    [Fact]
    public async Task 任务列表_相似度百分比取整_档位按配置阈值_不含姓名与事件细节()
    {
        var entry = await SeedActiveEntryAsync();
        await SeedTaskAsync(entry, score: 87.5m);
        await SeedTaskAsync(entry, score: 94.2m);

        var page = await _h.Unwelcome.ListTasksAsync(null, null, 1, 20, ReviewerA);

        var byPercent = page.Items.ToDictionary(t => t.ScorePercent);
        Assert.Equal("normal", byPercent[88].Band);
        Assert.Equal("high", byPercent[94].Band);
        Assert.All(page.Items, task =>
        {
            Assert.Equal("first", task.NextStep);
            Assert.True(task.CanReview);
            Assert.True(task.ImageAvailable);
            Assert.Equal(2, task.ReferencePhotoCount);
            Assert.Equal("Orion", task.StoreName);
            var json = System.Text.Json.JsonSerializer.Serialize(task);
            Assert.DoesNotContain(HitKey, json, StringComparison.Ordinal);
            Assert.DoesNotContain("EVID", json, StringComparison.Ordinal);
            Assert.DoesNotContain("violence", json, StringComparison.OrdinalIgnoreCase);
        });
        _h.Settings.Options.Unwelcome.HighBandScore = 80m;
        Assert.All((await _h.Unwelcome.ListTasksAsync(null, null, 1, 20, ReviewerA)).Items, task => Assert.Equal("high", task.Band));
    }

    [Fact]
    public async Task 一人复核_判断是同一个人即确认结案_不再有第二步_命中图按原保留策略保留()
    {
        var task = await SeedTaskAsync(await SeedActiveEntryAsync());

        var done = await _h.Unwelcome.ReviewTaskAsync(task, new() { Verdict = "match" }, ReviewerA);

        Assert.Equal("confirmed", done.ReviewStatusName);
        Assert.Equal("none", done.NextStep);
        Assert.Equal("Reviewer A", done.FirstReviewerName);
        Assert.Null(done.SecondReviewerName);
        var row = await _h.Db.Queryable<FaceMatchTask>().SingleAsync(t => t.TaskId == task);
        Assert.Equal(FaceMatchReviewStatus.Confirmed, row.ReviewStatus);
        Assert.Equal("rev-a", row.FirstReviewerId);
        Assert.Null(row.SecondReviewerId);
        // 确认的任务命中图按原保留策略保留，不提前删除。
        Assert.Equal(FaceMatchTestDatabase.Now.AddDays(7), row.ImageDeleteAtUtc);

        // 已结案：任何人再提交都是 409，而不是进入第二步。
        var closed = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ReviewTaskAsync(task, new() { Verdict = "match" }, ReviewerB));
        Assert.Equal("FACE_MATCH_TASK_CLOSED", closed.Code);
    }

    [Fact]
    public async Task 判断不是同一个人_任务立即驳回_命中图提前到期()
    {
        var firstSaysNo = await SeedTaskAsync(await SeedActiveEntryAsync());

        var a = await _h.Unwelcome.ReviewTaskAsync(firstSaysNo, new() { Verdict = "notMatch" }, ReviewerA);

        Assert.Equal("dismissed", a.ReviewStatusName);
        Assert.Equal("none", a.NextStep);
        var rowA = await _h.Db.Queryable<FaceMatchTask>().SingleAsync(t => t.TaskId == firstSaysNo);
        Assert.Null(rowA.SecondReviewerId);
        Assert.Equal(FaceMatchTestDatabase.Now, rowA.ImageDeleteAtUtc);
    }

    [Fact]
    public async Task 旧版遗留的已一审待二审任务_列表按待复核显示_任何复核人包括第一复核人本人都能直接结案()
    {
        var entry = await SeedActiveEntryAsync();
        var confirmByFirst = await SeedTaskAsync(entry);
        var dismissBySecond = await SeedTaskAsync(entry);
        // 模拟两人复核时代留下的数据：第一复核人已判断「是同一个人」，状态仍是 Pending。
        foreach (var id in new[] { confirmByFirst, dismissBySecond })
        {
            await _h.Db.Updateable<FaceMatchTask>()
                .SetColumns(t => new FaceMatchTask { FirstReviewerId = "rev-a", FirstReviewedAtUtc = FaceMatchTestDatabase.Now.AddMinutes(-3) })
                .Where(t => t.TaskId == id)
                .ExecuteCommandAsync();
        }

        var listed = (await _h.Unwelcome.ListTasksAsync("pending", null, 1, 20, ReviewerA)).Items;
        Assert.All(listed, t =>
        {
            Assert.Equal("first", t.NextStep);   // 不再出现 second
            Assert.True(t.CanReview);            // 第一复核人本人也能结案
        });

        var a = await _h.Unwelcome.ReviewTaskAsync(confirmByFirst, new() { Verdict = "match" }, ReviewerA);
        Assert.Equal("confirmed", a.ReviewStatusName);
        var b = await _h.Unwelcome.ReviewTaskAsync(dismissBySecond, new() { Verdict = "notMatch" }, ReviewerB);
        Assert.Equal("dismissed", b.ReviewStatusName);

        var rowA = await _h.Db.Queryable<FaceMatchTask>().SingleAsync(t => t.TaskId == confirmByFirst);
        Assert.Equal("rev-a", rowA.SecondReviewerId);   // 结案人记在 Second* 列
        var rowB = await _h.Db.Queryable<FaceMatchTask>().SingleAsync(t => t.TaskId == dismissBySecond);
        Assert.Equal("rev-b", rowB.SecondReviewerId);
        Assert.Equal(FaceMatchTestDatabase.Now, rowB.ImageDeleteAtUtc);
    }

    [Fact]
    public async Task 复核校验_结论非法400_图已删除不能复核_门店范围外404()
    {
        var entry = await SeedActiveEntryAsync();
        var task = await SeedTaskAsync(entry);
        var gone = await SeedTaskAsync(entry, imageKey: "");
        var otherStore = await SeedTaskAsync(entry, storeCode: "2001");

        var bad = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ReviewTaskAsync(task, new() { Verdict = "maybe" }, ReviewerA));
        Assert.Equal(400, bad.StatusCode);
        var imageGone = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ReviewTaskAsync(gone, new() { Verdict = "match" }, ReviewerA));
        Assert.Equal("FACE_MATCH_IMAGE_GONE", imageGone.Code);
        var outside = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ReviewTaskAsync(otherStore, new() { Verdict = "match" }, ReviewerA));
        Assert.Equal(404, outside.StatusCode);
        var unknown = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.ReviewTaskAsync("nope", new() { Verdict = "match" }, ReviewerA));
        Assert.Equal(404, unknown.StatusCode);
        // 非管理员只能看到自己门店的任务。
        var visible = await _h.Unwelcome.ListTasksAsync(null, null, 1, 20, ReviewerA);
        Assert.DoesNotContain(visible.Items, t => t.TaskId == otherStore);
        Assert.Contains((await _h.Unwelcome.ListTasksAsync(null, null, 1, 20, Submitter)).Items, t => t.TaskId == otherStore);
    }

    [Fact]
    public async Task 复核写审计_只有任务编号与步骤结论()
    {
        var entry = await SeedActiveEntryAsync();
        var taskA = await SeedTaskAsync(entry);
        var taskB = await SeedTaskAsync(entry);

        await _h.Unwelcome.ReviewTaskAsync(taskA, new() { Verdict = "match" }, ReviewerA);
        await _h.Unwelcome.ReviewTaskAsync(taskB, new() { Verdict = "notMatch" }, ReviewerB);

        var audits = (await _h.Db.Queryable<FaceAuditLog>().Where(a => a.Action == FaceMatchAuditActions.TaskReview).ToListAsync())
            .OrderBy(a => a.AuditId).ToList();
        Assert.Equal(["first:match", "first:notMatch"], audits.Select(a => a.Result));
        Assert.Equal([taskA, taskB], audits.Select(a => a.TargetRef));
        Assert.All(audits, a => Assert.Equal("1013", a.StoreCode));
    }

    // ---------- 看图 ----------

    [Fact]
    public async Task 命中图_服务端转发字节_写持久审计_日志与审计不含对象键()
    {
        var task = await SeedTaskAsync(await SeedActiveEntryAsync());

        var content = await _h.Unwelcome.GetTaskImageAsync(task, ReviewerA);

        Assert.Equal("image/jpeg", content.ContentType);
        Assert.Equal(FaceImageTestData.Jpeg, content.Bytes);
        var audit = Assert.Single(await _h.Db.Queryable<FaceAuditLog>().ToListAsync());
        Assert.Equal(FaceMatchAuditActions.TaskViewCapture, audit.Action);
        Assert.Equal(task, audit.TargetRef);
        Assert.Equal("ok", audit.Result);
        Assert.Equal("rev-a", audit.ActorId);
        Assert.DoesNotContain("face-match", audit.TargetRef + audit.Result);
        Assert.DoesNotContain(HitKey, _h.Logs.Dump(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 命中图_被拒绝的尝试也写审计_结果为错误码()
    {
        var entry = await SeedActiveEntryAsync();
        var gone = await SeedTaskAsync(entry, imageKey: "");
        var forged = await SeedTaskAsync(entry, imageKey: $"face-match/enrollment/{new string('a', 32)}/0.jpg");
        var notImage = await SeedTaskAsync(entry, imageKey: "face-match/hit/x/notimage.bin");
        _h.Store.Objects["face-match/hit/x/notimage.bin"] = [1, 2, 3, 4];

        var e1 = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.GetTaskImageAsync(gone, ReviewerA));
        var e2 = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.GetTaskImageAsync(forged, ReviewerA));
        var e3 = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.GetTaskImageAsync(notImage, ReviewerA));
        var e4 = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.GetTaskImageAsync("unknown", ReviewerA));

        Assert.Equal(["FACE_MATCH_IMAGE_GONE", "FACE_MATCH_IMAGE_GONE", "FACE_MATCH_IMAGE_UNSUPPORTED", "FACE_MATCH_NOT_FOUND"], [e1.Code, e2.Code, e3.Code, e4.Code]);
        Assert.DoesNotContain(_h.Store.Reads, key => key.Contains("enrollment"));
        var results = (await _h.Db.Queryable<FaceAuditLog>().ToListAsync()).OrderBy(a => a.AuditId).Select(a => a.Result).ToList();
        Assert.Equal(["FACE_MATCH_IMAGE_GONE", "FACE_MATCH_IMAGE_GONE", "FACE_MATCH_IMAGE_UNSUPPORTED", "FACE_MATCH_NOT_FOUND"], results);
    }

    [Fact]
    public async Task 命中图_非管理员看不到别的门店的任务_审计表写失败时不返回图片()
    {
        var other = await SeedTaskAsync(await SeedActiveEntryAsync(), storeCode: "2001");
        var mine = await SeedTaskAsync(await SeedActiveEntryAsync());

        var denied = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.GetTaskImageAsync(other, ReviewerA));
        Assert.Equal(404, denied.StatusCode);

        // 审计是合规控制：审计写不进去就不返回图片字节。
        _h.Db.Ado.ExecuteCommand("DROP TABLE FaceAuditLog");
        var failed = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.GetTaskImageAsync(mine, ReviewerA));
        Assert.Equal("FACE_MATCH_AUDIT_FAILED", failed.Code);
    }

    [Fact]
    public async Task 参考照片_按下标转发_条目撤回后不再提供_下标越界404_审计目标含下标()
    {
        var entry = await SeedActiveEntryAsync();
        var task = await SeedTaskAsync(entry);

        var first = await _h.Unwelcome.GetReferencePhotoAsync(task, 0, ReviewerA);
        var second = await _h.Unwelcome.GetReferencePhotoAsync(task, 1, ReviewerA);
        Assert.Equal("image/jpeg", first.ContentType);
        Assert.Equal("image/png", second.ContentType);

        var outOfRange = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.GetReferencePhotoAsync(task, 2, ReviewerA));
        var negative = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.GetReferencePhotoAsync(task, -1, ReviewerA));
        Assert.Equal(404, outOfRange.StatusCode);
        Assert.Equal(404, negative.StatusCode);

        await _h.Unwelcome.WithdrawEntryAsync(entry, new(), Submitter);
        var withdrawn = await Assert.ThrowsAsync<FaceMatchException>(() => _h.Unwelcome.GetReferencePhotoAsync(task, 0, ReviewerA));
        Assert.Equal("FACE_MATCH_IMAGE_GONE", withdrawn.Code);

        var audits = (await _h.Db.Queryable<FaceAuditLog>().Where(a => a.Action == FaceMatchAuditActions.TaskViewReference).ToListAsync())
            .OrderBy(a => a.AuditId).ToList();
        Assert.Equal([$"{task}:0", $"{task}:1", $"{task}:2", $"{task}:-1", $"{task}:0"], audits.Select(a => a.TargetRef));
        Assert.Equal("ok", audits[0].Result);
        Assert.Equal("FACE_MATCH_IMAGE_GONE", audits[^1].Result);
    }

    // ---------- 误报统计 ----------

    [Fact]
    public async Task 误报统计_按条目与相似度区间_驳回率高的条目建议复核()
    {
        var noisy = await SeedActiveEntryAsync();
        var solid = await SeedActiveEntryAsync();
        for (var i = 0; i < 5; i++) await SeedTaskAsync(noisy, score: 62.5m + i, status: FaceMatchReviewStatus.Dismissed);
        for (var i = 0; i < 4; i++) await SeedTaskAsync(solid, score: 96m, status: FaceMatchReviewStatus.Confirmed);
        await SeedTaskAsync(solid, score: 100m, status: FaceMatchReviewStatus.Dismissed);
        await SeedTaskAsync(solid, score: 91m);

        var stats = await _h.Unwelcome.GetStatsAsync(Submitter);

        Assert.Equal(11, stats.TaskCount);
        Assert.Equal(1, stats.PendingCount);
        Assert.Equal(6, stats.DismissedCount);
        Assert.Equal(4, stats.ConfirmedCount);
        Assert.Equal(0.6m, stats.DismissRate);
        var noisyStats = stats.Entries.Single(e => e.EntryId == noisy);
        Assert.True(noisyStats.SuggestReview);
        Assert.Equal(1m, noisyStats.DismissRate);
        var solidStats = stats.Entries.Single(e => e.EntryId == solid);
        Assert.False(solidStats.SuggestReview);
        Assert.Equal(0.2m, solidStats.DismissRate);
        Assert.Equal(noisy, stats.Entries[0].EntryId);   // 建议复核的排在最前
        var bucket95 = stats.ScoreBuckets.Single(b => b.From == 95);
        Assert.Equal(100, bucket95.To);
        Assert.Equal(5, bucket95.Total);          // 4 条 96 + 1 条 100
        Assert.Equal(1, bucket95.DismissedCount);
        Assert.Equal(4, bucket95.ConfirmedCount);
        Assert.Equal(3, stats.ScoreBuckets.Single(b => b.From == 60).Total);
        Assert.Equal(2, stats.ScoreBuckets.Single(b => b.From == 65).Total);
    }

    [Fact]
    public async Task 误报统计_非管理员只统计自己门店()
    {
        var entry = await SeedActiveEntryAsync();
        await SeedTaskAsync(entry);
        await SeedTaskAsync(entry, storeCode: "2001");

        Assert.Equal(1, (await _h.Unwelcome.GetStatsAsync(ReviewerA)).TaskCount);
        Assert.Equal(2, (await _h.Unwelcome.GetStatsAsync(Submitter)).TaskCount);
    }

    // ---------- 辅助 ----------

    private FaceUnwelcomeEntryCreateRequest NewEntry() => new()
    {
        IncidentCategory = FaceIncidentCategory.ViolenceOrThreat,
        EvidenceRef = "EVID-2026-0001",
        StoreScope = ["1013"],
        ExpiresAtUtc = _h.At(TimeSpan.FromDays(120)),
    };

    private async Task<FaceUnwelcomeEntryDto> SubmitAsync(
        int photos = 1, byte category = FaceIncidentCategory.ViolenceOrThreat, string? note = null, int expiresInDays = 120, FaceMatchActor? by = null) =>
        await _h.Unwelcome.CreateEntryAsync(
            NewEntry() with { IncidentCategory = category, CategoryNote = note, ExpiresAtUtc = _h.At(TimeSpan.FromDays(expiresInDays)) },
            _h.PhotoSet(photos), by ?? Submitter);

    private void EnableUnwelcome(string storeCode) =>
        _h.Db.Insertable(new FaceStoreProfile { StoreCode = storeCode, Mode = FaceStoreMode.Capture, UnwelcomeListEnabled = true, UpdatedAtUtc = FaceMatchTestDatabase.Now }).ExecuteCommand();

    private async Task<FaceUnwelcomeEntry> Load(string entryId) =>
        await _h.Db.Queryable<FaceUnwelcomeEntry>().SingleAsync(e => e.EntryId == entryId);

    /// <summary>直接写库种一个已生效、带两张参考照片的条目（跳过批准流程，复核 / 看图用例专注各自逻辑）。</summary>
    private async Task<string> SeedActiveEntryAsync()
    {
        var id = FaceMatchRules.NewId();
        var keys = new[] { FaceMatchObjectKeys.Unwelcome(id, 0, "jpg"), FaceMatchObjectKeys.Unwelcome(id, 1, "png") };
        _h.Store.Objects[keys[0]] = FaceImageTestData.Jpeg;
        _h.Store.Objects[keys[1]] = FaceImageTestData.Png;
        await _h.Db.Insertable(new FaceUnwelcomeEntry
        {
            EntryId = id,
            TencentPersonId = $"uw_{id}",
            IncidentCategory = FaceIncidentCategory.ViolenceOrThreat,
            EvidenceRef = "EVID-SEED",
            StoreScope = "1013",
            SubmittedBy = "sub-1",
            SubmittedAtUtc = FaceMatchTestDatabase.Now.AddDays(-5),
            ApprovedBy = "app-1",
            ApprovedAtUtc = FaceMatchTestDatabase.Now.AddDays(-4),
            ReviewDueAtUtc = FaceMatchTestDatabase.Now.AddDays(60),
            ExpiresAtUtc = FaceMatchTestDatabase.Now.AddDays(90),
            Status = FaceUnwelcomeEntryStatus.Active,
            PhotoObjectKeysJson = FaceMatchRules.SerializeKeys(keys),
        }).ExecuteCommandAsync();
        return id;
    }

    private async Task<string> SeedTaskAsync(
        string entryId, decimal score = 91.5m, string? imageKey = HitKey, string storeCode = "1013", byte status = FaceMatchReviewStatus.Pending)
    {
        var id = FaceMatchRules.NewId();
        if (!string.IsNullOrEmpty(imageKey) && imageKey.StartsWith("face-match/hit/") && !_h.Store.Objects.ContainsKey(imageKey))
        {
            _h.Store.Objects[imageKey] = FaceImageTestData.Jpeg;
        }

        await _h.Db.Insertable(new FaceMatchTask
        {
            TaskId = id,
            StoreCode = storeCode,
            CameraId = "cam-1",
            CapturedAtUtc = FaceMatchTestDatabase.Now.AddMinutes(-10),
            EntryId = entryId,
            Score = score,
            ImageObjectKey = imageKey ?? string.Empty,
            ReviewStatus = status,
            FirstReviewerId = status == FaceMatchReviewStatus.Pending ? null : "rev-a",
            ImageDeleteAtUtc = FaceMatchTestDatabase.Now.AddDays(7),
        }).ExecuteCommandAsync();
        return id;
    }
}
