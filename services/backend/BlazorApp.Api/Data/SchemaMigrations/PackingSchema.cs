namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 扫码打包：箱、箱明细、托盘、运输公司、分店存放位置、操作记录、发号表。
/// 每只箱 / 托盘都有所属地点（分店编码或总仓），店间调货靠「发出 → 对方签收」换所属地点。
/// 走独立版本号迁移，生产须显式执行 --schema=migrate；实体不进 SqlSugarContext 的自动建表清单。
/// 本文件由脚本按同一份列定义生成建表与校验两段 SQL，手改请两段同步。
/// </summary>
internal static class PackingSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.PackCarton', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PackCarton]
        (
            [CartonGUID] nvarchar(32) NOT NULL,
            [CartonNo] nvarchar(20) NOT NULL,
            [Status] int NOT NULL,
            [OwnerCode] nvarchar(50) NOT NULL,
            [OriginCode] nvarchar(50) NOT NULL,
            [OriginName] nvarchar(100) NOT NULL,
            [DestinationType] int NOT NULL,
            [DestinationCode] nvarchar(50) NULL,
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
            [DispatchedAtUtc] datetime2 NULL,
            [DispatchedByName] nvarchar(100) NULL,
            [ReceivedAtUtc] datetime2 NULL,
            [ReceivedByName] nvarchar(100) NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_PackCarton] PRIMARY KEY CLUSTERED ([CartonGUID])
        );
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackCarton')
          AND [name] = N'UX_PackCarton_CartonNo'
    )
    BEGIN
        -- 箱号就是箱标签上的条码内容，必须全局唯一。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_PackCarton_CartonNo] ON [dbo].[PackCarton] ([CartonNo]);
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackCarton')
          AND [name] = N'IX_PackCarton_Pallet'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_PackCarton_Pallet] ON [dbo].[PackCarton] ([PalletGUID]) INCLUDE ([Status], [CartonNo]);
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackCarton')
          AND [name] = N'IX_PackCarton_Owner_Status'
    )
    BEGIN
        -- 分店按所属地点看箱列表。
        CREATE NONCLUSTERED INDEX [IX_PackCarton_Owner_Status] ON [dbo].[PackCarton] ([OwnerCode], [Status], [UpdatedAtUtc] DESC);
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackCarton')
          AND [name] = N'IX_PackCarton_Destination'
    )
    BEGIN
        -- 待签收：发往本店且在路上的箱。
        CREATE NONCLUSTERED INDEX [IX_PackCarton_Destination] ON [dbo].[PackCarton] ([DestinationCode], [Status]);
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackCarton')
          AND [name] = N'IX_PackCarton_Origin'
    )
    BEGIN
        -- 发出方看「已发出 / 对方已签收」。
        CREATE NONCLUSTERED INDEX [IX_PackCarton_Origin] ON [dbo].[PackCarton] ([OriginCode], [Status]);
    END;
    IF OBJECT_ID(N'dbo.PackCartonItem', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PackCartonItem]
        (
            [CartonGUID] nvarchar(32) NOT NULL,
            [ProductCode] nvarchar(50) NOT NULL,
            [Quantity] int NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            [UpdatedByName] nvarchar(100) NOT NULL,
            CONSTRAINT [PK_PackCartonItem] PRIMARY KEY CLUSTERED ([CartonGUID], [ProductCode])
        );
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackCartonItem')
          AND [name] = N'IX_PackCartonItem_Product'
    )
    BEGIN
        -- 「商品位置查询」按商品找箱。
        CREATE NONCLUSTERED INDEX [IX_PackCartonItem_Product] ON [dbo].[PackCartonItem] ([ProductCode]) INCLUDE ([Quantity]);
    END;
    IF OBJECT_ID(N'dbo.PackPallet', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PackPallet]
        (
            [PalletGUID] nvarchar(32) NOT NULL,
            [PalletNo] nvarchar(20) NOT NULL,
            [Status] int NOT NULL,
            [OwnerCode] nvarchar(50) NOT NULL,
            [OriginCode] nvarchar(50) NOT NULL,
            [OriginName] nvarchar(100) NOT NULL,
            [DestinationType] int NOT NULL,
            [DestinationCode] nvarchar(50) NULL,
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
            [DispatchedAtUtc] datetime2 NULL,
            [DispatchedByName] nvarchar(100) NULL,
            [ReceivedAtUtc] datetime2 NULL,
            [ReceivedByName] nvarchar(100) NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_PackPallet] PRIMARY KEY CLUSTERED ([PalletGUID])
        );
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackPallet')
          AND [name] = N'UX_PackPallet_PalletNo'
    )
    BEGIN
        CREATE UNIQUE NONCLUSTERED INDEX [UX_PackPallet_PalletNo] ON [dbo].[PackPallet] ([PalletNo]);
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackPallet')
          AND [name] = N'IX_PackPallet_Owner_Status'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_PackPallet_Owner_Status] ON [dbo].[PackPallet] ([OwnerCode], [Status], [UpdatedAtUtc] DESC);
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackPallet')
          AND [name] = N'IX_PackPallet_Destination'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_PackPallet_Destination] ON [dbo].[PackPallet] ([DestinationCode], [Status]);
    END;
    IF OBJECT_ID(N'dbo.PackCarrier', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PackCarrier]
        (
            [CarrierGUID] nvarchar(32) NOT NULL,
            [Name] nvarchar(100) NOT NULL,
            [Remark] nvarchar(200) NULL,
            [IsActive] bit NOT NULL CONSTRAINT [DF_PackCarrier_IsActive] DEFAULT (1),
            [CreatedAtUtc] datetime2 NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_PackCarrier] PRIMARY KEY CLUSTERED ([CarrierGUID])
        );
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackCarrier')
          AND [name] = N'UX_PackCarrier_Name'
    )
    BEGIN
        CREATE UNIQUE NONCLUSTERED INDEX [UX_PackCarrier_Name] ON [dbo].[PackCarrier] ([Name]);
    END;
    IF OBJECT_ID(N'dbo.PackLocation', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PackLocation]
        (
            [LocationGUID] nvarchar(32) NOT NULL,
            [OwnerCode] nvarchar(50) NOT NULL,
            [Name] nvarchar(50) NOT NULL,
            [Barcode] nvarchar(20) NOT NULL,
            [IsActive] bit NOT NULL CONSTRAINT [DF_PackLocation_IsActive] DEFAULT (1),
            [CreatedAtUtc] datetime2 NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_PackLocation] PRIMARY KEY CLUSTERED ([LocationGUID])
        );
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackLocation')
          AND [name] = N'UX_PackLocation_Owner_Name'
    )
    BEGIN
        -- 同一分店内位置名称唯一。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_PackLocation_Owner_Name] ON [dbo].[PackLocation] ([OwnerCode], [Name]);
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackLocation')
          AND [name] = N'UX_PackLocation_Barcode'
    )
    BEGIN
        -- 位置标签上的条码，全局唯一。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_PackLocation_Barcode] ON [dbo].[PackLocation] ([Barcode]);
    END;
    IF OBJECT_ID(N'dbo.PackEvent', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PackEvent]
        (
            [EventGUID] nvarchar(32) NOT NULL,
            [TargetType] int NOT NULL,
            [TargetGUID] nvarchar(32) NOT NULL,
            [Action] int NOT NULL,
            [Detail] nvarchar(400) NULL,
            [OperatorUserGuid] nvarchar(50) NOT NULL,
            [OperatorName] nvarchar(100) NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_PackEvent] PRIMARY KEY CLUSTERED ([EventGUID])
        );
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackEvent')
          AND [name] = N'IX_PackEvent_Target'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_PackEvent_Target] ON [dbo].[PackEvent] ([TargetType], [TargetGUID], [CreatedAtUtc]);
    END;
    IF OBJECT_ID(N'dbo.PackSequence', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PackSequence]
        (
            [SequenceKey] nvarchar(20) NOT NULL,
            [LastValue] int NOT NULL,
            CONSTRAINT [PK_PackSequence] PRIMARY KEY CLUSTERED ([SequenceKey])
        );
    END;

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库，否则角色管理里无法单独授予（保存会被静默丢弃）。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), v.[Code], v.[Name], v.[Category], v.[Description],
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        FROM (VALUES
            (N'StorePacking.Use', N'分店扫码打包', N'分店管理',
             N'移动端与 Web「扫码打包」- 分店扫码装箱、封箱打印箱标签、组托盘、本店存放、店间调货发出与扫码签收、查询商品位置；只能操作自己所属的分店'),
            (N'Warehouse.Packing', N'仓库扫码打包', N'仓库管理',
             N'移动端与 Web「扫码打包」- 总仓扫码装箱、封箱打印箱标签、组托盘、发往分店并查询商品位置；持有管理仓库或管理仓库订货权限时自动具备')
        ) AS v([Code], [Name], [Category], [Description])
        WHERE NOT EXISTS (
            SELECT 1 FROM [dbo].[HbwebSysPermissions] AS [existing] WHERE [existing].[Code] = v.[Code]
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
IF OBJECT_ID(N'dbo.PackCarton', N'U') IS NULL
 OR OBJECT_ID(N'dbo.PackCartonItem', N'U') IS NULL
 OR OBJECT_ID(N'dbo.PackPallet', N'U') IS NULL
 OR OBJECT_ID(N'dbo.PackCarrier', N'U') IS NULL
 OR OBJECT_ID(N'dbo.PackLocation', N'U') IS NULL
 OR OBJECT_ID(N'dbo.PackEvent', N'U') IS NULL
 OR OBJECT_ID(N'dbo.PackSequence', N'U') IS NULL
    THROW 54480, N'Scan packing tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'PackCarton', N'CartonGUID', N'nvarchar', 64, 0),
      (N'PackCarton', N'CartonNo', N'nvarchar', 40, 0),
      (N'PackCarton', N'Status', N'int', 0, 0),
      (N'PackCarton', N'OwnerCode', N'nvarchar', 100, 0),
      (N'PackCarton', N'OriginCode', N'nvarchar', 100, 0),
      (N'PackCarton', N'OriginName', N'nvarchar', 200, 0),
      (N'PackCarton', N'DestinationType', N'int', 0, 0),
      (N'PackCarton', N'DestinationCode', N'nvarchar', 100, 1),
      (N'PackCarton', N'CarrierGUID', N'nvarchar', 64, 1),
      (N'PackCarton', N'OrderGUID', N'nvarchar', 100, 1),
      (N'PackCarton', N'PalletGUID', N'nvarchar', 64, 1),
      (N'PackCarton', N'LocationCode', N'nvarchar', 100, 1),
      (N'PackCarton', N'WeightKg', N'decimal', 0, 1),
      (N'PackCarton', N'Remark', N'nvarchar', 400, 1),
      (N'PackCarton', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'PackCarton', N'CreatedByUserGuid', N'nvarchar', 100, 0),
      (N'PackCarton', N'CreatedByName', N'nvarchar', 200, 0),
      (N'PackCarton', N'SealedAtUtc', N'datetime2', 0, 1),
      (N'PackCarton', N'SealedByUserGuid', N'nvarchar', 100, 1),
      (N'PackCarton', N'SealedByName', N'nvarchar', 200, 1),
      (N'PackCarton', N'DispatchedAtUtc', N'datetime2', 0, 1),
      (N'PackCarton', N'DispatchedByName', N'nvarchar', 200, 1),
      (N'PackCarton', N'ReceivedAtUtc', N'datetime2', 0, 1),
      (N'PackCarton', N'ReceivedByName', N'nvarchar', 200, 1),
      (N'PackCarton', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'PackCartonItem', N'CartonGUID', N'nvarchar', 64, 0),
      (N'PackCartonItem', N'ProductCode', N'nvarchar', 100, 0),
      (N'PackCartonItem', N'Quantity', N'int', 0, 0),
      (N'PackCartonItem', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'PackCartonItem', N'UpdatedByName', N'nvarchar', 200, 0),
      (N'PackPallet', N'PalletGUID', N'nvarchar', 64, 0),
      (N'PackPallet', N'PalletNo', N'nvarchar', 40, 0),
      (N'PackPallet', N'Status', N'int', 0, 0),
      (N'PackPallet', N'OwnerCode', N'nvarchar', 100, 0),
      (N'PackPallet', N'OriginCode', N'nvarchar', 100, 0),
      (N'PackPallet', N'OriginName', N'nvarchar', 200, 0),
      (N'PackPallet', N'DestinationType', N'int', 0, 0),
      (N'PackPallet', N'DestinationCode', N'nvarchar', 100, 1),
      (N'PackPallet', N'CarrierGUID', N'nvarchar', 64, 1),
      (N'PackPallet', N'ConsignmentNo', N'nvarchar', 200, 1),
      (N'PackPallet', N'LocationCode', N'nvarchar', 100, 1),
      (N'PackPallet', N'WeightKg', N'decimal', 0, 1),
      (N'PackPallet', N'Remark', N'nvarchar', 400, 1),
      (N'PackPallet', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'PackPallet', N'CreatedByUserGuid', N'nvarchar', 100, 0),
      (N'PackPallet', N'CreatedByName', N'nvarchar', 200, 0),
      (N'PackPallet', N'SealedAtUtc', N'datetime2', 0, 1),
      (N'PackPallet', N'SealedByUserGuid', N'nvarchar', 100, 1),
      (N'PackPallet', N'SealedByName', N'nvarchar', 200, 1),
      (N'PackPallet', N'DispatchedAtUtc', N'datetime2', 0, 1),
      (N'PackPallet', N'DispatchedByName', N'nvarchar', 200, 1),
      (N'PackPallet', N'ReceivedAtUtc', N'datetime2', 0, 1),
      (N'PackPallet', N'ReceivedByName', N'nvarchar', 200, 1),
      (N'PackPallet', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'PackCarrier', N'CarrierGUID', N'nvarchar', 64, 0),
      (N'PackCarrier', N'Name', N'nvarchar', 200, 0),
      (N'PackCarrier', N'Remark', N'nvarchar', 400, 1),
      (N'PackCarrier', N'IsActive', N'bit', 0, 0),
      (N'PackCarrier', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'PackCarrier', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'PackLocation', N'LocationGUID', N'nvarchar', 64, 0),
      (N'PackLocation', N'OwnerCode', N'nvarchar', 100, 0),
      (N'PackLocation', N'Name', N'nvarchar', 100, 0),
      (N'PackLocation', N'Barcode', N'nvarchar', 40, 0),
      (N'PackLocation', N'IsActive', N'bit', 0, 0),
      (N'PackLocation', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'PackLocation', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'PackEvent', N'EventGUID', N'nvarchar', 64, 0),
      (N'PackEvent', N'TargetType', N'int', 0, 0),
      (N'PackEvent', N'TargetGUID', N'nvarchar', 64, 0),
      (N'PackEvent', N'Action', N'int', 0, 0),
      (N'PackEvent', N'Detail', N'nvarchar', 800, 1),
      (N'PackEvent', N'OperatorUserGuid', N'nvarchar', 100, 0),
      (N'PackEvent', N'OperatorName', N'nvarchar', 200, 0),
      (N'PackEvent', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'PackSequence', N'SequenceKey', N'nvarchar', 40, 0),
      (N'PackSequence', N'LastValue', N'int', 0, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54481, N'Scan packing column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PackCarton') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PackCartonItem') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PackPallet') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PackCarrier') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PackLocation') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PackEvent') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PackSequence') AND [type] = N'PK')
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.PackCarton')
      AND [name] = N'UX_PackCarton_CartonNo' AND [is_unique] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.PackPallet')
      AND [name] = N'UX_PackPallet_PalletNo' AND [is_unique] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.PackCarrier')
      AND [name] = N'UX_PackCarrier_Name' AND [is_unique] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.PackLocation')
      AND [name] = N'UX_PackLocation_Owner_Name' AND [is_unique] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.PackLocation')
      AND [name] = N'UX_PackLocation_Barcode' AND [is_unique] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.PackCartonItem')
      AND [name] = N'IX_PackCartonItem_Product'
 )
    THROW 54482, N'Scan packing index signature is incompatible.', 1;
""";
}
