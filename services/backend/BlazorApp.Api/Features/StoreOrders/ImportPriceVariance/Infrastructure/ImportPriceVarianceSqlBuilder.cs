using System.Text;
using BlazorApp.Api.Features.StoreOrders.ImportPriceVariance.Domain;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.ImportPriceVariance.Infrastructure;

internal sealed class ImportPriceVarianceSqlBuildResult
{
    internal string SummarySql { get; init; } = string.Empty;

    internal string PagedSql { get; init; } = string.Empty;

    internal string SupplierSummarySql { get; init; } = string.Empty;

    internal List<SugarParameter> Parameters { get; init; } = new();
}

internal static class ImportPriceVarianceSqlBuilder
{
    internal static ImportPriceVarianceSqlBuildResult BuildSummary(
        StoreOrderImportPriceVarianceQueryDto query,
        IReadOnlyList<string> requestedStoreCodes,
        ImportPriceVariancePage page,
        bool isSqlite
    )
    {
        var parameters = new List<SugarParameter>
        {
            new("@Offset", (page.PageNumber - 1) * page.PageSize),
            new("@PageSize", page.PageSize),
        };
        var orderDirection = query.SortDescending ? "DESC" : "ASC";
        var orderExpression = ResolveSummaryOrderExpression(query.SortBy);
        var paginationSql = isSqlite
            ? "LIMIT @PageSize OFFSET @Offset"
            : "OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        var baseSql = BuildBase(
            query,
            requestedStoreCodes,
            parameters,
            isSqlite,
            productCodeFilter: null
        );

        var groupedSql =
            baseSql
            + """
,
GroupedRows AS (
    SELECT
        ProductCode,
        MAX(ItemNumber) AS ItemNumber,
        MAX(ProductName) AS ProductName,
        MAX(ProductImage) AS ProductImage,
        MAX(SupplierCode) AS SupplierCode,
        MAX(SupplierName) AS SupplierName,
        MAX(DomesticPrice) AS DomesticPrice,
        MAX(WarehouseImportPrice) AS WarehouseImportPrice,
        MAX(UnitVolume) AS UnitVolume,
        MAX(PackingQuantity) AS PackingQuantity,
        MAX(FirstContainerImportPrice) AS FirstContainerImportPrice,
        CAST(COALESCE(SUM(AllocQuantity), 0) AS decimal(18, 2)) AS AllocQuantityTotal,
        CAST(COALESCE(SUM(OriginalImportAmount), 0) AS decimal(18, 2)) AS OriginalImportAmountTotal,
        CAST(COALESCE(SUM(BaselineImportAmount), 0) AS decimal(18, 2)) AS BaselineImportAmountTotal,
        CAST(COALESCE(SUM(VarianceAmount), 0) AS decimal(18, 2)) AS VarianceAmountTotal,
        CAST(COUNT(1) AS int) AS DetailCount,
        MAX(FirstContainerCode) AS FirstContainerCode,
        MAX(FirstContainerNumber) AS FirstContainerNumber,
        MAX(FirstContainerDate) AS FirstContainerDate
    FROM FinalRows
    GROUP BY ProductCode
)
""";

        return new ImportPriceVarianceSqlBuildResult
        {
            Parameters = parameters,
            SummarySql =
                groupedSql
                + """
SELECT
    CAST(COUNT(1) AS int) AS TotalRows,
    CAST(COALESCE(SUM(OriginalImportAmountTotal), 0) AS decimal(18, 2)) AS OriginalImportAmountTotal,
    CAST(COALESCE(SUM(BaselineImportAmountTotal), 0) AS decimal(18, 2)) AS BaselineImportAmountTotal,
    CAST(COALESCE(SUM(VarianceAmountTotal), 0) AS decimal(18, 2)) AS VarianceAmountTotal
FROM GroupedRows
""",
            PagedSql =
                groupedSql
                + $"""
SELECT
    ProductCode,
    ItemNumber,
    ProductName,
    ProductImage,
    SupplierCode,
    SupplierName,
    DomesticPrice,
    WarehouseImportPrice,
    UnitVolume,
    PackingQuantity,
    FirstContainerImportPrice,
    AllocQuantityTotal,
    OriginalImportAmountTotal,
    BaselineImportAmountTotal,
    VarianceAmountTotal,
    DetailCount,
    FirstContainerCode,
    FirstContainerNumber,
    FirstContainerDate
FROM GroupedRows
ORDER BY {orderExpression} {orderDirection}, ProductCode ASC
{paginationSql}
""",
            SupplierSummarySql =
                baseSql
                + """
,
SupplierRows AS (
    SELECT
        SupplierCode,
        SupplierName,
        CAST(COUNT(DISTINCT ProductCode) AS int) AS ProductCount,
        CAST(COUNT(1) AS int) AS DetailCount,
        CAST(COALESCE(SUM(OriginalImportAmount), 0) AS decimal(18, 2)) AS OriginalImportAmountTotal,
        CAST(COALESCE(SUM(BaselineImportAmount), 0) AS decimal(18, 2)) AS BaselineImportAmountTotal,
        CAST(COALESCE(SUM(CASE WHEN VarianceAmount > 0 THEN VarianceAmount ELSE 0 END), 0) AS decimal(18, 2)) AS IncreaseVarianceAmountTotal,
        CAST(COALESCE(SUM(CASE WHEN VarianceAmount < 0 THEN -VarianceAmount ELSE 0 END), 0) AS decimal(18, 2)) AS DecreaseVarianceAmountTotal,
        CAST(COALESCE(SUM(VarianceAmount), 0) AS decimal(18, 2)) AS VarianceAmountTotal
    FROM FinalRows
    GROUP BY SupplierCode, SupplierName
)
SELECT
    SupplierCode,
    SupplierName,
    ProductCount,
    DetailCount,
    OriginalImportAmountTotal,
    BaselineImportAmountTotal,
    IncreaseVarianceAmountTotal,
    DecreaseVarianceAmountTotal,
    VarianceAmountTotal
FROM SupplierRows
-- 返回当前筛选条件下全部供应商，页面本地分页和排序。
ORDER BY ABS(VarianceAmountTotal) DESC, SupplierCode ASC
""",
        };
    }

    internal static ImportPriceVarianceSqlBuildResult BuildDetails(
        StoreOrderImportPriceVarianceDetailQueryDto query,
        string productCode,
        IReadOnlyList<string> requestedStoreCodes,
        ImportPriceVariancePage page,
        bool isSqlite
    )
    {
        var parameters = new List<SugarParameter>
        {
            new("@Offset", (page.PageNumber - 1) * page.PageSize),
            new("@PageSize", page.PageSize),
        };
        var orderBy = BuildDetailOrderBy(query.SortBy, query.SortDescending);
        var paginationSql = isSqlite
            ? "LIMIT @PageSize OFFSET @Offset"
            : "OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        var baseSql = BuildBase(
            query,
            requestedStoreCodes,
            parameters,
            isSqlite,
            productCodeFilter: productCode
        );

        return new ImportPriceVarianceSqlBuildResult
        {
            Parameters = parameters,
            SummarySql =
                baseSql
                + """
SELECT
    CAST(COUNT(1) AS int) AS TotalRows,
    CAST(COALESCE(SUM(OriginalImportAmount), 0) AS decimal(18, 2)) AS OriginalImportAmountTotal,
    CAST(COALESCE(SUM(BaselineImportAmount), 0) AS decimal(18, 2)) AS BaselineImportAmountTotal,
    CAST(COALESCE(SUM(VarianceAmount), 0) AS decimal(18, 2)) AS VarianceAmountTotal
FROM FinalRows
""",
            PagedSql =
                baseSql
                + $"""
SELECT
    OrderGUID,
    DetailGUID,
    OrderNo,
    OrderDate,
    StoreCode,
    StoreName,
    ProductCode,
    ItemNumber,
    ProductName,
    OrderImportPrice,
    FirstContainerImportPrice,
    AllocQuantity,
    OriginalImportAmount,
    BaselineImportAmount,
    VarianceAmount,
    FirstContainerCode,
    FirstContainerNumber,
    FirstContainerDate
FROM FinalRows
ORDER BY {orderBy}
{paginationSql}
""",
        };
    }

    private static string BuildBase(
        StoreOrderImportPriceVarianceQueryDto query,
        IReadOnlyList<string> requestedStoreCodes,
        List<SugarParameter> parameters,
        bool isSqlite,
        string? productCodeFilter
    )
    {
        var rowFilters = new StringBuilder();

        if (requestedStoreCodes.Count > 0)
        {
            var names = new List<string>();
            for (var index = 0; index < requestedStoreCodes.Count; index += 1)
            {
                var parameterName = $"@StoreCode{index}";
                names.Add(parameterName);
                parameters.Add(new SugarParameter(parameterName, requestedStoreCodes[index]));
            }
            rowFilters.AppendLine($"    AND o.StoreCode IN ({string.Join(", ", names)})");
        }

        if (!string.IsNullOrWhiteSpace(productCodeFilter))
        {
            parameters.Add(new SugarParameter("@ProductCode", productCodeFilter.Trim()));
            rowFilters.AppendLine("    AND d.ProductCode = @ProductCode");
        }

        if (!string.IsNullOrWhiteSpace(query.SupplierCode))
        {
            parameters.Add(new SugarParameter("@SupplierCode", query.SupplierCode.Trim()));
            // 国内供应商过滤只匹配 DomesticProduct.SupplierCode，不能误用 Product.LocalSupplierCode。
            rowFilters.AppendLine("    AND dp.SupplierCode = @SupplierCode");
        }

        if (!string.IsNullOrWhiteSpace(query.OrderNo))
        {
            parameters.Add(new SugarParameter("@OrderNo", $"%{query.OrderNo.Trim()}%"));
            rowFilters.AppendLine("    AND o.OrderNo LIKE @OrderNo");
        }

        if (query.StartDate.HasValue)
        {
            parameters.Add(new SugarParameter("@StartDate", query.StartDate.Value.Date));
            rowFilters.AppendLine("    AND o.OrderDate >= @StartDate");
        }

        if (query.EndDate.HasValue)
        {
            parameters.Add(
                new SugarParameter(
                    "@EndDate",
                    query.EndDate.Value.Date.AddDays(1).AddTicks(-1)
                )
            );
            rowFilters.AppendLine("    AND o.OrderDate <= @EndDate");
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            parameters.Add(new SugarParameter("@Keyword", $"%{query.Keyword.Trim()}%"));
            // 没有「查看国内供应商名称」权限的账号不能按名称搜索：名称这一支用独立参数，无权限时是匹配不到的占位关键词。
            parameters.Add(new SugarParameter(
                "@SupplierNameKeyword",
                $"%{DomesticSupplierInfoMasking.GuardNameKeyword(query.Keyword.Trim())}%"
            ));
            rowFilters.AppendLine(
                """
    AND (
        o.OrderNo LIKE @Keyword
        OR o.StoreCode LIKE @Keyword
        OR s.StoreName LIKE @Keyword
        OR d.ProductCode LIKE @Keyword
        OR p.ItemNumber LIKE @Keyword
        OR dp.HBProductNo LIKE @Keyword
        OR p.ProductName LIKE @Keyword
        OR dp.ProductName LIKE @Keyword
        OR dp.SupplierCode LIKE @Keyword
        OR cs.SupplierName LIKE @SupplierNameKeyword
    )
"""
            );
        }

        var directionFilter = (query.VarianceDirection ?? "all").Trim().ToLowerInvariant() switch
        {
            "increase" => "WHERE VarianceAmount > 0",
            "decrease" => "WHERE VarianceAmount < 0",
            _ => string.Empty,
        };
        var containerNumberLengthFilter = isSqlite
            ? "LENGTH(c.ContainerNumber) > 10"
            : "LEN(c.ContainerNumber) > 10";

        return
            """
WITH FirstContainerRanked AS (
    SELECT
        cd.ProductCode AS ProductCode,
        cd.ImportPrice AS FirstContainerImportPrice,
        c.ContainerCode AS FirstContainerCode,
        c.ContainerNumber AS FirstContainerNumber,
        c.LoadingDate AS FirstContainerDate,
        ROW_NUMBER() OVER (
            PARTITION BY cd.ProductCode
            ORDER BY c.LoadingDate ASC, c.ContainerCode ASC, cd.DetailCode ASC
        ) AS RowNumber
    FROM [ContainerDetail] cd
    INNER JOIN [Container] c ON cd.ContainerCode = c.ContainerCode
    WHERE
        (cd.IsDeleted = 0 OR cd.IsDeleted IS NULL)
        AND (c.IsDeleted = 0 OR c.IsDeleted IS NULL)
        AND cd.ProductCode IS NOT NULL
        AND cd.ProductCode <> ''
        AND cd.ImportPrice IS NOT NULL
        AND cd.ImportPrice > 0
        -- 有效首次货柜必须有长度大于 10 的货柜编号，排除临时或异常短编号。
        AND c.ContainerNumber IS NOT NULL
        AND c.ContainerNumber <> ''
"""
            + $"        AND {containerNumberLengthFilter}\n"
            + """
        AND c.LoadingDate IS NOT NULL
),
FirstContainer AS (
    SELECT
        ProductCode,
        FirstContainerImportPrice,
        FirstContainerCode,
        FirstContainerNumber,
        FirstContainerDate
    FROM FirstContainerRanked
    WHERE RowNumber = 1
),
FilteredRows AS (
    SELECT
        o.OrderGUID AS OrderGUID,
        d.DetailGUID AS DetailGUID,
        o.OrderNo AS OrderNo,
        o.OrderDate AS OrderDate,
        o.StoreCode AS StoreCode,
        s.StoreName AS StoreName,
        d.ProductCode AS ProductCode,
        -- 商品展示字段按确认来源取值；图片空字符串也要回退国内商品图片。
        COALESCE(NULLIF(p.ItemNumber, ''), dp.HBProductNo) AS ItemNumber,
        COALESCE(NULLIF(p.ProductName, ''), dp.ProductName) AS ProductName,
        COALESCE(NULLIF(p.ProductImage, ''), dp.ProductImage) AS ProductImage,
        dp.SupplierCode AS SupplierCode,
        cs.SupplierName AS SupplierName,
        CAST(COALESCE(wp.DomesticPrice, dp.DomesticPrice) AS decimal(18, 2)) AS DomesticPrice,
        CAST(wp.ImportPrice AS decimal(18, 2)) AS WarehouseImportPrice,
        CAST(COALESCE(wp.Volume, dp.UnitVolume) AS decimal(18, 4)) AS UnitVolume,
        COALESCE(wp.PackingQuantity, dp.PackingQuantity) AS PackingQuantity,
        CAST(d.ImportPrice AS decimal(18, 2)) AS OrderImportPrice,
        CAST(fc.FirstContainerImportPrice AS decimal(18, 2)) AS FirstContainerImportPrice,
        CAST(COALESCE(d.AllocQuantity, 0) AS decimal(18, 2)) AS AllocQuantity,
        CAST(COALESCE(d.AllocQuantity, 0) * d.ImportPrice AS decimal(18, 2)) AS OriginalImportAmount,
        CAST(COALESCE(d.AllocQuantity, 0) * fc.FirstContainerImportPrice AS decimal(18, 2)) AS BaselineImportAmount,
        -- 首次有效货柜价是基准价；原始金额按仓库订单进货价乘发货数量计算，不使用明细存储金额。
        CAST(
            (COALESCE(d.AllocQuantity, 0) * d.ImportPrice)
            - (COALESCE(d.AllocQuantity, 0) * fc.FirstContainerImportPrice)
            AS decimal(18, 2)
        ) AS VarianceAmount,
        fc.FirstContainerCode AS FirstContainerCode,
        fc.FirstContainerNumber AS FirstContainerNumber,
        fc.FirstContainerDate AS FirstContainerDate
    FROM [WareHouseOrderDetails] d
    INNER JOIN [WareHouseOrder] o ON d.OrderGUID = o.OrderGUID
    INNER JOIN FirstContainer fc ON d.ProductCode = fc.ProductCode
    LEFT JOIN [Product] p
        ON d.ProductCode = p.ProductCode
        AND (p.IsDeleted = 0 OR p.IsDeleted IS NULL)
    LEFT JOIN [WarehouseProduct] wp
        ON d.ProductCode = wp.ProductCode
        AND (wp.IsDeleted = 0 OR wp.IsDeleted IS NULL)
    LEFT JOIN [DomesticProduct] dp
        ON d.ProductCode = dp.ProductCode
        AND (dp.IsDeleted = 0 OR dp.IsDeleted IS NULL)
    LEFT JOIN [ChinaSupplier] cs
        ON dp.SupplierCode = cs.SupplierCode
        AND (cs.IsDeleted = 0 OR cs.IsDeleted IS NULL)
    LEFT JOIN [Store] s
        ON (o.StoreCode = s.StoreCode OR o.StoreCode = s.StoreGUID)
        AND (s.IsDeleted = 0 OR s.IsDeleted IS NULL)
    WHERE
        (d.IsDeleted = 0 OR d.IsDeleted IS NULL)
        AND (o.IsDeleted = 0 OR o.IsDeleted IS NULL)
        AND d.ProductCode IS NOT NULL
        AND d.ProductCode <> ''
        AND d.ImportPrice IS NOT NULL
        AND d.ImportPrice > 0
        AND o.OrderDate > fc.FirstContainerDate
        AND d.ImportPrice <> fc.FirstContainerImportPrice
        -- 订单价和首次货柜价相差超过 10 倍视为异常数据，不纳入统计。
        AND d.ImportPrice <= fc.FirstContainerImportPrice * 10
        AND fc.FirstContainerImportPrice <= d.ImportPrice * 10
"""
            + rowFilters
            + """
),
FinalRows AS (
    SELECT *
    FROM FilteredRows
"""
            + (string.IsNullOrWhiteSpace(directionFilter) ? string.Empty : $"    {directionFilter}\n")
            + ")\n";
    }

    private static string ResolveSummaryOrderExpression(string? sortBy)
    {
        return (sortBy ?? "absoluteVarianceAmount").Trim().ToLowerInvariant() switch
        {
            "productcode" => "ProductCode",
            "itemnumber" => "ItemNumber",
            "suppliercode" => "SupplierCode",
            // 按被遮掉的国内供应商名称排序会泄露相对顺序，无权限时改按供应商代码排。
            "suppliername" => DomesticSupplierInfoMasking.IsNameSearchAllowed ? "SupplierName" : "SupplierCode",
            "domesticprice" => "DomesticPrice",
            "warehouseimportprice" => "WarehouseImportPrice",
            "unitvolume" => "UnitVolume",
            "packingquantity" => "PackingQuantity",
            "firstcontainerimportprice" => "FirstContainerImportPrice",
            "allocquantitytotal" => "AllocQuantityTotal",
            "originalimportamounttotal" => "OriginalImportAmountTotal",
            "baselineimportamounttotal" => "BaselineImportAmountTotal",
            "varianceamounttotal" => "VarianceAmountTotal",
            "detailcount" => "DetailCount",
            "firstcontainerdate" => "FirstContainerDate",
            _ => "ABS(VarianceAmountTotal)",
        };
    }

    private static string ResolveDetailOrderExpression(string? sortBy)
    {
        return (sortBy ?? "absoluteVarianceAmount").Trim().ToLowerInvariant() switch
        {
            "orderdate" => "OrderDate",
            "orderno" => "OrderNo",
            "storecode" => "StoreCode",
            "productcode" => "ProductCode",
            "itemnumber" => "ItemNumber",
            "orderimportprice" => "OrderImportPrice",
            "firstcontainerimportprice" => "FirstContainerImportPrice",
            "allocquantity" => "AllocQuantity",
            "originalimportamount" => "OriginalImportAmount",
            "baselineimportamount" => "BaselineImportAmount",
            "varianceamount" => "VarianceAmount",
            "firstcontainerdate" => "FirstContainerDate",
            _ => "ABS(VarianceAmount)",
        };
    }

    internal static string BuildDetailOrderBy(string? sortBy, bool sortDescending)
    {
        var orderDirection = sortDescending ? "DESC" : "ASC";
        var terms = new List<(string Expression, string Direction)>
        {
            (ResolveDetailOrderExpression(sortBy), orderDirection),
        };

        void AddFallback(string expression, string direction)
        {
            if (
                terms.Any(term =>
                    string.Equals(
                        term.Expression,
                        expression,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            )
            {
                return;
            }

            terms.Add((expression, direction));
        }

        // SQL Server 不允许 ORDER BY 中重复同一列；二级稳定排序必须按主排序列去重。
        AddFallback("OrderDate", "DESC");
        AddFallback("OrderNo", "DESC");
        AddFallback("DetailGUID", "ASC");

        return string.Join(", ", terms.Select(term => $"{term.Expression} {term.Direction}"));
    }
}
