namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 邮箱验证码设置 / 找回密码表。新表走独立版本号迁移，生产须显式执行 --schema=migrate；
/// 实体不进 SqlSugarContext 的自动建表清单。错误号区间 51875–51877。
/// </summary>
internal static class UserPasswordResetCodeSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.UserPasswordResetCode', N'U') IS NULL
    BEGIN
        -- 验证码只存哈希；ConsumedAtUtc 非空即失效（已使用或被新码作废）。
        CREATE TABLE [dbo].[UserPasswordResetCode]
        (
            [Id] nvarchar(36) NOT NULL,
            [UserGUID] nvarchar(50) NOT NULL,
            [Purpose] nvarchar(16) NOT NULL,
            [CodeHash] nvarchar(64) NOT NULL,
            [ExpiresAtUtc] datetime2 NOT NULL,
            [FailedAttempts] int NOT NULL CONSTRAINT [DF_UserPasswordResetCode_FailedAttempts] DEFAULT(0),
            [ConsumedAtUtc] datetime2 NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [RequestedBy] nvarchar(100) NULL,
            [RequestIp] nvarchar(64) NULL,
            CONSTRAINT [PK_UserPasswordResetCode] PRIMARY KEY CLUSTERED ([Id])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.UserPasswordResetCode')
          AND [name] = N'IX_UserPasswordResetCode_User_Created'
    )
    BEGIN
        -- 取最新验证码、判断发送冷却都按账号 + 创建时间查。
        CREATE NONCLUSTERED INDEX [IX_UserPasswordResetCode_User_Created]
            ON [dbo].[UserPasswordResetCode] ([UserGUID], [CreatedAtUtc] DESC);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、可空性、主键与账号索引；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.UserPasswordResetCode', N'U') IS NULL
    THROW 51875, N'User password reset code table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'Id', N'nvarchar', 72, 0),
      (N'UserGUID', N'nvarchar', 100, 0),
      (N'Purpose', N'nvarchar', 32, 0),
      (N'CodeHash', N'nvarchar', 128, 0),
      (N'ExpiresAtUtc', N'datetime2', 0, 0),
      (N'FailedAttempts', N'int', 0, 0),
      (N'ConsumedAtUtc', N'datetime2', 0, 1),
      (N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'RequestedBy', N'nvarchar', 200, 1),
      (N'RequestIp', N'nvarchar', 128, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.UserPasswordResetCode') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 51876, N'User password reset code column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.UserPasswordResetCode') AND [type] = N'PK')
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.UserPasswordResetCode')
      AND [name] = N'IX_UserPasswordResetCode_User_Created'
 )
    THROW 51877, N'User password reset code index signature is incompatible.', 1;
""";
}
