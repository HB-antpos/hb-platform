namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 通知平台：通知内容与接收人两张表。新表走独立版本号迁移，生产须显式执行 --schema=migrate；
/// 实体不进 SqlSugarContext 的自动建表清单。错误号区间 52500–52502。
/// </summary>
internal static class AppNotificationSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.AppNotification', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[AppNotification]
        (
            [NotificationId] nvarchar(32) NOT NULL,
            [Type] nvarchar(64) NOT NULL,
            [Priority] int NOT NULL,
            [Title] nvarchar(200) NOT NULL,
            [Body] nvarchar(1000) NOT NULL,
            [DataJson] nvarchar(4000) NULL,
            [StoreCode] nvarchar(50) NULL,
            [DedupeKey] nvarchar(200) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [ExpiresAtUtc] datetime2 NULL,
            [RevokedAtUtc] datetime2 NULL,
            [CreatedBy] nvarchar(100) NOT NULL,
            CONSTRAINT [PK_AppNotification] PRIMARY KEY CLUSTERED ([NotificationId])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AppNotification')
          AND [name] = N'UX_AppNotification_DedupeKey'
    )
    BEGIN
        -- 业务幂等：同一幂等键只允许一条通知，并发重复发布由唯一索引兜底。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_AppNotification_DedupeKey]
            ON [dbo].[AppNotification] ([DedupeKey])
            WHERE [DedupeKey] IS NOT NULL;
    END;

    IF OBJECT_ID(N'dbo.AppNotificationRecipient', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[AppNotificationRecipient]
        (
            [NotificationId] nvarchar(32) NOT NULL,
            [UserGUID] nvarchar(50) NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [ReadAtUtc] datetime2 NULL,
            CONSTRAINT [PK_AppNotificationRecipient] PRIMARY KEY CLUSTERED ([NotificationId], [UserGUID])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AppNotificationRecipient')
          AND [name] = N'IX_AppNotificationRecipient_User_Created'
    )
    BEGIN
        -- 收件箱按人 + 时间倒序翻页、未读数按人统计都走这条索引。
        CREATE NONCLUSTERED INDEX [IX_AppNotificationRecipient_User_Created]
            ON [dbo].[AppNotificationRecipient] ([UserGUID], [CreatedAtUtc] DESC)
            INCLUDE ([ReadAtUtc]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、可空性、主键、幂等唯一索引与收件箱索引；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.AppNotification', N'U') IS NULL
 OR OBJECT_ID(N'dbo.AppNotificationRecipient', N'U') IS NULL
    THROW 52500, N'App notification tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'AppNotification', N'NotificationId', N'nvarchar', 64, 0),
      (N'AppNotification', N'Type', N'nvarchar', 128, 0),
      (N'AppNotification', N'Priority', N'int', 0, 0),
      (N'AppNotification', N'Title', N'nvarchar', 400, 0),
      (N'AppNotification', N'Body', N'nvarchar', 2000, 0),
      (N'AppNotification', N'DataJson', N'nvarchar', 8000, 1),
      (N'AppNotification', N'StoreCode', N'nvarchar', 100, 1),
      (N'AppNotification', N'DedupeKey', N'nvarchar', 400, 1),
      (N'AppNotification', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'AppNotification', N'ExpiresAtUtc', N'datetime2', 0, 1),
      (N'AppNotification', N'RevokedAtUtc', N'datetime2', 0, 1),
      (N'AppNotification', N'CreatedBy', N'nvarchar', 200, 0),
      (N'AppNotificationRecipient', N'NotificationId', N'nvarchar', 64, 0),
      (N'AppNotificationRecipient', N'UserGUID', N'nvarchar', 100, 0),
      (N'AppNotificationRecipient', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'AppNotificationRecipient', N'ReadAtUtc', N'datetime2', 0, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 52501, N'App notification column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.AppNotification') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.AppNotificationRecipient') AND [type] = N'PK')
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.AppNotification')
      AND [name] = N'UX_AppNotification_DedupeKey'
      AND [is_unique] = 1
      AND [has_filter] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.AppNotificationRecipient')
      AND [name] = N'IX_AppNotificationRecipient_User_Created'
 )
    THROW 52502, N'App notification index signature is incompatible.', 1;
""";
}
