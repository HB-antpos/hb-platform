namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 账号邮箱验证码表新增 TargetEmail 列，供员工绑定 / 更换邮箱时记录待验证的新邮箱。
/// 只加可空列，历史行不受影响；生产须显式执行 --schema=migrate。错误号区间 51880–51881。
/// </summary>
internal static class UserPasswordResetCodeTargetEmailSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.UserPasswordResetCode', N'U') IS NULL
    THROW 51880, N'User password reset code table is missing; apply 20261004.004 first.', 1;

IF COL_LENGTH(N'dbo.UserPasswordResetCode', N'TargetEmail') IS NULL
BEGIN
    ALTER TABLE [dbo].[UserPasswordResetCode] ADD [TargetEmail] nvarchar(254) NULL;
END;
""";

    // 只读门禁：列必须存在且为可空 nvarchar(254)（max_length 按字节计为 508）。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.UserPasswordResetCode', N'U') IS NULL
    THROW 51880, N'User password reset code table is missing.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.UserPasswordResetCode')
      AND c.[name] = N'TargetEmail'
      AND ty.[name] = N'nvarchar'
      AND c.[max_length] = 508
      AND c.[is_nullable] = 1
)
    THROW 51881, N'User password reset code TargetEmail column signature is incompatible.', 1;
""";
}
