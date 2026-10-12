-- SQL Server：部署前核对目标服务器、HBweb 数据库及 dbo.HbwebSysPermissions 表。
-- 1) 登记权限 FaceCapture.Cameras.Manage（Web 后台 /system/face-cameras：管理人脸抓拍摄像机）。
-- 2) 启动时不再自动执行权限种子，必须手工登记，否则角色管理页看不到该权限、保存时会被静默丢弃。
-- 3) 刻意不授予任何角色、不写角色授予表与用户直授权限表：Admin 角色靠角色放行，其他账号由管理员在角色管理里按需授予。
-- 已有同名权限保持原样；如已停用则中止，由管理员核对后处理。可重复执行。
SET XACT_ABORT ON;
IF DB_NAME() <> N'HBweb'
    THROW 51000, N'目标数据库必须是 HBweb。', 1;
IF OBJECT_ID(N'dbo.HbwebSysPermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HbwebSysPermissions 权限表。', 1;

DECLARE @Actor nvarchar(100) = N'Migration_20261013_FaceCameraManage';
DECLARE @Permissions TABLE (
    Code nvarchar(100) NOT NULL PRIMARY KEY,
    Name nvarchar(100) NOT NULL,
    Description nvarchar(500) NOT NULL
);
INSERT INTO @Permissions (Code, Name, Description) VALUES
    (N'FaceCapture.Cameras.Manage', N'管理人脸抓拍摄像机', N'Web 页面 /system/face-cameras - 新增、编辑、停用人脸抓拍摄像机，重新生成 token，查看最近抓拍记录（仅元数据，不含图片）');

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT p.Code
        FROM dbo.HbwebSysPermissions p WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN @Permissions expected ON expected.Code = p.Code
        GROUP BY p.Code
        HAVING COUNT(*) > 1
    )
        THROW 51000, N'存在重复的人脸抓拍摄像机管理权限，停止登记。', 1;
    IF EXISTS (
        SELECT 1
        FROM dbo.HbwebSysPermissions p
        INNER JOIN @Permissions expected ON expected.Code = p.Code
        WHERE p.IsDeleted = 1
    )
        THROW 51000, N'人脸抓拍摄像机管理权限已停用，停止自动恢复。', 1;

    INSERT INTO dbo.HbwebSysPermissions
        (Id, Code, Name, Category, Description, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted)
    SELECT CONVERT(nvarchar(36), NEWID()), expected.Code, expected.Name, N'系统管理', expected.Description,
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

-- 回退方式：按 CreatedBy = N'Migration_20261013_FaceCameraManage' 精确定位本脚本新建的
-- HbwebSysPermissions 行，先在角色管理中取消该权限的授予，再停用权限定义（IsDeleted = 1）。
-- 不得按权限码批量删除，以免误删管理员手工配置的同名授予。
