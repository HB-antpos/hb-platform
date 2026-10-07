using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BlazorApp.Shared.Constants;
using Hbpos.Api;
using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Stores;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

/// <summary>
/// 走真实授权管线验证：同步与回执只要求设备认证，不要求收银员票据或「设置小票打印机」权限。
/// 这是后台轮询——应用刚启动、没人登录、或当班收银员没有该权限时下发也必须生效；
/// 而手动「载入门店资料」仍属设置页功能，继续要求 ReceiptPrinter 权限（作对照，证明两者确有差别）。
/// </summary>
public sealed class StoreReceiptProfileSyncAuthorizationHttpTests
{
    private const string SyncUrl = "/api/v1/stores/current/receipt-profile/sync?knownVersion=0";
    private const string AckUrl = "/api/v1/stores/current/receipt-profile/ack";
    private const string LoadUrl = "/api/v1/stores/current/receipt-profile";

    [Fact]
    public async Task Sync_and_ack_without_device_authentication_return_401()
    {
        await using var factory = new ReceiptProfileApiFactory();
        using var client = factory.CreateClient();

        using var sync = await client.GetAsync(SyncUrl);
        using var ack = await client.PostAsJsonAsync(AckUrl, new StoreReceiptProfileAckRequest(1));

        Assert.Equal(HttpStatusCode.Unauthorized, sync.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, ack.StatusCode);
    }

    [Fact]
    public async Task Sync_and_ack_with_device_authentication_only_succeed_without_any_cashier_ticket()
    {
        await using var factory = new ReceiptProfileApiFactory();
        using var client = factory.CreateClient();
        AddDeviceAuthentication(client);

        using var sync = await client.GetAsync(SyncUrl);
        using var ack = await client.PostAsJsonAsync(AckUrl, new StoreReceiptProfileAckRequest(2));

        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        // 门店与设备只取认证声明（设备系统声明决定回执的端类型）。
        Assert.Equal(("S01", 0), (factory.Release.SyncStoreCode, factory.Release.SyncKnownVersion));
        Assert.Equal(("S01", "POS-07", "Android", 2), factory.Release.LastAck);
    }

    [Fact]
    public async Task Sync_and_ack_succeed_for_a_cashier_who_lacks_the_receipt_printer_permission()
    {
        // 确切场景：有收银员登录（票据有效），但没有 Settings.ReceiptPrinter 权限。
        await using var factory = new ReceiptProfileApiFactory(cashierHasReceiptPrinterPermission: false);
        using var client = factory.CreateClient();
        AddDeviceAuthentication(client);
        client.DefaultRequestHeaders.Add(CashierAuthorizationConstants.HeaderName, "valid");

        using var sync = await client.GetAsync(SyncUrl);
        using var ack = await client.PostAsJsonAsync(AckUrl, new StoreReceiptProfileAckRequest(1));

        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
    }

    [Fact]
    public async Task Manual_load_still_requires_the_receipt_printer_permission()
    {
        await using var factory = new ReceiptProfileApiFactory(cashierHasReceiptPrinterPermission: false);

        // 只有设备认证、没有收银员票据：403。
        using var deviceOnly = factory.CreateClient();
        AddDeviceAuthentication(deviceOnly);
        using var withoutTicket = await deviceOnly.GetAsync(LoadUrl);
        Assert.Equal(HttpStatusCode.Forbidden, withoutTicket.StatusCode);

        // 有票据但没有权限：403。
        using var cashierWithoutPermission = factory.CreateClient();
        AddDeviceAuthentication(cashierWithoutPermission);
        cashierWithoutPermission.DefaultRequestHeaders.Add(CashierAuthorizationConstants.HeaderName, "valid");
        using var withoutPermission = await cashierWithoutPermission.GetAsync(LoadUrl);
        Assert.Equal(HttpStatusCode.Forbidden, withoutPermission.StatusCode);
    }

    [Fact]
    public async Task Manual_load_succeeds_for_a_cashier_with_the_receipt_printer_permission()
    {
        await using var factory = new ReceiptProfileApiFactory(cashierHasReceiptPrinterPermission: true);
        using var client = factory.CreateClient();
        AddDeviceAuthentication(client);
        client.DefaultRequestHeaders.Add(CashierAuthorizationConstants.HeaderName, "valid");

        using var response = await client.GetAsync(LoadUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static void AddDeviceAuthentication(HttpClient client)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
    }

    private sealed class ReceiptProfileApiFactory(bool cashierHasReceiptPrinterPermission = false)
        : WebApplicationFactory<Program>
    {
        public RecordingReleaseService Release { get; } = new();

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
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                });
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

                services.RemoveAll<IStoreReceiptProfileReleaseService>();
                services.AddSingleton<IStoreReceiptProfileReleaseService>(Release);
                services.RemoveAll<IStoreReceiptProfileService>();
                services.AddSingleton<IStoreReceiptProfileService>(new FixedProfileService());

                services.RemoveAll<ICashierAuthorizationTicketService>();
                services.AddSingleton<ICashierAuthorizationTicketService>(new FakeTicketService());
                services.RemoveAll<ICashierService>();
                services.AddSingleton<ICashierService>(new FakeCashierService(cashierHasReceiptPrinterPermission));
                services.RemoveAll<IPosIpadAppReviewAuthorizationBoundary>();
                services.AddSingleton<IPosIpadAppReviewAuthorizationBoundary>(
                    new NonReviewDeviceAuthorizationBoundary());

                // 启动期的各类表结构初始化都换成空实现，测试宿主不连数据库。
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

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "ReceiptProfileHttpTestAuth";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!string.Equals(Request.Headers.Authorization.ToString(), "Test", StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-07"),
                    new Claim(DeviceAuthConstants.StoreCodeClaim, "S01"),
                    new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-007"),
                    new Claim(DeviceAuthConstants.DeviceSystemClaim, "Android")
                ],
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private sealed class RecordingReleaseService : IStoreReceiptProfileReleaseService
    {
        public string? SyncStoreCode { get; private set; }

        public int SyncKnownVersion { get; private set; } = -1;

        public (string StoreCode, string DeviceCode, string? DeviceSystem, int Version)? LastAck { get; private set; }

        public Task<StoreReceiptProfileSyncLookupResult> GetSyncAsync(
            string storeCode,
            int knownVersion,
            CancellationToken cancellationToken)
        {
            SyncStoreCode = storeCode;
            SyncKnownVersion = knownVersion;
            return Task.FromResult(new StoreReceiptProfileSyncLookupResult(
                new StoreReceiptProfileSyncDto(false, 0, null)));
        }

        public Task<StoreReceiptProfileAckLookupResult> AckAsync(
            string storeCode,
            string deviceCode,
            string? deviceSystem,
            int version,
            CancellationToken cancellationToken)
        {
            LastAck = (storeCode, deviceCode, deviceSystem, version);
            return Task.FromResult(new StoreReceiptProfileAckLookupResult(
                new StoreReceiptProfileAckResultDto(version)));
        }
    }

    private sealed class FixedProfileService : IStoreReceiptProfileService
    {
        public Task<StoreReceiptProfileLookupResult> GetCurrentAsync(
            string storeCode,
            CancellationToken cancellationToken) =>
            Task.FromResult(new StoreReceiptProfileLookupResult(
                new StoreReceiptProfileDto("S01", "示例分店", null, null, null, null, null)));
    }

    // 收银员票据只在请求头为 "valid" 时有效，对应 S01 / POS-07。
    private sealed class FakeTicketService : ICashierAuthorizationTicketService
    {
        public (string Token, DateTimeOffset ExpiresAtUtc) Issue(
            string cashierId,
            string userGuid,
            string storeCode,
            string deviceCode) => throw new NotSupportedException();

        public CashierAuthorizationTicket? Validate(string? token) =>
            string.Equals(token, "valid", StringComparison.Ordinal)
                ? new CashierAuthorizationTicket("C001", "U001", "S01", "POS-07", DateTimeOffset.UtcNow.AddHours(1))
                : null;
    }

    private sealed class FakeCashierService(bool receiptPrinterGranted) : ICashierService
    {
        public Task<CashierSessionDto?> BarcodeLoginAsync(
            CashierBarcodeLoginRequest request,
            CancellationToken cancellationToken) => Task.FromResult<CashierSessionDto?>(null);

        public Task<bool> HasAnyPermissionAsync(
            string userGuid,
            string storeCode,
            IReadOnlyCollection<string> permissionCodes,
            CancellationToken cancellationToken) => Task.FromResult(
                receiptPrinterGranted && permissionCodes.Contains(Permissions.PosTerminal.Settings.ReceiptPrinter));

        public Task<CashierSessionDto?> RefreshSessionAsync(
            CashierAuthorizationTicket ticket,
            CancellationToken cancellationToken) => Task.FromResult<CashierSessionDto?>(null);
    }
}
