using Microsoft.Data.SqlClient;

namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// RustDesk 远程维护增量 schema 的显式入口。正常 HTTP 启动不调用此类，避免隐式写生产库。
/// </summary>
internal sealed class RemoteMaintenanceSchemaMigrator(IConfiguration configuration)
{
    internal const string MigrationId = "20260906.001-remote-maintenance";

    private readonly string _connectionString =
        configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
    private readonly int _commandTimeoutSeconds = Math.Clamp(
        configuration.GetValue("Database:CommandTimeoutSeconds", 60), 1, 1800);

    internal async Task<SchemaOperationResult> MigrateAsync(CancellationToken cancellationToken)
    {
        try
        {
            EnsureSqlServerConnection();
            await SqlServerSchemaMigrationStore.ExecuteBatchAsync(
                _connectionString, ApplySql, _commandTimeoutSeconds, cancellationToken);
            await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
                _connectionString, VerifySql, _commandTimeoutSeconds, cancellationToken);
            return SchemaOperationResult.MigrationSucceeded();
        }
        catch (OperationCanceledException)
        {
            return SchemaOperationResult.Failure(SchemaExitCodes.Cancelled, SchemaDiagnosticCodes.Cancelled);
        }
        catch (SchemaProviderNotSupportedException)
        {
            return SchemaOperationResult.Failure(SchemaExitCodes.ConfigurationError, SchemaDiagnosticCodes.ProviderUnsupported);
        }
        catch (Exception)
        {
            return SchemaOperationResult.Failure(SchemaExitCodes.DatabaseFailure, SchemaDiagnosticCodes.MigrationFailure);
        }
    }

    internal async Task<SchemaOperationResult> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            EnsureSqlServerConnection();
            await SqlServerSchemaMigrationStore.ExecuteReadOnlyBatchAsync(
                _connectionString, VerifySql, _commandTimeoutSeconds, cancellationToken);
            return SchemaOperationResult.Ready();
        }
        catch (OperationCanceledException)
        {
            return SchemaOperationResult.Failure(SchemaExitCodes.Cancelled, SchemaDiagnosticCodes.Cancelled);
        }
        catch (SchemaProviderNotSupportedException)
        {
            return SchemaOperationResult.Failure(SchemaExitCodes.ConfigurationError, SchemaDiagnosticCodes.ProviderUnsupported);
        }
        catch (Exception)
        {
            return SchemaOperationResult.Failure(SchemaExitCodes.SchemaNotReady, SchemaDiagnosticCodes.MainMigrationMissing);
        }
    }

    private void EnsureSqlServerConnection()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)
            || string.IsNullOrWhiteSpace(new SqlConnectionStringBuilder(_connectionString).DataSource))
            throw new SchemaProviderNotSupportedException();
    }

    internal const string ApplySql = """
SET XACT_ABORT ON;
BEGIN TRY
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.HBweb_RemoteMaintenanceDevice', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[HBweb_RemoteMaintenanceDevice]
    (
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_HBweb_RemoteMaintenanceDevice] PRIMARY KEY,
        [DeviceRegistrationId] int NOT NULL,
        [HardwareId] nvarchar(100) NOT NULL,
        [StoreCode] nvarchar(50) NOT NULL,
        [DeviceCode] nvarchar(100) NOT NULL,
        [ComputerName] nvarchar(120) NOT NULL,
        [RustdeskId] nvarchar(120) NULL,
        [ClientVersion] nvarchar(80) NULL,
        [AgentVersion] nvarchar(80) NULL,
        [ServiceStatus] nvarchar(20) NOT NULL CONSTRAINT [DF_HBweb_RemoteMaintenanceDevice_ServiceStatus] DEFAULT N'notInstalled',
        [LastSeenAtUtc] datetime2 NULL,
        [RegisteredAtUtc] datetime2 NOT NULL,
        [LastAcceptedSequence] bigint NOT NULL CONSTRAINT [DF_HBweb_RemoteMaintenanceDevice_LastAcceptedSequence] DEFAULT 0,
        [LastAcceptedAtUtc] datetime2 NULL,
        [LastOperationId] uniqueidentifier NULL,
        [MonitorTokenHash] nvarchar(128) NULL,
        [CredentialCiphertext] nvarchar(2048) NULL,
        [CommitResponseCiphertext] nvarchar(max) NULL,
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_HBweb_RemoteMaintenanceDevice_IsDeleted] DEFAULT 0
    );
END;
IF COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'HardwareId') IS NULL
    ALTER TABLE [dbo].[HBweb_RemoteMaintenanceDevice] ADD [HardwareId] nvarchar(100) NOT NULL CONSTRAINT [DF_HBweb_RemoteMaintenanceDevice_HardwareId] DEFAULT N'';
-- 2026-11-02：心跳实际观察到的 RustDesk 版本（可空列，只改元数据）；用于发现被员工手动升级或程序被隔离的设备。
IF COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'ObservedRustDeskVersion') IS NULL
    ALTER TABLE [dbo].[HBweb_RemoteMaintenanceDevice] ADD [ObservedRustDeskVersion] nvarchar(80) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_HBweb_RemoteMaintenanceDevice_DeviceRegistrationId' AND object_id = OBJECT_ID(N'dbo.HBweb_RemoteMaintenanceDevice'))
    CREATE UNIQUE INDEX [UX_HBweb_RemoteMaintenanceDevice_DeviceRegistrationId] ON [dbo].[HBweb_RemoteMaintenanceDevice]([DeviceRegistrationId]) WHERE [IsDeleted] = 0;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_HBweb_RemoteMaintenanceDevice_StoreCode' AND object_id = OBJECT_ID(N'dbo.HBweb_RemoteMaintenanceDevice'))
    CREATE INDEX [IX_HBweb_RemoteMaintenanceDevice_StoreCode] ON [dbo].[HBweb_RemoteMaintenanceDevice]([StoreCode], [LastSeenAtUtc]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_HBweb_RemoteMaintenanceDevice_LastSeenAtUtc' AND object_id = OBJECT_ID(N'dbo.HBweb_RemoteMaintenanceDevice'))
    CREATE INDEX [IX_HBweb_RemoteMaintenanceDevice_LastSeenAtUtc] ON [dbo].[HBweb_RemoteMaintenanceDevice]([LastSeenAtUtc]);
COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.HBweb_RemoteMaintenanceDevice', N'U') IS NULL
    THROW 51700, N'RemoteMaintenanceDevice table is missing.', 1;
IF COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'HardwareId') IS NULL
    THROW 51701, N'RemoteMaintenanceDevice.HardwareId is missing.', 1;
IF COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'MonitorTokenHash') IS NULL
    THROW 51702, N'RemoteMaintenanceDevice.MonitorTokenHash is missing.', 1;
IF COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'CommitResponseCiphertext') IS NULL
    THROW 51703, N'RemoteMaintenanceDevice.CommitResponseCiphertext is missing.', 1;
IF COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'LastAcceptedSequence') IS NULL
    THROW 51704, N'RemoteMaintenanceDevice.LastAcceptedSequence is missing.', 1;
IF COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'LastAcceptedAtUtc') IS NULL
    THROW 51705, N'RemoteMaintenanceDevice.LastAcceptedAtUtc is missing.', 1;
IF COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'LastOperationId') IS NULL
    THROW 51706, N'RemoteMaintenanceDevice.LastOperationId is missing.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_HBweb_RemoteMaintenanceDevice_DeviceRegistrationId' AND object_id = OBJECT_ID(N'dbo.HBweb_RemoteMaintenanceDevice') AND is_unique = 1)
    THROW 51707, N'RemoteMaintenanceDevice registration index is missing.', 1;
IF COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'ObservedRustDeskVersion') IS NULL
    THROW 51708, N'RemoteMaintenanceDevice.ObservedRustDeskVersion is missing.', 1;
""";
}
