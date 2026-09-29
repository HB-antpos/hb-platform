using System.Net;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Client.Tests;

public sealed class OperationAuthorizationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AuthorizeAsync_current_cashier_has_permission_does_not_prompt_or_login()
    {
        var login = new FakeCashierLoginService();
        var logger = new RecordingAuditLogger();
        var cashierContext = CreateCashierContext(CreateCashier("REQUESTER", Permissions.PosTerminal.Sales.ChangePrice));
        var service = new OperationAuthorizationService(login, cashierContext, logger, new StubTimeProvider(Now));
        var state = CreateState(cashierContext.CurrentSession!);

        using var scope = await service.AuthorizeAsync(
            Permissions.PosTerminal.Sales.ChangePrice,
            "PosTerminal",
            "change-price",
            state);

        Assert.NotNull(scope);
        Assert.True(scope.IsActive);
        Assert.False(service.IsPromptOpen);
        Assert.Equal(0, login.CallCount);
        Assert.Empty(logger.Events);

        using var active = scope.Activate();
        OperationAuditEvents.RecordAction(
            logger,
            OperationAuditTypes.CashDrawerOpen,
            "Succeeded",
            state);
        Assert.Null(Assert.Single(logger.Events).Properties);
    }

    [Fact]
    public async Task AuthorizeAsync_scanned_authorizer_creates_scope_and_records_audit_without_replacing_requester()
    {
        var requester = CreateCashier("REQUESTER");
        var authorizer = CreateCashier("SUPERVISOR", Permissions.PosTerminal.Sales.ChangePrice);
        var login = new FakeCashierLoginService(CashierLoginResult.Success(authorizer));
        var logger = new RecordingAuditLogger();
        var service = new OperationAuthorizationService(login, CreateCashierContext(requester), logger, new StubTimeProvider(Now));
        var authorization = service.AuthorizeAsync(
            Permissions.PosTerminal.Sales.ChangePrice,
            "PosTerminal",
            "change-price",
            CreateState(requester));

        Assert.True(service.IsPromptOpen);
        Assert.Equal(Permissions.PosTerminal.Sales.ChangePrice, service.PermissionCode);
        Assert.Equal("PosTerminal", service.Screen);
        Assert.Equal("change-price", service.Action);
        Assert.True(service.ProcessScannerBarcode("supervisor-barcode"));
        using var scope = await authorization;

        Assert.NotNull(scope);
        Assert.True(scope.IsActive);
        Assert.Equal(string.Empty, service.PermissionCode);
        Assert.Equal(string.Empty, service.Screen);
        Assert.Equal(string.Empty, service.Action);
        Assert.Equal("REQUESTER", requester.CashierId);
        var audit = Assert.Single(logger.Events);
        Assert.Equal("PERMISSION_OVERRIDE", audit.OperationType);
        Assert.Equal("REQUESTER", audit.Properties?["requestingCashierId"]);
        Assert.Equal("SUPERVISOR", audit.Properties?["authorizingCashierId"]);
        Assert.Equal("online", audit.Properties?["authorizationMode"]);
        Assert.Equal(Permissions.PosTerminal.Sales.ChangePrice, audit.Properties?["permissionCode"]);
    }

    [Fact]
    public async Task ProcessScannerBarcode_synchronous_success_consumes_duplicate_without_second_login()
    {
        var requester = CreateCashier("REQUESTER");
        var login = new FakeCashierLoginService(CashierLoginResult.Success(
            CreateCashier("SUPERVISOR", Permissions.PosTerminal.Sales.ChangePrice)));
        var service = new OperationAuthorizationService(
            login,
            CreateCashierContext(requester),
            new RecordingAuditLogger(),
            new StubTimeProvider(Now));

        // 生产中授权运行在单线程的 WPF Dispatcher 上：成功后的收尾（清空待授权状态）被投递回 Dispatcher，
        // 同一轮同步处理里的重复扫码一定先于收尾。xunit 的调度上下文是多线程的，空闲线程可能抢先跑完收尾，
        // 使第二次扫码看不到待授权状态而返回 false；这里用由测试放行的上下文还原 Dispatcher 的先后次序。
        var dispatcher = new DeferredSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(dispatcher);
        Task<OperationAuthorizationScope?> authorization;
        try
        {
            authorization = service.AuthorizeAsync(
                Permissions.PosTerminal.Sales.ChangePrice,
                "Pos",
                "change-price",
                CreateState(requester));

            Assert.True(service.ProcessScannerBarcode("supervisor"));
            Assert.True(service.ProcessScannerBarcode("supervisor"));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        await WaitUntilAsync(() =>
        {
            dispatcher.RunPending();
            return authorization.IsCompleted;
        });
        using var scope = await authorization;
        Assert.NotNull(scope);
        Assert.Equal(1, login.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_accepts_valid_offline_cached_authorizer_and_audits_mode()
    {
        var requester = CreateCashier("REQUESTER");
        var authorizer = CreateCashier("SUPERVISOR", Permissions.PosTerminal.Sales.ChangePrice) with
        {
            IsOfflineCached = true
        };
        var logger = new RecordingAuditLogger();
        var service = new OperationAuthorizationService(
            new FakeCashierLoginService(CashierLoginResult.Success(authorizer)),
            CreateCashierContext(requester),
            logger,
            new StubTimeProvider(Now));
        var authorization = service.AuthorizeAsync(
            Permissions.PosTerminal.Sales.ChangePrice,
            "Pos",
            "change-price",
            CreateState(requester));

        service.ProcessScannerBarcode("supervisor");

        using var scope = await authorization;
        Assert.NotNull(scope);
        Assert.Equal("offline-cache", Assert.Single(logger.Events).Properties?["authorizationMode"]);
    }

    [Theory]
    [InlineData("expired", "AUTHORIZATION_TICKET_INVALID")]
    [InlineData("store", "STORE_OR_DEVICE_MISMATCH")]
    [InlineData("device", "STORE_OR_DEVICE_MISMATCH")]
    [InlineData("permission", "PERMISSION_DENIED")]
    public async Task AuthorizeAsync_rejects_invalid_authorizer_and_keeps_pending(
        string invalidCase,
        string expectedReason)
    {
        var requester = CreateCashier("REQUESTER");
        var authorizer = CreateCashier("SUPERVISOR", Permissions.PosTerminal.Sales.ChangePrice) with
        {
            StoreCode = invalidCase == "store" ? "OTHER" : "STORE-1",
            DeviceCode = invalidCase == "device" ? "OTHER" : "POS-1",
            PermissionCodes = invalidCase == "permission" ? [] : [Permissions.PosTerminal.Sales.ChangePrice],
            AuthorizationExpiresAtUtc = invalidCase == "expired" ? Now.AddMinutes(-1) : Now.AddYears(1)
        };
        var logger = new RecordingAuditLogger();
        var service = new OperationAuthorizationService(
            new FakeCashierLoginService(CashierLoginResult.Success(authorizer)),
            CreateCashierContext(requester),
            logger,
            new StubTimeProvider(Now));
        var authorization = service.AuthorizeAsync(
            Permissions.PosTerminal.Sales.ChangePrice,
            "Pos",
            "change-price",
            CreateState(requester));

        service.ProcessScannerBarcode("supervisor");
        await WaitUntilAsync(() => !service.IsBusy);

        Assert.False(authorization.IsCompleted);
        Assert.Equal(expectedReason, Assert.Single(logger.Events).ReasonCode);
        service.Cancel();
        Assert.Null(await authorization);
    }

    [Fact]
    public async Task AuthorizeAsync_rejects_emergency_or_expired_authorizer_and_keeps_pending_until_cancelled()
    {
        var emergency = CreateCashier("EMERGENCY", Permissions.PosTerminal.Sales.ChangePrice) with
        {
            IsEmergencyOverride = true
        };
        var login = new FakeCashierLoginService(CashierLoginResult.Success(emergency));
        var logger = new RecordingAuditLogger();
        var service = new OperationAuthorizationService(
            login,
            CreateCashierContext(CreateCashier("REQUESTER")),
            logger,
            new StubTimeProvider(Now));
        var authorization = service.AuthorizeAsync(
            Permissions.PosTerminal.Sales.ChangePrice,
            "PosTerminal",
            "change-price",
            CreateState(CreateCashier("REQUESTER")));

        service.ProcessScannerBarcode("emergency-token");
        await WaitUntilAsync(() => !service.IsBusy);

        Assert.False(authorization.IsCompleted);
        Assert.Contains("Emergency authorization", service.StatusMessage, StringComparison.Ordinal);
        var denied = Assert.Single(logger.Events);
        Assert.Equal("Denied", denied.Outcome);
        Assert.Equal("EMERGENCY_OVERRIDE_DENIED", denied.ReasonCode);
        service.Cancel();
        Assert.Null(await authorization);
        Assert.Contains(logger.Events, audit => audit.ReasonCode == "CANCELLED");
    }

    [Fact]
    public async Task AuthorizeAsync_allows_only_one_pending_request()
    {
        var state = CreateState(CreateCashier("REQUESTER"));
        var service = new OperationAuthorizationService(
            new FakeCashierLoginService(),
            CreateCashierContext(state.CashierSession!),
            new RecordingAuditLogger(),
            new StubTimeProvider(Now));
        var first = service.AuthorizeAsync("Permissions.PosTerminal.Sales.ChangePrice", "Pos", "first", state);

        var second = await service.AuthorizeAsync(
            "Permissions.PosTerminal.Sales.RemoveLine",
            "Pos",
            "second",
            state);

        Assert.Null(second);
        Assert.Contains("Another operation", service.StatusMessage, StringComparison.Ordinal);
        service.Cancel();
        Assert.Null(await first);
    }

    [Fact]
    public async Task AuthorizeAsync_accepts_legacy_discount_permission_from_authorizer()
    {
        var requester = CreateCashier("REQUESTER");
        var authorizer = CreateCashier("SUPERVISOR", Permissions.PosTerminal.Sales.LineDiscount);
        var service = new OperationAuthorizationService(
            new FakeCashierLoginService(CashierLoginResult.Success(authorizer)),
            CreateCashierContext(requester),
            new RecordingAuditLogger(),
            new StubTimeProvider(Now));
        var authorization = service.AuthorizeAsync(
            Permissions.PosTerminal.Sales.LineManualDiscount,
            "Pos",
            "line-discount",
            CreateState(requester));

        service.ProcessScannerBarcode("supervisor");

        using var scope = await authorization;
        Assert.NotNull(scope);
    }

    [Fact]
    public async Task Cancel_during_scanner_validation_prevents_scope_and_audit()
    {
        var requester = CreateCashier("REQUESTER");
        var authorizer = CreateCashier("SUPERVISOR", Permissions.PosTerminal.Sales.ChangePrice);
        var loginResult = new TaskCompletionSource<CashierLoginResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var login = new BlockingCashierLoginService(loginResult.Task);
        var logger = new RecordingAuditLogger();
        var service = new OperationAuthorizationService(
            login,
            CreateCashierContext(requester),
            logger,
            new StubTimeProvider(Now));
        var authorization = service.AuthorizeAsync(
            Permissions.PosTerminal.Sales.ChangePrice,
            "Pos",
            "change-price",
            CreateState(requester));
        service.ProcessScannerBarcode("supervisor");
        await WaitUntilAsync(() => service.IsBusy);

        service.Cancel();
        loginResult.SetResult(CashierLoginResult.Success(authorizer));

        Assert.Null(await authorization);
        await WaitUntilAsync(() => !service.IsBusy);
        var cancelled = Assert.Single(logger.Events);
        Assert.Equal("Denied", cancelled.Outcome);
        Assert.Equal("CANCELLED", cancelled.ReasonCode);
    }

    [Fact]
    public async Task Active_scope_uses_authorizer_ticket_and_dispose_invalidates_derived_context()
    {
        var requester = CreateCashier("REQUESTER") with { AuthorizationToken = "requester-ticket" };
        var authorizer = CreateCashier("SUPERVISOR", Permissions.PosTerminal.Sales.ChangePrice) with
        {
            AuthorizationToken = "authorizer-ticket"
        };
        var login = new FakeCashierLoginService(CashierLoginResult.Success(authorizer));
        var logger = new RecordingAuditLogger();
        var service = new OperationAuthorizationService(
            login,
            CreateCashierContext(requester),
            logger,
            new StubTimeProvider(Now));
        var authorization = service.AuthorizeAsync(
            Permissions.PosTerminal.Sales.ChangePrice,
            "Pos",
            "change-price",
            CreateState(requester));
        var unrelated = SendAndCaptureCashierTicketAsync(CreateCashierContext(requester));
        service.ProcessScannerBarcode("supervisor");
        var scope = await authorization;
        Assert.NotNull(scope);

        var cashierContext = CreateCashierContext(requester);
        Assert.Equal("requester-ticket", await unrelated);
        using var active = scope.Activate();
        Assert.Equal("authorizer-ticket", await SendAndCaptureCashierTicketAsync(cashierContext));
        OperationAuditEvents.RecordAction(
            logger,
            OperationAuditTypes.CashDrawerOpen,
            "Succeeded",
            CreateState(requester));
        var businessAudit = Assert.Single(logger.Events.Where(audit =>
            audit.OperationType == OperationAuditTypes.CashDrawerOpen));
        Assert.Equal("REQUESTER", businessAudit.CashierId);
        Assert.Equal("SUPERVISOR", businessAudit.Properties?["authorizingCashierId"]);
        Assert.Equal(Permissions.PosTerminal.Sales.ChangePrice, businessAudit.Properties?["permissionCode"]);

        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = Task.Run(async () =>
        {
            await releaseChild.Task;
            return await SendAndCaptureCashierTicketAsync(cashierContext);
        });
        scope.Dispose();
        releaseChild.SetResult();

        Assert.False(scope.IsActive);
        Assert.Equal("requester-ticket", await child);
    }

    [Fact]
    public async Task RevokeAll_invalidates_an_already_issued_scope()
    {
        var requester = CreateCashier("REQUESTER") with { AuthorizationToken = "requester-ticket" };
        var authorizer = CreateCashier("SUPERVISOR", Permissions.PosTerminal.Sales.ChangePrice) with
        {
            AuthorizationToken = "authorizer-ticket"
        };
        var service = new OperationAuthorizationService(
            new FakeCashierLoginService(CashierLoginResult.Success(authorizer)),
            CreateCashierContext(requester),
            new RecordingAuditLogger(),
            new StubTimeProvider(Now));
        var authorization = service.AuthorizeAsync(
            Permissions.PosTerminal.Sales.ChangePrice,
            "Pos",
            "change-price",
            CreateState(requester));
        service.ProcessScannerBarcode("supervisor");
        var scope = await authorization;
        Assert.NotNull(scope);

        service.RevokeAll();

        Assert.False(scope.IsActive);
        Assert.Throws<ObjectDisposedException>(() => scope.Activate());
    }

    [Fact]
    public async Task RevokeAll_racing_with_authorize_prevents_old_session_scope_creation()
    {
        var requester = CreateCashier("REQUESTER", Permissions.PosTerminal.Sales.ChangePrice);
        var cashierContext = new BlockingCashierSessionContext(requester);
        var service = new OperationAuthorizationService(
            new FakeCashierLoginService(),
            cashierContext,
            new RecordingAuditLogger(),
            new StubTimeProvider(Now));
        var authorization = Task.Run(() => service.AuthorizeAsync(
            Permissions.PosTerminal.Sales.ChangePrice,
            "Pos",
            "change-price",
            CreateState(requester)));
        await cashierContext.HasPermissionEntered.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        service.RevokeAll();
        cashierContext.ReleaseHasPermission.TrySetResult();

        Assert.Null(await authorization);
    }

    [Fact]
    public async Task Suspend_temporarily_restores_requester_ticket_for_background_work()
    {
        var requester = CreateCashier("REQUESTER") with { AuthorizationToken = "requester-ticket" };
        var authorizer = CreateCashier("SUPERVISOR", Permissions.PosTerminal.Sales.ChangePrice) with
        {
            AuthorizationToken = "authorizer-ticket"
        };
        var service = new OperationAuthorizationService(
            new FakeCashierLoginService(CashierLoginResult.Success(authorizer)),
            CreateCashierContext(requester),
            new RecordingAuditLogger(),
            new StubTimeProvider(Now));
        var authorization = service.AuthorizeAsync(
            Permissions.PosTerminal.Sales.ChangePrice,
            "Pos",
            "change-price",
            CreateState(requester));
        service.ProcessScannerBarcode("supervisor");
        using var scope = await authorization;
        Assert.NotNull(scope);
        var cashierContext = CreateCashierContext(requester);
        using var active = scope.Activate();

        Assert.Equal("authorizer-ticket", await SendAndCaptureCashierTicketAsync(cashierContext));
        using (OperationAuthorizationScope.Suspend())
        {
            Assert.Equal("requester-ticket", await SendAndCaptureCashierTicketAsync(cashierContext));
        }
        Assert.Equal("authorizer-ticket", await SendAndCaptureCashierTicketAsync(cashierContext));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_then_late_login_failure_or_exception_records_only_cancelled(bool throws)
    {
        var requester = CreateCashier("REQUESTER");
        var completion = new TaskCompletionSource<CashierLoginResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new RecordingAuditLogger();
        var service = new OperationAuthorizationService(
            new BlockingCashierLoginService(completion.Task),
            CreateCashierContext(requester),
            logger,
            new StubTimeProvider(Now));
        var authorization = service.AuthorizeAsync(
            Permissions.PosTerminal.Sales.ChangePrice,
            "Pos",
            "change-price",
            CreateState(requester));
        service.ProcessScannerBarcode("supervisor");
        await WaitUntilAsync(() => service.IsBusy);

        service.Cancel();
        if (throws)
        {
            completion.SetException(new HttpRequestException("offline"));
        }
        else
        {
            completion.SetResult(CashierLoginResult.Fail(
                "unavailable",
                "CASHIER_LOGIN_API_UNAVAILABLE"));
        }

        Assert.Null(await authorization);
        await Task.Delay(50);
        var audit = Assert.Single(logger.Events);
        Assert.Equal("CANCELLED", audit.ReasonCode);
    }

    [Fact]
    public void Service_registration_registers_operation_authorization_as_singleton()
    {
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], true, "PosTerminal", "en-AU"));

        var descriptor = Assert.Single(services, item =>
            item.ServiceType == typeof(IOperationAuthorizationService));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(OperationAuthorizationService), descriptor.ImplementationType);
    }

    [Fact]
    public void Service_registration_passes_operation_authorization_to_shell_and_child_factory()
    {
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], true, "PosTerminal", "en-AU"));
        using var provider = services.BuildServiceProvider();

        var authorization = provider.GetRequiredService<IOperationAuthorizationService>();
        var mainViewModel = provider.GetRequiredService<MainViewModel>();
        Assert.Same(authorization, mainViewModel.OperationAuthorization);

        var factoryField = typeof(MainViewModel).GetField(
            "_mainChildViewModelFactory",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var factory = Assert.IsType<MainChildViewModelFactory>(factoryField?.GetValue(mainViewModel));
        var authorizationField = typeof(MainChildViewModelFactory).GetField(
            "_operationAuthorizationService",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.Same(authorization, authorizationField?.GetValue(factory));
    }

    private static async Task<string?> SendAndCaptureCashierTicketAsync(ICashierSessionContext cashierContext)
    {
        string? ticket = null;
        var deviceState = new DeviceAuthorizationState();
        deviceState.Set(new DeviceAuthorizationContext("POS-1", "STORE-1", "HW-1", "device-ticket"));
        var handler = new DeviceAuthorizationMessageHandler(deviceState, cashierContext)
        {
            InnerHandler = new CaptureHandler(request =>
            {
                ticket = request.Headers.TryGetValues(CashierAuthorizationConstants.HeaderName, out var values)
                    ? values.Single()
                    : null;
            })
        };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("http://localhost/test");
        return ticket;
    }

    private static PosSessionState CreateState(CashierSessionDto cashier) =>
        new("HBPOS", "STORE-1", "Store", "POS-1", cashier.CashierId, cashier.CashierName, true, 0, cashier);

    private static CashierSessionContext CreateCashierContext(CashierSessionDto cashier)
    {
        var context = new CashierSessionContext(new StubTimeProvider(Now));
        context.SetCurrent(cashier);
        return context;
    }

    private static CashierSessionDto CreateCashier(string cashierId, params string[] permissions) =>
        new(
            cashierId,
            $"USER-{cashierId}",
            cashierId,
            "STORE-1",
            "POS-1",
            [],
            permissions,
            ["STORE-1"],
            IsSuperAdmin: false,
            IsOfflineCached: false,
            IsEmergencyOverride: false,
            AuthorizationToken: $"ticket-{cashierId}",
            AuthorizationExpiresAtUtc: Now.AddYears(1));

    private sealed class FakeCashierLoginService(params CashierLoginResult[] results) : ICashierLoginService
    {
        private readonly Queue<CashierLoginResult> _results = new(results);

        public int CallCount { get; private set; }

        public Task<CashierLoginResult> LoginAsync(
            string storeCode,
            string deviceCode,
            string userBarcode,
            bool attemptOnline = true,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_results.Count == 0
                ? CashierLoginResult.Fail("not configured")
                : _results.Dequeue());
        }
    }

    /// <summary>
    /// 模拟单线程 Dispatcher：投递的回调先排队，只有测试调用 <see cref="RunPending"/> 时才在当前线程执行。
    /// </summary>
    private sealed class DeferredSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void RunPending()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_queue.TryDequeue(out var item))
                {
                    item.Callback(item.State);
                }
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    private sealed class RecordingAuditLogger : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent) => Events.Add(auditEvent);
    }

    private sealed class BlockingCashierLoginService(Task<CashierLoginResult> result) : ICashierLoginService
    {
        public Task<CashierLoginResult> LoginAsync(
            string storeCode,
            string deviceCode,
            string userBarcode,
            bool attemptOnline = true,
            CancellationToken cancellationToken = default) => result;
    }

    private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class BlockingCashierSessionContext(CashierSessionDto session) : ICashierSessionContext
    {
        public TaskCompletionSource HasPermissionEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseHasPermission { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CashierSessionDto? CurrentSession { get; private set; } = session;

        public void SetCurrent(CashierSessionDto value) => CurrentSession = value;

        public void Clear() => CurrentSession = null;

        public bool TrySetCurrent(CashierSessionDto expected, CashierSessionDto replacement)
        {
            if (!ReferenceEquals(CurrentSession, expected)) return false;
            CurrentSession = replacement;
            return true;
        }

        public bool TryClear(CashierSessionDto expected)
        {
            if (!ReferenceEquals(CurrentSession, expected)) return false;
            CurrentSession = null;
            return true;
        }

        public bool HasPermission(string permissionCode)
        {
            HasPermissionEntered.TrySetResult();
            ReleaseHasPermission.Task.GetAwaiter().GetResult();
            return true;
        }

        public bool RequirePermission(string permissionCode, out string message)
        {
            message = string.Empty;
            return HasPermission(permissionCode);
        }
    }

    private sealed class CaptureHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
