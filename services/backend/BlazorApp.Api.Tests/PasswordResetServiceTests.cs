using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class PasswordResetServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly FakeEmailSender _sender = new();

    public PasswordResetServiceTests()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(User),
            typeof(RefreshToken),
            typeof(UserPasswordChangeRequirement),
            typeof(UserPasswordResetCode)
        );
    }

    [Fact]
    public async Task RequestSelfService_未知或不可送达邮箱_返回同一文案且不发信()
    {
        await SeedUserAsync("inactive", "inactive@example.com", isActive: false);
        await SeedUserAsync("internal", "internal@s001.store.local");
        var service = CreateService();

        var unknown = await service.RequestSelfServiceAsync("nobody@example.com", "1.1.1.1");
        var inactive = await service.RequestSelfServiceAsync("inactive@example.com", "1.1.1.1");
        var internalEmail = await service.RequestSelfServiceAsync("internal@s001.store.local", "1.1.1.1");

        Assert.True(unknown.Success);
        Assert.Equal(unknown.Message, inactive.Message);
        Assert.Equal(unknown.Message, internalEmail.Message);
        Assert.Empty(_sender.Sent);
        Assert.False(await _db.Queryable<UserPasswordResetCode>().AnyAsync());
    }

    [Fact]
    public async Task RequestSelfService_已开通账号_发送6位验证码且只存哈希_60秒内不重复发送()
    {
        await SeedUserAsync("staff", "Staff@Example.com");
        var service = CreateService();

        var first = await service.RequestSelfServiceAsync(" staff@example.com ", "1.1.1.1");
        var second = await service.RequestSelfServiceAsync("staff@example.com", "1.1.1.1");

        Assert.True(first.Success);
        Assert.True(second.Success);
        var sent = Assert.Single(_sender.Sent);
        Assert.Matches("^[0-9]{6}$", sent.Code);
        Assert.False(sent.Invite);
        var row = await _db.Queryable<UserPasswordResetCode>().SingleAsync();
        Assert.Equal(UserPasswordResetCode.PurposeReset, row.Purpose);
        Assert.NotEqual(sent.Code, row.CodeHash);
        Assert.Equal("self", row.RequestedBy);
        Assert.InRange((row.ExpiresAtUtc - row.CreatedAtUtc).TotalMinutes, 29, 31);
    }

    [Fact]
    public async Task Confirm_正确验证码_改密清标记吊销会话且验证码只能用一次()
    {
        var user = await SeedUserAsync("staff", "staff@example.com");
        await _db.Insertable(new UserPasswordChangeRequirement
        {
            UserGUID = user.UserGUID,
            Reason = UserPasswordChangeRequirement.ReasonReset,
            RequiredAtUtc = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await SeedRefreshTokenAsync(user.UserGUID, "session-a");
        var service = CreateService();
        await service.RequestSelfServiceAsync("staff@example.com", null);
        var code = _sender.Sent.Single().Code;

        var confirmed = await service.ConfirmAsync("STAFF@example.com", code, "MyOwn4567", null);
        var reused = await service.ConfirmAsync("staff@example.com", code, "Another999", null);

        Assert.True(confirmed.Success, confirmed.Message);
        // 老账号登录名不是邮箱，完成页靠回传的登录名提示与预填。
        Assert.Equal("staff", confirmed.Data!.LoginName);
        Assert.False(reused.Success);
        Assert.Equal(PasswordResetService.ResetCodeInvalidCode, reused.ErrorCode);
        var stored = await _db.Queryable<User>().FirstAsync(item => item.UserGUID == user.UserGUID);
        Assert.True(PasswordHasher.VerifyPassword("MyOwn4567", stored.PasswordHash, PasswordHasher.PasswordFormatRaw, out _));
        Assert.False(await _db.Queryable<UserPasswordChangeRequirement>().AnyAsync());
        Assert.True((await _db.Queryable<RefreshToken>().FirstAsync()).IsRevoked);
    }

    [Fact]
    public async Task Confirm_错5次后正确验证码也作废()
    {
        var user = await SeedUserAsync("staff", "staff@example.com");
        var service = CreateService();
        await service.RequestSelfServiceAsync("staff@example.com", null);
        var code = _sender.Sent.Single().Code;
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < PasswordResetService.MaxFailedAttempts; i++)
        {
            var attempt = await service.ConfirmAsync("staff@example.com", wrong, "MyOwn4567", null);
            Assert.False(attempt.Success);
        }
        var correctAfterLock = await service.ConfirmAsync("staff@example.com", code, "MyOwn4567", null);

        Assert.False(correctAfterLock.Success);
        var stored = await _db.Queryable<User>().FirstAsync(item => item.UserGUID == user.UserGUID);
        Assert.True(PasswordHasher.VerifyPassword("Secret123", stored.PasswordHash, PasswordHasher.PasswordFormatRaw, out _));
    }

    [Fact]
    public async Task Confirm_过期验证码与被新码作废的旧码都无效()
    {
        await SeedUserAsync("staff", "staff@example.com");
        var service = CreateService();
        await service.RequestSelfServiceAsync("staff@example.com", null);
        var oldCode = _sender.Sent.Single().Code;
        // 绕过 60 秒冷却：把第一条的创建时间提前。
        await _db.Updateable<UserPasswordResetCode>()
            .SetColumns(item => item.CreatedAtUtc == DateTime.UtcNow.AddMinutes(-5))
            .Where(item => item.ConsumedAtUtc == null)
            .ExecuteCommandAsync();
        var invite = await service.SendInviteAsync(
            (await _db.Queryable<User>().FirstAsync()).UserGUID, "manager-1", null);
        var newCode = _sender.Sent.Last().Code;

        Assert.True(invite.Success, invite.Message);
        Assert.True(_sender.Sent.Last().Invite);
        if (oldCode != newCode)
        {
            Assert.False((await service.ConfirmAsync("staff@example.com", oldCode, "MyOwn4567", null)).Success);
        }

        await _db.Updateable<UserPasswordResetCode>()
            .SetColumns(item => item.ExpiresAtUtc == DateTime.UtcNow.AddMinutes(-1))
            .Where(item => item.ConsumedAtUtc == null)
            .ExecuteCommandAsync();
        var expired = await service.ConfirmAsync("staff@example.com", newCode, "MyOwn4567", null);
        Assert.False(expired.Success);
        Assert.Equal(PasswordResetService.ResetCodeInvalidCode, expired.ErrorCode);
    }

    [Fact]
    public async Task SendInvite_无可用邮箱报错_发信失败时验证码立即作废()
    {
        var internalUser = await SeedUserAsync("internal", "internal@s001.store.local");
        var staff = await SeedUserAsync("staff", "staff@example.com");
        var service = CreateService();

        var missing = await service.SendInviteAsync(internalUser.UserGUID, "manager-1", null);
        _sender.FailNext = true;
        var failed = await service.SendInviteAsync(staff.UserGUID, "manager-1", null);

        Assert.False(missing.Success);
        Assert.Equal(PasswordResetService.NoDeliverableEmailCode, missing.ErrorCode);
        Assert.False(failed.Success);
        var row = await _db.Queryable<UserPasswordResetCode>().SingleAsync();
        Assert.NotNull(row.ConsumedAtUtc);
        Assert.Equal(UserPasswordResetCode.PurposeInvite, row.Purpose);
        Assert.InRange((row.ExpiresAtUtc - row.CreatedAtUtc).TotalHours, 71, 73);
    }

    [Fact]
    public async Task 同一邮箱对应多个账号时_不发信也不能确认()
    {
        await SeedUserAsync("dup-a", "dup@example.com");
        await SeedUserAsync("dup-b", "DUP@example.com");
        var service = CreateService();

        var request = await service.RequestSelfServiceAsync("dup@example.com", null);

        Assert.True(request.Success);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task EmailChange_验证通过才替换邮箱_并作废发往旧邮箱的设置密码验证码()
    {
        var user = await SeedUserAsync("legacy", "legacy@s001.store.local");
        var service = CreateEmailChangeService();
        // 先让店长发过一次邀请：占位邮箱发不出，这里直接造一条未用的邀请码代表「发往旧邮箱的码」。
        await _db.Insertable(new UserPasswordResetCode
        {
            UserGUID = user.UserGUID,
            Purpose = UserPasswordResetCode.PurposeInvite,
            CodeHash = "x",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
        }).ExecuteCommandAsync();

        var request = await service.RequestAsync(user.UserGUID, " New.Mail@Example.com ", "1.1.1.1");

        Assert.True(request.Success, request.Message);
        var sent = Assert.Single(_sender.EmailChangeSent);
        Assert.Equal("new.mail@example.com", sent.To);
        Assert.Equal("legacy", sent.LoginName);
        // 发码不影响进行中的邀请码，也不改邮箱。
        Assert.Equal(1, await _db.Queryable<UserPasswordResetCode>()
            .CountAsync(item => item.Purpose == UserPasswordResetCode.PurposeInvite && item.ConsumedAtUtc == null));
        Assert.Equal("legacy@s001.store.local", (await _db.Queryable<User>().FirstAsync(item => item.UserGUID == user.UserGUID)).Email);

        var wrongTarget = await service.ConfirmAsync(user.UserGUID, "other@example.com", sent.Code);
        Assert.False(wrongTarget.Success);
        Assert.Equal(PasswordResetService.ResetCodeInvalidCode, wrongTarget.ErrorCode);

        var confirmed = await service.ConfirmAsync(user.UserGUID, "new.mail@example.com", sent.Code);
        var reused = await service.ConfirmAsync(user.UserGUID, "new.mail@example.com", sent.Code);

        Assert.True(confirmed.Success, confirmed.Message);
        Assert.Equal("new.mail@example.com", confirmed.Data!.Email);
        Assert.False(reused.Success);
        Assert.Equal("new.mail@example.com", (await _db.Queryable<User>().FirstAsync(item => item.UserGUID == user.UserGUID)).Email);
        Assert.False(await _db.Queryable<UserPasswordResetCode>().AnyAsync(item => item.ConsumedAtUtc == null));
    }

    [Fact]
    public async Task EmailChange_换邮箱验证码不能用来设置密码()
    {
        var user = await SeedUserAsync("staff", "staff@example.com");
        var emailChange = CreateEmailChangeService();
        var reset = CreateService();
        await emailChange.RequestAsync(user.UserGUID, "next@example.com", null);
        var code = _sender.EmailChangeSent.Single().Code;

        // 用当前邮箱 + 换邮箱验证码去设密码：必须失败，且换邮箱验证码仍然有效。
        var hijack = await reset.ConfirmAsync("staff@example.com", code, "Hijack999", null);

        Assert.False(hijack.Success);
        Assert.Equal(PasswordResetService.ResetCodeInvalidCode, hijack.ErrorCode);
        var stored = await _db.Queryable<User>().FirstAsync(item => item.UserGUID == user.UserGUID);
        Assert.True(PasswordHasher.VerifyPassword("Secret123", stored.PasswordHash));
        Assert.True((await emailChange.ConfirmAsync(user.UserGUID, "next@example.com", code)).Success);
    }

    [Fact]
    public async Task EmailChange_拒绝占位邮箱_相同邮箱_已被占用_并有60秒冷却()
    {
        var user = await SeedUserAsync("staff", "staff@example.com");
        await SeedUserAsync("other", "Taken@Example.com");
        var service = CreateEmailChangeService();

        var placeholder = await service.RequestAsync(user.UserGUID, "staff@s001.store.local", null);
        var same = await service.RequestAsync(user.UserGUID, "STAFF@example.com", null);
        var taken = await service.RequestAsync(user.UserGUID, "taken@example.com", null);
        var first = await service.RequestAsync(user.UserGUID, "fresh@example.com", null);
        var second = await service.RequestAsync(user.UserGUID, "fresh2@example.com", null);

        Assert.Equal("INVALID_EMAIL", placeholder.ErrorCode);
        Assert.Equal(AccountEmailChangeService.EmailUnchangedCode, same.ErrorCode);
        Assert.Equal(AccountEmailChangeService.EmailExistsCode, taken.ErrorCode);
        Assert.True(first.Success, first.Message);
        Assert.Equal(PasswordResetService.ResendCooldownCode, second.ErrorCode);
        Assert.Single(_sender.EmailChangeSent);
    }

    [Fact]
    public async Task EmailChange_密码类验证码的冷却不挡换邮箱_换邮箱也不作废密码类验证码()
    {
        var user = await SeedUserAsync("staff", "staff@example.com");
        var reset = CreateService();
        await reset.RequestSelfServiceAsync("staff@example.com", null);

        var request = await CreateEmailChangeService().RequestAsync(user.UserGUID, "next@example.com", null);

        Assert.True(request.Success, request.Message);
        var resetCode = _sender.Sent.Single().Code;
        Assert.True((await reset.ConfirmAsync("staff@example.com", resetCode, "MyOwn4567", null)).Success);
    }

    private AccountEmailChangeService CreateEmailChangeService() =>
        new(CreateContext(_db), _sender, NullLogger<AccountEmailChangeService>.Instance);

    private PasswordResetService CreateService() =>
        new(CreateContext(_db), _sender, NullLogger<PasswordResetService>.Instance);

    private async Task<User> SeedUserAsync(string username, string email, bool isActive = true)
    {
        var user = new User
        {
            UserGUID = Guid.NewGuid().ToString(),
            Username = username,
            Email = email,
            PasswordHash = PasswordHasher.HashPassword("Secret123"),
            FullName = username,
            IsActive = isActive,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await _db.Insertable(user).ExecuteCommandAsync();
        return user;
    }

    private async Task SeedRefreshTokenAsync(string userGuid, string sessionId)
    {
        await _db.Insertable(new RefreshToken
        {
            RefreshTokenGUID = sessionId,
            UserGUID = userGuid,
            Token = $"token-{sessionId}",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            IsRevoked = false,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private static SqlSugarContext CreateContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private sealed class FakeEmailSender : IAccountPasswordEmailSender
    {
        public List<(string To, string Code, bool Invite)> Sent { get; } = new();
        public List<(string To, string LoginName, string Code)> EmailChangeSent { get; } = new();
        public bool FailNext { get; set; }

        public Task<ApiResponse<bool>> SendPasswordCodeAsync(
            string toEmail,
            string displayName,
            string loginName,
            string code,
            DateTime expiresAtUtc,
            bool invite,
            CancellationToken cancellationToken = default
        )
        {
            if (FailNext)
            {
                FailNext = false;
                return Task.FromResult(ApiResponse<bool>.Error("邮件发送失败，请稍后重试", "ACCOUNT_EMAIL_SEND_FAILED"));
            }
            Sent.Add((toEmail, code, invite));
            return Task.FromResult(ApiResponse<bool>.OK(true));
        }

        public Task<ApiResponse<bool>> SendEmailChangeCodeAsync(
            string toEmail,
            string displayName,
            string loginName,
            string code,
            DateTime expiresAtUtc,
            CancellationToken cancellationToken = default
        )
        {
            EmailChangeSent.Add((toEmail, loginName, code));
            return Task.FromResult(ApiResponse<bool>.OK(true));
        }
    }
}
