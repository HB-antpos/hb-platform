namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 使用帮助 FAQ 两张表（迁移 20261025.001-help-faq）：分组 HelpFaqGroup、问答 HelpFaqQuestion。
/// 只新建表与索引，不触碰任何既有表；回退只需删除两表并移除迁移账本行。
/// 首次内容由 <see cref="BlazorApp.Api.Services.HelpFaq.HelpFaqSeedImporter"/> 在表为空时灌入（见 SchemaMigrationRuntime）。
/// 错误号自成一段 54200–54209：54200 表缺失、54201 列签名、54202 主键、54203 索引。
/// </summary>
internal static class HelpFaqSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.HelpFaqGroup', N'U') IS NULL
    BEGIN
        -- 分组：Tab 为 pos / mobile，Code 是分组 id（如 sale、people），同一 Tab 内唯一。
        CREATE TABLE [dbo].[HelpFaqGroup]
        (
            [GroupGUID] nvarchar(50) NOT NULL,
            [Tab] nvarchar(16) NOT NULL,
            [Code] nvarchar(64) NOT NULL,
            [TitleZh] nvarchar(200) NOT NULL,
            [TitleEn] nvarchar(200) NOT NULL,
            [SortOrder] int NOT NULL,
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(100) NULL,
            [IsDeleted] bit NOT NULL CONSTRAINT [DF_HelpFaqGroup_IsDeleted] DEFAULT (0),
            CONSTRAINT [PK_HelpFaqGroup] PRIMARY KEY CLUSTERED ([GroupGUID])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.HelpFaqGroup')
          AND [name] = N'UX_HelpFaqGroup_Tab_Code'
    )
        CREATE UNIQUE NONCLUSTERED INDEX [UX_HelpFaqGroup_Tab_Code]
            ON [dbo].[HelpFaqGroup] ([Tab], [Code]);

    IF OBJECT_ID(N'dbo.HelpFaqQuestion', N'U') IS NULL
    BEGIN
        -- 问答：Code 即页面锚点 #faq-<code>，全局唯一（含软删除行，防止已分享链接被复用）。
        -- 步骤 / 提示 / 规则 / 相关 / 权限都是整体编辑的有序列表，存 JSON 文本，不拆子表。
        -- Status：Draft 从未发布 / Published；DraftJson 为已发布题再编辑时的整题草稿快照（P2 使用，P1 只建列）。
        -- Version 仅用于乐观并发。
        CREATE TABLE [dbo].[HelpFaqQuestion]
        (
            [QuestionGUID] nvarchar(50) NOT NULL,
            [GroupGUID] nvarchar(50) NOT NULL,
            [Code] nvarchar(96) NOT NULL,
            [TitleZh] nvarchar(400) NOT NULL,
            [TitleEn] nvarchar(400) NOT NULL,
            [WhereZh] nvarchar(400) NOT NULL,
            [WhereEn] nvarchar(400) NOT NULL,
            [StepsZh] nvarchar(max) NOT NULL,
            [StepsEn] nvarchar(max) NOT NULL,
            [TipsZh] nvarchar(max) NULL,
            [TipsEn] nvarchar(max) NULL,
            [RulesZh] nvarchar(max) NULL,
            [RulesEn] nvarchar(max) NULL,
            [RelatedZh] nvarchar(max) NULL,
            [RelatedEn] nvarchar(max) NULL,
            [Permissions] nvarchar(max) NULL,
            [SortOrder] int NOT NULL,
            [Status] nvarchar(16) NOT NULL CONSTRAINT [DF_HelpFaqQuestion_Status] DEFAULT (N'Draft'),
            [DraftJson] nvarchar(max) NULL,
            [PublishedAt] datetime2 NULL,
            [Version] int NOT NULL CONSTRAINT [DF_HelpFaqQuestion_Version] DEFAULT (1),
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(100) NULL,
            [IsDeleted] bit NOT NULL CONSTRAINT [DF_HelpFaqQuestion_IsDeleted] DEFAULT (0),
            CONSTRAINT [PK_HelpFaqQuestion] PRIMARY KEY CLUSTERED ([QuestionGUID])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.HelpFaqQuestion')
          AND [name] = N'UX_HelpFaqQuestion_Code'
    )
        CREATE UNIQUE NONCLUSTERED INDEX [UX_HelpFaqQuestion_Code]
            ON [dbo].[HelpFaqQuestion] ([Code]);

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.HelpFaqQuestion')
          AND [name] = N'IX_HelpFaqQuestion_Group_Sort'
    )
        CREATE NONCLUSTERED INDEX [IX_HelpFaqQuestion_Group_Sort]
            ON [dbo].[HelpFaqQuestion] ([GroupGUID], [SortOrder])
            INCLUDE ([Status], [IsDeleted]);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列签名、主键与两个唯一索引；不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length 对 nvarchar 是字节数（字符数 * 2），nvarchar(max) 为 -1。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.HelpFaqGroup', N'U') IS NULL
 OR OBJECT_ID(N'dbo.HelpFaqQuestion', N'U') IS NULL
    THROW 54200, N'Help FAQ tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'HelpFaqGroup', N'GroupGUID', N'nvarchar', 100, 0),
      (N'HelpFaqGroup', N'Tab', N'nvarchar', 32, 0),
      (N'HelpFaqGroup', N'Code', N'nvarchar', 128, 0),
      (N'HelpFaqGroup', N'TitleZh', N'nvarchar', 400, 0),
      (N'HelpFaqGroup', N'TitleEn', N'nvarchar', 400, 0),
      (N'HelpFaqGroup', N'SortOrder', N'int', 0, 0),
      (N'HelpFaqGroup', N'CreatedAt', N'datetime2', 0, 0),
      (N'HelpFaqGroup', N'UpdatedAt', N'datetime2', 0, 1),
      (N'HelpFaqGroup', N'IsDeleted', N'bit', 0, 0),
      (N'HelpFaqQuestion', N'QuestionGUID', N'nvarchar', 100, 0),
      (N'HelpFaqQuestion', N'GroupGUID', N'nvarchar', 100, 0),
      (N'HelpFaqQuestion', N'Code', N'nvarchar', 192, 0),
      (N'HelpFaqQuestion', N'TitleZh', N'nvarchar', 800, 0),
      (N'HelpFaqQuestion', N'TitleEn', N'nvarchar', 800, 0),
      (N'HelpFaqQuestion', N'WhereZh', N'nvarchar', 800, 0),
      (N'HelpFaqQuestion', N'WhereEn', N'nvarchar', 800, 0),
      (N'HelpFaqQuestion', N'StepsZh', N'nvarchar', -1, 0),
      (N'HelpFaqQuestion', N'StepsEn', N'nvarchar', -1, 0),
      (N'HelpFaqQuestion', N'TipsZh', N'nvarchar', -1, 1),
      (N'HelpFaqQuestion', N'TipsEn', N'nvarchar', -1, 1),
      (N'HelpFaqQuestion', N'RulesZh', N'nvarchar', -1, 1),
      (N'HelpFaqQuestion', N'RulesEn', N'nvarchar', -1, 1),
      (N'HelpFaqQuestion', N'RelatedZh', N'nvarchar', -1, 1),
      (N'HelpFaqQuestion', N'RelatedEn', N'nvarchar', -1, 1),
      (N'HelpFaqQuestion', N'Permissions', N'nvarchar', -1, 1),
      (N'HelpFaqQuestion', N'SortOrder', N'int', 0, 0),
      (N'HelpFaqQuestion', N'Status', N'nvarchar', 32, 0),
      (N'HelpFaqQuestion', N'DraftJson', N'nvarchar', -1, 1),
      (N'HelpFaqQuestion', N'PublishedAt', N'datetime2', 0, 1),
      (N'HelpFaqQuestion', N'Version', N'int', 0, 0),
      (N'HelpFaqQuestion', N'CreatedAt', N'datetime2', 0, 0),
      (N'HelpFaqQuestion', N'UpdatedAt', N'datetime2', 0, 1),
      (N'HelpFaqQuestion', N'IsDeleted', N'bit', 0, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54201, N'Help FAQ column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.HelpFaqGroup') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.HelpFaqQuestion') AND [type] = N'PK')
    THROW 54202, N'Help FAQ primary keys are missing.', 1;

-- 两个唯一索引必须存在、唯一且键列顺序正确：
--   UX_HelpFaqGroup_Tab_Code = (Tab, Code)；UX_HelpFaqQuestion_Code = (Code)，Code 全局唯一是 #faq-<code> 锚点不冲突的保证。
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.HelpFaqGroup')
      AND i.[name] = N'UX_HelpFaqGroup_Tab_Code'
      AND i.[is_unique] = 1
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 2
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'Tab')
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 2 AND c.[name] = N'Code')
)
 OR NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.HelpFaqQuestion')
      AND i.[name] = N'UX_HelpFaqQuestion_Code'
      AND i.[is_unique] = 1
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'Code')
)
 OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.HelpFaqQuestion')
                AND [name] = N'IX_HelpFaqQuestion_Group_Sort')
    THROW 54203, N'Help FAQ index signature is incompatible.', 1;
""";
}
