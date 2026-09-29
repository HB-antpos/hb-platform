using System.Text;
using System.Text.Json;
using BlazorApp.Api.Features.StoreOrders.ProductPicker.Domain;
using BlazorApp.Shared.DTOs;
using SqlSugar;
// 项目全局引用了 SqlSugar，其 DbType 与 ADO.NET 参数类型同名。
using AdoDbType = System.Data.DbType;

namespace BlazorApp.Api.Features.StoreOrders.ProductPicker.Infrastructure;

/// <summary>
/// 仓库在售商品分页的全部筛选条件；语义与 ProductPickerQueryBuilder 的 SqlSugar 写法逐条对应。
/// </summary>
internal sealed record ProductPickerSqlServerPageSpec(
    bool IncludeInactiveWarehouseProducts,
    IReadOnlyList<string>? CategoryIds,
    string? LocalSupplierCode,
    string? SupplierCode,
    ProductPickerSearchFilter Search,
    IReadOnlyList<string> LocationProductCodes,
    IReadOnlyList<string> Grades,
    StoreOrderProductColumnFiltersDto? ColumnFilters,
    string? SortBy,
    bool SortDescending,
    int PageNumber,
    int PageSize
);

internal sealed record ProductPickerSqlServerPageCommand(
    string PageSql,
    string CountSql,
    IReadOnlyList<ProductPickerSqlServerParameter> Parameters,
    long Skip
);

internal sealed record ProductPickerSqlServerParameter(
    string Name,
    object? Value,
    AdoDbType DbType,
    int? Size = null
);

internal sealed class ProductPickerSqlServerPageResult
{
    public List<StoreOrderProductDto> Items { get; init; } = new();

    public int Total { get; init; }

    /// <summary>当前页为空且不是第一页时，额外执行了一次计数兜底。</summary>
    public bool UsedCountFallback { get; init; }
}

/// <summary>
/// SQL Server 上的订货商品分页原生查询（2026-09-23/24 生产只读实测）：
/// - 旧写法从 17 万行 Product 出发全表扫描再哈希连接约 3.8 千行在售 WarehouseProduct，
///   计数、取页键、回查明细三条 SQL 各扫一遍，关键字约 0.5–0.8 秒/次；
/// - 现在从 WarehouseProduct 出发 LOOP JOIN 按商品编码定位 Product，计数用 COUNT(1) OVER() 与分页合成一条；
///   不加连接提示时优化器仍会选回全表扫描（实测约 0.5 秒），所以连接提示是必需的；
/// - 连接提示会固定连接顺序，但只涉及 Product 的半连接（EXISTS / IN 子查询）仍会被下推进 LOOP JOIN 的内侧，
///   与 Product 先连接再整表假脱机，逐行重扫（实测 1–2.5 秒）。因此凡是要查其它表的条件
///   （等级、国内供应商、供应商关键字、货位）都写成排在 Product 之后的显式 HASH JOIN 派生表，
///   派生表内 DISTINCT 按库排序规则去重，与 EXISTS / IN 的半连接语义一致且不放大行数；
/// - 分类、等级是普通 IN 谓词，用参数列表传入并按 2 的幂补齐个数（重复最后一个值，不改变 IN 的集合语义），
///   SQL 文本只随“档位”变化，全部分类最多约 10 份计划；OPENJSON 派生表在这里实测更慢（哈希内存授予、偶发溢出）；
/// - 删除标记写字面量 IsDeleted = 0，与旧的 NOT(IsDeleted = 1) 在 bit 列上真值表一致（NULL 两者都排除）；
/// - 关键字仍保留 LOWER(...) LIKE：连接顺序改对后只对约 3.8 千行做匹配，去掉 LOWER 实测只省 0–5 ms，
///   保留可确保与旧写法的匹配语义逐字符一致。
/// </summary>
internal static class ProductPickerSqlServerPageQuery
{
    private const int ScalarStringSize = 4000;

    internal static bool IsSupported(ISqlSugarClient db) =>
        db.CurrentConnectionConfig.DbType == DbType.SqlServer;

    internal static async Task<ProductPickerSqlServerPageResult> QueryAsync(
        ISqlSugarClient db,
        ProductPickerSqlServerPageSpec spec
    )
    {
        var command = Build(spec, ResolveNoLockHint(db));
        var rows = await db.Ado.SqlQueryAsync<ProductPickerSqlServerPageRow>(
            command.PageSql,
            ToSugarParameters(command.Parameters)
        );

        int total;
        var usedCountFallback = false;
        if (rows.Count > 0)
        {
            total = rows[0].TotalCount;
        }
        else if (command.Skip == 0)
        {
            total = 0;
        }
        else
        {
            // 越界页取不到 COUNT(1) OVER() 的值，但旧接口仍返回准确 Total，这里单独补一次计数。
            total = await db.Ado.GetIntAsync(
                command.CountSql,
                ToSugarParameters(command.Parameters)
            );
            usedCountFallback = true;
        }

        return new ProductPickerSqlServerPageResult
        {
            Items = rows.Select(MapRow).ToList(),
            Total = total,
            UsedCountFallback = usedCountFallback,
        };
    }

    internal static ProductPickerSqlServerPageCommand Build(
        ProductPickerSqlServerPageSpec spec,
        string noLockHint
    )
    {
        var parameters = new List<ProductPickerSqlServerParameter>();
        string AddString(string name, string value)
        {
            // 固定声明长度，保证同一形态的 SQL 复用同一份计划；超长值退回 nvarchar(max) 避免截断。
            parameters.Add(new ProductPickerSqlServerParameter(
                name,
                value,
                AdoDbType.String,
                value.Length > ScalarStringSize ? -1 : ScalarStringSize
            ));
            return name;
        }

        string AddStringList(string prefix, IReadOnlyList<string> values)
        {
            // 个数按 2 的幂补齐（重复最后一个值），IN 的集合语义不变，SQL 文本只随档位变化，计划数量有上界。
            var paddedCount = 1;
            while (paddedCount < values.Count)
            {
                paddedCount *= 2;
            }

            return string.Join(
                ", ",
                Enumerable.Range(0, paddedCount)
                    .Select(index => AddString(prefix + index, values[Math.Min(index, values.Count - 1)]))
            );
        }

        string AddJsonList(string name, IEnumerable<string> values)
        {
            parameters.Add(new ProductPickerSqlServerParameter(
                name,
                JsonSerializer.Serialize(values),
                AdoDbType.String,
                -1
            ));
            return name;
        }

        string AddInt(string name, int value)
        {
            parameters.Add(new ProductPickerSqlServerParameter(name, value, AdoDbType.Int32));
            return name;
        }

        string AddDecimal(string name, decimal value)
        {
            parameters.Add(new ProductPickerSqlServerParameter(name, value, AdoDbType.Decimal));
            return name;
        }

        var from = new StringBuilder();
        from.Append("FROM [WarehouseProduct] wp").Append(noLockHint).AppendLine();
        // 连接两侧类型不同（varchar(255) 与 nvarchar(50)），显式转换到不截断的 nvarchar(255)，
        // 与旧写法的隐式转换等价，且让 Product 一侧按商品编码走索引定位。
        from.Append("INNER LOOP JOIN [Product] p")
            .Append(noLockHint)
            .AppendLine(" ON p.[ProductCode] = CAST(wp.[ProductCode] AS nvarchar(255))");

        if (spec.Grades.Count > 0)
        {
            var gradeParameters = AddStringList("@Grade", spec.Grades);
            from.Append("INNER HASH JOIN (SELECT DISTINCT g.[ProductCode] FROM [ProductGrade] g")
                .Append(noLockHint)
                .Append(" WHERE g.[IsDeleted] = 0 AND g.[Grade] IN (")
                .Append(gradeParameters)
                .AppendLine(")) gradeFilter ON gradeFilter.[ProductCode] = p.[ProductCode]");
        }

        if (!string.IsNullOrEmpty(spec.SupplierCode))
        {
            var supplierParameter = AddString("@SupplierCode", spec.SupplierCode);
            from.Append("INNER HASH JOIN (SELECT DISTINCT dp.[ProductCode] FROM [DomesticProduct] dp")
                .Append(noLockHint)
                .Append(" WHERE dp.[SupplierCode] = ")
                .Append(supplierParameter)
                .AppendLine(" AND dp.[IsDeleted] = 0) domesticFilter ON domesticFilter.[ProductCode] = p.[ProductCode]");
        }

        var hasLocationCodes = spec.LocationProductCodes.Count > 0;
        if (hasLocationCodes)
        {
            // 货位命中的商品数不固定，用 JSON 数组参数；它只在 OR 条件里使用，LEFT JOIN 后判断是否命中。
            var locationParameter = AddJsonList(
                "@LocationProductCodesJson",
                spec.LocationProductCodes
            );
            from.Append("LEFT HASH JOIN (SELECT DISTINCT [value] AS [ProductCode] FROM OPENJSON(")
                .Append(locationParameter)
                .AppendLine(")) locationFilter ON locationFilter.[ProductCode] = p.[ProductCode]");
        }

        var supplierKeyword = NormalizeColumnFilterText(spec.ColumnFilters?.SupplierKeyword);
        if (supplierKeyword != null)
        {
            // 旧写法：EXISTS 国内商品（未删除、供应商未删除且启用且编号/名称/店号包含关键字），
            // 并按 商品编码 / 货号=HBProductNo / 条码 三种方式之一关联。拆成三个去重派生表分别左连接，
            // 任一命中即等价于 EXISTS。
            var keyword = AddString("@ColumnSupplierKeyword", supplierKeyword);
            var source =
                "FROM [ChinaSupplier] cs" + noLockHint
                + " INNER LOOP JOIN [DomesticProduct] dps" + noLockHint
                + " ON dps.[SupplierCode] = cs.[SupplierCode]"
                + " WHERE cs.[IsDeleted] = 0 AND cs.[Status] = 1 AND dps.[IsDeleted] = 0 AND ("
                + ContainsCondition("cs.[SupplierCode]", keyword) + " OR "
                + ContainsCondition("cs.[SupplierName]", keyword) + " OR "
                + ContainsCondition("cs.[ShopNumber]", keyword) + ")";
            from.Append("LEFT HASH JOIN (SELECT DISTINCT dps.[ProductCode] ")
                .Append(source)
                .AppendLine(") supplierByCode ON supplierByCode.[ProductCode] = p.[ProductCode]");
            from.Append("LEFT HASH JOIN (SELECT DISTINCT dps.[HBProductNo] ")
                .Append(source)
                .AppendLine(" AND dps.[HBProductNo] IS NOT NULL) supplierByItem ON supplierByItem.[HBProductNo] = p.[ItemNumber]");
            from.Append("LEFT HASH JOIN (SELECT DISTINCT dps.[Barcode] ")
                .Append(source)
                .AppendLine(" AND dps.[Barcode] IS NOT NULL) supplierByBarcode ON supplierByBarcode.[Barcode] = p.[Barcode]");
        }

        var where = new List<string>
        {
            "wp.[IsDeleted] = 0",
            "p.[IsDeleted] = 0",
        };
        if (!spec.IncludeInactiveWarehouseProducts)
        {
            where.Add("p.[IsActive] = 1");
            where.Add("wp.[IsActive] = 1");
        }

        if (spec.CategoryIds != null)
        {
            where.Add(
                "p.[WarehouseCategoryGUID] IS NOT NULL AND p.[WarehouseCategoryGUID] IN ("
                    + AddStringList("@CategoryId", spec.CategoryIds)
                    + ")"
            );
        }

        if (!string.IsNullOrEmpty(spec.LocalSupplierCode))
        {
            where.Add("p.[LocalSupplierCode] = " + AddString("@LocalSupplierCode", spec.LocalSupplierCode));
        }

        var searchCondition = BuildSearchCondition(spec.Search, hasLocationCodes, AddString);
        if (searchCondition != null)
        {
            where.Add(searchCondition);
        }

        where.AddRange(BuildColumnFilterConditions(
            spec.ColumnFilters,
            AddString,
            AddInt,
            AddDecimal
        ));

        var skip = (long)(Math.Max(spec.PageNumber, 1) - 1) * Math.Max(spec.PageSize, 1);
        var take = Math.Max(spec.PageSize, 1);
        parameters.Add(new ProductPickerSqlServerParameter("@Skip", skip, AdoDbType.Int64));
        parameters.Add(new ProductPickerSqlServerParameter("@Take", take, AdoDbType.Int32));

        var whereSql = "WHERE " + string.Join(Environment.NewLine + "  AND ", where);
        var orderBy = BuildOrderBy(spec.SortBy, spec.SortDescending);

        // 内层只取筛选、排序所需列（多数由商品编码索引覆盖），分页后再按主键回查展示列与分类/供应商名，
        // 回表只发生在本页行上。外层 ORDER BY RowNo 保证返回顺序与内层分页顺序一致。
        var pageSql = $"""
            SELECT f.[TotalCount], f.[ProductCode], f.[ItemNumber], f.[Barcode], f.[ProductName],
                   pd.[ProductImage], c.[CategoryName], pd.[WarehouseCategoryGUID], pd.[LocalSupplierCode],
                   s.[Name] AS [LocalSupplierName], f.[OEMPrice], f.[MinOrderQuantity], f.[StockQuantity],
                   pd.[MiddlePackageQuantity] AS [PackQty], f.[ImportPrice]
            FROM (
                SELECT p.[UUID], ISNULL(p.[ProductCode], N'') AS [ProductCode], p.[ItemNumber], p.[Barcode], p.[ProductName],
                       wp.[OEMPrice], ISNULL(wp.[MinOrderQuantity], 1) AS [MinOrderQuantity],
                       ISNULL(wp.[StockQuantity], 0) AS [StockQuantity], wp.[ImportPrice],
                       COUNT(1) OVER () AS [TotalCount],
                       ROW_NUMBER() OVER (ORDER BY {orderBy}) AS [RowNo]
                {from}{whereSql}
                ORDER BY {orderBy}
                OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY
            ) f
            INNER LOOP JOIN [Product] pd{noLockHint} ON pd.[UUID] = f.[UUID]
            LEFT LOOP JOIN [WarehouseCategory] c{noLockHint} ON pd.[WarehouseCategoryGUID] = c.[CategoryGUID]
            LEFT LOOP JOIN [LocalSupplier] s{noLockHint} ON pd.[LocalSupplierCode] = s.[LocalSupplierCode] AND s.[IsDeleted] = 0
            ORDER BY f.[RowNo]
            """;
        var countSql = $"""
            SELECT COUNT(1)
            {from}{whereSql}
            """;

        return new ProductPickerSqlServerPageCommand(pageSql, countSql, parameters, skip);
    }

    internal static string ResolveNoLockHint(ISqlSugarClient db)
    {
        // 与 SqlSugar 生成 SQL 的规则一致：开启 IsWithNoLockQuery 时加 NOLOCK，事务内按配置去掉。
        var settings = db.CurrentConnectionConfig.MoreSettings;
        if (settings?.IsWithNoLockQuery != true)
        {
            return string.Empty;
        }

        if (settings.DisableWithNoLockWithTran && db.Ado.Transaction != null)
        {
            return string.Empty;
        }

        return " WITH(NOLOCK)";
    }

    internal static string BuildOrderBy(string? sortBy, bool sortDescending)
    {
        // 与 ProductPickerQueryBuilder.ApplyWarehouseProductSort 的白名单逐项对应；末尾商品编码保证全序。
        var normalized = (sortBy ?? "default").Trim().ToLower();
        var direction = sortDescending ? "DESC" : "ASC";
        return normalized switch
        {
            "priceasc" => "wp.[OEMPrice] ASC, p.[ProductCode] ASC",
            "pricedesc" => "wp.[OEMPrice] DESC, p.[ProductCode] ASC",
            "name" => "p.[ProductName] ASC, p.[ProductCode] ASC",
            "productname" => $"p.[ProductName] {direction}, p.[ProductCode] ASC",
            "barcode" => $"p.[Barcode] {direction}, p.[ProductCode] ASC",
            "stockquantity" => $"ISNULL(wp.[StockQuantity], 0) {direction}, p.[ProductCode] ASC",
            "minorderquantity" => $"ISNULL(wp.[MinOrderQuantity], 1) {direction}, p.[ProductCode] ASC",
            "importprice" => $"ISNULL(wp.[ImportPrice], 0) {direction}, p.[ProductCode] ASC",
            "itemnumber" => $"p.[ItemNumber] {direction}, p.[ProductCode] ASC",
            _ => "p.[ItemNumber] ASC, p.[ProductCode] ASC",
        };
    }

    private static string? BuildSearchCondition(
        ProductPickerSearchFilter search,
        bool hasLocationCodes,
        Func<string, string, string> addString
    )
    {
        // 分支与 ProductPickerQueryBuilder.ApplyWarehouseProductSearch 完全对应：
        // 有货位命中时货位商品与关键字条件取 OR；否则按统一/拆分关键字组合。
        const string location = "locationFilter.[ProductCode] IS NOT NULL";
        static string Contains(string column, string parameter) => ContainsCondition(column, parameter);

        if (!string.IsNullOrWhiteSpace(search.UnifiedKeyword))
        {
            var keyword = addString("@Keyword", search.UnifiedKeyword);
            var keywordCondition =
                $"{Contains("p.[ItemNumber]", keyword)} OR {Contains("p.[Barcode]", keyword)} OR {Contains("p.[ProductName]", keyword)}";
            return hasLocationCodes
                ? $"({location} OR {keywordCondition})"
                : $"({keywordCondition})";
        }

        var hasItemOrBarcode = !string.IsNullOrWhiteSpace(search.ItemOrBarcodeKeyword);
        var hasProductName = !string.IsNullOrWhiteSpace(search.ProductNameKeyword);
        string? itemOrBarcodeCondition = null;
        string? productNameCondition = null;
        if (hasItemOrBarcode)
        {
            var keyword = addString("@ItemOrBarcodeKeyword", search.ItemOrBarcodeKeyword!);
            itemOrBarcodeCondition =
                $"({Contains("p.[ItemNumber]", keyword)} OR {Contains("p.[Barcode]", keyword)})";
        }

        if (hasProductName)
        {
            var keyword = addString("@ProductNameKeyword", search.ProductNameKeyword!);
            productNameCondition = Contains("p.[ProductName]", keyword);
        }

        if (hasLocationCodes)
        {
            if (itemOrBarcodeCondition != null && productNameCondition != null)
            {
                return $"({location} OR ({itemOrBarcodeCondition} AND {productNameCondition}))";
            }

            if (itemOrBarcodeCondition != null)
            {
                return $"({location} OR {itemOrBarcodeCondition})";
            }

            if (productNameCondition != null)
            {
                return $"({location} OR {productNameCondition})";
            }

            return $"({location})";
        }

        if (itemOrBarcodeCondition != null && productNameCondition != null)
        {
            return $"({itemOrBarcodeCondition} AND {productNameCondition})";
        }

        return itemOrBarcodeCondition ?? productNameCondition;
    }

    private static IEnumerable<string> BuildColumnFilterConditions(
        StoreOrderProductColumnFiltersDto? filters,
        Func<string, string, string> addString,
        Func<string, int, string> addInt,
        Func<string, decimal, string> addDecimal
    )
    {
        if (filters == null)
        {
            yield break;
        }

        // 与 ProductPickerQueryBuilder.ApplyWarehouseProductColumnFilters 逐项对应。
        static string Contains(string column, string parameter) => ContainsCondition(column, parameter);

        var itemNumber = NormalizeColumnFilterText(filters.ItemNumber);
        if (itemNumber != null)
        {
            yield return Contains("p.[ItemNumber]", addString("@ColumnItemNumber", itemNumber));
        }

        var productName = NormalizeColumnFilterText(filters.ProductName);
        if (productName != null)
        {
            yield return Contains("p.[ProductName]", addString("@ColumnProductName", productName));
        }

        var barcode = NormalizeColumnFilterText(filters.Barcode);
        if (barcode != null)
        {
            yield return Contains("p.[Barcode]", addString("@ColumnBarcode", barcode));
        }

        if (NormalizeColumnFilterText(filters.SupplierKeyword) != null)
        {
            // 三个供应商派生表已在 FROM 中左连接，这里只判断是否任一命中。
            yield return "(supplierByCode.[ProductCode] IS NOT NULL OR supplierByItem.[HBProductNo] IS NOT NULL OR supplierByBarcode.[Barcode] IS NOT NULL)";
        }

        if (filters.StockQuantityMin.HasValue)
        {
            yield return "ISNULL(wp.[StockQuantity], 0) >= "
                + addInt("@StockQuantityMin", filters.StockQuantityMin.Value);
        }

        if (filters.StockQuantityMax.HasValue)
        {
            yield return "ISNULL(wp.[StockQuantity], 0) <= "
                + addInt("@StockQuantityMax", filters.StockQuantityMax.Value);
        }

        if (filters.MinOrderQuantityMin.HasValue)
        {
            yield return "ISNULL(wp.[MinOrderQuantity], 1) >= "
                + addInt("@MinOrderQuantityMin", filters.MinOrderQuantityMin.Value);
        }

        if (filters.MinOrderQuantityMax.HasValue)
        {
            yield return "ISNULL(wp.[MinOrderQuantity], 1) <= "
                + addInt("@MinOrderQuantityMax", filters.MinOrderQuantityMax.Value);
        }

        if (filters.ImportPriceMin.HasValue)
        {
            yield return "(wp.[ImportPrice] IS NOT NULL AND wp.[ImportPrice] >= "
                + addDecimal("@ImportPriceMin", filters.ImportPriceMin.Value) + ")";
        }

        if (filters.ImportPriceMax.HasValue)
        {
            yield return "(wp.[ImportPrice] IS NOT NULL AND wp.[ImportPrice] <= "
                + addDecimal("@ImportPriceMax", filters.ImportPriceMax.Value) + ")";
        }
    }

    /// <summary>与 SqlSugar 对 x.ToLower().Contains(k) 的翻译一致：LOWER(列) LIKE '%' + @k + '%'（不转义通配符）。</summary>
    private static string ContainsCondition(string column, string parameter) =>
        $"({column} IS NOT NULL AND LOWER({column}) LIKE N'%' + {parameter} + N'%')";

    private static string? NormalizeColumnFilterText(string? value)
    {
        var trimmed = value?.Trim().ToLower();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static List<SugarParameter> ToSugarParameters(
        IReadOnlyList<ProductPickerSqlServerParameter> parameters
    )
    {
        // 每次执行都新建参数对象，计数兜底与取页不共享可变的 ADO 参数实例。
        return parameters
            .Select(parameter =>
            {
                var sugarParameter = new SugarParameter(parameter.Name, parameter.Value)
                {
                    DbType = parameter.DbType,
                };
                if (parameter.Size.HasValue)
                {
                    sugarParameter.Size = parameter.Size.Value;
                }

                return sugarParameter;
            })
            .ToList();
    }

    private static StoreOrderProductDto MapRow(ProductPickerSqlServerPageRow row)
    {
        return new StoreOrderProductDto
        {
            ProductCode = row.ProductCode ?? string.Empty,
            ItemNumber = row.ItemNumber,
            Barcode = row.Barcode,
            ProductName = row.ProductName,
            ProductImage = row.ProductImage,
            CategoryName = row.CategoryName,
            WarehouseCategoryGUID = row.WarehouseCategoryGUID,
            LocalSupplierCode = row.LocalSupplierCode,
            LocalSupplierName = row.LocalSupplierName,
            OEMPrice = row.OEMPrice,
            MinOrderQuantity = row.MinOrderQuantity,
            StockQuantity = row.StockQuantity,
            PackQty = row.PackQty,
            ImportPrice = row.ImportPrice,
        };
    }

    private sealed class ProductPickerSqlServerPageRow
    {
        public int TotalCount { get; set; }

        public string? ProductCode { get; set; }

        public string? ItemNumber { get; set; }

        public string? Barcode { get; set; }

        public string? ProductName { get; set; }

        public string? ProductImage { get; set; }

        public string? CategoryName { get; set; }

        public string? WarehouseCategoryGUID { get; set; }

        public string? LocalSupplierCode { get; set; }

        public string? LocalSupplierName { get; set; }

        public decimal? OEMPrice { get; set; }

        public int MinOrderQuantity { get; set; }

        public int StockQuantity { get; set; }

        public int? PackQty { get; set; }

        public decimal? ImportPrice { get; set; }
    }
}
