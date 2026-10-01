namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 仓库订单拣货“货位没货”标记表。拣货三张表的迁移已登记，新表必须走独立版本号迁移，
/// 生产须显式执行 --schema=migrate；实体不进 SqlSugarContext 的自动建表清单。
/// </summary>
internal static class WarehouseOrderPickStockoutSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.WarehouseOrderPickStockout', N'U') IS NULL
    BEGIN
        -- 每行最多一条：撤销或又拣到货时写 ClearedAtUtc，再次标记时原行覆盖。
        CREATE TABLE [dbo].[WarehouseOrderPickStockout]
        (
            [OrderGUID] nvarchar(50) NOT NULL,
            [DetailGUID] nvarchar(50) NOT NULL,
            [ProductCode] nvarchar(50) NOT NULL,
            [LocationCode] nvarchar(200) NULL,
            [Reason] int NOT NULL,
            [PickedAtMark] int NOT NULL,
            [MarkedByUserGuid] nvarchar(50) NOT NULL,
            [MarkedByName] nvarchar(100) NOT NULL,
            [MarkedAtUtc] datetime2 NOT NULL,
            [ClearedAtUtc] datetime2 NULL,
            [ClearedByName] nvarchar(100) NULL,
            CONSTRAINT [PK_WarehouseOrderPickStockout] PRIMARY KEY CLUSTERED ([OrderGUID], [DetailGUID])
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、可空性与主键；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.WarehouseOrderPickStockout', N'U') IS NULL
    THROW 51845, N'Warehouse order pick stockout table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'OrderGUID', N'nvarchar', 100, 0),
      (N'DetailGUID', N'nvarchar', 100, 0),
      (N'ProductCode', N'nvarchar', 100, 0),
      (N'LocationCode', N'nvarchar', 400, 1),
      (N'Reason', N'int', 0, 0),
      (N'PickedAtMark', N'int', 0, 0),
      (N'MarkedByUserGuid', N'nvarchar', 100, 0),
      (N'MarkedByName', N'nvarchar', 200, 0),
      (N'MarkedAtUtc', N'datetime2', 0, 0),
      (N'ClearedAtUtc', N'datetime2', 0, 1),
      (N'ClearedByName', N'nvarchar', 200, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.WarehouseOrderPickStockout') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 51846, N'Warehouse order pick stockout column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.WarehouseOrderPickStockout') AND [type] = N'PK')
    THROW 51847, N'Warehouse order pick stockout primary key is missing.', 1;
""";
}
