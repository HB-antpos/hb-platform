namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 门店人脸比对服务（docs/design/face-matching-service-20261012.md 第 13.3、14.4 节）：九张新表。
/// FaceStoreProfile / FaceConsentRecord / FaceEnrollment / FaceVisitGroup / FaceTrafficBucket / FaceMatchEvaluation /
/// FaceAuditLog / FaceUnwelcomeEntry / FaceMatchTask。列名、类型、可空性以设计为准，是主 API 与人脸接收服务（受限账号直连）的契约；
/// 只有本迁移建表，人脸服务不写迁移。新表走独立版本号迁移，生产须显式执行 --schema=migrate；实体不进自动建表清单。
/// 与设计相比只多出主 API 自己用的追加列（FaceUnwelcomeEntry.CategoryNote / SecondApprovedBy / SecondApprovedAtUtc）和辅助索引。
/// 时间列一律 UTC datetime2（设计里的 date 列为 date）。错误号区间 52700–52702。
/// </summary>
internal static class FaceMatchSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.FaceStoreProfile', N'U') IS NULL
    BEGIN
        -- 门店档案：决定处理模式与各开关前置条件；接收端每 30 秒读取。
        CREATE TABLE [dbo].[FaceStoreProfile]
        (
            [StoreCode] nvarchar(32) NOT NULL,
            [Mode] tinyint NOT NULL CONSTRAINT [DF_FaceStoreProfile_Mode] DEFAULT (0),
            [EvaluateMatch] bit NOT NULL CONSTRAINT [DF_FaceStoreProfile_EvaluateMatch] DEFAULT (0),
            [StaffExclusionEnabled] bit NOT NULL CONSTRAINT [DF_FaceStoreProfile_StaffExclusionEnabled] DEFAULT (0),
            [TrafficDedupEnabled] bit NOT NULL CONSTRAINT [DF_FaceStoreProfile_TrafficDedupEnabled] DEFAULT (0),
            [UnwelcomeListEnabled] bit NOT NULL CONSTRAINT [DF_FaceStoreProfile_UnwelcomeListEnabled] DEFAULT (0),
            [AllowedSubjects] nvarchar(200) NULL,
            [PiaReference] nvarchar(100) NULL,
            [SignageInstalledOn] date NULL,
            [StaffNoticeDate] date NULL,
            [LegalSignoffBy] nvarchar(100) NULL,
            [LegalSignoffAt] datetime2 NULL,
            [DedupLegalSignoffBy] nvarchar(100) NULL,
            [DedupLegalSignoffAt] datetime2 NULL,
            [CrossBorderRegion] nvarchar(32) NULL,
            [CrossBorderReviewRef] nvarchar(100) NULL,
            [CrossBorderReviewedBy] nvarchar(100) NULL,
            [CrossBorderReviewedAt] datetime2 NULL,
            [TimeZoneId] nvarchar(64) NULL,
            [UnwelcomeAssessmentRef] nvarchar(100) NULL,
            [UnwelcomeLegalSignoffBy] nvarchar(100) NULL,
            [UnwelcomeLegalSignoffAt] datetime2 NULL,
            [StaffTrainingDate] date NULL,
            [PrivacyContact] nvarchar(100) NULL,
            [UpdatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_FaceStoreProfile_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [UpdatedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_FaceStoreProfile] PRIMARY KEY CLUSTERED ([StoreCode])
        );
    END;

    IF OBJECT_ID(N'dbo.FaceConsentRecord', N'U') IS NULL
    BEGIN
        -- 员工同意记录；撤回后保留（证明同意与撤回的时间线）。
        CREATE TABLE [dbo].[FaceConsentRecord]
        (
            [ConsentId] char(32) NOT NULL,
            [UserGuid] nvarchar(50) NOT NULL,
            [ConsentVersion] nvarchar(32) NOT NULL,
            [SignedAtUtc] datetime2 NOT NULL,
            [Method] nvarchar(16) NOT NULL,
            [EvidenceObjectKey] nvarchar(400) NULL,
            [NswNoticeDate] date NULL,
            [WithdrawnAtUtc] datetime2 NULL,
            [WithdrawnReason] nvarchar(200) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NOT NULL,
            CONSTRAINT [PK_FaceConsentRecord] PRIMARY KEY CLUSTERED ([ConsentId])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceConsentRecord')
          AND [name] = N'IX_FaceConsentRecord_UserGuid'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_FaceConsentRecord_UserGuid]
            ON [dbo].[FaceConsentRecord] ([UserGuid]);
    END;

    IF OBJECT_ID(N'dbo.FaceEnrollment', N'U') IS NULL
    BEGIN
        -- 员工登记；登记照另存私有桶，撤回即删。
        CREATE TABLE [dbo].[FaceEnrollment]
        (
            [EnrollmentId] char(32) NOT NULL,
            [UserGuid] nvarchar(50) NOT NULL,
            [ConsentId] char(32) NOT NULL,
            [TencentPersonId] nvarchar(64) NOT NULL,
            [PhotoObjectKeysJson] nvarchar(2000) NOT NULL,
            [Status] tinyint NOT NULL,
            [EnrolledAtUtc] datetime2 NOT NULL,
            [EnrolledBy] nvarchar(100) NOT NULL,
            [WithdrawnAtUtc] datetime2 NULL,
            CONSTRAINT [PK_FaceEnrollment] PRIMARY KEY CLUSTERED ([EnrollmentId])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceEnrollment')
          AND [name] = N'UX_FaceEnrollment_UserGuid_Active'
    )
    BEGIN
        -- 每人同时只能有一条 Active 登记；撤回后可重新登记。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_FaceEnrollment_UserGuid_Active]
            ON [dbo].[FaceEnrollment] ([UserGuid])
            WHERE [Status] = 1;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceEnrollment')
          AND [name] = N'IX_FaceEnrollment_ConsentId'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_FaceEnrollment_ConsentId]
            ON [dbo].[FaceEnrollment] ([ConsentId]);
    END;

    IF OBJECT_ID(N'dbo.FaceVisitGroup', N'U') IS NULL
    BEGIN
        -- 当日访客库登记；到期由主 API 删库。
        CREATE TABLE [dbo].[FaceVisitGroup]
        (
            [GroupId] nvarchar(64) NOT NULL,
            [StoreCode] nvarchar(32) NOT NULL,
            [BusinessDate] date NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [DeleteAfterUtc] datetime2 NOT NULL,
            [DeletedAtUtc] datetime2 NULL,
            [VisitorCount] int NOT NULL CONSTRAINT [DF_FaceVisitGroup_VisitorCount] DEFAULT (0),
            CONSTRAINT [PK_FaceVisitGroup] PRIMARY KEY CLUSTERED ([GroupId])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceVisitGroup')
          AND [name] = N'IX_FaceVisitGroup_Store_Date'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_FaceVisitGroup_Store_Date]
            ON [dbo].[FaceVisitGroup] ([StoreCode], [BusinessDate]);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceVisitGroup')
          AND [name] = N'IX_FaceVisitGroup_DeletePending'
    )
    BEGIN
        -- 到期删库任务只扫未删除的行，已删除的不占索引。
        CREATE NONCLUSTERED INDEX [IX_FaceVisitGroup_DeletePending]
            ON [dbo].[FaceVisitGroup] ([DeleteAfterUtc])
            WHERE [DeletedAtUtc] IS NULL;
    END;

    IF OBJECT_ID(N'dbo.FaceTrafficBucket', N'U') IS NULL
    BEGIN
        -- 客流计数桶（每店每小时），只有计数。
        CREATE TABLE [dbo].[FaceTrafficBucket]
        (
            [StoreCode] nvarchar(32) NOT NULL,
            [BucketStartUtc] datetime2 NOT NULL,
            [CaptureCount] int NOT NULL CONSTRAINT [DF_FaceTrafficBucket_CaptureCount] DEFAULT (0),
            [StaffMatchCount] int NOT NULL CONSTRAINT [DF_FaceTrafficBucket_StaffMatchCount] DEFAULT (0),
            [UniqueVisitorCount] int NOT NULL CONSTRAINT [DF_FaceTrafficBucket_UniqueVisitorCount] DEFAULT (0),
            [RepeatCount] int NOT NULL CONSTRAINT [DF_FaceTrafficBucket_RepeatCount] DEFAULT (0),
            [LowQualityCount] int NOT NULL CONSTRAINT [DF_FaceTrafficBucket_LowQualityCount] DEFAULT (0),
            [ApiFailureCount] int NOT NULL CONSTRAINT [DF_FaceTrafficBucket_ApiFailureCount] DEFAULT (0),
            CONSTRAINT [PK_FaceTrafficBucket] PRIMARY KEY CLUSTERED ([StoreCode], [BucketStartUtc])
        );
    END;

    IF OBJECT_ID(N'dbo.FaceMatchEvaluation', N'U') IS NULL
    BEGIN
        -- 阈值标定评估记录（Capture + EvaluateMatch 的门店）。
        CREATE TABLE [dbo].[FaceMatchEvaluation]
        (
            [CaptureId] char(32) NOT NULL,
            [StaffScore] decimal(9,4) NULL,
            [VisitScore] decimal(9,4) NULL,
            [Decision] tinyint NOT NULL,
            CONSTRAINT [PK_FaceMatchEvaluation] PRIMARY KEY CLUSTERED ([CaptureId])
        );
    END;

    IF OBJECT_ID(N'dbo.FaceAuditLog', N'U') IS NULL
    BEGIN
        -- 持久审计；不含图片、对象键、密钥。
        CREATE TABLE [dbo].[FaceAuditLog]
        (
            [AuditId] bigint IDENTITY(1,1) NOT NULL,
            [AtUtc] datetime2 NOT NULL,
            [ActorId] nvarchar(100) NOT NULL,
            [ActorName] nvarchar(100) NOT NULL,
            [Action] nvarchar(48) NOT NULL,
            [StoreCode] nvarchar(32) NULL,
            [TargetRef] nvarchar(100) NULL,
            [Result] nvarchar(32) NOT NULL,
            CONSTRAINT [PK_FaceAuditLog] PRIMARY KEY CLUSTERED ([AuditId])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceAuditLog')
          AND [name] = N'IX_FaceAuditLog_AtUtc'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_FaceAuditLog_AtUtc]
            ON [dbo].[FaceAuditLog] ([AtUtc] DESC);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceAuditLog')
          AND [name] = N'IX_FaceAuditLog_Store_AtUtc'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_FaceAuditLog_Store_AtUtc]
            ON [dbo].[FaceAuditLog] ([StoreCode], [AtUtc] DESC);
    END;

    IF OBJECT_ID(N'dbo.FaceUnwelcomeEntry', N'U') IS NULL
    BEGIN
        -- 不受欢迎人员名单条目（Pending 时只有私有桶里的参考照片）。
        CREATE TABLE [dbo].[FaceUnwelcomeEntry]
        (
            [EntryId] char(32) NOT NULL,
            [TencentPersonId] nvarchar(64) NOT NULL,
            [IncidentCategory] tinyint NOT NULL,
            [EvidenceRef] nvarchar(100) NOT NULL,
            [StoreScope] nvarchar(500) NOT NULL,
            [SubmittedBy] nvarchar(100) NOT NULL,
            [SubmittedAtUtc] datetime2 NOT NULL,
            [ApprovedBy] nvarchar(100) NULL,
            [ApprovedAtUtc] datetime2 NULL,
            [ReviewDueAtUtc] datetime2 NOT NULL,
            [ExpiresAtUtc] datetime2 NOT NULL,
            [Status] tinyint NOT NULL,
            [PhotoObjectKeysJson] nvarchar(2000) NOT NULL,
            [DisputeNote] nvarchar(500) NULL,
            [CategoryNote] nvarchar(200) NULL,
            [SecondApprovedBy] nvarchar(100) NULL,
            [SecondApprovedAtUtc] datetime2 NULL,
            CONSTRAINT [PK_FaceUnwelcomeEntry] PRIMARY KEY CLUSTERED ([EntryId])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceUnwelcomeEntry')
          AND [name] = N'IX_FaceUnwelcomeEntry_Status_Expires'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_FaceUnwelcomeEntry_Status_Expires]
            ON [dbo].[FaceUnwelcomeEntry] ([Status], [ExpiresAtUtc]);
    END;

    IF OBJECT_ID(N'dbo.FaceMatchTask', N'U') IS NULL
    BEGIN
        -- 命中的复核任务；NotifiedAtUtc 由主 API 后台任务发出提示后写入。
        CREATE TABLE [dbo].[FaceMatchTask]
        (
            [TaskId] char(32) NOT NULL,
            [StoreCode] nvarchar(32) NOT NULL,
            [CameraId] nvarchar(64) NOT NULL,
            [CapturedAtUtc] datetime2 NOT NULL,
            [EntryId] char(32) NOT NULL,
            [Score] decimal(9,4) NOT NULL,
            [ImageObjectKey] nvarchar(400) NOT NULL,
            [ReviewStatus] tinyint NOT NULL CONSTRAINT [DF_FaceMatchTask_ReviewStatus] DEFAULT (0),
            [FirstReviewerId] nvarchar(100) NULL,
            [FirstReviewedAtUtc] datetime2 NULL,
            [SecondReviewerId] nvarchar(100) NULL,
            [SecondReviewedAtUtc] datetime2 NULL,
            [ImageDeleteAtUtc] datetime2 NOT NULL,
            [NotifiedAtUtc] datetime2 NULL,
            CONSTRAINT [PK_FaceMatchTask] PRIMARY KEY CLUSTERED ([TaskId])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceMatchTask')
          AND [name] = N'IX_FaceMatchTask_NotifyPending'
    )
    BEGIN
        -- 命中提示发布任务轮询 NotifiedAtUtc IS NULL 的新任务。
        CREATE NONCLUSTERED INDEX [IX_FaceMatchTask_NotifyPending]
            ON [dbo].[FaceMatchTask] ([CapturedAtUtc])
            WHERE [NotifiedAtUtc] IS NULL;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceMatchTask')
          AND [name] = N'IX_FaceMatchTask_Store_Review'
    )
    BEGIN
        -- 复核任务列表按门店 + 状态 + 时间倒序。
        CREATE NONCLUSTERED INDEX [IX_FaceMatchTask_Store_Review]
            ON [dbo].[FaceMatchTask] ([StoreCode], [ReviewStatus], [CapturedAtUtc] DESC);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceMatchTask')
          AND [name] = N'IX_FaceMatchTask_Entry'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_FaceMatchTask_Entry]
            ON [dbo].[FaceMatchTask] ([EntryId]);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceMatchTask')
          AND [name] = N'IX_FaceMatchTask_ImageDelete'
    )
    BEGIN
        -- 命中图片到期删除按 ImageDeleteAtUtc 扫描。
        CREATE NONCLUSTERED INDEX [IX_FaceMatchTask_ImageDelete]
            ON [dbo].[FaceMatchTask] ([ImageDeleteAtUtc]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、可空性、主键与索引；不兼容结构只能通过显式迁移修复。
    // max_length 按 sys.columns 口径：nvarchar(n) 为 2n、char(n) 为 n、decimal(9,4) 为 5；0 表示不比长度。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.FaceStoreProfile', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceConsentRecord', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceEnrollment', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceVisitGroup', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceTrafficBucket', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceMatchEvaluation', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceAuditLog', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceUnwelcomeEntry', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceMatchTask', N'U') IS NULL
    THROW 52700, N'Face match tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'FaceStoreProfile', N'StoreCode', N'nvarchar', 64, 0),
      (N'FaceStoreProfile', N'Mode', N'tinyint', 0, 0),
      (N'FaceStoreProfile', N'EvaluateMatch', N'bit', 0, 0),
      (N'FaceStoreProfile', N'StaffExclusionEnabled', N'bit', 0, 0),
      (N'FaceStoreProfile', N'TrafficDedupEnabled', N'bit', 0, 0),
      (N'FaceStoreProfile', N'UnwelcomeListEnabled', N'bit', 0, 0),
      (N'FaceStoreProfile', N'AllowedSubjects', N'nvarchar', 400, 1),
      (N'FaceStoreProfile', N'PiaReference', N'nvarchar', 200, 1),
      (N'FaceStoreProfile', N'SignageInstalledOn', N'date', 0, 1),
      (N'FaceStoreProfile', N'StaffNoticeDate', N'date', 0, 1),
      (N'FaceStoreProfile', N'LegalSignoffBy', N'nvarchar', 200, 1),
      (N'FaceStoreProfile', N'LegalSignoffAt', N'datetime2', 0, 1),
      (N'FaceStoreProfile', N'DedupLegalSignoffBy', N'nvarchar', 200, 1),
      (N'FaceStoreProfile', N'DedupLegalSignoffAt', N'datetime2', 0, 1),
      (N'FaceStoreProfile', N'CrossBorderRegion', N'nvarchar', 64, 1),
      (N'FaceStoreProfile', N'CrossBorderReviewRef', N'nvarchar', 200, 1),
      (N'FaceStoreProfile', N'CrossBorderReviewedBy', N'nvarchar', 200, 1),
      (N'FaceStoreProfile', N'CrossBorderReviewedAt', N'datetime2', 0, 1),
      (N'FaceStoreProfile', N'TimeZoneId', N'nvarchar', 128, 1),
      (N'FaceStoreProfile', N'UnwelcomeAssessmentRef', N'nvarchar', 200, 1),
      (N'FaceStoreProfile', N'UnwelcomeLegalSignoffBy', N'nvarchar', 200, 1),
      (N'FaceStoreProfile', N'UnwelcomeLegalSignoffAt', N'datetime2', 0, 1),
      (N'FaceStoreProfile', N'StaffTrainingDate', N'date', 0, 1),
      (N'FaceStoreProfile', N'PrivacyContact', N'nvarchar', 200, 1),
      (N'FaceStoreProfile', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'FaceStoreProfile', N'UpdatedBy', N'nvarchar', 200, 1),
      (N'FaceConsentRecord', N'ConsentId', N'char', 32, 0),
      (N'FaceConsentRecord', N'UserGuid', N'nvarchar', 100, 0),
      (N'FaceConsentRecord', N'ConsentVersion', N'nvarchar', 64, 0),
      (N'FaceConsentRecord', N'SignedAtUtc', N'datetime2', 0, 0),
      (N'FaceConsentRecord', N'Method', N'nvarchar', 32, 0),
      (N'FaceConsentRecord', N'EvidenceObjectKey', N'nvarchar', 800, 1),
      (N'FaceConsentRecord', N'NswNoticeDate', N'date', 0, 1),
      (N'FaceConsentRecord', N'WithdrawnAtUtc', N'datetime2', 0, 1),
      (N'FaceConsentRecord', N'WithdrawnReason', N'nvarchar', 400, 1),
      (N'FaceConsentRecord', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'FaceConsentRecord', N'CreatedBy', N'nvarchar', 200, 0),
      (N'FaceEnrollment', N'EnrollmentId', N'char', 32, 0),
      (N'FaceEnrollment', N'UserGuid', N'nvarchar', 100, 0),
      (N'FaceEnrollment', N'ConsentId', N'char', 32, 0),
      (N'FaceEnrollment', N'TencentPersonId', N'nvarchar', 128, 0),
      (N'FaceEnrollment', N'PhotoObjectKeysJson', N'nvarchar', 4000, 0),
      (N'FaceEnrollment', N'Status', N'tinyint', 0, 0),
      (N'FaceEnrollment', N'EnrolledAtUtc', N'datetime2', 0, 0),
      (N'FaceEnrollment', N'EnrolledBy', N'nvarchar', 200, 0),
      (N'FaceEnrollment', N'WithdrawnAtUtc', N'datetime2', 0, 1),
      (N'FaceVisitGroup', N'GroupId', N'nvarchar', 128, 0),
      (N'FaceVisitGroup', N'StoreCode', N'nvarchar', 64, 0),
      (N'FaceVisitGroup', N'BusinessDate', N'date', 0, 0),
      (N'FaceVisitGroup', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'FaceVisitGroup', N'DeleteAfterUtc', N'datetime2', 0, 0),
      (N'FaceVisitGroup', N'DeletedAtUtc', N'datetime2', 0, 1),
      (N'FaceVisitGroup', N'VisitorCount', N'int', 0, 0),
      (N'FaceTrafficBucket', N'StoreCode', N'nvarchar', 64, 0),
      (N'FaceTrafficBucket', N'BucketStartUtc', N'datetime2', 0, 0),
      (N'FaceTrafficBucket', N'CaptureCount', N'int', 0, 0),
      (N'FaceTrafficBucket', N'StaffMatchCount', N'int', 0, 0),
      (N'FaceTrafficBucket', N'UniqueVisitorCount', N'int', 0, 0),
      (N'FaceTrafficBucket', N'RepeatCount', N'int', 0, 0),
      (N'FaceTrafficBucket', N'LowQualityCount', N'int', 0, 0),
      (N'FaceTrafficBucket', N'ApiFailureCount', N'int', 0, 0),
      (N'FaceMatchEvaluation', N'CaptureId', N'char', 32, 0),
      (N'FaceMatchEvaluation', N'StaffScore', N'decimal', 5, 1),
      (N'FaceMatchEvaluation', N'VisitScore', N'decimal', 5, 1),
      (N'FaceMatchEvaluation', N'Decision', N'tinyint', 0, 0),
      (N'FaceAuditLog', N'AuditId', N'bigint', 0, 0),
      (N'FaceAuditLog', N'AtUtc', N'datetime2', 0, 0),
      (N'FaceAuditLog', N'ActorId', N'nvarchar', 200, 0),
      (N'FaceAuditLog', N'ActorName', N'nvarchar', 200, 0),
      (N'FaceAuditLog', N'Action', N'nvarchar', 96, 0),
      (N'FaceAuditLog', N'StoreCode', N'nvarchar', 64, 1),
      (N'FaceAuditLog', N'TargetRef', N'nvarchar', 200, 1),
      (N'FaceAuditLog', N'Result', N'nvarchar', 64, 0),
      (N'FaceUnwelcomeEntry', N'EntryId', N'char', 32, 0),
      (N'FaceUnwelcomeEntry', N'TencentPersonId', N'nvarchar', 128, 0),
      (N'FaceUnwelcomeEntry', N'IncidentCategory', N'tinyint', 0, 0),
      (N'FaceUnwelcomeEntry', N'EvidenceRef', N'nvarchar', 200, 0),
      (N'FaceUnwelcomeEntry', N'StoreScope', N'nvarchar', 1000, 0),
      (N'FaceUnwelcomeEntry', N'SubmittedBy', N'nvarchar', 200, 0),
      (N'FaceUnwelcomeEntry', N'SubmittedAtUtc', N'datetime2', 0, 0),
      (N'FaceUnwelcomeEntry', N'ApprovedBy', N'nvarchar', 200, 1),
      (N'FaceUnwelcomeEntry', N'ApprovedAtUtc', N'datetime2', 0, 1),
      (N'FaceUnwelcomeEntry', N'ReviewDueAtUtc', N'datetime2', 0, 0),
      (N'FaceUnwelcomeEntry', N'ExpiresAtUtc', N'datetime2', 0, 0),
      (N'FaceUnwelcomeEntry', N'Status', N'tinyint', 0, 0),
      (N'FaceUnwelcomeEntry', N'PhotoObjectKeysJson', N'nvarchar', 4000, 0),
      (N'FaceUnwelcomeEntry', N'DisputeNote', N'nvarchar', 1000, 1),
      (N'FaceUnwelcomeEntry', N'CategoryNote', N'nvarchar', 400, 1),
      (N'FaceUnwelcomeEntry', N'SecondApprovedBy', N'nvarchar', 200, 1),
      (N'FaceUnwelcomeEntry', N'SecondApprovedAtUtc', N'datetime2', 0, 1),
      (N'FaceMatchTask', N'TaskId', N'char', 32, 0),
      (N'FaceMatchTask', N'StoreCode', N'nvarchar', 64, 0),
      (N'FaceMatchTask', N'CameraId', N'nvarchar', 128, 0),
      (N'FaceMatchTask', N'CapturedAtUtc', N'datetime2', 0, 0),
      (N'FaceMatchTask', N'EntryId', N'char', 32, 0),
      (N'FaceMatchTask', N'Score', N'decimal', 5, 0),
      (N'FaceMatchTask', N'ImageObjectKey', N'nvarchar', 800, 0),
      (N'FaceMatchTask', N'ReviewStatus', N'tinyint', 0, 0),
      (N'FaceMatchTask', N'FirstReviewerId', N'nvarchar', 200, 1),
      (N'FaceMatchTask', N'FirstReviewedAtUtc', N'datetime2', 0, 1),
      (N'FaceMatchTask', N'SecondReviewerId', N'nvarchar', 200, 1),
      (N'FaceMatchTask', N'SecondReviewedAtUtc', N'datetime2', 0, 1),
      (N'FaceMatchTask', N'ImageDeleteAtUtc', N'datetime2', 0, 0),
      (N'FaceMatchTask', N'NotifiedAtUtc', N'datetime2', 0, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
       OR (ty.[name] = N'decimal' AND (c.[precision] <> 9 OR c.[scale] <> 4))
)
    THROW 52701, N'Face match column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.FaceStoreProfile') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.FaceConsentRecord') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.FaceEnrollment') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.FaceVisitGroup') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.FaceTrafficBucket') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.FaceMatchEvaluation') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.FaceAuditLog') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.FaceUnwelcomeEntry') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.FaceMatchTask') AND [type] = N'PK')
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceConsentRecord')
      AND [name] = N'IX_FaceConsentRecord_UserGuid'
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceEnrollment')
      AND [name] = N'UX_FaceEnrollment_UserGuid_Active'
      AND [is_unique] = 1
      AND [has_filter] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceEnrollment')
      AND [name] = N'IX_FaceEnrollment_ConsentId'
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceVisitGroup')
      AND [name] = N'IX_FaceVisitGroup_Store_Date'
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceVisitGroup')
      AND [name] = N'IX_FaceVisitGroup_DeletePending'
      AND [has_filter] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceAuditLog')
      AND [name] = N'IX_FaceAuditLog_AtUtc'
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceAuditLog')
      AND [name] = N'IX_FaceAuditLog_Store_AtUtc'
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceUnwelcomeEntry')
      AND [name] = N'IX_FaceUnwelcomeEntry_Status_Expires'
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceMatchTask')
      AND [name] = N'IX_FaceMatchTask_NotifyPending'
      AND [has_filter] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceMatchTask')
      AND [name] = N'IX_FaceMatchTask_Store_Review'
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceMatchTask')
      AND [name] = N'IX_FaceMatchTask_Entry'
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.FaceMatchTask')
      AND [name] = N'IX_FaceMatchTask_ImageDelete'
 )
    THROW 52702, N'Face match key or index signature is incompatible.', 1;
""";
}
