using Hbpos.Api.Services;

namespace Hbpos.Api.Tests;

public sealed class LinklyCloudBackendAsyncSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_executes_idempotent_backend_async_session_and_notification_ddl()
    {
        var executor = new CapturingLinklyCloudBackendAsyncSchemaSqlExecutor();
        var initializer = new SqlSugarLinklyCloudBackendAsyncSchemaInitializer(executor);

        await initializer.InitializeAsync();

        Assert.Single(executor.SqlStatements);
        var sql = executor.SqlStatements[0];
        Assert.Contains("SET XACT_ABORT ON", sql);
        Assert.Contains("BEGIN TRANSACTION", sql);
        Assert.Contains("sys.sp_getapplock", sql);
        Assert.Contains("Hbpos.LinklyCloud.Schema.v2", sql);
        Assert.Contains("@LockOwner = N'Transaction'", sql);
        Assert.Contains("COMMIT TRANSACTION", sql);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U') IS NULL", sql);
        Assert.Contains("[Environment] NVARCHAR(32) NOT NULL", sql);
        Assert.Contains("[StoreCode] NVARCHAR(32) NOT NULL", sql);
        Assert.Contains("[DeviceCode] NVARCHAR(64) NOT NULL", sql);
        Assert.Contains("[SessionId] NVARCHAR(64) NOT NULL", sql);
        Assert.Contains("[TxnRef] NVARCHAR(16) NULL", sql);
        Assert.Contains("[RequestTxnType] NVARCHAR(8) NULL", sql);
        Assert.Contains("[RequestAmountCents] BIGINT NULL", sql);
        Assert.Contains("[RequestRfn] NVARCHAR(128) NULL", sql);
        Assert.Contains("[DisplayText] NVARCHAR(512) NULL", sql);
        Assert.Contains("[DisplayLines] NVARCHAR(MAX) NULL", sql);
        Assert.Contains("[CancelKeyFlag] BIT NOT NULL", sql);
        Assert.Contains("[OKKeyFlag] BIT NOT NULL", sql);
        Assert.Contains("[AcceptYesKeyFlag] BIT NOT NULL", sql);
        Assert.Contains("[DeclineNoKeyFlag] BIT NOT NULL", sql);
        Assert.Contains("[AuthoriseKeyFlag] BIT NOT NULL", sql);
        Assert.Contains("[InputType] NVARCHAR(64) NULL", sql);
        Assert.Contains("[GraphicCode] NVARCHAR(64) NULL", sql);
        Assert.Contains("[ReceiptText] NVARCHAR(MAX) NULL", sql);
        Assert.Contains("[RecoveryCount] INT NOT NULL", sql);
        Assert.Contains("[ReceiptPrintedAt] DATETIME2(7) NULL", sql);
        Assert.Contains("[ClientAcknowledgedAt] DATETIME2(7) NULL", sql);
        Assert.Contains("[LastHttpStatus] INT NULL", sql);
        Assert.Contains("[TransactionSuccess] BIT NULL", sql);
        Assert.Contains("[OperationType] NVARCHAR(32) NOT NULL", sql);
        Assert.Contains("[OperationSuccess] BIT NULL", sql);
        Assert.Contains("[SettlementData] NVARCHAR(MAX) NULL", sql);
        Assert.Contains("[SettlementReceiptTexts] NVARCHAR(MAX) NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'DisplayText') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'RequestTxnType') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'RequestAmountCents') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'RequestRfn') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'DisplayLines') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'CancelKeyFlag') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'OKKeyFlag') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'AcceptYesKeyFlag') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'DeclineNoKeyFlag') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'AuthoriseKeyFlag') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'InputType') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'GraphicCode') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'ReceiptText') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'RecoveryCount') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'ReceiptPrintedAt') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'ClientAcknowledgedAt') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'LastHttpStatus') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'TransactionSuccess') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'OperationType') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'OperationSuccess') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'SettlementData') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'SettlementReceiptTexts') IS NULL", sql);
        // 会话创建时间：新表直接带默认值；升级时先加列再用 UpdatedAt 回填旧行（动态 SQL，避免同批次引用新列）。
        Assert.Contains("[CreatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_CreatedAt] DEFAULT (SYSUTCDATETIME())", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'CreatedAt') IS NULL", sql);
        Assert.Contains("SET [CreatedAt] = [UpdatedAt] WHERE [CreatedAt] IS NULL", sql);
        Assert.Contains("UNIQUE ([Environment], [StoreCode], [DeviceCode], [SessionId])", sql);
        Assert.Contains("UX_POSM_LinklyCloudBackendSession_ActiveTerminal", sql);
        Assert.Contains("UX_POSM_LinklyCloudBackendSession_TxnRef", sql);
        Assert.Contains("[Environment], [StoreCode], [TxnRef]", sql);
        Assert.Contains("WHERE [IsActive] = 1", sql);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendNotification]', N'U') IS NULL", sql);
        Assert.Contains("[PayloadJson] NVARCHAR(MAX) NOT NULL", sql);
        Assert.Contains("IX_POSM_LinklyCloudBackendNotification_Scope", sql);
        Assert.Contains("[Environment], [StoreCode], [DeviceCode], [SessionId]", sql);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendTerminal]', N'U') IS NULL", sql);
        Assert.Contains("[Secret] NVARCHAR(512) NOT NULL", sql);
        Assert.Contains("[PosId] NVARCHAR(64) NOT NULL", sql);
        Assert.Contains("UX_POSM_LinklyCloudBackendTerminal_Scope", sql);
        Assert.Contains("UNIQUE ([Environment], [StoreCode], [DeviceCode])", sql);
        Assert.Contains("[TerminalId] UNIQUEIDENTIFIER NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'TerminalId') IS NULL", sql);
        Assert.Contains("UX_POSM_LinklyCloudBackendSession_ActiveCloudTerminal", sql);
        Assert.Contains("IX_POSM_LinklyCloudBackendSession_DeviceRecovery", sql);
        Assert.Contains("IX_POSM_LinklyCloudBackendSession_TerminalRecovery", sql);
        Assert.Contains("INCLUDE ([UpdatedAt])", sql);
        Assert.Contains("[Environment], [StoreCode], [TerminalId]", sql);
        Assert.Contains("[TerminalId] IS NOT NULL", sql);

        // M35：回调按 (Environment, SessionId) 查会话，必须有以这两列打头的索引，并按名称幂等创建。
        Assert.Contains("[name] = N'IX_POSM_LinklyCloudBackendSession_EnvSession'", sql);
        Assert.Contains("ON [dbo].[POSM_LinklyCloudBackendSession] ([Environment], [SessionId])", sql);

        // H1：迟到最终结果的独立字段，以及官方查询 4xx 计数，都走幂等的 COL_LENGTH 迁移。
        foreach (var column in new[]
                 {
                     "LateFinalAt", "LateFinalTransactionSuccess", "LateFinalResponseCode",
                     "LateFinalResponseText", "OfficialQueryRejectCount"
                 })
        {
            Assert.Contains($"COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'{column}') IS NULL", sql);
        }

        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]', N'U') IS NULL", sql);
        Assert.Contains("CONSTRAINT [PK_POSM_LinklyCloudTerminal] PRIMARY KEY", sql);
        Assert.Contains("[LaneNo] INT NOT NULL", sql);
        Assert.Contains("[DisplayName] NVARCHAR(128) NOT NULL", sql);
        Assert.Contains("[Username] NVARCHAR(128) NOT NULL", sql);
        Assert.Contains("[Password] NVARCHAR(2048) NOT NULL", sql);
        Assert.Contains("[Secret] NVARCHAR(2048) NULL", sql);
        Assert.Contains("[CredentialProtectionVersion] TINYINT NOT NULL", sql);
        Assert.Contains("DF_POSM_LinklyCloudTerminal_CredentialProtectionVersion", sql);
        Assert.Contains("DEFAULT (1)", sql);
        Assert.Contains("CK_POSM_LinklyCloudTerminal_CredentialProtectionVersion", sql);
        Assert.Contains("CHECK ([CredentialProtectionVersion] IN (0, 1))", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'CredentialProtectionVersion') IS NULL", sql);
        Assert.Contains("DEFAULT (0) WITH VALUES", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'Password') < 4096", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'Secret') < 4096", sql);
        Assert.Contains("[PairingState] NVARCHAR(32) NOT NULL", sql);
        Assert.Contains("[PairingAttemptId] UNIQUEIDENTIFIER NULL", sql);
        Assert.Contains("[PairingLeaseExpiresAt] DATETIME2(7) NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'PairingAttemptId') IS NULL", sql);
        Assert.Contains("CK_POSM_LinklyCloudTerminal_Environment", sql);
        Assert.Contains("CK_POSM_LinklyCloudTerminal_PairingState", sql);
        Assert.Contains("UX_POSM_LinklyCloudTerminal_Scope_LaneNo", sql);
        Assert.Contains("UX_POSM_LinklyCloudTerminal_Scope_Username", sql);
        Assert.Contains("UX_POSM_LinklyCloudTerminal_Scope_DisplayName", sql);
        Assert.Contains("CREATE UNIQUE INDEX [UX_POSM_LinklyCloudTerminal_Scope_LaneNo]", sql);
        Assert.Contains("CREATE UNIQUE INDEX [UX_POSM_LinklyCloudTerminal_Scope_Username]", sql);
        Assert.Contains("CREATE UNIQUE INDEX [UX_POSM_LinklyCloudTerminal_Scope_DisplayName]", sql);
        Assert.DoesNotContain("UPDATE [dbo].[POSM_LinklyCloudTerminal]", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO [dbo].[POSM_LinklyCloudTerminal]", sql, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudDeviceSelection]', N'U') IS NULL", sql);
        Assert.Contains("CONSTRAINT [PK_POSM_LinklyCloudDeviceSelection] PRIMARY KEY", sql);
        Assert.Contains("[Revision] BIGINT NOT NULL", sql);
        Assert.Contains("FK_POSM_LinklyCloudDeviceSelection_Terminal", sql);
        Assert.Contains("UX_POSM_LinklyCloudDeviceSelection_Scope_Terminal", sql);
        Assert.Contains("GROUP BY [Environment], [StoreCode], [TerminalId]", sql);
        Assert.Contains("HAVING COUNT_BIG(*) > 1", sql);
        Assert.Contains("THROW 51004", sql);
        var duplicateGuardIndex = sql.IndexOf(
            "GROUP BY [Environment], [StoreCode], [TerminalId]",
            StringComparison.OrdinalIgnoreCase);
        var physicalTerminalIndex = sql.IndexOf(
            "CREATE UNIQUE INDEX [UX_POSM_LinklyCloudDeviceSelection_Scope_Terminal]",
            StringComparison.OrdinalIgnoreCase);
        Assert.True(duplicateGuardIndex >= 0 && physicalTerminalIndex > duplicateGuardIndex);
        Assert.DoesNotContain("DELETE FROM [dbo].[POSM_LinklyCloudDeviceSelection]", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE [dbo].[POSM_LinklyCloudDeviceSelection]", sql, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudConfigurationMode]', N'U') IS NULL", sql);
        Assert.Contains("CONSTRAINT [PK_POSM_LinklyCloudConfigurationMode] PRIMARY KEY", sql);
        Assert.Contains("CK_POSM_LinklyCloudConfigurationMode_Environment", sql);
        Assert.Contains("CK_POSM_LinklyCloudConfigurationMode_Mode", sql);
        Assert.Contains("[LegacyPairingAttemptId] UNIQUEIDENTIFIER NULL", sql);
        Assert.Contains("[LegacyPairingLeaseExpiresAt] DATETIME2(7) NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudConfigurationMode', N'LegacyPairingAttemptId') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudConfigurationMode', N'LegacyPairingLeaseExpiresAt') IS NULL", sql);
    }

    private sealed class CapturingLinklyCloudBackendAsyncSchemaSqlExecutor : ILinklyCloudBackendAsyncSchemaSqlExecutor
    {
        public List<string> SqlStatements { get; } = [];

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            SqlStatements.Add(sql);
            return Task.CompletedTask;
        }
    }
}
