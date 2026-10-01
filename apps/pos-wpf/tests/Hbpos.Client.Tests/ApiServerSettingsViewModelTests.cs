using System.Net;
using System.Net.Http.Json;
using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Health;

namespace Hbpos.Client.Tests;

public sealed class ApiServerSettingsViewModelTests
{
    [Fact]
    public void Quick_fill_commands_reuse_service_constants_without_saving()
    {
        var savedAddresses = new List<string>();
        var viewModel = CreateViewModel(_ => OnlineResponse(), savedAddresses: savedAddresses);

        viewModel.UseDevelopmentAddressCommand.Execute(null);
        Assert.Equal(ApiServerSettingsService.DevelopmentApiBaseAddress, viewModel.ServerAddressText);

        viewModel.UseReleaseAddressCommand.Execute(null);
        Assert.Equal(ApiServerSettingsService.ReleaseApiBaseAddress, viewModel.ServerAddressText);
        Assert.Empty(savedAddresses);
    }

    [Fact]
    public async Task Runtime_switch_records_server_change_with_cashier_captured_before_session_is_cleared()
    {
        var currentAddress = "https://prod.example.com/pos-api/";
        var cashierContext = new CashierSessionContext();
        cashierContext.SetCurrent(CreateCashier());
        var logger = new RecordingOperationAuditLogger();
        var coordinator = new FakeSwitchCoordinator(target =>
        {
            // 模拟真实切换：提交后地址变更，并在 PostCommit 中清空收银员会话。
            currentAddress = target;
            cashierContext.Clear();
            return new ApiServerSwitchResult(ApiServerSwitchStatus.Success);
        });
        var viewModel = CreateSwitchViewModel(() => currentAddress, coordinator, logger, cashierContext);
        viewModel.ServerAddressText = "https://test.example.com/pos-api/";

        await viewModel.SaveCommand.ExecuteAsync(null);

        // 切换服务器决定收银数据发往哪个后台，审计必须记下操作人和前后地址。
        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("API_SERVER_CHANGE", auditEvent.OperationType);
        Assert.Equal("Succeeded", auditEvent.Outcome);
        Assert.Equal("RUNTIME_SWITCH", auditEvent.ReasonCode);
        Assert.Equal("C001", auditEvent.CashierId);
        Assert.Equal("Alice", auditEvent.CashierName);
        Assert.Equal(
            "https://prod.example.com/pos-api/ -> https://test.example.com/pos-api/",
            auditEvent.SafeMessage);
    }

    [Fact]
    public async Task Blocked_runtime_switch_records_denied_server_change()
    {
        var cashierContext = new CashierSessionContext();
        cashierContext.SetCurrent(CreateCashier());
        var logger = new RecordingOperationAuditLogger();
        var coordinator = new FakeSwitchCoordinator(_ =>
            new ApiServerSwitchResult(ApiServerSwitchStatus.Blocked, "settings.serverAddress.status.blocked"));
        var viewModel = CreateSwitchViewModel(() => "https://prod.example.com/pos-api/", coordinator, logger, cashierContext);
        viewModel.ServerAddressText = "https://test.example.com/pos-api/";

        await viewModel.SaveCommand.ExecuteAsync(null);

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("API_SERVER_CHANGE", auditEvent.OperationType);
        Assert.Equal("Denied", auditEvent.Outcome);
        Assert.Equal("BLOCKED", auditEvent.ReasonCode);
        Assert.Equal("C001", auditEvent.CashierId);
    }

    [Fact]
    public async Task Same_address_switch_does_not_record_server_change()
    {
        var logger = new RecordingOperationAuditLogger();
        var coordinator = new FakeSwitchCoordinator(_ => new ApiServerSwitchResult(ApiServerSwitchStatus.SameAddress));
        var viewModel = CreateSwitchViewModel(() => "https://prod.example.com/pos-api/", coordinator, logger, new CashierSessionContext());
        viewModel.ServerAddressText = "https://prod.example.com/pos-api/";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Empty(logger.Events);
    }

    [Fact]
    public void Load_reads_current_server_address()
    {
        var viewModel = CreateViewModel(
            _ => OnlineResponse(),
            currentAddress: "https://current.example.com/base");

        viewModel.Load();

        Assert.Equal("https://current.example.com/base/", viewModel.ServerAddressText);
        Assert.Equal("Ready.", viewModel.StatusMessage);
        Assert.False(viewModel.RestartRequired);
    }

    [Fact]
    public async Task TestConnectionCommand_reports_success_without_saving()
    {
        var savedAddresses = new List<string>();
        var viewModel = CreateViewModel(_ => OnlineResponse(), savedAddresses: savedAddresses);
        viewModel.ServerAddressText = "https://api.example.com";

        await viewModel.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal("Connection succeeded.", viewModel.StatusMessage);
        Assert.False(viewModel.IsBusy);
        Assert.Empty(savedAddresses);
    }

    [Fact]
    public async Task SaveCommand_tests_before_saving_and_marks_restart_for_changed_address()
    {
        var events = new List<string>();
        var savedAddresses = new List<string>();
        var viewModel = CreateViewModel(
            _ =>
            {
                events.Add("test");
                return OnlineResponse();
            },
            currentAddress: "https://current.example.com/",
            savedAddresses: savedAddresses,
            onSave: _ => events.Add("save"));
        viewModel.ServerAddressText = "https://new.example.com";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["test", "save"], events);
        Assert.Equal("https://new.example.com/", Assert.Single(savedAddresses));
        Assert.True(viewModel.RestartRequired);
        Assert.Equal("Server address saved. Restart HBPOS to use the new address.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveCommand_does_not_write_when_health_check_fails()
    {
        var savedAddresses = new List<string>();
        var viewModel = CreateViewModel(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            savedAddresses: savedAddresses);
        viewModel.ServerAddressText = "https://offline.example.com";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Empty(savedAddresses);
        Assert.False(viewModel.RestartRequired);
        Assert.Equal("Connection failed. Check the address and try again.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveCommand_does_not_require_restart_when_normalized_address_is_unchanged()
    {
        var savedAddresses = new List<string>();
        var viewModel = CreateViewModel(
            _ => OnlineResponse(),
            currentAddress: "https://api.example.com/",
            savedAddresses: savedAddresses);
        viewModel.ServerAddressText = " HTTPS://API.EXAMPLE.COM ";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal("https://api.example.com/", Assert.Single(savedAddresses));
        Assert.False(viewModel.RestartRequired);
        Assert.Equal("Server address saved.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveCommand_requires_restart_when_path_differs_only_by_case()
    {
        var savedAddresses = new List<string>();
        var viewModel = CreateViewModel(
            _ => OnlineResponse(),
            currentAddress: "https://api.example.com/pos-api/",
            savedAddresses: savedAddresses);
        viewModel.ServerAddressText = "https://API.EXAMPLE.COM/POS-API/";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal("https://api.example.com/POS-API/", Assert.Single(savedAddresses));
        Assert.True(viewModel.RestartRequired);
        Assert.Equal("Server address saved. Restart HBPOS to use the new address.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Load_preserves_pending_address_and_restart_requirement_after_save()
    {
        var viewModel = CreateViewModel(
            _ => OnlineResponse(),
            currentAddress: "https://current.example.com/");
        viewModel.ServerAddressText = "https://new.example.com";
        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.Load();

        Assert.Equal("https://new.example.com/", viewModel.ServerAddressText);
        Assert.True(viewModel.RestartRequired);
        Assert.Equal("Server address saved. Restart HBPOS to use the new address.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveCommand_can_replace_legacy_process_address_rejected_for_new_saves()
    {
        var savedAddresses = new List<string>();
        var viewModel = CreateViewModel(
            _ => OnlineResponse(),
            currentAddress: "http://10.0.0.5:5159/",
            savedAddresses: savedAddresses);
        viewModel.ServerAddressText = "https://new.example.com";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal("https://new.example.com/", Assert.Single(savedAddresses));
        Assert.True(viewModel.RestartRequired);
        Assert.Equal("Server address saved. Restart HBPOS to use the new address.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveCommand_reports_save_failure_without_faulting_or_marking_restart()
    {
        var savedAddresses = new List<string>();
        var viewModel = CreateViewModel(
            _ => OnlineResponse(),
            currentAddress: "https://current.example.com/",
            savedAddresses: savedAddresses,
            onSave: _ => throw new UnauthorizedAccessException("用户环境变量不可写。"));
        viewModel.ServerAddressText = "https://new.example.com";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Empty(savedAddresses);
        Assert.False(viewModel.RestartRequired);
        Assert.Equal("Could not save the server address. Check Windows permissions and try again.", viewModel.StatusMessage);
    }

    private static ApiServerSettingsViewModel CreateViewModel(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string currentAddress = "http://localhost:5159/",
        List<string>? savedAddresses = null,
        Action<string>? onSave = null)
    {
        var handler = new StubHttpMessageHandler(responder);
        var service = new ApiServerSettingsService(
            new HttpClient(handler),
            () => currentAddress,
            address =>
            {
                onSave?.Invoke(address);
                savedAddresses?.Add(address);
            });
        return new ApiServerSettingsViewModel(service, new LocalizationService());
    }

    private static ApiServerSettingsViewModel CreateSwitchViewModel(
        Func<string> currentAddress,
        IApiServerSwitchCoordinator coordinator,
        IOperationAuditLogger logger,
        ICashierSessionContext cashierSessionContext)
    {
        var service = new ApiServerSettingsService(
            new HttpClient(new StubHttpMessageHandler(_ => OnlineResponse())),
            currentAddress,
            _ => { });
        return new ApiServerSettingsViewModel(
            service,
            new LocalizationService(),
            coordinator,
            logger,
            cashierSessionContext);
    }

    private static CashierSessionDto CreateCashier() =>
        new("C001", "user-guid-001", "Alice", "S001", "POS-01", [], [], ["S001"], false, false, false);

    private sealed class FakeSwitchCoordinator(Func<string, ApiServerSwitchResult> switchHandler) : IApiServerSwitchCoordinator
    {
        public Task<ApiServerSwitchResult> SwitchAsync(string targetAddress, CancellationToken cancellationToken = default) =>
            Task.FromResult(switchHandler(targetAddress));
    }

    private sealed class RecordingOperationAuditLogger : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent)
        {
            Events.Add(auditEvent);
        }
    }

    private static HttpResponseMessage OnlineResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(ApiResult<HealthCheckResponse>.Ok(
                new HealthCheckResponse(true, DateTimeOffset.UnixEpoch, "ok")))
        };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
