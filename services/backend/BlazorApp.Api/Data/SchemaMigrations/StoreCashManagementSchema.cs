namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 分店现金管理（存银行 / 现金支出 / 现金池）的六张新表：StoreCashDeposit（存款登记）、StoreCashDepositSlip（存单）、
/// StoreCashExpense（现金支出）、StoreCashAttachment（存单与收据图片，兼上传票据）、StoreCashCloseSelection（日结存档手选记录）、
/// StoreCashBalanceEntry（期初与盘点）。同时把 Cash.* 五个权限码幂等写入 HbwebSysPermissions：启动流程不跑权限种子，
/// 不入库的话角色管理里授不了（保存会被静默丢弃）。
/// 新表走独立版本号迁移，生产须显式执行 --schema=migrate；实体不进 SqlSugarContext 的自动建表清单。
/// 错误号区间 52200–52204。
/// </summary>
internal static class StoreCashManagementSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.StoreCashDeposit', N'U') IS NULL
    BEGIN
        -- 店长把现金存进银行的一次登记；金额为各存单之和。作废只改 Status，不删行。
        CREATE TABLE [dbo].[StoreCashDeposit]
        (
            [DepositGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [DepositDate] datetime2 NOT NULL,
            [CoveredFromDate] datetime2 NULL,
            [CoveredToDate] datetime2 NULL,
            [TotalAmount] decimal(18,2) NOT NULL,
            [Note] nvarchar(500) NULL,
            [OverrideReason] nvarchar(500) NULL,
            [Status] nvarchar(20) NOT NULL,
            [VoidReason] nvarchar(500) NULL,
            [VoidedAtUtc] datetime2 NULL,
            [VoidedByUserGuid] nvarchar(50) NULL,
            [VoidedByName] nvarchar(100) NULL,
            [ClientRequestId] nvarchar(64) NOT NULL,
            [CreatedByUserGuid] nvarchar(50) NOT NULL,
            [CreatedByName] nvarchar(100) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_StoreCashDeposit] PRIMARY KEY CLUSTERED ([DepositGuid])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.StoreCashDeposit') AND [name] = N'UX_StoreCashDeposit_ClientRequestId'
    )
    BEGIN
        -- 客户端请求号唯一：弱网重复提交只会落一条，也是幂等返回已有记录的依据。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_StoreCashDeposit_ClientRequestId]
            ON [dbo].[StoreCashDeposit] ([ClientRequestId]);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.StoreCashDeposit') AND [name] = N'IX_StoreCashDeposit_Store_Date'
    )
    BEGIN
        -- 现金池按分店 + 存款日期汇总，列表按分店 + 日期区间查。
        CREATE NONCLUSTERED INDEX [IX_StoreCashDeposit_Store_Date]
            ON [dbo].[StoreCashDeposit] ([StoreCode], [DepositDate]);
    END;

    IF OBJECT_ID(N'dbo.StoreCashDepositSlip', N'U') IS NULL
    BEGIN
        -- 一次存款下的一张存单：金额 + 照片（照片在附件表）。银行对账按存单粒度匹配流水。
        CREATE TABLE [dbo].[StoreCashDepositSlip]
        (
            [SlipGuid] nvarchar(50) NOT NULL,
            [DepositGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [Amount] decimal(18,2) NOT NULL,
            [SlipNo] nvarchar(100) NULL,
            [SortOrder] int NOT NULL,
            CONSTRAINT [PK_StoreCashDepositSlip] PRIMARY KEY CLUSTERED ([SlipGuid])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.StoreCashDepositSlip') AND [name] = N'IX_StoreCashDepositSlip_Deposit'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_StoreCashDepositSlip_Deposit]
            ON [dbo].[StoreCashDepositSlip] ([DepositGuid]);
    END;

    IF OBJECT_ID(N'dbo.StoreCashExpense', N'U') IS NULL
    BEGIN
        -- 店长动用现金的支出（现金工资 / 现金购物 / T2 / 其他），录入即生效；ReviewStatus 是财务事后核对标记，不影响生效。
        CREATE TABLE [dbo].[StoreCashExpense]
        (
            [ExpenseGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [ExpenseDate] datetime2 NOT NULL,
            [Category] nvarchar(20) NOT NULL,
            [Amount] decimal(18,2) NOT NULL,
            [PayeeUserGuid] nvarchar(50) NULL,
            [PayeeName] nvarchar(100) NULL,
            [Note] nvarchar(500) NULL,
            [ReviewStatus] nvarchar(20) NOT NULL,
            [ReviewedByUserGuid] nvarchar(50) NULL,
            [ReviewedAtUtc] datetime2 NULL,
            [ReviewNote] nvarchar(500) NULL,
            [Status] nvarchar(20) NOT NULL,
            [VoidReason] nvarchar(500) NULL,
            [VoidedAtUtc] datetime2 NULL,
            [VoidedByUserGuid] nvarchar(50) NULL,
            [VoidedByName] nvarchar(100) NULL,
            [ClientRequestId] nvarchar(64) NOT NULL,
            [CreatedByUserGuid] nvarchar(50) NOT NULL,
            [CreatedByName] nvarchar(100) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_StoreCashExpense] PRIMARY KEY CLUSTERED ([ExpenseGuid])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.StoreCashExpense') AND [name] = N'UX_StoreCashExpense_ClientRequestId'
    )
    BEGIN
        CREATE UNIQUE NONCLUSTERED INDEX [UX_StoreCashExpense_ClientRequestId]
            ON [dbo].[StoreCashExpense] ([ClientRequestId]);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.StoreCashExpense') AND [name] = N'IX_StoreCashExpense_Store_Date'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_StoreCashExpense_Store_Date]
            ON [dbo].[StoreCashExpense] ([StoreCode], [ExpenseDate]);
    END;

    IF OBJECT_ID(N'dbo.StoreCashAttachment', N'U') IS NULL
    BEGIN
        -- 存单与收据图片，兼任上传票据：Pending（已签发）→ Promoted（已校验并转正为私有正式对象）→ Linked（已挂单据）。
        CREATE TABLE [dbo].[StoreCashAttachment]
        (
            [AttachmentGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [Status] nvarchar(20) NOT NULL,
            [PendingObjectKey] nvarchar(300) NOT NULL,
            [FinalObjectKey] nvarchar(300) NOT NULL,
            [ContentType] nvarchar(50) NOT NULL,
            [FileSize] bigint NOT NULL,
            [OwnerType] nvarchar(20) NULL,
            [OwnerGuid] nvarchar(50) NULL,
            [SortOrder] int NOT NULL,
            [UploadedByUserGuid] nvarchar(50) NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [ExpiresAtUtc] datetime2 NOT NULL,
            [LinkedAtUtc] datetime2 NULL,
            CONSTRAINT [PK_StoreCashAttachment] PRIMARY KEY CLUSTERED ([AttachmentGuid])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.StoreCashAttachment') AND [name] = N'IX_StoreCashAttachment_Owner'
    )
    BEGIN
        -- 详情页按存单 / 支出批量读取图片。
        CREATE NONCLUSTERED INDEX [IX_StoreCashAttachment_Owner]
            ON [dbo].[StoreCashAttachment] ([OwnerGuid]);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.StoreCashAttachment') AND [name] = N'IX_StoreCashAttachment_Uploader'
    )
    BEGIN
        -- 签发前统计当前账号待确认的票据数量，防止无限制申请上传地址。
        CREATE NONCLUSTERED INDEX [IX_StoreCashAttachment_Uploader]
            ON [dbo].[StoreCashAttachment] ([UploadedByUserGuid], [Status]);
    END;

    IF OBJECT_ID(N'dbo.StoreCashCloseSelection', N'U') IS NULL
    BEGIN
        -- 某分店某设备某营业日纳入现金池的日结存档手选记录，只追加；IsCurrent 的那行生效。
        CREATE TABLE [dbo].[StoreCashCloseSelection]
        (
            [SelectionGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [BusinessDate] datetime2 NOT NULL,
            [DeviceCode] nvarchar(50) NOT NULL,
            [Mode] nvarchar(20) NOT NULL,
            [CloseIdsJson] nvarchar(1000) NOT NULL,
            [LatestSavedAtUtcAtSelection] datetime2 NULL,
            [OverlapWarning] bit NOT NULL,
            [Reason] nvarchar(500) NOT NULL,
            [IsCurrent] bit NOT NULL,
            [SelectedByUserGuid] nvarchar(50) NOT NULL,
            [SelectedByName] nvarchar(100) NULL,
            [SelectedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_StoreCashCloseSelection] PRIMARY KEY CLUSTERED ([SelectionGuid])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.StoreCashCloseSelection') AND [name] = N'UX_StoreCashCloseSelection_Current'
    )
    BEGIN
        -- 数据库兜底：每个（分店、营业日、设备）至多一条当前记录，并发的两次手选只会成功一次。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_StoreCashCloseSelection_Current]
            ON [dbo].[StoreCashCloseSelection] ([StoreCode], [BusinessDate], [DeviceCode])
            WHERE [IsCurrent] = 1;
    END;

    IF OBJECT_ID(N'dbo.StoreCashBalanceEntry', N'U') IS NULL
    BEGIN
        -- 现金池的期初（每店至多一条有效）与盘点记录。
        CREATE TABLE [dbo].[StoreCashBalanceEntry]
        (
            [EntryGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [EntryType] nvarchar(20) NOT NULL,
            [EntryDate] datetime2 NOT NULL,
            [Amount] decimal(18,2) NOT NULL,
            [ExpectedAmount] decimal(18,2) NULL,
            [Note] nvarchar(500) NULL,
            [Status] nvarchar(20) NOT NULL,
            [VoidReason] nvarchar(500) NULL,
            [VoidedAtUtc] datetime2 NULL,
            [VoidedByUserGuid] nvarchar(50) NULL,
            [VoidedByName] nvarchar(100) NULL,
            [ClientRequestId] nvarchar(64) NOT NULL,
            [CreatedByUserGuid] nvarchar(50) NOT NULL,
            [CreatedByName] nvarchar(100) NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_StoreCashBalanceEntry] PRIMARY KEY CLUSTERED ([EntryGuid])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.StoreCashBalanceEntry') AND [name] = N'UX_StoreCashBalanceEntry_ClientRequestId'
    )
    BEGIN
        CREATE UNIQUE NONCLUSTERED INDEX [UX_StoreCashBalanceEntry_ClientRequestId]
            ON [dbo].[StoreCashBalanceEntry] ([ClientRequestId]);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.StoreCashBalanceEntry') AND [name] = N'UX_StoreCashBalanceEntry_Opening'
    )
    BEGIN
        -- 数据库兜底：每店至多一条有效的期初记录。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_StoreCashBalanceEntry_Opening]
            ON [dbo].[StoreCashBalanceEntry] ([StoreCode])
            WHERE [EntryType] = N'Opening' AND [Status] = N'Active';
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.StoreCashBalanceEntry') AND [name] = N'IX_StoreCashBalanceEntry_Store_Date'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_StoreCashBalanceEntry_Store_Date]
            ON [dbo].[StoreCashBalanceEntry] ([StoreCode], [EntryDate]);
    END;

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库，否则角色管理里无法单独授予（保存会被静默丢弃）。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), [wanted].[Code], [wanted].[Name], N'分店财务', [wanted].[Description],
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        FROM (VALUES
            (N'Cash.Overview.View', N'查看现金管理',
             N'移动端「现金」- 查看分店现金池余额、按日明细、存款与现金支出记录；店长只限自己关联的分店'),
            (N'Cash.Deposit.Create', N'登记存银行',
             N'移动端「现金」- 登记现金存银行并上传存单、选择纳入现金池的日结、录入期初现金与盘点'),
            (N'Cash.Expense.Create', N'录入现金支出',
             N'移动端「现金」- 录入现金工资、现金购物、T2 与其他现金支出，录入即生效'),
            (N'Cash.Void', N'作废现金记录',
             N'现金管理 - 作废范围内任何存款、现金支出与期初盘点记录；没有该权限的店长只能在 24 小时内作废自己录入的记录'),
            (N'Cash.AllStores.View', N'查看全部分店现金与 T2',
             N'现金管理 - 查看全部分店的现金数据与全部历史 T2，补录不受回溯天数限制；没有该权限的店长只能看自己关联分店最近 14 天的 T2')
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

    // 只读门禁锁定六张表的列、类型（nvarchar 的 max_length 为字节数，即字符数 × 2；decimal 另校验精度 18 与小数位 2）、
    // 可空性、单列主键与全部索引（唯一性、过滤条件、键列顺序）；不兼容结构只能通过显式迁移修复，运行时绝不自动改写。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.StoreCashDeposit', N'U') IS NULL
 OR OBJECT_ID(N'dbo.StoreCashDepositSlip', N'U') IS NULL
 OR OBJECT_ID(N'dbo.StoreCashExpense', N'U') IS NULL
 OR OBJECT_ID(N'dbo.StoreCashAttachment', N'U') IS NULL
 OR OBJECT_ID(N'dbo.StoreCashCloseSelection', N'U') IS NULL
 OR OBJECT_ID(N'dbo.StoreCashBalanceEntry', N'U') IS NULL
    THROW 52200, N'Store cash management tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'StoreCashDeposit', N'DepositGuid', N'nvarchar', 100, 0, -1),
      (N'StoreCashDeposit', N'StoreCode', N'nvarchar', 100, 0, -1),
      (N'StoreCashDeposit', N'DepositDate', N'datetime2', 0, 0, -1),
      (N'StoreCashDeposit', N'CoveredFromDate', N'datetime2', 0, 1, -1),
      (N'StoreCashDeposit', N'CoveredToDate', N'datetime2', 0, 1, -1),
      (N'StoreCashDeposit', N'TotalAmount', N'decimal', 0, 0, 2),
      (N'StoreCashDeposit', N'Note', N'nvarchar', 1000, 1, -1),
      (N'StoreCashDeposit', N'OverrideReason', N'nvarchar', 1000, 1, -1),
      (N'StoreCashDeposit', N'Status', N'nvarchar', 40, 0, -1),
      (N'StoreCashDeposit', N'VoidReason', N'nvarchar', 1000, 1, -1),
      (N'StoreCashDeposit', N'VoidedAtUtc', N'datetime2', 0, 1, -1),
      (N'StoreCashDeposit', N'VoidedByUserGuid', N'nvarchar', 100, 1, -1),
      (N'StoreCashDeposit', N'VoidedByName', N'nvarchar', 200, 1, -1),
      (N'StoreCashDeposit', N'ClientRequestId', N'nvarchar', 128, 0, -1),
      (N'StoreCashDeposit', N'CreatedByUserGuid', N'nvarchar', 100, 0, -1),
      (N'StoreCashDeposit', N'CreatedByName', N'nvarchar', 200, 1, -1),
      (N'StoreCashDeposit', N'CreatedAtUtc', N'datetime2', 0, 0, -1),
      (N'StoreCashDepositSlip', N'SlipGuid', N'nvarchar', 100, 0, -1),
      (N'StoreCashDepositSlip', N'DepositGuid', N'nvarchar', 100, 0, -1),
      (N'StoreCashDepositSlip', N'StoreCode', N'nvarchar', 100, 0, -1),
      (N'StoreCashDepositSlip', N'Amount', N'decimal', 0, 0, 2),
      (N'StoreCashDepositSlip', N'SlipNo', N'nvarchar', 200, 1, -1),
      (N'StoreCashDepositSlip', N'SortOrder', N'int', 0, 0, -1),
      (N'StoreCashExpense', N'ExpenseGuid', N'nvarchar', 100, 0, -1),
      (N'StoreCashExpense', N'StoreCode', N'nvarchar', 100, 0, -1),
      (N'StoreCashExpense', N'ExpenseDate', N'datetime2', 0, 0, -1),
      (N'StoreCashExpense', N'Category', N'nvarchar', 40, 0, -1),
      (N'StoreCashExpense', N'Amount', N'decimal', 0, 0, 2),
      (N'StoreCashExpense', N'PayeeUserGuid', N'nvarchar', 100, 1, -1),
      (N'StoreCashExpense', N'PayeeName', N'nvarchar', 200, 1, -1),
      (N'StoreCashExpense', N'Note', N'nvarchar', 1000, 1, -1),
      (N'StoreCashExpense', N'ReviewStatus', N'nvarchar', 40, 0, -1),
      (N'StoreCashExpense', N'ReviewedByUserGuid', N'nvarchar', 100, 1, -1),
      (N'StoreCashExpense', N'ReviewedAtUtc', N'datetime2', 0, 1, -1),
      (N'StoreCashExpense', N'ReviewNote', N'nvarchar', 1000, 1, -1),
      (N'StoreCashExpense', N'Status', N'nvarchar', 40, 0, -1),
      (N'StoreCashExpense', N'VoidReason', N'nvarchar', 1000, 1, -1),
      (N'StoreCashExpense', N'VoidedAtUtc', N'datetime2', 0, 1, -1),
      (N'StoreCashExpense', N'VoidedByUserGuid', N'nvarchar', 100, 1, -1),
      (N'StoreCashExpense', N'VoidedByName', N'nvarchar', 200, 1, -1),
      (N'StoreCashExpense', N'ClientRequestId', N'nvarchar', 128, 0, -1),
      (N'StoreCashExpense', N'CreatedByUserGuid', N'nvarchar', 100, 0, -1),
      (N'StoreCashExpense', N'CreatedByName', N'nvarchar', 200, 1, -1),
      (N'StoreCashExpense', N'CreatedAtUtc', N'datetime2', 0, 0, -1),
      (N'StoreCashAttachment', N'AttachmentGuid', N'nvarchar', 100, 0, -1),
      (N'StoreCashAttachment', N'StoreCode', N'nvarchar', 100, 0, -1),
      (N'StoreCashAttachment', N'Status', N'nvarchar', 40, 0, -1),
      (N'StoreCashAttachment', N'PendingObjectKey', N'nvarchar', 600, 0, -1),
      (N'StoreCashAttachment', N'FinalObjectKey', N'nvarchar', 600, 0, -1),
      (N'StoreCashAttachment', N'ContentType', N'nvarchar', 100, 0, -1),
      (N'StoreCashAttachment', N'FileSize', N'bigint', 0, 0, -1),
      (N'StoreCashAttachment', N'OwnerType', N'nvarchar', 40, 1, -1),
      (N'StoreCashAttachment', N'OwnerGuid', N'nvarchar', 100, 1, -1),
      (N'StoreCashAttachment', N'SortOrder', N'int', 0, 0, -1),
      (N'StoreCashAttachment', N'UploadedByUserGuid', N'nvarchar', 100, 0, -1),
      (N'StoreCashAttachment', N'CreatedAtUtc', N'datetime2', 0, 0, -1),
      (N'StoreCashAttachment', N'ExpiresAtUtc', N'datetime2', 0, 0, -1),
      (N'StoreCashAttachment', N'LinkedAtUtc', N'datetime2', 0, 1, -1),
      (N'StoreCashCloseSelection', N'SelectionGuid', N'nvarchar', 100, 0, -1),
      (N'StoreCashCloseSelection', N'StoreCode', N'nvarchar', 100, 0, -1),
      (N'StoreCashCloseSelection', N'BusinessDate', N'datetime2', 0, 0, -1),
      (N'StoreCashCloseSelection', N'DeviceCode', N'nvarchar', 100, 0, -1),
      (N'StoreCashCloseSelection', N'Mode', N'nvarchar', 40, 0, -1),
      (N'StoreCashCloseSelection', N'CloseIdsJson', N'nvarchar', 2000, 0, -1),
      (N'StoreCashCloseSelection', N'LatestSavedAtUtcAtSelection', N'datetime2', 0, 1, -1),
      (N'StoreCashCloseSelection', N'OverlapWarning', N'bit', 0, 0, -1),
      (N'StoreCashCloseSelection', N'Reason', N'nvarchar', 1000, 0, -1),
      (N'StoreCashCloseSelection', N'IsCurrent', N'bit', 0, 0, -1),
      (N'StoreCashCloseSelection', N'SelectedByUserGuid', N'nvarchar', 100, 0, -1),
      (N'StoreCashCloseSelection', N'SelectedByName', N'nvarchar', 200, 1, -1),
      (N'StoreCashCloseSelection', N'SelectedAtUtc', N'datetime2', 0, 0, -1),
      (N'StoreCashBalanceEntry', N'EntryGuid', N'nvarchar', 100, 0, -1),
      (N'StoreCashBalanceEntry', N'StoreCode', N'nvarchar', 100, 0, -1),
      (N'StoreCashBalanceEntry', N'EntryType', N'nvarchar', 40, 0, -1),
      (N'StoreCashBalanceEntry', N'EntryDate', N'datetime2', 0, 0, -1),
      (N'StoreCashBalanceEntry', N'Amount', N'decimal', 0, 0, 2),
      (N'StoreCashBalanceEntry', N'ExpectedAmount', N'decimal', 0, 1, 2),
      (N'StoreCashBalanceEntry', N'Note', N'nvarchar', 1000, 1, -1),
      (N'StoreCashBalanceEntry', N'Status', N'nvarchar', 40, 0, -1),
      (N'StoreCashBalanceEntry', N'VoidReason', N'nvarchar', 1000, 1, -1),
      (N'StoreCashBalanceEntry', N'VoidedAtUtc', N'datetime2', 0, 1, -1),
      (N'StoreCashBalanceEntry', N'VoidedByUserGuid', N'nvarchar', 100, 1, -1),
      (N'StoreCashBalanceEntry', N'VoidedByName', N'nvarchar', 200, 1, -1),
      (N'StoreCashBalanceEntry', N'ClientRequestId', N'nvarchar', 128, 0, -1),
      (N'StoreCashBalanceEntry', N'CreatedByUserGuid', N'nvarchar', 100, 0, -1),
      (N'StoreCashBalanceEntry', N'CreatedByName', N'nvarchar', 200, 1, -1),
      (N'StoreCashBalanceEntry', N'CreatedAtUtc', N'datetime2', 0, 0, -1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable], [scale])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
       OR (expected.[scale] >= 0 AND (c.[precision] <> 18 OR c.[scale] <> expected.[scale]))
)
    THROW 52201, N'Store cash management column signature is incompatible.', 1;

-- 六张表主键都必须是单列：存款 / 存单 / 支出 / 附件 / 选择记录 / 余额记录各自的 Guid。
IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'StoreCashDeposit', N'DepositGuid'),
      (N'StoreCashDepositSlip', N'SlipGuid'),
      (N'StoreCashExpense', N'ExpenseGuid'),
      (N'StoreCashAttachment', N'AttachmentGuid'),
      (N'StoreCashCloseSelection', N'SelectionGuid'),
      (N'StoreCashBalanceEntry', N'EntryGuid')
    ) expected([table_name], [column_name])
    WHERE NOT EXISTS (
        SELECT 1
        FROM sys.key_constraints k
        INNER JOIN sys.indexes i ON i.[object_id] = k.[parent_object_id] AND i.[index_id] = k.[unique_index_id]
        WHERE k.[parent_object_id] = OBJECT_ID(N'dbo.' + expected.[table_name])
          AND k.[type] = N'PK'
          AND (SELECT COUNT(*) FROM sys.index_columns ic
               WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
          AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                        ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                      WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                        AND ic.[key_ordinal] = 1 AND c.[name] = expected.[column_name])
    )
)
    THROW 52202, N'Store cash management primary key signature is incompatible.', 1;

-- 索引属性：是否唯一、是否带过滤条件、键列个数；过滤条件必须同时包含两段关键文本（只有一段时第二段为 NULL）。
-- 「每分店每设备每营业日一条当前选择」与「每店至多一条有效期初」都靠过滤唯一索引兜底，必须原样存在。
IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'StoreCashDeposit', N'UX_StoreCashDeposit_ClientRequestId', 1, 0, 1, NULL, NULL),
      (N'StoreCashDeposit', N'IX_StoreCashDeposit_Store_Date', 0, 0, 2, NULL, NULL),
      (N'StoreCashDepositSlip', N'IX_StoreCashDepositSlip_Deposit', 0, 0, 1, NULL, NULL),
      (N'StoreCashExpense', N'UX_StoreCashExpense_ClientRequestId', 1, 0, 1, NULL, NULL),
      (N'StoreCashExpense', N'IX_StoreCashExpense_Store_Date', 0, 0, 2, NULL, NULL),
      (N'StoreCashAttachment', N'IX_StoreCashAttachment_Owner', 0, 0, 1, NULL, NULL),
      (N'StoreCashAttachment', N'IX_StoreCashAttachment_Uploader', 0, 0, 2, NULL, NULL),
      (N'StoreCashCloseSelection', N'UX_StoreCashCloseSelection_Current', 1, 1, 3, N'%IsCurrent%1%', NULL),
      (N'StoreCashBalanceEntry', N'UX_StoreCashBalanceEntry_ClientRequestId', 1, 0, 1, NULL, NULL),
      (N'StoreCashBalanceEntry', N'UX_StoreCashBalanceEntry_Opening', 1, 1, 1, N'%EntryType%Opening%', N'%Status%Active%'),
      (N'StoreCashBalanceEntry', N'IX_StoreCashBalanceEntry_Store_Date', 0, 0, 2, NULL, NULL)
    ) expected([table_name], [index_name], [is_unique], [has_filter], [key_count], [filter_like_1], [filter_like_2])
    WHERE NOT EXISTS (
        SELECT 1
        FROM sys.indexes i
        WHERE i.[object_id] = OBJECT_ID(N'dbo.' + expected.[table_name])
          AND i.[name] = expected.[index_name]
          AND i.[type] = 2
          AND CONVERT(int, i.[is_unique]) = expected.[is_unique]
          AND CONVERT(int, i.[has_filter]) = expected.[has_filter]
          AND (SELECT COUNT(*) FROM sys.index_columns ic
               WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = expected.[key_count]
          AND (expected.[filter_like_1] IS NULL OR i.[filter_definition] LIKE expected.[filter_like_1])
          AND (expected.[filter_like_2] IS NULL OR i.[filter_definition] LIKE expected.[filter_like_2])
    )
)
    THROW 52203, N'Store cash management index signature is incompatible.', 1;

-- 索引键列顺序：按 key_ordinal 逐列核对，复合索引的列顺序决定它能不能被现金池汇总与列表查询用上。
IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'StoreCashDeposit', N'UX_StoreCashDeposit_ClientRequestId', 1, N'ClientRequestId'),
      (N'StoreCashDeposit', N'IX_StoreCashDeposit_Store_Date', 1, N'StoreCode'),
      (N'StoreCashDeposit', N'IX_StoreCashDeposit_Store_Date', 2, N'DepositDate'),
      (N'StoreCashDepositSlip', N'IX_StoreCashDepositSlip_Deposit', 1, N'DepositGuid'),
      (N'StoreCashExpense', N'UX_StoreCashExpense_ClientRequestId', 1, N'ClientRequestId'),
      (N'StoreCashExpense', N'IX_StoreCashExpense_Store_Date', 1, N'StoreCode'),
      (N'StoreCashExpense', N'IX_StoreCashExpense_Store_Date', 2, N'ExpenseDate'),
      (N'StoreCashAttachment', N'IX_StoreCashAttachment_Owner', 1, N'OwnerGuid'),
      (N'StoreCashAttachment', N'IX_StoreCashAttachment_Uploader', 1, N'UploadedByUserGuid'),
      (N'StoreCashAttachment', N'IX_StoreCashAttachment_Uploader', 2, N'Status'),
      (N'StoreCashCloseSelection', N'UX_StoreCashCloseSelection_Current', 1, N'StoreCode'),
      (N'StoreCashCloseSelection', N'UX_StoreCashCloseSelection_Current', 2, N'BusinessDate'),
      (N'StoreCashCloseSelection', N'UX_StoreCashCloseSelection_Current', 3, N'DeviceCode'),
      (N'StoreCashBalanceEntry', N'UX_StoreCashBalanceEntry_ClientRequestId', 1, N'ClientRequestId'),
      (N'StoreCashBalanceEntry', N'UX_StoreCashBalanceEntry_Opening', 1, N'StoreCode'),
      (N'StoreCashBalanceEntry', N'IX_StoreCashBalanceEntry_Store_Date', 1, N'StoreCode'),
      (N'StoreCashBalanceEntry', N'IX_StoreCashBalanceEntry_Store_Date', 2, N'EntryDate')
    ) expected([table_name], [index_name], [key_ordinal], [column_name])
    WHERE NOT EXISTS (
        SELECT 1
        FROM sys.indexes i
        INNER JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
        INNER JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
        WHERE i.[object_id] = OBJECT_ID(N'dbo.' + expected.[table_name])
          AND i.[name] = expected.[index_name]
          AND ic.[is_included_column] = 0
          AND ic.[key_ordinal] = expected.[key_ordinal]
          AND c.[name] = expected.[column_name]
    )
)
    THROW 52204, N'Store cash management index key signature is incompatible.', 1;
""";
}
