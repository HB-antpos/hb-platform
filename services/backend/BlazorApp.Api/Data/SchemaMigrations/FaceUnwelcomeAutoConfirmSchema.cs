namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 名单命中自动确认阈值列 dbo.FaceStoreProfile.UnwelcomeAutoConfirmScore（迁移 20261016.002-face-unwelcome-auto-confirm，用户 2026-10-13 决定）：
/// 命中任务的相似度达到或超过门店阈值，主 API 后台任务自动把任务标为已确认。decimal(5,2) NOT NULL，默认 90，0 = 不自动确认，
/// CHECK 限制为 0 或 50–100。只给现有表加一列，不改任何现有数据（已有门店档案行得到默认值 90）。
/// 新列走独立版本号迁移，生产须先显式执行 --schema=migrate 再部署新版主 API（新实体会 SELECT 这一列）。错误号区间 52830–52831。
/// 旧版 API / 接收端不认识这一列：INSERT 省略它时取默认值，UPDATE 只写自己认识的列，所以迁移先于部署执行对旧版是无害的。
/// </summary>
internal static class FaceUnwelcomeAutoConfirmSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.FaceStoreProfile', N'U') IS NULL
        THROW 52830, N'Face store profile table is missing; apply the face-match migration first.', 1;

    IF COL_LENGTH(N'dbo.FaceStoreProfile', N'UnwelcomeAutoConfirmScore') IS NULL
    BEGIN
        -- 列级约束可以引用正在添加的列；已有行取默认值 90。0 = 关闭；非零值必须在 50–100 之间。
        ALTER TABLE [dbo].[FaceStoreProfile] ADD
            [UnwelcomeAutoConfirmScore] decimal(5, 2) NOT NULL
                CONSTRAINT [DF_FaceStoreProfile_UnwelcomeAutoConfirmScore] DEFAULT (90)
                CONSTRAINT [CK_FaceStoreProfile_UnwelcomeAutoConfirmScore]
                    CHECK ([UnwelcomeAutoConfirmScore] = 0 OR [UnwelcomeAutoConfirmScore] BETWEEN 50 AND 100);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列的类型 / 精度 / 小数位 / 可空性；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.FaceStoreProfile', N'U') IS NULL
    THROW 52830, N'Face store profile table is missing.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.FaceStoreProfile')
      AND c.[name] = N'UnwelcomeAutoConfirmScore'
      AND ty.[name] = N'decimal'
      AND c.[precision] = 5
      AND c.[scale] = 2
      AND c.[is_nullable] = 0
)
    THROW 52831, N'Face store profile auto-confirm column signature is incompatible.', 1;
""";
}
