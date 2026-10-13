/*
  人脸比对服务：给人脸接收服务的受限数据库账号 face_ingest_app 追加的表授权（设计 13.3 / 14.4）。
  ---------------------------------------------------------------------------------------------
  - 不由任何代码执行：由用户 / DBA 在以下两步之后手工执行一次（见 docs/runbooks/face-ingest.md 第 16 节）：
      1) 主 API 迁移 20261014.001-face-match 已建好九张表（--schema=migrate）；
      2) face-ingest-db-login.sql 已建好 face_ingest_app 登录与数据库用户。
  - 追加的授权（逐表最小权限，不授 DELETE / ALTER，不加入任何数据库角色）：
      dbo.FaceStoreProfile      SELECT
      dbo.FaceTrafficBucket     SELECT + INSERT + UPDATE
      dbo.FaceVisitGroup        SELECT + INSERT + UPDATE(VisitorCount)
      dbo.FaceMatchEvaluation   INSERT
      dbo.FaceUnwelcomeEntry    SELECT
      dbo.FaceMatchTask         SELECT + INSERT
    其余表（FaceConsentRecord、FaceEnrollment、FaceAuditLog）接收端无权访问：员工同意、登记与审计只在主 API 内读写。
  - 可重复执行：GRANT 本身幂等；任一前置缺失即报错中止，不创建登录或用户。
  - 回退：对上面每一条 GRANT 执行同对象同权限的 REVOKE（见文末注释），不影响 face-ingest-db-login.sql 授予的原有权限。

  用法（在能连主库的机器上，以有授权权限的管理员账号执行）：
    export MAIN_DATABASE=HBweb
    sqlcmd -S <server> -U <admin> -I -b -i services/backend/SqlScripts/face-match-db-grants.sql
  sqlcmd 会把同名环境变量当作脚本变量 $(MAIN_DATABASE) 使用。本文件里没有任何密码。
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

USE [$(MAIN_DATABASE)];

IF OBJECT_ID(N'dbo.FaceStoreProfile', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceTrafficBucket', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceVisitGroup', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceMatchEvaluation', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceUnwelcomeEntry', N'U') IS NULL
 OR OBJECT_ID(N'dbo.FaceMatchTask', N'U') IS NULL
    THROW 52750, N'Face match tables are missing: run the main API with --schema=migrate first (20261014.001-face-match).', 1;

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N'face_ingest_app')
    THROW 52751, N'Database user face_ingest_app does not exist: run face-ingest-db-login.sql first.', 1;

GRANT SELECT ON OBJECT::[dbo].[FaceStoreProfile] TO [face_ingest_app];
GRANT SELECT, INSERT, UPDATE ON OBJECT::[dbo].[FaceTrafficBucket] TO [face_ingest_app];
GRANT SELECT, INSERT ON OBJECT::[dbo].[FaceVisitGroup] TO [face_ingest_app];
GRANT UPDATE ([VisitorCount]) ON OBJECT::[dbo].[FaceVisitGroup] TO [face_ingest_app];
GRANT INSERT ON OBJECT::[dbo].[FaceMatchEvaluation] TO [face_ingest_app];
GRANT SELECT ON OBJECT::[dbo].[FaceUnwelcomeEntry] TO [face_ingest_app];
GRANT SELECT, INSERT ON OBJECT::[dbo].[FaceMatchTask] TO [face_ingest_app];

-- 核对：下面列出该用户在人脸比对六张表上的全部权限，应与上面的 GRANT 完全一致（不含任何 DELETE / ALTER / CONTROL）。
SELECT OBJECT_NAME(p.[major_id]) AS [object_name],
       COL_NAME(p.[major_id], p.[minor_id]) AS [column_name],
       p.[permission_name],
       p.[state_desc]
FROM sys.database_permissions p
WHERE p.[grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'face_ingest_app')
  AND p.[class] = 1
  AND OBJECT_NAME(p.[major_id]) IN (
      N'FaceStoreProfile', N'FaceTrafficBucket', N'FaceVisitGroup',
      N'FaceMatchEvaluation', N'FaceUnwelcomeEntry', N'FaceMatchTask')
ORDER BY [object_name], p.[permission_name];

/*
  回退（按需手工执行，只撤销本脚本追加的授权）：
  REVOKE SELECT ON OBJECT::[dbo].[FaceStoreProfile] FROM [face_ingest_app];
  REVOKE SELECT, INSERT, UPDATE ON OBJECT::[dbo].[FaceTrafficBucket] FROM [face_ingest_app];
  REVOKE SELECT, INSERT ON OBJECT::[dbo].[FaceVisitGroup] FROM [face_ingest_app];
  REVOKE UPDATE ([VisitorCount]) ON OBJECT::[dbo].[FaceVisitGroup] FROM [face_ingest_app];
  REVOKE INSERT ON OBJECT::[dbo].[FaceMatchEvaluation] FROM [face_ingest_app];
  REVOKE SELECT ON OBJECT::[dbo].[FaceUnwelcomeEntry] FROM [face_ingest_app];
  REVOKE SELECT, INSERT ON OBJECT::[dbo].[FaceMatchTask] FROM [face_ingest_app];
*/
