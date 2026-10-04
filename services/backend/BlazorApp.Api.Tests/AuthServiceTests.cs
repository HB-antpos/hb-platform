using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests
{
    public sealed class AuthServiceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;

        public AuthServiceTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
            _sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
            _sqliteConnection.Open();

            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _sqliteConnection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            });
        }

        [Fact]
        public async Task LoginAsync_WhenUserTableHasNoPhoneColumn_StillAuthenticates()
        {
            CreateLegacyUserSchemaWithoutPhone();
            await SeedLegacyUserAsync();

            var service = new AuthService(
                CreateSqlSugarContext(_db),
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            );

            var result = await service.LoginAsync(new LoginRequest
            {
                Username = "admin",
                Password = "Secret123",
                PasswordFormat = PasswordHasher.PasswordFormatRaw,
            });

            Assert.True(result.Success);
            Assert.Equal("admin", result.User?.Username);

            var migratedHash = await _db.Queryable<User>()
                .Where(user => user.Username == "admin")
                .Select(user => user.PasswordHash)
                .FirstAsync();
            Assert.StartsWith("pbkdf2-sha256$", migratedHash);
        }

        [Fact]
        public async Task LoginAsync_WhenLegacyClientSubmitsSha256_DoesNotMigratePasswordHash()
        {
            CreateLegacyUserSchemaWithoutPhone();
            var legacyHash = await SeedLegacyUserAsync();

            var service = new AuthService(
                CreateSqlSugarContext(_db),
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            );

            var result = await service.LoginAsync(new LoginRequest
            {
                Username = "admin",
                Password = PasswordHasher.ComputeSha256("Secret123"),
                PasswordFormat = PasswordHasher.PasswordFormatClientSha256,
            });

            Assert.True(result.Success);

            var storedHash = await _db.Queryable<User>()
                .Where(user => user.Username == "admin")
                .Select(user => user.PasswordHash)
                .FirstAsync();
            Assert.Equal(legacyHash, storedHash);
        }

        [Fact]
        public async Task EnsureLoginSessionSchema_WhenUserTableMissingLastLoginIp_AddsColumnBeforeLoginQuery()
        {
            CreateLegacyUserSchemaWithoutLastLoginIp();
            CreateLegacyRefreshTokenSchema();

            var dbContext = CreateSqlSugarContext(_db);
            dbContext.EnsureLoginSessionSchema();
            await SeedLegacyUserAsync();

            var columns = _db.DbMaintenance.GetColumnInfosByTableName("User", false);
            Assert.Contains(columns, column =>
                string.Equals(column.DbColumnName, "LastLoginIp", StringComparison.OrdinalIgnoreCase)
            );

            var indexes = _db.Ado.GetDataTable("PRAGMA index_list([RefreshToken])");
            Assert.Contains(indexes.Rows.Cast<System.Data.DataRow>(), row =>
                string.Equals(row["name"]?.ToString(), "IX_RefreshToken_UserGUID_IpAddress", StringComparison.OrdinalIgnoreCase)
            );

            var service = new AuthService(
                dbContext,
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            );

            var result = await service.LoginAsync(new LoginRequest
            {
                Username = "admin",
                Password = "Secret123",
                PasswordFormat = PasswordHasher.PasswordFormatRaw,
            });

            Assert.True(result.Success);
        }

        [Fact]
        public async Task RefreshTokensAsync_WhenUserIsInactive_ReturnsNullAndRevokesRefreshToken()
        {
            await CreateCurrentAuthSchemaAsync();

            const string userGuid = "inactive-user";
            const string refreshToken = "refresh-token-inactive";

            await _db.Insertable(
                new User
                {
                    UserGUID = userGuid,
                    Username = "inactive-user",
                    Email = "inactive@example.com",
                    PasswordHash = PasswordHasher.HashPassword("Secret123"),
                    FullName = "Inactive User",
                    IsActive = false,
                    IsDeleted = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }
            ).ExecuteCommandAsync();

            await _db.Insertable(
                new RefreshToken
                {
                    RefreshTokenGUID = Guid.NewGuid().ToString(),
                    UserGUID = userGuid,
                    Token = refreshToken,
                    ExpiresAt = DateTime.UtcNow.AddDays(1),
                    IsRevoked = false,
                    IsDeleted = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }
            ).ExecuteCommandAsync();

            var service = new AuthService(
                CreateSqlSugarContext(_db),
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            );

            var result = await service.RefreshTokensAsync(
                accessToken: string.Empty,
                refreshToken: refreshToken,
                ipAddress: "127.0.0.1",
                userAgent: "xunit"
            );

            Assert.Null(result);

            var storedToken = await _db.Queryable<RefreshToken>()
                .FirstAsync(token => token.Token == refreshToken);
            Assert.NotNull(storedToken);
            Assert.True(storedToken!.IsRevoked);
        }

        [Fact]
        public async Task GenerateTokensAsync_WhenExistingSessionUsesDifferentIp_RevokesOldSession()
        {
            await CreateCurrentAuthSchemaAsync();
            var user = await SeedCurrentUserAsync("session-user");

            await _db.Insertable(new RefreshToken
            {
                RefreshTokenGUID = "old-session",
                UserGUID = user.UserGUID,
                Token = "old-refresh",
                ExpiresAt = DateTime.UtcNow.AddDays(1),
                IsRevoked = false,
                IpAddress = "1.1.1.1",
                UserAgent = "old-agent",
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var service = new AuthService(
                CreateSqlSugarContext(_db),
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            );

            var result = await service.GenerateTokensAsync(user, "2.2.2.2", "new-agent");

            var oldSession = await _db.Queryable<RefreshToken>()
                .FirstAsync(token => token.Token == "old-refresh");
            var newSession = await _db.Queryable<RefreshToken>()
                .FirstAsync(token => token.Token == result.RefreshToken);
            var claims = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken).Claims.ToList();

            Assert.True(oldSession!.IsRevoked);
            Assert.False(newSession!.IsRevoked);
            Assert.Equal("2.2.2.2", newSession.IpAddress);
            Assert.Contains(claims, claim =>
                claim.Type == "sessionId" && claim.Value == newSession.RefreshTokenGUID
            );
        }

        [Fact]
        public async Task GenerateTokensAsync_WhenExistingSessionUsesSameIp_KeepsOldSession()
        {
            await CreateCurrentAuthSchemaAsync();
            var user = await SeedCurrentUserAsync("same-ip-user");

            await _db.Insertable(new RefreshToken
            {
                RefreshTokenGUID = "same-ip-session",
                UserGUID = user.UserGUID,
                Token = "same-ip-refresh",
                ExpiresAt = DateTime.UtcNow.AddDays(1),
                IsRevoked = false,
                IpAddress = "3.3.3.3",
                UserAgent = "old-agent",
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var service = new AuthService(
                CreateSqlSugarContext(_db),
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            );

            await service.GenerateTokensAsync(user, "3.3.3.3", "new-agent");

            var oldSession = await _db.Queryable<RefreshToken>()
                .FirstAsync(token => token.Token == "same-ip-refresh");

            Assert.False(oldSession!.IsRevoked);
        }

        [Fact]
        public async Task GenerateTokensAsync_WhenExistingSessionUsesMappedIpv4SameIp_KeepsOldSession()
        {
            await CreateCurrentAuthSchemaAsync();
            var user = await SeedCurrentUserAsync("mapped-same-ip-user");

            await _db.Insertable(new RefreshToken
            {
                RefreshTokenGUID = "mapped-same-ip-session",
                UserGUID = user.UserGUID,
                Token = "mapped-same-ip-refresh",
                ExpiresAt = DateTime.UtcNow.AddDays(1),
                IsRevoked = false,
                IpAddress = "::ffff:7.7.7.7",
                UserAgent = "old-agent",
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var service = new AuthService(
                CreateSqlSugarContext(_db),
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            );

            await service.GenerateTokensAsync(user, "7.7.7.7", "new-agent");

            var oldSession = await _db.Queryable<RefreshToken>()
                .FirstAsync(token => token.Token == "mapped-same-ip-refresh");

            Assert.False(oldSession!.IsRevoked);
        }

        [Fact]
        public async Task GenerateTokensAsync_WhenNewIpIsUnknown_KeepsExistingPublicIpSessions()
        {
            await CreateCurrentAuthSchemaAsync();
            var user = await SeedCurrentUserAsync("unknown-ip-user");

            await _db.Insertable(new RefreshToken
            {
                RefreshTokenGUID = "public-session",
                UserGUID = user.UserGUID,
                Token = "public-refresh",
                ExpiresAt = DateTime.UtcNow.AddDays(1),
                IsRevoked = false,
                IpAddress = "4.4.4.4",
                UserAgent = "old-agent",
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var service = new AuthService(
                CreateSqlSugarContext(_db),
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            );

            await service.GenerateTokensAsync(user, "unknown", "new-agent");

            var oldSession = await _db.Queryable<RefreshToken>()
                .FirstAsync(token => token.Token == "public-refresh");

            Assert.False(oldSession!.IsRevoked);
        }

        [Fact]
        public async Task GenerateTokensAsync_WhenExistingSessionIpIsUnknown_KeepsExistingSession()
        {
            await CreateCurrentAuthSchemaAsync();
            var user = await SeedCurrentUserAsync("old-unknown-ip-user");

            await _db.Insertable(new RefreshToken
            {
                RefreshTokenGUID = "unknown-session",
                UserGUID = user.UserGUID,
                Token = "unknown-refresh",
                ExpiresAt = DateTime.UtcNow.AddDays(1),
                IsRevoked = false,
                IpAddress = "unknown",
                UserAgent = "old-agent",
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var service = new AuthService(
                CreateSqlSugarContext(_db),
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            );

            await service.GenerateTokensAsync(user, "5.5.5.5", "new-agent");

            var oldSession = await _db.Queryable<RefreshToken>()
                .FirstAsync(token => token.Token == "unknown-refresh");

            Assert.False(oldSession!.IsRevoked);
        }

        [Fact]
        public async Task GenerateTokensAsync_WhenExistingSessionIpIsPrivate_KeepsExistingSession()
        {
            await CreateCurrentAuthSchemaAsync();
            var user = await SeedCurrentUserAsync("old-private-ip-user");

            await _db.Insertable(new RefreshToken
            {
                RefreshTokenGUID = "private-session",
                UserGUID = user.UserGUID,
                Token = "private-refresh",
                ExpiresAt = DateTime.UtcNow.AddDays(1),
                IsRevoked = false,
                IpAddress = "172.19.0.1",
                UserAgent = "old-agent",
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();

            var service = new AuthService(
                CreateSqlSugarContext(_db),
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            );

            await service.GenerateTokensAsync(user, "6.6.6.6", "new-agent");

            var oldSession = await _db.Queryable<RefreshToken>()
                .FirstAsync(token => token.Token == "private-refresh");

            Assert.False(oldSession!.IsRevoked);
        }

        [Fact]
        public async Task AuthSessionValidator_WhenSessionRevoked_ReturnsFalseForAccessTokenSession()
        {
            await CreateCurrentAuthSchemaAsync();
            var user = await SeedCurrentUserAsync("revoked-session-user");
            var dbContext = CreateSqlSugarContext(_db);
            var service = new AuthService(
                dbContext,
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            );

            var result = await service.GenerateTokensAsync(user, "4.4.4.4", "agent");
            var principal = CreatePrincipalFromAccessToken(result.AccessToken);
            var validator = new AuthSessionValidator(dbContext);

            Assert.True(await validator.IsAccessSessionActiveAsync(user.UserGUID, principal));

            var session = await _db.Queryable<RefreshToken>()
                .FirstAsync(token => token.Token == result.RefreshToken);
            Assert.NotNull(session);
            session!.IsRevoked = true;
            session.UpdatedAt = DateTime.UtcNow;
            await _db.Updateable(session)
                .UpdateColumns(token => new { token.IsRevoked, token.UpdatedAt })
                .ExecuteCommandAsync();

            Assert.False(await validator.IsAccessSessionActiveAsync(user.UserGUID, principal));
        }

        [Fact]
        public async Task GenerateTokensAsync_WhenUserHasManyDirectPermissions_KeepsSessionAccessTokensUnderCookieLimit()
        {
            await CreateCurrentAuthSchemaAsync();
            var user = await SeedCurrentUserAsync("many-permissions-user");
            var directPermissions = Enumerable.Range(1, 150)
                .Select(index => new SysUserPermission
                {
                    Id = $"{user.UserGUID}-permission-{index:D3}",
                    UserGuid = user.UserGUID,
                    PermissionCode = $"Regression.LargePermissionSet.Permission{index:D3}.View",
                    IsDeleted = false,
                })
                .ToList();
            await _db.Insertable(directPermissions).ExecuteCommandAsync();

            var dbContext = CreateSqlSugarContext(_db);
            var service = new AuthService(
                dbContext,
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            );
            var validator = new AuthSessionValidator(dbContext);

            var generatedTokens = await service.GenerateTokensAsync(user, "8.8.8.8", "xunit");
            await AssertCompactSessionAccessTokenAsync(
                generatedTokens.AccessToken,
                user.UserGUID,
                validator
            );

            var refreshedTokens = await service.RefreshTokensAsync(
                generatedTokens.AccessToken,
                generatedTokens.RefreshToken,
                "8.8.8.8",
                "xunit-refresh"
            );

            Assert.NotNull(refreshedTokens);
            await AssertCompactSessionAccessTokenAsync(
                refreshedTokens!.AccessToken,
                user.UserGUID,
                validator
            );
        }

        [Fact]
        public async Task LoginAsync_WhenUserHasDirectDashboardPermission_AddsPermissionClaim()
        {
            await CreateCurrentAuthSchemaAsync();
            var userGuid = Guid.NewGuid().ToString();

            await _db.Insertable(
                new User
                {
                    UserGUID = userGuid,
                    Username = "whs2",
                    Email = "whs2@example.com",
                    PasswordHash = PasswordHasher.HashPassword("Secret123"),
                    FullName = "WHS2",
                    IsActive = true,
                    IsDeleted = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }
            ).ExecuteCommandAsync();
            await _db.Insertable(
                new SysUserPermission
                {
                    Id = $"{userGuid}-dashboard",
                    UserGuid = userGuid,
                    PermissionCode = Permissions.Dashboard.View,
                    IsDeleted = false,
                }
            ).ExecuteCommandAsync();

            var service = new AuthService(
                CreateSqlSugarContext(_db),
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            );

            var result = await service.LoginAsync(new LoginRequest
            {
                Username = "whs2",
                Password = "Secret123",
                PasswordFormat = PasswordHasher.PasswordFormatRaw,
            });

            Assert.True(result.Success);
            var claims = new JwtSecurityTokenHandler().ReadJwtToken(result.Token).Claims.ToList();
            Assert.Contains(claims, claim =>
                claim.Type == "permission" && claim.Value == Permissions.Dashboard.View
            );
        }

        [Fact]
        public void GenerateJwtToken_WithLegacyLocalInvociePermission_AddsCanonicalLocalPurchaseClaim()
        {
            var service = new AuthService(
                CreateSqlSugarContext(_db),
                CreateJwtConfiguration(),
                new HttpContextAccessor()
            );
            var user = new User
            {
                UserGUID = "user-legacy",
                Username = "legacy-user",
                Email = "legacy@example.test",
                Roles = new List<Role> { new() { RoleName = "User" } },
            };

            var token = service.GenerateJwtToken(user, new List<string> { "LocalInvocie.View" });
            var claims = new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.ToList();

            Assert.Contains(claims, claim => claim.Type == "permission" && claim.Value == "LocalInvocie.View");
            Assert.Contains(claims, claim =>
                claim.Type == "permission" && claim.Value == Permissions.LocalPurchase.View
            );
        }

        public void Dispose()
        {
            _db.Dispose();
            _sqliteConnection.Dispose();

            if (File.Exists(_dbPath))
            {
                SqliteTempFileCleanup.DeleteIfExists(_dbPath);
            }
        }

        private void CreateLegacyUserSchemaWithoutPhone()
        {
            _db.Ado.ExecuteCommand(
                """
                CREATE TABLE [User] (
                    UserGUID TEXT NOT NULL PRIMARY KEY,
                    Username TEXT NOT NULL,
                    Email TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    FullName TEXT NULL,
                    LastLoginAt TEXT NULL,
                    LastLoginIp TEXT NULL,
                    IsActive INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    CreatedBy TEXT NULL,
                    UpdatedAt TEXT NULL,
                    UpdatedBy TEXT NULL,
                    IsDeleted INTEGER NOT NULL
                );

                CREATE TABLE [Role] (
                    RoleGUID TEXT NOT NULL PRIMARY KEY,
                    RoleName TEXT NOT NULL,
                    Description TEXT NULL,
                    IsActive INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    CreatedBy TEXT NULL,
                    UpdatedAt TEXT NULL,
                    UpdatedBy TEXT NULL,
                    IsDeleted INTEGER NOT NULL
                );

                CREATE TABLE [UserRole] (
                    UserRoleGUID TEXT NOT NULL PRIMARY KEY,
                    UserGUID TEXT NOT NULL,
                    RoleGUID TEXT NOT NULL,
                    AssignedAt TEXT NOT NULL,
                    AssignedByGUID TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    CreatedBy TEXT NULL,
                    UpdatedAt TEXT NULL,
                    UpdatedBy TEXT NULL,
                    IsDeleted INTEGER NOT NULL
                );
                """
            );
        }

        private void CreateLegacyUserSchemaWithoutLastLoginIp()
        {
            _db.Ado.ExecuteCommand(
                """
                CREATE TABLE [User] (
                    UserGUID TEXT NOT NULL PRIMARY KEY,
                    Username TEXT NOT NULL,
                    Email TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    FullName TEXT NULL,
                    LastLoginAt TEXT NULL,
                    IsActive INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    CreatedBy TEXT NULL,
                    UpdatedAt TEXT NULL,
                    UpdatedBy TEXT NULL,
                    IsDeleted INTEGER NOT NULL
                );

                CREATE TABLE [Role] (
                    RoleGUID TEXT NOT NULL PRIMARY KEY,
                    RoleName TEXT NOT NULL,
                    Description TEXT NULL,
                    IsActive INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    CreatedBy TEXT NULL,
                    UpdatedAt TEXT NULL,
                    UpdatedBy TEXT NULL,
                    IsDeleted INTEGER NOT NULL
                );

                CREATE TABLE [UserRole] (
                    UserRoleGUID TEXT NOT NULL PRIMARY KEY,
                    UserGUID TEXT NOT NULL,
                    RoleGUID TEXT NOT NULL,
                    AssignedAt TEXT NOT NULL,
                    AssignedByGUID TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    CreatedBy TEXT NULL,
                    UpdatedAt TEXT NULL,
                    UpdatedBy TEXT NULL,
                    IsDeleted INTEGER NOT NULL
                );
                """
            );
        }

        private void CreateLegacyRefreshTokenSchema()
        {
            _db.Ado.ExecuteCommand(
                """
                CREATE TABLE [RefreshToken] (
                    RefreshTokenGUID TEXT NOT NULL PRIMARY KEY,
                    UserGUID TEXT NOT NULL,
                    Token TEXT NOT NULL,
                    IpAddress TEXT NULL
                );
                """
            );
        }

        [Fact]
        public async Task ChangePasswordAsync_WhenCurrentPasswordValid_ClearsRequirementAndRevokesOtherSessions()
        {
            await CreateCurrentAuthSchemaAsync();
            _db.CodeFirst.InitTables<UserPasswordChangeRequirement>();
            var user = await SeedCurrentUserAsync("first-login");
            await _db.Insertable(new UserPasswordChangeRequirement
            {
                UserGUID = user.UserGUID,
                Reason = UserPasswordChangeRequirement.ReasonCreated,
                RequiredAtUtc = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await SeedRefreshTokenAsync(user.UserGUID, "session-current");
            await SeedRefreshTokenAsync(user.UserGUID, "session-other");
            var service = new AuthService(CreateSqlSugarContext(_db), CreateJwtConfiguration(), new HttpContextAccessor());

            var changed = await service.ChangePasswordAsync(
                user.UserGUID,
                new ChangePasswordDto
                {
                    CurrentPassword = "Secret123",
                    NewPassword = "MyOwn4567",
                    ConfirmPassword = "MyOwn4567",
                },
                "session-current"
            );

            Assert.True(changed);
            Assert.False(await _db.Queryable<UserPasswordChangeRequirement>().AnyAsync());
            var stored = await _db.Queryable<User>().FirstAsync(item => item.UserGUID == user.UserGUID);
            Assert.True(PasswordHasher.VerifyPassword("MyOwn4567", stored.PasswordHash, PasswordHasher.PasswordFormatRaw, out _));
            // 当前会话保留，其余会话吊销。
            var tokens = await _db.Queryable<RefreshToken>().ToListAsync();
            Assert.False(tokens.Single(item => item.RefreshTokenGUID == "session-current").IsRevoked);
            Assert.True(tokens.Single(item => item.RefreshTokenGUID == "session-other").IsRevoked);
        }

        [Theory]
        [InlineData("Wrong999", "MyOwn4567", "当前密码错误")]
        [InlineData("Secret123", "Secret123", "新密码不能与当前密码相同")]
        public async Task ChangePasswordAsync_WhenRejected_KeepsPasswordRequirementAndSessions(
            string currentPassword,
            string newPassword,
            string expectedMessage
        )
        {
            await CreateCurrentAuthSchemaAsync();
            _db.CodeFirst.InitTables<UserPasswordChangeRequirement>();
            var user = await SeedCurrentUserAsync("rejected-change");
            await _db.Insertable(new UserPasswordChangeRequirement
            {
                UserGUID = user.UserGUID,
                Reason = UserPasswordChangeRequirement.ReasonReset,
                RequiredAtUtc = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            await SeedRefreshTokenAsync(user.UserGUID, "session-other");
            var service = new AuthService(CreateSqlSugarContext(_db), CreateJwtConfiguration(), new HttpContextAccessor());

            var error = await Assert.ThrowsAsync<Exception>(() => service.ChangePasswordAsync(
                user.UserGUID,
                new ChangePasswordDto
                {
                    CurrentPassword = currentPassword,
                    NewPassword = newPassword,
                    ConfirmPassword = newPassword,
                },
                "session-current"
            ));

            Assert.Equal(expectedMessage, error.Message);
            Assert.True(await _db.Queryable<UserPasswordChangeRequirement>().AnyAsync());
            Assert.False((await _db.Queryable<RefreshToken>().FirstAsync()).IsRevoked);
            var stored = await _db.Queryable<User>().FirstAsync(item => item.UserGUID == user.UserGUID);
            Assert.True(PasswordHasher.VerifyPassword("Secret123", stored.PasswordHash, PasswordHasher.PasswordFormatRaw, out _));
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

        private async Task<string> SeedLegacyUserAsync()
        {
            var legacyHash = PasswordHasher.HashLegacyPassword(PasswordHasher.ComputeSha256("Secret123"));
            await _db.Ado.ExecuteCommandAsync(
                """
                INSERT INTO [User] (
                    UserGUID, Username, Email, PasswordHash, FullName, LastLoginAt, LastLoginIp, IsActive,
                    CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted
                )
                VALUES (
                    @UserGUID, @Username, @Email, @PasswordHash, @FullName, NULL, NULL, 1,
                    @CreatedAt, NULL, @UpdatedAt, NULL, 0
                );
                """,
                new List<SugarParameter>
                {
                    new("@UserGUID", Guid.NewGuid().ToString()),
                    new("@Username", "admin"),
                    new("@Email", "admin@example.com"),
                    new("@PasswordHash", legacyHash),
                    new("@FullName", "Admin User"),
                    new("@CreatedAt", DateTime.UtcNow),
                    new("@UpdatedAt", DateTime.UtcNow),
                }
            );

            return legacyHash;
        }

        private async Task<User> SeedCurrentUserAsync(string username)
        {
            var user = new User
            {
                UserGUID = Guid.NewGuid().ToString(),
                Username = username,
                Email = $"{username}@example.com",
                PasswordHash = PasswordHasher.HashPassword("Secret123"),
                FullName = username,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            await _db.Insertable(user).ExecuteCommandAsync();
            return user;
        }

        private Task CreateCurrentAuthSchemaAsync()
        {
            _db.CodeFirst.InitTables<User, Role, UserRole, RefreshToken, SysRolePermission>();
            _db.CodeFirst.InitTables<SysUserPermission>();
            return Task.CompletedTask;
        }

        private static ClaimsPrincipal CreatePrincipalFromAccessToken(string accessToken)
        {
            var claims = new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims;
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "jwt"));
        }

        private static async Task AssertCompactSessionAccessTokenAsync(
            string accessToken,
            string userGuid,
            AuthSessionValidator validator
        )
        {
            var context = new DefaultHttpContext();
            CookieHelper.SetAccessToken(context.Response, accessToken);
            var setCookieHeader = Assert.Single(context.Response.Headers["Set-Cookie"]);
            var cookieSize = Encoding.UTF8.GetByteCount(setCookieHeader!);
            var principal = CreatePrincipalFromAccessToken(accessToken);
            var claims = principal.Claims.ToList();

            Assert.True(
                cookieSize < 4096,
                $"完整 access Set-Cookie 为 {cookieSize} UTF-8 字节，必须小于 4096 字节。"
            );
            Assert.DoesNotContain(claims, claim => claim.Type == "permission");
            Assert.Contains(claims, claim =>
                claim.Type == "userId" && claim.Value == userGuid
            );
            Assert.Contains(claims, claim =>
                claim.Type == ClaimTypes.Role || claim.Type == "role"
            );
            Assert.Contains(claims, claim =>
                claim.Type == "sessionId" && !string.IsNullOrWhiteSpace(claim.Value)
            );
            Assert.True(await validator.IsAccessSessionActiveAsync(userGuid, principal));
        }

        private static IConfiguration CreateJwtConfiguration()
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = "abcdefghijklmnopqrstuvwxyz123456",
                    ["Jwt:Issuer"] = "BlazorApp.Tests",
                    ["Jwt:Audience"] = "BlazorApp.Tests",
                    ["Jwt:ExpireMinutes"] = "480",
                })
                .Build();
        }

        private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
        {
            var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
                typeof(SqlSugarContext)
            );

            var dbField = typeof(SqlSugarContext).GetField(
                "_db",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            dbField!.SetValue(context, db);

            return context;
        }
    }
}
