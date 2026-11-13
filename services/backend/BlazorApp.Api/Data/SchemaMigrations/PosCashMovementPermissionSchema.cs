namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 收银端现金存入 / 取出（迁移 20261113.001-pos-cash-movement-permissions）：
/// 只把三个新的收银端权限码幂等写入 HbwebSysPermissions，不建表、不改任何既有表、不授予任何角色（生产手动授权）。
/// 明细表 POSM_CashMovement 在 POSM 库，由 Hbpos.Api 启动时建立，不在本迁移内。
/// 错误号 54480：权限码缺失。生产须显式执行 --schema=migrate。
/// </summary>
internal static class PosCashMovementPermissionSchema
{
    internal const string ApplySql = """
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库；默认不授予任何角色。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), [wanted].[Code], [wanted].[Name], N'POS 钱箱', [wanted].[Description],
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        FROM (VALUES
            (N'Permissions.PosTerminal.CashMovement.CashIn', N'现金存入',
             N'收银端钱箱 - 现金存入（补零钱、其他存入）'),
            (N'Permissions.PosTerminal.CashMovement.CashOut', N'现金取出',
             N'收银端钱箱 - 现金取出（交店长 / 入保险柜、小额现金支出）'),
            (N'Permissions.PosTerminal.CashMovement.Void', N'冲正现金进出',
             N'收银端钱箱 - 冲正本期（未日结）的现金存入 / 取出记录')
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

    // 只读门禁：三个权限码都必须在库（未删除）。
    internal const string VerifySql = """
IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
   AND (SELECT COUNT(DISTINCT [Code]) FROM [dbo].[HbwebSysPermissions]
        WHERE [IsDeleted] = 0
          AND [Code] IN (
              N'Permissions.PosTerminal.CashMovement.CashIn',
              N'Permissions.PosTerminal.CashMovement.CashOut',
              N'Permissions.PosTerminal.CashMovement.Void')) <> 3
    THROW 54480, N'POS cash movement permission codes are missing.', 1;
""";
}
