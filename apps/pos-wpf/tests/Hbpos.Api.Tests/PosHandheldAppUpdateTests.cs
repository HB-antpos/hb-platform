using System.Reflection;
using System.Security.Claims;
using System.Net;
using System.Text;
using System.Text.Json;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.AppUpdates;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class PosHandheldAppUpdateTests
{
    [Fact]
    public void Controller_uses_independent_device_authenticated_routes()
    {
        var route = typeof(PosHandheldAppUpdateController).GetCustomAttribute<RouteAttribute>();
        var authorize = typeof(PosHandheldAppUpdateController).GetCustomAttribute<AuthorizeAttribute>();
        var ota = typeof(PosHandheldAppUpdateController)
            .GetMethod(nameof(PosHandheldAppUpdateController.CheckOta))!
            .GetCustomAttribute<HttpGetAttribute>();

        Assert.Equal("api/v1/app-updates/pos-handheld", route!.Template);
        Assert.Equal(DeviceAuthConstants.Scheme, authorize!.AuthenticationSchemes);
        Assert.Equal("ota", ota!.Template);
        Assert.DoesNotContain(
            typeof(PosHandheldAppUpdateController)
                .GetMethod(nameof(PosHandheldAppUpdateController.Check))!
                .GetParameters(),
            parameter => string.Equals(parameter.Name, "storeCode", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Theory]
    [InlineData("iOS")]
    [InlineData("Android")]
    public async Task Native_decision_uses_authenticated_store_and_platform_claims(string platform)
    {
        var gateway = new RecordingGateway
        {
            NativeDecision = new PosHandheldNativeUpdateResponse(
                State: "required",
                PolicyVersion: "7",
                Platform: platform,
                Required: true,
                LatestVersion: "2.0.0",
                LatestBuild: "200",
                MinimumSupportedVersion: "1.5.0",
                Distribution: platform == "Android" ? "apk" : "app-store",
                DownloadUrl: platform == "Android"
                    ? "https://downloads.example/handheld.apk"
                    : "https://apps.apple.com/au/app/id123456789",
                FileSize: platform == "Android" ? 1234 : null,
                Sha256: platform == "Android" ? new string('a', 64) : null,
                PackageName: platform == "Android" ? "com.hbweb.poshandheld" : null,
                SigningCertificateSha256: platform == "Android" ? new string('b', 64) : null,
                BundleIdentifier: platform == "iOS" ? "com.hbweb.poshandheld" : null,
                AppStoreId: platform == "iOS" ? "123456789" : null,
                ReleaseMessage: "upgrade")
        };
        var controller = CreateController(gateway, "0247", platform);

        var result = await controller.Check("1.0.0", "100", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var envelope = Assert.IsType<ApiResult<PosHandheldNativeUpdateResponse>>(ok.Value);
        Assert.True(envelope.Success);
        Assert.Equal("0247", gateway.NativeRequest!.StoreCode);
        Assert.Equal(platform, gateway.NativeRequest.Platform);
        Assert.Equal("2.0.0", envelope.Data!.LatestVersion);
    }

    [Theory]
    [InlineData("iOS", null, "true")]
    [InlineData("iOS", false, "false")]
    [InlineData("Android", null, "true")]
    [InlineData("Android", false, "false")]
    public async Task Native_decision_exposes_transaction_permission_without_changing_legacy_body(
        string platform,
        bool? allowTransactions,
        string expectedHeader)
    {
        var gateway = new RecordingGateway
        {
            NativeDecision = CreateNoUpdateDecision(platform),
        };
        var controller = CreateController(
            gateway,
            "0247",
            platform,
            allowTransactions);

        var result = await controller.Check("1.0.0", "100", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var envelope = Assert.IsType<ApiResult<PosHandheldNativeUpdateResponse>>(ok.Value);
        var body = JsonSerializer.SerializeToElement(
            envelope.Data,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(body.TryGetProperty("enabled", out _));
        Assert.Equal(
            expectedHeader,
            controller.Response.Headers["X-HBPOS-Allow-Transactions"].ToString());
    }

    [Fact]
    public async Task Ota_decision_uses_claim_scope_and_generic_update_identity()
    {
        var gateway = new RecordingGateway
        {
            OtaDecision = new PosHandheldOtaUpdateResponse(
                State: "optional",
                PolicyVersion: "9",
                AppKey: "pos-handheld",
                ProjectName: "hb-pos-handheld",
                Platform: "Android",
                Required: false,
                Channel: "production",
                RuntimeVersion: "1.0.0",
                UpdateId: "android-update-9",
                UpdateGroupId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                ReleaseMessage: null)
        };
        var controller = CreateController(gateway, "0247", "Android");

        var result = await controller.CheckOta(
            "1.0.0",
            "android-update-8",
            null,
            CancellationToken.None
        );

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var envelope = Assert.IsType<ApiResult<PosHandheldOtaUpdateResponse>>(ok.Value);
        Assert.Equal("0247", gateway.OtaRequest!.StoreCode);
        Assert.Equal("Android", gateway.OtaRequest.Platform);
        Assert.Equal("android-update-9", envelope.Data!.UpdateId);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("iPadOS")]
    [InlineData("AndroidTablet")]
    [InlineData("watchOS")]
    public async Task Non_handheld_platform_claim_fails_closed(string platform)
    {
        var gateway = new RecordingGateway();
        var controller = CreateController(gateway, "0247", platform);

        var result = await controller.Check("1.0.0", "100", CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var envelope = Assert.IsType<ApiResult<PosHandheldNativeUpdateResponse>>(badRequest.Value);
        Assert.False(envelope.Success);
        Assert.Null(gateway.NativeRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("00")]
    [InlineData("01")]
    [InlineData("1.2")]
    [InlineData(" 1 ")]
    [InlineData("1 ")]
    [InlineData("\t1")]
    [InlineData("9007199254740992")]
    [InlineData("10000000000000000")]
    public async Task Native_check_rejects_invalid_current_build_without_forwarding(string? build)
    {
        var gateway = new RecordingGateway();
        var controller = CreateController(gateway, "0247", "Android");

        var result = await controller.Check("1.0.0", build, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var envelope = Assert.IsType<ApiResult<PosHandheldNativeUpdateResponse>>(badRequest.Value);
        Assert.False(envelope.Success);
        Assert.Null(gateway.NativeRequest);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("9007199254740991")]
    public async Task Native_check_forwards_canonical_current_build(string build)
    {
        var gateway = new RecordingGateway();
        var controller = CreateController(gateway, "0247", "Android");

        await controller.Check("1.0.0", build, CancellationToken.None);

        Assert.Equal(build, gateway.NativeRequest!.Build);
    }

    [Fact]
    public async Task Http_gateway_calls_independent_native_path_with_read_token()
    {
        var handler = new RecordingHttpHandler(
            """
            {
              "success": true,
              "data": {
                "state": "required",
                "policyVersion": "12",
                "platform": "Android",
                "required": true,
                "latestVersion": "2.0.0",
                "latestBuild": "200",
                "minimumSupportedVersion": "1.5.0",
                "distribution": "apk",
                "downloadUrl": "https://downloads.example/handheld.apk",
                "fileSize": 2048,
                "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "packageName": "com.hbweb.poshandheld",
                "signingCertificateSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "bundleIdentifier": null,
                "appStoreId": null,
                "releaseMessage": "upgrade"
              }
            }
            """
        );
        var gateway = new HttpPosHandheldUpdateDecisionGateway(
            new HttpClient(handler),
            Options.Create(
                new AppUpdateOptions
                {
                    CenterBaseUrl = "https://center.example/",
                    ServiceApiToken = "hbsvc_read_only",
                }
            ),
            NullLogger<HttpPosHandheldUpdateDecisionGateway>.Instance
        );

        var decision = await gateway.GetNativeDecisionAsync(
            new PosHandheldNativeUpdateDecisionRequest(
                "0247",
                "Android",
                "1.0.0",
                "100"
            ),
            CancellationToken.None
        );

        Assert.NotNull(decision);
        Assert.Equal(
            "https://center.example/api/internal/app-update-decisions/pos-handheld/native",
            handler.RequestUri
        );
        Assert.Equal("Bearer hbsvc_read_only", handler.Authorization);
        Assert.Contains("\"storeCode\":\"0247\"", handler.RequestBody);
        Assert.Contains("\"platform\":\"Android\"", handler.RequestBody);
    }

    [Fact]
    public async Task Http_gateway_rejects_response_missing_verification_identity()
    {
        var handler = new RecordingHttpHandler(
            """
            {
              "success": true,
              "data": {
                "state": "required",
                "policyVersion": "12",
                "platform": "Android",
                "required": true,
                "latestVersion": "2.0.0",
                "latestBuild": "200",
                "minimumSupportedVersion": "1.5.0",
                "distribution": "apk",
                "downloadUrl": "https://downloads.example/handheld.apk",
                "fileSize": 2048,
                "sha256": null,
                "packageName": "com.hbweb.poshandheld",
                "signingCertificateSha256": null,
                "bundleIdentifier": null,
                "appStoreId": null,
                "releaseMessage": null
              }
            }
            """
        );
        var gateway = new HttpPosHandheldUpdateDecisionGateway(
            new HttpClient(handler),
            Options.Create(
                new AppUpdateOptions
                {
                    CenterBaseUrl = "https://center.example/",
                    ServiceApiToken = "hbsvc_read_only",
                }
            ),
            NullLogger<HttpPosHandheldUpdateDecisionGateway>.Instance
        );

        var decision = await gateway.GetNativeDecisionAsync(
            new PosHandheldNativeUpdateDecisionRequest(
                "0247",
                "Android",
                "1.0.0",
                "100"
            ),
            CancellationToken.None
        );

        Assert.Null(decision);
    }

    [Theory]
    [InlineData("iOS", "0")]
    [InlineData("Android", "0")]
    [InlineData("iOS", "00")]
    [InlineData("Android", "00")]
    [InlineData("iOS", "01")]
    [InlineData("Android", "01")]
    [InlineData("iOS", "9007199254740992")]
    [InlineData("Android", "9007199254740992")]
    [InlineData("iOS", "10000000000000000")]
    [InlineData("Android", "10000000000000000")]
    public async Task Http_gateway_rejects_noncanonical_or_unsafe_latest_build(
        string platform,
        string latestBuild)
    {
        var gateway = CreateHttpGateway(CreateNativeDecisionJson(platform, latestBuild));

        var decision = await gateway.GetNativeDecisionAsync(
            new PosHandheldNativeUpdateDecisionRequest("0247", platform, "1.0.0", "100"),
            CancellationToken.None
        );

        Assert.Null(decision);
    }

    [Theory]
    [InlineData("iOS", "1")]
    [InlineData("Android", "1")]
    [InlineData("iOS", "9007199254740991")]
    [InlineData("Android", "9007199254740991")]
    public async Task Http_gateway_accepts_canonical_javascript_safe_latest_build(
        string platform,
        string latestBuild)
    {
        var gateway = CreateHttpGateway(CreateNativeDecisionJson(platform, latestBuild));

        var decision = await gateway.GetNativeDecisionAsync(
            new PosHandheldNativeUpdateDecisionRequest("0247", platform, "1.0.0", "100"),
            CancellationToken.None
        );

        Assert.NotNull(decision);
        Assert.Equal(latestBuild, decision.LatestBuild);
    }

    [Fact]
    public async Task Http_gateway_accepts_ios_app_store_url_with_exact_id_segment()
    {
        var gateway = CreateHttpGateway(
            CreateIosNativeDecisionJson(
                "required",
                true,
                "app-store",
                "https://apps.apple.com/au/app/hb-pos-handheld/id123456789",
                "com.hbweb.poshandheld",
                "123456789"
            )
        );

        var decision = await gateway.GetNativeDecisionAsync(
            new PosHandheldNativeUpdateDecisionRequest("0247", "iOS", "1.0.0", "100"),
            CancellationToken.None
        );

        Assert.NotNull(decision);
        Assert.True(decision.Required);
    }

    [Theory]
    [InlineData("https://apps.apple.com/au/app/id987654321", "com.hbweb.poshandheld", "123456789")]
    [InlineData("https://apps.apple.com/au/app/id0123456789", "com.hbweb.poshandheld", "123456789")]
    [InlineData("https://apps.apple.com/au/app/product-id123456789", "com.hbweb.poshandheld", "123456789")]
    [InlineData("https://apps.apple.com/au/app/hb-pos-handheld", "com.hbweb.poshandheld", "123456789")]
    [InlineData("https://apps.apple.com/au/app/id123456789/reviews", "com.hbweb.poshandheld", "123456789")]
    [InlineData("https://apps.apple.com/au/app/id123456789", "com.example.other-handheld", "123456789")]
    [InlineData("https://apps.apple.com/au/app/id123456789", "com.hbweb.poshandheld", "12345abc")]
    [InlineData("https://apps.apple.com/au/app/id1234", "com.hbweb.poshandheld", "1234")]
    [InlineData("https://apps.apple.com/au/app/id123456789012345678901", "com.hbweb.poshandheld", "123456789012345678901")]
    public async Task Http_gateway_rejects_ios_identity_mismatch(
        string downloadUrl,
        string bundleIdentifier,
        string appStoreId)
    {
        var gateway = CreateHttpGateway(
            CreateIosNativeDecisionJson(
                "optional",
                false,
                "app-store",
                downloadUrl,
                bundleIdentifier,
                appStoreId
            )
        );

        var decision = await gateway.GetNativeDecisionAsync(
            new PosHandheldNativeUpdateDecisionRequest("0247", "iOS", "1.0.0", "100"),
            CancellationToken.None
        );

        Assert.Null(decision);
    }

    [Fact]
    public async Task Http_gateway_rejects_required_testflight_response()
    {
        var gateway = CreateHttpGateway(
            CreateIosNativeDecisionJson(
                "required",
                true,
                "testflight",
                "https://testflight.apple.com/join/AbCdEf12",
                "com.hbweb.poshandheld",
                "123456789"
            )
        );

        var decision = await gateway.GetNativeDecisionAsync(
            new PosHandheldNativeUpdateDecisionRequest("0247", "iOS", "1.0.0", "100"),
            CancellationToken.None
        );

        Assert.Null(decision);
    }

    [Theory]
    [InlineData("https://testflight.apple.com/not-join/AbCdEf12")]
    [InlineData("https://testflight.apple.com/join/AbCdEf12/extra")]
    [InlineData("https://testflight.apple.com/join/AbCdEf12?source=other")]
    public async Task Http_gateway_rejects_noncanonical_testflight_response(
        string downloadUrl)
    {
        var gateway = CreateHttpGateway(
            CreateIosNativeDecisionJson(
                "optional",
                false,
                "testflight",
                downloadUrl,
                "com.hbweb.poshandheld",
                "123456789"
            )
        );

        var decision = await gateway.GetNativeDecisionAsync(
            new PosHandheldNativeUpdateDecisionRequest("0247", "iOS", "1.0.0", "100"),
            CancellationToken.None
        );

        Assert.Null(decision);
    }

    [Fact]
    public async Task Http_gateway_accepts_optional_canonical_testflight_response()
    {
        var gateway = CreateHttpGateway(
            CreateIosNativeDecisionJson(
                "optional",
                false,
                "testflight",
                "https://testflight.apple.com/join/AbCdEf12",
                "com.hbweb.poshandheld",
                "123456789"
            )
        );

        var decision = await gateway.GetNativeDecisionAsync(
            new PosHandheldNativeUpdateDecisionRequest("0247", "iOS", "1.0.0", "100"),
            CancellationToken.None
        );

        Assert.NotNull(decision);
        Assert.False(decision.Required);
    }

    [Fact]
    public async Task Http_gateway_keeps_none_decision_unchanged()
    {
        var gateway = CreateHttpGateway(
            """
            {
              "success": true,
              "data": {
                "state": "none",
                "policyVersion": "none",
                "platform": "iOS",
                "required": false,
                "latestVersion": null,
                "latestBuild": null,
                "minimumSupportedVersion": null,
                "distribution": null,
                "downloadUrl": null,
                "fileSize": null,
                "sha256": null,
                "packageName": null,
                "signingCertificateSha256": null,
                "bundleIdentifier": null,
                "appStoreId": null,
                "releaseMessage": null
              }
            }
            """
        );

        var decision = await gateway.GetNativeDecisionAsync(
            new PosHandheldNativeUpdateDecisionRequest("0247", "iOS", "1.0.0", "100"),
            CancellationToken.None
        );

        Assert.NotNull(decision);
        Assert.Equal("none", decision.State);
        Assert.False(decision.Required);
    }

    [Fact]
    public async Task Wrong_platform_native_decision_is_not_forwarded_and_controller_returns_503()
    {
        var gateway = CreateHttpGateway(
            """
            {
              "success": true,
              "data": {
                "state": "required",
                "policyVersion": "12",
                "platform": "Android",
                "required": true,
                "latestVersion": "2.0.0",
                "latestBuild": "200",
                "minimumSupportedVersion": "1.5.0",
                "distribution": "apk",
                "downloadUrl": "https://downloads.example/handheld.apk",
                "fileSize": 2048,
                "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "packageName": "com.hbweb.poshandheld",
                "signingCertificateSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "bundleIdentifier": null,
                "appStoreId": null,
                "releaseMessage": null
              }
            }
            """
        );
        var controller = CreateController(gateway, "0247", "iOS");

        var result = await controller.Check("1.0.0", "100", CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        var envelope = Assert.IsType<ApiResult<PosHandheldNativeUpdateResponse>>(
            unavailable.Value
        );
        Assert.False(envelope.Success);
        Assert.Null(envelope.Data);
    }

    [Theory]
    [InlineData("iOS", "1.0.0")]
    [InlineData("Android", "2.0.0")]
    public async Task Ota_decision_must_match_requested_platform_and_runtime(
        string responsePlatform,
        string responseRuntime)
    {
        var gateway = CreateHttpGateway(
            $$"""
            {
              "success": true,
              "data": {
                "state": "optional",
                "policyVersion": "12",
                "appKey": "pos-handheld",
                "projectName": "hb-pos-handheld",
                "platform": "{{responsePlatform}}",
                "required": false,
                "channel": "pos-handheld-production",
                "runtimeVersion": "{{responseRuntime}}",
                "updateId": "handheld-update-12",
                "updateGroupId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                "releaseMessage": null
              }
            }
            """
        );

        var decision = await gateway.GetOtaDecisionAsync(
            new PosHandheldOtaUpdateDecisionRequest(
                "0247",
                "Android",
                "1.0.0",
                "handheld-update-11",
                null
            ),
            CancellationToken.None
        );

        Assert.Null(decision);
    }

    [Fact]
    public async Task Ota_decision_matching_requested_platform_and_runtime_is_forwarded()
    {
        var gateway = CreateHttpGateway(
            """
            {
              "success": true,
              "data": {
                "state": "optional",
                "policyVersion": "12",
                "appKey": "pos-handheld",
                "projectName": "hb-pos-handheld",
                "platform": "Android",
                "required": false,
                "channel": "pos-handheld-production",
                "runtimeVersion": "1.0.0",
                "updateId": "handheld-update-12",
                "updateGroupId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                "releaseMessage": null
              }
            }
            """
        );

        var decision = await gateway.GetOtaDecisionAsync(
            new PosHandheldOtaUpdateDecisionRequest(
                "0247",
                "Android",
                " 1.0.0 ",
                "handheld-update-11",
                null
            ),
            CancellationToken.None
        );

        Assert.NotNull(decision);
        Assert.Equal("handheld-update-12", decision.UpdateId);
    }

    [Theory]
    [InlineData("iOS", "pos-handheld-production")]
    [InlineData("iOS", "pos-handheld-production-ios-release-20260827t101500z-a1b2c3")]
    [InlineData("Android", "pos-handheld-production-android-release-20260827t101500z-d4e5f6")]
    public async Task Ota_decision_accepts_only_legacy_or_platform_release_channel(
        string platform,
        string channel)
    {
        var gateway = CreateHttpGateway(CreateOtaDecisionJson(platform, channel));

        var decision = await gateway.GetOtaDecisionAsync(
            new PosHandheldOtaUpdateDecisionRequest(
                "0247",
                platform,
                "1.0.0",
                null,
                null
            ),
            CancellationToken.None
        );

        Assert.NotNull(decision);
        Assert.Equal(channel, decision.Channel);
    }

    [Theory]
    [InlineData("iOS", "pos-handheld-production-android-release-20260827t101500z-a1b2c3")]
    [InlineData("Android", "pos-handheld-production-ios-release-20260827t101500z-a1b2c3")]
    [InlineData("iOS", "pos-handheld-preview-ios-release-20260827t101500z-a1b2c3")]
    [InlineData("iOS", "pos-handheld-production-ios-release-")]
    [InlineData("iOS", "arbitrary-channel")]
    public async Task Ota_decision_rejects_untrusted_or_cross_platform_release_channel(
        string platform,
        string channel)
    {
        var gateway = CreateHttpGateway(CreateOtaDecisionJson(platform, channel));

        var decision = await gateway.GetOtaDecisionAsync(
            new PosHandheldOtaUpdateDecisionRequest(
                "0247",
                platform,
                "1.0.0",
                null,
                null
            ),
            CancellationToken.None
        );

        Assert.Null(decision);
    }

    private static HttpPosHandheldUpdateDecisionGateway CreateHttpGateway(string responseJson) =>
        new(
            new HttpClient(new RecordingHttpHandler(responseJson)),
            Options.Create(
                new AppUpdateOptions
                {
                    CenterBaseUrl = "https://center.example/",
                    ServiceApiToken = "hbsvc_read_only",
                }
            ),
            NullLogger<HttpPosHandheldUpdateDecisionGateway>.Instance
        );

    private static string CreateOtaDecisionJson(string platform, string channel) =>
        $$"""
        {
          "success": true,
          "data": {
            "state": "optional",
            "policyVersion": "12",
            "appKey": "pos-handheld",
            "projectName": "hb-pos-handheld",
            "platform": "{{platform}}",
            "required": false,
            "channel": "{{channel}}",
            "runtimeVersion": "1.0.0",
            "updateId": "handheld-update-12",
            "updateGroupId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            "releaseMessage": null
          }
        }
        """;

    private static string CreateNativeDecisionJson(string platform, string latestBuild) =>
        platform == "Android"
            ? $$"""
            {
              "success": true,
              "data": {
                "state": "required",
                "policyVersion": "12",
                "platform": "Android",
                "required": true,
                "latestVersion": "3.0.0",
                "latestBuild": "{{latestBuild}}",
                "minimumSupportedVersion": null,
                "distribution": "apk",
                "downloadUrl": "https://downloads.example/handheld.apk",
                "fileSize": 2048,
                "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "packageName": "com.hbweb.poshandheld",
                "signingCertificateSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "bundleIdentifier": null,
                "appStoreId": null,
                "releaseMessage": null
              }
            }
            """
            : CreateIosNativeDecisionJson(
                "required",
                true,
                "app-store",
                "https://apps.apple.com/au/app/id123456789",
                "com.hbweb.poshandheld",
                "123456789",
                latestBuild
            );

    private static string CreateIosNativeDecisionJson(
        string state,
        bool required,
        string distribution,
        string downloadUrl,
        string bundleIdentifier,
        string appStoreId,
        string latestBuild = "300") =>
        $$"""
        {
          "success": true,
          "data": {
            "state": "{{state}}",
            "policyVersion": "12",
            "platform": "iOS",
            "required": {{required.ToString().ToLowerInvariant()}},
            "latestVersion": "3.0.0",
            "latestBuild": "{{latestBuild}}",
            "minimumSupportedVersion": null,
            "distribution": "{{distribution}}",
            "downloadUrl": "{{downloadUrl}}",
            "fileSize": null,
            "sha256": null,
            "packageName": null,
            "signingCertificateSha256": null,
            "bundleIdentifier": "{{bundleIdentifier}}",
            "appStoreId": "{{appStoreId}}",
            "releaseMessage": null
          }
        }
        """;

    private static PosHandheldAppUpdateController CreateController(
        IPosHandheldUpdateDecisionGateway gateway,
        string storeCode,
        string platform,
        bool? allowTransactions = null)
    {
        var claims = new List<Claim>
        {
            new(DeviceAuthConstants.StoreCodeClaim, storeCode),
            new(DeviceAuthConstants.DeviceSystemClaim, platform),
        };
        if (allowTransactions.HasValue)
        {
            claims.Add(new Claim(
                DeviceAuthConstants.AllowTransactionsClaim,
                allowTransactions.Value.ToString()));
        }

        return new PosHandheldAppUpdateController(gateway)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            claims,
                            DeviceAuthConstants.Scheme
                        )
                    )
                }
            }
        };
    }

    private static PosHandheldNativeUpdateResponse CreateNoUpdateDecision(string platform) =>
        new(
            State: "none",
            PolicyVersion: "none",
            Platform: platform,
            Required: false,
            LatestVersion: null,
            LatestBuild: null,
            MinimumSupportedVersion: null,
            Distribution: null,
            DownloadUrl: null,
            FileSize: null,
            Sha256: null,
            PackageName: null,
            SigningCertificateSha256: null,
            BundleIdentifier: null,
            AppStoreId: null,
            ReleaseMessage: null);

    private sealed class RecordingGateway : IPosHandheldUpdateDecisionGateway
    {
        public PosHandheldNativeUpdateResponse? NativeDecision { get; init; }

        public PosHandheldOtaUpdateResponse? OtaDecision { get; init; }

        public PosHandheldNativeUpdateDecisionRequest? NativeRequest { get; private set; }

        public PosHandheldOtaUpdateDecisionRequest? OtaRequest { get; private set; }

        public Task<PosHandheldNativeUpdateResponse?> GetNativeDecisionAsync(
            PosHandheldNativeUpdateDecisionRequest request,
            CancellationToken cancellationToken)
        {
            NativeRequest = request;
            return Task.FromResult(NativeDecision);
        }

        public Task<PosHandheldOtaUpdateResponse?> GetOtaDecisionAsync(
            PosHandheldOtaUpdateDecisionRequest request,
            CancellationToken cancellationToken)
        {
            OtaRequest = request;
            return Task.FromResult(OtaDecision);
        }
    }

    private sealed class RecordingHttpHandler(string responseJson) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }

        public string? Authorization { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
