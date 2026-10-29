namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// MYOB 工资单导出（迁移 20261029.004-myob-payslip-export）：下载 PDF / 邮件发送工资单，在原有查看记录上留痕。
/// - dbo.MyobPayslipViewLog 增加两列：Action（View / Download / Email，既有行默认 View）与 Recipient（邮件收件邮箱，其余动作为空）。
///   仍然只记「谁、何时、哪一期、做了什么、发给哪个邮箱」，不记金额、姓名或工资内容。
/// - 新权限码 Myob.Payslip.Download / Myob.Payslip.Email 幂等写入 HbwebSysPermissions，不授予任何角色（生产手动授权）。
/// 只给已存在的表加两列并插入两行权限；回退只需删除这两列、两条权限行并移除迁移账本行（先部署旧版主 API 即可，旧版不读这两列）。
/// 错误号自成一段 54380–54389：54380 表缺失、54381 Action 列签名、54382 Recipient 列签名。
/// 生产须显式执行 --schema=migrate，且要先于新版主 API 上线（新版写查看记录时会写 Action 列）。
/// </summary>
internal static class MyobPayslipExportSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF COL_LENGTH(N'dbo.MyobPayslipViewLog', N'Action') IS NULL
        ALTER TABLE [dbo].[MyobPayslipViewLog]
            ADD [Action] nvarchar(20) NOT NULL CONSTRAINT [DF_MyobPayslipViewLog_Action] DEFAULT N'View';
    IF COL_LENGTH(N'dbo.MyobPayslipViewLog', N'Recipient') IS NULL
        ALTER TABLE [dbo].[MyobPayslipViewLog]
            ADD [Recipient] nvarchar(254) NULL;

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库；默认不授予任何角色（生产手动授权）。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), [wanted].[Code], [wanted].[Name], N'系统管理', [wanted].[Description],
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        FROM (VALUES
            (N'Myob.Payslip.Download', N'下载工资单 PDF',
             N'在 Web「工资单」明细与手机端工资单页下载 MYOB 默认样式的 PDF'),
            (N'Myob.Payslip.Email', N'邮件发送工资单 PDF',
             N'把 MYOB 默认样式工资单 PDF 以邮件发给员工')
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

    // 只读门禁：两列的类型 / 长度 / 可空性必须精确匹配（nvarchar 的 max_length 是字节数，字符数 * 2）。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.MyobPayslipViewLog', N'U') IS NULL
    THROW 54380, N'MYOB payslip view log table is missing.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.MyobPayslipViewLog') AND c.[name] = N'Action'
      AND ty.[name] = N'nvarchar' AND c.[max_length] = 40 AND c.[is_nullable] = 0
)
    THROW 54381, N'MYOB payslip view log Action column is missing or incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.MyobPayslipViewLog') AND c.[name] = N'Recipient'
      AND ty.[name] = N'nvarchar' AND c.[max_length] = 508 AND c.[is_nullable] = 1
)
    THROW 54382, N'MYOB payslip view log Recipient column is missing or incompatible.', 1;
""";
}
