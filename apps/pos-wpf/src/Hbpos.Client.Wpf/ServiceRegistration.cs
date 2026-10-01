using System.IO;
using System.Net;
using System.Net.Http;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.Facades;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.RemoteMaintenance.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hbpos.Client.Wpf;

internal sealed class ApiBaseAddressConfigurationException : Exception
{
    public ApiBaseAddressConfigurationException(Exception innerException)
        : base("HBPOS_API_BASE_URL must be an absolute, permitted HTTP or HTTPS server address.", innerException)
    {
    }
}

public static class ServiceRegistration
{
    private const string ApiBaseUrlEnvironmentVariable = "HBPOS_API_BASE_URL";
    internal const string PreviewApiBaseAddress = "http://127.0.0.1:0/";
    private const string ApplicationLogUploadClientName = "HbposApplicationLogUpload";
    private const string OperationAuditUploadClientName = "HbposOperationAuditUpload";
#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    public static IServiceCollection AddHbposClientServices(
        this IServiceCollection services,
        AppStartupOptions startupOptions,
        StartupProgressTracker? startupProgress = null,
        IStartupScreen? startupScreen = null)
    {
        services.AddSingleton(startupOptions);
        // 启动页要最先出现，追踪器和启动页在服务容器建立前就已创建；这里登记同一个实例，
        // 供主窗口上报启动阶段、启动更新提示弹框前让启动页让位。Preview 不显示启动页，两者都为空。
        if (startupProgress is not null)
        {
            services.AddSingleton(startupProgress);
        }

        if (startupScreen is not null)
        {
            services.AddSingleton(startupScreen);
        }

        services.AddSingleton<ILocalizationService, LocalizationService>();
        var initialApiAddress = GetInitialApiBaseAddress(startupOptions);
        services.AddSingleton(new ApiRuntimeEndpointState(initialApiAddress.AbsoluteUri));
        services.AddTransient<ApiRuntimeEndpointHandler>();
        services.AddHttpClient<ApiServerSettingsService>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<ApiServerSettingsViewModel>();
        var localDataDirectory = startupOptions.PreviewMode
            ? Path.Combine(Path.GetTempPath(), $"hbpos-client-preview-{Environment.ProcessId}")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hbpos.Client");
        // 正式与调试 API 共用同一业务数据库，本地数据也固定落在同一个文件中。
        services.AddSingleton(new LocalSqliteStore(Path.Combine(localDataDirectory, "hbpos_client.db")));
        services.AddSingleton<ILocalSqliteCheckpointService>(sp => sp.GetRequiredService<LocalSqliteStore>());
        services.AddSingleton<LocalSchemaService>();
        services.AddSingleton<ILocalSchemaService>(sp => sp.GetRequiredService<LocalSchemaService>());
        services.AddSingleton<IAppUpdateDeviceCacheInitializer, AppUpdateDeviceCacheInitializer>();
        services.AddSingleton<IDeviceAuthorizationProtector, WindowsDpapiDeviceAuthorizationProtector>();
        services.AddSingleton<IDeviceActivationRecoveryStore>(sp =>
            new LocalDeviceActivationRecoveryStore(
                sp.GetRequiredService<IDeviceAuthorizationProtector>(),
                sp.GetRequiredService<ApiRuntimeEndpointState>(),
                sp.GetRequiredService<IDeviceFingerprintService>(),
                Path.Combine(localDataDirectory, "device-activation-recovery.pending")));
        services.AddSingleton<DeviceAuthorizationState>();
        services.AddTransient<DeviceAuthorizationMessageHandler>();
        services.AddSingleton<ILocalAppSettingsRepository, LocalAppSettingsRepository>();
        services.AddSingleton<IPaymentMethodSettingsService, PaymentMethodSettingsService>();
        services.AddSingleton<IRemoteMaintenanceSecretProtector, DpapiRemoteMaintenanceSecretProtector>();
        services.AddSingleton(sp => new RemoteMaintenanceJournal(
            Path.Combine(localDataDirectory, "remote-maintenance.journal"),
            sp.GetRequiredService<IRemoteMaintenanceSecretProtector>()));
        services.AddSingleton<IRemoteMaintenanceCommandRunner, WindowsRemoteMaintenanceCommandRunner>();
        services.AddSingleton<IRemoteMaintenanceServiceControl, WindowsRemoteMaintenanceServiceControl>();
        services.AddSingleton<IRemoteMaintenanceUacHelperLauncher, WindowsRemoteMaintenanceUacHelperLauncher>();
        services.AddSingleton<IRemoteMaintenanceInstaller, WindowsRemoteMaintenanceInstaller>();
        services.AddSingleton<IRemoteMaintenanceArtifactDownloader, RemoteMaintenanceArtifactDownloader>();
        services.AddSingleton<IRemoteMaintenanceService, RemoteMaintenanceService>();
        services.AddSingleton<IAppShutdownCoordinator, AppShutdownCoordinator>();
        services.AddSingleton<ICashierSessionContext, CashierSessionContext>();
        services.AddSingleton<IEmergencyLoginPublicKeyCache, EmergencyLoginPublicKeyCache>();
        services.AddHttpClient<IEmergencyLoginPublicKeyApiClient, EmergencyLoginPublicKeyApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(5);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddSingleton<IEmergencyLoginPublicKeySyncService, EmergencyLoginPublicKeySyncService>();
        services.AddSingleton<EmergencyLoginPublicKeySyncHostedService>();
        services.AddSingleton<IHostedService>(sp =>
            sp.GetRequiredService<EmergencyLoginPublicKeySyncHostedService>());
        services.AddSingleton<IEmergencyLoginTokenService, EmergencyLoginTokenService>();
        services.AddSingleton(_ => new ClientLogOutboxStore(GetLogDatabasePath(startupOptions)));
        services.AddSingleton(_ => ClientLogIdentity.CreateCurrent());
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetService<IConfiguration>() ?? new ConfigurationBuilder().Build();
            var options = ApplicationLogOptions.FromConfiguration(configuration, initialApiAddress);
            if (!startupOptions.PreviewMode)
            {
                return options;
            }

            // Preview 不得把测试日志上传到真实中心；清空端点并关闭上传，保留本地日志用于审计。
            return options with
            {
                Enabled = false,
                IngestUri = null
            };
        });
        services.AddSingleton(sp => OperationAuditUploadOptions.FromConfiguration(
            sp.GetService<IConfiguration>() ?? new ConfigurationBuilder().Build()));
        services.AddSingleton(sp => new ClientLogOutboxWriter(
            sp.GetRequiredService<ClientLogOutboxStore>(),
            sp.GetRequiredService<DeviceAuthorizationState>(),
            sp.GetRequiredService<ICashierSessionContext>(),
            sp.GetRequiredService<ClientLogIdentity>(),
            sp.GetRequiredService<ApplicationLogOptions>().QueueCapacity));
        services.AddSingleton<IApplicationLogSink>(sp => sp.GetRequiredService<ClientLogOutboxWriter>());
        services.AddSingleton<IOperationAuditLogger>(sp => sp.GetRequiredService<ClientLogOutboxWriter>());
        services.AddHttpClient(ApplicationLogUploadClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddHttpClient(OperationAuditUploadClientName, client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddSingleton(sp => new ApplicationLogUploadService(
            sp.GetRequiredService<ClientLogOutboxStore>(),
            sp.GetRequiredService<ApplicationLogOptions>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ApplicationLogUploadClientName),
            TimeProvider.System));
        services.AddSingleton(sp => new OperationAuditUploadService(
            sp.GetRequiredService<ClientLogOutboxStore>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(OperationAuditUploadClientName),
            TimeProvider.System,
            sp.GetRequiredService<OperationAuditUploadOptions>(),
            sp.GetRequiredService<DeviceAuthorizationState>()));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ApplicationLogUploadService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<OperationAuditUploadService>());
        // Generic Host 按注册逆序停止：writer 最后注册，退出时先落库，再由 uploader 做最终上传。
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ClientLogOutboxWriter>());
        services.AddSingleton(sp => new CashierLoginService(
            sp.GetRequiredService<ICashierLoginApiClient>(),
            sp.GetRequiredService<ILocalAppSettingsRepository>(),
            sp.GetRequiredService<IDeviceAuthorizationProtector>(),
            sp.GetRequiredService<IEmergencyLoginTokenService>()));
        services.AddSingleton<ICashierLoginService>(sp => sp.GetRequiredService<CashierLoginService>());
        services.AddSingleton<ICashierSessionCacheUpdater>(sp => sp.GetRequiredService<CashierLoginService>());
        services.AddSingleton<IOperationAuthorizationService, OperationAuthorizationService>();
        services.AddSingleton<ApiServerSwitchRuntime>();
        services.AddSingleton<IApiServerSwitchRuntime>(sp => sp.GetRequiredService<ApiServerSwitchRuntime>());
        services.AddSingleton<ApiServerSwitchCoordinator>(sp => new ApiServerSwitchCoordinator(
            sp.GetRequiredService<ApiServerSettingsService>(),
            sp.GetRequiredService<ApiRuntimeEndpointState>(),
            sp.GetRequiredService<IApiServerSwitchRuntime>()));
        services.AddSingleton<IApiServerSwitchCoordinator>(sp =>
            sp.GetRequiredService<ApiServerSwitchCoordinator>());
        services.AddHttpClient<ICashierSessionRefreshApiClient, CashierSessionRefreshApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(5);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddSingleton<CashierSessionRefreshService>();
        services.AddSingleton<CashierSessionRefreshHostedService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CashierSessionRefreshHostedService>());
        services.AddSingleton<IScannerBindingService, ScannerBindingService>();
        services.AddSingleton<ILocalDeviceRepository, LocalDeviceRepository>();
        services.AddSingleton<ILocalCatalogRepository, LocalCatalogRepository>();
        services.AddSingleton<ILocalPromotionRepository, LocalPromotionRepository>();
        services.AddSingleton<IPromotionEvaluationService, PromotionEvaluationService>();
        services.AddSingleton<ILocalOrderRepository, LocalOrderRepository>();
        services.AddSingleton<ISharedHeldOrderPayloadProtector, WindowsDpapiSharedHeldOrderPayloadProtector>();
        services.AddSingleton<ISharedHeldOrderCanonicalSerializer, SharedHeldOrderCanonicalJsonSerializer>();
        services.AddSingleton<ISharedHeldOrderPayloadSerializer, SharedHeldOrderJsonPayloadSerializer>();
        services.AddSingleton<ISharedHeldOrderRepository, SharedHeldOrderRepository>();
        services.AddSingleton<ISharedHeldOrderMapper, SharedHeldOrderMapper>();
        services.AddSingleton<ISharedHeldOrderReverseMapper, SharedHeldOrderReverseMapper>();
        services.AddSingleton<ISharedHeldOrderPaymentSourceResolver, SharedHeldOrderPaymentSourceResolver>();
        services.AddSingleton<ISharedHeldOrderCoordinator, SharedHeldOrderCoordinator>();
        services.AddSingleton<ISharedHeldOrderPublicationGate, SharedHeldOrderPublicationGate>();
        services.AddSingleton<ISharedHeldOrderPublicationWorker>(sp =>
            new SharedHeldOrderPublicationWorker(
                sp.GetRequiredService<ISharedHeldOrderRepository>(),
                sp.GetRequiredService<ISharedHeldOrderMapper>(),
                sp.GetRequiredService<ISharedHeldOrderApiClient>(),
                sp.GetRequiredService<ISharedHeldOrderPublicationGate>(),
                order => order.FrozenPromotionRules));
        services.AddSingleton<SharedHeldOrderPublicationHostedService>();
        services.AddSingleton<IHostedService>(sp =>
            sp.GetRequiredService<SharedHeldOrderPublicationHostedService>());
        services.AddSingleton<ILocalCardPaymentAttemptRepository, LocalCardPaymentAttemptRepository>();
        services.AddSingleton<ILinklyPaymentAttemptContextAccessor, LinklyPaymentAttemptContextAccessor>();
        services.AddSingleton<ILinklyTerminalSelectionTransitionGate, LinklyTerminalSelectionTransitionGate>();
        services.AddSingleton<ILocalSquarePaymentAttemptRepository, LocalSquarePaymentAttemptRepository>();
        services.AddSingleton<ISquarePaymentAttemptContextAccessor, SquarePaymentAttemptContextAccessor>();
        services.AddSingleton<ILocalInstallmentOrderRepository, LocalInstallmentOrderRepository>();
        services.AddSingleton<ILocalInstallmentOperationRepository, LocalInstallmentOperationRepository>();
        services.AddSingleton<LocalFinancialSupervisorResolutionRepository>();
        services.AddSingleton(sp => new FinancialSupervisorAuditReplayService(
            sp.GetRequiredService<LocalFinancialSupervisorResolutionRepository>(),
            sp.GetRequiredService<ClientLogOutboxStore>()));
        services.AddSingleton<FinancialSupervisorAuditReplayHostedService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<FinancialSupervisorAuditReplayHostedService>());
        services.AddSingleton<ILocalOrderUploadRepository, LocalOrderUploadRepository>();
        services.AddSingleton<ISuspendedOrderRepository, SuspendedOrderRepository>();
        services.AddSingleton<ISyncQueueRepository, SyncQueueRepository>();
        services.AddSingleton<ILocalDailyCloseRepository, LocalDailyCloseRepository>();
        services.AddSingleton<ILocalLinklySettlementRepository, LocalLinklySettlementRepository>();
        services.AddSingleton<ILinklyUnresolvedSettlementReader>(sp =>
            sp.GetRequiredService<ILocalLinklySettlementRepository>());
        services.AddSingleton<ITestSalesDataResetService, TestSalesDataResetService>();
        services.AddHttpClient<ICatalogApiClient, CatalogApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            // 商品同步由调用方令牌控制，禁止 HttpClient 隐式 100 秒超时截断冷缓存构建。
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        // 中文注释：声明并自动解压 gzip，服务端只对 checksumVersion=2 的目录分页与码冲突候选压缩，其它接口响应不变。
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<IDeviceApiClient, DeviceApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(3);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<IRemoteMaintenanceApiClient, RemoteMaintenanceApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<IConnectivityApiClient, ConnectivityApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(3);
        })
        .AddRuntimeApiEndpoint();
        services.AddHttpClient<IAttendanceSigningKeyApiClient, AttendanceSigningKeyApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(5);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<IPosRuntimeStatusApiClient, PosRuntimeStatusApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(3);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<ICashierLoginApiClient, CashierLoginApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(5);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<IAdvertisementApiClient, AdvertisementApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(5);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<IPromotionApiClient, PromotionApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(5);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<IAdvertisementMediaCache, AdvertisementMediaCacheService>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddSingleton<IAdvertisementMediaCacheDirectoryProvider, AdvertisementMediaCacheDirectoryProvider>();
        services.AddHttpClient<IOrderHistoryApiClient, OrderHistoryApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<IStoreReceiptProfileApiClient, StoreReceiptProfileApiClient>(client =>
        {
            client.BaseAddress = GetApiBaseAddress();
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<IOrderSyncApiClient, OrderSyncApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<ISharedHeldOrderApiClient, SharedHeldOrderApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<IInstallmentApiClient, InstallmentApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<IVoucherApiClient, VoucherApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<ISquareTokenApiClient, SquareTokenApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(5);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<ILinklyCloudCredentialApiClient, LinklyCloudCredentialApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(5);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddSingleton<IDeviceFingerprintService, DeviceFingerprintService>();
        services.AddSingleton<IAppUpdateDeviceCredentialProvider, AppUpdateDeviceCredentialProvider>();
        services.AddSingleton<IUiPriorityCoordinator, UiPriorityCoordinator>();
        services.AddSingleton<ILocalCatalogSyncService, LocalCatalogSyncService>();
        services.AddSingleton<IRemoteLookupRefreshService, RemoteLookupRefreshService>();
        services.AddSingleton<ISpecialProductService, SpecialProductService>();
        services.AddSingleton<IShellCultureService, ShellCultureService>();
        services.AddSingleton<ICatalogSyncStatusService, CatalogSyncStatusService>();
        services.AddSingleton<IShellCatalogService, ShellCatalogService>();
        services.AddSingleton<IMainShellStartupService, MainShellStartupService>();
        services.AddSingleton<IShellSyncCenterService, ShellSyncCenterService>();
        services.AddSingleton<IAppVersionProvider, AppVersionProvider>();
        services.AddSingleton(serviceProvider =>
        {
            var state = new AppUpdateState();
            state.InitializeCurrentVersion(
                serviceProvider.GetRequiredService<IAppVersionProvider>().CurrentVersion);
            return state;
        });
        services.AddSingleton<IAppUpdateChannelProvider, AppUpdateChannelProvider>();
        services.AddHttpClient<IAppUpdateApiClient, AppUpdateApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        .AddRuntimeApiEndpoint();
        services.AddHttpClient<IAppUpdateDownloadService, AppUpdateDownloadService>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddSingleton<IAppUpdateDownloadDirectoryProvider, AppUpdateDownloadDirectoryProvider>();
        services.AddSingleton<IProcessLauncher, ProcessLauncher>();
        services.AddSingleton<IAppUpdateInstallSafetyGuard, ShellAppUpdateInstallSafetyGuard>();
        services.AddSingleton<IAppUpdateProgressWindowLauncher>(sp => new AppUpdateProgressWindowLauncher(
            sp.GetRequiredService<IProcessLauncher>(),
            sp.GetRequiredService<IAppVersionProvider>(),
            AppUpdateProgressWindowOptions.CreateDefault()));
        services.AddSingleton<IAppUpdateInstallerLauncher, AppUpdateInstallerLauncher>();
        services.AddSingleton<IAppUpdatePromptService, WpfAppUpdatePromptService>();
        services.AddSingleton<IAppUpdateCoordinator, AppUpdateCoordinator>();
        services.AddSingleton(sp => AppUpdateBackgroundCheckOptions.FromConfiguration(
            sp.GetService<IConfiguration>() ?? new ConfigurationBuilder().Build()));
        services.AddSingleton<AppUpdateBackgroundCheckScheduler>();
        services.AddSingleton<IAppUpdateElevationProbe, WindowsAppUpdateElevationProbe>();
        services.AddSingleton(sp => AppUpdateUnattendedInstallOptions.FromConfiguration(
            sp.GetService<IConfiguration>() ?? new ConfigurationBuilder().Build(),
            IsDebugBuild));
        services.AddSingleton<AppUpdateUnattendedInstallScheduler>();
        services.AddSingleton(sp => new AttendanceQrPanelViewModel(
            sp.GetRequiredService<IAttendanceSigningKeyApiClient>(),
            sp.GetRequiredService<IConnectivityApiClient>(),
            sp.GetRequiredService<ILocalDeviceRepository>(),
            sp.GetRequiredService<IDeviceFingerprintService>(),
            sp.GetRequiredService<ILocalAppSettingsRepository>(),
            sp.GetRequiredService<IDeviceAuthorizationProtector>(),
            sp.GetRequiredService<ILocalizationService>(),
            endpointState: sp.GetRequiredService<ApiRuntimeEndpointState>()));
        services.AddSingleton<ICardPaymentResultPolicy, LinklyCardPaymentResultPolicy>();
        services.AddSingleton<ICardPaymentResultPolicy, SquareCardPaymentResultPolicy>();
        services.AddSingleton<ICardPaymentResultPolicy, FallbackCardPaymentResultPolicy>();
        services.AddSingleton<ICashPaymentWorkflowService, CashPaymentWorkflowService>();
        services.AddSingleton<CardPaymentRecoveryService>();
        services.AddSingleton<ISquarePaymentRecoveryService, SquarePaymentRecoveryService>();
        services.AddSingleton<ICardPaymentRecoveryService, CardPaymentRecoveryCoordinator>();
        services.AddSingleton<ISuspendedOrderService, SuspendedOrderService>();
        services.AddSingleton<IRemoteOrderHistoryService, RemoteOrderHistoryService>();
        services.AddSingleton<IReceiptQueryService, ReceiptQueryService>();
        services.AddSingleton<IReceiptPrinterSettingsStore, ReceiptPrinterSettingsStore>();
        services.AddSingleton<IReceiptTextFormatter, ReceiptTextFormatter>();
        services.AddSingleton<IReceiptPrinterDriver, XpReceiptPrinterDriver>();
        services.AddSingleton<IReceiptPrintService, ReceiptPrintService>();
        services.AddSingleton<ILinklyBankReceiptPrinter, LinklyBankReceiptPrinter>();
        services.AddSingleton<ICashDrawerService, CashDrawerService>();
        services.AddSingleton<IDailyCloseService, DailyCloseService>();
        services.AddSingleton<IDailyClosePrintService, DailyClosePrintService>();
        services.AddSingleton<IOrderUploadService, OrderUploadService>();
        services.AddSingleton<IOrderUploadExecutionService, OrderUploadExecutionService>();
        services.AddSingleton<IInstallmentOperationService, InstallmentOperationService>();
        services.AddSingleton<IInstallmentOrderService, InstallmentOrderService>();
        services.AddSingleton<ICardTerminalSettingsStore>(sp => new CardTerminalSettingsStore(
            sp.GetRequiredService<ILocalAppSettingsRepository>(),
            sp.GetRequiredService<IDeviceAuthorizationProtector>(),
            sp.GetRequiredService<ISquareTokenApiClient>()));
        services.AddSingleton<ICardTerminalSettingsProvider>(sp => sp.GetRequiredService<ICardTerminalSettingsStore>());
        services.AddSingleton<ILinklyCloudSecretStore>(sp => sp.GetRequiredService<ICardTerminalSettingsStore>());
        services.AddSingleton<ILinklyEftClientFactory, LinklyEftClientFactory>();
        services.AddSingleton<LinklyTerminalClient>();
        services.AddHttpClient<ILinklyCloudApiClient, LinklyCloudApiClient>(client =>
        {
            client.Timeout = LinklyTimeoutPolicy.HttpTimeout;
        });
        services.AddSingleton<ILinklyCloudTerminalClient, LinklyCloudTerminalClient>();
        services.AddHttpClient(nameof(ILinklyBackendTerminalClient), client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = LinklyTimeoutPolicy.HttpTimeout;
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        // 付款页选择与实际扣款必须共享同一终端目录及 revision，避免首笔交易使用另一个冷缓存实例。
        services.AddSingleton<ILinklyBackendTerminalClient>(sp => new LinklyBackendTerminalClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ILinklyBackendTerminalClient)),
            sp.GetRequiredService<ILinklyTerminalDialogService>(),
            localization: sp.GetRequiredService<ILocalizationService>(),
            paymentAttemptContextAccessor: sp.GetRequiredService<ILinklyPaymentAttemptContextAccessor>(),
            bankReceiptPrinter: sp.GetRequiredService<ILinklyBankReceiptPrinter>()));
        services.AddHttpClient(LinklyBackendReceiptPrintedNotifier.HttpClientName, client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddSingleton<ICardReceiptPrintedNotifier, LinklyBackendReceiptPrintedNotifier>();
        services.AddSingleton<LinklyFallbackPromptCoordinator>();
        services.AddSingleton<ILinklyFallbackPromptCoordinator>(sp => sp.GetRequiredService<LinklyFallbackPromptCoordinator>());
        services.AddSingleton<ILinklyFallbackPromptService>(sp => sp.GetRequiredService<LinklyFallbackPromptCoordinator>());
        services.AddSingleton<ILinklyTerminalClient, ConfiguredLinklyTerminalClient>();
        services.AddHttpClient<ILinklySettlementSyncApiClient, LinklySettlementSyncApiClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddSingleton<LinklySettlementUploadService>();
        services.AddSingleton<ILinklySettlementUploadQueueReader>(sp =>
            sp.GetRequiredService<LinklySettlementUploadService>());
        services.AddSingleton<ILinklySettlementUploadExecutionService>(sp =>
            sp.GetRequiredService<LinklySettlementUploadService>());
        services.AddSingleton<LinklySettlementUploadWorker>();
        services.AddSingleton<ILinklySettlementUploadScheduler>(sp =>
            sp.GetRequiredService<LinklySettlementUploadWorker>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<LinklySettlementUploadWorker>());
        services.AddSingleton<ILinklySettlementService, LinklySettlementService>();
        services.AddHttpClient<ISquareTerminalSetupClient, SquareTerminalSetupClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddHttpClient<ISquareTerminalPaymentClient, SquareTerminalPaymentClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddSingleton<ICardTerminalSetupService>(sp => new CardTerminalSetupService(
            sp.GetRequiredService<ICardTerminalSettingsStore>(),
            sp.GetRequiredService<ISquareTerminalSetupClient>(),
            sp.GetRequiredService<ILinklyTerminalClient>(),
            sp.GetRequiredService<ILinklyCloudApiClient>(),
            sp.GetRequiredService<ILinklyCloudCredentialApiClient>(),
            sp.GetRequiredService<ILinklyCloudTerminalClient>(),
            sp.GetRequiredService<ILinklyBackendTerminalClient>(),
            sp.GetRequiredService<DeviceAuthorizationState>(),
            localization: sp.GetRequiredService<ILocalizationService>(),
            cardPaymentRecoveryService: sp.GetRequiredService<ICardPaymentRecoveryService>(),
            linklyPaymentAttemptContextAccessor: sp.GetRequiredService<ILinklyPaymentAttemptContextAccessor>(),
            linklyTerminalSelectionTransitionGate: sp.GetRequiredService<ILinklyTerminalSelectionTransitionGate>(),
            linklySettlementRepository: sp.GetRequiredService<ILinklyUnresolvedSettlementReader>()));
        services.AddHttpClient<ICardTerminalClient, ConfiguredCardTerminalClient>(client =>
        {
            client.BaseAddress = initialApiAddress;
            client.Timeout = LinklyTimeoutPolicy.HttpTimeout;
        })
        .AddRuntimeApiEndpoint()
        .AddHttpMessageHandler<DeviceAuthorizationMessageHandler>();
        services.AddSingleton<IVoucherTenderClient>(sp => sp.GetRequiredService<IVoucherApiClient>());
        services.AddSingleton<IDeviceRegistrationWorkflowService, DeviceRegistrationWorkflowService>();
        services.AddSingleton<ISpecialProductsWorkflowService, SpecialProductsWorkflowService>();
        services.AddSingleton<IReceiptReturnsWorkflowService, ReceiptReturnsWorkflowService>();
        services.AddSingleton<ICustomerDisplayOrchestrator, CustomerDisplayOrchestrator>();
        services.AddSingleton<IUserFeedbackService, WpfAudioUserFeedbackService>();
        services.AddSingleton<IApplicationExitService, WpfApplicationExitService>();
        services.AddSingleton<WpfConfirmationDialogService>();
        services.AddSingleton<IConfirmationDialogService>(sp => sp.GetRequiredService<WpfConfirmationDialogService>());
        services.AddSingleton<IConfirmationDialogPresenter>(sp => sp.GetRequiredService<WpfConfirmationDialogService>());
        services.AddSingleton<ICardRecoveryResultDialogService, CardRecoveryResultDialogService>();
        services.AddSingleton<WpfLinklyTerminalDialogService>();
        services.AddSingleton<ILinklyTerminalDialogService>(sp => sp.GetRequiredService<WpfLinklyTerminalDialogService>());
        services.AddSingleton<ILinklyTerminalDialogPresenter>(sp => sp.GetRequiredService<WpfLinklyTerminalDialogService>());
        services.AddTransient<IPosTerminalWorkflowService>(sp => new PosTerminalWorkflowService(
            sp.GetRequiredService<LocalSellableItemIndex>(),
            sp.GetRequiredService<PosCartService>(),
            uiPriorityCoordinator: sp.GetRequiredService<IUiPriorityCoordinator>(),
            isCatalogSyncActive: () => sp.GetRequiredService<IShellCatalogService>().IsCatalogSyncActive));
        services.AddTransient<PosTerminalWorkflowFactory>(sp => (remoteLookupRefreshAsync, reloadCatalogAsync) =>
            new PosTerminalWorkflowService(
                sp.GetRequiredService<LocalSellableItemIndex>(),
                sp.GetRequiredService<PosCartService>(),
                remoteLookupRefreshAsync,
                reloadCatalogAsync,
                sp.GetRequiredService<IUiPriorityCoordinator>(),
                () => sp.GetRequiredService<IShellCatalogService>().IsCatalogSyncActive));
        services.AddSingleton<IDisplayTopologyService, DisplayTopologyService>();
        services.AddSingleton<IColorThemeService, ColorThemeService>();
        services.AddSingleton<ColorThemeSwitcherViewModel>();
        services.AddSingleton<IWindowOwnerProvider, WpfWindowOwnerProvider>();
        services.AddSingleton<ICustomerDisplayWindowPreferenceStore, CustomerDisplayWindowPreferenceStore>();
        services.AddSingleton<ICustomerDisplayWindowService, CustomerDisplayWindowService>();
        services.AddSingleton<RawScannerInputProcessor>();
        services.AddSingleton<IRawScannerService, RawScannerService>();
        services.AddSingleton<LocalSellableItemIndex>();
        services.AddSingleton<PosCartService>();
        services.AddSingleton<CashCheckoutService>();
        services.AddSingleton<IPosCoreServices>(sp => new PosCoreServices(
            sp.GetRequiredService<LocalSellableItemIndex>(),
            sp.GetRequiredService<PosCartService>(),
            sp.GetRequiredService<CashCheckoutService>(),
            sp.GetRequiredService<ILocalSchemaService>()));

        services.AddSingleton<IPaymentTerminalFacade>(sp => new PaymentTerminalFacade(
            sp.GetRequiredService<IVoucherApiClient>(),
            sp.GetRequiredService<ICardTerminalClient>(),
            sp.GetRequiredService<ICardTerminalSetupService>(),
            sp.GetRequiredService<ILinklyTerminalDialogPresenter>(),
            sp.GetRequiredService<ICardPaymentRecoveryService>(),
            sp.GetRequiredService<ICardRecoveryResultDialogService>(),
            sp.GetRequiredService<ILinklyFallbackPromptCoordinator>()));

        services.AddSingleton<IPrintFacade>(sp => new PrintFacade(
            sp.GetRequiredService<IReceiptPrintService>(),
            sp.GetRequiredService<IReceiptPrinterSettingsStore>(),
            sp.GetRequiredService<IReceiptTextFormatter>(),
            sp.GetRequiredService<ILinklyBankReceiptPrinter>()));

        services.AddSingleton<IPosInfrastructureFacade>(sp => new PosInfrastructureFacade(
            sp.GetRequiredService<IConnectivityApiClient>(),
            sp.GetRequiredService<IRawScannerService>(),
            sp.GetRequiredService<IUserFeedbackService>(),
            sp.GetRequiredService<IApplicationExitService>(),
            sp.GetRequiredService<IConfirmationDialogService>()));

        services.AddSingleton(sp =>
        {
            var viewModel = new MainViewModel(
            sp.GetRequiredService<IPosCoreServices>(),
            sp.GetRequiredService<IPosInfrastructureFacade>(),
            sp.GetRequiredService<IPaymentTerminalFacade>(),
            sp.GetRequiredService<IPrintFacade>(),
            sp.GetRequiredService<IShellCultureService>(),
            sp.GetRequiredService<IShellCatalogService>(),
            sp.GetRequiredService<ILocalCatalogRepository>(),
            sp.GetRequiredService<IRemoteLookupRefreshService>(),
            sp.GetRequiredService<ISpecialProductService>(),
            sp.GetRequiredService<IMainShellStartupService>(),
            sp.GetRequiredService<ILocalOrderRepository>(),
            sp.GetRequiredService<IShellSyncCenterService>(),
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<ICustomerDisplayOrchestrator>(),
            sp.GetRequiredService<IReceiptQueryService>(),
            sp.GetRequiredService<ICashPaymentWorkflowService>(),
            sp.GetRequiredService<IDeviceRegistrationWorkflowService>(),
            sp.GetRequiredService<ISpecialProductsWorkflowService>(),
            sp.GetRequiredService<PosTerminalWorkflowFactory>(),
            sp.GetRequiredService<ISuspendedOrderService>(),
            sp.GetRequiredService<IRemoteOrderHistoryService>(),
            promotionEvaluationService: sp.GetRequiredService<IPromotionEvaluationService>(),
            receiptReturnsWorkflowService: sp.GetRequiredService<IReceiptReturnsWorkflowService>(),
            orderUploadExecutionService: sp.GetRequiredService<IOrderUploadExecutionService>(),
            dailyCloseService: sp.GetRequiredService<IDailyCloseService>(),
            dailyClosePrintService: sp.GetRequiredService<IDailyClosePrintService>(),
            cashDrawerService: sp.GetRequiredService<ICashDrawerService>(),
            installmentOrderService: sp.GetRequiredService<IInstallmentOrderService>(),
            testSalesDataResetService: sp.GetRequiredService<ITestSalesDataResetService>(),
            windowOwnerProvider: sp.GetRequiredService<IWindowOwnerProvider>(),
            appUpdateState: sp.GetRequiredService<AppUpdateState>(),
            checkForAppUpdateAsync: cancellationToken => sp.GetRequiredService<IAppUpdateCoordinator>().CheckForUpdatesAsync(manual: true, cancellationToken),
            cashierSessionContext: sp.GetRequiredService<ICashierSessionContext>(),
            cashierLoginService: sp.GetRequiredService<ICashierLoginService>(),
            runtimeStatusApiClient: sp.GetRequiredService<IPosRuntimeStatusApiClient>(),
             enforceCashierPermissions: true,
             operationAuditLogger: sp.GetRequiredService<IOperationAuditLogger>(),
                apiServerSettings: sp.GetRequiredService<ApiServerSettingsViewModel>(),
                operationAuthorizationService: sp.GetRequiredService<IOperationAuthorizationService>(),
                runtimeEndpointState: sp.GetRequiredService<ApiRuntimeEndpointState>(),
                attendanceQrPanel: sp.GetRequiredService<AttendanceQrPanelViewModel>(),
                linklySettlementService: sp.GetRequiredService<ILinklySettlementService>(),
                linklySettlementUploadQueueReader: sp.GetRequiredService<ILinklySettlementUploadQueueReader>(),
                linklySettlementUploadExecutionService: sp.GetRequiredService<ILinklySettlementUploadExecutionService>(),
                sharedHeldOrderCoordinator: sp.GetRequiredService<ISharedHeldOrderCoordinator>(),
                sharedHeldOrderApiClient: sp.GetRequiredService<ISharedHeldOrderApiClient>(),
                sharedHeldOrderRepository: sp.GetRequiredService<ISharedHeldOrderRepository>(),
                sharedHeldOrderPublicationWorker: sp.GetRequiredService<ISharedHeldOrderPublicationWorker>(),
                storeReceiptProfileApiClient: sp.GetRequiredService<IStoreReceiptProfileApiClient>(),
                cashierSessionRefreshService: sp.GetRequiredService<CashierSessionRefreshService>(),
                remoteMaintenanceService: sp.GetRequiredService<IRemoteMaintenanceService>(),
                paymentMethodSettingsService: sp.GetRequiredService<IPaymentMethodSettingsService>(),
                catalogSyncStatusService: sp.GetRequiredService<ICatalogSyncStatusService>());
            viewModel.ConfigureAuditSyncCenter(
                sp.GetRequiredService<ClientLogOutboxStore>(),
                sp.GetRequiredService<OperationAuditUploadService>(),
                sp.GetRequiredService<DeviceAuthorizationState>());
            sp.GetRequiredService<ApiServerSwitchRuntime>().ConfigureShell(
                () => new ApiServerPaymentSafetyState(
                    viewModel.CashPayment?.IsCardPaymentInProgress == true,
                    viewModel.CashPayment?.IsPaymentInteractionLocked == true,
                    viewModel.CashPayment?.PaymentTenders.Count ?? 0),
                viewModel.ReinitializeAfterServerSwitchAsync,
                switching => viewModel.IsApiServerSwitching = switching);
            return viewModel;
        });
        services.AddSingleton<MainWindow>();

        return services;
    }

    internal static Uri GetInitialApiBaseAddress(AppStartupOptions startupOptions)
    {
        ArgumentNullException.ThrowIfNull(startupOptions);

        return ResolveInitialApiBaseAddress(
            startupOptions.PreviewMode,
            Environment.GetEnvironmentVariable(
                ApiBaseUrlEnvironmentVariable,
                EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable(
                ApiBaseUrlEnvironmentVariable,
                EnvironmentVariableTarget.Process));
    }

    internal static Uri ResolveInitialApiBaseAddress(
        bool previewMode,
        string? userBaseUrl,
        string? processBaseUrl)
    {
        return previewMode
            ? new Uri(PreviewApiBaseAddress, UriKind.Absolute)
            : ResolveApiBaseAddress(userBaseUrl, processBaseUrl);
    }

    internal static Uri GetApiBaseAddress()
    {
        return ResolveApiBaseAddress(
            Environment.GetEnvironmentVariable(
                ApiBaseUrlEnvironmentVariable,
                EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable(
                ApiBaseUrlEnvironmentVariable,
                EnvironmentVariableTarget.Process));
    }

    internal static Uri ResolveApiBaseAddress(string? userBaseUrl, string? processBaseUrl)
    {
        var configuredBaseUrl = !string.IsNullOrWhiteSpace(userBaseUrl)
            ? userBaseUrl
            : processBaseUrl;
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
#if DEBUG
            ? ApiServerSettingsService.DevelopmentApiBaseAddress
#else
            ? ApiServerSettingsService.ReleaseApiBaseAddress
#endif
            : configuredBaseUrl.Trim();

        try
        {
            return new Uri(ApiServerSettingsService.NormalizeAddress(baseUrl), UriKind.Absolute);
        }
        catch (ArgumentException ex)
        {
            // 不记录配置值，避免把可能包含凭据的错误地址写入日志或启动提示。
            throw new ApiBaseAddressConfigurationException(ex);
        }
    }

    private static string GetLogDatabasePath(AppStartupOptions startupOptions)
    {
        if (startupOptions.PreviewMode)
        {
            return Path.Combine(Path.GetTempPath(), $"hbpos-logs-preview-{Environment.ProcessId}-{Guid.NewGuid():N}.db");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hbpos.Client",
            "hbpos_logs.db");
    }

    private static IHttpClientBuilder AddRuntimeApiEndpoint(this IHttpClientBuilder builder)
    {
        return builder.AddHttpMessageHandler<ApiRuntimeEndpointHandler>();
    }
}
