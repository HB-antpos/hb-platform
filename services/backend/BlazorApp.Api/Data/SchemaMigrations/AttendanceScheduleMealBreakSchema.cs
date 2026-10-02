namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 考勤排班的用餐次数覆盖列。考勤表已随基线登记，新列走独立版本号迁移，生产须显式执行 --schema=migrate。
/// </summary>
internal static class AttendanceScheduleMealBreakSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.AttendanceSchedule', N'U') IS NULL
        THROW 51851, N'Attendance schedule table is missing.', 1;

    IF COL_LENGTH(N'dbo.AttendanceSchedule', N'MealBreakCount') IS NULL
    BEGIN
        -- 店长指定的用餐次数：NULL＝按班次时长自动扣，有值＝按指定次数扣（0＝取消用餐扣除）。
        ALTER TABLE [dbo].[AttendanceSchedule] ADD [MealBreakCount] int NULL;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列存在、类型 int 且可空；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.AttendanceSchedule', N'U') IS NULL
    THROW 51851, N'Attendance schedule table is missing.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.AttendanceSchedule')
      AND c.[name] = N'MealBreakCount'
      AND ty.[name] = N'int'
      AND c.[is_nullable] = 1
)
    THROW 51852, N'Attendance schedule meal break column signature is incompatible.', 1;
""";
}
