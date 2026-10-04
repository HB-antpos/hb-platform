using BlazorApp.Api.Services.Logging;
using BlazorApp.Api.Services.Performance;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;

namespace BlazorApp.Api.Data.SchemaMigrations;

internal enum SchemaDatabase
{
    Main,
    Posm,
}

/// <summary>
/// 将数据库 I/O 与迁移步骤隔离，协调器测试可精确验证跨库恢复和账本写入时机。
/// </summary>
internal interface ISchemaMigrationRuntime
{
    void EnsureSupportedProviders();

    Task ValidatePrerequisitesAsync(CancellationToken cancellationToken);

    Task<ISchemaMigrationSession> AcquireMigrationSessionAsync(
        SchemaDatabase database,
        CancellationToken cancellationToken
    );

    Task<bool> IsMigrationAppliedAsync(
        SchemaDatabase database,
        string migrationId,
        CancellationToken cancellationToken
    );

    Task ApplyMainBaselineAsync(CancellationToken cancellationToken);

    Task ApplyBrowserExtensionSessionGrantAsync(CancellationToken cancellationToken);

    Task ApplyContainerDetailQueryIndexesAsync(CancellationToken cancellationToken);

    Task ApplyContainerDetailCollaborationAsync(CancellationToken cancellationToken);

    Task VerifyContainerDetailCollaborationAsync(CancellationToken cancellationToken);

    Task VerifyContainerDetailQueryIndexesAsync(CancellationToken cancellationToken);

    Task ApplyProductHqSyncOutboxAsync(CancellationToken cancellationToken);

    Task VerifyProductHqSyncOutboxAsync(CancellationToken cancellationToken);

    Task ApplyPricingCurveAsync(CancellationToken cancellationToken);

    Task VerifyPricingCurveAsync(CancellationToken cancellationToken);

    Task ApplySalesDetailQueryProjectionAsync(CancellationToken cancellationToken);

    Task ApplySalesDetailQueryMappingUseAsync(CancellationToken cancellationToken);

    Task VerifySalesDetailQueryProjectionAsync(CancellationToken cancellationToken);

    Task ApplyMobileOtaRuntimeTargetsAsync(CancellationToken cancellationToken);

    Task VerifyMobileOtaRuntimeTargetsAsync(CancellationToken cancellationToken);

    Task ApplySalesDetailQueryMonthlyAsync(CancellationToken cancellationToken);

    Task VerifySalesDetailQueryMonthlyAsync(CancellationToken cancellationToken);

    Task ApplyLocalSupplierCategoryAsync(CancellationToken cancellationToken);

    Task VerifyLocalSupplierCategoryAsync(CancellationToken cancellationToken);

    Task ApplyCompactBoardMonthlyAsync(CancellationToken cancellationToken);

    Task VerifyCompactBoardMonthlyAsync(CancellationToken cancellationToken);

    Task ApplyWarehouseOrderPickingAsync(CancellationToken cancellationToken);

    Task VerifyWarehouseOrderPickingAsync(CancellationToken cancellationToken);

    Task ApplyWarehouseOrderPickStockoutAsync(CancellationToken cancellationToken);

    Task VerifyWarehouseOrderPickStockoutAsync(CancellationToken cancellationToken);

    Task ApplyWarehouseOrderPickAssignmentAsync(CancellationToken cancellationToken);

    Task VerifyWarehouseOrderPickAssignmentAsync(CancellationToken cancellationToken);

    Task ApplyAttendanceScheduleMealBreakAsync(CancellationToken cancellationToken);

    Task VerifyAttendanceScheduleMealBreakAsync(CancellationToken cancellationToken);

    Task ApplyAttendanceAvailabilityUnavailableAsync(CancellationToken cancellationToken);

    Task VerifyAttendanceAvailabilityUnavailableAsync(CancellationToken cancellationToken);

    Task ApplyEmployeeProfileSensitiveBirthdayAsync(CancellationToken cancellationToken);

    Task VerifyEmployeeProfileSensitiveBirthdayAsync(CancellationToken cancellationToken);
    Task ApplyUserPasswordChangeRequirementAsync(CancellationToken cancellationToken);

    Task VerifyUserPasswordChangeRequirementAsync(CancellationToken cancellationToken);

    Task ApplyEmployeeMinorComplianceAsync(CancellationToken cancellationToken);

    Task VerifyEmployeeMinorComplianceAsync(CancellationToken cancellationToken);

    Task ApplyUserPasswordResetCodeAsync(CancellationToken cancellationToken);

    Task VerifyUserPasswordResetCodeAsync(CancellationToken cancellationToken);

    Task ApplyPosmBaselineAsync(CancellationToken cancellationToken);

    Task ApplyMobileDeviceActivationAsync(CancellationToken cancellationToken);

    Task ApplyLinklyMultiTerminalAsync(CancellationToken cancellationToken);

    Task VerifyDeviceActivationSchemaAsync(CancellationToken cancellationToken);

    Task VerifyMobileDeviceActivationSchemaAsync(CancellationToken cancellationToken);

    Task VerifyLinklyMultiTerminalSchemaAsync(CancellationToken cancellationToken);

    Task ApplyLegacyEmployeeLogRiskAsync(CancellationToken cancellationToken);

    Task VerifyLegacyEmployeeLogRiskAsync(CancellationToken cancellationToken);

    Task ApplyPosOperationAuditRiskAsync(CancellationToken cancellationToken);

    Task VerifyPosOperationAuditRiskAsync(CancellationToken cancellationToken);
}

internal interface ISchemaMigrationSession : IAsyncDisposable
{
    Task EnsureHistoryTableAsync(CancellationToken cancellationToken);

    Task<bool> IsAppliedAsync(string migrationId, CancellationToken cancellationToken);

    Task RecordAppliedAsync(string migrationId, CancellationToken cancellationToken);
}

internal sealed class SchemaProviderNotSupportedException : Exception;

internal sealed class DeviceActivationSchemaMismatchException : Exception;

internal sealed class ContainerDetailQueryIndexSchemaMismatchException : Exception;

internal sealed class ContainerDetailCollaborationSchemaMismatchException : Exception;

internal sealed class ProductHqSyncOutboxSchemaMismatchException : Exception;

internal sealed class PricingCurveSchemaMismatchException : Exception;

internal sealed class SalesDetailQueryProjectionSchemaMismatchException : Exception;

internal sealed class LinklyMultiTerminalSchemaMismatchException : Exception;

internal sealed class MobileOtaRuntimeTargetsSchemaMismatchException : Exception;

internal sealed class LocalSupplierCategorySchemaMismatchException : Exception;

internal sealed class LegacyEmployeeLogRiskSchemaMismatchException : Exception;

internal sealed class PosOperationAuditRiskSchemaMismatchException : Exception;

internal sealed class SchemaBaselineSqlFailureException(string stepId) : Exception
{
    public string StepId { get; } = stepId;
}

internal sealed class SqlServerSchemaMigrationRuntime : ISchemaMigrationRuntime
{
    private static readonly SemaphoreSlim SchemaConsoleRedirectLock = new(1, 1);

    internal const string MainHistoryTable = "HBWebSchemaMigrationHistory";
    internal const string PosmHistoryTable = "HBWebPosmSchemaMigrationHistory";

    private const string MainHistoryPrimaryKey = "PK_HBWebSchemaMigrationHistory";
    private const string PosmHistoryPrimaryKey = "PK_HBWebPosmSchemaMigrationHistory";
    private const string MainLockResource = "HBWeb:SchemaMigration:Main";
    private const string PosmLockResource = "HBWeb:SchemaMigration:POSM";

    private readonly SqlSugarContext _mainDbContext;
    private readonly POSMSqlSugarContext _posmDbContext;
    private readonly DatabaseDefinition _mainDatabase;
    private readonly DatabaseDefinition _posmDatabase;
    private readonly int _commandTimeoutSeconds;
    private readonly string _applicationVersion;

    public SqlServerSchemaMigrationRuntime(
        IConfiguration configuration,
        SqlSugarContext mainDbContext,
        POSMSqlSugarContext posmDbContext
    )
    {
        _mainDbContext = mainDbContext;
        _posmDbContext = posmDbContext;
        _mainDatabase = new DatabaseDefinition(
            configuration.GetConnectionString("DefaultConnection") ?? string.Empty,
            MainHistoryTable,
            MainHistoryPrimaryKey,
            MainLockResource
        );
        _posmDatabase = new DatabaseDefinition(
            configuration.GetConnectionString("HBPOSMConnection") ?? string.Empty,
            PosmHistoryTable,
            PosmHistoryPrimaryKey,
            PosmLockResource
        );
        _commandTimeoutSeconds = Math.Clamp(
            configuration.GetValue("Database:CommandTimeoutSeconds", 60),
            1,
            1800
        );
        _applicationVersion = GetApplicationVersion();
    }

    public void EnsureSupportedProviders()
    {
        if (
            string.IsNullOrWhiteSpace(_mainDatabase.ConnectionString)
            || string.IsNullOrWhiteSpace(_posmDatabase.ConnectionString)
            || _mainDbContext.Db.CurrentConnectionConfig.DbType != DbType.SqlServer
            || _posmDbContext.Db.CurrentConnectionConfig.DbType != DbType.SqlServer
        )
        {
            throw new SchemaProviderNotSupportedException();
        }
    }

    public Task ValidatePrerequisitesAsync(CancellationToken cancellationToken) =>
        SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
            _mainDatabase.ConnectionString,
            PerformanceBaselineSchemaMigrator.ValidateSqlServerSnapshotIsolationSql,
            _commandTimeoutSeconds,
            cancellationToken
        );

    public async Task<ISchemaMigrationSession> AcquireMigrationSessionAsync(
        SchemaDatabase database,
        CancellationToken cancellationToken
    )
    {
        var definition = GetDefinition(database);
        var migrationLock = await SqlServerSchemaMigrationLock.AcquireAsync(
            definition.ConnectionString,
            definition.LockResource,
            _commandTimeoutSeconds,
            cancellationToken
        );
        return new SqlServerSchemaMigrationSession(
            migrationLock,
            definition,
            _applicationVersion,
            _commandTimeoutSeconds
        );
    }

    public Task<bool> IsMigrationAppliedAsync(
        SchemaDatabase database,
        string migrationId,
        CancellationToken cancellationToken
    )
    {
        var definition = GetDefinition(database);
        return SqlServerSchemaMigrationStore.IsAppliedAsync(
            definition.ConnectionString,
            definition.HistoryTable,
            migrationId,
            _commandTimeoutSeconds,
            cancellationToken
        );
    }

    public Task ApplyMainBaselineAsync(CancellationToken cancellationToken) =>
        ApplyBaselineAsync(SchemaDatabase.Main, cancellationToken);

    public Task ApplyBrowserExtensionSessionGrantAsync(
        CancellationToken cancellationToken
    ) => RunStrictBaselineAsync(
        _mainDbContext.Db,
        () => BrowserExtensionSessionGrantSchemaMigrator.EnsureAsync(
            _mainDbContext.Db,
            NullLogger.Instance
        ),
        cancellationToken,
        "main-browser-extension-session-grant"
    );

    public async Task ApplyContainerDetailQueryIndexesAsync(
        CancellationToken cancellationToken
    )
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            ContainerDetailQueryIndexSchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        // 精确签名在记录 migration ledger 前通过，同名错误索引不能被误标为已完成。
        await VerifyContainerDetailQueryIndexesAsync(cancellationToken);
    }

    public async Task ApplyContainerDetailCollaborationAsync(CancellationToken cancellationToken)
    {
        await RunStrictBaselineAsync(
            _mainDbContext.Db,
            () => ContainerDetailCollaborationSchemaMigrator.EnsureAsync(
                _mainDbContext.Db,
                NullLogger.Instance
            ),
            cancellationToken,
            "main-container-detail-collaboration"
        );
        // 成功建表后必须先验证精确签名，协调器才可写入 migration ledger。
        await VerifyContainerDetailCollaborationAsync(cancellationToken);
    }

    public async Task VerifyContainerDetailCollaborationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
                _mainDatabase.ConnectionString,
                ContainerDetailCollaborationSchemaMigrator.VerifySql,
                _commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (SqlException exception) when (exception.Number is >= 51540 and <= 51553)
        {
            throw new ContainerDetailCollaborationSchemaMismatchException();
        }
    }

    public async Task VerifyContainerDetailQueryIndexesAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
                _mainDatabase.ConnectionString,
                ContainerDetailQueryIndexSchema.VerifySql,
                _commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (SqlException exception) when (exception.Number is >= 51530 and <= 51539)
        {
            throw new ContainerDetailQueryIndexSchemaMismatchException();
        }
    }

    public async Task ApplyPricingCurveAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString, PricingCurveSchema.ApplySql,
            _commandTimeoutSeconds, cancellationToken);
        // 签名通过后，协调器才登记迁移成功，防止已有同名错误列被跳过。
        await VerifyPricingCurveAsync(cancellationToken);
    }

    public async Task VerifyPricingCurveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
                _mainDatabase.ConnectionString, PricingCurveSchema.VerifySql,
                _commandTimeoutSeconds, cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 51710 or 51711)
        {
            throw new PricingCurveSchemaMismatchException();
        }
    }

    public async Task ApplySalesDetailQueryProjectionAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString, SalesDetailQueryProjectionSchema.ApplySql,
            _commandTimeoutSeconds, cancellationToken);
        await VerifySalesDetailQueryProjectionAsync(cancellationToken);
    }

    public async Task ApplySalesDetailQueryMappingUseAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString, SalesDetailQueryProjectionSchema.ApplySql,
            _commandTimeoutSeconds, cancellationToken);
        await VerifySalesDetailQueryProjectionAsync(cancellationToken);
    }

    public async Task VerifySalesDetailQueryProjectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
                _mainDatabase.ConnectionString, SalesDetailQueryProjectionSchema.VerifySql,
                _commandTimeoutSeconds, cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is >= 51800 and <= 51802)
        {
            throw new SalesDetailQueryProjectionSchemaMismatchException();
        }
    }

    public async Task ApplySalesDetailQueryMonthlyAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            SalesDetailQueryMonthlySchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        await VerifySalesDetailQueryMonthlyAsync(cancellationToken);
    }

    public async Task VerifySalesDetailQueryMonthlyAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
            _mainDatabase.ConnectionString,
            SalesDetailQueryMonthlySchema.VerifySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
    }

    public async Task ApplyCompactBoardMonthlyAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            CompactBoardMonthlySchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        await VerifyCompactBoardMonthlyAsync(cancellationToken);
    }

    public async Task VerifyCompactBoardMonthlyAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
            _mainDatabase.ConnectionString,
            CompactBoardMonthlySchema.VerifySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
    }

    public async Task ApplyWarehouseOrderPickingAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            WarehouseOrderPickingSchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        await VerifyWarehouseOrderPickingAsync(cancellationToken);
    }

    public async Task VerifyWarehouseOrderPickingAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
            _mainDatabase.ConnectionString,
            WarehouseOrderPickingSchema.VerifySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
    }

    public async Task ApplyWarehouseOrderPickStockoutAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            WarehouseOrderPickStockoutSchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        // 精确签名通过后协调器才登记账本，已有同名但结构错误的表不会被误标为完成。
        await VerifyWarehouseOrderPickStockoutAsync(cancellationToken);
    }

    public async Task VerifyWarehouseOrderPickStockoutAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
            _mainDatabase.ConnectionString,
            WarehouseOrderPickStockoutSchema.VerifySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
    }

    public async Task ApplyWarehouseOrderPickAssignmentAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            WarehouseOrderPickAssignmentSchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        // 精确签名通过后协调器才登记账本，已有同名但结构错误的表不会被误标为完成。
        await VerifyWarehouseOrderPickAssignmentAsync(cancellationToken);
    }

    public async Task VerifyWarehouseOrderPickAssignmentAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
            _mainDatabase.ConnectionString,
            WarehouseOrderPickAssignmentSchema.VerifySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
    }

    public async Task ApplyAttendanceScheduleMealBreakAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            AttendanceScheduleMealBreakSchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        // 精确签名通过后协调器才登记账本，已有同名但结构错误的列不会被误标为完成。
        await VerifyAttendanceScheduleMealBreakAsync(cancellationToken);
    }

    public async Task VerifyAttendanceScheduleMealBreakAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
            _mainDatabase.ConnectionString,
            AttendanceScheduleMealBreakSchema.VerifySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
    }

    public async Task ApplyAttendanceAvailabilityUnavailableAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            AttendanceAvailabilityUnavailableSchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        // 精确签名通过后协调器才登记账本，已有同名但结构错误的列不会被误标为完成。
        await VerifyAttendanceAvailabilityUnavailableAsync(cancellationToken);
    }

    public async Task VerifyAttendanceAvailabilityUnavailableAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
            _mainDatabase.ConnectionString,
            AttendanceAvailabilityUnavailableSchema.VerifySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
    }

    public async Task ApplyEmployeeProfileSensitiveBirthdayAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            EmployeeProfileSensitiveBirthdaySchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        // 精确签名通过后协调器才登记账本，已有同名但结构错误的列不会被误标为完成。
        await VerifyEmployeeProfileSensitiveBirthdayAsync(cancellationToken);
    }

    public async Task VerifyEmployeeProfileSensitiveBirthdayAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
            _mainDatabase.ConnectionString,
            EmployeeProfileSensitiveBirthdaySchema.VerifySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
    }

    public async Task ApplyUserPasswordChangeRequirementAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            UserPasswordChangeRequirementSchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        // 精确签名通过后协调器才登记账本，已有同名但结构错误的表不会被误标为完成。
        await VerifyUserPasswordChangeRequirementAsync(cancellationToken);
    }

    public async Task VerifyUserPasswordChangeRequirementAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
            _mainDatabase.ConnectionString,
            UserPasswordChangeRequirementSchema.VerifySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
    }

    public async Task ApplyEmployeeMinorComplianceAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            EmployeeMinorComplianceSchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        // 六张表与关键列签名校验通过后协调器才登记账本，结构不兼容的同名表不会被误标为完成。
        await VerifyEmployeeMinorComplianceAsync(cancellationToken);
    }

    public async Task VerifyEmployeeMinorComplianceAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
            _mainDatabase.ConnectionString,
            EmployeeMinorComplianceSchema.VerifySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
    }

    public async Task ApplyUserPasswordResetCodeAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            UserPasswordResetCodeSchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        // 精确签名通过后协调器才登记账本，已有同名但结构错误的表不会被误标为完成。
        await VerifyUserPasswordResetCodeAsync(cancellationToken);
    }

    public async Task VerifyUserPasswordResetCodeAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
            _mainDatabase.ConnectionString,
            UserPasswordResetCodeSchema.VerifySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
    }

    public async Task ApplyMobileOtaRuntimeTargetsAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            MobileOtaRuntimeTargetsSchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        // 先通过精确签名检查，再由协调器登记 migration ledger。
        await VerifyMobileOtaRuntimeTargetsAsync(cancellationToken);
    }

    public async Task VerifyMobileOtaRuntimeTargetsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
                _mainDatabase.ConnectionString,
                MobileOtaRuntimeTargetsSchema.VerifySql,
                _commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (SqlException exception) when (exception.Number is >= 51910 and <= 51912)
        {
            throw new MobileOtaRuntimeTargetsSchemaMismatchException();
        }
    }

    public async Task ApplyLocalSupplierCategoryAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            LocalSupplierCategorySchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        // 精确签名通过后协调器才登记账本，已有同名但结构错误的表不会被误标为完成。
        await VerifyLocalSupplierCategoryAsync(cancellationToken);
    }

    public async Task VerifyLocalSupplierCategoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
                _mainDatabase.ConnectionString,
                LocalSupplierCategorySchema.VerifySql,
                _commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (SqlException exception) when (exception.Number is >= 51930 and <= 51939)
        {
            throw new LocalSupplierCategorySchemaMismatchException();
        }
    }

    public async Task ApplyProductHqSyncOutboxAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _mainDatabase.ConnectionString,
            ProductHqSyncOutboxSchemaMigrator.SqlServerApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        // 只有严格签名通过后，协调器才允许把此步骤写入迁移账本。
        await VerifyProductHqSyncOutboxAsync(cancellationToken);
    }

    public async Task VerifyProductHqSyncOutboxAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
                _mainDatabase.ConnectionString,
                ProductHqSyncOutboxSchemaMigrator.SqlServerVerifySql,
                _commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (SqlException exception) when (exception.Number is >= 51071 and <= 51081)
        {
            throw new ProductHqSyncOutboxSchemaMismatchException();
        }
    }

    public async Task ApplyPosmBaselineAsync(CancellationToken cancellationToken)
    {
        await ApplyBaselineAsync(SchemaDatabase.Posm, cancellationToken);
        // 严格签名必须在 coordinator 写入 POSM 账本前通过，不能留到最终 check 才失败。
        await VerifyDeviceActivationSchemaAsync(cancellationToken);
    }

    public Task ApplyMobileDeviceActivationAsync(CancellationToken cancellationToken) =>
        RunStrictBaselineAsync(
            _posmDbContext.Db,
            () => new MobileDeviceActivationSchemaMigrator(_posmDbContext)
                .MigrateAsync(cancellationToken),
            cancellationToken,
            "posm-mobile-device-activation"
        );

    public async Task ApplyLinklyMultiTerminalAsync(CancellationToken cancellationToken)
    {
        await RunStrictBaselineAsync(
            _posmDbContext.Db,
            () => PaymentTerminalSettingsSchemaMigrator.EnsureLinklyMultiTerminalAsync(
                _posmDbContext.Db,
                NullLogger.Instance
            ),
            cancellationToken,
            "posm-linkly-multi-terminal"
        );
        // 只有精确签名通过后，协调器才允许登记这个追加迁移版本。
        await VerifyLinklyMultiTerminalSchemaAsync(cancellationToken);
    }

    internal async Task ApplyBaselineAsync(
        SchemaDatabase database,
        CancellationToken cancellationToken
    )
    {
        if (database == SchemaDatabase.Main)
        {
            await RunStrictBaselineAsync(
                _mainDbContext.Db,
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _mainDbContext.EnsureLoginSessionSchema();
                    return Task.CompletedTask;
                },
                cancellationToken,
                "main-login-session"
            );
            await RunStrictBaselineAsync(
                _mainDbContext.Db,
                () =>
                {
                    _mainDbContext.CreateTable();
                    return Task.CompletedTask;
                },
                cancellationToken,
                "main-create-table"
            );
            await RunStrictBaselineAsync(
                _mainDbContext.Db,
                () => StartupSchemaMigrator.EnsureAsync(
                        _mainDbContext.Db,
                        NullLogger.Instance
                    ),
                cancellationToken,
                "main-startup-migrator"
            );
            await RunStrictBaselineAsync(
                _mainDbContext.Db,
                () => ApplicationLogSchemaMigrator.EnsureAsync(
                        _mainDbContext.Db,
                        NullLogger.Instance
                    ),
                cancellationToken,
                "main-application-log"
            );
            await RunStrictBaselineAsync(
                _mainDbContext.Db,
                () => PerformanceBaselineSchemaMigrator.EnsureAsync(
                        _mainDbContext.Db,
                        NullLogger.Instance
                    ),
                cancellationToken,
                "main-performance-baseline"
            );
            return;
        }

        await RunStrictBaselineAsync(
            _posmDbContext.Db,
            () => StartupSchemaMigrator.EnsurePosmAsync(
                    _posmDbContext.Db,
                    NullLogger.Instance
                ),
            cancellationToken,
            "posm-startup-migrator"
        );
        await RunStrictBaselineAsync(
            _posmDbContext.Db,
            () => PaymentTerminalSettingsSchemaMigrator.EnsureAsync(
                    _posmDbContext.Db,
                    NullLogger.Instance
                ),
            cancellationToken,
            "posm-payment-terminal"
        );
        await RunStrictBaselineAsync(
            _posmDbContext.Db,
            () => DeviceRuntimeStatusSchemaMigrator.EnsureAsync(
                    _posmDbContext.Db,
                    NullLogger.Instance
                ),
            cancellationToken,
            "posm-device-runtime-status"
        );
        await RunStrictBaselineAsync(
            _posmDbContext.Db,
            () => EmergencyLoginGrantSchemaMigrator.EnsureAsync(
                    _posmDbContext.Db,
                    NullLogger.Instance
                ),
            cancellationToken,
            "posm-emergency-login-grant"
        );
        await RunStrictBaselineAsync(
            _posmDbContext.Db,
            () => EmergencyLoginKeySchemaMigrator.EnsureAsync(
                    _posmDbContext.Db,
                    NullLogger.Instance
                ),
            cancellationToken,
            "posm-emergency-login-key"
        );
    }

    public async Task VerifyDeviceActivationSchemaAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
                _posmDatabase.ConnectionString,
                DeviceActivationCodeSchema.VerifySql,
                _commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (SqlException exception) when (exception.Number is >= 51100 and <= 51199)
        {
            throw new DeviceActivationSchemaMismatchException();
        }
    }

    public async Task VerifyMobileDeviceActivationSchemaAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
                _posmDatabase.ConnectionString,
                MobileDeviceActivationSchema.VerifySql,
                _commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (SqlException exception) when (exception.Number is >= 51400 and <= 51499)
        {
            throw new DeviceActivationSchemaMismatchException();
        }
    }

    public async Task VerifyLinklyMultiTerminalSchemaAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
                _posmDatabase.ConnectionString,
                PaymentTerminalSettingsSchemaMigrator.LinklyMultiTerminalVerifySql,
                _commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (SqlException exception) when (exception.Number is >= 51600 and <= 51616)
        {
            throw new LinklyMultiTerminalSchemaMismatchException();
        }
    }

    public async Task ApplyLegacyEmployeeLogRiskAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _posmDatabase.ConnectionString,
            LegacyEmployeeLogRiskSchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        // 精确签名通过后协调器才登记 POSM 账本，已有同名但结构错误的表不会被误标为完成。
        await VerifyLegacyEmployeeLogRiskAsync(cancellationToken);
    }

    public async Task VerifyLegacyEmployeeLogRiskAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
                _posmDatabase.ConnectionString,
                LegacyEmployeeLogRiskSchema.VerifySql,
                _commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (SqlException exception) when (exception.Number is >= 51950 and <= 51969)
        {
            throw new LegacyEmployeeLogRiskSchemaMismatchException();
        }
    }

    public async Task ApplyPosOperationAuditRiskAsync(CancellationToken cancellationToken)
    {
        await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
            _posmDatabase.ConnectionString,
            PosOperationAuditRiskSchema.ApplySql,
            _commandTimeoutSeconds,
            cancellationToken
        );
        // 精确签名通过后协调器才登记 POSM 账本，已有同名但结构错误的表不会被误标为完成。
        await VerifyPosOperationAuditRiskAsync(cancellationToken);
    }

    public async Task VerifyPosOperationAuditRiskAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
                _posmDatabase.ConnectionString,
                PosOperationAuditRiskSchema.VerifySql,
                _commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (SqlException exception) when (exception.Number is >= 51970 and <= 51989)
        {
            throw new PosOperationAuditRiskSchemaMismatchException();
        }
    }

    private DatabaseDefinition GetDefinition(SchemaDatabase database) =>
        database == SchemaDatabase.Main ? _mainDatabase : _posmDatabase;

    internal static async Task RunStrictBaselineAsync(
        ISqlSugarClient database,
        Func<Task> baseline,
        CancellationToken cancellationToken,
        string stepId = "baseline"
    )
    {
        await SchemaConsoleRedirectLock.WaitAsync(cancellationToken);
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var sqlFailureDetected = 0;

        try
        {
            // 旧迁移器会吞掉部分 SQL 异常；显式迁移必须把这类失败提升为 baseline 失败。
            database.Aop.OnError = _ => Interlocked.Exchange(ref sqlFailureDetected, 1);

            // 旧迁移器会直接输出原始异常。一次性 schema 进程中统一静音，外层只记安全诊断码。
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);

            await baseline();
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref sqlFailureDetected) != 0)
            {
                throw new SchemaBaselineSqlFailureException(stepId);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SchemaBaselineSqlFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            // 只保留固定阶段 ID，禁止把连接串、SQL 或原始异常带出一次性迁移进程。
            throw new SchemaBaselineSqlFailureException(stepId);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            // 当前两个上下文都没有常驻 OnError；显式迁移结束后恢复为空，避免影响后续只读检查。
            database.Aop.OnError = null;
            SchemaConsoleRedirectLock.Release();
        }
    }

    private static string GetApplicationVersion()
    {
        var version =
            typeof(SchemaMigrationCoordinator).Assembly.GetName().Version?.ToString() ?? "unknown";
        return version.Length <= 64 ? version : version[..64];
    }

    private sealed record DatabaseDefinition(
        string ConnectionString,
        string HistoryTable,
        string HistoryPrimaryKey,
        string LockResource
    );

    private sealed class SqlServerSchemaMigrationSession(
        SqlServerSchemaMigrationLock migrationLock,
        DatabaseDefinition definition,
        string applicationVersion,
        int commandTimeoutSeconds
    ) : ISchemaMigrationSession
    {
        public Task EnsureHistoryTableAsync(CancellationToken cancellationToken) =>
            SqlServerSchemaMigrationStore.EnsureHistoryTableAsync(
                migrationLock.Connection,
                definition.HistoryTable,
                definition.HistoryPrimaryKey,
                commandTimeoutSeconds,
                cancellationToken
            );

        public Task<bool> IsAppliedAsync(
            string migrationId,
            CancellationToken cancellationToken
        ) =>
            SqlServerSchemaMigrationStore.IsAppliedAsync(
                migrationLock.Connection,
                definition.HistoryTable,
                migrationId,
                commandTimeoutSeconds,
                cancellationToken
            );

        public Task RecordAppliedAsync(
            string migrationId,
            CancellationToken cancellationToken
        ) =>
            SqlServerSchemaMigrationStore.RecordAppliedAsync(
                migrationLock.Connection,
                definition.HistoryTable,
                migrationId,
                applicationVersion,
                commandTimeoutSeconds,
                cancellationToken
            );

        public ValueTask DisposeAsync() => migrationLock.DisposeAsync();
    }
}
