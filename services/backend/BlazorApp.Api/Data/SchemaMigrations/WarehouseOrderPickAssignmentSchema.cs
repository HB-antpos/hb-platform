namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 仓库订单拣货分配表。拣货既有迁移已登记，新表走独立版本号迁移，生产须显式执行 --schema=migrate；
/// 实体不进 SqlSugarContext 的自动建表清单。
/// </summary>
internal static class WarehouseOrderPickAssignmentSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.WarehouseOrderPickAssignment', N'U') IS NULL
    BEGIN
        -- 每张订单每行一条负责人（可空＝待扫分单领取）；重新分配时整单替换。
        CREATE TABLE [dbo].[WarehouseOrderPickAssignment]
        (
            [OrderGUID] nvarchar(50) NOT NULL,
            [DetailGUID] nvarchar(50) NOT NULL,
            [PickerUserGuid] nvarchar(50) NULL,
            [PickerName] nvarchar(100) NULL,
            [SegmentNo] int NOT NULL,
            [AssignmentVersion] int NOT NULL,
            [AssignedByUserGuid] nvarchar(50) NOT NULL,
            [AssignedByName] nvarchar(100) NOT NULL,
            [AssignedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_WarehouseOrderPickAssignment] PRIMARY KEY CLUSTERED ([OrderGUID], [DetailGUID])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.WarehouseOrderPickAssignment')
          AND [name] = N'IX_WarehouseOrderPickAssignment_Picker'
    )
    BEGIN
        -- “派给我的”订单列表按员工查。
        CREATE NONCLUSTERED INDEX [IX_WarehouseOrderPickAssignment_Picker]
            ON [dbo].[WarehouseOrderPickAssignment] ([PickerUserGuid], [OrderGUID]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、可空性、主键与员工索引；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.WarehouseOrderPickAssignment', N'U') IS NULL
    THROW 51848, N'Warehouse order pick assignment table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'OrderGUID', N'nvarchar', 100, 0),
      (N'DetailGUID', N'nvarchar', 100, 0),
      (N'PickerUserGuid', N'nvarchar', 100, 1),
      (N'PickerName', N'nvarchar', 200, 1),
      (N'SegmentNo', N'int', 0, 0),
      (N'AssignmentVersion', N'int', 0, 0),
      (N'AssignedByUserGuid', N'nvarchar', 100, 0),
      (N'AssignedByName', N'nvarchar', 200, 0),
      (N'AssignedAtUtc', N'datetime2', 0, 0)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.WarehouseOrderPickAssignment') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 51849, N'Warehouse order pick assignment column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.WarehouseOrderPickAssignment') AND [type] = N'PK')
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.WarehouseOrderPickAssignment')
      AND [name] = N'IX_WarehouseOrderPickAssignment_Picker'
 )
    THROW 51850, N'Warehouse order pick assignment index signature is incompatible.', 1;
""";
}
