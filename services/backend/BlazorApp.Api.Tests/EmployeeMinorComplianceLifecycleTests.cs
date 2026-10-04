using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class EmployeeMinorComplianceLifecycleTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"minor-lifecycle-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly FakeGuardianEmail _mail = new();
    public EmployeeMinorComplianceLifecycleTests()
    {
        _db = new(new ConnectionConfig { ConnectionString = $"Data Source={_path}", DbType = DbType.Sqlite, IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute });
        _db.CodeFirst.InitTables(typeof(EmployeeMinorCompliance), typeof(EmployeeMinorComplianceContact), typeof(EmployeeMinorComplianceAudit), typeof(EmployeeMinorComplianceRequest), typeof(EmployeeProfile), typeof(UserStore), typeof(Store), typeof(User));
    }

    [Fact]
    public async Task IncompleteDraftCanSave_StaleRevisionCannotOverwrite_ContactRequirementsApplyAtInvite()
    {
        await SeedEmploymentAsync();
        var service = Service("employee", new MemoryStore());
        var first = await service.UpsertSelfAsync(new() { EducationExemptionVerified = true });
        Assert.True(first.Success, first.Message);
        Assert.Equal(1, first.Data!.Revision);
        Assert.False(first.Data.EducationExemptionVerified);
        Assert.False((await service.InviteGuardianAsync(new() { Version = 1, Revision = 1 })).Success);
        var draft = Draft();
        draft.ExpectedRevision = 1;
        Assert.False((await service.UpsertSelfAsync(draft)).Success);
        draft.ExpectedVersion = 1; draft.ExpectedRevision = 1;
        var second = await service.UpsertSelfAsync(draft);
        Assert.True(second.Success, second.Message);
        Assert.Equal(1, second.Data!.Version);
        Assert.Equal(2, second.Data.Revision);
        Assert.False((await service.UpsertSelfAsync(draft)).Success);
        Assert.False((await service.InviteGuardianAsync(new() { Version = 1, Revision = 1 })).Success);
        Assert.True((await service.InviteGuardianAsync(new() { Version = 1, Revision = 2 })).Success);
    }

    [Fact]
    public async Task SavedInvitedSignedSubmittedReturnedAndEditedVersionKeepsOriginalDocumentImmutable()
    {
        await SeedEmploymentAsync();
        var storage = new MemoryStore();
        var service = Service("employee", storage);
        var saved = await service.UpsertSelfAsync(Draft());
        Assert.True(saved.Success, saved.Message);
        var invite = await service.InviteGuardianAsync(new() { Version = saved.Data!.Version, Revision = saved.Data.Revision });
        Assert.True(invite.Success, invite.Message);
        Assert.True(invite.Data!.EmailSent);
        var token = _mail.LastSigningUrl!.Split("#token=")[1];
        var session = await VerifyAsync(service, token);
        Assert.False((await service.SignGuardianAsync(token, SignDto(1), session)).Success); // 签署人不能改成另一个姓名。
        var signature = SignDto(1); signature.SignedName = saved.Data.GuardianName;
        var signedResult = await service.SignGuardianAsync(token, signature, session);
        Assert.True(signedResult.Success, signedResult.Message);
        Assert.True((await service.SubmitAsync(1)).Success);
        Assert.True((await service.ReviewAsync(saved.Data.Id, new() { Version = 1, Comment = "Confirm school dates", ReturnFields = ["schoolCalendar"] }, false)).Success);
        var signed = await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(saved.Data.Id);
        Assert.False((await service.InviteGuardianAsync(new() { Version = signed.Version, Revision = signed.Revision })).Success);
        Assert.Equal(signed.DocumentObjectKey, (await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(signed.Id)).DocumentObjectKey);
        var edited = Draft(); edited.ExpectedVersion = 1; edited.ExpectedRevision = signed.Revision;
        edited.GuardianPhone = "07 3111 2222";
        var next = await service.UpsertSelfAsync(edited);
        Assert.True(next.Success, next.Message);
        Assert.Equal(2, next.Data!.Version);
        Assert.Equal("draft", next.Data.Status);
        Assert.Null(next.Data.GuardianSignedAtUtc);
        var original = await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(signed.Id);
        Assert.Equal(signed.DocumentSha256, original.DocumentSha256);
        Assert.Equal(signed.GuardianPhone, original.GuardianPhone);
        Assert.Equal("returned", original.Status);
        Assert.False((await service.GetGuardianAsync(token, session)).Success);
        Assert.Equal(2, (await service.GetSelfHistoryAsync()).Data!.Count);
        Assert.NotEmpty((await service.DownloadDocumentAsync(signed.Id)).Data!);
        await storage.SaveAsync(signed.DocumentObjectKey!, Encoding.UTF8.GetBytes("replaced-object"));
        Assert.False((await service.DownloadDocumentAsync(signed.Id)).Success);
    }

    [Theory]
    [InlineData("", "parent@example.test")]
    [InlineData("()-- ++", "parent@example.test")]
    [InlineData("07 3000 1111", "")]
    [InlineData("07 3000 1111", "invalid")]
    public async Task ParentPhoneAndEmailAreRequiredBeforeInvitation(string phone, string email)
    {
        await SeedEmploymentAsync();
        var service = Service("employee", new MemoryStore());
        var draft = Draft(); draft.GuardianPhone = phone; draft.GuardianEmail = email;
        var saved = await service.UpsertSelfAsync(draft);
        Assert.True(saved.Success, saved.Message);
        Assert.False((await service.InviteGuardianAsync(new() { Version = saved.Data!.Version, Revision = saved.Data.Revision })).Success);
    }

    [Fact]
    public async Task BackupMobileCannotDuplicateParentPhone()
    {
        await SeedEmploymentAsync();
        var service = Service("employee", new MemoryStore());
        var draft = Draft(); draft.Contacts[0].Mobile = draft.GuardianPhone;
        var saved = await service.UpsertSelfAsync(draft);
        Assert.True(saved.Success);
        Assert.False((await service.InviteGuardianAsync(new() { Version = saved.Data!.Version, Revision = saved.Data.Revision })).Success);
    }

    [Fact]
    public async Task GuardianEmployeeHrClosedLoopPersistsDocumentAndDecision()
    {
        var row = await SeedAsync("employee", 1, "token");
        var storage = new MemoryStore();
        var service = Service("employee", storage);
        var sign = await service.SignGuardianAsync("token", SignDto(1), await VerifyAsync(service, "token"));
        Assert.True(sign.Success, sign.Message);
        Assert.Equal("signed_pending_employee_submit", (await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id)).Status);
        Assert.True((await service.SubmitAsync(1)).Success);
        Assert.True((await service.ReviewAsync(row.Id, new() { Version = 1 }, true)).Success);
        Assert.True((await service.GetDocumentAsync(row.Id)).Success);
        Assert.Equal("approved", (await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id)).Status);
        Assert.NotEmpty((await storage.ReadAsync((await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id)).DocumentObjectKey!)).Data!);
    }

    [Fact]
    public async Task StaleTokenAndRevisionCannotChangeCurrentRecord()
    {
        var old = await SeedAsync("employee", 1, "old-token");
        await _db.Insertable(new EmployeeMinorCompliance { UserGUID = "employee", Version = 2, Revision = 1, Status = "draft", GuardianName = "Parent" }).ExecuteCommandAsync();
        var service = Service("employee", new MemoryStore());
        Assert.False((await service.GetGuardianAsync("old-token")).Success);
        Assert.False((await service.SignGuardianAsync("old-token", SignDto(1))).Success);
        Assert.Null((await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(old.Id)).GuardianSignedAtUtc);
    }

    [Fact]
    public async Task LosingSignatureRequestRemovesOnlyItsUnreferencedUpload()
    {
        var row = await SeedAsync("employee", 1, "raced-token");
        var store = new MemoryStore();
        store.AfterSave = async () => await _db.Updateable<EmployeeMinorCompliance>()
            .SetColumns(x => x.GuardianTokenUsed == true).Where(x => x.Id == row.Id).ExecuteCommandAsync();
        var service = Service("employee", store);
        var result = await service.SignGuardianAsync("raced-token", SignDto(1), await VerifyAsync(service, "raced-token"));
        Assert.False(result.Success);
        Assert.Equal(0, store.Count);
        Assert.Null((await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id)).DocumentObjectKey);
    }

    [Fact]
    public async Task MissingDocumentStoreDoesNotMarkSigned()
    {
        var row = await SeedAsync("employee", 1, "no-store");
        var service = Service("employee", null);
        var result = await service.SignGuardianAsync("no-store", SignDto(1), await VerifyAsync(service, "no-store"));
        Assert.False(result.Success);
        Assert.Equal("DOCUMENT_STORAGE_UNAVAILABLE", result.ErrorCode);
        Assert.Equal("awaiting_guardian_signature", (await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id)).Status);
    }

    [Fact]
    public async Task 邀请默认直发监护人邮箱且不把链接回传员工_转发方式才返回链接()
    {
        await SeedEmploymentAsync();
        var service = Service("employee", new MemoryStore());
        var saved = await service.UpsertSelfAsync(Draft());
        var invite = await service.InviteGuardianAsync(new() { Version = saved.Data!.Version, Revision = saved.Data.Revision });
        Assert.True(invite.Success, invite.Message);
        Assert.True(invite.Data!.EmailSent);
        Assert.Equal("email", invite.Data.DeliveryChannel);
        Assert.Equal(string.Empty, invite.Data.SigningUrl);
        Assert.Equal(saved.Data.GuardianEmail, _mail.LastTo);
        Assert.StartsWith("https://hotbargain.vip/minor-employment/sign#token=", _mail.LastSigningUrl);
        Assert.NotNull((await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(saved.Data.Id)).GuardianInviteEmailSentAtUtc);

        // 改为转发：重新编辑生成新版本后，以 share 方式发起，只返回链接、不发邮件。
        var edited = Draft(); edited.ExpectedVersion = 1; edited.ExpectedRevision = (await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(saved.Data.Id)).Revision;
        var next = await service.UpsertSelfAsync(edited);
        var linksBefore = _mail.LinksSent;
        var share = await service.InviteGuardianAsync(new() { Version = next.Data!.Version, Revision = next.Data.Revision, DeliverByEmail = false });
        Assert.True(share.Success, share.Message);
        Assert.False(share.Data!.EmailSent);
        Assert.Contains("#token=", share.Data.SigningUrl);
        Assert.Equal(linksBefore, _mail.LinksSent);
    }

    [Fact]
    public async Task 邮件发送失败时邀请仍有效并回传链接供转发()
    {
        await SeedEmploymentAsync();
        var failing = new FakeGuardianEmail { Fail = true };
        var service = Service("employee", new MemoryStore(), mail: failing);
        var saved = await service.UpsertSelfAsync(Draft());
        var invite = await service.InviteGuardianAsync(new() { Version = saved.Data!.Version, Revision = saved.Data.Revision });
        Assert.True(invite.Success, invite.Message);
        Assert.False(invite.Data!.EmailSent);
        Assert.NotNull(invite.Data.EmailError);
        Assert.Contains("#token=", invite.Data.SigningUrl);
        Assert.Equal("awaiting_guardian_signature", (await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(saved.Data.Id)).Status);
    }

    [Fact]
    public async Task 未通过邮箱验证不能查看或签署_会话只返回打码邮箱()
    {
        var row = await SeedAsync("employee", 1, "fresh-token");
        var service = Service("employee", new MemoryStore());
        var session = await service.GetGuardianSessionAsync("fresh-token");
        Assert.True(session.Success, session.Message);
        Assert.False(session.Data!.EmailVerified);
        Assert.Equal("p***@example.test", session.Data.MaskedGuardianEmail);
        var preview = await service.GetGuardianAsync("fresh-token");
        Assert.Equal(EmployeeMinorComplianceService.GuardianNotVerifiedCode, preview.ErrorCode);
        var sign = await service.SignGuardianAsync("fresh-token", SignDto(1), "forged-session");
        Assert.Equal(EmployeeMinorComplianceService.GuardianNotVerifiedCode, sign.ErrorCode);
        Assert.Equal("awaiting_guardian_signature", (await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id)).Status);
    }

    [Fact]
    public async Task 验证码错五次作废_冷却期内不能重发_通过后同码不能再用()
    {
        var row = await SeedAsync("employee", 1, "otp-token");
        var service = Service("employee", new MemoryStore());
        Assert.True((await service.SendGuardianCodeAsync("otp-token")).Success);
        Assert.Equal("GUARDIAN_CODE_COOLDOWN", (await service.SendGuardianCodeAsync("otp-token")).ErrorCode);
        var correct = _mail.LastCode!;
        var wrong = correct == "000000" ? "111111" : "000000";
        for (var i = 0; i < 4; i++) Assert.Equal("GUARDIAN_CODE_INVALID", (await service.VerifyGuardianCodeAsync("otp-token", wrong)).ErrorCode);
        Assert.Equal("GUARDIAN_CODE_LOCKED", (await service.VerifyGuardianCodeAsync("otp-token", wrong)).ErrorCode);
        // 锁定后正确的码也不再接受，必须重发。
        Assert.Equal("GUARDIAN_CODE_LOCKED", (await service.VerifyGuardianCodeAsync("otp-token", correct)).ErrorCode);

        await _db.Updateable<EmployeeMinorCompliance>().SetColumns(x => x.GuardianOtpSentAtUtc == DateTime.UtcNow.AddMinutes(-2)).Where(x => x.Id == row.Id).ExecuteCommandAsync();
        Assert.True((await service.SendGuardianCodeAsync("otp-token")).Success);
        var fresh = _mail.LastCode!;
        var ok = await service.VerifyGuardianCodeAsync("otp-token", fresh);
        Assert.True(ok.Success, ok.Message);
        Assert.Equal("GUARDIAN_CODE_EXPIRED", (await service.VerifyGuardianCodeAsync("otp-token", fresh)).ErrorCode);
        Assert.True((await service.GetGuardianAsync("otp-token", ok.Data)).Success);
        Assert.False((await service.GetGuardianAsync("otp-token", ok.Data + "x")).Success);
        var stored = await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id);
        Assert.NotNull(stored.GuardianEmailVerifiedAtUtc);
        Assert.NotEqual(fresh, stored.GuardianSessionHash);
    }

    [Fact]
    public async Task 同一链接最多发送五次验证码()
    {
        var row = await SeedAsync("employee", 1, "limit-token");
        var service = Service("employee", new MemoryStore());
        for (var i = 0; i < EmployeeMinorComplianceService.GuardianCodeMaxSends; i++)
        {
            await _db.Updateable<EmployeeMinorCompliance>().SetColumns(x => x.GuardianOtpSentAtUtc == DateTime.UtcNow.AddMinutes(-5)).Where(x => x.Id == row.Id).ExecuteCommandAsync();
            Assert.True((await service.SendGuardianCodeAsync("limit-token")).Success);
        }
        await _db.Updateable<EmployeeMinorCompliance>().SetColumns(x => x.GuardianOtpSentAtUtc == DateTime.UtcNow.AddMinutes(-5)).Where(x => x.Id == row.Id).ExecuteCommandAsync();
        Assert.Equal("GUARDIAN_CODE_SEND_LIMIT", (await service.SendGuardianCodeAsync("limit-token")).ErrorCode);
        Assert.Equal(EmployeeMinorComplianceService.GuardianCodeMaxSends, _mail.CodesSent);
    }

    [Fact]
    public async Task 签署审计记录邮箱核验时间与来源()
    {
        var row = await SeedAsync("employee", 1, "audit-token");
        var service = Service("employee", new MemoryStore());
        var session = await VerifyAsync(service, "audit-token");
        var sign = await service.SignGuardianAsync("audit-token", SignDto(1), session, "203.0.113.9", "Mozilla/5.0 Test");
        Assert.True(sign.Success, sign.Message);
        var audit = await _db.Queryable<EmployeeMinorComplianceAudit>().Where(x => x.ComplianceId == row.Id && x.Action == "guardian_signed").FirstAsync();
        Assert.Contains("203.0.113.9", audit.MetadataJson);
        Assert.Contains("emailVerifiedAtUtc", audit.MetadataJson);
        Assert.Contains("p***@example.test", audit.MetadataJson);
        var stored = await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id);
        Assert.Null(stored.GuardianSessionHash);
        Assert.NotNull(stored.GuardianEmailVerifiedAtUtc);
    }

    [Fact]
    public async Task 店长发起请求_只列可管理门店未成年员工_重复发起幂等_提交审核后自动完成()
    {
        await SeedEmploymentAsync();
        await _db.Insertable(new User { UserGUID = "employee", Username = "teen", FullName = "Teen Worker", IsActive = true }).ExecuteCommandAsync();
        await _db.Insertable(new User { UserGUID = "adult", Username = "adult", FullName = "Adult Worker", IsActive = true }).ExecuteCommandAsync();
        await _db.Insertable(new UserStore { UserGUID = "adult", StoreGUID = "store-01", IsPrimary = true }).ExecuteCommandAsync();
        await _db.Insertable(new EmployeeProfile { UserGUID = "adult", Birthday = new DateTime(1990, 1, 1) }).ExecuteCommandAsync();
        await _db.Insertable(new Store { StoreGUID = "store-02", StoreCode = "02", Address = "2 Other St, Sydney NSW 2000" }).ExecuteCommandAsync();
        await _db.Insertable(new User { UserGUID = "other-teen", Username = "other", FullName = "Other Teen", IsActive = true }).ExecuteCommandAsync();
        await _db.Insertable(new UserStore { UserGUID = "other-teen", StoreGUID = "store-02", IsPrimary = true }).ExecuteCommandAsync();
        await _db.Insertable(new EmployeeProfile { UserGUID = "other-teen", Birthday = new DateTime(2011, 6, 1) }).ExecuteCommandAsync();
        var managerScope = new CurrentUserManageableStoreScope { IsAllowed = true, IsAuthenticated = true, IsStoreManager = true, StoreGuids = ["store-01"], StoreCodes = ["01"] };
        var manager = Service("manager", new MemoryStore(), managerScope);

        var candidates = await manager.GetManagerCandidatesAsync(null);
        Assert.True(candidates.Success, candidates.Message);
        var only = Assert.Single(candidates.Data!);
        Assert.Equal("employee", only.UserGUID);
        Assert.Equal("Teen Worker", only.EmployeeName);
        Assert.Equal("FORBIDDEN", (await manager.GetManagerCandidatesAsync("02")).ErrorCode);
        Assert.Equal("FORBIDDEN", (await manager.CreateRequestAsync(new() { UserGUID = "other-teen" })).ErrorCode);
        Assert.Equal("NOT_MINOR", (await manager.CreateRequestAsync(new() { UserGUID = "adult" })).ErrorCode);

        var created = await manager.CreateRequestAsync(new() { UserGUID = "employee", Note = "本周内填好" });
        Assert.True(created.Success, created.Message);
        var again = await manager.CreateRequestAsync(new() { UserGUID = "employee" });
        Assert.Equal(created.Data!.Id, again.Data!.Id);
        Assert.Equal(1, await _db.Queryable<EmployeeMinorComplianceRequest>().CountAsync());
        Assert.NotNull((await manager.GetManagerCandidatesAsync(null)).Data!.Single().OpenRequest);

        var employee = Service("employee", new MemoryStore());
        var mine = await employee.GetSelfRequestsAsync();
        Assert.Equal("本周内填好", Assert.Single(mine.Data!).Note);
        var saved = await employee.UpsertSelfAsync(Draft());
        await employee.InviteGuardianAsync(new() { Version = saved.Data!.Version, Revision = saved.Data.Revision });
        var token = _mail.LastSigningUrl!.Split("#token=")[1];
        var sign = SignDto(1); sign.SignedName = saved.Data.GuardianName;
        Assert.True((await employee.SignGuardianAsync(token, sign, await VerifyAsync(employee, token))).Success);
        Assert.True((await employee.SubmitAsync(1)).Success);
        var closed = await _db.Queryable<EmployeeMinorComplianceRequest>().InSingleAsync(created.Data.Id);
        Assert.Equal(EmployeeMinorComplianceRequest.StatusCompleted, closed.Status);
        Assert.Equal(saved.Data.Id, closed.CompletedComplianceId);
        Assert.Empty((await employee.GetSelfRequestsAsync()).Data!);
    }

    [Fact]
    public async Task 店长可撤销未完成请求_撤销后不能重复撤销()
    {
        await SeedEmploymentAsync();
        await _db.Insertable(new User { UserGUID = "employee", Username = "teen", IsActive = true }).ExecuteCommandAsync();
        var managerScope = new CurrentUserManageableStoreScope { IsAllowed = true, IsAuthenticated = true, StoreGuids = ["store-01"], StoreCodes = ["01"] };
        var manager = Service("manager", new MemoryStore(), managerScope);
        var created = await manager.CreateRequestAsync(new() { UserGUID = "employee" });
        Assert.True((await manager.CancelRequestAsync(created.Data!.Id)).Success);
        Assert.Equal("REQUEST_CLOSED", (await manager.CancelRequestAsync(created.Data.Id)).ErrorCode);
        // 撤销后可以重新发起。
        Assert.True((await manager.CreateRequestAsync(new() { UserGUID = "employee" })).Success);
        var outsider = Service("outsider", new MemoryStore(), new CurrentUserManageableStoreScope { IsAllowed = true, IsAuthenticated = true, StoreGuids = ["store-99"], StoreCodes = ["99"] });
        var open = await _db.Queryable<EmployeeMinorComplianceRequest>().Where(x => x.Status == "open").FirstAsync();
        Assert.Equal("FORBIDDEN", (await outsider.CancelRequestAsync(open.Id)).ErrorCode);
    }

    [Fact]
    public async Task 监护人签署前现场修改_写回同一版本并留痕_原件按修改后内容生成()
    {
        var row = await SeedAsync("employee", 1, "amend-token");
        var otherWork = JsonSerializer.Serialize(new EmployeeMinorOtherWorkDto { HasOtherWork = true, PlannedIntervals = [new() { StartUtc = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), EndUtc = new DateTime(2026, 10, 3, 3, 0, 0, DateTimeKind.Utc), Hours = 3 }] }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await _db.Updateable<EmployeeMinorCompliance>().SetColumns(x => x.OtherWorkJson == otherWork).Where(x => x.Id == row.Id).ExecuteCommandAsync();
        var storage = new MemoryStore();
        var service = Service("employee", storage);
        var session = await VerifyAsync(service, "amend-token");
        var sign = SignDto(1);
        sign.Amendments = new()
        {
            GuardianPhone = "0499 888 777",
            SchoolCalendar = new() { SchoolProvider = "Fixed High School", WeeklySchedule = [new() { DayOfWeek = DayOfWeek.Monday, MustAttend = true, StartLocalTime = "08:30", EndLocalTime = "15:10" }] },
            OtherWork = new() { HasOtherWork = false },
            Contacts = [new() { FullName = "Uncle Sam", Phone = "0400 555 666", Relationship = "Uncle" }],
        };
        var result = await service.SignGuardianAsync("amend-token", sign, session);
        Assert.True(result.Success, result.Message);

        var stored = await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id);
        Assert.Equal("0499 888 777", stored.GuardianPhone);
        Assert.Equal(2, stored.Revision);
        Assert.Contains("Fixed High School", stored.SchoolCalendarJson);
        // 计划工时区间不归监护人改，原样保留。
        Assert.Contains("2026-10-03", stored.OtherWorkJson);
        Assert.Equal(["guardianDetails", "schoolCalendar", "otherWork", "backupContact"], result.Data!.GuardianAmendedFields);
        Assert.Equal("Uncle Sam", Assert.Single(await _db.Queryable<EmployeeMinorComplianceContact>().Where(x => x.ComplianceId == row.Id).ToListAsync()).FullName);
        var audit = await _db.Queryable<EmployeeMinorComplianceAudit>().Where(x => x.ComplianceId == row.Id && x.Action == "guardian_amended").FirstAsync();
        Assert.Contains("0400 111 222", audit.MetadataJson); // 修改前的电话
        Assert.Contains("0499 888 777", audit.MetadataJson); // 修改后的电话
        Assert.Contains("Aunt", audit.MetadataJson);         // 修改前的备用联系人
        using var pdf = UglyToad.PdfPig.PdfDocument.Open((await storage.ReadAsync(stored.DocumentObjectKey!)).Data!);
        var text = string.Join("\n", pdf.GetPages().Select(x => x.Text));
        Assert.Contains("Uncle Sam", text);
        Assert.Contains("Fixed High School", text);
        Assert.DoesNotContain("Aunt", text);
    }

    [Fact]
    public async Task 监护人改了自己姓名后须用新姓名签署()
    {
        await SeedAsync("employee", 1, "rename-token");
        var service = Service("employee", new MemoryStore());
        var session = await VerifyAsync(service, "rename-token");
        var oldName = SignDto(1);
        oldName.Amendments = new() { GuardianName = "Parent Corrected" };
        Assert.Equal("SIGNER_NAME_MISMATCH", (await service.SignGuardianAsync("rename-token", oldName, session)).ErrorCode);
        var newName = SignDto(1); newName.SignedName = "Parent Corrected";
        newName.Amendments = new() { GuardianName = "Parent Corrected" };
        var signed = await service.SignGuardianAsync("rename-token", newName, session);
        Assert.True(signed.Success, signed.Message);
        Assert.Equal("Parent Corrected", signed.Data!.GuardianName);
    }

    [Fact]
    public async Task 修改后校验不过则整单不写入()
    {
        var row = await SeedAsync("employee", 1, "invalid-token");
        var service = Service("employee", new MemoryStore());
        var session = await VerifyAsync(service, "invalid-token");
        // 备用联系人与监护人同一个号码。
        var sameNumber = SignDto(1);
        sameNumber.Amendments = new() { Contacts = [new() { FullName = "Someone", Phone = "0400 111 222" }] };
        Assert.Equal("MINOR_COMPLIANCE_BACKUP_CONTACT_REQUIRED", (await service.SignGuardianAsync("invalid-token", sameNumber, session)).ErrorCode);
        // 上学时间格式错误。
        var badTime = SignDto(1);
        badTime.Amendments = new() { SchoolCalendar = new() { SchoolProvider = "Example School", WeeklySchedule = [new() { DayOfWeek = DayOfWeek.Monday, MustAttend = true, StartLocalTime = "15:00", EndLocalTime = "09:00" }] } };
        Assert.Equal("MINOR_COMPLIANCE_SCHOOL_CALENDAR_INVALID", (await service.SignGuardianAsync("invalid-token", badTime, session)).ErrorCode);
        // 清空孩子姓名。
        var noName = SignDto(1);
        noName.Amendments = new() { ChildGivenName = " " };
        Assert.Equal("MINOR_COMPLIANCE_FORM_INCOMPLETE", (await service.SignGuardianAsync("invalid-token", noName, session)).ErrorCode);
        var stored = await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id);
        Assert.Equal("awaiting_guardian_signature", stored.Status);
        Assert.Equal(1, stored.Revision);
        Assert.Null(stored.GuardianAmendedFieldsJson);
        Assert.Equal("Aunt", (await _db.Queryable<EmployeeMinorComplianceContact>().Where(x => x.ComplianceId == row.Id).FirstAsync()).FullName);
    }

    [Fact]
    public async Task 原样提交不算修改()
    {
        var row = await SeedAsync("employee", 1, "same-token");
        var service = Service("employee", new MemoryStore());
        var session = await VerifyAsync(service, "same-token");
        var sign = SignDto(1);
        sign.Amendments = new()
        {
            GuardianName = " Parent ", GuardianPhone = "0400 111 222", ChildGivenName = "Child", ChildFamilyName = "Person",
            Contacts = [new() { FullName = "Aunt", Phone = "0400 333 444", Email = "aunt@example.test" }],
        };
        var result = await service.SignGuardianAsync("same-token", sign, session);
        Assert.True(result.Success, result.Message);
        Assert.Empty(result.Data!.GuardianAmendedFields);
        Assert.Equal(1, (await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id)).Revision);
        Assert.False(await _db.Queryable<EmployeeMinorComplianceAudit>().AnyAsync(x => x.ComplianceId == row.Id && x.Action == "guardian_amended"));
    }

    private EmployeeMinorComplianceGuardianSignDto SignDto(int version) => new() { Version = version, SignedName = "Parent", SignatureData = EmployeeMinorComplianceDocumentTests.Signature(), ConfirmRelationship = true, ConfirmConsent = true, ConfirmBackupContact = true, ConsentScope = EmployeeMinorComplianceService.ServerConsentScope };
    private static EmployeeMinorComplianceUpsertDto Draft()
    {
        var source = EmployeeMinorComplianceDocumentTests.Profile();
        return new() { FormData = source.FormData!, SchoolCalendar = source.SchoolCalendar, OtherWork = source.OtherWork,
            Commute = source.Commute, GuardianName = source.GuardianName, GuardianPhone = source.GuardianPhone,
            GuardianEmail = source.GuardianEmail, GuardianRelationship = source.GuardianRelationship, Contacts = source.Contacts.Take(1).ToList() };
    }
    private async Task SeedEmploymentAsync()
    {
        await _db.Insertable(new Store { StoreGUID = "store-01", StoreCode = "01", Address = "1 Example St, Brisbane QLD 4000" }).ExecuteCommandAsync();
        await _db.Insertable(new UserStore { UserGUID = "employee", StoreGUID = "store-01", IsPrimary = true }).ExecuteCommandAsync();
        await _db.Insertable(new EmployeeProfile { UserGUID = "employee", Birthday = new DateTime(2012, 1, 1) }).ExecuteCommandAsync();
    }
    private EmployeeMinorComplianceService Service(string actor, IEmployeeMinorDocumentStore? storage, CurrentUserManageableStoreScope? scopeValue = null, IMinorGuardianEmailSender? mail = null)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, _db);
        var current = Mock.Of<ICurrentUserService>(x => x.GetCurrentUserGuid() == actor && x.GetCurrentUsername() == actor);
        var scope = new Mock<ICurrentUserManageableStoreScopeService>();
        scope.Setup(x => x.GetScopeAsync()).ReturnsAsync(scopeValue ?? new CurrentUserManageableStoreScope { IsAllowed = true, IsAdmin = true, IsAuthenticated = true });
        return new(context, current, NullLogger<EmployeeMinorComplianceService>.Instance, scope.Object, storage, mail ?? _mail);
    }

    /// <summary>走完监护人邮箱验证：发验证码 → 从假邮箱取码 → 校验，返回会话密钥。</summary>
    private async Task<string> VerifyAsync(EmployeeMinorComplianceService service, string token)
    {
        var sent = await service.SendGuardianCodeAsync(token);
        Assert.True(sent.Success, sent.Message);
        var verified = await service.VerifyGuardianCodeAsync(token, _mail.LastCode!);
        Assert.True(verified.Success, verified.Message);
        return verified.Data!;
    }
    private async Task<EmployeeMinorCompliance> SeedAsync(string user, int version, string token)
    {
        var row = new EmployeeMinorCompliance { UserGUID = user, Version = version, Revision = 1, Status = "awaiting_guardian_signature", GuardianName = "Parent", GuardianPhone = "0400 111 222", GuardianEmail = "parent@example.test", GuardianTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(), GuardianTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1), FormDataJson = JsonSerializer.Serialize(new EmployeeMinorCe1FormDto { ChildGivenName = "Child", ChildFamilyName = "Person", EmployerCompanyName = "HB Retail" }), SchoolCalendarJson = JsonSerializer.Serialize(new EmployeeMinorSchoolCalendarDto { SchoolProvider = "Example School" }) };
        row.Id = await _db.Insertable(row).ExecuteReturnIdentityAsync();
        await _db.Insertable(new EmployeeMinorComplianceContact { ComplianceId = row.Id, FullName = "Aunt", Phone = "0400 333 444", Email = "aunt@example.test" }).ExecuteCommandAsync();
        return row;
    }
    public void Dispose() { _db.Dispose(); File.Delete(_path); }

    private sealed class FakeGuardianEmail : IMinorGuardianEmailSender
    {
        public bool Fail { get; set; }
        public string? LastTo { get; private set; }
        public string? LastSigningUrl { get; private set; }
        public string? LastCode { get; private set; }
        public int CodesSent { get; private set; }
        public int LinksSent { get; private set; }
        public Task<ApiResponse<bool>> SendSigningLinkAsync(string toEmail, string guardianName, string childName, string signingUrl, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
        {
            if (Fail) return Task.FromResult(ApiResponse<bool>.Error("smtp down", "GUARDIAN_EMAIL_SEND_FAILED"));
            LastTo = toEmail; LastSigningUrl = signingUrl; LinksSent++;
            return Task.FromResult(ApiResponse<bool>.OK(true));
        }
        public Task<ApiResponse<bool>> SendVerificationCodeAsync(string toEmail, string guardianName, string code, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
        {
            if (Fail) return Task.FromResult(ApiResponse<bool>.Error("smtp down", "GUARDIAN_EMAIL_SEND_FAILED"));
            LastTo = toEmail; LastCode = code; CodesSent++;
            return Task.FromResult(ApiResponse<bool>.OK(true));
        }
    }

    private sealed class MemoryStore : IEmployeeMinorDocumentStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public Func<Task>? AfterSave { get; set; }
        public int Count => _items.Count;
        public async Task<ApiResponse<string>> SaveAsync(string objectKey, byte[] bytes) { _items[objectKey] = bytes; if (AfterSave != null) await AfterSave(); return ApiResponse<string>.OK(objectKey); }
        public Task<ApiResponse<byte[]>> ReadAsync(string objectKey) => Task.FromResult(_items.TryGetValue(objectKey, out var bytes) ? ApiResponse<byte[]>.OK(bytes) : ApiResponse<byte[]>.Error("missing"));
        public Task<ApiResponse<bool>> DeleteAsync(string objectKey) => Task.FromResult(ApiResponse<bool>.OK(_items.Remove(objectKey)));
    }
}
