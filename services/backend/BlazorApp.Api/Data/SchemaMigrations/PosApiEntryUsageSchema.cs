namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 收银端 POS API 三条入口的按日用量统计表 dbo.PosApiEntryUsageDaily（迁移 20261026.004-pos-api-entry-usage）。
/// 主键 (UsageDate, StoreCode, Entry)：UsageDate 为悉尼日期（date）、StoreCode 为分店代码（未鉴权请求记空串）、
/// Entry 为 edge / aws / direct / unknown；计数列 RequestCount / Status4xxCount / Status5xxCount 均 bigint，
/// 4xx / 5xx 是 RequestCount 的子集；UpdatedAt 为最近一次 upsert 的 UTC 时间。
/// 由 Hbpos.Api 每 60 秒批量累加写入，主 API 只读查询（周 / 月由查询时对日表汇总，不另建表）。
/// 只新建表，不触碰任何既有表；回退只需删除该表并移除迁移账本行。实体不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// 错误号自成一段 54300–54309：54300 表缺失、54301 列签名、54302 主键、54303 入口 CHECK 约束。
/// </summary>
internal static class PosApiEntryUsageSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.PosApiEntryUsageDaily', N'U') IS NULL
    BEGIN
        -- 33 家店 × 4 个入口 × 365 天约 4.8 万行/年，聚簇主键 (日期, 分店, 入口) 即满足按日期范围扫描的全部查询。
        CREATE TABLE [dbo].[PosApiEntryUsageDaily]
        (
            [UsageDate] date NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [Entry] nvarchar(16) NOT NULL,
            [RequestCount] bigint NOT NULL CONSTRAINT [DF_PosApiEntryUsageDaily_RequestCount] DEFAULT (0),
            [Status4xxCount] bigint NOT NULL CONSTRAINT [DF_PosApiEntryUsageDaily_Status4xxCount] DEFAULT (0),
            [Status5xxCount] bigint NOT NULL CONSTRAINT [DF_PosApiEntryUsageDaily_Status5xxCount] DEFAULT (0),
            [UpdatedAt] datetime2 NOT NULL,
            CONSTRAINT [PK_PosApiEntryUsageDaily] PRIMARY KEY CLUSTERED ([UsageDate], [StoreCode], [Entry]),
            CONSTRAINT [CK_PosApiEntryUsageDaily_Entry] CHECK ([Entry] IN (N'edge', N'aws', N'direct', N'unknown'))
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：列签名（类型 / 长度 / 可空性）、主键键列顺序与入口 CHECK 约束都必须精确匹配；
    // 不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length 对 nvarchar 是字节数（字符数 * 2）：nvarchar(50)=100、nvarchar(16)=32。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.PosApiEntryUsageDaily', N'U') IS NULL
    THROW 54300, N'POS API entry usage table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'UsageDate', N'date', 3, 0),
      (N'StoreCode', N'nvarchar', 100, 0),
      (N'Entry', N'nvarchar', 32, 0),
      (N'RequestCount', N'bigint', 8, 0),
      (N'Status4xxCount', N'bigint', 8, 0),
      (N'Status5xxCount', N'bigint', 8, 0),
      (N'UpdatedAt', N'datetime2', 8, 0)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.PosApiEntryUsageDaily') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54301, N'POS API entry usage column signature is incompatible.', 1;

-- 主键必须是 (UsageDate, StoreCode, Entry) 三列且顺序正确：upsert 的 UPDATE / INSERT 与按日期范围的查询都依赖它。
IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints k
    JOIN sys.indexes i ON i.[object_id] = k.[parent_object_id] AND i.[index_id] = k.[unique_index_id]
    WHERE k.[parent_object_id] = OBJECT_ID(N'dbo.PosApiEntryUsageDaily')
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
                    AND ic.[key_ordinal] = 2 AND c.[name] = N'StoreCode')
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 3 AND c.[name] = N'Entry')
)
    THROW 54302, N'POS API entry usage primary key is missing or incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_PosApiEntryUsageDaily_Entry'
      AND [parent_object_id] = OBJECT_ID(N'dbo.PosApiEntryUsageDaily')
      AND [is_disabled] = 0)
    THROW 54303, N'POS API entry usage check constraint is missing or disabled.', 1;
""";
}
