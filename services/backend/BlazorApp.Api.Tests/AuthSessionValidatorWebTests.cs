using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class AuthSessionValidatorWebTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public AuthSessionValidatorWebTests()
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
        _db.CodeFirst.InitTables<User, Role, UserRole, RefreshToken, UserLoginRestriction>();
    }

    [Fact]
    public async Task ValidateWebAccessSessionAsync_UsesOneSelectAndReturnsOnlyActiveRoles()
    {
        var user = await InsertUserAsync("web-user");
        var activeRole = await InsertRoleAsync("WarehouseManager", isActive: true);
        var inactiveRole = await InsertRoleAsync("DisabledRole", isActive: false);
        await InsertUserRoleAsync(user.UserGUID, activeRole.RoleGUID);
        await InsertUserRoleAsync(user.UserGUID, inactiveRole.RoleGUID);
        var session = await InsertSessionAsync(user.UserGUID, "web-session");

        var selectCount = 0;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                selectCount++;
            }
        };

        var result = await CreateValidator().ValidateWebAccessSessionAsync(
            user.UserGUID,
            session.RefreshTokenGUID);

        Assert.True(result.IsValid);
        Assert.Equal(["WarehouseManager"], result.ActiveRoleNames);
        Assert.Equal(1, selectCount);
    }

    [Fact]
    public async Task ValidateWebAccessSessionAsync_RevocationIsVisibleOnNextRequest()
    {
        var user = await InsertUserAsync("web-user");
        var session = await InsertSessionAsync(user.UserGUID, "web-session");
        var validator = CreateValidator();

        var firstResult = await validator.ValidateWebAccessSessionAsync(
            user.UserGUID,
            session.RefreshTokenGUID);
        Assert.True(firstResult.IsValid);

        await _db.Updateable<RefreshToken>()
            .SetColumns(token => token.IsRevoked == true)
            .Where(token => token.RefreshTokenGUID == session.RefreshTokenGUID)
            .ExecuteCommandAsync();

        var secondResult = await validator.ValidateWebAccessSessionAsync(
            user.UserGUID,
            session.RefreshTokenGUID);
        Assert.False(secondResult.IsValid);
    }

    [Fact]
    public async Task ValidateWebAccessSessionAsync_RejectsInactiveOrExpiredSessionAndUser()
    {
        var user = await InsertUserAsync("web-user");
        var expiredSession = await InsertSessionAsync(
            user.UserGUID,
            "expired-session",
            DateTime.UtcNow.AddMinutes(-1));
        var validator = CreateValidator();

        var expiredResult = await validator.ValidateWebAccessSessionAsync(
            user.UserGUID,
            expiredSession.RefreshTokenGUID);
        Assert.False(expiredResult.IsValid);

        await _db.Updateable<User>()
            .SetColumns(item => item.IsActive == false)
            .Where(item => item.UserGUID == user.UserGUID)
            .ExecuteCommandAsync();
        var activeSession = await InsertSessionAsync(user.UserGUID, "active-session");

        var inactiveUserResult = await validator.ValidateWebAccessSessionAsync(
            user.UserGUID,
            activeSession.RefreshTokenGUID);
        Assert.False(inactiveUserResult.IsValid);
    }

    [Fact]
    public async Task ValidateWebAccessSessionAsync_SoftDeletedSessionOrUserFailsClosed()
    {
        var user = await InsertUserAsync("web-user");
        var role = await InsertRoleAsync("WarehouseManager", isActive: true);
        await InsertUserRoleAsync(user.UserGUID, role.RoleGUID);
        var session = await InsertSessionAsync(user.UserGUID, "web-session");
        var validator = CreateValidator();

        await _db.Updateable<RefreshToken>()
            .SetColumns(item => item.IsDeleted == true)
            .Where(item => item.RefreshTokenGUID == session.RefreshTokenGUID)
            .ExecuteCommandAsync();
        Assert.False((await validator.ValidateWebAccessSessionAsync(
            user.UserGUID,
            session.RefreshTokenGUID)).IsValid);

        var secondSession = await InsertSessionAsync(user.UserGUID, "second-session");
        await _db.Updateable<User>()
            .SetColumns(item => item.IsDeleted == true)
            .Where(item => item.UserGUID == user.UserGUID)
            .ExecuteCommandAsync();
        Assert.False((await validator.ValidateWebAccessSessionAsync(
            user.UserGUID,
            secondSession.RefreshTokenGUID)).IsValid);
    }

    [Fact]
    public async Task ValidateWebAccessSessionAsync_MissingIdentifiersFailsWithoutQuery()
    {
        var selectCount = 0;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                selectCount++;
            }
        };

        var result = await CreateValidator().ValidateWebAccessSessionAsync("user", null);

        Assert.False(result.IsValid);
        Assert.Empty(result.ActiveRoleNames);
        Assert.Equal(0, selectCount);
    }

    private AuthSessionValidator CreateValidator() =>
        new(CreateSqlSugarContext(_db));

    private async Task<User> InsertUserAsync(string username)
    {
        var user = new User
        {
            UserGUID = Guid.NewGuid().ToString("N"),
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "test",
            IsActive = true,
            IsDeleted = false,
        };
        await _db.Insertable(user).ExecuteCommandAsync();
        return user;
    }

    private async Task<Role> InsertRoleAsync(string roleName, bool isActive)
    {
        var role = new Role
        {
            RoleGUID = Guid.NewGuid().ToString("N"),
            RoleName = roleName,
            IsActive = isActive,
            IsDeleted = false,
        };
        await _db.Insertable(role).ExecuteCommandAsync();
        return role;
    }

    private async Task InsertUserRoleAsync(string userGuid, string roleGuid)
    {
        await _db.Insertable(new UserRole
        {
            UserRoleGUID = Guid.NewGuid().ToString("N"),
            UserGUID = userGuid,
            RoleGUID = roleGuid,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task<RefreshToken> InsertSessionAsync(
        string userGuid,
        string sessionGuid,
        DateTime? expiresAt = null)
    {
        var session = new RefreshToken
        {
            RefreshTokenGUID = sessionGuid,
            UserGUID = userGuid,
            Token = $"token-{sessionGuid}",
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(30),
            IsRevoked = false,
            IsDeleted = false,
        };
        await _db.Insertable(session).ExecuteCommandAsync();
        return session;
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(SqlSugarContext));
        var dbField = typeof(SqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic);
        dbField!.SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
