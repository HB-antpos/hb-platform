namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// MYOB 对接阶段 2（迁移 20261027.004-myob-pay-category；方案 docs/plans/2026-10-27-myob-payroll-integration.md 第 4、8 节）：
/// dbo.MyobPayCategoryMapping —— 每个公司文件一行，记录我方 5 档转账工时（平日 / 周六 / 周日 / 病假 / 年假）各对应 MYOB 的哪个工资类别 UID。
/// UID 只存标识，不存名称（名称随时从 MYOB 读最新）；5 个 UID 都可空，表示尚未设置。
/// 只新建表，不触碰任何既有表，也不新增权限码（映射沿用 Myob.Connection.Manage，预览 / 推送沿用 Attendance.Payroll.Edit）；
/// 回退只需删除该表并移除迁移账本行。实体不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// 错误号自成一段 54350–54359：54350 表缺失、54351 列签名、54352 主键。
/// 生产须显式执行 --schema=migrate，且要先于新版主 API 上线。
/// </summary>
internal static class MyobPayCategorySchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.MyobPayCategoryMapping', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[MyobPayCategoryMapping]
        (
            [ConnectionGuid] nvarchar(50) NOT NULL,
            [WeekdayUid] nvarchar(50) NULL,
            [SaturdayUid] nvarchar(50) NULL,
            [SundayUid] nvarchar(50) NULL,
            [PersonalLeaveUid] nvarchar(50) NULL,
            [HolidayLeaveUid] nvarchar(50) NULL,
            [UpdatedAt] datetime2 NOT NULL,
            [UpdatedByUserGuid] nvarchar(50) NULL,
            CONSTRAINT [PK_MyobPayCategoryMapping] PRIMARY KEY CLUSTERED ([ConnectionGuid])
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：列签名（类型 / 长度 / 可空性）与主键必须精确匹配；不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length：nvarchar 为字节数（字符数 * 2）。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.MyobPayCategoryMapping', N'U') IS NULL
    THROW 54350, N'MYOB pay category mapping table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.MyobPayCategoryMapping', N'ConnectionGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobPayCategoryMapping', N'WeekdayUid', N'nvarchar', 100, 1),
      (N'dbo.MyobPayCategoryMapping', N'SaturdayUid', N'nvarchar', 100, 1),
      (N'dbo.MyobPayCategoryMapping', N'SundayUid', N'nvarchar', 100, 1),
      (N'dbo.MyobPayCategoryMapping', N'PersonalLeaveUid', N'nvarchar', 100, 1),
      (N'dbo.MyobPayCategoryMapping', N'HolidayLeaveUid', N'nvarchar', 100, 1),
      (N'dbo.MyobPayCategoryMapping', N'UpdatedAt', N'datetime2', 8, 0),
      (N'dbo.MyobPayCategoryMapping', N'UpdatedByUserGuid', N'nvarchar', 100, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54351, N'MYOB pay category mapping column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.MyobPayCategoryMapping') AND [type] = N'PK')
    THROW 54352, N'MYOB pay category mapping primary key is missing.', 1;
""";
}
