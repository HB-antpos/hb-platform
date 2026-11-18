namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 员工入职日期（HireDate）。零售 Award 里 18–20 岁青少年「在本雇主处工作满 6 个月」才转成人费率，
/// 而员工资料里只有生日、没有入职日期，系统无法判断。账号创建日 / 首次排班日不能代替（系统是 2026-10 才上线的，
/// 员工实际入职要早得多），所以单独存一个由人确认的日期。
/// 表已随基线登记，新列走独立版本号迁移，生产须显式执行 --schema=migrate。只加可空列，不回填历史数据。
/// </summary>
internal static class EmployeeProfileHireDateSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.EmployeeProfile', N'U') IS NULL
        THROW 54600, N'Employee profile table is missing.', 1;

    IF COL_LENGTH(N'dbo.EmployeeProfile', N'HireDate') IS NULL
    BEGIN
        -- 入职日期（只有日期，没有时间）；空 = 还没确认，不是「刚入职」。
        ALTER TABLE [dbo].[EmployeeProfile] ADD [HireDate] date NULL;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列存在、类型 date 且可空；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.EmployeeProfile', N'U') IS NULL
    THROW 54600, N'Employee profile table is missing.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.EmployeeProfile')
      AND c.[name] = N'HireDate'
      AND ty.[name] = N'date'
      AND c.[is_nullable] = 1
)
    THROW 54601, N'Employee profile hire date column signature is incompatible.', 1;
""";
}
