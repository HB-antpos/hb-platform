namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// MYOB 对接阶段 1（迁移 20261027.003-myob-employee-binding；方案 docs/plans/2026-10-27-myob-payroll-integration.md 第 4、6、7 节）：
/// - dbo.EmployeeMyobLink：员工 ↔ MYOB 员工卡（软解绑）。两个**过滤唯一索引**（UnboundAt IS NULL）保证「一个员工在一个公司文件里只有一张有效卡、
///   一张卡只绑一个员工」，已解除的历史行不受限；
/// - dbo.EmployeePayrollEmployer：发薪公司覆盖（员工 × 分店 × 生效日期，只新增不改），(UserGuid, StoreCode, EffectiveFrom) 唯一；
/// - dbo.MyobEmployeeIgnore：「MYOB 有、我方没有」名单里被忽略的员工卡。
/// 只新建表，不触碰任何既有表；回退只需删除三张表并移除迁移账本行。实体不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// 同时把新权限码 Myob.Employee.Bind 幂等写入 HbwebSysPermissions，不授予任何角色。
/// 错误号自成一段 54340–54349：54340 表缺失、54341 列签名、54342 主键、54343 绑定过滤唯一索引、54344 发薪公司唯一索引、54345 MatchMethod CHECK。
/// 含过滤索引：手工用 sqlcmd 执行必须加 -I（QUOTED_IDENTIFIER ON）。生产须显式执行 --schema=migrate，且要先于新版主 API 上线。
/// </summary>
internal static class MyobEmployeeBindingSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.EmployeeMyobLink', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[EmployeeMyobLink]
        (
            [LinkGuid] nvarchar(50) NOT NULL,
            [UserGuid] nvarchar(50) NOT NULL,
            [ConnectionGuid] nvarchar(50) NOT NULL,
            [MyobEmployeeUid] nvarchar(50) NOT NULL,
            [MyobName] nvarchar(200) NULL,
            [MatchMethod] nvarchar(20) NOT NULL CONSTRAINT [DF_EmployeeMyobLink_MatchMethod] DEFAULT (N'Manual'),
            [MatchScore] int NULL,
            [BoundByUserGuid] nvarchar(50) NULL,
            [BoundAt] datetime2 NOT NULL,
            [UnboundAt] datetime2 NULL,
            [UnboundByUserGuid] nvarchar(50) NULL,
            CONSTRAINT [PK_EmployeeMyobLink] PRIMARY KEY CLUSTERED ([LinkGuid]),
            CONSTRAINT [CK_EmployeeMyobLink_MatchMethod] CHECK ([MatchMethod] IN (N'Suggested', N'Manual'))
        );
    END;
    -- 同一员工在同一公司文件里同时只能有一张有效卡；已解除（UnboundAt 非空）的历史行不参与。
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeMyobLink') AND [name] = N'UX_EmployeeMyobLink_User_Connection_Active')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_EmployeeMyobLink_User_Connection_Active]
            ON [dbo].[EmployeeMyobLink] ([UserGuid], [ConnectionGuid])
            WHERE [UnboundAt] IS NULL;
    -- 同一张 MYOB 员工卡同时只能绑给一个员工。
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeMyobLink') AND [name] = N'UX_EmployeeMyobLink_Connection_Uid_Active')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_EmployeeMyobLink_Connection_Uid_Active]
            ON [dbo].[EmployeeMyobLink] ([ConnectionGuid], [MyobEmployeeUid])
            WHERE [UnboundAt] IS NULL;

    IF OBJECT_ID(N'dbo.EmployeePayrollEmployer', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[EmployeePayrollEmployer]
        (
            [EmployerGuid] nvarchar(50) NOT NULL,
            [UserGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [ConnectionGuid] nvarchar(50) NULL,
            [EffectiveFrom] date NOT NULL,
            [CreatedByUserGuid] nvarchar(50) NULL,
            [CreatedAt] datetime2 NOT NULL,
            CONSTRAINT [PK_EmployeePayrollEmployer] PRIMARY KEY CLUSTERED ([EmployerGuid])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeePayrollEmployer') AND [name] = N'UX_EmployeePayrollEmployer_User_Store_From')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_EmployeePayrollEmployer_User_Store_From]
            ON [dbo].[EmployeePayrollEmployer] ([UserGuid], [StoreCode], [EffectiveFrom]);

    IF OBJECT_ID(N'dbo.MyobEmployeeIgnore', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[MyobEmployeeIgnore]
        (
            [ConnectionGuid] nvarchar(50) NOT NULL,
            [MyobEmployeeUid] nvarchar(50) NOT NULL,
            [IgnoredByUserGuid] nvarchar(50) NULL,
            [IgnoredAt] datetime2 NOT NULL,
            CONSTRAINT [PK_MyobEmployeeIgnore] PRIMARY KEY CLUSTERED ([ConnectionGuid], [MyobEmployeeUid])
        );
    END;

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库；默认不授予任何角色（生产手动授权）。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), [wanted].[Code], [wanted].[Name], N'系统管理', [wanted].[Description],
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        FROM (VALUES
            (N'Myob.Employee.Bind', N'绑定 MYOB 员工',
             N'Web 页面 /pos-admin/myob「员工绑定」页签 - 把我方员工与 MYOB 员工卡配对、解除绑定')
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

    // 只读门禁：列签名（类型 / 长度 / 可空性）、主键、两个过滤唯一索引、发薪公司唯一索引与 MatchMethod CHECK 必须精确匹配；
    // 不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length：nvarchar 为字节数（字符数 * 2）。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.EmployeeMyobLink', N'U') IS NULL
   OR OBJECT_ID(N'dbo.EmployeePayrollEmployer', N'U') IS NULL
   OR OBJECT_ID(N'dbo.MyobEmployeeIgnore', N'U') IS NULL
    THROW 54340, N'MYOB employee binding tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.EmployeeMyobLink', N'LinkGuid', N'nvarchar', 100, 0),
      (N'dbo.EmployeeMyobLink', N'UserGuid', N'nvarchar', 100, 0),
      (N'dbo.EmployeeMyobLink', N'ConnectionGuid', N'nvarchar', 100, 0),
      (N'dbo.EmployeeMyobLink', N'MyobEmployeeUid', N'nvarchar', 100, 0),
      (N'dbo.EmployeeMyobLink', N'MyobName', N'nvarchar', 400, 1),
      (N'dbo.EmployeeMyobLink', N'MatchMethod', N'nvarchar', 40, 0),
      (N'dbo.EmployeeMyobLink', N'MatchScore', N'int', 4, 1),
      (N'dbo.EmployeeMyobLink', N'BoundByUserGuid', N'nvarchar', 100, 1),
      (N'dbo.EmployeeMyobLink', N'BoundAt', N'datetime2', 8, 0),
      (N'dbo.EmployeeMyobLink', N'UnboundAt', N'datetime2', 8, 1),
      (N'dbo.EmployeeMyobLink', N'UnboundByUserGuid', N'nvarchar', 100, 1),
      (N'dbo.EmployeePayrollEmployer', N'EmployerGuid', N'nvarchar', 100, 0),
      (N'dbo.EmployeePayrollEmployer', N'UserGuid', N'nvarchar', 100, 0),
      (N'dbo.EmployeePayrollEmployer', N'StoreCode', N'nvarchar', 100, 0),
      (N'dbo.EmployeePayrollEmployer', N'ConnectionGuid', N'nvarchar', 100, 1),
      (N'dbo.EmployeePayrollEmployer', N'EffectiveFrom', N'date', 3, 0),
      (N'dbo.EmployeePayrollEmployer', N'CreatedByUserGuid', N'nvarchar', 100, 1),
      (N'dbo.EmployeePayrollEmployer', N'CreatedAt', N'datetime2', 8, 0),
      (N'dbo.MyobEmployeeIgnore', N'ConnectionGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobEmployeeIgnore', N'MyobEmployeeUid', N'nvarchar', 100, 0),
      (N'dbo.MyobEmployeeIgnore', N'IgnoredByUserGuid', N'nvarchar', 100, 1),
      (N'dbo.MyobEmployeeIgnore', N'IgnoredAt', N'datetime2', 8, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54341, N'MYOB employee binding column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.EmployeeMyobLink') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.EmployeePayrollEmployer') AND [type] = N'PK')
 OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.MyobEmployeeIgnore') AND [type] = N'PK')
    THROW 54342, N'MYOB employee binding primary keys are missing.', 1;

-- 两个绑定唯一索引必须是「唯一 + 过滤（WHERE UnboundAt IS NULL）+ 恰好两个键列」：
-- 缺过滤会让「解绑后重新绑定」被历史行挡住；缺唯一会允许同一张卡绑给两个人。
IF (SELECT COUNT(*) FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.EmployeeMyobLink')
      AND i.[name] IN (N'UX_EmployeeMyobLink_User_Connection_Active', N'UX_EmployeeMyobLink_Connection_Uid_Active')
      AND i.[is_unique] = 1
      AND i.[has_filter] = 1
      AND i.[filter_definition] LIKE N'%UnboundAt%IS NULL%'
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 2) <> 2
    THROW 54343, N'MYOB employee link filtered unique indexes are missing or incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.EmployeePayrollEmployer')
      AND i.[name] = N'UX_EmployeePayrollEmployer_User_Store_From'
      AND i.[is_unique] = 1
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 3
)
    THROW 54344, N'MYOB payroll employer unique index is missing or incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_EmployeeMyobLink_MatchMethod'
      AND [parent_object_id] = OBJECT_ID(N'dbo.EmployeeMyobLink')
      AND [is_disabled] = 0)
    THROW 54345, N'MYOB employee link match method check constraint is missing or disabled.', 1;
""";
}
