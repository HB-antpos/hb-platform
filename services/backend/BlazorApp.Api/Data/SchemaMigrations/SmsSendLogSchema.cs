namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 短信发送记录（迁移 20261030.003-sms-send-log）：
/// - 新表 dbo.SmsSendLog —— 每次尝试发送一行，用于按手机号 / 全站限流与排查花费。
///   不存手机号明文也不存短信正文：号码只存带密钥的 HMAC 哈希 + 打码号码。
/// - 索引：(PhoneHash, CreatedAtUtc) 数某个号码最近一小时 / 一天的发送次数；(CreatedAtUtc) 数全站最近一天的总量。
/// 只新建表，不触碰既有表；回退 = DROP 该表 + 移除迁移账本行（旧版主 API 不读这张表，先回滚镜像即可）。
/// 实体不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// 错误号自成一段 54410–54419：54410 表缺失、54411 列签名、54412 主键、54413 号码索引、54414 时间索引。
/// 生产须显式执行 --schema=migrate，且要先于新版主 API 上线。
/// </summary>
internal static class SmsSendLogSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.SmsSendLog', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[SmsSendLog]
        (
            [Id] nvarchar(50) NOT NULL,
            [Purpose] nvarchar(40) NOT NULL,
            [PhoneHash] nvarchar(64) NOT NULL,
            [PhoneMask] nvarchar(20) NOT NULL,
            [Segments] int NOT NULL,
            [Status] nvarchar(20) NOT NULL,
            [ProviderMessageId] nvarchar(64) NULL,
            [ErrorCode] nvarchar(80) NULL,
            [RelatedKey] nvarchar(100) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [FinishedAtUtc] datetime2 NULL,
            CONSTRAINT [PK_SmsSendLog] PRIMARY KEY CLUSTERED ([Id])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.SmsSendLog') AND [name] = N'IX_SmsSendLog_Phone')
        CREATE NONCLUSTERED INDEX [IX_SmsSendLog_Phone]
            ON [dbo].[SmsSendLog] ([PhoneHash], [CreatedAtUtc]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.SmsSendLog') AND [name] = N'IX_SmsSendLog_Created')
        CREATE NONCLUSTERED INDEX [IX_SmsSendLog_Created]
            ON [dbo].[SmsSendLog] ([CreatedAtUtc]);

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
IF OBJECT_ID(N'dbo.SmsSendLog', N'U') IS NULL
    THROW 54410, N'SMS send log table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.SmsSendLog', N'Id', N'nvarchar', 100, 0),
      (N'dbo.SmsSendLog', N'Purpose', N'nvarchar', 80, 0),
      (N'dbo.SmsSendLog', N'PhoneHash', N'nvarchar', 128, 0),
      (N'dbo.SmsSendLog', N'PhoneMask', N'nvarchar', 40, 0),
      (N'dbo.SmsSendLog', N'Segments', N'int', 4, 0),
      (N'dbo.SmsSendLog', N'Status', N'nvarchar', 40, 0),
      (N'dbo.SmsSendLog', N'ProviderMessageId', N'nvarchar', 128, 1),
      (N'dbo.SmsSendLog', N'ErrorCode', N'nvarchar', 160, 1),
      (N'dbo.SmsSendLog', N'RelatedKey', N'nvarchar', 200, 1),
      (N'dbo.SmsSendLog', N'CreatedAtUtc', N'datetime2', 8, 0),
      (N'dbo.SmsSendLog', N'FinishedAtUtc', N'datetime2', 8, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54411, N'SMS send log column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.SmsSendLog') AND [type] = N'PK')
    THROW 54412, N'SMS send log primary key is missing.', 1;

-- 号码索引：非过滤、非唯一，键列恰好是 (PhoneHash, CreatedAtUtc)，都是升序。
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.SmsSendLog')
      AND i.[name] = N'IX_SmsSendLog_Phone'
      AND i.[has_filter] = 0
      AND i.[is_unique] = 0
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 2
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1 AND c.[name] = N'PhoneHash' AND ic.[is_descending_key] = 0)
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 2 AND c.[name] = N'CreatedAtUtc' AND ic.[is_descending_key] = 0)
)
    THROW 54413, N'SMS send log phone index is missing or incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.SmsSendLog')
      AND i.[name] = N'IX_SmsSendLog_Created'
      AND i.[has_filter] = 0
      AND i.[is_unique] = 0
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1 AND c.[name] = N'CreatedAtUtc' AND ic.[is_descending_key] = 0)
)
    THROW 54414, N'SMS send log created-time index is missing or incompatible.', 1;
""";
}
