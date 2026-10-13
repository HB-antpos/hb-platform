namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 门店分时客流报表表 dbo.FaceTrafficHourly（迁移 20261016.001-face-traffic-hourly）：
/// 每个「门店 + 本地日期 + 本地小时」一行，由主 API 后台任务从 FaceTrafficBucket（UTC 整点计数桶）换算投影而来，
/// 与营业额（HourlySalesStatistic）同为门店本地墙钟口径，报表可直接按 (门店, 日期, 小时) JOIN。
/// 只建这一张新表，不修改 FaceTrafficBucket 等任何现有表；不含任何生物信息，永久保留。
/// 新表走独立版本号迁移，生产须显式执行 --schema=migrate；实体不进 SqlSugarContext 的自动建表清单。错误号区间 52820–52822。
/// 不建外键：门店可以停用 / 删除，历史分时行仍要保留。
/// </summary>
internal static class FaceTrafficHourlySchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.FaceTrafficHourly', N'U') IS NULL
    BEGIN
        -- StatDate / StatHour 是门店本地墙钟日期与小时（与营业额 HourlySalesStatistic 的 Date / Hour 同口径），不是 UTC。
        -- VisitCount = max(0, Capture - Staff - LowQuality - ApiFailure)，由投影任务写入（派生列，含当日重复）；
        -- UniqueVisitorCount 是该小时首次出现的当日新访客（估算），同一天各小时相加得当日唯一访客数。
        CREATE TABLE [dbo].[FaceTrafficHourly]
        (
            [StoreCode] nvarchar(32) NOT NULL,
            [StatDate] date NOT NULL,
            [StatHour] tinyint NOT NULL,
            [CaptureCount] int NOT NULL CONSTRAINT [DF_FaceTrafficHourly_CaptureCount] DEFAULT (0),
            [StaffMatchCount] int NOT NULL CONSTRAINT [DF_FaceTrafficHourly_StaffMatchCount] DEFAULT (0),
            [UniqueVisitorCount] int NOT NULL CONSTRAINT [DF_FaceTrafficHourly_UniqueVisitorCount] DEFAULT (0),
            [RepeatCount] int NOT NULL CONSTRAINT [DF_FaceTrafficHourly_RepeatCount] DEFAULT (0),
            [LowQualityCount] int NOT NULL CONSTRAINT [DF_FaceTrafficHourly_LowQualityCount] DEFAULT (0),
            [ApiFailureCount] int NOT NULL CONSTRAINT [DF_FaceTrafficHourly_ApiFailureCount] DEFAULT (0),
            [VisitCount] int NOT NULL CONSTRAINT [DF_FaceTrafficHourly_VisitCount] DEFAULT (0),
            [TimeZoneId] nvarchar(64) NOT NULL,
            [ComputedAtUtc] datetime2 NOT NULL,
            CONSTRAINT [PK_FaceTrafficHourly] PRIMARY KEY CLUSTERED ([StoreCode], [StatDate], [StatHour]),
            CONSTRAINT [CK_FaceTrafficHourly_StatHour] CHECK ([StatHour] BETWEEN 0 AND 23)
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.FaceTrafficHourly')
          AND [name] = N'IX_FaceTrafficHourly_StatDate_Store'
    )
    BEGIN
        -- 报表按日期区间扫全部门店走这条索引（主键是门店在前，按日期扫全部门店用不上）。
        CREATE NONCLUSTERED INDEX [IX_FaceTrafficHourly_StatDate_Store]
            ON [dbo].[FaceTrafficHourly] ([StatDate], [StoreCode]);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、可空性、主键列顺序与日期索引；不兼容结构只能通过显式迁移修复。
    // nvarchar 的 max_length 是字节数（字符数 × 2）；tinyint / date / int / datetime2 长度固定，传 0 表示不比较。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.FaceTrafficHourly', N'U') IS NULL
    THROW 52820, N'Face traffic hourly table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'FaceTrafficHourly', N'StoreCode', N'nvarchar', 64, 0),
      (N'FaceTrafficHourly', N'StatDate', N'date', 0, 0),
      (N'FaceTrafficHourly', N'StatHour', N'tinyint', 0, 0),
      (N'FaceTrafficHourly', N'CaptureCount', N'int', 0, 0),
      (N'FaceTrafficHourly', N'StaffMatchCount', N'int', 0, 0),
      (N'FaceTrafficHourly', N'UniqueVisitorCount', N'int', 0, 0),
      (N'FaceTrafficHourly', N'RepeatCount', N'int', 0, 0),
      (N'FaceTrafficHourly', N'LowQualityCount', N'int', 0, 0),
      (N'FaceTrafficHourly', N'ApiFailureCount', N'int', 0, 0),
      (N'FaceTrafficHourly', N'VisitCount', N'int', 0, 0),
      (N'FaceTrafficHourly', N'TimeZoneId', N'nvarchar', 128, 0),
      (N'FaceTrafficHourly', N'ComputedAtUtc', N'datetime2', 0, 0)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 52821, N'Face traffic hourly column signature is incompatible.', 1;

-- 主键必须正好是 (StoreCode, StatDate, StatHour)，列顺序一致：重算按这三列整行覆盖，报表 JOIN 也依赖它；
-- 日期索引必须是 (StatDate, StoreCode) 两个键列的非唯一索引。多一列、少一列或换序都算不兼容。
IF EXISTS (
    SELECT 1 FROM (VALUES
      (1, N'StoreCode'),
      (2, N'StatDate'),
      (3, N'StatHour')
    ) expected([key_ordinal], [column_name])
    LEFT JOIN sys.key_constraints kc
        ON kc.[parent_object_id] = OBJECT_ID(N'dbo.FaceTrafficHourly') AND kc.[type] = N'PK'
    LEFT JOIN sys.index_columns ic
        ON ic.[object_id] = kc.[parent_object_id]
       AND ic.[index_id] = kc.[unique_index_id]
       AND ic.[key_ordinal] = expected.[key_ordinal]
    LEFT JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
    WHERE c.[name] IS NULL OR c.[name] <> expected.[column_name]
)
 OR EXISTS (
    SELECT 1 FROM sys.key_constraints kc
    JOIN sys.index_columns ic ON ic.[object_id] = kc.[parent_object_id] AND ic.[index_id] = kc.[unique_index_id]
    WHERE kc.[type] = N'PK'
      AND kc.[parent_object_id] = OBJECT_ID(N'dbo.FaceTrafficHourly')
      AND ic.[key_ordinal] > 3
)
 OR EXISTS (
    SELECT 1 FROM (VALUES
      (1, N'StatDate'),
      (2, N'StoreCode')
    ) expected([key_ordinal], [column_name])
    LEFT JOIN sys.indexes i
        ON i.[object_id] = OBJECT_ID(N'dbo.FaceTrafficHourly') AND i.[name] = N'IX_FaceTrafficHourly_StatDate_Store'
    LEFT JOIN sys.index_columns ic
        ON ic.[object_id] = i.[object_id]
       AND ic.[index_id] = i.[index_id]
       AND ic.[key_ordinal] = expected.[key_ordinal]
       AND ic.[is_included_column] = 0
    LEFT JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
    WHERE c.[name] IS NULL OR c.[name] <> expected.[column_name] OR i.[is_unique] = 1
)
 OR EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
    WHERE i.[object_id] = OBJECT_ID(N'dbo.FaceTrafficHourly')
      AND i.[name] = N'IX_FaceTrafficHourly_StatDate_Store'
      AND ic.[key_ordinal] > 2
)
    THROW 52822, N'Face traffic hourly key or index signature is incompatible.', 1;
""";
}
