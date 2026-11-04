using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Models.Planogram;
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
/// 商品陈列图批 2：几何校验器（读取与前端共用的夹具 layout-cases.json）+ 方案服务（SQLite 临时库）+ 控制器路由 / 权限元数据 + JSON 契约。
/// 契约依据 docs/plans/2026-11-04-planogram-shelf-display-designer.md 附录 B；商品与金额都是虚构的。
/// </summary>
public sealed class PlanogramPlanTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
    private const string Actor = "alice";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-planogram-plan-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly PlanogramModelService _models;
    private readonly ProductDisplaySpecService _specs;
    private readonly PlanogramPlanService _plans;

    public PlanogramPlanTests()
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
            typeof(Product), typeof(ProductCategory));
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

    /// <summary>取当前时间时先执行一次副作用（只触发一次）：用来在「读取 / 预检之后、写入之前」精确插入别人的写入。</summary>
    private sealed class RacingTime(DateTime utcNow) : TimeProvider
    {
        public Action? BeforeNextRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            var action = BeforeNextRead;
            BeforeNextRead = null;
            action?.Invoke();
            return new DateTimeOffset(utcNow, TimeSpan.Zero);
        }
    }

    // ================================================================ 共享夹具（校验器）

    private const string FixtureRelativePath = "apps/web/src/pages/PosAdmin/Planogram/engine/fixtures/layout-cases.json";

    private static readonly Lazy<JsonDocument> Fixture = new(() =>
    {
        // 测试输出目录在 services/backend/BlazorApp.Api.Tests/bin/...：一路向上找到含夹具的仓库根。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, FixtureRelativePath);
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllText(candidate));
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到共享夹具 {FixtureRelativePath}（从 {AppContext.BaseDirectory} 向上查找）");
    });

    public static IEnumerable<object[]> FixtureCaseNames() =>
        Fixture.Value.RootElement.GetProperty("cases").EnumerateArray()
            .Select(element => new object[] { element.GetProperty("name").GetString()! });

    private static JsonElement FixtureCase(string name) =>
        Fixture.Value.RootElement.GetProperty("cases").EnumerateArray()
            .Single(element => element.GetProperty("name").GetString() == name);

    private static decimal? NullableDecimal(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetDecimal();

    private static LayoutShelfInput ParseShelf(JsonElement e) => new(
        e.GetProperty("bayWidthMm").GetInt32(), e.GetProperty("totalHeightMm").GetInt32(),
        e.GetProperty("uprightWidthMm").GetInt32(), e.GetProperty("baseTopHeightMm").GetInt32(),
        e.GetProperty("holePitchXMm").GetDecimal(), e.GetProperty("holePitchYMm").GetDecimal(),
        e.GetProperty("firstHoleLeftMm").GetDecimal(), e.GetProperty("firstHoleTopMm").GetDecimal(),
        e.GetProperty("holeCols").GetInt32(), e.GetProperty("holeRows").GetInt32());

    private static LayoutHookInput ParseHook(JsonElement e) => new(
        e.GetProperty("hasPriceTagArm").GetBoolean(), e.GetProperty("armLengthMm").GetDecimal(),
        e.GetProperty("dropMm").GetDecimal(), e.GetProperty("tipReserveMm").GetDecimal(),
        e.GetProperty("tagWidthMm").GetDecimal(), e.GetProperty("tagHeightMm").GetDecimal(),
        e.GetProperty("tagBottomAboveArmMm").GetDecimal(), e.GetProperty("holeSpanX").GetInt32(),
        e.GetProperty("holeSpanY").GetInt32(), NullableDecimal(e.GetProperty("loadKg")));

    private static LayoutSpecInput ParseSpec(JsonElement e) => new(
        e.GetProperty("widthMm").GetInt32(), e.GetProperty("heightMm").GetInt32(), e.GetProperty("depthMm").GetInt32(),
        e.GetProperty("hangHoleOffsetMm").GetInt32(),
        e.GetProperty("unitWeightG").ValueKind == JsonValueKind.Null ? null : e.GetProperty("unitWeightG").GetInt32(),
        e.GetProperty("hookQtyOverrides").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()),
        e.GetProperty("retailPrice").GetDecimal());

    private static LayoutItemInput ParseItem(JsonElement e) => new(
        e.GetProperty("guid").GetString()!, e.GetProperty("bayIndex").GetInt32(), e.GetProperty("productCode").GetString()!,
        e.GetProperty("hookModelGuid").GetString()!, e.GetProperty("holeCol").GetInt32(), e.GetProperty("holeRow").GetInt32(),
        e.GetProperty("perHookQty").ValueKind == JsonValueKind.Null ? null : e.GetProperty("perHookQty").GetInt32());

    private static PlanogramLayoutResult RunFixtureCase(JsonElement fixtureCase) =>
        PlanogramLayoutValidator.Validate(
            ParseShelf(fixtureCase.GetProperty("shelf")),
            fixtureCase.GetProperty("hooks").EnumerateObject().ToDictionary(p => p.Name, p => ParseHook(p.Value)),
            fixtureCase.GetProperty("specs").EnumerateObject().ToDictionary(p => p.Name, p => ParseSpec(p.Value)),
            fixtureCase.GetProperty("items").EnumerateArray().Select(ParseItem).ToList(),
            fixtureCase.GetProperty("bayCount").GetInt32(),
            new LayoutGaps(
                fixtureCase.GetProperty("gaps").GetProperty("gapXMm").GetInt32(),
                fixtureCase.GetProperty("gaps").GetProperty("gapYMm").GetInt32()));

    /// <summary>冲突排序键：(code, itemGuid, otherItemGuid 缺省为空, severity)。</summary>
    private static (string Code, string ItemGuid, string Other, string Severity) ConflictKey(string code, string itemGuid, string? other, string severity) =>
        (code, itemGuid, other ?? string.Empty, severity);

    [Fact]
    public void 夹具_至少二十个用例_名称唯一_覆盖全部冲突代码()
    {
        var cases = Fixture.Value.RootElement.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 20, $"夹具只有 {cases.Count} 个用例");
        var names = cases.Select(element => element.GetProperty("name").GetString()!).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        var covered = cases
            .SelectMany(element => element.GetProperty("expectedConflicts").EnumerateArray())
            .Select(conflict => conflict.GetProperty("code").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var code in new[]
        {
            PlanogramConflictCodes.BayOutOfRange, PlanogramConflictCodes.ProductSpecMissing, PlanogramConflictCodes.HookModelMissing,
            PlanogramConflictCodes.HoleOutOfRange, PlanogramConflictCodes.HookHoleClash, PlanogramConflictCodes.OutOfBoard,
            PlanogramConflictCodes.EdgeGap, PlanogramConflictCodes.Overlap, PlanogramConflictCodes.Overload,
        })
        {
            Assert.Contains(code, covered);
        }
    }

    [Theory]
    [MemberData(nameof(FixtureCaseNames))]
    public void 夹具_校验器对每个用例给出手算的冲突与统计(string name)
    {
        var fixtureCase = FixtureCase(name);

        var result = RunFixtureCase(fixtureCase);

        var expectedConflicts = fixtureCase.GetProperty("expectedConflicts").EnumerateArray()
            .Select(conflict => ConflictKey(
                conflict.GetProperty("code").GetString()!,
                conflict.GetProperty("itemGuid").GetString()!,
                conflict.TryGetProperty("otherItemGuid", out var other) ? other.GetString() : null,
                conflict.GetProperty("severity").GetString()!))
            .OrderBy(key => key).ToList();
        var actualConflicts = result.Conflicts
            .Select(conflict => ConflictKey(conflict.Code, conflict.ItemGuid, conflict.OtherItemGuid, conflict.Severity))
            .OrderBy(key => key).ToList();
        Assert.Equal(expectedConflicts, actualConflicts);
        Assert.False(result.Truncated);
        Assert.All(result.Conflicts, conflict => Assert.False(string.IsNullOrWhiteSpace(conflict.Message)));

        var expected = fixtureCase.GetProperty("expectedStats");
        var stats = result.Stats;
        Assert.Equal(expected.GetProperty("itemCount").GetInt32(), stats.ItemCount);
        Assert.Equal(expected.GetProperty("skuCount").GetInt32(), stats.SkuCount);
        Assert.Equal(expected.GetProperty("unitCount").GetInt32(), stats.UnitCount);
        Assert.Equal(expected.GetProperty("retailValue").GetDecimal(), stats.RetailValue);
        Assert.Equal(expected.GetProperty("fillPercent").GetInt32(), stats.FillPercent);
        Assert.Equal(expected.GetProperty("conflictCount").GetInt32(), stats.ConflictCount);
    }

    [Fact]
    public void 校验器_成对冲突各带对方_输出顺序稳定()
    {
        var fixtureCase = FixtureCase("hook_hole_clash_pairs");

        var first = RunFixtureCase(fixtureCase).Conflicts.Select(c => (c.Code, c.ItemGuid, c.OtherItemGuid)).ToList();
        var second = RunFixtureCase(fixtureCase).Conflicts.Select(c => (c.Code, c.ItemGuid, c.OtherItemGuid)).ToList();

        Assert.Equal(first, second);
        // 成对冲突：A→B 之后紧跟 B→A。
        Assert.Contains((PlanogramConflictCodes.HookHoleClash, "c1", "c2"), first);
        Assert.Equal(
            first.FindIndex(x => x == (PlanogramConflictCodes.HookHoleClash, "c1", "c2")) + 1,
            first.FindIndex(x => x == (PlanogramConflictCodes.HookHoleClash, "c2", "c1")));
    }

    [Fact]
    public void 校验器_几百个条目叠在同一位置_冲突列表截断_但冲突条目数是完整的()
    {
        var fixtureCase = FixtureCase("baseline_no_conflicts");
        var shelf = ParseShelf(fixtureCase.GetProperty("shelf"));
        var hooks = fixtureCase.GetProperty("hooks").EnumerateObject().ToDictionary(p => p.Name, p => ParseHook(p.Value));
        var specs = fixtureCase.GetProperty("specs").EnumerateObject().ToDictionary(p => p.Name, p => ParseSpec(p.Value));
        // 250 个条目全在同一个孔：成对冲突 250*249 条 × (占孔冲突 + 重叠) 远超上限。
        var items = Enumerable.Range(0, 250)
            .Select(i => new LayoutItemInput($"x{i}", 1, "P-A", "hook-d", 5, 3, null))
            .ToList();

        var result = PlanogramLayoutValidator.Validate(shelf, hooks, specs, items, 1, new LayoutGaps(10, 10));

        Assert.True(result.Truncated);
        Assert.Equal(PlanogramLayoutValidator.MaxConflictsReturned, result.Conflicts.Count);
        Assert.Equal(250, result.Stats.ConflictCount);
        Assert.Equal(250, result.Stats.ItemCount);
    }

    [Fact]
    public void 校验器_建议每钩挂数向下取整_覆盖值与条目值小于1视为没填()
    {
        var hook = new LayoutHookInput(true, 200, 50, 20, 52, 26, 10, 2, 1, 3);
        var spec = new LayoutSpecInput(100, 100, 25, 10, 200, new Dictionary<string, int> { ["h"] = 0 }, 1m);
        var item = new LayoutItemInput("i", 1, "P", "h", 1, 1, null);

        // (200−20)÷25 = 7.2 → 7；覆盖值 0 无效。
        Assert.Equal(7, PlanogramLayoutValidator.SuggestPerHookQty(hook, spec));
        Assert.Equal(7, PlanogramLayoutValidator.ResolvePerHookQty(item, hook, spec));
        Assert.Equal(7, PlanogramLayoutValidator.ResolvePerHookQty(item with { PerHookQty = 0 }, hook, spec));
        Assert.Equal(9, PlanogramLayoutValidator.ResolvePerHookQty(item with { PerHookQty = 9 }, hook, spec));
        var withOverride = spec with { HookQtyOverrides = new Dictionary<string, int> { ["h"] = 4 } };
        Assert.Equal(4, PlanogramLayoutValidator.ResolvePerHookQty(item, hook, withOverride));
        Assert.Equal(9, PlanogramLayoutValidator.ResolvePerHookQty(item with { PerHookQty = 9 }, hook, withOverride));
        // 臂太短 / 厚度未填 → 0。
        Assert.Equal(0, PlanogramLayoutValidator.SuggestPerHookQty(hook with { TipReserveMm = 200 }, spec));
        Assert.Equal(0, PlanogramLayoutValidator.SuggestPerHookQty(hook, spec with { DepthMm = 0 }));
    }

    // ================================================================ 方案服务

    private void SeedProducts()
    {
        _db.Insertable(new Product
        {
            ProductCode = "P100", ItemNumber = "IT-100", Barcode = "9300000000011", ProductName = "挂钩收纳盒",
            EnglishName = "Hook Box", RetailPrice = 4.5m,
        }).ExecuteCommand();
        _db.Insertable(new Product
        {
            ProductCode = "P200", ItemNumber = "IT-200", Barcode = "9300000000028", ProductName = "洗碗海绵 3 片装",
            EnglishName = "Sponge 3pk", RetailPrice = 2.99m,
        }).ExecuteCommand();
        _db.Insertable(new Product { ProductCode = "P300", ItemNumber = "IT-300", ProductName = "不织布收纳袋", RetailPrice = 6m }).ExecuteCommand();
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

    private static ProductDisplaySpecDto Spec(int width = 120) => new()
    {
        WidthMm = width, HeightMm = 180, DepthMm = 30, HangHoleOffsetMm = 10, HangHoleType = 1, UnitWeightG = 200,
    };

    private sealed record Seed(ShelfModelDto Shelf, HookModelDto Hook);

    /// <summary>货架（板面 870×1050，33×41 孔）、双钩带价签、P100 / P200 已设参数（P300 没有）。</summary>
    private async Task<Seed> SeedModelsAsync()
    {
        var shelf = await _models.CreateShelfModelAsync(Shelf(), Actor, default);
        Assert.True(shelf.Success, shelf.Message);
        var hook = await _models.CreateHookModelAsync(Hook(), Actor, default);
        Assert.True(hook.Success, hook.Message);
        Assert.True((await _specs.SaveAsync("P100", Spec(), Actor, default)).Success);
        Assert.True((await _specs.SaveAsync("P200", Spec(100), Actor, default)).Success);
        return new Seed(shelf.Data!, hook.Data!);
    }

    private static PlanogramItemDto Item(string guid, string product, string hook, int col, int row, int bay = 1, int? qty = null, int sort = 0) => new()
    {
        Guid = guid, BayIndex = bay, ProductCode = product, HookModelGuid = hook, HoleCol = col, HoleRow = row,
        PerHookQty = qty, SortOrder = sort,
    };

    private async Task<PlanogramDetailDto> NewPlanAsync(Seed seed, string name = "试验方案", int bayCount = 1)
    {
        var created = await _plans.CreateAsync(
            new PlanogramCreateRequest { Name = name, ShelfModelGuid = seed.Shelf.Guid, BayCount = bayCount }, Actor, default);
        Assert.True(created.Success, created.Message);
        return created.Data!;
    }

    private static PlanogramSaveRequest SaveRequest(PlanogramDetailDto detail, params PlanogramItemDto[] items) => new()
    {
        Name = detail.Header.Name,
        Notes = detail.Header.Notes,
        ShelfModelGuid = detail.Header.ShelfModelGuid,
        BayCount = detail.Header.BayCount,
        GapXMm = detail.Header.GapXMm,
        GapYMm = detail.Header.GapYMm,
        Items = items.ToList(),
        RowVersion = detail.RowVersion,
    };

    private async Task<PlanogramDetailDto> SaveOkAsync(PlanogramDetailDto detail, params PlanogramItemDto[] items)
    {
        var saved = await _plans.SaveAsync(detail.Header.Guid, SaveRequest(detail, items), Actor, default);
        Assert.True(saved.Success, saved.Message + " " + JsonSerializer.Serialize(saved.Details));
        return saved.Data!;
    }

    private static PlanogramFieldError[] FieldErrors(object? details) =>
        Assert.IsType<List<PlanogramFieldError>>(details).ToArray();

    /// <summary>两件 P100（120×180，双钩）：各 6 件，零售 54，占板 4.73% -> 5，无冲突。</summary>
    private static PlanogramItemDto[] TwoCleanItems(string hook) =>
        new[] { Item("a", "P100", hook, 5, 3), Item("b", "P100", hook, 13, 3, sort: 1) };

    [Fact]
    public async Task 新建_默认间距10_草稿_版本1_返回完整详情()
    {
        var seed = await SeedModelsAsync();

        var created = await _plans.CreateAsync(
            new PlanogramCreateRequest { Name = "  入口货架  ", Notes = "试验", ShelfModelGuid = seed.Shelf.Guid, BayCount = 3 }, Actor, default);

        Assert.True(created.Success, created.Message);
        var detail = created.Data!;
        Assert.Equal("入口货架", detail.Header.Name);
        Assert.Equal("试验", detail.Header.Notes);
        Assert.Equal(10, detail.Header.GapXMm);
        Assert.Equal(10, detail.Header.GapYMm);
        Assert.Equal(PlanogramStatus.Draft, detail.Header.Status);
        Assert.Equal(1, detail.Header.Version);
        Assert.Null(detail.Header.PublishedVersion);
        Assert.Equal(3, detail.Header.BayCount);
        Assert.Equal(seed.Shelf.Name, detail.Header.ShelfModelName);
        Assert.Equal(Actor, detail.Header.UpdatedBy);
        Assert.False(string.IsNullOrWhiteSpace(detail.RowVersion));
        Assert.Empty(detail.Items);
        Assert.Empty(detail.Conflicts);
        Assert.Equal(0, detail.Stats.ItemCount);
        Assert.Equal(seed.Shelf.Guid, detail.ShelfModel.Guid);
        Assert.Single(detail.HookModels);
        Assert.Empty(detail.ProductSpecs);

        var row = await _db.Queryable<PlanogramHeader>().FirstAsync(plan => plan.Guid == detail.Header.Guid);
        Assert.Equal(Actor, row.CreatedBy);
        Assert.Equal(Now, row.CreatedAt);
    }

    [Fact]
    public async Task 新建_自定义间距_节数1到12_货架必须存在且启用_一次返回全部错误()
    {
        var seed = await SeedModelsAsync();
        var ok = await _plans.CreateAsync(
            new PlanogramCreateRequest { Name = "A", ShelfModelGuid = seed.Shelf.Guid, BayCount = 12, GapXMm = 0, GapYMm = 25 }, Actor, default);
        Assert.True(ok.Success, ok.Message);
        Assert.Equal(0, ok.Data!.Header.GapXMm);
        Assert.Equal(25, ok.Data.Header.GapYMm);

        var bad = await _plans.CreateAsync(
            new PlanogramCreateRequest { Name = "  ", ShelfModelGuid = "missing", BayCount = 13, GapXMm = -1, GapYMm = 500 }, Actor, default);
        Assert.False(bad.Success);
        Assert.Equal(PlanogramErrorCodes.Validation, bad.ErrorCode);
        Assert.Equal(
            new[] { "bayCount", "gapXMm", "gapYMm", "name", "shelfModelGuid" },
            FieldErrors(bad.Details).Select(error => error.Field).OrderBy(name => name, StringComparer.Ordinal));

        var zero = await _plans.CreateAsync(
            new PlanogramCreateRequest { Name = "A", ShelfModelGuid = seed.Shelf.Guid, BayCount = 0 }, Actor, default);
        Assert.Equal(PlanogramErrorCodes.Validation, zero.ErrorCode);

        // 停用的货架型号：不可新建。
        var disabled = Shelf("G-OFF");
        disabled.IsActive = false;
        var off = (await _models.CreateShelfModelAsync(disabled, Actor, default)).Data!;
        var onOff = await _plans.CreateAsync(
            new PlanogramCreateRequest { Name = "A", ShelfModelGuid = off.Guid, BayCount = 1 }, Actor, default);
        Assert.Equal("shelfModelGuid", Assert.Single(FieldErrors(onOff.Details)).Field);
    }

    [Fact]
    public async Task 读取_不存在返回NOT_FOUND_头是嵌套对象_rowVersion在顶层()
    {
        Assert.Equal(PlanogramErrorCodes.NotFound, (await _plans.GetAsync("missing", default)).ErrorCode);
        Assert.Equal(PlanogramErrorCodes.NotFound, (await _plans.GetAsync("  ", default)).ErrorCode);

        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var loaded = await _plans.GetAsync(plan.Header.Guid, default);

        Assert.True(loaded.Success);
        Assert.Equal(plan.Header.Guid, loaded.Data!.Header.Guid);
        Assert.Equal(plan.RowVersion, loaded.Data.RowVersion);
    }

    [Fact]
    public async Task 保存_整体替换明细_写冲突数_商品参数快照_统计_详情带齐型号与商品参数()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);

        var saved = await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid));

        Assert.Equal(2, saved.Items.Count);
        Assert.Equal(new[] { "a", "b" }, saved.Items.Select(item => item.Guid));
        Assert.Empty(saved.Conflicts);
        Assert.Equal(2, saved.Stats.ItemCount);
        Assert.Equal(12, saved.Stats.UnitCount);
        Assert.Equal(54m, saved.Stats.RetailValue);
        Assert.Equal(5, saved.Stats.FillPercent);
        Assert.Equal(2, saved.Header.ItemCount);
        Assert.Equal(1, saved.Header.SkuCount);
        Assert.Equal(12, saved.Header.UnitCount);
        Assert.NotEqual(plan.RowVersion, saved.RowVersion);
        Assert.Equal(1, saved.Header.Version);
        Assert.Equal(PlanogramStatus.Draft, saved.Header.Status);

        // 方案内商品的参数行：只含 P100，带零售价与已设参数；P200 不在方案里不返回。
        var spec = Assert.Single(saved.ProductSpecs);
        Assert.Equal("P100", spec.ProductCode);
        Assert.Equal(4.5m, spec.RetailPrice);
        Assert.True(spec.HasSpec);
        Assert.Equal(1, spec.ReferencedPlanCount);

        // 明细落库：guid 沿用、快照写入、头上记录冲突数与校验时间。
        var rows = await _db.Queryable<PlanogramItem>().Where(item => item.PlanogramGuid == plan.Header.Guid).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("{\"widthMm\":120,\"heightMm\":180,\"depthMm\":30,\"hangHoleOffsetMm\":10}", row.SpecSnapshotJson));
        var header = await _db.Queryable<PlanogramHeader>().FirstAsync(p => p.Guid == plan.Header.Guid);
        Assert.Equal(0, header.ConflictCount);
        Assert.NotNull(header.LastValidatedAt);

        // 再保存一次：旧明细被整体替换，不是追加。
        var again = await SaveOkAsync(saved, Item("c", "P200", seed.Hook.Guid, 20, 10));
        Assert.Equal(new[] { "c" }, again.Items.Select(item => item.Guid));
        Assert.Equal(1, await _db.Queryable<PlanogramItem>().Where(item => item.PlanogramGuid == plan.Header.Guid).CountAsync());
    }

    [Fact]
    public async Task 保存_有冲突也能保存_冲突数写入头_读取时复算并按条目返回()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed, bayCount: 2);
        // a、b 同孔 -> HOOK_HOLE_CLASH + OVERLAP；c 在第 3 节（只有 2 节）-> BAY_OUT_OF_RANGE；d 缺参数（P300 未设）。
        var saved = await SaveOkAsync(plan,
            Item("a", "P100", seed.Hook.Guid, 5, 3),
            Item("b", "P100", seed.Hook.Guid, 5, 3),
            Item("c", "P100", seed.Hook.Guid, 5, 3, bay: 3),
            Item("d", "P300", seed.Hook.Guid, 20, 20));

        Assert.Equal(4, saved.Header.ItemCount);
        Assert.Equal(4, saved.Header.ConflictCount);
        Assert.Equal(4, saved.Stats.ConflictCount);
        var codes = saved.Conflicts.Select(conflict => conflict.Code).Distinct().OrderBy(code => code, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "BAY_OUT_OF_RANGE", "HOOK_HOLE_CLASH", "OVERLAP", "PRODUCT_SPEC_MISSING" },
            codes);
        Assert.Equal(4, (await _db.Queryable<PlanogramHeader>().FirstAsync(p => p.Guid == plan.Header.Guid)).ConflictCount);
        // 缺参数的 P300：参数行仍返回（hasSpec=false），前端据此显示「补充参数」入口。
        Assert.False(saved.ProductSpecs.Single(row => row.ProductCode == "P300").HasSpec);

        // 重新读取：冲突是复算出来的，和保存时一致。
        var loaded = (await _plans.GetAsync(plan.Header.Guid, default)).Data!;
        Assert.Equal(saved.Conflicts.Count, loaded.Conflicts.Count);
    }

    [Fact]
    public async Task 保存_rowVersion过期返回STALE_库里内容不变_同一瞬间连续保存rowVersion也变化()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var first = await SaveOkAsync(plan, Item("a", "P100", seed.Hook.Guid, 5, 3));
        // FixedTime 下两次保存的「当前时间」完全相同，rowVersion 仍必须变。
        Assert.NotEqual(plan.RowVersion, first.RowVersion);

        // 用旧的 rowVersion（plan.RowVersion）再保存：被拒绝。
        var stale = await _plans.SaveAsync(plan.Header.Guid, SaveRequest(plan, Item("z", "P200", seed.Hook.Guid, 9, 9)), Actor, default);

        Assert.False(stale.Success);
        Assert.Equal(PlanogramErrorCodes.Stale, stale.ErrorCode);
        var rows = await _db.Queryable<PlanogramItem>().Where(item => item.PlanogramGuid == plan.Header.Guid).ToListAsync();
        Assert.Equal(new[] { "a" }, rows.Select(row => row.Guid));

        // 用新的 rowVersion 保存成功，且再次变化。
        var second = await SaveOkAsync(first, Item("b", "P200", seed.Hook.Guid, 9, 9));
        Assert.NotEqual(first.RowVersion, second.RowVersion);
        Assert.Equal(new[] { "b" }, second.Items.Select(item => item.Guid));
    }

    [Fact]
    public async Task 保存_真实时钟带7位刻度的时间戳_连续保存_读取的rowVersion与保存返回的一致_不误报过期()
    {
        // 其它用例用固定时间（毫秒为 0）；这里用系统时钟，UpdatedAt 带 100ns 刻度，验证库里实际存下的精度
        // （SQLite 文本 / SQL Server datetime2，且 SqlSugar 按 DbType.DateTime 传参会舍入到约 3.33 ms）
        // 不会让 rowVersion 往返后对不上、也不会让条件更新误判「别人改过」。
        var seed = await SeedModelsAsync();
        var service = new PlanogramPlanService(_db);
        var created = (await service.CreateAsync(
            new PlanogramCreateRequest { Name = "真实时钟", ShelfModelGuid = seed.Shelf.Guid, BayCount = 1 }, Actor, default)).Data!;
        var current = created;

        for (var round = 0; round < 5; round++)
        {
            var request = SaveRequest(current, Item("a", "P100", seed.Hook.Guid, 5 + round, 3));
            var saved = await service.SaveAsync(current.Header.Guid, request, Actor, default);
            Assert.True(saved.Success, $"第 {round + 1} 次保存失败：{saved.Message}");
            Assert.NotEqual(current.RowVersion, saved.Data!.RowVersion);
            var reread = (await service.GetAsync(current.Header.Guid, default)).Data!;
            Assert.Equal(saved.Data.RowVersion, reread.RowVersion);
            current = reread;
        }

        Assert.Equal(5 + 4, current.Items.Single().HoleCol);
    }

    [Fact]
    public async Task 保存_预检通过后别人抢先写入_条件更新0行_整体回滚报STALE_头与明细都不变()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var first = await SaveOkAsync(plan, Item("keep", "P100", seed.Hook.Guid, 5, 3));

        // 预检（rowVersion 一致）之后、写入之前，另一个写入者改了这一行：UpdatedAt 变了。
        var racing = new RacingTime(Now);
        var racingService = new PlanogramPlanService(_db, racing);
        var rivalUpdatedAt = Now.AddMinutes(5);
        racing.BeforeNextRead = () => _db.Updateable<PlanogramHeader>()
            .SetColumns(p => p.UpdatedAt == rivalUpdatedAt)
            .SetColumns(p => p.Name == "别人改的名字")
            .Where(p => p.Guid == plan.Header.Guid)
            .ExecuteCommand();

        var request = SaveRequest(first, Item("mine", "P200", seed.Hook.Guid, 9, 9));
        request.Name = "我改的名字";
        var result = await racingService.SaveAsync(plan.Header.Guid, request, Actor, default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.Stale, result.ErrorCode);
        var header = await _db.Queryable<PlanogramHeader>().FirstAsync(p => p.Guid == plan.Header.Guid);
        Assert.Equal("别人改的名字", header.Name); // 别人的写入保留，我的没有覆盖
        var rows = await _db.Queryable<PlanogramItem>().Where(item => item.PlanogramGuid == plan.Header.Guid).ToListAsync();
        Assert.Equal(new[] { "keep" }, rows.Select(row => row.Guid)); // 明细没有被删掉重建
    }

    [Fact]
    public async Task 保存_rowVersion缺失或格式不对_缺失是校验错误_格式不对按过期处理()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);

        var missing = SaveRequest(plan);
        missing.RowVersion = null;
        var noToken = await _plans.SaveAsync(plan.Header.Guid, missing, Actor, default);
        Assert.Equal(PlanogramErrorCodes.Validation, noToken.ErrorCode);
        Assert.Contains("rowVersion", FieldErrors(noToken.Details).Select(error => error.Field));

        var garbage = SaveRequest(plan);
        garbage.RowVersion = "not-a-token";
        Assert.Equal(PlanogramErrorCodes.Stale, (await _plans.SaveAsync(plan.Header.Guid, garbage, Actor, default)).ErrorCode);
    }

    [Fact]
    public async Task 保存_不存在返回NOT_FOUND_已归档返回ARCHIVED_恢复后可保存()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        Assert.Equal(PlanogramErrorCodes.NotFound, (await _plans.SaveAsync("missing", SaveRequest(plan), Actor, default)).ErrorCode);

        Assert.True((await _plans.ArchiveAsync(plan.Header.Guid, Actor, default)).Success);
        var archivedSave = await _plans.SaveAsync(plan.Header.Guid, SaveRequest(plan), Actor, default);
        Assert.Equal(PlanogramErrorCodes.Archived, archivedSave.ErrorCode);

        Assert.True((await _plans.RestoreAsync(plan.Header.Guid, Actor, default)).Success);
        var fresh = (await _plans.GetAsync(plan.Header.Guid, default)).Data!;
        Assert.True((await _plans.SaveAsync(plan.Header.Guid, SaveRequest(fresh, Item("a", "P100", seed.Hook.Guid, 5, 3)), Actor, default)).Success);
    }

    [Fact]
    public async Task 保存_已发布的方案保存后版本加1回到草稿_草稿保存不加版本_最近发布版本不变()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var draftSaved = await SaveOkAsync(plan, Item("a", "P100", seed.Hook.Guid, 5, 3));
        Assert.Equal(1, draftSaved.Header.Version); // 草稿保存不加版本

        // 模拟批 3 的发布：状态 1、最近发布版本 1。直接改状态列，不动 UpdatedAt，所以 rowVersion 仍有效。
        var publishedAt = Now; // SqlSugar 表达式里不能直接引用私有静态字段
        _db.Updateable<PlanogramHeader>()
            .SetColumns(p => p.Status == (byte)1)
            .SetColumns(p => p.PublishedVersion == 1)
            .SetColumns(p => p.PublishedAt == publishedAt)
            .SetColumns(p => p.PublishedBy == "boss")
            .Where(p => p.Guid == plan.Header.Guid)
            .ExecuteCommand();

        var published = (await _plans.GetAsync(plan.Header.Guid, default)).Data!;
        Assert.Equal(PlanogramStatus.Published, published.Header.Status);
        Assert.Equal(published.RowVersion, draftSaved.RowVersion);

        var afterEdit = await SaveOkAsync(published, Item("a", "P100", seed.Hook.Guid, 6, 3));
        Assert.Equal(PlanogramStatus.Draft, afterEdit.Header.Status);
        Assert.Equal(2, afterEdit.Header.Version);
        Assert.Equal(1, afterEdit.Header.PublishedVersion);
        Assert.Equal("boss", (await _db.Queryable<PlanogramHeader>().FirstAsync(p => p.Guid == plan.Header.Guid)).PublishedBy);

        // 回到草稿后再保存，版本号保持 2。
        var again = await SaveOkAsync(afterEdit, Item("a", "P100", seed.Hook.Guid, 7, 3));
        Assert.Equal(2, again.Header.Version);
    }

    [Fact]
    public async Task 保存_明细结构校验_guid不重复_挂数与面_货号不能为空_一次返回全部()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var request = SaveRequest(plan,
            Item("a", "NOPE", seed.Hook.Guid, 5, 3),
            Item("b", "P100", "no-such-hook", 6, 3),
            Item("dup", "P100", seed.Hook.Guid, 7, 3),
            Item("dup", "P100", seed.Hook.Guid, 8, 3),
            Item("q", "P100", seed.Hook.Guid, 9, 3, qty: 0),
            Item("", "", seed.Hook.Guid, 10, 3));
        request.Items![4].Face = 1;
        request.Items![4].ItemType = 2;

        var result = await _plans.SaveAsync(plan.Header.Guid, request, Actor, default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.Validation, result.ErrorCode);
        var fields = FieldErrors(result.Details).Select(error => error.Field).ToList();
        Assert.Contains("items[3].guid", fields); // 第二个 dup 在下标 3
        Assert.DoesNotContain("items[2].guid", fields);
        Assert.Contains("items[4].perHookQty", fields);
        Assert.Contains("items[4].face", fields);
        Assert.Contains("items[4].itemType", fields);
        Assert.Contains("items[5].productCode", fields);
        // 库里没有任何改动。
        Assert.Empty(await _db.Queryable<PlanogramItem>().Where(item => item.PlanogramGuid == plan.Header.Guid).ToListAsync());
    }

    [Fact]
    public async Task 保存_商品或挂钩型号不存在_直接PLANOGRAM_VALIDATION_且货号以主档写法为准()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);

        var missingProduct = await _plans.SaveAsync(
            plan.Header.Guid, SaveRequest(plan, Item("a", "NOPE", seed.Hook.Guid, 5, 3)), Actor, default);
        Assert.Equal(PlanogramErrorCodes.Validation, missingProduct.ErrorCode);
        Assert.Equal("items[0].productCode", Assert.Single(FieldErrors(missingProduct.Details)).Field);

        var missingHook = await _plans.SaveAsync(
            plan.Header.Guid, SaveRequest(plan, Item("a", "P100", "no-such-hook", 5, 3)), Actor, default);
        Assert.Equal("items[0].hookModelGuid", Assert.Single(FieldErrors(missingHook.Details)).Field);

        // 停用的挂钩型号仍然「存在」，可以保存。
        var inactive = Hook("HK-OLD");
        inactive.IsActive = false;
        var old = (await _models.CreateHookModelAsync(inactive, Actor, default)).Data!;
        Assert.True((await _plans.SaveAsync(plan.Header.Guid, SaveRequest(plan, Item("a", "P100", old.Guid, 5, 3)), Actor, default)).Success);
    }

    [Fact]
    public async Task 保存_节号孔位越界不是请求错误而是冲突_空guid由服务端生成_缺省的间距与明细被拒绝()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed, bayCount: 2);

        // 越界值（节号 0、孔位 0 / 999）照存，作为冲突返回。
        var saved = await SaveOkAsync(plan,
            Item("", "P100", seed.Hook.Guid, 0, 3, bay: 0),
            Item("z", "P100", seed.Hook.Guid, 999, 999));
        Assert.Equal(2, saved.Items.Count);
        Assert.All(saved.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.Guid)));
        Assert.Contains(saved.Conflicts, conflict => conflict.Code == PlanogramConflictCodes.BayOutOfRange);
        Assert.Equal(2, saved.Conflicts.Count(conflict => conflict.Code == PlanogramConflictCodes.HoleOutOfRange));

        var request = SaveRequest(saved);
        request.GapXMm = null;
        request.GapYMm = null;
        request.Items = null;
        var missing = await _plans.SaveAsync(plan.Header.Guid, request, Actor, default);
        Assert.Equal(
            new[] { "gapXMm", "gapYMm", "items" },
            FieldErrors(missing.Details).Select(error => error.Field).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task 保存_明细上限2000条_2001条被拒绝_2000条可保存()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed, bayCount: 12);
        PlanogramItemDto[] Many(int count) => Enumerable.Range(0, count)
            .Select(i => Item($"i{i}", "P100", seed.Hook.Guid, 1 + i % 30, 1 + i / 30 % 40, bay: 1 + i % 12, sort: i))
            .ToArray();

        var tooMany = await _plans.SaveAsync(plan.Header.Guid, SaveRequest(plan, Many(2001)), Actor, default);
        Assert.Equal(PlanogramErrorCodes.Validation, tooMany.ErrorCode);
        Assert.Equal("items", Assert.Single(FieldErrors(tooMany.Details)).Field);

        var saved = await SaveOkAsync(plan, Many(2000));
        Assert.Equal(2000, saved.Items.Count);
        Assert.Equal(2000, await _db.Queryable<PlanogramItem>().Where(item => item.PlanogramGuid == plan.Header.Guid).CountAsync());
    }

    [Fact]
    public async Task 保存_明细guid不能撞上别的方案的明细()
    {
        var seed = await SeedModelsAsync();
        var one = await NewPlanAsync(seed, "方案一");
        var two = await NewPlanAsync(seed, "方案二");
        await SaveOkAsync(one, Item("shared", "P100", seed.Hook.Guid, 5, 3));

        var clash = await _plans.SaveAsync(two.Header.Guid, SaveRequest(two, Item("shared", "P200", seed.Hook.Guid, 5, 3)), Actor, default);

        Assert.Equal(PlanogramErrorCodes.Validation, clash.ErrorCode);
        Assert.Contains("shared", clash.Message);
        Assert.Equal("shared", (await _db.Queryable<PlanogramItem>().SingleAsync(item => item.PlanogramGuid == one.Header.Guid)).Guid);
    }

    [Fact]
    public async Task 保存_切换货架型号_只能切到启用的型号_保持当前停用型号不受限_并按新孔阵重算冲突()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var saved = await SaveOkAsync(plan, Item("a", "P100", seed.Hook.Guid, 30, 3)); // col 30 占 30～31，33 列货架内合法
        Assert.Empty(saved.Conflicts);

        // 一个只有 10 列的小货架：节宽 300，立柱 30 -> 板面 270，首孔边距 25，孔距 25 -> ⌊220/25⌋+1 = 9 列。
        var small = Shelf("G-SMALL");
        small.BayWidthMm = 300;
        var smallShelf = (await _models.CreateShelfModelAsync(small, Actor, default)).Data!;
        var switchRequest = SaveRequest(saved, saved.Items.ToArray());
        switchRequest.ShelfModelGuid = smallShelf.Guid;
        var switched = await _plans.SaveAsync(plan.Header.Guid, switchRequest, Actor, default);
        Assert.True(switched.Success, switched.Message);
        Assert.Equal(smallShelf.Name, switched.Data!.Header.ShelfModelName);
        Assert.Contains(switched.Data.Conflicts, conflict => conflict.Code == PlanogramConflictCodes.HoleOutOfRange);
        Assert.Equal(1, switched.Data.Header.ConflictCount);

        // 切到停用的型号：拒绝。
        var disabledShelf = Shelf("G-OFF");
        disabledShelf.IsActive = false;
        var off = (await _models.CreateShelfModelAsync(disabledShelf, Actor, default)).Data!;
        var toOff = SaveRequest(switched.Data, switched.Data.Items.ToArray());
        toOff.ShelfModelGuid = off.Guid;
        var rejected = await _plans.SaveAsync(plan.Header.Guid, toOff, Actor, default);
        Assert.Equal("shelfModelGuid", Assert.Single(FieldErrors(rejected.Details)).Field);

        // 方案当前用的型号后来被停用：继续保存不受限。
        var deactivate = Shelf("G-SMALL");
        deactivate.BayWidthMm = 300;
        deactivate.IsActive = false;
        // 型号已被引用，停用只改 IsActive，不碰锁定字段。
        Assert.True((await _models.UpdateShelfModelAsync(smallShelf.Guid, deactivate, Actor, default)).Success);
        Assert.True((await _plans.SaveAsync(plan.Header.Guid, SaveRequest(switched.Data, switched.Data.Items.ToArray()), Actor, default)).Success);
    }

    [Fact]
    public async Task 读取_商品参数改了之后条目specChanged为true_重新保存后恢复()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var saved = await SaveOkAsync(plan,
            Item("a", "P100", seed.Hook.Guid, 5, 3),
            Item("b", "P200", seed.Hook.Guid, 15, 3, sort: 1),
            Item("c", "P300", seed.Hook.Guid, 25, 3, sort: 2)); // P300 保存时没有参数 -> 没有快照
        Assert.All(saved.Items, item => Assert.False(item.SpecChanged));

        Assert.True((await _specs.SaveAsync("P100", Spec(130), Actor, default)).Success);
        var changed = (await _plans.GetAsync(plan.Header.Guid, default)).Data!;
        Assert.True(changed.Items.Single(item => item.Guid == "a").SpecChanged);
        Assert.False(changed.Items.Single(item => item.Guid == "b").SpecChanged);
        Assert.False(changed.Items.Single(item => item.Guid == "c").SpecChanged);

        // P300 后来补了参数：保存时没有快照，不算「参数已变」。
        Assert.True((await _specs.SaveAsync("P300", Spec(), Actor, default)).Success);
        Assert.False((await _plans.GetAsync(plan.Header.Guid, default)).Data!.Items.Single(item => item.Guid == "c").SpecChanged);

        // 吊孔偏移也算（它决定商品在钩上的位置）。
        var offsetChanged = Spec(100);
        offsetChanged.HangHoleOffsetMm = 12;
        Assert.True((await _specs.SaveAsync("P200", offsetChanged, Actor, default)).Success);
        Assert.True((await _plans.GetAsync(plan.Header.Guid, default)).Data!.Items.Single(item => item.Guid == "b").SpecChanged);

        // 保存会刷新快照。
        var resaved = await SaveOkAsync(changed, changed.Items.ToArray());
        Assert.All(resaved.Items, item => Assert.False(item.SpecChanged));
    }

    [Fact]
    public async Task 读取_挂钩型号只含启用的加上方案引用到的停用型号()
    {
        var seed = await SeedModelsAsync();
        var inactive = Hook("HK-OLD");
        inactive.IsActive = false;
        var old = (await _models.CreateHookModelAsync(inactive, Actor, default)).Data!;
        var otherInactive = Hook("HK-OLD2");
        otherInactive.IsActive = false;
        await _models.CreateHookModelAsync(otherInactive, Actor, default);
        var plan = await NewPlanAsync(seed);

        var saved = await SaveOkAsync(plan, Item("a", "P100", old.Guid, 5, 3));

        Assert.Equal(new[] { "HK-D-PT-200", "HK-OLD" }, saved.HookModels.Select(hook => hook.Code).OrderBy(code => code, StringComparer.Ordinal));
        Assert.Equal(1, saved.HookModels.Single(hook => hook.Code == "HK-OLD").ReferencedPlanCount);
    }

    [Fact]
    public async Task 校验_不落库_返回冲突与统计_商品不存在同样是校验错误_方案不存在返回NOT_FOUND()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var request = SaveRequest(plan,
            Item("a", "P100", seed.Hook.Guid, 5, 3),
            Item("b", "P100", seed.Hook.Guid, 5, 3));
        request.RowVersion = null; // 校验不需要 rowVersion
        request.Name = string.Empty; // 也不需要名称

        var result = await _plans.ValidateAsync(plan.Header.Guid, request, default);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.Data!.Stats.ItemCount);
        Assert.Equal(2, result.Data.Stats.ConflictCount);
        Assert.Contains(result.Data.Conflicts, conflict => conflict.Code == PlanogramConflictCodes.HookHoleClash && conflict.OtherItemGuid == "b");
        Assert.Empty(await _db.Queryable<PlanogramItem>().Where(item => item.PlanogramGuid == plan.Header.Guid).ToListAsync());
        Assert.Equal(plan.RowVersion, (await _plans.GetAsync(plan.Header.Guid, default)).Data!.RowVersion);

        request.Items = new List<PlanogramItemDto> { Item("a", "NOPE", seed.Hook.Guid, 5, 3) };
        Assert.Equal(PlanogramErrorCodes.Validation, (await _plans.ValidateAsync(plan.Header.Guid, request, default)).ErrorCode);
        Assert.Equal(PlanogramErrorCodes.NotFound, (await _plans.ValidateAsync("missing", request, default)).ErrorCode);

        // 校验接口能带着不同的节数 / 间距试算（设计器「改间距即时看冲突」）。
        var tight = SaveRequest(plan, TwoCleanItems(seed.Hook.Guid));
        tight.GapXMm = 200;
        var withBigGap = await _plans.ValidateAsync(plan.Header.Guid, tight, default);
        Assert.True(withBigGap.Data!.Stats.ConflictCount > 0);
    }

    [Fact]
    public async Task 复制_新guid_草稿_版本1_明细全复制且guid全新_源方案不变()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed, "原方案", bayCount: 2);
        var saved = await SaveOkAsync(plan, Item("a", "P100", seed.Hook.Guid, 5, 3, qty: 4), Item("b", "P200", seed.Hook.Guid, 15, 3, bay: 2, sort: 1));
        _db.Updateable<PlanogramHeader>().SetColumns(p => p.Status == (byte)1).SetColumns(p => p.PublishedVersion == 1)
            .Where(p => p.Guid == plan.Header.Guid).ExecuteCommand();

        var copy = await _plans.DuplicateAsync(plan.Header.Guid, new PlanogramDuplicateRequest { Name = " 原方案（副本） " }, "bob", default);

        Assert.True(copy.Success, copy.Message);
        var detail = copy.Data!;
        Assert.NotEqual(plan.Header.Guid, detail.Header.Guid);
        Assert.Equal("原方案（副本）", detail.Header.Name);
        Assert.Equal(PlanogramStatus.Draft, detail.Header.Status);
        Assert.Equal(1, detail.Header.Version);
        Assert.Null(detail.Header.PublishedVersion);
        Assert.Equal(2, detail.Header.BayCount);
        Assert.Equal("bob", detail.Header.UpdatedBy);
        Assert.Equal(2, detail.Items.Count);
        Assert.Empty(detail.Items.Select(item => item.Guid).Intersect(new[] { "a", "b" }));
        Assert.Equal(4, detail.Items.Single(item => item.ProductCode == "P100").PerHookQty);
        Assert.Equal(2, detail.Items.Single(item => item.ProductCode == "P200").BayIndex);
        Assert.Equal(saved.Stats.UnitCount, detail.Stats.UnitCount);

        // 源方案原样：状态仍是已发布、明细仍是 a、b。
        var source = await _db.Queryable<PlanogramHeader>().FirstAsync(p => p.Guid == plan.Header.Guid);
        Assert.Equal(1, source.Status);
        Assert.Equal(new[] { "a", "b" }, (await _db.Queryable<PlanogramItem>().Where(item => item.PlanogramGuid == plan.Header.Guid).ToListAsync())
            .Select(row => row.Guid).OrderBy(guid => guid, StringComparer.Ordinal));

        // 名称必填；源不存在返回 NOT_FOUND。
        Assert.Equal(PlanogramErrorCodes.Validation, (await _plans.DuplicateAsync(plan.Header.Guid, new PlanogramDuplicateRequest(), Actor, default)).ErrorCode);
        Assert.Equal(PlanogramErrorCodes.NotFound, (await _plans.DuplicateAsync("missing", new PlanogramDuplicateRequest { Name = "x" }, Actor, default)).ErrorCode);
    }

    [Fact]
    public async Task 复制_保留保存时的尺寸快照_参数已变提示不会因复制消失()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        await SaveOkAsync(plan, Item("a", "P100", seed.Hook.Guid, 5, 3));
        Assert.True((await _specs.SaveAsync("P100", Spec(130), Actor, default)).Success);

        var copy = (await _plans.DuplicateAsync(plan.Header.Guid, new PlanogramDuplicateRequest { Name = "副本" }, Actor, default)).Data!;

        Assert.True(Assert.Single(copy.Items).SpecChanged);
    }

    [Fact]
    public async Task 归档与恢复_状态流转_重复操作幂等不动rowVersion_不存在返回NOT_FOUND()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);

        var archived = await _plans.ArchiveAsync(plan.Header.Guid, "boss", default);
        Assert.True(archived.Success, archived.Message);
        Assert.Equal(PlanogramStatus.Archived, archived.Data!.Status);
        Assert.Equal("boss", archived.Data.UpdatedBy);
        var versionAfterArchive = (await _plans.GetAsync(plan.Header.Guid, default)).Data!.RowVersion;
        Assert.NotEqual(plan.RowVersion, versionAfterArchive);

        // 重复归档：幂等，rowVersion 不变。
        Assert.True((await _plans.ArchiveAsync(plan.Header.Guid, Actor, default)).Success);
        Assert.Equal(versionAfterArchive, (await _plans.GetAsync(plan.Header.Guid, default)).Data!.RowVersion);

        var restored = await _plans.RestoreAsync(plan.Header.Guid, Actor, default);
        Assert.True(restored.Success, restored.Message);
        Assert.Equal(PlanogramStatus.Draft, restored.Data!.Status);
        var versionAfterRestore = (await _plans.GetAsync(plan.Header.Guid, default)).Data!.RowVersion;
        Assert.True((await _plans.RestoreAsync(plan.Header.Guid, Actor, default)).Success);
        Assert.Equal(versionAfterRestore, (await _plans.GetAsync(plan.Header.Guid, default)).Data!.RowVersion);

        Assert.Equal(PlanogramErrorCodes.NotFound, (await _plans.ArchiveAsync("missing", Actor, default)).ErrorCode);
        Assert.Equal(PlanogramErrorCodes.NotFound, (await _plans.RestoreAsync("missing", Actor, default)).ErrorCode);
    }

    [Fact]
    public async Task 恢复_发布过的方案版本号保持大于最近发布版本()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        _db.Updateable<PlanogramHeader>().SetColumns(p => p.Status == (byte)1).SetColumns(p => p.PublishedVersion == 1)
            .Where(p => p.Guid == plan.Header.Guid).ExecuteCommand();
        Assert.True((await _plans.ArchiveAsync(plan.Header.Guid, Actor, default)).Success);

        var restored = await _plans.RestoreAsync(plan.Header.Guid, Actor, default);

        Assert.Equal(PlanogramStatus.Draft, restored.Data!.Status);
        Assert.Equal(2, restored.Data.Version);
        Assert.Equal(1, restored.Data.PublishedVersion);
    }

    [Fact]
    public async Task 删除_只有草稿且从未发布可删_连明细一起删_其它情形NOT_DELETABLE()
    {
        var seed = await SeedModelsAsync();
        var draft = await NewPlanAsync(seed, "草稿");
        await SaveOkAsync(draft, TwoCleanItems(seed.Hook.Guid));
        var published = await NewPlanAsync(seed, "已发布过");
        var wasPublished = await NewPlanAsync(seed, "发布后改回草稿");
        var archived = await NewPlanAsync(seed, "归档");
        var hasSnapshot = await NewPlanAsync(seed, "有快照");
        _db.Updateable<PlanogramHeader>().SetColumns(p => p.Status == (byte)1).SetColumns(p => p.PublishedVersion == 1)
            .Where(p => p.Guid == published.Header.Guid).ExecuteCommand();
        _db.Updateable<PlanogramHeader>().SetColumns(p => p.PublishedVersion == 1)
            .Where(p => p.Guid == wasPublished.Header.Guid).ExecuteCommand();
        await _plans.ArchiveAsync(archived.Header.Guid, Actor, default);
        _db.Insertable(new PlanogramVersion { PlanogramGuid = hasSnapshot.Header.Guid, Version = 1, LayoutJson = "{}", PublishedAt = Now }).ExecuteCommand();

        foreach (var blocked in new[] { published, wasPublished, archived, hasSnapshot })
        {
            var result = await _plans.DeleteAsync(blocked.Header.Guid, default);
            Assert.False(result.Success);
            Assert.Equal(PlanogramErrorCodes.NotDeletable, result.ErrorCode);
            Assert.NotNull(await _db.Queryable<PlanogramHeader>().FirstAsync(p => p.Guid == blocked.Header.Guid));
        }

        var deleted = await _plans.DeleteAsync(draft.Header.Guid, default);
        Assert.True(deleted.Success, deleted.Message);
        Assert.Null(await _db.Queryable<PlanogramHeader>().FirstAsync(p => p.Guid == draft.Header.Guid));
        Assert.Empty(await _db.Queryable<PlanogramItem>().Where(item => item.PlanogramGuid == draft.Header.Guid).ToListAsync());
        Assert.Equal(PlanogramErrorCodes.NotFound, (await _plans.DeleteAsync(draft.Header.Guid, default)).ErrorCode);
    }

    [Fact]
    public async Task 列表_按货架_状态_关键字筛选_分页_统计现算()
    {
        var seed = await SeedModelsAsync();
        var otherShelf = (await _models.CreateShelfModelAsync(Shelf("G-OTHER"), Actor, default)).Data!;
        var one = await NewPlanAsync(seed, "入口陈列");
        await SaveOkAsync(one, TwoCleanItems(seed.Hook.Guid));
        var two = await NewPlanAsync(seed, "收银台陈列");
        await SaveOkAsync(two, Item("x", "P200", seed.Hook.Guid, 5, 3));
        var three = (await _plans.CreateAsync(
            new PlanogramCreateRequest { Name = "仓库", ShelfModelGuid = otherShelf.Guid, BayCount = 1 }, Actor, default)).Data!;
        await _plans.ArchiveAsync(three.Header.Guid, Actor, default);

        var all = (await _plans.ListAsync(null, null, null, 1, 20, default)).Data!;
        Assert.Equal(3, all.Total);
        var row = all.Items!.Single(item => item.Guid == one.Header.Guid);
        Assert.Equal(2, row.ItemCount);
        Assert.Equal(1, row.SkuCount);
        Assert.Equal(12, row.UnitCount);
        Assert.Equal(5, row.FillPercent);
        Assert.Equal(0, row.ConflictCount);
        Assert.Equal(seed.Shelf.Name, row.ShelfModelName);

        Assert.Equal(2, (await _plans.ListAsync(seed.Shelf.Guid, null, null, 1, 20, default)).Data!.Total);
        Assert.Equal(1, (await _plans.ListAsync(null, PlanogramStatus.Archived, null, 1, 20, default)).Data!.Total);
        Assert.Equal(2, (await _plans.ListAsync(null, PlanogramStatus.Draft, null, 1, 20, default)).Data!.Total);
        Assert.Equal(0, (await _plans.ListAsync(null, PlanogramStatus.Published, null, 1, 20, default)).Data!.Total);

        // 关键字：方案名称、明细商品货号 / 名称 / 条码 / 英文名。
        Assert.Equal(new[] { one.Header.Guid }, (await _plans.ListAsync(null, null, "入口", 1, 20, default)).Data!.Items!.Select(item => item.Guid));
        Assert.Equal(new[] { one.Header.Guid }, (await _plans.ListAsync(null, null, "P100", 1, 20, default)).Data!.Items!.Select(item => item.Guid));
        Assert.Equal(new[] { one.Header.Guid }, (await _plans.ListAsync(null, null, "收纳盒", 1, 20, default)).Data!.Items!.Select(item => item.Guid));
        Assert.Equal(new[] { two.Header.Guid }, (await _plans.ListAsync(null, null, "Sponge", 1, 20, default)).Data!.Items!.Select(item => item.Guid));
        Assert.Equal(new[] { two.Header.Guid }, (await _plans.ListAsync(null, null, "9300000000028", 1, 20, default)).Data!.Items!.Select(item => item.Guid));
        Assert.Empty((await _plans.ListAsync(null, null, "不存在的词", 1, 20, default)).Data!.Items!);

        var paged = (await _plans.ListAsync(null, null, null, 2, 2, default)).Data!;
        Assert.Equal(3, paged.Total);
        Assert.Single(paged.Items!);
        Assert.Equal(2, paged.Page);

        var bad = await _plans.ListAsync(null, 9, null, 1, 20, default);
        Assert.Equal(PlanogramErrorCodes.Validation, bad.ErrorCode);
    }

    [Fact]
    public async Task 列表_冲突数是现算的_商品参数改动后列表上的冲突数跟着变()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid));
        Assert.Equal(0, (await _plans.ListAsync(null, null, null, 1, 20, default)).Data!.Items!.Single().ConflictCount);

        // P100 变宽到 400：两件横向（间距 8 个孔 = 200 mm）相撞。头上存的冲突数仍是保存时的 0，列表现算为 2。
        Assert.True((await _specs.SaveAsync("P100", Spec(400), Actor, default)).Success);

        Assert.Equal(2, (await _plans.ListAsync(null, null, null, 1, 20, default)).Data!.Items!.Single().ConflictCount);
        Assert.Equal(0, (await _db.Queryable<PlanogramHeader>().FirstAsync(p => p.Guid == plan.Header.Guid)).ConflictCount);
    }

    // ---------------------------------------------------------------- 批 1 补强：被引用保护

    [Fact]
    public async Task 被引用保护_货架型号_挂钩型号_商品参数_引用消失后才能删除或清除()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var saved = await SaveOkAsync(plan, Item("a", "P100", seed.Hook.Guid, 5, 3));

        // 方案引用中：货架型号、挂钩型号都不能删（只能停用），商品参数不能清除。
        Assert.Equal(1, (await _models.ListShelfModelsAsync(null, null, 1, 20, default)).Data!.Items!.Single().ReferencedPlanCount);
        Assert.Equal(PlanogramErrorCodes.ModelInUse, (await _models.DeleteShelfModelAsync(seed.Shelf.Guid, default)).ErrorCode);
        Assert.Equal(PlanogramErrorCodes.ModelInUse, (await _models.DeleteHookModelAsync(seed.Hook.Guid, default)).ErrorCode);
        var specBlocked = await _specs.DeleteAsync("P100", default);
        Assert.False(specBlocked.Success);
        Assert.Equal(PlanogramErrorCodes.SpecInUse, specBlocked.ErrorCode);
        Assert.Contains("1 个陈列方案", specBlocked.Message);
        Assert.NotNull(await _db.Queryable<ProductDisplaySpec>().FirstAsync(spec => spec.ProductCode == "P100"));
        // 没被引用的 P200 可以清除。
        Assert.True((await _specs.DeleteAsync("P200", default)).Success);

        // 方案里把条目拿掉：商品参数、挂钩型号可以删了；货架型号仍被方案（空方案也算）引用。
        await SaveOkAsync(saved);
        Assert.True((await _specs.DeleteAsync("P100", default)).Success);
        Assert.True((await _models.DeleteHookModelAsync(seed.Hook.Guid, default)).Success);
        Assert.Equal(PlanogramErrorCodes.ModelInUse, (await _models.DeleteShelfModelAsync(seed.Shelf.Guid, default)).ErrorCode);

        // 删除方案后，货架型号也能删。
        Assert.True((await _plans.DeleteAsync(plan.Header.Guid, default)).Success);
        Assert.True((await _models.DeleteShelfModelAsync(seed.Shelf.Guid, default)).Success);
    }

    [Fact]
    public async Task 被引用保护_归档方案同样算引用_商品参数列表的引用数来自真实明细()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        await SaveOkAsync(plan, Item("a", "P100", seed.Hook.Guid, 5, 3), Item("b", "P100", seed.Hook.Guid, 15, 3, sort: 1));
        await _plans.ArchiveAsync(plan.Header.Guid, Actor, default);

        Assert.Equal(PlanogramErrorCodes.SpecInUse, (await _specs.DeleteAsync("P100", default)).ErrorCode);
        var row = (await _specs.GetAsync("P100", default)).Data!;
        Assert.Equal(1, row.ReferencedPlanCount); // 同一方案两行只算一个方案
    }

    [Fact]
    public async Task 货架型号锁定_真实方案引用后改孔距被拒_复制为新型号可改()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        await SaveOkAsync(plan, Item("a", "P100", seed.Hook.Guid, 5, 3));

        var changed = Shelf();
        changed.HolePitchXMm = 25.4m;
        var locked = await _models.UpdateShelfModelAsync(seed.Shelf.Guid, changed, Actor, default);
        Assert.Equal(PlanogramErrorCodes.ModelLocked, locked.ErrorCode);

        var copy = Shelf("G-S-PEG-90x30x120-25.4");
        copy.HolePitchXMm = 25.4m;
        Assert.True((await _models.CreateShelfModelAsync(copy, Actor, default)).Success);
        // 停用原型号不受限，方案仍可读。
        var disabled = Shelf();
        disabled.IsActive = false;
        Assert.True((await _models.UpdateShelfModelAsync(seed.Shelf.Guid, disabled, Actor, default)).Success);
        Assert.True((await _plans.GetAsync(plan.Header.Guid, default)).Success);
    }

    // ================================================================ 控制器

    public static IEnumerable<object[]> ControllerContract()
    {
        // 方法名, HTTP 方法, 路由模板, 权限策略（附录 B.2 + 任务约定：validate 需 Edit）
        yield return new object[] { nameof(PlanogramController.ListPlans), "GET", "planograms", Permissions.Planogram.View };
        yield return new object[] { nameof(PlanogramController.CreatePlan), "POST", "planograms", Permissions.Planogram.Edit };
        yield return new object[] { nameof(PlanogramController.GetPlan), "GET", "planograms/{guid}", Permissions.Planogram.View };
        yield return new object[] { nameof(PlanogramController.SavePlan), "PUT", "planograms/{guid}", Permissions.Planogram.Edit };
        yield return new object[] { nameof(PlanogramController.ValidatePlan), "POST", "planograms/{guid}/validate", Permissions.Planogram.Edit };
        yield return new object[] { nameof(PlanogramController.DuplicatePlan), "POST", "planograms/{guid}/duplicate", Permissions.Planogram.Edit };
        yield return new object[] { nameof(PlanogramController.ArchivePlan), "POST", "planograms/{guid}/archive", Permissions.Planogram.Manage };
        yield return new object[] { nameof(PlanogramController.RestorePlan), "POST", "planograms/{guid}/restore", Permissions.Planogram.Manage };
        yield return new object[] { nameof(PlanogramController.DeletePlan), "DELETE", "planograms/{guid}", Permissions.Planogram.Manage };
    }

    [Theory]
    [MemberData(nameof(ControllerContract))]
    public void 控制器_方案路由_HTTP方法_权限策略与附录B一致(string methodName, string verb, string template, string policy)
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
        Assert.False(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any());
    }

    [Fact]
    public void 控制器_方案路由全路径_读用View_保存校验复制用Edit_归档恢复删除用Manage()
    {
        var byPolicy = ControllerContract()
            .GroupBy(row => (string)row[3])
            .ToDictionary(group => group.Key, group => group.Select(row => $"{row[1]} {row[2]}").OrderBy(text => text, StringComparer.Ordinal).ToArray());

        Assert.Equal(new[] { "GET planograms", "GET planograms/{guid}" }, byPolicy[Permissions.Planogram.View]);
        Assert.Equal(
            new[] { "POST planograms", "POST planograms/{guid}/duplicate", "POST planograms/{guid}/validate", "PUT planograms/{guid}" },
            byPolicy[Permissions.Planogram.Edit]);
        Assert.Equal(
            new[] { "DELETE planograms/{guid}", "POST planograms/{guid}/archive", "POST planograms/{guid}/restore" },
            byPolicy[Permissions.Planogram.Manage]);
    }

    private PlanogramController NewController() => new(_models, _specs, _plans)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "carol") }, "TestAuth")),
            },
        },
    };

    [Fact]
    public async Task 控制器_方案接口_成功200_失败400保留业务错误码_用户名写入审计_空请求体按空对象处理()
    {
        var seed = await SeedModelsAsync();
        var controller = NewController();

        var created = await controller.CreatePlan(
            new PlanogramCreateRequest { Name = "控制器方案", ShelfModelGuid = seed.Shelf.Guid, BayCount = 1 }, default);
        var detail = Assert.IsType<ApiResponse<PlanogramDetailDto>>(Assert.IsType<OkObjectResult>(created).Value).Data!;
        Assert.Equal("carol", detail.Header.UpdatedBy);

        var save = SaveRequest(detail, Item("a", "P100", seed.Hook.Guid, 5, 3));
        var saved = await controller.SavePlan(detail.Header.Guid, save, default);
        Assert.IsType<OkObjectResult>(saved);

        // 同一个旧 rowVersion 再保存 -> 400 + PLANOGRAM_STALE。
        var stale = await controller.SavePlan(detail.Header.Guid, save, default);
        var staleBody = Assert.IsType<ApiResponse<PlanogramDetailDto>>(Assert.IsType<BadRequestObjectResult>(stale).Value);
        Assert.Equal(PlanogramErrorCodes.Stale, staleBody.ErrorCode);

        var nullBody = await controller.CreatePlan(null, default);
        Assert.Equal(
            PlanogramErrorCodes.Validation,
            Assert.IsType<ApiResponse<PlanogramDetailDto>>(Assert.IsType<BadRequestObjectResult>(nullBody).Value).ErrorCode);
        var nullSave = await controller.SavePlan(detail.Header.Guid, null, default);
        Assert.IsType<BadRequestObjectResult>(nullSave);

        var validated = await controller.ValidatePlan(detail.Header.Guid, save, default);
        Assert.IsType<ApiResponse<PlanogramValidateResultDto>>(Assert.IsType<OkObjectResult>(validated).Value);

        var listed = await controller.ListPlans(null, null, null, 1, 20, default);
        Assert.Equal(1, Assert.IsType<ApiResponse<PagedResult<PlanogramListItemDto>>>(Assert.IsType<OkObjectResult>(listed).Value).Data!.Total);
        Assert.IsType<OkObjectResult>(await controller.GetPlan(detail.Header.Guid, default));
        Assert.IsType<BadRequestObjectResult>(await controller.GetPlan("missing", default));

        var duplicated = await controller.DuplicatePlan(detail.Header.Guid, new PlanogramDuplicateRequest { Name = "副本" }, default);
        Assert.IsType<OkObjectResult>(duplicated);
        var archived = await controller.ArchivePlan(detail.Header.Guid, default);
        Assert.Equal("carol", Assert.IsType<ApiResponse<PlanogramListItemDto>>(Assert.IsType<OkObjectResult>(archived).Value).Data!.UpdatedBy);
        Assert.IsType<OkObjectResult>(await controller.RestorePlan(detail.Header.Guid, default));
        // 草稿且从未发布：可删；再删一次方案已不存在 -> 400 + NOT_FOUND。
        Assert.IsType<OkObjectResult>(await controller.DeletePlan(detail.Header.Guid, default));
        var gone = await controller.DeletePlan(detail.Header.Guid, default);
        Assert.Equal(
            PlanogramErrorCodes.NotFound,
            Assert.IsType<ApiResponse<object>>(Assert.IsType<BadRequestObjectResult>(gone).Value).ErrorCode);
    }

    // ================================================================ JSON 契约

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static string[] JsonKeys(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, WebJson)).RootElement
            .EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private static string[] Sorted(params string[] names) => names.OrderBy(name => name, StringComparer.Ordinal).ToArray();

    [Fact]
    public void JSON契约_方案列表行_明细_冲突_统计_字段名与附录B完全一致()
    {
        Assert.Equal(
            Sorted("guid", "name", "notes", "shelfModelGuid", "shelfModelName", "bayCount", "gapXMm", "gapYMm", "status", "version",
                "publishedVersion", "publishedAt", "itemCount", "skuCount", "unitCount", "fillPercent", "conflictCount",
                "hasUnpublishedChanges", "updatedAt", "updatedBy"),
            JsonKeys(new PlanogramListItemDto()));
        Assert.Equal(
            Sorted("guid", "bayIndex", "face", "itemType", "productCode", "hookModelGuid", "holeCol", "holeRow", "perHookQty", "sortOrder", "specChanged"),
            JsonKeys(new PlanogramItemDto()));
        Assert.Equal(Sorted("itemGuid", "otherItemGuid", "code", "severity", "message"), JsonKeys(new ConflictDto()));
        Assert.Equal(
            Sorted("itemCount", "skuCount", "unitCount", "retailValue", "fillPercent", "conflictCount"),
            JsonKeys(new PlanogramStatsDto()));
        Assert.Equal(Sorted("conflicts", "stats"), JsonKeys(new PlanogramValidateResultDto()));
    }

    [Fact]
    public void JSON契约_方案详情顶层字段_header嵌套_rowVersion在顶层()
    {
        Assert.Equal(
            Sorted("header", "rowVersion", "items", "shelfModel", "hookModels", "productSpecs", "conflicts", "stats"),
            JsonKeys(new PlanogramDetailDto()));
    }

    [Fact]
    public void JSON契约_请求体字段名_新建_保存_复制()
    {
        Assert.Equal(Sorted("name", "notes", "shelfModelGuid", "bayCount", "gapXMm", "gapYMm"), JsonKeys(new PlanogramCreateRequest()));
        Assert.Equal(
            Sorted("name", "notes", "shelfModelGuid", "bayCount", "gapXMm", "gapYMm", "items", "rowVersion"),
            JsonKeys(new PlanogramSaveRequest()));
        Assert.Equal(Sorted("name"), JsonKeys(new PlanogramDuplicateRequest()));
    }

    [Fact]
    public void JSON契约_请求体缺省字段_明细face为0_itemType为1_间距可空()
    {
        var item = JsonSerializer.Deserialize<PlanogramItemDto>(
            "{\"guid\":\"a\",\"productCode\":\"P100\",\"hookModelGuid\":\"h\",\"holeCol\":5,\"holeRow\":3}", WebJson)!;
        Assert.Equal(0, item.Face);
        Assert.Equal(1, item.ItemType);
        Assert.Equal(1, item.BayIndex);
        Assert.Null(item.PerHookQty);

        var save = JsonSerializer.Deserialize<PlanogramSaveRequest>("{\"name\":\"x\",\"shelfModelGuid\":\"s\",\"bayCount\":2}", WebJson)!;
        Assert.Null(save.GapXMm);
        Assert.Null(save.GapYMm);
        Assert.Null(save.Items);
        Assert.Null(save.RowVersion);

        var create = JsonSerializer.Deserialize<PlanogramCreateRequest>("{\"name\":\"x\",\"shelfModelGuid\":\"s\",\"bayCount\":2}", WebJson)!;
        Assert.Null(create.GapXMm);
    }

    [Fact]
    public async Task JSON契约_详情序列化后冲突严重度是error或warn_状态是数字()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var heavy = new ProductDisplaySpecDto { WidthMm = 60, HeightMm = 60, DepthMm = 10, UnitWeightG = 200 };
        Assert.True((await _specs.SaveAsync("P300", heavy, Actor, default)).Success);
        var saved = await SaveOkAsync(plan, Item("a", "P300", seed.Hook.Guid, 5, 5));

        var json = JsonDocument.Parse(JsonSerializer.Serialize(saved, WebJson)).RootElement;

        Assert.Equal(JsonValueKind.Number, json.GetProperty("header").GetProperty("status").ValueKind);
        var conflict = Assert.Single(json.GetProperty("conflicts").EnumerateArray());
        Assert.Equal("OVERLOAD", conflict.GetProperty("code").GetString());
        Assert.Equal("warn", conflict.GetProperty("severity").GetString());
        Assert.Equal(JsonValueKind.String, json.GetProperty("rowVersion").ValueKind);
        Assert.EndsWith("Z", json.GetProperty("rowVersion").GetString());
        Assert.Equal(JsonValueKind.Number, json.GetProperty("stats").GetProperty("retailValue").ValueKind);
    }
}
