using System.Reflection;
using System.Text.RegularExpressions;
using BlazorApp.Api.Data.SchemaMigrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 启动 schema 协调器的无数据库契约测试。
///
/// 这些测试刻意不构造 SqlSugarContext，也不连接数据库；它们锁定命令入口、
/// 迁移账本、锁语义和正常启动的只读边界。真正的 SQL Server 幂等性由集成测试覆盖。
/// </summary>
public sealed class SchemaMigrationCoordinatorTests
{
    [Theory]
    [MemberData(nameof(ValidCommands))]
    public void SchemaCommand_只接受无参数和两个精确模式(string[] args, string expectedMode)
    {
        var command = SchemaCommand.Parse(args);

        Assert.Equal(expectedMode, command.Mode.ToString());
    }

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public void SchemaCommand_未知或重复参数必须拒绝(string[] args)
    {
        var command = SchemaCommand.Parse(args);

        Assert.Equal(SchemaCommandMode.Invalid, command.Mode);
        Assert.False(string.IsNullOrWhiteSpace(command.Error));
    }

    public static IEnumerable<object[]> ValidCommands =>
    [
        [Array.Empty<string>(), "Server"],
        [new[] { "--schema=check" }, "Check"],
        [new[] { "--schema=migrate" }, "Migrate"],
        [new[] { "--schema=sales-detail-projection-backfill" }, "SalesDetailProjectionBackfill"],
        [new[] { "--schema=sales-detail-projection-check" }, "SalesDetailProjectionCheck"],
    ];

    public static IEnumerable<object[]> InvalidCommands =>
    [
        [new[] { "--schema=CHECK" }],
        [new[] { "--Schema=migrate" }],
        [new[] { "--SCHEMA=check" }],
        [new[] { "--schema=unknown" }],
        [new[] { "--schema=check", "--schema=migrate" }],
        [new[] { "--schema=check", "--schema=check" }],
    ];

    [Fact]
    public void SchemaExitCodes_固定且可由部署脚本依赖()
    {
        var values = typeof(SchemaExitCodes)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(int) && field.IsLiteral)
            .Select(field => (int)field.GetRawConstantValue()!)
            .ToHashSet();

        Assert.Equal(6, values.Count);
        Assert.Contains(0, values);
        Assert.Contains(2, values);
        Assert.Contains(20, values);
        Assert.Contains(22, values);
        Assert.Contains(23, values);
        Assert.Contains(130, values);
    }

    [Fact]
    public void SchemaMigrationStep_必须绑定明确执行器而不是由运行时按ID猜测()
    {
        var stepExecutor = typeof(SchemaMigrationStep).GetProperty("ApplyAsync");
        var runtimeMethods = typeof(ISchemaMigrationRuntime)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.Name)
            .ToArray();

        Assert.NotNull(stepExecutor);
        Assert.Contains("ApplyMainBaselineAsync", runtimeMethods);
        Assert.Contains("ApplyBrowserExtensionSessionGrantAsync", runtimeMethods);
        Assert.Contains("ApplyContainerDetailQueryIndexesAsync", runtimeMethods);
        Assert.Contains("VerifyContainerDetailQueryIndexesAsync", runtimeMethods);
        Assert.Contains("ApplyProductHqSyncOutboxAsync", runtimeMethods);
        Assert.Contains("VerifyProductHqSyncOutboxAsync", runtimeMethods);
        Assert.Contains("ApplySalesDetailQueryProjectionAsync", runtimeMethods);
        Assert.Contains("VerifySalesDetailQueryProjectionAsync", runtimeMethods);
        Assert.Contains("ApplyMobileOtaRuntimeTargetsAsync", runtimeMethods);
        Assert.Contains("VerifyMobileOtaRuntimeTargetsAsync", runtimeMethods);
        Assert.Contains("ApplyLocalSupplierCategoryAsync", runtimeMethods);
        Assert.Contains("VerifyLocalSupplierCategoryAsync", runtimeMethods);
        Assert.Contains("ApplyAttendanceScheduleMealBreakAsync", runtimeMethods);
        Assert.Contains("VerifyAttendanceScheduleMealBreakAsync", runtimeMethods);
        Assert.Contains("ApplyAttendanceAvailabilityUnavailableAsync", runtimeMethods);
        Assert.Contains("VerifyAttendanceAvailabilityUnavailableAsync", runtimeMethods);
        Assert.Contains("ApplyEmployeeProfileSensitiveBirthdayAsync", runtimeMethods);
        Assert.Contains("VerifyEmployeeProfileSensitiveBirthdayAsync", runtimeMethods);
        Assert.Contains("ApplyUserPasswordChangeRequirementAsync", runtimeMethods);
        Assert.Contains("VerifyUserPasswordChangeRequirementAsync", runtimeMethods);
        Assert.Contains("ApplyEmployeeMinorComplianceAsync", runtimeMethods);
        Assert.Contains("VerifyEmployeeMinorComplianceAsync", runtimeMethods);
        Assert.Contains("ApplyUserPasswordResetCodeAsync", runtimeMethods);
        Assert.Contains("VerifyUserPasswordResetCodeAsync", runtimeMethods);
        Assert.Contains("ApplyUserPasswordResetCodeTargetEmailAsync", runtimeMethods);
        Assert.Contains("VerifyUserPasswordResetCodeTargetEmailAsync", runtimeMethods);
        Assert.Contains("ApplyMobileAndroidNativeUpdatePolicyAsync", runtimeMethods);
        Assert.Contains("VerifyMobileAndroidNativeUpdatePolicyAsync", runtimeMethods);
        Assert.Contains("ApplyStoreReceiptProfileReleaseAsync", runtimeMethods);
        Assert.Contains("VerifyStoreReceiptProfileReleaseAsync", runtimeMethods);
        Assert.Contains("ApplyAttendanceMealBreakAsync", runtimeMethods);
        Assert.Contains("VerifyAttendanceMealBreakAsync", runtimeMethods);
        Assert.Contains("ApplyStoreCashManagementAsync", runtimeMethods);
        Assert.Contains("VerifyStoreCashManagementAsync", runtimeMethods);
        Assert.Contains("ApplyStoreReceiptTermsAsync", runtimeMethods);
        Assert.Contains("VerifyStoreReceiptTermsAsync", runtimeMethods);
        Assert.Contains("ApplySeasonalCardSupplierBatchAsync", runtimeMethods);
        Assert.Contains("VerifySeasonalCardSupplierBatchAsync", runtimeMethods);
        Assert.Contains("ApplyAdvertisementOrientationAsync", runtimeMethods);
        Assert.Contains("VerifyAdvertisementOrientationAsync", runtimeMethods);
        Assert.Contains("ApplyAppNotificationAsync", runtimeMethods);
        Assert.Contains("VerifyAppNotificationAsync", runtimeMethods);
        Assert.Contains("ApplyFaceCaptureAsync", runtimeMethods);
        Assert.Contains("VerifyFaceCaptureAsync", runtimeMethods);
        Assert.Contains("ApplyFaceMatchAsync", runtimeMethods);
        Assert.Contains("VerifyFaceMatchAsync", runtimeMethods);
        Assert.Contains("ApplyStorePriceUpdateNoticeAsync", runtimeMethods);
        Assert.Contains("VerifyStorePriceUpdateNoticeAsync", runtimeMethods);
        Assert.Contains("ApplyContainerNewProductNoticeAsync", runtimeMethods);
        Assert.Contains("VerifyContainerNewProductNoticeAsync", runtimeMethods);
        Assert.Contains("ApplyAttendanceLocationSampleBatchAsync", runtimeMethods);
        Assert.Contains("VerifyAttendanceLocationSampleBatchAsync", runtimeMethods);
        Assert.Contains("ApplyFaceTrafficHourlyAsync", runtimeMethods);
        Assert.Contains("VerifyFaceTrafficHourlyAsync", runtimeMethods);
        Assert.Contains("ApplyFaceUnwelcomeAutoConfirmAsync", runtimeMethods);
        Assert.Contains("VerifyFaceUnwelcomeAutoConfirmAsync", runtimeMethods);
        Assert.Contains("ApplyAppPushAsync", runtimeMethods);
        Assert.Contains("VerifyAppPushAsync", runtimeMethods);
        Assert.Contains("ApplyUserLoginRestrictionAsync", runtimeMethods);
        Assert.Contains("VerifyUserLoginRestrictionAsync", runtimeMethods);
        Assert.Contains("ApplyStorePriceUpdateSettingAsync", runtimeMethods);
        Assert.Contains("VerifyStorePriceUpdateSettingAsync", runtimeMethods);
        Assert.Contains("ApplyUserNamePartsAsync", runtimeMethods);
        Assert.Contains("VerifyUserNamePartsAsync", runtimeMethods);
        Assert.Contains("ApplyWarehouseOrderReturnFieldsAsync", runtimeMethods);
        Assert.Contains("VerifyWarehouseOrderReturnFieldsAsync", runtimeMethods);
        Assert.Contains("ApplySupplierPreorderAsync", runtimeMethods);
        Assert.Contains("VerifySupplierPreorderAsync", runtimeMethods);
        Assert.Contains("ApplySupplierPreorderHistoryAsync", runtimeMethods);
        Assert.Contains("VerifySupplierPreorderHistoryAsync", runtimeMethods);
        Assert.Contains("ApplySupplierPreorderItemImageAsync", runtimeMethods);
        Assert.Contains("VerifySupplierPreorderItemImageAsync", runtimeMethods);
        Assert.Contains("ApplySupplierPreorderRevisionAsync", runtimeMethods);
        Assert.Contains("VerifySupplierPreorderRevisionAsync", runtimeMethods);
        Assert.Contains("ApplyStorePosApiEntryAsync", runtimeMethods);
        Assert.Contains("VerifyStorePosApiEntryAsync", runtimeMethods);
        Assert.Contains("ApplyHelpFaqAsync", runtimeMethods);
        Assert.Contains("VerifyHelpFaqAsync", runtimeMethods);
        Assert.Contains("ApplyAttendanceReviewAutoClockOutAsync", runtimeMethods);
        Assert.Contains("VerifyAttendanceReviewAutoClockOutAsync", runtimeMethods);
        Assert.Contains("ApplyAttendancePayrollLeaveAsync", runtimeMethods);
        Assert.Contains("VerifyAttendancePayrollLeaveAsync", runtimeMethods);
        Assert.Contains("ApplyPosmBaselineAsync", runtimeMethods);
        Assert.Contains("ApplyMobileDeviceActivationAsync", runtimeMethods);
        Assert.Contains("ApplyLinklyMultiTerminalAsync", runtimeMethods);
        Assert.Contains("VerifyMobileDeviceActivationSchemaAsync", runtimeMethods);
        Assert.Contains("VerifyLinklyMultiTerminalSchemaAsync", runtimeMethods);
        Assert.Contains("ApplyLegacyEmployeeLogRiskAsync", runtimeMethods);
        Assert.Contains("VerifyLegacyEmployeeLogRiskAsync", runtimeMethods);
        Assert.Contains("ApplyPosOperationAuditRiskAsync", runtimeMethods);
        Assert.Contains("VerifyPosOperationAuditRiskAsync", runtimeMethods);
        Assert.Contains("ApplyPosDeviceApiEntryAsync", runtimeMethods);
        Assert.Contains("VerifyPosDeviceApiEntryAsync", runtimeMethods);
        Assert.Contains("ValidatePrerequisitesAsync", runtimeMethods);
        Assert.DoesNotContain("ApplyMigrationAsync", runtimeMethods);
    }

    [Fact]
    public void PosmMigrationSteps_必须包含独立Linkly多终端迁移()
    {
        Assert.Contains(
            SchemaMigrationCoordinator.PosmMigrationSteps,
            step =>
                step.MigrationId.Contains(
                    "linkly-multi-terminal",
                    StringComparison.OrdinalIgnoreCase
                )
                && step.MigrationId != SchemaMigrationCoordinator.PosmMigrationId
        );
    }

    [Fact]
    public void DeviceActivation主键签名_必须固定为Clustered主键()
    {
        var sourcePath = Path.Combine(
            FindRepoRoot(),
            "services/backend/BlazorApp.Shared/Models/POSM/DeviceActivationCodeGrant.cs"
        );
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("indexInfo.[type] = 1", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coordinator_迁移账本和SQL_Server会话锁保持固定契约()
    {
        var coordinatorSource = await ReadCoordinatorSourceAsync();
        var runtimeSource = await ReadRuntimeSourceAsync();
        var storeSource = await ReadStoreSourceAsync();
        var linklyApply = ExtractMethod(runtimeSource, "ApplyLinklyMultiTerminalAsync");

        Assert.Contains("HBWebSchemaMigrationHistory", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("HBWebPosmSchemaMigrationHistory", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("MigrationId", storeSource, StringComparison.Ordinal);
        Assert.Contains("AppliedAtUtc", storeSource, StringComparison.Ordinal);
        Assert.Contains("ApplicationVersion", storeSource, StringComparison.Ordinal);

        // lock 必须独立连接、session-owned 且不等待，避免常规启动占用 schema 锁。
        Assert.Contains("sp_getapplock", storeSource, StringComparison.Ordinal);
        Assert.Contains("@LockOwner = N'Session'", storeSource, StringComparison.Ordinal);
        Assert.Contains("@LockTimeout = 0", storeSource, StringComparison.Ordinal);
        Assert.Contains("SqlConnection", storeSource, StringComparison.Ordinal);
        Assert.Contains("SchemaExitCodes.MigrationLockUnavailable", coordinatorSource, StringComparison.Ordinal);
        Assert.True(
            IndexOfRequired(
                linklyApply,
                "PaymentTerminalSettingsSchemaMigrator.EnsureLinklyMultiTerminalAsync"
            ) < IndexOfRequired(linklyApply, "VerifyLinklyMultiTerminalSchemaAsync"),
            "Linkly v2 只能在专用 DDL 与精确签名都通过后由协调器登记。"
        );
    }

    [Fact]
    public async Task Coordinator_两个baseline维持既定顺序且成功后才登记()
    {
        var source = await ReadCoordinatorSourceAsync();
        var runtimeSource = await ReadRuntimeSourceAsync();
        var baseline = ExtractMethod(runtimeSource, "ApplyBaselineAsync");

        var mainLoginSession = IndexOfRequired(baseline, "EnsureLoginSessionSchema()");
        var mainCreateTable = IndexOfRequired(baseline, "CreateTable()");
        var mainStartup = IndexOfRequired(baseline, "StartupSchemaMigrator.EnsureAsync");
        var mainApplicationLog = IndexOfRequired(baseline, "ApplicationLogSchemaMigrator.EnsureAsync");
        var mainPerformance = IndexOfRequired(baseline, "PerformanceBaselineSchemaMigrator.EnsureAsync");
        Assert.True(
            mainLoginSession < mainCreateTable
                && mainCreateTable < mainStartup
                && mainStartup < mainApplicationLog
                && mainApplicationLog < mainPerformance,
            "主库 baseline 必须依次执行 LoginSession、CreateTable、Startup、ApplicationLog 和 Performance 迁移。"
        );
        Assert.Contains("database.Aop.OnError", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("SchemaBaselineSqlFailureException", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("Console.SetOut(TextWriter.Null)", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("Console.SetError(TextWriter.Null)", runtimeSource, StringComparison.Ordinal);

        var installment = IndexOfRequired(baseline, "EnsurePosmAsync");
        var paymentTerminal = IndexOfRequired(baseline, "PaymentTerminalSettingsSchemaMigrator.EnsureAsync");
        var deviceRuntime = IndexOfRequired(baseline, "DeviceRuntimeStatusSchemaMigrator.EnsureAsync");
        var emergencyGrant = IndexOfRequired(baseline, "EmergencyLoginGrantSchemaMigrator.EnsureAsync");
        var emergencyKey = IndexOfRequired(baseline, "EmergencyLoginKeySchemaMigrator.EnsureAsync");
        Assert.True(
            installment < paymentTerminal
                && paymentTerminal < deviceRuntime
                && deviceRuntime < emergencyGrant
                && emergencyGrant < emergencyKey,
            "POSM baseline 必须沿用分期订单、终端设置、设备状态、紧急 Grant、紧急 Key 的顺序。"
        );

        Assert.True(
            IndexOfRequired(source, "RecordAppliedAsync")
                > IndexOfRequired(source, "await migration.ApplyAsync(_runtime, cancellationToken)"),
            "任一 baseline 失败不得提前写迁移账本；登记必须在对应 baseline 成功之后。"
        );

        var migrateMethod = ExtractMethod(source, "MigrateAsync");
        Assert.True(
            IndexOfRequired(migrateMethod, "MigrateMainAsync")
                < IndexOfRequired(migrateMethod, "MigratePosmAsync"),
            "主库成功登记后才开始 POSM；POSM 失败重跑时必须保留并跳过主库记录。"
        );
    }

    [Fact]
    public async Task Coordinator_已应用跳过并支持主库成功后仅重跑POSM()
    {
        var source = await ReadCoordinatorSourceAsync();
        var runMigration = ExtractMethod(source, "RunMigrationAsync");

        Assert.Contains("migrationSession.IsAppliedAsync", runMigration, StringComparison.Ordinal);
        Assert.Contains("SCHEMA_MIGRATION_ALREADY_APPLIED", runMigration, StringComparison.Ordinal);
        Assert.Contains("migration.ApplyAsync", runMigration, StringComparison.Ordinal);
        Assert.Contains("migrationSession.RecordAppliedAsync", runMigration, StringComparison.Ordinal);

        Assert.True(
            IndexOfRequired(runMigration, "SCHEMA_MIGRATION_ALREADY_APPLIED")
                < IndexOfRequired(runMigration, "migration.ApplyAsync"),
            "已有 migration ID 时必须先返回，不能再次执行 baseline。"
        );
        Assert.True(
            IndexOfRequired(source, "MigrateMainAsync")
                < IndexOfRequired(source, "MigratePosmAsync"),
            "主库完成并登记后，POSM 失败重跑必须能跳过主库并继续 POSM。"
        );
    }

    [Fact]
    public async Task MigrateAsync_可追加迁移步骤必须按声明顺序独立记账()
    {
        const string appendedMigrationId = "20260828.002-main-appendable-contract";
        var runtime = new FakeSchemaMigrationRuntime();
        var coordinator = new SchemaMigrationCoordinator(
            runtime,
            NullLogger<SchemaMigrationCoordinator>.Instance,
            [
                new SchemaMigrationStep(
                    SchemaMigrationCoordinator.MainMigrationId,
                    static (migrationRuntime, cancellationToken) =>
                        migrationRuntime.ApplyMainBaselineAsync(cancellationToken)
                ),
                new SchemaMigrationStep(
                    appendedMigrationId,
                    (migrationRuntime, cancellationToken) =>
                        ((FakeSchemaMigrationRuntime)migrationRuntime).ApplyMainAppendAsync(
                            appendedMigrationId,
                            cancellationToken
                        )
                ),
            ],
            [
                new SchemaMigrationStep(
                    SchemaMigrationCoordinator.PosmMigrationId,
                    static (migrationRuntime, cancellationToken) =>
                        migrationRuntime.ApplyPosmBaselineAsync(cancellationToken)
                ),
            ]
        );

        var result = await coordinator.MigrateAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(
            runtime.Events.IndexOf($"Apply:Main:{SchemaMigrationCoordinator.MainMigrationId}")
                < runtime.Events.IndexOf($"Apply:Main:{appendedMigrationId}")
        );
        Assert.True(
            runtime.Events.IndexOf($"Record:Main:{SchemaMigrationCoordinator.MainMigrationId}")
                < runtime.Events.IndexOf($"Record:Main:{appendedMigrationId}")
        );
        Assert.Equal(1, runtime.Events.Count(entry => entry == "Acquire:Main"));
    }

    [Fact]
    public void Coordinator_迁移ID长度上限160字符可用()
    {
        var migrationId = new string('m', 160);

        var exception = Record.Exception(() =>
            new SchemaMigrationCoordinator(
                new FakeSchemaMigrationRuntime(),
                NullLogger<SchemaMigrationCoordinator>.Instance,
                [new SchemaMigrationStep(migrationId, static (_, _) => Task.CompletedTask)],
                SchemaMigrationCoordinator.PosmMigrationSteps
            )
        );

        Assert.Null(exception);
    }

    [Fact]
    public void Coordinator_迁移ID超过160字符且仅尾部不同时均在数据库IO前拒绝()
    {
        var sharedPrefix = new string('m', 160);
        var runtime = new FakeSchemaMigrationRuntime();

        var exception = Assert.Throws<ArgumentException>(() =>
            new SchemaMigrationCoordinator(
                runtime,
                NullLogger<SchemaMigrationCoordinator>.Instance,
                [
                    new SchemaMigrationStep(
                        $"{sharedPrefix}a",
                        static (_, _) => Task.CompletedTask
                    ),
                    new SchemaMigrationStep(
                        $"{sharedPrefix}b",
                        static (_, _) => Task.CompletedTask
                    ),
                ],
                SchemaMigrationCoordinator.PosmMigrationSteps
            )
        );

        Assert.Contains("160", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runtime.Events);
    }

    [Fact]
    public async Task Coordinator_Check只做账本与精确签名门禁且不执行迁移器()
    {
        var source = await ReadCoordinatorSourceAsync();
        var checkMethod = ExtractMethod(source, "CheckCoreAsync");
        var runtimeSource = await ReadRuntimeSourceAsync();

        Assert.Contains("CheckLedgerAsync", checkMethod, StringComparison.Ordinal);
        Assert.Contains("VerifyContainerDetailQueryIndexesAsync", checkMethod, StringComparison.Ordinal);
        Assert.Contains("VerifyProductHqSyncOutboxAsync", checkMethod, StringComparison.Ordinal);
        Assert.Contains("VerifyDeviceActivationSchemaAsync", checkMethod, StringComparison.Ordinal);
        Assert.Contains("VerifyMobileDeviceActivationSchemaAsync", checkMethod, StringComparison.Ordinal);
        Assert.Contains("VerifyLinklyMultiTerminalSchemaAsync", checkMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTable", checkMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("StartupSchemaMigrator", checkMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("PaymentTerminalSettingsSchemaMigrator", checkMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceRuntimeStatusSchemaMigrator", checkMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("EmergencyLogin", checkMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("sp_getapplock", checkMethod, StringComparison.Ordinal);

        // 两个账本查询 + 已登记迁移的精确签名 VerifySql，启动门禁不能重新扫描/写入 schema。
        Assert.Equal(2, CountOccurrences(checkMethod, "CheckLedgerAsync"));
        Assert.Equal(1, CountOccurrences(checkMethod, "VerifyContainerDetailQueryIndexesAsync"));
        Assert.Equal(1, CountOccurrences(checkMethod, "VerifyProductHqSyncOutboxAsync"));
        Assert.Equal(1, CountOccurrences(checkMethod, "VerifyDeviceActivationSchemaAsync"));
        Assert.Equal(1, CountOccurrences(checkMethod, "VerifyMobileDeviceActivationSchemaAsync"));
        Assert.Equal(1, CountOccurrences(checkMethod, "VerifyLinklyMultiTerminalSchemaAsync"));
        Assert.Equal(1, CountOccurrences(checkMethod, "VerifyLocalSupplierCategoryAsync"));
        Assert.Equal(1, CountOccurrences(checkMethod, "VerifyHelpFaqAsync"));
        Assert.Equal(1, CountOccurrences(checkMethod, "VerifyAttendanceLocationSampleBatchAsync"));
        Assert.Equal(1, CountOccurrences(checkMethod, "VerifyLegacyEmployeeLogRiskAsync"));
        Assert.Equal(1, CountOccurrences(checkMethod, "VerifyPosOperationAuditRiskAsync"));
        Assert.Contains("PosOperationAuditRiskSchema.VerifySql", runtimeSource, StringComparison.Ordinal);
        Assert.Contains(
            "exception.Number is >= 51970 and <= 51989",
            runtimeSource,
            StringComparison.Ordinal
        );
        Assert.Contains("LegacyEmployeeLogRiskSchema.VerifySql", runtimeSource, StringComparison.Ordinal);
        Assert.Contains(
            "exception.Number is >= 51950 and <= 51969",
            runtimeSource,
            StringComparison.Ordinal
        );
        Assert.Contains("LocalSupplierCategorySchema.VerifySql", runtimeSource, StringComparison.Ordinal);
        Assert.Contains(
            "exception.Number is >= 51930 and <= 51939",
            runtimeSource,
            StringComparison.Ordinal
        );
        Assert.Contains("HelpFaqSchema.VerifySql", runtimeSource, StringComparison.Ordinal);
        Assert.Contains(
            "exception.Number is >= 54200 and <= 54209",
            runtimeSource,
            StringComparison.Ordinal
        );
        Assert.Contains("AttendanceLocationSampleBatchSchema.VerifySql", runtimeSource, StringComparison.Ordinal);
        Assert.Contains(
            "exception.Number is >= 52900 and <= 52909",
            runtimeSource,
            StringComparison.Ordinal
        );
        Assert.Contains("DeviceActivationCodeSchema.VerifySql", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("MobileDeviceActivationSchema.VerifySql", runtimeSource, StringComparison.Ordinal);
        Assert.Contains(
            "ProductHqSyncOutboxSchemaMigrator.SqlServerVerifySql",
            runtimeSource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "PaymentTerminalSettingsSchemaMigrator.LinklyMultiTerminalVerifySql",
            runtimeSource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "exception.Number is >= 51071 and <= 51081",
            runtimeSource,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "exception.Number is >= 51600 and <= 51616",
            runtimeSource,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task MigrateAsync_两个账本已应用时跳过baseline和登记()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        runtime.MarkApplied(SchemaDatabase.Main, SchemaMigrationCoordinator.MainMigrationId);
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.BrowserExtensionSessionGrantMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.ContainerDetailQueryIndexesMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.ContainerDetailCollaborationMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.ProductHqSyncOutboxMigrationId
        );
        runtime.MarkApplied(SchemaDatabase.Main, SchemaMigrationCoordinator.PricingCurveMigrationId);
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.SalesDetailQueryProjectionMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.SalesDetailQueryMappingUseMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.MobileOtaRuntimeTargetsMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.SalesDetailQueryMonthlyMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.LocalSupplierCategoryMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.CompactBoardMonthlyMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.WarehouseOrderPickingMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.WarehouseOrderPickStockoutMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.WarehouseOrderPickAssignmentMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.AttendanceScheduleMealBreakMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.AttendanceAvailabilityUnavailableMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.EmployeeProfileSensitiveBirthdayMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.UserPasswordChangeRequirementMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.EmployeeMinorComplianceMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.UserPasswordResetCodeMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.UserPasswordResetCodeTargetEmailMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.MobileAndroidNativeUpdatePolicyMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.StoreReceiptProfileReleaseMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.AttendanceMealBreakMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.StoreCashManagementMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.StoreReceiptTermsMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.SeasonalCardSupplierBatchMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.AdvertisementOrientationMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.AppNotificationMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.FaceCaptureMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.FaceMatchMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.StorePriceUpdateNoticeMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.ContainerNewProductNoticeMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.AttendanceLocationSampleBatchMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.FaceTrafficHourlyMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.FaceUnwelcomeAutoConfirmMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.AppPushMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.UserLoginRestrictionMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.StorePriceUpdateSettingMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.UserNamePartsMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.WarehouseOrderReturnFieldsMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.SupplierPreorderMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.SupplierPreorderHistoryMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.SupplierPreorderItemImageMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.SupplierPreorderRevisionMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.StorePosApiEntryMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.HelpFaqMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.AttendanceReviewAutoClockOutMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.AttendancePayrollLeaveMigrationId
        );
        runtime.MarkApplied(SchemaDatabase.Posm, SchemaMigrationCoordinator.PosmMigrationId);
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.MobileDeviceActivationMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.LinklyMultiTerminalMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.LegacyEmployeeLogRiskMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.PosOperationAuditRiskMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.PosDeviceApiEntryMigrationId
        );
        var coordinator = CreateCoordinator(runtime);

        var result = await coordinator.MigrateAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SchemaExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain(runtime.Events, entry => entry.StartsWith("Apply:", StringComparison.Ordinal));
        Assert.DoesNotContain(runtime.Events, entry => entry.StartsWith("Record:", StringComparison.Ordinal));
        Assert.Equal(2, runtime.Events.Count(entry => entry.StartsWith("Acquire:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MigrateAsync_旧投影已登记_仍执行并登记映射证明迁移()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps
                     .Where(step => step.MigrationId != SchemaMigrationCoordinator.SalesDetailQueryMappingUseMigrationId))
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);

        var result = await CreateCoordinator(runtime).MigrateAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(
            $"Apply:Main:{SchemaMigrationCoordinator.SalesDetailQueryMappingUseMigrationId}",
            runtime.Events);
        Assert.Contains(
            $"Record:Main:{SchemaMigrationCoordinator.SalesDetailQueryMappingUseMigrationId}",
            runtime.Events);
    }

    [Fact]
    public async Task MigrateAsync_旧主库Baseline已应用_仍执行并记账浏览器扩展Grant迁移()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        runtime.MarkApplied(SchemaDatabase.Main, SchemaMigrationCoordinator.MainMigrationId);
        runtime.MarkApplied(SchemaDatabase.Posm, SchemaMigrationCoordinator.PosmMigrationId);
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.MobileDeviceActivationMigrationId
        );

        var result = await CreateCoordinator(runtime).MigrateAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain(
            $"Apply:Main:{SchemaMigrationCoordinator.MainMigrationId}",
            runtime.Events
        );
        Assert.Contains(
            $"Apply:Main:{SchemaMigrationCoordinator.BrowserExtensionSessionGrantMigrationId}",
            runtime.Events
        );
        Assert.Contains(
            $"Record:Main:{SchemaMigrationCoordinator.BrowserExtensionSessionGrantMigrationId}",
            runtime.Events
        );
    }

    [Fact]
    public async Task MigrateAsync_所有既有主库步骤已记账_仍执行并记账货柜协作迁移()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        runtime.MarkApplied(SchemaDatabase.Main, SchemaMigrationCoordinator.MainMigrationId);
        runtime.MarkApplied(SchemaDatabase.Main, SchemaMigrationCoordinator.BrowserExtensionSessionGrantMigrationId);
        runtime.MarkApplied(SchemaDatabase.Main, SchemaMigrationCoordinator.ContainerDetailQueryIndexesMigrationId);
        runtime.MarkApplied(SchemaDatabase.Posm, SchemaMigrationCoordinator.PosmMigrationId);
        runtime.MarkApplied(SchemaDatabase.Posm, SchemaMigrationCoordinator.MobileDeviceActivationMigrationId);

        var result = await CreateCoordinator(runtime).MigrateAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains($"Apply:Main:{SchemaMigrationCoordinator.ContainerDetailCollaborationMigrationId}", runtime.Events);
        Assert.Contains($"Record:Main:{SchemaMigrationCoordinator.ContainerDetailCollaborationMigrationId}", runtime.Events);
    }

    [Fact]
    public async Task MigrateAsync_所有既有主库步骤已记账_仍执行并记账ProductHqOutbox迁移()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        runtime.MarkApplied(SchemaDatabase.Main, SchemaMigrationCoordinator.MainMigrationId);
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.BrowserExtensionSessionGrantMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.ContainerDetailQueryIndexesMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.ContainerDetailCollaborationMigrationId
        );
        runtime.MarkApplied(SchemaDatabase.Posm, SchemaMigrationCoordinator.PosmMigrationId);
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.MobileDeviceActivationMigrationId
        );

        var result = await CreateCoordinator(runtime).MigrateAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(
            $"Apply:Main:{SchemaMigrationCoordinator.ProductHqSyncOutboxMigrationId}",
            runtime.Events
        );
        Assert.Contains(
            $"Record:Main:{SchemaMigrationCoordinator.ProductHqSyncOutboxMigrationId}",
            runtime.Events
        );
    }

    [Fact]
    public async Task CheckAsync_协作账本已登记但签名缺失_返回稳定不兼容诊断()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            ContainerDetailCollaborationVerifyException =
                new ContainerDetailCollaborationSchemaMismatchException(),
        };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.ContainerDetailCollaborationIncompatible, result.DiagnosticCode);
        Assert.Contains("VerifyContainerDetailCollaboration", runtime.Events);
    }

    [Fact]
    public async Task MigrateAsync_旧PosmBaseline已应用_仍执行并记账Mobile设备绑定迁移()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        runtime.MarkApplied(SchemaDatabase.Main, SchemaMigrationCoordinator.MainMigrationId);
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.BrowserExtensionSessionGrantMigrationId
        );
        runtime.MarkApplied(SchemaDatabase.Posm, SchemaMigrationCoordinator.PosmMigrationId);

        var result = await CreateCoordinator(runtime).MigrateAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain(
            $"Apply:Posm:{SchemaMigrationCoordinator.PosmMigrationId}",
            runtime.Events
        );
        Assert.Contains(
            $"Apply:Posm:{SchemaMigrationCoordinator.MobileDeviceActivationMigrationId}",
            runtime.Events
        );
        Assert.Contains(
            $"Record:Posm:{SchemaMigrationCoordinator.MobileDeviceActivationMigrationId}",
            runtime.Events
        );
    }

    [Fact]
    public async Task MigrateAsync_既有Posm步骤已记账_仍执行并记账Linkly多终端迁移()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
        {
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        }
        runtime.MarkApplied(SchemaDatabase.Posm, SchemaMigrationCoordinator.PosmMigrationId);
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.MobileDeviceActivationMigrationId
        );

        var result = await CreateCoordinator(runtime).MigrateAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain(
            $"Apply:Posm:{SchemaMigrationCoordinator.PosmMigrationId}",
            runtime.Events
        );
        Assert.DoesNotContain(
            $"Apply:Posm:{SchemaMigrationCoordinator.MobileDeviceActivationMigrationId}",
            runtime.Events
        );
        Assert.Contains(
            $"Apply:Posm:{SchemaMigrationCoordinator.LinklyMultiTerminalMigrationId}",
            runtime.Events
        );
        Assert.Contains(
            $"Record:Posm:{SchemaMigrationCoordinator.LinklyMultiTerminalMigrationId}",
            runtime.Events
        );
    }

    [Fact]
    public async Task MigrateAsync_Linkly签名失败不得登记新版本()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            LinklyVerifyException = new LinklyMultiTerminalSchemaMismatchException(),
        };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
        {
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        }
        runtime.MarkApplied(SchemaDatabase.Posm, SchemaMigrationCoordinator.PosmMigrationId);
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.MobileDeviceActivationMigrationId
        );

        var result = await CreateCoordinator(runtime).MigrateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.LinklyMultiTerminalIncompatible, result.DiagnosticCode);
        Assert.Contains(
            $"Apply:Posm:{SchemaMigrationCoordinator.LinklyMultiTerminalMigrationId}",
            runtime.Events
        );
        Assert.Contains("VerifyLinkly", runtime.Events);
        Assert.DoesNotContain(
            $"Record:Posm:{SchemaMigrationCoordinator.LinklyMultiTerminalMigrationId}",
            runtime.Events
        );
    }

    [Fact]
    public async Task CheckAsync_Linkly账本缺失_保留PosmMissing且跳过签名门禁()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            LinklyVerifyException = new LinklyMultiTerminalSchemaMismatchException(),
        };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
        {
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        }
        runtime.MarkApplied(SchemaDatabase.Posm, SchemaMigrationCoordinator.PosmMigrationId);
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.MobileDeviceActivationMigrationId
        );

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.PosmMigrationMissing, result.DiagnosticCode);
        Assert.DoesNotContain("VerifyLinkly", runtime.Events);
    }

    [Fact]
    public async Task CheckAsync_Linkly账本已登记但签名缺失_返回稳定不兼容诊断()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            LinklyVerifyException = new LinklyMultiTerminalSchemaMismatchException(),
        };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
        {
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        }
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps)
        {
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);
        }

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.LinklyMultiTerminalIncompatible, result.DiagnosticCode);
        Assert.Contains("VerifyLinkly", runtime.Events);
    }

    [Fact]
    public async Task MigrateAsync_baseline失败不得写入该数据库账本()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        runtime.ApplyFailures[SchemaDatabase.Main] = new InvalidOperationException("main failed");

        var result = await CreateCoordinator(runtime).MigrateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.DatabaseFailure, result.ExitCode);
        Assert.Contains(
            $"Apply:Main:{SchemaMigrationCoordinator.MainMigrationId}",
            runtime.Events
        );
        Assert.DoesNotContain("Record:Main:20260827.001-hbweb-baseline", runtime.Events);
        Assert.DoesNotContain(runtime.Events, entry => entry.StartsWith("Acquire:Posm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MigrateAsync_POSM中途失败后重跑只继续POSM()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        runtime.ApplyFailures[SchemaDatabase.Posm] = new InvalidOperationException("posm failed");
        var coordinator = CreateCoordinator(runtime);

        var first = await coordinator.MigrateAsync(CancellationToken.None);

        Assert.Equal(SchemaExitCodes.DatabaseFailure, first.ExitCode);
        Assert.Contains("Record:Main:20260827.001-hbweb-baseline", runtime.Events);
        Assert.DoesNotContain("Record:Posm:20260827.001-hbweb-posm-baseline", runtime.Events);

        runtime.ApplyFailures.Remove(SchemaDatabase.Posm);
        runtime.Events.Clear();
        var second = await coordinator.MigrateAsync(CancellationToken.None);

        Assert.True(second.Success);
        Assert.DoesNotContain(
            $"Apply:Main:{SchemaMigrationCoordinator.MainMigrationId}",
            runtime.Events
        );
        Assert.DoesNotContain("Record:Main:20260827.001-hbweb-baseline", runtime.Events);
        Assert.Contains(
            $"Apply:Posm:{SchemaMigrationCoordinator.PosmMigrationId}",
            runtime.Events
        );
        Assert.Contains("Record:Posm:20260827.001-hbweb-posm-baseline", runtime.Events);
    }

    [Fact]
    public async Task MigrateAsync_迁移锁不可用返回23()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            AcquireException = new SchemaMigrationLockUnavailableException(-1),
        };

        var result = await CreateCoordinator(runtime).MigrateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.MigrationLockUnavailable, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.MigrationLockUnavailable, result.DiagnosticCode);
    }

    [Fact]
    public async Task MigrateAsync_数据库异常返回22()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            EnsureProvidersException = new InvalidOperationException("database unavailable"),
        };

        var result = await CreateCoordinator(runtime).MigrateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.DatabaseFailure, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.MigrationFailure, result.DiagnosticCode);
    }

    [Fact]
    public async Task MigrateAsync_取消返回130()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await CreateCoordinator(runtime).MigrateAsync(cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.Cancelled, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.Cancelled, result.DiagnosticCode);
    }

    [Fact]
    public async Task CheckAsync_缺少查询索引账本时保留主库Missing且跳过该签名门禁()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            ContainerDetailIndexesVerifyException =
                new ContainerDetailQueryIndexSchemaMismatchException(),
        };
        runtime.MarkApplied(SchemaDatabase.Main, SchemaMigrationCoordinator.MainMigrationId);
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.BrowserExtensionSessionGrantMigrationId
        );
        runtime.MarkApplied(SchemaDatabase.Posm, SchemaMigrationCoordinator.PosmMigrationId);
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.MobileDeviceActivationMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.LinklyMultiTerminalMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.LegacyEmployeeLogRiskMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.PosOperationAuditRiskMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.PosDeviceApiEntryMigrationId
        );

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.MainMigrationMissing, result.DiagnosticCode);
        Assert.Equal(
            [
                "EnsureProviders",
                "Preflight",
                "Check:Main:20260827.001-hbweb-baseline",
                "Check:Main:20260830.001-browser-extension-session-grant",
                "Check:Main:20260902.001-container-detail-query-indexes",
                "Check:Main:20260903.001-container-detail-collaboration",
                "Check:Main:20260903.001-product-hq-sync-outbox",
                "Check:Main:20260909.001-pricing-curve",
                "Check:Main:20260909.002-sales-detail-query-projection",
                "Check:Main:20260909.003-sales-detail-query-mapping-use",
                "Check:Main:20260921.001-mobile-ota-runtime-targets",
                "Check:Main:20260922.001-sales-detail-query-monthly",
                "Check:Main:20260923.001-local-supplier-category",
                "Check:Main:20260924.001-compact-board-monthly",
                "Check:Main:20260929.001-warehouse-order-picking",
                "Check:Main:20260930.001-warehouse-order-pick-stockout",
                "Check:Main:20260930.002-warehouse-order-pick-assignment",
                "Check:Main:20261002.002-attendance-schedule-meal-break-count",
                "Check:Main:20261003.001-attendance-availability-unavailable",
                "Check:Main:20261004.001-employee-profile-sensitive-birthday",
                "Check:Main:20261004.002-user-password-change-requirement",
                "Check:Main:20261004.003-employee-minor-compliance",
                "Check:Main:20261004.004-user-password-reset-code",
                "Check:Main:20261005.001-user-password-reset-code-target-email",
                "Check:Main:20261005.002-mobile-android-native-update-policy",
                "Check:Main:20261007.001-store-receipt-profile-release",
                "Check:Main:20261007.002-attendance-meal-break",
                "Check:Main:20261008.001-store-cash-management",
                "Check:Main:20261008.002-store-receipt-terms",
                "Check:Main:20261009.001-seasonal-card-supplier-batch",
                "Check:Main:20261009.002-advertisement-orientation",
                "Check:Main:20261012.001-app-notification",
                "Check:Main:20261013.001-face-capture",
                "Check:Main:20261014.001-face-match",
                "Check:Main:20261015.001-store-price-notice",
                "Check:Main:20261015.002-container-new-product-notice",
                "Check:Main:20261015.003-attendance-location-sample-batch",
                "Check:Main:20261016.001-face-traffic-hourly",
                "Check:Main:20261016.002-face-unwelcome-auto-confirm",
                "Check:Main:20261017.001-app-push",
                "Check:Main:20261017.002-user-login-restriction",
                "Check:Main:20261017.003-store-price-update-setting",
                "Check:Main:20261018.001-user-name-parts",
                "Check:Main:20261021.001-warehouse-order-return-fields",
                "Check:Main:20261023.001-supplier-preorder",
                "Check:Main:20261023.002-supplier-preorder-history",
                "Check:Main:20261024.001-supplier-preorder-item-image",
                "Check:Main:20261024.002-supplier-preorder-revision",
                "Check:Main:20261024.003-store-pos-api-entry",
                "Check:Main:20261025.001-help-faq",
                "Check:Main:20261026.001-attendance-review-auto-clock-out",
                "Check:Main:20261026.002-attendance-payroll-leave",
                "Check:Posm:20260827.001-hbweb-posm-baseline",
                "Check:Posm:20260831.001-mobile-device-activation",
                "Check:Posm:20260903.001-linkly-multi-terminal",
                "Check:Posm:20261001.001-legacy-employee-log-risk",
                "Check:Posm:20261002.001-pos-operation-audit-risk",
                "Check:Posm:20261024.004-pos-device-api-entry",
                "VerifyLinkly",
                "VerifyLegacyEmployeeLogRisk",
                "VerifyPosOperationAuditRisk",
                "Verify",
                "VerifyMobile",
            ],
            runtime.Events
        );
        Assert.DoesNotContain("VerifyContainerDetailIndexes", runtime.Events);
        Assert.DoesNotContain(runtime.Events, entry => entry.StartsWith("Acquire:", StringComparison.Ordinal));
        Assert.DoesNotContain(runtime.Events, entry => entry.StartsWith("Apply:", StringComparison.Ordinal));
        Assert.DoesNotContain(runtime.Events, entry => entry.StartsWith("Record:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckAsync_Mobile设备绑定签名漂移返回20()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            MobileVerifyException = new DeviceActivationSchemaMismatchException(),
        };
        runtime.MarkApplied(SchemaDatabase.Main, SchemaMigrationCoordinator.MainMigrationId);
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.BrowserExtensionSessionGrantMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.ContainerDetailQueryIndexesMigrationId
        );
        runtime.MarkApplied(SchemaDatabase.Posm, SchemaMigrationCoordinator.PosmMigrationId);
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.MobileDeviceActivationMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.LinklyMultiTerminalMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.LegacyEmployeeLogRiskMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.PosOperationAuditRiskMigrationId
        );
        runtime.MarkApplied(
            SchemaDatabase.Posm,
            SchemaMigrationCoordinator.PosDeviceApiEntryMigrationId
        );

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.DeviceActivationIncompatible, result.DiagnosticCode);
        Assert.Contains("VerifyMobile", runtime.Events);
    }

    private static SchemaMigrationCoordinator CreateCoordinator(FakeSchemaMigrationRuntime runtime) =>
        new(runtime, NullLogger<SchemaMigrationCoordinator>.Instance);

    private static async Task<string> ReadCoordinatorSourceAsync()
    {
        var sourcePath = SchemaMigrationSourcePath("SchemaMigrationCoordinator.cs");
        Assert.True(File.Exists(sourcePath), $"缺少 schema 协调器源码: {sourcePath}");
        return await File.ReadAllTextAsync(sourcePath);
    }

    private static async Task<string> ReadStoreSourceAsync()
    {
        var sourcePath = SchemaMigrationSourcePath("SqlServerSchemaMigrationStore.cs");
        Assert.True(File.Exists(sourcePath), $"缺少 schema 存储源码: {sourcePath}");
        return await File.ReadAllTextAsync(sourcePath);
    }

    private static async Task<string> ReadRuntimeSourceAsync()
    {
        var sourcePath = SchemaMigrationSourcePath("SchemaMigrationRuntime.cs");
        Assert.True(File.Exists(sourcePath), $"缺少 schema runtime 源码: {sourcePath}");
        return await File.ReadAllTextAsync(sourcePath);
    }

    private static string SchemaMigrationSourcePath(string fileName) =>
        Path.Combine(
            FindRepoRoot(),
            "services/backend/BlazorApp.Api/Data/SchemaMigrations",
            fileName
        );

    private static int IndexOfRequired(string source, string value)
    {
        var index = source.IndexOf(value, StringComparison.Ordinal);
        Assert.True(index >= 0, $"未找到预期迁移契约: {value}");
        return index;
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var match = Regex.Match(
            source,
            $@"\b(?:public|private|internal)\s+(?:async\s+)?Task(?:<[^>]+>)?\s+{Regex.Escape(methodName)}\s*\(",
            RegexOptions.CultureInvariant
        );
        Assert.True(match.Success, $"未找到方法定义: {methodName}");
        var start = match.Index;

        var openBrace = source.IndexOf('{', start);
        Assert.True(openBrace >= 0, $"无法定位方法主体: {methodName}");
        var depth = 0;
        for (var index = openBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}' && --depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"方法主体未闭合: {methodName}");
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var programPath = Path.Combine(
                directory.FullName,
                "services/backend/BlazorApp.Api/Program.cs"
            );
            if (File.Exists(programPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("无法定位 hb-platform 仓库根目录");
    }

    [Fact]
    public async Task CheckAsync_定价曲线签名漂移时阻止启动且不写库()
    {
        var runtime = new FakeSchemaMigrationRuntime { PricingCurveVerifyException = new PricingCurveSchemaMismatchException() };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps) runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps) runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);
        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(SchemaDiagnosticCodes.PricingCurveIncompatible, result.DiagnosticCode);
        Assert.DoesNotContain(runtime.Events, e => e.StartsWith("Apply:") || e.StartsWith("Record:"));
    }

    [Fact]
    public async Task CheckAsync_销售明细投影签名漂移时阻止启动且不写库()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            SalesDetailQueryProjectionVerifyException =
                new SalesDetailQueryProjectionSchemaMismatchException(),
        };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            SchemaDiagnosticCodes.SalesDetailQueryProjectionIncompatible,
            result.DiagnosticCode
        );
        Assert.DoesNotContain(
            runtime.Events,
            entry => entry.StartsWith("Apply:") || entry.StartsWith("Record:")
        );
    }

    [Fact]
    public async Task CheckAsync_供应商分类签名漂移时阻止启动且不写库()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            LocalSupplierCategoryVerifyException = new LocalSupplierCategorySchemaMismatchException(),
        };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(
            SchemaDiagnosticCodes.LocalSupplierCategoryIncompatible,
            result.DiagnosticCode
        );
        Assert.DoesNotContain(
            runtime.Events,
            entry => entry.StartsWith("Apply:") || entry.StartsWith("Record:")
        );
    }

    [Fact]
    public async Task CheckAsync_班中定位批量上传签名漂移时阻止启动且不写库()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            AttendanceLocationSampleBatchVerifyException = new AttendanceLocationSampleBatchSchemaMismatchException(),
        };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(
            SchemaDiagnosticCodes.AttendanceLocationSampleBatchIncompatible,
            result.DiagnosticCode
        );
        Assert.DoesNotContain(
            runtime.Events,
            entry => entry.StartsWith("Apply:") || entry.StartsWith("Record:")
        );
    }

    [Fact]
    public async Task CheckAsync_班中定位批量上传迁移未登记时优先报主库迁移缺失且不校验签名()
    {
        // 账本里缺这一步（生产尚未执行 --schema=migrate）时，门禁报「迁移缺失」而不是签名漂移，
        // 且不会去校验一个尚未迁移的表结构。
        var runtime = new FakeSchemaMigrationRuntime();
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps.Where(step =>
                     step.MigrationId != SchemaMigrationCoordinator.AttendanceLocationSampleBatchMigrationId))
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaDiagnosticCodes.MainMigrationMissing, result.DiagnosticCode);
        Assert.DoesNotContain("VerifyAttendanceLocationSampleBatch", runtime.Events);
    }

    [Fact]
    public async Task MigrateAsync_供应商分类与看板月投影按日期追加在末尾且成功后才登记()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        var coordinator = CreateCoordinator(runtime);

        // 新迁移按日期追加：供应商分类（09-23）、看板月投影（09-24）、订单拣货（09-29），再是拣货货位没货与拣货分配（09-30），
        // 然后是排班用餐次数（10-02）、可上班时间的不能上班类型（10-03）、敏感申请生日列与须改密标记表（10-04），未成年用工合规（10-04），然后是找回密码验证码表（10-04），然后是验证码表的新邮箱列（10-05），然后是 Mobile 安卓原生最低构建号策略表（10-05），然后是门店小票资料下发的两张新表（10-07），然后是考勤用餐休息的两张新表（10-07），然后是分店现金管理的六张新表（10-08），然后是门店小票资料「代金券使用说明 / 分期条款」两列（10-08）、贺卡填报供应商与批次三列（10-09）、客显广告版式三列（10-09），然后是通知平台的两张新表（10-12），然后是人脸抓拍接收的三张新表（10-13），然后是人脸比对的九张新表（10-14），然后是门店价格更新已通知记录表（10-15），然后是新品到店每日推送的两张新表（10-15），然后是班中定位批量上传的两列与过滤唯一索引（10-15），然后是门店分时客流报表表（10-16）与名单命中自动确认阈值列（10-16），然后是 App 系统推送的设备表与发件箱（10-17）、「不允许登录」标记表与价格变更任务页面设置表（10-17），最后是用户姓名拆分的四个新列（10-18）。
        Assert.Equal(
            SchemaMigrationCoordinator.LocalSupplierCategoryMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^40].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.CompactBoardMonthlyMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^39].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.WarehouseOrderPickingMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^38].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.WarehouseOrderPickStockoutMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^37].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.WarehouseOrderPickAssignmentMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^36].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.AttendanceScheduleMealBreakMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^35].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.AttendanceAvailabilityUnavailableMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^34].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.EmployeeProfileSensitiveBirthdayMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^33].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.UserPasswordChangeRequirementMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^32].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.EmployeeMinorComplianceMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^31].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.UserPasswordResetCodeMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^30].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.UserPasswordResetCodeTargetEmailMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^29].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.MobileAndroidNativeUpdatePolicyMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^28].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.StoreReceiptProfileReleaseMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^27].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.AttendanceMealBreakMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^26].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.StoreCashManagementMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^25].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.StoreReceiptTermsMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^24].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.SeasonalCardSupplierBatchMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^23].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.AdvertisementOrientationMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^22].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.AppNotificationMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^21].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.FaceCaptureMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^20].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.FaceMatchMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^19].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.StorePriceUpdateNoticeMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^18].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.ContainerNewProductNoticeMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^17].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.AttendanceLocationSampleBatchMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^16].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.FaceTrafficHourlyMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^15].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.FaceUnwelcomeAutoConfirmMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^14].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.AppPushMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^13].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.UserLoginRestrictionMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^12].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.StorePriceUpdateSettingMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^11].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.UserNamePartsMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^10].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.WarehouseOrderReturnFieldsMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^9].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.SupplierPreorderMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^8].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.SupplierPreorderHistoryMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^7].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.SupplierPreorderItemImageMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^6].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.SupplierPreorderRevisionMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^5].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.StorePosApiEntryMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^4].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.HelpFaqMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^3].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.AttendanceReviewAutoClockOutMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^2].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.AttendancePayrollLeaveMigrationId,
            SchemaMigrationCoordinator.MainMigrationSteps[^1].MigrationId
        );
        Assert.Equal("20261007.001-store-receipt-profile-release", SchemaMigrationCoordinator.StoreReceiptProfileReleaseMigrationId);
        Assert.Equal("20261007.002-attendance-meal-break", SchemaMigrationCoordinator.AttendanceMealBreakMigrationId);
        Assert.Equal("20261008.001-store-cash-management", SchemaMigrationCoordinator.StoreCashManagementMigrationId);
        Assert.Equal("20261008.002-store-receipt-terms", SchemaMigrationCoordinator.StoreReceiptTermsMigrationId);
        Assert.Equal("20261009.001-seasonal-card-supplier-batch", SchemaMigrationCoordinator.SeasonalCardSupplierBatchMigrationId);
        Assert.Equal("20261009.002-advertisement-orientation", SchemaMigrationCoordinator.AdvertisementOrientationMigrationId);
        Assert.Equal("20261012.001-app-notification", SchemaMigrationCoordinator.AppNotificationMigrationId);
        Assert.Equal("20261013.001-face-capture", SchemaMigrationCoordinator.FaceCaptureMigrationId);
        Assert.Equal("20261014.001-face-match", SchemaMigrationCoordinator.FaceMatchMigrationId);
        Assert.Equal("20261015.001-store-price-notice", SchemaMigrationCoordinator.StorePriceUpdateNoticeMigrationId);
        Assert.Equal("20261015.002-container-new-product-notice", SchemaMigrationCoordinator.ContainerNewProductNoticeMigrationId);
        Assert.Equal("20261015.003-attendance-location-sample-batch", SchemaMigrationCoordinator.AttendanceLocationSampleBatchMigrationId);
        Assert.Equal("20261016.001-face-traffic-hourly", SchemaMigrationCoordinator.FaceTrafficHourlyMigrationId);
        Assert.Equal("20261016.002-face-unwelcome-auto-confirm", SchemaMigrationCoordinator.FaceUnwelcomeAutoConfirmMigrationId);
        Assert.Equal("20261017.001-app-push", SchemaMigrationCoordinator.AppPushMigrationId);
        Assert.Equal("20261017.002-user-login-restriction", SchemaMigrationCoordinator.UserLoginRestrictionMigrationId);
        Assert.Equal("20261017.003-store-price-update-setting", SchemaMigrationCoordinator.StorePriceUpdateSettingMigrationId);
        Assert.Equal("20261018.001-user-name-parts", SchemaMigrationCoordinator.UserNamePartsMigrationId);
        Assert.Equal("20261021.001-warehouse-order-return-fields", SchemaMigrationCoordinator.WarehouseOrderReturnFieldsMigrationId);
        Assert.Equal("20261023.001-supplier-preorder", SchemaMigrationCoordinator.SupplierPreorderMigrationId);
        Assert.Equal("20261023.002-supplier-preorder-history", SchemaMigrationCoordinator.SupplierPreorderHistoryMigrationId);
        Assert.Equal("20261024.001-supplier-preorder-item-image", SchemaMigrationCoordinator.SupplierPreorderItemImageMigrationId);
        Assert.Equal("20261024.002-supplier-preorder-revision", SchemaMigrationCoordinator.SupplierPreorderRevisionMigrationId);
        Assert.Equal("20261024.003-store-pos-api-entry", SchemaMigrationCoordinator.StorePosApiEntryMigrationId);
        Assert.Equal("20261025.001-help-faq", SchemaMigrationCoordinator.HelpFaqMigrationId);
        Assert.Equal("20261026.001-attendance-review-auto-clock-out", SchemaMigrationCoordinator.AttendanceReviewAutoClockOutMigrationId);
        Assert.Equal("20261026.002-attendance-payroll-leave", SchemaMigrationCoordinator.AttendancePayrollLeaveMigrationId);
        Assert.Equal("20261024.004-pos-device-api-entry", SchemaMigrationCoordinator.PosDeviceApiEntryMigrationId);
        Assert.True((await coordinator.MigrateAsync(CancellationToken.None)).Success);
        foreach (var migrationId in new[]
        {
            SchemaMigrationCoordinator.LocalSupplierCategoryMigrationId,
            SchemaMigrationCoordinator.CompactBoardMonthlyMigrationId,
            SchemaMigrationCoordinator.WarehouseOrderPickingMigrationId,
            SchemaMigrationCoordinator.WarehouseOrderPickStockoutMigrationId,
            SchemaMigrationCoordinator.WarehouseOrderPickAssignmentMigrationId,
            SchemaMigrationCoordinator.AttendanceScheduleMealBreakMigrationId,
            SchemaMigrationCoordinator.AttendanceAvailabilityUnavailableMigrationId,
            SchemaMigrationCoordinator.EmployeeProfileSensitiveBirthdayMigrationId,
            SchemaMigrationCoordinator.UserPasswordChangeRequirementMigrationId,
            SchemaMigrationCoordinator.EmployeeMinorComplianceMigrationId,
            SchemaMigrationCoordinator.UserPasswordResetCodeMigrationId,
            SchemaMigrationCoordinator.UserPasswordResetCodeTargetEmailMigrationId,
            SchemaMigrationCoordinator.MobileAndroidNativeUpdatePolicyMigrationId,
            SchemaMigrationCoordinator.StoreReceiptProfileReleaseMigrationId,
            SchemaMigrationCoordinator.AttendanceMealBreakMigrationId,
            SchemaMigrationCoordinator.StoreCashManagementMigrationId,
            SchemaMigrationCoordinator.StoreReceiptTermsMigrationId,
            SchemaMigrationCoordinator.SeasonalCardSupplierBatchMigrationId,
            SchemaMigrationCoordinator.AdvertisementOrientationMigrationId,
            SchemaMigrationCoordinator.AppNotificationMigrationId,
            SchemaMigrationCoordinator.FaceCaptureMigrationId,
            SchemaMigrationCoordinator.FaceMatchMigrationId,
            SchemaMigrationCoordinator.StorePriceUpdateNoticeMigrationId,
            SchemaMigrationCoordinator.ContainerNewProductNoticeMigrationId,
            SchemaMigrationCoordinator.AttendanceLocationSampleBatchMigrationId,
            SchemaMigrationCoordinator.FaceTrafficHourlyMigrationId,
            SchemaMigrationCoordinator.FaceUnwelcomeAutoConfirmMigrationId,
            SchemaMigrationCoordinator.AppPushMigrationId,
            SchemaMigrationCoordinator.UserLoginRestrictionMigrationId,
            SchemaMigrationCoordinator.StorePriceUpdateSettingMigrationId,
            SchemaMigrationCoordinator.UserNamePartsMigrationId,
            SchemaMigrationCoordinator.WarehouseOrderReturnFieldsMigrationId,
            SchemaMigrationCoordinator.SupplierPreorderMigrationId,
            SchemaMigrationCoordinator.SupplierPreorderHistoryMigrationId,
            SchemaMigrationCoordinator.SupplierPreorderItemImageMigrationId,
            SchemaMigrationCoordinator.SupplierPreorderRevisionMigrationId,
            SchemaMigrationCoordinator.StorePosApiEntryMigrationId,
            SchemaMigrationCoordinator.HelpFaqMigrationId,
            SchemaMigrationCoordinator.AttendanceReviewAutoClockOutMigrationId,
            SchemaMigrationCoordinator.AttendancePayrollLeaveMigrationId,
        })
        {
            var apply = $"Apply:Main:{migrationId}";
            var record = $"Record:Main:{migrationId}";
            Assert.True(runtime.Events.IndexOf(apply) < runtime.Events.IndexOf(record));
        }
        Assert.Contains("VerifyLocalSupplierCategory", runtime.Events);
        // 用餐次数列 Apply 后必须先过签名校验，协调器才登记账本。
        var mealBreakApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.AttendanceScheduleMealBreakMigrationId}");
        var mealBreakVerify = runtime.Events.IndexOf("VerifyAttendanceScheduleMealBreak", mealBreakApply);
        var mealBreakRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.AttendanceScheduleMealBreakMigrationId}");
        Assert.True(mealBreakApply >= 0 && mealBreakApply < mealBreakVerify && mealBreakVerify < mealBreakRecord);
        // 不能上班类型列同样 Apply → 签名校验 → 登记。
        var unavailableApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.AttendanceAvailabilityUnavailableMigrationId}");
        var unavailableVerify = runtime.Events.IndexOf("VerifyAttendanceAvailabilityUnavailable", unavailableApply);
        var unavailableRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.AttendanceAvailabilityUnavailableMigrationId}");
        Assert.True(unavailableApply >= 0 && unavailableApply < unavailableVerify && unavailableVerify < unavailableRecord);
        // 敏感申请生日列同样 Apply → 签名校验 → 登记。
        var birthdayApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.EmployeeProfileSensitiveBirthdayMigrationId}");
        var birthdayVerify = runtime.Events.IndexOf("VerifyEmployeeProfileSensitiveBirthday", birthdayApply);
        var birthdayRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.EmployeeProfileSensitiveBirthdayMigrationId}");
        Assert.True(birthdayApply >= 0 && birthdayApply < birthdayVerify && birthdayVerify < birthdayRecord);
        // 须改密标记表同样 Apply → 签名校验 → 登记。
        var passwordApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.UserPasswordChangeRequirementMigrationId}");
        var passwordVerify = runtime.Events.IndexOf("VerifyUserPasswordChangeRequirement", passwordApply);
        var passwordRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.UserPasswordChangeRequirementMigrationId}");
        Assert.True(passwordApply >= 0 && passwordApply < passwordVerify && passwordVerify < passwordRecord);
        // 未成年用工六张表同样 Apply → 签名校验 → 登记。
        var minorApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.EmployeeMinorComplianceMigrationId}");
        var minorVerify = runtime.Events.IndexOf("VerifyEmployeeMinorCompliance", minorApply);
        var minorRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.EmployeeMinorComplianceMigrationId}");
        Assert.True(minorApply >= 0 && minorApply < minorVerify && minorVerify < minorRecord);
        // 找回密码验证码表同样 Apply → 签名校验 → 登记。
        var resetApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.UserPasswordResetCodeMigrationId}");
        var resetVerify = runtime.Events.IndexOf("VerifyUserPasswordResetCode", resetApply);
        var resetRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.UserPasswordResetCodeMigrationId}");
        Assert.True(resetApply >= 0 && resetApply < resetVerify && resetVerify < resetRecord);
        // 验证码表新邮箱列同样 Apply → 签名校验 → 登记。
        var targetEmailApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.UserPasswordResetCodeTargetEmailMigrationId}");
        var targetEmailVerify = runtime.Events.IndexOf("VerifyUserPasswordResetCodeTargetEmail", targetEmailApply);
        var targetEmailRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.UserPasswordResetCodeTargetEmailMigrationId}");
        Assert.True(targetEmailApply >= 0 && targetEmailApply < targetEmailVerify && targetEmailVerify < targetEmailRecord);
        // 安卓原生最低构建号策略表同样 Apply → 签名校验 → 登记。
        var androidPolicyApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.MobileAndroidNativeUpdatePolicyMigrationId}");
        var androidPolicyVerify = runtime.Events.IndexOf("VerifyMobileAndroidNativeUpdatePolicy", androidPolicyApply);
        var androidPolicyRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.MobileAndroidNativeUpdatePolicyMigrationId}");
        Assert.True(androidPolicyApply >= 0 && androidPolicyApply < androidPolicyVerify && androidPolicyVerify < androidPolicyRecord);
        // 门店小票资料下发两张新表同样 Apply → 签名校验 → 登记。
        var receiptProfileApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.StoreReceiptProfileReleaseMigrationId}");
        var receiptProfileVerify = runtime.Events.IndexOf("VerifyStoreReceiptProfileRelease", receiptProfileApply);
        var receiptProfileRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.StoreReceiptProfileReleaseMigrationId}");
        Assert.True(receiptProfileApply >= 0 && receiptProfileApply < receiptProfileVerify && receiptProfileVerify < receiptProfileRecord);
        // 考勤用餐休息两张新表（10-07）同样 Apply → 签名校验 → 登记。
        var mealBreakTablesApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.AttendanceMealBreakMigrationId}");
        var mealBreakTablesVerify = runtime.Events.IndexOf("VerifyAttendanceMealBreak", mealBreakTablesApply);
        var mealBreakTablesRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.AttendanceMealBreakMigrationId}");
        Assert.True(mealBreakTablesApply >= 0 && mealBreakTablesApply < mealBreakTablesVerify && mealBreakTablesVerify < mealBreakTablesRecord);
        // 分店现金管理六张新表（10-08）同样 Apply → 签名校验 → 登记。
        var storeCashApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.StoreCashManagementMigrationId}");
        var storeCashVerify = runtime.Events.IndexOf("VerifyStoreCashManagement", storeCashApply);
        var storeCashRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.StoreCashManagementMigrationId}");
        Assert.True(storeCashApply >= 0 && storeCashApply < storeCashVerify && storeCashVerify < storeCashRecord);
        // 门店小票资料新增两列（10-08）同样 Apply → 列签名校验 → 登记。
        var receiptTermsApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.StoreReceiptTermsMigrationId}");
        var receiptTermsVerify = runtime.Events.IndexOf("VerifyStoreReceiptTerms", receiptTermsApply);
        var receiptTermsRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.StoreReceiptTermsMigrationId}");
        Assert.True(receiptTermsApply >= 0 && receiptTermsApply < receiptTermsVerify && receiptTermsVerify < receiptTermsRecord);
        // 贺卡填报供应商与批次三列（10-09）同样 Apply → 列与索引签名校验 → 登记。
        var seasonalCardApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.SeasonalCardSupplierBatchMigrationId}");
        var seasonalCardVerify = runtime.Events.IndexOf("VerifySeasonalCardSupplierBatch", seasonalCardApply);
        var seasonalCardRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.SeasonalCardSupplierBatchMigrationId}");
        Assert.True(seasonalCardApply >= 0 && seasonalCardApply < seasonalCardVerify && seasonalCardVerify < seasonalCardRecord);
        // 客显广告版式三列（10-09）同样 Apply → 列与约束签名校验 → 登记。
        var advertisementOrientationApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.AdvertisementOrientationMigrationId}");
        var advertisementOrientationVerify = runtime.Events.IndexOf(
            "VerifyAdvertisementOrientation", advertisementOrientationApply);
        var advertisementOrientationRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.AdvertisementOrientationMigrationId}");
        Assert.True(advertisementOrientationApply >= 0
            && advertisementOrientationApply < advertisementOrientationVerify
            && advertisementOrientationVerify < advertisementOrientationRecord);
        // 通知平台两张新表（10-12）同样 Apply → 列与索引签名校验 → 登记。
        var appNotificationApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.AppNotificationMigrationId}");
        var appNotificationVerify = runtime.Events.IndexOf("VerifyAppNotification", appNotificationApply);
        var appNotificationRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.AppNotificationMigrationId}");
        Assert.True(appNotificationApply >= 0
            && appNotificationApply < appNotificationVerify
            && appNotificationVerify < appNotificationRecord);
        // 人脸抓拍三张新表（10-13）同样 Apply → 列、外键与索引签名校验 → 登记。
        var faceCaptureApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.FaceCaptureMigrationId}");
        var faceCaptureVerify = runtime.Events.IndexOf("VerifyFaceCapture", faceCaptureApply);
        var faceCaptureRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.FaceCaptureMigrationId}");
        Assert.True(faceCaptureApply >= 0
            && faceCaptureApply < faceCaptureVerify
            && faceCaptureVerify < faceCaptureRecord);
        // 人脸比对九张新表（10-14）同样 Apply → 列、主键与索引签名校验 → 登记。
        var faceMatchApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.FaceMatchMigrationId}");
        var faceMatchVerify = runtime.Events.IndexOf("VerifyFaceMatch", faceMatchApply);
        var faceMatchRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.FaceMatchMigrationId}");
        Assert.True(faceMatchApply >= 0
            && faceMatchApply < faceMatchVerify
            && faceMatchVerify < faceMatchRecord);
        // 门店价格更新已通知记录表（10-15）同样 Apply → 列与主键签名校验 → 登记。
        var storePriceNoticeApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.StorePriceUpdateNoticeMigrationId}");
        var storePriceNoticeVerify = runtime.Events.IndexOf("VerifyStorePriceUpdateNotice", storePriceNoticeApply);
        var storePriceNoticeRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.StorePriceUpdateNoticeMigrationId}");
        Assert.True(storePriceNoticeApply >= 0
            && storePriceNoticeApply < storePriceNoticeVerify
            && storePriceNoticeVerify < storePriceNoticeRecord);

        // 新品到店每日推送两张新表（10-15）同样 Apply → 列与主键签名校验 → 登记。
        var containerNewProductNoticeApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.ContainerNewProductNoticeMigrationId}");
        var containerNewProductNoticeVerify = runtime.Events.IndexOf(
            "VerifyContainerNewProductNotice", containerNewProductNoticeApply);
        var containerNewProductNoticeRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.ContainerNewProductNoticeMigrationId}");
        Assert.True(containerNewProductNoticeApply >= 0
            && containerNewProductNoticeApply < containerNewProductNoticeVerify
            && containerNewProductNoticeVerify < containerNewProductNoticeRecord);

        // 班中定位批量上传（10-15）同样 Apply → 两列与过滤唯一索引签名校验 → 登记。
        var locationBatchApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.AttendanceLocationSampleBatchMigrationId}");
        var locationBatchVerify = runtime.Events.IndexOf(
            "VerifyAttendanceLocationSampleBatch", locationBatchApply);
        var locationBatchRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.AttendanceLocationSampleBatchMigrationId}");
        Assert.True(locationBatchApply >= 0
            && locationBatchApply < locationBatchVerify
            && locationBatchVerify < locationBatchRecord);

        // 门店分时客流报表表（10-16）同样 Apply → 列、主键与日期索引签名校验 → 登记。
        var faceTrafficHourlyApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.FaceTrafficHourlyMigrationId}");
        var faceTrafficHourlyVerify = runtime.Events.IndexOf(
            "VerifyFaceTrafficHourly", faceTrafficHourlyApply);
        var faceTrafficHourlyRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.FaceTrafficHourlyMigrationId}");
        Assert.True(faceTrafficHourlyApply >= 0
            && faceTrafficHourlyApply < faceTrafficHourlyVerify
            && faceTrafficHourlyVerify < faceTrafficHourlyRecord);

        // 名单命中自动确认阈值列（10-16）同样 Apply → 类型/精度/可空性签名校验 → 登记。
        var autoConfirmApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.FaceUnwelcomeAutoConfirmMigrationId}");
        var autoConfirmVerify = runtime.Events.IndexOf(
            "VerifyFaceUnwelcomeAutoConfirm", autoConfirmApply);
        var autoConfirmRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.FaceUnwelcomeAutoConfirmMigrationId}");
        Assert.True(autoConfirmApply >= 0
            && autoConfirmApply < autoConfirmVerify
            && autoConfirmVerify < autoConfirmRecord);

        // App 系统推送设备表与发件箱（10-17）同样 Apply → 列、主键、唯一索引与活动令牌过滤条件签名校验 → 登记。
        var appPushApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.AppPushMigrationId}");
        var appPushVerify = runtime.Events.IndexOf("VerifyAppPush", appPushApply);
        var appPushRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.AppPushMigrationId}");
        Assert.True(appPushApply >= 0
            && appPushApply < appPushVerify
            && appPushVerify < appPushRecord);

        // 「不允许登录」标记表（10-17）：Apply → 列与主键签名校验 → 登记。
        var loginRestrictionApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.UserLoginRestrictionMigrationId}");
        var loginRestrictionVerify = runtime.Events.IndexOf("VerifyUserLoginRestriction", loginRestrictionApply);
        var loginRestrictionRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.UserLoginRestrictionMigrationId}");
        Assert.True(loginRestrictionApply >= 0
            && loginRestrictionApply < loginRestrictionVerify
            && loginRestrictionVerify < loginRestrictionRecord);

        // 价格变更任务页面设置表（10-17）：Apply → 列与主键签名校验 → 登记。
        var priceSettingApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.StorePriceUpdateSettingMigrationId}");
        var priceSettingVerify = runtime.Events.IndexOf("VerifyStorePriceUpdateSetting", priceSettingApply);
        var priceSettingRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.StorePriceUpdateSettingMigrationId}");
        Assert.True(priceSettingApply >= 0
            && priceSettingApply < priceSettingVerify
            && priceSettingVerify < priceSettingRecord);

        // 用户姓名拆分四列（10-18）：Apply → 列类型/长度/可空签名校验 → 登记。
        var userNamePartsApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.UserNamePartsMigrationId}");
        var userNamePartsVerify = runtime.Events.IndexOf("VerifyUserNameParts", userNamePartsApply);
        var userNamePartsRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.UserNamePartsMigrationId}");
        Assert.True(userNamePartsApply >= 0
            && userNamePartsApply < userNamePartsVerify
            && userNamePartsVerify < userNamePartsRecord);

        // 分店订单退货三列（10-21）：Apply → 列类型/长度/可空签名校验 → 登记。
        var returnFieldsApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.WarehouseOrderReturnFieldsMigrationId}");
        var returnFieldsVerify = runtime.Events.IndexOf("VerifyWarehouseOrderReturnFields", returnFieldsApply);
        var returnFieldsRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.WarehouseOrderReturnFieldsMigrationId}");
        Assert.True(returnFieldsApply >= 0
            && returnFieldsApply < returnFieldsVerify
            && returnFieldsVerify < returnFieldsRecord);

        // 供应商节日预订单 10 张表（10-23）：Apply → 列签名与唯一索引校验 → 登记。
        var supplierPreorderApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.SupplierPreorderMigrationId}");
        var supplierPreorderVerify = runtime.Events.IndexOf("VerifySupplierPreorder", supplierPreorderApply);
        var supplierPreorderRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.SupplierPreorderMigrationId}");
        Assert.True(supplierPreorderApply >= 0
            && supplierPreorderApply < supplierPreorderVerify
            && supplierPreorderVerify < supplierPreorderRecord);

        // 供应商节日预订单去年历史快照表（10-23）：Apply → 列签名与唯一索引校验 → 登记。
        var supplierPreorderHistoryApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.SupplierPreorderHistoryMigrationId}");
        var supplierPreorderHistoryVerify = runtime.Events.IndexOf("VerifySupplierPreorderHistory", supplierPreorderHistoryApply);
        var supplierPreorderHistoryRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.SupplierPreorderHistoryMigrationId}");
        Assert.True(supplierPreorderHistoryApply >= 0
            && supplierPreorderHistoryApply < supplierPreorderHistoryVerify
            && supplierPreorderHistoryVerify < supplierPreorderHistoryRecord);

        // 供应商节日预订单新品图片转存 5 列（10-24）：Apply → 列签名校验 → 登记。
        var supplierPreorderImageApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.SupplierPreorderItemImageMigrationId}");
        var supplierPreorderImageVerify = runtime.Events.IndexOf("VerifySupplierPreorderItemImage", supplierPreorderImageApply);
        var supplierPreorderImageRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.SupplierPreorderItemImageMigrationId}");
        Assert.True(supplierPreorderImageApply >= 0
            && supplierPreorderImageApply < supplierPreorderImageVerify
            && supplierPreorderImageVerify < supplierPreorderImageRecord);

        // 供应商节日预订单修订版 7 列（10-24）：Apply → 列签名校验 → 登记。
        var supplierPreorderRevisionApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.SupplierPreorderRevisionMigrationId}");
        var supplierPreorderRevisionVerify = runtime.Events.IndexOf("VerifySupplierPreorderRevision", supplierPreorderRevisionApply);
        var supplierPreorderRevisionRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.SupplierPreorderRevisionMigrationId}");
        Assert.True(supplierPreorderRevisionApply >= 0
            && supplierPreorderRevisionApply < supplierPreorderRevisionVerify
            && supplierPreorderRevisionVerify < supplierPreorderRevisionRecord);

        // 分店收银入口首选 3 列 + CHECK 约束（10-24）：Apply → 签名/约束校验 → 登记。
        var storePosApiEntryApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.StorePosApiEntryMigrationId}");
        var storePosApiEntryVerify = runtime.Events.IndexOf("VerifyStorePosApiEntry", storePosApiEntryApply);
        var storePosApiEntryRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.StorePosApiEntryMigrationId}");
        Assert.True(storePosApiEntryApply >= 0
            && storePosApiEntryApply < storePosApiEntryVerify
            && storePosApiEntryVerify < storePosApiEntryRecord);

        // 使用帮助 FAQ 两张表（10-25）：Apply（建表 + 种子）→ 签名校验 → 登记。
        var helpFaqApply = runtime.Events.IndexOf(
            $"Apply:Main:{SchemaMigrationCoordinator.HelpFaqMigrationId}");
        var helpFaqVerify = runtime.Events.IndexOf("VerifyHelpFaq", helpFaqApply);
        var helpFaqRecord = runtime.Events.IndexOf(
            $"Record:Main:{SchemaMigrationCoordinator.HelpFaqMigrationId}");
        Assert.True(helpFaqApply >= 0
            && helpFaqApply < helpFaqVerify
            && helpFaqVerify < helpFaqRecord);
    }

    [Fact]
    public async Task CheckAsync_使用帮助FAQ签名漂移时阻止启动且不写库()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            HelpFaqVerifyException = new HelpFaqSchemaMismatchException(),
        };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.HelpFaqIncompatible, result.DiagnosticCode);
        Assert.Equal("SCHEMA_HELP_FAQ_INCOMPATIBLE", result.DiagnosticCode);
        Assert.DoesNotContain(
            runtime.Events,
            entry => entry.StartsWith("Apply:") || entry.StartsWith("Record:")
        );
    }

    [Fact]
    public async Task CheckAsync_使用帮助FAQ迁移未登记时优先报主库迁移缺失且不校验签名()
    {
        // 生产尚未执行 --schema=migrate：门禁报「迁移缺失」，不去校验一张尚未建出来的表。
        var runtime = new FakeSchemaMigrationRuntime();
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps.Where(step =>
                     step.MigrationId != SchemaMigrationCoordinator.HelpFaqMigrationId))
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaDiagnosticCodes.MainMigrationMissing, result.DiagnosticCode);
        Assert.DoesNotContain("VerifyHelpFaq", runtime.Events);
    }

    [Fact]
    public async Task MigrateAsync_使用帮助FAQ签名不兼容时返回专用诊断码且不登记账本()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            HelpFaqVerifyException = new HelpFaqSchemaMismatchException(),
        };

        var result = await CreateCoordinator(runtime).MigrateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.HelpFaqIncompatible, result.DiagnosticCode);
        Assert.DoesNotContain(
            $"Record:Main:{SchemaMigrationCoordinator.HelpFaqMigrationId}",
            runtime.Events
        );
    }

    [Fact]
    public void HelpFaqSchema_错误号自成一段且迁移不挂在启动迁移器下()
    {
        // 54200–54209 为 FAQ 专用，不得与其它 Schema 文件冲突。
        Assert.Contains("THROW 54200", HelpFaqSchema.VerifySql, StringComparison.Ordinal);
        Assert.Contains("THROW 54201", HelpFaqSchema.VerifySql, StringComparison.Ordinal);
        Assert.Contains("THROW 54202", HelpFaqSchema.VerifySql, StringComparison.Ordinal);
        Assert.Contains("THROW 54203", HelpFaqSchema.VerifySql, StringComparison.Ordinal);
        Assert.DoesNotContain("THROW 54200", HelpFaqSchema.ApplySql, StringComparison.Ordinal);

        // 建表 SQL 必须幂等：先判存在再建；Code 全局唯一索引、(Tab, Code) 唯一索引与 DraftJson 列都要有。
        Assert.Contains("IF OBJECT_ID(N'dbo.HelpFaqGroup', N'U') IS NULL", HelpFaqSchema.ApplySql, StringComparison.Ordinal);
        Assert.Contains("IF OBJECT_ID(N'dbo.HelpFaqQuestion', N'U') IS NULL", HelpFaqSchema.ApplySql, StringComparison.Ordinal);
        Assert.Contains("UX_HelpFaqQuestion_Code", HelpFaqSchema.ApplySql, StringComparison.Ordinal);
        Assert.Contains("UX_HelpFaqGroup_Tab_Code", HelpFaqSchema.ApplySql, StringComparison.Ordinal);
        Assert.Contains("[DraftJson] nvarchar(max) NULL", HelpFaqSchema.ApplySql, StringComparison.Ordinal);
        Assert.Contains("[Version] int NOT NULL", HelpFaqSchema.ApplySql, StringComparison.Ordinal);

        // 迁移步骤只在 Main 列表里，且不属于 POSM。
        Assert.Contains(
            SchemaMigrationCoordinator.MainMigrationSteps,
            step => step.MigrationId == SchemaMigrationCoordinator.HelpFaqMigrationId
        );
        Assert.DoesNotContain(
            SchemaMigrationCoordinator.PosmMigrationSteps,
            step => step.MigrationId == SchemaMigrationCoordinator.HelpFaqMigrationId
        );
    }

    [Fact]
    public async Task MigrateAsync_设备入口回显迁移追加在POSM末尾且签名通过后才登记()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        var coordinator = CreateCoordinator(runtime);

        // POSM 新迁移按日期追加在新收银审计风险（10-02）之后，是 POSM 链的最后一步。
        Assert.Equal(
            SchemaMigrationCoordinator.PosOperationAuditRiskMigrationId,
            SchemaMigrationCoordinator.PosmMigrationSteps[^2].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.PosDeviceApiEntryMigrationId,
            SchemaMigrationCoordinator.PosmMigrationSteps[^1].MigrationId
        );

        Assert.True((await coordinator.MigrateAsync(CancellationToken.None)).Success);
        var apply = $"Apply:Posm:{SchemaMigrationCoordinator.PosDeviceApiEntryMigrationId}";
        var record = $"Record:Posm:{SchemaMigrationCoordinator.PosDeviceApiEntryMigrationId}";
        var applyIndex = runtime.Events.IndexOf(apply);
        var verifyIndex = runtime.Events.IndexOf("VerifyPosDeviceApiEntry", applyIndex);
        Assert.True(applyIndex >= 0);
        Assert.True(applyIndex < verifyIndex && verifyIndex < runtime.Events.IndexOf(record));

        // 再跑一次：已登记，不再 Apply / Record。
        runtime.Events.Clear();
        Assert.True((await coordinator.MigrateAsync(CancellationToken.None)).Success);
        Assert.DoesNotContain(apply, runtime.Events);
        Assert.DoesNotContain(record, runtime.Events);
    }

    [Fact]
    public async Task MigrateAsync_老系统日志风险迁移追加在POSM末尾且签名通过后才登记()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        var coordinator = CreateCoordinator(runtime);

        // POSM 新迁移按日期追加在 Linkly 多终端（09-03）之后，新收银审计风险（10-02）之前。
        Assert.Equal(
            SchemaMigrationCoordinator.LinklyMultiTerminalMigrationId,
            SchemaMigrationCoordinator.PosmMigrationSteps[^4].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.LegacyEmployeeLogRiskMigrationId,
            SchemaMigrationCoordinator.PosmMigrationSteps[^3].MigrationId
        );

        Assert.True((await coordinator.MigrateAsync(CancellationToken.None)).Success);
        var apply = $"Apply:Posm:{SchemaMigrationCoordinator.LegacyEmployeeLogRiskMigrationId}";
        var record = $"Record:Posm:{SchemaMigrationCoordinator.LegacyEmployeeLogRiskMigrationId}";
        var applyIndex = runtime.Events.IndexOf(apply);
        var verifyIndex = runtime.Events.IndexOf("VerifyLegacyEmployeeLogRisk", applyIndex);
        Assert.True(applyIndex >= 0);
        Assert.True(applyIndex < verifyIndex && verifyIndex < runtime.Events.IndexOf(record));

        runtime.Events.Clear();
        Assert.True((await coordinator.MigrateAsync(CancellationToken.None)).Success);
        Assert.DoesNotContain(apply, runtime.Events);
        Assert.DoesNotContain(record, runtime.Events);
        Assert.Contains("VerifyLegacyEmployeeLogRisk", runtime.Events);
    }

    [Fact]
    public async Task MigrateAsync_老系统日志风险签名失败不得登记新版本()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            LegacyEmployeeLogRiskVerifyException = new LegacyEmployeeLogRiskSchemaMismatchException(),
        };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps
                     .Where(step => step.MigrationId != SchemaMigrationCoordinator.LegacyEmployeeLogRiskMigrationId))
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);

        var result = await CreateCoordinator(runtime).MigrateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.LegacyEmployeeLogRiskIncompatible, result.DiagnosticCode);
        Assert.Contains(
            $"Apply:Posm:{SchemaMigrationCoordinator.LegacyEmployeeLogRiskMigrationId}",
            runtime.Events
        );
        Assert.Contains("VerifyLegacyEmployeeLogRisk", runtime.Events);
        Assert.DoesNotContain(
            $"Record:Posm:{SchemaMigrationCoordinator.LegacyEmployeeLogRiskMigrationId}",
            runtime.Events
        );
    }

    [Fact]
    public async Task CheckAsync_老系统日志风险账本缺失_保留PosmMissing且跳过签名门禁()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            LegacyEmployeeLogRiskVerifyException = new LegacyEmployeeLogRiskSchemaMismatchException(),
        };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps
                     .Where(step => step.MigrationId != SchemaMigrationCoordinator.LegacyEmployeeLogRiskMigrationId))
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.PosmMigrationMissing, result.DiagnosticCode);
        Assert.DoesNotContain("VerifyLegacyEmployeeLogRisk", runtime.Events);
    }

    [Fact]
    public async Task CheckAsync_老系统日志风险账本已登记但签名漂移_返回稳定不兼容诊断且不写库()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            LegacyEmployeeLogRiskVerifyException = new LegacyEmployeeLogRiskSchemaMismatchException(),
        };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.LegacyEmployeeLogRiskIncompatible, result.DiagnosticCode);
        Assert.Contains("VerifyLegacyEmployeeLogRisk", runtime.Events);
        Assert.DoesNotContain(
            runtime.Events,
            entry => entry.StartsWith("Apply:") || entry.StartsWith("Record:")
        );
    }

    [Fact]
    public async Task MigrateAsync_新收银审计风险迁移追加在POSM末尾且签名通过后才登记()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        var coordinator = CreateCoordinator(runtime);

        // POSM 新迁移按日期追加在老系统日志风险（10-01）之后。
        Assert.Equal(
            SchemaMigrationCoordinator.LegacyEmployeeLogRiskMigrationId,
            SchemaMigrationCoordinator.PosmMigrationSteps[^3].MigrationId
        );
        Assert.Equal(
            SchemaMigrationCoordinator.PosOperationAuditRiskMigrationId,
            SchemaMigrationCoordinator.PosmMigrationSteps[^2].MigrationId
        );

        Assert.True((await coordinator.MigrateAsync(CancellationToken.None)).Success);
        var apply = $"Apply:Posm:{SchemaMigrationCoordinator.PosOperationAuditRiskMigrationId}";
        var record = $"Record:Posm:{SchemaMigrationCoordinator.PosOperationAuditRiskMigrationId}";
        var applyIndex = runtime.Events.IndexOf(apply);
        var verifyIndex = runtime.Events.IndexOf("VerifyPosOperationAuditRisk", applyIndex);
        Assert.True(applyIndex >= 0);
        Assert.True(applyIndex < verifyIndex && verifyIndex < runtime.Events.IndexOf(record));

        runtime.Events.Clear();
        Assert.True((await coordinator.MigrateAsync(CancellationToken.None)).Success);
        Assert.DoesNotContain(apply, runtime.Events);
        Assert.DoesNotContain(record, runtime.Events);
        Assert.Contains("VerifyPosOperationAuditRisk", runtime.Events);
    }

    [Fact]
    public async Task MigrateAsync_新收银审计风险签名失败不得登记新版本()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            PosOperationAuditRiskVerifyException = new PosOperationAuditRiskSchemaMismatchException(),
        };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps
                     .Where(step => step.MigrationId != SchemaMigrationCoordinator.PosOperationAuditRiskMigrationId))
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);

        var result = await CreateCoordinator(runtime).MigrateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.PosOperationAuditRiskIncompatible, result.DiagnosticCode);
        Assert.Contains(
            $"Apply:Posm:{SchemaMigrationCoordinator.PosOperationAuditRiskMigrationId}",
            runtime.Events
        );
        Assert.Contains("VerifyPosOperationAuditRisk", runtime.Events);
        Assert.DoesNotContain(
            $"Record:Posm:{SchemaMigrationCoordinator.PosOperationAuditRiskMigrationId}",
            runtime.Events
        );
    }

    [Fact]
    public async Task CheckAsync_新收银审计风险账本缺失_保留PosmMissing且跳过签名门禁()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            PosOperationAuditRiskVerifyException = new PosOperationAuditRiskSchemaMismatchException(),
        };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps
                     .Where(step => step.MigrationId != SchemaMigrationCoordinator.PosOperationAuditRiskMigrationId))
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.PosmMigrationMissing, result.DiagnosticCode);
        Assert.DoesNotContain("VerifyPosOperationAuditRisk", runtime.Events);
    }

    [Fact]
    public async Task CheckAsync_新收银审计风险账本已登记但签名漂移_返回稳定不兼容诊断且不写库()
    {
        var runtime = new FakeSchemaMigrationRuntime
        {
            PosOperationAuditRiskVerifyException = new PosOperationAuditRiskSchemaMismatchException(),
        };
        foreach (var step in SchemaMigrationCoordinator.MainMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Main, step.MigrationId);
        foreach (var step in SchemaMigrationCoordinator.PosmMigrationSteps)
            runtime.MarkApplied(SchemaDatabase.Posm, step.MigrationId);

        var result = await CreateCoordinator(runtime).CheckAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.PosOperationAuditRiskIncompatible, result.DiagnosticCode);
        Assert.Contains("VerifyPosOperationAuditRisk", runtime.Events);
        Assert.DoesNotContain(
            runtime.Events,
            entry => entry.StartsWith("Apply:") || entry.StartsWith("Record:")
        );
    }

    [Fact]
    public async Task MigrateAsync_定价曲线成功后登记且再次执行跳过()
    {
        var runtime = new FakeSchemaMigrationRuntime();
        var coordinator = CreateCoordinator(runtime);
        Assert.True((await coordinator.MigrateAsync(CancellationToken.None)).Success);
        var apply = $"Apply:Main:{SchemaMigrationCoordinator.PricingCurveMigrationId}";
        var record = $"Record:Main:{SchemaMigrationCoordinator.PricingCurveMigrationId}";
        Assert.True(runtime.Events.IndexOf(apply) < runtime.Events.IndexOf(record));
        runtime.Events.Clear();
        Assert.True((await coordinator.MigrateAsync(CancellationToken.None)).Success);
        Assert.DoesNotContain(apply, runtime.Events);
        Assert.DoesNotContain(record, runtime.Events);
        Assert.Contains("VerifyPricingCurve", runtime.Events);
    }

    private sealed class FakeSchemaMigrationRuntime : ISchemaMigrationRuntime
    {
        private readonly HashSet<(SchemaDatabase Database, string MigrationId)> _applied = [];

        public List<string> Events { get; } = [];
        public Dictionary<SchemaDatabase, Exception> ApplyFailures { get; } = [];
        public Exception? EnsureProvidersException { get; init; }
        public Exception? AcquireException { get; init; }
        public Exception? VerifyException { get; init; }
        public Exception? MobileVerifyException { get; init; }
        public Exception? ContainerDetailIndexesVerifyException { get; init; }
        public Exception? ContainerDetailCollaborationVerifyException { get; init; }
        public Exception? PricingCurveVerifyException { get; init; }
        public Exception? SalesDetailQueryProjectionVerifyException { get; init; }
        public Exception? ProductHqOutboxVerifyException { get; init; }
        public Exception? LinklyVerifyException { get; init; }
        public Exception? MobileOtaRuntimeTargetsVerifyException { get; init; }
        public Exception? LocalSupplierCategoryVerifyException { get; init; }
        public Exception? HelpFaqVerifyException { get; init; }
        public Exception? AttendanceLocationSampleBatchVerifyException { get; init; }
        public Exception? LegacyEmployeeLogRiskVerifyException { get; init; }
        public Exception? PosOperationAuditRiskVerifyException { get; init; }

        public void MarkApplied(SchemaDatabase database, string migrationId) =>
            _applied.Add((database, migrationId));

        public void EnsureSupportedProviders()
        {
            Events.Add("EnsureProviders");
            if (EnsureProvidersException is not null)
            {
                throw EnsureProvidersException;
            }
        }

        public Task ValidatePrerequisitesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("Preflight");
            return Task.CompletedTask;
        }

        public Task<ISchemaMigrationSession> AcquireMigrationSessionAsync(
            SchemaDatabase database,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add($"Acquire:{database}");
            if (AcquireException is not null)
            {
                throw AcquireException;
            }

            return Task.FromResult<ISchemaMigrationSession>(new FakeSchemaMigrationSession(this, database));
        }

        public Task<bool> IsMigrationAppliedAsync(
            SchemaDatabase database,
            string migrationId,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add($"Check:{database}:{migrationId}");
            return Task.FromResult(_applied.Contains((database, migrationId)));
        }

        public Task ApplyMainBaselineAsync(CancellationToken cancellationToken) =>
            ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.MainMigrationId,
                cancellationToken
            );

        public Task ApplyBrowserExtensionSessionGrantAsync(
            CancellationToken cancellationToken
        ) => ApplyAsync(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.BrowserExtensionSessionGrantMigrationId,
            cancellationToken
        );

        public Task ApplyContainerDetailQueryIndexesAsync(
            CancellationToken cancellationToken
        ) => ApplyAsync(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.ContainerDetailQueryIndexesMigrationId,
            cancellationToken
        );

        public Task ApplyContainerDetailCollaborationAsync(
            CancellationToken cancellationToken
        ) => ApplyAsync(
            SchemaDatabase.Main,
            SchemaMigrationCoordinator.ContainerDetailCollaborationMigrationId,
            cancellationToken
        );

        public Task ApplyPricingCurveAsync(CancellationToken cancellationToken) =>
            ApplyAsync(SchemaDatabase.Main, SchemaMigrationCoordinator.PricingCurveMigrationId, cancellationToken);

        public Task VerifyPricingCurveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyPricingCurve");
            if (PricingCurveVerifyException is not null) throw PricingCurveVerifyException;
            return Task.CompletedTask;
        }

        public Task ApplySalesDetailQueryProjectionAsync(CancellationToken cancellationToken) =>
            ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.SalesDetailQueryProjectionMigrationId,
                cancellationToken
            );

        public Task ApplySalesDetailQueryMappingUseAsync(CancellationToken cancellationToken) =>
            ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.SalesDetailQueryMappingUseMigrationId,
                cancellationToken
            );

        public Task VerifySalesDetailQueryProjectionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifySalesDetailQueryProjection");
            if (SalesDetailQueryProjectionVerifyException is not null)
                throw SalesDetailQueryProjectionVerifyException;
            return Task.CompletedTask;
        }

        public Task ApplyMobileOtaRuntimeTargetsAsync(CancellationToken cancellationToken) =>
            ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.MobileOtaRuntimeTargetsMigrationId,
                cancellationToken
            );

        public Task ApplySalesDetailQueryMonthlyAsync(CancellationToken cancellationToken) =>
            ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.SalesDetailQueryMonthlyMigrationId,
                cancellationToken
            );

        public Task VerifySalesDetailQueryMonthlyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifySalesDetailQueryMonthly");
            return Task.CompletedTask;
        }

        public Task ApplyCompactBoardMonthlyAsync(CancellationToken cancellationToken) =>
            ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.CompactBoardMonthlyMigrationId,
                cancellationToken
            );

        public Task VerifyCompactBoardMonthlyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyCompactBoardMonthly");
            return Task.CompletedTask;
        }

        public Task ApplyWarehouseOrderPickingAsync(CancellationToken cancellationToken) =>
            ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.WarehouseOrderPickingMigrationId,
                cancellationToken
            );

        public Task VerifyWarehouseOrderPickingAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyWarehouseOrderPicking");
            return Task.CompletedTask;
        }

        public Task ApplyWarehouseOrderPickStockoutAsync(CancellationToken cancellationToken) =>
            ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.WarehouseOrderPickStockoutMigrationId,
                cancellationToken
            );

        public Task VerifyWarehouseOrderPickStockoutAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyWarehouseOrderPickStockout");
            return Task.CompletedTask;
        }

        public Task ApplyWarehouseOrderPickAssignmentAsync(CancellationToken cancellationToken) =>
            ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.WarehouseOrderPickAssignmentMigrationId,
                cancellationToken
            );

        public Task VerifyWarehouseOrderPickAssignmentAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyWarehouseOrderPickAssignment");
            return Task.CompletedTask;
        }

        // 与真实实现一致：Apply 之后立即校验签名，校验通过才由协调器登记。
        public async Task ApplyAttendanceScheduleMealBreakAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.AttendanceScheduleMealBreakMigrationId,
                cancellationToken
            );
            await VerifyAttendanceScheduleMealBreakAsync(cancellationToken);
        }

        public Task VerifyAttendanceScheduleMealBreakAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyAttendanceScheduleMealBreak");
            return Task.CompletedTask;
        }

        public async Task ApplyAttendanceAvailabilityUnavailableAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.AttendanceAvailabilityUnavailableMigrationId,
                cancellationToken
            );
            await VerifyAttendanceAvailabilityUnavailableAsync(cancellationToken);
        }

        public Task VerifyAttendanceAvailabilityUnavailableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyAttendanceAvailabilityUnavailable");
            return Task.CompletedTask;
        }

        public async Task ApplyEmployeeProfileSensitiveBirthdayAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.EmployeeProfileSensitiveBirthdayMigrationId,
                cancellationToken
            );
            await VerifyEmployeeProfileSensitiveBirthdayAsync(cancellationToken);
        }

        public Task VerifyEmployeeProfileSensitiveBirthdayAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyEmployeeProfileSensitiveBirthday");
            return Task.CompletedTask;
        }

        public async Task ApplyUserPasswordChangeRequirementAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.UserPasswordChangeRequirementMigrationId,
                cancellationToken
            );
            await VerifyUserPasswordChangeRequirementAsync(cancellationToken);
        }

        public Task VerifyUserPasswordChangeRequirementAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyUserPasswordChangeRequirement");
            return Task.CompletedTask;
        }

        public async Task ApplyUserPasswordResetCodeAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.UserPasswordResetCodeMigrationId,
                cancellationToken
            );
            await VerifyUserPasswordResetCodeAsync(cancellationToken);
        }

        public Task VerifyUserPasswordResetCodeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyUserPasswordResetCode");
            return Task.CompletedTask;
        }

        public async Task ApplyUserPasswordResetCodeTargetEmailAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.UserPasswordResetCodeTargetEmailMigrationId,
                cancellationToken
            );
            await VerifyUserPasswordResetCodeTargetEmailAsync(cancellationToken);
        }

        public Task VerifyUserPasswordResetCodeTargetEmailAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyUserPasswordResetCodeTargetEmail");
            return Task.CompletedTask;
        }

        public async Task ApplyMobileAndroidNativeUpdatePolicyAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.MobileAndroidNativeUpdatePolicyMigrationId,
                cancellationToken
            );
            await VerifyMobileAndroidNativeUpdatePolicyAsync(cancellationToken);
        }

        public Task VerifyMobileAndroidNativeUpdatePolicyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyMobileAndroidNativeUpdatePolicy");
            return Task.CompletedTask;
        }

        public async Task ApplyStoreReceiptProfileReleaseAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.StoreReceiptProfileReleaseMigrationId,
                cancellationToken
            );
            await VerifyStoreReceiptProfileReleaseAsync(cancellationToken);
        }

        public Task VerifyStoreReceiptProfileReleaseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyStoreReceiptProfileRelease");
            return Task.CompletedTask;
        }

        public async Task ApplyAttendanceMealBreakAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.AttendanceMealBreakMigrationId,
                cancellationToken
            );
            await VerifyAttendanceMealBreakAsync(cancellationToken);
        }

        public Task VerifyAttendanceMealBreakAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyAttendanceMealBreak");
            return Task.CompletedTask;
        }

        public async Task ApplyStoreCashManagementAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.StoreCashManagementMigrationId,
                cancellationToken
            );
            await VerifyStoreCashManagementAsync(cancellationToken);
        }

        public Task VerifyStoreCashManagementAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyStoreCashManagement");
            return Task.CompletedTask;
        }

        public async Task ApplyStoreReceiptTermsAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.StoreReceiptTermsMigrationId,
                cancellationToken
            );
            await VerifyStoreReceiptTermsAsync(cancellationToken);
        }

        public Task VerifyStoreReceiptTermsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyStoreReceiptTerms");
            return Task.CompletedTask;
        }

        public async Task ApplySeasonalCardSupplierBatchAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.SeasonalCardSupplierBatchMigrationId,
                cancellationToken
            );
            await VerifySeasonalCardSupplierBatchAsync(cancellationToken);
        }

        public Task VerifySeasonalCardSupplierBatchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifySeasonalCardSupplierBatch");
            return Task.CompletedTask;
        }

        public async Task ApplyAdvertisementOrientationAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.AdvertisementOrientationMigrationId,
                cancellationToken
            );
            await VerifyAdvertisementOrientationAsync(cancellationToken);
        }

        public Task VerifyAdvertisementOrientationAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyAdvertisementOrientation");
            return Task.CompletedTask;
        }

        public async Task ApplyAppNotificationAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.AppNotificationMigrationId,
                cancellationToken
            );
            await VerifyAppNotificationAsync(cancellationToken);
        }

        public Task VerifyAppNotificationAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyAppNotification");
            return Task.CompletedTask;
        }

        public async Task ApplyFaceCaptureAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.FaceCaptureMigrationId,
                cancellationToken
            );
            await VerifyFaceCaptureAsync(cancellationToken);
        }

        public Task VerifyFaceCaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyFaceCapture");
            return Task.CompletedTask;
        }

        public async Task ApplyFaceMatchAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.FaceMatchMigrationId,
                cancellationToken
            );
            await VerifyFaceMatchAsync(cancellationToken);
        }

        public Task VerifyFaceMatchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyFaceMatch");
            return Task.CompletedTask;
        }

        public async Task ApplyStorePriceUpdateNoticeAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.StorePriceUpdateNoticeMigrationId,
                cancellationToken
            );
            await VerifyStorePriceUpdateNoticeAsync(cancellationToken);
        }

        public Task VerifyStorePriceUpdateNoticeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyStorePriceUpdateNotice");
            return Task.CompletedTask;
        }

        public async Task ApplyContainerNewProductNoticeAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.ContainerNewProductNoticeMigrationId,
                cancellationToken
            );
            await VerifyContainerNewProductNoticeAsync(cancellationToken);
        }

        public Task VerifyContainerNewProductNoticeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyContainerNewProductNotice");
            return Task.CompletedTask;
        }

        public async Task ApplyAttendanceLocationSampleBatchAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.AttendanceLocationSampleBatchMigrationId,
                cancellationToken
            );
            await VerifyAttendanceLocationSampleBatchAsync(cancellationToken);
        }

        public Task VerifyAttendanceLocationSampleBatchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyAttendanceLocationSampleBatch");
            if (AttendanceLocationSampleBatchVerifyException is not null)
            {
                throw AttendanceLocationSampleBatchVerifyException;
            }

            return Task.CompletedTask;
        }

        public async Task ApplyFaceTrafficHourlyAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.FaceTrafficHourlyMigrationId,
                cancellationToken
            );
            await VerifyFaceTrafficHourlyAsync(cancellationToken);
        }

        public Task VerifyFaceTrafficHourlyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyFaceTrafficHourly");
            return Task.CompletedTask;
        }

        public async Task ApplyFaceUnwelcomeAutoConfirmAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.FaceUnwelcomeAutoConfirmMigrationId,
                cancellationToken
            );
            await VerifyFaceUnwelcomeAutoConfirmAsync(cancellationToken);
        }

        public Task VerifyFaceUnwelcomeAutoConfirmAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyFaceUnwelcomeAutoConfirm");
            return Task.CompletedTask;
        }

        public async Task ApplyAppPushAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.AppPushMigrationId,
                cancellationToken
            );
            await VerifyAppPushAsync(cancellationToken);
        }

        public Task VerifyAppPushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyAppPush");
            return Task.CompletedTask;
        }

        public async Task ApplyUserLoginRestrictionAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.UserLoginRestrictionMigrationId,
                cancellationToken
            );
            await VerifyUserLoginRestrictionAsync(cancellationToken);
        }

        public Task VerifyUserLoginRestrictionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyUserLoginRestriction");
            return Task.CompletedTask;
        }

        public async Task ApplyStorePriceUpdateSettingAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.StorePriceUpdateSettingMigrationId,
                cancellationToken
            );
            await VerifyStorePriceUpdateSettingAsync(cancellationToken);
        }

        public Task VerifyStorePriceUpdateSettingAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyStorePriceUpdateSetting");
            return Task.CompletedTask;
        }

        public async Task ApplyUserNamePartsAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.UserNamePartsMigrationId,
                cancellationToken
            );
            await VerifyUserNamePartsAsync(cancellationToken);
        }

        public Task VerifyUserNamePartsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyUserNameParts");
            return Task.CompletedTask;
        }

        public async Task ApplyWarehouseOrderReturnFieldsAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.WarehouseOrderReturnFieldsMigrationId,
                cancellationToken
            );
            await VerifyWarehouseOrderReturnFieldsAsync(cancellationToken);
        }

        public Task VerifyWarehouseOrderReturnFieldsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyWarehouseOrderReturnFields");
            return Task.CompletedTask;
        }

        public async Task ApplySupplierPreorderAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.SupplierPreorderMigrationId,
                cancellationToken
            );
            await VerifySupplierPreorderAsync(cancellationToken);
        }

        public Task VerifySupplierPreorderAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifySupplierPreorder");
            return Task.CompletedTask;
        }

        public async Task ApplySupplierPreorderHistoryAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.SupplierPreorderHistoryMigrationId,
                cancellationToken
            );
            await VerifySupplierPreorderHistoryAsync(cancellationToken);
        }

        public Task VerifySupplierPreorderHistoryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifySupplierPreorderHistory");
            return Task.CompletedTask;
        }

        public async Task ApplySupplierPreorderItemImageAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.SupplierPreorderItemImageMigrationId,
                cancellationToken
            );
            await VerifySupplierPreorderItemImageAsync(cancellationToken);
        }

        public Task VerifySupplierPreorderItemImageAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifySupplierPreorderItemImage");
            return Task.CompletedTask;
        }

        public async Task ApplySupplierPreorderRevisionAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.SupplierPreorderRevisionMigrationId,
                cancellationToken
            );
            await VerifySupplierPreorderRevisionAsync(cancellationToken);
        }

        public Task VerifySupplierPreorderRevisionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifySupplierPreorderRevision");
            return Task.CompletedTask;
        }

        public async Task ApplyStorePosApiEntryAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.StorePosApiEntryMigrationId,
                cancellationToken
            );
            await VerifyStorePosApiEntryAsync(cancellationToken);
        }

        public Task VerifyStorePosApiEntryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyStorePosApiEntry");
            return Task.CompletedTask;
        }

        public async Task ApplyHelpFaqAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.HelpFaqMigrationId,
                cancellationToken
            );
            await VerifyHelpFaqAsync(cancellationToken);
        }

        public Task VerifyHelpFaqAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyHelpFaq");
            if (HelpFaqVerifyException is not null)
            {
                throw HelpFaqVerifyException;
            }

            return Task.CompletedTask;
        }

        public async Task ApplyAttendanceReviewAutoClockOutAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.AttendanceReviewAutoClockOutMigrationId,
                cancellationToken
            );
            await VerifyAttendanceReviewAutoClockOutAsync(cancellationToken);
        }

        public Task VerifyAttendanceReviewAutoClockOutAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyAttendanceReviewAutoClockOut");
            return Task.CompletedTask;
        }

        public async Task ApplyAttendancePayrollLeaveAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.AttendancePayrollLeaveMigrationId,
                cancellationToken
            );
            await VerifyAttendancePayrollLeaveAsync(cancellationToken);
        }

        public Task VerifyAttendancePayrollLeaveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyAttendancePayrollLeave");
            return Task.CompletedTask;
        }

        public async Task ApplyPosDeviceApiEntryAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Posm,
                SchemaMigrationCoordinator.PosDeviceApiEntryMigrationId,
                cancellationToken
            );
            await VerifyPosDeviceApiEntryAsync(cancellationToken);
        }

        public Task VerifyPosDeviceApiEntryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyPosDeviceApiEntry");
            return Task.CompletedTask;
        }

        public async Task ApplyEmployeeMinorComplianceAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.EmployeeMinorComplianceMigrationId,
                cancellationToken
            );
            await VerifyEmployeeMinorComplianceAsync(cancellationToken);
        }

        public Task VerifyEmployeeMinorComplianceAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyEmployeeMinorCompliance");
            return Task.CompletedTask;
        }

        public Task VerifyMobileOtaRuntimeTargetsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyMobileOtaRuntimeTargets");
            if (MobileOtaRuntimeTargetsVerifyException is not null)
            {
                throw MobileOtaRuntimeTargetsVerifyException;
            }

            return Task.CompletedTask;
        }

        public Task ApplyLocalSupplierCategoryAsync(CancellationToken cancellationToken) =>
            ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.LocalSupplierCategoryMigrationId,
                cancellationToken
            );

        public Task VerifyLocalSupplierCategoryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyLocalSupplierCategory");
            if (LocalSupplierCategoryVerifyException is not null)
            {
                throw LocalSupplierCategoryVerifyException;
            }

            return Task.CompletedTask;
        }

        public Task ApplyProductHqSyncOutboxAsync(CancellationToken cancellationToken) =>
            ApplyAsync(
                SchemaDatabase.Main,
                SchemaMigrationCoordinator.ProductHqSyncOutboxMigrationId,
                cancellationToken
            );

        public Task VerifyContainerDetailCollaborationAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyContainerDetailCollaboration");
            if (ContainerDetailCollaborationVerifyException is not null)
            {
                throw ContainerDetailCollaborationVerifyException;
            }
            return Task.CompletedTask;
        }

        public Task ApplyPosmBaselineAsync(CancellationToken cancellationToken) =>
            ApplyAsync(
                SchemaDatabase.Posm,
                SchemaMigrationCoordinator.PosmMigrationId,
                cancellationToken
            );

        public Task ApplyMobileDeviceActivationAsync(CancellationToken cancellationToken) =>
            ApplyAsync(
                SchemaDatabase.Posm,
                SchemaMigrationCoordinator.MobileDeviceActivationMigrationId,
                cancellationToken
            );

        public async Task ApplyLinklyMultiTerminalAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Posm,
                SchemaMigrationCoordinator.LinklyMultiTerminalMigrationId,
                cancellationToken
            );
            await VerifyLinklyMultiTerminalSchemaAsync(cancellationToken);
        }

        // 与真实 runtime 一致：建表后立刻核验签名，失败时协调器不得登记账本。
        public async Task ApplyLegacyEmployeeLogRiskAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Posm,
                SchemaMigrationCoordinator.LegacyEmployeeLogRiskMigrationId,
                cancellationToken
            );
            await VerifyLegacyEmployeeLogRiskAsync(cancellationToken);
        }

        public Task VerifyLegacyEmployeeLogRiskAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyLegacyEmployeeLogRisk");
            if (LegacyEmployeeLogRiskVerifyException is not null)
            {
                throw LegacyEmployeeLogRiskVerifyException;
            }

            return Task.CompletedTask;
        }

        // 与真实 runtime 一致：建表后立刻核验签名，失败时协调器不得登记账本。
        public async Task ApplyPosOperationAuditRiskAsync(CancellationToken cancellationToken)
        {
            await ApplyAsync(
                SchemaDatabase.Posm,
                SchemaMigrationCoordinator.PosOperationAuditRiskMigrationId,
                cancellationToken
            );
            await VerifyPosOperationAuditRiskAsync(cancellationToken);
        }

        public Task VerifyPosOperationAuditRiskAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyPosOperationAuditRisk");
            if (PosOperationAuditRiskVerifyException is not null)
            {
                throw PosOperationAuditRiskVerifyException;
            }

            return Task.CompletedTask;
        }

        public Task ApplyMainAppendAsync(
            string migrationId,
            CancellationToken cancellationToken
        ) => ApplyAsync(SchemaDatabase.Main, migrationId, cancellationToken);

        private Task ApplyAsync(
            SchemaDatabase database,
            string migrationId,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add($"Apply:{database}:{migrationId}");
            if (ApplyFailures.TryGetValue(database, out var exception))
            {
                throw exception;
            }

            return Task.CompletedTask;
        }

        public Task VerifyDeviceActivationSchemaAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("Verify");
            if (VerifyException is not null)
            {
                throw VerifyException;
            }

            return Task.CompletedTask;
        }

        public Task VerifyContainerDetailQueryIndexesAsync(
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyContainerDetailIndexes");
            if (ContainerDetailIndexesVerifyException is not null)
            {
                throw ContainerDetailIndexesVerifyException;
            }

            return Task.CompletedTask;
        }

        public Task VerifyProductHqSyncOutboxAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyProductHqOutbox");
            if (ProductHqOutboxVerifyException is not null)
            {
                throw ProductHqOutboxVerifyException;
            }

            return Task.CompletedTask;
        }

        public Task VerifyMobileDeviceActivationSchemaAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyMobile");
            if (MobileVerifyException is not null)
            {
                throw MobileVerifyException;
            }

            return Task.CompletedTask;
        }

        public Task VerifyLinklyMultiTerminalSchemaAsync(
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("VerifyLinkly");
            if (LinklyVerifyException is not null)
            {
                throw LinklyVerifyException;
            }

            return Task.CompletedTask;
        }

        private sealed class FakeSchemaMigrationSession(
            FakeSchemaMigrationRuntime runtime,
            SchemaDatabase database
        ) : ISchemaMigrationSession
        {
            public Task EnsureHistoryTableAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                runtime.Events.Add($"EnsureHistory:{database}");
                return Task.CompletedTask;
            }

            public Task<bool> IsAppliedAsync(string migrationId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                runtime.Events.Add($"SessionCheck:{database}:{migrationId}");
                return Task.FromResult(runtime._applied.Contains((database, migrationId)));
            }

            public Task RecordAppliedAsync(string migrationId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                runtime.Events.Add($"Record:{database}:{migrationId}");
                runtime._applied.Add((database, migrationId));
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
