-- SQL Server：部署前核对目标服务器、HBweb 数据库及 dbo.HbwebSysPermissions / dbo.HBwebSysRolePermissions / dbo.[Role] 表。
-- 1) 登记员工个人信息维护的两个新权限（Web「员工个人信息维护」页、用户管理「个人信息」页签、移动端「人员管理 > 资料」共用）：
--    EmployeeProfiles.ViewManagedStore（查看）、EmployeeProfiles.EditManagedStore（修改非敏感资料）。不初始化全部种子。
-- 2) 为避免上线后管理员入口消失：把新权限授予「当前已持有对应旧权限」的有效角色，授予关系一一对应：
--      EmployeeProfiles.View → EmployeeProfiles.ViewManagedStore
--      EmployeeProfiles.Edit → EmployeeProfiles.EditManagedStore
--    店长 / 经理等角色不会被自动授予（本脚本只复制已有旧权限的授予），是否开放由管理员在角色管理里手动授权。
-- 不授予任何用户直授权限，不收回任何已有权限。已有同名权限保持原样；如已停用则中止。可重复执行。
SET XACT_ABORT ON;
IF DB_NAME() <> N'HBweb'
    THROW 51000, N'目标数据库必须是 HBweb。', 1;
IF OBJECT_ID(N'dbo.HbwebSysPermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HbwebSysPermissions 权限表。', 1;
IF OBJECT_ID(N'dbo.HBwebSysRolePermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HBwebSysRolePermissions 角色权限表。', 1;

DECLARE @Actor nvarchar(100) = N'Migration_20261029_EmployeeProfilesManaged';
DECLARE @Permissions TABLE (
    Code nvarchar(100) NOT NULL PRIMARY KEY,
    Name nvarchar(100) NOT NULL,
    Description nvarchar(500) NOT NULL,
    LegacyCode nvarchar(100) NOT NULL
);
INSERT INTO @Permissions (Code, Name, Description, LegacyCode) VALUES
    (N'EmployeeProfiles.ViewManagedStore', N'查看员工个人信息（管理分店）',
     N'Web「员工个人信息维护」页面与用户管理「个人信息」页签、移动端「人员管理 > 资料」与人员详情 - 查看管理范围内员工的个人信息（银行 / 公积金 / 证件号脱敏）；管理员全部分店，店长限管理分店',
     N'EmployeeProfiles.View'),
    (N'EmployeeProfiles.EditManagedStore', N'维护员工个人信息（管理分店）',
     N'Web 与移动端 - 修改管理范围内员工的手机、性别、地址等非敏感资料（改职位类型另需修改职位类型权限；银行 / 公积金 / 证件 / 生日仍走员工提交审核）',
     N'EmployeeProfiles.Edit');

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT p.Code
        FROM dbo.HbwebSysPermissions p WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN @Permissions expected ON expected.Code = p.Code
        GROUP BY p.Code
        HAVING COUNT(*) > 1
    )
        THROW 51000, N'存在重复的员工个人信息维护权限，停止登记。', 1;
    IF EXISTS (
        SELECT 1
        FROM dbo.HbwebSysPermissions p
        INNER JOIN @Permissions expected ON expected.Code = p.Code
        WHERE p.IsDeleted = 1
    )
        THROW 51000, N'员工个人信息维护权限已停用，停止自动恢复。', 1;

    INSERT INTO dbo.HbwebSysPermissions
        (Id, Code, Name, Category, Description, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted)
    SELECT CONVERT(nvarchar(36), NEWID()), expected.Code, expected.Name, N'用户管理', expected.Description,
           SYSUTCDATETIME(), @Actor, SYSUTCDATETIME(), @Actor, 0
    FROM @Permissions expected
    WHERE NOT EXISTS (SELECT 1 FROM dbo.HbwebSysPermissions p WHERE p.Code = expected.Code);
    DECLARE @InsertedPermissions int = @@ROWCOUNT;

    -- 只复制「有效角色上的有效旧权限授予」；已存在有效新授予的组合跳过。
    INSERT INTO dbo.HBwebSysRolePermissions
        (Id, RoleGuid, PermissionCode, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted)
    SELECT CONVERT(nvarchar(36), NEWID()), legacy.RoleGuid, expected.Code,
           SYSUTCDATETIME(), @Actor, SYSUTCDATETIME(), @Actor, 0
    FROM @Permissions expected
    INNER JOIN dbo.HBwebSysRolePermissions legacy
        ON legacy.PermissionCode = expected.LegacyCode AND legacy.IsDeleted = 0
    INNER JOIN dbo.[Role] r
        ON r.RoleGUID = legacy.RoleGuid AND r.IsDeleted = 0 AND r.IsActive = 1
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.HBwebSysRolePermissions rp WITH (UPDLOCK, HOLDLOCK)
        WHERE rp.RoleGuid = legacy.RoleGuid
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

-- 回退方式：按 CreatedBy = N'Migration_20261029_EmployeeProfilesManaged' 精确定位本脚本新建的
-- HBwebSysRolePermissions 行与 HbwebSysPermissions 行，先在角色管理中取消该权限的授予，
-- 再停用权限定义。不得按权限码批量删除，以免误删管理员手工配置的同名授予。
