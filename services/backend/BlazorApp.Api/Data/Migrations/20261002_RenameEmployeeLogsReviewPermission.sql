-- SQL Server：部署前核对目标服务器、HBweb 数据库及 dbo.HbwebSysPermissions 表。
-- 「核查老系统异常操作」改为老收银、新收银共用的「核查员工操作日志异常」：只改一行的显示名称与说明，
-- 权限编码 LegacyEmployeeLogs.Review 不变，已有角色 / 用户授权不受影响。
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
    WHERE Code = @PermissionCode AND IsDeleted = 0;

    -- 预期恰好 1 行；0 行说明 20261001 脚本未执行，多行说明数据异常，均停止。
    IF @ExistingCount <> 1
        THROW 51000, N'核查权限行数不是 1，停止改名。', 1;

    -- 回读改名前的值，作为回退依据。
    SELECT Id, Code, Name, Description FROM dbo.HbwebSysPermissions
    WHERE Code = @PermissionCode AND IsDeleted = 0;

    UPDATE dbo.HbwebSysPermissions
    SET Name = N'核查员工操作日志异常',
        Description = N'Web 页面 /pos-admin/operation-logs 与移动端「员工操作日志」- 对老收银、新收银命中异常规则的操作标记确认正常或需跟进，并可撤销',
        UpdatedAt = SYSUTCDATETIME(),
        UpdatedBy = N'Migration_20261002_EmployeeLogsReviewRename'
    WHERE Code = @PermissionCode AND IsDeleted = 0;

    COMMIT TRANSACTION;
    SELECT Id, Code, Name, Description, UpdatedAt, UpdatedBy
    FROM dbo.HbwebSysPermissions WHERE Code = @PermissionCode;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- 回退方式：用上面回读的改名前 Name / Description，按同一 Code 改回。
