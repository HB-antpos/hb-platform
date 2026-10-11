using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBSalesRecord;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.SqlClient;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>仅针对 BATCH_SALES_SQLSERVER_TEST_CONNECTION 指向的无数据卷隔离 SQL Server 运行。</summary>
[Trait("Category", "SQL")]
public sealed partial class BatchProductSalesAnalysisSqlServerIntegrationTests : IAsyncLifetime
{
    private const string ConnectionEnvironmentVariable = "BATCH_SALES_SQLSERVER_TEST_CONNECTION";
    private readonly string? _master = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
    private readonly string _id = Guid.NewGuid().ToString("N")[..12];
    private string CatalogName => $"hb_batch_sales_test_catalog_{_id}";
    private string PosmName => $"hb_batch_sales_test_posm_{_id}";
    private string HbsName => $"hb_batch_sales_test_hbs_{_id}";
    private SqlSugarClient? _catalog;
    private SqlSugarClient? _posm;
    private SqlSugarClient? _hbs;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_master)) return;
        EnsureLoopback(_master);
        using var master = Client(_master);
        try
        {
            foreach (var name in DatabaseNames)
                await master.Ado.ExecuteCommandAsync($"CREATE DATABASE {Quote(name)}");
            // 与生产一致：HBweb、POSM 允许快照隔离（日统计已提交读依赖它），HOT_POS_CLOUD 不开。
            foreach (var name in new[] { CatalogName, PosmName })
                await master.Ado.ExecuteCommandAsync($"ALTER DATABASE {Quote(name)} SET ALLOW_SNAPSHOT_ISOLATION ON");
            _catalog = Client(WithDatabase(_master, CatalogName));
            _posm = Client(WithDatabase(_master, PosmName));
            _hbs = Client(WithDatabase(_master, HbsName));
            await CreateSchemaAsync();
        }
        catch
        {
            DisposeClients();
            await DropDatabasesAsync(master);
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_master)) return;
        DisposeClients();
        using var master = Client(_master);
        await DropDatabasesAsync(master);
    }

    [BatchSalesSqlServerFact]
    public async Task Snapshot_SQLServer并发去重和租约隔离_原子发布聚合结果()
    {
        _catalog!.CodeFirst.InitTables<BatchProductSalesDiscountSnapshot, SalesStatisticRefreshState, ProductStoreDailySalesStatistic>();
        // 隔离测试库复现生产日统计金额的 decimal(18,4) 存储精度。
        await _catalog.Ado.ExecuteCommandAsync("ALTER TABLE dbo.ProductStoreDailySalesStatistic ALTER COLUMN TotalAmount decimal(18,4) NOT NULL");
        var day = new DateTime(2025, 9, 1);
        await _catalog.Insertable(new SalesStatisticRefreshState { StatisticType = SalesStatisticType.ProductStoreDaily, Date = day, Status = "Fresh", CompletedAtUtc = day }).ExecuteCommandAsync();
        await _catalog.Insertable(new ProductStoreDailySalesStatistic { Date = day, ProductCode = "P1", BranchCode = "S1", SupplierCode = "A", TotalQuantity = 2, TotalAmount = 29.9921m }).ExecuteCommandAsync();
        var reader = new BatchProductSalesStatisticReader(_catalog);
        var version = await reader.StatusAsync(day, day, default);
        var store = new BatchProductSalesDiscountStore(_catalog);
        using var otherDb = Client(WithDatabase(_master!, CatalogName));
        var other = new BatchProductSalesDiscountStore(otherDb);
        var request = BatchProductSalesDiscountStore.Create("P1", day, day, ["S1"], version.Version);
        await Task.WhenAll(store.FindOrQueueAsync(request, default), other.FindOrQueueAsync(request, default));
        Assert.Equal(1, await _catalog.Queryable<BatchProductSalesDiscountSnapshot>().CountAsync());
        var claims = await Task.WhenAll(store.ClaimAsync(DateTime.UtcNow), other.ClaimAsync(DateTime.UtcNow));
        var job = Assert.Single(claims.Where(c => c != null))!;
        Assert.Null((await other.FindAsync(job.Id))!.PayloadJson);
        var state = await store.ComputeAsync(job, (_, _, _, _, _) => Task.FromResult(new List<BatchProductSalesAggregateRow> { new() { Date = day, ProductCode = "P1", BranchCode = "S1", Quantity = 2, DiscountQuantity = 2, SalesAmount = 29.99205m } }), default);
        Assert.Equal("Fresh", state);
        var published = (await other.FindAsync(job.Id))!;
        Assert.Equal("Fresh", published.Status);
        Assert.Contains("\"DiscountQuantity\":2", published.PayloadJson);
        Assert.Contains("\"SalesAmount\":29.9921", published.PayloadJson);
        Assert.False(await other.FinishAsync(job, "Failed", null, DateTime.UtcNow));
    }

    [BatchSalesSqlServerFact]
    public async Task ReadAsync_POSM支付分摊部分退货排重设备回填和错误原单均按事实口径()
    {
        var day = new DateTime(2025, 6, 10);
        await SeedProductsAsync(("P1", "001", null), ("P2", "002", null));
        await _posm!.Insertable(new POSM_设备注册信息表
        {
            设备硬件识别码 = "hardware-1", 系统设备编号 = "D1", 分店代码 = "S1",
            设备类型 = "POS", 设备系统 = "Windows", 设备状态 = 1, 是否允许交易 = true,
            设备授权码 = "isolated", 创建时间 = day,
        }).ExecuteCommandAsync();
        await _posm.Insertable(new SalesOrder[]
        {
            new SalesOrder { OrderGuid = "O1", OrderTime = day.AddHours(10), BranchCode = "", DeviceCode = "D1", Status = 1 },
            new SalesOrder { OrderGuid = "R1", OrderTime = day.AddHours(11), BranchCode = "", DeviceCode = "D1", Status = 1 },
            new SalesOrder { OrderGuid = "R2", OrderTime = day.AddHours(12), BranchCode = "", DeviceCode = "D1", Status = 1 },
            new SalesOrder { OrderGuid = "O-EMPTY", OrderTime = day.AddHours(13), BranchCode = "S1", Status = 1 },
            new SalesOrder { OrderGuid = "R3", OrderTime = day.AddHours(14), BranchCode = "S1", Status = 1 },
        }).ExecuteCommandAsync();
        await _posm.Insertable(new PaymentDetail[]
        {
            new PaymentDetail { PaymentGuid = "PAY-1", OrderGuid = "O1", Amount = 15m },
            // 退货订单付款记录使明细中的负实收金额按真实负收款进入金额口径。
            new PaymentDetail { PaymentGuid = "PAY-R1", OrderGuid = "R1", Amount = -5m },
        }).ExecuteCommandAsync();
        await _posm.Insertable(new SalesOrderDetail[]
        {
            new SalesOrderDetail { OrderDetailGuid = "D-1", OrderGuid = "O1", ProductCode = "P1", Quantity = 2, Price = 10m, Subtotal = 20m, ActualAmount = 20m, DiscountAmount = 0m },
            new SalesOrderDetail { OrderDetailGuid = "D-2", OrderGuid = "O1", ProductCode = "OTHER", Quantity = 1, Price = 10m, Subtotal = 10m, ActualAmount = 10m, DiscountAmount = 0m },
            // 部分退款不能由当前商品价格反推；原单不完整时必须显式未知。
            new SalesOrderDetail { OrderDetailGuid = "RET-DETAIL", OrderGuid = "R1", ProductCode = "P1", Quantity = -1, Price = 10m, Subtotal = 10m, ActualAmount = -5m, DiscountAmount = 5m },
            // 老数据原明细货号为空；退货记录货号有效时仍可继承原单正价证据。
            new SalesOrderDetail { OrderDetailGuid = "D-EMPTY", OrderGuid = "O-EMPTY", ProductCode = "", Quantity = 1, Price = 10m, Subtotal = 10m, ActualAmount = 10m, DiscountAmount = 0m },
        }).ExecuteCommandAsync();
        await _posm.Insertable(new SalesReturnRecord[]
        {
            // 已经在退货订单明细中出现，不能再由退货记录加一次。
            new SalesReturnRecord { ReturnDetailGuid = "RET-DETAIL", ReturnOrderGuid = "R1", OriginalOrderGuid = "O1", OriginalOrderDetailGuid = "D-1", ProductCode = "P1", ReturnQuantity = 1m, ReturnAmount = 5m },
            // 指向 P1 原单的 P2 退货不能串入 P1 查询。
            new SalesReturnRecord { ReturnDetailGuid = "RET-P2", ReturnOrderGuid = "R2", OriginalOrderGuid = "O1", OriginalOrderDetailGuid = "D-1", ProductCode = "P2", ReturnQuantity = 1m, ReturnAmount = 10m },
            new SalesReturnRecord { ReturnDetailGuid = "RET-EMPTY", ReturnOrderGuid = "R3", OriginalOrderGuid = "O-EMPTY", OriginalOrderDetailGuid = "D-EMPTY", ProductCode = "P1", ReturnQuantity = 1m, ReturnAmount = 10m },
        }).ExecuteCommandAsync();

        var rows = await ReadAsync("P1", day, "S1");
        Assert.Equal("S1", Assert.Single(rows.Select(row => row.BranchCode).Distinct())); // BranchCode 为空时回填设备所属分店。
        Assert.Equal(0m, rows.Sum(row => row.Quantity));
        Assert.Equal(1m, rows.Sum(row => row.RegularQuantity));
        Assert.Equal(-1m, rows.Sum(row => row.UnknownQuantity));
        Assert.Equal(2m, rows.Sum(row => row.ReturnQuantity)); // ReturnDetailGuid 排重后只退一次，且空原码退货仅退一次。
        Assert.Equal(-5m, rows.Sum(row => row.SalesAmount)); // 15 * 20 / (20 + 10) - 5 - 10。
        Assert.Equal(1, rows.Sum(row => row.UnknownRowCount));
    }

    [BatchSalesSqlServerFact]
    public async Task ReadAsync_POSM旧POS退款单Status3按净额并入_其退货记录不重复冲减_不归为未知()
    {
        var day = new DateTime(2026, 6, 10);
        await SeedProductsAsync(("P1", "001", null));
        await _posm!.Insertable(new SalesOrder[]
        {
            new SalesOrder { OrderGuid = "O1", OrderTime = day.AddHours(10), BranchCode = "S1", Status = 1 },
            // 旧 POS 的退款是独立的 Status=3 订单：明细、支付均为负数。
            new SalesOrder { OrderGuid = "X1", OrderTime = day.AddHours(11), BranchCode = "S1", Status = 3 },
        }).ExecuteCommandAsync();
        await _posm.Insertable(new PaymentDetail[]
        {
            new PaymentDetail { PaymentGuid = "PAY-1", OrderGuid = "O1", Amount = 20m },
            new PaymentDetail { PaymentGuid = "PAY-X1", OrderGuid = "X1", Amount = -10m },
        }).ExecuteCommandAsync();
        await _posm.Insertable(new SalesOrderDetail[]
        {
            new SalesOrderDetail { OrderDetailGuid = "D-1", OrderGuid = "O1", ProductCode = "P1", Quantity = 2, Price = 10m, Subtotal = 20m, ActualAmount = 20m, DiscountAmount = 0m },
            new SalesOrderDetail { OrderDetailGuid = "D-X1", OrderGuid = "X1", ProductCode = "P1", Quantity = -1, Price = 10m, Subtotal = -10m, ActualAmount = -10m, DiscountAmount = 0m },
        }).ExecuteCommandAsync();
        // 退货记录的 ReturnDetailGuid 与退款单明细 GUID 不重合；它属于 Status=3 订单，只能由退款单自带的负数明细冲减一次。
        await _posm.Insertable(new SalesReturnRecord
        {
            ReturnDetailGuid = "RET-X1", ReturnOrderGuid = "X1", OriginalOrderGuid = "O1", OriginalOrderDetailGuid = "D-1",
            ProductCode = "P1", ReturnQuantity = 1m, ReturnAmount = 10m,
        }).ExecuteCommandAsync();

        var rows = await ReadAsync("P1", day, "S1");

        Assert.Equal(1m, rows.Sum(row => row.Quantity)); // 2 - 1，不是 2 - 1 - 1
        Assert.Equal(10m, rows.Sum(row => row.SalesAmount)); // 20 - 10
        Assert.Equal(1m, rows.Sum(row => row.ReturnQuantity));
        Assert.Equal(0, rows.Sum(row => row.UnknownRowCount)); // 退款单行按自身原价/小计判定，不因缺原单证据变未知
        Assert.Equal(1m, rows.Sum(row => row.RegularQuantity));
    }

    [BatchSalesSqlServerFact]
    public async Task ReadAsync_POSM缺失可选退货表仍可读取已支付销售()
    {
        var day = new DateTime(2026, 6, 10);
        await SeedProductsAsync(("P1", "001", null));
        await _posm!.Insertable(new SalesOrder { OrderGuid = "O1", OrderTime = day, BranchCode = "S1", Status = 1 }).ExecuteCommandAsync();
        await _posm.Insertable(new PaymentDetail { PaymentGuid = "PAY-1", OrderGuid = "O1", Amount = 20m }).ExecuteCommandAsync();
        await _posm.Insertable(new SalesOrderDetail { OrderDetailGuid = "D-1", OrderGuid = "O1", ProductCode = "P1", Quantity = 2, Price = 10m, Subtotal = 20m, ActualAmount = 20m, DiscountAmount = 0m }).ExecuteCommandAsync();
        await _posm.Ado.ExecuteCommandAsync($"DROP TABLE {Quote(_posm.EntityMaintenance.GetTableName(typeof(SalesReturnRecord)))}");

        var row = Assert.Single(await ReadAsync("P1", day, "S1"));
        Assert.Equal(2m, row.Quantity);
        Assert.Equal(20m, row.SalesAmount);
        Assert.Equal(0m, row.ReturnQuantity);
        Assert.Equal("complete", row.Metrics.DiscountStatus);
    }

    [BatchSalesSqlServerFact]
    public async Task ReadAsync_POSM付款分摊保留超过六位的累计精度再按供应商落四位()
    {
        var day = new DateTime(2026, 6, 11);
        await SeedProductsAsync(("P1", "001", null));
        await _posm!.Ado.ExecuteCommandAsync($"ALTER TABLE {Quote(_posm.EntityMaintenance.GetTableName(typeof(PaymentDetail)))} ALTER COLUMN [Amount] decimal(19,4) NULL");
        await _posm.Insertable(new SalesOrder { OrderGuid = "O-PRECISION", OrderTime = day, BranchCode = "S1", Status = 1 }).ExecuteCommandAsync();
        await _posm.Insertable(new PaymentDetail { PaymentGuid = "PAY-PRECISION", OrderGuid = "O-PRECISION", Amount = 13.0026m }).ExecuteCommandAsync();
        await _posm.Insertable(Enumerable.Range(1, 1000).Select(index => new SalesOrderDetail
        {
            OrderDetailGuid = $"D-PRECISION-{index}", OrderGuid = "O-PRECISION", ProductCode = "P1", SupplierCode = "SUP-1",
            Quantity = 1, Price = 1m, Subtotal = 1m, ActualAmount = 1m, DiscountAmount = 0m,
        }).ToList()).ExecuteCommandAsync();

        var row = Assert.Single(await ReadAsync("P1", day, "S1"));
        Assert.Equal(1000m, row.Quantity);
        Assert.Equal(1000m, row.RegularQuantity);
        // 每条为 0.0130026；若 SQL 除法压到六位会得到 13.0030，而非 canonical 的 13.0026。
        Assert.Equal(13.0026m, row.SalesAmount);
    }

    [BatchSalesSqlServerFact]
    public async Task ReadAsync_POSM前导空格映射不能改变unknown供应商分组()
    {
        var day = new DateTime(2026, 6, 12);
        await SeedProductsAsync(("P1", "001", null));
        await _posm!.Ado.ExecuteCommandAsync($"ALTER TABLE {Quote(_posm.EntityMaintenance.GetTableName(typeof(PaymentDetail)))} ALTER COLUMN [Amount] decimal(19,6) NULL");
        await _posm.Insertable(new PosmProductSupplierMapping { ProductCode = " P1", LocalSupplierCode = "SUP-MAPPED" }).ExecuteCommandAsync();
        await _posm.Insertable(new SalesOrder[]
        {
            new() { OrderGuid = "O-LEADING-MAP", OrderTime = day, BranchCode = "S1", Status = 1 },
            new() { OrderGuid = "O-UNKNOWN", OrderTime = day, BranchCode = "S1", Status = 1 },
        }).ExecuteCommandAsync();
        await _posm.Ado.ExecuteCommandAsync($"INSERT INTO {Quote(_posm.EntityMaintenance.GetTableName(typeof(PaymentDetail)))} ([PaymentGuid], [OrderGuid], [Amount]) VALUES ('PAY-LEADING-MAP', 'O-LEADING-MAP', CAST(0.00004 AS decimal(19,6))), ('PAY-UNKNOWN', 'O-UNKNOWN', CAST(0.00004 AS decimal(19,6)))");
        await _posm.Insertable(new SalesOrderDetail[]
        {
            new() { OrderDetailGuid = "D-LEADING-MAP", OrderGuid = "O-LEADING-MAP", ProductCode = "P1", SupplierCode = "", Quantity = 1, Price = 1m, Subtotal = 1m, ActualAmount = 1m, DiscountAmount = 0m },
            new() { OrderDetailGuid = "D-UNKNOWN", OrderGuid = "O-UNKNOWN", ProductCode = "P1", SupplierCode = "UNKNOWN", Quantity = 1, Price = 1m, Subtotal = 1m, ActualAmount = 1m, DiscountAmount = 0m },
        }).ExecuteCommandAsync();

        var row = Assert.Single(await ReadAsync("P1", day, "S1"));
        Assert.Equal(2m, row.Quantity);
        // 空 supplier 必须仍与明确 UNKNOWN 同组。若 trim 了 mapping key，会拆成两个组且各自落为 0。
        Assert.Equal(0.0001m, row.SalesAmount);
    }

    [BatchSalesSqlServerFact]
    public async Task ReadAsync_POSM映射供应商与HBS同供应商必须共同落四位()
    {
        var day = new DateTime(2025, 6, 11);
        await SeedProductsAsync(("P1", "001", null));
        await _posm!.Ado.ExecuteCommandAsync($"ALTER TABLE {Quote(_posm.EntityMaintenance.GetTableName(typeof(PaymentDetail)))} ALTER COLUMN [Amount] decimal(19,6) NULL");
        await _hbs!.Ado.ExecuteCommandAsync($"ALTER TABLE {Quote(_hbs.EntityMaintenance.GetTableName(typeof(SalesOrderDetailRecord)))} ALTER COLUMN [B合计金额] decimal(19,6) NULL");
        await _hbs.Ado.ExecuteCommandAsync($"ALTER TABLE {Quote(_hbs.EntityMaintenance.GetTableName(typeof(SalesOrderDetailRecord)))} ALTER COLUMN [B原价合计金额] decimal(19,6) NULL");
        await _posm.Insertable(new PosmProductSupplierMapping { ProductCode = "P1", LocalSupplierCode = "SUP-1" }).ExecuteCommandAsync();
        await _posm.Insertable(new SalesOrder { OrderGuid = "O-MAPPED", OrderTime = day, BranchCode = "S1", Status = 1 }).ExecuteCommandAsync();
        // 模型默认金额列为两位；这里直接写入隔离表的六位源值，验证事实读取不会在来源侧截断。
        await _posm.Ado.ExecuteCommandAsync($"INSERT INTO {Quote(_posm.EntityMaintenance.GetTableName(typeof(PaymentDetail)))} ([PaymentGuid], [OrderGuid], [Amount]) VALUES ('PAY-MAPPED', 'O-MAPPED', CAST(0.00004 AS decimal(19,6)))");
        await _posm.Insertable(new SalesOrderDetail
        {
            OrderDetailGuid = "D-MAPPED", OrderGuid = "O-MAPPED", ProductCode = "P1", SupplierCode = "",
            Quantity = 1, Price = 1m, Subtotal = 1m, ActualAmount = 1m, DiscountAmount = 0m,
        }).ExecuteCommandAsync();
        await _hbs.Insertable(new SalesOrderMain { ID = 1, B销售单号 = "H-MAPPED", B单据类型 = "1", B结账日期 = day }).ExecuteCommandAsync();
        await _hbs.Ado.ExecuteCommandAsync($"INSERT INTO {Quote(_hbs.EntityMaintenance.GetTableName(typeof(SalesOrderDetailRecord)))} ([ID], [B销售单号], [B分店代码], [B结账日期], [B产品编号], [B供应商ID], [B数量], [B单价], [B原价合计金额], [B合计金额], [B折扣率]) VALUES (1, 'H-MAPPED', 'S1', '{day:yyyy-MM-dd}', 'P1', 'SUP-1', 1, CAST(0.00004 AS decimal(19,6)), CAST(0.00004 AS decimal(19,6)), CAST(0.00004 AS decimal(19,6)), 0)");

        var row = Assert.Single(await ReadAsync("P1", day, "S1"));
        Assert.Equal(2m, row.Quantity);
        Assert.Equal(2m, row.RegularQuantity);
        // 两来源各 0.00004；必须依 SupplierCode 合并成 0.00008 后统一落为 0.0001，不能先分别 round。
        Assert.Equal(0.0001m, row.SalesAmount);
    }

    [BatchSalesSqlServerFact]
    public async Task ReadAsync_HBSales原单退货别名消歧与错误关联保持真实分类()
    {
        var day = new DateTime(2025, 6, 10);
        await SeedProductsAsync(("P1", "001", null), ("P2", "001", null));
        await _catalog!.Insertable(new StoreMultiCodeProduct { StoreCode = "S1", ProductCode = "P1", MultiBarcode = "LOCAL1", IsDeleted = false }).ExecuteCommandAsync();
        await _hbs!.Insertable(new SalesOrderMain[]
        {
            new SalesOrderMain { ID = 1, B销售单号 = "HSALE", B单据类型 = "1", B结账日期 = day },
            new SalesOrderMain { ID = 2, B销售单号 = "HRET", B单据类型 = "3", B原销售单号 = "HSALE", B结账日期 = day },
            new SalesOrderMain { ID = 3, B销售单号 = "HRET-BAD", B单据类型 = "3", B原销售单号 = "HSALE", B结账日期 = day },
            new SalesOrderMain { ID = 4, B销售单号 = "HCONFLICT", B单据类型 = "1", B结账日期 = day },
            new SalesOrderMain { ID = 5, B销售单号 = "HLOCAL", B单据类型 = "1", B结账日期 = day },
            new SalesOrderMain { ID = 6, B销售单号 = "HSALE-EMPTY", B单据类型 = "1", B结账日期 = day },
            new SalesOrderMain { ID = 7, B销售单号 = "HRET-EMPTY", B单据类型 = "3", B原销售单号 = "HSALE-EMPTY", B结账日期 = day },
        }).ExecuteCommandAsync();
        await _hbs.Insertable(new SalesOrderDetailRecord[]
        {
            new SalesOrderDetailRecord { ID = 1, B销售单号 = "HSALE", B分店代码 = "S1", B结账日期 = day, B产品编号 = "P1", B数量 = 2m, B单价 = 10m, B原价合计金额 = 20m, B合计金额 = 20m, B折扣率 = 0m, B退货码 = "RET-CODE" },
            new SalesOrderDetailRecord { ID = 2, B销售单号 = "HRET", B分店代码 = "S1", B结账日期 = day, B产品编号 = "P1", B数量 = 1m, B单价 = 10m, B原价合计金额 = 10m, B合计金额 = 10m, B退货码 = "RET-CODE" },
            // 退货码无法匹配原单时不能伪装为正价或折扣，需保留 unknown。
            new SalesOrderDetailRecord { ID = 3, B销售单号 = "HRET-BAD", B分店代码 = "S1", B结账日期 = day, B产品编号 = "P1", B数量 = 1m, B单价 = 10m, B原价合计金额 = 10m, B合计金额 = 10m, B退货码 = "WRONG-CODE" },
            // 同一全局货号属于 P1/P2，缺 B产品编号 的旧记录不得任意选 P1。
            new SalesOrderDetailRecord { ID = 4, B销售单号 = "HCONFLICT", B分店代码 = "S1", B结账日期 = day, B货号 = "001", B数量 = 9m, B单价 = 10m, B原价合计金额 = 90m, B合计金额 = 90m },
            // 分店多码唯一命中 P1，缺产品编号的旧记录可以安全归属。
            new SalesOrderDetailRecord { ID = 5, B销售单号 = "HLOCAL", B分店代码 = "S1", B结账日期 = day, B条形码 = "LOCAL1", B数量 = 1m, B单价 = 10m, B原价合计金额 = 10m, B合计金额 = 10m, B折扣率 = 0m },
            // 原销售行没有 B产品编号，但 B退货码唯一匹配；退货行的 P1 仍需继承原单分类。
            new SalesOrderDetailRecord { ID = 6, B销售单号 = "HSALE-EMPTY", B分店代码 = "S1", B结账日期 = day, B数量 = 1m, B单价 = 10m, B原价合计金额 = 10m, B合计金额 = 10m, B折扣率 = 0m, B退货码 = "EMPTY-CODE" },
            new SalesOrderDetailRecord { ID = 7, B销售单号 = "HRET-EMPTY", B分店代码 = "S1", B结账日期 = day, B产品编号 = "P1", B数量 = 1m, B单价 = 10m, B原价合计金额 = 10m, B合计金额 = 10m, B退货码 = "EMPTY-CODE" },
        }).ExecuteCommandAsync();

        var rows = await ReadAsync("P1", day, "S1");
        Assert.Equal(0m, rows.Sum(row => row.Quantity)); // 2 - 1 - 1 + 1 - 1；冲突别名 9 件被排除。
        Assert.Equal(1m, rows.Sum(row => row.RegularQuantity));
        Assert.Equal(-1m, rows.Sum(row => row.UnknownQuantity));
        Assert.Equal(3m, rows.Sum(row => row.ReturnQuantity));
        Assert.Equal(0m, rows.Sum(row => row.SalesAmount));
        Assert.Equal(1, rows.Sum(row => row.UnknownRowCount));
    }

    private async Task CreateSchemaAsync()
    {
        // 测试表结构必须由与生产相同的 SqlSugar 模型生成，避免手写最小表遗漏字段或表名。
        _catalog!.CodeFirst.InitTables(typeof(Product), typeof(ProductSetCode), typeof(StoreMultiCodeProduct));
        _posm!.CodeFirst.InitTables(typeof(SalesOrder), typeof(SalesOrderDetail), typeof(PaymentDetail), typeof(SalesReturnRecord),
            typeof(POSM_设备注册信息表), typeof(PosmProductSupplierMapping));
        _hbs!.CodeFirst.InitTables(typeof(SalesOrderMain), typeof(SalesOrderDetailRecord));
        await Task.CompletedTask;
    }

    private async Task SeedProductsAsync(params (string Code, string ItemNumber, string? Barcode)[] products)
    {
        await _catalog!.Insertable(products.Select(product => new Product
        {
            ProductCode = product.Code,
            ItemNumber = product.ItemNumber,
            Barcode = product.Barcode,
            ProductName = product.Code,
            IsDeleted = false,
            IsActive = true,
        }).ToList()).ExecuteCommandAsync();
    }

    private async Task<List<BatchProductSalesAggregateRow>> ReadAsync(string productCode, DateTime day, string storeCode)
    {
        var reader = new BatchProductSalesAnalysisFactReader(_catalog!, _posm!, _hbs!);
        return await reader.ReadAsync([productCode], day, day, [storeCode], CancellationToken.None);
    }

    private IEnumerable<string> DatabaseNames => [CatalogName, PosmName, HbsName];

    private async Task DropDatabasesAsync(SqlSugarClient master)
    {
        foreach (var name in DatabaseNames)
        {
            // 数据库名仅由本测试实例生成；仍显式限定前缀和精确名称才允许清理。
            if (!name.StartsWith("hb_batch_sales_test_", StringComparison.Ordinal))
                throw new InvalidOperationException("拒绝清理非批量销量隔离测试数据库。");
            await master.Ado.ExecuteCommandAsync($"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE {Quote(name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Quote(name)}; END");
        }
    }

    private void DisposeClients()
    {
        _catalog?.Dispose();
        _posm?.Dispose();
        _hbs?.Dispose();
        _catalog = null;
        _posm = null;
        _hbs = null;
    }

    private static SqlSugarClient Client(string connection) => new(new ConnectionConfig
    {
        ConnectionString = connection,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true,
        InitKeyType = InitKeyType.Attribute,
    });

    private static string WithDatabase(string connection, string database)
    {
        var builder = new SqlConnectionStringBuilder(connection) { InitialCatalog = database };
        return builder.ConnectionString;
    }

    private static void EnsureLoopback(string connection)
    {
        var source = new SqlConnectionStringBuilder(connection).DataSource.Trim();
        if (source.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) source = source[4..];
        var host = source.Split(',', 2, StringSplitOptions.TrimEntries)[0].Trim('[', ']');
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && !host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{ConnectionEnvironmentVariable} 只能连接 localhost 或 127.0.0.1 的隔离 SQL Server。");
    }

    private static string Quote(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
}

internal sealed class BatchSalesSqlServerFactAttribute : FactAttribute
{
    public BatchSalesSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BATCH_SALES_SQLSERVER_TEST_CONNECTION")))
            Skip = "未设置 BATCH_SALES_SQLSERVER_TEST_CONNECTION；跳过隔离 SQL Server 集成测试。";
    }
}
