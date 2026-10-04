using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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

/// <summary>使用真实 SQLite 状态验证对象访问、签署生命周期与只读旧版本，避免只检查 API 声明。</summary>
public sealed class EmployeeMinorComplianceSecurityTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"minor-security-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    public EmployeeMinorComplianceSecurityTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig { ConnectionString = $"Data Source={_path}", DbType = DbType.Sqlite, IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute });
        _db.CodeFirst.InitTables(typeof(EmployeeMinorCompliance), typeof(EmployeeMinorComplianceContact), typeof(EmployeeMinorComplianceAudit), typeof(User), typeof(Store), typeof(UserStore), typeof(EmployeeProfile));
    }

    [Fact]
    public async Task HrCannotReadReviewOrDownloadAnEmployeeOutsideTheirScope()
    {
        var row = await SeedRecord("employee-other", "store-other", "pending_hr_review");
        var service = Service("manager-local", allowed: true, storeGuids: ["store-local"]);
        Assert.False((await service.GetHrDetailAsync(row.Id)).Success);
        Assert.False((await service.ReviewAsync(row.Id, new() { Version = 1 }, true)).Success);
        Assert.False((await service.GetDocumentAsync(row.Id)).Success);
        Assert.Equal("pending_hr_review", (await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id)).Status);
        var list = await service.GetHrListAsync(null, null);
        Assert.True(list.Success);
        Assert.Empty(list.Data!.Items);
    }

    [Fact]
    public async Task OrdinaryEmployeeCannotUseHrServiceEvenIfTheyKnowTheirOwnId()
    {
        var row = await SeedRecord("employee-self", "store-local", "pending_hr_review");
        var service = Service("employee-self", allowed: false);
        Assert.False((await service.GetHrDetailAsync(row.Id)).Success);
        Assert.False((await service.ReviewAsync(row.Id, new() { Version = 1 }, true)).Success);
        Assert.False((await service.GetHrListAsync(null, null)).Success);
    }

    [Fact]
    public async Task GuardianTokenForOldVersionCannotBeUsedWhenANewerDraftExists()
    {
        const string token = "old-test-token-not-a-real-credential";
        var old = await SeedRecord("employee-self", "store-local", "awaiting_guardian_signature", token: token);
        await SeedRecord("employee-self", "store-local", "draft", version: 2);
        var service = Service("", allowed: false);
        // 旧版本链接在会话校验之前就被拒绝，连验证码都不能申请。
        Assert.Equal(EmployeeMinorComplianceService.VersionConflictCode, (await service.GetGuardianAsync(token)).ErrorCode);
        Assert.Equal(EmployeeMinorComplianceService.VersionConflictCode, (await service.SendGuardianCodeAsync(token)).ErrorCode);
        Assert.False((await service.SignGuardianAsync(token, new() { Version = 1, SignedName = "Parent", SignatureData = EmployeeMinorComplianceDocumentTests.Signature(), ConfirmRelationship = true, ConfirmBackupContact = true, ConfirmConsent = true, ConsentScope = "Lawful employment only" })).Success);
        var saved = await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(old.Id);
        Assert.Null(saved.GuardianSignedAtUtc);
    }

    [Fact]
    public async Task UsedAndExpiredGuardianTokensDoNotDiscloseTheForm()
    {
        const string expired = "expired-test-token";
        const string used = "used-test-token";
        var first = await SeedRecord("first", "store-local", "awaiting_guardian_signature", token: expired);
        first.GuardianTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await _db.Updateable(first).ExecuteCommandAsync();
        var second = await SeedRecord("second", "store-local", "signed_pending_employee_submit", token: used);
        second.GuardianTokenUsed = true;
        await _db.Updateable(second).ExecuteCommandAsync();
        var service = Service("", allowed: false);
        Assert.Equal("GUARDIAN_TOKEN_INVALID", (await service.GetGuardianAsync(expired)).ErrorCode);
        Assert.Equal("GUARDIAN_TOKEN_INVALID", (await service.GetGuardianAsync(used)).ErrorCode);
        Assert.Equal("GUARDIAN_TOKEN_INVALID", (await service.GetGuardianSessionAsync(expired)).ErrorCode);
    }

    [Fact]
    public async Task ReturnRequiresFieldsAndComment_AndASecondReviewerCannotOverwriteDecision()
    {
        var row = await SeedRecord("employee-self", "store-local", "pending_hr_review");
        var service = Service("hr-admin", allowed: true, admin: true);
        Assert.False((await service.ReviewAsync(row.Id, new() { Version = 1, Comment = "Need details" }, false)).Success);
        Assert.False((await service.ReviewAsync(row.Id, new() { Version = 1, ReturnFields = ["guardianPhone"] }, false)).Success);
        Assert.True((await service.ReviewAsync(row.Id, new() { Version = 1, Comment = "Please correct the phone number", ReturnFields = ["guardianPhone"] }, false)).Success);
        Assert.False((await service.ReviewAsync(row.Id, new() { Version = 1 }, true)).Success);
        var saved = await _db.Queryable<EmployeeMinorCompliance>().InSingleAsync(row.Id);
        Assert.Equal("returned", saved.Status);
        Assert.Equal("Please correct the phone number", saved.ReviewComment);
    }

    private EmployeeMinorComplianceService Service(string actor, bool allowed, bool admin = false, IReadOnlyList<string>? storeGuids = null)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, _db);
        var user = Mock.Of<ICurrentUserService>(x => x.GetCurrentUserGuid() == actor && x.GetCurrentUsername() == actor);
        var scope = new Mock<ICurrentUserManageableStoreScopeService>();
        scope.Setup(x => x.GetScopeAsync()).ReturnsAsync(new CurrentUserManageableStoreScope { IsAllowed = allowed, IsAuthenticated = actor.Length > 0, IsAdmin = admin, UserGuid = actor, StoreGuids = storeGuids ?? [] });
        return new(context, user, NullLogger<EmployeeMinorComplianceService>.Instance, scope.Object);
    }

    private async Task<EmployeeMinorCompliance> SeedRecord(string user, string store, string status, int version = 1, string? token = null)
    {
        var row = new EmployeeMinorCompliance { UserGUID = user, StoreGUID = store, Version = version, Status = status, GuardianName = "Parent", GuardianPhone = "07 3000 0000", GuardianEmail = "parent@example.test", GuardianTokenHash = token is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(), GuardianTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1) };
        row.Id = await _db.Insertable(row).ExecuteReturnIdentityAsync();
        await _db.Insertable(new EmployeeMinorComplianceContact { ComplianceId = row.Id, FullName = "Backup person", Phone = "0400 111 111" }).ExecuteCommandAsync();
        return row;
    }

    public void Dispose() { _db.Dispose(); File.Delete(_path); }
}
