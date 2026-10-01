namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 老系统员工操作日志风险相关四张表：规则命中、金额让利、当前核查结论与核查流水。
/// 表放在 POSM 库，是为了让扫描规则在库内直接用 INSERT…SELECT 从 EmployeeLogs 写入命中，
/// 列表查询也能在同一连接里关联 EmployeeLogs，避免跨库搬运大结果集。
/// POSM 既有迁移已登记，新表必须走独立版本号迁移，生产须显式执行 --schema=migrate；
/// 实体不进 SqlSugar 自动建表清单。错误号区间 51950–51969。
/// </summary>
internal static class LegacyEmployeeLogRiskSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.LegacyEmployeeLogFlags', N'U') IS NULL
    BEGIN
        -- 每条日志每条规则一行；规则撤回只写 RetractedAtUtc，不删行，保留命中证据。
        CREATE TABLE [dbo].[LegacyEmployeeLogFlags]
        (
            [LogId] varchar(255) NOT NULL,
            [RuleCode] varchar(32) NOT NULL,
            [RuleVersion] int NOT NULL,
            [StoreCode] varchar(200) NOT NULL,
            [DeviceCode] varchar(200) NULL,
            [EmployeeId] varchar(50) NULL,
            [EmployeeName] varchar(50) NULL,
            [Operation] varchar(200) NULL,
            [OperationTime] datetime NOT NULL,
            [EvidenceJson] nvarchar(2000) NOT NULL,
            [DetectedAtUtc] datetime2 NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            [RetractedAtUtc] datetime2 NULL,
            CONSTRAINT [PK_LegacyEmployeeLogFlags] PRIMARY KEY CLUSTERED ([LogId], [RuleCode])
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.LegacyEmployeeLogFlags')
                   AND [name] = N'IX_LegacyEmployeeLogFlags_StoreCode_OperationTime')
    BEGIN
        -- 列表按分店 + 时间范围筛选，INCLUDE 覆盖规则、员工、设备与撤回状态，避免回表。
        CREATE NONCLUSTERED INDEX [IX_LegacyEmployeeLogFlags_StoreCode_OperationTime]
            ON [dbo].[LegacyEmployeeLogFlags] ([StoreCode], [OperationTime])
            INCLUDE ([RuleCode], [EmployeeId], [DeviceCode], [RetractedAtUtc]);
    END;

    IF OBJECT_ID(N'dbo.LegacyEmployeeLogImpacts', N'U') IS NULL
    BEGIN
        -- 危险操作的金额让利，正数 = 应收减少。
        CREATE TABLE [dbo].[LegacyEmployeeLogImpacts]
        (
            [LogId] varchar(255) NOT NULL,
            [StoreCode] varchar(200) NOT NULL,
            [EmployeeId] varchar(50) NULL,
            [Operation] varchar(200) NULL,
            [OperationTime] datetime NOT NULL,
            [Amount] decimal(18,2) NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_LegacyEmployeeLogImpacts] PRIMARY KEY CLUSTERED ([LogId])
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.LegacyEmployeeLogImpacts')
                   AND [name] = N'IX_LegacyEmployeeLogImpacts_StoreCode_OperationTime')
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_LegacyEmployeeLogImpacts_StoreCode_OperationTime]
            ON [dbo].[LegacyEmployeeLogImpacts] ([StoreCode], [OperationTime])
            INCLUDE ([EmployeeId], [Amount]);
    END;

    IF OBJECT_ID(N'dbo.LegacyEmployeeLogReviews', N'U') IS NULL
    BEGIN
        -- 每条日志当前核查结论：Result 0 已撤销（视同待核查）、1 确认正常、2 需跟进；Version 做乐观并发。
        CREATE TABLE [dbo].[LegacyEmployeeLogReviews]
        (
            [LogId] varchar(255) NOT NULL,
            [StoreCode] varchar(200) NOT NULL,
            [OperationTime] datetime NOT NULL,
            [Result] tinyint NOT NULL,
            [Note] nvarchar(500) NULL,
            [ReviewedByUserId] nvarchar(50) NOT NULL,
            [ReviewedByName] nvarchar(100) NOT NULL,
            [ReviewedAtUtc] datetime2 NOT NULL,
            [Version] int NOT NULL,
            CONSTRAINT [PK_LegacyEmployeeLogReviews] PRIMARY KEY CLUSTERED ([LogId])
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.LegacyEmployeeLogReviews')
                   AND [name] = N'IX_LegacyEmployeeLogReviews_StoreCode_OperationTime')
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_LegacyEmployeeLogReviews_StoreCode_OperationTime]
            ON [dbo].[LegacyEmployeeLogReviews] ([StoreCode], [OperationTime])
            INCLUDE ([Result]);
    END;

    IF OBJECT_ID(N'dbo.LegacyEmployeeLogReviewHistory', N'U') IS NULL
    BEGIN
        -- 只追加的核查流水，任何结论变更都新增一行，不修改历史。
        CREATE TABLE [dbo].[LegacyEmployeeLogReviewHistory]
        (
            [Id] bigint IDENTITY(1,1) NOT NULL,
            [LogId] varchar(255) NOT NULL,
            [Result] tinyint NOT NULL,
            [Note] nvarchar(500) NULL,
            [ActorUserId] nvarchar(50) NOT NULL,
            [ActorName] nvarchar(100) NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_LegacyEmployeeLogReviewHistory] PRIMARY KEY CLUSTERED ([Id])
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.LegacyEmployeeLogReviewHistory')
                   AND [name] = N'IX_LegacyEmployeeLogReviewHistory_LogId')
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_LegacyEmployeeLogReviewHistory_LogId]
            ON [dbo].[LegacyEmployeeLogReviewHistory] ([LogId])
            INCLUDE ([CreatedAtUtc]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、长度、可空性、decimal 精度、主键与非聚集索引；
    // 不兼容结构只能通过显式迁移修复。nvarchar 的 max_length 是字符数×2，varchar 是字符数。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.LegacyEmployeeLogFlags', N'U') IS NULL
 OR OBJECT_ID(N'dbo.LegacyEmployeeLogImpacts', N'U') IS NULL
 OR OBJECT_ID(N'dbo.LegacyEmployeeLogReviews', N'U') IS NULL
 OR OBJECT_ID(N'dbo.LegacyEmployeeLogReviewHistory', N'U') IS NULL
    THROW 51950, N'Legacy employee log risk tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'LegacyEmployeeLogFlags', N'LogId', N'varchar', 255, 0),
      (N'LegacyEmployeeLogFlags', N'RuleCode', N'varchar', 32, 0),
      (N'LegacyEmployeeLogFlags', N'RuleVersion', N'int', 0, 0),
      (N'LegacyEmployeeLogFlags', N'StoreCode', N'varchar', 200, 0),
      (N'LegacyEmployeeLogFlags', N'DeviceCode', N'varchar', 200, 1),
      (N'LegacyEmployeeLogFlags', N'EmployeeId', N'varchar', 50, 1),
      (N'LegacyEmployeeLogFlags', N'EmployeeName', N'varchar', 50, 1),
      (N'LegacyEmployeeLogFlags', N'Operation', N'varchar', 200, 1),
      (N'LegacyEmployeeLogFlags', N'OperationTime', N'datetime', 0, 0),
      (N'LegacyEmployeeLogFlags', N'EvidenceJson', N'nvarchar', 4000, 0),
      (N'LegacyEmployeeLogFlags', N'DetectedAtUtc', N'datetime2', 0, 0),
      (N'LegacyEmployeeLogFlags', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'LegacyEmployeeLogFlags', N'RetractedAtUtc', N'datetime2', 0, 1),
      (N'LegacyEmployeeLogImpacts', N'LogId', N'varchar', 255, 0),
      (N'LegacyEmployeeLogImpacts', N'StoreCode', N'varchar', 200, 0),
      (N'LegacyEmployeeLogImpacts', N'EmployeeId', N'varchar', 50, 1),
      (N'LegacyEmployeeLogImpacts', N'Operation', N'varchar', 200, 1),
      (N'LegacyEmployeeLogImpacts', N'OperationTime', N'datetime', 0, 0),
      (N'LegacyEmployeeLogImpacts', N'Amount', N'decimal', 0, 0),
      (N'LegacyEmployeeLogImpacts', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'LegacyEmployeeLogReviews', N'LogId', N'varchar', 255, 0),
      (N'LegacyEmployeeLogReviews', N'StoreCode', N'varchar', 200, 0),
      (N'LegacyEmployeeLogReviews', N'OperationTime', N'datetime', 0, 0),
      (N'LegacyEmployeeLogReviews', N'Result', N'tinyint', 0, 0),
      (N'LegacyEmployeeLogReviews', N'Note', N'nvarchar', 1000, 1),
      (N'LegacyEmployeeLogReviews', N'ReviewedByUserId', N'nvarchar', 100, 0),
      (N'LegacyEmployeeLogReviews', N'ReviewedByName', N'nvarchar', 200, 0),
      (N'LegacyEmployeeLogReviews', N'ReviewedAtUtc', N'datetime2', 0, 0),
      (N'LegacyEmployeeLogReviews', N'Version', N'int', 0, 0),
      (N'LegacyEmployeeLogReviewHistory', N'Id', N'bigint', 0, 0),
      (N'LegacyEmployeeLogReviewHistory', N'LogId', N'varchar', 255, 0),
      (N'LegacyEmployeeLogReviewHistory', N'Result', N'tinyint', 0, 0),
      (N'LegacyEmployeeLogReviewHistory', N'Note', N'nvarchar', 1000, 1),
      (N'LegacyEmployeeLogReviewHistory', N'ActorUserId', N'nvarchar', 100, 0),
      (N'LegacyEmployeeLogReviewHistory', N'ActorName', N'nvarchar', 200, 0),
      (N'LegacyEmployeeLogReviewHistory', N'CreatedAtUtc', N'datetime2', 0, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 51951, N'Legacy employee log risk column signature is incompatible.', 1;

-- 金额列必须是 decimal(18,2)；流水主键必须是自增列，否则追加写入会失败或丢精度。
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'dbo.LegacyEmployeeLogImpacts') AND [name] = N'Amount'
      AND [precision] = 18 AND [scale] = 2
)
 OR NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'dbo.LegacyEmployeeLogReviewHistory') AND [name] = N'Id'
      AND [is_identity] = 1
)
    THROW 51952, N'Legacy employee log risk numeric or identity signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.LegacyEmployeeLogFlags') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.LegacyEmployeeLogImpacts') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.LegacyEmployeeLogReviews') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.LegacyEmployeeLogReviewHistory') AND [type] = N'PK')
    THROW 51953, N'Legacy employee log risk primary keys are missing.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.LegacyEmployeeLogFlags')
               AND [name] = N'IX_LegacyEmployeeLogFlags_StoreCode_OperationTime')
 OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.LegacyEmployeeLogImpacts')
                AND [name] = N'IX_LegacyEmployeeLogImpacts_StoreCode_OperationTime')
 OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.LegacyEmployeeLogReviews')
                AND [name] = N'IX_LegacyEmployeeLogReviews_StoreCode_OperationTime')
 OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.LegacyEmployeeLogReviewHistory')
                AND [name] = N'IX_LegacyEmployeeLogReviewHistory_LogId')
    THROW 51954, N'Legacy employee log risk index signature is incompatible.', 1;
""";
}
