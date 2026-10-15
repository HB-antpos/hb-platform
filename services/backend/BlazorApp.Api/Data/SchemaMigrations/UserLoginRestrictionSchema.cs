namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 「不允许登录 Web / App」的标记表 dbo.UserLoginRestriction（迁移 20261017.002-user-login-restriction）。
/// 只建一张新表，不修改任何现有表、不改任何现有数据：有记录即禁止登录，老账号无记录＝继续可登录。
/// 新表走独立版本号迁移，生产须显式执行 --schema=migrate；实体不进 SqlSugarContext 的自动建表清单。
/// 登录、刷新令牌与会话校验都会读此表，迁移须先于新后端部署。错误号区间 53600–53602。
/// </summary>
internal static class UserLoginRestrictionSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.UserLoginRestriction', N'U') IS NULL
    BEGIN
        -- 每个账号最多一条；有记录即禁止登录，店长开启「允许登录」时删除。
        CREATE TABLE [dbo].[UserLoginRestriction]
        (
            [UserGUID] nvarchar(50) NOT NULL,
            [RestrictedAtUtc] datetime2 NOT NULL,
            [RestrictedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_UserLoginRestriction] PRIMARY KEY CLUSTERED ([UserGUID])
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
IF OBJECT_ID(N'dbo.UserLoginRestriction', N'U') IS NULL
    THROW 53600, N'User login restriction table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'UserGUID', N'nvarchar', 100, 0),
      (N'RestrictedAtUtc', N'datetime2', 0, 0),
      (N'RestrictedBy', N'nvarchar', 200, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.UserLoginRestriction') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 53601, N'User login restriction column signature is incompatible.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints k
    INNER JOIN sys.index_columns ic ON ic.[object_id] = k.[parent_object_id] AND ic.[index_id] = k.[unique_index_id]
    INNER JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
    WHERE k.[parent_object_id] = OBJECT_ID(N'dbo.UserLoginRestriction')
      AND k.[type] = N'PK'
      AND c.[name] = N'UserGUID'
)
    THROW 53602, N'User login restriction primary key signature is incompatible.', 1;
""";
}
