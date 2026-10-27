namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 后台按「分店」设置收银端访问 POS API 的首选入口（edge / aws / direct）：在 dbo.Store 新增三列
/// PosApiEntryMode nvarchar(16) NULL（NULL＝未设置，收银端按 edge 处理）、
/// PosApiEntryUpdatedAt datetime2 NULL（最近修改时间，UTC）、PosApiEntryUpdatedBy nvarchar(64) NULL（最近修改人），
/// 并加 CHECK 约束 CK_Store_PosApiEntryMode 只允许 NULL / 'edge' / 'aws' / 'direct'，任何写入路径都写不进脏值。
/// 只加可空列，历史行不受影响；生产须显式执行 --schema=migrate，且要先于新版主 API / Hbpos.Api 上线
/// （共享 Store 实体会查询这三列；Hbpos.Api 启动时也会幂等补列，但约束只有本迁移会加）。
/// 错误号区间 54100–54101。
/// </summary>
internal static class StorePosApiEntrySchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.Store', N'U') IS NULL
    THROW 54100, N'Store table is missing.', 1;

BEGIN TRANSACTION;
BEGIN TRY
    -- 加可空列只改元数据，不重写大表；COL_LENGTH 判断保证重复执行（含 Hbpos.Api 已先补列）是空操作。
    IF COL_LENGTH(N'dbo.Store', N'PosApiEntryMode') IS NULL
        ALTER TABLE [dbo].[Store] ADD [PosApiEntryMode] nvarchar(16) NULL;
    IF COL_LENGTH(N'dbo.Store', N'PosApiEntryUpdatedAt') IS NULL
        ALTER TABLE [dbo].[Store] ADD [PosApiEntryUpdatedAt] datetime2 NULL;
    IF COL_LENGTH(N'dbo.Store', N'PosApiEntryUpdatedBy') IS NULL
        ALTER TABLE [dbo].[Store] ADD [PosApiEntryUpdatedBy] nvarchar(64) NULL;

    -- 同一批里刚加的列不能被后面的 DDL 直接引用（编译期报列名无效），约束用动态 SQL 在列已存在后创建。
    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE [name] = N'CK_Store_PosApiEntryMode'
          AND [parent_object_id] = OBJECT_ID(N'dbo.Store'))
        EXEC (N'ALTER TABLE [dbo].[Store] ADD CONSTRAINT [CK_Store_PosApiEntryMode]
                CHECK ([PosApiEntryMode] IS NULL OR [PosApiEntryMode] IN (N''edge'', N''aws'', N''direct''))');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：三列都必须存在、类型与可空性精确匹配（nvarchar 的 max_length 按字节计，16→32、64→128），
    // 且 CHECK 约束存在并处于启用状态。已有同名但签名不符的列只能通过显式迁移修复，运行时绝不自动改写。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.Store', N'U') IS NULL
    THROW 54100, N'Store table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'PosApiEntryMode', N'nvarchar', 32, 1),
      (N'PosApiEntryUpdatedAt', N'datetime2', 8, 1),
      (N'PosApiEntryUpdatedBy', N'nvarchar', 128, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.Store') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54101, N'Store POS API entry column signature is incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Store_PosApiEntryMode'
      AND [parent_object_id] = OBJECT_ID(N'dbo.Store')
      AND [is_disabled] = 0)
    THROW 54101, N'Store POS API entry check constraint is missing or disabled.', 1;
""";
}
