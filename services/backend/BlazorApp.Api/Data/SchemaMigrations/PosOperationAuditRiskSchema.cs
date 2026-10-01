namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 新收银操作审计（POSM.dbo.pos_operation_audit）的风险相关三张表：规则命中、当前核查结论与核查流水。
/// 审计主表归 Hbpos.Api 所有，这里只新增旁挂表，不改主表结构。与老收银风险表同放 POSM，
/// 列表在同一连接里 EXISTS 关联审计主表。金额让利可从审计行的 before/after 金额直接算出，不单独建表。
/// POSM 既有迁移已登记，新表必须走独立版本号迁移，生产须显式执行 --schema=migrate；
/// 实体不进 SqlSugar 自动建表清单。错误号区间 51970–51989。
/// </summary>
internal static class PosOperationAuditRiskSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.PosOperationAuditFlags', N'U') IS NULL
    BEGIN
        -- 每个事件每条规则一行；规则撤回只写 RetractedAtUtc，不删行，保留命中证据。
        -- 冗余门店、设备、收银员与发生时间，列表与汇总不必回查审计主表。
        CREATE TABLE [dbo].[PosOperationAuditFlags]
        (
            [EventId] uniqueidentifier NOT NULL,
            [RuleCode] varchar(32) NOT NULL,
            [RuleVersion] int NOT NULL,
            [StoreCode] varchar(50) NOT NULL,
            [DeviceCode] varchar(64) NULL,
            [CashierId] varchar(100) NULL,
            [CashierName] nvarchar(128) NULL,
            [OperationType] varchar(64) NOT NULL,
            [OccurredAtUtc] datetime NOT NULL,
            [EvidenceJson] nvarchar(2000) NOT NULL,
            [DetectedAtUtc] datetime2 NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            [RetractedAtUtc] datetime2 NULL,
            CONSTRAINT [PK_PosOperationAuditFlags] PRIMARY KEY CLUSTERED ([EventId], [RuleCode])
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PosOperationAuditFlags')
                   AND [name] = N'IX_PosOperationAuditFlags_StoreCode_OccurredAtUtc')
    BEGIN
        -- 扫描写回按分店 + 时间窗口撤回，汇总按分店 + 时间统计；INCLUDE 覆盖规则、收银员、设备与撤回状态。
        CREATE NONCLUSTERED INDEX [IX_PosOperationAuditFlags_StoreCode_OccurredAtUtc]
            ON [dbo].[PosOperationAuditFlags] ([StoreCode], [OccurredAtUtc])
            INCLUDE ([RuleCode], [CashierId], [DeviceCode], [RetractedAtUtc]);
    END;

    IF OBJECT_ID(N'dbo.PosOperationAuditReviews', N'U') IS NULL
    BEGIN
        -- 每个事件当前核查结论：Result 0 已撤销（视同待核查）、1 确认正常、2 需跟进；Version 做乐观并发。
        CREATE TABLE [dbo].[PosOperationAuditReviews]
        (
            [EventId] uniqueidentifier NOT NULL,
            [StoreCode] varchar(50) NOT NULL,
            [OccurredAtUtc] datetime NOT NULL,
            [Result] tinyint NOT NULL,
            [Note] nvarchar(500) NULL,
            [ReviewedByUserId] nvarchar(50) NOT NULL,
            [ReviewedByName] nvarchar(100) NOT NULL,
            [ReviewedAtUtc] datetime2 NOT NULL,
            [Version] int NOT NULL,
            CONSTRAINT [PK_PosOperationAuditReviews] PRIMARY KEY CLUSTERED ([EventId])
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PosOperationAuditReviews')
                   AND [name] = N'IX_PosOperationAuditReviews_StoreCode_OccurredAtUtc')
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_PosOperationAuditReviews_StoreCode_OccurredAtUtc]
            ON [dbo].[PosOperationAuditReviews] ([StoreCode], [OccurredAtUtc])
            INCLUDE ([Result]);
    END;

    IF OBJECT_ID(N'dbo.PosOperationAuditReviewHistory', N'U') IS NULL
    BEGIN
        -- 只追加的核查流水，任何结论变更都新增一行，不修改历史。
        CREATE TABLE [dbo].[PosOperationAuditReviewHistory]
        (
            [Id] bigint IDENTITY(1,1) NOT NULL,
            [EventId] uniqueidentifier NOT NULL,
            [Result] tinyint NOT NULL,
            [Note] nvarchar(500) NULL,
            [ActorUserId] nvarchar(50) NOT NULL,
            [ActorName] nvarchar(100) NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_PosOperationAuditReviewHistory] PRIMARY KEY CLUSTERED ([Id])
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PosOperationAuditReviewHistory')
                   AND [name] = N'IX_PosOperationAuditReviewHistory_EventId')
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_PosOperationAuditReviewHistory_EventId]
            ON [dbo].[PosOperationAuditReviewHistory] ([EventId])
            INCLUDE ([CreatedAtUtc]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、长度、可空性、主键、自增列与非聚集索引；
    // 不兼容结构只能通过显式迁移修复。nvarchar 的 max_length 是字符数×2，varchar 是字符数。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.PosOperationAuditFlags', N'U') IS NULL
 OR OBJECT_ID(N'dbo.PosOperationAuditReviews', N'U') IS NULL
 OR OBJECT_ID(N'dbo.PosOperationAuditReviewHistory', N'U') IS NULL
    THROW 51970, N'POS operation audit risk tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'PosOperationAuditFlags', N'EventId', N'uniqueidentifier', 0, 0),
      (N'PosOperationAuditFlags', N'RuleCode', N'varchar', 32, 0),
      (N'PosOperationAuditFlags', N'RuleVersion', N'int', 0, 0),
      (N'PosOperationAuditFlags', N'StoreCode', N'varchar', 50, 0),
      (N'PosOperationAuditFlags', N'DeviceCode', N'varchar', 64, 1),
      (N'PosOperationAuditFlags', N'CashierId', N'varchar', 100, 1),
      (N'PosOperationAuditFlags', N'CashierName', N'nvarchar', 256, 1),
      (N'PosOperationAuditFlags', N'OperationType', N'varchar', 64, 0),
      (N'PosOperationAuditFlags', N'OccurredAtUtc', N'datetime', 0, 0),
      (N'PosOperationAuditFlags', N'EvidenceJson', N'nvarchar', 4000, 0),
      (N'PosOperationAuditFlags', N'DetectedAtUtc', N'datetime2', 0, 0),
      (N'PosOperationAuditFlags', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'PosOperationAuditFlags', N'RetractedAtUtc', N'datetime2', 0, 1),
      (N'PosOperationAuditReviews', N'EventId', N'uniqueidentifier', 0, 0),
      (N'PosOperationAuditReviews', N'StoreCode', N'varchar', 50, 0),
      (N'PosOperationAuditReviews', N'OccurredAtUtc', N'datetime', 0, 0),
      (N'PosOperationAuditReviews', N'Result', N'tinyint', 0, 0),
      (N'PosOperationAuditReviews', N'Note', N'nvarchar', 1000, 1),
      (N'PosOperationAuditReviews', N'ReviewedByUserId', N'nvarchar', 100, 0),
      (N'PosOperationAuditReviews', N'ReviewedByName', N'nvarchar', 200, 0),
      (N'PosOperationAuditReviews', N'ReviewedAtUtc', N'datetime2', 0, 0),
      (N'PosOperationAuditReviews', N'Version', N'int', 0, 0),
      (N'PosOperationAuditReviewHistory', N'Id', N'bigint', 0, 0),
      (N'PosOperationAuditReviewHistory', N'EventId', N'uniqueidentifier', 0, 0),
      (N'PosOperationAuditReviewHistory', N'Result', N'tinyint', 0, 0),
      (N'PosOperationAuditReviewHistory', N'Note', N'nvarchar', 1000, 1),
      (N'PosOperationAuditReviewHistory', N'ActorUserId', N'nvarchar', 100, 0),
      (N'PosOperationAuditReviewHistory', N'ActorName', N'nvarchar', 200, 0),
      (N'PosOperationAuditReviewHistory', N'CreatedAtUtc', N'datetime2', 0, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 51971, N'POS operation audit risk column signature is incompatible.', 1;

-- 流水主键必须是自增列，否则追加写入会失败。
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'dbo.PosOperationAuditReviewHistory') AND [name] = N'Id'
      AND [is_identity] = 1
)
    THROW 51972, N'POS operation audit risk identity signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PosOperationAuditFlags') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PosOperationAuditReviews') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PosOperationAuditReviewHistory') AND [type] = N'PK')
    THROW 51973, N'POS operation audit risk primary keys are missing.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PosOperationAuditFlags')
               AND [name] = N'IX_PosOperationAuditFlags_StoreCode_OccurredAtUtc')
 OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PosOperationAuditReviews')
                AND [name] = N'IX_PosOperationAuditReviews_StoreCode_OccurredAtUtc')
 OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PosOperationAuditReviewHistory')
                AND [name] = N'IX_PosOperationAuditReviewHistory_EventId')
    THROW 51974, N'POS operation audit risk index signature is incompatible.', 1;
""";
}
