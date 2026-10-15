-- SQL Server：部署前核对目标服务器、HBweb 数据库及 dbo.HbwebSysPermissions / dbo.HBwebSysRolePermissions / dbo.[Role] 表。
-- 1) 登记「查看本店员工」权限 Users.ViewStoreStaff（移动端「员工管理」入口，与「用户管理」的 Users.View 分开；不初始化全部种子）。
-- 2) 授予现有 StoreManager、店长、经理三个角色（与 PermissionSeedData 店长模板一致），已授予的保持原样。
--    本脚本不收回任何角色已有的 Users.View：持有 Users.View 的账号经后端别名同样能进「员工管理」，
--    是否把店长的 Users.View 收掉（让店长看不到「用户管理」）由管理员在角色管理里另行决定。
-- 不授予任何用户直授权限。已有同名权限保持原样；如已停用则中止，由管理员核对后处理。可重复执行。
SET XACT_ABORT ON;
IF DB_NAME() <> N'HBweb'
    THROW 51000, N'目标数据库必须是 HBweb。', 1;
IF OBJECT_ID(N'dbo.HbwebSysPermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HbwebSysPermissions 权限表。', 1;
IF OBJECT_ID(N'dbo.HBwebSysRolePermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HBwebSysRolePermissions 角色权限表。', 1;

DECLARE @Actor nvarchar(100) = N'Migration_20261015_ViewStoreStaff';
DECLARE @Permissions TABLE (
    Code nvarchar(100) NOT NULL PRIMARY KEY,
    Name nvarchar(100) NOT NULL,
    Description nvarchar(500) NOT NULL
);
INSERT INTO @Permissions (Code, Name, Description) VALUES
    (N'Users.ViewStoreStaff', N'查看本店员工', N'移动端「员工管理」- 查看本店店员列表与详情（不含 Web 全局用户管理）');

DECLARE @RoleNames TABLE (RoleName nvarchar(100) NOT NULL PRIMARY KEY);
INSERT INTO @RoleNames (RoleName) VALUES (N'StoreManager'), (N'店长'), (N'经理');

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT p.Code
        FROM dbo.HbwebSysPermissions p WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN @Permissions expected ON expected.Code = p.Code
        GROUP BY p.Code
        HAVING COUNT(*) > 1
    )
        THROW 51000, N'存在重复的查看本店员工权限，停止登记。', 1;
    IF EXISTS (
        SELECT 1
        FROM dbo.HbwebSysPermissions p
        INNER JOIN @Permissions expected ON expected.Code = p.Code
        WHERE p.IsDeleted = 1
    )
        THROW 51000, N'查看本店员工权限已停用，停止自动恢复。', 1;

    INSERT INTO dbo.HbwebSysPermissions
        (Id, Code, Name, Category, Description, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted)
    SELECT CONVERT(nvarchar(36), NEWID()), expected.Code, expected.Name, N'用户管理', expected.Description,
           SYSUTCDATETIME(), @Actor, SYSUTCDATETIME(), @Actor, 0
    FROM @Permissions expected
    WHERE NOT EXISTS (SELECT 1 FROM dbo.HbwebSysPermissions p WHERE p.Code = expected.Code);
    DECLARE @InsertedPermissions int = @@ROWCOUNT;

    -- 只授予仍有效的 StoreManager、店长、经理；已存在有效授予的组合跳过。
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

    -- 回读本次涉及的权限与授予，便于核对影响行数。
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

-- 回退方式：按 CreatedBy = N'Migration_20261015_ViewStoreStaff' 精确定位本脚本新建的
-- HBwebSysRolePermissions 行与 HbwebSysPermissions 行，先在角色管理中取消该权限的授予，
-- 再停用权限定义。不得按权限码批量删除，以免误删管理员手工配置的同名授予。
