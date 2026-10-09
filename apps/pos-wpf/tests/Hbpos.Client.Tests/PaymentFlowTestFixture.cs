using System.Net.Http;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;
using PCEFTPOS.EFTClient.IPInterface;

namespace Hbpos.Client.Tests;

/// <summary>
/// 跨层付款回归的类型化测试边界：付款页、工作流、配置路由、Cloud 客户端和
/// attempt/order/sync SQLite 仓储使用真实实现；只有外部 Cloud API、secret store
/// 以及未参与本场景的本地 EFT/backend 适配器使用替身。
/// </summary>
internal sealed class PaymentFlowTestFixture : IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly HttpClient _httpClient;

    private PaymentFlowTestFixture(
        string databasePath,
        HttpClient httpClient,
        ILinklyPaymentAttemptContextAccessor attemptContext,
        ScriptedLinklyCloudApi cloudApi,
        CardTerminalSettings settings,
        LocalSqliteStore store,
        LocalCardPaymentAttemptRepository attemptRepository,
        LocalOrderRepository orderRepository,
        SyncQueueRepository syncQueueRepository,
        LinklyCloudTerminalClient cloudClient,
        ConfiguredLinklyTerminalClient configuredLinkly,
        ConfiguredCardTerminalClient configuredCard,
        StaticCardTerminalSettingsProvider settingsProvider)
    {
        _databasePath = databasePath;
        _httpClient = httpClient;
        CloudApi = cloudApi;
        Settings = settings;
        Store = store;
        AttemptRepository = attemptRepository;
        OrderRepository = orderRepository;
        SyncQueueRepository = syncQueueRepository;
        CloudClient = cloudClient;
        ConfiguredLinkly = configuredLinkly;
        ConfiguredCard = configuredCard;
        SettingsProvider = settingsProvider;
        Workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orderRepository,
            syncQueueRepository,
            cardTerminalClient: configuredCard,
            cardPaymentAttemptRepository: attemptRepository,
            cardTerminalSettingsProvider: settingsProvider,
            linklyPaymentAttemptContextAccessor: attemptContext);
        Session = new PosSessionState(
            "HB POS",
            "S001",
            "Main Store",
            "POS-01",
            "C001",
            "Alice",
            true,
            0);
    }

    public ScriptedLinklyCloudApi CloudApi { get; }

    public CardTerminalSettings Settings { get; }

    public StaticCardTerminalSettingsProvider SettingsProvider { get; }

    public LocalSqliteStore Store { get; }

    public LocalCardPaymentAttemptRepository AttemptRepository { get; }

    public LocalOrderRepository OrderRepository { get; }

    public SyncQueueRepository SyncQueueRepository { get; }

    public LinklyCloudTerminalClient CloudClient { get; }

    public ConfiguredLinklyTerminalClient ConfiguredLinkly { get; }

    public ConfiguredCardTerminalClient ConfiguredCard { get; }

    public CashPaymentWorkflowService Workflow { get; }

    public PosSessionState Session { get; }

    public static async Task<PaymentFlowTestFixture> CreateAsync(ScriptedLinklyCloudApi cloudApi)
    {
        ArgumentNullException.ThrowIfNull(cloudApi);

        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-payment-flow-{Guid.NewGuid():N}.db");
        var store = new LocalSqliteStore(databasePath);
        HttpClient? httpClient = null;
        try
        {
            await new LocalSchemaService(store).InitializeAsync();

            var settings = CreateSettings();
            var settingsProvider = new StaticCardTerminalSettingsProvider(settings);
            var attemptContext = new LinklyPaymentAttemptContextAccessor();
            var secretStore = new PaymentFlowSecretStore();
            var cloudClient = new LinklyCloudTerminalClient(
                cloudApi,
                secretStore,
                pollInterval: TimeSpan.Zero,
                linklyPaymentAttemptContextAccessor: attemptContext);
            var configuredLinkly = new ConfiguredLinklyTerminalClient(
                new LinklyTerminalClient(new NoopLinklyEftClientFactory()),
                cloudClient,
                new NoopLinklyBackendTerminalClient());
            httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://payment-flow-tests.invalid/")
            };
            var configuredCard = new ConfiguredCardTerminalClient(
                settingsProvider,
                httpClient,
                configuredLinkly,
                linklyPaymentAttemptContextAccessor: attemptContext);
            var attemptRepository = new LocalCardPaymentAttemptRepository(store);
            var orderRepository = new LocalOrderRepository(store);
            var syncQueueRepository = new SyncQueueRepository(store);

            return new PaymentFlowTestFixture(
                databasePath,
                httpClient,
                attemptContext,
                cloudApi,
                settings,
                store,
                attemptRepository,
                orderRepository,
                syncQueueRepository,
                cloudClient,
                configuredLinkly,
                configuredCard,
                settingsProvider);
        }
        catch
        {
            httpClient?.Dispose();
            DeleteDatabase(databasePath);
            throw;
        }
    }

    public PosCartService CreateSaleCart(decimal amount = 10m)
    {
        var cart = new PosCartService();
        cart.AddItem(new SellableItemDto(
            StoreCode: Session.StoreCode,
            ProductCode: "PAYMENT-FLOW-ITEM",
            ReferenceCode: null,
            DisplayName: "Payment flow regression item",
            LookupCode: "930PAYMENTFLOW",
            ItemNumber: "PAYMENT-FLOW-ITEM",
            Barcode: "930PAYMENTFLOW",
            RetailPrice: amount,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow));
        return cart;
    }

    public PaymentViewModel CreatePaymentViewModel(
        PosCartService cart,
        Action? openCardRecoveryCenter = null,
        IPaymentMethodSettingsService? paymentMethodSettingsService = null)
    {
        return new PaymentViewModel(
            cart,
            Workflow,
            Session,
            openCardRecoveryCenter: openCardRecoveryCenter,
            paymentMethodSettingsService: paymentMethodSettingsService);
    }

    public CardPaymentRecoveryService CreateRecoveryService()
    {
        return new CardPaymentRecoveryService(
            AttemptRepository,
            SettingsProvider,
            new NoopLinklyBackendTerminalClient(),
            new CashCheckoutService(),
            OrderRepository,
            SyncQueueRepository,
            cloudTerminalClient: CloudClient);
    }

    public async Task<RestartedPaymentRecovery> CreateRestartedRecoveryAsync()
    {
        // 中文注释：恢复测试重新打开同一 SQLite 文件并重新创建 repository/client，
        // 用真实进程重启边界验证 attempt 与冻结身份，而不是复用原对象状态。
        var restartedStore = new LocalSqliteStore(_databasePath);
        await new LocalSchemaService(restartedStore).InitializeAsync();
        var attemptRepository = new LocalCardPaymentAttemptRepository(restartedStore);
        var orderRepository = new LocalOrderRepository(restartedStore);
        var syncQueueRepository = new SyncQueueRepository(restartedStore);
        var settingsProvider = new StaticCardTerminalSettingsProvider(Settings);
        var cloudClient = new LinklyCloudTerminalClient(
            CloudApi,
            new PaymentFlowSecretStore(),
            pollInterval: TimeSpan.Zero);
        var recoveryService = new CardPaymentRecoveryService(
            attemptRepository,
            settingsProvider,
            new NoopLinklyBackendTerminalClient(),
            new CashCheckoutService(),
            orderRepository,
            syncQueueRepository,
            cloudTerminalClient: cloudClient);
        return new RestartedPaymentRecovery(
            restartedStore,
            attemptRepository,
            orderRepository,
            recoveryService);
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        DeleteDatabase(_databasePath);
        return ValueTask.CompletedTask;
    }

    private static CardTerminalSettings CreateSettings()
    {
        var environment = CardTerminalEnvironment.Sandbox;
        return new CardTerminalSettings(
            CardProcessorKind.Linkly,
            environment,
            "127.0.0.1",
            2011,
            null,
            null,
            null,
            CardTerminalSettings.GetSquareApiBaseUrl(environment),
            TimeSpan.FromSeconds(90),
            LinklyConnectionMode.CloudDirectSync,
            LinklyCloudSecret: "payment-flow-test-secret",
            LinklyCloudAuthBaseUrl: CardTerminalSettings.GetLinklyCloudAuthBaseUrl(environment),
            LinklyCloudRestBaseUrl: CardTerminalSettings.GetLinklyCloudRestBaseUrl(environment),
            LinklyPosName: CardTerminalSettings.DefaultLinklyPosName,
            LinklyPosVersion: CardTerminalSettings.DefaultLinklyPosVersion,
            LinklyPosVendorId: CardTerminalSettings.SandboxPlaceholderLinklyPosVendorId);
    }

    private static void DeleteDatabase(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

internal sealed class RestartedPaymentRecovery(
    LocalSqliteStore store,
    LocalCardPaymentAttemptRepository attemptRepository,
    LocalOrderRepository orderRepository,
    CardPaymentRecoveryService service) : IAsyncDisposable
{
    public LocalSqliteStore Store { get; } = store;

    public LocalCardPaymentAttemptRepository AttemptRepository { get; } = attemptRepository;

    public LocalOrderRepository OrderRepository { get; } = orderRepository;

    public CardPaymentRecoveryService Service { get; } = service;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// 以显式 TCS 控制外部交易边界：调用方取消后，场景可选择让原 POST 返回批准，
/// 或让已提交 POST 以取消结束；测试不依赖短墙钟延迟。
/// </summary>
internal sealed class ScriptedLinklyCloudApi : ILinklyCloudApiClient
{
    private readonly bool _returnApprovalAfterRelease;
    private readonly object _gate = new();
    private string? _submittedSessionId;
    private string? _submittedTxnRef;
    private decimal _submittedAmount;

    public ScriptedLinklyCloudApi(bool returnApprovalAfterRelease)
    {
        _returnApprovalAfterRelease = returnApprovalAfterRelease;
    }

    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseApproval { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int SendCount { get; private set; }

    public int GetTransactionCount { get; private set; }

    public string? LastSubmittedSessionId
    {
        get
        {
            lock (_gate)
            {
                return _submittedSessionId;
            }
        }
    }

    public string? LastSubmittedTxnRef
    {
        get
        {
            lock (_gate)
            {
                return _submittedTxnRef;
            }
        }
    }

    public string? LastQueriedSessionId { get; private set; }

    // 为 true 时 GET 原交易返回“已结束且被拒绝”（没有扣款），模拟顾客放弃/终端超时后的权威终态。
    public bool GetTransactionReportsDecline { get; set; }

    public void ReleaseApprovedResult() => ReleaseApproval.TrySetResult();

    public Task<string> PairAsync(
        string authBaseUrl,
        string username,
        string password,
        string pairCode,
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new NotSupportedException());

    public Task<LinklyCloudToken> GetTokenAsync(
        CardTerminalSettings settings,
        string posId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LinklyCloudToken(
            "payment-flow-test-token",
            DateTimeOffset.UtcNow.AddMinutes(5)));

    public Task<LinklyCloudStatusResult> SendStatusAsync(
        CardTerminalSettings settings,
        string token,
        CancellationToken cancellationToken = default) =>
        Task.FromException<LinklyCloudStatusResult>(new NotSupportedException());

    public Task<LinklyCloudLogonResult> SendLogonAsync(
        CardTerminalSettings settings,
        string token,
        CancellationToken cancellationToken = default) =>
        Task.FromException<LinklyCloudLogonResult>(new NotSupportedException());

    public async Task<LinklyCloudTransactionResult> SendTransactionAsync(
        CardTerminalSettings settings,
        string token,
        LinklyCloudTransactionRequest request,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            SendCount++;
            _submittedSessionId = sessionId;
            _submittedTxnRef = request.TxnRef;
            _submittedAmount = request.AmtPurchase / 100m;
        }
        Started.TrySetResult();

        if (!_returnApprovalAfterRelease)
        {
            var cancellationSignal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                cancellationSignal);
            await cancellationSignal.Task;
            throw new OperationCanceledException(cancellationToken);
        }

        await ReleaseApproval.Task;
        return BuildApprovedResult(sessionId, request.TxnRef, _submittedAmount);
    }

    public Task<LinklyCloudTransactionResult> GetTransactionAsync(
        CardTerminalSettings settings,
        string token,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        GetTransactionCount++;
        LastQueriedSessionId = sessionId;
        var txnRef = LastSubmittedTxnRef ?? "P-RECOVERY-REF";
        if (GetTransactionReportsDecline)
        {
            return Task.FromResult(new LinklyCloudTransactionResult(
                sessionId,
                false,
                txnRef,
                null,
                null,
                null,
                null,
                null,
                "05",
                "DECLINED",
                "STAN-FLOW",
                _submittedAmount,
                null));
        }

        return Task.FromResult(BuildApprovedResult(sessionId, txnRef, _submittedAmount));
    }

    public Task SendKeyAsync(
        CardTerminalSettings settings,
        string token,
        string sessionId,
        string key,
        string? data,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    private static LinklyCloudTransactionResult BuildApprovedResult(
        string sessionId,
        string txnRef,
        decimal amount) =>
        new(
            sessionId,
            true,
            txnRef,
            "AUTH-FLOW",
            "VISA",
            "Visa",
            "****1111",
            "CAID-FLOW",
            "00",
            "APPROVED",
            "STAN-FLOW",
            amount,
            null);
}

internal sealed class PaymentFlowSecretStore : ILinklyCloudSecretStore
{
    public Task<string?> GetLinklyCloudSecretAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>("payment-flow-test-secret");

    public Task SaveLinklyCloudSecretAsync(
        CardTerminalEnvironment environment,
        string secret,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<string> GetOrCreateLinklyCloudPosIdAsync(
        CardTerminalEnvironment environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken = default) =>
        Task.FromResult("11111111-1111-4111-8111-111111111111");

    public Task<LinklyCloudCredentialSettings> GetLinklyCloudCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LinklyCloudCredentialSettings(null, null, false));

    public Task SaveLinklyCloudCredentialAsync(
        CardTerminalEnvironment environment,
        string username,
        string password,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class NoopLinklyEftClientFactory : ILinklyEftClientFactory
{
    public ILinklyEftClient Create() => new NoopLinklyEftClient();
}

internal sealed class NoopLinklyEftClient : ILinklyEftClient
{
    public Task<bool> ConnectAsync(
        string hostName,
        int hostPort,
        bool useSsl,
        bool useKeepAlive) =>
        Task.FromResult(false);

    public Task<bool> WriteRequestAsync(EFTRequest request) => Task.FromResult(false);

    public Task<bool> SendCancelRequestAsync() => Task.FromResult(false);

    public Task<EFTResponse?> ReadResponseAsync(CancellationToken cancellationToken) =>
        Task.FromResult<EFTResponse?>(null);

    public bool Disconnect() => true;

    public void Dispose()
    {
    }
}

internal sealed class NoopLinklyBackendTerminalClient : ILinklyBackendTerminalClient
{
    public Task<LinklyConnectionTestResult> TestConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LinklyConnectionTestResult(false, "unused"));

    public Task<LinklyConnectionTestResult> TestTransactionStatusAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LinklyConnectionTestResult(false, "unused"));

    public Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PaymentAuthorizationResult(false, null, "unused"));

    public Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PaymentAuthorizationResult(false, null, "unused"));

    public Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<LinklyCloudBackendSessionResponse?>(null);

    public Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<LinklyCloudBackendSessionResponse>(new NotSupportedException());

    public Task<LinklyCloudBackendSessionResponse> ResumeSessionUntilFinalAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse activeStatus,
        CancellationToken cancellationToken = default) =>
        Task.FromException<LinklyCloudBackendSessionResponse>(new NotSupportedException());

    public Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<LinklyCloudBackendSessionResponse>(new NotSupportedException());

    public Task AcknowledgeSessionAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task AcknowledgeSupervisorResolvedSessionAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default) =>
        AcknowledgeSessionAsync(settings, sessionId, cancellationToken);
}
