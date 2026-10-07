using Hbpos.Api.Data;

namespace Hbpos.Api.Services;

public interface IDailyCloseSchemaInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IDailyCloseSchemaSqlExecutor
{
    Task ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}

/// <summary>
/// 启动时幂等创建 POSM_DailyClose。该表同时被两类写入方使用：本 API 的收银端上传（ClientUpload）
/// 与运维回填脚本（AuditBackfill）；后台只读查询另有独立实体，所以这里不做任何结构迁移，只建表与索引。
/// </summary>
public sealed class SqlSugarDailyCloseSchemaInitializer(
    IDailyCloseSchemaSqlExecutor sqlExecutor) : IDailyCloseSchemaInitializer
{
    internal const string EnsureTableSql = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        -- 多实例同时启动时用应用锁串行化建表，避免并发 CREATE TABLE / CREATE INDEX 互相失败。
        DECLARE @SchemaLockResult INT;
        EXEC @SchemaLockResult = sys.sp_getapplock
            @Resource = N'Hbpos.DailyClose.Schema.v1',
            @LockMode = N'Exclusive',
            @LockOwner = N'Transaction',
            @LockTimeout = 60000;
        IF @SchemaLockResult < 0
            THROW 51000, 'Could not acquire the daily close schema lock.', 1;

        IF OBJECT_ID(N'[dbo].[POSM_DailyClose]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_DailyClose] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_DailyClose] PRIMARY KEY,
                [DailyCloseGuid] UNIQUEIDENTIFIER NOT NULL,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [DeviceCode] NVARCHAR(64) NOT NULL,
                [ClientKind] NVARCHAR(16) NOT NULL,
                [DetailLevel] NVARCHAR(16) NOT NULL,
                [DataSource] NVARCHAR(24) NOT NULL,
                [BackfillBatch] NVARCHAR(64) NULL,
                [BusinessDate] DATE NOT NULL,
                [BusinessDateInferred] BIT NOT NULL CONSTRAINT [DF_POSM_DailyClose_BusinessDateInferred] DEFAULT (0),
                [PeriodFromUtc] DATETIME2(7) NULL,
                [PeriodToUtc] DATETIME2(7) NULL,
                [CashierId] NVARCHAR(64) NOT NULL,
                [CashierName] NVARCHAR(128) NOT NULL,
                [SavedAtUtc] DATETIME2(7) NOT NULL,
                [AppVersion] NVARCHAR(64) NULL,
                [OrderCount] INT NULL,
                [ReturnQuantity] DECIMAL(18,3) NULL,
                [CashSalesAmount] DECIMAL(18,2) NULL,
                [CashRefundAmount] DECIMAL(18,2) NULL,
                [CashNetAmount] DECIMAL(18,2) NULL,
                [CardSalesAmount] DECIMAL(18,2) NULL,
                [CardRefundAmount] DECIMAL(18,2) NULL,
                [CardNetAmount] DECIMAL(18,2) NULL,
                [VoucherSalesAmount] DECIMAL(18,2) NULL,
                [VoucherRefundAmount] DECIMAL(18,2) NULL,
                [VoucherNetAmount] DECIMAL(18,2) NULL,
                [RefundAmount] DECIMAL(18,2) NULL,
                [ExpectedCashAmount] DECIMAL(18,2) NULL,
                [CountedCashAmount] DECIMAL(18,2) NULL,
                [CashDifference] DECIMAL(18,2) NULL,
                [NoteSubtotal] DECIMAL(18,2) NULL,
                [CoinSubtotal] DECIMAL(18,2) NULL,
                [CashCountsJson] NVARCHAR(MAX) NULL,
                [ReceivedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_DailyClose_ReceivedAtUtc] DEFAULT (SYSUTCDATETIME()),
                [UpdatedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_DailyClose_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
                CONSTRAINT [CK_POSM_DailyClose_ClientKind]
                    CHECK ([ClientKind] IN (N'Wpf', N'Handheld', N'Ipad')),
                CONSTRAINT [CK_POSM_DailyClose_DetailLevel]
                    CHECK ([DetailLevel] IN (N'Full', N'CashOnly', N'TraceOnly')),
                CONSTRAINT [CK_POSM_DailyClose_DataSource]
                    CHECK ([DataSource] IN (N'ClientUpload', N'AuditBackfill'))
            );
        END;

        -- DailyCloseGuid 全局唯一：上传幂等键，同时挡住并发重复插入。
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_DailyClose]', N'U')
              AND [name] = N'UX_POSM_DailyClose_Guid')
        BEGIN
            CREATE UNIQUE INDEX [UX_POSM_DailyClose_Guid]
                ON [dbo].[POSM_DailyClose] ([DailyCloseGuid]);
        END;

        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_DailyClose]', N'U')
              AND [name] = N'IX_POSM_DailyClose_StoreBusinessDate')
        BEGIN
            CREATE INDEX [IX_POSM_DailyClose_StoreBusinessDate]
                ON [dbo].[POSM_DailyClose] ([StoreCode], [BusinessDate] DESC, [DeviceCode], [SavedAtUtc] DESC);
        END;

        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_DailyClose]', N'U')
              AND [name] = N'IX_POSM_DailyClose_BusinessDate')
        BEGIN
            CREATE INDEX [IX_POSM_DailyClose_BusinessDate]
                ON [dbo].[POSM_DailyClose] ([BusinessDate] DESC, [SavedAtUtc] DESC);
        END;

        COMMIT TRANSACTION;
        """;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return sqlExecutor.ExecuteAsync(EnsureTableSql, cancellationToken);
    }
}

public sealed class SqlSugarDailyCloseSchemaSqlExecutor(
    HbposSqlSugarContext dbContext) : IDailyCloseSchemaSqlExecutor
{
    public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(sql);
    }
}
