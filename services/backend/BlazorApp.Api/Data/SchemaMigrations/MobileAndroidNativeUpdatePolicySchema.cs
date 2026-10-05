namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// Mobile 安卓原生「最低支持构建号」强制更新策略表。新表走独立版本号迁移，生产须显式执行 --schema=migrate；
/// 实体不进 SqlSugarContext 的自动建表清单，也不放进基线 AppUpdatePolicySchemaMigrator。错误号区间 51890–51892。
/// </summary>
internal static class MobileAndroidNativeUpdatePolicySchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.MobileAndroidNativeUpdatePolicy', N'U') IS NULL
    BEGIN
        -- 全局只有一行（PolicyKey = mobile-android）；审计列类型与 iOS 原生策略表一致。
        CREATE TABLE [dbo].[MobileAndroidNativeUpdatePolicy]
        (
            [Id] uniqueidentifier NOT NULL,
            [PolicyKey] nvarchar(40) NOT NULL,
            [Enabled] bit NOT NULL,
            [MinimumSupportedBuildNumber] int NULL,
            [ReleaseMessage] nvarchar(1000) NULL,
            [PolicyVersion] bigint NOT NULL,
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(max) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(max) NULL,
            [IsDeleted] bit NOT NULL CONSTRAINT [DF_MobileAndroidNativeUpdatePolicy_IsDeleted] DEFAULT(0),
            CONSTRAINT [PK_MobileAndroidNativeUpdatePolicy] PRIMARY KEY CLUSTERED ([Id])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.MobileAndroidNativeUpdatePolicy')
          AND [name] = N'UX_MobileAndroidNativeUpdatePolicy_PolicyKey'
    )
    BEGIN
        -- 未删除行按 PolicyKey 唯一，跨实例并发首次写入由唯一索引兜底。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_MobileAndroidNativeUpdatePolicy_PolicyKey]
            ON [dbo].[MobileAndroidNativeUpdatePolicy] ([PolicyKey])
            WHERE [IsDeleted] = 0;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列名、类型、长度（nvarchar 按字节计，max 为 -1）、可空性，以及主键与唯一过滤索引。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.MobileAndroidNativeUpdatePolicy', N'U') IS NULL
    THROW 51890, N'Mobile Android native update policy table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'Id', N'uniqueidentifier', 0, 0),
      (N'PolicyKey', N'nvarchar', 80, 0),
      (N'Enabled', N'bit', 0, 0),
      (N'MinimumSupportedBuildNumber', N'int', 0, 1),
      (N'ReleaseMessage', N'nvarchar', 2000, 1),
      (N'PolicyVersion', N'bigint', 0, 0),
      (N'CreatedAt', N'datetime2', 0, 0),
      (N'CreatedBy', N'nvarchar', -1, 1),
      (N'UpdatedAt', N'datetime2', 0, 1),
      (N'UpdatedBy', N'nvarchar', -1, 1),
      (N'IsDeleted', N'bit', 0, 0)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.MobileAndroidNativeUpdatePolicy') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 51891, N'Mobile Android native update policy column signature is incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.key_constraints
    WHERE [parent_object_id] = OBJECT_ID(N'dbo.MobileAndroidNativeUpdatePolicy') AND [type] = N'PK'
)
 OR NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    INNER JOIN sys.index_columns ic
        ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1
    INNER JOIN sys.columns c
        ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
    WHERE i.[object_id] = OBJECT_ID(N'dbo.MobileAndroidNativeUpdatePolicy')
      AND i.[name] = N'UX_MobileAndroidNativeUpdatePolicy_PolicyKey'
      AND i.[is_unique] = 1
      AND i.[has_filter] = 1
      AND i.[filter_definition] = N'([IsDeleted]=(0))'
      AND c.[name] = N'PolicyKey'
      AND (
          SELECT COUNT(*) FROM sys.index_columns ic2
          WHERE ic2.[object_id] = i.[object_id] AND ic2.[index_id] = i.[index_id] AND ic2.[key_ordinal] > 0
      ) = 1
 )
    THROW 51892, N'Mobile Android native update policy index signature is incompatible.', 1;
""";
}
