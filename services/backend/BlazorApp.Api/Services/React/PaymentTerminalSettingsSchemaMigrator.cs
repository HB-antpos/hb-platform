using SqlSugar;

namespace BlazorApp.Api.Services.React;

public static class PaymentTerminalSettingsSchemaMigrator
{
    internal const string SchemaLockSql = """
        SET XACT_ABORT ON;
        DECLARE @SchemaLockResult INT;
        EXEC @SchemaLockResult = sys.sp_getapplock
            @Resource = N'Hbpos.LinklyCloud.Schema.v2',
            @LockMode = N'Exclusive',
            @LockOwner = N'Transaction',
            @LockTimeout = 60000;
        IF @SchemaLockResult < 0
            THROW 51000, 'Could not acquire the shared Linkly Cloud schema lock.', 1;
        """;

    private const string EnsureSquareTokenTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_SquareToken]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_SquareToken] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_SquareToken] PRIMARY KEY,
                [Environment] NVARCHAR(32) NOT NULL,
                [AccessToken] NVARCHAR(2048) NOT NULL,
                [IsEnabled] BIT NOT NULL CONSTRAINT [DF_POSM_SquareToken_IsEnabled] DEFAULT (0),
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_SquareToken_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedBy] NVARCHAR(128) NULL,
                CONSTRAINT [CK_POSM_SquareToken_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox'))
            );
        END;
        """;

    private const string EnsureSquareTokenEnabledIndexSql = """
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = N'UX_POSM_SquareToken_Environment_Enabled'
              AND object_id = OBJECT_ID(N'[dbo].[POSM_SquareToken]')
        )
        BEGIN
            CREATE UNIQUE INDEX [UX_POSM_SquareToken_Environment_Enabled]
            ON [dbo].[POSM_SquareToken]([Environment])
            WHERE [IsEnabled] = 1;
        END;
        """;

    private const string EnsureLinklyCredentialTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_LinklyCloudCredential] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_LinklyCloudCredential] PRIMARY KEY,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [Environment] NVARCHAR(32) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudCredential_Environment] DEFAULT (N'Production'),
                [Username] NVARCHAR(256) NOT NULL,
                [Password] NVARCHAR(256) NOT NULL,
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudCredential_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedBy] NVARCHAR(128) NULL,
                CONSTRAINT [CK_POSM_LinklyCloudCredential_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox')),
                CONSTRAINT [UX_POSM_LinklyCloudCredential_StoreCode_Environment] UNIQUE ([StoreCode], [Environment])
            );
        END;
        """;

    private const string EnsureLinklyEnvironmentColumnSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U') IS NOT NULL
        BEGIN
            IF COL_LENGTH(N'dbo.POSM_LinklyCloudCredential', N'Environment') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudCredential]
                    ADD [Environment] NVARCHAR(32) NOT NULL
                    CONSTRAINT [DF_POSM_LinklyCloudCredential_Environment] DEFAULT (N'Production') WITH VALUES;
            END
        END;
        """;

    private const string NormalizeLinklyEnvironmentColumnSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.POSM_LinklyCloudCredential', N'Environment') IS NOT NULL
        BEGIN
            UPDATE [dbo].[POSM_LinklyCloudCredential]
            SET [Environment] = N'Production'
            WHERE NULLIF(LTRIM(RTRIM([Environment])), N'') IS NULL;

            IF EXISTS (
                SELECT 1
                FROM sys.columns
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U')
                  AND [name] = N'Environment'
                  AND [is_nullable] = 1)
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudCredential]
                    ALTER COLUMN [Environment] NVARCHAR(32) NOT NULL;
            END;
        END;
        """;

    private const string EnsureLinklyCredentialConstraintsSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U') IS NOT NULL
        BEGIN
            IF OBJECT_ID(N'[dbo].[DF_POSM_LinklyCloudCredential_Environment]', N'D') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudCredential]
                    ADD CONSTRAINT [DF_POSM_LinklyCloudCredential_Environment]
                    DEFAULT (N'Production') FOR [Environment];
            END;

            IF OBJECT_ID(N'[dbo].[UX_POSM_LinklyCloudCredential_StoreCode]', N'UQ') IS NOT NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudCredential]
                    DROP CONSTRAINT [UX_POSM_LinklyCloudCredential_StoreCode];
            END;

            IF OBJECT_ID(N'[dbo].[CK_POSM_LinklyCloudCredential_Environment]', N'C') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudCredential] WITH CHECK
                    ADD CONSTRAINT [CK_POSM_LinklyCloudCredential_Environment]
                    CHECK ([Environment] IN (N'Production', N'Sandbox'));
            END;

            IF OBJECT_ID(N'[dbo].[UX_POSM_LinklyCloudCredential_StoreCode_Environment]', N'UQ') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudCredential]
                    ADD CONSTRAINT [UX_POSM_LinklyCloudCredential_StoreCode_Environment]
                    UNIQUE ([StoreCode], [Environment]);
            END;
        END;
        """;

    private const string EnsureLinklyBackendSessionDependencySql = """
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
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                CONSTRAINT [CK_POSM_LinklyCloudBackendSession_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox')),
                CONSTRAINT [UX_POSM_LinklyCloudBackendSession_Scope] UNIQUE ([Environment], [StoreCode], [DeviceCode], [SessionId])
            );
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U') IS NOT NULL
        BEGIN
            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'TerminalId') IS NULL
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession] ADD [TerminalId] UNIQUEIDENTIFIER NULL;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'ClientAcknowledgedAt') IS NULL
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession] ADD [ClientAcknowledgedAt] DATETIME2(7) NULL;
        END;
        """;

    // 新列必须先在独立 batch 中提交给 SQL Server 编译器，再创建引用这些列的索引。
    private const string EnsureLinklyBackendSessionIndexesSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U') IS NOT NULL
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U')
                  AND [name] = N'UX_POSM_LinklyCloudBackendSession_ActiveCloudTerminal')
            BEGIN
                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudBackendSession_ActiveCloudTerminal]
                    ON [dbo].[POSM_LinklyCloudBackendSession] ([Environment], [StoreCode], [TerminalId])
                    WHERE [IsActive] = 1 AND [TerminalId] IS NOT NULL;
            END;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U')
                  AND [name] = N'IX_POSM_LinklyCloudBackendSession_DeviceRecovery')
            BEGIN
                CREATE INDEX [IX_POSM_LinklyCloudBackendSession_DeviceRecovery]
                    ON [dbo].[POSM_LinklyCloudBackendSession]
                        ([Environment], [StoreCode], [DeviceCode], [IsActive], [Status], [ClientAcknowledgedAt])
                    INCLUDE ([UpdatedAt]);
            END;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U')
                  AND [name] = N'IX_POSM_LinklyCloudBackendSession_TerminalRecovery')
            BEGIN
                CREATE INDEX [IX_POSM_LinklyCloudBackendSession_TerminalRecovery]
                    ON [dbo].[POSM_LinklyCloudBackendSession]
                        ([Environment], [StoreCode], [TerminalId], [IsActive], [Status], [ClientAcknowledgedAt])
                    INCLUDE ([UpdatedAt])
                    WHERE [TerminalId] IS NOT NULL;
            END;

            -- Linkly 回调只带 (Environment, SessionId)；与 Hbpos.Api 初始化脚本同名同定义，按名称幂等创建。
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U')
                  AND [name] = N'IX_POSM_LinklyCloudBackendSession_EnvSession')
            BEGIN
                CREATE INDEX [IX_POSM_LinklyCloudBackendSession_EnvSession]
                    ON [dbo].[POSM_LinklyCloudBackendSession] ([Environment], [SessionId])
                    INCLUDE ([UpdatedAt]);
            END;
        END;
        """;

    private const string EnsureLinklyTerminalTableSql = """
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
                [PairingState] NVARCHAR(32) NOT NULL
                    CONSTRAINT [DF_POSM_LinklyCloudTerminal_PairingState] DEFAULT (N'Unpaired'),
                [PairingAttemptId] UNIQUEIDENTIFIER NULL,
                [PairingLeaseExpiresAt] DATETIME2(7) NULL,
                [LastHealthStatus] NVARCHAR(32) NULL,
                [LastHealthAt] DATETIME2(7) NULL,
                [CreatedAt] DATETIME2(7) NOT NULL
                    CONSTRAINT [DF_POSM_LinklyCloudTerminal_CreatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedAt] DATETIME2(7) NOT NULL
                    CONSTRAINT [DF_POSM_LinklyCloudTerminal_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [CreatedBy] NVARCHAR(128) NULL,
                [UpdatedBy] NVARCHAR(128) NULL,
                CONSTRAINT [CK_POSM_LinklyCloudTerminal_Environment]
                    CHECK ([Environment] IN (N'Production', N'Sandbox')),
                CONSTRAINT [CK_POSM_LinklyCloudTerminal_PairingState]
                    CHECK ([PairingState] IN (N'Unpaired', N'Ready', N'Unknown', N'NeedsRepair')),
                CONSTRAINT [CK_POSM_LinklyCloudTerminal_CredentialProtectionVersion]
                    CHECK ([CredentialProtectionVersion] IN (0, 1)),
                CONSTRAINT [UX_POSM_LinklyCloudTerminal_Scope_LaneNo]
                    UNIQUE ([Environment], [StoreCode], [LaneNo]),
                CONSTRAINT [UX_POSM_LinklyCloudTerminal_Scope_Username]
                    UNIQUE ([Environment], [StoreCode], [Username]),
                CONSTRAINT [UX_POSM_LinklyCloudTerminal_Scope_DisplayName]
                    UNIQUE ([Environment], [StoreCode], [DisplayName])
            );
        END;
        """;

    private const string EnsureLinklyTerminalLeaseColumnsSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]', N'U') IS NOT NULL
        BEGIN
            IF COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'PairingAttemptId') IS NULL
                ALTER TABLE [dbo].[POSM_LinklyCloudTerminal] ADD [PairingAttemptId] UNIQUEIDENTIFIER NULL;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'PairingLeaseExpiresAt') IS NULL
                ALTER TABLE [dbo].[POSM_LinklyCloudTerminal] ADD [PairingLeaseExpiresAt] DATETIME2(7) NULL;
        END;
        """;

    private const string EnsureLinklyTerminalCredentialProtectionSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]', N'U') IS NOT NULL
        BEGIN
            -- 历史行一律标为 version 0；禁止在 SQL 迁移中读取、复制或自动重写可能的明文凭据。
            IF COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'CredentialProtectionVersion') IS NULL
                ALTER TABLE [dbo].[POSM_LinklyCloudTerminal]
                    ADD [CredentialProtectionVersion] TINYINT NOT NULL
                    CONSTRAINT [DF_POSM_LinklyCloudTerminal_CredentialProtectionVersion]
                    DEFAULT (0) WITH VALUES;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'Password') < 4096
                ALTER TABLE [dbo].[POSM_LinklyCloudTerminal]
                    ALTER COLUMN [Password] NVARCHAR(2048) NOT NULL;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'Secret') < 4096
                ALTER TABLE [dbo].[POSM_LinklyCloudTerminal]
                    ALTER COLUMN [Secret] NVARCHAR(2048) NULL;
        END;
        """;

    // 历史表可能刚在上一批补齐保护版本列，约束必须延后到独立 batch 编译。
    private const string EnsureLinklyTerminalCredentialProtectionConstraintSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]', N'U') IS NOT NULL
        BEGIN
            IF OBJECT_ID(N'[dbo].[CK_POSM_LinklyCloudTerminal_CredentialProtectionVersion]', N'C') IS NULL
                ALTER TABLE [dbo].[POSM_LinklyCloudTerminal] WITH CHECK
                    ADD CONSTRAINT [CK_POSM_LinklyCloudTerminal_CredentialProtectionVersion]
                    CHECK ([CredentialProtectionVersion] IN (0, 1));
        END;
        """;

    private const string EnsureLinklyTerminalIndexesSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]', N'U') IS NOT NULL
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'UX_POSM_LinklyCloudTerminal_Scope_LaneNo'
                  AND [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]'))
            BEGIN
                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudTerminal_Scope_LaneNo]
                ON [dbo].[POSM_LinklyCloudTerminal]([Environment], [StoreCode], [LaneNo]);
            END;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'UX_POSM_LinklyCloudTerminal_Scope_Username'
                  AND [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]'))
            BEGIN
                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudTerminal_Scope_Username]
                ON [dbo].[POSM_LinklyCloudTerminal]([Environment], [StoreCode], [Username]);
            END;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'UX_POSM_LinklyCloudTerminal_Scope_DisplayName'
                  AND [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]'))
            BEGIN
                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudTerminal_Scope_DisplayName]
                ON [dbo].[POSM_LinklyCloudTerminal]([Environment], [StoreCode], [DisplayName]);
            END;
        END;
        """;

    private const string EnsureLinklyDeviceSelectionTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudDeviceSelection]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_LinklyCloudDeviceSelection] (
                [Environment] NVARCHAR(32) NOT NULL,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [DeviceCode] NVARCHAR(64) NOT NULL,
                [TerminalId] UNIQUEIDENTIFIER NOT NULL,
                [Revision] BIGINT NOT NULL
                    CONSTRAINT [DF_POSM_LinklyCloudDeviceSelection_Revision] DEFAULT (1),
                [UpdatedAt] DATETIME2(7) NOT NULL
                    CONSTRAINT [DF_POSM_LinklyCloudDeviceSelection_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedBy] NVARCHAR(128) NULL,
                CONSTRAINT [PK_POSM_LinklyCloudDeviceSelection]
                    PRIMARY KEY ([Environment], [StoreCode], [DeviceCode]),
                CONSTRAINT [CK_POSM_LinklyCloudDeviceSelection_Environment]
                    CHECK ([Environment] IN (N'Production', N'Sandbox')),
                CONSTRAINT [FK_POSM_LinklyCloudDeviceSelection_Terminal]
                    FOREIGN KEY ([TerminalId]) REFERENCES [dbo].[POSM_LinklyCloudTerminal]([TerminalId])
            );
        END;
        """;

    private const string EnsureLinklyDeviceSelectionTerminalUniqueIndexSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudDeviceSelection]', N'U') IS NOT NULL
           AND NOT EXISTS (
               SELECT 1 FROM sys.indexes
               WHERE [name] = N'UX_POSM_LinklyCloudDeviceSelection_Scope_Terminal'
                 AND [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudDeviceSelection]')
           )
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM [dbo].[POSM_LinklyCloudDeviceSelection]
                GROUP BY [Environment], [StoreCode], [TerminalId]
                HAVING COUNT(*) > 1
            )
                THROW 51004, 'Duplicate Linkly Cloud terminal assignments must be resolved before migration.', 1;

            CREATE UNIQUE INDEX [UX_POSM_LinklyCloudDeviceSelection_Scope_Terminal]
                ON [dbo].[POSM_LinklyCloudDeviceSelection] ([Environment], [StoreCode], [TerminalId]);
        END;
        """;

    private const string EnsureLinklyConfigurationModeTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudConfigurationMode]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_LinklyCloudConfigurationMode] (
                [Environment] NVARCHAR(32) NOT NULL,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [Mode] NVARCHAR(16) NOT NULL
                    CONSTRAINT [DF_POSM_LinklyCloudConfigurationMode_Mode] DEFAULT (N'Legacy'),
                [LegacyPairingAttemptId] UNIQUEIDENTIFIER NULL,
                [LegacyPairingLeaseExpiresAt] DATETIME2(7) NULL,
                [UpdatedAt] DATETIME2(7) NOT NULL
                    CONSTRAINT [DF_POSM_LinklyCloudConfigurationMode_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedBy] NVARCHAR(128) NULL,
                CONSTRAINT [PK_POSM_LinklyCloudConfigurationMode]
                    PRIMARY KEY ([Environment], [StoreCode]),
                CONSTRAINT [CK_POSM_LinklyCloudConfigurationMode_Environment]
                    CHECK ([Environment] IN (N'Production', N'Sandbox')),
                CONSTRAINT [CK_POSM_LinklyCloudConfigurationMode_Mode]
                    CHECK ([Mode] IN (N'Legacy', N'Draft', N'Active'))
            );
        END;
        """;

    private const string EnsureLinklyConfigurationModeLegacyPairingLeaseColumnsSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudConfigurationMode]', N'U') IS NOT NULL
        BEGIN
            IF COL_LENGTH(N'dbo.POSM_LinklyCloudConfigurationMode', N'LegacyPairingAttemptId') IS NULL
                ALTER TABLE [dbo].[POSM_LinklyCloudConfigurationMode]
                    ADD [LegacyPairingAttemptId] UNIQUEIDENTIFIER NULL;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudConfigurationMode', N'LegacyPairingLeaseExpiresAt') IS NULL
                ALTER TABLE [dbo].[POSM_LinklyCloudConfigurationMode]
                    ADD [LegacyPairingLeaseExpiresAt] DATETIME2(7) NULL;
        END;
        """;

    // 独立迁移与常规 schema-check 共用同一只读签名，避免账本已登记但 Linkly 表被误删时静默通过。
    internal const string LinklyMultiTerminalVerifySql = """
        SET NOCOUNT ON;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U') IS NULL
            THROW 51600, N'POSM_LinklyCloudBackendSession table is missing.', 1;
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]', N'U') IS NULL
            THROW 51601, N'POSM_LinklyCloudTerminal table is missing.', 1;
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudDeviceSelection]', N'U') IS NULL
            THROW 51602, N'POSM_LinklyCloudDeviceSelection table is missing.', 1;
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudConfigurationMode]', N'U') IS NULL
            THROW 51603, N'POSM_LinklyCloudConfigurationMode table is missing.', 1;

        IF EXISTS (
            SELECT 1
            FROM (VALUES
                (N'Environment', 231, 64, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'StoreCode', 231, 64, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'DeviceCode', 231, 128, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'TerminalId', 36, 16, CAST(0 AS tinyint), CAST(1 AS bit)),
                (N'Status', 231, 64, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'IsActive', 104, 1, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'ClientAcknowledgedAt', 42, 8, CAST(7 AS tinyint), CAST(1 AS bit)),
                (N'UpdatedAt', 42, 8, CAST(7 AS tinyint), CAST(0 AS bit))
            ) AS expected(name, system_type_id, max_length, scale, is_nullable)
            LEFT JOIN sys.columns AS c
                ON c.object_id = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]')
               AND c.name = expected.name
            WHERE c.column_id IS NULL
               OR c.system_type_id <> expected.system_type_id
               OR c.max_length <> expected.max_length
               OR c.scale <> expected.scale
               OR c.is_nullable <> expected.is_nullable
        )
            THROW 51604, N'POSM_LinklyCloudBackendSession column signature is incompatible.', 1;

        IF EXISTS (
            SELECT 1
            FROM (VALUES
                (N'TerminalId', 36, 16, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'Environment', 231, 64, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'StoreCode', 231, 64, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'LaneNo', 56, 4, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'DisplayName', 231, 256, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'Username', 231, 256, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'Password', 231, 4096, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'Secret', 231, 4096, CAST(0 AS tinyint), CAST(1 AS bit)),
                (N'CredentialProtectionVersion', 48, 1, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'PosId', 231, 128, CAST(0 AS tinyint), CAST(1 AS bit)),
                (N'PairingState', 231, 64, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'PairingAttemptId', 36, 16, CAST(0 AS tinyint), CAST(1 AS bit)),
                (N'PairingLeaseExpiresAt', 42, 8, CAST(7 AS tinyint), CAST(1 AS bit)),
                (N'LastHealthStatus', 231, 64, CAST(0 AS tinyint), CAST(1 AS bit)),
                (N'LastHealthAt', 42, 8, CAST(7 AS tinyint), CAST(1 AS bit)),
                (N'CreatedAt', 42, 8, CAST(7 AS tinyint), CAST(0 AS bit)),
                (N'UpdatedAt', 42, 8, CAST(7 AS tinyint), CAST(0 AS bit)),
                (N'CreatedBy', 231, 256, CAST(0 AS tinyint), CAST(1 AS bit)),
                (N'UpdatedBy', 231, 256, CAST(0 AS tinyint), CAST(1 AS bit))
            ) AS expected(name, system_type_id, max_length, scale, is_nullable)
            LEFT JOIN sys.columns AS c
                ON c.object_id = OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]')
               AND c.name = expected.name
            WHERE c.column_id IS NULL
               OR c.system_type_id <> expected.system_type_id
               OR c.max_length <> expected.max_length
               OR c.scale <> expected.scale
               OR c.is_nullable <> expected.is_nullable
        )
            THROW 51605, N'POSM_LinklyCloudTerminal column signature is incompatible.', 1;

        IF EXISTS (
            SELECT 1
            FROM (VALUES
                (N'Environment', 231, 64, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'StoreCode', 231, 64, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'DeviceCode', 231, 128, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'TerminalId', 36, 16, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'Revision', 127, 8, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'UpdatedAt', 42, 8, CAST(7 AS tinyint), CAST(0 AS bit)),
                (N'UpdatedBy', 231, 256, CAST(0 AS tinyint), CAST(1 AS bit))
            ) AS expected(name, system_type_id, max_length, scale, is_nullable)
            LEFT JOIN sys.columns AS c
                ON c.object_id = OBJECT_ID(N'[dbo].[POSM_LinklyCloudDeviceSelection]')
               AND c.name = expected.name
            WHERE c.column_id IS NULL
               OR c.system_type_id <> expected.system_type_id
               OR c.max_length <> expected.max_length
               OR c.scale <> expected.scale
               OR c.is_nullable <> expected.is_nullable
        )
            THROW 51606, N'POSM_LinklyCloudDeviceSelection column signature is incompatible.', 1;

        IF EXISTS (
            SELECT 1
            FROM (VALUES
                (N'Environment', 231, 64, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'StoreCode', 231, 64, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'Mode', 231, 32, CAST(0 AS tinyint), CAST(0 AS bit)),
                (N'LegacyPairingAttemptId', 36, 16, CAST(0 AS tinyint), CAST(1 AS bit)),
                (N'LegacyPairingLeaseExpiresAt', 42, 8, CAST(7 AS tinyint), CAST(1 AS bit)),
                (N'UpdatedAt', 42, 8, CAST(7 AS tinyint), CAST(0 AS bit)),
                (N'UpdatedBy', 231, 256, CAST(0 AS tinyint), CAST(1 AS bit))
            ) AS expected(name, system_type_id, max_length, scale, is_nullable)
            LEFT JOIN sys.columns AS c
                ON c.object_id = OBJECT_ID(N'[dbo].[POSM_LinklyCloudConfigurationMode]')
               AND c.name = expected.name
            WHERE c.column_id IS NULL
               OR c.system_type_id <> expected.system_type_id
               OR c.max_length <> expected.max_length
               OR c.scale <> expected.scale
               OR c.is_nullable <> expected.is_nullable
        )
            THROW 51607, N'POSM_LinklyCloudConfigurationMode column signature is incompatible.', 1;

        IF NOT EXISTS (
            SELECT 1
            FROM sys.indexes AS i
            JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]')
              AND i.name = N'PK_POSM_LinklyCloudTerminal'
              AND i.is_primary_key = 1 AND i.type = 1 AND i.is_disabled = 0 AND i.is_hypothetical = 0
              AND ic.key_ordinal = 1 AND c.name = N'TerminalId'
              AND 1 = (SELECT COUNT(1) FROM sys.index_columns AS keysOnly WHERE keysOnly.object_id = i.object_id AND keysOnly.index_id = i.index_id AND keysOnly.key_ordinal > 0)
        )
            THROW 51608, N'POSM_LinklyCloudTerminal primary key is incompatible.', 1;

        IF EXISTS (
            SELECT required.name
            FROM (VALUES
                (N'UX_POSM_LinklyCloudTerminal_Scope_LaneNo', N'LaneNo'),
                (N'UX_POSM_LinklyCloudTerminal_Scope_Username', N'Username'),
                (N'UX_POSM_LinklyCloudTerminal_Scope_DisplayName', N'DisplayName')
            ) AS required(name, third_key)
            WHERE NOT EXISTS (
                SELECT 1 FROM sys.indexes AS i
                WHERE i.object_id = OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]')
                  AND i.name = required.name
                  AND i.is_unique = 1 AND i.is_disabled = 0 AND i.has_filter = 0 AND i.is_hypothetical = 0
                  AND 3 = (SELECT COUNT(1) FROM sys.index_columns AS keysOnly WHERE keysOnly.object_id = i.object_id AND keysOnly.index_id = i.index_id AND keysOnly.key_ordinal > 0)
                  AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 AND c.name = N'Environment')
                  AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 2 AND c.name = N'StoreCode')
                  AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 3 AND c.name = required.third_key)
            )
        )
            THROW 51609, N'POSM_LinklyCloudTerminal unique index signature is incompatible.', 1;

        IF EXISTS (
            SELECT required.name
            FROM (VALUES
                (N'PK_POSM_LinklyCloudDeviceSelection', CAST(1 AS bit), N'DeviceCode'),
                (N'UX_POSM_LinklyCloudDeviceSelection_Scope_Terminal', CAST(0 AS bit), N'TerminalId')
            ) AS required(name, is_primary_key, third_key)
            WHERE NOT EXISTS (
                SELECT 1 FROM sys.indexes AS i
                WHERE i.object_id = OBJECT_ID(N'[dbo].[POSM_LinklyCloudDeviceSelection]')
                  AND i.name = required.name
                  AND i.is_unique = 1
                  AND i.is_primary_key = required.is_primary_key
                  AND (required.is_primary_key = 0 OR i.type = 1)
                  AND i.is_disabled = 0 AND i.has_filter = 0 AND i.is_hypothetical = 0
                  AND 3 = (SELECT COUNT(1) FROM sys.index_columns AS keysOnly WHERE keysOnly.object_id = i.object_id AND keysOnly.index_id = i.index_id AND keysOnly.key_ordinal > 0)
                  AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 AND c.name = N'Environment')
                  AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 2 AND c.name = N'StoreCode')
                  AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 3 AND c.name = required.third_key)
            )
        )
            THROW 51610, N'POSM_LinklyCloudDeviceSelection key signature is incompatible.', 1;

        IF NOT EXISTS (
            SELECT 1 FROM sys.foreign_keys AS fk
            WHERE fk.parent_object_id = OBJECT_ID(N'[dbo].[POSM_LinklyCloudDeviceSelection]')
              AND fk.referenced_object_id = OBJECT_ID(N'[dbo].[POSM_LinklyCloudTerminal]')
              AND fk.name = N'FK_POSM_LinklyCloudDeviceSelection_Terminal'
              AND fk.is_disabled = 0 AND fk.is_not_trusted = 0
              AND fk.delete_referential_action = 0 AND fk.update_referential_action = 0
              AND 1 = (SELECT COUNT(1) FROM sys.foreign_key_columns AS fkc WHERE fkc.constraint_object_id = fk.object_id)
              AND EXISTS (
                  SELECT 1
                  FROM sys.foreign_key_columns AS fkc
                  JOIN sys.columns AS parentColumn ON parentColumn.object_id = fkc.parent_object_id AND parentColumn.column_id = fkc.parent_column_id
                  JOIN sys.columns AS referencedColumn ON referencedColumn.object_id = fkc.referenced_object_id AND referencedColumn.column_id = fkc.referenced_column_id
                  WHERE fkc.constraint_object_id = fk.object_id
                    AND parentColumn.name = N'TerminalId'
                    AND referencedColumn.name = N'TerminalId'
              )
        )
            THROW 51611, N'POSM_LinklyCloudDeviceSelection terminal foreign key is incompatible.', 1;

        IF EXISTS (
            SELECT required.name
            FROM (VALUES
                (N'POSM_LinklyCloudTerminal', N'CK_POSM_LinklyCloudTerminal_Environment', N'environment', N'n''production''', N'n''sandbox''', N'__unused__', N'__unused__', 2),
                (N'POSM_LinklyCloudTerminal', N'CK_POSM_LinklyCloudTerminal_PairingState', N'pairingstate', N'n''unpaired''', N'n''ready''', N'n''unknown''', N'n''needsrepair''', 4),
                (N'POSM_LinklyCloudTerminal', N'CK_POSM_LinklyCloudTerminal_CredentialProtectionVersion', N'credentialprotectionversion', N'0', N'1', N'__unused__', N'__unused__', 2),
                (N'POSM_LinklyCloudDeviceSelection', N'CK_POSM_LinklyCloudDeviceSelection_Environment', N'environment', N'n''production''', N'n''sandbox''', N'__unused__', N'__unused__', 2),
                (N'POSM_LinklyCloudConfigurationMode', N'CK_POSM_LinklyCloudConfigurationMode_Environment', N'environment', N'n''production''', N'n''sandbox''', N'__unused__', N'__unused__', 2),
                (N'POSM_LinklyCloudConfigurationMode', N'CK_POSM_LinklyCloudConfigurationMode_Mode', N'mode', N'n''legacy''', N'n''draft''', N'n''active''', N'__unused__', 3)
            ) AS required(table_name, name, column_token, value1, value2, value3, value4, value_count)
            WHERE NOT EXISTS (
                SELECT 1
                FROM sys.check_constraints AS ck
                CROSS APPLY (VALUES (
                    LOWER(
                        REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                            COALESCE(ck.definition, N''),
                            N'[', N''), N']', N''), N'(', N''), N')', N''), N' ', N''),
                        NCHAR(9), N''), NCHAR(10), N''), NCHAR(13), N'')
                    ) COLLATE Latin1_General_100_BIN2
                )) AS normalized(definition)
                CROSS APPLY (VALUES (
                    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                        normalized.definition,
                        required.column_token + N'in', N''), required.value1, N''),
                        required.value2, N''), required.value3, N''), required.value4, N'')
                )) AS in_form(remainder)
                CROSS APPLY (VALUES (
                    REPLACE(REPLACE(REPLACE(REPLACE(
                        normalized.definition,
                        required.column_token + N'=' + required.value1, N''),
                        required.column_token + N'=' + required.value2, N''),
                        required.column_token + N'=' + required.value3, N''),
                        required.column_token + N'=' + required.value4, N'')
                )) AS or_form(remainder)
                WHERE ck.parent_object_id = OBJECT_ID(N'[dbo].[' + required.table_name + N']')
                  AND ck.name = required.name AND ck.is_disabled = 0 AND ck.is_not_trusted = 0
                  AND (
                      (
                          LEFT(normalized.definition, LEN(required.column_token) + 2)
                              = required.column_token + N'in'
                          AND in_form.remainder = REPLICATE(N',', required.value_count - 1)
                          AND LEN(normalized.definition)
                              - LEN(REPLACE(normalized.definition, required.column_token, N''))
                              = LEN(required.column_token)
                          AND LEN(normalized.definition)
                              - LEN(REPLACE(normalized.definition, required.value1, N''))
                              = LEN(required.value1)
                          AND LEN(normalized.definition)
                              - LEN(REPLACE(normalized.definition, required.value2, N''))
                              = LEN(required.value2)
                          AND (required.value_count < 3 OR LEN(normalized.definition)
                              - LEN(REPLACE(normalized.definition, required.value3, N''))
                              = LEN(required.value3))
                          AND (required.value_count < 4 OR LEN(normalized.definition)
                              - LEN(REPLACE(normalized.definition, required.value4, N''))
                              = LEN(required.value4))
                      )
                      OR
                      (
                          or_form.remainder = REPLICATE(N'or', required.value_count - 1)
                          AND LEN(normalized.definition)
                              - LEN(REPLACE(
                                  normalized.definition,
                                  required.column_token + N'=' + required.value1,
                                  N''))
                              = LEN(required.column_token + N'=' + required.value1)
                          AND LEN(normalized.definition)
                              - LEN(REPLACE(
                                  normalized.definition,
                                  required.column_token + N'=' + required.value2,
                                  N''))
                              = LEN(required.column_token + N'=' + required.value2)
                          AND (required.value_count < 3 OR LEN(normalized.definition)
                              - LEN(REPLACE(
                                  normalized.definition,
                                  required.column_token + N'=' + required.value3,
                                  N''))
                              = LEN(required.column_token + N'=' + required.value3))
                          AND (required.value_count < 4 OR LEN(normalized.definition)
                              - LEN(REPLACE(
                                  normalized.definition,
                                  required.column_token + N'=' + required.value4,
                                  N''))
                              = LEN(required.column_token + N'=' + required.value4))
                      )
                  )
            )
        )
            THROW 51612, N'Linkly multi-terminal check constraints are missing or incompatible.', 1;

        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes AS i
            WHERE i.object_id = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]')
              AND i.name = N'IX_POSM_LinklyCloudBackendSession_DeviceRecovery'
              AND i.is_unique = 0 AND i.is_disabled = 0 AND i.has_filter = 0 AND i.is_hypothetical = 0
              AND 6 = (SELECT COUNT(1) FROM sys.index_columns AS keysOnly WHERE keysOnly.object_id = i.object_id AND keysOnly.index_id = i.index_id AND keysOnly.key_ordinal > 0)
              AND 1 = (SELECT COUNT(1) FROM sys.index_columns AS includedOnly WHERE includedOnly.object_id = i.object_id AND includedOnly.index_id = i.index_id AND includedOnly.is_included_column = 1)
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 AND c.name = N'Environment')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 2 AND c.name = N'StoreCode')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 3 AND c.name = N'DeviceCode')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 4 AND c.name = N'IsActive')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 5 AND c.name = N'Status')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 6 AND c.name = N'ClientAcknowledgedAt')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1 AND c.name = N'UpdatedAt')
        )
            THROW 51613, N'POSM_LinklyCloudBackendSession device recovery index is incompatible.', 1;

        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes AS i
            WHERE i.object_id = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]')
              AND i.name = N'IX_POSM_LinklyCloudBackendSession_TerminalRecovery'
              AND i.is_unique = 0 AND i.is_disabled = 0 AND i.has_filter = 1 AND i.is_hypothetical = 0
              AND LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(COALESCE(i.filter_definition, N''), N'[', N''), N']', N''), N'(', N''), N')', N''), N' ', N'')) COLLATE Latin1_General_100_BIN2 = N'terminalidisnotnull'
              AND 6 = (SELECT COUNT(1) FROM sys.index_columns AS keysOnly WHERE keysOnly.object_id = i.object_id AND keysOnly.index_id = i.index_id AND keysOnly.key_ordinal > 0)
              AND 1 = (SELECT COUNT(1) FROM sys.index_columns AS includedOnly WHERE includedOnly.object_id = i.object_id AND includedOnly.index_id = i.index_id AND includedOnly.is_included_column = 1)
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 AND c.name = N'Environment')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 2 AND c.name = N'StoreCode')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 3 AND c.name = N'TerminalId')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 4 AND c.name = N'IsActive')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 5 AND c.name = N'Status')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 6 AND c.name = N'ClientAcknowledgedAt')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1 AND c.name = N'UpdatedAt')
        )
            THROW 51614, N'POSM_LinklyCloudBackendSession terminal recovery index is incompatible.', 1;

        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes AS i
            WHERE i.object_id = OBJECT_ID(N'[dbo].[POSM_LinklyCloudConfigurationMode]')
              AND i.name = N'PK_POSM_LinklyCloudConfigurationMode'
              AND i.is_primary_key = 1 AND i.is_unique = 1 AND i.type = 1 AND i.is_disabled = 0 AND i.is_hypothetical = 0
              AND 2 = (SELECT COUNT(1) FROM sys.index_columns AS keysOnly WHERE keysOnly.object_id = i.object_id AND keysOnly.index_id = i.index_id AND keysOnly.key_ordinal > 0)
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 AND c.name = N'Environment')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 2 AND c.name = N'StoreCode')
        )
            THROW 51615, N'POSM_LinklyCloudConfigurationMode primary key is incompatible.', 1;

        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes AS i
            WHERE i.object_id = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]')
              AND i.name = N'UX_POSM_LinklyCloudBackendSession_ActiveCloudTerminal'
              AND i.is_unique = 1 AND i.is_disabled = 0 AND i.has_filter = 1 AND i.is_hypothetical = 0
              AND LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(COALESCE(i.filter_definition, N''), N'[', N''), N']', N''), N'(', N''), N')', N''), N' ', N'')) COLLATE Latin1_General_100_BIN2 = N'isactive=1andterminalidisnotnull'
              AND 3 = (SELECT COUNT(1) FROM sys.index_columns AS keysOnly WHERE keysOnly.object_id = i.object_id AND keysOnly.index_id = i.index_id AND keysOnly.key_ordinal > 0)
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 AND c.name = N'Environment')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 2 AND c.name = N'StoreCode')
              AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 3 AND c.name = N'TerminalId')
        )
            THROW 51616, N'POSM_LinklyCloudBackendSession active terminal fence is incompatible.', 1;
        """;

    internal static IReadOnlyList<string> LinklyMultiTerminalSqlScriptsForTests { get; } =
    [
        EnsureLinklyBackendSessionDependencySql,
        EnsureLinklyBackendSessionIndexesSql,
        EnsureLinklyTerminalTableSql,
        EnsureLinklyTerminalLeaseColumnsSql,
        EnsureLinklyTerminalCredentialProtectionSql,
        EnsureLinklyTerminalCredentialProtectionConstraintSql,
        EnsureLinklyTerminalIndexesSql,
        EnsureLinklyDeviceSelectionTableSql,
        EnsureLinklyDeviceSelectionTerminalUniqueIndexSql,
        EnsureLinklyConfigurationModeTableSql,
        EnsureLinklyConfigurationModeLegacyPairingLeaseColumnsSql,
    ];

    internal static IReadOnlyList<string> SqlScriptsForTests { get; } =
    [
        EnsureSquareTokenTableSql,
        EnsureSquareTokenEnabledIndexSql,
        EnsureLinklyCredentialTableSql,
        EnsureLinklyEnvironmentColumnSql,
        NormalizeLinklyEnvironmentColumnSql,
        EnsureLinklyCredentialConstraintsSql,
        .. LinklyMultiTerminalSqlScriptsForTests,
    ];

    public static async Task EnsureLinklyMultiTerminalAsync(
        ISqlSugarClient db,
        ILogger logger
    )
    {
        await db.Ado.BeginTranAsync();
        try
        {
            // 追加迁移只触碰 Linkly v2 对象，不重放旧 Square/共享凭据的数据修正。
            await db.Ado.ExecuteCommandAsync(SchemaLockSql);
            foreach (var sql in LinklyMultiTerminalSqlScriptsForTests)
            {
                await db.Ado.ExecuteCommandAsync(sql);
            }

            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        logger.LogInformation("POSM Linkly 多终端表结构检查完成");
    }

    public static async Task EnsureAsync(ISqlSugarClient db, ILogger logger)
    {
        await db.Ado.BeginTranAsync();
        try
        {
            // Web 与 POS API 都会确保 Linkly 表；共用事务级应用锁，保证首次并发启动时只有一个建表者。
            await db.Ado.ExecuteCommandAsync(SchemaLockSql);
            foreach (var sql in SqlScriptsForTests)
            {
                await db.Ado.ExecuteCommandAsync(sql);
            }

            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        logger.LogInformation("POSM 支付终端配置表结构检查完成");
    }
}
