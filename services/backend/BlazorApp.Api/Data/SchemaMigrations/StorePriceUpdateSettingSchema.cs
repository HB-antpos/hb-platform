namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 价格变更任务页面设置表 dbo.StorePriceUpdateSetting（迁移 20261017.003-store-price-update-setting）。
/// 只建一张新表，不修改任何现有表、不改任何现有数据：没有记录＝沿用配置默认值（7 天），行为与迁移前完全一致。
/// 新表走独立版本号迁移，生产须显式执行 --schema=migrate；实体不进 SqlSugarContext 的自动建表清单。
/// 后端读取该表时表不存在会自动回退到配置默认值，所以迁移与后端部署的先后不会导致报错；
/// 但在迁移执行之前，Web「设置」弹窗保存会提示「设置表尚未创建」。错误号区间 53700–53702。
/// </summary>
internal static class StorePriceUpdateSettingSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.StorePriceUpdateSetting', N'U') IS NULL
    BEGIN
        -- 单行表：Id 恒为 1，由页面「设置」保存时写入（不存在则插入，存在则更新）。
        CREATE TABLE [dbo].[StorePriceUpdateSetting]
        (
            [Id] int NOT NULL,
            [OverdueDays] int NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            [UpdatedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_StorePriceUpdateSetting] PRIMARY KEY CLUSTERED ([Id])
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、可空性与主键；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.StorePriceUpdateSetting', N'U') IS NULL
    THROW 53700, N'Store price update setting table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'Id', N'int', 0, 0),
      (N'OverdueDays', N'int', 0, 0),
      (N'UpdatedAtUtc', N'datetime2', 0, 0),
      (N'UpdatedBy', N'nvarchar', 200, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.StorePriceUpdateSetting') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 53701, N'Store price update setting column signature is incompatible.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints k
    INNER JOIN sys.index_columns ic ON ic.[object_id] = k.[parent_object_id] AND ic.[index_id] = k.[unique_index_id]
    INNER JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
    WHERE k.[parent_object_id] = OBJECT_ID(N'dbo.StorePriceUpdateSetting')
      AND k.[type] = N'PK'
      AND c.[name] = N'Id'
)
    THROW 53702, N'Store price update setting primary key signature is incompatible.', 1;
""";
}
