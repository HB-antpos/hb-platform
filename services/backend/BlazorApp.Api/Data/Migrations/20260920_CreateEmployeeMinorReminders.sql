-- 可重复执行；上线前随未成年用工档案迁移在目标环境核验。此脚本不修改历史考勤。
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.EmployeeMinorReminder', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.EmployeeMinorReminder (
  Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmployeeMinorReminder PRIMARY KEY,
  Fingerprint nvarchar(64) NOT NULL, UserGUID nvarchar(50) NOT NULL, StoreCode nvarchar(50) NOT NULL,
  ScheduleGuid nvarchar(50) NULL, RuleId nvarchar(80) NOT NULL, Severity nvarchar(30) NOT NULL,
  RuleCategory nvarchar(40) NOT NULL, Message nvarchar(1000) NOT NULL, SourceUrl nvarchar(500) NULL,
  WorkDate datetime2 NULL, ActualMinutes int NULL, LimitMinutes int NULL, [Trigger] nvarchar(40) NOT NULL,
  Status nvarchar(20) NOT NULL, Revision int NOT NULL CONSTRAINT DF_MinorReminderRevision DEFAULT(1),
  ActionActor nvarchar(200) NULL, ActionComment nvarchar(1000) NULL, ActionAtUtc datetime2 NULL,
  CreatedAt datetime2 NOT NULL, CreatedBy nvarchar(200) NULL, UpdatedAt datetime2 NULL, UpdatedBy nvarchar(200) NULL, IsDeleted bit NULL
 );
 CREATE UNIQUE INDEX UX_MinorReminder_Fingerprint ON dbo.EmployeeMinorReminder(Fingerprint);
 CREATE INDEX IX_MinorReminder_StoreStatus ON dbo.EmployeeMinorReminder(StoreCode, Status, CreatedAt DESC);
END;
IF OBJECT_ID(N'dbo.EmployeeMinorReminderEvent', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.EmployeeMinorReminderEvent (
  Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmployeeMinorReminderEvent PRIMARY KEY,
  ReminderId int NOT NULL, Action nvarchar(30) NOT NULL, Actor nvarchar(200) NOT NULL, Comment nvarchar(1000) NULL,
  CreatedAt datetime2 NOT NULL, CreatedBy nvarchar(200) NULL, UpdatedAt datetime2 NULL, UpdatedBy nvarchar(200) NULL, IsDeleted bit NULL
 );
 CREATE INDEX IX_MinorReminderEvent_Record ON dbo.EmployeeMinorReminderEvent(ReminderId, CreatedAt);
END;
COMMIT TRANSACTION;
