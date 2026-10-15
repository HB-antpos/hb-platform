-- SQL Server：部署前核对目标服务器、HBweb 数据库及 dbo.HBwebSysRolePermissions / dbo.[Role] 表。
-- 接在 20261015_AddViewStoreStaffPermission.sql 之后执行：让店长看不到「用户管理」，只保留「员工管理」。
-- 收回 StoreManager、店长、经理三个角色上的 Users.View（软删除，保留行以便回退）；
-- 只处理「已确认拥有 Users.ViewStoreStaff」的角色，避免店长既进不了员工管理也进不了用户管理。
-- 不改动其他角色（管理员等）、不改动任何用户直授权限。可重复执行。
-- 部署顺序：先执行 AddViewStoreStaffPermission → 部署后端 + 发移动端 OTA（旧版 App 的员工管理页按 Users.View 判断，
-- 旧版店长在本脚本执行后会暂时进不去该页，须与新版 OTA 同步，建议最后执行本脚本）。
SET XACT_ABORT ON;
IF DB_NAME() <> N'HBweb'
    THROW 51000, N'目标数据库必须是 HBweb。', 1;
IF OBJECT_ID(N'dbo.HBwebSysRolePermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HBwebSysRolePermissions 角色权限表。', 1;

DECLARE @Actor nvarchar(100) = N'Migration_20261015_RevokeUsersViewStoreManagers';
DECLARE @RoleNames TABLE (RoleName nvarchar(100) NOT NULL PRIMARY KEY);
INSERT INTO @RoleNames (RoleName) VALUES (N'StoreManager'), (N'店长'), (N'经理');

BEGIN TRY
    BEGIN TRANSACTION;

    SELECT rp.Id INTO #RevokedUsersView
    FROM dbo.HBwebSysRolePermissions rp WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.[Role] r ON r.RoleGUID = rp.RoleGuid
    INNER JOIN @RoleNames names ON names.RoleName = r.RoleName
    WHERE rp.PermissionCode = N'Users.View'
      AND rp.IsDeleted = 0
      AND r.IsDeleted = 0
      AND EXISTS (
          SELECT 1 FROM dbo.HBwebSysRolePermissions v
          WHERE v.RoleGuid = r.RoleGUID AND v.PermissionCode = N'Users.ViewStoreStaff' AND v.IsDeleted = 0
      );

    UPDATE rp
    SET IsDeleted = 1, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @Actor
    FROM dbo.HBwebSysRolePermissions rp
    INNER JOIN #RevokedUsersView x ON x.Id = rp.Id;
    DECLARE @Revoked int = @@ROWCOUNT;

    COMMIT TRANSACTION;

    -- 回读：影响行数与逐角色结果（回退时按下面的 Id 精确恢复）。
    SELECT @Revoked AS RevokedUsersViewGrants;
    SELECT r.RoleName, rp.PermissionCode, rp.Id, rp.UpdatedBy, rp.IsDeleted
    FROM dbo.HBwebSysRolePermissions rp
    INNER JOIN dbo.[Role] r ON r.RoleGUID = rp.RoleGuid
    INNER JOIN @RoleNames names ON names.RoleName = r.RoleName
    WHERE rp.PermissionCode IN (N'Users.View', N'Users.ViewStoreStaff')
    ORDER BY r.RoleName, rp.PermissionCode;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- 回退方式（按 UpdatedBy 精确定位，不得按权限码批量处理）：
--   UPDATE dbo.HBwebSysRolePermissions SET IsDeleted = 0, UpdatedBy = N'Rollback_20261015_RevokeUsersViewStoreManagers'
--   WHERE PermissionCode = N'Users.View' AND UpdatedBy = N'Migration_20261015_RevokeUsersViewStoreManagers' AND IsDeleted = 1;
