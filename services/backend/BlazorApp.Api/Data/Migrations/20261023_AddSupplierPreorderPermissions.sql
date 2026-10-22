-- SQL Server：部署前核对目标服务器、HBweb 数据库及 dbo.HbwebSysPermissions 表。
-- 登记两个权限：SupplierPreorder.Manage（管理供应商节日预订单）、SupplierPreorder.Order（预订单下单入口）。
-- 不初始化全部种子，也不授予任何角色或用户（默认无人具备，由管理员在「角色管理」里手动勾选；刻意不设别名）。
-- 「能替哪家店下单」不靠权限码，只认 Web「下单人员授权」页（dbo.SupplierPreorderOrdererStore）。
-- 已有同名权限保持原样；如已停用则中止，由管理员核对后处理。可重复执行。
-- 上线顺序：先 --schema=migrate（迁移 20261023.001-supplier-preorder 新建 10 张表），再执行本脚本，再部署主 API，最后部署 Web。
SET XACT_ABORT ON;
IF DB_NAME() <> N'HBweb'
    THROW 51000, N'目标数据库必须是 HBweb。', 1;
IF OBJECT_ID(N'dbo.HbwebSysPermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HbwebSysPermissions 权限表。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @Codes TABLE (Code nvarchar(100) NOT NULL PRIMARY KEY, Name nvarchar(200) NOT NULL, Description nvarchar(1000) NOT NULL);
    INSERT INTO @Codes (Code, Name, Description) VALUES
      (N'SupplierPreorder.Manage', N'管理供应商节日预订单',
       N'Web 页面 /supplier-preorders 与 /supplier-preorders/orderers - 导入 KFC、Dats、Brazco 等节日订单表，开启参与分店，维护下单人员授权，截止锁定并导出汇总（截止后仍可修改）；默认无人具备，管理员手动授权'),
      (N'SupplierPreorder.Order', N'供应商节日预订单下单',
       N'Web 页面 /supplier-preorder-ordering - 预订单下单入口；能替哪家店下单只认「下单人员授权」页，不看可管理分店或店长身份；默认无人具备，管理员手动授权');

    IF EXISTS (
        SELECT p.Code FROM dbo.HbwebSysPermissions p WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN @Codes c ON c.Code = p.Code
        GROUP BY p.Code HAVING COUNT(*) > 1
    )
        THROW 51000, N'存在重复的供应商节日预订单权限，停止登记。', 1;
    IF EXISTS (
        SELECT 1 FROM dbo.HbwebSysPermissions p
        INNER JOIN @Codes c ON c.Code = p.Code
        WHERE p.IsDeleted = 1
    )
        THROW 51000, N'供应商节日预订单权限已停用，停止自动恢复。', 1;

    INSERT INTO dbo.HbwebSysPermissions
        (Id, Code, Name, Category, Description, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted)
    SELECT CONVERT(nvarchar(36), NEWID()), c.Code, c.Name, N'仓库管理', c.Description,
           SYSUTCDATETIME(), N'Migration_20261023_SupplierPreorder',
           SYSUTCDATETIME(), N'Migration_20261023_SupplierPreorder', 0
    FROM @Codes c
    WHERE NOT EXISTS (SELECT 1 FROM dbo.HbwebSysPermissions p WHERE p.Code = c.Code);

    COMMIT TRANSACTION;
    -- 留存本次精确对象的回读结果；重复执行不会覆盖已有配置。
    SELECT p.Id, p.Code, p.Name, p.Category, p.IsDeleted, p.CreatedAt, p.CreatedBy
    FROM dbo.HbwebSysPermissions p
    WHERE p.Code IN (N'SupplierPreorder.Manage', N'SupplierPreorder.Order');
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- 回退方式：确认本脚本新建的 CreatedBy 标记及回读 Id，且尚未配置任何
-- HBwebSysRolePermissions / HBwebSysUserPermissions 关联后，按该 Id + Code
-- 在权限管理中停用记录。已有同名权限不属于本次回退范围。
