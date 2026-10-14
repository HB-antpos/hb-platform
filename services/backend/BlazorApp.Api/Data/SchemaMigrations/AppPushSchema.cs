namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// App 系统推送（走 Expo 推送服务）：推送设备 dbo.AppPushDevice 与发件箱 dbo.AppPushDelivery 两张表
/// （迁移 20261017.001-app-push）。只建这两张新表，不修改任何现有表、不改任何现有数据。
/// <para>
/// 设备表：活动令牌全库唯一（过滤唯一索引 UX_AppPushDevice_ActiveToken：ExpoPushToken WHERE RevokedAtUtc IS NULL），
/// 同一令牌换账号登录时旧行软撤销、新账号另起一行；按账号查活动设备走 (UserGUID, RevokedAtUtc)。
/// 发件箱：(NotificationId, PushDeviceId) 唯一，保证同一通知不会对同一设备重复入箱；
/// 后台任务按 (State, NextAttemptAtUtc) 领取待发送与到期待取回执的记录。不建外键：通知与设备行可以先于发件箱被清理。
/// </para>
/// 新表走独立版本号迁移，生产须显式执行 --schema=migrate；实体不进 SqlSugarContext 的自动建表清单。
/// 整个推送功能默认关闭（Push:Expo:Enabled=false），迁移先于开关执行。错误号区间 53500–53599（本迁移用 53500–53503）。
/// </summary>
internal static class AppPushSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.AppPushDevice', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[AppPushDevice]
        (
            [PushDeviceId] nvarchar(32) NOT NULL,
            [UserGUID] nvarchar(50) NOT NULL,
            [Platform] nvarchar(16) NOT NULL,
            [ExpoPushToken] nvarchar(200) NOT NULL,
            [AppVersion] nvarchar(50) NULL,
            [RuntimeVersion] nvarchar(50) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [LastSeenAtUtc] datetime2 NOT NULL,
            [RevokedAtUtc] datetime2 NULL,
            [RevokeReason] nvarchar(32) NULL,
            CONSTRAINT [PK_AppPushDevice] PRIMARY KEY CLUSTERED ([PushDeviceId])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AppPushDevice')
          AND [name] = N'UX_AppPushDevice_ActiveToken'
    )
    BEGIN
        -- 活动令牌唯一：同一令牌同一时刻只能属于一个账号；已撤销的历史行不参与唯一性，所以换人登录是「撤销旧行 + 新建一行」。
        -- 并发注册同一令牌时由本索引兜底（驱动层报 2601 / 2627，应用层据此重试）。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_AppPushDevice_ActiveToken]
            ON [dbo].[AppPushDevice] ([ExpoPushToken])
            WHERE [RevokedAtUtc] IS NULL;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AppPushDevice')
          AND [name] = N'IX_AppPushDevice_User_Revoked'
    )
    BEGIN
        -- 发布器按接收人查活动设备（RevokedAtUtc IS NULL）、注册时按账号统计活动设备数都走这条索引。
        CREATE NONCLUSTERED INDEX [IX_AppPushDevice_User_Revoked]
            ON [dbo].[AppPushDevice] ([UserGUID], [RevokedAtUtc]);
    END;

    IF OBJECT_ID(N'dbo.AppPushDelivery', N'U') IS NULL
    BEGIN
        -- State：0 待发送 / 1 已受理待回执 / 2 已送达 / 3 失败 / 4 跳过。
        -- NextAttemptAtUtc：待发送时是下次尝试时间，已受理时复用为回执到期时间。LastError 只存短错误码，不存令牌、正文或异常消息。
        CREATE TABLE [dbo].[AppPushDelivery]
        (
            [DeliveryId] nvarchar(32) NOT NULL,
            [NotificationId] nvarchar(32) NOT NULL,
            [PushDeviceId] nvarchar(32) NOT NULL,
            [UserGUID] nvarchar(50) NOT NULL,
            [State] int NOT NULL,
            [Attempts] int NOT NULL,
            [NextAttemptAtUtc] datetime2 NOT NULL,
            [LockedUntilUtc] datetime2 NULL,
            [TicketId] nvarchar(64) NULL,
            [LastError] nvarchar(64) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [SentAtUtc] datetime2 NULL,
            [ReceiptCheckedAtUtc] datetime2 NULL,
            CONSTRAINT [PK_AppPushDelivery] PRIMARY KEY CLUSTERED ([DeliveryId])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AppPushDelivery')
          AND [name] = N'UX_AppPushDelivery_Notification_Device'
    )
    BEGIN
        -- 同一通知对同一设备只入箱一次。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_AppPushDelivery_Notification_Device]
            ON [dbo].[AppPushDelivery] ([NotificationId], [PushDeviceId]);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AppPushDelivery')
          AND [name] = N'IX_AppPushDelivery_State_NextAttempt'
    )
    BEGIN
        -- 后台任务领取待发送（State = 0）与到期待取回执（State = 1）的记录都走这条索引。
        CREATE NONCLUSTERED INDEX [IX_AppPushDelivery_State_NextAttempt]
            ON [dbo].[AppPushDelivery] ([State], [NextAttemptAtUtc]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、可空性、主键与索引的键列顺序、唯一性，以及活动令牌索引的过滤条件；不兼容结构只能通过显式迁移修复。
    // nvarchar 的 max_length 是字节数（字符数 × 2）；int / datetime2 长度固定，传 0 表示不比较。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.AppPushDevice', N'U') IS NULL
 OR OBJECT_ID(N'dbo.AppPushDelivery', N'U') IS NULL
    THROW 53500, N'App push tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'AppPushDevice', N'PushDeviceId', N'nvarchar', 64, 0),
      (N'AppPushDevice', N'UserGUID', N'nvarchar', 100, 0),
      (N'AppPushDevice', N'Platform', N'nvarchar', 32, 0),
      (N'AppPushDevice', N'ExpoPushToken', N'nvarchar', 400, 0),
      (N'AppPushDevice', N'AppVersion', N'nvarchar', 100, 1),
      (N'AppPushDevice', N'RuntimeVersion', N'nvarchar', 100, 1),
      (N'AppPushDevice', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'AppPushDevice', N'LastSeenAtUtc', N'datetime2', 0, 0),
      (N'AppPushDevice', N'RevokedAtUtc', N'datetime2', 0, 1),
      (N'AppPushDevice', N'RevokeReason', N'nvarchar', 64, 1),
      (N'AppPushDelivery', N'DeliveryId', N'nvarchar', 64, 0),
      (N'AppPushDelivery', N'NotificationId', N'nvarchar', 64, 0),
      (N'AppPushDelivery', N'PushDeviceId', N'nvarchar', 64, 0),
      (N'AppPushDelivery', N'UserGUID', N'nvarchar', 100, 0),
      (N'AppPushDelivery', N'State', N'int', 0, 0),
      (N'AppPushDelivery', N'Attempts', N'int', 0, 0),
      (N'AppPushDelivery', N'NextAttemptAtUtc', N'datetime2', 0, 0),
      (N'AppPushDelivery', N'LockedUntilUtc', N'datetime2', 0, 1),
      (N'AppPushDelivery', N'TicketId', N'nvarchar', 128, 1),
      (N'AppPushDelivery', N'LastError', N'nvarchar', 128, 1),
      (N'AppPushDelivery', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'AppPushDelivery', N'SentAtUtc', N'datetime2', 0, 1),
      (N'AppPushDelivery', N'ReceiptCheckedAtUtc', N'datetime2', 0, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 53501, N'App push column signature is incompatible.', 1;

-- 主键必须正好是单列（设备表 PushDeviceId、发件箱 DeliveryId）；其余索引的键列、顺序与唯一性必须与迁移一致：
-- 多一列、少一列、换序或唯一性不符都算不兼容（唯一性是业务正确性的底线：活动令牌唯一、同一通知对同一设备只入箱一次）。
IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'AppPushDevice', N'PK_AppPushDevice', 1, N'PushDeviceId', 1),
      (N'AppPushDelivery', N'PK_AppPushDelivery', 1, N'DeliveryId', 1),
      (N'AppPushDevice', N'UX_AppPushDevice_ActiveToken', 1, N'ExpoPushToken', 1),
      (N'AppPushDevice', N'IX_AppPushDevice_User_Revoked', 1, N'UserGUID', 0),
      (N'AppPushDevice', N'IX_AppPushDevice_User_Revoked', 2, N'RevokedAtUtc', 0),
      (N'AppPushDelivery', N'UX_AppPushDelivery_Notification_Device', 1, N'NotificationId', 1),
      (N'AppPushDelivery', N'UX_AppPushDelivery_Notification_Device', 2, N'PushDeviceId', 1),
      (N'AppPushDelivery', N'IX_AppPushDelivery_State_NextAttempt', 1, N'State', 0),
      (N'AppPushDelivery', N'IX_AppPushDelivery_State_NextAttempt', 2, N'NextAttemptAtUtc', 0)
    ) expected([table_name], [index_name], [key_ordinal], [column_name], [is_unique])
    LEFT JOIN sys.indexes i
        ON i.[object_id] = OBJECT_ID(N'dbo.' + expected.[table_name]) AND i.[name] = expected.[index_name]
    LEFT JOIN sys.index_columns ic
        ON ic.[object_id] = i.[object_id]
       AND ic.[index_id] = i.[index_id]
       AND ic.[key_ordinal] = expected.[key_ordinal]
       AND ic.[is_included_column] = 0
    LEFT JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
    WHERE i.[index_id] IS NULL OR c.[name] IS NULL OR c.[name] <> expected.[column_name]
       OR i.[is_unique] <> expected.[is_unique]
)
 OR EXISTS (
    -- 键列数不得超过预期（主键与唯一索引的键列多出来，唯一性语义就变了）。
    SELECT 1 FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
    WHERE ic.[is_included_column] = 0
      AND (
            (i.[object_id] = OBJECT_ID(N'dbo.AppPushDevice') AND i.[name] = N'PK_AppPushDevice' AND ic.[key_ordinal] > 1)
         OR (i.[object_id] = OBJECT_ID(N'dbo.AppPushDelivery') AND i.[name] = N'PK_AppPushDelivery' AND ic.[key_ordinal] > 1)
         OR (i.[object_id] = OBJECT_ID(N'dbo.AppPushDevice') AND i.[name] = N'UX_AppPushDevice_ActiveToken' AND ic.[key_ordinal] > 1)
         OR (i.[object_id] = OBJECT_ID(N'dbo.AppPushDevice') AND i.[name] = N'IX_AppPushDevice_User_Revoked' AND ic.[key_ordinal] > 2)
         OR (i.[object_id] = OBJECT_ID(N'dbo.AppPushDelivery') AND i.[name] = N'UX_AppPushDelivery_Notification_Device' AND ic.[key_ordinal] > 2)
         OR (i.[object_id] = OBJECT_ID(N'dbo.AppPushDelivery') AND i.[name] = N'IX_AppPushDelivery_State_NextAttempt' AND ic.[key_ordinal] > 2)
      )
)
    THROW 53502, N'App push key or index signature is incompatible.', 1;

-- 活动令牌索引必须带过滤条件 RevokedAtUtc IS NULL：没有过滤就变成「令牌永久唯一」，换人登录时撤销旧行再新建会撞唯一键。
-- filter_definition 的括号与方括号写法由 SQL Server 规范化，这里去掉括号与方括号后再比较。
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.AppPushDevice')
      AND i.[name] = N'UX_AppPushDevice_ActiveToken'
      AND i.[is_unique] = 1
      AND i.[has_filter] = 1
      AND REPLACE(REPLACE(REPLACE(REPLACE(i.[filter_definition], N'[', N''), N']', N''), N'(', N''), N')', N'') = N'RevokedAtUtc IS NULL'
)
    THROW 53503, N'App push active-token index filter is incompatible.', 1;
""";
}
