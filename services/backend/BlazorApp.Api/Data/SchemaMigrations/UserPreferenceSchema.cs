namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 员工个人界面偏好表 dbo.UserPreference（迁移 20261029.004-user-preference）。
/// 主键 (UserGUID, PreferenceKey)：每个员工每项偏好一行，PreferenceKey 按客户端区分（如 pos-wpf.colorTheme），
/// PreferenceValue 是客户端自己解释的短字符串，UpdatedAtUtc 为最近一次写入的 UTC 时间。
/// 由 Hbpos.Api 读写（收银员登录后读取、员工改配色时 upsert），主 API 不读写。
/// 只新建表，不触碰任何既有表；回退只需删除该表并移除迁移账本行。实体不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// 表缺失时 Hbpos.Api 的偏好接口按「暂不可用」应答，收银端沿用本机主题，不影响登录。
/// 错误号自成一段 54380–54389：54380 表缺失、54381 列签名、54382 主键。
/// </summary>
internal static class UserPreferenceSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.UserPreference', N'U') IS NULL
    BEGIN
        -- 只按 (员工, 偏好键) 点查与 upsert，聚簇主键即可覆盖全部访问路径。
        CREATE TABLE [dbo].[UserPreference]
        (
            [UserGUID] nvarchar(50) NOT NULL,
            [PreferenceKey] nvarchar(64) NOT NULL,
            [PreferenceValue] nvarchar(200) NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_UserPreference] PRIMARY KEY CLUSTERED ([UserGUID], [PreferenceKey])
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：列签名（类型 / 长度 / 可空性）与主键键列顺序都必须精确匹配；
    // 不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length 对 nvarchar 是字节数（字符数 * 2）：nvarchar(50)=100、nvarchar(64)=128、nvarchar(200)=400。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.UserPreference', N'U') IS NULL
    THROW 54380, N'User preference table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'UserGUID', N'nvarchar', 100, 0),
      (N'PreferenceKey', N'nvarchar', 128, 0),
      (N'PreferenceValue', N'nvarchar', 400, 0),
      (N'UpdatedAtUtc', N'datetime2', 8, 0)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.UserPreference') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54381, N'User preference column signature is incompatible.', 1;

-- 主键必须是 (UserGUID, PreferenceKey) 两列且顺序正确：Hbpos.Api 的点查与 upsert 都依赖它。
IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints k
    JOIN sys.indexes i ON i.[object_id] = k.[parent_object_id] AND i.[index_id] = k.[unique_index_id]
    WHERE k.[parent_object_id] = OBJECT_ID(N'dbo.UserPreference')
      AND k.[type] = N'PK'
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 2
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'UserGUID')
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 2 AND c.[name] = N'PreferenceKey')
)
    THROW 54382, N'User preference primary key is missing or incompatible.', 1;
""";
}
