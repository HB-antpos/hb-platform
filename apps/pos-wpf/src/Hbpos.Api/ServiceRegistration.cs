using Hbpos.Api.Auth;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Api.Security;
using Microsoft.AspNetCore.DataProtection;
using BlazorApp.Shared.Options;

namespace Hbpos.Api;

public static class ServiceRegistration
{
    public static IServiceCollection AddHbposApiServices(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        if (configuration?.GetValue<bool>($"{PosIpadAppReviewOptions.SectionName}:Enabled") == true)
        {
            ValidateEnabledAppReviewConfiguration(configuration);
        }
        var globalKeysPath = ResolveDataProtectionKeysPath(
            configuration?["DataProtection:KeysPath"],
            "DataProtectionKeys");
        var configuredAttendanceKeysPath = configuration?["AttendanceQrDataProtection:KeysPath"];
        var environmentName = configuration?["ASPNETCORE_ENVIRONMENT"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(configuredAttendanceKeysPath))
        {
            // 关键逻辑：生产环境不得静默创建本地考勤 ring，否则会与主 backend 永久失配。
            throw new InvalidOperationException(
                "Production requires AttendanceQrDataProtection:KeysPath.");
        }

        var attendanceKeysPath = ResolveDataProtectionKeysPath(
            configuredAttendanceKeysPath,
            "AttendanceQrDataProtectionKeys");
        var configuredLinklyCredentialKeysPath =
            configuration?["LinklyCloudCredentialDataProtection:KeysPath"];
        if (string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(configuredLinklyCredentialKeysPath))
        {
            // 关键逻辑：Admin 与 POS 必须共享专用 ring，生产环境禁止回退到容器临时目录。
            throw new InvalidOperationException(
                "Production requires LinklyCloudCredentialDataProtection:KeysPath.");
        }

        // 非生产且未配置时，与 Admin 回落到同一个用户级目录；否则两个进程各用各自的程序目录，
        // 本机后台录入的终端凭据 POS 永远解不开（生产已在上面强制显式配置）。
        var linklyCredentialKeysPath = string.IsNullOrWhiteSpace(configuredLinklyCredentialKeysPath)
            && BlazorApp.Shared.Security.LinklyCloudTerminalCredentialDataProtection
                .ResolveSharedDevelopmentKeysPath() is { } sharedDevelopmentKeysPath
            ? sharedDevelopmentKeysPath
            : ResolveDataProtectionKeysPath(
                configuredLinklyCredentialKeysPath,
                "LinklyCloudCredentialDataProtectionKeys");

        Directory.CreateDirectory(globalKeysPath);
        // 关键逻辑：POS 自身票据使用独立持久 ring，不能挂载主 backend 的全局 ring。
        services.AddDataProtection()
            .SetApplicationName(configuration?["DataProtection:ApplicationName"] ?? "Hbpos.Api")
            .PersistKeysToFileSystem(new DirectoryInfo(globalKeysPath));
        // 关键逻辑：考勤密钥使用单独目录及固定应用名/purpose，仅与主 backend 的考勤 ring 共享。
        services.AddSingleton(AttendanceQrKeyDataProtection.CreateProtector(attendanceKeysPath));
        // 关键逻辑：只共享 Linkly 凭据专用 ring，不向 POS 暴露主 backend 的全局密钥。
        services.AddSingleton<BlazorApp.Shared.Security.ILinklyCloudTerminalCredentialProtector>(
            Hbpos.Api.Security.LinklyCloudTerminalCredentialDataProtection.CreateProtector(
                Hbpos.Api.Security.LinklyCloudTerminalCredentialDataProtection.CreateProvider(
                    linklyCredentialKeysPath)));
        services.AddSingleton<ICashierAuthorizationTicketService, CashierAuthorizationTicketService>();
        services.AddMemoryCache();
        services.AddScoped<IEmergencyLoginPublicKeyRepository, SqlSugarEmergencyLoginPublicKeyRepository>();
        services.AddScoped<IEmergencyLoginPublicKeyDistributionService, EmergencyLoginPublicKeyDistributionService>();
        services.AddScoped<IEmergencyLoginPublicKeyProvider, EmergencyLoginPublicKeyProvider>();
        services.AddScoped<IEmergencyGrantAuthorizationService, EmergencyGrantAuthorizationService>();
        services.AddOptions<SquareWebhookOptions>();
        services.AddOptions<AppUpdateOptions>();
        services.AddOptions<PosIpadOptions>();
        services.AddOptions<CatalogSnapshotOptions>();
        services.AddOptions<CatalogSyncOptions>();
        services.AddOptions<CatalogDailyPrebuildOptions>();
        services.AddOptions<InstallmentRepaymentClaimOptions>();
        services.AddOptions<InstallmentCancelClaimOptions>();
        services.AddOptions<InstallmentCrossDeviceLifecycleOptions>();
        services.AddOptions<SharedHeldOrderOptions>();
        services.AddOptions<DeviceActivationOptions>();
        services.AddOptions<RemoteMaintenanceGatewayOptions>();
        services.AddOptions<SquareTerminalRestOptions>()
            .Validate(
                options => SquareTerminalRestOptions.IsValidApiVersion(options.ApiVersion),
                "Square:ApiVersion must use yyyy-MM-dd.");
        if (configuration is not null)
        {
            services.Configure<LinklyCloudBackendAsyncOptions>(options =>
            {
                // 旧配置只作为兜底来源；新配置后应用，且空字符串不能覆盖已有有效值。
                ApplyLinklyCloudBackendAsyncOptionsSection(
                    options,
                    configuration.GetSection("LinklyCloudBackend"));
                ApplyLinklyCloudBackendAsyncOptionsSection(
                    options,
                    configuration.GetSection("LinklyCloudBackendAsync"));
            });
            services.Configure<SquareWebhookOptions>(configuration.GetSection("Square"));
            services.Configure<SquareTerminalRestOptions>(configuration.GetSection("Square"));
            services.Configure<AppUpdateOptions>(configuration.GetSection("AppUpdate"));
            services.Configure<PosIpadOptions>(configuration.GetSection("PosIpad"));
            services.Configure<PosIpadAppReviewOptions>(
                configuration.GetSection(PosIpadAppReviewOptions.SectionName));
            services.Configure<CatalogSnapshotOptions>(configuration.GetSection("CatalogSnapshot"));
            services.Configure<CatalogSyncOptions>(configuration.GetSection("CatalogSync"));
            services.Configure<CatalogDownloadFileOptions>(configuration.GetSection(CatalogDownloadFileOptions.SectionName));
            services.Configure<CatalogDailyPrebuildOptions>(configuration.GetSection("CatalogDailyPrebuild"));
            services.Configure<InstallmentRepaymentClaimOptions>(configuration.GetSection("InstallmentRepaymentClaims"));
            services.Configure<InstallmentCancelClaimOptions>(configuration.GetSection("InstallmentCancelClaims"));
            services.Configure<InstallmentCrossDeviceLifecycleOptions>(configuration.GetSection("InstallmentCrossDeviceLifecycle"));
            services.Configure<SharedHeldOrderOptions>(configuration.GetSection("SharedHeldOrders"));
            services.Configure<DeviceActivationOptions>(
                configuration.GetSection(DeviceActivationOptions.SectionName));
            services.Configure<RemoteMaintenanceGatewayOptions>(
                configuration.GetSection(RemoteMaintenanceGatewayOptions.SectionName));
        }

        services.AddScoped<HbposSqlSugarContext>();
        services.AddScoped<IOperationAuditIngestService, SqlSugarOperationAuditIngestService>();
        services.AddScoped<IOperationAuditReadService, SqlSugarOperationAuditReadService>();
        services.AddScoped<IOperationAuditSchemaInitializer, SqlSugarOperationAuditSchemaInitializer>();
        services.AddScoped<IDeviceRegistrationRepository, SqlSugarDeviceRegistrationRepository>();
        services.AddScoped<IPosIpadAppReviewAuthorizationBoundary, PosIpadAppReviewAuthorizationBoundary>();
        services.AddScoped<IDeviceService, DeviceService>();
        services.AddHttpClient<IRemoteMaintenanceGateway, RemoteMaintenanceGateway>();
        services.AddScoped<IDeviceActivationCodeService, DeviceActivationCodeService>();
        services.AddScoped<IDeviceAuthorizationService, DeviceAuthorizationService>();
        services.AddScoped<
            IDeviceActivationRecoveryAuthorizationService,
            DeviceActivationRecoveryAuthorizationService>();
        services.AddScoped<IAppUpdateDeviceIdentityValidator, AppUpdateDeviceIdentityValidator>();
        services.AddScoped<IAttendanceSigningKeyRegistrationService, AttendanceSigningKeyRegistrationService>();
        services.AddScoped<IAttendanceQrKeySchemaSqlExecutor, SqlSugarAttendanceQrKeySchemaSqlExecutor>();
        services.AddScoped<IAttendanceQrKeySchemaInitializer, SqlSugarAttendanceQrKeySchemaInitializer>();
        services.AddScoped<IDeviceRuntimeStatusSchemaSqlExecutor, SqlSugarDeviceRuntimeStatusSchemaSqlExecutor>();
        services.AddScoped<IDeviceRuntimeStatusSchemaInitializer, SqlSugarDeviceRuntimeStatusSchemaInitializer>();
        services.AddScoped<ICashierService, CashierService>();
        services.AddScoped<CatalogService>();
        services.AddScoped<ICatalogService>(sp => sp.GetRequiredService<CatalogService>());
        services.AddScoped<ICatalogIndexRefreshWorker>(sp => sp.GetRequiredService<CatalogService>());
        services.AddScoped<ICatalogTargetIndexSource>(sp => sp.GetRequiredService<CatalogService>());
        services.AddScoped<ICatalogFileSyncService, CatalogFileSyncService>();
        services.AddScoped<IPromotionRuleService, PromotionRuleService>();
        services.AddScoped<IAdvertisementPlaybackService, AdvertisementPlaybackService>();
        services.AddScoped<IStoreSchemaSqlExecutor, SqlSugarStoreSchemaSqlExecutor>();
        services.AddScoped<IStoreSchemaInitializer, SqlSugarStoreSchemaInitializer>();
        services.AddScoped<IStoreReceiptProfileService, StoreReceiptProfileService>();
        services.AddScoped<IStoreReceiptProfileReleaseRepository, SqlSugarStoreReceiptProfileReleaseRepository>();
        services.AddScoped<IStoreReceiptProfileReleaseService, StoreReceiptProfileReleaseService>();
        services.AddScoped<IAdvertisementSchemaInitializer, SqlSugarAdvertisementSchemaInitializer>();
        services.AddScoped<IOrderSyncSchemaSqlExecutor, SqlSugarOrderSyncSchemaSqlExecutor>();
        services.AddScoped<IOrderSyncSchemaInitializer, SqlSugarOrderSyncSchemaInitializer>();
        services.AddScoped<IOrderRepository, SqlSugarOrderRepository>();
        services.AddScoped<IOrderSyncService, OrderSyncService>();
        services.AddScoped<ISharedHeldOrderPayloadProtector, SharedHeldOrderPayloadProtector>();
        services.AddScoped<ISharedHeldOrderRepository, SqlSugarSharedHeldOrderRepository>();
        services.AddScoped<ISharedHeldOrderService, SharedHeldOrderService>();
        services.AddScoped<ISharedHeldOrderIdentityResolver, SharedHeldOrderIdentityResolver>();
        services.AddScoped<ISharedHeldOrderSchemaSqlExecutor, SqlSugarSharedHeldOrderSchemaSqlExecutor>();
        services.AddScoped<ISharedHeldOrderSchemaInitializer, SqlSugarSharedHeldOrderSchemaInitializer>();
        services.AddScoped<IOrderHistoryRepository, SqlSugarOrderHistoryRepository>();
        services.AddScoped<IOrderHistoryService, OrderHistoryService>();
        services.AddScoped<IOrderReturnRepository, SqlSugarOrderReturnRepository>();
        services.AddScoped<IOrderReturnService, OrderReturnService>();
        services.AddSingleton<InstallmentSchemaInitializationState>();
        services.AddSingleton<IInstallmentSchemaSqlExecutor, SqlSugarInstallmentSchemaSqlExecutor>();
        services.AddSingleton<IInstallmentSchemaInitializer, SqlSugarInstallmentSchemaInitializer>();
        services.AddHostedService<InstallmentSchemaStartupService>();
        services.AddScoped<IInstallmentRepository, SqlSugarInstallmentRepository>();
        services.AddScoped<InstallmentService>();
        services.AddScoped<IInstallmentService>(sp => sp.GetRequiredService<InstallmentService>());
        services.AddScoped<IInstallmentHistoryService>(sp => sp.GetRequiredService<InstallmentService>());
        services.AddScoped<IInstallmentRepaymentClaimRepository, SqlSugarInstallmentRepaymentClaimRepository>();
        services.AddScoped<IInstallmentRepaymentClaimCommitRepository, SqlSugarInstallmentRepaymentClaimCommitRepository>();
        services.AddScoped<IInstallmentRepaymentClaimService, InstallmentRepaymentClaimService>();
        services.AddScoped<IInstallmentRepaymentClaimIdentityResolver, InstallmentRepaymentClaimIdentityResolver>();
        services.AddScoped<IInstallmentRepaymentClaimSchemaSqlExecutor, SqlSugarInstallmentRepaymentClaimSchemaSqlExecutor>();
        services.AddScoped<IInstallmentRepaymentClaimSchemaInitializer, SqlSugarInstallmentRepaymentClaimSchemaInitializer>();
        services.AddSingleton<IInstallmentRepaymentClaimCommitFaultInjector, NoOpInstallmentRepaymentClaimCommitFaultInjector>();
        services.AddScoped<IInstallmentCancelClaimRepository, SqlSugarInstallmentCancelClaimRepository>();
        services.AddScoped<IInstallmentCancelClaimCommitRepository, SqlSugarInstallmentCancelClaimCommitRepository>();
        services.AddScoped<IInstallmentCancelClaimService, InstallmentCancelClaimService>();
        services.AddScoped<IInstallmentCancelClaimSchemaSqlExecutor, SqlSugarInstallmentCancelClaimSchemaSqlExecutor>();
        services.AddScoped<IInstallmentCancelClaimSchemaInitializer, SqlSugarInstallmentCancelClaimSchemaInitializer>();
        services.AddSingleton<IInstallmentCancelClaimCommitFaultInjector, NoOpInstallmentCancelClaimCommitFaultInjector>();
        services.AddScoped<IStoreVoucherRepository, SqlSugarStoreVoucherRepository>();
        services.AddScoped<IStoreVoucherService, StoreVoucherService>();
        services.AddScoped<ILinklyCloudCredentialRepository, SqlSugarLinklyCloudCredentialRepository>();
        services.AddScoped<ILinklyCloudCredentialService, LinklyCloudCredentialService>();
        services.AddScoped<ILinklyCloudCredentialSchemaSqlExecutor, SqlSugarLinklyCloudCredentialSchemaSqlExecutor>();
        services.AddScoped<ILinklyCloudCredentialSchemaInitializer, SqlSugarLinklyCloudCredentialSchemaInitializer>();
        services.AddScoped<ILinklyCloudBackendAsyncRepository, SqlSugarLinklyCloudBackendAsyncRepository>();
        services.AddScoped<ILinklyCloudBackendTerminalCredentialRepository, SqlSugarLinklyCloudBackendTerminalCredentialRepository>();
        services.AddScoped<ILinklyCloudTerminalRepository, SqlSugarLinklyCloudTerminalRepository>();
        services.AddScoped<ILinklyCloudTerminalService, LinklyCloudTerminalService>();
        services.AddHttpClient<ILinklyCloudBackendAsyncTransport, HttpLinklyCloudBackendAsyncTransport>(client =>
        {
            client.Timeout = LinklyCloudBackendTimeoutPolicy.HttpTimeout;
        })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .UseSocketsHttpHandler((handler, _) =>
            {
                // 固定工厂 Handler，并由连接生命周期定期刷新 DNS；并发由按终端闸门控制，
                // 连接数只设保险上限，避免跨门店请求挤在同一条连接上排队（见 LinklyCloudHttpConnectionPolicy）。
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15);
                handler.MaxConnectionsPerServer = LinklyCloudHttpConnectionPolicy.MaxConnectionsPerOrigin;
            });
        services.AddHttpClient<ILinklyCloudBackendTokenProvider, HttpLinklyCloudBackendTokenProvider>(client =>
        {
            client.Timeout = LinklyCloudBackendTimeoutPolicy.HttpTimeout;
        })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .UseSocketsHttpHandler((handler, _) =>
            {
                // 固定工厂 Handler，并由连接生命周期定期刷新 DNS；Token 与 REST 使用同一上限口径。
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15);
                handler.MaxConnectionsPerServer = LinklyCloudHttpConnectionPolicy.MaxConnectionsPerOrigin;
            });
        services.AddHttpClient<ILinklyCloudPairingTransport, HttpLinklyCloudPairingTransport>(client =>
        {
            client.Timeout = LinklyCloudBackendTimeoutPolicy.HttpTimeout;
        });
        services.AddHostedService<LinklyHttpConnectionMetricsService>();
        services.AddScoped<ILinklyCloudBackendAsyncService, LinklyCloudBackendAsyncService>();
        services.AddScoped<ILinklyCloudPairingService, LinklyCloudPairingService>();
        services.AddScoped<ILinklyCloudBackendAsyncSchemaSqlExecutor, SqlSugarLinklyCloudBackendAsyncSchemaSqlExecutor>();
        services.AddScoped<ILinklyCloudBackendAsyncSchemaInitializer, SqlSugarLinklyCloudBackendAsyncSchemaInitializer>();
        services.AddScoped<ILinklySettlementRepository, SqlSugarLinklySettlementRepository>();
        services.AddScoped<ILinklySettlementSyncService, LinklySettlementSyncService>();
        services.AddScoped<ILinklySettlementSchemaSqlExecutor, SqlSugarLinklySettlementSchemaSqlExecutor>();
        services.AddScoped<ILinklySettlementSchemaInitializer, SqlSugarLinklySettlementSchemaInitializer>();
        services.AddScoped<IDailyCloseRepository, SqlSugarDailyCloseRepository>();
        services.AddScoped<IDailyCloseSyncService, DailyCloseSyncService>();
        services.AddScoped<IDailyCloseSchemaSqlExecutor, SqlSugarDailyCloseSchemaSqlExecutor>();
        services.AddScoped<IDailyCloseSchemaInitializer, SqlSugarDailyCloseSchemaInitializer>();
        services.AddScoped<ISquareTokenRepository, SqlSugarSquareTokenRepository>();
        services.AddScoped<ISquareTokenService, SquareTokenService>();
        services.AddScoped<ISquareTokenSchemaSqlExecutor, SqlSugarSquareTokenSchemaSqlExecutor>();
        services.AddScoped<ISquareTokenSchemaInitializer, SqlSugarSquareTokenSchemaInitializer>();
        services.AddScoped<ISquareWebhookVerifier, SquareWebhookVerifier>();
        services.AddScoped<ISquareCheckoutSessionRepository, SqlSugarSquareCheckoutSessionRepository>();
        services.AddScoped<ISquareWebhookSchemaSqlExecutor, SqlSugarSquareWebhookSchemaSqlExecutor>();
        services.AddScoped<ISquareWebhookSchemaInitializer, SqlSugarSquareWebhookSchemaInitializer>();
        services.AddHttpClient<ISquareTerminalRestClient, HttpSquareTerminalRestClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<ISquareTerminalBackendService, SquareTerminalBackendService>();
        services.AddSingleton<ICatalogSnapshotStore>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CatalogSnapshotOptions>>().Value;
            var rootPath = string.IsNullOrWhiteSpace(options.RootPath)
                ? Path.Combine(AppContext.BaseDirectory, "App_Data", "CatalogSnapshots")
                : options.RootPath;
            return new GzipCatalogSnapshotStore(rootPath, options.MaxSnapshotsPerStore);
        });
        services.AddSingleton<ICatalogDownloadFileStore>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CatalogDownloadFileOptions>>().Value;
            var snapshotOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CatalogSnapshotOptions>>().Value;
            // 默认与目录快照同卷：生产快照目录已挂持久卷，无需改 compose 挂载。
            var rootPath = string.IsNullOrWhiteSpace(options.RootPath)
                ? Path.Combine(
                    string.IsNullOrWhiteSpace(snapshotOptions.RootPath)
                        ? Path.Combine(AppContext.BaseDirectory, "App_Data", "CatalogSnapshots")
                        : snapshotOptions.RootPath,
                    "download-files")
                : options.RootPath;
            return new CatalogDownloadFileStore(rootPath, options, sp.GetRequiredService<TimeProvider>());
        });
        services.AddSingleton<CatalogBackgroundRefreshService>();
        services.AddSingleton<ICatalogBackgroundRefreshScheduler>(
            sp => sp.GetRequiredService<CatalogBackgroundRefreshService>());
        services.AddHostedService(
            sp => sp.GetRequiredService<CatalogBackgroundRefreshService>());
        services.AddSingleton<IActiveCatalogStoreProvider, CatalogActiveStoreProvider>();
        services.AddSingleton(TimeProvider.System);
        services.AddHostedService<CatalogDailyPrebuildService>();
        services.AddSingleton<ICatalogIndexCache, CatalogIndexCache>();
        services.AddSingleton<ICatalogBaseDataCache, CatalogBaseDataCache>();
        services.AddSingleton<IPriceIndexBuilder, PriceIndexBuilder>();
        services.AddSingleton<IOrderSyncPlanner, OrderSyncPlanner>();
        // 门店时区缓存跨请求复用，内部按需创建作用域查询 HBweb 的门店表。
        services.AddSingleton<IStoreTimeZoneResolver, StoreTimeZoneResolver>();
        services.AddScoped<IStoreVoucherReservationService, SqlSugarStoreVoucherReservationService>();
        services.AddHttpClient<ILocalAppUpdateService, LocalAppUpdateService>(client =>
        {
            // 本地 WPF 更新检查不应继承 HttpClient 默认 100 秒超时，避免 POS API 线程长时间挂起。
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddHttpClient<IPosIpadUpdateDecisionGateway, HttpPosIpadUpdateDecisionGateway>(
            client =>
            {
                client.Timeout = TimeSpan.FromSeconds(8);
            });
        services.AddHttpClient<
            IPosHandheldUpdateDecisionGateway,
            HttpPosHandheldUpdateDecisionGateway
        >(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(8);
        });

        return services;
    }

    private static void ValidateEnabledAppReviewConfiguration(IConfiguration configuration)
    {
        var prefix = PosIpadAppReviewOptions.SectionName;
        if (string.IsNullOrWhiteSpace(configuration[$"{prefix}:StoreCode"]))
        {
            throw new InvalidOperationException($"Enabled {prefix} requires {prefix}:StoreCode.");
        }

        if (!DateTimeOffset.TryParse(configuration[$"{prefix}:ExpiresAtUtc"], out _))
        {
            throw new InvalidOperationException($"Enabled {prefix} requires a valid {prefix}:ExpiresAtUtc.");
        }

        if (configuration.GetValue<int>($"{prefix}:MaxActiveDevices") != 1)
        {
            throw new InvalidOperationException($"Enabled {prefix} requires {prefix}:MaxActiveDevices=1.");
        }

        if (!Guid.TryParse(configuration[$"{prefix}:GrantId"], out _))
        {
            throw new InvalidOperationException($"Enabled {prefix} requires a valid {prefix}:GrantId.");
        }

        var configuredHash = configuration[$"{prefix}:RegistrationCodeSha256"]?.Trim();
        try
        {
            if (string.IsNullOrEmpty(configuredHash)
                || Convert.FromHexString(configuredHash).Length != 32)
            {
                throw new FormatException();
            }
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"Enabled {prefix} requires a 64-character SHA-256 {prefix}:RegistrationCodeSha256.");
        }
    }

    private static string ResolveDataProtectionKeysPath(string? configuredPath, string defaultDirectoryName)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.Combine(AppContext.BaseDirectory, "App_Data", defaultDirectoryName);
        }

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
    }

    private static void ApplyLinklyCloudBackendAsyncOptionsSection(
        LinklyCloudBackendAsyncOptions options,
        IConfigurationSection section)
    {
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.ProductionNotificationBearer),
            value => options.ProductionNotificationBearer = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.SandboxNotificationBearer),
            value => options.SandboxNotificationBearer = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.PublicNotificationBaseUrl),
            value => options.PublicNotificationBaseUrl = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.ProductionAuthBaseUrl),
            value => options.ProductionAuthBaseUrl = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.SandboxAuthBaseUrl),
            value => options.SandboxAuthBaseUrl = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.ProductionRestBaseUrl),
            value => options.ProductionRestBaseUrl = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.SandboxRestBaseUrl),
            value => options.SandboxRestBaseUrl = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.PosName),
            value => options.PosName = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.PosVersion),
            value => options.PosVersion = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.SandboxPosVendorId),
            value => options.SandboxPosVendorId = value);
    }

    private static void AssignNonEmpty(
        IConfiguration section,
        string key,
        Action<string> assign)
    {
        var value = section[key];
        if (!string.IsNullOrWhiteSpace(value))
        {
            assign(value.Trim());
        }
    }

}
