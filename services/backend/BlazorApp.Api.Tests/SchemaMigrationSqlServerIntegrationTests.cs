using BlazorApp.Api.Data;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Services;
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
    public async Task 供应商分类签名门禁_缺表时报缺失()
    {
        await using var databases = await IsolatedSchemaDatabases.CreateAsync();

        var missing = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync(databases.MainConnectionString, LocalSupplierCategorySchema.VerifySql));

        Assert.Equal(51930, missing.Number);
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
