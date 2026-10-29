namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// MYOB 对接：公共假期单独映射（迁移 20261030.001-myob-pay-category-public-holiday）：
/// 公假上班与带薪公假原先并入「平日」类别推送；现在全职 / 兼职与临时工（Casual）各有一个公共假期类别，
/// 所以给 dbo.MyobPayCategoryMapping 加两个可空列 PublicHolidayUid / PublicHolidayCasualUid（只存 UID，不存名称）。
/// 只做「列不存在才加」的加列：不改既有列、不删数据、不写权限表；两列可空，旧版主 API 不认识它们也能照常读写（被忽略）。
/// 回退只需删掉这两列并移除迁移账本行（旧代码不依赖它们）。
/// 错误号沿用工资类别映射的 54350–54359 段：54355 表缺失、54356 新增两列签名不符。
/// 生产须显式执行 --schema=migrate，且要先于新版主 API 上线。
/// </summary>
internal static class MyobPayCategoryPublicHolidaySchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF COL_LENGTH(N'dbo.MyobPayCategoryMapping', N'PublicHolidayUid') IS NULL
    BEGIN
        ALTER TABLE [dbo].[MyobPayCategoryMapping] ADD [PublicHolidayUid] nvarchar(50) NULL;
    END;

    IF COL_LENGTH(N'dbo.MyobPayCategoryMapping', N'PublicHolidayCasualUid') IS NULL
    BEGIN
        ALTER TABLE [dbo].[MyobPayCategoryMapping] ADD [PublicHolidayCasualUid] nvarchar(50) NULL;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：新增两列的签名（类型 / 长度 / 可空性）必须精确匹配；不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length：nvarchar 为字节数（字符数 * 2）。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.MyobPayCategoryMapping', N'U') IS NULL
    THROW 54355, N'MYOB pay category mapping table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.MyobPayCategoryMapping', N'PublicHolidayUid', N'nvarchar', 100, 1),
      (N'dbo.MyobPayCategoryMapping', N'PublicHolidayCasualUid', N'nvarchar', 100, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54356, N'MYOB pay category public holiday column signature is incompatible.', 1;
""";
}
