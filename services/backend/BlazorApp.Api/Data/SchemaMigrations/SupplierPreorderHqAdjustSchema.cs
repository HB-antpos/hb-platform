namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 供应商节日预订单「总部调整」标记（迁移 20261111.001-supplier-preorder-hq-adjust）：
/// SupplierPreorderStoreOrderLine 加 HqOriginalCartons / HqAdjustedAtUtc / HqAdjustedBy 三个可空列。
/// 总部在订货汇总页改分店数量时，用它们留住「分店原来填的是多少、谁在什么时候改的」；分店自己再改这一行就清空。
/// 只加可空列，历史行全是 NULL = 没被总部改过；旧版主 API 不读这些列，回滚镜像不受影响。
/// 回退 = 不需要动库（多几个可空列无害）；真要删列需 ALTER TABLE ... DROP COLUMN 并移除迁移账本行。
/// 错误号接在供应商预订单那一段（54000–54009）之后：54010 订货行表缺失，54011 列签名不兼容。
/// 生产须显式执行 --schema=migrate，且要先于新版主 API 上线（新版主 API 查询订货行时会选这些列）。
/// </summary>
internal static class SupplierPreorderHqAdjustSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.SupplierPreorderStoreOrderLine', N'U') IS NULL
    THROW 54010, N'Supplier preorder store order line table is missing; apply 20261023.001 first.', 1;

IF COL_LENGTH(N'dbo.SupplierPreorderStoreOrderLine', N'HqOriginalCartons') IS NULL
    ALTER TABLE [dbo].[SupplierPreorderStoreOrderLine] ADD [HqOriginalCartons] int NULL;
IF COL_LENGTH(N'dbo.SupplierPreorderStoreOrderLine', N'HqAdjustedAtUtc') IS NULL
    ALTER TABLE [dbo].[SupplierPreorderStoreOrderLine] ADD [HqAdjustedAtUtc] datetime2 NULL;
IF COL_LENGTH(N'dbo.SupplierPreorderStoreOrderLine', N'HqAdjustedBy') IS NULL
    ALTER TABLE [dbo].[SupplierPreorderStoreOrderLine] ADD [HqAdjustedBy] nvarchar(100) NULL;
""";

    // 只读门禁：三列都必须存在且可空；nvarchar(100) 的 max_length 按字节计为 200。
    internal const string VerifySql = """
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.SupplierPreorderStoreOrderLine', N'U') IS NULL
    THROW 54010, N'Supplier preorder store order line table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'HqOriginalCartons', N'int', 0, 1),
      (N'HqAdjustedAtUtc', N'datetime2', 0, 1),
      (N'HqAdjustedBy', N'nvarchar', 200, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.SupplierPreorderStoreOrderLine') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54011, N'Supplier preorder hq-adjust columns are missing or incompatible.', 1;
""";
}
