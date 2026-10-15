using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Data;

/// <summary>
/// 分店价格更新任务与仓库商品建议折扣的独立、可重复执行 schema 迁移。
/// </summary>
public static class StorePriceUpdateTaskSchemaMigrator
{
    internal const string SqlServerApplySql = """
SET XACT_ABORT ON;
BEGIN TRY
BEGIN TRANSACTION;
DECLARE @StorePriceTaskSchemaLockResult int;
EXEC @StorePriceTaskSchemaLockResult = sys.sp_getapplock
    @Resource = N'StorePriceUpdateTask_Schema_Initialization',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 30000;
IF @StorePriceTaskSchemaLockResult < 0
    THROW 51090, N'Unable to acquire StorePriceUpdateTask schema lock.', 1;

IF OBJECT_ID(N'[dbo].[ProductSuggestedDiscount]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ProductSuggestedDiscount] (
        [ProductCode] nvarchar(50) NOT NULL CONSTRAINT [PK_ProductSuggestedDiscount] PRIMARY KEY,
        [SuggestedDiscountRate] decimal(18, 4) NULL,
        [UpdatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_ProductSuggestedDiscount_UpdatedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [UpdatedBy] nvarchar(255) NULL,
        CONSTRAINT [CK_ProductSuggestedDiscount_Rate] CHECK (
            [SuggestedDiscountRate] IS NULL OR ([SuggestedDiscountRate] >= 0 AND [SuggestedDiscountRate] <= 1)
        )
    );
END;

IF OBJECT_ID(N'[dbo].[StorePriceUpdateTask]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[StorePriceUpdateTask] (
        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_StorePriceUpdateTask] PRIMARY KEY,
        [StoreCode] nvarchar(50) NOT NULL,
        [ProductCode] nvarchar(50) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [Kind] nvarchar(20) NOT NULL,
        [ShelfRetailPrice] decimal(18, 2) NULL,
        [ShelfDiscountRate] decimal(18, 4) NULL,
        [TargetRetailPrice] decimal(18, 2) NULL,
        [TargetDiscountRate] decimal(18, 4) NULL,
        [StoreRetailPrice] decimal(18, 2) NULL,
        [StoreDiscountRate] decimal(18, 4) NULL,
        [InitiatorName] nvarchar(100) NOT NULL,
        [InitiatorSource] nvarchar(80) NOT NULL,
        [InitiatorReference] nvarchar(200) NULL,
        [InitiatedAtUtc] datetime2 NOT NULL,
        [ChangeCount] int NOT NULL CONSTRAINT [DF_StorePriceUpdateTask_ChangeCount] DEFAULT(1),
        [PriceAppliedBy] nvarchar(100) NULL,
        [PriceAppliedAtUtc] datetime2 NULL,
        [CompletionMode] nvarchar(30) NULL,
        [CompletedBy] nvarchar(100) NULL,
        [CompletedAtUtc] datetime2 NULL,
        [LabelPrintCount] int NOT NULL CONSTRAINT [DF_StorePriceUpdateTask_LabelPrintCount] DEFAULT(0),
        [HqSyncOperationKey] nvarchar(200) NULL,
        [CancelReason] nvarchar(40) NULL,
        [CancelledBy] nvarchar(100) NULL,
        [CancelledAtUtc] datetime2 NULL,
        [CancelNote] nvarchar(200) NULL,
        [CreatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_StorePriceUpdateTask_CreatedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_StorePriceUpdateTask_UpdatedAtUtc] DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT [CK_StorePriceUpdateTask_Status] CHECK ([Status] IN (N'Pending', N'Completed', N'Cancelled')),
        CONSTRAINT [CK_StorePriceUpdateTask_Kind] CHECK ([Kind] IN (N'PriceUpdate', N'LabelOnly'))
    );
END;

-- 手动撤销（Web 监控页）留痕：谁、何时、备注。均为可空新列，旧数据不受影响；已存在则跳过，可重复执行。
IF OBJECT_ID(N'[dbo].[StorePriceUpdateTask]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'[dbo].[StorePriceUpdateTask]', N'CancelledBy') IS NULL
        ALTER TABLE [dbo].[StorePriceUpdateTask] ADD [CancelledBy] nvarchar(100) NULL;
    IF COL_LENGTH(N'[dbo].[StorePriceUpdateTask]', N'CancelledAtUtc') IS NULL
        ALTER TABLE [dbo].[StorePriceUpdateTask] ADD [CancelledAtUtc] datetime2 NULL;
    IF COL_LENGTH(N'[dbo].[StorePriceUpdateTask]', N'CancelNote') IS NULL
        ALTER TABLE [dbo].[StorePriceUpdateTask] ADD [CancelNote] nvarchar(200) NULL;
END;

-- 同一「分店 + 商品」最多一条 Pending：由过滤唯一索引在数据库层兜底。
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_StorePriceUpdateTask_Pending_Store_Product'
      AND [object_id] = OBJECT_ID(N'[dbo].[StorePriceUpdateTask]', N'U')
)
    CREATE UNIQUE INDEX [UX_StorePriceUpdateTask_Pending_Store_Product]
        ON [dbo].[StorePriceUpdateTask]([StoreCode], [ProductCode])
        WHERE [Status] = N'Pending';

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_StorePriceUpdateTask_Store_Status'
      AND [object_id] = OBJECT_ID(N'[dbo].[StorePriceUpdateTask]', N'U')
)
    CREATE INDEX [IX_StorePriceUpdateTask_Store_Status]
        ON [dbo].[StorePriceUpdateTask]([StoreCode], [Status], [InitiatedAtUtc] DESC)
        INCLUDE ([Kind], [CompletedAtUtc]);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_StorePriceUpdateTask_Product_Status'
      AND [object_id] = OBJECT_ID(N'[dbo].[StorePriceUpdateTask]', N'U')
)
    CREATE INDEX [IX_StorePriceUpdateTask_Product_Status]
        ON [dbo].[StorePriceUpdateTask]([ProductCode], [Status], [InitiatedAtUtc] DESC);

-- 启动流程不跑权限种子：新权限码不入库就无法在角色管理里授权（保存时会被静默丢弃）。
-- 这里幂等补一行；已存在（含已软删除）则不动，避免覆盖人工维护的数据。
IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM [dbo].[HbwebSysPermissions] WHERE [Code] = N'StoreProducts.SyncToOtherStores'
   )
BEGIN
    INSERT INTO [dbo].[HbwebSysPermissions]
        ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
    VALUES (
        LOWER(CONVERT(nvarchar(36), NEWID())),
        N'StoreProducts.SyncToOtherStores',
        N'同步价格到其它分店',
        N'分店商品管理',
        N'移动端「商品维护」- 把本店零售价/折扣同步到其它可管理分店，被同步分店会收到「待换标签」通知',
        SYSUTCDATETIME(), N'StartupSchemaMigrator', SYSUTCDATETIME(), N'StartupSchemaMigrator', 0
    );
END;

IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM [dbo].[HbwebSysPermissions] WHERE [Code] = N'StoreProducts.PriceUpdates'
   )
BEGIN
    INSERT INTO [dbo].[HbwebSysPermissions]
        ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
    VALUES (
        LOWER(CONVERT(nvarchar(36), NEWID())),
        N'StoreProducts.PriceUpdates',
        N'处理价格更新通知',
        N'分店商品管理',
        N'移动端「价格更新」- 查看本店价格更新与换标签通知，按仓库价更新本店零售价/折扣，打印或标记标签',
        SYSUTCDATETIME(), N'StartupSchemaMigrator', SYSUTCDATETIME(), N'StartupSchemaMigrator', 0
    );
END;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // SQLite 的 AUTOINCREMENT 只允许用在 INTEGER PRIMARY KEY 上，CodeFirst 会把 long 主键建成 BIGINT 而失败，
    // 因此与 WarehouseProductChangeHistory 的测试建表一致，这里直接用原生语句。
    private const string SqliteApplySql = """
CREATE TABLE IF NOT EXISTS "ProductSuggestedDiscount" (
    "ProductCode" TEXT NOT NULL PRIMARY KEY,
    "SuggestedDiscountRate" NUMERIC NULL,
    "UpdatedAtUtc" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL
);
CREATE TABLE IF NOT EXISTS "StorePriceUpdateTask" (
    "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
    "StoreCode" TEXT NOT NULL,
    "ProductCode" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "Kind" TEXT NOT NULL,
    "ShelfRetailPrice" NUMERIC NULL,
    "ShelfDiscountRate" NUMERIC NULL,
    "TargetRetailPrice" NUMERIC NULL,
    "TargetDiscountRate" NUMERIC NULL,
    "StoreRetailPrice" NUMERIC NULL,
    "StoreDiscountRate" NUMERIC NULL,
    "InitiatorName" TEXT NOT NULL,
    "InitiatorSource" TEXT NOT NULL,
    "InitiatorReference" TEXT NULL,
    "InitiatedAtUtc" TEXT NOT NULL,
    "ChangeCount" INTEGER NOT NULL DEFAULT 1,
    "PriceAppliedBy" TEXT NULL,
    "PriceAppliedAtUtc" TEXT NULL,
    "CompletionMode" TEXT NULL,
    "CompletedBy" TEXT NULL,
    "CompletedAtUtc" TEXT NULL,
    "LabelPrintCount" INTEGER NOT NULL DEFAULT 0,
    "HqSyncOperationKey" TEXT NULL,
    "CancelReason" TEXT NULL,
    "CancelledBy" TEXT NULL,
    "CancelledAtUtc" TEXT NULL,
    "CancelNote" TEXT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_StorePriceUpdateTask_Pending_Store_Product"
    ON "StorePriceUpdateTask"("StoreCode", "ProductCode")
    WHERE "Status" = 'Pending';
CREATE INDEX IF NOT EXISTS "IX_StorePriceUpdateTask_Store_Status"
    ON "StorePriceUpdateTask"("StoreCode", "Status", "InitiatedAtUtc" DESC);
CREATE INDEX IF NOT EXISTS "IX_StorePriceUpdateTask_Product_Status"
    ON "StorePriceUpdateTask"("ProductCode", "Status", "InitiatedAtUtc" DESC);
""";

    public static async Task EnsureAsync(ISqlSugarClient db, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            if (db.CurrentConnectionConfig.DbType == DbType.Sqlite)
            {
                await db.Ado.ExecuteCommandAsync(SqliteApplySql);
                return;
            }

            if (db.CurrentConnectionConfig.DbType != DbType.SqlServer)
            {
                return;
            }

            await db.Ado.ExecuteCommandAsync(SqlServerApplySql);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "StorePriceUpdateTask schema 迁移失败");
            throw;
        }
    }
}
