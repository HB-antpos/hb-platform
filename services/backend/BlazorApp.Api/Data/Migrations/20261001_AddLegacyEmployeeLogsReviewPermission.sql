-- SQL Server：部署前核对目标服务器、HBweb 数据库及 dbo.HbwebSysPermissions 表。
-- 仅登记一个权限，不初始化全部种子，也不授予任何角色或用户。
-- 已有同名权限保持原样；如已停用则中止，由管理员核对后处理。
SET XACT_ABORT ON;
IF DB_NAME() <> N'HBweb'
    THROW 51000, N'目标数据库必须是 HBweb。', 1;
IF OBJECT_ID(N'dbo.HbwebSysPermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HbwebSysPermissions 权限表。', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @PermissionCode nvarchar(100) = N'LegacyEmployeeLogs.Review';
    DECLARE @ExistingCount int;
    SELECT @ExistingCount = COUNT(*)
    FROM dbo.HbwebSysPermissions WITH (UPDLOCK, HOLDLOCK)
    WHERE Code = @PermissionCode;

    IF @ExistingCount > 1
        THROW 51000, N'存在重复老系统异常核查权限，停止登记。', 1;
    IF EXISTS (
        SELECT 1 FROM dbo.HbwebSysPermissions
        WHERE Code = @PermissionCode AND IsDeleted = 1
    )
        THROW 51000, N'老系统异常核查权限已停用，停止自动恢复。', 1;

    IF @ExistingCount = 0
    BEGIN
        INSERT INTO dbo.HbwebSysPermissions
            (Id, Code, Name, Category, Description, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted)
        VALUES
            (CONVERT(nvarchar(36), NEWID()), @PermissionCode, N'核查老系统异常操作', N'POS 审计',
             N'Web 页面 /pos-admin/legacy-employee-logs 与移动端「员工操作日志」- 对命中异常规则的操作标记确认正常或需跟进，并可撤销',
             SYSUTCDATETIME(), N'Migration_20261001_LegacyEmployeeLogsReview',
             SYSUTCDATETIME(), N'Migration_20261001_LegacyEmployeeLogsReview', 0);
    END;

    COMMIT TRANSACTION;
    -- 留存本次精确对象的回读结果；重复执行不会覆盖已有配置。
    SELECT Id, Code, Name, Category, IsDeleted, CreatedAt, CreatedBy
    FROM dbo.HbwebSysPermissions WHERE Code = @PermissionCode;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- 回退方式：确认本脚本新建的 CreatedBy 标记及回读 Id，且尚未配置任何
-- HBwebSysRolePermissions / HBwebSysUserPermissions 关联后，按该 Id + Code
-- 在权限管理中停用记录。已有同名权限不属于本次回退范围。
