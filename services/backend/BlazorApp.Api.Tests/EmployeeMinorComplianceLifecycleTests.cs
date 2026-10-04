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
    public EmployeeMinorComplianceLifecycleTests()
    {
        _db = new(new ConnectionConfig { ConnectionString = $"Data Source={_path}", DbType = DbType.Sqlite, IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute });
        _db.CodeFirst.InitTables(typeof(EmployeeMinorCompliance), typeof(EmployeeMinorComplianceContact), typeof(EmployeeMinorComplianceAudit), typeof(EmployeeProfile), typeof(UserStore), typeof(Store));
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
        var token = invite.Data!.SigningUrl.Split("#token=")[1];
        Assert.False((await service.SignGuardianAsync(token, SignDto(1))).Success); // 签署人不能改成另一个姓名。
        var signature = SignDto(1); signature.SignedName = saved.Data.GuardianName;
        var signedResult = await service.SignGuardianAsync(token, signature);
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
        Assert.False((await service.GetGuardianAsync(token)).Success);
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
        var sign = await service.SignGuardianAsync("token", SignDto(1));
        Assert.True(sign.Success);
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
        var result = await Service("employee", store).SignGuardianAsync("raced-token", SignDto(1));
        Assert.False(result.Success);
        Assert.Equal(0, store.Count);
        Assert.Null((await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id)).DocumentObjectKey);
    }

    [Fact]
    public async Task MissingDocumentStoreDoesNotMarkSigned()
    {
        var row = await SeedAsync("employee", 1, "no-store");
        var result = await Service("employee", null).SignGuardianAsync("no-store", SignDto(1));
        Assert.False(result.Success);
        Assert.Equal("awaiting_guardian_signature", (await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id)).Status);
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
    private EmployeeMinorComplianceService Service(string actor, IEmployeeMinorDocumentStore? storage)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, _db);
        var current = Mock.Of<ICurrentUserService>(x => x.GetCurrentUserGuid() == actor && x.GetCurrentUsername() == actor);
        var scope = new Mock<ICurrentUserManageableStoreScopeService>();
        scope.Setup(x => x.GetScopeAsync()).ReturnsAsync(new CurrentUserManageableStoreScope { IsAllowed = true, IsAdmin = true, IsAuthenticated = true });
        return new(context, current, NullLogger<EmployeeMinorComplianceService>.Instance, scope.Object, storage);
    }
    private async Task<EmployeeMinorCompliance> SeedAsync(string user, int version, string token)
    {
        var row = new EmployeeMinorCompliance { UserGUID = user, Version = version, Revision = 1, Status = "awaiting_guardian_signature", GuardianName = "Parent", GuardianPhone = "0400 111 222", GuardianEmail = "parent@example.test", GuardianTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(), GuardianTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1), FormDataJson = JsonSerializer.Serialize(new EmployeeMinorCe1FormDto { ChildGivenName = "Child", ChildFamilyName = "Person", EmployerCompanyName = "HB Retail" }), SchoolCalendarJson = JsonSerializer.Serialize(new EmployeeMinorSchoolCalendarDto { SchoolProvider = "Example School" }) };
        row.Id = await _db.Insertable(row).ExecuteReturnIdentityAsync();
        await _db.Insertable(new EmployeeMinorComplianceContact { ComplianceId = row.Id, FullName = "Aunt", Phone = "0400 333 444", Email = "aunt@example.test" }).ExecuteCommandAsync();
        return row;
    }
    public void Dispose() { _db.Dispose(); File.Delete(_path); }

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
