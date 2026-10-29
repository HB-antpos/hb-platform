namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 考勤带薪假与工资（迁移 20261026.002-attendance-payroll-leave，规格第 1 节）：
/// - 新表：EmployeePayRate（个人时薪表，按生效日期留历史）、AttendancePayPeriod（发薪周期提交 / 锁定）、
///   AttendancePayPeriodNote（每人每期备注）、AttendancePayPeriodLeaveApproval（年假财务核定工时）、
///   AttendancePublicHolidayPay（带薪公假确认）；
/// - AttendanceLeaveRequest 加 PaidHours decimal(6,2) NULL / AttachmentUrls nvarchar(max) NULL / ScheduleGuids nvarchar(max) NULL；
/// - AttendanceAvailability 加 SourceLeaveGuid nvarchar(50) NULL（年假休息批准后自动写的「不能上班」指向来源请假）；
/// - AttendanceSettings 加十个参数列（均带默认值，FortnightAnchorDate 可空）。
/// 同时把新权限码 Attendance.Payroll.View / Edit / Unlock 幂等写入 HbwebSysPermissions，不授予任何角色。
/// Weeks 用 int（规格写 tinyint，SqlSugar 读 tinyint 需 byte，统一用 int 免类型转换）。
/// 生产须显式执行 --schema=migrate，且要先于新版主 API 上线（实体会查这些列）。错误号区间 54310–54313。
/// </summary>
internal static class AttendancePayrollLeaveSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.AttendanceLeaveRequest', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendanceAvailability', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendanceSettings', N'U') IS NULL
    THROW 54310, N'Attendance tables are missing; apply the HBweb baseline first.', 1;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.EmployeePayRate', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[EmployeePayRate]
        (
            [RateGuid] nvarchar(50) NOT NULL,
            [UserGuid] nvarchar(50) NOT NULL,
            [PayMode] nvarchar(20) NOT NULL,
            [FixedTransferHoursPerWeek] decimal(6,2) NOT NULL CONSTRAINT [DF_EmployeePayRate_FixedTransferHoursPerWeek] DEFAULT (0),
            [TransferWeekdayRate] decimal(8,2) NULL,
            [TransferSaturdayRate] decimal(8,2) NULL,
            [TransferSundayRate] decimal(8,2) NULL,
            [TransferPublicHolidayRate] decimal(8,2) NULL,
            [CashRate] decimal(8,2) NULL,
            [CashAllowancePerPeriod] decimal(8,2) NOT NULL CONSTRAINT [DF_EmployeePayRate_CashAllowancePerPeriod] DEFAULT (0),
            [MinimumRate] decimal(8,2) NULL,
            [EffectiveFrom] date NOT NULL,
            [Remark] nvarchar(500) NULL,
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_EmployeePayRate] PRIMARY KEY CLUSTERED ([RateGuid])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeePayRate') AND [name] = N'UX_EmployeePayRate_User_EffectiveFrom')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_EmployeePayRate_User_EffectiveFrom]
            ON [dbo].[EmployeePayRate] ([UserGuid], [EffectiveFrom]);

    IF OBJECT_ID(N'dbo.AttendancePayPeriod', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[AttendancePayPeriod]
        (
            [PeriodGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [PeriodStart] date NOT NULL,
            [Weeks] int NOT NULL,
            [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_AttendancePayPeriod_Status] DEFAULT (N'Open'),
            [SubmittedAt] datetime2 NULL,
            [SubmittedBy] nvarchar(50) NULL,
            [UnlockedAt] datetime2 NULL,
            [UnlockedBy] nvarchar(50) NULL,
            [UnlockReason] nvarchar(500) NULL,
            [CreatedAt] datetime2 NOT NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_AttendancePayPeriod] PRIMARY KEY CLUSTERED ([PeriodGuid])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.AttendancePayPeriod') AND [name] = N'UX_AttendancePayPeriod_Store_Period')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_AttendancePayPeriod_Store_Period]
            ON [dbo].[AttendancePayPeriod] ([StoreCode], [PeriodStart], [Weeks]);

    IF OBJECT_ID(N'dbo.AttendancePayPeriodNote', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[AttendancePayPeriodNote]
        (
            [NoteGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [PeriodStart] date NOT NULL,
            [Weeks] int NOT NULL,
            [UserGuid] nvarchar(50) NOT NULL,
            [Note] nvarchar(1000) NOT NULL,
            [UpdatedAt] datetime2 NOT NULL,
            [UpdatedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_AttendancePayPeriodNote] PRIMARY KEY CLUSTERED ([NoteGuid])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.AttendancePayPeriodNote') AND [name] = N'UX_AttendancePayPeriodNote_Store_Period_User')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_AttendancePayPeriodNote_Store_Period_User]
            ON [dbo].[AttendancePayPeriodNote] ([StoreCode], [PeriodStart], [Weeks], [UserGuid]);

    IF OBJECT_ID(N'dbo.AttendancePayPeriodLeaveApproval', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[AttendancePayPeriodLeaveApproval]
        (
            [ApprovalGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [PeriodStart] date NOT NULL,
            [Weeks] int NOT NULL,
            [UserGuid] nvarchar(50) NOT NULL,
            [LeaveType] nvarchar(30) NOT NULL,
            [RequestedHours] decimal(6,2) NOT NULL,
            [ApprovedHours] decimal(6,2) NOT NULL,
            [ApprovedBy] nvarchar(50) NULL,
            [ApprovedAt] datetime2 NOT NULL,
            CONSTRAINT [PK_AttendancePayPeriodLeaveApproval] PRIMARY KEY CLUSTERED ([ApprovalGuid])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.AttendancePayPeriodLeaveApproval') AND [name] = N'UX_AttendancePayPeriodLeaveApproval_Store_Period_User_Type')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_AttendancePayPeriodLeaveApproval_Store_Period_User_Type]
            ON [dbo].[AttendancePayPeriodLeaveApproval] ([StoreCode], [PeriodStart], [Weeks], [UserGuid], [LeaveType]);

    IF OBJECT_ID(N'dbo.AttendancePublicHolidayPay', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[AttendancePublicHolidayPay]
        (
            [PayGuid] nvarchar(50) NOT NULL,
            [UserGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [HolidayDate] date NOT NULL,
            [OrdinaryCount] int NOT NULL,
            [LookbackWeeks] int NOT NULL,
            [SuggestedHours] decimal(6,2) NULL,
            [Hours] decimal(6,2) NULL,
            [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_AttendancePublicHolidayPay_Status] DEFAULT (N'Pending'),
            [ReviewedBy] nvarchar(50) NULL,
            [ReviewedAt] datetime2 NULL,
            [CreatedAt] datetime2 NOT NULL,
            CONSTRAINT [PK_AttendancePublicHolidayPay] PRIMARY KEY CLUSTERED ([PayGuid])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.AttendancePublicHolidayPay') AND [name] = N'UX_AttendancePublicHolidayPay_User_Store_Date')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_AttendancePublicHolidayPay_User_Store_Date]
            ON [dbo].[AttendancePublicHolidayPay] ([UserGuid], [StoreCode], [HolidayDate]);

    -- 既有表加列：COL_LENGTH 判断保证重复执行是空操作；带默认值的 NOT NULL 列在 SQL Server 上只改元数据。
    IF COL_LENGTH(N'dbo.AttendanceLeaveRequest', N'PaidHours') IS NULL
        ALTER TABLE [dbo].[AttendanceLeaveRequest] ADD [PaidHours] decimal(6,2) NULL;
    IF COL_LENGTH(N'dbo.AttendanceLeaveRequest', N'AttachmentUrls') IS NULL
        ALTER TABLE [dbo].[AttendanceLeaveRequest] ADD [AttachmentUrls] nvarchar(max) NULL;
    IF COL_LENGTH(N'dbo.AttendanceLeaveRequest', N'ScheduleGuids') IS NULL
        ALTER TABLE [dbo].[AttendanceLeaveRequest] ADD [ScheduleGuids] nvarchar(max) NULL;

    IF COL_LENGTH(N'dbo.AttendanceAvailability', N'SourceLeaveGuid') IS NULL
        ALTER TABLE [dbo].[AttendanceAvailability] ADD [SourceLeaveGuid] nvarchar(50) NULL;

    IF COL_LENGTH(N'dbo.AttendanceSettings', N'DefaultPeriodWeeks') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [DefaultPeriodWeeks] int NOT NULL
            CONSTRAINT [DF_AttendanceSettings_DefaultPeriodWeeks] DEFAULT (1);
    IF COL_LENGTH(N'dbo.AttendanceSettings', N'FortnightAnchorDate') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [FortnightAnchorDate] date NULL;
    IF COL_LENGTH(N'dbo.AttendanceSettings', N'PublicHolidayLookbackWeeks') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [PublicHolidayLookbackWeeks] int NOT NULL
            CONSTRAINT [DF_AttendanceSettings_PublicHolidayLookbackWeeks] DEFAULT (10);
    IF COL_LENGTH(N'dbo.AttendanceSettings', N'PublicHolidayMinCount') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [PublicHolidayMinCount] int NOT NULL
            CONSTRAINT [DF_AttendanceSettings_PublicHolidayMinCount] DEFAULT (7);
    IF COL_LENGTH(N'dbo.AttendanceSettings', N'SickLeaveSubmitDays') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [SickLeaveSubmitDays] int NOT NULL
            CONSTRAINT [DF_AttendanceSettings_SickLeaveSubmitDays] DEFAULT (4);
    IF COL_LENGTH(N'dbo.AttendanceSettings', N'AnnualLeaveLeadWeeks') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [AnnualLeaveLeadWeeks] int NOT NULL
            CONSTRAINT [DF_AttendanceSettings_AnnualLeaveLeadWeeks] DEFAULT (4);
    IF COL_LENGTH(N'dbo.AttendanceSettings', N'AnnualLeaveMaxWeeks') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [AnnualLeaveMaxWeeks] int NOT NULL
            CONSTRAINT [DF_AttendanceSettings_AnnualLeaveMaxWeeks] DEFAULT (2);
    IF COL_LENGTH(N'dbo.AttendanceSettings', N'AnnualCashOutLeadDays') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [AnnualCashOutLeadDays] int NOT NULL
            CONSTRAINT [DF_AttendanceSettings_AnnualCashOutLeadDays] DEFAULT (2);
    IF COL_LENGTH(N'dbo.AttendanceSettings', N'AnnualCashOutMaxWeeks') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [AnnualCashOutMaxWeeks] int NOT NULL
            CONSTRAINT [DF_AttendanceSettings_AnnualCashOutMaxWeeks] DEFAULT (4);
    IF COL_LENGTH(N'dbo.AttendanceSettings', N'BirthdayRemindDays') IS NULL
        ALTER TABLE [dbo].[AttendanceSettings] ADD [BirthdayRemindDays] int NOT NULL
            CONSTRAINT [DF_AttendanceSettings_BirthdayRemindDays] DEFAULT (3);

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库；口径要求默认不授予任何角色（生产手动授权）。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), [wanted].[Code], [wanted].[Name], N'排班考勤', [wanted].[Description],
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        FROM (VALUES
            (N'Attendance.Payroll.View', N'查看工资表与时薪表',
             N'Web 页面 /pos-admin/attendance-payroll 与移动端「考勤管理」- 查看工资表、个人时薪表'),
            (N'Attendance.Payroll.Edit', N'编辑时薪、工资备注与提交锁定',
             N'Web 页面 /pos-admin/attendance-payroll 与移动端「考勤管理」- 新增时薪生效记录、填写工资备注与年假核定工时、提交并锁定发薪周期'),
            (N'Attendance.Payroll.Unlock', N'解锁已提交的发薪周期',
             N'Web 页面 /pos-admin/attendance-payroll 与移动端「考勤管理」- 解锁已提交的发薪周期（需填写原因，留操作记录）')
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

    // 只读门禁：新表与新列的类型、精度与可空性必须符合预期，唯一索引必须在（防重复的保证）。
    // sys.columns.max_length：nvarchar 为字节数（声明长度 × 2），nvarchar(max) 为 -1；decimal 用 precision / scale 校验。
    // 签名不符只能通过显式迁移修复，运行时绝不自动改写。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.EmployeePayRate', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendancePayPeriod', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendancePayPeriodNote', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendancePayPeriodLeaveApproval', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendancePublicHolidayPay', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendanceLeaveRequest', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendanceAvailability', N'U') IS NULL
   OR OBJECT_ID(N'dbo.AttendanceSettings', N'U') IS NULL
    THROW 54310, N'Attendance payroll / leave tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.EmployeePayRate', N'RateGuid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.EmployeePayRate', N'UserGuid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.EmployeePayRate', N'PayMode', N'nvarchar', 40, 0, 0, 0),
      (N'dbo.EmployeePayRate', N'FixedTransferHoursPerWeek', N'decimal', 0, 6, 2, 0),
      (N'dbo.EmployeePayRate', N'TransferWeekdayRate', N'decimal', 0, 8, 2, 1),
      (N'dbo.EmployeePayRate', N'TransferSaturdayRate', N'decimal', 0, 8, 2, 1),
      (N'dbo.EmployeePayRate', N'TransferSundayRate', N'decimal', 0, 8, 2, 1),
      (N'dbo.EmployeePayRate', N'TransferPublicHolidayRate', N'decimal', 0, 8, 2, 1),
      (N'dbo.EmployeePayRate', N'CashRate', N'decimal', 0, 8, 2, 1),
      (N'dbo.EmployeePayRate', N'CashAllowancePerPeriod', N'decimal', 0, 8, 2, 0),
      (N'dbo.EmployeePayRate', N'MinimumRate', N'decimal', 0, 8, 2, 1),
      (N'dbo.EmployeePayRate', N'EffectiveFrom', N'date', 0, 0, 0, 0),
      (N'dbo.EmployeePayRate', N'Remark', N'nvarchar', 1000, 0, 0, 1),
      (N'dbo.EmployeePayRate', N'CreatedAt', N'datetime2', 0, 0, 0, 0),
      (N'dbo.AttendancePayPeriod', N'PeriodGuid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.AttendancePayPeriod', N'StoreCode', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.AttendancePayPeriod', N'PeriodStart', N'date', 0, 0, 0, 0),
      (N'dbo.AttendancePayPeriod', N'Weeks', N'int', 0, 0, 0, 0),
      (N'dbo.AttendancePayPeriod', N'Status', N'nvarchar', 40, 0, 0, 0),
      (N'dbo.AttendancePayPeriod', N'SubmittedAt', N'datetime2', 0, 0, 0, 1),
      (N'dbo.AttendancePayPeriod', N'SubmittedBy', N'nvarchar', 100, 0, 0, 1),
      (N'dbo.AttendancePayPeriod', N'UnlockedAt', N'datetime2', 0, 0, 0, 1),
      (N'dbo.AttendancePayPeriod', N'UnlockedBy', N'nvarchar', 100, 0, 0, 1),
      (N'dbo.AttendancePayPeriod', N'UnlockReason', N'nvarchar', 1000, 0, 0, 1),
      (N'dbo.AttendancePayPeriodNote', N'NoteGuid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.AttendancePayPeriodNote', N'StoreCode', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.AttendancePayPeriodNote', N'PeriodStart', N'date', 0, 0, 0, 0),
      (N'dbo.AttendancePayPeriodNote', N'Weeks', N'int', 0, 0, 0, 0),
      (N'dbo.AttendancePayPeriodNote', N'UserGuid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.AttendancePayPeriodNote', N'Note', N'nvarchar', 2000, 0, 0, 0),
      (N'dbo.AttendancePayPeriodLeaveApproval', N'ApprovalGuid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.AttendancePayPeriodLeaveApproval', N'StoreCode', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.AttendancePayPeriodLeaveApproval', N'PeriodStart', N'date', 0, 0, 0, 0),
      (N'dbo.AttendancePayPeriodLeaveApproval', N'Weeks', N'int', 0, 0, 0, 0),
      (N'dbo.AttendancePayPeriodLeaveApproval', N'UserGuid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.AttendancePayPeriodLeaveApproval', N'LeaveType', N'nvarchar', 60, 0, 0, 0),
      (N'dbo.AttendancePayPeriodLeaveApproval', N'RequestedHours', N'decimal', 0, 6, 2, 0),
      (N'dbo.AttendancePayPeriodLeaveApproval', N'ApprovedHours', N'decimal', 0, 6, 2, 0),
      (N'dbo.AttendancePayPeriodLeaveApproval', N'ApprovedAt', N'datetime2', 0, 0, 0, 0),
      (N'dbo.AttendancePublicHolidayPay', N'PayGuid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.AttendancePublicHolidayPay', N'UserGuid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.AttendancePublicHolidayPay', N'StoreCode', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.AttendancePublicHolidayPay', N'HolidayDate', N'date', 0, 0, 0, 0),
      (N'dbo.AttendancePublicHolidayPay', N'OrdinaryCount', N'int', 0, 0, 0, 0),
      (N'dbo.AttendancePublicHolidayPay', N'LookbackWeeks', N'int', 0, 0, 0, 0),
      (N'dbo.AttendancePublicHolidayPay', N'SuggestedHours', N'decimal', 0, 6, 2, 1),
      (N'dbo.AttendancePublicHolidayPay', N'Hours', N'decimal', 0, 6, 2, 1),
      (N'dbo.AttendancePublicHolidayPay', N'Status', N'nvarchar', 40, 0, 0, 0),
      (N'dbo.AttendanceLeaveRequest', N'PaidHours', N'decimal', 0, 6, 2, 1),
      (N'dbo.AttendanceLeaveRequest', N'AttachmentUrls', N'nvarchar', -1, 0, 0, 1),
      (N'dbo.AttendanceLeaveRequest', N'ScheduleGuids', N'nvarchar', -1, 0, 0, 1),
      (N'dbo.AttendanceAvailability', N'SourceLeaveGuid', N'nvarchar', 100, 0, 0, 1),
      (N'dbo.AttendanceSettings', N'DefaultPeriodWeeks', N'int', 0, 0, 0, 0),
      (N'dbo.AttendanceSettings', N'FortnightAnchorDate', N'date', 0, 0, 0, 1),
      (N'dbo.AttendanceSettings', N'PublicHolidayLookbackWeeks', N'int', 0, 0, 0, 0),
      (N'dbo.AttendanceSettings', N'PublicHolidayMinCount', N'int', 0, 0, 0, 0),
      (N'dbo.AttendanceSettings', N'SickLeaveSubmitDays', N'int', 0, 0, 0, 0),
      (N'dbo.AttendanceSettings', N'AnnualLeaveLeadWeeks', N'int', 0, 0, 0, 0),
      (N'dbo.AttendanceSettings', N'AnnualLeaveMaxWeeks', N'int', 0, 0, 0, 0),
      (N'dbo.AttendanceSettings', N'AnnualCashOutLeadDays', N'int', 0, 0, 0, 0),
      (N'dbo.AttendanceSettings', N'AnnualCashOutMaxWeeks', N'int', 0, 0, 0, 0),
      (N'dbo.AttendanceSettings', N'BirthdayRemindDays', N'int', 0, 0, 0, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [precision], [scale], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR (expected.[precision] <> 0 AND (c.[precision] <> expected.[precision] OR c.[scale] <> expected.[scale]))
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54311, N'Attendance payroll / leave column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.EmployeePayRate') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.AttendancePayPeriod') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.AttendancePayPeriodNote') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.AttendancePayPeriodLeaveApproval') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.AttendancePublicHolidayPay') AND [type] = N'PK')
    THROW 54312, N'Attendance payroll / leave primary keys are missing.', 1;

-- 唯一索引必须存在、唯一且键列个数正确（键列顺序由 ApplySql 一次建出，这里校验唯一性与列数）。
IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.EmployeePayRate', N'UX_EmployeePayRate_User_EffectiveFrom', 2),
      (N'dbo.AttendancePayPeriod', N'UX_AttendancePayPeriod_Store_Period', 3),
      (N'dbo.AttendancePayPeriodNote', N'UX_AttendancePayPeriodNote_Store_Period_User', 4),
      (N'dbo.AttendancePayPeriodLeaveApproval', N'UX_AttendancePayPeriodLeaveApproval_Store_Period_User_Type', 5),
      (N'dbo.AttendancePublicHolidayPay', N'UX_AttendancePublicHolidayPay_User_Store_Date', 3)
    ) expected([table_name], [index_name], [key_count])
    LEFT JOIN sys.indexes i
      ON i.[object_id] = OBJECT_ID(expected.[table_name]) AND i.[name] = expected.[index_name]
    WHERE i.[index_id] IS NULL
       OR i.[is_unique] = 0
       OR (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0)
          <> expected.[key_count]
)
    THROW 54313, N'Attendance payroll / leave unique index signature is incompatible.', 1;
""";
}
