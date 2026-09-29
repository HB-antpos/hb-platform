using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Encodings.Web;
using BlazorApp.Shared.Security;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.EmergencyLogin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using SqlSugar;

namespace Hbpos.Api.Tests;

public sealed class EmergencyLoginPublicKeysTests
{
    [Fact]
    public void Controller_exposes_device_authenticated_public_key_routes_without_secret_fields()
    {
        var controllerType = typeof(EmergencyLoginPublicKeysController);
        Assert.Equal(
            "api/v1/emergency-login/public-keys",
            controllerType.GetCustomAttribute<RouteAttribute>()?.Template);
        var authorize = Assert.IsType<AuthorizeAttribute>(
            controllerType.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Equal(DeviceAuthConstants.Scheme, authorize.AuthenticationSchemes);
        Assert.Null(controllerType.GetCustomAttribute<AllowAnonymousAttribute>());

        Assert.Equal("", controllerType.GetMethod(nameof(EmergencyLoginPublicKeysController.Get))?
            .GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Equal("ack", controllerType.GetMethod(nameof(EmergencyLoginPublicKeysController.Acknowledge))?
            .GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Null(controllerType.GetMethod(nameof(EmergencyLoginPublicKeysController.Get))?
            .GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Null(controllerType.GetMethod(nameof(EmergencyLoginPublicKeysController.Acknowledge))?
            .GetCustomAttribute<AllowAnonymousAttribute>());

        var responseProperties = typeof(EmergencyLoginPublicKeyPackage).GetProperties()
            .Concat(typeof(EmergencyLoginPublicKey).GetProperties())
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(responseProperties, name =>
            name.Contains("Private", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Protected", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Encrypted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Public_key_json_uses_pem_wire_name()
    {
        var json = JsonSerializer.Serialize(CreatePackage(7), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"pem\":\"pem\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("publicKeyPem", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_returns_304_for_matching_etag()
    {
        var package = CreatePackage(7);
        var controller = CreateController(new StubService(package));
        controller.Request.Headers.IfNoneMatch = "\"emergency-login-keys-v7\"";

        var result = await controller.Get(CancellationToken.None);

        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_http_request_is_rejected_by_authorization_middleware()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(EmergencyLoginPublicKeysController).Assembly);
        builder.Services
            .AddAuthentication(DeviceAuthConstants.Scheme)
            .AddScheme<AuthenticationSchemeOptions, UnauthenticatedHandler>(
                DeviceAuthConstants.Scheme,
                _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IEmergencyLoginPublicKeyDistributionService>(
            new StubService(CreatePackage(7)));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();

        using var response = await app.GetTestClient().GetAsync(
            "/api/v1/emergency-login/public-keys");

        Assert.Equal(StatusCodes.Status401Unauthorized, (int)response.StatusCode);
    }

    [Fact]
    public async Task Ack_uses_authenticated_device_claims_and_rejects_future_version()
    {
        var service = new StubService(CreatePackage(7))
        {
            AckResult = EmergencyLoginPublicKeyAckResult.FutureVersion
        };
        var controller = CreateController(service);

        var result = await controller.Acknowledge(
            new EmergencyLoginPublicKeyAckRequest(8),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("POS-01", service.LastDevice?.DeviceCode);
        Assert.Equal("S001", service.LastDevice?.StoreCode);
        Assert.Equal("hardware-1", service.LastDevice?.HardwareId);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
    }

    [Fact]
    public async Task Ack_stale_version_returns_conflict_with_current_version()
    {
        var service = new StubService(CreatePackage(8))
        {
            AckResult = EmergencyLoginPublicKeyAckResult.StaleIgnored
        };
        var controller = CreateController(service);

        var result = await controller.Acknowledge(
            new EmergencyLoginPublicKeyAckRequest(7),
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var response = Assert.IsType<EmergencyLoginPublicKeyAckResponse>(conflict.Value);
        Assert.Equal(8, response.Version);
        Assert.Equal(
            DateTime.Parse("2026-07-15T02:00:00Z").ToUniversalTime(),
            response.ServerTimeUtc);
    }

    [Fact]
    public async Task Ack_success_returns_current_server_time_anchor()
    {
        var service = new StubService(CreatePackage(7));
        var controller = CreateController(service);

        var result = await controller.Acknowledge(
            new EmergencyLoginPublicKeyAckRequest(7),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<EmergencyLoginPublicKeyAckResponse>(ok.Value);
        Assert.Equal(7, response.Version);
        Assert.Equal(
            DateTime.Parse("2026-07-15T02:00:00Z").ToUniversalTime(),
            response.ServerTimeUtc);
    }

    [Fact]
    public async Task Distribution_returns_only_non_retired_keys_and_ignores_stale_ack()
    {
        var repository = new InMemoryRepository
        {
            Snapshot = new EmergencyLoginPublicKeySetSnapshot(
                4,
                "KACTIVE",
                DateTime.Parse("2026-07-15T01:00:00Z").ToUniversalTime(),
                [
                    new("KSTAGED", "ES256", "staged-pem", "AA", "Staged"),
                    new("KACTIVE", "ES256", "active-pem", "BB", "Active"),
                    new("KOLD", "ES256", "old-pem", "CC", "Retired")
                ])
        };
        var service = new EmergencyLoginPublicKeyDistributionService(repository, new FixedTimeProvider());

        var package = await service.GetAsync(CancellationToken.None);
        var ack = await service.AcknowledgeAsync(
            new EmergencyLoginDeviceIdentity("POS-01", "S001", "hardware-1"),
            3,
            CancellationToken.None);

        Assert.Equal(["KSTAGED", "KACTIVE"], package.Keys.Select(key => key.Kid));
        Assert.Equal(EmergencyLoginPublicKeyAckResult.StaleIgnored, ack);
        Assert.Single(repository.AckRequests);
        Assert.Equal(3, repository.AckRequests[0].Version);
    }

    [Fact]
    public async Task Ack_delegates_current_version_check_and_idempotency_to_atomic_repository_operation()
    {
        var repository = new InMemoryRepository
        {
            Snapshot = CreateSnapshot(7),
            AckResult = EmergencyLoginPublicKeyAckResult.Acknowledged
        };
        var service = new EmergencyLoginPublicKeyDistributionService(repository, new FixedTimeProvider());
        var device = new EmergencyLoginDeviceIdentity("POS-01", "S001", "hardware-1");

        var first = await service.AcknowledgeAsync(device, 7, CancellationToken.None);
        var second = await service.AcknowledgeAsync(device, 7, CancellationToken.None);

        Assert.Equal(EmergencyLoginPublicKeyAckResult.Acknowledged, first);
        Assert.Equal(EmergencyLoginPublicKeyAckResult.Acknowledged, second);
        Assert.Equal(2, repository.AckRequests.Count);
        Assert.All(repository.AckRequests, request => Assert.Equal(7, request.Version));
        Assert.Contains("BEGIN TRANSACTION", SqlSugarEmergencyLoginPublicKeyRepository.AckSqlForTests);
        Assert.Contains("UPDLOCK, HOLDLOCK", SqlSugarEmergencyLoginPublicKeyRepository.AckSqlForTests);
        Assert.Contains("@RequestedVersion > @CurrentVersion", SqlSugarEmergencyLoginPublicKeyRepository.AckSqlForTests);
        Assert.Contains("@RequestedVersion < @CurrentVersion", SqlSugarEmergencyLoginPublicKeyRepository.AckSqlForTests);
        Assert.Contains("MERGE [dbo].[POSM_EmergencyLoginKeyDeviceSync]", SqlSugarEmergencyLoginPublicKeyRepository.AckSqlForTests);
    }

    [Fact]
    public void Package_read_contract_locks_state_and_keys_in_one_transaction()
    {
        var sql = SqlSugarEmergencyLoginPublicKeyRepository.PackageReadSqlForTests;

        Assert.Contains("BEGIN TRANSACTION", sql);
        Assert.Contains("POSM_EmergencyLoginKeySetState] AS state WITH (UPDLOCK, HOLDLOCK)", sql);
        Assert.Contains("POSM_EmergencyLoginKey] AS [key] WITH (HOLDLOCK)", sql);
        Assert.Contains("COMMIT TRANSACTION", sql);
        Assert.Contains("state.[Version]", sql);
        Assert.Contains("[key].[KeyId]", sql);
    }

    [Fact]
    public async Task Public_key_provider_uses_time_provider_for_60_second_cache_expiry()
    {
        var repository = new InMemoryRepository { Snapshot = CreateSnapshot(1, "K1") };
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-15T02:00:00Z"));
        var provider = new EmergencyLoginPublicKeyProvider(repository, memoryCache, time);

        Assert.Contains("K1", await provider.GetKeysAsync(false, CancellationToken.None));
        repository.Snapshot = CreateSnapshot(2, "K2");
        time.UtcNow = time.UtcNow.AddSeconds(59);
        Assert.Contains("K1", await provider.GetKeysAsync(false, CancellationToken.None));
        time.UtcNow = time.UtcNow.AddSeconds(2);
        var refreshed = await provider.GetKeysAsync(false, CancellationToken.None);

        Assert.Contains("K2", refreshed);
        Assert.DoesNotContain("K1", refreshed);
    }

    [Fact]
    public async Task Grant_validation_unknown_kid_forces_one_refresh_and_database_failure_stays_closed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.Parse("2026-07-15T02:00:00Z");
        var token = EmergencyLoginTokenCodec.SignV2(
            Guid.NewGuid(),
            "S001",
            now.AddMinutes(-1).UtcDateTime,
            now.AddHours(1).UtcDateTime,
            "K2",
            key.ExportECPrivateKeyPem());
        var provider = new RefreshingPublicKeyProvider(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["K2"] = key.ExportSubjectPublicKeyInfoPem()
            });
        var unavailableDb = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(HbposSqlSugarContext));
        var service = new EmergencyGrantAuthorizationService(
            unavailableDb,
            provider,
            NullLogger<EmergencyGrantAuthorizationService>.Instance,
            new FixedTimeProvider());

        var result = await service.ValidateAsync(token, "S001", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal([false, true], provider.ForceRefreshCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Grant_validation_rechecks_active_database_grant_for_v2_and_legacy_v1(bool legacyV1)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.Parse("2026-07-15T02:00:00Z");
        var grantId = Guid.NewGuid();
        var token = legacyV1
            ? EmergencyLoginTokenCodec.SignLegacy(new EmergencyLoginTokenPayload
            {
                GrantId = grantId,
                StoreCode = "S001",
                BusinessDate = "2026-07-15",
                Issuer = "admin",
                IssuedAtUtc = now.UtcDateTime,
                NotBeforeUtc = now.AddMinutes(-1).UtcDateTime,
                ExpiresAtUtc = now.AddHours(1).UtcDateTime,
            }, "K1", key.ExportECPrivateKeyPem())
            : EmergencyLoginTokenCodec.SignV2(
                grantId,
                "S001",
                now.AddMinutes(-1).UtcDateTime,
                now.AddHours(1).UtcDateTime,
                "K1",
                key.ExportECPrivateKeyPem());

        var result = await ValidateWithDatabaseAsync(token, grantId, key, revokedAtUtc: null);

        Assert.NotNull(result);
        Assert.Equal(grantId, result.GrantId);
        Assert.Equal("S001", result.StoreCode);
    }

    [Fact]
    public async Task Grant_validation_rejects_revoked_v2_grant()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.Parse("2026-07-15T02:00:00Z");
        var grantId = Guid.NewGuid();
        var token = EmergencyLoginTokenCodec.SignV2(
            grantId,
            "S001",
            now.AddMinutes(-1).UtcDateTime,
            now.AddHours(1).UtcDateTime,
            "K1",
            key.ExportECPrivateKeyPem());

        var result = await ValidateWithDatabaseAsync(token, grantId, key, now.AddMinutes(-2).UtcDateTime);

        Assert.Null(result);
    }

    private static async Task<EmergencyLoginVerifiedClaims?> ValidateWithDatabaseAsync(
        string token,
        Guid grantId,
        ECDsa key,
        DateTime? revokedAtUtc)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.emergency-auth.db");
        try
        {
            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            using var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = connection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            });
            db.CodeFirst.InitTables<EmergencyGrantRow>();
            await db.Insertable(new EmergencyGrantRow
            {
                GrantId = grantId,
                StoreCode = "S001",
                ExpiresAtUtc = DateTime.Parse("2026-07-15T03:00:00Z").ToUniversalTime(),
                RevokedAtUtc = revokedAtUtc,
            }).ExecuteCommandAsync();

            var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HbposSqlSugarContext));
            typeof(HbposSqlSugarContext)
                .GetField("<PosmDb>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(context, db);
            var provider = new RefreshingPublicKeyProvider(
                new Dictionary<string, string> { ["K1"] = key.ExportSubjectPublicKeyInfoPem() },
                new Dictionary<string, string>());
            var service = new EmergencyGrantAuthorizationService(
                context,
                provider,
                NullLogger<EmergencyGrantAuthorizationService>.Instance,
                new FixedTimeProvider());

            return await service.ValidateAsync(token, "S001", CancellationToken.None);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                try
                {
                    File.Delete(dbPath);
                }
                catch (IOException)
                {
                    // Windows 上 SQLite 清空连接池后仍可能短暂占用文件句柄，不影响授权校验断言。
                }
            }
        }
    }

    private static EmergencyLoginPublicKeysController CreateController(
        IEmergencyLoginPublicKeyDistributionService service)
    {
        var controller = new EmergencyLoginPublicKeysController(
            service,
            new FixedTimeProvider())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-01"),
                        new Claim(DeviceAuthConstants.StoreCodeClaim, "S001"),
                        new Claim(DeviceAuthConstants.HardwareIdClaim, "hardware-1")
                    ], "test"))
                }
            }
        };
        return controller;
    }

    private static EmergencyLoginPublicKeyPackage CreatePackage(long version) => new(
        version,
        "K1",
        DateTime.Parse("2026-07-15T01:00:00Z").ToUniversalTime(),
        [new("K1", "ES256", "pem", "AA")]);

    private static EmergencyLoginPublicKeySetSnapshot CreateSnapshot(long version, string kid = "K1") => new(
        version,
        kid,
        DateTime.Parse("2026-07-15T01:00:00Z").ToUniversalTime(),
        [new(kid, "ES256", "pem", "AA", "Active")]);

    private sealed class StubService(EmergencyLoginPublicKeyPackage package)
        : IEmergencyLoginPublicKeyDistributionService
    {
        public EmergencyLoginPublicKeyAckResult AckResult { get; set; } = EmergencyLoginPublicKeyAckResult.Acknowledged;
        public EmergencyLoginDeviceIdentity? LastDevice { get; private set; }

        public Task<EmergencyLoginPublicKeyPackage> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(package);

        public Task<EmergencyLoginPublicKeyAckResult> AcknowledgeAsync(
            EmergencyLoginDeviceIdentity device,
            long version,
            CancellationToken cancellationToken)
        {
            LastDevice = device;
            return Task.FromResult(AckResult);
        }
    }

    private sealed class InMemoryRepository : IEmergencyLoginPublicKeyRepository
    {
        public required EmergencyLoginPublicKeySetSnapshot Snapshot { get; set; }
        public EmergencyLoginPublicKeyAckResult AckResult { get; set; } =
            EmergencyLoginPublicKeyAckResult.StaleIgnored;
        public List<(EmergencyLoginDeviceIdentity Device, long Version)> AckRequests { get; } = [];

        public Task<EmergencyLoginPublicKeySetSnapshot> GetKeySetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);

        public Task<EmergencyLoginPublicKeyAckResult> AcknowledgeAsync(
            EmergencyLoginDeviceIdentity device,
            long version,
            DateTime acknowledgedAtUtc,
            CancellationToken cancellationToken)
        {
            AckRequests.Add((device, version));
            return Task.FromResult(AckResult);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-07-15T02:00:00Z");
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class RefreshingPublicKeyProvider(
        IReadOnlyDictionary<string, string> cached,
        IReadOnlyDictionary<string, string> refreshed) : IEmergencyLoginPublicKeyProvider
    {
        public List<bool> ForceRefreshCalls { get; } = [];

        public Task<IReadOnlyDictionary<string, string>> GetKeysAsync(
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            ForceRefreshCalls.Add(forceRefresh);
            return Task.FromResult(forceRefresh ? refreshed : cached);
        }
    }

    [SugarTable("POSM_EmergencyLoginGrant")]
    private sealed class EmergencyGrantRow
    {
        [SugarColumn(IsPrimaryKey = true)]
        public Guid GrantId { get; set; }
        public string StoreCode { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
        [SugarColumn(IsNullable = true)]
        public DateTime? RevokedAtUtc { get; set; }
    }

    private sealed class UnauthenticatedHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
