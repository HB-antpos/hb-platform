using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.AppUpdates;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Json;

namespace Hbpos.Api.Tests;

public sealed class PosIpadAppUpdateContractsTests
{
    [Fact]
    public void Response_keeps_the_unlisted_app_update_fields_separate_from_wpf_release_contract()
    {
        var response = new PosIpadAppUpdateResponse(
            Enabled: true,
            MinimumSupportedVersion: "1.0.0",
            LatestVersion: "1.0.1",
            ForceUpdate: false,
            AppStoreUrl: "https://apps.apple.com/unlisted/example",
            ReleaseMessage: "Preview rollout");

        Assert.True(response.Enabled);
        Assert.Equal("1.0.1", response.LatestVersion);
        Assert.False(response.ForceUpdate);
    }

    [Fact]
    public void Response_json_keeps_the_exact_six_field_contract()
    {
        var response = new PosIpadAppUpdateResponse(
            Enabled: true,
            MinimumSupportedVersion: "1.5.0.42",
            LatestVersion: "1.5.0.88",
            ForceUpdate: true,
            AppStoreUrl: "https://apps.apple.com/au/app/example/id123",
            ReleaseMessage: "同版本构建升级");

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(
                response,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var fields = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "appStoreUrl",
                "enabled",
                "forceUpdate",
                "latestVersion",
                "minimumSupportedVersion",
                "releaseMessage"
            },
            fields);
    }

    [Fact]
    public void Controller_uses_the_separate_authenticated_ipad_update_route()
    {
        var route = typeof(PosIpadAppUpdateController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Single();
        var authorize = typeof(PosIpadAppUpdateController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single();

        Assert.Equal("api/v1/app-updates/pos-ipad", route.Template);
        Assert.Equal("HbposDevice", authorize.AuthenticationSchemes);
    }

    [Fact]
    public async Task Missing_transaction_permission_claim_defaults_to_allowed()
    {
        var controller = CreateAuthenticatedController(DeviceSystems.IpadOs);

        var response = GetResponse(await controller.Check(
            "1.0.0",
            "1",
            "1.0.0",
            CancellationToken.None));

        Assert.True(response.Enabled);
    }

    [Fact]
    public async Task Disabled_transaction_permission_is_returned_in_the_existing_enabled_field()
    {
        var controller = CreateAuthenticatedController(
            DeviceSystems.IpadOs,
            allowTransactions: false);

        var response = GetResponse(await controller.Check(
            "1.0.0",
            "1",
            "1.0.0",
            CancellationToken.None));

        Assert.False(response.Enabled);
    }

    [Fact]
    public async Task Central_native_decision_preserves_disabled_transaction_permission()
    {
        var controller = AuthenticateController(
            new PosIpadAppUpdateController(
                Options.Create(new PosIpadOptions()),
                Options.Create(new AppUpdateOptions { CentralPolicyEnabled = true }),
                new StubUpdateDecisionGateway()),
            DeviceSystems.IpadOs,
            allowTransactions: false);

        var response = GetResponse(await controller.Check(
            "1.0.0",
            "1",
            "1.0.0",
            CancellationToken.None));

        Assert.False(response.Enabled);
    }

    [Fact]
    public void Missing_configuration_uses_safe_first_testflight_defaults()
    {
        var controller = CreateController(new PosIpadOptions());

        var response = GetResponse(controller.Check("0.2.0", "2", "0.2.0"));

        Assert.Equal("0.0.0", response.MinimumSupportedVersion);
        Assert.Equal("0.2.0", response.LatestVersion);
        Assert.False(response.ForceUpdate);
    }

    [Fact]
    public void Controller_forces_update_when_marketing_version_and_build_are_below_minimum()
    {
        var controller = CreateController(new PosIpadOptions
        {
            MinimumSupportedVersion = "1.5.0.42",
            LatestVersion = "1.6.0"
        });

        var response = GetResponse(controller.Check("1.5.0", "41", "1.5.0"));

        Assert.True(response.ForceUpdate);
    }

    [Fact]
    public void Controller_uses_runtime_version_when_marketing_version_is_missing()
    {
        var controller = CreateController(new PosIpadOptions
        {
            MinimumSupportedVersion = "1.5.0",
            LatestVersion = "1.6.0"
        });

        var response = GetResponse(controller.Check(null, null, "1.4.9"));

        Assert.True(response.ForceUpdate);
    }

    [Fact]
    public void Configured_force_update_stops_for_clients_already_at_latest_version()
    {
        var controller = CreateController(new PosIpadOptions
        {
            MinimumSupportedVersion = "1.5.0",
            LatestVersion = "1.6.0",
            ForceUpdate = true
        });

        var response = GetResponse(controller.Check("1.6.0", "77", "1.6.0"));

        Assert.False(response.ForceUpdate);
    }

    [Fact]
    public void Missing_or_invalid_client_version_fails_closed_when_minimum_is_configured()
    {
        var controller = CreateController(new PosIpadOptions
        {
            MinimumSupportedVersion = "1.5.0"
        });

        var response = GetResponse(controller.Check("invalid", "invalid", "invalid"));

        Assert.True(response.ForceUpdate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AndroidTablet_native_check_returns_neutral_result_without_querying_ipad_policy(
        bool allowTransactions)
    {
        var gateway = new CountingNativeGateway();
        var controller = AuthenticateController(
            new PosIpadAppUpdateController(
                Options.Create(new PosIpadOptions
                {
                    MinimumSupportedVersion = "9.9.9",
                    LatestVersion = "9.9.9",
                    AppStoreUrl = "https://apps.apple.com/app/id1",
                    ReleaseMessage = "iPad only"
                }),
                Options.Create(new AppUpdateOptions { CentralPolicyEnabled = true }),
                gateway),
            DeviceSystems.AndroidTablet,
            allowTransactions);

        var response = GetResponse(await controller.Check(
            "0.0.1",
            "1",
            "0.0.1",
            CancellationToken.None));

        // 安卓平板不能套用 iPad App Store 策略：不查中央网关，也不带出 iPad 配置的版本 / 商店地址。
        Assert.Equal(0, gateway.NativeCalls);
        Assert.Equal(allowTransactions, response.Enabled);
        Assert.Null(response.MinimumSupportedVersion);
        Assert.Null(response.LatestVersion);
        Assert.False(response.ForceUpdate);
        Assert.Null(response.AppStoreUrl);
        Assert.Null(response.ReleaseMessage);
    }

    [Theory]
    [InlineData(DeviceSystems.AndroidTablet)]
    [InlineData("androidtablet")]
    public async Task AndroidTablet_is_forbidden_from_ipad_ota_endpoint(string deviceSystem)
    {
        var gateway = new CapturingOtaGateway();
        var controller = AuthenticateController(
            new PosIpadAppUpdateController(
                Options.Create(new PosIpadOptions()),
                Options.Create(new AppUpdateOptions()),
                gateway),
            deviceSystem);

        var otaResult = await controller.CheckOta(
            "1.0.0",
            null,
            null,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(otaResult.Result);
        Assert.Null(gateway.LastOtaRequest);
    }

    [Fact]
    public async Task Noncanonical_android_tablet_claim_is_forbidden_from_native_check()
    {
        var controller = CreateAuthenticatedController("androidtablet");

        var nativeResult = await controller.Check(
            "1.0.0",
            "1",
            "1.0.0",
            CancellationToken.None);

        Assert.IsType<ForbidResult>(nativeResult.Result);
    }

    [Theory]
    [InlineData(DeviceSystems.Ios)]
    [InlineData(DeviceSystems.Android)]
    [InlineData(DeviceSystems.Windows)]
    [InlineData("ipados")]
    [InlineData(null)]
    public async Task Non_iPadOS_device_scope_is_forbidden_from_both_update_endpoints(
        string? deviceSystem)
    {
        var controller = CreateAuthenticatedController(deviceSystem);

        var nativeResult = await controller.Check(
            "1.0.0",
            "1",
            "1.0.0",
            CancellationToken.None);
        var otaResult = await controller.CheckOta(
            "1.0.0",
            null,
            null,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(nativeResult.Result);
        Assert.IsType<ForbidResult>(otaResult.Result);
    }

    [Fact]
    public async Task Ota_check_forwards_device_identity_claims_to_central_decision()
    {
        var gateway = new CapturingOtaGateway();
        var controller = new PosIpadAppUpdateController(
            Options.Create(new PosIpadOptions()),
            Options.Create(new AppUpdateOptions()),
            gateway);
        var claims = new List<Claim>
        {
            new(DeviceAuthConstants.StoreCodeClaim, " STORE-1 "),
            new(DeviceAuthConstants.DeviceSystemClaim, DeviceSystems.IpadOs),
            new(DeviceAuthConstants.DeviceCodeClaim, "IPAD-0001"),
            new(DeviceAuthConstants.HardwareIdClaim, "hw-abc")
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, DeviceAuthConstants.Scheme))
            }
        };

        await controller.CheckOta("1.0.0", "update-1", "group-1", CancellationToken.None);

        // 中心按「设备 → 分店 → 基线」决策，必须拿到认证 claim 里的设备身份（客户端自报的值不可信）。
        var request = Assert.IsType<PosIpadOtaUpdateDecisionRequest>(gateway.LastOtaRequest);
        Assert.Equal("STORE-1", request.StoreCode);
        Assert.Equal("IPAD-0001", request.DeviceCode);
        Assert.Equal("hw-abc", request.HardwareId);
        Assert.Equal("update-1", request.CurrentUpdateId);
    }

    private sealed class CapturingOtaGateway : IPosIpadUpdateDecisionGateway
    {
        public PosIpadOtaUpdateDecisionRequest? LastOtaRequest { get; private set; }

        public Task<PosIpadNativeUpdateDecision?> GetNativeDecisionAsync(
            PosIpadNativeUpdateDecisionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<PosIpadNativeUpdateDecision?>(null);

        public Task<PosIpadOtaUpdateResponse?> GetOtaDecisionAsync(
            PosIpadOtaUpdateDecisionRequest request,
            CancellationToken cancellationToken)
        {
            LastOtaRequest = request;
            return Task.FromResult<PosIpadOtaUpdateResponse?>(null);
        }
    }

    private sealed class CountingNativeGateway : IPosIpadUpdateDecisionGateway
    {
        public int NativeCalls { get; private set; }

        public Task<PosIpadNativeUpdateDecision?> GetNativeDecisionAsync(
            PosIpadNativeUpdateDecisionRequest request,
            CancellationToken cancellationToken)
        {
            NativeCalls++;
            return Task.FromResult<PosIpadNativeUpdateDecision?>(new(
                State: "required",
                PolicyVersion: "policy-1",
                MinimumSupportedVersion: "9.9.9",
                LatestVersion: "9.9.9",
                AppStoreUrl: "https://apps.apple.com/app/id1",
                ReleaseMessage: "iPad only"));
        }

        public Task<PosIpadOtaUpdateResponse?> GetOtaDecisionAsync(
            PosIpadOtaUpdateDecisionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<PosIpadOtaUpdateResponse?>(null);
    }

    private static PosIpadAppUpdateController CreateController(PosIpadOptions configuration)
        => new(Options.Create(configuration));

    private static PosIpadAppUpdateController CreateAuthenticatedController(
        string? deviceSystem,
        bool? allowTransactions = null)
        => AuthenticateController(
            CreateController(new PosIpadOptions()),
            deviceSystem,
            allowTransactions);

    private static PosIpadAppUpdateController AuthenticateController(
        PosIpadAppUpdateController controller,
        string? deviceSystem,
        bool? allowTransactions = null)
    {
        var claims = new List<Claim>
        {
            new(DeviceAuthConstants.StoreCodeClaim, "STORE-1")
        };
        if (deviceSystem is not null)
        {
            claims.Add(new Claim(DeviceAuthConstants.DeviceSystemClaim, deviceSystem));
        }
        if (allowTransactions.HasValue)
        {
            claims.Add(new Claim(
                DeviceAuthConstants.AllowTransactionsClaim,
                allowTransactions.Value.ToString()));
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    claims,
                    DeviceAuthConstants.Scheme))
            }
        };
        return controller;
    }

    private sealed class StubUpdateDecisionGateway : IPosIpadUpdateDecisionGateway
    {
        public Task<PosIpadNativeUpdateDecision?> GetNativeDecisionAsync(
            PosIpadNativeUpdateDecisionRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<PosIpadNativeUpdateDecision?>(new(
                State: "none",
                PolicyVersion: "policy-1",
                MinimumSupportedVersion: null,
                LatestVersion: null,
                AppStoreUrl: null,
                ReleaseMessage: null));
        }

        public Task<PosIpadOtaUpdateResponse?> GetOtaDecisionAsync(
            PosIpadOtaUpdateDecisionRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<PosIpadOtaUpdateResponse?>(null);
        }
    }

    private static PosIpadAppUpdateResponse GetResponse(
        ActionResult<ApiResult<PosIpadAppUpdateResponse>> actionResult)
    {
        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var envelope = Assert.IsType<ApiResult<PosIpadAppUpdateResponse>>(ok.Value);
        return Assert.IsType<PosIpadAppUpdateResponse>(envelope.Data);
    }
}
