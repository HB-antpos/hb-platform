namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 商品陈列图（Planogram）六张新表（迁移 20261104.001-planogram；方案 docs/plans/2026-11-04-planogram-shelf-display-designer.md 第 3、8 节）：
/// - dbo.PlanogramShelfModel 货架型号（洞洞板孔阵）、dbo.PlanogramHookModel 挂钩型号（双钩 / 单钩，带价签臂）；
/// - dbo.ProductDisplaySpec 商品陈列参数（按 ProductCode 一对一，包装宽高厚、吊孔偏移、单件重、按挂钩型号的挂数覆盖）；
/// - dbo.Planogram 方案头（不绑定分店，含左右 / 上下间距）、dbo.PlanogramItem 明细（一钩一行）、dbo.PlanogramVersion 发布快照。
/// 批 1 只读写前三张，六张一次建好，避免批 2 / 批 3 再出迁移。
/// 新权限码 Planogram.View / Planogram.Edit / Planogram.Manage 幂等写入 HbwebSysPermissions，不授予任何角色（生产手动授权）。
/// 只新建表并插入三行权限，不触碰任何既有表；回退 = 先回滚主 API 镜像，再 DROP 这六张表、删除三条权限行并移除迁移账本行。
/// 实体不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// 错误号自成一段 54440–54449：54440 表缺失、54441 列签名、54442 主键、54443 唯一索引、54444 普通索引。
/// 所有索引均为非过滤索引，手工用 sqlcmd 执行无需特殊开关。生产须显式执行 --schema=migrate，且要先于新版主 API 上线。
/// </summary>
internal static class PlanogramSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    -- 货架型号：外形尺寸用 int（mm），孔距 / 孔径 / 首孔边距用 decimal(6,2)（允许 25.4 这类英寸换算值）。
    -- HoleCols / HoleRows 可由后端按板面尺寸自动换算，也允许手改；停用（IsActive=0）后已有方案可看不可新建。
    IF OBJECT_ID(N'dbo.PlanogramShelfModel', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PlanogramShelfModel]
        (
            [Guid] nvarchar(50) NOT NULL,
            [Code] nvarchar(50) NOT NULL,
            [Name] nvarchar(100) NOT NULL,
            [SideType] tinyint NOT NULL CONSTRAINT [DF_PlanogramShelfModel_SideType] DEFAULT (1),
            [Color] nvarchar(50) NULL,
            [Source] nvarchar(100) NULL,
            [BayWidthMm] int NOT NULL,
            [TotalHeightMm] int NOT NULL,
            [BaseDepthMm] int NOT NULL,
            [BaseTopHeightMm] int NOT NULL,
            [UprightWidthMm] int NOT NULL,
            [HolePitchXMm] decimal(6,2) NOT NULL,
            [HolePitchYMm] decimal(6,2) NOT NULL,
            [HoleDiameterMm] decimal(6,2) NOT NULL,
            [FirstHoleLeftMm] decimal(6,2) NOT NULL,
            [FirstHoleTopMm] decimal(6,2) NOT NULL,
            [HoleCols] int NOT NULL,
            [HoleRows] int NOT NULL,
            [RowNumberingFrom] tinyint NOT NULL CONSTRAINT [DF_PlanogramShelfModel_RowNumberingFrom] DEFAULT (1),
            [IsActive] bit NOT NULL CONSTRAINT [DF_PlanogramShelfModel_IsActive] DEFAULT (1),
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(100) NULL,
            [IsDeleted] bit NOT NULL CONSTRAINT [DF_PlanogramShelfModel_IsDeleted] DEFAULT (0),
            CONSTRAINT [PK_PlanogramShelfModel] PRIMARY KEY CLUSTERED ([Guid])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PlanogramShelfModel') AND [name] = N'UX_PlanogramShelfModel_Code')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_PlanogramShelfModel_Code]
            ON [dbo].[PlanogramShelfModel] ([Code]);

    -- 挂钩型号：臂长 / 钩高 / 线径 / 臂端预留 / 价签架尺寸都用 decimal(6,2)（挂钩常按英寸标注）。
    -- TagBottomAboveArmMm 为价签架底边高于挂货臂的距离，默认 10；HoleSpanX / HoleSpanY 为占孔数（双钩 2 × 1）。
    IF OBJECT_ID(N'dbo.PlanogramHookModel', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PlanogramHookModel]
        (
            [Guid] nvarchar(50) NOT NULL,
            [Code] nvarchar(50) NOT NULL,
            [Name] nvarchar(100) NOT NULL,
            [Color] nvarchar(50) NULL,
            [ProngCount] tinyint NOT NULL CONSTRAINT [DF_PlanogramHookModel_ProngCount] DEFAULT (2),
            [HasPriceTagArm] bit NOT NULL CONSTRAINT [DF_PlanogramHookModel_HasPriceTagArm] DEFAULT (1),
            [ArmLengthMm] decimal(6,2) NOT NULL,
            [DropMm] decimal(6,2) NOT NULL,
            [WireDiameterMm] decimal(6,2) NOT NULL,
            [TipReserveMm] decimal(6,2) NOT NULL CONSTRAINT [DF_PlanogramHookModel_TipReserveMm] DEFAULT (0),
            [TagWidthMm] decimal(6,2) NOT NULL CONSTRAINT [DF_PlanogramHookModel_TagWidthMm] DEFAULT (0),
            [TagHeightMm] decimal(6,2) NOT NULL CONSTRAINT [DF_PlanogramHookModel_TagHeightMm] DEFAULT (0),
            [TagBottomAboveArmMm] decimal(6,2) NOT NULL CONSTRAINT [DF_PlanogramHookModel_TagBottomAboveArmMm] DEFAULT (10),
            [HoleSpanX] int NOT NULL CONSTRAINT [DF_PlanogramHookModel_HoleSpanX] DEFAULT (2),
            [HoleSpanY] int NOT NULL CONSTRAINT [DF_PlanogramHookModel_HoleSpanY] DEFAULT (1),
            [LoadKg] decimal(5,2) NULL,
            [IsActive] bit NOT NULL CONSTRAINT [DF_PlanogramHookModel_IsActive] DEFAULT (1),
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(100) NULL,
            [IsDeleted] bit NOT NULL CONSTRAINT [DF_PlanogramHookModel_IsDeleted] DEFAULT (0),
            CONSTRAINT [PK_PlanogramHookModel] PRIMARY KEY CLUSTERED ([Guid])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PlanogramHookModel') AND [name] = N'UX_PlanogramHookModel_Code')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_PlanogramHookModel_Code]
            ON [dbo].[PlanogramHookModel] ([Code]);

    -- 商品陈列参数：以 ProductCode（业务键）为主键，与 Product 一对一，不动商品主档。
    -- HookQtyOverridesJson 形如 {"<hookGuid>": 16}，按挂钩型号手填覆盖建议挂数；DefaultHookGuid 为默认挂钩。
    IF OBJECT_ID(N'dbo.ProductDisplaySpec', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[ProductDisplaySpec]
        (
            [ProductCode] nvarchar(50) NOT NULL,
            [WidthMm] int NOT NULL,
            [HeightMm] int NOT NULL,
            [DepthMm] int NOT NULL,
            [HangHoleOffsetMm] int NOT NULL CONSTRAINT [DF_ProductDisplaySpec_HangHoleOffsetMm] DEFAULT (10),
            [HangHoleType] tinyint NOT NULL CONSTRAINT [DF_ProductDisplaySpec_HangHoleType] DEFAULT (1),
            [UnitWeightG] int NULL,
            [DefaultHookGuid] nvarchar(50) NULL,
            [HookQtyOverridesJson] nvarchar(1000) NULL,
            [Source] tinyint NOT NULL CONSTRAINT [DF_ProductDisplaySpec_Source] DEFAULT (1),
            [Notes] nvarchar(500) NULL,
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(100) NULL,
            [IsDeleted] bit NOT NULL CONSTRAINT [DF_ProductDisplaySpec_IsDeleted] DEFAULT (0),
            CONSTRAINT [PK_ProductDisplaySpec] PRIMARY KEY CLUSTERED ([ProductCode])
        );
    END;

    -- 方案头：不绑定分店（无 StoreCode）。Status：0 草稿 / 1 已发布 / 2 已归档；Version 为当前编辑版本号，每次发布 +1。
    -- GapXMm / GapYMm 为商品左右 / 上下间距，默认各 10 mm。
    IF OBJECT_ID(N'dbo.Planogram', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[Planogram]
        (
            [Guid] nvarchar(50) NOT NULL,
            [Name] nvarchar(100) NOT NULL,
            [Notes] nvarchar(500) NULL,
            [Tags] nvarchar(200) NULL,
            [GapXMm] int NOT NULL CONSTRAINT [DF_Planogram_GapXMm] DEFAULT (10),
            [GapYMm] int NOT NULL CONSTRAINT [DF_Planogram_GapYMm] DEFAULT (10),
            [ShelfModelGuid] nvarchar(50) NOT NULL,
            [BayCount] int NOT NULL CONSTRAINT [DF_Planogram_BayCount] DEFAULT (1),
            [Status] tinyint NOT NULL CONSTRAINT [DF_Planogram_Status] DEFAULT (0),
            [Version] int NOT NULL CONSTRAINT [DF_Planogram_Version] DEFAULT (1),
            [PublishedVersion] int NULL,
            [PublishedAt] datetime2 NULL,
            [PublishedBy] nvarchar(100) NULL,
            [ConflictCount] int NOT NULL CONSTRAINT [DF_Planogram_ConflictCount] DEFAULT (0),
            [LastValidatedAt] datetime2 NULL,
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(100) NULL,
            [IsDeleted] bit NOT NULL CONSTRAINT [DF_Planogram_IsDeleted] DEFAULT (0),
            CONSTRAINT [PK_Planogram] PRIMARY KEY CLUSTERED ([Guid])
        );
    END;
    -- 按货架型号统计被多少个方案引用（删除 / 停用型号前的保护）。
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.Planogram') AND [name] = N'IX_Planogram_ShelfModel')
        CREATE NONCLUSTERED INDEX [IX_Planogram_ShelfModel]
            ON [dbo].[Planogram] ([ShelfModelGuid]);

    -- 明细（一钩一行）：Face 单面恒 0；ItemType 1 挂钩商品 / 2 层板商品（二期）；SpecSnapshotJson 为保存时商品宽高厚快照。
    IF OBJECT_ID(N'dbo.PlanogramItem', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PlanogramItem]
        (
            [Guid] nvarchar(50) NOT NULL,
            [PlanogramGuid] nvarchar(50) NOT NULL,
            [BayIndex] int NOT NULL,
            [Face] tinyint NOT NULL CONSTRAINT [DF_PlanogramItem_Face] DEFAULT (0),
            [ItemType] tinyint NOT NULL CONSTRAINT [DF_PlanogramItem_ItemType] DEFAULT (1),
            [ProductCode] nvarchar(50) NOT NULL,
            [HookModelGuid] nvarchar(50) NOT NULL,
            [HoleCol] int NOT NULL,
            [HoleRow] int NOT NULL,
            [PerHookQty] int NULL,
            [SpecSnapshotJson] nvarchar(500) NULL,
            [SortOrder] int NOT NULL CONSTRAINT [DF_PlanogramItem_SortOrder] DEFAULT (0),
            CONSTRAINT [PK_PlanogramItem] PRIMARY KEY CLUSTERED ([Guid])
        );
    END;
    -- 设计器按方案取全部明细；商品 / 挂钩型号索引服务于「被多少个方案引用」的计数。
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PlanogramItem') AND [name] = N'IX_PlanogramItem_Plan')
        CREATE NONCLUSTERED INDEX [IX_PlanogramItem_Plan]
            ON [dbo].[PlanogramItem] ([PlanogramGuid]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PlanogramItem') AND [name] = N'IX_PlanogramItem_Product')
        CREATE NONCLUSTERED INDEX [IX_PlanogramItem_Product]
            ON [dbo].[PlanogramItem] ([ProductCode]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PlanogramItem') AND [name] = N'IX_PlanogramItem_Hook')
        CREATE NONCLUSTERED INDEX [IX_PlanogramItem_Hook]
            ON [dbo].[PlanogramItem] ([HookModelGuid]);

    -- 发布快照：LayoutJson 含头、明细、所用货架 / 挂钩型号与商品参数的完整快照，历史版本 PDF 可按当时数据重出。
    IF OBJECT_ID(N'dbo.PlanogramVersion', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PlanogramVersion]
        (
            [Guid] nvarchar(50) NOT NULL,
            [PlanogramGuid] nvarchar(50) NOT NULL,
            [Version] int NOT NULL,
            [LayoutJson] nvarchar(max) NOT NULL,
            [PublishedBy] nvarchar(100) NULL,
            [PublishedAt] datetime2 NOT NULL,
            CONSTRAINT [PK_PlanogramVersion] PRIMARY KEY CLUSTERED ([Guid])
        );
    END;
    -- 同一方案的同一版本号只能有一份快照（发布并发时的最后防线）。
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PlanogramVersion') AND [name] = N'UX_PlanogramVersion_Plan_Version')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_PlanogramVersion_Plan_Version]
            ON [dbo].[PlanogramVersion] ([PlanogramGuid], [Version]);

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库；默认不授予任何角色（生产手动授权）。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), [wanted].[Code], [wanted].[Name], N'商品陈列图', [wanted].[Description],
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        FROM (VALUES
            (N'Planogram.View', N'查看商品陈列图',
             N'Web 页面 /pos-admin/planograms - 查看陈列方案、商品陈列参数与货架 / 挂钩型号'),
            (N'Planogram.Edit', N'编辑商品陈列图',
             N'Web 页面 /pos-admin/planograms - 新建与编辑陈列方案、维护商品陈列参数（包装尺寸、吊孔偏移、每钩挂数）'),
            (N'Planogram.Manage', N'管理商品陈列图',
             N'Web 页面 /pos-admin/planograms - 维护货架与挂钩型号、清除商品陈列参数、发布与归档陈列方案')
        ) AS [wanted]([Code], [Name], [Description])
        WHERE NOT EXISTS (
            SELECT 1 FROM [dbo].[HbwebSysPermissions] AS [existing] WHERE [existing].[Code] = [wanted].[Code]
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：六张表的列签名（类型 / 长度 / 精度 / 可空性）、主键与索引必须精确匹配；不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length：nvarchar 为字节数（字符数 * 2），nvarchar(max) 为 -1；表里 max_length=0 表示该列不校验长度；
    // decimal 用 precision / scale 校验（precision=0 表示不校验）。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.PlanogramShelfModel', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PlanogramHookModel', N'U') IS NULL
   OR OBJECT_ID(N'dbo.ProductDisplaySpec', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Planogram', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PlanogramItem', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PlanogramVersion', N'U') IS NULL
    THROW 54440, N'Planogram tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.PlanogramShelfModel', N'Guid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.PlanogramShelfModel', N'Code', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.PlanogramShelfModel', N'Name', N'nvarchar', 200, 0, 0, 0),
      (N'dbo.PlanogramShelfModel', N'SideType', N'tinyint', 0, 0, 0, 0),
      (N'dbo.PlanogramShelfModel', N'Color', N'nvarchar', 100, 0, 0, 1),
      (N'dbo.PlanogramShelfModel', N'Source', N'nvarchar', 200, 0, 0, 1),
      (N'dbo.PlanogramShelfModel', N'BayWidthMm', N'int', 0, 0, 0, 0),
      (N'dbo.PlanogramShelfModel', N'TotalHeightMm', N'int', 0, 0, 0, 0),
      (N'dbo.PlanogramShelfModel', N'BaseDepthMm', N'int', 0, 0, 0, 0),
      (N'dbo.PlanogramShelfModel', N'BaseTopHeightMm', N'int', 0, 0, 0, 0),
      (N'dbo.PlanogramShelfModel', N'UprightWidthMm', N'int', 0, 0, 0, 0),
      (N'dbo.PlanogramShelfModel', N'HolePitchXMm', N'decimal', 0, 6, 2, 0),
      (N'dbo.PlanogramShelfModel', N'HolePitchYMm', N'decimal', 0, 6, 2, 0),
      (N'dbo.PlanogramShelfModel', N'HoleDiameterMm', N'decimal', 0, 6, 2, 0),
      (N'dbo.PlanogramShelfModel', N'FirstHoleLeftMm', N'decimal', 0, 6, 2, 0),
      (N'dbo.PlanogramShelfModel', N'FirstHoleTopMm', N'decimal', 0, 6, 2, 0),
      (N'dbo.PlanogramShelfModel', N'HoleCols', N'int', 0, 0, 0, 0),
      (N'dbo.PlanogramShelfModel', N'HoleRows', N'int', 0, 0, 0, 0),
      (N'dbo.PlanogramShelfModel', N'RowNumberingFrom', N'tinyint', 0, 0, 0, 0),
      (N'dbo.PlanogramShelfModel', N'IsActive', N'bit', 0, 0, 0, 0),
      (N'dbo.PlanogramShelfModel', N'CreatedAt', N'datetime2', 0, 0, 0, 0),
      (N'dbo.PlanogramShelfModel', N'CreatedBy', N'nvarchar', 200, 0, 0, 1),
      (N'dbo.PlanogramShelfModel', N'UpdatedAt', N'datetime2', 0, 0, 0, 1),
      (N'dbo.PlanogramShelfModel', N'UpdatedBy', N'nvarchar', 200, 0, 0, 1),
      (N'dbo.PlanogramShelfModel', N'IsDeleted', N'bit', 0, 0, 0, 0),
      (N'dbo.PlanogramHookModel', N'Guid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.PlanogramHookModel', N'Code', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.PlanogramHookModel', N'Name', N'nvarchar', 200, 0, 0, 0),
      (N'dbo.PlanogramHookModel', N'Color', N'nvarchar', 100, 0, 0, 1),
      (N'dbo.PlanogramHookModel', N'ProngCount', N'tinyint', 0, 0, 0, 0),
      (N'dbo.PlanogramHookModel', N'HasPriceTagArm', N'bit', 0, 0, 0, 0),
      (N'dbo.PlanogramHookModel', N'ArmLengthMm', N'decimal', 0, 6, 2, 0),
      (N'dbo.PlanogramHookModel', N'DropMm', N'decimal', 0, 6, 2, 0),
      (N'dbo.PlanogramHookModel', N'WireDiameterMm', N'decimal', 0, 6, 2, 0),
      (N'dbo.PlanogramHookModel', N'TipReserveMm', N'decimal', 0, 6, 2, 0),
      (N'dbo.PlanogramHookModel', N'TagWidthMm', N'decimal', 0, 6, 2, 0),
      (N'dbo.PlanogramHookModel', N'TagHeightMm', N'decimal', 0, 6, 2, 0),
      (N'dbo.PlanogramHookModel', N'TagBottomAboveArmMm', N'decimal', 0, 6, 2, 0),
      (N'dbo.PlanogramHookModel', N'HoleSpanX', N'int', 0, 0, 0, 0),
      (N'dbo.PlanogramHookModel', N'HoleSpanY', N'int', 0, 0, 0, 0),
      (N'dbo.PlanogramHookModel', N'LoadKg', N'decimal', 0, 5, 2, 1),
      (N'dbo.PlanogramHookModel', N'IsActive', N'bit', 0, 0, 0, 0),
      (N'dbo.PlanogramHookModel', N'CreatedAt', N'datetime2', 0, 0, 0, 0),
      (N'dbo.PlanogramHookModel', N'CreatedBy', N'nvarchar', 200, 0, 0, 1),
      (N'dbo.PlanogramHookModel', N'UpdatedAt', N'datetime2', 0, 0, 0, 1),
      (N'dbo.PlanogramHookModel', N'UpdatedBy', N'nvarchar', 200, 0, 0, 1),
      (N'dbo.PlanogramHookModel', N'IsDeleted', N'bit', 0, 0, 0, 0),
      (N'dbo.ProductDisplaySpec', N'ProductCode', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.ProductDisplaySpec', N'WidthMm', N'int', 0, 0, 0, 0),
      (N'dbo.ProductDisplaySpec', N'HeightMm', N'int', 0, 0, 0, 0),
      (N'dbo.ProductDisplaySpec', N'DepthMm', N'int', 0, 0, 0, 0),
      (N'dbo.ProductDisplaySpec', N'HangHoleOffsetMm', N'int', 0, 0, 0, 0),
      (N'dbo.ProductDisplaySpec', N'HangHoleType', N'tinyint', 0, 0, 0, 0),
      (N'dbo.ProductDisplaySpec', N'UnitWeightG', N'int', 0, 0, 0, 1),
      (N'dbo.ProductDisplaySpec', N'DefaultHookGuid', N'nvarchar', 100, 0, 0, 1),
      (N'dbo.ProductDisplaySpec', N'HookQtyOverridesJson', N'nvarchar', 2000, 0, 0, 1),
      (N'dbo.ProductDisplaySpec', N'Source', N'tinyint', 0, 0, 0, 0),
      (N'dbo.ProductDisplaySpec', N'Notes', N'nvarchar', 1000, 0, 0, 1),
      (N'dbo.ProductDisplaySpec', N'CreatedAt', N'datetime2', 0, 0, 0, 0),
      (N'dbo.ProductDisplaySpec', N'CreatedBy', N'nvarchar', 200, 0, 0, 1),
      (N'dbo.ProductDisplaySpec', N'UpdatedAt', N'datetime2', 0, 0, 0, 1),
      (N'dbo.ProductDisplaySpec', N'UpdatedBy', N'nvarchar', 200, 0, 0, 1),
      (N'dbo.ProductDisplaySpec', N'IsDeleted', N'bit', 0, 0, 0, 0),
      (N'dbo.Planogram', N'Guid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.Planogram', N'Name', N'nvarchar', 200, 0, 0, 0),
      (N'dbo.Planogram', N'Notes', N'nvarchar', 1000, 0, 0, 1),
      (N'dbo.Planogram', N'Tags', N'nvarchar', 400, 0, 0, 1),
      (N'dbo.Planogram', N'GapXMm', N'int', 0, 0, 0, 0),
      (N'dbo.Planogram', N'GapYMm', N'int', 0, 0, 0, 0),
      (N'dbo.Planogram', N'ShelfModelGuid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.Planogram', N'BayCount', N'int', 0, 0, 0, 0),
      (N'dbo.Planogram', N'Status', N'tinyint', 0, 0, 0, 0),
      (N'dbo.Planogram', N'Version', N'int', 0, 0, 0, 0),
      (N'dbo.Planogram', N'PublishedVersion', N'int', 0, 0, 0, 1),
      (N'dbo.Planogram', N'PublishedAt', N'datetime2', 0, 0, 0, 1),
      (N'dbo.Planogram', N'PublishedBy', N'nvarchar', 200, 0, 0, 1),
      (N'dbo.Planogram', N'ConflictCount', N'int', 0, 0, 0, 0),
      (N'dbo.Planogram', N'LastValidatedAt', N'datetime2', 0, 0, 0, 1),
      (N'dbo.Planogram', N'CreatedAt', N'datetime2', 0, 0, 0, 0),
      (N'dbo.Planogram', N'CreatedBy', N'nvarchar', 200, 0, 0, 1),
      (N'dbo.Planogram', N'UpdatedAt', N'datetime2', 0, 0, 0, 1),
      (N'dbo.Planogram', N'UpdatedBy', N'nvarchar', 200, 0, 0, 1),
      (N'dbo.Planogram', N'IsDeleted', N'bit', 0, 0, 0, 0),
      (N'dbo.PlanogramItem', N'Guid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.PlanogramItem', N'PlanogramGuid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.PlanogramItem', N'BayIndex', N'int', 0, 0, 0, 0),
      (N'dbo.PlanogramItem', N'Face', N'tinyint', 0, 0, 0, 0),
      (N'dbo.PlanogramItem', N'ItemType', N'tinyint', 0, 0, 0, 0),
      (N'dbo.PlanogramItem', N'ProductCode', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.PlanogramItem', N'HookModelGuid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.PlanogramItem', N'HoleCol', N'int', 0, 0, 0, 0),
      (N'dbo.PlanogramItem', N'HoleRow', N'int', 0, 0, 0, 0),
      (N'dbo.PlanogramItem', N'PerHookQty', N'int', 0, 0, 0, 1),
      (N'dbo.PlanogramItem', N'SpecSnapshotJson', N'nvarchar', 1000, 0, 0, 1),
      (N'dbo.PlanogramItem', N'SortOrder', N'int', 0, 0, 0, 0),
      (N'dbo.PlanogramVersion', N'Guid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.PlanogramVersion', N'PlanogramGuid', N'nvarchar', 100, 0, 0, 0),
      (N'dbo.PlanogramVersion', N'Version', N'int', 0, 0, 0, 0),
      (N'dbo.PlanogramVersion', N'LayoutJson', N'nvarchar', -1, 0, 0, 0),
      (N'dbo.PlanogramVersion', N'PublishedBy', N'nvarchar', 200, 0, 0, 1),
      (N'dbo.PlanogramVersion', N'PublishedAt', N'datetime2', 0, 0, 0, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [precision], [scale], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR (expected.[precision] <> 0 AND (c.[precision] <> expected.[precision] OR c.[scale] <> expected.[scale]))
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54441, N'Planogram column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PlanogramShelfModel') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PlanogramHookModel') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.ProductDisplaySpec') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.Planogram') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PlanogramItem') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PlanogramVersion') AND [type] = N'PK')
    THROW 54442, N'Planogram primary keys are missing.', 1;

-- 唯一索引：必须存在、唯一、非过滤，键列个数正确且第一键列正确（型号编码唯一、同方案同版本号只有一份快照）。
IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.PlanogramShelfModel', N'UX_PlanogramShelfModel_Code', 1, N'Code'),
      (N'dbo.PlanogramHookModel', N'UX_PlanogramHookModel_Code', 1, N'Code'),
      (N'dbo.PlanogramVersion', N'UX_PlanogramVersion_Plan_Version', 2, N'PlanogramGuid')
    ) expected([table_name], [index_name], [key_count], [first_key])
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
                        AND c.[name] = expected.[first_key] AND ic.[is_descending_key] = 0)
)
    THROW 54443, N'Planogram unique index is missing or incompatible.', 1;

-- 普通索引：必须存在、非唯一、非过滤，单键列且列名正确（服务于方案明细读取与型号 / 商品被引用计数）。
IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.Planogram', N'IX_Planogram_ShelfModel', N'ShelfModelGuid'),
      (N'dbo.PlanogramItem', N'IX_PlanogramItem_Plan', N'PlanogramGuid'),
      (N'dbo.PlanogramItem', N'IX_PlanogramItem_Product', N'ProductCode'),
      (N'dbo.PlanogramItem', N'IX_PlanogramItem_Hook', N'HookModelGuid')
    ) expected([table_name], [index_name], [key_column])
    LEFT JOIN sys.indexes i
      ON i.[object_id] = OBJECT_ID(expected.[table_name]) AND i.[name] = expected.[index_name]
    WHERE i.[index_id] IS NULL
       OR i.[is_unique] = 1
       OR i.[has_filter] = 1
       OR (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) <> 1
       OR NOT EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                      WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1
                        AND c.[name] = expected.[key_column] AND ic.[is_descending_key] = 0)
)
    THROW 54444, N'Planogram index is missing or incompatible.', 1;
""";
}
