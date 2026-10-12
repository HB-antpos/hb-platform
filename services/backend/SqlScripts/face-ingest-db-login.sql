/*
  人脸接收服务（BlazorApp.FaceIngest P1）专用受限数据库账号 face_ingest_app。
  ---------------------------------------------------------------------------------------------
  - 不由任何代码执行：由用户 / DBA 在主 API 迁移 20261013.001-face-capture 建好三张表之后手工执行一次（见 docs/runbooks/face-ingest.md）。
  - 只授人脸三张表的最小权限：
      dbo.FaceCamera        SELECT + UPDATE(LastSeenAtUtc)
      dbo.FaceCaptureEvent  SELECT + INSERT
      dbo.FaceCaptureImage  SELECT + INSERT
    不授 DELETE / ALTER / 其他任何表；不加入任何数据库角色。
  - 密码只经 sqlcmd 变量从环境变量传入、只写进服务器 /www/HBWeb/BackEnd/face-ingest/.env 的 FACE_INGEST_DB_CONNECTION，
    绝不写进本文件、Git、聊天或工单。本文件里没有任何真实密码。
  - 可重复执行：登录 / 用户已存在时跳过创建，授权语句本身幂等。

  用法（在能连主库的机器上，以有 ALTER ANY LOGIN 权限的管理员账号执行）：
    read -rs FACE_INGEST_DB_PASSWORD && export FACE_INGEST_DB_PASSWORD    # 输入新密码（不回显、不进 shell 历史）
    export MAIN_DATABASE=HBweb                                             # 主库名
    sqlcmd -S <server> -U <admin> -I -b -i services/backend/SqlScripts/face-ingest-db-login.sql
    unset FACE_INGEST_DB_PASSWORD
  sqlcmd 会把同名环境变量当作脚本变量 $(FACE_INGEST_DB_PASSWORD) / $(MAIN_DATABASE) 使用。
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

USE [master];

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE [name] = N'face_ingest_app')
BEGIN
    -- 密码策略跟随服务器 Windows / Linux 策略；不设过期，避免服务在半夜因密码过期断连（需要轮换时按 runbook 手工改）。
    CREATE LOGIN [face_ingest_app]
        WITH PASSWORD = N'$(FACE_INGEST_DB_PASSWORD)',
             DEFAULT_DATABASE = [$(MAIN_DATABASE)],
             CHECK_POLICY = ON,
             CHECK_EXPIRATION = OFF;
    PRINT N'Created login face_ingest_app.';
END
ELSE
BEGIN
    PRINT N'Login face_ingest_app already exists; password not changed (rotate with ALTER LOGIN when needed).';
END;

USE [$(MAIN_DATABASE)];

IF OBJECT_ID(N'dbo.FaceCamera', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceCaptureEvent', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceCaptureImage', N'U') IS NULL
    THROW 52650, N'Face capture tables are missing: run the main API with --schema=migrate first (20261013.001-face-capture).', 1;

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N'face_ingest_app')
BEGIN
    CREATE USER [face_ingest_app] FOR LOGIN [face_ingest_app] WITH DEFAULT_SCHEMA = [dbo];
    PRINT N'Created database user face_ingest_app.';
END;

GRANT SELECT ON OBJECT::[dbo].[FaceCamera] TO [face_ingest_app];
GRANT UPDATE ([LastSeenAtUtc]) ON OBJECT::[dbo].[FaceCamera] TO [face_ingest_app];
GRANT SELECT, INSERT ON OBJECT::[dbo].[FaceCaptureEvent] TO [face_ingest_app];
GRANT SELECT, INSERT ON OBJECT::[dbo].[FaceCaptureImage] TO [face_ingest_app];

-- 核对：应只列出上面 4 条授权涉及的 6 行权限（FaceCamera SELECT、UPDATE(LastSeenAtUtc)；另两张表各 SELECT、INSERT），
-- 且该用户不属于任何数据库角色。
SELECT OBJECT_NAME(p.[major_id]) AS [object_name],
       COL_NAME(p.[major_id], p.[minor_id]) AS [column_name],
       p.[permission_name],
       p.[state_desc]
FROM sys.database_permissions p
WHERE p.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'face_ingest_app')
  AND p.[class] = 1
ORDER BY [object_name], p.[permission_name];

SELECT r.[name] AS [role_name]
FROM sys.database_role_members m
JOIN sys.database_principals r ON r.[principal_id] = m.[role_principal_id]
WHERE m.[member_principal_id] = DATABASE_PRINCIPAL_ID(N'face_ingest_app');
