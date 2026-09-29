namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 供应商分类三张表：分类树、采集观察记录、商品归属。
/// 只新建表与索引，不触碰 Product 或任何既有列；回退只需删除三表并移除迁移账本行。
/// </summary>
internal static class LocalSupplierCategorySchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.LocalSupplierCategory', N'U') IS NULL
    BEGIN
        -- 每个供应商一棵分类树；ExternalKey 是站点稳定标识（归一化后的分类页路径）。
        CREATE TABLE [dbo].[LocalSupplierCategory]
        (
            [CategoryGUID] nvarchar(50) NOT NULL,
            [LocalSupplierCode] nvarchar(64) NOT NULL,
            [ParentGUID] nvarchar(50) NULL,
            [CategoryName] nvarchar(200) NOT NULL,
            [ExternalKey] nvarchar(400) NOT NULL,
            [FullPath] nvarchar(1000) NOT NULL,
            [Depth] int NOT NULL,
            [SourceUrl] nvarchar(1000) NULL,
            [IsPromotional] bit NOT NULL,
            [PromotionalSource] nvarchar(16) NOT NULL,
            [SortOrder] int NULL,
            [IsActive] bit NOT NULL,
            [FirstSeenAt] datetime2 NOT NULL,
            [LastSeenAt] datetime2 NOT NULL,
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(100) NULL,
            [IsDeleted] bit NOT NULL CONSTRAINT [DF_LocalSupplierCategory_IsDeleted] DEFAULT (0),
            CONSTRAINT [PK_LocalSupplierCategory] PRIMARY KEY CLUSTERED ([CategoryGUID])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.LocalSupplierCategory')
          AND [name] = N'UX_LocalSupplierCategory_Supplier_ExternalKey'
    )
        CREATE UNIQUE NONCLUSTERED INDEX [UX_LocalSupplierCategory_Supplier_ExternalKey]
            ON [dbo].[LocalSupplierCategory] ([LocalSupplierCode], [ExternalKey]);

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.LocalSupplierCategory')
          AND [name] = N'IX_LocalSupplierCategory_Supplier_Parent'
    )
        CREATE NONCLUSTERED INDEX [IX_LocalSupplierCategory_Supplier_Parent]
            ON [dbo].[LocalSupplierCategory] ([LocalSupplierCode], [ParentGUID])
            INCLUDE ([CategoryName], [Depth], [IsPromotional], [IsActive], [IsDeleted]);

    IF OBJECT_ID(N'dbo.LocalSupplierCategoryCapture', N'U') IS NULL
    BEGIN
        -- 复合主键即业务唯一键：同一供应商货号在同一分类只保留一行，重复采集累加计数。
        CREATE TABLE [dbo].[LocalSupplierCategoryCapture]
        (
            [LocalSupplierCode] nvarchar(64) NOT NULL,
            [ItemNumber] nvarchar(50) NOT NULL,
            [CategoryGUID] nvarchar(50) NOT NULL,
            [FirstSeenAt] datetime2 NOT NULL,
            [LastSeenAt] datetime2 NOT NULL,
            [SeenCount] int NOT NULL,
            [LastSourceUrl] nvarchar(1000) NULL,
            [LastMode] nvarchar(16) NOT NULL,
            [CapturedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_LocalSupplierCategoryCapture]
                PRIMARY KEY CLUSTERED ([LocalSupplierCode], [ItemNumber], [CategoryGUID])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.LocalSupplierCategoryCapture')
          AND [name] = N'IX_LocalSupplierCategoryCapture_Category'
    )
        CREATE NONCLUSTERED INDEX [IX_LocalSupplierCategoryCapture_Category]
            ON [dbo].[LocalSupplierCategoryCapture] ([CategoryGUID]);

    IF OBJECT_ID(N'dbo.LocalSupplierCategoryProductAssignment', N'U') IS NULL
    BEGIN
        -- 商品与供应商分类一对一；独立成表，避免 HQ 商品同步清表或整行覆盖 Product 时丢失。
        CREATE TABLE [dbo].[LocalSupplierCategoryProductAssignment]
        (
            [ProductCode] nvarchar(50) NOT NULL,
            [LocalSupplierCode] nvarchar(64) NOT NULL,
            [CategoryGUID] nvarchar(50) NOT NULL,
            [Source] nvarchar(16) NOT NULL,
            [ItemNumberKey] nvarchar(50) NULL,
            [AssignedAt] datetime2 NOT NULL,
            [AssignedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_LocalSupplierCategoryProductAssignment] PRIMARY KEY CLUSTERED ([ProductCode])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.LocalSupplierCategoryProductAssignment')
          AND [name] = N'IX_LocalSupplierCategoryProductAssignment_Supplier_Category'
    )
        CREATE NONCLUSTERED INDEX [IX_LocalSupplierCategoryProductAssignment_Supplier_Category]
            ON [dbo].[LocalSupplierCategoryProductAssignment] ([LocalSupplierCode], [CategoryGUID])
            INCLUDE ([Source]);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列签名、主键与唯一索引；不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.LocalSupplierCategory', N'U') IS NULL
 OR OBJECT_ID(N'dbo.LocalSupplierCategoryCapture', N'U') IS NULL
 OR OBJECT_ID(N'dbo.LocalSupplierCategoryProductAssignment', N'U') IS NULL
    THROW 51930, N'Local supplier category tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'LocalSupplierCategory', N'CategoryGUID', N'nvarchar', 100, 0),
      (N'LocalSupplierCategory', N'LocalSupplierCode', N'nvarchar', 128, 0),
      (N'LocalSupplierCategory', N'ParentGUID', N'nvarchar', 100, 1),
      (N'LocalSupplierCategory', N'CategoryName', N'nvarchar', 400, 0),
      (N'LocalSupplierCategory', N'ExternalKey', N'nvarchar', 800, 0),
      (N'LocalSupplierCategory', N'FullPath', N'nvarchar', 2000, 0),
      (N'LocalSupplierCategory', N'Depth', N'int', 0, 0),
      (N'LocalSupplierCategory', N'IsPromotional', N'bit', 0, 0),
      (N'LocalSupplierCategory', N'PromotionalSource', N'nvarchar', 32, 0),
      (N'LocalSupplierCategory', N'IsActive', N'bit', 0, 0),
      (N'LocalSupplierCategory', N'LastSeenAt', N'datetime2', 0, 0),
      (N'LocalSupplierCategory', N'IsDeleted', N'bit', 0, 0),
      (N'LocalSupplierCategoryCapture', N'LocalSupplierCode', N'nvarchar', 128, 0),
      (N'LocalSupplierCategoryCapture', N'ItemNumber', N'nvarchar', 100, 0),
      (N'LocalSupplierCategoryCapture', N'CategoryGUID', N'nvarchar', 100, 0),
      (N'LocalSupplierCategoryCapture', N'SeenCount', N'int', 0, 0),
      (N'LocalSupplierCategoryCapture', N'LastSeenAt', N'datetime2', 0, 0),
      (N'LocalSupplierCategoryCapture', N'LastMode', N'nvarchar', 32, 0),
      (N'LocalSupplierCategoryProductAssignment', N'ProductCode', N'nvarchar', 100, 0),
      (N'LocalSupplierCategoryProductAssignment', N'LocalSupplierCode', N'nvarchar', 128, 0),
      (N'LocalSupplierCategoryProductAssignment', N'CategoryGUID', N'nvarchar', 100, 0),
      (N'LocalSupplierCategoryProductAssignment', N'Source', N'nvarchar', 32, 0),
      (N'LocalSupplierCategoryProductAssignment', N'AssignedAt', N'datetime2', 0, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 51931, N'Local supplier category column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.LocalSupplierCategory') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.LocalSupplierCategoryCapture') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.LocalSupplierCategoryProductAssignment') AND [type] = N'PK')
    THROW 51932, N'Local supplier category primary keys are missing.', 1;

-- 唯一索引必须存在、唯一且键列顺序为 (LocalSupplierCode, ExternalKey)，否则并发采集会产生重复分类。
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.LocalSupplierCategory')
      AND i.[name] = N'UX_LocalSupplierCategory_Supplier_ExternalKey'
      AND i.[is_unique] = 1
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 2
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'LocalSupplierCode')
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 2 AND c.[name] = N'ExternalKey')
)
 OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.LocalSupplierCategoryCapture')
                AND [name] = N'IX_LocalSupplierCategoryCapture_Category')
 OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.LocalSupplierCategoryProductAssignment')
                AND [name] = N'IX_LocalSupplierCategoryProductAssignment_Supplier_Category')
    THROW 51933, N'Local supplier category index signature is incompatible.', 1;
""";
}
