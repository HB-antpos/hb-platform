using Hbpos.Api.Data;

namespace Hbpos.Api.Services;

public interface IInstallmentCancelClaimSchemaInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IInstallmentCancelClaimSchemaSqlExecutor
{
    Task ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}

public sealed class SqlSugarInstallmentCancelClaimSchemaInitializer(
    IInstallmentCancelClaimSchemaSqlExecutor sqlExecutor)
    : IInstallmentCancelClaimSchemaInitializer
{
    internal const string EnsureTableSql = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        DECLARE @SchemaLockResult INT;
        EXEC @SchemaLockResult = sys.sp_getapplock
            @Resource = N'Hbpos.InstallmentCancelClaim.Schema.v1',
            @LockMode = N'Exclusive',
            @LockOwner = N'Transaction',
            @LockTimeout = 60000;
        IF @SchemaLockResult < 0
            THROW 51000, 'Could not acquire the installment cancellation claim schema lock.', 1;

        IF OBJECT_ID(N'[dbo].[POSM_InstallmentCancelClaim]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_InstallmentCancelClaim] (
                [OperationGuid] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [PK_POSM_InstallmentCancelClaim] PRIMARY KEY,
                [InstallmentGuid] UNIQUEIDENTIFIER NOT NULL,
                [StoreCode] NVARCHAR(50) NOT NULL,
                [OriginalDeviceCode] NVARCHAR(50) NOT NULL,
                [ClaimantDeviceCode] NVARCHAR(50) NOT NULL,
                [CashierId] NVARCHAR(50) NOT NULL,
                [CashierName] NVARCHAR(100) NOT NULL,
                [IdempotencyKey] NVARCHAR(100) NOT NULL,
                [Reason] NVARCHAR(500) NULL,
                [RefundPlanFingerprint] CHAR(71) NOT NULL,
                [Status] NVARCHAR(32) NOT NULL,
                [IsBlocking] BIT NOT NULL,
                [CreatedAtUtc] DATETIME2(7) NOT NULL,
                [UpdatedAtUtc] DATETIME2(7) NOT NULL,
                [ExpiresAtUtc] DATETIME2(7) NULL,
                [CommittedAtUtc] DATETIME2(7) NULL,
                [CommitResponseJson] NVARCHAR(MAX) NULL,
                [LastRecoveryCashierId] NVARCHAR(50) NULL,
                [LastRecoveryCashierName] NVARCHAR(100) NULL,
                [LastRecoveryCashierUserGuid] NVARCHAR(50) NULL,
                [RecoveredAtUtc] DATETIME2(7) NULL,
                [RefundMode] NVARCHAR(20) NULL,
                [Revision] BIGINT NOT NULL,
                CONSTRAINT [CK_POSM_InstallmentCancelClaim_Fingerprint]
                    CHECK ([RefundPlanFingerprint] LIKE N'sha256:%' AND LEN([RefundPlanFingerprint]) = 71),
                CONSTRAINT [CK_POSM_InstallmentCancelClaim_Status]
                    CHECK ([Status] IN (N'Prepared', N'RefundPending', N'Committed', N'Released', N'Declined', N'Unknown')),
                CONSTRAINT [CK_POSM_InstallmentCancelClaim_Blocking]
                    CHECK (([IsBlocking] = 1 AND [Status] IN (N'Prepared', N'RefundPending', N'Unknown')) OR
                           ([IsBlocking] = 0 AND [Status] IN (N'Committed', N'Released', N'Declined'))),
                CONSTRAINT [CK_POSM_InstallmentCancelClaim_Revision] CHECK ([Revision] > 0)
            );
        END;

        IF OBJECT_ID(N'[dbo].[POSM_InstallmentCancelClaim]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.POSM_InstallmentCancelClaim', N'OriginalDeviceCode') IS NULL
        BEGIN
            ALTER TABLE [dbo].[POSM_InstallmentCancelClaim]
                ADD [OriginalDeviceCode] NVARCHAR(50) NULL;
            -- SQL Server 会先编译整个 batch；新增列后的回填与改为 NOT NULL 必须重新编译。
            EXEC(N'UPDATE [dbo].[POSM_InstallmentCancelClaim]
                SET [OriginalDeviceCode] = [ClaimantDeviceCode]
                WHERE [OriginalDeviceCode] IS NULL;');
            EXEC(N'ALTER TABLE [dbo].[POSM_InstallmentCancelClaim]
                ALTER COLUMN [OriginalDeviceCode] NVARCHAR(50) NOT NULL;');
        END;

        -- v1 可在首建或灰度期间重复执行；已存在表也必须补齐 commit 快照与恢复者审计列。
        IF OBJECT_ID(N'[dbo].[POSM_InstallmentCancelClaim]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.POSM_InstallmentCancelClaim', N'CommitResponseJson') IS NULL
        BEGIN
            ALTER TABLE [dbo].[POSM_InstallmentCancelClaim]
                ADD [CommitResponseJson] NVARCHAR(MAX) NULL;
        END;

        IF OBJECT_ID(N'[dbo].[POSM_InstallmentCancelClaim]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.POSM_InstallmentCancelClaim', N'LastRecoveryCashierId') IS NULL
        BEGIN
            ALTER TABLE [dbo].[POSM_InstallmentCancelClaim]
                ADD [LastRecoveryCashierId] NVARCHAR(50) NULL;
        END;

        IF OBJECT_ID(N'[dbo].[POSM_InstallmentCancelClaim]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.POSM_InstallmentCancelClaim', N'LastRecoveryCashierName') IS NULL
        BEGIN
            ALTER TABLE [dbo].[POSM_InstallmentCancelClaim]
                ADD [LastRecoveryCashierName] NVARCHAR(100) NULL;
        END;

        IF OBJECT_ID(N'[dbo].[POSM_InstallmentCancelClaim]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.POSM_InstallmentCancelClaim', N'LastRecoveryCashierUserGuid') IS NULL
        BEGIN
            ALTER TABLE [dbo].[POSM_InstallmentCancelClaim]
                ADD [LastRecoveryCashierUserGuid] NVARCHAR(50) NULL;
        END;

        IF OBJECT_ID(N'[dbo].[POSM_InstallmentCancelClaim]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.POSM_InstallmentCancelClaim', N'RecoveredAtUtc') IS NULL
        BEGIN
            ALTER TABLE [dbo].[POSM_InstallmentCancelClaim]
                ADD [RecoveredAtUtc] DATETIME2(7) NULL;
        END;

        -- 取消退款方式：NULL=原路退（升级前的 claim 均为此口径），Voucher=全部改发退款代金券。
        IF OBJECT_ID(N'[dbo].[POSM_InstallmentCancelClaim]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.POSM_InstallmentCancelClaim', N'RefundMode') IS NULL
        BEGIN
            ALTER TABLE [dbo].[POSM_InstallmentCancelClaim]
                ADD [RefundMode] NVARCHAR(20) NULL;
        END;

        IF OBJECT_ID(N'[dbo].[POSM_InstallmentCancelClaim]', N'U') IS NOT NULL
           AND OBJECT_ID(N'[dbo].[CK_POSM_InstallmentCancelClaim_RefundMode]', N'C') IS NULL
        BEGIN
            -- 新列在同一 batch 内编译期不可见，约束须经 EXEC 重新编译。
            EXEC(N'ALTER TABLE [dbo].[POSM_InstallmentCancelClaim]
                ADD CONSTRAINT [CK_POSM_InstallmentCancelClaim_RefundMode]
                CHECK ([RefundMode] IS NULL OR [RefundMode] IN (N''OriginalRoute'', N''Voucher''));');
        END;

        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_InstallmentCancelClaim]', N'U')
              AND [name] = N'UX_POSM_InstallmentCancelClaim_Idempotency')
        BEGIN
            CREATE UNIQUE INDEX [UX_POSM_InstallmentCancelClaim_Idempotency]
                ON [dbo].[POSM_InstallmentCancelClaim] ([InstallmentGuid], [IdempotencyKey]);
        END;

        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_InstallmentCancelClaim]', N'U')
              AND [name] = N'UX_POSM_InstallmentCancelClaim_Blocking')
        BEGIN
            CREATE UNIQUE INDEX [UX_POSM_InstallmentCancelClaim_Blocking]
                ON [dbo].[POSM_InstallmentCancelClaim] ([InstallmentGuid])
                WHERE [IsBlocking] = 1;
        END;

        COMMIT TRANSACTION;
        """;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        sqlExecutor.ExecuteAsync(EnsureTableSql, cancellationToken);
}

public sealed class SqlSugarInstallmentCancelClaimSchemaSqlExecutor(
    HbposSqlSugarContext dbContext) : IInstallmentCancelClaimSchemaSqlExecutor
{
    public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(sql);
    }
}
