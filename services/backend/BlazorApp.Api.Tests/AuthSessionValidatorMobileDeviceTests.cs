using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.MobileDeviceActivation;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class AuthSessionValidatorMobileDeviceTests
{
    private static readonly Guid BindingId = Guid.Parse("e2d61ff6-a86c-49f6-9ca9-edb7db784213");

    [Fact]
    public async Task MobileBoundAccountToken_ValidBinding_DoesNotRequireRefreshTokenSession()
    {
        const string userGuid = "mobile-bound-user";
        var bindingContext = new MobileDeviceBindingContext(
            BindingId,
            4,
            1,
            "mobile-hardware-001",
            userGuid);
        var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
        activationService
            .Setup(service => service.ValidateTokenBindingAsync(
                bindingContext,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileDeviceBoundAccountValidationResult(
                true,
                userGuid,
                "mobile-user",
                ["StoreUser"]));
        var validator = new AuthSessionValidator(
            CreateUninitializedSqlSugarContext(),
            activationService.Object);

        var isActive = await validator.IsAccessSessionActiveAsync(
            userGuid,
            CreateMobilePrincipal(bindingContext));

        Assert.True(isActive);
        activationService.VerifyAll();
    }

    [Fact]
    public async Task MobileBoundAccountToken_MalformedBindingClaims_FailsClosed()
    {
        const string userGuid = "mobile-bound-user";
        var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("token_use", MobileDeviceAccountTokenIssuer.TokenUse),
                new Claim("userGuid", userGuid),
            ],
            "Bearer"));
        var validator = new AuthSessionValidator(
            CreateUninitializedSqlSugarContext(),
            activationService.Object);

        var isActive = await validator.IsAccessSessionActiveAsync(userGuid, principal);

        Assert.False(isActive);
        activationService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MobileBoundAccountToken_ValidationReturnsDifferentUser_FailsClosed()
    {
        const string userGuid = "mobile-bound-user";
        var bindingContext = new MobileDeviceBindingContext(
            BindingId,
            4,
            27,
            "mobile-hardware-001",
            userGuid);
        var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
        activationService
            .Setup(service => service.ValidateTokenBindingAsync(
                bindingContext,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileDeviceBoundAccountValidationResult(
                true,
                "another-user",
                "mobile-user",
                ["StoreUser"]));
        var validator = new AuthSessionValidator(
            CreateUninitializedSqlSugarContext(),
            activationService.Object);

        var isActive = await validator.IsAccessSessionActiveAsync(
            userGuid,
            CreateMobilePrincipal(bindingContext));

        Assert.False(isActive);
        activationService.VerifyAll();
    }

    [Fact]
    public async Task MobileDeviceAggregateValidation_ValidAccountReadsMainDatabaseOnceAndReturnsRoles()
    {
        await using var fixture = await MainDatabaseFixture.CreateAsync();
        var binding = CreateBindingContext();
        var now = DateTime.UtcNow;
        await fixture.Database.Insertable(new Store
        {
            StoreGUID = "store-2", StoreCode = "S002", StoreName = "Inactive Store",
            IsActive = false, IsDeleted = false, CreatedAt = now, UpdatedAt = now,
        }).ExecuteCommandAsync();
        await fixture.Database.Insertable(new UserStore
        {
            UserStoreGUID = "user-store-2", UserGUID = binding.UserGuid, StoreGUID = "store-2",
            IsPrimary = false, IsDeleted = false, AssignedAt = now, CreatedAt = now, UpdatedAt = now,
        }).ExecuteCommandAsync();
        var bindingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainQueryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
        activationService
            .Setup(service => service.ValidateTokenBindingStateAsync(binding, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await bindingGate.Task;
                return new MobileDeviceTokenBindingValidationResult(true, binding.UserGuid, "S001");
            });

        var selectCount = 0;
        fixture.Database.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                selectCount++;
                mainQueryStarted.TrySetResult();
            }
        };
        var validationTask = new AuthSessionValidator(fixture.Context, activationService.Object)
            .ValidateMobileDeviceAccessAsync(binding.UserGuid, CreateMobilePrincipal(binding));
        await mainQueryStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.False(validationTask.IsCompleted);
        bindingGate.SetResult();
        var result = await validationTask;

        Assert.True(result.IsValid);
        Assert.Equal(["StoreUser"], result.ActiveRoleNames);
        Assert.Equal(["S001", "S002"], result.AccessibleStoreCodes);
        Assert.Equal(1, selectCount);
        activationService.Verify(service => service.ValidateTokenBindingStateAsync(
            binding,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false, false, false, true, false)] // 用户停用
    [InlineData(true, true, false, true, false)]  // 用户软删除
    [InlineData(true, false, true, true, false)]  // 门店权限撤销
    public async Task MobileDeviceAggregateValidation_InvalidAccountStateFailsClosed(
        bool isActive,
        bool isDeleted,
        bool storeAssignmentDeleted,
        bool roleActive,
        bool roleDeleted)
    {
        await using var fixture = await MainDatabaseFixture.CreateAsync(
            isActive,
            isDeleted,
            storeAssignmentDeleted,
            roleActive,
            roleDeleted);
        var binding = CreateBindingContext();
        var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
        activationService
            .Setup(service => service.ValidateTokenBindingStateAsync(binding, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileDeviceTokenBindingValidationResult(true, binding.UserGuid, "S001"));

        var result = await new AuthSessionValidator(fixture.Context, activationService.Object)
            .ValidateMobileDeviceAccessAsync(binding.UserGuid, CreateMobilePrincipal(binding));

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task MobileDeviceAggregateValidation_RoleDisabledOrDeletedRemainsValidWithoutRoleClaim()
    {
        await using var fixture = await MainDatabaseFixture.CreateAsync(
            roleActive: false,
            roleDeleted: true);
        var binding = CreateBindingContext();
        var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
        activationService
            .Setup(service => service.ValidateTokenBindingStateAsync(binding, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileDeviceTokenBindingValidationResult(true, binding.UserGuid, "S001"));

        var result = await new AuthSessionValidator(fixture.Context, activationService.Object)
            .ValidateMobileDeviceAccessAsync(binding.UserGuid, CreateMobilePrincipal(binding));

        Assert.True(result.IsValid);
        Assert.Empty(result.ActiveRoleNames);
    }

    [Fact]
    public async Task MobileDeviceAggregateValidation_InactiveBindingStoreFailsClosed()
    {
        await using var fixture = await MainDatabaseFixture.CreateAsync(storeActive: false);
        var binding = CreateBindingContext();
        var activationService = new Mock<IMobileDeviceActivationService>(MockBehavior.Strict);
        activationService
            .Setup(service => service.ValidateTokenBindingStateAsync(binding, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileDeviceTokenBindingValidationResult(true, binding.UserGuid, "S001"));

        var result = await new AuthSessionValidator(fixture.Context, activationService.Object)
            .ValidateMobileDeviceAccessAsync(binding.UserGuid, CreateMobilePrincipal(binding));

        Assert.False(result.IsValid);
        Assert.Empty(result.AccessibleStoreCodes ?? []);
    }

    [Theory]
    [InlineData(false, 4, "mobile-hardware-001", true, 1)] // 有效绑定，恰好一次 POSM 查询
    [InlineData(true, 4, "mobile-hardware-001", true, 1)]  // 绑定撤销
    [InlineData(false, 5, "mobile-hardware-001", true, 1)] // 令牌版本变化
    [InlineData(false, 4, "mobile-hardware-001", false, 1)] // registration 缺失
    [InlineData(false, 4, "mobile-hardware-001", true, 0)] // registration 停用
    [InlineData(false, 4, "MOBILE-HARDWARE-001", true, 1)] // 硬件大小写不一致
    public async Task MobileDeviceBindingStateQuery_UsesOneJoinAndFailsClosed(
        bool revoked,
        int tokenVersion,
        string tokenHardwareId,
        bool registrationExists,
        int registrationStatus)
    {
        await using var fixture = await PosmBindingFixture.CreateAsync(
            revoked,
            registrationExists,
            registrationStatus);
        var selectCount = 0;
        fixture.Database.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                selectCount++;
            }
        };
        var binding = new MobileDeviceBindingContext(
            BindingId,
            tokenVersion,
            1,
            tokenHardwareId,
            "mobile-bound-user");

        var result = await fixture.Service.ValidateTokenBindingStateAsync(
            binding,
            CancellationToken.None);

        Assert.Equal(
            !revoked
            && tokenVersion == 4
            && tokenHardwareId == "mobile-hardware-001"
            && registrationExists
            && registrationStatus == 1,
            result.IsValid);
        Assert.Equal(1, selectCount);
    }

    [Fact]
    public async Task MobileDeviceBindingStateQuery_DeviceTypeMustRemainExactlyMobile()
    {
        await using var fixture = await PosmBindingFixture.CreateAsync(false, true, 1);
        await fixture.Database.Updateable<POSM_设备注册信息表>()
            .SetColumns(device => device.设备类型 == "mobile")
            .Where(device => device.ID == 1)
            .ExecuteCommandAsync();
        var binding = new MobileDeviceBindingContext(BindingId, 4, 1, "mobile-hardware-001", "mobile-bound-user");
        var result = await fixture.Service.ValidateTokenBindingStateAsync(binding, CancellationToken.None);
        Assert.False(result.IsValid);
    }

    private static MobileDeviceBindingContext CreateBindingContext() => new(
        BindingId,
        4,
        27,
        "mobile-hardware-001",
        "mobile-bound-user");

    private sealed class MainDatabaseFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public SqlSugarClient Database { get; }
        public SqlSugarContext Context { get; }

        private MainDatabaseFixture(SqliteConnection connection, SqlSugarClient database)
        {
            this.connection = connection;
            Database = database;
            Context = CreateSqlSugarContext(database);
        }

        public static async Task<MainDatabaseFixture> CreateAsync(
            bool isActive = true,
            bool isDeleted = false,
            bool storeAssignmentDeleted = false,
            bool roleActive = true,
            bool roleDeleted = false,
            bool storeActive = true)
        {
            var connection = new SqliteConnection($"Data Source={Path.Combine(Path.GetTempPath(), $"auth-{Guid.NewGuid():N}.db")}");
            await connection.OpenAsync();
            var database = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = connection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            });
            database.CodeFirst.InitTables<User, Store, UserStore, UserRole, Role>();
            var now = DateTime.UtcNow;
            await database.Insertable(new User
            {
                UserGUID = "mobile-bound-user", Username = "mobile", Email = "mobile@example.test",
                PasswordHash = "hash", IsActive = isActive, IsDeleted = isDeleted,
                CreatedAt = now, UpdatedAt = now,
            }).ExecuteCommandAsync();
            await database.Insertable(new Store
            {
                StoreGUID = "store-1", StoreCode = "S001", StoreName = "Store 1",
                IsActive = storeActive, IsDeleted = false, CreatedAt = now, UpdatedAt = now,
            }).ExecuteCommandAsync();
            await database.Insertable(new UserStore
            {
                UserStoreGUID = "user-store-1", UserGUID = "mobile-bound-user", StoreGUID = "store-1",
                IsPrimary = true, IsDeleted = storeAssignmentDeleted, AssignedAt = now,
                CreatedAt = now, UpdatedAt = now,
            }).ExecuteCommandAsync();
            await database.Insertable(new Role
            {
                RoleGUID = "role-1", RoleName = "StoreUser", IsActive = roleActive,
                IsDeleted = roleDeleted, CreatedAt = now, UpdatedAt = now,
            }).ExecuteCommandAsync();
            await database.Insertable(new UserRole
            {
                UserRoleGUID = "user-role-1", UserGUID = "mobile-bound-user", RoleGUID = "role-1",
                IsDeleted = false, AssignedAt = now, CreatedAt = now, UpdatedAt = now,
            }).ExecuteCommandAsync();
            return new MainDatabaseFixture(connection, database);
        }

        public async ValueTask DisposeAsync()
        {
            Database.Dispose();
            await connection.DisposeAsync();
        }
    }

    private sealed class PosmBindingFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public SqlSugarClient Database { get; }
        public MobileDeviceActivationService Service { get; }

        private PosmBindingFixture(SqliteConnection connection, SqlSugarClient database)
        {
            this.connection = connection;
            Database = database;
            var posmContext = CreatePosmSqlSugarContext(database);
            Service = new MobileDeviceActivationService(
                posmContext,
                CreateUninitializedSqlSugarContext(),
                Mock.Of<IMobileDeviceAccountTokenIssuer>(),
                NullLogger<MobileDeviceActivationService>.Instance);
        }

        public static async Task<PosmBindingFixture> CreateAsync(
            bool revoked,
            bool registrationExists,
            int registrationStatus)
        {
            var connection = new SqliteConnection($"Data Source={Path.Combine(Path.GetTempPath(), $"auth-posm-{Guid.NewGuid():N}.db")}");
            await connection.OpenAsync();
            var database = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = connection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            });
            database.CodeFirst.InitTables<POSM_设备注册信息表>();
            // SQLite CodeFirst 将可空 DateTime/Guid 推断为 NOT NULL；这里建最小真实查询表，保留生产字段的可空语义。
            database.Ado.ExecuteCommand("""
                CREATE TABLE POSM_MobileDeviceAccountBinding (
                    BindingId TEXT PRIMARY KEY,
                    DeviceRegistrationId INTEGER NOT NULL,
                    HardwareId TEXT NOT NULL,
                    DeviceCode TEXT NOT NULL,
                    StoreCode TEXT NOT NULL,
                    DeviceSystem TEXT NOT NULL,
                    TargetUserGuid TEXT NOT NULL,
                    Version INTEGER NOT NULL,
                    RevokedAtUtc TEXT NULL
                )
                """);
            if (registrationExists)
            {
                await database.Insertable(new POSM_设备注册信息表
                {
                    ID = 27,
                    设备硬件识别码 = "mobile-hardware-001",
                    系统设备编号 = "mobile-device-001",
                    分店代码 = "S001",
                    设备类型 = "Mobile",
                    设备系统 = "Android",
                    设备状态 = registrationStatus,
                    设备授权码 = "code",
                }).ExecuteCommandAsync();
            }
            await database.Ado.ExecuteCommandAsync(
                "INSERT INTO POSM_MobileDeviceAccountBinding "
                + "(BindingId, DeviceRegistrationId, HardwareId, DeviceCode, StoreCode, DeviceSystem, TargetUserGuid, Version, RevokedAtUtc) "
                + "VALUES (@BindingId, 1, @HardwareId, 'mobile-device-001', 'S001', 'Android', 'mobile-bound-user', 4, @RevokedAtUtc)",
                new SugarParameter("@BindingId", BindingId.ToString()),
                new SugarParameter("@HardwareId", "mobile-hardware-001"),
                new SugarParameter("@RevokedAtUtc", revoked ? DateTime.UtcNow : null));
            return new PosmBindingFixture(connection, database);
        }

        public async ValueTask DisposeAsync()
        {
            Database.Dispose();
            await connection.DisposeAsync();
        }
    }

    private static ClaimsPrincipal CreateMobilePrincipal(MobileDeviceBindingContext context)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("token_use", MobileDeviceAccountTokenIssuer.TokenUse),
                new Claim("userGuid", context.UserGuid),
                new Claim(
                    MobileDeviceAccountTokenIssuer.BindingIdClaim,
                    context.BindingId.ToString("N")),
                new Claim(
                    MobileDeviceAccountTokenIssuer.BindingVersionClaim,
                    context.BindingVersion.ToString()),
                new Claim(
                    MobileDeviceAccountTokenIssuer.DeviceRegistrationIdClaim,
                    context.DeviceRegistrationId.ToString()),
                new Claim(
                    MobileDeviceAccountTokenIssuer.HardwareIdClaim,
                    context.HardwareId),
            ],
            "Bearer"));
    }

    private static SqlSugarContext CreateUninitializedSqlSugarContext()
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(SqlSugarContext));
        var dbField = typeof(SqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic);
        dbField!.SetValue(context, Mock.Of<ISqlSugarClient>());
        return context;
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static POSMSqlSugarContext CreatePosmSqlSugarContext(ISqlSugarClient db)
    {
        var context = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(POSMSqlSugarContext));
        typeof(POSMSqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }
}
