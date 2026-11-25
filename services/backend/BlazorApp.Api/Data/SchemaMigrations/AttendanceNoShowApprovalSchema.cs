namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 考勤设置「整班未打卡（缺勤）生成待审」（迁移 20261125.001-attendance-no-show-approval）：
/// 给 AttendanceSettings 加两列：
/// - NoShowApprovalEnabled bit NOT NULL DEFAULT 1：总开关，关掉后不再生成新的缺勤待审（已有待审保留）；
/// - NoShowGraceMinutes int NOT NULL DEFAULT 60：排班结束后多少分钟仍零打卡才算缺勤。
/// 只加列，不回填、不改其他表；缺勤待审本身复用 AttendanceApproval（SourceType = NoShow），不加表。
/// 生产须显式执行 --schema=migrate，且要先于新版主 API 上线（实体会读这两列）。错误号区间 54910–54911。
/// </summary>
internal static class AttendanceNoShowApprovalSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.AttendanceSettings', N'U') IS NULL
        THROW 54910, N'Attendance settings table is missing.', 1;

    -- COL_LENGTH 判断保证重复执行是空操作；带默认值的 NOT NULL 列在 SQL Server 上只改元数据。
    IF COL_LENGTH(N'dbo.AttendanceSettings', N'NoShowApprovalEnabled') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [NoShowApprovalEnabled] bit NOT NULL
            CONSTRAINT [DF_AttendanceSettings_NoShowApprovalEnabled] DEFAULT (1);
    IF COL_LENGTH(N'dbo.AttendanceSettings', N'NoShowGraceMinutes') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [NoShowGraceMinutes] int NOT NULL
            CONSTRAINT [DF_AttendanceSettings_NoShowGraceMinutes] DEFAULT (60);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：两列存在、类型正确且非空；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.AttendanceSettings', N'U') IS NULL
    THROW 54910, N'Attendance settings table is missing.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.AttendanceSettings')
      AND c.[name] = N'NoShowApprovalEnabled'
      AND ty.[name] = N'bit'
      AND c.[is_nullable] = 0
)
OR NOT EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.AttendanceSettings')
      AND c.[name] = N'NoShowGraceMinutes'
      AND ty.[name] = N'int'
      AND c.[is_nullable] = 0
)
    THROW 54911, N'Attendance no-show approval column signature is incompatible.', 1;
""";
}
