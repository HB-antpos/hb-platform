namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 客显广告加「版式」：dbo.Advertisement 加三列
/// Orientation nvarchar(16) NOT NULL（具名默认约束 DF_Advertisement_Orientation = N'Any'，
/// 具名 CHECK 约束 CK_Advertisement_Orientation 限定 Landscape / Portrait / Any），
/// MediaWidth int NULL、MediaHeight int NULL（素材像素宽高，Web 上传时读取）。
/// 旧广告按默认值回填为 Any，客显播放行为与加列前完全一致。
/// 广告表随基线 20260827.001（SqlSugarContext 的 CodeFirst tableTypes）建出，新列只能走独立版本号迁移；
/// 生产须显式执行 --schema=migrate，且要先于新版 POS API / HBweb 上线
/// （POS API 启动会对本表执行 CodeFirst.InitTables，缺列时会自行补列，定义可能与本迁移不一致）。
/// 错误号区间 52402–52403。
/// </summary>
internal static class AdvertisementOrientationSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.Advertisement', N'U') IS NULL
    THROW 52402, N'Advertisement table is missing; apply the HBweb baseline first.', 1;

BEGIN TRANSACTION;
BEGIN TRY
    -- NOT NULL 列带具名默认约束一次加出：SQL Server 用默认值回填历史行（旧广告 = Any）。
    -- COL_LENGTH 判断保证重复执行是空操作。
    IF COL_LENGTH(N'dbo.Advertisement', N'Orientation') IS NULL
        ALTER TABLE [dbo].[Advertisement]
            ADD [Orientation] nvarchar(16) NOT NULL
                CONSTRAINT [DF_Advertisement_Orientation] DEFAULT (N'Any');
    IF COL_LENGTH(N'dbo.Advertisement', N'MediaWidth') IS NULL
        ALTER TABLE [dbo].[Advertisement] ADD [MediaWidth] int NULL;
    IF COL_LENGTH(N'dbo.Advertisement', N'MediaHeight') IS NULL
        ALTER TABLE [dbo].[Advertisement] ADD [MediaHeight] int NULL;

    -- 全新库的基线由 CodeFirst 建表，会带出系统命名的默认约束（DF__Advertis__…）；
    -- 这里只在列上没有具名默认约束时，把已有的匿名默认约束换成具名约束，保证门禁签名一致。
    IF NOT EXISTS (
        SELECT 1 FROM sys.default_constraints
        WHERE [parent_object_id] = OBJECT_ID(N'dbo.Advertisement')
          AND [name] = N'DF_Advertisement_Orientation'
    )
    BEGIN
        DECLARE @existingDefault sysname = (
            SELECT TOP (1) dc.[name]
            FROM sys.default_constraints dc
            INNER JOIN sys.columns c
              ON c.[object_id] = dc.[parent_object_id] AND c.[column_id] = dc.[parent_column_id]
            WHERE dc.[parent_object_id] = OBJECT_ID(N'dbo.Advertisement')
              AND c.[name] = N'Orientation'
        );
        IF @existingDefault IS NOT NULL
        BEGIN
            -- EXEC() 里不能直接调用函数拼接，先拼到变量再执行。
            DECLARE @dropDefaultSql nvarchar(400) =
                N'ALTER TABLE [dbo].[Advertisement] DROP CONSTRAINT ' + QUOTENAME(@existingDefault) + N';';
            EXEC sys.sp_executesql @dropDefaultSql;
        END;
        EXEC(N'ALTER TABLE [dbo].[Advertisement]
            ADD CONSTRAINT [DF_Advertisement_Orientation] DEFAULT (N''Any'') FOR [Orientation];');
    END;

    -- CHECK 约束引用本批次刚加的列，用动态 SQL 推迟到列存在后再编译，避免整批编译时报列名无效。
    -- 不带 NOCHECK：加约束时会校验存量行（存量只可能是默认值 Any）。
    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE [parent_object_id] = OBJECT_ID(N'dbo.Advertisement')
          AND [name] = N'CK_Advertisement_Orientation'
    )
        EXEC(N'ALTER TABLE [dbo].[Advertisement] WITH CHECK
            ADD CONSTRAINT [CK_Advertisement_Orientation]
            CHECK ([Orientation] IN (N''Landscape'', N''Portrait'', N''Any''));');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：Orientation 必须是 nvarchar(16) NOT NULL（max_length 按字节计为 32），
    // 带具名默认约束 DF_Advertisement_Orientation = N'Any' 与受信任的具名 CHECK 约束 CK_Advertisement_Orientation；
    // MediaWidth / MediaHeight 必须是 int NULL。签名不符只能通过显式迁移修复，运行时绝不自动改写。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.Advertisement', N'U') IS NULL
    THROW 52402, N'Advertisement table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'Orientation', N'nvarchar', 32, 0),
      (N'MediaWidth', N'int', 4, 1),
      (N'MediaHeight', N'int', 4, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(N'dbo.Advertisement') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
 OR NOT EXISTS (
    SELECT 1
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c
      ON c.[object_id] = dc.[parent_object_id] AND c.[column_id] = dc.[parent_column_id]
    WHERE dc.[parent_object_id] = OBJECT_ID(N'dbo.Advertisement')
      AND dc.[name] = N'DF_Advertisement_Orientation'
      AND c.[name] = N'Orientation'
      AND dc.[definition] = N'(N''Any'')'
)
 OR NOT EXISTS (
    SELECT 1
    FROM sys.check_constraints ck
    WHERE ck.[parent_object_id] = OBJECT_ID(N'dbo.Advertisement')
      AND ck.[name] = N'CK_Advertisement_Orientation'
      AND ck.[is_disabled] = 0
      AND ck.[is_not_trusted] = 0
      -- SQL Server 把 IN 列表规范化成倒序的 OR 链存入 definition，这里按规范化后的文本精确比对。
      AND ck.[definition] = N'([Orientation]=N''Any'' OR [Orientation]=N''Portrait'' OR [Orientation]=N''Landscape'')'
)
    THROW 52403, N'Advertisement orientation column or constraint signature is incompatible.', 1;
""";
}
