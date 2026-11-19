namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 考勤设置「早到或晚走满 N 分钟才产生加班候选」（迁移 20261120.002-attendance-overtime-threshold）：
/// 给 AttendanceSettings 加一列 OvertimeThresholdMinutes int NOT NULL DEFAULT 15。
/// 默认 15 与改造前硬编码一致，所以迁移本身不改变任何计薪结果；线上调成 30 须由 Admin 在设置页手动改。
/// 只加一列，不回填、不改其他表；生产须显式执行 --schema=migrate，且要先于新版主 API 上线（实体会读这一列）。
/// 错误号区间 54900–54902。
/// </summary>
internal static class AttendanceOvertimeThresholdSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.AttendanceSettings', N'U') IS NULL
        THROW 54900, N'Attendance settings table is missing.', 1;

    IF COL_LENGTH(N'dbo.AttendanceSettings', N'OvertimeThresholdMinutes') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [OvertimeThresholdMinutes] int NOT NULL
            CONSTRAINT [DF_AttendanceSettings_OvertimeThresholdMinutes] DEFAULT (15);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：列存在、类型 int 且非空；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.AttendanceSettings', N'U') IS NULL
    THROW 54900, N'Attendance settings table is missing.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.AttendanceSettings')
      AND c.[name] = N'OvertimeThresholdMinutes'
      AND ty.[name] = N'int'
      AND c.[is_nullable] = 0
)
    THROW 54901, N'Attendance overtime threshold column signature is incompatible.', 1;
""";
}
