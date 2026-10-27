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
/// 供应商节日预订单服务（SQLite 真实落库）：导入建表、两层授权相交、按箱保存与汇总、关掉分店后不计入、
/// 提交后再改回到草稿、截止后普通下单人只读而管理员仍可改。
/// </summary>
public sealed class SupplierPreorderServiceSqliteTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"supplier-preorder-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly ManualClock _clock = new(new DateTime(2026, 2, 20, 0, 0, 0, DateTimeKind.Utc));

    public SupplierPreorderServiceSqliteTests()
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
            typeof(SupplierPreorderForm), typeof(SupplierPreorderFormItem), typeof(SupplierPreorderFormStore),
            typeof(SupplierPreorderOrderer), typeof(SupplierPreorderOrdererStore), typeof(SupplierPreorderAuthLog),
            typeof(SupplierPreorderStoreOrder), typeof(SupplierPreorderStoreOrderLine),
            typeof(SupplierPreorderImportProfile), typeof(SupplierPreorderSourceFile));

        _db.Insertable(new[]
        {
            new Store { StoreGUID = "S1", StoreCode = "1001", StoreName = "Orion", IsActive = true },
            new Store { StoreGUID = "S2", StoreCode = "1002", StoreName = "Kawana", IsActive = true },
            new Store { StoreGUID = "S3", StoreCode = "1003", StoreName = "Peninsula", IsActive = true },
        }).ExecuteCommand();
        _db.Insertable(new[]
        {
            new User { UserGUID = "user-1", Username = "storemanager", Email = "u1@example.test", PasswordHash = "x", FullName = "王店长", IsActive = true },
            new User { UserGUID = "user-2", Username = "areamanager", Email = "u2@example.test", PasswordHash = "x", FullName = "赵区经理", IsActive = true },
        }).ExecuteCommand();
        _db.Insertable(new HBLocalSupplier { Guid = "sup-257", LocalSupplierCode = "257", Name = "Uncle Bills (KFC)", Status = 1 }).ExecuteCommand();
        _db.Insertable(new Product { ProductCode = "P-A", ItemNumber = "XD0001815", Barcode = "9333527865090", ProductName = "Bauble", LocalSupplierCode = "257", PurchasePrice = 1.02m, IsActive = true }).ExecuteCommand();
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

    private async Task<string> CreateOpenFormAsync(SupplierPreorderService admin)
    {
        var detail = await admin.CreateFormAsync(new SupplierPreorderCreateFormDto
        {
            LocalSupplierCode = "257",
            HolidayType = "Christmas",
            SeasonYear = 2026,
            Title = "KFC Christmas 2026",
            DeadlineAtUtc = new DateTime(2026, 3, 6, 12, 59, 59, DateTimeKind.Utc),
            ContainerCapacityCbm = 67,
            ExportQtyUnit = "Cartons",
            HeaderRowNo = 1,
            HeaderSignature = "sig-kfc",
            Mapping = new SupplierPreorderColumnMappingDto { ItemCode = 0, Name = 1, CartonQty = 2, Price = 3, Barcode = 4, CartonCbm = 5 },
            Rows = new List<SupplierPreorderParsedRowDto>
            {
                new() { RowNo = 2, Cells = new List<string?> { "XD0001815", "Flocked Rib Glitter Bauble", "24", "1.10", "9333527865090", "0.05" } },
                new() { RowNo = 3, Cells = new List<string?> { "XD0001817", "Lantern Flocked Bauble", "12", "2", null, null } },
                new() { RowNo = 4, Cells = new List<string?> { "XD0009999", "Bad price", "12", "abc", null, null } },
            },
        });
        Assert.Equal(2, detail.Summary.ItemCount);          // 进价无效的一行被跳过
        Assert.Equal("Draft", detail.Summary.Status);
        Assert.Equal(1, detail.MatchedCount);                // 条码对上 P-A
        Assert.Equal(1, detail.NewCount);
        var formGuid = detail.Summary.FormGuid;

        await admin.UpdateFormStoresAsync(formGuid, new SupplierPreorderUpdateFormStoresDto { StoreGuids = new() { "S1", "S2" } });
        await admin.SetStatusAsync(formGuid, new SupplierPreorderSetStatusDto { Status = "Open" });
        // 列映射已按供应商 + 表头签名记住
        Assert.True(await _db.Queryable<SupplierPreorderImportProfile>().AnyAsync(p => p.LocalSupplierCode == "257" && p.HeaderSignature == "sig-kfc"));
        return formGuid;
    }

    [Fact]
    public async Task 两层授权相交_按箱保存汇总_提交后再改回草稿_截止后只读管理员仍可改_关店不计入()
    {
        var admin = CreateService("admin-1", Permissions.SupplierPreorder.Manage, Permissions.SupplierPreorder.Order);
        var formGuid = await CreateOpenFormAsync(admin);

        // 下单人员授权：user-1 可替 S1、S3 下单（S3 没在这张订单表开启），不能看其他分店
        await admin.SaveOrdererAsync("user-1", new SupplierPreorderSaveOrdererDto { StoreGuids = new() { "S1", "S3" }, CanViewOtherStores = false });
        var logs = await admin.GetAuthLogsAsync(new SupplierPreorderAuthLogQueryDto { UserGuid = "user-1" });
        Assert.Contains(logs.Items!, l => l.Action == "OrdererAdded" && l.Detail!.Contains("Orion"));

        var orderer = CreateService("user-1", Permissions.SupplierPreorder.Order);
        var context = await orderer.GetOrderingContextAsync(formGuid);
        Assert.False(context.CanViewOtherStores);
        Assert.Equal(new[] { "S1", "S3" }, context.Form.MyStores.Select(s => s.StoreGuid));
        Assert.True(context.Form.MyStores[0].CanEdit);
        Assert.False(context.Form.MyStores[1].CanEdit);
        Assert.Contains("没有开启本店", context.Form.MyStores[1].ReadOnlyReason);

        var items = await orderer.GetOrderingItemsAsync(formGuid, new SupplierPreorderOrderingItemQueryDto { StoreGuid = "S1", PageSize = 50 });
        var itemA = items.Items!.Single(i => i.SupplierItemCode == "XD0001815");
        var itemB = items.Items!.Single(i => i.SupplierItemCode == "XD0001817");
        Assert.Null(itemA.OtherStores);
        Assert.True(itemB.IsNew);

        var saved = await orderer.SaveLineAsync(formGuid, "S1", itemA.ItemGuid, new SupplierPreorderSaveLineDto { Cartons = 3 });
        Assert.Equal(3, saved.Summary.Cartons);
        Assert.Equal(72, saved.Summary.Pieces);
        Assert.Equal(79.20m, saved.Summary.Amount);
        Assert.Equal(0.15m, saved.Summary.Cbm);
        Assert.Null(saved.Summary.StoreCbms);

        // 没授权的 S2、没开启的 S3 都不能写
        var forbidden = await Assert.ThrowsAsync<SupplierPreorderException>(() =>
            orderer.SaveLineAsync(formGuid, "S2", itemA.ItemGuid, new SupplierPreorderSaveLineDto { Cartons = 1 }));
        Assert.Equal(403, forbidden.StatusCode);
        var readOnly = await Assert.ThrowsAsync<SupplierPreorderException>(() =>
            orderer.SaveLineAsync(formGuid, "S3", itemA.ItemGuid, new SupplierPreorderSaveLineDto { Cartons = 1 }));
        Assert.Equal("STORE_READ_ONLY", readOnly.ErrorCode);

        // 提交后再改：回到草稿，保留上次提交时间
        var submitted = await orderer.SubmitStoreOrderAsync(formGuid, "S1");
        Assert.Equal("Submitted", submitted.OrderStatus);
        var afterEdit = await orderer.SaveLineAsync(formGuid, "S1", itemB.ItemGuid, new SupplierPreorderSaveLineDto { Cartons = 2 });
        Assert.Equal("Draft", afterEdit.Summary.OrderStatus);
        Assert.NotNull(afterEdit.Summary.SubmittedAtUtc);
        Assert.Equal(5, afterEdit.Summary.Cartons);

        // 管理员替 S2 下单，矩阵合计含 S1、S2
        await admin.SaveLineAsync(formGuid, "S2", itemA.ItemGuid, new SupplierPreorderSaveLineDto { Cartons = 4 });
        var matrix = await admin.GetMatrixAsync(formGuid, new SupplierPreorderMatrixQueryDto { PageSize = 50 });
        Assert.Equal(9, matrix.Totals.TotalCartons);
        Assert.Equal(4, matrix.Totals.StoreCartons["S2"]);
        var stores = await admin.GetFormStoresAsync(formGuid);
        Assert.Equal("Draft", stores.Single(s => s.StoreGuid == "S1").OrderStatus);
        Assert.Equal(new[] { "王店长" }, stores.Single(s => s.StoreGuid == "S1").Orderers);

        // 关掉 S2：数据保留但不计入合计
        await admin.UpdateFormStoresAsync(formGuid, new SupplierPreorderUpdateFormStoresDto { StoreGuids = new() { "S1" } });
        matrix = await admin.GetMatrixAsync(formGuid, new SupplierPreorderMatrixQueryDto { PageSize = 50 });
        Assert.Equal(5, matrix.Totals.TotalCartons);
        Assert.False(matrix.Totals.StoreCartons.ContainsKey("S2"));
        Assert.Equal(1, await _db.Queryable<SupplierPreorderStoreOrderLine>().CountAsync(l => l.Cartons == 4));
        var forms = await admin.GetFormsAsync(new SupplierPreorderFormQueryDto { PageSize = 50 });
        Assert.Equal(5, forms.Items!.Single().TotalCartons);

        // 已有下单不能删除
        await Assert.ThrowsAsync<SupplierPreorderException>(() => admin.DeleteFormAsync(formGuid));

        // 过截止时间：普通下单人只读，管理员仍可改
        _clock.UtcNow = new DateTime(2026, 3, 7, 0, 0, 0, DateTimeKind.Utc);
        var late = await Assert.ThrowsAsync<SupplierPreorderException>(() =>
            orderer.SaveLineAsync(formGuid, "S1", itemA.ItemGuid, new SupplierPreorderSaveLineDto { Cartons = 6 }));
        Assert.Contains("截止", late.Message);
        var managerEdit = await admin.SaveLineAsync(formGuid, "S1", itemA.ItemGuid, new SupplierPreorderSaveLineDto { Cartons = 0 });
        Assert.Equal(2, managerEdit.Summary.Cartons);
        Assert.Equal(1, managerEdit.Summary.ItemCount);

        // 普通下单人只能看到开启了自己分店的订单表；没有入口权限直接 403
        var visible = await orderer.GetOrderingFormsAsync();
        Assert.Single(visible);
        Assert.True(visible[0].IsLocked);
        var noEntry = CreateService("user-1");
        var denied = await Assert.ThrowsAsync<SupplierPreorderException>(() => noEntry.GetOrderingFormsAsync());
        Assert.Equal(403, denied.StatusCode);
    }

    [Fact]
    public async Task 可查看其他分店的人看到其他店箱数与各店体积()
    {
        var admin = CreateService("admin-1", Permissions.SupplierPreorder.Manage);
        var formGuid = await CreateOpenFormAsync(admin);
        await admin.SaveOrdererAsync("user-2", new SupplierPreorderSaveOrdererDto { StoreGuids = new() { "S1" }, CanViewOtherStores = true });
        var itemA = (await _db.Queryable<SupplierPreorderFormItem>().FirstAsync(i => i.SupplierItemCode == "XD0001815")).ItemGuid;
        await admin.SaveLineAsync(formGuid, "S2", itemA, new SupplierPreorderSaveLineDto { Cartons = 2 });

        var viewer = CreateService("user-2", Permissions.SupplierPreorder.Order);
        var items = await viewer.GetOrderingItemsAsync(formGuid, new SupplierPreorderOrderingItemQueryDto { StoreGuid = "S1", Filter = "others" });
        var row = Assert.Single(items.Items!);
        Assert.Equal("Kawana", Assert.Single(row.OtherStores!).StoreName);
        var summary = await viewer.GetOrderingSummaryAsync(formGuid, "S1");
        Assert.Equal(0.1m, summary.AllStoresCbm);
        Assert.Equal("Kawana", Assert.Single(summary.StoreCbms!).StoreName);

        // 原授权里的分店后来停用：再次编辑时保留它，不报错；新加停用分店才拒绝
        await _db.Updateable<Store>().SetColumns(x => x.IsActive == false).Where(x => x.StoreGUID == "S3").ExecuteCommandAsync();
        await admin.SaveOrdererAsync("user-1", new SupplierPreorderSaveOrdererDto { StoreGuids = new() { "S1" }, CanViewOtherStores = false });
        await _db.Insertable(new SupplierPreorderOrdererStore { UserGuid = "user-1", StoreGuid = "S3", StoreCode = "1003", StoreName = "Peninsula" }).ExecuteCommandAsync();
        var kept = await admin.SaveOrdererAsync("user-1", new SupplierPreorderSaveOrdererDto { StoreGuids = new() { "S1", "S3" }, CanViewOtherStores = true });
        Assert.Equal(new[] { "S1", "S3" }, kept.Stores.Select(x => x.StoreGuid));
        await Assert.ThrowsAsync<SupplierPreorderException>(() =>
            admin.SaveOrdererAsync("user-2", new SupplierPreorderSaveOrdererDto { StoreGuids = new() { "S1", "S3" } }));

        var copy = await admin.CopyStoresFromPreviousAsync(formGuid);
        Assert.Null(copy.SourceTitle);
    }

    [Fact]
    public async Task 订单表没开启本人分店时直接访问返回403_开启分店停用后整体覆盖仍可保存()
    {
        var admin = CreateService("admin-1", Permissions.SupplierPreorder.Manage);
        var formGuid = await CreateOpenFormAsync(admin); // 开启 S1、S2
        await admin.SaveOrdererAsync("user-1", new SupplierPreorderSaveOrdererDto { StoreGuids = new() { "S3" } });
        var outsider = CreateService("user-1", Permissions.SupplierPreorder.Order);

        Assert.Empty(await outsider.GetOrderingFormsAsync());
        var denied = await Assert.ThrowsAsync<SupplierPreorderException>(() => outsider.GetOrderingContextAsync(formGuid));
        Assert.Equal(403, denied.StatusCode);
        await Assert.ThrowsAsync<SupplierPreorderException>(() =>
            outsider.GetOrderingItemsAsync(formGuid, new SupplierPreorderOrderingItemQueryDto { StoreGuid = "S3" }));

        // S2 开启后被停用：再开启 S3 时前端会把 S2 一起带回来，不应报错
        await _db.Updateable<Store>().SetColumns(x => x.IsActive == false).Where(x => x.StoreGUID == "S2").ExecuteCommandAsync();
        var stores = await admin.UpdateFormStoresAsync(formGuid, new SupplierPreorderUpdateFormStoresDto { StoreGuids = new() { "S1", "S2", "S3" } });
        Assert.True(stores.Single(x => x.StoreGuid == "S2").Enabled);
        Assert.True(stores.Single(x => x.StoreGuid == "S3").Enabled);
        Assert.NotNull(await outsider.GetOrderingContextAsync(formGuid));
    }
}
