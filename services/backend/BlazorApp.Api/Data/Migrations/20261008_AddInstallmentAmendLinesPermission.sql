-- SQL Server：部署前核对目标服务器、HBweb 数据库及 dbo.HbwebSysPermissions 表。
-- 1) 登记 POS 权限 Permissions.PosTerminal.Installments.AmendLines（收银端历史订单 - 分期订单明细「修改商品列表」）。
-- 2) 启动时不再自动执行权限种子，必须手工登记，否则角色管理页看不到该权限、保存时会被静默丢弃。
-- 3) 刻意不授予任何角色、不写角色授予表与用户直授权限表：由管理员在角色管理 / 用户门店 POS 权限里按需授予。
-- 已有同名权限保持原样；如已停用则中止，由管理员核对后处理。可重复执行。
SET XACT_ABORT ON;
IF DB_NAME() <> N'HBweb'
    THROW 51000, N'目标数据库必须是 HBweb。', 1;
IF OBJECT_ID(N'dbo.HbwebSysPermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HbwebSysPermissions 权限表。', 1;

DECLARE @Actor nvarchar(100) = N'Migration_20261008_InstallmentAmendLines';
DECLARE @Permissions TABLE (
    Code nvarchar(100) NOT NULL PRIMARY KEY,
    Name nvarchar(100) NOT NULL,
    Description nvarchar(500) NOT NULL
);
INSERT INTO @Permissions (Code, Name, Description) VALUES
    (N'Permissions.PosTerminal.Installments.AmendLines', N'修改分期商品列表', N'收银端历史订单 - 分期订单明细修改商品列表按钮');

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT p.Code
        FROM dbo.HbwebSysPermissions p WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN @Permissions expected ON expected.Code = p.Code
        GROUP BY p.Code
        HAVING COUNT(*) > 1
    )
        THROW 51000, N'存在重复的修改分期商品列表权限，停止登记。', 1;
    IF EXISTS (
        SELECT 1
        FROM dbo.HbwebSysPermissions p
        INNER JOIN @Permissions expected ON expected.Code = p.Code
        WHERE p.IsDeleted = 1
    )
        THROW 51000, N'修改分期商品列表权限已停用，停止自动恢复。', 1;

    INSERT INTO dbo.HbwebSysPermissions
        (Id, Code, Name, Category, Description, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted)
    SELECT CONVERT(nvarchar(36), NEWID()), expected.Code, expected.Name, N'POS 分期', expected.Description,
           SYSUTCDATETIME(), @Actor, SYSUTCDATETIME(), @Actor, 0
    FROM @Permissions expected
    WHERE NOT EXISTS (SELECT 1 FROM dbo.HbwebSysPermissions p WHERE p.Code = expected.Code);
    DECLARE @InsertedPermissions int = @@ROWCOUNT;

    COMMIT TRANSACTION;

    -- 回读本次涉及的权限，便于核对影响行数。
    SELECT @InsertedPermissions AS InsertedPermissions;
    SELECT Id, Code, Name, Category, IsDeleted, CreatedAt, CreatedBy
    FROM dbo.HbwebSysPermissions
    WHERE Code IN (SELECT Code FROM @Permissions);
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- 回退方式：按 CreatedBy = N'Migration_20261008_InstallmentAmendLines' 精确定位本脚本新建的
-- HbwebSysPermissions 行，先在角色管理中取消该权限的授予，再停用权限定义。
-- 不得按权限码批量删除，以免误删管理员手工配置的同名授予。
