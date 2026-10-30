namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 考勤设置唯一约束（迁移 20261030.004-attendance-settings-single-active）：
/// - 给 dbo.AttendanceSettings 加过滤唯一索引 UX_AttendanceSettings_SingleActive：
///   键列 IsDeleted，过滤条件 IsDeleted = 0，保证“未删除的设置行”最多一条。
///   过滤条件与代码读取一致（GetOrCreateSettingsModelAsync 用 !IsDeleted，即 IsDeleted = 0），
///   IsDeleted 为 NULL 的行代码本来就读不到，不受约束。
/// - 目的：多个请求/实例在“全库第一次”同时发现没有设置行并各插一行时，数据库只放行一条，
///   输掉竞争的插入由代码捕获唯一冲突后重读；旧版代码不认识该索引，但也不会因此出错（只是插入第二行会失败，
///   而旧版本在已有设置行的库里本来就不会插入）。
/// - 建索引前先查重：已有多于一条未删除设置行时直接失败（54421），绝不自动删除或合并——保留哪一条是业务决定，
///   须人工核对后处理再重跑迁移。
/// 只加索引，不改表结构与数据；回退 = DROP INDEX + 移除迁移账本行。
/// 错误号自成一段 54420–54423：Apply 内 54420 表缺失、54421 存在重复的未删除设置行；
/// Verify 内 54422 表缺失、54423 索引缺失或定义不符。
/// 生产须显式执行 --schema=migrate；不依赖也不阻塞主 API 版本（代码对缺索引的库照常工作）。
/// </summary>
internal static class AttendanceSettingsSingleActiveSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.AttendanceSettings', N'U') IS NULL
    THROW 54420, N'Attendance settings table is missing; apply the HBweb baseline first.', 1;

BEGIN TRANSACTION;
BEGIN TRY
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceSettings')
          AND [name] = N'UX_AttendanceSettings_SingleActive'
    )
    BEGIN
        -- 先查重：重复行会让唯一索引建失败；这里提前给出明确错误，且不动任何数据。
        IF (SELECT COUNT(*) FROM [dbo].[AttendanceSettings] WHERE [IsDeleted] = 0) > 1
            THROW 54421, N'More than one non-deleted AttendanceSettings row exists; resolve duplicates manually before applying.', 1;

        -- 数据库兜底：未删除的考勤设置行最多一条，并发的“首次创建”只会成功一次。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_AttendanceSettings_SingleActive]
            ON [dbo].[AttendanceSettings] ([IsDeleted])
            WHERE [IsDeleted] = 0;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：索引必须唯一、带过滤条件 IsDeleted = 0、键列恰好是 IsDeleted（升序）；
    // 同名但定义不符的索引只能由显式迁移修复，运行时绝不自动改写。
    // filter_definition 形如 ([IsDeleted]=(0))，去掉括号和空格后比较。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.AttendanceSettings', N'U') IS NULL
    THROW 54422, N'Attendance settings table is missing.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[object_id] = OBJECT_ID(N'dbo.AttendanceSettings')
      AND i.[name] = N'UX_AttendanceSettings_SingleActive'
      AND i.[is_unique] = 1
      AND i.[has_filter] = 1
      AND REPLACE(REPLACE(REPLACE(i.[filter_definition], N'(', N''), N')', N''), N' ', N'') = N'[IsDeleted]=0'
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[is_included_column] = 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                  WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] = 1 AND c.[name] = N'IsDeleted' AND ic.[is_descending_key] = 0)
)
    THROW 54423, N'Attendance settings single-active index is missing or incompatible.', 1;
""";
}
