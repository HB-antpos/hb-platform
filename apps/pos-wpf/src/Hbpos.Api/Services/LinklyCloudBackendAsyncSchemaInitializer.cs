using Hbpos.Api.Data;

namespace Hbpos.Api.Services;

public interface ILinklyCloudBackendAsyncSchemaInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface ILinklyCloudBackendAsyncSchemaSqlExecutor
{
    Task ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}

public sealed class SqlSugarLinklyCloudBackendAsyncSchemaInitializer(
    ILinklyCloudBackendAsyncSchemaSqlExecutor sqlExecutor) : ILinklyCloudBackendAsyncSchemaInitializer
{
    // 异步交易状态和通知都按环境、门店、设备、会话四段 scope 隔离。
    internal const string EnsureTableSql = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        DECLARE @SchemaLockResult INT;
        EXEC @SchemaLockResult = sys.sp_getapplock
            @Resource = N'Hbpos.LinklyCloud.Schema.v2',
            @LockMode = N'Exclusive',
            @LockOwner = N'Transaction',
            @LockTimeout = 60000;
        IF @SchemaLockResult < 0
            THROW 51000, 'Could not acquire the Linkly Cloud backend async schema lock.', 1;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_LinklyCloudBackendSession] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_LinklyCloudBackendSession] PRIMARY KEY,
                [Environment] NVARCHAR(32) NOT NULL,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [DeviceCode] NVARCHAR(64) NOT NULL,
                [TerminalId] UNIQUEIDENTIFIER NULL,
                [SessionId] NVARCHAR(64) NOT NULL,
                [Status] NVARCHAR(32) NOT NULL,
                [TxnRef] NVARCHAR(16) NULL,
                [RequestTxnType] NVARCHAR(8) NULL,
                [RequestAmountCents] BIGINT NULL,
                [RequestRfn] NVARCHAR(128) NULL,
                [TransactionSuccess] BIT NULL,
                [OperationType] NVARCHAR(32) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_OperationType] DEFAULT (N'Transaction'),
                [OperationSuccess] BIT NULL,
                [SettlementData] NVARCHAR(MAX) NULL,
                [SettlementReceiptTexts] NVARCHAR(MAX) NULL,
                [ResponseCode] NVARCHAR(32) NULL,
                [ResponseText] NVARCHAR(512) NULL,
                [RecoveryAction] NVARCHAR(64) NULL,
                [DisplayText] NVARCHAR(512) NULL,
                [DisplayLines] NVARCHAR(MAX) NULL,
                [CancelKeyFlag] BIT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_CancelKeyFlag] DEFAULT (0),
                [OKKeyFlag] BIT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_OKKeyFlag] DEFAULT (0),
                [AcceptYesKeyFlag] BIT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_AcceptYesKeyFlag] DEFAULT (0),
                [DeclineNoKeyFlag] BIT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_DeclineNoKeyFlag] DEFAULT (0),
                [AuthoriseKeyFlag] BIT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_AuthoriseKeyFlag] DEFAULT (0),
                [InputType] NVARCHAR(64) NULL,
                [GraphicCode] NVARCHAR(64) NULL,
                [ReceiptText] NVARCHAR(MAX) NULL,
                [RecoveryCount] INT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_RecoveryCount] DEFAULT (0),
                [ReceiptPrintedAt] DATETIME2(7) NULL,
                [ClientAcknowledgedAt] DATETIME2(7) NULL,
                [LastHttpStatus] INT NULL,
                [IsActive] BIT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_IsActive] DEFAULT (0),
                [CreatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_CreatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                -- 订单同步核对会话后回写的订单号；用于「已批准会话无订单」对账。
                [OrderGuid] NVARCHAR(50) NULL,
                CONSTRAINT [CK_POSM_LinklyCloudBackendSession_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox')),
                CONSTRAINT [UX_POSM_LinklyCloudBackendSession_Scope] UNIQUE ([Environment], [StoreCode], [DeviceCode], [SessionId])
            );
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U') IS NOT NULL
        BEGIN
            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'TerminalId') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [TerminalId] UNIQUEIDENTIFIER NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'RequestTxnType') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [RequestTxnType] NVARCHAR(8) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'RequestAmountCents') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [RequestAmountCents] BIGINT NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'RequestRfn') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [RequestRfn] NVARCHAR(128) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'DisplayText') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [DisplayText] NVARCHAR(512) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'DisplayLines') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [DisplayLines] NVARCHAR(MAX) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'CancelKeyFlag') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [CancelKeyFlag] BIT NOT NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_CancelKeyFlag_Upgrade] DEFAULT (0) WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'OKKeyFlag') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [OKKeyFlag] BIT NOT NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_OKKeyFlag_Upgrade] DEFAULT (0) WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'AcceptYesKeyFlag') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [AcceptYesKeyFlag] BIT NOT NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_AcceptYesKeyFlag_Upgrade] DEFAULT (0) WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'DeclineNoKeyFlag') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [DeclineNoKeyFlag] BIT NOT NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_DeclineNoKeyFlag_Upgrade] DEFAULT (0) WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'AuthoriseKeyFlag') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [AuthoriseKeyFlag] BIT NOT NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_AuthoriseKeyFlag_Upgrade] DEFAULT (0) WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'InputType') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [InputType] NVARCHAR(64) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'GraphicCode') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [GraphicCode] NVARCHAR(64) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'ReceiptText') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [ReceiptText] NVARCHAR(MAX) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'RecoveryCount') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [RecoveryCount] INT NOT NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_RecoveryCount_Upgrade] DEFAULT (0) WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'ReceiptPrintedAt') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [ReceiptPrintedAt] DATETIME2(7) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'ClientAcknowledgedAt') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [ClientAcknowledgedAt] DATETIME2(7) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'LastHttpStatus') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [LastHttpStatus] INT NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'TransactionSuccess') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [TransactionSuccess] BIT NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'OperationType') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [OperationType] NVARCHAR(32) NOT NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_OperationType_Upgrade] DEFAULT (N'Transaction') WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'OperationSuccess') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [OperationSuccess] BIT NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'SettlementData') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [SettlementData] NVARCHAR(MAX) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'SettlementReceiptTexts') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [SettlementReceiptTexts] NVARCHAR(MAX) NULL;
            END;

            -- 创建时间用于判断未决结算属于哪个营业日。升级时先加可空列并带默认值（之后新行自动写入），
            -- 旧行用 UpdatedAt 回填；回填语句用动态 SQL，避免同一批次里引用刚添加的列而编译失败。
            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'CreatedAt') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [CreatedAt] DATETIME2(7) NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_CreatedAt_Upgrade] DEFAULT (SYSUTCDATETIME());
                EXEC (N'UPDATE [dbo].[POSM_LinklyCloudBackendSession] SET [CreatedAt] = [UpdatedAt] WHERE [CreatedAt] IS NULL;');
            END;

            -- 订单同步（OrderSyncService）核对卡付款会话后回写订单号；可空，旧客户端与历史行不受影响。
            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'OrderGuid') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [OrderGuid] NVARCHAR(50) NULL;
            END;

            -- 会话被主管结案或已 ack 之后才到达的 Linkly 最终结果：独立保存，不改写 Status / ack 状态。
            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'LateFinalAt') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [LateFinalAt] DATETIME2(7) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'LateFinalTransactionSuccess') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [LateFinalTransactionSuccess] BIT NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'LateFinalResponseCode') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [LateFinalResponseCode] NVARCHAR(32) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'LateFinalResponseText') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [LateFinalResponseText] NVARCHAR(512) NULL;
            END;

            -- 状态查询/恢复 GET 连续收到未列出的 4xx（如 400/409/422）的次数，用于限制向 Linkly 的空转查询。
            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'OfficialQueryRejectCount') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [OfficialQueryRejectCount] INT NOT NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_OfficialQueryRejectCount_Upgrade] DEFAULT (0) WITH VALUES;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U')
                  AND [name] = N'UX_POSM_LinklyCloudBackendSession_ActiveTerminal')
            BEGIN
                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudBackendSession_ActiveTerminal]
                    ON [dbo].[POSM_LinklyCloudBackendSession] ([Environment], [StoreCode], [DeviceCode])
                    WHERE [IsActive] = 1;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U')
                  AND [name] = N'UX_POSM_LinklyCloudBackendSession_TxnRef')
            BEGIN
                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudBackendSession_TxnRef]
                    ON [dbo].[POSM_LinklyCloudBackendSession] ([Environment], [StoreCode], [TxnRef])
                    WHERE [TxnRef] IS NOT NULL;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U')
                  AND [name] = N'UX_POSM_LinklyCloudBackendSession_ActiveCloudTerminal')
            BEGIN
                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudBackendSession_ActiveCloudTerminal]
                    ON [dbo].[POSM_LinklyCloudBackendSession] ([Environment], [StoreCode], [TerminalId])
                    WHERE [IsActive] = 1 AND [TerminalId] IS NOT NULL;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U')
                  AND [name] = N'IX_POSM_LinklyCloudBackendSession_DeviceRecovery')
            BEGIN
                CREATE INDEX [IX_POSM_LinklyCloudBackendSession_DeviceRecovery]
                    ON [dbo].[POSM_LinklyCloudBackendSession]
                        ([Environment], [StoreCode], [DeviceCode], [IsActive], [Status], [ClientAcknowledgedAt])
                    INCLUDE ([UpdatedAt]);
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U')
                  AND [name] = N'IX_POSM_LinklyCloudBackendSession_TerminalRecovery')
            BEGIN
                CREATE INDEX [IX_POSM_LinklyCloudBackendSession_TerminalRecovery]
                    ON [dbo].[POSM_LinklyCloudBackendSession]
                        ([Environment], [StoreCode], [TerminalId], [IsActive], [Status], [ClientAcknowledgedAt])
                    INCLUDE ([UpdatedAt])
                    WHERE [TerminalId] IS NOT NULL;
            END;

            -- Linkly 回调只带 (Environment, SessionId)，唯一键以 StoreCode/DeviceCode 打头无法用于这条查询。
            -- 非唯一：不同门店/设备理论上可能出现相同 SessionId，由查询按 UpdatedAt 取最新一条。
            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U')
                  AND [name] = N'IX_POSM_LinklyCloudBackendSession_EnvSession')
            BEGIN
                CREATE INDEX [IX_POSM_LinklyCloudBackendSession_EnvSession]
                    ON [dbo].[POSM_LinklyCloudBackendSession] ([Environment], [SessionId])
                    INCLUDE ([UpdatedAt]);
            END;
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendTerminal]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_LinklyCloudBackendTerminal] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_LinklyCloudBackendTerminal] PRIMARY KEY,
                [Environment] NVARCHAR(32) NOT NULL,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [DeviceCode] NVARCHAR(64) NOT NULL,
                [Secret] NVARCHAR(512) NOT NULL,
                [PosId] NVARCHAR(64) NOT NULL,
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendTerminal_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedBy] NVARCHAR(128) NULL,
                CONSTRAINT [CK_POSM_LinklyCloudBackendTerminal_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox')),
                CONSTRAINT [UX_POSM_LinklyCloudBackendTerminal_Scope] UNIQUE ([Environment], [StoreCode], [DeviceCode])
            );
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendTerminal]', N'U') IS NOT NULL
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendTerminal]', N'U')
                  AND [name] = N'UX_POSM_LinklyCloudBackendTerminal_Scope')
            BEGIN
                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudBackendTerminal_Scope]
                    ON [dbo].[POSM_LinklyCloudBackendTerminal] ([Environment], [StoreCode], [DeviceCode]);
            END;
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_LinklyCloudTerminal] (
                [TerminalId] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [PK_POSM_LinklyCloudTerminal] PRIMARY KEY,
                [Environment] NVARCHAR(32) NOT NULL,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [LaneNo] INT NOT NULL,
                [DisplayName] NVARCHAR(128) NOT NULL,
                [Username] NVARCHAR(128) NOT NULL,
                [Password] NVARCHAR(2048) NOT NULL,
                [Secret] NVARCHAR(2048) NULL,
                [CredentialProtectionVersion] TINYINT NOT NULL
                    CONSTRAINT [DF_POSM_LinklyCloudTerminal_CredentialProtectionVersion] DEFAULT (1),
                [PosId] NVARCHAR(64) NULL,
                [PairingState] NVARCHAR(32) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudTerminal_PairingState] DEFAULT (N'Unpaired'),
                [PairingAttemptId] UNIQUEIDENTIFIER NULL,
                [PairingLeaseExpiresAt] DATETIME2(7) NULL,
                [LastHealthStatus] NVARCHAR(32) NULL,
                [LastHealthAt] DATETIME2(7) NULL,
                [CreatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudTerminal_CreatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudTerminal_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [CreatedBy] NVARCHAR(128) NULL,
                [UpdatedBy] NVARCHAR(128) NULL,
                CONSTRAINT [CK_POSM_LinklyCloudTerminal_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox')),
                CONSTRAINT [CK_POSM_LinklyCloudTerminal_PairingState] CHECK ([PairingState] IN (N'Unpaired', N'Ready', N'Unknown', N'NeedsRepair')),
                CONSTRAINT [CK_POSM_LinklyCloudTerminal_CredentialProtectionVersion]
                    CHECK ([CredentialProtectionVersion] IN (0, 1)),
                CONSTRAINT [UX_POSM_LinklyCloudTerminal_Scope_LaneNo] UNIQUE ([Environment], [StoreCode], [LaneNo]),
                CONSTRAINT [UX_POSM_LinklyCloudTerminal_Scope_Username] UNIQUE ([Environment], [StoreCode], [Username]),
                CONSTRAINT [UX_POSM_LinklyCloudTerminal_Scope_DisplayName] UNIQUE ([Environment], [StoreCode], [DisplayName])
            );
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]', N'U') IS NOT NULL
        BEGIN
            IF COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'PairingAttemptId') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudTerminal]
                    ADD [PairingAttemptId] UNIQUEIDENTIFIER NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'PairingLeaseExpiresAt') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudTerminal]
                    ADD [PairingLeaseExpiresAt] DATETIME2(7) NULL;
            END;

            -- 历史行统一标记为 version 0，禁止初始化过程自动读取、复制或迁移明文凭据。
            IF COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'CredentialProtectionVersion') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudTerminal]
                    ADD [CredentialProtectionVersion] TINYINT NOT NULL
                    CONSTRAINT [DF_POSM_LinklyCloudTerminal_CredentialProtectionVersion]
                    DEFAULT (0) WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'Password') < 4096
                ALTER TABLE [dbo].[POSM_LinklyCloudTerminal]
                    ALTER COLUMN [Password] NVARCHAR(2048) NOT NULL;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'Secret') < 4096
                ALTER TABLE [dbo].[POSM_LinklyCloudTerminal]
                    ALTER COLUMN [Secret] NVARCHAR(2048) NULL;

            IF OBJECT_ID(N'[dbo].[CK_POSM_LinklyCloudTerminal_CredentialProtectionVersion]', N'C') IS NULL
                ALTER TABLE [dbo].[POSM_LinklyCloudTerminal] WITH CHECK
                    ADD CONSTRAINT [CK_POSM_LinklyCloudTerminal_CredentialProtectionVersion]
                    CHECK ([CredentialProtectionVersion] IN (0, 1));

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]', N'U')
                  AND [name] = N'UX_POSM_LinklyCloudTerminal_Scope_LaneNo')
            BEGIN
                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudTerminal_Scope_LaneNo]
                    ON [dbo].[POSM_LinklyCloudTerminal] ([Environment], [StoreCode], [LaneNo]);
            END;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]', N'U')
                  AND [name] = N'UX_POSM_LinklyCloudTerminal_Scope_Username')
            BEGIN
                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudTerminal_Scope_Username]
                    ON [dbo].[POSM_LinklyCloudTerminal] ([Environment], [StoreCode], [Username]);
            END;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]', N'U')
                  AND [name] = N'UX_POSM_LinklyCloudTerminal_Scope_DisplayName')
            BEGIN
                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudTerminal_Scope_DisplayName]
                    ON [dbo].[POSM_LinklyCloudTerminal] ([Environment], [StoreCode], [DisplayName]);
            END;
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudDeviceSelection]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_LinklyCloudDeviceSelection] (
                [Environment] NVARCHAR(32) NOT NULL,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [DeviceCode] NVARCHAR(64) NOT NULL,
                [TerminalId] UNIQUEIDENTIFIER NOT NULL,
                [Revision] BIGINT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudDeviceSelection_Revision] DEFAULT (1),
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudDeviceSelection_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedBy] NVARCHAR(128) NULL,
                CONSTRAINT [PK_POSM_LinklyCloudDeviceSelection] PRIMARY KEY ([Environment], [StoreCode], [DeviceCode]),
                CONSTRAINT [CK_POSM_LinklyCloudDeviceSelection_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox')),
                CONSTRAINT [FK_POSM_LinklyCloudDeviceSelection_Terminal]
                    FOREIGN KEY ([TerminalId]) REFERENCES [dbo].[POSM_LinklyCloudTerminal] ([TerminalId])
            );
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudDeviceSelection]', N'U') IS NOT NULL
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudDeviceSelection]', N'U')
                  AND [name] = N'UX_POSM_LinklyCloudDeviceSelection_Scope_Terminal')
            BEGIN
                -- 历史重复属于需要人工确认的支付终端归属冲突，禁止初始化过程自动改写。
                IF EXISTS (
                    SELECT 1
                    FROM [dbo].[POSM_LinklyCloudDeviceSelection]
                    GROUP BY [Environment], [StoreCode], [TerminalId]
                    HAVING COUNT_BIG(*) > 1)
                    THROW 51004, 'Linkly Cloud terminal is already assigned to another POS.', 1;

                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudDeviceSelection_Scope_Terminal]
                    ON [dbo].[POSM_LinklyCloudDeviceSelection] ([Environment], [StoreCode], [TerminalId]);
            END;
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudConfigurationMode]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_LinklyCloudConfigurationMode] (
                [Environment] NVARCHAR(32) NOT NULL,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [Mode] NVARCHAR(16) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudConfigurationMode_Mode] DEFAULT (N'Legacy'),
                [LegacyPairingAttemptId] UNIQUEIDENTIFIER NULL,
                [LegacyPairingLeaseExpiresAt] DATETIME2(7) NULL,
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudConfigurationMode_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedBy] NVARCHAR(128) NULL,
                CONSTRAINT [PK_POSM_LinklyCloudConfigurationMode] PRIMARY KEY ([Environment], [StoreCode]),
                CONSTRAINT [CK_POSM_LinklyCloudConfigurationMode_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox')),
                CONSTRAINT [CK_POSM_LinklyCloudConfigurationMode_Mode] CHECK ([Mode] IN (N'Legacy', N'Draft', N'Active'))
            );
        END;

        IF COL_LENGTH(N'dbo.POSM_LinklyCloudConfigurationMode', N'LegacyPairingAttemptId') IS NULL
        BEGIN
            ALTER TABLE [dbo].[POSM_LinklyCloudConfigurationMode]
                ADD [LegacyPairingAttemptId] UNIQUEIDENTIFIER NULL;
        END;

        IF COL_LENGTH(N'dbo.POSM_LinklyCloudConfigurationMode', N'LegacyPairingLeaseExpiresAt') IS NULL
        BEGIN
            ALTER TABLE [dbo].[POSM_LinklyCloudConfigurationMode]
                ADD [LegacyPairingLeaseExpiresAt] DATETIME2(7) NULL;
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendNotification]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_LinklyCloudBackendNotification] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_LinklyCloudBackendNotification] PRIMARY KEY,
                [Environment] NVARCHAR(32) NOT NULL,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [DeviceCode] NVARCHAR(64) NOT NULL,
                [SessionId] NVARCHAR(64) NOT NULL,
                [Type] NVARCHAR(64) NOT NULL,
                [PayloadJson] NVARCHAR(MAX) NOT NULL,
                [ReceivedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendNotification_ReceivedAt] DEFAULT (SYSUTCDATETIME()),
                CONSTRAINT [CK_POSM_LinklyCloudBackendNotification_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox'))
            );
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendNotification]', N'U') IS NOT NULL
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendNotification]', N'U')
                  AND [name] = N'IX_POSM_LinklyCloudBackendNotification_Scope')
            BEGIN
                CREATE INDEX [IX_POSM_LinklyCloudBackendNotification_Scope]
                    ON [dbo].[POSM_LinklyCloudBackendNotification] ([Environment], [StoreCode], [DeviceCode], [SessionId], [ReceivedAt]);
            END;
        END;

        -- 卡付款对账异常表：订单入库校验（OrderSync）与后台对账作业（ReconciliationJob）共用。
        -- 同一问题按 DedupKey（IssueType|OrderGuid|PaymentGuid|SessionId）幂等合并，重复发现只刷新 LastDetectedAt。
        IF OBJECT_ID(N'[dbo].[POSM_CardTenderReconciliationIssue]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_CardTenderReconciliationIssue] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_CardTenderReconciliationIssue] PRIMARY KEY,
                [DedupKey] NVARCHAR(256) NOT NULL,
                [IssueType] NVARCHAR(64) NOT NULL,
                [Severity] NVARCHAR(16) NOT NULL,
                [Source] NVARCHAR(32) NOT NULL,
                -- Open 待处理；Resolved 已自动或人工解除；Dismissed 人工确认无需处理（再次发现不会重新打开）。
                [Status] NVARCHAR(16) NOT NULL CONSTRAINT [DF_POSM_CardTenderReconciliationIssue_Status] DEFAULT (N'Open'),
                [StoreCode] NVARCHAR(32) NOT NULL,
                [DeviceCode] NVARCHAR(64) NULL,
                [Environment] NVARCHAR(32) NULL,
                [SessionId] NVARCHAR(64) NULL,
                [TxnRef] NVARCHAR(64) NULL,
                [OrderGuid] NVARCHAR(50) NULL,
                [PaymentGuid] NVARCHAR(50) NULL,
                [Amount] DECIMAL(18,2) NULL,
                [Detail] NVARCHAR(1000) NULL,
                [OccurrenceCount] INT NOT NULL CONSTRAINT [DF_POSM_CardTenderReconciliationIssue_OccurrenceCount] DEFAULT (1),
                [FirstDetectedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_CardTenderReconciliationIssue_FirstDetectedAt] DEFAULT (SYSUTCDATETIME()),
                [LastDetectedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_CardTenderReconciliationIssue_LastDetectedAt] DEFAULT (SYSUTCDATETIME()),
                [ResolvedAt] DATETIME2(7) NULL,
                CONSTRAINT [CK_POSM_CardTenderReconciliationIssue_Status] CHECK ([Status] IN (N'Open', N'Resolved', N'Dismissed')),
                CONSTRAINT [CK_POSM_CardTenderReconciliationIssue_Severity] CHECK ([Severity] IN (N'Warning', N'Error')),
                CONSTRAINT [UX_POSM_CardTenderReconciliationIssue_DedupKey] UNIQUE ([DedupKey])
            );
        END;

        IF OBJECT_ID(N'[dbo].[POSM_CardTenderReconciliationIssue]', N'U') IS NOT NULL
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_CardTenderReconciliationIssue]', N'U')
                  AND [name] = N'IX_POSM_CardTenderReconciliationIssue_Status')
            BEGIN
                CREATE INDEX [IX_POSM_CardTenderReconciliationIssue_Status]
                    ON [dbo].[POSM_CardTenderReconciliationIssue] ([Status], [StoreCode], [LastDetectedAt] DESC);
            END;
        END;

        COMMIT TRANSACTION;
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[HBPOS][Api][LinklyCloudBackend] {DateTimeOffset.Now:O} backend async schema ensure start");
        try
        {
            await sqlExecutor.ExecuteAsync(EnsureTableSql, cancellationToken);
            Console.WriteLine($"[HBPOS][Api][LinklyCloudBackend] {DateTimeOffset.Now:O} backend async schema ensure succeeded");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[HBPOS][Api][LinklyCloudBackend] {DateTimeOffset.Now:O} backend async schema ensure canceled");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HBPOS][Api][LinklyCloudBackend] {DateTimeOffset.Now:O} backend async schema ensure failed error={ex.GetType().Name}");
            throw;
        }
    }
}

public sealed class SqlSugarLinklyCloudBackendAsyncSchemaSqlExecutor(
    HbposSqlSugarContext dbContext) : ILinklyCloudBackendAsyncSchemaSqlExecutor
{
    public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(sql);
    }
}
