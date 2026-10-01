/*
  老系统操作日志页：POSM.dbo.EmployeeLogs 按分店 + 操作时间定位的索引。
  2026-09-30 只读基线身份：@@SERVERNAME=10_3_8_3；POSM database_guid=99BE94C1-3CEC-48CB-A22A-6850BF07B169。

  背景：EmployeeLogs 约 2,220 万行、6.5 GB（数据 2025-04 起，每天约 5 万行），只有随机 GUID 聚集主键 PK_EmployeeLogs_Id，
  按分店或时间的任何查询都要扫全表。页面强制「分店 + 最长 31 天」，查询语句显式指定本索引（索引缺失时直接报错，不退化为全表扫描）。
  包含列覆盖按操作类型计数、员工 / 设备下拉；当前页的详情与上传时间按主键回表（每页最多 200 行）。

  预计大小约 2.8 GB（实测平均 Id 36、StoreCode 4、EmployeeId 36、EmployeeName 7、Operation 8、DeviceCode 13 字节，
  加 OperationTime 8 字节与行开销）；2026-09-30 数据盘 C:\ 可用约 60 GB，POSM 为 SIMPLE 恢复模式。
  未经用户确认执行窗口不得执行。首个错误即停止；回退见 PosmEmployeeLogsStoreTimeIndex.Rollback.sql。
  执行：sqlcmd -S <server> -U <user> -d POSM -I -b -C -i PosmEmployeeLogsStoreTimeIndex.sql（密码经 SQLCMDPASSWORD 传入）。
*/
USE [POSM];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET DEADLOCK_PRIORITY LOW;

/* 精确目标 guard：名称、实例和 database_guid 必须同时匹配只读基线。 */
IF DB_NAME() <> N'POSM'
    THROW 52000, N'拒绝执行：当前数据库不是 POSM。', 1;
IF @@SERVERNAME <> N'10_3_8_3'
    THROW 52001, N'拒绝执行：SQL 实例与 2026-09-30 只读基线不一致。', 1;
IF NOT EXISTS (
    SELECT 1 FROM sys.database_recovery_status
    WHERE database_id=DB_ID() AND database_guid=CONVERT(uniqueidentifier, N'99BE94C1-3CEC-48CB-A22A-6850BF07B169'))
    THROW 52002, N'拒绝执行：database_guid 与 2026-09-30 只读基线不一致。', 1;
IF TRY_CONVERT(int, SERVERPROPERTY('ProductMajorVersion')) <> 16
    THROW 52003, N'拒绝执行：需要 SQL Server 2022（major 16）。', 1;
/* ONLINE=ON 需要 Enterprise 能力；已核验生产为 Developer Edition。 */
IF CONVERT(nvarchar(256), SERVERPROPERTY('Edition')) NOT LIKE N'Developer Edition%'
    THROW 52004, N'拒绝执行：Edition 与已核验的 Developer Edition 不一致。', 1;
IF OBJECT_ID(N'dbo.EmployeeLogs', N'U') IS NULL
    THROW 52005, N'拒绝执行：目标表 dbo.EmployeeLogs 不存在。', 1;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EmployeeLogs') AND name=N'StoreCode' AND system_type_id=167 AND max_length=200)
   OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EmployeeLogs') AND name=N'OperationTime' AND system_type_id=61)
   OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EmployeeLogs') AND name=N'EmployeeId' AND system_type_id=167 AND max_length=50)
   OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EmployeeLogs') AND name=N'EmployeeName' AND system_type_id=167 AND max_length=50)
   OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EmployeeLogs') AND name=N'Operation' AND system_type_id=167 AND max_length=200)
   OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EmployeeLogs') AND name=N'DeviceCode' AND system_type_id=167 AND max_length=200)
    THROW 52006, N'拒绝执行：EmployeeLogs 列类型或长度与已核验的定义不一致。', 1;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EmployeeLogs') AND name=N'IX_EmployeeLogs_StoreCode_OperationTime')
    THROW 52007, N'拒绝执行：候选索引名已存在；先人工核对定义和归属。', 1;
/* 已有以 StoreCode 或 OperationTime 打头的索引时先人工评估，不按名称盲目重复创建。 */
IF EXISTS (
    SELECT 1 FROM sys.indexes AS i
    WHERE i.object_id=OBJECT_ID(N'dbo.EmployeeLogs') AND i.type IN (1, 2) AND i.is_disabled=0
      AND (SELECT c.name FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
           WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1) IN (N'StoreCode', N'OperationTime'))
    THROW 52008, N'拒绝执行：已存在以 StoreCode / OperationTime 打头的索引；先人工评估覆盖能力。', 1;

/* 空间与日志复查：数据文件可增长且磁盘余量充足（索引约 2.8 GB，在线构建另需同等临时空间，要求至少 16 GB）；日志可增长且未被占满。 */
DECLARE @volumeFreeMB decimal(19,1), @logUsedPercent decimal(9,2), @recovery nvarchar(60), @reuse nvarchar(60);
DECLARE @rowsCanGrow bit, @logCanGrow bit;
SELECT @rowsCanGrow=MIN(CASE WHEN growth>0 AND (max_size=-1 OR max_size>size) THEN 1 ELSE 0 END)
FROM sys.database_files WHERE type_desc=N'ROWS';
SELECT @logCanGrow=MIN(CASE WHEN growth>0 AND (max_size=-1 OR max_size>size) THEN 1 ELSE 0 END)
FROM sys.database_files WHERE type_desc=N'LOG';
SELECT @logUsedPercent=used_log_space_in_percent FROM sys.dm_db_log_space_usage;
SELECT @recovery=recovery_model_desc, @reuse=log_reuse_wait_desc FROM sys.databases WHERE database_id=DB_ID();
SELECT @volumeFreeMB=MIN(vs.available_bytes/1024.0/1024.0)
FROM sys.database_files AS df
CROSS APPLY sys.dm_os_volume_stats(DB_ID(), df.file_id) AS vs
WHERE df.type_desc IN (N'ROWS', N'LOG');
IF @volumeFreeMB IS NULL OR @volumeFreeMB<16384.0
   OR @rowsCanGrow<>1 OR @logCanGrow<>1
   OR @logUsedPercent IS NULL OR @logUsedPercent>=80
   OR @recovery NOT IN (N'SIMPLE', N'FULL') OR @reuse NOT IN (N'NOTHING', N'CHECKPOINT', N'LOG_BACKUP')
    THROW 52009, N'拒绝执行：volume/growth/log/recovery/log_reuse_wait 复查未通过。', 1;

/* 在线创建：旧收银仍在持续写入，最后的 schema 锁以低优先级等待，五分钟拿不到就自行放弃，不终止上传会话；单线程减轻 4 核服务器压力。 */
SET LOCK_TIMEOUT -1;
BEGIN TRY
    BEGIN TRANSACTION;
    CREATE NONCLUSTERED INDEX [IX_EmployeeLogs_StoreCode_OperationTime]
    ON dbo.[EmployeeLogs] ([StoreCode], [OperationTime])
    INCLUDE ([EmployeeId], [EmployeeName], [Operation], [DeviceCode])
    WITH (ONLINE=ON (WAIT_AT_LOW_PRIORITY (MAX_DURATION=5 MINUTES, ABORT_AFTER_WAIT=SELF)), MAXDOP=1, SORT_IN_TEMPDB=OFF);
    EXEC sys.sp_addextendedproperty
        @name=N'HB_PosmEmployeeLogsStoreTimeIndex_20260930_Owner', @value=N'hb-posm-employee-logs-store-time-index-20260930:v1',
        @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'EmployeeLogs',
        @level2type=N'INDEX', @level2name=N'IX_EmployeeLogs_StoreCode_OperationTime';
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

/* 仅验证元数据与大小；不运行业务查询。 */
SELECT i.name, i.type_desc, i.is_disabled, i.has_filter,
       CAST(SUM(ps.used_page_count)*8.0/1024 AS decimal(18,1)) AS [UsedMB], SUM(ps.row_count) AS [Rows]
FROM sys.indexes AS i
JOIN sys.dm_db_partition_stats AS ps ON ps.object_id=i.object_id AND ps.index_id=i.index_id
WHERE i.object_id=OBJECT_ID(N'dbo.EmployeeLogs') AND i.name=N'IX_EmployeeLogs_StoreCode_OperationTime'
GROUP BY i.name, i.type_desc, i.is_disabled, i.has_filter;
GO
