namespace Hbpos.Api.Services;

public interface ICashMovementSchemaInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 启动时幂等创建 POSM_CashMovement（收银端现金存入 / 取出明细）。复用日结的 SQL 执行器（同一个 POSM 库）。
/// 记录只增不改：输错由一条 VoidsMovementGuid 指向原记录的冲正记录抵消，筛选唯一索引保证每条原记录最多被冲正一次。
/// </summary>
public sealed class SqlSugarCashMovementSchemaInitializer(
    IDailyCloseSchemaSqlExecutor sqlExecutor) : ICashMovementSchemaInitializer
{
    internal const string EnsureTableSql = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        -- 多实例同时启动时用应用锁串行化建表。
        DECLARE @SchemaLockResult INT;
        EXEC @SchemaLockResult = sys.sp_getapplock
            @Resource = N'Hbpos.CashMovement.Schema.v1',
            @LockMode = N'Exclusive',
            @LockOwner = N'Transaction',
            @LockTimeout = 60000;
        IF @SchemaLockResult < 0
            THROW 51000, 'Could not acquire the cash movement schema lock.', 1;

        IF OBJECT_ID(N'[dbo].[POSM_CashMovement]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_CashMovement] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_CashMovement] PRIMARY KEY,
                [MovementGuid] UNIQUEIDENTIFIER NOT NULL,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [DeviceCode] NVARCHAR(64) NOT NULL,
                [ClientKind] NVARCHAR(16) NOT NULL,
                [Direction] NVARCHAR(8) NOT NULL,
                [ReasonCode] NVARCHAR(24) NOT NULL,
                [Amount] DECIMAL(18,2) NOT NULL,
                [Note] NVARCHAR(200) NULL,
                [VoidsMovementGuid] UNIQUEIDENTIFIER NULL,
                [BusinessDate] DATE NOT NULL,
                [OccurredAtUtc] DATETIME2(7) NOT NULL,
                [CashierId] NVARCHAR(64) NOT NULL,
                [CashierName] NVARCHAR(128) NOT NULL,
                [AuthorizedById] NVARCHAR(64) NULL,
                [AuthorizedByName] NVARCHAR(128) NULL,
                [DisplayNumber] NVARCHAR(32) NULL,
                [AppVersion] NVARCHAR(64) NULL,
                [ReceivedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_CashMovement_ReceivedAtUtc] DEFAULT (SYSUTCDATETIME()),
                CONSTRAINT [CK_POSM_CashMovement_ClientKind]
                    CHECK ([ClientKind] IN (N'Wpf', N'Handheld', N'Ipad')),
                CONSTRAINT [CK_POSM_CashMovement_Direction]
                    CHECK ([Direction] IN (N'In', N'Out')),
                CONSTRAINT [CK_POSM_CashMovement_ReasonCode]
                    CHECK ([ReasonCode] IN (N'ChangeTopUp', N'OtherIn', N'SafeDrop', N'PaidOut')),
                CONSTRAINT [CK_POSM_CashMovement_Amount]
                    CHECK ([Amount] > 0)
            );
        END;

        -- MovementGuid 全局唯一：上传幂等键。
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_CashMovement]', N'U')
              AND [name] = N'UX_POSM_CashMovement_Guid')
        BEGIN
            CREATE UNIQUE INDEX [UX_POSM_CashMovement_Guid]
                ON [dbo].[POSM_CashMovement] ([MovementGuid]);
        END;

        -- 每条原记录最多一条冲正记录。
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_CashMovement]', N'U')
              AND [name] = N'UX_POSM_CashMovement_Voids')
        BEGIN
            CREATE UNIQUE INDEX [UX_POSM_CashMovement_Voids]
                ON [dbo].[POSM_CashMovement] ([VoidsMovementGuid])
                WHERE [VoidsMovementGuid] IS NOT NULL;
        END;

        -- 后台按分店、营业日查明细（现金管理「每日」页签）。
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_CashMovement]', N'U')
              AND [name] = N'IX_POSM_CashMovement_StoreBusinessDate')
        BEGIN
            CREATE INDEX [IX_POSM_CashMovement_StoreBusinessDate]
                ON [dbo].[POSM_CashMovement] ([StoreCode], [BusinessDate] DESC, [DeviceCode], [OccurredAtUtc]);
        END;

        COMMIT TRANSACTION;
        """;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return sqlExecutor.ExecuteAsync(EnsureTableSql, cancellationToken);
    }
}
