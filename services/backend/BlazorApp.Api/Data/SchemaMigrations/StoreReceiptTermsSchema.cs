namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 门店小票资料下发新增两个文本字段：VoucherTerms（退款代金券券面「VOUCHER TERMS」正文）与
/// InstallmentTerms（进行中分期小票「INSTALLMENT TERMS」正文），在 dbo.Store（总部编辑的当前值）与
/// dbo.StoreReceiptProfileRelease（下发快照）两张表各加两列 nvarchar(600) NULL。
/// 只加可空列，历史行与旧快照不受影响（NULL＝收银端按内置默认文案打印）；
/// 生产须显式执行 --schema=migrate，且要先于新版 HBweb / Hbpos.Api 上线（两边都会按实体查询这些列）。
/// 错误号区间 52300–52301。
/// </summary>
internal static class StoreReceiptTermsSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.Store', N'U') IS NULL
 OR OBJECT_ID(N'dbo.StoreReceiptProfileRelease', N'U') IS NULL
    THROW 52300, N'Store or store receipt profile release table is missing; apply 20261007.001 first.', 1;

BEGIN TRANSACTION;
BEGIN TRY
    -- 加可空列只改元数据，不重写大表；COL_LENGTH 判断保证重复执行是空操作。
    IF COL_LENGTH(N'dbo.Store', N'VoucherTerms') IS NULL
        ALTER TABLE [dbo].[Store] ADD [VoucherTerms] nvarchar(600) NULL;
    IF COL_LENGTH(N'dbo.Store', N'InstallmentTerms') IS NULL
        ALTER TABLE [dbo].[Store] ADD [InstallmentTerms] nvarchar(600) NULL;

    IF COL_LENGTH(N'dbo.StoreReceiptProfileRelease', N'VoucherTerms') IS NULL
        ALTER TABLE [dbo].[StoreReceiptProfileRelease] ADD [VoucherTerms] nvarchar(600) NULL;
    IF COL_LENGTH(N'dbo.StoreReceiptProfileRelease', N'InstallmentTerms') IS NULL
        ALTER TABLE [dbo].[StoreReceiptProfileRelease] ADD [InstallmentTerms] nvarchar(600) NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：四列都必须存在且为可空 nvarchar(600)（max_length 按字节计为 1200）。
    // 已有同名但类型/长度/可空性不符的列只能通过显式迁移修复，运行时绝不自动改写。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.Store', N'U') IS NULL
 OR OBJECT_ID(N'dbo.StoreReceiptProfileRelease', N'U') IS NULL
    THROW 52300, N'Store or store receipt profile release table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'Store', N'VoucherTerms', N'nvarchar', 1200, 1),
      (N'Store', N'InstallmentTerms', N'nvarchar', 1200, 1),
      (N'StoreReceiptProfileRelease', N'VoucherTerms', N'nvarchar', 1200, 1),
      (N'StoreReceiptProfileRelease', N'InstallmentTerms', N'nvarchar', 1200, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 52301, N'Store receipt terms column signature is incompatible.', 1;
""";
}
