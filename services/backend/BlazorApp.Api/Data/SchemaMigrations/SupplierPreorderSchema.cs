namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 供应商节日预订单（迁移 20261023.001-supplier-preorder）：一次新建 10 张表，不修改任何现有表、不改任何现有数据。
/// SupplierPreorderForm / FormItem / FormStore / Orderer / OrdererStore / AuthLog / StoreOrder / StoreOrderLine /
/// ImportProfile / SourceFile。实体在 SupplierPreorderModels.cs，不进 SqlSugarContext 的自动建表清单；
/// 生产须显式执行 --schema=migrate。迁移前接口会因表不存在报错，但没有任何现有页面依赖这些表。错误号区间 54000–54002。
/// </summary>
internal static class SupplierPreorderSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.SupplierPreorderForm', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[SupplierPreorderForm]
        (
            [FormGuid] nvarchar(50) NOT NULL,
            [LocalSupplierCode] nvarchar(50) NOT NULL,
            [SupplierName] nvarchar(200) NOT NULL,
            [HolidayType] nvarchar(30) NOT NULL,
            [SeasonYear] int NOT NULL,
            [Title] nvarchar(200) NOT NULL,
            [QuoteNo] nvarchar(100) NULL,
            [OfferDate] datetime2 NULL,
            [DeadlineAtUtc] datetime2 NULL,
            [ContainerType] nvarchar(50) NULL,
            [ContainerCapacityCbm] decimal(10,3) NULL,
            [PriceTerms] nvarchar(200) NULL,
            [HistoryFrom] datetime2 NULL,
            [HistoryTo] datetime2 NULL,
            [Status] nvarchar(20) NOT NULL,
            [ColumnMappingJson] nvarchar(max) NULL,
            [SourceFileGuid] nvarchar(50) NULL,
            [ExportQtyUnit] nvarchar(20) NOT NULL,
            [ItemCount] int NOT NULL,
            [Notes] nvarchar(1000) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            [UpdatedBy] nvarchar(100) NULL,
            [IsDeleted] bit NOT NULL,
            CONSTRAINT [PK_SupplierPreorderForm] PRIMARY KEY CLUSTERED ([FormGuid])
        );
        CREATE INDEX [IX_SupplierPreorderForm_Supplier] ON [dbo].[SupplierPreorderForm] ([LocalSupplierCode], [HolidayType], [SeasonYear]);
    END;

    IF OBJECT_ID(N'dbo.SupplierPreorderFormItem', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[SupplierPreorderFormItem]
        (
            [ItemGuid] nvarchar(50) NOT NULL,
            [FormGuid] nvarchar(50) NOT NULL,
            [Seq] int NOT NULL,
            [SourceRowNo] int NOT NULL,
            [SortOrder] int NOT NULL,
            [SupplierItemCode] nvarchar(100) NOT NULL,
            [Barcode] nvarchar(50) NULL,
            [Name] nvarchar(300) NOT NULL,
            [Unit] nvarchar(30) NULL,
            [CartonQty] int NOT NULL,
            [CartonCbm] decimal(10,4) NULL,
            [Price] decimal(18,4) NOT NULL,
            [Category] nvarchar(100) NULL,
            [PrevYearItemCode] nvarchar(100) NULL,
            [ImageSourceUrl] nvarchar(500) NULL,
            [Notes] nvarchar(500) NULL,
            [MatchedProductCode] nvarchar(100) NULL,
            [MatchMethod] nvarchar(20) NOT NULL,
            [MatchNote] nvarchar(300) NULL,
            [RefProductCode] nvarchar(100) NULL,
            [RefSource] nvarchar(20) NULL,
            [RefScore] decimal(5,3) NULL,
            [RefSetBy] nvarchar(100) NULL,
            [RefSetAtUtc] datetime2 NULL,
            [IsActive] bit NOT NULL,
            CONSTRAINT [PK_SupplierPreorderFormItem] PRIMARY KEY CLUSTERED ([ItemGuid])
        );
        CREATE INDEX [IX_SupplierPreorderFormItem_Form] ON [dbo].[SupplierPreorderFormItem] ([FormGuid], [SortOrder]);
    END;

    IF OBJECT_ID(N'dbo.SupplierPreorderFormStore', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[SupplierPreorderFormStore]
        (
            [FormStoreGuid] nvarchar(50) NOT NULL,
            [FormGuid] nvarchar(50) NOT NULL,
            [StoreGuid] nvarchar(100) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [StoreName] nvarchar(200) NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_SupplierPreorderFormStore] PRIMARY KEY CLUSTERED ([FormStoreGuid])
        );
        CREATE UNIQUE INDEX [UX_SupplierPreorderFormStore_FormStore] ON [dbo].[SupplierPreorderFormStore] ([FormGuid], [StoreGuid]);
    END;

    IF OBJECT_ID(N'dbo.SupplierPreorderOrderer', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[SupplierPreorderOrderer]
        (
            [UserGuid] nvarchar(100) NOT NULL,
            [CanViewOtherStores] bit NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            [UpdatedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_SupplierPreorderOrderer] PRIMARY KEY CLUSTERED ([UserGuid])
        );
    END;

    IF OBJECT_ID(N'dbo.SupplierPreorderOrdererStore', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[SupplierPreorderOrdererStore]
        (
            [OrdererStoreGuid] nvarchar(50) NOT NULL,
            [UserGuid] nvarchar(100) NOT NULL,
            [StoreGuid] nvarchar(100) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [StoreName] nvarchar(200) NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_SupplierPreorderOrdererStore] PRIMARY KEY CLUSTERED ([OrdererStoreGuid])
        );
        CREATE UNIQUE INDEX [UX_SupplierPreorderOrdererStore_UserStore] ON [dbo].[SupplierPreorderOrdererStore] ([UserGuid], [StoreGuid]);
    END;

    IF OBJECT_ID(N'dbo.SupplierPreorderAuthLog', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[SupplierPreorderAuthLog]
        (
            [LogGuid] nvarchar(50) NOT NULL,
            [Scope] nvarchar(20) NOT NULL,
            [TargetUserGuid] nvarchar(100) NULL,
            [TargetUserName] nvarchar(200) NULL,
            [FormGuid] nvarchar(50) NULL,
            [Action] nvarchar(50) NOT NULL,
            [Detail] nvarchar(1000) NULL,
            [OperatorUserGuid] nvarchar(100) NULL,
            [OperatorName] nvarchar(100) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_SupplierPreorderAuthLog] PRIMARY KEY CLUSTERED ([LogGuid])
        );
        CREATE INDEX [IX_SupplierPreorderAuthLog_Created] ON [dbo].[SupplierPreorderAuthLog] ([CreatedAtUtc]);
    END;

    IF OBJECT_ID(N'dbo.SupplierPreorderStoreOrder', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[SupplierPreorderStoreOrder]
        (
            [OrderGuid] nvarchar(50) NOT NULL,
            [FormGuid] nvarchar(50) NOT NULL,
            [StoreGuid] nvarchar(100) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [StoreName] nvarchar(200) NOT NULL,
            [Status] nvarchar(20) NOT NULL,
            [SubmittedAtUtc] datetime2 NULL,
            [SubmittedBy] nvarchar(100) NULL,
            [LastEditedAtUtc] datetime2 NULL,
            [LastEditedBy] nvarchar(100) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_SupplierPreorderStoreOrder] PRIMARY KEY CLUSTERED ([OrderGuid])
        );
        CREATE UNIQUE INDEX [UX_SupplierPreorderStoreOrder_FormStore] ON [dbo].[SupplierPreorderStoreOrder] ([FormGuid], [StoreGuid]);
    END;

    IF OBJECT_ID(N'dbo.SupplierPreorderStoreOrderLine', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[SupplierPreorderStoreOrderLine]
        (
            [LineGuid] nvarchar(50) NOT NULL,
            [OrderGuid] nvarchar(50) NOT NULL,
            [FormGuid] nvarchar(50) NOT NULL,
            [ItemGuid] nvarchar(50) NOT NULL,
            [Cartons] int NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            [UpdatedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_SupplierPreorderStoreOrderLine] PRIMARY KEY CLUSTERED ([LineGuid])
        );
        CREATE UNIQUE INDEX [UX_SupplierPreorderStoreOrderLine_OrderItem] ON [dbo].[SupplierPreorderStoreOrderLine] ([OrderGuid], [ItemGuid]);
        CREATE INDEX [IX_SupplierPreorderStoreOrderLine_FormItem] ON [dbo].[SupplierPreorderStoreOrderLine] ([FormGuid], [ItemGuid]) INCLUDE ([OrderGuid], [Cartons]);
    END;

    IF OBJECT_ID(N'dbo.SupplierPreorderImportProfile', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[SupplierPreorderImportProfile]
        (
            [ProfileGuid] nvarchar(50) NOT NULL,
            [LocalSupplierCode] nvarchar(50) NOT NULL,
            [HeaderSignature] nvarchar(64) NOT NULL,
            [MappingJson] nvarchar(max) NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            [UpdatedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_SupplierPreorderImportProfile] PRIMARY KEY CLUSTERED ([ProfileGuid])
        );
        CREATE UNIQUE INDEX [UX_SupplierPreorderImportProfile_SupplierSignature] ON [dbo].[SupplierPreorderImportProfile] ([LocalSupplierCode], [HeaderSignature]);
    END;

    IF OBJECT_ID(N'dbo.SupplierPreorderSourceFile', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[SupplierPreorderSourceFile]
        (
            [FileGuid] nvarchar(50) NOT NULL,
            [FormGuid] nvarchar(50) NULL,
            [FileName] nvarchar(260) NOT NULL,
            [ContentType] nvarchar(100) NOT NULL,
            [SizeBytes] int NOT NULL,
            [Content] varbinary(max) NOT NULL,
            [UploadedAtUtc] datetime2 NOT NULL,
            [UploadedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_SupplierPreorderSourceFile] PRIMARY KEY CLUSTERED ([FileGuid])
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：10 张表都在、每列的类型/长度/可空性与实体一致；nvarchar 的 max_length 按字节（长度×2），max 为 -1。
    internal const string VerifySql = """
DECLARE @missing nvarchar(400) = (
    SELECT STRING_AGG(t.[name], N',')
    FROM (VALUES
      (N'SupplierPreorderForm'), (N'SupplierPreorderFormItem'), (N'SupplierPreorderFormStore'),
      (N'SupplierPreorderOrderer'), (N'SupplierPreorderOrdererStore'), (N'SupplierPreorderAuthLog'),
      (N'SupplierPreorderStoreOrder'), (N'SupplierPreorderStoreOrderLine'),
      (N'SupplierPreorderImportProfile'), (N'SupplierPreorderSourceFile')
    ) t([name])
    WHERE OBJECT_ID(N'dbo.' + t.[name], N'U') IS NULL
);
IF @missing IS NOT NULL
    THROW 54000, N'Supplier preorder tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'SupplierPreorderForm', N'FormGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderForm', N'LocalSupplierCode', N'nvarchar', 100, 0),
      (N'SupplierPreorderForm', N'SupplierName', N'nvarchar', 400, 0),
      (N'SupplierPreorderForm', N'HolidayType', N'nvarchar', 60, 0),
      (N'SupplierPreorderForm', N'SeasonYear', N'int', 0, 0),
      (N'SupplierPreorderForm', N'Title', N'nvarchar', 400, 0),
      (N'SupplierPreorderForm', N'QuoteNo', N'nvarchar', 200, 1),
      (N'SupplierPreorderForm', N'OfferDate', N'datetime2', 0, 1),
      (N'SupplierPreorderForm', N'DeadlineAtUtc', N'datetime2', 0, 1),
      (N'SupplierPreorderForm', N'ContainerType', N'nvarchar', 100, 1),
      (N'SupplierPreorderForm', N'ContainerCapacityCbm', N'decimal', 0, 1),
      (N'SupplierPreorderForm', N'PriceTerms', N'nvarchar', 400, 1),
      (N'SupplierPreorderForm', N'HistoryFrom', N'datetime2', 0, 1),
      (N'SupplierPreorderForm', N'HistoryTo', N'datetime2', 0, 1),
      (N'SupplierPreorderForm', N'Status', N'nvarchar', 40, 0),
      (N'SupplierPreorderForm', N'ColumnMappingJson', N'nvarchar', -1, 1),
      (N'SupplierPreorderForm', N'SourceFileGuid', N'nvarchar', 100, 1),
      (N'SupplierPreorderForm', N'ExportQtyUnit', N'nvarchar', 40, 0),
      (N'SupplierPreorderForm', N'ItemCount', N'int', 0, 0),
      (N'SupplierPreorderForm', N'Notes', N'nvarchar', 2000, 1),
      (N'SupplierPreorderForm', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'SupplierPreorderForm', N'CreatedBy', N'nvarchar', 200, 1),
      (N'SupplierPreorderForm', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'SupplierPreorderForm', N'UpdatedBy', N'nvarchar', 200, 1),
      (N'SupplierPreorderForm', N'IsDeleted', N'bit', 0, 0),
      (N'SupplierPreorderFormItem', N'ItemGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderFormItem', N'FormGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderFormItem', N'Seq', N'int', 0, 0),
      (N'SupplierPreorderFormItem', N'SourceRowNo', N'int', 0, 0),
      (N'SupplierPreorderFormItem', N'SortOrder', N'int', 0, 0),
      (N'SupplierPreorderFormItem', N'SupplierItemCode', N'nvarchar', 200, 0),
      (N'SupplierPreorderFormItem', N'Barcode', N'nvarchar', 100, 1),
      (N'SupplierPreorderFormItem', N'Name', N'nvarchar', 600, 0),
      (N'SupplierPreorderFormItem', N'Unit', N'nvarchar', 60, 1),
      (N'SupplierPreorderFormItem', N'CartonQty', N'int', 0, 0),
      (N'SupplierPreorderFormItem', N'CartonCbm', N'decimal', 0, 1),
      (N'SupplierPreorderFormItem', N'Price', N'decimal', 0, 0),
      (N'SupplierPreorderFormItem', N'Category', N'nvarchar', 200, 1),
      (N'SupplierPreorderFormItem', N'PrevYearItemCode', N'nvarchar', 200, 1),
      (N'SupplierPreorderFormItem', N'ImageSourceUrl', N'nvarchar', 1000, 1),
      (N'SupplierPreorderFormItem', N'Notes', N'nvarchar', 1000, 1),
      (N'SupplierPreorderFormItem', N'MatchedProductCode', N'nvarchar', 200, 1),
      (N'SupplierPreorderFormItem', N'MatchMethod', N'nvarchar', 40, 0),
      (N'SupplierPreorderFormItem', N'MatchNote', N'nvarchar', 600, 1),
      (N'SupplierPreorderFormItem', N'RefProductCode', N'nvarchar', 200, 1),
      (N'SupplierPreorderFormItem', N'RefSource', N'nvarchar', 40, 1),
      (N'SupplierPreorderFormItem', N'RefScore', N'decimal', 0, 1),
      (N'SupplierPreorderFormItem', N'RefSetBy', N'nvarchar', 200, 1),
      (N'SupplierPreorderFormItem', N'RefSetAtUtc', N'datetime2', 0, 1),
      (N'SupplierPreorderFormItem', N'IsActive', N'bit', 0, 0),
      (N'SupplierPreorderFormStore', N'FormStoreGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderFormStore', N'FormGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderFormStore', N'StoreGuid', N'nvarchar', 200, 0),
      (N'SupplierPreorderFormStore', N'StoreCode', N'nvarchar', 100, 0),
      (N'SupplierPreorderFormStore', N'StoreName', N'nvarchar', 400, 0),
      (N'SupplierPreorderFormStore', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'SupplierPreorderFormStore', N'CreatedBy', N'nvarchar', 200, 1),
      (N'SupplierPreorderOrderer', N'UserGuid', N'nvarchar', 200, 0),
      (N'SupplierPreorderOrderer', N'CanViewOtherStores', N'bit', 0, 0),
      (N'SupplierPreorderOrderer', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'SupplierPreorderOrderer', N'CreatedBy', N'nvarchar', 200, 1),
      (N'SupplierPreorderOrderer', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'SupplierPreorderOrderer', N'UpdatedBy', N'nvarchar', 200, 1),
      (N'SupplierPreorderOrdererStore', N'OrdererStoreGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderOrdererStore', N'UserGuid', N'nvarchar', 200, 0),
      (N'SupplierPreorderOrdererStore', N'StoreGuid', N'nvarchar', 200, 0),
      (N'SupplierPreorderOrdererStore', N'StoreCode', N'nvarchar', 100, 0),
      (N'SupplierPreorderOrdererStore', N'StoreName', N'nvarchar', 400, 0),
      (N'SupplierPreorderOrdererStore', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'SupplierPreorderOrdererStore', N'CreatedBy', N'nvarchar', 200, 1),
      (N'SupplierPreorderAuthLog', N'LogGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderAuthLog', N'Scope', N'nvarchar', 40, 0),
      (N'SupplierPreorderAuthLog', N'TargetUserGuid', N'nvarchar', 200, 1),
      (N'SupplierPreorderAuthLog', N'TargetUserName', N'nvarchar', 400, 1),
      (N'SupplierPreorderAuthLog', N'FormGuid', N'nvarchar', 100, 1),
      (N'SupplierPreorderAuthLog', N'Action', N'nvarchar', 100, 0),
      (N'SupplierPreorderAuthLog', N'Detail', N'nvarchar', 2000, 1),
      (N'SupplierPreorderAuthLog', N'OperatorUserGuid', N'nvarchar', 200, 1),
      (N'SupplierPreorderAuthLog', N'OperatorName', N'nvarchar', 200, 1),
      (N'SupplierPreorderAuthLog', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'SupplierPreorderStoreOrder', N'OrderGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderStoreOrder', N'FormGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderStoreOrder', N'StoreGuid', N'nvarchar', 200, 0),
      (N'SupplierPreorderStoreOrder', N'StoreCode', N'nvarchar', 100, 0),
      (N'SupplierPreorderStoreOrder', N'StoreName', N'nvarchar', 400, 0),
      (N'SupplierPreorderStoreOrder', N'Status', N'nvarchar', 40, 0),
      (N'SupplierPreorderStoreOrder', N'SubmittedAtUtc', N'datetime2', 0, 1),
      (N'SupplierPreorderStoreOrder', N'SubmittedBy', N'nvarchar', 200, 1),
      (N'SupplierPreorderStoreOrder', N'LastEditedAtUtc', N'datetime2', 0, 1),
      (N'SupplierPreorderStoreOrder', N'LastEditedBy', N'nvarchar', 200, 1),
      (N'SupplierPreorderStoreOrder', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'SupplierPreorderStoreOrderLine', N'LineGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderStoreOrderLine', N'OrderGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderStoreOrderLine', N'FormGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderStoreOrderLine', N'ItemGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderStoreOrderLine', N'Cartons', N'int', 0, 0),
      (N'SupplierPreorderStoreOrderLine', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'SupplierPreorderStoreOrderLine', N'UpdatedBy', N'nvarchar', 200, 1),
      (N'SupplierPreorderImportProfile', N'ProfileGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderImportProfile', N'LocalSupplierCode', N'nvarchar', 100, 0),
      (N'SupplierPreorderImportProfile', N'HeaderSignature', N'nvarchar', 128, 0),
      (N'SupplierPreorderImportProfile', N'MappingJson', N'nvarchar', -1, 0),
      (N'SupplierPreorderImportProfile', N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'SupplierPreorderImportProfile', N'UpdatedBy', N'nvarchar', 200, 1),
      (N'SupplierPreorderSourceFile', N'FileGuid', N'nvarchar', 100, 0),
      (N'SupplierPreorderSourceFile', N'FormGuid', N'nvarchar', 100, 1),
      (N'SupplierPreorderSourceFile', N'FileName', N'nvarchar', 520, 0),
      (N'SupplierPreorderSourceFile', N'ContentType', N'nvarchar', 200, 0),
      (N'SupplierPreorderSourceFile', N'SizeBytes', N'int', 0, 0),
      (N'SupplierPreorderSourceFile', N'Content', N'varbinary', -1, 0),
      (N'SupplierPreorderSourceFile', N'UploadedAtUtc', N'datetime2', 0, 0),
      (N'SupplierPreorderSourceFile', N'UploadedBy', N'nvarchar', 200, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.' + expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54001, N'Supplier preorder column signature is incompatible.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'SupplierPreorderFormStore', N'UX_SupplierPreorderFormStore_FormStore'),
      (N'SupplierPreorderOrdererStore', N'UX_SupplierPreorderOrdererStore_UserStore'),
      (N'SupplierPreorderStoreOrder', N'UX_SupplierPreorderStoreOrder_FormStore'),
      (N'SupplierPreorderStoreOrderLine', N'UX_SupplierPreorderStoreOrderLine_OrderItem'),
      (N'SupplierPreorderImportProfile', N'UX_SupplierPreorderImportProfile_SupplierSignature')
    ) expected([table_name], [index_name])
    LEFT JOIN sys.indexes i ON i.[object_id] = OBJECT_ID(N'dbo.' + expected.[table_name]) AND i.[name] = expected.[index_name] AND i.[is_unique] = 1
    WHERE i.[index_id] IS NULL
)
    THROW 54002, N'Supplier preorder unique index signature is incompatible.', 1;
""";
}
