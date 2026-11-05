using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Models.Planogram;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Planogram;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 商品陈列图批 1：货架 / 挂钩型号、商品陈列参数（SQLite 临时库）+ 权限登记、导航、控制器授权与 JSON 契约。
/// 契约依据 docs/plans/2026-11-04-planogram-shelf-display-designer.md 附录 A；商品与金额都是虚构的。
/// </summary>
public sealed class PlanogramTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
    private const string Actor = "alice";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-planogram-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly PlanogramModelService _models;
    private readonly ProductDisplaySpecService _specs;
    private readonly PlanogramPlanService _plans;

    public PlanogramTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(PlanogramShelfModel), typeof(PlanogramHookModel), typeof(ProductDisplaySpec),
            typeof(PlanogramHeader), typeof(PlanogramItem), typeof(PlanogramVersion),
            typeof(Product), typeof(ProductCategory), typeof(ProductGrade));
        _models = new PlanogramModelService(_db, new FixedTime(Now));
        _specs = new ProductDisplaySpecService(_db, new FixedTime(Now));
        _plans = new PlanogramPlanService(_db, new FixedTime(Now));
        SeedProducts();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private sealed class FixedTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    // ---------------------------------------------------------------- 数据

    /// <summary>P100/P200/P300 启用；P400 停用；P500 已软删；P600 无货号。</summary>
    private void SeedProducts()
    {
        _db.Insertable(new ProductCategory { CategoryGUID = "cat-1", CategoryName = "收纳用品" }).ExecuteCommand();
        _db.Insertable(new Product
        {
            ProductCode = "P100", ItemNumber = "IT-100", Barcode = "9300000000011", ProductName = "挂钩收纳盒",
            EnglishName = "Hook Box", RetailPrice = 4.5m, ProductImage = "/img/p100.jpg", ProductCategoryGUID = "cat-1",
        }).ExecuteCommand();
        _db.Insertable(new Product
        {
            ProductCode = "P200", ItemNumber = "IT-200", Barcode = "9300000000028", ProductName = "洗碗海绵 3 片装",
            EnglishName = "Sponge 3pk", RetailPrice = 2.99m,
        }).ExecuteCommand();
        _db.Insertable(new Product
        {
            ProductCode = "P300", ItemNumber = "IT-300", Barcode = "9300000000035", ProductName = "不织布收纳袋",
            RetailPrice = 6m,
        }).ExecuteCommand();
        _db.Insertable(new Product { ProductCode = "P400", ItemNumber = "IT-400", ProductName = "已停用商品", IsActive = false }).ExecuteCommand();
        _db.Insertable(new Product { ProductCode = "P500", ItemNumber = "IT-500", ProductName = "已删除商品", IsDeleted = true }).ExecuteCommand();
    }

    private static ShelfModelDto Shelf(string code = "G-S-PEG-90x30x120") => new()
    {
        Code = code, Name = "单面洞洞板 900×1200 · 白", Color = "白", Source = "Shop Fittings Store G",
        BayWidthMm = 900, TotalHeightMm = 1200, BaseDepthMm = 300, BaseTopHeightMm = 150, UprightWidthMm = 30,
        HolePitchXMm = 25, HolePitchYMm = 25, HoleDiameterMm = 6, FirstHoleLeftMm = 25, FirstHoleTopMm = 25,
    };

    private static HookModelDto Hook(string code = "HK-D-PT-200") => new()
    {
        Code = code, Name = "双钩带价签 200 · 黑", Color = "黑", ProngCount = 2, HasPriceTagArm = true,
        ArmLengthMm = 200, DropMm = 50, WireDiameterMm = 5, TipReserveMm = 20, TagWidthMm = 52, TagHeightMm = 26,
        HoleSpanX = 2, HoleSpanY = 1, LoadKg = 3,
    };

    private static ProductDisplaySpecDto Spec() => new()
    {
        WidthMm = 120, HeightMm = 180, DepthMm = 30, HangHoleOffsetMm = 10, HangHoleType = 1, UnitWeightG = 200,
    };

    private async Task<ShelfModelDto> CreateShelfAsync(string code = "G-S-PEG-90x30x120")
    {
        var created = await _models.CreateShelfModelAsync(Shelf(code), Actor, default);
        Assert.True(created.Success, created.Message);
        return created.Data!;
    }

    private async Task<HookModelDto> CreateHookAsync(string code = "HK-D-PT-200")
    {
        var created = await _models.CreateHookModelAsync(Hook(code), Actor, default);
        Assert.True(created.Success, created.Message);
        return created.Data!;
    }

    private void InsertPlan(string planGuid, string shelfGuid, params (string Product, string Hook)[] rows)
    {
        _db.Insertable(new PlanogramHeader { Guid = planGuid, Name = "试验方案", ShelfModelGuid = shelfGuid }).ExecuteCommand();
        var sort = 0;
        foreach (var (product, hook) in rows)
        {
            _db.Insertable(new PlanogramItem
            {
                PlanogramGuid = planGuid, ProductCode = product, HookModelGuid = hook, HoleCol = 1 + sort * 2, HoleRow = 1, SortOrder = sort++,
            }).ExecuteCommand();
        }
    }

    private static PlanogramFieldError[] FieldErrors(object? details) =>
        Assert.IsType<List<PlanogramFieldError>>(details).ToArray();

    // ---------------------------------------------------------------- 货架型号

    [Fact]
    public async Task 货架型号_创建_孔数缺省时按板面尺寸自动换算_并写入审计()
    {
        var created = await _models.CreateShelfModelAsync(Shelf(), Actor, default);

        Assert.True(created.Success, created.Message);
        var dto = created.Data!;
        Assert.False(string.IsNullOrWhiteSpace(dto.Guid));
        // 板面 870 × 1050，首孔边距 25，孔距 25：⌊(870−50)÷25⌋+1 = 33 列，⌊(1050−50)÷25⌋+1 = 41 行。
        Assert.Equal(33, dto.HoleCols);
        Assert.Equal(41, dto.HoleRows);
        Assert.True(dto.IsActive);
        Assert.Equal(1, dto.SideType);
        Assert.Equal(1, dto.RowNumberingFrom);
        Assert.Equal(0, dto.ReferencedPlanCount);
        Assert.Equal(Actor, dto.UpdatedBy);
        Assert.Equal(Now, dto.UpdatedAt);

        var row = await _db.Queryable<PlanogramShelfModel>().FirstAsync(model => model.Guid == dto.Guid);
        Assert.Equal(33, row.HoleCols);
        Assert.Equal(Actor, row.CreatedBy);
        Assert.Equal(Now, row.CreatedAt);
        Assert.False(row.IsDeleted);
    }

    [Theory]
    // 参数：板面尺寸、单侧首孔边距（公式里两侧各扣一次）、孔距、期望孔数。
    [InlineData(870, 25, 25, 33)] // (870−50)÷25 = 32.8，向下取整 + 1
    [InlineData(870, 25, 25.4, 33)] // 英寸孔距：32.28 → 32 + 1
    [InlineData(1050, 25, 25, 41)]
    [InlineData(50, 25, 25, 1)] // 两侧边距刚好占满板面：只放得下一个孔
    [InlineData(49, 25, 25, 0)] // 连一个孔都放不下 → 0（由校验拒绝）
    public void ComputeHoleCount_公式为向下取整加一(double board, double margin, double pitch, int expected)
    {
        Assert.Equal(expected, PlanogramModelService.ComputeHoleCount((decimal)board, (decimal)margin, (decimal)pitch));
    }

    [Fact]
    public async Task 货架型号_孔距允许英寸换算值_手填孔数优先于自动换算()
    {
        var input = Shelf();
        input.HolePitchXMm = 25.4m;
        input.HoleCols = 30; // 手填，不再自动换算
        var created = await _models.CreateShelfModelAsync(input, Actor, default);

        Assert.True(created.Success, created.Message);
        Assert.Equal(25.4m, created.Data!.HolePitchXMm);
        Assert.Equal(30, created.Data.HoleCols);
        Assert.Equal(41, created.Data.HoleRows); // 另一向仍自动换算
    }

    [Fact]
    public async Task 货架型号_换算不出至少一个孔时按字段报错()
    {
        var input = Shelf();
        input.FirstHoleLeftMm = 500; // 板面宽 870，首孔边距 500×2 > 870

        var result = await _models.CreateShelfModelAsync(input, Actor, default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.Validation, result.ErrorCode);
        Assert.Contains(FieldErrors(result.Details), error => error.Field == "holeCols");
        Assert.Empty(await _db.Queryable<PlanogramShelfModel>().ToListAsync());
    }

    [Fact]
    public async Task 货架型号_尺寸校验_一次返回全部不合法字段()
    {
        var input = Shelf("  ");
        input.Name = " ";
        input.BayWidthMm = 0;
        input.HolePitchXMm = 0.4m; // 孔距下限 0.5
        input.HolePitchYMm = 200.01m; // 上限 200
        input.HoleDiameterMm = 0;
        input.HoleRows = -1;
        input.SideType = 3;
        input.RowNumberingFrom = 0;

        var result = await _models.CreateShelfModelAsync(input, Actor, default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.Validation, result.ErrorCode);
        var fields = FieldErrors(result.Details).Select(error => error.Field).ToHashSet();
        foreach (var expected in new[]
                 {
                     "code", "name", "bayWidthMm", "holePitchXMm", "holePitchYMm", "holeDiameterMm", "holeRows", "sideType", "rowNumberingFrom",
                 })
        {
            Assert.Contains(expected, fields);
        }
    }

    [Fact]
    public async Task 货架型号_立柱宽不小于节宽或底板面不低于总高_被拒绝()
    {
        var wide = Shelf();
        wide.UprightWidthMm = 900;
        var tall = Shelf("G-2");
        tall.BaseTopHeightMm = 1200;

        var first = await _models.CreateShelfModelAsync(wide, Actor, default);
        var second = await _models.CreateShelfModelAsync(tall, Actor, default);

        Assert.Contains(FieldErrors(first.Details), error => error.Field == "uprightWidthMm");
        Assert.Contains(FieldErrors(second.Details), error => error.Field == "baseTopHeightMm");
    }

    [Fact]
    public async Task 货架型号_编码重复被拒_大小写不敏感_更新自身不算重复()
    {
        var first = await CreateShelfAsync("G-A");

        var duplicate = await _models.CreateShelfModelAsync(Shelf("g-a"), Actor, default);
        Assert.False(duplicate.Success);
        Assert.Equal(PlanogramErrorCodes.CodeDuplicate, duplicate.ErrorCode);
        Assert.Contains(FieldErrors(duplicate.Details), error => error.Field == "code");

        // 改自己（编码不变）可以；改成别人的编码不行。
        var second = await CreateShelfAsync("G-B");
        var renameSelf = Shelf("G-A");
        renameSelf.Name = "改名后";
        var ok = await _models.UpdateShelfModelAsync(first.Guid, renameSelf, Actor, default);
        Assert.True(ok.Success, ok.Message);
        Assert.Equal("改名后", ok.Data!.Name);

        var clash = await _models.UpdateShelfModelAsync(second.Guid, Shelf("G-A"), Actor, default);
        Assert.False(clash.Success);
        Assert.Equal(PlanogramErrorCodes.CodeDuplicate, clash.ErrorCode);
        Assert.Equal(2, (await _db.Queryable<PlanogramShelfModel>().ToListAsync()).Count);
    }

    [Fact]
    public async Task 货架型号_更新_未被引用时可以改尺寸_缺省孔数按新孔距重算()
    {
        var shelf = await CreateShelfAsync();
        var changed = Shelf();
        changed.HolePitchXMm = 50;

        var result = await _models.UpdateShelfModelAsync(shelf.Guid, changed, "bob", default);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, result.Data!.ReferencedPlanCount);
        Assert.Equal(17, result.Data.HoleCols); // ⌊(870−50)÷50⌋+1 = 17，缺省孔数按新孔距重算
        Assert.Equal("bob", result.Data.UpdatedBy);
    }

    [Fact]
    public async Task 货架型号_更新_被方案引用后外形和孔阵字段被锁定_停用改名称颜色不受限()
    {
        var shelf = await CreateShelfAsync();
        InsertPlan("plan-1", shelf.Guid);
        InsertPlan("plan-2", shelf.Guid);

        var changed = Shelf();
        changed.HolePitchXMm = 50;
        var locked = await _models.UpdateShelfModelAsync(shelf.Guid, changed, "bob", default);

        Assert.False(locked.Success);
        Assert.Equal(PlanogramErrorCodes.ModelLocked, locked.ErrorCode);
        Assert.Contains("复制为新型号", locked.Message);
        Assert.Contains("2 个陈列方案", locked.Message);
        // 被锁的字段在 details 里逐个列出，且只列真正变了的。
        Assert.Equal(new[] { "holeCols", "holePitchXMm" }, FieldErrors(locked.Details).Select(error => error.Field).OrderBy(name => name, StringComparer.Ordinal));
        var stored = await _db.Queryable<PlanogramShelfModel>().FirstAsync(model => model.Guid == shelf.Guid);
        Assert.Equal(25m, stored.HolePitchXMm);
        Assert.Equal(33, stored.HoleCols);

        // 提交与库里一致的外形 / 孔阵（孔数缺省 → 自动换算结果相同）不算改动，同时改名称 / 颜色 / 孔径 / 停用都允许。
        var allowed = Shelf();
        allowed.Name = "改名后的货架";
        allowed.Color = "灰";
        allowed.HoleDiameterMm = 8;
        allowed.IsActive = false;
        var ok = await _models.UpdateShelfModelAsync(shelf.Guid, allowed, "bob", default);
        Assert.True(ok.Success, ok.Message);
        Assert.Equal(2, ok.Data!.ReferencedPlanCount);
        Assert.Equal("改名后的货架", ok.Data.Name);
        Assert.False(ok.Data.IsActive);
        Assert.Equal("bob", ok.Data.UpdatedBy);
    }

    [Theory]
    [InlineData("bayWidthMm")]
    [InlineData("totalHeightMm")]
    [InlineData("uprightWidthMm")]
    [InlineData("baseTopHeightMm")]
    [InlineData("holePitchXMm")]
    [InlineData("holePitchYMm")]
    [InlineData("firstHoleLeftMm")]
    [InlineData("firstHoleTopMm")]
    [InlineData("holeCols")]
    [InlineData("holeRows")]
    public async Task 货架型号_被方案引用后_十个锁定字段逐个改动都被拒绝(string field)
    {
        var shelf = await CreateShelfAsync();
        InsertPlan("plan-1", shelf.Guid);
        var changed = Shelf();
        changed.HoleCols = 33;
        changed.HoleRows = 41; // 手填孔数 = 原值：避免「改尺寸连带孔数自动变」干扰单字段断言
        switch (field)
        {
            case "bayWidthMm": changed.BayWidthMm = 950; break;
            case "totalHeightMm": changed.TotalHeightMm = 1250; break;
            case "uprightWidthMm": changed.UprightWidthMm = 40; break;
            case "baseTopHeightMm": changed.BaseTopHeightMm = 160; break;
            case "holePitchXMm": changed.HolePitchXMm = 25.4m; break;
            case "holePitchYMm": changed.HolePitchYMm = 25.4m; break;
            case "firstHoleLeftMm": changed.FirstHoleLeftMm = 30; break;
            case "firstHoleTopMm": changed.FirstHoleTopMm = 30; break;
            case "holeCols": changed.HoleCols = 30; break;
            case "holeRows": changed.HoleRows = 40; break;
        }

        var result = await _models.UpdateShelfModelAsync(shelf.Guid, changed, Actor, default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.ModelLocked, result.ErrorCode);
        Assert.Contains(field, FieldErrors(result.Details).Select(error => error.Field));
    }

    [Fact]
    public async Task 货架型号_被方案引用后_孔径底板深行号方向不在锁定清单里()
    {
        var shelf = await CreateShelfAsync();
        InsertPlan("plan-1", shelf.Guid);
        var changed = Shelf();
        changed.BaseDepthMm = 350;
        changed.HoleDiameterMm = 8;
        changed.RowNumberingFrom = 2;

        var result = await _models.UpdateShelfModelAsync(shelf.Guid, changed, Actor, default);

        Assert.True(result.Success, result.Message);
        Assert.Equal(350, result.Data!.BaseDepthMm);
        Assert.Equal(2, result.Data.RowNumberingFrom);
    }

    [Fact]
    public async Task 货架型号_更新或删除不存在的型号_返回NOT_FOUND()
    {
        var update = await _models.UpdateShelfModelAsync("missing", Shelf(), Actor, default);
        var delete = await _models.DeleteShelfModelAsync("missing", default);

        Assert.Equal(PlanogramErrorCodes.NotFound, update.ErrorCode);
        Assert.Equal(PlanogramErrorCodes.NotFound, delete.ErrorCode);
    }

    [Fact]
    public async Task 货架型号_被方案引用时不能删除_改为停用后仍引用_未引用的可删除()
    {
        var used = await CreateShelfAsync("G-USED");
        var spare = await CreateShelfAsync("G-SPARE");
        InsertPlan("plan-1", used.Guid);
        // 已归档的方案也算引用：归档可以恢复。
        _db.Updateable<PlanogramHeader>().SetColumns(plan => plan.Status == (byte)2).Where(plan => plan.Guid == "plan-1").ExecuteCommand();

        var blocked = await _models.DeleteShelfModelAsync(used.Guid, default);
        Assert.False(blocked.Success);
        Assert.Equal(PlanogramErrorCodes.ModelInUse, blocked.ErrorCode);
        Assert.Contains("停用", blocked.Message);
        Assert.NotNull(await _db.Queryable<PlanogramShelfModel>().FirstAsync(model => model.Guid == used.Guid));

        // 提示的出路：改为停用。停用后列表上能看到引用数，依旧不能删。
        var deactivate = Shelf("G-USED");
        deactivate.IsActive = false;
        var disabled = await _models.UpdateShelfModelAsync(used.Guid, deactivate, Actor, default);
        Assert.True(disabled.Success, disabled.Message);
        Assert.False(disabled.Data!.IsActive);
        Assert.Equal(1, disabled.Data.ReferencedPlanCount);
        Assert.Equal(PlanogramErrorCodes.ModelInUse, (await _models.DeleteShelfModelAsync(used.Guid, default)).ErrorCode);

        var removed = await _models.DeleteShelfModelAsync(spare.Guid, default);
        Assert.True(removed.Success, removed.Message);
        Assert.Null(await _db.Queryable<PlanogramShelfModel>().FirstAsync(model => model.Guid == spare.Guid));
    }

    [Fact]
    public async Task 货架型号_列表_关键字_状态过滤_分页_引用数()
    {
        var a = await CreateShelfAsync("G-A-900");
        await CreateShelfAsync("G-B-1200");
        var off = Shelf("G-C-OFF");
        off.IsActive = false;
        off.Name = "停用的板";
        Assert.True((await _models.CreateShelfModelAsync(off, Actor, default)).Success);
        InsertPlan("plan-1", a.Guid);

        var all = await _models.ListShelfModelsAsync(null, null, 1, 20, default);
        Assert.Equal(3, all.Data!.Total);
        Assert.Equal(new[] { "G-A-900", "G-B-1200", "G-C-OFF" }, all.Data.Items!.Select(item => item.Code));
        Assert.Equal(1, all.Data.Items!.Single(item => item.Code == "G-A-900").ReferencedPlanCount);

        var active = await _models.ListShelfModelsAsync(null, true, 1, 20, default);
        Assert.Equal(2, active.Data!.Total);
        var inactive = await _models.ListShelfModelsAsync(null, false, 1, 20, default);
        Assert.Equal("G-C-OFF", Assert.Single(inactive.Data!.Items!).Code);

        var byName = await _models.ListShelfModelsAsync("停用的", null, 1, 20, default);
        Assert.Equal("G-C-OFF", Assert.Single(byName.Data!.Items!).Code);
        var byCode = await _models.ListShelfModelsAsync("b-1200", null, 1, 20, default);
        Assert.Equal(1, byCode.Data!.Total);

        var paged = await _models.ListShelfModelsAsync(null, null, 2, 2, default);
        Assert.Equal(3, paged.Data!.Total);
        Assert.Equal(2, paged.Data.Page);
        Assert.Equal(2, paged.Data.PageSize);
        Assert.Equal("G-C-OFF", Assert.Single(paged.Data.Items!).Code);
    }

    // ---------------------------------------------------------------- 挂钩型号

    [Fact]
    public async Task 挂钩型号_创建_价签底边高度缺省为10_并保存全部字段()
    {
        var input = Hook();
        input.TagBottomAboveArmMm = 10;
        var created = await _models.CreateHookModelAsync(input, Actor, default);

        Assert.True(created.Success, created.Message);
        var dto = created.Data!;
        Assert.Equal(10m, dto.TagBottomAboveArmMm);
        Assert.Equal(200m, dto.ArmLengthMm);
        Assert.Equal(2, dto.HoleSpanX);
        Assert.Equal(3m, dto.LoadKg);
        // 不带 TagBottomAboveArmMm 的请求体（DTO 默认值）同样得到 10。
        Assert.Equal(10m, new HookModelDto().TagBottomAboveArmMm);
        var row = await _db.Queryable<PlanogramHookModel>().FirstAsync(model => model.Guid == dto.Guid);
        Assert.Equal(Actor, row.CreatedBy);
    }

    [Fact]
    public async Task 挂钩型号_不带价签臂时价签字段可为0_带价签臂时必须大于0()
    {
        var plain = Hook("HK-S-PLAIN");
        plain.HasPriceTagArm = false;
        plain.TagWidthMm = 0;
        plain.TagHeightMm = 0;
        plain.TagBottomAboveArmMm = 0;
        var tagged = Hook("HK-S-TAG");
        tagged.TagWidthMm = 0;

        var ok = await _models.CreateHookModelAsync(plain, Actor, default);
        var bad = await _models.CreateHookModelAsync(tagged, Actor, default);

        Assert.True(ok.Success, ok.Message);
        Assert.Equal(0m, ok.Data!.TagWidthMm);
        Assert.False(bad.Success);
        Assert.Equal(PlanogramErrorCodes.Validation, bad.ErrorCode);
        Assert.Contains(FieldErrors(bad.Details), error => error.Field == "tagWidthMm");
    }

    [Fact]
    public async Task 挂钩型号_尺寸校验_臂端预留须小于臂长()
    {
        var input = Hook();
        input.ProngCount = 3;
        input.ArmLengthMm = 200;
        input.TipReserveMm = 200;
        input.DropMm = 0;
        input.HoleSpanX = 0;
        input.LoadKg = 0;

        var result = await _models.CreateHookModelAsync(input, Actor, default);

        Assert.False(result.Success);
        var fields = FieldErrors(result.Details).Select(error => error.Field).ToHashSet();
        foreach (var expected in new[] { "prongCount", "tipReserveMm", "dropMm", "holeSpanX", "loadKg" })
        {
            Assert.Contains(expected, fields);
        }
    }

    [Fact]
    public async Task 挂钩型号_编码重复被拒_货架与挂钩编码互不影响()
    {
        await CreateHookAsync("SAME-CODE");
        await CreateShelfAsync("SAME-CODE"); // 两类型号各自内唯一

        var duplicate = await _models.CreateHookModelAsync(Hook("same-code"), Actor, default);

        Assert.False(duplicate.Success);
        Assert.Equal(PlanogramErrorCodes.CodeDuplicate, duplicate.ErrorCode);
    }

    [Fact]
    public async Task 挂钩型号_被方案明细引用时不能删除_同一方案多行只计一次()
    {
        var shelf = await CreateShelfAsync();
        var hook = await CreateHookAsync();
        InsertPlan("plan-1", shelf.Guid, ("P100", hook.Guid), ("P200", hook.Guid)); // 同一方案两行
        InsertPlan("plan-2", shelf.Guid, ("P100", hook.Guid));

        var list = await _models.ListHookModelsAsync(null, null, 1, 20, default);
        Assert.Equal(2, Assert.Single(list.Data!.Items!).ReferencedPlanCount);

        var blocked = await _models.DeleteHookModelAsync(hook.Guid, default);
        Assert.False(blocked.Success);
        Assert.Equal(PlanogramErrorCodes.ModelInUse, blocked.ErrorCode);

        var deactivate = Hook();
        deactivate.IsActive = false;
        var disabled = await _models.UpdateHookModelAsync(hook.Guid, deactivate, Actor, default);
        Assert.True(disabled.Success, disabled.Message);
        Assert.False(disabled.Data!.IsActive);
        Assert.Equal(2, disabled.Data.ReferencedPlanCount);
    }

    [Fact]
    public async Task 挂钩型号_被商品参数的默认挂钩或挂数覆盖引用时不能删除()
    {
        var byDefault = await CreateHookAsync("HK-DEFAULT");
        var byOverride = await CreateHookAsync("HK-OVERRIDE");
        var free = await CreateHookAsync("HK-FREE");
        var spec = Spec();
        spec.DefaultHookGuid = byDefault.Guid;
        spec.HookQtyOverrides = new Dictionary<string, int> { [byOverride.Guid] = 8 };
        Assert.True((await _specs.SaveAsync("P100", spec, Actor, default)).Success);

        Assert.Equal(PlanogramErrorCodes.ModelInUse, (await _models.DeleteHookModelAsync(byDefault.Guid, default)).ErrorCode);
        Assert.Equal(PlanogramErrorCodes.ModelInUse, (await _models.DeleteHookModelAsync(byOverride.Guid, default)).ErrorCode);
        Assert.True((await _models.DeleteHookModelAsync(free.Guid, default)).Success);

        // 清掉商品参数后引用消失，可以删除。
        Assert.True((await _specs.DeleteAsync("P100", default)).Success);
        Assert.True((await _models.DeleteHookModelAsync(byDefault.Guid, default)).Success);
    }

    [Fact]
    public async Task 挂钩型号_更新与列表过滤()
    {
        var a = await CreateHookAsync("HK-A");
        await CreateHookAsync("HK-B");
        var change = Hook("HK-A");
        change.ArmLengthMm = 203.2m; // 8 英寸
        change.IsActive = false;

        var updated = await _models.UpdateHookModelAsync(a.Guid, change, "bob", default);

        Assert.True(updated.Success, updated.Message);
        Assert.Equal(203.2m, updated.Data!.ArmLengthMm);
        Assert.Equal("bob", updated.Data.UpdatedBy);
        var active = await _models.ListHookModelsAsync(null, true, 1, 20, default);
        Assert.Equal("HK-B", Assert.Single(active.Data!.Items!).Code);
        var byKeyword = await _models.ListHookModelsAsync("hk-a", null, 1, 20, default);
        Assert.Equal(1, byKeyword.Data!.Total);
        Assert.Equal(PlanogramErrorCodes.NotFound, (await _models.UpdateHookModelAsync("missing", Hook(), Actor, default)).ErrorCode);
        Assert.Equal(PlanogramErrorCodes.NotFound, (await _models.DeleteHookModelAsync("missing", default)).ErrorCode);
    }

    // ---------------------------------------------------------------- 商品陈列参数

    [Fact]
    public async Task 商品参数_列表只含启用且未软删的商品_按状态过滤()
    {
        Assert.True((await _specs.SaveAsync("P100", Spec(), Actor, default)).Success);

        var all = await _specs.ListAsync(null, null, 1, 20, default);
        Assert.Equal(new[] { "P100", "P200", "P300" }, all.Data!.Items!.Select(item => item.ProductCode));
        Assert.Equal(3, all.Data.Total);

        var set = await _specs.ListAsync(null, "set", 1, 20, default);
        Assert.Equal("P100", Assert.Single(set.Data!.Items!).ProductCode);
        Assert.True(set.Data.Items![0].HasSpec);

        var unset = await _specs.ListAsync(null, "UNSET", 1, 20, default);
        Assert.Equal(new[] { "P200", "P300" }, unset.Data!.Items!.Select(item => item.ProductCode));
        Assert.All(unset.Data.Items!, item =>
        {
            Assert.False(item.HasSpec);
            Assert.Null(item.Spec);
        });
        Assert.Equal(2, unset.Data.Total);

        var explicitAll = await _specs.ListAsync(null, "all", 1, 20, default);
        Assert.Equal(3, explicitAll.Data!.Total);
    }

    [Fact]
    public async Task 商品参数_列表行联商品主档_返回图片名称零售价分类和参数()
    {
        var hook = await CreateHookAsync();
        var spec = Spec();
        spec.DefaultHookGuid = hook.Guid;
        spec.HookQtyOverrides = new Dictionary<string, int> { [hook.Guid] = 6 };
        spec.Notes = "供应商给的尺寸";
        spec.Source = 2;
        Assert.True((await _specs.SaveAsync("P100", spec, "bob", default)).Success);

        var list = await _specs.ListAsync(null, "set", 1, 20, default);

        var row = Assert.Single(list.Data!.Items!);
        Assert.Equal("P100", row.ProductCode);
        Assert.Equal("IT-100", row.ItemNumber);
        Assert.Equal("9300000000011", row.Barcode);
        Assert.Equal("挂钩收纳盒", row.ProductName);
        Assert.Equal("Hook Box", row.EnglishName);
        Assert.Equal("/img/p100.jpg", row.ProductImage);
        Assert.Equal(4.5m, row.RetailPrice);
        Assert.Equal("收纳用品", row.CategoryName);
        Assert.Equal(0, row.ReferencedPlanCount);
        Assert.Equal("bob", row.UpdatedBy);
        Assert.Equal(Now, row.UpdatedAt);
        var saved = row.Spec!;
        Assert.Equal(120, saved.WidthMm);
        Assert.Equal(180, saved.HeightMm);
        Assert.Equal(30, saved.DepthMm);
        Assert.Equal(10, saved.HangHoleOffsetMm);
        Assert.Equal(1, saved.HangHoleType);
        Assert.Equal(200, saved.UnitWeightG);
        Assert.Equal(hook.Guid, saved.DefaultHookGuid);
        Assert.Equal(6, saved.HookQtyOverrides[hook.Guid]);
        Assert.Equal(2, saved.Source);
        Assert.Equal("供应商给的尺寸", saved.Notes);
    }

    [Fact]
    public async Task 商品参数_关键字匹配货号条码名称英文名_分页()
    {
        Assert.Equal("P100", Assert.Single((await _specs.ListAsync("IT-100", null, 1, 20, default)).Data!.Items!).ProductCode);
        Assert.Equal("P200", Assert.Single((await _specs.ListAsync("9300000000028", null, 1, 20, default)).Data!.Items!).ProductCode);
        Assert.Equal("P200", Assert.Single((await _specs.ListAsync("海绵", null, 1, 20, default)).Data!.Items!).ProductCode);
        Assert.Equal("P100", Assert.Single((await _specs.ListAsync("Hook Box", null, 1, 20, default)).Data!.Items!).ProductCode);
        Assert.Equal("P300", Assert.Single((await _specs.ListAsync("P300", null, 1, 20, default)).Data!.Items!).ProductCode);
        // 停用 / 软删的商品搜不到。
        Assert.Empty((await _specs.ListAsync("已停用", null, 1, 20, default)).Data!.Items!);
        Assert.Empty((await _specs.ListAsync("已删除", null, 1, 20, default)).Data!.Items!);

        var page2 = await _specs.ListAsync(null, null, 2, 2, default);
        Assert.Equal(3, page2.Data!.Total);
        Assert.Equal("P300", Assert.Single(page2.Data.Items!).ProductCode);
    }

    [Fact]
    public async Task 商品参数_状态参数不合法_返回校验错误()
    {
        var result = await _specs.ListAsync(null, "weird", 1, 20, default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.Validation, result.ErrorCode);
        Assert.Contains(FieldErrors(result.Details), error => error.Field == "status");
    }

    [Fact]
    public async Task 商品参数_读取单个商品_无参数时hasSpec为false_停用或软删商品为NOT_FOUND()
    {
        var unset = await _specs.GetAsync("P200", default);
        Assert.True(unset.Success);
        Assert.False(unset.Data!.HasSpec);
        Assert.Null(unset.Data.Spec);
        Assert.Equal("洗碗海绵 3 片装", unset.Data.ProductName);
        Assert.Null(unset.Data.CategoryName);

        foreach (var code in new[] { "P400", "P500", "NOPE", " " })
        {
            Assert.Equal(PlanogramErrorCodes.NotFound, (await _specs.GetAsync(code, default)).ErrorCode);
        }
    }

    [Fact]
    public async Task 商品参数_保存_新增后再保存是覆盖不是新增_创建人保持不变()
    {
        var first = await _specs.SaveAsync("P100", Spec(), "alice", default);
        Assert.True(first.Success, first.Message);
        Assert.True(first.Data!.HasSpec);

        var changed = Spec();
        changed.WidthMm = 150;
        changed.Notes = "  改过  ";
        var second = await _specs.SaveAsync("P100", changed, "bob", default);

        Assert.True(second.Success, second.Message);
        Assert.Equal(150, second.Data!.Spec!.WidthMm);
        Assert.Equal("改过", second.Data.Spec.Notes);
        Assert.Equal("bob", second.Data.UpdatedBy);
        var rows = await _db.Queryable<ProductDisplaySpec>().ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("alice", row.CreatedBy);
        Assert.Equal("bob", row.UpdatedBy);
    }

    [Fact]
    public async Task 商品参数_保存时以路径货号为准_请求体里的货号被忽略()
    {
        var input = Spec();
        input.ProductCode = "P300";

        var result = await _specs.SaveAsync("P100", input, Actor, default);

        Assert.True(result.Success, result.Message);
        Assert.Equal("P100", result.Data!.ProductCode);
        Assert.Equal("P100", Assert.Single(await _db.Queryable<ProductDisplaySpec>().ToListAsync()).ProductCode);
    }

    [Fact]
    public async Task 商品参数_保存校验_宽高厚大于0_吊孔偏移_单件重_枚举值()
    {
        var input = Spec();
        input.WidthMm = 0;
        input.HeightMm = -5;
        input.DepthMm = 0;
        input.HangHoleOffsetMm = -1;
        input.UnitWeightG = 0;
        input.HangHoleType = 5;
        input.Source = 9;
        input.Notes = new string('备', 501);

        var result = await _specs.SaveAsync("P100", input, Actor, default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.Validation, result.ErrorCode);
        var fields = FieldErrors(result.Details).Select(error => error.Field).ToHashSet();
        foreach (var expected in new[]
                 {
                     "widthMm", "heightMm", "depthMm", "hangHoleOffsetMm", "unitWeightG", "hangHoleType", "source", "notes",
                 })
        {
            Assert.Contains(expected, fields);
        }

        Assert.Empty(await _db.Queryable<ProductDisplaySpec>().ToListAsync());
    }

    [Fact]
    public async Task 商品参数_吊孔偏移不能大于包装高_无吊孔时不限制()
    {
        var tooLow = Spec();
        tooLow.HangHoleOffsetMm = 200; // 包装高 180
        var noHole = Spec();
        noHole.HangHoleType = 0;
        noHole.HangHoleOffsetMm = 200;

        var bad = await _specs.SaveAsync("P100", tooLow, Actor, default);
        var ok = await _specs.SaveAsync("P100", noHole, Actor, default);

        Assert.Contains(FieldErrors(bad.Details), error => error.Field == "hangHoleOffsetMm");
        Assert.True(ok.Success, ok.Message);
    }

    [Fact]
    public async Task 商品参数_覆盖挂数必须大于等于1且挂钩型号必须存在_默认挂钩同样必须存在()
    {
        var hook = await CreateHookAsync();

        var zero = Spec();
        zero.HookQtyOverrides = new Dictionary<string, int> { [hook.Guid] = 0 };
        var unknownHook = Spec();
        unknownHook.HookQtyOverrides = new Dictionary<string, int> { ["no-such-hook"] = 5 };
        var unknownDefault = Spec();
        unknownDefault.DefaultHookGuid = "no-such-hook";

        var zeroResult = await _specs.SaveAsync("P100", zero, Actor, default);
        var unknownHookResult = await _specs.SaveAsync("P100", unknownHook, Actor, default);
        var unknownDefaultResult = await _specs.SaveAsync("P100", unknownDefault, Actor, default);

        Assert.Equal(PlanogramErrorCodes.Validation, zeroResult.ErrorCode);
        Assert.Contains(FieldErrors(zeroResult.Details), error => error.Field == "hookQtyOverrides");
        Assert.Equal(PlanogramErrorCodes.Validation, unknownHookResult.ErrorCode);
        Assert.Contains(FieldErrors(unknownHookResult.Details), error => error.Field == "hookQtyOverrides");
        Assert.Equal(PlanogramErrorCodes.Validation, unknownDefaultResult.ErrorCode);
        Assert.Contains(FieldErrors(unknownDefaultResult.Details), error => error.Field == "defaultHookGuid");
        Assert.Empty(await _db.Queryable<ProductDisplaySpec>().ToListAsync());
    }

    [Fact]
    public async Task 商品参数_停用的挂钩型号仍可作为覆盖和默认挂钩保存()
    {
        var hook = await CreateHookAsync();
        var off = Hook();
        off.IsActive = false;
        Assert.True((await _models.UpdateHookModelAsync(hook.Guid, off, Actor, default)).Success);
        var input = Spec();
        input.DefaultHookGuid = hook.Guid;
        input.HookQtyOverrides = new Dictionary<string, int> { [hook.Guid] = 4 };

        var result = await _specs.SaveAsync("P100", input, Actor, default);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task 商品参数_挂数覆盖按挂钩Guid排序后存JSON_空覆盖存null_脏JSON读作无覆盖()
    {
        var hookB = await CreateHookAsync("HK-B");
        var hookA = await CreateHookAsync("HK-A");
        var input = Spec();
        input.HookQtyOverrides = new Dictionary<string, int> { [hookB.Guid] = 8, [hookA.Guid] = 12 };
        Assert.True((await _specs.SaveAsync("P100", input, Actor, default)).Success);

        var stored = (await _db.Queryable<ProductDisplaySpec>().FirstAsync(spec => spec.ProductCode == "P100")).HookQtyOverridesJson!;
        var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(stored)!;
        Assert.Equal(parsed.Keys.OrderBy(key => key, StringComparer.Ordinal), parsed.Keys);
        Assert.Equal(12, parsed[hookA.Guid]);
        Assert.Equal(8, parsed[hookB.Guid]);

        // 再保存成空覆盖：库里存 null，接口返回空字典。
        input.HookQtyOverrides = new Dictionary<string, int>();
        var cleared = await _specs.SaveAsync("P100", input, Actor, default);
        Assert.Empty(cleared.Data!.Spec!.HookQtyOverrides);
        Assert.Null((await _db.Queryable<ProductDisplaySpec>().FirstAsync(spec => spec.ProductCode == "P100")).HookQtyOverridesJson);

        Assert.Empty(ProductDisplaySpecService.ParseOverrides("{not json"));
        Assert.Empty(ProductDisplaySpecService.ParseOverrides(null));
        Assert.Empty(ProductDisplaySpecService.ParseOverrides("  "));
    }

    [Fact]
    public async Task 商品参数_挂数覆盖超出1000字符上限_被拒绝而不是截断()
    {
        var input = Spec();
        var overrides = new Dictionary<string, int>();
        for (var index = 0; index < 30; index++)
        {
            var hook = await CreateHookAsync($"HK-{index:00}");
            overrides[hook.Guid] = 5;
        }

        input.HookQtyOverrides = overrides;
        var result = await _specs.SaveAsync("P100", input, Actor, default);

        Assert.False(result.Success);
        Assert.Contains(FieldErrors(result.Details), error => error.Field == "hookQtyOverrides");
    }

    [Fact]
    public async Task 商品参数_保存不存在_停用_软删的商品为NOT_FOUND()
    {
        foreach (var code in new[] { "NOPE", "P400", "P500" })
        {
            var result = await _specs.SaveAsync(code, Spec(), Actor, default);
            Assert.Equal(PlanogramErrorCodes.NotFound, result.ErrorCode);
        }

        Assert.Empty(await _db.Queryable<ProductDisplaySpec>().ToListAsync());
    }

    [Fact]
    public async Task 商品参数_清除后回到未设置_再清除幂等成功_商品不存在才NOT_FOUND()
    {
        Assert.True((await _specs.SaveAsync("P100", Spec(), Actor, default)).Success);

        var cleared = await _specs.DeleteAsync("P100", default);
        Assert.True(cleared.Success, cleared.Message);
        Assert.False((await _specs.GetAsync("P100", default)).Data!.HasSpec);
        Assert.True((await _specs.DeleteAsync("P100", default)).Success);
        Assert.Equal(PlanogramErrorCodes.NotFound, (await _specs.DeleteAsync("NOPE", default)).ErrorCode);
        // 商品主档不动。
        Assert.NotNull(await _db.Queryable<Product>().FirstAsync(product => product.ProductCode == "P100"));
    }

    [Fact]
    public async Task 商品参数_被方案引用数按方案去重_来自明细表()
    {
        var shelf = await CreateShelfAsync();
        var hook = await CreateHookAsync();
        Assert.True((await _specs.SaveAsync("P100", Spec(), Actor, default)).Success);
        InsertPlan("plan-1", shelf.Guid, ("P100", hook.Guid), ("P100", hook.Guid)); // 同一方案两行
        InsertPlan("plan-2", shelf.Guid, ("P100", hook.Guid), ("P200", hook.Guid));

        var list = await _specs.ListAsync(null, null, 1, 20, default);

        Assert.Equal(2, list.Data!.Items!.Single(item => item.ProductCode == "P100").ReferencedPlanCount);
        Assert.Equal(1, list.Data.Items!.Single(item => item.ProductCode == "P200").ReferencedPlanCount);
        Assert.Equal(0, list.Data.Items!.Single(item => item.ProductCode == "P300").ReferencedPlanCount);
        Assert.Equal(2, (await _specs.GetAsync("P100", default)).Data!.ReferencedPlanCount);
    }

    // ---------------------------------------------------------------- 权限 / 导航 / 控制器

    [Fact]
    public void 权限别名_Manage满足Edit和View_Edit满足View_反向不成立()
    {
        var manage = Permissions.ExpandPermissionCodes(new[] { Permissions.Planogram.Manage });
        var edit = Permissions.ExpandPermissionCodes(new[] { Permissions.Planogram.Edit });
        var view = Permissions.ExpandPermissionCodes(new[] { Permissions.Planogram.View });

        Assert.Contains(Permissions.Planogram.View, manage);
        Assert.Contains(Permissions.Planogram.Edit, manage);
        Assert.Contains(Permissions.Planogram.Manage, manage);
        Assert.Contains(Permissions.Planogram.View, edit);
        Assert.DoesNotContain(Permissions.Planogram.Manage, edit);
        Assert.Equal(new[] { Permissions.Planogram.View }, view.ToArray());

        var equivalent = Permissions.GetEquivalentPermissionCodes(Permissions.Planogram.View);
        Assert.Contains(Permissions.Planogram.Edit, equivalent);
        Assert.Contains(Permissions.Planogram.Manage, equivalent);
        Assert.Equal(
            new[] { Permissions.Planogram.Edit, Permissions.Planogram.Manage },
            Permissions.GetPermissionAliases()[Permissions.Planogram.View].OrderBy(code => code, StringComparer.Ordinal));
        Assert.Equal(new[] { Permissions.Planogram.Manage }, Permissions.GetPermissionAliases()[Permissions.Planogram.Edit]);
        Assert.False(Permissions.GetPermissionAliases().ContainsKey(Permissions.Planogram.Manage));
    }

    [Fact]
    public void 权限码_三个码已登记种子_使用端为Web_不写入任何角色模板_名称与迁移一致()
    {
        var codes = new[] { Permissions.Planogram.View, Permissions.Planogram.Edit, Permissions.Planogram.Manage };
        Assert.Equal(new[] { "Planogram.View", "Planogram.Edit", "Planogram.Manage" }, codes);

        foreach (var code in codes)
        {
            var seed = Assert.Single(PermissionSeedData.AllPermissions, item => item.Code == code);
            Assert.Equal("商品陈列图", seed.Category);
            Assert.False(string.IsNullOrWhiteSpace(seed.Name));
            Assert.Contains("/pos-admin/planograms", seed.Description);
            Assert.True(PermissionSurfaceCatalog.IsRegistered(code));
            Assert.Equal(new[] { "web" }, PermissionSurfaceCatalog.ResolveNames(code));
            // 默认不授予任何角色（含店长 / 仓库经理）：生产由管理员手动授权。
            Assert.DoesNotContain(
                PermissionSeedData.RolePermissionTemplates,
                template => template.PermissionCodes.Contains(code, StringComparer.OrdinalIgnoreCase));
            // 迁移 ApplySql 里的中文名称与描述必须与种子逐字一致，否则种子与生产库会各说各话。
            Assert.Contains($"N'{seed.Code}', N'{seed.Name}'", PlanogramSchema.ApplySql, StringComparison.Ordinal);
            Assert.Contains($"N'{seed.Description}'", PlanogramSchema.ApplySql, StringComparison.Ordinal);
        }

        Assert.Contains("N'商品陈列图'", PlanogramSchema.ApplySql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Permissions.Planogram.View)]
    [InlineData(Permissions.Planogram.Edit)]
    [InlineData(Permissions.Planogram.Manage)]
    public void 导航_三个权限任一都能进后台并点亮商品与价格合并页(string permission)
    {
        var service = new NavigationService();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("permission", permission) }, "TestAuth"));

        var menu = service.BuildMenu(user);

        var posAdmin = Assert.Single(menu, item => item.Path == "/pos-admin");
        var group = Assert.Single(posAdmin.Children!, item => item.Path == "/pos-admin/products-pricing");
        Assert.Equal("menu.groupProductsPricing", group.TitleKey);
        Assert.Contains(Permissions.Planogram.View, group.AnyPermissions!);
        // 只有陈列图权限的账号看不到数据看板菜单。
        Assert.DoesNotContain(menu, item => item.Path == "/dashboard");
    }

    [Fact]
    public void 导航_没有陈列图权限时商品与价格合并页不因它出现()
    {
        var service = new NavigationService();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("permission", Permissions.Dashboard.View) }, "TestAuth"));

        var menu = service.BuildMenu(user);

        var posAdmin = menu.SingleOrDefault(item => item.Path == "/pos-admin");
        Assert.True(posAdmin?.Children is null || posAdmin.Children.All(item => item.Path != "/pos-admin/products-pricing"));
    }

    public static IEnumerable<object[]> ControllerContract()
    {
        // 方法名, HTTP 方法, 路由模板, 权限策略（附录 A）
        yield return new object[] { nameof(PlanogramController.ListShelfModels), "GET", "shelf-models", Permissions.Planogram.View };
        yield return new object[] { nameof(PlanogramController.CreateShelfModel), "POST", "shelf-models", Permissions.Planogram.Manage };
        yield return new object[] { nameof(PlanogramController.UpdateShelfModel), "PUT", "shelf-models/{guid}", Permissions.Planogram.Manage };
        yield return new object[] { nameof(PlanogramController.DeleteShelfModel), "DELETE", "shelf-models/{guid}", Permissions.Planogram.Manage };
        yield return new object[] { nameof(PlanogramController.ListHookModels), "GET", "hook-models", Permissions.Planogram.View };
        yield return new object[] { nameof(PlanogramController.CreateHookModel), "POST", "hook-models", Permissions.Planogram.Manage };
        yield return new object[] { nameof(PlanogramController.UpdateHookModel), "PUT", "hook-models/{guid}", Permissions.Planogram.Manage };
        yield return new object[] { nameof(PlanogramController.DeleteHookModel), "DELETE", "hook-models/{guid}", Permissions.Planogram.Manage };
        yield return new object[] { nameof(PlanogramController.ListProductSpecs), "GET", "product-specs", Permissions.Planogram.View };
        yield return new object[] { nameof(PlanogramController.GetProductSpec), "GET", "product-specs/{productCode}", Permissions.Planogram.View };
        yield return new object[] { nameof(PlanogramController.SaveProductSpec), "PUT", "product-specs/{productCode}", Permissions.Planogram.Edit };
        yield return new object[] { nameof(PlanogramController.DeleteProductSpec), "DELETE", "product-specs/{productCode}", Permissions.Planogram.Manage };
    }

    [Theory]
    [MemberData(nameof(ControllerContract))]
    public void 控制器_路由_HTTP方法_权限策略与附录A一致(string methodName, string verb, string template, string policy)
    {
        var controller = typeof(PlanogramController);
        Assert.Equal("api/react/v1/planogram", controller.GetCustomAttribute<RouteAttribute>()!.Template);

        var method = controller.GetMethod(methodName)!;
        var http = Assert.Single(method.GetCustomAttributes<HttpMethodAttribute>(inherit: false));
        Assert.Equal(new[] { verb }, http.HttpMethods);
        Assert.Equal(template, http.Template);
        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        Assert.Equal(policy, authorize.Policy);
        Assert.Null(authorize.Roles);

        // 控制器上不挂类级角色门禁；所有公开动作都必须声明权限，不能漏。
        Assert.All(
            controller.GetCustomAttributes<AuthorizeAttribute>(inherit: false),
            attribute => Assert.True(string.IsNullOrEmpty(attribute.Roles)));
    }

    [Fact]
    public void 控制器_所有公开动作都在附录A和B和C和D清单里且都带权限策略()
    {
        var declared = typeof(PlanogramController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: false).Any())
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var contract = ControllerContract().Concat(PlanogramPlanTests.ControllerContract()).Concat(PlanogramPublishTests.ControllerContract())
            .Concat(PlanogramBatchTests.ControllerContract())
            .Select(row => (string)row[0]).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(contract, declared);
    }

    [Fact]
    public async Task 控制器_成功返回200_失败返回400并保留业务错误码_用户名写入审计()
    {
        var controller = new PlanogramController(_models, _specs, _plans)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.Name, "carol") }, "TestAuth")),
                },
            },
        };

        var created = await controller.CreateShelfModel(Shelf(), default);
        var ok = Assert.IsType<OkObjectResult>(created);
        var body = Assert.IsType<ApiResponse<ShelfModelDto>>(ok.Value);
        Assert.Equal("carol", body.Data!.UpdatedBy);

        var duplicate = await controller.CreateShelfModel(Shelf(), default);
        var bad = Assert.IsType<BadRequestObjectResult>(duplicate);
        Assert.Equal(PlanogramErrorCodes.CodeDuplicate, Assert.IsType<ApiResponse<ShelfModelDto>>(bad.Value).ErrorCode);

        // 请求体缺失（null）按空对象处理，得到校验错误而不是 500。
        var empty = await controller.CreateHookModel(null, default);
        var emptyBad = Assert.IsType<BadRequestObjectResult>(empty);
        Assert.Equal(PlanogramErrorCodes.Validation, Assert.IsType<ApiResponse<HookModelDto>>(emptyBad.Value).ErrorCode);

        var listed = await controller.ListShelfModels(null, null, 1, 20, default);
        var page = Assert.IsType<ApiResponse<PagedResult<ShelfModelDto>>>(Assert.IsType<OkObjectResult>(listed).Value);
        Assert.Equal(1, page.Data!.Total);
    }

    // ---------------------------------------------------------------- JSON 契约

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static string[] JsonKeys(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, WebJson)).RootElement
            .EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private static string[] Sorted(params string[] names) => names.OrderBy(name => name, StringComparer.Ordinal).ToArray();

    [Fact]
    public void JSON契约_货架型号字段名与附录A完全一致()
    {
        var dto = new ShelfModelDto { Color = "白", Source = "x", UpdatedAt = Now, UpdatedBy = "a" };

        Assert.Equal(
            Sorted(
                "guid", "code", "name", "sideType", "color", "source", "bayWidthMm", "totalHeightMm", "baseDepthMm",
                "baseTopHeightMm", "uprightWidthMm", "holePitchXMm", "holePitchYMm", "holeDiameterMm", "firstHoleLeftMm",
                "firstHoleTopMm", "holeCols", "holeRows", "rowNumberingFrom", "isActive", "referencedPlanCount", "updatedAt", "updatedBy"),
            JsonKeys(dto));
    }

    [Fact]
    public void JSON契约_挂钩型号字段名与附录A完全一致()
    {
        var dto = new HookModelDto { Color = "黑", LoadKg = 3, UpdatedAt = Now, UpdatedBy = "a" };

        Assert.Equal(
            Sorted(
                "guid", "code", "name", "color", "prongCount", "hasPriceTagArm", "armLengthMm", "dropMm", "wireDiameterMm",
                "tipReserveMm", "tagWidthMm", "tagHeightMm", "tagBottomAboveArmMm", "holeSpanX", "holeSpanY", "loadKg", "isActive",
                "referencedPlanCount", "updatedAt", "updatedBy"),
            JsonKeys(dto));
    }

    [Fact]
    public void JSON契约_商品参数与列表行字段名与附录A完全一致()
    {
        var spec = new ProductDisplaySpecDto
        {
            UnitWeightG = 1, DefaultHookGuid = "h", Notes = "n",
            HookQtyOverrides = new Dictionary<string, int> { ["h"] = 1 },
        };
        var item = new ProductSpecListItemDto
        {
            EnglishName = "e", ProductImage = "i", CategoryName = "c", Grade = "A", WarehouseCategoryPath = "百货 > 收纳", Spec = spec, UpdatedAt = Now, UpdatedBy = "a",
        };

        Assert.Equal(
            Sorted(
                "productCode", "widthMm", "heightMm", "depthMm", "hangHoleOffsetMm", "hangHoleType", "unitWeightG",
                "defaultHookGuid", "hookQtyOverrides", "source", "notes"),
            JsonKeys(spec));
        Assert.Equal(
            Sorted(
                "productCode", "itemNumber", "barcode", "productName", "englishName", "productImage", "retailPrice",
                "categoryName", "grade", "warehouseCategoryPath", "hasSpec", "referencedPlanCount", "spec", "updatedAt", "updatedBy"),
            JsonKeys(item));
    }

    [Fact]
    public void JSON契约_请求体缺省字段取附录A约定的默认值()
    {
        var shelf = JsonSerializer.Deserialize<ShelfModelDto>(
            """{"code":"G","name":"板","bayWidthMm":900,"totalHeightMm":1200,"baseDepthMm":300,"baseTopHeightMm":150,"uprightWidthMm":30,"holePitchXMm":25,"holePitchYMm":25,"holeDiameterMm":6,"firstHoleLeftMm":25,"firstHoleTopMm":25}""",
            WebJson)!;
        Assert.Equal(0, shelf.HoleCols); // 缺省 = 自动换算
        Assert.Equal(0, shelf.HoleRows);
        Assert.Equal(1, shelf.SideType);
        Assert.Equal(1, shelf.RowNumberingFrom);
        Assert.True(shelf.IsActive);

        var hook = JsonSerializer.Deserialize<HookModelDto>("""{"code":"H","name":"钩"}""", WebJson)!;
        Assert.Equal(10m, hook.TagBottomAboveArmMm);
        Assert.Equal(2, hook.ProngCount);
        Assert.True(hook.HasPriceTagArm);
        Assert.Equal(2, hook.HoleSpanX);
        Assert.Equal(1, hook.HoleSpanY);

        var spec = JsonSerializer.Deserialize<ProductDisplaySpecDto>(
            """{"widthMm":1,"heightMm":2,"depthMm":3,"hookQtyOverrides":{"abc":16}}""", WebJson)!;
        Assert.Equal(10, spec.HangHoleOffsetMm);
        Assert.Equal(1, spec.HangHoleType);
        Assert.Equal(1, spec.Source);
        Assert.Equal(16, spec.HookQtyOverrides["abc"]);
    }
}
