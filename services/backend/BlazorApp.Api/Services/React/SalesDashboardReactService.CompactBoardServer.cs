using System.Data;
using BlazorApp.Api.Cache;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 独立销售看板的数据库端聚合（SQL Server）。
/// API↔数据库走公网、约 1–1.5 MB/秒（2026-09-24 生产 Query Store：分桶读取 CPU 5 秒、等网络 30 秒以上），
/// 把区间内几十万行原始聚合拉回内存再算的立方体做法冷查询要 40–60 秒。这里在一个批次里算完四栏，只回几百行：
/// 整月读 CompactBoardMonthlyCell（身份一致才用），其余日期读日事实；供应商归属、交叉筛选、关键词、排序与分页
/// 与内存立方体（FillCompactSalesBoard）同一口径，后者仍用于 SQLite 测试并作为等价性对照。
/// </summary>
public partial class SalesDashboardReactService
{
    private static readonly TimeSpan CompactSalesBoardViewCacheDuration = TimeSpan.FromMinutes(10);

    /// <summary>测试钩子：强制走内存立方体，用于在 SQL Server 上与数据库端聚合逐项比对。</summary>
    internal bool ForceCompactBoardInMemory { get; set; }

    private bool UsesCompactBoardServerAggregation(out string posmDatabase)
    {
        posmDatabase = string.Empty;
        return !ForceCompactBoardInMemory
            && UsesCompactBoardDedicatedConnection()
            && TryGetSameServerPosmDatabase(out posmDatabase);
    }

    private async Task<CompactSalesBoardDto> GetCompactSalesBoardOnServerAsync(
        CompactSalesBoardDto board,
        string posmDatabase,
        DateRangeDto boardRange,
        string cacheVersion,
        HashSet<string>? branchScope,
        CompactSalesBoardQuery query,
        int pageIndex,
        int pageSize,
        long expectedGeneration
    )
    {
        var sortField = NormalizeCompactSortField(query.SortField);
        var descending = string.IsNullOrWhiteSpace(query.SortOrder)
            ? sortField != CompactSalesBoardQuery.SortByItemNumber
            : !string.Equals(query.SortOrder.Trim(), "asc", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(query.SortOrder.Trim(), "ascend", StringComparison.OrdinalIgnoreCase);
        var keywordTerms = (query.Keyword ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8)
            .ToArray();
        string? Selected(string? code) => string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        var selectedBranch = Selected(query.SelectedBranchCode);
        var selectedSupplier = Selected(query.SelectedChinaSupplierCode);
        var selectedProduct = Selected(query.SelectedProductCode);
        var scopeCodes = branchScope?.OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToArray();

        // 结果只有几百行，按全部参数缓存；统计水位变化即换键。强制刷新绕过。
        var viewKey = SalesDashboardCacheKeys.CompactSalesBoardView(
            boardRange,
            cacheVersion,
            scopeCodes == null ? "*" : string.Join(",", scopeCodes),
            $"{selectedBranch}|{selectedSupplier}|{selectedProduct}|{string.Join(" ", keywordTerms)}|{sortField}|{descending}|{pageIndex}|{pageSize}"
        );
        if (!query.ForceRefresh
            && _cache.TryGetValue<CompactSalesBoardDto>(viewKey, out var cachedView)
            && cachedView != null)
        {
            return CopyCompactSalesBoard(cachedView, fromCache: true);
        }

        await using var connection = await OpenCompactBoardDedicatedConnectionAsync();
        var monthlyTables = await CompactBoardMonthlyTablesExistAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Max(1, _context.Db.Ado.CommandTimeOut);
        // 关键词或货号排序要用全部商品的资料；否则只给当前页补资料。
        var needAllProductInfo = keywordTerms.Length > 0 || sortField == CompactSalesBoardQuery.SortByItemNumber;
        command.CommandText = BuildCompactSalesBoardServerSql(posmDatabase, monthlyTables, sortField, descending, needAllProductInfo);
        command.Parameters.Add("@cbStart", SqlDbType.DateTime).Value = boardRange.StartDate.Date;
        command.Parameters.Add("@cbEnd", SqlDbType.DateTime).Value = boardRange.EndDate.Date.AddDays(1);
        command.Parameters.Add("@cbAllBranches", SqlDbType.Bit).Value = scopeCodes == null;
        command.Parameters.Add("@cbBranches", SqlDbType.NVarChar, -1).Value =
            System.Text.Json.JsonSerializer.Serialize(scopeCodes ?? Array.Empty<string>());
        command.Parameters.Add("@cbBranch", SqlDbType.NVarChar, 50).Value = (object?)selectedBranch ?? DBNull.Value;
        command.Parameters.Add("@cbSupplier", SqlDbType.NVarChar, 50).Value = (object?)selectedSupplier ?? DBNull.Value;
        command.Parameters.Add("@cbProduct", SqlDbType.NVarChar, 50).Value = (object?)selectedProduct ?? DBNull.Value;
        command.Parameters.Add("@cbTerms", SqlDbType.NVarChar, -1).Value = System.Text.Json.JsonSerializer.Serialize(keywordTerms);
        command.Parameters.Add("@cbOffset", SqlDbType.Int).Value = (int)Math.Min(int.MaxValue, (long)(pageIndex - 1) * pageSize);
        command.Parameters.Add("@cbPageSize", SqlDbType.Int).Value = pageSize;

        var states = new List<SalesDetailReportStatusSqlRow>();
        var summary = new CompactSalesBoardSummaryDto();
        var stores = new List<CompactSalesBoardStoreDto>();
        var suppliers = new List<CompactSalesBoardChinaSupplierDto>();
        var products = new List<CompactSalesBoardProductDto>();
        var total = 0;
        var scopeAmount = 0m;
        var selectedProductCount = 0;
        static int Int(object value) => value is DBNull ? 0 : (int)Math.Min(int.MaxValue, Convert.ToInt64(value));
        static decimal Dec(object value) => value is DBNull ? 0m : Convert.ToDecimal(value);
        static string? Str(object value) => value is DBNull ? null : Convert.ToString(value);

        await using (var reader = await command.ExecuteReaderAsync())
        {
            // ① 与数据同一快照的状态行
            while (await reader.ReadAsync())
            {
                states.Add(new SalesDetailReportStatusSqlRow
                {
                    Type = SalesStatisticType.ProductStoreDaily,
                    Date = D(reader, 0),
                    Status = S(reader, 1),
                    LastAggregatedAtUtc = ND(reader, 2),
                    CompletedAtUtc = ND(reader, 3),
                    SourceProductVersion = NS(reader, 4),
                });
            }
            await NextAsync();
            // ② 授权范围合计
            if (await reader.ReadAsync())
            {
                summary.OverallAmount = Dec(reader[0]);
                summary.OverallQuantity = Int(reader[1]);
            }
            await NextAsync();
            // ③ 分店栏
            while (await reader.ReadAsync())
            {
                var code = Str(reader[0]) ?? string.Empty;
                var name = Str(reader[1]);
                var amount = Dec(reader[2]);
                stores.Add(new CompactSalesBoardStoreDto
                {
                    BranchCode = code,
                    BranchName = string.IsNullOrWhiteSpace(name) ? code : name,
                    TotalAmount = amount,
                    TotalQuantity = Int(reader[3]),
                    DomesticSupplierAmount = amount,
                    ProductCount = Int(reader[4]),
                });
            }
            await NextAsync();
            // ④ 国内供应商栏
            while (await reader.ReadAsync())
            {
                var code = Str(reader[0]) ?? string.Empty;
                suppliers.Add(new CompactSalesBoardChinaSupplierDto
                {
                    SupplierCode = code,
                    SupplierName = Str(reader[1]) ?? code,
                    TotalAmount = Dec(reader[2]),
                    TotalQuantity = Int(reader[3]),
                    ProductCount = Int(reader[4]),
                });
            }
            await NextAsync();
            // ⑤ 商品栏合计：占比分母（不受关键词影响）、关键词过滤后的总数、受商品选中项约束的款数
            if (await reader.ReadAsync())
            {
                scopeAmount = Dec(reader[0]);
                total = Int(reader[1]);
                selectedProductCount = Int(reader[2]);
            }
            await NextAsync();
            // ⑥ 商品栏当前页（已在数据库全量排序后分页）
            while (await reader.ReadAsync())
            {
                var supplierCode = Str(reader[4]);
                var quantity = Int(reader[6]);
                var amount = Dec(reader[7]);
                products.Add(new CompactSalesBoardProductDto
                {
                    ProductCode = Str(reader[0]) ?? string.Empty,
                    ItemNumber = Str(reader[1]),
                    ProductName = Str(reader[2]),
                    ProductImage = Str(reader[3]),
                    ChinaSupplierCode = supplierCode,
                    ChinaSupplierName = Str(reader[5]) ?? supplierCode,
                    TotalQuantity = quantity,
                    TotalAmount = amount,
                    UnitPrice = quantity > 0 ? Math.Round(amount / quantity, 4) : 0m,
                });
            }
            // 消费完剩余结果，让批次里的 COMMIT 执行完毕、错误能抛出来。
            while (await reader.NextResultAsync()) { }

            async Task NextAsync()
            {
                if (!await reader.NextResultAsync())
                    throw new InvalidOperationException("看板聚合结果集不完整。");
            }
        }

        // 数据与状态同一快照：期间若有新版本发布或状态变为未完成，以快照为准。
        var status = BuildSalesDetailReportStatus(states, boardRange, false);
        ApplyCompactSalesBoardStatus(board, status);
        if (status.StatisticStatus != SalesStatisticRefreshStatus.Fresh)
            return board;

        // 汇总同时受三个选中项约束，由各栏结果推出（与内存立方体逐格累加等价）：
        // 分店栏已受供应商、商品约束，再按选中分店取行；供应商栏已受分店、商品约束，再按选中供应商计数；
        // 商品栏已受分店、供应商约束，受选中商品约束的款数由数据库给出。
        bool MatchesSelection(string code, string? selected) =>
            selected == null || string.Equals(code, selected, StringComparison.OrdinalIgnoreCase);
        var selectedStores = stores.Where(row => MatchesSelection(row.BranchCode, selectedBranch)).ToList();
        summary.TotalAmount = selectedStores.Sum(row => row.TotalAmount);
        summary.TotalQuantity = selectedStores.Sum(row => row.TotalQuantity);
        summary.StoreCount = selectedStores.Count;
        summary.SupplierCount = suppliers.Count(row => MatchesSelection(row.SupplierCode, selectedSupplier));
        summary.ProductCount = selectedProductCount;
        board.Summary = summary;
        board.Stores = stores
            .OrderByDescending(row => row.TotalAmount)
            .ThenBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        board.ChinaSuppliers = suppliers
            .OrderByDescending(row => row.TotalAmount)
            .ThenBy(row => row.SupplierCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        board.ProductDetails = new PagedCompactSalesBoardProductDto
        {
            Data = products,
            Total = total,
            PageIndex = pageIndex,
            PageSize = pageSize,
            ScopeAmount = scopeAmount,
        };
        // 写视图缓存前补上分店总营业额，命中缓存时不必再查。
        await FillCompactSalesBoardBranchTotalsAsync(board.Stores, boardRange);

        SalesDashboardCacheKeys.TryExecuteProductSalesAnalysisCacheWrite(
            viewKey,
            expectedGeneration,
            (registrationToken, expirationToken) => _cache.Set(
                viewKey,
                CopyCompactSalesBoard(board, fromCache: false),
                BuildProductSalesAnalysisCacheOptions(viewKey, CompactSalesBoardViewCacheDuration, registrationToken, expirationToken)
            )
        );
        return board;
    }

    /// <summary>
    /// 分店栏占比的分母：各分店在区间内的总营业额，取 StoreSalesStatistic（营业额日报同一口径，含全部供应商的商品）。
    /// 在数据库里按分店汇总，只回几十行；选中供应商、商品只收窄分子（国内商品金额），分母不变。两条聚合路径共用。
    /// </summary>
    private async Task FillCompactSalesBoardBranchTotalsAsync(IReadOnlyList<CompactSalesBoardStoreDto> stores, DateRangeDto range)
    {
        if (stores.Count == 0)
            return;
        var codes = stores.Select(row => row.BranchCode.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var start = range.StartDate.Date;
        var endExclusive = range.EndDate.Date.AddDays(1);
        var rows = await _context.Db.Queryable<StoreSalesStatistic>()
            .Where(row => row.Date >= start && row.Date < endExclusive && codes.Contains(row.BranchCode))
            .GroupBy(row => row.BranchCode)
            .Select(row => new CompactBoardBranchTotalRow
            {
                BranchCode = row.BranchCode,
                TotalAmount = SqlSugar.SqlFunc.AggregateSum(row.TotalAmount),
            })
            .ToListAsync();
        // 分店代码可能带尾随空格：去空格后再合并，与看板分店栏的代码对齐。
        var totals = rows
            .GroupBy(row => (row.BranchCode ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.TotalAmount), StringComparer.OrdinalIgnoreCase);
        foreach (var store in stores)
            store.BranchTotalAmount = totals.TryGetValue(store.BranchCode.Trim(), out var total) ? total : 0m;
    }

    private sealed class CompactBoardBranchTotalRow
    {
        public string BranchCode { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
    }

    private static CompactSalesBoardDto CopyCompactSalesBoard(CompactSalesBoardDto source, bool fromCache) => new()
    {
        Stores = source.Stores,
        ChinaSuppliers = source.ChinaSuppliers,
        ProductDetails = source.ProductDetails,
        Summary = source.Summary,
        StatisticStatus = source.StatisticStatus,
        StatisticMessage = source.StatisticMessage,
        StatisticUpdatedAt = source.StatisticUpdatedAt,
        FromCache = fromCache,
    };

    private static async Task<bool> CompactBoardMonthlyTablesExistAsync(SqlConnection connection)
    {
        await using var probe = connection.CreateCommand();
        probe.CommandText = CompactBoardMonthlyProjection.TablesExistSql;
        return Convert.ToInt32(await probe.ExecuteScalarAsync()) == 1;
    }

    /// <summary>
    /// 看板数据库端聚合批次。结果集依次为：状态行、授权范围合计、分店栏、国内供应商栏、商品栏合计、商品栏当前页。
    /// 生产逐语句计时（2026-09-24）：临时表每次新建，引用它的语句每次都要编译（每条 0.4–0.5 秒），
    /// 三个 COUNT(DISTINCT CASE …) 的汇总执行 0.8 秒；所以语句尽量合并、汇总改由各栏结果推出、
    /// 商品资料只在关键词或货号排序需要时才查全部，否则只查当前页。
    /// 月表未部署时整段读日事实（仍只回几百行）；排序字段与方向来自白名单，拼进 SQL 文本是安全的。
    /// </summary>
    internal static string BuildCompactSalesBoardServerSql(
        string posmDatabase, bool monthlyTables, string sortField, bool descending, bool needAllProductInfo)
    {
        var posm = SalesDetailQueryMonthlyProjection.QuoteIdentifier(posmDatabase);
        var direction = descending ? "DESC" : "ASC";
        var sortKey = sortField switch
        {
            CompactSalesBoardQuery.SortByQuantity => "p.[Quantity]",
            CompactSalesBoardQuery.SortByUnitPrice => "CASE WHEN p.[Quantity] > 0 THEN p.[Amount] / p.[Quantity] ELSE 0 END",
            CompactSalesBoardQuery.SortByItemNumber => "UPPER(COALESCE(p.[ItemNumber], p.[ProductCode])) COLLATE Latin1_General_100_BIN2",
            _ => "p.[Amount]",
        };
        // 交叉筛选：每栏只受其他栏的选中项约束；选中项不存在时匹配不到任何行（与内存立方体一致）。
        const string branchMatch = "(@cbBranch IS NULL OR c.[BranchCode] = @cbBranch)";
        const string supplierMatch = "(@cbSupplier IS NULL OR c.[ChinaSupplierCode] = @cbSupplier)";
        const string productMatch = "(@cbProduct IS NULL OR c.[ProductCode] = @cbProduct)";
        // 关键词每个词都要命中货号、名称或商品编码之一（库默认排序规则，不区分大小写）。
        const string keywordMatch = """
NOT EXISTS (SELECT 1 FROM OPENJSON(@cbTerms) t
            WHERE NOT (CHARINDEX(CONVERT(nvarchar(200), t.[value]) COLLATE DATABASE_DEFAULT, COALESCE(p.[ItemNumber], N'')) > 0
                    OR CHARINDEX(CONVERT(nvarchar(200), t.[value]) COLLATE DATABASE_DEFAULT, COALESCE(p.[ProductName], N'')) > 0
                    OR CHARINDEX(CONVERT(nvarchar(200), t.[value]) COLLATE DATABASE_DEFAULT, p.[ProductCode]) > 0))
""";
        const string supplierName = """
OUTER APPLY (SELECT TOP (1) cs.[SupplierName] FROM [dbo].[ChinaSupplier] cs
             WHERE LTRIM(RTRIM(cs.[SupplierCode])) = {0} AND NULLIF(LTRIM(RTRIM(cs.[SupplierName])), N'') IS NOT NULL
             ORDER BY cs.[IsDeleted]) sn
""";
        const string productInfo = """
OUTER APPLY (SELECT TOP (1) pr.[ItemNumber], pr.[ProductName], pr.[ProductImage]
             FROM [dbo].[Product] pr WHERE pr.[ProductCode] = {0} AND pr.[IsDeleted] = 0) info
""";
        var usableMonths = monthlyTables
            ? $$"""
    -- 整月可读月表：区间覆盖该月全部已有状态的日期，且月表身份、编码族签名与当前一致。
    UPDATE m SET [Usable] = 1
    FROM #cbMonths m
    INNER JOIN [dbo].[CompactBoardMonthlyState] st ON st.[Month] = m.[Month]
    CROSS APPLY (SELECT {{SalesDetailQueryMonthlyProjection.BuildMonthIdentitySql("m.[Month]")}} [Identity]) ident
    WHERE m.[RangeStart] = CONVERT(datetime, m.[Month])
      AND NOT EXISTS (SELECT 1 FROM [dbo].[SalesStatisticRefreshState] r
                      WHERE r.[StatisticType] = N'ProductStoreDaily' AND r.[Date] >= m.[RangeEnd]
                        AND r.[Date] < CONVERT(datetime, DATEADD(month, 1, m.[Month])))
      AND st.[ProjectionSchemaVersion] = {{CompactBoardMonthlyProjection.SchemaVersion}}
      AND st.[DayIdentity] = ident.[Identity]
      AND st.[CodeFamilySignature] = @cbFamilySignature;
"""
            : string.Empty;
        var monthlyCells = monthlyTables
            ? """
            SELECT c.[BranchCode], c.[ProductCode], c.[RawSupplierCode], c.[Quantity], c.[Amount], c.[LastDate]
            FROM #cbMonths m INNER JOIN [dbo].[CompactBoardMonthlyCell] c ON c.[Month] = m.[Month]
            WHERE m.[Usable] = 1
            UNION ALL
"""
            : string.Empty;
        // 商品栏：关键词或货号排序需要全部商品的资料；否则先排序分页，只给当前页补资料。
        var productRows = needAllProductInfo
            ? $$"""
    DROP TABLE IF EXISTS #cbProducts;
    SELECT g.[ProductCode], g.[ChinaSupplierCode], g.[Amount], g.[Quantity], info.[ItemNumber], info.[ProductName], info.[ProductImage]
    INTO #cbProducts
    FROM (SELECT c.[ProductCode], MAX(c.[ChinaSupplierCode]) [ChinaSupplierCode], SUM(c.[Amount]) [Amount], SUM(c.[Quantity]) [Quantity]
          FROM #cbCube c WHERE {{branchMatch}} AND {{supplierMatch}} GROUP BY c.[ProductCode]) g
    {{string.Format(productInfo, "g.[ProductCode]")}};

    -- ⑤ 商品栏合计：占比分母不受关键词影响；关键词过滤后的款数；受商品选中项约束的款数（汇总用）。
    -- 聚合函数里不能含子查询：先逐行判定关键词命中，再求和。
    SELECT COALESCE(SUM(k.[Amount]), 0), COALESCE(SUM(k.[Hit]), 0), COALESCE(SUM(k.[Selected]), 0)
    FROM (SELECT p.[Amount], CASE WHEN {{keywordMatch}} THEN 1 ELSE 0 END [Hit],
                 CASE WHEN @cbProduct IS NULL OR p.[ProductCode] = @cbProduct THEN 1 ELSE 0 END [Selected]
          FROM #cbProducts p) k;

    -- ⑥ 商品栏当前页：全部结果排序后分页，同值按商品编码（不区分大小写的序数比较）升序。
    SELECT p.[ProductCode], p.[ItemNumber], p.[ProductName], p.[ProductImage], p.[ChinaSupplierCode], sn.[SupplierName], p.[Quantity], p.[Amount]
    FROM #cbProducts p
    {{string.Format(supplierName, "p.[ChinaSupplierCode]")}}
    WHERE {{keywordMatch}}
    ORDER BY {{sortKey}} {{direction}}, UPPER(p.[ProductCode]) COLLATE Latin1_General_100_BIN2
    OFFSET @cbOffset ROWS FETCH NEXT @cbPageSize ROWS ONLY;
"""
            : $$"""
    DROP TABLE IF EXISTS #cbProducts;
    SELECT c.[ProductCode], MAX(c.[ChinaSupplierCode]) [ChinaSupplierCode], SUM(c.[Amount]) [Amount], SUM(c.[Quantity]) [Quantity]
    INTO #cbProducts
    FROM #cbCube c WHERE {{branchMatch}} AND {{supplierMatch}} GROUP BY c.[ProductCode];

    -- ⑤ 商品栏合计：无关键词时总数即全部款数；受商品选中项约束的款数（汇总用）。
    SELECT COALESCE(SUM(p.[Amount]), 0), COUNT(*),
           COALESCE(SUM(CASE WHEN @cbProduct IS NULL OR p.[ProductCode] = @cbProduct THEN 1 ELSE 0 END), 0)
    FROM #cbProducts p;

    -- ⑥ 商品栏当前页：先对全部结果排序分页，再只给这一页补商品资料与供应商名称。
    SELECT p.[ProductCode], info.[ItemNumber], info.[ProductName], info.[ProductImage], p.[ChinaSupplierCode], sn.[SupplierName], p.[Quantity], p.[Amount]
    FROM (SELECT p.[ProductCode], p.[ChinaSupplierCode], p.[Quantity], p.[Amount],
                 ROW_NUMBER() OVER (ORDER BY {{sortKey}} {{direction}}, UPPER(p.[ProductCode]) COLLATE Latin1_General_100_BIN2) [RowNumber]
          FROM #cbProducts p
          ORDER BY {{sortKey}} {{direction}}, UPPER(p.[ProductCode]) COLLATE Latin1_General_100_BIN2
          OFFSET @cbOffset ROWS FETCH NEXT @cbPageSize ROWS ONLY) p
    {{string.Format(productInfo, "p.[ProductCode]")}}
    {{string.Format(supplierName, "p.[ChinaSupplierCode]")}}
    ORDER BY p.[RowNumber];
""";

        return $$"""
SET NOCOUNT ON;
IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND snapshot_isolation_state = 1)
BEGIN
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;
END;
BEGIN TRY
    -- ① 状态行：与下面的数据同一快照，调用方据此判定完整性与水位。
    SELECT r.[Date], r.[Status], r.[LastAggregatedAtUtc], r.[CompletedAtUtc], r.[SourceProductVersion]
    FROM [dbo].[SalesStatisticRefreshState] r
    WHERE r.[StatisticType] = N'ProductStoreDaily' AND r.[Date] >= @cbStart AND r.[Date] < @cbEnd;

    {{CompactBoardMonthlyProjection.CodeFamilySql}}

    -- 区间按自然月切片：每片要么整月读月表，要么按片内日期范围读日事实。
    DECLARE @cbStartDate date = CONVERT(date, @cbStart), @cbEndDate date = CONVERT(date, @cbEnd);
    DROP TABLE IF EXISTS #cbMonths;
    CREATE TABLE #cbMonths ([Month] date NOT NULL, [RangeStart] datetime NOT NULL, [RangeEnd] datetime NOT NULL, [Usable] bit NOT NULL);
    ;WITH Digits(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n)),
    MonthOffsets(n) AS (SELECT a.n + b.n * 10 FROM Digits a CROSS JOIN Digits b)
    INSERT INTO #cbMonths ([Month], [RangeStart], [RangeEnd], [Usable])
    SELECT m.[Month],
           CASE WHEN m.[Month] < @cbStartDate THEN @cbStart ELSE CONVERT(datetime, m.[Month]) END,
           CASE WHEN DATEADD(month, 1, m.[Month]) > @cbEndDate THEN @cbEnd ELSE CONVERT(datetime, DATEADD(month, 1, m.[Month])) END,
           0
    FROM MonthOffsets o
    CROSS APPLY (SELECT DATEADD(month, o.n, DATEFROMPARTS(YEAR(@cbStartDate), MONTH(@cbStartDate), 1)) [Month]) m
    WHERE m.[Month] < @cbEndDate;
{{usableMonths}}
    -- 旧 200 行靠 POSM 映射还原国内供应商；同一商品多条映射取最小编码，结果确定。
    DROP TABLE IF EXISTS #cbMap;
    SELECT LTRIM(RTRIM(m.[ProductCode])) COLLATE DATABASE_DEFAULT [ProductCode],
           MIN(LTRIM(RTRIM(m.[ChinaSupplierCode])) COLLATE DATABASE_DEFAULT) [ChinaSupplierCode]
    INTO #cbMap
    FROM {{posm}}.[dbo].[posm_product_supplier_mapping] m
    WHERE m.[LocalSupplierCode] = N'200' AND m.[IsDeleted] = 0 AND m.[ProductCode] IS NOT NULL
      AND m.[ChinaSupplierCode] IS NOT NULL AND LTRIM(RTRIM(m.[ChinaSupplierCode])) <> N''
    GROUP BY LTRIM(RTRIM(m.[ProductCode])) COLLATE DATABASE_DEFAULT;

    -- 区间内「分店×商品×原始供应商编码」原始聚合并逐行还原国内供应商（一条语句）：
    -- 整月读月表，其余日期按片内日期范围逐片定位日事实（聚集键以日期打头）；直写行用行上的编码，未映射的 200 行不计入。
    DROP TABLE IF EXISTS #cbResolved;
    SELECT a.[BranchCode], a.[ProductCode], a.[RawSupplierCode], a.[Quantity], a.[Amount], a.[LastDate],
           CASE WHEN a.[RawSupplierCode] = N'200' THEN mp.[ChinaSupplierCode] ELSE a.[RawSupplierCode] END [ChinaSupplierCode]
    INTO #cbResolved
    FROM (
        SELECT x.[BranchCode], x.[ProductCode], x.[RawSupplierCode],
               SUM(x.[Quantity]) [Quantity], SUM(x.[Amount]) [Amount], MAX(x.[LastDate]) [LastDate]
        FROM (
{{monthlyCells}}            SELECT f.[BranchCode], f.[ProductCode], f.[RawSupplierCode], f.[Quantity], f.[Amount], f.[LastDate]
            FROM (
                SELECT LTRIM(RTRIM(COALESCE(s.[BranchCode], N''))) [BranchCode],
                       LTRIM(RTRIM(COALESCE(s.[ProductCode], N''))) [ProductCode],
                       LTRIM(RTRIM(COALESCE(s.[SupplierCode], N''))) [RawSupplierCode],
                       CONVERT(bigint, s.[TotalQuantity]) [Quantity], CONVERT(decimal(38,4), s.[TotalAmount]) [Amount],
                       CONVERT(date, s.[Date]) [LastDate]
                FROM #cbMonths m
                INNER LOOP JOIN [dbo].[ProductStoreDailySalesStatistic] s ON s.[Date] >= m.[RangeStart] AND s.[Date] < m.[RangeEnd]
                WHERE m.[Usable] = 0
            ) f
            WHERE f.[RawSupplierCode] IN (SELECT [Code] FROM #cbFamily) AND f.[BranchCode] <> N'' AND f.[ProductCode] <> N''
        ) x
        GROUP BY x.[BranchCode], x.[ProductCode], x.[RawSupplierCode]
    ) a
    LEFT JOIN #cbMap mp ON a.[RawSupplierCode] = N'200' AND mp.[ProductCode] = a.[ProductCode]
    WHERE a.[RawSupplierCode] <> N'200' OR mp.[ChinaSupplierCode] IS NOT NULL
    OPTION (RECOMPILE);

    -- 立方体（一条语句）：每个商品只挂一个国内供应商——全部分店范围内取最近销售日那一条，同日直写优先，再按编码序；
    -- 归属在授权过滤之前确定（窗口在内层对全部分店计算），然后每个分店×商品一格。
    -- 生产实测（2026-09-24）先在商品粒度聚合再开窗并不更快（执行 0.83 秒 vs 0.86 秒，编译反而多 0.3 秒），瓶颈是行模式处理 20 万格本身。
    DROP TABLE IF EXISTS #cbCube;
    SELECT w.[BranchCode], w.[ProductCode], w.[ProductSupplier] [ChinaSupplierCode], SUM(w.[Quantity]) [Quantity], SUM(w.[Amount]) [Amount]
    INTO #cbCube
    FROM (
        SELECT r.[BranchCode], r.[ProductCode], r.[Quantity], r.[Amount],
               FIRST_VALUE(r.[ChinaSupplierCode]) OVER (
                   PARTITION BY r.[ProductCode]
                   ORDER BY r.[LastDate] DESC, CASE WHEN r.[RawSupplierCode] = N'200' THEN 1 ELSE 0 END,
                            r.[ChinaSupplierCode] COLLATE Latin1_General_100_BIN2
                   ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) [ProductSupplier]
        FROM #cbResolved r
    ) w
    WHERE @cbAllBranches = 1
       OR w.[BranchCode] IN (SELECT CONVERT(nvarchar(50), b.[value]) COLLATE DATABASE_DEFAULT FROM OPENJSON(@cbBranches) b)
    GROUP BY w.[BranchCode], w.[ProductCode], w.[ProductSupplier];

    -- ② 授权范围合计（不受选中项约束）；其余汇总数字由各栏结果推出，避免 COUNT(DISTINCT CASE …)。
    SELECT COALESCE(SUM(c.[Amount]), 0), COALESCE(SUM(c.[Quantity]), 0) FROM #cbCube c;

    -- ③ 分店栏：受供应商、商品选中项约束；动销款数按格计数。
    SELECT g.[BranchCode], stn.[StoreName], g.[Amount], g.[Quantity], g.[ProductCount]
    FROM (SELECT c.[BranchCode], SUM(c.[Amount]) [Amount], SUM(c.[Quantity]) [Quantity], COUNT(*) [ProductCount]
          FROM #cbCube c WHERE {{supplierMatch}} AND {{productMatch}} GROUP BY c.[BranchCode]) g
    OUTER APPLY (SELECT TOP (1) st.[StoreName] FROM [dbo].[Store] st WHERE st.[StoreCode] = g.[BranchCode]) stn;

    -- ④ 国内供应商栏：受分店、商品选中项约束；款数用两级分组代替 COUNT(DISTINCT)。
    SELECT g.[ChinaSupplierCode], sn.[SupplierName], g.[Amount], g.[Quantity], g.[ProductCount]
    FROM (SELECT sp.[ChinaSupplierCode], SUM(sp.[Amount]) [Amount], SUM(sp.[Quantity]) [Quantity], COUNT(*) [ProductCount]
          FROM (SELECT c.[ChinaSupplierCode], c.[ProductCode], SUM(c.[Amount]) [Amount], SUM(c.[Quantity]) [Quantity]
                FROM #cbCube c WHERE {{branchMatch}} AND {{productMatch}} GROUP BY c.[ChinaSupplierCode], c.[ProductCode]) sp
          GROUP BY sp.[ChinaSupplierCode]) g
    {{string.Format(supplierName, "g.[ChinaSupplierCode]")}};
{{productRows}}
    DROP TABLE IF EXISTS #cbProducts;
    DROP TABLE IF EXISTS #cbCube;
    DROP TABLE IF EXISTS #cbResolved;
    DROP TABLE IF EXISTS #cbMap;
    DROP TABLE IF EXISTS #cbMonths;
    DROP TABLE IF EXISTS #cbFamily;
    IF @@TRANCOUNT > 0 COMMIT TRANSACTION;
    SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
    THROW;
END CATCH;
""";
    }
}
