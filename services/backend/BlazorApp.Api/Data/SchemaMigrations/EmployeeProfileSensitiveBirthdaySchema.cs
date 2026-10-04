namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 生日改为敏感资料后，敏感变更申请表需要保存申请中的生日。申请表已随基线登记，
/// 新列走独立版本号迁移，生产须显式执行 --schema=migrate。只加可空列，不回填历史申请。
/// </summary>
internal static class EmployeeProfileSensitiveBirthdaySchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.EmployeeProfileSensitiveChangeRequest', N'U') IS NULL
        THROW 51861, N'Employee profile sensitive change request table is missing.', 1;

    IF COL_LENGTH(N'dbo.EmployeeProfileSensitiveChangeRequest', N'Birthday') IS NULL
    BEGIN
        -- 申请中的生日；只有变更字段含 birthday 时审批才写回正式资料。
        ALTER TABLE [dbo].[EmployeeProfileSensitiveChangeRequest] ADD [Birthday] date NULL;
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
IF OBJECT_ID(N'dbo.EmployeeProfileSensitiveChangeRequest', N'U') IS NULL
    THROW 51861, N'Employee profile sensitive change request table is missing.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.EmployeeProfileSensitiveChangeRequest')
      AND c.[name] = N'Birthday'
      AND ty.[name] = N'date'
      AND c.[is_nullable] = 1
)
    THROW 51862, N'Employee profile sensitive change request birthday column signature is incompatible.', 1;
""";
}
