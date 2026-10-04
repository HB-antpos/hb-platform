SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.EmployeeMinorCompliance', N'U') IS NULL
BEGIN
CREATE TABLE dbo.EmployeeMinorCompliance (
 Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmployeeMinorCompliance PRIMARY KEY,
 UserGUID nvarchar(50) NOT NULL, Version int NOT NULL, Revision int NOT NULL CONSTRAINT DF_MinorCompliance_Revision DEFAULT(1), Status nvarchar(30) NOT NULL,
 StateCode nvarchar(8) NOT NULL, FormType nvarchar(80) NOT NULL,
 DateOfBirth datetime2 NULL, SchoolName nvarchar(200) NULL, YearLevel nvarchar(100) NULL,
 CompletedYear10 bit NULL, EducationStatus nvarchar(40) NULL, RequiredToBeEnrolled bit NULL,
 EducationExemptionVerified bit NULL, ParticipationEndDate datetime2 NULL,
 StoreGUID nvarchar(50) NULL, StoreCode nvarchar(50) NULL, StoreTimeZoneId nvarchar(80) NULL,
 SchoolCalendarJson nvarchar(max) NULL, OtherWorkJson nvarchar(max) NULL, CommuteJson nvarchar(max) NULL, FormDataJson nvarchar(max) NULL,
 GuardianName nvarchar(200) NOT NULL, GuardianPhone nvarchar(50) NULL, GuardianEmail nvarchar(254) NULL, GuardianRelationship nvarchar(100) NULL,
 GuardianTokenHash nvarchar(500) NULL, GuardianTokenExpiresAtUtc datetime2 NULL, GuardianSignedAtUtc datetime2 NULL,
 GuardianSignedName nvarchar(200) NULL, GuardianSignatureHash nvarchar(80) NULL, GuardianTokenUsed bit NOT NULL CONSTRAINT DF_MinorGuardianTokenUsed DEFAULT(0),
 DocumentObjectKey nvarchar(500) NULL, DocumentSha256 nvarchar(64) NULL, ReviewActor nvarchar(100) NULL, ReviewedAtUtc datetime2 NULL,
 ReviewComment nvarchar(1000) NULL, ReturnFieldsJson nvarchar(1000) NULL, SubmittedBy nvarchar(100) NULL, SubmittedAtUtc datetime2 NULL,
 CreatedAt datetime2 NOT NULL, CreatedBy nvarchar(200) NULL, UpdatedAt datetime2 NULL, UpdatedBy nvarchar(200) NULL, IsDeleted bit NULL
);
CREATE UNIQUE INDEX UX_MinorCompliance_UserVersion ON dbo.EmployeeMinorCompliance(UserGUID, Version);
CREATE INDEX IX_MinorCompliance_Current ON dbo.EmployeeMinorCompliance(UserGUID, Version DESC, Status);
END;
IF COL_LENGTH(N'dbo.EmployeeMinorCompliance', N'Revision') IS NULL ALTER TABLE dbo.EmployeeMinorCompliance ADD Revision int NOT NULL CONSTRAINT DF_MinorCompliance_Revision_Existing DEFAULT(1);
IF OBJECT_ID(N'dbo.EmployeeMinorComplianceContact', N'U') IS NULL
BEGIN
CREATE TABLE dbo.EmployeeMinorComplianceContact (
 Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmployeeMinorComplianceContact PRIMARY KEY,
 ComplianceId int NOT NULL, ContactType nvarchar(30) NOT NULL, FullName nvarchar(200) NOT NULL,
 Phone nvarchar(50) NOT NULL, Mobile nvarchar(50) NULL, Email nvarchar(254) NULL,
 Address nvarchar(500) NULL, Postcode nvarchar(20) NULL, Relationship nvarchar(100) NULL,
 CreatedAt datetime2 NOT NULL, CreatedBy nvarchar(200) NULL, UpdatedAt datetime2 NULL, UpdatedBy nvarchar(200) NULL, IsDeleted bit NULL
);
CREATE INDEX IX_MinorContact_Compliance ON dbo.EmployeeMinorComplianceContact(ComplianceId);
END;
IF COL_LENGTH(N'dbo.EmployeeMinorComplianceContact', N'Mobile') IS NULL ALTER TABLE dbo.EmployeeMinorComplianceContact ADD Mobile nvarchar(50) NULL;
IF COL_LENGTH(N'dbo.EmployeeMinorComplianceContact', N'Address') IS NULL ALTER TABLE dbo.EmployeeMinorComplianceContact ADD Address nvarchar(500) NULL;
IF COL_LENGTH(N'dbo.EmployeeMinorComplianceContact', N'Postcode') IS NULL ALTER TABLE dbo.EmployeeMinorComplianceContact ADD Postcode nvarchar(20) NULL;
IF OBJECT_ID(N'dbo.EmployeeMinorComplianceAudit', N'U') IS NULL
BEGIN
CREATE TABLE dbo.EmployeeMinorComplianceAudit (
 Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmployeeMinorComplianceAudit PRIMARY KEY,
 ComplianceId int NOT NULL, Action nvarchar(50) NOT NULL, ActorUserGuid nvarchar(50) NULL,
 ActorLabel nvarchar(200) NULL, MetadataJson nvarchar(1000) NULL,
 CreatedAt datetime2 NOT NULL, CreatedBy nvarchar(200) NULL, UpdatedAt datetime2 NULL, UpdatedBy nvarchar(200) NULL, IsDeleted bit NULL
);
CREATE INDEX IX_MinorAudit_Compliance ON dbo.EmployeeMinorComplianceAudit(ComplianceId, CreatedAt);
END;
COMMIT TRANSACTION;
