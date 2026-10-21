using System.Text.Json;
using BlazorApp.Shared.Security;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Devices;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Reflection;
using System.Security.Cryptography;
using System.ComponentModel.DataAnnotations;
using Hbpos.Api.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

public sealed class DeviceActivationCodeContractTests
{
    [Fact]
    public void Codec_GeneratesCanonicalOneTimeSecretAndMatchesOnlyItsOwnHash()
    {
        var material = DeviceActivationCodeCodec.Create();

        Assert.Matches("^HBDEV1-[0-9A-HJKMNP-TV-Z]{26}-[0-9A-HJKMNP-TV-Z]{26}$", material.ActivationCode);
        Assert.True(DeviceActivationCodeCodec.TryParse(material.ActivationCode, out var parsed));
        Assert.Equal(material.GrantId, parsed.GrantId);
        Assert.True(DeviceActivationCodeCodec.Matches(material.SecretHash, parsed.Secret));
        Assert.True(DeviceActivationCodeCodec.TryParse(
            $"  {material.ActivationCode.ToLowerInvariant()[..20]}\n{material.ActivationCode.ToLowerInvariant()[20..]}  ",
            out var normalized));
        Assert.Equal(material.GrantId, normalized.GrantId);
        Assert.False(DeviceActivationCodeCodec.TryParse(
            material.ActivationCode.Replace('-', '\u00a0'),
            out _));
        var asciiLongSLookalikeTarget = material.ActivationCode[..^1] + "S";
        Assert.True(DeviceActivationCodeCodec.TryParse(asciiLongSLookalikeTarget, out _));
        Assert.False(DeviceActivationCodeCodec.TryParse(
            asciiLongSLookalikeTarget[..^1] + "\u017f",
            out _));

        var other = DeviceActivationCodeCodec.Create();
        Assert.True(DeviceActivationCodeCodec.TryParse(other.ActivationCode, out var otherParsed));
        Assert.False(DeviceActivationCodeCodec.Matches(material.SecretHash, otherParsed.Secret));
    }

    [Fact]
    public void Codec_DetectsCompleteReservedCodeInsidePublicMetadataWithoutReturningTheSecret()
    {
        var activationCode = DeviceActivationCodeCodec.Create().ActivationCode;

        Assert.True(DeviceActivationCodeCodec.ContainsReservedActivationCode(
            $"Counter {activationCode.ToLowerInvariant()} backup"));
        Assert.True(DeviceActivationCodeCodec.ContainsReservedActivationCode(
            $"Counter {activationCode[..24]}\n{activationCode[24..]} backup"));
        Assert.False(DeviceActivationCodeCodec.ContainsReservedActivationCode("Counter HBDEV1-CODE"));
        Assert.Equal(
            "[REDACTED]",
            DeviceActivationCodeCodec.RedactReservedActivationMetadata(
                $"Counter {activationCode} backup"));
    }

    [Fact]
    public void PosResponses_DefensivelyRedactReservedCodeFromHistoricalStoreName()
    {
        var activationCode = DeviceActivationCodeCodec.Create().ActivationCode;

        Assert.Equal(
            "[REDACTED]",
            DeviceActivationCodeService.SanitizeStoreNameForResponse(
                $"Store {activationCode}"));
    }

    [Fact]
    public void TerminalName_IsBoundedToKeepProductionRemarkWithinNvarchar500()
    {
        foreach (var requestType in new[]
                 {
                     typeof(DeviceActivationCodeRedeemRequest),
                     typeof(DeviceActivationCodeRebindRequest),
                 })
        {
            var terminalName = requestType.GetConstructors().Single().GetParameters()
                .Single(parameter => parameter.Name == "TerminalName");
            var length = Assert.Single(
                terminalName.GetCustomAttributes(typeof(StringLengthAttribute), inherit: true)
                    .Cast<StringLengthAttribute>());

            Assert.Equal(200, length.MaximumLength);
        }
    }

    [Fact]
    public async Task Service_RejectsReservedCodeInPublicMetadataBeforeAnyDatabaseRead()
    {
        var activationCode = DeviceActivationCodeCodec.Create().ActivationCode;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MainConnection"] =
                    "Server=127.0.0.1,1;Database=NeverConnectMain;User Id=none;Password=none;Connect Timeout=1",
                ["ConnectionStrings:PosmConnection"] =
                    "Server=127.0.0.1,1;Database=NeverConnectPosm;User Id=none;Password=none;Connect Timeout=1",
            })
            .Build();
        var context = new HbposSqlSugarContext(
            configuration,
            NullLogger<HbposSqlSugarContext>.Instance);
        var service = new DeviceActivationCodeService(
            context,
            configuration,
            NullLogger<DeviceActivationCodeService>.Instance);

        var redeemWithReservedHardware = await service.RedeemAsync(
            new DeviceActivationCodeRedeemRequest(
                activationCode,
                $"HW-{activationCode}",
                "Counter",
                DeviceSystems.Windows),
            CancellationToken.None);
        var redeemWithReservedTerminal = await service.RedeemAsync(
            new DeviceActivationCodeRedeemRequest(
                activationCode,
                "HW-1",
                $"Counter {activationCode}",
                DeviceSystems.Windows),
            CancellationToken.None);
        var rebindWithReservedHardware = await service.RebindAsync(
            new DeviceActivationCodeRebindRequest(activationCode, "Counter"),
            new DeviceActivationRebindContext(
                "POS-S001-1",
                "S001",
                $"HW-{activationCode}",
                DeviceSystems.Windows),
            CancellationToken.None);
        var rebindWithReservedTerminal = await service.RebindAsync(
            new DeviceActivationCodeRebindRequest(
                activationCode,
                $"Counter {activationCode}"),
            new DeviceActivationRebindContext(
                "POS-S001-1",
                "S001",
                "HW-1",
                DeviceSystems.Windows),
            CancellationToken.None);

        foreach (var response in new[]
                 {
                     redeemWithReservedHardware,
                     redeemWithReservedTerminal,
                     rebindWithReservedHardware,
                     rebindWithReservedTerminal,
                 })
        {
            Assert.Equal(DeviceActivationReasonCodes.DeviceStateConflict, response.ReasonCode);
        }
    }

    [Fact]
    public void UtcWireNormalization_RestoresSqlServerDatetime2KindAndSerializesWithZ()
    {
        var databaseValue = new DateTime(2026, 8, 27, 1, 2, 3, DateTimeKind.Unspecified);

        var normalized = DeviceActivationCodeCodec.NormalizeUtcForWire(databaseValue);
        var json = JsonSerializer.Serialize(
            new { expiresAtUtc = normalized },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        Assert.Contains("2026-08-27T01:02:03Z", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PosDtos_UseTheApprovedCamelCaseWireContract()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = new DeviceActivationCodeRedeemRequest(
            "HBDEV1-00000000000000000000000000-00000000000000000000000000",
            "HW-1",
            "Counter 1",
            DeviceSystems.Windows);

        var json = JsonSerializer.Serialize(request, options);

        Assert.Contains("\"activationCode\"", json, StringComparison.Ordinal);
        Assert.Contains("\"hardwareId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"terminalName\"", json, StringComparison.Ordinal);
        Assert.Contains("\"deviceSystem\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("storeCode", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActivationController_UsesFixedRoutesAndRebindKeepsCashierPolicy()
    {
        var controllerRoute = Assert.Single(
            typeof(DeviceActivationCodeController)
                .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
                .Cast<RouteAttribute>());
        Assert.Equal("api/v1/devices/activation-code", controllerRoute.Template);

        Assert.Equal("preview", PostTemplate(nameof(DeviceActivationCodeController.Preview)));
        Assert.Equal("redeem", PostTemplate(nameof(DeviceActivationCodeController.Redeem)));
        Assert.Equal("rebind", PostTemplate(nameof(DeviceActivationCodeController.Rebind)));

        var policy = Assert.Single(
            typeof(DeviceActivationCodeController)
                .GetMethod(nameof(DeviceActivationCodeController.Rebind))!
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                .Cast<AuthorizeAttribute>());
        Assert.Equal(CashierAuthorizationPolicies.DeviceRegistration, policy.Policy);

        var recoveryHeader = Assert.Single(
            typeof(DeviceActivationCodeController)
                .GetMethod(nameof(DeviceActivationCodeController.Redeem))!
                .GetParameters(),
            parameter => parameter.GetCustomAttribute<FromHeaderAttribute>() != null);
        Assert.Equal(typeof(bool), recoveryHeader.ParameterType);
        Assert.False(recoveryHeader.HasDefaultValue && Equals(recoveryHeader.DefaultValue, true));
        Assert.Equal(
            "X-HBPOS-Activation-Recovery-Only",
            recoveryHeader.GetCustomAttribute<FromHeaderAttribute>()!.Name);
    }

    [Fact]
    public async Task Redeem_RejectsActivationCodeInPublicMetadataWithoutCallingService()
    {
        var activationCode = DeviceActivationCodeCodec.Create().ActivationCode;
        var service = new FakeActivationService();
        var controller = new DeviceActivationCodeController(service);
        var requests = new[]
        {
            new DeviceActivationCodeRedeemRequest(
                activationCode,
                activationCode,
                "Counter 1",
                DeviceSystems.Windows),
            new DeviceActivationCodeRedeemRequest(
                activationCode,
                "HW-1",
                $"Counter {activationCode}",
                DeviceSystems.Windows),
        };

        foreach (var request in requests)
        {
            var result = await controller.Redeem(request, CancellationToken.None);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
            Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
            Assert.Equal("Invalid device metadata.", problem.Title);
        }

        Assert.Equal(0, service.RedeemCalls);
    }

    [Fact]
    public async Task Rebind_RejectsActivationCodeInTerminalNameWithoutCallingService()
    {
        var activationCode = DeviceActivationCodeCodec.Create().ActivationCode;
        var service = new FakeActivationService();
        var controller = new DeviceActivationCodeController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(new[]
            {
                new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-S001-1"),
                new Claim(DeviceAuthConstants.StoreCodeClaim, "S001"),
                new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-1"),
                new Claim(DeviceAuthConstants.DeviceSystemClaim, DeviceSystems.Windows),
            }, "Test"));

        var result = await controller.Rebind(
            new DeviceActivationCodeRebindRequest(activationCode, $"Counter {activationCode}"),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Invalid device metadata.", problem.Title);
        Assert.Equal(0, service.RebindCalls);
    }

    [Fact]
    public void AnonymousActivationRateLimit_UsesHttp429()
    {
        var options = new RateLimiterOptions();

        DeviceActivationRateLimitOptions.Configure(options);

        Assert.Equal(StatusCodes.Status429TooManyRequests, options.RejectionStatusCode);
    }

    [Fact]
    public void AnonymousActivationRateLimit_AllowsTwentyRequestsPerTenMinutesPerIp()
    {
        Assert.Equal(20, DeviceActivationRateLimitOptions.PermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(10), DeviceActivationRateLimitOptions.Window);
    }

    [Fact]
    public async Task AnonymousActivationRateLimit_RejectionCarriesRetryAfterSeconds()
    {
        var options = new RateLimiterOptions();
        DeviceActivationRateLimitOptions.Configure(options);
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        await options.OnRejected!(
            new OnRejectedContext
            {
                HttpContext = httpContext,
                Lease = new RetryAfterLease(TimeSpan.FromSeconds(125.2))
            },
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status429TooManyRequests, httpContext.Response.StatusCode);
        // 向上取整到秒，客户端据此提示「请等几分钟」。
        Assert.Equal("126", httpContext.Response.Headers.RetryAfter.ToString());
    }

    private sealed class RetryAfterLease(TimeSpan retryAfter) : System.Threading.RateLimiting.RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames =>
            new[] { System.Threading.RateLimiting.MetadataName.RetryAfter.Name };

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == System.Threading.RateLimiting.MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter;
                return true;
            }

            metadata = null;
            return false;
        }
    }

    [Fact]
    public void ReasonCodes_AreExactlyTheApprovedNonLeakingSet()
    {
        var values = typeof(DeviceActivationReasonCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(field => Assert.IsType<string>(field.GetRawConstantValue()))
            .OrderBy(value => value)
            .ToArray();

        Assert.Equal(new[]
        {
            "ACTIVATED",
            "ACTIVATION_CODE_NOT_AVAILABLE",
            "ACTIVATION_CODE_REQUIRED",
            "ACTIVATION_PLATFORM_MISMATCH",
            "ACTIVATION_RECOVERED",
            "DEVICE_ALREADY_REGISTERED",
            "DEVICE_STATE_CONFLICT",
            "STORE_UNAVAILABLE",
            "TARGET_STORE_UNCHANGED",
        }.OrderBy(value => value), values);
    }

    [Fact]
    public async Task Rebind_ForwardsOnlyClaimIdentityAndCanReturnRecoveredCredentials()
    {
        var expected = new DeviceActivationCodeRedeemResponse(
            "POS-S002-1",
            "S002",
            "Target",
            1,
            true,
            "Device activation credentials were recovered.",
            "NEW-AUTH",
            DeviceActivationReasonCodes.ActivationRecovered);
        var service = new FakeActivationService { RebindResponse = expected };
        var controller = new DeviceActivationCodeController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(new[]
            {
                new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-S001-1"),
                new Claim(DeviceAuthConstants.StoreCodeClaim, "S001"),
                new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-1"),
                new Claim(DeviceAuthConstants.DeviceSystemClaim, DeviceSystems.Windows),
            }, "Test"));
        var request = new DeviceActivationCodeRebindRequest("HBDEV1-CODE", "Counter");

        var result = await controller.Rebind(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, Assert.IsType<Hbpos.Contracts.Common.ApiResult<DeviceActivationCodeRedeemResponse>>(ok.Value).Data);
        Assert.Equal(
            new DeviceActivationRebindContext("POS-S001-1", "S001", "HW-1", DeviceSystems.Windows),
            service.LastRebindContext);
    }

    [Fact]
    public async Task PosStartup_UsesTheSharedActivationGrantSchemaAfterRuntimeColumns()
    {
        var executor = new RecordingSchemaExecutor();
        var initializer = new SqlSugarDeviceRuntimeStatusSchemaInitializer(executor);

        await initializer.InitializeAsync();

        Assert.Equal(2, executor.Scripts.Count);
        Assert.Equal(
            BlazorApp.Shared.Models.POSM.DeviceActivationCodeSchema.EnsureSql,
            executor.Scripts[1]);
    }

    [Fact]
    public void RecoveryAuthorization_IsNarrowedToConsumedRebindAndExactOldAndNewRows()
    {
        var sql = DeviceActivationRecoveryAuthorizationService.RecoveryAuthorizationSql;

        Assert.Contains("[ConsumptionKind] = 'Rebind'", sql, StringComparison.Ordinal);
        Assert.Contains("[PreviousStoreCode]", sql, StringComparison.Ordinal);
        Assert.Contains("[PreviousDeviceCode]", sql, StringComparison.Ordinal);
        Assert.Contains("source.[设备状态] = 0", sql, StringComparison.Ordinal);
        Assert.Contains("target.[设备状态] = 1", sql, StringComparison.Ordinal);
        Assert.Equal(new[] { "store", "grant", "hardware" }, DeviceActivationCodeService.LockOrderForTests);
    }

    [Fact]
    public void Redeem_UnavailableGrantDoesNotLeakPlatformMetadata()
    {
        var now = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc);
        var expired = Grant(expiresAtUtc: now.AddMinutes(-1));
        var revoked = Grant(expiresAtUtc: now.AddMinutes(30));
        revoked.RevokedAtUtc = now.AddMinutes(-2);

        var expiredDecision = DeviceActivationCodeService.EvaluateRedeemGrantGate(
            expired,
            "HW-1",
            platformIsValid: true,
            DeviceSystems.Android,
            now);
        var revokedDecision = DeviceActivationCodeService.EvaluateRedeemGrantGate(
            revoked,
            "HW-1",
            platformIsValid: false,
            string.Empty,
            now);

        Assert.Equal(DeviceActivationReasonCodes.NotAvailable, expiredDecision.ReasonCode);
        Assert.Equal(DeviceActivationReasonCodes.NotAvailable, revokedDecision.ReasonCode);
    }

    [Fact]
    public void Rebind_UnavailableGrantDoesNotLeakPlatformOrTargetStoreMetadata()
    {
        var now = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc);
        var expiredSameStore = Grant(
            storeCode: "S001",
            expiresAtUtc: now.AddSeconds(-1));

        var decision = DeviceActivationCodeService.EvaluateRebindGrantGate(
            expiredSameStore,
            "HW-1",
            DeviceSystems.Android,
            "S001",
            "POS-S001-1",
            now);

        Assert.Equal(DeviceActivationReasonCodes.NotAvailable, decision.ReasonCode);
    }

    [Fact]
    public void ConsumedGrant_OnlyExactOwnerCanRecover()
    {
        var now = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc);
        var consumed = Grant(expiresAtUtc: now.AddMinutes(-1));
        consumed.ConsumedAtUtc = now.AddMinutes(-2);
        consumed.ConsumedHardwareId = "HW-1";
        consumed.ConsumedDeviceSystem = DeviceSystems.Windows;

        var owner = DeviceActivationCodeService.EvaluateRedeemGrantGate(
            consumed,
            "HW-1",
            platformIsValid: true,
            DeviceSystems.Windows,
            now);
        var otherHardware = DeviceActivationCodeService.EvaluateRedeemGrantGate(
            consumed,
            "HW-2",
            platformIsValid: true,
            DeviceSystems.Windows,
            now);

        Assert.True(owner.IsRecovery);
        Assert.Equal(DeviceActivationReasonCodes.NotAvailable, otherHardware.ReasonCode);
    }

    public static TheoryData<string> SupportedDeviceSystems => new()
    {
        DeviceSystems.Windows,
        DeviceSystems.IpadOs,
        DeviceSystems.Android,
        DeviceSystems.Ios,
    };

    [Theory]
    [MemberData(nameof(SupportedDeviceSystems))]
    public void AvailableRedeemGate_AllowsEverySupportedPlatformAndRejectsMismatch(
        string deviceSystem)
    {
        var now = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc);
        var grant = Grant(expiresAtUtc: now.AddMinutes(30));
        grant.DeviceSystem = deviceSystem;
        var material = DeviceActivationCodeCodec.Create();
        grant.SecretHash = material.SecretHash;
        Assert.True(DeviceActivationCodeCodec.TryParse(material.ActivationCode, out var parsed));

        var previewAllowed = DeviceActivationCodeService.EvaluatePreviewGrantGate(
            grant,
            parsed.Secret,
            deviceSystem,
            now);
        var previewMismatched = DeviceActivationCodeService.EvaluatePreviewGrantGate(
            grant,
            parsed.Secret,
            deviceSystem == DeviceSystems.Windows ? DeviceSystems.Android : DeviceSystems.Windows,
            now);
        var allowed = DeviceActivationCodeService.EvaluateRedeemGrantGate(
            grant,
            "HW-1",
            platformIsValid: true,
            deviceSystem,
            now,
            recoveryOnly: false);
        var mismatched = DeviceActivationCodeService.EvaluateRedeemGrantGate(
            grant,
            "HW-1",
            platformIsValid: true,
            deviceSystem == DeviceSystems.Windows ? DeviceSystems.Android : DeviceSystems.Windows,
            now,
            recoveryOnly: false);

        Assert.True(previewAllowed.IsAllowed);
        Assert.Equal(deviceSystem, previewAllowed.DeviceSystem);
        Assert.Equal(DeviceActivationReasonCodes.PlatformMismatch, previewMismatched.ReasonCode);
        Assert.True(allowed.IsAllowed);
        Assert.False(allowed.IsRecovery);
        Assert.Equal(DeviceActivationReasonCodes.PlatformMismatch, mismatched.ReasonCode);
    }

    [Fact]
    public void RecoveryOnlyRedeem_NeverConsumesAnAvailableGrant()
    {
        var now = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc);
        var available = Grant(expiresAtUtc: now.AddMinutes(30));

        var decision = DeviceActivationCodeService.EvaluateRedeemGrantGate(
            available,
            "HW-1",
            platformIsValid: true,
            DeviceSystems.Windows,
            now,
            recoveryOnly: true);

        Assert.False(decision.IsAllowed);
        Assert.False(decision.IsRecovery);
        Assert.Equal(DeviceActivationReasonCodes.NotAvailable, decision.ReasonCode);
    }

    [Fact]
    public void Preview_UnknownCodeWithInvalidPlatformDoesNotLeakPlatformReason()
    {
        var now = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc);
        var material = DeviceActivationCodeCodec.Create();
        Assert.True(DeviceActivationCodeCodec.TryParse(material.ActivationCode, out var parsed));
        var available = Grant(expiresAtUtc: now.AddMinutes(30));
        available.SecretHash = material.SecretHash;

        var unknown = DeviceActivationCodeService.EvaluatePreviewGrantGate(
            grant: null,
            parsed.Secret,
            "Plan9",
            now);
        var validCodeWrongPlatform = DeviceActivationCodeService.EvaluatePreviewGrantGate(
            available,
            parsed.Secret,
            "Plan9",
            now);

        Assert.Equal(DeviceActivationReasonCodes.NotAvailable, unknown.ReasonCode);
        Assert.Equal(DeviceActivationReasonCodes.PlatformMismatch, validCodeWrongPlatform.ReasonCode);
    }

    [Fact]
    public void ConsumedAuthorizationHash_OnlyMatchesTheAuthorizationIssuedAtConsumption()
    {
        var hash = DeviceActivationCodeService.HashAuthorizationCode("AUTH-AT-CONSUME");
        try
        {
            Assert.Equal(32, hash.Length);
            Assert.True(DeviceActivationCodeService.MatchesAuthorizationCode(
                hash,
                "AUTH-AT-CONSUME"));
            Assert.False(DeviceActivationCodeService.MatchesAuthorizationCode(
                hash,
                "AUTH-ROTATED-LATER"));
            Assert.False(DeviceActivationCodeService.MatchesAuthorizationCode(
                null,
                "AUTH-AT-CONSUME"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static string? PostTemplate(string methodName) =>
        typeof(DeviceActivationCodeController)
            .GetMethod(methodName)!
            .GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
            .Cast<HttpPostAttribute>()
            .Single()
            .Template;

    private static DeviceActivationCodeGrant Grant(
        string storeCode = "S002",
        DateTime? expiresAtUtc = null) =>
        new()
        {
            GrantId = Guid.NewGuid(),
            SecretHash = new byte[32],
            StoreCode = storeCode,
            DeviceSystem = DeviceSystems.Windows,
            CreatedAtUtc = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc),
            CreatedBy = "TEST",
            ExpiresAtUtc = expiresAtUtc
                ?? new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc),
        };

    private sealed class FakeActivationService : IDeviceActivationCodeService
    {
        public DeviceActivationCodeRedeemResponse? RebindResponse { get; init; }
        public DeviceActivationRebindContext? LastRebindContext { get; private set; }
        public int RedeemCalls { get; private set; }
        public int RebindCalls { get; private set; }

        public Task<DeviceActivationCodePreviewResponse> PreviewAsync(
            DeviceActivationCodePreviewRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DeviceActivationCodeRedeemResponse> RedeemAsync(
            DeviceActivationCodeRedeemRequest request,
            bool recoveryOnly,
            CancellationToken cancellationToken)
        {
            RedeemCalls++;
            return Task.FromResult(new DeviceActivationCodeRedeemResponse(
                "POS-S001-1",
                "S001",
                "Store",
                1,
                true));
        }

        public Task<DeviceActivationCodeRedeemResponse> RebindAsync(
            DeviceActivationCodeRebindRequest request,
            DeviceActivationRebindContext currentDevice,
            CancellationToken cancellationToken)
        {
            RebindCalls++;
            LastRebindContext = currentDevice;
            return Task.FromResult(RebindResponse!);
        }
    }

    private sealed class RecordingSchemaExecutor : IDeviceRuntimeStatusSchemaSqlExecutor
    {
        public List<string> Scripts { get; } = [];

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            Scripts.Add(sql);
            return Task.CompletedTask;
        }
    }
}
