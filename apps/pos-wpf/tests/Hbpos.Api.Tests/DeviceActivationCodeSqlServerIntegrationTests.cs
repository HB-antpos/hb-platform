using BlazorApp.Shared.Security;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.Devices;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

public sealed class DeviceActivationSqlServerFactAttribute : FactAttribute
{
    internal const string ConnectionEnvironmentVariable =
        "DEVICE_ACTIVATION_SQLSERVER_TEST_CONNECTION";

    public DeviceActivationSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过隔离的真实 SQL Server 设备开通码事务测试。";
        }
    }
}

public sealed class DeviceActivationSqlServerTheoryAttribute : TheoryAttribute
{
    public DeviceActivationSqlServerTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    DeviceActivationSqlServerFactAttribute.ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {DeviceActivationSqlServerFactAttribute.ConnectionEnvironmentVariable}，跳过隔离的真实 SQL Server 设备开通码事务测试。";
        }
    }
}

[CollectionDefinition(Name)]
public sealed class DeviceActivationSqlServerCollection
    : ICollectionFixture<DeviceActivationSqlServerFixture>
{
    public const string Name = "DeviceActivationSqlServer";
}

public sealed class DeviceActivationSqlServerFixture : IAsyncLifetime
{
    private string? _masterConnectionString;
    private string? _mainDatabaseName;
    private string? _posmDatabaseName;

    public string MainConnectionString { get; private set; } = string.Empty;

    public string PosmConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var dedicatedConnectionString = Environment.GetEnvironmentVariable(
            DeviceActivationSqlServerFactAttribute.ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(dedicatedConnectionString))
        {
            return;
        }

        _mainDatabaseName = $"HbActivationMain_{Guid.NewGuid():N}";
        _posmDatabaseName = $"HbActivationPosm_{Guid.NewGuid():N}";
        _masterConnectionString = BuildConnectionString(dedicatedConnectionString, "master");
        MainConnectionString = BuildConnectionString(
            dedicatedConnectionString,
            _mainDatabaseName);
        PosmConnectionString = BuildConnectionString(
            dedicatedConnectionString,
            _posmDatabaseName);

        await ExecuteNonQueryAsync(
            _masterConnectionString,
            $"CREATE DATABASE {QuoteName(_mainDatabaseName)};");
        try
        {
            await ExecuteNonQueryAsync(
                _masterConnectionString,
                $"CREATE DATABASE {QuoteName(_posmDatabaseName)};");
            await CreateSchemasAsync();
        }
        catch
        {
            await DropDatabaseAsync(_masterConnectionString, _posmDatabaseName);
            await DropDatabaseAsync(_masterConnectionString, _mainDatabaseName);
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_masterConnectionString == null)
        {
            return;
        }

        await DropDatabaseAsync(_masterConnectionString, _posmDatabaseName);
        await DropDatabaseAsync(_masterConnectionString, _mainDatabaseName);
    }

    public async Task ResetAsync()
    {
        Assert.False(string.IsNullOrWhiteSpace(PosmConnectionString));
        await ExecuteNonQueryAsync(
            PosmConnectionString,
            """
            IF OBJECT_ID(N'dbo.TR_DeviceActivationGrantConsumeFailure', N'TR') IS NOT NULL
                DROP TRIGGER [dbo].[TR_DeviceActivationGrantConsumeFailure];
            IF OBJECT_ID(N'dbo.TR_DeviceActivationExpireDuringWrite', N'TR') IS NOT NULL
                DROP TRIGGER [dbo].[TR_DeviceActivationExpireDuringWrite];
            DELETE FROM [dbo].[POSM_DeviceActivationGrant];
            DELETE FROM [dbo].[POSM_设备注册信息表];
            """);
        await ExecuteNonQueryAsync(
            MainConnectionString,
            """
            UPDATE [dbo].[Store]
            SET [IsActive] = 1, [IsDeleted] = 0;
            """);
    }

    public (DeviceActivationCodeService Service, HbposSqlSugarContext Context) CreateService()
    {
        var configuration = CreateConfiguration();
        var context = new HbposSqlSugarContext(
            configuration,
            NullLogger<HbposSqlSugarContext>.Instance);
        return (
            new DeviceActivationCodeService(
                context,
                configuration,
                NullLogger<DeviceActivationCodeService>.Instance),
            context);
    }

    // 每次返回独立连接的上下文，供并发用例模拟多个 API 请求。
    public HbposSqlSugarContext CreateContext() =>
        new(CreateConfiguration(), NullLogger<HbposSqlSugarContext>.Instance);

    private IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MainConnection"] = MainConnectionString,
                ["ConnectionStrings:PosmConnection"] = PosmConnectionString,
                ["Database:CommandTimeoutSeconds"] = "30",
            })
            .Build();

    public async Task<DeviceActivationCodeMaterial> SeedGrantAsync(
        string storeCode = "S002",
        string deviceSystem = DeviceSystems.Windows,
        DateTime? expiresAtUtc = null)
    {
        var material = DeviceActivationCodeCodec.Create();
        await using var connection = new SqlConnection(PosmConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            INSERT INTO [dbo].[POSM_DeviceActivationGrant]
                ([GrantId], [SecretHash], [StoreCode], [DeviceSystem], [CreatedAtUtc],
                 [CreatedBy], [Reason], [ExpiresAtUtc])
            VALUES
                (@GrantId, @SecretHash, @StoreCode, @DeviceSystem, @CreatedAtUtc,
                 N'SQL_TEST', N'Integration test', @ExpiresAtUtc);
            """,
            connection);
        command.Parameters.AddWithValue("@GrantId", material.GrantId);
        command.Parameters.AddWithValue("@SecretHash", material.SecretHash);
        command.Parameters.AddWithValue("@StoreCode", storeCode);
        command.Parameters.AddWithValue("@DeviceSystem", deviceSystem);
        command.Parameters.AddWithValue("@CreatedAtUtc", DateTime.UtcNow.AddMinutes(-1));
        command.Parameters.AddWithValue(
            "@ExpiresAtUtc",
            expiresAtUtc ?? DateTime.UtcNow.AddMinutes(10));
        await command.ExecuteNonQueryAsync();
        return material;
    }

    public async Task<int> SeedDeviceAsync(
        string hardwareId,
        string deviceCode,
        string storeCode,
        int status,
        string authorizationCode,
        string deviceSystem = DeviceSystems.Windows)
    {
        await using var connection = new SqlConnection(PosmConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            INSERT INTO [dbo].[POSM_设备注册信息表]
                ([设备硬件识别码], [系统设备编号], [分店代码], [设备类型], [设备系统],
                 [设备状态], [设备授权码], [备注], [创建时间], [创建人])
            VALUES
                (@HardwareId, @DeviceCode, @StoreCode, N'POS', @DeviceSystem,
                 @Status, @AuthorizationCode, N'SQL integration test', SYSUTCDATETIME(), N'SQL_TEST');
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            connection);
        command.Parameters.AddWithValue("@HardwareId", hardwareId);
        command.Parameters.AddWithValue("@DeviceCode", deviceCode);
        command.Parameters.AddWithValue("@StoreCode", storeCode);
        command.Parameters.AddWithValue("@DeviceSystem", deviceSystem);
        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue("@AuthorizationCode", authorizationCode);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public Task<int> ScalarIntAsync(string sql, params SqlParameter[] parameters) =>
        ScalarAsync<int>(PosmConnectionString, sql, parameters);

    public Task<string?> ScalarStringAsync(string sql, params SqlParameter[] parameters) =>
        ScalarAsync<string?>(PosmConnectionString, sql, parameters);

    public Task<string?> GrantTableFingerprintAsync() =>
        ScalarStringAsync(
            """
            SELECT CONVERT(varchar(64), HASHBYTES(
                'SHA2_256',
                CONVERT(varbinary(max), COALESCE((
                    SELECT *
                    FROM [dbo].[POSM_DeviceActivationGrant]
                    ORDER BY [GrantId]
                    FOR JSON PATH, INCLUDE_NULL_VALUES
                ), N'[]'))), 2);
            """);

    public Task ExecutePosmAsync(string sql, params SqlParameter[] parameters) =>
        ExecuteNonQueryAsync(PosmConnectionString, sql, parameters);

    public Task ExecuteMainAsync(string sql, params SqlParameter[] parameters) =>
        ExecuteNonQueryAsync(MainConnectionString, sql, parameters);

    private async Task CreateSchemasAsync()
    {
        await ExecuteNonQueryAsync(
            MainConnectionString,
            """
            CREATE TABLE [dbo].[Store]
            (
                [StoreGUID] varchar(36) NOT NULL DEFAULT (CONVERT(varchar(36), NEWID())),
                [StoreCode] varchar(50) NOT NULL PRIMARY KEY,
                [StoreName] nvarchar(100) NOT NULL,
                [Address] nvarchar(500) NULL,
                [TimeZoneId] nvarchar(80) NULL,
                [ReturnPolicy] nvarchar(500) NULL,
                [ContactEmail] nvarchar(100) NULL,
                [ABN] nvarchar(20) NULL,
                [BrandName] nvarchar(100) NULL,
                [Phone] nvarchar(200) NULL,
                [IsActive] bit NOT NULL,
                [CreatedAt] datetime2(7) NOT NULL DEFAULT (SYSUTCDATETIME()),
                [CreatedBy] nvarchar(100) NULL,
                [UpdatedAt] datetime2(7) NULL,
                [UpdatedBy] nvarchar(100) NULL,
                [IsDeleted] bit NOT NULL
            );
            INSERT INTO [dbo].[Store] ([StoreCode], [StoreName], [IsActive], [IsDeleted])
            VALUES ('S001', N'Source store', 1, 0), ('S002', N'Target store', 1, 0);
            """);
        await ExecuteNonQueryAsync(
            PosmConnectionString,
            """
            CREATE TABLE [dbo].[POSM_设备注册信息表]
            (
                [ID] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                [设备硬件识别码] varchar(100) NOT NULL,
                [系统设备编号] varchar(50) NOT NULL,
                [分店代码] varchar(50) NOT NULL,
                [设备类型] nvarchar(20) NOT NULL,
                [设备系统] varchar(20) NOT NULL,
                [设备状态] int NOT NULL,
                [设备授权码] varchar(100) NULL,
                [备注] nvarchar(500) NULL,
                [创建时间] datetime2(7) NOT NULL,
                [创建人] nvarchar(128) NULL,
                [最后修改时间] datetime2(7) NULL,
                [最后修改人] nvarchar(128) NULL,
                [是否在线] bit NULL,
                [最后心跳时间] datetime2(7) NULL,
                [当前收银员ID] nvarchar(100) NULL,
                [当前收银员姓名] nvarchar(100) NULL,
                [收银员登录时间] datetime2(7) NULL,
                CONSTRAINT [UX_POSM_Device_StoreCode] UNIQUE ([分店代码], [系统设备编号])
            );
            """);
        await ExecuteNonQueryAsync(
            PosmConnectionString,
            BlazorApp.Shared.Models.POSM.DeviceActivationCodeSchema.EnsureSql);
    }

    private static string BuildConnectionString(string connectionString, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = databaseName,
            ApplicationName = "Hbpos.DeviceActivation.SqlTests",
        };
        return builder.ConnectionString;
    }

    private static async Task<T> ScalarAsync<T>(
        string connectionString,
        string sql,
        params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        if (value == null || value == DBNull.Value)
        {
            return default!;
        }
        return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static async Task ExecuteNonQueryAsync(
        string connectionString,
        string sql,
        params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string masterConnectionString, string? databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return;
        }
        await ExecuteNonQueryAsync(
            masterConnectionString,
            $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE {QuoteName(databaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {QuoteName(databaseName)};
            END;
            """);
    }

    private static string QuoteName(string value) =>
        $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
}

[Collection(DeviceActivationSqlServerCollection.Name)]
[Trait("Category", "SQL")]
public sealed class DeviceActivationCodeSqlServerIntegrationTests(
    DeviceActivationSqlServerFixture fixture)
{
    private const string InstallLegacyWeakConsumptionConstraintSql = """
        ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
            DROP CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption];
        ALTER TABLE [dbo].[POSM_DeviceActivationGrant] WITH CHECK
            ADD CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption] CHECK
            (
                ([ConsumedAtUtc] IS NULL
                    AND [ConsumedHardwareId] IS NULL
                    AND [ConsumedDeviceCode] IS NULL
                    AND [ConsumedDeviceRegistrationId] IS NULL
                    AND [ConsumedAuthorizationHash] IS NULL
                    AND [ConsumedDeviceSystem] IS NULL
                    AND [ConsumptionKind] IS NULL
                    AND [PreviousStoreCode] IS NULL
                    AND [PreviousDeviceCode] IS NULL)
                OR
                ([ConsumedAtUtc] IS NOT NULL
                    AND [ConsumedHardwareId] IS NOT NULL
                    AND [ConsumedDeviceCode] IS NOT NULL
                    AND [ConsumedDeviceRegistrationId] IS NOT NULL
                    AND [ConsumedAuthorizationHash] IS NOT NULL
                    AND [ConsumedDeviceSystem] IS NOT NULL
                    AND [ConsumptionKind] IN ('Initial', 'Rebind')
                    AND (([ConsumptionKind] = 'Initial'
                            AND [PreviousStoreCode] IS NULL
                            AND [PreviousDeviceCode] IS NULL)
                        OR ([ConsumptionKind] = 'Rebind'
                            AND [PreviousStoreCode] IS NOT NULL
                            AND [PreviousDeviceCode] IS NOT NULL)))
            );
        ALTER TABLE [dbo].[POSM_DeviceActivationGrant] WITH CHECK
            CHECK CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption];
        """;

    private const string RestoreStrictConsumptionConstraintSql = """
        ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
            DROP CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption];
        ALTER TABLE [dbo].[POSM_DeviceActivationGrant] WITH CHECK
            ADD CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption] CHECK
            (
                ([ConsumedAtUtc] IS NULL
                    AND [ConsumedHardwareId] IS NULL
                    AND [ConsumedDeviceCode] IS NULL
                    AND [ConsumedDeviceRegistrationId] IS NULL
                    AND [ConsumedAuthorizationHash] IS NULL
                    AND [ConsumedDeviceSystem] IS NULL
                    AND [ConsumptionKind] IS NULL
                    AND [PreviousStoreCode] IS NULL
                    AND [PreviousDeviceCode] IS NULL)
                OR
                ([ConsumedAtUtc] IS NOT NULL
                    AND [ConsumedHardwareId] IS NOT NULL
                    AND [ConsumedDeviceCode] IS NOT NULL
                    AND [ConsumedDeviceRegistrationId] IS NOT NULL
                    AND [ConsumedAuthorizationHash] IS NOT NULL
                    AND [ConsumedDeviceSystem] IS NOT NULL
                    AND [ConsumptionKind] IS NOT NULL
                    AND [ConsumptionKind] IN ('Initial', 'Rebind')
                    AND (([ConsumptionKind] = 'Initial'
                            AND [PreviousStoreCode] IS NULL
                            AND [PreviousDeviceCode] IS NULL)
                        OR ([ConsumptionKind] = 'Rebind'
                            AND [PreviousStoreCode] IS NOT NULL
                            AND [PreviousDeviceCode] IS NOT NULL)))
            );
        ALTER TABLE [dbo].[POSM_DeviceActivationGrant] WITH CHECK
            CHECK CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption];
        """;

    private const string InsertCase14Sql = """
        DECLARE @GrantId uniqueidentifier = NEWID();
        DECLARE @ProbeAt datetime2(7) = SYSUTCDATETIME();
        INSERT INTO [dbo].[POSM_DeviceActivationGrant]
        (
            [GrantId], [SecretHash], [StoreCode], [DeviceSystem], [CreatedAtUtc],
            [CreatedBy], [Reason], [ExpiresAtUtc], [ConsumedAtUtc],
            [ConsumedHardwareId], [ConsumedDeviceCode], [ConsumedDeviceRegistrationId],
            [ConsumedAuthorizationHash], [ConsumedDeviceSystem], [ConsumptionKind],
            [PreviousStoreCode], [PreviousDeviceCode]
        )
        VALUES
        (
            @GrantId,
            HASHBYTES('SHA2_256', CONVERT(varchar(36), @GrantId)),
            'S002', 'Windows', @ProbeAt, N'SQL_TEST', N'Case 14',
            DATEADD(minute, 10, @ProbeAt), @ProbeAt, 'HW-CASE-14', 'DEVICE-CASE-14',
            -2147480014, HASHBYTES('SHA2_256', CONVERT(varchar(36), NEWID())),
            'Windows', NULL, NULL, NULL
        );
        """;

    [DeviceActivationSqlServerFact]
    public async Task FreshEnsureSqlRunsAllSemanticProbesAndCase14IsRejectedByNamedConstraint()
    {
        await fixture.ResetAsync();
        await fixture.ExecutePosmAsync("DROP TABLE [dbo].[POSM_DeviceActivationGrant];");

        await fixture.ExecutePosmAsync(
            BlazorApp.Shared.Models.POSM.DeviceActivationCodeSchema.EnsureSql);

        Assert.Equal(
            4,
            await fixture.ScalarIntAsync(
                """
                SELECT COUNT(1)
                FROM sys.check_constraints
                WHERE [parent_object_id] = OBJECT_ID(N'dbo.POSM_DeviceActivationGrant')
                  AND [is_disabled] = 0
                  AND [is_not_trusted] = 0;
                """));
        Assert.Equal(
            0,
            await fixture.ScalarIntAsync(
                "SELECT COUNT(1) FROM [dbo].[POSM_DeviceActivationGrant];"));

        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            fixture.ExecutePosmAsync(InsertCase14Sql));

        Assert.Equal(547, exception.Number);
        Assert.Contains(
            "CK_POSM_DeviceActivationGrant_Consumption",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(
            0,
            await fixture.ScalarIntAsync(
                "SELECT COUNT(1) FROM [dbo].[POSM_DeviceActivationGrant];"));
    }

    [DeviceActivationSqlServerFact]
    public async Task MigrationUpgradesLegacyWeakConstraintWithoutChangingExistingRows()
    {
        await fixture.ResetAsync();
        await fixture.ExecutePosmAsync(InstallLegacyWeakConsumptionConstraintSql);
        try
        {
            await fixture.SeedGrantAsync();
            await fixture.ExecutePosmAsync(
                """
                DECLARE @GrantId uniqueidentifier = NEWID();
                DECLARE @ConsumedAt datetime2(7) = SYSUTCDATETIME();
                INSERT INTO [dbo].[POSM_DeviceActivationGrant]
                (
                    [GrantId], [SecretHash], [StoreCode], [DeviceSystem], [CreatedAtUtc],
                    [CreatedBy], [Reason], [ExpiresAtUtc], [ConsumedAtUtc],
                    [ConsumedHardwareId], [ConsumedDeviceCode], [ConsumedDeviceRegistrationId],
                    [ConsumedAuthorizationHash], [ConsumedDeviceSystem], [ConsumptionKind],
                    [PreviousStoreCode], [PreviousDeviceCode]
                )
                VALUES
                (
                    @GrantId,
                    HASHBYTES('SHA2_256', CONVERT(varchar(36), @GrantId)),
                    'S002', 'Windows', DATEADD(minute, -1, @ConsumedAt), N'SQL_TEST',
                    N'Existing initial consumption', DATEADD(minute, 10, @ConsumedAt),
                    @ConsumedAt, 'HW-EXISTING', 'DEVICE-EXISTING', -2147480013,
                    HASHBYTES('SHA2_256', CONVERT(varchar(36), NEWID())),
                    'Windows', 'Initial', NULL, NULL
                );
                """);
            var fingerprintBefore = await fixture.GrantTableFingerprintAsync();

            await fixture.ExecutePosmAsync(ReadConsumptionConstraintMigrationSql());

            Assert.Equal(2, await fixture.ScalarIntAsync(
                "SELECT COUNT(1) FROM [dbo].[POSM_DeviceActivationGrant];"));
            Assert.Equal(fingerprintBefore, await fixture.GrantTableFingerprintAsync());
            Assert.Equal(
                1,
                await fixture.ScalarIntAsync(
                    """
                    SELECT COUNT(1)
                    FROM sys.check_constraints
                    WHERE [parent_object_id] = OBJECT_ID(N'dbo.POSM_DeviceActivationGrant')
                      AND [name] = N'CK_POSM_DeviceActivationGrant_Consumption'
                      AND [is_disabled] = 0
                      AND [is_not_trusted] = 0;
                    """));
            await fixture.ExecutePosmAsync(
                BlazorApp.Shared.Models.POSM.DeviceActivationCodeSchema.EnsureSql);
            Assert.Equal(fingerprintBefore, await fixture.GrantTableFingerprintAsync());
        }
        finally
        {
            await fixture.ResetAsync();
            await fixture.ExecutePosmAsync(RestoreStrictConsumptionConstraintSql);
        }
    }

    [DeviceActivationSqlServerFact]
    public async Task MigrationRollsBackWithoutChangingWeakConstraintOrInvalidRows()
    {
        await fixture.ResetAsync();
        await fixture.ExecutePosmAsync(InstallLegacyWeakConsumptionConstraintSql);
        try
        {
            await fixture.ExecutePosmAsync(InsertCase14Sql);
            var fingerprintBefore = await fixture.GrantTableFingerprintAsync();
            var constraintObjectIdBefore = await fixture.ScalarIntAsync(
                "SELECT OBJECT_ID(N'dbo.CK_POSM_DeviceActivationGrant_Consumption', N'C');");

            var migrationException = await Assert.ThrowsAsync<SqlException>(() =>
                fixture.ExecutePosmAsync(ReadConsumptionConstraintMigrationSql()));

            Assert.Equal(51022, migrationException.Number);
            Assert.Equal(1, await fixture.ScalarIntAsync(
                "SELECT COUNT(1) FROM [dbo].[POSM_DeviceActivationGrant];"));
            Assert.Equal(fingerprintBefore, await fixture.GrantTableFingerprintAsync());
            Assert.Equal(
                constraintObjectIdBefore,
                await fixture.ScalarIntAsync(
                    "SELECT OBJECT_ID(N'dbo.CK_POSM_DeviceActivationGrant_Consumption', N'C');"));

            var startupException = await Assert.ThrowsAsync<SqlException>(() =>
                fixture.ExecutePosmAsync(
                    BlazorApp.Shared.Models.POSM.DeviceActivationCodeSchema.EnsureSql));
            Assert.Equal(51013, startupException.Number);
            Assert.Equal(fingerprintBefore, await fixture.GrantTableFingerprintAsync());
        }
        finally
        {
            await fixture.ResetAsync();
            await fixture.ExecutePosmAsync(RestoreStrictConsumptionConstraintSql);
        }
    }

    [DeviceActivationSqlServerFact]
    public async Task ConcurrentWebAndPosInitializersWaitForTheSharedApplicationLockAndBothSucceed()
    {
        await fixture.ResetAsync();
        await using var blockerConnection = new SqlConnection(fixture.PosmConnectionString);
        await blockerConnection.OpenAsync();
        await using var blockerTransaction =
            (SqlTransaction)await blockerConnection.BeginTransactionAsync();
        var lockReleased = false;
        Task webInitializer = Task.CompletedTask;
        Task posInitializer = Task.CompletedTask;
        try
        {
            await using (var lockCommand = new SqlCommand(
                """
                DECLARE @LockResult int;
                EXEC @LockResult = sys.sp_getapplock
                    @Resource = N'HBPOS:Schema:DeviceActivationGrant',
                    @LockMode = N'Exclusive',
                    @LockOwner = N'Transaction',
                    @LockTimeout = 5000;
                SELECT @LockResult;
                """,
                blockerConnection,
                blockerTransaction))
            {
                Assert.True(Convert.ToInt32(await lockCommand.ExecuteScalarAsync()) >= 0);
            }

            webInitializer = fixture.ExecutePosmAsync(
                BlazorApp.Shared.Models.POSM.DeviceActivationCodeSchema.EnsureSql);
            posInitializer = fixture.ExecutePosmAsync(
                BlazorApp.Shared.Models.POSM.DeviceActivationCodeSchema.EnsureSql);
            var bothInitializers = Task.WhenAll(webInitializer, posInitializer);

            Assert.NotSame(
                bothInitializers,
                await Task.WhenAny(bothInitializers, Task.Delay(TimeSpan.FromMilliseconds(250))));

            await blockerTransaction.CommitAsync();
            lockReleased = true;
            await bothInitializers;

            Assert.Equal(
                0,
                await fixture.ScalarIntAsync(
                    """
                    SELECT COUNT(1)
                    FROM [dbo].[POSM_DeviceActivationGrant]
                    WHERE [CreatedBy] = N'HBPOS_SCHEMA_PROBE';
                    """));
        }
        finally
        {
            if (!lockReleased)
            {
                await blockerTransaction.RollbackAsync();
            }

            try
            {
                await Task.WhenAll(webInitializer, posInitializer);
            }
            catch
            {
                // 保留测试主体的原始异常；初始化任务错误会在正常路径直接抛出。
            }
        }
    }

    [DeviceActivationSqlServerFact]
    public async Task ExistingSchemaWithMissingUsableIndexFailsClosedInsteadOfRepairing()
    {
        await fixture.ResetAsync();
        await fixture.ExecutePosmAsync(
            "DROP INDEX [IX_POSM_DeviceActivationGrant_Usable] ON [dbo].[POSM_DeviceActivationGrant];");
        try
        {
            var exception = await Assert.ThrowsAsync<SqlException>(() =>
                fixture.ExecutePosmAsync(
                    BlazorApp.Shared.Models.POSM.DeviceActivationCodeSchema.EnsureSql));

            Assert.Equal(51011, exception.Number);
            Assert.Equal(
                0,
                await fixture.ScalarIntAsync(
                    "SELECT COUNT(1) FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.POSM_DeviceActivationGrant') AND [name] = N'IX_POSM_DeviceActivationGrant_Usable';"));
        }
        finally
        {
            await fixture.ExecutePosmAsync(
                """
                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE [object_id] = OBJECT_ID(N'dbo.POSM_DeviceActivationGrant')
                      AND [name] = N'IX_POSM_DeviceActivationGrant_Usable')
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_POSM_DeviceActivationGrant_Usable]
                        ON [dbo].[POSM_DeviceActivationGrant] ([StoreCode], [DeviceSystem], [ExpiresAtUtc])
                        INCLUDE ([GrantId], [SecretHash])
                        WHERE [RevokedAtUtc] IS NULL AND [ConsumedAtUtc] IS NULL;
                END;
                """);
        }
    }

    [DeviceActivationSqlServerFact]
    public async Task LookalikeConstraintNameCannotMaskTautologicalConsumptionCheckDuringSemanticProbe()
    {
        await fixture.ResetAsync();
        await fixture.ExecutePosmAsync(
            """
            ALTER TABLE [dbo].[POSM_DeviceActivationGrant] WITH CHECK
                ADD CONSTRAINT [CKxPOSMxDeviceActivationGrantxConsumption] CHECK
                (
                    ([ConsumedAtUtc] IS NULL
                        AND [ConsumedHardwareId] IS NULL
                        AND [ConsumedDeviceCode] IS NULL
                        AND [ConsumedDeviceRegistrationId] IS NULL
                        AND [ConsumedAuthorizationHash] IS NULL
                        AND [ConsumedDeviceSystem] IS NULL
                        AND [ConsumptionKind] IS NULL
                        AND [PreviousStoreCode] IS NULL
                        AND [PreviousDeviceCode] IS NULL)
                    OR
                    ([ConsumedAtUtc] IS NOT NULL
                        AND [ConsumedHardwareId] IS NOT NULL
                        AND [ConsumedDeviceCode] IS NOT NULL
                        AND [ConsumedDeviceRegistrationId] IS NOT NULL
                        AND [ConsumedAuthorizationHash] IS NOT NULL
                        AND [ConsumedDeviceSystem] IS NOT NULL
                        AND [ConsumptionKind] IS NOT NULL
                        AND [ConsumptionKind] IN ('Initial', 'Rebind')
                        AND (([ConsumptionKind] = 'Initial'
                                AND [PreviousStoreCode] IS NULL
                                AND [PreviousDeviceCode] IS NULL)
                            OR ([ConsumptionKind] = 'Rebind'
                                AND [PreviousStoreCode] IS NOT NULL
                                AND [PreviousDeviceCode] IS NOT NULL)))
                );
            ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                CHECK CONSTRAINT [CKxPOSMxDeviceActivationGrantxConsumption];

            ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                DROP CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption];
            ALTER TABLE [dbo].[POSM_DeviceActivationGrant] WITH CHECK
                ADD CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption] CHECK
                (
                    ([ConsumedAtUtc] IS NULL OR [ConsumedAtUtc] IS NOT NULL)
                    AND ([ConsumedHardwareId] IS NULL OR [ConsumedHardwareId] IS NOT NULL)
                    AND ([ConsumedDeviceCode] IS NULL OR [ConsumedDeviceCode] IS NOT NULL)
                    AND ([ConsumedDeviceRegistrationId] IS NULL OR [ConsumedDeviceRegistrationId] IS NOT NULL)
                    AND ([ConsumedAuthorizationHash] IS NULL OR [ConsumedAuthorizationHash] IS NOT NULL)
                    AND ([ConsumedDeviceSystem] IS NULL OR [ConsumedDeviceSystem] IS NOT NULL)
                    AND ([ConsumptionKind] IS NULL OR [ConsumptionKind] IS NOT NULL)
                    AND ([PreviousStoreCode] IS NULL OR [PreviousStoreCode] IS NOT NULL)
                    AND ([PreviousDeviceCode] IS NULL OR [PreviousDeviceCode] IS NOT NULL)
                );
            ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                CHECK CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption];
            """);
        try
        {
            var exception = await Assert.ThrowsAsync<SqlException>(() =>
                fixture.ExecutePosmAsync(
                    BlazorApp.Shared.Models.POSM.DeviceActivationCodeSchema.EnsureSql));

            Assert.Equal(51013, exception.Number);
            Assert.Equal(
                0,
                await fixture.ScalarIntAsync(
                    "SELECT COUNT(1) FROM [dbo].[POSM_DeviceActivationGrant];"));
        }
        finally
        {
            await fixture.ExecutePosmAsync(
                """
                IF EXISTS (
                    SELECT 1
                    FROM sys.check_constraints
                    WHERE [parent_object_id] = OBJECT_ID(N'dbo.POSM_DeviceActivationGrant')
                      AND [name] = N'CKxPOSMxDeviceActivationGrantxConsumption')
                BEGIN
                    ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                        DROP CONSTRAINT [CKxPOSMxDeviceActivationGrantxConsumption];
                END;

                ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                    DROP CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption];
                ALTER TABLE [dbo].[POSM_DeviceActivationGrant] WITH CHECK
                    ADD CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption] CHECK
                    (
                        ([ConsumedAtUtc] IS NULL
                            AND [ConsumedHardwareId] IS NULL
                            AND [ConsumedDeviceCode] IS NULL
                            AND [ConsumedDeviceRegistrationId] IS NULL
                            AND [ConsumedAuthorizationHash] IS NULL
                            AND [ConsumedDeviceSystem] IS NULL
                            AND [ConsumptionKind] IS NULL
                            AND [PreviousStoreCode] IS NULL
                            AND [PreviousDeviceCode] IS NULL)
                        OR
                        ([ConsumedAtUtc] IS NOT NULL
                            AND [ConsumedHardwareId] IS NOT NULL
                            AND [ConsumedDeviceCode] IS NOT NULL
                            AND [ConsumedDeviceRegistrationId] IS NOT NULL
                            AND [ConsumedAuthorizationHash] IS NOT NULL
                            AND [ConsumedDeviceSystem] IS NOT NULL
                            AND [ConsumptionKind] IS NOT NULL
                            AND [ConsumptionKind] IN ('Initial', 'Rebind')
                            AND (([ConsumptionKind] = 'Initial'
                                    AND [PreviousStoreCode] IS NULL
                                    AND [PreviousDeviceCode] IS NULL)
                                OR ([ConsumptionKind] = 'Rebind'
                                    AND [PreviousStoreCode] IS NOT NULL
                                    AND [PreviousDeviceCode] IS NOT NULL)))
                    );
                ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                    CHECK CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption];
                """);
        }
    }

    [DeviceActivationSqlServerFact]
    public async Task InitialOnlyConsumptionConstraintFailsClosedDuringPositiveRebindProbe()
    {
        await fixture.ResetAsync();
        await fixture.ExecutePosmAsync(
            """
            ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                DROP CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption];
            ALTER TABLE [dbo].[POSM_DeviceActivationGrant] WITH CHECK
                ADD CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption] CHECK
                (
                    ([ConsumedAtUtc] IS NULL
                        AND [ConsumedHardwareId] IS NULL
                        AND [ConsumedDeviceCode] IS NULL
                        AND [ConsumedDeviceRegistrationId] IS NULL
                        AND [ConsumedAuthorizationHash] IS NULL
                        AND [ConsumedDeviceSystem] IS NULL
                        AND [ConsumptionKind] IS NULL
                        AND [PreviousStoreCode] IS NULL
                        AND [PreviousDeviceCode] IS NULL)
                    OR
                    ([ConsumedAtUtc] IS NOT NULL
                        AND [ConsumedHardwareId] IS NOT NULL
                        AND [ConsumedDeviceCode] IS NOT NULL
                        AND [ConsumedDeviceRegistrationId] IS NOT NULL
                        AND [ConsumedAuthorizationHash] IS NOT NULL
                        AND [ConsumedDeviceSystem] IS NOT NULL
                        AND [ConsumptionKind] IS NOT NULL
                        AND [ConsumptionKind] = 'Initial'
                        AND [PreviousStoreCode] IS NULL
                        AND [PreviousDeviceCode] IS NULL)
                );
            ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                CHECK CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption];
            """);
        try
        {
            var exception = await Assert.ThrowsAsync<SqlException>(() =>
                fixture.ExecutePosmAsync(
                    BlazorApp.Shared.Models.POSM.DeviceActivationCodeSchema.EnsureSql));

            Assert.Equal(51014, exception.Number);
            Assert.Equal(
                0,
                await fixture.ScalarIntAsync(
                    "SELECT COUNT(1) FROM [dbo].[POSM_DeviceActivationGrant];"));
        }
        finally
        {
            await fixture.ExecutePosmAsync(
                """
                ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                    DROP CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption];
                ALTER TABLE [dbo].[POSM_DeviceActivationGrant] WITH CHECK
                    ADD CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption] CHECK
                    (
                        ([ConsumedAtUtc] IS NULL
                            AND [ConsumedHardwareId] IS NULL
                            AND [ConsumedDeviceCode] IS NULL
                            AND [ConsumedDeviceRegistrationId] IS NULL
                            AND [ConsumedAuthorizationHash] IS NULL
                            AND [ConsumedDeviceSystem] IS NULL
                            AND [ConsumptionKind] IS NULL
                            AND [PreviousStoreCode] IS NULL
                            AND [PreviousDeviceCode] IS NULL)
                        OR
                        ([ConsumedAtUtc] IS NOT NULL
                            AND [ConsumedHardwareId] IS NOT NULL
                            AND [ConsumedDeviceCode] IS NOT NULL
                            AND [ConsumedDeviceRegistrationId] IS NOT NULL
                            AND [ConsumedAuthorizationHash] IS NOT NULL
                            AND [ConsumedDeviceSystem] IS NOT NULL
                            AND [ConsumptionKind] IS NOT NULL
                            AND [ConsumptionKind] IN ('Initial', 'Rebind')
                            AND (([ConsumptionKind] = 'Initial'
                                    AND [PreviousStoreCode] IS NULL
                                    AND [PreviousDeviceCode] IS NULL)
                                OR ([ConsumptionKind] = 'Rebind'
                                    AND [PreviousStoreCode] IS NOT NULL
                                    AND [PreviousDeviceCode] IS NOT NULL)))
                    );
                ALTER TABLE [dbo].[POSM_DeviceActivationGrant]
                    CHECK CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption];
                """);
        }
    }

    private static string ReadConsumptionConstraintMigrationSql(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFilePath)!,
            "..",
            "..",
            "..",
            "..",
            "services",
            "backend",
            "SqlScripts",
            "MigrateDeviceActivationGrantConsumptionConstraint.sql")));

    [DeviceActivationSqlServerTheory]
    [InlineData(DeviceSystems.Windows)]
    [InlineData(DeviceSystems.IpadOs)]
    [InlineData(DeviceSystems.Android)]
    [InlineData(DeviceSystems.Ios)]
    public async Task PreviewAndInitialRedeemSupportEveryApprovedPlatform(string deviceSystem)
    {
        await fixture.ResetAsync();
        var material = await fixture.SeedGrantAsync(deviceSystem: deviceSystem);
        var (service, context) = fixture.CreateService();
        try
        {
            var preview = await service.PreviewAsync(
                new DeviceActivationCodePreviewRequest(material.ActivationCode, deviceSystem),
                CancellationToken.None);
            var mismatchedPreview = await service.PreviewAsync(
                new DeviceActivationCodePreviewRequest(
                    material.ActivationCode,
                    "Plan9"),
                CancellationToken.None);
            var unknownCodeWithInvalidPlatform = await service.PreviewAsync(
                new DeviceActivationCodePreviewRequest(
                    DeviceActivationCodeCodec.Create().ActivationCode,
                    "Plan9"),
                CancellationToken.None);
            var redeemed = await service.RedeemAsync(
                new DeviceActivationCodeRedeemRequest(
                    material.ActivationCode,
                    $"HW-{deviceSystem}",
                    null,
                    deviceSystem),
                CancellationToken.None);

            Assert.True(preview.IsAllowed);
            Assert.Equal(deviceSystem, preview.DeviceSystem);
            Assert.Equal(DateTimeKind.Utc, preview.ExpiresAtUtc!.Value.Kind);
            Assert.False(mismatchedPreview.IsAllowed);
            Assert.Equal(
                DeviceActivationReasonCodes.PlatformMismatch,
                mismatchedPreview.ReasonCode);
            Assert.False(unknownCodeWithInvalidPlatform.IsAllowed);
            Assert.Equal(
                DeviceActivationReasonCodes.NotAvailable,
                unknownCodeWithInvalidPlatform.ReasonCode);
            Assert.True(redeemed.IsAllowed);
            Assert.Equal(DeviceActivationReasonCodes.Activated, redeemed.ReasonCode);
            Assert.Equal(
                32,
                await fixture.ScalarIntAsync(
                    "SELECT DATALENGTH([ConsumedAuthorizationHash]) FROM [dbo].[POSM_DeviceActivationGrant] WHERE [GrantId] = @GrantId;",
                    new SqlParameter("@GrantId", material.GrantId)));
        }
        finally
        {
            Dispose(context);
        }
    }

    [DeviceActivationSqlServerFact]
    public async Task PreviewAndRedeemRedactReservedCodeFromHistoricalStoreName()
    {
        await fixture.ResetAsync();
        var reservedCode = DeviceActivationCodeCodec.Create().ActivationCode;
        await fixture.ExecuteMainAsync(
            "UPDATE [dbo].[Store] SET [StoreName] = @StoreName WHERE [StoreCode] = 'S002';",
            new SqlParameter("@StoreName", $"Store {reservedCode}"));
        var material = await fixture.SeedGrantAsync();
        var (service, context) = fixture.CreateService();
        try
        {
            var preview = await service.PreviewAsync(
                new DeviceActivationCodePreviewRequest(
                    material.ActivationCode,
                    DeviceSystems.Windows),
                CancellationToken.None);
            var redeemed = await service.RedeemAsync(
                new DeviceActivationCodeRedeemRequest(
                    material.ActivationCode,
                    "HW-STORE-NAME-REDACTION",
                    null,
                    DeviceSystems.Windows),
                CancellationToken.None);

            Assert.True(preview.IsAllowed);
            Assert.Equal("[REDACTED]", preview.StoreName);
            Assert.True(redeemed.IsAllowed);
            Assert.Equal("[REDACTED]", redeemed.StoreName);
        }
        finally
        {
            Dispose(context);
            await fixture.ExecuteMainAsync(
                "UPDATE [dbo].[Store] SET [StoreName] = N'Target store' WHERE [StoreCode] = 'S002';");
        }
    }

    [DeviceActivationSqlServerFact]
    public async Task ExistingNvarchar500Remark_CannotOverflowDuringActivation()
    {
        await fixture.ResetAsync();
        var material = await fixture.SeedGrantAsync();
        var registrationId = await fixture.SeedDeviceAsync(
            "HW-REMARK",
            "POS-S002-REMARK",
            "S002",
            -1,
            "PENDING-AUTH");
        await fixture.ExecutePosmAsync(
            "UPDATE [dbo].[POSM_设备注册信息表] SET [备注] = REPLICATE(N'X', 500) WHERE [ID] = @Id;",
            new SqlParameter("@Id", registrationId));
        var (service, context) = fixture.CreateService();
        try
        {
            var result = await service.RedeemAsync(
                new DeviceActivationCodeRedeemRequest(
                    material.ActivationCode,
                    "HW-REMARK",
                    new string('T', 200),
                    DeviceSystems.Windows),
                CancellationToken.None);

            Assert.True(result.IsAllowed);
            Assert.Equal(
                500,
                await fixture.ScalarIntAsync(
                    "SELECT LEN([备注]) FROM [dbo].[POSM_设备注册信息表] WHERE [ID] = @Id;",
                    new SqlParameter("@Id", registrationId)));
            var remark = await fixture.ScalarStringAsync(
                "SELECT [备注] FROM [dbo].[POSM_设备注册信息表] WHERE [ID] = @Id;",
                new SqlParameter("@Id", registrationId));
            Assert.Contains("Activated by one-time device activation code", remark);
            Assert.EndsWith(new string('T', 200), remark, StringComparison.Ordinal);
        }
        finally
        {
            Dispose(context);
        }
    }

    [DeviceActivationSqlServerFact]
    public async Task RecoveryOnlyRedeemRejectsAvailableGrantWithoutAnyWrite()
    {
        await fixture.ResetAsync();
        var material = await fixture.SeedGrantAsync();
        var (service, context) = fixture.CreateService();
        try
        {
            var result = await service.RedeemAsync(
                new DeviceActivationCodeRedeemRequest(
                    material.ActivationCode,
                    "HW-RECOVERY-ONLY",
                    null,
                    DeviceSystems.Windows),
                recoveryOnly: true,
                CancellationToken.None);

            Assert.False(result.IsAllowed);
            Assert.Equal(DeviceActivationReasonCodes.NotAvailable, result.ReasonCode);
            Assert.Equal(
                0,
                await fixture.ScalarIntAsync(
                    "SELECT COUNT(1) FROM [dbo].[POSM_DeviceActivationGrant] WHERE [GrantId] = @GrantId AND [ConsumedAtUtc] IS NOT NULL;",
                    new SqlParameter("@GrantId", material.GrantId)));
            Assert.Equal(
                0,
                await fixture.ScalarIntAsync(
                    "SELECT COUNT(1) FROM [dbo].[POSM_设备注册信息表] WHERE [设备硬件识别码] = 'HW-RECOVERY-ONLY';"));
        }
        finally
        {
            Dispose(context);
        }
    }

    [DeviceActivationSqlServerFact]
    public async Task InitialConsumeAndDeviceWriteCommitTogetherAndRollBackTogether()
    {
        await fixture.ResetAsync();
        var material = await fixture.SeedGrantAsync();
        var (service, context) = fixture.CreateService();
        try
        {
            var result = await service.RedeemAsync(
                new DeviceActivationCodeRedeemRequest(
                    material.ActivationCode,
                    "HW-ATOMIC",
                    "Counter atomic",
                    DeviceSystems.Windows),
                CancellationToken.None);

            Assert.True(result.IsAllowed);
            Assert.Equal(
                1,
                await fixture.ScalarIntAsync(
                    "SELECT COUNT(1) FROM [dbo].[POSM_DeviceActivationGrant] WHERE [GrantId] = @GrantId AND [ConsumedAtUtc] IS NOT NULL;",
                    new SqlParameter("@GrantId", material.GrantId)));
            Assert.Equal(
                1,
                await fixture.ScalarIntAsync(
                    "SELECT COUNT(1) FROM [dbo].[POSM_设备注册信息表] WHERE [设备硬件识别码] = 'HW-ATOMIC' AND [设备状态] = 1;"));
        }
        finally
        {
            Dispose(context);
        }

        await fixture.ResetAsync();
        material = await fixture.SeedGrantAsync();
        await fixture.ExecutePosmAsync(
            """
            CREATE TRIGGER [dbo].[TR_DeviceActivationGrantConsumeFailure]
            ON [dbo].[POSM_DeviceActivationGrant]
            AFTER UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted WHERE [ConsumedAtUtc] IS NOT NULL)
                    THROW 52001, 'Injected consume failure.', 1;
            END;
            """);
        (service, context) = fixture.CreateService();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => service.RedeemAsync(
                new DeviceActivationCodeRedeemRequest(
                    material.ActivationCode,
                    "HW-ROLLBACK",
                    null,
                    DeviceSystems.Windows),
                CancellationToken.None));
        }
        finally
        {
            Dispose(context);
        }
        Assert.Equal(
            0,
            await fixture.ScalarIntAsync(
                "SELECT COUNT(1) FROM [dbo].[POSM_设备注册信息表] WHERE [设备硬件识别码] = 'HW-ROLLBACK';"));
        Assert.Equal(
            0,
            await fixture.ScalarIntAsync(
                "SELECT COUNT(1) FROM [dbo].[POSM_DeviceActivationGrant] WHERE [GrantId] = @GrantId AND [ConsumedAtUtc] IS NOT NULL;",
                new SqlParameter("@GrantId", material.GrantId)));
    }

    [DeviceActivationSqlServerFact]
    public async Task RebindDisablesOldEnablesTargetAndConsumesGrantAtomically()
    {
        await fixture.ResetAsync();
        var material = await fixture.SeedGrantAsync("S002");
        var targetId = await fixture.SeedDeviceAsync(
            "HW-REBIND",
            "POS-S002-PENDING",
            "S002",
            -1,
            "PENDING-AUTH");
        var sourceId = await fixture.SeedDeviceAsync(
            "HW-REBIND",
            "POS-S001-OLD",
            "S001",
            1,
            "OLD-AUTH");
        await fixture.ExecutePosmAsync(
            "UPDATE [dbo].[POSM_设备注册信息表] SET [备注] = REPLICATE(N'X', 500) WHERE [ID] = @Id;",
            new SqlParameter("@Id", sourceId));
        var otherPendingId = await fixture.SeedDeviceAsync(
            "HW-REBIND",
            "POS-S003-PENDING",
            "S003",
            -1,
            "OTHER-PENDING-AUTH");
        await fixture.ExecutePosmAsync(
            "UPDATE [dbo].[POSM_设备注册信息表] SET [备注] = REPLICATE(N'X', 500) WHERE [ID] = @Id;",
            new SqlParameter("@Id", otherPendingId));
        var (service, context) = fixture.CreateService();
        try
        {
            var result = await service.RebindAsync(
                new DeviceActivationCodeRebindRequest(material.ActivationCode, "Moved counter"),
                new DeviceActivationRebindContext(
                    "POS-S001-OLD",
                    "S001",
                    "HW-REBIND",
                    DeviceSystems.Windows),
                CancellationToken.None);

            Assert.True(result.IsAllowed);
            Assert.Equal("POS-S002-PENDING", result.DeviceCode);
            Assert.Equal(
                0,
                await fixture.ScalarIntAsync(
                    "SELECT [设备状态] FROM [dbo].[POSM_设备注册信息表] WHERE [系统设备编号] = 'POS-S001-OLD';"));
            Assert.Contains(
                "Rebound to S002",
                await fixture.ScalarStringAsync(
                    "SELECT [备注] FROM [dbo].[POSM_设备注册信息表] WHERE [ID] = @Id;",
                    new SqlParameter("@Id", sourceId)));
            Assert.Contains(
                "Disabled by activation switch to S002",
                await fixture.ScalarStringAsync(
                    "SELECT [备注] FROM [dbo].[POSM_设备注册信息表] WHERE [ID] = @Id;",
                    new SqlParameter("@Id", otherPendingId)));
            Assert.Equal(
                1,
                await fixture.ScalarIntAsync(
                    "SELECT [设备状态] FROM [dbo].[POSM_设备注册信息表] WHERE [ID] = @Id;",
                    new SqlParameter("@Id", targetId)));
            Assert.Equal(
                "S001|POS-S001-OLD|Rebind",
                await fixture.ScalarStringAsync(
                    "SELECT CONCAT([PreviousStoreCode], '|', [PreviousDeviceCode], '|', [ConsumptionKind]) FROM [dbo].[POSM_DeviceActivationGrant] WHERE [GrantId] = @GrantId;",
                    new SqlParameter("@GrantId", material.GrantId)));
        }
        finally
        {
            Dispose(context);
        }
    }

    [DeviceActivationSqlServerFact]
    public async Task SameCodeConcurrentDifferentHardwareOnlyOneConsumes()
    {
        await fixture.ResetAsync();
        var material = await fixture.SeedGrantAsync();
        var first = fixture.CreateService();
        var second = fixture.CreateService();
        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = new[]
            {
                RedeemAfterGate(first.Service, material.ActivationCode, "HW-RACE-1", gate.Task),
                RedeemAfterGate(second.Service, material.ActivationCode, "HW-RACE-2", gate.Task),
            };
            gate.SetResult();
            var results = await Task.WhenAll(tasks);

            Assert.Single(results, item => item.IsAllowed);
            Assert.Single(results, item =>
                item.ReasonCode == DeviceActivationReasonCodes.NotAvailable);
            Assert.Equal(
                1,
                await fixture.ScalarIntAsync(
                    "SELECT COUNT(1) FROM [dbo].[POSM_DeviceActivationGrant] WHERE [ConsumedAtUtc] IS NOT NULL;"));
            Assert.Equal(
                1,
                await fixture.ScalarIntAsync(
                    "SELECT COUNT(1) FROM [dbo].[POSM_设备注册信息表] WHERE [设备状态] = 1;"));
        }
        finally
        {
            Dispose(first.Context);
            Dispose(second.Context);
        }
    }

    [DeviceActivationSqlServerFact]
    public async Task SameHardwareConcurrentDifferentCodesOnlyOneConsumes()
    {
        await fixture.ResetAsync();
        var firstCode = await fixture.SeedGrantAsync("S001");
        var secondCode = await fixture.SeedGrantAsync("S002");
        var first = fixture.CreateService();
        var second = fixture.CreateService();
        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = new[]
            {
                RedeemAfterGate(first.Service, firstCode.ActivationCode, "HW-ONE", gate.Task),
                RedeemAfterGate(second.Service, secondCode.ActivationCode, "HW-ONE", gate.Task),
            };
            gate.SetResult();
            var results = await Task.WhenAll(tasks);

            Assert.Single(results, item => item.IsAllowed);
            Assert.Single(results, item =>
                item.ReasonCode == DeviceActivationReasonCodes.DeviceConflict);
            Assert.Equal(
                1,
                await fixture.ScalarIntAsync(
                    "SELECT COUNT(1) FROM [dbo].[POSM_DeviceActivationGrant] WHERE [ConsumedAtUtc] IS NOT NULL;"));
            Assert.Equal(
                1,
                await fixture.ScalarIntAsync(
                    "SELECT COUNT(1) FROM [dbo].[POSM_设备注册信息表] WHERE [设备硬件识别码] = 'HW-ONE' AND [设备状态] = 1;"));
        }
        finally
        {
            Dispose(first.Context);
            Dispose(second.Context);
        }
    }

    [DeviceActivationSqlServerFact]
    public async Task ExpiryDuringDeviceWriteRollsBackEveryPartialWrite()
    {
        await fixture.ResetAsync();
        var material = await fixture.SeedGrantAsync();
        await fixture.ExecutePosmAsync(
            """
            CREATE TRIGGER [dbo].[TR_DeviceActivationExpireDuringWrite]
            ON [dbo].[POSM_设备注册信息表]
            AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
                UPDATE [dbo].[POSM_DeviceActivationGrant]
                SET [ExpiresAtUtc] = DATEADD(millisecond, 1, [CreatedAtUtc])
                WHERE [ConsumedAtUtc] IS NULL AND [RevokedAtUtc] IS NULL;
            END;
            """);
        var (service, context) = fixture.CreateService();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RedeemAsync(
                new DeviceActivationCodeRedeemRequest(
                    material.ActivationCode,
                    "HW-EXPIRY",
                    null,
                    DeviceSystems.Windows),
                CancellationToken.None));
        }
        finally
        {
            Dispose(context);
        }

        Assert.Equal(
            0,
            await fixture.ScalarIntAsync(
                "SELECT COUNT(1) FROM [dbo].[POSM_设备注册信息表] WHERE [设备硬件识别码] = 'HW-EXPIRY';"));
        Assert.Equal(
            0,
            await fixture.ScalarIntAsync(
                "SELECT COUNT(1) FROM [dbo].[POSM_DeviceActivationGrant] WHERE [GrantId] = @GrantId AND [ConsumedAtUtc] IS NOT NULL;",
                new SqlParameter("@GrantId", material.GrantId)));
    }

    [DeviceActivationSqlServerFact]
    public async Task ConcurrentRevokeAndRedeemSerializeWithoutPartialState()
    {
        await fixture.ResetAsync();
        var material = await fixture.SeedGrantAsync();
        var (service, context) = fixture.CreateService();
        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var redeemTask = RedeemAfterGate(
                service,
                material.ActivationCode,
                "HW-REVOKE-RACE",
                gate.Task);
            var revokeTask = Task.Run(async () =>
            {
                await gate.Task;
                await using var connection = new SqlConnection(fixture.PosmConnectionString);
                await connection.OpenAsync();
                await using var command = new SqlCommand(
                    """
                    UPDATE [dbo].[POSM_DeviceActivationGrant]
                    SET [RevokedAtUtc] = SYSUTCDATETIME(),
                        [RevokedBy] = N'SQL_TEST',
                        [RevokeReason] = N'Race'
                    WHERE [GrantId] = @GrantId
                      AND [RevokedAtUtc] IS NULL
                      AND [ConsumedAtUtc] IS NULL;
                    """,
                    connection);
                command.Parameters.AddWithValue("@GrantId", material.GrantId);
                return await command.ExecuteNonQueryAsync();
            });
            gate.SetResult();
            var redeem = await redeemTask;
            var revokedRows = await revokeTask;
            var consumed = await fixture.ScalarIntAsync(
                "SELECT COUNT(1) FROM [dbo].[POSM_DeviceActivationGrant] WHERE [GrantId] = @GrantId AND [ConsumedAtUtc] IS NOT NULL;",
                new SqlParameter("@GrantId", material.GrantId));
            var revoked = await fixture.ScalarIntAsync(
                "SELECT COUNT(1) FROM [dbo].[POSM_DeviceActivationGrant] WHERE [GrantId] = @GrantId AND [RevokedAtUtc] IS NOT NULL;",
                new SqlParameter("@GrantId", material.GrantId));
            var devices = await fixture.ScalarIntAsync(
                "SELECT COUNT(1) FROM [dbo].[POSM_设备注册信息表] WHERE [设备硬件识别码] = 'HW-REVOKE-RACE';");

            Assert.True(
                (redeem.IsAllowed && consumed == 1 && revoked == 0 && revokedRows == 0 && devices == 1)
                || (!redeem.IsAllowed && consumed == 0 && revoked == 1 && revokedRows == 1 && devices == 0));
        }
        finally
        {
            Dispose(context);
        }
    }

    [DeviceActivationSqlServerFact]
    public async Task InitialAndRebindRecoveryRequireExactHardwareAndPreviousIdentity()
    {
        await fixture.ResetAsync();
        var initial = await fixture.SeedGrantAsync();
        var first = fixture.CreateService();
        try
        {
            var activated = await first.Service.RedeemAsync(
                new DeviceActivationCodeRedeemRequest(
                    initial.ActivationCode,
                    "HW-RECOVER",
                    null,
                    DeviceSystems.Windows),
                CancellationToken.None);
            var recovered = await first.Service.RedeemAsync(
                new DeviceActivationCodeRedeemRequest(
                    initial.ActivationCode,
                    "HW-RECOVER",
                    null,
                    DeviceSystems.Windows),
                recoveryOnly: true,
                CancellationToken.None);
            var rejected = await first.Service.RedeemAsync(
                new DeviceActivationCodeRedeemRequest(
                    initial.ActivationCode,
                    "HW-OTHER",
                    null,
                    DeviceSystems.Windows),
                recoveryOnly: true,
                CancellationToken.None);

            await fixture.ExecutePosmAsync(
                """
                UPDATE target
                SET target.[设备授权码] = 'ROTATED-LATER-AUTH'
                FROM [dbo].[POSM_设备注册信息表] AS target
                INNER JOIN [dbo].[POSM_DeviceActivationGrant] AS [grant]
                    ON [grant].[ConsumedDeviceRegistrationId] = target.[ID]
                WHERE [grant].[GrantId] = @GrantId;
                """,
                new SqlParameter("@GrantId", initial.GrantId));
            var rotatedCredentialRejected = await first.Service.RedeemAsync(
                new DeviceActivationCodeRedeemRequest(
                    initial.ActivationCode,
                    "HW-RECOVER",
                    null,
                    DeviceSystems.Windows),
                recoveryOnly: true,
                CancellationToken.None);

            Assert.True(activated.IsAllowed);
            Assert.Equal(DeviceActivationReasonCodes.ActivationRecovered, recovered.ReasonCode);
            Assert.Equal(activated.AuthorizationCode, recovered.AuthorizationCode);
            Assert.Equal(DeviceActivationReasonCodes.NotAvailable, rejected.ReasonCode);
            Assert.Equal(
                DeviceActivationReasonCodes.NotAvailable,
                rotatedCredentialRejected.ReasonCode);
        }
        finally
        {
            Dispose(first.Context);
        }

        await fixture.ResetAsync();
        var rebind = await fixture.SeedGrantAsync("S002");
        await fixture.SeedDeviceAsync(
            "HW-RECOVER",
            "POS-S001-OLD",
            "S001",
            1,
            "OLD-AUTH");
        var second = fixture.CreateService();
        try
        {
            var activated = await second.Service.RebindAsync(
                new DeviceActivationCodeRebindRequest(rebind.ActivationCode, null),
                new DeviceActivationRebindContext(
                    "POS-S001-OLD",
                    "S001",
                    "HW-RECOVER",
                    DeviceSystems.Windows),
                CancellationToken.None);
            var recovered = await second.Service.RebindAsync(
                new DeviceActivationCodeRebindRequest(rebind.ActivationCode, null),
                new DeviceActivationRebindContext(
                    "POS-S001-OLD",
                    "S001",
                    "HW-RECOVER",
                    DeviceSystems.Windows),
                CancellationToken.None);
            var anonymousRecovered = await second.Service.RedeemAsync(
                new DeviceActivationCodeRedeemRequest(
                    rebind.ActivationCode,
                    "HW-RECOVER",
                    null,
                    DeviceSystems.Windows),
                recoveryOnly: true,
                CancellationToken.None);
            var rejected = await second.Service.RebindAsync(
                new DeviceActivationCodeRebindRequest(rebind.ActivationCode, null),
                new DeviceActivationRebindContext(
                    "POS-S001-OLD",
                    "S001",
                    "HW-OTHER",
                    DeviceSystems.Windows),
                CancellationToken.None);
            var recoveryAuthorization = new DeviceActivationRecoveryAuthorizationService(
                second.Context);
            var oldIdentity = await recoveryAuthorization.TryAuthorizePreviousDeviceAsync(
                "OLD-AUTH",
                "POS-S001-OLD",
                "S001",
                "HW-RECOVER",
                CancellationToken.None);
            await fixture.ExecutePosmAsync(
                """
                UPDATE target
                SET target.[设备授权码] = 'ROTATED-REBIND-AUTH'
                FROM [dbo].[POSM_设备注册信息表] AS target
                INNER JOIN [dbo].[POSM_DeviceActivationGrant] AS [grant]
                    ON [grant].[ConsumedDeviceRegistrationId] = target.[ID]
                WHERE [grant].[GrantId] = @GrantId;
                """,
                new SqlParameter("@GrantId", rebind.GrantId));
            var rotatedCredentialRejected = await second.Service.RedeemAsync(
                new DeviceActivationCodeRedeemRequest(
                    rebind.ActivationCode,
                    "HW-RECOVER",
                    null,
                    DeviceSystems.Windows),
                recoveryOnly: true,
                CancellationToken.None);

            Assert.True(activated.IsAllowed);
            Assert.Equal(DeviceActivationReasonCodes.ActivationRecovered, recovered.ReasonCode);
            Assert.Equal(activated.AuthorizationCode, recovered.AuthorizationCode);
            Assert.Equal(DeviceActivationReasonCodes.ActivationRecovered, anonymousRecovered.ReasonCode);
            Assert.Equal(activated.AuthorizationCode, anonymousRecovered.AuthorizationCode);
            Assert.Equal(DeviceActivationReasonCodes.NotAvailable, rejected.ReasonCode);
            Assert.Equal(
                DeviceActivationReasonCodes.NotAvailable,
                rotatedCredentialRejected.ReasonCode);
            Assert.Null(oldIdentity.FailureCode);
            Assert.NotNull(oldIdentity.Device);
            Assert.False(oldIdentity.Device.AllowTransactions);
        }
        finally
        {
            Dispose(second.Context);
        }
    }

    private static async Task<DeviceActivationCodeRedeemResponse> RedeemAfterGate(
        DeviceActivationCodeService service,
        string activationCode,
        string hardwareId,
        Task gate)
    {
        await gate;
        return await service.RedeemAsync(
            new DeviceActivationCodeRedeemRequest(
                activationCode,
                hardwareId,
                null,
                DeviceSystems.Windows),
            CancellationToken.None);
    }

    private static void Dispose(HbposSqlSugarContext context)
    {
        context.MainDb.Dispose();
        context.PosmDb.Dispose();
    }
}
