using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.Invoice;
using BlazorApp.Api.Features.StoreOrders.Invoice.Domain;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Infrastructure;
using BlazorApp.Api.Features.StoreOrders.Orders.Infrastructure;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using ClosedXML.Excel;
using iTextSharp.text.pdf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 订单详情汇总（发货 / 退货冲减 / 净额）、发票金额与 documentType 切换、退款单 / 带调整项发票的附件与文件名。
/// 同时对涉及的新过滤 / 汇总做 SQL Server 方言 SQL 文本断言（吸取 #904：SQLite 抓不到 HasValue 被翻成 &lt;&gt;'' 的问题）。
/// </summary>
public sealed class StoreOrderReturnSummaryAndInvoiceTests : IDisposable
{
    private const string OrderGuid = "order-sum";

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public StoreOrderReturnSummaryAndInvoiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(WareHouseOrderDetails),
            typeof(Product),
            typeof(WarehouseProduct),
            typeof(DomesticProduct)
        );
    }

    // ---------- 订单详情汇总 ----------

    [Fact]
    public async Task 汇总拆出发货_退货冲减_净额_退货行数与退回件数()
    {
        await _db.Insertable(new List<WareHouseOrderDetails>
        {
            Line("d1", "P1", quantity: 10, alloc: 10, importPrice: 2m),
            Line("d2", "P2", quantity: 5, alloc: 5, importPrice: 4m),
            Line("d3", "P3", quantity: 0, alloc: -3, importPrice: 2m, reason: "damaged"),
            Line("d4", "P4", quantity: 0, alloc: -2, importPrice: 5m, reason: "wrongItem"),
            // 订了没发：发货 0，金额 0（不计入发货 / 退货任何一边）
            Line("d5", "P5", quantity: 4, alloc: 0, importPrice: 9m),
            // 历史行：发货数量 NULL 视为 0，不计入任何一边
            Line("d6", "P6", quantity: 1, alloc: null, importPrice: 9m),
            // 已软删的退货行不参与
            Line("d7", "P7", quantity: 0, alloc: -9, importPrice: 9m, reason: "other", isDeleted: true),
        }).ExecuteCommandAsync();

        var summary = await StoreOrderDetailQueryStore.BuildSummaryQuery(_db, OrderGuid).FirstAsync();

        Assert.Equal(40m, summary.TotalShipImportAmount);
        Assert.Equal(-16m, summary.TotalReturnImportAmount);
        // 净额 = 发货 + 退货冲减；原字段保持净额语义
        Assert.Equal(24m, summary.TotalAllocatedImportAmount);
        Assert.Equal(summary.TotalShipImportAmount + summary.TotalReturnImportAmount, summary.TotalAllocatedImportAmount);
        Assert.Equal(2, summary.ReturnLineCount);
        Assert.Equal(5m, summary.TotalReturnQuantity);
        Assert.Equal(10m, summary.TotalAllocQuantity);
        // 单据类型按净额判定
        Assert.Equal("invoice", StoreOrderReturnRules.ResolveDocumentType(summary.TotalAllocatedImportAmount));
    }

    [Fact]
    public async Task 只含退货行的订单_净额为负_单据类型为退款单_不报错()
    {
        await _db.Insertable(new List<WareHouseOrderDetails>
        {
            Line("d1", "P1", quantity: 0, alloc: -5, importPrice: 1.5m, reason: "damaged"),
            Line("d2", "P2", quantity: 0, alloc: -2, importPrice: 3.2m, reason: "wrongItem"),
        }).ExecuteCommandAsync();

        var summary = await StoreOrderDetailQueryStore.BuildSummaryQuery(_db, OrderGuid).FirstAsync();

        Assert.Equal(0m, summary.TotalShipImportAmount);
        Assert.Equal(-13.9m, summary.TotalReturnImportAmount);
        Assert.Equal(-13.9m, summary.TotalAllocatedImportAmount);
        Assert.Equal(2, summary.ReturnLineCount);
        Assert.Equal(7m, summary.TotalReturnQuantity);
        Assert.Equal("creditNote", StoreOrderReturnRules.ResolveDocumentType(summary.TotalAllocatedImportAmount));
    }

    [Fact]
    public async Task 没有任何退货行的订单_新增汇总字段全为零_口径与原来一致()
    {
        await _db.Insertable(new List<WareHouseOrderDetails>
        {
            Line("d1", "P1", quantity: 10, alloc: 10, importPrice: 2m),
        }).ExecuteCommandAsync();

        var summary = await StoreOrderDetailQueryStore.BuildSummaryQuery(_db, OrderGuid).FirstAsync();

        Assert.Equal(20m, summary.TotalAllocatedImportAmount);
        Assert.Equal(20m, summary.TotalShipImportAmount);
        Assert.Equal(0m, summary.TotalReturnImportAmount);
        Assert.Equal(0, summary.ReturnLineCount);
        Assert.Equal(0m, summary.TotalReturnQuantity);
    }

    // ---------- SQL Server 方言 SQL 文本断言 ----------

    [Fact]
    public void SqlServer方言_汇总查询按发货数量正负分段_且没有HasValue式的空串比较()
    {
        var sql = StoreOrderDetailQueryStore.BuildSummaryQuery(CreateSqlServerClient(), OrderGuid).ToSql().Key;
        var compact = sql.Replace(" ", string.Empty);

        Assert.Contains("[TotalShipImportAmount]", sql);
        Assert.Contains("[TotalReturnImportAmount]", sql);
        Assert.Contains("[ReturnLineCount]", sql);
        Assert.Contains("[TotalReturnQuantity]", sql);
        // 正发货 / 退货分段：CASE WHEN 发货数量 > 0 / < 0，NULL 发货数量按三值逻辑落到 ELSE
        Assert.Contains("CASE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[detail].[AllocQuantity]>@Const", compact);
        Assert.Contains("[detail].[AllocQuantity]<@Const", compact);
        // 可空数值列不能被翻成 <>''（SQL Server 把 '' 转成 0，值为 0 的行会被排除，SQLite 发现不了，#904）
        Assert.DoesNotContain("<>''", compact);
        Assert.DoesNotContain("<>@", compact.Replace("<>@OrderGUID", string.Empty));
    }

    [Fact]
    public void SqlServer方言_最近一次正发货进口价查询_直接比较发货数量与进口价_不带空串比较()
    {
        var sql = WarehouseOrderPersistenceQuery().ToSql().Key;
        var compact = sql.Replace(" ", string.Empty);

        Assert.Contains("[AllocQuantity]>@Const", compact);
        Assert.Contains("[ImportPrice]>@Const", compact);
        Assert.Contains("TOP1", compact.ToUpperInvariant());
        Assert.Contains("ORDERBY[CreatedAt]DESC", compact);
        Assert.DoesNotContain("<>''", compact);
    }

    [Fact]
    public void SqlServer方言_拣货过滤_NULL发货数量显式写IS_NULL_不会把NULL行排除()
    {
        // 与 WarehousePickingQueries / WarehousePickingService 里退货行排除条件同一写法：
        // (AllocQuantity IS NULL OR AllocQuantity >= 0)。写成 !(AllocQuantity < 0) 会在 SQL 里把 NULL 行一并排除。
        var sql = CreateSqlServerClient()
            .Queryable<WareHouseOrderDetails>()
            .Where(detail =>
                detail.OrderGUID == "o1"
                && !detail.IsDeleted
                && (detail.AllocQuantity == null || detail.AllocQuantity >= 0)
            )
            .ToSql()
            .Key;
        var compact = sql.Replace(" ", string.Empty);

        Assert.Contains("[AllocQuantity]ISNULL", compact);
        Assert.Contains("[AllocQuantity]>=@Const", compact);
        Assert.DoesNotContain("<>''", compact);
    }

    private static ISugarQueryable<StoreOrderManagementPersistence.LastShippedImportPriceRow> WarehouseOrderPersistenceQuery() =>
        StoreOrderManagementPersistence.BuildLastShippedImportPriceQuery(
            CreateSqlServerClient(),
            "1013",
            "P-CUP",
            "order-1"
        );

    private static SqlSugarClient CreateSqlServerClient() =>
        new(new ConnectionConfig
        {
            DbType = DbType.SqlServer,
            ConnectionString = "Server=127.0.0.1;Database=x;User Id=x;Password=x;",
            IsAutoCloseConnection = true,
        });

    // ---------- 发票明细规则 ----------

    [Fact]
    public void 发票明细_净额为正仍是发票_退货作为调整项_金额为净额()
    {
        // 设计图 ③ A：发货为主、夹带退货
        var dto = StoreOrderInvoiceDetailRules.ToDto(Snapshot(
            Ship("l1", "Cotton Towel 2pk", alloc: 30, price: 2.50m),
            Ship("l2", "000 Shopping bag", alloc: 12, price: 0.20m),
            Return("l3", "Kids Water Bottle 500ml", alloc: -5, price: 1.50m, reason: "damaged")
        ));

        Assert.Equal(69.9m, dto.TotalAllocatedImportAmount);
        Assert.Equal(77.4m, dto.TotalShipImportAmount);
        Assert.Equal(-7.5m, dto.TotalReturnImportAmount);
        Assert.Equal(1, dto.ReturnLineCount);
        Assert.Equal(5m, dto.TotalReturnQuantity);
        Assert.Equal("invoice", dto.DocumentType);
        var returned = dto.Items.Single(item => item.DetailGUID == "l3");
        Assert.Equal(-7.5m, returned.AllocatedImportAmount);
        Assert.Equal("damaged", returned.ReturnReason);
    }

    [Theory]
    [InlineData(-5, 1.50, "creditNote")]
    [InlineData(0, 1.50, "invoice")]
    public void 发票明细_只有退货行时净额为负是退款单_没有任何行是0元发票(int alloc, double price, string expected)
    {
        var dto = StoreOrderInvoiceDetailRules.ToDto(Snapshot(
            alloc == 0
                ? Ship("l1", "Nothing shipped", alloc: 0, price: (decimal)price)
                : Return("l1", "Kids Water Bottle 500ml", alloc: alloc, price: (decimal)price, reason: "damaged")
        ));

        Assert.Equal(expected, dto.DocumentType);
    }

    // ---------- 附件：发票 / 退款单 ----------

    [Fact]
    public async Task 净额为负_生成退款单_标题文件名与金额都切换_带原因文案_不含收款信息()
    {
        // 设计图 ③ B
        var order = StoreOrderInvoiceDetailRules.ToDto(Snapshot(
            Return("l1", "Kids Water Bottle 500ml", alloc: -5, price: 1.50m, reason: "damaged"),
            Return("l2", "Ceramic Mug White", alloc: -2, price: 3.20m, reason: "wrongItem")
        ));

        var bundle = await GenerateAsync(order);

        Assert.Equal("creditNote", bundle.DocumentType);
        var pdf = bundle.Attachments.Single(item => item.ContentType == "application/pdf");
        var excel = bundle.Attachments.Single(item => item.ContentType.Contains("spreadsheetml"));
        Assert.StartsWith("CreditNote_S001_SO001_", pdf.FileName);
        Assert.StartsWith("CreditNote_S001_SO001_", excel.FileName);

        var pdfText = ExtractPdfText(pdf.Bytes);
        Assert.Contains("ADJUSTMENT NOTE", pdfText);
        Assert.Contains("CREDIT", pdfText);
        Assert.Contains("ADJUSTMENT NOTE NO. SO001", pdfText);
        Assert.Contains("CREDIT TO:", pdfText);
        Assert.Contains(ReturnReference, pdfText);
        Assert.Contains("Returned goods", pdfText);
        Assert.Contains("Reason: damaged", pdfText);
        Assert.Contains("Reason: wrong item sent", pdfText);
        // 净额 -13.90，GST 同向冲减 -1.39，运费 0 → Total Credit = -15.29（设计图 ③ B）
        Assert.Contains("-$13.90", pdfText);
        Assert.Contains("-$1.39", pdfText);
        Assert.Contains("Total Credit:", pdfText);
        Assert.Contains("-$15.29", pdfText);
        Assert.Contains("This credit adjusts the GST previously charged on the returned goods.", pdfText);
        // 退款单不是付款通知
        Assert.DoesNotContain("PAYMENT DETAIL", pdfText);
        Assert.DoesNotContain("208034605", pdfText);
        Assert.DoesNotContain("INVOICE NO.", pdfText);

        using var workbook = new XLWorkbook(new MemoryStream(excel.Bytes));
        var sheet = workbook.Worksheet("Credit Note");
        Assert.Equal("ADJUSTMENT NOTE", sheet.Cell(1, 1).GetString());
        Assert.Equal("ADJUSTMENT NOTE NO. SO001", sheet.Cell(2, 1).GetString());
        Assert.Equal("CREDIT TO:", sheet.Cell(3, 1).GetString());
        Assert.Equal("Reason", sheet.Cell(7, 10).GetString());
        var cells = sheet.CellsUsed().Select(cell => cell.GetString()).ToList();
        Assert.Contains("Returned goods", cells);
        Assert.Contains("damaged", cells);
        Assert.Contains("wrong item sent", cells);
        Assert.Contains("Total Credit:", cells);
        var subTotalRow = sheet.CellsUsed().Single(cell => cell.GetString() == "Sub-Total:").Address.RowNumber;
        Assert.Equal(-13.9m, sheet.Cell(subTotalRow, 9).GetValue<decimal>());
        Assert.Equal(-1.39m, sheet.Cell(subTotalRow + 1, 9).GetValue<decimal>());
        Assert.Equal(-15.29m, sheet.Cell(subTotalRow + 3, 9).GetValue<decimal>());
    }

    [Fact]
    public async Task 净额为正但含退货行_仍是发票_退货行进Adjustments段_净额与GST同口径()
    {
        var order = StoreOrderInvoiceDetailRules.ToDto(Snapshot(
            Ship("l1", "Cotton Towel 2pk", alloc: 30, price: 2.50m),
            Ship("l2", "000 Shopping bag", alloc: 12, price: 0.20m),
            Return("l3", "Kids Water Bottle 500ml", alloc: -5, price: 1.50m, reason: "damaged")
        ));
        order.ShippingFee = 0m;

        var bundle = await GenerateAsync(order);

        Assert.Equal("invoice", bundle.DocumentType);
        var pdf = bundle.Attachments.Single(item => item.ContentType == "application/pdf");
        var excel = bundle.Attachments.Single(item => item.ContentType.Contains("spreadsheetml"));
        Assert.StartsWith("Invoice_S001_SO001_", pdf.FileName);

        var pdfText = ExtractPdfText(pdf.Bytes);
        Assert.Contains("INVOICE NO. SO001", pdfText);
        Assert.Contains("Goods supplied", pdfText);
        Assert.Contains("Adjustments / Returned goods", pdfText);
        Assert.Contains("Reason: damaged", pdfText);
        // 净额 69.90，GST 6.99，Total 76.89（设计图 ③ A）
        Assert.Contains("$69.90", pdfText);
        Assert.Contains("$6.99", pdfText);
        Assert.Contains("$76.89", pdfText);
        Assert.Contains("PAYMENT DETAIL: DIRECT DEBIT", pdfText);
        Assert.Contains("Returned goods are credited at the price supplied and netted against this invoice.", pdfText);
        Assert.DoesNotContain("ADJUSTMENT NOTE", pdfText);

        using var workbook = new XLWorkbook(new MemoryStream(excel.Bytes));
        var sheet = workbook.Worksheet("Invoice");
        Assert.Equal("INVOICE", sheet.Cell(1, 1).GetString());
        Assert.Equal("Reason", sheet.Cell(7, 10).GetString());
        var cells = sheet.CellsUsed().Select(cell => cell.GetString()).ToList();
        Assert.Contains("Adjustments / Returned goods", cells);
        Assert.Contains("damaged", cells);
        var subTotalRow = sheet.CellsUsed().Single(cell => cell.GetString() == "Sub-Total:").Address.RowNumber;
        Assert.Equal(69.9m, sheet.Cell(subTotalRow, 9).GetValue<decimal>());
        Assert.Equal(76.89m, sheet.Cell(subTotalRow + 3, 9).GetValue<decimal>());
    }

    [Fact]
    public async Task 没有退货行的普通发票_附件与原来完全一致_不出现新增段落与列()
    {
        var order = StoreOrderInvoiceDetailRules.ToDto(Snapshot(
            Ship("l1", "Cotton Towel 2pk", alloc: 30, price: 2.50m)
        ));

        var bundle = await GenerateAsync(order);

        Assert.Equal("invoice", bundle.DocumentType);
        var pdf = bundle.Attachments.Single(item => item.ContentType == "application/pdf");
        var excel = bundle.Attachments.Single(item => item.ContentType.Contains("spreadsheetml"));
        Assert.StartsWith("Invoice_S001_SO001_", pdf.FileName);
        var pdfText = ExtractPdfText(pdf.Bytes);
        Assert.DoesNotContain("Adjustments", pdfText);
        Assert.DoesNotContain("Goods supplied", pdfText);
        Assert.DoesNotContain("Returned goods", pdfText);
        using var workbook = new XLWorkbook(new MemoryStream(excel.Bytes));
        Assert.True(workbook.Worksheet("Invoice").Cell(7, 10).IsEmpty());
    }

    [Fact]
    public void 邮件任务默认主题随单据类型切换()
    {
        // 前端发信弹窗自带主题；后端兜底主题必须随类型切换，防止退款单发成发票主题。
        var source = File.ReadAllText(ResolvePath("BlazorApp.Api", "Services", "React", "StoreOrderInvoiceEmailJobService.cs"));

        Assert.Contains("$\"Credit note {bundle.OrderNo ?? bundle.OrderGUID}\"", source);
        Assert.Contains("\"creditNote\"", source);
    }

    // ---------- 辅助 ----------

    private const string ReturnReference = "Return of goods previously supplied";

    private static async Task<StoreOrderInvoiceAttachmentBundle> GenerateAsync(StoreOrderCartDto order)
    {
        order.OrderGUID = "order-1";
        order.OrderNo = "SO001";
        order.StoreCode = "S001";
        order.StoreName = "TestStore (1042)";
        order.StoreAddress = "1 Test Street";
        order.StoreContactEmail = "customer@example.com";
        order.OrderDate = new DateTime(2026, 10, 21);
        var reader = new Mock<IStoreOrderInvoiceDetailReader>(MockBehavior.Strict);
        reader
            .Setup(item => item.GetInvoiceDetailAsync("order-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreOrderCartDto?>.OK(order));
        var service = new StoreOrderInvoiceAttachmentService(
            reader.Object,
            NullLogger<StoreOrderInvoiceAttachmentService>.Instance
        );

        var result = await service.GenerateAttachmentsAsync("order-1");

        Assert.True(result.Success, result.Message);
        return result.Data!;
    }

    private static StoreOrderInvoiceDetailSnapshot Snapshot(params StoreOrderInvoiceLineSnapshot[] lines) =>
        new(new StoreOrderInvoiceHeaderSnapshot { OrderGuid = "order-1", OrderNo = "SO001" }, lines);

    private static StoreOrderInvoiceLineSnapshot Ship(string guid, string name, decimal alloc, decimal price) => new()
    {
        DetailGuid = guid,
        ProductCode = guid,
        ItemNumber = $"HB-{guid}",
        Barcode = $"93000{guid}",
        ProductName = name,
        Quantity = alloc,
        AllocQuantity = alloc,
        DetailImportPrice = price,
        RetailPrice = 9.99m,
    };

    private static StoreOrderInvoiceLineSnapshot Return(string guid, string name, decimal alloc, decimal price, string reason)
    {
        var line = Ship(guid, name, alloc, price);
        line.Quantity = 0;
        line.ReturnReason = reason;
        return line;
    }

    private static WareHouseOrderDetails Line(
        string detailGuid,
        string productCode,
        decimal quantity,
        decimal? alloc,
        decimal importPrice,
        string? reason = null,
        bool isDeleted = false
    ) => new()
    {
        DetailGUID = detailGuid,
        OrderGUID = OrderGuid,
        StoreCode = "1013",
        ProductCode = productCode,
        Quantity = quantity,
        AllocQuantity = alloc,
        ImportPrice = importPrice,
        ImportAmount = quantity * importPrice,
        ReturnReason = reason,
        IsDeleted = isDeleted,
    };

    private static string ExtractPdfText(byte[] pdfBytes)
    {
        using var reader = new PdfReader(pdfBytes);
        return string.Join(
            "\n",
            Enumerable
                .Range(1, reader.NumberOfPages)
                .Select(page => System.Text.Encoding.Latin1.GetString(reader.GetPageContent(page)))
        );
    }

    private static string ResolvePath(
        string first,
        string second,
        string third,
        string fourth,
        [System.Runtime.CompilerServices.CallerFilePath] string testFilePath = ""
    )
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("无法解析测试文件目录");
        return Path.GetFullPath(Path.Combine(testDirectory, "..", first, second, third, fourth));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }
}
