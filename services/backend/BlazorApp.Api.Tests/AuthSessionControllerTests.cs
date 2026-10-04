using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class AuthSessionControllerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _sqliteConnection;
    private readonly SqlSugarClient _db;

    public AuthSessionControllerTests()
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

        _db.CodeFirst.InitTables<User, Role, UserRole, SysRolePermission>();
        _db.CodeFirst.InitTables<SysUserPermission, SysPermission>();
        _db.CodeFirst.InitTables<UserPasswordChangeRequirement, RefreshToken>();
    }

    [Fact]
    public async Task SessionLogin_ReturnsCookieOnlySessionPayload()
    {
        await _db.Insertable(
            new User
            {
                UserGUID = "user-1",
                Username = "alice",
                Email = "alice@example.com",
                PasswordHash = "hashed",
                FullName = "Alice",
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }
        ).ExecuteCommandAsync();

        var authService = new Mock<IAuthService>();
        authService
            .Setup(service => service.LoginAsync(It.IsAny<LoginRequest>()))
            .ReturnsAsync(
                new LoginResponse
                {
                    Success = true,
                    User = new LoginUserDto
                    {
                        UserGUID = "user-1",
                        Username = "alice",
                        Email = "alice@example.com",
                    },
                }
            );
        authService
            .Setup(service =>
                service.GenerateTokensAsync(
                    It.Is<User>(user => user.UserGUID == "user-1"),
                    It.IsAny<string>(),
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync(
                new TokenResponse
                {
                    AccessToken = "access-token",
                    RefreshToken = "refresh-token",
                    AccessTokenExpiry = DateTime.UtcNow.AddMinutes(15),
                    RefreshTokenExpiry = DateTime.UtcNow.AddDays(7),
                    Success = true,
                }
            );

        var controller = CreateController(authService.Object);

        var result = await InvokeAsync(controller, "SessionLogin", new LoginRequest
        {
            Username = "alice",
            Password = "Secret123",
        });

        Assert.NotNull(result);
        Assert.True(GetBoolean(result!, "Success"));
        Assert.False(HasNestedProperty(result!, "Data", "AccessToken"));
        Assert.False(HasNestedProperty(result!, "Data", "RefreshToken"));

        var setCookieHeaders = controller.Response.Headers.SetCookie.ToArray();
        Assert.Contains(
            setCookieHeaders,
            header =>
                header != null
                && header.Contains("access_token=", StringComparison.OrdinalIgnoreCase)
                && header.Contains("httponly", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            setCookieHeaders,
            header =>
                header != null
                && header.Contains("refresh_token=", StringComparison.OrdinalIgnoreCase)
                && header.Contains("httponly", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task Login_AllowsMissingLocationForUserPasswordLogin()
    {
        await _db.Insertable(
            new User
            {
                UserGUID = "mobile-user-1",
                Username = "mobileuser",
                Email = "mobile@example.com",
                PasswordHash = "hashed",
                FullName = "Mobile User",
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }
        ).ExecuteCommandAsync();

        var authService = new Mock<IAuthService>();
        authService
            .Setup(service => service.LoginAsync(It.Is<LoginRequest>(request =>
                request.Username == "mobileuser" && !request.LocationLatitude.HasValue
            )))
            .ReturnsAsync(
                new LoginResponse
                {
                    Success = true,
                    User = new LoginUserDto
                    {
                        UserGUID = "mobile-user-1",
                        Username = "mobileuser",
                        Email = "mobile@example.com",
                    },
                }
            );
        authService
            .Setup(service =>
                service.GenerateTokensAsync(
                    It.Is<User>(user => user.UserGUID == "mobile-user-1"),
                    It.IsAny<string>(),
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync(
                new TokenResponse
                {
                    AccessToken = "mobile-access-token",
                    RefreshToken = "mobile-refresh-token",
                    AccessTokenExpiry = DateTime.UtcNow.AddMinutes(15),
                    RefreshTokenExpiry = DateTime.UtcNow.AddDays(7),
                    Success = true,
                }
            );

        var controller = CreateController(authService.Object);

        var result = await InvokeAsync(controller, "Login", new LoginRequest
        {
            Username = "mobileuser",
            Password = "Secret123",
            PasswordFormat = "raw",
        });

        Assert.NotNull(result);
        Assert.True(GetBoolean(result!, "Success"));
        Assert.True(HasNestedProperty(result!, "Data", "AccessToken"));
        authService.Verify(service => service.LoginAsync(It.IsAny<LoginRequest>()), Times.Once);
    }

    [Fact]
    public async Task SessionLogin_RecordsResolvedLoginIpOnUser()
    {
        await _db.Insertable(
            new User
            {
                UserGUID = "ip-user-1",
                Username = "ipuser",
                Email = "ipuser@example.com",
                PasswordHash = "hashed",
                FullName = "IP User",
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }
        ).ExecuteCommandAsync();

        var authService = new Mock<IAuthService>();
        authService
            .Setup(service => service.LoginAsync(It.IsAny<LoginRequest>()))
            .ReturnsAsync(
                new LoginResponse
                {
                    Success = true,
                    User = new LoginUserDto
                    {
                        UserGUID = "ip-user-1",
                        Username = "ipuser",
                        Email = "ipuser@example.com",
                    },
                }
            );
        authService
            .Setup(service =>
                service.GenerateTokensAsync(
                    It.Is<User>(user => user.UserGUID == "ip-user-1"),
                    "8.8.8.9",
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync(
                new TokenResponse
                {
                    AccessToken = "access-token",
                    RefreshToken = "refresh-token",
                    AccessTokenExpiry = DateTime.UtcNow.AddMinutes(15),
                    RefreshTokenExpiry = DateTime.UtcNow.AddDays(7),
                    Success = true,
                }
            );

        var controller = CreateController(authService.Object);
        controller.ControllerContext.HttpContext.Request.Headers["X-Forwarded-For"] =
            "8.8.8.9, 10.0.0.1";

        var result = await InvokeAsync(controller, "SessionLogin", new LoginRequest
        {
            Username = "ipuser",
            Password = "Secret123",
        });

        Assert.NotNull(result);
        Assert.True(GetBoolean(result!, "Success"));

        var user = await _db.Queryable<User>().FirstAsync(item => item.UserGUID == "ip-user-1");
        Assert.Equal("8.8.8.9", user!.LastLoginIp);
        Assert.NotNull(user.LastLoginAt);
        authService.Verify(
            service => service.GenerateTokensAsync(
                It.Is<User>(item => item.UserGUID == "ip-user-1"),
                "8.8.8.9",
                It.IsAny<string>()
            ),
            Times.Once
        );
    }


    [Fact]
    public async Task SessionRefresh_ReturnsCookieOnlySessionPayload()
    {
        var authService = new Mock<IAuthService>();
        authService
            .Setup(service =>
                service.RefreshTokensAsync(
                    It.IsAny<HttpContext>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync(
                new TokenResponse
                {
                    AccessToken = "new-access",
                    RefreshToken = "new-refresh",
                    AccessTokenExpiry = DateTime.UtcNow.AddMinutes(15),
                    RefreshTokenExpiry = DateTime.UtcNow.AddDays(7),
                    Success = true,
                }
            );

        var controller = CreateController(authService.Object);
        controller.ControllerContext.HttpContext.Request.Headers.UserAgent = "xunit";
        controller.ControllerContext.HttpContext.Request.Headers.Cookie = "refresh_token=cookie-token";

        var result = await InvokeAsync(controller, "SessionRefresh");

        Assert.NotNull(result);
        Assert.True(GetBoolean(result!, "Success"));
        Assert.False(HasNestedProperty(result!, "Data", "AccessToken"));
        Assert.False(HasNestedProperty(result!, "Data", "RefreshToken"));
    }

    [Fact]
    public async Task SessionRefresh_WhenConcurrentRotationLoses_DoesNotClearWinnerCookies()
    {
        var authService = new Mock<IAuthService>();
        authService
            .Setup(service =>
                service.RefreshTokensAsync(
                    It.IsAny<HttpContext>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync((TokenResponse?)null);

        var controller = CreateController(authService.Object);
        controller.ControllerContext.HttpContext.Request.Headers.Cookie =
            "access_token=expired-access; refresh_token=already-consumed";

        var result = await InvokeAsync(controller, "SessionRefresh");

        Assert.NotNull(result);
        Assert.False(GetBoolean(result!, "Success"));
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task SessionLogout_RevokesRefreshTokenFromCookie()
    {
        var authService = new Mock<IAuthService>();
        authService
            .Setup(service => service.RevokeRefreshTokenAsync("refresh-cookie-token"))
            .ReturnsAsync(true);

        var controller = CreateController(authService.Object);
        controller.ControllerContext.HttpContext.Request.Headers.Cookie =
            "refresh_token=refresh-cookie-token";

        var result = await InvokeAsync(controller, "SessionLogout");

        Assert.NotNull(result);
        Assert.True(GetBoolean(result!, "Success"));
        authService.Verify(
            service => service.RevokeRefreshTokenAsync("refresh-cookie-token"),
            Times.Once
        );

        var setCookieHeaders = controller.Response.Headers.SetCookie.ToArray();
        Assert.Contains(
            setCookieHeaders,
            header => header != null && header.Contains("access_token=", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            setCookieHeaders,
            header => header != null && header.Contains("refresh_token=", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task GetCurrentUser_WhenLinkedStoreInactive_ReturnsStoreAndAllowsCurrentUserLoad()
    {
        await _db.Insertable(
            new User
            {
                UserGUID = "inactive-store-user",
                Username = "inactive-store-user",
                Email = "inactive-store-user@example.com",
                PasswordHash = "hashed",
                FullName = "Inactive Store User",
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }
        ).ExecuteCommandAsync();

        var userService = new Mock<IUserService>();
        userService
            .Setup(service => service.GetUserStoresAsync("inactive-store-user"))
            .ReturnsAsync(
                ApiResponse<List<UserStoreDto>>.OK(
                    new List<UserStoreDto>
                    {
                        new()
                        {
                            StoreGUID = "inactive-store",
                            StoreName = "Inactive Store",
                            StoreCode = "INACTIVE",
                            IsActive = false,
                            AssignedAt = DateTime.UtcNow,
                        },
                    },
                    "获取用户分店成功"
                )
            );

        var controller = CreateController(
            Mock.Of<IAuthService>(),
            roleService: CreateRoleService(),
            userService: userService.Object
        );
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim("userId", "inactive-store-user"),
                    new Claim(ClaimTypes.NameIdentifier, "inactive-store-user"),
                },
                "TestAuthType"
            )
        );

        var result = await controller.GetCurrentUser();

        Assert.True(result.Success);
        var store = Assert.Single(result.Data!.Stores!);
        Assert.Equal("Inactive Store", store.StoreName);
        Assert.False(store.IsActive);
    }

    [Fact]
    public async Task GetCurrentUser_WhenUserHasDirectDashboardPermission_ReturnsDirectPermission()
    {
        await _db.Insertable(
            new User
            {
                UserGUID = "direct-permission-user",
                Username = "whs2",
                Email = "whs2@example.com",
                PasswordHash = "hashed",
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
                Id = "direct-permission-user-dashboard",
                UserGuid = "direct-permission-user",
                PermissionCode = Permissions.Dashboard.View,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        var userService = new Mock<IUserService>();
        userService
            .Setup(service => service.GetUserStoresAsync("direct-permission-user"))
            .ReturnsAsync(ApiResponse<List<UserStoreDto>>.OK(new List<UserStoreDto>(), "获取用户分店成功"));

        var controller = CreateController(
            Mock.Of<IAuthService>(),
            roleService: CreateRoleService(),
            userService: userService.Object
        );
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim("userId", "direct-permission-user"),
                    new Claim(ClaimTypes.NameIdentifier, "direct-permission-user"),
                },
                "TestAuthType"
            )
        );

        var result = await controller.GetCurrentUser();

        Assert.True(result.Success);
        Assert.Contains(Permissions.Dashboard.View, result.Data!.Permissions);
    }

    [Fact]
    public async Task GetCurrentUser_ExactPermissions_ReturnsRawRoleCodeWithExpandedPermissions()
    {
        await SeedAuthUserAsync("exact-user-1", "role-user", "User");
        await InsertRolePermissionAsync("role-user", Permissions.Reports.View);

        var controller = CreateController(
            Mock.Of<IAuthService>(),
            roleService: CreateRoleService(),
            userService: CreateEmptyUserService()
        );
        SetCurrentUser(controller, "exact-user-1");

        var result = await controller.GetCurrentUser();

        Assert.True(result.Success);
        Assert.Contains(Permissions.Reports.View, result.Data!.Permissions);
        Assert.Contains(Permissions.Reports.ProductMovementView, result.Data.Permissions);
        Assert.Contains(Permissions.Reports.View, result.Data.ExactPermissions);
        Assert.DoesNotContain(
            Permissions.Reports.ProductMovementView,
            result.Data.ExactPermissions
        );
    }

    [Fact]
    public async Task GetCurrentUser_ExactPermissions_IncludesDirectUserPermission()
    {
        await SeedAuthUserAsync("exact-user-2", "role-user-2", "User");
        await _db.Insertable(
            new SysUserPermission
            {
                Id = "exact-user-2-product-movement",
                UserGuid = "exact-user-2",
                PermissionCode = Permissions.Reports.ProductMovementView,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        var controller = CreateController(
            Mock.Of<IAuthService>(),
            roleService: CreateRoleService(),
            userService: CreateEmptyUserService()
        );
        SetCurrentUser(controller, "exact-user-2");

        var result = await controller.GetCurrentUser();

        Assert.True(result.Success);
        Assert.Contains(Permissions.Reports.ProductMovementView, result.Data!.ExactPermissions);
        Assert.Contains(Permissions.Reports.ProductMovementView, result.Data.Permissions);
    }

    [Fact]
    public async Task GetCurrentUser_WhenPasswordChangeRequired_ReturnsMustChangePassword()
    {
        await SeedAuthUserAsync("first-login-user", "role-user-first", "User");
        var controller = CreateController(
            Mock.Of<IAuthService>(),
            roleService: CreateRoleService(),
            userService: CreateEmptyUserService()
        );
        SetCurrentUser(controller, "first-login-user");

        var before = await controller.GetCurrentUser();
        await _db.Insertable(new UserPasswordChangeRequirement
        {
            UserGUID = "first-login-user",
            Reason = UserPasswordChangeRequirement.ReasonCreated,
            RequiredAtUtc = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        var after = await controller.GetCurrentUser();

        Assert.True(before.Success);
        Assert.False(before.Data!.MustChangePassword);
        Assert.True(after.Success);
        Assert.True(after.Data!.MustChangePassword);
    }

    [Fact]
    public async Task GetCurrentUser_SuperAdmin_ExactReturnsAllPermissions()
    {
        await SeedAuthUserAsync("exact-admin-1", "role-admin", "管理员");
        await InsertPermissionAsync(Permissions.Reports.View);
        await InsertPermissionAsync(Permissions.Reports.ProductMovementView);

        var controller = CreateController(
            Mock.Of<IAuthService>(),
            roleService: CreateRoleService(),
            userService: CreateEmptyUserService()
        );
        SetCurrentUser(controller, "exact-admin-1");

        var result = await controller.GetCurrentUser();

        Assert.True(result.Success);
        Assert.Contains(Permissions.Reports.View, result.Data!.ExactPermissions);
        Assert.Contains(Permissions.Reports.ProductMovementView, result.Data.ExactPermissions);
    }

    [Fact]
    public async Task GetCurrentUser_DisabledRole_ExcludesRoleAndPermissionsFromMenuFields()
    {
        await _db.Insertable(
            new User
            {
                UserGUID = "disabled-role-user",
                Username = "disabled-role-user",
                Email = "disabled-role-user@example.com",
                PasswordHash = "hashed",
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new Role
            {
                RoleGUID = "disabled-role",
                RoleName = "MenuRole",
                IsActive = false,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new UserRole
            {
                UserRoleGUID = "disabled-role-user-disabled-role",
                UserGUID = "disabled-role-user",
                RoleGUID = "disabled-role",
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await InsertRolePermissionAsync("disabled-role", Permissions.Reports.ProductMovementView);

        var controller = CreateController(
            Mock.Of<IAuthService>(),
            roleService: CreateRoleService(),
            userService: CreateEmptyUserService()
        );
        SetCurrentUser(controller, "disabled-role-user");

        var result = await controller.GetCurrentUser();

        Assert.True(result.Success);
        Assert.DoesNotContain("MenuRole", result.Data!.RoleNames);
        Assert.Empty(result.Data.Roles);
        Assert.DoesNotContain(Permissions.Reports.ProductMovementView, result.Data.Permissions);
        Assert.DoesNotContain(Permissions.Reports.ProductMovementView, result.Data.ExactPermissions);
    }

    [Fact]
    public async Task GetCurrentUser_SoftDeletedUserRole_ExcludesRoleAndPermissionsFromMenuFields()
    {
        await _db.Insertable(
            new User
            {
                UserGUID = "soft-deleted-role-user",
                Username = "soft-deleted-role-user",
                Email = "soft-deleted-role-user@example.com",
                PasswordHash = "hashed",
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new Role
            {
                RoleGUID = "soft-deleted-role",
                RoleName = "MenuRole",
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new UserRole
            {
                UserRoleGUID = "soft-deleted-role-user-soft-deleted-role",
                UserGUID = "soft-deleted-role-user",
                RoleGUID = "soft-deleted-role",
                IsDeleted = true,
            }
        ).ExecuteCommandAsync();
        await InsertRolePermissionAsync("soft-deleted-role", Permissions.Reports.ProductMovementView);

        var controller = CreateController(
            Mock.Of<IAuthService>(),
            roleService: CreateRoleService(),
            userService: CreateEmptyUserService()
        );
        SetCurrentUser(controller, "soft-deleted-role-user");

        var result = await controller.GetCurrentUser();

        Assert.True(result.Success);
        Assert.DoesNotContain("MenuRole", result.Data!.RoleNames);
        Assert.Empty(result.Data.Roles);
        Assert.DoesNotContain(Permissions.Reports.ProductMovementView, result.Data.Permissions);
        Assert.DoesNotContain(Permissions.Reports.ProductMovementView, result.Data.ExactPermissions);
    }

    [Fact]
    public async Task GetCurrentUser_InactiveUser_FailsClosed()
    {
        await _db.Insertable(
            new User
            {
                UserGUID = "inactive-user",
                Username = "inactive-user",
                Email = "inactive-user@example.com",
                PasswordHash = "hashed",
                IsActive = false,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }
        ).ExecuteCommandAsync();

        var controller = CreateController(
            Mock.Of<IAuthService>(),
            roleService: CreateRoleService(),
            userService: CreateEmptyUserService()
        );
        SetCurrentUser(controller, "inactive-user");

        var result = await controller.GetCurrentUser();

        Assert.False(result.Success);
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

    private AuthController CreateController(
        IAuthService authService,
        IRoleService? roleService = null,
        IUserService? userService = null
    )
    {
        var controller = new AuthController(
            authService,
            CreateSqlSugarContext(_db),
            roleService ?? Mock.Of<IRoleService>(),
            userService ?? Mock.Of<IUserService>(),
            Mock.Of<ILogger<AuthController>>()
        );

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        httpContext.Response.Body = new MemoryStream();
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = httpContext,
        };

        return controller;
    }

    private static async Task<object?> InvokeAsync(
        AuthController controller,
        string methodName,
        params object?[] args
    )
    {
        var method = typeof(AuthController).GetMethod(methodName);
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(controller, args));
        await task;

        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static bool GetBoolean(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<bool>(property!.GetValue(instance));
    }

    private static bool HasNestedProperty(object instance, string outerProperty, string nestedProperty)
    {
        var outer = instance.GetType().GetProperty(outerProperty);
        Assert.NotNull(outer);

        var outerValue = outer!.GetValue(instance);
        Assert.NotNull(outerValue);

        return outerValue!.GetType().GetProperty(nestedProperty) != null;
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

    private async Task SeedAuthUserAsync(string userGuid, string roleGuid, string roleName)
    {
        await _db.Insertable(
            new User
            {
                UserGUID = userGuid,
                Username = userGuid,
                Email = $"{userGuid}@example.test",
                PasswordHash = "hashed",
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }
        ).ExecuteCommandAsync();

        await _db.Insertable(
            new Role
            {
                RoleGUID = roleGuid,
                RoleName = roleName,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        await _db.Insertable(
            new UserRole
            {
                UserRoleGUID = $"{userGuid}-{roleGuid}",
                UserGUID = userGuid,
                RoleGUID = roleGuid,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private async Task InsertRolePermissionAsync(string roleGuid, string permissionCode)
    {
        await _db.Insertable(
            new SysRolePermission
            {
                Id = $"{roleGuid}-{permissionCode}",
                RoleGuid = roleGuid,
                PermissionCode = permissionCode,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private async Task InsertPermissionAsync(string code)
    {
        await _db.Insertable(
            new SysPermission
            {
                Id = code,
                Code = code,
                Name = code,
                Category = "test",
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private static void SetCurrentUser(AuthController controller, string userGuid)
    {
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim("userId", userGuid),
                    new Claim(ClaimTypes.NameIdentifier, userGuid),
                },
                "TestAuthType"
            )
        );
    }

    private RoleService CreateRoleService()
    {
        return new RoleService(
            CreateSqlSugarContext(_db),
            NullLogger<RoleService>.Instance,
            new HttpContextAccessor()
        );
    }

    private static IUserService CreateEmptyUserService()
    {
        var userService = new Mock<IUserService>();
        userService
            .Setup(service => service.GetUserStoresAsync(It.IsAny<string>()))
            .ReturnsAsync(
                ApiResponse<List<UserStoreDto>>.OK(
                    new List<UserStoreDto>(),
                    "获取用户分店成功"
                )
            );
        return userService.Object;
    }
}
