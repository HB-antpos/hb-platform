namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// MYOB 对接：「从 MYOB 同步资料到我方」撤销快照批次表（迁移 20261029.001-myob-employee-sync-batch）：
/// dbo.MyobEmployeeSyncBatch —— 每次同步一行，ChangesJson 保存本批全部改动的撤销快照（[{userGuid,field,before,after}]），
/// 用于整批撤销；UndoneAt / UndoneByUserGuid 非空表示该批已撤销。(ConnectionGuid, CreatedAt DESC) 索引服务于「某公司文件最近的同步批次」查询。
/// 只新建表，不触碰任何既有表，也不新增权限码；回退只需 DROP 该表并移除迁移账本行。
/// 实体不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// 错误号自成一段 54360–54369：54360 表缺失、54361 列签名、54362 主键、54363 查询索引。
/// （54350–54359 已被 MYOB 工资类别映射 MyobPayCategorySchema 占用，故顺延。）
/// 含大对象列 nvarchar(max)，索引为非过滤索引，手工用 sqlcmd 执行无需特殊开关。
/// 生产须显式执行 --schema=migrate，且要先于新版主 API 上线。
/// </summary>
internal static class MyobEmployeeSyncBatchSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.MyobEmployeeSyncBatch', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[MyobEmployeeSyncBatch]
        (
            [BatchGuid] nvarchar(50) NOT NULL,
            [ConnectionGuid] nvarchar(50) NOT NULL,
            [ActorUserGuid] nvarchar(50) NULL,
            [CreatedAt] datetime2 NOT NULL,
            [EmployeeCount] int NOT NULL,
            [ChangeCount] int NOT NULL,
            [SensitiveCount] int NOT NULL,
            [ChangesJson] nvarchar(max) NOT NULL,
            [UndoneAt] datetime2 NULL,
            [UndoneByUserGuid] nvarchar(50) NULL,
            CONSTRAINT [PK_MyobEmployeeSyncBatch] PRIMARY KEY CLUSTERED ([BatchGuid])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.MyobEmployeeSyncBatch') AND [name] = N'IX_MyobEmployeeSyncBatch_Connection_CreatedAt')
        CREATE NONCLUSTERED INDEX [IX_MyobEmployeeSyncBatch_Connection_CreatedAt]
            ON [dbo].[MyobEmployeeSyncBatch] ([ConnectionGuid], [CreatedAt] DESC);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：列签名（类型 / 长度 / 可空性）、主键与查询索引必须精确匹配；不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length：nvarchar 为字节数（字符数 * 2），nvarchar(max) 为 -1。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.MyobEmployeeSyncBatch', N'U') IS NULL
    THROW 54360, N'MYOB employee sync batch table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.MyobEmployeeSyncBatch', N'BatchGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobEmployeeSyncBatch', N'ConnectionGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobEmployeeSyncBatch', N'ActorUserGuid', N'nvarchar', 100, 1),
      (N'dbo.MyobEmployeeSyncBatch', N'CreatedAt', N'datetime2', 8, 0),
      (N'dbo.MyobEmployeeSyncBatch', N'EmployeeCount', N'int', 4, 0),
      (N'dbo.MyobEmployeeSyncBatch', N'ChangeCount', N'int', 4, 0),
      (N'dbo.MyobEmployeeSyncBatch', N'SensitiveCount', N'int', 4, 0),
      (N'dbo.MyobEmployeeSyncBatch', N'ChangesJson', N'nvarchar', -1, 0),
      (N'dbo.MyobEmployeeSyncBatch', N'UndoneAt', N'datetime2', 8, 1),
      (N'dbo.MyobEmployeeSyncBatch', N'UndoneByUserGuid', N'nvarchar', 100, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54361, N'MYOB employee sync batch column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.MyobEmployeeSyncBatch') AND [type] = N'PK')
    THROW 54362, N'MYOB employee sync batch primary key is missing.', 1;

-- 查询索引必须存在、非过滤，且键列恰好是 (ConnectionGuid ASC, CreatedAt DESC)。
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.MyobEmployeeSyncBatch')
      AND i.[name] = N'IX_MyobEmployeeSyncBatch_Connection_CreatedAt'
      AND i.[has_filter] = 0
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 2
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1 AND c.[name] = N'ConnectionGuid' AND ic.[is_descending_key] = 0)
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 2 AND c.[name] = N'CreatedAt' AND ic.[is_descending_key] = 1)
)
    THROW 54363, N'MYOB employee sync batch query index is missing or incompatible.', 1;
""";
}
