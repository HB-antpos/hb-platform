using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React.SupplierPreorders;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 供应商节日预订单去年历史（SQLite 真实落库）：快照计算口径（总仓配货排除草稿、供应商进货单按零售价 UUID 回查、窗口外不算）、
/// 系统推荐参考商品与人工指定不被覆盖、下单列表 / 展开行只给本店数据，看不到其他分店的人拿不到其他分店的去年数据。
/// </summary>
public sealed class SupplierPreorderHistorySqliteTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"supplier-preorder-history-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly ManualClock _clock = new(new DateTime(2026, 2, 20, 0, 0, 0, DateTimeKind.Utc));

    public SupplierPreorderHistorySqliteTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_path}",
            DbType = DbType.Sqlite,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = false,
        });
        _db.CodeFirst.InitTables(
            typeof(Store), typeof(User), typeof(UserStore), typeof(Product), typeof(HBLocalSupplier),
            typeof(ProductStoreDailySalesStatistic), typeof(StoreRetailPrice),
            typeof(WareHouseOrder), typeof(WareHouseOrderDetails), typeof(StoreLocalSupplierInvoice), typeof(StoreLocalSupplierInvoiceDetails),
            typeof(SupplierPreorderForm), typeof(SupplierPreorderFormItem), typeof(SupplierPreorderFormStore),
            typeof(SupplierPreorderOrderer), typeof(SupplierPreorderOrdererStore), typeof(SupplierPreorderAuthLog),
            typeof(SupplierPreorderStoreOrder), typeof(SupplierPreorderStoreOrderLine),
            typeof(SupplierPreorderImportProfile), typeof(SupplierPreorderSourceFile), typeof(SupplierPreorderHistorySnapshot));

        _db.Insertable(new[]
        {
            new Store { StoreGUID = "S1", StoreCode = "1001", StoreName = "Orion", IsActive = true },
            new Store { StoreGUID = "S2", StoreCode = "1002", StoreName = "Kawana", IsActive = true },
            new Store { StoreGUID = "S3", StoreCode = "1003", StoreName = "Peninsula", IsActive = true },
        }).ExecuteCommand();
        _db.Insertable(new[]
        {
            new User { UserGUID = "user-1", Username = "orion", Email = "u1@example.test", PasswordHash = "x", FullName = "王店长", IsActive = true },
            new User { UserGUID = "user-2", Username = "area", Email = "u2@example.test", PasswordHash = "x", FullName = "赵区经理", IsActive = true },
        }).ExecuteCommand();
        _db.Insertable(new HBLocalSupplier { Guid = "sup-257", LocalSupplierCode = "257", Name = "Uncle Bills (KFC)", Status = 1 }).ExecuteCommand();
        _db.Insertable(new[]
        {
            new Product { ProductCode = "P-A", ItemNumber = "XD0001815", Barcode = "9333527865090", ProductName = "Flocked Rib Glitter Bauble", LocalSupplierCode = "257", PurchasePrice = 1.02m, IsActive = true },
            new Product { ProductCode = "P-REF", ItemNumber = "XD0001356", Barcode = "9333527800001", ProductName = "Lantern Flocked Bauble 15cm", LocalSupplierCode = "257", PurchasePrice = 1.50m, IsActive = true },
            new Product { ProductCode = "P-EAR", ItemNumber = "XP0000001", Barcode = "9333527800002", ProductName = "Party Earrings", LocalSupplierCode = "257", PurchasePrice = 2.00m, IsActive = true },
        }).ExecuteCommand();

        // 日统计逐行插入（SQLite 批量插入的日期文本格式与查询参数不一致）
        foreach (var (date, branch, product, qty) in new[]
        {
            (new DateTime(2025, 10, 3), "1001", "P-A", 5),
            (new DateTime(2025, 12, 24), "1001", "P-A", 15),
            (new DateTime(2025, 12, 1), "1002", "P-A", 40),
            (new DateTime(2024, 12, 20), "1001", "P-A", 100),   // 窗口外
            (new DateTime(2025, 11, 1), "1001", "P-REF", 8),
            (new DateTime(2025, 11, 2), "1002", "P-REF", 30),
            (new DateTime(2025, 11, 3), "1001", "P-EAR", 2),
        })
        {
            _db.Insertable(new ProductStoreDailySalesStatistic
            {
                Date = date, BranchCode = branch, SupplierCode = "257", ProductCode = product, TotalQuantity = qty, TotalAmount = qty * 4m,
            }).ExecuteCommand();
        }

        // 总仓配货：已出库的算，草稿（FlowStatus = 0）不算
        _db.Insertable(new WareHouseOrder { OrderGUID = "WO-1", StoreCode = "1001", OrderNo = "WO-1", FlowStatus = 2, OutboundDate = new DateTime(2025, 9, 21) }).ExecuteCommand();
        _db.Insertable(new WareHouseOrder { OrderGUID = "WO-DRAFT", StoreCode = "1001", OrderNo = "WO-DRAFT", FlowStatus = 0, OrderDate = new DateTime(2025, 10, 1) }).ExecuteCommand();
        _db.Insertable(new WareHouseOrderDetails { OrderGUID = "WO-1", StoreCode = "1001", ProductCode = "P-A", Quantity = 240, AllocQuantity = 240 }).ExecuteCommand();
        _db.Insertable(new WareHouseOrderDetails { OrderGUID = "WO-DRAFT", StoreCode = "1001", ProductCode = "P-A", Quantity = 999, AllocQuantity = 999 }).ExecuteCommand();

        // 分店供应商进货单：明细缺商品编码，按零售价 UUID 回查到 P-A
        _db.Insertable(new StoreRetailPrice { UUID = "RP-1002-A", StoreCode = "1002", ProductCode = "P-A" }).ExecuteCommand();
        _db.Insertable(new StoreLocalSupplierInvoice { InvoiceGUID = "INV-1", StoreCode = "1002", InvoiceNo = "INV-1", InboundDate = new DateTime(2025, 11, 14) }).ExecuteCommand();
        _db.Insertable(new StoreLocalSupplierInvoiceDetails { InvoiceGUID = "INV-1", StoreCode = "1002", ProductCode = "", StoreProductCode = "RP-1002-A", Quantity = 96 }).ExecuteCommand();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_path);
    }

    private sealed class ManualClock : TimeProvider
    {
        public ManualClock(DateTime utcNow) => UtcNow = utcNow;
        public DateTime UtcNow { get; set; }
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }

    private SupplierPreorderService CreateService(string userGuid, params string[] policies)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("userId", userGuid) }, "test"));
        var http = new Mock<IHttpContextAccessor>();
        http.SetupGet(x => x.HttpContext).Returns(new DefaultHttpContext { User = principal });
        var auth = new Mock<IAuthorizationService>();
        auth.Setup(x => x.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync((ClaimsPrincipal _, object? _, string policy) =>
                policies.Contains(policy) ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var user = new Mock<ICurrentUserService>();
        user.Setup(x => x.GetCurrentUserGuid()).Returns(userGuid);
        user.Setup(x => x.GetCurrentUsername()).Returns(userGuid + "-name");
        return new SupplierPreorderService(_db, user.Object, auth.Object, http.Object, new Mock<IRoleService>().Object,
            NullLogger<SupplierPreorderService>.Instance, _clock);
    }

    private SupplierPreorderHistoryService History() => new(_db, NullLogger.Instance, _clock);

    private async Task<(string FormGuid, Dictionary<string, string> ItemGuids)> CreateOpenFormAsync(SupplierPreorderService admin)
    {
        var detail = await admin.CreateFormAsync(new SupplierPreorderCreateFormDto
        {
            LocalSupplierCode = "257",
            HolidayType = "Christmas",
            SeasonYear = 2026,
            Title = "KFC Christmas 2026",
            DeadlineAtUtc = new DateTime(2026, 3, 6, 12, 59, 59, DateTimeKind.Utc),
            ExportQtyUnit = "Cartons",
            HeaderRowNo = 1,
            Mapping = new SupplierPreorderColumnMappingDto { ItemCode = 0, Name = 1, CartonQty = 2, Price = 3, Barcode = 4 },
            Rows = new List<SupplierPreorderParsedRowDto>
            {
                new() { RowNo = 2, Cells = new List<string?> { "XD0001815", "Flocked Rib Glitter Bauble 10cm Asst", "24", "1.10", "9333527865090" } },
                new() { RowNo = 3, Cells = new List<string?> { "XD0001817", "Lantern Flocked Bauble 12cm Asst", "24", "1.53", null } },
                new() { RowNo = 4, Cells = new List<string?> { "XD0009998", "Musical Santa Sleigh Ride", "2", "43.07", null } },
            },
        });
        var formGuid = detail.Summary.FormGuid;
        // 建表即排队（草稿阶段先算一次）
        Assert.Equal("Queued", (await History().ReadMetaAsync(formGuid))!.Status);
        await admin.UpdateFormStoresAsync(formGuid, new SupplierPreorderUpdateFormStoresDto { StoreGuids = new() { "S1", "S2" } });
        await admin.SetStatusAsync(formGuid, new SupplierPreorderSetStatusDto { Status = "Open" });
        Assert.Equal(1, await History().RunDueAsync(CancellationToken.None));

        var items = await _db.Queryable<SupplierPreorderFormItem>().Where(i => i.FormGuid == formGuid).ToListAsync();
        return (formGuid, items.ToDictionary(i => i.SupplierItemCode, i => i.ItemGuid));
    }

    [Fact]
    public async Task 计算快照_系统推荐参考_下单列表只给本店_不能看其他店时展开行不返回其他店()
    {
        var admin = CreateService("admin-1", Permissions.SupplierPreorder.Manage, Permissions.SupplierPreorder.Order);
        var (formGuid, itemGuids) = await CreateOpenFormAsync(admin);

        var meta = await History().ReadMetaAsync(formGuid);
        Assert.Equal("Ready", meta!.Status);
        Assert.Equal(new DateTime(2025, 9, 1), meta.WindowFrom);
        Assert.Equal(new DateTime(2026, 1, 18), meta.WindowTo);

        // 系统推荐：新品 Lantern → 同供应商、去年有销量的 P-REF；Musical Santa 找不到像的，不带
        var lantern = await _db.Queryable<SupplierPreorderFormItem>().FirstAsync(i => i.ItemGuid == itemGuids["XD0001817"]);
        Assert.Equal(("P-REF", SupplierPreorderRefSources.Auto), (lantern.RefProductCode, lantern.RefSource));
        Assert.True(lantern.RefScore >= 0.6m);
        var santa = await _db.Queryable<SupplierPreorderFormItem>().FirstAsync(i => i.ItemGuid == itemGuids["XD0009998"]);
        Assert.Null(santa.RefProductCode);

        // 只能替 Orion 下单、不能看其他分店的店长
        await admin.SaveOrdererAsync("user-1", new SupplierPreorderSaveOrdererDto { StoreGuids = new() { "S1" }, CanViewOtherStores = false });
        var orderer = CreateService("user-1", Permissions.SupplierPreorder.Order);
        var context = await orderer.GetOrderingContextAsync(formGuid);
        Assert.Equal("Ready", context.History.Status);
        Assert.Contains(context.History.KeyDates, k => k.Key == "BlackFriday" && k.Date == new DateTime(2025, 11, 28));

        var page = await orderer.GetOrderingItemsAsync(formGuid, new SupplierPreorderOrderingItemQueryDto { StoreGuid = "S1", Sort = "lastYearSalesDesc" });
        Assert.Equal(new[] { "XD0001815", "XD0001817", "XD0009998" }, page.Items!.Select(i => i.SupplierItemCode));
        var bauble = page.Items![0].History!;
        Assert.Equal("Own", bauble.Source);
        Assert.Equal(20m, bauble.SalesQty);           // 只有 Orion：5 + 15，Kawana 的 40 不能混进来，窗口外的 100 不算
        Assert.Equal(240m, bauble.PurchaseQty);       // 草稿配货 999 不算
        Assert.Equal(1, bauble.PurchaseBatches);
        Assert.Equal(new DateTime(2025, 9, 21), bauble.PurchaseFirst);
        Assert.Equal(0.0833m, bauble.SellThrough);
        Assert.Equal("unknown", bauble.DiscountStatus); // 测试库没有折扣日快照：如实标未判定
        var lanternHistory = page.Items[1].History!;
        Assert.Equal(("Ref", "P-REF", 8m), (lanternHistory.Source, lanternHistory.ProductCode, lanternHistory.SalesQty));
        Assert.Equal("XD0001356", lanternHistory.Ref!.ItemNumber);
        Assert.Null(page.Items[2].History);

        var sold = await orderer.GetOrderingItemsAsync(formGuid, new SupplierPreorderOrderingItemQueryDto { StoreGuid = "S1", Filter = "lastYearSold" });
        Assert.Equal(new[] { "XD0001815" }, sold.Items!.Select(i => i.SupplierItemCode)); // 参考商品的销量不算「去年有售」

        var detail = await orderer.GetOrderingItemHistoryAsync(formGuid, itemGuids["XD0001815"], "S1");
        Assert.Equal("Own", detail.Source);
        Assert.Null(detail.Stores);
        Assert.Equal("1001", detail.Store!.StoreCode);
        Assert.Equal(20m, detail.Store.SalesQty);
        Assert.Equal(new[] { "warehouse" }, detail.Store.Batches.Select(b => b.Source));
        Assert.Equal(1, detail.SuggestedCartons);    // 20 ÷ 24 四舍五入

        // 不能拿别的店的 storeGuid 去读
        await Assert.ThrowsAsync<SupplierPreorderException>(() => orderer.GetOrderingItemHistoryAsync(formGuid, itemGuids["XD0001815"], "S2"));

        // 能看其他分店的区域经理：对比表有 Kawana（供应商进货单按零售价 UUID 回查到 96 件），也带今年已订箱数
        await admin.SaveOrdererAsync("user-2", new SupplierPreorderSaveOrdererDto { StoreGuids = new() { "S2" }, CanViewOtherStores = true });
        await orderer.SaveLineAsync(formGuid, "S1", itemGuids["XD0001815"], new SupplierPreorderSaveLineDto { Cartons = 3 });
        var viewer = CreateService("user-2", Permissions.SupplierPreorder.Order);
        var viewerDetail = await viewer.GetOrderingItemHistoryAsync(formGuid, itemGuids["XD0001815"], "S2");
        Assert.NotNull(viewerDetail.Stores);
        var kawana = viewerDetail.Stores!.Single(s => s.StoreCode == "1002");
        Assert.True(kawana.IsCurrent);
        Assert.Equal((40m, 96m), (kawana.SalesQty, kawana.PurchaseQty));
        Assert.Empty(kawana.Daily);                   // 对比表只给汇总数字
        var orion = viewerDetail.Stores.Single(s => s.StoreCode == "1001");
        Assert.Equal(3, orion.ThisYearCartons);
    }

    [Fact]
    public async Task 人工指定与清除不被系统覆盖_交回系统后重新推荐_管理端计数与筛选()
    {
        var admin = CreateService("admin-1", Permissions.SupplierPreorder.Manage, Permissions.SupplierPreorder.Order);
        var (formGuid, itemGuids) = await CreateOpenFormAsync(admin);

        var candidates = await admin.GetRefCandidatesAsync(formGuid, itemGuids["XD0001817"], null);
        Assert.Equal("P-REF", candidates.Recommended.First().ProductCode);
        Assert.DoesNotContain(candidates.Recommended, c => c.ProductCode == "P-A"); // 本表已匹配的商品不作候选
        var search = await admin.GetRefCandidatesAsync(formGuid, itemGuids["XD0009998"], "Earrings");
        Assert.Equal("P-EAR", search.SearchResults.Single().ProductCode);
        Assert.False(search.SearchResults[0].Eligible);

        // Santa 人工指定 P-EAR（即使分数不够也允许，采购说了算）；Lantern 人工清除
        var santa = await admin.SetItemRefAsync(formGuid, itemGuids["XD0009998"], new SupplierPreorderSetRefDto { Mode = "Manual", ProductCode = "P-EAR" });
        Assert.Equal(("Manual", "P-EAR", "admin-1-name"), (santa.Ref!.Source, santa.Ref.ProductCode, santa.Ref.SetBy));
        Assert.Equal(2m, santa.Ref.SalesQty);         // 指定后立刻补算了这个商品的快照
        var lantern = await admin.SetItemRefAsync(formGuid, itemGuids["XD0001817"], new SupplierPreorderSetRefDto { Mode = "Clear" });
        Assert.Equal("Cleared", lantern.Ref!.Source);
        Assert.Null(lantern.Ref.ProductCode);
        await Assert.ThrowsAsync<SupplierPreorderException>(() =>
            admin.SetItemRefAsync(formGuid, itemGuids["XD0001815"], new SupplierPreorderSetRefDto { Mode = "Manual", ProductCode = "P-A" }));

        await admin.RecomputeHistoryAsync(formGuid);
        Assert.Equal(1, await History().RunDueAsync(CancellationToken.None));
        var items = (await _db.Queryable<SupplierPreorderFormItem>().Where(i => i.FormGuid == formGuid).ToListAsync()).ToDictionary(i => i.SupplierItemCode);
        Assert.Equal(("P-EAR", "Manual"), (items["XD0009998"].RefProductCode, items["XD0009998"].RefSource));
        Assert.Equal((null, "Cleared"), (items["XD0001817"].RefProductCode, items["XD0001817"].RefSource));

        var detail = await admin.GetFormAsync(formGuid);
        Assert.Equal("Ready", detail.History.Status);
        Assert.Equal((1, 0, 1, 1), (detail.OwnHistoryCount, detail.RefAutoCount, detail.RefManualCount, detail.NeedRefCount));
        var needRef = await admin.GetFormItemsAsync(formGuid, new SupplierPreorderFormItemQueryDto { Match = "needRef" });
        Assert.Equal(new[] { "XD0001817" }, needRef.Items!.Select(i => i.SupplierItemCode));
        var own = (await admin.GetFormItemsAsync(formGuid, new SupplierPreorderFormItemQueryDto())).Items!.Single(i => i.SupplierItemCode == "XD0001815");
        Assert.Equal(60m, own.OwnSalesQty);           // 管理端看全部分店合计：20 + 40

        // 交回系统：下次计算重新推荐
        await admin.SetItemRefAsync(formGuid, itemGuids["XD0001817"], new SupplierPreorderSetRefDto { Mode = "Auto" });
        Assert.Equal(1, await History().RunDueAsync(CancellationToken.None));
        var again = await _db.Queryable<SupplierPreorderFormItem>().FirstAsync(i => i.ItemGuid == itemGuids["XD0001817"]);
        Assert.Equal(("P-REF", SupplierPreorderRefSources.Auto), (again.RefProductCode, again.RefSource));
        // 同一天内已算过、没有排队：不再重复计算
        Assert.Equal(0, await History().RunDueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 导入时人工指定的参考按行号写入_不要参考记为清除()
    {
        var admin = CreateService("admin-1", Permissions.SupplierPreorder.Manage);
        var detail = await admin.CreateFormAsync(new SupplierPreorderCreateFormDto
        {
            LocalSupplierCode = "257",
            HolidayType = "Christmas",
            SeasonYear = 2026,
            Title = "KFC Christmas 2026",
            HeaderRowNo = 1,
            Mapping = new SupplierPreorderColumnMappingDto { ItemCode = 0, Name = 1, CartonQty = 2, Price = 3 },
            Rows = new List<SupplierPreorderParsedRowDto>
            {
                new() { RowNo = 7, Cells = new List<string?> { "XD0001817", "Lantern Flocked Bauble 12cm Asst", "24", "1.53" } },
                new() { RowNo = 8, Cells = new List<string?> { "XD0009998", "Musical Santa Sleigh Ride", "2", "43.07" } },
            },
            RefOverrides = new() { new() { RowNo = 7, ProductCode = null }, new() { RowNo = 8, ProductCode = "P-REF" } },
        });
        var items = (await _db.Queryable<SupplierPreorderFormItem>().Where(i => i.FormGuid == detail.Summary.FormGuid).ToListAsync()).ToDictionary(i => i.SupplierItemCode);
        Assert.Equal((null, "Cleared"), (items["XD0001817"].RefProductCode, items["XD0001817"].RefSource));
        Assert.Equal(("P-REF", "Manual"), (items["XD0009998"].RefProductCode, items["XD0009998"].RefSource));

        await Assert.ThrowsAsync<SupplierPreorderException>(() => admin.CreateFormAsync(new SupplierPreorderCreateFormDto
        {
            LocalSupplierCode = "257",
            HolidayType = "Christmas",
            SeasonYear = 2026,
            Title = "bad",
            HeaderRowNo = 1,
            Mapping = new SupplierPreorderColumnMappingDto { ItemCode = 0, Name = 1, CartonQty = 2, Price = 3 },
            Rows = new List<SupplierPreorderParsedRowDto> { new() { RowNo = 2, Cells = new List<string?> { "X1", "Thing", "1", "1" } } },
            RefOverrides = new() { new() { RowNo = 2, ProductCode = "NOPE" } },
        }));
    }
}
