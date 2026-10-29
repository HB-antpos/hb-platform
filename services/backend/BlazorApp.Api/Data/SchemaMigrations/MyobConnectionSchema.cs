namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// MYOB 对接阶段 0（迁移 20261027.002-myob-connection；方案 docs/plans/2026-10-27-myob-payroll-integration.md 第 4、5 节）：
/// - dbo.MyobConnection：一个 ABN 的 MYOB 公司文件一条。BusinessId（即 API 路径里的 cf_uri）唯一；
///   RefreshTokenProtected 为 Data Protection 加密后的 refresh token（断开时置 NULL）；access token 只放内存缓存，不入库；
/// - dbo.MyobStoreMapping：分店 → 连接（分店所属公司，即默认发薪公司）。StoreCode 为主键，一个分店只对应一个连接。
/// 只新建表，不触碰任何既有表；回退只需删除两张表并移除迁移账本行。实体不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// 同时把新权限码 Myob.Connection.Manage 幂等写入 HbwebSysPermissions，不授予任何角色。
/// 错误号自成一段 54330–54339：54330 表缺失、54331 列签名、54332 主键、54333 唯一索引、54334 Status CHECK。
/// 生产须显式执行 --schema=migrate，且要先于新版主 API 上线（实体会查这些表）。
/// </summary>
internal static class MyobConnectionSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.MyobConnection', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[MyobConnection]
        (
            [ConnectionGuid] nvarchar(50) NOT NULL,
            [BusinessId] nvarchar(50) NOT NULL,
            [BusinessName] nvarchar(200) NOT NULL,
            [Abn] nvarchar(14) NULL,
            [RefreshTokenProtected] nvarchar(max) NULL,
            [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_MyobConnection_Status] DEFAULT (N'Active'),
            [AuthorizedByUserGuid] nvarchar(50) NULL,
            [AuthorizedAt] datetime2 NOT NULL,
            [LastRefreshedAt] datetime2 NULL,
            [LastError] nvarchar(500) NULL,
            [CreatedAt] datetime2 NOT NULL,
            [UpdatedAt] datetime2 NULL,
            CONSTRAINT [PK_MyobConnection] PRIMARY KEY CLUSTERED ([ConnectionGuid]),
            CONSTRAINT [CK_MyobConnection_Status] CHECK ([Status] IN (N'Active', N'NeedsReauth', N'Disconnected'))
        );
    END;
    -- 同一个公司文件只会有一条连接（重新授权 = 更新这一条，不新增）。
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.MyobConnection') AND [name] = N'UX_MyobConnection_BusinessId')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_MyobConnection_BusinessId]
            ON [dbo].[MyobConnection] ([BusinessId]);

    IF OBJECT_ID(N'dbo.MyobStoreMapping', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[MyobStoreMapping]
        (
            [StoreCode] nvarchar(50) NOT NULL,
            [ConnectionGuid] nvarchar(50) NOT NULL,
            [MappedBy] nvarchar(50) NULL,
            [MappedAt] datetime2 NOT NULL,
            CONSTRAINT [PK_MyobStoreMapping] PRIMARY KEY CLUSTERED ([StoreCode])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.MyobStoreMapping') AND [name] = N'IX_MyobStoreMapping_ConnectionGuid')
        CREATE NONCLUSTERED INDEX [IX_MyobStoreMapping_ConnectionGuid]
            ON [dbo].[MyobStoreMapping] ([ConnectionGuid]);

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库；默认不授予任何角色（生产手动授权）。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), [wanted].[Code], [wanted].[Name], N'系统管理', [wanted].[Description],
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        FROM (VALUES
            (N'Myob.Connection.Manage', N'管理 MYOB 连接',
             N'Web 页面 /pos-admin/myob - 连接 / 断开 MYOB 公司文件、维护分店与公司文件的映射')
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

    // 只读门禁：列签名（类型 / 长度 / 可空性）、主键、唯一索引与 Status CHECK 必须精确匹配；
    // 不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length：nvarchar 为字节数（字符数 * 2），nvarchar(max) 为 -1。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.MyobConnection', N'U') IS NULL
   OR OBJECT_ID(N'dbo.MyobStoreMapping', N'U') IS NULL
    THROW 54330, N'MYOB connection tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.MyobConnection', N'ConnectionGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobConnection', N'BusinessId', N'nvarchar', 100, 0),
      (N'dbo.MyobConnection', N'BusinessName', N'nvarchar', 400, 0),
      (N'dbo.MyobConnection', N'Abn', N'nvarchar', 28, 1),
      (N'dbo.MyobConnection', N'RefreshTokenProtected', N'nvarchar', -1, 1),
      (N'dbo.MyobConnection', N'Status', N'nvarchar', 40, 0),
      (N'dbo.MyobConnection', N'AuthorizedByUserGuid', N'nvarchar', 100, 1),
      (N'dbo.MyobConnection', N'AuthorizedAt', N'datetime2', 8, 0),
      (N'dbo.MyobConnection', N'LastRefreshedAt', N'datetime2', 8, 1),
      (N'dbo.MyobConnection', N'LastError', N'nvarchar', 1000, 1),
      (N'dbo.MyobConnection', N'CreatedAt', N'datetime2', 8, 0),
      (N'dbo.MyobConnection', N'UpdatedAt', N'datetime2', 8, 1),
      (N'dbo.MyobStoreMapping', N'StoreCode', N'nvarchar', 100, 0),
      (N'dbo.MyobStoreMapping', N'ConnectionGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobStoreMapping', N'MappedBy', N'nvarchar', 100, 1),
      (N'dbo.MyobStoreMapping', N'MappedAt', N'datetime2', 8, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54331, N'MYOB connection column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.MyobConnection') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.MyobStoreMapping') AND [type] = N'PK')
    THROW 54332, N'MYOB connection primary keys are missing.', 1;

-- BusinessId 唯一索引必须存在、唯一且只有一个键列：重新授权按它找回同一条连接，防止同一公司文件出现两条记录。
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.MyobConnection')
      AND i.[name] = N'UX_MyobConnection_BusinessId'
      AND i.[is_unique] = 1
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
)
    THROW 54333, N'MYOB connection unique index signature is incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_MyobConnection_Status'
      AND [parent_object_id] = OBJECT_ID(N'dbo.MyobConnection')
      AND [is_disabled] = 0)
    THROW 54334, N'MYOB connection status check constraint is missing or disabled.', 1;
""";
}
