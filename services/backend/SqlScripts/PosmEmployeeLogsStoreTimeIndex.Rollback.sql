/* 精确回退：只删除本方案创建、且定义和专属 extended property 都匹配的 IX_EmployeeLogs_StoreCode_OperationTime。
   注意：删除后「老系统操作日志」页面的查询会因索引提示失效而直接报错（不会退化为全表扫描），应先下线页面入口。 */
USE [POSM];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET DEADLOCK_PRIORITY LOW;
SET LOCK_TIMEOUT -1;

IF DB_NAME()<>N'POSM' OR @@SERVERNAME<>N'10_3_8_3'
    THROW 52100, N'拒绝回退：数据库或 SQL 实例与 2026-09-30 基线不一致。', 1;
IF NOT EXISTS (SELECT 1 FROM sys.database_recovery_status WHERE database_id=DB_ID()
               AND database_guid=CONVERT(uniqueidentifier,N'99BE94C1-3CEC-48CB-A22A-6850BF07B169'))
    THROW 52101, N'拒绝回退：database_guid 与基线不一致。', 1;

DECLARE @owner nvarchar(256)=N'hb-posm-employee-logs-store-time-index-20260930:v1';
/* 先验完整定义和唯一归属；不匹配就停，不猜测删除。 */
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EmployeeLogs') AND name=N'IX_EmployeeLogs_StoreCode_OperationTime')
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes AS i
        JOIN sys.extended_properties AS ep ON ep.class=7 AND ep.major_id=i.object_id AND ep.minor_id=i.index_id
            AND ep.name=N'HB_PosmEmployeeLogsStoreTimeIndex_20260930_Owner' AND CONVERT(nvarchar(256),ep.value)=@owner
        WHERE i.object_id=OBJECT_ID(N'dbo.EmployeeLogs') AND i.name=N'IX_EmployeeLogs_StoreCode_OperationTime'
          AND i.type=2 AND i.is_unique=0 AND i.is_primary_key=0 AND i.has_filter=0
          AND (SELECT STRING_AGG(CONCAT(CASE WHEN ic.is_included_column=1 THEN N'I:' ELSE N'K:' END,c.name),N';') WITHIN GROUP (ORDER BY CASE WHEN ic.is_included_column=0 THEN 0 ELSE 1 END, ic.key_ordinal,ic.index_column_id)
               FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
               WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id)
              =N'K:StoreCode;K:OperationTime;I:EmployeeId;I:EmployeeName;I:Operation;I:DeviceCode')
        THROW 52102, N'拒绝回退：定义或专属 extended property 不匹配。', 1;

    DROP INDEX [IX_EmployeeLogs_StoreCode_OperationTime] ON dbo.[EmployeeLogs];
END;

SELECT N'IX_EmployeeLogs_StoreCode_OperationTime' AS [Index],
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EmployeeLogs') AND name=N'IX_EmployeeLogs_StoreCode_OperationTime')
            THEN N'仍存在' ELSE N'已不存在' END AS [State];
GO
