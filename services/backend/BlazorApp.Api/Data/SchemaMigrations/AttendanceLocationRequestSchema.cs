namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 「服务端要求员工取一次高精度位置」（迁移 20261119.002-attendance-location-request）：
/// 新表 dbo.AttendanceLocationRequest（请求记录，同时是审计留痕），并把新权限码
/// Attendance.Punch.RequestLocation 幂等写入 HbwebSysPermissions（启动不跑权限种子，不入库角色管理里就授不了），
/// <b>不授予任何角色</b>——授给哪些角色由管理员在生产手动决定。
/// <para>
/// 表只记请求本身：Status 只存 Pending / Responded / Cancelled（「已过期」读取时按 ExpiresAtUtc 推导，不落库）；
/// 员工响应后取到的点写入既有的 AttendanceLocationSample，两边用 SampleGuid 关联。
/// 索引：RequestGuid 唯一（推送 / 接口 / 样本幂等键共用）；(TargetUserGuid, CreatedAt) 服务限频与员工待办查询；
/// (StoreCode, CreatedAt) 服务 Web 按分店看请求历史。不建外键：与考勤其他表一致。
/// </para>
/// 新表走独立版本号迁移：实体不进 SqlSugarContext 的自动建表清单（否则全新库基线会先用 CodeFirst 建出不同的列类型，
/// 迁移的 IF OBJECT_ID IS NULL 被跳过，VerifySql 的精确签名就会失败）；生产须显式执行 --schema=migrate，
/// 且要先于新版主 API 上线（新版 API 启动时的只读 schema 检查在账本缺这条迁移时会拒绝启动）。
/// 错误号区间 53300–53399（本迁移用 53300 缺表 / 53301 列签名 / 53302 主键与索引签名）。
/// </summary>
internal static class AttendanceLocationRequestSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    IF OBJECT_ID(N'dbo.AttendanceLocationRequest', N'U') IS NULL
    BEGIN
        -- 列与 BaseEntity 的审计列保持考勤其他表同一口径（CreatedBy nvarchar(max) NULL、IsDeleted bit NULL）。
        CREATE TABLE [dbo].[AttendanceLocationRequest]
        (
            [Id] int IDENTITY(1,1) NOT NULL,
            [RequestGuid] nvarchar(50) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [TargetUserGuid] nvarchar(50) NOT NULL,
            [RequestedByUserGuid] nvarchar(50) NOT NULL,
            [Status] nvarchar(20) NOT NULL,
            [StatusReason] nvarchar(30) NULL,
            [ExpiresAtUtc] datetime2 NOT NULL,
            [RespondedAtUtc] datetime2 NULL,
            [SampleGuid] nvarchar(50) NULL,
            [CreatedAt] datetime2 NOT NULL,
            [CreatedBy] nvarchar(max) NULL,
            [UpdatedAt] datetime2 NULL,
            [UpdatedBy] nvarchar(max) NULL,
            [IsDeleted] bit NULL,
            CONSTRAINT [PK_AttendanceLocationRequest] PRIMARY KEY CLUSTERED ([Id])
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceLocationRequest')
          AND [name] = N'UX_AttendanceLocationRequest_RequestGuid'
    )
    BEGIN
        -- RequestGuid 全库唯一：员工响应用它定位请求，样本幂等键也是它。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_AttendanceLocationRequest_RequestGuid]
            ON [dbo].[AttendanceLocationRequest] ([RequestGuid]);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceLocationRequest')
          AND [name] = N'IX_AttendanceLocationRequest_Target_CreatedAt'
    )
    BEGIN
        -- 创建时按目标员工查「最近一次请求 / 未过期待响应」（限频），员工待办也走它。
        CREATE NONCLUSTERED INDEX [IX_AttendanceLocationRequest_Target_CreatedAt]
            ON [dbo].[AttendanceLocationRequest] ([TargetUserGuid], [CreatedAt]);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceLocationRequest')
          AND [name] = N'IX_AttendanceLocationRequest_Store_CreatedAt'
    )
    BEGIN
        -- Web 按分店看请求历史。
        CREATE NONCLUSTERED INDEX [IX_AttendanceLocationRequest_Store_CreatedAt]
            ON [dbo].[AttendanceLocationRequest] ([StoreCode], [CreatedAt]);
    END;

    -- 启动流程不跑权限种子，新权限码随本迁移幂等入库，否则角色管理里无法授予；口径要求默认不授予任何角色。
    IF OBJECT_ID(N'[dbo].[HbwebSysPermissions]', N'U') IS NOT NULL
    BEGIN
        INSERT INTO [dbo].[HbwebSysPermissions]
            ([Id], [Code], [Name], [Category], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [IsDeleted])
        SELECT LOWER(CONVERT(nvarchar(36), NEWID())), [wanted].[Code], [wanted].[Name], N'排班考勤', [wanted].[Description],
               SYSUTCDATETIME(), N'SchemaMigration', SYSUTCDATETIME(), N'SchemaMigration', 0
        FROM (VALUES
            (N'Attendance.Punch.RequestLocation', N'要求员工上报当前位置',
             N'Web 页面 /pos-admin/schedule-attendance「班中定位样本」- 要求管辖分店内已上班的员工在手机 App 前台取一次高精度位置；员工会收到通知，请求 15 分钟内有效，留审计记录')
        ) AS [wanted]([Code], [Name], [Description])
        WHERE NOT EXISTS (
            SELECT 1 FROM [dbo].[HbwebSysPermissions] AS [existing] WHERE [existing].[Code] = [wanted].[Code]
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁锁定列、类型、可空性、主键与索引的键列顺序、唯一性；不兼容结构只能通过显式迁移修复，运行时绝不自动改写。
    // 不校验权限码行：管理员事后软删除该行不应让整个 API 启动不了。
    // nvarchar 的 max_length 是字节数（字符数 × 2；nvarchar(max) 为 -1）；datetime2 / int / bit 长度固定，传 0 表示不比较。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.AttendanceLocationRequest', N'U') IS NULL
    THROW 53300, N'Attendance location request table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'Id', N'int', 0, 0),
      (N'RequestGuid', N'nvarchar', 100, 0),
      (N'StoreCode', N'nvarchar', 100, 0),
      (N'TargetUserGuid', N'nvarchar', 100, 0),
      (N'RequestedByUserGuid', N'nvarchar', 100, 0),
      (N'Status', N'nvarchar', 40, 0),
      (N'StatusReason', N'nvarchar', 60, 1),
      (N'ExpiresAtUtc', N'datetime2', 0, 0),
      (N'RespondedAtUtc', N'datetime2', 0, 1),
      (N'SampleGuid', N'nvarchar', 100, 1),
      (N'CreatedAt', N'datetime2', 0, 0),
      (N'IsDeleted', N'bit', 0, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(N'dbo.AttendanceLocationRequest') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    -- 全新库若由 CodeFirst 建出过同名表，DateTime 会落成 datetime；与 datetime2 同为 8 字节语义，两者都接受。
    WHERE c.[column_id] IS NULL
       OR (ty.[name] <> expected.[type_name]
           AND NOT (expected.[type_name] = N'datetime2' AND ty.[name] = N'datetime'))
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 53301, N'Attendance location request column signature is incompatible.', 1;

-- 主键必须正好是单列 Id；三条索引的键列、顺序与唯一性必须与迁移一致。
-- RequestGuid 唯一性是业务底线（响应定位与样本幂等都靠它），不唯一就会让「同一请求被响应两次」落进两条样本。
IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'PK_AttendanceLocationRequest', 1, N'Id', 1),
      (N'UX_AttendanceLocationRequest_RequestGuid', 1, N'RequestGuid', 1),
      (N'IX_AttendanceLocationRequest_Target_CreatedAt', 1, N'TargetUserGuid', 0),
      (N'IX_AttendanceLocationRequest_Target_CreatedAt', 2, N'CreatedAt', 0),
      (N'IX_AttendanceLocationRequest_Store_CreatedAt', 1, N'StoreCode', 0),
      (N'IX_AttendanceLocationRequest_Store_CreatedAt', 2, N'CreatedAt', 0)
    ) expected([index_name], [key_ordinal], [column_name], [is_unique])
    LEFT JOIN sys.indexes i
        ON i.[object_id] = OBJECT_ID(N'dbo.AttendanceLocationRequest') AND i.[name] = expected.[index_name]
    LEFT JOIN sys.index_columns ic
        ON ic.[object_id] = i.[object_id]
       AND ic.[index_id] = i.[index_id]
       AND ic.[key_ordinal] = expected.[key_ordinal]
       AND ic.[is_included_column] = 0
    LEFT JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
    WHERE i.[index_id] IS NULL OR c.[name] IS NULL OR c.[name] <> expected.[column_name]
       OR i.[is_unique] <> expected.[is_unique]
)
 OR EXISTS (
    -- 键列数不得超过预期：主键与唯一索引多出一列，唯一性语义就变了。
    SELECT 1 FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
    WHERE i.[object_id] = OBJECT_ID(N'dbo.AttendanceLocationRequest')
      AND ic.[is_included_column] = 0
      AND (
            (i.[name] = N'PK_AttendanceLocationRequest' AND ic.[key_ordinal] > 1)
         OR (i.[name] = N'UX_AttendanceLocationRequest_RequestGuid' AND ic.[key_ordinal] > 1)
         OR (i.[name] = N'IX_AttendanceLocationRequest_Target_CreatedAt' AND ic.[key_ordinal] > 2)
         OR (i.[name] = N'IX_AttendanceLocationRequest_Store_CreatedAt' AND ic.[key_ordinal] > 2)
      )
)
    THROW 53302, N'Attendance location request key or index signature is incompatible.', 1;
""";
}
