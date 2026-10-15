-- SQL Server：部署前核对目标服务器、HBweb 数据库及 dbo.HbwebSysPermissions / dbo.HBwebSysRolePermissions / dbo.[Role] 表。
-- 移动端「员工管理」与「用户管理」拆开权限码：
-- 1) 登记新权限 Users.ViewStoreStaff（员工管理入口与店员列表/详情读取）。
-- 2) 授予现有 StoreManager、店长、经理三个角色（与 PermissionSeedData 店长模板一致）。
-- 3) 收回这三个角色上的 Users.View（软删除，保留行以便回退）：Users.View 只留给「用户管理」。
--    只在第 2 步已确保该角色拥有 Users.ViewStoreStaff 之后才收回，避免店长既进不了员工管理也进不了用户管理。
-- 不改动任何用户直授权限，也不改动其他角色（管理员等持有 Users.View 的角色通过权限别名自动包含 ViewStoreStaff）。
-- 已有同名权限保持原样；如已停用则中止，由管理员核对后处理。可重复执行。
-- 部署顺序：先执行本脚本，再部署后端，最后发移动端 OTA（旧版 App 的「员工管理」入口仍按 Users.View 判断，
-- 本脚本收回后旧版店长会暂时看不到该入口，须与新版 OTA 同步上线）。
SET XACT_ABORT ON;
IF DB_NAME() <> N'HBweb'
    THROW 51000, N'目标数据库必须是 HBweb。', 1;
IF OBJECT_ID(N'dbo.HbwebSysPermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HbwebSysPermissions 权限表。', 1;
IF OBJECT_ID(N'dbo.HBwebSysRolePermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HBwebSysRolePermissions 角色权限表。', 1;

DECLARE @Actor nvarchar(100) = N'Migration_20261015_ViewStoreStaff';
DECLARE @Code nvarchar(100) = N'Users.ViewStoreStaff';
DECLARE @Name nvarchar(100) = N'查看本店店员';
DECLARE @Description nvarchar(500) = N'移动端「员工管理」- 查看本人可管理分店的店员与店长本人（不含其他店长、管理员，也不含「用户管理」）；持有 Users.View 自动包含';

DECLARE @RoleNames TABLE (RoleName nvarchar(100) NOT NULL PRIMARY KEY);
INSERT INTO @RoleNames (RoleName) VALUES (N'StoreManager'), (N'店长'), (N'经理');

BEGIN TRY
    BEGIN TRANSACTION;

    IF (SELECT COUNT(*) FROM dbo.HbwebSysPermissions WITH (UPDLOCK, HOLDLOCK) WHERE Code = @Code) > 1
        THROW 51000, N'存在重复的 Users.ViewStoreStaff 权限，停止登记。', 1;
    IF EXISTS (SELECT 1 FROM dbo.HbwebSysPermissions WHERE Code = @Code AND IsDeleted = 1)
        THROW 51000, N'Users.ViewStoreStaff 权限已停用，停止自动恢复。', 1;

    INSERT INTO dbo.HbwebSysPermissions
        (Id, Code, Name, Category, Description, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted)
    SELECT CONVERT(nvarchar(36), NEWID()), @Code, @Name, N'用户管理', @Description,
           SYSUTCDATETIME(), @Actor, SYSUTCDATETIME(), @Actor, 0
    WHERE NOT EXISTS (SELECT 1 FROM dbo.HbwebSysPermissions p WHERE p.Code = @Code);
    DECLARE @InsertedPermissions int = @@ROWCOUNT;

    -- 先授予 ViewStoreStaff（只授予仍有效的三个店长角色；已存在有效授予的跳过）。
    INSERT INTO dbo.HBwebSysRolePermissions
        (Id, RoleGuid, PermissionCode, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted)
    SELECT CONVERT(nvarchar(36), NEWID()), r.RoleGUID, @Code,
           SYSUTCDATETIME(), @Actor, SYSUTCDATETIME(), @Actor, 0
    FROM dbo.[Role] r
    INNER JOIN @RoleNames names ON names.RoleName = r.RoleName
    WHERE r.IsDeleted = 0
      AND r.IsActive = 1
      AND NOT EXISTS (
          SELECT 1 FROM dbo.HBwebSysRolePermissions rp WITH (UPDLOCK, HOLDLOCK)
          WHERE rp.RoleGuid = r.RoleGUID
            AND rp.PermissionCode = @Code
            AND rp.IsDeleted = 0
      );
    DECLARE @InsertedGrants int = @@ROWCOUNT;

    -- 再收回这三个角色上的 Users.View：只处理已确认拥有 ViewStoreStaff 的角色，按行软删除。
    SELECT rp.Id INTO #RevokedUsersView
    FROM dbo.HBwebSysRolePermissions rp
    INNER JOIN dbo.[Role] r ON r.RoleGUID = rp.RoleGuid
    INNER JOIN @RoleNames names ON names.RoleName = r.RoleName
    WHERE rp.PermissionCode = N'Users.View'
      AND rp.IsDeleted = 0
      AND r.IsDeleted = 0
      AND EXISTS (
          SELECT 1 FROM dbo.HBwebSysRolePermissions v
          WHERE v.RoleGuid = r.RoleGUID AND v.PermissionCode = @Code AND v.IsDeleted = 0
      );

    UPDATE rp
    SET IsDeleted = 1, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @Actor
    FROM dbo.HBwebSysRolePermissions rp
    INNER JOIN #RevokedUsersView x ON x.Id = rp.Id;
    DECLARE @RevokedUsersView int = @@ROWCOUNT;

    COMMIT TRANSACTION;

    -- 回读本次涉及的权限、授予与收回，便于核对影响行数（回退时按下面的 Id 精确恢复）。
    SELECT @InsertedPermissions AS InsertedPermissions, @InsertedGrants AS InsertedRoleGrants, @RevokedUsersView AS RevokedUsersViewGrants;
    SELECT Id, Code, Name, Category, IsDeleted, CreatedAt, CreatedBy
    FROM dbo.HbwebSysPermissions WHERE Code = @Code;
    SELECT r.RoleName, rp.PermissionCode, rp.Id, rp.CreatedBy, rp.UpdatedBy, rp.IsDeleted
    FROM dbo.HBwebSysRolePermissions rp
    INNER JOIN dbo.[Role] r ON r.RoleGUID = rp.RoleGuid
    INNER JOIN @RoleNames names ON names.RoleName = r.RoleName
    WHERE rp.PermissionCode IN (@Code, N'Users.View')
    ORDER BY r.RoleName, rp.PermissionCode;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- 回退方式（均按 Actor 精确定位，不得按权限码批量处理）：
--   1) 恢复被收回的 Users.View：UPDATE dbo.HBwebSysRolePermissions SET IsDeleted = 0, UpdatedBy = N'Rollback_20261015_ViewStoreStaff'
--      WHERE PermissionCode = N'Users.View' AND UpdatedBy = N'Migration_20261015_ViewStoreStaff' AND IsDeleted = 1;
--   2) 取消本脚本新建的 ViewStoreStaff 授予：先在角色管理中取消，或对 CreatedBy = N'Migration_20261015_ViewStoreStaff'
--      的 HBwebSysRolePermissions 行软删除；最后停用权限定义。
