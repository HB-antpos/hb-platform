using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React;

public partial class SalesDashboardReactService
{
    // 投影表缺失（51014）后 5 分钟内不再尝试预聚合路径，避免每次请求都多一次往返。
    private static DateTime _monthlyProjectionMissingUntilUtc = DateTime.MinValue;

    /// <summary>
    /// 没有关键词的请求走预聚合；授权分店与选中分店由分店粒度事实过滤，商品栏按分店范围读日事实（见 BuildSalesDetailMonthlyScopedProductFactsSql）。
    /// 单独请求分店栏的商品抽屉走原查询的商品索引已足够快。
    /// </summary>
    internal static bool UsesMonthlyProjection(
        string? search, IReadOnlyCollection<string>? branches, string? selectedBranch, IReadOnlySet<SalesDetailSection> wanted)
        => string.IsNullOrWhiteSpace(search)
           && !(wanted.Count == 1 && wanted.Contains(SalesDetailSection.Branches));

    private static bool MonthlyProjectionRecentlyMissing() => DateTime.UtcNow < _monthlyProjectionMissingUntilUtc;

    private static void RememberMonthlyProjectionMissing() => _monthlyProjectionMissingUntilUtc = DateTime.UtcNow.AddMinutes(5);

    /// <summary>全部商品事实约 1 秒（生产 7 个月双期），约等于逐日多读 40 万行事实；范围内比范围外多出这个差额才改用减法。</summary>
    internal const long SalesDetailScopeComplementThresholdRows = 400_000;

    private const string SdmPeriods = """
Periods AS
(
 SELECT 0 [Period], @sdrCurrentStart [StartDate], @sdrCurrentEnd [EndDate]
 UNION ALL
 SELECT 1 [Period], @sdrCompareStart [StartDate], @sdrCompareEnd [EndDate] WHERE @sdrHasCompare=1
)
""";
    private const string SdmMeasures = "[Revenue], [Quantity], [OrderCount], [GrossProfit], [StatisticRowCount], [CostedRowCount], [GrossProfitRowCount]";
    private const string SdmSumMeasures = "SUM([Revenue]) [Revenue], SUM([Quantity]) [Quantity], SUM([OrderCount]) [OrderCount], SUM([GrossProfit]) [GrossProfit], "
        + "SUM([StatisticRowCount]) [StatisticRowCount], SUM([CostedRowCount]) [CostedRowCount], SUM([GrossProfitRowCount]) [GrossProfitRowCount]";
    private const string SdmFactMeasures = "SUM(s.[TotalAmount]) [Revenue], SUM(CONVERT(bigint, s.[TotalQuantity])) [Quantity], SUM(CONVERT(bigint, s.[OrderCount])) [OrderCount], "
        + "SUM(s.[GrossProfit]) [GrossProfit], COUNT_BIG(*) [StatisticRowCount], COUNT_BIG(s.[TotalCost]) [CostedRowCount], COUNT_BIG(s.[GrossProfit]) [GrossProfitRowCount]";
    private const string SdmTrimmedKeys = "LTRIM(RTRIM(COALESCE([RawSupplierCode],''))), LTRIM(RTRIM(COALESCE([BranchCode],''))), LTRIM(RTRIM(COALESCE([ProductCode],'')))";
    private static string SdmPrefixed(string alias) => alias + "." + SdmMeasures.Replace(", [", $", {alias}.[");
    private static string SdmChina(string alias) => $"CASE WHEN {alias}.[RawSupplierCode]='200' THEN NULLIF(LTRIM(RTRIM(m.[ChinaSupplierCode])), '') WHEN cs.[SupplierCode] IS NOT NULL THEN {alias}.[RawSupplierCode] END";
    private static string SdmAus(string alias) => $"CASE WHEN {alias}.[RawSupplierCode]='200' OR cs.[SupplierCode] IS NOT NULL THEN '200' ELSE NULLIF({alias}.[RawSupplierCode], '') END";
    private static string SdmMappingJoins(string database, string alias) => $"""
LEFT JOIN {database}.[dbo].[posm_product_supplier_mapping] m ON m.[ProductCode] = {alias}.[ProductCode] AND m.[LocalSupplierCode] = '200' AND m.[IsDeleted] = 0
LEFT JOIN (SELECT [SupplierCode] FROM [ChinaSupplier] WHERE [SupplierCode] IS NOT NULL AND [SupplierCode]<>'' GROUP BY [SupplierCode]) cs ON cs.[SupplierCode]={alias}.[RawSupplierCode]
""";

    /// <summary>
    /// 月表 → 日表 → 日事实三级回退的前四步：#sdmMonths、#sdmDays、#sdmBaseFacts，以及未选商品时的分店粒度事实 #sdmBranchFacts。
    /// 调用方须先声明 @sdmMappingVersion。branchEdgesOnly 时边缘日事实只为分店粒度读取（关键词查询的供应商/分店/分母栏）。
    /// </summary>
    private static (string Months, string Days, string BaseFacts, string BranchFacts) BuildSalesDetailMonthlyBranchFactsSql(
        string posmDatabase, bool branchEdgesOnly)
    {
        var database = QuoteIdentifier(posmDatabase);
        var edgeFilter = branchEdgesOnly ? "d.[BranchSource]=2" : "d.[ProductSource]=2 OR d.[BranchSource]=2";
        // 1. 完整落在各期内的月份：商品粒度只看日身份，分店粒度还要求映射签名未变。
        var months = $"""
WITH {SdmPeriods}, Digits(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n)),
MonthOffsets(n) AS (SELECT a.n + b.n * 10 FROM Digits a CROSS JOIN Digits b),
Months AS
(
 SELECT p.[Period], p.[StartDate], p.[EndDate],
        DATEADD(month, o.n, DATEFROMPARTS(YEAR(p.[StartDate]), MONTH(p.[StartDate]), 1)) [Month]
 FROM Periods p CROSS JOIN MonthOffsets o WHERE o.n <= 26
)
SELECT m.[Period], m.[Month], DATEADD(month, 1, m.[Month]) [NextMonth],
       CAST(CASE WHEN st.[Month] IS NOT NULL AND st.[ProjectionSchemaVersion]={SalesDetailQueryMonthlyProjection.SchemaVersion}
                  AND st.[DayIdentity]=ident.[Identity]
                  AND NOT EXISTS (SELECT 1 FROM [dbo].[SalesStatisticRefreshState] failed
                                  WHERE failed.[StatisticType]=N'ProductStoreDaily'
                                    AND failed.[Status]=N'Failed'
                                    AND failed.[Date] >= CONVERT(datetime, m.[Month])
                                    AND failed.[Date] < CONVERT(datetime, DATEADD(month, 1, m.[Month]))) THEN 1 ELSE 0 END AS bit) [ProductValid],
       CAST(CASE WHEN st.[Month] IS NOT NULL AND st.[ProjectionSchemaVersion]={SalesDetailQueryMonthlyProjection.SchemaVersion}
                  AND st.[DayIdentity]=ident.[Identity] AND st.[MappingVersion]=@sdmMappingVersion
                  AND NOT EXISTS (SELECT 1 FROM [dbo].[SalesStatisticRefreshState] failed
                                  WHERE failed.[StatisticType]=N'ProductStoreDaily'
                                    AND failed.[Status]=N'Failed'
                                    AND failed.[Date] >= CONVERT(datetime, m.[Month])
                                    AND failed.[Date] < CONVERT(datetime, DATEADD(month, 1, m.[Month]))) THEN 1 ELSE 0 END AS bit) [BranchValid]
INTO #sdmMonths
FROM Months m
LEFT JOIN [dbo].[SalesDetailQueryMonthlyState] st ON st.[Month]=m.[Month]
CROSS APPLY (SELECT {SalesDetailQueryMonthlyProjection.BuildMonthIdentitySql("m.[Month]")} [Identity]) ident
WHERE m.[Month] >= m.[StartDate] AND DATEADD(month, 1, m.[Month]) <= m.[EndDate];
""";
        // 2. 未被有效月份覆盖的日期：来源 0=月表已含、1=日表、2=日事实（日表无状态、身份不同或分店粒度映射签名不同）。
        var days = $"""
WITH {SdmPeriods}, Digits(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n)),
DayOffsets(n) AS (SELECT a.n + b.n * 10 + c.n * 100 FROM Digits a CROSS JOIN Digits b CROSS JOIN Digits c),
Days AS
(
 SELECT p.[Period], CONVERT(date, DATEADD(day, o.n, p.[StartDate])) [Day]
 FROM Periods p CROSS JOIN DayOffsets o WHERE o.n < DATEDIFF(day, p.[StartDate], p.[EndDate])
)
SELECT d.[Period], d.[Day], CONVERT(datetime, d.[Day]) [DayStart], CONVERT(datetime, DATEADD(day, 1, d.[Day])) [DayEnd],
       CAST(CASE WHEN pm.[Month] IS NOT NULL THEN 0 WHEN dv.[DayValid]=1 THEN 1 ELSE 2 END AS tinyint) [ProductSource],
       CAST(CASE WHEN bm.[Month] IS NOT NULL THEN 0 WHEN dv.[DayValid]=1 AND dv.[MappingValid]=1 THEN 1 ELSE 2 END AS tinyint) [BranchSource]
INTO #sdmDays
FROM Days d
LEFT JOIN #sdmMonths pm ON pm.[Period]=d.[Period] AND pm.[ProductValid]=1 AND d.[Day]>=pm.[Month] AND d.[Day]<pm.[NextMonth]
LEFT JOIN #sdmMonths bm ON bm.[Period]=d.[Period] AND bm.[BranchValid]=1 AND d.[Day]>=bm.[Month] AND d.[Day]<bm.[NextMonth]
CROSS APPLY (SELECT
 CASE WHEN EXISTS (SELECT 1 FROM [dbo].[SalesStatisticRefreshState] r
                   INNER JOIN [dbo].[SalesDetailQueryDailyState] ds ON ds.[Date]=d.[Day]
                   WHERE r.[StatisticType]=N'ProductStoreDaily' AND r.[Date]=CONVERT(datetime, d.[Day])
                     AND {SalesDetailQueryMonthlyProjection.BuildDayIdentityMatchesSql("ds", "r")}) THEN 1 ELSE 0 END [DayValid],
 CASE WHEN EXISTS (SELECT 1 FROM [dbo].[SalesDetailQueryDailyState] ds WHERE ds.[Date]=d.[Day] AND ds.[MappingVersion]=@sdmMappingVersion) THEN 1 ELSE 0 END [MappingValid]) dv
WHERE (pm.[Month] IS NULL OR bm.[Month] IS NULL)
  AND NOT EXISTS (SELECT 1 FROM [dbo].[SalesStatisticRefreshState] failed
                  WHERE failed.[StatisticType]=N'ProductStoreDaily'
                    AND failed.[Status]=N'Failed'
                    AND failed.[Date]=CONVERT(datetime, d.[Day]));
""";
        // 3. 日表没覆盖的日期读日事实：临时表里的日期逐日走聚集主键 seek（每天约 1.2 万行），正常只有当日或回填中的日期。
        var baseFacts = $"""
WITH RawFacts AS
(
 SELECT d.[Period], d.[ProductSource], d.[BranchSource], s.[SupplierCode] [RawSupplierCode], s.[BranchCode], s.[ProductCode], {SdmFactMeasures}
 FROM #sdmDays d
 INNER JOIN [ProductStoreDailySalesStatistic] s ON s.[Date]>=d.[DayStart] AND s.[Date]<d.[DayEnd]
 WHERE {edgeFilter}
 GROUP BY d.[Period], d.[ProductSource], d.[BranchSource], s.[SupplierCode], s.[BranchCode], s.[ProductCode]
)
SELECT [Period], CAST(CASE WHEN [ProductSource]=2 THEN 1 ELSE 0 END AS bit) [IsProductEdge], CAST(CASE WHEN [BranchSource]=2 THEN 1 ELSE 0 END AS bit) [IsBranchEdge],
       LTRIM(RTRIM(COALESCE([RawSupplierCode],''))) [RawSupplierCode],
       LTRIM(RTRIM(COALESCE([BranchCode],''))) [BranchCode],
       LTRIM(RTRIM(COALESCE([ProductCode],''))) [ProductCode],
       {SdmSumMeasures}
INTO #sdmBaseFacts
FROM RawFacts
GROUP BY [Period], [ProductSource], [BranchSource], {SdmTrimmedKeys};
""";
        var branchFacts = $"""
WITH BaseResolved AS
(
 SELECT e.[Period], e.[BranchCode], e.[RawSupplierCode], {SdmChina("e")} [ChinaSupplierCode], {SdmAus("e")} [AustralianSupplierCode],
        e.[ProductCode], {SdmPrefixed("e")}
 FROM #sdmBaseFacts e
 {SdmMappingJoins(database, "e")}
 WHERE e.[IsBranchEdge]=1
), Combined AS
(
 SELECT mo.[Period], b.[BranchCode], b.[RawSupplierCode], b.[ChinaSupplierCode], b.[AustralianSupplierCode],
        b.[MinProductCode], b.[MaxProductCode], {SdmPrefixed("b")}
 FROM [dbo].[SalesDetailQueryMonthlyBranch] b
 INNER JOIN #sdmMonths mo ON mo.[Month]=b.[Month] AND mo.[BranchValid]=1
 UNION ALL
 SELECT d.[Period], db.[BranchCode], db.[RawSupplierCode], db.[ChinaSupplierCode], db.[AustralianSupplierCode],
        db.[MinProductCode], db.[MaxProductCode], {SdmPrefixed("db")}
 FROM [dbo].[SalesDetailQueryDailyBranch] db
 INNER JOIN #sdmDays d ON d.[Day]=db.[Date] AND d.[BranchSource]=1
 UNION ALL
 SELECT [Period], [BranchCode], [RawSupplierCode], [ChinaSupplierCode], [AustralianSupplierCode], [ProductCode], [ProductCode], {SdmMeasures}
 FROM BaseResolved
)
SELECT [Period], [BranchCode], [RawSupplierCode], [ChinaSupplierCode], [AustralianSupplierCode],
       CASE WHEN @sdrKind=1 THEN [ChinaSupplierCode] ELSE [AustralianSupplierCode] END [SupplierCode],
       MIN([MinProductCode]) [MinProductCode], MAX([MaxProductCode]) [MaxProductCode], {SdmSumMeasures}
INTO #sdmBranchFacts
FROM Combined
GROUP BY [Period], [BranchCode], [RawSupplierCode], [ChinaSupplierCode], [AustralianSupplierCode] OPTION (HASH GROUP);
""";
        return (months, days, baseFacts, branchFacts);
    }

    /// <summary>
    /// 预聚合路径：完整落在各期内且身份有效的月份读月表，其余日期按日身份读日表，日表也没覆盖的日期才读日事实；
    /// 七个结果集与原查询同形同序。商品粒度归属在这里用当前映射解析，分店粒度用月表/日表烘焙的归属，
    /// 映射签名变化的月份和日期分店行退回日事实。汇总、供应商与分母改从分店粒度事实求和（同一批基础行的另一种分组，和数一致），
    /// 只有商品栏和选中商品的筛选需要商品粒度事实。
    /// 授权分店或选中分店时（分店范围），分店粒度事实直接按规范化门店码过滤；商品栏需要"分店 × 商品"，
    /// 按范围内外的事实行数在运行时二选一：范围小就逐日按主键读范围内门店，范围接近全部就用全部商品事实减去范围外门店。
    /// </summary>
    internal static string BuildSalesDetailReportSqlMonthly(
        string posmDatabase, DateRangeDto range, SalesDetailKind kind, string? selectedSupplier, string? selectedProduct,
        int pageIndex, int pageSize, IReadOnlySet<SalesDetailSection> wanted,
        IReadOnlyCollection<string>? selectedSuppliers = null, IReadOnlyCollection<string>? branches = null, string? selectedBranch = null,
        long scopeComplementThresholdRows = SalesDetailScopeComplementThresholdRows)
    {
        var hasCompare = HasCompare(range);
        var database = QuoteIdentifier(posmDatabase);
        // 单个供应商时与原先完全相同（@sdrSelectedSupplier），多选时为 IN 列表。
        var supplierFilter = BuildSalesDetailSupplierFilter(selectedSupplier, selectedSuppliers);
        var hasBranchScope = branches is { Count: > 0 };
        var hasScope = hasBranchScope || !string.IsNullOrWhiteSpace(selectedBranch);
        // 月表/日表只有规范化门店码；生产事实表门店码没有前导空格，尾随空格在比较时本就忽略，与原查询按原始码过滤等价。
        var authFilter = hasBranchScope ? $" AND [BranchCode] IN ({string.Join(",", branches!.Select((_, i) => $"@sdrBranch{i}"))})" : string.Empty;
        var scopeFilter = authFilter + (string.IsNullOrWhiteSpace(selectedBranch) ? string.Empty : " AND [BranchCode] = @sdrSelectedBranch");
        // 选中商品按商品索引读日事实时，门店码在索引包含列里，授权过滤能在回表前生效。
        var rawAuthFilter = hasBranchScope ? $" AND s.[BranchCode] IN ({string.Join(",", branches!.Select((_, i) => $"@sdrBranch{i}"))})" : string.Empty;
        var productFilter = string.IsNullOrWhiteSpace(selectedProduct) ? string.Empty : " AND [ProductCode] = @sdrSelectedProduct";
        var hasSelectedProduct = !string.IsNullOrWhiteSpace(selectedProduct);
        var offset = ((long)pageIndex - 1L) * pageSize;
        const string periods = SdmPeriods;
        const string measures = SdmMeasures;
        const string sumMeasures = SdmSumMeasures;
        const string factMeasures = SdmFactMeasures;
        const string trimmedKeys = SdmTrimmedKeys;
        string prefixed(string alias) => SdmPrefixed(alias);
        string china(string alias) => SdmChina(alias);
        string aus(string alias) => SdmAus(alias);
        string mappingJoins(string alias) => SdmMappingJoins(database, alias);

        var guard = $"""
IF OBJECT_ID(N'dbo.SalesDetailQueryMonthlyProduct', N'U') IS NULL OR OBJECT_ID(N'dbo.SalesDetailQueryMonthlyBranch', N'U') IS NULL
 OR OBJECT_ID(N'dbo.SalesDetailQueryMonthlyState', N'U') IS NULL OR OBJECT_ID(N'dbo.SalesDetailQueryDailyProduct', N'U') IS NULL
 OR OBJECT_ID(N'dbo.SalesDetailQueryDailyBranch', N'U') IS NULL OR OBJECT_ID(N'dbo.SalesDetailQueryDailyState', N'U') IS NULL
 THROW {SalesDetailQueryMonthlyProjection.MissingSchemaErrorNumber}, N'销售明细预聚合投影尚未建立。', 1;
DECLARE @sdmMappingVersion varchar(64) = {SalesDetailQueryProjection.BuildMappingSignatureSql(posmDatabase)};
""";
        var branchFactSql = BuildSalesDetailMonthlyBranchFactsSql(posmDatabase, branchEdgesOnly: false);
        // 选中商品时 #sdmBranchFacts 只含该商品；有分店范围时分母与范围取舍仍要全部商品的分店粒度事实。
        var allBranchFacts = hasSelectedProduct ? "#sdmAllBranchFacts" : "#sdmBranchFacts";
        var allBranchFactsSql = hasScope && hasSelectedProduct
            ? branchFactSql.BranchFacts.Replace("INTO #sdmBranchFacts", "INTO #sdmAllBranchFacts", StringComparison.Ordinal)
            : string.Empty;
        // 4. 商品粒度事实：月表 + 日表 + 日事实，归属用当前映射解析（与原查询同样在聚合后的键上 LEFT JOIN）。
        var productFacts = $"""
WITH Combined AS
(
 SELECT mo.[Period], mp.[RawSupplierCode], mp.[ProductCode], {prefixed("mp")}
 FROM [dbo].[SalesDetailQueryMonthlyProduct] mp
 INNER JOIN #sdmMonths mo ON mo.[Month]=mp.[Month] AND mo.[ProductValid]=1
 UNION ALL
 SELECT d.[Period], dp.[RawSupplierCode], dp.[ProductCode], {prefixed("dp")}
 FROM [dbo].[SalesDetailQueryDailyProduct] dp
 INNER JOIN #sdmDays d ON d.[Day]=dp.[Date] AND d.[ProductSource]=1
 UNION ALL
 SELECT [Period], [RawSupplierCode], [ProductCode], {measures} FROM #sdmBaseFacts WHERE [IsProductEdge]=1
), Grouped AS
(
 SELECT [Period], [RawSupplierCode], [ProductCode], {sumMeasures} FROM Combined GROUP BY [Period], [RawSupplierCode], [ProductCode]
)
SELECT g.[Period], g.[RawSupplierCode], {china("g")} [ChinaSupplierCode], {aus("g")} [AustralianSupplierCode], g.[ProductCode],
       CASE WHEN @sdrKind=1 THEN {china("g")} ELSE {aus("g")} END [SupplierCode],
       {prefixed("g")}
INTO #sdmProductFacts
FROM Grouped g
{mappingJoins("g")}
OPTION (HASH GROUP);
""";
        // 5. 分店粒度事实：选中商品时分店栏只看该商品，直接按商品索引读日事实；否则月表 + 日表 + 日事实（当前映射解析）。
        var branchFacts = !hasSelectedProduct
            ? branchFactSql.BranchFacts
            : $"""
WITH {periods}, RawFacts AS
(
 SELECT p.[Period], s.[SupplierCode] [RawSupplierCode], s.[BranchCode], s.[ProductCode], {factMeasures}
 FROM [ProductStoreDailySalesStatistic] s CROSS JOIN Periods p
 WHERE s.[Date]>=p.[StartDate] AND s.[Date]<p.[EndDate] AND s.[ProductCode]=@sdrSelectedProduct{rawAuthFilter}
   AND NOT EXISTS (SELECT 1 FROM [dbo].[SalesStatisticRefreshState] failed
                   WHERE failed.[StatisticType]=N'ProductStoreDaily'
                     AND failed.[Status]=N'Failed'
                     AND failed.[Date]=CONVERT(datetime, CONVERT(date, s.[Date])))
 GROUP BY p.[Period], s.[SupplierCode], s.[BranchCode], s.[ProductCode]
), Trimmed AS
(
 SELECT [Period], LTRIM(RTRIM(COALESCE([RawSupplierCode],''))) [RawSupplierCode], LTRIM(RTRIM(COALESCE([BranchCode],''))) [BranchCode],
        LTRIM(RTRIM(COALESCE([ProductCode],''))) [ProductCode], {sumMeasures}
 FROM RawFacts GROUP BY [Period], {trimmedKeys}
), Resolved AS
(
 SELECT e.[Period], e.[BranchCode], e.[RawSupplierCode], {china("e")} [ChinaSupplierCode], {aus("e")} [AustralianSupplierCode],
        e.[ProductCode], {prefixed("e")}
 FROM Trimmed e
 {mappingJoins("e")}
)
SELECT [Period], [BranchCode], [RawSupplierCode], [ChinaSupplierCode], [AustralianSupplierCode],
       CASE WHEN @sdrKind=1 THEN [ChinaSupplierCode] ELSE [AustralianSupplierCode] END [SupplierCode],
       MIN([ProductCode]) [MinProductCode], MAX([ProductCode]) [MaxProductCode], {sumMeasures}
INTO #sdmBranchFacts
FROM Resolved
GROUP BY [Period], [BranchCode], [RawSupplierCode], [ChinaSupplierCode], [AustralianSupplierCode];
""";

        // 6. 各栏位：列名与顺序和原查询完全一致（20 列）。
        string compare(string current, string zero) => hasCompare ? current : zero;
        string measureColumns(string prefix = "") => $"""
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [Revenue] ELSE 0 END),0) [Revenue], {compare("COALESCE(SUM(CASE WHEN [Period]=1 THEN [Revenue] ELSE 0 END),0)", "0")} [CompareRevenue],
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [Quantity] ELSE 0 END),0) [Quantity], {compare("COALESCE(SUM(CASE WHEN [Period]=1 THEN [Quantity] ELSE 0 END),0)", "0")} [CompareQuantity],
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [OrderCount] ELSE 0 END),0) [OrderCount], {compare("COALESCE(SUM(CASE WHEN [Period]=1 THEN [OrderCount] ELSE 0 END),0)", "0")} [CompareOrderCount],
 SUM(CASE WHEN [Period]=0 THEN [GrossProfit] END) [GrossProfit], {compare("SUM(CASE WHEN [Period]=1 THEN [GrossProfit] END)", "CAST(NULL AS decimal(18,2))")} [CompareGrossProfit],
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [StatisticRowCount] ELSE 0 END),0) [StatisticRowCount], COALESCE(SUM(CASE WHEN [Period]=0 THEN [CostedRowCount] ELSE 0 END),0) [CostedRowCount], COALESCE(SUM(CASE WHEN [Period]=0 THEN [GrossProfitRowCount] ELSE 0 END),0) [GrossProfitRowCount],
 {compare("COALESCE(SUM(CASE WHEN [Period]=1 THEN [StatisticRowCount] ELSE 0 END),0)", "0")} [CompareStatisticRowCount], {compare("COALESCE(SUM(CASE WHEN [Period]=1 THEN [CostedRowCount] ELSE 0 END),0)", "0")} [CompareCostedRowCount], {compare("COALESCE(SUM(CASE WHEN [Period]=1 THEN [GrossProfitRowCount] ELSE 0 END),0)", "0")} [CompareGrossProfitRowCount],
""";
        string productMultiplicity(string period, string min, string max)
            => $"CASE WHEN MIN(CASE WHEN [Period]={period} THEN [{min}] END) IS NULL THEN 0 WHEN MIN(CASE WHEN [Period]={period} THEN [{min}] END) = MAX(CASE WHEN [Period]={period} THEN [{max}] END) THEN 1 ELSE 2 END";
        string sectionSelect(string source, string whereClause, string group, string code, string name, string order, string minColumn, string maxColumn) => $"""
SELECT {code} [Code], {name} [Name], CAST(NULL AS nvarchar(50)) [ItemNumber], CAST(NULL AS nvarchar(200)) [ProductImage],
{measureColumns()}
 {productMultiplicity("0", minColumn, maxColumn)} [CurrentProductCount], {compare(productMultiplicity("1", minColumn, maxColumn), "0")} [CompareProductCount]
FROM {source} f {whereClause}
{(string.IsNullOrWhiteSpace(group) ? string.Empty : $"GROUP BY {group}")} {order};
""";
        var supplierName = $"CASE WHEN @sdrKind=1 THEN COALESCE(NULLIF(LTRIM(RTRIM((SELECT MAX(cName.[SupplierName]) FROM [ChinaSupplier] cName WHERE cName.[SupplierCode]=f.[SupplierCode]))), ''), f.[SupplierCode]) ELSE COALESCE(NULLIF(LTRIM(RTRIM((SELECT MAX(lName.[Name]) FROM [LocalSupplier] lName WHERE lName.[LocalSupplierCode]=f.[SupplierCode] AND lName.[IsDeleted]=0))), ''), CASE WHEN f.[SupplierCode]='{CHINA_LOCAL_SUPPLIER_CODE}' THEN '{CHINA_LOCAL_SUPPLIER_FALLBACK_NAME}' ELSE f.[SupplierCode] END) END";
        var branchName = "COALESCE(NULLIF(LTRIM(RTRIM((SELECT MAX(sName.[StoreName]) FROM [Store] sName WHERE sName.[StoreCode]=f.[BranchCode]))), ''), f.[BranchCode])";
        // 没有选中商品时，汇总、供应商、分母从分店粒度事实（约 1 万行）求和；选中商品时这些栏位要按商品过滤，仍用商品粒度事实。
        // 有分店范围时商品粒度事实没有门店，改用分店粒度事实（选中商品时它本就只含该商品）按门店过滤。
        var totalsSource = hasSelectedProduct && !hasScope ? "#sdmProductFacts" : "#sdmBranchFacts";
        var totalsMin = hasSelectedProduct && !hasScope ? "ProductCode" : "MinProductCode";
        var totalsMax = hasSelectedProduct && !hasScope ? "ProductCode" : "MaxProductCode";
        var totalsProductFilter = hasScope ? string.Empty : productFilter;
        var summary = sectionSelect(totalsSource, $"WHERE [SupplierCode] IS NOT NULL{supplierFilter}{totalsProductFilter}{scopeFilter}", string.Empty, "'summary'", "'当前筛选汇总'", string.Empty, totalsMin, totalsMax);
        var suppliers = sectionSelect(totalsSource, $"WHERE [SupplierCode] IS NOT NULL{totalsProductFilter}{scopeFilter}", "f.[SupplierCode]", "f.[SupplierCode]", supplierName, "ORDER BY [Revenue] DESC, [CompareRevenue] DESC, [Code] ASC", totalsMin, totalsMax);
        // 分店栏不按选中分店过滤，只受授权分店限制。
        var branchesSql = sectionSelect("#sdmBranchFacts", $"WHERE [SupplierCode] IS NOT NULL{supplierFilter}{authFilter}", "f.[BranchCode]", "f.[BranchCode]", branchName, "ORDER BY [Revenue] DESC, [CompareRevenue] DESC, [Code] ASC", "MinProductCode", "MaxProductCode");
        var productSource = hasScope ? "#sdmScopedProductFacts" : "#sdmProductFacts";
        // 商品名兜底也只看范围内门店，与原查询一致。
        var statScope = (hasBranchScope ? $" AND s0.[BranchCode] IN ({string.Join(",", branches!.Select((_, i) => $"@sdrBranch{i}"))})" : string.Empty)
            + (string.IsNullOrWhiteSpace(selectedBranch) ? string.Empty : " AND s0.[BranchCode] = @sdrSelectedBranch");
        var productAggregate = $"""
SELECT [ProductCode],
       SUM(CASE WHEN [Period]=0 THEN [Revenue] ELSE 0 END) [Revenue], {compare("SUM(CASE WHEN [Period]=1 THEN [Revenue] ELSE 0 END)", "0")} [CompareRevenue],
       SUM(CASE WHEN [Period]=0 THEN [Quantity] ELSE 0 END) [Quantity], {compare("SUM(CASE WHEN [Period]=1 THEN [Quantity] ELSE 0 END)", "0")} [CompareQuantity],
       SUM(CASE WHEN [Period]=0 THEN [OrderCount] ELSE 0 END) [OrderCount], {compare("SUM(CASE WHEN [Period]=1 THEN [OrderCount] ELSE 0 END)", "0")} [CompareOrderCount],
       SUM(CASE WHEN [Period]=0 THEN [GrossProfit] END) [GrossProfit], {compare("SUM(CASE WHEN [Period]=1 THEN [GrossProfit] END)", "CAST(NULL AS decimal(18,2))")} [CompareGrossProfit],
       SUM(CASE WHEN [Period]=0 THEN [StatisticRowCount] ELSE 0 END) [StatisticRowCount], SUM(CASE WHEN [Period]=0 THEN [CostedRowCount] ELSE 0 END) [CostedRowCount], SUM(CASE WHEN [Period]=0 THEN [GrossProfitRowCount] ELSE 0 END) [GrossProfitRowCount],
       {compare("SUM(CASE WHEN [Period]=1 THEN [StatisticRowCount] ELSE 0 END)", "0")} [CompareStatisticRowCount], {compare("SUM(CASE WHEN [Period]=1 THEN [CostedRowCount] ELSE 0 END)", "0")} [CompareCostedRowCount], {compare("SUM(CASE WHEN [Period]=1 THEN [GrossProfitRowCount] ELSE 0 END)", "0")} [CompareGrossProfitRowCount],
       CASE WHEN MAX(CASE WHEN [Period]=0 THEN 1 ELSE 0 END)=1 THEN 1 ELSE 0 END [CurrentProductCount],
       {compare("CASE WHEN MAX(CASE WHEN [Period]=1 THEN 1 ELSE 0 END)=1 THEN 1 ELSE 0 END", "0")} [CompareProductCount]
FROM {productSource} WHERE [SupplierCode] IS NOT NULL{supplierFilter}
GROUP BY [ProductCode]
""";
        var products = $"""
SELECT a.[ProductCode] [Code], COALESCE(NULLIF(LTRIM(RTRIM(p.[ProductName])), ''),NULLIF(LTRIM(RTRIM(stat.[StatisticProductName])), ''),a.[ProductCode]) [Name], p.[ItemNumber], p.[ProductImage],
       a.[Revenue], a.[CompareRevenue], a.[Quantity], a.[CompareQuantity], a.[OrderCount], a.[CompareOrderCount],
       a.[GrossProfit], a.[CompareGrossProfit], a.[StatisticRowCount], a.[CostedRowCount], a.[GrossProfitRowCount],
       a.[CompareStatisticRowCount], a.[CompareCostedRowCount], a.[CompareGrossProfitRowCount], a.[CurrentProductCount], a.[CompareProductCount]
FROM ({productAggregate} ORDER BY [Quantity] DESC, [CompareQuantity] DESC, [ProductCode] ASC OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY) a
OUTER APPLY (SELECT TOP (1) p0.[ProductName], p0.[EnglishName], p0.[ItemNumber], p0.[ProductImage], p0.[Barcode]
             FROM [Product] p0 WHERE p0.[ProductCode]=a.[ProductCode] ORDER BY p0.[UUID]) p
OUTER APPLY (SELECT TOP (1) s0.[ProductName] [StatisticProductName]
             FROM [ProductStoreDailySalesStatistic] s0
             WHERE NULLIF(LTRIM(RTRIM(p.[ProductName])), '') IS NULL
               AND s0.[ProductCode]=a.[ProductCode]
               AND ((s0.[Date]>=@sdrCurrentStart AND s0.[Date]<@sdrCurrentEnd) OR (@sdrHasCompare=1 AND s0.[Date]>=@sdrCompareStart AND s0.[Date]<@sdrCompareEnd)){statScope}
             ORDER BY s0.[Date] DESC) stat
ORDER BY a.[Quantity] DESC, a.[CompareQuantity] DESC, a.[ProductCode] ASC OPTION (HASH GROUP);
""";
        var productCount = $"SELECT COUNT(*) FROM (SELECT [ProductCode] FROM {productSource} WHERE [SupplierCode] IS NOT NULL{supplierFilter} GROUP BY [ProductCode]) x;";
        // 分母不按商品过滤：无分店范围时沿用原来源；有分店范围时用全部商品的分店粒度事实按门店过滤。
        var denominatorSource = hasScope ? allBranchFacts : totalsSource;
        var denominator = wanted.Contains(SalesDetailSection.Suppliers)
            ? $"SELECT COALESCE(SUM(CASE WHEN [Period]=0 AND [AustralianSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0), COALESCE(SUM(CASE WHEN [Period]=0 AND [ChinaSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0), COALESCE(SUM(CASE WHEN [Period]=1 AND [AustralianSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0), COALESCE(SUM(CASE WHEN [Period]=1 AND [ChinaSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0) FROM {denominatorSource} f WHERE [AustralianSupplierCode] IS NOT NULL{scopeFilter};"
            : "SELECT 0,0,0,0;";
        const string emptyRows = "SELECT TOP 0 CAST(NULL AS nvarchar(50)) [Code], CAST(NULL AS nvarchar(200)) [Name], CAST(NULL AS nvarchar(50)) [ItemNumber], CAST(NULL AS nvarchar(200)) [ProductImage], CAST(0 AS decimal(18,2)) [Revenue], CAST(0 AS decimal(18,2)) [CompareRevenue], CAST(0 AS int) [Quantity], CAST(0 AS int) [CompareQuantity], CAST(0 AS int) [OrderCount], CAST(0 AS int) [CompareOrderCount], CAST(NULL AS decimal(18,2)) [GrossProfit], CAST(NULL AS decimal(18,2)) [CompareGrossProfit], CAST(0 AS int) [StatisticRowCount], CAST(0 AS int) [CostedRowCount], CAST(0 AS int) [GrossProfitRowCount], CAST(0 AS int) [CompareStatisticRowCount], CAST(0 AS int) [CompareCostedRowCount], CAST(0 AS int) [CompareGrossProfitRowCount], CAST(0 AS int) [CurrentProductCount], CAST(0 AS int) [CompareProductCount];";
        if (!wanted.Contains(SalesDetailSection.Summary)) summary = emptyRows;
        if (!wanted.Contains(SalesDetailSection.Suppliers)) suppliers = emptyRows;
        if (!wanted.Contains(SalesDetailSection.Branches)) branchesSql = emptyRows;
        if (!wanted.Contains(SalesDetailSection.Products)) { products = emptyRows; productCount = "SELECT 0;"; }
        var status = "SELECT [StatisticType],[Date],[Status],[LastAggregatedAtUtc],[CompletedAtUtc],[SourceProductVersion] FROM [SalesStatisticRefreshState] WHERE [StatisticType]='ProductStoreDaily' AND (([Date]>=@sdrCurrentStart AND [Date]<@sdrCurrentEnd) OR (@sdrHasCompare=1 AND [Date]>=@sdrCompareStart AND [Date]<@sdrCompareEnd)) ORDER BY [Date],[StatisticType];";
        if (!hasScope)
            return guard + branchFactSql.Months + branchFactSql.Days + branchFactSql.BaseFacts + productFacts + branchFacts + status + summary + suppliers + branchesSql + products + productCount + denominator
                + "DROP TABLE #sdmMonths;DROP TABLE #sdmDays;DROP TABLE #sdmBaseFacts;DROP TABLE #sdmProductFacts;DROP TABLE #sdmBranchFacts;";
        var scopedProductFacts = BuildSalesDetailMonthlyScopedProductFactsSql(database, allBranchFacts, scopeFilter, productFacts, scopeComplementThresholdRows);
        return guard + branchFactSql.Months + branchFactSql.Days + branchFactSql.BaseFacts + branchFacts + allBranchFactsSql + scopedProductFacts
            + status + summary + suppliers + branchesSql + products + productCount + denominator
            + "DROP TABLE #sdmMonths;DROP TABLE #sdmDays;DROP TABLE #sdmBaseFacts;DROP TABLE IF EXISTS #sdmProductFacts;DROP TABLE #sdmBranchFacts;"
            + "DROP TABLE IF EXISTS #sdmAllBranchFacts;DROP TABLE #sdmScopedProductFacts;";
    }

    /// <summary>
    /// 分店范围内的商品粒度事实 #sdmScopedProductFacts（列同 #sdmProductFacts）。月表/日表的商品粒度不含门店，只能读日事实：
    /// 按分店粒度事实里范围内外的事实行数取舍——范围内较少时逐日按聚集主键 (Date, BranchCode) 读范围内门店；
    /// 范围接近全部（如关联了几十家店的账号）时读全部商品事实，再减去逐日读出的范围外门店。
    /// 生产事实表 Date 全是零点，逐日等值查找才能同时用上主键第二列。
    /// </summary>
    private static string BuildSalesDetailMonthlyScopedProductFactsSql(string database, string allBranchFacts, string scopeFilter, string productFactsSql,
        long complementThresholdRows)
    {
        return $"""
CREATE TABLE #sdmScopedProductFacts
(
 [Period] int NOT NULL, [RawSupplierCode] nvarchar(255) COLLATE DATABASE_DEFAULT NOT NULL,
 [ChinaSupplierCode] nvarchar(255) COLLATE DATABASE_DEFAULT NULL, [AustralianSupplierCode] nvarchar(255) COLLATE DATABASE_DEFAULT NULL,
 [ProductCode] nvarchar(255) COLLATE DATABASE_DEFAULT NOT NULL, [SupplierCode] nvarchar(255) COLLATE DATABASE_DEFAULT NULL,
 [Revenue] decimal(38,4) NULL, [Quantity] bigint NULL, [OrderCount] bigint NULL, [GrossProfit] decimal(38,4) NULL,
 [StatisticRowCount] bigint NULL, [CostedRowCount] bigint NULL, [GrossProfitRowCount] bigint NULL
);
DECLARE @sdmScopeRows bigint, @sdmOtherRows bigint;
SELECT @sdmScopeRows = COALESCE(SUM(CASE WHEN 1=1{scopeFilter} THEN [StatisticRowCount] ELSE 0 END),0),
       @sdmOtherRows = COALESCE(SUM(CASE WHEN 1=1{scopeFilter} THEN 0 ELSE [StatisticRowCount] END),0)
FROM {allBranchFacts};
DECLARE @sdmUseComplement bit = CASE WHEN @sdmScopeRows > @sdmOtherRows + {complementThresholdRows} THEN 1 ELSE 0 END;
SELECT DISTINCT [BranchCode] INTO #sdmReadBranches FROM {allBranchFacts}
WHERE (@sdmUseComplement=0 AND 1=1{scopeFilter}) OR (@sdmUseComplement=1 AND NOT (1=1{scopeFilter}));
WITH {SdmPeriods}, Digits(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n)),
DayOffsets(n) AS (SELECT a.n + b.n * 10 + c.n * 100 FROM Digits a CROSS JOIN Digits b CROSS JOIN Digits c)
-- 统计失败日与全部商品事实（月表/日表路径）一样排除，直接读取与"全部减范围外"两边口径一致。
SELECT p.[Period], CONVERT(datetime, DATEADD(day, o.n, p.[StartDate])) [DayStart]
INTO #sdmScopeDays
FROM Periods p CROSS JOIN DayOffsets o WHERE o.n < DATEDIFF(day, p.[StartDate], p.[EndDate])
  AND NOT EXISTS (SELECT 1 FROM [dbo].[SalesStatisticRefreshState] failed
                  WHERE failed.[StatisticType]=N'ProductStoreDaily' AND failed.[Status]=N'Failed'
                    AND failed.[Date]=CONVERT(datetime, DATEADD(day, o.n, p.[StartDate])));
-- 先只做事实读取与聚合（强制逐日主键查找），映射解析放到下一条语句，避免连接提示连带固定映射连接方式。
SELECT [Period], LTRIM(RTRIM(COALESCE([RawSupplierCode],''))) [RawSupplierCode], LTRIM(RTRIM(COALESCE([ProductCode],''))) [ProductCode], {SdmSumMeasures}
INTO #sdmReadProductFacts
FROM
(
 SELECT d.[Period], s.[SupplierCode] [RawSupplierCode], s.[ProductCode], {SdmFactMeasures}
 FROM #sdmScopeDays d CROSS JOIN #sdmReadBranches b
 INNER LOOP JOIN [ProductStoreDailySalesStatistic] s ON s.[Date]=d.[DayStart] AND s.[BranchCode]=b.[BranchCode]
 GROUP BY d.[Period], s.[SupplierCode], s.[ProductCode]
) r
GROUP BY [Period], LTRIM(RTRIM(COALESCE([RawSupplierCode],''))), LTRIM(RTRIM(COALESCE([ProductCode],'')));
IF @sdmUseComplement=0
 INSERT INTO #sdmScopedProductFacts
 SELECT g.[Period], g.[RawSupplierCode], {SdmChina("g")}, {SdmAus("g")}, g.[ProductCode],
        CASE WHEN @sdrKind=1 THEN {SdmChina("g")} ELSE {SdmAus("g")} END, {SdmPrefixed("g")}
 FROM #sdmReadProductFacts g
 {SdmMappingJoins(database, "g")};
ELSE
BEGIN
{productFactsSql}
 -- 同一 (期间, 原始供应商, 商品) 键上全部减去范围外；映射一码多供应商时每个映射行都减同一份，与原查询的倍增口径一致。
 INSERT INTO #sdmScopedProductFacts
 SELECT pf.[Period], pf.[RawSupplierCode], pf.[ChinaSupplierCode], pf.[AustralianSupplierCode], pf.[ProductCode], pf.[SupplierCode],
        pf.[Revenue]-COALESCE(ex.[Revenue],0), pf.[Quantity]-COALESCE(ex.[Quantity],0), pf.[OrderCount]-COALESCE(ex.[OrderCount],0),
        CASE WHEN pf.[GrossProfitRowCount]-COALESCE(ex.[GrossProfitRowCount],0)=0 THEN NULL ELSE COALESCE(pf.[GrossProfit],0)-COALESCE(ex.[GrossProfit],0) END,
        pf.[StatisticRowCount]-COALESCE(ex.[StatisticRowCount],0), pf.[CostedRowCount]-COALESCE(ex.[CostedRowCount],0),
        pf.[GrossProfitRowCount]-COALESCE(ex.[GrossProfitRowCount],0)
 FROM #sdmProductFacts pf
 LEFT JOIN #sdmReadProductFacts ex ON ex.[Period]=pf.[Period] AND ex.[RawSupplierCode]=pf.[RawSupplierCode] AND ex.[ProductCode]=pf.[ProductCode]
 WHERE pf.[StatisticRowCount]-COALESCE(ex.[StatisticRowCount],0)>0;
END;
DROP TABLE #sdmReadBranches;DROP TABLE #sdmScopeDays;DROP TABLE #sdmReadProductFacts;
""";
    }
}
