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
    }
}
