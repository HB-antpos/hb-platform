namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 分店级「特殊商品分类」两张新表（迁移 20261111.002-store-special-product-category）：
/// - dbo.StoreSpecialProductCategory：分类（Guid 主键；同店名称唯一；同店按 SortOrder 排序）；
/// - dbo.StoreSpecialProductCategoryItem：商品归属（主键 StoreCode + ProductCode，一个商品在一家店只属于一个分类）。
/// 只新建两张表，不触碰任何既有表、不写任何数据、不新增权限码（复用 PosTerminal.SpecialProducts.*）。
/// 回退 = 先回滚主 API / POS API 镜像，再 DROP 这两张表并移除迁移账本行；旧版程序不读这两张表，回滚镜像不受影响。
/// 实体不进 SqlSugarContext 的 tableTypes（启动不得代建表，否则 VerifySql 的精确签名会与 CodeFirst 的列类型冲突）。
/// 错误号自成一段 54470–54479：54470 表缺失、54471 列签名、54472 主键、54473 唯一索引、54474 普通索引。
/// 所有索引均为非过滤索引，手工用 sqlcmd 执行无需 -I。生产须显式执行 --schema=migrate，且要先于新版主 API 与 POS API 上线。
/// </summary>
internal static class StoreSpecialProductCategorySchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    -- 分类：同店名称唯一（SQL Server 默认排序规则不区分大小写，正好满足「同店不重名」）；
    -- (StoreCode, SortOrder) 索引服务于分类栏按店读取并排序。IsDeleted 恒为 0（分类是硬删除），只为复用 BaseEntity 审计列。
    IF OBJECT_ID(N'dbo.StoreSpecialProductCategory', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[StoreSpecialProductCategory]
        (
            [Guid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [Name] nvarchar(50) NOT NULL,
            [Color] nvarchar(16) NULL,
            [SortOrder] int NOT NULL CONSTRAINT [DF_StoreSpecialProductCategory_SortOrder] DEFAULT (0),
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(100) NULL,
            [IsDeleted] bit NOT NULL CONSTRAINT [DF_StoreSpecialProductCategory_IsDeleted] DEFAULT (0),
            CONSTRAINT [PK_StoreSpecialProductCategory] PRIMARY KEY CLUSTERED ([Guid])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.StoreSpecialProductCategory') AND [name] = N'UX_StoreSpecialProductCategory_Store_Name')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_StoreSpecialProductCategory_Store_Name]
            ON [dbo].[StoreSpecialProductCategory] ([StoreCode], [Name]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.StoreSpecialProductCategory') AND [name] = N'IX_StoreSpecialProductCategory_Store_Sort')
        CREATE NONCLUSTERED INDEX [IX_StoreSpecialProductCategory_Store_Sort]
            ON [dbo].[StoreSpecialProductCategory] ([StoreCode], [SortOrder]);

    -- 归属：主键 (StoreCode, ProductCode) 保证一个商品在一家店只属于一个分类；
    -- 取消特殊商品标记时不删行，读取方须再与 StoreRetailPrice.IsSpecialProduct 求交集。
    -- (StoreCode, CategoryGuid, SortOrder) 索引服务于分类内按顺序读取与分类数量统计。
    IF OBJECT_ID(N'dbo.StoreSpecialProductCategoryItem', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[StoreSpecialProductCategoryItem]
        (
            [StoreCode] nvarchar(50) NOT NULL,
            [ProductCode] nvarchar(50) NOT NULL,
            [CategoryGuid] nvarchar(50) NOT NULL,
            [SortOrder] int NOT NULL CONSTRAINT [DF_StoreSpecialProductCategoryItem_SortOrder] DEFAULT (0),
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(100) NULL,
            [IsDeleted] bit NOT NULL CONSTRAINT [DF_StoreSpecialProductCategoryItem_IsDeleted] DEFAULT (0),
            CONSTRAINT [PK_StoreSpecialProductCategoryItem] PRIMARY KEY CLUSTERED ([StoreCode], [ProductCode])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.StoreSpecialProductCategoryItem') AND [name] = N'IX_StoreSpecialProductCategoryItem_Store_Category_Sort')
        CREATE NONCLUSTERED INDEX [IX_StoreSpecialProductCategoryItem_Store_Category_Sort]
            ON [dbo].[StoreSpecialProductCategoryItem] ([StoreCode], [CategoryGuid], [SortOrder]);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：两张表的列签名（类型 / 长度 / 可空性）、主键与索引必须精确匹配；不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length：nvarchar 为字节数（字符数 * 2）；0 表示该列不校验长度。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.StoreSpecialProductCategory', N'U') IS NULL
   OR OBJECT_ID(N'dbo.StoreSpecialProductCategoryItem', N'U') IS NULL
    THROW 54470, N'Store special product category tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.StoreSpecialProductCategory', N'Guid', N'nvarchar', 100, 0),
      (N'dbo.StoreSpecialProductCategory', N'StoreCode', N'nvarchar', 100, 0),
      (N'dbo.StoreSpecialProductCategory', N'Name', N'nvarchar', 100, 0),
      (N'dbo.StoreSpecialProductCategory', N'Color', N'nvarchar', 32, 1),
      (N'dbo.StoreSpecialProductCategory', N'SortOrder', N'int', 0, 0),
      (N'dbo.StoreSpecialProductCategory', N'CreatedAt', N'datetime2', 0, 0),
      (N'dbo.StoreSpecialProductCategory', N'CreatedBy', N'nvarchar', 200, 1),
      (N'dbo.StoreSpecialProductCategory', N'UpdatedAt', N'datetime2', 0, 1),
      (N'dbo.StoreSpecialProductCategory', N'UpdatedBy', N'nvarchar', 200, 1),
      (N'dbo.StoreSpecialProductCategory', N'IsDeleted', N'bit', 0, 0),
      (N'dbo.StoreSpecialProductCategoryItem', N'StoreCode', N'nvarchar', 100, 0),
      (N'dbo.StoreSpecialProductCategoryItem', N'ProductCode', N'nvarchar', 100, 0),
      (N'dbo.StoreSpecialProductCategoryItem', N'CategoryGuid', N'nvarchar', 100, 0),
      (N'dbo.StoreSpecialProductCategoryItem', N'SortOrder', N'int', 0, 0),
      (N'dbo.StoreSpecialProductCategoryItem', N'CreatedAt', N'datetime2', 0, 0),
      (N'dbo.StoreSpecialProductCategoryItem', N'CreatedBy', N'nvarchar', 200, 1),
      (N'dbo.StoreSpecialProductCategoryItem', N'UpdatedAt', N'datetime2', 0, 1),
      (N'dbo.StoreSpecialProductCategoryItem', N'UpdatedBy', N'nvarchar', 200, 1),
      (N'dbo.StoreSpecialProductCategoryItem', N'IsDeleted', N'bit', 0, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54471, N'Store special product category column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.StoreSpecialProductCategory') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.StoreSpecialProductCategoryItem') AND [type] = N'PK')
    THROW 54472, N'Store special product category primary keys are missing.', 1;

-- 归属表主键必须恰好是 (StoreCode, ProductCode) 两列：一个商品在一家店只能有一个归属。
IF (SELECT COUNT(*) FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
    WHERE i.[object_id] = OBJECT_ID(N'dbo.StoreSpecialProductCategoryItem') AND i.[is_primary_key] = 1) <> 2
 OR NOT EXISTS (SELECT 1 FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
    JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
    WHERE i.[object_id] = OBJECT_ID(N'dbo.StoreSpecialProductCategoryItem') AND i.[is_primary_key] = 1
      AND ic.[key_ordinal] = 1 AND c.[name] = N'StoreCode')
 OR NOT EXISTS (SELECT 1 FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
    JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
    WHERE i.[object_id] = OBJECT_ID(N'dbo.StoreSpecialProductCategoryItem') AND i.[is_primary_key] = 1
      AND ic.[key_ordinal] = 2 AND c.[name] = N'ProductCode')
    THROW 54472, N'Store special product category item primary key is incompatible.', 1;

-- 唯一索引：必须存在、唯一、非过滤，键列个数与前两键列顺序正确（同店名称不重复）。
IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.StoreSpecialProductCategory', N'UX_StoreSpecialProductCategory_Store_Name', 2, N'StoreCode', N'Name')
    ) expected([table_name], [index_name], [key_count], [key1], [key2])
    LEFT JOIN sys.indexes i
      ON i.[object_id] = OBJECT_ID(expected.[table_name]) AND i.[name] = expected.[index_name]
    WHERE i.[index_id] IS NULL
       OR i.[is_unique] = 0
       OR i.[has_filter] = 1
       OR (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0)
          <> expected.[key_count]
       OR NOT EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                      WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1
                        AND c.[name] = expected.[key1] AND ic.[is_descending_key] = 0)
       OR NOT EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                      WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 2
                        AND c.[name] = expected.[key2] AND ic.[is_descending_key] = 0)
)
    THROW 54473, N'Store special product category unique index is missing or incompatible.', 1;

-- 普通索引：必须存在、非唯一、非过滤，键列个数与各键列顺序正确（服务于分类栏读取与分类内排序读取）。
IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.StoreSpecialProductCategory', N'IX_StoreSpecialProductCategory_Store_Sort', 2, N'StoreCode', N'SortOrder', N''),
      (N'dbo.StoreSpecialProductCategoryItem', N'IX_StoreSpecialProductCategoryItem_Store_Category_Sort', 3, N'StoreCode', N'CategoryGuid', N'SortOrder')
    ) expected([table_name], [index_name], [key_count], [key1], [key2], [key3])
    LEFT JOIN sys.indexes i
      ON i.[object_id] = OBJECT_ID(expected.[table_name]) AND i.[name] = expected.[index_name]
    WHERE i.[index_id] IS NULL
       OR i.[is_unique] = 1
       OR i.[has_filter] = 1
       OR (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0)
          <> expected.[key_count]
       OR NOT EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                      WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1
                        AND c.[name] = expected.[key1] AND ic.[is_descending_key] = 0)
       OR NOT EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                      WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 2
                        AND c.[name] = expected.[key2] AND ic.[is_descending_key] = 0)
       OR (expected.[key3] <> N'' AND NOT EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                      WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 3
                        AND c.[name] = expected.[key3] AND ic.[is_descending_key] = 0))
)
    THROW 54474, N'Store special product category index is missing or incompatible.', 1;
""";
}
