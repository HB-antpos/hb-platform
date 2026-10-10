using System.Text.Json;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

public partial class SalesDashboardReactService
{
    /// <summary>
    /// 商品明细（enhanced-sales-product-details）每页条数上限。
    /// 移动端商品报告提供 100/200/500 三档；外层方法与 SQL 分页方法必须共用这一个上限，
    /// 否则一处放宽、另一处仍夹到旧值，会让返回的 PageSize 与前端按所选档位算出的总页数对不上。
    /// </summary>
    internal const int EnhancedProductDetailMaxPageSize = 500;

    /// <summary>
    /// SQL Server 商品报表读取结果。无数据页仍返回一行 HasData=false，保证越界页可以得到准确 Total。
    /// </summary>
    internal sealed class ProductReportPagingSqlRow
    {
        public bool HasData { get; set; }
        public int TotalCount { get; set; }
        public string? ProductCode { get; set; }
        public string? CurrentProductName { get; set; }
        public string? CompareProductName { get; set; }
        public int CurrentQuantity { get; set; }
        public decimal CurrentSalesAmount { get; set; }
        public int CurrentOrderCount { get; set; }
        public decimal? CurrentGrossProfit { get; set; }
        public int CurrentStatisticRowCount { get; set; }
        public int CurrentCostedRowCount { get; set; }
        public int CurrentGrossProfitRowCount { get; set; }
        public int CompareQuantity { get; set; }
        public decimal CompareSalesAmount { get; set; }
        public int CompareOrderCount { get; set; }
        public decimal? CompareGrossProfit { get; set; }
        public int CompareStatisticRowCount { get; set; }
        public int CompareCostedRowCount { get; set; }
        public int CompareGrossProfitRowCount { get; set; }
        public string? ItemNumber { get; set; }
        public string? ProductImage { get; set; }
    }

    /// <summary>
    /// 在 SQL Server 中一次完成两期商品聚合和分页。输入集合全部通过 JSON 参数传递，避免大 IN 参数和分批往返。
    /// </summary>
    internal async Task<PagedSalesProductDetailWithDiscountDto> GetEnhancedSalesProductDetailsSqlServerAsync(
        DateRangeDto dateRange,
        List<string>? branchCodes,
        List<string>? localSupplierCodes,
        List<string>? chinaSupplierCodes,
        int pageIndex,
        int pageSize,
        string? productSearch,
        bool chinaSupplierScope,
        ProductReportSort sort = default
    )
    {
        ValidateDateRange(dateRange);
        pageIndex = Math.Max(1, pageIndex);
        pageSize = Math.Clamp(pageSize, 1, EnhancedProductDetailMaxPageSize);

        if (_context.Db.CurrentConnectionConfig.DbType != DbType.SqlServer)
        {
            throw new InvalidOperationException("商品分页 SQL 优化仅支持 SQL Server。");
        }

        var normalizedBranches = NormalizeCodes(branchCodes);
        if (branchCodes is not null && normalizedBranches.Count == 0)
        {
            return CreateEmptyProductPagingResult(pageIndex, pageSize);
        }
        var normalizedLocal = NormalizeCodes(chinaSupplierScope ? null : localSupplierCodes);
        var normalizedChina = NormalizeCodes(chinaSupplierCodes);

        var chinaProductMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (chinaSupplierScope || normalizedChina.Any())
        {
            // 先按本期/同期和授权分店确定遗留商品，避免为一页结果读取整份商品映射。
            // 全部中国供应商代码单独读取，仍包含期间内只有直写统计行的供应商。
            var legacyProducts = await GetReportLegacyChinaProductCodesAsync(dateRange, normalizedBranches);
            var includeAllSuppliers = chinaSupplierScope && !normalizedChina.Any();
            var mapping = await ReadReportChinaSupplierMappingsAsync(legacyProducts, includeAllSuppliers);
            chinaProductMap = mapping.ProductMap;
            if (includeAllSuppliers)
            {
                normalizedChina = (await GetChinaSupplierCodeSetAsync(mapping.SupplierCodes)).ToList();
                if (!normalizedChina.Any())
                {
                    return CreateEmptyProductPagingResult(pageIndex, pageSize);
                }
            }
        }

        // 本地供应商选择 200 时，现有口径包含 200 旧行和日统计中直写的全部中国供应商行。
        var sqlLocalSupplierCodes = normalizedLocal;
        if (!normalizedChina.Any() && normalizedLocal.Any(IsChinaLocalSupplierCode))
        {
            sqlLocalSupplierCodes = normalizedLocal
                .Where(code => !IsChinaLocalSupplierCode(code))
                .Concat(new[] { CHINA_LOCAL_SUPPLIER_CODE })
                .Concat(await GetChinaSupplierCodeSetAsync())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var normalizedSearch = string.IsNullOrWhiteSpace(productSearch) ? null : productSearch.Trim();
        var hasCompare = dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue;
        var selectedChinaCodes = normalizedChina.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parameters = new List<SugarParameter>
        {
            new("@CurrentStart", dateRange.StartDate.Date),
            new("@CurrentEndExclusive", dateRange.EndDate.Date.AddDays(1)),
            new("@Branches", JsonSerializer.Serialize(normalizedBranches)),
            new("@HasBranchFilter", normalizedBranches.Any()),
            new("@LocalSuppliers", JsonSerializer.Serialize(sqlLocalSupplierCodes)),
            new("@HasLocalFilter", sqlLocalSupplierCodes.Any()),
            new("@ChinaSuppliers", JsonSerializer.Serialize(normalizedChina)),
            // 映射已限定为当前中国供应商集合，SQL 无需在每一行重复匹配供应商表。
            new("@ChinaProductMap", JsonSerializer.Serialize(chinaProductMap
                .Where(pair => selectedChinaCodes.Contains(pair.Value)).Select(pair => new
            {
                ProductCode = pair.Key,
                ChinaSupplierCode = pair.Value,
            }))),
            new("@HasChinaFilter", normalizedChina.Any()),
            new("@ProductSearch", normalizedSearch is null ? DBNull.Value : EscapeLikeValue(normalizedSearch)),
            new("@PageOffset", checked((long)(pageIndex - 1) * pageSize)),
            new("@PageSize", pageSize),
        };
        if (hasCompare)
        {
            parameters.Add(new SugarParameter("@CompareStart", dateRange.CompareStartDate!.Value.Date));
            parameters.Add(new SugarParameter("@CompareEndExclusive", dateRange.CompareEndDate!.Value.Date.AddDays(1)));
        }

        var rows = await _context.Db.Ado.SqlQueryAsync<ProductReportPagingSqlRow>(
            BuildProductReportPagingSql(hasCompare, sort),
            parameters.ToArray()
        );
        // 先确定实际页，避免优化器把商品图片等宽字段连接提前到全量汇总之前。
        var pageCodes = rows.Where(row => row.HasData && !string.IsNullOrWhiteSpace(row.ProductCode))
            .Select(row => row.ProductCode!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (pageCodes.Count > 0)
        {
            var products = await _context.Db.Queryable<Product>()
                .Where(product => product.ProductCode != null && pageCodes.Contains(product.ProductCode))
                .Select(product => new ProductInfo
                {
                    ProductCode = product.ProductCode ?? string.Empty,
                    ItemNumber = product.ItemNumber,
                    ProductImage = product.ProductImage,
                }).ToListAsync();
            var byCode = products.ToDictionary(product => product.ProductCode, StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.Where(row => row.HasData))
                if (row.ProductCode != null && byCode.TryGetValue(row.ProductCode, out var product))
                {
                    row.ItemNumber = product.ItemNumber;
                    row.ProductImage = product.ProductImage;
                }
        }
        return MapProductReportPagingRows(rows, pageIndex, pageSize);
    }

    internal Task<(Dictionary<string, string> ProductMap, List<string> SupplierCodes)> ReadReportChinaSupplierMappingsAsync(
        HashSet<string> productCodes, bool includeAllSupplierCodes)
    {
        return ReadReportSnapshotOnConnectionAsync(_posmContext.Db, async () =>
        {
            var query = _posmContext.Db.Queryable<PosmProductSupplierMapping>()
                .With(SqlWith.Null)
                .Where(row => !row.IsDeleted && row.LocalSupplierCode == CHINA_LOCAL_SUPPLIER_CODE
                    && row.ChinaSupplierCode != null && row.ChinaSupplierCode != "");
            // 即使期间没有遗留 200 商品，也必须保留完整代码集合来识别直写中国供应商行。
            var suppliers = includeAllSupplierCodes
                ? await query.Clone().Where(row => row.ProductCode != null && row.ProductCode.Trim() != "")
                    .Select(row => row.ChinaSupplierCode!).Distinct().ToListAsync()
                : new List<string>();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (productCodes.Count > 0 && _posmContext.Db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                // 月报涉及上万商品，使用一个 JSON 参数做集合连接，避免数十批查询往返。
                // 原始 SQL 不继承连接的 NOLOCK 默认值，仍由外层同一个 POSM 快照事务保护。
                // OPENJSON 默认估算 50 行；哈希连接避免对上万个代码逐个执行动态索引查找。
                var rows = await _posmContext.Db.Ado.SqlQueryAsync<ChinaSupplierProductMapRow>("""
                    SELECT m.[ProductCode], m.[ChinaSupplierCode]
                    FROM [posm_product_supplier_mapping] AS m
                    INNER JOIN OPENJSON(@ProductCodes) WITH ([ProductCode] nvarchar(50) '$') AS requested
                        ON requested.[ProductCode] COLLATE DATABASE_DEFAULT = m.[ProductCode]
                    WHERE m.[IsDeleted] = 0 AND m.[LocalSupplierCode] = N'200'
                        AND m.[ChinaSupplierCode] IS NOT NULL AND m.[ChinaSupplierCode] <> N''
                    OPTION (RECOMPILE, HASH JOIN)
                    """, new[] { new SugarParameter("@ProductCodes", JsonSerializer.Serialize(productCodes)) });
                MergeChinaSupplierProductMap(map, rows);
                return (map, suppliers);
            }
            foreach (var batch in BatchProductSalesCodes(productCodes))
            {
                var codes = batch.ToList();
                var rows = await query.Clone().Where(row => codes.Contains(row.ProductCode))
                    .Select(row => new ChinaSupplierProductMapRow
                    {
                        ProductCode = row.ProductCode,
                        ChinaSupplierCode = row.ChinaSupplierCode!,
                    }).ToListAsync();
                MergeChinaSupplierProductMap(map, rows);
            }
            return (map, suppliers);
        });
    }

    internal async Task<HashSet<string>> GetReportLegacyChinaProductCodesAsync(DateRangeDto range, List<string> branches)
    {
        var start = range.StartDate.Date;
        var end = range.EndDate.Date.AddDays(1);
        var compareStart = range.CompareStartDate?.Date ?? start;
        var compareEnd = range.CompareEndDate?.Date.AddDays(1) ?? end;
        var query = _context.Db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(row => row.SupplierCode == CHINA_LOCAL_SUPPLIER_CODE
                && ((row.Date >= start && row.Date < end) || (row.Date >= compareStart && row.Date < compareEnd)));
        if (branches.Count > 0)
            query = query.Where(row => branches.Contains(row.BranchCode));
        var selected = query.Select(row => row.ProductCode).Distinct();
        if (_context.Db.CurrentConnectionConfig.DbType == DbType.SqlServer)
        {
            // 周/月与日范围相差很大，旧计划可能退化为供应商索引的全历史并行扫描。
            var sql = selected.ToSql();
            return (await _context.Db.Ado.SqlQueryAsync<string>(sql.Key + " OPTION (RECOMPILE)", sql.Value.ToArray()))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return (await selected.ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static string BuildProductReportPagingSql(bool includeCompare, ProductReportSort sort = default)
    {
        // 排名只聚合销售额；页码确定后再读取本页的名称、数量和成本覆盖，避免对全部商品做宽行聚合。
        // 按数量或均价排序时才在排名阶段额外汇总数量，默认排序的 SQL 文本保持不变。
        return $"""
            BEGIN TRY
            -- 筛选集合只解析一次；显式标记空筛选，避免逐商品重复执行 OPENJSON/NOT EXISTS。
            -- 快照事务不允许独立 CREATE INDEX；索引随临时表定义创建，避免中断一致性读取。
            CREATE TABLE #ProductReportBranchFilter ([BranchCode] nvarchar(100) COLLATE DATABASE_DEFAULT,
                INDEX [IX_ProductReportBranchFilter] CLUSTERED ([BranchCode]));
            INSERT INTO #ProductReportBranchFilter SELECT DISTINCT CONVERT(nvarchar(100), [value]) FROM OPENJSON(@Branches);
            CREATE TABLE #ProductReportLocalFilter ([SupplierCode] nvarchar(100) COLLATE DATABASE_DEFAULT,
                INDEX [IX_ProductReportLocalFilter] CLUSTERED ([SupplierCode]));
            INSERT INTO #ProductReportLocalFilter SELECT DISTINCT CONVERT(nvarchar(100), [value]) FROM OPENJSON(@LocalSuppliers);
            CREATE TABLE #ProductReportChinaFilter ([SupplierCode] nvarchar(100) COLLATE DATABASE_DEFAULT,
                INDEX [IX_ProductReportChinaFilter] CLUSTERED ([SupplierCode]));
            INSERT INTO #ProductReportChinaFilter SELECT DISTINCT CONVERT(nvarchar(100), [value]) FROM OPENJSON(@ChinaSuppliers);
            CREATE TABLE #ProductReportChinaMap ([ProductCode] nvarchar(100) COLLATE DATABASE_DEFAULT,
                [ChinaSupplierCode] nvarchar(100) COLLATE DATABASE_DEFAULT,
                INDEX [IX_ProductReportChinaMap_Product] CLUSTERED ([ProductCode]));
            INSERT INTO #ProductReportChinaMap
            SELECT [ProductCode], [ChinaSupplierCode]
            FROM OPENJSON(@ChinaProductMap)
            WITH
            (
                [ProductCode] nvarchar(100) '$.ProductCode',
                [ChinaSupplierCode] nvarchar(100) '$.ChinaSupplierCode'
            );
            CREATE TABLE #ProductReportSearchProducts ([ProductCode] nvarchar(50) COLLATE DATABASE_DEFAULT,
                INDEX [IX_ProductReportSearchProducts] CLUSTERED ([ProductCode]));
            INSERT INTO #ProductReportSearchProducts
            SELECT DISTINCT product.[ProductCode]
            FROM [dbo].[Product] AS product
            WHERE @ProductSearch IS NOT NULL
                AND (product.[ItemNumber] LIKE N'%' + @ProductSearch + N'%' ESCAPE N'\'
                    OR product.[Barcode] LIKE N'%' + @ProductSearch + N'%' ESCAPE N'\');

            {BuildProductReportSourceCtes(includeCompare, pageOnly: false)}
            SELECT [ProductCode],
                SUM(CASE WHEN [Period] = 0 THEN [TotalAmount] ELSE 0 END) AS [CurrentSalesAmount],
                SUM(CASE WHEN [Period] = 1 THEN [TotalAmount] ELSE 0 END) AS [CompareSalesAmount]{(sort.RequiresQuantity ? ProductReportRankingQuantityColumns : string.Empty)}
            INTO #ProductReportAggregates
            FROM PeriodRows
            WHERE [ProductCode] IS NOT NULL AND LTRIM(RTRIM([ProductCode])) <> N''
            GROUP BY [ProductCode]
            OPTION (RECOMPILE);

            CREATE TABLE #ProductReportPage ([HasData] bit NOT NULL, [TotalCount] int NOT NULL,
                [RowNumber] bigint NOT NULL, [ProductCode] nvarchar(50) COLLATE DATABASE_DEFAULT,
                INDEX [IX_ProductReportPage_Product] CLUSTERED ([ProductCode]));
            ;WITH Windowed AS
            (
                SELECT a.*,
                    CONVERT(int, COUNT(*) OVER ()) AS [TotalCount],
                    ROW_NUMBER() OVER
                    (
                        ORDER BY {BuildProductReportRankingOrderBy(sort)}
                    ) AS [RowNumber]
                FROM #ProductReportAggregates AS a
            )
            INSERT INTO #ProductReportPage
            SELECT CAST(CASE WHEN windowed.[RowNumber] > @PageOffset THEN 1 ELSE 0 END AS bit) AS [HasData],
                windowed.[TotalCount], windowed.[RowNumber], windowed.[ProductCode]
            FROM Windowed AS windowed
            WHERE
                (
                    windowed.[RowNumber] > @PageOffset
                    AND windowed.[RowNumber] <= @PageOffset + @PageSize
                )
                OR
                (
                    windowed.[TotalCount] <= @PageOffset
                    AND windowed.[RowNumber] = 1
                );

            {BuildProductReportSourceCtes(includeCompare, pageOnly: true)},
            Aggregated AS
            (
                SELECT [ProductCode],
                    MAX(CASE WHEN [Period] = 0 THEN [ProductName] END) AS [CurrentProductName],
                    MAX(CASE WHEN [Period] = 1 THEN [ProductName] END) AS [CompareProductName],
                    SUM(CASE WHEN [Period] = 0 THEN [TotalQuantity] ELSE 0 END) AS [CurrentQuantity],
                    SUM(CASE WHEN [Period] = 0 THEN [TotalAmount] ELSE 0 END) AS [CurrentSalesAmount],
                    SUM(CASE WHEN [Period] = 0 THEN [OrderCount] ELSE 0 END) AS [CurrentOrderCount],
                    SUM(CASE WHEN [Period] = 0 THEN [GrossProfit] END) AS [CurrentGrossProfit],
                    SUM(CASE WHEN [Period] = 0 THEN 1 ELSE 0 END) AS [CurrentStatisticRowCount],
                    SUM(CASE WHEN [Period] = 0 AND [TotalCost] IS NOT NULL THEN 1 ELSE 0 END) AS [CurrentCostedRowCount],
                    SUM(CASE WHEN [Period] = 0 AND [GrossProfit] IS NOT NULL THEN 1 ELSE 0 END) AS [CurrentGrossProfitRowCount],
                    SUM(CASE WHEN [Period] = 1 THEN [TotalQuantity] ELSE 0 END) AS [CompareQuantity],
                    SUM(CASE WHEN [Period] = 1 THEN [TotalAmount] ELSE 0 END) AS [CompareSalesAmount],
                    SUM(CASE WHEN [Period] = 1 THEN [OrderCount] ELSE 0 END) AS [CompareOrderCount],
                    SUM(CASE WHEN [Period] = 1 THEN [GrossProfit] END) AS [CompareGrossProfit],
                    SUM(CASE WHEN [Period] = 1 THEN 1 ELSE 0 END) AS [CompareStatisticRowCount],
                    SUM(CASE WHEN [Period] = 1 AND [TotalCost] IS NOT NULL THEN 1 ELSE 0 END) AS [CompareCostedRowCount],
                    SUM(CASE WHEN [Period] = 1 AND [GrossProfit] IS NOT NULL THEN 1 ELSE 0 END) AS [CompareGrossProfitRowCount]
                FROM PeriodRows
                GROUP BY [ProductCode]
            )
            SELECT page.[HasData], page.[TotalCount], page.[ProductCode],
                detail.[CurrentProductName], detail.[CompareProductName],
                detail.[CurrentQuantity], detail.[CurrentSalesAmount], detail.[CurrentOrderCount], detail.[CurrentGrossProfit],
                detail.[CurrentStatisticRowCount], detail.[CurrentCostedRowCount], detail.[CurrentGrossProfitRowCount],
                detail.[CompareQuantity], detail.[CompareSalesAmount], detail.[CompareOrderCount], detail.[CompareGrossProfit],
                detail.[CompareStatisticRowCount], detail.[CompareCostedRowCount], detail.[CompareGrossProfitRowCount]
            FROM #ProductReportPage AS page
            LEFT JOIN Aggregated AS detail ON detail.[ProductCode] = page.[ProductCode]
            ORDER BY page.[RowNumber] ASC;

            DROP TABLE #ProductReportSearchProducts;
            DROP TABLE #ProductReportPage;
            DROP TABLE #ProductReportAggregates;
            DROP TABLE #ProductReportChinaMap;
            DROP TABLE #ProductReportBranchFilter;
            DROP TABLE #ProductReportLocalFilter;
            DROP TABLE #ProductReportChinaFilter;
            END TRY
            BEGIN CATCH
                -- 事务已不可提交时由外层回滚清理，避免 DROP 再次报错而掩盖原始异常。
                IF XACT_STATE() <> -1
                BEGIN
                IF OBJECT_ID(N'tempdb..#ProductReportSearchProducts') IS NOT NULL
                    DROP TABLE #ProductReportSearchProducts;
                IF OBJECT_ID(N'tempdb..#ProductReportPage') IS NOT NULL
                    DROP TABLE #ProductReportPage;
                IF OBJECT_ID(N'tempdb..#ProductReportAggregates') IS NOT NULL
                    DROP TABLE #ProductReportAggregates;
                IF OBJECT_ID(N'tempdb..#ProductReportChinaMap') IS NOT NULL
                    DROP TABLE #ProductReportChinaMap;
                IF OBJECT_ID(N'tempdb..#ProductReportBranchFilter') IS NOT NULL
                    DROP TABLE #ProductReportBranchFilter;
                IF OBJECT_ID(N'tempdb..#ProductReportLocalFilter') IS NOT NULL
                    DROP TABLE #ProductReportLocalFilter;
                IF OBJECT_ID(N'tempdb..#ProductReportChinaFilter') IS NOT NULL
                    DROP TABLE #ProductReportChinaFilter;
                END;
                THROW;
            END CATCH;
            """;
    }

    private const string ProductReportRankingQuantityColumns = """
        ,
                        SUM(CASE WHEN [Period] = 0 THEN [TotalQuantity] ELSE 0 END) AS [CurrentQuantity],
                        SUM(CASE WHEN [Period] = 1 THEN [TotalQuantity] ELSE 0 END) AS [CompareQuantity]
        """;

    /// <summary>
    /// 排名阶段的 ORDER BY：本期值 → 同期值 → 商品编码。只从固定片段中选择，排序参数不会拼进 SQL；
    /// 均价口径与 AverageUnitPrice 一致，数量不大于 0 时按 0。
    /// </summary>
    internal static string BuildProductReportRankingOrderBy(ProductReportSort sort)
    {
        var direction = sort.Ascending ? "ASC" : "DESC";
        return sort.Field switch
        {
            ProductReportSortField.Quantity =>
                $"a.[CurrentQuantity] {direction}, a.[CompareQuantity] {direction}, a.[ProductCode] ASC",
            ProductReportSortField.UnitPrice =>
                $"CASE WHEN a.[CurrentQuantity] > 0 THEN a.[CurrentSalesAmount] / a.[CurrentQuantity] ELSE 0 END {direction}, "
                + $"CASE WHEN a.[CompareQuantity] > 0 THEN a.[CompareSalesAmount] / a.[CompareQuantity] ELSE 0 END {direction}, "
                + "a.[ProductCode] ASC",
            _ => $"a.[CurrentSalesAmount] {direction}, a.[CompareSalesAmount] {direction}, a.[ProductCode] ASC",
        };
    }

    /// <summary>
    /// SqlSugar 快速路径的均价排序片段（不含商品编码兜底）。
    /// SQLite 会把 decimal 常量参数按 TEXT 绑定、SUM/SUM 又是整数除法，所以用常量片段并乘 1.0 强制实数除法；
    /// SQL Server 上仍是 decimal 运算，结果与路径 A 一致。
    /// </summary>
    internal static string BuildFastPathUnitPriceOrderBy(bool ascending)
    {
        var direction = ascending ? "ASC" : "DESC";
        return $"CASE WHEN [CurrentQuantity] > 0 THEN [CurrentSalesAmount] * 1.0 / [CurrentQuantity] ELSE 0 END {direction}, "
            + $"CASE WHEN [CompareQuantity] > 0 THEN [CompareSalesAmount] * 1.0 / [CompareQuantity] ELSE 0 END {direction}";
    }

    private static string BuildProductReportSourceCtes(bool includeCompare, bool pageOnly)
    {
        // 排名和本页明细共用同一组来源条件，确保筛选、同期独有商品和中国供应商 200 映射口径一致。
        var sourceNames = new List<string>();
        var sourceCtes = new List<string>();
        foreach (var compare in includeCompare ? new[] { false, true } : new[] { false })
        {
            var period = compare ? "Compare" : "Current";
            var name = period + "Source";
            sourceNames.Add(name);
            sourceCtes.Add($"""
                {name} AS
                (
                    SELECT s.[ProductCode], s.[ProductName], s.[TotalQuantity], s.[TotalAmount],
                        s.[OrderCount], s.[TotalCost], s.[GrossProfit], CAST({(compare ? 1 : 0)} AS int) AS [Period]
                    FROM [dbo].[ProductStoreDailySalesStatistic] AS s
                    {(pageOnly ? "INNER JOIN #ProductReportPage AS requested ON requested.[ProductCode] = s.[ProductCode] AND requested.[HasData] = 1" : string.Empty)}
                    WHERE s.[Date] >= @{period}Start AND s.[Date] < @{period}EndExclusive
                    {BuildSourceFilters()}
                )
                """);
        }
        return """
            ;WITH SearchProducts AS
            (
                SELECT [ProductCode] FROM #ProductReportSearchProducts
            ),
            BranchFilter AS
            (
                SELECT [BranchCode] FROM #ProductReportBranchFilter
            ),
            LocalSupplierFilter AS
            (
                SELECT [SupplierCode] FROM #ProductReportLocalFilter
            ),
            ChinaSupplierFilter AS
            (
                SELECT [SupplierCode] FROM #ProductReportChinaFilter
            ),
            ChinaProductMap AS
            (
                SELECT [ProductCode], [ChinaSupplierCode] FROM #ProductReportChinaMap
            ),
            """ + string.Join(",\n", sourceCtes) + """
            , PeriodRows AS
            (
            """ + string.Join("\nUNION ALL ", sourceNames.Select(name => "SELECT * FROM " + name)) + "\n)";
    }

    private static string BuildSourceFilters()
    {
        const string chinaFilter = """
                  AND
                  (
                      @HasChinaFilter = 0
                      OR EXISTS
                      (
                          SELECT 1 FROM ChinaSupplierFilter AS supplier
                          WHERE supplier.[SupplierCode] = s.[SupplierCode]
                      )
                      OR
                      (
                          s.[SupplierCode] = N'200'
                          AND EXISTS
                          (
                              SELECT 1
                              FROM ChinaProductMap AS mapping
                              WHERE mapping.[ProductCode] = s.[ProductCode]

                          )
                      )
                  )
                """;

        return """
            AND
            (
                @HasBranchFilter = 0
                OR EXISTS
                (
                    SELECT 1 FROM BranchFilter AS branch
                    WHERE branch.[BranchCode] = s.[BranchCode]
                )
            )
            AND
            (
                @HasLocalFilter = 0
                OR EXISTS
                (
                    SELECT 1 FROM LocalSupplierFilter AS supplier
                    WHERE supplier.[SupplierCode] = s.[SupplierCode]
                )
            )
            """ + chinaFilter + """
            AND
            (
                @ProductSearch IS NULL
                OR EXISTS
                (
                    SELECT 1 FROM SearchProducts AS product
                    WHERE product.[ProductCode] = s.[ProductCode]
                )
                OR s.[Barcode] LIKE N'%' + @ProductSearch + N'%' ESCAPE N'\'
            )
        """;
    }

    internal static PagedSalesProductDetailWithDiscountDto MapProductReportPagingRows(
        IEnumerable<ProductReportPagingSqlRow> rows,
        int pageIndex,
        int pageSize
    )
    {
        var rowList = rows.ToList();
        return new PagedSalesProductDetailWithDiscountDto
        {
            Data = rowList
                .Where(row => row.HasData && !string.IsNullOrWhiteSpace(row.ProductCode))
                .Select(ToSalesProductDetailWithDiscount)
                .ToList(),
            Total = rowList.FirstOrDefault()?.TotalCount ?? 0,
            PageIndex = pageIndex,
            PageSize = pageSize,
        };
    }

    internal static SalesProductDetailWithDiscountDto ToSalesProductDetailWithDiscount(
        ProductReportPagingSqlRow row
    )
    {
        var result = new SalesProductDetailWithDiscountDto
        {
            ProductCode = row.ProductCode ?? string.Empty,
            ItemNumber = row.ItemNumber,
            ProductImage = row.ProductImage,
            ProductName = row.CurrentProductName ?? row.CompareProductName,
            Quantity = row.CurrentQuantity,
            DiscountedQuantity = 0,
            SalesAmount = row.CurrentSalesAmount,
            AverageUnitPrice = row.CurrentQuantity > 0 ? row.CurrentSalesAmount / row.CurrentQuantity : 0,
            AverageOriginalPrice = null,
            OrderCount = row.CurrentOrderCount,
            CostStatus = GetCostStatus(
                row.CurrentStatisticRowCount,
                row.CurrentCostedRowCount,
                row.CurrentGrossProfitRowCount
            ),
            GrossProfit = GetCompleteGrossProfit(
                row.CurrentGrossProfit,
                row.CurrentStatisticRowCount,
                row.CurrentCostedRowCount,
                row.CurrentGrossProfitRowCount
            ),
            QuantityLY = row.CompareQuantity,
            DiscountedQuantityLY = 0,
            SalesAmountLY = row.CompareSalesAmount,
            AverageUnitPriceLY = row.CompareQuantity > 0 ? row.CompareSalesAmount / row.CompareQuantity : 0,
            AverageOriginalPriceLY = null,
            OrderCountLY = row.CompareOrderCount,
            CompareCostStatus = GetCostStatus(
                row.CompareStatisticRowCount,
                row.CompareCostedRowCount,
                row.CompareGrossProfitRowCount
            ),
            GrossProfitLY = GetCompleteGrossProfit(
                row.CompareGrossProfit,
                row.CompareStatisticRowCount,
                row.CompareCostedRowCount,
                row.CompareGrossProfitRowCount
            ),
        };
        result.GrossMarginRate = CalculateGrossMarginRate(result.SalesAmount, result.GrossProfit);
        result.GrossMarginRateLY = CalculateGrossMarginRate(result.SalesAmountLY, result.GrossProfitLY);
        return result;
    }

    private static PagedSalesProductDetailWithDiscountDto CreateEmptyProductPagingResult(int pageIndex, int pageSize)
    {
        return new PagedSalesProductDetailWithDiscountDto
        {
            Data = new List<SalesProductDetailWithDiscountDto>(),
            Total = 0,
            PageIndex = pageIndex,
            PageSize = pageSize,
        };
    }

    internal static string EscapeLikeValue(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);
    }
}
