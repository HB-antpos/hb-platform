using System.Security.Claims;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.StoreSpecialProductCategories;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 分店级特殊商品分类服务（SQLite 真实落库）：分类增删改与重名 / 上限 / 校验、排序排列校验与冲突、
/// 归类（标记 + 归类 / 移出分类 / 没有价格行跳过）、取消标记不删归属、列表只含仍是特殊商品的、候选搜索、
/// 分类内排序、同步 preview / execute（merge / replace、confirmReplace 门禁、单店失败隔离、分类数上限）、
/// 分店范围校验（非主分店被拒）与控制器的设备会话拒绝 / 错误码映射。
/// </summary>
public sealed class StoreSpecialProductCategoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"store-special-category-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;

    public StoreSpecialProductCategoryTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_path}",
            DbType = DbType.Sqlite,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = false,
        });
        _db.CodeFirst.InitTables(
            typeof(Store), typeof(Product), typeof(StoreRetailPrice),
            typeof(StoreSpecialProductCategory), typeof(StoreSpecialProductCategoryItem));
        // 与生产迁移一致的唯一索引（同店名称唯一，不区分大小写），验证并发重名的兜底。
        _db.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX UX_StoreSpecialProductCategory_Store_Name ON StoreSpecialProductCategory (StoreCode, Name COLLATE NOCASE)");

        _db.Insertable(new[]
        {
            new Store { StoreGUID = "S1", StoreCode = "1001", StoreName = "Orion", IsActive = true },
            new Store { StoreGUID = "S2", StoreCode = "1002", StoreName = "Kawana", IsActive = true },
            new Store { StoreGUID = "S3", StoreCode = "1003", StoreName = "Peninsula", IsActive = true },
            new Store { StoreGUID = "S4", StoreCode = "1004", StoreName = "Closed", IsActive = false },
        }).ExecuteCommand();
        _db.Insertable(new[]
        {
            Product("P1", "Bauble Red", "XD001", "9300000000011"),
            Product("P2", "Bauble Blue", "XD002", "9300000000028"),
            Product("P3", "Lantern", "XD003", "9300000000035"),
            Product("P4", "Candle", "XD004", "9300000000042"),
            Product("P5", "Ribbon", "XD005", "9300000000059"),
            Product("P6", "No Price Item", "XD006", "9300000000066"),
        }).ExecuteCommand();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_path);
    }

    // ------------------------------------------------------------------ 造数与替身

    private static Product Product(string code, string name, string itemNumber, string barcode) => new()
    {
        ProductCode = code,
        ProductName = name,
        ItemNumber = itemNumber,
        Barcode = barcode,
        RetailPrice = 9.9m,
        IsActive = true,
    };

    private void AddPrice(string store, string product, bool special, bool active = true, decimal price = 5m)
    {
        _db.Insertable(new StoreRetailPrice
        {
            StoreCode = store,
            ProductCode = product,
            StoreRetailPriceValue = price,
            IsSpecialProduct = special,
            IsActive = active,
        }).ExecuteCommand();
    }

    private async Task<StoreSpecialCategoryDto> CreateCategoryAsync(
        StoreSpecialProductCategoryService service, string store, string name, string? color = null)
    {
        var response = await service.CreateAsync(new StoreSpecialCategorySaveRequestDto { StoreCode = store, Name = name, Color = color });
        Assert.True(response.Success, response.Message);
        return response.Data!;
    }

    private async Task AssignAsync(
        StoreSpecialProductCategoryService service, string store, string? categoryId, bool mark, params string[] codes)
    {
        var response = await service.AssignAsync(new StoreSpecialCategoryAssignRequestDto
        {
            StoreCode = store,
            ProductCodes = codes.ToList(),
            CategoryId = categoryId,
            MarkAsSpecial = mark,
        });
        Assert.True(response.Success, response.Message);
    }

    private static CurrentUserManageableStoreScope Scope(bool admin, params string[] storeCodes) => new()
    {
        IsAllowed = true,
        IsAuthenticated = true,
        IsAdmin = admin,
        IsStoreManager = !admin,
        ActorLabel = "tester",
        StoreCodes = storeCodes,
    };

    private StoreSpecialProductCategoryService CreateService(CurrentUserManageableStoreScope scope)
    {
        var scopeService = new Mock<ICurrentUserManageableStoreScopeService>();
        scopeService.Setup(x => x.GetScopeAsync()).ReturnsAsync(scope);
        return new StoreSpecialProductCategoryService(
            _db, scopeService.Object, NullLogger<StoreSpecialProductCategoryService>.Instance);
    }

    /// <summary>管理员：不限分店。</summary>
    private StoreSpecialProductCategoryService AdminService() => CreateService(Scope(admin: true));

    private bool IsSpecial(string store, string product) =>
        _db.Queryable<StoreRetailPrice>().Where(r => r.StoreCode == store && r.ProductCode == product).First().IsSpecialProduct;

    private List<StoreSpecialProductCategoryItem> Items(string store) =>
        _db.Queryable<StoreSpecialProductCategoryItem>().Where(i => i.StoreCode == store).ToList();

    private static List<string> Names(IEnumerable<StoreSpecialCategoryDto> categories) =>
        categories.OrderBy(c => c.SortOrder).Select(c => c.Name).ToList();

    // ------------------------------------------------------------------ 分类维护

    [Fact]
    public async Task 分类_新建改名删除_名称规整_颜色规整_同店不区分大小写不重名_换店可重名()
    {
        var service = AdminService();

        var toys = await CreateCategoryAsync(service, "1001", "  Toys  ", "#e65a2f");
        Assert.Equal("Toys", toys.Name);              // 名称 trim
        Assert.Equal("#E65A2F", toys.Color);          // 颜色统一大写
        Assert.Equal(0, toys.SortOrder);
        var gifts = await CreateCategoryAsync(service, "1001", "Gifts");
        Assert.Null(gifts.Color);
        Assert.Equal(1, gifts.SortOrder);             // 追加到末尾

        var duplicate = await service.CreateAsync(new StoreSpecialCategorySaveRequestDto { StoreCode = "1001", Name = "TOYS" });
        Assert.False(duplicate.Success);
        Assert.Equal(StoreSpecialCategoryErrorCodes.NameDuplicate, duplicate.ErrorCode);

        // 换一家店可以重名。
        AddPrice("1002", "P1", special: false);
        await CreateCategoryAsync(service, "1002", "Toys");

        foreach (var (name, color) in new[] { ("", (string?)null), ("   ", null), (new string('x', 31), null), ("Ok", "red"), ("Ok", "#12345") })
        {
            var invalid = await service.CreateAsync(new StoreSpecialCategorySaveRequestDto { StoreCode = "1001", Name = name, Color = color });
            Assert.False(invalid.Success);
            Assert.Equal(StoreSpecialCategoryErrorCodes.ValidationError, invalid.ErrorCode);
        }

        // 恰好 30 个字符可以。
        Assert.True((await service.CreateAsync(new StoreSpecialCategorySaveRequestDto { StoreCode = "1001", Name = new string('y', 30) })).Success);

        // 改名：可改成自己名称的不同大小写；改成别的分类的名字被拒；颜色可清空。
        var renamed = await service.UpdateAsync(toys.Id, new StoreSpecialCategorySaveRequestDto { StoreCode = "1001", Name = "TOYS", Color = null });
        Assert.True(renamed.Success, renamed.Message);
        Assert.Equal("TOYS", renamed.Data!.Name);
        Assert.Null(renamed.Data.Color);
        var clash = await service.UpdateAsync(toys.Id, new StoreSpecialCategorySaveRequestDto { StoreCode = "1001", Name = "gifts" });
        Assert.Equal(StoreSpecialCategoryErrorCodes.NameDuplicate, clash.ErrorCode);

        // 改别的店的分类 id → 不存在（分类按 id + 分店双重限定）。
        var wrongStore = await service.UpdateAsync(toys.Id, new StoreSpecialCategorySaveRequestDto { StoreCode = "1002", Name = "Hijack" });
        Assert.Equal(StoreSpecialCategoryErrorCodes.CategoryNotFound, wrongStore.ErrorCode);
        var missing = await service.UpdateAsync("no-such", new StoreSpecialCategorySaveRequestDto { StoreCode = "1001", Name = "Z" });
        Assert.Equal(StoreSpecialCategoryErrorCodes.CategoryNotFound, missing.ErrorCode);

        // 审计列：创建者是当前账号。
        var row = _db.Queryable<StoreSpecialProductCategory>().First(c => c.Guid == toys.Id);
        Assert.Equal("tester", row.CreatedBy);
        Assert.False(row.IsDeleted);
    }

    [Fact]
    public async Task 分类_每店最多50个_超过给稳定错误_删掉一个又能建()
    {
        var service = AdminService();
        _db.Insertable(Enumerable.Range(0, 50).Select(i => new StoreSpecialProductCategory
        {
            StoreCode = "1001",
            Name = $"C{i:00}",
            SortOrder = i,
        }).ToList()).ExecuteCommand();

        var overflow = await service.CreateAsync(new StoreSpecialCategorySaveRequestDto { StoreCode = "1001", Name = "One more" });
        Assert.False(overflow.Success);
        Assert.Equal(StoreSpecialCategoryErrorCodes.LimitReached, overflow.ErrorCode);

        var first = _db.Queryable<StoreSpecialProductCategory>().First(c => c.Name == "C00");
        Assert.True((await service.DeleteAsync(first.Guid, "1001")).Success);
        var created = await service.CreateAsync(new StoreSpecialCategorySaveRequestDto { StoreCode = "1001", Name = "One more" });
        Assert.True(created.Success, created.Message);
        Assert.Equal(49, created.Data!.SortOrder);    // 删除后顺序压实成 0..48，新分类追加到 49
    }

    [Fact]
    public async Task 删除分类_归属行一并删除_特殊标记不变_商品回到未分类_剩余分类顺序压实()
    {
        var service = AdminService();
        AddPrice("1001", "P1", special: true);
        AddPrice("1001", "P2", special: true);
        AddPrice("1001", "P3", special: true);
        var a = await CreateCategoryAsync(service, "1001", "A");
        var b = await CreateCategoryAsync(service, "1001", "B");
        var c = await CreateCategoryAsync(service, "1001", "C");
        await AssignAsync(service, "1001", b.Id, false, "P1", "P2");
        await AssignAsync(service, "1001", c.Id, false, "P3");
        // 取消 P2 的标记但保留归属：删分类时它的归属行也要删，但对外只报告仍是特殊商品的数量。
        Assert.True((await service.UnmarkAsync(new StoreSpecialCategoryUnmarkRequestDto { StoreCode = "1001", ProductCodes = new() { "P2" } })).Success);

        var deleted = await service.DeleteAsync(b.Id, "1001");
        Assert.True(deleted.Success, deleted.Message);
        Assert.Equal(1, deleted.Data!.RemovedItemCount);
        Assert.Equal(new[] { "P3" }, Items("1001").Select(i => i.ProductCode).ToArray());
        Assert.True(IsSpecial("1001", "P1"));     // 特殊标记不变
        Assert.False(IsSpecial("1001", "P2"));

        var list = (await service.GetListAsync("1001")).Data!;
        Assert.Equal(new[] { "A", "C" }, Names(list.Categories));
        Assert.Equal(new[] { 0, 1 }, list.Categories.Select(x => x.SortOrder).ToArray());
        Assert.Equal(3 - 1, list.TotalCount);     // P1、P3 仍是特殊商品
        Assert.Equal(1, list.UncategorizedCount); // P1 回到未分类
        Assert.Equal(a.Id, list.Categories[0].Id);
    }

    [Fact]
    public async Task 分类排序_必须是全部分类的排列_缺少多出重复未知id都是冲突_成功后顺序落库()
    {
        var service = AdminService();
        var a = await CreateCategoryAsync(service, "1001", "A");
        var b = await CreateCategoryAsync(service, "1001", "B");
        var c = await CreateCategoryAsync(service, "1001", "C");
        var other = await CreateCategoryAsync(service, "1002", "Other");

        foreach (var bad in new List<List<string>>
                 {
                     new() { a.Id, b.Id },                    // 缺一个
                     new() { a.Id, b.Id, c.Id, other.Id },    // 多出别店的
                     new() { a.Id, a.Id, b.Id },              // 重复
                     new() { a.Id, b.Id, "ghost" },           // 未知 id
                     new(),                                   // 空
                 })
        {
            var conflict = await service.ReorderCategoriesAsync(new StoreSpecialCategoryOrderRequestDto { StoreCode = "1001", CategoryIds = bad });
            Assert.False(conflict.Success);
            Assert.Equal(StoreSpecialCategoryErrorCodes.OrderConflict, conflict.ErrorCode);
        }

        var ok = await service.ReorderCategoriesAsync(new StoreSpecialCategoryOrderRequestDto { StoreCode = "1001", CategoryIds = new() { c.Id, a.Id, b.Id } });
        Assert.True(ok.Success, ok.Message);
        Assert.Equal(new[] { "C", "A", "B" }, ok.Data!.Categories.Select(x => x.Name).ToArray());
        Assert.Equal(new[] { "C", "A", "B" }, Names((await service.GetListAsync("1001")).Data!.Categories));
        // 别店的分类顺序不受影响。
        Assert.Equal(0, _db.Queryable<StoreSpecialProductCategory>().First(x => x.Guid == other.Id).SortOrder);
    }

    // ------------------------------------------------------------------ 归类 / 取消标记 / 列表

    [Fact]
    public async Task 归类_标记并归类_没有价格行跳过_未标记要求已是特殊商品_移出分类_追加到末尾并保持传入顺序()
    {
        var service = AdminService();
        AddPrice("1001", "P1", special: false);
        AddPrice("1001", "P2", special: true);
        AddPrice("1001", "P3", special: false, active: false);   // 只有停用的价格行：不算有效
        AddPrice("1001", "P4", special: true);
        var toys = await CreateCategoryAsync(service, "1001", "Toys");

        var response = await service.AssignAsync(new StoreSpecialCategoryAssignRequestDto
        {
            StoreCode = "1001",
            CategoryId = toys.Id,
            MarkAsSpecial = true,
            ProductCodes = new() { "P2", "P1", "P6", "P3", " P1 " },   // P6 没价格行；P3 价格行停用；P1 重复
        });
        Assert.True(response.Success, response.Message);
        Assert.Equal(2, response.Data!.AssignedCount);
        Assert.Equal(1, response.Data.MarkedCount);                       // 只有 P1 是新标记的
        Assert.Equal(
            new[] { ("P6", "NO_STORE_PRICE"), ("P3", "NO_STORE_PRICE") },
            response.Data.Skipped.Select(s => (s.ProductCode, s.Reason)).ToArray());
        Assert.True(IsSpecial("1001", "P1"));
        Assert.False(IsSpecial("1001", "P3"));
        // 没有给没价格行的商品新建 StoreRetailPrice 行。
        Assert.Equal(0, _db.Queryable<StoreRetailPrice>().Count(r => r.StoreCode == "1001" && r.ProductCode == "P6"));
        // 保持传入顺序：P2 在前、P1 在后。
        var items = Items("1001").OrderBy(i => i.SortOrder).ToList();
        Assert.Equal(new[] { "P2", "P1" }, items.Select(i => i.ProductCode).ToArray());
        Assert.All(items, i => Assert.Equal(toys.Id, i.CategoryGuid));

        // 再追加：P4 排到末尾；已在该分类的 P2 原位不动。
        await AssignAsync(service, "1001", toys.Id, false, "P4", "P2");
        Assert.Equal(new[] { "P2", "P1", "P4" }, Items("1001").OrderBy(i => i.SortOrder).Select(i => i.ProductCode).ToArray());

        // markAsSpecial=false：商品必须已是特殊商品，否则跳过且不写归属。
        var notSpecial = await service.AssignAsync(new StoreSpecialCategoryAssignRequestDto
        {
            StoreCode = "1001", CategoryId = toys.Id, MarkAsSpecial = false, ProductCodes = new() { "P3" },
        });
        Assert.Equal(0, notSpecial.Data!.AssignedCount);
        Assert.Equal("NOT_SPECIAL_PRODUCT", Assert.Single(notSpecial.Data.Skipped).Reason);
        Assert.False(IsSpecial("1001", "P3"));

        // categoryId=null：移到未分类，删归属行，特殊标记不变。
        await AssignAsync(service, "1001", null, false, "P1");
        Assert.DoesNotContain(Items("1001"), i => i.ProductCode == "P1");
        Assert.True(IsSpecial("1001", "P1"));

        // 分类必须属于本店。
        var otherStore = await CreateCategoryAsync(service, "1002", "Foreign");
        var wrong = await service.AssignAsync(new StoreSpecialCategoryAssignRequestDto
        {
            StoreCode = "1001", CategoryId = otherStore.Id, MarkAsSpecial = false, ProductCodes = new() { "P2" },
        });
        Assert.Equal(StoreSpecialCategoryErrorCodes.CategoryNotFound, wrong.ErrorCode);
        var empty = await service.AssignAsync(new StoreSpecialCategoryAssignRequestDto { StoreCode = "1001", CategoryId = toys.Id, ProductCodes = new() });
        Assert.Equal(StoreSpecialCategoryErrorCodes.ValidationError, empty.ErrorCode);
    }

    [Fact]
    public async Task 标记特殊_同商品多行价格行全部置位_只在主行盖时间戳_不产生改价任务()
    {
        var service = AdminService();
        var older = DateTime.UtcNow.AddDays(-3);
        _db.Insertable(new[]
        {
            new StoreRetailPrice { UUID = "row-old", StoreCode = "1001", ProductCode = "P1", StoreRetailPriceValue = 4m, IsActive = true, UpdatedAt = older, CreatedAt = older },
            new StoreRetailPrice { UUID = "row-new", StoreCode = "1001", ProductCode = "P1", StoreRetailPriceValue = 5m, IsActive = true, UpdatedAt = older.AddDays(1), CreatedAt = older },
        }).ExecuteCommand();
        var toys = await CreateCategoryAsync(service, "1001", "Toys");

        await AssignAsync(service, "1001", toys.Id, true, "P1");
        var rows = _db.Queryable<StoreRetailPrice>().Where(r => r.ProductCode == "P1").ToList();
        Assert.All(rows, r => Assert.True(r.IsSpecialProduct));
        Assert.True(rows.Single(r => r.UUID == "row-new").UpdatedAt > DateTime.UtcNow.AddMinutes(-1));   // 主行盖了时间戳
        Assert.True(Math.Abs((rows.Single(r => r.UUID == "row-old").UpdatedAt!.Value - older).TotalSeconds) < 1);   // 非主行不动（SQLite 时间精度容差）
        Assert.Equal(5m, rows.Single(r => r.UUID == "row-new").StoreRetailPriceValue);                  // 价格不动
    }

    [Fact]
    public async Task 取消标记_不删归属行_列表和计数只含仍是特殊商品的_重新标记回到原分类()
    {
        var service = AdminService();
        AddPrice("1001", "P1", special: true);
        AddPrice("1001", "P2", special: true);
        AddPrice("1001", "P3", special: true);
        var toys = await CreateCategoryAsync(service, "1001", "Toys");
        await AssignAsync(service, "1001", toys.Id, false, "P1", "P2");

        var unmark = await service.UnmarkAsync(new StoreSpecialCategoryUnmarkRequestDto { StoreCode = "1001", ProductCodes = new() { "P2", "P3", "P4" } });
        Assert.Equal(2, unmark.Data!.UnmarkedCount);   // P2、P3 本来是特殊的；P4 没价格行不计
        Assert.Contains(Items("1001"), i => i.ProductCode == "P2");   // 归属行还在

        var list = (await service.GetListAsync("1001")).Data!;
        Assert.Equal(1, list.TotalCount);
        Assert.Equal(0, list.UncategorizedCount);
        Assert.Equal(1, Assert.Single(list.Categories).ItemCount);

        var products = (await service.GetProductsAsync("1001", toys.Id, null, 1, 50)).Data!;
        Assert.Equal(new[] { "P1" }, products.Items.Select(i => i.ProductCode).ToArray());

        // 重新标记（不指定分类）：归属还在，仍回到 Toys。
        await AssignAsync(service, "1001", toys.Id, true, "P2");
        var again = (await service.GetProductsAsync("1001", toys.Id, null, 1, 50)).Data!;
        Assert.Equal(new[] { "P1", "P2" }, again.Items.Select(i => i.ProductCode).ToArray());
    }

    [Fact]
    public async Task 商品列表_全部_未分类_分类_关键词_分页_只含特殊商品_分类内按服务器顺序()
    {
        var service = AdminService();
        foreach (var code in new[] { "P1", "P2", "P3", "P4" })
        {
            AddPrice("1001", code, special: true);
        }

        AddPrice("1001", "P5", special: false);
        var toys = await CreateCategoryAsync(service, "1001", "Toys");
        await AssignAsync(service, "1001", toys.Id, false, "P3", "P1");   // 服务器顺序 P3、P1

        var all = (await service.GetProductsAsync("1001", null, null, 1, 50)).Data!;
        Assert.Equal(4, all.Total);
        // 全部：按名称排序（Bauble Blue / Bauble Red / Candle / Lantern）。
        Assert.Equal(new[] { "P2", "P1", "P4", "P3" }, all.Items.Select(i => i.ProductCode).ToArray());
        var p1 = all.Items.Single(i => i.ProductCode == "P1");
        Assert.Equal("Bauble Red", p1.DisplayName);
        Assert.Equal("XD001", p1.ItemNumber);
        Assert.Equal(toys.Id, p1.CategoryId);
        Assert.Equal("Toys", p1.CategoryName);
        Assert.Equal(5m, p1.RetailPrice);
        Assert.Null(all.Items.Single(i => i.ProductCode == "P2").CategoryId);

        var uncategorized = (await service.GetProductsAsync("1001", "Uncategorized", null, 1, 50)).Data!;
        Assert.Equal(new[] { "P2", "P4" }, uncategorized.Items.Select(i => i.ProductCode).ToArray());

        var inToys = (await service.GetProductsAsync("1001", toys.Id, null, 1, 50)).Data!;
        Assert.Equal(new[] { "P3", "P1" }, inToys.Items.Select(i => i.ProductCode).ToArray());   // 服务器顺序
        Assert.Equal(new[] { 0, 1 }, inToys.Items.Select(i => i.SortOrder).ToArray());

        // 关键词匹配名称 / 货号 / 条码 / 商品编码。
        Assert.Equal(new[] { "P2", "P1" }, (await service.GetProductsAsync("1001", null, "bauble", 1, 50)).Data!.Items.Select(i => i.ProductCode).ToArray());
        Assert.Equal("P3", Assert.Single((await service.GetProductsAsync("1001", null, "XD003", 1, 50)).Data!.Items).ProductCode);
        Assert.Equal("P4", Assert.Single((await service.GetProductsAsync("1001", null, "9300000000042", 1, 50)).Data!.Items).ProductCode);
        Assert.Equal("P2", Assert.Single((await service.GetProductsAsync("1001", null, "p2", 1, 50)).Data!.Items).ProductCode);

        // 分页。
        var page2 = (await service.GetProductsAsync("1001", null, null, 2, 3)).Data!;
        Assert.Equal(4, page2.Total);
        Assert.Equal(new[] { "P3" }, page2.Items.Select(i => i.ProductCode).ToArray());

        var missing = await service.GetProductsAsync("1001", "ghost", null, 1, 50);
        Assert.Equal(StoreSpecialCategoryErrorCodes.CategoryNotFound, missing.ErrorCode);
    }

    [Fact]
    public async Task 候选搜索_至少两个字符_只含有有效价格行的商品_精确命中排最前_带特殊标记与分类()
    {
        var service = AdminService();
        AddPrice("1001", "P1", special: true);
        AddPrice("1001", "P2", special: false);
        AddPrice("1001", "P3", special: false, active: false);   // 价格行停用：不是候选
        // P4 / P6 没价格行：不是候选。
        AddPrice("1002", "P4", special: false);                  // 别店有价格行不算
        var toys = await CreateCategoryAsync(service, "1001", "Toys");
        await AssignAsync(service, "1001", toys.Id, false, "P1");

        var tooShort = await service.SearchCandidatesAsync("1001", "B", null);
        Assert.Equal(StoreSpecialCategoryErrorCodes.ValidationError, tooShort.ErrorCode);

        var byName = (await service.SearchCandidatesAsync("1001", "Bauble", null)).Data!;
        Assert.Equal(new[] { "P2", "P1" }, byName.Items.Select(i => i.ProductCode).ToArray());
        var p1 = byName.Items.Single(i => i.ProductCode == "P1");
        Assert.True(p1.IsSpecialProduct);
        Assert.Equal(toys.Id, p1.CategoryId);
        Assert.Equal("Toys", p1.CategoryName);
        Assert.False(byName.Items.Single(i => i.ProductCode == "P2").IsSpecialProduct);

        Assert.Empty((await service.SearchCandidatesAsync("1001", "Lantern", null)).Data!.Items);       // P3 停用
        Assert.Empty((await service.SearchCandidatesAsync("1001", "Candle", null)).Data!.Items);        // P4 本店没价格行
        Assert.Empty((await service.SearchCandidatesAsync("1001", "No Price", null)).Data!.Items);      // P6

        // 扫码：条码精确命中。
        var scanned = (await service.SearchCandidatesAsync("1001", "9300000000028", null)).Data!;
        Assert.Equal("P2", Assert.Single(scanned.Items).ProductCode);

        // limit 上限 50、下限 1。
        Assert.Single((await service.SearchCandidatesAsync("1001", "Bauble", 1)).Data!.Items);
    }

    [Fact]
    public async Task 分类内商品排序_必须是该分类当前特殊商品的排列_取消标记的归属行排在后面_冲突给稳定错误()
    {
        var service = AdminService();
        foreach (var code in new[] { "P1", "P2", "P3", "P4" })
        {
            AddPrice("1001", code, special: true);
        }

        var toys = await CreateCategoryAsync(service, "1001", "Toys");
        var other = await CreateCategoryAsync(service, "1001", "Other");
        await AssignAsync(service, "1001", toys.Id, false, "P1", "P2", "P3");
        await AssignAsync(service, "1001", other.Id, false, "P4");
        await service.UnmarkAsync(new StoreSpecialCategoryUnmarkRequestDto { StoreCode = "1001", ProductCodes = new() { "P2" } });   // P2 休眠

        foreach (var bad in new List<List<string>>
                 {
                     new() { "P1" },                  // 缺一个
                     new() { "P1", "P3", "P4" },      // 混入别的分类
                     new() { "P1", "P2", "P3" },      // 含休眠的
                     new() { "P3", "P3" },            // 重复
                 })
        {
            var conflict = await service.ReorderProductsAsync(toys.Id, new StoreSpecialCategoryProductOrderRequestDto { StoreCode = "1001", ProductCodes = bad });
            Assert.Equal(StoreSpecialCategoryErrorCodes.OrderConflict, conflict.ErrorCode);
        }

        var ok = await service.ReorderProductsAsync(toys.Id, new StoreSpecialCategoryProductOrderRequestDto { StoreCode = "1001", ProductCodes = new() { "P3", "P1" } });
        Assert.True(ok.Success, ok.Message);
        Assert.True(ok.Data!.Ok);
        var items = Items("1001").Where(i => i.CategoryGuid == toys.Id).OrderBy(i => i.SortOrder).ToList();
        Assert.Equal(new[] { "P3", "P1", "P2" }, items.Select(i => i.ProductCode).ToArray());   // 休眠的 P2 排在最后
        Assert.Equal(new[] { 0, 1, 2 }, items.Select(i => i.SortOrder).ToArray());
        Assert.Equal(new[] { "P3", "P1" }, (await service.GetProductsAsync("1001", toys.Id, null, 1, 50)).Data!.Items.Select(i => i.ProductCode).ToArray());

        var ghost = await service.ReorderProductsAsync("ghost", new StoreSpecialCategoryProductOrderRequestDto { StoreCode = "1001", ProductCodes = new() { "P1" } });
        Assert.Equal(StoreSpecialCategoryErrorCodes.CategoryNotFound, ghost.ErrorCode);
    }

    // ------------------------------------------------------------------ 分店范围

    [Fact]
    public async Task 分店范围_店长只能操作主分店口径内的店_非范围内一律拒绝_同步的来源与全部目标都校验()
    {
        var manager = CreateService(Scope(admin: false, "1001"));
        AddPrice("1001", "P1", special: true);
        var toys = await CreateCategoryAsync(manager, "1001", "Toys");

        // 非主分店（不在 StoreCodes 里）：所有接口都拒绝，不泄露也不写库。
        async Task<string?> CodeOf<T>(Task<ApiResponse<T>> task) => (await task).ErrorCode;
        Assert.Equal("STORE_OUT_OF_SCOPE", await CodeOf(manager.GetListAsync("1002")));
        Assert.Equal("STORE_OUT_OF_SCOPE", await CodeOf(manager.CreateAsync(new StoreSpecialCategorySaveRequestDto { StoreCode = "1002", Name = "X" })));
        Assert.Equal("STORE_OUT_OF_SCOPE", await CodeOf(manager.UpdateAsync(toys.Id, new StoreSpecialCategorySaveRequestDto { StoreCode = "1002", Name = "X" })));
        Assert.Equal("STORE_OUT_OF_SCOPE", await CodeOf(manager.DeleteAsync(toys.Id, "1002")));
        Assert.Equal("STORE_OUT_OF_SCOPE", await CodeOf(manager.ReorderCategoriesAsync(new StoreSpecialCategoryOrderRequestDto { StoreCode = "1002", CategoryIds = new() })));
        Assert.Equal("STORE_OUT_OF_SCOPE", await CodeOf(manager.GetProductsAsync("1002", null, null, 1, 10)));
        Assert.Equal("STORE_OUT_OF_SCOPE", await CodeOf(manager.SearchCandidatesAsync("1002", "Bauble", null)));
        Assert.Equal("STORE_OUT_OF_SCOPE", await CodeOf(manager.AssignAsync(new StoreSpecialCategoryAssignRequestDto { StoreCode = "1002", ProductCodes = new() { "P1" } })));
        Assert.Equal("STORE_OUT_OF_SCOPE", await CodeOf(manager.UnmarkAsync(new StoreSpecialCategoryUnmarkRequestDto { StoreCode = "1002", ProductCodes = new() { "P1" } })));
        Assert.Equal("STORE_OUT_OF_SCOPE", await CodeOf(manager.ReorderProductsAsync(toys.Id, new StoreSpecialCategoryProductOrderRequestDto { StoreCode = "1002", ProductCodes = new() { "P1" } })));
        Assert.Equal("STORE_OUT_OF_SCOPE", await CodeOf(manager.GetSyncTargetsAsync("1002")));
        Assert.Equal(1, _db.Queryable<StoreSpecialProductCategory>().Count());

        // 同步：来源在范围内、任一目标越权 → 整个请求拒绝，没有任何写入。
        var partial = CreateService(Scope(admin: false, "1001", "1002"));
        var denied = await partial.ExecuteSyncAsync(new StoreSpecialCategorySyncRequestDto
        {
            SourceStoreCode = "1001",
            TargetStoreCodes = new() { "1002", "1003" },
            Scope = "categories",
            Mode = "merge",
        });
        Assert.Equal("STORE_OUT_OF_SCOPE", denied.ErrorCode);
        Assert.Equal(0, _db.Queryable<StoreSpecialProductCategory>().Count(c => c.StoreCode == "1002"));
        // 来源越权。
        Assert.Equal("STORE_OUT_OF_SCOPE", (await partial.PreviewSyncAsync(new StoreSpecialCategorySyncRequestDto
        {
            SourceStoreCode = "1003", TargetStoreCodes = new() { "1001" }, Scope = "categories", Mode = "merge",
        })).ErrorCode);

        // 账号不被允许管理任何分店（未登录 / 无店员管理权限）。
        var notAllowed = CreateService(new CurrentUserManageableStoreScope { IsAllowed = false, Message = "no" });
        Assert.Equal("STORE_OUT_OF_SCOPE", (await notAllowed.GetListAsync("1001")).ErrorCode);

        // 不存在的分店 / 缺分店代码。
        Assert.Equal("STORE_NOT_FOUND", (await AdminService().GetListAsync("9999")).ErrorCode);
        Assert.Equal("VALIDATION_ERROR", (await AdminService().GetListAsync("  ")).ErrorCode);
    }

    // ------------------------------------------------------------------ 同步

    /// <summary>
    /// 造同步场景：
    /// 来源 1001：Toys(#E65A2F) [P1, P2]、Gifts(#111111) [P3]、Empty；P5 特殊但未分类；P4 取消标记但保留在 Gifts 的归属。
    /// 目标 1002：gifts(#222222, 已有，大小写不同) 、Local(已有，含 P2)；P1 非特殊、P2 特殊（在 Local）、P3 无价格行、P4 / P5 特殊。
    /// </summary>
    private async Task<(StoreSpecialProductCategoryService Service, string SourceToys)> SeedSyncScenarioAsync()
    {
        var service = AdminService();
        foreach (var code in new[] { "P1", "P2", "P3", "P5" })
        {
            AddPrice("1001", code, special: true);
        }

        AddPrice("1001", "P4", special: false);
        var toys = await CreateCategoryAsync(service, "1001", "Toys", "#E65A2F");
        var gifts = await CreateCategoryAsync(service, "1001", "Gifts", "#111111");
        await CreateCategoryAsync(service, "1001", "Empty");
        await AssignAsync(service, "1001", toys.Id, false, "P2", "P1");   // 来源顺序 P2、P1
        await AssignAsync(service, "1001", gifts.Id, false, "P3");
        // P4：先标记归类，再取消标记，留下休眠归属——同步时不该处理它。
        await AssignAsync(service, "1001", gifts.Id, true, "P4");
        await service.UnmarkAsync(new StoreSpecialCategoryUnmarkRequestDto { StoreCode = "1001", ProductCodes = new() { "P4" } });

        AddPrice("1002", "P1", special: false);
        AddPrice("1002", "P2", special: true);
        AddPrice("1002", "P4", special: true);
        AddPrice("1002", "P5", special: true);
        var targetGifts = await CreateCategoryAsync(service, "1002", "gifts", "#222222");
        var local = await CreateCategoryAsync(service, "1002", "Local");
        await AssignAsync(service, "1002", local.Id, false, "P2", "P4");
        Assert.Equal(targetGifts.Id, _db.Queryable<StoreSpecialProductCategory>().First(c => c.StoreCode == "1002" && c.Name == "gifts").Guid);
        return (service, toys.Id);
    }

    private static StoreSpecialCategorySyncRequestDto SyncRequest(
        string scope, string mode, bool confirm = false, params string[] targets) => new()
    {
        SourceStoreCode = "1001",
        TargetStoreCodes = targets.ToList(),
        Scope = scope,
        Mode = mode,
        ConfirmReplace = confirm,
    };

    [Fact]
    public async Task 同步目标列表_只含管理范围内启用且未删除的其他分店_带分类数和特殊商品数()
    {
        var (service, _) = await SeedSyncScenarioAsync();

        var targets = (await service.GetSyncTargetsAsync("1001")).Data!;
        Assert.Equal("1001", targets.Source.StoreCode);
        Assert.Equal(3, targets.Source.CategoryCount);
        Assert.Equal(3, targets.Source.ProductCount);   // P1、P2、P3：仍是特殊商品且已归类（P4 休眠、P5 未分类不算）
        Assert.Equal(new[] { "1002", "1003" }, targets.Targets.Select(t => t.StoreCode).ToArray());   // 不含来源 1001 和停用的 1004
        var kawana = targets.Targets.Single(t => t.StoreCode == "1002");
        Assert.Equal("Kawana", kawana.StoreName);
        Assert.Equal(2, kawana.CategoryCount);
        Assert.Equal(3, kawana.SpecialProductCount);    // P2、P4、P5
        Assert.Equal(0, targets.Targets.Single(t => t.StoreCode == "1003").CategoryCount);

        // 店长只看到自己管理范围内的店。
        var manager = CreateService(Scope(admin: false, "1001", "1003"));
        Assert.Equal(new[] { "1003" }, (await manager.GetSyncTargetsAsync("1001")).Data!.Targets.Select(t => t.StoreCode).ToArray());
    }

    [Fact]
    public async Task 同步_仅分类_merge_按名称合并_颜色与顺序跟来源_目标独有的排后面_不改任何标记与归属()
    {
        var (service, _) = await SeedSyncScenarioAsync();
        var itemsBefore = Items("1002").Select(i => (i.ProductCode, i.CategoryGuid, i.SortOrder)).OrderBy(x => x.ProductCode).ToList();

        var preview = (await service.PreviewSyncAsync(SyncRequest("categories", "merge", false, "1002", "1003"))).Data!;
        var kawana = preview.Targets.Single(t => t.StoreCode == "1002");
        Assert.Equal((2, 1, 0), (kawana.CreatedCategories, kawana.MergedCategories, kawana.RemovedCategories));   // Toys / Empty 新建，gifts 合并
        Assert.Equal(0, kawana.NewlyMarkedProducts + kawana.ReassignedProducts + kawana.UnchangedProducts + kawana.UnmarkedProducts);
        Assert.Empty(kawana.Skipped);
        Assert.Equal(3, preview.Targets.Single(t => t.StoreCode == "1003").CreatedCategories);
        // 预览只算不写。
        Assert.Equal(2, _db.Queryable<StoreSpecialProductCategory>().Count(c => c.StoreCode == "1002"));
        Assert.Equal(0, _db.Queryable<StoreSpecialProductCategory>().Count(c => c.StoreCode == "1003"));

        var executed = (await service.ExecuteSyncAsync(SyncRequest("categories", "merge", false, "1002", "1003"))).Data!;
        Assert.All(executed.Results, r => Assert.True(r.Ok, r.Error));
        var list = (await service.GetListAsync("1002")).Data!;
        Assert.Equal(new[] { "Toys", "gifts", "Empty", "Local" }, Names(list.Categories));   // 来源顺序在前，目标独有的 Local 在后
        Assert.Equal("#E65A2F", list.Categories.Single(c => c.Name == "Toys").Color);
        Assert.Equal("#111111", list.Categories.Single(c => c.Name == "gifts").Color);          // 颜色跟来源，名称保留目标店原写法
        Assert.Equal(new[] { 0, 1, 2, 3 }, list.Categories.OrderBy(c => c.SortOrder).Select(c => c.SortOrder).ToArray());
        // scope=categories 不改目标店任何特殊商品标记与归属。
        Assert.Equal(itemsBefore, Items("1002").Select(i => (i.ProductCode, i.CategoryGuid, i.SortOrder)).OrderBy(x => x.ProductCode).ToList());
        Assert.False(IsSpecial("1002", "P1"));
        Assert.True(IsSpecial("1002", "P4"));
        Assert.Equal(new[] { "Toys", "Gifts", "Empty" }, Names((await service.GetListAsync("1003")).Data!.Categories));
    }

    [Fact]
    public async Task 同步_分类和商品_merge_标记并归类_保持来源分类内顺序_冲突以来源为准_没价格行跳过_不删任何东西()
    {
        var (service, toysId) = await SeedSyncScenarioAsync();

        var preview = (await service.PreviewSyncAsync(SyncRequest("categories-and-products", "merge", false, "1002"))).Data!.Targets.Single();
        Assert.Equal(1, preview.NewlyMarkedProducts);      // P1 在目标店不是特殊商品
        Assert.Equal(1, preview.ReassignedProducts);       // P2 原来在 Local，改到 Toys
        Assert.Equal(0, preview.UnchangedProducts);
        Assert.Equal(0, preview.UnmarkedProducts);         // merge 不取消任何标记
        var skipped = Assert.Single(preview.Skipped);      // P3 目标店没有价格行
        Assert.Equal(("P3", "Lantern", "NO_STORE_PRICE"), (skipped.ProductCode, skipped.DisplayName, skipped.Reason));
        Assert.False(IsSpecial("1002", "P1"));             // 预览没写库

        var result = (await service.ExecuteSyncAsync(SyncRequest("categories-and-products", "merge", false, "1002"))).Data!.Results.Single();
        Assert.True(result.Ok, result.Error);
        Assert.Equal((1, 1, 0), (result.NewlyMarkedProducts, result.ReassignedProducts, result.UnmarkedProducts));
        Assert.True(IsSpecial("1002", "P1"));
        Assert.True(IsSpecial("1002", "P4"));              // merge 不取消目标店的特殊商品
        Assert.Equal(0, _db.Queryable<StoreRetailPrice>().Count(r => r.StoreCode == "1002" && r.ProductCode == "P3"));   // 没有新建价格行

        var targetToys = _db.Queryable<StoreSpecialProductCategory>().First(c => c.StoreCode == "1002" && c.Name == "Toys");
        var inToys = Items("1002").Where(i => i.CategoryGuid == targetToys.Guid).OrderBy(i => i.SortOrder).ToList();
        Assert.Equal(new[] { "P2", "P1" }, inToys.Select(i => i.ProductCode).ToArray());   // 保持来源的分类内顺序 P2、P1
        // P4 原来在 Local，来源里不是特殊商品，不动；Local 分类仍在。
        var local = _db.Queryable<StoreSpecialProductCategory>().First(c => c.StoreCode == "1002" && c.Name == "Local");
        Assert.Equal(new[] { "P4" }, Items("1002").Where(i => i.CategoryGuid == local.Guid).Select(i => i.ProductCode).ToArray());

        // 来源店不受影响。
        Assert.Equal(4, Items("1001").Count);   // P2、P1、P3，加休眠的 P4
        // 再同步一次：全部「不变」，不重复计数。
        var second = (await service.PreviewSyncAsync(SyncRequest("categories-and-products", "merge", false, "1002"))).Data!.Targets.Single();
        Assert.Equal((0, 0, 2), (second.NewlyMarkedProducts, second.ReassignedProducts, second.UnchangedProducts));
    }

    [Fact]
    public async Task 同步_replace_删除目标独有分类并取消来源非特殊商品的标记_必须confirmReplace_预览如实算出两个数字()
    {
        var (service, _) = await SeedSyncScenarioAsync();

        var preview = (await service.PreviewSyncAsync(SyncRequest("categories-and-products", "replace", false, "1002"))).Data!.Targets.Single();
        Assert.Equal(1, preview.RemovedCategories);        // Local
        Assert.Equal(1, preview.UnmarkedProducts);         // P4：目标店特殊，来源店不是特殊商品（P5 来源店也是特殊，保留）
        Assert.Equal(1, preview.NewlyMarkedProducts);
        Assert.Equal(1, preview.ReassignedProducts);

        // 没确认就拒绝，且什么都没写。
        var refused = await service.ExecuteSyncAsync(SyncRequest("categories-and-products", "replace", false, "1002"));
        Assert.False(refused.Success);
        Assert.Equal(StoreSpecialCategoryErrorCodes.ReplaceNotConfirmed, refused.ErrorCode);
        Assert.Equal(2, _db.Queryable<StoreSpecialProductCategory>().Count(c => c.StoreCode == "1002"));
        Assert.True(IsSpecial("1002", "P4"));

        var result = (await service.ExecuteSyncAsync(SyncRequest("categories-and-products", "replace", true, "1002"))).Data!.Results.Single();
        Assert.True(result.Ok, result.Error);
        Assert.Equal((2, 1, 1, 1, 1), (result.CreatedCategories, result.RemovedCategories, result.UnmarkedProducts, result.NewlyMarkedProducts, result.ReassignedProducts));
        Assert.Equal(new[] { "Toys", "gifts", "Empty" }, Names((await service.GetListAsync("1002")).Data!.Categories));
        Assert.False(IsSpecial("1002", "P4"));             // 被取消标记
        Assert.True(IsSpecial("1002", "P5"));              // 来源也是特殊商品，保留
        Assert.True(IsSpecial("1002", "P1"));
        // Local 的归属行被删除（P4 的行删了），P2 已改到 Toys。
        Assert.DoesNotContain(Items("1002"), i => i.ProductCode == "P4");
        var targetToys = _db.Queryable<StoreSpecialProductCategory>().First(c => c.StoreCode == "1002" && c.Name == "Toys");
        Assert.Equal(new[] { "P2", "P1" }, Items("1002").Where(i => i.CategoryGuid == targetToys.Guid).OrderBy(i => i.SortOrder).Select(i => i.ProductCode).ToArray());
    }

    [Fact]
    public async Task 同步_replace_仅分类_目标独有分类被删其商品回到未分类_特殊标记全部不动_unmarked恒为0()
    {
        var (service, _) = await SeedSyncScenarioAsync();

        var result = (await service.ExecuteSyncAsync(SyncRequest("categories", "replace", true, "1002"))).Data!.Results.Single();
        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, result.RemovedCategories);
        Assert.Equal(0, result.UnmarkedProducts);
        Assert.True(IsSpecial("1002", "P4"));
        Assert.True(IsSpecial("1002", "P2"));
        Assert.Empty(Items("1002"));                       // Local 的归属行随分类删除，商品回到未分类
        Assert.Equal(new[] { "Toys", "gifts", "Empty" }, Names((await service.GetListAsync("1002")).Data!.Categories));
        Assert.Equal(3, (await service.GetListAsync("1002")).Data!.UncategorizedCount);
    }

    [Fact]
    public async Task 同步_每家目标店一个事务_某店失败整店回滚且不影响其他店()
    {
        var (service, _) = await SeedSyncScenarioAsync();
        AddPrice("1003", "P1", special: false);
        var boom = service;
        boom.BeforeSyncCommitForTesting = store =>
            store == "1002" ? throw new InvalidOperationException("模拟 1002 中途失败") : Task.CompletedTask;

        var response = await service.ExecuteSyncAsync(SyncRequest("categories-and-products", "merge", false, "1002", "1003"));
        Assert.True(response.Success);                     // 请求本身成功，逐店给结果
        var failed = response.Data!.Results.Single(r => r.StoreCode == "1002");
        var succeeded = response.Data.Results.Single(r => r.StoreCode == "1003");
        Assert.False(failed.Ok);
        // 对外只给固定稳定码，不回传异常细节。
        Assert.Equal(StoreSpecialCategoryErrorCodes.SyncStoreFailed, failed.Error);
        Assert.DoesNotContain("模拟", failed.Error);
        Assert.True(succeeded.Ok, succeeded.Error);

        // 1002 整店回滚：分类、归属、标记都没变。
        Assert.Equal(new[] { "gifts", "Local" }, Names((await boom.GetListAsync("1002")).Data!.Categories));
        Assert.False(IsSpecial("1002", "P1"));
        Assert.Equal(2, Items("1002").Count);
        // 1003 已完成。
        Assert.Equal(new[] { "Toys", "Gifts", "Empty" }, Names((await boom.GetListAsync("1003")).Data!.Categories));
        Assert.True(IsSpecial("1003", "P1"));
        Assert.Equal("P1", Assert.Single(Items("1003")).ProductCode);
    }

    [Fact]
    public async Task 同步_合并后分类数超过上限的店_预览与执行都给出原因且不动该店_其他店照常()
    {
        var (service, _) = await SeedSyncScenarioAsync();
        _db.Insertable(Enumerable.Range(0, 49).Select(i => new StoreSpecialProductCategory
        {
            StoreCode = "1003",
            Name = $"Existing{i:00}",
            SortOrder = i,
        }).ToList()).ExecuteCommand();

        var preview = (await service.PreviewSyncAsync(SyncRequest("categories", "merge", false, "1002", "1003"))).Data!;
        Assert.Null(preview.Targets.Single(t => t.StoreCode == "1002").Error);
        Assert.NotNull(preview.Targets.Single(t => t.StoreCode == "1003").Error);

        var executed = (await service.ExecuteSyncAsync(SyncRequest("categories", "merge", false, "1002", "1003"))).Data!;
        Assert.True(executed.Results.Single(r => r.StoreCode == "1002").Ok);
        var capped = executed.Results.Single(r => r.StoreCode == "1003");
        Assert.False(capped.Ok);
        Assert.False(string.IsNullOrWhiteSpace(capped.Error));
        Assert.Equal(49, _db.Queryable<StoreSpecialProductCategory>().Count(c => c.StoreCode == "1003"));
    }

    [Fact]
    public async Task 同步_请求校验_范围与方式必须合法_目标必填_剔除来源与重复_不能同步到停用分店()
    {
        var (service, _) = await SeedSyncScenarioAsync();

        Assert.Equal("VALIDATION_ERROR", (await service.PreviewSyncAsync(SyncRequest("everything", "merge", false, "1002"))).ErrorCode);
        Assert.Equal("VALIDATION_ERROR", (await service.PreviewSyncAsync(SyncRequest("categories", "overwrite", false, "1002"))).ErrorCode);
        Assert.Equal("VALIDATION_ERROR", (await service.PreviewSyncAsync(SyncRequest("categories", "merge", false))).ErrorCode);
        Assert.Equal("VALIDATION_ERROR", (await service.PreviewSyncAsync(SyncRequest("categories", "merge", false, "1001"))).ErrorCode);   // 只有来源自己
        Assert.Equal("VALIDATION_ERROR", (await service.PreviewSyncAsync(SyncRequest("categories", "merge", false, "1004"))).ErrorCode);   // 停用
        Assert.Equal("VALIDATION_ERROR", (await service.PreviewSyncAsync(null)).ErrorCode);

        var deduped = (await service.PreviewSyncAsync(SyncRequest("categories", "merge", false, "1001", "1002", "1002"))).Data!;
        Assert.Equal("1002", Assert.Single(deduped.Targets).StoreCode);
    }

    // ------------------------------------------------------------------ 「仍是特殊商品」口径（与 POS 一致）

    /// <summary>给商品在某店加一条价格行，可指定更新时间（决定「最新一行」）与启用状态。</summary>
    private void AddPriceRow(string store, string product, string uuid, bool special, bool active, DateTime updatedAt, decimal price = 5m)
    {
        _db.Insertable(new StoreRetailPrice
        {
            UUID = uuid,
            StoreCode = store,
            ProductCode = product,
            StoreRetailPriceValue = price,
            IsSpecialProduct = special,
            IsActive = active,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
        }).ExecuteCommand();
    }

    [Fact]
    public async Task 特殊商品口径_停用行带旧标记不算_多行标记不一致取最新启用行_列表计数候选一致()
    {
        var service = AdminService();
        var now = DateTime.UtcNow;
        // P1：只有一条停用行带标记 → 不是特殊商品。
        AddPriceRow("1001", "P1", "p1-off", special: true, active: false, now.AddDays(-1));
        // P2：旧行带标记、最新启用行没标记 → 不是特殊商品。
        AddPriceRow("1001", "P2", "p2-old", special: true, active: true, now.AddDays(-5));
        AddPriceRow("1001", "P2", "p2-new", special: false, active: true, now.AddDays(-1));
        // P3：旧行没标记、最新启用行带标记 → 是特殊商品；比它更新的停用行（没标记）不参与裁决。
        AddPriceRow("1001", "P3", "p3-old", special: false, active: true, now.AddDays(-5));
        AddPriceRow("1001", "P3", "p3-new", special: true, active: true, now.AddDays(-1));
        AddPriceRow("1001", "P3", "p3-off", special: false, active: false, now);
        // P4：更新时间并列时按 UUID 升序取第一行裁决（POS 同口径）。
        AddPriceRow("1001", "P4", "p4-a", special: true, active: true, now.AddDays(-2));
        AddPriceRow("1001", "P4", "p4-b", special: false, active: true, now.AddDays(-2));
        // P5：已删除的行不参与。
        AddPriceRow("1001", "P5", "p5-live", special: false, active: true, now.AddDays(-3));
        _db.Insertable(new StoreRetailPrice
        {
            UUID = "p5-deleted", StoreCode = "1001", ProductCode = "P5", IsSpecialProduct = true, IsActive = true,
            IsDeleted = true, UpdatedAt = now, CreatedAt = now,
        }).ExecuteCommand();

        var list = (await service.GetListAsync("1001")).Data!;
        Assert.Equal(new[] { "P3", "P4" }, (await service.GetProductsAsync("1001", null, null, 1, 50)).Data!.Items.Select(i => i.ProductCode).OrderBy(c => c).ToArray());
        Assert.Equal(2, list.TotalCount);
        Assert.Equal(2, list.UncategorizedCount);

        // 候选里的 isSpecialProduct 同口径（P1 只有停用行，不是候选）。
        var candidates = (await service.SearchCandidatesAsync("1001", "Bauble", null)).Data!.Items;   // P1 Red / P2 Blue
        Assert.Equal(new[] { "P2" }, candidates.Select(c => c.ProductCode).ToArray());
        Assert.False(candidates.Single().IsSpecialProduct);
        Assert.True((await service.SearchCandidatesAsync("1001", "Lantern", null)).Data!.Items.Single().IsSpecialProduct);   // P3

        // 同步目标列表里的 specialProductCount 也是同口径：给 1002 造同样的数据。
        AddPriceRow("1002", "P2", "k-p2-old", special: true, active: true, now.AddDays(-5));
        AddPriceRow("1002", "P2", "k-p2-new", special: false, active: true, now.AddDays(-1));
        AddPriceRow("1002", "P3", "k-p3", special: true, active: true, now.AddDays(-1));
        var targets = (await service.GetSyncTargetsAsync("1001")).Data!.Targets;
        Assert.Equal(1, targets.Single(t => t.StoreCode == "1002").SpecialProductCount);
    }

    [Fact]
    public async Task 归类_旧行特殊但最新行不特殊的商品_会真正标记_MarkedCount为1_全部未删除行置位_不恢复停用行()
    {
        var service = AdminService();
        var now = DateTime.UtcNow;
        AddPriceRow("1001", "P1", "old-special", special: true, active: true, now.AddDays(-5));
        AddPriceRow("1001", "P1", "new-plain", special: false, active: true, now.AddDays(-1));
        AddPriceRow("1001", "P1", "off", special: false, active: false, now.AddDays(-3));
        AddPriceRow("1001", "P2", "p2-only-off", special: true, active: false, now.AddDays(-1));   // 只有停用行
        var toys = await CreateCategoryAsync(service, "1001", "Toys");

        var response = await service.AssignAsync(new StoreSpecialCategoryAssignRequestDto
        {
            StoreCode = "1001", CategoryId = toys.Id, MarkAsSpecial = true, ProductCodes = new() { "P1", "P2" },
        });
        Assert.True(response.Success, response.Message);
        Assert.Equal(1, response.Data!.MarkedCount);
        Assert.Equal(1, response.Data.AssignedCount);
        var skipped = Assert.Single(response.Data.Skipped);
        Assert.Equal(("P2", "NO_STORE_PRICE"), (skipped.ProductCode, skipped.Reason));   // 没有任何启用行：不恢复停用行
        Assert.All(_db.Queryable<StoreRetailPrice>().Where(r => r.ProductCode == "P1").ToList(), r => Assert.True(r.IsSpecialProduct));
        Assert.False(_db.Queryable<StoreRetailPrice>().First(r => r.UUID == "p2-only-off").IsActive);   // 仍停用
        Assert.Equal(1, (await service.GetListAsync("1001")).Data!.TotalCount);

        // markAsSpecial=false：旧行特殊不算「已是特殊商品」，要跳过。
        AddPriceRow("1001", "P3", "p3-old", special: true, active: true, now.AddDays(-5));
        AddPriceRow("1001", "P3", "p3-new", special: false, active: true, now.AddDays(-1));
        var notSpecial = await service.AssignAsync(new StoreSpecialCategoryAssignRequestDto
        {
            StoreCode = "1001", CategoryId = toys.Id, MarkAsSpecial = false, ProductCodes = new() { "P3" },
        });
        Assert.Equal("NOT_SPECIAL_PRODUCT", Assert.Single(notSpecial.Data!.Skipped).Reason);
    }

    [Fact]
    public async Task 标记特殊_全局审计AOP下只给主行盖时间_非主行时间与操作人不变_最新行不会换人()
    {
        // 同构模拟生产 SqlSugarContext 的审计 AOP：UpdateByObject 时把 UpdatedAt / UpdatedBy 改成现在 / 系统，除非处于 Preserve 作用域。
        _db.Aop.DataExecuting = (_, entityInfo) =>
        {
            if (entityInfo.EntityValue is not BaseEntity
                || entityInfo.OperationType != DataFilterType.UpdateByObject
                || SqlSugarAuditScope.ShouldPreserveExplicitAuditFields)
            {
                return;
            }

            if (entityInfo.PropertyName == "UpdatedAt")
            {
                entityInfo.SetValue(DateTime.UtcNow);
            }

            if (entityInfo.PropertyName == "UpdatedBy")
            {
                entityInfo.SetValue("AOP-System");
            }
        };
        var service = AdminService();
        var now = DateTime.UtcNow;
        AddPriceRow("1001", "P1", "row-older", special: false, active: true, now.AddDays(-5));
        AddPriceRow("1001", "P1", "row-latest", special: false, active: true, now.AddDays(-2));
        AddPriceRow("1001", "P1", "row-mid", special: false, active: true, now.AddDays(-3));
        var toys = await CreateCategoryAsync(service, "1001", "Toys");
        await CreateCategoryAsync(service, "1001", "Other");

        await AssignAsync(service, "1001", toys.Id, true, "P1");

        var rows = _db.Queryable<StoreRetailPrice>().Where(r => r.ProductCode == "P1").ToList().ToDictionary(r => r.UUID);
        Assert.All(rows.Values, r => Assert.True(r.IsSpecialProduct));
        // 主行（最新启用行）盖了当前时间与操作人；非主行的时间、操作人都没被 AOP 改写。
        Assert.True(rows["row-latest"].UpdatedAt > now.AddMinutes(-1));
        Assert.Equal("tester", rows["row-latest"].UpdatedBy);
        Assert.True(Math.Abs((rows["row-older"].UpdatedAt!.Value - now.AddDays(-5)).TotalSeconds) < 1);
        Assert.True(Math.Abs((rows["row-mid"].UpdatedAt!.Value - now.AddDays(-3)).TotalSeconds) < 1);
        Assert.NotEqual("AOP-System", rows["row-older"].UpdatedBy);
        Assert.NotEqual("AOP-System", rows["row-mid"].UpdatedBy);
        Assert.Equal("row-latest", rows.Values.OrderByDescending(r => r.UpdatedAt).First().UUID);   // 最新行没有悄悄换成别的行

        // 对照：不在 Preserve 作用域的写入（分类排序）确实会被这个 AOP 改写，证明模拟拦截器生效。
        var categories = (await service.GetListAsync("1001")).Data!.Categories;
        await service.ReorderCategoriesAsync(new StoreSpecialCategoryOrderRequestDto
        {
            StoreCode = "1001",
            CategoryIds = categories.Select(c => c.Id).Reverse().ToList(),
        });
        Assert.Contains(_db.Queryable<StoreSpecialProductCategory>().ToList(), c => c.UpdatedBy == "AOP-System");

        // 取消标记同样只给主行盖时间。
        await service.UnmarkAsync(new StoreSpecialCategoryUnmarkRequestDto { StoreCode = "1001", ProductCodes = new() { "P1" } });
        var after = _db.Queryable<StoreRetailPrice>().Where(r => r.ProductCode == "P1").ToList().ToDictionary(r => r.UUID);
        Assert.All(after.Values, r => Assert.False(r.IsSpecialProduct));
        Assert.True(Math.Abs((after["row-older"].UpdatedAt!.Value - now.AddDays(-5)).TotalSeconds) < 1);
    }

    [Fact]
    public async Task 同步_来源与目标的特殊商品集合都按统一口径_旧行特殊的来源商品不同步_replace只取消真正特殊的()
    {
        var service = AdminService();
        var now = DateTime.UtcNow;
        // 来源 1001：P1 真特殊、已归类；P2 旧行特殊但最新行不特殊（不算特殊，不同步）。
        AddPriceRow("1001", "P1", "s-p1", special: true, active: true, now.AddDays(-1));
        AddPriceRow("1001", "P2", "s-p2-old", special: true, active: true, now.AddDays(-5));
        AddPriceRow("1001", "P2", "s-p2-new", special: false, active: true, now.AddDays(-1));
        var toys = await CreateCategoryAsync(service, "1001", "Toys");
        await AssignAsync(service, "1001", toys.Id, false, "P1");
        // 休眠归属：P2 的归属行直接造（它现在不是特殊商品）。
        _db.Insertable(new StoreSpecialProductCategoryItem { StoreCode = "1001", ProductCode = "P2", CategoryGuid = toys.Id, SortOrder = 1 }).ExecuteCommand();

        // 目标 1002：P1 非特殊；P2 真特殊；P3 只有停用行带标记（不算特殊，不该被计入取消标记）；P4 真特殊。
        AddPriceRow("1002", "P1", "t-p1", special: false, active: true, now.AddDays(-1));
        AddPriceRow("1002", "P2", "t-p2", special: true, active: true, now.AddDays(-1));
        AddPriceRow("1002", "P3", "t-p3", special: true, active: false, now.AddDays(-1));
        AddPriceRow("1002", "P4", "t-p4", special: true, active: true, now.AddDays(-1));

        var targets = (await service.GetSyncTargetsAsync("1001")).Data!;
        Assert.Equal(1, targets.Source.ProductCount);                    // 只有 P1
        var preview = (await service.PreviewSyncAsync(SyncRequest("categories-and-products", "replace", true, "1002"))).Data!.Targets.Single();
        Assert.Equal(1, preview.NewlyMarkedProducts);                    // P1
        Assert.Equal(2, preview.UnmarkedProducts);                       // P2、P4 真特殊且来源不是特殊；P3 停用行上的旧标记不算

        var result = (await service.ExecuteSyncAsync(SyncRequest("categories-and-products", "replace", true, "1002"))).Data!.Results.Single();
        Assert.True(result.Ok, result.Error);
        Assert.True(_db.Queryable<StoreRetailPrice>().First(r => r.UUID == "t-p1").IsSpecialProduct);
        Assert.False(_db.Queryable<StoreRetailPrice>().First(r => r.UUID == "t-p2").IsSpecialProduct);
        Assert.False(_db.Queryable<StoreRetailPrice>().First(r => r.UUID == "t-p4").IsSpecialProduct);
        Assert.Equal(new[] { "P1" }, (await service.GetProductsAsync("1002", null, null, 1, 50)).Data!.Items.Select(i => i.ProductCode).ToArray());
    }

    // ------------------------------------------------------------------ 并发与唯一冲突

    [Fact]
    public async Task 归类_撞归属主键时整个事务回滚后重读重试一次_仍冲突给409稳定错误码()
    {
        var service = AdminService();
        AddPrice("1001", "P1", special: false);
        var toys = await CreateCategoryAsync(service, "1001", "Toys");

        // 第一次撞主键（模拟 SQL Server 2627 / SQLite 文本），重试成功：标记与归属都只落一次，没有 500。
        var calls = 0;
        service.BeforeAssignInsertForTesting = () =>
            ++calls == 1 ? throw new InvalidOperationException("UNIQUE constraint failed: StoreSpecialProductCategoryItem.StoreCode, ProductCode") : Task.CompletedTask;
        var retried = await service.AssignAsync(new StoreSpecialCategoryAssignRequestDto
        {
            StoreCode = "1001", CategoryId = toys.Id, MarkAsSpecial = true, ProductCodes = new() { "P1" },
        });
        Assert.True(retried.Success, retried.Message);
        Assert.Equal(2, calls);
        Assert.Equal(1, retried.Data!.MarkedCount);        // 第一次的标记随事务回滚，重试时重新算出
        Assert.True(IsSpecial("1001", "P1"));
        Assert.Equal("P1", Assert.Single(Items("1001")).ProductCode);

        // 两次都撞：409 + SPECIAL_CATEGORY_ASSIGN_CONFLICT，数据不变。
        AddPrice("1001", "P2", special: false);
        service.BeforeAssignInsertForTesting = () => throw new InvalidOperationException("UNIQUE constraint failed: x");
        var conflict = await service.AssignAsync(new StoreSpecialCategoryAssignRequestDto
        {
            StoreCode = "1001", CategoryId = toys.Id, MarkAsSpecial = true, ProductCodes = new() { "P2" },
        });
        Assert.False(conflict.Success);
        Assert.Equal(StoreSpecialCategoryErrorCodes.AssignConflict, conflict.ErrorCode);
        Assert.False(IsSpecial("1001", "P2"));
        Assert.DoesNotContain(Items("1001"), i => i.ProductCode == "P2");
        var controller = CreateController(service, authenticated: true, Permissions.PosTerminal.SpecialProducts.Manage);
        Assert.Equal(409, Assert.IsType<ConflictObjectResult>(await controller.Assign(new StoreSpecialCategoryAssignRequestDto
        {
            StoreCode = "1001", CategoryId = toys.Id, MarkAsSpecial = true, ProductCodes = new() { "P2" },
        })).StatusCode);

        // 非唯一冲突的异常不吞：原样抛出。
        service.BeforeAssignInsertForTesting = () => throw new InvalidOperationException("disk on fire");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AssignAsync(new StoreSpecialCategoryAssignRequestDto
        {
            StoreCode = "1001", CategoryId = toys.Id, MarkAsSpecial = true, ProductCodes = new() { "P2" },
        }));
    }

    [Fact]
    public async Task 同名分类_预检通过但数据库唯一索引口径更严_撞索引也是409重名而不是500()
    {
        var service = AdminService();
        // 换成「前 3 个字符相同即冲突」的唯一索引，模拟 .NET OrdinalIgnoreCase 预检与数据库排序规则口径不一致。
        _db.Ado.ExecuteCommand("DROP INDEX UX_StoreSpecialProductCategory_Store_Name");
        _db.Ado.ExecuteCommand("CREATE UNIQUE INDEX UX_StoreSpecialProductCategory_Store_Name ON StoreSpecialProductCategory (StoreCode, substr(Name, 1, 3))");

        var first = await CreateCategoryAsync(service, "1001", "Toy A");
        var created = await service.CreateAsync(new StoreSpecialCategorySaveRequestDto { StoreCode = "1001", Name = "Toy B" });
        Assert.False(created.Success);
        Assert.Equal(StoreSpecialCategoryErrorCodes.NameDuplicate, created.ErrorCode);

        var other = await CreateCategoryAsync(service, "1001", "Other");
        var renamed = await service.UpdateAsync(other.Id, new StoreSpecialCategorySaveRequestDto { StoreCode = "1001", Name = "Toy C" });
        Assert.False(renamed.Success);
        Assert.Equal(StoreSpecialCategoryErrorCodes.NameDuplicate, renamed.ErrorCode);
        Assert.Equal("Other", _db.Queryable<StoreSpecialProductCategory>().First(c => c.Guid == other.Id).Name);
        Assert.Equal(first.Id, _db.Queryable<StoreSpecialProductCategory>().First(c => c.Name == "Toy A").Guid);
    }

    [Fact]
    public async Task 删除分类与排序_只写各自的列_排序不会用旧值覆盖名称颜色()
    {
        var service = AdminService();
        AddPrice("1001", "P1", special: true);
        AddPrice("1001", "P2", special: true);
        var a = await CreateCategoryAsync(service, "1001", "A", "#111111");
        var b = await CreateCategoryAsync(service, "1001", "B", "#222222");
        await AssignAsync(service, "1001", a.Id, false, "P1", "P2");

        // 别处改名后，用旧快照发起的分类内排序不能把名称写回去（排序只写 SortOrder 与审计列）。
        _db.Updateable<StoreSpecialProductCategory>().SetColumns(c => c.Name == "A-renamed").Where(c => c.Guid == a.Id).ExecuteCommand();
        Assert.True((await service.ReorderProductsAsync(a.Id, new StoreSpecialCategoryProductOrderRequestDto { StoreCode = "1001", ProductCodes = new() { "P2", "P1" } })).Success);
        Assert.True((await service.ReorderCategoriesAsync(new StoreSpecialCategoryOrderRequestDto { StoreCode = "1001", CategoryIds = new() { b.Id, a.Id } })).Success);
        var row = _db.Queryable<StoreSpecialProductCategory>().First(c => c.Guid == a.Id);
        Assert.Equal("A-renamed", row.Name);
        Assert.Equal("#111111", row.Color);
        Assert.Equal(1, row.SortOrder);

        // 删除：事务内按分类整体删归属行（含休眠行）；removedItemCount 只数仍是特殊商品的。
        await service.UnmarkAsync(new StoreSpecialCategoryUnmarkRequestDto { StoreCode = "1001", ProductCodes = new() { "P1" } });
        var deleted = await service.DeleteAsync(a.Id, "1001");
        Assert.Equal(1, deleted.Data!.RemovedItemCount);
        Assert.Empty(Items("1001"));
    }

    // ------------------------------------------------------------------ 控制器

    private static StoreSpecialProductCategoriesController CreateController(
        IStoreSpecialProductCategoryService service,
        bool authenticated,
        params string[] grantedPolicies)
    {
        var auth = new Mock<IAuthorizationService>();
        auth.Setup(x => x.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync((ClaimsPrincipal _, object? _, string policy) =>
                grantedPolicies.Contains(policy) ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var identity = authenticated
            ? new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "tester") }, "test")
            : new ClaimsIdentity();   // 匿名设备会话：没有认证身份
        return new StoreSpecialProductCategoriesController(service, auth.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
            },
        };
    }

    [Fact]
    public async Task 控制器_设备会话_匿名_所有接口一律Forbid_且不会碰服务()
    {
        var service = new Mock<IStoreSpecialProductCategoryService>(MockBehavior.Strict);
        var controller = CreateController(
            service.Object, authenticated: false,
            Permissions.PosTerminal.SpecialProducts.View, Permissions.PosTerminal.SpecialProducts.Manage);

        Assert.IsType<ForbidResult>(await controller.GetList("1001"));
        Assert.IsType<ForbidResult>(await controller.Create(new StoreSpecialCategorySaveRequestDto()));
        Assert.IsType<ForbidResult>(await controller.ReorderCategories(new StoreSpecialCategoryOrderRequestDto()));
        Assert.IsType<ForbidResult>(await controller.Update("x", new StoreSpecialCategorySaveRequestDto()));
        Assert.IsType<ForbidResult>(await controller.Delete("x", "1001"));
        Assert.IsType<ForbidResult>(await controller.GetProducts("1001", null, null));
        Assert.IsType<ForbidResult>(await controller.SearchCandidates("1001", "ab", null));
        Assert.IsType<ForbidResult>(await controller.Assign(new StoreSpecialCategoryAssignRequestDto()));
        Assert.IsType<ForbidResult>(await controller.Unmark(new StoreSpecialCategoryUnmarkRequestDto()));
        Assert.IsType<ForbidResult>(await controller.ReorderProducts("x", new StoreSpecialCategoryProductOrderRequestDto()));
        Assert.IsType<ForbidResult>(await controller.GetSyncTargets("1001"));
        Assert.IsType<ForbidResult>(await controller.PreviewSync(new StoreSpecialCategorySyncRequestDto()));
        Assert.IsType<ForbidResult>(await controller.ExecuteSync(new StoreSpecialCategorySyncRequestDto()));
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 控制器_读接口认View或Manage_无权限Forbid_业务错误码映射成稳定的HTTP状态()
    {
        var real = AdminService();
        // 只持 View：读 OK。
        var viewer = CreateController(real, authenticated: true, Permissions.PosTerminal.SpecialProducts.View);
        Assert.IsType<OkObjectResult>(await viewer.GetList("1001"));
        // 只持 Manage：也能读（移动端入口只认 Manage）。
        var manager = CreateController(real, authenticated: true, Permissions.PosTerminal.SpecialProducts.Manage);
        Assert.IsType<OkObjectResult>(await manager.GetList("1001"));
        // 两者都没有：Forbid。
        var nobody = CreateController(real, authenticated: true);
        Assert.IsType<ForbidResult>(await nobody.GetList("1001"));
        Assert.IsType<ForbidResult>(await nobody.GetProducts("1001", null, null));

        // 错误码 → 状态：重名 409、分类不存在 404、越权 403、校验 400。
        var created = Assert.IsType<OkObjectResult>(await manager.Create(new StoreSpecialCategorySaveRequestDto { StoreCode = "1001", Name = "Toys" }));
        var envelope = Assert.IsType<ApiResponse<StoreSpecialCategoryDto>>(created.Value);
        Assert.True(envelope.Success);
        Assert.Equal(409, Assert.IsType<ConflictObjectResult>(await manager.Create(new StoreSpecialCategorySaveRequestDto { StoreCode = "1001", Name = "toys" })).StatusCode);
        Assert.Equal(404, Assert.IsType<NotFoundObjectResult>(await manager.Delete("ghost", "1001")).StatusCode);
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(await manager.Create(new StoreSpecialCategorySaveRequestDto { StoreCode = "1001", Name = "" })).StatusCode);
        var limited = CreateController(CreateService(Scope(false, "1001")), authenticated: true, Permissions.PosTerminal.SpecialProducts.Manage);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await limited.GetList("1002")).StatusCode);
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(await manager.ExecuteSync(new StoreSpecialCategorySyncRequestDto
        {
            SourceStoreCode = "1001", TargetStoreCodes = new() { "1002" }, Scope = "categories", Mode = "replace", ConfirmReplace = false,
        })).StatusCode);
    }

    [Fact]
    public void 控制器_路由与权限特性_写接口和同步接口声明Manage策略_类上要求登录()
    {
        var type = typeof(StoreSpecialProductCategoriesController);
        Assert.Contains(type.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>(), a => a.Policy is null);
        var route = Assert.Single(type.GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>());
        Assert.Equal("api/react/v1/store-special-product-categories", route.Template);

        foreach (var method in new[]
                 {
                     nameof(StoreSpecialProductCategoriesController.Create),
                     nameof(StoreSpecialProductCategoriesController.Update),
                     nameof(StoreSpecialProductCategoriesController.Delete),
                     nameof(StoreSpecialProductCategoriesController.ReorderCategories),
                     nameof(StoreSpecialProductCategoriesController.Assign),
                     nameof(StoreSpecialProductCategoriesController.Unmark),
                     nameof(StoreSpecialProductCategoriesController.ReorderProducts),
                     nameof(StoreSpecialProductCategoriesController.GetSyncTargets),
                     nameof(StoreSpecialProductCategoriesController.PreviewSync),
                     nameof(StoreSpecialProductCategoriesController.ExecuteSync),
                 })
        {
            var policies = type.GetMethod(method)!.GetCustomAttributes(typeof(AuthorizeAttribute), true)
                .Cast<AuthorizeAttribute>().Select(a => a.Policy).ToArray();
            Assert.Contains(Permissions.PosTerminal.SpecialProducts.Manage, policies);
        }
    }
}
