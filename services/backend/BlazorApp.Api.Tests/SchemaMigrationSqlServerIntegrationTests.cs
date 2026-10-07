using BlazorApp.Api.Data;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.StoreReceiptProfiles;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 显式迁移的真实 SQL Server 回归测试。
///
/// 只接受专用测试连接，且每个用例均创建 GUID 命名的两个隔离数据库；绝不读取
/// appsettings 或现有 HBWeb 数据库连接。默认环境未配置时在发现阶段跳过。
/// </summary>
public sealed class SchemaMigrationSqlServerFactAttribute : FactAttribute
{
    private const string ConnectionEnvironmentVariable =
        "HBWEB_SCHEMA_SQLSERVER_TEST_CONNECTION";

    public SchemaMigrationSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过完全隔离的 HBWeb schema SQL Server 集成测试。";
        }
    }
}

[CollectionDefinition(nameof(SchemaMigrationSqlServerCollection), DisableParallelization = true)]
public sealed class SchemaMigrationSqlServerCollection { }

[Collection(nameof(SchemaMigrationSqlServerCollection))]
[Trait("Category", "SQL")]
public sealed class SchemaMigrationSqlServerIntegrationTests
{
    private const string ConnectionEnvironmentVariable =
        "HBWEB_SCHEMA_SQLSERVER_TEST_CONNECTION";

    [SchemaMigrationSqlServerFact]
    public async Task 定价曲线新增列_保留旧数据且可重复执行并识别签名漂移()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        await ExecuteNonQueryAsync(databases.MainConnectionString,
            "CREATE TABLE dbo.PricingStrategyDetail (Id int NOT NULL); INSERT dbo.PricingStrategyDetail VALUES (1);");
        await ExecuteNonQueryAsync(databases.MainConnectionString, PricingCurveSchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, PricingCurveSchema.VerifySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, PricingCurveSchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
IF (SELECT COUNT(*) FROM dbo.PricingStrategyDetail WHERE Id = 1 AND StartRetailPrice IS NULL AND EndRetailPrice IS NULL AND CurveBend IS NULL) <> 1
    THROW 51712, 'Existing row was changed.', 1;
ALTER TABLE dbo.PricingStrategyDetail ALTER COLUMN CurveBend decimal(18,5) NULL;
""");
        var mismatch = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, PricingCurveSchema.VerifySql));
        Assert.Equal(51711, mismatch.Number);
        var repeated = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, PricingCurveSchema.ApplySql));
        Assert.Equal(51711, repeated.Number);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 定价曲线已有错误列_迁移在新增其他列之前拒绝()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        await ExecuteNonQueryAsync(databases.MainConnectionString,
            "CREATE TABLE dbo.PricingStrategyDetail (Id int NOT NULL, CurveBend int NULL);");
        var mismatch = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, PricingCurveSchema.ApplySql));
        Assert.Equal(51711, mismatch.Number);
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
IF COL_LENGTH(N'dbo.PricingStrategyDetail', N'StartRetailPrice') IS NOT NULL
    OR COL_LENGTH(N'dbo.PricingStrategyDetail', N'EndRetailPrice') IS NOT NULL
    THROW 51712, 'Preflight allowed a partial migration.', 1;
""");
    }

    [SchemaMigrationSqlServerFact]
    public async Task 空隔离库_迁移检查并重复迁移_两个账本均保持正确()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        var coordinator = databases.CreateCoordinator();

        var emptyDatabaseCheck = await coordinator.CheckAsync(CancellationToken.None);
        Assert.False(emptyDatabaseCheck.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, emptyDatabaseCheck.ExitCode);

        var migrated = await coordinator.MigrateAsync(CancellationToken.None);
        Assert.True(migrated.Success, migrated.DiagnosticCode);
        Assert.Equal(SchemaExitCodes.Success, migrated.ExitCode);

        var checkedResult = await coordinator.CheckAsync(CancellationToken.None);
        Assert.True(checkedResult.Success);
        Assert.Equal(SchemaExitCodes.Success, checkedResult.ExitCode);

        var repeatedMigration = await coordinator.MigrateAsync(CancellationToken.None);
        Assert.True(repeatedMigration.Success);
        Assert.Equal(SchemaExitCodes.Success, repeatedMigration.ExitCode);

        await AssertHistoryTableAsync(
            databases.MainConnectionString,
            SqlServerSchemaMigrationRuntime.MainHistoryTable,
            SchemaMigrationCoordinator.MainMigrationId
        );
        await AssertHistoryTableAsync(
            databases.MainConnectionString,
            SqlServerSchemaMigrationRuntime.MainHistoryTable,
            SchemaMigrationCoordinator.BrowserExtensionSessionGrantMigrationId
        );
        await AssertHistoryTableAsync(
            databases.PosmConnectionString,
            SqlServerSchemaMigrationRuntime.PosmHistoryTable,
            SchemaMigrationCoordinator.PosmMigrationId
        );
    }

    [SchemaMigrationSqlServerFact]
    public async Task 主库未启用SnapshotIsolation_迁移退出22且不登记Baseline()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync(
            enableMainSnapshotIsolation: false
        );

        var result = await databases.CreateCoordinator().MigrateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.DatabaseFailure, result.ExitCode);
        Assert.False(
            await TableExistsAsync(
                databases.MainConnectionString,
                SqlServerSchemaMigrationRuntime.MainHistoryTable
            )
        );
        Assert.False(
            await TableExistsAsync(
                databases.MainConnectionString,
                "WarehouseProductChangeHistory"
            )
        );
    }

    [SchemaMigrationSqlServerFact]
    public async Task 已登记Baseline后_生产Runtime仍可执行并登记后续版本步骤()
    {
        const string appendedMigrationId = "20260828.002-main-versioned-step-contract";
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        Assert.True((await databases.CreateCoordinator().MigrateAsync(CancellationToken.None)).Success);

        var coordinator = new SchemaMigrationCoordinator(
            databases.CreateRuntime(),
            NullLogger<SchemaMigrationCoordinator>.Instance,
            [
                SchemaMigrationCoordinator.MainMigrationSteps[0],
                new SchemaMigrationStep(
                    appendedMigrationId,
                    static (runtime, cancellationToken) =>
                        runtime.ApplyMainBaselineAsync(cancellationToken)
                ),
            ],
            SchemaMigrationCoordinator.PosmMigrationSteps
        );

        var appended = await coordinator.MigrateAsync(CancellationToken.None);

        Assert.True(appended.Success);
        Assert.True(
            await IsMigrationAppliedAsync(
                databases.MainConnectionString,
                SqlServerSchemaMigrationRuntime.MainHistoryTable,
                appendedMigrationId
            )
        );
        Assert.True((await coordinator.CheckAsync(CancellationToken.None)).Success);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 旧POSM账本缺少Linkly步骤_迁移只补齐多终端结构并登记新版本()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        var coordinator = databases.CreateCoordinator();
        Assert.True((await coordinator.MigrateAsync(CancellationToken.None)).Success);

        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            $"""
            DELETE FROM [dbo].[{SqlServerSchemaMigrationRuntime.PosmHistoryTable}]
            WHERE [MigrationId] = N'{SchemaMigrationCoordinator.LinklyMultiTerminalMigrationId}';

            DROP TABLE [dbo].[POSM_LinklyCloudDeviceSelection];
            DROP TABLE [dbo].[POSM_LinklyCloudConfigurationMode];
            DROP TABLE [dbo].[POSM_LinklyCloudTerminal];
            DROP INDEX [UX_POSM_LinklyCloudBackendSession_ActiveCloudTerminal]
                ON [dbo].[POSM_LinklyCloudBackendSession];
            DROP INDEX [IX_POSM_LinklyCloudBackendSession_TerminalRecovery]
                ON [dbo].[POSM_LinklyCloudBackendSession];
            ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                DROP COLUMN [TerminalId];
            """
        );

        var missing = await coordinator.CheckAsync(CancellationToken.None);
        Assert.False(missing.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, missing.ExitCode);
        Assert.Equal(SchemaDiagnosticCodes.PosmMigrationMissing, missing.DiagnosticCode);

        var migrated = await coordinator.MigrateAsync(CancellationToken.None);

        Assert.True(migrated.Success, migrated.DiagnosticCode);
        Assert.True(
            await IsMigrationAppliedAsync(
                databases.PosmConnectionString,
                SqlServerSchemaMigrationRuntime.PosmHistoryTable,
                SchemaMigrationCoordinator.LinklyMultiTerminalMigrationId
            )
        );
        Assert.True(
            await TableExistsAsync(databases.PosmConnectionString, "POSM_LinklyCloudTerminal")
        );
        Assert.True(
            await TableExistsAsync(
                databases.PosmConnectionString,
                "POSM_LinklyCloudDeviceSelection"
            )
        );
        Assert.True(
            await TableExistsAsync(
                databases.PosmConnectionString,
                "POSM_LinklyCloudConfigurationMode"
            )
        );
        Assert.True(
            await ColumnExistsAsync(
                databases.PosmConnectionString,
                "POSM_LinklyCloudBackendSession",
                "TerminalId"
            )
        );
        Assert.True(
            await IndexExistsAsync(
                databases.PosmConnectionString,
                "POSM_LinklyCloudBackendSession",
                "UX_POSM_LinklyCloudBackendSession_ActiveCloudTerminal"
            )
        );
        Assert.True((await coordinator.CheckAsync(CancellationToken.None)).Success);
    }

    [SchemaMigrationSqlServerFact]
    public async Task Linkly同名错误Check或Cascade外键_检查返回稳定不兼容诊断()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        var coordinator = databases.CreateCoordinator();
        Assert.True((await coordinator.MigrateAsync(CancellationToken.None)).Success);

        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            """
            ALTER TABLE [dbo].[POSM_LinklyCloudConfigurationMode]
                DROP CONSTRAINT [CK_POSM_LinklyCloudConfigurationMode_Mode];
            ALTER TABLE [dbo].[POSM_LinklyCloudConfigurationMode] WITH CHECK
                ADD CONSTRAINT [CK_POSM_LinklyCloudConfigurationMode_Mode]
                CHECK ([Mode] IN (N'Legacy'));
            ALTER TABLE [dbo].[POSM_LinklyCloudConfigurationMode]
                CHECK CONSTRAINT [CK_POSM_LinklyCloudConfigurationMode_Mode];
            """
        );

        var wrongCheck = await coordinator.CheckAsync(CancellationToken.None);
        Assert.False(wrongCheck.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, wrongCheck.ExitCode);
        Assert.Equal(
            SchemaDiagnosticCodes.LinklyMultiTerminalIncompatible,
            wrongCheck.DiagnosticCode
        );

        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            """
            ALTER TABLE [dbo].[POSM_LinklyCloudConfigurationMode]
                DROP CONSTRAINT [CK_POSM_LinklyCloudConfigurationMode_Mode];
            ALTER TABLE [dbo].[POSM_LinklyCloudConfigurationMode] WITH CHECK
                ADD CONSTRAINT [CK_POSM_LinklyCloudConfigurationMode_Mode]
                CHECK ([Mode] IN (N'Legacy', N'Draft', N'Active') OR N'Active' = N'Active');
            ALTER TABLE [dbo].[POSM_LinklyCloudConfigurationMode]
                CHECK CONSTRAINT [CK_POSM_LinklyCloudConfigurationMode_Mode];
            """
        );

        var tautologicalCheck = await coordinator.CheckAsync(CancellationToken.None);
        Assert.False(tautologicalCheck.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, tautologicalCheck.ExitCode);
        Assert.Equal(
            SchemaDiagnosticCodes.LinklyMultiTerminalIncompatible,
            tautologicalCheck.DiagnosticCode
        );

        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            """
            ALTER TABLE [dbo].[POSM_LinklyCloudConfigurationMode]
                DROP CONSTRAINT [CK_POSM_LinklyCloudConfigurationMode_Mode];
            ALTER TABLE [dbo].[POSM_LinklyCloudConfigurationMode] WITH CHECK
                ADD CONSTRAINT [CK_POSM_LinklyCloudConfigurationMode_Mode]
                CHECK ([Mode] IN (N'Legacy', N'Draft', N'Active'));
            ALTER TABLE [dbo].[POSM_LinklyCloudConfigurationMode]
                CHECK CONSTRAINT [CK_POSM_LinklyCloudConfigurationMode_Mode];

            ALTER TABLE [dbo].[POSM_LinklyCloudDeviceSelection]
                DROP CONSTRAINT [FK_POSM_LinklyCloudDeviceSelection_Terminal];
            ALTER TABLE [dbo].[POSM_LinklyCloudDeviceSelection] WITH CHECK
                ADD CONSTRAINT [FK_POSM_LinklyCloudDeviceSelection_Terminal]
                FOREIGN KEY ([TerminalId])
                REFERENCES [dbo].[POSM_LinklyCloudTerminal]([TerminalId])
                ON DELETE CASCADE;
            ALTER TABLE [dbo].[POSM_LinklyCloudDeviceSelection]
                CHECK CONSTRAINT [FK_POSM_LinklyCloudDeviceSelection_Terminal];
            """
        );

        var cascadeForeignKey = await coordinator.CheckAsync(CancellationToken.None);
        Assert.False(cascadeForeignKey.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, cascadeForeignKey.ExitCode);
        Assert.Equal(
            SchemaDiagnosticCodes.LinklyMultiTerminalIncompatible,
            cascadeForeignKey.DiagnosticCode
        );
    }

    [SchemaMigrationSqlServerFact]
    public async Task 真实API进程_显式命令不监听且普通启动通过门禁后才监听()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        Assert.True((await databases.CreateCoordinator().MigrateAsync(CancellationToken.None)).Success);

        foreach (var argument in new[] { "--schema=check", "--schema=migrate" })
        {
            var explicitResult = await RunApiToExitAsync(databases, [argument]);
            Assert.Equal(SchemaExitCodes.Success, explicitResult.ExitCode);
            Assert.DoesNotContain(
                "Now listening on:",
                explicitResult.CombinedOutput,
                StringComparison.Ordinal
            );
        }

        var serverOutput = await RunApiUntilListeningAsync(databases);
        Assert.Contains("Now listening on:", serverOutput, StringComparison.Ordinal);
    }

    [SchemaMigrationSqlServerFact]
    public async Task POSM激活表不兼容_主库账本保留且修复后只续跑POSM()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            "CREATE TABLE [dbo].[POSM_DeviceActivationGrant] ([GrantId] int NOT NULL);"
        );

        var first = await databases.CreateCoordinator().MigrateAsync(CancellationToken.None);

        Assert.False(first.Success);
        Assert.Equal(SchemaExitCodes.DatabaseFailure, first.ExitCode);
        Assert.True(
            await IsMigrationAppliedAsync(
                databases.MainConnectionString,
                SqlServerSchemaMigrationRuntime.MainHistoryTable,
                SchemaMigrationCoordinator.MainMigrationId
            )
        );
        Assert.False(
            await IsMigrationAppliedAsync(
                databases.PosmConnectionString,
                SqlServerSchemaMigrationRuntime.PosmHistoryTable,
                SchemaMigrationCoordinator.PosmMigrationId
            )
        );

        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            "DROP TABLE [dbo].[POSM_DeviceActivationGrant];"
        );

        var rerun = await databases.CreateCoordinator().MigrateAsync(CancellationToken.None);
        Assert.True(rerun.Success);
        Assert.Equal(SchemaExitCodes.Success, rerun.ExitCode);
        Assert.True(
            await IsMigrationAppliedAsync(
                databases.PosmConnectionString,
                SqlServerSchemaMigrationRuntime.PosmHistoryTable,
                SchemaMigrationCoordinator.PosmMigrationId
            )
        );
    }

    [SchemaMigrationSqlServerFact]
    public async Task POSM激活表主键为Nonclustered_严格门禁失败且不得登记Baseline()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        Assert.True((await databases.CreateCoordinator().MigrateAsync(CancellationToken.None)).Success);

        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            $"""
            DELETE FROM [dbo].[{SqlServerSchemaMigrationRuntime.PosmHistoryTable}]
            WHERE [MigrationId] = N'{SchemaMigrationCoordinator.PosmMigrationId}';

            ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                DROP CONSTRAINT [PK_POSM_DeviceActivationGrant];
            ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                ADD CONSTRAINT [PK_POSM_DeviceActivationGrant]
                PRIMARY KEY NONCLUSTERED ([GrantId]);
            """
        );

        var result = await databases.CreateCoordinator().MigrateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, result.ExitCode);
        Assert.False(
            await IsMigrationAppliedAsync(
                databases.PosmConnectionString,
                SqlServerSchemaMigrationRuntime.PosmHistoryTable,
                SchemaMigrationCoordinator.PosmMigrationId
            )
        );
    }

    [SchemaMigrationSqlServerFact]
    public async Task 同库并发迁移锁_第二个Session立即不可用()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        var lockResource = $"HBWeb:SchemaMigration:Integration:{Guid.NewGuid():N}";

        await using var first = await SqlServerSchemaMigrationLock.AcquireAsync(
            databases.MainConnectionString,
            lockResource,
            commandTimeoutSeconds: 30,
            CancellationToken.None
        );

        var exception = await Assert.ThrowsAsync<SchemaMigrationLockUnavailableException>(
            () =>
                SqlServerSchemaMigrationLock.AcquireAsync(
                    databases.MainConnectionString,
                    lockResource,
                    commandTimeoutSeconds: 30,
                    CancellationToken.None
                )
        );

        Assert.True(exception.ResultCode < 0);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 旧迁移器吞掉SQL异常_严格baseline仍失败且不输出原始细节()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        var database = databases.CreateMainContext().Db;
        using var capturedOutput = new StringWriter();
        var previousOut = Console.Out;
        const string SensitiveMarker = "SENSITIVE_SCHEMA_SQL_DETAIL";
        Console.SetOut(capturedOutput);

        try
        {
            var exception = await Assert.ThrowsAsync<SchemaBaselineSqlFailureException>(
                () =>
                    SqlServerSchemaMigrationRuntime.RunStrictBaselineAsync(
                        database,
                        () =>
                        {
                            try
                            {
                                database.Ado.ExecuteCommand(
                                    "SELECT 1 FROM [dbo].[__HBWebMissingSchemaObject];"
                                );
                            }
                            catch
                            {
                                // 模拟旧迁移器捕获异常后继续执行的历史行为。
                            }

                            Console.WriteLine(SensitiveMarker);
                            return Task.CompletedTask;
                        },
                        CancellationToken.None,
                        "strict-baseline-test"
                    )
            );
            Assert.Equal("strict-baseline-test", exception.StepId);
        }
        finally
        {
            Console.SetOut(previousOut);
        }

        Assert.DoesNotContain(SensitiveMarker, capturedOutput.ToString(), StringComparison.Ordinal);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 已迁移库_激活码索引或CHECK漂移_检查返回20且恢复后就绪()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        var coordinator = databases.CreateCoordinator();
        var migrated = await coordinator.MigrateAsync(CancellationToken.None);
        Assert.True(migrated.Success);

        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            "ALTER INDEX [UX_POSM_DeviceActivationGrant_SecretHash] ON [dbo].[POSM_DeviceActivationGrant] DISABLE;"
        );
        var disabledIndex = await coordinator.CheckAsync(CancellationToken.None);
        Assert.False(disabledIndex.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, disabledIndex.ExitCode);

        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            "ALTER INDEX [UX_POSM_DeviceActivationGrant_SecretHash] ON [dbo].[POSM_DeviceActivationGrant] REBUILD;"
        );
        Assert.True((await coordinator.CheckAsync(CancellationToken.None)).Success);

        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            "ALTER TABLE [dbo].[POSM_DeviceActivationGrant] NOCHECK CONSTRAINT [CK_POSM_DeviceActivationGrant_Expiry];"
        );
        var untrustedCheck = await coordinator.CheckAsync(CancellationToken.None);
        Assert.False(untrustedCheck.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, untrustedCheck.ExitCode);

        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            "ALTER TABLE [dbo].[POSM_DeviceActivationGrant] WITH CHECK CHECK CONSTRAINT [CK_POSM_DeviceActivationGrant_Expiry];"
        );
        Assert.True((await coordinator.CheckAsync(CancellationToken.None)).Success);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 已迁移库_主键名称或Clustered类型漂移_门禁返回20且恢复后Ready()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        var coordinator = databases.CreateCoordinator();
        Assert.True((await coordinator.MigrateAsync(CancellationToken.None)).Success);

        // 主键名称也属于安全签名，避免误把其他主键当成受管结构。
        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            "EXEC sys.sp_rename N'dbo.PK_POSM_DeviceActivationGrant', N'PK_POSM_DeviceActivationGrant_Renamed', N'OBJECT';"
        );

        var checkedResult = await coordinator.CheckAsync(CancellationToken.None);
        Assert.False(checkedResult.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, checkedResult.ExitCode);

        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            "EXEC sys.sp_rename N'dbo.PK_POSM_DeviceActivationGrant_Renamed', N'PK_POSM_DeviceActivationGrant', N'OBJECT';"
        );

        var restoredResult = await coordinator.CheckAsync(CancellationToken.None);
        Assert.True(restoredResult.Success);
        Assert.Equal(SchemaExitCodes.Success, restoredResult.ExitCode);

        // 同名、同列但改为 NONCLUSTERED 仍属于物理结构漂移，不能被只读门禁接受。
        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            """
            ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                DROP CONSTRAINT [PK_POSM_DeviceActivationGrant];
            ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                ADD CONSTRAINT [PK_POSM_DeviceActivationGrant]
                PRIMARY KEY NONCLUSTERED ([GrantId] ASC);
            """
        );

        var nonClusteredResult = await coordinator.CheckAsync(CancellationToken.None);
        Assert.False(nonClusteredResult.Success);
        Assert.Equal(SchemaExitCodes.SchemaNotReady, nonClusteredResult.ExitCode);

        await ExecuteNonQueryAsync(
            databases.PosmConnectionString,
            """
            ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                DROP CONSTRAINT [PK_POSM_DeviceActivationGrant];
            ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                ADD CONSTRAINT [PK_POSM_DeviceActivationGrant]
                PRIMARY KEY CLUSTERED ([GrantId] ASC);
            """
        );
        Assert.True((await coordinator.CheckAsync(CancellationToken.None)).Success);
    }

    private static async Task AssertHistoryTableAsync(
        string connectionString,
        string tableName,
        string migrationId
    )
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT [MigrationId], [AppliedAtUtc], [ApplicationVersion]
            FROM [dbo].[{tableName}]
            WHERE [MigrationId] = @MigrationId;
            """;
        command.Parameters.AddWithValue("@MigrationId", migrationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"账本 {tableName} 缺少 migration {migrationId}。");
        Assert.Equal(migrationId, reader.GetString(0));
        Assert.NotEqual(default, reader.GetDateTime(1));
        Assert.InRange(reader.GetString(2).Length, 1, 64);

        await reader.CloseAsync();
        command.Parameters.Clear();
        command.CommandText = $"""
            SELECT [name], TYPE_NAME([system_type_id]), [max_length], [is_nullable]
            FROM sys.columns
            WHERE [object_id] = OBJECT_ID(N'[dbo].[{tableName}]', N'U');
            """;
        await using var shapeReader = await command.ExecuteReaderAsync();
        var columns = new Dictionary<string, (string TypeName, short Length, bool Nullable)>(
            StringComparer.Ordinal
        );
        while (await shapeReader.ReadAsync())
        {
            columns.Add(
                shapeReader.GetString(0),
                (shapeReader.GetString(1), shapeReader.GetInt16(2), shapeReader.GetBoolean(3))
            );
        }

        Assert.Equal(
            new Dictionary<string, (string TypeName, short Length, bool Nullable)>(
                StringComparer.Ordinal
            )
            {
                ["MigrationId"] = ("nvarchar", 320, false),
                ["AppliedAtUtc"] = ("datetime2", 8, false),
                ["ApplicationVersion"] = ("nvarchar", 128, false),
            },
            columns
        );
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        string connectionString,
        string tableName,
        string migrationId
    )
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM [dbo].[{tableName}] WHERE [MigrationId] = @MigrationId
            ) THEN 1 ELSE 0 END AS bit);
            """;
        command.Parameters.AddWithValue("@MigrationId", migrationId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string tableName)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.tables WHERE [schema_id] = SCHEMA_ID(N'dbo') AND [name] = @TableName) THEN 1 ELSE 0 END AS bit);";
        command.Parameters.AddWithValue("@TableName", tableName);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> ColumnExistsAsync(
        string connectionString,
        string tableName,
        string columnName
    )
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1
                FROM sys.columns
                WHERE [object_id] = OBJECT_ID(N'[dbo].[' + @TableName + N']', N'U')
                  AND [name] = @ColumnName
            ) THEN 1 ELSE 0 END AS bit);
            """;
        command.Parameters.AddWithValue("@TableName", tableName);
        command.Parameters.AddWithValue("@ColumnName", columnName);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> IndexExistsAsync(
        string connectionString,
        string tableName,
        string indexName
    )
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1
                FROM sys.indexes AS i
                JOIN sys.tables AS t ON t.[object_id] = i.[object_id]
                WHERE t.[schema_id] = SCHEMA_ID(N'dbo')
                  AND t.[name] = @TableName
                  AND i.[name] = @IndexName
            ) THEN 1 ELSE 0 END AS bit);
            """;
        command.Parameters.AddWithValue("@TableName", tableName);
        command.Parameters.AddWithValue("@IndexName", indexName);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<ProcessResult> RunApiToExitAsync(
        IsolatedSchemaDatabases databases,
        IReadOnlyList<string> arguments
    )
    {
        var temporaryRoot = CreateProcessTemporaryRoot();
        try
        {
            using var process = CreateApiProcess(databases, arguments, temporaryRoot);
            Assert.True(process.Start());
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                throw new TimeoutException("API schema SQL 进程未在 120 秒内退出。");
            }
            return new ProcessResult(
                process.ExitCode,
                $"{await standardOutput}\n{await standardError}"
            );
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [SchemaMigrationSqlServerFact]
    public async Task 移动OTA并行目标列_保留旧策略行且可重复执行()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            CREATE TABLE dbo.MobileOtaPolicy (
                Id uniqueidentifier NOT NULL,
                Environment nvarchar(32) NOT NULL,
                Platform nvarchar(32) NOT NULL,
                PolicyVersion int NOT NULL,
                TargetRuntimeVersion nvarchar(64) NULL
            );
            INSERT dbo.MobileOtaPolicy (Id, Environment, Platform, PolicyVersion, TargetRuntimeVersion)
            VALUES ('11111111-1111-1111-1111-111111111111', N'production', N'Android', 29, N'1.0.5');
            """);

        await ExecuteNonQueryAsync(
            databases.MainConnectionString,
            MobileOtaRuntimeTargetsSchema.ApplySql
        );
        await ExecuteNonQueryAsync(
            databases.MainConnectionString,
            MobileOtaRuntimeTargetsSchema.VerifySql
        );
        await ExecuteNonQueryAsync(
            databases.MainConnectionString,
            MobileOtaRuntimeTargetsSchema.ApplySql
        );

        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            IF (SELECT COUNT(*) FROM dbo.MobileOtaPolicy
                WHERE Id = '11111111-1111-1111-1111-111111111111'
                  AND Environment = N'production'
                  AND Platform = N'Android'
                  AND PolicyVersion = 29
                  AND TargetRuntimeVersion = N'1.0.5'
                  AND AdditionalTargetsJson IS NULL) <> 1
                THROW 51913, 'Existing MobileOtaPolicy row was changed.', 1;
            IF (SELECT COUNT(*) FROM sys.columns
                WHERE object_id = OBJECT_ID(N'dbo.MobileOtaPolicy')
                  AND name = N'AdditionalTargetsJson'
                  AND system_type_id = TYPE_ID(N'nvarchar')
                  AND max_length = -1
                  AND is_nullable = 1) <> 1
                THROW 51914, 'AdditionalTargetsJson signature is invalid.', 1;
            """);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 移动OTA并行目标列_已有错误签名时拒绝且不修正()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            CREATE TABLE dbo.MobileOtaPolicy (
                Id uniqueidentifier NOT NULL,
                AdditionalTargetsJson nvarchar(4000) NULL
            );
            """);

        var mismatch = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(
                databases.MainConnectionString,
                MobileOtaRuntimeTargetsSchema.ApplySql
            )
        );

        Assert.Equal(51911, mismatch.Number);
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            IF (SELECT max_length FROM sys.columns
                WHERE object_id = OBJECT_ID(N'dbo.MobileOtaPolicy')
                  AND name = N'AdditionalTargetsJson') <> 8000
                THROW 51915, 'Migration changed the incompatible column.', 1;
            """);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 敏感申请生日列_保留历史申请可重复执行且签名门禁识别漂移()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            CREATE TABLE dbo.EmployeeProfileSensitiveChangeRequest (
                RequestId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                UserGUID nvarchar(50) NOT NULL,
                BankAccountNumber nvarchar(50) NULL
            );
            INSERT dbo.EmployeeProfileSensitiveChangeRequest (UserGUID, BankAccountNumber)
            VALUES (N'user-legacy', N'legacy-account');
            """);

        await ExecuteNonQueryAsync(databases.MainConnectionString, EmployeeProfileSensitiveBirthdaySchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, EmployeeProfileSensitiveBirthdaySchema.VerifySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, EmployeeProfileSensitiveBirthdaySchema.ApplySql);

        // 历史申请不回填生日，审批侧据此不改动正式生日。
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            IF (SELECT COUNT(*) FROM dbo.EmployeeProfileSensitiveChangeRequest
                WHERE UserGUID = N'user-legacy'
                  AND BankAccountNumber = N'legacy-account'
                  AND Birthday IS NULL) <> 1
                THROW 51863, 'Existing sensitive change request row was changed.', 1;
            """);

        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            ALTER TABLE dbo.EmployeeProfileSensitiveChangeRequest DROP COLUMN Birthday;
            ALTER TABLE dbo.EmployeeProfileSensitiveChangeRequest ADD Birthday datetime NULL;
            """);
        var mismatch = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, EmployeeProfileSensitiveBirthdaySchema.VerifySql));
        Assert.Equal(51862, mismatch.Number);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 须改密标记表_可重复执行且签名门禁识别漂移()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();

        await ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordChangeRequirementSchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordChangeRequirementSchema.VerifySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            INSERT dbo.UserPasswordChangeRequirement (UserGUID, Reason, RequiredAtUtc, RequiredBy)
            VALUES (N'user-1', N'created', SYSUTCDATETIME(), N'manager-1');
            """);
        // 重复执行不得丢失已有标记。
        await ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordChangeRequirementSchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            IF (SELECT COUNT(*) FROM dbo.UserPasswordChangeRequirement WHERE UserGUID = N'user-1') <> 1
                THROW 51873, 'Existing password change requirement row was lost.', 1;
            """);

        await ExecuteNonQueryAsync(
            databases.MainConnectionString,
            "ALTER TABLE dbo.UserPasswordChangeRequirement ALTER COLUMN Reason nvarchar(64) NOT NULL;"
        );
        var mismatch = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordChangeRequirementSchema.VerifySql));
        Assert.Equal(51871, mismatch.Number);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 门店小票资料下发两张表_可重复执行且签名门禁识别漂移()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        var main = databases.MainConnectionString;

        await ExecuteNonQueryAsync(main, StoreReceiptProfileReleaseSchema.ApplySql);
        await ExecuteNonQueryAsync(main, StoreReceiptProfileReleaseSchema.VerifySql);
        await ExecuteNonQueryAsync(main, """
            INSERT dbo.StoreReceiptProfileRelease (StoreCode, Version, StoreName, PublishedAtUtc, PublishedBy)
            VALUES (N'S001', 1, N'Store 1', SYSUTCDATETIME(), N'alice');
            INSERT dbo.PosReceiptProfileAck (DeviceCode, StoreCode, AppliedVersion, AppliedAtUtc, ClientKind)
            VALUES (N'DEV-1', N'S001', 1, SYSUTCDATETIME(), N'wpf');
            """);
        // 重复执行不得丢失已有快照与回执。
        await ExecuteNonQueryAsync(main, StoreReceiptProfileReleaseSchema.ApplySql);
        await ExecuteNonQueryAsync(main, """
            IF (SELECT COUNT(*) FROM dbo.StoreReceiptProfileRelease WHERE StoreCode = N'S001' AND Version = 1) <> 1
                THROW 52010, 'Existing release row was lost.', 1;
            IF (SELECT COUNT(*) FROM dbo.PosReceiptProfileAck WHERE DeviceCode = N'DEV-1') <> 1
                THROW 52011, 'Existing ack row was lost.', 1;
            """);
        // 主键 (StoreCode, Version)：同店同版本不能重复，不同版本可以并存。
        await Assert.ThrowsAsync<SqlException>(() => ExecuteNonQueryAsync(main, """
            INSERT dbo.StoreReceiptProfileRelease (StoreCode, Version, StoreName, PublishedAtUtc)
            VALUES (N'S001', 1, N'dup', SYSUTCDATETIME());
            """));
        await ExecuteNonQueryAsync(main, """
            INSERT dbo.StoreReceiptProfileRelease (StoreCode, Version, StoreName, PublishedAtUtc)
            VALUES (N'S001', 2, N'Store 1b', SYSUTCDATETIME());
            """);

        // 快照表列宽漂移（ABN nvarchar(20) → 40）。
        await ExecuteNonQueryAsync(main, "ALTER TABLE dbo.StoreReceiptProfileRelease ALTER COLUMN ABN nvarchar(40) NULL;");
        var releaseColumns = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, StoreReceiptProfileReleaseSchema.VerifySql));
        Assert.Equal(52001, releaseColumns.Number);
        await ExecuteNonQueryAsync(main, "ALTER TABLE dbo.StoreReceiptProfileRelease ALTER COLUMN ABN nvarchar(20) NULL;");
        await ExecuteNonQueryAsync(main, StoreReceiptProfileReleaseSchema.VerifySql);

        // 快照表主键被改成非聚集。
        await ExecuteNonQueryAsync(main, """
            ALTER TABLE dbo.StoreReceiptProfileRelease DROP CONSTRAINT PK_StoreReceiptProfileRelease;
            ALTER TABLE dbo.StoreReceiptProfileRelease ADD CONSTRAINT PK_StoreReceiptProfileRelease
                PRIMARY KEY NONCLUSTERED (StoreCode, Version);
            """);
        var releaseKey = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, StoreReceiptProfileReleaseSchema.VerifySql));
        Assert.Equal(52002, releaseKey.Number);
        await ExecuteNonQueryAsync(main, """
            ALTER TABLE dbo.StoreReceiptProfileRelease DROP CONSTRAINT PK_StoreReceiptProfileRelease;
            ALTER TABLE dbo.StoreReceiptProfileRelease ADD CONSTRAINT PK_StoreReceiptProfileRelease
                PRIMARY KEY CLUSTERED (StoreCode, Version);
            """);
        await ExecuteNonQueryAsync(main, StoreReceiptProfileReleaseSchema.VerifySql);

        // 快照表主键列序颠倒（Version, StoreCode）。
        await ExecuteNonQueryAsync(main, """
            ALTER TABLE dbo.StoreReceiptProfileRelease DROP CONSTRAINT PK_StoreReceiptProfileRelease;
            ALTER TABLE dbo.StoreReceiptProfileRelease ADD CONSTRAINT PK_StoreReceiptProfileRelease
                PRIMARY KEY CLUSTERED (Version, StoreCode);
            """);
        var reversedKey = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, StoreReceiptProfileReleaseSchema.VerifySql));
        Assert.Equal(52002, reversedKey.Number);
        await ExecuteNonQueryAsync(main, """
            ALTER TABLE dbo.StoreReceiptProfileRelease DROP CONSTRAINT PK_StoreReceiptProfileRelease;
            ALTER TABLE dbo.StoreReceiptProfileRelease ADD CONSTRAINT PK_StoreReceiptProfileRelease
                PRIMARY KEY CLUSTERED (StoreCode, Version);
            """);

        // 回执表列可空性漂移。
        await ExecuteNonQueryAsync(main, "ALTER TABLE dbo.PosReceiptProfileAck ALTER COLUMN ClientKind nvarchar(16) NULL;");
        var ackColumns = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, StoreReceiptProfileReleaseSchema.VerifySql));
        Assert.Equal(52003, ackColumns.Number);
        await ExecuteNonQueryAsync(main, "ALTER TABLE dbo.PosReceiptProfileAck ALTER COLUMN ClientKind nvarchar(16) NOT NULL;");
        await ExecuteNonQueryAsync(main, StoreReceiptProfileReleaseSchema.VerifySql);

        // 回执门店索引缺失：Verify 报错，重新 Apply 后补回。
        await ExecuteNonQueryAsync(main, "DROP INDEX [IX_PosReceiptProfileAck_StoreCode] ON dbo.PosReceiptProfileAck;");
        var ackIndex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, StoreReceiptProfileReleaseSchema.VerifySql));
        Assert.Equal(52005, ackIndex.Number);
        await ExecuteNonQueryAsync(main, StoreReceiptProfileReleaseSchema.ApplySql);
        await ExecuteNonQueryAsync(main, StoreReceiptProfileReleaseSchema.VerifySql);

        // 任一张表缺失：门禁报 52000。
        await ExecuteNonQueryAsync(main, "DROP TABLE dbo.PosReceiptProfileAck;");
        var missing = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, StoreReceiptProfileReleaseSchema.VerifySql));
        Assert.Equal(52000, missing.Number);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 考勤用餐休息两张表_可重复执行且过滤唯一索引与签名门禁生效()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        var main = databases.MainConnectionString;

        await ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.ApplySql);
        await ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.VerifySql);
        await ExecuteNonQueryAsync(main, """
            INSERT dbo.AttendanceMealBreak (BreakGuid, UserGuid, StoreCode, WorkDate, ScheduleGuid, StartUtc, EndUtc, CreatedAtUtc)
            VALUES (N'b1', N'u1', N'S001', '2026-05-18', N'sch-1', SYSUTCDATETIME(), NULL, SYSUTCDATETIME());
            INSERT dbo.AttendanceMealClaim
                (ClaimGuid, ScheduleGuid, UserGuid, StoreCode, WorkDate, ClockOutPunchGuid,
                 ExpectedCount, RecordedCount, MissingCount, NotTakenCount, ClaimedMinutes, Status, CreatedAtUtc)
            VALUES (N'c1', N'sch-1', N'u1', N'S001', '2026-05-18', N'out-1', 1, 0, 1, 1, 30, N'Pending', SYSUTCDATETIME());
            """);
        // 重复执行不得丢失已有休息记录与声明。
        await ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.ApplySql);
        await ExecuteNonQueryAsync(main, """
            IF (SELECT COUNT(*) FROM dbo.AttendanceMealBreak WHERE BreakGuid = N'b1') <> 1
                THROW 52110, 'Existing meal break row was lost.', 1;
            IF (SELECT COUNT(*) FROM dbo.AttendanceMealClaim WHERE ClaimGuid = N'c1') <> 1
                THROW 52111, 'Existing meal claim row was lost.', 1;
            """);

        // 每个员工同一时刻最多一条进行中的休息：第二条被过滤唯一索引拒绝，结束后可以再开，其他员工互不影响。
        await Assert.ThrowsAsync<SqlException>(() => ExecuteNonQueryAsync(main, """
            INSERT dbo.AttendanceMealBreak (BreakGuid, UserGuid, StoreCode, WorkDate, ScheduleGuid, StartUtc, EndUtc, CreatedAtUtc)
            VALUES (N'b2', N'u1', N'S001', '2026-05-18', N'sch-1', SYSUTCDATETIME(), NULL, SYSUTCDATETIME());
            """));
        await ExecuteNonQueryAsync(main, """
            UPDATE dbo.AttendanceMealBreak SET EndUtc = SYSUTCDATETIME() WHERE BreakGuid = N'b1';
            INSERT dbo.AttendanceMealBreak (BreakGuid, UserGuid, StoreCode, WorkDate, ScheduleGuid, StartUtc, EndUtc, CreatedAtUtc)
            VALUES (N'b2', N'u1', N'S001', '2026-05-18', N'sch-1', SYSUTCDATETIME(), NULL, SYSUTCDATETIME());
            INSERT dbo.AttendanceMealBreak (BreakGuid, UserGuid, StoreCode, WorkDate, ScheduleGuid, StartUtc, EndUtc, CreatedAtUtc)
            VALUES (N'b3', N'u2', N'S001', '2026-05-18', N'sch-2', SYSUTCDATETIME(), NULL, SYSUTCDATETIME());
            """);
        // 一次下班打卡最多一条声明（也是重复提交的幂等键）。
        await Assert.ThrowsAsync<SqlException>(() => ExecuteNonQueryAsync(main, """
            INSERT dbo.AttendanceMealClaim
                (ClaimGuid, ScheduleGuid, UserGuid, StoreCode, WorkDate, ClockOutPunchGuid,
                 ExpectedCount, RecordedCount, MissingCount, NotTakenCount, ClaimedMinutes, Status, CreatedAtUtc)
            VALUES (N'c2', N'sch-1', N'u1', N'S001', '2026-05-18', N'out-1', 1, 0, 1, 0, 0, N'None', SYSUTCDATETIME());
            """));

        // 休息表列宽漂移。
        await ExecuteNonQueryAsync(main, "ALTER TABLE dbo.AttendanceMealBreak ALTER COLUMN StoreCode nvarchar(60) NOT NULL;");
        var breakColumns = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.VerifySql));
        Assert.Equal(52101, breakColumns.Number);
        await ExecuteNonQueryAsync(main, "ALTER TABLE dbo.AttendanceMealBreak ALTER COLUMN StoreCode nvarchar(50) NOT NULL;");
        await ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.VerifySql);

        // 进行中休息的过滤唯一索引缺失：Verify 报错，重新 Apply 后补回。
        await ExecuteNonQueryAsync(main, "DROP INDEX [UX_AttendanceMealBreak_OpenPerUser] ON dbo.AttendanceMealBreak;");
        var openIndex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.VerifySql));
        Assert.Equal(52103, openIndex.Number);
        await ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.ApplySql);
        await ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.VerifySql);

        // 过滤唯一索引被改成不带过滤条件：同样识别为不兼容。
        await ExecuteNonQueryAsync(main, """
            DROP INDEX [UX_AttendanceMealBreak_OpenPerUser] ON dbo.AttendanceMealBreak;
            CREATE UNIQUE NONCLUSTERED INDEX [UX_AttendanceMealBreak_OpenPerUser]
                ON dbo.AttendanceMealBreak ([UserGuid], [BreakGuid]);
            """);
        var unfilteredIndex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.VerifySql));
        Assert.Equal(52103, unfilteredIndex.Number);
        await ExecuteNonQueryAsync(main, """
            DROP INDEX [UX_AttendanceMealBreak_OpenPerUser] ON dbo.AttendanceMealBreak;
            CREATE UNIQUE NONCLUSTERED INDEX [UX_AttendanceMealBreak_OpenPerUser]
                ON dbo.AttendanceMealBreak ([UserGuid]) WHERE [EndUtc] IS NULL;
            """);
        await ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.VerifySql);

        // 声明表列宽漂移。
        await ExecuteNonQueryAsync(main, "ALTER TABLE dbo.AttendanceMealClaim ALTER COLUMN Reason nvarchar(600) NULL;");
        var claimColumns = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.VerifySql));
        Assert.Equal(52105, claimColumns.Number);
        await ExecuteNonQueryAsync(main, "ALTER TABLE dbo.AttendanceMealClaim ALTER COLUMN Reason nvarchar(500) NULL;");
        await ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.VerifySql);

        // 声明的下班打卡唯一索引缺失。
        await ExecuteNonQueryAsync(main, "DROP INDEX [UX_AttendanceMealClaim_ClockOutPunch] ON dbo.AttendanceMealClaim;");
        var claimIndex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.VerifySql));
        Assert.Equal(52107, claimIndex.Number);
        await ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.ApplySql);
        await ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.VerifySql);

        // 任一张表缺失：门禁报 52100。
        await ExecuteNonQueryAsync(main, "DROP TABLE dbo.AttendanceMealClaim;");
        var missing = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, AttendanceMealBreakSchema.VerifySql));
        Assert.Equal(52100, missing.Number);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 分店现金管理六张表_可重复执行_权限码幂等入库且唯一索引与签名门禁生效()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        var main = databases.MainConnectionString;

        // 隔离库里没有基线建出的权限表，放一张同列的最小桩表，验证迁移内的权限码入库。
        await ExecuteNonQueryAsync(main, """
            CREATE TABLE dbo.HbwebSysPermissions
            (
                Id nvarchar(50) NOT NULL PRIMARY KEY,
                Code nvarchar(100) NOT NULL,
                Name nvarchar(100) NOT NULL,
                Category nvarchar(100) NOT NULL,
                Description nvarchar(500) NULL,
                CreatedAt datetime2 NOT NULL,
                CreatedBy nvarchar(100) NULL,
                UpdatedAt datetime2 NULL,
                UpdatedBy nvarchar(100) NULL,
                IsDeleted bit NOT NULL
            );
            """);
        await ExecuteNonQueryAsync(main, StoreCashManagementSchema.ApplySql);
        await ExecuteNonQueryAsync(main, StoreCashManagementSchema.VerifySql);
        // 重复执行：权限码不重复入库，也不丢已有数据。
        await ExecuteNonQueryAsync(main, """
            INSERT dbo.StoreCashBalanceEntry
                (EntryGuid, StoreCode, EntryType, EntryDate, Amount, Status, ClientRequestId, CreatedByUserGuid, CreatedAtUtc)
            VALUES (N'e1', N'S001', N'Opening', '2026-10-08', 100.00, N'Active', N'r1', N'u1', SYSUTCDATETIME());
            """);
        await ExecuteNonQueryAsync(main, StoreCashManagementSchema.ApplySql);
        await ExecuteNonQueryAsync(main, """
            IF (SELECT COUNT(*) FROM dbo.HbwebSysPermissions WHERE Code LIKE N'Cash.%') <> 5
                THROW 52210, 'Cash permissions should be inserted exactly once.', 1;
            IF (SELECT COUNT(*) FROM dbo.StoreCashBalanceEntry WHERE EntryGuid = N'e1') <> 1
                THROW 52211, 'Existing balance entry row was lost.', 1;
            """);

        // 每店至多一条有效期初：第二条有效期初被过滤唯一索引拒绝；作废后可以重录；盘点不受限。
        await Assert.ThrowsAsync<SqlException>(() => ExecuteNonQueryAsync(main, """
            INSERT dbo.StoreCashBalanceEntry
                (EntryGuid, StoreCode, EntryType, EntryDate, Amount, Status, ClientRequestId, CreatedByUserGuid, CreatedAtUtc)
            VALUES (N'e2', N'S001', N'Opening', '2026-10-08', 50.00, N'Active', N'r2', N'u1', SYSUTCDATETIME());
            """));
        await ExecuteNonQueryAsync(main, """
            INSERT dbo.StoreCashBalanceEntry
                (EntryGuid, StoreCode, EntryType, EntryDate, Amount, Status, ClientRequestId, CreatedByUserGuid, CreatedAtUtc)
            VALUES (N'e3', N'S001', N'Count', '2026-10-08', 80.00, N'Active', N'r3', N'u1', SYSUTCDATETIME());
            INSERT dbo.StoreCashBalanceEntry
                (EntryGuid, StoreCode, EntryType, EntryDate, Amount, Status, ClientRequestId, CreatedByUserGuid, CreatedAtUtc)
            VALUES (N'e4', N'S002', N'Opening', '2026-10-08', 60.00, N'Active', N'r4', N'u1', SYSUTCDATETIME());
            UPDATE dbo.StoreCashBalanceEntry SET Status = N'Voided' WHERE EntryGuid = N'e1';
            INSERT dbo.StoreCashBalanceEntry
                (EntryGuid, StoreCode, EntryType, EntryDate, Amount, Status, ClientRequestId, CreatedByUserGuid, CreatedAtUtc)
            VALUES (N'e5', N'S001', N'Opening', '2026-10-08', 90.00, N'Active', N'r5', N'u1', SYSUTCDATETIME());
            """);
        // 客户端请求号全局唯一，是幂等键。
        await Assert.ThrowsAsync<SqlException>(() => ExecuteNonQueryAsync(main, """
            INSERT dbo.StoreCashBalanceEntry
                (EntryGuid, StoreCode, EntryType, EntryDate, Amount, Status, ClientRequestId, CreatedByUserGuid, CreatedAtUtc)
            VALUES (N'e6', N'S003', N'Count', '2026-10-08', 1.00, N'Active', N'r3', N'u1', SYSUTCDATETIME());
            """));

        // 每个（分店、营业日、设备）至多一条当前的日结选择记录；历史（非当前）记录不受限。
        await ExecuteNonQueryAsync(main, """
            INSERT dbo.StoreCashCloseSelection
                (SelectionGuid, StoreCode, BusinessDate, DeviceCode, Mode, CloseIdsJson, OverlapWarning, Reason, IsCurrent, SelectedByUserGuid, SelectedAtUtc)
            VALUES (N's1', N'S001', '2026-10-07', N'POS_1', N'Manual', N'["c1","c2"]', 1, N'两次班结', 1, N'u1', SYSUTCDATETIME());
            """);
        await Assert.ThrowsAsync<SqlException>(() => ExecuteNonQueryAsync(main, """
            INSERT dbo.StoreCashCloseSelection
                (SelectionGuid, StoreCode, BusinessDate, DeviceCode, Mode, CloseIdsJson, OverlapWarning, Reason, IsCurrent, SelectedByUserGuid, SelectedAtUtc)
            VALUES (N's2', N'S001', '2026-10-07', N'POS_1', N'Manual', N'["c1"]', 0, N'重复', 1, N'u1', SYSUTCDATETIME());
            """));
        await ExecuteNonQueryAsync(main, """
            UPDATE dbo.StoreCashCloseSelection SET IsCurrent = 0 WHERE SelectionGuid = N's1';
            INSERT dbo.StoreCashCloseSelection
                (SelectionGuid, StoreCode, BusinessDate, DeviceCode, Mode, CloseIdsJson, OverlapWarning, Reason, IsCurrent, SelectedByUserGuid, SelectedAtUtc)
            VALUES (N's2', N'S001', '2026-10-07', N'POS_1', N'Default', N'[]', 0, N'恢复默认', 1, N'u1', SYSUTCDATETIME());
            """);

        // 存款与支出的客户端请求号同样是幂等键。
        await ExecuteNonQueryAsync(main, """
            INSERT dbo.StoreCashDeposit
                (DepositGuid, StoreCode, DepositDate, TotalAmount, Status, ClientRequestId, CreatedByUserGuid, CreatedAtUtc)
            VALUES (N'd1', N'S001', '2026-10-08', 120.50, N'Active', N'dep-1', N'u1', SYSUTCDATETIME());
            """);
        await Assert.ThrowsAsync<SqlException>(() => ExecuteNonQueryAsync(main, """
            INSERT dbo.StoreCashDeposit
                (DepositGuid, StoreCode, DepositDate, TotalAmount, Status, ClientRequestId, CreatedByUserGuid, CreatedAtUtc)
            VALUES (N'd2', N'S001', '2026-10-08', 10.00, N'Active', N'dep-1', N'u1', SYSUTCDATETIME());
            """));

        // 金额列漂移成别的精度：签名门禁报 52201；改回后通过。
        await ExecuteNonQueryAsync(main, "ALTER TABLE dbo.StoreCashDeposit ALTER COLUMN TotalAmount decimal(18,4) NOT NULL;");
        var amountColumn = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, StoreCashManagementSchema.VerifySql));
        Assert.Equal(52201, amountColumn.Number);
        await ExecuteNonQueryAsync(main, "ALTER TABLE dbo.StoreCashDeposit ALTER COLUMN TotalAmount decimal(18,2) NOT NULL;");
        await ExecuteNonQueryAsync(main, StoreCashManagementSchema.VerifySql);

        // 列宽漂移（选未建索引的列，建了索引的列 SQL Server 不允许直接改宽）。
        await ExecuteNonQueryAsync(main, "ALTER TABLE dbo.StoreCashExpense ALTER COLUMN PayeeName nvarchar(120) NULL;");
        var width = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, StoreCashManagementSchema.VerifySql));
        Assert.Equal(52201, width.Number);
        await ExecuteNonQueryAsync(main, "ALTER TABLE dbo.StoreCashExpense ALTER COLUMN PayeeName nvarchar(100) NULL;");
        await ExecuteNonQueryAsync(main, StoreCashManagementSchema.VerifySql);

        // 期初的过滤唯一索引缺失：Verify 报 52203，重新 Apply 后补回。
        await ExecuteNonQueryAsync(main, "DROP INDEX [UX_StoreCashBalanceEntry_Opening] ON dbo.StoreCashBalanceEntry;");
        var openingIndex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, StoreCashManagementSchema.VerifySql));
        Assert.Equal(52203, openingIndex.Number);
        await ExecuteNonQueryAsync(main, StoreCashManagementSchema.ApplySql);
        await ExecuteNonQueryAsync(main, StoreCashManagementSchema.VerifySql);

        // 当前选择的过滤唯一索引被换成同名的普通索引（不唯一、不带过滤；表里已有同键的历史行，建不出无过滤的唯一索引）：
        // 同样识别为不兼容。
        await ExecuteNonQueryAsync(main, """
            DROP INDEX [UX_StoreCashCloseSelection_Current] ON dbo.StoreCashCloseSelection;
            CREATE NONCLUSTERED INDEX [UX_StoreCashCloseSelection_Current]
                ON dbo.StoreCashCloseSelection ([StoreCode], [BusinessDate], [DeviceCode]);
            """);
        var unfiltered = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, StoreCashManagementSchema.VerifySql));
        Assert.Equal(52203, unfiltered.Number);
        await ExecuteNonQueryAsync(main, """
            DROP INDEX [UX_StoreCashCloseSelection_Current] ON dbo.StoreCashCloseSelection;
            CREATE UNIQUE NONCLUSTERED INDEX [UX_StoreCashCloseSelection_Current]
                ON dbo.StoreCashCloseSelection ([StoreCode], [BusinessDate], [DeviceCode]) WHERE [IsCurrent] = 1;
            """);
        await ExecuteNonQueryAsync(main, StoreCashManagementSchema.VerifySql);

        // 复合索引键列顺序颠倒：报 52204。
        await ExecuteNonQueryAsync(main, """
            DROP INDEX [IX_StoreCashDeposit_Store_Date] ON dbo.StoreCashDeposit;
            CREATE NONCLUSTERED INDEX [IX_StoreCashDeposit_Store_Date]
                ON dbo.StoreCashDeposit ([DepositDate], [StoreCode]);
            """);
        var keyOrder = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, StoreCashManagementSchema.VerifySql));
        Assert.Equal(52204, keyOrder.Number);
        await ExecuteNonQueryAsync(main, """
            DROP INDEX [IX_StoreCashDeposit_Store_Date] ON dbo.StoreCashDeposit;
            CREATE NONCLUSTERED INDEX [IX_StoreCashDeposit_Store_Date]
                ON dbo.StoreCashDeposit ([StoreCode], [DepositDate]);
            """);
        await ExecuteNonQueryAsync(main, StoreCashManagementSchema.VerifySql);

        // 任一张表缺失：门禁报 52200。
        await ExecuteNonQueryAsync(main, "DROP TABLE dbo.StoreCashAttachment;");
        var missing = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(main, StoreCashManagementSchema.VerifySql));
        Assert.Equal(52200, missing.Number);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 门店小票资料下发服务_真实SQLServer下发_并发同店只产生一个版本且冲突映射409()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        var mainContext = databases.CreateMainContext();
        var posmContext = databases.CreatePosmContext();
        // Store / 设备表只为本用例准备；两张新表走迁移 SQL，与生产建表方式一致。
        mainContext.Db.CodeFirst.InitTables<Store>();
        posmContext.Db.CodeFirst.InitTables<POSM_设备注册信息表>();
        await ExecuteNonQueryAsync(databases.MainConnectionString, StoreReceiptProfileReleaseSchema.ApplySql);
        foreach (var (guid, code, name) in new[]
        {
            ("g-1", "S001", "Store 1"), ("g-2", "S002", "Store 2"), ("g-3", "S003", "Store 3"),
            ("g-4", "S004", "Store 4"), ("g-5", "S005", "Store 5"), ("g-6", "S006", "Store 6"),
        })
        {
            await mainContext.Db.Insertable(new Store { StoreGUID = guid, StoreCode = code, StoreName = name }).ExecuteCommandAsync();
        }
        await posmContext.Db.Insertable(new POSM_设备注册信息表
        {
            设备硬件识别码 = "hw-1", 系统设备编号 = "DEV-1", 分店代码 = "S001", 设备类型 = "POS", 设备系统 = "Windows", 设备状态 = 1, 设备授权码 = "a",
        }).ExecuteCommandAsync();
        await posmContext.Db.Insertable(new POSM_设备注册信息表
        {
            设备硬件识别码 = "hw-2", 系统设备编号 = "DEV-2", 分店代码 = "S001", 设备类型 = "pos", 设备系统 = "Android", 设备状态 = 1, 设备授权码 = "b",
        }).ExecuteCommandAsync();
        var service = new StoreReceiptProfileService(
            mainContext, posmContext, NullLogger<StoreReceiptProfileService>.Instance, TimeProvider.System);

        // 首次下发 v1，重复下发 unchanged，改资料后只有改过的店升到 v2（真实执行 UPDLOCK, HOLDLOCK 读取）。
        var first = await service.PublishAsync(new[] { "g-1", "g-2" }, "alice");
        Assert.True(first.Success, first.Message);
        Assert.All(first.Data!.Items, item => Assert.Equal(("published", 1), (item.Outcome, item.Version)));
        var again = await service.PublishAsync(new[] { "g-1", "g-2" }, "alice");
        Assert.Equal(2, again.Data!.UnchangedCount);
        await ExecuteNonQueryAsync(databases.MainConnectionString, "UPDATE dbo.Store SET Phone = N'0400' WHERE StoreGUID = N'g-1';");
        var third = await service.PublishAsync(new[] { "g-1", "g-2" }, "alice");
        Assert.Equal(
            new[] { ("g-1", "published", 2), ("g-2", "unchanged", 1) },
            third.Data!.Items.Select(item => (item.StoreGuid, item.Outcome, item.Version)).ToArray());

        // 设备应用情况：设备类型大小写不敏感，回执按设备当前门店隔离。
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            INSERT dbo.PosReceiptProfileAck (DeviceCode, StoreCode, AppliedVersion, AppliedAtUtc, ClientKind)
            VALUES (N'DEV-1', N'S001', 2, SYSUTCDATETIME(), N'wpf'),
                   (N'DEV-2', N'S999', 9, SYSUTCDATETIME(), N'handheld');
            """);
        var status = (await service.GetStatusAsync(new[] { "g-1" })).Data!.Single();
        Assert.Equal("synced", status.Status);
        Assert.Equal(2, status.LatestVersion);
        Assert.Equal(DateTimeKind.Utc, status.PublishedAtUtc!.Value.Kind);
        Assert.Equal(2, status.DeviceTotal);
        Assert.Equal(1, status.DeviceApplied);
        var devices = (await service.GetDevicesAsync("g-1")).Data!.Devices;
        Assert.Equal(new[] { "DEV-1", "DEV-2" }, devices.Select(device => device.DeviceCode).ToArray());
        Assert.Equal(new int?[] { 2, null }, devices.Select(device => device.AppliedVersion).ToArray());
        Assert.Equal(new[] { true, false }, devices.Select(device => device.UpToDate).ToArray());

        // 并发下发同一家从未下发的店：HOLDLOCK 串行化，只会有一个事务写出 v1，其余读到后判为 unchanged，没有主键冲突。
        var sameStore = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            PublishWithFreshContextsAsync(databases, new[] { "g-3" })));
        Assert.All(sameStore, result => Assert.True(result.Success, result.Message));
        Assert.Equal(1, sameStore.Sum(result => result.Data!.PublishedCount));
        Assert.Equal(7, sameStore.Sum(result => result.Data!.UnchangedCount));
        Assert.Equal(1, await CountReleasesAsync(databases.MainConnectionString, "S003"));

        // 请求顺序相反的并发批量下发（g-4、g-5 互为逆序）：IN 列表按固定顺序申请锁，不死锁，每家店恰好一个 v1。
        var crossed = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            PublishWithFreshContextsAsync(databases, index % 2 == 0 ? new[] { "g-4", "g-5" } : new[] { "g-5", "g-4" })));
        Assert.All(crossed, result => Assert.True(result.Success, $"{result.ErrorCode}: {result.Message}"));
        Assert.Equal(2, crossed.Sum(result => result.Data!.PublishedCount));
        Assert.Equal(1, await CountReleasesAsync(databases.MainConnectionString, "S004"));
        Assert.Equal(1, await CountReleasesAsync(databases.MainConnectionString, "S005"));

        // 主键冲突（兜底路径）：钩子在同一事务里先写入 (S006, 1)，真实 SQL Server 报 2627，映射为 409 语义并整批回滚。
        var conflictMain = databases.CreateMainContext();
        var conflicting = new StoreReceiptProfileService(
            conflictMain.Db,
            databases.CreatePosmContext().Db,
            NullLogger<StoreReceiptProfileService>.Instance,
            TimeProvider.System,
            async () => await conflictMain.Db.Insertable(new StoreReceiptProfileRelease
            {
                StoreCode = "S006",
                Version = 1,
                StoreName = "抢先写入",
                PublishedAtUtc = DateTime.UtcNow,
            }).ExecuteCommandAsync());
        var conflict = await conflicting.PublishAsync(new[] { "g-6" }, "alice");
        Assert.False(conflict.Success);
        Assert.Equal("RECEIPT_PROFILE_PUBLISH_CONFLICT", conflict.ErrorCode);
        Assert.Equal(0, await CountReleasesAsync(databases.MainConnectionString, "S006"));
        var retried = await service.PublishAsync(new[] { "g-6" }, "alice");
        Assert.Equal(1, retried.Data!.Items.Single().Version);
    }

    private static Task<BlazorApp.Shared.DTOs.ApiResponse<StoreReceiptProfilePublishResultDto>> PublishWithFreshContextsAsync(
        IsolatedSchemaDatabases databases,
        string[] storeGuids) =>
        Task.Run(() => new StoreReceiptProfileService(
            databases.CreateMainContext(),
            databases.CreatePosmContext(),
            NullLogger<StoreReceiptProfileService>.Instance,
            TimeProvider.System).PublishAsync(storeGuids, "alice"));

    private static async Task<int> CountReleasesAsync(string connectionString, string storeCode)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.StoreReceiptProfileRelease WHERE StoreCode = @code";
        command.Parameters.AddWithValue("@code", storeCode);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    [SchemaMigrationSqlServerFact]
    public async Task 找回密码验证码表_可重复执行且签名门禁识别漂移()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();

        await ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordResetCodeSchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordResetCodeSchema.VerifySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            INSERT dbo.UserPasswordResetCode (Id, UserGUID, Purpose, CodeHash, ExpiresAtUtc, CreatedAtUtc)
            VALUES (N'code-1', N'user-1', N'invite', REPLICATE(N'a', 64), DATEADD(HOUR, 72, SYSUTCDATETIME()), SYSUTCDATETIME());
            """);
        // 重复执行不得丢失已有验证码记录；FailedAttempts 默认 0。
        await ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordResetCodeSchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            IF (SELECT COUNT(*) FROM dbo.UserPasswordResetCode WHERE Id = N'code-1' AND FailedAttempts = 0) <> 1
                THROW 51878, 'Existing password reset code row was lost.', 1;
            """);

        await ExecuteNonQueryAsync(
            databases.MainConnectionString,
            "DROP INDEX [IX_UserPasswordResetCode_User_Created] ON dbo.UserPasswordResetCode;"
        );
        var mismatch = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordResetCodeSchema.VerifySql));
        Assert.Equal(51877, mismatch.Number);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 验证码表新邮箱列_依赖原表_可重复执行且签名门禁识别漂移()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();

        // 原表不存在时拒绝执行，提示先跑 20261004.004。
        var missing = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordResetCodeTargetEmailSchema.ApplySql));
        Assert.Equal(51880, missing.Number);

        await ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordResetCodeSchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            INSERT dbo.UserPasswordResetCode (Id, UserGUID, Purpose, CodeHash, ExpiresAtUtc, CreatedAtUtc)
            VALUES (N'code-1', N'user-1', N'invite', REPLICATE(N'a', 64), DATEADD(HOUR, 72, SYSUTCDATETIME()), SYSUTCDATETIME());
            """);
        await ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordResetCodeTargetEmailSchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordResetCodeTargetEmailSchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordResetCodeTargetEmailSchema.VerifySql);
        // 原表门禁不受新增列影响；历史行保留且新列为空。
        await ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordResetCodeSchema.VerifySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            IF (SELECT COUNT(*) FROM dbo.UserPasswordResetCode WHERE Id = N'code-1' AND TargetEmail IS NULL) <> 1
                THROW 51879, 'Existing password reset code row was lost.', 1;
            """);

        await ExecuteNonQueryAsync(
            databases.MainConnectionString,
            "ALTER TABLE dbo.UserPasswordResetCode ALTER COLUMN TargetEmail nvarchar(100) NULL;"
        );
        var mismatch = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, UserPasswordResetCodeTargetEmailSchema.VerifySql));
        Assert.Equal(51881, mismatch.Number);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 安卓原生最低构建号策略表_可重复执行_唯一过滤索引生效且签名门禁识别漂移()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();

        var missing = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, MobileAndroidNativeUpdatePolicySchema.VerifySql));
        Assert.Equal(51890, missing.Number);

        await ExecuteNonQueryAsync(databases.MainConnectionString, MobileAndroidNativeUpdatePolicySchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, MobileAndroidNativeUpdatePolicySchema.VerifySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            INSERT dbo.MobileAndroidNativeUpdatePolicy
                (Id, PolicyKey, Enabled, MinimumSupportedBuildNumber, ReleaseMessage, PolicyVersion, CreatedAt, CreatedBy)
            VALUES (NEWID(), N'mobile-android', 1, 63, N'msg', 1, SYSUTCDATETIME(), N'admin');
            """);
        // 重复执行不得丢失已有策略行；IsDeleted 默认 0。
        await ExecuteNonQueryAsync(databases.MainConnectionString, MobileAndroidNativeUpdatePolicySchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, MobileAndroidNativeUpdatePolicySchema.VerifySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            IF (SELECT COUNT(*) FROM dbo.MobileAndroidNativeUpdatePolicy
                WHERE PolicyKey = N'mobile-android' AND MinimumSupportedBuildNumber = 63 AND IsDeleted = 0) <> 1
                THROW 51893, 'Existing mobile android policy row was lost.', 1;
            """);

        // 未删除行按 PolicyKey 唯一；软删除行不占唯一键。
        var duplicate = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, """
                INSERT dbo.MobileAndroidNativeUpdatePolicy (Id, PolicyKey, Enabled, PolicyVersion, CreatedAt)
                VALUES (NEWID(), N'mobile-android', 0, 1, SYSUTCDATETIME());
                """));
        Assert.Contains(duplicate.Number, new[] { 2601, 2627 });
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            INSERT dbo.MobileAndroidNativeUpdatePolicy (Id, PolicyKey, Enabled, PolicyVersion, CreatedAt, IsDeleted)
            VALUES (NEWID(), N'mobile-android', 0, 1, SYSUTCDATETIME(), 1);
            """);

        await ExecuteNonQueryAsync(
            databases.MainConnectionString,
            "ALTER TABLE dbo.MobileAndroidNativeUpdatePolicy ALTER COLUMN ReleaseMessage nvarchar(500) NULL;"
        );
        var columnMismatch = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, MobileAndroidNativeUpdatePolicySchema.VerifySql));
        Assert.Equal(51891, columnMismatch.Number);

        await ExecuteNonQueryAsync(
            databases.MainConnectionString,
            "ALTER TABLE dbo.MobileAndroidNativeUpdatePolicy ALTER COLUMN ReleaseMessage nvarchar(1000) NULL;"
        );
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            DROP INDEX [UX_MobileAndroidNativeUpdatePolicy_PolicyKey] ON dbo.MobileAndroidNativeUpdatePolicy;
            CREATE UNIQUE INDEX [UX_MobileAndroidNativeUpdatePolicy_PolicyKey]
                ON dbo.MobileAndroidNativeUpdatePolicy (PolicyKey) WHERE IsDeleted = 1;
            """);
        var indexMismatch = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, MobileAndroidNativeUpdatePolicySchema.VerifySql));
        Assert.Equal(51892, indexMismatch.Number);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 供应商分类三表_可重复执行且签名门禁识别漂移()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();

        await ExecuteNonQueryAsync(databases.MainConnectionString, LocalSupplierCategorySchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, LocalSupplierCategorySchema.VerifySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, LocalSupplierCategorySchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, LocalSupplierCategorySchema.VerifySql);

        // 唯一索引保证同一供应商同一站点键只有一行，重复插入必须被数据库拒绝。
        var duplicate = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, """
                INSERT dbo.LocalSupplierCategory
                    (CategoryGUID, LocalSupplierCode, CategoryName, ExternalKey, FullPath, Depth,
                     IsPromotional, PromotionalSource, IsActive, FirstSeenAt, LastSeenAt, CreatedAt)
                VALUES
                    (N'a', N'240', N'Office', N'/office', N'Office', 0, 0, N'pattern', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (N'b', N'240', N'Office', N'/office', N'Office', 0, 0, N'pattern', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());
                """));
        Assert.Contains(duplicate.Number, new[] { 2601, 2627 });

        await ExecuteNonQueryAsync(databases.MainConnectionString,
            "DROP INDEX [UX_LocalSupplierCategory_Supplier_ExternalKey] ON dbo.LocalSupplierCategory;");
        var missingIndex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, LocalSupplierCategorySchema.VerifySql));
        Assert.Equal(51933, missingIndex.Number);

        await ExecuteNonQueryAsync(databases.MainConnectionString, LocalSupplierCategorySchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, LocalSupplierCategorySchema.VerifySql);

        await ExecuteNonQueryAsync(databases.MainConnectionString,
            "ALTER TABLE dbo.LocalSupplierCategoryProductAssignment ALTER COLUMN Source nvarchar(64) NOT NULL;");
        var driftedColumn = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, LocalSupplierCategorySchema.VerifySql));
        Assert.Equal(51931, driftedColumn.Number);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 未成年用工六表_可重复执行且未完成请求唯一并识别漂移()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();

        var missing = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, EmployeeMinorComplianceSchema.VerifySql));
        Assert.Equal(51980, missing.Number);

        await ExecuteNonQueryAsync(databases.MainConnectionString, EmployeeMinorComplianceSchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, EmployeeMinorComplianceSchema.VerifySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, EmployeeMinorComplianceSchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, EmployeeMinorComplianceSchema.VerifySql);

        // 同一员工只能有一条 open 请求；已完成的历史请求不受限制。
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            INSERT dbo.EmployeeMinorComplianceRequest (UserGUID, Status, CreatedAt) VALUES
                (N'u1', N'open', SYSUTCDATETIME()),
                (N'u1', N'completed', SYSUTCDATETIME()),
                (N'u1', N'cancelled', SYSUTCDATETIME());
            """);
        var duplicateOpen = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString,
                "INSERT dbo.EmployeeMinorComplianceRequest (UserGUID, Status, CreatedAt) VALUES (N'u1', N'open', SYSUTCDATETIME());"));
        Assert.Contains(duplicateOpen.Number, new[] { 2601, 2627 });

        // 验证码计数列有默认值，旧代码插入不带这些列也不会失败。
        await ExecuteNonQueryAsync(databases.MainConnectionString, """
            INSERT dbo.EmployeeMinorCompliance (UserGUID, Version, Status, StateCode, FormType, GuardianName, CreatedAt)
            VALUES (N'u1', 1, N'draft', N'QLD', N'QLD_CE1', N'Parent', SYSUTCDATETIME());
            """);

        await ExecuteNonQueryAsync(databases.MainConnectionString,
            "DROP INDEX [UX_MinorRequest_OpenPerUser] ON dbo.EmployeeMinorComplianceRequest;");
        var missingIndex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, EmployeeMinorComplianceSchema.VerifySql));
        Assert.Equal(51984, missingIndex.Number);
        await ExecuteNonQueryAsync(databases.MainConnectionString, EmployeeMinorComplianceSchema.ApplySql);
        await ExecuteNonQueryAsync(databases.MainConnectionString, EmployeeMinorComplianceSchema.VerifySql);

        await ExecuteNonQueryAsync(databases.MainConnectionString,
            "ALTER TABLE dbo.EmployeeMinorCompliance ALTER COLUMN GuardianOtpHash nvarchar(32) NULL;");
        var drifted = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, EmployeeMinorComplianceSchema.VerifySql));
        Assert.Equal(51981, drifted.Number);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 供应商分类签名门禁_缺表时报缺失()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();

        var missing = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, LocalSupplierCategorySchema.VerifySql));

        Assert.Equal(51930, missing.Number);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 老系统日志风险四表_可重复执行且签名门禁识别漂移()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        var posm = databases.PosmConnectionString;

        var missing = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(posm, LegacyEmployeeLogRiskSchema.VerifySql));
        Assert.Equal(51950, missing.Number);

        await ExecuteNonQueryAsync(posm, LegacyEmployeeLogRiskSchema.ApplySql);
        await ExecuteNonQueryAsync(posm, LegacyEmployeeLogRiskSchema.VerifySql);
        await ExecuteNonQueryAsync(posm, LegacyEmployeeLogRiskSchema.ApplySql);
        await ExecuteNonQueryAsync(posm, LegacyEmployeeLogRiskSchema.VerifySql);

        // 非聚集索引被删后门禁必须报索引漂移，重跑 ApplySql 可补回。
        await ExecuteNonQueryAsync(posm,
            "DROP INDEX [IX_LegacyEmployeeLogReviewHistory_LogId] ON dbo.LegacyEmployeeLogReviewHistory;");
        var missingIndex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(posm, LegacyEmployeeLogRiskSchema.VerifySql));
        Assert.Equal(51954, missingIndex.Number);
        await ExecuteNonQueryAsync(posm, LegacyEmployeeLogRiskSchema.ApplySql);
        await ExecuteNonQueryAsync(posm, LegacyEmployeeLogRiskSchema.VerifySql);

        // 金额精度漂移由 precision/scale 段识别（Amount 在索引 INCLUDE 中，改列前须先删索引，校验先于索引段报错）。
        await ExecuteNonQueryAsync(posm, """
            DROP INDEX [IX_LegacyEmployeeLogImpacts_StoreCode_OperationTime] ON dbo.LegacyEmployeeLogImpacts;
            ALTER TABLE dbo.LegacyEmployeeLogImpacts ALTER COLUMN Amount decimal(18,4) NOT NULL;
            """);
        var driftedDecimal = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(posm, LegacyEmployeeLogRiskSchema.VerifySql));
        Assert.Equal(51952, driftedDecimal.Number);
        await ExecuteNonQueryAsync(posm, "ALTER TABLE dbo.LegacyEmployeeLogImpacts ALTER COLUMN Amount decimal(18,2) NOT NULL;");
        await ExecuteNonQueryAsync(posm, LegacyEmployeeLogRiskSchema.ApplySql);
        await ExecuteNonQueryAsync(posm, LegacyEmployeeLogRiskSchema.VerifySql);

        // nvarchar 长度按字节核对：nvarchar(500) 改成 nvarchar(250) 必须被识别。
        await ExecuteNonQueryAsync(posm, "ALTER TABLE dbo.LegacyEmployeeLogReviews ALTER COLUMN Note nvarchar(250) NULL;");
        var driftedColumn = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(posm, LegacyEmployeeLogRiskSchema.VerifySql));
        Assert.Equal(51951, driftedColumn.Number);
    }

    [SchemaMigrationSqlServerFact]
    public async Task 新收银审计风险三表_可重复执行且签名门禁识别漂移()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();
        var posm = databases.PosmConnectionString;

        var missing = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(posm, PosOperationAuditRiskSchema.VerifySql));
        Assert.Equal(51970, missing.Number);

        await ExecuteNonQueryAsync(posm, PosOperationAuditRiskSchema.ApplySql);
        await ExecuteNonQueryAsync(posm, PosOperationAuditRiskSchema.VerifySql);
        await ExecuteNonQueryAsync(posm, PosOperationAuditRiskSchema.ApplySql);
        await ExecuteNonQueryAsync(posm, PosOperationAuditRiskSchema.VerifySql);

        // 非聚集索引被删后门禁必须报索引漂移，重跑 ApplySql 可补回。
        await ExecuteNonQueryAsync(posm,
            "DROP INDEX [IX_PosOperationAuditReviewHistory_EventId] ON dbo.PosOperationAuditReviewHistory;");
        var missingIndex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(posm, PosOperationAuditRiskSchema.VerifySql));
        Assert.Equal(51974, missingIndex.Number);
        await ExecuteNonQueryAsync(posm, PosOperationAuditRiskSchema.ApplySql);
        await ExecuteNonQueryAsync(posm, PosOperationAuditRiskSchema.VerifySql);

        // nvarchar 长度按字节核对：nvarchar(500) 改成 nvarchar(250) 必须被识别。
        await ExecuteNonQueryAsync(posm, "ALTER TABLE dbo.PosOperationAuditReviews ALTER COLUMN Note nvarchar(250) NULL;");
        var driftedColumn = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(posm, PosOperationAuditRiskSchema.VerifySql));
        Assert.Equal(51971, driftedColumn.Number);
    }

    private static async Task<string> RunApiUntilListeningAsync(
        IsolatedSchemaDatabases databases
    )
    {
        var temporaryRoot = CreateProcessTemporaryRoot();
        using var process = CreateApiProcess(databases, [], temporaryRoot);
        var output = new List<string>();
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void CaptureLine(object sender, DataReceivedEventArgs eventArgs)
        {
            if (eventArgs.Data is null)
            {
                return;
            }

            lock (output)
            {
                output.Add(eventArgs.Data);
            }
            if (eventArgs.Data.Contains("Now listening on:", StringComparison.Ordinal))
            {
                listening.TrySetResult();
            }
        }

        process.OutputDataReceived += CaptureLine;
        process.ErrorDataReceived += CaptureLine;
        try
        {
            Assert.True(process.Start());
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await listening.Task.WaitAsync(timeout.Token);
            Assert.False(process.HasExited, "API 在报告监听后不应立即退出。");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.CancelOutputRead();
            process.CancelErrorRead();
            Directory.Delete(temporaryRoot, recursive: true);
        }

        lock (output)
        {
            return string.Join(Environment.NewLine, output);
        }
    }

    private static Process CreateApiProcess(
        IsolatedSchemaDatabases databases,
        IReadOnlyList<string> arguments,
        string temporaryRoot
    )
    {
        var apiAssemblyPath = Path.Combine(AppContext.BaseDirectory, "BlazorApp.Api.dll");
        Assert.True(File.Exists(apiAssemblyPath), $"缺少 API 构建产物: {apiAssemblyPath}");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        KeepOnlyRequiredProcessEnvironment(process.StartInfo);
        process.StartInfo.ArgumentList.Add(apiAssemblyPath);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        process.StartInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        process.StartInfo.Environment["Database__InitializeOnStartup"] = "false";
        process.StartInfo.Environment["Database__CommandTimeoutSeconds"] = "30";
        process.StartInfo.Environment["Cache__EnableStoreOrderWarmUp"] = "false";
        process.StartInfo.Environment["ApplicationLogging__Enabled"] = "false";
        process.StartInfo.Environment["PerformanceMetrics__Enabled"] = "false";
        process.StartInfo.Environment["PerformanceMetrics__SentryReleaseHealth__Enabled"] =
            "false";
        process.StartInfo.Environment["ScheduledTasks__Enabled"] = "false";
        process.StartInfo.Environment["ConnectionStrings__DefaultConnection"] =
            databases.MainConnectionString;
        process.StartInfo.Environment["ConnectionStrings__HBPOSMConnection"] =
            databases.PosmConnectionString;
        process.StartInfo.Environment["DataProtection__KeysPath"] =
            Path.Combine(temporaryRoot, "data-protection");
        process.StartInfo.Environment["AttendanceQrDataProtection__KeysPath"] =
            Path.Combine(temporaryRoot, "attendance-qr");
        process.StartInfo.Environment["Jwt__Key"] =
            "SchemaProcessTestsOnly-Key-With-At-Least-32-Bytes";
        process.StartInfo.Environment["Jwt__Issuer"] = "SchemaProcessTests";
        process.StartInfo.Environment["Jwt__Audience"] = "SchemaProcessTests";
        return process;
    }

    private static void KeepOnlyRequiredProcessEnvironment(ProcessStartInfo startInfo)
    {
        var requiredEnvironment = new[]
        {
            "PATH",
            "DOTNET_ROOT",
            "DOTNET_ROOT_X64",
            "TMPDIR",
            "LANG",
            "LC_ALL",
        }
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToArray();

        // 关键位置：SQL 集成测试只能看见隔离数据库和最小运行时环境。
        startInfo.Environment.Clear();
        foreach (var item in requiredEnvironment)
        {
            startInfo.Environment[item.Name] = item.Value!;
        }
    }

    private static string CreateProcessTemporaryRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"hb-schema-sql-process-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record ProcessResult(int ExitCode, string CombinedOutput);

    private sealed class IsolatedSchemaDatabases : IAsyncDisposable
    {
        private readonly string _masterConnectionString;
        private readonly string _mainDatabaseName;
        private readonly string _posmDatabaseName;

        private IsolatedSchemaDatabases(
            string masterConnectionString,
            string mainConnectionString,
            string posmConnectionString,
            string mainDatabaseName,
            string posmDatabaseName
        )
        {
            _masterConnectionString = masterConnectionString;
            MainConnectionString = mainConnectionString;
            PosmConnectionString = posmConnectionString;
            _mainDatabaseName = mainDatabaseName;
            _posmDatabaseName = posmDatabaseName;
        }

        public string MainConnectionString { get; }

        public string PosmConnectionString { get; }

        public static async Task<IsolatedSchemaDatabases> CreateAsync(
            bool enableMainSnapshotIsolation = true
        )
        {
            var suppliedConnectionString = Environment.GetEnvironmentVariable(
                ConnectionEnvironmentVariable
            );
            Assert.False(string.IsNullOrWhiteSpace(suppliedConnectionString));

            var masterConnectionString = BuildConnectionString(suppliedConnectionString!, "master");
            var suffix = Guid.NewGuid().ToString("N");
            var mainDatabaseName = $"HbWebSchemaMain_{suffix}";
            var posmDatabaseName = $"HbWebSchemaPosm_{suffix}";
            await ExecuteNonQueryAsync(
                masterConnectionString,
                $"CREATE DATABASE {QuoteSqlServerName(mainDatabaseName)};"
            );
            try
            {
                if (enableMainSnapshotIsolation)
                {
                    await ExecuteNonQueryAsync(
                        masterConnectionString,
                        $"ALTER DATABASE {QuoteSqlServerName(mainDatabaseName)} SET ALLOW_SNAPSHOT_ISOLATION ON;"
                    );
                }
                await ExecuteNonQueryAsync(
                    masterConnectionString,
                    $"CREATE DATABASE {QuoteSqlServerName(posmDatabaseName)};"
                );
                return new IsolatedSchemaDatabases(
                    masterConnectionString,
                    BuildConnectionString(suppliedConnectionString, mainDatabaseName),
                    BuildConnectionString(suppliedConnectionString, posmDatabaseName),
                    mainDatabaseName,
                    posmDatabaseName
                );
            }
            catch
            {
                SqlConnection.ClearAllPools();
                await DropDatabaseAsync(masterConnectionString, mainDatabaseName);
                SqlConnection.ClearAllPools();
                throw;
            }
        }

        public SchemaMigrationCoordinator CreateCoordinator()
        {
            var configuration = CreateConfiguration();
            var currentUser = new IsolatedMigrationCurrentUserService();
            return new SchemaMigrationCoordinator(
                configuration,
                new SqlSugarContext(
                    configuration,
                    NullLogger<SqlSugarContext>.Instance,
                    currentUser
                ),
                new POSMSqlSugarContext(
                    configuration,
                    currentUser,
                    NullLogger<POSMSqlSugarContext>.Instance
                ),
                NullLogger<SchemaMigrationCoordinator>.Instance
            );
        }

        public SqlServerSchemaMigrationRuntime CreateRuntime()
        {
            var configuration = CreateConfiguration();
            var currentUser = new IsolatedMigrationCurrentUserService();
            return new SqlServerSchemaMigrationRuntime(
                configuration,
                new SqlSugarContext(
                    configuration,
                    NullLogger<SqlSugarContext>.Instance,
                    currentUser
                ),
                new POSMSqlSugarContext(
                    configuration,
                    currentUser,
                    NullLogger<POSMSqlSugarContext>.Instance
                )
            );
        }

        public POSMSqlSugarContext CreatePosmContext()
        {
            var configuration = CreateConfiguration();
            return new POSMSqlSugarContext(
                configuration,
                new IsolatedMigrationCurrentUserService(),
                NullLogger<POSMSqlSugarContext>.Instance
            );
        }

        public SqlSugarContext CreateMainContext()
        {
            var configuration = CreateConfiguration();
            return new SqlSugarContext(
                configuration,
                NullLogger<SqlSugarContext>.Instance,
                new IsolatedMigrationCurrentUserService()
            );
        }

        private IConfigurationRoot CreateConfiguration() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = MainConnectionString,
                        ["ConnectionStrings:HBPOSMConnection"] = PosmConnectionString,
                        ["Database:CommandTimeoutSeconds"] = "300",
                        ["Database:InitializeOnStartup"] = "false",
                        ["Database:EnableSqlLogging"] = "false",
                    }
                )
                .Build();

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            try
            {
                await DropDatabaseAsync(_masterConnectionString, _posmDatabaseName);
                await DropDatabaseAsync(_masterConnectionString, _mainDatabaseName);
            }
            finally
            {
                SqlConnection.ClearAllPools();
            }
        }

        private static string BuildConnectionString(string connectionString, string databaseName)
        {
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = databaseName,
            };
            return builder.ConnectionString;
        }

        private static async Task DropDatabaseAsync(string masterConnectionString, string databaseName)
        {
            var quotedName = QuoteSqlServerName(databaseName);
            await ExecuteNonQueryAsync(
                masterConnectionString,
                $"""
                IF DB_ID(N'{databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE {quotedName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE {quotedName};
                END;
                """
            );
        }

        private static string QuoteSqlServerName(string name) =>
            $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    private sealed class IsolatedMigrationCurrentUserService : ICurrentUserService
    {
        public string GetCurrentUsername() => "SchemaIntegrationTest";

        public string GetCurrentUserGuid() => string.Empty;
    }
}
