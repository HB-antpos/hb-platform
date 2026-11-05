using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;
using SqlSugar;

namespace Hbpos.Api.Tests;

public sealed class OrderHistoryServiceTests
{
    private static readonly TimeSpan QueryBudget = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task GetDetailsAsync_preserves_manual_card_identity_from_persisted_reference()
    {
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 1);
        var db = database.DbContext.PosmDb;
        db.CodeFirst.InitTables<BankTransaction>();
        var orderGuid = OrderHistorySqliteFixture.TargetOrderGuid;
        var paymentGuid = Guid.NewGuid().ToString("D");
        var reference = ManualCardPaymentReference.Format(Guid.NewGuid());
        await db.Insertable(new PaymentDetail
        {
            PaymentGuid = paymentGuid,
            OrderGuid = orderGuid.ToString("D"),
            PaymentMethod = (int)PaymentMethodKind.Card,
            Amount = 20m,
            Reference = reference
        }).ExecuteCommandAsync();
        await db.Insertable(new BankTransaction
        {
            Id = Guid.NewGuid(),
            PaymentGuid = paymentGuid,
            OrderGuid = orderGuid.ToString("D"),
            TxnRef = reference,
            Amount = 20m,
            ResponseText = "Manually confirmed by cashier"
        }).ExecuteCommandAsync();

        var details = await new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver()).GetDetailsAsync(orderGuid, CancellationToken.None);
        var payment = Assert.Single(details!.Payments);
        Assert.Equal(reference, payment.Reference);
        var transaction = Assert.Single(payment.CardTransactions!);
        Assert.Equal("Manual", transaction.Processor);
        Assert.Null(transaction.RefundReference);
    }

    public static TheoryData<string, string> CardReferencesAndProcessors()
    {
        const string purchase = "HBZ-799287a33dc24df1bca3d33eea19b35a";
        const string refund = "HBZR-799287a33dc24df1bca3d33eea19b35a";
        const string uuid = "65c4b343-03e6-49ef-a54d-012e3f2c222c";
        var payment = ZellerPaymentReference.FormatPayment(purchase, uuid);
        return new TheoryData<string, string>
        {
            { payment, "Zeller" },
            // 退款引用由收款流程包装，显示用的是里面的退款部分。
            { CardRefundReference.Format(ZellerPaymentReference.FormatRefund(refund, uuid), payment), "Zeller" },
            // 主管确认已付款、但没有 transactionUuid 的未核实格式。
            { $"ZELLER:{purchase}", "Zeller" },
            { "ANZ:TXN-1", "ANZ" },
            { "SQ:payment-1", "Square" },
            { "CARD:unknown", "Card" }
        };
    }

    [Theory]
    [MemberData(nameof(CardReferencesAndProcessors))]
    public async Task GetDetailsAsync_infers_the_card_processor_from_the_persisted_reference(string reference, string processor)
    {
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 1);
        var db = database.DbContext.PosmDb;
        db.CodeFirst.InitTables<BankTransaction>();
        var orderGuid = OrderHistorySqliteFixture.TargetOrderGuid;
        var paymentGuid = Guid.NewGuid().ToString("D");
        await db.Insertable(new PaymentDetail
        {
            PaymentGuid = paymentGuid,
            OrderGuid = orderGuid.ToString("D"),
            PaymentMethod = (int)PaymentMethodKind.Card,
            Amount = 1m,
            Reference = reference
        }).ExecuteCommandAsync();
        await db.Insertable(new BankTransaction
        {
            Id = Guid.NewGuid(),
            PaymentGuid = paymentGuid,
            OrderGuid = orderGuid.ToString("D"),
            TxnRef = "65c4b343-03e6-49ef-a54d-012e3f2c222c",
            Amount = 1m,
            ResponseText = "APPROVED"
        }).ExecuteCommandAsync();

        var details = await new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver()).GetDetailsAsync(orderGuid, CancellationToken.None);
        var transaction = Assert.Single(Assert.Single(details!.Payments).CardTransactions!);

        Assert.Equal(processor, transaction.Processor);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task QueryAsync_matches_item_number_and_barcode_within_two_seconds_without_global_detail_materialization()
    {
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 10_000);
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());

        await AssertFastLookupAsync(repository, database, "SKU-TARGET");
        await AssertFastLookupAsync(repository, database, "ITEM-TARGET");
        await AssertFastLookupAsync(repository, database, "930000000001");
    }

    [Fact]
    public async Task QueryAsync_treats_order_time_as_store_wall_clock()
    {
        // 库里的 OrderTime 是门店本地墙钟时间（2026-08-25 10:00 布里斯班 = 00:00Z）。
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 1);
        var repository = new SqlSugarOrderHistoryRepository(
            database.DbContext,
            new StubStoreTimeZoneResolver(TestStoreTimeZones.Brisbane));

        var localDay = await repository.QueryAsync(
            new OrderHistoryQueryRequest(
                "S001",
                // 门店本地当天 00:00–23:59，对应 UTC 前一天 14:00 起。
                SoldFrom: DateTimeOffset.Parse("2026-08-24T14:00:00Z"),
                SoldTo: DateTimeOffset.Parse("2026-08-25T13:59:59Z"),
                Take: 100),
            CancellationToken.None);
        var previousDay = await repository.QueryAsync(
            new OrderHistoryQueryRequest(
                "S001",
                SoldFrom: DateTimeOffset.Parse("2026-08-23T14:00:00Z"),
                SoldTo: DateTimeOffset.Parse("2026-08-24T13:59:59Z"),
                Take: 100),
            CancellationToken.None);

        var order = Assert.Single(localDay.Orders, x => x.OrderGuid == OrderHistorySqliteFixture.TargetOrderGuid);
        Assert.Equal(DateTime.Parse("2026-08-25T10:00:00"), order.SoldAt.DateTime);
        Assert.Equal(TimeSpan.FromHours(10), order.SoldAt.Offset);
        Assert.Empty(previousDay.Orders);
    }

    [Fact]
    public async Task QueryAsync_does_not_partially_match_item_number_marker()
    {
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 100);
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());

        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest(
                "S001",
                SoldFrom: DateTimeOffset.Parse("2026-08-25T00:00:00Z"),
                SoldTo: DateTimeOffset.Parse("2026-08-25T23:59:59Z"),
                Keyword: "ITEM-TARGE",
                Take: 100),
            CancellationToken.None);

        Assert.Empty(response.Orders);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("---")]
    public async Task QueryAsync_hyphen_only_keyword_does_not_match_every_order_guid(string keyword)
    {
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 100);
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());

        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest(
                "S001",
                SoldFrom: DateTimeOffset.Parse("2026-08-25T00:00:00Z"),
                SoldTo: DateTimeOffset.Parse("2026-08-25T23:59:59Z"),
                Keyword: keyword,
                Take: 100),
            CancellationToken.None);

        Assert.Empty(response.Orders);
    }

    [Theory]
    [InlineData("ITEM_1", "ITEMX1")]
    [InlineData("ITEM%1", "ITEMX1")]
    [InlineData("ITEM[1]", "ITEM1")]
    [InlineData("_", "X")]
    [InlineData("%", "X")]
    [InlineData("[", "X")]
    public async Task QueryAsync_item_number_treats_sql_wildcards_as_literals(
        string itemNumber,
        string wildcardEquivalent)
    {
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 2);
        var targetOrderGuid = OrderHistorySqliteFixture.TargetOrderGuid.ToString("D");
        var otherOrderGuid = await database.DbContext.PosmDb.Queryable<SalesOrder>()
            .Where(order => order.BranchCode == "S001" && order.OrderGuid != targetOrderGuid)
            .OrderBy(order => order.OrderTime)
            .Select(order => order.OrderGuid)
            .FirstAsync();

        // 货号现在经商品主档精确解析：两个商品的货号分别是关键字和它的「通配符等价物」，各卖在不同订单里。
        await database.DbContext.PosmDb.Insertable(new[]
        {
            new Product { UUID = Guid.NewGuid().ToString("D"), ProductCode = "WILD-A", ProductName = "A", ItemNumber = itemNumber },
            new Product { UUID = Guid.NewGuid().ToString("D"), ProductCode = "WILD-B", ProductName = "B", ItemNumber = wildcardEquivalent }
        }).ExecuteCommandAsync();
        await database.DbContext.PosmDb.Updateable<SalesOrderDetail>()
            .SetColumns(line => line.ProductCode == "WILD-A")
            .Where(line => line.OrderGuid == targetOrderGuid)
            .ExecuteCommandAsync();
        await database.DbContext.PosmDb.Updateable<SalesOrderDetail>()
            .SetColumns(line => line.ProductCode == "WILD-B")
            .Where(line => line.OrderGuid == otherOrderGuid)
            .ExecuteCommandAsync();

        var statements = database.CaptureSql();
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());
        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest(
                "S001",
                SoldFrom: DateTimeOffset.Parse("2026-08-25T00:00:00Z"),
                SoldTo: DateTimeOffset.Parse("2026-08-25T23:59:59Z"),
                Keyword: itemNumber,
                Take: 100),
            CancellationToken.None);

        var order = Assert.Single(response.Orders);
        Assert.Equal(OrderHistorySqliteFixture.TargetOrderGuid, order.OrderGuid);
        // 通配符只会出现在等值比较的参数里，任何一条语句都不能用 LIKE。
        Assert.DoesNotContain(statements, statement => statement.Contains(" like ", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("db596f11", 1)]
    [InlineData("DB59-6F11", 2)]
    public void Sql_server_order_guid_probe_selects_only_order_guid_without_boolean_parameter(
        string keyword,
        int expectedCharIndexCount)
    {
        using var db = CreateSqlServerPreviewClient();

        var sql = SqlSugarOrderHistoryRepository
            .BuildOrderGuidProbeQuery(db.Queryable<SalesOrder>(), "1004", keyword)
            .ToSql();

        // 带连字符的关键字要多比一次「去连字符」的形式；不带连字符的只比一次。
        Assert.Equal(expectedCharIndexCount, CountOccurrences(sql.Key, "CHARINDEX"));
        Assert.Contains("BranchCode", sql.Key, StringComparison.OrdinalIgnoreCase);
        // 不能带 ORDER BY：生产实测带上 OrderTime 排序会让优化器改扫覆盖全部分店的 IX_sales_order_OrderTime（5.5 万页）。
        Assert.DoesNotContain("ORDER BY", sql.Key, StringComparison.OrdinalIgnoreCase);
        // 选择列表里出现 Remark / CashierName 等非覆盖索引列，优化器就会改走整表聚集索引扫描。
        Assert.DoesNotContain("CashierName", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remark", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXISTS", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sales_order_detail", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sql.Value, parameter => parameter.Value is bool);
        Assert.DoesNotContain(sql.Value, parameter => Equals(parameter.Value, string.Empty));
        // 库里的订单号全是小写：关键字转小写后比较。
        Assert.Contains(sql.Value, parameter => Equals(parameter.Value, keyword.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("db596f11", true)]
    [InlineData("DB59-6F11", true)]
    [InlineData("930000000001", true)]
    [InlineData("-", false)]
    [InlineData("--", false)]
    [InlineData("ITEM-TARGET", false)]
    [InlineData("12 34", false)]
    [InlineData("123", false)]
    [InlineData("a-b-c", false)]
    [InlineData("12-34", true)]
    [InlineData("6F11", true)]
    [InlineData("9311192735139", false)]
    [InlineData("930000000001", true)]
    [InlineData("6a3c4dfd-ab7b-4d95-84db-024edb596f11", true)]
    [InlineData("6a3c4dfd-ab7b-4d95-84db-024edb596f11-0", false)]
    public void Order_guid_fragment_needs_hex_digits_and_hyphens_within_guid_bounds(
        string keyword,
        bool expected)
    {
        Assert.Equal(expected, SqlSugarOrderHistoryRepository.IsOrderGuidFragment(keyword));
    }

    [Theory]
    [InlineData("DB596F11", true)]
    [InlineData("db596f11", true)]
    [InlineData("12345678", true)]
    [InlineData("1234567", false)]
    [InlineData("123456789", false)]
    [InlineData("DB596F1G", false)]
    [InlineData("DB59-F11", false)]
    [InlineData("１２３４５６７８", false)]
    public void Order_short_code_shape_requires_exactly_eight_ascii_hex_characters(string keyword, bool expected)
    {
        Assert.Equal(expected, SqlSugarOrderHistoryRepository.IsOrderShortCodeShape(keyword));
    }

    [Fact]
    public async Task QueryAsync_order_short_code_matches_order_guid_without_touching_detail_table()
    {
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 100);
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());
        var statements = database.CaptureSql();

        // 小票单号是订单 GUID 末 8 位，WPF 会传大写；库里是小写，必须仍能命中。
        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest("S001", Keyword: "EEEEEEEE", Take: 20),
            CancellationToken.None);

        var order = Assert.Single(response.Orders);
        Assert.Equal(OrderHistorySqliteFixture.TargetOrderGuid, order.OrderGuid);
        // 性能约束：短码命中订单号时不能再带明细 EXISTS 子查询——生产里它是 39 秒整表扫描的来源。
        Assert.DoesNotContain(
            statements,
            statement => statement.Contains("sales_order_detail", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task QueryAsync_order_short_code_does_not_cross_store_boundary()
    {
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 20);
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());

        // S002 那张订单的末 8 位是 44444444；用 S001 的身份查，不能看到别的分店的订单。
        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest("S001", Keyword: "44444444", Take: 20),
            CancellationToken.None);

        Assert.Empty(response.Orders);
    }

    [Fact]
    public async Task QueryAsync_order_short_code_still_honors_time_window()
    {
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 20);
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());

        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest(
                "S001",
                SoldFrom: DateTimeOffset.Parse("2026-08-20T00:00:00Z"),
                SoldTo: DateTimeOffset.Parse("2026-08-21T00:00:00Z"),
                Keyword: "EEEEEEEE",
                Take: 20),
            CancellationToken.None);

        Assert.Empty(response.Orders);
    }

    [Fact]
    public async Task QueryAsync_eight_hex_keyword_without_order_guid_match_resolves_product_through_master_barcode()
    {
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 20);
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());
        // 8 位纯数字同样是合法十六进制（EAN-8 条码）：订单号里没有它时，要经商品主档解析出商品，再按商品找订单。
        var orderGuid = "11111111-2222-4333-8444-555555555555";
        await InsertProductAsync(database, "EAN8-ITEM", barcode: "12345678");
        await InsertOrderWithLineAsync(database, orderGuid, "EAN8-ITEM", barcode: "12345678");

        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest("S001", Keyword: "12345678", Take: 20),
            CancellationToken.None);

        var order = Assert.Single(response.Orders);
        Assert.Equal(Guid.Parse(orderGuid), order.OrderGuid);
    }

    [Fact]
    public async Task QueryAsync_barcode_variant_missing_from_master_is_not_matched_by_design()
    {
        // 用户确认接受的取舍：明细里记着带前导零的写法，主档里只有规范写法。按规范写法能搜到，按变体写法搜不到。
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 20);
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());
        var orderGuid = "22222222-3333-4444-8555-666666666666";
        await InsertProductAsync(database, "VARIANT-ITEM", barcode: "99887766");
        await InsertOrderWithLineAsync(database, orderGuid, "VARIANT-ITEM", barcode: "0099887766");

        var byCanonical = await repository.QueryAsync(
            new OrderHistoryQueryRequest("S001", Keyword: "99887766", Take: 20),
            CancellationToken.None);
        var byVariant = await repository.QueryAsync(
            new OrderHistoryQueryRequest("S001", Keyword: "0099887766", Take: 20),
            CancellationToken.None);

        Assert.Equal(Guid.Parse(orderGuid), Assert.Single(byCanonical.Orders).OrderGuid);
        Assert.Empty(byVariant.Orders);
    }

    [Fact]
    public async Task QueryAsync_temporary_product_missing_from_master_is_matched_by_full_product_code()
    {
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 20);
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());
        var orderGuid = "33333333-4444-4555-8666-777777777777";
        // POS 临时商品不在主档里，仍可按完整商品编码搜到。
        await InsertOrderWithLineAsync(database, orderGuid, "TEMP-OPEN-1", barcode: "TEMP-OPEN-1");

        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest("S001", Keyword: "TEMP-OPEN-1", Take: 20),
            CancellationToken.None);

        Assert.Equal(Guid.Parse(orderGuid), Assert.Single(response.Orders).OrderGuid);
    }

    [Fact]
    public async Task QueryAsync_unknown_keyword_returns_empty_without_order_guid_scan_or_exists_subquery()
    {
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 100);
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());
        var statements = database.CaptureSql();

        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest("S001", Keyword: "NOT-A-PRODUCT", Take: 20),
            CancellationToken.None);

        Assert.Empty(response.Orders);
        // 含非十六进制字符的关键字不可能出现在订单号里：不做订单号扫描（SQLite 里 CHARINDEX 会翻成 INSTR）。
        Assert.DoesNotContain(statements, statement => statement.Contains("INSTR", StringComparison.OrdinalIgnoreCase));
        // 查无此商品：只读一次商品编码索引，不能出现逐单探明细的 EXISTS 子查询——它是 39 秒整表扫描的来源。
        Assert.DoesNotContain(statements, statement => statement.Contains("EXISTS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task QueryAsync_short_numeric_keyword_is_not_scanned_as_order_guid_fragment()
    {
        // 3 位数字是合法十六进制，但太短，会命中成百上千个订单号：不按订单号搜，只按商品编码精确匹配。
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 100);
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());
        var statements = database.CaptureSql();

        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest("S001", Keyword: "000", Take: 20),
            CancellationToken.None);

        Assert.Empty(response.Orders);
        Assert.DoesNotContain(statements, statement => statement.Contains("INSTR", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("x'); DROP TABLE sales_order;--")]
    [InlineData("a'b")]
    [InlineData("back\\slash'")]
    public async Task QueryAsync_keyword_with_sql_metacharacters_is_treated_as_data(string keyword)
    {
        // 商品编码 IN 列表由 SqlSugar 内联成字符串字面量：单引号必须被转义，恶意关键字不能逃出字面量。
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 20);
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());

        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest("S001", Keyword: keyword, Take: 20),
            CancellationToken.None);

        Assert.Empty(response.Orders);
        // 表还在、数据还在：后续正常查询照常返回目标订单。
        var targetByCode = await repository.QueryAsync(
            new OrderHistoryQueryRequest("S001", Keyword: "SKU-TARGET", Take: 20),
            CancellationToken.None);
        Assert.Equal(OrderHistorySqliteFixture.TargetOrderGuid, Assert.Single(targetByCode.Orders).OrderGuid);
    }

    [Fact]
    public async Task QueryAsync_hot_product_falls_back_to_exists_and_returns_newest_orders_first()
    {
        // 明细行数超过冷门上限（1000）的商品，按订单号 IN 会超出参数上限，改用 EXISTS。
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 1_100);
        await database.DbContext.PosmDb.Ado.ExecuteCommandAsync(
            "UPDATE sales_order_detail SET ProductCode = 'HOT-1' WHERE ProductCode LIKE 'P-%'");
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());
        var statements = database.CaptureSql();

        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest("S001", Keyword: "HOT-1", Take: 5),
            CancellationToken.None);

        Assert.Equal(5, response.Orders.Count);
        // fixture 里订单 i 的下单时间 = 基准 - i 秒，目标订单卖的不是 HOT-1；最新的 HOT-1 订单是 1 号。
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), response.Orders[0].OrderGuid);
        Assert.Contains(statements, statement => statement.Contains("EXISTS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sql_server_order_guid_probe_carries_the_query_date_range()
    {
        using var db = CreateSqlServerPreviewClient();

        var sql = SqlSugarOrderHistoryRepository
            .BuildOrderGuidProbeQuery(
                db.Queryable<SalesOrder>(),
                "1004",
                "db596f11",
                new DateTime(2025, 11, 2),
                new DateTime(2026, 11, 2, 23, 59, 59))
            .ToSql();

        // 最早 1 年的下限也要落到探针上，候选里不会冒出窗口外的旧订单号；OrderTime 是覆盖索引的包含列，不加读页数。
        Assert.Equal(2, CountOccurrences(sql.Key, "[OrderTime]"));
        Assert.DoesNotContain("ORDER BY", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sql.Value, parameter => Equals(parameter.Value, new DateTime(2025, 11, 2)));
    }

    [Theory]
    [InlineData(DbType.SqlServer, "2026-10-03", "2026-11-02 23:59:59", true)]
    [InlineData(DbType.SqlServer, "2025-11-02", "2026-11-02 23:59:59", false)] // 按单号定位的 1 年窗口：扫时间索引反而更多
    [InlineData(DbType.SqlServer, "2026-10-03", null, false)]
    [InlineData(DbType.SqlServer, null, "2026-11-02 23:59:59", false)]
    [InlineData(DbType.Sqlite, "2026-10-03", "2026-11-02 23:59:59", false)]    // 表提示是 SQL Server 语法
    public void Order_time_index_hint_only_for_sql_server_windows_up_to_one_month(
        DbType dbType,
        string? from,
        string? to,
        bool expected)
    {
        Assert.Equal(
            expected,
            SqlSugarOrderHistoryRepository.ShouldHintOrderTimeIndex(
                dbType,
                from is null ? null : DateTime.Parse(from),
                to is null ? null : DateTime.Parse(to)));
    }

    [Fact]
    public void Sql_server_order_time_index_hint_lands_on_the_outer_order_table()
    {
        using var db = CreateSqlServerPreviewClient();
        var codes = new List<string> { "G006243" };

        var sql = db.Queryable<SalesOrder>()
            .Where(x => x.BranchCode == "1011")
            .Where(x => SqlFunc.Subqueryable<SalesOrderDetail>()
                .Where(line => line.OrderGuid == x.OrderGuid && codes.Contains(line.ProductCode!))
                .Any())
            .With(SqlSugarOrderHistoryRepository.OrderTimeIndexHint)
            .ToSql();

        // 提示必须挂在外层 sales_order 上；挂到明细子查询或位置错了都会让 SQL Server 报错或用错索引。
        Assert.Contains("[sales_order] [x] WITH(INDEX(IX_sales_order_OrderTime))", sql.Key, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(sql.Key, "IX_sales_order_OrderTime"));
    }

    [Fact]
    public async Task QueryAsync_hot_product_within_a_date_window_still_returns_newest_orders_first()
    {
        // 带日期窗口时（交易历史页按商品搜）热门商品路径在 SQLite 上不加表提示，结果仍按时间倒序且受窗口约束。
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 1_100);
        await database.DbContext.PosmDb.Ado.ExecuteCommandAsync(
            "UPDATE sales_order_detail SET ProductCode = 'HOT-1' WHERE ProductCode LIKE 'P-%'");
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());

        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest(
                "S001",
                // fixture 的订单墙钟时间在 2026-08-25 09:41~10:00；按悉尼时区换算后窗口要覆盖到这段。
                SoldFrom: DateTimeOffset.Parse("2026-08-24T00:00:00Z"),
                SoldTo: DateTimeOffset.Parse("2026-08-25T23:59:59Z"),
                Keyword: "HOT-1",
                Take: 3),
            CancellationToken.None);

        Assert.Equal(3, response.Orders.Count);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), response.Orders[0].OrderGuid);
    }

    private static async Task InsertProductAsync(OrderHistorySqliteFixture database, string productCode, string barcode)
    {
        await database.DbContext.PosmDb.Insertable(new Product
        {
            UUID = Guid.NewGuid().ToString("D"),
            ProductCode = productCode,
            ProductName = productCode,
            Barcode = barcode
        }).ExecuteCommandAsync();
    }

    private static async Task InsertOrderWithLineAsync(
        OrderHistorySqliteFixture database,
        string orderGuid,
        string productCode,
        string barcode)
    {
        await database.DbContext.PosmDb.Insertable(new SalesOrder
        {
            OrderGuid = orderGuid,
            OrderTime = DateTime.Parse("2026-08-25T10:00:00Z").ToUniversalTime(),
            BranchCode = "S001",
            DeviceCode = "POS01",
            CashierName = "Cashier",
            TotalAmount = 5m,
            ActualAmount = 5m,
            ItemCount = 1,
            Status = 1
        }).ExecuteCommandAsync();
        await database.DbContext.PosmDb.Insertable(new SalesOrderDetail
        {
            OrderDetailGuid = Guid.NewGuid().ToString("D"),
            OrderGuid = orderGuid,
            ProductCode = productCode,
            ProductName = productCode,
            Barcode = barcode,
            Quantity = 1,
            Price = 5m,
            ActualAmount = 5m
        }).ExecuteCommandAsync();
    }

    private static SqlSugarClient CreateSqlServerPreviewClient() => new(new ConnectionConfig
    {
        ConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=hbpos-sql-preview;Trusted_Connection=True;",
        DbType = DbType.SqlServer,
        InitKeyType = InitKeyType.Attribute,
        IsAutoCloseConnection = true
    });

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.OrdinalIgnoreCase);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }
    private static async Task AssertFastLookupAsync(
        SqlSugarOrderHistoryRepository repository,
        OrderHistorySqliteFixture database,
        string keyword)
    {
        var statements = database.CaptureSql();
        var stopwatch = Stopwatch.StartNew();

        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest(
                "S001",
                SoldFrom: DateTimeOffset.Parse("2026-08-25T00:00:00Z"),
                SoldTo: DateTimeOffset.Parse("2026-08-25T23:59:59Z"),
                Keyword: keyword,
                Take: 100),
            CancellationToken.None);

        stopwatch.Stop();
        var order = Assert.Single(response.Orders);
        Assert.Equal(OrderHistorySqliteFixture.TargetOrderGuid, order.OrderGuid);
        Assert.True(
            stopwatch.Elapsed < QueryBudget,
            $"在线订单按 {keyword} 查询耗时 {stopwatch.Elapsed.TotalMilliseconds:F1} ms，超过 2 秒预算。");

        var selects = statements
            .Where(statement => statement.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .ToList();
        // 冷门商品：读商品主档、读一次明细 ProductCode 探针、再按订单号 IN 取行。
        // 明细表只被探针读一次，且不带条码 / 备注文本条件，也没有逐单探明细的 EXISTS。
        var detailSelect = Assert.Single(
            selects,
            statement => statement.Contains("sales_order_detail", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ProductCode", detailSelect, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Barcode", detailSelect, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remark", detailSelect, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(selects, statement => statement.Contains("EXISTS", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class OrderHistorySqliteFixture : IDisposable
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-order-history-{Guid.NewGuid():N}.db");
        private readonly SqlSugarClient client;

        private OrderHistorySqliteFixture()
        {
            client = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databasePath}",
                DbType = DbType.Sqlite,
                InitKeyType = InitKeyType.Attribute,
                IsAutoCloseConnection = true
            });
            // 商品主档在生产里是 HBweb 库的 Product 表；测试里 MainDb 与 PosmDb 指向同一个 SQLite，表建在一起。
            client.CodeFirst.InitTables<SalesOrder, SalesOrderDetail, PaymentDetail, Product>();
            DbContext = CreateDbContext(client);
        }

        public static Guid TargetOrderGuid { get; } = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        public HbposSqlSugarContext DbContext { get; }

        public static async Task<OrderHistorySqliteFixture> CreateAsync(int orderCount)
        {
            var fixture = new OrderHistorySqliteFixture();
            await fixture.SeedAsync(orderCount);
            return fixture;
        }

        public List<string> CaptureSql()
        {
            var statements = new List<string>();
            client.Aop.OnLogExecuting = (sql, _) => statements.Add(sql);
            return statements;
        }

        public void Dispose()
        {
            client.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                try
                {
                    File.Delete(databasePath);
                }
                catch (IOException)
                {
                    // SQLite 可能短暂占用测试数据库文件，不影响查询断言。
                }
            }
        }

        private async Task SeedAsync(int orderCount)
        {
            var orderTime = DateTime.Parse("2026-08-25T10:00:00Z").ToUniversalTime();
            var orders = Enumerable.Range(1, orderCount)
                .Select(index => new SalesOrder
                {
                    OrderGuid = Guid.Parse($"00000000-0000-0000-0000-{index:D12}").ToString("D"),
                    OrderTime = orderTime.AddSeconds(-index),
                    BranchCode = "S001",
                    DeviceCode = "POS01",
                    CashierName = "Cashier",
                    TotalAmount = 10m,
                    ActualAmount = 10m,
                    ItemCount = 1,
                    Status = 1
                })
                .Append(new SalesOrder
                {
                    OrderGuid = TargetOrderGuid.ToString("D"),
                    OrderTime = orderTime,
                    BranchCode = "S001",
                    DeviceCode = "POS01",
                    CashierName = "Cashier",
                    TotalAmount = 20m,
                    ActualAmount = 20m,
                    ItemCount = 1,
                    Status = 1
                })
                .Append(new SalesOrder
                {
                    OrderGuid = Guid.Parse("ffffffff-1111-2222-3333-444444444444").ToString("D"),
                    OrderTime = orderTime,
                    BranchCode = "S002",
                    DeviceCode = "POS02",
                    CashierName = "Other",
                    TotalAmount = 20m,
                    ActualAmount = 20m,
                    ItemCount = 1,
                    Status = 1
                })
                .ToList();
            var lines = orders.Select((order, index) => new SalesOrderDetail
            {
                OrderDetailGuid = Guid.NewGuid().ToString("D"),
                OrderGuid = order.OrderGuid!,
                ProductCode = order.OrderGuid == TargetOrderGuid.ToString("D") || order.BranchCode == "S002"
                    ? "SKU-TARGET"
                    : $"P-{index:D6}",
                ProductName = "Tea",
                Barcode = order.OrderGuid == TargetOrderGuid.ToString("D") || order.BranchCode == "S002"
                    ? "930000000001"
                    : $"9301{index:D8}",
                Quantity = 1,
                Price = 10m,
                ActualAmount = 10m,
                Remark = order.OrderGuid == TargetOrderGuid.ToString("D") || order.BranchCode == "S002"
                    ? "priceSource=1;itemNo=ITEM-TARGET"
                    : $"priceSource=1;itemNo=ITEM-{index:D6}"
            }).ToList();

            foreach (var batch in orders.Chunk(500))
            {
                await client.Insertable(batch).ExecuteCommandAsync();
            }

            foreach (var batch in lines.Chunk(500))
            {
                await client.Insertable(batch).ExecuteCommandAsync();
            }

            // 目标商品在主档里的编码、货号、条码，与上面明细里的 SKU-TARGET / ITEM-TARGET / 930000000001 一一对应。
            await client.Insertable(new Product
            {
                UUID = Guid.NewGuid().ToString("D"),
                ProductCode = "SKU-TARGET",
                ProductName = "Tea",
                ItemNumber = "ITEM-TARGET",
                Barcode = "930000000001"
            }).ExecuteCommandAsync();

            await client.Ado.ExecuteCommandAsync(
                "CREATE INDEX IX_Test_SalesOrder_Scope ON sales_order(BranchCode, OrderTime DESC);");
            await client.Ado.ExecuteCommandAsync(
                "CREATE INDEX IX_Test_SalesOrderDetail_OrderGuid ON sales_order_detail(OrderGuid);");
        }

        private static HbposSqlSugarContext CreateDbContext(ISqlSugarClient posmDb)
        {
            var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HbposSqlSugarContext));
            SetAutoProperty(context, nameof(HbposSqlSugarContext.MainDb), posmDb);
            SetAutoProperty(context, nameof(HbposSqlSugarContext.PosmDb), posmDb);
            return context;
        }

        private static void SetAutoProperty(HbposSqlSugarContext context, string propertyName, ISqlSugarClient value)
        {
            var backingField = typeof(HbposSqlSugarContext).GetField(
                $"<{propertyName}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(backingField);
            backingField!.SetValue(context, value);
        }
    }
}
