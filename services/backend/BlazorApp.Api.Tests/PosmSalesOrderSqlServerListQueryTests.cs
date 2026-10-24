using System.Data;
using System.Text.Json;
using BlazorApp.Api.Features.PosmSalesOrders;
using BlazorApp.Shared.DTOs;
using Xunit;
using DbType = System.Data.DbType;

namespace BlazorApp.Api.Tests;

/// <summary>SQL Server 批处理的结构约束；真实执行见 PosmSalesOrderListSqlServerIntegrationTests。</summary>
public sealed class PosmSalesOrderSqlServerListQueryTests
{
    private static PosmSalesOrderQueryParams Today() =>
        new()
        {
            StartDate = new DateTime(2026, 9, 19),
            EndDate = new DateTime(2026, 9, 19),
            SortField = "orderTime",
            SortDirection = "desc",
        };

    [Fact]
    public void 无关键词无明细条件时只查订单主表且汇总不带状态条件()
    {
        var query = Today();
        query.OrderType = OrderType.Refunded;
        query.BranchCode = "1005";

        var command = PosmSalesOrderSqlServerListQuery.Build(query, null, null, 50, 50);

        Assert.Equal("plain", command.Path);
        Assert.DoesNotContain("#hb_posm_orders", command.Sql);
        Assert.DoesNotContain("sales_order_detail", command.Sql);
        var statements = command.Sql.Split("GROUP BY o.[Status]");
        // 汇总语句在 GROUP BY 之前，不能含状态条件；状态只作用于取页。
        Assert.DoesNotContain("@Status", statements[0]);
        Assert.Contains("o.[Status] = @Status", statements[1]);
        Assert.Contains("ORDER BY o.[OrderTime] DESC, o.[OrderGuid] ASC", command.Sql);
        Assert.Equal(2, CountOccurrences(command.Sql, "OPTION (RECOMPILE)"));
        // 分店用 varchar 参数，避免列发生隐式转换。
        var branch = Assert.Single(command.Parameters, parameter => parameter.Name == "@BranchCode");
        Assert.Equal(DbType.AnsiString, branch.DbType);
        Assert.Equal(new DateTime(2026, 9, 20), command.Parameters.Single(parameter => parameter.Name == "@EndExclusive").Value);
    }

    [Theory]
    [InlineData(null, false, null)]
    [InlineData("bogus", false, null)]
    [InlineData("any", true, null)]
    [InlineData("ANY", true, null)]
    [InlineData("partial", true, ">= 0")]
    [InlineData("full", true, "< 0")]
    public void 退货筛选按退款单或退货记录判定并按整单金额正负分档(string? refundFilter, bool expectsRefundPredicate, string? amountCondition)
    {
        var query = Today();
        query.RefundFilter = refundFilter;

        var command = PosmSalesOrderSqlServerListQuery.Build(query, null, null, 0, 20);

        Assert.Equal("plain", command.Path);
        Assert.Equal(expectsRefundPredicate, command.Sql.Contains("[dbo].[sales_return_record]"));
        if (expectsRefundPredicate)
        {
            // 退款单（Status=3）或订单里夹带了退货记录，做成订单号集合再 IN；
            // 不能用 `OR EXISTS`（生产实测汇总慢 5 倍以上），也不对明细表做扫描。
            Assert.Contains("o.[OrderGuid] IN (SELECT rr.[ReturnOrderGuid] FROM [dbo].[sales_return_record]", command.Sql);
            Assert.Contains("UNION ALL SELECT s.[OrderGuid] FROM [dbo].[sales_order] AS s WITH (NOLOCK) WHERE s.[Status] = 3", command.Sql);
            // 退款单分支带同一时间区间，避免扫全表。
            Assert.Contains("AND s.[OrderTime] >= @Start AND s.[OrderTime] < @EndExclusive", command.Sql);
            Assert.DoesNotContain("OR EXISTS", command.Sql);
            Assert.DoesNotContain("sales_order_detail", command.Sql);
        }
        // 汇总与取页共用同一份订单条件，所以两条语句都带退货条件。
        Assert.Equal(expectsRefundPredicate ? 2 : 0, CountOccurrences(command.Sql, "[dbo].[sales_return_record]"));
        // 整单金额口径与卡片一致：实付为 0 / 空时回落订单总金额。
        const string amountExpression = "COALESCE(NULLIF(o.[ActualAmount], 0), o.[TotalAmount], 0)";
        Assert.Equal(amountCondition == null ? 0 : 2, CountOccurrences(command.Sql, $"{amountExpression} {amountCondition}"));
    }

    [Fact]
    public void 退货筛选与关键词路径同样带上退货条件()
    {
        var query = Today();
        query.RefundFilter = "full";

        var command = PosmSalesOrderSqlServerListQuery.Build(query, "squishy", new[] { "P-1" }, 0, 20);

        Assert.NotEqual("plain", command.Path);
        Assert.Contains("[dbo].[sales_return_record]", command.Sql);
        Assert.Contains("COALESCE(NULLIF(o.[ActualAmount], 0), o.[TotalAmount], 0) < 0", command.Sql);
    }

    [Fact]
    public void 商品关键词落临时表并按成本在逐单与商品侧之间选路()
    {
        var command = PosmSalesOrderSqlServerListQuery.Build(Today(), "squishy", new[] { "P-1", " p-1 ", "P-2", "squishy" }, 0, 50);

        Assert.Equal("keyword", command.Path);
        Assert.Contains("COLLATE DATABASE_DEFAULT", command.Sql);
        Assert.Contains($"@hbOrderCount * {PosmSalesOrderSqlServerListQuery.OrderDrivenRatio}", command.Sql);
        Assert.Contains("OPTION (LOOP JOIN, FORCE ORDER, RECOMPILE)", command.Sql);
        Assert.Contains("INNER HASH JOIN [dbo].[sales_order] AS o", command.Sql);
        // 商品名等非订单号关键词不再逐单比较订单号，也不再匹配收银机号。
        Assert.DoesNotContain("@OrderNoUpper", command.Sql);
        Assert.DoesNotContain("[DeviceCode] AS nvarchar", command.Sql);
        var codes = JsonSerializer.Deserialize<List<string>>(
            (string)command.Parameters.Single(parameter => parameter.Name == "@ProductCodes").Value!
        );
        Assert.Equal(new[] { "P-1", "P-2", "squishy" }, codes);
    }

    [Fact]
    public void 订单号片段关键词同时按大小写两种写法匹配订单号()
    {
        var command = PosmSalesOrderSqlServerListQuery.Build(Today(), "1b4d21", new[] { "1b4d21" }, 0, 50);

        Assert.Contains("o.[OrderGuid] COLLATE Latin1_General_100_BIN2 LIKE @OrderNoUpper", command.Sql);
        Assert.Equal("%1B4D21%", command.Parameters.Single(parameter => parameter.Name == "@OrderNoUpper").Value);
        Assert.Equal("%1b4d21%", command.Parameters.Single(parameter => parameter.Name == "@OrderNoLower").Value);
    }

    [Fact]
    public void 件数种数条件先按订单号范围连续汇总再删除不符合的订单()
    {
        var query = Today();
        query.QuantityMin = 5;
        query.SkuCountMax = 3;
        query.SortField = "quantity";

        var command = PosmSalesOrderSqlServerListQuery.Build(query, null, null, 0, 50);

        Assert.Equal("detail-aggregate", command.Path);
        Assert.Contains("SUBSTRING([OrderGuid], 15, 1) = '7'", command.Sql);
        Assert.Contains("d.[OrderGuid] >= @hbGuidMin AND d.[OrderGuid] <= @hbGuidMax", command.Sql);
        Assert.Contains("WITH (NOLOCK, FORCESEEK)", command.Sql);
        Assert.Contains("DELETE FROM #hb_posm_orders WHERE NOT ([SkuCount] <= @SkuCountMax AND [QuantityTotal] >= @QuantityMin);", command.Sql);
        Assert.Contains("ORDER BY m.[QuantityTotal] DESC, m.[OrderGuid] ASC", command.Sql);
    }

    [Theory]
    [InlineData("actualPay", "asc", "actualpay", false)]
    [InlineData("TotalAmount", "DESC", "totalamount", true)]
    [InlineData("DROP TABLE", "asc", "ordertime", false)]
    [InlineData("orderGuid", "sideways", "ordertime", false)]
    public void 排序字段只接受白名单非法时回退下单时间升序(string field, string direction, string expectedField, bool expectedDescending)
    {
        Assert.Equal((expectedField, expectedDescending), PosmSalesOrderSqlServerListQuery.NormalizeSort(field, direction));
    }

    [Fact]
    public void 用户输入只进参数不拼进SQL()
    {
        var query = Today();
        query.BranchCodes = new() { "1005'; DROP TABLE x;--", "1033" };
        query.DeviceCodeKeyword = "POS_'--";

        var command = PosmSalesOrderSqlServerListQuery.Build(query, "a'b", new[] { "a'b" }, 0, 50);

        Assert.DoesNotContain("DROP TABLE x", command.Sql);
        Assert.DoesNotContain("a'b", command.Sql);
        Assert.Contains("o.[BranchCode] IN (@Branch0, @Branch1)", command.Sql);
        // LIKE 通配符按方括号转义，下划线不会被当成任意字符。
        Assert.Equal("%POS[_]'--%", command.Parameters.Single(parameter => parameter.Name == "@DevicePattern").Value);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
