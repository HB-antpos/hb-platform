namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 员工通知（迁移 20261106.001-staff-notification）：后台给员工发邮件 / 短信，带模板。
/// - dbo.StaffNotificationTemplate —— 通知模板（系统模板「账号已开通」改过文案才会有一行，自建模板每个一行）。
/// - dbo.StaffNotificationBatch —— 一次发送 = 一个批次，记录谁发的、用哪个模板、进度。
/// - dbo.StaffNotificationRecipient —— 批次里每个员工一行：只存姓名、打码邮箱 / 手机号、两个渠道的状态与失败原因。
///   不存邮箱 / 手机号明文，也不存邮件正文和随机密码（密码只存在于邮件里）。
/// 只新建表，不触碰既有表；回退 = DROP 三张表 + 移除迁移账本行（旧版主 API 不读这些表，先回滚镜像即可）。
/// 实体不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// 错误号自成一段 54450–54461：
///   54450–54453 模板表（缺表 / 列签名 / 主键 / 模板键唯一索引）；
///   54454–54457 批次表（缺表 / 列签名 / 主键 / 创建时间索引）；
///   54458–54461 收件人表（缺表 / 列签名 / 主键 / 批次索引）。
/// 生产须显式执行 --schema=migrate，且要先于新版主 API 上线。
/// </summary>
internal static class StaffNotificationSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.StaffNotificationTemplate', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[StaffNotificationTemplate]
        (
            [Id] nvarchar(50) NOT NULL,
            [TemplateKey] nvarchar(60) NOT NULL,
            [Name] nvarchar(100) NOT NULL,
            [Kind] nvarchar(20) NOT NULL,
            [IsEnabled] bit NOT NULL,
            [EmailSubject] nvarchar(200) NOT NULL,
            [EmailBody] nvarchar(4000) NOT NULL,
            [SmsBody] nvarchar(500) NOT NULL,
            [CreatedBy] nvarchar(100) NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [UpdatedBy] nvarchar(100) NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_StaffNotificationTemplate] PRIMARY KEY CLUSTERED ([Id])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.StaffNotificationTemplate') AND [name] = N'UX_StaffNotificationTemplate_Key')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_StaffNotificationTemplate_Key]
            ON [dbo].[StaffNotificationTemplate] ([TemplateKey]);

    IF OBJECT_ID(N'dbo.StaffNotificationBatch', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[StaffNotificationBatch]
        (
            [Id] nvarchar(50) NOT NULL,
            [TemplateKey] nvarchar(60) NOT NULL,
            [TemplateName] nvarchar(100) NOT NULL,
            [CreatedBy] nvarchar(100) NOT NULL,
            [CreatedByUserGuid] nvarchar(50) NOT NULL,
            [SendEmail] bit NOT NULL,
            [SendSms] bit NOT NULL,
            [Status] nvarchar(20) NOT NULL,
            [TotalCount] int NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [StartedAtUtc] datetime2 NULL,
            [FinishedAtUtc] datetime2 NULL,
            CONSTRAINT [PK_StaffNotificationBatch] PRIMARY KEY CLUSTERED ([Id])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.StaffNotificationBatch') AND [name] = N'IX_StaffNotificationBatch_Created')
        CREATE NONCLUSTERED INDEX [IX_StaffNotificationBatch_Created]
            ON [dbo].[StaffNotificationBatch] ([CreatedAtUtc]);

    IF OBJECT_ID(N'dbo.StaffNotificationRecipient', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[StaffNotificationRecipient]
        (
            [Id] nvarchar(50) NOT NULL,
            [BatchId] nvarchar(50) NOT NULL,
            [UserGuid] nvarchar(50) NOT NULL,
            [DisplayName] nvarchar(200) NOT NULL,
            [StoreName] nvarchar(200) NULL,
            [EmailMask] nvarchar(200) NULL,
            [PhoneMask] nvarchar(20) NULL,
            [EmailStatus] nvarchar(20) NOT NULL,
            [EmailError] nvarchar(200) NULL,
            [SmsStatus] nvarchar(20) NOT NULL,
            [SmsError] nvarchar(200) NULL,
            [SmsSegments] int NOT NULL,
            [Note] nvarchar(300) NOT NULL,
            [SortOrder] int NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_StaffNotificationRecipient] PRIMARY KEY CLUSTERED ([Id])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.StaffNotificationRecipient') AND [name] = N'IX_StaffNotificationRecipient_Batch')
        CREATE NONCLUSTERED INDEX [IX_StaffNotificationRecipient_Batch]
            ON [dbo].[StaffNotificationRecipient] ([BatchId], [SortOrder]);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：列签名（类型 / 长度 / 可空性）、主键与索引必须精确匹配；不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length：nvarchar 为字节数（字符数 * 2），bit 为 1。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.StaffNotificationTemplate', N'U') IS NULL
    THROW 54450, N'Staff notification template table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.StaffNotificationTemplate', N'Id', N'nvarchar', 100, 0),
      (N'dbo.StaffNotificationTemplate', N'TemplateKey', N'nvarchar', 120, 0),
      (N'dbo.StaffNotificationTemplate', N'Name', N'nvarchar', 200, 0),
      (N'dbo.StaffNotificationTemplate', N'Kind', N'nvarchar', 40, 0),
      (N'dbo.StaffNotificationTemplate', N'IsEnabled', N'bit', 1, 0),
      (N'dbo.StaffNotificationTemplate', N'EmailSubject', N'nvarchar', 400, 0),
      (N'dbo.StaffNotificationTemplate', N'EmailBody', N'nvarchar', 8000, 0),
      (N'dbo.StaffNotificationTemplate', N'SmsBody', N'nvarchar', 1000, 0),
      (N'dbo.StaffNotificationTemplate', N'CreatedBy', N'nvarchar', 200, 0),
      (N'dbo.StaffNotificationTemplate', N'CreatedAtUtc', N'datetime2', 8, 0),
      (N'dbo.StaffNotificationTemplate', N'UpdatedBy', N'nvarchar', 200, 0),
      (N'dbo.StaffNotificationTemplate', N'UpdatedAtUtc', N'datetime2', 8, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54451, N'Staff notification template column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.StaffNotificationTemplate') AND [type] = N'PK')
    THROW 54452, N'Staff notification template primary key is missing.', 1;

-- 模板键唯一索引：唯一、非过滤，键列恰好是 (TemplateKey)，升序。
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.StaffNotificationTemplate')
      AND i.[name] = N'UX_StaffNotificationTemplate_Key'
      AND i.[has_filter] = 0
      AND i.[is_unique] = 1
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1 AND c.[name] = N'TemplateKey' AND ic.[is_descending_key] = 0)
)
    THROW 54453, N'Staff notification template key unique index is missing or incompatible.', 1;

IF OBJECT_ID(N'dbo.StaffNotificationBatch', N'U') IS NULL
    THROW 54454, N'Staff notification batch table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.StaffNotificationBatch', N'Id', N'nvarchar', 100, 0),
      (N'dbo.StaffNotificationBatch', N'TemplateKey', N'nvarchar', 120, 0),
      (N'dbo.StaffNotificationBatch', N'TemplateName', N'nvarchar', 200, 0),
      (N'dbo.StaffNotificationBatch', N'CreatedBy', N'nvarchar', 200, 0),
      (N'dbo.StaffNotificationBatch', N'CreatedByUserGuid', N'nvarchar', 100, 0),
      (N'dbo.StaffNotificationBatch', N'SendEmail', N'bit', 1, 0),
      (N'dbo.StaffNotificationBatch', N'SendSms', N'bit', 1, 0),
      (N'dbo.StaffNotificationBatch', N'Status', N'nvarchar', 40, 0),
      (N'dbo.StaffNotificationBatch', N'TotalCount', N'int', 4, 0),
      (N'dbo.StaffNotificationBatch', N'CreatedAtUtc', N'datetime2', 8, 0),
      (N'dbo.StaffNotificationBatch', N'StartedAtUtc', N'datetime2', 8, 1),
      (N'dbo.StaffNotificationBatch', N'FinishedAtUtc', N'datetime2', 8, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54455, N'Staff notification batch column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.StaffNotificationBatch') AND [type] = N'PK')
    THROW 54456, N'Staff notification batch primary key is missing.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.StaffNotificationBatch')
      AND i.[name] = N'IX_StaffNotificationBatch_Created'
      AND i.[has_filter] = 0
      AND i.[is_unique] = 0
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1 AND c.[name] = N'CreatedAtUtc' AND ic.[is_descending_key] = 0)
)
    THROW 54457, N'Staff notification batch created-time index is missing or incompatible.', 1;

IF OBJECT_ID(N'dbo.StaffNotificationRecipient', N'U') IS NULL
    THROW 54458, N'Staff notification recipient table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.StaffNotificationRecipient', N'Id', N'nvarchar', 100, 0),
      (N'dbo.StaffNotificationRecipient', N'BatchId', N'nvarchar', 100, 0),
      (N'dbo.StaffNotificationRecipient', N'UserGuid', N'nvarchar', 100, 0),
      (N'dbo.StaffNotificationRecipient', N'DisplayName', N'nvarchar', 400, 0),
      (N'dbo.StaffNotificationRecipient', N'StoreName', N'nvarchar', 400, 1),
      (N'dbo.StaffNotificationRecipient', N'EmailMask', N'nvarchar', 400, 1),
      (N'dbo.StaffNotificationRecipient', N'PhoneMask', N'nvarchar', 40, 1),
      (N'dbo.StaffNotificationRecipient', N'EmailStatus', N'nvarchar', 40, 0),
      (N'dbo.StaffNotificationRecipient', N'EmailError', N'nvarchar', 400, 1),
      (N'dbo.StaffNotificationRecipient', N'SmsStatus', N'nvarchar', 40, 0),
      (N'dbo.StaffNotificationRecipient', N'SmsError', N'nvarchar', 400, 1),
      (N'dbo.StaffNotificationRecipient', N'SmsSegments', N'int', 4, 0),
      (N'dbo.StaffNotificationRecipient', N'Note', N'nvarchar', 600, 0),
      (N'dbo.StaffNotificationRecipient', N'SortOrder', N'int', 4, 0),
      (N'dbo.StaffNotificationRecipient', N'UpdatedAtUtc', N'datetime2', 8, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54459, N'Staff notification recipient column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.StaffNotificationRecipient') AND [type] = N'PK')
    THROW 54460, N'Staff notification recipient primary key is missing.', 1;

-- 批次索引：非过滤、非唯一，键列恰好是 (BatchId, SortOrder)，都是升序。
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.StaffNotificationRecipient')
      AND i.[name] = N'IX_StaffNotificationRecipient_Batch'
      AND i.[has_filter] = 0
      AND i.[is_unique] = 0
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 2
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1 AND c.[name] = N'BatchId' AND ic.[is_descending_key] = 0)
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 2 AND c.[name] = N'SortOrder' AND ic.[is_descending_key] = 0)
)
    THROW 54461, N'Staff notification recipient batch index is missing or incompatible.', 1;
""";
}
