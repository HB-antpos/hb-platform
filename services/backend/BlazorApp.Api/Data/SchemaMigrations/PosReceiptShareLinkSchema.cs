namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// POS 订单在线小票分享链接（迁移 20261030.005-pos-receipt-share-link）：
/// - 新表 dbo.PosReceiptShareLink —— 店员给某张订单生成一条客人可免登录打开的只读小票链接，一次生成一行。
///   令牌明文不落库：公开接口按 SHA-256 哈希（小写十六进制）查找；店员再次打开二维码弹层要重新显示同一条链接，
///   所以另存一份 DataProtection 加密的密文（TokenProtected，密钥环不在库里），库泄露也还原不出可用链接。
/// - 索引：TokenHash 唯一（公开接口按哈希查）；(OrderGuid, CreatedAtUtc) 找某订单当前有效链接 / 重新生成时撤销旧链接。
/// 只新建表，不触碰既有表；回退 = DROP 该表 + 移除迁移账本行（旧版主 API 不读这张表，先回滚镜像即可）。
/// 实体不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// 错误号自成一段 54430–54439：54430 表缺失、54431 列签名、54432 主键、54433 令牌唯一索引、54434 订单索引。
/// 生产须显式执行 --schema=migrate，且要先于新版主 API 上线。
/// </summary>
internal static class PosReceiptShareLinkSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.PosReceiptShareLink', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PosReceiptShareLink]
        (
            [Id] nvarchar(50) NOT NULL,
            [OrderGuid] nvarchar(50) NOT NULL,
            [BranchCode] nvarchar(20) NULL,
            [TokenHash] nvarchar(64) NOT NULL,
            [TokenProtected] nvarchar(500) NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [CreatedBy] nvarchar(100) NULL,
            [ExpiresAtUtc] datetime2 NOT NULL,
            [RevokedAtUtc] datetime2 NULL,
            [OpenCount] int NOT NULL CONSTRAINT [DF_PosReceiptShareLink_OpenCount] DEFAULT (0),
            [LastOpenedAtUtc] datetime2 NULL,
            CONSTRAINT [PK_PosReceiptShareLink] PRIMARY KEY CLUSTERED ([Id])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PosReceiptShareLink') AND [name] = N'UX_PosReceiptShareLink_TokenHash')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_PosReceiptShareLink_TokenHash]
            ON [dbo].[PosReceiptShareLink] ([TokenHash]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PosReceiptShareLink') AND [name] = N'IX_PosReceiptShareLink_Order')
        CREATE NONCLUSTERED INDEX [IX_PosReceiptShareLink_Order]
            ON [dbo].[PosReceiptShareLink] ([OrderGuid], [CreatedAtUtc]);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：列签名（类型 / 长度 / 可空性）、主键与两个索引必须精确匹配；不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length：nvarchar 为字节数（字符数 * 2）。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.PosReceiptShareLink', N'U') IS NULL
    THROW 54430, N'POS receipt share link table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.PosReceiptShareLink', N'Id', N'nvarchar', 100, 0),
      (N'dbo.PosReceiptShareLink', N'OrderGuid', N'nvarchar', 100, 0),
      (N'dbo.PosReceiptShareLink', N'BranchCode', N'nvarchar', 40, 1),
      (N'dbo.PosReceiptShareLink', N'TokenHash', N'nvarchar', 128, 0),
      (N'dbo.PosReceiptShareLink', N'TokenProtected', N'nvarchar', 1000, 0),
      (N'dbo.PosReceiptShareLink', N'CreatedAtUtc', N'datetime2', 8, 0),
      (N'dbo.PosReceiptShareLink', N'CreatedBy', N'nvarchar', 200, 1),
      (N'dbo.PosReceiptShareLink', N'ExpiresAtUtc', N'datetime2', 8, 0),
      (N'dbo.PosReceiptShareLink', N'RevokedAtUtc', N'datetime2', 8, 1),
      (N'dbo.PosReceiptShareLink', N'OpenCount', N'int', 4, 0),
      (N'dbo.PosReceiptShareLink', N'LastOpenedAtUtc', N'datetime2', 8, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54431, N'POS receipt share link column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PosReceiptShareLink') AND [type] = N'PK')
    THROW 54432, N'POS receipt share link primary key is missing.', 1;

-- 令牌索引：必须唯一、非过滤，键列恰好是 TokenHash（升序）；唯一性是「公开接口按哈希查到至多一行」的前提。
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.PosReceiptShareLink')
      AND i.[name] = N'UX_PosReceiptShareLink_TokenHash'
      AND i.[has_filter] = 0
      AND i.[is_unique] = 1
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1 AND c.[name] = N'TokenHash' AND ic.[is_descending_key] = 0)
)
    THROW 54433, N'POS receipt share link token index is missing or incompatible.', 1;

-- 订单索引：非过滤、非唯一，键列恰好是 (OrderGuid, CreatedAtUtc)，都是升序。
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.PosReceiptShareLink')
      AND i.[name] = N'IX_PosReceiptShareLink_Order'
      AND i.[has_filter] = 0
      AND i.[is_unique] = 0
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 2
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1 AND c.[name] = N'OrderGuid' AND ic.[is_descending_key] = 0)
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 2 AND c.[name] = N'CreatedAtUtc' AND ic.[is_descending_key] = 0)
)
    THROW 54434, N'POS receipt share link order index is missing or incompatible.', 1;
""";
}
