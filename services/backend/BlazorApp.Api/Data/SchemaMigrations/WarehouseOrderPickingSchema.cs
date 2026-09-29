namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 仓库订单拣货：会话、拣货记录（只追加）、参与人。
/// 走独立版本号迁移，生产须显式执行 --schema=migrate；实体不进 SqlSugarContext 的自动建表清单。
/// </summary>
internal static class WarehouseOrderPickingSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.WarehouseOrderPickSession', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[WarehouseOrderPickSession]
        (
            [OrderGUID] nvarchar(50) NOT NULL,
            [Status] int NOT NULL,
            [StartedAtUtc] datetime2 NOT NULL,
            [StartedByUserGuid] nvarchar(50) NULL,
            [StartedByName] nvarchar(100) NOT NULL,
            [SubmittedAtUtc] datetime2 NULL,
            [SubmittedByUserGuid] nvarchar(50) NULL,
            [SubmittedByName] nvarchar(100) NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_WarehouseOrderPickSession] PRIMARY KEY CLUSTERED ([OrderGUID])
        );
    END;

    IF OBJECT_ID(N'dbo.WarehouseOrderPickRecord', N'U') IS NULL
    BEGIN
        -- 只追加：行合计 = SUM(QuantityDelta)；ClientRequestId 唯一保证客户端重试幂等。
        CREATE TABLE [dbo].[WarehouseOrderPickRecord]
        (
            [RecordGUID] nvarchar(32) NOT NULL,
            [OrderGUID] nvarchar(50) NOT NULL,
            [DetailGUID] nvarchar(50) NOT NULL,
            [ProductCode] nvarchar(50) NOT NULL,
            [QuantityDelta] int NOT NULL,
            [Source] int NOT NULL,
            [ScannedCode] nvarchar(100) NULL,
            [MatchedBy] int NULL,
            [MinOrderQuantityAtPick] int NULL,
            [PickerUserGuid] nvarchar(50) NOT NULL,
            [PickerName] nvarchar(100) NOT NULL,
            [AuthUserGuid] nvarchar(50) NULL,
            [DeviceCode] nvarchar(100) NULL,
            [ClientRequestId] uniqueidentifier NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_WarehouseOrderPickRecord] PRIMARY KEY CLUSTERED ([RecordGUID])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.WarehouseOrderPickRecord')
          AND [name] = N'UX_WarehouseOrderPickRecord_ClientRequestId'
    )
    BEGIN
        CREATE UNIQUE NONCLUSTERED INDEX [UX_WarehouseOrderPickRecord_ClientRequestId]
            ON [dbo].[WarehouseOrderPickRecord] ([ClientRequestId]);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.WarehouseOrderPickRecord')
          AND [name] = N'IX_WarehouseOrderPickRecord_Order_Detail'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_WarehouseOrderPickRecord_Order_Detail]
            ON [dbo].[WarehouseOrderPickRecord] ([OrderGUID], [DetailGUID])
            INCLUDE ([QuantityDelta], [PickerUserGuid], [PickerName]);
    END;

    IF OBJECT_ID(N'dbo.WarehouseOrderPickParticipant', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[WarehouseOrderPickParticipant]
        (
            [OrderGUID] nvarchar(50) NOT NULL,
            [PickerUserGuid] nvarchar(50) NOT NULL,
            [PickerName] nvarchar(100) NOT NULL,
            [JoinedAtUtc] datetime2 NOT NULL,
            [LastActiveAtUtc] datetime2 NOT NULL,
            [LastDetailGUID] nvarchar(50) NULL,
            CONSTRAINT [PK_WarehouseOrderPickParticipant] PRIMARY KEY CLUSTERED ([OrderGUID], [PickerUserGuid])
        );
    END;

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库，否则角色管理里无法单独授予（保存会被静默丢弃）。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), N'Warehouse.Picking', N'仓库订单拣货', N'仓库管理',
               N'移动端「订单拣货」- 扫码拣货、补录缺失的中包数并提交配货数；持有管理仓库或管理仓库订货权限时自动具备',
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        WHERE NOT EXISTS (
            SELECT 1 FROM [dbo].[HbwebSysPermissions] AS [existing] WHERE [existing].[Code] = N'Warehouse.Picking'
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、可空性、主键与幂等唯一索引；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.WarehouseOrderPickSession', N'U') IS NULL
 OR OBJECT_ID(N'dbo.WarehouseOrderPickRecord', N'U') IS NULL
 OR OBJECT_ID(N'dbo.WarehouseOrderPickParticipant', N'U') IS NULL
    THROW 51840, N'Warehouse order picking tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'WarehouseOrderPickSession', N'OrderGUID', N'nvarchar', 100, 0),
      (N'WarehouseOrderPickSession', N'Status', N'int', 0, 0),
      (N'WarehouseOrderPickSession', N'StartedAtUtc', N'datetime2', 0, 0),
      (N'WarehouseOrderPickSession', N'StartedByUserGuid', N'nvarchar', 100, 1),
      (N'WarehouseOrderPickSession', N'StartedByName', N'nvarchar', 200, 0),
      (N'WarehouseOrderPickSession', N'SubmittedAtUtc', N'datetime2', 0, 1),
      (N'WarehouseOrderPickSession', N'SubmittedByUserGuid', N'nvarchar', 100, 1),
      (N'WarehouseOrderPickSession', N'SubmittedByName', N'nvarchar', 200, 1),
      (N'WarehouseOrderPickSession', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'WarehouseOrderPickRecord', N'RecordGUID', N'nvarchar', 64, 0),
      (N'WarehouseOrderPickRecord', N'OrderGUID', N'nvarchar', 100, 0),
      (N'WarehouseOrderPickRecord', N'DetailGUID', N'nvarchar', 100, 0),
      (N'WarehouseOrderPickRecord', N'ProductCode', N'nvarchar', 100, 0),
      (N'WarehouseOrderPickRecord', N'QuantityDelta', N'int', 0, 0),
      (N'WarehouseOrderPickRecord', N'Source', N'int', 0, 0),
      (N'WarehouseOrderPickRecord', N'ScannedCode', N'nvarchar', 200, 1),
      (N'WarehouseOrderPickRecord', N'MatchedBy', N'int', 0, 1),
      (N'WarehouseOrderPickRecord', N'MinOrderQuantityAtPick', N'int', 0, 1),
      (N'WarehouseOrderPickRecord', N'PickerUserGuid', N'nvarchar', 100, 0),
      (N'WarehouseOrderPickRecord', N'PickerName', N'nvarchar', 200, 0),
      (N'WarehouseOrderPickRecord', N'AuthUserGuid', N'nvarchar', 100, 1),
      (N'WarehouseOrderPickRecord', N'DeviceCode', N'nvarchar', 200, 1),
      (N'WarehouseOrderPickRecord', N'ClientRequestId', N'uniqueidentifier', 0, 0),
      (N'WarehouseOrderPickRecord', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'WarehouseOrderPickParticipant', N'OrderGUID', N'nvarchar', 100, 0),
      (N'WarehouseOrderPickParticipant', N'PickerUserGuid', N'nvarchar', 100, 0),
      (N'WarehouseOrderPickParticipant', N'PickerName', N'nvarchar', 200, 0),
      (N'WarehouseOrderPickParticipant', N'JoinedAtUtc', N'datetime2', 0, 0),
      (N'WarehouseOrderPickParticipant', N'LastActiveAtUtc', N'datetime2', 0, 0),
      (N'WarehouseOrderPickParticipant', N'LastDetailGUID', N'nvarchar', 100, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 51841, N'Warehouse order picking column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.WarehouseOrderPickSession') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.WarehouseOrderPickRecord') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.WarehouseOrderPickParticipant') AND [type] = N'PK')
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.WarehouseOrderPickRecord')
      AND [name] = N'UX_WarehouseOrderPickRecord_ClientRequestId'
      AND [is_unique] = 1
 )
    THROW 51842, N'Warehouse order picking index signature is incompatible.', 1;
""";
}
