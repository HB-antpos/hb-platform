using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BlazorApp.Api.Authorization;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Middleware;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 大屏收银 App 内订货助手的设备令牌：签发内容、分店收口、权限拒绝、作用域与限流分区。
/// </summary>
public sealed class BrowserExtensionPosDeviceTests
{
    private static readonly BrowserExtensionPosDeviceIdentity Device = new(
        "POS_1042_0925",
        "1042",
        "HW-IPAD-1",
        "iPadOS"
    );

    [Fact]
    public void BuildClaims_不带任何员工身份与角色()
    {
        var claims = BrowserExtensionPosDeviceTokenIssuer.BuildClaims(Device);

        var types = claims.Select(claim => claim.Type).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(JwtRegisteredClaimNames.Sub, types);
        Assert.DoesNotContain("userId", types);
        Assert.DoesNotContain("userGuid", types);
        Assert.DoesNotContain(ClaimTypes.NameIdentifier, types);
        Assert.DoesNotContain(ClaimTypes.Name, types);
        Assert.DoesNotContain(ClaimTypes.Role, types);
        Assert.DoesNotContain("sessionId", types);
        Assert.Contains(claims, claim => claim.Type == "token_use" && claim.Value == "browser_extension");
        Assert.Contains(claims, claim =>
            claim.Type == BrowserExtensionPosDevicePrincipal.StoreCodeClaim && claim.Value == "1042");
    }

    [Fact]
    public void Issue_签发五分钟令牌且能还原设备身份()
    {
        var now = new DateTimeOffset(2026, 11, 26, 1, 0, 0, TimeSpan.Zero);
        var issuer = new BrowserExtensionPosDeviceTokenIssuer(CreateJwtConfiguration(), new FixedTimeProvider(now));

        var issued = issuer.Issue(Device);

        Assert.Equal(now.UtcDateTime.AddMinutes(5), issued.ExpiresAtUtc);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);
        Assert.Equal("test-issuer", jwt.Issuer);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(jwt.Claims, "test"));
        Assert.True(BrowserExtensionPosDevicePrincipal.TryGetIdentity(principal, out var restored));
        Assert.Equal(Device, restored);
    }

    [Fact]
    public void 普通员工扩展令牌不会被当成设备()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("userId", "user-1"), new Claim("token_use", "browser_extension")],
            "test"));

        Assert.False(BrowserExtensionPosDevicePrincipal.IsPosDevice(user));
        Assert.False(BrowserExtensionPosDevicePrincipal.TryGetIdentity(user, out _));
    }

    [Fact]
    public void 设备声明缺字段时不认()
    {
        var claims = BrowserExtensionPosDeviceTokenIssuer.BuildClaims(Device)
            .Where(claim => claim.Type != BrowserExtensionPosDevicePrincipal.HardwareIdClaim);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        Assert.True(BrowserExtensionPosDevicePrincipal.IsPosDevice(principal));
        Assert.False(BrowserExtensionPosDevicePrincipal.TryGetIdentity(principal, out _));
    }

    [Theory]
    [InlineData((int)DeviceStatus.启用, "iPadOS", "1042", true)]
    [InlineData((int)DeviceStatus.启用, "AndroidTablet", "1042", true)]
    [InlineData((int)DeviceStatus.启用, "ipados", "1042", true)]
    [InlineData((int)DeviceStatus.启用, "Windows", "1042", false)]
    [InlineData((int)DeviceStatus.启用, "Android", "1042", false)]
    [InlineData((int)DeviceStatus.启用, "iPadOS", "", false)]
    [InlineData((int)DeviceStatus.待确认, "iPadOS", "1042", false)]
    public void ToActiveIdentity_只认启用的大屏收银设备(
        int status,
        string deviceSystem,
        string storeCode,
        bool expected
    )
    {
        var row = new POSM_设备注册信息表
        {
            系统设备编号 = "POS_1042_0925",
            分店代码 = storeCode,
            设备硬件识别码 = "HW-IPAD-1",
            设备系统 = deviceSystem,
            设备状态 = status,
        };

        var identity = BrowserExtensionPosDeviceDirectory.ToActiveIdentity(row);

        Assert.Equal(expected, identity != null);
        if (expected)
        {
            Assert.Equal("1042", identity!.StoreCode);
        }
    }

    [Fact]
    public async Task AccessService_设备只能访问绑定分店且不查任何员工数据()
    {
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        var scope = new Mock<ICurrentUserManageableStoreScopeService>(MockBehavior.Strict);
        var userService = new Mock<IUserService>(MockBehavior.Strict);
        var service = new BrowserExtensionAccessService(
            authorization.Object,
            scope.Object,
            userService.Object
        );
        var device = CreateDevicePrincipal();

        Assert.True(await service.CanAccessAsync(device));
        Assert.True(await service.CanAccessAsync(device, "1042"));
        Assert.True(await service.CanAccessAsync(device, " 1042 "));
        Assert.False(await service.CanAccessAsync(device, "1002"));
        Assert.Equal(new[] { "1042" }, await service.GetRelatedStoreCodesAsync(device));

        authorization.VerifyNoOtherCalls();
        scope.VerifyNoOtherCalls();
        userService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PermissionHandler_设备即使带权限声明也一律拒绝且不查角色()
    {
        var roleService = new Mock<IRoleService>(MockBehavior.Strict);
        var handler = CreatePermissionHandler(roleService);
        var identity = new ClaimsIdentity(BrowserExtensionPosDeviceTokenIssuer.BuildClaims(Device), "test");
        identity.AddClaim(new Claim("permission", Permissions.SalesDashboard.SalesDetailView));
        identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
        var requirement = new PermissionRequirement(Permissions.SalesDashboard.SalesDetailView);
        var context = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(identity),
            resource: null
        );

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        roleService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("/api/react/v1/browser-extension/stores", true)]
    [InlineData("/api/react/v1/browser-extension/barcode-lookup", true)]
    [InlineData("/api/users", false)]
    [InlineData("/api/Auth/current", false)]
    [InlineData("/api/attendance/punch", false)]
    public async Task ScopeMiddleware_设备令牌只能进订货助手接口(string path, bool allowed)
    {
        var nextCalled = false;
        var middleware = new BrowserExtensionTokenScopeMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var claims = BrowserExtensionPosDeviceTokenIssuer.BuildClaims(Device).ToList();
        claims.Add(new Claim(
            JwtRegisteredClaimNames.Exp,
            DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString()));
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        };
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(allowed, nextCalled);
        if (!allowed)
        {
            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        }
    }

    [Fact]
    public void CaptureRateLimit_按设备分区并记设备为操作者()
    {
        var context = new DefaultHttpContext { User = CreateDevicePrincipal() };

        Assert.Equal("pos-device:1042:POS_1042_0925", BrowserExtensionCaptureRateLimits.ResolvePartitionKey(context));
        Assert.Equal("pos-device:1042:POS_1042_0925", BrowserExtensionPosDevicePrincipal.GetActorName(context.User));
    }

    [Fact]
    public async Task InternalController_设备不合格时拒绝签发()
    {
        var directory = new Mock<IBrowserExtensionPosDeviceDirectory>();
        directory
            .Setup(item => item.FindActiveAsync("POS_1042_0925", "1042", "HW-IPAD-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((BrowserExtensionPosDeviceIdentity?)null);
        var issuer = new Mock<IBrowserExtensionPosDeviceTokenIssuer>(MockBehavior.Strict);
        var controller = new InternalBrowserExtensionPosDeviceTokensController(
            directory.Object,
            issuer.Object,
            Mock.Of<ILogger<InternalBrowserExtensionPosDeviceTokensController>>()
        );

        var result = await controller.Issue(
            new PosDeviceSupplierAssistantTokenRequest("POS_1042_0925", "1042", "HW-IPAD-1"),
            CancellationToken.None
        );

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        var body = Assert.IsType<ApiResponse<PosDeviceSupplierAssistantTokenResponse>>(objectResult.Value);
        Assert.Equal(InternalBrowserExtensionPosDeviceTokensController.DeviceNotEligibleErrorCode, body.ErrorCode);
        issuer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InternalController_分店以登记记录为准()
    {
        // 登记记录里的分店写法（去空格后）才是令牌里的分店，请求里的大小写 / 空格不会带进令牌。
        var directory = new Mock<IBrowserExtensionPosDeviceDirectory>();
        directory
            .Setup(item => item.FindActiveAsync(" POS_1042_0925 ", "1042", "HW-IPAD-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Device);
        var issuer = new Mock<IBrowserExtensionPosDeviceTokenIssuer>();
        issuer
            .Setup(item => item.Issue(Device))
            .Returns(new BrowserExtensionPosDeviceIssuedToken("jwt", new DateTime(2026, 11, 26, 1, 5, 0, DateTimeKind.Utc)));
        var controller = new InternalBrowserExtensionPosDeviceTokensController(
            directory.Object,
            issuer.Object,
            Mock.Of<ILogger<InternalBrowserExtensionPosDeviceTokensController>>()
        );

        var result = await controller.Issue(
            new PosDeviceSupplierAssistantTokenRequest(" POS_1042_0925 ", "1042", "HW-IPAD-1"),
            CancellationToken.None
        );

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ApiResponse<PosDeviceSupplierAssistantTokenResponse>>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal("jwt", body.Data!.AccessToken);
        Assert.Equal("1042", body.Data.StoreCode);
        Assert.Equal("POS_1042_0925", body.Data.DeviceCode);
    }

    [Fact]
    public void ServiceToken_新用途只授予签发设备令牌这一个范围()
    {
        var field = typeof(ServiceApiTokenService).GetField(
            "PurposeScopes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var map = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string[]>>(field!.GetValue(null));

        Assert.Equal(
            new[] { ServiceApiScopes.IssuePosDeviceSupplierAssistantTokens },
            map[ServiceApiTokenPurposes.PosSupplierAssistantTokenIssuer]);
    }

    [Fact]
    public async Task TokenValidated_普通员工令牌不经过设备分支()
    {
        var identity = new ClaimsIdentity([new Claim("userId", "user-1")], "test");
        var directory = new Mock<IBrowserExtensionPosDeviceDirectory>(MockBehavior.Strict);

        var result = await BrowserExtensionPosDevicePrincipal.ValidateOnTokenValidatedAsync(
            new ClaimsPrincipal(identity), identity, () => directory.Object, CancellationToken.None);

        Assert.Null(result);
        directory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TokenValidated_设备仍启用时放行并剥掉角色权限()
    {
        var identity = new ClaimsIdentity(BrowserExtensionPosDeviceTokenIssuer.BuildClaims(Device), "test");
        identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
        identity.AddClaim(new Claim("permission", Permissions.OrderFront.View));
        var directory = new Mock<IBrowserExtensionPosDeviceDirectory>();
        directory
            .Setup(item => item.FindActiveAsync("POS_1042_0925", "1042", "HW-IPAD-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Device);

        var result = await BrowserExtensionPosDevicePrincipal.ValidateOnTokenValidatedAsync(
            new ClaimsPrincipal(identity), identity, () => directory.Object, CancellationToken.None);

        Assert.True(result);
        Assert.DoesNotContain(identity.Claims, claim => claim.Type == ClaimTypes.Role);
        Assert.DoesNotContain(identity.Claims, claim => claim.Type == "permission");
    }

    [Fact]
    public async Task TokenValidated_设备停用或换店后立即拒绝()
    {
        var identity = new ClaimsIdentity(BrowserExtensionPosDeviceTokenIssuer.BuildClaims(Device), "test");
        var disabled = new Mock<IBrowserExtensionPosDeviceDirectory>();
        disabled
            .Setup(item => item.FindActiveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BrowserExtensionPosDeviceIdentity?)null);
        var moved = new Mock<IBrowserExtensionPosDeviceDirectory>();
        moved
            .Setup(item => item.FindActiveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Device with { StoreCode = "1002" });

        Assert.False(await BrowserExtensionPosDevicePrincipal.ValidateOnTokenValidatedAsync(
            new ClaimsPrincipal(identity), identity, () => disabled.Object, CancellationToken.None));
        Assert.False(await BrowserExtensionPosDevicePrincipal.ValidateOnTokenValidatedAsync(
            new ClaimsPrincipal(identity), identity, () => moved.Object, CancellationToken.None));
    }

    [Fact]
    public void Program_设备复核排在员工会话校验之前()
    {
        var program = File.ReadAllText(Path.Combine(FindBackendRoot(), "BlazorApp.Api", "Program.cs"));
        var deviceIndex = program.IndexOf("ValidateOnTokenValidatedAsync", StringComparison.Ordinal);
        var userIndex = program.IndexOf("令牌缺少用户标识", StringComparison.Ordinal);

        Assert.True(deviceIndex > 0, "Program.cs 缺少设备令牌复核");
        Assert.True(deviceIndex < userIndex, "设备令牌复核必须排在取员工 userGuid 之前");
    }

    private static string FindBackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "BlazorApp.Api", "Program.cs")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("找不到 services/backend 目录");
    }

    private static ClaimsPrincipal CreateDevicePrincipal() =>
        new(new ClaimsIdentity(BrowserExtensionPosDeviceTokenIssuer.BuildClaims(Device), "test"));

    private static IConfiguration CreateJwtConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-signing-key-test-signing-key-0123456789",
                ["Jwt:Issuer"] = "test-issuer",
                ["Jwt:Audience"] = "test-audience",
            })
            .Build();

    private static PermissionAuthorizationHandler CreatePermissionHandler(Mock<IRoleService> roleService)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(provider => provider.GetService(typeof(IRoleService)))
            .Returns(roleService.Object);
        var scope = new Mock<IServiceScope>();
        scope.SetupGet(item => item.ServiceProvider).Returns(serviceProvider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(factory => factory.CreateScope()).Returns(scope.Object);
        return new PermissionAuthorizationHandler(
            scopeFactory.Object,
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ILogger<PermissionAuthorizationHandler>>(),
            Options.Create(new AppUpdatePolicyOptions())
        );
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
