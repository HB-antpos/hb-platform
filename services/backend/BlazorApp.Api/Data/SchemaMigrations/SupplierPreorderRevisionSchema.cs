namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 供应商节日预订单修订版（迁移 20261024.002-supplier-preorder-revision）：
/// SupplierPreorderForm 加 Revision / RevisedAtUtc，SupplierPreorderFormItem 加 RevisionNo / RevisionChange / PrevPrice / PrevCartonQty / RemovedAtUtc。
/// 都可空或带默认值 0，不改现有数据；须在部署新代码之前执行（新代码会读这些列）。错误号 54008–54009。
/// </summary>
internal static class SupplierPreorderRevisionSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.SupplierPreorderForm', N'U') IS NULL OR OBJECT_ID(N'dbo.SupplierPreorderFormItem', N'U') IS NULL
        THROW 54008, N'Supplier preorder tables are missing; run 20261023.001-supplier-preorder first.', 1;

    IF COL_LENGTH(N'dbo.SupplierPreorderForm', N'Revision') IS NULL
        ALTER TABLE [dbo].[SupplierPreorderForm] ADD [Revision] int NOT NULL
            CONSTRAINT [DF_SupplierPreorderForm_Revision] DEFAULT (0);
    IF COL_LENGTH(N'dbo.SupplierPreorderForm', N'RevisedAtUtc') IS NULL
        ALTER TABLE [dbo].[SupplierPreorderForm] ADD [RevisedAtUtc] datetime2 NULL;

    IF COL_LENGTH(N'dbo.SupplierPreorderFormItem', N'RevisionNo') IS NULL
        ALTER TABLE [dbo].[SupplierPreorderFormItem] ADD [RevisionNo] int NOT NULL
            CONSTRAINT [DF_SupplierPreorderFormItem_RevisionNo] DEFAULT (0);
    IF COL_LENGTH(N'dbo.SupplierPreorderFormItem', N'RevisionChange') IS NULL
        ALTER TABLE [dbo].[SupplierPreorderFormItem] ADD [RevisionChange] nvarchar(100) NULL;
    IF COL_LENGTH(N'dbo.SupplierPreorderFormItem', N'PrevPrice') IS NULL
        ALTER TABLE [dbo].[SupplierPreorderFormItem] ADD [PrevPrice] decimal(18,4) NULL;
    IF COL_LENGTH(N'dbo.SupplierPreorderFormItem', N'PrevCartonQty') IS NULL
        ALTER TABLE [dbo].[SupplierPreorderFormItem] ADD [PrevCartonQty] int NULL;
    IF COL_LENGTH(N'dbo.SupplierPreorderFormItem', N'RemovedAtUtc') IS NULL
        ALTER TABLE [dbo].[SupplierPreorderFormItem] ADD [RemovedAtUtc] datetime2 NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    internal const string VerifySql = """
SET NOCOUNT ON;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'SupplierPreorderForm', N'Revision', N'int', 0, 0),
      (N'SupplierPreorderForm', N'RevisedAtUtc', N'datetime2', 0, 1),
      (N'SupplierPreorderFormItem', N'RevisionNo', N'int', 0, 0),
      (N'SupplierPreorderFormItem', N'RevisionChange', N'nvarchar', 200, 1),
      (N'SupplierPreorderFormItem', N'PrevPrice', N'decimal', 0, 1),
      (N'SupplierPreorderFormItem', N'PrevCartonQty', N'int', 0, 1),
      (N'SupplierPreorderFormItem', N'RemovedAtUtc', N'datetime2', 0, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.' + expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54009, N'Supplier preorder revision columns are missing or incompatible.', 1;
""";
}
