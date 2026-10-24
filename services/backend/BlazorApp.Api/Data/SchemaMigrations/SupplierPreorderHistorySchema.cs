namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 供应商节日预订单去年历史快照（迁移 20261023.002-supplier-preorder-history）：只新建 SupplierPreorderHistorySnapshot 一张表，
/// 不修改任何现有表、不改任何现有数据。实体在 SupplierPreorderModels.cs，不进 SqlSugarContext 的自动建表清单；
/// 生产须显式执行 --schema=migrate。迁移前历史接口会因表不存在报错（下单页本身不受影响，只是没有去年数据）。错误号 54003–54005。
/// </summary>
internal static class SupplierPreorderHistorySchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.SupplierPreorderHistorySnapshot', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[SupplierPreorderHistorySnapshot]
        (
            [SnapshotGuid] nvarchar(50) NOT NULL,
            [FormGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [ProductCode] nvarchar(100) NOT NULL,
            [PayloadJson] nvarchar(max) NOT NULL,
            [ComputedAtUtc] datetime2 NOT NULL,
            [RuleVersion] nvarchar(50) NOT NULL,
            CONSTRAINT [PK_SupplierPreorderHistorySnapshot] PRIMARY KEY CLUSTERED ([SnapshotGuid])
        );
        -- 一张订单表内（分店, 商品）唯一；按订单表整批替换、按商品单独刷新都走这个索引
        CREATE UNIQUE INDEX [UX_SupplierPreorderHistorySnapshot_FormStoreProduct]
            ON [dbo].[SupplierPreorderHistorySnapshot] ([FormGuid], [StoreCode], [ProductCode]);
        CREATE INDEX [IX_SupplierPreorderHistorySnapshot_FormProduct]
            ON [dbo].[SupplierPreorderHistorySnapshot] ([FormGuid], [ProductCode]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    internal const string VerifySql = """
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.SupplierPreorderHistorySnapshot', N'U') IS NULL
    THROW 54003, N'Supplier preorder history snapshot table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'SnapshotGuid', N'nvarchar', 100, 0),
      (N'FormGuid', N'nvarchar', 100, 0),
      (N'StoreCode', N'nvarchar', 100, 0),
      (N'ProductCode', N'nvarchar', 200, 0),
      (N'PayloadJson', N'nvarchar', -1, 0),
      (N'ComputedAtUtc', N'datetime2', 0, 0),
      (N'RuleVersion', N'nvarchar', 100, 0)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.SupplierPreorderHistorySnapshot') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54004, N'Supplier preorder history snapshot column signature is incompatible.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.SupplierPreorderHistorySnapshot')
      AND i.[name] = N'UX_SupplierPreorderHistorySnapshot_FormStoreProduct'
      AND i.[is_unique] = 1
)
    THROW 54005, N'Supplier preorder history snapshot unique index is missing.', 1;
""";
}
