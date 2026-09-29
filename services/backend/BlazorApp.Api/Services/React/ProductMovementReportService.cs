using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class ProductMovementReportService : IProductMovementReportService
    {
        private readonly ISqlSugarClient _db;
        private readonly ILogger<ProductMovementReportService> _logger;

        public ProductMovementReportService(
            SqlSugarContext context,
            ILogger<ProductMovementReportService> logger
        )
        {
            _db = context.Db;
            _logger = logger;
        }

        public async Task<ProductMovementReportResponseDto> GetReportAsync(
            ProductMovementReportQueryDto query,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var normalized = ProductMovementReportSqlBuilder.NormalizeQuery(query);
            var sql = ProductMovementReportSqlBuilder.Build(normalized, scopedStoreCodes);

            _logger.LogInformation(
                "查询商品经营分析报表 StoreScope={StoreScope}, Suggestion={Suggestion}, Credibility={Credibility}, Sort={SortBy} {SortDirection}, Page={Page}, PageSize={PageSize}",
                scopedStoreCodes == null ? "ALL" : string.Join(",", scopedStoreCodes),
                normalized.Suggestion,
                normalized.DataCredibility,
                normalized.SortBy ?? "default",
                normalized.SortDirection,
                normalized.Page,
                normalized.PageSize
            );

            var snapshot = await TryReadSnapshotAsync(normalized, scopedStoreCodes);
            if (snapshot != null)
            {
                return snapshot;
            }

            // 一次往返取回分页、汇总、更新时间三个结果集，中间数据只在库里物化一次。
            var (rows, summaryRows, lastUpdateRows) = await _db.Ado.SqlQueryAsync<
                ProductMovementReportSqlRow,
                ProductMovementReportSummarySqlRow,
                ProductMovementReportLastUpdateSqlRow
            >(sql.Sql, sql.Parameters.ToArray());

            return BuildResponse(normalized, rows, summaryRows, lastUpdateRows, snapshotGeneratedAtUtc: null);
        }

        /// <summary>
        /// 查询当天（悉尼业务日）且目标门店都有未过期快照时直接读快照；否则返回 null 走实时计算。
        /// 快照只是加速手段，读取出错一律回退实时计算，不能让页面因快照表问题报错。
        /// </summary>
        private async Task<ProductMovementReportResponseDto?> TryReadSnapshotAsync(
            ProductMovementReportQueryDto normalized,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var asOfDate = (normalized.AsOfDate ?? DateTime.Today).Date;
            if (asOfDate != SalesStatisticsBusinessDate.GetBusinessDate(DateTimeOffset.UtcNow))
            {
                return null;
            }

            try
            {
                var store = new ProductMovementReportSnapshotStore(_db);
                if (!store.SchemaReady)
                {
                    return null;
                }

                // 与实时查询的门店解析一致：指定门店 > 权限范围 > 全部启用门店。
                IReadOnlyList<string> targetStores = !string.IsNullOrWhiteSpace(normalized.StoreCode)
                    ? new[] { normalized.StoreCode.Trim() }
                    : scopedStoreCodes ?? await store.GetActiveStoreCodesAsync();
                if (targetStores.Count == 0)
                {
                    return null;
                }

                var latestRuns = await store.GetLatestReadyRunsAsync(asOfDate, targetStores);
                var runs = ProductMovementReportSnapshotPolicy.SelectCoveringRuns(targetStores, latestRuns, DateTime.UtcNow);
                if (runs == null)
                {
                    return null;
                }

                var sql = ProductMovementReportSqlBuilder.BuildSnapshotRead(
                    normalized,
                    runs.Select(run => run.RunId).ToList()
                );
                var (rows, summaryRows, lastUpdateRows) = await _db.Ado.SqlQueryAsync<
                    ProductMovementReportSqlRow,
                    ProductMovementReportSummarySqlRow,
                    ProductMovementReportLastUpdateSqlRow
                >(sql.Sql, sql.Parameters.ToArray());

                // 多店取最旧的一家作为整页的数据时点，界面据此提示「数据生成于」。
                var generatedAt = runs.Min(run => run.CompletedAtUtc);
                return BuildResponse(normalized, rows, summaryRows, lastUpdateRows, generatedAt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "商品经营分析快照读取失败，回退实时计算");
                return null;
            }
        }

        private static ProductMovementReportResponseDto BuildResponse(
            ProductMovementReportQueryDto normalized,
            List<ProductMovementReportSqlRow> rows,
            List<ProductMovementReportSummarySqlRow> summaryRows,
            List<ProductMovementReportLastUpdateSqlRow> lastUpdateRows,
            DateTime? snapshotGeneratedAtUtc
        )
        {
            var total = rows.FirstOrDefault()?.TotalCount ?? 0;
            return new ProductMovementReportResponseDto
            {
                Items = rows.Cast<ProductMovementReportRowDto>().ToList(),
                Total = total,
                Page = normalized.Page,
                PageSize = normalized.PageSize,
                SuggestionSummary = summaryRows
                    .Where(item => item.SummaryType == "Suggestion")
                    .Select(item => new ProductMovementReportSummaryDto
                    {
                        Key = item.Key,
                        Count = item.Count,
                    })
                    .ToList(),
                CredibilitySummary = summaryRows
                    .Where(item => item.SummaryType == "Credibility")
                    .Select(item => new ProductMovementReportSummaryDto
                    {
                        Key = item.Key,
                        Count = item.Count,
                    })
                    .ToList(),
                SalesStatisticLastUpdate =
                    rows.FirstOrDefault()?.SalesStatisticLastUpdate
                    ?? lastUpdateRows.FirstOrDefault()?.SalesStatisticLastUpdate,
                // 库里存的是 UTC（datetime2 读出为 Unspecified），显式标记后序列化才会带 Z。
                SnapshotGeneratedAtUtc = snapshotGeneratedAtUtc.HasValue
                    ? DateTime.SpecifyKind(snapshotGeneratedAtUtc.Value, DateTimeKind.Utc)
                    : null,
            };
        }

        public async Task<List<ProductMovementReportStoreOptionDto>> GetStoreOptionsAsync(
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var parameters = new List<SugarParameter>();
            var storeParameterNames = ProductMovementReportSqlBuilder.AddStoreParametersForStoreOptions(
                parameters,
                scopedStoreCodes
            );
            var storeFilter = ProductMovementReportSqlBuilder.BuildStoreFilterForStoreOptions(
                "s.StoreCode",
                storeParameterNames
            );

            // 使用报表自己的门店选项接口，避免店长/仓库经理必须额外拥有 Stores.View 权限。
            var sql =
                "SELECT\n"
                + "    COALESCE(NULLIF(s.StoreName, N''), s.StoreCode) AS Label,\n"
                + "    s.StoreCode AS Value,\n"
                // 移动营业额报表按门店时区对齐截止整点，空串视为未维护。
                + "    NULLIF(s.TimeZoneId, N'') AS TimeZoneId\n"
                + "FROM [Store] s\n"
                + "WHERE\n"
                + "    COALESCE(s.IsActive, 0) = 1\n"
                + "    AND COALESCE(s.IsDeleted, 0) = 0\n"
                + "    AND NULLIF(s.StoreCode, N'') IS NOT NULL\n"
                + storeFilter
                + "\nORDER BY\n"
                + "    s.StoreCode";

            return await _db.Ado.SqlQueryAsync<ProductMovementReportStoreOptionDto>(
                sql,
                parameters.ToArray()
            );
        }

        private sealed class ProductMovementReportSqlRow : ProductMovementReportRowDto
        {
            public int TotalCount { get; set; }
        }

        private sealed class ProductMovementReportSummarySqlRow
        {
            public string SummaryType { get; set; } = string.Empty;
            public string Key { get; set; } = string.Empty;
            public int Count { get; set; }
        }

        private sealed class ProductMovementReportLastUpdateSqlRow
        {
            public DateTime? SalesStatisticLastUpdate { get; set; }
        }
    }

    public static class ProductMovementReportSqlBuilder
    {
        private const int FastSalesDays = 30;
        private const int StableSalesDays = 90;
        private const int PurchaseDays = 180;
        private const int LowCoverDays = 14;
        private const int ClearanceNoSaleDays = 45;
        private const int StockUpCoverDays = 30;
        private const decimal StockUpGrossMarginRate = 0.3500m;
        private const decimal LowGrossMarginRate = 0.1500m;
        private const decimal GrowthRateForStockUp = 0.2000m;

        /// <summary>唯一允许的排序字段：近 30 天销量。</summary>
        public const string SalesQty30SortKey = "salesQty30";

        public static ProductMovementReportQueryDto NormalizeQuery(ProductMovementReportQueryDto query)
        {
            // 排序走白名单：拼进 ORDER BY 的只有代码里的常量，请求里的原值不会进入 SQL。
            var sortBy = string.Equals(NormalizeText(query.SortBy), SalesQty30SortKey, StringComparison.OrdinalIgnoreCase)
                ? SalesQty30SortKey
                : null;

            return new ProductMovementReportQueryDto
            {
                StoreCode = NormalizeText(query.StoreCode),
                AsOfDate = (query.AsOfDate ?? DateTime.Today).Date,
                Suggestion = NormalizeText(query.Suggestion),
                DataCredibility = NormalizeText(query.DataCredibility),
                Keyword = NormalizeText(query.Keyword),
                Page = query.Page <= 0 ? 1 : query.Page,
                PageSize = Math.Clamp(query.PageSize <= 0 ? 50 : query.PageSize, 1, 200),
                SortBy = sortBy,
                // 销量排序默认从高到低，只有明确传 asc 才反过来。
                SortDirection = sortBy == null
                    ? null
                    : string.Equals(NormalizeText(query.SortDirection), "asc", StringComparison.OrdinalIgnoreCase)
                        ? "asc"
                        : "desc",
            };
        }

        /// <summary>快照规则版本：分类规则或口径 SQL 变更时递增，旧版本快照随即失效并由后台重建。</summary>
        public const int SnapshotRuleVersion = 1;

        public static ProductMovementReportSqlBuildResult Build(
            ProductMovementReportQueryDto query,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var parameters = BuildRuleParameters((query.AsOfDate ?? DateTime.Today).Date);
            AddPageAndFilterParameters(parameters, query);
            var storeCodes = ResolveStoreCodes(query.StoreCode, scopedStoreCodes);
            var storeParameterNames = AddStoreParameters(parameters, storeCodes);

            return new ProductMovementReportSqlBuildResult
            {
                Parameters = parameters,
                // 可信度与关键词同时约束明细和汇总，物化进 #FinalRows 前就过滤掉。
                Sql = BuildLiveMaterializeSql(storeParameterNames, BuildScopeWhere(query), BuildKeywordProductsSql(query))
                    + BuildResultSetsSql(query),
            };
        }

        /// <summary>
        /// 为单个门店生成快照：与实时查询同一套物化 SQL（不带可信度/关键词筛选），结果整批写入快照表，
        /// 并在同一事务里把批次标为 Ready。含除法的三列按实时查询输出时的 CAST 精度落库。
        /// </summary>
        public static ProductMovementReportSqlBuildResult BuildSnapshotWrite(
            DateTime asOfDate,
            string storeCode,
            Guid runId
        )
        {
            var parameters = BuildRuleParameters(asOfDate.Date);
            parameters.Add(new SugarParameter("@RunId", runId));
            var storeParameterNames = AddStoreParameters(parameters, new[] { storeCode });

            return new ProductMovementReportSqlBuildResult
            {
                Parameters = parameters,
                Sql = "SET XACT_ABORT ON;\n"
                    + BuildLiveMaterializeSql(storeParameterNames, "\n", keywordProductsSql: string.Empty)
                    + """

BEGIN TRANSACTION;

INSERT INTO dbo.ProductMovementReportSnapshot (
    RunId, BranchCode, StoreName, ProductCode, ProductName, Barcode, ImageUrl,
    SalesQty30, SalesQty90, SalesQty180, DailySalesQty30, SalesAmount90Aud, GrossProfit90Aud, GrossMarginRate90,
    LastSaleDate, NoSaleDays, PurchaseQty180, EstimatedRemainingQty, EstimatedCoverDays,
    DataCredibility, DataExceptionFlag, SystemSuggestion, StoreManagerAction, ActionPriority, RowSalesStatLastUpdate
)
SELECT
    @RunId, BranchCode, StoreName, ProductCode, ProductName, Barcode, ImageUrl,
    SalesQty30, SalesQty90, SalesQty180,
    CAST(DailySalesQty30 AS decimal(18, 2)), SalesAmount90Aud, GrossProfit90Aud, CAST(GrossMarginRate90 AS decimal(18, 4)),
    LastSaleDate, NoSaleDays, PurchaseQty180, EstimatedRemainingQty, CAST(EstimatedCoverDays AS decimal(18, 2)),
    DataCredibility, DataExceptionFlag, SystemSuggestion, StoreManagerAction, ActionPriority, RowSalesStatLastUpdate
FROM #FinalRows;

DECLARE @Inserted int = @@ROWCOUNT;

UPDATE dbo.ProductMovementReportSnapshotRun
SET [Status] = N'Ready',
    [RowCount] = @Inserted,
    SalesStatLastUpdate = @SalesStatLastUpdate,
    CompletedAtUtc = SYSUTCDATETIME()
WHERE RunId = @RunId AND [Status] = N'Building';

-- 批次已被清理或改为失败时放弃发布，已写入的快照行随事务回滚。
IF @@ROWCOUNT <> 1
    THROW 51010, N'快照批次状态已变更，放弃发布。', 1;

COMMIT;
""",
            };
        }

        /// <summary>
        /// 从已就绪的快照批次读取：按可信度/关键词过滤后，复用实时查询的三段结果集 SQL，
        /// 保证两条路径输出的列、排序和汇总口径完全一致。
        /// </summary>
        public static ProductMovementReportSqlBuildResult BuildSnapshotRead(
            ProductMovementReportQueryDto query,
            IReadOnlyList<Guid> runIds
        )
        {
            if (runIds.Count == 0)
            {
                throw new ArgumentException("快照读取至少需要一个批次。", nameof(runIds));
            }

            var parameters = new List<SugarParameter>();
            AddPageAndFilterParameters(parameters, query);
            var runParameterNames = new List<string>();
            for (var i = 0; i < runIds.Count; i++)
            {
                var name = "@Run" + i;
                runParameterNames.Add(name);
                parameters.Add(new SugarParameter(name, runIds[i]));
            }

            var runFilter = "RunId IN (" + string.Join(", ", runParameterNames) + ")";
            var scopeClauses = BuildScopeClauses(query);
            var scopeFilter = scopeClauses.Count == 0
                ? string.Empty
                : "\n        AND " + string.Join("\n        AND ", scopeClauses);

            // 快照本身已是物化结果，按批次号走主键查找直接读；复制进临时表反而要把全部分店约 30 万行
            // 宽行写一遍 tempdb（本机 38 万行实测占整次读取 2.2 秒中的 1.65 秒）。
            // 例外是关键词：库排序规则下的前导通配 LIKE 很贵，分页与两段汇总会各做一遍（生产全部分店
            // 「card」7–8 秒）；命中行通常只占少数，先物化一次再复用，降到 1.6–2.2 秒且结果不变。
            // 货号不在快照里，关键词先在商品档案里按货号取出命中商品（#KeywordProducts），再参与上面的过滤。
            var source = $$"""
(
    SELECT
        BranchCode, StoreName, ProductCode, ProductName, Barcode, ImageUrl,
        SalesQty30, SalesQty90, SalesQty180, DailySalesQty30, SalesAmount90Aud, GrossProfit90Aud, GrossMarginRate90,
        LastSaleDate, NoSaleDays, PurchaseQty180, EstimatedRemainingQty, EstimatedCoverDays,
        DataCredibility, DataExceptionFlag, SystemSuggestion, StoreManagerAction, ActionPriority,
        -- 与实时查询同口径：无销售的行用本次查询范围内的最大统计时间补齐。
        COALESCE(RowSalesStatLastUpdate, @SalesStatLastUpdate) AS SalesStatLastUpdate
    FROM dbo.ProductMovementReportSnapshot
    WHERE {{runFilter}}{{scopeFilter}}
) FinalRows
""";

            return new ProductMovementReportSqlBuildResult
            {
                Parameters = parameters,
                Sql = $$"""
SET NOCOUNT ON;

DECLARE @SalesStatLastUpdate datetime = (
    SELECT MAX(SalesStatLastUpdate) FROM dbo.ProductMovementReportSnapshotRun WHERE {{runFilter}}
);
"""
                    + (string.IsNullOrWhiteSpace(query.Keyword)
                        ? BuildResultSetsSql(query, source)
                        : BuildKeywordProductsSql(query)
                            + "\nSELECT * INTO #FinalRows FROM " + source + ";\n"
                            + BuildResultSetsSql(query)),
            };
        }

        private static List<SugarParameter> BuildRuleParameters(DateTime asOfDate)
        {
            return new List<SugarParameter>
            {
                new("@AsOfDate", asOfDate),
                new("@FastSalesDays", FastSalesDays),
                new("@StableSalesDays", StableSalesDays),
                new("@PurchaseDays", PurchaseDays),
                new("@LowCoverDays", LowCoverDays),
                new("@ClearanceNoSaleDays", ClearanceNoSaleDays),
                new("@StockUpCoverDays", StockUpCoverDays),
                new("@StockUpGrossMarginRate", StockUpGrossMarginRate),
                new("@LowGrossMarginRate", LowGrossMarginRate),
                new("@GrowthRateForStockUp", GrowthRateForStockUp),
                new("@FastStartDate", asOfDate.AddDays(-FastSalesDays + 1)),
                new("@StableStartDate", asOfDate.AddDays(-StableSalesDays + 1)),
                new("@PurchaseStartDate", asOfDate.AddDays(-PurchaseDays + 1)),
                new("@NextDate", asOfDate.AddDays(1)),
                new("@PreviousSalesDays", StableSalesDays - FastSalesDays),
            };
        }

        private static void AddPageAndFilterParameters(List<SugarParameter> parameters, ProductMovementReportQueryDto query)
        {
            parameters.Add(new SugarParameter("@Offset", (query.Page - 1) * query.PageSize));
            parameters.Add(new SugarParameter("@PageSize", query.PageSize));
            if (!string.IsNullOrWhiteSpace(query.Suggestion))
            {
                parameters.Add(new SugarParameter("@Suggestion", query.Suggestion));
            }
            if (!string.IsNullOrWhiteSpace(query.DataCredibility))
            {
                parameters.Add(new SugarParameter("@DataCredibility", query.DataCredibility));
            }
            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                parameters.Add(new SugarParameter("@Keyword", "%" + EscapeLikeValue(query.Keyword) + "%"));
            }
        }

        private static string BuildLiveMaterializeSql(
            IReadOnlyList<string> storeParameterNames,
            string scopeWhere,
            string keywordProductsSql
        )
        {
            return BuildMaterializeSql(
                BuildSalesSource(storeParameterNames),
                BuildStoreFilter("s.BranchCode", storeParameterNames),
                // 进货明细的门店与发票门店一致（2026-09-19 核对 65 万行无差异），按发票门店过滤才能走
                // IX_LSPSA_Invoice_EffectiveDate_Store_Invoice；三路 COALESCE 表达式只能全表扫描。
                BuildStoreFilter("i.StoreCode", storeParameterNames),
                BuildStoreFilter("u.BranchCode", storeParameterNames),
                scopeWhere,
                keywordProductsSql
            );
        }

        /// <summary>
        /// 货号（ItemNumber）只在商品档案里维护，与 ProductCode 从不相同（2026-09-19 生产 17.3 万商品核对），
        /// 快照和日统计里都没有这一列。有关键词时先按货号取出命中商品编码，再在范围过滤里与编码/名称/条码并列匹配。
        /// 必须先物化：写成 IN 子查询时优化器会逐行回查商品表，生产单店从 0.3 秒涨到 1.4 秒；物化后只多约 0.2 秒。
        /// </summary>
        private static string BuildKeywordProductsSql(ProductMovementReportQueryDto query)
        {
            if (string.IsNullOrWhiteSpace(query.Keyword))
            {
                return string.Empty;
            }

            // IsDeleted = 0 写成字面量，才能命中 WHERE IsDeleted = 0 的货号过滤索引。
            return """

SELECT DISTINCT p.ProductCode
INTO #KeywordProducts
FROM [Product] p
WHERE
    p.IsDeleted = 0
    AND p.ItemNumber LIKE @Keyword;

""";
        }

        /// <summary>
        /// 分页排序。默认按建议紧急程度；按销量排序时跨门店统一比较，并列时依次看 90 天销量、90 天销售额，
        /// 最后用门店与商品编码兜底，保证翻页时次序确定、不重不漏。
        /// </summary>
        private static string BuildPageOrderBy(ProductMovementReportQueryDto query, string prefix)
        {
            string[] keys = query.SortBy != SalesQty30SortKey
                ? new[] { "ActionPriority", "BranchCode", "SalesQty30 DESC", "SalesAmount90Aud DESC", "ProductCode" }
                : query.SortDirection == "asc"
                    ? new[] { "SalesQty30", "SalesQty90", "SalesAmount90Aud", "BranchCode", "ProductCode" }
                    : new[] { "SalesQty30 DESC", "SalesQty90 DESC", "SalesAmount90Aud DESC", "BranchCode", "ProductCode" };

            return string.Join(",\n    ", keys.Select(key => prefix + key));
        }

        /// <summary>
        /// 三个结果集：分页明细、汇总、最后更新时间。实时查询与快照读取共用同一段 SQL，
        /// 只换数据来源：实时查询读 #FinalRows，快照读取直接读快照表子查询。
        /// </summary>
        private static string BuildResultSetsSql(ProductMovementReportQueryDto query, string source = "#FinalRows")
        {
            // 系统建议只约束明细：前台用各建议的计数卡片当筛选入口，
            // 选中其中一类时，其余卡片仍要显示同一门店/关键词范围内的真实计数。
            var suggestionWhere = BuildSuggestionWhere(query);

            // 先按排序取出当前页，再只为这一页（最多 200 行）按商品编码查货号：放在分页之后，
            // 不必为全部分店约 30 万行关联商品表，生产实测分页耗时不变。外层重复同一排序，保证输出次序。
            return $$"""

-- 结果集 1：分页明细
SELECT
    pg.BranchCode AS StoreCode,
    pg.StoreName AS StoreName,
    pg.ProductCode AS ProductCode,
    im.ItemNumber AS ItemNumber,
    pg.ProductName AS ProductName,
    pg.Barcode AS Barcode,
    pg.ImageUrl AS ImageUrl,
    pg.SalesQty30 AS SalesQty30,
    pg.SalesQty90 AS SalesQty90,
    CAST(pg.DailySalesQty30 AS decimal(18, 2)) AS DailySalesQty30,
    CAST(pg.SalesAmount90Aud AS decimal(18, 2)) AS SalesAmount90Aud,
    CAST(pg.GrossProfit90Aud AS decimal(18, 2)) AS GrossProfit90Aud,
    CAST(pg.GrossMarginRate90 AS decimal(18, 4)) AS GrossMarginRate90,
    pg.LastSaleDate AS LastSaleDate,
    pg.NoSaleDays AS NoSaleDays,
    CAST(pg.PurchaseQty180 AS decimal(18, 2)) AS PurchaseQty180,
    pg.SalesQty180 AS SalesQty180,
    CAST(pg.EstimatedRemainingQty AS decimal(18, 2)) AS EstimatedRemainingQty,
    CAST(pg.EstimatedCoverDays AS decimal(18, 2)) AS EstimatedCoverDays,
    pg.DataCredibility AS DataCredibility,
    pg.DataExceptionFlag AS DataExceptionFlag,
    pg.SystemSuggestion AS SystemSuggestion,
    pg.StoreManagerAction AS StoreManagerAction,
    pg.SalesStatLastUpdate AS SalesStatisticLastUpdate,
    pg.TotalCount AS TotalCount
FROM (
    SELECT
        BranchCode, StoreName, ProductCode, ProductName, Barcode, ImageUrl,
        SalesQty30, SalesQty90, DailySalesQty30, SalesAmount90Aud, GrossProfit90Aud, GrossMarginRate90,
        LastSaleDate, NoSaleDays, PurchaseQty180, SalesQty180, EstimatedRemainingQty, EstimatedCoverDays,
        DataCredibility, DataExceptionFlag, SystemSuggestion, StoreManagerAction, ActionPriority, SalesStatLastUpdate,
        COUNT(1) OVER() AS TotalCount
    FROM {{source}}{{suggestionWhere}}
    ORDER BY
    {{BuildPageOrderBy(query, string.Empty)}}
    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
) pg
OUTER APPLY (
    SELECT MAX(NULLIF(p.ItemNumber, N'')) AS ItemNumber
    FROM [Product] p
    WHERE
        p.IsDeleted = 0
        AND p.ProductCode = pg.ProductCode
) im
ORDER BY
    {{BuildPageOrderBy(query, "pg.")}};

-- 结果集 2：建议与可信度汇总（不受系统建议筛选影响）
SELECT N'Suggestion' AS SummaryType, SystemSuggestion AS [Key], COUNT(1) AS [Count]
FROM {{source}}
GROUP BY SystemSuggestion
UNION ALL
SELECT N'Credibility' AS SummaryType, DataCredibility AS [Key], COUNT(1) AS [Count]
FROM {{source}}
GROUP BY DataCredibility;

-- 结果集 3：销售统计最后更新时间
SELECT @SalesStatLastUpdate AS SalesStatisticLastUpdate;
""";
        }

        public static bool ContainsWriteKeyword(string sql)
        {
            var upper = sql.ToUpperInvariant();
            var unsafeWords = new[] { " INSERT ", " UPDATE ", " DELETE ", " MERGE ", " CREATE ", " ALTER ", " DROP ", " TRUNCATE ", " EXEC " };
            return unsafeWords.Any(upper.Contains);
        }

        /// <summary>
        /// 一个批次内把日统计与进货各物化一次：原先的 CTE 在 ProductUniverse 与 Metrics 里被引用两次，
        /// SQL Server 会各算一遍，且分页、汇总、更新时间是三次独立往返，同一份数据共要算五遍。
        /// 临时表随 sp_executesql 结束自动释放，不需要（也不能，否则只读校验会拦截）显式 DROP。
        /// </summary>
        private static string BuildMaterializeSql(
            string salesSource,
            string salesStoreFilter,
            string purchaseStoreFilter,
            string metricsStoreFilter,
            string scopeWhere,
            string keywordProductsSql
        )
        {
            return $$"""
SET NOCOUNT ON;
{{keywordProductsSql}}
SELECT
    s.BranchCode,
    s.ProductCode,
    MAX(NULLIF(s.ProductName, N'')) AS ProductName,
    MAX(NULLIF(s.Barcode, N'')) AS Barcode,
    SUM(CASE WHEN s.Date >= @FastStartDate THEN s.TotalQuantity ELSE 0 END) AS SalesQty30,
    SUM(CASE WHEN s.Date >= @StableStartDate THEN s.TotalQuantity ELSE 0 END) AS SalesQty90,
    SUM(s.TotalQuantity) AS SalesQty180,
    SUM(CASE WHEN s.Date >= @StableStartDate THEN s.TotalAmount ELSE 0 END) AS SalesAmount90Aud,
    SUM(CASE WHEN s.Date >= @StableStartDate THEN COALESCE(s.GrossProfit, 0) ELSE 0 END) AS GrossProfit90Aud,
    SUM(CASE WHEN s.Date >= @StableStartDate THEN COALESCE(s.TotalCost, 0) ELSE 0 END) AS TotalCost90Aud,
    SUM(CASE WHEN s.Date >= @StableStartDate AND s.TotalCost IS NULL THEN 1 ELSE 0 END) AS MissingCostRows90,
    SUM(CASE WHEN s.Date >= @StableStartDate AND s.GrossProfit IS NULL THEN 1 ELSE 0 END) AS MissingGrossProfitRows90,
    SUM(CASE WHEN s.Date >= @StableStartDate AND s.Date < @FastStartDate THEN s.TotalQuantity ELSE 0 END) AS PreviousSalesQty,
    MAX(CASE WHEN s.TotalQuantity > 0 THEN s.Date END) AS LastSaleDate,
    MAX(s.UpdateTime) AS RowSalesStatLastUpdate
INTO #SalesBase
{{salesSource}}
WHERE
    s.Date >= @PurchaseStartDate
    AND s.Date < @NextDate
    {{salesStoreFilter}}
    AND NULLIF(s.BranchCode, N'') IS NOT NULL
    AND NULLIF(s.ProductCode, N'') IS NOT NULL
GROUP BY
    s.BranchCode,
    s.ProductCode;

DECLARE @SalesStatLastUpdate datetime = (SELECT MAX(RowSalesStatLastUpdate) FROM #SalesBase);

SELECT
    i.StoreCode AS BranchCode,
    COALESCE(NULLIF(d.ProductCode, N''), NULLIF(srp.ProductCode, N'')) AS ProductCode,
    MAX(NULLIF(d.ProductName, N'')) AS ProductName,
    MAX(NULLIF(d.Barcode, N'')) AS Barcode,
    SUM(COALESCE(d.Quantity, 0)) AS PurchaseQty180,
    SUM(CASE WHEN COALESCE(i.InboundStatus, -1) <> 2 OR i.InboundDate IS NULL THEN 1 ELSE 0 END) AS UnconfirmedPurchaseRows180,
    MAX(COALESCE(i.InboundDate, i.OrderDate)) AS LastPurchaseDate
INTO #PurchaseBase
FROM [StoreLocalSupplierInvoice] i
INNER JOIN [StoreLocalSupplierInvoiceDetails] d
    ON d.InvoiceGUID = i.InvoiceGUID
    AND d.IsDeleted = 0
-- 只有明细缺商品编码时才回查零售价补编码，按主键逐行查找，不再整表关联 470 万行。
LEFT JOIN [StoreRetailPrice] srp
    ON NULLIF(d.ProductCode, N'') IS NULL
    AND srp.UUID = d.StoreProductCode
    AND srp.IsDeleted = 0
WHERE
    i.IsDeleted = 0
    -- 进货数据按进货单口径统计；用户已要求不限制为已入库单据。
    -- EffectivePurchaseDate 是持久化列 CONVERT(date, COALESCE(InboundDate, OrderDate, CreatedAt))，可走过滤索引；
    -- 再补一句 COALESCE(i.InboundDate, i.OrderDate) 非空，保持原口径不把只有创建时间的单据算进来。
    AND i.EffectivePurchaseDate >= CAST(@PurchaseStartDate AS date)
    AND i.EffectivePurchaseDate < CAST(@NextDate AS date)
    AND COALESCE(i.InboundDate, i.OrderDate) IS NOT NULL
    {{purchaseStoreFilter}}
    AND NULLIF(i.StoreCode, N'') IS NOT NULL
    AND COALESCE(NULLIF(d.ProductCode, N''), NULLIF(srp.ProductCode, N'')) IS NOT NULL
GROUP BY
    i.StoreCode,
    COALESCE(NULLIF(d.ProductCode, N''), NULLIF(srp.ProductCode, N''));

WITH ProductUniverse AS (
    SELECT BranchCode, ProductCode FROM #SalesBase
    UNION
    SELECT BranchCode, ProductCode FROM #PurchaseBase
),
ProductMaster AS (
    -- 只取本次涉及的商品，IsDeleted = 0 字面量才能命中覆盖名称、条码和主图的 IX_Product_ProductCode_Active。
    SELECT
        p.ProductCode,
        MAX(NULLIF(p.ProductName, N'')) AS ProductName,
        MAX(NULLIF(p.Barcode, N'')) AS Barcode,
        -- 商品主图只在商品档案里维护，销售统计和进货明细都没有这一列。
        MAX(NULLIF(p.ProductImage, N'')) AS ImageUrl
    FROM [Product] p
    WHERE
        p.IsDeleted = 0
        AND p.ProductCode IN (SELECT ProductCode FROM ProductUniverse)
    GROUP BY p.ProductCode
),
Metrics AS (
    SELECT
        u.BranchCode,
        st.StoreName,
        u.ProductCode,
        COALESCE(sb.ProductName, pb.ProductName, pm.ProductName) AS ProductName,
        COALESCE(sb.Barcode, pb.Barcode, pm.Barcode) AS Barcode,
        pm.ImageUrl AS ImageUrl,
        COALESCE(sb.SalesQty30, 0) AS SalesQty30,
        COALESCE(sb.SalesQty90, 0) AS SalesQty90,
        CAST(COALESCE(sb.SalesQty30, 0) AS decimal(18, 4)) / NULLIF(@FastSalesDays, 0) AS DailySalesQty30,
        COALESCE(sb.SalesAmount90Aud, 0) AS SalesAmount90Aud,
        CASE
            WHEN COALESCE(sb.SalesQty90, 0) > 0
                AND (COALESCE(sb.MissingCostRows90, 0) > 0 OR COALESCE(sb.MissingGrossProfitRows90, 0) > 0)
                THEN NULL
            ELSE sb.GrossProfit90Aud
        END AS GrossProfit90Aud,
        CASE
            WHEN COALESCE(sb.SalesAmount90Aud, 0) > 0
                AND NOT (COALESCE(sb.MissingCostRows90, 0) > 0 OR COALESCE(sb.MissingGrossProfitRows90, 0) > 0)
                THEN sb.GrossProfit90Aud / NULLIF(sb.SalesAmount90Aud, 0)
            ELSE NULL
        END AS GrossMarginRate90,
        sb.LastSaleDate,
        CASE
            WHEN sb.LastSaleDate IS NULL THEN NULL
            ELSE DATEDIFF(day, sb.LastSaleDate, @AsOfDate)
        END AS NoSaleDays,
        COALESCE(pb.PurchaseQty180, 0) AS PurchaseQty180,
        COALESCE(sb.SalesQty180, 0) AS SalesQty180,
        COALESCE(pb.PurchaseQty180, 0) - COALESCE(sb.SalesQty180, 0) AS EstimatedRemainingQty,
        CASE
            WHEN COALESCE(sb.SalesQty30, 0) <= 0 THEN NULL
            ELSE (COALESCE(pb.PurchaseQty180, 0) - COALESCE(sb.SalesQty180, 0))
                / NULLIF(CAST(COALESCE(sb.SalesQty30, 0) AS decimal(18, 4)) / NULLIF(@FastSalesDays, 0), 0)
        END AS EstimatedCoverDays,
        CASE
            WHEN @PreviousSalesDays <= 0 THEN NULL
            ELSE CAST(COALESCE(sb.PreviousSalesQty, 0) AS decimal(18, 4)) / @PreviousSalesDays
        END AS PreviousDailySalesQty,
        COALESCE(sb.MissingCostRows90, 0) + COALESCE(sb.MissingGrossProfitRows90, 0) AS MissingCostOrProfitRows90,
        COALESCE(pb.UnconfirmedPurchaseRows180, 0) AS UnconfirmedPurchaseRows180,
        pb.LastPurchaseDate,
        COALESCE(sb.RowSalesStatLastUpdate, @SalesStatLastUpdate) AS SalesStatLastUpdate,
        sb.RowSalesStatLastUpdate AS RowSalesStatLastUpdate
    FROM ProductUniverse u
    LEFT JOIN #SalesBase sb
        ON sb.BranchCode = u.BranchCode
        AND sb.ProductCode = u.ProductCode
    LEFT JOIN #PurchaseBase pb
        ON pb.BranchCode = u.BranchCode
        AND pb.ProductCode = u.ProductCode
    LEFT JOIN ProductMaster pm
        ON pm.ProductCode = u.ProductCode
    LEFT JOIN [Store] st
        ON st.StoreCode = u.BranchCode
        AND COALESCE(st.IsDeleted, 0) = 0
    WHERE
        1 = 1
        {{metricsStoreFilter}}
),
Ranked AS (
    SELECT
        m.*,
        CASE
            -- 近 30 天销量大量并列（如都卖 1 件）时四分位边界会落在并列组中间；补商品编码作次序，
            -- 否则执行计划一变，边界上的商品就会在「好卖/需要订货」与「正常」之间来回翻转。
            WHEN m.SalesQty30 > 0 THEN NTILE(4) OVER (PARTITION BY m.BranchCode ORDER BY m.SalesQty30 DESC, m.ProductCode)
            ELSE 4
        END AS FastSalesQuartile
    FROM Metrics m
),
Classified AS (
    SELECT
        r.*,
        CASE
            WHEN r.PurchaseQty180 = 0 AND r.SalesQty180 > 0 THEN N'低'
            WHEN r.EstimatedRemainingQty < 0 THEN N'低'
            WHEN r.ProductName IS NULL OR r.Barcode IS NULL THEN N'低'
            WHEN r.UnconfirmedPurchaseRows180 > 0 THEN N'中'
            WHEN r.SalesQty180 > 0 AND r.MissingCostOrProfitRows90 > 0 THEN N'中'
            WHEN r.PurchaseQty180 > 0 AND r.SalesQty180 > 0 AND r.EstimatedRemainingQty >= 0 THEN N'高'
            ELSE N'中'
        END AS DataCredibility,
        COALESCE(
            NULLIF(
                CONCAT_WS(
                    N'；',
                    CASE WHEN r.PurchaseQty180 = 0 AND r.SalesQty180 > 0 THEN N'无进货有销售，需核对期初库存/进货记录/调拨' END,
                    CASE WHEN r.EstimatedRemainingQty < 0 THEN N'销售大于进货，需核对期初库存/进货记录/调拨' END,
                    CASE WHEN r.SalesQty180 = 0 AND r.PurchaseQty180 > 0 THEN N'有进货无销售，需检查陈列/价格/库存' END,
                    CASE WHEN r.UnconfirmedPurchaseRows180 > 0 THEN N'包含未入库或未确认进货单，需核对是否实际到货' END,
                    CASE WHEN r.SalesQty90 > 0 AND r.MissingCostOrProfitRows90 > 0 THEN N'成本或毛利缺失，毛利判断不完整' END,
                    CASE WHEN r.ProductName IS NULL THEN N'商品名称缺失' END,
                    CASE WHEN r.Barcode IS NULL THEN N'条码缺失' END
                ),
                N''
            ),
            N'正常'
        ) AS DataExceptionFlag
    FROM Ranked r
),
    FinalRows AS (
        SELECT
            c.*,
            CASE
                WHEN c.DataCredibility = N'低' THEN N'观察'
                WHEN c.SalesQty90 > 0 AND c.MissingCostOrProfitRows90 > 0 THEN N'观察'
                WHEN c.SalesQty90 > 0 AND c.GrossMarginRate90 IS NOT NULL AND c.GrossMarginRate90 < @LowGrossMarginRate THEN N'观察'
                WHEN c.EstimatedRemainingQty > 0
                    AND DATEDIFF(
                        day,
                        CASE
                            WHEN c.LastSaleDate IS NULL THEN c.LastPurchaseDate
                            WHEN c.LastPurchaseDate IS NULL THEN c.LastSaleDate
                            WHEN c.LastPurchaseDate > c.LastSaleDate THEN c.LastPurchaseDate
                            ELSE c.LastSaleDate
                        END,
                        @AsOfDate
                    ) >= @ClearanceNoSaleDays
                    THEN N'需要清仓'
                WHEN c.FastSalesQuartile = 1 AND c.SalesQty30 > 0 AND c.EstimatedRemainingQty <= 0 THEN N'需要订货'
                WHEN c.FastSalesQuartile = 1 AND c.SalesQty30 > 0 AND c.EstimatedCoverDays <= @LowCoverDays THEN N'需要备货'
                WHEN c.FastSalesQuartile = 1
                    AND c.SalesQty30 > 0
                AND c.GrossMarginRate90 >= @StockUpGrossMarginRate
                AND c.EstimatedCoverDays <= @StockUpCoverDays
                AND c.PreviousDailySalesQty > 0
                    AND c.DailySalesQty30 >= c.PreviousDailySalesQty * (1 + @GrowthRateForStockUp)
                    THEN N'值得囤货'
                WHEN c.FastSalesQuartile = 1 AND c.SalesQty90 > 0 THEN N'好卖'
                ELSE N'正常'
            END AS SystemSuggestion,
            CASE
                WHEN c.DataCredibility = N'低'
                    THEN N'数据或毛利异常，请先核对商品、成本、进货记录。'
                WHEN c.SalesQty90 > 0 AND (c.GrossMarginRate90 < @LowGrossMarginRate OR c.MissingCostOrProfitRows90 > 0)
                    THEN N'数据或毛利异常，请先核对商品、成本、进货记录。'
                WHEN c.EstimatedRemainingQty > 0
                    AND DATEDIFF(
                        day,
                        CASE
                            WHEN c.LastSaleDate IS NULL THEN c.LastPurchaseDate
                            WHEN c.LastPurchaseDate IS NULL THEN c.LastSaleDate
                            WHEN c.LastPurchaseDate > c.LastSaleDate THEN c.LastPurchaseDate
                            ELSE c.LastSaleDate
                        END,
                        @AsOfDate
                    ) >= @ClearanceNoSaleDays
                    THEN N'长期不动销，请检查陈列、价格和库存，考虑 markdown / clearance。'
                WHEN c.FastSalesQuartile = 1 AND c.SalesQty30 > 0 AND c.EstimatedRemainingQty <= 0
                    THEN N'估算剩余量不足，请核对货架、后仓和进货单到货情况；不足再向总部/供应商补进。'
            WHEN c.FastSalesQuartile = 1 AND c.SalesQty30 > 0 AND c.EstimatedCoverDays <= @LowCoverDays
                THEN N'请检查货架和后仓；有货先上架，无货再订货。'
            WHEN c.FastSalesQuartile = 1
                AND c.SalesQty30 > 0
                AND c.GrossMarginRate90 >= @StockUpGrossMarginRate
                AND c.EstimatedCoverDays <= @StockUpCoverDays
                AND c.PreviousDailySalesQty > 0
                    AND c.DailySalesQty30 >= c.PreviousDailySalesQty * (1 + @GrowthRateForStockUp)
                    THEN N'热销且毛利较好，建议保持安全库存。'
                WHEN c.FastSalesQuartile = 1 AND c.SalesQty90 > 0
                    THEN N'商品动销较好，请保持关注，避免断货。'
                ELSE N'暂无特殊动作，按正常陈列和订货节奏处理。'
            END AS StoreManagerAction,
            CASE
                WHEN c.DataCredibility = N'低' THEN 6
                WHEN c.SalesQty90 > 0 AND (c.GrossMarginRate90 < @LowGrossMarginRate OR c.MissingCostOrProfitRows90 > 0) THEN 6
                WHEN c.EstimatedRemainingQty > 0
                    AND DATEDIFF(
                        day,
                        CASE
                            WHEN c.LastSaleDate IS NULL THEN c.LastPurchaseDate
                            WHEN c.LastPurchaseDate IS NULL THEN c.LastSaleDate
                            WHEN c.LastPurchaseDate > c.LastSaleDate THEN c.LastPurchaseDate
                            ELSE c.LastSaleDate
                        END,
                        @AsOfDate
                    ) >= @ClearanceNoSaleDays
                    THEN 4
                WHEN c.FastSalesQuartile = 1 AND c.SalesQty30 > 0 AND c.EstimatedRemainingQty <= 0 THEN 1
                WHEN c.FastSalesQuartile = 1 AND c.SalesQty30 > 0 AND c.EstimatedCoverDays <= @LowCoverDays THEN 2
                WHEN c.FastSalesQuartile = 1
                AND c.SalesQty30 > 0
                AND c.GrossMarginRate90 >= @StockUpGrossMarginRate
                AND c.EstimatedCoverDays <= @StockUpCoverDays
                AND c.PreviousDailySalesQty > 0
                    AND c.DailySalesQty30 >= c.PreviousDailySalesQty * (1 + @GrowthRateForStockUp)
                    THEN 3
                WHEN c.FastSalesQuartile = 1 AND c.SalesQty90 > 0 THEN 5
                ELSE 7
            END AS ActionPriority
        FROM Classified c
    )
SELECT *
INTO #FinalRows
FROM FinalRows
""" + scopeWhere + ";";
        }

        /// <summary>
        /// 日统计的聚集主键是 (Date, BranchCode, ...)，日期在前：按日期范围扫描时，单店也要读完
        /// 180 天内所有门店的宽行（2026-09-19 生产约 219 万行、单店 3.8 秒）。有门店范围时改为
        /// 逐日等值关联并强制嵌套循环，每天直接按 (Date, BranchCode) 定位到该门店的行。
        /// 该表按天存储，Date 恒为零点（同日核对近 180 天无非零点时间），等值关联与原范围口径一致。
        /// 全部分店本就要读全部行，逐日查找没有收益，保留范围扫描。
        /// </summary>
        private static string BuildSalesSource(IReadOnlyList<string> storeParameterNames)
        {
            var hasStoreScope = storeParameterNames.Count > 0 && storeParameterNames[0] != "__NO_STORE__";
            if (!hasStoreScope)
            {
                return "FROM [ProductStoreDailySalesStatistic] s";
            }

            return """
FROM (
    SELECT TOP (DATEDIFF(day, @PurchaseStartDate, @NextDate))
        DATEADD(day, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1, @PurchaseStartDate) AS [Day]
    FROM sys.all_objects
) salesDays
INNER LOOP JOIN [ProductStoreDailySalesStatistic] s
    ON s.Date = salesDays.[Day]
""";
        }

        private static string BuildScopeWhere(ProductMovementReportQueryDto query)
        {
            var clauses = BuildScopeClauses(query);
            return clauses.Count == 0 ? "\n" : "\nWHERE " + string.Join("\n    AND ", clauses) + "\n";
        }

        private static List<string> BuildScopeClauses(ProductMovementReportQueryDto query)
        {
            var clauses = new List<string>();
            if (!string.IsNullOrWhiteSpace(query.DataCredibility))
            {
                clauses.Add("DataCredibility = @DataCredibility");
            }
            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                clauses.Add(
                    // 保留库排序规则的 LIKE：BIN2 虽快约 7 倍，但不折叠全半角，搜「$2」会漏掉「＄2 CARDS」
                    // （2026-09-19 生产核对全部分店少 25 行）。关键词的速度改由快照读取时先物化命中行解决。
                    // 货号命中的商品由 BuildKeywordProductsSql 预先物化在 #KeywordProducts。
                    "(ProductCode LIKE @Keyword OR ProductName LIKE @Keyword OR Barcode LIKE @Keyword"
                        + " OR ProductCode IN (SELECT ProductCode FROM #KeywordProducts))"
                );
            }

            return clauses;
        }

        private static string BuildSuggestionWhere(ProductMovementReportQueryDto query)
        {
            return string.IsNullOrWhiteSpace(query.Suggestion)
                ? string.Empty
                : "\nWHERE SystemSuggestion = @Suggestion";
        }

        private static IReadOnlyList<string>? ResolveStoreCodes(
            string? storeCode,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            if (!string.IsNullOrWhiteSpace(storeCode))
            {
                return new[] { storeCode.Trim() };
            }

            return scopedStoreCodes;
        }

        public static IReadOnlyList<string> AddStoreParametersForStoreOptions(
            List<SugarParameter> parameters,
            IReadOnlyList<string>? storeCodes
        )
        {
            return AddStoreParameters(parameters, storeCodes);
        }

        public static string BuildStoreFilterForStoreOptions(
            string expression,
            IReadOnlyList<string> parameterNames
        )
        {
            return BuildStoreFilter(expression, parameterNames);
        }

        private static IReadOnlyList<string> AddStoreParameters(
            List<SugarParameter> parameters,
            IReadOnlyList<string>? storeCodes
        )
        {
            if (storeCodes == null)
            {
                return Array.Empty<string>();
            }

            var parameterNames = new List<string>();
            var normalizedCodes = storeCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < normalizedCodes.Count; i++)
            {
                var parameterName = "@StoreCode" + i;
                parameterNames.Add(parameterName);
                parameters.Add(new SugarParameter(parameterName, normalizedCodes[i]));
            }

            if (parameterNames.Count == 0)
            {
                parameterNames.Add("__NO_STORE__");
            }

            return parameterNames;
        }

        private static string BuildStoreFilter(string expression, IReadOnlyList<string> parameterNames)
        {
            if (parameterNames.Count == 0)
            {
                return string.Empty;
            }

            if (parameterNames.Count == 1 && parameterNames[0] == "__NO_STORE__")
            {
                return "AND 1 = 0";
            }

            return $"AND {expression} IN ({string.Join(", ", parameterNames)})";
        }

        private static string? NormalizeText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string EscapeLikeValue(string value)
        {
            return value
                .Replace("[", "[[]")
                .Replace("%", "[%]")
                .Replace("_", "[_]");
        }
    }

    public sealed class ProductMovementReportSqlBuildResult
    {
        /// <summary>单个批次，依次返回分页明细、汇总、最后更新时间三个结果集。</summary>
        public string Sql { get; set; } = string.Empty;
        public List<SugarParameter> Parameters { get; set; } = new();
    }
}
