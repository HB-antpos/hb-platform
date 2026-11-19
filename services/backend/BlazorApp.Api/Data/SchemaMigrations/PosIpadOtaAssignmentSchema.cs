namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// iPad POS OTA 覆盖层（PosIpadOtaAssignment）：在全局基线（PosIpadOtaRollout）之上，对单个分店或单台设备指定另一个 OTA 版本。
/// 新表走独立版本号迁移，生产须显式执行 --schema=migrate；实体不进 SqlSugarContext 的自动建表清单，
/// 也不放进基线 AppUpdatePolicySchemaMigrator。错误号区间 54700–54704。
/// </summary>
internal static class PosIpadOtaAssignmentSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.PosIpadOtaAssignment', N'U') IS NULL
    BEGIN
        -- 审计列类型与 PosIpadOtaRollout 一致；分店行与设备行靠 CHECK 约束保证目标二选一。
        CREATE TABLE [dbo].[PosIpadOtaAssignment]
        (
            [Id] uniqueidentifier NOT NULL,
            [Environment] nvarchar(32) NOT NULL,
            [TargetType] nvarchar(16) NOT NULL,
            [StoreGuid] nvarchar(100) NULL,
            [DeviceRegistrationId] int NULL,
            [ReleaseId] uniqueidentifier NOT NULL,
            [ForceUpdate] bit NOT NULL,
            [ReleaseMessage] nvarchar(1000) NULL,
            [Note] nvarchar(200) NULL,
            [ExpiresAtUtc] datetime2 NULL,
            [Enabled] bit NOT NULL,
            [PolicyVersion] bigint NOT NULL,
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(max) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(max) NULL,
            [IsDeleted] bit NOT NULL CONSTRAINT [DF_PosIpadOtaAssignment_IsDeleted] DEFAULT(0),
            CONSTRAINT [PK_PosIpadOtaAssignment] PRIMARY KEY CLUSTERED ([Id]),
            CONSTRAINT [CK_PosIpadOtaAssignment_TargetType]
                CHECK ([TargetType] IN (N'store', N'device')),
            CONSTRAINT [CK_PosIpadOtaAssignment_Target]
                CHECK (
                    ([TargetType] = N'store' AND [StoreGuid] IS NOT NULL AND [DeviceRegistrationId] IS NULL)
                    OR ([TargetType] = N'device' AND [StoreGuid] IS NULL AND [DeviceRegistrationId] IS NOT NULL)
                )
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PosIpadOtaAssignment')
          AND [name] = N'UX_PosIpadOtaAssignment_Store'
    )
    BEGIN
        -- 同环境同分店同时只能有一条启用的分店覆盖，并发写入由唯一索引兜底。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_PosIpadOtaAssignment_Store]
            ON [dbo].[PosIpadOtaAssignment] ([Environment], [StoreGuid])
            WHERE [Enabled] = 1 AND [IsDeleted] = 0 AND [TargetType] = N'store';
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PosIpadOtaAssignment')
          AND [name] = N'UX_PosIpadOtaAssignment_Device'
    )
    BEGIN
        -- 同环境同设备注册号同时只能有一条启用的设备覆盖。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_PosIpadOtaAssignment_Device]
            ON [dbo].[PosIpadOtaAssignment] ([Environment], [DeviceRegistrationId])
            WHERE [Enabled] = 1 AND [IsDeleted] = 0 AND [TargetType] = N'device';
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PosIpadOtaAssignment')
          AND [name] = N'UX_PosIpadOtaAssignment_Environment_PolicyVersion'
    )
    BEGIN
        -- 同环境未删除行的策略版本号唯一，客户端按版本号判断是否需要重新拉取。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_PosIpadOtaAssignment_Environment_PolicyVersion]
            ON [dbo].[PosIpadOtaAssignment] ([Environment], [PolicyVersion])
            WHERE [IsDeleted] = 0;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列名、类型、长度（nvarchar 按字节计，max 为 -1）、可空性，主键、两个 CHECK 与三个过滤唯一索引。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.PosIpadOtaAssignment', N'U') IS NULL
    THROW 54700, N'POS iPad OTA assignment table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'Id', N'uniqueidentifier', 0, 0),
      (N'Environment', N'nvarchar', 64, 0),
      (N'TargetType', N'nvarchar', 32, 0),
      (N'StoreGuid', N'nvarchar', 200, 1),
      (N'DeviceRegistrationId', N'int', 0, 1),
      (N'ReleaseId', N'uniqueidentifier', 0, 0),
      (N'ForceUpdate', N'bit', 0, 0),
      (N'ReleaseMessage', N'nvarchar', 2000, 1),
      (N'Note', N'nvarchar', 400, 1),
      (N'ExpiresAtUtc', N'datetime2', 0, 1),
      (N'Enabled', N'bit', 0, 0),
      (N'PolicyVersion', N'bigint', 0, 0),
      (N'CreatedAt', N'datetime2', 0, 0),
      (N'CreatedBy', N'nvarchar', -1, 1),
      (N'UpdatedAt', N'datetime2', 0, 1),
      (N'UpdatedBy', N'nvarchar', -1, 1),
      (N'IsDeleted', N'bit', 0, 0)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.PosIpadOtaAssignment') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54701, N'POS iPad OTA assignment column signature is incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.key_constraints
    WHERE [parent_object_id] = OBJECT_ID(N'dbo.PosIpadOtaAssignment') AND [type] = N'PK'
)
    THROW 54702, N'POS iPad OTA assignment primary key is missing.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [parent_object_id] = OBJECT_ID(N'dbo.PosIpadOtaAssignment')
      AND [name] = N'CK_PosIpadOtaAssignment_TargetType'
      AND [is_disabled] = 0
)
 OR NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [parent_object_id] = OBJECT_ID(N'dbo.PosIpadOtaAssignment')
      AND [name] = N'CK_PosIpadOtaAssignment_Target'
      AND [is_disabled] = 0
)
    THROW 54703, N'POS iPad OTA assignment check constraint is missing or disabled.', 1;

IF 3 <> (
    SELECT COUNT(*)
    FROM (VALUES
      (N'UX_PosIpadOtaAssignment_Store', N'Environment', N'StoreGuid', N'FILTER_STORE'),
      (N'UX_PosIpadOtaAssignment_Device', N'Environment', N'DeviceRegistrationId', N'FILTER_DEVICE'),
      (N'UX_PosIpadOtaAssignment_Environment_PolicyVersion', N'Environment', N'PolicyVersion', N'FILTER_VERSION')
    ) expected([index_name], [key1], [key2], [filter_key])
    INNER JOIN sys.indexes i
        ON i.[object_id] = OBJECT_ID(N'dbo.PosIpadOtaAssignment') AND i.[name] = expected.[index_name]
    WHERE i.[is_unique] = 1
      AND i.[has_filter] = 1
      AND (
          SELECT COUNT(*) FROM sys.index_columns ic
          WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] > 0
      ) = 2
      AND EXISTS (
          SELECT 1 FROM sys.index_columns ic
          INNER JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
          WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
            AND ic.[key_ordinal] = 1 AND c.[name] = expected.[key1]
      )
      AND EXISTS (
          SELECT 1 FROM sys.index_columns ic
          INNER JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
          WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
            AND ic.[key_ordinal] = 2 AND c.[name] = expected.[key2]
      )
      -- 过滤条件按 SQL Server 规范化后的文本比较（已在真实 SQL Server 2022 上核对）。
      AND i.[filter_definition] = CASE expected.[filter_key]
          WHEN N'FILTER_STORE' THEN N'([Enabled]=(1) AND [IsDeleted]=(0) AND [TargetType]=N''store'')'
          WHEN N'FILTER_DEVICE' THEN N'([Enabled]=(1) AND [IsDeleted]=(0) AND [TargetType]=N''device'')'
          ELSE N'([IsDeleted]=(0))'
      END
)
    THROW 54704, N'POS iPad OTA assignment index signature is incompatible.', 1;
""";
}
