using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BlazorApp.Shared.Constants;
using Hbpos.Api;
using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Linkly;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class LinklyCloudPairingAuthorizationHttpTests
{
    public static IEnumerable<object[]> HealthPermissionCases()
    {
        yield return
        [
            new[] { Permissions.PosTerminal.Settings.PaymentTerminal },
            HttpStatusCode.OK
        ];
        yield return
        [
            new[]
            {
                Permissions.PosTerminal.Payment.TakeCard,
                Permissions.PosTerminal.Payment.Confirm
            },
            HttpStatusCode.OK
        ];
        yield return
        [
            new[] { Permissions.PosTerminal.Payment.TakeCard },
            HttpStatusCode.Forbidden
        ];
        yield return
        [
            new[] { Permissions.PosTerminal.Payment.Confirm },
            HttpStatusCode.Forbidden
        ];
    }

    [Fact]
    public async Task Pair_without_authorization_returns_401()
    {
        await using var factory = new LinklyCloudPairingAuthorizationApiFactory(
            new CapturingLinklyCloudPairingService(),
            paymentSettingsGranted: false);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/pair",
            new LinklyCloudBackendPairRequest("Sandbox", "123456"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Pair_with_authenticated_device_but_without_payment_settings_returns_403()
    {
        await using var factory = new LinklyCloudPairingAuthorizationApiFactory(
            new CapturingLinklyCloudPairingService(),
            paymentSettingsGranted: false);
        using var client = factory.CreateClient();
        AddDeviceAuthentication(client);
        client.DefaultRequestHeaders.Add(CashierAuthorizationConstants.HeaderName, "valid");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/pair",
            new LinklyCloudBackendPairRequest("Sandbox", "123456"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Pair_with_payment_settings_uses_authenticated_store_and_device_claims()
    {
        var pairing = new CapturingLinklyCloudPairingService();
        await using var factory = new LinklyCloudPairingAuthorizationApiFactory(
            pairing,
            paymentSettingsGranted: true);
        using var client = factory.CreateClient();
        AddDeviceAuthentication(client);
        client.DefaultRequestHeaders.Add(CashierAuthorizationConstants.HeaderName, "valid");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/pair",
            new LinklyCloudBackendPairRequest("Sandbox", "123456"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, pairing.Calls);
        Assert.Equal("S01", pairing.StoreCode);
        Assert.Equal("POS-01", pairing.DeviceCode);
        Assert.Equal("Sandbox", pairing.Request?.Environment);
        Assert.Equal("123456", pairing.Request?.PairCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    public async Task Pair_invalid_json_body_returns_stable_api_error_envelope(string json)
    {
        var pairing = new CapturingLinklyCloudPairingService();
        await using var factory = new LinklyCloudPairingAuthorizationApiFactory(
            pairing,
            paymentSettingsGranted: true);
        using var client = factory.CreateClient();
        AddDeviceAuthentication(client);
        client.DefaultRequestHeaders.Add(CashierAuthorizationConstants.HeaderName, "valid");
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(
            "/api/v1/linkly/cloud-backend/pair",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "LINKLY_CLOUD_BACKEND_PAIR_REQUEST_INVALID",
            document.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(0, pairing.Calls);
    }

    [Theory]
    [MemberData(nameof(HealthPermissionCases))]
    public async Task Health_allows_settings_or_complete_card_permissions(
        string[] grantedPermissions,
        HttpStatusCode expectedStatusCode)
    {
        await using var factory = new LinklyCloudPairingAuthorizationApiFactory(
            new CapturingLinklyCloudPairingService(),
            paymentSettingsGranted: false,
            grantedPermissions);
        using var client = factory.CreateClient();
        AddDeviceAuthentication(client);
        client.DefaultRequestHeaders.Add(CashierAuthorizationConstants.HeaderName, "valid");

        using var response = await client.GetAsync(
            "/api/v1/linkly/cloud-backend/health?environment=Sandbox");

        Assert.Equal(expectedStatusCode, response.StatusCode);
    }

    [Fact]
    public async Task Complete_card_permissions_do_not_grant_pair_or_logon_management_access()
    {
        var pairing = new CapturingLinklyCloudPairingService();
        await using var factory = new LinklyCloudPairingAuthorizationApiFactory(
            pairing,
            paymentSettingsGranted: false,
            [
                Permissions.PosTerminal.Payment.TakeCard,
                Permissions.PosTerminal.Payment.Confirm
            ]);
        using var client = factory.CreateClient();
        AddDeviceAuthentication(client);
        client.DefaultRequestHeaders.Add(CashierAuthorizationConstants.HeaderName, "valid");

        using var pairResponse = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/pair",
            new LinklyCloudBackendPairRequest("Sandbox", "123456"));
        using var logonResponse = await client.PostAsync(
            "/api/v1/linkly/cloud-backend/logon-test?environment=Sandbox",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, pairResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, logonResponse.StatusCode);
        Assert.Equal(0, pairing.Calls);
    }

    [Theory]
    [InlineData("P", Permissions.PosTerminal.Payment.TakeCard, HttpStatusCode.OK)]
    [InlineData("R", Permissions.PosTerminal.Payment.TakeCard, HttpStatusCode.Forbidden)]
    [InlineData("r", Permissions.PosTerminal.Payment.TakeCard, HttpStatusCode.Forbidden)]
    [InlineData("R", Permissions.PosTerminal.Returns.Confirm, HttpStatusCode.OK)]
    [InlineData("R", Permissions.PosTerminal.Installments.Cancel, HttpStatusCode.OK)]
    public async Task Card_refund_requires_refund_permission_on_top_of_take_card(
        string txnType,
        string extraPermission,
        HttpStatusCode expectedStatusCode)
    {
        var backend = new RecordingTransactionBackendService();
        await using var factory = new LinklyCloudPairingAuthorizationApiFactory(
            new CapturingLinklyCloudPairingService(),
            paymentSettingsGranted: false,
            [
                Permissions.PosTerminal.Payment.TakeCard,
                Permissions.PosTerminal.Payment.Confirm,
                extraPermission
            ],
            backend);
        using var client = factory.CreateClient();
        AddDeviceAuthentication(client);
        client.DefaultRequestHeaders.Add(CashierAuthorizationConstants.HeaderName, "valid");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/transactions",
            new LinklyCloudBackendTransactionRequest(
                "Sandbox",
                txnType,
                1000,
                new Dictionary<string, string> { ["RFN"] = "ORIGINAL-RFN" }));

        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal(expectedStatusCode == HttpStatusCode.OK ? 1 : 0, backend.StartCalls);
    }

    [Fact]
    public async Task Card_purchase_does_not_require_refund_permission()
    {
        var backend = new RecordingTransactionBackendService();
        await using var factory = new LinklyCloudPairingAuthorizationApiFactory(
            new CapturingLinklyCloudPairingService(),
            paymentSettingsGranted: false,
            [
                Permissions.PosTerminal.Payment.TakeCard,
                Permissions.PosTerminal.Payment.Confirm
            ],
            backend);
        using var client = factory.CreateClient();
        AddDeviceAuthentication(client);
        client.DefaultRequestHeaders.Add(CashierAuthorizationConstants.HeaderName, "valid");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/linkly/cloud-backend/transactions",
            new LinklyCloudBackendTransactionRequest("Sandbox", "P", 1000, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, backend.StartCalls);
    }

    private sealed class RecordingTransactionBackendService : NoOpLinklyCloudBackendAsyncService
    {
        public int StartCalls { get; private set; }

        public override Task<LinklyCloudBackendSessionResponse> StartTransactionAsync(
            string storeCode,
            string deviceCode,
            LinklyCloudBackendTransactionRequest request,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            return Task.FromResult(new LinklyCloudBackendSessionResponse(
                request.Environment,
                storeCode,
                deviceCode,
                "session-1",
                "Pending",
                "SERVER-TXN",
                null,
                null,
                null,
                null,
                false,
                false,
                false,
                false,
                false,
                null,
                null,
                [],
                null,
                0,
                null,
                null,
                null,
                []));
        }
    }

    private static void AddDeviceAuthentication(HttpClient client)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
    }

    private sealed class LinklyCloudPairingAuthorizationApiFactory(
        CapturingLinklyCloudPairingService pairing,
        bool paymentSettingsGranted,
        IReadOnlyCollection<string>? grantedPermissions = null,
        ILinklyCloudBackendAsyncService? backendService = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CashierAuthorization:Mode"] = "Enforce"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = PairingTestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = PairingTestAuthHandler.SchemeName;
                    options.DefaultScheme = PairingTestAuthHandler.SchemeName;
                });

                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, PairingTestAuthHandler>(
                        PairingTestAuthHandler.SchemeName,
                        _ => { });

                services.RemoveAll<ILinklyCloudPairingService>();
                services.AddSingleton<ILinklyCloudPairingService>(pairing);
                services.RemoveAll<ILinklyCloudCredentialService>();
                services.AddSingleton<ILinklyCloudCredentialService>(new NoOpLinklyCloudCredentialService());
                services.RemoveAll<ILinklyCloudBackendAsyncService>();
                services.AddSingleton<ILinklyCloudBackendAsyncService>(
                    backendService ?? new NoOpLinklyCloudBackendAsyncService(
                        new LinklyCloudBackendHealthResponse(
                            "Sandbox",
                            "S01",
                            "POS-01",
                            true,
                            null,
                            [])));
                services.RemoveAll<ILinklyCloudTerminalService>();
                services.AddSingleton<ILinklyCloudTerminalService>(
                    new FixedLinklyCloudTerminalModeService("Legacy"));

                services.RemoveAll<ICashierAuthorizationTicketService>();
                services.AddSingleton<ICashierAuthorizationTicketService>(new PairingTicketService());
                services.RemoveAll<ICashierService>();
                services.AddSingleton<ICashierService>(new PairingCashierService(
                    paymentSettingsGranted,
                    grantedPermissions));
                services.RemoveAll<IPosIpadAppReviewAuthorizationBoundary>();
                services.AddSingleton<IPosIpadAppReviewAuthorizationBoundary>(
                    new NonReviewDeviceAuthorizationBoundary());

                var schemaInitializer = new NoOpLinklySchemaInitializer();
                services.RemoveAll<IStoreSchemaInitializer>();
                services.AddSingleton<IStoreSchemaInitializer>(schemaInitializer);
                services.RemoveAll<IAttendanceQrKeySchemaInitializer>();
                services.AddSingleton<IAttendanceQrKeySchemaInitializer>(schemaInitializer);
                services.RemoveAll<IDeviceRuntimeStatusSchemaInitializer>();
                services.AddSingleton<IDeviceRuntimeStatusSchemaInitializer>(schemaInitializer);
                services.RemoveAll<IAdvertisementSchemaInitializer>();
                services.AddSingleton<IAdvertisementSchemaInitializer>(schemaInitializer);
                services.RemoveAll<ILinklyCloudCredentialSchemaInitializer>();
                services.AddSingleton<ILinklyCloudCredentialSchemaInitializer>(schemaInitializer);
                services.RemoveAll<ILinklyCloudBackendAsyncSchemaInitializer>();
                services.AddSingleton<ILinklyCloudBackendAsyncSchemaInitializer>(schemaInitializer);
                services.RemoveAll<ISquareTokenSchemaInitializer>();
                services.AddSingleton<ISquareTokenSchemaInitializer>(schemaInitializer);
            });
        }
    }

    private sealed class PairingTestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "LinklyPairingHttpTestAuth";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!string.Equals(Request.Headers.Authorization.ToString(), "Test", StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-01"),
                    new Claim(DeviceAuthConstants.StoreCodeClaim, "S01"),
                    new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-001")
                ],
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private sealed class PairingTicketService : ICashierAuthorizationTicketService
    {
        public (string Token, DateTimeOffset ExpiresAtUtc) Issue(
            string cashierId,
            string userGuid,
            string storeCode,
            string deviceCode) => throw new NotSupportedException();

        public CashierAuthorizationTicket? Validate(string? token) =>
            string.Equals(token, "valid", StringComparison.Ordinal)
                ? new CashierAuthorizationTicket(
                    "C001",
                    "U001",
                    "S01",
                    "POS-01",
                    DateTimeOffset.UtcNow.AddHours(1))
                : null;
    }

    private sealed class PairingCashierService(
        bool paymentSettingsGranted,
        IReadOnlyCollection<string>? grantedPermissions) : ICashierService
    {
        public Task<CashierSessionDto?> BarcodeLoginAsync(
            CashierBarcodeLoginRequest request,
            CancellationToken cancellationToken) => Task.FromResult<CashierSessionDto?>(null);

        public Task<bool> HasAnyPermissionAsync(
            string userGuid,
            string storeCode,
            IReadOnlyCollection<string> permissionCodes,
            CancellationToken cancellationToken) => Task.FromResult(
                grantedPermissions is null
                    ? paymentSettingsGranted
                    : permissionCodes.Any(grantedPermissions.Contains));

        public Task<CashierSessionDto?> RefreshSessionAsync(
            CashierAuthorizationTicket ticket,
            CancellationToken cancellationToken) => Task.FromResult<CashierSessionDto?>(null);
    }
}
