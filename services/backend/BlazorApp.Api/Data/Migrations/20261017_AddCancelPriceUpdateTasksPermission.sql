-- SQL Server：部署前核对目标服务器、HBweb 数据库及 dbo.HbwebSysPermissions 表。
-- 登记「撤销价格变更任务」权限 Warehouse.CancelPriceUpdateTasks（Web 价格变更任务页与移动端「价格和标签」共用；不初始化全部种子）。
-- 只登记权限定义，不授予任何角色或用户：后端别名让持有 Warehouse.ManageProducts / Warehouse.Manage 的账号自动具备本权限，
-- Web 原有的撤销能力不会丢；登记之后管理员才能在角色管理里把它单独授予其他人（未入库的新码在角色管理里看不到、保存时会被静默丢弃）。
-- 已有同名权限保持原样；如已停用则中止，由管理员核对后处理。可重复执行。
-- 上线顺序：先执行本脚本，再部署主 API（接口策略换成新码），最后发移动端 OTA（旧移动端仍按 ManageProducts 判断，不受影响）。
SET XACT_ABORT ON;
IF DB_NAME() <> N'HBweb'
    THROW 51000, N'目标数据库必须是 HBweb。', 1;
IF OBJECT_ID(N'dbo.HbwebSysPermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HbwebSysPermissions 权限表。', 1;

DECLARE @Actor nvarchar(100) = N'Migration_20261017_CancelPriceUpdateTasks';
DECLARE @Permissions TABLE (
    Code nvarchar(100) NOT NULL PRIMARY KEY,
    Name nvarchar(100) NOT NULL,
    Description nvarchar(500) NOT NULL
);
INSERT INTO @Permissions (Code, Name, Description) VALUES
    (N'Warehouse.CancelPriceUpdateTasks', N'撤销价格变更任务',
     N'Web 页面 /warehouse/products/price-update-tasks 与移动端「价格和标签」- 撤销未完成的分店价格变更任务（按商品覆盖所有分店；只取消提醒，不修改任何价格，不可恢复）。持有「管理仓库商品」的账号自动具备');

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT p.Code
        FROM dbo.HbwebSysPermissions p WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN @Permissions expected ON expected.Code = p.Code
        GROUP BY p.Code
        HAVING COUNT(*) > 1
    )
        THROW 51000, N'存在重复的撤销价格变更任务权限，停止登记。', 1;
    IF EXISTS (
        SELECT 1
        FROM dbo.HbwebSysPermissions p
        INNER JOIN @Permissions expected ON expected.Code = p.Code
        WHERE p.IsDeleted = 1
    )
        THROW 51000, N'撤销价格变更任务权限已停用，停止自动恢复。', 1;

    INSERT INTO dbo.HbwebSysPermissions
        (Id, Code, Name, Category, Description, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted)
    SELECT CONVERT(nvarchar(36), NEWID()), expected.Code, expected.Name, N'仓库管理', expected.Description,
           SYSUTCDATETIME(), @Actor, SYSUTCDATETIME(), @Actor, 0
    FROM @Permissions expected
    WHERE NOT EXISTS (SELECT 1 FROM dbo.HbwebSysPermissions p WHERE p.Code = expected.Code);
    DECLARE @InsertedPermissions int = @@ROWCOUNT;

    COMMIT TRANSACTION;

    -- 回读本次涉及的权限，便于核对影响行数（首次执行 InsertedPermissions=1，重复执行为 0）。
    SELECT @InsertedPermissions AS InsertedPermissions;
    SELECT Id, Code, Name, Category, IsDeleted, CreatedAt, CreatedBy
    FROM dbo.HbwebSysPermissions
    WHERE Code IN (SELECT Code FROM @Permissions);
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- 回退方式：按 CreatedBy = N'Migration_20261017_CancelPriceUpdateTasks' 精确定位本脚本新建的 HbwebSysPermissions 行，
-- 先在角色管理中取消该权限的所有授予，再停用权限定义（IsDeleted=1）。不得按权限码批量删除。
