namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 节日贺卡剩余填报改为「分店 + 年份 + 节日 + 供应商」整组提交：dbo.SeasonalCardRemainingSubmission 加三列
/// LocalSupplierCode nvarchar(64) NULL（对齐 LocalSupplier.LocalSupplierCode）、SupplierName nvarchar(128) NULL（名称快照）、
/// BatchGuid nvarchar(50) NULL（同一次整组提交共用），并建按年份/节日/分店查询的索引；
/// 同时把后台统计用的权限码 SeasonalCards.Remaining.ViewAllStores 幂等写入 HbwebSysPermissions（启动不跑权限种子）。
/// 贺卡表随基线 20260827.001 登记，新列只能走独立版本号迁移；只加可空列，批量填报前的历史行保持 NULL。
/// 生产须显式执行 --schema=migrate，且要先于新版 HBweb 上线（实体会查这些列）。
/// 错误号区间 52400–52401。
/// </summary>
internal static class SeasonalCardSupplierBatchSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.SeasonalCardRemainingSubmission', N'U') IS NULL
    THROW 52400, N'SeasonalCardRemainingSubmission table is missing; apply the HBweb baseline first.', 1;

BEGIN TRANSACTION;
BEGIN TRY
    -- 加可空列只改元数据；COL_LENGTH 判断保证重复执行是空操作。
    IF COL_LENGTH(N'dbo.SeasonalCardRemainingSubmission', N'LocalSupplierCode') IS NULL
        ALTER TABLE [dbo].[SeasonalCardRemainingSubmission] ADD [LocalSupplierCode] nvarchar(64) NULL;
    IF COL_LENGTH(N'dbo.SeasonalCardRemainingSubmission', N'SupplierName') IS NULL
        ALTER TABLE [dbo].[SeasonalCardRemainingSubmission] ADD [SupplierName] nvarchar(128) NULL;
    IF COL_LENGTH(N'dbo.SeasonalCardRemainingSubmission', N'BatchGuid') IS NULL
        ALTER TABLE [dbo].[SeasonalCardRemainingSubmission] ADD [BatchGuid] nvarchar(50) NULL;

    -- 索引引用本批次刚加的列，用动态 SQL 推迟到列存在后再编译，避免整批编译时报列名无效。
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.SeasonalCardRemainingSubmission')
          AND [name] = N'IX_SeasonalCardRemainingSubmission_Year_Type_Store'
    )
        EXEC(N'CREATE NONCLUSTERED INDEX [IX_SeasonalCardRemainingSubmission_Year_Type_Store]
            ON [dbo].[SeasonalCardRemainingSubmission] ([SeasonYear], [CardType], [StoreCode], [SubmittedAt])
            INCLUDE ([LocalSupplierCode], [BatchGuid], [CatalogGuid], [PriceOption], [RemainingQuantity], [UnitPrice], [IsDeleted]);');

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库，否则角色管理里无法授予（保存会被静默丢弃）。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), [wanted].[Code], [wanted].[Name], N'季节卡片', [wanted].[Description],
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        FROM (VALUES
            (N'SeasonalCards.Remaining.ViewAllStores', N'查看全部分店季节卡填报统计',
             N'Web 后台「节日贺卡填报统计」- 查看全部分店的贺卡剩余填报汇总、未填报分店与单店提交历史')
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

    // 只读门禁：三列必须存在且为可空 nvarchar（max_length 按字节计为声明长度的 2 倍），索引存在。
    // 已有同名但类型/长度/可空性不符的列只能通过显式迁移修复，运行时绝不自动改写。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.SeasonalCardRemainingSubmission', N'U') IS NULL
    THROW 52400, N'SeasonalCardRemainingSubmission table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'LocalSupplierCode', N'nvarchar', 128, 1),
      (N'SupplierName', N'nvarchar', 256, 1),
      (N'BatchGuid', N'nvarchar', 100, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(N'dbo.SeasonalCardRemainingSubmission') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.SeasonalCardRemainingSubmission')
      AND [name] = N'IX_SeasonalCardRemainingSubmission_Year_Type_Store'
)
    THROW 52401, N'Seasonal card supplier batch column or index signature is incompatible.', 1;
""";
}
