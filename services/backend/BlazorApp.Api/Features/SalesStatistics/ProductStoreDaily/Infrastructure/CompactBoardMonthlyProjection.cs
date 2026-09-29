namespace BlazorApp.Api.Services;

/// <summary>
/// 独立销售看板月投影（CompactBoardMonthlyCell）的 SQL 构造器。
/// 生产实测（2026-09-24）：API↔数据库走公网约 1–1.5 MB/秒，把区间内几十万行「分店×商品」原始聚合拉回 API
/// 再在内存里算，1 年全冷要 40–60 秒；改为按月预聚合、查询在数据库里算完只回几百行。
/// 月身份与销售明细月投影同一口径（该月日状态行的日期、来源版本、聚合时间），另加国内编码族签名：
/// 新增国内供应商会让直写行进入编码族，旧月份须重建。
/// </summary>
internal static class CompactBoardMonthlyProjection
{
    internal const int SchemaVersion = 1;

    internal const string TablesExistSql = """
SELECT CASE WHEN OBJECT_ID(N'dbo.CompactBoardMonthlyCell', N'U') IS NOT NULL
 AND OBJECT_ID(N'dbo.CompactBoardMonthlyState', N'U') IS NOT NULL THEN 1 ELSE 0 END;
""";

    /// <summary>
    /// 国内编码族（200 与全部国内供应商编码，含停用和软删除，去空格）写入 #cbFamily，并算出签名 @cbFamilySignature。
    /// 临时表显式使用库默认排序规则，避免与 tempdb 排序规则冲突。
    /// </summary>
    internal const string CodeFamilySql = """
DROP TABLE IF EXISTS #cbFamily;
-- 不建主键：SNAPSHOT 事务内建索引会报 3964；DISTINCT 已去重。
CREATE TABLE #cbFamily ([Code] nvarchar(50) COLLATE DATABASE_DEFAULT NOT NULL);
INSERT INTO #cbFamily ([Code]) VALUES (N'200');
INSERT INTO #cbFamily ([Code])
SELECT DISTINCT LTRIM(RTRIM(c.[SupplierCode]))
FROM [dbo].[ChinaSupplier] c
WHERE c.[SupplierCode] IS NOT NULL AND LTRIM(RTRIM(c.[SupplierCode])) NOT IN (N'', N'200');
DECLARE @cbFamilySignature varchar(64) = CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varbinary(max),
    ISNULL((SELECT f.[Code] FROM #cbFamily f ORDER BY f.[Code] COLLATE Latin1_General_100_BIN2 FOR JSON PATH), N'[]'))), 2);
""";

    /// <summary>
    /// 列出需要重建的月份（参数 @cbMaxMonths）：无状态、schema 版本不同、日身份或编码族签名变化。
    /// 范围从最早的商品日统计状态所在月到当前月；最近的月份排前面，当月每小时重算后最先追上。
    /// </summary>
    internal static string BuildStaleMonthsSql() => $$"""
SET NOCOUNT ON;
{{CodeFamilySql}}
DECLARE @cbFirstMonth date = (SELECT DATEFROMPARTS(YEAR(MIN([Date])), MONTH(MIN([Date])), 1)
                              FROM [dbo].[SalesStatisticRefreshState] WHERE [StatisticType] = N'ProductStoreDaily');
DECLARE @cbLastMonth date = DATEFROMPARTS(YEAR(SYSDATETIME()), MONTH(SYSDATETIME()), 1);
IF @cbFirstMonth IS NOT NULL
BEGIN
    ;WITH Digits(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n)),
    MonthOffsets(n) AS (SELECT a.n + b.n * 10 FROM Digits a CROSS JOIN Digits b),
    Months AS (SELECT DATEADD(month, n, @cbFirstMonth) [Month] FROM MonthOffsets WHERE DATEADD(month, n, @cbFirstMonth) <= @cbLastMonth)
    SELECT TOP (@cbMaxMonths) m.[Month]
    FROM Months m
    LEFT JOIN [dbo].[CompactBoardMonthlyState] st ON st.[Month] = m.[Month]
    CROSS APPLY (SELECT {{SalesDetailQueryMonthlyProjection.BuildMonthIdentitySql("m.[Month]")}} [Identity]) ident
    WHERE st.[Month] IS NULL
       OR st.[ProjectionSchemaVersion] <> {{SchemaVersion}}
       OR st.[DayIdentity] <> ident.[Identity]
       OR st.[CodeFamilySignature] <> @cbFamilySignature
    ORDER BY m.[Month] DESC;
END;
DROP TABLE #cbFamily;
""";

    /// <summary>
    /// 在调用方开启的 SNAPSHOT 事务里重建一个月（参数 @cbMonth）：身份、编码族与事实取自同一快照。
    /// 键去空格后分组，与看板按分店去空格、按商品合并的口径一致。
    /// </summary>
    internal static string BuildRefreshMonthSql() => $$"""
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @cbMonthStart date = DATEFROMPARTS(YEAR(@cbMonth), MONTH(@cbMonth), 1);
DECLARE @cbMonthEnd date = DATEADD(month, 1, @cbMonthStart);
DECLARE @cbStartedAt datetime2 = SYSUTCDATETIME();
{{CodeFamilySql}}
DECLARE @cbDayIdentity varchar(64) = {{SalesDetailQueryMonthlyProjection.BuildMonthIdentitySql("@cbMonthStart")}};

DELETE FROM [dbo].[CompactBoardMonthlyCell] WHERE [Month] = @cbMonthStart;
INSERT INTO [dbo].[CompactBoardMonthlyCell]
    ([Month], [BranchCode], [ProductCode], [RawSupplierCode], [Quantity], [Amount], [LastDate])
SELECT @cbMonthStart, f.[BranchCode], f.[ProductCode], f.[RawSupplierCode],
       SUM(f.[Quantity]), SUM(f.[Amount]), MAX(f.[LastDate])
FROM (
    SELECT LTRIM(RTRIM(COALESCE(s.[BranchCode], N''))) [BranchCode],
           LTRIM(RTRIM(COALESCE(s.[ProductCode], N''))) [ProductCode],
           LTRIM(RTRIM(COALESCE(s.[SupplierCode], N''))) [RawSupplierCode],
           CONVERT(bigint, s.[TotalQuantity]) [Quantity], CONVERT(decimal(38,4), s.[TotalAmount]) [Amount],
           CONVERT(date, s.[Date]) [LastDate]
    FROM [dbo].[ProductStoreDailySalesStatistic] s
    WHERE s.[Date] >= CONVERT(datetime, @cbMonthStart) AND s.[Date] < CONVERT(datetime, @cbMonthEnd)
) f
WHERE f.[RawSupplierCode] IN (SELECT [Code] FROM #cbFamily) AND f.[BranchCode] <> N'' AND f.[ProductCode] <> N''
GROUP BY f.[BranchCode], f.[ProductCode], f.[RawSupplierCode]
OPTION (RECOMPILE);
DECLARE @cbCellCount int = @@ROWCOUNT;

DELETE FROM [dbo].[CompactBoardMonthlyState] WHERE [Month] = @cbMonthStart;
INSERT INTO [dbo].[CompactBoardMonthlyState]
    ([Month], [ProjectionSchemaVersion], [DayIdentity], [CodeFamilySignature], [CellCount], [RefreshedAtUtc], [RefreshDurationMs])
VALUES (@cbMonthStart, {{SchemaVersion}}, @cbDayIdentity, @cbFamilySignature, @cbCellCount,
        SYSUTCDATETIME(), DATEDIFF(millisecond, @cbStartedAt, SYSUTCDATETIME()));
DROP TABLE #cbFamily;
""";
}
