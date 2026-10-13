namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 班中定位批量上传（完整轨迹）的幂等所需结构：dbo.AttendanceLocationSample 加两列并建一条过滤唯一索引。
/// - ClientSampleId nvarchar(50) NULL：客户端生成的采样点 GUID，批量上传的幂等键；旧版单条上传不带，保持 NULL。
/// - IsMocked bit NULL：设备上报的模拟定位标记，旧数据为 NULL（未知）。
/// - UX_AttendanceLocationSample_User_ClientSample：UNIQUE (UserGuid, ClientSampleId) WHERE ClientSampleId IS NOT NULL，
///   同一用户同一个客户端 ID 只能入库一次；历史行 ClientSampleId 全为 NULL，不参与唯一性，建索引不会冲突。
/// 定位样本表随基线 20260827.001（SqlSugarContext 的 CodeFirst tableTypes）建出，新列只能走独立版本号迁移；
/// 生产须显式执行 --schema=migrate，且要先于启用批量接口的新版 API 上线（缺索引时幂等兜底失效，启动门禁也会拒绝）。
/// 全新库由 CodeFirst 按实体属性建出同类型的两列（nvarchar(50) NULL / bit NULL），本迁移对已存在的列是空操作。
/// 错误号区间 52900–52909（已用 52902）。
/// </summary>
internal static class AttendanceLocationSampleBatchSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.AttendanceLocationSample', N'U') IS NULL
    THROW 52900, N'Attendance location sample table is missing; apply the HBweb baseline first.', 1;

BEGIN TRANSACTION;
BEGIN TRY
    -- 两列都可空且无默认值：仅改元数据，不重写存量行；COL_LENGTH 判断保证重复执行是空操作。
    IF COL_LENGTH(N'dbo.AttendanceLocationSample', N'ClientSampleId') IS NULL
        ALTER TABLE [dbo].[AttendanceLocationSample] ADD [ClientSampleId] nvarchar(50) NULL;
    IF COL_LENGTH(N'dbo.AttendanceLocationSample', N'IsMocked') IS NULL
        ALTER TABLE [dbo].[AttendanceLocationSample] ADD [IsMocked] bit NULL;

    -- 过滤唯一索引引用本批次刚加的列，用动态 SQL 推迟到列存在后再编译，避免整批编译时报列名无效。
    -- 过滤条件让历史行（ClientSampleId 为 NULL）和旧版单条上传不受唯一性约束。
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceLocationSample')
          AND [name] = N'UX_AttendanceLocationSample_User_ClientSample'
    )
        EXEC(N'CREATE UNIQUE NONCLUSTERED INDEX [UX_AttendanceLocationSample_User_ClientSample]
            ON [dbo].[AttendanceLocationSample] ([UserGuid], [ClientSampleId])
            WHERE [ClientSampleId] IS NOT NULL;');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：ClientSampleId 必须是 nvarchar(50) NULL（max_length 按字节计为 100），IsMocked 必须是 bit NULL；
    // 唯一索引必须存在、唯一、带过滤条件 [ClientSampleId] IS NOT NULL，且键列顺序为 (UserGuid, ClientSampleId)，
    // 否则并发重复提交无法被数据库兜住。签名不符只能通过显式迁移修复，运行时绝不自动改写。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.AttendanceLocationSample', N'U') IS NULL
    THROW 52900, N'Attendance location sample table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'ClientSampleId', N'nvarchar', 100, 1),
      (N'IsMocked', N'bit', 1, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(N'dbo.AttendanceLocationSample') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 52901, N'Attendance location sample batch column signature is incompatible.', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.AttendanceLocationSample')
      AND i.[name] = N'UX_AttendanceLocationSample_User_ClientSample'
      AND i.[is_unique] = 1
      AND i.[has_filter] = 1
      AND CHARINDEX(N'[ClientSampleId] IS NOT NULL', i.[filter_definition]) > 0
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 2
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 1 AND c.[name] = N'UserGuid')
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                    AND ic.[key_ordinal] = 2 AND c.[name] = N'ClientSampleId')
)
    THROW 52902, N'Attendance location sample unique index signature is incompatible.', 1;
""";
}
