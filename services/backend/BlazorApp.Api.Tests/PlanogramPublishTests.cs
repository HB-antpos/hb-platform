using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BlazorApp.Api.Authorization;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.Planogram;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Planogram;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 商品陈列图批 3：发布与版本快照、历史版本读取、JSON 导出 / 导入（SQLite 临时库，补建了与迁移一致的
/// <c>(PlanogramGuid, Version)</c> 唯一索引）+ 控制器路由 / 权限元数据 + 权限别名。
/// 契约依据 docs/plans/2026-11-04-planogram-shelf-display-designer.md 附录 C；商品与金额都是虚构的。
/// 涉及并发 / 唯一索引 / 事务的用例另在文件末尾的 SQL Server 类里用真实库再跑一遍（需环境变量，见该类）。
/// </summary>
public sealed class PlanogramPublishTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
    private const string Actor = "alice";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-planogram-publish-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly PlanogramModelService _models;
    private readonly ProductDisplaySpecService _specs;
    private readonly PlanogramPlanService _plans;

    public PlanogramPublishTests()
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
        // 与迁移 20261104.001 的 UX_PlanogramVersion_Plan_Version 一致：同一方案同一版本号只能有一份快照（并发发布的最后防线）。
        _db.Ado.ExecuteCommand("CREATE UNIQUE INDEX UX_PlanogramVersion_Plan_Version ON PlanogramVersion (PlanogramGuid, Version)");
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

    /// <summary>取当前时间时先执行一次副作用（只触发一次）：用来在「预检之后、写入之前」精确插入别人的写入。</summary>
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

    // ================================================================ 夹具数据

    private void SeedProducts()
    {
        _db.Insertable(new Product
        {
            ProductCode = "P100", ItemNumber = "IT-100", Barcode = "9300000000011", ProductName = "挂钩收纳盒",
            EnglishName = "Hook Box", RetailPrice = 4.5m, ProductImage = "/img/p100.jpg",
        }).ExecuteCommand();
        _db.Insertable(new Product
        {
            ProductCode = "P200", ItemNumber = "IT-200", Barcode = "9300000000028", ProductName = "洗碗海绵 3 片装",
            EnglishName = "Sponge 3pk", RetailPrice = 2.99m,
        }).ExecuteCommand();
        _db.Insertable(new Product { ProductCode = "P300", ItemNumber = "IT-300", ProductName = "不织布收纳袋", RetailPrice = 6m }).ExecuteCommand();
    }

    internal static ShelfModelDto Shelf(string code = "G-S-PEG-90x30x120") => new()
    {
        Code = code, Name = "单面洞洞板 900×1200 · 白", Color = "白", Source = "Shop Fittings Store G",
        BayWidthMm = 900, TotalHeightMm = 1200, BaseDepthMm = 300, BaseTopHeightMm = 150, UprightWidthMm = 30,
        HolePitchXMm = 25, HolePitchYMm = 25, HoleDiameterMm = 6, FirstHoleLeftMm = 25, FirstHoleTopMm = 25,
    };

    internal static HookModelDto Hook(string code = "HK-D-PT-200") => new()
    {
        Code = code, Name = "双钩带价签 200 · 黑", Color = "黑", ProngCount = 2, HasPriceTagArm = true,
        ArmLengthMm = 200, DropMm = 50, WireDiameterMm = 5, TipReserveMm = 20, TagWidthMm = 52, TagHeightMm = 26,
        HoleSpanX = 2, HoleSpanY = 1, LoadKg = 3,
    };

    /// <summary>无价签臂的小挂钩：占位只有商品本身，用来在一块板上排满两千个不冲突的小商品（大快照用例）。</summary>
    internal static HookModelDto SmallHook() => new()
    {
        Code = "HK-S-100", Name = "单钩无价签", ProngCount = 1, HasPriceTagArm = false,
        ArmLengthMm = 200, DropMm = 50, WireDiameterMm = 5, TipReserveMm = 20, TagWidthMm = 0, TagHeightMm = 0,
        HoleSpanX = 2, HoleSpanY = 1,
    };

    internal static ProductDisplaySpecDto Spec(int width = 120) => new()
    {
        WidthMm = width, HeightMm = 180, DepthMm = 30, HangHoleOffsetMm = 10, HangHoleType = 1, UnitWeightG = 200,
    };

    internal static ProductDisplaySpecDto TinySpec() => new()
    {
        WidthMm = 20, HeightMm = 20, DepthMm = 10, HangHoleOffsetMm = 5, HangHoleType = 1, UnitWeightG = 100,
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

    internal static PlanogramItemDto Item(string guid, string product, string hook, int col, int row, int bay = 1, int? qty = null, int sort = 0) => new()
    {
        Guid = guid, BayIndex = bay, ProductCode = product, HookModelGuid = hook, HoleCol = col, HoleRow = row,
        PerHookQty = qty, SortOrder = sort,
    };

    private async Task<PlanogramDetailDto> NewPlanAsync(Seed seed, string name = "试验方案", int bayCount = 1, int? gapX = null, int? gapY = null)
    {
        var created = await _plans.CreateAsync(
            new PlanogramCreateRequest { Name = name, ShelfModelGuid = seed.Shelf.Guid, BayCount = bayCount, GapXMm = gapX, GapYMm = gapY },
            Actor, default);
        Assert.True(created.Success, created.Message);
        return created.Data!;
    }

    internal static PlanogramSaveRequest SaveRequest(PlanogramDetailDto detail, params PlanogramItemDto[] items) => new()
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

    internal static PlanogramPublishRequest PublishRequest(PlanogramDetailDto detail) => new() { RowVersion = detail.RowVersion };

    private async Task<PlanogramDetailDto> PublishOkAsync(PlanogramDetailDto detail, string actor = "boss")
    {
        var published = await _plans.PublishAsync(detail.Header.Guid, PublishRequest(detail), actor, default);
        Assert.True(published.Success, published.Message + " " + JsonSerializer.Serialize(published.Details));
        return published.Data!;
    }

    private static PlanogramFieldError[] FieldErrors(object? details) =>
        Assert.IsType<List<PlanogramFieldError>>(details).ToArray();

    /// <summary>两件 P100（120×180，双钩）：各 6 件，零售 54，无冲突。</summary>
    /// <summary>明细 guid 是全库主键，同一个用例里给多个方案各放一套时用 prefix 区分。</summary>
    internal static PlanogramItemDto[] TwoCleanItems(string hook, string prefix = "") =>
        new[] { Item(prefix + "a", "P100", hook, 5, 3), Item(prefix + "b", "P100", hook, 13, 3, sort: 1) };

    private async Task<PlanogramDetailDto> GetOkAsync(string planGuid)
    {
        var loaded = await _plans.GetAsync(planGuid, default);
        Assert.True(loaded.Success, loaded.Message);
        return loaded.Data!;
    }

    private async Task<PlanogramSnapshotDto> GetVersionOkAsync(string planGuid, int version)
    {
        var loaded = await _plans.GetVersionAsync(planGuid, version, default);
        Assert.True(loaded.Success, loaded.Message);
        return loaded.Data!;
    }

    private Task<int> VersionRowCountAsync(string planGuid) =>
        _db.Queryable<PlanogramVersion>().Where(row => row.PlanogramGuid == planGuid).CountAsync();

    // ================================================================ 发布：成功

    [Fact]
    public async Task 发布_成功_写版本快照并把头置为已发布_返回完整详情_rowVersion变化()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed, "入口货架");
        var saved = await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid));

        var result = await _plans.PublishAsync(plan.Header.Guid, PublishRequest(saved), "boss", default);

        Assert.True(result.Success, result.Message);
        var detail = result.Data!;
        Assert.Equal(PlanogramStatus.Published, detail.Header.Status);
        Assert.Equal(1, detail.Header.Version);
        Assert.Equal(1, detail.Header.PublishedVersion);
        Assert.NotNull(detail.Header.PublishedAt);
        Assert.False(detail.Header.HasUnpublishedChanges);
        Assert.Equal(2, detail.Items.Count);
        Assert.NotEqual(saved.RowVersion, detail.RowVersion);

        var versionRow = await _db.Queryable<PlanogramVersion>().FirstAsync(row => row.PlanogramGuid == plan.Header.Guid);
        Assert.Equal(1, versionRow.Version);
        Assert.Equal("boss", versionRow.PublishedBy);
        Assert.False(string.IsNullOrWhiteSpace(versionRow.LayoutJson));

        var header = await _db.Queryable<PlanogramHeader>().FirstAsync(p => p.Guid == plan.Header.Guid);
        Assert.Equal(PlanogramStatus.Published, header.Status);
        Assert.Equal(1, header.PublishedVersion);
        Assert.Equal("boss", header.PublishedBy);
        Assert.Equal(versionRow.PublishedAt, header.PublishedAt);
        Assert.Equal("boss", header.UpdatedBy);
        Assert.Equal(0, header.ConflictCount);
        Assert.True(header.PublishedAt >= Now);
    }

    [Fact]
    public async Task 发布_只有warn级冲突不拦截_超承重照常发布()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var heavy = new ProductDisplaySpecDto { WidthMm = 60, HeightMm = 60, DepthMm = 10, UnitWeightG = 200 };
        Assert.True((await _specs.SaveAsync("P300", heavy, Actor, default)).Success);
        var saved = await SaveOkAsync(plan, Item("a", "P300", seed.Hook.Guid, 5, 5));
        var warn = Assert.Single(saved.Conflicts);
        Assert.Equal(PlanogramSeverity.Warn, warn.Severity);

        var published = await PublishOkAsync(saved);

        Assert.Equal(PlanogramStatus.Published, published.Header.Status);
        Assert.Equal(0, published.Stats.ConflictCount);
        // 快照里的统计与冲突口径一致：warn 不计入冲突条目数。
        Assert.Equal(0, (await GetVersionOkAsync(plan.Header.Guid, 1)).Stats.ConflictCount);
    }

    // ================================================================ 发布：快照固化

    [Fact]
    public async Task 发布_快照固化发布时刻的商品名_价格_图片_参数_型号_发布后再改历史版本仍是旧值()
    {
        var seed = await SeedModelsAsync();
        // 方案里没用到的挂钩型号：不应进快照。
        var unused = Hook("HK-UNUSED");
        unused.Name = "没用到的挂钩";
        Assert.True((await _models.CreateHookModelAsync(unused, Actor, default)).Success);

        var plan = await NewPlanAsync(seed, "入口货架", gapX: 12, gapY: 8);
        plan = await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid));
        // 保存之后、发布之前参数改了：详情里 specChanged=true；快照里取发布时刻的新参数，且 specChanged 清成 false。
        Assert.True((await _specs.SaveAsync("P100", Spec(140), Actor, default)).Success);
        var beforePublish = await GetOkAsync(plan.Header.Guid);
        Assert.All(beforePublish.Items, item => Assert.True(item.SpecChanged));
        Assert.Empty(beforePublish.Conflicts);

        var published = await PublishOkAsync(beforePublish);
        Assert.Equal(PlanogramStatus.Published, published.Header.Status);

        // 发布之后：商品主档、陈列参数、挂钩型号、货架型号全部再改一遍。
        _db.Updateable<Product>()
            .SetColumns(p => p.ProductName == "改名后的收纳盒")
            .SetColumns(p => p.EnglishName == "Renamed Box")
            .SetColumns(p => p.RetailPrice == 9.9m)
            .SetColumns(p => p.ProductImage == "/img/new.jpg")
            .Where(p => p.ProductCode == "P100")
            .ExecuteCommand();
        Assert.True((await _specs.SaveAsync("P100", Spec(160), Actor, default)).Success);
        var hookEdit = seed.Hook;
        hookEdit.Name = "改名后的挂钩";
        hookEdit.ArmLengthMm = 250;
        Assert.True((await _models.UpdateHookModelAsync(seed.Hook.Guid, hookEdit, Actor, default)).Success);
        var shelfEdit = seed.Shelf;
        shelfEdit.Name = "改名后的货架";
        Assert.True((await _models.UpdateShelfModelAsync(seed.Shelf.Guid, shelfEdit, Actor, default)).Success);

        // 现在的详情是新值（对照）。
        var now = await GetOkAsync(plan.Header.Guid);
        Assert.Equal("改名后的收纳盒", now.ProductSpecs.Single().ProductName);
        Assert.Equal(9.9m, now.ProductSpecs.Single().RetailPrice);

        // 历史版本 v1 仍是发布时刻的值。
        var snapshot = await GetVersionOkAsync(plan.Header.Guid, 1);
        Assert.Equal(1, snapshot.Version);
        Assert.Equal("boss", snapshot.PublishedBy);
        Assert.True(snapshot.PublishedAt >= Now);
        Assert.Equal(plan.Header.Guid, snapshot.Plan.Guid);
        Assert.Equal("入口货架", snapshot.Plan.Name);
        Assert.Equal(1, snapshot.Plan.BayCount);
        Assert.Equal(12, snapshot.Plan.GapXMm);
        Assert.Equal(8, snapshot.Plan.GapYMm);
        Assert.Equal(seed.Shelf.Code, snapshot.ShelfModel.Code);
        Assert.Equal("单面洞洞板 900×1200 · 白", snapshot.ShelfModel.Name);

        var hook = Assert.Single(snapshot.HookModels); // 只含方案用到的挂钩
        Assert.Equal(seed.Hook.Guid, hook.Guid);
        Assert.Equal("双钩带价签 200 · 黑", hook.Name);
        Assert.Equal(200m, hook.ArmLengthMm);

        var product = Assert.Single(snapshot.Products); // 只含方案用到的商品（P200 没用到）
        Assert.Equal("P100", product.ProductCode);
        Assert.Equal("IT-100", product.ItemNumber);
        Assert.Equal("9300000000011", product.Barcode);
        Assert.Equal("挂钩收纳盒", product.ProductName);
        Assert.Equal("Hook Box", product.EnglishName);
        Assert.Equal("/img/p100.jpg", product.ProductImage);
        Assert.Equal(4.5m, product.RetailPrice);
        Assert.Equal(140, product.Spec.WidthMm); // 发布时刻的参数，不是保存时的 120，也不是之后的 160
        Assert.Equal(180, product.Spec.HeightMm);
        Assert.Equal(30, product.Spec.DepthMm);

        Assert.Equal(new[] { "a", "b" }, snapshot.Items.Select(item => item.Guid));
        Assert.All(snapshot.Items, item => Assert.False(item.SpecChanged));
        Assert.Equal(2, snapshot.Stats.ItemCount);
        Assert.Equal(1, snapshot.Stats.SkuCount);
        Assert.Equal(12, snapshot.Stats.UnitCount);
        Assert.Equal(54m, snapshot.Stats.RetailValue);
        Assert.Equal(0, snapshot.Stats.ConflictCount);
    }

    // ================================================================ 发布：拒绝

    [Fact]
    public async Task 发布_有error级冲突被拒绝_details带冲突列表_不写快照_头与rowVersion都不变()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        // a、b 同孔 -> HOOK_HOLE_CLASH + OVERLAP；P300 没设参数 -> PRODUCT_SPEC_MISSING。
        var saved = await SaveOkAsync(plan,
            Item("a", "P100", seed.Hook.Guid, 5, 3),
            Item("b", "P100", seed.Hook.Guid, 5, 3),
            Item("c", "P300", seed.Hook.Guid, 20, 20));

        var result = await _plans.PublishAsync(plan.Header.Guid, PublishRequest(saved), "boss", default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.HasConflicts, result.ErrorCode);
        var conflicts = Assert.IsType<List<ConflictDto>>(result.Details);
        Assert.NotEmpty(conflicts);
        Assert.All(conflicts, conflict => Assert.Equal(PlanogramSeverity.Error, conflict.Severity));
        var codes = conflicts.Select(conflict => conflict.Code).Distinct().OrderBy(code => code, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "HOOK_HOLE_CLASH", "OVERLAP", "PRODUCT_SPEC_MISSING" }, codes);
        // a、b 同孔：占孔冲突 + 重叠各成对两条，c 缺参数一条，共 5 条；message 写总数。
        Assert.Equal(saved.Conflicts.Count(conflict => conflict.Severity == PlanogramSeverity.Error), conflicts.Count);
        Assert.Equal(5, conflicts.Count);
        Assert.Contains("5 条冲突", result.Message);
        Assert.Equal(0, await VersionRowCountAsync(plan.Header.Guid));
        var after = await GetOkAsync(plan.Header.Guid);
        Assert.Equal(PlanogramStatus.Draft, after.Header.Status);
        Assert.Null(after.Header.PublishedVersion);
        Assert.Equal(saved.RowVersion, after.RowVersion);
    }

    [Fact]
    public async Task 发布_按存库内容现算冲突_保存时无冲突但之后参数改动造成越界也会被拒绝()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var saved = await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid));
        Assert.Equal(0, (await _db.Queryable<PlanogramHeader>().FirstAsync(p => p.Guid == plan.Header.Guid)).ConflictCount);

        // 商品变宽 300：第一件左边越过板面 -> OUT_OF_BOARD。头上保存的冲突数仍是 0，但发布看的是现算结果。
        Assert.True((await _specs.SaveAsync("P100", Spec(300), Actor, default)).Success);
        var result = await _plans.PublishAsync(plan.Header.Guid, PublishRequest(saved), "boss", default);

        Assert.Equal(PlanogramErrorCodes.HasConflicts, result.ErrorCode);
        Assert.Contains(Assert.IsType<List<ConflictDto>>(result.Details), conflict => conflict.Code == "OUT_OF_BOARD");
        Assert.Equal(0, await VersionRowCountAsync(plan.Header.Guid));
    }

    [Fact]
    public async Task 发布_空方案返回EMPTY()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);

        var result = await _plans.PublishAsync(plan.Header.Guid, PublishRequest(plan), "boss", default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.Empty, result.ErrorCode);
        Assert.Equal(0, await VersionRowCountAsync(plan.Header.Guid));
    }

    [Fact]
    public async Task 发布_已归档返回ARCHIVED_已发布后归档同样不能再发布_恢复后可发布()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var saved = await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid));
        var published = await PublishOkAsync(saved);

        Assert.True((await _plans.ArchiveAsync(plan.Header.Guid, Actor, default)).Success);
        var archived = await GetOkAsync(plan.Header.Guid);
        var result = await _plans.PublishAsync(plan.Header.Guid, PublishRequest(archived), "boss", default);
        Assert.Equal(PlanogramErrorCodes.Archived, result.ErrorCode);
        // 判定顺序：已归档先于 rowVersion，用旧令牌也是 ARCHIVED。
        Assert.Equal(
            PlanogramErrorCodes.Archived,
            (await _plans.PublishAsync(plan.Header.Guid, PublishRequest(published), "boss", default)).ErrorCode);
        Assert.Equal(1, await VersionRowCountAsync(plan.Header.Guid));
    }

    [Fact]
    public async Task 发布_不存在返回NOT_FOUND_rowVersion缺失是校验错误_过期或格式不对返回STALE()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var saved = await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid));

        Assert.Equal(PlanogramErrorCodes.NotFound, (await _plans.PublishAsync("missing", PublishRequest(saved), "boss", default)).ErrorCode);
        Assert.Equal(PlanogramErrorCodes.NotFound, (await _plans.PublishAsync("  ", PublishRequest(saved), "boss", default)).ErrorCode);

        var missing = await _plans.PublishAsync(plan.Header.Guid, new PlanogramPublishRequest(), "boss", default);
        Assert.Equal(PlanogramErrorCodes.Validation, missing.ErrorCode);
        Assert.Equal("rowVersion", Assert.Single(FieldErrors(missing.Details)).Field);

        // 用保存之前的旧令牌（plan.RowVersion）发布：过期。
        Assert.Equal(PlanogramErrorCodes.Stale, (await _plans.PublishAsync(plan.Header.Guid, PublishRequest(plan), "boss", default)).ErrorCode);
        Assert.Equal(
            PlanogramErrorCodes.Stale,
            (await _plans.PublishAsync(plan.Header.Guid, new PlanogramPublishRequest { RowVersion = "not-a-token" }, "boss", default)).ErrorCode);
        Assert.Equal(0, await VersionRowCountAsync(plan.Header.Guid));
    }

    [Fact]
    public async Task 发布_当前版本已有快照_未改动再次发布返回ALREADY_PUBLISHED_不重复写快照_旧令牌按过期处理()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var saved = await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid));
        var published = await PublishOkAsync(saved);

        var again = await _plans.PublishAsync(plan.Header.Guid, PublishRequest(published), "boss", default);

        Assert.False(again.Success);
        Assert.Equal(PlanogramErrorCodes.AlreadyPublished, again.ErrorCode);
        Assert.Equal(1, await VersionRowCountAsync(plan.Header.Guid));
        Assert.Equal(published.RowVersion, (await GetOkAsync(plan.Header.Guid)).RowVersion);

        // 用发布前的旧令牌再点一次（双击）：rowVersion 先判，按过期处理。
        Assert.Equal(PlanogramErrorCodes.Stale, (await _plans.PublishAsync(plan.Header.Guid, PublishRequest(saved), "boss", default)).ErrorCode);
        Assert.Equal(1, await VersionRowCountAsync(plan.Header.Guid));
    }

    // ================================================================ 发布：版本递进（保存 / 归档 / 恢复与唯一性）

    [Fact]
    public async Task 发布v1_修改保存变v2草稿_再发布v2_两个快照都能读出且内容不同()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed, "入口货架");
        var saved = await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid));
        var v1 = await PublishOkAsync(saved);
        Assert.Equal(1, v1.Header.PublishedVersion);

        // 修改并保存：版本号 +1、回到草稿；最近发布版本仍是 1；列表上标「有未发布改动」。
        var edited = await SaveOkAsync(
            v1,
            Item("a", "P100", seed.Hook.Guid, 6, 3),
            Item("c", "P200", seed.Hook.Guid, 20, 10, qty: 2));
        Assert.Equal(PlanogramStatus.Draft, edited.Header.Status);
        Assert.Equal(2, edited.Header.Version);
        Assert.Equal(1, edited.Header.PublishedVersion);
        Assert.True(edited.Header.HasUnpublishedChanges);

        var v2 = await PublishOkAsync(edited);
        Assert.Equal(PlanogramStatus.Published, v2.Header.Status);
        Assert.Equal(2, v2.Header.Version);
        Assert.Equal(2, v2.Header.PublishedVersion);
        Assert.False(v2.Header.HasUnpublishedChanges);

        // 两个快照都在，内容不同。
        var first = await GetVersionOkAsync(plan.Header.Guid, 1);
        var second = await GetVersionOkAsync(plan.Header.Guid, 2);
        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.Equal(new[] { "a", "b" }, first.Items.Select(item => item.Guid));
        Assert.Equal(new[] { "a", "c" }, second.Items.Select(item => item.Guid));
        Assert.Equal(5, first.Items.Single(item => item.Guid == "a").HoleCol);
        Assert.Equal(6, second.Items.Single(item => item.Guid == "a").HoleCol);
        Assert.Equal(new[] { "P100" }, first.Products.Select(product => product.ProductCode));
        Assert.Equal(new[] { "P100", "P200" }, second.Products.Select(product => product.ProductCode));
        Assert.Equal(12, first.Stats.UnitCount);
        Assert.Equal(6 + 2, second.Stats.UnitCount);
        Assert.Equal(2, await VersionRowCountAsync(plan.Header.Guid));

        // 再改再发：v3。
        var v3 = await PublishOkAsync(await SaveOkAsync(v2, Item("z", "P200", seed.Hook.Guid, 9, 9)));
        Assert.Equal(3, v3.Header.PublishedVersion);
        Assert.Equal(3, await VersionRowCountAsync(plan.Header.Guid));
        Assert.Equal(new[] { "z" }, (await GetVersionOkAsync(plan.Header.Guid, 3)).Items.Select(item => item.Guid));
    }

    [Fact]
    public async Task 归档后恢复_发布过的方案版本号高于最近发布版本_可以直接发布新版本不撞唯一索引()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var v1 = await PublishOkAsync(await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid)));
        Assert.Equal(1, v1.Header.Version);

        Assert.True((await _plans.ArchiveAsync(plan.Header.Guid, Actor, default)).Success);
        var restored = await _plans.RestoreAsync(plan.Header.Guid, Actor, default);
        Assert.Equal(PlanogramStatus.Draft, restored.Data!.Status);
        Assert.Equal(2, restored.Data.Version); // 恢复时 version 至少为 publishedVersion+1
        Assert.True(restored.Data.HasUnpublishedChanges);

        var v2 = await PublishOkAsync(await GetOkAsync(plan.Header.Guid));
        Assert.Equal(2, v2.Header.PublishedVersion);
        Assert.Equal(
            new[] { 2, 1 },
            (await _plans.ListVersionsAsync(plan.Header.Guid, default)).Data!.Select(row => row.Version));
    }

    [Fact]
    public async Task 草稿v2归档再恢复_版本号保持2_发布得v2_从未发布的方案归档恢复后发布得v1()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var v1 = await PublishOkAsync(await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid)));
        var draft2 = await SaveOkAsync(v1, Item("a", "P100", seed.Hook.Guid, 6, 3));
        Assert.Equal(2, draft2.Header.Version);

        Assert.True((await _plans.ArchiveAsync(plan.Header.Guid, Actor, default)).Success);
        Assert.True((await _plans.RestoreAsync(plan.Header.Guid, Actor, default)).Success);
        var restored = await GetOkAsync(plan.Header.Guid);
        Assert.Equal(2, restored.Header.Version);
        Assert.Equal(2, (await PublishOkAsync(restored)).Header.PublishedVersion);

        // 从未发布的方案：归档 / 恢复不动版本号，首次发布得 v1。
        var fresh = await NewPlanAsync(seed, "另一个方案");
        var freshSaved = await SaveOkAsync(fresh, TwoCleanItems(seed.Hook.Guid, "f"));
        Assert.True((await _plans.ArchiveAsync(fresh.Header.Guid, Actor, default)).Success);
        Assert.True((await _plans.RestoreAsync(fresh.Header.Guid, Actor, default)).Success);
        var freshRestored = await GetOkAsync(fresh.Header.Guid);
        Assert.Equal(1, freshRestored.Header.Version);
        Assert.NotEqual(freshSaved.RowVersion, freshRestored.RowVersion);
        Assert.Equal(1, (await PublishOkAsync(freshRestored)).Header.PublishedVersion);
    }

    [Fact]
    public async Task 异常状态兜底_草稿版本号不高于最近发布版本时_保存会把版本号推到发布版本加1_之后可发布()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var v1 = await PublishOkAsync(await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid)));

        // 直连库造出异常状态：草稿，但版本号 == 最近发布版本号（v1 快照已存在）。
        _db.Updateable<PlanogramHeader>().SetColumns(p => p.Status == (byte)0).Where(p => p.Guid == plan.Header.Guid).ExecuteCommand();
        var broken = await GetOkAsync(plan.Header.Guid);
        Assert.Equal(1, broken.Header.Version);
        Assert.Equal(1, broken.Header.PublishedVersion);
        Assert.Equal(PlanogramErrorCodes.AlreadyPublished, (await _plans.PublishAsync(plan.Header.Guid, PublishRequest(broken), "boss", default)).ErrorCode);

        var healed = await SaveOkAsync(broken, Item("a", "P100", seed.Hook.Guid, 6, 3));
        Assert.Equal(2, healed.Header.Version);
        Assert.Equal(2, (await PublishOkAsync(healed)).Header.PublishedVersion);
        Assert.Equal(v1.Header.Guid, plan.Header.Guid);
        Assert.Equal(2, await VersionRowCountAsync(plan.Header.Guid));
    }

    // ================================================================ 发布：并发

    [Fact]
    public async Task 发布_并发下唯一索引冲突转成ALREADY_PUBLISHED_不抛500_头不变_快照只有别人写的那一份()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var saved = await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid));

        // 预检（还没有 v1 快照）通过之后、写入之前，另一个发布者已经把 v1 快照提交了。
        var racing = new RacingTime(Now);
        var racingService = new PlanogramPlanService(_db, racing);
        racing.BeforeNextRead = () => _db.Insertable(new PlanogramVersion
        {
            PlanogramGuid = plan.Header.Guid, Version = 1, LayoutJson = "{\"rival\":true}", PublishedBy = "rival", PublishedAt = Now,
        }).ExecuteCommand();

        var result = await racingService.PublishAsync(plan.Header.Guid, PublishRequest(saved), "boss", default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.AlreadyPublished, result.ErrorCode);
        var rows = await _db.Queryable<PlanogramVersion>().Where(row => row.PlanogramGuid == plan.Header.Guid).ToListAsync();
        Assert.Equal("rival", Assert.Single(rows).PublishedBy);
        var header = await _db.Queryable<PlanogramHeader>().FirstAsync(p => p.Guid == plan.Header.Guid);
        Assert.Equal(PlanogramStatus.Draft, header.Status);
        Assert.Null(header.PublishedVersion);
        Assert.Equal(saved.RowVersion, PlanogramPlanService.ToRowVersion(header.UpdatedAt));
        // 连接和事务状态正常：之后的读写不受影响。
        Assert.True((await _plans.SaveAsync(plan.Header.Guid, SaveRequest(saved, Item("a", "P100", seed.Hook.Guid, 5, 3)), Actor, default)).Success);
    }

    [Fact]
    public async Task 发布_预检之后别人抢先保存了方案_头的条件更新0行_整体回滚报STALE_不留快照()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var saved = await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid));

        var racing = new RacingTime(Now);
        var racingService = new PlanogramPlanService(_db, racing);
        var rivalUpdatedAt = Now.AddMinutes(5);
        racing.BeforeNextRead = () => _db.Updateable<PlanogramHeader>()
            .SetColumns(p => p.UpdatedAt == rivalUpdatedAt)
            .SetColumns(p => p.Name == "别人改的名字")
            .Where(p => p.Guid == plan.Header.Guid)
            .ExecuteCommand();

        var result = await racingService.PublishAsync(plan.Header.Guid, PublishRequest(saved), "boss", default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.Stale, result.ErrorCode);
        Assert.Equal(0, await VersionRowCountAsync(plan.Header.Guid)); // 快照随事务回滚
        var header = await _db.Queryable<PlanogramHeader>().FirstAsync(p => p.Guid == plan.Header.Guid);
        Assert.Equal("别人改的名字", header.Name);
        Assert.Equal(PlanogramStatus.Draft, header.Status);
        Assert.Null(header.PublishedVersion);
    }

    // ================================================================ 版本列表 / 历史版本

    [Fact]
    public async Task 版本列表_按版本倒序_带发布人发布时间和统计_没发布过是空数组_方案不存在NOT_FOUND()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        Assert.Empty((await _plans.ListVersionsAsync(plan.Header.Guid, default)).Data!);
        Assert.Equal(PlanogramErrorCodes.NotFound, (await _plans.ListVersionsAsync("missing", default)).ErrorCode);

        var v1 = await PublishOkAsync(await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid)), "boss");
        var v2 = await PublishOkAsync(
            await SaveOkAsync(v1, Item("a", "P100", seed.Hook.Guid, 5, 3), Item("b", "P200", seed.Hook.Guid, 13, 3), Item("c", "P200", seed.Hook.Guid, 20, 10)),
            "carol");

        var list = (await _plans.ListVersionsAsync(plan.Header.Guid, default)).Data!;

        Assert.Equal(new[] { 2, 1 }, list.Select(row => row.Version));
        Assert.Equal(new[] { "carol", "boss" }, list.Select(row => row.PublishedBy));
        Assert.Equal(new[] { 3, 2 }, list.Select(row => row.ItemCount));
        Assert.Equal(new[] { 2, 1 }, list.Select(row => row.SkuCount));
        Assert.Equal(new[] { 6 + 6 + 6, 12 }, list.Select(row => row.UnitCount)); // P200 100 宽，厚 30 -> 同样每钩 6 件
        Assert.True(list[0].PublishedAt > list[1].PublishedAt);
        Assert.Equal(v2.Header.PublishedAt, list[0].PublishedAt);
    }

    [Fact]
    public async Task 历史版本_方案或版本不存在返回NOT_FOUND_版本号以版本表为准()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        await PublishOkAsync(await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid)), "boss");

        Assert.Equal(PlanogramErrorCodes.NotFound, (await _plans.GetVersionAsync("missing", 1, default)).ErrorCode);
        foreach (var version in new[] { 0, -1, 2, 99 })
        {
            var result = await _plans.GetVersionAsync(plan.Header.Guid, version, default);
            Assert.Equal(PlanogramErrorCodes.NotFound, result.ErrorCode);
        }

        // 发布人以版本表这一行为准（与版本列表一致）。
        _db.Updateable<PlanogramVersion>().SetColumns(row => row.PublishedBy == "someone-else").Where(row => row.PlanogramGuid == plan.Header.Guid).ExecuteCommand();
        Assert.Equal("someone-else", (await GetVersionOkAsync(plan.Header.Guid, 1)).PublishedBy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"plan\":{\"guid\":\"p\"}}")]
    [InlineData("{\"plan\":{\"guid\":\"p\"},\"shelfModel\":{\"guid\":\"s\"},\"items\":\"oops\"}")]
    public async Task 历史版本_快照内容读不出来_返回明确的SNAPSHOT_INVALID_不抛异常_列表里仍出现该版本(string layoutJson)
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        _db.Insertable(new PlanogramVersion
        {
            PlanogramGuid = plan.Header.Guid, Version = 5, LayoutJson = layoutJson, PublishedBy = "boss", PublishedAt = Now,
        }).ExecuteCommand();

        var result = await _plans.GetVersionAsync(plan.Header.Guid, 5, default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.SnapshotInvalid, result.ErrorCode);
        Assert.Contains("v5", result.Message);
        var list = (await _plans.ListVersionsAsync(plan.Header.Guid, default)).Data!;
        var row = Assert.Single(list);
        Assert.Equal(5, row.Version);
        Assert.Equal(0, row.ItemCount);
    }

    [Fact]
    public async Task 历史版本_JSON里显式写null的集合读成空集合_统计字段单独可读()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        _db.Insertable(new PlanogramVersion
        {
            PlanogramGuid = plan.Header.Guid, Version = 1, PublishedBy = "boss", PublishedAt = Now,
            LayoutJson = "{\"plan\":{\"guid\":\"p\"},\"shelfModel\":{\"guid\":\"s\"},\"items\":null,\"hookModels\":null,\"products\":null,\"stats\":null}",
        }).ExecuteCommand();
        _db.Insertable(new PlanogramVersion
        {
            PlanogramGuid = plan.Header.Guid, Version = 2, PublishedBy = "boss", PublishedAt = Now,
            LayoutJson = "{\"stats\":{\"itemCount\":3,\"skuCount\":2,\"unitCount\":9}}",
        }).ExecuteCommand();

        var loaded = await GetVersionOkAsync(plan.Header.Guid, 1);
        Assert.Empty(loaded.Items);
        Assert.Empty(loaded.HookModels);
        Assert.Empty(loaded.Products);
        Assert.NotNull(loaded.Stats);

        // v2 整体不是完整快照（读取报错），但列表仍能读出它的统计。
        Assert.Equal(PlanogramErrorCodes.SnapshotInvalid, (await _plans.GetVersionAsync(plan.Header.Guid, 2, default)).ErrorCode);
        var second = (await _plans.ListVersionsAsync(plan.Header.Guid, default)).Data!.Single(row => row.Version == 2);
        Assert.Equal((3, 2, 9), (second.ItemCount, second.SkuCount, second.UnitCount));
    }

    // ================================================================ 列表：hasUnpublishedChanges

    [Fact]
    public async Task 列表_hasUnpublishedChanges只在草稿且发布过时为true()
    {
        var seed = await SeedModelsAsync();
        var neverPublished = await NewPlanAsync(seed, "从未发布");
        var published = await NewPlanAsync(seed, "已发布");
        var edited = await NewPlanAsync(seed, "发布后又改");
        var archived = await NewPlanAsync(seed, "已归档");

        await PublishOkAsync(await SaveOkAsync(published, TwoCleanItems(seed.Hook.Guid, "p")));
        var editedPublished = await PublishOkAsync(await SaveOkAsync(edited, TwoCleanItems(seed.Hook.Guid, "e")));
        await SaveOkAsync(editedPublished, Item("ea", "P100", seed.Hook.Guid, 6, 3));
        await PublishOkAsync(await SaveOkAsync(archived, TwoCleanItems(seed.Hook.Guid, "r")));
        Assert.True((await _plans.ArchiveAsync(archived.Header.Guid, Actor, default)).Success);

        var page = (await _plans.ListAsync(null, null, null, 1, 50, default)).Data!;
        bool Flag(string name) => page.Items!.Single(row => row.Name == name).HasUnpublishedChanges;

        Assert.False(Flag("从未发布"));
        Assert.False(Flag("已发布"));
        Assert.True(Flag("发布后又改"));
        Assert.False(Flag("已归档"));
        Assert.Equal(neverPublished.Header.Guid, page.Items!.Single(row => row.Name == "从未发布").Guid);
    }

    // ================================================================ 导出

    [Fact]
    public async Task 导出_用型号编码和商品货号_不带任何guid_已归档方案也能导出_不存在返回NOT_FOUND()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed, "入口货架", bayCount: 2, gapX: 12, gapY: 8);
        var planWithNotes = SaveRequest(plan,
            Item("a", "P100", seed.Hook.Guid, 5, 3),
            Item("b", "P200", seed.Hook.Guid, 13, 3, bay: 2, qty: 4, sort: 1));
        planWithNotes.Notes = "第一排挂勾";
        Assert.True((await _plans.SaveAsync(plan.Header.Guid, planWithNotes, Actor, default)).Success);

        var exported = await _plans.ExportAsync(plan.Header.Guid, default);

        Assert.True(exported.Success, exported.Message);
        var file = exported.Data!;
        Assert.Equal(1, file.FormatVersion);
        Assert.Equal(Now, file.ExportedAt);
        Assert.Equal("入口货架", file.Plan!.Name);
        Assert.Equal("第一排挂勾", file.Plan.Notes);
        Assert.Equal(2, file.Plan.BayCount);
        Assert.Equal(12, file.Plan.GapXMm);
        Assert.Equal(8, file.Plan.GapYMm);
        Assert.Equal(seed.Shelf.Code, file.ShelfModelCode);
        Assert.Equal(2, file.Items!.Count);
        var second = file.Items.Single(item => item.ProductCode == "P200");
        Assert.Equal((2, seed.Hook.Code, 13, 3, (int?)4), (second.BayIndex, second.HookModelCode, second.HoleCol, second.HoleRow, second.PerHookQty));
        Assert.Null(file.Items.Single(item => item.ProductCode == "P100").PerHookQty);

        var json = JsonSerializer.Serialize(file, WebJson);
        Assert.DoesNotContain(seed.Shelf.Guid, json);
        Assert.DoesNotContain(seed.Hook.Guid, json);
        Assert.DoesNotContain(plan.Header.Guid, json);

        Assert.True((await _plans.ArchiveAsync(plan.Header.Guid, Actor, default)).Success);
        Assert.True((await _plans.ExportAsync(plan.Header.Guid, default)).Success);
        Assert.Equal(PlanogramErrorCodes.NotFound, (await _plans.ExportAsync("missing", default)).ErrorCode);
    }

    [Fact]
    public async Task 导出_引用的挂钩型号被直连库删除时_明确报错而不是导出残缺文件()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid));
        _db.Deleteable<PlanogramHookModel>().Where(hook => hook.Guid == seed.Hook.Guid).ExecuteCommand();

        var result = await _plans.ExportAsync(plan.Header.Guid, default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.NotFound, result.ErrorCode);
        Assert.Contains("挂钩型号", result.Message);
    }

    // ================================================================ 导入导出往返

    internal static PlanogramImportRequest ThroughJson(PlanogramExportDto file) =>
        // 模拟前端：导出结果存成文件，再原样作为导入请求体读回来。
        JsonSerializer.Deserialize<PlanogramImportRequest>(JsonSerializer.Serialize(file, WebJson), WebJson)!;

    private static (int Bay, string Product, string Hook, int Col, int Row, int? Qty, int Sort)[] ItemShape(IEnumerable<PlanogramItemDto> items) =>
        items.Select(item => (item.BayIndex, item.ProductCode, item.HookModelGuid, item.HoleCol, item.HoleRow, item.PerHookQty, item.SortOrder)).ToArray();

    [Fact]
    public async Task 导出再导入_得到同样的明细_统计_冲突_货架和间距_新方案是version1的草稿且明细guid全新()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed, "入口货架", bayCount: 2, gapX: 12, gapY: 8);
        var request = SaveRequest(plan,
            Item("a", "P100", seed.Hook.Guid, 5, 3),
            Item("b", "P100", seed.Hook.Guid, 13, 3, qty: 4, sort: 1),
            Item("c", "P200", seed.Hook.Guid, 5, 10, bay: 2, sort: 2),
            Item("d", "P100", seed.Hook.Guid, 13, 3, sort: 3), // 与 b 同孔：保留冲突
            Item("e", "P300", seed.Hook.Guid, 20, 20, bay: 2, sort: 4)); // 缺参数
        request.Notes = "第一排挂勾";
        var original = (await _plans.SaveAsync(plan.Header.Guid, request, Actor, default)).Data!;
        Assert.NotEmpty(original.Conflicts);

        var file = ThroughJson((await _plans.ExportAsync(plan.Header.Guid, default)).Data!);
        var imported = await _plans.ImportAsync(file, "dave", default);

        Assert.True(imported.Success, imported.Message + " " + JsonSerializer.Serialize(imported.Details));
        var copy = imported.Data!;
        Assert.NotEqual(original.Header.Guid, copy.Header.Guid);
        Assert.Equal("入口货架", copy.Header.Name);
        Assert.Equal("第一排挂勾", copy.Header.Notes);
        Assert.Equal(PlanogramStatus.Draft, copy.Header.Status);
        Assert.Equal(1, copy.Header.Version);
        Assert.Null(copy.Header.PublishedVersion);
        Assert.False(copy.Header.HasUnpublishedChanges);
        Assert.Equal(original.Header.ShelfModelGuid, copy.Header.ShelfModelGuid);
        Assert.Equal((2, 12, 8), (copy.Header.BayCount, copy.Header.GapXMm, copy.Header.GapYMm));
        Assert.Equal("dave", copy.Header.UpdatedBy);

        // 同样的明细（按文件顺序，sortOrder 重新编号），guid 全新。
        Assert.Equal(
            ItemShape(original.Items.Select((item, index) => { item.SortOrder = index; return item; })),
            ItemShape(copy.Items));
        Assert.Empty(copy.Items.Select(item => item.Guid).Intersect(original.Items.Select(item => item.Guid)));
        Assert.All(copy.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.Guid)));

        // 同样的统计与冲突。
        Assert.Equal(JsonSerializer.Serialize(original.Stats, WebJson), JsonSerializer.Serialize(copy.Stats, WebJson));
        Assert.Equal(original.Header.ConflictCount, copy.Header.ConflictCount);
        Assert.Equal(
            original.Conflicts.Select(conflict => (conflict.Code, conflict.Severity)).OrderBy(x => x).ToArray(),
            copy.Conflicts.Select(conflict => (conflict.Code, conflict.Severity)).OrderBy(x => x).ToArray());

        var row = await _db.Queryable<PlanogramHeader>().FirstAsync(p => p.Guid == copy.Header.Guid);
        Assert.Equal("dave", row.CreatedBy);
        Assert.Equal(original.Header.ConflictCount, row.ConflictCount);
        // 原方案没有被动过。
        Assert.Equal(original.RowVersion, (await GetOkAsync(plan.Header.Guid)).RowVersion);
    }

    [Fact]
    public async Task 导入_name可覆盖方案名称_空白name沿用文件里的名称_同一个文件可以导入多次()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed, "入口货架");
        await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid));
        var file = ThroughJson((await _plans.ExportAsync(plan.Header.Guid, default)).Data!);

        file.Name = "  门店 A 副本  ";
        var renamed = (await _plans.ImportAsync(file, Actor, default)).Data!;
        Assert.Equal("门店 A 副本", renamed.Header.Name);

        file.Name = "   ";
        var kept = (await _plans.ImportAsync(file, Actor, default)).Data!;
        Assert.Equal("入口货架", kept.Header.Name);

        file.Name = null;
        var again = (await _plans.ImportAsync(file, Actor, default)).Data!;
        Assert.Equal(4, (await _plans.ListAsync(null, null, null, 1, 50, default)).Data!.Total);
        Assert.NotEqual(kept.Header.Guid, again.Header.Guid);
        Assert.Empty(kept.Items.Select(item => item.Guid).Intersect(again.Items.Select(item => item.Guid)));
    }

    [Fact]
    public async Task 导入_编码不区分大小写_缺省间距按10处理_空明细也可导入()
    {
        var seed = await SeedModelsAsync();
        var file = new PlanogramImportRequest
        {
            FormatVersion = 1,
            ShelfModelCode = seed.Shelf.Code.ToLowerInvariant(),
            Plan = new PlanogramExportPlanDto { Name = "手写方案", BayCount = 1 },
            Items = new List<PlanogramExportItemDto>
            {
                new() { ProductCode = "P100", HookModelCode = seed.Hook.Code.ToLowerInvariant(), HoleCol = 5, HoleRow = 3 },
            },
        };

        var result = await _plans.ImportAsync(file, Actor, default);

        Assert.True(result.Success, result.Message + " " + JsonSerializer.Serialize(result.Details));
        Assert.Equal((10, 10), (result.Data!.Header.GapXMm, result.Data.Header.GapYMm));
        Assert.Equal(seed.Hook.Guid, Assert.Single(result.Data.Items).HookModelGuid);

        file.Items = new List<PlanogramExportItemDto>();
        var empty = await _plans.ImportAsync(file, Actor, default);
        Assert.True(empty.Success, empty.Message);
        Assert.Empty(empty.Data!.Items);
    }

    [Fact]
    public async Task 导入_解析不到的编码一次返回_去重_PLANOGRAM_IMPORT_UNRESOLVED_且不创建任何方案()
    {
        var seed = await SeedModelsAsync();
        var file = new PlanogramImportRequest
        {
            FormatVersion = 1,
            ShelfModelCode = "NO-SUCH-SHELF",
            Plan = new PlanogramExportPlanDto { Name = "搬运来的方案", BayCount = 1 },
            Items = new List<PlanogramExportItemDto>
            {
                new() { ProductCode = "ZZ1", HookModelCode = "HK-X", HoleCol = 1, HoleRow = 1 },
                new() { ProductCode = "ZZ1", HookModelCode = "HK-X", HoleCol = 3, HoleRow = 1 },
                new() { ProductCode = "ZZ2", HookModelCode = "hk-x", HoleCol = 5, HoleRow = 1 }, // 大小写不同的同一个编码：去重
                new() { ProductCode = "P100", HookModelCode = seed.Hook.Code, HoleCol = 7, HoleRow = 1 }, // 能解析的不报
                new() { ProductCode = "P100", HookModelCode = "HK-Y", HoleCol = 9, HoleRow = 1 },
            },
        };

        var result = await _plans.ImportAsync(file, Actor, default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.ImportUnresolved, result.ErrorCode);
        var details = Assert.IsType<List<PlanogramImportUnresolvedDto>>(result.Details);
        Assert.Equal(
            new[]
            {
                new PlanogramImportUnresolvedDto("shelf", "NO-SUCH-SHELF"),
                new PlanogramImportUnresolvedDto("hook", "HK-X"),
                new PlanogramImportUnresolvedDto("hook", "HK-Y"),
                new PlanogramImportUnresolvedDto("product", "ZZ1"),
                new PlanogramImportUnresolvedDto("product", "ZZ2"),
            },
            details.ToArray());
        Assert.Equal(0, await _db.Queryable<PlanogramHeader>().CountAsync());
        Assert.Equal(0, await _db.Queryable<PlanogramItem>().CountAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public async Task 导入_formatVersion不认识_返回PLANOGRAM_VALIDATION_且指向formatVersion字段(int formatVersion)
    {
        var seed = await SeedModelsAsync();
        var file = new PlanogramImportRequest
        {
            FormatVersion = formatVersion,
            ShelfModelCode = seed.Shelf.Code,
            Plan = new PlanogramExportPlanDto { Name = "x", BayCount = 1 },
            Items = new List<PlanogramExportItemDto>(),
        };

        var result = await _plans.ImportAsync(file, Actor, default);

        Assert.Equal(PlanogramErrorCodes.Validation, result.ErrorCode);
        Assert.Equal("formatVersion", Assert.Single(FieldErrors(result.Details)).Field);
        Assert.Equal(0, await _db.Queryable<PlanogramHeader>().CountAsync());

        // 请求体缺失按空对象处理（formatVersion 缺省为 0 -> 同样的校验错误）。
        Assert.Equal(PlanogramErrorCodes.Validation, (await _plans.ImportAsync(new PlanogramImportRequest { FormatVersion = 0 }, Actor, default)).ErrorCode);
    }

    [Fact]
    public async Task 导入_缺少plan_items_货架编码_或明细超过2000条_都是校验错误()
    {
        var seed = await SeedModelsAsync();
        var empty = await _plans.ImportAsync(new PlanogramImportRequest { FormatVersion = 1 }, Actor, default);
        Assert.Equal(PlanogramErrorCodes.Validation, empty.ErrorCode);
        Assert.Equal(
            new[] { "items", "plan", "shelfModelCode" },
            FieldErrors(empty.Details).Select(error => error.Field).OrderBy(name => name, StringComparer.Ordinal));

        var tooMany = new PlanogramImportRequest
        {
            FormatVersion = 1,
            ShelfModelCode = seed.Shelf.Code,
            Plan = new PlanogramExportPlanDto { Name = "x", BayCount = 1 },
            Items = Enumerable.Range(0, 2001)
                .Select(i => new PlanogramExportItemDto { ProductCode = "P100", HookModelCode = seed.Hook.Code, HoleCol = 1, HoleRow = 1 })
                .ToList(),
        };
        var rejected = await _plans.ImportAsync(tooMany, Actor, default);
        Assert.Equal(PlanogramErrorCodes.Validation, rejected.ErrorCode);
        var error = Assert.Single(FieldErrors(rejected.Details));
        Assert.Equal("items", error.Field);
        Assert.Contains("2000", error.Message);
        Assert.Equal(0, await _db.Queryable<PlanogramHeader>().CountAsync());
    }

    [Fact]
    public async Task 导入_复用保存校验_错误字段名指向导入文件里的位置()
    {
        var seed = await SeedModelsAsync();
        var file = new PlanogramImportRequest
        {
            FormatVersion = 1,
            ShelfModelCode = seed.Shelf.Code,
            Plan = new PlanogramExportPlanDto { Name = "  ", Notes = new string('n', 501), BayCount = 13, GapXMm = -1, GapYMm = 500 },
            Items = new List<PlanogramExportItemDto>
            {
                new() { ProductCode = "P100", HookModelCode = seed.Hook.Code, HoleCol = 5, HoleRow = 3 },
                new() { ProductCode = "", HookModelCode = "", HoleCol = 5, HoleRow = 3 },
                new() { ProductCode = "P100", HookModelCode = seed.Hook.Code, HoleCol = 5, HoleRow = 3, PerHookQty = 0 },
            },
        };

        var result = await _plans.ImportAsync(file, Actor, default);

        Assert.Equal(PlanogramErrorCodes.Validation, result.ErrorCode);
        Assert.Equal(
            new[]
            {
                "items[1].hookModelCode", "items[1].productCode", "items[2].perHookQty",
                "plan.bayCount", "plan.gapXMm", "plan.gapYMm", "plan.name", "plan.notes",
            },
            FieldErrors(result.Details).Select(error => error.Field).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(0, await _db.Queryable<PlanogramHeader>().CountAsync());

        // 覆盖名称过长时错误指向顶层 name；停用的货架型号指向 shelfModelCode。
        var good = new PlanogramImportRequest
        {
            FormatVersion = 1,
            ShelfModelCode = seed.Shelf.Code,
            Name = new string('x', 101),
            Plan = new PlanogramExportPlanDto { Name = "x", BayCount = 1 },
            Items = new List<PlanogramExportItemDto>(),
        };
        Assert.Equal("name", Assert.Single(FieldErrors((await _plans.ImportAsync(good, Actor, default)).Details)).Field);

        var disabled = Shelf("G-OFF");
        disabled.IsActive = false;
        Assert.True((await _models.CreateShelfModelAsync(disabled, Actor, default)).Success);
        good.Name = null;
        good.ShelfModelCode = "G-OFF";
        Assert.Equal("shelfModelCode", Assert.Single(FieldErrors((await _plans.ImportAsync(good, Actor, default)).Details)).Field);
    }

    // ================================================================ 大方案（两千条明细）：nvarchar(max) 快照与往返

    /// <summary>
    /// 7 节 × 每节 16 列 × 19 行的小商品网格：商品 20×20、无价签小挂钩，行列间距都远大于最小间距，保证不冲突。
    /// </summary>
    internal static List<PlanogramItemDto> GridItems(string hook, int count)
    {
        const int perBay = 16 * 19;
        var items = new List<PlanogramItemDto>(count);
        for (var i = 0; i < count; i++)
        {
            var slot = i % perBay;
            items.Add(Item($"g{i:D4}", "P100", hook, 1 + slot % 16 * 2, 1 + slot / 16 * 2, i / perBay + 1, sort: i));
        }

        return items;
    }

    private async Task<(Seed Seed, PlanogramDetailDto Plan)> NewGridPlanAsync()
    {
        var shelf = (await _models.CreateShelfModelAsync(Shelf(), Actor, default)).Data!;
        var hook = (await _models.CreateHookModelAsync(SmallHook(), Actor, default)).Data!;
        Assert.True((await _specs.SaveAsync("P100", TinySpec(), Actor, default)).Success);
        var seed = new Seed(shelf, hook);
        return (seed, await NewPlanAsync(seed, "大方案", bayCount: 7));
    }

    [Fact]
    public async Task 两千条明细的大方案_可以发布_快照超过4000字符照样存取_导出再导入数量一致_2001条的文件被拒绝()
    {
        var (seed, plan) = await NewGridPlanAsync();
        var saved = await SaveOkAsync(plan, GridItems(seed.Hook.Guid, 2000).ToArray());
        Assert.Empty(saved.Conflicts);

        var published = await PublishOkAsync(saved);
        Assert.Equal(2000, published.Stats.ItemCount);

        var row = await _db.Queryable<PlanogramVersion>().FirstAsync(r => r.PlanogramGuid == plan.Header.Guid);
        Assert.True(row.LayoutJson.Length > 100_000, $"快照只有 {row.LayoutJson.Length} 字符");
        var snapshot = await GetVersionOkAsync(plan.Header.Guid, 1);
        Assert.Equal(2000, snapshot.Items.Count);
        Assert.Equal(2000, snapshot.Stats.ItemCount);
        Assert.Equal(1, snapshot.Stats.SkuCount);
        var summary = (await _plans.ListVersionsAsync(plan.Header.Guid, default)).Data!.Single();
        Assert.Equal((2000, 1), (summary.ItemCount, summary.SkuCount));

        var file = ThroughJson((await _plans.ExportAsync(plan.Header.Guid, default)).Data!);
        Assert.Equal(2000, file.Items!.Count);
        var imported = await _plans.ImportAsync(file, Actor, default);
        Assert.True(imported.Success, imported.Message);
        Assert.Equal(2000, imported.Data!.Items.Count);
        Assert.Equal(saved.Stats.UnitCount, imported.Data.Stats.UnitCount);
        Assert.Empty(imported.Data.Conflicts);

        file.Items.Add(new PlanogramExportItemDto { ProductCode = "P100", HookModelCode = seed.Hook.Code, HoleCol = 1, HoleRow = 39 });
        Assert.Equal(PlanogramErrorCodes.Validation, (await _plans.ImportAsync(file, Actor, default)).ErrorCode);
    }

    // ================================================================ 控制器：路由 / 权限元数据

    public static IEnumerable<object[]> ControllerContract()
    {
        // 方法名, HTTP 方法, 路由模板, 权限策略（附录 C）
        yield return new object[] { nameof(PlanogramController.PublishPlan), "POST", "planograms/{guid}/publish", Permissions.Planogram.Manage };
        yield return new object[] { nameof(PlanogramController.ListPlanVersions), "GET", "planograms/{guid}/versions", Permissions.Planogram.View };
        yield return new object[] { nameof(PlanogramController.GetPlanVersion), "GET", "planograms/{guid}/versions/{version:int}", Permissions.Planogram.View };
        yield return new object[] { nameof(PlanogramController.ExportPlan), "GET", "planograms/{guid}/export", Permissions.Planogram.Edit };
        yield return new object[] { nameof(PlanogramController.ImportPlan), "POST", "planograms/import", Permissions.Planogram.Edit };
    }

    [Theory]
    [MemberData(nameof(ControllerContract))]
    public void 控制器_批3路由_HTTP方法_权限策略与附录C一致(string methodName, string verb, string template, string policy)
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
    public void 控制器_全部路由的HTTP方法加模板组合唯一_import只有POST_且planograms的POST与import是不同路径()
    {
        var routes = typeof(PlanogramController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: false)
                .SelectMany(http => http.HttpMethods.Select(verb => (Verb: verb, Template: http.Template ?? string.Empty, Method: method.Name))))
            .ToList();

        // 同一个动词 + 同一个模板不能出现两次（否则 ASP.NET Core 报 AmbiguousMatchException）。
        Assert.Empty(routes.GroupBy(route => (route.Verb, route.Template)).Where(group => group.Count() > 1).Select(group => group.Key));

        Assert.Equal(
            new[] { "POST planograms", "POST planograms/import" },
            routes.Where(route => route.Verb == "POST" && route.Template.StartsWith("planograms", StringComparison.Ordinal) && route.Template.Split('/').Length <= 2)
                .Select(route => $"{route.Verb} {route.Template}").OrderBy(text => text, StringComparer.Ordinal));
        // planograms/{guid} 这个单参数模板上没有 POST：import 的字面量路径不会与它抢同一个动词。
        Assert.DoesNotContain(routes, route => route.Verb == "POST" && route.Template == "planograms/{guid}");
        Assert.Equal(
            new[] { "DELETE", "GET", "PUT" },
            routes.Where(route => route.Template == "planograms/{guid}").Select(route => route.Verb).OrderBy(verb => verb, StringComparer.Ordinal));
    }

    [Fact]
    public void 权限别名_Manage满足发布导入导出和版本读取_Edit满足导入导出和版本读取但不满足发布_View只满足版本读取()
    {
        static bool Satisfies(string held, string required) =>
            Permissions.GetEquivalentPermissionCodes(required).Contains(held, StringComparer.OrdinalIgnoreCase);

        foreach (var row in ControllerContract())
        {
            var required = (string)row[3];
            var name = (string)row[0];
            Assert.True(Satisfies(Permissions.Planogram.Manage, required), $"Manage 应满足 {name}");
            var editOk = required != Permissions.Planogram.Manage;
            Assert.Equal(editOk, Satisfies(Permissions.Planogram.Edit, required));
            var viewOk = required == Permissions.Planogram.View;
            Assert.Equal(viewOk, Satisfies(Permissions.Planogram.View, required));
        }

        // 登录时展开：持有 Manage 的账号权限声明里同时有 Edit 和 View，反向不成立。
        var expanded = Permissions.ExpandPermissionCodes(new[] { Permissions.Planogram.Manage });
        Assert.Contains(Permissions.Planogram.Edit, expanded);
        Assert.Contains(Permissions.Planogram.View, expanded);
        var editOnly = Permissions.ExpandPermissionCodes(new[] { Permissions.Planogram.Edit });
        Assert.Contains(Permissions.Planogram.View, editOnly);
        Assert.DoesNotContain(Permissions.Planogram.Manage, editOnly);
    }

    private PlanogramController NewController(string? user = "carol") => new(_models, _specs, _plans)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = user is null
                    ? new ClaimsPrincipal(new ClaimsIdentity())
                    : new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, user) }, "TestAuth")),
            },
        },
    };

    [Fact]
    public async Task 控制器_批3接口_成功200_失败400保留业务错误码_用户名写入发布人_空请求体按空对象处理()
    {
        var seed = await SeedModelsAsync();
        var controller = NewController();
        var plan = await NewPlanAsync(seed, "控制器方案");
        var saved = await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid));

        // 空请求体：rowVersion 缺失 -> 400 PLANOGRAM_VALIDATION，不是 500。
        var nullBody = await controller.PublishPlan(plan.Header.Guid, null, default);
        Assert.Equal(
            PlanogramErrorCodes.Validation,
            Assert.IsType<ApiResponse<PlanogramDetailDto>>(Assert.IsType<BadRequestObjectResult>(nullBody).Value).ErrorCode);

        var published = await controller.PublishPlan(plan.Header.Guid, PublishRequest(saved), default);
        var body = Assert.IsType<ApiResponse<PlanogramDetailDto>>(Assert.IsType<OkObjectResult>(published).Value);
        Assert.Equal(1, body.Data!.Header.PublishedVersion);
        Assert.Equal("carol", (await _db.Queryable<PlanogramVersion>().FirstAsync(row => row.PlanogramGuid == plan.Header.Guid)).PublishedBy);
        Assert.Equal("carol", (await _db.Queryable<PlanogramHeader>().FirstAsync(p => p.Guid == plan.Header.Guid)).PublishedBy);

        var again = await controller.PublishPlan(plan.Header.Guid, PublishRequest(body.Data), default);
        Assert.Equal(
            PlanogramErrorCodes.AlreadyPublished,
            Assert.IsType<ApiResponse<PlanogramDetailDto>>(Assert.IsType<BadRequestObjectResult>(again).Value).ErrorCode);

        var versions = await controller.ListPlanVersions(plan.Header.Guid, default);
        Assert.Single(Assert.IsType<ApiResponse<List<PlanogramVersionSummaryDto>>>(Assert.IsType<OkObjectResult>(versions).Value).Data!);
        var snapshot = await controller.GetPlanVersion(plan.Header.Guid, 1, default);
        Assert.Equal(2, Assert.IsType<ApiResponse<PlanogramSnapshotDto>>(Assert.IsType<OkObjectResult>(snapshot).Value).Data!.Items.Count);
        var missingVersion = await controller.GetPlanVersion(plan.Header.Guid, 9, default);
        Assert.Equal(
            PlanogramErrorCodes.NotFound,
            Assert.IsType<ApiResponse<PlanogramSnapshotDto>>(Assert.IsType<BadRequestObjectResult>(missingVersion).Value).ErrorCode);

        var exported = await controller.ExportPlan(plan.Header.Guid, default);
        var file = Assert.IsType<ApiResponse<PlanogramExportDto>>(Assert.IsType<OkObjectResult>(exported).Value).Data!;
        Assert.Equal(2, file.Items!.Count);
        var imported = await controller.ImportPlan(ThroughJson(file), default);
        var importedBody = Assert.IsType<ApiResponse<PlanogramDetailDto>>(Assert.IsType<OkObjectResult>(imported).Value);
        Assert.Equal("carol", importedBody.Data!.Header.UpdatedBy);

        var nullImport = await controller.ImportPlan(null, default);
        Assert.Equal(
            PlanogramErrorCodes.Validation,
            Assert.IsType<ApiResponse<PlanogramDetailDto>>(Assert.IsType<BadRequestObjectResult>(nullImport).Value).ErrorCode);
        Assert.IsType<BadRequestObjectResult>(await controller.ExportPlan("missing", default));
    }

    // ================================================================ JSON 契约

    internal static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static string[] JsonKeys(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, WebJson)).RootElement
            .EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private static string[] Sorted(params string[] names) => names.OrderBy(name => name, StringComparer.Ordinal).ToArray();

    [Fact]
    public void JSON契约_快照_导出文件_版本列表_发布请求_字段名与附录C完全一致()
    {
        Assert.Equal(
            Sorted("version", "publishedAt", "publishedBy", "plan", "shelfModel", "hookModels", "products", "items", "stats"),
            JsonKeys(new PlanogramSnapshotDto()));
        Assert.Equal(Sorted("guid", "name", "notes", "bayCount", "gapXMm", "gapYMm"), JsonKeys(new PlanogramSnapshotPlanDto()));
        Assert.Equal(
            Sorted("productCode", "itemNumber", "barcode", "productName", "englishName", "productImage", "retailPrice", "spec"),
            JsonKeys(new PlanogramSnapshotProductDto()));
        Assert.Equal(
            Sorted("version", "publishedAt", "publishedBy", "itemCount", "skuCount", "unitCount"),
            JsonKeys(new PlanogramVersionSummaryDto()));
        Assert.Equal(Sorted("rowVersion"), JsonKeys(new PlanogramPublishRequest()));

        Assert.Equal(Sorted("formatVersion", "exportedAt", "plan", "shelfModelCode", "items"), JsonKeys(new PlanogramExportDto()));
        Assert.Equal(Sorted("name", "notes", "bayCount", "gapXMm", "gapYMm"), JsonKeys(new PlanogramExportPlanDto()));
        Assert.Equal(
            Sorted("bayIndex", "productCode", "hookModelCode", "holeCol", "holeRow", "perHookQty"),
            JsonKeys(new PlanogramExportItemDto()));
        // 导入请求体 = 导出文件 + 可选的 name。
        Assert.Equal(Sorted("formatVersion", "exportedAt", "plan", "shelfModelCode", "items", "name"), JsonKeys(new PlanogramImportRequest()));
        Assert.Equal(Sorted("kind", "code"), JsonKeys(new PlanogramImportUnresolvedDto("hook", "x")));
    }

    [Fact]
    public async Task JSON契约_快照序列化后status等是数字_时间是ISO字符串_集合是数组()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        await PublishOkAsync(await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid)));
        var snapshot = await GetVersionOkAsync(plan.Header.Guid, 1);

        var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, WebJson)).RootElement;

        Assert.Equal(JsonValueKind.Number, json.GetProperty("version").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("publishedAt").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("items").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("hookModels").ValueKind);
        var product = json.GetProperty("products")[0];
        Assert.Equal(JsonValueKind.Number, product.GetProperty("retailPrice").ValueKind);
        Assert.Equal(120, product.GetProperty("spec").GetProperty("widthMm").GetInt32());
        Assert.Equal(JsonValueKind.Object, product.GetProperty("spec").GetProperty("hookQtyOverrides").ValueKind);
        Assert.Equal(JsonValueKind.Number, json.GetProperty("shelfModel").GetProperty("holePitchXMm").ValueKind);
    }

    [Fact]
    public async Task 发布后的详情_头的JSON里hasUnpublishedChanges是布尔值()
    {
        var seed = await SeedModelsAsync();
        var plan = await NewPlanAsync(seed);
        var published = await PublishOkAsync(await SaveOkAsync(plan, TwoCleanItems(seed.Hook.Guid)));

        var json = JsonDocument.Parse(JsonSerializer.Serialize(published, WebJson)).RootElement;

        Assert.Equal(JsonValueKind.False, json.GetProperty("header").GetProperty("hasUnpublishedChanges").ValueKind);
        Assert.Equal(1, json.GetProperty("header").GetProperty("status").GetInt32());
        Assert.Equal(1, json.GetProperty("header").GetProperty("publishedVersion").GetInt32());
    }
}

/// <summary>
/// 真实 MVC 路由 + 授权管道下的批 3 接口：401 / 403 / 200，<c>planograms/import</c> 不被 <c>planograms/{guid}</c> 吞掉，
/// 权限别名（Manage ⊇ Edit ⊇ View）走真实的 PermissionPolicyProvider + PermissionAuthorizationHandler，
/// 角色服务用 Mock 按 <see cref="Permissions.GetEquivalentPermissionCodes"/> 模拟 RoleService 的别名判断。
/// </summary>
public sealed class PlanogramPublishPipelineTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
    private const string ApiBase = "/api/react/v1/planogram";
    private const string Manager = "user-manage";
    private const string Editor = "user-edit";
    private const string Viewer = "user-view";
    private const string Nobody = "user-none";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-planogram-pipeline-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly PlanogramModelService _models;
    private readonly ProductDisplaySpecService _specs;
    private readonly PlanogramPlanService _plans;
    private readonly TestServer _server;
    private readonly HttpClient _client;

    public PlanogramPublishPipelineTests()
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
        _db.Ado.ExecuteCommand("CREATE UNIQUE INDEX UX_PlanogramVersion_Plan_Version ON PlanogramVersion (PlanogramGuid, Version)");
        _models = new PlanogramModelService(_db, new FixedTime(Now));
        _specs = new ProductDisplaySpecService(_db, new FixedTime(Now));
        _plans = new PlanogramPlanService(_db, new FixedTime(Now));

        // 账号各自持有的权限码；判断口径与 RoleService.UserHasPermissionAsync 一致：持有码 ∩ (所需码 + 它的别名)。
        var held = new Dictionary<string, string[]>
        {
            [Manager] = new[] { Permissions.Planogram.Manage },
            [Editor] = new[] { Permissions.Planogram.Edit },
            [Viewer] = new[] { Permissions.Planogram.View },
            [Nobody] = Array.Empty<string>(),
        };
        var roleService = new Mock<IRoleService>();
        roleService
            .Setup(service => service.UserHasPermissionAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string user, string permission) => ApiResponse<bool>.OK(
                held.TryGetValue(user, out var codes)
                && codes.Intersect(Permissions.GetEquivalentPermissionCodes(permission), StringComparer.OrdinalIgnoreCase).Any()));

        _server = new TestServer(
            new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddMemoryCache();
                    services.AddOptions<AppUpdatePolicyOptions>();
                    services.AddSingleton(roleService.Object);
                    services.AddSingleton<IPlanogramModelService>(_models);
                    services.AddSingleton<IProductDisplaySpecService>(_specs);
                    services.AddSingleton<IPlanogramPlanService>(_plans);
                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { });
                    services.AddAuthorization();
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
                    services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    services.AddControllers().AddApplicationPart(typeof(PlanogramController).Assembly);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }));
        _client = _server.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private sealed class FixedTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, user.ToString()),
                    new Claim("userId", user.ToString()),
                    new Claim(ClaimTypes.Name, user + ".name"),
                ],
                "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string? user, object? body = null)
    {
        using var request = new HttpRequestMessage(method, ApiBase + path);
        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user);
        }

        if (body is not null)
        {
            var json = body is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(body, WebJson);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>货架、挂钩型号、P100 参数 + 一个已保存好两件商品的方案；返回方案 guid 与当前 rowVersion。</summary>
    private async Task<(string PlanGuid, string RowVersion)> SeedPlanAsync()
    {
        _db.Insertable(new Product
        {
            ProductCode = "P100", ItemNumber = "IT-100", Barcode = "9300000000011", ProductName = "挂钩收纳盒", RetailPrice = 4.5m,
        }).ExecuteCommand();
        var shelf = (await _models.CreateShelfModelAsync(new ShelfModelDto
        {
            Code = "G-S-PEG-90x30x120", Name = "单面洞洞板", BayWidthMm = 900, TotalHeightMm = 1200, BaseDepthMm = 300, BaseTopHeightMm = 150,
            UprightWidthMm = 30, HolePitchXMm = 25, HolePitchYMm = 25, HoleDiameterMm = 6, FirstHoleLeftMm = 25, FirstHoleTopMm = 25,
        }, "seed", default)).Data!;
        var hook = (await _models.CreateHookModelAsync(new HookModelDto
        {
            Code = "HK-D-PT-200", Name = "双钩带价签", ProngCount = 2, HasPriceTagArm = true, ArmLengthMm = 200, DropMm = 50,
            WireDiameterMm = 5, TipReserveMm = 20, TagWidthMm = 52, TagHeightMm = 26, HoleSpanX = 2, HoleSpanY = 1, LoadKg = 3,
        }, "seed", default)).Data!;
        Assert.True((await _specs.SaveAsync(
            "P100", new ProductDisplaySpecDto { WidthMm = 120, HeightMm = 180, DepthMm = 30, UnitWeightG = 200 }, "seed", default)).Success);
        var created = (await _plans.CreateAsync(
            new PlanogramCreateRequest { Name = "管道方案", ShelfModelGuid = shelf.Guid, BayCount = 1 }, "seed", default)).Data!;
        var saved = await _plans.SaveAsync(created.Header.Guid, new PlanogramSaveRequest
        {
            Name = created.Header.Name, ShelfModelGuid = shelf.Guid, BayCount = 1, GapXMm = 10, GapYMm = 10, RowVersion = created.RowVersion,
            Items = new List<PlanogramItemDto>
            {
                new() { Guid = "a", ProductCode = "P100", HookModelGuid = hook.Guid, HoleCol = 5, HoleRow = 3 },
                new() { Guid = "b", ProductCode = "P100", HookModelGuid = hook.Guid, HoleCol = 13, HoleRow = 3, SortOrder = 1 },
            },
        }, "seed", default);
        Assert.True(saved.Success, saved.Message);
        return (created.Header.Guid, saved.Data!.RowVersion);
    }

    private static string? ErrorCode(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : null;

    [Fact]
    public async Task 未登录返回401_已登录但权限不够返回403_且不触达服务层()
    {
        var (planGuid, rowVersion) = await SeedPlanAsync();
        var calls = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, $"/planograms/{planGuid}/publish", new { rowVersion }),
            (HttpMethod.Get, $"/planograms/{planGuid}/versions", null),
            (HttpMethod.Get, $"/planograms/{planGuid}/versions/1", null),
            (HttpMethod.Get, $"/planograms/{planGuid}/export", null),
            (HttpMethod.Post, "/planograms/import", new { formatVersion = 1 }),
        };

        foreach (var call in calls)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(call.Method, call.Path, null, call.Body)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(call.Method, call.Path, Nobody, call.Body)).Status);
        }

        // 权限别名的反向不成立：View 看得了版本，但导出 / 导入要 Edit、发布要 Manage。
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, $"/planograms/{planGuid}/export", Viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/planograms/import", Viewer, new { formatVersion = 1 })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/planograms/{planGuid}/publish", Viewer, new { rowVersion })).Status);
        // Edit 不够发布。
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/planograms/{planGuid}/publish", Editor, new { rowVersion })).Status);
        Assert.Equal(0, await _db.Queryable<PlanogramVersion>().CountAsync());
        Assert.Equal(1, await _db.Queryable<PlanogramHeader>().CountAsync());
    }

    [Fact]
    public async Task 权限别名_Manage_Edit_View各自能访问的批3接口()
    {
        var (planGuid, rowVersion) = await SeedPlanAsync();

        // View：只能读版本列表。
        var viewList = await SendAsync(HttpMethod.Get, $"/planograms/{planGuid}/versions", Viewer);
        Assert.Equal(HttpStatusCode.OK, viewList.Status);
        Assert.Equal(0, viewList.Body.GetProperty("data").GetArrayLength());

        // Edit（经别名满足 View）：能读版本列表、导出、导入；不能发布。
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/planograms/{planGuid}/versions", Editor)).Status);
        var export = await SendAsync(HttpMethod.Get, $"/planograms/{planGuid}/export", Editor);
        Assert.Equal(HttpStatusCode.OK, export.Status);
        var file = export.Body.GetProperty("data");
        Assert.Equal(1, file.GetProperty("formatVersion").GetInt32());
        Assert.Equal("G-S-PEG-90x30x120", file.GetProperty("shelfModelCode").GetString());
        Assert.Equal(2, file.GetProperty("items").GetArrayLength());
        var imported = await SendAsync(HttpMethod.Post, "/planograms/import", Editor, file);
        Assert.Equal(HttpStatusCode.OK, imported.Status);
        Assert.Equal(0, imported.Body.GetProperty("data").GetProperty("header").GetProperty("status").GetInt32());
        Assert.Equal(2, imported.Body.GetProperty("data").GetProperty("items").GetArrayLength());

        // Manage（经别名满足 Edit / View）：发布成功，随后版本列表 / 历史版本可读。
        var published = await SendAsync(HttpMethod.Post, $"/planograms/{planGuid}/publish", Manager, new { rowVersion });
        Assert.Equal(HttpStatusCode.OK, published.Status);
        Assert.Equal(1, published.Body.GetProperty("data").GetProperty("header").GetProperty("publishedVersion").GetInt32());
        Assert.False(published.Body.GetProperty("data").GetProperty("header").GetProperty("hasUnpublishedChanges").GetBoolean());
        var versions = await SendAsync(HttpMethod.Get, $"/planograms/{planGuid}/versions", Manager);
        Assert.Equal(1, versions.Body.GetProperty("data").GetArrayLength());
        Assert.Equal(2, versions.Body.GetProperty("data")[0].GetProperty("itemCount").GetInt32());
        var snapshot = await SendAsync(HttpMethod.Get, $"/planograms/{planGuid}/versions/1", Viewer);
        Assert.Equal(HttpStatusCode.OK, snapshot.Status);
        Assert.Equal("管道方案", snapshot.Body.GetProperty("data").GetProperty("plan").GetProperty("name").GetString());
        Assert.Equal(2, snapshot.Body.GetProperty("data").GetProperty("items").GetArrayLength());

        // 再发布一次（带新令牌）：当前版本已有快照 -> 400 + 业务错误码。
        var latest = (await SendAsync(HttpMethod.Get, $"/planograms/{planGuid}", Manager)).Body.GetProperty("data").GetProperty("rowVersion").GetString();
        var again = await SendAsync(HttpMethod.Post, $"/planograms/{planGuid}/publish", Manager, new { rowVersion = latest });
        Assert.Equal(HttpStatusCode.BadRequest, again.Status);
        Assert.Equal(PlanogramErrorCodes.AlreadyPublished, ErrorCode(again.Body));
    }

    [Fact]
    public async Task 路由_planograms_import不被planograms_guid吞掉_POST_planograms仍是新建_GET_import落到guid路由()
    {
        var (planGuid, _) = await SeedPlanAsync();
        var shelfGuid = (await _db.Queryable<PlanogramShelfModel>().FirstAsync(model => model.Code == "G-S-PEG-90x30x120")).Guid;

        // POST planograms/import -> 导入（空体：校验错误，证明命中的是 ImportPlan 而不是别的动作）。
        var importEmpty = await SendAsync(HttpMethod.Post, "/planograms/import", Editor, new { });
        Assert.Equal(HttpStatusCode.BadRequest, importEmpty.Status);
        Assert.Equal(PlanogramErrorCodes.Validation, ErrorCode(importEmpty.Body));
        var fields = importEmpty.Body.GetProperty("details").EnumerateArray().Select(item => item.GetProperty("field").GetString()).ToArray();
        Assert.Contains("formatVersion", fields);

        // POST planograms -> 新建，请求体字段与导入不同，且创建成功。
        var created = await SendAsync(HttpMethod.Post, "/planograms", Editor, new { name = "路由新建", shelfModelGuid = shelfGuid, bayCount = 1 });
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Assert.Equal("路由新建", created.Body.GetProperty("data").GetProperty("header").GetProperty("name").GetString());

        // GET planograms/import：import 只有 POST，GET 落到 planograms/{guid}，把 "import" 当 guid -> 业务 NOT_FOUND（不是 404/405）。
        var getImport = await SendAsync(HttpMethod.Get, "/planograms/import", Viewer);
        Assert.Equal(HttpStatusCode.BadRequest, getImport.Status);
        Assert.Equal(PlanogramErrorCodes.NotFound, ErrorCode(getImport.Body));
        // 真实方案的 guid 路由不受影响。
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/planograms/{planGuid}", Viewer)).Status);

        // 版本号必须是整数：非数字路由不匹配 -> 404（不会误落到别的动作）。
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/planograms/{planGuid}/versions/abc", Viewer)).Status);
        // 不存在的版本 / 方案 -> 业务 NOT_FOUND。
        var missingVersion = await SendAsync(HttpMethod.Get, $"/planograms/{planGuid}/versions/7", Viewer);
        Assert.Equal(PlanogramErrorCodes.NotFound, ErrorCode(missingVersion.Body));
        Assert.Equal(PlanogramErrorCodes.NotFound, ErrorCode((await SendAsync(HttpMethod.Get, "/planograms/missing/export", Editor)).Body));
    }
}

/// <summary>需要真实 SQL Server 的批 3 用例开关：未配置 <c>HB_TEST_SQLSERVER_CONNECTION</c> 时跳过（与其它 SQL Server 集成测试同一个变量）。</summary>
public sealed class PlanogramSqlServerFactAttribute : FactAttribute
{
    public const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public PlanogramSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过商品陈列图发布 / 导入导出的真实 SQL Server 集成测试。";
        }
    }
}

/// <summary>
/// 发布 / 版本快照 / 导入导出在真实 SQL Server 上的回归（需要 <c>HB_TEST_SQLSERVER_CONNECTION</c>，每个用例建一个 GUID 命名的独立数据库并在结束时删除）：
/// 表用迁移 20261104.001 的真实建表脚本创建（nvarchar(max)、datetime2、<c>UX_PlanogramVersion_Plan_Version</c> 唯一索引），
/// 客户端配置与生产的 SqlSugarContext 一致（MARS、WITH(NOLOCK) 查询、事务内不加 NOLOCK）。
/// 覆盖 SQLite 测不出的部分：驱动层唯一冲突号（2601 / 2627）识别、真实并发下的唯一索引与行锁、事务回滚、
/// datetime2 / 3.33ms 参数舍入下的 rowVersion、大于 4000 字符的 nvarchar(max) 参数、大小写不敏感的编码匹配。
/// </summary>
[Trait("Category", "SQL")]
public sealed class PlanogramPublishSqlServerTests
{
    private const string Actor = "alice";
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PlanogramPublishSqlServerTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    /// <summary>真实时钟 + 「读时间时先执行一次副作用」：用来在预检之后、事务开始之前插入别人的写入。</summary>
    private sealed class HookTime : TimeProvider
    {
        public Action? BeforeNextRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            var action = BeforeNextRead;
            BeforeNextRead = null;
            action?.Invoke();
            return DateTimeOffset.UtcNow;
        }
    }

    private sealed class Env(IsolatedDatabase database, SqlSugarClient db, string shelfGuid, string hookGuid)
    {
        public IsolatedDatabase Database { get; } = database;
        public SqlSugarClient Db { get; } = db;
        public string ShelfGuid { get; } = shelfGuid;
        public string HookGuid { get; } = hookGuid;
        public PlanogramPlanService Plans { get; } = new(db);
        public PlanogramModelService Models { get; } = new(db);
        public ProductDisplaySpecService Specs { get; } = new(db);
    }

    /// <summary>建库 + 迁移 + 商品与型号。<paramref name="grid"/> = true 时用小商品 / 无价签小挂钩（两千条大方案用）。</summary>
    private static async Task<Env> SetUpAsync(IsolatedDatabase database, bool grid = false)
    {
        var db = database.CreateClient();
        db.Insertable(new Product
        {
            ProductCode = "P100", ItemNumber = "IT-100", Barcode = "9300000000011", ProductName = "挂钩收纳盒",
            EnglishName = "Hook Box", RetailPrice = 4.5m, ProductImage = "/img/p100.jpg",
        }).ExecuteCommand();
        db.Insertable(new Product
        {
            ProductCode = "P200", ItemNumber = "IT-200", Barcode = "9300000000028", ProductName = "洗碗海绵 3 片装",
            EnglishName = "Sponge 3pk", RetailPrice = 2.99m,
        }).ExecuteCommand();

        var models = new PlanogramModelService(db);
        var specs = new ProductDisplaySpecService(db);
        var shelf = await models.CreateShelfModelAsync(PlanogramPublishTests.Shelf(), Actor, default);
        Assert.True(shelf.Success, shelf.Message);
        var hook = await models.CreateHookModelAsync(grid ? PlanogramPublishTests.SmallHook() : PlanogramPublishTests.Hook(), Actor, default);
        Assert.True(hook.Success, hook.Message);
        Assert.True((await specs.SaveAsync("P100", grid ? PlanogramPublishTests.TinySpec() : PlanogramPublishTests.Spec(), Actor, default)).Success);
        Assert.True((await specs.SaveAsync("P200", PlanogramPublishTests.Spec(100), Actor, default)).Success);
        return new Env(database, db, shelf.Data!.Guid, hook.Data!.Guid);
    }

    private static async Task<PlanogramDetailDto> NewPlanAsync(PlanogramPlanService plans, Env env, string name, int bayCount = 1)
    {
        var created = await plans.CreateAsync(
            new PlanogramCreateRequest { Name = name, ShelfModelGuid = env.ShelfGuid, BayCount = bayCount }, Actor, default);
        Assert.True(created.Success, created.Message);
        return created.Data!;
    }

    private static async Task<PlanogramDetailDto> SaveAsync(PlanogramPlanService plans, PlanogramDetailDto detail, params PlanogramItemDto[] items)
    {
        var saved = await plans.SaveAsync(detail.Header.Guid, PlanogramPublishTests.SaveRequest(detail, items), Actor, default);
        Assert.True(saved.Success, saved.Message + " " + JsonSerializer.Serialize(saved.Details));
        return saved.Data!;
    }

    private static async Task<PlanogramDetailDto> PublishAsync(PlanogramPlanService plans, PlanogramDetailDto detail, string actor = "boss")
    {
        var published = await plans.PublishAsync(detail.Header.Guid, PlanogramPublishTests.PublishRequest(detail), actor, default);
        Assert.True(published.Success, published.Message + " " + JsonSerializer.Serialize(published.Details));
        return published.Data!;
    }

    private static async Task<int> CountAsync(SqlSugarClient db, string table, string planGuidColumn, string planGuid) =>
        Convert.ToInt32(await db.Ado.GetScalarAsync($"SELECT COUNT(*) FROM dbo.{table} WHERE {planGuidColumn} = @g", new SugarParameter("@g", planGuid)));

    // ================================================================ 用例

    [PlanogramSqlServerFact]
    public async Task SQLServer_发布v1_修改保存v2_再发布_两个快照都能读出_真实时钟下rowVersion往返不误报过期()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var env = await SetUpAsync(database);
        var plans = env.Plans;

        var plan = await NewPlanAsync(plans, env, "入口货架");
        var saved = await SaveAsync(plans, plan, PlanogramPublishTests.TwoCleanItems(env.HookGuid));
        var v1 = await PublishAsync(plans, saved);
        Assert.Equal((PlanogramStatus.Published, 1, 1), (v1.Header.Status, v1.Header.Version, v1.Header.PublishedVersion ?? 0));
        Assert.NotEqual(saved.RowVersion, v1.RowVersion);

        // 发布后改商品主档 / 陈列参数：历史版本不变。
        env.Db.Updateable<Product>().SetColumns(p => p.ProductName == "改名后的收纳盒").SetColumns(p => p.RetailPrice == 9.9m).Where(p => p.ProductCode == "P100").ExecuteCommand();
        Assert.True((await env.Specs.SaveAsync("P100", PlanogramPublishTests.Spec(140), Actor, default)).Success);

        var edited = await SaveAsync(plans, v1, PlanogramPublishTests.Item("a", "P100", env.HookGuid, 6, 3), PlanogramPublishTests.Item("c", "P200", env.HookGuid, 20, 10));
        Assert.Equal((PlanogramStatus.Draft, 2, 1, true), (edited.Header.Status, edited.Header.Version, edited.Header.PublishedVersion ?? 0, edited.Header.HasUnpublishedChanges));
        var v2 = await PublishAsync(plans, edited, "carol");
        Assert.Equal(2, v2.Header.PublishedVersion);

        var first = (await plans.GetVersionAsync(plan.Header.Guid, 1, default)).Data!;
        var second = (await plans.GetVersionAsync(plan.Header.Guid, 2, default)).Data!;
        Assert.Equal(new[] { "a", "b" }, first.Items.Select(item => item.Guid));
        Assert.Equal(new[] { "a", "c" }, second.Items.Select(item => item.Guid));
        Assert.Equal("挂钩收纳盒", first.Products.Single().ProductName);
        Assert.Equal(4.5m, first.Products.Single().RetailPrice);
        Assert.Equal(120, first.Products.Single().Spec.WidthMm);
        Assert.Equal("改名后的收纳盒", second.Products.Single(p => p.ProductCode == "P100").ProductName);
        Assert.Equal(140, second.Products.Single(p => p.ProductCode == "P100").Spec.WidthMm);

        var list = (await plans.ListVersionsAsync(plan.Header.Guid, default)).Data!;
        Assert.Equal(new[] { 2, 1 }, list.Select(row => row.Version));
        Assert.Equal(new[] { "carol", "boss" }, list.Select(row => row.PublishedBy));
        Assert.Equal(v2.Header.PublishedAt, list[0].PublishedAt);
        Assert.Equal(v1.Header.PublishedAt, list[1].PublishedAt);
        Assert.Equal(2, await CountAsync(env.Db, "PlanogramVersion", "PlanogramGuid", plan.Header.Guid));

        // 归档 -> 恢复 -> 直接发布 v3：版本号 = 最近发布版本 + 1，不撞唯一索引。
        Assert.True((await plans.ArchiveAsync(plan.Header.Guid, Actor, default)).Success);
        Assert.True((await plans.RestoreAsync(plan.Header.Guid, Actor, default)).Success);
        var restored = (await plans.GetAsync(plan.Header.Guid, default)).Data!;
        Assert.Equal(3, restored.Header.Version);
        Assert.Equal(3, (await PublishAsync(plans, restored)).Header.PublishedVersion);
    }

    [PlanogramSqlServerFact]
    public async Task SQLServer_发布被拒绝时整体回滚_空方案_有冲突_已发布未改动_头与rowVersion都不变()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var env = await SetUpAsync(database);
        var plans = env.Plans;

        var empty = await NewPlanAsync(plans, env, "空方案");
        Assert.Equal(PlanogramErrorCodes.Empty, (await plans.PublishAsync(empty.Header.Guid, PlanogramPublishTests.PublishRequest(empty), "boss", default)).ErrorCode);
        Assert.Equal(empty.RowVersion, (await plans.GetAsync(empty.Header.Guid, default)).Data!.RowVersion);

        var conflicted = await NewPlanAsync(plans, env, "有冲突");
        var clash = await SaveAsync(plans, conflicted,
            PlanogramPublishTests.Item("x1", "P100", env.HookGuid, 5, 3), PlanogramPublishTests.Item("x2", "P100", env.HookGuid, 5, 3));
        var rejected = await plans.PublishAsync(conflicted.Header.Guid, PlanogramPublishTests.PublishRequest(clash), "boss", default);
        Assert.Equal(PlanogramErrorCodes.HasConflicts, rejected.ErrorCode);
        Assert.NotEmpty(Assert.IsType<List<ConflictDto>>(rejected.Details));
        Assert.Equal(clash.RowVersion, (await plans.GetAsync(conflicted.Header.Guid, default)).Data!.RowVersion);
        Assert.Equal(0, await CountAsync(env.Db, "PlanogramVersion", "PlanogramGuid", conflicted.Header.Guid));

        var clean = await NewPlanAsync(plans, env, "干净");
        var cleanSaved = await SaveAsync(plans, clean, PlanogramPublishTests.TwoCleanItems(env.HookGuid, "c"));
        var published = await PublishAsync(plans, cleanSaved);
        var again = await plans.PublishAsync(clean.Header.Guid, PlanogramPublishTests.PublishRequest(published), "boss", default);
        Assert.Equal(PlanogramErrorCodes.AlreadyPublished, again.ErrorCode);
        Assert.Equal(published.RowVersion, (await plans.GetAsync(clean.Header.Guid, default)).Data!.RowVersion);
        Assert.Equal(1, await CountAsync(env.Db, "PlanogramVersion", "PlanogramGuid", clean.Header.Guid));
    }

    [PlanogramSqlServerFact]
    public async Task SQLServer_预检之后别人提交了同一版本的快照_真实驱动的唯一索引错误转成ALREADY_PUBLISHED_回滚后连接仍可用()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var env = await SetUpAsync(database);
        var hookTime = new HookTime();
        var plans = new PlanogramPlanService(env.Db, hookTime);

        var plan = await NewPlanAsync(plans, env, "入口货架");
        var saved = await SaveAsync(plans, plan, PlanogramPublishTests.TwoCleanItems(env.HookGuid));

        // 预检（还没有 v1 快照）通过之后、事务开始之前，另一个会话（独立连接，自动提交）写入了 v1 快照。
        hookTime.BeforeNextRead = () => database.ExecuteSync(
            $"INSERT dbo.PlanogramVersion (Guid, PlanogramGuid, Version, LayoutJson, PublishedBy, PublishedAt) VALUES (N'rival-1', N'{plan.Header.Guid}', 1, N'{{}}', N'rival', SYSUTCDATETIME())");
        var result = await plans.PublishAsync(plan.Header.Guid, PlanogramPublishTests.PublishRequest(saved), "boss", default);

        Assert.False(result.Success);
        Assert.Equal(PlanogramErrorCodes.AlreadyPublished, result.ErrorCode);
        Assert.Equal("rival", await database.ScalarAsync($"SELECT PublishedBy FROM dbo.PlanogramVersion WHERE PlanogramGuid = N'{plan.Header.Guid}'"));
        Assert.Equal("1", await database.ScalarAsync($"SELECT COUNT(*) FROM dbo.PlanogramVersion WHERE PlanogramGuid = N'{plan.Header.Guid}'"));
        var header = (await plans.GetAsync(plan.Header.Guid, default)).Data!;
        Assert.Equal(PlanogramStatus.Draft, header.Header.Status);
        Assert.Null(header.Header.PublishedVersion);
        Assert.Equal(saved.RowVersion, header.RowVersion); // 事务回滚：占住头的那次更新也撤销了

        // 回滚之后同一个客户端 / 连接照常读写（事务状态没有残留）。
        var next = await SaveAsync(plans, header, PlanogramPublishTests.Item("a", "P100", env.HookGuid, 5, 3));
        Assert.Equal(PlanogramStatus.Draft, next.Header.Status);
    }

    [PlanogramSqlServerFact]
    public async Task SQLServer_预检之后别人抢先保存了方案_占头的条件更新0行_报STALE_别人的修改保留_不留快照()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var env = await SetUpAsync(database);
        var hookTime = new HookTime();
        var plans = new PlanogramPlanService(env.Db, hookTime);

        var plan = await NewPlanAsync(plans, env, "入口货架");
        var saved = await SaveAsync(plans, plan, PlanogramPublishTests.TwoCleanItems(env.HookGuid));

        hookTime.BeforeNextRead = () => database.ExecuteSync(
            $"UPDATE dbo.Planogram SET Name = N'别人改的名字', UpdatedAt = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE Guid = N'{plan.Header.Guid}'");
        var result = await plans.PublishAsync(plan.Header.Guid, PlanogramPublishTests.PublishRequest(saved), "boss", default);

        Assert.Equal(PlanogramErrorCodes.Stale, result.ErrorCode);
        Assert.Equal("别人改的名字", await database.ScalarAsync($"SELECT Name FROM dbo.Planogram WHERE Guid = N'{plan.Header.Guid}'"));
        Assert.Equal("0", await database.ScalarAsync($"SELECT Status FROM dbo.Planogram WHERE Guid = N'{plan.Header.Guid}'"));
        Assert.Equal(0, await CountAsync(env.Db, "PlanogramVersion", "PlanogramGuid", plan.Header.Guid));
    }

    [PlanogramSqlServerFact]
    public async Task SQLServer_多个会话并发发布同一版本_恰好一个成功_其余ALREADY_PUBLISHED或STALE_不抛异常_快照只有一份()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var env = await SetUpAsync(database);
        const int Rounds = 4;
        const int Workers = 8;
        var outcomes = new Dictionary<string, int>();

        for (var round = 0; round < Rounds; round++)
        {
            var plan = await NewPlanAsync(env.Plans, env, $"并发发布 {round}");
            var saved = await SaveAsync(env.Plans, plan, PlanogramPublishTests.TwoCleanItems(env.HookGuid, $"r{round}-"));

            // 每个会话各用一个独立的客户端 / 连接，同一把闸门放行。
            var gate = new TaskCompletionSource();
            var tasks = Enumerable.Range(0, Workers).Select(_ => Task.Run(async () =>
            {
                using var db = database.CreateClient();
                var service = new PlanogramPlanService(db);
                await gate.Task;
                return await service.PublishAsync(plan.Header.Guid, PlanogramPublishTests.PublishRequest(saved), "boss", default);
            })).ToList();
            gate.SetResult();
            var results = await Task.WhenAll(tasks);

            Assert.Single(results, result => result.Success);
            foreach (var failure in results.Where(result => !result.Success))
            {
                Assert.Contains(failure.ErrorCode, new[] { PlanogramErrorCodes.AlreadyPublished, PlanogramErrorCodes.Stale });
                outcomes[failure.ErrorCode!] = outcomes.GetValueOrDefault(failure.ErrorCode!) + 1;
            }

            Assert.Equal(1, await CountAsync(env.Db, "PlanogramVersion", "PlanogramGuid", plan.Header.Guid));
            var header = (await env.Plans.GetAsync(plan.Header.Guid, default)).Data!;
            Assert.Equal((PlanogramStatus.Published, 1, 1), (header.Header.Status, header.Header.Version, header.Header.PublishedVersion ?? 0));
        }

        _output.WriteLine($"并发发布 {Rounds} 轮 × {Workers} 个会话，失败方错误码分布：" + string.Join("，", outcomes.Select(pair => $"{pair.Key}={pair.Value}")));
    }

    [PlanogramSqlServerFact]
    public async Task SQLServer_发布与保存并发_只有一方成功_快照永远对应发布瞬间提交的明细_不会固化保存到一半的数据()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var env = await SetUpAsync(database);
        const int Rounds = 6;
        var publishWins = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var plan = await NewPlanAsync(env.Plans, env, $"发布对保存 {round}");
            var originalItems = PlanogramPublishTests.TwoCleanItems(env.HookGuid, $"r{round}-");
            var saved = await SaveAsync(env.Plans, plan, originalItems);

            // 发布的起跑时间逐轮错开（0 / 40 / 80 ms），让「发布先成功」和「保存先成功」两种结局都有机会出现。
            var publishDelay = TimeSpan.FromMilliseconds(round % 3 * 40);
            var gate = new TaskCompletionSource();
            var publishTask = Task.Run(async () =>
            {
                using var db = database.CreateClient();
                await gate.Task;
                await Task.Delay(publishDelay);
                return await new PlanogramPlanService(db).PublishAsync(plan.Header.Guid, PlanogramPublishTests.PublishRequest(saved), "boss", default);
            });
            var saveTask = Task.Run(async () =>
            {
                using var db = database.CreateClient();
                await gate.Task;
                // 保存：整体换成 3 件不同的明细（用同一个旧 rowVersion）。
                return await new PlanogramPlanService(db).SaveAsync(
                    plan.Header.Guid,
                    PlanogramPublishTests.SaveRequest(
                        saved,
                        PlanogramPublishTests.Item($"s{round}-1", "P200", env.HookGuid, 5, 10),
                        PlanogramPublishTests.Item($"s{round}-2", "P200", env.HookGuid, 13, 10),
                        PlanogramPublishTests.Item($"s{round}-3", "P200", env.HookGuid, 21, 10)),
                    Actor,
                    default);
            });
            gate.SetResult();
            var publish = await publishTask;
            var save = await saveTask;

            // 两边用的是同一个 rowVersion：最多一方成功。
            Assert.True(publish.Success ^ save.Success, $"发布={publish.ErrorCode ?? "OK"} 保存={save.ErrorCode ?? "OK"}");
            var items = (await env.Plans.GetAsync(plan.Header.Guid, default)).Data!.Items;
            if (publish.Success)
            {
                publishWins++;
                Assert.Equal(PlanogramErrorCodes.Stale, save.ErrorCode);
                var snapshot = (await env.Plans.GetVersionAsync(plan.Header.Guid, 1, default)).Data!;
                Assert.Equal(originalItems.Select(item => item.Guid), snapshot.Items.Select(item => item.Guid));
                Assert.Equal(originalItems.Select(item => item.Guid), items.Select(item => item.Guid));
            }
            else
            {
                Assert.Equal(PlanogramErrorCodes.Stale, publish.ErrorCode);
                Assert.Equal(0, await CountAsync(env.Db, "PlanogramVersion", "PlanogramGuid", plan.Header.Guid));
                Assert.Equal(3, items.Count);
            }
        }

        _output.WriteLine($"发布与保存并发 {Rounds} 轮：发布先成功 {publishWins} 轮，保存先成功 {Rounds - publishWins} 轮");
    }

    [PlanogramSqlServerFact]
    public async Task SQLServer_发布与删除并发_不会把刚发布的方案连同明细删掉()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var env = await SetUpAsync(database);
        const int Rounds = 8;
        var publishWins = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var plan = await NewPlanAsync(env.Plans, env, $"发布对删除 {round}");
            var saved = await SaveAsync(env.Plans, plan, PlanogramPublishTests.TwoCleanItems(env.HookGuid, $"d{round}-"));

            // 偶数轮让删除晚 25ms 起跑、奇数轮让发布晚 25ms 起跑，两种结局（发布先成功 / 删除先成功）都会出现。
            var delayPublish = round % 2 == 1;
            var gate = new TaskCompletionSource();
            var publishTask = Task.Run(async () =>
            {
                using var db = database.CreateClient();
                await gate.Task;
                if (delayPublish)
                {
                    await Task.Delay(25);
                }

                return await new PlanogramPlanService(db).PublishAsync(plan.Header.Guid, PlanogramPublishTests.PublishRequest(saved), "boss", default);
            });
            var deleteTask = Task.Run(async () =>
            {
                using var db = database.CreateClient();
                await gate.Task;
                if (!delayPublish)
                {
                    await Task.Delay(25);
                }

                return await new PlanogramPlanService(db).DeleteAsync(plan.Header.Guid, default);
            });
            gate.SetResult();
            var publish = await publishTask;
            var delete = await deleteTask;

            var headerCount = await CountAsync(env.Db, "Planogram", "Guid", plan.Header.Guid);
            var itemCount = await CountAsync(env.Db, "PlanogramItem", "PlanogramGuid", plan.Header.Guid);
            var versionCount = await CountAsync(env.Db, "PlanogramVersion", "PlanogramGuid", plan.Header.Guid);
            if (publish.Success)
            {
                publishWins++;
                // 发布赢：删除必须被拒（NOT_DELETABLE），方案、明细、快照都在。
                Assert.Equal(PlanogramErrorCodes.NotDeletable, delete.ErrorCode);
                Assert.Equal((1, 2, 1), (headerCount, itemCount, versionCount));
            }
            else
            {
                // 删除赢：发布要么在预检时就找不到方案（NOT_FOUND），要么占头时发现头已被删（STALE）；不留任何快照 / 明细。
                Assert.True(delete.Success, delete.Message);
                Assert.Contains(publish.ErrorCode, new[] { PlanogramErrorCodes.NotFound, PlanogramErrorCodes.Stale });
                Assert.Equal((0, 0, 0), (headerCount, itemCount, versionCount));
            }
        }

        _output.WriteLine($"发布与删除并发 {Rounds} 轮：发布先成功 {publishWins} 轮，删除先成功 {Rounds - publishWins} 轮");
    }

    [PlanogramSqlServerFact]
    public async Task SQLServer_两千条明细的大快照_nvarchar_max参数_发布读取_导出再导入_编码大小写不敏感且货号以主档写法为准()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var env = await SetUpAsync(database, grid: true);
        var plans = env.Plans;

        var plan = await NewPlanAsync(plans, env, "大方案", bayCount: 7);
        var saved = await SaveAsync(plans, plan, PlanogramPublishTests.GridItems(env.HookGuid, 2000).ToArray());
        Assert.Empty(saved.Conflicts);
        var published = await PublishAsync(plans, saved);
        Assert.Equal(2000, published.Stats.ItemCount);

        var length = Convert.ToInt64(await database.ScalarAsync($"SELECT DATALENGTH(LayoutJson) FROM dbo.PlanogramVersion WHERE PlanogramGuid = N'{plan.Header.Guid}'"));
        Assert.True(length > 100_000, $"快照只有 {length} 字节");
        var snapshot = (await plans.GetVersionAsync(plan.Header.Guid, 1, default)).Data!;
        Assert.Equal(2000, snapshot.Items.Count);
        Assert.Equal(2000, snapshot.Stats.ItemCount);
        var summary = (await plans.ListVersionsAsync(plan.Header.Guid, default)).Data!.Single();
        Assert.Equal((2000, 1), (summary.ItemCount, summary.SkuCount));

        var file = PlanogramPublishTests.ThroughJson((await plans.ExportAsync(plan.Header.Guid, default)).Data!);
        Assert.Equal(2000, file.Items!.Count);
        // 编码全部改成小写：SQL Server 默认排序规则不分大小写，货架 / 挂钩 / 商品都能解析；落库的货号是主档里的写法。
        file.ShelfModelCode = file.ShelfModelCode.ToLowerInvariant();
        foreach (var item in file.Items)
        {
            item.ProductCode = item.ProductCode.ToLowerInvariant();
            item.HookModelCode = item.HookModelCode.ToLowerInvariant();
        }

        var imported = await plans.ImportAsync(file, Actor, default);
        Assert.True(imported.Success, imported.Message + " " + JsonSerializer.Serialize(imported.Details));
        Assert.Equal(2000, imported.Data!.Items.Count);
        Assert.All(imported.Data.Items, item => Assert.Equal("P100", item.ProductCode));
        Assert.Equal(published.Stats.UnitCount, imported.Data.Stats.UnitCount);
        Assert.Empty(imported.Data.Conflicts);
        Assert.Equal(PlanogramStatus.Draft, imported.Data.Header.Status);

        // 解析不到的编码（含不存在的商品）一次返回、去重。
        file.Items[0].ProductCode = "NO-SUCH-PRODUCT";
        file.Items[1].ProductCode = "no-such-product";
        var unresolved = await plans.ImportAsync(file, Actor, default);
        Assert.Equal(PlanogramErrorCodes.ImportUnresolved, unresolved.ErrorCode);
        var details = Assert.IsType<List<PlanogramImportUnresolvedDto>>(unresolved.Details);
        Assert.Equal(new PlanogramImportUnresolvedDto("product", "NO-SUCH-PRODUCT"), Assert.Single(details));
    }

    // ================================================================ 真实库夹具

    private sealed class IsolatedDatabase : IAsyncDisposable
    {
        private readonly string _serverConnectionString;
        private readonly string _databaseName;

        private IsolatedDatabase(string serverConnectionString, string databaseName, string connectionString)
        {
            _serverConnectionString = serverConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<IsolatedDatabase> CreateAsync()
        {
            var server = Environment.GetEnvironmentVariable(PlanogramSqlServerFactAttribute.ConnectionEnvironmentVariable)!;
            var name = $"hb_planogram_{Guid.NewGuid():N}";
            await ExecuteOnAsync(server, $"CREATE DATABASE [{name}] COLLATE Chinese_PRC_90_CI_AS;");
            // 与生产 SqlSugarContext 一致：开 MARS。
            var connectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = name, MultipleActiveResultSets = true }.ConnectionString;
            var database = new IsolatedDatabase(server, name, connectionString);
            using (var db = database.CreateClient())
            {
                db.CodeFirst.InitTables(typeof(Product), typeof(ProductCategory));
            }

            // 真实迁移建表脚本（含 UX_PlanogramVersion_Plan_Version），并跑一遍只读门禁。
            await database.ExecuteAsync(Data.SchemaMigrations.PlanogramSchema.ApplySql);
            await database.ExecuteAsync(Data.SchemaMigrations.PlanogramSchema.VerifySql);
            return database;
        }

        /// <summary>客户端配置对齐生产 SqlSugarContext：自动关连接、查询默认 NOLOCK、事务内不加 NOLOCK、nvarchar 建表。</summary>
        public SqlSugarClient CreateClient() =>
            new(new ConnectionConfig
            {
                ConnectionString = ConnectionString,
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
                MoreSettings = new ConnMoreSettings
                {
                    IsAutoRemoveDataCache = true,
                    IsWithNoLockQuery = true,
                    DisableWithNoLockWithTran = true,
                    SqlServerCodeFirstNvarchar = true,
                },
            });

        public Task ExecuteAsync(string sql) => ExecuteOnAsync(ConnectionString, sql);

        public void ExecuteSync(string sql) => ExecuteOnAsync(ConnectionString, sql).GetAwaiter().GetResult();

        public async Task<string> ScalarAsync(string sql)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
        }

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await ExecuteOnAsync(
                _serverConnectionString,
                $"IF DB_ID(N'{_databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]; END");
        }

        private static async Task ExecuteOnAsync(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
        }
    }
}
