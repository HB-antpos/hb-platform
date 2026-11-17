namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// MYOB 工资单本地快照（迁移 20261117.001-myob-payslip-snapshot；方案 docs/plans/2026-10-29-myob-payslip-history.md 第 12 节）：
/// - dbo.MyobPayslipSnapshot：已发薪工资单的本地副本。列表 / 明细 / PDF / 邮件读这张表，不再实时请求 MYOB。
///   (ConnectionGuid, SlipKey) 唯一（SlipKey = 员工卡 + 支付日 + 周期 + 税前 + 实发的哈希，报表本身没有工资单 ID）；
///   (ConnectionGuid, PaymentDate DESC) 服务于按支付日期区间读取。MYOB 里撤销 / 重发的单只标 RemovedAt，不物理删除。
///   含薪资金额、时薪与 Reference Number（产品决定明文保存）；仍不存年薪、生日、雇佣分类、银行账号与 TFN。
/// - dbo.MyobPayslipSyncState：每个公司文件一行，记同步进度（LastSuccessAt / CoveredFrom）、最近错误与同步租约。
/// 只新建两张表，不触碰任何既有表；回退只需 DROP 这两张表并移除迁移账本行（旧版主 API 不读它们）。
/// 实体不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// 错误号自成一段 54500–54504：54500 快照表缺失、54501 快照列签名、54502 快照主键、54503 快照索引、54504 同步状态表结构。
/// 索引均为非过滤索引，手工用 sqlcmd 执行无需特殊开关。生产须显式执行 --schema=migrate，且要先于新版主 API 上线。
/// </summary>
internal static class MyobPayslipSnapshotSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.MyobPayslipSnapshot', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[MyobPayslipSnapshot]
        (
            [SnapshotGuid] nvarchar(50) NOT NULL,
            [ConnectionGuid] nvarchar(50) NOT NULL,
            [SlipKey] nvarchar(64) NOT NULL,
            [EmployeeUid] nvarchar(50) NOT NULL,
            [EmployeeName] nvarchar(200) NULL,
            [EmployeeDisplayId] nvarchar(50) NULL,
            [EmployerName] nvarchar(200) NULL,
            [EmployerAbn] nvarchar(50) NULL,
            [PaymentDate] date NOT NULL,
            [PeriodStart] date NULL,
            [PeriodEnd] date NULL,
            [PayFrequency] nvarchar(30) NULL,
            [GrossPay] decimal(18,2) NULL,
            [NetPay] decimal(18,2) NULL,
            [SuperFundName] nvarchar(200) NULL,
            [HourlyRate] decimal(18,4) NULL,
            [ChequeNumber] nvarchar(100) NULL,
            [LinesJson] nvarchar(max) NOT NULL,
            [ContentHash] nvarchar(64) NOT NULL,
            [FirstSeenAt] datetime2 NOT NULL,
            [UpdatedAt] datetime2 NOT NULL,
            [RemovedAt] datetime2 NULL,
            CONSTRAINT [PK_MyobPayslipSnapshot] PRIMARY KEY CLUSTERED ([SnapshotGuid])
        );
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.MyobPayslipSnapshot') AND [name] = N'UX_MyobPayslipSnapshot_Connection_SlipKey')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_MyobPayslipSnapshot_Connection_SlipKey]
            ON [dbo].[MyobPayslipSnapshot] ([ConnectionGuid], [SlipKey]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.MyobPayslipSnapshot') AND [name] = N'IX_MyobPayslipSnapshot_Connection_PaymentDate')
        CREATE NONCLUSTERED INDEX [IX_MyobPayslipSnapshot_Connection_PaymentDate]
            ON [dbo].[MyobPayslipSnapshot] ([ConnectionGuid], [PaymentDate] DESC);

    IF OBJECT_ID(N'dbo.MyobPayslipSyncState', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[MyobPayslipSyncState]
        (
            [ConnectionGuid] nvarchar(50) NOT NULL,
            [LastSuccessAt] datetime2 NULL,
            [LastAttemptAt] datetime2 NULL,
            [CoveredFrom] date NULL,
            [LastError] nvarchar(300) NULL,
            [LastErrorCode] nvarchar(50) NULL,
            [LeaseUntil] datetime2 NULL,
            [LeaseOwner] nvarchar(50) NULL,
            CONSTRAINT [PK_MyobPayslipSyncState] PRIMARY KEY CLUSTERED ([ConnectionGuid])
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：列签名（类型 / 长度 / 可空性）、主键与索引必须精确匹配；不兼容结构只能由显式迁移修复，运行时绝不自动改写。
    // sys.columns.max_length：nvarchar 为字节数（字符数 * 2），nvarchar(max) 为 -1，decimal(18,x) 为 9。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.MyobPayslipSnapshot', N'U') IS NULL
    THROW 54500, N'MYOB payslip snapshot table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.MyobPayslipSnapshot', N'SnapshotGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobPayslipSnapshot', N'ConnectionGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobPayslipSnapshot', N'SlipKey', N'nvarchar', 128, 0),
      (N'dbo.MyobPayslipSnapshot', N'EmployeeUid', N'nvarchar', 100, 0),
      (N'dbo.MyobPayslipSnapshot', N'EmployeeName', N'nvarchar', 400, 1),
      (N'dbo.MyobPayslipSnapshot', N'EmployeeDisplayId', N'nvarchar', 100, 1),
      (N'dbo.MyobPayslipSnapshot', N'EmployerName', N'nvarchar', 400, 1),
      (N'dbo.MyobPayslipSnapshot', N'EmployerAbn', N'nvarchar', 100, 1),
      (N'dbo.MyobPayslipSnapshot', N'PaymentDate', N'date', 3, 0),
      (N'dbo.MyobPayslipSnapshot', N'PeriodStart', N'date', 3, 1),
      (N'dbo.MyobPayslipSnapshot', N'PeriodEnd', N'date', 3, 1),
      (N'dbo.MyobPayslipSnapshot', N'PayFrequency', N'nvarchar', 60, 1),
      (N'dbo.MyobPayslipSnapshot', N'GrossPay', N'decimal', 9, 1),
      (N'dbo.MyobPayslipSnapshot', N'NetPay', N'decimal', 9, 1),
      (N'dbo.MyobPayslipSnapshot', N'SuperFundName', N'nvarchar', 400, 1),
      (N'dbo.MyobPayslipSnapshot', N'HourlyRate', N'decimal', 9, 1),
      (N'dbo.MyobPayslipSnapshot', N'ChequeNumber', N'nvarchar', 200, 1),
      (N'dbo.MyobPayslipSnapshot', N'LinesJson', N'nvarchar', -1, 0),
      (N'dbo.MyobPayslipSnapshot', N'ContentHash', N'nvarchar', 128, 0),
      (N'dbo.MyobPayslipSnapshot', N'FirstSeenAt', N'datetime2', 8, 0),
      (N'dbo.MyobPayslipSnapshot', N'UpdatedAt', N'datetime2', 8, 0),
      (N'dbo.MyobPayslipSnapshot', N'RemovedAt', N'datetime2', 8, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54501, N'MYOB payslip snapshot column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.MyobPayslipSnapshot') AND [type] = N'PK')
    THROW 54502, N'MYOB payslip snapshot primary key is missing.', 1;

-- 两个索引必须存在、非过滤，唯一性与键列（含升降序）逐项匹配；键列个数也要一致（不允许多出键列）。
IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'UX_MyobPayslipSnapshot_Connection_SlipKey', 1, N'ConnectionGuid', 0, 1, 2),
      (N'UX_MyobPayslipSnapshot_Connection_SlipKey', 2, N'SlipKey', 0, 1, 2),
      (N'IX_MyobPayslipSnapshot_Connection_PaymentDate', 1, N'ConnectionGuid', 0, 0, 2),
      (N'IX_MyobPayslipSnapshot_Connection_PaymentDate', 2, N'PaymentDate', 1, 0, 2)
    ) expected([index_name], [key_ordinal], [column_name], [is_descending], [is_unique], [key_count])
    LEFT JOIN sys.indexes i
      ON i.[object_id] = OBJECT_ID(N'dbo.MyobPayslipSnapshot') AND i.[name] = expected.[index_name]
     AND i.[has_filter] = 0 AND i.[is_unique] = expected.[is_unique]
    LEFT JOIN sys.index_columns ic
      ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
     AND ic.[key_ordinal] = expected.[key_ordinal] AND ic.[is_included_column] = 0
    LEFT JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
    WHERE i.[index_id] IS NULL
       OR ic.[column_id] IS NULL
       OR c.[name] <> expected.[column_name]
       OR ic.[is_descending_key] <> expected.[is_descending]
       OR (SELECT COUNT(*) FROM sys.index_columns k
           WHERE k.[object_id] = i.[object_id] AND k.[index_id] = i.[index_id] AND k.[is_included_column] = 0 AND k.[key_ordinal] > 0) <> expected.[key_count]
)
    THROW 54503, N'MYOB payslip snapshot indexes are missing or incompatible.', 1;

IF OBJECT_ID(N'dbo.MyobPayslipSyncState', N'U') IS NULL
    THROW 54504, N'MYOB payslip sync state table is missing or incompatible.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'dbo.MyobPayslipSyncState', N'ConnectionGuid', N'nvarchar', 100, 0),
      (N'dbo.MyobPayslipSyncState', N'LastSuccessAt', N'datetime2', 8, 1),
      (N'dbo.MyobPayslipSyncState', N'LastAttemptAt', N'datetime2', 8, 1),
      (N'dbo.MyobPayslipSyncState', N'CoveredFrom', N'date', 3, 1),
      (N'dbo.MyobPayslipSyncState', N'LastError', N'nvarchar', 600, 1),
      (N'dbo.MyobPayslipSyncState', N'LastErrorCode', N'nvarchar', 100, 1),
      (N'dbo.MyobPayslipSyncState', N'LeaseUntil', N'datetime2', 8, 1),
      (N'dbo.MyobPayslipSyncState', N'LeaseOwner', N'nvarchar', 100, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(expected.[table_name]) AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL
       OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.MyobPayslipSyncState') AND [type] = N'PK')
    THROW 54504, N'MYOB payslip sync state table is missing or incompatible.', 1;
""";
}
