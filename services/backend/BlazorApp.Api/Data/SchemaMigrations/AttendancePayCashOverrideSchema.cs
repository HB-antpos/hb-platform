namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 工资表「公假 / 带薪假」现金金额手填（迁移 20261120.001-attendance-pay-cash-override）：
/// 新表 AttendancePayPeriodCashOverride，每人每期每档（PublicHoliday / Leave）一条，
/// (StoreCode, PeriodStart, Weeks, UserGuid, Bucket) 唯一。手填金额覆盖该档的系统现金金额，不影响转账与 MYOB 推送。
/// 只新建一张表，不改任何既有表，不回填历史数据；生产须显式执行 --schema=migrate，且要先于新版主 API 上线（实体会查这张表）。
/// 错误号区间 54800–54803。
/// </summary>
internal static class AttendancePayCashOverrideSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.AttendancePayPeriod', N'U') IS NULL
    THROW 54800, N'Attendance pay period table is missing; apply the payroll migration first.', 1;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.AttendancePayPeriodCashOverride', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[AttendancePayPeriodCashOverride]
        (
            [OverrideGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [PeriodStart] date NOT NULL,
            [Weeks] int NOT NULL,
            [UserGuid] nvarchar(50) NOT NULL,
            [Bucket] nvarchar(20) NOT NULL,
            [Amount] decimal(10,2) NOT NULL,
            [SystemAmount] decimal(10,2) NOT NULL,
            [UpdatedAt] datetime2 NOT NULL,
            [UpdatedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_AttendancePayPeriodCashOverride] PRIMARY KEY CLUSTERED ([OverrideGuid])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AttendancePayPeriodCashOverride')
          AND [name] = N'UX_AttendancePayPeriodCashOverride_Store_Period_User_Bucket')
        CREATE UNIQUE NONCLUSTERED INDEX [UX_AttendancePayPeriodCashOverride_Store_Period_User_Bucket]
            ON [dbo].[AttendancePayPeriodCashOverride] ([StoreCode], [PeriodStart], [Weeks], [UserGuid], [Bucket]);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：表存在、关键列类型正确、唯一索引存在；不兼容结构只能通过显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.AttendancePayPeriodCashOverride', N'U') IS NULL
    THROW 54801, N'Attendance pay cash override table is missing.', 1;

IF (
    SELECT COUNT(*)
    FROM sys.columns c
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.AttendancePayPeriodCashOverride')
      AND (
            (c.[name] = N'OverrideGuid' AND ty.[name] = N'nvarchar' AND c.[max_length] = 100 AND c.[is_nullable] = 0)
         OR (c.[name] = N'StoreCode'    AND ty.[name] = N'nvarchar' AND c.[max_length] = 100 AND c.[is_nullable] = 0)
         OR (c.[name] = N'PeriodStart'  AND ty.[name] = N'date' AND c.[is_nullable] = 0)
         OR (c.[name] = N'Weeks'        AND ty.[name] = N'int' AND c.[is_nullable] = 0)
         OR (c.[name] = N'UserGuid'     AND ty.[name] = N'nvarchar' AND c.[max_length] = 100 AND c.[is_nullable] = 0)
         OR (c.[name] = N'Bucket'       AND ty.[name] = N'nvarchar' AND c.[max_length] = 40 AND c.[is_nullable] = 0)
         OR (c.[name] = N'Amount'       AND ty.[name] = N'decimal' AND c.[precision] = 10 AND c.[scale] = 2 AND c.[is_nullable] = 0)
         OR (c.[name] = N'SystemAmount' AND ty.[name] = N'decimal' AND c.[precision] = 10 AND c.[scale] = 2 AND c.[is_nullable] = 0)
         OR (c.[name] = N'UpdatedAt'    AND ty.[name] = N'datetime2' AND c.[is_nullable] = 0)
         OR (c.[name] = N'UpdatedBy'    AND ty.[name] = N'nvarchar' AND c.[max_length] = 200 AND c.[is_nullable] = 1)
          )
) <> 10
    THROW 54802, N'Attendance pay cash override column signature is incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.AttendancePayPeriodCashOverride')
      AND [name] = N'UX_AttendancePayPeriodCashOverride_Store_Period_User_Bucket'
      AND [is_unique] = 1
)
    THROW 54803, N'Attendance pay cash override unique index is missing.', 1;
""";
}
