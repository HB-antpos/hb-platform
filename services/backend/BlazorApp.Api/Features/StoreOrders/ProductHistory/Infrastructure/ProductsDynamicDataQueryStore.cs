using System.Diagnostics;
using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.ProductHistory.Domain;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;
// 项目全局引用了 SqlSugar，其 DbType 与 ADO.NET 参数类型同名。
using AdoDbType = System.Data.DbType;

namespace BlazorApp.Api.Features.StoreOrders.ProductHistory.Infrastructure;

internal sealed class ProductsDynamicDataQueryStore(
    SqlSugarContext context,
    IStoreOrderActorContext actorContext,
    ProductSalesHistoryQueryStore salesHistoryQueryStore,
    ILogger<ProductsDynamicDataQueryStore> logger
)
{
    private readonly ISqlSugarClient _db = context.Db;

    internal async Task<ProductsDynamicDataReadResult> GetProductsDynamicDataAsync(
        ProductsDynamicDataQueryInput input
    )
    {
        ProductHistorySalesContext? salesContext = null;
        if (input.IncludeSales)
        {
            try
            {
                // 门店不存在或停用会在此短路，后续不会查询来货或销售统计。
                salesContext = await salesHistoryQueryStore.GetActiveStoreSalesContextAsync(
                    input.StoreCode
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "GetProductsDynamicDataAsync check store active failed; skip sales-since-last-arrival"
                );
            }
        }

        var cartSw = Stopwatch.StartNew();
        var cartOwnerUserGuid = ResolveActiveCartOwnerUserGuid();
        // 购物车数量保持数据库侧聚合，并严格保留仓库员工独立购物车范围。
        var cartItems = await QueryCartRowsAsync(input, cartOwnerUserGuid);
        cartSw.Stop();

        // 最近订货日期与历史明细合并为一次扫描（旧写法是“先 GROUP BY 取 MAX，再取全部明细”两条 SQL）：
        // 数据库侧用窗口 MAX 只回传最近日期的明细行；内存里仍按旧规则（按商品取最大日期、DateTime 相等）再筛一遍，
        // 后续同订单合并与挑选规则不变，结果与旧写法逐字段一致。
        var latestDateSw = new Stopwatch();
        var historySw = Stopwatch.StartNew();
        var historyRows = await QueryHistoryRowsAsync(input);

        latestDateSw.Start();
        var latestDateMap = historyRows
            .Where(item => !string.IsNullOrWhiteSpace(item.ProductCode))
            .GroupBy(item => item.ProductCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Max(item => item.OrderDate),
                StringComparer.OrdinalIgnoreCase
            );
        latestDateSw.Stop();
        // 同订单同商品先聚合，再按与历史弹窗完全相同的稳定顺序选最新行。
        var historyItems = historyRows
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.ProductCode)
                && !string.IsNullOrWhiteSpace(item.OrderGUID)
                && latestDateMap.TryGetValue(item.ProductCode!, out var latestDate)
                && item.OrderDate == latestDate
            )
            .GroupBy(item => new { item.ProductCode, item.OrderGUID })
            .Select(orderGroup => new ProductHistoryDynamicHistoryRow
            {
                ProductCode = orderGroup.Key.ProductCode,
                OrderGUID = orderGroup.Key.OrderGUID,
                OrderDate = orderGroup.First().OrderDate,
                CreatedAt = orderGroup.First().CreatedAt,
                Quantity = orderGroup.Sum(item => item.Quantity ?? 0m),
                AllocQuantity = orderGroup.Sum(item => item.AllocQuantity ?? 0m),
            })
            .GroupBy(item => item.ProductCode!, StringComparer.OrdinalIgnoreCase)
            .Select(productGroup =>
                productGroup
                    .OrderByDescending(item => item.OrderDate)
                    .ThenByDescending(item => item.CreatedAt)
                    .ThenByDescending(item => item.OrderGUID)
                    .First()
            )
            .ToList();
        historySw.Stop();

        var cartQuantityMap = cartItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ProductCode))
            .GroupBy(item => item.ProductCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => item.CartQuantity ?? 0m),
                StringComparer.OrdinalIgnoreCase
            );
        var latestHistoryMap = historyItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ProductCode))
            .GroupBy(item => item.ProductCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase
            );

        var result = new List<StoreOrderDynamicDataDto>();
        foreach (var productCode in input.ProductCodes)
        {
            var item = new StoreOrderDynamicDataDto { ProductCode = productCode };
            if (cartQuantityMap.TryGetValue(productCode, out var cartQuantity))
            {
                item.CartQuantity = cartQuantity;
            }

            if (latestHistoryMap.TryGetValue(productCode, out var historyItem))
            {
                item.LastOrderDate = historyItem.OrderDate;
                item.LastQuantity = historyItem.Quantity;
                item.LastAllocQuantity = historyItem.AllocQuantity;
            }

            result.Add(item);
        }

        var salesSw = Stopwatch.StartNew();
        var salesRows = 0;
        if (input.IncludeSales && salesContext != null)
        {
            try
            {
                var salesQuantityResult =
                    await salesHistoryQueryStore.GetSalesQuantitySinceLastArrivalMapAsync(
                        salesContext.StoreCode,
                        input.ProductCodes,
                        salesContext.EndDate
                    );
                salesRows = salesQuantityResult.SalesQuantityMap.Count;
                foreach (var item in result)
                {
                    item.SalesQuantitySinceLastArrival =
                        salesQuantityResult.SalesQuantityMap.TryGetValue(
                            item.ProductCode,
                            out var salesQuantity
                        )
                            ? salesQuantity
                            : null;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "GetProductsDynamicDataAsync fill sales-since-last-arrival failed; new field remains null"
                );
            }
        }
        salesSw.Stop();

        return new ProductsDynamicDataReadResult(
            result,
            cartItems.Count,
            latestDateMap.Count,
            historyItems.Count,
            cartSw.ElapsedMilliseconds,
            latestDateSw.ElapsedMilliseconds,
            historySw.ElapsedMilliseconds,
            salesContext != null,
            salesRows,
            salesSw.ElapsedMilliseconds
        );
    }

    private async Task<List<ProductHistoryDynamicCartRow>> QueryCartRowsAsync(
        ProductsDynamicDataQueryInput input,
        string? cartOwnerUserGuid
    )
    {
        var sqlParts = ProductsDynamicDataSql.TryCreate(_db);
        if (sqlParts == null)
        {
            return await QueryCartRowsWithSqlSugarAsync(input, cartOwnerUserGuid);
        }

        var rows = new List<ProductHistoryDynamicCartRow>();
        foreach (var chunk in ProductsDynamicDataSql.Chunk(input.ProductCodes))
        {
            var parameters = sqlParts.CreateParameters(input.StoreCode, chunk, out var productCodeList);
            string cartOwnerCondition;
            if (string.IsNullOrWhiteSpace(cartOwnerUserGuid))
            {
                // 与 SqlFunc.IsNullOrEmpty 的翻译一致。
                cartOwnerCondition = "(o.[CartOwnerUserGuid] IS NULL OR o.[CartOwnerUserGuid] = '')";
            }
            else
            {
                cartOwnerCondition = "o.[CartOwnerUserGuid] = @CartOwnerUserGuid";
                parameters.Add(sqlParts.CreateStringParameter("@CartOwnerUserGuid", cartOwnerUserGuid));
            }

            rows.AddRange(await _db.Ado.SqlQueryAsync<ProductHistoryDynamicCartRow>(
                $"""
                SELECT d.[ProductCode] AS [ProductCode], SUM(d.[Quantity]) AS [CartQuantity]
                {sqlParts.FromDetailsJoinOrders}
                WHERE {ProductsDynamicDataSql.StoreCondition(input.StoreCode)} AND o.[FlowStatus] = 0
                  AND o.[IsDeleted] = 0 AND d.[IsDeleted] = 0
                  AND d.[ProductCode] IS NOT NULL AND d.[ProductCode] IN ({productCodeList})
                  AND {cartOwnerCondition}
                GROUP BY d.[ProductCode]
                """,
                parameters
            ));
        }

        return rows;
    }

    private async Task<List<ProductHistoryDynamicHistoryRow>> QueryHistoryRowsAsync(
        ProductsDynamicDataQueryInput input
    )
    {
        var sqlParts = ProductsDynamicDataSql.TryCreate(_db);
        if (sqlParts == null)
        {
            return await QueryHistoryRowsWithSqlSugarAsync(input);
        }

        // 一次扫描：窗口 MAX 按商品编码分区（分区规则与旧 GROUP BY 相同）求最近订货日期，只回传最近日期的明细行；
        // 最近日期全为 NULL 的商品保留全部 NULL 日期行，与旧的 C# “null == null” 比较一致。
        // 分块时每个商品只落在一个块里，分区结果与不分块一致。
        var rows = new List<ProductHistoryDynamicHistoryRow>();
        foreach (var chunk in ProductsDynamicDataSql.Chunk(input.ProductCodes))
        {
            var parameters = sqlParts.CreateParameters(input.StoreCode, chunk, out var productCodeList);
            rows.AddRange(await _db.Ado.SqlQueryAsync<ProductHistoryDynamicHistoryRow>(
                $"""
                SELECT h.[ProductCode], h.[OrderGUID], h.[OrderDate], h.[CreatedAt], h.[Quantity], h.[AllocQuantity]
                FROM (
                    SELECT d.[ProductCode] AS [ProductCode], d.[OrderGUID] AS [OrderGUID], o.[OrderDate] AS [OrderDate],
                           o.[CreatedAt] AS [CreatedAt], d.[Quantity] AS [Quantity], d.[AllocQuantity] AS [AllocQuantity],
                           MAX(o.[OrderDate]) OVER (PARTITION BY d.[ProductCode]) AS [LatestOrderDate]
                    {sqlParts.FromDetailsJoinOrders}
                    WHERE {ProductsDynamicDataSql.StoreCondition(input.StoreCode)} AND o.[FlowStatus] > 0
                      AND o.[IsDeleted] = 0 AND d.[IsDeleted] = 0
                      AND d.[ProductCode] IS NOT NULL AND d.[ProductCode] IN ({productCodeList})
                ) h
                WHERE {sqlParts.SameInstant("h.[OrderDate]", "h.[LatestOrderDate]")}
                   OR (h.[OrderDate] IS NULL AND h.[LatestOrderDate] IS NULL)
                """,
                parameters
            ));
        }

        return rows;
    }

    /// <summary>PostgreSQL 等其它库沿用改造前的 SqlSugar 条件（商品编码内联 IN 列表）。</summary>
    private Task<List<ProductHistoryDynamicCartRow>> QueryCartRowsWithSqlSugarAsync(
        ProductsDynamicDataQueryInput input,
        string? cartOwnerUserGuid
    )
    {
        var cartQuery = _db.Queryable<WareHouseOrderDetails>()
            .InnerJoin<WareHouseOrder>((detail, order) => detail.OrderGUID == order.OrderGUID)
            .Where((detail, order) =>
                order.StoreCode == input.StoreCode
                && order.FlowStatus == 0
                && !order.IsDeleted
                && !detail.IsDeleted
            )
            .Where((detail, order) =>
                detail.ProductCode != null
                && input.ProductCodes.Contains(detail.ProductCode)
            );
        cartQuery = string.IsNullOrWhiteSpace(cartOwnerUserGuid)
            ? cartQuery.Where((detail, order) =>
                SqlFunc.IsNullOrEmpty(order.CartOwnerUserGuid)
            )
            : cartQuery.Where((detail, order) =>
                order.CartOwnerUserGuid == cartOwnerUserGuid
            );
        return cartQuery
            .GroupBy((detail, order) => detail.ProductCode)
            .Select((detail, order) => new ProductHistoryDynamicCartRow
            {
                ProductCode = detail.ProductCode,
                CartQuantity = SqlFunc.AggregateSum(detail.Quantity),
            })
            .ToListAsync();
    }

    private Task<List<ProductHistoryDynamicHistoryRow>> QueryHistoryRowsWithSqlSugarAsync(
        ProductsDynamicDataQueryInput input
    )
    {
        return _db.Queryable<WareHouseOrderDetails>()
            .InnerJoin<WareHouseOrder>((detail, order) => detail.OrderGUID == order.OrderGUID)
            .Where((detail, order) =>
                order.StoreCode == input.StoreCode
                && order.FlowStatus > 0
                && !order.IsDeleted
                && !detail.IsDeleted
            )
            .Where((detail, order) =>
                detail.ProductCode != null
                && input.ProductCodes.Contains(detail.ProductCode)
            )
            .Select((detail, order) => new ProductHistoryDynamicHistoryRow
            {
                ProductCode = detail.ProductCode,
                OrderGUID = detail.OrderGUID,
                OrderDate = order.OrderDate,
                CreatedAt = order.CreatedAt,
                Quantity = detail.Quantity,
                AllocQuantity = detail.AllocQuantity,
            })
            .ToListAsync();
    }

    private string? ResolveActiveCartOwnerUserGuid()
    {
        var isWarehouseStaff = actorContext.HasRole("WarehouseStaff")
            || actorContext.HasRole("仓库员工");
        var hasSuperAdminRole = Permissions.SuperAdminRoleNames.Any(actorContext.HasRole);
        var hasWarehouseManagerRole = Permissions.WarehouseManagerRoleNames.Any(
            actorContext.HasRole
        );
        if (!isWarehouseStaff || hasSuperAdminRole || hasWarehouseManagerRole)
        {
            return null;
        }

        var user = actorContext.User;
        var userGuid = (
            user?.FindFirst("userId")?.Value
            ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user?.FindFirst("userGuid")?.Value
            ?? user?.FindFirst("userGUID")?.Value
            ?? user?.FindFirst("UserGuid")?.Value
            ?? user?.FindFirst("sub")?.Value
            ?? string.Empty
        ).Trim();
        if (string.IsNullOrWhiteSpace(userGuid))
        {
            throw new InvalidOperationException("无法识别当前仓库员工");
        }

        return userGuid;
    }
}

/// <summary>
/// 动态数据原生 SQL 的方言片段；其余谓词与改造前 SqlSugar 生成的条件逐条对应。其它库返回 null 走旧写法。
/// 商品编码用参数列表传入（生产实测 OPENJSON 派生表会让优化器放弃“按门店订单逐单定位明细”的计划，
/// 50 个编码从约 0.1 秒退化到 0.6–1 秒；参数列表与旧的内联字面量计划相同，但 SQL 文本不随商品变化）。
/// 个数按 2 的幂补齐（重复最后一个值，不改变 IN 的集合语义），同一档位的页面复用同一份计划。
/// </summary>
internal sealed class ProductsDynamicDataSql
{
    internal const int MaximumProductCodesPerStatement = 1024;
    private const int ScalarStringSize = 4000;

    private readonly bool _isSqlServer;
    private readonly string _noLockHint;

    private ProductsDynamicDataSql(bool isSqlServer, string noLockHint)
    {
        _isSqlServer = isSqlServer;
        _noLockHint = noLockHint;
    }

    internal static ProductsDynamicDataSql? TryCreate(ISqlSugarClient db)
    {
        return db.CurrentConnectionConfig.DbType switch
        {
            DbType.SqlServer => new ProductsDynamicDataSql(
                isSqlServer: true,
                ResolveSqlServerNoLockHint(db)
            ),
            DbType.Sqlite => new ProductsDynamicDataSql(isSqlServer: false, string.Empty),
            _ => null,
        };
    }

    /// <summary>SQL Server 单条语句最多 2100 个参数，超长列表按块拆开，每块最多 1024 个编码。</summary>
    internal static IEnumerable<IReadOnlyList<string>> Chunk(IReadOnlyList<string> productCodes)
    {
        for (var offset = 0; offset < productCodes.Count; offset += MaximumProductCodesPerStatement)
        {
            yield return productCodes
                .Skip(offset)
                .Take(MaximumProductCodesPerStatement)
                .ToList();
        }
    }

    internal string FromDetailsJoinOrders =>
        $"FROM [WareHouseOrderDetails] d{_noLockHint}"
        + Environment.NewLine
        + $"INNER JOIN [WareHouseOrder] o{_noLockHint} ON d.[OrderGUID] = o.[OrderGUID]";

    /// <summary>
    /// 两个日期是否同一时刻。SQLite 把日期存成文本，批量插入与单行插入的格式不同（是否带 .000），
    /// 用 julianday 归一后比较，与旧逻辑在 C# 里按 DateTime 相等比较的结果一致。
    /// </summary>
    internal string SameInstant(string left, string right) =>
        _isSqlServer ? $"{left} = {right}" : $"julianday({left}) = julianday({right})";

    /// <summary>门店编码为 null 时与 SqlSugar 对空变量的翻译一致写成 IS NULL。</summary>
    internal static string StoreCondition(string? storeCode) =>
        storeCode == null ? "o.[StoreCode] IS NULL" : "o.[StoreCode] = @StoreCode";

    internal List<SugarParameter> CreateParameters(
        string? storeCode,
        IReadOnlyList<string> productCodes,
        out string productCodeList
    )
    {
        var parameters = new List<SugarParameter>();
        var paddedCount = 1;
        while (paddedCount < productCodes.Count)
        {
            paddedCount *= 2;
        }

        var names = new List<string>(paddedCount);
        for (var index = 0; index < paddedCount; index++)
        {
            var name = "@ProductCode" + index;
            names.Add(name);
            parameters.Add(CreateStringParameter(
                name,
                productCodes[Math.Min(index, productCodes.Count - 1)]
            ));
        }

        productCodeList = string.Join(", ", names);
        if (storeCode != null)
        {
            parameters.Add(CreateStringParameter("@StoreCode", storeCode));
        }

        return parameters;
    }

    internal SugarParameter CreateStringParameter(string name, string value)
    {
        var parameter = new SugarParameter(name, value) { DbType = AdoDbType.String };
        if (_isSqlServer)
        {
            // 固定声明长度，让不同门店、用户、商品复用同一份计划；超长值退回 nvarchar(max) 避免截断。
            parameter.Size = value.Length > ScalarStringSize ? -1 : ScalarStringSize;
        }

        return parameter;
    }

    private static string ResolveSqlServerNoLockHint(ISqlSugarClient db)
    {
        // 与 SqlSugar 生成 SQL 的规则一致：开启 IsWithNoLockQuery 时加 NOLOCK，事务内按配置去掉。
        var settings = db.CurrentConnectionConfig.MoreSettings;
        if (settings?.IsWithNoLockQuery != true)
        {
            return string.Empty;
        }

        return settings.DisableWithNoLockWithTran && db.Ado.Transaction != null
            ? string.Empty
            : " WITH(NOLOCK)";
    }
}
