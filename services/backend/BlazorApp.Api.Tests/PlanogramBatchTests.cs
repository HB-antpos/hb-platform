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
/// 附录 D 的共用夹具：一份有仓库分类树、本地供应商分类树、归属登记、商品等级的小目录，
/// SQLite 单元用例与真实 SQL Server 用例用同一份数据、同一组预期。商品与分类都是虚构的。
///
/// 启用商品（按货号排序）：
///   P100 供应商 200 · 仓库 W-GRAND   · 等级 A
///   P101 供应商 200 · 仓库 W-CHILD2  · 等级 B
///   P102 供应商 空  · 仓库 W-ROOT    · 等级 C
///   P103 供应商 ""  · 仓库 无        · 无等级
///   P104 供应商 200 · 仓库 W-DELETED（已软删的分类） · 无等级
///   P105 供应商 200 · 仓库 W-OTHER   · 等级行 "E"（A～D 之外）
///   P200 供应商 240 · 仓库 W-OTHER   · 归属 S-CHILD · 等级 D
///   P201 供应商 240 · 仓库 无        · 归属 S-ROOT  · 只有一条已软删的等级行（视为无等级）
///   P202 供应商 240 · 仓库 W-GRAND   · 未归属 · 无等级
///   P203 供应商 240 · 仓库 无        · 归属登记在供应商 243 名下（HQ 改过供应商，视为未归类） · 无等级
///   P300 供应商 243 · 仓库 无        · 归属 T-ROOT  · 等级 A
/// 不会出现在列表里：P900（已停用）、P901（已软删）。
/// 仓库分类树：W-ROOT → W-CHILD1 → W-GRAND；W-ROOT → W-CHILD2；W-OTHER 独立；W-DELETED（已软删）挂在 W-ROOT 下。
/// 供应商分类树：240 的 S-ROOT → S-CHILD；243 的 T-ROOT。
/// </summary>
internal static class PlanogramBatchCatalog
{
    public static readonly DateTime SeedTime = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    public static readonly string[] ActiveCodes =
        { "P100", "P101", "P102", "P103", "P104", "P105", "P200", "P201", "P202", "P203", "P300" };

    /// <summary>SQLite 里需要建的表（SQL Server 里陈列图表与供应商分类表走真实迁移脚本）。</summary>
    public static readonly Type[] SqliteTables =
    {
        typeof(PlanogramShelfModel), typeof(PlanogramHookModel), typeof(ProductDisplaySpec),
        typeof(PlanogramHeader), typeof(PlanogramItem), typeof(PlanogramVersion),
        typeof(Product), typeof(ProductCategory), typeof(ProductGrade), typeof(WarehouseCategory),
        typeof(LocalSupplierCategory), typeof(LocalSupplierCategoryProductAssignment),
    };

    public static void Seed(ISqlSugarClient db)
    {
        WarehouseCategory Warehouse(string guid, string? parent, string name, bool deleted = false) =>
            new() { CategoryGUID = guid, ParentGUID = parent, CategoryName = name, IsDeleted = deleted };
        db.Insertable(new List<WarehouseCategory>
        {
            Warehouse("W-ROOT", null, "百货"),
            Warehouse("W-CHILD1", "W-ROOT", "收纳"),
            Warehouse("W-GRAND", "W-CHILD1", "收纳盒"),
            Warehouse("W-CHILD2", "W-ROOT", "清洁"),
            Warehouse("W-OTHER", null, "文具"),
            Warehouse("W-DELETED", "W-ROOT", "已删分类", deleted: true),
        }).ExecuteCommand();

        LocalSupplierCategory Supplier(string guid, string supplier, string? parent, string name, int depth) => new()
        {
            CategoryGUID = guid, LocalSupplierCode = supplier, ParentGUID = parent, CategoryName = name,
            ExternalKey = "/" + guid.ToLowerInvariant(), FullPath = name, Depth = depth,
        };
        db.Insertable(new List<LocalSupplierCategory>
        {
            Supplier("S-ROOT", "240", null, "办公文具", 0),
            Supplier("S-CHILD", "240", "S-ROOT", "胶带", 1),
            Supplier("T-ROOT", "243", null, "厨房", 0),
        }).ExecuteCommand();

        Product Make(string code, string? supplier, string? warehouse, bool active = true, bool deleted = false) => new()
        {
            ProductCode = code, ItemNumber = "IT-" + code[1..], Barcode = "93" + code[1..].PadLeft(11, '0'),
            ProductName = "商品" + code, EnglishName = "Item " + code, RetailPrice = 3m,
            LocalSupplierCode = supplier, WarehouseCategoryGUID = warehouse, IsActive = active, IsDeleted = deleted,
        };
        db.Insertable(new List<Product>
        {
            Make("P100", "200", "W-GRAND"),
            Make("P101", "200", "W-CHILD2"),
            Make("P102", null, "W-ROOT"),
            Make("P103", "", null),
            Make("P104", "200", "W-DELETED"),
            Make("P105", "200", "W-OTHER"),
            Make("P200", "240", "W-OTHER"),
            Make("P201", "240", null),
            Make("P202", "240", "W-GRAND"),
            Make("P203", "240", null),
            Make("P300", "243", null),
            Make("P900", "200", "W-GRAND", active: false),
            Make("P901", "200", "W-GRAND", deleted: true),
        }).ExecuteCommand();

        LocalSupplierCategoryProductAssignment Assign(string code, string supplier, string category) =>
            new() { ProductCode = code, LocalSupplierCode = supplier, CategoryGUID = category, Source = "manual" };
        db.Insertable(new List<LocalSupplierCategoryProductAssignment>
        {
            Assign("P200", "240", "S-CHILD"),
            Assign("P201", "240", "S-ROOT"),
            Assign("P203", "243", "S-CHILD"), // 登记的供应商（243）与商品当前供应商（240）不一致 → 未归类
            Assign("P300", "243", "T-ROOT"),
        }).ExecuteCommand();

        ProductGrade Grade(string code, string grade, bool deleted = false) =>
            new() { ProductCode = code, Grade = grade, IsDeleted = deleted };
        db.Insertable(new List<ProductGrade>
        {
            Grade("P100", "A"), Grade("P101", "B"), Grade("P102", "C"), Grade("P105", "E"),
            Grade("P200", "D"), Grade("P201", "A", deleted: true), Grade("P300", "A"), Grade("P900", "A"),
        }).ExecuteCommand();
    }

    public static ProductDisplaySpec Spec(
        string code, int width = 120, int height = 180, int depth = 30, int offset = 10, byte type = 1, int? unitWeight = 200,
        string? defaultHook = null, string? overridesJson = null, byte source = 1, string? notes = null) => new()
    {
        ProductCode = code, WidthMm = width, HeightMm = height, DepthMm = depth, HangHoleOffsetMm = offset, HangHoleType = type,
        UnitWeightG = unitWeight, DefaultHookGuid = defaultHook, HookQtyOverridesJson = overridesJson, Source = source, Notes = notes,
        CreatedAt = SeedTime, CreatedBy = "seed", UpdatedAt = SeedTime, UpdatedBy = "seed",
    };

    /// <summary>过滤条件下的预期货号集合（按货号排序）。</summary>
    public static string[] Codes(params string[] codes) => codes.OrderBy(code => code, StringComparer.Ordinal).ToArray();
}

/// <summary>
/// 商品陈列参数附录 D（SQLite 临时库）：列表的仓库分类 / 本地供应商 / 供应商分类 / 未归类 / 商品等级过滤，
/// 以及 <c>POST product-specs/batch</c> 批量修改的目标解析、补丁校验、创建 / 更新 / 不变 / 跳过、dryRun 与事务原子性。
/// 契约依据 docs/plans/2026-11-04-planogram-shelf-display-designer.md 附录 D。
/// </summary>
public sealed class PlanogramBatchTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
    private const string Actor = "carol";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-planogram-batch-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly PlanogramModelService _models;
    private readonly ProductDisplaySpecService _specs;
    private readonly PlanogramPlanService _plans;

    public PlanogramBatchTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(PlanogramBatchCatalog.SqliteTables);
        _models = new PlanogramModelService(_db, new FixedTime(Now));
        _specs = new ProductDisplaySpecService(_db, new FixedTime(Now));
        _plans = new PlanogramPlanService(_db, new FixedTime(Now));
        PlanogramBatchCatalog.Seed(_db);
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

    /// <summary>
    /// 取当前时间时先执行一次副作用（只触发一次）。批量执行只在「计算完、开事务写入之前」取一次时间，
    /// 用它精确插入「别人同时创建了同一货号的参数」。
    /// </summary>
    internal sealed class RacingTime(DateTime utcNow) : TimeProvider
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

    // ---------------------------------------------------------------- 辅助

    private static ProductSpecFilterDto Filter(
        string? keyword = null, string? status = null, string[]? warehouse = null, string? supplier = null,
        string[]? supplierCategories = null, bool? unassigned = null, string[]? grades = null) => new()
    {
        Keyword = keyword,
        Status = status,
        WarehouseCategoryGuids = warehouse?.ToList(),
        LocalSupplierCode = supplier,
        SupplierCategoryGuids = supplierCategories?.ToList(),
        SupplierCategoryUnassignedOnly = unassigned,
        Grades = grades?.ToList(),
    };

    private async Task<string[]> ListCodesAsync(ProductSpecFilterDto filter, int pageSize = 100)
    {
        var result = await _specs.ListAsync(filter, 1, pageSize, default);
        Assert.True(result.Success, result.Message);
        return result.Data!.Items!.Select(item => item.ProductCode).ToArray();
    }

    private static PlanogramFieldError[] FieldErrors(object? details) =>
        Assert.IsType<List<PlanogramFieldError>>(details).ToArray();

    private static Dictionary<string, JsonElement> Patch(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static ProductSpecBatchOverridesDto Overrides(string mode, string? valuesJson) => new()
    {
        Mode = mode,
        Values = valuesJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(valuesJson),
    };

    private static ProductSpecBatchRequest ByCodes(
        string[] codes, string patchJson = "{}", bool createMissing = false, bool dryRun = false,
        ProductSpecBatchOverridesDto? overrides = null) => new()
    {
        Target = new ProductSpecBatchTargetDto { ProductCodes = codes.ToList() },
        Patch = Patch(patchJson),
        HookQtyOverrides = overrides,
        CreateMissing = createMissing,
        DryRun = dryRun,
    };

    private static ProductSpecBatchRequest ByFilter(
        ProductSpecFilterDto filter, string patchJson = "{}", bool createMissing = false, bool dryRun = false,
        ProductSpecBatchOverridesDto? overrides = null) => new()
    {
        Target = new ProductSpecBatchTargetDto { Filter = filter },
        Patch = Patch(patchJson),
        HookQtyOverrides = overrides,
        CreateMissing = createMissing,
        DryRun = dryRun,
    };

    private async Task<ProductSpecBatchResultDto> RunOkAsync(ProductSpecBatchRequest request)
    {
        var result = await _specs.BatchUpdateAsync(request, Actor, default);
        Assert.True(result.Success, result.Message + " " + JsonSerializer.Serialize(result.Details));
        return result.Data!;
    }

    private void SeedSpec(ProductDisplaySpec spec) => _db.Insertable(spec).ExecuteCommand();

    private ProductDisplaySpec? LoadSpec(string code) =>
        _db.Queryable<ProductDisplaySpec>().Where(spec => spec.ProductCode == code).First();

    private async Task<HookModelDto> CreateHookAsync(string code, bool active = true)
    {
        var created = await _models.CreateHookModelAsync(new HookModelDto
        {
            Code = code, Name = "挂钩 " + code, ProngCount = 2, HasPriceTagArm = true, ArmLengthMm = 200, DropMm = 50,
            WireDiameterMm = 5, TipReserveMm = 20, TagWidthMm = 52, TagHeightMm = 26, HoleSpanX = 2, HoleSpanY = 1, LoadKg = 3,
            IsActive = active,
        }, "seed", default);
        Assert.True(created.Success, created.Message);
        return created.Data!;
    }

    private static Dictionary<string, int> OverridesOf(ProductDisplaySpec? spec) =>
        ProductDisplaySpecService.ParseOverrides(spec?.HookQtyOverridesJson);

    // ================================================================ 列表过滤（D.1）

    [Fact]
    public async Task 过滤_仓库分类含全部子分类_未设仓库分类的商品不参与_任何供应商都可匹配()
    {
        // 子分类 W-CHILD1 → 自身 + W-GRAND：P100（供应商 200）与 P202（供应商 240）都命中。
        Assert.Equal(PlanogramBatchCatalog.Codes("P100", "P202"), await ListCodesAsync(Filter(warehouse: new[] { "W-CHILD1" })));
        Assert.Equal(PlanogramBatchCatalog.Codes("P100", "P202"), await ListCodesAsync(Filter(warehouse: new[] { "W-GRAND" })));
        // 根分类 → 整棵树（已软删的 W-DELETED 不展开，所以 P104 不在内）；P103 / P201 / P203 / P300 没设仓库分类，不参与。
        Assert.Equal(
            PlanogramBatchCatalog.Codes("P100", "P101", "P102", "P202"),
            await ListCodesAsync(Filter(warehouse: new[] { "W-ROOT" })));
        // 多选取并集。
        Assert.Equal(
            PlanogramBatchCatalog.Codes("P101", "P105", "P200"),
            await ListCodesAsync(Filter(warehouse: new[] { "W-CHILD2", "W-OTHER" })));
        // 不存在的分类、已软删的分类：返回空，而不是忽略条件。
        Assert.Empty(await ListCodesAsync(Filter(warehouse: new[] { "NO-SUCH" })));
        Assert.Empty(await ListCodesAsync(Filter(warehouse: new[] { "W-DELETED" })));
    }

    [Fact]
    public async Task 过滤_本地供应商_200含供应商为空的商品_其他供应商精确匹配_首尾空白忽略()
    {
        Assert.Equal(
            PlanogramBatchCatalog.Codes("P100", "P101", "P102", "P103", "P104", "P105"),
            await ListCodesAsync(Filter(supplier: "200")));
        Assert.Equal(PlanogramBatchCatalog.Codes("P200", "P201", "P202", "P203"), await ListCodesAsync(Filter(supplier: " 240 ")));
        Assert.Equal(new[] { "P300" }, await ListCodesAsync(Filter(supplier: "243")));
        Assert.Empty(await ListCodesAsync(Filter(supplier: "999")));
        // 空白 = 不过滤。
        Assert.Equal(PlanogramBatchCatalog.ActiveCodes, await ListCodesAsync(Filter(supplier: "  ")));
    }

    [Fact]
    public async Task 过滤_供应商分类含子分类_按所属树分流_归属供应商须等于商品当前供应商()
    {
        // 供应商 240 的根分类 → S-ROOT + S-CHILD：P200（挂在子分类）与 P201（挂在根）；
        // P203 的归属登记在 243 名下，与商品当前供应商 240 不一致，视为未归类，不命中。
        Assert.Equal(PlanogramBatchCatalog.Codes("P200", "P201"), await ListCodesAsync(Filter(supplierCategories: new[] { "S-ROOT" })));
        Assert.Equal(new[] { "P200" }, await ListCodesAsync(Filter(supplierCategories: new[] { "S-CHILD" })));
        Assert.Equal(new[] { "P300" }, await ListCodesAsync(Filter(supplierCategories: new[] { "T-ROOT" })));
        // 200 的供应商分类就是仓库分类：只命中 200 / 空供应商的商品，P202（供应商 240，仓库在子树里）不算。
        Assert.Equal(new[] { "P100" }, await ListCodesAsync(Filter(supplierCategories: new[] { "W-CHILD1" })));
        Assert.Equal(PlanogramBatchCatalog.Codes("P100", "P101", "P102"), await ListCodesAsync(Filter(supplierCategories: new[] { "W-ROOT" })));
        // 供应商分类与仓库分类混选取并集。
        Assert.Equal(
            PlanogramBatchCatalog.Codes("P101", "P200", "P201"),
            await ListCodesAsync(Filter(supplierCategories: new[] { "S-ROOT", "W-CHILD2" })));
        Assert.Empty(await ListCodesAsync(Filter(supplierCategories: new[] { "NO-SUCH" })));
    }

    [Fact]
    public async Task 过滤_仅未归类_与商品列表同口径()
    {
        // 200 / 空供应商且没设仓库分类（P103）；其他供应商且没有有效归属（P202 没登记，P203 登记的供应商对不上）。
        Assert.Equal(PlanogramBatchCatalog.Codes("P103", "P202", "P203"), await ListCodesAsync(Filter(unassigned: true)));
        Assert.Equal(PlanogramBatchCatalog.ActiveCodes, await ListCodesAsync(Filter(unassigned: false)));
        Assert.Equal(PlanogramBatchCatalog.ActiveCodes, await ListCodesAsync(Filter(unassigned: null)));
    }

    [Fact]
    public async Task 过滤_商品等级_各字母_多选并集_none_未知值报错()
    {
        // 软删的等级行视为没有等级（P201）；已停用商品不出现（P900）；"E" 不是 A～D，也不算 none（有未删除的等级行）。
        Assert.Equal(PlanogramBatchCatalog.Codes("P100", "P300"), await ListCodesAsync(Filter(grades: new[] { "A" })));
        Assert.Equal(new[] { "P101" }, await ListCodesAsync(Filter(grades: new[] { "B" })));
        Assert.Equal(new[] { "P102" }, await ListCodesAsync(Filter(grades: new[] { "C" })));
        Assert.Equal(new[] { "P200" }, await ListCodesAsync(Filter(grades: new[] { "D" })));
        Assert.Equal(
            PlanogramBatchCatalog.Codes("P100", "P101", "P300"),
            await ListCodesAsync(Filter(grades: new[] { "A", "B" })));
        Assert.Equal(
            PlanogramBatchCatalog.Codes("P103", "P104", "P201", "P202", "P203"),
            await ListCodesAsync(Filter(grades: new[] { "none" })));
        // 字母与 none 取并集。
        Assert.Equal(
            PlanogramBatchCatalog.Codes("P100", "P300", "P103", "P104", "P201", "P202", "P203"),
            await ListCodesAsync(Filter(grades: new[] { "A", "none" })));
        // 大小写、首尾空白不敏感，空值忽略（重复键 grades= 的情形）。
        Assert.Equal(
            PlanogramBatchCatalog.Codes("P100", "P101", "P300"),
            await ListCodesAsync(Filter(grades: new[] { " a ", "B", "", "  ", "a" })));
        Assert.Equal(PlanogramBatchCatalog.ActiveCodes, await ListCodesAsync(Filter(grades: new[] { "", " " })));

        var unknown = await _specs.ListAsync(Filter(grades: new[] { "A", "E" }), 1, 20, default);
        Assert.False(unknown.Success);
        Assert.Equal(PlanogramErrorCodes.Validation, unknown.ErrorCode);
        Assert.Contains(FieldErrors(unknown.Details), error => error.Field == "grades");
    }

    [Fact]
    public async Task 过滤_条件之间是并且_可与分类_供应商_状态_关键字组合()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));
        SeedSpec(PlanogramBatchCatalog.Spec("P200"));

        Assert.Equal(new[] { "P100" }, await ListCodesAsync(Filter(grades: new[] { "A" }, supplier: "200")));
        Assert.Equal(new[] { "P100" }, await ListCodesAsync(Filter(grades: new[] { "A" }, warehouse: new[] { "W-ROOT" })));
        Assert.Equal(new[] { "P202" }, await ListCodesAsync(Filter(warehouse: new[] { "W-ROOT" }, supplier: "240")));
        Assert.Equal(
            PlanogramBatchCatalog.Codes("P103", "P202", "P203"),
            await ListCodesAsync(Filter(grades: new[] { "none" }, unassigned: true)));
        Assert.Equal(new[] { "P200" }, await ListCodesAsync(Filter(status: "set", supplier: "240")));
        Assert.Equal(PlanogramBatchCatalog.Codes("P101", "P102", "P202"), await ListCodesAsync(Filter(status: "unset", warehouse: new[] { "W-ROOT" })));
        Assert.Equal(new[] { "P202" }, await ListCodesAsync(Filter(keyword: "P202", warehouse: new[] { "W-GRAND" })));
        Assert.Empty(await ListCodesAsync(Filter(keyword: "P101", warehouse: new[] { "W-GRAND" })));
        // 分类不存在时即使其他条件能匹配，也是空结果。
        Assert.Empty(await ListCodesAsync(Filter(supplierCategories: new[] { "NO-SUCH" }, supplier: "240")));
    }

    [Fact]
    public async Task 过滤_分页计数作用在过滤之后_排序仍按货号()
    {
        var first = await _specs.ListAsync(Filter(warehouse: new[] { "W-ROOT" }), 1, 3, default);
        var second = await _specs.ListAsync(Filter(warehouse: new[] { "W-ROOT" }), 2, 3, default);

        Assert.Equal(4, first.Data!.Total);
        Assert.Equal(4, second.Data!.Total);
        Assert.Equal(new[] { "P100", "P101", "P102" }, first.Data.Items!.Select(item => item.ProductCode));
        Assert.Equal(new[] { "P202" }, second.Data.Items!.Select(item => item.ProductCode));
        Assert.Equal((2, 3), (second.Data.Page, second.Data.PageSize));

        // 旧签名（keyword, status, …）仍可用，行为不变。
        var legacy = await _specs.ListAsync(null, null, 1, 100, default);
        Assert.Equal(PlanogramBatchCatalog.ActiveCodes.Length, legacy.Data!.Total);
    }

    [Fact]
    public async Task 过滤_列表行带只读等级_仅A到D_软删等级行与非A到D为空()
    {
        var rows = (await _specs.ListAsync(Filter(), 1, 100, default)).Data!.Items!.ToDictionary(item => item.ProductCode);

        Assert.Equal("A", rows["P100"].Grade);
        Assert.Equal("B", rows["P101"].Grade);
        Assert.Equal("C", rows["P102"].Grade);
        Assert.Equal("D", rows["P200"].Grade);
        Assert.Equal("A", rows["P300"].Grade);
        Assert.Null(rows["P103"].Grade);
        Assert.Null(rows["P105"].Grade); // 等级行是 "E"，不在 A～D
        Assert.Null(rows["P201"].Grade); // 只有已软删的等级行
        // 单个读取同样带等级。
        Assert.Equal("A", (await _specs.GetAsync("P100", default)).Data!.Grade);
    }

    [Fact]
    public async Task 过滤_列表行带只读仓库分类路径_逐级祖先拼出_未设或分类已不存在为空()
    {
        var rows = (await _specs.ListAsync(Filter(), 1, 100, default)).Data!.Items!.ToDictionary(item => item.ProductCode);

        Assert.Equal("百货 > 收纳 > 收纳盒", rows["P100"].WarehouseCategoryPath);
        Assert.Equal("百货 > 清洁", rows["P101"].WarehouseCategoryPath);
        Assert.Equal("百货", rows["P102"].WarehouseCategoryPath);
        Assert.Equal("文具", rows["P105"].WarehouseCategoryPath);
        Assert.Equal("文具", rows["P200"].WarehouseCategoryPath); // 与供应商无关，任何供应商的商品都带
        Assert.Equal("百货 > 收纳 > 收纳盒", rows["P202"].WarehouseCategoryPath);
        Assert.Equal("百货 > 已删分类", rows["P104"].WarehouseCategoryPath); // 路径只是展示，分类软删后仍能显示
        Assert.Null(rows["P103"].WarehouseCategoryPath); // 没设仓库分类
        Assert.Null(rows["P300"].WarehouseCategoryPath);

        // 商品上的仓库分类 GUID 在分类表里已不存在：为空，不报错。
        _db.Updateable<Product>().SetColumns(product => product.WarehouseCategoryGUID == "GHOST").Where(product => product.ProductCode == "P103").ExecuteCommand();
        Assert.Null((await _specs.GetAsync("P103", default)).Data!.WarehouseCategoryPath);
        // 单个读取同样带路径。
        Assert.Equal("百货 > 收纳 > 收纳盒", (await _specs.GetAsync("P100", default)).Data!.WarehouseCategoryPath);
    }

    [Fact]
    public async Task 过滤_分类GUID_空值空白重复被忽略_超过200个报校验错误()
    {
        // 空串 / 空白 / 重复（含大小写不同的重复）都忽略。
        Assert.Equal(
            PlanogramBatchCatalog.Codes("P100", "P202"),
            await ListCodesAsync(Filter(warehouse: new[] { "  W-GRAND ", "w-grand", "", " ", "W-GRAND" })));
        // 全是空值 = 不过滤。
        Assert.Equal(PlanogramBatchCatalog.ActiveCodes, await ListCodesAsync(Filter(warehouse: new[] { "", "  " }, supplierCategories: new[] { " " })));

        // 恰好 200 个不同的 GUID 可以（都不存在 → 空结果，不报错）；201 个报错。
        var many = Enumerable.Range(1, 200).Select(i => $"NO-SUCH-{i}").ToArray();
        Assert.Empty(await ListCodesAsync(Filter(warehouse: many)));
        Assert.Empty(await ListCodesAsync(Filter(supplierCategories: many)));
        // 201 个但去重后只有 200 个，不算超限。
        Assert.Empty(await ListCodesAsync(Filter(warehouse: many.Append("no-such-1").ToArray())));

        var tooMany = many.Append("NO-SUCH-201").ToArray();
        var warehouse = await _specs.ListAsync(Filter(warehouse: tooMany), 1, 20, default);
        Assert.Equal(PlanogramErrorCodes.Validation, warehouse.ErrorCode);
        Assert.Contains(FieldErrors(warehouse.Details), error => error.Field == "warehouseCategoryGuids");
        var supplier = await _specs.ListAsync(Filter(supplierCategories: tooMany), 1, 20, default);
        Assert.Equal(PlanogramErrorCodes.Validation, supplier.ErrorCode);
        Assert.Contains(FieldErrors(supplier.Details), error => error.Field == "supplierCategoryGuids");
        // 状态不合法与超限一起报。
        var both = await _specs.ListAsync(Filter(status: "weird", warehouse: tooMany), 1, 20, default);
        Assert.Equal(new[] { "status", "warehouseCategoryGuids" }, FieldErrors(both.Details).Select(error => error.Field).OrderBy(name => name, StringComparer.Ordinal));
    }

    // ================================================================ 批量：目标解析

    [Fact]
    public async Task 批量_按货号_dryRun不落库_真正执行的计数与预览一致_审计列与单个保存一致()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100", notes: "旧备注"));
        SeedSpec(PlanogramBatchCatalog.Spec("P200", width: 100));
        var codes = new[] { "P100", "P200", "P101", "P999" };

        var preview = await RunOkAsync(ByCodes(codes, """{"widthMm":70,"notes":"批量"}""", dryRun: true));

        Assert.True(preview.DryRun);
        Assert.Equal(4, preview.Matched);
        Assert.Equal((0, 2, 0), (preview.WillCreate, preview.WillUpdate, preview.WillStayUnchanged));
        Assert.Equal((0, 0, 0), (preview.Created, preview.Updated, preview.Unchanged));
        Assert.Equal(2, preview.Skipped);
        var noSpec = Assert.Single(preview.SkippedItems, item => item.ProductCode == "P101");
        Assert.Equal((PlanogramBatchSkipReasons.NoSpec, "IT-101", "商品P101"), (noSpec.Reason, noSpec.ItemNumber, noSpec.ProductName));
        var missing = Assert.Single(preview.SkippedItems, item => item.ProductCode == "P999");
        Assert.Equal((PlanogramBatchSkipReasons.ProductNotFound, string.Empty, string.Empty), (missing.Reason, missing.ItemNumber, missing.ProductName));
        // dryRun 不写库：参数与审计列都没动。
        Assert.Equal(120, LoadSpec("P100")!.WidthMm);
        Assert.Equal("seed", LoadSpec("P100")!.UpdatedBy);
        Assert.Null(LoadSpec("P101"));

        var executed = await RunOkAsync(ByCodes(codes, """{"widthMm":70,"notes":"批量"}"""));

        Assert.False(executed.DryRun);
        Assert.Equal(4, executed.Matched);
        Assert.Equal((0, 2, 0), (executed.Created, executed.Updated, executed.Unchanged));
        Assert.Equal((0, 0, 0), (executed.WillCreate, executed.WillUpdate, executed.WillStayUnchanged));
        Assert.Equal(preview.Skipped, executed.Skipped);
        Assert.Equal(
            preview.SkippedItems.Select(item => (item.ProductCode, item.Reason)),
            executed.SkippedItems.Select(item => (item.ProductCode, item.Reason)));
        var saved = LoadSpec("P100")!;
        Assert.Equal((70, 180, 30), (saved.WidthMm, saved.HeightMm, saved.DepthMm));
        Assert.Equal("批量", saved.Notes);
        Assert.Equal(200, saved.UnitWeightG); // 没勾选的字段保持原值
        Assert.Equal(70, LoadSpec("P200")!.WidthMm);
        // 审计列：UpdatedBy 为当前用户、UpdatedAt 为当前时间；CreatedBy / CreatedAt 不动。
        Assert.Equal((Actor, Now), (saved.UpdatedBy, saved.UpdatedAt));
        Assert.Equal(("seed", PlanogramBatchCatalog.SeedTime), (saved.CreatedBy, saved.CreatedAt));
        // 没勾选「创建」：P101 仍然没有参数。
        Assert.Null(LoadSpec("P101"));
    }

    [Fact]
    public async Task 批量_按筛选_全部匹配商品_创建缺参数的并更新已有的()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));
        var filter = Filter(supplier: "200");
        const string patch = """{"widthMm":50,"heightMm":60,"depthMm":20}""";

        var preview = await RunOkAsync(ByFilter(filter, patch, createMissing: true, dryRun: true));

        // 供应商 200 共 6 个启用商品（含供应商为空的），P100 已有参数，其余 5 个创建。
        Assert.Equal(6, preview.Matched);
        Assert.Equal((5, 1, 0, 0), (preview.WillCreate, preview.WillUpdate, preview.WillStayUnchanged, preview.Skipped));
        Assert.Equal(1, await _db.Queryable<ProductDisplaySpec>().CountAsync());

        var executed = await RunOkAsync(ByFilter(filter, patch, createMissing: true));

        Assert.Equal((5, 1, 0, 0), (executed.Created, executed.Updated, executed.Unchanged, executed.Skipped));
        Assert.Equal(6, await _db.Queryable<ProductDisplaySpec>().CountAsync());
        var created = LoadSpec("P103")!;
        Assert.Equal((50, 60, 20), (created.WidthMm, created.HeightMm, created.DepthMm));
        // 创建时没给的字段取缺省：吊孔偏移 10、吊孔类型 2、来源 1，其余为空。
        Assert.Equal((10, (byte)2, (byte)1), (created.HangHoleOffsetMm, created.HangHoleType, created.Source));
        Assert.Null(created.UnitWeightG);
        Assert.Null(created.DefaultHookGuid);
        Assert.Null(created.HookQtyOverridesJson);
        Assert.Null(created.Notes);
        Assert.Equal((Actor, Now, Actor, Now), (created.CreatedBy, created.CreatedAt, created.UpdatedBy, created.UpdatedAt));
        // 不在筛选里的商品没被创建。
        Assert.Null(LoadSpec("P200"));
    }

    [Fact]
    public async Task 批量_按筛选_带分类状态和等级条件_只处理匹配的商品()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P101"));
        // 仓库 W-ROOT 下尚未设参数的商品：P102、P202（P101 已有、P100 在 status=unset 里也排除不了？P100 没参数，所以也算）。
        var preview = await RunOkAsync(ByFilter(
            Filter(status: "unset", warehouse: new[] { "W-ROOT" }), """{"notes":"x"}""", dryRun: true));
        Assert.Equal(3, preview.Matched); // P100 / P102 / P202
        Assert.Equal(3, preview.Skipped); // 都没有参数，也没勾选创建
        Assert.All(preview.SkippedItems, item => Assert.Equal(PlanogramBatchSkipReasons.NoSpec, item.Reason));

        // 等级 A + 未分级：按并集解析目标。
        var graded = await RunOkAsync(ByFilter(Filter(grades: new[] { "A", "none" }), """{"notes":"x"}""", dryRun: true));
        Assert.Equal(7, graded.Matched);
        // 未归类 + 供应商 240。
        var unassigned = await RunOkAsync(ByFilter(Filter(unassigned: true, supplier: "240"), """{"notes":"x"}""", dryRun: true));
        Assert.Equal(2, unassigned.Matched); // P202 / P203
        // 供应商分类子树。
        var tree = await RunOkAsync(ByFilter(Filter(supplierCategories: new[] { "S-ROOT" }), """{"notes":"x"}""", dryRun: true));
        Assert.Equal(2, tree.Matched);
    }

    [Fact]
    public async Task 批量_目标为空_没有匹配_或二选一冲突_各有错误码()
    {
        var patch = Patch("""{"notes":"x"}""");

        foreach (var request in new[]
        {
            new ProductSpecBatchRequest { Patch = patch },
            new ProductSpecBatchRequest { Patch = patch, Target = new ProductSpecBatchTargetDto() },
            new ProductSpecBatchRequest { Patch = patch, Target = new ProductSpecBatchTargetDto { ProductCodes = new List<string>() } },
            new ProductSpecBatchRequest { Patch = patch, Target = new ProductSpecBatchTargetDto { ProductCodes = new List<string> { "", "  " } } },
            // 筛选没有匹配到任何商品。
            new ProductSpecBatchRequest { Patch = patch, Target = new ProductSpecBatchTargetDto { Filter = Filter(supplier: "999") } },
        })
        {
            var result = await _specs.BatchUpdateAsync(request, Actor, default);
            Assert.False(result.Success);
            Assert.Equal(PlanogramErrorCodes.BatchEmpty, result.ErrorCode);
        }

        // productCodes 与 filter 只能二选一。
        var both = await _specs.BatchUpdateAsync(new ProductSpecBatchRequest
        {
            Patch = patch,
            Target = new ProductSpecBatchTargetDto { ProductCodes = new List<string> { "P100" }, Filter = Filter() },
        }, Actor, default);
        Assert.Equal(PlanogramErrorCodes.Validation, both.ErrorCode);
        Assert.Contains(FieldErrors(both.Details), error => error.Field == "target");

        // 目标里的筛选条件不合法：错误挂在 target.filter. 下。
        var badFilter = await _specs.BatchUpdateAsync(
            ByFilter(Filter(grades: new[] { "Z" }, status: "weird"), """{"notes":"x"}"""), Actor, default);
        Assert.Equal(PlanogramErrorCodes.Validation, badFilter.ErrorCode);
        Assert.Equal(
            new[] { "target.filter.grades", "target.filter.status" },
            FieldErrors(badFilter.Details).Select(error => error.Field).OrderBy(name => name, StringComparer.Ordinal));

        // 补丁不合法优先于目标为空。
        var invalidPatch = await _specs.BatchUpdateAsync(new ProductSpecBatchRequest { Patch = Patch("""{"foo":1}""") }, Actor, default);
        Assert.Equal(PlanogramErrorCodes.Validation, invalidPatch.ErrorCode);
    }

    [Fact]
    public async Task 批量_货号超过2000个报TOO_MANY_恰好2000个可以_跳过明细最多200条但跳过总数是真实值()
    {
        var codes2001 = Enumerable.Range(1, 2001).Select(i => $"X{i:D5}").ToArray();
        var tooMany = await _specs.BatchUpdateAsync(ByCodes(codes2001, """{"notes":"x"}"""), Actor, default);
        Assert.Equal(PlanogramErrorCodes.BatchTooMany, tooMany.ErrorCode);
        Assert.Equal(new PlanogramBatchLimitDetails(2001, 2000), tooMany.Details);

        // 重复货号去重后再算：2001 个里有重复，实际 2000 个不同的。
        var deduped = codes2001.Take(2000).Append("x00001").ToArray();
        var exactly = await RunOkAsync(ByCodes(deduped, """{"notes":"x"}""", dryRun: true));
        Assert.Equal(2000, exactly.Matched);
        Assert.Equal(2000, exactly.Skipped); // 货号都不存在
        Assert.Equal(200, exactly.SkippedItems.Count); // 明细最多 200 条，skipped 仍是真实总数
        Assert.All(exactly.SkippedItems, item => Assert.Equal(PlanogramBatchSkipReasons.ProductNotFound, item.Reason));
    }

    [Fact]
    public async Task 批量_筛选匹配超过2000个报TOO_MANY并带匹配数_恰好2000个可以()
    {
        SeedBulkProducts("777", 2001);

        var tooMany = await _specs.BatchUpdateAsync(ByFilter(Filter(supplier: "777"), """{"notes":"x"}"""), Actor, default);
        Assert.Equal(PlanogramErrorCodes.BatchTooMany, tooMany.ErrorCode);
        Assert.Equal(new PlanogramBatchLimitDetails(2001, 2000), tooMany.Details);

        _db.Deleteable<Product>().Where(product => product.ProductCode == "B02001").ExecuteCommand();
        var exactly = await RunOkAsync(ByFilter(
            Filter(supplier: "777"), """{"widthMm":50,"heightMm":60,"depthMm":20}""", createMissing: true, dryRun: true));
        Assert.Equal(2000, exactly.Matched);
        Assert.Equal((2000, 0, 0, 0), (exactly.WillCreate, exactly.WillUpdate, exactly.WillStayUnchanged, exactly.Skipped));
    }

    private void SeedBulkProducts(string supplier, int count, bool withSpec = false, int width = 100)
    {
        var products = Enumerable.Range(1, count).Select(i => new Product
        {
            ProductCode = $"B{i:D5}", ItemNumber = $"BI-{i:D5}", ProductName = $"批量商品{i}", LocalSupplierCode = supplier,
        }).ToList();
        _db.Insertable(products).PageSize(100).ExecuteCommand();
        if (withSpec)
        {
            _db.Insertable(products.Select(product => PlanogramBatchCatalog.Spec(product.ProductCode!, width: width)).ToList())
                .PageSize(100).ExecuteCommand();
        }
    }

    // ================================================================ 批量：补丁校验

    public static IEnumerable<object[]> InvalidPatches()
    {
        yield return new object[] { """{"foo":1}""", "foo" };
        yield return new object[] { """{"productCode":"P200"}""", "productCode" }; // 不在白名单
        yield return new object[] { """{"updatedBy":"x"}""", "updatedBy" };
        yield return new object[] { """{"WidthMm":70}""", "WidthMm" }; // 键区分大小写，必须是 camelCase
        yield return new object[] { """{"widthMm":"70"}""", "widthMm" };
        yield return new object[] { """{"widthMm":70.5}""", "widthMm" };
        yield return new object[] { """{"widthMm":true}""", "widthMm" };
        yield return new object[] { """{"widthMm":null}""", "widthMm" }; // 非空字段不能清空
        yield return new object[] { """{"widthMm":0}""", "widthMm" };
        yield return new object[] { """{"widthMm":10001}""", "widthMm" };
        yield return new object[] { """{"heightMm":0}""", "heightMm" };
        yield return new object[] { """{"heightMm":null}""", "heightMm" };
        yield return new object[] { """{"depthMm":10001}""", "depthMm" };
        yield return new object[] { """{"depthMm":"x"}""", "depthMm" };
        yield return new object[] { """{"hangHoleOffsetMm":-1}""", "hangHoleOffsetMm" };
        yield return new object[] { """{"hangHoleOffsetMm":null}""", "hangHoleOffsetMm" };
        yield return new object[] { """{"hangHoleType":3}""", "hangHoleType" };
        yield return new object[] { """{"hangHoleType":-1}""", "hangHoleType" };
        yield return new object[] { """{"hangHoleType":null}""", "hangHoleType" };
        yield return new object[] { """{"source":0}""", "source" };
        yield return new object[] { """{"source":4}""", "source" };
        yield return new object[] { """{"source":null}""", "source" };
        yield return new object[] { """{"unitWeightG":0}""", "unitWeightG" };
        yield return new object[] { """{"unitWeightG":1000001}""", "unitWeightG" };
        yield return new object[] { """{"unitWeightG":"x"}""", "unitWeightG" };
        yield return new object[] { """{"unitWeightG":1.5}""", "unitWeightG" };
        yield return new object[] { """{"notes":123}""", "notes" };
        yield return new object[] { """{"defaultHookGuid":123}""", "defaultHookGuid" };
        yield return new object[] { """{"defaultHookGuid":"no-such-hook"}""", "defaultHookGuid" };
    }

    [Theory]
    [MemberData(nameof(InvalidPatches))]
    public async Task 批量_补丁不合法_整批不执行_校验错误带字段名(string patchJson, string expectedField)
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));

        foreach (var dryRun in new[] { true, false })
        {
            var result = await _specs.BatchUpdateAsync(ByCodes(new[] { "P100", "P200" }, patchJson, createMissing: true, dryRun: dryRun), Actor, default);

            Assert.False(result.Success);
            Assert.Equal(PlanogramErrorCodes.Validation, result.ErrorCode);
            Assert.Contains(FieldErrors(result.Details), error => error.Field == expectedField && !string.IsNullOrWhiteSpace(error.Message));
        }

        // 什么都没写：P100 保持原样，P200 没被创建。
        var untouched = LoadSpec("P100")!;
        Assert.Equal((120, 180, 30, "seed"), (untouched.WidthMm, untouched.HeightMm, untouched.DepthMm, untouched.UpdatedBy));
        Assert.Null(LoadSpec("P200"));
    }

    [Fact]
    public async Task 批量_多个补丁错误一次返回_超长备注也报错()
    {
        var multi = await _specs.BatchUpdateAsync(
            ByCodes(new[] { "P100" }, """{"foo":1,"widthMm":0,"unitWeightG":"x","hangHoleType":9}"""), Actor, default);
        Assert.Equal(
            new[] { "foo", "hangHoleType", "unitWeightG", "widthMm" },
            FieldErrors(multi.Details).Select(error => error.Field).Distinct().OrderBy(name => name, StringComparer.Ordinal));

        var longNotes = new string('备', 501);
        var tooLong = await _specs.BatchUpdateAsync(
            ByCodes(new[] { "P100" }, $$"""{"notes":"{{longNotes}}"}"""), Actor, default);
        Assert.Contains(FieldErrors(tooLong.Details), error => error.Field == "notes");

        // 恰好 500 个字符可以；首尾空白不计入。
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));
        var ok = new string('备', 500);
        await RunOkAsync(ByCodes(new[] { "P100" }, $$"""{"notes":"  {{ok}}  "}"""));
        Assert.Equal(ok, LoadSpec("P100")!.Notes);
    }

    [Fact]
    public async Task 批量_null清空可空字段_空白字符串同样清空_其余字段保持()
    {
        var hook = await CreateHookAsync("HK-1");
        SeedSpec(PlanogramBatchCatalog.Spec("P100", unitWeight: 200, defaultHook: hook.Guid, notes: "旧备注"));
        SeedSpec(PlanogramBatchCatalog.Spec("P200", unitWeight: 300, defaultHook: hook.Guid, notes: "另一条"));

        var cleared = await RunOkAsync(ByCodes(new[] { "P100" }, """{"unitWeightG":null,"notes":null,"defaultHookGuid":null}"""));
        Assert.Equal(1, cleared.Updated);
        var p100 = LoadSpec("P100")!;
        Assert.Null(p100.UnitWeightG);
        Assert.Null(p100.Notes);
        Assert.Null(p100.DefaultHookGuid);
        Assert.Equal((120, 180, 30), (p100.WidthMm, p100.HeightMm, p100.DepthMm));

        // 空白字符串与单个保存一致：按清空处理。
        await RunOkAsync(ByCodes(new[] { "P200" }, """{"notes":"   ","defaultHookGuid":""}"""));
        var p200 = LoadSpec("P200")!;
        Assert.Null(p200.Notes);
        Assert.Null(p200.DefaultHookGuid);
        Assert.Equal(300, p200.UnitWeightG);
    }

    [Fact]
    public async Task 批量_空补丁合法_全部计为不变或跳过()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));

        var result = await RunOkAsync(ByCodes(new[] { "P100", "P101" }, "{}"));

        Assert.Equal((0, 0, 1, 1), (result.Created, result.Updated, result.Unchanged, result.Skipped));
        Assert.Equal("seed", LoadSpec("P100")!.UpdatedBy);
    }

    // ================================================================ 批量：每钩挂数覆盖

    [Fact]
    public async Task 批量_挂数覆盖_merge保留其余_replace整体替换_空对象清空()
    {
        var h1 = await CreateHookAsync("HK-1");
        var h2 = await CreateHookAsync("HK-2");
        var h3 = await CreateHookAsync("HK-3");
        var existing = $$"""{"{{h1.Guid}}":6,"{{h2.Guid}}":8}""";
        SeedSpec(PlanogramBatchCatalog.Spec("P100", overridesJson: existing));
        SeedSpec(PlanogramBatchCatalog.Spec("P200"));

        // merge：只覆盖给出的挂钩，其余保留；没有覆盖的商品得到新增项。
        var merged = await RunOkAsync(ByCodes(
            new[] { "P100", "P200" }, "{}", overrides: Overrides("merge", $$"""{"{{h2.Guid}}":10,"{{h3.Guid}}":4}""")));
        Assert.Equal((2, 0), (merged.Updated, merged.Unchanged));
        Assert.Equal(
            new Dictionary<string, int> { [h1.Guid] = 6, [h2.Guid] = 10, [h3.Guid] = 4 }.OrderBy(pair => pair.Key),
            OverridesOf(LoadSpec("P100")).OrderBy(pair => pair.Key));
        Assert.Equal(
            new Dictionary<string, int> { [h2.Guid] = 10, [h3.Guid] = 4 }.OrderBy(pair => pair.Key),
            OverridesOf(LoadSpec("P200")).OrderBy(pair => pair.Key));
        // 序列化后键已排序，与单个保存的写法一致。
        var json = LoadSpec("P100")!.HookQtyOverridesJson!;
        var keys = JsonDocument.Parse(json).RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(keys.OrderBy(key => key, StringComparer.Ordinal), keys);

        // replace：整体替换为 values。
        await RunOkAsync(ByCodes(new[] { "P100" }, "{}", overrides: Overrides("replace", $$"""{"{{h3.Guid}}":2}""")));
        Assert.Equal(new Dictionary<string, int> { [h3.Guid] = 2 }, OverridesOf(LoadSpec("P100")));

        // replace + 空对象 = 清空全部覆盖（库里存 NULL）。
        var cleared = await RunOkAsync(ByCodes(new[] { "P100", "P200" }, "{}", overrides: Overrides("replace", "{}")));
        Assert.Equal(2, cleared.Updated);
        Assert.Null(LoadSpec("P100")!.HookQtyOverridesJson);
        Assert.Null(LoadSpec("P200")!.HookQtyOverridesJson);

        // 再清一次：已经是空 → 不变；merge + 空对象同样不变。
        var again = await RunOkAsync(ByCodes(new[] { "P100" }, "{}", overrides: Overrides("replace", "{}")));
        Assert.Equal((0, 1), (again.Updated, again.Unchanged));
        var noop = await RunOkAsync(ByCodes(new[] { "P100" }, "{}", overrides: Overrides("MERGE", "{}")));
        Assert.Equal((0, 1), (noop.Updated, noop.Unchanged));
    }

    [Fact]
    public async Task 批量_挂数覆盖_与补丁字段一起写_停用的挂钩型号也算存在_dryRun不落库()
    {
        var inactive = await CreateHookAsync("HK-OFF", active: false);
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));

        var preview = await RunOkAsync(ByCodes(
            new[] { "P100" }, $$"""{"notes":"同时改","defaultHookGuid":"{{inactive.Guid}}"}""", dryRun: true,
            overrides: Overrides("merge", $$"""{"{{inactive.Guid}}":5}""")));
        Assert.Equal(1, preview.WillUpdate);
        Assert.Null(LoadSpec("P100")!.HookQtyOverridesJson);

        await RunOkAsync(ByCodes(
            new[] { "P100" }, $$"""{"notes":"同时改","defaultHookGuid":"{{inactive.Guid}}"}""",
            overrides: Overrides("merge", $$"""{"{{inactive.Guid}}":5}""")));
        var saved = LoadSpec("P100")!;
        Assert.Equal((inactive.Guid, "同时改"), (saved.DefaultHookGuid, saved.Notes));
        Assert.Equal(5, OverridesOf(saved)[inactive.Guid]);
    }

    [Fact]
    public async Task 批量_挂数覆盖不合法_整批不执行()
    {
        var hook = await CreateHookAsync("HK-1");
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));
        var good = $$"""{"{{hook.Guid}}":5}""";
        var cases = new (ProductSpecBatchOverridesDto Overrides, string Why)[]
        {
            (Overrides("bad", good), "mode 不认识"),
            (Overrides(string.Empty, good), "mode 为空"),
            (new ProductSpecBatchOverridesDto { Values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(good) }, "mode 缺省"),
            (Overrides("replace", null), "values 缺省（不能当成清空）"),
            (Overrides("merge", $$"""{"{{hook.Guid}}":0}"""), "挂数 0"),
            (Overrides("merge", $$"""{"{{hook.Guid}}":10000}"""), "挂数超上限"),
            (Overrides("merge", $$"""{"{{hook.Guid}}":-3}"""), "挂数为负"),
            (Overrides("merge", $$"""{"{{hook.Guid}}":"5"}"""), "挂数是字符串"),
            (Overrides("merge", $$"""{"{{hook.Guid}}":1.5}"""), "挂数是小数"),
            (Overrides("merge", $$"""{"{{hook.Guid}}":null}"""), "挂数为 null"),
            (Overrides("merge", """{"no-such-hook":5}"""), "挂钩型号不存在"),
        };

        foreach (var (overrides, why) in cases)
        {
            var result = await _specs.BatchUpdateAsync(ByCodes(new[] { "P100" }, """{"notes":"x"}""", overrides: overrides), Actor, default);
            Assert.False(result.Success, why);
            Assert.Equal(PlanogramErrorCodes.Validation, result.ErrorCode);
            Assert.Contains(FieldErrors(result.Details), error => error.Field == "hookQtyOverrides");
        }

        Assert.Null(LoadSpec("P100")!.Notes); // 同批的合法补丁也没执行
    }

    [Fact]
    public async Task 批量_挂数覆盖超出1000字符上限_只跳过该商品_与单个保存同一阈值_不截断()
    {
        // 30 个挂钩型号；每项约 42 个字符（36 位 guid + 引号 + 冒号 + 挂数 + 逗号）。
        var hooks = Enumerable.Range(1, 30).Select(i => new PlanogramHookModel
        {
            Guid = Guid.NewGuid().ToString(), Code = $"HK-{i:D2}", Name = $"挂钩{i}", ProngCount = 2, ArmLengthMm = 200,
            DropMm = 50, WireDiameterMm = 5, TipReserveMm = 20, HoleSpanX = 2, HoleSpanY = 1,
        }).ToList();
        _db.Insertable(hooks).ExecuteCommand();
        string Json(IEnumerable<PlanogramHookModel> items) =>
            JsonSerializer.Serialize(items.ToDictionary(item => item.Guid, _ => 10));

        var twenty = Json(hooks.Take(20));
        Assert.True(twenty.Length < 1000);
        SeedSpec(PlanogramBatchCatalog.Spec("P100", overridesJson: twenty));
        SeedSpec(PlanogramBatchCatalog.Spec("P200"));

        // P100 合并后 26 项 ≈ 1090 字符 → 跳过；P200 只有新增的 6 项 → 正常写入。
        var sixMore = Json(hooks.Skip(20).Take(6));
        var preview = await RunOkAsync(ByCodes(new[] { "P100", "P200" }, "{}", dryRun: true, overrides: Overrides("merge", sixMore)));
        Assert.Equal((2, 1, 1), (preview.Matched, preview.WillUpdate, preview.Skipped));
        Assert.Equal(PlanogramBatchSkipReasons.OverridesTooLong, Assert.Single(preview.SkippedItems).Reason);
        Assert.Equal("P100", preview.SkippedItems[0].ProductCode);

        var executed = await RunOkAsync(ByCodes(new[] { "P100", "P200" }, """{"notes":"同批"}""", overrides: Overrides("merge", sixMore)));
        Assert.Equal((1, 1), (executed.Updated, executed.Skipped));
        var p100 = LoadSpec("P100")!;
        Assert.Equal(twenty, p100.HookQtyOverridesJson); // 没有被截断，也没有被改动
        Assert.Null(p100.Notes); // 跳过的商品整行不动，补丁里的备注也不写
        Assert.Equal(6, OverridesOf(LoadSpec("P200")).Count);
        Assert.Equal("同批", LoadSpec("P200")!.Notes);

        // 单个保存对同样的内容也是拒绝：26 项被拒，20 项可以。
        var all26 = hooks.Take(26).ToDictionary(item => item.Guid, _ => 10);
        var rejected = await _specs.SaveAsync("P101", new ProductDisplaySpecDto { WidthMm = 10, HeightMm = 10, DepthMm = 10, HookQtyOverrides = all26 }, Actor, default);
        Assert.Equal(PlanogramErrorCodes.Validation, rejected.ErrorCode);
        var accepted = await _specs.SaveAsync(
            "P101", new ProductDisplaySpecDto { WidthMm = 10, HeightMm = 10, DepthMm = 10, HookQtyOverrides = hooks.Take(20).ToDictionary(item => item.Guid, _ => 10) }, Actor, default);
        Assert.True(accepted.Success, accepted.Message);
    }

    // ================================================================ 批量：创建 / 跳过规则

    [Fact]
    public async Task 批量_createMissing关闭时未设参数的商品计NO_SPEC_开启但缺宽高厚计MISSING_DIMENSIONS()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P200"));
        var codes = new[] { "P101", "P102", "P200" };

        var off = await RunOkAsync(ByCodes(codes, """{"notes":"x"}"""));
        Assert.Equal((0, 1, 2), (off.Created, off.Updated, off.Skipped));
        Assert.All(off.SkippedItems, item => Assert.Equal(PlanogramBatchSkipReasons.NoSpec, item.Reason));
        Assert.Null(LoadSpec("P101"));

        // 开启创建，但补丁缺深度（宽、高、厚必须同时给）：这两个商品跳过，已有参数的 P200 照常更新。
        var partial = await RunOkAsync(ByCodes(codes, """{"widthMm":50,"heightMm":60,"notes":"y"}""", createMissing: true));
        Assert.Equal((0, 1, 2), (partial.Created, partial.Updated, partial.Skipped));
        Assert.All(partial.SkippedItems, item => Assert.Equal(PlanogramBatchSkipReasons.MissingDimensions, item.Reason));
        Assert.Null(LoadSpec("P102"));

        // 只改挂数覆盖（不含宽高厚）同样不能创建。
        var hook = await CreateHookAsync("HK-1");
        var overridesOnly = await RunOkAsync(ByCodes(
            new[] { "P101" }, "{}", createMissing: true, overrides: Overrides("replace", $$"""{"{{hook.Guid}}":3}""")));
        Assert.Equal(PlanogramBatchSkipReasons.MissingDimensions, Assert.Single(overridesOnly.SkippedItems).Reason);
    }

    [Fact]
    public async Task 批量_创建时补丁给的字段生效_其余取缺省_商品不存在或已停用计PRODUCT_NOT_FOUND()
    {
        var hook = await CreateHookAsync("HK-1");
        const string dims = "\"widthMm\":50,\"heightMm\":60,\"depthMm\":20";
        var patch = $$"""{ {{dims}},"hangHoleType":0,"hangHoleOffsetMm":5,"source":2,"unitWeightG":150,"notes":"新建","defaultHookGuid":"{{hook.Guid}}" }""";

        var result = await RunOkAsync(ByCodes(
            new[] { "P101", "P900", "P901", "NOPE" }, patch, createMissing: true,
            overrides: Overrides("replace", $$"""{"{{hook.Guid}}":3}""")));

        Assert.Equal((1, 0, 3), (result.Created, result.Updated, result.Skipped));
        Assert.All(result.SkippedItems, item => Assert.Equal(PlanogramBatchSkipReasons.ProductNotFound, item.Reason));
        // 已停用 / 已软删的商品也带出货号与名称，方便前端展示；主档里根本没有的货号为空。
        Assert.Equal("商品P900", result.SkippedItems.Single(item => item.ProductCode == "P900").ProductName);
        Assert.Equal("IT-901", result.SkippedItems.Single(item => item.ProductCode == "P901").ItemNumber);
        Assert.Equal(string.Empty, result.SkippedItems.Single(item => item.ProductCode == "NOPE").ProductName);

        var created = LoadSpec("P101")!;
        Assert.Equal((50, 60, 20, 5), (created.WidthMm, created.HeightMm, created.DepthMm, created.HangHoleOffsetMm));
        Assert.Equal(((byte)0, (byte)2, 150, "新建", hook.Guid), (created.HangHoleType, created.Source, created.UnitWeightG, created.Notes, created.DefaultHookGuid));
        Assert.Equal(3, OverridesOf(created)[hook.Guid]);
        Assert.Null(LoadSpec("P900"));
        Assert.Null(LoadSpec("P901"));

        // 创建后列表里显示已设参数。
        Assert.True((await _specs.GetAsync("P101", default)).Data!.HasSpec);
    }

    [Fact]
    public async Task 批量_吊孔偏移超过改后的包装高_逐商品跳过_类型为无不判_等于高度允许()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100", height: 180));
        SeedSpec(PlanogramBatchCatalog.Spec("P200", height: 90));
        SeedSpec(PlanogramBatchCatalog.Spec("P101", height: 90, type: 0)); // 吊孔类型「无」
        var codes = new[] { "P100", "P200", "P101" };

        // 偏移 100：P100（高 180）通过；P200（高 90）超高跳过；P101 类型为无，不判。
        var result = await RunOkAsync(ByCodes(codes, """{"hangHoleOffsetMm":100}"""));
        Assert.Equal((2, 1), (result.Updated, result.Skipped));
        var skipped = Assert.Single(result.SkippedItems);
        Assert.Equal(("P200", PlanogramBatchSkipReasons.OffsetExceedsHeight), (skipped.ProductCode, skipped.Reason));
        Assert.Equal(10, LoadSpec("P200")!.HangHoleOffsetMm);
        Assert.Equal(100, LoadSpec("P100")!.HangHoleOffsetMm);

        // 把高度压到 50：P100 现有偏移 100 > 50 → 跳过；P200 偏移仍是 10、P101 类型为无 → 都通过。
        var shrink = await RunOkAsync(ByCodes(codes, """{"heightMm":50}"""));
        Assert.Equal((2, 1), (shrink.Updated, shrink.Skipped));
        var shrunk = Assert.Single(shrink.SkippedItems);
        Assert.Equal(("P100", PlanogramBatchSkipReasons.OffsetExceedsHeight), (shrunk.ProductCode, shrunk.Reason));
        Assert.Equal(180, LoadSpec("P100")!.HeightMm);
        Assert.Equal(50, LoadSpec("P200")!.HeightMm);
        Assert.Equal(50, LoadSpec("P101")!.HeightMm);
    }

    [Fact]
    public async Task 批量_偏移等于包装高允许_把类型改为无后高度可以小于偏移_创建时同样判断()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100", height: 180));

        var equal = await RunOkAsync(ByCodes(new[] { "P100" }, """{"hangHoleOffsetMm":180}"""));
        Assert.Equal(1, equal.Updated);

        var noHole = await RunOkAsync(ByCodes(new[] { "P100" }, """{"hangHoleType":0,"heightMm":20}"""));
        Assert.Equal(1, noHole.Updated);
        Assert.Equal(20, LoadSpec("P100")!.HeightMm);

        // 创建：吊孔类型缺省 2（有吊孔），偏移 100 > 高 60 → 跳过；同时把类型设为 0 就可以。
        const string dims = "\"widthMm\":50,\"heightMm\":60,\"depthMm\":20";
        var create = await RunOkAsync(ByCodes(new[] { "P101" }, $$"""{ {{dims}},"hangHoleOffsetMm":100 }""", createMissing: true));
        Assert.Equal(PlanogramBatchSkipReasons.OffsetExceedsHeight, Assert.Single(create.SkippedItems).Reason);
        Assert.Null(LoadSpec("P101"));
        var createNoHole = await RunOkAsync(ByCodes(new[] { "P101" }, $$"""{ {{dims}},"hangHoleOffsetMm":100,"hangHoleType":0 }""", createMissing: true));
        Assert.Equal(1, createNoHole.Created);
    }

    [Fact]
    public async Task 批量_补丁应用后与现有值完全相同计为不变_不写库_不动审计列()
    {
        var hook = await CreateHookAsync("HK-1");
        SeedSpec(PlanogramBatchCatalog.Spec("P100", notes: "同", overridesJson: $$"""{"{{hook.Guid}}":6}""", defaultHook: hook.Guid));
        SeedSpec(PlanogramBatchCatalog.Spec("P200"));
        var codes = new[] { "P100", "P200" };

        // P100 的这些字段、挂数覆盖都已是目标值 → 不变；P200 的备注不同 → 更新。
        var preview = await RunOkAsync(ByCodes(
            codes,
            $$"""{"widthMm":120,"heightMm":180,"notes":"同","defaultHookGuid":"{{hook.Guid}}","unitWeightG":200}""",
            dryRun: true,
            overrides: Overrides("merge", $$"""{"{{hook.Guid}}":6}""")));
        // P200 没有覆盖，merge 之后多一项 → 更新；P100 完全相同 → 不变。
        Assert.Equal((0, 1, 1), (preview.WillCreate, preview.WillUpdate, preview.WillStayUnchanged));

        var executed = await RunOkAsync(ByCodes(
            codes,
            $$"""{"widthMm":120,"heightMm":180,"notes":"同","defaultHookGuid":"{{hook.Guid}}","unitWeightG":200}""",
            overrides: Overrides("merge", $$"""{"{{hook.Guid}}":6}""")));
        Assert.Equal((0, 1, 1), (executed.Created, executed.Updated, executed.Unchanged));
        var untouched = LoadSpec("P100")!;
        Assert.Equal(("seed", PlanogramBatchCatalog.SeedTime), (untouched.UpdatedBy, untouched.UpdatedAt));
        Assert.Equal((Actor, Now), (LoadSpec("P200")!.UpdatedBy, LoadSpec("P200")!.UpdatedAt));

        // 历史上手写的、未排序 / 带空格的覆盖 JSON：内容相同就是不变，不会被「规范化」改写。
        SeedSpec(PlanogramBatchCatalog.Spec("P101", overridesJson: $$"""{ "{{hook.Guid}}" : 6 }"""));
        var same = await RunOkAsync(ByCodes(new[] { "P101" }, "{}", overrides: Overrides("replace", $$"""{"{{hook.Guid}}":6}""")));
        Assert.Equal((0, 1), (same.Updated, same.Unchanged));
        Assert.Equal($$"""{ "{{hook.Guid}}" : 6 }""", LoadSpec("P101")!.HookQtyOverridesJson);
    }

    // ================================================================ 批量：改前改后明细

    [Fact]
    public async Task 批量_改前改后明细_更新只含实际改变的字段_新建before为null_不变与跳过不进表_dryRun与执行一致()
    {
        var h1 = await CreateHookAsync("HK-1");
        var h2 = await CreateHookAsync("HK-2");
        SeedSpec(PlanogramBatchCatalog.Spec("P100", width: 120, notes: "旧", overridesJson: $$"""{"{{h1.Guid}}":6}"""));
        // P200 已经是目标值 → 不变；P999 不存在 → 跳过。两者都不进明细。
        SeedSpec(PlanogramBatchCatalog.Spec("P200", width: 70, notes: null, overridesJson: $$"""{"{{h2.Guid}}":4}"""));
        var request = ByCodes(
            new[] { "P200", "P101", "P999", "P100" },
            """{"widthMm":70,"heightMm":180,"depthMm":30,"notes":null}""",
            createMissing: true,
            dryRun: true,
            overrides: Overrides("merge", $$"""{"{{h2.Guid}}":4}"""));

        var preview = await RunOkAsync(request);

        Assert.Equal((1, 1, 1, 1), (preview.WillCreate, preview.WillUpdate, preview.WillStayUnchanged, preview.Skipped));
        Assert.Equal(2, preview.ChangedTotal);
        Assert.Equal(new[] { "P100", "P101" }, preview.ChangedItems.Select(item => item.ProductCode)); // 按货号排序

        // 更新：高、厚没变（180 / 30），不出现；宽、备注、挂数覆盖变了，前后都给出。
        var update = preview.ChangedItems[0];
        Assert.Equal((PlanogramBatchChangeActions.Update, "IT-100", "商品P100"), (update.Action, update.ItemNumber, update.ProductName));
        Assert.Equal(new[] { "widthMm", "notes", "hookQtyOverrides" }, update.Before!.Keys);
        Assert.Equal(new[] { "widthMm", "notes", "hookQtyOverrides" }, update.After.Keys);
        Assert.Equal((120, "旧"), ((int)update.Before["widthMm"]!, (string?)update.Before["notes"]));
        Assert.Equal(70, (int)update.After["widthMm"]!);
        Assert.Null(update.After["notes"]); // 清空 = after 里显式为 null
        Assert.Equal(new Dictionary<string, int> { [h1.Guid] = 6 }, (Dictionary<string, int>)update.Before["hookQtyOverrides"]!);
        Assert.Equal(
            new Dictionary<string, int> { [h1.Guid] = 6, [h2.Guid] = 4 }.OrderBy(pair => pair.Key),
            ((Dictionary<string, int>)update.After["hookQtyOverrides"]!).OrderBy(pair => pair.Key));

        // 新建：before 为 null；after 给出宽高厚、缺省的吊孔偏移 / 类型 / 来源与补丁设置的挂数覆盖，没设置的可空字段不出现。
        var create = preview.ChangedItems[1];
        Assert.Equal(PlanogramBatchChangeActions.Create, create.Action);
        Assert.Null(create.Before);
        Assert.Equal(
            new[] { "widthMm", "heightMm", "depthMm", "hangHoleOffsetMm", "hangHoleType", "source", "hookQtyOverrides" },
            create.After.Keys);
        Assert.Equal(
            (70, 180, 30, 10, 2, 1),
            ((int)create.After["widthMm"]!, (int)create.After["heightMm"]!, (int)create.After["depthMm"]!,
                (int)create.After["hangHoleOffsetMm"]!, (int)create.After["hangHoleType"]!, (int)create.After["source"]!));
        Assert.Equal(new Dictionary<string, int> { [h2.Guid] = 4 }, (Dictionary<string, int>)create.After["hookQtyOverrides"]!);

        // 真正执行：明细与预览完全相同。
        request.DryRun = false;
        var executed = await RunOkAsync(request);
        Assert.Equal((1, 1, 1), (executed.Created, executed.Updated, executed.Unchanged));
        Assert.Equal(2, executed.ChangedTotal);
        Assert.Equal(JsonSerializer.Serialize(preview.ChangedItems), JsonSerializer.Serialize(executed.ChangedItems));
    }

    [Fact]
    public async Task 批量_改前改后明细_最多50条按货号取前50_changedTotal是真实总数()
    {
        SeedBulkProducts("888", 60, withSpec: true, width: 100);

        var result = await RunOkAsync(ByFilter(Filter(supplier: "888"), """{"widthMm":77}""", dryRun: true));

        Assert.Equal((60, 60), (result.WillUpdate, result.ChangedTotal));
        Assert.Equal(Enumerable.Range(1, 50).Select(i => $"B{i:D5}"), result.ChangedItems.Select(item => item.ProductCode));
        Assert.All(result.ChangedItems, item =>
        {
            Assert.Equal(new[] { "widthMm" }, item.Before!.Keys);
            Assert.Equal((100, 77), ((int)item.Before["widthMm"]!, (int)item.After["widthMm"]!));
        });

        // 全部不变 / 全部跳过时明细为空。
        var unchanged = await RunOkAsync(ByFilter(Filter(supplier: "888"), """{"widthMm":100}""", dryRun: true));
        Assert.Equal((0, 0), (unchanged.ChangedTotal, unchanged.ChangedItems.Count));
    }

    [Fact]
    public void JSON契约_改前改后明细_生产序列化选项下before为null也输出_after里的null保留()
    {
        // 与 Program.cs 的 AddJsonOptions 一致：camelCase + 写入时忽略 null 属性。
        var production = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        var created = new ProductSpecBatchChangedItemDto
        {
            ProductCode = "P101", ItemNumber = "IT-101", ProductName = "n", Action = PlanogramBatchChangeActions.Create,
            After = new Dictionary<string, object?> { ["widthMm"] = 70, ["hookQtyOverrides"] = new Dictionary<string, int> { ["h"] = 4 } },
        };
        var updated = new ProductSpecBatchChangedItemDto
        {
            ProductCode = "P100", Action = PlanogramBatchChangeActions.Update,
            Before = new Dictionary<string, object?> { ["notes"] = "旧" },
            After = new Dictionary<string, object?> { ["notes"] = null },
        };

        var createdJson = JsonDocument.Parse(JsonSerializer.Serialize(created, production)).RootElement;
        Assert.Equal(
            new[] { "action", "after", "before", "itemNumber", "productCode", "productName" },
            createdJson.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, createdJson.GetProperty("before").ValueKind);
        Assert.Equal("create", createdJson.GetProperty("action").GetString());
        Assert.Equal(4, createdJson.GetProperty("after").GetProperty("hookQtyOverrides").GetProperty("h").GetInt32());

        var updatedJson = JsonDocument.Parse(JsonSerializer.Serialize(updated, production)).RootElement;
        Assert.Equal(JsonValueKind.Null, updatedJson.GetProperty("after").GetProperty("notes").ValueKind);
        Assert.Equal("旧", updatedJson.GetProperty("before").GetProperty("notes").GetString());
    }

    // ================================================================ 批量：并发冲突

    [Fact]
    public async Task 批量_并发冲突_预览之后别人抢先创建了同一货号_整批回滚_返回BATCH_CONFLICT_重新执行成功()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));
        var racing = new RacingTime(Now);
        var specs = new ProductDisplaySpecService(_db, racing);
        // 计算完成、开事务写入之前，「别人」给 P101 建了参数。
        racing.BeforeNextRead = () =>
        {
            var rival = PlanogramBatchCatalog.Spec("P101", width: 33);
            rival.UpdatedBy = "rival";
            _db.Insertable(rival).ExecuteCommand();
        };
        var request = ByCodes(new[] { "P100", "P101", "P102" }, """{"widthMm":77,"heightMm":90,"depthMm":20,"hangHoleOffsetMm":5}""", createMissing: true);

        var conflict = await specs.BatchUpdateAsync(request, Actor, default);

        Assert.False(conflict.Success);
        Assert.Equal(PlanogramErrorCodes.BatchConflict, conflict.ErrorCode);
        Assert.Equal("有其他人同时修改了这些商品，请重新预览后再执行", conflict.Message);
        // 整批回滚：P100 的更新、P102 的创建都没留下；别人的 P101 原样保留。
        Assert.Equal((120, "seed"), (LoadSpec("P100")!.WidthMm, LoadSpec("P100")!.UpdatedBy));
        Assert.Null(LoadSpec("P102"));
        Assert.Equal((33, "rival"), (LoadSpec("P101")!.WidthMm, LoadSpec("P101")!.UpdatedBy));

        // 重新预览：P101 现在走更新；再执行成功（连接与事务状态都已复原）。
        var preview = await RunOkAsync(ByCodes(new[] { "P100", "P101", "P102" }, """{"widthMm":77,"heightMm":90,"depthMm":20,"hangHoleOffsetMm":5}""", createMissing: true, dryRun: true));
        Assert.Equal((1, 2), (preview.WillCreate, preview.WillUpdate));
        request.DryRun = false;
        var retried = await specs.BatchUpdateAsync(request, Actor, default);
        Assert.True(retried.Success, retried.Message);
        Assert.Equal((1, 2), (retried.Data!.Created, retried.Data.Updated));
        Assert.Equal(77, LoadSpec("P101")!.WidthMm);
    }

    [Fact]
    public async Task 批量_并发冲突_计算之后别人清除了其中一个商品的参数_更新命中行数不足_整批回滚_返回BATCH_CONFLICT()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));
        SeedSpec(PlanogramBatchCatalog.Spec("P200"));
        var racing = new RacingTime(Now);
        var specs = new ProductDisplaySpecService(_db, racing);
        // 计算完成（P100 / P200 走更新、P101 走创建）、开事务写入之前，「别人」清除了 P200 的参数。
        racing.BeforeNextRead = () => _db.Deleteable<ProductDisplaySpec>().Where(spec => spec.ProductCode == "P200").ExecuteCommand();
        var request = ByCodes(new[] { "P100", "P200", "P101" }, """{"widthMm":77,"heightMm":90,"depthMm":20,"hangHoleOffsetMm":5}""", createMissing: true);

        var conflict = await specs.BatchUpdateAsync(request, Actor, default);

        Assert.False(conflict.Success);
        Assert.Equal(PlanogramErrorCodes.BatchConflict, conflict.ErrorCode);
        Assert.Equal("有其他人同时修改了这些商品，请重新预览后再执行", conflict.Message);
        Assert.Null(conflict.Data); // 不返回「已更新」之类的计数
        // 整批无改动：P100 没被更新，P101 没被创建；P200 保持别人删除后的状态。
        Assert.Equal((120, "seed"), (LoadSpec("P100")!.WidthMm, LoadSpec("P100")!.UpdatedBy));
        Assert.Null(LoadSpec("P101"));
        Assert.Null(LoadSpec("P200"));
        Assert.Equal(1, await _db.Queryable<ProductDisplaySpec>().CountAsync());

        // 重新执行：P200 现在没有参数，按「创建」处理，整批成功。
        var retried = await specs.BatchUpdateAsync(request, Actor, default);
        Assert.True(retried.Success, retried.Message);
        Assert.Equal((2, 1), (retried.Data!.Created, retried.Data.Updated));
        Assert.Equal(77, LoadSpec("P200")!.WidthMm);
    }

    [Fact]
    public async Task 批量_并发冲突_跨分批时最后一批才少一行_前面各批的更新也一并回滚()
    {
        SeedBulkProducts("888", 1200, withSpec: true, width: 100);
        var racing = new RacingTime(Now);
        var specs = new ProductDisplaySpecService(_db, racing);
        racing.BeforeNextRead = () => _db.Deleteable<ProductDisplaySpec>().Where(spec => spec.ProductCode == "B01200").ExecuteCommand();

        var conflict = await specs.BatchUpdateAsync(ByFilter(Filter(supplier: "888"), """{"widthMm":77}"""), Actor, default);

        Assert.Equal(PlanogramErrorCodes.BatchConflict, conflict.ErrorCode);
        Assert.Equal(0, await _db.Queryable<ProductDisplaySpec>().Where(spec => spec.WidthMm == 77).CountAsync());
        Assert.Equal(1199, await _db.Queryable<ProductDisplaySpec>().Where(spec => spec.WidthMm == 100 && spec.UpdatedBy == "seed").CountAsync());
    }

    [Fact]
    public async Task 批量_预览之后到点执行之前别人清除了参数_执行时按当前数据重新计算_该商品计NO_SPEC_不报冲突()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));
        SeedSpec(PlanogramBatchCatalog.Spec("P200"));
        var request = ByCodes(new[] { "P100", "P200" }, """{"widthMm":77}""", dryRun: true);
        Assert.Equal(2, (await RunOkAsync(request)).WillUpdate);

        // 预览与点「确认执行」之间被删：执行请求会重新读取、重新计算，结果如实反映（不是冲突）。
        _db.Deleteable<ProductDisplaySpec>().Where(spec => spec.ProductCode == "P200").ExecuteCommand();
        request.DryRun = false;
        var executed = await RunOkAsync(request);

        Assert.Equal((1, 1), (executed.Updated, executed.Skipped));
        Assert.Equal(PlanogramBatchSkipReasons.NoSpec, Assert.Single(executed.SkippedItems).Reason);
        Assert.Equal(new[] { "P100" }, executed.ChangedItems.Select(item => item.ProductCode));
    }

    [Fact]
    public async Task 批量_并发冲突识别只针对主键唯一冲突_其他数据库错误仍然抛出()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));
        _db.Ado.ExecuteCommand(
            "CREATE TRIGGER trg_batch_other_error BEFORE UPDATE ON ProductDisplaySpec WHEN NEW.ProductCode = 'P100' " +
            "BEGIN SELECT RAISE(ABORT, 'boom'); END;");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _specs.BatchUpdateAsync(ByCodes(new[] { "P100" }, """{"widthMm":77}"""), Actor, default));
        Assert.Equal(120, LoadSpec("P100")!.WidthMm);
    }

    // ================================================================ 批量：事务

    [Fact]
    public async Task 批量_事务_更新中途数据库报错_整批回滚_触发器移除后重试成功()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));
        SeedSpec(PlanogramBatchCatalog.Spec("P200"));
        // 对 P200 的更新直接 ABORT：P100 在它之前已经被更新，回滚后必须恢复。
        _db.Ado.ExecuteCommand(
            "CREATE TRIGGER trg_batch_fail_update BEFORE UPDATE ON ProductDisplaySpec WHEN NEW.ProductCode = 'P200' " +
            "BEGIN SELECT RAISE(ABORT, 'boom'); END;");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _specs.BatchUpdateAsync(ByCodes(new[] { "P100", "P200" }, """{"widthMm":77}"""), Actor, default));

        Assert.Equal(120, LoadSpec("P100")!.WidthMm);
        Assert.Equal("seed", LoadSpec("P100")!.UpdatedBy);
        Assert.Equal(120, LoadSpec("P200")!.WidthMm);

        _db.Ado.ExecuteCommand("DROP TRIGGER trg_batch_fail_update");
        var retried = await RunOkAsync(ByCodes(new[] { "P100", "P200" }, """{"widthMm":77}"""));
        Assert.Equal(2, retried.Updated);
        Assert.Equal(77, LoadSpec("P100")!.WidthMm);
        Assert.Equal(77, LoadSpec("P200")!.WidthMm);
    }

    [Fact]
    public async Task 批量_事务_创建中途数据库报错_已做的更新和已插入的行一并回滚()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));
        _db.Ado.ExecuteCommand(
            "CREATE TRIGGER trg_batch_fail_insert BEFORE INSERT ON ProductDisplaySpec WHEN NEW.ProductCode = 'P300' " +
            "BEGIN SELECT RAISE(ABORT, 'boom'); END;");

        // P100 更新、P101 / P102 创建先成功，P300 创建时报错。
        await Assert.ThrowsAnyAsync<Exception>(() => _specs.BatchUpdateAsync(
            ByCodes(new[] { "P100", "P101", "P102", "P300" }, """{"widthMm":77,"heightMm":90,"depthMm":20,"hangHoleOffsetMm":5}""", createMissing: true),
            Actor,
            default));

        Assert.Equal(120, LoadSpec("P100")!.WidthMm);
        Assert.Null(LoadSpec("P101"));
        Assert.Null(LoadSpec("P102"));
        Assert.Null(LoadSpec("P300"));
        Assert.Equal(1, await _db.Queryable<ProductDisplaySpec>().CountAsync());
    }

    [Fact]
    public async Task 批量_事务_跨分批_1200个商品最后一批报错时前面各批全部回滚_成功后全部生效()
    {
        SeedBulkProducts("888", 1200, withSpec: true, width: 100);
        // 最后一个货号（排序后在第三批 500 条里）更新时报错。
        _db.Ado.ExecuteCommand(
            "CREATE TRIGGER trg_batch_fail_last BEFORE UPDATE ON ProductDisplaySpec WHEN NEW.ProductCode = 'B01200' " +
            "BEGIN SELECT RAISE(ABORT, 'boom'); END;");
        var filter = Filter(supplier: "888");

        await Assert.ThrowsAnyAsync<Exception>(() => _specs.BatchUpdateAsync(ByFilter(filter, """{"widthMm":77}"""), Actor, default));

        Assert.Equal(0, await _db.Queryable<ProductDisplaySpec>().Where(spec => spec.WidthMm == 77).CountAsync());
        Assert.Equal(1200, await _db.Queryable<ProductDisplaySpec>().Where(spec => spec.WidthMm == 100 && spec.UpdatedBy == "seed").CountAsync());

        _db.Ado.ExecuteCommand("DROP TRIGGER trg_batch_fail_last");
        var preview = await RunOkAsync(ByFilter(filter, """{"widthMm":77}""", dryRun: true));
        var executed = await RunOkAsync(ByFilter(filter, """{"widthMm":77}"""));
        Assert.Equal((1200, 1200), (preview.WillUpdate, executed.Updated));
        Assert.Equal(1200, await _db.Queryable<ProductDisplaySpec>().Where(spec => spec.WidthMm == 77 && spec.UpdatedBy == Actor).CountAsync());
    }

    [Fact]
    public async Task 批量_事务_目标全部不变或全部跳过时不开事务也不写库()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));
        var unchanged = await RunOkAsync(ByCodes(new[] { "P100", "P999" }, """{"widthMm":120}"""));
        Assert.Equal((0, 0, 1, 1), (unchanged.Created, unchanged.Updated, unchanged.Unchanged, unchanged.Skipped));
        Assert.Equal("seed", LoadSpec("P100")!.UpdatedBy);
    }

    // ================================================================ 已被方案引用的商品

    [Fact]
    public async Task 批量_已被方案引用的商品_方案明细不动_下次打开自然标参数已变_引用数不变()
    {
        var shelf = (await _models.CreateShelfModelAsync(new ShelfModelDto
        {
            Code = "G-S-PEG-90x30x120", Name = "单面洞洞板", BayWidthMm = 900, TotalHeightMm = 1200, BaseDepthMm = 300, BaseTopHeightMm = 150,
            UprightWidthMm = 30, HolePitchXMm = 25, HolePitchYMm = 25, HoleDiameterMm = 6, FirstHoleLeftMm = 25, FirstHoleTopMm = 25,
        }, "seed", default)).Data!;
        var hook = await CreateHookAsync("HK-1");
        Assert.True((await _specs.SaveAsync(
            "P100", new ProductDisplaySpecDto { WidthMm = 120, HeightMm = 180, DepthMm = 30, UnitWeightG = 200 }, "seed", default)).Success);
        var created = (await _plans.CreateAsync(new PlanogramCreateRequest { Name = "引用方案", ShelfModelGuid = shelf.Guid, BayCount = 1 }, "seed", default)).Data!;
        var saved = await _plans.SaveAsync(created.Header.Guid, new PlanogramSaveRequest
        {
            Name = "引用方案", ShelfModelGuid = shelf.Guid, BayCount = 1, GapXMm = 10, GapYMm = 10, RowVersion = created.RowVersion,
            Items = new List<PlanogramItemDto> { new() { Guid = "a", ProductCode = "P100", HookModelGuid = hook.Guid, HoleCol = 5, HoleRow = 3 } },
        }, "seed", default);
        Assert.True(saved.Success, saved.Message);
        Assert.False(saved.Data!.Items.Single().SpecChanged);
        var itemsBefore = JsonSerializer.Serialize(_db.Queryable<PlanogramItem>().OrderBy(item => item.Guid).ToList());
        var headerBefore = JsonSerializer.Serialize(_db.Queryable<PlanogramHeader>().ToList());

        var result = await RunOkAsync(ByCodes(new[] { "P100" }, """{"widthMm":70}"""));

        Assert.Equal(1, result.Updated);
        // 方案侧（含保存时的尺寸快照）一字不动；方案头也没被碰。
        Assert.Equal(itemsBefore, JsonSerializer.Serialize(_db.Queryable<PlanogramItem>().OrderBy(item => item.Guid).ToList()));
        Assert.Equal(headerBefore, JsonSerializer.Serialize(_db.Queryable<PlanogramHeader>().ToList()));
        Assert.Equal(1, (await _specs.GetAsync("P100", default)).Data!.ReferencedPlanCount);
        // 下次打开方案时，该商品被标为「参数已变」。
        Assert.True((await _plans.GetAsync(created.Header.Guid, default)).Data!.Items.Single().SpecChanged);
    }

    // ================================================================ 控制器：路由 / 权限 / 参数绑定

    public static IEnumerable<object[]> ControllerContract()
    {
        // 方法名, HTTP 方法, 路由模板, 权限策略（附录 D.2）
        yield return new object[] { nameof(PlanogramController.BatchUpdateProductSpecs), "POST", "product-specs/batch", Permissions.Planogram.Edit };
    }

    [Theory]
    [MemberData(nameof(ControllerContract))]
    public void 控制器_批量修改路由_HTTP方法_权限策略与附录D一致(string methodName, string verb, string template, string policy)
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
    public void 控制器_批量路由与product_specs其他路由不冲突_同一动词加模板只出现一次()
    {
        var routes = typeof(PlanogramController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: false)
                .SelectMany(http => http.HttpMethods.Select(verb => $"{verb} {http.Template}")))
            .ToList();

        Assert.Empty(routes.GroupBy(route => route).Where(group => group.Count() > 1).Select(group => group.Key));
        // product-specs/{productCode} 只有 GET / PUT / DELETE，批量用的 POST 字面量路径不会被它吞掉。
        Assert.DoesNotContain("POST product-specs/{productCode}", routes);
        Assert.Contains("POST product-specs/batch", routes);
    }

    [Fact]
    public void 控制器_列表的过滤参数从查询串绑定_数组用重复键()
    {
        var parameters = typeof(PlanogramController).GetMethod(nameof(PlanogramController.ListProductSpecs))!
            .GetParameters().ToDictionary(parameter => parameter.Name!);

        foreach (var name in new[] { "warehouseCategoryGuids", "supplierCategoryGuids", "grades" })
        {
            Assert.Equal(typeof(string[]), parameters[name].ParameterType);
            Assert.NotNull(parameters[name].GetCustomAttribute<FromQueryAttribute>());
        }

        Assert.Equal(typeof(string), parameters["localSupplierCode"].ParameterType);
        Assert.Equal(typeof(bool?), parameters["supplierCategoryUnassignedOnly"].ParameterType);
        Assert.NotNull(parameters["localSupplierCode"].GetCustomAttribute<FromQueryAttribute>());
        Assert.NotNull(parameters["supplierCategoryUnassignedOnly"].GetCustomAttribute<FromQueryAttribute>());
    }

    [Fact]
    public async Task 控制器_批量_成功200_失败400保留业务错误码_空请求体按空对象处理_用户名写入审计()
    {
        SeedSpec(PlanogramBatchCatalog.Spec("P100"));
        var controller = new PlanogramController(_models, _specs, _plans)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "dave") }, "TestAuth")),
                },
            },
        };

        var ok = await controller.BatchUpdateProductSpecs(ByCodes(new[] { "P100" }, """{"notes":"控制器"}"""), default);
        var body = Assert.IsType<ApiResponse<ProductSpecBatchResultDto>>(Assert.IsType<OkObjectResult>(ok).Value);
        Assert.Equal(1, body.Data!.Updated);
        Assert.Equal("dave", LoadSpec("P100")!.UpdatedBy);

        var empty = await controller.BatchUpdateProductSpecs(null, default);
        var bad = Assert.IsType<BadRequestObjectResult>(empty);
        Assert.Equal(PlanogramErrorCodes.BatchEmpty, Assert.IsType<ApiResponse<ProductSpecBatchResultDto>>(bad.Value).ErrorCode);

        // 列表动作把重复键数组交给过滤：空值被忽略。
        var list = await controller.ListProductSpecs(
            null, null, new[] { "W-ROOT", "" }, "200", null, null, new[] { "A" }, 1, 20, default);
        var page = Assert.IsType<ApiResponse<PagedResult<ProductSpecListItemDto>>>(Assert.IsType<OkObjectResult>(list).Value);
        Assert.Equal(new[] { "P100" }, page.Data!.Items!.Select(item => item.ProductCode));
    }

    // ================================================================ JSON 契约

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static string[] JsonKeys(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, WebJson)).RootElement
            .EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    [Fact]
    public void JSON契约_批量结果与跳过明细字段名与附录D完全一致()
    {
        Assert.Equal(
            new[] { "changedItems", "changedTotal", "created", "dryRun", "matched", "skipped", "skippedItems", "unchanged", "updated", "willCreate", "willStayUnchanged", "willUpdate" },
            JsonKeys(new ProductSpecBatchResultDto()));
        Assert.Equal(
            new[] { "itemNumber", "productCode", "productName", "reason" },
            JsonKeys(new ProductSpecBatchSkippedItemDto()));
        Assert.Equal(new[] { "matched", "max" }, JsonKeys(new PlanogramBatchLimitDetails(1, 2)));
        Assert.Equal("PLANOGRAM_BATCH_TOO_MANY", PlanogramErrorCodes.BatchTooMany);
        Assert.Equal("PLANOGRAM_BATCH_EMPTY", PlanogramErrorCodes.BatchEmpty);
        Assert.Equal(
            new[] { "NO_SPEC", "MISSING_DIMENSIONS", "OFFSET_EXCEEDS_HEIGHT", "PRODUCT_NOT_FOUND", "OVERRIDES_TOO_LONG" },
            new[]
            {
                PlanogramBatchSkipReasons.NoSpec, PlanogramBatchSkipReasons.MissingDimensions,
                PlanogramBatchSkipReasons.OffsetExceedsHeight, PlanogramBatchSkipReasons.ProductNotFound,
                PlanogramBatchSkipReasons.OverridesTooLong,
            });
    }

    [Fact]
    public void JSON契约_附录D的请求样例能被完整绑定_patch里的null保持为Null值而不是丢键()
    {
        const string sample = """
        {
          "target": { "productCodes": ["P100"], "filter": { "keyword": "", "status": "all", "warehouseCategoryGuids": [], "localSupplierCode": null, "supplierCategoryGuids": [], "supplierCategoryUnassignedOnly": false, "grades": ["A", "none"] } },
          "patch": { "widthMm": 70, "heightMm": 150, "depthMm": 25, "hangHoleOffsetMm": 10, "hangHoleType": 2, "unitWeightG": null, "defaultHookGuid": "h", "source": 1, "notes": null },
          "hookQtyOverrides": { "mode": "merge", "values": { "h": 12 } },
          "createMissing": false,
          "dryRun": true
        }
        """;

        var request = JsonSerializer.Deserialize<ProductSpecBatchRequest>(sample, WebJson)!;

        Assert.True(request.DryRun);
        Assert.False(request.CreateMissing);
        Assert.Equal(new[] { "P100" }, request.Target!.ProductCodes);
        Assert.Equal(new[] { "A", "none" }, request.Target.Filter!.Grades);
        Assert.False(request.Target.Filter.SupplierCategoryUnassignedOnly);
        Assert.Equal(9, request.Patch!.Count);
        Assert.Equal(JsonValueKind.Null, request.Patch["unitWeightG"].ValueKind);
        Assert.Equal(JsonValueKind.Null, request.Patch["notes"].ValueKind);
        Assert.Equal(70, request.Patch["widthMm"].GetInt32());
        Assert.Equal("merge", request.HookQtyOverrides!.Mode);
        Assert.Equal(12, request.HookQtyOverrides.Values!["h"].GetInt32());
    }
}

/// <summary>
/// 附录 D 的 HTTP 层：真实 MVC 管线（TestServer）验证重复键数组的查询串绑定、批量接口的 JSON 绑定与权限（401 / 403 / 200）。
/// </summary>
public sealed class PlanogramBatchHttpTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
    private const string ApiBase = "/api/react/v1/planogram";
    private const string Manager = "user-manage";
    private const string Editor = "user-edit";
    private const string Viewer = "user-view";
    private const string Nobody = "user-none";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-planogram-batch-http-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly TestServer _server;
    private readonly HttpClient _client;

    public PlanogramBatchHttpTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(PlanogramBatchCatalog.SqliteTables);
        PlanogramBatchCatalog.Seed(_db);
        var models = new PlanogramModelService(_db, new FixedTime(Now));
        var specs = new ProductDisplaySpecService(_db, new FixedTime(Now));
        var plans = new PlanogramPlanService(_db, new FixedTime(Now));

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
                    services.AddSingleton<IPlanogramModelService>(models);
                    services.AddSingleton<IProductDisplaySpecService>(specs);
                    services.AddSingleton<IPlanogramPlanService>(plans);
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

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string? user, string? rawJson = null)
    {
        using var request = new HttpRequestMessage(method, ApiBase + path);
        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user);
        }

        if (rawJson is not null)
        {
            request.Content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string[] Codes(JsonElement body) =>
        body.GetProperty("data").GetProperty("items").EnumerateArray().Select(item => item.GetProperty("productCode").GetString()!).ToArray();

    private static string? ErrorCode(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : null;

    private static string[] DetailFields(JsonElement body) =>
        body.GetProperty("details").EnumerateArray().Select(item => item.GetProperty("field").GetString()!).ToArray();

    [Fact]
    public async Task 列表_数组参数用重复键绑定_空值忽略_各过滤条件并且()
    {
        // 仓库分类 W-CHILD1 + W-CHILD2（含子分类）+ 等级 A / B / none 的并集。
        var union = await SendAsync(
            HttpMethod.Get,
            "/product-specs?warehouseCategoryGuids=W-CHILD1&warehouseCategoryGuids=W-CHILD2&grades=A&grades=B&grades=none&grades=",
            Viewer);
        Assert.Equal(HttpStatusCode.OK, union.Status);
        Assert.Equal(new[] { "P100", "P101", "P202" }, Codes(union.Body));
        Assert.Equal(3, union.Body.GetProperty("data").GetProperty("total").GetInt32());

        // 再叠加「仅未归类」与供应商：P202（供应商 240、没有有效归属）。
        var narrowed = await SendAsync(
            HttpMethod.Get,
            "/product-specs?warehouseCategoryGuids=W-ROOT&localSupplierCode=240&supplierCategoryUnassignedOnly=true&supplierCategoryGuids=",
            Viewer);
        Assert.Equal(new[] { "P202" }, Codes(narrowed.Body));

        // 供应商分类重复键（供应商树 + 仓库树混选）。
        var categories = await SendAsync(HttpMethod.Get, "/product-specs?supplierCategoryGuids=S-ROOT&supplierCategoryGuids=W-CHILD2", Viewer);
        Assert.Equal(new[] { "P101", "P200", "P201" }, Codes(categories.Body));

        // 等级列随行返回（A～D）。
        var rows = await SendAsync(HttpMethod.Get, "/product-specs?grades=A&grades=D", Viewer);
        var grades = rows.Body.GetProperty("data").GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("productCode").GetString()!, item => item.GetProperty("grade").GetString());
        Assert.Equal(new Dictionary<string, string?> { ["P100"] = "A", ["P200"] = "D", ["P300"] = "A" }, grades);
        // 仓库分类路径（只读）随行返回；没设仓库分类的商品为 null。
        var paths = rows.Body.GetProperty("data").GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("productCode").GetString()!, item => item.GetProperty("warehouseCategoryPath").GetString());
        Assert.Equal(new Dictionary<string, string?> { ["P100"] = "百货 > 收纳 > 收纳盒", ["P200"] = "文具", ["P300"] = null }, paths);
    }

    [Fact]
    public async Task 列表_未知等级和超过200个分类GUID_返回400_校验错误带字段名()
    {
        var unknown = await SendAsync(HttpMethod.Get, "/product-specs?grades=Z", Viewer);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
        Assert.Equal(PlanogramErrorCodes.Validation, ErrorCode(unknown.Body));
        Assert.Contains("grades", DetailFields(unknown.Body));

        var query = string.Join("&", Enumerable.Range(1, 201).Select(i => $"warehouseCategoryGuids=NO-SUCH-{i}"));
        var tooMany = await SendAsync(HttpMethod.Get, "/product-specs?" + query, Viewer);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.Status);
        Assert.Contains("warehouseCategoryGuids", DetailFields(tooMany.Body));
    }

    [Fact]
    public async Task 批量_未登录401_无权限403_只读403_编辑权限200_管理权限经别名也可()
    {
        const string body = """{"target":{"productCodes":["P100"]},"patch":{"notes":"x"},"dryRun":true}""";

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/product-specs/batch", null, body)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/product-specs/batch", Nobody, body)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/product-specs/batch", Viewer, body)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/product-specs/batch", Editor, body)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/product-specs/batch", Manager, body)).Status);
        // 只读权限可以看列表，但改不了。
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/product-specs", Viewer)).Status);
        // 被拒绝的请求没有到达服务层：没有任何写入。
        Assert.Equal(0, await _db.Queryable<ProductDisplaySpec>().CountAsync());
    }

    [Fact]
    public async Task 批量_附录D的请求样例走通_dryRun与执行的响应字段齐全_数据库按执行结果落库()
    {
        _db.Insertable(PlanogramBatchCatalog.Spec("P100")).ExecuteCommand();
        const string preview = """
        {"target":{"productCodes":["P100","P101","NOPE"]},
         "patch":{"widthMm":70,"unitWeightG":null,"notes":null},
         "createMissing":false,"dryRun":true}
        """;

        var dry = await SendAsync(HttpMethod.Post, "/product-specs/batch", Editor, preview);

        Assert.Equal(HttpStatusCode.OK, dry.Status);
        var data = dry.Body.GetProperty("data");
        Assert.Equal(
            new[] { "changedItems", "changedTotal", "created", "dryRun", "matched", "skipped", "skippedItems", "unchanged", "updated", "willCreate", "willStayUnchanged", "willUpdate" },
            data.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.True(data.GetProperty("dryRun").GetBoolean());
        Assert.Equal((3, 1, 0, 0, 2), (
            data.GetProperty("matched").GetInt32(), data.GetProperty("willUpdate").GetInt32(), data.GetProperty("willCreate").GetInt32(),
            data.GetProperty("updated").GetInt32(), data.GetProperty("skipped").GetInt32()));
        var skipped = data.GetProperty("skippedItems").EnumerateArray().ToList();
        Assert.Equal(
            new[] { "itemNumber", "productCode", "productName", "reason" },
            skipped[0].EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { ("P101", "NO_SPEC"), ("NOPE", "PRODUCT_NOT_FOUND") },
            skipped.Select(item => (item.GetProperty("productCode").GetString()!, item.GetProperty("reason").GetString()!)));
        Assert.Equal(120, (await _db.Queryable<ProductDisplaySpec>().FirstAsync(spec => spec.ProductCode == "P100")).WidthMm);

        var run = await SendAsync(HttpMethod.Post, "/product-specs/batch", Editor, preview.Replace("\"dryRun\":true", "\"dryRun\":false"));
        Assert.Equal(HttpStatusCode.OK, run.Status);
        Assert.Equal(1, run.Body.GetProperty("data").GetProperty("updated").GetInt32());
        Assert.Equal(0, run.Body.GetProperty("data").GetProperty("willUpdate").GetInt32());
        var saved = await _db.Queryable<ProductDisplaySpec>().FirstAsync(spec => spec.ProductCode == "P100");
        Assert.Equal((70, null, null, "user-edit.name"), (saved.WidthMm, saved.UnitWeightG, saved.Notes, saved.UpdatedBy));
    }

    [Fact]
    public async Task 批量_目标用筛选_重复键数组之外的筛选字段走JSON数组()
    {
        const string body = """
        {"target":{"filter":{"status":"all","warehouseCategoryGuids":["W-CHILD1"],"grades":["A"],"supplierCategoryUnassignedOnly":false}},
         "patch":{"widthMm":50,"heightMm":60,"depthMm":20},"createMissing":true,"dryRun":true}
        """;

        var result = await SendAsync(HttpMethod.Post, "/product-specs/batch", Editor, body);

        Assert.Equal(HttpStatusCode.OK, result.Status);
        var data = result.Body.GetProperty("data");
        Assert.Equal((1, 1), (data.GetProperty("matched").GetInt32(), data.GetProperty("willCreate").GetInt32())); // 仅 P100
    }

    [Fact]
    public async Task 批量_业务错误返回400并保留错误码和details_空对象体是BATCH_EMPTY()
    {
        var unknownKey = await SendAsync(
            HttpMethod.Post, "/product-specs/batch", Editor, """{"target":{"productCodes":["P100"]},"patch":{"foo":1,"widthMm":"x"}}""");
        Assert.Equal(HttpStatusCode.BadRequest, unknownKey.Status);
        Assert.Equal(PlanogramErrorCodes.Validation, ErrorCode(unknownKey.Body));
        Assert.Equal(new[] { "foo", "widthMm" }, DetailFields(unknownKey.Body).OrderBy(name => name, StringComparer.Ordinal));

        var empty = await SendAsync(HttpMethod.Post, "/product-specs/batch", Editor, "{}");
        Assert.Equal(HttpStatusCode.BadRequest, empty.Status);
        Assert.Equal(PlanogramErrorCodes.BatchEmpty, ErrorCode(empty.Body));

        var codes = string.Join(",", Enumerable.Range(1, 2001).Select(i => $"\"X{i}\""));
        var tooMany = await SendAsync(HttpMethod.Post, "/product-specs/batch", Editor, "{\"target\":{\"productCodes\":[" + codes + "]},\"patch\":{\"notes\":\"x\"}}");
        Assert.Equal(PlanogramErrorCodes.BatchTooMany, ErrorCode(tooMany.Body));
        Assert.Equal(2001, tooMany.Body.GetProperty("details").GetProperty("matched").GetInt32());
        Assert.Equal(2000, tooMany.Body.GetProperty("details").GetProperty("max").GetInt32());
    }
}

/// <summary>
/// 附录 D 在真实 SQL Server 上的回归（需要 <c>HB_TEST_SQLSERVER_CONNECTION</c>，未配置时跳过；每个用例建一个 GUID 命名的独立数据库并在结束时删除）：
/// 分类 / 供应商 / 等级过滤的 SQL 翻译（EXISTS、NOT EXISTS、IN、1 = 0）、批量 dryRun 与执行、2000 条的参数上限、事务回滚。
/// </summary>
[Trait("Category", "SQL")]
public sealed class PlanogramBatchSqlServerTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
    private const string Actor = "carol";

    private sealed class FixedTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static ProductSpecBatchRequest ByFilter(ProductSpecFilterDto filter, string patchJson, bool createMissing = false, bool dryRun = false) => new()
    {
        Target = new ProductSpecBatchTargetDto { Filter = filter },
        Patch = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(patchJson),
        CreateMissing = createMissing,
        DryRun = dryRun,
    };

    private static async Task<string[]> CodesAsync(ProductDisplaySpecService specs, ProductSpecFilterDto filter)
    {
        var result = await specs.ListAsync(filter, 1, 100, default);
        Assert.True(result.Success, result.Message);
        return result.Data!.Items!.Select(item => item.ProductCode).ToArray();
    }

    [PlanogramSqlServerFact]
    public async Task SQLServer_分类供应商等级过滤_与SQLite同一组预期()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        PlanogramBatchCatalog.Seed(db);
        var specs = new ProductDisplaySpecService(db, new FixedTime(Now));
        db.Insertable(PlanogramBatchCatalog.Spec("P100")).ExecuteCommand();
        db.Insertable(PlanogramBatchCatalog.Spec("P200")).ExecuteCommand();
        string[] Codes(params string[] codes) => PlanogramBatchCatalog.Codes(codes);

        // 仓库分类（含子分类、软删分类不展开、不存在 → 空）。
        Assert.Equal(Codes("P100", "P202"), await CodesAsync(specs, new ProductSpecFilterDto { WarehouseCategoryGuids = new() { "W-CHILD1" } }));
        Assert.Equal(Codes("P100", "P101", "P102", "P202"), await CodesAsync(specs, new ProductSpecFilterDto { WarehouseCategoryGuids = new() { "W-ROOT" } }));
        Assert.Empty(await CodesAsync(specs, new ProductSpecFilterDto { WarehouseCategoryGuids = new() { "W-DELETED" } }));
        Assert.Empty(await CodesAsync(specs, new ProductSpecFilterDto { WarehouseCategoryGuids = new() { "NO-SUCH" } }));
        // 本地供应商：200 含空值（NULL 与空串）。
        Assert.Equal(Codes("P100", "P101", "P102", "P103", "P104", "P105"), await CodesAsync(specs, new ProductSpecFilterDto { LocalSupplierCode = "200" }));
        Assert.Equal(Codes("P200", "P201", "P202", "P203"), await CodesAsync(specs, new ProductSpecFilterDto { LocalSupplierCode = "240" }));
        // 供应商分类（供应商树 / 仓库树分流）与未归类。
        Assert.Equal(Codes("P200", "P201"), await CodesAsync(specs, new ProductSpecFilterDto { SupplierCategoryGuids = new() { "S-ROOT" } }));
        Assert.Equal(Codes("P100"), await CodesAsync(specs, new ProductSpecFilterDto { SupplierCategoryGuids = new() { "W-CHILD1" } }));
        Assert.Equal(Codes("P101", "P200", "P201"), await CodesAsync(specs, new ProductSpecFilterDto { SupplierCategoryGuids = new() { "S-ROOT", "W-CHILD2" } }));
        Assert.Equal(Codes("P103", "P202", "P203"), await CodesAsync(specs, new ProductSpecFilterDto { SupplierCategoryUnassignedOnly = true }));
        // 等级：字母 / 并集 / none（软删等级行、非 A～D 的等级行）。
        Assert.Equal(Codes("P100", "P300"), await CodesAsync(specs, new ProductSpecFilterDto { Grades = new() { "A" } }));
        Assert.Equal(Codes("P103", "P104", "P201", "P202", "P203"), await CodesAsync(specs, new ProductSpecFilterDto { Grades = new() { "none" } }));
        Assert.Equal(
            Codes("P100", "P300", "P103", "P104", "P201", "P202", "P203"),
            await CodesAsync(specs, new ProductSpecFilterDto { Grades = new() { "A", "none" } }));
        // 组合与分页计数。
        Assert.Equal(Codes("P103", "P202", "P203"), await CodesAsync(specs, new ProductSpecFilterDto { Grades = new() { "none" }, SupplierCategoryUnassignedOnly = true }));
        Assert.Equal(new[] { "P200" }, await CodesAsync(specs, new ProductSpecFilterDto { Status = "set", LocalSupplierCode = "240" }));
        var paged = await specs.ListAsync(new ProductSpecFilterDto { WarehouseCategoryGuids = new() { "W-ROOT" } }, 2, 3, default);
        Assert.Equal(4, paged.Data!.Total);
        Assert.Equal(new[] { "P202" }, paged.Data.Items!.Select(item => item.ProductCode).ToArray());
        // 等级回填（当前页小查询）。
        var rows = (await specs.ListAsync(new ProductSpecFilterDto(), 1, 100, default)).Data!.Items!.ToDictionary(item => item.ProductCode);
        Assert.Equal(("A", "D", null, null), (rows["P100"].Grade, rows["P200"].Grade, rows["P201"].Grade, rows["P105"].Grade));
        // 仓库分类路径（逐级祖先回溯，真实库里也走 IN 分批查询）。
        Assert.Equal(
            ("百货 > 收纳 > 收纳盒", "文具", null),
            (rows["P100"].WarehouseCategoryPath, rows["P200"].WarehouseCategoryPath, rows["P103"].WarehouseCategoryPath));
    }

    [PlanogramSqlServerFact]
    public async Task SQLServer_批量_按筛选dryRun与执行计数一致_创建更新不变跳过_审计列()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        PlanogramBatchCatalog.Seed(db);
        var specs = new ProductDisplaySpecService(db, new FixedTime(Now));
        db.Insertable(PlanogramBatchCatalog.Spec("P100")).ExecuteCommand();
        db.Insertable(PlanogramBatchCatalog.Spec("P101", width: 70)).ExecuteCommand(); // 已是目标宽度 → 不变
        var filter = new ProductSpecFilterDto { LocalSupplierCode = "200" };
        const string patch = """{"widthMm":70,"heightMm":180,"depthMm":30,"hangHoleOffsetMm":10,"hangHoleType":1,"unitWeightG":200,"notes":null}""";

        var preview = await specs.BatchUpdateAsync(ByFilter(filter, patch, createMissing: true, dryRun: true), Actor, default);
        Assert.True(preview.Success, preview.Message);
        Assert.Equal((6, 4, 1, 1, 0), (preview.Data!.Matched, preview.Data.WillCreate, preview.Data.WillUpdate, preview.Data.WillStayUnchanged, preview.Data.Skipped));
        Assert.Equal(2, await db.Queryable<ProductDisplaySpec>().CountAsync()); // dryRun 不写库

        var executed = await specs.BatchUpdateAsync(ByFilter(filter, patch, createMissing: true), Actor, default);
        Assert.True(executed.Success, executed.Message);
        Assert.Equal((6, 4, 1, 1, 0), (executed.Data!.Matched, executed.Data.Created, executed.Data.Updated, executed.Data.Unchanged, executed.Data.Skipped));
        Assert.Equal((0, 0, 0), (executed.Data.WillCreate, executed.Data.WillUpdate, executed.Data.WillStayUnchanged));
        Assert.Equal(6, await db.Queryable<ProductDisplaySpec>().CountAsync());
        var created = await db.Queryable<ProductDisplaySpec>().FirstAsync(spec => spec.ProductCode == "P103");
        Assert.Equal((70, Actor, Now), (created.WidthMm, created.UpdatedBy, created.UpdatedAt));
        var updated = await db.Queryable<ProductDisplaySpec>().FirstAsync(spec => spec.ProductCode == "P100");
        Assert.Equal((70, Actor, "seed"), (updated.WidthMm, updated.UpdatedBy, updated.CreatedBy));
        var unchanged = await db.Queryable<ProductDisplaySpec>().FirstAsync(spec => spec.ProductCode == "P101");
        Assert.Equal("seed", unchanged.UpdatedBy);

        // 目标超过 2000 个（匹配数带在 details 里）。
        var many = Enumerable.Range(1, 2001).Select(i => new Product { ProductCode = $"B{i:D5}", ItemNumber = $"BI-{i}", ProductName = $"批量{i}", LocalSupplierCode = "777" }).ToList();
        db.Insertable(many).PageSize(100).ExecuteCommand();
        var tooMany = await specs.BatchUpdateAsync(ByFilter(new ProductSpecFilterDto { LocalSupplierCode = "777" }, patch), Actor, default);
        Assert.Equal(PlanogramErrorCodes.BatchTooMany, tooMany.ErrorCode);
        Assert.Equal(new PlanogramBatchLimitDetails(2001, 2000), tooMany.Details);
    }

    [PlanogramSqlServerFact]
    public async Task SQLServer_批量_2000条创建再2000条更新_不触到2100参数上限_重复执行幂等()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        PlanogramBatchCatalog.Seed(db);
        var specs = new ProductDisplaySpecService(db, new FixedTime(Now));
        var products = Enumerable.Range(1, 2000).Select(i => new Product { ProductCode = $"B{i:D5}", ItemNumber = $"BI-{i}", ProductName = $"批量{i}", LocalSupplierCode = "888" }).ToList();
        db.Insertable(products).PageSize(100).ExecuteCommand();
        var filter = new ProductSpecFilterDto { LocalSupplierCode = "888" };

        var create = await specs.BatchUpdateAsync(ByFilter(filter, """{"widthMm":60,"heightMm":90,"depthMm":20}""", createMissing: true), Actor, default);
        Assert.True(create.Success, create.Message);
        Assert.Equal((2000, 0, 0, 0), (create.Data!.Created, create.Data.Updated, create.Data.Unchanged, create.Data.Skipped));
        Assert.Equal(2000, await db.Queryable<ProductDisplaySpec>().Where(spec => spec.WidthMm == 60 && spec.HangHoleType == 2 && spec.UpdatedBy == Actor).CountAsync());

        var update = await specs.BatchUpdateAsync(ByFilter(filter, """{"widthMm":61,"notes":"批量备注","unitWeightG":150}"""), Actor, default);
        Assert.True(update.Success, update.Message);
        Assert.Equal((0, 2000, 0), (update.Data!.Created, update.Data.Updated, update.Data.Unchanged));
        Assert.Equal(2000, await db.Queryable<ProductDisplaySpec>().Where(spec => spec.WidthMm == 61 && spec.Notes == "批量备注" && spec.UnitWeightG == 150).CountAsync());

        // 同样的补丁再执行一次：全部不变。
        var again = await specs.BatchUpdateAsync(ByFilter(filter, """{"widthMm":61,"notes":"批量备注","unitWeightG":150}"""), Actor, default);
        Assert.Equal((0, 0, 2000), (again.Data!.Created, again.Data.Updated, again.Data.Unchanged));
    }

    [PlanogramSqlServerFact]
    public async Task SQLServer_批量_事务回滚_约束在最后一批报错时前面各批更新和插入全部撤销_恢复后可重试()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        PlanogramBatchCatalog.Seed(db);
        var specs = new ProductDisplaySpecService(db, new FixedTime(Now));
        var products = Enumerable.Range(1, 1200).Select(i => new Product { ProductCode = $"B{i:D5}", ItemNumber = $"BI-{i}", ProductName = $"批量{i}", LocalSupplierCode = "888" }).ToList();
        db.Insertable(products).PageSize(100).ExecuteCommand();
        // 前 600 个已有参数（走更新），后 600 个没有（走创建）。
        db.Insertable(products.Take(600).Select(product => PlanogramBatchCatalog.Spec(product.ProductCode!, width: 100)).ToList()).PageSize(100).ExecuteCommand();
        var filter = new ProductSpecFilterDto { LocalSupplierCode = "888" };
        const string patch = """{"widthMm":77,"heightMm":90,"depthMm":20,"hangHoleOffsetMm":5}""";

        // 约束：货号 B01200 的宽度不能是 77（只在创建这一步、也就是整批的最后才会违反）。
        await database.ExecuteAsync("ALTER TABLE dbo.ProductDisplaySpec ADD CONSTRAINT CK_batch_fail CHECK (NOT (ProductCode = N'B01200' AND WidthMm = 77))");

        await Assert.ThrowsAnyAsync<Exception>(() => specs.BatchUpdateAsync(ByFilter(filter, patch, createMissing: true), Actor, default));

        // 前面已执行过的 600 条更新、前几批创建都被撤销。
        Assert.Equal(600, await db.Queryable<ProductDisplaySpec>().CountAsync());
        Assert.Equal(600, await db.Queryable<ProductDisplaySpec>().Where(spec => spec.WidthMm == 100 && spec.UpdatedBy == "seed").CountAsync());
        Assert.Equal(0, await db.Queryable<ProductDisplaySpec>().Where(spec => spec.WidthMm == 77).CountAsync());

        // 回滚后连接没有被「半个事务」占住：撤掉约束再执行，整批成功。
        await database.ExecuteAsync("ALTER TABLE dbo.ProductDisplaySpec DROP CONSTRAINT CK_batch_fail");
        var retried = await specs.BatchUpdateAsync(ByFilter(filter, patch, createMissing: true), Actor, default);
        Assert.True(retried.Success, retried.Message);
        Assert.Equal((600, 600, 0), (retried.Data!.Created, retried.Data.Updated, retried.Data.Skipped));
        Assert.Equal(1200, await db.Queryable<ProductDisplaySpec>().Where(spec => spec.WidthMm == 77).CountAsync());
    }

    [PlanogramSqlServerFact]
    public async Task SQLServer_批量_并发冲突_主键冲突2627转成BATCH_CONFLICT_整批回滚_连接可继续使用()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        PlanogramBatchCatalog.Seed(db);
        db.Insertable(PlanogramBatchCatalog.Spec("P100")).ExecuteCommand();
        var racing = new PlanogramBatchTests.RacingTime(Now);
        var specs = new ProductDisplaySpecService(db, racing);
        racing.BeforeNextRead = () =>
        {
            var rival = PlanogramBatchCatalog.Spec("P101", width: 33);
            rival.UpdatedBy = "rival";
            db.Insertable(rival).ExecuteCommand();
        };
        var request = new ProductSpecBatchRequest
        {
            Target = new ProductSpecBatchTargetDto { ProductCodes = new List<string> { "P100", "P101", "P102" } },
            Patch = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"widthMm":77,"heightMm":90,"depthMm":20,"hangHoleOffsetMm":5}"""),
            CreateMissing = true,
        };

        var conflict = await specs.BatchUpdateAsync(request, Actor, default);

        Assert.Equal(PlanogramErrorCodes.BatchConflict, conflict.ErrorCode);
        var rows = (await db.Queryable<ProductDisplaySpec>().ToListAsync()).ToDictionary(spec => spec.ProductCode);
        Assert.Equal(new[] { "P100", "P101" }, rows.Keys.OrderBy(code => code, StringComparer.Ordinal));
        Assert.Equal((120, "seed"), (rows["P100"].WidthMm, rows["P100"].UpdatedBy));
        Assert.Equal((33, "rival"), (rows["P101"].WidthMm, rows["P101"].UpdatedBy));

        // 回滚后同一个客户端再执行：成功，且改前改后明细与结果一致。
        var retried = await specs.BatchUpdateAsync(request, Actor, default);
        Assert.True(retried.Success, retried.Message);
        Assert.Equal((1, 2, 3), (retried.Data!.Created, retried.Data.Updated, retried.Data.ChangedTotal));
        Assert.Equal(new[] { "P100", "P101", "P102" }, retried.Data.ChangedItems.Select(item => item.ProductCode));
        Assert.Equal(PlanogramBatchChangeActions.Create, retried.Data.ChangedItems[2].Action);
        Assert.Equal(77, (await db.Queryable<ProductDisplaySpec>().FirstAsync(spec => spec.ProductCode == "P101")).WidthMm);
    }

    [PlanogramSqlServerFact]
    public async Task SQLServer_批量_并发冲突_计算之后别人清除了参数_最后一批更新少一行_整批回滚报BATCH_CONFLICT()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        using var db = database.CreateClient();
        PlanogramBatchCatalog.Seed(db);
        var products = Enumerable.Range(1, 1200).Select(i => new Product { ProductCode = $"B{i:D5}", ItemNumber = $"BI-{i}", ProductName = $"批量{i}", LocalSupplierCode = "888" }).ToList();
        db.Insertable(products).PageSize(100).ExecuteCommand();
        db.Insertable(products.Select(product => PlanogramBatchCatalog.Spec(product.ProductCode!, width: 100)).ToList()).PageSize(100).ExecuteCommand();
        var racing = new PlanogramBatchTests.RacingTime(Now);
        var specs = new ProductDisplaySpecService(db, racing);
        // 第三批（第 1001～1200 个）里的最后一个在写入前被别人删掉。
        racing.BeforeNextRead = () => db.Deleteable<ProductDisplaySpec>().Where(spec => spec.ProductCode == "B01200").ExecuteCommand();
        var request = new ProductSpecBatchRequest
        {
            Target = new ProductSpecBatchTargetDto { Filter = new ProductSpecFilterDto { LocalSupplierCode = "888" } },
            Patch = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"widthMm":77}"""),
        };

        var conflict = await specs.BatchUpdateAsync(request, Actor, default);

        Assert.Equal(PlanogramErrorCodes.BatchConflict, conflict.ErrorCode);
        Assert.Equal(0, await db.Queryable<ProductDisplaySpec>().Where(spec => spec.WidthMm == 77).CountAsync());
        Assert.Equal(1199, await db.Queryable<ProductDisplaySpec>().Where(spec => spec.WidthMm == 100 && spec.UpdatedBy == "seed").CountAsync());

        // 再执行：B01200 已没有参数，计 NO_SPEC 跳过，其余 1199 个更新成功。
        var retried = await specs.BatchUpdateAsync(request, Actor, default);
        Assert.True(retried.Success, retried.Message);
        Assert.Equal((1199, 1), (retried.Data!.Updated, retried.Data.Skipped));
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
            var name = $"hb_planogram_batch_{Guid.NewGuid():N}";
            await ExecuteOnAsync(server, $"CREATE DATABASE [{name}] COLLATE Chinese_PRC_90_CI_AS;");
            // 与生产 SqlSugarContext 一致：开 MARS。
            var connectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = name, MultipleActiveResultSets = true }.ConnectionString;
            var database = new IsolatedDatabase(server, name, connectionString);
            using (var db = database.CreateClient())
            {
                db.CodeFirst.InitTables(typeof(Product), typeof(ProductCategory), typeof(ProductGrade), typeof(WarehouseCategory));
            }

            // 真实迁移脚本：陈列图 6 张表（含只读门禁）与本地供应商分类 3 张表。
            await database.ExecuteAsync(Data.SchemaMigrations.PlanogramSchema.ApplySql);
            await database.ExecuteAsync(Data.SchemaMigrations.PlanogramSchema.VerifySql);
            await database.ExecuteAsync(Data.SchemaMigrations.LocalSupplierCategorySchema.ApplySql);
            await database.ExecuteAsync(Data.SchemaMigrations.LocalSupplierCategorySchema.VerifySql);
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
