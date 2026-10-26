namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 角色等级（迁移 20261026.001-role-level）：dbo.Role 加一列 Level int NOT NULL DEFAULT 0，
/// 并按角色名把已知角色回填为基准等级（与 Permissions.RoleLevels.ForRoleName 保持一致）：
/// 超级管理员 100 / 仓库经理 80 / 店长 70 / Manager 60 / 员工与订货、仓库员工 30 / User 10。
/// 回填只更新 Level = 0 的行：重复执行不会覆盖已设置（大于 0）的等级；若已知角色被手工调回 0，
/// 重跑时会被重新回填为基准值（只有账本缺失导致重跑时才会发生，已在本机 SQL Server 验证）。
/// 未登记的自定义角色保持 0，须管理员在角色页设置：其持有者等级按 0 算，只能修改同样为 0 以下的账号（即无人）。
/// Role 随基线 20260827.001 建出，新列只能走独立版本号迁移；实体写的是 [SugarColumn(IsNullable = false)]。
/// 部署顺序：先执行 --schema=migrate（或手工 sqlcmd 执行 ApplySql），再发主 API（Hbpos.Api 与主 API 共用 Role 实体，
/// 缺列时整实体查询会直接失败），最后发 Web。错误号区间 54300–54301。
/// </summary>
internal static class RoleLevelSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.Role', N'U') IS NULL
    THROW 54300, N'Role table is missing; apply the HBweb baseline first.', 1;

BEGIN TRANSACTION;
BEGIN TRY
    -- COL_LENGTH 判断保证重复执行是空操作；若 CodeFirst 已抢先建出同名列，则保持原列不动，
    -- 由 VerifySql 校验类型与可空性（不符合只能显式修复，运行时绝不自动改写）。
    IF COL_LENGTH(N'dbo.Role', N'Level') IS NULL
        ALTER TABLE [dbo].[Role] ADD [Level] int NOT NULL CONSTRAINT [DF_Role_Level] DEFAULT (0);

    -- 新列与同批次语句不能静态引用，回填走动态 SQL；只改 Level = 0 的行，保证幂等。
    EXEC sp_executesql N'
        UPDATE r
        SET r.[Level] = v.[Level]
        FROM [dbo].[Role] r
        INNER JOIN (VALUES
            (N''Admin'', 100), (N''管理员'', 100), (N''SuperAdmin'', 100), (N''超级管理员'', 100),
            (N''WarehouseManager'', 80), (N''仓库经理'', 80), (N''Warehouse'', 80),
            (N''仓库管理员'', 80), (N''WarehouseAdmin'', 80),
            (N''StoreManager'', 70), (N''店长'', 70), (N''经理'', 70),
            (N''Manager'', 60),
            (N''StoreStaff'', 30), (N''Employee'', 30), (N''店铺员工'', 30), (N''店员'', 30),
            (N''员工'', 30), (N''Order'', 30), (N''WarehouseStaff'', 30),
            (N''User'', 10)
        ) v([RoleName], [Level]) ON r.[RoleName] = v.[RoleName]
        WHERE r.[Level] = 0;';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：Level int NOT NULL（sys.columns.max_length 按字节计，int = 4）。
    // 签名不符只能通过显式迁移修复，运行时绝不自动改写。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.Role', N'U') IS NULL
    THROW 54300, N'Role table is missing.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.Role')
      AND c.[name] = N'Level'
      AND ty.[name] = N'int'
      AND c.[max_length] = 4
      AND c.[is_nullable] = 0
)
    THROW 54301, N'Role level column signature is incompatible.', 1;
""";
}
