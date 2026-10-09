using System.Net;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Square;

namespace Hbpos.Client.Tests;

[Collection(EnvironmentVariableTestCollection.Name)]
public sealed class CardTerminalSetupServiceTests
{
    private const string LocalToken = "opaque-local-setup-token";

    [Fact]
    public async Task Linkly_backend_terminal_directory_selection_and_pairing_delegate_without_credentials()
    {
        var terminalId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var backend = new FakeLinklyBackendTerminalClient
        {
            TerminalDirectory = new LinklyCloudTerminalListResponse(
                "Sandbox",
                terminalId,
                3,
                [new LinklyCloudTerminalSummary(terminalId, 1, "Front", "Unpaired", false, false, null, null)]),
            TerminalSelection = new LinklyCloudTerminalSelectionResponse("Sandbox", terminalId, 4),
            TerminalPairResult = new LinklyCloudTerminalPairResponse(
                terminalId,
                "Sandbox",
                "Front",
                "Ready",
                true,
                "Paired")
        };
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(),
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyBackendTerminalClient: backend);

        var directory = await service.ListLinklyCloudBackendTerminalsAsync(CardTerminalEnvironment.Sandbox);
        var selection = await service.SelectLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment.Sandbox,
            terminalId,
            expectedRevision: 3);
        var pairing = await service.PairLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment.Sandbox,
            terminalId,
            "123456");

        Assert.Same(backend.TerminalDirectory, directory);
        Assert.Same(backend.TerminalSelection, selection);
        Assert.Same(backend.TerminalPairResult, pairing);
        Assert.Equal(CardTerminalEnvironment.Sandbox, backend.LastTerminalEnvironment);
        Assert.Equal(terminalId, backend.LastTerminalId);
        Assert.Equal(3, backend.LastExpectedRevision);
        Assert.Equal("123456", backend.LastPairCode);
    }

    [Fact]
    public async Task Linkly_terminal_connection_test_uses_terminal_assignment_snapshot()
    {
        var terminal = new LinklyCloudTerminalSummary(
            Guid.NewGuid(), 2, "Returns", "Ready", false, true, null, null,
            "POS-2", 7, "2026-09-10T01:02:03.1234567");
        var backend = new FakeLinklyBackendTerminalClient();
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(),
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyBackendTerminalClient: backend);

        await service.TestLinklyCloudBackendTerminalAsync(CardTerminalEnvironment.Sandbox, terminal);

        Assert.Equal(terminal.TerminalId, backend.LastTerminalId);
        Assert.Equal(terminal.TerminalVersion, backend.LastConnectionTestRequest!.ExpectedTerminalVersion);
        Assert.Equal("POS-2", backend.LastConnectionTestRequest.ExpectedAssignedDeviceCode);
        Assert.Equal(7, backend.LastConnectionTestRequest.ExpectedAssignmentRevision);
    }

    [Fact]
    public async Task Linkly_terminal_unbind_does_not_pair_or_clear_client_credentials()
    {
        var terminal = new LinklyCloudTerminalSummary(
            Guid.NewGuid(), 1, "Front", "Ready", false, true, null, null,
            "POS-2", 11, "v-11");
        var backend = new FakeLinklyBackendTerminalClient();
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(),
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyBackendTerminalClient: backend);
        var session = new Hbpos.Client.Wpf.Models.PosSessionState(
            "HB POS", "S01", "Store", "POS-1", "C1", "Cashier", true, 0);

        var result = await service.AssignLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment.Sandbox,
            terminal,
            targetDevice: null,
            devices: [],
            session);

        Assert.True(result.Succeeded);
        Assert.Null(backend.LastAssignmentRequest!.TargetDeviceCode);
        Assert.Null(backend.LastAssignmentRequest.ExpectedTargetTerminalId);
        Assert.Equal(0, backend.LastAssignmentRequest.ExpectedTargetSelectionRevision);
        Assert.Null(backend.LastPairCode);
    }

    [Fact]
    public async Task Linkly_terminal_assignment_blocks_while_current_payment_attempt_is_active()
    {
        var terminal = new LinklyCloudTerminalSummary(
            Guid.NewGuid(), 1, "Front", "Ready", false, true, null, null,
            null, 0, "v-1");
        var target = new LinklyCloudAssignableDevice("POS-1", "WPF", true, null, 0);
        var backend = new FakeLinklyBackendTerminalClient();
        var accessor = new FakeLinklyPaymentAttemptContextAccessor
        {
            CurrentValue = new LinklyPaymentAttemptContext(Guid.NewGuid(), (_, _, _, _) => Task.CompletedTask)
        };
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(),
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyBackendTerminalClient: backend,
            linklyPaymentAttemptContextAccessor: accessor);
        var session = new Hbpos.Client.Wpf.Models.PosSessionState(
            "HB POS", "S01", "Store", "POS-1", "C1", "Cashier", true, 0);

        var result = await service.AssignLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment.Production, terminal, target, [target], session);

        Assert.False(result.Succeeded);
        Assert.Contains("currently running", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(backend.LastAssignmentRequest);
    }

    [Fact]
    public async Task Linkly_terminal_assignment_same_owner_is_no_op_and_preserves_health()
    {
        var terminalId = Guid.NewGuid();
        var checkedAt = DateTimeOffset.UtcNow;
        var terminal = new LinklyCloudTerminalSummary(
            terminalId, 1, "Front", "Ready", false, true, "connected", checkedAt,
            "POS-1", 4, "v-4");
        var target = new LinklyCloudAssignableDevice("POS-1", "WPF", true, terminalId, 4);
        var backend = new FakeLinklyBackendTerminalClient
        {
            AssignmentResult = new LinklyCloudTerminalListResponse(
                "Production", terminalId, 4, [terminal], "Active", [target])
        };
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(), new FakeSquareTerminalSetupClient(), new FakeLinklyTerminalClient(),
            linklyBackendTerminalClient: backend);
        var session = new Hbpos.Client.Wpf.Models.PosSessionState(
            "HB POS", "S01", "Store", "POS-1", "C1", "Cashier", true, 0);

        var result = await service.AssignLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment.Production, terminal, target, [target], session);

        Assert.True(result.Succeeded);
        Assert.Equal("connected", terminal.LastHealthStatus);
        Assert.Equal(checkedAt, terminal.LastHealthAt);
        Assert.Equal(1, backend.AssignmentCallCount);
        Assert.Equal(0, backend.DirectoryCallCount);
    }

    [Fact]
    public async Task Linkly_terminal_no_op_stale_snapshot_is_rejected_by_server_cas()
    {
        var terminalId = Guid.NewGuid();
        var terminal = new LinklyCloudTerminalSummary(
            terminalId, 1, "Front", "Ready", false, true, "Healthy", DateTimeOffset.UtcNow,
            "POS-1", 4, "v-4");
        var target = new LinklyCloudAssignableDevice("POS-1", "WPF", true, terminalId, 4);
        var backend = new FakeLinklyBackendTerminalClient
        {
            AssignmentException = new HttpRequestException("selection changed", null, HttpStatusCode.Conflict)
        };
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(), new FakeSquareTerminalSetupClient(), new FakeLinklyTerminalClient(),
            linklyBackendTerminalClient: backend);
        var session = new Hbpos.Client.Wpf.Models.PosSessionState(
            "HB POS", "S01", "Store", "POS-1", "C1", "Cashier", true, 0);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.AssignLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment.Production, terminal, target, [target], session));

        Assert.Equal(1, backend.AssignmentCallCount);
        Assert.Equal(0, backend.DirectoryCallCount);
    }

    [Fact]
    public async Task Linkly_terminal_assignment_blocks_durable_unresolved_settlement_after_restart()
    {
        var terminal = new LinklyCloudTerminalSummary(
            Guid.NewGuid(), 1, "Front", "Ready", false, true, null, null,
            null, 0, "v-1");
        var target = new LinklyCloudAssignableDevice("POS-1", "WPF", true, null, 0);
        var backend = new FakeLinklyBackendTerminalClient();
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(), new FakeSquareTerminalSetupClient(), new FakeLinklyTerminalClient(),
            linklyBackendTerminalClient: backend,
            linklySettlementRepository: new FakeUnresolvedSettlementReader(true));
        var session = new Hbpos.Client.Wpf.Models.PosSessionState(
            "HB POS", "S01", "Store", "POS-1", "C1", "Cashier", true, 0);

        var result = await service.AssignLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment.Production, terminal, target, [target], session);

        Assert.False(result.Succeeded);
        Assert.Contains("unfinished Linkly settlement", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, backend.AssignmentCallCount);
    }

    [Fact]
    public async Task Linkly_terminal_assignment_reconciles_response_loss_with_one_put_and_one_get()
    {
        var terminalId = Guid.NewGuid();
        var terminal = new LinklyCloudTerminalSummary(
            terminalId, 1, "Front", "Ready", false, true, "connected", DateTimeOffset.UtcNow,
            null, 0, "v-1");
        var target = new LinklyCloudAssignableDevice("POS-1", "WPF", true, null, 0);
        var backend = new FakeLinklyBackendTerminalClient
        {
            AssignmentException = new TaskCanceledException("response lost"),
            TerminalDirectory = ChangedDirectory(terminal, target)
        };
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(), new FakeSquareTerminalSetupClient(), new FakeLinklyTerminalClient(),
            linklyBackendTerminalClient: backend);
        var session = new Hbpos.Client.Wpf.Models.PosSessionState(
            "HB POS", "S01", "Store", "POS-1", "C1", "Cashier", true, 0);

        var result = await service.AssignLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment.Production, terminal, target, [target], session);

        Assert.True(result.Succeeded);
        Assert.True(result.Reconciled);
        Assert.Equal(1, backend.AssignmentCallCount);
        Assert.Equal(1, backend.DirectoryCallCount);
    }

    [Fact]
    public async Task Linkly_terminal_assignment_rechecks_incomplete_success_response_without_replaying_put()
    {
        var terminalId = Guid.NewGuid();
        var terminal = new LinklyCloudTerminalSummary(
            terminalId, 1, "Front", "Ready", false, true, "connected", DateTimeOffset.UtcNow,
            null, 0, "v-1");
        var target = new LinklyCloudAssignableDevice("POS-1", "WPF", true, null, 0);
        var backend = new FakeLinklyBackendTerminalClient
        {
            AssignmentResult = new LinklyCloudTerminalListResponse("Production", null, null, [], "Active", []),
            TerminalDirectory = ChangedDirectory(terminal, target)
        };
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(), new FakeSquareTerminalSetupClient(), new FakeLinklyTerminalClient(),
            linklyBackendTerminalClient: backend);
        var session = new Hbpos.Client.Wpf.Models.PosSessionState(
            "HB POS", "S01", "Store", "POS-1", "C1", "Cashier", true, 0);

        var result = await service.AssignLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment.Production, terminal, target, [target], session);

        Assert.True(result.Succeeded);
        Assert.Equal(1, backend.AssignmentCallCount);
        Assert.Equal(1, backend.DirectoryCallCount);
    }

    [Fact]
    public async Task Linkly_terminal_rebind_accepts_per_device_revision_and_cleared_displaced_line()
    {
        var sourceId = Guid.NewGuid();
        var displacedId = Guid.NewGuid();
        var source = new LinklyCloudTerminalSummary(
            sourceId, 1, "Front", "Ready", false, true, "Healthy", DateTimeOffset.UtcNow,
            null, 0, "v-source-1");
        var target = new LinklyCloudAssignableDevice("POS-1", "WPF", true, displacedId, 5);
        var backend = new FakeLinklyBackendTerminalClient
        {
            AssignmentResult = new LinklyCloudTerminalListResponse(
                "Production", sourceId, 6,
                [
                    source with
                    {
                        AssignedDeviceCode = "POS-1", AssignmentRevision = 6,
                        TerminalVersion = "v-source-2", LastHealthStatus = null, LastHealthAt = null
                    },
                    new LinklyCloudTerminalSummary(
                        displacedId, 2, "Returns", "Ready", false, true, null, null,
                        null, 0, "v-displaced-2")
                ],
                "Active",
                [target with { SelectedTerminalId = sourceId, SelectionRevision = 6 }])
        };
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(), new FakeSquareTerminalSetupClient(), new FakeLinklyTerminalClient(),
            linklyBackendTerminalClient: backend);
        var session = new Hbpos.Client.Wpf.Models.PosSessionState(
            "HB POS", "S01", "Store", "POS-1", "C1", "Cashier", true, 0);

        var result = await service.AssignLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment.Production, source, target, [target], session);

        Assert.True(result.Succeeded);
        Assert.Equal(1, backend.AssignmentCallCount);
        Assert.Equal(0, backend.DirectoryCallCount);
    }

    [Fact]
    public async Task Linkly_terminal_assignment_reconciles_malformed_success_body_and_http_408_once_each()
    {
        foreach (var ambiguous in new Exception[]
                 {
                     new System.Text.Json.JsonException("truncated 200 body"),
                     new HttpRequestException("request timeout", null, HttpStatusCode.RequestTimeout),
                     new HttpRequestException("empty 200 contract", null, HttpStatusCode.OK)
                 })
        {
            var terminal = new LinklyCloudTerminalSummary(
                Guid.NewGuid(), 1, "Front", "Ready", false, true, null, null, null, 0, "v-1");
            var target = new LinklyCloudAssignableDevice("POS-1", "WPF", true, null, 0);
            var backend = new FakeLinklyBackendTerminalClient
            {
                AssignmentException = ambiguous,
                TerminalDirectory = ChangedDirectory(terminal, target)
            };
            var service = new CardTerminalSetupService(
                new FakeCardTerminalSettingsStore(), new FakeSquareTerminalSetupClient(), new FakeLinklyTerminalClient(),
                linklyBackendTerminalClient: backend);
            var session = new Hbpos.Client.Wpf.Models.PosSessionState(
                "HB POS", "S01", "Store", "POS-1", "C1", "Cashier", true, 0);

            var result = await service.AssignLinklyCloudBackendTerminalAsync(
                CardTerminalEnvironment.Production, terminal, target, [target], session);

            Assert.True(result.Succeeded);
            Assert.Equal(1, backend.AssignmentCallCount);
            Assert.Equal(1, backend.DirectoryCallCount);
        }
    }

    [Fact]
    public async Task Linkly_terminal_assignment_returns_stable_failure_when_reconcile_body_is_malformed()
    {
        var terminal = new LinklyCloudTerminalSummary(
            Guid.NewGuid(), 1, "Front", "Ready", false, true, null, null, null, 0, "v-1");
        var target = new LinklyCloudAssignableDevice("POS-1", "WPF", true, null, 0);
        var backend = new FakeLinklyBackendTerminalClient
        {
            AssignmentException = new System.Text.Json.JsonException("truncated PUT body"),
            DirectoryException = new System.Text.Json.JsonException("truncated GET body")
        };
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(), new FakeSquareTerminalSetupClient(), new FakeLinklyTerminalClient(),
            linklyBackendTerminalClient: backend);
        var session = new Hbpos.Client.Wpf.Models.PosSessionState(
            "HB POS", "S01", "Store", "POS-1", "C1", "Cashier", true, 0);

        var result = await service.AssignLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment.Production, terminal, target, [target], session);

        Assert.False(result.Succeeded);
        Assert.True(result.Reconciled);
        Assert.Contains("could not be refreshed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, backend.AssignmentCallCount);
        Assert.Equal(1, backend.DirectoryCallCount);
    }

    private static LinklyCloudTerminalListResponse ChangedDirectory(
        LinklyCloudTerminalSummary terminal,
        LinklyCloudAssignableDevice target) =>
        new(
            "Production",
            terminal.TerminalId,
            target.SelectionRevision + 1,
            [terminal with
            {
                AssignedDeviceCode = target.DeviceCode,
                AssignmentRevision = target.SelectedTerminalId is null ? 4_107 : target.SelectionRevision + 1,
                TerminalVersion = terminal.TerminalVersion + "-next",
                LastHealthStatus = null,
                LastHealthAt = null
            }],
            "Active",
            [target with
            {
                SelectedTerminalId = terminal.TerminalId,
                SelectionRevision = target.SelectedTerminalId is null ? 4_107 : target.SelectionRevision + 1
            }]);

    [Fact]
    public async Task LogonLinklyAsync_delegates_to_local_terminal_client()
    {
        var expected = new LinklyLogonResult(true, "logged on", "00", "APPROVED");
        var linklyClient = new FakeLinklyTerminalClient { LogonResult = expected };
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(),
            new FakeSquareTerminalSetupClient(),
            linklyClient);
        using var cancellationTokenSource = new CancellationTokenSource();

        var result = await service.LogonLinklyAsync(
            "192.168.1.10",
            2011,
            TimeSpan.FromSeconds(45),
            cancellationTokenSource.Token);

        Assert.Same(expected, result);
        Assert.Equal("192.168.1.10", linklyClient.LastLogonHost);
        Assert.Equal(2011, linklyClient.LastLogonPort);
        Assert.Equal(TimeSpan.FromSeconds(45), linklyClient.LastLogonTimeout);
        Assert.Equal(cancellationTokenSource.Token, linklyClient.LastLogonCancellationToken);
    }

    [Fact]
    public async Task GetSquareAccessTokenAsync_returns_null_without_reading_local_store()
    {
        var store = new FakeCardTerminalSettingsStore();
        var service = new CardTerminalSetupService(store, new FakeSquareTerminalSetupClient(), new FakeLinklyTerminalClient());

        var token = await service.GetSquareAccessTokenAsync();

        Assert.Null(token);
        Assert.Equal(0, store.CachedTokenReadCount);
        Assert.Equal(0, store.EnvironmentTokenReadCount);
        Assert.Equal(0, store.ForceRefreshCount);
    }

    [Fact]
    public async Task ListSquareLocationsAsync_without_local_square_token_still_calls_backend_setup_client()
    {
        var store = new FakeCardTerminalSettingsStore();
        var squareClient = new FakeSquareTerminalSetupClient();
        var service = new CardTerminalSetupService(store, squareClient, new FakeLinklyTerminalClient());

        var locations = await service.ListSquareLocationsAsync(null, CardTerminalEnvironment.Production);

        Assert.Single(locations);
        Assert.Equal(string.Empty, Assert.Single(squareClient.Tokens));
        Assert.Equal(0, store.CachedTokenReadCount);
        Assert.Equal(0, store.EnvironmentTokenReadCount);
        Assert.Equal(0, store.ForceRefreshCount);
    }

    [Fact]
    public async Task CreateSquareDeviceCodeAsync_without_local_square_token_still_calls_backend_setup_client()
    {
        var store = new FakeCardTerminalSettingsStore();
        var squareClient = new FakeSquareTerminalSetupClient();
        var service = new CardTerminalSetupService(store, squareClient, new FakeLinklyTerminalClient());

        var result = await service.CreateSquareDeviceCodeAsync(null, CardTerminalEnvironment.Production, "LOC-1", "Counter 2");

        Assert.Equal("PAIR123", result.Code);
        Assert.Equal(string.Empty, Assert.Single(squareClient.Tokens));
        Assert.Equal(0, store.CachedTokenReadCount);
        Assert.Equal(0, store.EnvironmentTokenReadCount);
        Assert.Equal(0, store.ForceRefreshCount);
    }

    [Fact]
    public async Task ListSquareDevicesAsync_localizes_sandbox_test_device_status_display_name()
    {
        var localization = new LocalizationService();
        localization.SetCulture(LocalizationService.ChineseCultureName);
        var squareClient = new FakeSquareTerminalSetupClient
        {
            Devices =
            [
                new(
                    SquareSandboxTerminalDeviceIds.BuyerCanceled,
                    "Sandbox: cancel by buyer",
                    SquareSandboxTerminalDeviceIds.TestDeviceStatus)
            ]
        };
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(),
            squareClient,
            new FakeLinklyTerminalClient(),
            localization: localization);

        var devices = await service.ListSquareDevicesAsync(null, CardTerminalEnvironment.Sandbox, "LOC-1");

        var device = Assert.Single(devices);
        Assert.Equal(SquareSandboxTerminalDeviceIds.TestDeviceStatus, device.Status);
        Assert.Equal("Square 沙盒测试", device.StatusDisplayName);
    }

    [Fact]
    public async Task ListSquareLocationsAsync_propagates_setup_client_failure_without_refresh()
    {
        var store = new FakeCardTerminalSettingsStore();
        var expected = new SquareApiException(
            "Square locations request failed with status 401 (Unauthorized).",
            HttpStatusCode.Unauthorized);
        var squareClient = new FakeSquareTerminalSetupClient
        {
            ListLocationsException = expected
        };
        var service = new CardTerminalSetupService(store, squareClient, new FakeLinklyTerminalClient());

        var exception = await Assert.ThrowsAsync<SquareApiException>(() =>
            service.ListSquareLocationsAsync(null, CardTerminalEnvironment.Production));

        Assert.Same(expected, exception);
        Assert.Equal(string.Empty, Assert.Single(squareClient.Tokens));
        Assert.Equal(0, store.CachedTokenReadCount);
        Assert.Equal(0, store.EnvironmentTokenReadCount);
        Assert.Equal(0, store.ForceRefreshCount);
    }

    [Fact]
    public async Task Device_code_operations_are_blocked_in_sandbox()
    {
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(),
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ListSquareDeviceCodesAsync(null, CardTerminalEnvironment.Sandbox, "LOC-1"));

        Assert.Equal("Square Device Codes are only supported in Production.", exception.Message);
    }

    [Fact]
    public async Task SaveSquareAsync_normalizes_devices_api_device_id_before_saving()
    {
        var store = new FakeCardTerminalSettingsStore();
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient());
        var configuration = CardTerminalConfiguration.Default with
        {
            Processor = CardProcessorKind.Square,
            SquareLocationId = "LOC-1",
            SquareDeviceId = "device:533CS145C3000413"
        };

        await service.SaveSquareAsync(configuration, squareAccessToken: null);

        Assert.NotNull(store.SavedConfiguration);
        Assert.Equal("533CS145C3000413", store.SavedConfiguration!.SquareDeviceId);
    }

    [Fact]
    public async Task PairLinklyCloudAsync_uses_local_credentials_and_saves_protected_secret()
    {
        var store = new FakeCardTerminalSettingsStore();
        store.LinklyCloudCredentials[CardTerminalEnvironment.Production] =
            new LinklyCloudCredentialSettings("local-user", "local-password", true);
        var cloudApi = new FakeLinklyCloudApiClient { PairSecret = "cloud-secret" };
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudApiClient: cloudApi);

        var result = await service.PairLinklyCloudAsync(CardTerminalEnvironment.Production, "12345", null, null);

        Assert.True(result.Succeeded);
        Assert.Equal("local-user", cloudApi.LastUsername);
        Assert.Equal("local-password", cloudApi.LastPassword);
        Assert.Equal("12345", cloudApi.LastPairCode);
        Assert.Equal("cloud-secret", store.SavedLinklyCloudSecret);
    }

    [Fact]
    public async Task PairLinklyCloudAsync_uses_current_input_before_saved_credentials()
    {
        var store = new FakeCardTerminalSettingsStore();
        store.LinklyCloudCredentials[CardTerminalEnvironment.Sandbox] =
            new LinklyCloudCredentialSettings("saved-user", "saved-password", true);
        var cloudApi = new FakeLinklyCloudApiClient { PairSecret = "cloud-secret" };
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudApiClient: cloudApi);

        var result = await service.PairLinklyCloudAsync(
            CardTerminalEnvironment.Sandbox,
            "12345",
            "current-user",
            "current-password");

        Assert.True(result.Succeeded);
        Assert.Equal("current-user", cloudApi.LastUsername);
        Assert.Equal("current-password", cloudApi.LastPassword);
    }

    [Fact]
    public async Task PairLinklyCloudAsync_blocks_missing_local_credentials()
    {
        var cloudApi = new FakeLinklyCloudApiClient { PairSecret = "cloud-secret" };
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(),
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudApiClient: cloudApi);

        var result = await service.PairLinklyCloudAsync(CardTerminalEnvironment.Production, "12345", null, null);

        Assert.False(result.Succeeded);
        Assert.Equal("Save the Linkly Cloud API username and password first.", result.Message);
        Assert.Null(cloudApi.LastPairCode);
    }

    [Fact]
    public async Task PairLinklyCloudAsync_does_not_require_pos_vendor_id()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LINKLY_POS_VENDOR_ID"] = null
        });
        var store = new FakeCardTerminalSettingsStore();
        var cloudApi = new FakeLinklyCloudApiClient { PairSecret = "cloud-secret" };
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            cloudApi);

        var result = await service.PairLinklyCloudAsync(CardTerminalEnvironment.Production, "12345", "user", "password");

        Assert.True(result.Succeeded);
        Assert.Equal("user", cloudApi.LastUsername);
        Assert.Equal("password", cloudApi.LastPassword);
        Assert.Equal("12345", cloudApi.LastPairCode);
        Assert.Equal("cloud-secret", store.SavedLinklyCloudSecret);
    }

    [Fact]
    public async Task PairLinklyCloudAsync_rejects_sandbox_auth_host_for_production_environment()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LINKLY_CLOUD_AUTH_BASE_URL_PRODUCTION"] = "https://auth.sandbox.cloud.pceftpos.com/v1/"
        });
        var store = new FakeCardTerminalSettingsStore();
        store.LinklyCloudCredentials[CardTerminalEnvironment.Production] =
            new LinklyCloudCredentialSettings("local-user", "local-password", true);
        var cloudApi = new FakeLinklyCloudApiClient { PairSecret = "cloud-secret" };
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudApiClient: cloudApi);

        var result = await service.PairLinklyCloudAsync(CardTerminalEnvironment.Production, "12345", null, null);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "Linkly Cloud Auth endpoint does not match the selected Production environment. Update the configured host and try again.",
            result.Message);
        Assert.Null(cloudApi.LastPairCode);
        Assert.Null(store.SavedLinklyCloudSecret);
    }

    [Fact]
    public async Task PairLinklyCloudAsync_reports_official_auth_failure_causes()
    {
        var store = new FakeCardTerminalSettingsStore();
        store.LinklyCloudCredentials[CardTerminalEnvironment.Sandbox] =
            new LinklyCloudCredentialSettings("sandbox-user", "sandbox-password", true);
        var cloudApi = new FakeLinklyCloudApiClient
        {
            PairException = new LinklyCloudApiException(
                "Linkly Cloud pairing failed.",
                HttpStatusCode.Unauthorized)
        };
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudApiClient: cloudApi);

        var result = await service.PairLinklyCloudAsync(CardTerminalEnvironment.Sandbox, "123456", null, null);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "Linkly Cloud pairing failed. Check the Sandbox VPP pair code and Cloud test account username/password.",
            result.Message);
        Assert.Null(store.SavedLinklyCloudSecret);
    }

    [Fact]
    public async Task SaveLinklyCloudCredentialAsync_saves_local_credential_and_upserts_backend_store_credential()
    {
        var store = new FakeCardTerminalSettingsStore();
        var credentialApiClient = new FakeLinklyCloudCredentialApiClient();
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudCredentialApiClient: credentialApiClient);

        await service.SaveLinklyCloudCredentialAsync(
            CardTerminalEnvironment.Sandbox,
            "sandbox-user",
            "sandbox-password",
            syncBackendCredential: true);

        Assert.Equal(
            (CardTerminalEnvironment.Sandbox, "sandbox-user", "sandbox-password"),
            store.SavedLinklyCloudCredential);
        Assert.Equal(
            (CardTerminalEnvironment.Sandbox, "sandbox-user", "sandbox-password"),
            credentialApiClient.LastCredentialUpsertRequest);
    }

    [Fact]
    public async Task SaveLinklyCloudCredentialAsync_keeps_local_credentials_separate_per_environment()
    {
        var store = new FakeCardTerminalSettingsStore();
        store.LinklyCloudCredentials[CardTerminalEnvironment.Production] =
            new LinklyCloudCredentialSettings("prod-user", "prod-password", true);
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient());

        await service.SaveLinklyCloudCredentialAsync(
            CardTerminalEnvironment.Sandbox,
            "sandbox-user",
            "sandbox-password");

        Assert.Equal("prod-user", store.LinklyCloudCredentials[CardTerminalEnvironment.Production].Username);
        Assert.Equal("sandbox-user", store.LinklyCloudCredentials[CardTerminalEnvironment.Sandbox].Username);
    }

    [Fact]
    public async Task PairLinklyCloudAsync_upserts_backend_terminal_credential_with_pos_id()
    {
        var store = new FakeCardTerminalSettingsStore();
        store.LinklyCloudCredentials[CardTerminalEnvironment.Sandbox] =
            new LinklyCloudCredentialSettings("sandbox-user", "sandbox-password", true);
        store.PosIds[CardTerminalEnvironment.Sandbox] = "f4b8344c-22b8-4d2a-9ca7-e9a846f46c8c";
        var cloudApi = new FakeLinklyCloudApiClient { PairSecret = "cloud-secret" };
        var credentialApiClient = new FakeLinklyCloudCredentialApiClient();
        var deviceState = new DeviceAuthorizationState();
        deviceState.Set(new DeviceAuthorizationContext("TERM-1", "S01", "HW-1", "AUTH-1"));
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudApiClient: cloudApi,
            linklyCloudCredentialApiClient: credentialApiClient,
            deviceAuthorizationState: deviceState);

        var result = await service.PairLinklyCloudAsync(
            CardTerminalEnvironment.Sandbox,
            "12345",
            null,
            null,
            syncBackendTerminalCredential: true);

        Assert.True(result.Succeeded);
        Assert.Equal("cloud-secret", store.SavedLinklyCloudSecret);
        Assert.Equal(
            (CardTerminalEnvironment.Sandbox, "cloud-secret", "f4b8344c-22b8-4d2a-9ca7-e9a846f46c8c"),
            credentialApiClient.LastTerminalCredentialUpsertRequest);
        Assert.Equal(
            (CardTerminalEnvironment.Sandbox, "S01", "TERM-1"),
            store.LastPosIdRequest);
    }

    [Fact]
    public async Task PairLinklyCloudAsync_direct_sync_does_not_upsert_backend_terminal_credential()
    {
        var store = new FakeCardTerminalSettingsStore();
        store.LinklyCloudCredentials[CardTerminalEnvironment.Sandbox] =
            new LinklyCloudCredentialSettings("sandbox-user", "sandbox-password", true);
        var cloudApi = new FakeLinklyCloudApiClient { PairSecret = "cloud-secret" };
        var credentialApiClient = new FakeLinklyCloudCredentialApiClient();
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudApiClient: cloudApi,
            linklyCloudCredentialApiClient: credentialApiClient);

        var result = await service.PairLinklyCloudAsync(CardTerminalEnvironment.Sandbox, "12345", null, null);

        Assert.True(result.Succeeded);
        Assert.Equal("cloud-secret", store.SavedLinklyCloudSecret);
        Assert.Null(credentialApiClient.LastTerminalCredentialUpsertRequest);
        Assert.Null(store.LastPosIdRequest);
    }

    [Fact]
    public async Task SaveLinklyCloudAsync_saves_linkly_processor_with_cloud_mode()
    {
        var store = new FakeCardTerminalSettingsStore();
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient());

        await service.SaveLinklyCloudAsync(CardTerminalConfiguration.Default with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyConnectionMode = LinklyConnectionMode.Local
        });

        Assert.NotNull(store.SavedConfiguration);
        Assert.Equal(CardProcessorKind.Linkly, store.SavedConfiguration!.Processor);
        Assert.Equal(LinklyConnectionMode.CloudDirectSync, store.SavedConfiguration.LinklyConnectionMode);
    }

    [Fact]
    public async Task SaveLinklyCloudAsync_preserves_backend_async_mode()
    {
        var store = new FakeCardTerminalSettingsStore();
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient());

        await service.SaveLinklyCloudAsync(CardTerminalConfiguration.Default with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
        });

        Assert.NotNull(store.SavedConfiguration);
        Assert.Equal(LinklyConnectionMode.CloudBackendAsync, store.SavedConfiguration!.LinklyConnectionMode);
    }

    [Fact]
    public async Task TestLinklyCloudConnectionAsync_uses_secret_and_endpoint_for_requested_environment()
    {
        var store = new FakeCardTerminalSettingsStore();
        store.LinklyCloudSecrets[CardTerminalEnvironment.Production] = "production-secret";
        store.LinklyCloudSecrets[CardTerminalEnvironment.Sandbox] = "sandbox-secret";
        var cloudTerminal = new FakeLinklyCloudTerminalClient
        {
            TestResult = new LinklyConnectionTestResult(true, "sandbox ready")
        };
        var deviceState = new DeviceAuthorizationState();
        deviceState.Set(new DeviceAuthorizationContext("TERM-1", "S01", "HW-1", "AUTH-1"));
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudTerminalClient: cloudTerminal,
            deviceAuthorizationState: deviceState);

        var result = await service.TestLinklyCloudConnectionAsync(CardTerminalEnvironment.Sandbox);

        Assert.True(result.Succeeded);
        Assert.NotNull(cloudTerminal.LastSettings);
        Assert.Equal(CardTerminalEnvironment.Sandbox, cloudTerminal.LastSettings!.Environment);
        Assert.Equal("sandbox-secret", cloudTerminal.LastSettings.LinklyCloudSecret);
        Assert.Equal("https://auth.sandbox.cloud.pceftpos.com/v1/", cloudTerminal.LastSettings.LinklyCloudAuthBaseUrl);
        Assert.Equal("https://rest.pos.sandbox.cloud.pceftpos.com/v1/", cloudTerminal.LastSettings.LinklyCloudRestBaseUrl);
        Assert.Equal(CardTerminalSettings.SandboxPlaceholderLinklyPosVendorId, cloudTerminal.LastSettings.LinklyPosVendorId);
        Assert.Equal("S01", cloudTerminal.LastStoreCode);
        Assert.Equal("TERM-1", cloudTerminal.LastDeviceCode);
    }

    [Fact]
    public async Task TestLinklyCloudConnectionAsync_rejects_production_rest_host_for_sandbox_environment()
    {
        using var variables = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["HBPOS_LINKLY_CLOUD_REST_BASE_URL_SANDBOX"] = "https://rest.pos.cloud.pceftpos.com/v1/"
        });
        var store = new FakeCardTerminalSettingsStore();
        store.LinklyCloudSecrets[CardTerminalEnvironment.Sandbox] = "sandbox-secret";
        var cloudTerminal = new FakeLinklyCloudTerminalClient
        {
            TestResult = new LinklyConnectionTestResult(true, "sandbox ready")
        };
        var deviceState = new DeviceAuthorizationState();
        deviceState.Set(new DeviceAuthorizationContext("TERM-1", "S01", "HW-1", "AUTH-1"));
        var service = new CardTerminalSetupService(
            store,
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyCloudTerminalClient: cloudTerminal,
            deviceAuthorizationState: deviceState);

        var result = await service.TestLinklyCloudConnectionAsync(CardTerminalEnvironment.Sandbox);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "Linkly Cloud REST endpoint does not match the selected Sandbox environment. Update the configured host and try again.",
            result.Message);
        Assert.Null(cloudTerminal.LastSettings);
    }

    [Fact]
    public async Task TestLinklyCloudBackendTransactionStatusAsync_calls_backend_client()
    {
        var backendClient = new FakeLinklyBackendTerminalClient
        {
            StatusTestResult = new LinklyConnectionTestResult(true, "status accepted")
        };
        var service = new CardTerminalSetupService(
            new FakeCardTerminalSettingsStore(),
            new FakeSquareTerminalSetupClient(),
            new FakeLinklyTerminalClient(),
            linklyBackendTerminalClient: backendClient);

        var result = await service.TestLinklyCloudBackendTransactionStatusAsync(CardTerminalEnvironment.Sandbox);

        Assert.True(result.Succeeded);
        Assert.Equal("status accepted", result.Message);
        Assert.Equal(CardTerminalEnvironment.Sandbox, backendClient.LastStatusTestEnvironment);
        Assert.Equal(1, backendClient.StatusTestCallCount);
    }

    private sealed class FakeCardTerminalSettingsStore : ICardTerminalSettingsStore
    {
        public int CachedTokenReadCount { get; private set; }

        public int EnvironmentTokenReadCount { get; private set; }

        public int ForceRefreshCount { get; private set; }

        public CardTerminalConfiguration? SavedConfiguration { get; private set; }

        public string? SavedLinklyCloudSecret { get; private set; }

        public Dictionary<CardTerminalEnvironment, string?> LinklyCloudSecrets { get; } = [];

        public Dictionary<CardTerminalEnvironment, string> PosIds { get; } = [];

        public Dictionary<CardTerminalEnvironment, LinklyCloudCredentialSettings> LinklyCloudCredentials { get; } = [];

        public (CardTerminalEnvironment Environment, string Username, string Password)? SavedLinklyCloudCredential { get; private set; }

        public (CardTerminalEnvironment Environment, string StoreCode, string DeviceCode)? LastPosIdRequest { get; private set; }

        public Task<CardTerminalConfiguration> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CardTerminalConfiguration.Default with
            {
                Processor = CardProcessorKind.Square,
                HasProtectedSquareAccessToken = true
            });
        }

        public Task SaveAsync(
            CardTerminalConfiguration configuration,
            string? squareAccessToken,
            CancellationToken cancellationToken = default)
        {
            SavedConfiguration = configuration;
            return Task.CompletedTask;
        }

        public Task<string?> GetSquareAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            CachedTokenReadCount++;
            return Task.FromResult<string?>(LocalToken);
        }

        public Task<string?> GetSquareAccessTokenAsync(
            CardTerminalEnvironment environment,
            bool forceRefresh,
            CancellationToken cancellationToken = default)
        {
            EnvironmentTokenReadCount++;
            if (forceRefresh)
            {
                ForceRefreshCount++;
            }

            return Task.FromResult<string?>(LocalToken);
        }

        public Task<string?> GetTokenAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return GetSquareAccessTokenAsync(environment, forceRefresh: false, cancellationToken);
        }

        public Task<string?> RefreshTokenAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return GetSquareAccessTokenAsync(environment, forceRefresh: true, cancellationToken);
        }

        public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Square,
                SquareAccessToken = LocalToken
            });
        }

        public Task<string?> GetLinklyCloudSecretAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LinklyCloudSecrets.TryGetValue(environment, out var secret)
                ? secret
                : "linkly-secret");
        }

        public Task SaveLinklyCloudSecretAsync(
            CardTerminalEnvironment environment,
            string secret,
            CancellationToken cancellationToken = default)
        {
            SavedLinklyCloudSecret = secret;
            LinklyCloudSecrets[environment] = secret;
            return Task.CompletedTask;
        }

        public Task<string> GetOrCreateLinklyCloudPosIdAsync(
            CardTerminalEnvironment environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken = default)
        {
            LastPosIdRequest = (environment, storeCode, deviceCode);
            return Task.FromResult(PosIds.TryGetValue(environment, out var posId)
                ? posId
                : Guid.NewGuid().ToString("D"));
        }

        public Task<LinklyCloudCredentialSettings> GetLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LinklyCloudCredentials.TryGetValue(environment, out var credential)
                ? credential
                : new LinklyCloudCredentialSettings(null, null, false));
        }

        public Task SaveLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            var credential = new LinklyCloudCredentialSettings(username, password, true);
            LinklyCloudCredentials[environment] = credential;
            SavedLinklyCloudCredential = (environment, username, password);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLinklyCloudTerminalClient : ILinklyCloudTerminalClient
    {
        public LinklyConnectionTestResult TestResult { get; init; } = new(false, "not ready");

        public CardTerminalSettings? LastSettings { get; private set; }

        public string? LastStoreCode { get; private set; }

        public string? LastDeviceCode { get; private set; }

        public Task<LinklyConnectionTestResult> TestConnectionAsync(
            CardTerminalSettings settings,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken = default)
        {
            LastSettings = settings;
            LastStoreCode = storeCode;
            LastDeviceCode = deviceCode;
            return Task.FromResult(TestResult);
        }

        public Task<PaymentAuthorizationResult> PurchaseAsync(
            decimal amount,
            Hbpos.Client.Wpf.Models.PosSessionState session,
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            Hbpos.Client.Wpf.Models.PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeLinklyBackendTerminalClient : ILinklyBackendTerminalClient
    {
        public LinklyCloudTerminalListResponse TerminalDirectory { get; init; } =
            new("Sandbox", null, null, []);

        public LinklyCloudTerminalListResponse? AssignmentResult { get; init; }

        public Exception? AssignmentException { get; init; }

        public Exception? DirectoryException { get; init; }

        public int DirectoryCallCount { get; private set; }

        public int AssignmentCallCount { get; private set; }

        public LinklyCloudTerminalSelectionResponse TerminalSelection { get; init; } =
            new("Sandbox", Guid.Empty, 1);

        public LinklyCloudTerminalPairResponse TerminalPairResult { get; init; } =
            new(Guid.Empty, "Sandbox", string.Empty, "Unpaired", false, string.Empty);

        public CardTerminalEnvironment? LastTerminalEnvironment { get; private set; }

        public Guid? LastTerminalId { get; private set; }

        public long? LastExpectedRevision { get; private set; }

        public string? LastPairCode { get; private set; }

        public LinklyConnectionTestResult StatusTestResult { get; init; } = new(false, "status failed");

        public CardTerminalEnvironment? LastStatusTestEnvironment { get; private set; }

        public int StatusTestCallCount { get; private set; }

        public LinklyCloudTerminalConnectionTestRequest? LastConnectionTestRequest { get; private set; }

        public LinklyCloudTerminalAssignmentRequest? LastAssignmentRequest { get; private set; }

        public Task<LinklyCloudTerminalListResponse> GetTerminalsAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            LastTerminalEnvironment = environment;
            DirectoryCallCount++;
            if (DirectoryException is not null)
            {
                return Task.FromException<LinklyCloudTerminalListResponse>(DirectoryException);
            }
            return Task.FromResult(TerminalDirectory);
        }

        public Task<LinklyCloudTerminalSelectionResponse> SelectTerminalAsync(
            CardTerminalEnvironment environment,
            Guid terminalId,
            long? expectedRevision,
            CancellationToken cancellationToken = default)
        {
            LastTerminalEnvironment = environment;
            LastTerminalId = terminalId;
            LastExpectedRevision = expectedRevision;
            return Task.FromResult(TerminalSelection);
        }

        public Task<LinklyCloudTerminalPairResponse> PairTerminalAsync(
            CardTerminalEnvironment environment,
            Guid terminalId,
            string pairCode,
            CancellationToken cancellationToken = default)
        {
            LastTerminalEnvironment = environment;
            LastTerminalId = terminalId;
            LastPairCode = pairCode;
            return Task.FromResult(TerminalPairResult);
        }

        public Task<LinklyCloudTerminalConnectionTestResponse> TestTerminalConnectionAsync(
            Guid terminalId,
            LinklyCloudTerminalConnectionTestRequest request,
            CancellationToken cancellationToken = default)
        {
            LastTerminalId = terminalId;
            LastConnectionTestRequest = request;
            return Task.FromResult(new LinklyCloudTerminalConnectionTestResponse(
                terminalId,
                request.Environment,
                request.ExpectedTerminalVersion,
                request.ExpectedAssignedDeviceCode,
                request.ExpectedAssignmentRevision,
                true,
                "connected",
                DateTimeOffset.UtcNow,
                "Connected"));
        }

        public Task<LinklyCloudTerminalListResponse> AssignTerminalAsync(
            Guid terminalId,
            LinklyCloudTerminalAssignmentRequest request,
            CancellationToken cancellationToken = default)
        {
            LastTerminalId = terminalId;
            LastAssignmentRequest = request;
            AssignmentCallCount++;
            if (AssignmentException is not null)
            {
                return Task.FromException<LinklyCloudTerminalListResponse>(AssignmentException);
            }

            return Task.FromResult(AssignmentResult ?? new LinklyCloudTerminalListResponse(
                request.Environment,
                request.TargetDeviceCode is null ? null : terminalId,
                1,
                [new LinklyCloudTerminalSummary(
                    terminalId, 1, "Line", "Ready", false, true, null, null,
                    request.TargetDeviceCode,
                    request.TargetDeviceCode is null
                        ? 0
                        : request.ExpectedTargetTerminalId is null ? 4_107 : request.ExpectedTargetSelectionRevision + 1,
                    request.ExpectedTerminalVersion + "-next")],
                "Active",
                request.TargetDeviceCode is null
                    ? []
                    : [new LinklyCloudAssignableDevice(
                        request.TargetDeviceCode, "WPF", true, terminalId,
                        request.ExpectedTargetTerminalId is null ? 4_107 : request.ExpectedTargetSelectionRevision + 1)]));
        }

        public Task<LinklyConnectionTestResult> TestConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyConnectionTestResult> TestTransactionStatusAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            LastStatusTestEnvironment = environment;
            StatusTestCallCount++;
            return Task.FromResult(StatusTestResult);
        }

        public Task<PaymentAuthorizationResult> PurchaseAsync(
            decimal amount,
            Hbpos.Client.Wpf.Models.PosSessionState session,
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            Hbpos.Client.Wpf.Models.PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudBackendSessionResponse> ResumeSessionUntilFinalAsync(
            CardTerminalSettings settings,
            LinklyCloudBackendSessionResponse activeStatus,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task AcknowledgeSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task AcknowledgeSupervisorResolvedSessionAsync(
            CardTerminalSettings settings,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeUnresolvedSettlementReader(bool hasUnresolved) : ILinklyUnresolvedSettlementReader
    {
        public Task<bool> HasUnresolvedAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken = default) => Task.FromResult(hasUnresolved);
    }

    private sealed class FakeLinklyPaymentAttemptContextAccessor : ILinklyPaymentAttemptContextAccessor
    {
        public LinklyPaymentAttemptContext? CurrentValue { get; init; }

        public LinklyPaymentAttemptContext? Current => CurrentValue;

        public IDisposable Begin(LinklyPaymentAttemptContext context) => throw new NotSupportedException();
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = new(StringComparer.OrdinalIgnoreCase);

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var entry in values)
            {
                _originalValues[entry.Key] = Environment.GetEnvironmentVariable(entry.Key);
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }

        public void Dispose()
        {
            foreach (var entry in _originalValues)
            {
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }
    }

    private sealed class FakeSquareTerminalSetupClient : ISquareTerminalSetupClient
    {
        public Exception? ListLocationsException { get; init; }

        public List<string> Tokens { get; } = [];

        public IReadOnlyList<SquareDeviceOption> Devices { get; init; } = [];

        public Task<IReadOnlyList<SquareLocationOption>> ListLocationsAsync(
            string accessToken,
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            Tokens.Add(accessToken);
            if (ListLocationsException is not null)
            {
                throw ListLocationsException;
            }

            IReadOnlyList<SquareLocationOption> locations = [new("LOC-1", "Main")];
            return Task.FromResult(locations);
        }

        public Task<IReadOnlyList<SquareDeviceOption>> ListDevicesAsync(
            string accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            CancellationToken cancellationToken = default)
        {
            Tokens.Add(accessToken);
            return Task.FromResult(Devices);
        }

        public Task<IReadOnlyList<SquareDeviceCodeOption>> ListDeviceCodesAsync(
            string accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SquareDeviceCodeOption> CreateDeviceCodeAsync(
            string accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            string name,
            CancellationToken cancellationToken = default)
        {
            Tokens.Add(accessToken);
            return Task.FromResult(new SquareDeviceCodeOption(
                "DC-1",
                name,
                "PAIR123",
                "UNPAIRED",
                locationId,
                null,
                DateTimeOffset.UtcNow.AddMinutes(5),
                DateTimeOffset.UtcNow));
        }

        public Task<SquareDeviceCodeOption> GetDeviceCodeAsync(
            string accessToken,
            CardTerminalEnvironment environment,
            string deviceCodeId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeLinklyTerminalClient : ILinklyTerminalClient
    {
        public LinklyLogonResult LogonResult { get; init; } = new(false);

        public string? LastLogonHost { get; private set; }

        public int? LastLogonPort { get; private set; }

        public TimeSpan? LastLogonTimeout { get; private set; }

        public CancellationToken LastLogonCancellationToken { get; private set; }

        public Task<LinklyConnectionTestResult> TestConnectionAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(false));
        }

        public Task<LinklyLogonResult> LogonAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            LastLogonHost = host;
            LastLogonPort = port;
            LastLogonTimeout = timeout;
            LastLogonCancellationToken = cancellationToken;
            return Task.FromResult(LogonResult);
        }

        public Task<PaymentAuthorizationResult> PurchaseAsync(
            decimal amount,
            Hbpos.Client.Wpf.Models.PosSessionState session,
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PaymentAuthorizationResult> PurchaseWithReferenceAsync(
            decimal amount,
            Hbpos.Client.Wpf.Models.PosSessionState session,
            CardTerminalSettings settings,
            string txnRef,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PaymentAuthorizationResult> RecoverLastTransactionAsync(
            decimal amount,
            Hbpos.Client.Wpf.Models.PosSessionState session,
            CardTerminalSettings settings,
            string txnRef,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            Hbpos.Client.Wpf.Models.PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PaymentAuthorizationResult> VoidAsync(
            decimal amount,
            Hbpos.Client.Wpf.Models.PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeLinklyCloudCredentialApiClient : ILinklyCloudCredentialApiClient
    {
        public (CardTerminalEnvironment Environment, string Username, string Password)? LastCredentialUpsertRequest { get; private set; }

        public (CardTerminalEnvironment Environment, string Secret, string PosId)? LastTerminalCredentialUpsertRequest { get; private set; }

        public Task<LinklyCloudCredentialUpsertResponse> UpsertCredentialAsync(
            CardTerminalEnvironment environment,
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            LastCredentialUpsertRequest = (environment, username, password);
            return Task.FromResult(new LinklyCloudCredentialUpsertResponse(
                "S01",
                environment.ToString(),
                username,
                HasPassword: true,
                DateTimeOffset.UtcNow));
        }

        public Task<LinklyCloudBackendTerminalCredentialResponse> UpsertBackendTerminalCredentialAsync(
            CardTerminalEnvironment environment,
            string secret,
            string posId,
            CancellationToken cancellationToken = default)
        {
            LastTerminalCredentialUpsertRequest = (environment, secret, posId);
            return Task.FromResult(new LinklyCloudBackendTerminalCredentialResponse(
                environment.ToString(),
                "S01",
                "TERM-1",
                HasSecret: true,
                posId,
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeLinklyCloudApiClient : ILinklyCloudApiClient
    {
        public string PairSecret { get; init; } = "secret";

        public Exception? PairException { get; init; }

        public string? LastUsername { get; private set; }

        public string? LastPassword { get; private set; }

        public string? LastPairCode { get; private set; }

        public Task<string> PairAsync(
            string authBaseUrl,
            string username,
            string password,
            string pairCode,
            CancellationToken cancellationToken = default)
        {
            LastUsername = username;
            LastPassword = password;
            LastPairCode = pairCode;
            if (PairException is not null)
            {
                throw PairException;
            }

            return Task.FromResult(PairSecret);
        }

        public Task<LinklyCloudToken> GetTokenAsync(
            CardTerminalSettings settings,
            string posId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudStatusResult> SendStatusAsync(
            CardTerminalSettings settings,
            string token,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudLogonResult> SendLogonAsync(
            CardTerminalSettings settings,
            string token,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudTransactionResult> SendTransactionAsync(
            CardTerminalSettings settings,
            string token,
            LinklyCloudTransactionRequest request,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LinklyCloudTransactionResult> GetTransactionAsync(
            CardTerminalSettings settings,
            string token,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SendKeyAsync(
            CardTerminalSettings settings,
            string token,
            string sessionId,
            string key,
            string? data,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
