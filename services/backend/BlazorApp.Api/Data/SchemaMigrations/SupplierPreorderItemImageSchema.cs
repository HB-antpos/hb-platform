namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 供应商节日预订单新品图片转存 COS（迁移 20261024.001-supplier-preorder-item-image）：给 SupplierPreorderFormItem 加 5 列
/// （ImageCosUrl / ImageCopyStatus / ImageCopyAttempts / ImageCopyError / ImageCopiedAtUtc），都可空或带默认值，不改现有数据。
/// 实体在 SupplierPreorderModels.cs；生产须显式执行 --schema=migrate，且要在部署新代码之前（新代码会读这些列）。错误号 54006–54007。
/// </summary>
internal static class SupplierPreorderItemImageSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.SupplierPreorderFormItem', N'U') IS NULL
        THROW 54006, N'SupplierPreorderFormItem is missing; run 20261023.001-supplier-preorder first.', 1;

    IF COL_LENGTH(N'dbo.SupplierPreorderFormItem', N'ImageCosUrl') IS NULL
        ALTER TABLE [dbo].[SupplierPreorderFormItem] ADD [ImageCosUrl] nvarchar(500) NULL;
    IF COL_LENGTH(N'dbo.SupplierPreorderFormItem', N'ImageCopyStatus') IS NULL
        ALTER TABLE [dbo].[SupplierPreorderFormItem] ADD [ImageCopyStatus] nvarchar(20) NULL;
    IF COL_LENGTH(N'dbo.SupplierPreorderFormItem', N'ImageCopyAttempts') IS NULL
        ALTER TABLE [dbo].[SupplierPreorderFormItem] ADD [ImageCopyAttempts] int NOT NULL
            CONSTRAINT [DF_SupplierPreorderFormItem_ImageCopyAttempts] DEFAULT (0);
    IF COL_LENGTH(N'dbo.SupplierPreorderFormItem', N'ImageCopyError') IS NULL
        ALTER TABLE [dbo].[SupplierPreorderFormItem] ADD [ImageCopyError] nvarchar(300) NULL;
    IF COL_LENGTH(N'dbo.SupplierPreorderFormItem', N'ImageCopiedAtUtc') IS NULL
        ALTER TABLE [dbo].[SupplierPreorderFormItem] ADD [ImageCopiedAtUtc] datetime2 NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：5 列的类型 / 长度 / 可空性与实体一致（nvarchar 的 max_length 按字节，长度 × 2）。
    internal const string VerifySql = """
SET NOCOUNT ON;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'ImageCosUrl', N'nvarchar', 1000, 1),
      (N'ImageCopyStatus', N'nvarchar', 40, 1),
      (N'ImageCopyAttempts', N'int', 0, 0),
      (N'ImageCopyError', N'nvarchar', 600, 1),
      (N'ImageCopiedAtUtc', N'datetime2', 0, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.SupplierPreorderFormItem') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54007, N'Supplier preorder item image columns are missing or incompatible.', 1;
""";
}
