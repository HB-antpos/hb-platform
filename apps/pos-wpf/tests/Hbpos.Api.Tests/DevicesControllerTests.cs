using System.Security.Claims;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using BlazorApp.Shared.Options;

namespace Hbpos.Api.Tests;

public sealed class DevicesControllerTests
{
    [Fact]
    public void DeviceEndpoints_KeepExpectedRoutes()
    {
        Assert.Equal("register", GetHttpPostTemplate(nameof(DevicesController.Register)));
        Assert.Equal("app-review-register", GetHttpPostTemplate(nameof(DevicesController.AppReviewRegister)));
        Assert.Equal("verify", GetHttpPostTemplate(nameof(DevicesController.Verify)));
        Assert.Equal("reregister", GetHttpPostTemplate(nameof(DevicesController.Reregister)));
        Assert.Equal("reset-registration", GetHttpPostTemplate(nameof(DevicesController.ResetRegistration)));
        Assert.Equal("runtime-status", GetHttpPostTemplate(nameof(DevicesController.ReportRuntimeStatus)));
        Assert.Equal(
            CashierAuthorizationPolicies.DeviceRegistration,
            typeof(DevicesController).GetMethod(nameof(DevicesController.Reregister))?
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                .Cast<AuthorizeAttribute>()
                .Single()
                .Policy);
        Assert.Equal(
            CashierAuthorizationPolicies.DeviceRegistrationReset,
            typeof(DevicesController).GetMethod(nameof(DevicesController.ResetRegistration))?
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                .Cast<AuthorizeAttribute>()
                .Single()
                .Policy);
        Assert.NotNull(typeof(DevicesController)
            .GetMethod(nameof(DevicesController.ReportRuntimeStatus))?
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .SingleOrDefault());
        Assert.Empty(typeof(DevicesController)
            .GetMethod(nameof(DevicesController.Register))!
            .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: false));
        var reviewRateLimit = Assert.Single(typeof(DevicesController)
            .GetMethod(nameof(DevicesController.AppReviewRegister))!
            .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: false)
            .Cast<EnableRateLimitingAttribute>());
        Assert.Equal("app-review-device-registration", reviewRateLimit.PolicyName);
    }

    [Fact]
    public async Task Register_ReturnsWrappedResponseWithPendingStatus()
    {
        var expected = new DeviceRegisterResponse(
            "POS_1002_1011",
            "1002",
            "Lutwyche",
            -1,
            false,
            "Device registration is pending approval.");
        var service = new FakeDeviceService { RegisterResponse = expected };
        var controller = new DevicesController(service);
        var request = new DeviceRegisterRequest("1002", "HW-001", "Counter 1");

        var result = await controller.Register(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<DeviceRegisterResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(expected, apiResult.Data);
        Assert.Equal(request, service.LastRegisterRequest);
        Assert.Null(Assert.IsType<DeviceRegisterRequest>(service.LastRegisterRequest).ProvisioningCode);
    }

    [Fact]
    public async Task Register_WhenWindowsLegacyDisabled_ReturnsActivationRequiredWithoutServiceWrite()
    {
        var service = new FakeDeviceService();
        var controller = new DevicesController(
            service,
            Options.Create(new DeviceActivationOptions
            {
                LegacyRegistrationEnabled = new LegacyRegistrationOptions { Windows = false },
            }));

        var result = await controller.Register(
            new DeviceRegisterRequest("S001", "HW-1", DeviceSystem: DeviceSystems.Windows),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResult<DeviceRegisterResponse>>(ok.Value).Data!;
        Assert.False(response.IsAllowed);
        Assert.Equal(DeviceActivationReasonCodes.ActivationCodeRequired, response.ReasonCode);
        Assert.Null(service.LastRegisterRequest);
    }

    [Fact]
    public async Task AppReviewRegister_BypassesLegacyRegistrationFlag()
    {
        var expected = new DeviceRegisterResponse("POS-1", "S001", "Store", 1, true);
        var service = new FakeDeviceService { RegisterResponse = expected };
        var controller = new DevicesController(
            service,
            Options.Create(new DeviceActivationOptions
            {
                LegacyRegistrationEnabled = new LegacyRegistrationOptions { IpadOS = false },
            }));
        var request = new DeviceRegisterRequest(
            "S001", "HW-1", "Review", DeviceSystems.IpadOs, "REVIEW-CODE-123456");

        await controller.AppReviewRegister(request, CancellationToken.None);

        Assert.Same(request, service.LastAppReviewRegisterRequest);
    }

    [Fact]
    public async Task AppReviewRegister_ForwardsProvisioningCodeToDedicatedServicePath()
    {
        var expected = new DeviceRegisterResponse(
            "POS_1042_1011", "1042", "testStore", 1, true, "Device is enabled.", "AUTH");
        var service = new FakeDeviceService { RegisterResponse = expected };
        var controller = new DevicesController(service);
        var request = new DeviceRegisterRequest(
            "1042", "IPAD-REVIEW", "App Review", DeviceSystems.IpadOs, "OPEN-REVIEW-DEVICE");

        var result = await controller.AppReviewRegister(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(Assert.IsType<ApiResult<DeviceRegisterResponse>>(ok.Value).Success);
        Assert.Equal("OPEN-REVIEW-DEVICE", service.LastRegisterRequest?.ProvisioningCode);
    }

    [Fact]
    public async Task Verify_ReturnsWrappedDeniedResponseWithStatus()
    {
        var expected = new DeviceVerifyResponse(
            "POS_1002_1011",
            "1002",
            "Lutwyche",
            -1,
            false,
            "Device registration is pending approval.");
        var service = new FakeDeviceService { VerifyResponse = expected };
        var controller = new DevicesController(service);
        var request = new DeviceVerifyRequest("POS_1002_1011", "1002", "HW-001", "Counter 1");

        var result = await controller.Verify(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<DeviceVerifyResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(expected, apiResult.Data);
        Assert.Same(request, service.LastVerifyRequest);
    }

    [Fact]
    public async Task Reregister_RequiresAuthenticatedDeviceClaims()
    {
        var service = new FakeDeviceService();
        var controller = new DevicesController(service);

        var result = await controller.Reregister(
            new DeviceReregisterRequest("1003", "HW-001", "Counter 1"),
            CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<DeviceReregisterResponse>>(unauthorized.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("DEVICE_AUTH_REQUIRED", apiResult.ErrorCode);
    }

    [Fact]
    public async Task Reregister_InheritsIpadOsFromAuthenticatedDeviceClaim()
    {
        var expected = new DeviceReregisterResponse(
            "POS_1003_1011",
            "1003",
            "New Store",
            -1,
            false,
            "Device registration is pending approval.");
        var service = new FakeDeviceService { ReregisterResponse = expected };
        var controller = new DevicesController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS_1002_0900"),
                        new Claim(DeviceAuthConstants.StoreCodeClaim, "1002"),
                        new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-IPAD"),
                        new Claim(DeviceAuthConstants.DeviceSystemClaim, DeviceSystems.IpadOs),
                    ], DeviceAuthConstants.Scheme)),
                },
            },
        };
        var request = new DeviceReregisterRequest("1003", "HW-IPAD");

        var result = await controller.Reregister(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<DeviceReregisterResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(expected, apiResult.Data);
        Assert.Equal(DeviceSystems.IpadOs, service.LastReregisterContext?.DeviceSystem);
    }

    [Fact]
    public async Task ResetRegistration_UsesOnlyAuthenticatedDeviceAndVerifiedCashierIdentity()
    {
        var operationId = Guid.Parse("36d6605c-1e25-4fd1-b345-bec1c4ffad31");
        var expected = new DeviceRegistrationResetResponse(
            operationId,
            "POS_1042_0247",
            "1042",
            new DateTime(2026, 8, 18, 2, 30, 0, DateTimeKind.Utc));
        var service = new FakeDeviceService { ResetResponse = expected };
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS_1042_0247"),
                new Claim(DeviceAuthConstants.StoreCodeClaim, "1042"),
                new Claim(DeviceAuthConstants.HardwareIdClaim, "INSTALL-1042"),
            ], DeviceAuthConstants.Scheme)),
        };
        httpContext.Items[CashierAuthorizationContext.CashierIdItemKey] = "EMPLOYEE-HGUID";
        var controller = new DevicesController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var result = await controller.ResetRegistration(
            new DeviceRegistrationResetRequest(operationId),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<DeviceRegistrationResetResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(expected, apiResult.Data);
        Assert.Equal("POS_1042_0247", service.LastResetContext?.DeviceCode);
        Assert.Equal("1042", service.LastResetContext?.StoreCode);
        Assert.Equal("INSTALL-1042", service.LastResetContext?.HardwareId);
        Assert.Equal("EMPLOYEE-HGUID", service.LastResetContext?.CashierId);
    }

    [Fact]
    public async Task ResetRegistration_RejectsMissingVerifiedCashierIdentityBeforeServiceWrite()
    {
        var service = new FakeDeviceService();
        var controller = new DevicesController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS_1042_0247"),
                        new Claim(DeviceAuthConstants.StoreCodeClaim, "1042"),
                        new Claim(DeviceAuthConstants.HardwareIdClaim, "INSTALL-1042"),
                    ], DeviceAuthConstants.Scheme)),
                }
            }
        };

        var result = await controller.ResetRegistration(
            new DeviceRegistrationResetRequest(Guid.NewGuid()),
            CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal(
            "CASHIER_AUTH_REQUIRED",
            Assert.IsType<ApiResult<DeviceRegistrationResetResponse>>(unauthorized.Value).ErrorCode);
        Assert.Null(service.LastResetContext);
    }

    [Fact]
    public async Task ReportRuntimeStatus_PassesClientAppVersionToService()
    {
        var service = new FakeDeviceService();
        var controller = CreateRuntimeStatusController(service);

        var result = await controller.ReportRuntimeStatus(
            new DevicesController.DeviceRuntimeStatusRequest(true, "CASHIER-1", "Alice", "1.0.51"),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("1.0.51", service.LastRuntimeStatusAppVersion);
    }

    [Fact]
    public async Task ReportRuntimeStatus_AcceptsOldClientPayloadWithoutAppVersion()
    {
        // 旧版客户端的请求体里没有 appVersion：必须照常成功，并把 null 交给服务层（服务端据此保留旧版本）。
        var service = new FakeDeviceService();
        var controller = CreateRuntimeStatusController(service);

        var result = await controller.ReportRuntimeStatus(
            new DevicesController.DeviceRuntimeStatusRequest(true, null, null),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Null(service.LastRuntimeStatusAppVersion);
    }

    private static DevicesController CreateRuntimeStatusController(FakeDeviceService service) =>
        new(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS_1042_0247"),
                        new Claim(DeviceAuthConstants.StoreCodeClaim, "1042"),
                        new Claim(DeviceAuthConstants.HardwareIdClaim, "INSTALL-1042"),
                    ], DeviceAuthConstants.Scheme)),
                }
            }
        };

    [Fact]
    public void CreateDeviceCode_UsesStoreCodeAndLocalHourMinute()
    {
        var deviceCode = DeviceService.CreateDeviceCode(
            "1009",
            new DateTime(2026, 5, 22, 10, 11, 0));

        Assert.Equal("POS_1009_1011", deviceCode);
    }

    [Fact]
    public void AddHbposApiServices_RegistersDeviceRegistrationRepository()
    {
        var services = new ServiceCollection();

        services.AddHbposApiServices();

        var descriptor = Assert.Single(services, x => x.ServiceType == typeof(IDeviceRegistrationRepository));
        Assert.Equal(typeof(SqlSugarDeviceRegistrationRepository), descriptor.ImplementationType);
    }

    private static string? GetHttpPostTemplate(string methodName)
    {
        return typeof(DevicesController)
            .GetMethod(methodName)?
            .GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
            .Cast<HttpPostAttribute>()
            .Single()
            .Template;
    }

    private sealed class FakeDeviceService : IDeviceService
    {
        public DeviceRegisterResponse? RegisterResponse { get; init; }

        public DeviceVerifyResponse? VerifyResponse { get; init; }

        public DeviceReregisterResponse? ReregisterResponse { get; init; }

        public DeviceRegistrationResetResponse? ResetResponse { get; init; }

        public DeviceRegisterRequest? LastRegisterRequest { get; private set; }

        public DeviceRegisterRequest? LastAppReviewRegisterRequest { get; private set; }

        public DeviceVerifyRequest? LastVerifyRequest { get; private set; }

        public DeviceReregisterRequest? LastReregisterRequest { get; private set; }

        public DeviceReregisterContext? LastReregisterContext { get; private set; }

        public DeviceRegistrationResetContext? LastResetContext { get; private set; }

        public string? LastRuntimeStatusAppVersion { get; private set; }

        public Task<bool> UpdateRuntimeStatusAsync(
            string hardwareId,
            string deviceCode,
            string storeCode,
            bool isOnline,
            string? cashierId,
            string? cashierName,
            string? appVersion,
            CancellationToken cancellationToken)
        {
            LastRuntimeStatusAppVersion = appVersion;
            return Task.FromResult(true);
        }

        public Task<DeviceRegisterResponse> RegisterAsync(
            DeviceRegisterRequest request,
            CancellationToken cancellationToken)
        {
            LastRegisterRequest = request;
            return Task.FromResult(RegisterResponse!);
        }

        public Task<DeviceRegisterResponse> RegisterForAppReviewAsync(
            DeviceRegisterRequest request,
            CancellationToken cancellationToken)
        {
            LastRegisterRequest = request;
            LastAppReviewRegisterRequest = request;
            return Task.FromResult(RegisterResponse!);
        }

        public Task<DeviceVerifyResponse> VerifyAsync(
            DeviceVerifyRequest request,
            CancellationToken cancellationToken)
        {
            LastVerifyRequest = request;
            return Task.FromResult(VerifyResponse!);
        }

        public Task<DeviceReregisterResponse> ReregisterAsync(
            DeviceReregisterRequest request,
            DeviceReregisterContext currentDevice,
            CancellationToken cancellationToken)
        {
            LastReregisterRequest = request;
            LastReregisterContext = currentDevice;
            return Task.FromResult(ReregisterResponse!);
        }

        public Task<DeviceRegistrationResetResponse> ResetRegistrationAsync(
            DeviceRegistrationResetRequest request,
            DeviceRegistrationResetContext currentDevice,
            CancellationToken cancellationToken)
        {
            LastResetContext = currentDevice;
            return Task.FromResult(ResetResponse!);
        }
    }
}
