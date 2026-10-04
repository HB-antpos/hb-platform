namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 未成年用工合规：档案版本、备用联系人、审计、店长发起的填写请求、排班提醒待办五张表，
/// 以及监护人邮箱验证码所需列。全部走独立版本号迁移，生产须显式执行 --schema=migrate；
/// 实体不进 SqlSugarContext 的 tableTypes，避免 CodeFirst 先建出签名不同的表。
/// </summary>
internal static class EmployeeMinorComplianceSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.EmployeeMinorCompliance', N'U') IS NULL
    BEGIN
        -- 每次影响同意范围的修改都生成新 Version；Revision 只做同一草稿的乐观锁。
        CREATE TABLE [dbo].[EmployeeMinorCompliance] (
            [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EmployeeMinorCompliance] PRIMARY KEY,
            [UserGUID] nvarchar(50) NOT NULL,
            [StoreGUID] nvarchar(50) NULL,
            [StoreCode] nvarchar(50) NULL,
            [StoreTimeZoneId] nvarchar(80) NULL,
            [Version] int NOT NULL,
            [Revision] int NOT NULL CONSTRAINT [DF_EmployeeMinorCompliance_Revision] DEFAULT (1),
            [Status] nvarchar(30) NOT NULL,
            [StateCode] nvarchar(8) NOT NULL,
            [FormType] nvarchar(80) NOT NULL,
            [DateOfBirth] datetime2 NULL,
            [SchoolName] nvarchar(200) NULL,
            [YearLevel] nvarchar(100) NULL,
            [CompletedYear10] bit NULL,
            [EducationStatus] nvarchar(40) NULL,
            [RequiredToBeEnrolled] bit NULL,
            [EducationExemptionVerified] bit NULL,
            [ParticipationEndDate] datetime2 NULL,
            [SchoolCalendarJson] nvarchar(max) NULL,
            [OtherWorkJson] nvarchar(max) NULL,
            [CommuteJson] nvarchar(max) NULL,
            [FormDataJson] nvarchar(max) NULL,
            [GuardianName] nvarchar(200) NOT NULL,
            [GuardianPhone] nvarchar(50) NULL,
            [GuardianEmail] nvarchar(254) NULL,
            [GuardianRelationship] nvarchar(100) NULL,
            [GuardianTokenHash] nvarchar(500) NULL,
            [GuardianTokenExpiresAtUtc] datetime2 NULL,
            [GuardianSignedAtUtc] datetime2 NULL,
            [GuardianSignedName] nvarchar(200) NULL,
            [GuardianSignatureHash] nvarchar(80) NULL,
            [GuardianTokenUsed] bit NOT NULL CONSTRAINT [DF_EmployeeMinorCompliance_GuardianTokenUsed] DEFAULT (0),
            [DocumentObjectKey] nvarchar(500) NULL,
            [DocumentSha256] nvarchar(64) NULL,
            [ReviewActor] nvarchar(100) NULL,
            [ReviewedAtUtc] datetime2 NULL,
            [ReviewComment] nvarchar(1000) NULL,
            [ReturnFieldsJson] nvarchar(1000) NULL,
            [SubmittedBy] nvarchar(100) NULL,
            [SubmittedAtUtc] datetime2 NULL,
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(200) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(200) NULL,
            [IsDeleted] bit NULL
        );
    END;

    -- 监护人邮箱送达与验证码：验证码只存哈希，和签署 token 绑定，重新发起邀请时一并清空。
    IF COL_LENGTH(N'dbo.EmployeeMinorCompliance', N'GuardianInviteChannel') IS NULL
        ALTER TABLE [dbo].[EmployeeMinorCompliance] ADD [GuardianInviteChannel] nvarchar(20) NULL;
    IF COL_LENGTH(N'dbo.EmployeeMinorCompliance', N'GuardianInviteEmailSentAtUtc') IS NULL
        ALTER TABLE [dbo].[EmployeeMinorCompliance] ADD [GuardianInviteEmailSentAtUtc] datetime2 NULL;
    IF COL_LENGTH(N'dbo.EmployeeMinorCompliance', N'GuardianOtpHash') IS NULL
        ALTER TABLE [dbo].[EmployeeMinorCompliance] ADD [GuardianOtpHash] nvarchar(64) NULL;
    IF COL_LENGTH(N'dbo.EmployeeMinorCompliance', N'GuardianOtpExpiresAtUtc') IS NULL
        ALTER TABLE [dbo].[EmployeeMinorCompliance] ADD [GuardianOtpExpiresAtUtc] datetime2 NULL;
    IF COL_LENGTH(N'dbo.EmployeeMinorCompliance', N'GuardianOtpSentAtUtc') IS NULL
        ALTER TABLE [dbo].[EmployeeMinorCompliance] ADD [GuardianOtpSentAtUtc] datetime2 NULL;
    IF COL_LENGTH(N'dbo.EmployeeMinorCompliance', N'GuardianOtpSendCount') IS NULL
        ALTER TABLE [dbo].[EmployeeMinorCompliance] ADD [GuardianOtpSendCount] int NOT NULL
            CONSTRAINT [DF_EmployeeMinorCompliance_GuardianOtpSendCount] DEFAULT (0);
    IF COL_LENGTH(N'dbo.EmployeeMinorCompliance', N'GuardianOtpFailedAttempts') IS NULL
        ALTER TABLE [dbo].[EmployeeMinorCompliance] ADD [GuardianOtpFailedAttempts] int NOT NULL
            CONSTRAINT [DF_EmployeeMinorCompliance_GuardianOtpFailedAttempts] DEFAULT (0);
    IF COL_LENGTH(N'dbo.EmployeeMinorCompliance', N'GuardianEmailVerifiedAtUtc') IS NULL
        ALTER TABLE [dbo].[EmployeeMinorCompliance] ADD [GuardianEmailVerifiedAtUtc] datetime2 NULL;
    -- 验证通过后发给监护人浏览器的会话密钥（只存哈希）：查看与签署都要带上，转发出去的链接没有它就打不开。
    IF COL_LENGTH(N'dbo.EmployeeMinorCompliance', N'GuardianSessionHash') IS NULL
        ALTER TABLE [dbo].[EmployeeMinorCompliance] ADD [GuardianSessionHash] nvarchar(64) NULL;
    IF COL_LENGTH(N'dbo.EmployeeMinorCompliance', N'GuardianSessionExpiresAtUtc') IS NULL
        ALTER TABLE [dbo].[EmployeeMinorCompliance] ADD [GuardianSessionExpiresAtUtc] datetime2 NULL;
    -- 监护人在签署页现场修改过的字段分组（JSON 数组），供员工与 HR 查看；修改前后值记在审计里。
    IF COL_LENGTH(N'dbo.EmployeeMinorCompliance', N'GuardianAmendedFieldsJson') IS NULL
        ALTER TABLE [dbo].[EmployeeMinorCompliance] ADD [GuardianAmendedFieldsJson] nvarchar(1000) NULL;

    IF OBJECT_ID(N'dbo.EmployeeMinorComplianceContact', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[EmployeeMinorComplianceContact] (
            [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EmployeeMinorComplianceContact] PRIMARY KEY,
            [ComplianceId] int NOT NULL,
            [ContactType] nvarchar(30) NOT NULL,
            [FullName] nvarchar(200) NOT NULL,
            [Phone] nvarchar(50) NOT NULL,
            [Mobile] nvarchar(50) NULL,
            [Email] nvarchar(254) NULL,
            [Address] nvarchar(500) NULL,
            [Postcode] nvarchar(20) NULL,
            [Relationship] nvarchar(100) NULL,
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(200) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(200) NULL,
            [IsDeleted] bit NULL
        );
    END;

    IF OBJECT_ID(N'dbo.EmployeeMinorComplianceAudit', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[EmployeeMinorComplianceAudit] (
            [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EmployeeMinorComplianceAudit] PRIMARY KEY,
            [ComplianceId] int NOT NULL,
            [Action] nvarchar(50) NOT NULL,
            [ActorUserGuid] nvarchar(50) NULL,
            [ActorLabel] nvarchar(200) NULL,
            [MetadataJson] nvarchar(max) NULL,
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(200) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(200) NULL,
            [IsDeleted] bit NULL
        );
    END;

    -- 审计元数据要容纳监护人修改前后的完整资料，早期按 1000 建过的表一并放宽。
    IF COL_LENGTH(N'dbo.EmployeeMinorComplianceAudit', N'MetadataJson') <> -1
        ALTER TABLE [dbo].[EmployeeMinorComplianceAudit] ALTER COLUMN [MetadataJson] nvarchar(max) NULL;

    IF OBJECT_ID(N'dbo.EmployeeMinorComplianceRequest', N'U') IS NULL
    BEGIN
        -- 店长发起的填写请求：员工还没建档时也要能挂待办，所以独立成表；员工提交 HR 审核时自动完成。
        CREATE TABLE [dbo].[EmployeeMinorComplianceRequest] (
            [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EmployeeMinorComplianceRequest] PRIMARY KEY,
            [UserGUID] nvarchar(50) NOT NULL,
            [StoreGUID] nvarchar(50) NULL,
            [StoreCode] nvarchar(50) NULL,
            [Status] nvarchar(20) NOT NULL,
            [Note] nvarchar(500) NULL,
            [DueDate] datetime2 NULL,
            [RequestedByUserGuid] nvarchar(50) NULL,
            [RequestedByName] nvarchar(200) NULL,
            [CompletedAtUtc] datetime2 NULL,
            [CompletedComplianceId] int NULL,
            [CancelledAtUtc] datetime2 NULL,
            [CancelledBy] nvarchar(200) NULL,
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(200) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(200) NULL,
            [IsDeleted] bit NULL
        );
    END;

    IF OBJECT_ID(N'dbo.EmployeeMinorReminder', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[EmployeeMinorReminder] (
            [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EmployeeMinorReminder] PRIMARY KEY,
            [Fingerprint] nvarchar(64) NOT NULL,
            [UserGUID] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [ScheduleGuid] nvarchar(50) NULL,
            [RuleId] nvarchar(80) NOT NULL,
            [Severity] nvarchar(30) NOT NULL,
            [RuleCategory] nvarchar(40) NOT NULL,
            [Message] nvarchar(1000) NOT NULL,
            [SourceUrl] nvarchar(500) NULL,
            [WorkDate] datetime2 NULL,
            [ActualMinutes] int NULL,
            [LimitMinutes] int NULL,
            [Trigger] nvarchar(40) NOT NULL,
            [Status] nvarchar(20) NOT NULL,
            [Revision] int NOT NULL CONSTRAINT [DF_EmployeeMinorReminder_Revision] DEFAULT (1),
            [ActionActor] nvarchar(200) NULL,
            [ActionComment] nvarchar(1000) NULL,
            [ActionAtUtc] datetime2 NULL,
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(200) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(200) NULL,
            [IsDeleted] bit NULL
        );
    END;

    IF OBJECT_ID(N'dbo.EmployeeMinorReminderEvent', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[EmployeeMinorReminderEvent] (
            [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EmployeeMinorReminderEvent] PRIMARY KEY,
            [ReminderId] int NOT NULL,
            [Action] nvarchar(30) NOT NULL,
            [Actor] nvarchar(200) NOT NULL,
            [Comment] nvarchar(1000) NULL,
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(200) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(200) NULL,
            [IsDeleted] bit NULL
        );
    END;

    -- 索引单独按名称幂等补建：丢失的索引重跑迁移即可恢复，不受建表块只执行一次的限制。
    -- UX_MinorRequest_OpenPerUser：同一员工同时只允许一条未完成请求，并发重复发起由数据库兜底。
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeMinorCompliance') AND [name] = N'UX_MinorCompliance_UserVersion')
        CREATE UNIQUE INDEX [UX_MinorCompliance_UserVersion] ON [dbo].[EmployeeMinorCompliance] ([UserGUID], [Version]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeMinorCompliance') AND [name] = N'IX_MinorCompliance_Store_Status')
        CREATE INDEX [IX_MinorCompliance_Store_Status] ON [dbo].[EmployeeMinorCompliance] ([StoreGUID], [Status]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeMinorComplianceContact') AND [name] = N'IX_MinorContact_Compliance')
        CREATE INDEX [IX_MinorContact_Compliance] ON [dbo].[EmployeeMinorComplianceContact] ([ComplianceId]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeMinorComplianceAudit') AND [name] = N'IX_MinorAudit_Compliance')
        CREATE INDEX [IX_MinorAudit_Compliance] ON [dbo].[EmployeeMinorComplianceAudit] ([ComplianceId], [CreatedAt]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeMinorComplianceRequest') AND [name] = N'IX_MinorRequest_User_Status')
        CREATE INDEX [IX_MinorRequest_User_Status] ON [dbo].[EmployeeMinorComplianceRequest] ([UserGUID], [Status]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeMinorComplianceRequest') AND [name] = N'UX_MinorRequest_OpenPerUser')
        CREATE UNIQUE INDEX [UX_MinorRequest_OpenPerUser] ON [dbo].[EmployeeMinorComplianceRequest] ([UserGUID])
            WHERE [Status] = N'open';
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeMinorReminder') AND [name] = N'UX_MinorReminder_Fingerprint')
        CREATE UNIQUE INDEX [UX_MinorReminder_Fingerprint] ON [dbo].[EmployeeMinorReminder] ([Fingerprint]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeMinorReminder') AND [name] = N'IX_MinorReminder_StoreStatus')
        CREATE INDEX [IX_MinorReminder_StoreStatus] ON [dbo].[EmployeeMinorReminder] ([StoreCode], [Status], [CreatedAt] DESC);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeMinorReminderEvent') AND [name] = N'IX_MinorReminderEvent_Record')
        CREATE INDEX [IX_MinorReminderEvent_Record] ON [dbo].[EmployeeMinorReminderEvent] ([ReminderId], [CreatedAt]);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：六张表都在，且业务依赖的关键列类型、可空性和唯一索引正确；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.EmployeeMinorCompliance', N'U') IS NULL
    OR OBJECT_ID(N'dbo.EmployeeMinorComplianceContact', N'U') IS NULL
    OR OBJECT_ID(N'dbo.EmployeeMinorComplianceAudit', N'U') IS NULL
    OR OBJECT_ID(N'dbo.EmployeeMinorComplianceRequest', N'U') IS NULL
    OR OBJECT_ID(N'dbo.EmployeeMinorReminder', N'U') IS NULL
    OR OBJECT_ID(N'dbo.EmployeeMinorReminderEvent', N'U') IS NULL
    THROW 51980, N'Employee minor compliance tables are missing.', 1;

IF (
    SELECT COUNT(*)
    FROM sys.columns c
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.EmployeeMinorCompliance')
      AND (
            (c.[name] = N'UserGUID' AND ty.[name] = N'nvarchar' AND c.[max_length] = 100 AND c.[is_nullable] = 0)
         OR (c.[name] = N'Version' AND ty.[name] = N'int' AND c.[is_nullable] = 0)
         OR (c.[name] = N'Revision' AND ty.[name] = N'int' AND c.[is_nullable] = 0)
         OR (c.[name] = N'Status' AND ty.[name] = N'nvarchar' AND c.[max_length] = 60 AND c.[is_nullable] = 0)
         OR (c.[name] = N'GuardianTokenUsed' AND ty.[name] = N'bit' AND c.[is_nullable] = 0)
         OR (c.[name] = N'GuardianInviteChannel' AND ty.[name] = N'nvarchar' AND c.[max_length] = 40)
         OR (c.[name] = N'GuardianOtpHash' AND ty.[name] = N'nvarchar' AND c.[max_length] = 128)
         OR (c.[name] = N'GuardianOtpSendCount' AND ty.[name] = N'int' AND c.[is_nullable] = 0)
         OR (c.[name] = N'GuardianOtpFailedAttempts' AND ty.[name] = N'int' AND c.[is_nullable] = 0)
         OR (c.[name] = N'GuardianEmailVerifiedAtUtc' AND ty.[name] = N'datetime2')
         OR (c.[name] = N'GuardianSessionHash' AND ty.[name] = N'nvarchar' AND c.[max_length] = 128)
         OR (c.[name] = N'GuardianAmendedFieldsJson' AND ty.[name] = N'nvarchar' AND c.[max_length] = 2000)
      )
) <> 12
    THROW 51981, N'Employee minor compliance column signature is incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeMinorCompliance')
      AND [name] = N'UX_MinorCompliance_UserVersion' AND [is_unique] = 1
)
    THROW 51982, N'Employee minor compliance version index is missing.', 1;

IF (
    SELECT COUNT(*)
    FROM sys.columns c
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.EmployeeMinorComplianceRequest')
      AND (
            (c.[name] = N'UserGUID' AND ty.[name] = N'nvarchar' AND c.[max_length] = 100 AND c.[is_nullable] = 0)
         OR (c.[name] = N'Status' AND ty.[name] = N'nvarchar' AND c.[max_length] = 40 AND c.[is_nullable] = 0)
         OR (c.[name] = N'StoreGUID' AND ty.[name] = N'nvarchar' AND c.[max_length] = 100)
         OR (c.[name] = N'CompletedComplianceId' AND ty.[name] = N'int')
      )
) <> 4
    THROW 51983, N'Employee minor compliance request column signature is incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeMinorComplianceRequest')
      AND [name] = N'UX_MinorRequest_OpenPerUser' AND [is_unique] = 1 AND [has_filter] = 1
)
    THROW 51984, N'Employee minor compliance open request index is missing.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns c
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.EmployeeMinorComplianceAudit')
      AND c.[name] = N'MetadataJson' AND ty.[name] = N'nvarchar' AND c.[max_length] = -1
)
    THROW 51986, N'Employee minor compliance audit metadata must be nvarchar(max).', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeMinorReminder')
      AND [name] = N'UX_MinorReminder_Fingerprint' AND [is_unique] = 1
)
    THROW 51985, N'Employee minor reminder fingerprint index is missing.', 1;
""";
}
