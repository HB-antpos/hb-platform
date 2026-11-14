namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 收银端设置下发（迁移 20261114.001-pos-settings）：
/// - PosSettingValue：每层每项已设置的值（全局 / 门店 / 设备），主键 (Scope, ScopeKey, SettingKey)；
/// - PosSettingChange：只追加的修改记录；
/// - PosSettingAck：每台设备一行的回执（已应用指纹、实际生效值、离线临时修改次数），由 Hbpos.Api upsert。
/// 同时把两个权限码 PosSettings.View / PosSettings.Manage 幂等写入 HbwebSysPermissions，不授予任何角色。
/// 只新建表，不触碰任何既有表；回退只需删除三张表并移除迁移账本行。实体不进 SqlSugarContext 的 tableTypes。
/// 错误号 54490–54494：54490 表缺失、54491 列签名、54492 主键、54493 CHECK、54494 权限码缺失。
/// 生产须显式执行 --schema=migrate，且要先于 POS API 与主 API 上线。
/// </summary>
internal static class PosSettingsSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.PosSettingValue', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PosSettingValue]
        (
            [Scope] nvarchar(16) NOT NULL,
            [ScopeKey] nvarchar(100) NOT NULL,
            [SettingKey] nvarchar(100) NOT NULL,
            [Value] bit NOT NULL,
            [UpdatedAtUtc] datetime2 NOT NULL,
            [UpdatedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_PosSettingValue] PRIMARY KEY CLUSTERED ([Scope], [ScopeKey], [SettingKey]),
            CONSTRAINT [CK_PosSettingValue_Scope] CHECK ([Scope] IN (N'global', N'store', N'device'))
        );
    END;

    IF OBJECT_ID(N'dbo.PosSettingChange', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PosSettingChange]
        (
            [Id] bigint IDENTITY(1,1) NOT NULL,
            [Scope] nvarchar(16) NOT NULL,
            [ScopeKey] nvarchar(100) NOT NULL,
            [SettingKey] nvarchar(100) NOT NULL,
            [OldValue] bit NULL,
            [NewValue] bit NULL,
            [ChangedAtUtc] datetime2 NOT NULL,
            [ChangedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_PosSettingChange] PRIMARY KEY CLUSTERED ([Id])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PosSettingChange') AND [name] = N'IX_PosSettingChange_Scope'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_PosSettingChange_Scope]
            ON [dbo].[PosSettingChange] ([Scope], [ScopeKey], [ChangedAtUtc] DESC);
    END;

    IF OBJECT_ID(N'dbo.PosSettingAck', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PosSettingAck]
        (
            [DeviceCode] nvarchar(100) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [AppliedHash] nvarchar(64) NOT NULL,
            [EffectiveJson] nvarchar(max) NULL,
            [ClientKind] nvarchar(16) NOT NULL,
            [AppVersion] nvarchar(64) NULL,
            [AppliedAtUtc] datetime2 NOT NULL,
            [OfflineOverrideCount] int NOT NULL CONSTRAINT [DF_PosSettingAck_OfflineOverrideCount] DEFAULT (0),
            [LastOfflineOverrideAtUtc] datetime2 NULL,
            CONSTRAINT [PK_PosSettingAck] PRIMARY KEY CLUSTERED ([DeviceCode])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PosSettingAck') AND [name] = N'IX_PosSettingAck_StoreCode'
    )
    BEGIN
        -- 后台按门店统计「设备应用情况」时按 StoreCode 查回执。
        CREATE NONCLUSTERED INDEX [IX_PosSettingAck_StoreCode] ON [dbo].[PosSettingAck] ([StoreCode]);
    END;

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库；默认不授予任何角色（生产手动授权）。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), [wanted].[Code], [wanted].[Name], N'POS 管理', [wanted].[Description],
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        FROM (VALUES
            (N'PosSettings.View', N'查看收银端设置',
             N'Web 页面 /system/pos-settings 与员工 App「收银端设置」- 查看全局、门店、设备的收银端设置与设备应用情况'),
            (N'PosSettings.Manage', N'管理收银端设置',
             N'Web 页面 /system/pos-settings 与员工 App「收银端设置」- 修改全局、门店、设备的收银端设置并下发到收银机')
        ) AS [wanted]([Code], [Name], [Description])
        WHERE NOT EXISTS (
            SELECT 1 FROM [dbo].[HbwebSysPermissions] AS [existing] WHERE [existing].[Code] = [wanted].[Code]
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：三张表的列签名（类型 / 长度 / 可空性）、主键与 Scope CHECK 必须精确匹配，权限码必须在库；
    // 不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length：nvarchar 为字节数（字符数 * 2），nvarchar(max) 为 -1。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.PosSettingValue', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PosSettingChange', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PosSettingAck', N'U') IS NULL
    THROW 54490, N'POS settings tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.PosSettingValue', N'Scope', N'nvarchar', 32, 0),
      (N'dbo.PosSettingValue', N'ScopeKey', N'nvarchar', 200, 0),
      (N'dbo.PosSettingValue', N'SettingKey', N'nvarchar', 200, 0),
      (N'dbo.PosSettingValue', N'Value', N'bit', 1, 0),
      (N'dbo.PosSettingValue', N'UpdatedAtUtc', N'datetime2', 8, 0),
      (N'dbo.PosSettingValue', N'UpdatedBy', N'nvarchar', 200, 1),
      (N'dbo.PosSettingChange', N'Id', N'bigint', 8, 0),
      (N'dbo.PosSettingChange', N'Scope', N'nvarchar', 32, 0),
      (N'dbo.PosSettingChange', N'ScopeKey', N'nvarchar', 200, 0),
      (N'dbo.PosSettingChange', N'SettingKey', N'nvarchar', 200, 0),
      (N'dbo.PosSettingChange', N'OldValue', N'bit', 1, 1),
      (N'dbo.PosSettingChange', N'NewValue', N'bit', 1, 1),
      (N'dbo.PosSettingChange', N'ChangedAtUtc', N'datetime2', 8, 0),
      (N'dbo.PosSettingChange', N'ChangedBy', N'nvarchar', 200, 1),
      (N'dbo.PosSettingAck', N'DeviceCode', N'nvarchar', 200, 0),
      (N'dbo.PosSettingAck', N'StoreCode', N'nvarchar', 100, 0),
      (N'dbo.PosSettingAck', N'AppliedHash', N'nvarchar', 128, 0),
      (N'dbo.PosSettingAck', N'EffectiveJson', N'nvarchar', -1, 1),
      (N'dbo.PosSettingAck', N'ClientKind', N'nvarchar', 32, 0),
      (N'dbo.PosSettingAck', N'AppVersion', N'nvarchar', 128, 1),
      (N'dbo.PosSettingAck', N'AppliedAtUtc', N'datetime2', 8, 0),
      (N'dbo.PosSettingAck', N'OfflineOverrideCount', N'int', 4, 0),
      (N'dbo.PosSettingAck', N'LastOfflineOverrideAtUtc', N'datetime2', 8, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54491, N'POS settings column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PosSettingValue') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PosSettingChange') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PosSettingAck') AND [type] = N'PK')
    THROW 54492, N'POS settings primary keys are missing.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_PosSettingValue_Scope'
      AND [parent_object_id] = OBJECT_ID(N'dbo.PosSettingValue')
      AND [is_disabled] = 0)
    THROW 54493, N'POS settings scope check constraint is missing or disabled.', 1;

IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
   AND (SELECT COUNT(DISTINCT [Code]) FROM [dbo].[HbwebSysPermissions]
        WHERE [IsDeleted] = 0 AND [Code] IN (N'PosSettings.View', N'PosSettings.Manage')) <> 2
    THROW 54494, N'POS settings permission codes are missing.', 1;
""";
}
