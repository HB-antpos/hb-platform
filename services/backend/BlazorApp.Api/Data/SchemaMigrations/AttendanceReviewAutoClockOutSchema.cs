namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 考勤审核口径第一轮（迁移 20261026.001-attendance-review-auto-clock-out），一次加齐卡 A 需要的列：
/// - AttendancePunch.IsSystemAutoClockOut bit NOT NULL DEFAULT 0：午夜自动下班卡标记，该卡闭合的班段不计薪；
/// - AttendanceSchedule.DeletedAt datetime2 NULL / DeletedBy nvarchar(100) NULL：软删除留痕，供「已删除排班」找回；
/// - AttendanceApproval.IsSelfReview bit NOT NULL DEFAULT 0：持 Attendance.Approval.ReviewSelf 审自己的申请时标记；
/// - AttendanceSettings.EmployeeSegmentLimit int NOT NULL DEFAULT 4 / ManagerSegmentLimit int NOT NULL DEFAULT 6
///   / AutoClockOutAtMidnight bit NOT NULL DEFAULT 1：每日段数上限与午夜自动下班开关。
/// 同时把新权限码 Attendance.Approval.ReviewSelf 幂等写入 HbwebSysPermissions（启动不跑权限种子），不授予任何角色。
/// 考勤表随基线登记，新列只能走独立版本号迁移；生产须显式执行 --schema=migrate，且要先于新版主 API 上线
/// （实体会查这些列）。错误号区间 54300–54301。
/// </summary>
internal static class AttendanceReviewAutoClockOutSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.AttendancePunch', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendanceSchedule', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendanceApproval', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendanceSettings', N'U') IS NULL
    THROW 54300, N'Attendance tables are missing; apply the HBweb baseline first.', 1;

BEGIN TRANSACTION;
BEGIN TRY
    -- COL_LENGTH 判断保证重复执行是空操作；带默认值的 NOT NULL 列在 SQL Server 上只改元数据。
    IF COL_LENGTH(N'dbo.AttendancePunch', N'IsSystemAutoClockOut') IS NULL
        ALTER TABLE [dbo].[AttendancePunch] ADD [IsSystemAutoClockOut] bit NOT NULL
            CONSTRAINT [DF_AttendancePunch_IsSystemAutoClockOut] DEFAULT (0);

    IF COL_LENGTH(N'dbo.AttendanceSchedule', N'DeletedAt') IS NULL
        ALTER TABLE [dbo].[AttendanceSchedule] ADD [DeletedAt] datetime2 NULL;
    IF COL_LENGTH(N'dbo.AttendanceSchedule', N'DeletedBy') IS NULL
        ALTER TABLE [dbo].[AttendanceSchedule] ADD [DeletedBy] nvarchar(100) NULL;

    IF COL_LENGTH(N'dbo.AttendanceApproval', N'IsSelfReview') IS NULL
        ALTER TABLE [dbo].[AttendanceApproval] ADD [IsSelfReview] bit NOT NULL
            CONSTRAINT [DF_AttendanceApproval_IsSelfReview] DEFAULT (0);

    IF COL_LENGTH(N'dbo.AttendanceSettings', N'EmployeeSegmentLimit') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [EmployeeSegmentLimit] int NOT NULL
            CONSTRAINT [DF_AttendanceSettings_EmployeeSegmentLimit] DEFAULT (4);
    IF COL_LENGTH(N'dbo.AttendanceSettings', N'ManagerSegmentLimit') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [ManagerSegmentLimit] int NOT NULL
            CONSTRAINT [DF_AttendanceSettings_ManagerSegmentLimit] DEFAULT (6);
    IF COL_LENGTH(N'dbo.AttendanceSettings', N'AutoClockOutAtMidnight') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [AutoClockOutAtMidnight] bit NOT NULL
            CONSTRAINT [DF_AttendanceSettings_AutoClockOutAtMidnight] DEFAULT (1);

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库，否则角色管理里无法授予；口径要求默认不授予任何角色。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), [wanted].[Code], [wanted].[Name], N'排班考勤', [wanted].[Description],
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        FROM (VALUES
            (N'Attendance.Approval.ReviewSelf', N'审核本人考勤申请',
             N'Web 页面 /pos-admin/schedule-attendance 与移动端「考勤管理」- 持有者可审核自己提交的考勤申请，审核记录标记「本人审批」')
        ) AS [wanted]([Code], [Name], [Description])
        WHERE NOT EXISTS (
            SELECT 1 FROM [dbo].[HbwebSysPermissions] AS [existing] WHERE [existing].[Code] = [wanted].[Code]
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：七列的类型、长度与可空性必须符合预期（nvarchar 的 max_length 按字节计为声明长度的 2 倍）。
    // 签名不符只能通过显式迁移修复，运行时绝不自动改写。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.AttendancePunch', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendanceSchedule', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendanceApproval', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendanceSettings', N'U') IS NULL
    THROW 54300, N'Attendance tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.AttendancePunch', N'IsSystemAutoClockOut', N'bit', 1, 0),
      (N'dbo.AttendanceSchedule', N'DeletedAt', N'datetime2', 8, 1),
      (N'dbo.AttendanceSchedule', N'DeletedBy', N'nvarchar', 200, 1),
      (N'dbo.AttendanceApproval', N'IsSelfReview', N'bit', 1, 0),
      (N'dbo.AttendanceSettings', N'EmployeeSegmentLimit', N'int', 4, 0),
      (N'dbo.AttendanceSettings', N'ManagerSegmentLimit', N'int', 4, 0),
      (N'dbo.AttendanceSettings', N'AutoClockOutAtMidnight', N'bit', 1, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    -- 全新库的基线由 CodeFirst 按实体建表，DateTime? 会落成 datetime；与 datetime2 同为 8 字节，两者都接受。
    WHERE c.[column_id] IS NULL
       OR (ty.[name] <> expected.[type_name]
           AND NOT (expected.[type_name] = N'datetime2' AND ty.[name] = N'datetime'))
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54301, N'Attendance review / auto clock-out column signature is incompatible.', 1;
""";
}
