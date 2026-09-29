namespace BlazorApp.Api.Services.React;

public partial class SalesDashboardReactService
{
    /// <summary>月投影四张表齐全时，关键词查询的供应商/分店/分母改读月投影的分店粒度事实。</summary>
    private const string SalesDetailMonthlyTotalsReadySql =
        "OBJECT_ID(N'dbo.SalesDetailQueryMonthlyBranch', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SalesDetailQueryMonthlyState', N'U') IS NOT NULL"
        + " AND OBJECT_ID(N'dbo.SalesDetailQueryDailyBranch', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SalesDetailQueryDailyState', N'U') IS NOT NULL";

    private static string BuildSalesDetailProjectionGuardSql(string posmDatabase, string sourceBranch)
    {
        var projectionBranch = sourceBranch.Replace("s.[BranchCode]", "s.[SourceBranchCode]", StringComparison.Ordinal);
        // 供应商归类核对只保护日投影里烘焙的澳洲归属；汇总改读月投影时它不再被消费，省去整段日投影扫描。
        var checkSupplierClassification = $"NOT ({SalesDetailMonthlyTotalsReadySql})";
        // 全局映射变化时核对该日实际用到的商品和供应商分类，无关商品同步不会淘汰整段历史。
        // 澳洲栏位不展示中国分母且无中国目录词命中时，只需证明相关 raw 分类及映射行数未变。
        // CompletedAt 仅用于审计；
        // ProvisionalFresh 批末升级 Fresh 会修改它，事实身份由版本、任务和 LastAggregatedAt 共同确定。
        return $"""
-- 覆盖检查和后面的读取都在调用方的同一 SNAPSHOT 中。
-- 统计失败日先整体排除；其余缺少或无法核验投影的日期只跳过并在响应中提示，不报错、也不回退全表扫描。
-- 前端最多请求 731 天（两年）；更长的关键词请求明确报错，不能截断覆盖检查后误放行。
IF DATEDIFF(day,@sdrCurrentStart,@sdrCurrentEnd)>731
 OR (@sdrHasCompare=1 AND DATEDIFF(day,@sdrCompareStart,@sdrCompareEnd)>731)
 THROW 51012, N'销售明细查询投影日期范围超限。', 1;
IF OBJECT_ID(N'dbo.SalesDetailQueryDaily', N'U') IS NULL
 OR OBJECT_ID(N'dbo.SalesDetailQueryProductAlias', N'U') IS NULL
 OR OBJECT_ID(N'dbo.SalesDetailQueryProjectionState', N'U') IS NULL
 OR OBJECT_ID(N'dbo.SalesDetailQueryMappingUse', N'U') IS NULL
 THROW 51012, N'销售明细查询投影尚未建立。', 1;
WITH Digits(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n)),
DayOffsets(n) AS (SELECT a.n + b.n * 10 + c.n * 100 FROM Digits a CROSS JOIN Digits b CROSS JOIN Digits c),
RequiredDates AS
(
 SELECT DATEADD(day,n,@sdrCurrentStart) [Date] FROM DayOffsets WHERE n<DATEDIFF(day,@sdrCurrentStart,@sdrCurrentEnd)
 UNION
 SELECT DATEADD(day,n,@sdrCompareStart) FROM DayOffsets WHERE @sdrHasCompare=1 AND n<DATEDIFF(day,@sdrCompareStart,@sdrCompareEnd)
)
SELECT [Date] INTO #SalesDetailRequiredDates FROM RequiredDates;
-- 对账失败日不参与投影计算；状态结果仍单独返回给前端提示，避免失败日触发整段事实回退。
DELETE d
FROM #SalesDetailRequiredDates d
INNER JOIN dbo.SalesStatisticRefreshState r
  ON r.[Date]=d.[Date]
 AND r.[StatisticType]='ProductStoreDaily'
 AND r.[Status]='Failed';
DECLARE @sdrMappingVersion varchar(64) = {SalesDetailQueryProjection.BuildMappingSignatureSql(posmDatabase)};
-- 缺少投影或发布身份对不上的日期（如对账失败、从未发布的历史日）逐日记入跳过清单；
-- 之后的投影汇总、候选事实和映射核验都只处理剩余日期，被跳过的日期由接口列在提示里。
SELECT CONVERT(date,d.[Date]) [Date] INTO #SalesDetailSkippedDates
FROM #SalesDetailRequiredDates d
LEFT JOIN dbo.SalesStatisticRefreshState r ON r.[Date]=d.[Date] AND r.[StatisticType]='ProductStoreDaily'
LEFT JOIN dbo.SalesDetailQueryProjectionState p ON p.[Date]=d.[Date]
WHERE p.[Date] IS NULL OR r.[Date] IS NULL
   OR p.[ProjectionSchemaVersion]<>{SalesDetailQueryProjection.SchemaVersion}
   OR r.[Status] NOT IN ('Fresh','ProvisionalFresh','Queued','Running')
   OR NULLIF(r.[SourceProductVersion],'') IS NULL
   OR r.[LastAggregatedAtUtc] IS NULL
   OR EXISTS
      (SELECT p.[SourceProductVersion],p.[SourceLastAggregatedAtUtc]
       EXCEPT SELECT r.[SourceProductVersion],r.[LastAggregatedAtUtc])
   -- 排队会更换下一次任务的 JobId，但旧事实仍完整发布；成功提交时再严格核对新任务身份。
   OR (r.[Status] IN ('Fresh','ProvisionalFresh')
       AND EXISTS (SELECT p.[SourceJobId] EXCEPT SELECT r.[JobId]));
DELETE d FROM #SalesDetailRequiredDates d WHERE EXISTS (SELECT 1 FROM #SalesDetailSkippedDates k WHERE k.[Date]=d.[Date]);

-- 映射变化时先在去重后的关联键上找出冲突，再只回查用到冲突键的日期并跳过，避免相关子查询按日期重复扫描。
SELECT p.[Date],p.[MappingHasFanout] INTO #SalesDetailChangedMappingDates
FROM #SalesDetailRequiredDates d JOIN dbo.SalesDetailQueryProjectionState p ON p.[Date]=d.[Date]
WHERE p.[MappingVersion]<>@sdrMappingVersion;
IF EXISTS (SELECT 1 FROM #SalesDetailChangedMappingDates)
BEGIN
 -- 当前映射出现一码多供应商时，旧投影的金额口径全部不可复用；否则只跳过旧快照本身带放大的日期。
 IF EXISTS (SELECT [ProductCode] FROM {QuoteIdentifier(posmDatabase)}.dbo.posm_product_supplier_mapping
            WHERE [LocalSupplierCode]='200' AND [IsDeleted]=0 AND [ProductCode] IS NOT NULL
            GROUP BY [ProductCode] HAVING COUNT_BIG(*)>1)
  INSERT INTO #SalesDetailSkippedDates SELECT [Date] FROM #SalesDetailChangedMappingDates;
 ELSE
 BEGIN
  INSERT INTO #SalesDetailSkippedDates SELECT [Date] FROM #SalesDetailChangedMappingDates WHERE [MappingHasFanout]<>0;

  IF {checkSupplierClassification}
  BEGIN
  SELECT DISTINCT s.[RawSupplierCode],s.[AustralianSupplierCode] INTO #SalesDetailChangedSupplierKeys
  FROM dbo.SalesDetailQueryDaily s JOIN #SalesDetailChangedMappingDates d ON d.[Date]=s.[Date]
  WHERE d.[MappingHasFanout]=0{projectionBranch};
  SELECT s.[RawSupplierCode],s.[AustralianSupplierCode] INTO #SalesDetailConflictSupplierKeys
  FROM #SalesDetailChangedSupplierKeys s
  LEFT JOIN (SELECT DISTINCT [SupplierCode] FROM dbo.ChinaSupplier WHERE [SupplierCode] IS NOT NULL AND [SupplierCode]<>'') c
    ON c.[SupplierCode]=s.[RawSupplierCode]
  WHERE EXISTS (SELECT CASE WHEN s.[RawSupplierCode]='200' OR c.[SupplierCode] IS NOT NULL THEN '200' ELSE NULLIF(s.[RawSupplierCode],'') END
                EXCEPT SELECT s.[AustralianSupplierCode]);
  -- 供应商归类变化只影响实际出现该供应商的日期。
  IF EXISTS (SELECT 1 FROM #SalesDetailConflictSupplierKeys)
   INSERT INTO #SalesDetailSkippedDates
   SELECT DISTINCT d.[Date] FROM #SalesDetailChangedMappingDates d
   JOIN dbo.SalesDetailQueryDaily s ON s.[Date]=d.[Date]
   JOIN #SalesDetailConflictSupplierKeys k ON k.[RawSupplierCode]=s.[RawSupplierCode]
    AND EXISTS (SELECT k.[AustralianSupplierCode] INTERSECT SELECT s.[AustralianSupplierCode])
   WHERE d.[MappingHasFanout]=0{projectionBranch};
  DROP TABLE #SalesDetailConflictSupplierKeys;
  DROP TABLE #SalesDetailChangedSupplierKeys;
  END;

  IF @sdrKind=1 OR EXISTS (SELECT 1 FROM #SalesDetailSupplierSearchMatches)
  BEGIN
   SELECT DISTINCT u.[ProductCode],u.[ChinaSupplierCode] INTO #SalesDetailChangedProductMappings
   FROM dbo.SalesDetailQueryMappingUse u JOIN #SalesDetailChangedMappingDates d ON d.[Date]=u.[Date]
   WHERE d.[MappingHasFanout]=0;
   SELECT u.[ProductCode],u.[ChinaSupplierCode] INTO #SalesDetailConflictProductMappings
   FROM #SalesDetailChangedProductMappings u
   -- 映射商品码为 varchar，日投影为 nvarchar；使用哈希连接，避免隐式转换导致逐商品全表扫描。
   LEFT HASH JOIN {QuoteIdentifier(posmDatabase)}.dbo.posm_product_supplier_mapping m
     ON m.[ProductCode]=u.[ProductCode] AND m.[LocalSupplierCode]='200' AND m.[IsDeleted]=0
   WHERE EXISTS (SELECT NULLIF(LTRIM(RTRIM(m.[ChinaSupplierCode])), '') EXCEPT SELECT u.[ChinaSupplierCode]);
   -- 商品改映射只影响卖过该商品的日期，其余日期继续复用投影。
   IF EXISTS (SELECT 1 FROM #SalesDetailConflictProductMappings)
    INSERT INTO #SalesDetailSkippedDates
    SELECT DISTINCT d.[Date] FROM #SalesDetailChangedMappingDates d
    JOIN dbo.SalesDetailQueryMappingUse u ON u.[Date]=d.[Date]
    INNER HASH JOIN #SalesDetailConflictProductMappings k ON k.[ProductCode]=u.[ProductCode]
    WHERE d.[MappingHasFanout]=0 AND EXISTS (SELECT k.[ChinaSupplierCode] INTERSECT SELECT u.[ChinaSupplierCode]);
   DROP TABLE #SalesDetailConflictProductMappings;
   DROP TABLE #SalesDetailChangedProductMappings;
  END;
 END;
 DELETE d FROM #SalesDetailRequiredDates d WHERE EXISTS (SELECT 1 FROM #SalesDetailSkippedDates k WHERE k.[Date]=d.[Date]);
END;
DROP TABLE #SalesDetailChangedMappingDates;
""";
    }

    private static string BuildSalesDetailProjectionSourceSql(
        string posmDatabase, string sourceBranch, int candidateToken, string? selectedProduct, string queryHint, string candidateRefinementSql)
    {
        // 最终条件是所有关键词同时成立，所以任选一个必要条件都能覆盖全部正确结果。
        // 用较长词先缩小候选，避免短词命中大量供应商后搬入整段事实；最终仍逐词精筛。
        var aliasFilter = $"(LTRIM(RTRIM(a.[ProductCode])) LIKE @sdrSearch{candidateToken} OR a.[ProductName] LIKE @sdrSearch{candidateToken} OR a.[Barcode] LIKE @sdrSearch{candidateToken})";
        var supplierFilter = $"(f.[RawSupplierCode] LIKE @sdrSearch{candidateToken} OR f.[SupplierCode] LIKE @sdrSearch{candidateToken} OR f.[SupplierName] LIKE @sdrSearch{candidateToken})";
        var selectedProductFilter = string.IsNullOrWhiteSpace(selectedProduct)
            ? string.Empty : " OR LTRIM(RTRIM(a.[ProductCode]))=@sdrSelectedProduct";
        // 授权在原始门店码上应用，之后才合并规范化后的门店码，保持旧查询的空白处理边界。
        var projectionBranch = sourceBranch.Replace("s.[BranchCode]", "s.[SourceBranchCode]", StringComparison.Ordinal);
        // 失败日与守卫跳过的日期都已从 #SalesDetailRequiredDates 删除；各读取路径按它等值连接即统一排除。
        var dates = "((s.[Date]>=@sdrCurrentStart AND s.[Date]<@sdrCurrentEnd) OR (@sdrHasCompare=1 AND s.[Date]>=@sdrCompareStart AND s.[Date]<@sdrCompareEnd))";
        var sourceColumns = "s.[Date],s.[SupplierCode],s.[BranchCode],s.[ProductCode],s.[ProductName],s.[Barcode],s.[TotalQuantity],s.[TotalAmount],s.[OrderCount],s.[GrossProfit],s.[TotalCost]";
        // 日投影汇总：periods 用派生表而非 CTE，便于放进 IF/ELSE 分支；into 为空时追加到已建好的临时表。
        string dailyTotals(string top, string insert, string into) => $"""
{insert}SELECT {top}periods.[Period],s.[RawSupplierCode],s.[ChinaSupplierCode],s.[AustralianSupplierCode],s.[BranchCode],
 MIN(s.[MinProductCode]) [ProductCode],MIN(s.[MinProductCode]) [MinProductCode],MAX(s.[MaxProductCode]) [MaxProductCode],
 CAST(NULL AS nvarchar(255)) [StatisticProductName],CAST(NULL AS nvarchar(100)) [StatisticBarcode],
 SUM(s.[Revenue]) [Revenue],SUM(s.[Quantity]) [Quantity],SUM(s.[OrderCount]) [OrderCount],SUM(s.[GrossProfit]) [GrossProfit],
 SUM(s.[StatisticRowCount]) [StatisticRowCount],SUM(s.[CostedRowCount]) [CostedRowCount],SUM(s.[GrossProfitRowCount]) [GrossProfitRowCount]
{into}FROM dbo.SalesDetailQueryDaily s
JOIN #SalesDetailRequiredDates d ON d.[Date]=s.[Date]
CROSS JOIN (SELECT 0 [Period],@sdrCurrentStart [StartDate],@sdrCurrentEnd [EndDate]
            UNION ALL SELECT 1,@sdrCompareStart,@sdrCompareEnd WHERE @sdrHasCompare=1) periods
WHERE s.[Date]>=periods.[StartDate] AND s.[Date]<periods.[EndDate]{projectionBranch}
GROUP BY periods.[Period],s.[RawSupplierCode],s.[ChinaSupplierCode],s.[AustralianSupplierCode],s.[BranchCode]{queryHint};
""";
        // 关键词不改变供应商/分店栏与分母的统计范围。与无关键词页面一样读月表 → 日表 → 日事实的分店粒度事实：
        // 日投影按日期范围汇总要读几十万宽行（生产 7 个月双期 5–9 秒，8 家授权店 6 秒），月投影只要零点几秒，且与无关键词页面数字一致。
        // 月投影只有规范化门店码；生产事实表门店码没有前导空格（尾随空格比较时本就忽略），授权过滤与按原始码等价。月投影表缺失时退回日投影。
        // 月投影同样排除统计失败日；守卫因投影覆盖跳过的日期不影响这三栏（它们不依赖关键词投影）。
        const string into = "INTO #SalesDetailProjectionTotals\n";
        var monthly = BuildSalesDetailMonthlyBranchFactsSql(posmDatabase, branchEdgesOnly: true);
        var monthlyBranch = sourceBranch.Replace("s.[BranchCode]", "[BranchCode]", StringComparison.Ordinal);
        var totals = dailyTotals("TOP (0) ", string.Empty, into) + $"""
IF {SalesDetailMonthlyTotalsReadySql}
BEGIN
DECLARE @sdmMappingVersion varchar(64) = @sdrMappingVersion;
{monthly.Months}{monthly.Days}{monthly.BaseFacts}{monthly.BranchFacts}
INSERT INTO #SalesDetailProjectionTotals
SELECT [Period],[RawSupplierCode],[ChinaSupplierCode],[AustralianSupplierCode],[BranchCode],
 [MinProductCode],[MinProductCode],[MaxProductCode],CAST(NULL AS nvarchar(255)),CAST(NULL AS nvarchar(100)),
 [Revenue],[Quantity],[OrderCount],[GrossProfit],[StatisticRowCount],[CostedRowCount],[GrossProfitRowCount]
FROM #sdmBranchFacts
WHERE 1=1{monthlyBranch};
DROP TABLE #sdmMonths;DROP TABLE #sdmDays;DROP TABLE #sdmBaseFacts;DROP TABLE #sdmBranchFacts;
END
ELSE
BEGIN
{dailyTotals(string.Empty, "INSERT INTO #SalesDetailProjectionTotals\n", string.Empty)}END;
""";
        return $"""
{totals}

-- 当前资料、规范码或选中商品命中时，该码的全部原始形式已经一并进入候选。
-- 只有历史名称/条码单独命中的商品才需要额外展开，避免普通货号搜索再扫一遍完整别名词典。
SELECT DISTINCT a.[ProductCode],
 CAST(CASE WHEN p.[ProductCode] IS NOT NULL OR LTRIM(RTRIM(a.[ProductCode])) LIKE @sdrSearch{candidateToken}{selectedProductFilter}
  THEN 0 ELSE 1 END AS bit) [NeedsExpansion]
INTO #SalesDetailCandidateSeeds
FROM dbo.SalesDetailQueryProductAlias a
LEFT HASH JOIN (SELECT DISTINCT [ProductCode] FROM #SalesDetailProductSearchMatches WHERE [TokenIndex]={candidateToken}) p
 ON p.[ProductCode]=LTRIM(RTRIM(a.[ProductCode]))
WHERE ({aliasFilter}){selectedProductFilter}
 OR p.[ProductCode] IS NOT NULL{queryHint};
SELECT [ProductCode] INTO #SalesDetailCandidateProducts FROM #SalesDetailCandidateSeeds;
IF EXISTS (SELECT 1 FROM #SalesDetailCandidateSeeds WHERE [NeedsExpansion]=1)
 INSERT INTO #SalesDetailCandidateProducts
 SELECT DISTINCT a.[ProductCode] FROM dbo.SalesDetailQueryProductAlias a
 INNER HASH JOIN
  (SELECT DISTINCT LTRIM(RTRIM([ProductCode])) [ProductCode] FROM #SalesDetailCandidateSeeds WHERE [NeedsExpansion]=1) p
  ON p.[ProductCode]=LTRIM(RTRIM(a.[ProductCode]))
 WHERE NOT EXISTS (SELECT 1 FROM #SalesDetailCandidateProducts old WHERE old.[ProductCode]=a.[ProductCode]){queryHint};
DROP TABLE #SalesDetailCandidateSeeds;

-- 名称匹配只对不同的供应商键执行一次，避免按日期和分店重复扫描相同名称。
WITH SupplierKeys AS
(
 SELECT DISTINCT [RawSupplierCode],[ChinaSupplierCode],[AustralianSupplierCode] FROM #SalesDetailProjectionTotals
), SupplierNames AS
(
 SELECT d.[RawSupplierCode],d.[ChinaSupplierCode],
  CASE WHEN @sdrKind=1 THEN d.[ChinaSupplierCode] ELSE d.[AustralianSupplierCode] END [SupplierCode],
  CASE WHEN @sdrKind=1 THEN
    CASE WHEN d.[RawSupplierCode]='200' OR d.[ChinaSupplierCode] IS NOT NULL
      THEN COALESCE(NULLIF(LTRIM(RTRIM(china.[SupplierName])),''),'200')
      ELSE COALESCE(NULLIF(LTRIM(RTRIM(local.[Name])),''),d.[RawSupplierCode]) END
   ELSE COALESCE(NULLIF(LTRIM(RTRIM(local.[Name])),''),CASE WHEN d.[AustralianSupplierCode]='200' THEN 'hotbargain' ELSE d.[AustralianSupplierCode] END) END [SupplierName]
 FROM SupplierKeys d
 LEFT JOIN (SELECT [SupplierCode],MAX([SupplierName]) [SupplierName] FROM dbo.ChinaSupplier
            WHERE [SupplierCode] IS NOT NULL AND [SupplierCode]<>'' GROUP BY [SupplierCode]) china ON china.[SupplierCode]=d.[ChinaSupplierCode]
 LEFT JOIN dbo.LocalSupplier local ON local.[LocalSupplierCode]=CASE WHEN @sdrKind=1 THEN d.[RawSupplierCode] ELSE d.[AustralianSupplierCode] END AND local.[IsDeleted]=0
)
SELECT * INTO #SalesDetailProjectionSupplierNames FROM SupplierNames;
SELECT DISTINCT f.[RawSupplierCode],f.[ChinaSupplierCode] INTO #SalesDetailCandidateSupplierKeys
FROM #SalesDetailProjectionSupplierNames f
WHERE ({supplierFilter})
 OR EXISTS (SELECT 1 FROM #SalesDetailSupplierSearchMatches c WHERE c.[SupplierCode]=f.[ChinaSupplierCode] AND c.[TokenIndex]={candidateToken});

-- 仓库供应商都使用 raw 200；命中一家中国供应商时按已核验的每日映射缩到商品，避免读入整个仓库。
-- 有重复映射的旧快照保留原始供应商路径，继续维持原查询的金额倍增口径。
DECLARE @sdrMappingFanout bit=CASE WHEN EXISTS
 (SELECT 1 FROM #SalesDetailRequiredDates d JOIN dbo.SalesDetailQueryProjectionState p ON p.[Date]=d.[Date]
  WHERE p.[MappingHasFanout]<>0) THEN 1 ELSE 0 END;
SELECT DISTINCT [RawSupplierCode] INTO #SalesDetailCandidateSuppliers
FROM #SalesDetailCandidateSupplierKeys WHERE [RawSupplierCode]<>'200' OR @sdrMappingFanout=1;
IF @sdrMappingFanout=0 AND EXISTS (SELECT 1 FROM #SalesDetailCandidateSupplierKeys WHERE [RawSupplierCode]='200')
BEGIN
 SELECT DISTINCT u.[ProductCode] INTO #SalesDetailWarehouseCandidateCodes
 FROM dbo.SalesDetailQueryMappingUse u JOIN #SalesDetailRequiredDates d ON d.[Date]=u.[Date]
 INNER HASH JOIN (SELECT DISTINCT [ChinaSupplierCode] FROM #SalesDetailCandidateSupplierKeys WHERE [RawSupplierCode]='200') c
  ON ISNULL(c.[ChinaSupplierCode],N'')=ISNULL(u.[ChinaSupplierCode],N'');
 INSERT INTO #SalesDetailCandidateProducts
 SELECT DISTINCT a.[ProductCode] FROM dbo.SalesDetailQueryProductAlias a
 INNER HASH JOIN #SalesDetailWarehouseCandidateCodes p ON p.[ProductCode]=LTRIM(RTRIM(a.[ProductCode]))
 WHERE NOT EXISTS (SELECT 1 FROM #SalesDetailCandidateProducts old WHERE old.[ProductCode]=a.[ProductCode]);
 DROP TABLE #SalesDetailWarehouseCandidateCodes;
END;
DROP TABLE #SalesDetailCandidateSupplierKeys;

{candidateRefinementSql}
DROP TABLE #SalesDetailProjectionSupplierNames;

-- 商品码用原始键回读，可使用既有 ProductCode/Date 索引；供应商命中是独立路径，避免 OR 令精确商品退化成全表扫描。
SELECT TOP(0) {sourceColumns} INTO #SalesDetailCandidateSource FROM dbo.ProductStoreDailySalesStatistic s;
-- 小候选按商品码查索引，避免长日期范围触发全段扫描；广泛搜索仍由优化器选择读取方式。
IF (SELECT COUNT_BIG(*) FROM #SalesDetailCandidateProducts)<=2048
 INSERT INTO #SalesDetailCandidateSource
 SELECT {sourceColumns} FROM #SalesDetailCandidateProducts p
 INNER LOOP JOIN dbo.ProductStoreDailySalesStatistic s ON s.[ProductCode]=p.[ProductCode]
 INNER JOIN #SalesDetailRequiredDates d ON d.[Date]=s.[Date]
 WHERE {dates}{sourceBranch};
ELSE
 INSERT INTO #SalesDetailCandidateSource
 SELECT {sourceColumns} FROM dbo.ProductStoreDailySalesStatistic s
 JOIN #SalesDetailCandidateProducts p ON p.[ProductCode]=s.[ProductCode]
 JOIN #SalesDetailRequiredDates d ON d.[Date]=s.[Date]
 WHERE {dates}{sourceBranch};
IF EXISTS (SELECT 1 FROM #SalesDetailCandidateSuppliers)
 INSERT INTO #SalesDetailCandidateSource
 SELECT {sourceColumns} FROM dbo.ProductStoreDailySalesStatistic s
 JOIN #SalesDetailCandidateSuppliers c ON c.[RawSupplierCode]=LTRIM(RTRIM(COALESCE(s.[SupplierCode],'')))
 JOIN #SalesDetailRequiredDates d ON d.[Date]=s.[Date]
 WHERE {dates}{sourceBranch}
   AND NOT EXISTS (SELECT 1 FROM #SalesDetailCandidateProducts p WHERE p.[ProductCode]=s.[ProductCode]);
""";
    }

    private static string BuildSalesDetailProjectionRefinementSql(IEnumerable<int> tokenIndexes, string? selectedProduct)
    {
        var preserveSelected = string.IsNullOrWhiteSpace(selectedProduct)
            ? string.Empty : " AND LTRIM(RTRIM(p.[ProductCode]))<>@sdrSelectedProduct";
        // 只有该词不可能命中任何供应商字段时，商品别名/当前资料才构成可安全提前执行的必要条件。
        // 每次按规范码整体保留，事实的跨日 MAX、金额汇总和最终多词 AND 均保持原样。
        return string.Join("\n", tokenIndexes.Select(i => $"""
IF NOT EXISTS
 (SELECT 1 FROM #SalesDetailProjectionSupplierNames f
  WHERE f.[RawSupplierCode] LIKE @sdrSearch{i} OR f.[SupplierCode] LIKE @sdrSearch{i} OR f.[SupplierName] LIKE @sdrSearch{i}
   OR EXISTS (SELECT 1 FROM #SalesDetailSupplierSearchMatches c WHERE c.[SupplierCode]=f.[ChinaSupplierCode] AND c.[TokenIndex]={i}))
BEGIN
 SELECT LTRIM(RTRIM(a.[ProductCode])) [ProductCode] INTO #SalesDetailTokenCandidateCodes{i}
 -- 候选已经展开全部原始码，可按词典原始键查询，再按规范码合并命中。
 FROM #SalesDetailCandidateProducts p
 INNER JOIN dbo.SalesDetailQueryProductAlias a ON a.[ProductCode]=p.[ProductCode]
 WHERE LTRIM(RTRIM(p.[ProductCode])) LIKE @sdrSearch{i} OR a.[ProductName] LIKE @sdrSearch{i} OR a.[Barcode] LIKE @sdrSearch{i}
 UNION SELECT [ProductCode] FROM #SalesDetailProductSearchMatches WHERE [TokenIndex]={i};
 DELETE p FROM #SalesDetailCandidateProducts p
 LEFT HASH JOIN #SalesDetailTokenCandidateCodes{i} possible ON possible.[ProductCode]=LTRIM(RTRIM(p.[ProductCode]))
 WHERE possible.[ProductCode] IS NULL{preserveSelected};
 DROP TABLE #SalesDetailTokenCandidateCodes{i};
END;
"""));
    }

    private static string CompressSalesDetailResultSql(string select)
        => "SELECT COMPRESS(COALESCE((" + select.Trim().TrimEnd(';')
            + " FOR JSON PATH, INCLUDE_NULL_VALUES),N'[]'));";
}
