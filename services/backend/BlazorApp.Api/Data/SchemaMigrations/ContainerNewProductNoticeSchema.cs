namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 新品到店每日推送（门店本地 8 点）：两张新表。
/// ContainerNewProductSeen 是每店累计「已见过的新品」集合（比昨天新增的基线），ContainerNewProductRun 记录每店每个本地日期已处理过，防同日重复。
/// 新表走独立版本号迁移，生产须显式执行 --schema=migrate；实体不进 SqlSugarContext 的自动建表清单。错误号区间 52800–52802。
/// </summary>
internal static class ContainerNewProductNoticeSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.ContainerNewProductSeen', N'U') IS NULL
    BEGIN
        -- 每店累计已见过的新品；主键 (门店, 商品) 同时兜住并发重复写入。
        CREATE TABLE [dbo].[ContainerNewProductSeen]
        (
            [StoreCode] nvarchar(50) NOT NULL,
            [ProductCode] nvarchar(50) NOT NULL,
            [FirstLocalDate] date NOT NULL,
            [ContainerCode] nvarchar(50) NOT NULL,
            [NotificationId] nvarchar(32) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_ContainerNewProductSeen] PRIMARY KEY CLUSTERED ([StoreCode], [ProductCode])
        );
    END;

    IF OBJECT_ID(N'dbo.ContainerNewProductRun', N'U') IS NULL
    BEGIN
        -- 每店每个本地日期一行：已处理（含首次基线日与零新增日），同日不再重复处理。
        CREATE TABLE [dbo].[ContainerNewProductRun]
        (
            [StoreCode] nvarchar(50) NOT NULL,
            [LocalDate] date NOT NULL,
            [ProcessedAtUtc] datetime2 NOT NULL,
            [NewCount] int NOT NULL,
            [IsBaseline] bit NOT NULL,
            [NotificationId] nvarchar(32) NULL,
            CONSTRAINT [PK_ContainerNewProductRun] PRIMARY KEY CLUSTERED ([StoreCode], [LocalDate])
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、可空性与主键列顺序；不兼容结构只能通过显式迁移修复。
    // nvarchar 的 max_length 是字节数（字符数 × 2）；date / int / bit 长度固定，传 0 表示不比较。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.ContainerNewProductSeen', N'U') IS NULL
 OR OBJECT_ID(N'dbo.ContainerNewProductRun', N'U') IS NULL
    THROW 52810, N'Container new product notice tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'ContainerNewProductSeen', N'StoreCode', N'nvarchar', 100, 0),
      (N'ContainerNewProductSeen', N'ProductCode', N'nvarchar', 100, 0),
      (N'ContainerNewProductSeen', N'FirstLocalDate', N'date', 0, 0),
      (N'ContainerNewProductSeen', N'ContainerCode', N'nvarchar', 100, 0),
      (N'ContainerNewProductSeen', N'NotificationId', N'nvarchar', 64, 1),
      (N'ContainerNewProductSeen', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'ContainerNewProductRun', N'StoreCode', N'nvarchar', 100, 0),
      (N'ContainerNewProductRun', N'LocalDate', N'date', 0, 0),
      (N'ContainerNewProductRun', N'ProcessedAtUtc', N'datetime2', 0, 0),
      (N'ContainerNewProductRun', N'NewCount', N'int', 0, 0),
      (N'ContainerNewProductRun', N'IsBaseline', N'bit', 0, 0),
      (N'ContainerNewProductRun', N'NotificationId', N'nvarchar', 64, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 52811, N'Container new product notice column signature is incompatible.', 1;

-- 主键必须正好是 (StoreCode, ProductCode) 与 (StoreCode, LocalDate)，列顺序一致；多一列或换序都算不兼容。
IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'ContainerNewProductSeen', 1, N'StoreCode'),
      (N'ContainerNewProductSeen', 2, N'ProductCode'),
      (N'ContainerNewProductRun', 1, N'StoreCode'),
      (N'ContainerNewProductRun', 2, N'LocalDate')
    ) expected([table_name], [key_ordinal], [column_name])
    LEFT JOIN sys.key_constraints kc
        ON kc.[parent_object_id] = OBJECT_ID(N'dbo.' + expected.[table_name]) AND kc.[type] = N'PK'
    LEFT JOIN sys.index_columns ic
        ON ic.[object_id] = kc.[parent_object_id]
       AND ic.[index_id] = kc.[unique_index_id]
       AND ic.[key_ordinal] = expected.[key_ordinal]
    LEFT JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
    WHERE c.[name] IS NULL OR c.[name] <> expected.[column_name]
)
 OR EXISTS (
    SELECT 1 FROM sys.key_constraints kc
    JOIN sys.index_columns ic ON ic.[object_id] = kc.[parent_object_id] AND ic.[index_id] = kc.[unique_index_id]
    WHERE kc.[type] = N'PK'
      AND kc.[parent_object_id] IN (OBJECT_ID(N'dbo.ContainerNewProductSeen'), OBJECT_ID(N'dbo.ContainerNewProductRun'))
      AND ic.[key_ordinal] > 2
)
    THROW 52812, N'Container new product notice primary key signature is incompatible.', 1;
""";
}
