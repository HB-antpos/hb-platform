namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// MYOB 历史工资单（迁移 20261029.003-myob-payslip；方案 docs/plans/2026-10-29-myob-payslip-history.md 第 3.4、4 节）：
/// - dbo.MyobPayslipViewLog：谁在何时打开了谁的哪一期工资单。只记「操作人、公司文件、被查看员工（我方员工 GUID，未绑定时为空）、
///   MYOB 员工卡 UID、支付日期、时间」，不记金额、姓名或任何工资内容。(ConnectionGuid, MyobEmployeeUid, ViewedAt DESC) 索引服务于
///   「某名员工的工资单被谁看过」的查询。
/// - 新权限码 Myob.Payslip.ViewManagedStore / Myob.Payslip.ViewAllStores 幂等写入 HbwebSysPermissions，不授予任何角色（生产手动授权）。
/// 只新建一张表并插入两行权限，不触碰任何既有表；回退只需 DROP 该表、删除两条权限行并移除迁移账本行。
/// 实体不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// 错误号自成一段 54370–54379：54370 表缺失、54371 列签名、54372 主键、54373 查询索引。
/// 索引为非过滤索引，手工用 sqlcmd 执行无需特殊开关。生产须显式执行 --schema=migrate，且要先于新版主 API 上线。
/// </summary>
internal static class MyobPayslipSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.MyobPayslipViewLog', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[MyobPayslipViewLog]
        (
            [LogGuid] nvarchar(50) NOT NULL,
            [ActorUserGuid] nvarchar(50) NOT NULL,
            [ConnectionGuid] nvarchar(50) NOT NULL,
            [TargetUserGuid] nvarchar(50) NULL,
            [MyobEmployeeUid] nvarchar(50) NOT NULL,
            [PaymentDate] date NOT NULL,
            [ViewedAt] datetime2 NOT NULL,
            CONSTRAINT [PK_MyobPayslipViewLog] PRIMARY KEY CLUSTERED ([LogGuid])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.MyobPayslipViewLog') AND [name] = N'IX_MyobPayslipViewLog_Connection_Employee_ViewedAt')
        CREATE NONCLUSTERED INDEX [IX_MyobPayslipViewLog_Connection_Employee_ViewedAt]
            ON [dbo].[MyobPayslipViewLog] ([ConnectionGuid], [MyobEmployeeUid], [ViewedAt] DESC);

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库；默认不授予任何角色（生产手动授权）。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), [wanted].[Code], [wanted].[Name], N'系统管理', [wanted].[Description],
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        FROM (VALUES
            (N'Myob.Payslip.ViewManagedStore', N'查看管辖分店员工工资单',
             N'Web 页面 /pos-admin/myob「工资单」页签 - 查看管辖分店员工在 MYOB 里的历史工资单（只读）'),
            (N'Myob.Payslip.ViewAllStores', N'查看全部分店员工工资单',
             N'Web 页面 /pos-admin/myob「工资单」页签 - 查看全部分店与公司文件的历史工资单，含未绑定员工（只读）')
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

    // 只读门禁：列签名（类型 / 长度 / 可空性）、主键与查询索引必须精确匹配；不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length：nvarchar 为字节数（字符数 * 2）。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.MyobPayslipViewLog', N'U') IS NULL
    THROW 54370, N'MYOB payslip view log table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.MyobPayslipViewLog', N'LogGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobPayslipViewLog', N'ActorUserGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobPayslipViewLog', N'ConnectionGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobPayslipViewLog', N'TargetUserGuid', N'nvarchar', 100, 1),
      (N'dbo.MyobPayslipViewLog', N'MyobEmployeeUid', N'nvarchar', 100, 0),
      (N'dbo.MyobPayslipViewLog', N'PaymentDate', N'date', 3, 0),
      (N'dbo.MyobPayslipViewLog', N'ViewedAt', N'datetime2', 8, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54371, N'MYOB payslip view log column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.MyobPayslipViewLog') AND [type] = N'PK')
    THROW 54372, N'MYOB payslip view log primary key is missing.', 1;

-- 查询索引必须存在、非过滤，且键列恰好是 (ConnectionGuid ASC, MyobEmployeeUid ASC, ViewedAt DESC)。
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.MyobPayslipViewLog')
      AND i.[name] = N'IX_MyobPayslipViewLog_Connection_Employee_ViewedAt'
      AND i.[has_filter] = 0
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 3
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1 AND c.[name] = N'ConnectionGuid' AND ic.[is_descending_key] = 0)
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 2 AND c.[name] = N'MyobEmployeeUid' AND ic.[is_descending_key] = 0)
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 3 AND c.[name] = N'ViewedAt' AND ic.[is_descending_key] = 1)
)
    THROW 54373, N'MYOB payslip view log query index is missing or incompatible.', 1;
""";
}
