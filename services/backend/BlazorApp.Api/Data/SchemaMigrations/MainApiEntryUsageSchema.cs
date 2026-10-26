namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 主 API（Web 后台 + 员工 App，路径 /api/*）三条入口的按日用量统计表 dbo.MainApiEntryUsageDaily
/// （迁移 20261027.001-main-api-entry-usage；与收银端 PosApiEntryUsageDaily 同构，但没有分店维度，改为客户端类别）。
/// 主键 (UsageDate, Entry, ClientKind)：UsageDate 为悉尼日期（date）、Entry 为 edge / aws / direct / unknown、
/// ClientKind 为 web / app / other；计数列 RequestCount / Status4xxCount / Status5xxCount 均 bigint，
/// 4xx / 5xx 是 RequestCount 的子集；UpdatedAt 为最近一次 upsert 的 UTC 时间。
/// 只存计数：不存 User-Agent 原文、IP、用户。由主 API 自己每 60 秒批量累加写入并只读查询（周 / 月由查询时对日表汇总）。
/// 只新建表，不触碰任何既有表；回退只需删除该表并移除迁移账本行。实体不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// 错误号自成一段 54320–54329：54320 表缺失、54321 列签名、54322 主键、54323 入口 CHECK、54324 客户端类别 CHECK。
/// </summary>
internal static class MainApiEntryUsageSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.MainApiEntryUsageDaily', N'U') IS NULL
    BEGIN
        -- 4 个入口 × 3 个类别 × 365 天最多约 4400 行/年，聚簇主键 (日期, 入口, 类别) 即满足按日期范围扫描的全部查询。
        CREATE TABLE [dbo].[MainApiEntryUsageDaily]
        (
            [UsageDate] date NOT NULL,
            [Entry] nvarchar(16) NOT NULL,
            [ClientKind] nvarchar(16) NOT NULL,
            [RequestCount] bigint NOT NULL CONSTRAINT [DF_MainApiEntryUsageDaily_RequestCount] DEFAULT (0),
            [Status4xxCount] bigint NOT NULL CONSTRAINT [DF_MainApiEntryUsageDaily_Status4xxCount] DEFAULT (0),
            [Status5xxCount] bigint NOT NULL CONSTRAINT [DF_MainApiEntryUsageDaily_Status5xxCount] DEFAULT (0),
            [UpdatedAt] datetime2 NOT NULL,
            CONSTRAINT [PK_MainApiEntryUsageDaily] PRIMARY KEY CLUSTERED ([UsageDate], [Entry], [ClientKind]),
            CONSTRAINT [CK_MainApiEntryUsageDaily_Entry] CHECK ([Entry] IN (N'edge', N'aws', N'direct', N'unknown')),
            CONSTRAINT [CK_MainApiEntryUsageDaily_ClientKind] CHECK ([ClientKind] IN (N'web', N'app', N'other'))
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：列签名（类型 / 长度 / 可空性）、主键键列顺序与两个 CHECK 约束都必须精确匹配；
    // 不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length 对 nvarchar 是字节数（字符数 * 2）：nvarchar(16)=32。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.MainApiEntryUsageDaily', N'U') IS NULL
    THROW 54320, N'Main API entry usage table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'UsageDate', N'date', 3, 0),
      (N'Entry', N'nvarchar', 32, 0),
      (N'ClientKind', N'nvarchar', 32, 0),
      (N'RequestCount', N'bigint', 8, 0),
      (N'Status4xxCount', N'bigint', 8, 0),
      (N'Status5xxCount', N'bigint', 8, 0),
      (N'UpdatedAt', N'datetime2', 8, 0)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.MainApiEntryUsageDaily') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54321, N'Main API entry usage column signature is incompatible.', 1;

-- 主键必须是 (UsageDate, Entry, ClientKind) 三列且顺序正确：upsert 的 UPDATE / INSERT 与按日期范围的查询都依赖它。
IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints k
    JOIN sys.indexes i ON i.[object_id] = k.[parent_object_id] AND i.[index_id] = k.[unique_index_id]
    WHERE k.[parent_object_id] = OBJECT_ID(N'dbo.MainApiEntryUsageDaily')
      AND k.[type] = N'PK'
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 3
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'UsageDate')
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 2 AND c.[name] = N'Entry')
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 3 AND c.[name] = N'ClientKind')
)
    THROW 54322, N'Main API entry usage primary key is missing or incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_MainApiEntryUsageDaily_Entry'
      AND [parent_object_id] = OBJECT_ID(N'dbo.MainApiEntryUsageDaily')
      AND [is_disabled] = 0)
    THROW 54323, N'Main API entry usage entry check constraint is missing or disabled.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_MainApiEntryUsageDaily_ClientKind'
      AND [parent_object_id] = OBJECT_ID(N'dbo.MainApiEntryUsageDaily')
      AND [is_disabled] = 0)
    THROW 54324, N'Main API entry usage client kind check constraint is missing or disabled.', 1;
""";
}
