using System.Diagnostics;

namespace BlazorApp.Api.Data.SchemaMigrations;

internal sealed record SchemaMigrationStep(
    string MigrationId,
    Func<ISchemaMigrationRuntime, CancellationToken, Task> ApplyAsync
);

internal sealed class SchemaMigrationCoordinator
{
    internal const string MainMigrationId = "20260827.001-hbweb-baseline";
    internal const string BrowserExtensionSessionGrantMigrationId =
        "20260830.001-browser-extension-session-grant";
    internal const string ContainerDetailQueryIndexesMigrationId =
        "20260902.001-container-detail-query-indexes";
    internal const string ContainerDetailCollaborationMigrationId =
        "20260903.001-container-detail-collaboration";
    internal const string ProductHqSyncOutboxMigrationId =
        "20260903.001-product-hq-sync-outbox";
    internal const string PricingCurveMigrationId = "20260909.001-pricing-curve";
    internal const string SalesDetailQueryProjectionMigrationId =
        "20260909.002-sales-detail-query-projection";
    internal const string SalesDetailQueryMappingUseMigrationId =
        "20260909.003-sales-detail-query-mapping-use";
    internal const string MobileOtaRuntimeTargetsMigrationId =
        "20260921.001-mobile-ota-runtime-targets";
    internal const string SalesDetailQueryMonthlyMigrationId =
        "20260922.001-sales-detail-query-monthly";
    internal const string LocalSupplierCategoryMigrationId =
        "20260923.001-local-supplier-category";
    internal const string CompactBoardMonthlyMigrationId =
        "20260924.001-compact-board-monthly";
    internal const string WarehouseOrderPickingMigrationId =
        "20260929.001-warehouse-order-picking";
    internal const string WarehouseOrderPickStockoutMigrationId =
        "20260930.001-warehouse-order-pick-stockout";
    internal const string WarehouseOrderPickAssignmentMigrationId =
        "20260930.002-warehouse-order-pick-assignment";
    internal const string AttendanceScheduleMealBreakMigrationId =
        "20261002.002-attendance-schedule-meal-break-count";
    internal const string AttendanceAvailabilityUnavailableMigrationId =
        "20261003.001-attendance-availability-unavailable";
    internal const string EmployeeProfileSensitiveBirthdayMigrationId =
        "20261004.001-employee-profile-sensitive-birthday";
    internal const string UserPasswordChangeRequirementMigrationId =
        "20261004.002-user-password-change-requirement";
    internal const string EmployeeMinorComplianceMigrationId =
        "20261004.003-employee-minor-compliance";
    internal const string UserPasswordResetCodeMigrationId =
        "20261004.004-user-password-reset-code";
    internal const string UserPasswordResetCodeTargetEmailMigrationId =
        "20261005.001-user-password-reset-code-target-email";
    internal const string MobileAndroidNativeUpdatePolicyMigrationId =
        "20261005.002-mobile-android-native-update-policy";
    internal const string StoreReceiptProfileReleaseMigrationId =
        "20261007.001-store-receipt-profile-release";
    internal const string AttendanceMealBreakMigrationId =
        "20261007.002-attendance-meal-break";
    internal const string StoreCashManagementMigrationId =
        "20261008.001-store-cash-management";
    internal const string PosmMigrationId = "20260827.001-hbweb-posm-baseline";
    internal const string MobileDeviceActivationMigrationId =
        "20260831.001-mobile-device-activation";
    internal const string LinklyMultiTerminalMigrationId =
        "20260903.001-linkly-multi-terminal";
    internal const string LegacyEmployeeLogRiskMigrationId =
        "20261001.001-legacy-employee-log-risk";
    internal const string PosOperationAuditRiskMigrationId =
        "20261002.001-pos-operation-audit-risk";

    internal static readonly IReadOnlyList<SchemaMigrationStep> MainMigrationSteps =
    [
        new(
            MainMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyMainBaselineAsync(cancellationToken)
        ),
        new(
            BrowserExtensionSessionGrantMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyBrowserExtensionSessionGrantAsync(cancellationToken)
        ),
        new(
            ContainerDetailQueryIndexesMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyContainerDetailQueryIndexesAsync(cancellationToken)
        ),
        new(
            ContainerDetailCollaborationMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyContainerDetailCollaborationAsync(cancellationToken)
        ),
        new(
            ProductHqSyncOutboxMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyProductHqSyncOutboxAsync(cancellationToken)
        ),
        new(
            PricingCurveMigrationId,
            static (runtime, cancellationToken) => runtime.ApplyPricingCurveAsync(cancellationToken)
        ),
        new(
            SalesDetailQueryProjectionMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplySalesDetailQueryProjectionAsync(cancellationToken)
        ),
        new(
            SalesDetailQueryMappingUseMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplySalesDetailQueryMappingUseAsync(cancellationToken)
        ),
        new(
            MobileOtaRuntimeTargetsMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyMobileOtaRuntimeTargetsAsync(cancellationToken)
        ),
        new(
            SalesDetailQueryMonthlyMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplySalesDetailQueryMonthlyAsync(cancellationToken)
        ),
        new(
            LocalSupplierCategoryMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyLocalSupplierCategoryAsync(cancellationToken)
        ),
        new(
            CompactBoardMonthlyMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyCompactBoardMonthlyAsync(cancellationToken)
        ),
        new(
            WarehouseOrderPickingMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyWarehouseOrderPickingAsync(cancellationToken)
        ),
        new(
            WarehouseOrderPickStockoutMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyWarehouseOrderPickStockoutAsync(cancellationToken)
        ),
        new(
            WarehouseOrderPickAssignmentMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyWarehouseOrderPickAssignmentAsync(cancellationToken)
        ),
        new(
            AttendanceScheduleMealBreakMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyAttendanceScheduleMealBreakAsync(cancellationToken)
        ),
        new(
            AttendanceAvailabilityUnavailableMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyAttendanceAvailabilityUnavailableAsync(cancellationToken)
        ),
        new(
            EmployeeProfileSensitiveBirthdayMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyEmployeeProfileSensitiveBirthdayAsync(cancellationToken)
        ),
        new(
            UserPasswordChangeRequirementMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyUserPasswordChangeRequirementAsync(cancellationToken)
        ),
        new(
            EmployeeMinorComplianceMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyEmployeeMinorComplianceAsync(cancellationToken)
        ),
        new(
            UserPasswordResetCodeMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyUserPasswordResetCodeAsync(cancellationToken)
        ),
        new(
            UserPasswordResetCodeTargetEmailMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyUserPasswordResetCodeTargetEmailAsync(cancellationToken)
        ),
        new(
            MobileAndroidNativeUpdatePolicyMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyMobileAndroidNativeUpdatePolicyAsync(cancellationToken)
        ),
        new(
            StoreReceiptProfileReleaseMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyStoreReceiptProfileReleaseAsync(cancellationToken)
        ),
        new(
            AttendanceMealBreakMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyAttendanceMealBreakAsync(cancellationToken)
        ),
        new(
            StoreCashManagementMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyStoreCashManagementAsync(cancellationToken)
        ),
    ];

    internal static readonly IReadOnlyList<SchemaMigrationStep> PosmMigrationSteps =
    [
        new(
            PosmMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyPosmBaselineAsync(cancellationToken)
        ),
        new(
            MobileDeviceActivationMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyMobileDeviceActivationAsync(cancellationToken)
        ),
        new(
            LinklyMultiTerminalMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyLinklyMultiTerminalAsync(cancellationToken)
        ),
        new(
            LegacyEmployeeLogRiskMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyLegacyEmployeeLogRiskAsync(cancellationToken)
        ),
        new(
            PosOperationAuditRiskMigrationId,
            static (runtime, cancellationToken) =>
                runtime.ApplyPosOperationAuditRiskAsync(cancellationToken)
        ),
    ];

    private const string MainScope = "Main";
    private const string PosmScope = "POSM";
    private const string DeviceActivationSignatureId = "device-activation-schema-signature";
    private const string MobileDeviceActivationSignatureId =
        "mobile-device-activation-schema-signature";
    private const string ContainerDetailQueryIndexesSignatureId =
        "container-detail-query-indexes-schema-signature";
    private const string ContainerDetailCollaborationSignatureId =
        "container-detail-collaboration-schema-signature";
    private const string ProductHqSyncOutboxSignatureId =
        "product-hq-sync-outbox-schema-signature";
    private const string LinklyMultiTerminalSignatureId =
        "linkly-multi-terminal-schema-signature";
    private const string MobileOtaRuntimeTargetsSignatureId =
        "mobile-ota-runtime-targets-schema-signature";
    private const string LocalSupplierCategorySignatureId =
        "local-supplier-category-schema-signature";
    private const string LegacyEmployeeLogRiskSignatureId =
        "legacy-employee-log-risk-schema-signature";
    private const string PosOperationAuditRiskSignatureId =
        "pos-operation-audit-risk-schema-signature";

    private readonly ISchemaMigrationRuntime _runtime;
    private readonly ILogger<SchemaMigrationCoordinator> _logger;
    private readonly IReadOnlyList<SchemaMigrationStep> _mainMigrations;
    private readonly IReadOnlyList<SchemaMigrationStep> _posmMigrations;

    public SchemaMigrationCoordinator(
        IConfiguration configuration,
        SqlSugarContext mainDbContext,
        POSMSqlSugarContext posmDbContext,
        ILogger<SchemaMigrationCoordinator> logger
    )
        : this(
            new SqlServerSchemaMigrationRuntime(
                configuration,
                mainDbContext,
                posmDbContext
            ),
            logger
        )
    { }

    internal SchemaMigrationCoordinator(
        ISchemaMigrationRuntime runtime,
        ILogger<SchemaMigrationCoordinator> logger
    )
        : this(runtime, logger, MainMigrationSteps, PosmMigrationSteps)
    { }

    internal SchemaMigrationCoordinator(
        ISchemaMigrationRuntime runtime,
        ILogger<SchemaMigrationCoordinator> logger,
        IReadOnlyList<SchemaMigrationStep> mainMigrations,
        IReadOnlyList<SchemaMigrationStep> posmMigrations
    )
    {
        _runtime = runtime;
        _logger = logger;
        _mainMigrations = ValidateMigrations(mainMigrations, MainScope);
        _posmMigrations = ValidateMigrations(posmMigrations, PosmScope);
    }

    public async Task<SchemaOperationResult> MigrateAsync(CancellationToken cancellationToken)
    {
        try
        {
            _runtime.EnsureSupportedProviders();
            // 数据库级先决条件必须在迁移锁、账本建表或业务 DDL 之前只读验证。
            await _runtime.ValidatePrerequisitesAsync(cancellationToken);
            await MigrateMainAsync(cancellationToken);
            await MigratePosmAsync(cancellationToken);

            var verification = await CheckCoreAsync(cancellationToken);
            return verification.Success
                ? SchemaOperationResult.MigrationSucceeded()
                : verification;
        }
        catch (OperationCanceledException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.Cancelled,
                SchemaDiagnosticCodes.Cancelled
            );
        }
        catch (SchemaMigrationLockUnavailableException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.MigrationLockUnavailable,
                SchemaDiagnosticCodes.MigrationLockUnavailable
            );
        }
        catch (DeviceActivationSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.DeviceActivationIncompatible
            );
        }
        catch (ContainerDetailQueryIndexSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.ContainerDetailQueryIndexesIncompatible
            );
        }
        catch (ContainerDetailCollaborationSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.ContainerDetailCollaborationIncompatible
            );
        }
        catch (PricingCurveSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.PricingCurveIncompatible
            );
        }
        catch (SalesDetailQueryProjectionSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.SalesDetailQueryProjectionIncompatible
            );
        }
        catch (ProductHqSyncOutboxSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.MainMigrationMissing
            );
        }
        catch (LinklyMultiTerminalSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.LinklyMultiTerminalIncompatible
            );
        }
        catch (MobileOtaRuntimeTargetsSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.MobileOtaRuntimeTargetsIncompatible
            );
        }
        catch (LocalSupplierCategorySchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.LocalSupplierCategoryIncompatible
            );
        }
        catch (LegacyEmployeeLogRiskSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.LegacyEmployeeLogRiskIncompatible
            );
        }
        catch (PosOperationAuditRiskSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.PosOperationAuditRiskIncompatible
            );
        }
        catch (SchemaProviderNotSupportedException)
        {
            LogResult(
                "All",
                "schema-migrate",
                elapsedMilliseconds: 0,
                "Failed",
                SchemaDiagnosticCodes.ProviderUnsupported
            );
            return SchemaOperationResult.Failure(
                SchemaExitCodes.DatabaseFailure,
                SchemaDiagnosticCodes.ProviderUnsupported
            );
        }
        catch (Exception)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.DatabaseFailure,
                SchemaDiagnosticCodes.MigrationFailure
            );
        }
    }

    public async Task<SchemaOperationResult> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            _runtime.EnsureSupportedProviders();
            await _runtime.ValidatePrerequisitesAsync(cancellationToken);
            return await CheckCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.Cancelled,
                SchemaDiagnosticCodes.Cancelled
            );
        }
        catch (DeviceActivationSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.DeviceActivationIncompatible
            );
        }
        catch (ContainerDetailQueryIndexSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.ContainerDetailQueryIndexesIncompatible
            );
        }
        catch (ContainerDetailCollaborationSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.ContainerDetailCollaborationIncompatible
            );
        }
        catch (PricingCurveSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.PricingCurveIncompatible
            );
        }
        catch (SalesDetailQueryProjectionSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.SalesDetailQueryProjectionIncompatible
            );
        }
        catch (ProductHqSyncOutboxSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.MainMigrationMissing
            );
        }
        catch (LinklyMultiTerminalSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.LinklyMultiTerminalIncompatible
            );
        }
        catch (MobileOtaRuntimeTargetsSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.MobileOtaRuntimeTargetsIncompatible
            );
        }
        catch (LocalSupplierCategorySchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.LocalSupplierCategoryIncompatible
            );
        }
        catch (LegacyEmployeeLogRiskSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.LegacyEmployeeLogRiskIncompatible
            );
        }
        catch (PosOperationAuditRiskSchemaMismatchException)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.PosOperationAuditRiskIncompatible
            );
        }
        catch (SchemaProviderNotSupportedException)
        {
            LogResult(
                "All",
                "schema-check",
                elapsedMilliseconds: 0,
                "Failed",
                SchemaDiagnosticCodes.ProviderUnsupported
            );
            return SchemaOperationResult.Failure(
                SchemaExitCodes.DatabaseFailure,
                SchemaDiagnosticCodes.ProviderUnsupported
            );
        }
        catch (Exception)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.DatabaseFailure,
                SchemaDiagnosticCodes.DatabaseFailure
            );
        }
    }

    private async Task MigrateMainAsync(CancellationToken cancellationToken)
    {
        await RunMigrationsAsync(
            SchemaDatabase.Main,
            MainScope,
            _mainMigrations,
            cancellationToken
        );
    }

    private async Task MigratePosmAsync(CancellationToken cancellationToken)
    {
        await RunMigrationsAsync(
            SchemaDatabase.Posm,
            PosmScope,
            _posmMigrations,
            cancellationToken
        );
    }

    private async Task RunMigrationsAsync(
        SchemaDatabase database,
        string databaseScope,
        IReadOnlyList<SchemaMigrationStep> migrations,
        CancellationToken cancellationToken
    )
    {
        await using var migrationSession = await _runtime.AcquireMigrationSessionAsync(
            database,
            cancellationToken
        );
        await migrationSession.EnsureHistoryTableAsync(cancellationToken);

        foreach (var migration in migrations)
        {
            await RunMigrationAsync(
                migrationSession,
                database,
                databaseScope,
                migration,
                cancellationToken
            );
        }
    }

    private async Task RunMigrationAsync(
        ISchemaMigrationSession migrationSession,
        SchemaDatabase database,
        string databaseScope,
        SchemaMigrationStep migration,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (await migrationSession.IsAppliedAsync(migration.MigrationId, cancellationToken))
            {
                LogResult(
                    databaseScope,
                    migration.MigrationId,
                    stopwatch.ElapsedMilliseconds,
                    "Skipped",
                    "SCHEMA_MIGRATION_ALREADY_APPLIED"
                );
                return;
            }

            // 每个 versioned step 各自管理事务；这里只在该步骤完全成功后登记账本。
            await migration.ApplyAsync(_runtime, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await migrationSession.RecordAppliedAsync(migration.MigrationId, cancellationToken);

            LogResult(
                databaseScope,
                migration.MigrationId,
                stopwatch.ElapsedMilliseconds,
                "Applied",
                "SCHEMA_MIGRATION_APPLIED"
            );
        }
        catch (Exception exception)
        {
            LogResult(
                databaseScope,
                migration.MigrationId,
                stopwatch.ElapsedMilliseconds,
                "Failed",
                exception switch
                {
                    OperationCanceledException => SchemaDiagnosticCodes.Cancelled,
                    SchemaMigrationLockUnavailableException =>
                        SchemaDiagnosticCodes.MigrationLockUnavailable,
                    _ => SchemaDiagnosticCodes.MigrationFailure,
                }
            );
            throw;
        }
    }

    private async Task<SchemaOperationResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        // 常规启动门禁只读检查账本，并仅对已登记的查询索引迁移核验精确签名。
        var mainApplied = await CheckLedgerAsync(
            SchemaDatabase.Main,
            MainScope,
            _mainMigrations,
            SchemaDiagnosticCodes.MainMigrationMissing,
            cancellationToken
        );
        var posmApplied = await CheckLedgerAsync(
            SchemaDatabase.Posm,
            PosmScope,
            _posmMigrations,
            SchemaDiagnosticCodes.PosmMigrationMissing,
            cancellationToken
        );
        if (mainApplied)
        {
            // 缺少迁移账本时保留 Missing 诊断；索引不存在并不等于已登记迁移发生签名漂移。
            await VerifyContainerDetailQueryIndexesAsync(cancellationToken);
            await VerifyContainerDetailCollaborationAsync(cancellationToken);
            await VerifyProductHqSyncOutboxAsync(cancellationToken);
            await _runtime.VerifyPricingCurveAsync(cancellationToken);
            await _runtime.VerifySalesDetailQueryProjectionAsync(cancellationToken);
            await VerifyMobileOtaRuntimeTargetsAsync(cancellationToken);
            await VerifyLocalSupplierCategoryAsync(cancellationToken);
        }
        if (posmApplied)
        {
            // 新 migration ID 尚未登记时优先返回 POSM Missing；仅在账本齐全后判断结构漂移。
            await VerifyLinklyMultiTerminalSchemaAsync(cancellationToken);
            await VerifyLegacyEmployeeLogRiskAsync(cancellationToken);
            await VerifyPosOperationAuditRiskAsync(cancellationToken);
        }
        await VerifyDeviceActivationSchemaAsync(cancellationToken);
        await VerifyMobileDeviceActivationSchemaAsync(cancellationToken);

        if (!mainApplied)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.MainMigrationMissing
            );
        }

        if (!posmApplied)
        {
            return SchemaOperationResult.Failure(
                SchemaExitCodes.SchemaNotReady,
                SchemaDiagnosticCodes.PosmMigrationMissing
            );
        }

        return SchemaOperationResult.Ready();
    }

    private async Task VerifyMobileOtaRuntimeTargetsAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _runtime.VerifyMobileOtaRuntimeTargetsAsync(cancellationToken);
            LogResult(
                MainScope,
                MobileOtaRuntimeTargetsSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Ready",
                SchemaDiagnosticCodes.Ready
            );
        }
        catch (Exception exception)
        {
            LogResult(
                MainScope,
                MobileOtaRuntimeTargetsSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Failed",
                exception is OperationCanceledException
                    ? SchemaDiagnosticCodes.Cancelled
                    : SchemaDiagnosticCodes.MobileOtaRuntimeTargetsIncompatible
            );
            throw;
        }
    }

    private async Task VerifyLocalSupplierCategoryAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _runtime.VerifyLocalSupplierCategoryAsync(cancellationToken);
            LogResult(
                MainScope,
                LocalSupplierCategorySignatureId,
                stopwatch.ElapsedMilliseconds,
                "Ready",
                SchemaDiagnosticCodes.Ready
            );
        }
        catch (Exception exception)
        {
            LogResult(
                MainScope,
                LocalSupplierCategorySignatureId,
                stopwatch.ElapsedMilliseconds,
                "Failed",
                exception switch
                {
                    OperationCanceledException => SchemaDiagnosticCodes.Cancelled,
                    LocalSupplierCategorySchemaMismatchException =>
                        SchemaDiagnosticCodes.LocalSupplierCategoryIncompatible,
                    _ => SchemaDiagnosticCodes.DatabaseFailure,
                }
            );
            throw;
        }
    }

    private async Task<bool> CheckLedgerAsync(
        SchemaDatabase database,
        string databaseScope,
        IReadOnlyList<SchemaMigrationStep> migrations,
        string missingDiagnosticCode,
        CancellationToken cancellationToken
    )
    {
        var allApplied = true;
        foreach (var migration in migrations)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var applied = await _runtime.IsMigrationAppliedAsync(
                    database,
                    migration.MigrationId,
                    cancellationToken
                );
                LogResult(
                    databaseScope,
                    migration.MigrationId,
                    stopwatch.ElapsedMilliseconds,
                    applied ? "Ready" : "Missing",
                    applied ? SchemaDiagnosticCodes.Ready : missingDiagnosticCode
                );
                allApplied &= applied;
            }
            catch (Exception exception)
            {
                LogResult(
                    databaseScope,
                    migration.MigrationId,
                    stopwatch.ElapsedMilliseconds,
                    "Failed",
                    exception is OperationCanceledException
                        ? SchemaDiagnosticCodes.Cancelled
                        : SchemaDiagnosticCodes.DatabaseFailure
                );
                throw;
            }
        }

        return allApplied;
    }

    private async Task VerifyContainerDetailQueryIndexesAsync(
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _runtime.VerifyContainerDetailQueryIndexesAsync(cancellationToken);
            LogResult(
                MainScope,
                ContainerDetailQueryIndexesSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Ready",
                SchemaDiagnosticCodes.Ready
            );
        }
        catch (Exception exception)
        {
            LogResult(
                MainScope,
                ContainerDetailQueryIndexesSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Failed",
                exception switch
                {
                    OperationCanceledException => SchemaDiagnosticCodes.Cancelled,
                    ContainerDetailQueryIndexSchemaMismatchException =>
                        SchemaDiagnosticCodes.ContainerDetailQueryIndexesIncompatible,
                    _ => SchemaDiagnosticCodes.DatabaseFailure,
                }
            );
            throw;
        }
    }

    private async Task VerifyContainerDetailCollaborationAsync(
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _runtime.VerifyContainerDetailCollaborationAsync(cancellationToken);
            LogResult(
                MainScope,
                ContainerDetailCollaborationSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Ready",
                SchemaDiagnosticCodes.Ready
            );
        }
        catch (Exception exception)
        {
            LogResult(
                MainScope,
                ContainerDetailCollaborationSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Failed",
                exception switch
                {
                    OperationCanceledException => SchemaDiagnosticCodes.Cancelled,
                    ContainerDetailCollaborationSchemaMismatchException =>
                        SchemaDiagnosticCodes.ContainerDetailCollaborationIncompatible,
                    _ => SchemaDiagnosticCodes.DatabaseFailure,
                }
            );
            throw;
        }
    }

    private async Task VerifyProductHqSyncOutboxAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _runtime.VerifyProductHqSyncOutboxAsync(cancellationToken);
            LogResult(
                MainScope,
                ProductHqSyncOutboxSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Ready",
                SchemaDiagnosticCodes.Ready
            );
        }
        catch (Exception exception)
        {
            LogResult(
                MainScope,
                ProductHqSyncOutboxSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Failed",
                exception switch
                {
                    OperationCanceledException => SchemaDiagnosticCodes.Cancelled,
                    ProductHqSyncOutboxSchemaMismatchException =>
                        SchemaDiagnosticCodes.MainMigrationMissing,
                    _ => SchemaDiagnosticCodes.DatabaseFailure,
                }
            );
            throw;
        }
    }

    private async Task VerifyDeviceActivationSchemaAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _runtime.VerifyDeviceActivationSchemaAsync(cancellationToken);
            LogResult(
                PosmScope,
                DeviceActivationSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Ready",
                SchemaDiagnosticCodes.Ready
            );
        }
        catch (Exception exception)
        {
            LogResult(
                PosmScope,
                DeviceActivationSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Failed",
                exception switch
                {
                    OperationCanceledException => SchemaDiagnosticCodes.Cancelled,
                    DeviceActivationSchemaMismatchException =>
                        SchemaDiagnosticCodes.DeviceActivationIncompatible,
                    _ => SchemaDiagnosticCodes.DatabaseFailure,
                }
            );
            throw;
        }
    }

    private async Task VerifyMobileDeviceActivationSchemaAsync(
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _runtime.VerifyMobileDeviceActivationSchemaAsync(cancellationToken);
            LogResult(
                PosmScope,
                MobileDeviceActivationSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Ready",
                SchemaDiagnosticCodes.Ready
            );
        }
        catch (Exception exception)
        {
            LogResult(
                PosmScope,
                MobileDeviceActivationSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Failed",
                exception switch
                {
                    OperationCanceledException => SchemaDiagnosticCodes.Cancelled,
                    DeviceActivationSchemaMismatchException =>
                        SchemaDiagnosticCodes.DeviceActivationIncompatible,
                    _ => SchemaDiagnosticCodes.DatabaseFailure,
                }
            );
            throw;
        }
    }

    private async Task VerifyLinklyMultiTerminalSchemaAsync(
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _runtime.VerifyLinklyMultiTerminalSchemaAsync(cancellationToken);
            LogResult(
                PosmScope,
                LinklyMultiTerminalSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Ready",
                SchemaDiagnosticCodes.Ready
            );
        }
        catch (Exception exception)
        {
            LogResult(
                PosmScope,
                LinklyMultiTerminalSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Failed",
                exception switch
                {
                    OperationCanceledException => SchemaDiagnosticCodes.Cancelled,
                    LinklyMultiTerminalSchemaMismatchException =>
                        SchemaDiagnosticCodes.LinklyMultiTerminalIncompatible,
                    _ => SchemaDiagnosticCodes.DatabaseFailure,
                }
            );
            throw;
        }
    }

    private async Task VerifyLegacyEmployeeLogRiskAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _runtime.VerifyLegacyEmployeeLogRiskAsync(cancellationToken);
            LogResult(
                PosmScope,
                LegacyEmployeeLogRiskSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Ready",
                SchemaDiagnosticCodes.Ready
            );
        }
        catch (Exception exception)
        {
            LogResult(
                PosmScope,
                LegacyEmployeeLogRiskSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Failed",
                exception switch
                {
                    OperationCanceledException => SchemaDiagnosticCodes.Cancelled,
                    LegacyEmployeeLogRiskSchemaMismatchException =>
                        SchemaDiagnosticCodes.LegacyEmployeeLogRiskIncompatible,
                    _ => SchemaDiagnosticCodes.DatabaseFailure,
                }
            );
            throw;
        }
    }

    private async Task VerifyPosOperationAuditRiskAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _runtime.VerifyPosOperationAuditRiskAsync(cancellationToken);
            LogResult(
                PosmScope,
                PosOperationAuditRiskSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Ready",
                SchemaDiagnosticCodes.Ready
            );
        }
        catch (Exception exception)
        {
            LogResult(
                PosmScope,
                PosOperationAuditRiskSignatureId,
                stopwatch.ElapsedMilliseconds,
                "Failed",
                exception switch
                {
                    OperationCanceledException => SchemaDiagnosticCodes.Cancelled,
                    PosOperationAuditRiskSchemaMismatchException =>
                        SchemaDiagnosticCodes.PosOperationAuditRiskIncompatible,
                    _ => SchemaDiagnosticCodes.DatabaseFailure,
                }
            );
            throw;
        }
    }

    private void LogResult(
        string databaseScope,
        string migrationId,
        long elapsedMilliseconds,
        string result,
        string diagnosticCode
    ) =>
        _logger.LogInformation(
            "Schema operation Scope={DatabaseScope} MigrationId={MigrationId} ElapsedMs={ElapsedMs} Result={Result} DiagnosticCode={DiagnosticCode}",
            databaseScope,
            migrationId,
            elapsedMilliseconds,
            result,
            diagnosticCode
        );

    private static IReadOnlyList<SchemaMigrationStep> ValidateMigrations(
        IReadOnlyList<SchemaMigrationStep> migrations,
        string databaseScope
    )
    {
        if (migrations.Count == 0)
        {
            throw new ArgumentException($"{databaseScope} 迁移步骤不得为空。", nameof(migrations));
        }

        if (
            migrations.Any(migration => string.IsNullOrWhiteSpace(migration.MigrationId))
            || migrations.Any(migration =>
                migration.MigrationId.Length
                    > SqlServerSchemaMigrationStore.MigrationIdMaxLength
            )
            || migrations.Select(migration => migration.MigrationId).Distinct(StringComparer.Ordinal).Count()
                != migrations.Count
        )
        {
            throw new ArgumentException(
                $"{databaseScope} 迁移 ID 必须非空、唯一且不超过 {SqlServerSchemaMigrationStore.MigrationIdMaxLength} 个字符。",
                nameof(migrations)
            );
        }

        return migrations.ToArray();
    }

}
