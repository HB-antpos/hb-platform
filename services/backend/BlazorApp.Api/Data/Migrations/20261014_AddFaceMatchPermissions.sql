-- SQL Server：部署前核对目标服务器、HBweb 数据库及 dbo.HbwebSysPermissions 表。
-- 1) 登记五个权限（Web 后台 /system/face-match「人脸比对」）：
--    FaceMatch.Manage、FaceMatch.Traffic.View、FaceMatch.Unwelcome.Manage、FaceMatch.Unwelcome.Approve、FaceMatch.Unwelcome.Review。
-- 2) 启动时不再自动执行权限种子，必须手工登记，否则角色管理页看不到这些权限、保存时会被静默丢弃。
-- 3) 刻意不授予任何角色、不写角色授予表与用户直授权限表：Admin 角色靠角色放行，其他账号由管理员在角色管理里按需授予；
--    五个权限互相独立（管理档案不自动包含看客流，批准条目不自动能复核命中），涉及敏感生物识别信息，不要整体打包授予。
-- 已有同名权限保持原样；如已停用则中止，由管理员核对后处理。可重复执行。
SET XACT_ABORT ON;
IF DB_NAME() <> N'HBweb'
    THROW 51000, N'目标数据库必须是 HBweb。', 1;
IF OBJECT_ID(N'dbo.HbwebSysPermissions', N'U') IS NULL
    THROW 51000, N'缺少 dbo.HbwebSysPermissions 权限表。', 1;

DECLARE @Actor nvarchar(100) = N'Migration_20261014_FaceMatch';
DECLARE @Permissions TABLE (
    Code nvarchar(100) NOT NULL PRIMARY KEY,
    Name nvarchar(100) NOT NULL,
    Description nvarchar(500) NOT NULL
);
INSERT INTO @Permissions (Code, Name, Description) VALUES
    (N'FaceMatch.Manage', N'管理人脸比对', N'Web 页面 /system/face-match - 维护门店人脸比对档案与启用前置条件、记录员工同意、登记与撤回员工登记照、查看人脸比对审计（敏感生物识别信息）'),
    (N'FaceMatch.Traffic.View', N'查看人脸比对客流统计', N'Web 页面 /system/face-match「客流统计」- 查看按门店汇总的抓拍、员工、唯一访客（估算）、重复、低质量计数（只有计数，没有任何生物信息）'),
    (N'FaceMatch.Unwelcome.Manage', N'管理不受欢迎人员名单', N'Web 页面 /system/face-match「不受欢迎人员名单」- 提交、查看、撤回不受欢迎人员名单条目并上传参考照片（敏感生物识别信息）'),
    (N'FaceMatch.Unwelcome.Approve', N'批准不受欢迎人员名单条目', N'Web 页面 /system/face-match「不受欢迎人员名单」- 批准条目入名单；批准人必须不同于提交人，「其他」类别需要两位不同的批准人'),
    (N'FaceMatch.Unwelcome.Review', N'复核不受欢迎人员名单命中', N'Web 页面 /system/face-match「复核任务」- 接收所属门店的疑似命中提示，并排查看现场抓拍与参考照片，参与两人复核（敏感生物识别信息，每次查看都会记审计）');

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT p.Code
        FROM dbo.HbwebSysPermissions p WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN @Permissions expected ON expected.Code = p.Code
        GROUP BY p.Code
        HAVING COUNT(*) > 1
    )
        THROW 51000, N'存在重复的人脸比对权限，停止登记。', 1;
    IF EXISTS (
        SELECT 1
        FROM dbo.HbwebSysPermissions p
        INNER JOIN @Permissions expected ON expected.Code = p.Code
        WHERE p.IsDeleted = 1
    )
        THROW 51000, N'人脸比对权限已停用，停止自动恢复。', 1;

    INSERT INTO dbo.HbwebSysPermissions
        (Id, Code, Name, Category, Description, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted)
    SELECT CONVERT(nvarchar(36), NEWID()), expected.Code, expected.Name, N'系统管理', expected.Description,
           SYSUTCDATETIME(), @Actor, SYSUTCDATETIME(), @Actor, 0
    FROM @Permissions expected
    WHERE NOT EXISTS (SELECT 1 FROM dbo.HbwebSysPermissions p WHERE p.Code = expected.Code);
    DECLARE @InsertedPermissions int = @@ROWCOUNT;

    COMMIT TRANSACTION;

    -- 回读本次涉及的权限，便于核对影响行数（全新环境应为 5 行）。
    SELECT @InsertedPermissions AS InsertedPermissions;
    SELECT Id, Code, Name, Category, IsDeleted, CreatedAt, CreatedBy
    FROM dbo.HbwebSysPermissions
    WHERE Code IN (SELECT Code FROM @Permissions);
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- 回退方式：按 CreatedBy = N'Migration_20261014_FaceMatch' 精确定位本脚本新建的
-- HbwebSysPermissions 行，先在角色管理中取消这些权限的授予，再停用权限定义（IsDeleted = 1）。
-- 不得按权限码批量删除，以免误删管理员手工配置的同名授予。
