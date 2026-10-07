namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 考勤用餐休息的两张新表：AttendanceMealBreak（当班期间的休息记录）与 AttendanceMealClaim（下班时的用餐声明）。
/// 新表走独立版本号迁移，生产须显式执行 --schema=migrate；实体不进 SqlSugarContext 的自动建表清单。
/// 错误号区间 52100–52107。
/// </summary>
internal static class AttendanceMealBreakSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.AttendanceMealBreak', N'U') IS NULL
    BEGIN
        -- 员工当班期间的休息记录：EndUtc 为 NULL 表示进行中。
        CREATE TABLE [dbo].[AttendanceMealBreak]
        (
            [BreakGuid] nvarchar(50) NOT NULL,
            [UserGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [WorkDate] datetime2 NOT NULL,
            [ScheduleGuid] nvarchar(50) NULL,
            [StartUtc] datetime2 NOT NULL,
            [EndUtc] datetime2 NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_AttendanceMealBreak] PRIMARY KEY CLUSTERED ([BreakGuid])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceMealBreak')
          AND [name] = N'IX_AttendanceMealBreak_Schedule'
    )
    BEGIN
        -- 工时与审批展示按排班批量读取休息记录。
        CREATE NONCLUSTERED INDEX [IX_AttendanceMealBreak_Schedule]
            ON [dbo].[AttendanceMealBreak] ([ScheduleGuid]);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceMealBreak')
          AND [name] = N'UX_AttendanceMealBreak_OpenPerUser'
    )
    BEGIN
        -- 数据库兜底：每个员工同一时刻最多一条进行中的休息，并发的两次「开始休息」只会成功一次。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_AttendanceMealBreak_OpenPerUser]
            ON [dbo].[AttendanceMealBreak] ([UserGuid])
            WHERE [EndUtc] IS NULL;
    END;

    IF OBJECT_ID(N'dbo.AttendanceMealClaim', N'U') IS NULL
    BEGIN
        -- 下班时的用餐声明：声明“没休息”才生成店长审核，批准后 ApprovedMinutes 加回工时。
        CREATE TABLE [dbo].[AttendanceMealClaim]
        (
            [ClaimGuid] nvarchar(50) NOT NULL,
            [ScheduleGuid] nvarchar(50) NOT NULL,
            [UserGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [WorkDate] datetime2 NOT NULL,
            [ClockOutPunchGuid] nvarchar(50) NOT NULL,
            [ExpectedCount] int NOT NULL,
            [RecordedCount] int NOT NULL,
            [MissingCount] int NOT NULL,
            [NotTakenCount] int NOT NULL,
            [ClaimedMinutes] int NOT NULL,
            [ApprovedMinutes] int NULL,
            [Status] nvarchar(20) NOT NULL,
            [Reason] nvarchar(500) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [ReviewedAtUtc] datetime2 NULL,
            CONSTRAINT [PK_AttendanceMealClaim] PRIMARY KEY CLUSTERED ([ClaimGuid])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceMealClaim')
          AND [name] = N'UX_AttendanceMealClaim_ClockOutPunch'
    )
    BEGIN
        -- 一次下班打卡最多一条声明；也是重复提交的幂等键。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_AttendanceMealClaim_ClockOutPunch]
            ON [dbo].[AttendanceMealClaim] ([ClockOutPunchGuid]);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceMealClaim')
          AND [name] = N'IX_AttendanceMealClaim_Schedule'
    )
    BEGIN
        -- 工时加回、同一天后续下班判定「已处理次数」都按排班查声明。
        CREATE NONCLUSTERED INDEX [IX_AttendanceMealClaim_Schedule]
            ON [dbo].[AttendanceMealClaim] ([ScheduleGuid]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定两张表的列、类型（nvarchar 的 max_length 为字节数，即字符数 × 2）、可空性、主键与三条索引；
    // 不兼容结构只能通过显式迁移修复，运行时绝不自动改写。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.AttendanceMealBreak', N'U') IS NULL
 OR OBJECT_ID(N'dbo.AttendanceMealClaim', N'U') IS NULL
    THROW 52100, N'Attendance meal break tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'AttendanceMealBreak', N'BreakGuid', N'nvarchar', 100, 0),
      (N'AttendanceMealBreak', N'UserGuid', N'nvarchar', 100, 0),
      (N'AttendanceMealBreak', N'StoreCode', N'nvarchar', 100, 0),
      (N'AttendanceMealBreak', N'WorkDate', N'datetime2', 0, 0),
      (N'AttendanceMealBreak', N'ScheduleGuid', N'nvarchar', 100, 1),
      (N'AttendanceMealBreak', N'StartUtc', N'datetime2', 0, 0),
      (N'AttendanceMealBreak', N'EndUtc', N'datetime2', 0, 1),
      (N'AttendanceMealBreak', N'CreatedAtUtc', N'datetime2', 0, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 52101, N'Attendance meal break column signature is incompatible.', 1;

-- 休息记录主键必须是单列 BreakGuid。
IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints k
    INNER JOIN sys.indexes i ON i.[object_id] = k.[parent_object_id] AND i.[index_id] = k.[unique_index_id]
    WHERE k.[parent_object_id] = OBJECT_ID(N'dbo.AttendanceMealBreak')
      AND k.[type] = N'PK'
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'BreakGuid')
)
    THROW 52102, N'Attendance meal break primary key signature is incompatible.', 1;

-- 「每人至多一条进行中休息」依赖这条过滤唯一索引：必须唯一、单键列 UserGuid、过滤条件为 EndUtc IS NULL。
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.AttendanceMealBreak')
      AND i.[name] = N'UX_AttendanceMealBreak_OpenPerUser'
      AND i.[is_unique] = 1
      AND i.[has_filter] = 1
      AND i.[filter_definition] LIKE N'%EndUtc%IS NULL%'
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'UserGuid')
)
    THROW 52103, N'Attendance meal break open-per-user index signature is incompatible.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.AttendanceMealBreak')
      AND i.[name] = N'IX_AttendanceMealBreak_Schedule'
      AND i.[is_unique] = 0
      AND i.[type] = 2
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'ScheduleGuid')
)
    THROW 52104, N'Attendance meal break schedule index signature is incompatible.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'AttendanceMealClaim', N'ClaimGuid', N'nvarchar', 100, 0),
      (N'AttendanceMealClaim', N'ScheduleGuid', N'nvarchar', 100, 0),
      (N'AttendanceMealClaim', N'UserGuid', N'nvarchar', 100, 0),
      (N'AttendanceMealClaim', N'StoreCode', N'nvarchar', 100, 0),
      (N'AttendanceMealClaim', N'WorkDate', N'datetime2', 0, 0),
      (N'AttendanceMealClaim', N'ClockOutPunchGuid', N'nvarchar', 100, 0),
      (N'AttendanceMealClaim', N'ExpectedCount', N'int', 0, 0),
      (N'AttendanceMealClaim', N'RecordedCount', N'int', 0, 0),
      (N'AttendanceMealClaim', N'MissingCount', N'int', 0, 0),
      (N'AttendanceMealClaim', N'NotTakenCount', N'int', 0, 0),
      (N'AttendanceMealClaim', N'ClaimedMinutes', N'int', 0, 0),
      (N'AttendanceMealClaim', N'ApprovedMinutes', N'int', 0, 1),
      (N'AttendanceMealClaim', N'Status', N'nvarchar', 40, 0),
      (N'AttendanceMealClaim', N'Reason', N'nvarchar', 1000, 1),
      (N'AttendanceMealClaim', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'AttendanceMealClaim', N'ReviewedAtUtc', N'datetime2', 0, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 52105, N'Attendance meal claim column signature is incompatible.', 1;

-- 声明主键必须是单列 ClaimGuid（审批的 SourceGuid 指向它）。
IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints k
    INNER JOIN sys.indexes i ON i.[object_id] = k.[parent_object_id] AND i.[index_id] = k.[unique_index_id]
    WHERE k.[parent_object_id] = OBJECT_ID(N'dbo.AttendanceMealClaim')
      AND k.[type] = N'PK'
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'ClaimGuid')
)
    THROW 52106, N'Attendance meal claim primary key signature is incompatible.', 1;

-- 声明的两条索引：下班打卡唯一（幂等键）、排班非唯一。
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.AttendanceMealClaim')
      AND i.[name] = N'UX_AttendanceMealClaim_ClockOutPunch'
      AND i.[is_unique] = 1
      AND i.[type] = 2
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'ClockOutPunchGuid')
)
 OR NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.AttendanceMealClaim')
      AND i.[name] = N'IX_AttendanceMealClaim_Schedule'
      AND i.[is_unique] = 0
      AND i.[type] = 2
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'ScheduleGuid')
)
    THROW 52107, N'Attendance meal claim index signature is incompatible.', 1;
""";
}
