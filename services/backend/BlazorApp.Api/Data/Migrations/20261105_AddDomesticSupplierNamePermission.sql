-- SQL Server：部署前核对目标服务器、HBweb 数据库及 dbo.HbwebSysPermissions / dbo.HBwebSysRolePermissions / dbo.[Role] 表。
-- 1) 登记「查看国内供应商名称」权限 DomesticPurchase.ViewSupplierName（Web 与移动端所有显示国内供应商的页面及导出文件；不初始化全部种子）。
--    没有此权限的账号，接口返回里的国内供应商名称、店铺号、联系人、电话、邮箱、门头照、备注一律为空，只保留供应商代码（如 HB196）。
-- 2) 授予现有 WarehouseManager 角色（仓库管理，与 PermissionSeedData 模板一致）；Admin 角色由后端自动放行，无需授予。
--    持有「管理国内供应商」DomesticPurchase.ManageSuppliers 的账号经权限别名自动具备，不在这里重复授予。
--    不授予 WarehouseStaff、店长、员工、订货员等其他角色，也不授予任何用户直授权限；需要时由管理员在用户管理 / 角色管理里另行授予。
-- 已有同名权限保持原样；如已停用则中止，由管理员核对后处理。可重复执行。
-- 上线顺序：先执行本脚本，再部署主 API 与 Web，最后发移动端 OTA。
--   若先部署 API 未执行本脚本：WarehouseManager 账号也只能看到供应商代码（管理员不受影响），执行后约 1 分钟内恢复（权限结果按账号缓存 60 秒）。
SET XACT_ABORT ON;
IF DB_NAME() <> N'HBweb'
    THROW 51000, N'目标数据库必须是 HBweb。', 1;
IF OBJECT_ID(N'dbo.HbwebSysPermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HbwebSysPermissions 权限表。', 1;
IF OBJECT_ID(N'dbo.HBwebSysRolePermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HBwebSysRolePermissions 角色权限表。', 1;

DECLARE @Actor nvarchar(100) = N'Migration_20261105_DomesticSupplierName';
DECLARE @Permissions TABLE (
    Code nvarchar(100) NOT NULL PRIMARY KEY,
    Name nvarchar(100) NOT NULL,
    Description nvarchar(500) NOT NULL
);
INSERT INTO @Permissions (Code, Name, Description) VALUES
    (N'DomesticPurchase.ViewSupplierName', N'查看国内供应商名称',
     N'Web 与移动端所有显示国内供应商的页面及导出文件 - 默认只显示供应商代码（如 HB196），有此权限才显示供应商名称、店铺号、联系人与联系方式');

DECLARE @RoleNames TABLE (RoleName nvarchar(100) NOT NULL PRIMARY KEY);
INSERT INTO @RoleNames (RoleName) VALUES (N'WarehouseManager');

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT p.Code
        FROM dbo.HbwebSysPermissions p WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN @Permissions expected ON expected.Code = p.Code
        GROUP BY p.Code
        HAVING COUNT(*) > 1
    )
        THROW 51000, N'存在重复的查看国内供应商名称权限，停止登记。', 1;
    IF EXISTS (
        SELECT 1
        FROM dbo.HbwebSysPermissions p
        INNER JOIN @Permissions expected ON expected.Code = p.Code
        WHERE p.IsDeleted = 1
    )
        THROW 51000, N'查看国内供应商名称权限已停用，停止自动恢复。', 1;

    INSERT INTO dbo.HbwebSysPermissions
        (Id, Code, Name, Category, Description, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted)
    SELECT CONVERT(nvarchar(36), NEWID()), expected.Code, expected.Name, N'国内采购', expected.Description,
           SYSUTCDATETIME(), @Actor, SYSUTCDATETIME(), @Actor, 0
    FROM @Permissions expected
    WHERE NOT EXISTS (SELECT 1 FROM dbo.HbwebSysPermissions p WHERE p.Code = expected.Code);
    DECLARE @InsertedPermissions int = @@ROWCOUNT;

    -- 只授予仍有效的 WarehouseManager；已存在有效授予的组合跳过。
    INSERT INTO dbo.HBwebSysRolePermissions
        (Id, RoleGuid, PermissionCode, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted)
    SELECT CONVERT(nvarchar(36), NEWID()), r.RoleGUID, expected.Code,
           SYSUTCDATETIME(), @Actor, SYSUTCDATETIME(), @Actor, 0
    FROM dbo.[Role] r
    INNER JOIN @RoleNames names ON names.RoleName = r.RoleName
    CROSS JOIN @Permissions expected
    WHERE r.IsDeleted = 0
      AND r.IsActive = 1
      AND NOT EXISTS (
          SELECT 1 FROM dbo.HBwebSysRolePermissions rp WITH (UPDLOCK, HOLDLOCK)
          WHERE rp.RoleGuid = r.RoleGUID
            AND rp.PermissionCode = expected.Code
            AND rp.IsDeleted = 0
      );
    DECLARE @InsertedGrants int = @@ROWCOUNT;

    COMMIT TRANSACTION;

    -- 回读本次涉及的权限与授予（首次执行预期 InsertedPermissions=1、InsertedRoleGrants=1，重复执行均为 0）。
    SELECT @InsertedPermissions AS InsertedPermissions, @InsertedGrants AS InsertedRoleGrants;
    SELECT Id, Code, Name, Category, IsDeleted, CreatedAt, CreatedBy
    FROM dbo.HbwebSysPermissions
    WHERE Code IN (SELECT Code FROM @Permissions);
    SELECT r.RoleName, rp.PermissionCode, rp.Id, rp.CreatedBy, rp.IsDeleted
    FROM dbo.HBwebSysRolePermissions rp
    INNER JOIN dbo.[Role] r ON r.RoleGUID = rp.RoleGuid
    WHERE rp.PermissionCode IN (SELECT Code FROM @Permissions)
    ORDER BY r.RoleName, rp.PermissionCode;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- 回退方式：按 CreatedBy = N'Migration_20261105_DomesticSupplierName' 精确定位本脚本新建的
-- HBwebSysRolePermissions 行与 HbwebSysPermissions 行，先在角色管理中取消该权限的授予，
-- 再停用权限定义。不得按权限码批量删除，以免误删管理员手工配置的同名授予。
