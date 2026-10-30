namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// MYOB Timesheet 推送（迁移 20261030.001-myob-timesheet-push，方案 docs/plans/2026-10-29-myob-timesheet-push.md 第 4.4 节）：
/// - 新表 dbo.MyobTimesheetPush —— 每个员工每次推送 / 跳过 / 撤销一行。BeforeJson（推送前 MYOB 里的内容）与 AfterJson（我们写入的内容）
///   只含「工资类别 UID、日期、小时」，不含姓名、金额或 MYOB 响应体；撤销就靠 BeforeJson 把 MYOB 恢复原样（实测 PUT 是整体覆盖，DELETE 返回 405）。
/// - 索引：(ConnectionGuid, PeriodStart, UserGuid, StartedAt DESC) 查最近一次；(BatchGuid) 查批次；
///   过滤唯一索引 UX_MyobTimesheetPush_Running 保证同一 (公司文件, 周期) 同一时刻最多一行 Status = Running，
///   两个人同时点推送时第二个插入会失败，服务据此回 PUSH_IN_PROGRESS。
/// - 新权限码 Myob.Timesheet.Push 幂等写入 HbwebSysPermissions，不授予任何角色（生产手动授权给财务账号）。
/// 只新建表并插入一行权限，不触碰既有表；回退 = DROP 该表 + 删这条权限行 + 移除迁移账本行（旧版主 API 不读这张表，先回滚镜像即可）。
/// 实体不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// 错误号自成一段 54400–54409：54400 表缺失、54401 列签名、54402 主键、54403 最近一次索引、54404 批次索引、54405 Running 过滤唯一索引。
/// 过滤索引要求写入会话 QUOTED_IDENTIFIER ON：应用连接默认满足；手工用 sqlcmd 执行请加 -I。
/// 生产须显式执行 --schema=migrate，且要先于新版主 API 上线。
/// </summary>
internal static class MyobTimesheetPushSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.MyobTimesheetPush', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[MyobTimesheetPush]
        (
            [PushGuid] nvarchar(50) NOT NULL,
            [BatchGuid] nvarchar(50) NOT NULL,
            [ConnectionGuid] nvarchar(50) NOT NULL,
            [PeriodStart] datetime2 NOT NULL,
            [Weeks] int NOT NULL,
            [UserGuid] nvarchar(50) NOT NULL,
            [MyobEmployeeUid] nvarchar(50) NOT NULL,
            [Status] nvarchar(30) NOT NULL,
            [RequestHash] nvarchar(64) NULL,
            [PushedMinutes] int NOT NULL,
            [BeforeJson] nvarchar(max) NULL,
            [AfterJson] nvarchar(max) NULL,
            [ErrorCode] nvarchar(80) NULL,
            [ErrorStatus] int NULL,
            [ActorUserGuid] nvarchar(50) NULL,
            [StartedAt] datetime2 NOT NULL,
            [FinishedAt] datetime2 NULL,
            [UndoneAt] datetime2 NULL,
            [UndoneByUserGuid] nvarchar(50) NULL,
            [UndoNote] nvarchar(200) NULL,
            CONSTRAINT [PK_MyobTimesheetPush] PRIMARY KEY CLUSTERED ([PushGuid])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.MyobTimesheetPush') AND [name] = N'IX_MyobTimesheetPush_Latest')
        CREATE NONCLUSTERED INDEX [IX_MyobTimesheetPush_Latest]
            ON [dbo].[MyobTimesheetPush] ([ConnectionGuid], [PeriodStart], [UserGuid], [StartedAt] DESC);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.MyobTimesheetPush') AND [name] = N'IX_MyobTimesheetPush_Batch')
        CREATE NONCLUSTERED INDEX [IX_MyobTimesheetPush_Batch]
            ON [dbo].[MyobTimesheetPush] ([BatchGuid]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.MyobTimesheetPush') AND [name] = N'UX_MyobTimesheetPush_Running')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_MyobTimesheetPush_Running]
            ON [dbo].[MyobTimesheetPush] ([ConnectionGuid], [PeriodStart])
            WHERE [Status] = N'Running';

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库；默认不授予任何角色（生产手动授权）。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), [wanted].[Code], [wanted].[Name], N'系统管理', [wanted].[Description],
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        FROM (VALUES
            (N'Myob.Timesheet.Push', N'推送工时到 MYOB',
             N'Web 页面 /pos-admin/myob「推送与对账」页签 - 把工资周期的转账工时推送到 MYOB Timesheet，并可按批次撤销（会写 MYOB 生产数据）')
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

    // 只读门禁：列签名（类型 / 长度 / 可空性）、主键与三个索引必须精确匹配；不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length：nvarchar 为字节数（字符数 * 2），nvarchar(max) 为 -1。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.MyobTimesheetPush', N'U') IS NULL
    THROW 54400, N'MYOB timesheet push table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.MyobTimesheetPush', N'PushGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobTimesheetPush', N'BatchGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobTimesheetPush', N'ConnectionGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobTimesheetPush', N'PeriodStart', N'datetime2', 8, 0),
      (N'dbo.MyobTimesheetPush', N'Weeks', N'int', 4, 0),
      (N'dbo.MyobTimesheetPush', N'UserGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobTimesheetPush', N'MyobEmployeeUid', N'nvarchar', 100, 0),
      (N'dbo.MyobTimesheetPush', N'Status', N'nvarchar', 60, 0),
      (N'dbo.MyobTimesheetPush', N'RequestHash', N'nvarchar', 128, 1),
      (N'dbo.MyobTimesheetPush', N'PushedMinutes', N'int', 4, 0),
      (N'dbo.MyobTimesheetPush', N'BeforeJson', N'nvarchar', -1, 1),
      (N'dbo.MyobTimesheetPush', N'AfterJson', N'nvarchar', -1, 1),
      (N'dbo.MyobTimesheetPush', N'ErrorCode', N'nvarchar', 160, 1),
      (N'dbo.MyobTimesheetPush', N'ErrorStatus', N'int', 4, 1),
      (N'dbo.MyobTimesheetPush', N'ActorUserGuid', N'nvarchar', 100, 1),
      (N'dbo.MyobTimesheetPush', N'StartedAt', N'datetime2', 8, 0),
      (N'dbo.MyobTimesheetPush', N'FinishedAt', N'datetime2', 8, 1),
      (N'dbo.MyobTimesheetPush', N'UndoneAt', N'datetime2', 8, 1),
      (N'dbo.MyobTimesheetPush', N'UndoneByUserGuid', N'nvarchar', 100, 1),
      (N'dbo.MyobTimesheetPush', N'UndoNote', N'nvarchar', 400, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54401, N'MYOB timesheet push column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.MyobTimesheetPush') AND [type] = N'PK')
    THROW 54402, N'MYOB timesheet push primary key is missing.', 1;

-- 最近一次索引：非过滤、非唯一，键列恰好是 (ConnectionGuid, PeriodStart, UserGuid ASC, StartedAt DESC)。
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.MyobTimesheetPush')
      AND i.[name] = N'IX_MyobTimesheetPush_Latest'
      AND i.[has_filter] = 0
      AND i.[is_unique] = 0
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 4
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1 AND c.[name] = N'ConnectionGuid' AND ic.[is_descending_key] = 0)
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 2 AND c.[name] = N'PeriodStart' AND ic.[is_descending_key] = 0)
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 3 AND c.[name] = N'UserGuid' AND ic.[is_descending_key] = 0)
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 4 AND c.[name] = N'StartedAt' AND ic.[is_descending_key] = 1)
)
    THROW 54403, N'MYOB timesheet push latest-record index is missing or incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.MyobTimesheetPush')
      AND i.[name] = N'IX_MyobTimesheetPush_Batch'
      AND i.[has_filter] = 0
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1 AND c.[name] = N'BatchGuid')
)
    THROW 54404, N'MYOB timesheet push batch index is missing or incompatible.', 1;

-- Running 过滤唯一索引：唯一、带过滤条件，键列恰好是 (ConnectionGuid, PeriodStart)；它是「同一周期同时只有一个推送在跑」的并发保险。
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.MyobTimesheetPush')
      AND i.[name] = N'UX_MyobTimesheetPush_Running'
      AND i.[is_unique] = 1
      AND i.[has_filter] = 1
      -- 过滤条件被 SQL Server 存成 ([Status]=N'Running')：去掉括号后必须恰好是 Status=N'Running'。
      AND REPLACE(REPLACE(REPLACE(REPLACE(i.[filter_definition], N'(', N''), N')', N''), N'[', N''), N']', N'') = N'Status=N''Running'''
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 2
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1 AND c.[name] = N'ConnectionGuid')
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 2 AND c.[name] = N'PeriodStart')
)
    THROW 54405, N'MYOB timesheet push running unique index is missing or incompatible.', 1;
""";
}
