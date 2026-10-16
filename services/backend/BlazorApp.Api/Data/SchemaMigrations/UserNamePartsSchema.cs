namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 用户姓名拆分（迁移 20261018.001-user-name-parts）：dbo.[User] 一次加四列
/// GivenName（名）、MiddleName（中间名）、FamilyName（姓）、Nickname（昵称），均为 nvarchar(50) NULL。
/// 只加可空列、不回填任何数据：全名仍由 FullName 承载，存量账号四列为空，显示与搜索行为与加列前一致。
/// User 表随基线 20260827.001 建出（SqlSugarContext 的 CodeFirst tableTypes），新列只能走独立版本号迁移；
/// 实体 User 上对应属性写的是 [SugarColumn(IsNullable = true, Length = 50)]，长度必须与这里保持一致
/// （CheckCoreAsync 只查账本、不比较实体与库列，一致性靠本迁移的 VerifySql 门禁兜底）。
/// 部署顺序：先执行 --schema=migrate，再发主 API，再发 POS API（Hbpos.Api 与主 API 共用 User 实体，
/// 且没有 schema 门禁，缺列时整实体查询会直接失败）。错误号区间 53800–53801。
/// </summary>
internal static class UserNamePartsSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.User', N'U') IS NULL
    THROW 53800, N'User table is missing; apply the HBweb baseline first.', 1;

BEGIN TRANSACTION;
BEGIN TRY
    -- COL_LENGTH 判断保证重复执行是空操作；若 CodeFirst 已抢先建出同名列，则保持原列不动，
    -- 由 VerifySql 校验类型与长度是否符合预期（不符合只能显式修复，运行时绝不自动改写）。
    IF COL_LENGTH(N'dbo.User', N'GivenName') IS NULL
        ALTER TABLE [dbo].[User] ADD [GivenName] nvarchar(50) NULL;
    IF COL_LENGTH(N'dbo.User', N'MiddleName') IS NULL
        ALTER TABLE [dbo].[User] ADD [MiddleName] nvarchar(50) NULL;
    IF COL_LENGTH(N'dbo.User', N'FamilyName') IS NULL
        ALTER TABLE [dbo].[User] ADD [FamilyName] nvarchar(50) NULL;
    IF COL_LENGTH(N'dbo.User', N'Nickname') IS NULL
        ALTER TABLE [dbo].[User] ADD [Nickname] nvarchar(50) NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：四列都必须是 nvarchar(50) NULL（sys.columns.max_length 按字节计，nvarchar(50) = 100）。
    // 签名不符只能通过显式迁移修复，运行时绝不自动改写。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.User', N'U') IS NULL
    THROW 53800, N'User table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'GivenName', N'nvarchar', 100, 1),
      (N'MiddleName', N'nvarchar', 100, 1),
      (N'FamilyName', N'nvarchar', 100, 1),
      (N'Nickname', N'nvarchar', 100, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(N'dbo.User') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 53801, N'User name part columns signature is incompatible.', 1;
""";
}
