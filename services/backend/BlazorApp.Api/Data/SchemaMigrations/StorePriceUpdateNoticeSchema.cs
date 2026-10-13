namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 门店价格更新合并推送（N3-a）：已通知记录表 dbo.StorePriceUpdateNotice。
/// 新表走独立版本号迁移，生产须显式执行 --schema=migrate；实体不进 SqlSugarContext 的自动建表清单。
/// 不建外键：任务过期清理（StorePriceUpdateTask）不应被本表阻塞。错误号区间 52800–52802。
/// </summary>
internal static class StorePriceUpdateNoticeSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.StorePriceUpdateNotice', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[StorePriceUpdateNotice]
        (
            [TaskId] bigint NOT NULL,
            [ChangeCount] int NOT NULL,
            [NotifiedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_StorePriceUpdateNotice] PRIMARY KEY CLUSTERED ([TaskId])
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、可空性与主键；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.StorePriceUpdateNotice', N'U') IS NULL
    THROW 52800, N'Store price update notice table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'StorePriceUpdateNotice', N'TaskId', N'bigint', 0, 0),
      (N'StorePriceUpdateNotice', N'ChangeCount', N'int', 0, 0),
      (N'StorePriceUpdateNotice', N'NotifiedAtUtc', N'datetime2', 0, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 52801, N'Store price update notice column signature is incompatible.', 1;

-- 主键必须是 TaskId 单列：Upsert 与“未通知”判断都依赖它保证每个任务至多一行。
IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints kc
    JOIN sys.index_columns ic ON ic.[object_id] = kc.[parent_object_id] AND ic.[index_id] = kc.[unique_index_id]
    JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
    WHERE kc.[parent_object_id] = OBJECT_ID(N'dbo.StorePriceUpdateNotice')
      AND kc.[type] = N'PK'
      AND c.[name] = N'TaskId'
      AND ic.[key_ordinal] = 1
)
 OR (
    SELECT COUNT(*)
    FROM sys.key_constraints kc
    JOIN sys.index_columns ic ON ic.[object_id] = kc.[parent_object_id] AND ic.[index_id] = kc.[unique_index_id]
    WHERE kc.[parent_object_id] = OBJECT_ID(N'dbo.StorePriceUpdateNotice')
      AND kc.[type] = N'PK'
) <> 1
    THROW 52802, N'Store price update notice primary key signature is incompatible.', 1;
""";
}
