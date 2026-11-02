using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    public async Task QueryAsync_item_number_suffix_treats_sql_wildcards_as_literals(
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

        await database.DbContext.PosmDb.Updateable<SalesOrderDetail>()
            .SetColumns(line => line.Remark == $"priceSource=1;itemNo={itemNumber}")
            .Where(line => line.OrderGuid == targetOrderGuid)
            .ExecuteCommandAsync();
        await database.DbContext.PosmDb.Updateable<SalesOrderDetail>()
            .SetColumns(line => line.Remark == $"priceSource=1;itemNo={wildcardEquivalent}")
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
        var select = Assert.Single(statements, statement =>
            statement.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            statement.Contains("FROM `sales_order`", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("INSTR", select, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("`Remark` like", select, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("jmq02")]
    [InlineData("-")]
    public void Sql_server_keyword_filter_does_not_emit_boolean_predicate_parameter(string keyword)
    {
        using var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=hbpos-sql-preview;Trusted_Connection=True;",
            DbType = DbType.SqlServer,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = true
        });
        var sql = SqlSugarOrderHistoryRepository.ApplyKeywordFilter(
                db.Queryable<SalesOrder>(),
                keyword)
            .ToSql();

        Assert.Contains("CHARINDEX", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXISTS", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sql.Value, parameter => parameter.Value is bool);
        Assert.DoesNotContain(sql.Value, parameter => Equals(parameter.Value, string.Empty));
    }

    [Fact]
    public void Sql_server_item_number_suffix_uses_parameterized_charindex_instead_of_like()
    {
        using var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=hbpos-sql-preview;Trusted_Connection=True;",
            DbType = DbType.SqlServer,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = true
        });
        const string keyword = "ITEM_1%[";
        var itemNumberMarker = $"itemNo={keyword}";
        var itemNumberSuffix = $";{itemNumberMarker}";

        var sql = db.Queryable<SalesOrder>()
            .Where(order =>
                (order.OrderGuid != null && SqlFunc.CharIndexNew(order.OrderGuid, keyword) > 0)
                || SqlFunc.Subqueryable<SalesOrderDetail>()
                    .Where(line => line.OrderGuid == order.OrderGuid
                        && line.Remark != null
                        && (line.Remark == itemNumberMarker
                            || SqlFunc.CharIndexNew(line.Remark, itemNumberSuffix)
                                == SqlFunc.Length(line.Remark) - itemNumberSuffix.Length + 1))
                    .Any())
            .ToSql();

        Assert.Contains("CHARINDEX", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" LIKE ", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sql.Value, parameter => Equals(parameter.Value, itemNumberSuffix));
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
    public async Task QueryAsync_eight_hex_keyword_without_order_guid_match_falls_back_to_barcode_search()
    {
        using var database = await OrderHistorySqliteFixture.CreateAsync(orderCount: 20);
        var repository = new SqlSugarOrderHistoryRepository(database.DbContext, new StubStoreTimeZoneResolver());
        // 8 位纯数字同样是合法十六进制（EAN-8 条码）：订单号里没有它时，必须退回完整关键字过滤。
        var orderGuid = "11111111-2222-4333-8444-555555555555";
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
            ProductCode = "EAN8-ITEM",
            ProductName = "Gum",
            Barcode = "12345678",
            Quantity = 1,
            Price = 5m,
            ActualAmount = 5m
        }).ExecuteCommandAsync();

        var response = await repository.QueryAsync(
            new OrderHistoryQueryRequest("S001", Keyword: "12345678", Take: 20),
            CancellationToken.None);

        var order = Assert.Single(response.Orders);
        Assert.Equal(Guid.Parse(orderGuid), order.OrderGuid);
    }

    [Fact]
    public void Sql_server_short_code_probe_selects_only_order_guid_without_detail_subquery()
    {
        using var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=hbpos-sql-preview;Trusted_Connection=True;",
            DbType = DbType.SqlServer,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = true
        });

        var sql = SqlSugarOrderHistoryRepository
            .BuildShortCodeOrderGuidQuery(db.Queryable<SalesOrder>(), "1004", "db596f11")
            .ToSql();

        Assert.Contains("CHARINDEX", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BranchCode", sql.Key, StringComparison.OrdinalIgnoreCase);
        // 选择列表里出现 Remark / CashierName 等非覆盖索引列，优化器就会改走整表聚集索引扫描。
        Assert.DoesNotContain("CashierName", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remark", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXISTS", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sales_order_detail", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sql.Value, parameter => Equals(parameter.Value, "db596f11"));
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
        Assert.Equal(2, selects.Count);
        Assert.Contains("EXISTS", selects[0], StringComparison.OrdinalIgnoreCase);
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
            client.CodeFirst.InitTables<SalesOrder, SalesOrderDetail, PaymentDetail>();
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
