namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 考勤可上班时间的「不能上班」类型列。考勤表已随基线登记，新列走独立版本号迁移，生产须显式执行 --schema=migrate。
/// </summary>
internal static class AttendanceAvailabilityUnavailableSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.AttendanceAvailability', N'U') IS NULL
        THROW 51853, N'Attendance availability table is missing.', 1;

    IF COL_LENGTH(N'dbo.AttendanceAvailability', N'IsUnavailable') IS NULL
    BEGIN
        -- 时间段类型：0＝可上班（历史数据按默认值回填），1＝不能上班。
        ALTER TABLE [dbo].[AttendanceAvailability]
            ADD [IsUnavailable] bit NOT NULL
                CONSTRAINT [DF_AttendanceAvailability_IsUnavailable] DEFAULT (0);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列存在、类型 bit 且不可空；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.AttendanceAvailability', N'U') IS NULL
    THROW 51853, N'Attendance availability table is missing.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.AttendanceAvailability')
      AND c.[name] = N'IsUnavailable'
      AND ty.[name] = N'bit'
      AND c.[is_nullable] = 0
)
    THROW 51854, N'Attendance availability unavailable column signature is incompatible.', 1;
""";
}
