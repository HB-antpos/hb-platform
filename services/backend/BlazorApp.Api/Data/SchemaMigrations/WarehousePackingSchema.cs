namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 仓库扫码打包：箱、箱明细、托盘、运输公司、操作记录、发号表。
/// 走独立版本号迁移，生产须显式执行 --schema=migrate；实体不进 SqlSugarContext 的自动建表清单。
/// </summary>
internal static class WarehousePackingSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.WarehousePackCarton', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[WarehousePackCarton]
        (
            [CartonGUID] nvarchar(32) NOT NULL,
            [CartonNo] nvarchar(20) NOT NULL,
            [Status] int NOT NULL,
            [OriginName] nvarchar(100) NOT NULL,
            [DestinationType] int NOT NULL,
            [DestinationStoreCode] nvarchar(50) NULL,
            [CarrierGUID] nvarchar(32) NULL,
            [OrderGUID] nvarchar(50) NULL,
            [PalletGUID] nvarchar(32) NULL,
            [LocationCode] nvarchar(50) NULL,
            [WeightKg] decimal(9, 2) NULL,
            [Remark] nvarchar(200) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [CreatedByUserGuid] nvarchar(50) NOT NULL,
            [CreatedByName] nvarchar(100) NOT NULL,
            [SealedAtUtc] datetime2 NULL,
            [SealedByUserGuid] nvarchar(50) NULL,
            [SealedByName] nvarchar(100) NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_WarehousePackCarton] PRIMARY KEY CLUSTERED ([CartonGUID])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.WarehousePackCarton')
          AND [name] = N'UX_WarehousePackCarton_CartonNo'
    )
    BEGIN
        -- 箱号就是箱标签上的条码内容，必须全局唯一。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_WarehousePackCarton_CartonNo]
            ON [dbo].[WarehousePackCarton] ([CartonNo]);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.WarehousePackCarton')
          AND [name] = N'IX_WarehousePackCarton_Pallet'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_WarehousePackCarton_Pallet]
            ON [dbo].[WarehousePackCarton] ([PalletGUID])
            INCLUDE ([Status], [CartonNo]);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.WarehousePackCarton')
          AND [name] = N'IX_WarehousePackCarton_Status_Created'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_WarehousePackCarton_Status_Created]
            ON [dbo].[WarehousePackCarton] ([Status], [CreatedAtUtc] DESC);
    END;

    IF OBJECT_ID(N'dbo.WarehousePackCartonItem', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[WarehousePackCartonItem]
        (
            [CartonGUID] nvarchar(32) NOT NULL,
            [ProductCode] nvarchar(50) NOT NULL,
            [Quantity] int NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            [UpdatedByName] nvarchar(100) NOT NULL,
            CONSTRAINT [PK_WarehousePackCartonItem] PRIMARY KEY CLUSTERED ([CartonGUID], [ProductCode])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.WarehousePackCartonItem')
          AND [name] = N'IX_WarehousePackCartonItem_Product'
    )
    BEGIN
        -- 「商品位置查询」按商品找箱。
        CREATE NONCLUSTERED INDEX [IX_WarehousePackCartonItem_Product]
            ON [dbo].[WarehousePackCartonItem] ([ProductCode])
            INCLUDE ([Quantity]);
    END;

    IF OBJECT_ID(N'dbo.WarehousePackPallet', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[WarehousePackPallet]
        (
            [PalletGUID] nvarchar(32) NOT NULL,
            [PalletNo] nvarchar(20) NOT NULL,
            [Status] int NOT NULL,
            [OriginName] nvarchar(100) NOT NULL,
            [DestinationType] int NOT NULL,
            [DestinationStoreCode] nvarchar(50) NULL,
            [CarrierGUID] nvarchar(32) NULL,
            [ConsignmentNo] nvarchar(100) NULL,
            [LocationCode] nvarchar(50) NULL,
            [WeightKg] decimal(9, 2) NULL,
            [Remark] nvarchar(200) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [CreatedByUserGuid] nvarchar(50) NOT NULL,
            [CreatedByName] nvarchar(100) NOT NULL,
            [SealedAtUtc] datetime2 NULL,
            [SealedByUserGuid] nvarchar(50) NULL,
            [SealedByName] nvarchar(100) NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_WarehousePackPallet] PRIMARY KEY CLUSTERED ([PalletGUID])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.WarehousePackPallet')
          AND [name] = N'UX_WarehousePackPallet_PalletNo'
    )
    BEGIN
        CREATE UNIQUE NONCLUSTERED INDEX [UX_WarehousePackPallet_PalletNo]
            ON [dbo].[WarehousePackPallet] ([PalletNo]);
    END;

    IF OBJECT_ID(N'dbo.WarehousePackCarrier', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[WarehousePackCarrier]
        (
            [CarrierGUID] nvarchar(32) NOT NULL,
            [Name] nvarchar(100) NOT NULL,
            [Remark] nvarchar(200) NULL,
            [IsActive] bit NOT NULL CONSTRAINT [DF_WarehousePackCarrier_IsActive] DEFAULT (1),
            [CreatedAtUtc] datetime2 NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_WarehousePackCarrier] PRIMARY KEY CLUSTERED ([CarrierGUID])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.WarehousePackCarrier')
          AND [name] = N'UX_WarehousePackCarrier_Name'
    )
    BEGIN
        CREATE UNIQUE NONCLUSTERED INDEX [UX_WarehousePackCarrier_Name]
            ON [dbo].[WarehousePackCarrier] ([Name]);
    END;

    IF OBJECT_ID(N'dbo.WarehousePackEvent', N'U') IS NULL
    BEGIN
        -- 只追加：箱 / 托盘的操作轨迹。
        CREATE TABLE [dbo].[WarehousePackEvent]
        (
            [EventGUID] nvarchar(32) NOT NULL,
            [TargetType] int NOT NULL,
            [TargetGUID] nvarchar(32) NOT NULL,
            [Action] int NOT NULL,
            [Detail] nvarchar(400) NULL,
            [OperatorUserGuid] nvarchar(50) NOT NULL,
            [OperatorName] nvarchar(100) NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_WarehousePackEvent] PRIMARY KEY CLUSTERED ([EventGUID])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.WarehousePackEvent')
          AND [name] = N'IX_WarehousePackEvent_Target'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_WarehousePackEvent_Target]
            ON [dbo].[WarehousePackEvent] ([TargetType], [TargetGUID], [CreatedAtUtc]);
    END;

    IF OBJECT_ID(N'dbo.WarehousePackSequence', N'U') IS NULL
    BEGIN
        -- 发号表：一个「前缀 + 年月日」一行，靠行锁保证并发不重号。
        CREATE TABLE [dbo].[WarehousePackSequence]
        (
            [SequenceKey] nvarchar(20) NOT NULL,
            [LastValue] int NOT NULL,
            CONSTRAINT [PK_WarehousePackSequence] PRIMARY KEY CLUSTERED ([SequenceKey])
        );
    END;

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库，否则角色管理里无法单独授予（保存会被静默丢弃）。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), N'Warehouse.Packing', N'仓库扫码打包', N'仓库管理',
               N'移动端与 Web「扫码打包」- 扫码装箱、封箱打印箱标签、组托盘、指定去向并查询商品位置；持有管理仓库或管理仓库订货权限时自动具备',
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        WHERE NOT EXISTS (
            SELECT 1 FROM [dbo].[HbwebSysPermissions] AS [existing] WHERE [existing].[Code] = N'Warehouse.Packing'
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、可空性、主键与唯一索引；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.WarehousePackCarton', N'U') IS NULL
 OR OBJECT_ID(N'dbo.WarehousePackCartonItem', N'U') IS NULL
 OR OBJECT_ID(N'dbo.WarehousePackPallet', N'U') IS NULL
 OR OBJECT_ID(N'dbo.WarehousePackCarrier', N'U') IS NULL
 OR OBJECT_ID(N'dbo.WarehousePackEvent', N'U') IS NULL
 OR OBJECT_ID(N'dbo.WarehousePackSequence', N'U') IS NULL
    THROW 54480, N'Warehouse packing tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'WarehousePackCarton', N'CartonGUID', N'nvarchar', 64, 0),
      (N'WarehousePackCarton', N'CartonNo', N'nvarchar', 40, 0),
      (N'WarehousePackCarton', N'Status', N'int', 0, 0),
      (N'WarehousePackCarton', N'OriginName', N'nvarchar', 200, 0),
      (N'WarehousePackCarton', N'DestinationType', N'int', 0, 0),
      (N'WarehousePackCarton', N'DestinationStoreCode', N'nvarchar', 100, 1),
      (N'WarehousePackCarton', N'CarrierGUID', N'nvarchar', 64, 1),
      (N'WarehousePackCarton', N'OrderGUID', N'nvarchar', 100, 1),
      (N'WarehousePackCarton', N'PalletGUID', N'nvarchar', 64, 1),
      (N'WarehousePackCarton', N'LocationCode', N'nvarchar', 100, 1),
      (N'WarehousePackCarton', N'WeightKg', N'decimal', 0, 1),
      (N'WarehousePackCarton', N'Remark', N'nvarchar', 400, 1),
      (N'WarehousePackCarton', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'WarehousePackCarton', N'CreatedByUserGuid', N'nvarchar', 100, 0),
      (N'WarehousePackCarton', N'CreatedByName', N'nvarchar', 200, 0),
      (N'WarehousePackCarton', N'SealedAtUtc', N'datetime2', 0, 1),
      (N'WarehousePackCarton', N'SealedByUserGuid', N'nvarchar', 100, 1),
      (N'WarehousePackCarton', N'SealedByName', N'nvarchar', 200, 1),
      (N'WarehousePackCarton', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'WarehousePackCartonItem', N'CartonGUID', N'nvarchar', 64, 0),
      (N'WarehousePackCartonItem', N'ProductCode', N'nvarchar', 100, 0),
      (N'WarehousePackCartonItem', N'Quantity', N'int', 0, 0),
      (N'WarehousePackCartonItem', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'WarehousePackCartonItem', N'UpdatedByName', N'nvarchar', 200, 0),
      (N'WarehousePackPallet', N'PalletGUID', N'nvarchar', 64, 0),
      (N'WarehousePackPallet', N'PalletNo', N'nvarchar', 40, 0),
      (N'WarehousePackPallet', N'Status', N'int', 0, 0),
      (N'WarehousePackPallet', N'OriginName', N'nvarchar', 200, 0),
      (N'WarehousePackPallet', N'DestinationType', N'int', 0, 0),
      (N'WarehousePackPallet', N'DestinationStoreCode', N'nvarchar', 100, 1),
      (N'WarehousePackPallet', N'CarrierGUID', N'nvarchar', 64, 1),
      (N'WarehousePackPallet', N'ConsignmentNo', N'nvarchar', 200, 1),
      (N'WarehousePackPallet', N'LocationCode', N'nvarchar', 100, 1),
      (N'WarehousePackPallet', N'WeightKg', N'decimal', 0, 1),
      (N'WarehousePackPallet', N'Remark', N'nvarchar', 400, 1),
      (N'WarehousePackPallet', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'WarehousePackPallet', N'CreatedByUserGuid', N'nvarchar', 100, 0),
      (N'WarehousePackPallet', N'CreatedByName', N'nvarchar', 200, 0),
      (N'WarehousePackPallet', N'SealedAtUtc', N'datetime2', 0, 1),
      (N'WarehousePackPallet', N'SealedByUserGuid', N'nvarchar', 100, 1),
      (N'WarehousePackPallet', N'SealedByName', N'nvarchar', 200, 1),
      (N'WarehousePackPallet', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'WarehousePackCarrier', N'CarrierGUID', N'nvarchar', 64, 0),
      (N'WarehousePackCarrier', N'Name', N'nvarchar', 200, 0),
      (N'WarehousePackCarrier', N'Remark', N'nvarchar', 400, 1),
      (N'WarehousePackCarrier', N'IsActive', N'bit', 0, 0),
      (N'WarehousePackCarrier', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'WarehousePackCarrier', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'WarehousePackEvent', N'EventGUID', N'nvarchar', 64, 0),
      (N'WarehousePackEvent', N'TargetType', N'int', 0, 0),
      (N'WarehousePackEvent', N'TargetGUID', N'nvarchar', 64, 0),
      (N'WarehousePackEvent', N'Action', N'int', 0, 0),
      (N'WarehousePackEvent', N'Detail', N'nvarchar', 800, 1),
      (N'WarehousePackEvent', N'OperatorUserGuid', N'nvarchar', 100, 0),
      (N'WarehousePackEvent', N'OperatorName', N'nvarchar', 200, 0),
      (N'WarehousePackEvent', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'WarehousePackSequence', N'SequenceKey', N'nvarchar', 40, 0),
      (N'WarehousePackSequence', N'LastValue', N'int', 0, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54481, N'Warehouse packing column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.WarehousePackCarton') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.WarehousePackCartonItem') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.WarehousePackPallet') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.WarehousePackCarrier') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.WarehousePackEvent') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.WarehousePackSequence') AND [type] = N'PK')
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.WarehousePackCarton')
      AND [name] = N'UX_WarehousePackCarton_CartonNo' AND [is_unique] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.WarehousePackPallet')
      AND [name] = N'UX_WarehousePackPallet_PalletNo' AND [is_unique] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.WarehousePackCarrier')
      AND [name] = N'UX_WarehousePackCarrier_Name' AND [is_unique] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.WarehousePackCartonItem')
      AND [name] = N'IX_WarehousePackCartonItem_Product'
 )
    THROW 54482, N'Warehouse packing index signature is incompatible.', 1;
""";
}
