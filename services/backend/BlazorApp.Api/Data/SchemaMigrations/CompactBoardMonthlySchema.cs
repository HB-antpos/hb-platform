namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 独立销售看板按月预聚合：分店×商品×日统计原始供应商编码（只收国内编码族：200 与全部国内供应商编码）。
/// 由 CompactBoardMonthlyProjectionWorker 从日事实按月生成；看板查询整月读本表、零散日期读日事实，
/// 在数据库里算完四栏只回几百行（API↔数据库走公网、约 1–1.5 MB/秒，不能把几十万行拉回内存）。
/// </summary>
internal static class CompactBoardMonthlySchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.CompactBoardMonthlyCell', N'U') IS NULL
    BEGIN
        -- 键全部去空格；国内供应商归属不烘焙，查询时按当前映射与「最近销售日」解析，映射变化不使本表失效。
        CREATE TABLE [dbo].[CompactBoardMonthlyCell]
        (
            [Month] date NOT NULL,
            [BranchCode] nvarchar(50) NOT NULL,
            [ProductCode] nvarchar(50) NOT NULL,
            [RawSupplierCode] nvarchar(50) NOT NULL,
            [Quantity] bigint NOT NULL,
            [Amount] decimal(38,4) NOT NULL,
            [LastDate] date NOT NULL,
            CONSTRAINT [PK_CompactBoardMonthlyCell] PRIMARY KEY CLUSTERED ([Month], [BranchCode], [ProductCode], [RawSupplierCode])
        );
    END;

    IF OBJECT_ID(N'dbo.CompactBoardMonthlyState', N'U') IS NULL
    BEGIN
        -- 月身份与销售明细月投影同一口径（该月日状态行的日期、来源版本、聚合时间）；编码族签名覆盖新增国内供应商。
        CREATE TABLE [dbo].[CompactBoardMonthlyState]
        (
            [Month] date NOT NULL,
            [ProjectionSchemaVersion] int NOT NULL,
            [DayIdentity] varchar(64) NOT NULL,
            [CodeFamilySignature] varchar(64) NOT NULL,
            [CellCount] int NOT NULL,
            [RefreshedAtUtc] datetime2 NOT NULL,
            [RefreshDurationMs] int NOT NULL,
            CONSTRAINT [PK_CompactBoardMonthlyState] PRIMARY KEY CLUSTERED ([Month])
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型和主键；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.CompactBoardMonthlyCell', N'U') IS NULL
 OR OBJECT_ID(N'dbo.CompactBoardMonthlyState', N'U') IS NULL
    THROW 51830, N'Compact board monthly projection tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'CompactBoardMonthlyCell', N'Month', N'date', 0, 0),
      (N'CompactBoardMonthlyCell', N'BranchCode', N'nvarchar', 100, 0),
      (N'CompactBoardMonthlyCell', N'ProductCode', N'nvarchar', 100, 0),
      (N'CompactBoardMonthlyCell', N'RawSupplierCode', N'nvarchar', 100, 0),
      (N'CompactBoardMonthlyCell', N'Quantity', N'bigint', 0, 0),
      (N'CompactBoardMonthlyCell', N'Amount', N'decimal', 0, 0),
      (N'CompactBoardMonthlyCell', N'LastDate', N'date', 0, 0),
      (N'CompactBoardMonthlyState', N'Month', N'date', 0, 0),
      (N'CompactBoardMonthlyState', N'ProjectionSchemaVersion', N'int', 0, 0),
      (N'CompactBoardMonthlyState', N'DayIdentity', N'varchar', 64, 0),
      (N'CompactBoardMonthlyState', N'CodeFamilySignature', N'varchar', 64, 0),
      (N'CompactBoardMonthlyState', N'CellCount', N'int', 0, 0),
      (N'CompactBoardMonthlyState', N'RefreshedAtUtc', N'datetime2', 0, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 51831, N'Compact board monthly projection column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.CompactBoardMonthlyCell') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.CompactBoardMonthlyState') AND [type] = N'PK')
    THROW 51832, N'Compact board monthly projection index signature is incompatible.', 1;
""";
}
