using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
// 项目全局引用了 SqlSugar，其 DbType 与 ADO.NET 参数类型同名。
using DbType = System.Data.DbType;

namespace BlazorApp.Api.Features.PosmSalesOrders;

/// <summary>一次查询的结果：按状态汇总（不受状态筛选影响）与当前页订单。</summary>
public sealed record PosmSalesOrderListPage(
    List<PosmSalesOrderStatusSummaryDto> Summary,
    List<PosmSalesOrderDto> Rows
);

/// <summary>构建好的 SQL 批处理与参数；Path 用于日志区分走了哪条路径。</summary>
public sealed record PosmSalesOrderSqlServerListCommand(
    string Sql,
    IReadOnlyList<PosmSalesOrderSqlParameter> Parameters,
    string Path
);

public sealed record PosmSalesOrderSqlParameter(string Name, object? Value, DbType DbType, int? Size = null);

/// <summary>
/// SQL Server 上的收银记录列表：一次往返返回按状态汇总与当前页。
/// 生产实测（2026-09-19）的关键点：
/// - 原写法把日期范围内所有订单 LEFT JOIN 明细后 GROUP BY 再分页，计数与取页各跑一遍；
///   带关键词时要对每条明细回表读商品名和条码，全部分店 7 天约 2 秒、冷缓存 5–10 秒。
/// - 现在无关键词、无件数/种数条件时只查订单主表（时间索引），种数、件数、支付方式由服务层只为当前页补齐。
/// - 关键词先在商品主档解析成商品编码，再经 IX_sales_order_detail_ProductCode 定位订单；
///   命中订单先落临时表，汇总与取页共用，关键词只算一次。
/// - 日期范围从 1 天到 92 天、有无分店差别很大，随范围变化的语句都带 OPTION (RECOMPILE)：
///   按 1 天编译的计划（时间索引 seek + 逐行回表取状态）被 92 天复用时汇总要 23 秒，重编译后约 0.5 秒。
/// </summary>
public static class PosmSalesOrderSqlServerListQuery
{
    private const string Bin2 = LocalSupplierProductSalesAnalysisService.SqlServerBinaryCollation;

    /// <summary>
    /// 逐单查明细与从商品侧建哈希表的分界：命中商品的全部历史明细行数超过范围内订单数的这个倍数时，逐单查更省。
    /// 热缓存下约 10 倍即持平；但逐单查是在 1 GB 的订单号索引上随机读，生产缓存寿命低时冷读代价大得多
    /// （宽泛关键词查 7 天冷读 8.5 秒，改走商品侧约 0.5 秒），所以放宽到 50 倍，只有「订单很少」时才逐单查。
    /// </summary>
    public const int OrderDrivenRatio = 50;

    private const string OrderColumns =
        "o.[OrderGuid], o.[OrderTime], o.[BranchCode], o.[DeviceCode], o.[TotalAmount], o.[DiscountAmount], o.[ActualAmount], o.[ItemCount]";

    private const string TempColumns =
        "[OrderGuid], [OrderTime], [BranchCode], [DeviceCode], [TotalAmount], [DiscountAmount], [ActualAmount], [ItemCount], [Status]";

    public static PosmSalesOrderSqlServerListCommand Build(
        PosmSalesOrderQueryParams query,
        string? keyword,
        IReadOnlyCollection<string>? keywordProductCodes,
        int offset,
        int pageSize
    )
    {
        var parameters = new List<PosmSalesOrderSqlParameter>();
        string Param(string name, object? value, DbType dbType, int? size = null)
        {
            parameters.Add(new PosmSalesOrderSqlParameter(name, value, dbType, size));
            return name;
        }

        var filter = BuildOrderFilter(query, Param);
        int? status = query.OrderType.HasValue && query.OrderType.Value != OrderType.All
            ? (int)query.OrderType.Value
            : null;
        var needsAggregates = PosmSalesOrderListRules.NeedsDetailAggregates(query);
        var hasKeyword = !string.IsNullOrEmpty(keyword);
        var (sortField, descending) = NormalizeSort(query.SortField, query.SortDirection);
        Param("@Offset", offset, DbType.Int32);
        Param("@PageSize", pageSize, DbType.Int32);
        if (status.HasValue)
        {
            Param("@Status", status.Value, DbType.Int32);
        }

        if (!hasKeyword && !needsAggregates)
        {
            return new PosmSalesOrderSqlServerListCommand(
                BuildPlainSql(filter, status.HasValue, SortExpression("o", sortField, descending)),
                parameters,
                "plain"
            );
        }

        var sql = new StringBuilder();
        sql.AppendLine("SET NOCOUNT ON;");
        sql.AppendLine("IF OBJECT_ID(N'tempdb..#hb_posm_orders') IS NOT NULL DROP TABLE #hb_posm_orders;");
        sql.AppendLine("IF OBJECT_ID(N'tempdb..#hb_posm_codes') IS NOT NULL DROP TABLE #hb_posm_codes;");
        // 临时表建在 tempdb，字符列必须显式跟随当前库排序规则，否则与 POSM 列比较会报排序规则冲突。
        sql.AppendLine(
            """
            CREATE TABLE #hb_posm_orders (
                [OrderGuid] varchar(255) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                [OrderTime] datetime NULL,
                [BranchCode] varchar(20) COLLATE DATABASE_DEFAULT NULL,
                [DeviceCode] varchar(20) COLLATE DATABASE_DEFAULT NULL,
                [TotalAmount] decimal(18, 4) NULL,
                [DiscountAmount] decimal(18, 4) NULL,
                [ActualAmount] decimal(18, 4) NULL,
                [ItemCount] int NULL,
                [Status] int NULL,
                [SkuCount] int NULL,
                [QuantityTotal] int NULL
            );
            """
        );

        var path = "detail-aggregate";
        if (hasKeyword)
        {
            path = "keyword";
            var codes = (keywordProductCodes ?? Array.Empty<string>())
                .Where(code => !string.IsNullOrWhiteSpace(code) && code.Trim().Length <= 50)
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (codes.Count > 0)
            {
                Param("@ProductCodes", JsonSerializer.Serialize(codes), DbType.String, -1);
                AppendProductKeywordInsert(sql, filter);
            }

            // 订单号只在关键词像订单号片段（≥4 位十六进制）时匹配：订单号是 ASCII，直接对 varchar 列做 BIN2 比较，
            // 大写、小写两种写法各比一次（2026-09-19 生产有 113 单小写订单号），省掉逐行 UPPER/CAST 的 CPU。
            if (PosmSalesOrderListRules.IsOrderNumberFragment(keyword!))
            {
                var pattern = LocalSupplierProductSalesAnalysisService.BuildSqlServerLikePattern(keyword!.ToUpperInvariant());
                Param("@OrderNoUpper", pattern, DbType.AnsiString, 300);
                Param("@OrderNoLower", pattern.ToLowerInvariant(), DbType.AnsiString, 300);
                sql.AppendLine(
                    $"""
                    INSERT INTO #hb_posm_orders ({TempColumns})
                    SELECT {OrderColumns}, o.[Status]
                    FROM [dbo].[sales_order] AS o WITH (NOLOCK)
                    WHERE {filter}
                      AND (o.[OrderGuid] COLLATE {Bin2} LIKE @OrderNoUpper OR o.[OrderGuid] COLLATE {Bin2} LIKE @OrderNoLower)
                      AND NOT EXISTS (SELECT 1 FROM #hb_posm_orders AS m WHERE m.[OrderGuid] = o.[OrderGuid])
                    OPTION (RECOMPILE);
                    """
                );
            }
        }
        else
        {
            sql.AppendLine(
                $"""
                INSERT INTO #hb_posm_orders ({TempColumns})
                SELECT {OrderColumns}, o.[Status]
                FROM [dbo].[sales_order] AS o WITH (NOLOCK)
                WHERE {filter}
                OPTION (RECOMPILE);
                """
            );
        }

        if (needsAggregates)
        {
            // 件数、种数是明细聚合值，先对已落表的订单汇总，再按区间删掉不符合的订单。
            // 订单号索引已包含 ProductCode 与 Quantity（SqlScripts/PosmSalesOrderDetailOrderGuidIndexIncludeQuantity.sql）：
            // 1. 取落表订单号（只取 UUIDv7 的）最小值到最大值，对明细订单号索引做一次范围扫描，哈希连接留下落表订单后汇总。
            //    范围本身就是落表订单号的上下界，结果不依赖订单号随时间递增；递增只让范围里夹带的其他订单更少。
            //    逐单 seek 在单店长区间冷读时是上万次随机读（单店 92 天 20 秒），连续扫描约 1–2 秒。
            // 2. 旧的随机 GUID 订单与没有明细的订单仍为空，按单 seek 补齐（强制 seek，防止优化器改成聚合整张明细表）。
            sql.AppendLine(
                $"""
                DECLARE @hbGuidMin varchar(255), @hbGuidMax varchar(255);
                SELECT @hbGuidMin = MIN([OrderGuid]), @hbGuidMax = MAX([OrderGuid])
                FROM #hb_posm_orders
                WHERE SUBSTRING([OrderGuid], 15, 1) = '7';

                IF @hbGuidMin IS NOT NULL
                BEGIN
                    UPDATE m
                    SET m.[SkuCount] = a.[SkuCount], m.[QuantityTotal] = a.[QuantityTotal]
                    FROM #hb_posm_orders AS m
                    INNER JOIN (
                        SELECT d.[OrderGuid], COUNT(DISTINCT d.[ProductCode]) AS [SkuCount], ISNULL(SUM(d.[Quantity]), 0) AS [QuantityTotal]
                        FROM [dbo].[sales_order_detail] AS d WITH (NOLOCK)
                        INNER HASH JOIN #hb_posm_orders AS k ON k.[OrderGuid] COLLATE {Bin2} = d.[OrderGuid] COLLATE {Bin2}
                        WHERE d.[OrderGuid] >= @hbGuidMin AND d.[OrderGuid] <= @hbGuidMax
                        GROUP BY d.[OrderGuid]
                    ) AS a ON a.[OrderGuid] = m.[OrderGuid]
                    OPTION (RECOMPILE);
                END;

                UPDATE m
                SET m.[SkuCount] = a.[SkuCount], m.[QuantityTotal] = a.[QuantityTotal]
                FROM #hb_posm_orders AS m
                CROSS APPLY (
                    SELECT COUNT(DISTINCT d.[ProductCode]) AS [SkuCount], ISNULL(SUM(d.[Quantity]), 0) AS [QuantityTotal]
                    FROM [dbo].[sales_order_detail] AS d WITH (NOLOCK, FORCESEEK)
                    WHERE d.[OrderGuid] = m.[OrderGuid]
                ) AS a
                WHERE m.[SkuCount] IS NULL
                OPTION (LOOP JOIN);
                """
            );
            var aggregateConditions = BuildAggregateConditions(query, Param);
            if (aggregateConditions.Count > 0)
            {
                sql.AppendLine(
                    $"DELETE FROM #hb_posm_orders WHERE NOT ({string.Join(" AND ", aggregateConditions)});"
                );
            }
        }

        sql.AppendLine(
            """
            SELECT m.[Status], COUNT_BIG(*) AS [OrderCount], SUM(m.[TotalAmount]) AS [TotalAmount], SUM(m.[DiscountAmount]) AS [DiscountAmount]
            FROM #hb_posm_orders AS m
            GROUP BY m.[Status];
            """
        );
        sql.AppendLine(
            $"""
            SELECT m.[OrderGuid], m.[OrderTime], m.[BranchCode], m.[DeviceCode], m.[TotalAmount], m.[DiscountAmount],
                   m.[ActualAmount], m.[ItemCount], m.[Status], m.[SkuCount], m.[QuantityTotal]
            FROM #hb_posm_orders AS m
            {(status.HasValue ? "WHERE m.[Status] = @Status" : string.Empty)}
            ORDER BY {SortExpression("m", sortField, descending)}, m.[OrderGuid] ASC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """
        );
        sql.AppendLine("DROP TABLE #hb_posm_orders;");
        sql.AppendLine("IF OBJECT_ID(N'tempdb..#hb_posm_codes') IS NOT NULL DROP TABLE #hb_posm_codes;");
        return new PosmSalesOrderSqlServerListCommand(sql.ToString(), parameters, path);
    }

    /// <summary>
    /// 按商品编码命中订单：先数范围内订单数与命中商品的历史明细行数，再二选一。
    /// - 订单少而命中商品卖得多（宽泛关键词查 1 天）：逐单查明细，避免把几十万行历史明细读一遍；
    ///   FORCE ORDER 保证由订单驱动，否则 LOOP JOIN 可能从上万个编码反向驱动。
    /// - 其余情况：编码经 IX_sales_order_detail_ProductCode 取明细建哈希表，范围内订单边读边匹配；
    ///   强制连接顺序避免优化器低估去重后行数、排序溢出到 tempdb（生产实测 1.7 秒 → 0.1 秒）。
    ///   订单号用 BIN2 比较：2026-09-19 全表核对 683 万行明细，与库排序规则的匹配结果零差异，省掉 Windows 排序规则的哈希开销。
    ///   状态已是时间索引的包含列（SqlScripts/PosmSalesOrderOrderTimeIndexIncludeStatus.sql），不再回表。
    /// </summary>
    private static void AppendProductKeywordInsert(StringBuilder sql, string filter)
    {
        sql.AppendLine(
            $"""
            CREATE TABLE #hb_posm_codes ([ProductCode] varchar(50) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);
            INSERT INTO #hb_posm_codes ([ProductCode])
            SELECT DISTINCT CAST([value] AS varchar(50)) FROM OPENJSON(@ProductCodes) WHERE [value] IS NOT NULL;

            DECLARE @hbOrderCount int;
            SELECT @hbOrderCount = COUNT(*) FROM [dbo].[sales_order] AS o WITH (NOLOCK) WHERE {filter} OPTION (RECOMPILE);
            DECLARE @hbProductRowLimit int = CASE WHEN @hbOrderCount > 200000000 THEN 2147483647 ELSE @hbOrderCount * {OrderDrivenRatio} + 1 END;
            DECLARE @hbProductRows int = (
                SELECT COUNT(*) FROM (
                    SELECT TOP (@hbProductRowLimit) 1 AS [x]
                    FROM #hb_posm_codes AS c
                    INNER LOOP JOIN [dbo].[sales_order_detail] AS d WITH (NOLOCK) ON d.[ProductCode] = c.[ProductCode]
                ) AS t
            );

            IF @hbProductRows > CAST(@hbOrderCount AS bigint) * {OrderDrivenRatio}
            BEGIN
                INSERT INTO #hb_posm_orders ({TempColumns})
                SELECT {OrderColumns}, o.[Status]
                FROM [dbo].[sales_order] AS o WITH (NOLOCK)
                WHERE {filter}
                  AND EXISTS (
                      SELECT 1
                      FROM [dbo].[sales_order_detail] AS d WITH (NOLOCK)
                      INNER JOIN #hb_posm_codes AS c ON c.[ProductCode] = d.[ProductCode]
                      WHERE d.[OrderGuid] = o.[OrderGuid]
                  )
                OPTION (LOOP JOIN, FORCE ORDER, RECOMPILE);
            END
            ELSE
            BEGIN
                INSERT INTO #hb_posm_orders ({TempColumns})
                SELECT DISTINCT {OrderColumns}, o.[Status]
                FROM (#hb_posm_codes AS c
                    INNER LOOP JOIN [dbo].[sales_order_detail] AS d WITH (NOLOCK) ON d.[ProductCode] = c.[ProductCode])
                INNER HASH JOIN [dbo].[sales_order] AS o WITH (NOLOCK)
                    ON o.[OrderGuid] COLLATE {Bin2} = d.[OrderGuid] COLLATE {Bin2}
                WHERE {filter}
                OPTION (RECOMPILE);
            END;
            """
        );
    }

    private static string BuildPlainSql(string filter, bool hasStatus, string sort) =>
        $"""
        SET NOCOUNT ON;
        SELECT o.[Status], COUNT_BIG(*) AS [OrderCount], SUM(o.[TotalAmount]) AS [TotalAmount], SUM(o.[DiscountAmount]) AS [DiscountAmount]
        FROM [dbo].[sales_order] AS o WITH (NOLOCK)
        WHERE {filter}
        GROUP BY o.[Status]
        OPTION (RECOMPILE);
        SELECT {OrderColumns}, o.[Status], CAST(NULL AS int) AS [SkuCount], CAST(NULL AS int) AS [QuantityTotal]
        FROM [dbo].[sales_order] AS o WITH (NOLOCK)
        WHERE {filter}{(hasStatus ? " AND o.[Status] = @Status" : string.Empty)}
        ORDER BY {sort}, o.[OrderGuid] ASC
        OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
        OPTION (RECOMPILE);
        """;

    /// <summary>
    /// 订单级条件（不含状态）：全部来自订单主表且都在时间索引的包含列里（状态也已包含），不需要回表。
    /// 分店、收银机用 varchar 参数，避免 nvarchar 参数让列发生隐式转换。
    /// </summary>
    private static string BuildOrderFilter(
        PosmSalesOrderQueryParams query,
        Func<string, object?, DbType, int?, string> param
    )
    {
        var where = new List<string>();
        if (query.StartDate.HasValue)
        {
            where.Add($"o.[OrderTime] >= {param("@Start", query.StartDate.Value.Date, DbType.DateTime, null)}");
        }
        if (query.EndDate.HasValue)
        {
            where.Add($"o.[OrderTime] < {param("@EndExclusive", query.EndDate.Value.Date.AddDays(1), DbType.DateTime, null)}");
        }
        if (!string.IsNullOrWhiteSpace(query.BranchCode))
        {
            where.Add($"o.[BranchCode] = {param("@BranchCode", query.BranchCode.Trim(), DbType.AnsiString, 20)}");
        }
        var branchCodes = (query.BranchCodes ?? new List<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (branchCodes.Count > 0)
        {
            var names = branchCodes.Select((code, index) => param($"@Branch{index}", code, DbType.AnsiString, 20));
            where.Add($"o.[BranchCode] IN ({string.Join(", ", names)})");
        }
        if (!string.IsNullOrWhiteSpace(query.DeviceCode))
        {
            where.Add($"o.[DeviceCode] = {param("@DeviceCode", query.DeviceCode.Trim(), DbType.AnsiString, 20)}");
        }
        if (!string.IsNullOrWhiteSpace(query.OrderGuidKeyword))
        {
            var pattern = LocalSupplierProductSalesAnalysisService.BuildSqlServerLikePattern(query.OrderGuidKeyword.Trim().ToUpperInvariant());
            where.Add($"UPPER(CAST(o.[OrderGuid] AS nvarchar(4000))) COLLATE {Bin2} LIKE {param("@OrderGuidPattern", pattern, DbType.String, 4000)}");
        }
        if (!string.IsNullOrWhiteSpace(query.DeviceCodeKeyword))
        {
            var pattern = LocalSupplierProductSalesAnalysisService.BuildSqlServerLikePattern(query.DeviceCodeKeyword.Trim().ToUpperInvariant());
            where.Add($"UPPER(CAST(o.[DeviceCode] AS nvarchar(4000))) COLLATE {Bin2} LIKE {param("@DevicePattern", pattern, DbType.String, 4000)}");
        }
        // 时段按墙钟秒数比较（OrderTime 是门店本地时间，不做时区换算），与 SQLite 路径的时*3600+分*60+秒一致。
        if (query.TimeStart.HasValue)
        {
            where.Add($"DATEDIFF(second, CAST(o.[OrderTime] AS date), o.[OrderTime]) >= {param("@TimeStartSeconds", (int)query.TimeStart.Value.TotalSeconds, DbType.Int32, null)}");
        }
        if (query.TimeEnd.HasValue)
        {
            where.Add($"DATEDIFF(second, CAST(o.[OrderTime] AS date), o.[OrderTime]) <= {param("@TimeEndSeconds", (int)query.TimeEnd.Value.TotalSeconds, DbType.Int32, null)}");
        }
        // 退货筛选：旧 POS 的退款单（Status=3）或新 POS 订单里夹带的退货记录（sales_return_record，仅 1.3 万行）。
        // 不能写成 `Status = 3 OR EXISTS (...)`：OR 让优化器对范围内每一单逐行探测，30 天全分店汇总 1.2~1.6 秒（生产实测）；
        // 先把「退货记录订单号 ∪ 区间内退款单订单号」做成一个集合再 IN，同一汇总只要 0.23 秒，且不用改各处 FROM。
        // partial / full 再按整单金额正负分档，金额口径与列表卡片一致：实付为 0 / 空时回落订单总金额。
        var refundFilter = PosmSalesOrderListRules.NormalizeRefundFilter(query.RefundFilter);
        if (refundFilter != null)
        {
            // 退款单分支带上同一时间区间，避免扫全表；@Start / @EndExclusive 已在上面按需登记。
            var refundOrderRange = new StringBuilder();
            if (query.StartDate.HasValue)
            {
                refundOrderRange.Append(" AND s.[OrderTime] >= @Start");
            }
            if (query.EndDate.HasValue)
            {
                refundOrderRange.Append(" AND s.[OrderTime] < @EndExclusive");
            }
            where.Add(
                $"o.[OrderGuid] IN (SELECT rr.[ReturnOrderGuid] FROM [dbo].[sales_return_record] AS rr WITH (NOLOCK) "
                    + $"UNION ALL SELECT s.[OrderGuid] FROM [dbo].[sales_order] AS s WITH (NOLOCK) WHERE s.[Status] = {PosmSalesOrderListRules.RefundOrderStatus}{refundOrderRange})"
            );
            if (refundFilter == PosmSalesOrderListRules.RefundFilterPartial)
            {
                where.Add("COALESCE(NULLIF(o.[ActualAmount], 0), o.[TotalAmount], 0) >= 0");
            }
            else if (refundFilter == PosmSalesOrderListRules.RefundFilterFull)
            {
                where.Add("COALESCE(NULLIF(o.[ActualAmount], 0), o.[TotalAmount], 0) < 0");
            }
        }
        AddRange(where, "o.[ItemCount]", query.ItemCountMin, query.ItemCountMax, "@ItemCount", DbType.Int32, param);
        AddRange(where, "o.[TotalAmount]", query.TotalAmountMin, query.TotalAmountMax, "@TotalAmount", DbType.Decimal, param);
        AddRange(where, "o.[DiscountAmount]", query.DiscountAmountMin, query.DiscountAmountMax, "@DiscountAmount", DbType.Decimal, param);
        AddRange(where, "(o.[TotalAmount] - o.[DiscountAmount])", query.ActualPayMin, query.ActualPayMax, "@ActualPay", DbType.Decimal, param);
        return where.Count == 0 ? "1 = 1" : string.Join("\n  AND ", where);
    }

    private static List<string> BuildAggregateConditions(
        PosmSalesOrderQueryParams query,
        Func<string, object?, DbType, int?, string> param
    )
    {
        var conditions = new List<string>();
        AddRange(conditions, "[SkuCount]", query.SkuCountMin, query.SkuCountMax, "@SkuCount", DbType.Int32, param);
        AddRange(conditions, "[QuantityTotal]", query.QuantityMin, query.QuantityMax, "@Quantity", DbType.Int32, param);
        return conditions;
    }

    private static void AddRange<T>(
        List<string> where,
        string expression,
        T? min,
        T? max,
        string namePrefix,
        DbType dbType,
        Func<string, object?, DbType, int?, string> param
    )
        where T : struct
    {
        if (min.HasValue)
        {
            where.Add($"{expression} >= {param(namePrefix + "Min", min.Value, dbType, null)}");
        }
        if (max.HasValue)
        {
            where.Add($"{expression} <= {param(namePrefix + "Max", max.Value, dbType, null)}");
        }
    }

    /// <summary>排序字段白名单；方向或字段非法时回退下单时间升序，与原接口一致。</summary>
    public static (string Field, bool Descending) NormalizeSort(string? sortField, string? sortDirection)
    {
        var field = sortField?.Trim().ToLowerInvariant();
        var direction = sortDirection?.Trim().ToLowerInvariant();
        if (direction is not ("asc" or "desc"))
        {
            return ("ordertime", false);
        }
        return field switch
        {
            "orderguid" or "branchcode" or "devicecode" or "ordertime" or "skucount" or "quantity"
                or "itemcount" or "totalamount" or "discountamount" or "actualpay" => (field!, direction == "desc"),
            _ => ("ordertime", false),
        };
    }

    private static string SortExpression(string alias, string field, bool descending)
    {
        var column = field switch
        {
            "orderguid" => $"{alias}.[OrderGuid]",
            "branchcode" => $"{alias}.[BranchCode]",
            "devicecode" => $"{alias}.[DeviceCode]",
            "skucount" => $"{alias}.[SkuCount]",
            "quantity" => $"{alias}.[QuantityTotal]",
            "itemcount" => $"{alias}.[ItemCount]",
            "totalamount" => $"{alias}.[TotalAmount]",
            "discountamount" => $"{alias}.[DiscountAmount]",
            "actualpay" => $"({alias}.[TotalAmount] - {alias}.[DiscountAmount])",
            _ => $"{alias}.[OrderTime]",
        };
        return $"{column} {(descending ? "DESC" : "ASC")}";
    }

    /// <summary>在 POSM 连接上执行批处理并读取两个结果集；连接由 SqlSugar 管理，只在本方法打开时负责关闭。</summary>
    public static async Task<PosmSalesOrderListPage> ExecuteAsync(
        DbConnection connection,
        PosmSalesOrderSqlServerListCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var dbCommand = connection.CreateCommand();
            dbCommand.CommandText = command.Sql;
            dbCommand.CommandTimeout = 30;
            foreach (var parameter in command.Parameters)
            {
                var dbParameter = dbCommand.CreateParameter();
                dbParameter.ParameterName = parameter.Name;
                dbParameter.Value = parameter.Value ?? DBNull.Value;
                dbParameter.DbType = parameter.DbType;
                if (parameter.Size.HasValue)
                {
                    dbParameter.Size = parameter.Size.Value;
                }
                if (parameter.DbType == DbType.Decimal)
                {
                    dbParameter.Precision = 18;
                    dbParameter.Scale = 4;
                }
                dbCommand.Parameters.Add(dbParameter);
            }

            await using var reader = await dbCommand.ExecuteReaderAsync(cancellationToken);
            var summary = new List<PosmSalesOrderStatusSummaryDto>();
            while (await reader.ReadAsync(cancellationToken))
            {
                summary.Add(
                    new PosmSalesOrderStatusSummaryDto
                    {
                        Status = reader.IsDBNull(0) ? null : reader.GetInt32(0),
                        OrderCount = Convert.ToInt32(reader.GetValue(1)),
                        TotalAmount = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2),
                        DiscountAmount = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3),
                    }
                );
            }
            if (!await reader.NextResultAsync(cancellationToken))
            {
                throw new InvalidOperationException("收银记录查询未返回分页结果集。");
            }

            var rows = new List<PosmSalesOrderDto>();
            while (await reader.ReadAsync(cancellationToken))
            {
                string? NullableString(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
                decimal? NullableDecimal(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
                int? NullableInt(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
                rows.Add(
                    new PosmSalesOrderDto
                    {
                        OrderGuid = NullableString(0),
                        OrderTime = reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                        BranchCode = NullableString(2),
                        DeviceCode = NullableString(3),
                        TotalAmount = NullableDecimal(4),
                        DiscountAmount = NullableDecimal(5),
                        ActualAmount = NullableDecimal(6),
                        ItemCount = NullableInt(7),
                        Status = NullableInt(8),
                        SkuCount = NullableInt(9),
                        QuantityTotal = NullableInt(10),
                    }
                );
            }

            return new PosmSalesOrderListPage(summary, rows);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }
}
