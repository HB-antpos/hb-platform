namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 首次登录 / 重置后须改密的标记表。新表走独立版本号迁移，生产须显式执行 --schema=migrate；
/// 实体不进 SqlSugarContext 的自动建表清单。错误号区间 51870–51872。
/// </summary>
internal static class UserPasswordChangeRequirementSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.UserPasswordChangeRequirement', N'U') IS NULL
    BEGIN
        -- 每个账号最多一条；有记录即须改密，改密成功后删除。
        CREATE TABLE [dbo].[UserPasswordChangeRequirement]
        (
            [UserGUID] nvarchar(50) NOT NULL,
            [Reason] nvarchar(32) NOT NULL,
            [RequiredAtUtc] datetime2 NOT NULL,
            [RequiredBy] nvarchar(100) NULL,
            CONSTRAINT [PK_UserPasswordChangeRequirement] PRIMARY KEY CLUSTERED ([UserGUID])
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
IF OBJECT_ID(N'dbo.UserPasswordChangeRequirement', N'U') IS NULL
    THROW 51870, N'User password change requirement table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'UserGUID', N'nvarchar', 100, 0),
      (N'Reason', N'nvarchar', 64, 0),
      (N'RequiredAtUtc', N'datetime2', 0, 0),
      (N'RequiredBy', N'nvarchar', 200, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.UserPasswordChangeRequirement') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 51871, N'User password change requirement column signature is incompatible.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints k
    INNER JOIN sys.index_columns ic ON ic.[object_id] = k.[parent_object_id] AND ic.[index_id] = k.[unique_index_id]
    INNER JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
    WHERE k.[parent_object_id] = OBJECT_ID(N'dbo.UserPasswordChangeRequirement')
      AND k.[type] = N'PK'
      AND c.[name] = N'UserGUID'
)
    THROW 51872, N'User password change requirement primary key signature is incompatible.', 1;
""";
}
