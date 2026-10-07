namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 门店小票资料「下发」两张新表：StoreReceiptProfileRelease（只追加的下发快照）与
/// PosReceiptProfileAck（每台收银设备一行的「已应用版本」回执）。
/// 独立建表而不给 [Store] 加列：Store 实体由 HBweb 与 Hbpos.Api 共用，加列会让未迁移环境的 POS 查询报列不存在。
/// 新表走独立版本号迁移，生产须显式执行 --schema=migrate；实体不进 SqlSugarContext 的自动建表清单。
/// 错误号区间 52000–52005。
/// </summary>
internal static class StoreReceiptProfileReleaseSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.StoreReceiptProfileRelease', N'U') IS NULL
    BEGIN
        -- 只追加：每次「下发」写一行，Version 对每家店从 1 递增；回滚＝把旧内容改回后再下发一次，不删行。
        -- 列宽与 [Store] 表对应列一致，保证 Store 当前值一定放得进快照。
        CREATE TABLE [dbo].[StoreReceiptProfileRelease]
        (
            [StoreCode] nvarchar(50) NOT NULL,
            [Version] int NOT NULL,
            [StoreName] nvarchar(100) NOT NULL,
            [BrandName] nvarchar(100) NULL,
            [Address] nvarchar(500) NULL,
            [Phone] nvarchar(200) NULL,
            [ABN] nvarchar(20) NULL,
            [ReturnPolicy] nvarchar(500) NULL,
            [PublishedAtUtc] datetime2 NOT NULL,
            [PublishedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_StoreReceiptProfileRelease] PRIMARY KEY CLUSTERED ([StoreCode], [Version])
        );
    END;

    IF OBJECT_ID(N'dbo.PosReceiptProfileAck', N'U') IS NULL
    BEGIN
        -- 每台设备一行（upsert）：DeviceCode 对应 POSM 设备表「系统设备编号」，也是设备认证声明 hbpos_device_code。
        CREATE TABLE [dbo].[PosReceiptProfileAck]
        (
            [DeviceCode] nvarchar(100) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [AppliedVersion] int NOT NULL,
            [AppliedAtUtc] datetime2 NOT NULL,
            [ClientKind] nvarchar(16) NOT NULL,
            CONSTRAINT [PK_PosReceiptProfileAck] PRIMARY KEY CLUSTERED ([DeviceCode])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PosReceiptProfileAck')
          AND [name] = N'IX_PosReceiptProfileAck_StoreCode'
    )
    BEGIN
        -- Web 按门店统计「设备应用情况」时按 StoreCode 查回执。
        CREATE NONCLUSTERED INDEX [IX_PosReceiptProfileAck_StoreCode]
            ON [dbo].[PosReceiptProfileAck] ([StoreCode]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定两张表的列、类型（nvarchar 的 max_length 为字节数，即字符数 × 2）、可空性、主键与回执索引；
    // 不兼容结构只能通过显式迁移修复，运行时绝不自动改写。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.StoreReceiptProfileRelease', N'U') IS NULL
 OR OBJECT_ID(N'dbo.PosReceiptProfileAck', N'U') IS NULL
    THROW 52000, N'Store receipt profile release tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'StoreReceiptProfileRelease', N'StoreCode', N'nvarchar', 100, 0),
      (N'StoreReceiptProfileRelease', N'Version', N'int', 0, 0),
      (N'StoreReceiptProfileRelease', N'StoreName', N'nvarchar', 200, 0),
      (N'StoreReceiptProfileRelease', N'BrandName', N'nvarchar', 200, 1),
      (N'StoreReceiptProfileRelease', N'Address', N'nvarchar', 1000, 1),
      (N'StoreReceiptProfileRelease', N'Phone', N'nvarchar', 400, 1),
      (N'StoreReceiptProfileRelease', N'ABN', N'nvarchar', 40, 1),
      (N'StoreReceiptProfileRelease', N'ReturnPolicy', N'nvarchar', 1000, 1),
      (N'StoreReceiptProfileRelease', N'PublishedAtUtc', N'datetime2', 0, 0),
      (N'StoreReceiptProfileRelease', N'PublishedBy', N'nvarchar', 200, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 52001, N'Store receipt profile release column signature is incompatible.', 1;

-- 主键必须是聚集的 (StoreCode, Version)：版本号分配与「取最新快照」都依赖这个键序。
IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints k
    INNER JOIN sys.indexes i ON i.[object_id] = k.[parent_object_id] AND i.[index_id] = k.[unique_index_id]
    WHERE k.[parent_object_id] = OBJECT_ID(N'dbo.StoreReceiptProfileRelease')
      AND k.[type] = N'PK'
      AND i.[type] = 1
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 2
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'StoreCode')
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 2 AND c.[name] = N'Version')
)
    THROW 52002, N'Store receipt profile release primary key signature is incompatible.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'PosReceiptProfileAck', N'DeviceCode', N'nvarchar', 200, 0),
      (N'PosReceiptProfileAck', N'StoreCode', N'nvarchar', 100, 0),
      (N'PosReceiptProfileAck', N'AppliedVersion', N'int', 0, 0),
      (N'PosReceiptProfileAck', N'AppliedAtUtc', N'datetime2', 0, 0),
      (N'PosReceiptProfileAck', N'ClientKind', N'nvarchar', 32, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 52003, N'Pos receipt profile ack column signature is incompatible.', 1;

-- 回执主键必须是单列 DeviceCode：Hbpos.Api 按设备 upsert，主键不符会让回执重复或丢失。
IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints k
    INNER JOIN sys.indexes i ON i.[object_id] = k.[parent_object_id] AND i.[index_id] = k.[unique_index_id]
    WHERE k.[parent_object_id] = OBJECT_ID(N'dbo.PosReceiptProfileAck')
      AND k.[type] = N'PK'
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'DeviceCode')
)
    THROW 52004, N'Pos receipt profile ack primary key signature is incompatible.', 1;

-- 回执的 StoreCode 索引必须存在、非唯一、单键列 StoreCode。
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.PosReceiptProfileAck')
      AND i.[name] = N'IX_PosReceiptProfileAck_StoreCode'
      AND i.[is_unique] = 0
      AND i.[type] = 2
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'StoreCode')
)
    THROW 52005, N'Pos receipt profile ack index signature is incompatible.', 1;
""";
}
